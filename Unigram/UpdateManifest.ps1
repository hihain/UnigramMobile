param (
  [string]$path = $(throw "-path is required"),
  [string]$config = "DEBUG",
  [string]$experimental = "false"
)

Write-Output "Config: $config"
Write-Output "Path: $path"

$path = Resolve-Path $path
$path_manifest = "${path}\Package.appxmanifest"

$config = $config.ToUpper()
$useExperimental = $experimental -match '^(?i:true|1|yes)$'

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
if ($useExperimental) {
    $h["DEBUG"] = "49197Wirdschon.UnigramMobileTdlibExperimental"
    $h["RELEASE"] = "49197Wirdschon.UnigramMobileTdlibExperimental"
} else {
    $h["DEBUG"] = "49197Wirdschon.UnigramMobileExperimental"
    $h["RELEASE"] = "49197Wirdschon.UnigramMobile"
}

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
if ($useExperimental) {
    $h["DEBUG"] = "Unigram Mobile"
    # Properties.DisplayName must match a localized AppName resource; keep the
    # experimental label on uap:VisualElements below.
    $h["RELEASE"] = "Unigram Mobile"
} else {
    $h["DEBUG"] = "Unigram Mobile Experimental"
    $h["RELEASE"] = "Unigram"
}

$properties = $document.GetElementsByTagName("Properties")[0]
$displayName = $properties.SelectSingleNode("*[local-name()='DisplayName']")
$displayName.InnerText = $h[$config]

$h = @{}
if ($useExperimental) {
    $h["DEBUG"] = "Unigram Mobile TDLib Experimental"
    $h["RELEASE"] = "Unigram Mobile TDLib Experimental"
} else {
    $h["DEBUG"] = "Unigram Mobile Experimental"
    $h["RELEASE"] = "Unigram"
}

$visualElements = $document.GetElementsByTagName("uap:VisualElements")[0]
$visualElements.Attributes["DisplayName"].Value = $h[$config]

$document.Save($path_manifest)