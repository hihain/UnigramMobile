using LinqToVisualTree;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Telegram.Td.Api;
using Unigram.Common;
using Unigram.Services;
using Unigram.ViewModels.Delegates;
using Unigram.ViewModels.Drawers;
using Unigram.Views;
using Windows.Foundation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Hosting;
using Windows.UI.Xaml.Input;
using StickerDrawerViewModel = Unigram.ViewModels.Drawers.StickerDrawerViewModel;
using StickerSetViewModel = Unigram.ViewModels.Drawers.StickerSetViewModel;
using StickerViewModel = Unigram.ViewModels.Drawers.StickerViewModel;

namespace Unigram.Controls.Drawers
{
    public sealed partial class StickerDrawer : UserControl, IDrawer, IFileDelegate
    {
        public StickerDrawerViewModel ViewModel => DataContext as StickerDrawerViewModel;

        public Action<Sticker> ItemClick { get; set; }
        public event TypedEventHandler<UIElement, ContextRequestedEventArgs> ItemContextRequested;

        private readonly AnimatedListHandler<StickerViewModel> _handler;
        private readonly ZoomableListHandler _zoomer;

        //private readonly AnimatedListHandler<StickerSetViewModel> _toolbarHandler;

        private FileContext<StickerViewModel> _stickers = new FileContext<StickerViewModel>();
        private FileContext<StickerSetViewModel> _stickerSets = new FileContext<StickerSetViewModel>();

        private bool _isActive;

#if MODERN_TDLIB
        private static int _templateBudget = 2;
        private static int _searchBudget = 2;

        private static void TraceTemplate(string site)
        {
            if (System.Threading.Interlocked.Decrement(ref _templateBudget) >= 0)
            {
                Logs.PushDiagnostics.Write("drawer.template", $"result=unavailable;site={site}");
            }
        }

        private static void TraceSearch(NullReferenceException exception, string context)
        {
            if (System.Threading.Interlocked.Decrement(ref _searchBudget) >= 0)
            {
                Logs.PushDiagnostics.WriteException("drawer.search", exception, context);
            }
        }
#endif

        public StickerDrawer()
        {
            InitializeComponent();

            _handler = new AnimatedListHandler<StickerViewModel>(Stickers);
            _handler.DownloadFile = (id, sticker) =>
            {
                DownloadFile(_stickers, id, sticker);
            };

            // Only for Desktop or Continuum
            if (ApiInfo.IsFullExperience || Windows.UI.ViewManagement.UIViewSettings.GetForCurrentView().UserInteractionMode == Windows.UI.ViewManagement.UserInteractionMode.Mouse)
            {
                _zoomer = new ZoomableListHandler(Stickers);
                _zoomer.Opening = _handler.UnloadVisibleItems;
                _zoomer.Closing = _handler.ThrottleVisibleItems;
                _zoomer.DownloadFile = fileId => ViewModel.ProtoService.DownloadFile(fileId, 32);
                _zoomer.GetEmojisAsync = fileId => ViewModel.ProtoService.SendAsync(new GetStickerEmojis(new InputFileId(fileId)));
            }

            //_toolbarHandler = new AnimatedStickerHandler<StickerSetViewModel>(Toolbar);

#if MODERN_TDLIB
            try
            {
                ElementCompositionPreview.GetElementVisual(this).Clip = Window.Current.Compositor.CreateInsetClip();

                var shadow = DropShadowEx.Attach(Separator, 20, 0.25f);
                DropShadowEx.SetRelativeSize(shadow, Separator);
            }
            catch (Exception ex)
            {
                Logs.PushDiagnostics.WriteException("drawer.construct", ex, "drawer=sticker");
            }
#else
            ElementCompositionPreview.GetElementVisual(this).Clip = Window.Current.Compositor.CreateInsetClip();

            var shadow = DropShadowEx.Attach(Separator, 20, 0.25f);
            shadow.RelativeSizeAdjustment = Vector2.One;
#endif

            var observable = Observable.FromEventPattern<TextChangedEventArgs>(FieldStickers, "TextChanged");
            var throttled = observable.Throttle(TimeSpan.FromMilliseconds(Constants.TypingTimeout)).ObserveOnDispatcher().Subscribe(async x =>
            {
                var items = ViewModel.SearchStickers;
                if (items != null && string.Equals(FieldStickers.Text, items.Query))
                {
                    await LoadSearchPhaseAsync(items, 1);
                    await LoadSearchPhaseAsync(items, 2);
                }
            });
        }

#if MODERN_TDLIB
        private static async Task LoadSearchPhaseAsync(SearchStickerSetsCollection items, uint phase)
        {
            try
            {
                await items.LoadMoreItemsAsync(phase);
            }
            catch (NullReferenceException ex)
            {
                // Emoji suggestions and sticker-set search are independent, so one failed
                // phase must not prevent the other from returning results.
                TraceSearch(ex, $"phase={phase}");
            }
        }
#else
        private static async Task LoadSearchPhaseAsync(SearchStickerSetsCollection items, uint phase)
        {
            await items.LoadMoreItemsAsync(phase);
        }
#endif

        public Services.Settings.StickersTab Tab => Services.Settings.StickersTab.Stickers;

        public void Activate()
        {
            _isActive = true;
            _handler.ThrottleVisibleItems();
        }

        public void Deactivate()
        {
            _isActive = false;
            _handler.UnloadVisibleItems();
        }

        public void LoadVisibleItems()
        {
            if (_isActive)
            {
                _handler.LoadVisibleItems(false);
            }
        }

        public void UnloadVisibleItems()
        {
            _handler.UnloadVisibleItems();
        }

        public void SetView(StickersPanelMode mode)
        {
        }

        public void UpdateFile(File file)
        {
            if (_stickers.TryGetValue(file.Id, out List<StickerViewModel> items) && items.Count > 0)
            {
                foreach (var item in items)
                {
                    item.UpdateFile(file);

                    if (item.Thumbnail?.File.Id == file.Id)
                    {
                        var container = Stickers.ContainerFromItem(item) as SelectorItem;
                        if (container == null)
                        {
                            continue;
                        }

                        var content = container.ContentTemplateRoot as Grid;
                        var photo = content?.Children.Count > 0 ? content.Children[0] as Image : null;
                        if (photo == null)
                        {
#if MODERN_TDLIB
                            TraceTemplate("file_item");
#endif
                            continue;
                        }

                        photo.Source = PlaceholderHelper.GetWebPFrame(file.Local.Path);
                    }
                    else if (item.StickerValue.Id == file.Id)
                    {
                        _handler.ThrottleVisibleItems();
                    }
                }
            }

            if (_stickerSets.TryGetValue(file.Id, out List<StickerSetViewModel> sets) && sets.Count > 0)
            {
                foreach (var item in sets)
                {
                    var cover = item.Thumbnail ?? item.Covers.FirstOrDefault()?.Thumbnail;
                    if (cover == null)
                    {
                        continue;
                    }

                    cover.UpdateFile(file);

                    var container = Toolbar.ContainerFromItem(item) as SelectorItem;
                    if (container == null)
                    {
                        continue;
                    }

                    var content = container.ContentTemplateRoot as Grid;
                    var photo = content?.Children.Count > 0 ? content.Children[0] as Image : null;

                    if (photo == null)
                    {
#if MODERN_TDLIB
                        TraceTemplate("file_toolbar");
#endif
                        continue;
                    }

                    if (item.IsAnimated)
                    {
                        photo.Source = PlaceholderHelper.GetLottieFrame(file.Local.Path, 0, 36, 36);
                    }
                    else
                    {
                        photo.Source = PlaceholderHelper.GetWebPFrame(file.Local.Path);
                    }
                }
            }

            if (ApiInfo.IsFullExperience || Windows.UI.ViewManagement.UIViewSettings.GetForCurrentView().UserInteractionMode == Windows.UI.ViewManagement.UserInteractionMode.Mouse)
                _zoomer.UpdateFile(file);
        }

        private void Stickers_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is StickerViewModel sticker && sticker.StickerValue != null)
            {
                ItemClick?.Invoke(sticker);
            }
        }

        private void Stickers_Loaded(object sender, RoutedEventArgs e)
        {
            var scrollingHost = Stickers.Descendants<ScrollViewer>().FirstOrDefault() as ScrollViewer;
            if (scrollingHost != null)
            {
                // Syncronizes GridView with the toolbar ListView
                scrollingHost.ViewChanged += ScrollingHost_ViewChanged;
                ScrollingHost_ViewChanged(null, null);
            }
        }

        private void Toolbar_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is StickerSetViewModel set && set.Stickers != null)
            {
                Stickers.ScrollIntoView(e.ClickedItem, ScrollIntoViewAlignment.Leading);
            }
        }

        public async void Refresh()
        {
            // TODO: memes

            await Task.Delay(100);
            //Pivot_SelectionChanged(null, null);
        }

        private void ScrollingHost_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            var scrollingHost = Stickers.ItemsPanelRoot as ItemsWrapGrid;
            if (scrollingHost != null && _isActive)
            {
                var first = Stickers.ContainerFromIndex(scrollingHost.FirstVisibleIndex);
                if (first != null)
                {
                    var header = Stickers.GroupHeaderContainerFromItemContainer(first) as GridViewHeaderItem;
                    if (header != null && header.Content != Toolbar.SelectedItem)
                    {
                        Toolbar.SelectedItem = header.Content;
                        Toolbar.ScrollIntoView(header.Content);
                    }
                }
            }
        }

        public bool ToggleActiveView()
        {
            //if (Pivot.SelectedIndex == 2 && !SemanticStickers.IsZoomedInViewActive && SemanticStickers.CanChangeViews)
            //{
            //    SemanticStickers.ToggleActiveView();
            //    return true;
            //}

            return false;
        }

        private async void OnChoosingGroupHeaderContainer(ListViewBase sender, ChoosingGroupHeaderContainerEventArgs args)
        {
            if (args.GroupHeaderContainer == null)
            {
                args.GroupHeaderContainer = new GridViewHeaderItem();
                args.GroupHeaderContainer.Style = Stickers.GroupStyle[0].HeaderContainerStyle;
                args.GroupHeaderContainer.ContentTemplate = Stickers.GroupStyle[0].HeaderTemplate;
            }

            if (args.Group is StickerSetViewModel group && !group.IsLoaded)
            {
                group.IsLoaded = true;

                //Debug.WriteLine("Loading sticker set " + group.Id);

                var response = await ViewModel.ProtoService.SendAsync(new GetStickerSet(group.Id));
                if (response is StickerSet full)
                {
                    group.Update(full, true);
                }
            }
        }

        private void OnChoosingItemContainer(ListViewBase sender, ChoosingItemContainerEventArgs args)
        {
            if (args.ItemContainer == null)
            {
                args.ItemContainer = new GridViewItem();
                args.ItemContainer.Style = sender.ItemContainerStyle;
                args.ItemContainer.ContentTemplate = sender.ItemTemplate;
                args.ItemContainer.ContextRequested += Sticker_ContextRequested;

                if (ApiInfo.IsFullExperience || Windows.UI.ViewManagement.UIViewSettings.GetForCurrentView().UserInteractionMode == Windows.UI.ViewManagement.UserInteractionMode.Mouse)
                    _zoomer.ElementPrepared(args.ItemContainer);
            }

            args.IsContainerPrepared = true;
        }

        private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            var content = args.ItemContainer.ContentTemplateRoot as Grid;
            var photo = content?.Children.Count > 0 ? content.Children[0] as Image : null;
            if (photo == null)
            {
#if MODERN_TDLIB
                TraceTemplate(args.InRecycleQueue ? "item_recycle" : "item_prepare");
#endif
                return;
            }

            if (args.InRecycleQueue)
            {
                while (content.Children.Count > 1)
                {
                    content.Children.RemoveAt(1);
                }

                photo.Opacity = 1;
                photo.Source = null;
                return;
            }

            var sticker = args.Item as StickerViewModel;

            args.ItemContainer.Tag = args.Item;
            args.ItemContainer.Content = args.Item;
            content.Tag = args.Item;

            if (sticker == null || sticker.Thumbnail == null)
            {
                while (content.Children.Count > 1)
                {
                    content.Children[0].Opacity = 1;
                    content.Children.RemoveAt(1);
                }

                photo.Source = null;
                return;
            }

            if (args.Phase < 2)
            {
                while (content.Children.Count > 1)
                {
                    content.Children.RemoveAt(1);
                }

                photo.Opacity = 1;
                photo.Source = null;
                args.RegisterUpdateCallback(OnContainerContentChanging);

                var file = sticker.Thumbnail.File;
                if (file.Local.CanBeDownloaded && !file.Local.IsDownloadingActive && !file.Local.IsDownloadingCompleted)
                {
                    DownloadFile(_stickers, file.Id, sticker);
                }
            }
            else if (args.Phase == 2)
            {
                //Debug.WriteLine("Loading sticker " + sticker.StickerValue.Id + " for sticker set id " + sticker.SetId);

                var file = sticker.Thumbnail.File;
                if (file.Local.IsDownloadingCompleted)
                {
                    photo.Source = PlaceholderHelper.GetWebPFrame(file.Local.Path);
                }
                else if (file.Local.CanBeDownloaded && !file.Local.IsDownloadingActive)
                {
                    DownloadFile(_stickers, file.Id, sticker);
                }
            }

            args.Handled = true;
        }

        private void Toolbar_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.InRecycleQueue)
            {
                return;
            }

            if (args.Item is SupergroupStickerSetViewModel supergroup)
            {
                var chat = ViewModel.CacheService.GetChat(supergroup.ChatId);
                if (chat == null)
                {
                    return;
                }

                var content = args.ItemContainer.ContentTemplateRoot as ProfilePicture;
                if (content == null)
                {
                    return;
                }

                content.Source = PlaceholderHelper.GetChat(ViewModel.ProtoService, chat, 36);
            }
            else if (args.Item is StickerSetViewModel sticker)
            {
                var content = args.ItemContainer.ContentTemplateRoot as Grid;
                var photo = content?.Children.Count > 0 ? content.Children[0] as Image : null;

                if (photo == null || sticker == null || (sticker.Thumbnail == null && sticker.Covers == null))
                {
#if MODERN_TDLIB
                    if (photo == null)
                    {
                        TraceTemplate("toolbar_prepare");
                    }
#endif
                    return;
                }

                var cover = sticker.Thumbnail ?? sticker.Covers.FirstOrDefault()?.Thumbnail;
                if (cover == null)
                {
                    photo.Source = null;
                    return;
                }

                var file = cover.File;
                if (file.Local.IsDownloadingCompleted)
                {
                    if (sticker.IsAnimated)
                    {
                        photo.Source = PlaceholderHelper.GetLottieFrame(file.Local.Path, 0, 36, 36);
                    }
                    else
                    {
                        photo.Source = PlaceholderHelper.GetWebPFrame(file.Local.Path);
                    }
                }
                else if (file.Local.CanBeDownloaded && !file.Local.IsDownloadingActive)
                {
                    photo.Source = null;
                    DownloadFile(_stickerSets, file.Id, sticker);
                }
            }
        }

        private void DownloadFile<T>(FileContext<T> context, int id, T sticker)
        {
            context[id].Add(sticker);
            ViewModel.ProtoService.DownloadFile(id, 1);
        }

        private void FieldStickers_TextChanged(object sender, TextChangedEventArgs e)
        {
            ViewModel.FindStickers(FieldStickers.Text);
        }

        private void Sticker_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
        {
            ItemContextRequested?.Invoke(sender, args);
        }
    }
}
