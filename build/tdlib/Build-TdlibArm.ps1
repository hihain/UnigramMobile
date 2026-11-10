[CmdletBinding()]
param(
    [string]$WorkRoot = (Join-Path $env:LOCALAPPDATA "UnigramTdlibExperiment"),
    [string]$VisualStudioPath = "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools",
    [ValidateSet("Dependencies", "Generate", "Configure", "Build", "All")]
    [string]$Stage = "All",
    # Toolset used for the ARM UWP C++/CX build of TDLib.
    #
    # v143 (MSVC 14.44) is the default. TDLib 1.8.66 is 2026-era C++ and v141
    # (MSVC 14.16, 2017) miscompiled it: the resulting binary corrupted the
    # process heap during concurrent TdDb SQLite initialization, surfacing as an
    # LFH double free when .NET Native freed an HSTRING. v143 also matches the
    # generation of vccorlib140_app.dll shipped by the Microsoft.VCLibs.140.00
    # 14.0.33519.0 framework package that the app actually runs against, so the
    # inline Platform::String code in the headers matches the runtime DLL.
    [ValidateSet("v141", "v143")]
    [string]$Toolset = "v143"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

# Visual Studio and the Copilot Git shim can export an incomplete
# GIT_CONFIG_* override set. vcpkg invokes Git internally, so remove those
# process-local overrides before using either Git or vcpkg.
Get-ChildItem Env: |
    Where-Object { $_.Name -eq "GIT_CONFIG_PARAMETERS" -or $_.Name -like "GIT_CONFIG_*" } |
    ForEach-Object { Remove-Item "Env:$($_.Name)" -ErrorAction SilentlyContinue }

$tdlibCommit = "022d60202e446ad1287b9fb68e687c8a0760788b"
$tdlibVersion = "1.8.66"
$vcpkgCommit = "45f9f39362a4c52e2b1fbe57b7e649db7f3d96d4"
$manifestRoot = $PSScriptRoot
$tripletRoot = Join-Path $PSScriptRoot "triplets"
$overlayRoot = Join-Path $PSScriptRoot "overlays"
$patchRoot = Join-Path $PSScriptRoot "patches"
$tdlibRoot = Join-Path $WorkRoot "tdlib"
$vcpkgRoot = Join-Path $WorkRoot "vcpkg"
$installedRoot = Join-Path $WorkRoot "vcpkg_installed"
$hostInstalledRoot = Join-Path $WorkRoot "vcpkg_host_installed"
$nativeBuild = Join-Path $WorkRoot "build-native"
$uwpBuild = Join-Path $WorkRoot "build-uwp-arm"
$toolsetVcVarsVersion = @{ "v141" = "14.16"; "v143" = "14.44" }[$Toolset]

function Invoke-Checked {
    param(
        [Parameter(Mandatory)]
        [string]$FilePath,
        [string[]]$Arguments
    )

    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        & $FilePath @Arguments
        $exitCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    if ($exitCode -ne 0) {
        throw "$FilePath exited with code $exitCode."
    }
}

function Initialize-PinnedCheckout {
    param(
        [Parameter(Mandatory)]
        [string]$Path,
        [Parameter(Mandatory)]
        [string]$Remote,
        [Parameter(Mandatory)]
        [string]$Commit
    )

    if (-not (Test-Path (Join-Path $Path ".git"))) {
        New-Item -ItemType Directory -Force -Path $Path | Out-Null
        Invoke-Checked -FilePath git -Arguments @("-C", $Path, "init")
        Invoke-Checked -FilePath git -Arguments @("-C", $Path, "remote", "add", "origin", $Remote)
    }

    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $headOutput = & git -C $Path rev-parse HEAD 2>&1
        $headExitCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    $head = ($headOutput | Out-String).Trim()
    if ($headExitCode -ne 0 -or $head -ne $Commit) {
        Invoke-Checked -FilePath git -Arguments @("-C", $Path, "fetch", "--depth", "1", "origin", $Commit)
        Invoke-Checked -FilePath git -Arguments @("-C", $Path, "checkout", "--detach", "FETCH_HEAD")
    }

    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $head = (& git -C $Path rev-parse HEAD 2>&1 | Out-String).Trim()
        $headExitCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    if ($headExitCode -ne 0) {
        throw "Pinned checkout could not be resolved at $Path."
    }
    if ($head -ne $Commit) {
        throw "Pinned checkout mismatch at $Path. Expected $Commit, found $head."
    }
}

function Get-CMake {
    $cmake = (& (Join-Path $vcpkgRoot "vcpkg.exe") fetch cmake).Trim().Split([Environment]::NewLine)[-1]
    if (-not (Test-Path $cmake)) {
        throw "vcpkg did not return a usable CMake path."
    }
    return $cmake
}

function Get-Sha256 {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.IO.File]::ReadAllBytes($Path)
        return ([System.BitConverter]::ToString($sha256.ComputeHash($bytes))).Replace("-", "")
    } finally {
        $sha256.Dispose()
    }
}

function Get-TrackedSourceState {
    $paths = @(& git -C $tdlibRoot diff --name-only $tdlibCommit)
    if ($LASTEXITCODE -ne 0) {
        throw "Could not inspect the patched TDLib source state."
    }

    $state = @{}
    foreach ($path in $paths) {
        if (-not [string]::IsNullOrWhiteSpace($path)) {
            $sourcePath = Join-Path $tdlibRoot $path
            if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
                throw "Patched TDLib source file is missing: $path"
            }
            $state[$path] = Get-Sha256 $sourcePath
        }
    }
    return $state
}

function Apply-PinnedPatches {
    $stampPath = Join-Path $WorkRoot "tdlib-patches-applied.txt"
    $applied = @{}
    $stampedSource = @{}
    if (Test-Path -LiteralPath $stampPath -PathType Leaf) {
        foreach ($line in Get-Content -LiteralPath $stampPath) {
            $parts = $line -split "\|", 2
            if ($parts.Count -eq 2) {
                if ($parts[0].StartsWith("__TREE__")) {
                    $stampedSource[$parts[0].Substring(8)] = $parts[1]
                } else {
                    $applied[$parts[0]] = $parts[1]
                }
            }
        }
    }

    $patches = @(Get-ChildItem $patchRoot -Filter "*.patch" | Sort-Object Name)
    $patchNames = @($patches | ForEach-Object { $_.Name })
    $requiresReset = $false
    if (@($applied.Keys | Where-Object { $_ -notin $patchNames }).Count -gt 0) {
        $requiresReset = $true
    }
    if ($stampedSource.Count -eq 0) {
        $requiresReset = $true
    } else {
        $currentSource = Get-TrackedSourceState
        if ($currentSource.Count -ne $stampedSource.Count) {
            $requiresReset = $true
        } else {
            foreach ($path in $stampedSource.Keys) {
                if (-not $currentSource.ContainsKey($path) -or $currentSource[$path] -ne $stampedSource[$path]) {
                    $requiresReset = $true
                    break
                }
            }
        }
    }
    foreach ($patch in $patches) {
        $hash = Get-Sha256 $patch.FullName
        if (-not $applied.ContainsKey($patch.Name) -or $applied[$patch.Name] -ne $hash) {
            $requiresReset = $true
            break
        }
    }

    if ($requiresReset) {
        Invoke-Checked -FilePath git -Arguments @("-C", $tdlibRoot, "reset", "--hard", $tdlibCommit)
        Invoke-Checked -FilePath git -Arguments @("-C", $tdlibRoot, "clean", "-fdx")
        Remove-Item -LiteralPath $stampPath -Force -ErrorAction SilentlyContinue
        $applied = @{}
    }

    foreach ($patch in $patches) {
        $hash = Get-Sha256 $patch.FullName
        if ($applied.ContainsKey($patch.Name) -and $applied[$patch.Name] -eq $hash) {
            continue
        }

        & git -C $tdlibRoot apply --check $patch.FullName 2>$null
        if ($LASTEXITCODE -eq 0) {
            Invoke-Checked -FilePath git -Arguments @("-C", $tdlibRoot, "apply", $patch.FullName)
        } else {
            & git -C $tdlibRoot apply --reverse --check $patch.FullName 2>$null
            if ($LASTEXITCODE -ne 0) {
                throw "Pinned patch cannot be applied cleanly: $($patch.Name). Delete the external work root and rebuild from the pinned checkout."
            }
        }
        $applied[$patch.Name] = $hash
    }

    $sourceState = Get-TrackedSourceState
    $stamp = @($patches | ForEach-Object { "$($_.Name)|$($applied[$_.Name])" })
    $stamp += @($sourceState.Keys | ForEach-Object { "__TREE__$($_)|$($sourceState[$_])" })
    Set-Content -LiteralPath $stampPath -Value $stamp -Encoding ASCII
}

function Import-VcVars {
    param(
        [Parameter(Mandatory)]
        [ValidateSet("x64", "x64_arm")]
        [string]$Architecture,
        # Compiler version to activate. Dependency and host-tool stages stay
        # pinned to 14.16 for reproducibility; the ARM UWP stages follow the
        # selected $Toolset.
        [string]$VcVarsVersion = "14.16"
    )

    $vcvars = Join-Path $VisualStudioPath "VC\Auxiliary\Build\vcvarsall.bat"
    $command = "call `"$vcvars`" $Architecture 10.0.18362.0 -vcvars_ver=$VcVarsVersion >nul && set"
    $environment = & $env:ComSpec /d /c $command
    if ($LASTEXITCODE -ne 0) {
        throw "vcvarsall failed for $Architecture with code $LASTEXITCODE."
    }

    foreach ($line in $environment) {
        $separator = $line.IndexOf("=")
        if ($separator -gt 0) {
            $name = $line.Substring(0, $separator)
            $value = $line.Substring($separator + 1)
            Set-Item -Path "env:$name" -Value $value
        }
    }
}

New-Item -ItemType Directory -Force -Path $WorkRoot | Out-Null
if (-not (Test-Path (Join-Path $VisualStudioPath "VC\Auxiliary\Build\vcvarsall.bat"))) {
    throw "Visual Studio C++ tools were not found at $VisualStudioPath."
}
$visualStudioInstaller = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"
if (Test-Path (Join-Path $visualStudioInstaller "vswhere.exe")) {
    $env:PATH = "$visualStudioInstaller;$env:PATH"
}

Initialize-PinnedCheckout $tdlibRoot "https://github.com/tdlib/td.git" $tdlibCommit
Initialize-PinnedCheckout $vcpkgRoot "https://github.com/microsoft/vcpkg.git" $vcpkgCommit
Apply-PinnedPatches

# A standalone Build stage cannot safely use generated sources after a
# checkout reset. Run the complete pipeline so generation and configuration
# always correspond to the pinned and patched source tree.
if ($Stage -eq "Build") {
    $Stage = "All"
}

$vcpkg = Join-Path $vcpkgRoot "vcpkg.exe"
if (-not (Test-Path $vcpkg)) {
    Invoke-Checked -FilePath (Join-Path $vcpkgRoot "bootstrap-vcpkg.bat") -Arguments @("-disableMetrics")
}

if ($Stage -in @("Dependencies", "All")) {
    Import-VcVars "x64"
    Invoke-Checked -FilePath $vcpkg -Arguments @(
        "install",
        "gperf:x64-windows",
        "--overlay-triplets", $tripletRoot,
        "--x-install-root", $hostInstalledRoot
    )

    Import-VcVars "x64_arm"
    Invoke-Checked -FilePath $vcpkg -Arguments @(
        "install",
        "--triplet", "arm-uwp-dynamic-v141",
        "--host-triplet", "x64-windows",
        "--overlay-triplets", $tripletRoot,
        "--overlay-ports", $overlayRoot,
        "--x-manifest-root", $manifestRoot,
        "--x-install-root", $installedRoot
    )
}

$cmake = Get-CMake

if ($Stage -in @("Generate", "All")) {
    Import-VcVars "x64"
    $env:PATH = "$(Join-Path $hostInstalledRoot 'x64-windows\tools\gperf');$env:PATH"
    New-Item -ItemType Directory -Force -Path $nativeBuild | Out-Null
    Invoke-Checked -FilePath $cmake -Arguments @(
        "-S", $tdlibRoot,
        "-B", $nativeBuild,
        "-G", "Visual Studio 17 2022",
        "-DCMAKE_GENERATOR_INSTANCE=$VisualStudioPath",
        "-A", "x64",
        "-T", "v141",
        "-DTD_GENERATE_SOURCE_FILES=ON",
        "-DTD_ENABLE_DOTNET=CX"
    )
    Invoke-Checked -FilePath $cmake -Arguments @(
        "--build", $nativeBuild,
        "--config", "Release",
        "--target", "td_generate_dotnet_api",
        "--clean-first",
        "--", "/m:1"
    )
    Invoke-Checked -FilePath $cmake -Arguments @(
        "--build", $nativeBuild,
        "--config", "Release",
        "--target", "prepare_cross_compiling",
        "--", "/m:1"
    )
}

if ($Stage -in @("Configure", "All")) {
    Import-VcVars "x64_arm" -VcVarsVersion $toolsetVcVarsVersion
    New-Item -ItemType Directory -Force -Path $uwpBuild | Out-Null

    # CMake refuses to reuse a cache that was generated with a different
    # toolset. Drop the cache (but keep the directory) so switching between
    # v141 and v143 reconfigures cleanly instead of failing.
    $uwpCache = Join-Path $uwpBuild "CMakeCache.txt"
    if (Test-Path $uwpCache) {
        $toolsetMatch = Select-String -LiteralPath $uwpCache -Pattern '^CMAKE_GENERATOR_TOOLSET:INTERNAL=(.*)$' |
            Select-Object -First 1
        # A truncated or corrupt cache has no toolset line. Treat that as a mismatch so
        # this recovery path clears it instead of failing on a null property access.
        $cachedToolset = ""
        if ($null -ne $toolsetMatch) {
            $cachedToolset = $toolsetMatch.Matches.Groups[1].Value
        }

        if ($cachedToolset -ne $Toolset) {
            Write-Output "Toolset changed ($cachedToolset -> $Toolset); clearing CMake cache."
            Remove-Item -LiteralPath $uwpCache -Force
            Remove-Item -LiteralPath (Join-Path $uwpBuild "CMakeFiles") -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    Invoke-Checked -FilePath $cmake -Arguments @(
        "-S", $tdlibRoot,
        "-B", $uwpBuild,
        "-G", "Visual Studio 17 2022",
        "-DCMAKE_GENERATOR_INSTANCE=$VisualStudioPath",
        "-A", "ARM",
        "-T", $Toolset,
        "-DCMAKE_SYSTEM_NAME=WindowsStore",
        "-DCMAKE_SYSTEM_VERSION=10.0.18362.0",
        "-DCMAKE_TOOLCHAIN_FILE=$(Join-Path $vcpkgRoot 'scripts\buildsystems\vcpkg.cmake')",
        "-DVCPKG_TARGET_TRIPLET=arm-uwp-dynamic-v141",
        "-DVCPKG_INSTALLED_DIR=$installedRoot",
        "-DVCPKG_OVERLAY_TRIPLETS=$tripletRoot",
        "-DVCPKG_OVERLAY_PORTS=$overlayRoot",
        "-DTD_ENABLE_DOTNET=CX",
        "-DTD_ENABLE_LTO=OFF",
        "-DTD_ENABLE_MULTI_PROCESSOR_COMPILATION=OFF"
    )
}

if ($Stage -in @("Build", "All")) {
    Import-VcVars "x64_arm" -VcVarsVersion $toolsetVcVarsVersion
    Invoke-Checked -FilePath $cmake -Arguments @(
        "--build", $uwpBuild,
        "--config", "RelWithDebInfo",
        "--target", "tddotnet",
        "--", "/m:1"
    )

    $outputRoot = Join-Path $uwpBuild "RelWithDebInfo"
    $manifest = @(
        "TDLIB_VERSION=$tdlibVersion",
        "TDLIB_COMMIT=$tdlibCommit",
        "VCPKG_COMMIT=$vcpkgCommit",
        "TOOLSET=$Toolset",
        "OPENSSL_VERSION=3.5.7",
        "ZLIB_VERSION=1.3.2",
        "TELEGRAM_TD_DLL_SHA256=$(Get-Sha256 (Join-Path $outputRoot 'Telegram.Td.dll'))",
        "TELEGRAM_TD_WINMD_SHA256=$(Get-Sha256 (Join-Path $outputRoot 'Telegram.Td.winmd'))"
    )
    Set-Content -LiteralPath (Join-Path $outputRoot "TdlibBuildManifest.txt") -Value $manifest -Encoding ASCII
}

Write-Output "TDLib version: $tdlibVersion"
Write-Output "TDLib commit: $tdlibCommit"
Write-Output "vcpkg commit: $vcpkgCommit"
Write-Output "ARM UWP output: $(Join-Path $uwpBuild 'RelWithDebInfo')"
