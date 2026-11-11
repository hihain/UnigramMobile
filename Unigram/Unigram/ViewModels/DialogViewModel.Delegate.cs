using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using Telegram.Td.Api;
using Unigram.Common;
using Unigram.Controls;
using Unigram.Controls.Gallery;
using Unigram.Services;
using Unigram.Services.Settings;
using Unigram.Services.Updates;
using Unigram.ViewModels.Chats;
using Unigram.ViewModels.Delegates;
using Unigram.ViewModels.Gallery;
using Unigram.Views;
using Unigram.Views.Popups;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;

namespace Unigram.ViewModels
{
    public partial class DialogViewModel : IMessageDelegate
    {
        private FileContext<MessageViewModel> _filesMap = new FileContext<MessageViewModel>();
        private FileContext<MessageViewModel> _photosMap = new FileContext<MessageViewModel>();

        public bool CanBeDownloaded(MessageViewModel message)
        {
            var content = message.Content as object;
            if (content is MessageAnimation animationMessage)
            {
                content = animationMessage.Animation;
            }
            else if (content is MessageAudio audioMessage)
            {
                content = audioMessage.Audio;
            }
            else if (content is MessageDocument documentMessage)
            {
                content = documentMessage.Document;
            }
            else if (content is MessageGame gameMessage)
            {
                if (gameMessage.Game.Animation != null)
                {
                    content = gameMessage.Game.Animation;
                }
                else if (gameMessage.Game.Photo != null)
                {
                    content = gameMessage.Game.Photo;
                }
            }
            else if (content is MessageInvoice invoiceMessage)
            {
                content = invoiceMessage.GetPhoto();
            }
            else if (content is MessageLocation locationMessage)
            {
                content = locationMessage.Location;
            }
            else if (content is MessagePhoto photoMessage)
            {
                content = photoMessage.Photo;
            }
            else if (content is MessageSticker stickerMessage)
            {
                content = stickerMessage.Sticker;
            }
            else if (content is MessageText textMessage)
            {
                if (textMessage?.WebPage?.Animation != null)
                {
                    content = textMessage?.WebPage?.Animation;
                }
                else if (textMessage?.WebPage?.Document != null)
                {
                    content = textMessage?.WebPage?.Document;
                }
                else if (textMessage?.WebPage?.Sticker != null)
                {
                    content = textMessage?.WebPage?.Sticker;
                }
                else if (textMessage?.WebPage?.Video != null)
                {
                    content = textMessage?.WebPage?.Video;
                }
                else if (textMessage?.WebPage?.VideoNote != null)
                {
                    content = textMessage?.WebPage?.VideoNote;
                }
                // PHOTO SHOULD ALWAYS BE AT THE END!
                else if (textMessage?.WebPage?.Photo != null)
                {
                    content = textMessage?.WebPage?.Photo;
                }
            }
            else if (content is MessageVideo videoMessage)
            {
                content = videoMessage.Video;
            }
            else if (content is MessageVideoNote videoNoteMessage)
            {
                content = videoNoteMessage.VideoNote;
            }
            else if (content is MessageVoiceNote voiceNoteMessage)
            {
                content = voiceNoteMessage.VoiceNote;
            }

            var file = message.GetFile();
            if (file != null && ProtoService.IsDownloadFileCanceled(file.Id))
            {
                return false;
            }

            var chat = _chat;
            if (chat == null)
            {
                return false;
            }

            if (content is Animation animation)
            {
                return Settings.AutoDownload.ShouldDownloadVideo(GetChatType(chat), (int)Math.Min(int.MaxValue, animation.AnimationValue.Size));
            }
            else if (content is Audio audio)
            {
                return Settings.AutoDownload.ShouldDownloadDocument(GetChatType(chat), (int)Math.Min(int.MaxValue, audio.AudioValue.Size));
            }
            else if (content is Document document)
            {
                return Settings.AutoDownload.ShouldDownloadDocument(GetChatType(chat), (int)Math.Min(int.MaxValue, document.DocumentValue.Size));
            }
            else if (content is Photo photo)
            {
                var big = photo.GetBig();
                if (big != null && ProtoService.IsDownloadFileCanceled(big.Photo.Id))
                {
                    return false;
                }

                return Settings.AutoDownload.ShouldDownloadPhoto(GetChatType(chat));
            }
            else if (content is Sticker sticker)
            {
                // Stickers aren't part of the deal
                return true;
            }
            else if (content is Video video)
            {
                return Settings.AutoDownload.ShouldDownloadVideo(GetChatType(chat), (int)Math.Min(int.MaxValue, video.VideoValue.Size));
            }
            else if (content is VideoNote videoNote)
            {
                return Settings.AutoDownload.ShouldDownloadVideo(GetChatType(chat), (int)Math.Min(int.MaxValue, videoNote.Video.Size));
            }
            else if (content is VoiceNote voiceNote)
            {
                return Settings.AutoDownload.ShouldDownloadVoiceMessages(GetChatType(chat));
            }

            return false;
        }

        private AutoDownloadChat GetChatType(Chat chat)
        {
            if (chat.Type is ChatTypeSupergroup supergroup && !supergroup.IsChannel || chat.Type is ChatTypeBasicGroup)
            {
                return AutoDownloadChat.Group;
            }
            else if (chat.Type is ChatTypePrivate || chat.Type is ChatTypeSecret)
            {
                var user = ProtoService.GetUser(chat);
                if (user == null)
                {
                    return AutoDownloadChat.OtherPrivateChat;
                }
                else if (user.IsContact)
                {
                    return AutoDownloadChat.Contact;
                }
            }

            return AutoDownloadChat.Channel;
        }

        public void DownloadFile(MessageViewModel message, File file)
        {
            ProtoService.DownloadFile(file.Id, 1);
        }

        public bool TryGetMessagesForFileId(int fileId, out IList<MessageViewModel> items)
        {
            if (_filesMap.TryGetValue(fileId, out List<MessageViewModel> messages))
            {
                items = messages;
                return true;
            }

            items = null;
            return false;
        }

        public bool TryGetMessagesForPhotoId(int fileId, out IList<MessageViewModel> items)
        {
            if (_photosMap.TryGetValue(fileId, out List<MessageViewModel> messages))
            {
                items = messages;
                return true;
            }

            items = null;
            return false;
        }



        public void ReplyToMessage(MessageViewModel message)
        {
            MessageReplyCommand.Execute(message);
        }

        public void DoubleTapMessage(MessageViewModel message)
        {
            if (message == null || message.Id == 0 || message.IsService())
            {
                return;
            }

            switch (Settings.DoubleTapAction)
            {
                case DoubleTapAction.Reply:
                    ReplyToMessage(message);
                    break;
#if MODERN_TDLIB
                case DoubleTapAction.React:
                    ToggleQuickReaction(message);
                    break;
#endif
            }
        }

#if MODERN_TDLIB
        private void ToggleQuickReaction(MessageViewModel message)
        {
            if (message.SendingState != null || message.SchedulingState != null)
            {
                return;
            }

            ToggleMessageReaction(message, ProtoService.DefaultReactionType ?? new ReactionTypeEmoji("\U0001F44D"), "double_tap");
        }

        public void ToggleMessageReaction(MessageViewModel message, ReactionType type, string source)
        {
            if (message == null || message.Id == 0 || type == null)
            {
                return;
            }

            var chosen = IsReactionChosen(message, type);

            Function function = chosen
                ? new RemoveMessageReaction(message.ChatId, message.Id, type)
                : (Function)new AddMessageReaction(message.ChatId, message.Id, type, false, true);

            ProtoService.Send(function, result =>
            {
                if (result is Error error)
                {
                    Logs.PushDiagnostics.Write(
                        "message.reaction",
                        $"result=failed;operation={source}_{(chosen ? "remove" : "add")};code={error.Code};message={Logs.PushDiagnostics.SanitizeErrorMessage(error.Message)}");
                }
            });
        }

        public static bool IsReactionChosen(MessageViewModel message, ReactionType type)
        {
            return message.InteractionInfo?.Reactions?.Reactions?.Any(x => x.IsChosen && AreSameReaction(x.Type, type)) == true;
        }

        private static bool AreSameReaction(ReactionType x, ReactionType y)
        {
            if (x is ReactionTypeEmoji xEmoji && y is ReactionTypeEmoji yEmoji)
            {
                return xEmoji.Emoji == yEmoji.Emoji;
            }
            else if (x is ReactionTypeCustomEmoji xCustom && y is ReactionTypeCustomEmoji yCustom)
            {
                return xCustom.CustomEmojiId == yCustom.CustomEmojiId;
            }
            else if (x is ReactionTypePaid && y is ReactionTypePaid)
            {
                return true;
            }

            return false;
        }
#endif

        public async void OpenReply(MessageViewModel message)
        {
            if (message.ReplyToMessageState == ReplyToMessageState.None)
            {
                if (message.ReplyInChatId == message.ChatId || message.ReplyInChatId == 0)
                {
                    await LoadMessageSliceAsync(message.Id, message.ReplyToMessageId);
                }
                else
                {
                    NavigationService.NavigateToChat(message.ReplyInChatId, message.ReplyToMessageId);
                }
            }
        }

        public async void OpenThread(MessageViewModel message)
        {
            var response = await ProtoService.SendAsync(new GetMessageThread(message.ChatId, message.Id));
            if (response is MessageThreadInfo)
            {
                NavigationService.NavigateToThread(message.ChatId, message.Id);
            }
        }



        public async void OpenFile(File file)
        {
            if (file.Local.IsDownloadingCompleted)
            {
                if (file.Local.Path.EndsWith(".unigram-theme"))
                {
                    await new ThemePreviewPopup(file.Local.Path).ShowQueuedAsync();
                    return;
                }

                var temp = await ProtoService.GetFileAsync(file);
                if (temp != null)
                {
                    await Windows.System.Launcher.LaunchFileAsync(temp);
                }
            }
        }

        public void OpenWebPage(WebPage webPage)
        {
            if (webPage.InstantViewVersion != 0)
            {
                //if (NavigationService is UnigramNavigationService asdas)
                //{
                //    asdas.NavigateToInstant(webPage.Url);
                //    return;
                //}

                NavigationService.NavigateToInstant(webPage.Url);
            }
            else if (MessageHelper.TryCreateUri(webPage.Url, out Uri uri) &&
                    (string.Equals(ModernTdlibCompatibility.GetWebPageTypeName(webPage), "telegram_megagroup", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(ModernTdlibCompatibility.GetWebPageTypeName(webPage), "telegram_channel", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(ModernTdlibCompatibility.GetWebPageTypeName(webPage), "telegram_message", StringComparison.OrdinalIgnoreCase)))
            {
                MessageHelper.OpenTelegramUrl(ProtoService, NavigationService, uri);
            }
        }

        public void OpenRichMessage(MessageViewModel message)
        {
            NavigationService.NavigateToInstant(RichMessageRenderer.GetNavigationParameter(message.ChatId, message.Id));
        }

        public async void OpenSticker(Sticker sticker)
        {
            if (sticker.SetId != 0)
            {
                await StickerSetPopup.GetForCurrentView().ShowAsync(sticker.SetId, Sticker_Click);
            }
        }

        public async void OpenLocation(Location location, string title)
        {
            var options = new Windows.System.LauncherOptions();
            options.FallbackUri = new Uri(string.Format(CultureInfo.InvariantCulture, "https://www.google.com/maps/search/?api=1&query={0},{1}", location.Latitude, location.Longitude));

            if (title != null)
            {
                await Windows.System.Launcher.LaunchUriAsync(new Uri(string.Format(CultureInfo.InvariantCulture, "bingmaps:?collection=point.{0}_{1}_{2}", location.Latitude, location.Longitude, WebUtility.UrlEncode(title))), options);
            }
            else
            {
                await Windows.System.Launcher.LaunchUriAsync(new Uri(string.Format(CultureInfo.InvariantCulture, "bingmaps:?collection=point.{0}_{1}", location.Latitude, location.Longitude)), options);
            }
        }

        public void OpenLiveLocation(MessageViewModel message)
        {
            NavigationService.Navigate(typeof(LiveLocationPage), message.ChatId);
        }

        public void OpenInlineButton(MessageViewModel message, InlineKeyboardButton button)
        {
            KeyboardButtonExecute(message, button);
        }

        public void Call(MessageViewModel message)
        {
            CallCommand.Execute();
        }

        public async void VotePoll(MessageViewModel message, IList<PollOption> options)
        {
            var poll = message.Content as MessagePoll;
            if (poll == null)
            {
                return;
            }

            var ids = options.Select(x => poll.Poll.Options.IndexOf(x)).ToArray();
            if (ids.IsEmpty())
            {
                return;
            }

            await ProtoService.SendAsync(new SetPollAnswer(message.ChatId, message.Id, ids));

            var updated = message.Content as MessagePoll;
            if (updated.Poll.Type is PollTypeQuiz quiz)
            {
                if (quiz.GetCorrectOptionId() == ids[0])
                {
                    Aggregator.Publish(new UpdateConfetti());
                }
                else
                {
                    var container = ListField?.ContainerFromItem(message) as SelectorItem;
                    var root = container?.ContentTemplateRoot as FrameworkElement;

                    var bubble = root?.FindName("Bubble") as FrameworkElement;
                    if (bubble == null)
                    {
                        return;
                    }

                    VisualUtilities.ShakeView(bubble);
                }
            }
        }


        public async void OpenUsername(string username)
        {
            var response = await ProtoService.SendAsync(new SearchPublicChat(username));
            if (response is Chat chat)
            {
                if (chat.Type is ChatTypePrivate privata)
                {
                    var user = ProtoService.GetUser(privata.UserId);
                    if (user?.Type is UserTypeBot)
                    {
                        NavigationService.NavigateToChat(chat);
                    }
                    else
                    {
                        NavigationService.Navigate(typeof(ProfilePage), chat.Id);
                    }
                }
                else
                {
                    NavigationService.NavigateToChat(chat);
                }
            }
            else
            {
                await MessagePopup.ShowAsync(Strings.Resources.NoUsernameFound, Strings.Resources.AppName, Strings.Resources.OK);
            }
        }

        public async void OpenUser(long userId)
        {
            var response = await ProtoService.SendAsync(new CreatePrivateChat(userId, false));
            if (response is Chat chat)
            {
                var user = ProtoService.GetUser(userId);
                if (user?.Type is UserTypeBot)
                {
                    NavigationService.NavigateToChat(chat);
                }
                else
                {
                    NavigationService.Navigate(typeof(ProfilePage), chat.Id);
                }
            }
        }

        public void OpenViaBot(long viaBotUserId)
        {
            var chat = Chat;
            if (chat != null && chat.Type is ChatTypeSupergroup super && super.IsChannel)
            {
                var supergroup = ProtoService.GetSupergroup(super.SupergroupId);
                if (supergroup != null && !supergroup.CanPostMessages())
                {
                    return;
                }
            }

            var user = ProtoService.GetUser(viaBotUserId);
            if (user != null)
            {
                SetText($"@{user.GetUsername()} ");
                ResolveInlineBot(user.GetUsername());
            }
        }

        public void OpenChat(long chatId, bool profile = false)
        {
            var chat = ProtoService.GetChat(chatId);
            if (chat == null)
            {
                return;
            }

            if (profile)
            {
                NavigationService.Navigate(typeof(ProfilePage), chat.Id);
            }
            else
            {
                NavigationService.NavigateToChat(chat);
            }
        }

        public void OpenChat(long chatId, long messageId)
        {
            var chat = ProtoService.GetChat(chatId);
            if (chat == null)
            {
                return;
            }

            NavigationService.NavigateToChat(chat, message: messageId);
        }

        public void OpenHashtag(string hashtag)
        {
            var search = Search = new ChatSearchViewModel(ProtoService, CacheService, Settings, Aggregator, this);
            search.Search(hashtag, null, null);
        }

        public async void OpenUrl(string url, bool untrust)
        {
            if (MessageHelper.TryCreateUri(url, out Uri uri))
            {
                if (MessageHelper.IsTelegramUrl(uri))
                {
                    MessageHelper.OpenTelegramUrl(ProtoService, NavigationService, uri);
                }
                else
                {
                    //if (message?.Media is TLMessageMediaWebPage webpageMedia)
                    //{
                    //    if (webpageMedia.WebPage is TLWebPage webpage && webpage.HasCachedPage && webpage.Url.Equals(navigation))
                    //    {
                    //        var service = WindowWrapper.Current().NavigationServices.GetByFrameId("Main");
                    //        if (service != null)
                    //        {
                    //            service.Navigate(typeof(InstantPage), webpageMedia);
                    //            return;
                    //        }
                    //    }
                    //}

                    if (untrust)
                    {
                        var confirm = await MessagePopup.ShowAsync(string.Format(Strings.Resources.OpenUrlAlert, url), Strings.Resources.AppName, Strings.Resources.OK, Strings.Resources.Cancel);
                        if (confirm != ContentDialogResult.Primary)
                        {
                            return;
                        }
                    }

                    try
                    {
                        await Windows.System.Launcher.LaunchUriAsync(uri);
                    }
                    catch { }
                }
            }
        }

        public void OpenBankCardNumber(string number)
        {
            //var response = await ProtoService.SendAsync(new GetBankCardInfo(number));
            //if (response is BankCardInfo info)
            //{
            //    var url = info.Actions.FirstOrDefault(x => x.)
            //}
        }

        public async void OpenMedia(MessageViewModel message, FrameworkElement target)
        {
            if (message.Content is MessagePoll poll)
            {
                await new PollResultsPopup(ProtoService, CacheService, Settings, Aggregator, this, message.ChatId, message.Id, poll.Poll).ShowQueuedAsync();
                return;
            }
            else if (message.Content is MessageGame game && message.ReplyMarkup is ReplyMarkupInlineKeyboard inline)
            {
                foreach (var row in inline.Rows)
                foreach (var button in row)
                {
                    if (button.Type is InlineKeyboardButtonTypeCallbackGame)
                    {
                        KeyboardButtonExecute(message, button);
                        return;
                    }
                }
            }

            GalleryViewModelBase viewModel = null;

            var webPage = message.Content is MessageText text ? text.WebPage : null;
            if (webPage != null && webPage.IsInstantGallery())
            {
                viewModel = await InstantGalleryViewModel.CreateAsync(ProtoService, Aggregator, message, webPage);

                if (viewModel.Items.IsEmpty())
                {
                    viewModel = null;
                }
            }

            if (viewModel == null && (message.Content is MessageVideoNote || (webPage != null && webPage.VideoNote != null) || message.Content is MessageAnimation || (webPage != null && webPage.Animation != null)))
            {
                Delegate?.PlayMessage(message, target);
            }
            else
            {
                if (viewModel == null)
                {
                    if ((message.Content is MessageAnimation || message.Content is MessagePhoto || message.Content is MessageVideo) && !message.IsSecret())
                    {
                        viewModel = new ChatGalleryViewModel(ProtoService, Aggregator, message.ChatId, _threadId, message.Get());
                    }
                    else
                    {
                        viewModel = new SingleGalleryViewModel(ProtoService, Aggregator, new GalleryMessage(ProtoService, message.Get()));
                    }
                }

                await GalleryView.GetForCurrentView().ShowAsync(viewModel, () => target);
            }

            TextField?.Focus(FocusState.Programmatic);
        }

        public void PlayMessage(MessageViewModel message)
        {
            if ((message.Content is MessageVideoNote videoNote && !videoNote.IsViewed && !message.IsOutgoing) || (message.Content is MessageVoiceNote voiceNote && !voiceNote.IsListened && !message.IsOutgoing))
            {
                ProtoService.Send(new OpenMessageContent(message.ChatId, message.Id));
            }

            _playbackService.Enqueue(message.Get());
        }



        public async void SendBotCommand(string command)
        {
            await SendMessageAsync(command);
        }



        public string GetAdminTitle(MessageViewModel message)
        {
            if (message.SenderId is MessageSenderUser senderUser)
            {
                return GetAdminTitle(senderUser.UserId);
            }
            else if (message.SenderId is MessageSenderChat && !message.IsChannelPost)
            {
                return message.AuthorSignature.Length > 0 ? message.AuthorSignature : null;
            }

            return null;
        }

        public string GetAdminTitle(long userId)
        {
            var chat = _chat;
            if (chat == null)
            {
                return null;
            }

            if (_admins.TryGetValue(chat.Id, out IList<ChatAdministrator> value))
            {
                var admin = value.FirstOrDefault(x => x.UserId == userId);
                if (admin != null)
                {
                    if (string.IsNullOrEmpty(ModernTdlibCompatibility.GetAdministratorCustomTitle(admin)))
                    {
                        if (admin.IsOwner)
                        {
                            return Strings.Resources.ChannelCreator;
                        }

                        return Strings.Resources.ChannelAdmin;
                    }

                    return ModernTdlibCompatibility.GetAdministratorCustomTitle(admin);
                }
            }

            return null;
        }
    }
}
