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

        public MessageViewersPopup(IProtoService protoService, IMessageDelegate delegato, IList<MessageViewerItem> items, string title)
        {
            InitializeComponent();

            _protoService = protoService;
            _delegate = delegato;

            Title = title;
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
            photo.Source = PlaceholderHelper.GetMessageSender(_protoService, item.Sender, 36);

            var title = content.Children[1] as TextBlock;
            title.Text = GetSenderName(item.Sender);

            var date = content.Children[2] as TextBlock;
            date.Text = FormatViewDate(item.Date);

            // Nothing to show for somebody who only read the message, and an empty block is
            // not the same as a missing one: the column is shared, so it has to keep its
            // place or the names above and below stop lining up.
            var reaction = content.Children[3] as TextBlock;
            reaction.Text = item.Reaction is ReactionTypeEmoji emoji ? emoji.Emoji : string.Empty;

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

        // Anonymous admins and channels react as a chat rather than as a person, so the
        // name and the tap have to cope with both.
        private string GetSenderName(MessageSender sender)
        {
            if (_protoService.TryGetUser(sender, out User user))
            {
                return user.GetFullName();
            }
            else if (_protoService.TryGetChat(sender, out Chat chat))
            {
                return _protoService.GetTitle(chat);
            }

            return string.Empty;
        }

        private void OnItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is MessageViewerItem item)
            {
                if (item.Sender is MessageSenderUser senderUser)
                {
                    Hide();
                    _delegate.OpenUser(senderUser.UserId);
                }
                else if (item.Sender is MessageSenderChat senderChat)
                {
                    Hide();
                    _delegate.OpenChat(senderChat.ChatId, true);
                }
            }
        }

        private void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
        }
    }

    /// <summary>
    /// One row: somebody who read the message, reacted to it, or both. The reaction is null
    /// for a reader, and the date is when they read it or when they reacted, whichever this
    /// row stands for.
    /// </summary>
    public class MessageViewerItem
    {
        public MessageViewerItem(MessageSender sender, int date, ReactionType reaction = null)
        {
            Sender = sender;
            Date = date;
            Reaction = reaction;
        }

        public MessageSender Sender { get; private set; }
        public int Date { get; private set; }
        public ReactionType Reaction { get; private set; }
    }
}
