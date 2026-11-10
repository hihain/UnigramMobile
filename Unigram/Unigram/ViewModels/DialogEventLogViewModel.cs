using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Telegram.Td.Api;
using Unigram.Common;
using Unigram.Controls;
using Unigram.Converters;
using Unigram.Services;
using Unigram.Services.Factories;
using Unigram.Views.Popups;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unigram.ViewModels
{
    public class DialogEventLogViewModel : DialogViewModel
    {
        public DialogEventLogViewModel(IProtoService protoService, ICacheService cacheService, ISettingsService settingsService, IEventAggregator aggregator, ILocationService locationService, INotificationsService pushService, IPlaybackService playbackService, IVoIPService voipService, INetworkService networkService, IMessageFactory messageFactory)
            : base(protoService, cacheService, settingsService, aggregator, locationService, pushService, playbackService, voipService, networkService, messageFactory)
        {
            HelpCommand = new RelayCommand(HelpExecute);
        }

        public override DialogType Type => DialogType.EventLog;

        private long _minEventId = long.MaxValue;

#if MODERN_TDLIB
        private ChatEventLogFilters _filters = new ChatEventLogFilters(true, true, true, true, true, true, true, true, false, true, true, true, true, true, true);
#else
        private ChatEventLogFilters _filters = new ChatEventLogFilters(true, true, true, true, true, true, true, true, true, true, true, true);
#endif
        public ChatEventLogFilters Filters
        {
            get => _filters;
            set => Set(ref _filters, value);
        }

        private IList<long> _userIds = new long[0];
        public IList<long> UserIds
        {
            get => _userIds;
            set => Set(ref _userIds, value);
        }

        public override string Subtitle
        {
            get
            {
                if (_filters.InfoChanges &&
                    _filters.MemberInvites &&
                    _filters.MemberJoins &&
                    _filters.MemberLeaves &&
                    _filters.MemberPromotions &&
                    _filters.MemberRestrictions &&
                    _filters.MessageDeletions &&
                    _filters.MessageEdits &&
                    _filters.MessagePins &&
                    _filters.SettingChanges &&
                    _filters.InviteLinkChanges &&
                    _filters.VideoChatChanges &&
                    _userIds.IsEmpty())
                {
                    return Strings.Resources.EventLogAllEvents;
                }

                return Strings.Resources.EventLogSelectedEvents;
            }
        }

        protected override async void FilterExecute()
        {
            var chat = _chat;
            if (chat == null)
            {
                return;
            }

            var supergroup = CacheService.GetSupergroup(chat);
            if (supergroup == null)
            {
                return;
            }

            var dialog = new SupergroupEventLogFiltersPopup();

            var confirm = await dialog.ShowAsync(ProtoService, supergroup.Id, _filters, _userIds);
            if (confirm == ContentDialogResult.Primary)
            {
                Filters = dialog.Filters;
                UserIds = dialog.UserIds;

                RaisePropertyChanged(() => Subtitle);

                await LoadEventLogSliceAsync();
            }
        }

        public RelayCommand HelpCommand { get; }
        private async void HelpExecute()
        {
            var chat = _chat;
            if (chat == null)
            {
                return;
            }

            await MessagePopup.ShowAsync(chat.Type is ChatTypeSupergroup supergroup && supergroup.IsChannel ? Strings.Resources.EventLogInfoDetailChannel : Strings.Resources.EventLogInfoDetail, Strings.Resources.EventLogInfoTitle, Strings.Resources.OK);
        }

        public override async Task LoadEventLogSliceAsync(string query = "")
        {
            using (await _loadMoreLock.WaitAsync())
            {
                var chat = _chat;
                if (chat == null)
                {
                    return;
                }

                if (_isLoadingNextSlice || _isLoadingPreviousSlice)
                {
                    return;
                }

                _isLoadingNextSlice = true;
                _isLoadingPreviousSlice = true;
                IsLastSliceLoaded = null;
                IsFirstSliceLoaded = null;
                IsLoading = true;

                System.Diagnostics.Debug.WriteLine("DialogViewModel: LoadScheduledSliceAsync");

                var response = await ProtoService.SendAsync(new GetChatEventLog(chat.Id, query, 0, 50, _filters, _userIds));
                if (response is ChatEvents events)
                {
                    _groupedMessages.Clear();

                    if (events.Events.Count > 0)
                    {
                        SetScrollMode(ItemsUpdatingScrollMode.KeepLastItemInView, true);
                        Logs.Logger.Debug(Logs.Target.Chat, "Setting scroll mode to KeepLastItemInView");
                    }

                    var replied = ProcessEvents(events);
                    await ProcessMessagesAsync(chat, replied);

                    var target = replied.FirstOrDefault();
                    if (target != null)
                    {
                        replied.Insert(0, _messageFactory.Create(this, CreateHeaderMessage(target.SenderId, target.ChatId, target.SendingState, target.SchedulingState, target.IsOutgoing, target.IsChannelPost, target.Date)));
                    }

                    Items.ReplaceWith(replied);

                    IsLastSliceLoaded = false;
                    IsFirstSliceLoaded = true;
                }

                _isLoadingNextSlice = false;
                _isLoadingPreviousSlice = false;
                IsLoading = false;
            }

            var already = Items.LastOrDefault();
            if (already != null)
            {
                var field = ListField;
                if (field != null)
                {
                    await field.ScrollToItem(already, VerticalAlignment.Bottom, false, int.MaxValue, ScrollIntoViewAlignment.Leading, true);
                }
            }
        }

        public async override Task LoadNextSliceAsync(bool force = false, bool init = false)
        {
            using (await _loadMoreLock.WaitAsync())
            {
                try
                {
                    // We don't want to flood with requests when the chat gets initialized
                    if (init && ListField?.ScrollingHost?.ScrollableHeight >= 200)
                    {
                        return;
                    }
                }
                catch { }

                var chat = _migratedChat ?? _chat;
                if (chat == null)
                {
                    return;
                }

                if (_isLoadingNextSlice || _isLoadingPreviousSlice || Items.Count < 1 || IsLastSliceLoaded == true)
                {
                    return;
                }

                _isLoadingNextSlice = true;
                IsLoading = true;

                System.Diagnostics.Debug.WriteLine("DialogViewModel: LoadNextSliceAsync");
                System.Diagnostics.Debug.WriteLine("DialogViewModel: LoadNextSliceAsync: Begin request");

                var response = await ProtoService.SendAsync(new GetChatEventLog(chat.Id, string.Empty, _minEventId, 50, _filters, _userIds));
                if (response is ChatEvents events)
                {
                    if (events.Events.Count > 0)
                    {
                        SetScrollMode(ItemsUpdatingScrollMode.KeepLastItemInView, force);
                        Logs.Logger.Debug(Logs.Target.Chat, "Setting scroll mode to KeepLastItemInView");
                    }

                    var replied = ProcessEvents(events);
                    await ProcessMessagesAsync(chat, replied);

                    foreach (var message in replied)
                    {
                        Items.Insert(0, message);
                    }

                    IsLastSliceLoaded = replied.IsEmpty();

                    if (replied.IsEmpty())
                    {
                        await AddHeaderAsync();
                    }
                }

                _isLoadingNextSlice = false;
                IsLoading = false;
            }
        }

        private Message CreateMessage(long chatId, bool isChannel, ChatEvent chatEvent, bool child = false)
        {
#if MODERN_TDLIB
            MessageSender sender = chatEvent.MemberId;
#else
            MessageSender sender = new MessageSenderUser(chatEvent.UserId);
#endif

            if (child)
            {
                if (chatEvent.Action is ChatEventMessageDeleted messageDeleted)
                {
                    sender = messageDeleted.Message.SenderId;
                }
                else if (chatEvent.Action is ChatEventMessageEdited messageEdited)
                {
                    sender = messageEdited.NewMessage.SenderId;
                }
                else if (chatEvent.Action is ChatEventMessagePinned messagePinned)
                {
                    sender = messagePinned.Message.SenderId;
                }
                else if (chatEvent.Action is ChatEventPollStopped pollStopped)
                {
                    sender = pollStopped.Message.SenderId;
                }
            }

#if MODERN_TDLIB
            return ModernTdlibCompatibility.CreateMessage(chatEvent.Id, sender, chatId, null, null, false, isChannel, chatEvent.Date, null);
#else
            return new Message(chatEvent.Id, sender, chatId, null, null, false, false, false, false, false, false, false, false, false, false, false, false, isChannel, false, chatEvent.Date, 0, null, null, 0, 0, 0, 0, 0, 0, string.Empty, 0, string.Empty, null, null);
#endif
        }

        private static Message CreateHeaderMessage(MessageSender sender, long chatId, MessageSendingState sendingState, MessageSchedulingState schedulingState, bool isOutgoing, bool isChannelPost, int date)
        {
#if MODERN_TDLIB
            return ModernTdlibCompatibility.CreateMessage(0, sender, chatId, sendingState, schedulingState, isOutgoing, isChannelPost, date, new MessageHeaderDate());
#else
            return new Message(0, sender, chatId, null, schedulingState, isOutgoing, false, false, false, false, true, false, false, false, false, false, false, isChannelPost, false, date, 0, null, null, 0, 0, 0, 0, 0, 0, string.Empty, 0, string.Empty, new MessageHeaderDate(), null);
#endif
        }

        private MessageViewModel GetMessage(long chatId, bool isChannel, ChatEvent chatEvent, bool child = false)
        {
            var message = _messageFactory.Create(this, CreateMessage(chatId, isChannel, chatEvent, child));
            message.IsFirst = true;
            message.IsLast = true;

            return message;
        }

        private IList<MessageViewModel> ProcessEvents(ChatEvents events)
        {
            var result = new List<MessageViewModel>();
            var channel = _chat.Type is ChatTypeSupergroup super && super.IsChannel;

            foreach (var item in events.Events)
            {
                _minEventId = Math.Min(_minEventId, item.Id);

                MessageViewModel message = null;
                switch (item.Action)
                {
                    case ChatEventMemberInvited memberInvited:
                        message = GetMessage(_chat.Id, channel, item);
                        message.Content = new MessageChatAddMembers(new[] { memberInvited.UserId });
                        break;
                    case ChatEventSlowModeDelayChanged slowModeDelayChanged:
                    case ChatEventPermissionsChanged permissionsChanged:
                    case ChatEventMemberRestricted memberRestricted:
                    case ChatEventMemberPromoted memberPromoted:
                        message = GetMessage(_chat.Id, channel, item);
                        //message.Content = new MessageChatEvent(item, true);
                        message.Content = GetMessageContent(item, channel);
                        break;
                    case ChatEventSignMessagesToggled signMessagesToggled:
                    case ChatEventStickerSetChanged stickerSetChanged:
                    case ChatEventInvitesToggled invitesToggled:
                    case ChatEventIsAllHistoryAvailableToggled isAllHistoryAvailableToggled:
                    case ChatEventMessageUnpinned messageUnpinned:
                    case ChatEventLinkedChatChanged linkedChatChanged:
                    case ChatEventLocationChanged locationChanged:
                        message = GetMessage(_chat.Id, channel, item);
                        message.Content = new MessageChatEvent(item);
                        break;
                    case ChatEventMemberLeft memberLeft:
                        message = GetMessage(_chat.Id, channel, item);
                        message.Content = new MessageChatDeleteMember(item.MemberId is MessageSenderUser member ? member.UserId : 0);
                        break;
                    case ChatEventDescriptionChanged descriptionChanged:
                    case ChatEventUsernameChanged usernameChanged:
                    case ChatEventMessageDeleted messageDeleted:
                    case ChatEventMessageEdited messageEdited:
                    case ChatEventMessagePinned messagePinned:
                    case ChatEventPollStopped pollStopped:
                        message = GetMessage(_chat.Id, channel, item, true);
                        //message.Content = new MessageChatEvent(item, true);
                        message.Content = GetMessageContent(item, channel);
                        result.Add(message);
                        message = GetMessage(_chat.Id, channel, item);
                        message.Content = new MessageChatEvent(item);
                        break;
                    case ChatEventPhotoChanged photoChanged:
                        message = GetMessage(_chat.Id, channel, item);
                        if (photoChanged.NewPhoto == null)
                        {
                            message.Content = new MessageChatDeletePhoto();
                            break;
                        }

                        message.Content = new MessageChatChangePhoto(photoChanged.NewPhoto);
                        break;
                    case ChatEventMemberJoined memberJoined:
                        message = GetMessage(_chat.Id, channel, item);
                        message.Content = new MessageChatAddMembers(new long[] { item.MemberId is MessageSenderUser senderUser ? senderUser.UserId : 0 });
                        break;
                    case ChatEventTitleChanged titleChanged:
                        message = GetMessage(_chat.Id, channel, item);
                        message.Content = new MessageChatChangeTitle(titleChanged.NewTitle);
                        break;
                }

                if (message != null)
                {
                    result.Add(message);
                }
            }

            //if (response.Result.Events.Count < 50)
            //{
            //    _hasMore = false;
            //}

            result.Reverse();
            return result;
        }

        private MessageContent GetMessageContent(ChatEvent item, bool channel)
        {
            if (item.Action is ChatEventDescriptionChanged descriptionChanged)
            {
                var text = new FormattedText(descriptionChanged.NewDescription, new TextEntity[0]);
                var webPage = string.IsNullOrEmpty(descriptionChanged.OldDescription) ? null : new WebPage { SiteName = Strings.Resources.EventLogPreviousGroupDescription, Description = new FormattedText { Text = descriptionChanged.OldDescription } };

                return ModernTdlibCompatibility.CreateMessageText(text, webPage);
            }
            else if (item.Action is ChatEventUsernameChanged usernameChanged)
            {
                var link = string.IsNullOrEmpty(usernameChanged.NewUsername) ? string.Empty : MeUrlPrefixConverter.Convert(CacheService, usernameChanged.NewUsername);

                var text = new FormattedText(link, new[] { new TextEntity(0, link.Length, new TextEntityTypeUrl()) });
                var webPage = string.IsNullOrEmpty(usernameChanged.OldUsername) ? null : new WebPage { SiteName = Strings.Resources.EventLogPreviousLink, Description = new FormattedText { Text = MeUrlPrefixConverter.Convert(CacheService, usernameChanged.OldUsername) } };

                return ModernTdlibCompatibility.CreateMessageText(text, webPage);
            }
            else if (item.Action is ChatEventPermissionsChanged permissionChanged)
            {
                var text = string.Empty;
                var entities = new List<TextEntity>();

                ChatPermissions o = permissionChanged.OldPermissions;
                ChatPermissions n = permissionChanged.NewPermissions;

                if (o == null)
                {
                    o = CreateEmptyPermissions();
                }
                if (n == null)
                {
                    n = CreateEmptyPermissions();
                }

                var rights = new StringBuilder(Strings.Resources.EventLogDefaultPermissions);
                var added = false;

                void AppendChange(bool value, string label)
                {
                    if (!added)
                    {
                        rights.Append('\n');
                        added = true;
                    }

                    rights.Append('\n').Append(value ? '+' : '-').Append(' ');
                    rights.Append(label);
                }

                //if (o.IsViewMessages != n.IsViewMessages)
                //{
                //    AppendChange(n.IsViewMessages, Strings.Resources.EventLogRestrictedReadMessages);
                //}
                if (o.GetCanSendMessages() != n.GetCanSendMessages())
                {
                    AppendChange(n.GetCanSendMessages(), Strings.Resources.EventLogRestrictedSendMessages);
                }
                if (o.CanSendOtherMessages != n.CanSendOtherMessages)
                {
                    AppendChange(n.CanSendOtherMessages, Strings.Resources.EventLogRestrictedSendStickers);
                }
                if (o.GetCanSendMediaMessages() != n.GetCanSendMediaMessages())
                {
                    AppendChange(n.GetCanSendMediaMessages(), Strings.Resources.EventLogRestrictedSendMedia);
                }
                if (o.CanSendPolls != n.CanSendPolls)
                {
                    AppendChange(n.CanSendPolls, Strings.Resources.EventLogRestrictedSendPolls);
                }
                if (o.GetCanAddWebPagePreviews() != n.GetCanAddWebPagePreviews())
                {
                    AppendChange(n.GetCanAddWebPagePreviews(), Strings.Resources.EventLogRestrictedSendEmbed);
                }
                if (o.CanChangeInfo != n.CanChangeInfo)
                {
                    AppendChange(n.CanChangeInfo, Strings.Resources.EventLogRestrictedChangeInfo);
                }
                if (o.CanInviteUsers != n.CanInviteUsers)
                {
                    AppendChange(n.CanInviteUsers, Strings.Resources.EventLogRestrictedSendEmbed);
                }
                if (o.CanPinMessages != n.CanPinMessages)
                {
                    AppendChange(n.CanPinMessages, Strings.Resources.EventLogRestrictedPinMessages);
                }

                text = rights.ToString();

                return ModernTdlibCompatibility.CreateMessageText(new FormattedText(text, entities), null);
            }
            else if (item.Action is ChatEventMemberRestricted memberRestricted)
            {
                var text = string.Empty;
                var entities = new List<TextEntity>();

                var whoUser = CacheService.GetMessageSender(memberRestricted.MemberId);
                ChatMemberStatusRestricted o = null;
                ChatMemberStatusRestricted n = null;

                if (memberRestricted.OldStatus is ChatMemberStatusRestricted oldRestricted)
                {
                    o = oldRestricted;
                }
                else if (memberRestricted.OldStatus is ChatMemberStatusBanned oldBanned)
                {
                    o = new ChatMemberStatusRestricted(false, oldBanned.BannedUntilDate, ModernTdlibCompatibility.CreateChatPermissions(false));
                }
                else if (memberRestricted.OldStatus is ChatMemberStatusMember)
                {
                    o = new ChatMemberStatusRestricted(true, 0, ModernTdlibCompatibility.CreateChatPermissions(true));
                }

                if (memberRestricted.NewStatus is ChatMemberStatusRestricted newRestricted)
                {
                    n = newRestricted;
                }
                else if (memberRestricted.NewStatus is ChatMemberStatusBanned newBanned)
                {
                    n = new ChatMemberStatusRestricted(false, newBanned.BannedUntilDate, ModernTdlibCompatibility.CreateChatPermissions(false));
                }
                else if (memberRestricted.NewStatus is ChatMemberStatusMember)
                {
                    n = new ChatMemberStatusRestricted(true, 0, ModernTdlibCompatibility.CreateChatPermissions(true));
                }

                if (!channel && (n == null || n != null && o != null /*&& n.RestrictedUntilDate != o.RestrictedUntilDate*/))
                {
                    StringBuilder rights;
                    String bannedDuration;
                    if (n != null && !n.IsForever())
                    {
                        bannedDuration = "";
                        int duration = n.RestrictedUntilDate - item.Date;
                        int days = duration / 60 / 60 / 24;
                        duration -= days * 60 * 60 * 24;
                        int hours = duration / 60 / 60;
                        duration -= hours * 60 * 60;
                        int minutes = duration / 60;
                        int count = 0;
                        for (int a = 0; a < 3; a++)
                        {
                            String addStr = null;
                            if (a == 0)
                            {
                                if (days != 0)
                                {
                                    addStr = Locale.Declension("Days", days);
                                    count++;
                                }
                            }
                            else if (a == 1)
                            {
                                if (hours != 0)
                                {
                                    addStr = Locale.Declension("Hours", hours);
                                    count++;
                                }
                            }
                            else
                            {
                                if (minutes != 0)
                                {
                                    addStr = Locale.Declension("Minutes", minutes);
                                    count++;
                                }
                            }
                            if (addStr != null)
                            {
                                if (bannedDuration.Length > 0)
                                {
                                    bannedDuration += ", ";
                                }
                                bannedDuration += addStr;
                            }
                            if (count == 2)
                            {
                                break;
                            }
                        }
                    }
                    else
                    {
                        bannedDuration = Strings.Resources.UserRestrictionsUntilForever;
                    }

                    var str = Strings.Resources.EventLogRestrictedUntil;
                    rights = new StringBuilder(String.Format(str, GetUserName(whoUser, entities, str.IndexOf("{0}")), bannedDuration));
                    var added = false;
                    if (o == null)
                    {
                        o = new ChatMemberStatusRestricted(true, 0, ModernTdlibCompatibility.CreateChatPermissions(true));
                    }
                    if (n == null)
                    {
                        n = new ChatMemberStatusRestricted(true, 0, ModernTdlibCompatibility.CreateChatPermissions(true));
                    }

                    void AppendChange(bool value, string label)
                    {
                        if (!added)
                        {
                            rights.Append('\n');
                            added = true;
                        }

                        rights.Append('\n').Append(value ? '+' : '-').Append(' ');
                        rights.Append(label);
                    }

                    //if (o.IsViewMessages != n.IsViewMessages)
                    //{
                    //    AppendChange(n.IsViewMessages, Strings.Resources.EventLogRestrictedReadMessages);
                    //}
                    if (o.Permissions.GetCanSendMessages() != n.Permissions.GetCanSendMessages())
                    {
                        AppendChange(n.Permissions.GetCanSendMessages(), Strings.Resources.EventLogRestrictedSendMessages);
                    }
                    if (o.Permissions.CanSendOtherMessages != n.Permissions.CanSendOtherMessages)
                    {
                        AppendChange(n.Permissions.CanSendOtherMessages, Strings.Resources.EventLogRestrictedSendStickers);
                    }
                    if (o.Permissions.GetCanSendMediaMessages() != n.Permissions.GetCanSendMediaMessages())
                    {
                        AppendChange(n.Permissions.GetCanSendMediaMessages(), Strings.Resources.EventLogRestrictedSendMedia);
                    }
                    if (o.Permissions.CanSendPolls != n.Permissions.CanSendPolls)
                    {
                        AppendChange(n.Permissions.CanSendPolls, Strings.Resources.EventLogRestrictedSendPolls);
                    }
                    if (o.Permissions.GetCanAddWebPagePreviews() != n.Permissions.GetCanAddWebPagePreviews())
                    {
                        AppendChange(n.Permissions.GetCanAddWebPagePreviews(), Strings.Resources.EventLogRestrictedSendEmbed);
                    }
                    if (o.Permissions.CanChangeInfo != n.Permissions.CanChangeInfo)
                    {
                        AppendChange(n.Permissions.CanChangeInfo, Strings.Resources.EventLogRestrictedChangeInfo);
                    }
                    if (o.Permissions.CanInviteUsers != n.Permissions.CanInviteUsers)
                    {
                        AppendChange(n.Permissions.CanInviteUsers, Strings.Resources.EventLogRestrictedSendEmbed);
                    }
                    if (o.Permissions.CanPinMessages != n.Permissions.CanPinMessages)
                    {
                        AppendChange(n.Permissions.CanPinMessages, Strings.Resources.EventLogRestrictedPinMessages);
                    }

                    text = rights.ToString();
                }
                else
                {
                    String str;
                    if (o == null || memberRestricted.NewStatus is ChatMemberStatusBanned)
                    {
                        str = Strings.Resources.EventLogChannelRestricted;
                    }
                    else
                    {
                        str = Strings.Resources.EventLogChannelUnrestricted;
                    }

                    text = String.Format(str, GetUserName(whoUser, entities, str.IndexOf("{0}")));
                }

                return ModernTdlibCompatibility.CreateMessageText(new FormattedText(text, entities), null);
            }
            else if (item.Action is ChatEventMemberPromoted memberPromoted)
            {
                var entities = new List<TextEntity>();

                var whoUser =  CacheService.GetUser(memberPromoted.UserId);
                var str = memberPromoted.NewStatus is ChatMemberStatusCreator
                    ? Strings.Resources.EventLogChangedOwnership
                    : Strings.Resources.EventLogPromoted;
                var userName = GetUserName(whoUser, entities, str.IndexOf("{0}"));
                var builder = new StringBuilder(string.Format(str, userName));
                var added = false;

                if (memberPromoted.NewStatus is ChatMemberStatusCreator)
                {
                    return ModernTdlibCompatibility.CreateMessageText(new FormattedText(builder.ToString(), entities), null);
                }

                ChatMemberStatusAdministrator o = null;
                ChatMemberStatusAdministrator n = null;

                if (memberPromoted.OldStatus is ChatMemberStatusAdministrator oldAdmin)
                {
                    o = oldAdmin;
                }
                if (memberPromoted.NewStatus is ChatMemberStatusAdministrator newAdmin)
                {
                    n = newAdmin;
                }

                if (o == null)
                {
                    o = CreateEmptyAdministratorStatus();
                }
                if (n == null)
                {
                    n = CreateEmptyAdministratorStatus();
                }

                if (!string.Equals(ModernTdlibCompatibility.GetAdministratorCustomTitle(o), ModernTdlibCompatibility.GetAdministratorCustomTitle(n)))
                {
                    if (!added)
                    {
                        builder.Append('\n');
                        added = true;
                    }

                    if (string.IsNullOrEmpty(ModernTdlibCompatibility.GetAdministratorCustomTitle(n)))
                    {
                        builder.Append('\n').Append('-').Append(' ');
                        builder.Append(Strings.Resources.EventLogPromotedRemovedTitle);
                    }
                    else
                    {
                        builder.Append('\n').Append('+').Append(' ');
                        builder.AppendFormat(Strings.Resources.EventLogPromotedTitle, ModernTdlibCompatibility.GetAdministratorCustomTitle(n));
                    }
                }

                void AppendChange(bool value, string label)
                {
                    if (!added)
                    {
                        builder.Append('\n');
                        added = true;
                    }

                    builder.Append('\n').Append(value ? '+' : '-').Append(' ');
                    builder.Append(label);
                }

                if (ModernTdlibCompatibility.GetAdministratorCanChangeInfo(o) != ModernTdlibCompatibility.GetAdministratorCanChangeInfo(n))
                {
                    AppendChange(ModernTdlibCompatibility.GetAdministratorCanChangeInfo(n), channel ? Strings.Resources.EventLogPromotedChangeChannelInfo : Strings.Resources.EventLogPromotedChangeGroupInfo);
                }

                if (channel)
                {
                    if (ModernTdlibCompatibility.GetAdministratorCanPostMessages(o) != ModernTdlibCompatibility.GetAdministratorCanPostMessages(n))
                    {
                        AppendChange(ModernTdlibCompatibility.GetAdministratorCanPostMessages(n), Strings.Resources.EventLogPromotedPostMessages);
                    }
                    if (ModernTdlibCompatibility.GetAdministratorCanEditMessages(o) != ModernTdlibCompatibility.GetAdministratorCanEditMessages(n))
                    {
                        AppendChange(ModernTdlibCompatibility.GetAdministratorCanEditMessages(n), Strings.Resources.EventLogPromotedEditMessages);
                    }
                }
                if (ModernTdlibCompatibility.GetAdministratorCanDeleteMessages(o) != ModernTdlibCompatibility.GetAdministratorCanDeleteMessages(n))
                {
                    AppendChange(ModernTdlibCompatibility.GetAdministratorCanDeleteMessages(n), Strings.Resources.EventLogPromotedDeleteMessages);
                }
                if (ModernTdlibCompatibility.GetAdministratorCanPromoteMembers(o) != ModernTdlibCompatibility.GetAdministratorCanPromoteMembers(n))
                {
                    AppendChange(ModernTdlibCompatibility.GetAdministratorCanPromoteMembers(n), Strings.Resources.EventLogPromotedAddAdmins);
                }
                if (!channel)
                {
                    if (ModernTdlibCompatibility.GetAdministratorIsAnonymous(o) != ModernTdlibCompatibility.GetAdministratorIsAnonymous(n))
                    {
                        AppendChange(ModernTdlibCompatibility.GetAdministratorIsAnonymous(n), Strings.Resources.EventLogPromotedSendAnonymously);
                    }
                    if (ModernTdlibCompatibility.GetAdministratorCanRestrictMembers(o) != ModernTdlibCompatibility.GetAdministratorCanRestrictMembers(n))
                    {
                        AppendChange(ModernTdlibCompatibility.GetAdministratorCanRestrictMembers(n), Strings.Resources.EventLogPromotedBanUsers);
                    }
                }
                if (ModernTdlibCompatibility.GetAdministratorCanInviteUsers(o) != ModernTdlibCompatibility.GetAdministratorCanInviteUsers(n))
                {
                    AppendChange(ModernTdlibCompatibility.GetAdministratorCanInviteUsers(n), Strings.Resources.EventLogPromotedAddUsers);
                }
                if (!channel && ModernTdlibCompatibility.GetAdministratorCanPinMessages(o) != ModernTdlibCompatibility.GetAdministratorCanPinMessages(n))
                {
                    AppendChange(ModernTdlibCompatibility.GetAdministratorCanPinMessages(n), Strings.Resources.EventLogPromotedPinMessages);
                }

                return ModernTdlibCompatibility.CreateMessageText(new FormattedText(builder.ToString(), entities), null);
            }
            else if (item.Action is ChatEventMessageDeleted messageDeleted)
            {
                return messageDeleted.Message.Content;
            }
            else if (item.Action is ChatEventMessageEdited messageEdited)
            {
                if (messageEdited.NewMessage.Content is MessageText editedText && messageEdited.OldMessage.Content is MessageText oldText)
                {
                    editedText.WebPage = new WebPage
                    {
                        SiteName = Strings.Resources.EventLogOriginalMessages,
                        Description = oldText.Text
                    };
                }

                return messageEdited.NewMessage.Content;
            }
            else if (item.Action is ChatEventMessagePinned messagePinned)
            {
                return messagePinned.Message.Content;
            }
            else if (item.Action is ChatEventPollStopped pollStopped)
            {
                return pollStopped.Message.Content;
            }

            return new MessageChatEvent(item);
        }

        private static ChatMemberStatusAdministrator CreateEmptyAdministratorStatus()
        {
#if MODERN_TDLIB
            return new ChatMemberStatusAdministrator(false, new ChatAdministratorRights(false, false, false, false, false, false, false, false, false, false, false, false, false, false, false, false, false));
#else
            return new ChatMemberStatusAdministrator();
#endif
        }

        private static ChatPermissions CreateEmptyPermissions()
        {
#if MODERN_TDLIB
            return new ChatPermissions(false, false, false, false, false, false, false, false, false, false, false, false, false, false, false, false);
#else
            return new ChatPermissions();
#endif
        }

        private string GetUserName(BaseObject sender, List<TextEntity> entities, int offset)
        {
            if (sender is User user)
            {
                var name = user.GetFullName();

                if (offset >= 0)
                {
                    entities.Add(new TextEntity(offset, name.Length, new TextEntityTypeMentionName(user.Id)));
                }

                if (string.IsNullOrEmpty(user.GetUsername()))
                {
                    return name;
                }

                if (offset >= 0)
                {
                    entities.Add(new TextEntity(name.Length + offset + 2, user.GetUsername().Length + 1, new TextEntityTypeMentionName(user.Id)));
                }

                return string.Format("{0} (@{1})", name, user.GetUsername());
            }
            else if (sender is Chat chat)
            {
                var name = CacheService.GetTitle(chat);

                // Make it clickable

                return name;
            }

            return string.Empty;
        }
    }
}
