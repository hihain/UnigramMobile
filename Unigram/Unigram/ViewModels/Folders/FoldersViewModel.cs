using System.Collections.Generic;
using System.Threading.Tasks;
using Telegram.Td.Api;
using Unigram.Collections;
using Unigram.Common;
using Unigram.Controls;
using Unigram.Services;
using Unigram.Services.Updates;
using Unigram.Views.Folders;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace Unigram.ViewModels.Folders
{
    public class FoldersViewModel : TLViewModelBase, IHandle<UpdateChatFolders>
    {
        public FoldersViewModel(IProtoService protoService, ICacheService cacheService, ISettingsService settingsService, IEventAggregator aggregator)
            : base(protoService, cacheService, settingsService, aggregator)
        {
            UseLeftLayout = settingsService.IsLeftTabsEnabled;
            UseTopLayout = !settingsService.IsLeftTabsEnabled;

            Items = new MvxObservableCollection<ChatFolderInfo>();
            Recommended = new MvxObservableCollection<RecommendedChatFolder>();

            RecommendCommand = new RelayCommand<RecommendedChatFolder>(RecommendExecute);
            EditCommand = new RelayCommand<ChatFolderInfo>(EditExecute);
            DeleteCommand = new RelayCommand<ChatFolderInfo>(DeleteExecute);

            CreateCommand = new RelayCommand(CreateExecute);
        }

        public MvxObservableCollection<ChatFolderInfo> Items { get; private set; }
        public MvxObservableCollection<RecommendedChatFolder> Recommended { get; private set; }

        public override async Task OnNavigatedToAsync(object parameter, NavigationMode mode, IDictionary<string, object> state)
        {
            Items.ReplaceWith(CacheService.ChatFolders);

            if (Items.Count < 10)
            {
                var response = await ProtoService.SendAsync(new GetRecommendedChatFolders());
                if (response is RecommendedChatFolders filters)
                {
                    Recommended.ReplaceWith(filters.ChatFolders);
                }
            }
            else
            {
                Recommended.Clear();
            }

            Aggregator.Subscribe(this);
        }

        public override Task OnNavigatedFromAsync(IDictionary<string, object> pageState, bool suspending)
        {
            Aggregator.Unsubscribe(this);
            return base.OnNavigatedFromAsync(pageState, suspending);
        }

        public int LeftLayoutScale
        {
            get
            {
                //Note: Slider snaps in only on integers -> convert float percentage to int
                return System.Convert.ToInt16(Settings.Appearance.TabsLeftLayoutScale * 100 - 50) / 5;
            }
            set
            {
                var update = value * .05f + .5f;

                if (Settings.Appearance.TabsLeftLayoutScale != update)
                {
                    Settings.Appearance.TabsLeftLayoutScale = update;
                    RaisePropertyChanged();
                    Aggregator.Publish(new UpdateChatFiltersLayout());
                }
            }
        }

        private bool _useLeftLayout;
        public bool UseLeftLayout
        {
            get => _useLeftLayout;
            set
            {
                if (_useLeftLayout != value)
                {
                    _useLeftLayout = value;
                    Settings.IsLeftTabsEnabled = value;

                    RaisePropertyChanged(() => UseLeftLayout);

                    Aggregator.Publish(new UpdateChatFiltersLayout());
                }
            }
        }

        private bool _useTopLayout;
        public bool UseTopLayout
        {
            get => _useTopLayout;
            set => Set(ref _useTopLayout, value);
        }

        public void Handle(UpdateChatFolders update)
        {
            BeginOnUIThread(async () =>
            {
                Items.ReplaceWith(update.ChatFolders);

                if (Items.Count < 10)
                {
                    var response = await ProtoService.SendAsync(new GetRecommendedChatFolders());
                    if (response is RecommendedChatFolders recommended)
                    {
                        Recommended.ReplaceWith(recommended.ChatFolders);
                    }
                }
                else
                {
                    Recommended.Clear();
                }
            });
        }

        public RelayCommand<RecommendedChatFolder> RecommendCommand { get; }
        private void RecommendExecute(RecommendedChatFolder filter)
        {
            Recommended.Remove(filter);
            ProtoService.Send(new CreateChatFolder(filter.Folder));
        }

        public RelayCommand<ChatFolderInfo> EditCommand { get; }
        private void EditExecute(ChatFolderInfo filter)
        {
            NavigationService.Navigate(typeof(FolderPage), filter.Id);
        }

        public RelayCommand<ChatFolderInfo> DeleteCommand { get; }
        private async void DeleteExecute(ChatFolderInfo filter)
        {
            var confirm = await MessagePopup.ShowAsync(Strings.Resources.FilterDeleteAlert, Strings.Resources.FilterDelete, Strings.Resources.Delete, Strings.Resources.Cancel);
            if (confirm != ContentDialogResult.Primary)
            {
                return;
            }

            ProtoService.Send(new DeleteChatFolder(filter.Id, new long[0]));
        }

        public RelayCommand CreateCommand { get; }
        private void CreateExecute()
        {
            NavigationService.Navigate(typeof(FolderPage));
        }
    }
}
