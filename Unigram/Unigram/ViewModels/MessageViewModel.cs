using System.Collections.Generic;
using Telegram.Td.Api;
using Unigram.Common;
using Unigram.Services;
using Unigram.ViewModels.Delegates;
using Windows.Foundation.Metadata;

namespace Unigram.ViewModels
{
    public class MessageViewModel
    {
        private readonly IProtoService _protoService;
        private readonly IPlaybackService _playbackService;
        private readonly IMessageDelegate _delegate;

        private Message _message;

        public MessageViewModel(IProtoService protoService, IPlaybackService playbackService, IMessageDelegate delegato, Message message)
        {
            _protoService = protoService;
            _playbackService = playbackService;
            _delegate = delegato;

            _message = message;
        }

        public IProtoService ProtoService => _protoService;
        public IPlaybackService PlaybackService => _playbackService;
        public IMessageDelegate Delegate => _delegate;

        public bool IsInitial { get; set; } = true;

        public bool IsFirst { get; set; } = true;
        public bool IsLast { get; set; } = true;

        private Dictionary<string, UnreadReaction> _unreadReactions;
        public IDictionary<string, UnreadReaction> UnreadReactions
        {
            get
            {
                if (_unreadReactions == null)
                {
                    _unreadReactions = new Dictionary<string, UnreadReaction>();

                    if (_message.UnreadReactions != null)
                    {
                        foreach (var item in _message.UnreadReactions)
                        {
                            var key = item.Type.ToKey();
                            if (string.IsNullOrEmpty(key))
                            {
                                continue;
                            }

                            _unreadReactions[key] = item;
                        }
                    }
                }

                return _unreadReactions;
            }
        }

        public void UpdateUnreadReactions(IList<UnreadReaction> value)
        {
            _message.UnreadReactions = value;
            _unreadReactions = null;
        }

        public ReplyMarkup ReplyMarkup { get => _message.ReplyMarkup; set => _message.ReplyMarkup = value; }
        public MessageContent Content { get => _message.Content; set => _message.Content = value; }
        public long MediaAlbumId => _message.MediaAlbumId;
        public MessageInteractionInfo InteractionInfo { get => _message.InteractionInfo; set => _message.InteractionInfo = value; }
        public string AuthorSignature => _message.AuthorSignature;
        public long ViaBotUserId => _message.ViaBotUserId;
        public double TtlExpiresIn { get => _message.SelfDestructIn; set => _message.SelfDestructIn = value; }
        public int Ttl => (_message.SelfDestructType as MessageSelfDestructTypeTimer)?.SelfDestructTime ?? 0;
        public long ReplyToMessageId => (_message.ReplyTo as MessageReplyToMessage)?.MessageId ?? 0;
        public long ReplyInChatId => (_message.ReplyTo as MessageReplyToMessage)?.ChatId ?? 0;
        public TextQuote ReplyToQuote => (_message.ReplyTo as MessageReplyToMessage)?.Quote;
        public MessageForwardInfo ForwardInfo => _message.ForwardInfo;
        public MessageImportInfo ImportInfo => _message.ImportInfo;
        public int EditDate { get => _message.EditDate; set => _message.EditDate = value; }
        public int Date => _message.Date;
        public bool ContainsUnreadMention { get => _message.ContainsUnreadMention; set => _message.ContainsUnreadMention = value; }
        public bool IsChannelPost => _message.IsChannelPost;
        public bool CanBeDeletedForAllUsers => _message.CanBeDeletedForAllUsers;
        public bool CanBeDeletedOnlyForSelf => _message.CanBeDeletedOnlyForSelf;
        public bool CanBeForwarded => _message.CanBeForwarded;
        public bool CanBeEdited => _message.CanBeEdited;
        public bool CanBeSaved => _message.CanBeSaved;
        public bool CanGetMessageThread => _message.CanGetMessageThread;
        public bool CanGetStatistics => _message.CanGetStatistics;
        public bool CanGetViewers => _message.CanGetViewers;
        public bool IsOutgoing { get => _message.IsOutgoing; set => _message.IsOutgoing = value; }
        public bool IsPinned { get => _message.IsPinned; set => _message.IsPinned = value; }
        public MessageSchedulingState SchedulingState => _message.SchedulingState;
        public MessageSendingState SendingState => _message.SendingState;
        public long ChatId => _message.ChatId;
        public long MessageThreadId => _message.MessageThreadId;
        public MessageSender SenderId => _message.SenderId;
        public long Id => _message.Id;

        public Photo GetPhoto() => _message.GetPhoto();
        public File GetAnimation() => _message.GetAnimation();

        public bool IsService() => _message.IsService();
        public bool IsSaved() => _message.IsSaved(_protoService.Options.MyId);
        public bool IsSecret() => _message.IsSecret();

        public MessageViewModel ReplyToMessage { get; set; }
        public ReplyToMessageState ReplyToMessageState { get; set; } = ReplyToMessageState.None;

        /// <summary>
        /// This is used for additional content that's generated by the app
        /// </summary>
        public MessageContent GeneratedContent { get; set; }
        public bool GeneratedContentUnread { get; set; }

        public BaseObject GetSender()
        {
            if (_message.SenderId is MessageSenderUser user)
            {
                return ProtoService.GetUser(user.UserId);
            }
            else if (_message.SenderId is MessageSenderChat chat)
            {
                return ProtoService.GetChat(chat.ChatId);
            }

            return null;
        }

        public User GetViaBotUser()
        {
            if (_message.ViaBotUserId != 0)
            {
                return ProtoService.GetUser(_message.ViaBotUserId);
            }

            if (ProtoService.TryGetUser(_message.SenderId, out User user) && user.Type is UserTypeBot)
            {
                return user;
            }

            return null;
        }

        public Chat GetChat()
        {
            return ProtoService.GetChat(_message.ChatId);
        }

        public Message Get()
        {
            return _message;
        }

        public void Replace(Message message)
        {
            _message = message;
        }

        public bool UpdateFile(File file)
        {
            var message = _message.UpdateFile(file);
            var generated = UpdateGeneratedFile(file);

            var reply = ReplyToMessage;
            if (reply != null)
            {
                return reply.UpdateFile(file) || message;
            }

            return message || generated;
        }

        private bool UpdateGeneratedFile(File file)
        {
            switch (GeneratedContent)
            {
                case MessageAlbum album:
                    return album.UpdateFile(file);
                case MessageAnimation animation:
                    return animation.UpdateFile(file);
                case MessageAudio audio:
                    return audio.UpdateFile(file);
                case MessageDocument document:
                    return document.UpdateFile(file);
                case MessageGame game:
                    return game.UpdateFile(file);
                case MessageInvoice invoice:
                    return invoice.UpdateFile(file);
                case MessagePhoto photo:
                    return photo.UpdateFile(file);
                case MessageSticker sticker:
                    return sticker.UpdateFile(file);
                case MessageText text:
                    return text.UpdateFile(file);
                case MessageVideo video:
                    return video.UpdateFile(file);
                case MessageVideoNote videoNote:
                    return videoNote.UpdateFile(file);
                case MessageVoiceNote voiceNote:
                    return voiceNote.UpdateFile(file);
                case MessageChatChangePhoto chatChangePhoto:
                    return chatChangePhoto.UpdateFile(file);
                default:
                    return false;
            }
        }

        public bool IsShareable()
        {
            var message = this;
            if (message.SchedulingState != null)
            {
                return false;
            }
            //if (currentPosition != null && !currentPosition.last)
            //{
            //    return false;
            //}
            //else if (messageObject.eventId != 0)
            //{
            //    return false;
            //}
            //else if (messageObject.messageOwner.fwd_from != null && !messageObject.isOutOwner() && messageObject.messageOwner.fwd_from.saved_from_peer != null && messageObject.getDialogId() == UserConfig.getInstance(currentAccount).getClientUserId())
            //{
            //    drwaShareGoIcon = true;
            //    return true;
            //}
            //else 
            if (message.Content is MessageSticker)
            {
                return false;
            }
            //else if (messageObject.messageOwner.fwd_from != null && messageObject.messageOwner.fwd_from.channel_id != 0 && !messageObject.isOutOwner())
            //{
            //    return true;
            //}
            else if (message.SenderId is MessageSenderUser)
            {
                if (message.Content is MessageText)
                {
                    return false;
                }

                if (ProtoService.TryGetUser(message.SenderId, out User user) && user.Type is UserTypeBot)
                {
                    return true;
                }
                if (!message.IsOutgoing)
                {
                    if (message.Content is MessageGame || message.Content is MessageInvoice)
                    {
                        return true;
                    }

                    var chat = message.ProtoService.GetChat(message.ChatId);
                    if (chat != null && chat.Type is ChatTypeSupergroup super && !super.IsChannel)
                    {
                        var supergroup = message.ProtoService.GetSupergroup(super.SupergroupId);
                        return supergroup != null && supergroup.ActiveUsername().Length > 0 && !(message.Content is MessageContact) && !(message.Content is MessageLocation);
                    }
                }
            }
            //else if (messageObject.messageOwner.from_id < 0 || messageObject.messageOwner.post)
            //{
            //    if (messageObject.messageOwner.to_id.channel_id != 0 && (messageObject.messageOwner.via_bot_id == 0 && messageObject.messageOwner.reply_to_msg_id == 0 || messageObject.type != 13))
            //    {
            //        return true;
            //    }
            //}
            else if (message.IsChannelPost)
            {
                if (message.ViaBotUserId == 0 && message.ReplyToMessageId == 0 || !(message.Content is MessageSticker))
                {
                    return true;
                }
            }

            return false;
        }

        public override bool Equals(object obj)
        {
            if (obj is Message y)
            {
                return Id == y.Id && ChatId == y.ChatId;
            }

            return base.Equals(obj);
        }

        public int AnimationHash()
        {
            return base.GetHashCode();
        }

        public override int GetHashCode()
        {
            return Id.GetHashCode() ^ ChatId.GetHashCode();
        }

        public void UpdateWith(MessageViewModel message)
        {
            UpdateWith(message.Get());
        }

        public void UpdateWith(Message message)
        {
            _message.AuthorSignature = message.AuthorSignature;
            _message.CanBeDeletedForAllUsers = message.CanBeDeletedForAllUsers;
            _message.CanBeDeletedOnlyForSelf = message.CanBeDeletedOnlyForSelf;
            _message.CanBeEdited = message.CanBeEdited;
            _message.CanBeSaved = message.CanBeSaved;
            _message.CanBeForwarded = message.CanBeForwarded;
            _message.CanGetMessageThread = message.CanGetMessageThread;
            _message.CanGetStatistics = message.CanGetStatistics;
            _message.CanGetViewers = message.CanGetViewers;
            _message.ChatId = message.ChatId;
            _message.ContainsUnreadMention = message.ContainsUnreadMention;
            UpdateUnreadReactions(message.UnreadReactions);
            //_message.Content = message.Content;
            //_message.Date = message.Date;
            _message.EditDate = message.EditDate;
            _message.ForwardInfo = message.ForwardInfo;
            _message.Id = message.Id;
            _message.IsChannelPost = message.IsChannelPost;
            _message.IsOutgoing = message.IsOutgoing;
            _message.IsPinned = message.IsPinned;
            _message.MessageThreadId = message.MessageThreadId;
            _message.MediaAlbumId = message.MediaAlbumId;
            _message.ReplyMarkup = message.ReplyMarkup;
            _message.ReplyTo = message.ReplyTo;
            _message.SenderId = message.SenderId;
            _message.SendingState = message.SendingState;
            _message.SelfDestructType = message.SelfDestructType;
            _message.SelfDestructIn = message.SelfDestructIn;
            _message.ViaBotUserId = message.ViaBotUserId;
            _message.InteractionInfo = message.InteractionInfo;

            if (_message.Content is MessageAlbum album)
            {
                FormattedText caption = null;

                if (album.IsMedia)
                {
                    foreach (var child in album.Messages)
                    {
                        var childCaption = child.Content?.GetCaption();
                        if (childCaption != null && !string.IsNullOrEmpty(childCaption.Text))
                        {
                            if (caption == null || string.IsNullOrEmpty(caption.Text))
                            {
                                caption = childCaption;
                            }
                            else
                            {
                                caption = null;
                                break;
                            }
                        }
                    }
                }
                else if (album.Messages.Count > 0)
                {
                    caption = album.Messages[album.Messages.Count - 1].Content.GetCaption();
                }

                album.Caption = caption ?? new FormattedText();
            }
        }
    }

    public enum ReplyToMessageState
    {
        None,
        Loading,
        Deleted,
        Hidden
    }
}
