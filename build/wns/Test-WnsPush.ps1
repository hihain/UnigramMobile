<#
.SYNOPSIS
Sends one Telegram-shaped raw WNS notification directly to a device, bypassing Telegram.

.DESCRIPTION
Separates "Telegram is not sending" from "Microsoft/app is not delivering":
  1. Authenticates to WNS with the Partner Center Package SID and client secret.
     Failure here means those credentials are invalid, so Telegram cannot use them either.
  2. Optionally POSTs a raw notification to the channel URI copied from
     Settings > Diagnostics > Copy WNS channel URI.
     X-WNS-Status=received plus native.task.run/native.raw.received in
     push-diagnostics.txt proves the package, WNS and background task work, so a
     missing Telegram push is a server-side credential/api_id mapping problem.

The client secret is read with a secure prompt and is never printed or written to disk.
The access token and channel URI are not printed.

.EXAMPLE
.\Test-WnsPush.ps1 -PackageSid 'ms-app://s-1-15-2-...'
.\Test-WnsPush.ps1 -PackageSid 'ms-app://s-1-15-2-...' -ChannelUri (Get-Clipboard)
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackageSid,

    [string]$ChannelUri
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

if ($PackageSid -notmatch '^ms-app://s-1-15-2-') {
    throw 'PackageSid must be the Partner Center value that starts with ms-app://s-1-15-2-.'
}

$secure = Read-Host -AsSecureString 'Client secret'
$bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
try {
    $secret = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
}
finally {
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
}

$body = 'grant_type=client_credentials' +
    '&client_id=' + [Uri]::EscapeDataString($PackageSid) +
    '&client_secret=' + [Uri]::EscapeDataString($secret) +
    '&scope=notify.windows.com'
$secret = $null

try {
    $auth = Invoke-RestMethod -Method Post -Uri 'https://login.live.com/accesstoken.srf' -ContentType 'application/x-www-form-urlencoded' -Body $body
}
catch {
    $status = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 'none' }
    Write-Host "WNS authentication: FAILED (HTTP $status). The Package SID/client secret pair is not valid for WNS." -ForegroundColor Red
    exit 1
}
finally {
    $body = $null
}

Write-Host "WNS authentication: OK (token_type=$($auth.token_type)). These credentials are valid." -ForegroundColor Green

if (-not $ChannelUri) {
    $ChannelUri = Read-Host 'Channel URI from Settings > Diagnostics (close the app first; leave blank to skip)'
}

if (-not $ChannelUri) {
    Write-Host 'No channel URI supplied; skipped the delivery test.'
    exit 0
}

$channel = [Uri]$ChannelUri.Trim()
if ($channel.Scheme -ne 'https' -or $channel.Host -notmatch '\.notify\.windows\.com$') {
    throw 'ChannelUri must be the https://*.notify.windows.com URI copied from the app.'
}

$payload = @{
    data = @{
        loc_key  = 'MESSAGE_TEXT'
        loc_args = @('WNS delivery test', 'Direct push reached this phone')
        custom   = @{}
    }
    date = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
} | ConvertTo-Json -Depth 5 -Compress

$headers = @{
    'Authorization' = "Bearer $($auth.access_token)"
    'X-WNS-Type'    = 'wns/raw'
}

try {
    $response = Invoke-WebRequest -Method Post -Uri $channel -Headers $headers -ContentType 'application/octet-stream' -Body ([Text.Encoding]::UTF8.GetBytes($payload)) -UseBasicParsing
    Write-Host "WNS delivery: HTTP $([int]$response.StatusCode); X-WNS-Status=$($response.Headers['X-WNS-Status']); X-WNS-DeviceConnectionStatus=$($response.Headers['X-WNS-DeviceConnectionStatus'])" -ForegroundColor Green
    Write-Host 'Now check push-diagnostics.txt on the phone for native.task.run and native.raw.received.'
}
catch {
    $r = $_.Exception.Response
    $status = if ($r) { [int]$r.StatusCode } else { 'none' }
    $description = $_.Exception.Message
    if ($r) {
        try {
            $values = $null
            if ($r.Headers -is [Net.WebHeaderCollection]) { $description = $r.Headers['X-WNS-Error-Description'] }
            elseif ($r.Headers.TryGetValues('X-WNS-Error-Description', [ref]$values)) { $description = $values -join ' ' }
        }
        catch { }
    }
    Write-Host "WNS delivery: FAILED (HTTP $status) $description" -ForegroundColor Red
    if ($status -eq 401) {
        Write-Host 'HTTP 401: the access token was rejected for this channel.'
    }
    elseif ($status -eq 403) {
        Write-Host 'HTTP 403: the channel belongs to a different package than this Package SID.'
    }
    elseif ($status -eq 404 -or $status -eq 410) {
        Write-Host 'HTTP 404/410: the channel URI is invalid or expired; reopen the app and copy it again.'
    }
    exit 1
}
