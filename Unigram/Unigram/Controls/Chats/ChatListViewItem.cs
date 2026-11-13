using System;
using System.Numerics;
using Telegram.Td.Api;
using Unigram.Common;
using Unigram.Controls.Messages;
using Unigram.Services;
using Unigram.ViewModels;
using Windows.Foundation;
using Windows.UI.Composition;
using Windows.UI.Composition.Interactions;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation.Peers;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Hosting;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace Unigram.Controls.Chats
{
    public class ChatListViewItem : LazoListViewItem, IInteractionTrackerOwner
    {
        private readonly ChatListView _parent;

        private const float SwipeTrigger = 56;
        private const float SwipeFlickDistance = 24;
        private const float SwipeFlickVelocity = 600;

        private SpriteVisual _hitTest;
        private ContainerVisual _container;
        private Visual _visual;
        private ContainerVisual _indicator;

        private bool _hasInitialLoadedEventFired;
        private InteractionTracker _tracker;
        private VisualInteractionSource _interactionSource;

        private bool _forward;
        private bool _reply;

        private ListViewItemPresenter _presenter;

        public ChatListViewItem(ChatListView parent)
            : base(parent)
        {
            _parent = parent;

            AddHandler(PointerPressedEvent, new PointerEventHandler(OnPointerPressed), true);
        }

        protected override AutomationPeer OnCreateAutomationPeer()
        {
            return new ChatListViewAutomationPeer(this);
        }

        protected override void OnApplyTemplate()
        {
            base.OnApplyTemplate();

            _presenter = GetTemplateChild("Presenter") as ListViewItemPresenter;

            DetachEventHandlers();
            AttachEventHandlers();
        }

        private void AttachEventHandlers()
        {
            Loaded += OnLoaded;
            SizeChanged += OnSizeChanged;
        }

        private void DetachEventHandlers()
        {
            Loaded -= OnLoaded;
            SizeChanged -= OnSizeChanged;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (!_hasInitialLoadedEventFired)
            {
                _visual = ElementCompositionPreview.GetElementVisual(_presenter);

                _hitTest = _visual.Compositor.CreateSpriteVisual();
                _hitTest.Brush = _visual.Compositor.CreateColorBrush(Windows.UI.Colors.Transparent);

                _container = _visual.Compositor.CreateContainerVisual();
                _container.Children.InsertAtBottom(_hitTest);
                _container.Size = _hitTest.Size = new Vector2((float)ActualWidth, (float)ActualHeight);

                ElementCompositionPreview.SetElementChildVisual(this, _container);

                ConfigureInteractionTracker();
            }

            _hasInitialLoadedEventFired = true;
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_hitTest != null)
            {
                _hitTest.Size = _container.Size = e.NewSize.ToVector2();
            }
        }

        private void ConfigureInteractionTracker()
        {
            _interactionSource = VisualInteractionSource.Create(_hitTest);

            //Configure for y-direction panning
            _interactionSource.ManipulationRedirectionMode = VisualInteractionSourceRedirectionMode.CapableTouchpadOnly;
            _interactionSource.PositionXSourceMode = InteractionSourceMode.EnabledWithInertia;
            _interactionSource.PositionXChainingMode = InteractionChainingMode.Never;
            _interactionSource.IsPositionXRailsEnabled = true;

            //Create tracker and associate interaction source
            _tracker = InteractionTracker.CreateWithOwner(_visual.Compositor, this);
            _tracker.InteractionSources.Add(_interactionSource);

            _tracker.MaxPosition = new Vector3(_reply ? 72 : 0);
            _tracker.MinPosition = new Vector3(_forward ? -72 : 0);

            _tracker.Properties.InsertBoolean("CanReply", _reply);
            _tracker.Properties.InsertBoolean("CanForward", _forward);

            ConfigureAnimations(_visual, null);
            ConfigureRestingPoints();
        }

        private void ConfigureRestingPoints()
        {
            var neutralX = InteractionTrackerInertiaRestingValue.Create(_visual.Compositor);
            neutralX.Condition = _visual.Compositor.CreateExpressionAnimation("true");
            neutralX.RestingValue = _visual.Compositor.CreateExpressionAnimation("0");

            _tracker.ConfigurePositionXInertiaModifiers(new InteractionTrackerInertiaModifier[] { neutralX });
        }

        private void ConfigureAnimations(Visual visual, Visual indicator)
        {
            // Create an animation that changes the offset of the photoVisual and shadowVisual based on the manipulation progress
            var offsetExp = _visual.Compositor.CreateExpressionAnimation("(tracker.Position.X > 0 && !tracker.CanReply) || (tracker.Position.X <= 0 && !tracker.CanForward) ? 0 : Max(-72, Min(72, -tracker.Position.X))");
            //var photoOffsetExp = _visual.Compositor.CreateExpressionAnimation("tracker.Position.X > 0 && !tracker.CanReply || tracker.Position.X <= 0 && !tracker.CanForward ? 0 : Max(-72, Min(72, -tracker.Position.X))");
            //var photoOffsetExp = _visual.Compositor.CreateExpressionAnimation("-tracker.Position.X");
            offsetExp.SetReferenceParameter("tracker", _tracker);
            visual.StartAnimation("Offset.X", offsetExp);
        }

        #region ContentMargin

        public Thickness ContentMargin
        {
            get { return (Thickness)GetValue(ContentMarginProperty); }
            set { SetValue(ContentMarginProperty, value); }
        }

        public static readonly DependencyProperty ContentMarginProperty =
            DependencyProperty.Register("ContentMargin", typeof(Thickness), typeof(ChatListViewItem), new PropertyMetadata(default(Thickness)));

        #endregion

        private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (e.Pointer.PointerDeviceType == Windows.Devices.Input.PointerDeviceType.Touch || e.Pointer.PointerDeviceType == Windows.Devices.Input.PointerDeviceType.Pen)
            {
                try
                {
                    _interactionSource.TryRedirectForManipulation(e.GetCurrentPoint(this));
                }
                catch (Exception)
                {
                    // Ignoring the failed redirect to prevent app crashing
                }
            }
        }

        protected override void OnPointerPressed(PointerRoutedEventArgs e)
        {
            if (_parent.SelectionMode == ListViewSelectionMode.Multiple && !IsSelected)
            {
                e.Handled = CantSelect();
            }

            base.OnPointerPressed(e);
        }

        public override bool CantSelect()
        {
            return ContentTemplateRoot is FrameworkElement element && element.Tag is MessageViewModel message && message.IsService();
        }

        public void PrepareForItemOverride(MessageViewModel message)
        {
            _reply = CanReply();
            _forward = CanForward();

            if (_tracker != null)
            {
                _tracker.Properties.InsertBoolean("CanReply", _reply);
                _tracker.Properties.InsertBoolean("CanForward", _forward);
                _tracker.MaxPosition = new Vector3(_reply ? 72 : 0);
                _tracker.MinPosition = new Vector3(_forward ? -72 : 0);

                if (_tracker.Position.X != 0)
                {
                    _tracker.TryUpdatePosition(new Vector3());
                }

                if (_visual != null && _visual.Offset.X != 0)
                {
                    _visual.Offset = new Vector3();
                }
            }
        }

        private bool CanReply()
        {
            if (ContentTemplateRoot is FrameworkElement element && element.Tag is MessageViewModel message)
            {
                if (message.IsService())
                {
                    return false;
                }

                var chat = message.GetChat();
                if (chat != null && chat.Type is ChatTypeSupergroup supergroupType)
                {
                    var supergroup = _parent.ViewModel.ProtoService.GetSupergroup(supergroupType.SupergroupId);
                    if (supergroup.IsChannel)
                    {
                        return supergroup.Status is ChatMemberStatusCreator || supergroup.Status is ChatMemberStatusAdministrator;
                    }
                    else if (supergroup.Status is ChatMemberStatusRestricted restricted)
                    {
                        return restricted.Permissions.GetCanSendMessages();
                    }
                }
                else
                {
                    return chat.Permissions.GetCanSendMessages();
                }

                return true;
            }

            return false;
        }

        private bool CanForward()
        {
            if (ContentTemplateRoot is FrameworkElement element && element.Tag is MessageViewModel message)
            {
#if MODERN_TDLIB
                if (message.IsService())
                {
                    return false;
                }

                if (!message.HasMessageProperties)
                {
                    LoadCanForward(message);
                    return false;
                }
#endif
                return message.CanBeForwarded;
            }

            return false;
        }

#if MODERN_TDLIB
        private async void LoadCanForward(MessageViewModel message)
        {
            if (await message.LoadMessagePropertiesAsync() && ContentTemplateRoot is FrameworkElement element && element.Tag == message)
            {
                _forward = message.CanBeForwarded;

                if (_tracker != null)
                {
                    _tracker.Properties.InsertBoolean("CanForward", _forward);
                    _tracker.MinPosition = new Vector3(_forward ? -72 : 0);
                }
            }
        }
#endif

        public void ValuesChanged(InteractionTracker sender, InteractionTrackerValuesChangedArgs args)
        {
            if (_indicator == null && (_tracker.Position.X > 0.0001f || _tracker.Position.X < -0.0001f))
            {
                _indicator = CreateIndicator();
                _container.Children.InsertAtTop(_indicator);
            }

            var offset = (_tracker.Position.X > 0 && !_reply) || (_tracker.Position.X <= 0 && !_forward) ? 0 : Math.Max(0, Math.Min(SwipeTrigger, Math.Abs(_tracker.Position.X)));

            var abs = Math.Abs(offset);
            var percent = abs / SwipeTrigger;

            var width = (float)ActualWidth;
            var height = (float)ActualHeight;

            if (_indicator != null)
            {
                _indicator.Offset = new Vector3(_tracker.Position.X > 0 ? width - percent * 60 : -30 + percent * 55, (height - 30) / 2, 0);
                _indicator.Scale = new Vector3(_tracker.Position.X > 0 ? 0.8f + percent * 0.2f : -(0.8f + percent * 0.2f), 0.8f + percent * 0.2f, 1);
                _indicator.Opacity = percent;
            }
        }

        private ContainerVisual CreateIndicator()
        {
            var compositor = _visual.Compositor;

            var sprite = compositor.CreateSpriteVisual();
            sprite.Size = new Vector2(30, 30);
            sprite.CenterPoint = new Vector3(15);

            var indicator = compositor.CreateContainerVisual();
            indicator.Size = new Vector2(30, 30);
            indicator.CenterPoint = new Vector3(15);
            indicator.Scale = new Vector3();
            indicator.Opacity = 0;

            var surface = LoadedImageSurface.StartLoadFromUri(new Uri("ms-appx:///Assets/Images/Reply.png"));

            if (ApiInfo.CanUseDirectComposition)
            {
                surface.LoadCompleted += (s, e) =>
                {
                    sprite.Brush = compositor.CreateSurfaceBrush(s);
                };

                var ellipse = compositor.CreateEllipseGeometry();
                ellipse.Radius = new Vector2(15);

                var ellipseShape = compositor.CreateSpriteShape(ellipse);
                ellipseShape.FillBrush = compositor.CreateColorBrush((Windows.UI.Color)App.Current.Resources["MessageServiceBackgroundColor"]);
                ellipseShape.Offset = new Vector2(15);

                var shape = compositor.CreateShapeVisual();
                shape.Shapes.Add(ellipseShape);
                shape.Size = new Vector2(30, 30);

                indicator.Children.InsertAtBottom(shape);
            }
            else
            {
                // Shape visuals need contract 7; W10M draws the white arrow tinted with the accent color instead.
                var mask = compositor.CreateMaskBrush();
                mask.Source = compositor.CreateColorBrush(new UISettings().GetColorValue(UIColorType.Accent));
                mask.Mask = compositor.CreateSurfaceBrush(surface);

                sprite.Brush = mask;
            }

            indicator.Children.InsertAtTop(sprite);
            return indicator;
        }

        public void InertiaStateEntered(InteractionTracker sender, InteractionTrackerInertiaStateEnteredArgs args)
        {
            if (ContentTemplateRoot is FrameworkElement element && element.Tag is MessageViewModel message)
            {
                var position = _tracker.Position.X;
                var velocity = args.PositionVelocityInPixelsPerSecond.X;

                if (_reply && (position >= SwipeTrigger || (position >= SwipeFlickDistance && velocity >= SwipeFlickVelocity)))
                {
                    _parent.ViewModel.MessageReplyCommand.Execute(message);
                }
                else if (_forward && (position <= -SwipeTrigger || (position <= -SwipeFlickDistance && velocity <= -SwipeFlickVelocity)))
                {
                    _parent.ViewModel.MessageForwardCommand.Execute(message);
                }
            }
        }

        public void CustomAnimationStateEntered(InteractionTracker sender, InteractionTrackerCustomAnimationStateEnteredArgs args)
        {

        }

        public void IdleStateEntered(InteractionTracker sender, InteractionTrackerIdleStateEnteredArgs args)
        {

        }

        public void InteractingStateEntered(InteractionTracker sender, InteractionTrackerInteractingStateEnteredArgs args)
        {

        }

        public void RequestIgnored(InteractionTracker sender, InteractionTrackerRequestIgnoredArgs args)
        {

        }
    }

    public class AccessibleChatListViewItem : ListViewItem
    {
        private readonly IProtoService _protoService;

        public AccessibleChatListViewItem()
        {

        }

        public AccessibleChatListViewItem(IProtoService protoService)
        {
            _protoService = protoService;
        }

        protected override AutomationPeer OnCreateAutomationPeer()
        {
            return new ChatListViewAutomationPeer(this, _protoService);
        }
    }

    public class ChatListViewAutomationPeer : ListViewItemAutomationPeer
    {
        private readonly ListViewItem _owner;
        private readonly IProtoService _protoService;

        public ChatListViewAutomationPeer(ListViewItem owner)
            : base(owner)
        {
            _owner = owner;
        }

        public ChatListViewAutomationPeer(ListViewItem owner, IProtoService protoService)
            : base(owner)
        {
            _owner = owner;
            _protoService = protoService;
        }

        protected override string GetNameCore()
        {
            if (_owner.ContentTemplateRoot is FrameworkElement content && content is MessageBubble == false)
            {
                var bubble = content.FindName("Bubble") as MessageBubble;
                if (bubble != null)
                {
                    return bubble.GetAutomationName() ?? base.GetNameCore();
                }
            }
            else if (_owner.ContentTemplateRoot is MessageBubble child)
            {
                return child.GetAutomationName() ?? base.GetNameCore();
            }
            else if (_owner.Content is Message message && _protoService != null)
            {
                return Automation.GetDescription(_protoService, message);
            }

            return base.GetNameCore();
        }
    }

    public class ChatGridViewItem : GridViewItem
    {
        private readonly IProtoService _protoService;

        public ChatGridViewItem()
        {

        }

        public ChatGridViewItem(IProtoService protoService)
        {
            _protoService = protoService;
        }

        protected override AutomationPeer OnCreateAutomationPeer()
        {
            return new ChatGridViewAutomationPeer(this, _protoService);
        }
    }

    public class ChatGridViewAutomationPeer : GridViewItemAutomationPeer
    {
        private readonly ChatGridViewItem _owner;
        private readonly IProtoService _protoService;

        public ChatGridViewAutomationPeer(ChatGridViewItem owner)
            : base(owner)
        {
            _owner = owner;
        }

        public ChatGridViewAutomationPeer(ChatGridViewItem owner, IProtoService protoService)
            : base(owner)
        {
            _owner = owner;
            _protoService = protoService;
        }

        protected override string GetNameCore()
        {
            if (_owner.ContentTemplateRoot is FrameworkElement content && content is MessageBubble == false)
            {
                var bubble = content.FindName("Bubble") as MessageBubble;
                if (bubble != null)
                {
                    return bubble.GetAutomationName() ?? base.GetNameCore();
                }
            }
            else if (_owner.ContentTemplateRoot is MessageBubble child)
            {
                return child.GetAutomationName() ?? base.GetNameCore();
            }
            else if (_owner.Content is Message message && _protoService != null)
            {
                return Automation.GetDescription(_protoService, message);
            }

            return base.GetNameCore();
        }
    }
}
