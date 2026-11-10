using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Td.Api;
using Unigram.Services;
using Unigram.Views;
using Windows.ApplicationModel.Background;
using Windows.Storage;

namespace Unigram.Common
{
    /// <summary>
    /// Periodic in-process check for new messages, used while Telegram is not delivering
    /// WNS pushes. Every 15 minutes Windows wakes the app in the background; the already
    /// configured TDLib sessions reconnect and catch up, and TDLib's own notification
    /// updates raise toasts and the Live Tile through NotificationsService as usual.
    /// </summary>
    public static class BackgroundSync
    {
        public const string TaskName = "BackgroundSyncTask";

        // Windows does not run time triggers more often than every 15 minutes.
        private const uint IntervalMinutes = 15;

        private const string EnabledKey = "BackgroundSyncEnabled";

        // Written by the native push task whenever TDLib resolves a real Telegram push.
        private const string LastPushTimeKey = "LastTelegramPushTime";
        private static readonly TimeSpan PushActiveWindow = TimeSpan.FromHours(24);

        // Windows 10 Mobile gives a background activation about 30 seconds.
        private static readonly TimeSpan SyncDeadline = TimeSpan.FromSeconds(18);
        private static readonly TimeSpan ReconnectGrace = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan NotificationSettle = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

        private static int _running;
        private static int _notificationGroups;

        public static bool IsRunning => Volatile.Read(ref _running) == 1;

        /// <summary>
        /// True only while a sync run is active and the app has not been brought to the
        /// foreground, so normal foreground notification rules are never relaxed.
        /// </summary>
        public static bool IsActiveInBackground => IsRunning && !App.IsInForeground;

        public static bool IsEnabled
        {
            get
            {
                return !(ApplicationData.Current.LocalSettings.Values.TryGetValue(EnabledKey, out object value) && value is bool enabled && !enabled);
            }
            set
            {
                ApplicationData.Current.LocalSettings.Values[EnabledKey] = value;
            }
        }

        public static void OnNotificationGroup()
        {
            if (IsRunning)
            {
                Interlocked.Increment(ref _notificationGroups);
            }
        }

        public static void Unregister()
        {
            foreach (var task in BackgroundTaskRegistration.AllTasks.Values.Where(x => x.Name == TaskName).ToList())
            {
                task.Unregister(false);
                Logs.PushDiagnostics.Write("background.task.removed", $"name={TaskName};id={task.TaskId}");
            }
        }

        /// <summary>
        /// Must be called after background access was granted.
        /// </summary>
        public static void UpdateRegistration()
        {
            try
            {
                Unregister();

                if (!IsEnabled)
                {
                    Logs.PushDiagnostics.Write("background.sync.registration", "result=disabled");
                    return;
                }

                var builder = new BackgroundTaskBuilder
                {
                    Name = TaskName
                };

                builder.SetTrigger(new TimeTrigger(IntervalMinutes, false));
                builder.AddCondition(new SystemCondition(SystemConditionType.InternetAvailable));

                var registration = builder.Register();
                Logs.PushDiagnostics.Write("background.task.registered", $"name={TaskName};entrypoint=in-process;id={registration.TaskId};trigger=TimeTrigger;interval_minutes={IntervalMinutes}");
            }
            catch (Exception ex)
            {
                Logs.Logger.Error(Logs.Target.Notifications, $"Unable to register background sync task: {ex.Message}");
                Logs.PushDiagnostics.WriteException("background.sync.registration.failed", ex);
            }
        }

        public static async Task RunAsync(IBackgroundTaskInstance taskInstance, bool appInForeground)
        {
            if (!IsEnabled)
            {
                Logs.PushDiagnostics.Write("background.sync.skipped", "reason=disabled");
                return;
            }

            if (appInForeground)
            {
                Logs.PushDiagnostics.Write("background.sync.skipped", "reason=app_foreground");
                return;
            }

            if (IsPushActive())
            {
                Logs.PushDiagnostics.Write("background.sync.skipped", "reason=push_active");
                return;
            }

            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            {
                Logs.PushDiagnostics.Write("background.sync.skipped", "reason=already_running");
                return;
            }

            var cancellation = new CancellationTokenSource();
            BackgroundTaskCanceledEventHandler canceled = (s, reason) =>
            {
                Logs.PushDiagnostics.Write("background.sync.canceled", $"reason={reason}");
                cancellation.Cancel();
            };

            var watch = Stopwatch.StartNew();
            var sessions = new List<ISessionService>();
            var ready = 0;
            var signedOut = 0;
            var result = "timeout";

            try
            {
                taskInstance.Canceled += canceled;
                Interlocked.Exchange(ref _notificationGroups, 0);

                sessions = TLContainer.Current.GetSessions()
                    .Where(x => x.IsActive || SettingsService.Current.IsAllAccountsNotifications)
                    .ToList();

                Logs.PushDiagnostics.Write("background.sync.start", $"sessions={sessions.Count}");

                foreach (var session in sessions)
                {
                    if (TLContainer.Current.TryResolve(session.Id, out INetworkService network))
                    {
                        network.Reconnect();
                    }
                }

                // A suspended process still reports its last connection state, so give
                // TDLib a moment to notice the dropped sockets before trusting "Ready".
                await Task.Delay(ReconnectGrace, cancellation.Token);

                while (true)
                {
                    ready = 0;
                    signedOut = 0;
                    var pending = 0;

                    foreach (var session in sessions)
                    {
                        switch (GetStatus(session.ProtoService))
                        {
                            case SessionStatus.Ready:
                                ready++;
                                break;
                            case SessionStatus.SignedOut:
                                signedOut++;
                                break;
                            default:
                                pending++;
                                break;
                        }
                    }

                    if (pending == 0)
                    {
                        result = "synced";
                        break;
                    }

                    if (watch.Elapsed >= SyncDeadline)
                    {
                        break;
                    }

                    await Task.Delay(PollInterval, cancellation.Token);
                }

                if (ready > 0)
                {
                    // TDLib flushes notifications collected during catch-up shortly after
                    // the connection becomes ready; give them time to reach the toast APIs.
                    await Task.Delay(NotificationSettle, cancellation.Token);
                }
            }
            catch (OperationCanceledException)
            {
                result = "canceled";
            }
            catch (Exception ex)
            {
                result = "error";
                Logs.PushDiagnostics.WriteException("background.sync.failed", ex);
            }
            finally
            {
                taskInstance.Canceled -= canceled;
                Volatile.Write(ref _running, 0);

                // The CTS has no timer, so it is left to the GC rather than disposed; a late
                // Canceled callback racing with this block could otherwise hit a disposed source.
                Logs.PushDiagnostics.Write("background.sync.result", $"result={result};sessions={sessions.Count};ready={ready};signed_out={signedOut};notification_groups={Volatile.Read(ref _notificationGroups)};elapsed_ms={watch.ElapsedMilliseconds}");
            }
        }

        private static bool IsPushActive()
        {
            if (ApplicationData.Current.LocalSettings.Values.TryGetValue(LastPushTimeKey, out object value) && value is long seconds)
            {
                var received = DateTimeOffset.FromUnixTimeSeconds(seconds);
                return DateTimeOffset.UtcNow - received < PushActiveWindow;
            }

            return false;
        }

        private enum SessionStatus
        {
            Pending,
            Ready,
            SignedOut
        }

        private static SessionStatus GetStatus(IProtoService protoService)
        {
            switch (protoService.GetAuthorizationState())
            {
                case null:
                case AuthorizationStateWaitTdlibParameters _:
#if !MODERN_TDLIB
                case AuthorizationStateWaitEncryptionKey _:
#endif
                    return SessionStatus.Pending;
                case AuthorizationStateReady _:
                    return protoService.GetConnectionState() is ConnectionStateReady
                        ? SessionStatus.Ready
                        : SessionStatus.Pending;
                default:
                    return SessionStatus.SignedOut;
            }
        }
    }
}
