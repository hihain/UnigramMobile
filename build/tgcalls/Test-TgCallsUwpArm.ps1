[CmdletBinding()]
param(
    [string]$TgCallsRoot = (Join-Path $env:LOCALAPPDATA 'UnigramTdlibExperiment\tgcalls'),
    [string]$WebRtcRoot,
    [string]$VcVarsAllPath = 'C:\Program Files (x86)\Microsoft Visual Studio\2017\Professional\VC\Auxiliary\Build\vcvarsall.bat',
    [ValidateSet('x86_arm', 'x64_arm')][string]$VcVarsArchitecture = 'x86_arm',
    [string]$WebRtcOutputRoot
)

$ErrorActionPreference = 'Stop'

$expectedCommit = '1c236c09f8d8569fead14bd68000618a52051225'
$instanceSource = Join-Path $TgCallsRoot 'tgcalls\Instance.cpp'
$engineSource = Join-Path $TgCallsRoot 'tgcalls\InstanceImpl.cpp'
$audioDevicePatch = Join-Path $PSScriptRoot 'patches\tgcalls-m123-winuwp-audio-device.patch'

if (-not (Test-Path -LiteralPath $instanceSource) -or -not (Test-Path -LiteralPath $engineSource)) {
    throw "TgCalls source is incomplete: expected '$instanceSource' and '$engineSource'."
}

if (-not (Test-Path -LiteralPath $audioDevicePatch)) {
    throw "The M123 TgCalls audio-device compatibility patch was not found at '$audioDevicePatch'."
}

if (-not (Test-Path -LiteralPath $VcVarsAllPath)) {
    throw "The ARM compiler environment script was not found at '$VcVarsAllPath'."
}

$actualCommit = (& git -C $TgCallsRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) {
    throw "Unable to read the TgCalls Git revision at '$TgCallsRoot'."
}

if ($actualCommit -ne $expectedCommit) {
    throw "Unexpected TgCalls revision '$actualCommit'; expected '$expectedCommit'."
}

Push-Location $TgCallsRoot
try {
    & git apply --reverse --check --ignore-whitespace $audioDevicePatch 2>$null
    if ($LASTEXITCODE -ne 0) {
        & git apply --3way --ignore-whitespace $audioDevicePatch
        if ($LASTEXITCODE -ne 0) {
            throw "Unable to apply the M123 TgCalls audio-device compatibility patch."
        }
    }
}
finally {
    Pop-Location
}

$includeArguments = @("/I `"$TgCallsRoot`"")
$compileArguments = @('/EHsc', '/std:c++17')
if ($WebRtcRoot) {
    if (-not (Test-Path -LiteralPath (Join-Path $WebRtcRoot 'rtc_base\logging.h'))) {
        throw "WebRTC root '$WebRtcRoot' does not contain rtc_base\logging.h."
    }

    $abseilRoot = Join-Path $WebRtcRoot 'third_party\abseil-cpp'
    if (-not (Test-Path -LiteralPath (Join-Path $abseilRoot 'absl\base\attributes.h'))) {
        throw "WebRTC root '$WebRtcRoot' does not contain third_party\abseil-cpp\absl\base\attributes.h."
    }

    $includeArguments += "/I `"$WebRtcRoot`""
    $includeArguments += "/I `"$abseilRoot`""

    if (-not $WebRtcOutputRoot) {
        $WebRtcOutputRoot = Join-Path $WebRtcRoot 'out\msvc\uwp\Release\arm'
    }

    $webRtcGeneratedRoot = Join-Path $WebRtcOutputRoot 'gen'
    if (-not (Test-Path -LiteralPath $webRtcGeneratedRoot)) {
        throw "WebRTC output '$WebRtcOutputRoot' does not contain generated headers at '$webRtcGeneratedRoot'."
    }

    $includeArguments += "/I `"$webRtcGeneratedRoot`""
    $compileArguments = @(
        '/EHsc',
        '/std:c++20',
        '/Zc:__cplusplus',
        '/DWEBRTC_WIN',
        '/DWINUWP',
        '/DWINAPI_FAMILY=WINAPI_FAMILY_PC_APP',
        '/DWIN32_LEAN_AND_MEAN',
        '/DNOMINMAX',
        '/D_UNICODE',
        '/DUNICODE',
        '/DNTDDI_VERSION=NTDDI_WIN10_NI',
        '/D_WIN32_WINNT=0x0A00',
        '/DWINVER=0x0A00',
        '/DWEBRTC_ENABLE_PROTOBUF=0',
        '/DWEBRTC_STRICT_FIELD_TRIALS=0',
        '/DWEBRTC_HAVE_SCTP',
        '/DWEBRTC_ARCH_ARM',
        '/DRTC_DISABLE_METRICS',
        '/DWEBRTC_VIDEO_CAPTURE_WINRT'
    )
}

$probeDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ('UnigramTgCallsProbe-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $probeDirectory | Out-Null

try {
    $metadataObject = Join-Path $probeDirectory 'Instance.obj'
    $engineObject = Join-Path $probeDirectory 'InstanceImpl.obj'
    $includeCommand = $includeArguments -join ' '
    $compileCommand = $compileArguments -join ' '
    $installerPath = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'

    $metadataCommand = "set `"PATH=$installerPath;%PATH%`" && call `"$VcVarsAllPath`" $VcVarsArchitecture >nul && cd /d `"$TgCallsRoot`" && cl /nologo $compileCommand /c $includeCommand /Fo`"$metadataObject`" tgcalls\Instance.cpp"
    & $env:ComSpec /d /c $metadataCommand
    if ($LASTEXITCODE -ne 0) {
        throw "The ARM metadata compilation probe failed."
    }

    $engineCommand = "set `"PATH=$installerPath;%PATH%`" && call `"$VcVarsAllPath`" $VcVarsArchitecture >nul && cd /d `"$TgCallsRoot`" && cl /nologo $compileCommand /c $includeCommand /Fo`"$engineObject`" tgcalls\InstanceImpl.cpp"
    & $env:ComSpec /d /c $engineCommand
    if ($LASTEXITCODE -ne 0) {
        throw "The ARM private-call engine compilation probe failed. Supply a compatible WebRTC source root with -WebRtcRoot before attempting wrapper integration."
    }

    Write-Host "TgCalls ARM UWP source probes passed for $actualCommit."
}
finally {
    if (Test-Path -LiteralPath $probeDirectory) {
        Remove-Item -LiteralPath $probeDirectory -Recurse -Force
    }
}
