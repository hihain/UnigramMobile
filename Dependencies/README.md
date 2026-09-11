# Dependencies

## Telegram.Td.UWP-1.8.1.vsix (Git LFS)

TDLib 1.8.1 built from source as UWP Extension SDK, identity `Telegram.Td.UWP, Version=1.0`
(referenced by `Unigram/Unigram/Unigram.csproj` as an SDKReference). Contains all six
arch/config combinations (x86/x64/ARM × Debug/Release) plus DesignTime metadata.

Rebuilt 2026-08 because the original build artifact (former `tdlib` branch) was lost.

**IMPORTANT — ARM Release is built with `/Od` (optimizer disabled).** The VS2017 15.9 ARM
optimizer produces a broken `Telegram.Td.dll` at both `/O1` and `/O2`: the app crashes with
heap corruption (access violation inside coreclr) about one second after the TDLib client
starts, reproducibly, on Windows 10 Mobile 15254. `/Od` with release CRT, `NDEBUG` and
retail OpenSSL/zlib works. Bisected 2026-08-29 (OpenSSL, zlib, app IL, CRT flavor and
NDEBUG code paths were all ruled out as single variables). When rebuilding the ARM Release
config, configure with:

```
cmake -DCMAKE_CXX_FLAGS_RELEASE="/MD /Od /DNDEBUG" -DCMAKE_C_FLAGS_RELEASE="/MD /Od /DNDEBUG" ...
```

### Install (no admin required)

Instead of running VSIXInstaller (which needs admin for `Scope="Global"`), extract the
VSIX (it is a zip) and copy its SDK payload to the per-user Extension SDK location:

```
%LOCALAPPDATA%\Microsoft SDKs\Windows Kits\10\ExtensionSDKs\Telegram.Td.UWP\1.0\
```

so that `SDKManifest.xml`, `References\`, `Redist\` and `DesignTime\` sit directly in
the `1.0` folder. MSBuild resolves per-user Extension SDKs automatically.

### How it was built

- Source: tdlib/td commit `92c2a9c4e521df720abeaa9872e1c2b797d5c93f` (= v1.8.1)
- Toolchain: VS2017 15.9 (v141), Windows SDK 10.0.18362.0, CMake 4.x, gperf
- Deps via vcpkg tag `2022.02.23`: openssl 1.1.1m + zlib 1.2.11 for arm-uwp/x64-uwp/x86-uwp
  (openssl/zlib portfiles need their pkg-config fixup steps removed — those download from
  msys2 mirrors that are long dead; TDLib uses find_package, not pkg-config)
- CMake 4.x refuses td 1.8.1's `cmake_minimum_required(3.0.2)` — set env
  `CMAKE_POLICY_VERSION_MINIMUM=3.5` before building
- Build: `td\example\uwp\build.ps1` (phases prepare/config/build/export; note it does not
  check compiler exit codes — verify the six `Telegram.Td.dll`/`.winmd` outputs exist)
