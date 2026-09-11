#Requires -Version 7.0
<#
.SYNOPSIS
    Installs a sideload package onto a Windows 10 Mobile phone over the Windows Device
    Portal.

.DESCRIPTION
    Deliberately an update and not a fresh install. Uninstalling first would be the simpler
    script, but it takes the TDLib database and the signed-in session with it, and getting
    those back means logging in again on a phone. The Device Portal only accepts an update
    whose version is higher than the one already installed, and the version is the commit
    count of the branch being built -- so a branch with fewer commits than whatever is on
    the phone cannot be deployed over it. Build from a branch that includes the installed
    one, or accept the uninstall and do it by hand.

    The phone drops its Wi-Fi while the screen is off and the connection then times out.
    Wake the screen and run it again.

.PARAMETER Ip
    The phone's address. No default on purpose: an address from someone's home network has
    no business in a repository. Set UNIGRAM_DEPLOY_IP instead of passing it every time.

.PARAMETER Ver
    Package version, e.g. 26.9.6168.0. Read from Package.appxmanifest when omitted, which
    is what the last build left behind.

.EXAMPLE
    pwsh -File tools\deploy-lumia.ps1 -Ip 192.168.1.2
#>
param(
    [string]$Ip = $env:UNIGRAM_DEPLOY_IP,
    [string]$Ver = '',
    [string]$Root = (Split-Path -Parent $PSScriptRoot),
    [switch]$WithDependencies
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http

if (-not $Ip) {
    throw 'No address. Pass -Ip, or set UNIGRAM_DEPLOY_IP once and forget about it.'
}

if (-not $Ver) {
    $manifest = [IO.File]::ReadAllText((Join-Path $Root 'Unigram\Unigram\Package.appxmanifest'))
    $Ver = ([regex]::Match($manifest, '<Identity[^>]*\sVersion="([0-9.]+)"')).Groups[1].Value
    Write-Host "[deploy] version from manifest: $Ver"
}

$dir = Join-Path $Root "Unigram\Unigram\AppPackages\Unigram_${Ver}_Test"
$bundle = Join-Path $dir "Unigram_${Ver}_arm.appxbundle"
$cer = Join-Path $dir "Unigram_${Ver}_arm.cer"

if (-not (Test-Path $bundle)) { throw "No package at $bundle" }

$files = @($bundle, $cer)
if ($WithDependencies) {
    # Only needed for a first install. An update finds them already on the phone.
    $files += (Get-ChildItem (Join-Path $dir 'Dependencies\arm') -Filter *.appx | ForEach-Object { $_.FullName })
}

$base = "https://${Ip}:443"
$h = [Net.Http.HttpClientHandler]::new()
$h.ServerCertificateCustomValidationCallback = [Net.Http.HttpClientHandler]::DangerousAcceptAnyServerCertificateValidator
$h.CookieContainer = [Net.CookieContainer]::new()
$cli = [Net.Http.HttpClient]::new($h)
$cli.Timeout = [TimeSpan]::FromMinutes(15)
$script:Csrf = $null

function Sync-Csrf($resp) {
    $sc = $null
    if ($resp.Headers.TryGetValues('Set-Cookie', [ref]$sc)) {
        foreach ($l in $sc) {
            if ($l -match 'CSRF-Token=([^;,\s]+)') {
                $script:Csrf = $Matches[1]
                try { $h.CookieContainer.Add([Uri]$base, [Net.Cookie]::new('CSRF-Token', $script:Csrf, '/', $Ip)) } catch {}
            }
        }
    }
}

function Wdp([string]$m, [string]$p, [Net.Http.HttpContent]$c) {
    $req = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::new($m), "$base$p")
    if ($script:Csrf) { $req.Headers.Add('X-CSRF-Token', $script:Csrf) }
    if ($c) { $req.Content = $c }
    $r = $cli.SendAsync($req).GetAwaiter().GetResult()
    Sync-Csrf $r
    return $r
}

function Txt($r) { return $r.Content.ReadAsStringAsync().Result }

function Installed() {
    return ((Txt (Wdp GET '/api/app/packagemanager/packages' $null)) | ConvertFrom-Json).InstalledPackages |
        Where-Object { $_.PackageFullName -like '*UnigramMobileFork*' }
}

Sync-Csrf ($cli.GetAsync("$base/api/os/info").GetAwaiter().GetResult())
$os = (Txt (Wdp GET '/api/os/info' $null)) | ConvertFrom-Json
Write-Host "[deploy] device: $($os.ComputerName)  $($os.OsVersion)"

$before = Installed
if ($before) {
    Write-Host "[deploy] installed: $($before.PackageFullName)"

    # An update over a running app is refused often enough to be worth avoiding.
    $pkg64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($before.PackageFullName))
    $k = Wdp DELETE "/api/taskmanager/app?package=$([uri]::EscapeDataString($pkg64))" $null
    Write-Host "[deploy] stop running app: $([int]$k.StatusCode)"
    Start-Sleep 3
}
else {
    Write-Host '[deploy] nothing installed yet -- a first install needs -WithDependencies'
}

$mp = [Net.Http.MultipartFormDataContent]::new()
foreach ($f in $files) {
    $name = [IO.Path]::GetFileName($f)
    Write-Host ("[deploy] adding {0} ({1:N1} MB)" -f $name, ((Get-Item $f).Length / 1MB))
    $bc = [Net.Http.ByteArrayContent]::new([IO.File]::ReadAllBytes($f))
    $bc.Headers.ContentDisposition = [Net.Http.Headers.ContentDispositionHeaderValue]::new('form-data')
    $bc.Headers.ContentDisposition.Name = "`"$name`""
    $bc.Headers.ContentDisposition.FileName = "`"$name`""
    $bc.Headers.ContentType = [Net.Http.Headers.MediaTypeHeaderValue]::new('application/octet-stream')
    $mp.Add($bc)
}

Write-Host "[deploy] uploading $(Get-Date -Format HH:mm:ss) ..."
$ins = Wdp POST "/api/app/packagemanager/package?package=$([uri]::EscapeDataString([IO.Path]::GetFileName($bundle)))" $mp
Write-Host "[deploy] install request: $([int]$ins.StatusCode)  $(Txt $ins)"

$ok = $false
for ($i = 0; $i -lt 80; $i++) {
    Start-Sleep 5
    $now = Installed
    if ($now -and $now.PackageFullName -like "*_${Ver}_*") { $ok = $true; break }

    $state = try { (Txt (Wdp GET '/api/app/packagemanager/state' $null)) } catch { '' }
    Write-Host "  ... installing ($i) $($state.Substring(0, [Math]::Min(90, $state.Length)))"
}

if (-not $ok) { Write-Host '[deploy] INSTALL NOT CONFIRMED' -ForegroundColor Red; exit 1 }

$full = (Installed).PackageFullName
Write-Host "[deploy] installed $full" -ForegroundColor Green

$praid = "$($full.Split('_')[0])_$($full.Split('__')[-1])!App"
$aid = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($praid))
$pkg = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($full))
$lr = Wdp POST "/api/taskmanager/app?appid=$([uri]::EscapeDataString($aid))&package=$([uri]::EscapeDataString($pkg))" $null
Write-Host "[deploy] launch ($praid): $([int]$lr.StatusCode)"
Write-Host "[deploy] DONE $(Get-Date -Format HH:mm:ss)" -ForegroundColor Green
