using Telegram.Td.Api;
using Unigram.Common;
using Unigram.Controls;
using Unigram.Logs;
using Unigram.Services;
using Unigram.Views.Channels;
using Windows.Storage;
using Windows.UI.Xaml.Media.Imaging;

namespace Unigram.ViewModels.Channels
{
    public class ChannelCreateStep1ViewModel : TLViewModelBase
    {
        public ChannelCreateStep1ViewModel(IProtoService protoService, ICacheService cacheService, ISettingsService settingsService, IEventAggregator aggregator)
            : base(protoService, cacheService, settingsService, aggregator)
        {
            SendCommand = new RelayCommand(SendExecute, CanSend);
            EditPhotoCommand = new RelayCommand<StorageFile>(EditPhotoExecute);
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

        private string _about;
        public string About
        {
            get
            {
                return _about;
            }
            set
            {
                Set(ref _about, value);
            }
        }

        private BitmapImage _preview;
        public BitmapImage Preview
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

        public RelayCommand SendCommand { get; }
        private bool CanSend()
        {
            return !_isCreating && !string.IsNullOrWhiteSpace(Title);
        }

        private async void SendExecute()
        {
            if (_isCreating)
            {
                return;
            }

            _isCreating = true;
            SendCommand.RaiseCanExecuteChanged();

            try
            {
                var response = await ProtoService.SendAsync(
                    ModernTdlibCompatibility.CreateNewSupergroupChat(_title, true, _about ?? string.Empty, null, false));
                if (response is Chat chat)
                {
                    // Keep the creation pages on the back stack until Step 2 completes.
                    // Step 2 uses GoBackAt to remove the whole wizard after it has
                    // acknowledged creation and opened the channel.
                    NavigationService.Navigate(typeof(ChannelCreateStep2Page), chat.Id);
                }
                else if (response is Error error)
                {
                    PushDiagnostics.Write("channel.create", $"result=error;code={error.Code}");
                    await MessagePopup.ShowAsync(Strings.Resources.ErrorOccurred, Strings.Resources.AppName, Strings.Resources.OK);
                }
                else
                {
                    PushDiagnostics.Write("channel.create", $"result=unexpected_response;type={response?.GetType().Name ?? "null"}");
                    await MessagePopup.ShowAsync(Strings.Resources.ErrorOccurred, Strings.Resources.AppName, Strings.Resources.OK);
                }
            }
            finally
            {
                _isCreating = false;
                SendCommand.RaiseCanExecuteChanged();
            }
        }

        public RelayCommand<StorageFile> EditPhotoCommand { get; }
        private async void EditPhotoExecute(StorageFile file)
        {
            Logs.Logger.Warning(Logs.Target.API, "Call to not implemented EditPhotoExecute (ChannelCreateStep1)");
            await System.Threading.Tasks.Task.CompletedTask;
        }
    }
}
