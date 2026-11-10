[CmdletBinding()]
param(
    [string]$WebRtcRoot = 'C:\wrtcar\src',
    [string]$OutputRoot = (Join-Path $env:LOCALAPPDATA 'UnigramTdlibExperiment\bridge-probe'),
    [string]$ZlibRoot = (Join-Path $env:LOCALAPPDATA 'UnigramTdlibExperiment\vcpkg_installed\arm-uwp-dynamic-v141')
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'bridge-probe\ModernCallsBridge.vcxproj'
$engineProject = Join-Path $PSScriptRoot 'native-engine\TgCallsEngine.vcxproj'
$msbuild = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe'
$tgCallsRoot = Join-Path $env:LOCALAPPDATA 'UnigramTdlibExperiment\tgcalls'
$tgCallsPatches = @(
    @{ Path = Join-Path $PSScriptRoot 'patches\tgcalls-m123-winuwp-audio-device.patch'; Name = 'M123 audio-device compatibility' }
    @{ Path = Join-Path $PSScriptRoot 'patches\tgcalls-m123-incoming-audio-counters.patch'; Name = 'incoming audio receive-path counters' }
    @{ Path = Join-Path $PSScriptRoot 'patches\tgcalls-m123-v2-uwp-arm-h264-bitrate.patch'; Name = 'V2 ARM UWP H.264 bitrate policy'; ZeroContext = $true }
)
$expectedTgCallsCommit = '1c236c09f8d8569fead14bd68000618a52051225'
$expectedZlibVersion = '1.3.2'

if (-not (Test-Path -LiteralPath (Join-Path $WebRtcRoot 'out\msvc\uwp\Release\arm\obj\webrtc.lib'))) {
    throw "Release ARM webrtc.lib was not found below '$WebRtcRoot'."
}

foreach ($path in @(
    (Join-Path $ZlibRoot 'include\zlib.h'),
    (Join-Path $ZlibRoot 'lib\z.lib')
)) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Pinned ARM UWP zlib input was not found at '$path'."
    }
}

$zlibVersion = (Select-String -LiteralPath (Join-Path $ZlibRoot 'include\zlib.h') -Pattern '^#define ZLIB_VERSION "([^"]+)"' |
    Select-Object -First 1).Matches.Groups[1].Value
if ($zlibVersion -ne $expectedZlibVersion) {
    throw "Unexpected zlib version '$zlibVersion'; expected '$expectedZlibVersion'."
}

if (-not (Test-Path -LiteralPath $tgCallsRoot)) {
    throw "The pinned TgCalls checkout was not found at '$tgCallsRoot'."
}

$actualTgCallsCommit = (& git -C $tgCallsRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) {
    throw "Unable to read the TgCalls revision at '$tgCallsRoot'."
}

if ($actualTgCallsCommit -ne $expectedTgCallsCommit) {
    throw "Unexpected TgCalls revision '$actualTgCallsCommit'; expected '$expectedTgCallsCommit'."
}

Push-Location $tgCallsRoot
try {
    foreach ($patch in $tgCallsPatches) {
        if (-not (Test-Path -LiteralPath $patch.Path)) {
            throw "The TgCalls $($patch.Name) patch was not found at '$($patch.Path)'."
        }

        $applyOptions = @('--ignore-whitespace')
        if ($patch.ZeroContext) {
            $applyOptions += '--unidiff-zero'
        }

        & git apply --reverse --check @applyOptions $patch.Path 2>$null
        if ($LASTEXITCODE -ne 0) {
            # A plain apply works whenever the tree is clean for this patch; --3way is the
            # recovery path and needs the blobs it references to be reachable, which is not
            # the case once an earlier patch has been staged over the same checkout.
            & git apply @applyOptions --whitespace=nowarn $patch.Path 2>$null
            if ($LASTEXITCODE -ne 0) {
                # --3way implies --index: on conflict it writes conflict markers into the
                # working tree and records conflicted index stages, and leaves them there
                # when it fails. Without cleanup every later build fails identically --
                # reverse-check fails, plain apply fails against the marker-laden file,
                # --3way conflicts again -- with nothing to say the checkout needs resetting.
                $touched = (& git apply --numstat @applyOptions $patch.Path 2>$null |
                    ForEach-Object { ($_ -split "`t")[2] }) | Where-Object { $_ }

                & git apply --3way @applyOptions $patch.Path
                if ($LASTEXITCODE -ne 0) {
                    foreach ($file in $touched) {
                        & git reset -q -- $file 2>$null
                        & git checkout -- $file 2>$null
                    }

                    throw "Unable to apply the TgCalls $($patch.Name) patch to the checkout at '$tgCallsRoot'. Any partial result was reverted; re-run the build."
                }
            }
        }
    }
}
finally {
    Pop-Location
}

$engineOutputRoot = Join-Path $OutputRoot 'native-engine'
& $msbuild $engineProject /nologo /m /t:Rebuild /p:Configuration=Release /p:Platform=ARM /p:WebRtcRoot=$WebRtcRoot /p:TgCallsRoot=$tgCallsRoot /p:ZlibRoot=$ZlibRoot /p:OutputDirectory=$engineOutputRoot
if ($LASTEXITCODE -ne 0) {
    throw "The ARM UWP TgCalls engine library failed with exit code $LASTEXITCODE."
}

& $msbuild $project /nologo /m /t:Rebuild /p:Configuration=Release /p:Platform=ARM /p:WebRtcRoot=$WebRtcRoot /p:TgCallsEngineOutputRoot=$engineOutputRoot /p:ZlibRoot=$ZlibRoot /p:OutputDirectory=$OutputRoot
if ($LASTEXITCODE -ne 0) {
    throw "The ARM UWP C++/CX bridge proof failed with exit code $LASTEXITCODE."
}
