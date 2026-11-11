using System.Collections.Generic;
using System.Threading.Tasks;
using Telegram.Td.Api;
using Unigram.Common;
using Unigram.Controls;
using Unigram.Services;
using Unigram.Views.Settings.Password;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace Unigram.ViewModels.Settings.Password
{
    public class SettingsPasswordConfirmViewModel : TLViewModelBase
    {
        private bool _isSending;

        public SettingsPasswordConfirmViewModel(IProtoService protoService, ICacheService cacheService, ISettingsService settingsService, IEventAggregator aggregator)
            : base(protoService, cacheService, settingsService, aggregator)
        {
            ResendCommand = new RelayCommand(ResendExecute);
            SendCommand = new RelayCommand(SendExecute);
            AbortCommand = new RelayCommand(AbortExecute);
        }

        private int _length;

        private string _pattern;
        public string Pattern
        {
            get => _pattern;
            set => Set(ref _pattern, value);
        }

        private string _code;
        public string Code
        {
            get => _code;
            set
            {
                Set(ref _code, value);

                if (_length > 0 && value?.Length == _length)
                {
                    SendExecute();
                }
            }
        }

        public override Task OnNavigatedToAsync(object parameter, NavigationMode mode, IDictionary<string, object> state)
        {
            if (state.TryGet("pattern", out string pattern) && !string.IsNullOrEmpty(pattern))
            {
                Pattern = pattern;
            }
            else if (state.TryGet("email", out string email))
            {
                Pattern = email;
            }

            if (state.TryGet("length", out int length))
            {
                _length = length;
            }

            return base.OnNavigatedToAsync(parameter, mode, state);
        }

        public RelayCommand ResendCommand { get; }
        private async void ResendExecute()
        {
            if (_isSending)
            {
                return;
            }

            _isSending = true;
            var response = await ProtoService.SendAsync(new ResendRecoveryEmailAddressCode());
            _isSending = false;

            if (response is PasswordState passwordState && passwordState.RecoveryEmailAddressCodeInfo != null)
            {
                _length = passwordState.RecoveryEmailAddressCodeInfo.Length;

                if (!string.IsNullOrEmpty(passwordState.RecoveryEmailAddressCodeInfo.EmailAddressPattern))
                {
                    Pattern = passwordState.RecoveryEmailAddressCodeInfo.EmailAddressPattern;
                }

                await MessagePopup.ShowAsync(Strings.Resources.ResendCodeInfo, Strings.Resources.TwoStepVerification, Strings.Resources.OK);
            }
            else if (response is PasswordState)
            {
                await MessagePopup.ShowAsync(Strings.Resources.CodeExpired, Strings.Resources.TwoStepVerification, Strings.Resources.OK);
            }
            else if (response is Error error)
            {
                await ShowErrorAsync(error);
            }
        }

        public RelayCommand SendCommand { get; }
        private async void SendExecute()
        {
            var code = _code;
            if (string.IsNullOrEmpty(code))
            {
                await MessagePopup.ShowAsync(Strings.Resources.InvalidCode, Strings.Resources.TwoStepVerification, Strings.Resources.OK);
                return;
            }

            if (_isSending)
            {
                return;
            }

            _isSending = true;
            var response = await ProtoService.SendAsync(new CheckRecoveryEmailAddressCode(code));
            _isSending = false;

            if (response is PasswordState passwordState)
            {
                if (passwordState.RecoveryEmailAddressCodeInfo != null)
                {
                    Code = string.Empty;
                    await MessagePopup.ShowAsync(Strings.Resources.InvalidCode, Strings.Resources.RestorePasswordNoEmailTitle, Strings.Resources.OK);
                }
                else if (passwordState.HasPassword)
                {
                    NavigationService.Navigate(typeof(SettingsPasswordDonePage));
                }
                else
                {
                    await MessagePopup.ShowAsync(Strings.Resources.CodeExpired, Strings.Resources.TwoStepVerification, Strings.Resources.OK);
                }
            }
            else if (response is Error error)
            {
                Code = string.Empty;

                if (error.TypeEquals(ErrorType.CODE_INVALID))
                {
                    await MessagePopup.ShowAsync(Strings.Resources.InvalidCode, Strings.Resources.RestorePasswordNoEmailTitle, Strings.Resources.OK);
                }
                else
                {
                    await ShowErrorAsync(error);
                }
            }
        }

        public RelayCommand AbortCommand { get; }
        private async void AbortExecute()
        {
            if (_isSending)
            {
                return;
            }

            var confirm = await MessagePopup.ShowAsync(Strings.Resources.CancelEmailQuestion, Strings.Resources.CancelEmailQuestionTitle, Strings.Resources.Abort, Strings.Resources.Cancel);
            if (confirm != ContentDialogResult.Primary)
            {
                return;
            }

            _isSending = true;
            var response = await ProtoService.SendAsync(new CancelRecoveryEmailAddressVerification());
            _isSending = false;

            if (response is PasswordState)
            {
                SettingsPasswordDoneViewModel.RemoveSetupPages(NavigationService);

                if (NavigationService.CanGoBack)
                {
                    NavigationService.GoBack();
                }
            }
            else if (response is Error error)
            {
                await ShowErrorAsync(error);
            }
        }

        private static Task<ContentDialogResult> ShowErrorAsync(Error error)
        {
            return MessagePopup.ShowAsync(error.Message, Strings.Resources.AppName, Strings.Resources.OK);
        }
    }
}