using System.Collections.Generic;
using Telegram.Td.Api;

namespace Unigram.Services
{
    // Keeps shared sticker/event-log view models buildable against the legacy SDK.
    public static class ModernTdlibCompatibility
    {
        public static string GetWebPageTypeName(WebPage webPage)
        {
            return webPage?.Type;
        }

        public static TdlibParameters CreateTdlibParameters(bool useTestDc, string databaseDirectory, string filesDirectory, bool useFileDatabase, bool useChatInfoDatabase, bool useMessageDatabase, bool useSecretChats, int apiId, string apiHash, string systemLanguageCode, string deviceModel, string systemVersion, string applicationVersion, bool enableStorageOptimizer, bool ignoreFileNames)
        {
            return new TdlibParameters
            {
                UseTestDc = useTestDc,
                DatabaseDirectory = databaseDirectory,
                FilesDirectory = filesDirectory,
                UseFileDatabase = useFileDatabase,
                UseChatInfoDatabase = useChatInfoDatabase,
                UseMessageDatabase = useMessageDatabase,
                UseSecretChats = useSecretChats,
                ApiId = apiId,
                ApiHash = apiHash,
                SystemLanguageCode = systemLanguageCode,
                DeviceModel = deviceModel,
                SystemVersion = systemVersion,
                ApplicationVersion = applicationVersion,
                EnableStorageOptimizer = enableStorageOptimizer,
                IgnoreFileNames = ignoreFileNames
            };
        }

        public static Function CreateSetTdlibParameters(TdlibParameters parameters)
        {
            return new SetTdlibParameters(parameters);
        }

        public static Function CreateCheckDatabaseEncryptionKey(IList<byte> encryptionKey)
        {
            return new CheckDatabaseEncryptionKey(encryptionKey);
        }

        public static Function CreateGetWebPagePreview(FormattedText text)
        {
            return new GetWebPagePreview(text);
        }

        public static InputMessageContent CreateInputMessageText(FormattedText text, bool disableWebPagePreview, bool clearDraft)
        {
            return new InputMessageText(text, disableWebPagePreview, clearDraft);
        }

        public static Function CreateAddLocalMessage(long chatId, MessageSender senderId, long replyToMessageId, bool disableNotification, InputMessageContent inputMessageContent)
        {
            return new AddLocalMessage(chatId, senderId, replyToMessageId, disableNotification, inputMessageContent);
        }

        public static Function CreateSendMessage(long chatId, long replyToMessageId, MessageSendOptions options, InputMessageContent content)
        {
            return new SendMessage(chatId, 0, replyToMessageId, options, null, content);
        }

        public static Function CreateSendMessage(long chatId, long threadId, long replyToMessageId, MessageSendOptions options, InputMessageContent content)
        {
            return new SendMessage(chatId, threadId, replyToMessageId, options, null, content);
        }

        public static Function CreateSendMessageAlbum(long chatId, long threadId, long replyToMessageId, MessageSendOptions options, IList<InputMessageContent> contents)
        {
            return new SendMessageAlbum(chatId, threadId, replyToMessageId, options, contents);
        }

        public static Function CreateUploadFile(InputFile file, FileType type, int priority)
        {
            return new UploadFile(file, type, priority);
        }

        public static Function CreateCancelUploadFile(int fileId)
        {
            return new CancelUploadFile(fileId);
        }

        public static Function CreateResendMessages(long chatId, IList<long> messageIds)
        {
            return new ResendMessages(chatId, messageIds);
        }

        public static Function CreateGetMessageLink(long chatId, long messageId, int mediaTimestamp, int checklistTaskId, string pollOptionId, bool forAlbum, bool inMessageThread)
        {
            return new GetMessageLink(chatId, messageId, mediaTimestamp, forAlbum, inMessageThread);
        }

        public static Function CreateAddContact(Contact contact, bool sharePhoneNumber)
        {
            return new AddContact(contact, sharePhoneNumber);
        }

        public static Message CreateMessage(long id, MessageSender sender, long chatId, MessageSendingState sendingState, MessageSchedulingState schedulingState, bool isOutgoing, bool isChannelPost, int date, MessageContent content)
        {
            return new Message(id, sender, chatId, null, schedulingState, isOutgoing,
                false, false, false, false, false, false, false, false, false, false, false,
                isChannelPost, false, date, 0, null, null, 0, 0, 0, 0, 0, 0, string.Empty,
                0, string.Empty, content, null);
        }

        public static MessageContent CreateMessageSticker(Sticker sticker)
        {
            return new MessageSticker(sticker);
        }

        public static bool GetStickerSetIsMasks(StickerSet stickerSet)
        {
            return stickerSet?.IsMasks == true;
        }

        public static Function CreateGetChatMessageCount(long chatId, long threadId, SearchMessagesFilter filter, bool returnLocal)
        {
            return new GetChatMessageCount(chatId, filter, returnLocal);
        }

        public static DraftMessage CreateDraftMessage(long replyToMessageId, int date, FormattedText text)
        {
            return new DraftMessage(replyToMessageId, date, new InputMessageText(text, false, false));
        }

        public static Function CreateEditMessageCaption(long chatId, long messageId, ReplyMarkup replyMarkup, FormattedText caption)
        {
            return new EditMessageCaption(chatId, messageId, replyMarkup, caption);
        }

        public static Function CreateSetChatDraftMessage(long chatId, long threadId, DraftMessage draft)
        {
            return new SetChatDraftMessage(chatId, threadId, draft);
        }

        public static Function CreateReportChat(long chatId, IList<long> messageIds, ChatReportReason reason, string text)
        {
            return new ReportChat(chatId, messageIds, reason, text);
        }

        public static bool GetLoginUrlSkipConfirmation(LoginUrlInfoOpen info)
        {
            return info?.SkipConfirm == true;
        }

        public static bool GetSwitchInlineInCurrentChat(InlineKeyboardButtonTypeSwitchInline info)
        {
            return info?.InCurrentChat == true;
        }

        public static Function CreateSetLogStream(string path, int maxFileSize, bool redirectStderr)
        {
            return new SetLogStream(new LogStreamFile(path, maxFileSize, redirectStderr));
        }

        public static MessageSchedulingState CreateMessageSchedulingStateSendAtDate(int date)
        {
            return new MessageSchedulingStateSendAtDate(date);
        }

        public static Invoice GetPaymentFormInvoice(PaymentForm paymentForm)
        {
            return paymentForm?.Invoice;
        }

        public static OrderInfo GetPaymentFormSavedOrderInfo(PaymentForm paymentForm)
        {
            return paymentForm?.SavedOrderInfo;
        }

        public static bool HasPaymentFormSavedCredentials(PaymentForm paymentForm)
        {
            return paymentForm?.SavedCredentials != null;
        }

        public static bool GetPaymentFormCanSaveCredentials(PaymentForm paymentForm)
        {
            return paymentForm?.CanSaveCredentials == true;
        }

        public static void ClearPaymentFormSavedCredentials(PaymentForm paymentForm)
        {
            if (paymentForm != null)
            {
                paymentForm.SavedCredentials = null;
            }
        }

        public static string GetPaymentFormUrl(PaymentForm paymentForm)
        {
            return paymentForm?.Url;
        }

        public static Function CreateValidateOrderInfo(long chatId, long messageId, OrderInfo orderInfo, bool allowSave)
        {
            return new ValidateOrderInfo(chatId, messageId, orderInfo, allowSave);
        }

        public static Invoice GetPaymentReceiptInvoice(PaymentReceipt receipt)
        {
            return receipt?.Invoice;
        }

        public static OrderInfo GetPaymentReceiptOrderInfo(PaymentReceipt receipt)
        {
            return receipt?.OrderInfo;
        }

        public static ShippingOption GetPaymentReceiptShippingOption(PaymentReceipt receipt)
        {
            return receipt?.ShippingOption;
        }

        public static string GetPaymentReceiptCredentialsTitle(PaymentReceipt receipt)
        {
            return receipt?.CredentialsTitle;
        }

        public static long GetPaymentReceiptPaymentProviderUserId(PaymentReceipt receipt)
        {
            return receipt?.PaymentsProviderUserId ?? 0;
        }

        public static BaseObject GetMessageProperties(IProtoService protoService, Message message)
        {
            return null;
        }

        public static string GetUserRestrictionReason(User user)
        {
            return user?.RestrictionReason ?? string.Empty;
        }

        public static string GetSupergroupRestrictionReason(Supergroup supergroup)
        {
            return supergroup?.RestrictionReason ?? string.Empty;
        }

        public static FormattedText GetDraftMessageText(DraftMessage draft)
        {
            return (draft?.InputMessageText as InputMessageText)?.Text;
        }

        public static string GetUserFullInfoDescription(UserFullInfo fullInfo, bool isBot)
        {
            return (isBot ? fullInfo?.ShareText : fullInfo?.Bio) ?? string.Empty;
        }

        public static IList<BotCommand> GetUserFullInfoCommands(UserFullInfo fullInfo)
        {
            return fullInfo?.Commands ?? new List<BotCommand>();
        }

        public static int GetChatMessageTtlSetting(Chat chat)
        {
            return chat.MessageTtlSetting;
        }

        public static Function CreateSetChatMessageTtlSetting(long chatId, int ttl)
        {
            return new SetChatMessageTtlSetting(chatId, ttl);
        }

        public static long GetUpdateChatReplyMarkupMessageId(UpdateChatReplyMarkup update)
        {
            return update.ReplyMarkupMessageId;
        }

        public static bool GetMessageCanBeDeletedForAllUsers(Message message, BaseObject properties)
        {
            return message.CanBeDeletedForAllUsers;
        }

        public static bool GetMessageCanBeDeletedForAllUsers(IProtoService protoService, Message message)
        {
            return GetMessageCanBeDeletedForAllUsers(message, null);
        }

        public static bool GetMessageCanBeDeletedOnlyForSelf(Message message, BaseObject properties)
        {
            return message.CanBeDeletedOnlyForSelf;
        }

        public static bool GetMessageCanBeDeletedOnlyForSelf(IProtoService protoService, Message message)
        {
            return GetMessageCanBeDeletedOnlyForSelf(message, null);
        }

        public static bool GetMessageCanBeForwarded(Message message, BaseObject properties)
        {
            return message.CanBeForwarded;
        }

        public static bool GetMessageCanBeForwarded(IProtoService protoService, Message message)
        {
            return GetMessageCanBeForwarded(message, null);
        }

        public static bool GetMessageCanBeEdited(Message message, BaseObject properties)
        {
            return message.CanBeEdited;
        }

        public static bool GetMessageCanGetMessageThread(Message message, BaseObject properties)
        {
            return message.CanGetMessageThread;
        }

        public static bool GetMessageCanGetStatistics(Message message, BaseObject properties)
        {
            return message.CanGetStatistics;
        }

        public static double GetMessageTtlExpiresIn(Message message)
        {
            return message.TtlExpiresIn;
        }

        public static void SetMessageTtlExpiresIn(Message message, double value)
        {
            message.TtlExpiresIn = value;
        }

        public static int GetMessageTtl(Message message)
        {
            return message.Ttl;
        }

        public static long GetMessageReplyToMessageId(Message message)
        {
            return message.ReplyToMessageId;
        }

        public static long GetMessageReplyInChatId(Message message)
        {
            return message.ReplyInChatId;
        }

        public static long GetMessageThreadId(Message message)
        {
            return message.MessageThreadId;
        }

        public static long GetUpdateChatActionThreadId(UpdateChatAction update)
        {
            return update.MessageThreadId;
        }

        public static long GetDraftReplyToMessageId(DraftMessage draft)
        {
            return draft.ReplyToMessageId;
        }

        public static void UpdateMessageReplyTo(Message target, Message source)
        {
            target.ReplyToMessageId = source.ReplyToMessageId;
            target.ReplyInChatId = source.ReplyInChatId;
        }

        public static void SetMessageReplyToMessageId(Message message, long value)
        {
            message.ReplyToMessageId = value;
        }

        public static bool GetUserIsVerified(User user)
        {
            return user.IsVerified;
        }

        public static bool GetSupergroupIsVerified(Supergroup supergroup)
        {
            return supergroup.IsVerified;
        }

        public static bool GetStickerIsAnimated(Sticker sticker)
        {
            return sticker?.IsAnimated == true;
        }

        public static bool GetStickerSetIsAnimated(StickerSet stickerSet)
        {
            return stickerSet?.IsAnimated == true;
        }

        public static bool GetStickerSetInfoIsAnimated(StickerSetInfo stickerSet)
        {
            return stickerSet?.IsAnimated == true;
        }

        public static Function SetMessageSenderBlocked(MessageSender sender, bool blocked)
        {
            return new ToggleMessageSenderIsBlocked(sender, blocked);
        }

        public static Function CreateAddProxy(string server, int port, bool enabled, ProxyType type)
        {
            return new AddProxy(server, port, enabled, type);
        }

        public static MessageSendOptions CreateMessageSendOptions(bool disableNotification, bool fromBackground, MessageSchedulingState schedulingState)
        {
            return new MessageSendOptions(disableNotification, fromBackground, schedulingState);
        }

        public static MessageContent CreateMessageText(FormattedText text, WebPage webPage)
        {
            return new MessageText(text, webPage);
        }

        public static string GetPollQuestion(Poll poll)
        {
            return poll?.Question ?? string.Empty;
        }

        public static Function GetInstalledStickerSets(bool masks)
        {
            return new GetInstalledStickerSets(masks);
        }

        public static Function GetRecentStickers(bool masks)
        {
            return new GetRecentStickers(masks);
        }

        public static Function GetArchivedStickerSets(bool masks, long offset, int limit)
        {
            return new GetArchivedStickerSets(masks, offset, limit);
        }

        public static Function ReorderInstalledStickerSets(bool masks, IList<long> stickerSetIds)
        {
            return new ReorderInstalledStickerSets(masks, stickerSetIds);
        }

        public static Function GetTrendingStickerSets()
        {
            return new GetTrendingStickerSets(0, 24);
        }

        public static Function GetStickers(string query, int limit)
        {
            return new GetStickers(query, limit);
        }

        public static Function SearchStickers(string query, int limit)
        {
            return new SearchStickers(query, limit);
        }

        public static Function SearchStickerSet(string name)
        {
            return new SearchStickerSet(name);
        }

        public static Function CreateSearchChats(string query, int limit)
        {
            return new SearchChats(query, limit);
        }

        public static Function CreateSearchChatsOnServer(string query, int limit)
        {
            return new SearchChatsOnServer(query, limit);
        }

        public static Function CreateSearchPublicChats(string query)
        {
            return new SearchPublicChats(query);
        }

        public static Function CreateSearchMessages(ChatList chatList, string query, SearchMessagesFilter filter, int minDate, int maxDate)
        {
            return new SearchMessages(new long[0], query, int.MaxValue, 0, 0, 100, filter, minDate, maxDate);
        }

        public static Function CreateViewMessages(long chatId, long threadId, IList<long> messageIds, bool forceRead)
        {
            return new ViewMessages(chatId, threadId, messageIds, forceRead);
        }

        public static Function CreateNewBasicGroupChat(IList<long> userIds, string title, int messageAutoDeleteTime = 0)
        {
            return new CreateNewBasicGroupChat(userIds, title);
        }

        public static Function CreateNewSupergroupChat(string title, bool isChannel, string description, ChatLocation location, bool forImport)
        {
            return new CreateNewSupergroupChat(title, isChannel, description, location, forImport);
        }

        public static Function CreateImportContacts(string phoneNumber, string firstName, string lastName)
        {
            return new ImportContacts(new[] { new Contact(phoneNumber, firstName, lastName, string.Empty, 0) });
        }

        public static Function CreateChangeImportedContacts(IEnumerable<(string PhoneNumber, string FirstName, string LastName)> contacts)
        {
            var importedContacts = new List<Contact>();
            foreach (var contact in contacts)
            {
                importedContacts.Add(new Contact(contact.PhoneNumber, contact.FirstName, contact.LastName, string.Empty, 0));
            }

            return new ChangeImportedContacts(importedContacts);
        }

        public static IList<long> GetChatStatisticsMessageIds(ChatStatisticsChannel statistics)
        {
            var messageIds = new List<long>();
            foreach (var interaction in statistics.RecentMessageInteractions)
            {
                messageIds.Add(interaction.MessageId);
            }

            return messageIds;
        }

        public static bool TryGetChatStatisticsInteraction(ChatStatisticsChannel statistics, long messageId, out int forwardCount, out int viewCount)
        {
            foreach (var interaction in statistics.RecentMessageInteractions)
            {
                if (interaction.MessageId == messageId)
                {
                    forwardCount = interaction.ForwardCount;
                    viewCount = interaction.ViewCount;
                    return true;
                }
            }

            forwardCount = 0;
            viewCount = 0;
            return false;
        }

        public static Function CreateSearchEmojis(string query, string inputLanguage)
        {
            return new SearchEmojis(query, false, new[] { inputLanguage });
        }

        public static StickerSetInfo CreateStickerSetInfo(long id, string title, string name, Thumbnail thumbnail, IList<ClosedVectorPath> thumbnailOutline, bool isOwned, bool isInstalled, bool isArchived, bool isOfficial, bool isAnimated, bool isMasks, bool isViewed, int size, IList<Sticker> covers)
        {
            return new StickerSetInfo(id, title, name, thumbnail, thumbnailOutline, isInstalled, isArchived, isOfficial, isAnimated, isMasks, isViewed, size, covers);
        }

        public static ChatPermissions CreateChatPermissions(bool allowed)
        {
            return new ChatPermissions(allowed, allowed, allowed, allowed, allowed, allowed, allowed, allowed);
        }

        public static InputMessageContent CreateInputMessageDocument(InputFile file, InputThumbnail thumbnail, bool disableContentTypeDetection, FormattedText caption)
        {
            return new InputMessageDocument(file, thumbnail, disableContentTypeDetection, caption);
        }

        public static InputMessageContent CreateInputMessagePhoto(InputFile file, InputThumbnail thumbnail, int width, int height, FormattedText caption, int ttl)
        {
            return new InputMessagePhoto(file, thumbnail, new int[0], width, height, caption, ttl);
        }

        public static InputMessageContent CreateInputMessageAnimation(InputFile file, InputThumbnail thumbnail, int duration, int width, int height, FormattedText caption)
        {
            return new InputMessageAnimation(file, thumbnail, new int[0], duration, width, height, caption);
        }

        public static InputMessageContent CreateInputMessageVideo(InputFile file, InputThumbnail thumbnail, int duration, int width, int height, FormattedText caption, int ttl)
        {
            return new InputMessageVideo(file, thumbnail, new int[0], duration, width, height, true, caption, ttl);
        }

        public static InputMessageContent CreateInputMessageVideoNote(InputFile file, InputThumbnail thumbnail, int duration, int length)
        {
            return new InputMessageVideoNote(file, thumbnail, duration, length);
        }

        public static InputMessageContent CreateInputMessageSticker(InputFile file, InputThumbnail thumbnail, int width, int height, string emoji)
        {
            return new InputMessageSticker(file, thumbnail, width, height, emoji);
        }

        public static InputMessageContent CreateInputMessageAudio(InputFile file, InputThumbnail thumbnail, int duration, string title, string performer, FormattedText caption)
        {
            return new InputMessageAudio(file, thumbnail, duration, title, performer, caption);
        }

        public static InputMessageContent CreateInputMessageVoiceNote(InputFile file, int duration, FormattedText caption)
        {
            return new InputMessageVoiceNote(file, duration, new byte[0], caption);
        }

        public static InputMessageContent CreateInputMessagePoll(string question, IList<string> options, bool isAnonymous, PollType type, bool allowsMultipleAnswers)
        {
            return new InputMessagePoll(question, options, isAnonymous, type, 0, 0, false);
        }

        public static PollType CreatePollTypeQuiz(int correctOptionId, FormattedText explanation)
        {
            return new PollTypeQuiz(correctOptionId, explanation);
        }

        public static PollType CreatePollTypeRegular(bool allowsMultipleAnswers)
        {
            return new PollTypeRegular(allowsMultipleAnswers);
        }

        public static InputMessageContent CreateInputMessageLocation(Location location)
        {
            return new InputMessageLocation(location, 0, 0, 0);
        }

        public static MessageContent CreateMessagePhoto(Photo photo, FormattedText caption)
        {
            return new MessagePhoto(photo, caption, false);
        }

        public static bool GetAdministratorCanChangeInfo(ChatMemberStatusAdministrator administrator)
        {
            return administrator.CanChangeInfo;
        }

        public static bool GetAdministratorCanDeleteMessages(ChatMemberStatusAdministrator administrator)
        {
            return administrator.CanDeleteMessages;
        }

        public static bool GetAdministratorCanEditMessages(ChatMemberStatusAdministrator administrator)
        {
            return administrator.CanEditMessages;
        }

        public static bool GetAdministratorCanInviteUsers(ChatMemberStatusAdministrator administrator)
        {
            return administrator.CanInviteUsers;
        }

        public static bool GetAdministratorCanPinMessages(ChatMemberStatusAdministrator administrator)
        {
            return administrator.CanPinMessages;
        }

        public static bool GetAdministratorCanPostMessages(ChatMemberStatusAdministrator administrator)
        {
            return administrator.CanPostMessages;
        }

        public static bool GetAdministratorCanPromoteMembers(ChatMemberStatusAdministrator administrator)
        {
            return administrator.CanPromoteMembers;
        }

        public static bool GetAdministratorCanRestrictMembers(ChatMemberStatusAdministrator administrator)
        {
            return administrator.CanRestrictMembers;
        }

        public static bool GetAdministratorIsAnonymous(ChatMemberStatusAdministrator administrator)
        {
            return administrator.IsAnonymous;
        }

        public static string GetAdministratorCustomTitle(ChatMemberStatusAdministrator administrator)
        {
            return administrator.CustomTitle;
        }

        public static string GetAdministratorCustomTitle(ChatAdministrator administrator)
        {
            return administrator.CustomTitle;
        }

        public static string GetCreatorCustomTitle(ChatMemberStatusCreator creator)
        {
            return creator.CustomTitle;
        }

        public static ChatMemberStatus CreateAdministratorStatus(bool canBeEdited, bool canChangeInfo, bool canDeleteMessages, bool canEditMessages, bool canInviteUsers, bool canPinMessages, bool canPostMessages, bool canPromoteMembers, bool canRestrictMembers, bool isAnonymous, string customTitle)
        {
            return new ChatMemberStatusAdministrator
            {
                CanBeEdited = canBeEdited,
                IsAnonymous = isAnonymous,
                CanChangeInfo = canChangeInfo,
                CanDeleteMessages = canDeleteMessages,
                CanEditMessages = canEditMessages,
                CanInviteUsers = canInviteUsers,
                CanPinMessages = canPinMessages,
                CanPostMessages = canPostMessages,
                CanPromoteMembers = canPromoteMembers,
                CanRestrictMembers = canRestrictMembers,
                CustomTitle = customTitle ?? string.Empty
            };
        }
    }
}
