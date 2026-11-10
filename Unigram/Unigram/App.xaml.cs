using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Telegram.Td;
using Telegram.Td.Api;
using Unigram.Common;
using Unigram.Navigation;
using Unigram.Navigation.Services;
using Unigram.Services;
using Unigram.Services.Updates;
using Unigram.Views;
using Unigram.Views.Host;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.AppService;
using Windows.ApplicationModel.Background;
using Windows.ApplicationModel.DataTransfer;
using Windows.ApplicationModel.DataTransfer.ShareTarget;
using Windows.ApplicationModel.ExtendedExecution;
using Windows.Foundation;
using Windows.Media;
using Windows.Networking.PushNotifications;
using Windows.Storage;
using Windows.System.Profile;
using Windows.UI.Core;
using Windows.UI.Notifications;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Resources;

namespace Unigram
{
    sealed partial class App : BootStrapper
    {
        public static ShareOperation ShareOperation { get; set; }
        public static Window ShareWindow { get; set; }

        public static ConcurrentDictionary<long, DataPackageView> DataPackages { get; } = new ConcurrentDictionary<long, DataPackageView>();

        public static AppServiceConnection Connection { get; private set; }
        public static BackgroundTaskDeferral Deferral { get; private set; }

        private static volatile bool _isInForeground;
        public static bool IsInForeground
        {
            get => _isInForeground;
            private set => _isInForeground = value;
        }

        private ExtendedExecutionSession _extendedSession;
        private MediaExtensionManager _mediaExtensionManager;

        public ViewModelLocator Locator { get; } = new ViewModelLocator();

        /// <summary>
        /// Initializes a new instance of the <see cref="App"/> class.
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            var packageVersion = Package.Current.Id.Version;
            Logs.PushDiagnostics.Write("startup.app", $"stage=configure;package_version={packageVersion.Major}.{packageVersion.Minor}.{packageVersion.Build}.{packageVersion.Revision}");
#if MODERN_TDLIB
            Common.ApiInfo.WriteCapabilities();
#endif
            try
            {
                Locator.Configure(/*session*/);
            }
            catch (Exception ex)
            {
                Logs.PushDiagnostics.WriteException("startup.configure", ex);
                throw;
            }

            if (SettingsService.Current.Appearance.RequestedTheme != ElementTheme.Default)
            {
                RequestedTheme = SettingsService.Current.Appearance.GetCalculatedApplicationTheme();
            }

            InitializeComponent();

            try
            {
                if (!ApiInfo.IsFullExperience)
                {
                    _mediaExtensionManager = new MediaExtensionManager();
                    _mediaExtensionManager.RegisterByteStreamHandler("Unigram.Native.Media.OpusByteStreamHandler", ".ogg", "audio/ogg");
                    _mediaExtensionManager.RegisterByteStreamHandler("Unigram.Native.Media.OpusByteStreamHandler", ".oga", "audio/ogg");
                }
            }
            catch
            {
                // User won't be able to play and record voice messages, but it still better than not being able to use the app at all.
            }

            InactivityHelper.Detected += Inactivity_Detected;

            // Background activations (e.g. the periodic sync) start without UI and never
            // raise LeavingBackground, so this stays false until the app is actually shown.
            LeavingBackground += (s, e) => IsInForeground = true;
            EnteredBackground += (s, e) => IsInForeground = false;

            UnhandledException += (s, args) =>
            {
                if (args.Exception is NotSupportedException)
                {
                    args.Handled = true;
                }
                else
                {
                    Client.Execute(new AddLogMessage(1, "Unhandled exception:\n" + args.Exception.ToString()));
                }

#if MODERN_TDLIB
                Logs.PushDiagnostics.WriteException("app.unhandled", args.Exception, null, args.Message);
#elif !DEBUG
                Microsoft.AppCenter.Crashes.Crashes.TrackError(args.Exception);
#endif

                //try
                //{
                //    await new MessagePopup(args.Exception?.ToString() ?? string.Empty, "Unhandled exception").ShowQueuedAsync();
                //}
                //catch { }
            };

#if MODERN_TDLIB
            // App Center is deliberately never started on the experimental TDLib branch.
            // Its UWP session tracker queries Windows.System.Diagnostics.ProcessDiagnosticInfo
            // on a background thread, and that WinRT server faults on Windows 10 Mobile 15254.
            // The native fault crosses a managed P/Invoke boundary, so .NET Native fail-fasts
            // the whole process (0x1007) a few seconds after launch. It would also publish this
            // experimental build's telemetry, including the signed-in user id, into the
            // production App Center app. Unhandled exceptions go to the local diagnostics log.
            TaskScheduler.UnobservedTaskException += OnUnobservedException;
            UnhandledException += OnUnhandledException;

            // Application.UnhandledException only ever observes the UI thread, which is why
            // every call crash so far has been invisible: the call engine raises its failures
            // on WebRTC's own threads. CoreApplication.UnhandledErrorDetected is the one
            // managed hook that sees those, and Propagate() rethrows the error here so it can
            // be recorded. Catching it also marks the error handled, so a managed failure on a
            // background thread no longer fail-fasts the process without leaving a trace.
            Windows.ApplicationModel.Core.CoreApplication.UnhandledErrorDetected += OnUnhandledErrorDetected;

            // Windows 10 Mobile terminates an app that holds audio while it is being suspended,
            // and that death is indistinguishable from a crash in the log. Recording the
            // lifecycle transitions is what separates "we faulted" from "the OS took us out".
            Windows.ApplicationModel.Core.CoreApplication.Suspending += (s, e) =>
                Logs.PushDiagnostics.Write("app.lifecycle", "event=suspending");
            Windows.ApplicationModel.Core.CoreApplication.Resuming += (s, e) =>
                Logs.PushDiagnostics.Write("app.lifecycle", "event=resuming");
            EnteredBackground += (s, e) =>
                Logs.PushDiagnostics.Write("app.lifecycle", "event=entered_background");
            LeavingBackground += (s, e) =>
                Logs.PushDiagnostics.Write("app.lifecycle", "event=leaving_background");
#elif !DEBUG
            Microsoft.AppCenter.AppCenter.Start(Constants.AppCenterId,
                typeof(Microsoft.AppCenter.Analytics.Analytics),
                typeof(Microsoft.AppCenter.Crashes.Crashes));

            string deviceFamilyVersion = AnalyticsInfo.VersionInfo.DeviceFamilyVersion;
            ulong version = ulong.Parse(deviceFamilyVersion);
            ulong major = (version & 0xFFFF000000000000L) >> 48;
            ulong minor = (version & 0x0000FFFF00000000L) >> 32;
            ulong build = (version & 0x00000000FFFF0000L) >> 16;

            Microsoft.AppCenter.Analytics.Analytics.TrackEvent($"{major}.{minor}.{build}");
            Microsoft.AppCenter.Analytics.Analytics.TrackEvent(AnalyticsInfo.VersionInfo.DeviceFamily);

            TaskScheduler.UnobservedTaskException += OnUnobservedException;
            UnhandledException += OnUnhandledException;
#endif
        }

        protected override void OnWindowCreated(WindowCreatedEventArgs args)
        {
            //var flowDirectionSetting = Windows.ApplicationModel.Resources.Core.ResourceContext.GetForCurrentView().QualifierValues["LayoutDirection"];

            //args.Window.CoreWindow.FlowDirection = flowDirectionSetting == "RTL" ? CoreWindowFlowDirection.RightToLeft : CoreWindowFlowDirection.LeftToRight;

            Theme.Current.Initialize();
            CustomXamlResourceLoader.Current = new XamlResourceLoader();
            base.OnWindowCreated(args);
        }

        protected override WindowContext CreateWindowWrapper(Window window)
        {
            return new TLWindowContext(window, ApplicationView.GetApplicationViewIdForWindow(window.CoreWindow));
        }

        private void Inactivity_Detected(object sender, EventArgs e)
        {
            WindowContext.Default().Dispatcher.Dispatch(() =>
            {
                var passcode = TLContainer.Current.Resolve<IPasscodeService>();
                if (passcode != null && UIViewSettings.GetForCurrentView().UserInteractionMode == UserInteractionMode.Mouse)
                {
                    passcode.Lock();
                    ShowPasscode();
                }
            });
        }

        [ThreadStatic]
        private static bool _passcodeShown;
        public static async void ShowPasscode()
        {
            if (_passcodeShown)
            {
                return;
            }

            _passcodeShown = true;

            // This is a rare case, but it can happen.
            var content = Window.Current.Content;
            if (content != null)
            {
                content.Visibility = Visibility.Collapsed;
            }

            var dialog = new PasscodePage();
            TypedEventHandler<ContentDialog, ContentDialogClosingEventArgs> handler = null;
            handler = (s, args) =>
            {
                dialog.Closing -= handler;

                // This is a rare case, but it can happen.
                var contentInner = Window.Current.Content;
                if (contentInner != null)
                {
                    contentInner.Visibility = Visibility.Visible;
                }
            };

            dialog.Closing += handler;
            var result = await dialog.ShowQueuedAsync();

            _passcodeShown = false;
        }

        private void Window_Activated(object sender, WindowActivatedEventArgs e)
        {
            if (ApiInfo.IsUniversalApiContract5Present)
                HandleActivated(Window.Current.CoreWindow.ActivationMode == CoreWindowActivationMode.ActivatedInForeground);
            else
                HandleActivated(e.WindowActivationState != CoreWindowActivationState.Deactivated);
            SettingsService.Current.Appearance.UpdateTimer();
        }

        private void Window_VisibilityChanged(object sender, VisibilityChangedEventArgs e)
        {
            //HandleActivated(e.Visible);

            if (e.Visible && TLContainer.Current.Passcode.IsLockscreenRequired)
            {
                ShowPasscode();
            }
            else
            {
                if (UIViewSettings.GetForCurrentView().UserInteractionMode == UserInteractionMode.Touch)
                {
                    TLContainer.Current.Passcode.CloseTime = DateTime.Now;
                }
                else
                {
                    TLContainer.Current.Passcode.CloseTime = DateTime.MaxValue;
                }
            }
        }

        private void HandleActivated(bool active)
        {
            var aggregator = TLContainer.Current.Resolve<IEventAggregator>();
            if (aggregator != null)
            {
                aggregator.Publish(new UpdateWindowActivated(active));
            }

            var cacheService = TLContainer.Current.Resolve<ICacheService>();
            if (cacheService != null)
            {
                cacheService.Options.Online = active;
            }
        }

        protected override async void OnBackgroundActivated(BackgroundActivatedEventArgs args)
        {
            base.OnBackgroundActivated(args);
            Logs.PushDiagnostics.Write("managed.background.run", $"task_name={args.TaskInstance.Task.Name};trigger={args.TaskInstance.TriggerDetails?.GetType().Name ?? "null"}");

            //if (args.TaskInstance.TriggerDetails is AppServiceTriggerDetails appService && string.Equals(appService.CallerPackageFamilyName, Package.Current.Id.FamilyName))
            //{
            //    Connection = appService.AppServiceConnection;
            //    Deferral = args.TaskInstance.GetDeferral();

            //    appService.AppServiceConnection.RequestReceived += AppServiceConnection_RequestReceived;
            //    args.TaskInstance.Canceled += (s, e) =>
            //    {
            //        Deferral.Complete();
            //    };
            //}
            //else
            {
                var deferral = args.TaskInstance.GetDeferral();
                try
                {
                    if (args.TaskInstance.Task.Name == BackgroundSync.TaskName)
                    {
                        await BackgroundSync.RunAsync(args.TaskInstance, IsInForeground);
                    }
                    else if (args.TaskInstance.TriggerDetails is ToastNotificationActionTriggerDetail triggerDetail)
                    {
                        var data = Toast.GetData(triggerDetail);
                        if (data == null)
                        {
                            Logs.PushDiagnostics.Write("managed.background.toast.invalid");
                            return;
                        }

                        var session = TLContainer.Current.Lifetime.ActiveItem.Id;
                        if (data.TryGetValue("session", out string value) && int.TryParse(value, out int result))
                        {
                            session = result;
                        }

                        if (TLContainer.Current.TryResolve(session, out INotificationsService service))
                        {
                            await service.ProcessAsync(data);
                        }
                    }
                    else if (args.TaskInstance.TriggerDetails is RawNotification notification)
                    {
                        Logs.PushDiagnostics.Write("managed.raw.received", $"length={notification.Content?.Length ?? 0}");

                        int? GetSession(long id)
                        {
                            if (ApplicationData.Current.LocalSettings.Values.TryGet($"PushReceiverId{id}", out int receiverSession))
                            {
                                return receiverSession;
                            }

                            if (ApplicationData.Current.LocalSettings.Values.TryGet($"User{id}", out int value))
                            {
                                return value;
                            }

                            return null;
                        }

                        var receiver = Client.Execute(new GetPushReceiverId(notification.Content)) as PushReceiverId;
                        if (receiver == null)
                        {
                            Logs.PushDiagnostics.Write("tdlib.get_push_receiver.result", "result=missing");
                            return;
                        }

                        var session = GetSession(receiver.Id);
                        Logs.PushDiagnostics.Write("tdlib.get_push_receiver.result", $"result=success;receiver_hash={Logs.PushDiagnostics.HashIdentifier(receiver.Id.ToString(System.Globalization.CultureInfo.InvariantCulture))};mapping_found={session != null}");
                        if (session == null)
                        {
                            return;
                        }

                        if (TLContainer.Current.TryResolve(session.Value, out IProtoService service))
                        {
                            for (var attempt = 1; attempt <= 3; attempt++)
                            {
                                Logs.PushDiagnostics.Write("tdlib.process_push.request", $"session={session.Value};attempt={attempt}");
                                var response = await service.SendAsync(new ProcessPushNotification(notification.Content));
                                if (!(response is Error error) || error.Code != 406)
                                {
                                    if (response is Error finalError)
                                    {
                                        Logs.Logger.Error(Logs.Target.Notifications, $"Unable to process push notification: {finalError}");
                                        Logs.PushDiagnostics.Write("tdlib.process_push.result", $"session={session.Value};result=error;code={finalError.Code};attempt={attempt}");
                                    }
                                    else
                                    {
                                        Logs.PushDiagnostics.Write("tdlib.process_push.result", $"session={session.Value};result=success;type={response?.GetType().Name ?? "null"};attempt={attempt}");
                                    }

                                    break;
                                }

                                if (attempt == 3)
                                {
                                    Logs.Logger.Error(Logs.Target.Notifications, $"Unable to process push notification after {attempt} attempts: {error}");
                                    Logs.PushDiagnostics.Write("tdlib.process_push.result", $"session={session.Value};result=error;code={error.Code};attempt={attempt}");
                                    break;
                                }

                                Logs.PushDiagnostics.Write("tdlib.process_push.retry", $"session={session.Value};code={error.Code};attempt={attempt}");
                                await Task.Delay(5000);
                            }
                        }
                        else
                        {
                            Logs.PushDiagnostics.Write("tdlib.process_push.unavailable", $"session={session.Value};reason=service_not_resolved");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logs.PushDiagnostics.WriteException("managed.background.failed", ex);
                }
                finally
                {
                    deferral.Complete();
                    Logs.PushDiagnostics.Write("managed.background.complete");
                }
            }
        }

        private void AppServiceConnection_RequestReceived(AppServiceConnection sender, AppServiceRequestReceivedEventArgs args)
        {
            if (args.Request.Message.TryGetValue("Exit", out object exit))
            {
                Application.Current.Exit();
            }
        }

        public override Task OnInitializeAsync(IActivatedEventArgs args)
        {
            //Locator.Configure();
            //UnigramContainer.Current.ResolveType<IGenerationService>();

            if (TLContainer.Current.Passcode.IsEnabled)
            {
                TLContainer.Current.Passcode.Lock();
                InactivityHelper.Initialize(TLContainer.Current.Passcode.AutolockTimeout);
            }

            if (Window.Current != null)
            {
                Window.Current.Activated -= Window_Activated;
                Window.Current.Activated += Window_Activated;
                Window.Current.VisibilityChanged -= Window_VisibilityChanged;
                Window.Current.VisibilityChanged += Window_VisibilityChanged;

                TLWindowContext.GetForCurrentView().UpdateTitleBar();
            }

            return base.OnInitializeAsync(args);
        }

        public override async Task OnStartAsync(StartKind startKind, IActivatedEventArgs args)
        {
            if (TLContainer.Current.Passcode.IsLockscreenRequired)
            {
                ShowPasscode();
            }

            if (startKind == StartKind.Activate)
            {
                var lifetime = TLContainer.Current.Lifetime;
                var sessionId = lifetime.ActiveItem.Id;

                var id = Toast.GetSession(args);
                if (id != null)
                {
                    lifetime.ActiveItem = lifetime.Items.FirstOrDefault(x => x.Id == id.Value) ?? lifetime.ActiveItem;
                }

                if (sessionId != TLContainer.Current.Lifetime.ActiveItem.Id)
                {
                    var root = Window.Current.Content as RootPage;
                    root.Switch(lifetime.ActiveItem);
                }
            }

            var navService = WindowContext.GetForCurrentView().NavigationServices.GetByFrameId($"{TLContainer.Current.Lifetime.ActiveItem.Id}");
            var service = TLContainer.Current.Resolve<IProtoService>();
            if (service == null)
            {
                return;
            }

            var state = service.GetAuthorizationState();
#if MODERN_TDLIB
            // Modern TDLib initializes on a background task, so a fresh start reaches
            // this point before the first UpdateAuthorizationState has been delivered
            // and the state is still null. Returning here would skip SetActivatedArgs
            // and leave the content frame empty, rendering only the shell, and would
            // also skip background task registration. Continue instead and let the
            // null state navigate to a placeholder until the real state arrives.
            Logs.PushDiagnostics.Write("startup.start", $"state={state?.GetType().Name ?? "null"}");
#else
            if (state == null)
            {
                return;
            }
#endif

            TLWindowContext.GetForCurrentView().SetActivatedArgs(args, navService);
            TLWindowContext.GetForCurrentView().UpdateTitleBar();

            Window.Current.Activated -= Window_Activated;
            Window.Current.Activated += Window_Activated;
            Window.Current.VisibilityChanged -= Window_VisibilityChanged;
            Window.Current.VisibilityChanged += Window_VisibilityChanged;

            ApplicationView.GetForCurrentView().SetPreferredMinSize(new Size(320, 500));
            //SystemNavigationManager.GetForCurrentView().AppViewBackButtonVisibility = AppViewBackButtonVisibility.Visible;

            var dispatcher = Window.Current.Dispatcher;
            await Task.Run(() => OnStartSync(dispatcher));
            //return Task.CompletedTask;
        }

        public override UIElement CreateRootElement(IActivatedEventArgs e)
        {
            var id = Toast.GetSession(e);
            if (id != null)
            {
                TLContainer.Current.Lifetime.ActiveItem = TLContainer.Current.Lifetime.Items.FirstOrDefault(x => x.Id == id.Value) ?? TLContainer.Current.Lifetime.ActiveItem;
            }

            var sessionId = TLContainer.Current.Lifetime.ActiveItem.Id;

            if (e is ContactPanelActivatedEventArgs /*|| (e is ProtocolActivatedEventArgs protocol && protocol.Uri.PathAndQuery.Contains("domain=telegrampassport", StringComparison.OrdinalIgnoreCase))*/)
            {
                var navigationFrame = new Frame { FlowDirection = ApiInfo.FlowDirection };
                var navigationService = NavigationServiceFactory(BackButton.Ignore, ExistingContent.Include, navigationFrame, sessionId, $"Main{sessionId}", false) as NavigationService;

                return navigationFrame;
            }
            else
            {
                var navigationFrame = new Frame();
                var navigationService = NavigationServiceFactory(BackButton.Ignore, ExistingContent.Include, navigationFrame, sessionId, $"{sessionId}", true) as NavigationService;

                return new RootPage(navigationService) { FlowDirection = ApiInfo.FlowDirection };
            }
        }

        public override UIElement CreateRootElement(INavigationService navigationService)
        {
            return new StandalonePage(navigationService) { FlowDirection = ApiInfo.FlowDirection };
        }

        protected override INavigationService CreateNavigationService(Frame frame, int session, string id, bool root)
        {
            if (root)
            {
                return new TLRootNavigationService(TLContainer.Current.Resolve<ISessionService>(session), frame, session, id);
            }

            return new TLNavigationService(TLContainer.Current.Resolve<IProtoService>(session), frame, session, id);
        }

        private async void OnStartSync(CoreDispatcher dispatcher)
        {
            //#if DEBUG
            //await VoIPConnection.Current.ConnectAsync();
            //#endif

            await Toast.RegisterBackgroundTasks();

            try
            {
                ToastNotificationManager.History.Clear("App");
            }
            catch { }

#if DESKTOP_BRIDGE
            if (ApiInformation.IsTypePresent("Windows.ApplicationModel.FullTrustProcessLauncher"))
            {
                try
                {
                    await FullTrustProcessLauncher.LaunchFullTrustProcessForCurrentAppAsync();
                }
                catch
                {
                    // The app has been compiled without desktop bridge
                }
            }
#endif

            if (_extendedSession == null && ApiInfo.IsFullExperience)
            {
                var session = new ExtendedExecutionSession { Reason = ExtendedExecutionReason.Unspecified };
                var result = await session.RequestExtensionAsync();

                if (result == ExtendedExecutionResult.Allowed)
                {
                    _extendedSession = session;

                    Logs.Logger.Info(Logs.Target.Lifecycle, "ExtendedExecutionResult.Allowed");
                }
                else
                {
                    session.Dispose();

                    Logs.Logger.Warning(Logs.Target.Lifecycle, "ExtendedExecutionResult.Denied");
                }
            }
        }

        public override void OnResuming(object s, object e, AppExecutionState previousExecutionState)
        {
            Logs.Logger.Info(Logs.Target.Lifecycle, "OnResuming");

            // #1225: Will this work? No one knows.
            foreach (var network in TLContainer.Current.ResolveAll<INetworkService>())
            {
                network.Reconnect();
            }

            base.OnResuming(s, e, previousExecutionState);
        }

        public override Task OnSuspendingAsync(object s, SuspendingEventArgs e, bool prelaunchActivated)
        {
            Logs.Logger.Info(Logs.Target.Lifecycle, "OnSuspendingAsync");

            return base.OnSuspendingAsync(s, e, prelaunchActivated);
        }

#if MODERN_TDLIB
        private void OnUnobservedException(object sender, UnobservedTaskExceptionEventArgs e)
        {
            Logs.PushDiagnostics.WriteException("app.unobserved", e.Exception);
            e.SetObserved();
        }

        private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Logs.PushDiagnostics.WriteException("app.unhandled.handler", e.Exception, null, e.Message);
            e.Handled = true;
        }

        private void OnUnhandledErrorDetected(object sender, Windows.ApplicationModel.Core.UnhandledErrorDetectedEventArgs e)
        {
            // Reached while the process is already ending because this handler rethrew.
            // Returning without calling Propagate leaves the error unhandled, which is
            // what keeps that termination on course instead of looping back in here.
            if (System.Threading.Volatile.Read(ref _unhandledErrorFatal) != 0)
            {
                return;
            }

            // Propagate() is the only way to obtain the underlying exception; it rethrows on
            // this thread, and catching it is what marks the error handled. The handler is
            // reached from arbitrary threads, so it must not touch UI state.
            Exception error = null;
            try
            {
                e.UnhandledError.Propagate();
            }
            catch (Exception ex)
            {
                error = ex;
            }

            if (error == null)
            {
                return;
            }

            // A null dereference on the call UI path happens on every call and has been
            // observed to be survivable, so that is the one shape this handler absorbs.
            // Anything else stays unhandled on purpose: a managed failure inside a native
            // callback leaves the caller's invariants half applied, and resuming it would
            // trade a crash that names its cause for a later one that does not.
            var survivable = error is NullReferenceException;

            try
            {
                Logs.PushDiagnostics.WriteException(
                    survivable ? "app.unhandled.core.suppressed" : "app.unhandled.core.fatal",
                    error,
                    null,
                    null);
            }
            catch
            {
                // Losing the record is survivable; failing to record is not a reason to
                // change what happens to the error itself.
            }

            if (!survivable)
            {
                // The record above is already on disk, so ending here costs no diagnostics.
                System.Threading.Volatile.Write(ref _unhandledErrorFatal, 1);
                throw error;
            }
        }

        private static int _unhandledErrorFatal;
#elif !DEBUG
        private void OnUnobservedException(object sender, UnobservedTaskExceptionEventArgs e)
        {
            Microsoft.AppCenter.Crashes.Crashes.TrackError(e.Exception);
            e.SetObserved();
        }

        private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Microsoft.AppCenter.Crashes.Crashes.TrackError(e.Exception);
            e.Handled = true;
        }
#endif
    }
}
