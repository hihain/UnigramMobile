using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.Background;
using Windows.UI.Notifications;

namespace Unigram.Common
{
    public class Toast
    {
        private const string NotificationTaskName = "NotificationTask";
        private const string NotificationTaskEntryPoint = "Unigram.Native.Tasks.NotificationTask";
        private const string InteractiveTaskName = "NewInteractiveTask";

        private static readonly HashSet<string> LegacyTaskNames = new HashSet<string>
        {
            NotificationTaskName,
            "NewNotificationTask",
            "NewNotificationTask2",
            "InProcessNotificationTask",
            InteractiveTaskName
        };

        public static async Task<bool> RegisterBackgroundTasks()
        {
            BackgroundAccessStatus access;
            Logs.PushDiagnostics.Write("background.access.request");
            try
            {
                access = await BackgroundExecutionManager.RequestAccessAsync();
            }
            catch (Exception ex)
            {
                Logs.Logger.Error(Logs.Target.Notifications, $"Unable to request background access: {ex.Message}");
                Logs.PushDiagnostics.WriteException("background.access.result", ex);
                return false;
            }

            Logs.PushDiagnostics.Write("background.access.result", $"status={access}");
            if (!IsBackgroundAccessAllowed(access))
            {
                Logs.Logger.Warning(Logs.Target.Notifications, $"Background notifications are disabled: {access}");
                return false;
            }

            foreach (var task in BackgroundTaskRegistration.AllTasks)
            {
                Logs.PushDiagnostics.Write("background.task.found", $"name={task.Value.Name};id={task.Key}");
                if (LegacyTaskNames.Contains(task.Value.Name))
                {
                    task.Value.Unregister(true);
                    Logs.PushDiagnostics.Write("background.task.removed", $"name={task.Value.Name};id={task.Key}");
                }
            }

            var registered = true;
            try
            {
                var registration = Register(NotificationTaskName, NotificationTaskEntryPoint, new PushNotificationTrigger());
                Logs.Logger.Info(Logs.Target.Notifications, "Registered out-of-process push notification task");
                Logs.PushDiagnostics.Write("background.task.registered", $"name={NotificationTaskName};entrypoint={NotificationTaskEntryPoint};id={registration.TaskId};trigger=PushNotificationTrigger");
            }
            catch (Exception ex)
            {
                registered = false;
                Logs.Logger.Error(Logs.Target.Notifications, $"Unable to register push notification task: {ex.Message}");
                Logs.PushDiagnostics.WriteException("background.task.push.failed", ex);
            }

            try
            {
                var registration = Register(InteractiveTaskName, null, new ToastNotificationActionTrigger());
                Logs.Logger.Info(Logs.Target.Notifications, "Registered in-process toast action task");
                Logs.PushDiagnostics.Write("background.task.registered", $"name={InteractiveTaskName};entrypoint=in-process;id={registration.TaskId};trigger=ToastNotificationActionTrigger");
            }
            catch (Exception ex)
            {
                registered = false;
                Logs.Logger.Error(Logs.Target.Notifications, $"Unable to register toast action task: {ex.Message}");
                Logs.PushDiagnostics.WriteException("background.task.toast.failed", ex);
            }

            return registered;
        }

        private static bool IsBackgroundAccessAllowed(BackgroundAccessStatus access)
        {
            return access == BackgroundAccessStatus.AllowedMayUseActiveRealTimeConnectivity
                || access == BackgroundAccessStatus.AllowedWithAlwaysOnRealTimeConnectivity
                || access == BackgroundAccessStatus.AllowedSubjectToSystemPolicy
                || access == BackgroundAccessStatus.AlwaysAllowed;
        }

        private static BackgroundTaskRegistration Register(string name, string entryPoint, IBackgroundTrigger trigger)
        {
            var builder = new BackgroundTaskBuilder
            {
                Name = name
            };

            if (entryPoint != null)
            {
                builder.TaskEntryPoint = entryPoint;
            }

            builder.SetTrigger(trigger);
            return builder.Register();
        }

        public static int? GetSession(IActivatedEventArgs args)
        {
            string arguments = null;

            switch (args)
            {
                case ToastNotificationActivatedEventArgs toastNotification:
                    arguments = toastNotification.Argument;
                    break;
                case LaunchActivatedEventArgs launch:
                    if (launch.TileActivatedInfo != null && launch.TileActivatedInfo.RecentlyShownNotifications.Count > 0)
                    {
                        arguments = launch.TileActivatedInfo.RecentlyShownNotifications[0].Arguments;
                    }
                    break;
                case ProtocolActivatedEventArgs protocol:
                    var uri = protocol.Uri.ToString();
                    break;
            }

            var data = SplitArguments(arguments);
            if (data.TryGetValue("session", out string value) && int.TryParse(value, out int result))
            {
                // TODO: move additional checks here
                return result;
            }

            return null;
        }

        public static Dictionary<string, string> GetData(IActivatedEventArgs args)
        {
            if (args.Kind == ActivationKind.ToastNotification)
            {
                ToastNotificationActivatedEventArgs toastActivationArgs = args as ToastNotificationActivatedEventArgs;

                var dictionary = SplitArguments(toastActivationArgs.Argument);
                if (toastActivationArgs.UserInput != null && toastActivationArgs.UserInput.Count > 0)
                {
                    for (int i = 0; i < toastActivationArgs.UserInput.Count; i++)
                    {
                        dictionary.Add(toastActivationArgs.UserInput.Keys.ElementAt(i), toastActivationArgs.UserInput.Values.ElementAt(i).ToString());
                    }
                }

                return dictionary;
            }

            return null;
        }

        public static Dictionary<string, string> GetData(IBackgroundTaskInstance args)
        {
            if (args.TriggerDetails is ToastNotificationActionTriggerDetail)
            {
                ToastNotificationActionTriggerDetail details = args.TriggerDetails as ToastNotificationActionTriggerDetail;
                if (details == null)
                {
                    return null;
                }

                var dictionary = SplitArguments(details.Argument);
                if (details.UserInput != null && details.UserInput.Count > 0)
                {
                    for (int i = 0; i < details.UserInput.Count; i++)
                    {
                        dictionary.Add(details.UserInput.Keys.ElementAt(i), details.UserInput.Values.ElementAt(i).ToString());
                    }
                }

                return dictionary;
            }

            return null;
        }

        public static Dictionary<string, string> GetData(ToastNotificationActionTriggerDetail triggerDetail)
        {
            var dictionary = SplitArguments(triggerDetail.Argument);
            if (triggerDetail.UserInput != null && triggerDetail.UserInput.Count > 0)
            {
                foreach (var input in triggerDetail.UserInput)
                {
                    dictionary[input.Key] = input.Value.ToString();
                }
            }

            return dictionary;
        }

        public static Dictionary<string, string> SplitArguments(string arguments)
        {
            var dictionary = new Dictionary<string, string>();
            if (arguments == null || arguments == string.Empty || !arguments.Contains("="))
            {
                return dictionary;
            }

            string[] items = arguments.Split('&');
            foreach (string item in items)
            {
                string[] pair = item.Split('=');
                dictionary.Add(pair[0], pair[1]);
            }

            return dictionary;
        }
    }
}
