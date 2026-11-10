using Telegram.Td.Api;
using Unigram.Common;
using Unigram.Controls;
using Unigram.Logs;
using Unigram.Services;
using Unigram.ViewModels.Supergroups;

namespace Unigram.ViewModels.Channels
{
    public class ChannelCreateStep2ViewModel : SupergroupEditViewModelBase
    {
        public ChannelCreateStep2ViewModel(IProtoService protoService, ICacheService cacheService, ISettingsService settingsService, IEventAggregator aggregator)
            : base(protoService, cacheService, settingsService, aggregator)
        {
        }

        protected override async void SendExecute()
        {
            var chat = _chat;
            if (chat == null)
            {
                return;
            }

            if (chat.Type is ChatTypeSupergroup supergroup)
            {
                var item = ProtoService.GetSupergroup(supergroup.SupergroupId);

                if (item == null)
                {
                    PushDiagnostics.Write("channel.create", "result=supergroup_unavailable");
                    await MessagePopup.ShowAsync(Strings.Resources.ErrorOccurred, Strings.Resources.AppName, Strings.Resources.OK);
                    return;
                }

                var username = _isPublic ? _username?.Trim() ?? string.Empty : string.Empty;

                if (!string.Equals(username, item.GetUsername()))
                {
                    var response = await ProtoService.SendAsync(new SetSupergroupUsername(item.Id, username));
                    if (response is Error error)
                    {
                        if (error.TypeEquals(ErrorType.CHANNELS_ADMIN_PUBLIC_TOO_MUCH))
                        {
                            HasTooMuchUsernames = true;
                            LoadAdminedPublicChannels();
                        }
                        // TODO:

                        return;
                    }
                }

                await MessagePopup.ShowAsync(
                    Strings.Resources.ActionCreateChannel,
                    chat.Title,
                    Strings.Resources.OpenChannel);

                NavigationService.NavigateToChat(chat);
                NavigationService.GoBackAt(0, false);
                //NavigationService.Navigate(typeof(ChannelCreateStep3Page), chat.Id);
            }
        }
    }
}
