using System;
using System.Threading.Tasks;
using Telegram.Td.Api;
using Unigram.Common;
using Unigram.Controls;
using Unigram.Navigation;
using Unigram.Services;
using Unigram.Views.Popups;

namespace Unigram.ViewModels
{
    /// <summary>
    /// Base class for ViewModel
    /// </summary>
    public class TLViewModelBase : ViewModelBase
    {
        private readonly IProtoService _protoService;
        private readonly ICacheService _cacheService;
        private readonly ISettingsService _settingsService;
        private readonly IEventAggregator _aggregator;

        public TLViewModelBase(IProtoService protoService, ICacheService cacheService, ISettingsService settingsService, IEventAggregator aggregator)
        {
            _protoService = protoService;
            _cacheService = cacheService;
            _settingsService = settingsService;
            _aggregator = aggregator;
        }

        //public override IDispatcherWrapper Dispatcher
        //{
        //    get
        //    {
        //        return _dispatcher;
        //    }
        //    set
        //    {

        //    }
        //}

        public IProtoService ProtoService => _protoService;
        public ICacheService CacheService => _cacheService;

        public ISettingsService Settings => _settingsService;

        public IEventAggregator Aggregator => _aggregator;

        public int SessionId => _protoService.SessionId;

        protected async Task<bool> ShowAddChatMemberResultAsync(BaseObject response, User user, bool isChannel)
        {
            if (response is FailedToAddMembers failedToAddMembers)
            {
                if (failedToAddMembers.FailedToAddMembersValue == null ||
                    failedToAddMembers.FailedToAddMembersValue.Count == 0)
                {
                    await MessagePopup.ShowAsync(
                        Strings.Resources.ActionYouAddUser.Replace("un2", user.GetFullName()),
                        Strings.Resources.AppName,
                        Strings.Resources.OK);
                    return true;
                }

                await MessagePopup.ShowAsync(
                    isChannel ? Strings.Resources.InviteToChannelError : Strings.Resources.InviteToGroupError,
                    Strings.Resources.AppName,
                    Strings.Resources.OK);
            }
            else if (response is Error error)
            {
                AlertsService.ShowAddUserAlert(Dispatcher, error.Message, isChannel);
            }
            else
            {
                await MessagePopup.ShowAsync(Strings.Resources.ErrorOccurred, Strings.Resources.AppName, Strings.Resources.OK);
            }

            return false;
        }

        private bool _isLoading;
        public virtual bool IsLoading
        {
            get
            {
                return _isLoading;
            }
            set
            {
                Set(ref _isLoading, value);
            }
        }

        protected virtual void BeginOnUIThread(Action action, Action fallback = null)
        {
            var dispatcher = Dispatcher;
            if (dispatcher == null)
            {
                dispatcher = WindowContext.Default()?.Dispatcher;
            }

            if (dispatcher != null)
            {
                dispatcher.Dispatch(action);
            }
            else if (fallback != null)
            {
                fallback();
            }
            else
            {
                try
                {
                    action();
                }
                catch { }
            }
        }
    }
}
