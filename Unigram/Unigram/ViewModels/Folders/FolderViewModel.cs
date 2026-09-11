using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Telegram.Td.Api;
using Unigram.Collections;
using Unigram.Common;
using Unigram.Converters;
using Unigram.Services;
using Unigram.Views.Popups;
using Windows.UI.Xaml.Navigation;

namespace Unigram.ViewModels.Folders
{
    public class FolderViewModel : TLViewModelBase
    {
        public FolderViewModel(IProtoService protoService, ICacheService cacheService, ISettingsService settingsService, IEventAggregator aggregator)
            : base(protoService, cacheService, settingsService, aggregator)
        {
            Include = new MvxObservableCollection<ChatFolderElement>();
            Exclude = new MvxObservableCollection<ChatFolderElement>();

            Include.CollectionChanged += OnCollectionChanged;
            Exclude.CollectionChanged += OnCollectionChanged;

            RemoveIncludeCommand = new RelayCommand<ChatFolderElement>(RemoveIncludeExecute);
            RemoveExcludeCommand = new RelayCommand<ChatFolderElement>(RemoveExcludeExecute);

            AddIncludeCommand = new RelayCommand(AddIncludeExecute);
            AddExcludeCommand = new RelayCommand(AddExcludeExecute);

            SendCommand = new RelayCommand(SendExecute, SendCanExecute);
        }

        private void OnCollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            SendCommand.RaiseCanExecuteChanged();
        }

        public override async Task OnNavigatedToAsync(object parameter, NavigationMode mode, IDictionary<string, object> state)
        {
            ChatFolder filter = null;

            if (parameter is int id)
            {
                var response = await ProtoService.SendAsync(new GetChatFolder(id));
                if (response is ChatFolder result)
                {
                    Id = id;
                    Filter = result;
                    filter = result;
                }
                else
                {
                    // TODO
                }
            }
            else
            {
                Id = null;
                Filter = null;
                filter = new ChatFolder();
                filter.PinnedChatIds = new List<long>();
                filter.IncludedChatIds = new List<long>();
                filter.ExcludedChatIds = new List<long>();
            }

            if (filter == null)
            {
                return;
            }

            if (state != null && state.TryGet("included_chat_id", out long includedChatId))
            {
                filter.IncludedChatIds.Add(includedChatId);
            }

            _pinnedChatIds = filter.PinnedChatIds;

            _iconPicked = !string.IsNullOrEmpty(filter.Icon?.Name);

            Title = filter.Title;
            Icon = Icons.ParseFilter(filter);

            Include.Clear();
            Exclude.Clear();

            if (filter.IncludeContacts) Include.Add(new FilterFlag { Flag = ChatListFolderFlags.IncludeContacts });
            if (filter.IncludeNonContacts) Include.Add(new FilterFlag { Flag = ChatListFolderFlags.IncludeNonContacts });
            if (filter.IncludeGroups) Include.Add(new FilterFlag { Flag = ChatListFolderFlags.IncludeGroups });
            if (filter.IncludeChannels) Include.Add(new FilterFlag { Flag = ChatListFolderFlags.IncludeChannels });
            if (filter.IncludeBots) Include.Add(new FilterFlag { Flag = ChatListFolderFlags.IncludeBots });

            if (filter.ExcludeMuted) Exclude.Add(new FilterFlag { Flag = ChatListFolderFlags.ExcludeMuted });
            if (filter.ExcludeRead) Exclude.Add(new FilterFlag { Flag = ChatListFolderFlags.ExcludeRead });
            if (filter.ExcludeArchived) Exclude.Add(new FilterFlag { Flag = ChatListFolderFlags.ExcludeArchived });

            foreach (var chatId in filter.PinnedChatIds.Union(filter.IncludedChatIds))
            {
                var chat = CacheService.GetChat(chatId);
                if (chat == null)
                {
                    continue;
                }

                Include.Add(new FilterChat { Chat = chat });
            }

            foreach (var chatId in filter.ExcludedChatIds)
            {
                var chat = CacheService.GetChat(chatId);
                if (chat == null)
                {
                    continue;
                }

                Exclude.Add(new FilterChat { Chat = chat });
            }

            UpdateIcon();
        }

        public int? Id { get; set; }

        private ChatFolder _filter;
        public ChatFolder Filter
        {
            get => _filter;
            set => Set(ref _filter, value);
        }

        private string _title = string.Empty;
        public string Title
        {
            get => _title;
            set
            {
                Set(ref _title, value);
                SendCommand.RaiseCanExecuteChanged();
            }
        }

        private bool _iconPicked;

        private ChatFolderIconKind _icon;
        public ChatFolderIconKind Icon
        {
            get => _icon;
            private set => Set(ref _icon, value);
        }

        public void SetIcon(ChatFolderIconKind icon)
        {
            _iconPicked = true;
            Icon = icon;
        }

        private void UpdateIcon()
        {
            if (_iconPicked)
            {
                return;
            }

            Icon = Icons.ParseFilter(GetFilter());
        }

        private IList<long> _pinnedChatIds;

        public MvxObservableCollection<ChatFolderElement> Include { get; private set; }
        public MvxObservableCollection<ChatFolderElement> Exclude { get; private set; }



        public RelayCommand AddIncludeCommand { get; }
        private async void AddIncludeExecute()
        {
            await AddIncludeAsync();
            UpdateIcon();
        }

        public async Task AddIncludeAsync()
        {
            var result = await SharePopup.AddExecute(true, Include.ToList());
            if (result != null)
            {
                foreach (var item in result.OfType<FilterChat>())
                {
                    var already = Exclude.OfType<FilterChat>().FirstOrDefault(x => x.Chat.Id == item.Chat.Id);
                    if (already != null)
                    {
                        Exclude.Remove(already);
                    }
                }

                var flags = result.OfType<FilterFlag>().Cast<ChatFolderElement>();
                var chats = result.OfType<FilterChat>().OrderBy(x =>
                {
                    var index = _pinnedChatIds.IndexOf(x.Chat.Id);
                    if (index != -1)
                    {
                        return index;
                    }

                    return int.MaxValue;
                });

                Include.ReplaceWith(flags.Union(chats));
            }
        }

        public RelayCommand AddExcludeCommand { get; }
        private async void AddExcludeExecute()
        {
            await AddExcludeAsync();
            UpdateIcon();
        }

        public async Task AddExcludeAsync()
        {
            var result = await SharePopup.AddExecute(false, Exclude.ToList());
            if (result != null)
            {
                foreach (var item in result.OfType<FilterChat>())
                {
                    var already = Include.OfType<FilterChat>().FirstOrDefault(x => x.Chat.Id == item.Chat.Id);
                    if (already != null)
                    {
                        Include.Remove(already);
                    }
                }

                Exclude.ReplaceWith(result);
            }
        }

        public RelayCommand<ChatFolderElement> RemoveIncludeCommand { get; }
        private void RemoveIncludeExecute(ChatFolderElement chat)
        {
            Include.Remove(chat);
            UpdateIcon();
        }

        public RelayCommand<ChatFolderElement> RemoveExcludeCommand { get; }
        private void RemoveExcludeExecute(ChatFolderElement chat)
        {
            Exclude.Remove(chat);
            UpdateIcon();
        }

        public RelayCommand SendCommand { get; }
        private async void SendExecute()
        {
            var response = await SendAsync();
            if (response is ChatFolderInfo)
            {
                NavigationService.GoBack();
            }
        }

        public Task<BaseObject> SendAsync()
        {
            Function function;
            if (Id is int id)
            {
                function = new EditChatFolder(id, GetFilter());
            }
            else
            {
                function = new CreateChatFolder(GetFilter());
            }

            return ProtoService.SendAsync(function);
        }

        private bool SendCanExecute()
        {
            return !string.IsNullOrEmpty(Title) && Include.Count > 0;
        }

        private ChatFolder GetFilter()
        {
            var filter = new ChatFolder();
            filter.Title = Title ?? string.Empty;
            filter.Icon = new ChatFolderIcon(_iconPicked ? Enum.GetName(typeof(ChatFolderIconKind), Icon) : string.Empty);
            filter.PinnedChatIds = new List<long>();
            filter.IncludedChatIds = new List<long>();
            filter.ExcludedChatIds = new List<long>();

            foreach (var item in Include)
            {
                if (item is FilterFlag flag)
                {
                    switch (flag.Flag)
                    {
                        case ChatListFolderFlags.IncludeContacts:
                            filter.IncludeContacts = true;
                            break;
                        case ChatListFolderFlags.IncludeNonContacts:
                            filter.IncludeNonContacts = true;
                            break;
                        case ChatListFolderFlags.IncludeGroups:
                            filter.IncludeGroups = true;
                            break;
                        case ChatListFolderFlags.IncludeChannels:
                            filter.IncludeChannels = true;
                            break;
                        case ChatListFolderFlags.IncludeBots:
                            filter.IncludeBots = true;
                            break;
                    }
                }
                else if (item is FilterChat chat)
                {
                    if (_pinnedChatIds.Contains(chat.Chat.Id))
                    {
                        filter.PinnedChatIds.Add(chat.Chat.Id);
                    }
                    else
                    {
                        filter.IncludedChatIds.Add(chat.Chat.Id);
                    }
                }
            }

            foreach (var item in Exclude)
            {
                if (item is FilterFlag flag)
                {
                    switch (flag.Flag)
                    {
                        case ChatListFolderFlags.ExcludeMuted:
                            filter.ExcludeMuted = true;
                            break;
                        case ChatListFolderFlags.ExcludeRead:
                            filter.ExcludeRead = true;
                            break;
                        case ChatListFolderFlags.ExcludeArchived:
                            filter.ExcludeArchived = true;
                            break;
                    }
                }
                else if (item is FilterChat chat)
                {
                    filter.ExcludedChatIds.Add(chat.Chat.Id);
                }
            }

            return filter;
        }
    }

    public class ChatFolderElement
    {
    }

    public class FilterFlag : ChatFolderElement
    {
        public ChatListFolderFlags Flag { get; set; }
    }

    public class FilterChat : ChatFolderElement
    {
        public Chat Chat { get; set; }
    }
}
