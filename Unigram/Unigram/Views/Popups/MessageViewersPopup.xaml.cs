using System;
using System.Collections.Generic;
using Telegram.Td.Api;
using Unigram.Common;
using Unigram.Controls;
using Unigram.Converters;
using Unigram.Services;
using Unigram.ViewModels.Delegates;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unigram.Views.Popups
{
    public sealed partial class MessageViewersPopup : ContentPopup
    {
        private readonly IProtoService _protoService;
        private readonly IMessageDelegate _delegate;

        public MessageViewersPopup(IProtoService protoService, IMessageDelegate delegato, IList<MessageViewer> viewers, bool played)
        {
            InitializeComponent();

            _protoService = protoService;
            _delegate = delegato;

            var items = new List<MessageViewerItem>();

            foreach (var viewer in viewers)
            {
                var user = protoService.GetUser(viewer.UserId);
                if (user != null)
                {
                    items.Add(new MessageViewerItem(user, viewer.ViewDate));
                }
            }

            Title = Locale.Declension(played ? "MessagePlayed" : "MessageSeen", items.Count);
            PrimaryButtonText = Strings.Resources.Close;

            ScrollingHost.ItemsSource = items;

            Loaded += OnLoaded;
        }

        // ContentPopup only bounds its content on the desktop, so on the phone the list
        // has to be capped here or it stops filling the dialog once it overflows.
        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            var bounds = ApplicationView.GetForCurrentView().VisibleBounds;
            ScrollingHost.MaxHeight = Math.Max(240, bounds.Height - 160);
        }

        private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.InRecycleQueue)
            {
                return;
            }

            var item = args.Item as MessageViewerItem;
            var content = args.ItemContainer.ContentTemplateRoot as Grid;

            if (item == null || content == null)
            {
                return;
            }

            var photo = content.Children[0] as ProfilePicture;
            photo.Source = PlaceholderHelper.GetUser(_protoService, item.User, 36);

            var title = content.Children[1] as TextBlock;
            title.Text = item.User.GetFullName();

            var date = content.Children[2] as TextBlock;
            date.Text = FormatViewDate(item.ViewDate);

            args.Handled = true;
        }

        // DateExtended drops the time outside of today, which is what people actually
        // want to know here, so it gets appended back on.
        private string FormatViewDate(int value)
        {
            var dateTime = BindConvert.Current.DateTime(value);
            var text = BindConvert.Current.DateExtended(value);

            if (dateTime.Date != System.DateTime.Now.Date)
            {
                text += ", " + BindConvert.Current.ShortTime.Format(dateTime);
            }

            return text;
        }

        private void OnItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is MessageViewerItem item)
            {
                Hide();
                _delegate.OpenUser(item.User.Id);
            }
        }

        private void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
        }
    }

    public class MessageViewerItem
    {
        public MessageViewerItem(User user, int viewDate)
        {
            User = user;
            ViewDate = viewDate;
        }

        public User User { get; private set; }
        public int ViewDate { get; private set; }
    }
}
