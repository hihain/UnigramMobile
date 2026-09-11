using System;
using System.Collections.Generic;
using System.Numerics;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.Composition;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Hosting;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Shapes;

namespace Unigram.Controls
{
    public class ColorSlider : Control
    {
        private Dictionary<uint, Point> m_pointerPositions;

        private FrameworkElement _layoutRoot;
        private FrameworkElement _container;

        private FrameworkElement _thumb;
        private Ellipse _thumbDrop;

        private Visual _thumbVisual;

        private Vector2 _value;
        private Vector2 _current;

        public ColorSlider()
        {
            DefaultStyleKey = typeof(ColorSlider);
            SizeChanged += OnSizeChanged;

            m_pointerPositions = new Dictionary<uint, Point>();
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            SetDefault(_value);
        }

        #region Properties

        public event EventHandler StrokeChanged;

        // Live color while dragging: _current tracks the pointer, _value is
        // only committed on release.
        public Color Stroke => ColorForValue(_current.X);

        public void SetDefault(Vector2 value)
        {
            _value = new Vector2(value.X, value.Y);
            _current = _value;

            if (_layoutRoot == null || _layoutRoot.ActualWidth == 0)
            {
                return;
            }

            var w = (float)_layoutRoot.ActualWidth;

            _thumbVisual.Offset = new Vector3(_current.X * w, 0, 0);

            _thumbDrop.Fill = new SolidColorBrush(ColorForValue(_current.X));
        }

        public Vector2 GetDefault()
        {
            return new Vector2(_value.X, _value.Y);
        }

        #endregion

        protected override void OnApplyTemplate()
        {
            var gradient = new LinearGradientBrush();
            gradient.StartPoint = new Point(0, 0);
            gradient.EndPoint = new Point(1, 0);

            for (int i = 0; i < _colors.Length; i++)
            {
                gradient.GradientStops.Add(new GradientStop
                {
                    Color = _colors[i],
                    Offset = _offsets[i]
                });
            }

            _layoutRoot = (FrameworkElement)GetTemplateChild("LayoutRoot");

            _container = (FrameworkElement)GetTemplateChild("Container");
            _container.PointerPressed += Thumb_PointerPressed;
            _container.PointerMoved += Thumb_PointerMoved;
            _container.PointerReleased += Thumb_PointerReleased;

            _thumb = (FrameworkElement)GetTemplateChild("Thumb");

            _thumbVisual = ElementCompositionPreview.GetElementVisual(_thumb);
            _thumbVisual.CenterPoint = new Vector3(24);
            _thumbVisual.Scale = new Vector3(0.5f);

            _thumbDrop = (Ellipse)GetTemplateChild("ThumbDrop");
            _thumbDrop.StrokeThickness = 0;

            Background = gradient;
            SetDefault(_value);

            base.OnApplyTemplate();
        }

        private void Thumb_PointerPressed(object sender, Windows.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            ((UIElement)sender).CapturePointer(e.Pointer);

            var pointer = e.GetCurrentPoint(_container);
            m_pointerPositions[pointer.PointerId] = pointer.Position;

            _current = new Vector2(_value.X, _value.Y);

            UpdateThumb(pointer.Position);
            e.Handled = true;
        }

        private void Thumb_PointerReleased(object sender, Windows.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            var pointer = e.GetCurrentPoint(_container);
            m_pointerPositions.Remove(pointer.PointerId);

            ((UIElement)sender).ReleasePointerCapture(e.Pointer);

            _value = _current;
            StrokeChanged?.Invoke(this, EventArgs.Empty);

            UpdateThumb(pointer.Position);
            e.Handled = true;
        }

        private void Thumb_PointerMoved(object sender, Windows.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (e.Pointer.IsInContact && m_pointerPositions.TryGetValue(e.Pointer.PointerId, out _))
            {
                UpdateThumb(e.GetCurrentPoint(_container).Position.ToVector2());
                StrokeChanged?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
            }
        }

        private void UpdateThumb(Point position)
        {
            UpdateThumb(position.ToVector2());
        }

        private void UpdateThumb(Vector2 position)
        {
            if (_layoutRoot == null)
            {
                return;
            }

            var w = (float)_layoutRoot.ActualWidth;

            var offsetX = position.X / w;
            var left = Clamp(offsetX, 0, 1);

            _current.X = left;

            _thumbDrop.Fill = new SolidColorBrush(ColorForValue(_current.X));

            _thumbVisual.Offset = new Vector3(_current.X * w, 0, 0);
        }

        private Color ColorForValue(double location)
        {
            if (location >= 1)
            {
                return _colors[_colors.Length - 1];
            }

            if (location < double.Epsilon)
            {
                return _colors[0];
            }
            else if (location > 1 - double.Epsilon)
            {
                return _colors[_colors.Length - 1];
            }

            var leftIndex = -1;
            var rightIndex = -1;


            for (int i = 1; i < _offsets.Length; i++)
            {
                if (_offsets[i] > location)
                {
                    leftIndex = i - 1;
                    rightIndex = i;
                    break;
                }
            }

            var leftLocation = _offsets[leftIndex];
            var leftColor = _colors[leftIndex];

            var rightLocation = _offsets[rightIndex];
            var rightColor = _colors[rightIndex];

            var factor = (location - leftLocation) / (rightLocation - leftLocation);

            return interpolateColor(leftColor, rightColor, factor);
        }

        private Color interpolateColor(Color color1, Color color2, double factor)
        {
            factor = Clamp(factor, 0, 1);


            var r1 = color1.R / 255f;
            var r2 = color2.R / 255f;
            var g1 = color1.G / 255f;
            var g2 = color2.G / 255f;
            var b1 = color1.B / 255f;
            var b2 = color2.B / 255f;

            var r = r1 + (r2 - r1) * factor;
            var g = g1 + (g2 - g1) * factor;
            var b = b1 + (b2 - b1) * factor;

            return Color.FromArgb(0xFF, (byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
        }

        private Color[] _colors = new[]
        {
            Color.FromArgb(0xff, 0xea, 0x27, 0x39),
            Color.FromArgb(0xff, 0xdb, 0x3a, 0xd2),
            Color.FromArgb(0xff, 0x30, 0x51, 0xe3),
            Color.FromArgb(0xff, 0x49, 0xc5, 0xed),
            Color.FromArgb(0xff, 0x80, 0xc8, 0x64),
            Color.FromArgb(0xff, 0xfc, 0xde, 0x65),
            Color.FromArgb(0xff, 0xfc, 0x96, 0x4d),
            Color.FromArgb(0xff, 0x00, 0x00, 0x00),
            Color.FromArgb(0xff, 0xff, 0xff, 0xff)
        };

        private double[] _offsets = new[]
        {
            0.0,  //red
            0.14, //pink
            0.24, //blue
            0.39, //cyan
            0.49, //green
            0.62, //yellow
            0.73, //orange
            0.85, //black
            1.0
        };

        #region MissingFrameworkFunction
        public static T Clamp<T>(T val, T min, T max) where T : IComparable<T>
        {
            if (val.CompareTo(min) < 0) return min;
            else if (val.CompareTo(max) > 0) return max;
            else return val;
        }
        #endregion
    }
}
