# Dependencies

## Where the Extension SDK comes from

It is not in this repository. It is about 470 MB for the two of them, which is not
something to put in somebody's git history, so they are attached to a release instead
and this says how they were made -- with enough detail to make them again rather than
trust the ones on offer.

Built from tdlib/td, checked out at the tag for the version in question, with
`example/uwp/build.ps1`. That script drives vcpkg and produces the VSIX; the arguments
that matter are the architecture (`arm` is the one this fork needs) and the
configuration. The VSIX identity is `Telegram.Td.UWP` version `1.0` for every TDLib
version, so **only one can be installed at a time** and nothing in the build will tell
you which one you have -- a mismatch shows up as a wall of compile errors about types
that do not exist.

The ARM Release build is compiled with the optimiser off (`/Od`). With it on, VS2017's
C++ compiler takes hours on td's generated sources and, on this codebase, produced a
binary that faulted on startup on 15063. The package is bigger and slower to link;
neither has been a problem in practice.

Installing it means unpacking the VSIX into the per-user Extension SDK folder:

    %LOCALAPPDATA%\Microsoft SDKs\Windows Kits\10\ExtensionSDKs\Telegram.Td.UWP\1.0

A VSIX is a zip. Unpacking it there works and needs no administrator, which running the
installer does.

## Telegram.Td.UWP-1.8.24.vsix (Git LFS) — CURRENT

TDLib 1.8.24 built from source as UWP Extension SDK, same identity `Telegram.Td.UWP,
Version=1.0` and same layout as the 1.8.1 package below, so it installs to the same
per-user location. The app code requires this version since the 1.8.1→1.8.24 port.

- Source: tdlib/td commit `c5c55092d` (= v1.8.24, 2024-01-27; still C++14/VS2017-buildable)
- Same toolchain/vcpkg deps and the same **mandatory `/Od` for ARM Release** as described
  below — the VS2017 ARM optimizer bug reproduces with 1.8.24 as well.
- x86/x64 configs are built with stock optimization (`/O2`); the bug is ARM-specific.
- The `Redist\Debug\ARM` slot intentionally contains the same `/Od` *Release* DLL as
  `Redist\Retail\ARM` (a genuine ARM Debug build was never needed; the release CRT
  dependencies keep Debug app builds deployable without debug-CRT sideloading).

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
