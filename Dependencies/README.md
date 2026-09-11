# Dependencies

## The TDLib Extension SDK

The app references TDLib as a UWP Extension SDK -- `Telegram.Td.UWP`, an `SDKReference` in
`Unigram/Unigram/Unigram.csproj`. It is not on NuGet and it is not in this repository: the
package is about 240 MB, which is not something to put in somebody's git history. It hangs
off a release on the fork this branch comes from, and this says how it was made, in enough
detail to make it again rather than take the one on offer on trust.

<https://github.com/ahorn42/UnigramMobile/releases/download/tdlib-sdk-1.8.24/Telegram.Td.UWP-1.8.24.vsix>

Nothing in the build reads this folder. MSBuild resolves the SDK from the per-user
Extension SDK location, so the VSIX only has to be unpacked once per machine.

### Installing it

A VSIX is a zip. Unpack it into the per-user Extension SDK folder:

```
%LOCALAPPDATA%\Microsoft SDKs\Windows Kits\10\ExtensionSDKs\Telegram.Td.UWP\1.0\
```

so that `SDKManifest.xml`, `References\`, `Redist\` and `DesignTime\` sit directly in the
`1.0` folder. MSBuild resolves per-user Extension SDKs on its own, and this way needs no
administrator -- running VSIXInstaller does, because the VSIX declares `Scope="Global"`.

**Every TDLib version installs as `Telegram.Td.UWP` version `1.0`.** Nothing distinguishes
them, so only one can be present at a time and no part of the build will tell you which one
you have. A mismatch shows up as a wall of compile errors about types that do not exist.

### TDLib 1.8.24 -- the one the app needs

- Source: tdlib/td commit `c5c55092d` (= v1.8.24, 2024-01-27), the last release that still
  builds with the C++14 toolset VS2017 has.
- Toolchain: VS2017 15.9 (v141), Windows SDK 10.0.18362.0, CMake, gperf.
- Built with `td\example\uwp\build.ps1`, which drives vcpkg and exports the VSIX. It does
  **not** check compiler exit codes, so verify that all six `Telegram.Td.dll` / `.winmd`
  outputs exist before believing it.
- Contains all six arch/config combinations (x86/x64/ARM x Debug/Release) plus DesignTime
  metadata. The `Redist\Debug\ARM` slot deliberately holds the same Release DLL as
  `Redist\Retail\ARM`: a real ARM Debug build was never needed, and the release CRT
  dependencies are what keep Debug app builds deployable without sideloading a debug CRT.

### ARM Release is built with the optimiser off

This is not a preference and it is the thing most likely to be undone by accident.

The VS2017 15.9 ARM optimiser produces a broken `Telegram.Td.dll` at both `/O1` and `/O2`:
about a second after the TDLib client starts, the app dies of heap corruption inside
coreclr. Reproducibly, on Windows 10 Mobile 15254. `/Od` with the release CRT, `NDEBUG` and
retail OpenSSL/zlib works. Bisected in August 2026 with OpenSSL, zlib, the app IL, the CRT
flavour and the `NDEBUG` code paths each ruled out on their own.

```
cmake -DCMAKE_CXX_FLAGS_RELEASE="/MD /Od /DNDEBUG" -DCMAKE_C_FLAGS_RELEASE="/MD /Od /DNDEBUG" ...
```

x86 and x64 are built with stock optimisation; the fault is ARM-specific. The ARM package
is larger and slower to link, and neither has been a problem in practice.

### TDLib 1.8.1 -- notes only

Rebuilt in August 2026 because the original artifact was lost with the `tdlib` branch. The
app moved to 1.8.24 and no longer builds against it, so it is not published anywhere; these
notes are here in case it is ever needed again:

- Source: tdlib/td commit `92c2a9c4e521df720abeaa9872e1c2b797d5c93f` (= v1.8.1)
- Dependencies via vcpkg tag `2022.02.23`: openssl 1.1.1m and zlib 1.2.11 for arm-uwp,
  x64-uwp and x86-uwp. Both portfiles need their pkg-config fixup steps removed -- those
  download from msys2 mirrors that are long dead, and TDLib uses `find_package` anyway.
- CMake 4.x refuses td 1.8.1's `cmake_minimum_required(3.0.2)`; set
  `CMAKE_POLICY_VERSION_MINIMUM=3.5` in the environment before building.
- The ARM optimiser fault above reproduces here too.
