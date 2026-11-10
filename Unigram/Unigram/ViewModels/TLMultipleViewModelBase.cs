using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Unigram.Navigation;
using Unigram.Navigation.Services;
using Unigram.Services;
using Windows.UI.Xaml.Navigation;

namespace Unigram.ViewModels
{
    public class TLMultipleViewModelBase : TLViewModelBase
    {
        public readonly List<TLViewModelBase> Children;

        public TLMultipleViewModelBase(IProtoService protoService, ICacheService cacheService, ISettingsService settingsService, IEventAggregator aggregator)
            : base(protoService, cacheService, settingsService, aggregator)
        {
            Children = new List<TLViewModelBase>();
        }

        public override IDispatcherWrapper Dispatcher
        {
            get
            {
                return base.Dispatcher;
            }
            set
            {
                base.Dispatcher = value;

                foreach (var child in Children)
                {
                    child.Dispatcher = value;
                }
            }
        }

        public override INavigationService NavigationService
        {
            get
            {
                return base.NavigationService;
            }
            set
            {
                base.NavigationService = value;

                foreach (var child in Children)
                {
                    child.NavigationService = value;
                }
            }
        }

        public override IDictionary<string, object> SessionState
        {
            get
            {
                return base.SessionState;
            }
            set
            {
                base.SessionState = value;

                foreach (var child in Children)
                {
                    child.SessionState = value;
                }
            }
        }

        public override async Task OnNavigatedFromAsync(IDictionary<string, object> pageState, bool suspending)
        {
            await base.OnNavigatedFromAsync(pageState, suspending);
            await Task.WhenAll(Children.Select(x => x.OnNavigatedFromAsync(pageState, suspending)));
        }

        public override async Task OnNavigatedToAsync(object parameter, NavigationMode mode, IDictionary<string, object> state)
        {
            await base.OnNavigatedToAsync(parameter, mode, state);
#if MODERN_TDLIB
            // Task.WhenAll reports only the first fault and loses which child produced it,
            // and the caller is async void, so the failure arrives as a bare unhandled
            // exception. Start every child as before, then await them individually so the
            // faulting view model is named.
            var pending = new List<Task>();

            foreach (var child in Children)
            {
                try
                {
                    pending.Add(child.OnNavigatedToAsync(parameter, mode, state));
                }
                catch (System.Exception ex)
                {
                    Logs.PushDiagnostics.WriteException($"children.navigated.{child.GetType().Name}", ex);
                    throw;
                }
            }

            for (int i = 0; i < pending.Count; i++)
            {
                try
                {
                    await pending[i];
                }
                catch (System.Exception ex)
                {
                    Logs.PushDiagnostics.WriteException($"children.navigated.{Children[i].GetType().Name}", ex);
                    throw;
                }
            }

            Logs.PushDiagnostics.Write("children.navigated", $"count={pending.Count};result=ok");
#else
            await Task.WhenAll(Children.Select(x => x.OnNavigatedToAsync(parameter, mode, state)));
#endif
        }

        public override void OnNavigatingFrom(NavigatingEventArgs args)
        {
            base.OnNavigatingFrom(args);
            Children.ForEach(x => x.OnNavigatingFrom(args));
        }
    }

    public interface IChildViewModel
    {
        void Activate();
        void Deactivate();
    }
}
