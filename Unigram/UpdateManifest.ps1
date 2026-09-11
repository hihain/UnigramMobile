param (
  [string]$path = $(throw "-path is required"),
  [string]$config = "DEBUG"
)

# This runs as a pre-build event, where nothing reads what it prints. Without this, a
# script that falls over halfway leaves the manifest holding the version of whatever
# commit it was last saved on, the build carries on and packages it, and the first sign
# of trouble is a phone refusing the install. The two exits below are the exception:
# no git and no commit count are the source-without-history case, and that one is
# allowed to build.
$ErrorActionPreference = "Stop"

Write-Output "Config: $config"
Write-Output "Path: $path"

$path = Resolve-Path $path
$path_manifest = "${path}\Package.appxmanifest"

$config = $config.ToUpper()

try {
    $out = Invoke-Command -ScriptBlock {git -C $path rev-list --count HEAD}
} catch {
    exit
}

Write-Host "Git rev-list: $out"

$rtn = 0
if ([double]::TryParse($out, [ref]$rtn) -ne $true) {
    exit
}

[xml]$document = Get-Content $path_manifest

$h = @{}
$h["DEBUG"] = "49197Wirdschon.UnigramMobileExperimental"
$h["RELEASE"] = "49197Wirdschon.UnigramMobile"

$identity = $document.GetElementsByTagName("Identity")[0]
$original1 = $identity.Attributes["Name"].Value
$original2 = $identity.Attributes["Version"].Value

$identity.Attributes["Name"].Value = $h[$config]

$version = $identity.Attributes["Version"].Value;
$regex = [regex]'(?:(\d+)\.)(?:(\d+)\.)(?:(\d*?)\.\d+)'
$date = Get-Date -Format yy.M

$identity.Attributes["Version"].Value = -join($date, $regex.Replace($version, '.{0}.0' -f $out))

if ($original1 -eq $identity.Attributes["Name"].Value -and $original2 -eq $identity.Attributes["Version"].Value) {
    exit
}

$h = @{}
$h["DEBUG"] = "Unigram Mobile Experimental"
$h["RELEASE"] = "Unigram Mobile Messenger"

$properties = $document.GetElementsByTagName("Properties")[0]
$displayName = $properties.GetElementsByTagName("DisplayName")[0]
$displayName.InnerText = $h[$config]

$h = @{}
$h["DEBUG"] = "Unigram Mobile Experimental"
$h["RELEASE"] = "Unigram Mobile Messenger"

$visualElements = $document.GetElementsByTagName("uap:VisualElements")[0]
$visualElements.Attributes["DisplayName"].Value = $h[$config]

# On Windows 10 Mobile the PhoneProductId is the effective app identity; without a
# distinct GUID per config, Debug and Release refuse to install side by side there.
$h = @{}
$h["DEBUG"] = "502422b9-37d8-485f-9fc7-6a5e1211cb72"
$h["RELEASE"] = "bf16cc61-265a-4c8a-9ead-f4f77e6a0a97"

$phoneIdentity = $document.GetElementsByTagName("mp:PhoneIdentity")[0]
$phoneIdentity.Attributes["PhoneProductId"].Value = $h[$config]

$document.Save($path_manifest)