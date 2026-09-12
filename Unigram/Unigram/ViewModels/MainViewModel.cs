using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Telegram.Td.Api;
using Unigram.Common;
using Unigram.Controls;
using Unigram.Converters;
using Unigram.Navigation;
using Unigram.Services;
using Unigram.Services.Updates;
using Unigram.ViewModels.Folders;
using Unigram.Views;
using Unigram.Views.Folders;
using Unigram.Views.Popups;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace Unigram.ViewModels
{
    public class MainViewModel : TLMultipleViewModelBase,
        IHandle<UpdateServiceNotification>,
        IHandle<UpdateUnreadMessageCount>,
        IHandle<UpdateUnreadChatCount>,
        IHandle<UpdateChatFolders>,
#if CLOUDUPDATES
        IHandle<UpdateAppVersion>,
        IHandle<UpdateWindowActivated>,
#endif
        IDisposable
    {
        private readonly INotificationsService _pushService;
        private readonly IContactsService _contactsService;
        private readonly IVibrationService _vibrationService;
        private readonly IPasscodeService _passcodeService;
        private readonly ILifetimeService _lifetimeService;
        private readonly ISessionService _sessionService;
        private readonly IVoIPService _voipService;
        private readonly IEmojiSetService _emojiSetService;
#if CLOUDUPDATES
        private readonly ICloudUpdateService _cloudUpdateService;
#endif
        private readonly IPlaybackService _playbackService;
        private readonly IShortcutsService _shortcutService;

        public bool Refresh { get; set; }

#if CLOUDUPDATES
        public MainViewModel(IProtoService protoService, ICacheService cacheService, ISettingsService settingsService, IEventAggregator aggregator, INotificationsService pushService, IContactsService contactsService, IVibrationService vibrationService, IPasscodeService passcodeService, ILifetimeService lifecycle, ISessionService session, IVoIPService voipService, ISettingsSearchService settingsSearchService, IEmojiSetService emojiSetService, ICloudUpdateService cloudUpdateService, IPlaybackService playbackService, IShortcutsService shortcutService)
            : base(protoService, cacheService, settingsService, aggregator)
#else
        public MainViewModel(IProtoService protoService, ICacheService cacheService, ISettingsService settingsService, IEventAggregator aggregator, INotificationsService pushService, IContactsService contactsService, IVibrationService vibrationService, IPasscodeService passcodeService, ILifetimeService lifecycle, ISessionService session, IVoIPService voipService, ISettingsSearchService settingsSearchService, IEmojiSetService emojiSetService, IPlaybackService playbackService, IShortcutsService shortcutService)
            : base(protoService, cacheService, settingsService, aggregator)
#endif
        {
            _pushService = pushService;
            _contactsService = contactsService;
            _vibrationService = vibrationService;
            _passcodeService = passcodeService;
            _lifetimeService = lifecycle;
            _sessionService = session;
            _voipService = voipService;
            _emojiSetService = emojiSetService;
#if CLOUDUPDATES
            _cloudUpdateService = cloudUpdateService;
#endif
            _playbackService = playbackService;
            _shortcutService = shortcutService;

            Filters = new ChatFolderCollection();

            Chats = new ChatsViewModel(protoService, cacheService, settingsService, aggregator, pushService, new ChatListMain());
            ArchivedChats = new ChatsViewModel(protoService, cacheService, settingsService, aggregator, pushService, new ChatListArchive());
            Contacts = new ContactsViewModel(protoService, cacheService, settingsService, aggregator, contactsService);
            Calls = new CallsViewModel(protoService, cacheService, settingsService, aggregator);
            Settings = new SettingsViewModel(protoService, cacheService, settingsService, aggregator, settingsSearchService);

            // This must represent pivot tabs
            Children.Add(Chats);
            Children.Add(Contacts);
            Children.Add(Calls);
            Children.Add(Settings);

            // Any additional child
            Children.Add(ArchivedChats);
            Children.Add(_voipService as TLViewModelBase);

            aggregator.Subscribe(this);

            ReturnToCallCommand = new RelayCommand(ReturnToCallExecute);

            ToggleArchiveCommand = new RelayCommand(ToggleArchiveExecute);

            CreateSecretChatCommand = new RelayCommand(CreateSecretChatExecute);

            SetupFiltersCommand = new RelayCommand(SetupFiltersExecute);
#if CLOUDUPDATES
            UpdateAppCommand = new RelayCommand(UpdateAppExecute);
#endif
            FilterEditCommand = new RelayCommand<ChatFolderViewModel>(FilterEditExecute);
            FilterAddCommand = new RelayCommand<ChatFolderViewModel>(FilterAddExecute);
            FilterMarkAsReadCommand = new RelayCommand<ChatFolderViewModel>(FilterMarkAsReadExecute);
            FilterDeleteCommand = new RelayCommand<ChatFolderViewModel>(FilterDeleteExecute);
        }

        public void Dispose()
        {
            Aggregator.Unsubscribe(ArchivedChats.Items);
            Aggregator.Unsubscribe(Chats.Items);
            Aggregator.Unsubscribe(this);

            if (Dispatcher != null && Dispatcher.HasThreadAccess)
            {
                ArchivedChats.Items.Clear();
                Chats.Items.Clear();
            }

            Children.Clear();
        }

        public ILifetimeService Lifetime => _lifetimeService;
        public ISessionService Session => _sessionService;

        public IPasscodeService Passcode => _passcodeService;

        public IPlaybackService PlaybackService => _playbackService;

        public IShortcutsService ShortcutService => _shortcutService;

        public RelayCommand ToggleArchiveCommand { get; }
        private void ToggleArchiveExecute()
        {
            CollapseArchivedChats = !CollapseArchivedChats;
        }

        public RelayCommand SetupFiltersCommand { get; }
        private void SetupFiltersExecute()
        {
            NavigationService.Navigate(typeof(FoldersPage));
        }

        public bool CollapseArchivedChats
        {
            get
            {
                return base.Settings.CollapseArchivedChats;
            }
            set
            {
                base.Settings.CollapseArchivedChats = value;
                RaisePropertyChanged();
            }
        }

        private int _unreadCount;
        public int UnreadCount
        {
            get { return _unreadCount; }
            set { Set(ref _unreadCount, value); }
        }

        private int _unreadMutedCount;
        public int UnreadMutedCount
        {
            get => _unreadMutedCount;
            set => Set(ref _unreadMutedCount, value);
        }

        private int _unreadUnmutedCount;
        public int UnreadUnmutedCount
        {
            get => _unreadUnmutedCount;
            set => Set(ref _unreadUnmutedCount, value);
        }

        private bool _isUpdateAvailable;
        public bool IsUpdateAvailable
        {
            get => _isUpdateAvailable;
            set => Set(ref _isUpdateAvailable, value);
        }

        public RelayCommand ReturnToCallCommand { get; }
        private void ReturnToCallExecute()
        {
            _voipService.Show();
        }
#if CLOUDUPDATES
        public void Handle(UpdateAppVersion update)
        {
            BeginOnUIThread(() => UpdateAppVersion(update.Update));
        }

        public void Handle(UpdateWindowActivated update)
        {
            if (update.IsActive)
            {
                _ = _cloudUpdateService.UpdateAsync(false);
            }
        }

        private void UpdateAppVersion(CloudUpdate update)
        {
            IsUpdateAvailable = update?.File != null;
        }
#endif
        public void Handle(UpdateServiceNotification update)
        {

        }

        public void Handle(UpdateUnreadMessageCount update)
        {
            if (update.ChatList is ChatListMain)
            {
                BeginOnUIThread(() =>
                {
                    UnreadCount = update.UnreadCount;
                    UnreadUnmutedCount = update.UnreadUnmutedCount;
                    UnreadMutedCount = update.UnreadCount - update.UnreadUnmutedCount;
                });
            }
        }

        public void Handle(UpdateUnreadChatCount update)
        {
            BeginOnUIThread(() =>
            {
                foreach (var filter in _filters)
                {
                    if (filter.ChatList is ChatListFolder && filter.ChatList.ListEquals(update.ChatList))
                    {
                        filter.UpdateCount(update);
                    }
                }
            });
        }

        public void Handle(UpdateChatFolders update)
        {
            BeginOnUIThread(() => UpdateChatFolders(update.ChatFolders));
        }

        private void UpdateChatFolders(IList<ChatFolderInfo> chatFolders)
        {
            if (chatFolders.Count > 0)
            {
                var selected = SelectedFilter?.ChatFolderId ?? Constants.ChatListMain;
                //var origin = chatFolders.Select(x => Filters.FirstOrDefault(y => y.ChatFolderId == x.ChatFolderId));

                Merge(Filters, new[] { new ChatFolderInfo { Id = Constants.ChatListMain, Title = Strings.Resources.FilterAllChats, Icon = new ChatFolderIcon("All") } }.Union(chatFolders).ToArray());

                if (Chats.Items.ChatList is ChatListFolder already && already.ChatFolderId != selected)
                {
                    SelectedFilter = Filters[0];
                }
                else
                {
                    RaisePropertyChanged(() => SelectedFilter);
                }

                foreach (var filter in _filters)
                {
                    if (filter.ChatList is ChatListMain)
                    {
                        continue;
                    }

                    var unreadCount = CacheService.GetUnreadCount(filter.ChatList);
                    if (unreadCount == null)
                    {
                        continue;
                    }

                    filter.UpdateCount(unreadCount.UnreadChatCount);
                }
            }
            else
            {
                Filters.Clear();
                SelectedFilter = ChatFolderViewModel.Main;
            }
        }

        private void Merge(IList<ChatFolderViewModel> destination, IList<ChatFolderInfo> origin)
        {
            if (destination.Count > 0)
            {
                for (int i = 0; i < destination.Count; i++)
                {
                    var user = destination[i];
                    var index = -1;

                    for (int j = 0; j < origin.Count; j++)
                    {
                        if (origin[j].Id == user.ChatFolderId)
                        {
                            index = j;
                            break;
                        }
                    }

                    if (index == -1)
                    {
                        destination.Remove(user);
                        i--;
                    }
                }

                for (int i = 0; i < origin.Count; i++)
                {
                    var filter = origin[i];
                    var index = -1;

                    for (int j = 0; j < destination.Count; j++)
                    {
                        if (destination[j].ChatFolderId == filter.Id)
                        {
                            destination[j].Update(filter);

                            index = j;
                            break;
                        }
                    }

                    if (index > -1 && index != i)
                    {
                        destination.RemoveAt(index);
                        destination.Insert(Math.Min(i, destination.Count), new ChatFolderViewModel(filter));
                    }
                    else if (index == -1)
                    {
                        destination.Insert(Math.Min(i, destination.Count), new ChatFolderViewModel(filter));
                    }
                }
            }
            else
            {
                destination.Clear();
                destination.AddRange(origin.Select(x => new ChatFolderViewModel(x)));
            }
        }

        private ChatFolderCollection _filters;
        public ChatFolderCollection Filters
        {
            get => _filters;
            set => Set(ref _filters, value);
        }

        public ChatFolderViewModel SelectedFilter
        {
            get
            {
                if (Chats.Items.ChatList is ChatListFolder filter && _filters != null)
                {
                    return _filters.FirstOrDefault(x => x.ChatFolderId == filter.ChatFolderId);
                }

                return _filters?.FirstOrDefault();
            }
            set
            {
                if (Chats.Items.ChatList.ListEquals(value.ChatList))
                {
                    return;
                }

                Chats.SetFilter(value.ChatList);
                RaisePropertyChanged();
            }
        }

        public override Task OnNavigatedToAsync(object parameter, NavigationMode mode, IDictionary<string, object> state)
        {
            //BeginOnUIThread(() => Calls.OnNavigatedToAsync(parameter, mode, state));
            //BeginOnUIThread(() => Settings.OnNavigatedToAsync(parameter, mode, state));
            //Dispatch(() => Dialogs.LoadFirstSlice());
            //Dispatch(() => Contacts.getTLContacts());
            //Dispatch(() => Contacts.GetSelfAsync());
#if CLOUDUPDATES
            UpdateAppVersion(_cloudUpdateService.NextUpdate);
#endif
            UpdateChatFolders(CacheService.ChatFolders);

            var unreadCount = CacheService.GetUnreadCount(new ChatListMain());
            UnreadCount = unreadCount.UnreadMessageCount.UnreadCount;
            UnreadMutedCount = unreadCount.UnreadMessageCount.UnreadCount - unreadCount.UnreadMessageCount.UnreadUnmutedCount;

            if (mode == NavigationMode.New)
            {
                Task.Run(() => _pushService.RegisterAsync());
                Task.Run(() => _contactsService.JumpListAsync());
                Task.Run(() => _emojiSetService.UpdateAsync());
#if CLOUDUPDATES
                Task.Run(() => _cloudUpdateService.UpdateAsync(false));
#endif
            }

            return base.OnNavigatedToAsync(parameter, mode, state);
        }

        public ChatsViewModel Chats { get; private set; }
        public ChatsViewModel ArchivedChats { get; private set; }
        public ContactsViewModel Contacts { get; private set; }
        public CallsViewModel Calls { get; private set; }
        public new SettingsViewModel Settings { get; private set; }

        public ChatsViewModel Folder { get; private set; }

        public void SetFolder(ChatList chatList)
        {
            if (chatList is ChatListMain || chatList == null)
            {
                return;
            }

            Folder = ArchivedChats;
            RaisePropertyChanged(() => Folder);
        }


#if CLOUDUPDATES
        public RelayCommand UpdateAppCommand { get; }
        private async void UpdateAppExecute()
        {
            var file = _cloudUpdateService.NextUpdate?.File;
            if (file == null)
            {
                return;
            }

            await Launcher.LaunchFileAsync(file);
            Application.Current.Exit();
        }
#endif
        public RelayCommand CreateSecretChatCommand { get; }
        private async void CreateSecretChatExecute()
        {
            var selected = await SharePopup.PickChatAsync(Strings.Resources.NewSecretChat);
            var user = CacheService.GetUser(selected);

            if (user == null)
            {
                return;
            }

            var confirm = await MessagePopup.ShowAsync(Strings.Resources.AreYouSureSecretChat, Strings.Resources.AppName, Strings.Resources.OK, Strings.Resources.Cancel);
            if (confirm != ContentDialogResult.Primary)
            {
                return;
            }

            var response = await ProtoService.SendAsync(new CreateNewSecretChat(user.Id));
            if (response is Chat chat)
            {
                NavigationService.NavigateToChat(chat);
            }
        }

        public RelayCommand<ChatFolderViewModel> FilterAddCommand { get; }
        private void FilterEditExecute(ChatFolderViewModel filter)
        {
            if (filter.ChatFolderId == Constants.ChatListMain)
            {
                NavigationService.Navigate(typeof(FoldersPage));
            }
            else
            {
                NavigationService.Navigate(typeof(FolderPage), filter.ChatFolderId);
            }
        }

        public RelayCommand<ChatFolderViewModel> FilterEditCommand { get; }
        private async void FilterAddExecute(ChatFolderViewModel filter)
        {
            var viewModel = TLContainer.Current.Resolve<FolderViewModel>();
            await viewModel.OnNavigatedToAsync(filter.ChatFolderId, NavigationMode.New, null);
            await viewModel.AddIncludeAsync();
            await viewModel.SendAsync();
        }

        public RelayCommand<ChatFolderViewModel> FilterMarkAsReadCommand { get; }
        private async void FilterMarkAsReadExecute(ChatFolderViewModel filter)
        {
            var confirm = await MessagePopup.ShowAsync(Strings.Resources.AreYouSure, Strings.Resources.AppName, Strings.Resources.MarkAsRead, Strings.Resources.Cancel);
            if (confirm != ContentDialogResult.Primary)
            {
                return;
            }

            var chats = await ProtoService.GetChatListAsync(filter.ChatList, 0, int.MaxValue);
            if (chats.TotalCount > chats.ChatIds.Count)
            {
                // ???
            }

            foreach (var id in chats.ChatIds)
            {
                var chat = CacheService.GetChat(id);
                if (chat == null || chat.LastMessage == null || !chat.IsUnread())
                {
                    continue;
                }

                ProtoService.Send(new ViewMessages(chat.Id, new[] { chat.LastMessage.Id }, null, true));
            }
        }

        public RelayCommand<ChatFolderViewModel> FilterDeleteCommand { get; }
        private async void FilterDeleteExecute(ChatFolderViewModel filter)
        {
            var confirm = await MessagePopup.ShowAsync(Strings.Resources.FilterDeleteAlert, Strings.Resources.FilterDelete, Strings.Resources.Delete, Strings.Resources.Cancel);
            if (confirm != ContentDialogResult.Primary)
            {
                return;
            }

            ProtoService.Send(new DeleteChatFolder(filter.ChatFolderId, new long[0]));
        }
    }

    public class ChatFolderViewModel : BindableBase
    {
        public static ChatFolderViewModel Main => new ChatFolderViewModel
        {
            ChatFolderId = Constants.ChatListMain,
            Title = Strings.Resources.FilterAllChats
        };

        public ChatFolderViewModel(ChatFolderInfo info)
        {
            if (info.Id == Constants.ChatListMain)
            {
                ChatList = new ChatListMain();
            }
            else
            {
                ChatList = new ChatListFolder(info.Id);
            }

            ChatFolderId = info.Id;

            _title = info.Title;
            _icon = Icons.ParseFilter(info.Icon?.Name);
            _iconUri = new Uri($"ms-appx:///Assets/Filters/{_icon}.png");
        }

        private ChatFolderViewModel()
        {
            ChatList = new ChatListMain();
        }

        public void Update(ChatFolderInfo info)
        {
            Title = info.Title;
            Icon = Icons.ParseFilter(info.Icon?.Name);
            IconUri = new Uri($"ms-appx:///Assets/Filters/{_icon}.png");
        }

        public ChatList ChatList { get; }

        public int ChatFolderId { get; set; }

        private string _title;
        public string Title
        {
            get => _title;
            set => Set(ref _title, value);
        }

        private ChatFolderIconKind _icon;
        public ChatFolderIconKind Icon
        {
            get => _icon;
            set => Set(ref _icon, value);
        }

        private Uri _iconUri;
        public Uri IconUri
        {
            get => _iconUri;
            set => Set(ref _iconUri, value);
        }

        private int _unreadCount;
        public int UnreadCount
        {
            get => _unreadCount;
            set => Set(ref _unreadCount, value);
        }

        private int _unreadUnmutedCount;
        public int UnreadUnmutedCount
        {
            get => _unreadUnmutedCount;
            set => Set(ref _unreadUnmutedCount, value);
        }

        private int _unreadMutedCount;
        public int UnreadMutedCount
        {
            get => _unreadMutedCount;
            set => Set(ref _unreadMutedCount, value);
        }

        public bool ShowUnmuted => _unreadUnmutedCount > 0;
        public bool ShowMuted => _unreadMutedCount > 0 && _unreadUnmutedCount == 0;

        public void UpdateCount(UpdateUnreadChatCount update)
        {
            UnreadCount = update.UnreadCount;
            UnreadUnmutedCount = update.UnreadUnmutedCount;
            UnreadMutedCount = update.UnreadCount - update.UnreadUnmutedCount;

            RaisePropertyChanged(() => ShowUnmuted);
            RaisePropertyChanged(() => ShowMuted);
        }
    }

    public enum ChatFolderIconKind
    {
        Custom,
        All,
        Unread,
        Unmuted,
        Bots,
        Channels,
        Groups,
        Private,
        Setup,
        Cat,
        Crown,
        Favorite,
        Flower,
        Game,
        Home,
        Love,
        Mask,
        Party,
        Sport,
        Study,
        Trade,
        Travel,
        Work
    }

    public class ChatFolderCollection : ObservableCollection<ChatFolderViewModel>, IKeyIndexMapping
    {
        public ChatFolderCollection()
        {

        }

        public ChatFolderCollection(IEnumerable<ChatFolderViewModel> source)
            : base(source)
        {

        }

        public string KeyFromIndex(int index)
        {
            return this[index].ChatFolderId.ToString();
        }

        public int IndexFromKey(string key)
        {
            return IndexOf(this.FirstOrDefault(x => key == x.ChatFolderId.ToString()));
        }
    }
}
