using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Numerics;
using Telegram.Td.Api;
using Unigram.Collections;
using Unigram.Common;
using Unigram.Controls;
using Unigram.Navigation;
using Unigram.Navigation.Services;
using Unigram.Services;
using Unigram.ViewModels;
using Unigram.Views.SignIn;
using Windows.ApplicationModel.Core;
using Windows.Foundation;
using Windows.Foundation.Metadata;
using Windows.UI.Composition;
using Windows.UI.Core;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
using Windows.UI.Xaml.Shapes;
using Point = Windows.Foundation.Point;

namespace Unigram.Views.Host
{
    public sealed partial class RootPage : Page
    {
        private ILifetimeService _lifetime;
        private NavigationService _navigationService;

        private RootDestination _navigationViewSelected;
        private MvxObservableCollection<object> _navigationViewItems;
        private readonly Dictionary<int, User> _sessionUsers = new Dictionary<int, User>();
        private readonly HashSet<int> _pendingSessionUsers = new HashSet<int>();
        private int _accountSelectorDiagnosticBudget = 64;
        private long _accountSelectorSessionsHash = long.MinValue;
        private bool _accountSelectorWasExpanded;

        public RootPage(NavigationService service)
        {
            if (SettingsService.Current.Appearance.RequestedTheme != ElementTheme.Default)
            {
                RequestedTheme = SettingsService.Current.Appearance.GetCalculatedElementTheme();
            }

            InitializeComponent();

            _lifetime = TLContainer.Current.Lifetime;
            _lifetime.Items.CollectionChanged += OnSessionsChanged;

            _navigationViewSelected = RootDestination.Chats;
            _navigationViewItems = new MvxObservableCollection<object>
            {
                //RootDestination.Separator,

                RootDestination.NewChat,
                RootDestination.NewSecretChat,
                RootDestination.NewChannel,

                RootDestination.Separator,

                RootDestination.Chats,
                RootDestination.Contacts,
                RootDestination.Calls,
                RootDestination.Settings,

                //RootDestination.Separator,

                //RootDestination.Wallet,

                RootDestination.Separator,

                RootDestination.SavedMessages,
                RootDestination.News
            };

            NavigationViewList.ItemsSource = _navigationViewItems;

            service.Frame.Navigating += OnNavigating;
            service.Frame.Navigated += OnNavigated;
            _navigationService = service;

            InitializeTitleBar();
            InitializeNavigation(service.Frame);
            InitializeLocalization();

            Navigation.Content = _navigationService.Frame;

            var shadow = DropShadowEx.Attach(ThemeShadow, 20, 0.25f);
            ThemeShadow.SizeChanged += (s, args) =>
            {
                shadow.Size = args.NewSize.ToVector2();
            };
        }

        public void UpdateComponent()
        {
            _contentLoaded = false;
            Resources.Clear();
            InitializeComponent();

            _navigationViewSelected = RootDestination.Chats;
            NavigationViewList.ItemsSource = _navigationViewItems;
            _accountSelectorSessionsHash = long.MinValue;

            InitializeTitleBar();
            InitializeNavigation(_navigationService.Frame);
            InitializeLocalization();

            Switch(_lifetime.ActiveItem);
        }

        private void InitializeTitleBar()
        {
            if (ApiInfo.IsFullExperience &&
                UIViewSettings.GetForCurrentView().UserInteractionMode == UserInteractionMode.Mouse) return;
            var sender = CoreApplication.GetCurrentView().TitleBar;
            Navigation.Padding = new Thickness(0, sender.IsVisible ? sender.Height : 0, 0, 0);
        }

        public void SetTopPadding(Thickness thickness)
        {
            Navigation.SetTopPadding(thickness);
        }

        public void Create()
        {
            Switch(_lifetime.Create());
        }

        public void Switch(ISessionService session)
        {
            _lifetime.ActiveItem = session;
            InitializeSessions(SettingsService.Current.IsAccountsSelectorExpanded, GetSessionsSnapshot());

            if (_navigationService != null)
            {
                Destroy(_navigationService);
            }

            Navigation.IsPaneOpen = false;
            Navigation.SetTopPadding(new Thickness());

            var service = WindowContext.GetForCurrentView().NavigationServices.GetByFrameId($"{session.Id}") as NavigationService;
            if (service == null)
            {
                service = BootStrapper.Current.NavigationServiceFactory(BootStrapper.BackButton.Attach, BootStrapper.ExistingContent.Exclude, new Frame(), session.Id, $"{session.Id}", true) as NavigationService;
                service.Frame.Navigating += OnNavigating;
                service.Frame.Navigated += OnNavigated;

                switch (session.ProtoService.GetAuthorizationState())
                {
                    case AuthorizationStateReady ready:
                        service.Navigate(typeof(MainPage));
                        break;
                    case AuthorizationStateWaitPhoneNumber waitPhoneNumber:
                    case AuthorizationStateWaitOtherDeviceConfirmation waitOtherDeviceConfirmation:
                        service.Navigate(typeof(SignInPage));
                        service.Frame.BackStack.Add(new PageStackEntry(typeof(BlankPage), null, null));
                        break;
                    case AuthorizationStateWaitCode waitCode:
                        service.Navigate(typeof(SignInSentCodePage));
                        break;
                    case AuthorizationStateWaitRegistration waitRegistration:
                        service.Navigate(typeof(SignUpPage));
                        break;
                    case AuthorizationStateWaitPassword waitPassword:
                        service.Navigate(typeof(SignInPasswordPage));
                        break;
#if MODERN_TDLIB
                    case null:
                    case AuthorizationStateWaitTdlibParameters waitTdlibParameters:
                        if (session.Settings.UserId == 0)
                        {
                            service.Navigate(typeof(IntroPage));
                        }
                        break;
#endif
                }

                //WindowContext.GetForCurrentView().Handle(session, new UpdateConnectionState(session.ProtoService.GetConnectionState()));
                session.Aggregator.Publish(new UpdateConnectionState(session.ProtoService.GetConnectionState()));
            }
            else
            {
                // TODO: This should actually __never__ happen.
            }

            _navigationService = service;
            Navigation.Content = service.Frame;
        }

        private void Destroy(NavigationService master)
        {
            if (master.Frame.Content is IRootContentPage content)
            {
                content.Root = null;
                content.Dispose();
            }

            var detail = WindowContext.GetForCurrentView().NavigationServices.GetByFrameId($"Main{master.FrameFacade.FrameId}");
            if (detail != null)
            {
                detail.Navigate(typeof(BlankPage));
                detail.ClearCache();
            }

            master.Frame.Navigating -= OnNavigating;
            master.Frame.Navigated -= OnNavigated;
            master.Frame.Navigate(typeof(BlankPage));

            WindowContext.GetForCurrentView().NavigationServices.Remove(master);
            WindowContext.GetForCurrentView().NavigationServices.Remove(detail);
        }

        private void OnNavigating(object sender, NavigatingCancelEventArgs e)
        {
            if (_navigationService?.Frame?.Content is IRootContentPage content)
            {
                content.Root = null;
            }
        }

        private void OnNavigated(object sender, NavigationEventArgs e)
        {
            if (e.Content is IRootContentPage content)
            {
                content.Root = this;
                Navigation.PaneToggleButtonVisibility = content.EvalutatePaneToggleButtonVisibility();
                InitializeNavigation(sender as Frame);
            }
            else
            {
                Navigation.PaneToggleButtonVisibility = Visibility.Collapsed;
            }
        }

        private void InitializeLocalization()
        {
            AddAccountItem.Text = Strings.Resources.AddAccount;
            //NavigationChats.Text = Strings.Additional.Chats;
            //NavigationAbout.Content = Strings.Additional.About;
            //NavigationNews.Text = Strings.Additional.News;
        }

        private void InitializeNavigation(Frame frame)
        {
            if (frame?.Content is MainPage main)
            {
                InitializeUser(main.ViewModel);
            }

            InitializeSessions(SettingsService.Current.IsAccountsSelectorExpanded, GetSessionsSnapshot());
        }

        private async void OnSessionsChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (Dispatcher.HasThreadAccess)
            {
                InitializeSessions(SettingsService.Current.IsAccountsSelectorExpanded, GetSessionsSnapshot());
                return;
            }

            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                InitializeSessions(SettingsService.Current.IsAccountsSelectorExpanded, GetSessionsSnapshot());
            });
        }

        private async void InitializeUser(MainViewModel viewModel)
        {
            var user = viewModel.CacheService.GetUser(viewModel.CacheService.Options.MyId);
            if (user == null)
            {
                user = await viewModel.ProtoService.SendAsync(new GetMe()) as User;
            }

            if (user == null)
            {
                return;
            }

            NameLabel.Text = user.GetFullName();
#if DEBUG
            PhoneLabel.Text = "+42 --- --- ----";
#else
            if (viewModel.Chats.Settings.UseTestDC)
            {
                PhoneLabel.Text = "+42 --- --- ----";
            }
            else
            {
                PhoneLabel.Text = PhoneNumber.Format(user.PhoneNumber);
            }
#endif
            Expanded.IsChecked = SettingsService.Current.IsAccountsSelectorExpanded;
            Automation.SetToolTip(Accounts, SettingsService.Current.IsAccountsSelectorExpanded ? Strings.Resources.AccDescrHideAccounts : Strings.Resources.AccDescrShowAccounts);
        }

        private static IList<ISessionService> GetSessionsSnapshot()
        {
            return TLContainer.Current.GetSessions().OrderBy(session => session.Id).ToList();
        }

        private static long GetSessionsHash(IList<ISessionService> sessions)
        {
            unchecked
            {
                var hash = 17L;
                foreach (var session in sessions)
                {
                    hash = hash * 31 + session.Id;
                }

                return hash;
            }
        }

        private User GetSessionUser(ISessionService session)
        {
            var user = session.ProtoService.GetUser(session.UserId);
            if (user == null)
            {
                _sessionUsers.TryGetValue(session.Id, out user);
            }

            return user;
        }

        private void RefreshSessionContainers()
        {
            foreach (var child in AccountSelectorPanel.Children)
            {
                var container = child as ListViewItem;
                var session = container?.Tag as ISessionService;
                var content = container?.ContentTemplateRoot as Grid;
                if (session == null || content == null)
                {
                    continue;
                }

                var user = GetSessionUser(session);
                if (user == null)
                {
                    RequestSessionUser(session);
                    continue;
                }

                var title = content.Children[2] as TextBlock;
                title.Text = user.GetFullName();
                Automation.SetToolTip(content, title.Text);

                var indicator = content.Children[1] as Rectangle;
                indicator.Visibility = session == _lifetime.ActiveItem ? Visibility.Visible : Visibility.Collapsed;

                var photo = content.Children[0] as ProfilePicture;
                photo.Source = PlaceholderHelper.GetUser(session.ProtoService, user, 28);
            }
        }

        private void RequestSessionUser(ISessionService session)
        {
            if (session.UserId == 0 || !_pendingSessionUsers.Add(session.Id))
            {
                TraceAccountSelector($"stage=request;slot={session.Id};queued=0");
                return;
            }

            TraceAccountSelector($"stage=request;slot={session.Id};queued=1");
            session.ProtoService.Send(new GetUser(session.UserId), response =>
            {
                _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    _pendingSessionUsers.Remove(session.Id);

                    if (response is User user && user.Id == session.UserId)
                    {
                        _sessionUsers[session.Id] = user;
                        TraceAccountSelector($"stage=response;slot={session.Id};result=user");
                        InitializeSessions(SettingsService.Current.IsAccountsSelectorExpanded, GetSessionsSnapshot());
                    }
                    else
                    {
                        TraceAccountSelector($"stage=response;slot={session.Id};result={response?.GetType().Name ?? "null"}");
                    }
                });
            });
        }

        private void TraceAccountSelector(string value)
        {
            if (System.Threading.Interlocked.Decrement(ref _accountSelectorDiagnosticBudget) >= 0)
            {
                Logs.PushDiagnostics.Write("account_selector", value);
            }
        }

        private void InitializeSessions(bool show, IList<ISessionService> items)
        {
            var visibility = show ? Visibility.Visible : Visibility.Collapsed;
            AccountSelectorPanel.Visibility = visibility;
            AddAccountItem.Visibility = visibility;
            AccountSelectorSeparator.Visibility = visibility;

            var sessionsHash = GetSessionsHash(items);
            if (_accountSelectorSessionsHash == long.MinValue)
            {
                TraceAccountSelector($"stage=origin;stored={CountStoredSessions()};containers={items?.Count ?? 0}");
            }

            if (_accountSelectorSessionsHash == sessionsHash && _accountSelectorWasExpanded == show
                && AccountSelectorPanel.Children.Count == (show ? items.Count : 0))
            {
                RefreshSessionContainers();
                TraceAccountSelector($"stage=refresh;expanded={(show ? 1 : 0)};source={items.Count};rows={AccountSelectorPanel.Children.Count}");
                return;
            }

            var desiredSessions = show ? items : new List<ISessionService>();

            AccountSelectorPanel.Children.Clear();

            foreach (var session in desiredSessions)
            {
                AccountSelectorPanel.Children.Add(CreateSessionRow(session));
            }

            _accountSelectorSessionsHash = sessionsHash;
            _accountSelectorWasExpanded = show;
            TraceAccountSelector($"stage=update;expanded={(show ? 1 : 0)};source={items?.Count ?? 0};rows={AccountSelectorPanel.Children.Count}");

            RefreshSessionContainers();
            TraceAccountSelectorLayout();
        }

        private static int CountStoredSessions()
        {
            try
            {
                return SessionStorage.EnumerateStoredSessions().Count();
            }
            catch
            {
                return -1;
            }
        }

        private ListViewItem CreateSessionRow(ISessionService session)
        {
            var container = new ListViewItem
            {
                Content = session,
                Tag = session,
                ContentTemplate = Resources["SessionItemTemplate"] as DataTemplate,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(0)
            };

            container.Tapped += OnSessionRowTapped;
            container.Loaded += OnSessionRowLoaded;
            return container;
        }

        private void OnSessionRowLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is ListViewItem container && container.Tag is ISessionService)
            {
                RefreshSessionContainers();
            }
        }

        private void OnSessionRowTapped(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            if (sender is ListViewItem container && container.Tag is ISessionService session && !session.IsActive)
            {
                Switch(session);
            }
        }

        private void TraceAccountSelectorLayout()
        {
            AccountSelectorPanel.UpdateLayout();

            var built = AccountSelectorPanel.Children.Count;
            var realized = 0;
            var heights = 0.0;

            foreach (var child in AccountSelectorPanel.Children)
            {
                var element = child as FrameworkElement;
                if (element == null)
                {
                    continue;
                }

                if (element.Visibility == Visibility.Visible && element.ActualHeight > 0)
                {
                    realized++;
                }

                heights += element.ActualHeight;
            }

            TraceAccountSelector($"stage=layout;built={built};realized={realized};rowsum={(int)heights};panel={(int)AccountSelectorPanel.ActualHeight};vis={(AccountSelectorPanel.Visibility == Visibility.Visible ? 1 : 0)}");
        }

        #region Recycling

        private void OnChoosingItemContainer(ListViewBase sender, ChoosingItemContainerEventArgs args)
        {
            if (args.Item is ISessionService && (args.ItemContainer == null || args.ItemContainer is Controls.NavigationViewItem || args.ItemContainer is Controls.NavigationViewItemSeparator))
            {
                args.ItemContainer = new ListViewItem();
                args.ItemContainer.Style = sender.ItemContainerStyle;
                args.ItemContainer.ContentTemplate = Resources["SessionItemTemplate"] as DataTemplate;
            }
            else if (args.Item is RootDestination destination)
            {
                if (destination == RootDestination.Separator && !(args.ItemContainer is Controls.NavigationViewItemSeparator))
                {
                    args.ItemContainer = new Controls.NavigationViewItemSeparator();
                }
                else if (destination != RootDestination.Separator && !(args.ItemContainer is Controls.NavigationViewItem))
                {
                    args.ItemContainer = new Controls.NavigationViewItem();
                    args.ItemContainer.ContextRequested += OnContextRequested;
                }
            }

            args.IsContainerPrepared = true;
        }

        private void OnContextRequested(UIElement sender, Windows.UI.Xaml.Input.ContextRequestedEventArgs args)
        {
            var container = sender as Controls.NavigationViewItem;
            if (container is ISessionService session && !session.IsActive)
            {

            }
            else if (container.Content is RootDestination destination && destination == RootDestination.AddAccount)
            {
                var alt = Window.Current.CoreWindow.GetKeyState(Windows.System.VirtualKey.Menu).HasFlag(CoreVirtualKeyStates.Down);
                var ctrl = Window.Current.CoreWindow.GetKeyState(Windows.System.VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
                var shift = Window.Current.CoreWindow.GetKeyState(Windows.System.VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);

                if (alt && !ctrl && shift)
                {
                    var flyout = new MenuFlyout();

                    flyout.CreateFlyoutItem(new RelayCommand(() => Switch(_lifetime.Create(test: false))), "Production Server", new FontIcon { Glyph = "\uE774" });
                    flyout.CreateFlyoutItem(new RelayCommand(() => Switch(_lifetime.Create(test: true))), "Test Server", new FontIcon { Glyph = "\uE825" });

                    args.ShowAt(flyout, container);
                }
            }
        }

        private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.InRecycleQueue)
            {
                return;
            }

            if (args.Item is ISessionService session)
            {
                var content = args.ItemContainer.ContentTemplateRoot as Grid;
                if (content == null)
                {
                    return;
                }

                var user = GetSessionUser(session);

                if (user == null)
                {
                    RequestSessionUser(session);
                }

                TraceAccountSelector($"stage=container;slot={session.Id};phase={args.Phase};profile={(user != null ? 1 : 0)}");

                if (args.Phase == 0)
                {
                    var title = content.Children[2] as TextBlock;
                    title.Text = user?.GetFullName() ?? Strings.Resources.Loading;

                    Automation.SetToolTip(content, title.Text);

                    var indicator = content.Children[1] as Rectangle;
                    indicator.Visibility = session == _lifetime.ActiveItem ? Visibility.Visible : Visibility.Collapsed;
                }
                else if (args.Phase == 2 && user != null)
                {
                    var photo = content.Children[0] as ProfilePicture;
                    photo.Source = PlaceholderHelper.GetUser(session.ProtoService, user, 28);
                }

                if (args.Phase < 2)
                {
                    args.RegisterUpdateCallback(OnContainerContentChanging);
                }
            }
            else if (args.Item is RootDestination destination)
            {
                var content = args.ItemContainer as Controls.NavigationViewItem;
                if (content != null)
                {
                    content.IsChecked = destination == _navigationViewSelected;

                    if (destination == RootDestination.SavedMessages)
                    {
                        content.FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Telegram.ttf#Telegram");
                    }
                    else
                    {
                        content.FontFamily = new FontFamily("Segoe MDL2 Assets");
                    }
                }

                switch (destination)
                {
                    case RootDestination.AddAccount:
                        content.Text = Strings.Resources.AddAccount;
                        content.Glyph = "\uE109";
                        break;

                    case RootDestination.NewChat:
                        content.Text = Strings.Resources.NewGroup;
                        content.Glyph = "\uE902";
                        break;
                    case RootDestination.NewSecretChat:
                        content.Text = Strings.Resources.NewSecretChat;
                        content.Glyph = "\uE1F6";
                        break;
                    case RootDestination.NewChannel:
                        content.Text = Strings.Resources.NewChannel;
                        content.Glyph = "\uE789";
                        break;

                    case RootDestination.Chats:
                        content.Text = Strings.Resources.FilterChats;
                        content.Glyph = "\uE8BD";
                        break;
                    case RootDestination.Contacts:
                        content.Text = Strings.Resources.Contacts;
                        content.Glyph = "\uE716";
                        break;
                    case RootDestination.Calls:
                        content.Text = Strings.Resources.Calls;
                        content.Glyph = "\uE717";
                        break;
                    case RootDestination.Settings:
                        content.Text = Strings.Resources.Settings;
                        content.Glyph = "\uE115";
                        break;

                    case RootDestination.SavedMessages:
                        content.Text = Strings.Resources.SavedMessages;
                        content.Glyph = "\uE907";
                        break;

                    case RootDestination.News:
                        content.Text = Strings.Additional.News;
                        content.Glyph = "\uE789";
                        break;
                }
            }
        }

        #endregion

        private void Expand_Click(object sender, RoutedEventArgs e)
        {
            SettingsService.Current.IsAccountsSelectorExpanded = !SettingsService.Current.IsAccountsSelectorExpanded;

            InitializeSessions(SettingsService.Current.IsAccountsSelectorExpanded, GetSessionsSnapshot());
            Expanded.IsChecked = SettingsService.Current.IsAccountsSelectorExpanded;

            Automation.SetToolTip(Accounts, SettingsService.Current.IsAccountsSelectorExpanded ? Strings.Resources.AccDescrHideAccounts : Strings.Resources.AccDescrShowAccounts);
        }

        private void OnAddAccountTapped(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            Switch(_lifetime.Create());
        }

        private void OnItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is ISessionService session)
            {
                if (session.IsActive)
                {
                    return;
                }

                Switch(session);
            }
            else if (e.ClickedItem is RootDestination destination)
            {
                if (destination == RootDestination.AddAccount)
                {
                    Switch(_lifetime.Create());
                }
                else if (_navigationService?.Frame?.Content is IRootContentPage content)
                {
                    content.NavigationView_ItemClick(destination);
                }
            }

            Navigation.IsPaneOpen = false;

            var scroll = NavigationViewList.GetScrollViewer();
            if (scroll != null)
            {
                scroll.ChangeView(null, 0, null, true);
            }
        }

        #region Exposed

        public void SetPaneToggleButtonVisibility(Visibility value)
        {
            Navigation.PaneToggleButtonVisibility = value;
        }

        public void SetSelectedIndex(RootDestination value)
        {
            _navigationViewSelected = value;

            void SetChecked(RootDestination destination, RootDestination target)
            {
                var selector = NavigationViewList.ContainerFromItem(_navigationViewItems.FirstOrDefault(x => x is RootDestination y && y == destination)) as Controls.NavigationViewItem;
                if (selector != null)
                {
                    selector.IsChecked = destination == target;
                }
            }

            SetChecked(RootDestination.Chats, value);
            SetChecked(RootDestination.Contacts, value);
            SetChecked(RootDestination.Calls, value);
            SetChecked(RootDestination.Settings, value);
        }

        #endregion

        public void ShowEditor(ThemeCustomInfo theme)
        {
            var resize = ThemePage == null;

            FindName("ThemePage");
            ThemePage.Load(theme);

            if (resize)
            {
                MainColumn.Width = new GridLength(ActualWidth, GridUnitType.Pixel);

                var view = ApplicationView.GetForCurrentView();
                var size = view.VisibleBounds;

                view.TryResizeView(new Size(size.Width + 320, size.Height));
                ApplicationView.PreferredLaunchViewSize = new Size(size.Width, size.Height);

                MainColumn.Width = new GridLength(1, GridUnitType.Star);
            }
        }

        public void HideEditor()
        {
            UnloadObject(ThemePage);

            var view = ApplicationView.GetForCurrentView();
            view.TryResizeView(ApplicationView.PreferredLaunchViewSize);
        }
    }

    public interface IRootContentPage
    {
        RootPage Root { get; set; }

        void NavigationView_ItemClick(RootDestination destination);

        //Visibility PaneToggleButtonVisibility { get; }

        Visibility EvalutatePaneToggleButtonVisibility();

        void Dispose();
    }

    public enum RootDestination
    {
        AddAccount,

        NewChat,
        NewSecretChat,
        NewChannel,

        Chats,
        Contacts,
        Calls,
        Settings,

        InviteFriends,

        SavedMessages,
        News,

        Separator
    }
}
