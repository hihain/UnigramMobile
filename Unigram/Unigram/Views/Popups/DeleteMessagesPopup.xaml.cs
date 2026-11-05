using System.Collections.Generic;
using System.Linq;
using Telegram.Td.Api;
using Unigram.Common;
using Unigram.Controls;
using Unigram.Services;
using Windows.UI.Xaml;

namespace Unigram.Views.Popups
{
    public sealed partial class DeleteMessagesPopup : ContentPopup
    {
        public DeleteMessagesPopup(IProtoService protoService, IList<Message> messages, IDictionary<long, BaseObject> properties)
        {
            BaseObject GetProperties(Message message)
            {
                return properties != null && properties.TryGetValue(message.Id, out var value) ? value : null;
            }

            this.InitializeComponent();

            Title = messages.Count == 1 ? Strings.Resources.DeleteSingleMessagesTitle : string.Format(Strings.Resources.DeleteMessagesTitle, Locale.Declension("messages", messages.Count));
            PrimaryButtonText = Strings.Resources.OK;
            SecondaryButtonText = Strings.Resources.Cancel;

            var first = messages.FirstOrDefault();
            if (first == null)
            {
                return;
            }

            var chat = protoService.GetChat(first.ChatId);
            if (chat == null)
            {
                return;
            }

            var user = protoService.GetUser(chat);

            var sameUser = messages.All(x => x.SenderId.IsEqual(first.SenderId));
            var supergroup = chat.Type as ChatTypeSupergroup;
            if (sameUser && !first.IsOutgoing && supergroup != null && !supergroup.IsChannel)
            {
                RevokeCheck.Visibility = Visibility.Collapsed;
                BanUserCheck.Visibility = Visibility.Visible;
                ReportSpamCheck.Visibility = Visibility.Visible;
                DeleteAllCheck.Visibility = Visibility.Visible;

                var sender = protoService.GetMessageSender(first.SenderId);
                if (sender is User senderUser)
                {
                    DeleteAllCheck.Content = string.Format(Strings.Resources.DeleteAllFrom, senderUser.GetFullName());
                }
                else if (sender is Chat senderChat)
                {
                    DeleteAllCheck.Content = string.Format(Strings.Resources.DeleteAllFrom, senderChat.Title);
                }

                TextBlockHelper.SetMarkdown(Message, messages.Count == 1
                    ? Strings.Resources.AreYouSureDeleteSingleMessage
                    : Strings.Resources.AreYouSureDeleteFewMessages);
            }
            else
            {
                RevokeCheck.Visibility = Visibility.Collapsed;
                BanUserCheck.Visibility = Visibility.Collapsed;
                ReportSpamCheck.Visibility = Visibility.Collapsed;
                DeleteAllCheck.Visibility = Visibility.Collapsed;

                var canBeDeletedForAllUsers = messages.All(x => ModernTdlibCompatibility.GetMessageCanBeDeletedForAllUsers(x, GetProperties(x)));
                var canBeDeletedOnlyForSelf = messages.All(x => ModernTdlibCompatibility.GetMessageCanBeDeletedOnlyForSelf(x, GetProperties(x)));
                var anyCanBeDeletedForAllUsers = messages.Any(x => x.IsOutgoing && ModernTdlibCompatibility.GetMessageCanBeDeletedForAllUsers(x, GetProperties(x)));

                if (chat.Type is ChatTypePrivate || chat.Type is ChatTypeBasicGroup)
                {
                    if (anyCanBeDeletedForAllUsers && !canBeDeletedForAllUsers)
                    {
                        TextBlockHelper.SetMarkdown(Message, chat.Type is ChatTypePrivate && user != null
                            ? string.Format(Strings.Resources.DeleteMessagesText, Locale.Declension("messages", messages.Count), user.FirstName)
                            : string.Format(Strings.Resources.DeleteMessagesTextGroup, Locale.Declension("messages", messages.Count)));

                        RevokeCheck.IsChecked = true;
                        RevokeCheck.Visibility = Visibility.Visible;
                        RevokeCheck.Content = Strings.Resources.DeleteMessagesOption;
                    }
                    else
                    {
                        TextBlockHelper.SetMarkdown(Message, messages.Count == 1
                            ? Strings.Resources.AreYouSureDeleteSingleMessage
                            : Strings.Resources.AreYouSureDeleteFewMessages);

                        if (canBeDeletedForAllUsers)
                        {
                            RevokeCheck.IsChecked = true;
                            RevokeCheck.Visibility = Visibility.Visible;
                            RevokeCheck.Content = chat.Type is ChatTypePrivate && user != null
                                ? string.Format(Strings.Resources.DeleteMessagesOptionAlso, user.FirstName)
                                : Strings.Resources.DeleteForAll;
                        }
                    }
                }
                else if (chat.Type is ChatTypeSupergroup super && !super.IsChannel)
                {
                    TextBlockHelper.SetMarkdown(Message, messages.Count == 1
                        ? Strings.Resources.AreYouSureDeleteSingleMessageMega
                        : Strings.Resources.AreYouSureDeleteFewMessagesMega);
                }
                else
                {
                    TextBlockHelper.SetMarkdown(Message, messages.Count == 1
                        ? Strings.Resources.AreYouSureDeleteSingleMessage
                        : Strings.Resources.AreYouSureDeleteFewMessages);
                }
            }
        }

        public bool Revoke
        {
            get { return RevokeCheck.IsChecked ?? false; }
            set { RevokeCheck.IsChecked = value; }
        }

        public bool BanUser
        {
            get { return BanUserCheck.IsChecked ?? false; }
            set { BanUserCheck.IsChecked = value; }
        }

        public bool ReportSpam
        {
            get { return ReportSpamCheck.IsChecked ?? false; }
            set { ReportSpamCheck.IsChecked = value; }
        }

        public bool DeleteAll
        {
            get { return DeleteAllCheck.IsChecked ?? false; }
            set { DeleteAllCheck.IsChecked = value; }
        }
    }
}
