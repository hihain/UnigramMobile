# Experimental TDLib 1.8.66 ARM UWP build

This directory contains reproducible inputs for an experimental C++/CX ARM UWP
build. Upstream source and compiled output stay outside the application
repository.

## Pinned inputs

- TDLib: `022d60202e446ad1287b9fb68e687c8a0760788b` (declares 1.8.66)
- vcpkg: `45f9f39362a4c52e2b1fbe57b7e649db7f3d96d4`
- OpenSSL: 3.5.7, from tag `openssl-3.5.7`
- zlib: 1.3.2, vcpkg port revision 2
- Generator host: Visual Studio 2022 Build Tools
- Compiler/toolset: Visual Studio v143 ARM (`14.44.35207`) for the ARM UWP build;
  v141 (`14.16.27023`) for vcpkg dependencies and the x64 host code generator
- Windows SDK: 10.0.18362.0
- UWP dependencies: dynamic CRT and dynamic libraries

### Why v143 for the ARM UWP build

TDLib 1.8.66 is 2026-era C++. Building it with v141 (MSVC 14.16, 2017) produced a
binary that corrupted the process heap during `TdDb::init_sqlite` /
`init_message_db`, which surfaced on device as an LFH double-free
(`0xC0000374`) when .NET Native freed an `HSTRING` on the update callback. Every
layer of the C++/CX string path was verified correct by disassembly, so the
fault was in code generation rather than in source.

v143 also matches the generation of `vccorlib140_app.dll` shipped by the
`Microsoft.VCLibs.140.00` **14.0.33519.0** framework package the app actually
runs against, so the inline `Platform::String` code compiled into
`Telegram.Td.dll` matches the runtime DLL implementing it.

v143 has full C++/CX ARM32 store support (`lib\arm\store\vccorlib.lib` and an
ARM `cl.exe` are both present in 14.44.35207). The generated WinMD is
byte-identical between the two toolsets, so the projected API surface is
unchanged.

Use `-Toolset v141` to reproduce the previous build for comparison.

The OpenSSL overlay is based on the pinned vcpkg port recipe because the vcpkg
registry at the pinned commit does not contain OpenSSL 3.5.7. Its source archive
SHA-512 is pinned in `overlays/openssl/portfile.cmake`. UWP builds disable only
the OpenSSL Windows certificate-store provider, which calls desktop-only
`CertOpenSystemStoreW`; TDLib does not use that provider.

`patches/0001-qualify-cx-field-types.patch` fixes the C++/CX generator to fully
qualify reference types in properties. This avoids a v141 name-hiding error when
an object has both a `Message` property and a later `Array<Message^>` property;
it does not change the projected WinMD API.

`patches/0002-project-legacy-web-page.patch` keeps the legacy `WebPage` projected
name for the modern `LinkPreview` object and exposes typed media accessors over
the direct `LinkPreviewType` variants. This is a narrow compatibility surface
for the existing message renderer; unsupported preview categories still retain
their modern typed `Type` object.

## Build

From a PowerShell prompt:

```powershell
.\build\tdlib\Build-TdlibArm.ps1 -WorkRoot C:\tdlib-build
```

The work root must be outside this repository. The script verifies both Git
commits, installs only the pinned manifest dependencies, generates the C++/CX
API with a native v141 build, configures WindowsStore ARM against SDK 18362,
and builds only `tddotnet`. Use `-VisualStudioPath` if the Build Tools instance
is installed elsewhere.

The script imports explicit developer environments before invoking vcpkg. This
avoids vcpkg selecting another same-version Visual Studio instance that does not
have the ARM compiler installed. Dependency and host-tool stages stay pinned to
`-vcvars_ver=14.16`; the ARM UWP stages follow the selected `-Toolset`.

Switching `-Toolset` clears the ARM UWP CMake cache automatically, because CMake
refuses to reuse a cache generated with a different toolset.

The existing machine-installed `Telegram.Td.UWP` Extension SDK is never changed.
Integration must consume proof-build files from the external work root, leaving
the stable SDK as a reproducible rollback path.

To verify an existing proof output and print stable SHA-256 values:

```powershell
.\build\tdlib\Verify-TdlibArm.ps1 -OutputRoot C:\tdlib-build\build-uwp-arm\RelWithDebInfo
```

Build the app only with the verified output:

```powershell
msbuild .\Unigram\Unigram\Unigram.csproj /p:Configuration=Release /p:Platform=ARM `
  /p:ModernTdlibRoot=C:\tdlib-build\build-uwp-arm\RelWithDebInfo
```

The project defaults to modern TDLib and rejects `UseModernTdlib=false`.
The verifier is also invoked during MSBuild with the expected version and
commit hard-coded, so changing build properties cannot select another TDLib
version. To rebuild from a clean pinned source checkout, delete the external
`C:\tdlib-build` work root and rerun the script; never substitute files inside
the output directory manually.

When building from Visual Studio, no separate TDLib command is required. The
project automatically invokes the pinned build as an incremental MSBuild target
when its external output is missing or older than the checked-in TDLib build
inputs. The default work root is
`%LOCALAPPDATA%\UnigramTdlibExperiment`; it contains the upstream checkout,
dependencies, intermediate builds, and final proof output, and is not committed
to this repository. Once the verified manifest is current, ordinary solution
builds reuse the output and only run the lightweight verifier.

## Application port status

The native proof build completes and produces `Telegram.Td.dll` and
`Telegram.Td.winmd`. The application project in this experimental branch is modern-TDLib-only:
`UseModernTdlib` defaults to `true`, and a build fails if it is overridden to
`false`. The legacy `Telegram.Td.UWP` Extension SDK is therefore not selectable
from this branch.
`UpdateManifest.ps1` selects the stable package identity/display name for the
default configuration and the isolated experimental identity when that property
is enabled. Modern bundles are ARM-only so they cannot accidentally include
unsupported x64 TDLib payloads. The experimental `uap:VisualElements` label is
unmistakable; the top-level `Properties/DisplayName` remains the localized
`Unigram Mobile` resource required by APPX validation.

The modern schema is not source-compatible with the 26.8 application surface.
The experimental build explicitly disables VoIP, nearby chats, and chat-folder
editing. Those areas are gated at compile time and emit a diagnostic event when
invoked. Notification registration continues to use
`RegisterDevice(DeviceTokenWindowsPush)` and preserves the existing
`PushReceiverId` session mapping and native background-task entry point.
The package manifest explicitly registers `Telegram.Td.Client` against
`Telegram.Td.dll`; the TDLib component PRI is also added as an APPX payload.
This is required for WinRT activation of the external C++/CX proof payload.

The modern Release ARM managed/XAML compile and signed APPX packaging complete
with the pinned WinMD and native payload when the ignored local
`Constants.Secret.cs` and test signing certificate are present. The PFX and
public CER remain local-only; the current test certificate thumbprint is
`B5E7EBF1650558A9D670BD1C5B9F82E24A213434`. The generated APPX and
`.appxupload` files are under `Unigram\Unigram\bin\ARM\Release\Upload` and
`Unigram\Unigram\AppPackages`. Modern chat/message reporting is compile-time
disabled until the server-driven `ReportChatResult` option flow is implemented.
Microsoft App Center (Analytics and Crashes) is also compile-time disabled on
this branch. Its UWP session tracker reaches
`Windows.System.Diagnostics.ProcessDiagnosticInfo` from a background thread, and
that WinRT server faults on Windows 10 Mobile 15254. Because the fault surfaces
inside a managed-to-COM call, .NET Native cannot marshal it and fail-fasts the
process with code `0x1007` a few seconds after launch. Keeping it enabled would
additionally publish this experimental build's telemetry, including the
signed-in user id, into the production App Center application. Unhandled and
unobserved exceptions are written to the local privacy-filtered diagnostics log
instead.
Device installation and fresh login are complete. Push delivery and Live Tile
validation remain outstanding. The isolated experimental package registers its
own WNS channel successfully, but it has a different package identity from the
stable application and therefore cannot inherit that application's WNS
association. `RegisterDevice` confirms only that TDLib accepted this channel;
the log must subsequently contain `native.task.run` and
`native.notification.displayed` to prove a WNS delivery reached the native
background task and updated the tile. 26.9.6122.0 adds a privacy-safe
`wns.identity` record to make that boundary explicit. Until an experimental
WNS association is provisioned, a missing native-task record is an upstream
delivery/configuration issue, not a Live Tile rendering failure.

#### 26.9.6038 Store build comparison

The submitted 26.9.6038 Store-build source was compared directly with this
experimental worktree. `Common\Toast.cs` and the complete native notification
task (`NotificationTask.cpp` and `.h`) are byte-identical. The managed
notification-service differences are only the required modern-TDLib API
adaptations and experimental diagnostics; the WNS registration, background
task, and Live Tile behavior are already present in this branch.

The functional difference is packaging. 26.9.6038 is associated with the
separate Store identity `ZuneTracks.UnigramMobileUWP`; the modern build uses
`49197Wirdschon.UnigramMobileTdlibExperimental` to protect the existing
production TDLib database and allow side-by-side installation. Replacing the
experimental manifest identity with the 26.9.6038 Store identity would turn
this package into an update/replacement of that Store app, require its private
signing key, and violate the isolation requirement. It is therefore explicitly
unsafe and is not done here.

To enable remote WNS delivery for this experimental package, reserve and
associate **a new Store identity** for
`49197Wirdschon.UnigramMobileTdlibExperimental` in the owning Dev Center
account, then obtain its Store-signed package through the normal submission
pipeline. No application credential, certificate, PFX, or Store association
metadata from 26.9.6038 is copied into this repository or sideload artifact.
After installing that identity, verify `wns.channel.created`,
`tdlib.register_device.result|result=success`, and, after a remote push,
`native.task.run`, `native.raw.received`, and
`native.notification.displayed`.

#### Store BETA push delivery test (26.11.6280.0)

The Store build uses `ZuneTracks.UnigramMobileMessengerBETA`. The stable
26.8.6012.3 build that received pushes used `49197Wirdschon.UnigramMobile`,
whose WNS Package SID is different. Telegram signs each WNS push with the
credentials it holds for the `api_id` that registered the device, so pushes
reach this package only if Telegram holds **this** product's Package SID and
client secret for **this build's** `api_id`. A successful `RegisterDevice` does
not prove that.

`build\wns\Test-WnsPush.ps1` separates the two halves without involving
Telegram:

1. On the phone, open Settings > Diagnostics > **Copy WNS channel URI**
   (user-initiated; the URI is never written to the log), send it to the PC
   privately, then close the app.
2. Run `.\build\wns\Test-WnsPush.ps1 -PackageSid 'ms-app://s-1-15-2-...' -ChannelUri '<uri>'`
   and enter the client secret at the secure prompt.
3. Interpret:
   - Authentication fails: the Partner Center credentials are wrong.
   - HTTP 403: the channel belongs to a different package than that SID.
   - `X-WNS-Status=received` and the log shows `native.task.run` /
     `native.raw.received` / `native.notification.displayed` with a "WNS
     delivery test" toast: the package, WNS, and background task all work, so
     missing Telegram pushes are a Telegram server-side credential/`api_id`
     mapping issue.
   - `received` but no `native.task.run`: background access or battery saver on
     the device is blocking the task (check `background.access.result`).

#### Foreground Live Tile tracing

The Store and experimental sources use the same foreground tile renderer and
the experimental diagnostics have recorded successful `managed.tile.update`
and `managed.tile.decision|...;result=applied` calls. Those records prove that
the Windows tile API accepted an update; they do not prove which tile is pinned
or visible in the shell. This is independent of remote WNS delivery.

26.9.6130.0 adds a bounded, privacy-safe trace of the foreground path:
`managed.tile.group`, `managed.tile.notification`, `managed.tile.message`, and
`managed.tile.gate`. It records notification kinds, counts, session state, and
the reason for a skip, but never chat IDs, message content, tokens, account
identifiers, or paths. The trace distinguishes no incoming TDLib notification,
an inactive account, an unavailable chat, the intentionally suppressed
currently-open chat, and a completed tile write.

26.9.6131.0 corrects the avatar source itself. The legacy implementation
assumed every completed TDLib profile photo lived under
`0/profile_photos/<file-name>` and constructed a tile URI from that assumed
layout. Modern TDLib can choose a different local download directory, so the
tile API received a valid update containing an unread count but a URI that
could not resolve to the avatar. The experimental build now copies the
completed supported avatar into its own `tile-avatars` LocalState cache and
uses the corresponding `ms-appdata` URI. The cache is isolated with the
experimental package and is removed with it; diagnostics report only the
cache outcome, never paths or identifiers.

### Experimental audio calls

26.9.6132.0 begins the audio-call port. It restores the tracked ARM
`libtgvoip` WinMD/DLL wrapper, the existing audio-only call page and service,
and the call entry points that were deliberately compiled out during the
modern-TDLib migration. The wrapper's ARM payload is byte-identical to the
26.9.6038 worktree payload; no signing material, Store association metadata,
or credentials are reused.

The port adapts TDLib 1.8.66 call request shapes for call creation, acceptance,
discard, and debug information. It creates every outgoing call with
`is_video=false`. Incoming/video call states are explicitly discarded with a
privacy-safe `voip.update|...reason=video_unsupported` diagnostic; camera and
video rendering are not enabled. Additional bounded diagnostics record only
state names, booleans, and reflector endpoint counts. They never record call
IDs, users, IP addresses, encryption keys, signaling data, tokens, audio, or
file paths.

Audio call setup requires a Telegram reflector endpoint supported by the
existing `libtgvoip` wrapper. A ready state without one is discarded rather
than starting an invalid media session. The experimental manifest already
contains `microphone` and `uap:voipCall` capabilities. Device validation must
cover outgoing, incoming accept/decline, microphone mute, speaker/earpiece
routing, call teardown, and the resulting `voip.*` diagnostics before any
video-call work begins.

#### Audio-call protocol compatibility

The ARM `libtgvoip` wrapper is a 2019 legacy transport and reports its maximum
protocol layer at runtime. The original call code sent an empty
`CallProtocol.LibraryVersions` vector. TDLib 1.8.66 carries that vector
unchanged to `phone.requestCall`, `phone.acceptCall`, and
`phone.confirmCall`. Modern Telegram clients, notably iOS, can negotiate by
library version rather than by layer alone; an empty vector isn't a compatible
declaration for that newer iOS/TgCalls path.

26.9.6133.0 added the wrapper's recognized legacy protocol tag `2.4.4` with
its actual reported maximum layer for every create, accept, and retry request.
That tag alone did not restore interoperability in device testing and is not
used by the Android/W10M compatibility baseline.

26.9.6135.0 restores the proven W10M legacy protocol: `min_layer=65`, the
wrapper-reported maximum layer, and an empty `library_versions` vector. It
constructs `CallProtocol` through parameterless activation plus individual
property setters because the modern projection previously exhibited
activation-factory marshalling failures for other multi-argument request
types.

26.9.6138.0 tested `2.4.4`, but current Android advertises only its newer
`2.7.7` and `5.0.0` transports. The bundled 2019 ARM wrapper does not expose
either transport and must not falsely advertise one. 26.9.6139.0 therefore
restores the W10M-compatible empty vector and explicitly documents Android and
iOS as unsupported by this native transport.

Bounded `voip.protocol` records confirm only the emitted minimum, maximum, and
library-version count. Bounded `voip.create`, `voip.retry`, and `voip.accept`
records distinguish a local TDLib request rejection from a later peer or
transport failure. Ready-state diagnostics now include reflector and WebRTC
endpoint counts. Supporting current Android/iOS requires an ARM UWP
implementation of a supported newer TgCalls/WebRTC transport; it is not a
safe C#-level compatibility change. Diagnostics never record call identifiers,
peers, IP addresses, keys, or signaling data.

#### TgCalls ARM UWP feasibility proof

The experimental branch pins the external
`TelegramMessenger/tgcalls@1c236c09f8d8569fead14bd68000618a52051225`
checkout under `%LOCALAPPDATA%\UnigramTdlibExperiment\tgcalls`. Its
`InstanceImpl` implements Android's current private-call transports (`2.7.7`
protocol V0 and `5.0.0` protocol V1), but the upstream checkout provides no
WebRTC headers, ARM UWP libraries, or UWP build project. The reproducible
`build\tgcalls\Test-TgCallsUwpArm.ps1` proof successfully compiles the
metadata unit with VS 2017 v141 ARM and stops at the actual call engine's
missing `rtc_base/logging.h` dependency. See `build\tgcalls\README.md` for
the source pin, command, licensing gate, and integration prerequisites.
Standard vcpkg rejects `webrtc:arm-uwp`, while the current Unigram UWP WebRTC
port supports only x64/ARM64, so an ARM Windows Mobile WebRTC build must be
ported from source. No unsupported protocol version is advertised as a result
of this proof.

For a conclusive test, pin the separate **Unigram Mobile TDLib Experimental**
primary tile, foreground the app on the chat list or a different chat, then
receive a message in a non-open chat. A currently open target chat is
intentionally excluded from toast and tile updates by `UpdateAsync`. If the
same test reports `managed.tile.update|...result=success` and
`managed.tile.decision|...result=applied` but the pinned experimental tile
does not redraw, the remaining defect is the Windows Mobile shell's tile
presentation/cache for that isolated package, rather than TDLib, WNS, or the
tile XML renderer.

### Composition degradations on device

`MainPage` could not be constructed on Windows 10 Mobile: the constructor threw
a `NullReferenceException` inside `DropShadowEx.Attach(FolderShadow, ...)`, which
left the app showing only the flyout menu. Three purely decorative composition
sites now report the failure and continue instead of taking the page down. Each
one logs to the diagnostics file so the degradation is visible rather than
silent, and none of them affect chat list, messaging, or notification behaviour:

- `MainPage..ctor` folder drop shadow - `main.construct.folder_shadow`.
- `MainPage..ctor` page header inset clip - `main.construct.page_header_visual`.
- `MainPage.ShowHideArchive` show/hide animation, skipped when the chat list
  template is not realized yet so `VisualTreeHelper.GetChild` returns null -
  `main.archive|result=skipped`. The end state is applied directly, so archive
  visibility stays correct; only the transition is dropped.

`DropShadowEx.Attach` additionally traces its first four calls per process
(`shadow.attach|step=...`) to identify which WinRT composition call returns null.
The budget exists because the diagnostics file is deleted once it reaches its
size cap, and unbounded tracing would evict the startup records. All of this is
gated behind `MODERN_TDLIB`; the non-modern configuration keeps the original
unguarded statements.

#### `Visual.RelativeSizeAdjustment` is reported present but is unusable

Device tracing showed `DropShadowEx.Attach` completing successfully
(`shadow.attach|step=done`) immediately before the `MainPage` fault, which
isolated the throw to the statement after it: `RelativeSizeAdjustment`.
That property lives on `IVisual2`, introduced in UniversalApiContract v5
(Windows 10 1709), so on this Windows 10 Mobile build the interface query fails
and the setter faults with a bare `NullReferenceException` under .NET Native.

The same property was also the reason **tapping a chat did nothing**:
`ChatView.xaml` instantiates `controls:StickerPanel` eagerly, that constructor
set `RelativeSizeAdjustment`, and the XAML parser wrapped the resulting
`NullReferenceException` as a `XamlParseException` (`HRESULT 0x802B000A`), which
aborted navigation to the chat page with no visible error.

**`ApiInformation.IsPropertyPresent` cannot be used to gate this property.** The
first fix probed it per member, and the device capability line proved the probe
is a false positive: `visual_relative_size=True` on a `universal_contract=4`
device, while the setter still failed with `hresult=0x80004003` (`E_POINTER`) at
all five converted call sites. `DropShadowEx.SetRelativeSize` therefore gates on
`ApiInfo.IsUniversalApiContract5Present` instead, which is also the convention
the rest of `ApiInfo` already follows for contract v5 members — the per-member
probes there are deliberately commented out in favour of the contract check.
When the contract is absent the shadow visual is sized from the host element and
kept in sync through `SizeChanged`, so the shadows still render.
The `#else` (non-`MODERN_TDLIB`) branches keep the original statements.

The call sites converted are `MainPage..ctor`, `StickerPanel..ctor`,
`EmojiDrawer..ctor`, `AnimationDrawer..ctor`, `StickerDrawer..ctor` and
`ChatBackgroundPresenter.UpdateBlurred`. The first sweep missed the three
drawers because the search glob did not recurse past one directory level; the
`EmojiDrawer` constructor was what actually aborted chat navigation.

Because the XAML parser turns any throwing constructor into a navigation
failure, the decorative composition in `StickerPanel`, `EmojiDrawer`,
`AnimationDrawer` and `StickerDrawer` is additionally wrapped in a reporting
`try`/`catch` (`drawer.construct|drawer=...`). These controls are instantiated
eagerly by `ChatView.xaml`, so a single unavailable composition member in any of
them would otherwise make chats impossible to open.

#### Supergroup chats crashed on the pinned-message path

Opening certain supergroups terminated the app. The diagnostics correlated the
crash precisely: `SupergroupFullInfo` → `ChatAdministrators` →
`GetChatMessageCount` → `app.unhandled` 16 ms later, then a fresh
`startup.app|stage=configure`. Chats without a pinned message were unaffected,
which is why one group crashed consistently and another never did.

`DialogViewModel.LoadPinnedMessagesSliceAsync` is an `async void` method, so any
fault inside it goes straight to `Application.UnhandledException` — which under
.NET Native receives an exception whose stack has **already been discarded** —
and takes the process down. That is why all four `app.unhandled` entries carried
no `stack=` field and could not be localised from the log alone.

The method is now a thin wrapper that awaits
`LoadPinnedMessagesSliceCoreAsync` inside a reporting `try`/`catch`
(`pinned.load`). A failure now records a usable stack and leaves the chat open
without its pinned-message banner instead of terminating the app.

`PushDiagnostics.WriteException` additionally falls back to a sanitised
`Exception.ToString()` as `detail=` when `StackTrace` is empty, so a stripped
unhandled exception still carries whatever locator information remains.

On device that guard worked as intended and produced the stack the unhandled
handler could not: the fault is in `MessagePinned.UpdateMessage`, reached from
`ChatView.UpdatePinnedMessage`. It is raised when `GetChatMessageCount` reports
a non-zero count, so only chats that actually have a pinned message enter the
banner's cross-fade — confirming the one-group-crashes-and-one-does-not split.

`MessagePinned.UpdateMessage` now takes a non-animated path when
`ApiInfo.CanUseDirectComposition` is false. On those devices `MessagePinnedLine`
and `NumericTextBlock` both disable themselves in their constructors, so the
cross-fade animates nothing and only risks the scoped batch and visual
animations it has to set up first. The method already branched on the same
capability to decide the title text, so this extends an existing distinction
rather than introducing one. `ShowHide` additionally falls back to toggling
`Visibility` when `InitializeParent` has not run, because
`ElementCompositionPreview.GetElementVisual(null)` throws out through WinRT.

Because the exception unwinds through a WinRT frame no managed stack survives to
name the exact statement, so `UpdateMessage` and `ShowHide` emit budgeted
`pinned.ui|step=` traces. The budget (60 entries per process) exists for the
same reason as the `shadow.attach` one: the diagnostics file is deleted at its
size cap, and unbounded tracing would evict the records that matter.

#### Opening a chat crashed while its history streamed in

With the pinned-message fault contained, the same chat still terminated the app
a few seconds later, after a long run of `tdlib.result|type=Message` entries —
message rendering rather than the banner.

`ChatView.OnCollectionChanged` animates the messages around an insert or a
removal, and both of its loops start at `panel.FirstCacheIndex`:

```csharp
for (int i = panel.FirstCacheIndex; i <= args.NewStartingIndex; i++)
{
    var container = Messages.ContainerFromIndex(i) as SelectorItem;
    var child = VisualTreeHelper.GetChild(container, 0) as UIElement;
```

`FirstCacheIndex` is `-1` until the panel has been measured, and
`ContainerFromIndex` returns null for any index the panel has not realised.
`VisualTreeHelper.GetChild` is a WinRT call, so a null container does not raise
a catchable managed exception — it throws across a native frame, and .NET Native
tears the process down there.

That is also why `App.OnUnhandledException` could not save it. The handler
already sets `e.Handled = true`, but `Handled` only suppresses termination for
exceptions the XAML framework can unwind; once the throw has crossed a native
callback frame the process is going down regardless. This is the reason faults
have to be caught at their managed source rather than centrally, and it is worth
remembering before trusting the global handler again.

Both loops now skip unrealised containers and containers with no visual child,
and `OnCollectionChanged` — another `async void` — delegates to
`OnCollectionChangedCoreAsync` inside a reporting `try`/`catch`
(`messages.collection`). The cost of a skipped entry is one missing slide
animation.

#### Composition members verified as already gated

A recursive sweep of every `CreateShapeVisual`, `CreateLinearGradientBrush` and
`CreateSpringVector3Animation` call site confirmed that all of them except
`StorageChart` are already behind `ApiInfo.CanUseDirectComposition`,
`ApiInfo.IsFullExperience` or `IsUniversalApiContract7Present`, or are dead code
(`FileButton.OnPauseToPlay` / `OnPlayToPause` are commented out at their call
sites). `ChatActionIndicator` is gated inside `UpdateAction`, and
`MessagePinnedLine`, `NumericTextBlock` and `ProgressBarRing` all null-check the
fields their guarded constructors leave unset.

`StorageChart` was the one genuinely unguarded user: its constructor called
`CreateShapeVisual` unconditionally and `ArrangeOverride`, `SetItems` and
`Update` dereferenced the result. It is now gated the same way, with null checks
on each member, so the storage page renders without the ring chart rather than
faulting.

A later re-sweep found one more: `ChatView.Autocomplete_SizeChanged` called
`CreateSpringVector3Animation` unconditionally. Spring animations are
UniversalApiContract v7, and the device reports `create_spring_vector3=False`,
so the autocomplete list would have faulted as it grew while typing. It is now
gated on `ApiInfo.CanUseDirectComposition`.

Two related sites are deliberately left alone. `ColorSlider` has one
`CreateSpringVector3Animation` block behind `if (false)` (dead) and another
gated on `IsUniversalApiContract5Present`. That second guard is the wrong
contract — the API is v7, so it would still fault on a contract v5 or v6 device
— but it is correctly skipped on this contract v4 device, sits in the photo
editor rather than on any in-scope path, and is pre-existing. It is recorded
here rather than changed.

#### Calls are intentionally unavailable

`voip.disabled|result=unsupported;feature=experimental_tdlib` in the diagnostics
is expected, not a regression. VoIP is one of the features explicitly disabled
for this experimental build (see the disabled-feature list above); the modern
TDLib call API was out of the ported scope. Placing or receiving a call logs
that line and does nothing else.

The experimental branch began at 26.8.6012.4, so it does **not** inherit the
stable 26.8.6012.5 call-protocol commit. 26.9.6122.0 carries that small source
fix forward for the eventual call port: profile call start, declined-call retry,
and call acceptance now use
`libtgvoip.VoIPControllerWrapper.GetConnectionMaxLayer()` rather than the
obsolete hard-coded layer 74. This aligns every source call site (the dialog
path already used the dynamic layer), but it deliberately does not remove the
experimental VoIP gate or claim that calling now works.

#### Device capability reporting

`ApiInfo.WriteCapabilities` runs once per process from the `App` constructor and
records `device.capabilities|universal_contract=...` along with the presence of
the individual composition members this port depends on. Moving to a newer TDLib
did not change the OS, but it did exercise code paths whose availability had not
been tested on this hardware, and .NET Native reports an unavailable WinRT member
as a bare `NullReferenceException` rather than a typed error. The line contains
API surface metadata only - no user data, identifiers, or paths.

#### Navigation failure reporting
`FrameFacade.Navigate` records the target page name and the full exception
(`navigate.page|page=...`) before rethrowing. A page whose XAML fails to load is
otherwise only visible as an unhandled HRESULT with no stack and no indication
of which page was involved.

`Build-TdlibArm.ps1` writes `TdlibBuildManifest.txt` beside the proof payload.
It records the pinned TDLib version/commit and hashes the generated WinMD and
implementation DLL. `Verify-TdlibArm.ps1` rejects missing or mismatched
metadata, and the application build requires that verification manifest before
it can resolve the TDLib reference. This prevents a stale, floating, or
manually substituted TDLib payload from reaching the APPX.

The experimental project explicitly excludes the portable
`System.Numerics.Vectors` packages and selects the matching UWP reference
assemblies from `Microsoft.NETCore.UniversalWindowsPlatform` 6.2.10 for
compile and native interop generation. The final ARM package includes the
corresponding ARM AOT runtime pair (`4.1.4.0` and `4.0.4.0`) through explicit
APPX payload items. This avoids both the portable `4.1.1.0`/`4.0.1.0` pair
and an app/device split where the native image requests `4.1.4.0` but the
device resolves another manifest (`0x80131040`).

The Store `.appxupload` must not carry that AOT pair: the Store recompiles the
upload's MSIL, and the AOT `System.Numerics.Vectors` makes its .NET Native
compiler fail with `NUTC1056` (Store error 1201, reproduced locally with
`ilc.exe` exit code 1201). The upload therefore receives the same 6.2.10
reference pair (also `4.1.4.0`/`4.0.4.0`) that the local ILC compiles; only the
sideload package receives the AOT runtime pair.

## Chat scroll crash: floating date header (26.9.6116.0)

The third on-device chat crash reproduced when scrolling **up** through a
supergroup. Instrumentation added in 26.9.6114.0 wrapped the four scroll-rate
handlers in managed try/catch blocks and recorded 116 identical faults:

    scroll.header|result=error;hresult=0x80004003;type=NullReferenceException;
    stack= at Unigram.Views.ChatView.UpdateHeaderDateCore(Boolean intermediate)
        << at Unigram.Views.ChatView.UpdateHeaderDate(Boolean intermediate)

`_dateHeader`, `_dateHeaderPanel` and `_dateHeaderTimer` are assigned only
inside `if (DateHeaderPanel != null)` in the `ChatView` constructor
(`ChatView.xaml.cs`), and the first hypothesis was that one of them was null.
A `scroll.header.state` probe added in 26.9.6116.0 reported every candidate as
non-null on the device:

    scroll.header.state|date_header=True;date_timer=True;date_panel_visual=True;
    date_panel=True;date_relative=True;view_model=True;pinned_list=True

**That hypothesis was wrong.** The guards listed below are inert hardening and
are retained only because they are harmless; they are not the fix. A later
review reached the same conclusion independently: the constructor dereferences
sibling `x:Name` fields before the `DateHeaderPanel` test, so a constructed
`ChatView` implies those fields are non-null, and `PinnedMessages` is a
get-only auto-property with an initializer that can never be null.

The real fault is in `MessagePinned.UpdateMessage`. The dedupe guard added in
26.9.6113.0 to stop the banner rebuilding on every scroll frame evaluated
`_chatId == chat.Id` **before** its own `step=enter` trace, so a null `chat`
threw with no `pinned.ui` record emitted - exactly what the logs showed. The
caller, `UpdateHeaderDateCore`, passes `ViewModel.Chat` straight through, and
that is null while a chat is being swapped. .NET Native inlines the small
callee, which is why the captured stack named only `UpdateHeaderDateCore` and
showed no frame for the method that actually faulted.

Before the dedupe guard existed, the method's first use of `chat` came after
the `message == null && !known` early exit, so a null chat was harmless. The
fix restores that by testing `chat != null` inside the dedupe condition
(`MessagePinned.xaml.cs`, under `MODERN_TDLIB`).

The crash is scroll-direction specific because the pinned-banner tail of
`UpdateHeaderDateCore` runs only once history is scrolled far enough for the
banner to need updating, which is why one group reproduced it and another did
not.

Inert guards retained under `MODERN_TDLIB` in `ChatView.Bubbles.xaml.cs`:

* `_dateHeader.Offset` - the if/else became a guarded ternary.
* `_dateHeaderTimer.Stop()/Start()`.
* `ShowHideDateHeader` returns early when `DateHeaderPanel` or
  `_dateHeaderPanel` is null, before any dereference.
* The pinned-banner tail returns early when `ViewModel` is null, and
  `ViewModel.PinnedMessages` is null-checked before `.Count`.

The visible effect when the date header is absent is that the floating date
pill does not appear; message history, the pinned banner and scrolling are
unaffected.

### Operator precedence defect in template selection

`SelectTemplateCore` contained:

    if (chat != null && chat.Type is ChatTypeSupergroup || chat.Type is ChatTypeBasicGroup)

`&&` binds tighter than `||`, so this parsed as
`(chat != null && ...Supergroup) || (chat.Type is ChatTypeBasicGroup)` and
dereferenced `chat` in the second operand whenever `GetChat()` missed the
cache. Parenthesised under `MODERN_TDLIB`; a null chat now falls through to
`FriendMessageTemplate`. This site never appeared in the device logs, so it is
a latent fix rather than the observed crash.

### Scroll instrumentation and trace budgets

The four scroll entry points (`ViewVisibleMessages`, `UpdateHeaderDate`,
`OnChoosingItemContainer`, `OnContainerContentChanging`) delegate to `*Core`
methods inside try/catch. Because `PushDiagnostics` enforces its 1 MB ceiling
by **deleting** the file rather than rotating it, an unthrottled fault at
scroll rate would evict the first and only useful occurrence. Each site
therefore has its own budget of four records.

`OnChoosingItemContainer` cannot simply swallow: the `Messages` list declares
no `ItemTemplate`, so a container's template and type tag come only from
`CreateSelectorItem`. Leaving `IsContainerPrepared` false would hand XAML an
untagged, untemplated container that renders blank and can never be recycled.
XAML's own suggestion cannot be reused either, because a fault before the
container was taken out of the recycle pool would realize a container that is
still pool-owned and could later be handed to a second item. The catch
therefore removes the suggestion from every pool, supplies a fresh
`EmptyMessageTemplate` container, and wraps that recovery in its own catch,
since this handler is invoked from WinRT and an unwinding recovery would cause
the fail-fast the outer catch exists to prevent.

### Still unresolved after 26.9.6118.0: a second header NRE

The `MessagePinned.UpdateMessage` null-chat fix landed and demonstrably worked -
`pinned.ui|step=enter` now appears in the device log, which it never did before.
But `scroll.header` still records four NREs per session, and the state probe
reports every collaborator present:

    scroll.header.state|date_header=True;date_timer=True;date_panel_visual=True;
                        date_panel=True;date_relative=True;view_model=True;
                        pinned_list=True

so a null collaborator is not the cause and adding further guards is pointless.
The failure is also intermittent rather than systematic: a `pinned.ui` record
appears 65 ms *after* a failed invocation, proving the same path completes
successfully on other passes, which points at a per-container or per-message
condition inside the loop.

The captured stack names `UpdateHeaderDateCore`, but .NET Native inlines small
callees, so the frame that actually threw is routinely absent. 26.9.6119.0
therefore replaces guesswork with a `_headerStep` ordinal, assigned at eleven
points through the method and reported as `step=` on the next failure:

| step | reached |
| --- | --- |
| 1 | method entry |
| 2 | panel resolved, before the loop |
| 3 | first-visible `TransformToVisual` |
| 4 | `DateHeader`/`DateHeaderLabel` assignment |
| 5 | `MessageHeaderDate` transform |
| 6 | timer restart |
| 7 | `ShowHideDateHeader` |
| 8 | pinned-banner tail entered |
| 9 | thread branch |
| 10 | pinned-list branch |
| 11 | completed |
| 12 | before `DateHeader.ActualHeight` |
| 13 | before calculating the offset |
| 14 | before hiding the inline separator |
| 15 | before showing the inline separator |
| 16 | before updating the date-header composition offset |

The probe also now reports `DateHeader`, `DateHeaderLabel`, `PinnedMessage` and
`ViewModel.Chat`, the four collaborators the original probe omitted. All are
booleans and a step ordinal; no identifiers, content or paths.

### Resolved in 26.9.6126.0: recycled date-header composition visual

The ordinal paid for itself immediately. The next device log reported
`step=5` on **all four** occurrences, with every one of the eleven probed
collaborators `True`. Step 5 is the `MessageHeaderDate` branch, and after the
expanded probe that block contains exactly one dereference the probe does not
cover: `transform`, the result of

    container.TransformToVisual(DateHeaderRelative)

`TransformToVisual` is declared to return a `GeneralTransform`, so nothing in
the signature suggests it can yield null or throw. On a container that is no
longer part of the live visual tree, it can do either. During a fast scroll
through weeks of history the list virtualizes containers out from under the
loop, and the date-separator container is the one most likely to be recycled,
because it is not a message and is created and discarded as day boundaries
pass.

The decisive detail is that **step 3 runs the identical call earlier in the same
pass and succeeds** - otherwise the ordinal would have read 3, not 5. That rules
out `DateHeaderRelative` itself being detached or unloaded, which would have
failed both calls. The fault is specific to the container being transformed.

26.9.6121.0 guarded only a null return. The 26.9.6122.0 device log still showed
four `step=5` NREs and **no** `scroll.header.transform` record. 26.9.6123.0
then caught the `TransformToVisual` call itself, and 26.9.6124.0 caught both
that call and `TransformPoint`. The next device log still reported `step=5`
without a recovery record. The transform hypothesis is therefore disproven:
the remaining dereference is later in this branch, after point conversion.

26.9.6125.0 subdivided the coarse ordinal around every remaining WinRT/object
operation: `DateHeader.ActualHeight`, both `container.Opacity` assignments, and
the `_dateHeader.Offset` composition write. The next device log confirmed the
package was this new build and reported **`step=16` for every remaining NRE**.
The list data and inline separator operations are therefore sound; the fault is
the compositor-backed `_dateHeader.Offset` setter when its visual is invalidated
as the date-header element is recycled.

26.9.6126.0 catches that specific `NullReferenceException` around only the
decorative composition assignment, logs up to two
`scroll.header.composition|result=throw;step=16` records, and leaves the inline
date separator visible. It also formats the startup version numerically (for
example, `package_version=26.9.6126.0`) instead of relying on
`PackageVersion.ToString()`, which prints only its type name on this runtime.

Both guards restore `container.Opacity` to 1 before skipping, and that detail is
easy to get wrong. `Opacity` is set to 0 only when a separator is hidden
underneath the floating pill, and every path that sets it back to 1 lives
*below* the guards. A container skipped without the restore therefore keeps a
stale 0 - and nothing in the container-recycling path clears it either, so the
separator would simply stay blank in the list. The step-3 site is as exposed as
step 5 here, because it runs while `minItem` is still true, which is exactly the
window in which the first date separator appears.

A budgeted `scroll.header.transform|result=throw;step=<n>` or
`result=null;step=<n>` remains useful if virtualization affects either
transform call. The next device run should instead use the expanded
`scroll.header.state|step=<n>` ordinal to identify the remaining failure.

### Locating a stackless NRE: why first-chance capture is not available

One `app.unhandled` NRE remains, non-fatal and handled, surfacing when the
emoji/sticker drawer opens. It cannot be located the usual way: under .NET
Native a `NullReferenceException` reaching `Application.UnhandledException` has
`StackTrace == null`, and `ToString()` returns only the resource key, which is
why the log shows `detail=System.NullReferenceException: [redacted_token]` with
no frames at all.

The obvious answer is `AppDomain.CurrentDomain.FirstChanceException`, which runs
*while the throw is in flight* and can therefore read `Environment.StackTrace`
before the runtime discards it. **That does not compile for this target:**

    App.xaml.cs(621,93): error CS0234: The type or namespace name
    'FirstChanceExceptionEventArgs' does not exist in the namespace
    'System.Runtime.ExceptionServices'

The type is absent from the UAP surface, so the event cannot be bound at all.
Recorded here so the approach is not attempted a third time.

The fallback is ordinary instrumentation, chosen by matching the log timeline
against the code. The records before the fault -
`GetFavoriteStickers`, `GetRecentStickers`, `StickerSets`, `StickerSet` - are
exactly the nested send chain in `StickerDrawerViewModel.SyncStickers`, whose
three terminal continuations all marshal to the UI thread and call
`SavedStickers.ReplaceWith`. Those three are now routed through a single
`ReplaceSavedStickers(site, ...)` helper that catches and reports a site
ordinal under `drawer.stickers`. The next device log either names the site or
excludes this path outright; the catch additionally prevents a drawer fault
from unwinding into the dispatcher, which would turn a handled fault into a
fatal one.

The initial probe excluded that final collection-replacement path. 26.9.6127.0
hardened two independent W10M-sensitive surfaces in the experimental
configuration:

* The complete decorative composition setup (`GetElementVisual`, clip creation,
  shadow attachment and relative-size setup) is now inside the existing
  `drawer.construct` recovery block. The sibling emoji and animation drawers
  already did this; the sticker drawer had left its first two composition calls
  outside the block.
* Virtualized item and toolbar paths now verify their template root and first
  image child before reading or writing them. A recycled container can be
  offered before the XAML template is realized on Windows 10 Mobile. The
  bounded `drawer.template|result=unavailable;site=<site>` marker identifies
  that benign skip without logging sticker, chat, or user data.

The 6127 device run recorded one safe `drawer.template` recovery but still
reported three `app.unhandled` NREs. Each occurred immediately before its
paired `GetStickers` and `SearchStickers` results, not during the saved-set
load or toolbar realization. The visual-container guards remain correct, but
they are not the primary fault.

26.9.6128.0 narrows recovery to the two optional, independent throttled search
phases. It catches only `NullReferenceException` around each awaited
`LoadMoreItemsAsync` operation, writes up to two
`drawer.search|phase=<1-or-2>;...` records while the exception still retains
its throw information, and continues from an emoji-suggestion failure to the
independent sticker-set search phase. This is not a general dispatcher catch:
other exception types still surface normally, and a phase failure remains
explicitly diagnostic rather than being treated as success.

The 6128 device run still recorded three `app.unhandled` NREs and **no**
`drawer.search` record, proving the throw occurs before the throttled
phase-one/two awaits. The remaining uncaught `async void` boundary is the
initial `FindStickers` path: current input-language lookup, collection
assignment, or its phase-zero installed-set load.

26.9.6129.0 catches only `NullReferenceException` across that initialization
boundary, writes up to two `drawer.search|phase=0;step=initialize;...` records,
and clears the current search collection so stale results are not presented as
the new query. It does not fabricate a fallback language or silently reuse an
old request; any non-NRE still reaches the normal exception surface.

**Device-confirmed:** the 6129 run exercised `SearchInstalledStickerSets`,
`SearchEmojis`, `GetStickers`, and `SearchStickerSets` across repeated
emoji/text search actions with no `app.unhandled`, `app.unobserved`, or
`drawer.search` record. One `drawer.template|result=unavailable;site=toolbar_prepare`
record confirmed that the template-recycling guard safely skipped an unrealized
toolbar visual. The drawer remained usable throughout the test.

## Fatal crash: RLottie cannot load in Release (26.9.6117.0)

Scrolling a supergroup killed the process outright even though the managed
try/catch blocks above were demonstrably holding - 116 caught faults with the
app still running. The log ended on `FileNotFoundException` HRESULT
`0x8007007E` with no module name, and a crash dump supplied by the user
resolved it.

Method, for reuse:

    cdb -z Unigram.exe.5904.dmp -c ".lastevent; .ecxr; k; lmo; q"

`.lastevent` reported `c000027b` - `STATUS_STOWED_EXCEPTION`, the .NET
Native/WinRT fail-fast wrapper, confirming a managed exception crossed a WinRT
boundary. Managed frames are not resolvable without symbols, but `lmo` is: the
loaded-module list contained `Telegram_Td`, `Unigram_Native`, `avcodec_58`,
`libcrypto_3_arm`, `libssl_3_arm` and `Microsoft_Graphics_Canvas`, and did
**not** contain `RLottie.dll`. **Absence of an expected module is the signal.**

    dumpbin /dependents RLottie.dll   ->  zlib1.dll
    dumpbin /imports    RLottie.dll   ->  inflate, inflateEnd, inflateInit2_

The package shipped `z.dll` (the pinned modern zlib built for TDLib) but no
`zlib1.dll`, because `Unigram.csproj` included that payload only when
`'$(Configuration)' == 'Debug'`. Release packages therefore could not load the
animated-sticker renderer at all; the first TGS stricker raised the
`FileNotFoundException` above, which fail-fasted the process.

This is a **pre-existing upstream packaging defect**, not something the TDLib
port introduced. It had simply never been hit, because Release sideloads were
not the normal test path.

The fix stages the pinned modern zlib a second time under the name RLottie's
import table expects, rather than restoring the bundled zlib 1.2.11 copy:

* `StageModernZlib1` (`AfterTargets="ResolveAssemblyReferences"`, gated on
  `UseModernTdlib`) copies `$(ModernTdlibRoot)\z.dll` to
  `$(IntermediateOutputPath)modern-tdlib\zlib1.dll` and adds it to
  `ReferenceCopyLocalPaths`. `ResolveAssemblyReferences` populates that item
  and `_CopyFilesMarkedCopyLocal` consumes it afterwards, so the ordering is
  required.
* The legacy Debug-only item is scoped with `'$(UseModernTdlib)' != 'true'` so
  the non-modern path is byte-identical.

Loading the same zlib code under two file names is safe: they are separate
module instances with independent state, consumed by different libraries.
Verified in the 26.9.6117.0 package: `zlib1.dll` present at 69,632 bytes
(identical to `z.dll`, i.e. the pinned 1.3.x build and not the 1.2.11 copy),
exporting `inflate`, `inflateEnd` and `inflateInit2_`.

### Missing native module diagnostics

The same log ended with `FileNotFoundException` HRESULT `0x8007007E` ("The
specified module could not be found") with no indication of which module.
`PushDiagnostics.WriteException` now emits `missing_module=` from
`FileNotFoundException.FileName` (and `unnamed` when the name is absent, as it
was here because the failure was a WinRT activation rather than an assembly
load). The field is gated on HRESULT `0x8007007E`, because `WriteException` is
also wired to the app-wide unhandled handler: an ordinary file I/O failure
carries a caller-supplied name that may describe user content, whereas a
module name is a build artifact. The shared sanitizer still strips anything
path-shaped. The only managed `DllImport` in the app
(`ShortcutsService`, `user32.dll`) is commented out, so a "module could not be
found" fault is always a WinRT activation or a native-to-native import
failure, never a P/Invoke.

### Latent: ColorSlider contract gate

`ColorSlider.cs` gates `CreateSpringVector3Animation` (universal API contract
7) on `IsUniversalApiContract5Present`. It is unreachable on this contract-4
device and belongs to the out-of-scope photo editor, so it is recorded here
rather than changed.

## Emoji render blank in chat bubbles (26.9.6118.0, fixed in 26.9.6119.0)

### Symptom

Emoji-only messages produced bubbles containing nothing but the timestamp. Text
messages in the same chat rendered normally, emoji could be typed and sent, and
recipients on Android/iOS saw them correctly. Only *display* on device was
affected.

### Why the rendering code itself was exonerated

Every inline-emoji code path is byte-identical to the stable base `7abe5bdd3`:

    git diff --stat 7abe5bdd3 HEAD -- '*MessageBubble.xaml' '*Themes/*' \
        '*Common/Theme.cs' '*App.xaml' '*TextStyleRun*'

returns empty. The only emoji-related port change is `SearchEmojis` in
`Common/Emoji.cs`, which serves the drawer's search box and cannot affect bubble
rendering. That correctly ruled out a rendering regression - but it also pointed
away from the actual cause, which was never in the rendering code at all: it was
an unhandled *content type* arriving from the newer TDLib.

### First diagnosis (26.9.6118.0) - wrong, recorded deliberately

`Assets\Emoji\apple.ttf` was measured and found to be 99.1 % CBDT colour-bitmap
data (`glyf` 89,192 B of outlines against `CBDT` 13,786,695 B of bitmaps), and
the blank bubbles were attributed to Windows 10 Mobile's DirectWrite honouring
`hmtx` advance widths without compositing those bitmaps. 26.9.6118.0 therefore
switched `EmojiThemeFontFamily` to `XamlAutoFontFamily`.

**The device disproved this.** The 26.9.6118.0 log records

    theme.emoji|set=microsoft;font=system;reason=cbdt_bitmap_not_composited

confirming the system font was in effect, and emoji were *still* blank. The
font-table measurement is factually correct but was never the cause.

The error was an over-read of the first screenshot. The bubbles were not
"correctly sized but empty" - they were **timestamp-width, with no gap reserved
for a glyph at all**. Zero advance width means the inline content is empty, not
that a glyph painted invisibly. Measuring a suspect bubble against a
timestamp-only baseline would have ruled the font out immediately.

### Root cause

TDLib 1.8.0 delivered a lone emoji as `messageText`, and
`DialogViewModel.ProcessEmojiAsync` converted it into a sticker. **TDLib 1.8.66
delivers `messageAnimatedEmoji` instead:**

    messageAnimatedEmoji animated_emoji:animatedEmoji emoji:string = MessageContent;
    animatedEmoji sticker:sticker sticker_width:int32 sticker_height:int32
                  fitzpatrick_type:int32 sound:file = AnimatedEmoji;

That content type matched **no branch** in `MessageBubble.UpdateMessageText`, so
`Span.Inlines` stayed empty and `Message` was collapsed, and none in
`UpdateMessageContent`, so `Media.Child` stayed null. The bubble drew its footer
and nothing else.

Upstream does contain a `MessageAnimatedEmoji` handler, but it is **unreachable
dead code**: it sits in an `else if` nested inside `if (message.Content is
MessageText text)`, and a message cannot be both types. `git diff` against
`7abe5bdd3` confirms this nesting is upstream's, not a port artefact - the port
only swapped the constructor for `ModernTdlibCompatibility.CreateMessageSticker`.
The defect was simply unreachable under 1.8.0, which never sent the type.

### Fix (26.9.6119.0)

`ProcessEmojiAsync` handles `MessageAnimatedEmoji` at the **outer** level under
`MODERN_TDLIB`, as a sibling of the `MessageText` test rather than nested inside
it. When `animatedEmoji.sticker` is present the message is given a generated
`MessageSticker`, which renders through the animated-sticker path already proven
on device. The schema marks `sticker` optional, so a null sticker falls back to
a `MessageBigEmoji` carrying the plain `emoji` string; the upstream dead line
dereferenced it unguarded.

`MessageBubble.UpdateMessageText` also gains a `MessageAnimatedEmoji` branch
that draws the emoji string at `FontSize = 32`. This is a safety net: it
guarantees the bubble can never be empty even for a message that reaches the
bubble without `ProcessEmojiAsync` having run, such as one arriving through an
update handler. Both changes are `MODERN_TDLIB`-only; the legacy path is
untouched, and the unreachable upstream branch is left exactly as it is.

### Status of the 26.9.6118.0 font change

`XamlAutoFontFamily` is **retained but is not the fix**. With single emoji now
rendering as stickers, the font only governs inline emoji inside ordinary text
and multi-emoji `MessageBigEmoji` runs, and there is still no device evidence
either way for those - every blank bubble observed so far was a single emoji,
so the font was never actually exercised. It is kept because it is independently
defensible (the platform family is guaranteed to have emoji coverage) and
because it closed a real silent-`catch` hole. If inline emoji in mixed text turn
out to render worse than the bundled set, reverting `Theme.cs` to the `apple`
family is a one-line change and `apple.ttf` is still shipped for exactly that
reason.

### Diagnostics

The emoji font is resolved in its own scope with its own `catch`, because the
`Theme` constructor is wrapped in a silent `catch { }` and the emoji block was
its last statement - any earlier failure would have left `EmojiThemeFontFamily`
unregistered, leaving every emoji-bearing control with no font at all. A
`theme.emoji` record reports `set=<id>;font=system;reason=...`, and a failed
settings read reports `set=error_<ExceptionType>` rather than being swallowed.
The set id is a preference identifier, not user data; no paths are emitted.

A new `emoji.animated` record is written once per batch, not once per message:

    emoji.animated|count=<n>;with_sticker=<n>

`count` is how many `MessageAnimatedEmoji` messages were seen and
`with_sticker` how many carried a sticker. Counts only - no emoji, no message
text, no identifiers. This confirms on the next device log both that single
emoji really do arrive as this content type and how often the null-sticker
fallback is taken.
