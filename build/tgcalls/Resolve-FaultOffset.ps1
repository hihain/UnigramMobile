<#
.SYNOPSIS
    Resolves an `offset=` value from a voip.fault diagnostic to the function it fell in.

.DESCRIPTION
    The native fault handler reports a fault address as an offset from the module base,
    which is the same thing as a relative virtual address. A linker map file lists every
    function by absolute address together with the preferred load address, so subtracting
    one from the other turns each entry into an RVA and the answer is the last entry at or
    below the reported offset.

    Use the map archived beside the artifacts for the build that produced the log. A map
    from any other build will resolve to the wrong function.

.EXAMPLE
    .\Resolve-FaultOffset.ps1 -Offset 0x000afbdc `
        -MapPath "$env:LOCALAPPDATA\UnigramTdlibExperiment\artifacts\ModernCallsBridge_26.9.6157.0.map"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Offset,

    [Parameter(Mandatory)]
    [string] $MapPath,

    [int] $Context = 3
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $MapPath)) {
    throw "Map file not found: $MapPath"
}

$target = [Convert]::ToUInt32(($Offset -replace '^0x', ''), 16)

$preferred = $null
$symbols = [System.Collections.Generic.List[object]]::new()

# Entries look like: 0001:0001d0ec  ?Name@@sig  1001e0ec f i Lib:object.obj
$entry = [regex]'^\s[0-9A-Fa-f]{4}:[0-9A-Fa-f]{8}\s+(\S+)\s+([0-9A-Fa-f]{8})\s'

foreach ($line in [System.IO.File]::ReadLines($MapPath)) {
    if ($null -eq $preferred) {
        if ($line -match 'Preferred load address is ([0-9A-Fa-f]+)') {
            $preferred = [Convert]::ToUInt32($Matches[1], 16)
        }
        continue
    }

    $match = $entry.Match($line)
    if (-not $match.Success) {
        continue
    }

    $address = [Convert]::ToUInt32($match.Groups[2].Value, 16)
    if ($address -lt $preferred) {
        continue
    }

    $symbols.Add([pscustomobject]@{
        Rva    = $address - $preferred
        Symbol = $match.Groups[1].Value
        Source = ($line -split '\s+')[-1]
    })
}

if ($null -eq $preferred) {
    throw "No preferred load address in $MapPath; it does not look like a linker map."
}

if ($symbols.Count -eq 0) {
    throw "No symbols parsed from $MapPath."
}

$ordered = $symbols | Sort-Object Rva

# The owning function is the last symbol starting at or before the faulting address.
$index = -1
for ($i = 0; $i -lt $ordered.Count; $i++) {
    if ($ordered[$i].Rva -le $target) { $index = $i } else { break }
}

if ($index -lt 0) {
    throw ("Offset 0x{0:X8} is below the first symbol in the map." -f $target)
}

$hit = $ordered[$index]

Write-Host ("Offset   0x{0:X8}" -f $target)
Write-Host ("Function {0}" -f $hit.Symbol)
Write-Host ("Starts   0x{0:X8}  (+0x{1:X} into the function)" -f $hit.Rva, ($target - $hit.Rva))
Write-Host ("Object   {0}" -f $hit.Source)

if ($Context -gt 0) {
    Write-Host ''
    Write-Host 'Neighbouring symbols:'
    $from = [Math]::Max(0, $index - $Context)
    $to = [Math]::Min($ordered.Count - 1, $index + $Context)
    for ($i = $from; $i -le $to; $i++) {
        $marker = if ($i -eq $index) { '>' } else { ' ' }
        Write-Host ("{0} 0x{1:X8}  {2}" -f $marker, $ordered[$i].Rva, $ordered[$i].Symbol)
    }
}
