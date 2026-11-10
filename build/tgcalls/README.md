# TgCalls ARM UWP proof

This directory contains the reproducible native-source proof for the
experimental ARM UWP private-call transport. It does not package a TgCalls
binary, change `CallProtocol`, or replace the proven legacy `libtgvoip`
fallback.

## Video renderer status

The experimental ARM bridge now negotiates V2 video, captures and sends H.264
from the Lumia, and applies camera-orientation metadata. The CPU preview
prototype is deliberately disabled:

- It converted each native I420 frame to BGRA, copied it through a WinRT
  `IBuffer`, and rendered it into a XAML `WriteableBitmap`.
- Device diagnostics show that attaching just the *local* preview output,
  with no remote output attached, consistently ends an otherwise established
  video call with `0xC0000005` (`access=write;null_page=1`).
- Calls with both preview outputs isolated remain established and remote peers
  receive the Lumia camera, so signaling, capture, H.264 encoding, and video
  transport are not the failing boundary.

The CPU path has now been replaced by a native composition-surface proof:

- `CompositionVideoOutput` is a native `rtc::VideoSinkInterface` that retains
  one latest `webrtc::VideoFrame`, renders it on a dedicated native thread,
  and never projects its pixels through C++/CX or C#.
- It creates an ARM hardware D3D11/D2D device and a
  `CompositionDrawingSurface` for the page-owned `SpriteVisual`. It performs
  the interim I420-to-ARGB conversion with libyuv in native code, then draws
  the bitmap with native rotation and local-camera mirroring.
- The local and remote outputs are independent native sinks. They were enabled
  in separate physical-device milestones so a failure could be attributed to
  one output rather than hidden by a broad renderer change.

This is a memory-lifetime proof, not the final renderer optimization. The
planned follow-up is the upstream-style D2D YUV420 pixel shader and
deterministic `i420.bin` packaging, which removes the native ARGB conversion
and copy. Do not enable the remote sink or claim full preview support before
the local-only device test succeeds.

### Local-preview device acceptance

Test a signed experimental APPX with `UseModernTgCalls=true` on a Lumia after
installing only its matching ARM dependencies. The experimental package
identity is intentionally distinct from the stable app, so it creates an
isolated LocalState/database. Do not install it over the stable identity.

Make one outgoing or incoming video call to a peer that can receive H.264
video. The acceptance result is:

1. the call reaches `Established` and keeps audio working;
2. the local Lumia preview is visible and remains stable for at least one
   camera switch, hangup, and retry;
3. the peer still receives the Lumia camera stream with the expected
   orientation; and
4. the app does not terminate with `0xC0000005` during or after output
   attachment.

The local-only milestone passed on `26.9.6191.0`: the preview was centered,
the native output attached, and several calls stopped cleanly without a new
access violation. The next package enables the independent remote native sink
alongside the validated local sink. It must be treated as a separate device
gate: verify incoming peer video appears in the main call area while the local
preview remains stable, then exercise hangup and retry before considering both
outputs validated.

The remote-preview gate passed for Windows 10 Mobile, Android, and iOS video
presentation. An earlier iOS report identified delayed incoming video,
feedback, and an app exit after ending a call. The `26.9.6193.0` follow-up
preserved both native outputs but made two narrow lifecycle/performance
corrections:

- it joins and releases both native video sinks before clearing their XAML
  composition hosts, removing the race between a renderer thread and UI visual
  detachment during hangup; and
- it reuses the renderer's native BGRA buffer instead of allocating one for
  every I420 frame, reducing 32-bit ARM heap pressure and avoidable render
  backlog.

The added diagnostics mark the beginning and completion of native output
teardown. The `26.9.6193.0` device test attached both outputs in four calls,
recorded four ordered teardown pairs and completed stops, and reported no
renderer failure, access violation, or app exit. It confirms the native
preview lifecycle is stable.

The test did observe iOS temporarily report remote video inactive and then
active again after loud feedback. This corresponds to a peer-media
renegotiation rather than a native output failure: both output lifecycles and
teardowns completed normally. Treat it as a remaining cross-device
audio/video-quality risk, not a reason to reintroduce the removed CPU preview
path or change codec/AEC settings without a reproducible audio-device trace.

Collect only the privacy-safe call diagnostics: they may contain fixed
result/state tokens and HRESULTs, but must not contain credentials, signaling,
phone numbers, user IDs, message content, tokens, or paths.

The working upstream Unigram renderer is the implementation reference. It
keeps `webrtc::VideoFrame` objects entirely native: a
`rtc::VideoSinkInterface` coalesces frames on a per-sink render queue, uploads
I420 planes to Direct2D bitmaps, and draws them to a
`CompositionDrawingSurface` with a D2D pixel shader. Managed code owns only
the `SpriteVisual` host and frame-size/state notifications; it never receives
pixel buffers.

That source cannot be copied as a component: its current project is C++/WinRT,
ARM64/x64-only, has a 18362 minimum, and includes unrelated desktop/FFmpeg
dependencies. The required composition interop
(`ICompositorInterop` and `ICompositionDrawingSurfaceInterop`) is present in
the 15063 SDK, and this app already links ARM D2D/D3D11 in
`Unigram.Native.Media`. The next renderer work must therefore be a small
C++/CX adaptation in the existing calls bridge:

1. create a native composition-surface sink from a page-owned `SpriteVisual`;
2. attach/detach it directly as TgCalls' local or incoming output, retaining
   exactly one strong sink owner and passing `nullptr` on page/session teardown;
3. coalesce and render I420 frames natively, including rotation, mirroring,
   and device-replacement handling;
4. generate and package the upstream-style `i420.bin` shader deterministically;
5. prove native ARM linkage before one local-only device test, then test the
   remote sink independently.

## Source pin

The source checkout is intentionally external to this repository:

```text
%LOCALAPPDATA%\UnigramTdlibExperiment\tgcalls
```

It is pinned to:

```text
TelegramMessenger/tgcalls@1c236c09f8d8569fead14bd68000618a52051225
```

That revision's `InstanceImpl` registers the actual current private-call
versions `2.7.7` (protocol V0) and `5.0.0` (protocol V1). Those values must
not be advertised by the experimental app until a matching native transport
has been built and packaged.

## Reproducible probe

From the app repository, run:

```powershell
.\build\tgcalls\Test-TgCallsUwpArm.ps1
```

The probe initializes the installed VS 2017 v141 ARM compiler environment and
compiles `tgcalls\Instance.cpp`, then attempts the real V0/V1
`tgcalls\InstanceImpl.cpp` unit. It verifies the exact source commit before
compiling and removes its temporary object files.

At the pinned revision, metadata compilation succeeds, while the private-call
engine stops at:

```text
fatal error C1083: Cannot open include file: 'rtc_base/logging.h'
```

This is expected: the upstream checkout contains neither WebRTC headers nor
ARM UWP WebRTC libraries, and it has no UWP CMake or Visual Studio project.
The `tgcalls\platform\uwp` sources are not a build system; their capture code
also belongs to the deferred video scope.

The installed vcpkg revision (`98d7cb0cf1f4686a3e43aa5672b6230c1d56bce8`)
rejects `webrtc:arm-uwp` during its dry-run dependency plan because the port
supports desktop Windows, not UWP. The current `UnigramDev/Unigram`
`Telegram.Native.Calls` component confirms the required modern bridge exists,
but its dedicated UWP WebRTC port deliberately supports only x64 and ARM64.
Neither route supplies an ARM Windows Mobile library.

After supplying a source-compatible WebRTC checkout, repeat the probe with:

```powershell
.\build\tgcalls\Test-TgCallsUwpArm.ps1 -WebRtcRoot C:\path\to\webrtc\src
```

The default probe deliberately uses the app's v141 ARM compiler. The pinned
M123 Abseil source rejects Visual Studio 2017, so that check currently proves
that the modern engine cannot be built in the app's existing native toolset.
It must not be bypassed by editing the upstream version check. To evaluate a
separate VS 2022 bridge toolset after the WebRTC Release ARM build exists,
pass its environment script and host/target pair explicitly:

```powershell
.\build\tgcalls\Test-TgCallsUwpArm.ps1 `
    -WebRtcRoot C:\wrtcar\src `
    -VcVarsAllPath 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvarsall.bat' `
    -VcVarsArchitecture x64_arm
```

With `-WebRtcRoot`, the probe adds the pinned Abseil and generated-output
include roots plus the UWP WebRTC platform definitions from the ARM GN build.
This only validates C++ source compilation; a separate C++/CX WinMD project
must still prove toolset/link/runtime compatibility before integration.

M123 adds string device-selection members to `AudioDeviceModule`. The pinned
TgCalls wrapper already delegates these members but incorrectly limits that
delegation to its desktop UWP macro. The probe applies
`patches\tgcalls-m123-winuwp-audio-device.patch` to expose the same existing
delegation on UWP ARM; it neither changes device selection semantics nor adds
a fallback.

The required WebRTC build must target Windows Store ARM with the app's
Windows 10 SDK 18362 constraints and provide the matching headers, generated
configuration, static libraries, and UWP-compatible audio/network
implementations. It must be built or ported from source; headers alone do not
constitute a transport proof.

## Integration gate

Only after the probe and a linked ARM C++/CX WinMD proof pass may a new
component be introduced. It must expose audio-only lifecycle, encryption,
reflector/WebRTC server configuration, signaling, routing, state callbacks,
and a version list; `CallsService` must select it only for the versions it
actually implements. The existing ARM `libtgvoip` wrapper stays in place as
the validated W10M legacy fallback. Video remains out of scope.

TgCalls is LGPL-3.0. Before distributing any resulting APPX, preserve the
upstream license and source pin, publish required notices and corresponding
source for modifications, and complete a licensing review of the component
linkage and redistribution terms.

## ARM WebRTC source build

`UnigramDev/webrtc-uwp` carries the needed `winuwp` platform changes, but its
published archives intentionally omit 32-bit ARM. The external
`UnigramDev/deps@6ce0019e1e5ea4e06ab3bc21651242567774a4e0` build script
pins the UWP fork at
`801b01361857fd9afd40f0efd29034b0e48001c7` and its WebRTC source branch at
`branch-heads/6312`.

Run the experimental wrapper with VS 2022 ARM C++ Build Tools installed:

```powershell
.\build\tgcalls\Build-WebRtcUwpArm.ps1
```

It verifies the external build-script revision, selects a Visual Studio
instance with the ARM workload, and injects `arm` into the script's published
x64/ARM64 architecture allow-list only in a temporary copy. The external
script and its pinned upstream source remain unmodified. The default build
root is deliberately short (`C:\wrtcar`): Chromium's third-party checkout
contains paths that exceed Windows' legacy limit under the longer
`%LOCALAPPDATA%` location. It downloads `depot_tools`, synchronizes
approximately 20 GB of WebRTC dependencies, then generates
`target_os="winuwp"` and `target_cpu="arm"` before building `webrtc.lib`.
This operation can take several hours.

Chromium M123's generic Rust configuration rejects Windows ARM before GN sees
that WebRTC's standalone build defaults Rust off. The wrapper explicitly
passes `enable_rust=false` and applies the small, guarded
`patches\webrtc-m123-winuwp-arm-no-rust.patch`: it permits ARM only when Rust
is disabled and continues to reject it if a dependency enables Rust. This does
not disable SCTP, WebRTC signaling, or any C++ audio transport component.

The corresponding Chromium runtime-DLL staging helper has no ARM case even
though the UWP compiler toolchain does. The wrapper applies
`patches\webrtc-m123-winuwp-arm-skip-runtime-copy.patch` to bypass that
desktop-oriented copy only for the static UWP ARM library; the ARM compiler
environment still comes from `setup_toolchain.py`.

MSVC cannot assemble the fork's GNU ARMv7/NEON routines or compile their
GCC-style inline assembly. For this proof only, the wrapper selects WebRTC's
existing scalar paths with `arm_version=6 arm_use_neon=false` and applies
`patches\webrtc-m123-winuwp-arm-scalar-audio.patch`. The scalar PFFFT
configuration also reveals an upstream GN list-assignment error; the wrapper
applies `patches\webrtc-m123-winuwp-arm-scalar-pffft.patch` to preserve the
Windows math-constant define while adding `PFFFT_SIMD_DISABLE`. These changes
control WebRTC optimization feature selection only; they do not change the
`target_cpu` (`arm`), UWP architecture, audio codec support, SCTP, signaling,
or the eventual APPX architecture. They trade performance for a correctly
compiled native ARM proof and require audio performance/device validation
before distribution.

Opus also enables an ARM runtime-dispatched GNU assembly routine whenever its
target is Windows ARM. `patches\webrtc-m123-winuwp-arm-scalar-opus.patch`
disables that assembly-only selection for UWP ARM, retaining Opus's existing
portable C implementation in `pitch.c`. This is the same scalar-proof
constraint as the common-audio changes, not a change to Opus support or media
encryption.

The scalar configuration retains `WEBRTC_ARCH_ARM` for generic ARM behavior,
but MSVC cannot compile the GNU extended assembly used only to control the
floating-point denormal mode. The wrapper therefore applies
`patches\webrtc-m123-winuwp-arm-msvc-denormal.patch`, which leaves denormal
control unsupported for 32-bit MSVC ARM and uses WebRTC's existing no-op
fallback. It does not affect media encryption, audio codecs, signaling, or
call lifecycle.

The same compiler limitation reaches BoringSSL's optimized GAS sources.
`patches\webrtc-m123-winuwp-arm-boringssl-no-asm.patch` selects BoringSSL's
existing `OPENSSL_NO_ASM` configuration for UWP ARM, matching its established
ARM64/sanitizer fallback. The portable C cryptographic implementation remains
in use; this is a performance tradeoff that needs device validation, not a
substitute or a reduction in encryption behavior.

The proof intentionally excludes AV1 and libvpx by passing
`enable_libaom=false rtc_build_libvpx=false rtc_libvpx_build_vp9=false`.
`patches\webrtc-m123-winuwp-arm-audio-only-libaom.patch` makes the standalone
root target honor those existing configuration options instead of
unconditionally including the AV1, VP8, and VP9 factory adapters.
`patches\webrtc-m123-winuwp-arm-audio-only-libvpx.patch` preserves the public
VP8/VP9 target labels for dependency-graph compatibility but omits their
libvpx-dependent implementation sources whenever `rtc_build_libvpx=false`.
The fork does not supply the required ARM-generated libvpx headers
(`vpx_config.h` and `vpx/vp8*.h`), so this is an explicit audio-only build,
not a codec fallback. AV1, VP8, VP9, and all broader video-call work remain
deferred.

The generated library is only a native proof input. It must still compile and
link with the audio-only C++/CX TgCalls bridge, activate from its WinMD, and
complete device interoperability before it can replace any packaged transport.

## Linked ARM WinMD bridge proof

`Build-ModernCallsBridgeProof.ps1` now builds the complete Release|ARM
linkage proof:

```powershell
.\build\tgcalls\Build-ModernCallsBridgeProof.ps1
```

It first builds the pinned TgCalls core as a native v143 C++20 static library,
then links that library and the generated ARM `webrtc.lib` into a separate
v143 C++/CX UWP Runtime Component. This split is required because modern
WebRTC headers use `generic`, which is reserved by C++/CX and therefore cannot
be compiled under `/ZW`.

The external proof output root is:

```text
%LOCALAPPDATA%\UnigramTdlibExperiment\bridge-probe
```

Successful builds produce:

```text
ModernCallsBridge.dll
Unigram.Native.Calls.Proof.winmd
Unigram.Native.Calls.Proof.pri
native-engine\TgCallsEngine.lib
```

`ModernCallsBridge.dll` is an ARM AppContainer DLL. `Diagnostics::GetBuildInfo()`
registers `InstanceImpl` with TgCalls' `Meta` registry, then obtains the first
supported version through that actual native-engine path; it is not a
hard-coded version string.

The proof now also exports an audio-only lifecycle surface:

- `AudioCallConfiguration` carries the registered protocol version, timeouts,
  P2P/TCP settings, API layer, direction, network type, 256-byte encryption
  key, reflector endpoints, and STUN/TURN server entries.
- `AudioCallSession` is configured before it starts the native instance.
  The app subscribes to state/signaling/stop events, assigns session ownership,
  then explicitly calls `Start()` so the first synchronous offer or answer
  cannot be lost. It accepts encrypted signaling, controls mute and network
  type, asynchronously stops, and reports native state, emitted signaling, and
  final-stop events.
- The bridge rejects missing versions, missing endpoint/server data, non-UTF-16
  strings, non-256-byte keys, and reflector peer tags other than 16 bytes.
  It neither logs nor persists keys, server credentials, or signaling.
- The C++/CX owner is weakly referenced from native callbacks and has
  deterministic disposal, so callback ownership cannot retain a stopped call
  session.

The bridge is included only in opt-in experimental APPXs, where
`CallsService` selects it after modern protocol-version negotiation. It has
not yet been device-tested with Android/iOS. The default build remains
`libtgvoip`-only, and negotiated modern calls never silently fall back to the
legacy transport after setup fails.

Current verified output hashes from the external proof root:

```text
573C65F6D07F933306E1F1DCC7BB6423550C28DA375BAF767BCF0D23549C247B  ModernCallsBridge.dll
54505BA880224F24E1BB9229C69E4AB5B534B6FD4847DCE9EF5A8CBE26B9224C  Unigram.Native.Calls.Proof.winmd
5829394098835DEA8A09811A8DECE1171B348301E426E9B83ACD386CB3E1AF38  Unigram.Native.Calls.Proof.pri
3E48E7CF39E911B7B25C73454C8277C2857866219D36E2FE0C70DA066BDD8814  native-engine\TgCallsEngine.lib
```

The engine's `AudioOnlyPlatform.cpp` deliberately reports empty video encoder
and decoder factory formats, reports no video encoding support, and exposes no
capture/source implementation. It retains Opus audio and the native
peer-connection setup required by TgCalls, while avoiding the unavailable ARM
libvpx codecs and Windows Mobile video-capture surface. Video remains
explicitly unsupported.

The source closure includes TgCalls' reflector relay, reflector port, and raw
TCP port implementations. It also requires
`webrtc-m123-winuwp-arm-boringssl-tls.patch`: BoringSSL's Windows code emits
x86-decorated linker `/INCLUDE` names for non-x64 architectures, whereas ARM
uses undecorated symbols. The wrapper applies the narrow ARM directive fix,
rebuilds `thread_win.obj`, and preserves BoringSSL's real thread-exit callback
and TLS directory instead of suppressing either linker dependency.

Any later app integration must package the resulting bridge conditionally,
retain `libtgvoip` for validated legacy Windows 10 Mobile calls, and must not
advertise modern TgCalls versions until device interoperability is verified.

## Experimental app integration

The experimental app project has an explicit `UseModernTgCalls` build property.
It defaults to `false`, so normal experimental ARM packages continue to
advertise the legacy empty call-library list and use only `libtgvoip`.

Set it only for the dedicated interoperability package:

```powershell
$env:SolutionDir = "$PWD\Unigram\"
& 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe' `
    .\Unigram\Unigram\Unigram.csproj `
    /m /t:Build /p:Configuration=Release /p:Platform=ARM `
    /p:UseModernTgCalls=true /p:GenerateAppxPackageOnBuild=true
```

When enabled, the project rebuilds the external bridge proof, requires its
ARM WinMD/DLL/PRI output, references it as a runtime component, and packages:

```text
Unigram.Native.Calls.Proof.winmd
ModernCallsBridge.dll
Unigram.Native.Calls.Proof.pri
```

The app uses the audited version list from the pinned TgCalls source
(`2.7.7` and `5.0.0`) and advertises only those values. This avoids activating
the WebRTC bridge merely to build `CallProtocol`; bridge activation is
deferred until TDLib provides ready-state configuration. It selects the modern
transport only when the peer's `CallProtocol.LibraryVersions` has an exact
intersection with that list.
It maps Telegram reflector endpoints and TDLib WebRTC STUN/TURN server roles,
forwards `UpdateNewCallSignalingData` to the bridge, and sends emitted
signaling through `SendCallSignalingData`. If a modern version was negotiated
but server/configuration validation or bridge creation fails, the app records
a privacy-safe result and discards the call; it does not silently substitute
legacy `libtgvoip` after modern negotiation.

`CreateCall` and `AcceptCall` are created parameterlessly and populated through
property setters. Their generated modern TDLib constructors marshal a
`CallProtocol` object through a multi-argument WinMD activation factory, which
is not reliable on this C++/CX projection; `DiscardCall` uses only primitive
arguments and is not affected. The property-setter path avoids that ABI
boundary and is used for both outgoing and accepted calls.

Call actions record activation failures with privacy-safe diagnostics and show
an explicit failure dialog rather than failing silently. That makes the
remaining device-side test result observable without logging call keys,
signaling, user IDs, or message content.

Modern chat folders use the same parameterless/property-setter construction
for folder names, folder objects, and get/create/edit/delete requests. The
folder chat picker is enabled again for included and excluded chats; its
selected IDs are mapped to `ChatFolder` fields before the request is sent.

Legacy peers without a matching library version continue on the existing
`libtgvoip` route. The existing call page remains the legacy controller UI:
modern transport state updates the connecting/established tone lifecycle, but
per-device volume selection and preferred-relay reporting are not yet exposed
by the modern bridge. They are explicitly outside this audio-only proof and
must be completed before treating the opt-in package as a general release.

Current verified opt-in package output:

```text
APPX:
%LOCALAPPDATA%\UnigramTdlibExperiment\artifacts\
    Unigram_26.9.6154.0_ARM_ModernTgCalls_AudioDeviceTrace.appx

Minimal ARM sideload ZIP:
%LOCALAPPDATA%\UnigramTdlibExperiment\artifacts\
    Unigram_26.9.6154.0_ARM_ModernTgCalls_AudioDeviceTrace_Sideload.zip
APPX SHA-256:
FF3825C36D49F2C44AC2D4F80E28807895C1A2B12845688DD3184C56EA6B3322
ZIP SHA-256:
23D76538501AB4CACA9EBBBBE03D736395145E18033A3FAB3973975EE4AF348A
```

The ZIP contains the signed APPX, its public `.cer`, and only the ARM NET
Native, XAML, and VCLibs dependency APPXs. It contains no PFX, private key,
or source secret. The APPX was signature-verified and its manifest confirms
the side-by-side experimental identity
`49197Wirdschon.UnigramMobileTdlibExperimental`, version `26.9.6154.0`,
ARM architecture, and the existing native notification background entry point.

This remains a device-test package, not a released call fix. The 26.9.6148.0
trace proved that WinRT bridge activation succeeds, but the managed mapping
then rejected TDLib WebRTC servers as `invalid_webrtc_server` because their
64-bit IDs were incorrectly required to fit in a byte. TgCalls defines the
corresponding wire ID as an 8-bit value; its reference implementation
intentionally narrows TDLib's ID. That implementation also exposes Telegram
reflectors as ordered TURN servers, which the bridge had omitted.

Version 26.9.6149.0 mirrors that mapping: it narrows the WebRTC wire ID
without rejecting the server, adds reflector TURN entries using the reference
implementation's stable sorted ordinal, and accepts IPv6-only endpoints. It
records only aggregate mapped server counts before bridge creation; no
endpoint, credential, peer-tag, signaling, or key data is logged.

Version 26.9.6150.0 retains the corrected server mapping and serializes
call-window show/hide operations through the existing asynchronous mutex.
Rapid pending-call updates previously could concurrently create or dispose the
secondary compact view, producing an unhandled `NullReferenceException` before
call setup. The UI path now holds that mutex across creation, update, and
disposal; unexpected view failures are emitted as privacy-safe `voip.ui`
diagnostics with only the operation, HRESULT, and sanitized message. This does
not change TDLib call negotiation, bridge session creation, signaling, or the
legacy transport selection.

Version 26.9.6151.0 guards the call-page routing API. On the tested Windows
10 Mobile image, the Phone contract is present while
`AudioRoutingManager.GetDefault()` can return no manager. The old call-page
loaded/unloaded, route-click, and endpoint-change paths dereferenced that
unavailable manager. The page now retains a manager only when it is available,
hides the routing control otherwise, and unsubscribes from that same manager
during cleanup. The `6151` device trace proved the null reference occurred
before the call page's `Loaded` event, so this guard did not cover the actual
secondary-view fault.

Version 26.9.6152.0 also guards `TLWindowContext` title/status-bar
customization. It is instantiated while the compact secondary view is created,
before the `VoIPPage` content is loaded, and previously dereferenced
`ApplicationView.TitleBar` and `CoreApplicationViewTitleBar` unconditionally.
Those shell objects are absent in the Windows 10 Mobile compact-view context.
The call page now records ordered, privacy-safe `voip.ui` construction stages
as well. The `6151` trace reached modern bridge `Established` with
bidirectional signaling before the app restart; it did not show a native
bridge rejection.

Chat Folders is now device-verified: after registering `FoldersViewModel` and
`FolderViewModel` for modern TDLib, a folder created in the experimental app
with two group chats synchronized successfully with the regular Telegram
client.

Version 26.9.6153.0 makes the media layer observable. The `6152` trace proved
the app no longer restarts during a call and that the modern transport reaches
`Established` with bidirectional signaling, yet neither side had audio. Every
diagnostic up to that point described signaling and transport only, so an
audio-device failure and a media-routing failure were indistinguishable. The
bridge now reports four media facts to managed code:

- `voip.media result=audio_device` — whether the platform audio device module
  was created, its `Init` result, and the playout and recording device counts.
  TgCalls silently continues without audio when this module is null or fails to
  initialize, so this distinguishes an audio fault from a network fault.
- `voip.media result=audio_level` — a five-second summary of the engine's audio
  level callback: sample count, how many samples exceeded an audible threshold,
  and a quantised peak amplitude. TgCalls only runs its level timer when this
  callback is supplied, so it had never been active before. This value is
  deliberately labelled `active` and not `incoming`: the engine reports the
  larger of the local capture level and the decoded remote level, so room noise
  on the microphone alone can raise it and it cannot by itself separate the two
  directions.
- `voip.media result=remote_audio` — the peer's reported audio state.
- `voip.media result=signal_bars` — the engine's network quality estimate. This
  is derived purely from the outgoing send bandwidth estimate, so it is an
  upstream signal and is not evidence that media is being received.

Because the engine's own level callback is non-directional, each audio level
summary also carries the live state of the audio device module the bridge
supplies, as `created`, `recording`, and `playing` booleans. These are the
unambiguous answer to "the transport is established but nobody can hear
anything": `playing=0` means no playout is running regardless of what arrives
on the wire, and `recording=0` means nothing is being captured to send.

Media diagnostics are sampled for the entire duration of a call, so they draw
on a separate per-call allowance that is reset when a call is disposed. They
deliberately do not charge the shared call diagnostic budget, because a single
long call would otherwise exhaust it and silence the lifecycle diagnostics of
every later call in the same app session.

None of these carry audio content, device names, identifiers, hosts, or
credentials; only fixed keys, enum names, counts, booleans, and a quantised
amplitude. The audio device module the bridge now supplies is the same platform
default TgCalls would otherwise create for itself, so this adds visibility
without changing which device is used.

The same version records the platform description of an unhandled exception.
.NET Native discards a managed stack before it reaches the app's handler, and
the diagnostics sanitizer additionally redacted framework resource keys such as
`Arg_NullReferenceException` because they are long enough to look like opaque
tokens. Exception-shaped identifiers built only from letters and underscores
are now preserved, while anything bearing digits or hyphens still redacts. The
residual non-fatal null reference raised once per call in `6152` was therefore
unattributable and should now name its throw site.

`CallsService` also no longer swaps the two TgCalls timeouts: the connect
timeout maps to `initializationTimeout` and the packet timeout to
`receiveTimeout`, with non-zero fallbacks because the server reports both
options as zero. This is a correctness fix only. The pinned TgCalls tree never
reads either field — every implementation hard-codes a 20 second timeout — so
it cannot by itself change call behaviour and is not claimed as a call fix.

Required validation is experimental W10M to/from current Android and iOS audio
calls, including accept, outgoing signaling, mute, route changes, foreground
and background behavior, reconnect, rejection, and cleanup. Preserve the
legacy-W10M call regression test in both `UseModernTgCalls=false` and enabled
package configurations. Collect fresh privacy-safe diagnostics: a modern call
should now log `voip.ready result=creating` followed by either
`bridge_create`/`bridge_start` failure detail or the subsequent session
startup result. It should not emit an unhandled `CallStatePending`
null-reference error or restart when audio routing is unavailable; that state
is recorded as `voip.ui result=routing_unavailable` and route selection is
hidden. `6152` should log `view.title_bar result=unavailable` or complete the
`voip.ui` construction stages without crashing. `6153` should additionally log
one `voip.media result=audio_device` line per call and, once the transport is
established, recurring `voip.media result=audio_level` summaries carrying
`recording=` and `playing=` flags. This is not a claim that Android/iOS audio
is established; `6153` is a diagnostic build and is not expected to fix calls.

## 26.9.6154.0 — the crash is inside the platform audio stack

The `6153` trace from an outgoing W10M to Android call answered the question
`6153` was built to ask, and it cleared the audio device module of suspicion:

```text
voip.media|result=audio_device;created=1;init=0;playout_devices=2;recording_devices=3
voip.transport|result=state;state=Established
voip.media|result=remote_audio;state=Active
```

The module is created, initializes with code `0`, and enumerates two playout
and three recording devices, so device discovery works on Windows 10 Mobile.
The transport reaches `Established` and the peer reports its audio as active.

The decisive detail is how the log ends. `PushDiagnostics` opens, appends, and
closes the file on every single line, so the log is a flushed breadcrumb trail
and its last line is the last thing the process did. The trace stops mid-call
roughly 360 ms after `Established`, on an ordinary `voip.signaling` line, with
no `CallStateDiscarded`, no error, and no teardown. The process was killed
rather than the call being ended, which matches every earlier report that the
app closes immediately once a call is answered.

That window is exactly where `MediaManager::setIsConnected(true)` starts the
audio send and receive channels, which is what drives the audio device module
into `InitPlayout`, `StartPlayout`, `InitRecording`, and `StartRecording`. A
structured exception there terminates the process before managed code can log
anything, which is why every previous build produced a truncated trace.

`6154` instruments precisely that region. The bridge now returns a wrapper
around the platform module, derived from the upstream
`DefaultWrappedAudioDeviceModule` so that every method it does not override is
forwarded unchanged. The wrapper brackets each once-per-call lifecycle call
with a `phase=begin` and a `phase=end` breadcrumb carrying only a fixed step
name and the numeric result:

```text
voip.media|result=audio_device;step=start_playout;phase=begin
voip.media|result=audio_device;step=start_playout;phase=end;faulted=0;code=0
```

Each forwarded call also runs inside a structured exception guard. If the
platform audio stack raises an access violation, the guard reports
`faulted=1;code=-1` and returns instead of letting the process die. This names
the faulting step and, because the process survives, the remaining diagnostics
for that call are still written. A `phase=begin` with no matching `phase=end`
would instead mean the fault escaped the guard.

Catching an access violation and continuing can leave the audio stack in an
undefined state, so this is a diagnostic build only. The guard exists to
identify the faulting call, not to ship as a fix.

Because the wrapper is a forwarding subclass of the upstream wrapper rather
than a hand-written implementation of the interface, it cannot silently drop a
method. The media diagnostic allowance is raised to cover the extra breadcrumbs
for a whole call, and remains separate from the shared call budget.

The residual non-fatal `NullReferenceException` is still unattributed: .NET
Native reports both its message and its platform description as the resource
key `Arg_NullReferenceException`, with no stack and no inner exception, so the
`6153` description change could not name the throw site. It fires about 31 ms
after `CreateCall` returns and remains non-fatal.

Expected `6154` evidence from one call: a `step=`/`phase=` pair for each audio
device lifecycle call, and either a `faulted=1` line naming the faulting step,
or a complete set of `faulted=0` pairs proving the audio device stack is not
where the process dies.

## 26.9.6156.0 — the process dies inside `InitRecording`

The `6154` trace answered its question exactly. Every one of the bracketed
audio device lifecycle calls up to and including playout completed cleanly:

```text
voip.media|result=audio_device;step=init_playout;phase=end;faulted=0;code=0
voip.media|result=audio_device;step=start_playout;phase=end;faulted=0;code=0
voip.media|result=audio_device;step=init_recording;phase=begin
```

`init_recording` has a `phase=begin` and no `phase=end`, and the log stops
about 40 ms later. Playout therefore works end to end and the process dies in
the capture path. Because the structured exception guard around that call did
not report `faulted=1`, the fault did not happen on the thread that made the
call: UWP activates a capture endpoint through `ActivateAudioInterfaceAsync`,
whose completion runs on a thread pool thread that no `__except` of ours
covers.

The most likely cause is microphone consent. Searching the app shows that
nothing in the call path ever requests it — only `ChatRecordButton` does, for
voice messages, through `MediaCapture`. Playout needs no consent, which is why
it succeeds, and raw WASAPI capture activation is the first thing in a call
that does. The webrtc-uwp fork's capture activation handler does not appear to
tolerate a failed activation result.

So `6156` acquires consent before the engine is ever constructed.
`CallsService` starts an idempotent `MediaCapture` initialization as soon as a
call reaches `CallStatePending`, using `StreamingCaptureMode.Audio` and
`MediaCategory.Communications`, and disposes it immediately. The TDLib update
thread then waits up to eight seconds for that result before creating the
session. It never waits on the UI thread: `Handle(UpdateCall)` runs on TDLib's
native receive thread, so the `MediaCapture` work is marshalled to the UI
thread and awaited from the caller without any possibility of self-blocking.

If consent has not been granted the call is rejected with
`reason=microphone_unavailable` instead of proceeding into the fatal path. A
timeout is treated as a refusal for the same reason — the only case that times
out is a first-ever consent prompt still on screen — but the pending
acquisition is deliberately left in place so answering it makes the next call
work. A non-timeout failure clears the cached task so one transient error
cannot wedge every later call.

```text
voip.media|result=microphone;acquired=1
voip.media|result=microphone;acquired=0;hresult=0x80070005
```

### Reporting a fault that kills the process

Because the fault can land on a thread we do not control, `6156` also installs
a vectored exception handler in the native engine. Three details make it safe:

- It only reports while an audio device lifecycle call is in flight, tracked by
  an interlocked depth counter. A first-chance handler otherwise sees every
  exception in the process, and .NET Native raises `NullReferenceException` as
  an access violation, so an ungated handler would burn its report budget on
  benign managed exceptions and on the very access violations the sibling
  structured exception guard deliberately swallows.
- It ignores `EXCEPTION_STACK_OVERFLOW`, where running any handler on the
  remaining guard page would simply fault again.
- It always returns `EXCEPTION_CONTINUE_SEARCH`, so it changes no behaviour.

It writes to its own file, `Diagnostics\voip-fault.txt`, not to the main log. A
fault context can only append with raw file APIs, while the managed writer
tracks its own end-of-file offset and would write back over anything appended
behind its back. `CallsService` drains that file into the main log at the start
of the next call, tagging each recovered line `deferred=1`, so the user still
collects a single artifact. `CreateFileW` is unavailable in the UWP app
partition, so the handler uses `CreateFile2`; `AddVectoredExceptionHandler` is
likewise gated out of the app partition headers but the export is present and
links against the UWP import library once declared by hand.

### Artifacts

```text
%LOCALAPPDATA%\UnigramTdlibExperiment\artifacts\Unigram_26.9.6156.0_ARM_ModernTgCalls_MicAcquire.appx
  57497091 bytes  SHA-256 B089503D464760B1A56636C61C7E800CAA25A603C44440FB6FE2E4EE503BF2C5
%LOCALAPPDATA%\UnigramTdlibExperiment\artifacts\Unigram_26.9.6156.0_ARM_ModernTgCalls_MicAcquire_Sideload.zip
  64098545 bytes  SHA-256 92DDE730A150DEA4BC8200DF2E07EF31C21F117A11E53AFBE95ED58366EA4666
```

Expected evidence from one call: `result=microphone;acquired=1` followed by
`step=init_recording;phase=end`, which would confirm the diagnosis and leave
the remaining lifecycle calls visible for the first time. A clean
`acquired=0;hresult=` instead names the consent failure directly. If
`init_recording` still truncates the log, the drained `voip.fault` line on the
following call reports the exception code.

### Upstream comparison

`UnigramDev/Unigram`'s `Telegram.Native.Calls/VoipManager.cpp` was read as a
reference. It does not override `createAudioDeviceModule`, does not request
microphone consent, and builds a `tgcalls::Descriptor` and `Config` matching
ours field for field. That is a useful negative result: it rules out a
mis-integration of tgcalls, and it is consistent with the diagnosis, since raw
WASAPI capture activation does succeed unprompted on desktop UWP. Two
differences remain noted but unapplied: upstream sets `config.logPath` (which
makes tgcalls write a native log containing IP addresses, so it cannot be fed
into the shared diagnostics as-is), and its reflector `server.id` is 0-based
where ours is 1-based. The transport already reaches `Established`, so the
latter is not the cause of this crash.

## 26.9.6157.0 — microphone consent was never the problem

The `6156` trace settled it: `result=microphone;acquired=1` on every call, with
no prompt, because consent had already been granted. The crash is unchanged,
and the consent hypothesis is dead.

What `6156` did buy is the first direct evidence of the fault. The gated
vectored handler fired, and its records survived into the main log:

```text
voip.media|result=audio_device;step=init_recording;phase=begin
voip.fault|result=exception;code=0xC0000005;noncontinuable=0;deferred=1
```

An access violation, raised while an audio device lifecycle call was in flight,
not caught by the structured exception guard around that call.

That is still not conclusive, for two reasons. .NET Native raises every
ordinary null dereference as `0xC0000005`, and this app has a known benign
`NullReferenceException` that fires during the same window — so the code alone
cannot distinguish a real native fault from that. And a fatal error that is not
a structured exception, such as the `abort()` at the end of a WebRTC
`RTC_CHECK` failure, never reaches a vectored handler at all and would leave
exactly the same evidence: a log that simply stops.

`6157` closes both gaps.

### Locating the fault

The handler now reports where the fault is, not just that it happened:

```text
result=exception;code=0xC0000005;step=init_recording;same_thread=0;engine=1;offset=0x00ABCDEF;access=read;null_page=1;noncontinuable=0
```

- `step` names the lifecycle call in flight, recorded by the scope that gates
  the handler.
- `same_thread` compares the faulting thread against the thread driving the
  module, which decides whether the structured exception guard could ever have
  caught it.
- `engine` is whether the fault address lies inside this module, resolved
  against the linker-supplied `__ImageBase` and the image size read from the PE
  header — no module API is called from the fault context. `engine=1` means
  webrtc or tgcalls code; `engine=0` means the fault belongs to another module
  and is almost certainly the managed null dereference.
- `offset` is the module-relative address. It carries nothing about the install
  and resolves to a function against the build's map file, which is archived
  next to the artifacts as `ModernCallsBridge_26.9.6157.0.map` and is
  deliberately not packaged.
- `access` and `null_page` separate a null dereference from genuine memory
  corruption.

The project now links with `/MAP` so that offset is resolvable.

### Catching a termination that is not an exception

`signal(SIGABRT)`, `std::set_terminate`, `_set_purecall_handler` and
`_set_invalid_parameter_handler` each write a line to the same fault file:

```text
result=abort;step=init_recording;in_lifecycle=1
```

A WebRTC `RTC_CHECK` failure ends in `abort()` and is live in release builds,
unlike `RTC_DCHECK`. If that is what kills the process, this names it. Only a
`__fastfail` would still escape.

Sharing the push log from the diagnostics page now drains the fault file first,
so the shared file is complete without waiting for the next call to collect it.

### Artifacts

```text
%LOCALAPPDATA%\UnigramTdlibExperiment\artifacts\Unigram_26.9.6157.0_ARM_ModernTgCalls_FaultLocate.appx
  57497252 bytes  SHA-256 96C511703B84C4CA418014A9C74E4561F216E82120EAE602ED603D28BF392F13
%LOCALAPPDATA%\UnigramTdlibExperiment\artifacts\Unigram_26.9.6157.0_ARM_ModernTgCalls_FaultLocate_Sideload.zip
  64099016 bytes  SHA-256 2C78EEE43DB8619FC1C6F5CAD014A72DC7291B24A4B439DB51B24E415261B55F
%LOCALAPPDATA%\UnigramTdlibExperiment\artifacts\ModernCallsBridge_26.9.6157.0.map
  14800124 bytes  SHA-256 AEED2B0A22A59C6F4335BAF36D4A82014AB548A2E43C5BA93FF2ADD235E63D8A
```

### What the audio device module actually is

Worth recording, because it narrows where the fault can be. The pinned WebRTC
selects `AudioDeviceWindowsCore` from `win/audio_device_core_win.cc`, and that
file contains no `WINUWP`, `WINRT` or `WINAPI_FAMILY` conditionals at all: it
is stock desktop WASAPI code running in an app container. `InitRecording`
itself is wholly synchronous and starts no threads, which makes a
`same_thread=0` report significant if it appears. Its one early exit is
`InitRecordingDMO`, reached only when built-in AEC is enabled; `_dmo` is null
unless `CLSID_CWMAudioAEC` could be created, and `EnableBuiltInAEC` refuses to
enable the flag in that case, so the DMO path should be unreachable here. The
`RTC_DCHECK(_dmo)` guarding it is compiled out in release.

### Resolving a reported offset

`Resolve-FaultOffset.ps1` turns an `offset=` value from a `voip.fault` line into
the function it landed in. The offset is a relative virtual address, and a
linker map lists every function by absolute address next to the preferred load
address, so the answer is the last entry at or below the offset.

```powershell
cd build\tgcalls
.\Resolve-FaultOffset.ps1 -Offset 0x000afbe0 `
    -MapPath "$env:LOCALAPPDATA\UnigramTdlibExperiment\artifacts\ModernCallsBridge_26.9.6157.0.map"
```

```text
Function ?InitRecording@AudioDeviceWindowsCore@webrtc@@UAAHXZ
Starts   0x000AFBDC  (+0x4 into the function)
Object   webrtc:audio_device_core_win.obj
```

Use the map archived for the build that produced the log; a map from any other
build resolves to the wrong function.

## 26.9.6158.0 — the crash was managed all along

The 26.9.6157.0 run reported this, and it ends the search for a native fault:

```text
result=exception;code=0xC0000005;step=init_playout;same_thread=0;engine=0;access=read;null_page=1
result=exception;code=0xC0000005;step=init_recording;same_thread=0;engine=0;access=write;null_page=1
```

`engine=0` means the faulting address is outside `ModernCallsBridge.dll`, which
is where all of WebRTC and tgcalls live, so neither of these is a native audio
fault. `null_page=1` on both, and the first one fired while `init_playout` was
in flight — a step that went on to complete with `faulted=0;code=0`. A fault the
process survives is not the fault that kills it.

What these actually are is how .NET Native raises a `NullReferenceException`:
a hardware access violation against the null page. The main log carries the
matching `hresult=0x80004003` entries. So the crash is a managed exception, and
the reason nothing was ever recorded is that it never reached a handler:
`Application.UnhandledException` only observes the UI thread, and the log shows
no `app.unhandled` line for the fatal one, unlike the benign ones a few seconds
earlier.

That leaves exactly one mechanism. TDLib's own native callbacks were contained
long ago — `ProtoService.OnResult`, `TdHandler.OnResult` and
`TdCompletionSource.OnResult` each catch and record, precisely because a managed
exception escaping a reverse P/Invoke reaches
`RhpFailFastForPInvokeExceptionPreemp` and terminates the process with no
managed stack. The tgcalls events were the last boundary of that shape left
unguarded: `StateChanged`, `SignalingData`, `Stopped`, `AudioDeviceReport`,
`SignalBarsChanged`, `AudioLevelChanged` and `RemoteAudioStateChanged` are all
raised on native worker threads, and every one of them ran straight into
application code.

`GuardModernCallback` now wraps all seven. It contains the exception and records
`voip.callback|result=error;name=<callback>;type=<exception>`, which both stops
the fail-fast and names the offending callback on the next run. The callback
name and exception type are API surface, not user data.

Note that the truncation at `init_recording` was always a coincidence of
timing rather than a cause: the ADM call is simply the last thing written
before an unrelated thread takes the process down.

### Artifacts

```text
%LOCALAPPDATA%\UnigramTdlibExperiment\artifacts\Unigram_26.9.6158.0_ARM_ModernTgCalls_CallbackGuard.appx
  57500419 bytes  SHA-256 9ADABB9366BA071731722DC5697C3EAB3D57131E173E23D431B9CD9A4AA03E38
%LOCALAPPDATA%\UnigramTdlibExperiment\artifacts\Unigram_26.9.6158.0_ARM_ModernTgCalls_CallbackGuard_Sideload.zip
  64105253 bytes  SHA-256 4B180124F62F8A01F1829626664D8BBF88DE9E7D478FA5401614AA3B7F02A867
%LOCALAPPDATA%\UnigramTdlibExperiment\artifacts\ModernCallsBridge_26.9.6158.0.map
  14800124 bytes  SHA-256 7B20BA631CCF66A9A707D406A21D3EF774E9FCB8AD3C5DC7EFAC252DA978819E
```

### Two defects the guard exposed

Containing the callbacks made two pre-existing races matter, so both were fixed
in the same change.

`DisposeModernCall` is reached from the tgcalls worker thread through the
session's stopped event and from the TDLib update thread when the call is
discarded, and it previously null-checked `_modernController`, disposed it, and
only then cleared the field. Two unsynchronised callers could each pass that
check and dispose the same native session twice; `AudioCallSession`'s destructor
blocks in `StopCallSession` before `delete holder`, so a double close is a heap
corruption rather than a no-op. The field is now claimed with
`Interlocked.Exchange` and the call id and starting flag are cleared before
`Dispose` runs, so a throwing dispose can no longer leave a dead call installed
— which, with the exception now swallowed, would otherwise have made the service
reject every later call until the app was restarted.

The callback failure line is also recorded only once per callback name per call.
A null dereference recurs on every invocation, and audio levels arrive ten times
a second, so reporting each one would have drained the process-wide call
diagnostic budget within seconds and silenced the rest of the log on exactly the
run being read.

## 26.9.6160.0 — the second call, and who owns the microphone

The callback guard in 6158 paid off, but not in the way it was meant to. No
`voip.callback` line appeared anywhere in the device log, so the fatal fault was
never a managed exception in the seven tgcalls callbacks. What the log showed
instead settles the question the previous five builds were circling:

    19:17:06.722  init_recording;phase=begin
    19:17:06.907  init_recording;phase=end;faulted=0;code=0
    19:17:06.915  start_recording;phase=begin
    19:17:06.947  start_recording;phase=end;faulted=0;code=0

The first call of the process completed the entire audio pipeline, reached
`Established`, saw `remote_audio Active`, and ran for thirteen seconds. The audio
device module, `InitRecording`, and the WASAPI capture path are not broken, and
never were. The crash came from a *second* call, accepted twelve seconds later,
which died at `init_recording;phase=begin`. The first call had no
`CallStateDiscarded` and no `stopped` event anywhere in the log: it was never
torn down.

### Stop is asynchronous, and nothing downstream of it is synchronous either

`CallSession::Stop` called `_instance->stop(completion)` and the C++/CX
destructor then freed its holder and returned. Reading the pinned tgcalls source
shows how little of the teardown that actually performs:

* `InstanceImpl::stop` posts to the manager thread, which posts to the network
  thread, which posts back to the manager thread, which finally runs the
  completion inside `_mediaManager->perform`. It never stops the media pipeline.
* `~ThreadLocalObject` does not block. It `PostTask`s the held object's reset to
  its thread and returns, so `_instance.reset()` only *queues* `~Manager`, which
  in turn only queues `~MediaManager`.
* `~MediaManager` is the thing that does the real work: it drops the channels to
  `kNetworkDown`, calls `SetSend(false)` and `SetPlayout(false)`, and only then
  releases the audio device module.

So the capture endpoint stays owned across two further thread hops after the
destructor has returned. Windows 10 Mobile allows a single capture owner, and
the stock desktop WASAPI code has no handling for contention, so the second
call's `InitRecording` activated an endpoint the first call still held. That is
an access violation inside the audio stack, on a tgcalls thread, outside this
module — which is exactly the `engine=0`, `same_thread=0`, no-managed-frame
signature every one of these crashes has carried.

### What 6160 does

Teardown is now synchronous from the caller's point of view, but the shutdown
itself is left where tgcalls already does it correctly:

* The session retains the *wrapper* it hands to tgcalls rather than the platform
  module underneath it, and drops that reference inside the stop completion. The
  last reference is then `~MediaManager`'s, so tgcalls tears the device down in
  its own order, with the channels already stopped.
* `DiagnosticAudioDeviceModule`'s destructor terminates the platform module and
  signals the session. Because it runs only once every tgcalls owner has let go,
  it is the first provably-idle moment, and it is the honest answer to "is the
  microphone free".
* `StopCallSessionAndWait` blocks until the session has stopped *and* that signal
  has arrived, so the next call cannot start against a device the previous one
  owns.
* A stop completion that never arrives is now survivable. tgcalls drops it
  silently when the manager has already gone, which would pin the device for the
  life of the process, so a timeout forces the instance reset and waits out a
  short grace period.

An earlier revision of this fix stopped and terminated the device directly in the
stop completion. That was wrong for an instructive reason: the completion runs
*inside* a live `MediaManager::perform` callback, on the media thread, while
`webrtc::Call` and the send channel are still streaming into that device. It
would have moved the access violation from the second call's `InitRecording` to
the first call's teardown and looked like progress.

### Two lifetime bugs the blocking teardown exposed

Making the caller wait turned two latent races into near-deterministic ones.

The outcome used to be stored on the session and read back through a
`TeardownResult` property *after* `Dispose`. A C++/CX destructor is `Dispose`, and
reading a member of an instance whose destructor has run is undefined — the
handle is dangling, and a managed `catch` does not help, because C++/CX generates
no `RO_E_CLOSED` guard unless the class writes one. Worse for a diagnostic that
must never leak, a reused `String^` buffer would have put arbitrary heap bytes in
`result=`. Teardown is now an explicit `Teardown()` call that *returns* the token,
so nothing is read off a disposed instance.

The stop completion also published the stop and woke the waiter *before* raising
the `stopped` callback, so the disposing thread could destroy the owning object
while the native event was still being raised on its stack. The callback is now
raised first and the stop published after. For the same reason the managed
`stopped` handler no longer disposes the call: it runs on the completion thread,
inside the event raise, and disposal there would tear down the object mid-raise.
It does not need to any more — the device is released by the native side before
that callback runs, so disposal can wait for `CallStateDiscarded` or for the
stale-session sweep when the next call arrives.

### Reading the result

`voip.teardown` reports a fixed token and a coarse duration bucket:

    voip.teardown|result=drained;transport=modern_tgcalls;elapsed_ms=0
    voip.teardown|result=stale;transport=modern_tgcalls

`drained` means the device was confirmed destroyed before the next call could
start. `timeout` means it was not, and the next call is at risk. `stale` is the
sweep releasing a previous session that TDLib never discarded. A
`voip.transport|result=stopped` line should now appear for every call that ends,
which it never did before.

### Correction after device testing

The heading above frames the fault as a second-call problem. Device testing of
26.9.6160.0 disproved that framing and it is left in place only so the reasoning
history stays honest.

Both crashes in the 26.9.6160.0 log happen on the **first** call of a freshly started
process, in both directions, with no second session anywhere in the segment. The
overlapping-call reading explained the 26.9.6158.0 log but is not the general cause,
and the 26.9.6158.0 call that survived now looks like a timing fluke rather than
evidence that the capture path is sound.

The teardown defects 26.9.6160.0 fixed were real and were verified against the pinned
upstream source, so the change is kept. It is unfalsified rather than confirmed: the
log contains no `voip.teardown` and no `voip.transport|result=stopped`, so the process
died before any of the new code ran.

## 26.9.6161.0 — proving where the process actually dies

Every build so far concluded "the process dies inside `InitRecording`" from the absence
of a `step=init_recording;phase=end` line. That inference does not hold.

`phase=begin` and `phase=end` are both written by **managed** code: the device module
wrapper raises a report, the bridge marshals it across the WinMD boundary, and
`CallsService` writes the line. A death anywhere in that window produces exactly the
same evidence, so the missing `phase=end` is equally consistent with

* a fault inside the native `InitRecording`,
* a fault inside the managed report that follows it, and
* an unrelated death on another thread that merely happened to land in the window.

The 26.9.6160.0 log actively argues against the first reading. The process survived
65 ms past `step=init_recording;phase=begin` and answered a TDLib request in that time
(`tdlib.result|type=Ok`), which is not the shape of an instantaneous fault inside a
WASAPI call.

Two gaps made this impossible to settle:

1. **The vectored handler cannot see the death.** It only observes structured
   exceptions. When a managed exception escapes a reverse call the runtime ends the
   process with a fail-fast, which bypasses every handler, so that death writes nothing
   at all. `result=abort`, `terminate`, `purecall` and `invalid_parameter` are all
   absent from the log and the fault budget was never exhausted, so whatever ends the
   process is not something the existing instrumentation can record.
2. **A fault address was classified only against this module.** `engine=0` says the
   faulting instruction is not in `ModernCallsBridge.dll`, which does not distinguish
   the platform audio stack from managed code — and those need opposite fixes.

### What this build adds

**A crash-proof in-flight marker.** A 64-byte file-backed section holds the step
currently in flight. Updating it is a memory write, not a system call, so it is cheap
enough not to perturb the timing of the fault, and because the section is file-backed
the memory manager still writes the last value to disk no matter how the process ends —
fail-fast, stack overflow or outright termination included. The next launch reports it
as `result=inflight_at_exit;step=<step>:<stage>` where the stage is one of

| stage | meaning |
| --- | --- |
| `report_begin` | died in the managed `phase=begin` report |
| `native` | died inside the real audio device module call |
| `report_end` | died in the managed `phase=end` report |

This separates the three readings above in a single device test.

**Fault address classification against the app binary.** The runtime compiles managed
code into the app executable, so a faulting instruction inside it is a managed null
dereference rather than a platform failure. Faults now carry `app=0|1`, plus an
`offset=` when the address falls in either known module. The module ranges are captured
once at install time, away from any fault context, because resolving a module from a
handler would take the loader lock the faulting thread may already hold. The fault
budget rises from 8 to 24 so a repeating fault no longer hides later ones.

**A self-arming capture bypass.** If the recovered marker names a capture step, the next
call runs the device module with `InitRecording`, `StartRecording` and `StopRecording`
reporting success without touching the platform module, logged as
`phase=skipped;reason=prior_fault`. A call that then survives proves the capture path is
what ends the process, and the user gets audio in one direction while we find out. It
arms only after a death inside a capture step, so a death anywhere else leaves the
device module completely untouched and adds no noise.

Nothing here changes the call path on a healthy first call. This build is instrumentation
plus a fallback that only a crash can switch on.

### Also established this round

* The build links WebRTC's **desktop** Core Audio module, `audio_device_core_win.cc`,
  inside an app container. This is worth noting as a structural risk, but it is not yet
  implicated: device enumeration, speaker and microphone init, both stereo queries,
  `init_playout` and `start_playout` all return `code=0`.
* `_builtInAecEnabled` is false — nothing in WebRTC or tgcalls calls `EnableBuiltInAEC`,
  so `InitRecording` takes the plain WASAPI path and never touches the voice-capture DSP
  that does not exist on Windows 10 Mobile. That branch is ruled out.
* Diagnostic budget exhaustion is ruled out as a cause of the log stopping: the crashing
  segments wrote 31 and 32 media lines against a budget of 192.

## 26.9.6161.0 result — the recording path is not the cause

The in-flight marker worked and returned a clear answer on the first device test.

### What the device reported

```
voip.media|step=init_recording;phase=begin        20:59:38.1336
voip.media|step=init_recording;phase=end;faulted=0;code=0   20:59:39.1893
voip.media|step=init_recording;phase=begin/end  (the duplicate init)
voip.media|step=start_recording;phase=begin      20:59:39.2013
<process death>
voip.fault|result=inflight_at_exit;step=start_recording:native;capture_skipped=1
```

Three conclusions follow directly, and they retire a theory each earlier build was
built on.

1. **`InitRecording` succeeds.** It returns `code=0`, taking 1.05 seconds. Every
   previous build concluded the process died inside it purely because the log stopped
   after `phase=begin`. It did not. The 1-second duration is why that line was so often
   the last one written.
2. **The process died with `start_recording` in flight, in the `native` stage** — not in
   the managed reporting path. That rules out the managed reverse-callback theory for
   this death.
3. **But `StartRecording` is almost certainly a bystander.** It does very little: it
   creates the capture thread and then blocks in
   `WaitForSingleObject(_hCaptureStartedEvent, 1000)`. A step is marked in flight for the
   whole of that wait, so any death on any thread during that second is attributed to it.

### The bypass experiment settled it

The same build armed a capture bypass on the next launch. The following call ran with
`init_recording` and `start_recording` skipped entirely, reached
`voip.transport|state=Established` — **and the process still died**, this time leaving
no marker at all, because no audio device step was in flight.

So:

* Skipping the entire recording path does not keep the call alive. The recording path is
  not the cause.
* A death with no step in flight is invisible to the fault reporter, because the vectored
  handler was gated on `g_audioDeviceLifecycleDepth > 0`. That gate is the reason the
  fatal event has never been captured.

The capture bypass is retired in 26.9.6162.0. It cannot help, and leaving it armed would
remove the microphone from every subsequent call and make a surviving call impossible to
recognise.

### What this leaves

The killer runs on a thread the instrumentation does not cover, during a window the fault
reporter was not armed for. Playout is started and the transport is established in every
failing run, so the engine's own media threads are live in both the capture and the
no-capture case.

## 26.9.6163.0 — arming the instrumentation where the death actually happens

Four changes, in order of expected value. (Built as 6162 first; that build was never
released, because review found its wider reporting window could spend the log budget on
benign faults and drop the fatal one. 6163 is 6162 plus the corrections below.)

### 1. `CoreApplication.UnhandledErrorDetected` (diagnostic *and* candidate fix)

The app subscribed to `Application.UnhandledException` only, which observes the UI thread
and nothing else. The call engine raises its failures on WebRTC's threads, so a managed
failure there was never going to appear — on .NET Native it fail-fasts the process
outright, writing nothing.

`CoreApplication.UnhandledErrorDetected` is the one managed hook that sees those.
`Propagate()` rethrows the error on the handler's thread so it can be recorded, and
catching it marks the error handled. If the crash is a managed exception escaping a
native callback, this both names it and stops it being fatal.

The handler records every error but only *absorbs* a `NullReferenceException`, the shape
this app is already known to raise once per call and survive. Anything else is logged and
then rethrown, leaving it unhandled. Suppressing an arbitrary failure inside a native
callback would resume the caller with half-applied invariants and convert a crash that
names its cause into a later one that does not. Log lines are
`app.unhandled.core.suppressed` and `app.unhandled.core.fatal`.

### 2. Fault reporting armed for the whole call

`g_callActiveDepth` is incremented once the tgcalls instance exists and is decremented at
teardown (`Stop()`'s completion and `ForceRelease()`, with `~CallSession` as a backstop —
tgcalls holds a strong reference in its stop completion, so destruction alone would close
the window at an unspecified later time). The vectored handler now reports while either an
audio device step is on the stack **or** a call is live. A new `in_step=` field preserves
the old distinction, and when no step is live the report says `step=none;same_thread=0`
rather than naming a device call that finished long ago.

### 3. A mapped fault slot, so a fault outside a device step costs nothing

Widening the window admits the benign managed access violation this app raises on every
call. Writing each one to the log would spend the 24-entry budget long before the fatal
fault arrived — and an empty log is indistinguishable from a clean run — and would run
blocking file I/O on WebRTC's real-time threads, which the fault handler is explicitly not
allowed to do.

So faults raised outside a device step are written to a fixed 192-byte slot in the same
file-backed section as the in-flight marker. Each fault overwrites the last, costs no
system call and claims no budget, and because the memory manager writes dirty pages back
regardless of how the process ends, the value that survives is by construction the last
fault before the death. The next launch emits it as
`voip.fault|result=last_fault_before_exit;...`.

Faults *inside* a device step still go straight to the log as before. Their budget is now
restored at the start of every call, and the first drop within a call emits
`result=budget_exhausted` so a truncated record is never read as a clean one.

### 4. App lifecycle logging

`Suspending`, `Resuming`, `EnteredBackground`, `LeavingBackground`. Windows 10 Mobile
terminates an app that holds audio across a suspend, and in the log that is
indistinguishable from a crash. These lines separate "we faulted" from "the OS took us
out" — a theory that is still open and that nothing so far excludes.

### How to read the next log

Read these in order; the first that matches names the layer the death happens in.

* `app.unhandled.core.fatal` or `app.unhandled.core.suppressed` present → the crash is a
  managed failure off the UI thread, now named by type and stack. `suppressed` means the
  process was allowed to continue, so if the call also survives, this was the cause. This
  is the best case.
* `voip.fault|result=last_fault_before_exit;...` → the last fault the dead process saw
  while a call was live, recovered from the mapped slot. `engine=1` is bridge/WebRTC code
  and `offset=` resolves against the archived map file via `Resolve-FaultOffset.ps1`;
  `app=1` is .NET Native compiled managed code; `null_page=1` with `app=1` is a managed
  null dereference.
* `voip.fault|result=exception;...;in_step=1` → a fault raised while an audio device call
  was actually on the stack.
* `app.lifecycle|event=suspending` just before the death → the OS is suspending the app
  during the call and this was never a crash.
* `result=budget_exhausted` → reports were dropped; the record is incomplete rather than
  clean.
* Still nothing → the death is not an exception, not managed, and not a suspend, which
  points at an OS-level kill of the app container.

The capture bypass is retired: it was tested on device and the process died anyway, so it
only removed the microphone and made a surviving call impossible to recognise. The mic is
live again in this build.

### 26.9.6163.0 artifacts

Under `%LOCALAPPDATA%\UnigramTdlibExperiment\artifacts\`.

| File | SHA-256 |
| --- | --- |
| `Unigram_26.9.6163.0_ARM_ModernTgCalls_FaultSlot_Sideload.zip` | `D25092C1D702A6B11311A9D6A8371F0AC59CAFEC455E0544B156D15051CBB047` |
| `Unigram_26.9.6163.0_ARM_ModernTgCalls_FaultSlot.appx` | `FA290B13E7E97293F977B308177DAE452BAE66450B5072CC5B5F6F17CCB2B7BE` |
| `Unigram_26.9.6163.0_ARM_ModernTgCalls_FaultSlot.cer` | `5D891C3D3F5DF85A556C01BD5BA58C6837A736776E4D781CDEE9790060A72B85` |
| `ModernCallsBridge_26.9.6163.0.map` | `4B6D2DA99ECA13C09F184B05D79CE18235CF7B70A696773D111D6E6E80DAE67C` |

Packed `ModernCallsBridge.dll` `07A573AF8341252D61E9DCD6D0EA4F41354E60C31759454A6E9C8C06FE23A30F`,
identical to the freshly built binary. The map file is kept out of the package and is only
needed to resolve an `offset=` from this exact build.

## 26.9.6164.0 - the ABI mismatch, and why nothing was ever logged

This build stops adding observers. The 6163 log finally said something decisive, and what
it pointed at is a build configuration defect rather than a bug in the call code.

### What 6163 proved

Two separate results, both useful:

* The per-call managed exception is named at last: a `NullReferenceException` in
  `VoIPPage.OnSizeChanged`. It was suppressed, the process survived it by roughly 2.3
  seconds, and the call carried on normally through `AcceptCall`, `CallStateReady`,
  transport setup and the whole audio device sequence. It is a real bug, fixed in this
  build, but it was never the crash.
* The fatal death raises **no exception at all** within the filter we were using, and no
  `app.lifecycle|event=suspending` precedes it. The mapped fault slot was empty, so there
  was not even an out-of-step fault to recover. The log simply stops between
  `start_playout;phase=begin` and the relaunch.

### Root cause

`TgCallsEngine.vcxproj` compiled the tgcalls sources and this facade with a **different
ABI than the `webrtc.lib` they link against**. Comparing the project's defines against
`C:\wrtcar\src\out\msvc\uwp\Release\arm\obj\modules\audio_device\audio_device_api.ninja`
(the library was configured `is_debug = false`):

| Define | webrtc.lib | bridge, before | Effect of the mismatch |
| --- | --- | --- | --- |
| `NDEBUG` | set | **missing** | `RTC_DCHECK_IS_ON` 0 vs **1** |
| `_HAS_EXCEPTIONS=0` | set | missing | MSVC STL ODR mismatch |
| `ABSL_ALLOCATOR_NOTHROW=1` | set | missing | abseil container ABI |
| `WEBRTC_LIBRARY_IMPL` | set | missing | export and trace linkage |
| `WEBRTC_NON_STATIC_TRACE_EVENT_HANDLERS=0` | set | missing | trace handler indirection |
| `WEBRTC_INCLUDE_INTERNAL_AUDIO_DEVICE` | set | missing | gates the real ADM in headers |

`NDEBUG` is not cosmetic here. It selects `RTC_DCHECK_IS_ON`, and `webrtc::SequenceChecker`
is a full mutex-and-thread-reference object when that is on and an **empty stub** when it
is off. Every WebRTC type embedding one - `AudioDeviceBuffer`, `cricket::BaseChannel`,
`TaskQueueBase`, `Mutex` - therefore has a different `sizeof` and different member offsets
on each side of the boundary. We were constructing and driving exactly those objects
across it.

That explains every symptom that previously had no explanation: a death with no exception,
no suspend and no stable location; `start_recording` in 6161 and `start_playout` in 6163;
and why bypassing the capture path never helped. It was never a step. It was memory
corruption, and the step on the marker was only whatever happened to be in flight.

### Why a failed check was invisible

`rtc_base/checks.cc` ends `FatalLog` with:

```cpp
#if defined(WEBRTC_WIN)
  DebugBreak();
#endif
  abort();
```

`DebugBreak()` raises `EXCEPTION_BREAKPOINT` (`0x80000003`). `IsFatalExceptionCode` did not
accept that code, so the handler returned `EXCEPTION_CONTINUE_SEARCH`, nothing else
handled it, and the process died leaving no record whatsoever. With DCHECKs wrongly
enabled in our translation units, any thread-affinity assertion - and `AudioDeviceBuffer`
is covered in them - would kill the process exactly this silently.

### Changes

1. **`TgCallsEngine.vcxproj`** now defines `NDEBUG`, `WEBRTC_INCLUDE_INTERNAL_AUDIO_DEVICE`,
   `WEBRTC_LIBRARY_IMPL`, `WEBRTC_NON_STATIC_TRACE_EVENT_HANDLERS=0` and
   `ABSL_ALLOCATOR_NOTHROW=1`, matching the library.
2. **`IsFatalExceptionCode`** gained `IsAlwaysFatalExceptionCode`, covering
   `EXCEPTION_BREAKPOINT`, `STATUS_STACK_BUFFER_OVERRUN`, `STATUS_INVALID_CRUNTIME_PARAMETER`,
   `STATUS_HEAP_CORRUPTION` and `STATUS_ASSERTION_FAILURE`. These are fatal by construction,
   so they are written to the log wherever they occur and mirrored into the mapped slot.
   `RTC_CHECK` stays live even with `NDEBUG`, so this remains necessary.
   C++ exceptions (`0xE06D7363`) are still excluded: this facade throws
   `std::invalid_argument` as ordinary argument validation.
3. **`VoIPPage.OnSizeChanged`** returns early when disposed or before the composition
   visuals exist, and null-checks the named elements it transforms.
4. **Review fixes.** The fault slot is cleared in `DisarmFaultReporting` so a clean call
   cannot leave a stale record to be reported as `last_fault_before_exit` on the next
   launch; `PublishFaultSlot` takes a single-writer claim and publishes its first byte last
   so a torn record reads as empty rather than as a different fault; the
   `budget_exhausted` notice moved into `ClaimFaultReport` so the abrupt termination
   handlers are covered too; and the recovered line no longer carries two `result=` keys.

### Residual risk

`_HAS_EXCEPTIONS=0` is still **not** matched, because this facade throws `std::invalid_argument`
and `std::logic_error` as part of its contract with the managed layer. Mixing the two
settings is formally unsupported. If calls still fail after this build, converting those
throws to status returns and matching the flag is the next step. `NTDDI_VERSION=NTDDI_WIN10_RS2`
is also ours alone, deliberately, to keep the Windows 10 Mobile API surface.

### How to read the next log

1. `voip.fault|result=exception;code=0x80000003` → a WebRTC `RTC_CHECK` failed, and the
   `offset=` now resolves to the exact function through `ModernCallsBridge_26.9.6164.0.map`.
2. No crash, call connects → the ABI mismatch was the whole story.
3. A crash with still nothing logged → `_HAS_EXCEPTIONS` is the remaining mismatch; see
   residual risk above.

### 26.9.6164.0 artifacts

Under `%LOCALAPPDATA%\UnigramTdlibExperiment\artifacts\`.

| File | SHA-256 |
| --- | --- |
| `Unigram_26.9.6164.0_ARM_ModernTgCalls_AbiFix_Sideload.zip` | `DA0519CD872049AB4373B1EE3D254E5B59112158320760C0B9754943F660A965` |
| `Unigram_26.9.6164.0_ARM_ModernTgCalls_AbiFix.appx` | `5BF4999A3323566FAF74A610C38A48BCED20C255A6987AEFCC251DD6EA4456A0` |
| `Unigram_26.9.6164.0_ARM_ModernTgCalls_AbiFix.cer` | `5D891C3D3F5DF85A556C01BD5BA58C6837A736776E4D781CDEE9790060A72B85` |
| `ModernCallsBridge_26.9.6164.0.map` | `4832B120B62014C3511E00F360CBF86DB88813D41BE1FA4BD3D72C27569AA7DC` |

Packed `ModernCallsBridge.dll` `1AF6A2AE57EAC31970C1A487841267566E1C027864C14572ED08E3A43BB65D45`,
identical to the freshly built binary.

## 26.9.6165.0 - calls work; fixing the call UI that never left "exchanging keys"

### What 6164 proved

The ABI fix worked. Device testing of 6164 produced two complete calls that ran end to
end without killing the process, in both directions:

```
voip.update     state=CallStateReady
voip.ready      result=starting
voip.transport  state=Reconnecting
voip.transport  state=Established
                ... every audio step phase=end;faulted=0;code=0 ...
voip.media      remote_audio;state=Active
voip.media      audio_level;samples=49;active=34;peak=5.08;created=1;recording=1;playing=1
voip.media      signal_bars;bars=4
voip.update     state=CallStateDiscarded
voip.teardown   result=drained;elapsed_ms=100
```

`start_playout` and `start_recording` - the exact steps that killed every build from
6155 through 6163 - now complete normally. Audio is audible in both directions. The
crash is fixed.

What remained was a **UI-only** defect: the call connected and audio flowed, but the
page kept displaying "Exchanging encryption keys" for the entire call, and the call
duration never started counting.

### Root cause of the stuck label

`VoIPPage.Update(Call, DateTime)` ends with a switch that sets `StateLabel.Content`:

| TDLib call state | Label |
| --- | --- |
| `CallStatePending` | Requesting / Waiting / Ringing / Incoming |
| `CallStateExchangingKeys` | "Exchanging encryption keys" |
| `CallStateHangingUp` | "Hanging up" |
| `CallStateDiscarded` | "Busy" / "Call ended" |
| **`CallStateReady`** | **no case - label left untouched** |

That omission is not a bug in the original app. `CallStateReady` is the point where
TDLib hands the call over to the voice engine, and in the legacy design
`VoIPControllerWrapper.CallStateChanged` took over from there:
`VoIPPage.OnCallStateChanged` set "Connecting" on `WaitInit`/`WaitInitAck`, then on
`Established` set the label to `00:00`, revealed `SignalBarsLabel`, and started
`_durationTimer`.

With the modern tgcalls bridge there is no `VoIPControllerWrapper`. `CallsService`
never constructs one, so `VoIPPage.Connect(controller)` is never called and
`OnCallStateChanged` never fires. TDLib's last labelled state was
`CallStateExchangingKeys`, so that string stayed on screen for the whole call while the
bridge quietly reached `Established` and pumped audio underneath it.

The bridge was already reporting everything needed - `voip.transport|state=Established`
and `voip.media|signal_bars;bars=4` are in the 6164 log - it simply had no route to the
page.

### The fix

**`Unigram\Unigram\Views\VoIPPage.xaml.cs`**

- New `_modernEstablished` field, the modern counterpart to `_state ==
  libtgvoip.CallState.Established`.
- New `case CallStateReady` in the label switch, showing "Connecting" so the label
  advances the moment TDLib is done, even before the transport comes up. Guarded by
  `!_modernEstablished` so a later `Update()` cannot clobber a running duration.
- `CallStateHangingUp` / `CallStateDiscarded` now clear `_modernEstablished`, which also
  stops the duration timer through its existing tick check.
- New `UpdateModernTransportState(ModernCalls.CallState)` under `MODERN_TGCALLS`. It is
  a direct mirror of `OnCallStateChanged`: "Connecting" on `WaitInit`/`WaitInitAck`,
  `00:00` + signal bars + `StartUpdatingCallDuration()` on `Established`, "Failed" on
  `Failed`. `Reconnecting` is deliberately unhandled so a mid-call blip does not wipe
  the duration. Idempotent via `_modernEstablished`, and it early-returns if `_disposed`.
- `DurationTimer_Tick` and the discard-duration calculation now accept either the legacy
  `_state` or `_modernEstablished`.
- `SetSignalBars` no longer hard-casts `FindName($"Signal{i}")`; a missing element is
  skipped instead of throwing an NRE.

**`Unigram\Unigram\Services\CallsService.cs`**

- `OnModernCallStateChanged` records the state in a new `_modernTransportState` field
  and forwards it to `_callPage.UpdateModernTransportState(state)`. If the page does not
  exist yet it logs `voip.ui|result=transport_deferred;state=<state>` rather than
  dropping the event silently.
- `OnModernSignalBarsChanged` forwards to `_callPage.SetSignalBars(bars)` on the UI
  thread.
- `ShowAsync` replays the last `_modernTransportState` and `_modernSignalBars` right
  after `callPage.Update(call, started)`. This closes the race where the page is created
  (or re-created on activation) after the transport already reported `Established`, in
  which case no further event would ever arrive.
- Both fields are reset alongside `_modernSignalBars` / `_modernRemoteAudioState` when a
  call is torn down.

### Also in this build

The two code-review fixes made after commit `20b313625` are included here:

- `FatalExceptionReporter` no longer discards the fault-slot write when the diagnostic
  budget is exhausted. It publishes to the mapped slot first and claims the budget only
  around the log write, so an always-fatal code arriving late is still recorded.
- `ClearFaultSlot` takes the `g_faultSlotWriter` claim, so it cannot race a concurrent
  publish and resurrect a partially written record.

### Still open, deliberately

- A survivable `NullReferenceException` is still absorbed at `App.xaml.cs:655` roughly
  140 ms after the call page is constructed, in both call directions. It does **not**
  block the UI - subsequent `Update()` calls demonstrably ran, which is how the label
  reached "exchanging keys" at all - so it is not the cause of this defect. The reported
  stack names `VoIPPage.OnSizeChanged`, but its caller frame is
  `TypedEventHandler<UIElement, RoutedEventArgs>`, which is not the `SizeChanged`
  delegate type; .NET Native symbol merging means the real throw site is elsewhere.
  Adding guards to `OnSizeChanged` in 6164 did not stop it, confirming the
  misattribution. Chase it only if it starts causing visible harm.
- `_HAS_EXCEPTIONS=0` is still not matched against `webrtc.lib` (the facade throws
  `std::invalid_argument` / `std::logic_error` in ~15 places). Now that calls are
  stable this is a lower priority, but it remains formally unsupported mixing.
- `DisarmFaultReporting()` still runs inside the `stop()` completion, before tgcalls'
  deferred `~Manager` / `~MediaManager` teardown. Faults in that window are declined.

### Artifacts

| File | SHA-256 |
| --- | --- |
| `Unigram_26.9.6165.0_ARM_ModernTgCalls_CallUi_Sideload.zip` | `2ABAD6FF9A19DBF38F69922D94F592B072FA11563C42323DB6D0DD3492C0DBED` |
| `Unigram_26.9.6165.0_ARM_ModernTgCalls_CallUi.appx` | `A32E084E65E6129DE8C5873D904192E7A741C280EC7232480C7A6CF3B3FAB795` |
| `Unigram_26.9.6165.0_ARM_ModernTgCalls_CallUi.cer` | `5D891C3D3F5DF85A556C01BD5BA58C6837A736776E4D781CDEE9790060A72B85` |
| `ModernCallsBridge_26.9.6165.0.map` | `AAF413BCD5FD536B8D7EE13F9E07DF170F9B285101771FD3EB5E0466892BA446` |

Packed `ModernCallsBridge.dll` =
`79B2CE0716DCC7080F2D420DC46E81A0D68A8F15CC47DE3B5A57B509A9178BD9` (4,250,112 bytes),
hash-identical to the freshly built binary.

### What to look for in the next log

Success now looks like a normal call *plus* a usable screen:

- the label leaves "exchanging encryption keys" within a second of
  `voip.update|state=CallStateReady`,
- it shows "Connecting" briefly, then switches to a counting `00:00` timer at
  `voip.transport|state=Established`,
- the signal-bar indicator appears and tracks `voip.media|result=signal_bars`.

If the label still sticks, check for `voip.ui|result=transport_deferred` - that would
mean the transport state arrived before the page existed and the `ShowAsync` replay did
not run.

## 26.9.6166.0 - inbound audio inaudible and a dead mute button

### What 6165 proved

The call page now leaves "exchanging encryption keys", shows the connected state and
runs the duration timer. The UI fix landed. Device testing surfaced two further,
separate defects:

1. Nothing is audible on the Windows 10 Mobile handset, while the Android peer hears
   the user fine. Capture works; playout does not reach the ear.
2. The mute toggle does nothing.

### Mute - confirmed, same class of bug as the label

`VoIPPage.IsMuted`'s setter only ever called `_controller.SetMicMute(value)`.
`_controller` is the legacy `VoIPControllerWrapper`, which the modern engine never
constructs, so the setter was a no-op for every modern call. The native chain already
existed end to end - `AudioCallSession.SetMuted` -> `CallSession::SetMuted` ->
`_instance->setMuteMicrophone` - it simply had no caller.

Fixed by giving `VoIPPage` a `ModernMuteRequested` callback that `CallsService`
assigns in `ShowAsync`. `SetModernMuted` stores the value in `_modernMuted` and
applies it to the session; the stored value is re-applied when the session is created,
so a toggle flipped before the call reaches Ready is not lost. Each attempt logs
`voip.media|result=mute;muted=N;applied=N`.

### Silence - not yet diagnosed, so 6166 adds the missing telemetry

Everything the 6165 log can show is green: `playout_devices=2`, every ADM step
`faulted=0;code=0`, `start_playout` succeeds, `Playing()` is true, `remote_audio`
`state=Active`, transport `Established`. The `audio_level` summary is misleading here:
tgcalls' `audioLevelUpdated` reports the *outgoing* microphone level, so its peaks only
confirm capture - consistent with Android hearing us. We had no playout telemetry at
all.

Two hypotheses, both now observable:

- The platform ADM starts the render endpoint muted or at zero gain. tgcalls never
  calls `SetSpeakerVolume`/`SetSpeakerMute`, so nothing would correct it.
  `AudioDeviceStatus()` now also reports `speaker_volume=<vol>/<max>` and
  `speaker_mute=0|1`, and a new `EnsureSpeakerAudible()` runs after a successful
  `StartPlayout()`: it unmutes a muted speaker and raises a zero volume to the
  maximum. Every step is reported as `voip.media|...step=speaker_*`. Only numeric
  gain is logged, never device names.
- Windows 10 Mobile routes the Communications render stream to the earpiece. The page
  already has a speakerphone toggle driven by `AudioRoutingManager`, but `OnLoaded`
  returned silently in two of its branches and only logged failure, so no routing line
  appeared anywhere in the log. It now logs `routing_skipped` with a reason,
  `routing_unavailable`, or `routing_ready;endpoint=...;available=...`.

`SetPlayoutDevice`/`SetRecordingDevice` now log the endpoint tgcalls selected
(`phase=select;index=` or `;type=`) for both the index and `WindowsDeviceType`
overloads, so a successful-but-wrong endpoint selection is visible.

### Review findings folded in

A code review of the 6165 diff raised three valid defects, all fixed here:

- **Stale call duration (high).** `VoIPService._callStarted` is never reset between
  calls, and `VoIPPage.Update` assigns `_started` from it unconditionally, so the
  second call in an app session would open its timer at the first call's elapsed time
  and report that inflated duration to Telegram in `CreateDiscardCall`. The
  `Established` branch now takes `_started = DateTime.Now` directly, and
  `_callStarted` is reset during teardown.
- **Hang-up resurrected by the replay (medium).** `_modernTransportState` is cleared
  only by `DisposeModernCall()`, which `CallStateHangingUp` never reaches, so the
  `ShowAsync` replay re-applied `Established` one dispatcher pass after the teardown
  cleared it - restarting the timer and hiding the "Hanging up" label. The replay now
  skips terminal call states.
- **Signal bars on the wrong dispatcher (medium).** `OnModernSignalBarsChanged` used
  the service's `BeginOnUIThread`, which resolves to the *main* window, while
  `ShowAsync` can host the page in a secondary `ApplicationView` on desktop ARM -
  `RPC_E_WRONG_THREAD` on every bars change. It now uses `callPage.BeginOnUIThread`.
  `SetSignalBars` also gained the `_disposed` guard it was missing.

### Still open

The silence itself. 6166 is a diagnostic round with one speculative fix; if the
speaker was already unmuted at full volume and the endpoint is correct, the next
suspect is the render side of the UWP ADM itself.

### Artifacts

`%LOCALAPPDATA%\UnigramTdlibExperiment\artifacts\`

| File | SHA-256 | Bytes |
| --- | --- | --- |
| `Unigram_26.9.6166.0_ARM_ModernTgCalls_Audio_Sideload.zip` | `D4761C0EBD9671311B02DEAA1F9A2E40B9E41FB29FF130CB06ACB080614C0DE4` | 64,114,158 |
| `Unigram_26.9.6166.0_ARM_ModernTgCalls_Audio.appx` | `87C2080432973FA325D8EA363E768A1C2308F1F3884B5E6822D5D7A9808CD463` | 57,512,435 |
| `Unigram_26.9.6166.0_ARM_ModernTgCalls_Audio.cer` | `5D891C3D3F5DF85A556C01BD5BA58C6837A736776E4D781CDEE9790060A72B85` | 832 |
| `ModernCallsBridge_26.9.6166.0.map` | `20B90BC0DEE75EF5352028BE143B1FF90A57F3C8747C4D69BA95FBC4675DEDCE` | 14,841,516 |

Packed `ModernCallsBridge.dll` = `4B832C012EDD94760BF514AE2B18F7FC742DC6C65B5B62119CA31B3425EB32B6`
(4,253,696 bytes), matching the built binary.

### Reading the next log

- `voip.ui|result=routing_*` - exactly one per call page. `routing_ready` names the
  active endpoint; anything else means the speakerphone toggle is not even wired up.
- `voip.media|...step=set_playout_device;phase=select` - which endpoint tgcalls chose.
- `voip.media|...step=speaker_mute;phase=state;muted=` and
  `step=speaker_volume;phase=state;volume=N;max=N` - the state the render endpoint was
  actually in when playout started. A `set_speaker_*` step means we corrected it.
- `voip.media|result=mute;muted=N;applied=N` - one per toggle press.

## 26.9.6167.0 - measuring the playout stream itself

6166 added speaker and routing telemetry, but all of it describes the *device*. If the
device is configured correctly and audio is still inaudible, the next question is
whether WebRTC is handing the device anything to play. Nothing in the pipeline answered
that, so this adds the one measurement that splits the problem cleanly in two.

### Why the existing levels could not answer it

tgcalls' `audioLevelUpdated` reports the **outgoing** microphone level. Its healthy
peaks in every log so far only confirm capture, which the Android peer already proved by
hearing us. `AudioDeviceModule::Playing()` returning true means the render thread is
running, not that the samples it renders are non-silent.

### The playout probe

`webrtc::AudioTransport` is the interface the device module pulls render data through.
`DiagnosticAudioDeviceModule::RegisterAudioCallback` now inserts a
`PlayoutProbeAudioTransport` between WebRTC and the device. It forwards every call
unchanged and, on `NeedMorePlayData` and `PullRenderData`, scans the buffer that was
just filled for its peak absolute amplitude.

Peaks are accumulated over roughly two seconds and reported once per window as
`voip.media|...step=playout_level;phase=window;source=play|pull;peak_permille=N;rate=N`,
capped at twelve reports per session. Only a peak and a sample rate leave the probe: no
audio is copied, retained, or reported in any form from which speech could be
reconstructed.

Interpreting the result is unambiguous:

- **`peak_permille` stays 0** - WebRTC is rendering digital silence. The fault is
  upstream of the device: decode, the receive stream, the audio track, or the network.
  Speaker volume and routing are then irrelevant.
- **`peak_permille` is non-zero and nothing is audible** - WebRTC is producing real
  audio and the device or the routing is swallowing it. The 6166 speaker and endpoint
  fields then say which.

### Lifetime

The device module keeps a raw pointer to the probe until a later registration replaces
it, so the probe is owned by the wrapper rather than the caller. Member destruction runs
before the base class releases the module, so the destructor explicitly unregisters the
callback first (`step=unregister_audio_callback`) rather than relying on that ordering.
A `RegisterAudioCallback(nullptr)` from WebRTC clears the probe as well.

### Artifacts

`%LOCALAPPDATA%\UnigramTdlibExperiment\artifacts\`

| File | SHA-256 | Bytes |
| --- | --- | --- |
| `Unigram_26.9.6167.0_ARM_ModernTgCalls_PlayoutProbe_Sideload.zip` | `974B9966962518998FA8A11D7B40707879BB2A5ED95E323EE996F240B24D0766` | 64,111,991 |
| `Unigram_26.9.6167.0_ARM_ModernTgCalls_PlayoutProbe.appx` | `E560871146F71F430D33B100024E7D33F25F016638E50A7E4DA928E251DFF8F7` | 57,510,638 |
| `Unigram_26.9.6167.0_ARM_ModernTgCalls_PlayoutProbe.cer` | `5D891C3D3F5DF85A556C01BD5BA58C6837A736776E4D781CDEE9790060A72B85` | 832 |
| `ModernCallsBridge_26.9.6167.0.map` | `47C006DB80DD70F6A8A557E98FB8668A800E8ED11EC4185DF21FD8552BCE74FD` | 14,861,761 |

Packed `ModernCallsBridge.dll` = `145A45A1CBC74D2614C1B695B0B6589BC1AFA11D14F55B591F5316DDF295DEAD`,
matching the built binary. Signature verified, identity
`49197Wirdschon.UnigramMobileTdlibExperimental 26.9.6167.0 arm`, background entry point
present, zero forbidden payload entries, 23 files in the sideload ZIP.

This supersedes 26.9.6166.0, which was never installed; it contains everything 6166 did.

## 26.9.6168.0 — corrected playout probe (supersedes 6166 and 6167)

Neither 26.9.6166.0 nor 26.9.6167.0 was ever installed on the device. Their
artifacts have been deleted, because the 6167 probe contained a critical buffer
overread. 6168 carries the full 6166 + 6167 feature set with four review fixes
applied on top, and is the build to install.

### Review fixes folded in

1. **Critical: stereo playout buffer overread.** `NeedMorePlayData`'s `nSamplesOut`
   is already the *interleaved* total (frames x channels), not a per-channel count —
   see `audio/audio_transport_impl.cc` (`RTC_DCHECK_EQ(nSamplesOut, nChannels * nSamples)`)
   and `modules/audio_device/audio_device_buffer.cc:359-360`, which divides it by
   `play_channels_`. The probe multiplied it by `nChannels` again and read up to twice
   the buffer length. Playout is stereo by default on this ADM
   (`audio_device_core_win.cc:481`), so this was a live 2x overread on every render
   callback. `Measure()` now takes an explicit interleaved `count` plus `channels`;
   `NeedMorePlayData` passes `nSamplesOut` unmodified and `PullRenderData` passes
   `number_of_frames * number_of_channels`.
2. **Realtime safety.** The probe no longer does file I/O. `AudioDeviceWindowsCore`'s
   render loop holds `_critSect` for the whole iteration and aborts playout if the
   render event wait exceeds 0.5 s (`audio_device_core_win.cc:1551-1587`), so blocking
   on `PushDiagnostics` there could itself have stopped audio. `Measure()` now only
   writes three file-scope atomics (`g_playoutPeakPermille`, `g_playoutSampleRate`,
   `g_playoutWindows`); `AudioDeviceStatus()` reads them on a normal thread.
3. **Dangling probe on refused unregister.** `AudioDeviceBuffer::RegisterAudioCallback`
   refuses with `-1` and keeps the old pointer while `playing_ || recording_`
   (`audio_device_buffer.cc:87-90`). Freeing the probe after a refused unregister would
   leave WebRTC holding a dangling `AudioTransport*`. `ReleaseProbe(bool detached)` now
   frees it only when the detach was accepted; otherwise it logs
   `step=release_audio_probe;phase=leaked` and deliberately leaks the object.
4. **Speaker volume correction made conservative.** `SetSpeakerVolume` maps to
   `ISimpleAudioVolume::SetMasterVolume` on the app's render session, which Windows
   **persists per application**. Forcing it to maximum on every `StartPlayout` was a
   real user-facing defect. The zero-volume correction now runs at most once per module
   and targets half scale.

### What to look for in the next `push-diagnostics.txt`

- `voip.media|result=audio_device;...playout_peak_permille=N` — **the decisive field.**
  `0` means WebRTC is rendering digital silence and the fault is upstream (decode,
  receive stream, audio track, network); speaker volume and routing are then irrelevant.
  Non-zero with nothing audible means the device or routing is swallowing real audio.
- `voip.ui|result=routing_ready;endpoint=...;available=...` (or `routing_skipped` /
  `routing_unavailable`) — exactly one per call page.
- `voip.media|...step=set_playout_device;phase=select;index=` or `;type=`.
- `step=speaker_volume;phase=state;volume=N;max=N` and `step=speaker_mute;phase=state;muted=`.
- `voip.media|result=mute;muted=N;applied=N` — one per mute-button press.
- `step=release_audio_probe;phase=leaked` — abnormal teardown; unregister was refused.

### Artifacts

Under `%LOCALAPPDATA%\UnigramTdlibExperiment\artifacts\`:

| File | SHA-256 | Bytes |
| --- | --- | --- |
| `Unigram_26.9.6168.0_ARM_ModernTgCalls_PlayoutProbe_Sideload.zip` | `C97E576F1140CAC0B22F3C56404204B6142D05F1BBA392CCCEE74BCD33067895` | 64,110,253 |
| `Unigram_26.9.6168.0_ARM_ModernTgCalls_PlayoutProbe.appx` | `830A5879B91E59D0424815C1F52631F21F3C8A0506DD81418C2A7BFAB2771D45` | 57,508,330 |
| `Unigram_26.9.6168.0_ARM_ModernTgCalls_PlayoutProbe.cer` | `5D891C3D3F5DF85A556C01BD5BA58C6837A736776E4D781CDEE9790060A72B85` | 832 |
| `ModernCallsBridge_26.9.6168.0.map` | `94B59CBD142CDFED474CA37DE21B993AFDDF6DB1726CAB24339F118718C11A5A` | 14,862,341 |

Packed `ModernCallsBridge.dll`: `CC43B50A25881D8D110A189DF6963BF34A57E6463082E03E9A3F4B201063257C`
(4,258,304 bytes), hash-identical to the built binary in `bridge-probe\`.

Verified: `signtool verify /pa` OK; identity `49197Wirdschon.UnigramMobileTdlibExperimental`,
version `26.9.6168.0`, `ProcessorArchitecture="arm"`; background entry point
`Unigram.Native.Tasks.NotificationTask` present; `Telegram.Td.dll`, `Telegram.Td.winmd`
and `ModernCallsBridge.dll` all present; zero `.pfx` / `Constants.Secret.cs` / `.pdb`
entries; 23-file sideload ZIP (`.appxsym` and `TelemetryDependencies` excluded).

## 26.9.6169.0 — the silent call: render stream tagged as "other sounds"

The 6168 playout probe settled the question in one test. From the device log:

```text
voip.media|result=audio_level;...;playing=1;speaker_volume=100/100;speaker_mute=0;playout_peak_permille=827;playout_rate=48000
```

Peaks of 827, 637, 570 and 343 permille at 48 kHz, with the session unmuted at full
volume. WebRTC was rendering loud, correct audio the whole time. Nothing upstream was
broken: not decode, not the receive stream, not the network, not the audio track. The
rendered samples were simply never reaching a speaker, which is why every ADM step had
always reported `faulted=0;code=0`.

### Root cause

`InitMixer()` in `modules/audio_device/win/audio_device_core_win.cc` tagged the two
directions of the call differently:

```cpp
if constexpr (DEVICE_CLASS == DeviceClass::DeviceClass_AudioCapture) {
  properties.eCategory = AudioCategory_Communications;
} else {
  properties.eCategory = AudioCategory_Other;   // render
}
```

Windows ducks streams tagged `AudioCategory_Other` while a communications stream is
live, and the Windows 10 Mobile call audio policy mutes them outright. The app was
therefore muting its own call audio: the capture stream declared a call in progress, and
the render stream declared itself to be "other sounds" playing during that call.

This explains every observation at once, including the asymmetry. Outbound audio always
worked because capture was already tagged correctly, so the Android side heard
everything. Only the inbound direction was silenced, and only on the handset.

Fixed by tagging both directions as communications, which is also what upstream's newer
core audio implementation (`core_audio_utility_win.cc`) does unconditionally. Persisted
as `patches\webrtc-m123-winuwp-arm-render-communications-category.patch` and wired into
`Build-WebRtcUwpArm.ps1`, so a clean WebRTC rebuild reproduces it; `webrtc.lib` must be
rebuilt for the change to take effect.

### Second defect: the speakerphone button never existed

`VoIPPage.xaml` declared the routing toggle with `x:DeferLoadStrategy="Lazy"`, so the
element was not realised until something called `FindName`, and nothing did. `OnLoaded`
read it, found null, and took its early return:

```text
voip.ui|result=routing_skipped;reason=control_unavailable
```

That return happens before `AudioRoutingManager.GetDefault()`, so the deferral did not
merely hide a button: it disabled call audio routing entirely. There was no earpiece or
speakerphone control, and no `AudioEndpointChanged` subscription. The element now loads
eagerly and starts `Collapsed`; `OnLoaded` still decides whether to show it, so an
environment without the phone contract behaves as before.

The control template binds `Text` to `CheckedGlyph` in both states, so the existing
single-glyph declaration renders correctly.

### Third change: endpoint names in diagnostics

Module creation now reports `playout_names=0:<name>|1:<name>`, so the endpoint a silent
call is playing into can be identified directly. Names describe hardware, not the user,
and are sanitised regardless: every character outside `[A-Za-z0-9 _-]` is dropped and
each name is truncated to 48 characters, so a renamed device cannot inject free text
into the log.

### Not a bug: the four emoji

The four emoji above the caller's name are the call's encryption key fingerprint, which
is standard Telegram behaviour. Both parties see the same four; reading them aloud
confirms there is no man in the middle.

### Artifacts

Under `%LOCALAPPDATA%\UnigramTdlibExperiment\artifacts\`:

| File | SHA-256 | Bytes |
| --- | --- | --- |
| `Unigram_26.9.6169.0_ARM_ModernTgCalls_AudioCategory_Sideload.zip` | `51FA851162399EF4BE2DC56DD94F78EDEC4537E0D70C47628C741C1BBF8AEE16` | 64,133,865 |
| `Unigram_26.9.6169.0_ARM_ModernTgCalls_AudioCategory.appx` | `13E38539864616CDD8B7C6E9F930B1EDB3968A65A5EDC7BFEACFF5CBEE2E2D79` | 57,518,085 |
| `Unigram_26.9.6169.0_ARM_ModernTgCalls_AudioCategory.cer` | `5D891C3D3F5DF85A556C01BD5BA58C6837A736776E4D781CDEE9790060A72B85` | 832 |
| `ModernCallsBridge_26.9.6169.0.map` | `C72255D25FB69C388C265FF76492CF4BECDEFF57FD602FEDE5809AE335ECC0A4` | 14,866,148 |

Packed `ModernCallsBridge.dll`: `06EA2EC8E6AE6009D33BCAAFEAF8B345F1805B595A11F050D09E3CD34EE869B6`,
hash-identical to the built binary in `bridge-probe\`.

Verified: `signtool verify /pa` OK; identity `49197Wirdschon.UnigramMobileTdlibExperimental`,
version `26.9.6169.0`, `ProcessorArchitecture="arm"`; background entry point
`Unigram.Native.Tasks.NotificationTask` present; `Telegram.Td.dll`, `Telegram.Td.winmd`
and `ModernCallsBridge.dll` present; zero `.pfx` / `Constants.Secret.cs` / `.pdb`
entries; 23-file sideload ZIP.

### What to check on the device

1. Inbound audio from the Android handset is now audible.
2. A speakerphone toggle appears to the right of the hang-up button and switches between
   earpiece and loudspeaker.
3. Mute still works (shipped in 6166, never device-tested).
4. `playout_names=` in the log names both render endpoints.

## 26.9.6170.0 — call protocol version negotiation

### What 6169 settled

Device testing confirmed both 6169 fixes.

Calls to and from Android now carry audio in both directions. The render stream being
tagged `AudioCategory_Other` was the whole of the inbound silence, and the log agrees:
`playout_peak_permille` runs 163-781 across those calls, and the user hears them.

The routing control also came back. `voip.ui|result=routing_ready;endpoint=Speakerphone;available=Earpiece, Speakerphone`
replaced the old `routing_skipped;reason=control_unavailable`, and a later line in the
same session shows `endpoint=Earpiece`, so the toggle is both present and effective.

Two failures remain, and only one of them is ours.

Calls between this build and another Windows 10 Mobile handset report that the peer must
update Telegram. That handset runs the stable build on TDLib 1.7.10, which offers only
libtgvoip. There is no shared protocol between libtgvoip and tgcalls, so this is expected
and out of scope.

Calls to and from iOS connect, hold, report four signal bars and report
`remote_audio;state=Active`, and carry no audio in either direction.

### Root cause candidate: we disagreed with the peer about which protocol to speak

The iOS log is the opposite of the Android one. `playout_peak_permille` is `0` on every
single sample across both iOS calls, while the microphone peak sits at its normal 3.4,
so capture is healthy and the decoder is producing nothing at all. Transport is up;
media is not. That is the signature of two peers running different wire protocols over a
working connection.

The bridge registers `InstanceImpl` alone, which claims versions `2.7.7` and `5.0.0`.
Those are not aliases. `tgcalls::Meta::Create` in `Instance.cpp:42-46` maps `2.7.7` to
`ProtocolVersion::V0` and `5.0.0` to `ProtocolVersion::V1`. Both peers must land on the
same string.

Comparing against UnigramDev/Unigram, the reference implementation, this build deviated
from it in two places.

The offer was ordered backwards. `VoipManager::Protocol()` sorts the registered versions
numerically descending before offering them, with the comment "Server processes them
newer to older". This build hardcoded `{ "2.7.7", "5.0.0" }`, which is ascending, so the
offer advertised the older protocol as its preference.

The reply was searched instead of read. The reference takes
`ready.Protocol.LibraryVersions[0]` and nothing else: the ready-state list is the
server's decision, not a menu. This build scanned the whole list for the first entry it
recognised. That is identical to reading element zero whenever element zero is
supported, and silently diverges the moment it is not, which is exactly how one peer ends
up on V0 while the other runs V1.

Both are now fixed. The offer is `{ "5.0.0", "2.7.7" }`, newest first, and selection
reads the head and stops. A head this build does not implement no longer falls through to
some other version: it is reported and declined, so the call drops to the legacy
transport honestly rather than connecting into silence.

### The diagnostic that will confirm or refute this

One line per call, on its own budget so the shared call diagnostics cannot starve it:

```text
voip.version|result=negotiated;version=5.0.0;offered=2
voip.version|result=unsupported;version=12.0.0;offered=8
voip.version|result=unavailable;offered=0
```

This is the decisive line. `result=negotiated` with audible audio means the ordering was
the defect and the matter is closed.

`result=unsupported` means the server is negotiating a protocol this build does not
implement, and names it. In that case the ordering was not the cause and the fix is to
build and register `InstanceV2Impl` (versions `7.0.0`, `8.0.0`, `9.0.0`, `12.0.0`) or
`InstanceV2ReferenceImpl` (`10.0.0`, `11.0.0`), neither of which is compiled into the
bridge today. That is a substantially larger piece of work than this change, which is why
the cheap and provably-correct alignment with the reference implementation comes first.

`result=negotiated` with iOS still silent rules out version negotiation entirely and
moves the search to codec or transport parameters.

Version strings are protocol constants and carry nothing about the user. A server-supplied
value is still reduced to digits and dots and capped at 16 characters before it reaches the
log, so it cannot introduce separators into the line.

### Also in this build

`DescribePlayoutDevices` now names at most four render endpoints and appends `+N` for the
remainder. Naming a device is not a cheap lookup in this WebRTC fork: every call
re-enumerates the whole render device class and blocks on the result, so an unbounded loop
put one blocking enumeration per endpoint on the call-setup path.

### Artifacts

Under `%LOCALAPPDATA%\UnigramTdlibExperiment\artifacts\`:

| File | SHA-256 | Bytes |
| --- | --- | --- |
| `Unigram_26.9.6170.0_ARM_ModernTgCalls_VersionNegotiation_Sideload.zip` | `036002DE85CAAA983A10CE19C0B48A98AA63C54BC0C20A4CBE5EE5E71881A608` | 64,124,527 |
| `Unigram_26.9.6170.0_ARM_ModernTgCalls_VersionNegotiation.appx` | `CA511F077E82DC71574843ABC94B746368C29D4AE2B880BFE365CFF1EA16409A` | 57,517,445 |
| `Unigram_26.9.6170.0_ARM_ModernTgCalls_VersionNegotiation.cer` | `5D891C3D3F5DF85A556C01BD5BA58C6837A736776E4D781CDEE9790060A72B85` | 832 |
| `ModernCallsBridge_26.9.6170.0.map` | `5A99E024CCDBD3755CD8DD89E13C9071965EC536D45055AF778C739156EC7B90` | 14,866,746 |

Packed `ModernCallsBridge.dll`: `9B72D7E470EBFA8C7C30ECBB6717DB2804AED6316F264C4BD292AE93DF5E3FAC`,
hash-identical to the built binary in `bridge-probe\`.

Verified: `signtool verify /pa` OK; identity `49197Wirdschon.UnigramMobileTdlibExperimental`,
version `26.9.6170.0`, `ProcessorArchitecture="arm"`; entry points `Unigram.App` and
`Unigram.Native.Tasks.NotificationTask` present; 555 payload entries with zero `.pfx`,
`Constants.Secret.cs`, `.pdb` or `.appxsym`; 23-file sideload ZIP.

6169 is retained as the rollback reference, since it is the last build confirmed working
against Android.

## 26.9.6171.0 — per-call diagnostic budgets

`voip.version` now reports the complete negotiated list (`list=`) rather than only the
head and a count, which is what finally closed the protocol-version question: every call,
working or silent, reports `offered=1;list=5.0.0`. The TDLib ready-state library-version
list carries the server's single decision, not a menu to choose from, so the Android call
that carries audio and the iOS call that does not agree on the identical protocol.
Version negotiation is therefore exonerated and should not be revisited.

A diagnostics defect found while comparing two calls back-to-back was fixed at the same
time. `_audioCallDiagnosticBudget` was a process-wide countdown that nothing replenished,
so the first call of a launch spent it and the second recorded no `voip.ready`,
`voip.transport` or `voip.signaling` evidence whatsoever — and the second call of a pair
is normally the one under investigation. It is now reset per call. Per-message signalling
lines were also reduced to the first occurrence, with running totals folded into the
periodic summary as `sig_sent=` / `sig_recv=`; previously fifty near-identical lines
consumed the entire budget.

## 26.9.6173.0 — counting incoming audio RTP

### What 6171 proved

The iOS call establishes and stays established. `state=Established`, `signal_bars=4`,
`sig_sent=18;sig_recv=18` holding equal across the whole call, microphone capture at
normal levels, and — decisively — peer-driven `remote_audio` `Muted` → `Active`
transitions. Those transitions travel in-band over the UDP encrypted connection, so both
the TDLib-relayed signalling channel and the encrypted media transport demonstrably work
bidirectionally with iOS. Yet `playout_peak_permille=0` on every sample, in both
directions, while `playout_windows` keeps incrementing: the render callback is firing and
the mixer is returning silence. The fault is confined to the audio RTP path.

### The instrument

From the playout buffer, three very different failures look identical — no media arrives,
media arrives malformed, or media parses but no receive stream claims it. A new tgcalls
patch (`patches/tgcalls-m123-incoming-audio-counters.patch`) adds four relaxed atomic
counters to `MediaManager::receiveMessage`, which is where an `AudioDataMessage` is
unwrapped and handed to the WebRTC call receiver:

| Counter | Field | Incremented at |
| --- | --- | --- |
| `g_diagIncomingAudioRtp` | `rtp_in` | a packet parsed and was delivered to `DeliverRtpPacket` |
| `g_diagIncomingAudioRtcp` | `rtcp_in` | the packet was RTCP |
| `g_diagIncomingAudioParseFailed` | `rtp_bad` | `RtpPacketReceived::Parse` rejected it |
| `g_diagIncomingAudioUndemuxed` | `rtp_undemux` | it parsed but the demuxer matched no receive stream |

`rtp_undemux` is the undemuxable-packet callback that tgcalls previously discarded with a
bare `return false`. It is the one signal that separates a transport fault from an SSRC or
payload-type mismatch.

The facade declares the counters `extern` at **global** scope and qualifies every use as
`::tgcalls::`. Reopening `namespace tgcalls` from inside `Unigram::Native::Calls` declares
a nested namespace that shadows the real one; the first attempt did exactly that and
failed with `C2039: 'DefaultWrappedAudioDeviceModule': is not a member of
'Unigram::Native::Calls::'anonymous-namespace'::tgcalls'`. The counters are process-global,
so `Start()` zeroes them per call; otherwise the previous call's totals read as this one's.
Only packet counts cross the boundary — nothing derived from packet contents.

The values ride the existing `voip.media result=audio_level` line, which has its own
per-call budget of 192, and are integers so the diagnostics redaction layer preserves them.

### How to read the result

- `rtp_in == 0` and `rtcp_in == 0` — no audio media arrives at all. The encrypted control
  path works (proved above), so suspect the *unreliable* packet path in
  `EncryptedConnection` specifically, and MTU.
- `rtp_in == 0` with `rtp_bad > 0` — packets arrive but fail to parse; suspect header
  extension map disagreement.
- `rtp_in > 0` with `rtp_undemux > 0` — media arrives and parses but no receive stream
  claims it; suspect SSRC demux or payload type.
- `rtp_in > 0`, `rtp_undemux == 0`, playout still zero — delivery succeeds and the fault is
  in Opus decode or the mixer in the WinUWP WebRTC fork.

### Build-script change

`Build-ModernCallsBridgeProof.ps1` now iterates a list of tgcalls patches instead of
applying a single hard-coded one. The apply sequence is reverse-check (already applied →
skip), then a plain `git apply`, then `--3way` as recovery. `--3way` cannot be the first
attempt here: an earlier patch in the list leaves its file staged, and `--3way` then fails
the whole run with `does not match index`.

### Code-review fixes carried in this build

- `sig_recv` counted only directly delivered signalling messages. For an incoming call the
  peer's initial burst is buffered while the session starts and then flushed, so the bulk
  was never counted and the metric could read far too low — or zero while signalling was
  plainly arriving. `FlushPendingModernSignalingData` now counts what it flushes and
  reports it as `count=` on the `result=flushed` line.
- Budget reset moved from the `CallStateReady` branch to the first update carrying a new
  call id. Diagnostics begin well before Ready, so a call that is declined, errors, or is
  discarded while pending previously never reset at all — exactly the failures most worth
  recording.
- `ModernTdlibCompatibility`'s two budgets were also set once per process. Since
  `WriteAudioCallVersionDiagnostic` is the only sink for the negotiated-version line, it
  went permanently silent after roughly two dozen calls. Both are now replenished per call
  via `ResetAudioCallDiagnosticBudgets()`.
- First-occurrence signalling suppression is keyed on the call id rather than on the
  message counter. Keying it on the counter raced the per-call reset: a late increment from
  a torn-down call left the counter non-zero, so the next call's first message did not look
  like the first and the only per-message evidence was lost. Comparing call ids can at worst
  emit one extra line, which errs toward evidence.

### Review fixes applied before shipping

A review of the first cut found two defects that would have blunted the instrument itself:

- The counters were appended **last** to the native audio-device status, and that status is
  passed through `SanitizeErrorMessage`, whose default cap is 256 characters. The counters
  were therefore the first fields truncation would drop. They are now emitted **first**, and
  the managed call site raises the cap to 512. The cap bounds length, not redaction; every
  redaction rule still runs.
- The sanitizer also rewrites any run of six or more digits as `[redacted_number]`, so an
  unbounded `rtp_in` would erase itself after roughly half an hour of call. The counters are
  now saturated at 99999. The diagnostic question is whether media arrives and roughly how
  fast, not the exact total.
- `AudioDeviceStatus()` returned early with `created=0` when the audio device module was
  unavailable, dropping the counters entirely — and that is precisely the state where it
  matters whether RTP is arriving, since it separates "the device never came up" from "it
  came up and nothing feeds it". Both exits now carry the counters.
- The budget reset fired on any change of call id. TDLib interleaves late updates for a
  discarded call with the first updates of its successor, so each alternation restored the
  full budget during a call transition — the busiest logging window, and the one the cap
  exists to bound. The immediately preceding call id is now remembered and skipped.
- `git apply --3way` implies `--index`: on conflict it writes conflict markers into the
  working tree and records conflicted index stages, then fails. Nothing undid that, so every
  later build failed identically with no hint that the checkout needed resetting. The failure
  path now reverts the files the patch touches and names the checkout in the error.

Reviewed and found correct: the increment placement partitions every audio message exactly
once (`rtcp_in + rtp_bad + rtp_in`, with `rtp_undemux ⊆ rtp_in`) and `MediaManager::receiveMessage`
is the only audio receive path; the `extern` declarations bind correctly across the static-library
boundary; relaxed atomics cannot tear and inter-field skew cannot change a diagnosis; the
signalling flush path does not double-count, because a queued message returns before reaching
the direct-delivery increment. One interpretation caveat: `rtcp_in` is incremented outside the
worker-thread `BlockingCall`, so it means *arrived* rather than *delivered* — if that thread
ever wedges, `rtcp_in` climbs while `rtp_in` stays 0.

### Artifacts

| File | SHA-256 | Bytes |
| --- | --- | --- |
| `Unigram_26.9.6173.0_ARM_ModernTgCalls_RtpCounters.appx` | `689FA32D3CABC6F0ED48E166B77969FBE1B62F6DE64D13CAF17A17922D59A803` | 57,515,391 |
| `Unigram_26.9.6173.0_ARM_ModernTgCalls_RtpCounters_Sideload.zip` | `3AE5409BF7B269E0DD5968C16E7CD9F31FC6489ADBEEBEA08437856EF9B2FCDC` | 64,123,468 |
| `Unigram_26.9.6173.0_ARM_ModernTgCalls_RtpCounters.cer` | `5D891C3D3F5DF85A556C01BD5BA58C6837A736776E4D781CDEE9790060A72B85` | 832 |
| `ModernCallsBridge_26.9.6173.0.map` | `F75BB0E0EB6172314B2E5BCFF971B66420E2D7A335A276A78798AC3D46E5ED3A` | 14,871,524 |

Verified: signature chains to `DC409D7A-979D-42E5-AAA5-E9A0F674260F`; identity
`49197Wirdschon.UnigramMobileTdlibExperimental`; version `26.9.6173.0`; architecture `arm`;
background entry point `Unigram.Native.Tasks.NotificationTask`; 554 payload entries; no
source, PFX or PDB in the package; packaged `ModernCallsBridge.dll` hash matches the built
binary (`76C515B6…`); ZIP contains exactly 23 entries.

26.9.6172.0 was built and verified but superseded by the review fixes above before it was
ever handed over, so it was discarded rather than shipped; no two binaries share a version.

## 26.9.6175.0 — iOS RTP isolated; speakerphone routing fixed; RTCP-kind probe

### What 26.9.6173.0 proved about iOS

The incoming-RTP counter shipped in 6173 answered the iOS question outright.

| Call | Peer | `rtp_in` | `rtcp_in` | `rtp_bad` | `rtp_undemux` | `playout_peak_permille` |
| --- | --- | --- | --- | --- | --- | --- |
| outgoing | iOS | **0** | 20 | 0 | 0 | 0 |
| incoming | iOS | **0** | 19 to 100 | 0 | 0 | 0 |
| outgoing | Android | 52 to 368 | 20 to 163 | 0 | 0 | 79/355/385/267 |

RTP and RTCP from the peer arrive over the identical `AudioDataMessage` path, so a
non-zero `rtcp_in` with `rtp_in=0` proves transport, decryption and deserialization all
work against iOS. The transport is **not** the fault. Misclassification is ruled out by
rate: `rtcp_in` climbs at the same ~4/sec in both calls, where RTP counted as RTCP would
make the iOS figure ~3x the Android one. `rtp_bad=0` and `rtp_undemux=0` rule out
header-extension-map disagreement and SSRC/payload-type demux failure. iOS simply sends
no audio RTP.

### The probe added in 26.9.6175.0

Five counters in `MediaManager.cpp` narrow this to a side:

| Key | Meaning |
| --- | --- |
| `rtcp_sr` | Incoming RTCP Sender Reports (PT 200). Only a peer with an active **send** stream emits these. |
| `rtcp_fb` | Incoming transport feedback (PT 205/206). Only a peer that is **receiving** our media emits these. |
| `rtp_out` / `rtcp_out` | Outgoing audio RTP/RTCP, counted in `NetworkInterfaceImpl::sendTransportMessage` under `!_isVideo`. |
| `msg_max` | Largest incoming audio message seen, to detect a transport silently dropping large packets. |

`diagScanIncomingAudioRtcp` walks compound RTCP block by block rather than reading only
the first header, so a Sender Report bundled behind a Receiver Report is still counted.

Decision table for the next log:

- `rtcp_sr>0` — iOS *is* sending; the loss is on our receive/decrypt path.
- `rtcp_sr=0, rtcp_fb>0` — iOS is not sending but *is* receiving us; fault is the iOS-side send stream.
- `rtcp_sr=0, rtcp_fb=0` — the session is half-dead in both directions.

The 6174 key `rtp_max` was renamed `msg_max` because it counts **all** incoming audio
messages, not just RTP. Left as `rtp_max` it would have read non-zero in exactly the iOS
case (`rtp_in=0`) and invited the opposite conclusion.

### The speakerphone fix — and the `GlyphToggleButton` trap

`Routing` in `VoIPPage.xaml` is a `controls:GlyphToggleButton`, **not** a stock
`ToggleButton`. Its `OnToggle()` override (`GlyphToggleButton.cs:104-117`) calls
`base.OnToggle()` only when `IsOneWay == false`, or when `IsOneWay == true` *and*
`GetBindingExpression(IsCheckedProperty)` is a `TwoWay` binding. `IsOneWayProperty`
defaults to **`true`**, and `VoIPPage.xaml:319` sets neither — so **`IsChecked` does not
flip when the button is clicked**. Compare `Mute` at `:276-278`, which sets both.

Never assume stock `ToggleButton` semantics for this control. Any handler must own the
checked state explicitly.

The real defect: `AudioRoutingManager.GetAudioEndpoint()` reports `Speakerphone` at
`routing_ready` on this handset while playout is audibly in the **earpiece** — the
manager's state and the hardware disagree from the start, so the toggle began every call
out of step and the first press appeared to do nothing. The fix:

- `OnLoaded` now **asserts** the starting endpoint via `ApplyAudioEndpoint(..., "ready")`
  rather than merely reading it.
- `ApplyAudioEndpoint` re-reads `GetAudioEndpoint()` after every `SetAudioEndpoint` and
  assigns `Routing.IsChecked` from the **actual** endpoint, so the glyph cannot drift.
- `SelectPrivateAudioEndpoint` picks Bluetooth when available, otherwise Earpiece.
- `voip.ui result=routing_changed;stage=;requested=;actual=` makes a silently-refused
  switch visible; `result=routing_failed` means `SetAudioEndpoint` threw.

The render stream is tagged `AudioCategory_Communications` (see
`patches/webrtc-m123-winuwp-arm-render-communications-category.patch`), so
`AudioRoutingManager` is the correct lever here.

`DescribePlayoutDevices` now emits `index:kind:name` triples (`ear`/`spk`/`hs`/`oth`).
Because `:` is not a word character, the short kind token survives the diagnostics
sanitizer even when the long device name is redacted — this gives the ADM index-to-endpoint
mapping as a fallback lever if `AudioRoutingManager` turns out not to be honoured.

### Artifacts

Under `%LOCALAPPDATA%\UnigramTdlibExperiment\artifacts\`:

| File | SHA-256 | Bytes |
| --- | --- | --- |
| `Unigram_26.9.6175.0_ARM_ModernTgCalls_RoutingFix_RtcpProbe_Sideload.zip` | `9CAC4CEFA9993E560DD565B8B60F7C7EC0B95C66A61EAE196275F930B143BB4F` | 64,136,550 |
| `Unigram_26.9.6175.0_ARM_ModernTgCalls_RoutingFix_RtcpProbe.appx` | `91151A0F71C632D5B4F00C0BEF1F9928A9AC7056DDB988C95FC4C9BE9D6BD322` | 57,519,108 |
| `Unigram_26.9.6175.0_ARM_ModernTgCalls_RoutingFix_RtcpProbe.cer` | `5D891C3D3F5DF85A556C01BD5BA58C6837A736776E4D781CDEE9790060A72B85` | 832 |
| `ModernCallsBridge_26.9.6175.0.map` | `3A4D9BB3CFB151A5B37955E4FBD57E5B588C2111A6AD0AC28F20EF7B9BDCE322` | 14,877,819 |

Packaged `ModernCallsBridge.dll`: `D23488110F0AF8318A94D8378CF02C30DF15A9A2AB660C3CCF0B6F4F46D9A7B5`
(parity with the built bridge: True).

Verification: signature OK; identity `49197Wirdschon.UnigramMobileTdlibExperimental`;
version `26.9.6175.0`; architecture `arm`; background entry point
`Unigram.Native.Tasks.NotificationTask` with `<Task Type="pushNotification" />`; display
name `Unigram Mobile TDLib Experimental`; 554 payload entries; 0 forbidden files; 23-entry ZIP.

> 26.9.6174.0 was built and then discarded before shipping: a code review caught that the
> first attempt at the routing fix, written on the stock-`ToggleButton` assumption, left the
> button completely inert. No 26.9.6174.0 binary was ever handed over.

## 26.9.6176.0 — upstream Unigram output-device integration

### Upstream comparison

The local UI-routing workaround was checked against the current upstream
[Unigram](https://github.com/UnigramDev/Unigram) source before adding another guess.
The comparison source is upstream `develop` at `b6eeb455251aa34cda8ba2256679cecf1fec4a03`
(2026-09-24):

- `Telegram.Native.Calls/VoipManager.cpp` constructs
  `tgcalls::MediaDevicesConfig` with `audioInputId` and `audioOutputId`.
- `Telegram.Native.Calls/VoipManager.idl` exposes `SetAudioOutputDevice(String id)`.
- `VoipManager::SetAudioOutputDevice` forwards that value to
  `tgcalls::Instance::setAudioOutputDevice`.
- `Telegram/Services/Calls/VoipCall.cs` forwards output changes to that native method.

The experimental bridge already owned a `tgcalls::Instance`, but had exposed only mute,
network and diagnostics controls—there was no output-device control at all. 6176 adds
that missing integration through the same `setAudioOutputDevice` path.

On Windows 10 Mobile this TgCalls fork accepts a `#<index>` selector. The bridge learns
the first classified earpiece and loudspeaker indices while it performs the *existing*
bounded `DescribePlayoutDevices` enumeration at ADM construction; no UI-thread hardware
enumeration is added. A rejected `AudioRoutingManager` request now queues the classified
endpoint through TgCalls' media queue, which stops/reinitializes/restarts playout around
the indexed selection. The page retains that queued intent so a subsequent ineffective
`AudioEndpointChanged` notification cannot flip the glyph back.

Expected diagnostics for a successful loudspeaker request:

```text
voip.media|result=audio_output;...;requested=speakerphone;queued=1;native=queued;target=speakerphone;index=1
voip.media|result=audio_device;...;step=set_playout_device;phase=select;index=1
voip.media|result=audio_device;...;step=set_playout_device;phase=end;faulted=0;code=0
voip.ui|result=routing_changed;...;native_queued=1
```

`queued=0;native=unavailable` means the bounded enumeration could not classify the target
on that hardware; no guessed ordinal is sent. A non-zero `code` from
`set_playout_device` means the platform rejected the selection.

### This does not fix the iOS media failure

The 6175 instrumentation now provides the exact answer for the iOS call:

```text
iOS:     rtp_out=175; rtcp_fb=79; rtcp_sr=0; rtp_in=0; playout_peak_permille=0
Android: rtp_out=224; rtcp_fb=100; rtcp_sr=5; rtp_in=228; playout_peak_permille>0
```

The iOS peer acknowledges our transport packets (`rtcp_fb`) but emits no Sender Report
and no audio RTP. The W10M app therefore has no incoming audio to route or decode. This
is separate from its physical output issue.

Upstream's submodule is `TelegramMessenger/tgcalls` at
`ec56af8daaed387ae8e522e28eae96c167431f49`. Comparing the experimental base
`1c236c09f8d8569fead14bd68000618a52051225` with that revision shows exactly two
upstream-only files: `tgcalls/platform/uwp/UwpScreenCapturer.cpp` and `.h`, from
“Improve the UWP screen capturer.” All upstream iOS audio-device changes predate the
experimental base, so bringing this submodule forward cannot fix an iOS peer that does
not originate RTP.

### Artifacts

Under `%LOCALAPPDATA%\UnigramTdlibExperiment\artifacts\`:

| File | SHA-256 | Bytes |
| --- | --- | --- |
| `Unigram_26.9.6176.0_ARM_UpstreamOutputRouting_Sideload.zip` | `E112A4696BDD7AB8C250EE624F27211363FE5203E92D9FA6996EBB1402E607CF` | 64,123,079 |
| `Unigram_26.9.6176.0_ARM_UpstreamOutputRouting.appx` | `CE8C18F8CEEA5601747121EBD311600B09E6F5698284CE6982AEFBB9AC1CE5B7` | 57,514,409 |
| `Unigram_26.9.6176.0_ARM_UpstreamOutputRouting.cer` | `5D891C3D3F5DF85A556C01BD5BA58C6837A736776E4D781CDEE9790060A72B85` | 832 |
| `ModernCallsBridge_26.9.6176.0.map` | `A179D891A98A94DF44D343CEE5C6B34A76369CE8C045EAAAC62038D4DDD69BEF` | 14,888,551 |

Packaged `ModernCallsBridge.dll`: `C400754B32F88C862DA22BAE38827B61A74EC71E4C98792BB4EF7B542A6EEF49`
(parity with the built bridge: True).

Verification: signature OK; identity `49197Wirdschon.UnigramMobileTdlibExperimental`;
version `26.9.6176.0`; architecture `arm`; background entry point
`Unigram.Native.Tasks.NotificationTask` with `<Task Type="pushNotification" />`; display
name `Unigram Mobile TDLib Experimental`; 554 payload entries; 0 forbidden files; 23-entry ZIP.

## 26.9.6177.0 — AEC3 state and residual-echo telemetry

### Upstream baseline and platform result

The speakerphone echo report was checked directly against current upstream Unigram and
its pinned TgCalls source before changing any signal-processing setting. Upstream creates
the same `AudioOptions` and sender settings used by this build:

- echo cancellation enabled;
- noise suppression enabled;
- automatic gain control enabled on non-iOS targets.

The bridge also matches upstream's direct `setAudioOutputDevice` path introduced in
6176. There is no additional Windows Mobile AEC policy in upstream to transplant.

On this UWP ARM target, `AudioDeviceWindowsCore::BuiltInAECIsAvailable()` is false, so
the active canceller must be WebRTC software AEC3. Forcing WebRTC's `mobile_mode` would
replace AEC3 with the older AECM implementation; it is deliberately not used. Both
capture and render streams are already tagged `AudioCategory_Communications` by the
existing UWP patch. The diagnostics wrapper was also checked to forward both capture
overloads and both render callbacks, preserving the AEC render reference.

### Telemetry added

The reproducible `tgcalls-m123-incoming-audio-counters.patch` now retains the
already-created WebRTC `AudioProcessing` reference in `MediaManager`, samples its
configuration and aggregate statistics on TgCalls' existing two-second worker-thread
statistics cadence, then releases the reference during media-engine teardown.

`voip.media result=audio_device` status now adds these compact fields:

| Field | Meaning |
| --- | --- |
| `apm` | `aec3`, `aecm`, `off`, or `pending`; this identifies the actual configured echo canceller. |
| `apm_ns`, `apm_agc` | Active noise suppression and gain-control configuration (`1`/`0`). |
| `aec_residual_pm`, `aec_residual_max_pm` | Current/recent residual-echo likelihood, quantized to 0–1000. Higher values during speakerphone use mean the canceller detects remaining echo. |
| `aec_erl_db10`, `aec_erle_db10` | Echo-return loss and enhancement in tenths of a decibel. |
| `aec_delay_ms` | AEC's current delay estimate. |

These values are aggregate DSP health metrics. The patch never logs, stores, transmits,
or derives recoverable audio, PCM samples, device IDs/names, paths, message content,
account data, tokens, or credentials. It does not change routing, media packets, output
gain, or any AEC setting.

### Device-test interpretation

Make a normal Android call, switch from earpiece to speakerphone, and have the remote
party speak continuously for at least ten seconds before exporting diagnostics. A healthy
configuration should report `apm=aec3;apm_ns=1;apm_agc=1`. A high residual likelihood or
poor/absent enhancement while `apm=aec3` proves the handset's acoustic path is exceeding
software cancellation, which makes a bounded per-call speaker attenuation the appropriate
next mitigation. `apm=off`, `apm=aecm`, or `pending` instead identifies a configuration
or initialization fault to correct first.

The iOS peer-originated RTP failure remains unrelated: it is not an appropriate
acceptance test for this speakerphone/AEC diagnostic build.

### Artifacts

Under `%LOCALAPPDATA%\UnigramTdlibExperiment\artifacts\`:

| File | SHA-256 | Bytes |
| --- | --- | ---: |
| `Unigram_26.9.6177.0_ARM_AecTelemetry_Sideload.zip` | `12B6134F481AB8896A56551A08CCCB9B92A86F7F24F83F4DD093DA23B8C31EF6` | 64,122,788 |
| `Unigram_26.9.6177.0_ARM_AecTelemetry.appx` | `1ABD149803A732BD028FDBE69A9F2DF366836A94142231E4B9B4205E1AC86959` | 57,515,495 |
| `Unigram_26.9.6177.0_ARM_AecTelemetry.cer` | `5D891C3D3F5DF85A556C01BD5BA58C6837A736776E4D781CDEE9790060A72B85` | 832 |
| `ModernCallsBridge_26.9.6177.0.map` | `23B27CA18DA00D07E4D0FD70828B9E12B9251D46BF02B053C25A9C187460E96F` | 14,898,629 |

The packaged `ModernCallsBridge.dll` SHA-256 is
`865DAA2670626F50AD20E9B8A5FADA8DAA4AC946CD9A1AD580497363DC860856`,
which matches the rebuilt native bridge exactly.

Verification: Release|ARM native bridge build and Release|ARM UWP APPX build succeeded;
APPX signature verification passed; identity
`49197Wirdschon.UnigramMobileTdlibExperimental`; version `26.9.6177.0`; architecture
`arm`; `Unigram.Native.Tasks.NotificationTask` declares `pushNotification`; 555 payload
entries; `Telegram.Td.dll`, `Telegram.Td.winmd`, and `ModernCallsBridge.dll` present; 0
source-secret/PFX/PDB payload entries; and the sideload ZIP contains exactly 23 entries
with four ARM dependency APPXs and no x86/x64/ARM64/Win32 or telemetry dependencies.

## 26.9.6178.0 — UWP speakerphone session-gain cap

The working indexed loudspeaker path added in 6176 exposed a physical acoustic loop on
the test handset. The audio device reports no built-in AEC, while software AEC3 remains
enabled; a loudspeaker session level of `100/100` leaves too little acoustic margin for
software cancellation to suppress feedback reliably.

This build applies a 50% cap only after TgCalls has queued the classified native
loudspeaker endpoint. The ordered `setAudioOutputDevice` and `setOutputVolume` commands
run on TgCalls' manager/media queues, so the gain is applied after playout has restarted
on the requested speaker. It is not issued for the routing manager path alone.

The external patch enables `MediaManager::setOutputVolume` only under `WINUWP`. It reads
and saves the app session's original `ISimpleAudioVolume` level once, scales that saved
level by 0.5 for native speakerphone use, and restores it when switching to earpiece or
when the media manager tears down. It does not alter hardware or device-wide volume.
Non-UWP TgCalls builds retain the upstream no-op behavior. If platform volume control is
unavailable or rejects a call, the route is retained and the failure is reported rather
than silently treating the cap as applied.

Expected privacy-safe diagnostics are:

```text
voip.media|result=audio_output;...;native=queued;target=speakerphone;index=1;gain=50
voip.media|result=audio_device;...;step=set_speaker_volume;phase=select;level=50
voip.media|result=audio_device;...;step=set_speaker_volume;phase=end;faulted=0;code=0
```

`gain=50` confirms the ordered request was queued; the final `set_speaker_volume` result
is the platform acceptance evidence. The numeric level is an app-session gain only; no
device name, identifier, audio, credentials, message content, account data, token, or
path is logged.

Device acceptance requires an Android-to-W10M call because Android is the known
bidirectional-media peer. Test earpiece, switch to speakerphone, have the Android peer
speak for at least ten seconds, then verify intelligible remote audio, working uplink,
and reduced echo/feedback. Export diagnostics with the output-gain result plus
`apm`, `aec_residual_pm`, `aec_residual_max_pm`, `aec_erle_db10`, and `aec_delay_ms`.
The unresolved iOS no-RTP condition remains separate and is not an acceptance test for
this cap.

### Artifacts

Under `%LOCALAPPDATA%\UnigramTdlibExperiment\artifacts\`:

| File | SHA-256 | Bytes |
| --- | --- | ---: |
| `Unigram_26.9.6178.0_ARM_SpeakerGain_Sideload.zip` | `A5BE95448D912186ABC2F4443AE8D9A6F43AC23AE498B84E543C1D4E2EF7EC4E` | 64,123,511 |
| `Unigram_26.9.6178.0_ARM_SpeakerGain.appx` | `4AE148E13D5E63F7B568BB7B8225644E46A27D8470F8D4A28A81759F5AD1A7A3` | 57,516,116 |
| `Unigram_26.9.6178.0_ARM_SpeakerGain.cer` | `5D891C3D3F5DF85A556C01BD5BA58C6837A736776E4D781CDEE9790060A72B85` | 832 |
| `ModernCallsBridge_26.9.6178.0.map` | `12692273E737E8F644184390C3C656A6459F36B5616DEBFCFFE45BDF637CDB8A` | 14,901,517 |

The packaged `ModernCallsBridge.dll` SHA-256 is
`6218340E91A3F30D20F6B49D114D7E824351738E2336658F9C12C4359E74C31E`,
which matches the bridge rebuilt from the pinned external source exactly.

Verification: the Release|ARM native engine and C++/CX bridge builds both completed with
zero errors; the Release|ARM UWP APPX build completed successfully; and Authenticode
signature verification passed. The APPX has identity
`49197Wirdschon.UnigramMobileTdlibExperimental`, version `26.9.6178.0`, architecture
`arm`, and the `Unigram.Native.Tasks.NotificationTask` `pushNotification` entry point.
It has 555 payload entries, includes `Telegram.Td.dll`, `Telegram.Td.winmd`, and
`ModernCallsBridge.dll`, and contains no source-secret, PFX, or PDB payload. The
sideload ZIP has 23 entries: the installer scripts/resources, the APPX/certificate, and
exactly four ARM dependency APPXs—no x86, x64, ARM64, Win32, or telemetry dependency.

## 26.9.6179.0 — upstream V1 relay-contract alignment

The iOS media investigation was re-based on the current upstream Unigram `develop`
implementation at `b6eeb455251aa34cda8ba2256679cecf1fec4a03`, not on a guessed
Windows Mobile-specific media policy. Its `Telegram.Native.Calls/VoipManager.cpp` uses
the same V1 `tgcalls::InstanceImpl` transport that this experimental bridge creates for
negotiated `5.0.0` calls.

The comparison exposed three concrete relay mapping differences:

- upstream sets `allowTCP = false`; the experimental bridge was allowing TCP;
- upstream assigns each Telegram reflector its sorted, zero-based `RtcServer.id`; the
  bridge assigned the same reflectors one-based IDs;
- upstream registers both non-empty IPv4 and IPv6 WebRTC server addresses, adding STUN
  entries first and TURN entries only when both credentials are supplied. The bridge
  previously collapsed every server to a single preferred address.

6179 applies those exact V1 transport rules. These values feed TgCalls'
`ReflectorRelayPortFactory`; they are not audio-processing or speakerphone changes.
Android can hide an incorrect relay map when a direct route succeeds, whereas an iOS
peer or its network may require the correctly identified/addressed relay route. This is
therefore a focused compatibility correction, not a claim that the absent iOS RTP is
already resolved.

The V1 manager consumes the normalized `RtcServer` list and does not consume the
legacy endpoint list, so 6179 deliberately leaves the bridge's legacy endpoint
diagnostic/validation surface unchanged. The current upstream manager supplies that
legacy list empty for the same reason. No server hostname, address, credential, peer
tag, account data, signaling data, key, token, or audio is written to diagnostics.

Device acceptance is an iOS-to-W10M call in both directions, on a network where the
iOS peer previously connected without audio. Keep the call open for at least ten
seconds of speech each way and export diagnostics. A successful result requires
non-zero incoming RTP and playout on W10M as well as working W10M uplink; an established
transport or RTCP feedback alone is insufficient evidence.

### Artifacts

Under `%LOCALAPPDATA%\UnigramTdlibExperiment\artifacts\`:

| File | SHA-256 | Bytes |
| --- | --- | ---: |
| `Unigram_26.9.6179.0_ARM_IosRelayAlignment_Sideload.zip` | `FA157BED2E532CAACA1C901F35686D9A0855A3A6F75B070A5B8892351F5E6101` | 64,143,964 |
| `Unigram_26.9.6179.0_ARM_IosRelayAlignment.appx` | `FD54958C974280531E30AC129AFBD37731D75D2268072805F798FF21D684C5B9` | 57,531,994 |
| `Unigram_26.9.6179.0_ARM_IosRelayAlignment.cer` | `5D891C3D3F5DF85A556C01BD5BA58C6837A736776E4D781CDEE9790060A72B85` | 832 |
| `ModernCallsBridge_26.9.6179.0.map` | `8DB994C46B1ACF8D09526565AC87F9A5C83A3DA6886E13D73E7AF87166414A4A` | 14,901,517 |

The package and rebuilt `ModernCallsBridge.dll` SHA-256 values both equal
`9383B3DF4ACDC4A3C2BD0C32BF63C1DCEC3B6346D0DB06106ACA2D3F2DC41DF1`.

Verification: the Release|ARM UWP APPX build completed successfully and signature
verification passed. The APPX identity is
`49197Wirdschon.UnigramMobileTdlibExperimental`; version `26.9.6179.0`;
architecture `arm`; and `Unigram.Native.Tasks.NotificationTask` declares
`pushNotification`. Its 555 payload entries include `Telegram.Td.dll`,
`Telegram.Td.winmd`, and `ModernCallsBridge.dll` with no source-secret, PFX, or PDB
payload. The sideload ZIP contains exactly 23 entries: installer scripts/resources, the
APPX/certificate, and four ARM dependency APPXs with no x86, x64, ARM64, Win32, or
telemetry dependencies.

## 26.9.6180.0 — upstream V2 protocol support for iOS compatibility

The 6179 iOS acceptance run confirmed that the corrected V1 relay mapping alone was
not sufficient. In both iOS-to-W10M and W10M-to-iOS calls, the W10M bridge reached
`Established`, reported active recording/playout, emitted RTP, and received RTCP
feedback; however, incoming RTP and RTCP sender reports remained zero. This isolates
the failure above UWP capture, rendering, routing, and V1 relay normalization.

The previous bridge registered only upstream TgCalls `InstanceImpl`, advertising V0/V1
versions `2.7.7` and `5.0.0`. Current upstream Unigram also registers
`InstanceV2Impl` and `InstanceV2ReferenceImpl`; excluding them forced a newer iOS peer
to the older V1 fallback even though the pinned source contains its current protocol
implementations.

6180 mirrors upstream's registration:

| Native implementation | Advertised versions |
| --- | --- |
| `InstanceImpl` | `5.0.0`, `2.7.7` |
| `InstanceV2Impl` | `13.0.0`, `12.0.0`, `9.0.0`, `8.0.0`, `7.0.0` |
| `InstanceV2ReferenceImpl` | `11.0.0`, `10.0.0` |

The managed offer is newest-first (`13.0.0` through `2.7.7`), and still reads only
TDLib's negotiated `LibraryVersions[0]`; it never substitutes a locally supported
version. The established `CallProtocol` connection layer remains the legacy
controller's value of 92. No V1 routing, audio-device, AEC, codec, or speakerphone
behavior was changed.

The ARM native project now compiles the required upstream V2 signaling, ICE, SCTP, and
content-negotiation source closure. V2 compressed signaling is linked against the
already packaged, pinned ARM UWP dynamic zlib `1.3.2` import library from the TDLib
build, not Chromium's bundled `1.3.0.1-motley` zlib. The bridge proof script validates
the zlib header version and `z.lib` before either native project builds. `z.dll` is
already a required TDLib payload and is now also the V2 bridge's import; `zlib1.dll`
remains the same pinned bytes staged solely for RLottie's legacy import name.

This is a protocol-compatibility build, not a claim that iOS media is fixed. For device
acceptance, install 6180 and make one iOS-to-W10M and one W10M-to-iOS call with at
least ten seconds of speech each way. The diagnostic must show a negotiated version in
the V2 range, non-zero W10M `rtp_in`, `rtcp_sr`, and `playout_peak_permille`, plus
audible W10M uplink on iOS. An established transport, RTCP feedback, or output RTP
alone is not success evidence. Diagnostics continue to contain only bounded aggregate
counts and state; they do not log signaling, keys, server data, accounts, tokens,
paths, or media.

### Artifacts

Under `%LOCALAPPDATA%\UnigramTdlibExperiment\artifacts\`:

| File | SHA-256 | Bytes |
| --- | --- | ---: |
| `Unigram_26.9.6180.0_ARM_UpstreamV2_IosCompatibility_Sideload.zip` | `EBF85D24F6EA9802DCFA161847548A5B858981E63C76B9AF1B28264949969D7D` | 65,009,201 |
| `Unigram_26.9.6180.0_ARM_UpstreamV2_IosCompatibility.appx` | `D1D9C56006FEE621A5F7E9DBAEF2909F5C30829B3026C369B864243D5961DD1D` | 58,397,792 |
| `Unigram_26.9.6180.0_ARM_UpstreamV2_IosCompatibility.cer` | `5D891C3D3F5DF85A556C01BD5BA58C6837A736776E4D781CDEE9790060A72B85` | 832 |
| `ModernCallsBridge_26.9.6180.0.map` | `8FFBF565B7D86CBE751AD0D25378759491E5CAA2C761286483945314704B4B0F` | 23,830,248 |

The packaged `ModernCallsBridge.dll` SHA-256 is
`51DB45866A29749111FE6F94C3AAAAF2AC96980210772273BB53C8619903172E`,
matching the rebuilt bridge. Verification: the hardened native proof completed with
zero errors; Release|ARM APPX build completed with zero errors; Authenticode status is
valid; identity is `49197Wirdschon.UnigramMobileTdlibExperimental`; version
`26.9.6180.0`; architecture `arm`; and `Unigram.Native.Tasks.NotificationTask`
declares `pushNotification`. The APPX contains 555 entries, including TDLib, the V2
bridge WinMD/DLL, `z.dll`, and `zlib1.dll`, with no source-secret, PFX, or PDB payload.
The sideload ZIP has exactly 23 entries: installer scripts/resources, the APPX and
certificate, and four ARM-only dependencies with no x86, x64, ARM64, Win32, or
telemetry content.

## 26.9.6181.0 — V2 receive-path diagnostics

6180 added the upstream V2 implementations and preserves the supplied Windows audio
device module for both `InstanceV2Impl` and `InstanceV2ReferenceImpl`. Its existing
aggregate RTP/RTCP counters, however, were attached only to the V1 `MediaManager`
receive path.

6181 also records bounded, content-free receive measurements at V2's
`RtpPacketReceived_n` and `OnRtcpPacketReceived_n` callbacks: aggregate incoming
RTP/RTCP counts, largest received packet, RTCP sender reports, and RTCP feedback. The
V1 path continues to use the shared helpers and preserves malformed-RTP and undemuxed
counters. No packet payload, ID, address, signaling, key, account, token, or path is
stored or logged.

V2 device acceptance later established an important diagnostic boundary: version
`8.0.0` delivered non-silent W10M playout while the V2 RTP callback counter remained
zero. Therefore V2 `rtp_in` is supplementary transport evidence, not a condition for
success. The acceptance criteria are negotiated V2, active recording/playout,
non-silent `playout_peak_permille`, and audible media in both directions. Outbound V2
packet counts remain intentionally unspecified.

### Artifacts

Under `%LOCALAPPDATA%\UnigramTdlibExperiment\artifacts\`:

| File | SHA-256 | Bytes |
| --- | --- | ---: |
| `Unigram_26.9.6181.0_ARM_UpstreamV2Diagnostics_Sideload.zip` | `D14555DD9721BBDC01874F54B948E83D949C2A146DAE3CC72C30139F6048E712` | 65,011,572 |
| `Unigram_26.9.6181.0_ARM_UpstreamV2Diagnostics.appx` | `5D924C1EEF6EEE7EAB28182E5D761A841E89B360019C3EA17CB25C89DBD70BC0` | 58,401,563 |
| `Unigram_26.9.6181.0_ARM_UpstreamV2Diagnostics.cer` | `5D891C3D3F5DF85A556C01BD5BA58C6837A736776E4D781CDEE9790060A72B85` | 832 |

The rebuilt/package-matching bridge SHA-256 is
`7F51413CB18E856762E7D7D3C9AD95514FF8FC5BBA0286482206C0C6E7345275`.
The non-package map at
`%LOCALAPPDATA%\UnigramTdlibExperiment\bridge-probe\ModernCallsBridge.map` has SHA-256
`4C5A46243648270D0BC828BEE2E921B7B2ED3846E92CF8DC8F120A2F1D0B7C82`
and 23,830,502 bytes.

Verification: the hardened native bridge proof and the Release|ARM APPX build completed
with zero errors; Authenticode status is valid; manifest identity is
`49197Wirdschon.UnigramMobileTdlibExperimental`; version `26.9.6181.0`; architecture
`arm`; and the push background entry point remains present. The APPX has 554 entries
including TDLib, the V2 bridge WinMD/DLL, `z.dll`, and `zlib1.dll`, with no
source-secret, PFX, or PDB payload. The sideload ZIP has exactly 23 entries and contains
only installer resources, the APPX/certificate, and four ARM dependency APPXs.

## 26.9.6182.0 — privacy-safe capture evidence

The audio-device transport wrapper already reported a bounded playout peak. 6182 adds
the matching capture-side aggregate: a two-second microphone peak amplitude, sample
rate, and completed-window count. The wrapper reads 16-bit frames only long enough to
compute a peak and forwards them immediately; it does not retain, serialize, or log
audio samples. `capture_peak_permille` therefore confirms that the local W10M capture
callback is receiving non-silent input without exposing message or call content.

### Device acceptance result

The supplied W10M/iOS device diagnostics confirmed the upstream V2 fix. Two calls
negotiated `8.0.0`, reached `Established`, and reported active remote audio. W10M
reported `recording=1`, `playing=1`, and non-silent playout peaks (including 560, 538,
759, 582, and 301 permille at 16 kHz). RTCP sender-report and feedback totals increased
through each call. The tester confirmed audible media in both iOS-to-W10M and
W10M-to-iOS directions and confirmed speakerphone behavior is resolved. The log contains
no V2 RTP callback increments, which is expected under the diagnostic boundary above
and does not contradict the observed decoded playout.

The subsequent 6182 cross-platform test confirmed successful calls to and from
experimental W10M, iOS, and Android clients. Its privacy-safe log includes two further
V2 `8.0.0` negotiations, three established transitions, nine non-silent playout
windows, and twelve nonzero local-capture windows. Speakerphone was also confirmed
working during this test.

### Artifacts

Under `%LOCALAPPDATA%\UnigramTdlibExperiment\artifacts\`:

| File | SHA-256 | Bytes |
| --- | --- | ---: |
| `Unigram_26.9.6182.0_ARM_CaptureDiagnostics_Sideload.zip` | `48B5EC6BDD35AD0F57763F24C27F98BB5FEBEDCB2635D26A90DC3C35AA72F830` | 65,011,789 |
| `Unigram_26.9.6182.0_ARM_CaptureDiagnostics.appx` | `6C78FDB90EA0D08B8FC407AC28A845F363FD88E9C563948159B8CE1305DA888C` | 58,401,682 |
| `Unigram_26.9.6182.0_ARM_CaptureDiagnostics.cer` | `5D891C3D3F5DF85A556C01BD5BA58C6837A736776E4D781CDEE9790060A72B85` | 832 |

The rebuilt/package-matching bridge SHA-256 is
`58133AE47C515C3E9D484210583C3DF98FA7B90C3CD0FF73BF1CA7FCA145F6C2`.
The non-package map at
`%LOCALAPPDATA%\UnigramTdlibExperiment\bridge-probe\ModernCallsBridge.map` has SHA-256
`7891C0EBC07F147F7085786872ED0BE892B8AC9ABFFABCD1ABE386FA404F4ACD`
and 23,833,795 bytes.

Verification: the hardened native bridge proof and Release|ARM APPX build completed
with zero errors; Authenticode status is valid; manifest identity remains
`49197Wirdschon.UnigramMobileTdlibExperimental`; version `26.9.6182.0`; architecture
`arm`; and the push background entry point remains present. The APPX has 554 entries
including TDLib, the V2 bridge WinMD/DLL, `z.dll`, and `zlib1.dll`, with no
source-secret, PFX, or PDB payload. The sideload ZIP has exactly 23 entries and contains
only installer resources, the APPX/certificate, and four ARM dependency APPXs.

## 26.9.6228.0 — UWP capture-initialization diagnostics

The Alcatel Idol 4S `6227` trace proved that microphone consent and managed
pre-acquisition succeed, but native `InitRecording()` returns `-1` before capture
starts. This build does not change capture behavior or remove the per-call circuit
breaker. It adds numeric, privacy-safe failure telemetry to distinguish the failing
UWP audio-client operation.

`webrtc-m123-winuwp-arm-capture-init-diagnostics.patch` initializes the capture
diagnostic state to stage `1` with `E_FAIL`, then records these stages:

| Stage | Native operation |
| ---: | --- |
| 6 | `IAudioClient::GetMixFormat` |
| 7 | PCM format selection |
| 8 | `IAudioClient::Initialize` |
| 9 | `IAudioClient::SetEventHandle` |
| 10 | `IAudioClient::GetService(IAudioCaptureClient)` |
| 0 | Capture initialization succeeded |

On failure the bridge writes
`step=init_recording;phase=platform_failure;stage=<n>;hresult=<signed-number>`.
The trace contains only a numeric stage and HRESULT; it contains no endpoint name,
device ID, path, token, account data, or captured audio. The reproducible ARM WebRTC
build script applies the patch after the existing communications-category and bounded
media-capture patches.

### Device test

Install the experimental ARM package and its four ARM dependencies, make one short
audio call from the Idol to iOS, and export Push diagnostics. The requested evidence is
the `platform_failure` line above (if capture still fails) and whether the iOS caller
can hear the Idol. Use the reported stage/HRESULT to choose a compatible capture-format
or UWP-audio fix; do not add a speculative fallback.

### Artifacts

The signed APPX is at
`Unigram\Unigram\AppPackages\Unigram_26.9.6228.0_ARM_Test\`. The minimal sideload ZIP
is under `%LOCALAPPDATA%\UnigramTdlibExperiment\artifacts\` and contains exactly the
APPX, certificate, and four ARM dependency APPXs.

| File | SHA-256 | Bytes |
| --- | --- | ---: |
| `Unigram_26.9.6228.0_ARM_CaptureInitDiagnostics_Sideload.zip` | `64AB71A20B9E688052EC6A4E0D49917BB2DEF7B9E6A09F423E93EFFCF39F4B08` | 65,096,435 |
| `Unigram_26.9.6228.0_ARM.appx` | `9EE700B63A98CCF70C9132138432EA2BB28AB68DC56E2ECA5C1B8B88075208F4` | 58,551,116 |
| `Unigram_26.9.6228.0_ARM.cer` | `5D891C3D3F5DF85A556C01BD5BA58C6837A736776E4D781CDEE9790060A72B85` | 832 |

Verification: the ARM WebRTC rebuild, native bridge proof, and Release|ARM APPX
rebuild completed with zero errors. The APPX signature is valid for
`CN=DC409D7A-979D-42E5-AAA5-E9A0F674260F`; its manifest retains identity
`49197Wirdschon.UnigramMobileTdlibExperimental`, version `26.9.6228.0`, ARM
architecture, `Unigram.App` entry point, TDLib WinMD/DLL, and
`ModernCallsBridge.dll`. The package has no source-secret or PFX payload, and the
sideload ZIP has no PFX, PDB, APPXSYM, or source-secret content.

## 26.9.6229.0 — audio calls no longer show video preview

The supplied `6228` audio-call diagnostic showed `video=false`, followed by repeated
attempts to attach local and remote video outputs. The local composition host is the
lower-right preview panel, so those attempts made an empty preview visible during an
audio-only call. They also failed with `0x80070057`.

`VoIPService` now attaches modern video outputs only when the active call is a video
call. `VoIPPage` separately refuses audio-call composition visuals and clears either
video host when it receives an audio-call update. These three guards preserve local and
remote preview behavior for actual video calls while preventing stale or accidental
video preview UI on audio calls.

This package also contains the capture-initialization diagnostic bridge. Unlike `6228`,
the packaged `ModernCallsBridge.dll` was verified to contain both
`phase=platform_failure` and `stage=`. If microphone initialization again returns
`-1`, its Push diagnostics must include the numeric platform stage and HRESULT needed
for the next native fix.

### Device test

Install the experimental `6229` package, place an audio call to iOS, and verify that
there is no lower-right video-preview panel. Export Push diagnostics and provide the
single `step=init_recording;phase=platform_failure;stage=<n>;hresult=<n>` line if the
iOS peer still cannot hear the Idol. A video call should still show the local preview.

### Artifacts

Under `%LOCALAPPDATA%\UnigramTdlibExperiment\artifacts\`:

| File | SHA-256 | Bytes |
| --- | --- | ---: |
| `Unigram_26.9.6229.0_ARM_AudioPreviewAndCaptureDiagnostics_Sideload.zip` | `EC1681461B10C639903F1F0C5B61113FC721040DF5E3EC9BE9A6DCA6CFF83175` | 65,097,804 |
| `Unigram_26.9.6229.0_ARM.appx` | `A1F2A35AB0202FA2CBB23DC64877C0EDC9D68AE4B85729E22FC65C2E07EB43A6` | 58,550,207 |
| `Unigram_26.9.6229.0_ARM.cer` | `5D891C3D3F5DF85A556C01BD5BA58C6837A736776E4D781CDEE9790060A72B85` | 832 |

Verification: the Release|ARM build completed with zero errors; the APPX signature is
valid; its experimental identity is
`49197Wirdschon.UnigramMobileTdlibExperimental`; its version is `26.9.6229.0`; and
its architecture is ARM. The six-file sideload ZIP contains the APPX, certificate, and
four ARM dependencies only, with no PFX, PDB, APPXSYM, or source-secret payload.

## 26.9.6230.0 — capture HRESULT preserved in diagnostics

The `6229` device test confirmed that audio-only calls no longer show a video preview
and that the local preview remains available for video calls. Its new capture telemetry
also located the microphone failure at stage `8`, `IAudioClient::Initialize`, on both
the Alcatel Idol 4S and Lumia 1520; the Lumia 950 XL captures successfully. This
identifies endpoint stream compatibility rather than a manufacturer-wide device-ID
mapping problem.

The stage `8` HRESULT was originally written as a signed decimal value. The diagnostics
sanitizer correctly redacts long decimal sequences, so `6230` changes only its textual
representation to the existing privacy-safe hexadecimal convention:
`hresult=0xXXXXXXXX`. The value remains the same HRESULT, now preserved in exported
diagnostics without endpoint names, IDs, paths, credentials, tokens, or audio data.
No capture behavior changes in this build.

### Device test

Install the experimental `6230` package and make one short audio call from an affected
device. Export Push diagnostics and provide the single line:
`step=init_recording;phase=platform_failure;stage=8;hresult=0xXXXXXXXX`.
That exact HRESULT determines whether the next change is a supported PCM-format
fallback or a stream-flag compatibility adjustment.

### Artifacts

Under `%LOCALAPPDATA%\UnigramTdlibExperiment\artifacts\`:

| File | SHA-256 | Bytes |
| --- | --- | ---: |
| `Unigram_26.9.6230.0_ARM_CaptureHresultDiagnostics_Sideload.zip` | `D0D347540285E36D42D664BCA11330387F4628556BBB2D5268EFED370B6088C6` | 65,096,943 |
| `Unigram_26.9.6230.0_ARM.appx` | `0E1CE73BA53F21D30BA5FB5049D43BAC8B22658507A02EDA551DE99FB205D461` | 58,549,556 |
| `Unigram_26.9.6230.0_ARM.cer` | `5D891C3D3F5DF85A556C01BD5BA58C6837A736776E4D781CDEE9790060A72B85` | 832 |

Verification: the native bridge proof and Release|ARM package completed with zero
errors. The signature is valid for `CN=DC409D7A-979D-42E5-AAA5-E9A0F674260F`; the
manifest retains experimental identity
`49197Wirdschon.UnigramMobileTdlibExperimental`, version `26.9.6230.0`, and ARM
architecture. The signed payload includes the `phase=platform_failure` and `0x%08lX`
diagnostic strings; the sideload ZIP has exactly six allowed files and no PFX, PDB,
APPXSYM, or source-secret content.

## 26.9.6232.0 — shared-mode PCM capture conversion

The `6231` device trace still reports
`stage=8;hresult=0x88890008`, `AUDCLNT_E_UNSUPPORTED_FORMAT`, after trying the
endpoint's advertised 16-bit PCM mix descriptor. The endpoint is found and activated
correctly, but the Windows 10 Mobile audio client rejects all direct stream formats.

`webrtc-m123-winuwp-arm-capture-pcm-mix-fallback.patch` preserves the existing direct
16-bit PCM candidates. When none is accepted, it now requests a fixed 48 kHz,
mono, 16-bit PCM stream with `AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM` and
`AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY`. Windows performs its documented
shared-mode channel and sample-rate conversion to the endpoint. WebRTC still receives
only explicitly configured 16-bit PCM, so no float or non-PCM data is reinterpreted.
Failures in this conversion path report stage `11`, distinct from the direct stage
`8` path.

### Device test

Install `6232` on the Idol 4S or Lumia 1520 and make a short audio call. A successful
call must show `recording=1` and a non-pending `capture_peak_permille` in Push
diagnostics, while the remote party hears the device. The Lumia 950 XL remains the
regression device: it must continue to capture successfully.

### Artifacts

Under `%LOCALAPPDATA%\UnigramTdlibExperiment\artifacts\`:

| File | SHA-256 | Bytes |
| --- | --- | ---: |
| `Unigram_26.9.6232.0_ARM_SharedModePcmConversion_Sideload.zip` | `011D18ABEB043E0E464EE840C225854E91BCD921C3E0515A7E8C4850B1D71FD7` | 65,096,341 |
| `Unigram_26.9.6232.0_ARM.appx` | `B87B97AA45A96492A257AE1017C98C9717B686F049DDD2E465218CE74078D5F9` | 58,549,769 |
| `Unigram_26.9.6232.0_ARM.cer` | `5D891C3D3F5DF85A556C01BD5BA58C6837A736776E4D781CDEE9790060A72B85` | 832 |

Verification: the patched ARM WebRTC build, native bridge proof, and Release|ARM
package completed with zero errors. The APPX signature is valid; its manifest retains
experimental identity `49197Wirdschon.UnigramMobileTdlibExperimental`, version
`26.9.6232.0`, ARM architecture, and the push-notification background entry point.
The payload includes TDLib and the PCM conversion bridge, with no source-secret or PFX
content. The ZIP contains exactly the APPX, certificate, and four ARM dependencies.

## 26.9.6233.0 — initial call-route and echo-control alignment

Comparison against upstream Unigram at `b6eeb455251aa34cda8ba2256679cecf1fec4a03`
shows both call paths enable AEC, noise suppression, and automatic gain control.
Upstream leaves the legacy `enableVolumeControl` flag disabled; the experimental
facade now does the same. The pinned modern TgCalls implementation has no consumer of
that flag, so the behavior-changing correction is route lifecycle handling.

The previous mobile page sent its initial private-output request before the native
TgCalls session existed, logged `reason=session_missing`, and discarded it. The
platform could consequently retain a loudspeaker at full hardware gain. The updated
route path records that request, applies it as soon as the audio device module has
enumerated its earpiece and loudspeaker endpoints, and applies the existing 50% gain
cap when speakerphone is requested. It also routes a matching platform request through
TgCalls, rather than assuming the platform routing state changed the already-created
native audio device.

### Device test

Make a speakerphone call to iOS and confirm the first `audio_output` diagnostic has
`queued=1`, followed by an active-session route report whose native value includes
`gain=50`. Verify that the iOS participant no longer hears sustained echo or feedback,
then switch back to earpiece and confirm the route report includes `gain=restore`.
The APM fields in a successful call should report enabled AEC, noise suppression, and
gain control rather than `apm=pending`.

### Artifacts

Under `%LOCALAPPDATA%\UnigramTdlibExperiment\artifacts\`:

| File | SHA-256 | Bytes |
| --- | --- | ---: |
| `Unigram_26.9.6233.0_ARM_InitialRouteEchoControl_Sideload.zip` | `7C96D95E802AE8F00904FBFF58DDA949169F655FE9FC9BECDDEE2775638F8CA1` | 65,102,511 |
| `Unigram_26.9.6233.0_ARM.appx` | `AACFBD7239D5456870B68303066BE0DEC1CB6FD042CFDEF727A17C8FA35DE5EA` | 58,554,201 |
| `Unigram_26.9.6233.0_ARM.cer` | `5D891C3D3F5DF85A556C01BD5BA58C6837A736776E4D781CDEE9790060A72B85` | 832 |

## 26.9.6234.0 — V2 hardware-cap diagnosis

The `6233` Idol 4S trace validated the queued route but exposed two issues:

- protocol `8.0.0` selects TgCalls `InstanceV2Impl`, whose `setOutputVolume()` is a
  no-op, so the reported `gain=50` did not change the hardware output;
- `MaxSpeakerVolume()` raised an access violation after the call. It was invoked by
  temporary startup and periodic gain diagnostics, not by capture, signaling, or the
  established call transport.

The `6234` device test proved that a direct 50% UWP mixer cap reduced feedback, but the
Alcatel then faulted at `set_speaker_volume`. This establishes that its speaker mixer is
unsafe for writes as well as range reads. Do not reuse the direct hardware-cap approach.

The previous APM diagnostic remains `apm=pending` on successful V2 calls. That
instrumentation does not yet observe V2's processing instance, so it must not be
treated as evidence that AEC, noise suppression, or gain control are disabled.

### Artifacts

Under `%LOCALAPPDATA%\UnigramTdlibExperiment\artifacts\`:

| File | SHA-256 | Bytes |
| --- | --- | ---: |
| `Unigram_26.9.6234.0_ARM_V2SpeakerCap_Sideload.zip` | `44084F0611C88399361031523C693F40D3E92FB6331C5BB1F98D5BB0FF68344D` | 65,101,281 |
| `Unigram_26.9.6234.0_ARM.appx` | `10EBE7023B10C48A168355940FE6C8E543B648C971B4CCCCEEF7ADA7DFA95A8F` | 58,552,744 |
| `Unigram_26.9.6234.0_ARM.cer` | `5D891C3D3F5DF85A556C01BD5BA58C6837A736776E4D781CDEE9790060A72B85` | 832 |

## 26.9.6235.0 — software speakerphone attenuation

The `6235` speaker-cap path makes no speaker-volume API calls. The retained WebRTC
playout probe halves each 16-bit PCM render sample while speakerphone is selected and
immediately returns to unscaled samples for earpiece, headset, or call teardown. This
applies the same 50% acoustic cap without changing or persisting the handset's system
volume.

### Device test

On an Idol 4S speakerphone call, `audio_output` must contain
`gain=software_cap_pending`; subsequent `audio_level` diagnostics must report
`speaker_gain=software_cap` with a positive `buffers` count. Switching to earpiece
must report `gain=software_restore` and `speaker_gain=software_restore`. The process
must remain alive through the call and teardown, with no `max_speaker_volume`,
`set_speaker_volume`, or `voip.fault` record. The iOS peer should no longer hear the
full-volume feedback loop.

### Artifacts

Under `%LOCALAPPDATA%\UnigramTdlibExperiment\artifacts\`:

| File | SHA-256 | Bytes |
| --- | --- | ---: |
| `Unigram_26.9.6235.0_ARM_SoftwareSpeakerCap_Sideload.zip` | `D50C36E6BA9BEA46EA088D86E1DD7FB013E5015386F2670946DB31F818C0E6DA` | 65,102,488 |
| `Unigram_26.9.6235.0_ARM.appx` | `DEA04D9B195D38C050D9F961573FE14A9811FFB8F7F69DF97E72ABDB35037DB7` | 58,553,022 |
| `Unigram_26.9.6235.0_ARM.cer` | `5D891C3D3F5DF85A556C01BD5BA58C6837A736776E4D781CDEE9790060A72B85` | 832 |

## 26.9.6236.0 — route-state hardening

The software gain request is now stored before attempting native endpoint selection.
If the selected endpoint is unavailable, an earpiece, headset, or call teardown request
still clears speaker attenuation immediately instead of retaining a prior speaker state.

### Artifacts

Under `%LOCALAPPDATA%\UnigramTdlibExperiment\artifacts\`:

| File | SHA-256 | Bytes |
| --- | --- | ---: |
| `Unigram_26.9.6236.0_ARM_SoftwareSpeakerCap_Sideload.zip` | `5D795A23F0DA79A124C5F2E6AAAD0C295D68CC8E9CA4191C576E496A1061D3EE` | 65,101,881 |
| `Unigram_26.9.6236.0_ARM.appx` | `5617C0699F056BBEC39B739B0A056A2A48B67FA2178897C0CC0F46B23B230FC6` | 58,553,850 |
| `Unigram_26.9.6236.0_ARM.cer` | `5D891C3D3F5DF85A556C01BD5BA58C6837A736776E4D781CDEE9790060A72B85` | 832 |

## 26.9.6237.0 — experimental display identity

The application’s VisualElements and tile ShortName appear as
`Unigram Mobile TDLib Experimental` in the app list. The package Properties display
name remains `Unigram Mobile` because the publisher’s app-reservation validation only
permits registered display names. Its package identity remains
`49197Wirdschon.UnigramMobileTdlibExperimental`, preserving its separate LocalState and
TDLib database space from the stable app.

This package also:

- detaches modern video composition outputs on the XAML UI thread before native call
  teardown, preventing the renderer from releasing a `SpriteVisual` on the TDLib worker
  after a remote video-call hangup;
- uses typed property initialization for modern `SearchChatMessages` requests and fixes
  incremental pagination, restoring populated Media, Files, Links, Music, and Voice
  sections in group shared media.

### Artifacts

Under `%LOCALAPPDATA%\UnigramTdlibExperiment\artifacts\`:

| File | SHA-256 | Bytes |
| --- | --- | ---: |
| `Unigram_26.9.6237.0_ARM_VideoTeardownAndSharedMedia_Sideload.zip` | `E489C66E25C3A4057BC45B9756232DB326FD4405A322C98B0400EF20352D02EB` | 65,101,840 |
| `Unigram_26.9.6237.0_ARM.appx` | `94663B1E2F589DF400B16B8A03D6792521F7577FC8DFAAD8BBCCC23F479B94D9` | 58,552,987 |
| `Unigram_26.9.6237.0_ARM.cer` | `5D891C3D3F5DF85A556C01BD5BA58C6837A736776E4D781CDEE9790060A72B85` | 832 |

## 26.9.6238.0 — shared-media response migration

Modern TDLib returns `FoundChatMessages` from `SearchChatMessages`, not the legacy
`Messages` object. The prior migration built requests successfully but discarded every
successful result by checking only for `Messages`. Shared-media headers and collections
now consume `FoundChatMessages.Messages`, `TotalCount`, and `NextFromMessageId`.

This restores Media, Files, Links, Music, and Voice group content and pagination. The
added diagnostics record only the category plus returned and total counts; they contain
no chat identity, message content, user identity, file name, or token.

### Artifacts

Under `%LOCALAPPDATA%\UnigramTdlibExperiment\artifacts\`:

| File | SHA-256 | Bytes |
| --- | --- | ---: |
| `Unigram_26.9.6238.0_ARM_FoundChatMessages_Sideload.zip` | `676E03DAA2FBE9D6F87837683790DF79283DCCA5D582E458F35D98D4AF97D629` | 65,102,165 |
| `Unigram_26.9.6238.0_ARM.appx` | `5CA2DEDA9A4420803C713CD5C66C24D427F82D0D284C5F975D0781E6FEE9E270` | 58,555,102 |
| `Unigram_26.9.6238.0_ARM.cer` | `5D891C3D3F5DF85A556C01BD5BA58C6837A736776E4D781CDEE9790060A72B85` | 832 |

## 26.9.6239.0 — account selector refresh

The root account selector now subscribes to `Lifetime.Items.CollectionChanged` and
rebuilds its navigation entries on the XAML dispatcher when a session is created or
removed. Previously, creating a second account updated `Lifetime.Items` but left the
selector’s independent footer collection stale until the user tapped Add Account again.

### Artifacts

Under `%LOCALAPPDATA%\UnigramTdlibExperiment\artifacts\`:

| File | SHA-256 | Bytes |
| --- | --- | ---: |
| `Unigram_26.9.6239.0_ARM_AccountSelectorRefresh_Sideload.zip` | `2A97419A7F44014DDD2F004B582218FBCD5E07608BD8E9E81CAFFFB06417092D` | 65,098,225 |
| `Unigram_26.9.6239.0_ARM.appx` | `DBE10DA071318F3CA67801D5BB51256F549A20D0B629DFD9398E2671AE557B1C` | 58,554,228 |
| `Unigram_26.9.6239.0_ARM.cer` | `5D891C3D3F5DF85A556C01BD5BA58C6837A736776E4D781CDEE9790060A72B85` | 832 |

## 26.9.6242.0 — account selector and pinned-message response fixes

The previous selector refresh was insufficient on Windows 10 Mobile because it rebuilt
the virtualized footer by raising individual removal and insertion notifications. A
recycled session container could remain blank immediately above Add Account until another
action happened to rebuild the list. The selector now snapshots live session containers in
stable slot order and replaces the footer collection with one reset. An account whose own
user record has not reached the local cache renders a visible localized `Loading...` row
rather than an empty invisible container. `SessionService` also formally subscribes to
`UpdateUser`, enabling its pre-existing lifecycle refresh when the signed-in account's
profile arrives.

Modern TDLib returns `FoundChatMessages` from `SearchChatMessages`. Pinned-message
loading still expected only legacy `Messages`, so both the chat banner and pinned-history
navigation could discard successful search results. The shared compatibility parser now
normalizes these calls, and the request construction uses the ARM-safe property-initialized
factory already used by the shared-media migration.

### Artifacts

Under `%LOCALAPPDATA%\UnigramTdlibExperiment\artifacts\`:

| File | SHA-256 | Bytes |
| --- | --- | ---: |
| `Unigram_26.9.6242.0_ARM_AccountSelectorAndPinnedMessages_Sideload.zip` | `FA758EEB67EE08E6E3D508BD118FC87F861726C7767BAEC4DE7EA598A481548F` | 65,093,628 |
| `Unigram_26.9.6242.0_ARM.appx` | `A49CD370C56DD93370450173425F002568DAD012128E95CCE74E84A1ACEE45A4` | 58,556,031 |
| `Unigram_26.9.6242.0_ARM.cer` | `5D891C3D3F5DF85A556C01BD5BA58C6837A736776E4D781CDEE9790060A72B85` | 832 |

Release|ARM rebuilt successfully. Signature verification succeeded; the package identity
is `49197Wirdschon.UnigramMobileTdlibExperimental`, version `26.9.6242.0`, architecture
`arm`; the payload contains `Telegram.Td.dll`, `Telegram.Td.winmd`,
`ModernCallsBridge.dll`, and `Unigram.Native.Tasks.dll`; and it contains no
`Constants.Secret.cs`, PFX, or PDB files. The sideload ZIP contains exactly the signed APPX,
CER, and four ARM dependency APPXs.

Device acceptance remains required: expand the selector immediately after adding and
signing in to a second account, then confirm both rows are present with no duplicates after
switching and relaunching. In a chat with a known pin, verify that the pinned banner appears
at the top and that the pinned-messages view can page through the results.

## 26.9.6243.0 — upstream account-profile hydration

The `6242` pinned-message fix was confirmed on device: a known pin now appears at the top
of its chat. The account selector still needed a further upstream comparison. Current
upstream Unigram `develop` at `bab9f3cf3de9adc16312393eb5d7d8bbd1d25dd3` uses
`LifetimeService.GetItemsForMenu` to maintain a stable, user-selectable session ordering
and a hash to avoid unnecessary rebuilds. More importantly for this issue, its account-row
container explicitly sends `GetUser(session.UserId)` whenever the session's cached profile
is absent.

The experimental UWP selector previously only returned when the cache lookup failed (and
`6242` changed that blank row to `Loading...`). `6243` now follows upstream's hydration
behavior. It requests the missing user once per session slot, retains the returned profile
only in the root page's in-memory session cache, and atomically refreshes the selector on
the XAML dispatcher. This deliberately does not retain identities outside the process or
log account details. It also does not port upstream's drag-reordering feature, which is
outside this targeted Windows 10 Mobile correction.

### Artifacts

Under `%LOCALAPPDATA%\UnigramTdlibExperiment\artifacts\`:

| File | SHA-256 | Bytes |
| --- | --- | ---: |
| `Unigram_26.9.6243.0_ARM_UpstreamAccountHydration_Sideload.zip` | `711F10DE0E0DE5D6A6D1C665F5D4FCC6809651130AABC06380926F8352A89CD9` | 65,113,434 |
| `Unigram_26.9.6243.0_ARM.appx` | `F20FE11274D3F50FD9549E2F05C7224F291B5736C0F9BCEF558222AB0452D8F8` | 58,563,746 |
| `Unigram_26.9.6243.0_ARM.cer` | `5D891C3D3F5DF85A556C01BD5BA58C6837A736776E4D781CDEE9790060A72B85` | 832 |

Release|ARM rebuilt successfully and APPX signature verification passed. The package keeps
the isolated `49197Wirdschon.UnigramMobileTdlibExperimental` identity, version
`26.9.6243.0`, and ARM architecture. Its payload includes `Telegram.Td.dll`,
`Telegram.Td.winmd`, `ModernCallsBridge.dll`, and `Unigram.Native.Tasks.dll`; it contains
no `Constants.Secret.cs`, PFX, or PDB files. The sideload ZIP has exactly six root files:
the APPX, CER, and the four ARM dependency APPXs.

Device acceptance: after signing in to a second account, open the account selector before
tapping Add Account. Its row should now appear immediately; if it first says `Loading...`,
it should change to the account name and avatar after the privacy-safe `GetUser` request
returns. Confirm that switching and relaunching introduce neither duplicate nor blank rows.

## 26.9.6244.0 — account selector source-versus-render diagnostics

The upstream-style profile hydration in `6243` did not make the secondary account visible
on the affected device. That rules out an absent cached `User` as the sole cause. `6244`
adds a maximum of 24 privacy-safe `account_selector` diagnostics per root page so the next
device run can distinguish whether the account is absent from the live session source or
present but not realized by the Windows 10 Mobile footer `ListView`.

Each record contains only a process-local session slot, phase, boolean state, and counts:

```text
account_selector|stage=rebuild;expanded=<0|1>;source=<count>;rows=<count>;bound=<count>
account_selector|stage=container;slot=<slot>;phase=<0..2>;profile=<0|1>
account_selector|stage=request;slot=<slot>;queued=<0|1>
account_selector|stage=response;slot=<slot>;result=<type>
```

It never records Telegram user IDs, names, phone numbers, account credentials, message
content, tokens, or paths. `source=2;rows=2` paired with one missing row isolates the
problem to container realization; `source=1` isolates it to account/session lifecycle.

### Artifacts

Under `%LOCALAPPDATA%\UnigramTdlibExperiment\artifacts\`:

| File | SHA-256 | Bytes |
| --- | --- | ---: |
| `Unigram_26.9.6244.0_ARM_AccountSelectorDiagnostics_Sideload.zip` | `9BE9B8C8EFD999FE3992F301CC708FD7EB3BFBEF7A32C844515F0A01EAE4AE7F` | 65,106,288 |
| `Unigram_26.9.6244.0_ARM.appx` | `F061E88C9C1D5E955EA7B7DFAA344A1F4DB5DBDFC2633E718FF2E19729FCEDB0` | 58,559,402 |
| `Unigram_26.9.6244.0_ARM.cer` | `5D891C3D3F5DF85A556C01BD5BA58C6837A736776E4D781CDEE9790060A72B85` | 832 |

Release|ARM rebuilt successfully and APPX signature verification passed. The package keeps
the isolated `49197Wirdschon.UnigramMobileTdlibExperimental` identity, version
`26.9.6244.0`, and ARM architecture. Its payload includes `Telegram.Td.dll`,
`Telegram.Td.winmd`, `ModernCallsBridge.dll`, and `Unigram.Native.Tasks.dll`; it contains
no `Constants.Secret.cs`, PFX, or PDB files. The sideload ZIP has exactly six root files:
the APPX, CER, and four ARM dependency APPXs.

## 26.9.6246.0 — nonvirtualized account selector and restored-session startup

The account rows now use a nonvirtualized `StackPanel` item host. This small, bounded
selector no longer depends on `ItemsStackPanel` container realization, so every existing
account row is created when the selector opens. The active-account indicator is also
updated explicitly from `LifetimeService.ActiveItem`; its source property now raises
`PropertyChanged`, preventing the blue selection bar from remaining on both rows.

Startup now uses the persisted, non-sensitive per-session `Settings.UserId` only as a
restoration marker. A known account defers navigation while TDLib is initializing and
routes directly to `MainPage` upon `AuthorizationStateReady`, avoiding the transient
Start Messaging intro. A genuinely new or logged-out session still receives the
onboarding experience.

### Artifacts

Under `%LOCALAPPDATA%\UnigramTdlibExperiment\artifacts\`:

| File | SHA-256 | Bytes |
| --- | --- | ---: |
| `Unigram_26.9.6246.0_ARM_NonvirtualSelector_SplashRouting_Sideload.zip` | `FF413D09ABB1B0BF02929D614B362833C0AF1927758AABB47D2892A57BC3433F` | 65,117,129 |
| `Unigram_26.9.6246.0_ARM.appx` | `2C1BE587E0142B2874D34D1CF66C80879C0C868BB99F3C956F5DDEDC188182A3` | 58,570,762 |
| `Unigram_26.9.6246.0_ARM.cer` | `5D891C3D3F5DF85A556C01BD5BA58C6837A736776E4D781CDEE9790060A72B85` | 832 |

Release|ARM rebuilt successfully and APPX signature verification passed. The package keeps
the isolated `49197Wirdschon.UnigramMobileTdlibExperimental` identity, version
`26.9.6246.0`, and ARM architecture. Its payload includes `Telegram.Td.dll`,
`Telegram.Td.winmd`, `ModernCallsBridge.dll`, and `Unigram.Native.Tasks.dll`; it contains
no `Constants.Secret.cs`, PFX, or PDB files. The sideload ZIP has exactly six root files:
the APPX, CER, and four ARM dependency APPXs.

Device test: fully exit and relaunch with two authorized accounts, open the menu before
tapping Add Account, and verify two visible rows with exactly one blue indicator. Switch
both ways, relaunch, and verify the existing account opens directly to the main page
without showing Start Messaging. Then create a new account and verify onboarding remains
available.

## 26.9.6249.0 — separate legacy account selector

The legacy menu flyout continues to use its original account-row template, Add Account
item, separator, and navigation destinations. The fragile mixed-item `NavigationViewList`
has been split into two sibling controls:

- `AccountSelectorList` is a bounded, non-scrolling session-only list. It retains the
  existing `SessionItemTemplate`, account switching behavior, profile hydration, unread
  badge, and single active-account indicator.
- `NavigationViewList` now renders only `RootDestination` menu rows.

This removes the cross-type container recycling that could hide a live second session
until Add Account forced a separate list realization. The account source remains event
driven; it is not continuously polled because the earlier diagnostics already proved both
sessions were present before the mixed list failed to render one.

### Artifacts

Under `%LOCALAPPDATA%\UnigramTdlibExperiment\artifacts\`:

| File | SHA-256 | Bytes |
| --- | --- | ---: |
| `Unigram_26.9.6249.0_ARM_SeparatedAccountSelector_Sideload.zip` | `504DFE33A6DBA7354D3CEA2CF656E6A61DE357660659245F97AE8C205D6EA414` | 65,110,531 |
| `Unigram_26.9.6249.0_ARM.appx` | `157895BC3FD06843B9922AC8039BCC152115A459C64EA89809ADAB1A65F07CDB` | 58,564,122 |
| `Unigram_26.9.6249.0_ARM.cer` | `5D891C3D3F5DF85A556C01BD5BA58C6837A736776E4D781CDEE9790060A72B85` | 832 |

Release|ARM rebuilt successfully and APPX signature verification passed. The package keeps
the isolated `49197Wirdschon.UnigramMobileTdlibExperimental` identity, version
`26.9.6249.0`, and ARM architecture. Its payload includes `Telegram.Td.dll`,
`Telegram.Td.winmd`, `ModernCallsBridge.dll`, and `Unigram.Native.Tasks.dll`; it contains
no `Constants.Secret.cs`, PFX, or PDB files. The sideload ZIP has exactly six root files:
the APPX, CER, and four ARM dependency APPXs.

Device test: fully exit and relaunch with two authorized accounts. Open the legacy flyout
before Add Account; both templated account rows must appear immediately, switch accounts
when tapped, and have exactly one blue active-account indicator.

## 26.9.6248.0 — incremental legacy account rows

The legacy flyout remains unchanged. The selector previously used
`MvxObservableCollection.ReplaceWith`, which raises a collection `Reset` after profile or
session updates. On Windows 10 Mobile, that reset discards every mixed-list container;
the second account can consequently remain unrealized until an unrelated Add Account
action forces another collection rebuild.

`6248` replaces the reset with an identity-preserving diff of the leading account rows.
When the second singleton `SessionService` becomes available, it is inserted at index 1;
the first account and normal navigation containers are retained. Account removal and
selector collapse use corresponding indexed removes. This avoids overlapping rows and
preserves the original `ListView` item panel and templates.

### Artifacts

Under `%LOCALAPPDATA%\UnigramTdlibExperiment\artifacts\`:

| File | SHA-256 | Bytes |
| --- | --- | ---: |
| `Unigram_26.9.6248.0_ARM_IncrementalAccountRows_Sideload.zip` | `C792FE1150F17C69E6C35CA5656C8AC988D7F2A00D71046C0899AE0E001F7C38` | 65,113,214 |
| `Unigram_26.9.6248.0_ARM.appx` | `AE44023C48A0D8DF7F59A2E676B1E1D12425AA92EE37EC031D519DCB29653C01` | 58,569,531 |
| `Unigram_26.9.6248.0_ARM.cer` | `5D891C3D3F5DF85A556C01BD5BA58C6837A736776E4D781CDEE9790060A72B85` | 832 |

Release|ARM rebuilt successfully and APPX signature verification passed. The package keeps
the isolated `49197Wirdschon.UnigramMobileTdlibExperimental` identity, version
`26.9.6248.0`, and ARM architecture. Its payload includes `Telegram.Td.dll`,
`Telegram.Td.winmd`, `ModernCallsBridge.dll`, and `Unigram.Native.Tasks.dll`; it contains
no `Constants.Secret.cs`, PFX, or PDB files. The sideload ZIP has exactly six root files:
the APPX, CER, and four ARM dependency APPXs.

Device test: fully exit and relaunch with two authorized accounts, then open the legacy
flyout before selecting Add Account. Both account rows must be visible immediately, retain
the original template, and show exactly one blue active-account indicator.

## 26.9.6247.0 — restore menu container rendering

Do not deploy `6246` for selector testing. On Windows 10 Mobile, replacing this
`ListView`'s default `ItemsStackPanel` with `StackPanel` bypassed the app's
`ChoosingItemContainer` and `ContainerContentChanging` path. Account objects and menu
destinations consequently rendered as raw type names rather than their templates.

`6247` removes only that incompatible panel override, restoring the established
container-rendering pipeline. It retains the roster-stable behavior from `6245`, the
one-active-account indicator correction, and the restored-session startup routing. Device
testing has already confirmed that the Start Messaging screen no longer flashes during
the splash sequence for an existing account.

### Artifacts

Under `%LOCALAPPDATA%\UnigramTdlibExperiment\artifacts\`:

| File | SHA-256 | Bytes |
| --- | --- | ---: |
| `Unigram_26.9.6247.0_ARM_MenuRenderingRestore_Sideload.zip` | `60324265B45C602B671FBE63B03D10088975DADE4AA648C2A6A3A21F9D0089D3` | 65,115,078 |
| `Unigram_26.9.6247.0_ARM.appx` | `0574DA932DFA141CCAAF7313D0B1EBC0F694F25ABCD76C7663E027AD4AADB6ED` | 58,569,452 |
| `Unigram_26.9.6247.0_ARM.cer` | `5D891C3D3F5DF85A556C01BD5BA58C6837A736776E4D781CDEE9790060A72B85` | 832 |

Release|ARM rebuilt successfully and APPX signature verification passed. The package keeps
the isolated `49197Wirdschon.UnigramMobileTdlibExperimental` identity, version
`26.9.6247.0`, and ARM architecture. Its payload includes `Telegram.Td.dll`,
`Telegram.Td.winmd`, `ModernCallsBridge.dll`, and `Unigram.Native.Tasks.dll`; it contains
no `Constants.Secret.cs`, PFX, or PDB files. The sideload ZIP has exactly six root files:
the APPX, CER, and four ARM dependency APPXs.

## 26.9.6245.0 — account selector roster-stable refresh

The `6244` device log isolated the actual defect:

```text
account_selector|stage=rebuild;expanded=1;source=2;rows=2;bound=15
account_selector|stage=container;slot=1;phase=0;profile=0
account_selector|stage=response;slot=1;result=user
```

Both sessions were present in the source and footer rows, and the second profile request
completed successfully. The row then disappeared because every profile update rebuilt the
entire footer collection, recycling its container before Windows 10 Mobile could keep it
realized. This matches the upstream distinction between a changed session roster and a
same-roster visual refresh.

`6245` hashes the ordered, process-local session slots and the expanded state. It replaces
the footer collection only when that roster or state changes; otherwise it updates realized
profile rows in place. This preserves the second row while its profile is hydrated and
removes the reset loop observed in the supplied log. The existing diagnostics remain
bounded and privacy safe.

### Artifacts

Under `%LOCALAPPDATA%\UnigramTdlibExperiment\artifacts\`:

| File | SHA-256 | Bytes |
| --- | --- | ---: |
| `Unigram_26.9.6245.0_ARM_AccountSelectorRosterRefresh_Sideload.zip` | `F924F75F0D9F739406276244E3B60F48E771894A476163D4DBC6EBE5DD896005` | 65,115,565 |
| `Unigram_26.9.6245.0_ARM.appx` | `9ADAF76C9A3EC4EEA3F9BF404A812A8D949E9BDDD451FC42FF74D8CA3D20E7F7` | 58,571,794 |
| `Unigram_26.9.6245.0_ARM.cer` | `5D891C3D3F5DF85A556C01BD5BA58C6837A736776E4D781CDEE9790060A72B85` | 832 |

Release|ARM rebuilt successfully and APPX signature verification passed. The package keeps
the isolated `49197Wirdschon.UnigramMobileTdlibExperimental` identity, version
`26.9.6245.0`, and ARM architecture. Its payload includes `Telegram.Td.dll`,
`Telegram.Td.winmd`, `ModernCallsBridge.dll`, and `Unigram.Native.Tasks.dll`; it contains
no `Constants.Secret.cs`, PFX, or PDB files. The sideload ZIP has exactly six root files:
the APPX, CER, and four ARM dependency APPXs.


## 26.9.6250.0 - build account rows directly

The account selector no longer uses an items control at all. `AccountSelectorPanel` is a
plain `StackPanel` and `InitializeSessions` constructs one `ListViewItem` per session in
code, assigning `SessionItemTemplate` as the content template so the legacy row visuals,
tap behaviour and unread badge are unchanged. This removes virtualization, container
recycling and `ChoosingItemContainer`/`ContainerContentChanging` from the account rows
entirely, so a session can no longer be dropped by the realization pipeline.

Diagnostics were extended to make the remaining failure modes separable without guessing:
`stage=origin` reports the number of stored session folders against the number of resolved
session containers (counts only), and `stage=layout` reports how many rows were built, how
many have a non-zero measured height, their summed height and the panel height. The
account-selector diagnostic budget was raised from 24 to 64 records so the first flyout open
is never truncated. No names, identifiers, tokens, paths or message content are recorded.

## 26.9.6251.0 - register every stored session at startup

Fixes the long-standing defect where only one account appeared in the menu flyout until
`Add Account` was tapped. The cause was never in the flyout.

`ProtoService` relocates each session database to `LocalFolder\tdlib-experimental\{id}`
so the experimental package can never touch the stable app's production database, but
`ViewModelLocator.GetSessions` still enumerated numeric directories at the `LocalFolder`
root. In the modern build that enumeration matched nothing, so `Configure` took its
`fail` fallback and registered session 0 only. Session 1 existed on disk and was fully
authorized, but no container was ever built for it, so it was absent from
`TLContainer.GetSessions` and therefore from `LifetimeService.Items` and the selector.
`LifetimeService.Create` derives the next id from `max(registered id) + 1`, so tapping
`Add Account` chose id 1 and adopted the existing session-1 database - which is why the
real second account appeared at that moment instead of a blank one.

`SessionStorage` is now the single source of truth for the session storage root and the
per-session directory. `ProtoService`, `ViewModelLocator`, `LifetimeService.Destroy`,
`CallsService` voip log paths and `RootPage` diagnostics all resolve through it, so the
layout can never again diverge between the writer and the readers.

Under `MODERN_TDLIB` the enumerator no longer deletes a session directory that lacks the
`UserId` settings marker; it skips and records `result=skipped;reason=no-marker`. An
unexpected layout change must not be able to destroy an account that cannot be re-synced.

Startup now records `sessions|stored=N;configured=M;fallback=0|1` - counts only, no
identifiers or paths.

Verified on device: both accounts are listed in the menu flyout immediately on launch,
without tapping `Add Account`, on Alcatel Idol 4S, Lumia 1520 and Lumia 950 XL.

## 26.9.6253.0 - session-scoped notification avatars and voip log read path

Two follow-ups from the code review of the session registration fix.

`NotificationTask::GetPicture` built its avatar URI from a hardcoded
`ms-appdata:///local/0/profile_photos/`. It already received the resolved session id
and ignored it, so a push for the second account pointed at the first account's avatar
directory, and under the experimental build the root was wrong for every account because
the databases live under `tdlib-experimental`. The URI is now assembled from the real
session id, with the `tdlib-experimental/` segment added under `MODERN_TDLIB`.

`Unigram.Native.Tasks.vcxproj` did not define `MODERN_TDLIB`; the define is now added
by an `ItemDefinitionGroup` conditioned on `UseModernTdlib`. Confirmed present on the
`CL.exe` command line in a detailed-verbosity build of the project.

`CallsService` attached voip debug logs to a call rating by reading from the old
root-relative layout after the write path had already moved to `SessionStorage`. The
read path now resolves through `SessionStorage` as well, so the attachment is not
silently empty.

## 26.9.6254.0 - resolve native pushes through TDLib receiver mappings

The native raw-push task resolved `data.user_id` only through the legacy
`User{userId}` mapping and truncated that identifier to a 32-bit integer. Modern TDLib
uses signed 53-bit identifiers and persists `PushReceiverId{receiverId}` when it
registers a Windows push token. As a result, the native task could fail to associate a
secondary-account push with its local session, then incorrectly fall back to session 0.

The native task now mirrors the managed background handler: it resolves the
`PushReceiverId` mapping first, falls back to `User`, and preserves the full identifier
width for both keys. `ProtoService` likewise stores `my_id` without an `int` cast, so
the fallback mapping is correct after the app next initializes each account.

The task records only `native.session.resolve|result=mapped|fallback_default` and
`native.tile.update|result=success`; neither diagnostic contains an account identifier,
push token, message content, or filesystem path.

## 26.9.6255.0 - resolve raw-push routing through TDLib

The native background task still inferred the target session from the raw JSON
`data.user_id` field. Modern TDLib provides `getPushReceiverId(payload)` precisely
because a raw payload must be parsed by TDLib to find the subscription receiver. The
managed activation path already uses that function, but the native task did not.

The native task now references the packaged, pinned `Telegram.Td.winmd` and calls
`Client.Execute(new GetPushReceiverId(payload))` before it selects a session. It uses
the resulting receiver id to look up `PushReceiverId{receiverId}`, then falls back to
the legacy user mapping only for older payloads that TDLib cannot resolve. This removes
the undocumented raw-field assumption from the out-of-process notification path.

Diagnostics now record `native.receiver.resolve|result=success|missing|error` in
addition to the session and tile outcomes. They remain privacy-safe: no receiver id,
user id, token, message content, or path is written.

## 26.9.6256.0 - preserve new notifications and finish channel creation

On the Lumia 1520, a secondary-account notification was added, rendered, and then
removed about 300 ms later. `UpdateActiveNotifications` takes an OS toast-history
snapshot and removes unknown TDLib notifications. It raced the asynchronous toast
write that `UpdateNotificationGroup` had just started: the snapshot did not yet contain
the toast, so the application called `RemoveNotification`, then processed TDLib's
resulting removal update and dismissed its own new toast.

New notifications are now tracked in-memory for five seconds before the active-history
reconciler may remove them. Explicit TDLib removal updates (for example, a read
notification) still remove the toast immediately. The reconciliation diagnostic reports
only `result=deferred;reason=toast_write_pending`.

Channel creation now retains its wizard pages until the final channel-type step has
completed. The prior flow removed two arbitrary back-stack entries as soon as the
channel was created, which could close the wizard. The first step is guarded against
duplicate submissions and reports explicit errors. The final step no longer waits for
optional supergroup full-info cache data, confirms "Channel created", then opens the
channel and removes the wizard stack using the established group-creation pattern.

## 26.9.6257.0 - confirm member and subscriber additions

`AddChatMember` returns `Ok` in the pinned modern TDLib contract, but the legacy
callers silently ignored both successful results and errors. Adding a subscriber to a
channel therefore completed without confirmation, and a failed request did not explain
the problem.

The member-management and profile invite flows now confirm a successful add using the
existing localized “You added …” message. TDLib errors use the existing, localized
add-user alerts with channel-specific wording; unexpected result types show the
standard error dialog. This change records no diagnostic data and exposes no account or
member identifiers outside the existing local UI.

## 26.9.6258.0 - handle modern add-member results

Device diagnostics showed `AddChatMember` returning `FailedToAddMembers`, not `Ok`.
That is the documented result shape in the pinned modern TDLib: the list is empty when
the selected user was added, and contains an entry when that user could not be added.
The 6257 success check therefore treated every add attempt as an unexpected error.

Both member-add entry points now share one typed result handler. An empty
`FailedToAddMembers` list confirms success; a nonempty list shows the existing
channel- or group-specific invite restriction. Legacy `Error` responses still use the
existing mapped alert, and only unknown result types reach the generic error dialog.

## 26.9.6259.0 - refresh members after a confirmed addition

The members/subscribers view is backed by an incremental collection that is created
only when the page opens. A successful `AddChatMember` did not refresh that collection,
so the confirmation was shown but the new subscriber remained absent until the user
left and reopened the channel.

The shared result handler now returns whether TDLib confirmed the addition. On success,
the members view replaces its collection using the same construction path as initial
navigation, which causes the existing incremental list control to query the updated
members. The profile's embedded members surface refreshes as well when it represents
the same chat. Failed additions do not reload the list.

## 26.9.6260.0 - highlight read message receipts

Outgoing message receipts already used TDLib's `LastReadOutboxMessageId` to select the
single- or double-check state, but both states used the same footer brush. A read
receipt was therefore present but had no visual emphasis.

The message footer now uses the app accent brush for a confirmed read receipt in both
the glyph fallback and composition-drawn tick paths. Media and service-overlay footers
keep their white receipt color so it remains legible over their translucent backgrounds.
