using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unigram.Common;
using Unigram.Navigation.Services;
using Unigram.Services;
using Unigram.Views.Settings;
using Unigram.Views.Settings.Password;
using Windows.UI.Xaml.Navigation;

namespace Unigram.ViewModels.Settings.Password
{
    public class SettingsPasswordDoneViewModel : TLViewModelBase
    {
        private static readonly Type[] _setupPages =
        {
            typeof(SettingsPasswordIntroPage),
            typeof(SettingsPasswordCreatePage),
            typeof(SettingsPasswordHintPage),
            typeof(SettingsPasswordEmailPage),
            typeof(SettingsPasswordConfirmPage),
            typeof(SettingsPasswordDonePage)
        };

        public SettingsPasswordDoneViewModel(IProtoService protoService, ICacheService cacheService, ISettingsService settingsService, IEventAggregator aggregator)
            : base(protoService, cacheService, settingsService, aggregator)
        {
            SendCommand = new RelayCommand(SendExecute);
        }

        public override Task OnNavigatedToAsync(object parameter, NavigationMode mode, IDictionary<string, object> state)
        {
            // The password is now set, so the setup pages must not be reachable with Back.
            RemoveSetupPages(NavigationService);
            return base.OnNavigatedToAsync(parameter, mode, state);
        }

        public RelayCommand SendCommand { get; }
        private void SendExecute()
        {
            NavigationService.Navigate(typeof(SettingsPasswordPage));
            RemoveSetupPages(NavigationService);
        }

        public static void RemoveSetupPages(INavigationService service)
        {
            var backStack = service?.Frame?.BackStack;
            if (backStack == null)
            {
                return;
            }

            for (int i = backStack.Count - 1; i >= 0; i--)
            {
                if (Array.IndexOf(_setupPages, backStack[i].SourcePageType) >= 0)
                {
                    backStack.RemoveAt(i);
                }
            }
        }
    }
}