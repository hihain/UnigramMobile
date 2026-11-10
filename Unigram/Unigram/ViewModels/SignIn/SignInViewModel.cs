using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Telegram.Td.Api;
using Unigram.Common;
using Unigram.Controls;
using Unigram.Entities;
using Unigram.Logs;
using Unigram.Services;
using Unigram.ViewModels.Delegates;
using Unigram.Views.Settings;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace Unigram.ViewModels.SignIn
{
    public class SignInViewModel : TLViewModelBase, IDelegable<ISignInDelegate>
    {
        private readonly ISessionService _sessionService;
        private readonly ILifetimeService _lifetimeService;
        private readonly INotificationsService _notificationsService;

        public ISignInDelegate Delegate { get; set; }

        public SignInViewModel(IProtoService protoService, ICacheService cacheService, ISettingsService settingsService, IEventAggregator aggregator, ISessionService sessionService, ILifetimeService lifecycleService, INotificationsService notificationsService)
            : base(protoService, cacheService, settingsService, aggregator)
        {
            _sessionService = sessionService;
            _lifetimeService = lifecycleService;
            _notificationsService = notificationsService;

            SwitchCommand = new RelayCommand(SwitchExecute);
            SendCommand = new RelayCommand(SendExecute, () => !IsLoading);
            ProxyCommand = new RelayCommand(ProxyExecute);
        }

        public override Task OnNavigatedToAsync(object parameter, NavigationMode mode, IDictionary<string, object> state)
        {
            ProtoService.Send(new GetCountryCode(), result =>
            {
                if (result is Text text)
                {
                    BeginOnUIThread(() => GotUserCountry(text.TextValue));
                }
            });

            var authState = ProtoService.GetAuthorizationState();
            var waitState = authState is AuthorizationStateWaitPhoneNumber || authState is AuthorizationStateWaitCode || authState is AuthorizationStateWaitPassword;

            if (authState is AuthorizationStateWaitTdlibParameters)
            {
                IsLoading = false;
                Delegate?.UpdateQrCodeMode(QrCodeMode.Loading);
            }
            else if (waitState && mode != NavigationMode.Refresh)
            {
                SetAuthorizationLoading(false);
                Delegate?.UpdateQrCodeMode(QrCodeMode.Loading);

                ProtoService.Send(new GetApplicationConfig(), result =>
                {
                    if (result is JsonValueObject json)
                    {
                        var camera = json.GetNamedBoolean("qr_login_camera", true);
                        var code = json.GetNamedString("qr_login_code", "primary");

                        if (camera && Enum.TryParse(code, true, out QrCodeMode qrmode))
                        {
                            BeginOnUIThread(() => Delegate?.UpdateQrCodeMode(qrmode));

                            if (qrmode == QrCodeMode.Primary)
                            {
                                if (authState is AuthorizationStateWaitPhoneNumber)
                                {
                                    ProtoService.Send(new RequestQrCodeAuthentication(), qrResult =>
                                    {
                                        if (qrResult is Error error)
                                        {
                                            PushDiagnostics.Write(
                                                "signin.qr",
                                                $"result=failed;code={error.Code};message={PushDiagnostics.SanitizeErrorMessage(error.Message)}");

                                            BeginOnUIThread(() => Delegate?.UpdateQrCodeMode(QrCodeMode.Secondary));
                                        }
                                    });
                                }
                                else
                                {
                                    _sessionService.RequestQrCodeAuthentication();
                                }
                            }

                            return;
                        }
                    }

                    BeginOnUIThread(() => Delegate?.UpdateQrCodeMode(QrCodeMode.Disabled));
                });
            }
            else if (authState is AuthorizationStateWaitOtherDeviceConfirmation waitOtherDeviceConfirmation)
            {
                Token = waitOtherDeviceConfirmation.Link;
                Delegate?.UpdateQrCode(waitOtherDeviceConfirmation.Link);

                if (mode != NavigationMode.Refresh)
                {
                    Delegate?.UpdateQrCodeMode(QrCodeMode.Primary);
                }
            }
#if MODERN_TDLIB
            else if (!waitState)
            {
                // PhonePanel and TokenPanel share one grid cell and neither declares an
                // initial Visibility, so a single UpdateQrCodeMode call is the only thing
                // that makes this page readable. Any authorization state left unhandled
                // here renders both panels stacked on top of each other. A null state just
                // means TDLib has not reported yet, and TLRootNavigationService refreshes
                // this view model once authorizationStateWaitPhoneNumber arrives.
                IsLoading = false;
                Delegate?.UpdateQrCodeMode(authState == null ? QrCodeMode.Loading : QrCodeMode.Secondary);

                PushDiagnostics.Write("signin.navigated", $"state={authState?.GetType().Name ?? "null"};mode=fallback");
            }
#endif

            return Task.CompletedTask;
        }

        private void GotUserCountry(string code)
        {
            Country country = null;
            foreach (var local in Country.Countries)
            {
                if (string.Equals(local.Code, code, StringComparison.OrdinalIgnoreCase))
                {
                    country = local;
                    break;
                }
            }

            if (country != null && SelectedCountry == null && string.IsNullOrEmpty(PhoneNumber))
            {
                BeginOnUIThread(() =>
                {
                    SelectedCountry = country;
                });
            }
        }

        private string _token;
        public string Token
        {
            get => _token;
            set => Set(ref _token, value);
        }

        private Country _selectedCountry;
        public Country SelectedCountry
        {
            get => _selectedCountry;
            set => Set(ref _selectedCountry, value);
        }

        private string _phoneNumber;
        public string PhoneNumber
        {
            get => _phoneNumber;
            set => Set(ref _phoneNumber, value);
        }

        public IList<Country> Countries { get; } = Country.Countries.OrderBy(x => x.DisplayName).ToList();

        public RelayCommand SwitchCommand { get; }
        private void SwitchExecute()
        {
            if (ProtoService.AuthorizationState is AuthorizationStateWaitPhoneNumber)
            {
                ProtoService.Send(new RequestQrCodeAuthentication());
            }
        }

        public RelayCommand SendCommand { get; }
        private async void SendExecute()
        {
            var phoneNumber = _phoneNumber?.Trim('+').Replace(" ", string.Empty);
            if (string.IsNullOrEmpty(_phoneNumber))
            {
                RaisePropertyChanged("PHONE_NUMBER_INVALID");
                return;
            }

            foreach (var session in _lifetimeService.Items)
            {
                // We don't want to check other accounts if current one is test
                if (Settings.UseTestDC || session.Settings.UseTestDC)
                {
                    continue;
                }

                var user = session.ProtoService.GetUser(session.UserId);
                if (user == null)
                {
                    continue;
                }

                if (user.PhoneNumber.Contains(phoneNumber) || phoneNumber.Contains(user.PhoneNumber))
                {
                    var confirm = await MessagePopup.ShowAsync(Strings.Resources.AccountAlreadyLoggedIn, Strings.Resources.AppName, Strings.Resources.AccountSwitch, Strings.Resources.OK);
                    if (confirm == ContentDialogResult.Primary)
                    {
                        _lifetimeService.PreviousItem = session;
                        ProtoService.Send(new Destroy());
                    }

                    return;
                }
            }

            SetAuthorizationLoading(true);

            await _notificationsService.CloseAsync();

            var function = new SetAuthenticationPhoneNumber(phoneNumber, new PhoneNumberAuthenticationSettings(false, false, false, false
#if MODERN_TDLIB
                , false, null, new string[0]
#else
                , new string[0]
#endif
            ));
            var request = default(Task<BaseObject>);

            if (ProtoService.AuthorizationState is AuthorizationStateWaitOtherDeviceConfirmation)
            {
                request = _sessionService.SetAuthenticationPhoneNumberAsync(function);
            }
            else
            {
                request = ProtoService.SendAsync(function);
            }

            var response = await request;
            if (response is Error error)
            {
                SetAuthorizationLoading(false);
                PushDiagnostics.Write(
                    "signin.phone",
                    $"result=failed;code={error.Code};message={PushDiagnostics.SanitizeErrorMessage(error.Message)}");

                if (error.TypeEquals(ErrorType.PHONE_NUMBER_INVALID))
                {
                    //needShowInvalidAlert(req.phone_number, false);
                    await MessagePopup.ShowAsync(Strings.Resources.InvalidPhoneNumber, Strings.Resources.AppName, Strings.Resources.OK);
                }
                else if (error.TypeEquals(ErrorType.PHONE_PASSWORD_FLOOD))
                {
                    await MessagePopup.ShowAsync(Strings.Resources.FloodWait, Strings.Resources.AppName, Strings.Resources.OK);
                }
                else if (error.TypeEquals(ErrorType.PHONE_NUMBER_FLOOD))
                {
                    await MessagePopup.ShowAsync(Strings.Resources.PhoneNumberFlood, Strings.Resources.AppName, Strings.Resources.OK);
                }
                else if (error.TypeEquals(ErrorType.PHONE_NUMBER_BANNED))
                {
                    //needShowInvalidAlert(req.phone_number, true);
                    await MessagePopup.ShowAsync(Strings.Resources.BannedPhoneNumber, Strings.Resources.AppName, Strings.Resources.OK);
                }
                else if (error.TypeEquals(ErrorType.PHONE_CODE_EMPTY) || error.TypeEquals(ErrorType.PHONE_CODE_INVALID))
                {
                    await MessagePopup.ShowAsync(Strings.Resources.InvalidCode, Strings.Resources.AppName, Strings.Resources.OK);
                }
                else if (error.TypeEquals(ErrorType.PHONE_CODE_EXPIRED))
                {
                    await MessagePopup.ShowAsync(Strings.Resources.CodeExpired, Strings.Resources.AppName, Strings.Resources.OK);
                }
                else if (error.Message.StartsWith("FLOOD_WAIT"))
                {
                    await MessagePopup.ShowAsync(Strings.Resources.FloodWait, Strings.Resources.AppName, Strings.Resources.OK);
                }
                else if (error.Code != -1000)
                {
                    await MessagePopup.ShowAsync(error.Message, Strings.Resources.AppName, Strings.Resources.OK);
                }
            }
        }

        private void SetAuthorizationLoading(bool value)
        {
            IsLoading = value;
            SendCommand.RaiseCanExecuteChanged();
        }

        public RelayCommand ProxyCommand { get; }
        private void ProxyExecute()
        {
            NavigationService.Navigate(typeof(SettingsProxiesPage));
        }
    }
}