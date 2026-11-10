using System;
using System.Linq;
using System.Threading.Tasks;
using Telegram.Td.Api;
using Unigram.Collections;
using Unigram.Common;
using Unigram.Controls;
using Unigram.Entities;
using Unigram.Logs;
using Unigram.Services;
using Unigram.Views.Popups;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI.Xaml.Media;
using static Unigram.Services.GenerationService;

namespace Unigram.ViewModels.BasicGroups
{
    public class BasicGroupCreateStep1ViewModel : TLViewModelBase
    {
        public BasicGroupCreateStep1ViewModel(IProtoService protoService, ICacheService cacheService, ISettingsService settingsService, IEventAggregator aggregator)
            : base(protoService, cacheService, settingsService, aggregator)
        {
            Items = new MvxObservableCollection<Chat>();

            AddCommand = new RelayCommand(AddExecute);
            SendCommand = new RelayCommand(SendExecute, CanSend);
            EditPhotoCommand = new RelayCommand<StorageMedia>(EditPhotoExecute);
            ConfigureMessageAutoDeleteTimeCommand = new RelayCommand(ConfigureMessageAutoDeleteTimeExecute);
        }

        private bool _isCreating;

        private string _title;
        public string Title
        {
            get
            {
                return _title;
            }
            set
            {
                Set(ref _title, value);
                SendCommand.RaiseCanExecuteChanged();
            }
        }

        private ImageSource _preview;
        public ImageSource Preview
        {
            get
            {
                return _preview;
            }
            set
            {
                Set(ref _preview, value);
            }
        }

        private StoragePhoto _photo;

        private int _messageAutoDeleteTime;
        public int MessageAutoDeleteTime
        {
            get
            {
                return _messageAutoDeleteTime;
            }
            set
            {
                if (Set(ref _messageAutoDeleteTime, value))
                {
                    RaisePropertyChanged(nameof(MessageAutoDeleteTimeText));
                }
            }
        }

        public string MessageAutoDeleteTimeText =>
            MessageAutoDeleteTime == 0
                ? Strings.Resources.ShortMessageLifetimeForever
                : Locale.FormatTtl(MessageAutoDeleteTime);

        public MvxObservableCollection<Chat> Items { get; private set; }

        public RelayCommand AddCommand { get; }
        private async void AddExecute()
        {
            var chats = await SharePopup.PickChatsAsync(Strings.Resources.SelectContacts, Items.Select(x => x.Id).ToArray());
            if (chats != null)
            {
                Items.ReplaceWith(chats);
            }

            SendCommand.RaiseCanExecuteChanged();
        }

        public RelayCommand SendCommand { get; }
        private bool CanSend()
        {
            return !_isCreating && !string.IsNullOrWhiteSpace(Title) && Items.Count > 0;
        }

        private async void SendExecute()
        {
            if (_isCreating)
            {
                return;
            }

            _isCreating = true;
            SendCommand.RaiseCanExecuteChanged();

            var maxSize = CacheService.Options.BasicGroupSizeMax;

            try
            {
                var peers = Items.Select(x => x.Type).OfType<ChatTypePrivate>().Select(x => x.UserId).ToArray();
                if (peers.Length <= maxSize)
                {
                    var response = await ProtoService.SendAsync(
                        ModernTdlibCompatibility.CreateNewBasicGroupChat(peers, _title, MessageAutoDeleteTime));
#if MODERN_TDLIB
                    if (response is CreatedBasicGroupChat created)
                    {
                        var chat = await ProtoService.SendAsync(new GetChat(created.ChatId)) as Chat;
                        if (chat != null)
                        {
                            await OfferInviteLinkAsync(chat, created.FailedToAddMembers);
                            await CompleteCreationAsync(chat);
                        }
                        else
                        {
                            PushDiagnostics.Write("group.create", "result=created_chat_unavailable");
                            await MessagePopup.ShowAsync(Strings.Resources.ErrorOccurred, Strings.Resources.AppName, Strings.Resources.OK);
                        }
                    }
#else
                    if (response is Chat chat)
                    {
                        await CompleteCreationAsync(chat);
                    }
#endif
                    else if (response is Error error)
                    {
                        AlertsService.ShowAddUserAlert(Dispatcher, error.Message, false);
                    }
                    else
                    {
                        PushDiagnostics.Write("group.create", $"result=unexpected_response;type={response?.GetType().Name ?? "null"}");
                        await MessagePopup.ShowAsync(Strings.Resources.ErrorOccurred, Strings.Resources.AppName, Strings.Resources.OK);
                    }
                }
                else
                {
                    await MessagePopup.ShowAsync(Strings.Resources.ErrorOccurred, Strings.Resources.AppName, Strings.Resources.OK);
                }
            }
            finally
            {
                _isCreating = false;
                SendCommand.RaiseCanExecuteChanged();
            }
        }

        private async Task CompleteCreationAsync(Chat chat)
        {
            await UploadPhotoAsync(chat);

            await MessagePopup.ShowAsync(
                Strings.Resources.ActionYouCreateGroup,
                chat.Title,
                Strings.Resources.OpenGroup);

            NavigationService.NavigateToChat(chat);
            NavigationService.GoBackAt(0, false);
        }

        private async Task UploadPhotoAsync(Chat chat)
        {
            if (_photo == null)
            {
                return;
            }

            var generated = await _photo.File.ToGeneratedAsync(
                ConversionType.Compress,
                Newtonsoft.Json.JsonConvert.SerializeObject(_photo.EditState));
            var response = await ProtoService.SendAsync(new SetChatPhoto(chat.Id, new InputChatPhotoStatic(generated)));
            if (response is Error error)
            {
                PushDiagnostics.Write("group.create", $"result=photo_failed;code={error.Code}");
                await MessagePopup.ShowAsync(Strings.Resources.ErrorOccurred, Strings.Resources.AppName, Strings.Resources.OK);
            }
        }

#if MODERN_TDLIB
        private async Task OfferInviteLinkAsync(Chat chat, FailedToAddMembers failedToAddMembers)
        {
            if (failedToAddMembers?.FailedToAddMembersValue == null ||
                failedToAddMembers.FailedToAddMembersValue.Count == 0)
            {
                return;
            }

            PushDiagnostics.Write(
                "group.create",
                $"result=members_not_added;count={failedToAddMembers.FailedToAddMembersValue.Count}");

            var confirmation = await MessagePopup.ShowAsync(
                Strings.Resources.InviteToGroupError,
                Strings.Resources.InviteToGroupByLink,
                Strings.Resources.CopyLink,
                Strings.Resources.Close);
            if (confirmation != Windows.UI.Xaml.Controls.ContentDialogResult.Primary)
            {
                return;
            }

            var response = await ProtoService.SendAsync(
                new CreateChatInviteLink(chat.Id, string.Empty, 0, 0, false));
            if (response is ChatInviteLink inviteLink)
            {
                var dataPackage = new DataPackage();
                dataPackage.SetText(inviteLink.InviteLink);
                ClipboardEx.TrySetContent(dataPackage);

                await MessagePopup.ShowAsync(
                    Strings.Resources.LinkCopied,
                    Strings.Resources.AppName,
                    Strings.Resources.OK);
            }
            else if (response is Error error)
            {
                PushDiagnostics.Write("group.create", $"result=invite_link_failed;code={error.Code}");
                await MessagePopup.ShowAsync(Strings.Resources.ErrorOccurred, Strings.Resources.AppName, Strings.Resources.OK);
            }
            else
            {
                PushDiagnostics.Write(
                    "group.create",
                    $"result=invite_link_unexpected;type={response?.GetType().Name ?? "null"}");
                await MessagePopup.ShowAsync(Strings.Resources.ErrorOccurred, Strings.Resources.AppName, Strings.Resources.OK);
            }
        }
#endif

        public RelayCommand<StorageMedia> EditPhotoCommand { get; }
        private async void EditPhotoExecute(StorageMedia media)
        {
            if (media is StoragePhoto photo)
            {
                _photo = photo;
                Preview = await ImageHelper.CropAndPreviewAsync(photo.File, photo.EditState);
            }
        }

        public RelayCommand ConfigureMessageAutoDeleteTimeCommand { get; }
        private async void ConfigureMessageAutoDeleteTimeExecute()
        {
            var dialog = new ChatTtlPopup(false)
            {
                Value = MessageAutoDeleteTime
            };

            var result = await dialog.ShowQueuedAsync();
            if (result == Windows.UI.Xaml.Controls.ContentDialogResult.Primary)
            {
                MessageAutoDeleteTime = dialog.Value;
            }
        }

        private void ContinueUploadingPhoto()
        {
            //NavigationService.Navigate(typeof(BasicGroupCreateStep2Page), new ChatCreateStep2Tuple(_title, null));
        }
    }
}
