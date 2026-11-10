using System;
using Windows.UI.Xaml;
using Telegram.Td.Api;
using Unigram.Common;
using Unigram.Services;
using Windows.UI.Xaml.Controls;

namespace Unigram.Controls
{
    public class MenuFlyoutMediaItem : MenuFlyoutSeparator
    {
        private readonly object _media;

        public MenuFlyoutMediaItem(ViewModels.Drawers.StickerViewModel sticker) : base()
        {
            DefaultStyleKey = typeof(MenuFlyoutMediaItem);
            _media = sticker;
        }

        public MenuFlyoutMediaItem(Animation animation) : base()
        {
            DefaultStyleKey = typeof(MenuFlyoutMediaItem);
            _media = animation;
        }

        protected override void OnApplyTemplate()
        {
            base.OnApplyTemplate();

            UpdatePreview(true);
        }

        public void UpdatePreview(bool getEmojis = false) {
            if (GetTemplateChild("Aspect") is AspectView aspect &&
                GetTemplateChild("TitleScroll") is ScrollViewer titleScroll &&
                GetTemplateChild("Title") is TextBlock title &&
                GetTemplateChild("Thumbnail") is Image thumbnail &&
                GetTemplateChild("Container") is Border container &&
                GetTemplateChild("Texture") is ImageView texture)
            {
                switch (_media)
                {
                    case ViewModels.Drawers.StickerViewModel sticker:
                        titleScroll.Visibility = Visibility.Visible;
                        aspect.MaxWidth = 200;
                        aspect.MaxHeight = 200;
                        aspect.Constraint = sticker;

                        if (sticker.Thumbnail != null)
                        {
                            UpdateThumbnail(sticker.Thumbnail.File, ref thumbnail);
                        }

                        UpdateFile(sticker, sticker.StickerValue, ref thumbnail, ref texture, ref container);

                        if (getEmojis)
                            this.BeginOnUIThread(async () => {
                                if (await sticker.ProtoService.SendAsync(new GetStickerEmojis(new InputFileId(sticker.StickerValue.Id))) is Emojis emojis)
                                {
                                    title.Text = string.Join(" ", emojis.EmojisValue);
                                }
                            });
                        break;

                    case Animation animation:
                        titleScroll.Visibility = Visibility.Collapsed;
                        aspect.MaxWidth = 320;
                        aspect.MaxHeight = 420;
                        aspect.Constraint = animation;

                        if (animation.Thumbnail != null && animation.Thumbnail.Format is ThumbnailFormatJpeg)
                        {
                            UpdateThumbnail(animation.Thumbnail.File, ref thumbnail);
                        }

                        UpdateFile(animation, animation.AnimationValue, ref thumbnail, ref texture, ref container);
                        break;
                }
            }
        }

        private void UpdateFile(ViewModels.Drawers.StickerViewModel sticker, File file, ref Image thumbnail, ref ImageView texture, ref Border container)
        {
            if (file.Local.IsDownloadingCompleted)
            {
                if (ModernTdlibCompatibility.GetStickerIsAnimated(sticker))
                {
                    thumbnail.Opacity = 0;
                    texture.Source = null;
                    container.Child = new LottieView { Source = new Uri("file:///" + file.Local.Path) };
                }
                else
                {
                    thumbnail.Opacity = 0;
                    texture.Source = PlaceholderHelper.GetWebPFrame(file.Local.Path);
                    container.Child = new Border();
                }
            }
            else if (file.Local.CanBeDownloaded && !file.Local.IsDownloadingActive)
            {
                thumbnail.Opacity = 1;
                texture.Source = null;
                container.Child = new Border();

                sticker.ProtoService.DownloadFile(file.Id, 32);
            }
        }

        private void UpdateFile(Animation animation, File file, ref Image thumbnail, ref ImageView texture, ref Border container)
        {
            if (file.Local.IsDownloadingCompleted)
            {
                thumbnail.Opacity = 0;
                texture.Source = null;
                container.Child = new AnimationView { Source = new Uri("file:///" + file.Local.Path) };
            }
            else if (file.Local.CanBeDownloaded && !file.Local.IsDownloadingActive)
            {
                thumbnail.Opacity = 1;
                texture.Source = null;
                container.Child = new Border();
            }
            //var player = new Windows.Media.Playback.MediaPlayer
            //{
            //    AutoPlay = true,
            //    IsMuted = false,
            //    IsLoopingEnabled = true,
            //    Source = Windows.Media.Core.MediaSource.CreateFromUri(UriEx.GetLocal(file.Local.Path))
            //};
            //container.Child = new MediaPlayerView
            //{
            //    MediaPlayer = player
            //};
        }

        private void UpdateThumbnail(File file, ref Image thumbnail)
        {
            if (file.Local.IsDownloadingCompleted)
            {
                thumbnail.Source = PlaceholderHelper.GetWebPFrame(file.Local.Path);
            }
            else if (file.Local.CanBeDownloaded && !file.Local.IsDownloadingActive)
            {
                thumbnail.Source = null;
                Logs.Logger.Warning(Logs.Target.API, "Thumbnail not downloaded yet.", "MenuFlyoutMediaItem"); // If ever called - implement download & update here
            }
        }        
    }
}
