# Building the fork

One command builds everything this fork ships. It is written down because none of the
flags on it are optional and none of them are obvious.

## Before the first build

- **Visual Studio 2017 15.9** with the UWP workload, and the **Windows 10 SDK
  10.0.18362.0**. 15.9 is not a preference: the app targets `10.0.15063` as its minimum
  and later toolsets have dropped that.
- **The TDLib Extension SDK.** It is not on NuGet; it is a VSIX in `Dependencies/`, built
  from source, and it has to be unpacked into the per-user Extension SDK folder before
  anything compiles. `Dependencies/README.md` says how, and why the ARM Release build of
  it is compiled with the optimiser switched off.
- **`Unigram/Unigram/Constants.Secret.cs`**, which is gitignored and stays that way. Copy
  `Constants.Secret.example.cs` over it and fill in an api_id/api_hash of your own; the
  build does not compile without the file.
- **A signing key at `Unigram/Unigram/Unigram_DevKey.pfx`**, gitignored for the same kind
  of reason: a signing key in a repository is a key everybody has, and a package signed
  with it is one every phone that trusted an earlier build will install without asking.
  Make your own, and give it the subject the manifest already names as its publisher --
  packaging fails on a mismatch:

  ```powershell
  $cert = New-SelfSignedCertificate -Type Custom `
    -Subject "CN=DC409D7A-979D-42E5-AAA5-E9A0F674260F" `
    -KeyUsage DigitalSignature -CertStoreLocation "Cert:\CurrentUser\My" `
    -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")

  [IO.File]::WriteAllBytes("<repo>\Unigram\Unigram\Unigram_DevKey.pfx", $cert.Export("Pfx"))
  ```

  `Export-PfxCertificate` is the usual way to write one out and is no use here: it insists
  on a password or a DPAPI protector, and MSBuild wants a file that opens without either.
  Exporting through .NET gives a passwordless one.

  To use a subject of your own instead, change `Publisher` on the `<Identity>` element in
  `Package.appxmanifest` to match it. `UpdateManifest.ps1` rewrites the name and the
  version on every build but leaves the publisher alone, so that edit survives.

## The command

Run it from a PowerShell prompt, with an absolute path to the solution:

```powershell
& "C:\Program Files (x86)\Microsoft Visual Studio\2017\Community\MSBuild\15.0\Bin\MSBuild.exe" `
  "<repo>\Unigram\UnigramMobile.sln" `
  /p:Configuration=Release /p:Platform=ARM `
  /p:UseDotNetNativeToolchain=false `
  /p:UapAppxPackageBuildMode=SideloadOnly `
  /p:AppxBundle=Always `
  /m /v:minimal /nologo
```

- `UseDotNetNativeToolchain=false` keeps the Release build off the .NET Native compiler
  and on the same IL path the Debug build uses. It is what the phone has been running all
  along, and it turns a very long compile into a merely long one.
- `UapAppxPackageBuildMode=SideloadOnly` and `AppxBundle=Always` produce
  `Unigram\Unigram\AppPackages\Unigram_<version>_Test\Unigram_<version>_arm.appxbundle`,
  which is exactly what `tools\deploy-lumia.ps1` looks for.
- `Platform=ARM` is the only platform the phone can run. x86/x64 exist for the desktop and
  are not what this fork is for.

## The version number

`UpdateManifest.ps1` runs before every build and stamps the manifest with
`yy.M.<git rev-list --count HEAD>.0`. Two things follow from that, and both have cost time:

- The version is **branch-relative**. A branch with fewer commits than the one already on
  the phone produces a *lower* version, and the Device Portal will not install it over the
  top. Squashing lowers it too.
- The manifest is **generated output**. A build always leaves it modified; discard that
  before committing. The file says so itself, at the top.

## Getting it onto the phone

`tools\deploy-lumia.ps1`. It installs over the top rather than uninstalling first, on
purpose: an uninstall takes the TDLib database and the signed-in session with it.
