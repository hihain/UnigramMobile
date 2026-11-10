using System;
using Telegram.Td.Api;
using Unigram.Controls;
using Unigram.Logs;
using Unigram.Navigation.Services;
using Unigram.Services;
using Unigram.ViewModels.SignIn;
using Unigram.Views;
using Unigram.Views.SignIn;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace Unigram.Common
{
    public class TLRootNavigationService : NavigationService, IHandle<UpdateAuthorizationState>
    {
        private readonly ILifetimeService _lifetimeService;
        private readonly ISessionService _sessionService;

        public TLRootNavigationService(ISessionService sessionService, Frame frame, int session, string id)
            : base(frame, session, id)
        {
            _lifetimeService = TLContainer.Current.Lifetime;
            _sessionService = sessionService;
        }

        public async void Handle(UpdateAuthorizationState update)
        {
#if MODERN_TDLIB
            PushDiagnostics.Write("startup.authorization", $"state={update.AuthorizationState?.GetType().Name ?? "null"}");
#endif
            switch (update.AuthorizationState)
            {
                case AuthorizationStateWaitTdlibParameters waitTdlibParameters:
#if MODERN_TDLIB
                    // A known account is restored as soon as TDLib reports Ready. Do not
                    // overwrite the system splash with the first-run intro during that wait.
                    if (_sessionService.Settings.UserId == 0 && Frame.Content == null)
                    {
                        Navigate(typeof(IntroPage));
                    }
#else
                    Navigate(typeof(SignInPage));
#endif
                    break;
                case AuthorizationStateReady ready:
#if MODERN_TDLIB
                    // Navigate builds MainPage synchronously, so a failure in that page would
                    // otherwise surface far from its cause - either swallowed by
                    // ProtoService.OnResult or reported as a bare app-level unhandled
                    // exception with no indication that navigation was the trigger.
                    try
                    {
                        Navigate(typeof(MainPage));
                        PushDiagnostics.Write("startup.navigation", "target=MainPage;result=ok");
                    }
                    catch (Exception ex)
                    {
                        PushDiagnostics.WriteException("startup.navigation.MainPage", ex);
                        throw;
                    }
#else
                    Navigate(typeof(MainPage));
#endif
                    break;
                case AuthorizationStateWaitPhoneNumber waitPhoneNumber:
                case AuthorizationStateWaitOtherDeviceConfirmation waitOtherDeviceConfirmation:
                    if (_lifetimeService.Items.Count > 1)
                    {
                        if (Frame.Content is SignInPage page && page.DataContext is SignInViewModel viewModel)
                        {
                            await viewModel.OnNavigatedToAsync(null, NavigationMode.Refresh, null);
                        }
                        else
                        {
                            Navigate(typeof(SignInPage));
                        }

                        Frame.BackStack.Clear();
                        Frame.BackStack.Add(new PageStackEntry(typeof(BlankPage), null, null));
                    }
                    else
                    {
                        if (Frame.Content is SignInPage page && page.DataContext is SignInViewModel viewModel)
                        {
                            await viewModel.OnNavigatedToAsync(null, NavigationMode.Refresh, null);
                        }
                        else
                        {
                            Navigate(typeof(SignInPage));
                        }
                    }
                    break;
                case AuthorizationStateWaitCode waitCode:
                    Navigate(typeof(SignInSentCodePage));
                    break;
                case AuthorizationStateWaitRegistration waitRegistration:
                    Navigate(typeof(SignUpPage));
                    break;
                case AuthorizationStateWaitPassword waitPassword:
                    if (!string.IsNullOrEmpty(waitPassword.RecoveryEmailAddressPattern))
                    {
                        await MessagePopup.ShowAsync(string.Format(Strings.Resources.RestoreEmailSent, waitPassword.RecoveryEmailAddressPattern), Strings.Resources.AppName, Strings.Resources.OK);
                    }

                    Navigate(string.IsNullOrEmpty(waitPassword.RecoveryEmailAddressPattern) ? typeof(SignInPasswordPage) : typeof(SignInRecoveryPage));
                    break;
#if MODERN_TDLIB
                default:
                    // Deliberately does not navigate: an unmapped modern state must not
                    // misroute the user into phone login. Content is already on screen by
                    // this point, so recording the state is enough to diagnose it.
                    PushDiagnostics.Write("startup.authorization", $"state={update.AuthorizationState?.GetType().Name ?? "null"};result=unmapped");
                    break;
#endif
            }
        }
    }
}
