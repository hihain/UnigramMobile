[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$OutputRoot,
    [string]$ExpectedTdlibVersion = "1.8.66",
    [string]$ExpectedTdlibCommit = "022d60202e446ad1287b9fb68e687c8a0760788b"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

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

$requiredFiles = @(
    "Telegram.Td.dll",
    "Telegram.Td.winmd",
    "Telegram.Td.pri",
    "libcrypto-3-arm.dll",
    "libssl-3-arm.dll",
    "z.dll"
)

$manifestPath = Join-Path $OutputRoot "TdlibBuildManifest.txt"
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "TDLib build manifest is missing: $manifestPath. Rebuild with Build-TdlibArm.ps1."
}

$manifest = @{}
foreach ($line in Get-Content -LiteralPath $manifestPath) {
    $parts = $line -split "=", 2
    if ($parts.Count -eq 2) {
        $manifest[$parts[0]] = $parts[1]
    }
}

if ($manifest["TDLIB_VERSION"] -ne $ExpectedTdlibVersion) {
    throw "TDLib version mismatch. Expected $ExpectedTdlibVersion, found $($manifest["TDLIB_VERSION"])."
}
if ($manifest["TDLIB_COMMIT"] -ne $ExpectedTdlibCommit) {
    throw "TDLib commit mismatch. Expected $ExpectedTdlibCommit, found $($manifest["TDLIB_COMMIT"])."
}

foreach ($name in $requiredFiles) {
    $path = Join-Path $OutputRoot $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required ARM TDLib output is missing: $path"
    }
}

function Test-ArmPe {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 0x40 -or $bytes[0] -ne 0x4d -or $bytes[1] -ne 0x5a) {
        return $false
    }

    $peOffset = [System.BitConverter]::ToInt32($bytes, 0x3c)
    if ($peOffset -lt 0 -or $peOffset + 6 -gt $bytes.Length) {
        return $false
    }

    $signature = [System.BitConverter]::ToUInt32($bytes, $peOffset)
    $machine = [System.BitConverter]::ToUInt16($bytes, $peOffset + 4)
    return $signature -eq 0x00004550 -and $machine -eq 0x01c4
}

foreach ($name in @("Telegram.Td.dll", "libcrypto-3-arm.dll", "libssl-3-arm.dll", "z.dll")) {
    $path = Join-Path $OutputRoot $name
    if (-not (Test-ArmPe -Path $path)) {
        throw "$name does not contain an ARM PE image."
    }
}

$tdDllHash = Get-Sha256 (Join-Path $OutputRoot "Telegram.Td.dll")
$tdWinmdHash = Get-Sha256 (Join-Path $OutputRoot "Telegram.Td.winmd")
if ($manifest["TELEGRAM_TD_DLL_SHA256"] -ne $tdDllHash) {
    throw "Telegram.Td.dll does not match the hash recorded by the pinned build."
}
if ($manifest["TELEGRAM_TD_WINMD_SHA256"] -ne $tdWinmdHash) {
    throw "Telegram.Td.winmd does not match the hash recorded by the pinned build."
}

Get-ChildItem -LiteralPath $OutputRoot -File |
    Where-Object { $_.Name -in $requiredFiles } |
    ForEach-Object {
        [PSCustomObject]@{
            File = $_.FullName
            Sha256 = Get-Sha256 $_.FullName
        }
    }
