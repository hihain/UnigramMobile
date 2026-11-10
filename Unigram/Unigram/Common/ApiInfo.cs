using Unigram.Native;
using Windows.Foundation.Metadata;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media.Animation;

namespace Unigram.Common
{
    public static class ApiInfo
    {
        //private static bool? _canAddContextRequestedEvent;
        public static bool CanAddContextRequestedEvent => IsUniversalApiContract6Present;// (_canAddContextRequestedEvent = _canAddContextRequestedEvent ?? ApiInformation.IsReadOnlyPropertyPresent("Windows.UI.Xaml.UIElement", "ContextRequestedEvent")) ?? false; //Note: 17134 (1803), UniversalApiContract v6

        //private static bool? _canCheckTextTrimming;
        public static bool CanCheckTextTrimming => IsUniversalApiContract5Present; // (_canCheckTextTrimming = _canCheckTextTrimming ?? ApiInformation.IsReadOnlyPropertyPresent("Windows.UI.Xaml.Controls.TextBlock", "IsTextTrimmed")) ?? false;

        private static bool? _canUseDirectComposition;
        public static bool CanUseDirectComposition => (_canUseDirectComposition = _canUseDirectComposition ?? ApiInformation.IsApiContractPresent("Windows.Foundation.UniversalApiContract", 7)) ?? false;

        //private static bool? _canUseAccelerators;
        public static bool CanUseAccelerators => IsUniversalApiContract5Present;// (_canUseAccelerators = _canUseAccelerators ?? ApiInformation.IsPropertyPresent("Windows.UI.Xaml.UIElement", "KeyboardAccelerators")) ?? false;

        //private static bool? _canUseFlyoutShowOptions;
        public static bool CanUseFlyoutShowOptions => IsUniversalApiContract7Present;// (_canUseFlyoutShowOptions = _canUseFlyoutShowOptions ?? ApiInformation.IsTypePresent("Windows.UI.Xaml.Controls.Primitives.FlyoutShowOptions")) ?? false; //Note: 17763 (1809), UniversalApiContract v7

        //private static bool? _canUseNewFlyoutPlacementMode; //Note: 17763, UniversalApiContract v7
        public static bool CanUseNewFlyoutPlacementMode => IsUniversalApiContract7Present;// (_canUseNewFlyoutPlacementMode = _canUseNewFlyoutPlacementMode ?? ApiInformation.IsEnumNamedValuePresent("Windows.UI.Xaml.Controls.Primitives.FlyoutPlacementMode", "BottomEdgeAlignedLeft")) ?? false; //Note: Valid also for: BottomEdgeAlignedRight, TopEdgeAlignedRight, ...

        //private static bool? _canUseKeyboardAcceleratorPlacementMode;
        public static bool CanUseKeyboardAcceleratorPlacementMode => IsUniversalApiContract6Present;// (_canUseKeyboardAcceleratorPlacementMode = _canUseKeyboardAcceleratorPlacementMode ?? ApiInformation.IsPropertyPresent("Windows.UI.Xaml.UIElement", "KeyboardAcceleratorPlacementMode")) ?? false; //Note: 17134 (1803), UniversalApiContract v6

        private static bool? _canUseShadow;
        public static bool CanUseShadow => (_canUseShadow = _canUseShadow ?? ApiInformation.IsPropertyPresent("Windows.UI.Xaml.UIElement", "Shadow")) ?? false; //Note: 18362 (1903), UniversalApiContract v8

        //private static bool? _canCreateGeometricClip;
        public static bool CanCreateGeometricClip => IsUniversalApiContract7Present;// (_canCreateGeometricClip = _canCreateGeometricClip ?? ApiInformation.IsMethodPresent("Windows.UI.Composition.Compositor", "CreateGeometricClip")) ?? false; //Note: 17763, UniversalApiContract v7

        //private static bool? _canUsePreviewKeyDownEvent;
        public static bool CanUsePreviewKeyDownEvent => IsUniversalApiContract5Present;// (_canUsePreviewKeyDownEvent = _canUsePreviewKeyDownEvent ?? ApiInformation.IsEventPresent("Windows.UI.Xaml.UIElement", "PreviewKeyDown")) ?? false; //Note: 16299, UniversalApiContract v5

        //private static bool? _canUseTextHighlighters;
        public static bool CanUseTextHighlighters => IsUniversalApiContract5Present;// (_canUseTextHighlighters = _canUseTextHighlighters ?? ApiInformation.IsPropertyPresent("Windows.UI.Xaml.Controls.TextBlock", "TextHighlighters")) ?? false; //Note: 16299, UniversalApiContract v5

        //private static bool? _canUseSplitViewPaneOpeningEvent;
        public static bool CanUseSplitViewPaneOpeningEvent => IsUniversalApiContract5Present;// (_canUseSplitViewPaneOpeningEvent = _canUseSplitViewPaneOpeningEvent ?? ApiInformation.IsEventPresent("Windows.UI.Xaml.Controls.SplitView", "PaneOpening")) ?? false; //Note: 16299, UniversalApiContract v5

        public static bool IsUniversalApiContract7Present => CanUseAccelerators;

        private static bool? _isUniversalApiContract6Present;
        public static bool IsUniversalApiContract6Present => (_isUniversalApiContract6Present = _isUniversalApiContract6Present ?? ApiInformation.IsApiContractPresent("Windows.Foundation.UniversalApiContract", 6)) ?? false;

        private static bool? _isUniversalApiContract5Present;
        public static bool IsUniversalApiContract5Present => (_isUniversalApiContract5Present = _isUniversalApiContract5Present ?? ApiInformation.IsApiContractPresent("Windows.Foundation.UniversalApiContract", 5)) ?? false;

        private static bool? _hasStatusBar;
        public static bool HasStatusBar => (_hasStatusBar = _hasStatusBar ?? ApiInformation.IsTypePresent("Windows.UI.ViewManagement.StatusBar")) ?? false;

        private static bool? _isPhoneContractPresent;
        public static bool IsPhoneContractPresent => (_isPhoneContractPresent = _isPhoneContractPresent ?? ApiInformation.IsApiContractPresent("Windows.Phone.PhoneContract", 1)) ?? false;

        private static bool? _isFullExperience;
        public static bool IsFullExperience => (_isFullExperience = _isFullExperience ?? Windows.System.Profile.AnalyticsInfo.VersionInfo.DeviceFamily != "Windows.Mobile") ?? true;

        private static bool? _canUseViewports;
        public static bool CanUseViewports => (_canUseViewports = _canUseViewports ?? ApiInformation.IsEventPresent("Windows.UI.Xaml.FrameworkElement", "EffectiveViewportChanged")) ?? false;

        private static bool? _isMediaSupported;
        public static bool IsMediaSupported => (_isMediaSupported = _isMediaSupported ?? NativeUtils.IsMediaSupported()) ?? true;

#if MODERN_TDLIB
        private static int _capabilitiesWritten;

        /// <summary>
        /// Records which WinRT API contracts and individual composition members this device
        /// actually exposes. Porting to a newer TDLib did not change the OS, but it did move
        /// the app onto code paths whose availability was previously untested here, and a
        /// missing member surfaces as a bare NullReferenceException under .NET Native rather
        /// than as a typed error. This is API surface metadata only; it contains no user data.
        /// </summary>
        public static void WriteCapabilities()
        {
            if (System.Threading.Interlocked.Exchange(ref _capabilitiesWritten, 1) != 0)
            {
                return;
            }

            try
            {
                var contract = 0;
                for (ushort i = 4; i <= 12; i++)
                {
                    if (ApiInformation.IsApiContractPresent("Windows.Foundation.UniversalApiContract", i))
                    {
                        contract = i;
                    }
                }

                var details =
                    $"universal_contract={contract}" +
                    $";family={Windows.System.Profile.AnalyticsInfo.VersionInfo.DeviceFamily}" +
                    $";visual_relative_size={ApiInformation.IsPropertyPresent("Windows.UI.Composition.Visual", "RelativeSizeAdjustment")}" +
                    $";window_compositor={ApiInformation.IsPropertyPresent("Windows.UI.Xaml.Window", "Compositor")}" +
                    $";create_shape_visual={ApiInformation.IsMethodPresent("Windows.UI.Composition.Compositor", "CreateShapeVisual")}" +
                    $";create_geometric_clip={ApiInformation.IsMethodPresent("Windows.UI.Composition.Compositor", "CreateGeometricClip")}" +
                    $";create_spring_vector3={ApiInformation.IsMethodPresent("Windows.UI.Composition.Compositor", "CreateSpringVector3Animation")}" +
                    $";create_linear_gradient={ApiInformation.IsMethodPresent("Windows.UI.Composition.Compositor", "CreateLinearGradientBrush")}" +
                    $";set_is_translation_enabled={ApiInformation.IsMethodPresent("Windows.UI.Xaml.Hosting.ElementCompositionPreview", "SetIsTranslationEnabled")}" +
                    $";uielement_shadow={ApiInformation.IsPropertyPresent("Windows.UI.Xaml.UIElement", "Shadow")}";

                Logs.PushDiagnostics.Write("device.capabilities", details);
            }
            catch (System.Exception ex)
            {
                Logs.PushDiagnostics.WriteException("device.capabilities", ex);
            }
        }
#endif

        public static TransitionCollection CreateSlideTransition()
        {
            //if (ApiInformation.IsPropertyPresent("Windows.UI.Xaml.Media.Animation.SlideNavigationTransitionInfo", "Effect"))
            if (CanUseDirectComposition)
            {
                return new TransitionCollection { new NavigationThemeTransition { DefaultNavigationTransitionInfo = new SlideNavigationTransitionInfo { Effect = SlideNavigationTransitionEffect.FromRight } } };
            }

            return null;
        }



        private static FlowDirection? _flowDirection;
        public static FlowDirection FlowDirection => (_flowDirection = _flowDirection ?? LoadFlowDirection()) ?? FlowDirection.LeftToRight;

        private static FlowDirection LoadFlowDirection()
        {
#if DEBUG
            var flowDirectionSetting = Windows.ApplicationModel.Resources.Core.ResourceContext.GetForCurrentView().QualifierValues["LayoutDirection"];
            return flowDirectionSetting == "RTL" ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
#else
            return FlowDirection.LeftToRight;
#endif
        }
    }
}
