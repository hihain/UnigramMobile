# Packages the pinned modern TDLib ARM UWP build as a Visual Studio Extension SDK (VSIX),
# using the same layout as TDLib's example/uwp/build.ps1 (export-vsix), restricted to ARM.
param(
    [string]$WorkRoot = (Join-Path $env:LOCALAPPDATA "UnigramTdlibExperiment"),
    [string]$BuildRoot,
    [string]$OutputDirectory
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

if (-not $BuildRoot) { $BuildRoot = Join-Path $WorkRoot "build-uwp-arm\RelWithDebInfo" }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $WorkRoot "artifacts\vsix" }
$template = Join-Path $WorkRoot "tdlib\example\uwp"
$manifestPath = Join-Path $BuildRoot "TdlibBuildManifest.txt"

$buildInfo = @{}
Get-Content $manifestPath | ForEach-Object {
    $kv = $_ -split "=", 2
    if ($kv.Count -eq 2) { $buildInfo[$kv[0]] = $kv[1] }
}
$version = $buildInfo["TDLIB_VERSION"]
if (-not $version) { throw "TDLIB_VERSION missing from $manifestPath" }

$references = @("Telegram.Td.winmd", "Telegram.Td.pri")
$docs = Join-Path $BuildRoot "Telegram.Td.xml"
if ((Test-Path $docs) -and (Get-Item $docs).Length -gt 0) { $references += "Telegram.Td.xml" }
$redist = @("Telegram.Td.dll", "libcrypto-3-arm.dll", "libssl-3-arm.dll", "z.dll")
$designTime = @("Telegram.Td.lib")
foreach ($f in $references + $redist + $designTime) {
    if (-not (Test-Path (Join-Path $BuildRoot $f))) { throw "Missing build output: $f" }
}

$staging = Join-Path $OutputDirectory "staging"
Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $staging | Out-Null

function Copy-Into([string[]]$files, [string]$relative) {
    $dest = Join-Path $staging $relative
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    foreach ($f in $files) { Copy-Item (Join-Path $BuildRoot $f) $dest }
}
Copy-Into $references "References\CommonConfiguration\ARM"
Copy-Into $redist "Redist\CommonConfiguration\ARM"
Copy-Into $designTime "DesignTime\CommonConfiguration\ARM"

Copy-Item (Join-Path $template "LICENSE_1_0.txt") $staging
Copy-Item $manifestPath $staging

$patchDest = Join-Path $staging "patches"
New-Item -ItemType Directory -Force -Path $patchDest | Out-Null
Copy-Item (Join-Path $PSScriptRoot "patches\*.patch") $patchDest

$utf8 = New-Object System.Text.UTF8Encoding($false)

$sdkManifest = (Get-Content -LiteralPath (Join-Path $template "SDKManifest.xml") -Raw) `
    -replace 'SupportedArchitectures="[^"]*"', 'SupportedArchitectures="ARM"'
[IO.File]::WriteAllText((Join-Path $staging "SDKManifest.xml"), $sdkManifest, $utf8)

$vsixManifest = (Get-Content -LiteralPath (Join-Path $template "extension.vsixmanifest") -Raw) `
    -replace 'Version="[0-9.]+" Language', ('Version="' + $version + '" Language') `
    -replace '<Description>[^<]*</Description>', ('<Description>TDLib ' + $version + ' (commit ' + $buildInfo["TDLIB_COMMIT"] + ') C++/CX for UWP ARM32 / Windows 10 Mobile. OpenSSL ' + $buildInfo["OPENSSL_VERSION"] + ', zlib ' + $buildInfo["ZLIB_VERSION"] + '. Built by the UnigramMobile project; see patches/ for binding-generator changes.</Description>')
[IO.File]::WriteAllText((Join-Path $staging "extension.vsixmanifest"), $vsixManifest, $utf8)

$contentTypes = (Get-Content -LiteralPath (Join-Path $template "[Content_Types].xml") -Raw) `
    -replace '(<Default Extension="txt")', ('<Default Extension="patch" ContentType="text/plain" />' + "`r`n  " + '$1')
[IO.File]::WriteAllText((Join-Path $staging "[Content_Types].xml"), $contentTypes, $utf8)

$vsix = Join-Path $OutputDirectory "Telegram.Td.UWP-$version-ARM.vsix"
Remove-Item $vsix -Force -ErrorAction SilentlyContinue
$zip = [IO.Compression.ZipFile]::Open($vsix, "Create")
try {
    Get-ChildItem $staging -Recurse -File | ForEach-Object {
        $entry = $_.FullName.Substring($staging.Length + 1).Replace("\", "/")
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $_.FullName, $entry, "Optimal") | Out-Null
    }
} finally {
    $zip.Dispose()
}
Remove-Item $staging -Recurse -Force

$hash = (Get-FileHash $vsix -Algorithm SHA256).Hash
Write-Host "VSIX:   $vsix"
Write-Host "SHA256: $hash"
