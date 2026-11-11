using Unigram.Common;
using Unigram.ViewModels.Settings.Password;

namespace Unigram.Views.Settings.Password
{
    public sealed partial class SettingsPasswordConfirmPage : HostedPage
    {
        public SettingsPasswordConfirmViewModel ViewModel => DataContext as SettingsPasswordConfirmViewModel;

        public SettingsPasswordConfirmPage()
        {
            InitializeComponent();
            DataContext = TLContainer.Current.Resolve<SettingsPasswordConfirmViewModel>();

            Transitions = ApiInfo.CreateSlideTransition();
        }
    }
}