using System;
using System.Collections.Generic;
using System.Linq;
using Telegram.Td.Api;
using Unigram.Common;
using Unigram.Logs;
using Unigram.Services.ViewService;
using Unigram.ViewModels;

namespace Unigram.Services
{
    public sealed class TdlibParameters
    {
        public TdlibParameters(bool useTestDc, string databaseDirectory, string filesDirectory, bool useFileDatabase, bool useChatInfoDatabase, bool useMessageDatabase, bool useSecretChats, int apiId, string apiHash, string systemLanguageCode, string deviceModel, string systemVersion, string applicationVersion, bool enableStorageOptimizer, bool ignoreFileNames)
        {
            UseTestDc = useTestDc;
            DatabaseDirectory = databaseDirectory;
            FilesDirectory = filesDirectory;
            UseFileDatabase = useFileDatabase;
            UseChatInfoDatabase = useChatInfoDatabase;
            UseMessageDatabase = useMessageDatabase;
            UseSecretChats = useSecretChats;
            ApiId = apiId;
            ApiHash = apiHash;
            SystemLanguageCode = systemLanguageCode;
            DeviceModel = deviceModel;
            SystemVersion = systemVersion;
            ApplicationVersion = applicationVersion;
        }

        public bool UseTestDc { get; }
        public string DatabaseDirectory { get; }
        public string FilesDirectory { get; set; }
        public bool UseFileDatabase { get; }
        public bool UseChatInfoDatabase { get; }
        public bool UseMessageDatabase { get; }
        public bool UseSecretChats { get; }
        public int ApiId { get; }
        public string ApiHash { get; }
        public string SystemLanguageCode { get; }
        public string DeviceModel { get; }
        public string SystemVersion { get; }
        public string ApplicationVersion { get; }
    }

    public static class ModernTdlibCompatibility
    {
        private const int LegacyVoipMinimumLayer = 65;
        private const int AudioCallDiagnosticBudget = 24;
        private static int _audioCallDiagnosticBudget = AudioCallDiagnosticBudget;
#if MODERN_TGCALLS
        // Version negotiation gets its own budget. It is one line per call and the
        // single most important thing in the log when a call connects but carries no
        // audio, so it must not be starved by the shared call diagnostics.
        private const int AudioCallVersionDiagnosticBudget = 24;
        private static int _audioCallVersionDiagnosticBudget = AudioCallVersionDiagnosticBudget;
#endif

        /// <summary>
        /// Replenishes both budgets for a new call. They were previously set once per process,
        /// so after roughly two dozen calls the negotiated-version line — the only place the
        /// agreed protocol is recorded — went permanently silent for the rest of the launch.
        /// </summary>
        public static void ResetAudioCallDiagnosticBudgets()
        {
            System.Threading.Interlocked.Exchange(ref _audioCallDiagnosticBudget, AudioCallDiagnosticBudget);
#if MODERN_TGCALLS
            System.Threading.Interlocked.Exchange(ref _audioCallVersionDiagnosticBudget, AudioCallVersionDiagnosticBudget);
#endif
        }

#if MODERN_TGCALLS
        // These are the registered versions from the pinned TgCalls source used by
        // the ARM bridge. Keeping the audited source-pin list in managed code
        // avoids loading WebRTC merely to construct a TDLib call request; the
        // bridge is activated only after TDLib provides the ready-state server
        // configuration.
        //
        // This matches upstream Unigram's registration of InstanceImpl,
        // InstanceV2Impl, and InstanceV2ReferenceImpl. Order is part of the contract:
        // the server reads the offer newest first and reports its choice back as
        // LibraryVersions[0]. Both peers must run that exact selected version.
        private static readonly string[] ModernTgCallsVersions =
        {
            "13.0.0", "12.0.0", "11.0.0", "10.0.0",
            "9.0.0", "8.0.0", "7.0.0", "5.0.0", "2.7.7"
        };
#endif

        public static MessageTopic GetMessageTopic(long threadId)
        {
            return threadId == 0 ? null : new MessageTopicThread(threadId);
        }

        public static SearchChatMessages CreateSearchChatMessages(long chatId, MessageTopic topic, string query, MessageSender sender, long fromMessageId, int offset, int limit, SearchMessagesFilter filter)
        {
            // Avoid the generated multi-argument activation factory: it fails to marshal
            // optional WinRT interface arguments reliably in the .NET Native ARM build.
            var request = new SearchChatMessages
            {
                ChatId = chatId,
                Query = query ?? string.Empty,
                FromMessageId = fromMessageId,
                Offset = offset,
                Limit = limit,
                Filter = filter ?? new SearchMessagesFilterEmpty()
            };

            if (topic != null)
            {
                request.TopicId = topic;
            }

            if (sender != null)
            {
                request.SenderId = sender;
            }

            return request;
        }

        public static bool TryGetSearchChatMessages(
            BaseObject response,
            out int totalCount,
            out IList<Message> messages,
            out long nextFromMessageId)
        {
#if MODERN_TDLIB
            if (response is FoundChatMessages found)
            {
                totalCount = found.TotalCount;
                messages = found.Messages;
                nextFromMessageId = found.NextFromMessageId;
                return true;
            }
#endif
            if (response is Messages legacy)
            {
                totalCount = legacy.TotalCount;
                messages = legacy.MessagesValue;
                nextFromMessageId = messages.Count > 0 ? messages.Min(message => message.Id) : 0;
                return true;
            }

            totalCount = 0;
            messages = null;
            nextFromMessageId = 0;
            return false;
        }

        public static string GetWebPageTypeName(WebPage webPage)
        {
            if (webPage?.Type == null)
            {
                return null;
            }

            if (webPage.Type is LinkPreviewTypePhoto)
            {
                return "photo";
            }
            if (webPage.Type is LinkPreviewTypeEmbeddedVideoPlayer || webPage.Type is LinkPreviewTypeExternalVideo)
            {
                return "video";
            }
            if (webPage.Type is LinkPreviewTypeArticle)
            {
                return "article";
            }
            if (webPage.Type is LinkPreviewTypeBackground)
            {
                return "telegram_background";
            }
            if (webPage.Type is LinkPreviewTypeMessage)
            {
                return "telegram_message";
            }
            if (webPage.Type is LinkPreviewTypeChat chat)
            {
                if (chat.Type is InviteLinkChatTypeChannel)
                {
                    return "telegram_channel";
                }
                if (chat.Type is InviteLinkChatTypeSupergroup)
                {
                    return "telegram_megagroup";
                }
            }

            return null;
        }

        public static TdlibParameters CreateTdlibParameters(bool useTestDc, string databaseDirectory, string filesDirectory, bool useFileDatabase, bool useChatInfoDatabase, bool useMessageDatabase, bool useSecretChats, int apiId, string apiHash, string systemLanguageCode, string deviceModel, string systemVersion, string applicationVersion, bool enableStorageOptimizer, bool ignoreFileNames)
        {
            return new TdlibParameters(useTestDc, databaseDirectory, filesDirectory, useFileDatabase, useChatInfoDatabase, useMessageDatabase, useSecretChats, apiId, apiHash, systemLanguageCode, deviceModel, systemVersion, applicationVersion, enableStorageOptimizer, ignoreFileNames);
        }

        public static Function CreateSetTdlibParameters(TdlibParameters parameters)
        {
            if (parameters == null)
            {
                throw new ArgumentNullException(nameof(parameters));
            }

            // The generated 1.8.66 activation factory takes all fourteen fields at once.
            // Calling it that way made .NET Native marshal every argument in a single ABI
            // call, and that call threw ArgumentNullException before reaching native code.
            // Note that DatabaseEncryptionKey is not a contiguous buffer here: CxCli.h
            // defines Array as IVector, so the field projects to IList<byte> and crosses
            // the ABI element by element. The parameterless activation plus property
            // setters marshals one value per call, which is how the previously shipping
            // build populated this request.
            var request = new SetTdlibParameters();

            request.UseTestDc = parameters.UseTestDc;
            request.DatabaseDirectory = parameters.DatabaseDirectory ?? string.Empty;
            request.FilesDirectory = parameters.FilesDirectory ?? string.Empty;
            request.UseFileDatabase = parameters.UseFileDatabase;
            request.UseChatInfoDatabase = parameters.UseChatInfoDatabase;
            request.UseMessageDatabase = parameters.UseMessageDatabase;
            request.UseSecretChats = parameters.UseSecretChats;
            request.ApiId = parameters.ApiId;
            request.ApiHash = parameters.ApiHash ?? string.Empty;
            request.SystemLanguageCode = parameters.SystemLanguageCode ?? string.Empty;
            request.DeviceModel = parameters.DeviceModel ?? string.Empty;
            request.SystemVersion = parameters.SystemVersion ?? string.Empty;
            request.ApplicationVersion = parameters.ApplicationVersion ?? string.Empty;

            // DatabaseEncryptionKey is deliberately left unset. This build stores the
            // database unencrypted, and TDLib's own ToUnmanaged(Array<BYTE>^) maps a null
            // vector to an empty key, which TdDb then resolves to the same default key a
            // zero-length vector produced, so an existing database still opens.

            return request;
        }

        public static Function CreateGetWebPagePreview(FormattedText text)
        {
            return new GetLinkPreview(text, new LinkPreviewOptions(false, string.Empty, false, false, false));
        }

        public static InputMessageContent CreateInputMessageText(FormattedText text, bool disableWebPagePreview, bool clearDraft)
        {
            return new InputMessageText(text, new LinkPreviewOptions(disableWebPagePreview, string.Empty, false, false, false), clearDraft);
        }

        public static Function CreateAddLocalMessage(long chatId, MessageSender senderId, long replyToMessageId, bool disableNotification, InputMessageContent inputMessageContent)
        {
            return new AddLocalMessage(chatId, senderId, replyToMessageId == 0 ? null : new InputMessageReplyToMessage(replyToMessageId, null, 0, string.Empty), disableNotification, inputMessageContent);
        }

        public static Function CreateSendMessage(long chatId, long replyToMessageId, MessageSendOptions options, InputMessageContent content)
        {
            return new SendMessage(chatId, null, replyToMessageId == 0 ? null : new InputMessageReplyToMessage(replyToMessageId, null, 0, string.Empty), options, null, content);
        }

        public static Function CreateSendMessage(long chatId, long threadId, long replyToMessageId, MessageSendOptions options, InputMessageContent content)
        {
            return new SendMessage(chatId, GetMessageTopic(threadId), replyToMessageId == 0 ? null : new InputMessageReplyToMessage(replyToMessageId, null, 0, string.Empty), options, null, content);
        }

        public static Function CreateSendMessageAlbum(long chatId, long threadId, long replyToMessageId, MessageSendOptions options, IList<InputMessageContent> contents)
        {
            return new SendMessageAlbum(chatId, GetMessageTopic(threadId), replyToMessageId == 0 ? null : new InputMessageReplyToMessage(replyToMessageId, null, 0, string.Empty), options, contents);
        }

        public static Function CreateUploadFile(InputFile file, FileType type, int priority)
        {
            return new PreliminaryUploadFile(file, type, priority);
        }

        public static Function CreateCancelUploadFile(int fileId)
        {
            return new CancelPreliminaryUploadFile(fileId);
        }

        public static Function CreateResendMessages(long chatId, IList<long> messageIds)
        {
            return new ResendMessages(chatId, messageIds, null, 0);
        }

        public static Function CreateGetMessageLink(long chatId, long messageId, int mediaTimestamp, int checklistTaskId, string pollOptionId, bool forAlbum, bool inMessageThread)
        {
            return new GetMessageLink(chatId, messageId, mediaTimestamp, checklistTaskId, pollOptionId, forAlbum, inMessageThread);
        }

        public static Function CreateAddContact(long userId, Telegram.Td.Api.ImportedContact contact)
        {
            return new AddContact(userId, contact, false);
        }

        public static Function CreateAddContact(Contact contact, bool sharePhoneNumber)
        {
            return new AddContact(contact.UserId,
                new Telegram.Td.Api.ImportedContact(contact.PhoneNumber, contact.FirstName, contact.LastName, new FormattedText(contact.Vcard, new TextEntity[0])),
                sharePhoneNumber);
        }

        public static Function CreateGetChatMessageCount(long chatId, long threadId, SearchMessagesFilter filter, bool returnLocal)
        {
            return new GetChatMessageCount(chatId, GetMessageTopic(threadId), filter, returnLocal);
        }

        public static DraftMessage CreateDraftMessage(long replyToMessageId, int date, FormattedText text)
        {
            return new DraftMessage(replyToMessageId == 0 ? null : new InputMessageReplyToMessage(replyToMessageId, null, 0, string.Empty),
                date, new DraftMessageContentText(text, new LinkPreviewOptions(false, string.Empty, false, false, false)), 0, null);
        }

        public static Function CreateSetChatDraftMessage(long chatId, long threadId, DraftMessage draft)
        {
            return new SetChatDraftMessage(chatId, GetMessageTopic(threadId), draft);
        }

        public static Function CreateEditMessageCaption(long chatId, long messageId, ReplyMarkup replyMarkup, FormattedText caption)
        {
            return new EditMessageCaption(chatId, messageId, replyMarkup, caption, false);
        }

        public static bool GetLoginUrlSkipConfirmation(LoginUrlInfoOpen info)
        {
            return info?.SkipConfirmation == true;
        }

        public static bool GetSwitchInlineInCurrentChat(InlineKeyboardButtonTypeSwitchInline info)
        {
            return info?.TargetChat is TargetChatCurrent;
        }

        public static Function CreateSetLogStream(string path, int maxFileSize, bool redirectStderr)
        {
            return new SetLogStream(new LogStreamFile(path, maxFileSize, redirectStderr));
        }

        public static Invoice GetPaymentFormInvoice(PaymentForm paymentForm)
        {
            return (paymentForm?.Type as PaymentFormTypeRegular)?.Invoice;
        }

        public static OrderInfo GetPaymentFormSavedOrderInfo(PaymentForm paymentForm)
        {
            return (paymentForm?.Type as PaymentFormTypeRegular)?.SavedOrderInfo;
        }

        public static bool HasPaymentFormSavedCredentials(PaymentForm paymentForm)
        {
            return (paymentForm?.Type as PaymentFormTypeRegular)?.SavedCredentials?.Count > 0;
        }

        public static bool GetPaymentFormCanSaveCredentials(PaymentForm paymentForm)
        {
            return (paymentForm?.Type as PaymentFormTypeRegular)?.CanSaveCredentials == true;
        }

        public static void ClearPaymentFormSavedCredentials(PaymentForm paymentForm)
        {
            var regular = paymentForm?.Type as PaymentFormTypeRegular;
            if (regular != null)
            {
                regular.SavedCredentials = new List<SavedCredentials>();
            }
        }

        public static string GetPaymentFormUrl(PaymentForm paymentForm)
        {
            var regular = paymentForm?.Type as PaymentFormTypeRegular;
            return (regular?.PaymentProvider as PaymentProviderOther)?.Url;
        }

        public static Function CreateValidateOrderInfo(long chatId, long messageId, OrderInfo orderInfo, bool allowSave)
        {
            return new ValidateOrderInfo(new InputInvoiceMessage(chatId, messageId), orderInfo, allowSave);
        }

        public static Invoice GetPaymentReceiptInvoice(PaymentReceipt receipt)
        {
            return (receipt?.Type as PaymentReceiptTypeRegular)?.Invoice;
        }

        public static OrderInfo GetPaymentReceiptOrderInfo(PaymentReceipt receipt)
        {
            return (receipt?.Type as PaymentReceiptTypeRegular)?.OrderInfo;
        }

        public static ShippingOption GetPaymentReceiptShippingOption(PaymentReceipt receipt)
        {
            return (receipt?.Type as PaymentReceiptTypeRegular)?.ShippingOption;
        }

        public static string GetPaymentReceiptCredentialsTitle(PaymentReceipt receipt)
        {
            return (receipt?.Type as PaymentReceiptTypeRegular)?.CredentialsTitle;
        }

        public static long GetPaymentReceiptPaymentProviderUserId(PaymentReceipt receipt)
        {
            return (receipt?.Type as PaymentReceiptTypeRegular)?.PaymentProviderUserId ?? 0;
        }

        public static BaseObject GetMessageProperties(IProtoService protoService, Message message)
        {
            return protoService.Execute(new Telegram.Td.Api.GetMessageProperties(message.ChatId, message.Id));
        }

        public static string GetUserRestrictionReason(User user)
        {
            return user?.RestrictionInfo?.RestrictionReason ?? string.Empty;
        }

        public static string GetSupergroupRestrictionReason(Supergroup supergroup)
        {
            return supergroup?.RestrictionInfo?.RestrictionReason ?? string.Empty;
        }

        public static FormattedText GetDraftMessageText(DraftMessage draft)
        {
            return (draft?.Content as DraftMessageContentText)?.Text;
        }

        public static string GetUserFullInfoDescription(UserFullInfo fullInfo, bool isBot)
        {
            return (isBot ? fullInfo?.BotInfo?.Description : fullInfo?.Bio?.Text) ?? string.Empty;
        }

        public static IList<BotCommand> GetUserFullInfoCommands(UserFullInfo fullInfo)
        {
            return fullInfo?.BotInfo?.Commands ?? new List<BotCommand>();
        }

        public static int GetChatMessageTtlSetting(Chat chat)
        {
            return chat.MessageAutoDeleteTime;
        }

        public static Function CreateSetChatMessageTtlSetting(long chatId, int ttl)
        {
            return new SetChatMessageAutoDeleteTime(chatId, ttl);
        }

        public static long GetUpdateChatReplyMarkupMessageId(UpdateChatReplyMarkup update)
        {
            return update.ReplyMarkupMessage?.Id ?? 0;
        }

        public static bool GetMessageCanBeDeletedForAllUsers(Message message, BaseObject properties)
        {
            return (properties as MessageProperties)?.CanBeDeletedForAllUsers == true;
        }

        public static bool GetMessageCanBeDeletedForAllUsers(IProtoService protoService, Message message)
        {
            return GetMessageCanBeDeletedForAllUsers(message, GetMessageProperties(protoService, message));
        }

        public static bool GetMessageCanBeDeletedOnlyForSelf(Message message, BaseObject properties)
        {
            return (properties as MessageProperties)?.CanBeDeletedOnlyForSelf == true;
        }

        public static bool GetMessageCanBeDeletedOnlyForSelf(IProtoService protoService, Message message)
        {
            return GetMessageCanBeDeletedOnlyForSelf(message, GetMessageProperties(protoService, message));
        }

        public static bool GetMessageCanBeForwarded(Message message, BaseObject properties)
        {
            return (properties as MessageProperties)?.CanBeForwarded == true;
        }

        public static bool GetMessageCanBeForwarded(IProtoService protoService, Message message)
        {
            return GetMessageCanBeForwarded(message, GetMessageProperties(protoService, message));
        }

        public static bool GetMessageCanBeEdited(Message message, BaseObject properties)
        {
            return (properties as MessageProperties)?.CanBeEdited == true;
        }

        public static bool GetMessageCanGetMessageThread(Message message, BaseObject properties)
        {
            return (properties as MessageProperties)?.CanGetMessageThread == true;
        }

        public static bool GetMessageCanGetStatistics(Message message, BaseObject properties)
        {
            return (properties as MessageProperties)?.CanGetStatistics == true;
        }

        public static double GetMessageTtlExpiresIn(Message message)
        {
            return message.SelfDestructIn;
        }

        public static void SetMessageTtlExpiresIn(Message message, double value)
        {
            message.SelfDestructIn = value;
        }

        public static int GetMessageTtl(Message message)
        {
            return (message.SelfDestructType as MessageSelfDestructTypeTimer)?.SelfDestructTime ?? 0;
        }

        public static long GetMessageReplyToMessageId(Message message)
        {
            return (message.ReplyTo as MessageReplyToMessage)?.MessageId ?? 0;
        }

        public static long GetMessageReplyInChatId(Message message)
        {
            return (message.ReplyTo as MessageReplyToMessage)?.ChatId ?? 0;
        }

        public static long GetMessageThreadId(Message message)
        {
            return GetMessageThreadId(message.TopicId);
        }

        public static long GetMessageThreadId(MessageTopic topic)
        {
            if (topic is MessageTopicThread thread)
            {
                return thread.MessageThreadId;
            }
            if (topic is MessageTopicForum forum)
            {
                return forum.ForumTopicId;
            }
            if (topic is MessageTopicDirectMessages directMessages)
            {
                return directMessages.DirectMessagesChatTopicId;
            }
            if (topic is MessageTopicSavedMessages savedMessages)
            {
                return savedMessages.SavedMessagesTopicId;
            }
            return 0;
        }

        public static long GetUpdateChatActionThreadId(UpdateChatAction update)
        {
            return GetMessageThreadId(update.TopicId);
        }

        public static long GetDraftReplyToMessageId(DraftMessage draft)
        {
            return (draft.ReplyTo as InputMessageReplyToMessage)?.MessageId ?? 0;
        }

        public static void UpdateMessageReplyTo(Message target, Message source)
        {
            target.ReplyTo = source.ReplyTo;
        }

        public static void SetMessageReplyToMessageId(Message message, long value)
        {
            if (message.ReplyTo is MessageReplyToMessage reply)
            {
                reply.MessageId = value;
            }
        }

        public static bool GetUserIsVerified(User user)
        {
            return user.VerificationStatus?.IsVerified == true;
        }

        public static bool GetSupergroupIsVerified(Supergroup supergroup)
        {
            return supergroup.VerificationStatus?.IsVerified == true;
        }

        public static bool GetStickerIsAnimated(Sticker sticker)
        {
            return sticker?.Format is StickerFormatTgs || sticker?.Format is StickerFormatWebm;
        }

        public static bool GetStickerSetIsAnimated(StickerSet stickerSet)
        {
            return stickerSet?.Stickers?.Any(GetStickerIsAnimated) == true;
        }

        public static bool GetStickerSetIsMasks(StickerSet stickerSet)
        {
            return stickerSet?.StickerType is StickerTypeMask;
        }

        public static bool GetStickerSetInfoIsAnimated(StickerSetInfo stickerSet)
        {
            return stickerSet?.Covers?.Any(GetStickerIsAnimated) == true;
        }

        public static Function SetMessageSenderBlocked(MessageSender sender, bool blocked)
        {
#if MODERN_TDLIB
            return new SetMessageSenderBlockList(sender, blocked ? new BlockListMain() : null);
#else
            return new ToggleMessageSenderIsBlocked(sender, blocked);
#endif
        }

        public static Function CreateAddProxy(string server, int port, bool enabled, ProxyType type)
        {
#if MODERN_TDLIB
            return new Telegram.Td.Api.AddProxy(new Telegram.Td.Api.Proxy(server, port, type), enabled, string.Empty);
#else
            return new Telegram.Td.Api.AddProxy(server, port, enabled, type);
#endif
        }

        public static MessageSendOptions CreateMessageSendOptions(bool disableNotification, bool fromBackground, MessageSchedulingState schedulingState)
        {
            return new MessageSendOptions(null, disableNotification, fromBackground, false, false, 0, false, schedulingState, 0, 0, false);
        }

        public static MessageSchedulingState CreateMessageSchedulingStateSendAtDate(int date)
        {
            return new MessageSchedulingStateSendAtDate(date, 0);
        }

        public static MessageContent CreateMessageText(FormattedText text, WebPage webPage)
        {
            return new MessageText(text, webPage, null);
        }

        public static string GetPollQuestion(Poll poll)
        {
#if MODERN_TDLIB
            return poll?.Question?.Text ?? string.Empty;
#else
            return poll?.Question ?? string.Empty;
#endif
        }

        public static StickerType GetStickerType(bool masks)
        {
#if MODERN_TDLIB
            return masks ? (StickerType)new StickerTypeMask() : new StickerTypeRegular();
#else
            return null;
#endif
        }

        public static Function GetInstalledStickerSets(bool masks)
        {
#if MODERN_TDLIB
            return new Telegram.Td.Api.GetInstalledStickerSets(GetStickerType(masks));
#else
            return new Telegram.Td.Api.GetInstalledStickerSets(masks);
#endif
        }

        public static Function GetRecentStickers(bool masks)
        {
            return new Telegram.Td.Api.GetRecentStickers(masks);
        }

        public static Function GetArchivedStickerSets(bool masks, long offset, int limit)
        {
#if MODERN_TDLIB
            return new Telegram.Td.Api.GetArchivedStickerSets(GetStickerType(masks), offset, limit);
#else
            return new Telegram.Td.Api.GetArchivedStickerSets(masks, offset, limit);
#endif
        }

        public static Function ReorderInstalledStickerSets(bool masks, IList<long> stickerSetIds)
        {
#if MODERN_TDLIB
            return new Telegram.Td.Api.ReorderInstalledStickerSets(GetStickerType(masks), stickerSetIds);
#else
            return new Telegram.Td.Api.ReorderInstalledStickerSets(masks, stickerSetIds);
#endif
        }

        public static Function GetTrendingStickerSets()
        {
#if MODERN_TDLIB
            return new Telegram.Td.Api.GetTrendingStickerSets(new StickerTypeRegular(), 0, 24);
#else
            return new Telegram.Td.Api.GetTrendingStickerSets(0, 24);
#endif
        }

        public static Function GetStickers(string query, int limit)
        {
#if MODERN_TDLIB
            return new Telegram.Td.Api.GetStickers(new StickerTypeRegular(), query, limit, 0);
#else
            return new Telegram.Td.Api.GetStickers(query, limit);
#endif
        }

        public static Function SearchStickers(string query, int limit)
        {
#if MODERN_TDLIB
            return new Telegram.Td.Api.SearchStickers(new StickerTypeRegular(), string.Empty, query, new List<string>(), 0, limit);
#else
            return new Telegram.Td.Api.SearchStickers(query, limit);
#endif
        }

        public static Function SearchStickerSet(string name)
        {
#if MODERN_TDLIB
            return new Telegram.Td.Api.SearchStickerSet(name, false);
#else
            return new Telegram.Td.Api.SearchStickerSet(name);
#endif
        }

        public static Function CreateSearchChats(string query, int limit)
        {
#if MODERN_TDLIB
            return new Telegram.Td.Api.SearchChats(query, null, limit);
#else
            return new Telegram.Td.Api.SearchChats(query, limit);
#endif
        }

        public static Function CreateSearchChatsOnServer(string query, int limit)
        {
#if MODERN_TDLIB
            return new Telegram.Td.Api.SearchChatsOnServer(query, null, limit);
#else
            return new Telegram.Td.Api.SearchChatsOnServer(query, limit);
#endif
        }

        public static Function CreateSearchPublicChats(string query)
        {
#if MODERN_TDLIB
            return new Telegram.Td.Api.SearchPublicChats(query, null);
#else
            return new Telegram.Td.Api.SearchPublicChats(query);
#endif
        }

        public static Function CreateSearchMessages(ChatList chatList, string query, SearchMessagesFilter filter, int minDate, int maxDate)
        {
#if MODERN_TDLIB
            return new Telegram.Td.Api.SearchMessages(chatList, query, string.Empty, 100, filter, null, minDate, maxDate);
#else
            return new Telegram.Td.Api.SearchMessages(new long[0], query, int.MaxValue, 0, 0, 100, filter, minDate, maxDate);
#endif
        }

        public static Function CreateViewMessages(long chatId, long threadId, IList<long> messageIds, bool forceRead)
        {
#if MODERN_TDLIB
            return new Telegram.Td.Api.ViewMessages(chatId, messageIds, null, forceRead);
#else
            return new Telegram.Td.Api.ViewMessages(chatId, threadId, messageIds, forceRead);
#endif
        }

        public static Function CreateNewBasicGroupChat(IList<long> userIds, string title, int messageAutoDeleteTime = 0)
        {
#if MODERN_TDLIB
            return new Telegram.Td.Api.CreateNewBasicGroupChat(userIds, title, messageAutoDeleteTime);
#else
            return new Telegram.Td.Api.CreateNewBasicGroupChat(userIds, title);
#endif
        }

        public static Function CreateNewSupergroupChat(string title, bool isChannel, string description, ChatLocation location, bool forImport)
        {
#if MODERN_TDLIB
            return new Telegram.Td.Api.CreateNewSupergroupChat(title, false, isChannel, description, location, 0, forImport);
#else
            return new Telegram.Td.Api.CreateNewSupergroupChat(title, isChannel, description, location, forImport);
#endif
        }

        public static Function CreateImportContacts(string phoneNumber, string firstName, string lastName)
        {
#if MODERN_TDLIB
            return new Telegram.Td.Api.ImportContacts(new[] { new Telegram.Td.Api.ImportedContact(phoneNumber, firstName, lastName, null) });
#else
            return new Telegram.Td.Api.ImportContacts(new[] { new Telegram.Td.Api.Contact(phoneNumber, firstName, lastName, string.Empty, 0) });
#endif
        }

        public static Function CreateChangeImportedContacts(IEnumerable<(string PhoneNumber, string FirstName, string LastName)> contacts)
        {
#if MODERN_TDLIB
            var importedContacts = new List<Telegram.Td.Api.ImportedContact>();
            foreach (var contact in contacts)
            {
                importedContacts.Add(new Telegram.Td.Api.ImportedContact(contact.PhoneNumber, contact.FirstName, contact.LastName, null));
            }

            return new Telegram.Td.Api.ChangeImportedContacts(importedContacts);
#else
            var importedContacts = new List<Telegram.Td.Api.Contact>();
            foreach (var contact in contacts)
            {
                importedContacts.Add(new Telegram.Td.Api.Contact(contact.PhoneNumber, contact.FirstName, contact.LastName, string.Empty, 0));
            }

            return new Telegram.Td.Api.ChangeImportedContacts(importedContacts);
#endif
        }

        public static IList<long> GetChatStatisticsMessageIds(ChatStatisticsChannel statistics)
        {
            var messageIds = new List<long>();
#if MODERN_TDLIB
            foreach (var interaction in statistics.RecentInteractions)
            {
                if (interaction.ObjectType is ChatStatisticsObjectTypeMessage message)
                {
                    messageIds.Add(message.MessageId);
                }
            }
#else
            foreach (var interaction in statistics.RecentMessageInteractions)
            {
                messageIds.Add(interaction.MessageId);
            }
#endif
            return messageIds;
        }

        public static bool TryGetChatStatisticsInteraction(ChatStatisticsChannel statistics, long messageId, out int forwardCount, out int viewCount)
        {
#if MODERN_TDLIB
            foreach (var interaction in statistics.RecentInteractions)
            {
                if (interaction.ObjectType is ChatStatisticsObjectTypeMessage message && message.MessageId == messageId)
                {
                    forwardCount = interaction.ForwardCount;
                    viewCount = interaction.ViewCount;
                    return true;
                }
            }
#else
            foreach (var interaction in statistics.RecentMessageInteractions)
            {
                if (interaction.MessageId == messageId)
                {
                    forwardCount = interaction.ForwardCount;
                    viewCount = interaction.ViewCount;
                    return true;
                }
            }
#endif
            forwardCount = 0;
            viewCount = 0;
            return false;
        }

        public static Function CreateSearchEmojis(string query, string inputLanguage)
        {
#if MODERN_TDLIB
            return new Telegram.Td.Api.SearchEmojis(query, new[] { inputLanguage });
#else
            return new Telegram.Td.Api.SearchEmojis(query, false, new[] { inputLanguage });
#endif
        }

#if MODERN_TDLIB
        public static StickerSetInfo CreateStickerSetInfo(long id, string title, string name, Thumbnail thumbnail, Outline thumbnailOutline, bool isOwned, bool isInstalled, bool isArchived, bool isOfficial, bool isAnimated, bool isMasks, bool isViewed, int size, IList<Sticker> covers)
        {
            return new StickerSetInfo(id, title, name, thumbnail, thumbnailOutline, isOwned, isInstalled, isArchived, isOfficial, GetStickerType(isMasks), false, false, isViewed, size, covers);
        }
#else
        public static StickerSetInfo CreateStickerSetInfo(long id, string title, string name, Thumbnail thumbnail, IList<ClosedVectorPath> thumbnailOutline, bool isOwned, bool isInstalled, bool isArchived, bool isOfficial, bool isAnimated, bool isMasks, bool isViewed, int size, IList<Sticker> covers)
        {
            return new StickerSetInfo(id, title, name, thumbnail, thumbnailOutline, isInstalled, isArchived, isOfficial, isAnimated, isMasks, isViewed, size, covers);
        }
#endif

        public static bool IsStickerType(bool masks, StickerType stickerType)
        {
#if MODERN_TDLIB
            return masks ? stickerType is StickerTypeMask : stickerType is StickerTypeRegular;
#else
            return false;
#endif
        }

        public static bool GetAdministratorCanChangeInfo(ChatMemberStatusAdministrator administrator)
        {
#if MODERN_TDLIB
            return administrator.Rights?.CanChangeInfo == true;
#else
            return administrator.CanChangeInfo;
#endif
        }

        public static InputMessageContent CreateInputMessageDocument(InputFile file, InputThumbnail thumbnail, bool disableContentTypeDetection, FormattedText caption)
        {
#if MODERN_TDLIB
            return new InputMessageDocument(new InputDocument(file, thumbnail, disableContentTypeDetection), caption);
#else
            return new InputMessageDocument(file, thumbnail, disableContentTypeDetection, caption);
#endif
        }

        public static InputMessageContent CreateInputMessagePhoto(InputFile file, InputThumbnail thumbnail, int width, int height, FormattedText caption, int ttl)
        {
#if MODERN_TDLIB
            var photo = new InputPhoto(file, thumbnail, null, new int[0], width, height);
            return new InputMessagePhoto(photo, caption, false, ttl > 0 ? new MessageSelfDestructTypeTimer(ttl) : null, false);
#else
            return new InputMessagePhoto(file, thumbnail, new int[0], width, height, caption, ttl);
#endif
        }

        public static InputMessageContent CreateInputMessageAnimation(InputFile file, InputThumbnail thumbnail, int duration, int width, int height, FormattedText caption)
        {
#if MODERN_TDLIB
            var animation = new InputAnimation(file, thumbnail, new int[0], duration, width, height);
            return new InputMessageAnimation(animation, caption, false, false);
#else
            return new InputMessageAnimation(file, thumbnail, new int[0], duration, width, height, caption);
#endif
        }

        public static InputMessageContent CreateInputMessageVideo(InputFile file, InputThumbnail thumbnail, int duration, int width, int height, FormattedText caption, int ttl)
        {
#if MODERN_TDLIB
            var video = new InputVideo(file, thumbnail, null, 0, new int[0], duration, width, height, true);
            return new InputMessageVideo(video, caption, false, ttl > 0 ? new MessageSelfDestructTypeTimer(ttl) : null, false);
#else
            return new InputMessageVideo(file, thumbnail, new int[0], duration, width, height, true, caption, ttl);
#endif
        }

        public static InputMessageContent CreateInputMessageVideoNote(InputFile file, InputThumbnail thumbnail, int duration, int length)
        {
#if MODERN_TDLIB
            return new InputMessageVideoNote(new InputVideoNote(file, thumbnail, duration, length), null);
#else
            return new InputMessageVideoNote(file, thumbnail, duration, length);
#endif
        }

        public static InputMessageContent CreateInputMessageSticker(InputFile file, InputThumbnail thumbnail, int width, int height, string emoji)
        {
#if MODERN_TDLIB
            return new InputMessageSticker(new InputSticker(file, thumbnail, width, height), emoji);
#else
            return new InputMessageSticker(file, thumbnail, width, height, emoji);
#endif
        }

        public static InputMessageContent CreateInputMessageAudio(InputFile file, InputThumbnail thumbnail, int duration, string title, string performer, FormattedText caption)
        {
#if MODERN_TDLIB
            return new InputMessageAudio(new InputAudio(file, thumbnail, duration, title, performer), caption);
#else
            return new InputMessageAudio(file, thumbnail, duration, title, performer, caption);
#endif
        }

        public static InputMessageContent CreateInputMessageVoiceNote(InputFile file, int duration, FormattedText caption)
        {
#if MODERN_TDLIB
            return new InputMessageVoiceNote(new InputVoiceNote(file, duration, new byte[0]), caption, null);
#else
            return new InputMessageVoiceNote(file, duration, new byte[0], caption);
#endif
        }

        public static InputMessageContent CreateInputMessagePoll(string question, IList<string> options, bool isAnonymous, PollType type, bool allowsMultipleAnswers)
        {
#if MODERN_TDLIB
            var questionText = new FormattedText(question, new TextEntity[0]);
            var pollOptions = options.Select(option => new InputPollOption(new FormattedText(option, new TextEntity[0]), null)).ToList();
            InputPollType inputType;
            if (type is PollTypeQuiz quiz)
            {
                inputType = new InputPollTypeQuiz(new List<int>(quiz.CorrectOptionIds), quiz.Explanation, null);
            }
            else
            {
                inputType = new InputPollTypeRegular(allowsMultipleAnswers);
            }

            return new InputMessagePoll(questionText, pollOptions, null, null, isAnonymous, allowsMultipleAnswers, false, false, new List<string>(), false, false, inputType, 0, 0, false);
#else
            return new InputMessagePoll(question, options, isAnonymous, type, 0, 0, false);
#endif
        }

        public static PollType CreatePollTypeQuiz(int correctOptionId, FormattedText explanation)
        {
#if MODERN_TDLIB
            return new PollTypeQuiz(new List<int> { correctOptionId }, explanation, null);
#else
            return new PollTypeQuiz(correctOptionId, explanation);
#endif
        }

        public static PollType CreatePollTypeRegular(bool allowsMultipleAnswers)
        {
#if MODERN_TDLIB
            return new PollTypeRegular();
#else
            return new PollTypeRegular(allowsMultipleAnswers);
#endif
        }

        public static InputMessageContent CreateInputMessageLocation(Location location)
        {
#if MODERN_TDLIB
            return new InputMessageLocation(location);
#else
            return new InputMessageLocation(location, 0, 0, 0);
#endif
        }

        public static MessageContent CreateMessagePhoto(Photo photo, FormattedText caption)
        {
#if MODERN_TDLIB
            return new MessagePhoto(photo, null, caption, false, false, false);
#else
            return new MessagePhoto(photo, caption, false);
#endif
        }

        public static bool GetAdministratorCanDeleteMessages(ChatMemberStatusAdministrator administrator)
        {
#if MODERN_TDLIB
            return administrator.Rights?.CanDeleteMessages == true;
#else
            return administrator.CanDeleteMessages;
#endif
        }

        public static bool GetAdministratorCanEditMessages(ChatMemberStatusAdministrator administrator)
        {
#if MODERN_TDLIB
            return administrator.Rights?.CanEditMessages == true;
#else
            return administrator.CanEditMessages;
#endif
        }

        public static bool GetAdministratorCanInviteUsers(ChatMemberStatusAdministrator administrator)
        {
#if MODERN_TDLIB
            return administrator.Rights?.CanInviteUsers == true;
#else
            return administrator.CanInviteUsers;
#endif
        }

        public static bool GetAdministratorCanPinMessages(ChatMemberStatusAdministrator administrator)
        {
#if MODERN_TDLIB
            return administrator.Rights?.CanPinMessages == true;
#else
            return administrator.CanPinMessages;
#endif
        }

        public static bool GetAdministratorCanPostMessages(ChatMemberStatusAdministrator administrator)
        {
#if MODERN_TDLIB
            return administrator.Rights?.CanPostMessages == true;
#else
            return administrator.CanPostMessages;
#endif
        }

        public static bool GetAdministratorCanPromoteMembers(ChatMemberStatusAdministrator administrator)
        {
#if MODERN_TDLIB
            return administrator.Rights?.CanPromoteMembers == true;
#else
            return administrator.CanPromoteMembers;
#endif
        }

        public static bool GetAdministratorCanRestrictMembers(ChatMemberStatusAdministrator administrator)
        {
#if MODERN_TDLIB
            return administrator.Rights?.CanRestrictMembers == true;
#else
            return administrator.CanRestrictMembers;
#endif
        }

        public static bool GetAdministratorIsAnonymous(ChatMemberStatusAdministrator administrator)
        {
#if MODERN_TDLIB
            return administrator.Rights?.IsAnonymous == true;
#else
            return administrator.IsAnonymous;
#endif
        }

        public static string GetAdministratorCustomTitle(ChatMemberStatusAdministrator administrator)
        {
#if MODERN_TDLIB
            return string.Empty;
#else
            return administrator.CustomTitle;
#endif
        }

        public static string GetAdministratorCustomTitle(ChatAdministrator administrator)
        {
            return administrator.CustomTitle;
        }

        public static string GetCreatorCustomTitle(ChatMemberStatusCreator creator)
        {
#if MODERN_TDLIB
            return string.Empty;
#else
            return creator.CustomTitle;
#endif
        }

        public static ChatMemberStatus CreateAdministratorStatus(bool canBeEdited, bool canChangeInfo, bool canDeleteMessages, bool canEditMessages, bool canInviteUsers, bool canPinMessages, bool canPostMessages, bool canPromoteMembers, bool canRestrictMembers, bool isAnonymous, string customTitle)
        {
#if MODERN_TDLIB
            var rights = new ChatAdministratorRights(
                true,
                canChangeInfo,
                canPostMessages,
                canEditMessages,
                canDeleteMessages,
                canInviteUsers,
                canRestrictMembers,
                canPinMessages,
                false,
                canPromoteMembers,
                false,
                false,
                false,
                false,
                false,
                false,
                isAnonymous);
            return new ChatMemberStatusAdministrator(canBeEdited, rights);
#else
            return new ChatMemberStatusAdministrator
            {
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
#endif
        }

        public static ChatPermissions CreateChatPermissions(bool allowed)
        {
#if MODERN_TDLIB
            return new ChatPermissions(allowed, allowed, allowed, allowed, allowed, allowed, allowed, allowed, allowed, allowed, allowed, allowed, allowed, allowed, allowed, allowed);
#else
            return new ChatPermissions(allowed, allowed, allowed, allowed, allowed, allowed, allowed, allowed);
#endif
        }

        public static Message CreateMessage(long id, MessageSender sender, long chatId, MessageSendingState sendingState, MessageSchedulingState schedulingState, bool isOutgoing, bool isChannelPost, int date, MessageContent content)
        {
            return new Message(
                id,
                sender,
                null,
                chatId,
                sendingState,
                schedulingState,
                isOutgoing,
                false,
                false,
                false,
                false,
                isChannelPost,
                false,
                false,
                false,
                false,
                date,
                0,
                null,
                null,
                null,
                new List<UnreadReaction>(),
                null,
                null,
                null,
                null,
                null,
                0,
                0,
                0,
                null,
                0,
                0,
                string.Empty,
                0,
                string.Empty,
                0,
                0,
                null,
                string.Empty,
                content,
                null,
                0);
        }

        public static MessageContent CreateMessageSticker(Sticker sticker)
        {
            return new MessageSticker(sticker, false);
        }

        public static Function CreateCall(long userId, CallProtocol protocol, bool isVideo = false)
        {
            if (protocol == null)
            {
                throw new ArgumentNullException(nameof(protocol));
            }

            var request = new CreateCall();
            request.UserId = userId;
            request.Protocol = protocol;
            request.IsVideo = isVideo;
            return request;
        }

        public static Function CreateGetChatFolder(int folderId)
        {
            var request = new GetChatFolder();
            request.ChatFolderId = folderId;
            return request;
        }

        public static Function CreateCreateChatFolder(ChatFolder folder)
        {
            if (folder == null)
            {
                throw new ArgumentNullException(nameof(folder));
            }

            var request = new CreateChatFolder();
            request.Folder = folder;
            return request;
        }

        public static Function CreateEditChatFolder(int folderId, ChatFolder folder)
        {
            if (folder == null)
            {
                throw new ArgumentNullException(nameof(folder));
            }

            var request = new EditChatFolder();
            request.ChatFolderId = folderId;
            request.Folder = folder;
            return request;
        }

        public static Function CreateDeleteChatFolder(int folderId, IList<long> leaveChatIds)
        {
            var request = new DeleteChatFolder();
            request.ChatFolderId = folderId;
            request.LeaveChatIds = leaveChatIds ?? new List<long>();
            return request;
        }

        public static ChatFolder CreateChatFolder(
            string title,
            string iconName,
            IList<long> pinnedChatIds,
            IList<long> includedChatIds,
            IList<long> excludedChatIds,
            bool excludeMuted,
            bool excludeRead,
            bool excludeArchived,
            bool includeContacts,
            bool includeNonContacts,
            bool includeBots,
            bool includeGroups,
            bool includeChannels)
        {
            var name = new ChatFolderName();
            name.Text = new FormattedText
            {
                Text = title ?? string.Empty,
                Entities = new List<TextEntity>()
            };
            name.AnimateCustomEmoji = false;

            var icon = new ChatFolderIcon();
            icon.Name = iconName ?? string.Empty;

            var folder = new ChatFolder();
            folder.Name = name;
            folder.Icon = icon;
            folder.ColorId = 0;
            folder.IsShareable = false;
            folder.PinnedChatIds = pinnedChatIds ?? new List<long>();
            folder.IncludedChatIds = includedChatIds ?? new List<long>();
            folder.ExcludedChatIds = excludedChatIds ?? new List<long>();
            folder.ExcludeMuted = excludeMuted;
            folder.ExcludeRead = excludeRead;
            folder.ExcludeArchived = excludeArchived;
            folder.IncludeContacts = includeContacts;
            folder.IncludeNonContacts = includeNonContacts;
            folder.IncludeBots = includeBots;
            folder.IncludeGroups = includeGroups;
            folder.IncludeChannels = includeChannels;
            return folder;
        }

        public static CallProtocol CreateAudioCallProtocol()
        {
            // The modern C++/CX projection has already exhibited activation-factory
            // marshalling failures for other multi-argument requests. Set every
            // field separately so the protocol values cross its ABI one at a time.
            var protocol = new CallProtocol
            {
                UdpP2p = true,
                UdpReflector = true,
                MinLayer = LegacyVoipMinimumLayer,
                MaxLayer = libtgvoip.VoIPControllerWrapper.GetConnectionMaxLayer(),
                LibraryVersions = new List<string>()
            };

#if MODERN_TGCALLS
            foreach (var version in ModernTgCallsVersions)
            {
                protocol.LibraryVersions.Add(version);
            }

#endif

            if (System.Threading.Interlocked.Decrement(ref _audioCallDiagnosticBudget) >= 0)
            {
                PushDiagnostics.Write(
                    "voip.protocol",
                    $"result=created;min_layer={protocol.MinLayer};max_layer={protocol.MaxLayer};library_versions={protocol.LibraryVersions.Count}");
            }

            return protocol;
        }

        public static string GetModernAudioCallVersion(IList<string> peerVersions)
        {
#if MODERN_TGCALLS
            if (peerVersions == null || peerVersions.Count == 0)
            {
                WriteAudioCallVersionDiagnostic("result=unavailable;offered=0;list=none");
                return null;
            }

            // The whole list, not just the head. Whether the ready-state list is the
            // server's intersection of both offers or the peer's own catalogue decides
            // where a silent call has to be chased next, and the two are told apart by
            // what is actually in it. Version strings are protocol constants.
            var list = DescribeVersions(peerVersions);

            // Take the head and nothing else. The ready-state list is the server's
            // decision, not a menu to search: every other client reads element zero,
            // so scanning further down for something we happen to support is how one
            // peer ends up on V0 while the other runs V1. That mismatch still
            // connects and still reports signal bars, and then neither side can
            // decode a single frame from the other.
            var negotiated = peerVersions[0];
            if (string.IsNullOrWhiteSpace(negotiated))
            {
                WriteAudioCallVersionDiagnostic(
                    $"result=unavailable;offered={peerVersions.Count};list={list}");
                return null;
            }

            foreach (var localVersion in ModernTgCallsVersions)
            {
                if (string.Equals(negotiated, localVersion, StringComparison.Ordinal))
                {
                    WriteAudioCallVersionDiagnostic(
                        $"result=negotiated;version={negotiated};offered={peerVersions.Count};list={list}");
                    return negotiated;
                }
            }

            // The server picked something this build does not implement. Report it and
            // let the caller fall back to the legacy transport; silently substituting a
            // version we do support would produce the exact silent-call mismatch above.
            WriteAudioCallVersionDiagnostic(
                $"result=unsupported;version={SanitizeVersion(negotiated)};offered={peerVersions.Count};list={list}");
#endif

            return null;
        }

#if MODERN_TGCALLS
        /// <summary>
        /// Renders the negotiated version list as a single comma-separated field. Each
        /// entry goes through the same reduction as a lone version, and the list is
        /// capped, so an unexpected server value cannot lengthen or reshape the line.
        /// </summary>
        private static string DescribeVersions(IList<string> versions)
        {
            if (versions == null || versions.Count == 0)
            {
                return "none";
            }

            const int MaxListed = 12;
            var listed = versions.Count < MaxListed ? versions.Count : MaxListed;
            var builder = new System.Text.StringBuilder();

            for (var index = 0; index < listed; index++)
            {
                if (builder.Length > 0)
                {
                    builder.Append(',');
                }

                builder.Append(SanitizeVersion(versions[index]));
            }

            if (listed < versions.Count)
            {
                builder.Append(",+").Append(versions.Count - listed);
            }

            return builder.ToString();
        }

        /// <summary>
        /// Keeps a server-supplied version string to the shape a version can have, so an
        /// unexpected value cannot introduce separators into the diagnostics line. Version
        /// strings are protocol constants and carry nothing about the user.
        /// </summary>
        private static string SanitizeVersion(string version)
        {
            if (string.IsNullOrEmpty(version))
            {
                return "none";
            }

            var builder = new System.Text.StringBuilder(version.Length);
            foreach (var character in version)
            {
                if (char.IsDigit(character) || character == '.')
                {
                    builder.Append(character);
                }

                if (builder.Length >= 16)
                {
                    break;
                }
            }

            return builder.Length == 0 ? "unrecognised" : builder.ToString();
        }

        private static void WriteAudioCallVersionDiagnostic(string detail)
        {
            if (System.Threading.Interlocked.Decrement(ref _audioCallVersionDiagnosticBudget) >= 0)
            {
                PushDiagnostics.Write("voip.version", detail);
            }
        }
#endif

        public static void LogAudioCallRequestResult(string operation, BaseObject response)
        {
            if (System.Threading.Interlocked.Decrement(ref _audioCallDiagnosticBudget) < 0)
            {
                return;
            }

            if (response is Error error)
            {
                PushDiagnostics.Write(
                    $"voip.{operation}",
                    $"result=error;code={error.Code};message={PushDiagnostics.SanitizeErrorMessage(error.Message)}");
                return;
            }

            PushDiagnostics.Write($"voip.{operation}", $"result={response?.GetType().Name ?? "null"}");
        }

        public static void LogAudioCallRequestException(string operation, Exception exception)
        {
            PushDiagnostics.WriteException($"voip.{operation}", exception);
        }

        public static Function CreateAcceptCall(int callId, CallProtocol protocol)
        {
            if (protocol == null)
            {
                throw new ArgumentNullException(nameof(protocol));
            }

            var request = new AcceptCall();
            request.CallId = callId;
            request.Protocol = protocol;
            return request;
        }

        public static Function CreateDiscardCall(int callId, bool isDisconnected, int duration, long connectionId)
        {
            return new DiscardCall(callId, isDisconnected, string.Empty, duration, false, connectionId);
        }

        public static Function CreateSendCallDebugInformation(int callId, string debugInformation)
        {
            return new SendCallDebugInformation(new InputCallDiscarded(callId), debugInformation ?? string.Empty);
        }
    }
}

namespace Telegram.Td.Api
{
    using System.Collections.Generic;

    // The experimental app uses these small compatibility models for shared folder views.
    public sealed class ChatFilterInfo
    {
        public int Id { get; set; }
        public string Title { get; set; }
        public string IconName { get; set; }
    }

    public sealed class ChatFilter
    {
        public string IconName { get; set; }
    }

    public sealed class RecommendedChatFilter
    {
        public ChatFolder Folder { get; set; }
        public string Description { get; set; }
    }

    public sealed class UpdateChatFilters
    {
        public IList<ChatFilterInfo> ChatFilters { get; set; } = new List<ChatFilterInfo>();
    }
}
