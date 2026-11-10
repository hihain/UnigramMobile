using libtgvoip;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Telegram.Td.Api;
using Unigram.Common;
using Unigram.Controls;
using Unigram.Services.Updates;
using Unigram.Services.ViewService;
using Unigram.ViewModels;
using Unigram.Views;
using Unigram.Views.Popups;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.UI.Core;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
#if MODERN_TGCALLS
using ModernCalls = Unigram.Native.Calls.Proof;
#endif

namespace Unigram.Services
{
    public interface IVoIPService : IHandle<UpdateCall>
#if MODERN_TGCALLS
        , IHandle<UpdateNewCallSignalingData>
#endif
    {
        string CurrentAudioInput { get; set; }
        float CurrentVolumeInput { get; set; }

        string CurrentAudioOutput { get; set; }
        float CurrentVolumeOutput { get; set; }

        Call ActiveCall { get; }

        void Show();
    }

    public class VoIPService : TLViewModelBase, IVoIPService
    {
        private readonly IViewService _viewService;

        private readonly MediaPlayer _mediaPlayer;
        private const int AudioCallDiagnosticBudget = 64;
        private static int _audioCallDiagnosticBudget = AudioCallDiagnosticBudget;

        // The call whose diagnostics budget is currently in force, so a new call can be told
        // apart from further updates to the one already being recorded.
        private int _diagnosticCallId;
        private int _previousDiagnosticCallId;

        private Call _call;
        private DateTime _callStarted;
        private VoIPControllerWrapper _controller;
#if MODERN_TGCALLS
        private enum ModernSignalingQueueResult
        {
            Queued,
            SessionReady,
            LimitExceeded
        }

        private const int MaxPendingModernSignalingMessages = 32;
        private const int MaxPendingModernSignalingBytes = 1024 * 1024;
        private readonly object _modernSignalingLock = new object();
        private ModernCalls.AudioCallSession _modernController;
        private int _modernCallId;
        private volatile bool _modernCallStarting;
        private readonly Dictionary<int, List<List<byte>>> _pendingModernSignalingData = new Dictionary<int, List<List<byte>>>();
        private const float ModernAudibleLevel = 0.01f;
        private string _modernVideoCaptureDeviceId;
        private string _modernPendingVideoCaptureDeviceId;
        private int _modernPendingVideoCaptureIsFront;
        private const int ModernMediaDiagnosticBudget = 192;
        private const int ModernMicrophoneWaitMs = 8000;
        // The CPU/WriteableBitmap preview path is permanently replaced by native
        // composition-surface sinks. Local rendering passed physical-device validation;
        // test the incoming sink as the next isolated renderer milestone.
        private const bool ModernVideoLocalPreviewEnabled = true;
        private const bool ModernVideoRemotePreviewEnabled = true;
        private const int ModernV2H264MaxBitrateKbps = 1536;
        private static readonly object _microphoneLock = new object();
        private static Task<int> _microphoneTask;
        private static readonly object _videoCaptureLock = new object();
        private static Task<VideoCapturePreflight> _videoCaptureTask;
        private readonly object _modernAudioLevelLock = new object();
        private readonly object _modernCallbackFaultLock = new object();
        private readonly HashSet<string> _modernCallbackFaults = new HashSet<string>();
        private int _modernMediaDiagnosticBudget = ModernMediaDiagnosticBudget;
        private int _modernAudioLevelSamples;
        private int _modernAudioLevelActive;
        private float _modernAudioLevelPeak;
        private DateTime _modernAudioLevelReported = DateTime.MinValue;
        private int _modernSignalBars = -1;
        private int _modernSignalingSent;
        private int _modernSignalingReceived;

        // First-occurrence reporting is keyed on the call the message belonged to rather than
        // on the counters above. Keying it on a counter raced the per-call reset: an increment
        // arriving from a torn-down call could leave the counter non-zero, so the next call's
        // first message never looked like the first one and the only per-message evidence was
        // lost. Comparing call ids can at worst emit one extra line, which errs toward evidence.
        private int _modernSignalingSentCallId;
        private int _modernSignalingReceivedCallId;
        private ModernCalls.RemoteAudioState? _modernRemoteAudioState;
        private ModernCalls.CallState? _modernTransportState;
        private bool _modernMuted;
        private int _modernRequestedSpeakerphone = -1;
        private bool _modernVideoOutputsEnabled;
        private sealed class VideoCapturePreflight
        {
            public int Result { get; set; }
            public string DeviceId { get; set; }
            public bool IsFrontCamera { get; set; }
        }
#endif

        private VoIPPage _callPage;
        private OverlayPage _callDialog;
        private ViewLifetimeControl _callLifetime;
        private readonly DisposableMutex _callPageMutex = new DisposableMutex();

        public VoIPService(IProtoService protoService, ICacheService cacheService, ISettingsService settingsService, IEventAggregator aggregator, IViewService viewService)
            : base(protoService, cacheService, settingsService, aggregator)
        {
            _viewService = viewService;

            if (ApiInfo.IsMediaSupported)
            {
                _mediaPlayer = new MediaPlayer();
                _mediaPlayer.CommandManager.IsEnabled = false;
                _mediaPlayer.AudioDeviceType = MediaPlayerAudioDeviceType.Communications;
                _mediaPlayer.AudioCategory = MediaPlayerAudioCategory.Communications;
            }

            aggregator.Subscribe(this);

            _watcher = Windows.Devices.Enumeration.DeviceInformation.CreateWatcher(Windows.Devices.Sensors.ProximitySensor.GetDeviceSelector());
            _watcher.Added += OnProximitySensorAdded;
            _watcher.Start();
        }

        #region Proximity

        private Windows.Devices.Sensors.ProximitySensor _sensor;
        private Windows.Devices.Sensors.ProximitySensorDisplayOnOffController _displayController;
        private Windows.Devices.Enumeration.DeviceWatcher _watcher;

        /// <summary>
        /// Invoked when the device watcher finds a proximity sensor
        /// </summary>
        /// <param name="sender">The device watcher</param>
        /// <param name="device">Device information for the proximity sensor that was found</param>
        private void OnProximitySensorAdded(Windows.Devices.Enumeration.DeviceWatcher sender, Windows.Devices.Enumeration.DeviceInformation device)
        {
            if (_sensor == null && Windows.Devices.Sensors.ProximitySensor.FromId(device.Id) is Windows.Devices.Sensors.ProximitySensor foundSensor)
                _sensor = foundSensor;
        }

        private void EnableDisplayOnOffController()
        {
            if (_sensor != null && _displayController == null)
            {
                // Acquires the display on/off controller for this proximity sensor.
                // This tells the system to use the sensor's IsDetected state to
                // turn the screen on or off.  If the display does not support this
                // feature, this code will do nothing.
                _displayController = _sensor.CreateDisplayOnOffController();
            }
        }

        private void DisableDisplayOnOffController()
        {
            if (_displayController != null)
            {
                _displayController.Dispose(); // closes the controller
                _displayController = null;
            }
        }

        #endregion

        public string CurrentAudioInput
        {
            get
            {
                return _controller?.CurrentAudioInput ?? SettingsService.Current.VoIP.InputDevice;
            }
            set
            {
                SettingsService.Current.VoIP.InputDevice = value;

                if (_controller != null)
                {
                    _controller.CurrentAudioInput = value;
                }
            }
        }

        public float CurrentVolumeInput
        {
            get
            {
                return SettingsService.Current.VoIP.InputVolume;
            }
            set
            {
                SettingsService.Current.VoIP.InputVolume = value;

                if (_controller != null)
                {
                    _controller.SetInputVolume(value);
                }
            }
        }

        public string CurrentAudioOutput
        {
            get
            {
                return _controller?.CurrentAudioOutput ?? SettingsService.Current.VoIP.OutputDevice;
            }
            set
            {
                SettingsService.Current.VoIP.OutputDevice = value;

                if (_controller != null)
                {
                    _controller.CurrentAudioOutput = value;
                }
            }
        }

        public float CurrentVolumeOutput
        {
            get
            {
                return SettingsService.Current.VoIP.OutputVolume;
            }
            set
            {
                SettingsService.Current.VoIP.OutputVolume = value;

                if (_controller != null)
                {
                    _controller.SetOutputVolume(value);
                }
            }
        }

        public async void Handle(UpdateCall update)
        {
            if (update?.Call == null)
            {
                WriteAudioCallDiagnostic("voip.update", "result=ignored;reason=call_unavailable");
                return;
            }

            _call = update.Call;

            // Budgets and first-occurrence flags belong to a call, not to an app launch.
            // Resetting on the first update for a new call id — rather than at Ready —
            // means a call that is declined, errors out, or is discarded while pending is
            // still recorded, and those are exactly the failures worth having evidence for.
            //
            // TDLib interleaves late updates for a discarded call with the first updates of
            // its successor, so the immediately preceding id is remembered too. Without that,
            // each alternation would restore the full budget during a call transition, which
            // is the single busiest logging window and the one the cap exists to bound.
            if (_diagnosticCallId != update.Call.Id)
            {
                if (_previousDiagnosticCallId != update.Call.Id)
                {
                    ResetAudioCallDiagnosticBudget();
                    ModernTdlibCompatibility.ResetAudioCallDiagnosticBudgets();
                }

                _previousDiagnosticCallId = _diagnosticCallId;
                _diagnosticCallId = update.Call.Id;
            }

            WriteAudioCallDiagnostic("voip.update", $"result=received;state={update.Call.State?.GetType().Name ?? "null"};outgoing={update.Call.IsOutgoing.ToString().ToLowerInvariant()};video={update.Call.IsVideo.ToString().ToLowerInvariant()}");

            if (update.Call.State is CallStatePending pending)
            {
#if MODERN_TGCALLS
                DisposeStaleModernCall(update.Call.Id);
                if (update.Call.IsVideo)
                {
                    BeginAcquireVideoCapture();
                }
                else
                {
                    BeginAcquireMicrophone();
                }
#endif
                if (update.Call.IsOutgoing && pending.IsCreated && pending.IsReceived)
                {
                    if (pending.IsCreated && pending.IsReceived)
                    {
                        PlayTone("voip_ringback.mp3", true);
                    }
                }
            }
            if (update.Call.State is CallStateReady ready)
            {
                if (ready.Protocol == null || ready.EncryptionKey == null)
                {
                    WriteAudioCallDiagnostic("voip.ready", "result=skipped;reason=invalid_parameters");
                    ProtoService.Send(ModernTdlibCompatibility.CreateDiscardCall(update.Call.Id, true, 0, 0));
                    return;
                }

                var user = CacheService.GetUser(update.Call.UserId);
                if (user == null)
                {
                    WriteAudioCallDiagnostic("voip.ready", "result=skipped;reason=user_unavailable");
                    return;
                }

#if MODERN_TGCALLS
                var modernVersion = ModernTdlibCompatibility.GetModernAudioCallVersion(ready.Protocol?.LibraryVersions);
                if (!string.IsNullOrEmpty(modernVersion))
                {
                    if (_modernController != null && _modernCallId == update.Call.Id)
                    {
                        return;
                    }

                    WriteAudioCallDiagnostic("voip.ready", "result=selected;transport=modern_tgcalls");
                    var legacyController = _controller;
                    _controller = null;
                    try
                    {
                        legacyController?.Dispose();
                        DisposeModernCall();
                    }
                    catch (Exception error)
                    {
                        WriteAudioCallDiagnostic(
                            "voip.ready",
                            $"result=rejected;reason=transport_cleanup;transport=modern_tgcalls;hresult=0x{error.HResult:X8};message={Logs.PushDiagnostics.SanitizeErrorMessage(error.Message)}");
                        ProtoService.Send(ModernTdlibCompatibility.CreateDiscardCall(update.Call.Id, true, 0, 0));
                        return;
                    }

                    if (TryStartModernCall(update.Call, ready, modernVersion))
                    {
                        WriteAudioCallDiagnostic("voip.ready", "result=starting;transport=modern_tgcalls");
                        BeginOnUIThread(() => Show(update.Call, null, _callStarted));
                        return;
                    }

                    WriteAudioCallDiagnostic("voip.ready", "result=failed;transport=modern_tgcalls");
                    ProtoService.Send(ModernTdlibCompatibility.CreateDiscardCall(update.Call.Id, true, 0, 0));
                    return;
                }

                ClearPendingModernSignalingData(update.Call.Id);
#endif

                VoIPControllerWrapper.UpdateServerConfig(ready.Config);

                var logFile = Path.Combine(Views.SessionStorage.GetSessionDirectory(SessionId), $"voip{update.Call.Id}.txt");
                var statsDumpFile = Path.Combine(Views.SessionStorage.GetSessionDirectory(SessionId), "tgvoip.statsDump.txt");

                var call_packet_timeout_ms = CacheService.Options.CallPacketTimeoutMs;
                var call_connect_timeout_ms = CacheService.Options.CallConnectTimeoutMs;

                if (_controller != null)
                {
                    _controller.Dispose();
                    _controller = null;
                }

                var config = new VoIPConfig
                {
                    initTimeout = call_packet_timeout_ms / 1000.0,
                    recvTimeout = call_connect_timeout_ms / 1000.0,
                    dataSaving = GetVoipDataSavingMode(base.Settings.UseLessData),
                    enableAEC = true,
                    enableNS = true,
                    enableAGC = true,

                    enableVolumeControl = true,

                    logFilePath = logFile,
                    statsDumpFilePath = statsDumpFile
                };
                
                _controller = new VoIPControllerWrapper();
                _controller.SetConfig(config);
                _controller.CurrentAudioInput = SettingsService.Current.VoIP.InputDevice;
                _controller.CurrentAudioOutput = SettingsService.Current.VoIP.OutputDevice;
                _controller.SetInputVolume(SettingsService.Current.VoIP.InputVolume);
                _controller.SetOutputVolume(SettingsService.Current.VoIP.OutputVolume);

                _controller.CallStateChanged += (s, args) =>
                {
                    var error = args == libtgvoip.CallState.Failed
                        ? $";error={s.GetLastError()}"
                        : string.Empty;
                    WriteAudioCallDiagnostic("voip.transport", $"result=state;state={args}{error}");

                    BeginOnUIThread(() =>
                    {
                        if (args == libtgvoip.CallState.WaitInit || args == libtgvoip.CallState.WaitInitAck)
                        {
                            PlayTone("voip_connecting.mp3", false);
                        }
                        else if (args == libtgvoip.CallState.Established)
                        {
                            _callStarted = DateTime.Now;
                            StopTone();
                        }
                    });
                };

                BeginOnUIThread(() =>
                {
                    Show(update.Call, _controller, _callStarted);
                });

                var endpoints = new List<Endpoint>();
                var webRtcEndpointCount = 0;

                foreach (var server in ready.Servers ?? new CallServer[0])
                {
                    if (server.Type is CallServerTypeTelegramReflector telegramReflector)
                    {
                        endpoints.Add(new Endpoint
                        {
                            id = server.Id,
                            ipv4 = server.IpAddress,
                            ipv6 = server.Ipv6Address,
                            peerTag = telegramReflector.PeerTag.ToArray(),
                            port = (ushort)server.Port
                        });
                    }
                    else if (server.Type is CallServerTypeWebrtc)
                    {
                        webRtcEndpointCount++;
                    }
                }

                if (endpoints.Count == 0)
                {
                    WriteAudioCallDiagnostic("voip.ready", $"result=skipped;reason=no_reflector_endpoint;webrtc_endpoints={webRtcEndpointCount}");
                    _controller.Dispose();
                    _controller = null;
                    ProtoService.Send(ModernTdlibCompatibility.CreateDiscardCall(update.Call.Id, true, 0, 0));
                    return;
                }

                WriteAudioCallDiagnostic("voip.ready", $"result=starting;reflector_endpoints={endpoints.Count};webrtc_endpoints={webRtcEndpointCount};p2p={ready.Protocol.UdpP2p && ready.AllowP2p};protocol_min={ready.Protocol.MinLayer};protocol_max={ready.Protocol.MaxLayer};library_versions={ready.Protocol.LibraryVersions?.Count ?? 0}");
                _controller.SetEncryptionKey(ready.EncryptionKey.ToArray(), update.Call.IsOutgoing);
                _controller.SetPublicEndpoints(endpoints.ToArray(), ready.Protocol.UdpP2p && ready.AllowP2p, ready.Protocol.MaxLayer);
                _controller.Start();
                _controller.Connect();
            }
            else if (update.Call.State is CallStateDiscarded discarded)
            {
                if (discarded.NeedDebugInformation && _controller != null)
                {
                    ProtoService.Send(ModernTdlibCompatibility.CreateSendCallDebugInformation(update.Call.Id, _controller.GetDebugLog()));
                }

                if (discarded.NeedRating)
                {
                    BeginOnUIThread(async () => await SendRatingAsync(update.Call.Id));
                }

                _controller?.Dispose();
                _controller = null;
#if MODERN_TGCALLS
                ClearPendingModernSignalingData(update.Call.Id);
                DisposeModernCall();
#endif
                _call = null;
            }

            await Dispatcher.DispatchAsync(() =>
            {
                switch (update.Call.State)
                {
                    case CallStateDiscarded discarded:
                        if (update.Call.IsOutgoing && discarded.Reason is CallDiscardReasonDeclined)
                        {
                            PlayTone("voip_busy.mp3", true);

                            Show(update.Call, null, _callStarted);
                        }
                        else
                        {
                            StopTone();

                            Hide();
                        }
                        break;
                    case CallStateError error:
                        WriteAudioCallDiagnostic(
                            "voip.error",
                            $"code={error.Error?.Code ?? 0};message={Logs.PushDiagnostics.SanitizeErrorMessage(error.Error?.Message)}");
                        StopTone();
                        Hide();
                        break;
                    default:
                        Show(update.Call, null, _callStarted);
                        break;
                }
            });
        }

#if MODERN_TGCALLS
        public void Handle(UpdateNewCallSignalingData update)
        {
            if (update == null || update.Data == null)
            {
                WriteAudioCallDiagnostic("voip.signaling", "result=ignored;reason=invalid_update");
                return;
            }

            var session = _modernController;
            if (session != null && update.CallId == _modernCallId)
            {
                switch (QueueModernSignalingDataIfSessionStarting(update.CallId, update.Data))
                {
                    case ModernSignalingQueueResult.Queued:
                        WriteAudioCallDiagnostic("voip.signaling", "result=queued;reason=session_unavailable");
                        return;
                    case ModernSignalingQueueResult.LimitExceeded:
                        WriteAudioCallDiagnostic("voip.signaling", "result=rejected;reason=buffer_limit");
                        ClearPendingModernSignalingData(update.CallId);
                        ProtoService.Send(ModernTdlibCompatibility.CreateDiscardCall(update.CallId, true, 0, 0));
                        return;
                }

                try
                {
                    session.ReceiveSignalingData(update.Data.ToList());
                    System.Threading.Interlocked.Increment(ref _modernSignalingReceived);

                    // Only the first delivery of a call is reported; the rest are counted and
                    // folded into the periodic media summary. Logging every message spent the
                    // whole call budget on fifty near-identical lines.
                    if (System.Threading.Interlocked.Exchange(ref _modernSignalingReceivedCallId, update.CallId) != update.CallId)
                    {
                        WriteAudioCallDiagnostic("voip.signaling", "result=received;transport=modern_tgcalls");
                    }
                }
                catch (ArgumentException)
                {
                    WriteAudioCallDiagnostic("voip.signaling", "result=rejected;transport=modern_tgcalls");
                }
                return;
            }

            if (_call != null && update.CallId == _call.Id)
            {
                if (TryQueueModernSignalingData(update.CallId, update.Data))
                {
                    WriteAudioCallDiagnostic("voip.signaling", "result=queued;reason=session_unavailable");
                }
                else
                {
                    WriteAudioCallDiagnostic("voip.signaling", "result=rejected;reason=buffer_limit");
                    ClearPendingModernSignalingData(update.CallId);
                    ProtoService.Send(ModernTdlibCompatibility.CreateDiscardCall(update.CallId, true, 0, 0));
                }
                return;
            }

            WriteAudioCallDiagnostic("voip.signaling", "result=ignored;reason=no_matching_modern_call");
        }

        /// <summary>
        /// Opens the microphone through the supported UWP capture API before the call engine
        /// touches it. The engine activates the capture endpoint directly, which on this
        /// platform terminates the process rather than returning an error when the endpoint
        /// cannot be opened, so the failure is invisible and uncatchable. Going through
        /// MediaCapture first performs the consent flow and surfaces any failure as an
        /// ordinary HRESULT that can be reported and acted on.
        /// </summary>
        private void BeginAcquireMicrophone()
        {
            lock (_microphoneLock)
            {
                if (_microphoneTask != null)
                {
                    return;
                }

                _microphoneTask = AcquireMicrophoneAsync();
            }
        }

        private Task<int> AcquireMicrophoneAsync()
        {
            var completion = new TaskCompletionSource<int>();

            BeginOnUIThread(async () =>
            {
                var result = 0;
                Windows.Media.Capture.MediaCapture capture = null;

                try
                {
                    capture = new Windows.Media.Capture.MediaCapture();
                    await capture.InitializeAsync(new Windows.Media.Capture.MediaCaptureInitializationSettings
                    {
                        StreamingCaptureMode = Windows.Media.Capture.StreamingCaptureMode.Audio,
                        MediaCategory = Windows.Media.Capture.MediaCategory.Communications
                    });

                    WriteModernMediaDiagnostic("result=microphone;acquired=1");
                }
                catch (Exception error)
                {
                    result = error.HResult == 0 ? -1 : error.HResult;
                    WriteModernMediaDiagnostic($"result=microphone;acquired=0;hresult=0x{result:X8}");
                }
                finally
                {
                    try
                    {
                        capture?.Dispose();
                    }
                    catch
                    {
                        // Disposal failures are not interesting and must not mask the result.
                    }

                    completion.TrySetResult(result);
                }
            });

            return completion.Task;
        }

        /// <summary>
        /// Blocks the update thread, never the UI thread, until the microphone has been
        /// opened. A timeout is treated as failure: the only case that actually reaches it
        /// is the first call on a device, where the consent prompt is still waiting to be
        /// answered, and that is exactly the state in which letting the engine activate the
        /// capture device would kill the process. Rejecting the call leaves the prompt up,
        /// so answering it once makes every later call work.
        /// </summary>
        private bool WaitForMicrophone()
        {
            Task<int> task;
            lock (_microphoneLock)
            {
                task = _microphoneTask;
            }

            if (task == null)
            {
                BeginAcquireMicrophone();
                lock (_microphoneLock)
                {
                    task = _microphoneTask;
                }
            }

            try
            {
                if (!task.Wait(ModernMicrophoneWaitMs))
                {
                    // The attempt is deliberately left in place rather than discarded, so
                    // that answering the prompt completes it for the next call.
                    WriteModernMediaDiagnostic("result=microphone;acquired=0;reason=consent_pending");
                    return false;
                }
            }
            catch (Exception error)
            {
                WriteModernMediaDiagnostic($"result=microphone;acquired=0;hresult=0x{error.HResult:X8}");
                return false;
            }

            if (task.Result != 0)
            {
                // Retry from scratch next time: a transient failure such as the device
                // being held by another app must not wedge every later call.
                lock (_microphoneLock)
                {
                    _microphoneTask = null;
                }

                return false;
            }

            return true;
        }

        private void BeginAcquireVideoCapture()
        {
            lock (_videoCaptureLock)
            {
                if (_videoCaptureTask == null)
                {
                    _videoCaptureTask = AcquireVideoCaptureAsync();
                }
            }
        }

        private Task<VideoCapturePreflight> AcquireVideoCaptureAsync()
        {
            var completion = new TaskCompletionSource<VideoCapturePreflight>();
            BeginOnUIThread(async () =>
            {
                var result = new VideoCapturePreflight();
                Windows.Media.Capture.MediaCapture capture = null;
                try
                {
                    var devices = await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(
                        Windows.Devices.Enumeration.DeviceClass.VideoCapture);
                    var device = devices.FirstOrDefault(x =>
                        x.EnclosureLocation?.Panel == Windows.Devices.Enumeration.Panel.Front)
                        ?? devices.FirstOrDefault();

                    if (device == null)
                    {
                        result.Result = -1;
                    }
                    else
                    {
                        capture = new Windows.Media.Capture.MediaCapture();
                        await capture.InitializeAsync(new Windows.Media.Capture.MediaCaptureInitializationSettings
                        {
                            VideoDeviceId = device.Id,
                            StreamingCaptureMode = Windows.Media.Capture.StreamingCaptureMode.AudioAndVideo,
                            MediaCategory = Windows.Media.Capture.MediaCategory.Communications
                        });
                        result.DeviceId = device.Id;
                        result.IsFrontCamera = device.EnclosureLocation?.Panel == Windows.Devices.Enumeration.Panel.Front;
                        WriteModernMediaDiagnostic($"result=video_capture;acquired=1;front={(result.IsFrontCamera ? 1 : 0)};selector=uwp_id");
                    }
                }
                catch (Exception error)
                {
                    result.Result = error.HResult == 0 ? -1 : error.HResult;
                    WriteModernMediaDiagnostic($"result=video_capture;acquired=0;hresult=0x{result.Result:X8}");
                }
                finally
                {
                    capture?.Dispose();
                    completion.TrySetResult(result);
                }
            });
            return completion.Task;
        }

        private VideoCapturePreflight WaitForVideoCapture()
        {
            BeginAcquireVideoCapture();
            Task<VideoCapturePreflight> task;
            lock (_videoCaptureLock)
            {
                task = _videoCaptureTask;
            }

            if (task == null ||
                !task.Wait(ModernMicrophoneWaitMs) ||
                task.Result.Result != 0 ||
                string.IsNullOrEmpty(task.Result.DeviceId))
            {
                lock (_videoCaptureLock)
                {
                    _videoCaptureTask = null;
                }
                return null;
            }
            return task.Result;
        }

        private bool SwitchModernVideoCaptureDevice(string deviceId, bool isFrontCamera)
        {
            var session = _modernController;
            if (session == null || string.IsNullOrEmpty(deviceId))
            {
                return false;
            }

            if (System.Threading.Interlocked.CompareExchange(
                    ref _modernPendingVideoCaptureDeviceId,
                    deviceId,
                    null) != null)
            {
                WriteModernMediaDiagnostic("result=video_capture;state=device_switch_pending");
                return false;
            }

            System.Threading.Interlocked.Exchange(
                ref _modernPendingVideoCaptureIsFront,
                isFrontCamera ? 1 : 0);

            try
            {
                session.SwitchVideoCaptureDevice(deviceId, isFrontCamera);
                WriteModernMediaDiagnostic(
                    $"result=video_capture;state=device_switch_requested;front={(isFrontCamera ? 1 : 0)};selector=uwp_id");
                return true;
            }
            catch (Exception error)
            {
                System.Threading.Interlocked.Exchange(ref _modernPendingVideoCaptureDeviceId, null);
                System.Threading.Interlocked.Exchange(ref _modernPendingVideoCaptureIsFront, 0);
                WriteModernMediaDiagnostic($"result=video_capture;state=device_switch_failed;hresult=0x{error.HResult:X8}");
                return false;
            }
        }

        private bool ConfigureModernVideoControls(VoIPPage callPage, int callId)
        {
            if (callPage == null || _modernController == null || _modernCallId != callId)
            {
                return false;
            }

            callPage.ModernVideoDeviceRequested = SwitchModernVideoCaptureDevice;
            callPage.SetModernVideoCaptureDevice(_modernVideoCaptureDeviceId);
            callPage.EnableModernVideoControls();
            return true;
        }

        private void EnableModernVideoControls(int callId)
        {
            var callPage = _callPage;
            if (callPage == null)
            {
                WriteModernMediaDiagnostic("result=camera_controls;state=deferred");
                return;
            }

            try
            {
                callPage.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                {
                    try
                    {
                        WriteModernMediaDiagnostic(
                            ConfigureModernVideoControls(callPage, callId)
                                ? "result=camera_controls;state=enabled"
                                : "result=camera_controls;state=not_ready");
                    }
                    catch (Exception error)
                    {
                        WriteModernMediaDiagnostic(
                            $"result=camera_controls;state=failed;hresult=0x{error.HResult:X8}");
                    }
                });
            }
            catch (Exception error)
            {
                WriteModernMediaDiagnostic(
                    $"result=camera_controls;state=dispatch_failed;hresult=0x{error.HResult:X8}");
            }
        }

        private static bool _modernCrashDiagnosticsEnabled;

        private void EnableModernCrashDiagnostics()
        {
            if (_modernCrashDiagnosticsEnabled)
            {
                return;
            }

            _modernCrashDiagnosticsEnabled = true;

            try
            {
                Logs.PushDiagnostics.DrainFaultFile();

                var path = Logs.PushDiagnostics.GetFaultFilePath();
                if (!string.IsNullOrEmpty(path))
                {
                    ModernCalls.Diagnostics.EnableCrashDiagnostics(path);

                    // Enabling the native reporter is what recovers the step left in flight by a
                    // previous process, and it appends that record to the file the drain above
                    // has already consumed. Draining a second time is what makes the record
                    // visible in this launch instead of the one after it.
                    Logs.PushDiagnostics.DrainFaultFile();
                }
            }
            catch (Exception error)
            {
                WriteModernMediaDiagnostic($"result=fault_reporter;enabled=0;hresult=0x{error.HResult:X8}");
            }
        }

        private bool TryStartModernCall(Call call, CallStateReady ready, string version)
        {
            WriteAudioCallDiagnostic("voip.ready", "result=bridge_dispatch;transport=modern_tgcalls");
            try
            {
                return TryStartModernCallWithBridgeTypes(call, ready, version);
            }
            catch (Exception error)
            {
                WriteAudioCallDiagnostic(
                    "voip.ready",
                    $"result=rejected;reason=bridge_dispatch;transport=modern_tgcalls;hresult=0x{error.HResult:X8};message={Logs.PushDiagnostics.SanitizeErrorMessage(error.Message)}");
                return false;
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private bool TryStartModernCallWithBridgeTypes(Call call, CallStateReady ready, string version)
        {
            if (ready.Protocol == null || ready.EncryptionKey == null)
            {
                return false;
            }

            // tgcalls maps callConnectTimeoutMs to initializationTimeout and callPacketTimeoutMs
            // to receiveTimeout; both server options are reported as 0 on this account, so fall
            // back to the upstream defaults rather than handing the engine a zero timeout.
            var connectTimeout = CacheService.Options.CallConnectTimeoutMs;
            var packetTimeout = CacheService.Options.CallPacketTimeoutMs;

            var configuration = new ModernCalls.AudioCallConfiguration
            {
                Version = version,
                InitializationTimeout = (connectTimeout > 0 ? connectTimeout : 30000) / 1000.0,
                ReceiveTimeout = (packetTimeout > 0 ? packetTimeout : 10000) / 1000.0,
                EnableP2P = ready.Protocol.UdpP2p && ready.AllowP2p,
                AllowTcp = false,
                MaxApiLayer = ready.Protocol.MaxLayer,
                IsOutgoing = call.IsOutgoing,
                IsVideo = call.IsVideo,
                InitialNetworkType = ModernCalls.NetworkType.Unknown,
                EncryptionKey = ready.EncryptionKey.ToList()
            };

            var servers = ready.Servers ?? new CallServer[0];
            var reflectorIds = servers
                .Where(server => server?.Type is CallServerTypeTelegramReflector)
                .Select(server => server.Id)
                .OrderBy(id => id)
                .ToList();

            foreach (var server in servers)
            {
                if (server.Port <= 0 || server.Port > ushort.MaxValue)
                {
                    WriteAudioCallDiagnostic("voip.ready", "result=rejected;reason=invalid_server_port;transport=modern_tgcalls");
                    return false;
                }

                if (server.Type is CallServerTypeTelegramReflector reflector)
                {
                    if ((string.IsNullOrWhiteSpace(server.IpAddress) && string.IsNullOrWhiteSpace(server.Ipv6Address)) ||
                        reflector.PeerTag == null)
                    {
                        WriteAudioCallDiagnostic("voip.ready", "result=rejected;reason=invalid_reflector;transport=modern_tgcalls");
                        return false;
                    }

                    configuration.ReflectorEndpoints.Add(new ModernCalls.ReflectorEndpoint
                    {
                        Id = server.Id,
                        Ipv4Address = server.IpAddress,
                        Ipv6Address = server.Ipv6Address ?? string.Empty,
                        Port = (ushort)server.Port,
                        IsTcp = reflector.IsTcp,
                        PeerTag = reflector.PeerTag.ToList()
                    });

                    var reflectorIndex = reflectorIds.BinarySearch(server.Id);
                    if (reflectorIndex < 0)
                    {
                        WriteAudioCallDiagnostic("voip.ready", "result=rejected;reason=missing_reflector_index;transport=modern_tgcalls");
                        return false;
                    }

                    configuration.RtcServers.Add(CreateModernReflectorRtcServer(server, reflector, unchecked((byte)reflectorIndex)));
                }
                else if (server.Type is CallServerTypeWebrtc webRtc)
                {
                    if (string.IsNullOrWhiteSpace(server.IpAddress) && string.IsNullOrWhiteSpace(server.Ipv6Address))
                    {
                        WriteAudioCallDiagnostic("voip.ready", "result=rejected;reason=invalid_webrtc_server;transport=modern_tgcalls");
                        return false;
                    }

                    var hosts = GetModernRtcServerHosts(server).ToList();
                    if (webRtc.SupportsStun)
                    {
                        foreach (var host in hosts)
                        {
                            configuration.RtcServers.Add(CreateModernRtcServer(server, webRtc, host, false));
                        }
                    }

                    if (webRtc.SupportsTurn &&
                        !string.IsNullOrWhiteSpace(webRtc.Username) &&
                        !string.IsNullOrWhiteSpace(webRtc.Password))
                    {
                        foreach (var host in hosts)
                        {
                            configuration.RtcServers.Add(CreateModernRtcServer(server, webRtc, host, true));
                        }
                    }
                }
            }

            WriteAudioCallDiagnostic(
                "voip.ready",
                $"result=servers_mapped;reflector_endpoints={configuration.ReflectorEndpoints.Count};rtc_servers={configuration.RtcServers.Count};transport=modern_tgcalls");

            if (configuration.ReflectorEndpoints.Count == 0 && configuration.RtcServers.Count == 0)
            {
                WriteAudioCallDiagnostic("voip.ready", "result=rejected;reason=no_supported_server;transport=modern_tgcalls");
                return false;
            }

            VideoCapturePreflight videoCapture = null;
            if (call.IsVideo)
            {
                videoCapture = WaitForVideoCapture();
                if (videoCapture == null)
                {
                    WriteAudioCallDiagnostic("voip.ready", "result=rejected;reason=video_capture_unavailable;transport=modern_tgcalls");
                    return false;
                }
                configuration.CameraDeviceId = videoCapture.DeviceId;
                configuration.CameraIsFront = videoCapture.IsFrontCamera;
            }
            else if (!WaitForMicrophone())
            {
                WriteAudioCallDiagnostic("voip.ready", "result=rejected;reason=microphone_unavailable;transport=modern_tgcalls");
                return false;
            }

            EnableModernCrashDiagnostics();

            ModernCalls.AudioCallSession session;
            try
            {
                WriteAudioCallDiagnostic("voip.ready", "result=creating;transport=modern_tgcalls");
                session = ModernCalls.AudioCallSession.Create(configuration);
                if (call.IsVideo)
                {
                    WriteModernMediaDiagnostic(
                        $"result=video_encoder;transport=modern_tgcalls;capture=1280x720;fps=30;max_bitrate_kbps={ModernV2H264MaxBitrateKbps}");
                }
            }
            catch (Exception error)
            {
                WriteAudioCallDiagnostic(
                    "voip.ready",
                    $"result=rejected;reason=bridge_create;transport=modern_tgcalls;hresult=0x{error.HResult:X8};message={Logs.PushDiagnostics.SanitizeErrorMessage(error.Message)}");
                return false;
            }

            try
            {
                session.StateChanged += (sender, state) =>
                    GuardModernCallback("state_changed", () => OnModernCallStateChanged(call.Id, state));
                session.SignalingData += (sender, data) =>
                    GuardModernCallback("signaling_data", () => SendModernSignalingData(call.Id, session, data));
                session.Stopped += (sender, completed) =>
                    GuardModernCallback("stopped", () => OnModernCallStopped(call.Id, session, completed));
                session.AudioDeviceReport += (sender, report) =>
                    GuardModernCallback("audio_device_report", () => OnModernAudioDeviceReport(call.Id, report));
                session.SignalBarsChanged += (sender, bars) =>
                    GuardModernCallback("signal_bars", () => OnModernSignalBarsChanged(call.Id, bars));
                session.AudioLevelChanged += (sender, level) =>
                    GuardModernCallback("audio_level", () => OnModernAudioLevelChanged(call.Id, level));
                session.RemoteAudioStateChanged += (sender, state) =>
                    GuardModernCallback("remote_audio_state", () => OnModernRemoteAudioStateChanged(call.Id, state));
                session.RemoteVideoStateChanged += (sender, state) =>
                    GuardModernCallback("remote_video_state", () => WriteModernMediaDiagnostic($"result=remote_video;transport=modern_tgcalls;state={state}"));
                session.VideoCaptureFailed += (sender, ignored) =>
                    GuardModernCallback("video_capture_failed", () => WriteModernMediaDiagnostic("result=video_capture;transport=modern_tgcalls;state=failed"));
                session.VideoCaptureSwitchCompleted += (sender, succeeded) =>
                    GuardModernCallback("video_capture_switch_completed", () =>
                    {
                        var deviceId = System.Threading.Interlocked.Exchange(
                            ref _modernPendingVideoCaptureDeviceId,
                            null);
                        var isFrontCamera = System.Threading.Interlocked.Exchange(
                            ref _modernPendingVideoCaptureIsFront,
                            0) != 0;
                        if (succeeded && !string.IsNullOrEmpty(deviceId))
                        {
                            _modernVideoCaptureDeviceId = deviceId;
                            var callPage = _callPage;
                            callPage?.Dispatcher.RunAsync(
                                Windows.UI.Core.CoreDispatcherPriority.Normal,
                                () => callPage.CompleteModernVideoCaptureDeviceSwitch(deviceId, true));
                        }
                        else
                        {
                            var callPage = _callPage;
                            callPage?.Dispatcher.RunAsync(
                                Windows.UI.Core.CoreDispatcherPriority.Normal,
                                () => callPage.CompleteModernVideoCaptureDeviceSwitch(null, false));
                        }

                        WriteModernMediaDiagnostic(
                            $"result=video_capture;state=device_switch_{(succeeded ? "completed" : "failed")};front={(isFrontCamera ? 1 : 0)};selector=uwp_id");
                    });
                session.VideoOutputFailed += (sender, hresult) =>
                    GuardModernCallback("video_output", () =>
                        WriteModernMediaDiagnostic($"result=video_output;transport=modern_tgcalls;state=native_render_failed;hresult=0x{hresult:X8}"));

                _modernController = session;
                _modernCallId = call.Id;
                if (videoCapture != null)
                {
                    _modernVideoCaptureDeviceId = videoCapture.DeviceId;
                }
                _modernCallStarting = true;
                session.Start();
                var requestedSpeakerphone = System.Threading.Interlocked.CompareExchange(
                    ref _modernRequestedSpeakerphone,
                    -1,
                    -1);
                if (requestedSpeakerphone >= 0)
                {
                    SetModernAudioOutputEndpoint(requestedSpeakerphone != 0);
                }
                if (call.IsVideo)
                {
                    EnableModernVideoControls(call.Id);
                }
                if (_modernMuted)
                {
                    // The toggle can be flipped before the session exists, so re-apply it
                    // rather than silently starting an unmuted call.
                    ApplyModernMuted(_modernMuted);
                }

                if (!FlushPendingModernSignalingData(call.Id, session))
                {
                    DisposeModernCall();
                    return false;
                }
                return true;
            }
            catch (Exception error)
            {
                DisposeModernCall();
                WriteAudioCallDiagnostic(
                    "voip.ready",
                    $"result=rejected;reason=bridge_start;transport=modern_tgcalls;hresult=0x{error.HResult:X8};message={Logs.PushDiagnostics.SanitizeErrorMessage(error.Message)}");
                return false;
            }
        }

        private static ModernCalls.RtcServer CreateModernReflectorRtcServer(
            CallServer server,
            CallServerTypeTelegramReflector reflector,
            byte id)
        {
            return new ModernCalls.RtcServer
            {
                Id = id,
                Host = string.IsNullOrWhiteSpace(server.IpAddress) ? server.Ipv6Address : server.IpAddress,
                Port = (ushort)server.Port,
                Username = "reflector",
                Password = string.Concat(reflector.PeerTag.Select(value => value.ToString("X2"))),
                IsTurn = true,
                IsTcp = reflector.IsTcp
            };
        }

        private static IEnumerable<string> GetModernRtcServerHosts(CallServer server)
        {
            if (!string.IsNullOrWhiteSpace(server.IpAddress))
            {
                yield return server.IpAddress;
            }

            if (!string.IsNullOrWhiteSpace(server.Ipv6Address))
            {
                yield return server.Ipv6Address;
            }
        }

        private static ModernCalls.RtcServer CreateModernRtcServer(
            CallServer server,
            CallServerTypeWebrtc webRtc,
            string host,
            bool isTurn)
        {
            return new ModernCalls.RtcServer
            {
                Id = unchecked((byte)server.Id),
                Host = host,
                Port = (ushort)server.Port,
                Username = webRtc.Username ?? string.Empty,
                Password = webRtc.Password ?? string.Empty,
                IsTurn = isTurn,
                IsTcp = false
            };
        }

        /// <summary>
        /// tgcalls raises these events on its own native worker threads. A managed exception that
        /// escapes back into native code there is never routed to <see cref="Application.UnhandledException"/>,
        /// so .NET Native fail-fasts the process instead of reporting it. Every callback is therefore
        /// contained here, and the failure is recorded as a diagnostic naming only the callback and
        /// the exception type so a fault can be attributed without ending the call.
        /// </summary>
        private void GuardModernCallback(string name, Action body)
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                try
                {
                    // A null dereference in a callback recurs on every invocation, and audio levels
                    // alone arrive ten times a second, so reporting each one would exhaust the
                    // process-wide call diagnostic budget within seconds and silence the rest of
                    // the log. One line per callback is enough to name the offender.
                    bool first;
                    lock (_modernCallbackFaultLock)
                    {
                        first = _modernCallbackFaults.Add(name);
                    }

                    if (first)
                    {
                        WriteAudioCallDiagnostic(
                            "voip.callback",
                            $"result=error;transport=modern_tgcalls;name={name};type={ex.GetType().Name};hresult=0x{ex.HResult:X8}");
                    }
                }
                catch
                {
                    // A diagnostic must never be the reason a callback escapes into native code.
                }
            }
        }

        /// <summary>
        /// Reports the platform audio device module outcome once per call. A null or
        /// uninitialised module mutes the call without failing the transport, so this is the
        /// only signal that distinguishes an audio device fault from a network fault.
        /// </summary>
        private void OnModernAudioDeviceReport(int callId, string report)
        {
            if (_modernCallId != callId)
            {
                return;
            }

            WriteModernMediaDiagnostic(
                $"result=audio_device;transport=modern_tgcalls;{Logs.PushDiagnostics.SanitizeErrorMessage(report)}");
        }

        /// <summary>
        /// Receives the call page's mute toggle. VoIPControllerWrapper is never created when
        /// the modern engine is in use, so without this the toggle has no effect at all.
        /// </summary>
        private void SetModernMuted(bool muted)
        {
            _modernMuted = muted;
            ApplyModernMuted(muted);
        }

        private void ApplyModernMuted(bool muted)
        {
            var session = _modernController;
            if (session == null)
            {
                WriteAudioCallDiagnostic("voip.media", $"result=mute;transport=modern_tgcalls;muted={(muted ? 1 : 0)};applied=0");
                return;
            }

            try
            {
                session.SetMuted(muted);
                WriteAudioCallDiagnostic("voip.media", $"result=mute;transport=modern_tgcalls;muted={(muted ? 1 : 0)};applied=1");
            }
            catch (Exception error)
            {
                WriteAudioCallDiagnostic(
                    "voip.media",
                    $"result=mute;transport=modern_tgcalls;muted={(muted ? 1 : 0)};applied=0;hresult=0x{error.HResult:X8}");
            }
        }

        /// <summary>
        /// Selects the earpiece or loudspeaker through the active TgCalls media-device
        /// path. This matches current upstream Unigram's output-device integration:
        /// it passes an audio output identifier to TgCalls rather than relying on the
        /// UI-level routing manager to alter an already-created audio device module.
        /// </summary>
        private bool SetModernAudioOutputEndpoint(bool speakerphone)
        {
            System.Threading.Interlocked.Exchange(
                ref _modernRequestedSpeakerphone,
                speakerphone ? 1 : 0);
            var session = _modernController;
            var requested = speakerphone ? "speakerphone" : "earpiece";
            if (session == null)
            {
                WriteAudioCallDiagnostic(
                    "voip.media",
                    $"result=audio_output;transport=modern_tgcalls;requested={requested};queued=1;reason=session_pending");
                return true;
            }

            try
            {
                var nativeResult = session.SetAudioOutputEndpoint(speakerphone) ?? "unreadable";
                var queued = nativeResult.StartsWith("queued;", StringComparison.Ordinal);
                WriteAudioCallDiagnostic(
                    "voip.media",
                    $"result=audio_output;transport=modern_tgcalls;requested={requested};queued={(queued ? 1 : 0)}" +
                    $";native={Logs.PushDiagnostics.SanitizeErrorMessage(nativeResult)}");
                return queued;
            }
            catch (Exception error)
            {
                WriteAudioCallDiagnostic(
                    "voip.media",
                    $"result=audio_output;transport=modern_tgcalls;requested={requested};queued=0;hresult=0x{error.HResult:X8}");
                return false;
            }
        }

        private void OnModernSignalBarsChanged(int callId, int bars)
        {
            if (_modernCallId != callId || _modernSignalBars == bars)
            {
                return;
            }

            _modernSignalBars = bars;
            WriteModernMediaDiagnostic($"result=signal_bars;transport=modern_tgcalls;bars={bars}");

            var callPage = _callPage;
            if (callPage != null)
            {
                callPage.BeginOnUIThread(() => callPage.SetSignalBars(bars));
            }
        }

        private void OnModernRemoteAudioStateChanged(int callId, ModernCalls.RemoteAudioState state)
        {
            if (_modernCallId != callId || _modernRemoteAudioState == state)
            {
                return;
            }

            _modernRemoteAudioState = state;
            WriteModernMediaDiagnostic($"result=remote_audio;transport=modern_tgcalls;state={state}");
        }

        /// <summary>
        /// tgcalls raises an audio level ten times a second, so the samples are accumulated
        /// and summarised instead of logged individually. The engine reports the larger of the
        /// local capture level and the decoded remote level, so this value alone cannot tell
        /// the two directions apart; the audio device recording and playout flags recorded
        /// alongside it are what distinguish a capture fault from a playout fault. Only the
        /// sample count and a quantised peak amplitude are recorded; neither describes the audio.
        /// </summary>
        private void OnModernAudioLevelChanged(int callId, float level)
        {
            if (_modernCallId != callId)
            {
                return;
            }

            string summary = null;

            lock (_modernAudioLevelLock)
            {
                _modernAudioLevelSamples++;
                if (level > _modernAudioLevelPeak)
                {
                    _modernAudioLevelPeak = level;
                }

                if (level > ModernAudibleLevel)
                {
                    _modernAudioLevelActive++;
                }

                var now = DateTime.Now;
                if (_modernAudioLevelReported == DateTime.MinValue)
                {
                    _modernAudioLevelReported = now;
                }
                else if (now - _modernAudioLevelReported >= TimeSpan.FromSeconds(5))
                {
                    summary = $"result=audio_level;transport=modern_tgcalls;samples={_modernAudioLevelSamples}" +
                        $";active={_modernAudioLevelActive};peak={_modernAudioLevelPeak.ToString("F2", CultureInfo.InvariantCulture)}" +
                        $";sig_sent={_modernSignalingSent};sig_recv={_modernSignalingReceived}";
                    _modernAudioLevelReported = now;
                    _modernAudioLevelSamples = 0;
                    _modernAudioLevelActive = 0;
                    _modernAudioLevelPeak = 0f;
                }
            }

            if (summary == null)
            {
                return;
            }

            var session = _modernController;
            if (session != null)
            {
                try
                {
                    // The native status is a fixed-shape list of our own keys and integers,
                    // but it still goes through the shared sanitizer so a device name can
                    // never leak. The default 256-character cap truncates the tail of that
                    // list, so it is raised here; the cap bounds length, not redaction, and
                    // every redaction rule still runs.
                    summary += ";" + Logs.PushDiagnostics.SanitizeErrorMessage(session.GetAudioDeviceStatus(), 512);
                }
                catch (Exception error)
                {
                    summary += $";audio_device_error=0x{error.HResult:X8}";
                }
            }

            WriteModernMediaDiagnostic(summary);
        }

        /// <summary>
        /// Media diagnostics are sampled for the whole duration of a call, so they are given
        /// their own per-call allowance. Charging them to the shared call diagnostic budget
        /// would let one long call silence the lifecycle diagnostics of every later call.
        /// </summary>
        private void WriteModernMediaDiagnostic(string details)
        {
            if (System.Threading.Interlocked.Decrement(ref _modernMediaDiagnosticBudget) >= 0)
            {
                Logs.PushDiagnostics.Write("voip.media", details);
            }
        }

        private void ResetModernMediaDiagnostics()
        {
            lock (_modernAudioLevelLock)
            {
                _modernAudioLevelSamples = 0;
                _modernAudioLevelActive = 0;
                _modernAudioLevelPeak = 0f;
                _modernAudioLevelReported = DateTime.MinValue;
            }

            lock (_modernCallbackFaultLock)
            {
                _modernCallbackFaults.Clear();
            }

            _modernSignalBars = -1;
            System.Threading.Interlocked.Exchange(ref _modernSignalingSent, 0);
            System.Threading.Interlocked.Exchange(ref _modernSignalingReceived, 0);
            System.Threading.Interlocked.Exchange(ref _modernSignalingSentCallId, 0);
            System.Threading.Interlocked.Exchange(ref _modernSignalingReceivedCallId, 0);
            _modernRemoteAudioState = null;
            _modernTransportState = null;
            _modernMuted = false;
            _modernVideoOutputsEnabled = false;
            _callStarted = DateTime.MinValue;
            _modernMediaDiagnosticBudget = ModernMediaDiagnosticBudget;
        }

        private void OnModernCallStateChanged(int callId, ModernCalls.CallState state)
        {
            if (_modernController == null || _modernCallId != callId)
            {
                return;
            }

            WriteAudioCallDiagnostic("voip.transport", $"result=state;transport=modern_tgcalls;state={state}");
            _modernTransportState = state;

            var callPage = _callPage;
            if (callPage != null)
            {
                callPage.BeginOnUIThread(() => callPage.UpdateModernTransportState(state));
            }
            else
            {
                WriteAudioCallDiagnostic("voip.ui", $"result=transport_deferred;state={state}");
            }

            BeginOnUIThread(() =>
            {
                if (state == ModernCalls.CallState.WaitInit || state == ModernCalls.CallState.WaitInitAck)
                {
                    PlayTone("voip_connecting.mp3", false);
                }
                else if (state == ModernCalls.CallState.Established)
                {
                    _callStarted = DateTime.Now;
                    StopTone();
                    EnableModernVideoOutputs(callId);
                }
            });
        }

        private void EnableModernVideoOutputs(int callId)
        {
            if (_modernVideoOutputsEnabled || _modernCallId != callId)
            {
                return;
            }

            if (_call?.Id != callId || !_call.IsVideo)
            {
                WriteModernMediaDiagnostic("result=video_output;state=disabled;reason=audio_call");
                return;
            }

            if (!ModernVideoLocalPreviewEnabled && !ModernVideoRemotePreviewEnabled)
            {
                WriteModernMediaDiagnostic("result=video_output;state=disabled;reason=preview_isolation");
                return;
            }

            var page = _callPage;
            if (page == null)
            {
                WriteModernMediaDiagnostic("result=video_output;state=deferred;reason=page_unavailable");
                return;
            }

            page.BeginOnUIThread(() =>
            {
                if (_modernVideoOutputsEnabled || _modernCallId != callId)
                {
                    return;
                }

                var session = _modernController;
                if (session == null)
                {
                    WriteModernMediaDiagnostic("result=video_output;state=deferred;reason=session_unavailable");
                    return;
                }

                try
                {
                    WriteModernMediaDiagnostic(
                        $"result=video_output;state=attaching;local={(ModernVideoLocalPreviewEnabled ? 1 : 0)};remote={(ModernVideoRemotePreviewEnabled ? 1 : 0)}");
                    if (ModernVideoLocalPreviewEnabled)
                    {
                        var visual = page.CreateModernVideoOutputVisual(true);
                        if (visual == null)
                        {
                            throw new InvalidOperationException("The local video composition host is unavailable.");
                        }
                        session.SetVideoOutput(true, visual, true);
                    }
                    if (ModernVideoRemotePreviewEnabled)
                    {
                        var visual = page.CreateModernVideoOutputVisual(false);
                        if (visual == null)
                        {
                            throw new InvalidOperationException("The remote video composition host is unavailable.");
                        }
                        session.SetVideoOutput(false, visual, false);
                    }
                    _modernVideoOutputsEnabled = true;
                    WriteModernMediaDiagnostic(
                        $"result=video_output;state=attached;local={(ModernVideoLocalPreviewEnabled ? 1 : 0)};remote={(ModernVideoRemotePreviewEnabled ? 1 : 0)}");
                }
                catch (Exception error)
                {
                    WriteModernMediaDiagnostic($"result=video_output;state=failed;hresult=0x{error.HResult:X8}");
                }
            });
        }

        private void SendModernSignalingData(int callId, ModernCalls.AudioCallSession session, IList<byte> data)
        {
            if (data == null || _modernController != session || _modernCallId != callId)
            {
                return;
            }

            ProtoService.Send(new SendCallSignalingData
            {
                CallId = callId,
                Data = data.ToList()
            });
            System.Threading.Interlocked.Increment(ref _modernSignalingSent);
            if (System.Threading.Interlocked.Exchange(ref _modernSignalingSentCallId, callId) != callId)
            {
                WriteAudioCallDiagnostic("voip.signaling", "result=sent;transport=modern_tgcalls");
            }
        }

        private void OnModernCallStopped(int callId, ModernCalls.AudioCallSession session, bool completed)
        {
            WriteAudioCallDiagnostic(
                "voip.transport",
                $"result=stopped;transport=modern_tgcalls;completed={completed.ToString().ToLowerInvariant()}");

            // Deliberately no disposal here. This runs on the tgcalls completion thread,
            // inside the native event raise, so disposing would destroy the object whose
            // event is still being raised. The audio device no longer depends on it
            // either: the native session releases its reference before this callback, and
            // tgcalls destroys the device on its own threads. Disposal happens on the
            // TDLib path through CallStateDiscarded, or through DisposeStaleModernCall
            // when the next call arrives.
        }

        /// <summary>
        /// Reached from both the tgcalls worker thread, through the session's stopped event, and
        /// the TDLib update thread when the call is discarded, so the session has to be claimed
        /// atomically: two unsynchronised callers would otherwise each pass a null check and
        /// dispose the same native session twice. Clearing the fields before disposing also keeps
        /// a throwing <see cref="IDisposable.Dispose"/> from leaving a dead call installed, which
        /// would make the service reject every later call for the lifetime of the process.
        /// </summary>
        private void DisposeModernCall()
        {
            var callId = _modernCallId;
            var controller = System.Threading.Interlocked.Exchange(ref _modernController, null);

            _modernCallId = 0;
            _modernVideoCaptureDeviceId = null;
            _modernPendingVideoCaptureDeviceId = null;
            _modernPendingVideoCaptureIsFront = 0;
            System.Threading.Interlocked.Exchange(ref _modernRequestedSpeakerphone, -1);
            _modernCallStarting = false;

            try
            {
                if (controller == null)
                {
                    return;
                }

                var callPage = _callPage;
                var elapsed = System.Diagnostics.Stopwatch.StartNew();
                string teardown;
                try
                {
                    // CompositionVideoOutput owns WinRT composition objects. Detach it
                    // from the XAML apartment before native teardown joins its render
                    // thread, rather than releasing a SpriteVisual from the TDLib worker.
                    WriteModernMediaDiagnostic(
                        "result=video_output;state=teardown_begin;local=1;remote=1");
                    DetachModernVideoOutputs(controller, callPage);
                    teardown = controller.Teardown();
                    WriteModernMediaDiagnostic(
                        "result=video_output;state=teardown_end;local=1;remote=1");
                }
                catch (Exception error)
                {
                    teardown = $"unreadable_0x{error.HResult:X8}";
                }

                controller.Dispose();
                elapsed.Stop();

                if (callPage != null)
                {
                    callPage.BeginOnUIThread(() =>
                    {
                        callPage.ClearModernVideoOutputVisual(true);
                        callPage.ClearModernVideoOutputVisual(false);
                    });
                }

                // Teardown blocks until tgcalls has destroyed the audio device, so a
                // "timeout" here means the next call would start against a microphone
                // this session still owns.
                WriteAudioCallDiagnostic(
                    "voip.teardown",
                    $"result={teardown};transport=modern_tgcalls;elapsed_ms={QuantizeTeardownElapsed(elapsed.ElapsedMilliseconds)}");
            }
            finally
            {
                ResetModernMediaDiagnostics();
                ClearPendingModernSignalingData(callId);
            }
        }

        private static void DetachModernVideoOutputs(ModernCalls.AudioCallSession controller, VoIPPage callPage)
        {
            if (controller == null || callPage?.Dispatcher == null)
            {
                return;
            }

            Action detach = () =>
            {
                controller.ClearVideoOutput(true);
                controller.ClearVideoOutput(false);
            };

            if (callPage.Dispatcher.HasThreadAccess)
            {
                detach();
                return;
            }

            callPage.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => detach())
                .AsTask()
                .GetAwaiter()
                .GetResult();
        }

        /// <summary>
        /// A previous call can still own the capture endpoint when the next one arrives, because
        /// TDLib does not always deliver <c>CallStateDiscarded</c> for the old call before the new
        /// call's first update. Releasing the stale session here keeps the microphone
        /// single-owner: Windows 10 Mobile faults inside the audio stack when a second engine
        /// activates capture while the first still holds it.
        /// </summary>
        private void DisposeStaleModernCall(int incomingCallId)
        {
            if (_modernController == null || _modernCallId == incomingCallId)
            {
                return;
            }

            WriteAudioCallDiagnostic("voip.teardown", "result=stale;transport=modern_tgcalls");
            DisposeModernCall();
        }

        /// <summary>
        /// Rounds the teardown duration into coarse buckets so the diagnostic shows whether the
        /// release was prompt without turning the log into a timing fingerprint.
        /// </summary>
        private static long QuantizeTeardownElapsed(long milliseconds)
        {
            if (milliseconds < 100)
            {
                return 0;
            }

            return milliseconds < 1000
                ? milliseconds / 100 * 100
                : milliseconds / 500 * 500;
        }

        private bool TryQueueModernSignalingData(int callId, IList<byte> data)
        {
            lock (_modernSignalingLock)
            {
                return TryQueueModernSignalingDataLocked(callId, data);
            }
        }

        private ModernSignalingQueueResult QueueModernSignalingDataIfSessionStarting(int callId, IList<byte> data)
        {
            lock (_modernSignalingLock)
            {
                if (!_modernCallStarting)
                {
                    return ModernSignalingQueueResult.SessionReady;
                }

                return TryQueueModernSignalingDataLocked(callId, data)
                    ? ModernSignalingQueueResult.Queued
                    : ModernSignalingQueueResult.LimitExceeded;
            }
        }

        private bool TryQueueModernSignalingDataLocked(int callId, IList<byte> data)
        {
            if (data.Count > MaxPendingModernSignalingBytes)
            {
                return false;
            }

            if (!_pendingModernSignalingData.TryGetValue(callId, out var pending))
            {
                pending = new List<List<byte>>();
                _pendingModernSignalingData[callId] = pending;
            }

            var pendingBytes = pending.Sum(item => item.Count);
            if (pending.Count >= MaxPendingModernSignalingMessages ||
                pendingBytes > MaxPendingModernSignalingBytes - data.Count)
            {
                return false;
            }

            pending.Add(data.ToList());
            return true;
        }

        private bool FlushPendingModernSignalingData(int callId, ModernCalls.AudioCallSession session)
        {
            try
            {
                var flushed = false;
                var flushedCount = 0;
                while (true)
                {
                    List<List<byte>> pending;
                    lock (_modernSignalingLock)
                    {
                        if (!_pendingModernSignalingData.TryGetValue(callId, out pending))
                        {
                            _modernCallStarting = false;
                            if (flushed)
                            {
                                WriteAudioCallDiagnostic("voip.signaling", $"result=flushed;transport=modern_tgcalls;count={flushedCount}");
                            }
                            return true;
                        }

                        _pendingModernSignalingData.Remove(callId);
                    }

                    flushed = true;
                    foreach (var data in pending)
                    {
                        session.ReceiveSignalingData(data);
                        flushedCount++;
                        System.Threading.Interlocked.Increment(ref _modernSignalingReceived);
                    }
                }
            }
            catch (ArgumentException)
            {
                WriteAudioCallDiagnostic("voip.signaling", "result=rejected;reason=buffered_data");
                return false;
            }
        }

        private void ClearPendingModernSignalingData(int callId)
        {
            if (callId == 0)
            {
                return;
            }

            lock (_modernSignalingLock)
            {
                _pendingModernSignalingData.Remove(callId);
            }
        }
#endif

        /// <summary>
        /// Lifecycle diagnostics are budgeted per call rather than per process. The budget was
        /// previously a single static countdown that nothing ever replenished, so the first call
        /// of a session spent it and every later call recorded no setup, transport or signalling
        /// evidence at all. That silently hid the second half of every back-to-back comparison,
        /// which is normally the call actually under investigation.
        /// </summary>
        private static void ResetAudioCallDiagnosticBudget()
        {
            System.Threading.Interlocked.Exchange(ref _audioCallDiagnosticBudget, AudioCallDiagnosticBudget);
        }

        private static bool CanWriteAudioCallDiagnostic()
        {
            return System.Threading.Interlocked.Decrement(ref _audioCallDiagnosticBudget) >= 0;
        }

        private static void WriteAudioCallDiagnostic(string eventName, string details)
        {
            if (CanWriteAudioCallDiagnostic())
            {
                Logs.PushDiagnostics.Write(eventName, details);
            }
        }

        private void PlayTone(string fileName, bool isLooping)
        {
            if (_mediaPlayer == null)
            {
                WriteAudioCallDiagnostic("voip.tone", "result=skipped;reason=media_unavailable");
                return;
            }

            _mediaPlayer.Source = MediaSource.CreateFromUri(new Uri("ms-appx:///Assets/Audio/" + fileName));
            _mediaPlayer.IsLoopingEnabled = isLooping;
            _mediaPlayer.Play();
        }

        private void StopTone()
        {
            if (_mediaPlayer != null)
            {
                _mediaPlayer.Source = null;
            }
        }

        private async Task SendRatingAsync(int callId)
        {
            var dialog = new CallRatingPopup();

            var confirm = await dialog.ShowQueuedAsync();
            if (confirm == ContentDialogResult.Primary)
            {
                // We need updates here
                //await ProtoService.SendAsync(new SendCallRating(callId, dialog.Rating, dialog.Rating >= 1 && dialog.Rating <= 4 ? dialog.Comment : string.Empty));

                if (dialog.IncludeDebugLogs && dialog.Rating <= 3)
                {
                    var logPath = Path.Combine(Views.SessionStorage.GetSessionDirectory(SessionId), $"voip{callId}.txt");

                    StorageFile file = null;
                    try
                    {
                        file = await StorageFile.GetFileFromPathAsync(logPath);
                    }
                    catch { }

                    if (file == null)
                    {
                        return;
                    }

                    var chat = await ProtoService.SendAsync(new CreatePrivateChat(4244000, false)) as Chat;
                    if (chat == null)
                    {
                        return;
                    }

                    ProtoService.Send(ModernTdlibCompatibility.CreateSendMessage(
                        chat.Id,
                        0,
                        ModernTdlibCompatibility.CreateMessageSendOptions(false, false, null),
                        ModernTdlibCompatibility.CreateInputMessageDocument(new InputFileLocal(file.Path), null, false, null)));
                }
            }
        }

        private static libtgvoip.DataSavingMode GetVoipDataSavingMode(DataSavingMode value)
        {
            switch (value)
            {
                case DataSavingMode.MobileOnly:
                    return libtgvoip.DataSavingMode.MobileOnly;
                case DataSavingMode.Always:
                    return libtgvoip.DataSavingMode.Always;
                default:
                    return libtgvoip.DataSavingMode.Never;
            }
        }

        public Call ActiveCall
        {
            get
            {
                return _call;
            }
        }

        public void Show()
        {
            if (_call == null)
            {
                return;
            }

            Show(_call, _controller, _callStarted, true);
        }

        private void Show(Call call, VoIPControllerWrapper controller, DateTime started, bool activate = false)
        {
            _ = ShowAsync(call, controller, started, activate);
        }

        private async Task ShowAsync(Call call, VoIPControllerWrapper controller, DateTime started, bool activate)
        {
            using (await _callPageMutex.WaitAsync())
            {
                try
                {
                    var createdCallPage = false;
                    if (_callPage == null)
                    {
                        if (ApplicationView.GetForCurrentView().IsViewModeSupported(ApplicationViewMode.CompactOverlay))
                        {
                            _callLifetime = await _viewService.OpenAsync(
                                () => _callPage = _callPage ?? new VoIPPage(ProtoService, CacheService, Aggregator, _call, _controller, _callStarted),
                                call.Id);
                            _callLifetime.WindowWrapper.ApplicationView().Consolidated -= ApplicationView_Consolidated;
                            _callLifetime.WindowWrapper.ApplicationView().Consolidated += ApplicationView_Consolidated;
                        }
                        else
                        {
                            _callPage = new VoIPPage(ProtoService, CacheService, Aggregator, _call, _controller, _callStarted);

                            _callDialog = new OverlayPage();
                            _callDialog.HorizontalAlignment = HorizontalAlignment.Stretch;
                            _callDialog.VerticalAlignment = VerticalAlignment.Stretch;
                            _callDialog.Content = _callPage;
                            _callDialog.IsOpen = true;
                        }

                        Aggregator.Publish(new UpdateCallDialog(call, true));
                        createdCallPage = true;
                    }

                    var callPage = _callPage;
                    if (callPage == null)
                    {
                        WriteAudioCallDiagnostic("voip.ui", "result=skipped;reason=page_unavailable");
                        return;
                    }

                    if (activate && !createdCallPage)
                    {
                        if (_callDialog != null)
                        {
                            _callDialog.IsOpen = true;
                        }
                        else if (_callLifetime != null)
                        {
                            _callLifetime = await _viewService.OpenAsync(
                                () => _callPage = _callPage ?? new VoIPPage(ProtoService, CacheService, Aggregator, _call, _controller, _callStarted),
                                call.Id);
                            _callLifetime.WindowWrapper.ApplicationView().Consolidated -= ApplicationView_Consolidated;
                            _callLifetime.WindowWrapper.ApplicationView().Consolidated += ApplicationView_Consolidated;
                        }

                        Aggregator.Publish(new UpdateCallDialog(_call, true));
                    }

                    await callPage.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                    {
                        if (controller != null)
                        {
                            callPage.Connect(controller);
                        }

                        callPage.Update(call, started);
#if MODERN_TGCALLS
                        callPage.ModernMuteRequested = SetModernMuted;
                        callPage.ModernAudioOutputEndpointRequested = SetModernAudioOutputEndpoint;
                        callPage.ModernVideoDeviceRequested = SwitchModernVideoCaptureDevice;
                        if (call.IsVideo)
                        {
                            ConfigureModernVideoControls(callPage, call.Id);
                        }

                        // The page can be created after the bridge already reported its
                        // transport state, so replay the latest values instead of waiting
                        // for an event that has already been raised. Terminal states are
                        // excluded: replaying a stale Established would undo the label and
                        // restart the duration timer the teardown just stopped.
                        var terminal = call.State is CallStateHangingUp
                            || call.State is CallStateDiscarded
                            || call.State is CallStateError;

                        var transportState = _modernTransportState;
                        if (transportState.HasValue && !terminal)
                        {
                            callPage.UpdateModernTransportState(transportState.Value);
                        }

                        if (_modernSignalBars >= 0 && !terminal)
                        {
                            callPage.SetSignalBars(_modernSignalBars);
                        }
#endif
                    });
                    EnableDisplayOnOffController();
                }
                catch (Exception error)
                {
                    WriteAudioCallDiagnostic(
                        "voip.ui",
                        $"result=error;operation=show;hresult=0x{error.HResult:X8};message={Logs.PushDiagnostics.SanitizeErrorMessage(error.Message)}");
                }
            }
        }

        private void Hide()
        {
            _ = HideAsync();
        }

        private async Task HideAsync()
        {
            using (await _callPageMutex.WaitAsync())
            {
                var callPage = _callPage;
                if (callPage == null)
                {
                    DisableDisplayOnOffController();
                    return;
                }

                try
                {
                    await callPage.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                    {
                        if (_callDialog != null)
                        {
                            _callDialog.IsOpen = false;
                            _callDialog = null;
                        }
                        else if (_callLifetime != null)
                        {
                            _callLifetime.StopViewInUse();
                            _callLifetime.WindowWrapper.Window.Close();
                            _callLifetime = null;
                        }

                        callPage.Dispose();
                        if (_callPage == callPage)
                        {
                            _callPage = null;
                        }
                    });

                    Aggregator.Publish(new UpdateCallDialog(_call, true));
                }
                catch (Exception error)
                {
                    WriteAudioCallDiagnostic(
                        "voip.ui",
                        $"result=error;operation=hide;hresult=0x{error.HResult:X8};message={Logs.PushDiagnostics.SanitizeErrorMessage(error.Message)}");
                }
            }
            DisableDisplayOnOffController();
        }

        private void ApplicationView_Consolidated(ApplicationView sender, ApplicationViewConsolidatedEventArgs args)
        {
            if (_callLifetime != null)
            {
                _callLifetime.StopViewInUse();
                _callLifetime.WindowWrapper.Window.Close();
                _callLifetime = null;
            }

            if (_callPage != null)
            {
                _callPage.Dispose();
                _callPage = null;
            }

            Aggregator.Publish(new UpdateCallDialog(_call, false));
        }
    }
}
