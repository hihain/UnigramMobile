using System.Numerics;
using Windows.UI;
using Windows.UI.Composition;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Hosting;
using Windows.UI.Xaml.Shapes;

namespace Unigram.Common
{
    public static class DropShadowEx
    {
#if MODERN_TDLIB
        // Attach is called from many controls, and the diagnostics log is deleted once it
        // reaches its size cap, so unbounded tracing here would evict the startup records
        // this instrumentation exists to capture. Trace only the first few calls, which
        // covers the MainPage constructor.
        private static int _traceBudget = 4;

        private static bool ShouldTrace()
        {
            return System.Threading.Interlocked.Decrement(ref _traceBudget) >= 0;
        }

        private static void Trace(bool enabled, string details)
        {
            if (enabled)
            {
                Unigram.Logs.PushDiagnostics.Write("shadow.attach", details);
            }
        }
#endif

        public static Visual Attach(UIElement element, float radius, float opacity, CompositionClip clip = null)
        {
#if MODERN_TDLIB
            var trace = ShouldTrace();
            Trace(trace, $"step=element;element={(element == null ? "null" : "ok")}");
#endif
            var elementVisual = ElementCompositionPreview.GetElementVisual(element);
#if MODERN_TDLIB
            Trace(trace, $"step=element_visual;visual={(elementVisual == null ? "null" : "ok")};compositor={(elementVisual?.Compositor == null ? "null" : "ok")}");
#endif

            var shadow = elementVisual.Compositor.CreateDropShadow();
            shadow.BlurRadius = radius;
            shadow.Opacity = opacity;
            shadow.Color = Colors.Black;
#if MODERN_TDLIB
            Trace(trace, $"step=shadow;shadow={(shadow == null ? "null" : "ok")}");
#endif

            var visual = elementVisual.Compositor.CreateSpriteVisual();
#if MODERN_TDLIB
            Trace(trace, $"step=sprite;sprite={(visual == null ? "null" : "ok")}");
#endif
            visual.Shadow = shadow;
            visual.Size = new Vector2(0, 0);
            visual.Offset = new Vector3(0, 0, 0);
            visual.Clip = clip;

            switch (element)
            {
                case Image image:
                    shadow.Mask = image.GetAlphaMask();
                    break;
                case Shape shape:
                    shadow.Mask = shape.GetAlphaMask();
                    break;
                case TextBlock textBlock:
                    shadow.Mask = textBlock.GetAlphaMask();
                    break;
            }

            ElementCompositionPreview.SetElementChildVisual(element, visual);
#if MODERN_TDLIB
            Trace(trace, "step=done");
#endif
            return visual;
        }

#if MODERN_TDLIB
        private static bool? _canUseRelativeSizeAdjustment;

        // Visual.RelativeSizeAdjustment lives on IVisual2, which is UniversalApiContract v5.
        // IsPropertyPresent cannot be used here: on Windows 10 Mobile (contract v4) it reports
        // the property as present, but the IVisual2 query still fails and the setter faults
        // with E_POINTER. The contract level is the only reliable signal, so gate on that.
        // Attaching a shadow and then never sizing it would leave an invisible shadow, which
        // is why this falls back to manual sizing rather than skipping.
        private static bool CanUseRelativeSizeAdjustment
        {
            get
            {
                return (_canUseRelativeSizeAdjustment = _canUseRelativeSizeAdjustment
                    ?? ApiInfo.IsUniversalApiContract5Present) ?? false;
            }
        }

        /// <summary>
        /// Sizes <paramref name="visual"/> to match <paramref name="element"/> for as long as
        /// the element lives, using relative sizing when the device supports it.
        /// </summary>
        public static void SetRelativeSize(Visual visual, FrameworkElement element)
        {
            if (visual == null || element == null)
            {
                return;
            }

            if (CanUseRelativeSizeAdjustment)
            {
                visual.RelativeSizeAdjustment = Vector2.One;
                return;
            }

            visual.Size = new Vector2((float)element.ActualWidth, (float)element.ActualHeight);
            element.SizeChanged += (s, args) =>
            {
                visual.Size = new Vector2((float)args.NewSize.Width, (float)args.NewSize.Height);
            };
        }
#endif
    }
}
