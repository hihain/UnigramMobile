using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using System;
using System.Collections.Generic;
using System.Numerics;
using Unigram.Common;
using Unigram.Controls;
using Unigram.Converters;
using Unigram.Entities;
using Unigram.Services;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Media.Core;
using Windows.Media.Editing;
using Windows.Storage;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Shapes;

namespace Unigram.Views.Popups
{
    public sealed partial class EditMediaPopup : OverlayPage
    {
        //public StorageMedia ResultMedia { get; private set; }

        private StorageFile _file;
        private StorageMedia _media;
        private BitmapEditState _originalMediaEditState;
        private bool _originalVideoIsMuted;
        private int _originalVideoCompression;

        private BitmapRotation _rotation = BitmapRotation.None;
        private BitmapFlip _flip = BitmapFlip.None;

        private bool _hasMessageContext = true;

        // Stroke thickness presets, in editor canvas pixels. PencilCanvas divides by its
        // own width when a stroke starts, so the same stroke comes out proportional on an
        // output bitmap of any size.
        private static readonly float[] _thicknessValues = { 2, 3, 4, 6, 9, 13, 20 };
        private readonly List<(Border Ring, Ellipse Dot)> _thicknessPresets = new List<(Border, Ellipse)>();
        private int _thicknessIndex = 2;

        public EditMediaPopup(StorageMedia media, ImageCropperMask mask = ImageCropperMask.Rectangle, bool ttl = false)
        {
            InitializeComponent();

            Canvas.Strokes = media.EditState.Strokes;
            Cropper.SetMask(mask);
            if (mask == ImageCropperMask.Ellipse)
            {
                _hasMessageContext = false;
                media.EditState.Proportions = BitmapProportions.Square;
            }
            Cropper.SetProportions(media.EditState.Proportions);

            _file = media.File;
            _media = media;
            _originalMediaEditState = CopyBitmapEditState(media.EditState);

            Loaded += async (s, args) =>
            {
                await Cropper.SetSourceAsync(media.File, media.EditState.Rotation, media.EditState.Flip, media.EditState.Proportions, media.EditState.Rectangle);
                Cropper.IsCropEnabled = !_hasMessageContext && _media.IsVideo;
            };
            Unloaded += (s, args) =>
            {
                Media.Source = null;
            };

            #region Init UI
            if (mask == ImageCropperMask.Ellipse && string.Equals(media.File.FileType, ".mp4", StringComparison.OrdinalIgnoreCase))
            {
                FindName(nameof(TrimToolbar));
                TrimToolbar.Visibility = Visibility.Visible;
                BasicToolbar.Visibility = Visibility.Collapsed;
                SeparatorLeft.Visibility = Visibility.Collapsed;
                SeparatorRight.Visibility = Visibility.Collapsed;

                InitializeVideo(media.File);
            }

            if (_media is StorageVideo video)
            {
                _originalVideoCompression = video.Compression;
                _originalVideoIsMuted = video.IsMuted;
                Mute.IsChecked = video.IsMuted;
                Mute.Visibility = Visibility.Visible;
                Compress.IsChecked = video.Compression != video.MaxCompression;
                Compress.Visibility = video.CanCompress ? Visibility.Visible : Visibility.Collapsed;

                if (video.CanCompress)
                {
                    Compress.Glyph = new CompressionToGlyphConverter().Convert(video.Compression, typeof(string), null, video.MaxCompression.ToString()).ToString();
                    Compress.CheckedGlyph = Compress.Glyph;
                    Compress.IsChecked = video.Compression != video.MaxCompression;
                }
            }
            else
            {
                Mute.Visibility = Visibility.Collapsed;
                Compress.Visibility = Visibility.Collapsed;
            }

            Crop.Visibility = _media is StoragePhoto ? Visibility.Visible : Visibility.Collapsed;
            Draw.Visibility = Crop.Visibility;
            Ttl.Visibility = ttl ? Visibility.Visible : Visibility.Collapsed;
            Ttl.IsChecked = _media.Ttl > 0;

            if (_media.EditState is BitmapEditState editSate)
            {
                Crop.IsChecked = editSate.Proportions != BitmapProportions.Custom ||
                                    editSate.Flip != BitmapFlip.None ||
                                    editSate.Rotation != BitmapRotation.None ||
                                    (!editSate.Rectangle.IsEmpty &&
                                    (editSate.Rectangle.X > 0 || editSate.Rectangle.Y > 0 ||
                                    Math.Abs(editSate.Rectangle.Width - 1) > 0.1f || Math.Abs(editSate.Rectangle.Height - 1) > 0.1f));

                Draw.IsChecked = editSate.Strokes != null && editSate.Strokes.Count > 0;
            }
            else
            {
                Crop.IsChecked = false;
                Draw.IsChecked = false;
            }

            if (_hasMessageContext)
            {
                if (!string.IsNullOrWhiteSpace(media.Caption?.Text))
                    CaptionInput.SetText(media.Caption);
            }
            else
                CaptionInput.Visibility = Visibility.Collapsed;
            #endregion
        }

        //public bool IsCropEnabled
        //{
        //    get { return this.Cropper.IsCropEnabled; }
        //    set { this.Cropper.IsCropEnabled = value; }
        //}

        //public Rect CropRectangle
        //{
        //    get { return this.Cropper.CropRectangle; }
        //}

        private async void InitializeVideo(StorageFile file)
        {
            Media.Source = MediaSource.CreateFromStorageFile(file);
            Media.MediaPlayer.AutoPlay = true;
            Media.MediaPlayer.IsMuted = true;
            Media.MediaPlayer.IsLoopingEnabled = true;
            Media.MediaPlayer.PlaybackSession.PositionChanged += MediaPlayer_PositionChanged;

            var clip = await MediaClip.CreateFromFileAsync(file);
            var composition = new MediaComposition();
            composition.Clips.Add(clip);

            var props = clip.GetVideoEncodingProperties();

            double ratioX = (double)40 / props.Width;
            double ratioY = (double)40 / props.Height;
            double ratio = Math.Max(ratioY, ratioY);

            var width = (int)(props.Width * ratio);
            var height = (int)(props.Height * ratio);

            var count = Math.Ceiling(280d / width);

            var times = new List<TimeSpan>();

            for (int i = 0; i < count; i++)
            {
                times.Add(TimeSpan.FromMilliseconds(clip.OriginalDuration.TotalMilliseconds / count * i)); // TimeSpan.FromMilliseconds for older Frameworks (instead of Divide)
            }

            TrimThumbnails.Children.Clear();
#if MOBILE
            var lumiaScaleFactor = 4; // prevents Catastrophic failure on Lumia 640 & probably others
            var thumbnails = await composition.GetThumbnailsAsync(times, width * lumiaScaleFactor, height * lumiaScaleFactor, VideoFramePrecision.NearestKeyFrame);
#else
            var thumbnails = await composition.GetThumbnailsAsync(times, width, height, VideoFramePrecision.NearestKeyFrame);
#endif
            foreach (var thumb in thumbnails)
            {
                var bitmap = new BitmapImage();
                await bitmap.SetSourceAsync(thumb);

                var image = new Image();
                image.Width = width;
                image.Height = height;
                image.Stretch = Windows.UI.Xaml.Media.Stretch.UniformToFill;
                image.Source = bitmap;

                TrimThumbnails.Children.Add(image);
            }

            TrimRange.SetOriginalDuration(clip.OriginalDuration);
        }

        private void Accept_Click(object sender, RoutedEventArgs e)
        {
            if ((CropToolbar != null && CropToolbar.Visibility == Visibility.Visible) ||
                (!_hasMessageContext && _media.IsVideo))
            {
                var rect = Cropper.CropRectangle;

                TimeSpan trimStartTime;
                TimeSpan trimStopTime;

                if (TrimRange != null)
                {
                    trimStartTime = TimeSpan.FromMilliseconds(TrimRange.Minimum * TrimRange.OriginalDuration.TotalMilliseconds);
                    trimStopTime = TimeSpan.FromMilliseconds(TrimRange.Maximum * TrimRange.OriginalDuration.TotalMilliseconds);
                }

                if (_media?.EditState is BitmapEditState es)
                {
                    es.Rectangle = rect;
                    es.Proportions = Cropper.Proportions;
                    es.Strokes = Canvas.Strokes;
                    es.Flip = _flip;
                    es.Rotation = _rotation;
                    es.TrimStartTime = trimStartTime;
                    es.TrimStopTime = trimStopTime;
                }
                Crop.IsChecked = IsCropped(rect);
                ResetUiVisibility();
                if (!_hasMessageContext && _media.IsVideo)
                    Hide(ContentDialogResult.Primary);
            }
            else if (DrawToolbar != null && DrawToolbar.Visibility == Visibility.Visible)
            {
                Canvas.SaveState();
                _media.EditState.Strokes = Canvas.Strokes;

                Draw.IsChecked = Canvas.Strokes?.Count > 0;
                ResetUiVisibility();

                SettingsService.Current.Pencil = DrawSlider.GetDefault();
            }
            else
            {
                if (_hasMessageContext)
                {
                    ResetUiVisibility();
                    _media.Caption = CaptionInput.GetFormattedText();
                }
                Hide(ContentDialogResult.Primary);
            }
        }

        private async void Cancel_Click(object sender, RoutedEventArgs e)
        {
            if (CropToolbar != null && CropToolbar.Visibility == Visibility.Visible)
            {
                // Reset Cropper
                await Cropper.SetSourceAsync(_media.File, _media.EditState.Rotation, _media.EditState.Flip, _media.EditState.Proportions, _media.EditState.Rectangle);
                _rotation = _media.EditState.Rotation;
                _flip = _media.EditState.Flip;
                Crop.IsChecked = IsCropped(Cropper.CropRectangle);
                ResetUiVisibility();
            }
            else if (DrawToolbar != null && DrawToolbar.Visibility == Visibility.Visible)
            {
                Canvas.RestoreState();

                ResetUiVisibility();
                SettingsService.Current.Pencil = DrawSlider.GetDefault();
            }
            else
            {
                if (_media is StorageMedia)
                    _media.EditState = _originalMediaEditState; // Reset to state before starting editing
                if (_media is StorageVideo video)
                {
                    video.IsMuted = _originalVideoIsMuted;
                    video.Compression = _originalVideoCompression;
                }
                ResetUiVisibility();
                Hide(ContentDialogResult.Secondary);
            }
        }

        private BitmapEditState CopyBitmapEditState(BitmapEditState original)
        {
            return new BitmapEditState() { Flip = original.Flip, Proportions = original.Proportions, Rectangle = original.Rectangle, Rotation = original.Rotation, Strokes = original.Strokes };
        }

        private bool IsCropped(Rect rect)
        {
            return Cropper.Proportions != BitmapProportions.Custom ||
                   _flip != BitmapFlip.None ||
                   _rotation != BitmapRotation.None ||
                   (!rect.IsEmpty &&
                    (rect.X > 0 || rect.Y > 0 ||
                     Math.Abs(rect.Width - 1) > 0.1f || Math.Abs(rect.Height - 1) > 0.1f));
        }

        private void ResetUiVisibility()
        {
            Cropper.IsCropEnabled = false;
            Canvas.IsEnabled = false;
            CaptionInput.Visibility = _hasMessageContext ? Visibility.Visible : Visibility.Collapsed;
            BasicToolbar.Visibility = Visibility.Visible;
            if (CropToolbar != null)
                CropToolbar.Visibility = Visibility.Collapsed;
            if (DrawToolbar != null)
                DrawToolbar.Visibility = Visibility.Collapsed;
            if (DrawPanel != null)
                DrawPanel.Visibility = Visibility.Collapsed;
            if (TrimToolbar != null)
                TrimToolbar.Visibility = !_hasMessageContext && _media.IsVideo ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Proportions_Click(object sender, RoutedEventArgs e)
        {
            if (Cropper.Proportions != BitmapProportions.Custom)
            {
                Cropper.SetProportions(BitmapProportions.Custom);
                Proportions.IsChecked = false;
            }
            else
            {
                var flyout = new MenuFlyout();
                var items = Cropper.GetProportions();

                var handler = new RoutedEventHandler((s, args) =>
                {
                    if (s is MenuFlyoutItem option)
                    {
                        Cropper.SetProportions((BitmapProportions)option.Tag);
                        Proportions.IsChecked = true;
                    }
                });

                foreach (var item in items)
                {
                    var option = new MenuFlyoutItem();
                    option.Click += handler;
                    option.Text = ProportionsToLabelConverter.Convert(item);
                    option.Tag = item;
                    option.MinWidth = 140;
                    option.HorizontalContentAlignment = HorizontalAlignment.Center;

                    flyout.Items.Add(option);
                }

                if (flyout.Items.Count > 0)
                {
                    flyout.ShowAt((GlyphToggleButton)sender);
                }
            }
        }

        private async void Rotate_Click(object sender, RoutedEventArgs e)
        {
            var rotation = BitmapRotation.None;

            var proportions = RotateProportions(Cropper.Proportions);
            var rectangle = RotateArea(Cropper.CropRectangle);

            switch (_rotation)
            {
                case BitmapRotation.None:
                    rotation = BitmapRotation.Clockwise90Degrees;
                    break;
                case BitmapRotation.Clockwise90Degrees:
                    rotation = BitmapRotation.Clockwise180Degrees;
                    break;
                case BitmapRotation.Clockwise180Degrees:
                    rotation = BitmapRotation.Clockwise270Degrees;
                    break;
            }

            _rotation = rotation;
            await Cropper.SetSourceAsync(_file, rotation, _flip, proportions, rectangle);

            Rotate.IsChecked = _rotation != BitmapRotation.None;
            Canvas.Invalidate();
        }

        private Rect RotateArea(Rect area)
        {
            var point = new Point(1 - area.Bottom, 1 - (1 - area.X));
            var result = new Rect(point.X, point.Y, area.Height, area.Width);

            return result;
        }

        private BitmapProportions RotateProportions(BitmapProportions proportions)
        {
            switch (proportions)
            {
                case BitmapProportions.Original:
                case BitmapProportions.Square:
                default:
                    return proportions;
                // Portrait
                case BitmapProportions.TwoOverThree:
                    return BitmapProportions.ThreeOverTwo;
                case BitmapProportions.ThreeOverFive:
                    return BitmapProportions.FiveOverThree;
                case BitmapProportions.ThreeOverFour:
                    return BitmapProportions.FourOverThree;
                case BitmapProportions.FourOverFive:
                    return BitmapProportions.FiveOverFour;
                case BitmapProportions.FiveOverSeven:
                    return BitmapProportions.SevenOverFive;
                case BitmapProportions.NineOverSixteen:
                    return BitmapProportions.SixteenOverNine;
                // Landscape
                case BitmapProportions.ThreeOverTwo:
                    return BitmapProportions.TwoOverThree;
                case BitmapProportions.FiveOverThree:
                    return BitmapProportions.ThreeOverFive;
                case BitmapProportions.FourOverThree:
                    return BitmapProportions.ThreeOverFour;
                case BitmapProportions.FiveOverFour:
                    return BitmapProportions.FourOverFive;
                case BitmapProportions.SevenOverFive:
                    return BitmapProportions.FiveOverSeven;
                case BitmapProportions.SixteenOverNine:
                    return BitmapProportions.NineOverSixteen;
            }
        }

        private async void Flip_Click(object sender, RoutedEventArgs e)
        {
            var flip = _flip;
            var rotation = _rotation;

            var proportions = Cropper.Proportions;
            var rectangle = FlipArea(Cropper.CropRectangle);

            switch (rotation)
            {
                case BitmapRotation.Clockwise90Degrees:
                case BitmapRotation.Clockwise270Degrees:
                    switch (flip)
                    {
                        case BitmapFlip.None:
                            flip = BitmapFlip.Vertical;
                            break;
                        case BitmapFlip.Vertical:
                            flip = BitmapFlip.None;
                            break;
                        case BitmapFlip.Horizontal:
                            flip = BitmapFlip.None;
                            rotation = rotation == BitmapRotation.Clockwise90Degrees
                                ? BitmapRotation.Clockwise270Degrees
                                : BitmapRotation.Clockwise90Degrees;
                            break;
                    }
                    break;
                case BitmapRotation.None:
                case BitmapRotation.Clockwise180Degrees:
                    switch (flip)
                    {
                        case BitmapFlip.None:
                            flip = BitmapFlip.Horizontal;
                            break;
                        case BitmapFlip.Horizontal:
                            flip = BitmapFlip.None;
                            break;
                        case BitmapFlip.Vertical:
                            flip = BitmapFlip.None;
                            rotation = rotation == BitmapRotation.None
                                ? BitmapRotation.Clockwise180Degrees
                                : BitmapRotation.None;
                            break;
                    }
                    break;
            }

            _flip = flip;
            _rotation = rotation;
            await Cropper.SetSourceAsync(_file, _rotation, flip, proportions, rectangle);

            //Transform.ScaleX = _flip == BitmapFlip.Horizontal ? -1 : 1;
            //Transform.ScaleY = _flip == BitmapFlip.Vertical ? -1 : 1;

            Flip.IsChecked = _flip != BitmapFlip.None;
            Canvas.Invalidate();
        }

        private Rect FlipArea(Rect area)
        {
            var point = new Point(1 - area.Right, area.Y);
            var result = new Rect(point.X, point.Y, area.Width, area.Height);

            return result;
        }

        private void Mute_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Windows.UI.Xaml.Controls.Primitives.ToggleButton button && _media is StorageVideo video)
            {
                button.IsChecked = !button.IsChecked == true;
                video.IsMuted = button.IsChecked == true;
                Compress.IsEnabled = video.CanCompress;
                if (video.CanCompress && Compress.Visibility == Visibility.Collapsed)
                { // never hide, just readd if it was hidden
                    Compress.Visibility = Visibility.Visible;
                    Compress.IsChecked = false;
                    video.Compression = video.MaxCompression;
                }

                if (video.IsMuted)
                {
                    Compress.Glyph = new CompressionToGlyphConverter().Convert(video.Compression, typeof(string), null, video.MaxCompression.ToString()).ToString();
                    Compress.CheckedGlyph = Compress.Glyph;
                    Compress.IsChecked = video.Compression != video.MaxCompression;
                }
            }
        }

        private void Crop_Click(object sender, RoutedEventArgs e)
        {
            ResetUiVisibility();
            Cropper.IsCropEnabled = true;
            CaptionInput.Visibility = Visibility.Collapsed;
            BasicToolbar.Visibility = Visibility.Collapsed;

            if (CropToolbar == null)
                FindName(nameof(CropToolbar));
            CropToolbar.Visibility = Visibility.Visible;

            if (_media.EditState.Proportions != BitmapProportions.Custom)
            {
                Proportions.IsChecked = true;
                Proportions.IsEnabled = _hasMessageContext;
            }

            Rotate.IsChecked = _media.EditState.Rotation != BitmapRotation.None;
            Flip.IsChecked = _media.EditState.Flip != BitmapFlip.None;
        }

        private void Draw_Click(object sender, RoutedEventArgs e)
        {
            ResetUiVisibility();
            Canvas.IsEnabled = true;
            CaptionInput.Visibility = Visibility.Collapsed;
            BasicToolbar.Visibility = Visibility.Collapsed;

            if (DrawToolbar == null)
                FindName(nameof(DrawToolbar));
            if (DrawPanel == null)
                FindName(nameof(DrawPanel));

            DrawToolbar.Visibility = Visibility.Visible;
            DrawPanel.Visibility = Visibility.Visible;

            var pencil = SettingsService.Current.Pencil;
            DrawSlider.SetDefault(pencil);

            if (ThicknessPanel.Children.Count == 0)
            {
                BuildThicknessPresets();
            }

            SelectThicknessPreset((int)Math.Round(pencil.Y * (_thicknessValues.Length - 1)));

            Brush.IsChecked = true;
            Erase.IsChecked = false;
            InvalidateToolbar();
        }

        public int Compression
        {
            get => (_media is StorageVideo video) ? video.Compression : -1;
        }

        private void Compress_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is GlyphToggleButton button && _media is StorageVideo video)) return;
            var compressionToGlyphConverter = new CompressionToGlyphConverter();
            if (!video.IsMuted && video.Compression != video.MaxCompression)
            {
                // Do not compress
                video.Compression = video.MaxCompression;
                Compress.Glyph = compressionToGlyphConverter.Convert(video.Compression, typeof(string), null, video.MaxCompression.ToString()).ToString();
                Compress.CheckedGlyph = Compress.Glyph;
                Compress.IsChecked = false;
                return;
            }
            var text = new TextBlock
            {
                Style = App.Current.Resources["InfoCaptionTextBlockStyle"] as Style,
                TextWrapping = TextWrapping.Wrap,
                Text = video.ToString()
            };
            var slider = new Slider
            {
                IsThumbToolTipEnabled = false,
                Minimum = 0,
                Maximum = video.MaxCompression,
                StepFrequency = 1,
                SmallChange = 1,
                LargeChange = 1,
                Value = video.Compression
            };
            slider.Header = compressionToGlyphConverter.Convert(video.Compression, typeof(string), string.Empty, video.MaxCompression.ToString());
            slider.ValueChanged += (s, args) =>
            {
                var index = (int)args.NewValue;
                slider.Header = compressionToGlyphConverter.Convert(index, typeof(string), string.Empty, video.MaxCompression.ToString());
                video.Compression = index;
                Compress.Glyph = compressionToGlyphConverter.Convert(index, typeof(string), null, video.MaxCompression.ToString()).ToString();
                Compress.CheckedGlyph = Compress.Glyph;
                Compress.IsChecked = video.Compression != video.MaxCompression;
                text.Text = video.ToString();
            };

            var stack = new StackPanel { Width = 260 };
            stack.Children.Add(slider);
            stack.Children.Add(text);

            var flyout = new Flyout { Content = stack };

            if (ApiInfo.CanUseNewFlyoutPlacementMode)
            {
                flyout.ShowAt(button.Parent as UIElement, new Windows.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Placement = Windows.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.TopEdgeAlignedRight });
            }
            else
            {
                flyout.ShowAt(button.Parent as FrameworkElement);
            }
        }

        private void Ttl_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Windows.UI.Xaml.Controls.Primitives.ToggleButton button)) return;
            var slider = new Slider
            {
                IsThumbToolTipEnabled = false,
                Header = MessageTtlConverter.Convert(MessageTtlConverter.ConvertSeconds(_media.Ttl)),
                Minimum = 0,
                Maximum = 28,
                StepFrequency = 1,
                SmallChange = 1,
                LargeChange = 1,
                Value = MessageTtlConverter.ConvertSeconds(_media.Ttl)
            };
            slider.ValueChanged += (s, args) =>
            {
                var index = (int)args.NewValue;
                var label = MessageTtlConverter.Convert(index);

                slider.Header = label;
                _media.Ttl = MessageTtlConverter.ConvertBack(index);
                Ttl.IsChecked = _media.Ttl > 0;
            };

            var text = new TextBlock
            {
                Style = App.Current.Resources["InfoCaptionTextBlockStyle"] as Style,
                TextWrapping = TextWrapping.Wrap,
                Text = _media is StoragePhoto
                    ? Strings.Resources.MessageLifetimePhoto
                    : Strings.Resources.MessageLifetimeVideo
            };

            var stack = new StackPanel { Width = 260 };
            stack.Children.Add(slider);
            stack.Children.Add(text);

            var flyout = new Flyout { Content = stack };

            if (ApiInfo.CanUseNewFlyoutPlacementMode)
            {
                flyout.ShowAt(button.Parent as UIElement, new Windows.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Placement = Windows.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.TopEdgeAlignedRight });
            }
            else
            {
                flyout.ShowAt(button.Parent as FrameworkElement);
            }
        }

        private void Brush_Click(object sender, RoutedEventArgs e)
        {
            if (Canvas.Mode != PencilCanvasMode.Stroke)
            {
                Canvas.Mode = PencilCanvasMode.Stroke;
                Brush.IsChecked = true;
                Erase.IsChecked = false;
            }
        }

        private void Erase_Click(object sender, RoutedEventArgs e)
        {
            if (Canvas.Mode != PencilCanvasMode.Eraser)
            {
                Canvas.Mode = PencilCanvasMode.Eraser;
                Brush.IsChecked = false;
                Erase.IsChecked = true;
            }
        }

        private void Undo_Click(object sender, RoutedEventArgs e)
        {
            Canvas.Undo();
        }

        private void Redo_Click(object sender, RoutedEventArgs e)
        {
            Canvas.Redo();
        }

        private void DrawSlider_StrokeChanged(object sender, EventArgs e)
        {
            Canvas.Stroke = DrawSlider.Stroke;
            Canvas.StrokeThickness = _thicknessValues[_thicknessIndex];

            foreach (var preset in _thicknessPresets)
            {
                preset.Dot.Fill = new SolidColorBrush(DrawSlider.Stroke);
            }

            Brush_Click(null, null);
        }

        private void BuildThicknessPresets()
        {
            var accent = Application.Current.Resources["TelegramBackgroundAccentBrush"] as SolidColorBrush;

            for (int i = 0; i < _thicknessValues.Length; i++)
            {
                var dot = new Ellipse
                {
                    Width = _thicknessValues[i] * 1.5,
                    Height = _thicknessValues[i] * 1.5,
                    Fill = new SolidColorBrush(DrawSlider.Stroke),
                    Stroke = new SolidColorBrush(Colors.White),
                    StrokeThickness = 1,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };

                // The ring is only shown for the selected preset; the dot must
                // stay visible for every preset, so it cannot be a child of the
                // (collapsed) ring.
                var ring = new Border
                {
                    Width = 36,
                    Height = 36,
                    CornerRadius = new CornerRadius(18),
                    BorderBrush = accent,
                    BorderThickness = new Thickness(2),
                    Visibility = Visibility.Collapsed
                };

                // Fixed-size content (36x36, the ring size) so showing/hiding
                // the selection ring never shifts the layout.
                var content = new Grid { Width = 36, Height = 36 };
                content.Children.Add(dot);
                content.Children.Add(ring);

                var button = new Button
                {
                    Content = content,
                    Background = new SolidColorBrush(Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(3),
                    Margin = new Thickness(2, 0, 2, 0),
                    MinWidth = 0,
                    MinHeight = 0,
                    Tag = i
                };
                button.Click += ThicknessPreset_Click;

                ThicknessPanel.Children.Add(button);
                _thicknessPresets.Add((ring, dot));
            }
        }

        private void ThicknessPreset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is int index)
            {
                SelectThicknessPreset(index);
            }
        }

        private void SelectThicknessPreset(int index)
        {
            // The index comes back out of a saved setting as well as out of a button, and
            // the setting is a float that nothing keeps inside the range this array has.
            _thicknessIndex = Math.Max(0, Math.Min(_thicknessValues.Length - 1, index));

            for (int i = 0; i < _thicknessPresets.Count; i++)
            {
                _thicknessPresets[i].Ring.Visibility = i == _thicknessIndex ? Visibility.Visible : Visibility.Collapsed;
            }

            DrawSlider.SetDefault(new Vector2(DrawSlider.GetDefault().X, (float)_thicknessIndex / (_thicknessValues.Length - 1)));

            Canvas.Stroke = DrawSlider.Stroke;
            Canvas.StrokeThickness = _thicknessValues[_thicknessIndex];

            Brush_Click(null, null);
        }

        private void Canvas_StrokesChanged(object sender, EventArgs e)
        {
            InvalidateToolbar();
        }

        private void InvalidateToolbar()
        {
            if (Undo != null)
            {
                Undo.IsEnabled = Canvas.CanUndo;
            }

            if (Redo != null)
            {
                Redo.IsEnabled = Canvas.CanRedo;
            }
        }

        private async void MediaPlayer_PositionChanged(Windows.Media.Playback.MediaPlaybackSession sender, object args)
        {
            await TrimRange.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
            {
                if (sender.Position.TotalMilliseconds >= TrimRange.Maximum * sender.NaturalDuration.TotalMilliseconds)
                {
                    sender.Position = TimeSpan.FromMilliseconds(sender.NaturalDuration.TotalMilliseconds * TrimRange.Minimum);
                    return;
                }

                TrimRange.Value = sender.Position.TotalMilliseconds / sender.NaturalDuration.TotalMilliseconds;
            });
        }

        private void TrimRange_MinimumChanged(object sender, double e)
        {
            Media.MediaPlayer.Pause();
            Media.MediaPlayer.PlaybackSession.Position =
                TimeSpan.FromMilliseconds(Media.MediaPlayer.PlaybackSession.NaturalDuration.TotalMilliseconds * e);
        }

        private void TrimRange_MaximumChanged(object sender, double e)
        {
            Media.MediaPlayer.PlaybackSession.Position =
                TimeSpan.FromMilliseconds(Media.MediaPlayer.PlaybackSession.NaturalDuration.TotalMilliseconds * e);
            Media.MediaPlayer.Play();
        }

#region Media Description Events
        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Window.Current.CoreWindow.CharacterReceived += OnCharacterReceived;
            Windows.UI.ViewManagement.InputPane.GetForCurrentView().Showing += InputPane_Showing;
            Windows.UI.ViewManagement.InputPane.GetForCurrentView().Hiding += InputPane_Hiding;
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            Window.Current.CoreWindow.CharacterReceived -= OnCharacterReceived;
            Windows.UI.ViewManagement.InputPane.GetForCurrentView().Showing -= InputPane_Showing;
            Windows.UI.ViewManagement.InputPane.GetForCurrentView().Hiding -= InputPane_Hiding;
        }

        private void OnCharacterReceived(Windows.UI.Core.CoreWindow sender, Windows.UI.Core.CharacterReceivedEventArgs args)
        {
            var character = System.Text.Encoding.UTF32.GetString(BitConverter.GetBytes(args.KeyCode));
            if (character.Length == 0 || (char.IsControl(character[0]) && character != "\u0016" && character != "\r") || char.IsWhiteSpace(character[0]))
            {
                return;
            }

            var focused = Windows.UI.Xaml.Input.FocusManager.GetFocusedElement();
            if (focused != CaptionInput)
            {
                if (character == "\u0016" && CaptionInput.Document.Selection.CanPaste(0))
                {
                    CaptionInput.Focus(FocusState.Keyboard);
                    CaptionInput.Document.Selection.Paste(0);
                }
                //else if (character == "\r")
                //{
                //    Accept_Click(null, null);
                //}
                else
                {
                    CaptionInput.Focus(FocusState.Keyboard);
                    CaptionInput.InsertText(character);
                }

                args.Handled = true;
            }
        }


        private void InputPane_Showing(Windows.UI.ViewManagement.InputPane sender, Windows.UI.ViewManagement.InputPaneVisibilityEventArgs args)
        {
            args.EnsuredFocusedElementInView = Windows.UI.Xaml.Input.FocusManager.GetFocusedElement() is FormattedTextBox;
            KeyboardPlaceholder.Height = new GridLength(args.OccludedRect.Height);
        }

        private void InputPane_Hiding(Windows.UI.ViewManagement.InputPane sender, Windows.UI.ViewManagement.InputPaneVisibilityEventArgs args)
        {
            KeyboardPlaceholder.Height = new GridLength(1, GridUnitType.Auto);
        }
#endregion
    }

    public sealed class SmoothPathBuilder
    {
        private List<Vector2> _controlPoints;
        private List<Vector2> _path;

        private Vector2 _beginPoint;

        public SmoothPathBuilder(Vector2 beginPoint)
        {
            _beginPoint = beginPoint;

            _controlPoints = new List<Vector2>();
            _path = new List<Vector2>();
        }

        public Color? Stroke { get; set; }
        public float StrokeThickness { get; set; }

        public void MoveTo(Vector2 point)
        {
            if (_controlPoints.Count < 4)
            {
                _controlPoints.Add(point);
                return;
            }

            var endPoint = new Vector2(
                (_controlPoints[2].X + point.X) / 2,
                (_controlPoints[2].Y + point.Y) / 2);

            _path.Add(_controlPoints[1]);
            _path.Add(_controlPoints[2]);
            _path.Add(endPoint);

            _controlPoints = new List<Vector2> { endPoint, point };
        }

        public void EndFigure(Vector2 point)
        {
            if (_controlPoints.Count > 1)
            {
                for (int i = 0; i < _controlPoints.Count; i++)
                {
                    MoveTo(point);
                }
            }
        }

        public CanvasGeometry ToGeometry(ICanvasResourceCreator resourceCreator, Vector2 canvasSize)
        {
            //var multiplier = NSMakePoint(imageSize.width / touch.canvasSize.width, imageSize.height / touch.canvasSize.height)
            var multiplier = canvasSize; //_imageSize / canvasSize;

            var builder = new CanvasPathBuilder(resourceCreator);
            builder.BeginFigure(_beginPoint * multiplier);

            for (int i = 0; i < _path.Count; i += 3)
            {
                builder.AddCubicBezier(
                    _path[i] * multiplier,
                    _path[i + 1] * multiplier,
                    _path[i + 2] * multiplier);
            }

            builder.EndFigure(CanvasFigureLoop.Open);

            return CanvasGeometry.CreatePath(builder);
        }
    }
}
