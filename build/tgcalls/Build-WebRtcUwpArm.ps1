[CmdletBinding()]
param(
    [string]$DependencyScriptsRoot = (Join-Path $env:LOCALAPPDATA 'UnigramTdlibExperiment\webrtc-uwp-deps'),
    [string]$Root = 'C:\wrtcar',
    [ValidateSet('Release', 'Debug')][string[]]$Configuration = @('Release', 'Debug'),
    [switch]$SkipAcquire
)

$ErrorActionPreference = 'Stop'

$expectedScriptsCommit = '6ce0019e1e5ea4e06ab3bc21651242567774a4e0'
$buildScript = Join-Path $DependencyScriptsRoot 'webrtc\build.ps1'
$patches = @{
    Rust = Join-Path $PSScriptRoot 'patches\webrtc-m123-winuwp-arm-no-rust.patch'
    Runtime = Join-Path $PSScriptRoot 'patches\webrtc-m123-winuwp-arm-skip-runtime-copy.patch'
    AudioAssembly = Join-Path $PSScriptRoot 'patches\webrtc-m123-winuwp-arm-scalar-audio.patch'
    Pffft = Join-Path $PSScriptRoot 'patches\webrtc-m123-winuwp-arm-scalar-pffft.patch'
    Denormal = Join-Path $PSScriptRoot 'patches\webrtc-m123-winuwp-arm-msvc-denormal.patch'
    BoringSsl = Join-Path $PSScriptRoot 'patches\webrtc-m123-winuwp-arm-boringssl-no-asm.patch'
    Opus = Join-Path $PSScriptRoot 'patches\webrtc-m123-winuwp-arm-scalar-opus.patch'
    AudioOnlyLibaom = Join-Path $PSScriptRoot 'patches\webrtc-m123-winuwp-arm-audio-only-libaom.patch'
    AudioOnlyLibvpx = Join-Path $PSScriptRoot 'patches\webrtc-m123-winuwp-arm-audio-only-libvpx.patch'
    RenderCategory = Join-Path $PSScriptRoot 'patches\webrtc-m123-winuwp-arm-render-communications-category.patch'
    BoundedMediaCapture = Join-Path $PSScriptRoot 'patches\webrtc-m123-winuwp-arm-bounded-media-capture.patch'
    CaptureInitDiagnostics = Join-Path $PSScriptRoot 'patches\webrtc-m123-winuwp-arm-capture-init-diagnostics.patch'
    CapturePcmMixFallback = Join-Path $PSScriptRoot 'patches\webrtc-m123-winuwp-arm-capture-pcm-mix-fallback.patch'
}

if (-not (Test-Path -LiteralPath $buildScript)) {
    throw "The pinned WebRTC UWP build script was not found at '$buildScript'."
}

foreach ($patch in $patches.Values) {
    if (-not (Test-Path -LiteralPath $patch)) {
        throw "The ARM UWP compatibility patch was not found at '$patch'."
    }
}

$actualScriptsCommit = (& git -C $DependencyScriptsRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) {
    throw "Unable to read the WebRTC UWP build-script revision at '$DependencyScriptsRoot'."
}

if ($actualScriptsCommit -ne $expectedScriptsCommit) {
    throw "Unexpected WebRTC UWP build-script revision '$actualScriptsCommit'; expected '$expectedScriptsCommit'."
}

$scriptText = Get-Content -LiteralPath $buildScript -Raw
$architectureValidation = "[ValidateSet('x64', 'arm64')]"
$visualStudioLookup = "`$path = & `$vswhere -version '[17.0,19.0)' -latest -products * -property installationPath"

if (-not $scriptText.Contains($architectureValidation) -or -not $scriptText.Contains($visualStudioLookup)) {
    throw 'The pinned WebRTC UWP build script no longer has the expected architecture or Visual Studio lookup.'
}

$scriptText = $scriptText.Replace(
    $architectureValidation,
    "[ValidateSet('x64', 'arm', 'arm64')]"
)
$scriptText = $scriptText.Replace(
    $visualStudioLookup,
    "`$path = & `$vswhere -version '[17.0,19.0)' -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.ARM64 -property installationPath"
)
$scriptText = $scriptText.Replace(
    'target_cpu=\`"$a\`""',
    'target_cpu=\`"$a\`" enable_rust=false arm_version=6 arm_use_neon=false enable_libaom=false rtc_build_libvpx=false rtc_libvpx_build_vp9=false"'
)

$patchInjection = @'
Use-Patch (Join-Path $Src 'build') '__RUST_PATCH__'
Use-Patch (Join-Path $Src 'build') '__RUNTIME_PATCH__'
Use-Patch $Src '__AUDIO_ASSEMBLY_PATCH__'
Use-Patch (Join-Path $Src 'third_party') '__PFFFT_PATCH__'
Use-Patch $Src '__DENORMAL_PATCH__'
Use-Patch (Join-Path $Src 'third_party') '__BORINGSSL_PATCH__'

$boringSslTlsSource = Join-Path $Src 'third_party\boringssl\src\crypto\thread_win.c'
$boringSslTlsText = Get-Content -LiteralPath $boringSslTlsSource -Raw
if ($boringSslTlsText.Contains('#if defined(_WIN64) || defined(_M_ARM)')) {
    Write-Host '  already applied: webrtc-m123-winuwp-arm-boringssl-tls.patch'
} else {
    $boringSslTlsUpdated = [regex]::Replace($boringSslTlsText, '(?m)^#ifdef _WIN64(?=\r?\n__pragma)', '#if defined(_WIN64) || defined(_M_ARM)', 1)
    if ($boringSslTlsUpdated -eq $boringSslTlsText) { throw 'The ARM UWP BoringSSL TLS-linker patch does not apply cleanly.' }
    Write-Host '  applying: webrtc-m123-winuwp-arm-boringssl-tls.patch'
    [System.IO.File]::WriteAllText($boringSslTlsSource, $boringSslTlsUpdated, [System.Text.UTF8Encoding]::new($false))
}

Use-Patch (Join-Path $Src 'third_party') '__OPUS_PATCH__'
Use-Patch $Src '__AUDIO_ONLY_LIBAOM_PATCH__'
Use-Patch $Src '__AUDIO_ONLY_LIBVPX_PATCH__'
Use-Patch $Src '__RENDER_CATEGORY_PATCH__'
Use-Patch $Src '__BOUNDED_MEDIA_CAPTURE_PATCH__'

$captureDiagnosticsSource = Join-Path $Src 'modules\audio_device\win\audio_device_core_win.cc'
$captureDiagnosticsText = Get-Content -LiteralPath $captureDiagnosticsSource -Raw
if ($captureDiagnosticsText.Contains('g_unigram_webrtc_capture_init_stage')) {
    Write-Host '  already applied: webrtc-m123-winuwp-arm-capture-init-diagnostics.patch'
} else {
    Use-Patch $Src '__CAPTURE_INIT_DIAGNOSTICS_PATCH__'
}

Use-Patch $Src '__CAPTURE_PCM_MIX_FALLBACK_PATCH__'
'@

$patchInjection = $patchInjection.Replace('__RUST_PATCH__', $patches.Rust)
$patchInjection = $patchInjection.Replace('__RUNTIME_PATCH__', $patches.Runtime)
$patchInjection = $patchInjection.Replace('__AUDIO_ASSEMBLY_PATCH__', $patches.AudioAssembly)
$patchInjection = $patchInjection.Replace('__PFFFT_PATCH__', $patches.Pffft)
$patchInjection = $patchInjection.Replace('__DENORMAL_PATCH__', $patches.Denormal)
$patchInjection = $patchInjection.Replace('__BORINGSSL_PATCH__', $patches.BoringSsl)
$patchInjection = $patchInjection.Replace('__OPUS_PATCH__', $patches.Opus)
$patchInjection = $patchInjection.Replace('__AUDIO_ONLY_LIBAOM_PATCH__', $patches.AudioOnlyLibaom)
$patchInjection = $patchInjection.Replace('__AUDIO_ONLY_LIBVPX_PATCH__', $patches.AudioOnlyLibvpx)
$patchInjection = $patchInjection.Replace('__RENDER_CATEGORY_PATCH__', $patches.RenderCategory)
$patchInjection = $patchInjection.Replace('__BOUNDED_MEDIA_CAPTURE_PATCH__', $patches.BoundedMediaCapture)
$patchInjection = $patchInjection.Replace('__CAPTURE_INIT_DIAGNOSTICS_PATCH__', $patches.CaptureInitDiagnostics)
$patchInjection = $patchInjection.Replace('__CAPTURE_PCM_MIX_FALLBACK_PATCH__', $patches.CapturePcmMixFallback)

$scriptText = $scriptText.Replace(
    '$vs = Get-VisualStudio',
    $patchInjection + "`r`n`$vs = Get-VisualStudio"
)

if (-not $scriptText.Contains('enable_rust=false arm_version=6 arm_use_neon=false enable_libaom=false rtc_build_libvpx=false rtc_libvpx_build_vp9=false') -or
    -not $scriptText.Contains($patches.Rust) -or
    -not $scriptText.Contains($patches.Runtime) -or
    -not $scriptText.Contains($patches.AudioAssembly) -or
    -not $scriptText.Contains($patches.Pffft) -or
    -not $scriptText.Contains($patches.Denormal) -or
    -not $scriptText.Contains($patches.BoringSsl) -or
    -not $scriptText.Contains($patches.Opus) -or
    -not $scriptText.Contains($patches.AudioOnlyLibaom) -or
    -not $scriptText.Contains($patches.AudioOnlyLibvpx) -or
    -not $scriptText.Contains($patches.RenderCategory) -or
    -not $scriptText.Contains($patches.BoundedMediaCapture) -or
    -not $scriptText.Contains($patches.CaptureInitDiagnostics) -or
    -not $scriptText.Contains($patches.CapturePcmMixFallback)) {
    throw 'Unable to apply the ARM UWP compatibility configuration to the temporary build script.'
}

$temporaryBuildScript = Join-Path (Split-Path -Parent $buildScript) ('.copilot-arm-' + [Guid]::NewGuid().ToString('N') + '.ps1')
[System.IO.File]::WriteAllText($temporaryBuildScript, $scriptText, [System.Text.UTF8Encoding]::new($false))

try {
    $arguments = @{
        Root = $Root
        Arch = @('arm')
        Configuration = $Configuration
    }
    if ($SkipAcquire) {
        $arguments.SkipAcquire = $true
    }

    & $temporaryBuildScript @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "The ARM UWP WebRTC build failed with exit code $LASTEXITCODE."
    }
}
finally {
    if (Test-Path -LiteralPath $temporaryBuildScript) {
        Remove-Item -LiteralPath $temporaryBuildScript -Force
    }
}
