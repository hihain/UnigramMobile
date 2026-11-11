using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Telegram.Td.Api;
using Unigram.Converters;
using Unigram.Logs;
using Unigram.Services;
using Unigram.ViewModels;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.Text;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Documents;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace Unigram.Common
{
    // Renders TDLib rich messages (messageRichMessage, article-like channel posts made of
    // PageBlocks) as inline text inside a message bubble. Media blocks become links that
    // open the full post in InstantPage, which renders photos and other media.
    public static class RichMessageRenderer
    {
        private const string NavigationPrefix = "tg-richmessage:";

        public static string GetNavigationParameter(long chatId, long messageId)
        {
            return NavigationPrefix + chatId.ToString(CultureInfo.InvariantCulture) + ":" + messageId.ToString(CultureInfo.InvariantCulture);
        }

        public static bool TryParseNavigationParameter(string value, out long chatId, out long messageId)
        {
            chatId = 0;
            messageId = 0;

            if (value == null || !value.StartsWith(NavigationPrefix, StringComparison.Ordinal))
            {
                return false;
            }

            var parts = value.Substring(NavigationPrefix.Length).Split(':');
            return parts.Length == 2
                && long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out chatId)
                && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out messageId);
        }

#if MODERN_TDLIB
        // Handles RichText types added after TDLib 1.8.0: returns either the wrapped text
        // or a plain-text replacement for leaf types.
        public static bool TryUnwrap(RichText text, out RichText inner, out string plain)
        {
            inner = null;
            plain = null;

            switch (text)
            {
                case RichTextSpoiler spoiler:
                    inner = spoiler.Text;
                    return true;
                case RichTextMention mention:
                    inner = mention.Text;
                    return true;
                case RichTextHashtag hashtag:
                    inner = hashtag.Text;
                    return true;
                case RichTextCashtag cashtag:
                    inner = cashtag.Text;
                    return true;
                case RichTextBankCardNumber bankCardNumber:
                    inner = bankCardNumber.Text;
                    return true;
                case RichTextBotCommand botCommand:
                    inner = botCommand.Text;
                    return true;
                case RichTextMentionName mentionName:
                    inner = mentionName.Text;
                    return true;
                case RichTextDateTime dateTime:
                    inner = dateTime.Text;
                    return true;
                case RichTextReference reference:
                    inner = reference.Text;
                    return true;
                case RichTextReferenceLink referenceLink:
                    inner = referenceLink.Text;
                    return true;
                case RichTextDiff diff:
                    inner = diff.Text;
                    return true;
                case RichTextCustomEmoji customEmoji:
                    plain = customEmoji.AlternativeText;
                    return true;
                case RichTextMathematicalExpression expression:
                    plain = expression.Expression;
                    return true;
                default:
                    return false;
            }
        }

        public static string GetSummary(RichMessage message)
        {
            if (message?.Blocks == null)
            {
                return string.Empty;
            }

            foreach (var block in message.Blocks)
            {
                var text = GetBlockText(block)?.ToPlainText();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text.Trim();
                }
            }

            return string.Empty;
        }

        private static RichText GetBlockText(PageBlock block)
        {
            switch (block)
            {
                case PageBlockTitle title:
                    return title.Title;
                case PageBlockSubtitle subtitle:
                    return subtitle.Subtitle;
                case PageBlockHeader header:
                    return header.Header;
                case PageBlockSubheader subheader:
                    return subheader.Subheader;
                case PageBlockSectionHeading sectionHeading:
                    return sectionHeading.Text;
                case PageBlockKicker kicker:
                    return kicker.Kicker;
                case PageBlockParagraph paragraph:
                    return paragraph.Text;
                case PageBlockPreformatted preformatted:
                    return preformatted.Text;
                case PageBlockFooter footer:
                    return footer.Footer;
                case PageBlockThinking thinking:
                    return thinking.Text;
                case PageBlockPullQuote pullQuote:
                    return pullQuote.Text;
                case PageBlockCover cover:
                    return GetBlockText(cover.Cover);
                default:
                    return null;
            }
        }

        private sealed class RenderContext
        {
            public MessageViewModel Message;
            public Brush LinkBrush;
            public bool First = true;
            public bool SuppressBreak;
            public int QuoteDepth;
            public bool NeedsFullView;
        }

        public static bool Render(MessageViewModel message, Span span, RichMessage rich, Brush linkBrush)
        {
            if (rich?.Blocks == null || rich.Blocks.Count == 0)
            {
                return false;
            }

            var context = new RenderContext { Message = message, LinkBrush = linkBrush };

            foreach (var block in rich.Blocks)
            {
                RenderBlock(context, span, block);
            }

            if (context.NeedsFullView || !rich.IsFull)
            {
                BeginBlock(context, span, false);
                AddActionLink(context, span, Strings.Resources.InstantView, () => message.Delegate?.OpenRichMessage(message));
            }

            return !context.First;
        }

        private static void BeginBlock(RenderContext context, Span span, bool tight)
        {
            if (context.SuppressBreak)
            {
                context.SuppressBreak = false;
            }
            else if (!context.First)
            {
                span.Inlines.Add(new LineBreak());

                if (!tight)
                {
                    span.Inlines.Add(new LineBreak());
                }
            }

            context.First = false;

            for (int i = 0; i < context.QuoteDepth; i++)
            {
                span.Inlines.Add(new Run { Text = "\u258E ", Foreground = context.LinkBrush });
            }
        }

        private static void AddTextBlock(RenderContext context, Span span, RichText text, Action<Span> style, bool tight = false)
        {
            if (text.IsNullOrEmpty())
            {
                return;
            }

            BeginBlock(context, span, tight);

            var inner = new Span();
            style?.Invoke(inner);
            span.Inlines.Add(inner);
            RenderText(context, text, inner, false);
        }

        private static void RenderBlock(RenderContext context, Span span, PageBlock block, bool tight = false)
        {
            switch (block)
            {
                case PageBlockTitle title:
                    AddTextBlock(context, span, title.Title, x => { x.FontWeight = FontWeights.SemiBold; x.FontSize = 20; }, tight);
                    break;
                case PageBlockSubtitle subtitle:
                    AddTextBlock(context, span, subtitle.Subtitle, x => x.FontWeight = FontWeights.SemiBold, tight);
                    break;
                case PageBlockHeader header:
                    AddTextBlock(context, span, header.Header, x => { x.FontWeight = FontWeights.SemiBold; x.FontSize = 18; }, tight);
                    break;
                case PageBlockSubheader subheader:
                    AddTextBlock(context, span, subheader.Subheader, x => x.FontWeight = FontWeights.SemiBold, tight);
                    break;
                case PageBlockSectionHeading sectionHeading:
                    AddTextBlock(context, span, sectionHeading.Text, x => x.FontWeight = FontWeights.SemiBold, tight);
                    break;
                case PageBlockKicker kicker:
                    AddTextBlock(context, span, kicker.Kicker, x => x.FontWeight = FontWeights.SemiBold, tight);
                    break;
                case PageBlockParagraph paragraph:
                    AddTextBlock(context, span, paragraph.Text, null, tight);
                    break;
                case PageBlockThinking thinking:
                    AddTextBlock(context, span, thinking.Text, x => x.FontStyle = FontStyle.Italic, tight);
                    break;
                case PageBlockFooter footer:
                    AddTextBlock(context, span, footer.Footer, x => x.FontSize = 13, tight);
                    break;
                case PageBlockPreformatted preformatted:
                    AddTextBlock(context, span, preformatted.Text, x => x.FontFamily = new FontFamily("Consolas"), tight);
                    break;
                case PageBlockMathematicalExpression expression:
                    AddTextBlock(context, span, new RichTextPlain(expression.Expression ?? string.Empty), x => x.FontFamily = new FontFamily("Consolas"), tight);
                    break;
                case PageBlockAuthorDate authorDate:
                    RenderAuthorDate(context, span, authorDate, tight);
                    break;
                case PageBlockDivider _:
                    BeginBlock(context, span, tight);
                    span.Inlines.Add(new Run { Text = "\u2014\u2014\u2014" });
                    break;
                case PageBlockAnchor _:
                    break;
                case PageBlockCover cover:
                    RenderBlock(context, span, cover.Cover, tight);
                    break;
                case PageBlockList list:
                    RenderList(context, span, list, tight);
                    break;
                case PageBlockBlockQuote blockQuote:
                    context.QuoteDepth++;
                    var firstQuoted = true;
                    foreach (var inner in blockQuote.Blocks)
                    {
                        RenderBlock(context, span, inner, firstQuoted ? tight : true);
                        firstQuoted = false;
                    }
                    AddTextBlock(context, span, blockQuote.Credit, x => x.FontWeight = FontWeights.SemiBold, true);
                    context.QuoteDepth--;
                    break;
                case PageBlockPullQuote pullQuote:
                    context.QuoteDepth++;
                    AddTextBlock(context, span, pullQuote.Text, x => x.FontStyle = FontStyle.Italic, tight);
                    AddTextBlock(context, span, pullQuote.Credit, x => x.FontWeight = FontWeights.SemiBold, true);
                    context.QuoteDepth--;
                    break;
                case PageBlockDetails details:
                    AddTextBlock(context, span, details.Header, x => x.FontWeight = FontWeights.SemiBold, tight);
                    foreach (var inner in details.Blocks)
                    {
                        RenderBlock(context, span, inner, true);
                    }
                    break;
                case PageBlockPhoto photo:
                    if (!AddPhotoBlock(context, span, photo.Photo, photo.Caption, tight))
                    {
                        AddMediaBlock(context, span, Strings.Resources.AttachPhoto, photo.Caption, tight);
                    }
                    break;
                case PageBlockVideo video:
                    AddThumbnailBlock(context, span, video.Video?.Thumbnail);
                    AddMediaBlock(context, span, Strings.Resources.AttachVideo, video.Caption, true);
                    break;
                case PageBlockAnimation animation:
                    AddThumbnailBlock(context, span, animation.Animation?.Thumbnail);
                    AddMediaBlock(context, span, Strings.Resources.AttachGif, animation.Caption, true);
                    break;
                case PageBlockAudio audio:
                    AddMediaBlock(context, span, Strings.Resources.AttachMusic, audio.Caption, tight);
                    break;
                case PageBlockVoiceNote voiceNote:
                    AddMediaBlock(context, span, Strings.Resources.AttachAudio, voiceNote.Caption, tight);
                    break;
                case PageBlockCollage collage:
                    RenderMediaGroup(context, span, collage.Blocks, collage.Caption, tight);
                    break;
                case PageBlockSlideshow slideshow:
                    RenderMediaGroup(context, span, slideshow.Blocks, slideshow.Caption, tight);
                    break;
                case PageBlockMap map:
                    AddMediaBlock(context, span, Strings.Resources.AttachLocation, map.Caption, tight);
                    break;
                case PageBlockEmbedded embedded:
                    AddMediaBlock(context, span, string.IsNullOrEmpty(embedded.Url) ? Strings.Resources.InstantView : embedded.Url, embedded.Caption, tight);
                    break;
                case PageBlockEmbeddedPost embeddedPost:
                    AddMediaBlock(context, span, string.IsNullOrEmpty(embeddedPost.Author) ? Strings.Resources.InstantView : embeddedPost.Author, embeddedPost.Caption, tight);
                    break;
                case PageBlockTable table:
                    AddMediaBlock(context, span, table.Caption.IsNullOrEmpty() ? Strings.Resources.InstantView : table.Caption.ToPlainText(), null, tight);
                    break;
                case PageBlockRelatedArticles relatedArticles:
                    AddTextBlock(context, span, relatedArticles.Header, x => x.FontWeight = FontWeights.SemiBold, tight);
                    context.NeedsFullView = true;
                    break;
                case PageBlockChatLink chatLink:
                    BeginBlock(context, span, tight);
                    if (string.IsNullOrEmpty(chatLink.Username))
                    {
                        span.Inlines.Add(new Run { Text = chatLink.Title ?? string.Empty });
                    }
                    else
                    {
                        var username = chatLink.Username;
                        AddActionLink(context, span, chatLink.Title ?? "@" + username, () => context.Message.Delegate?.OpenUsername(username));
                    }
                    break;
                default:
                    context.NeedsFullView = true;
                    break;
            }
        }

        private static void RenderAuthorDate(RenderContext context, Span span, PageBlockAuthorDate block, bool tight)
        {
            var hasAuthor = !block.Author.IsNullOrEmpty();
            if (!hasAuthor && block.PublishDate == 0)
            {
                return;
            }

            BeginBlock(context, span, tight);

            var inner = new Span { FontSize = 13 };
            span.Inlines.Add(inner);

            if (hasAuthor)
            {
                RenderText(context, block.Author, inner, false);
            }

            if (block.PublishDate != 0)
            {
                if (hasAuthor)
                {
                    inner.Inlines.Add(new Run { Text = " \u2014 " });
                }

                inner.Inlines.Add(new Run { Text = BindConvert.Current.DayMonthFullYear.Format(BindConvert.Current.DateTime(block.PublishDate)) });
            }
        }

        private static void RenderList(RenderContext context, Span span, PageBlockList list, bool tight)
        {
            var firstItem = true;

            foreach (var item in list.Items)
            {
                BeginBlock(context, span, tight || !firstItem);
                firstItem = false;

                var label = string.IsNullOrEmpty(item.Label) ? "\u2022" : item.Label;
                if (item.HasCheckbox)
                {
                    label = item.IsChecked ? "\u2611" : "\u2610";
                }

                span.Inlines.Add(new Run { Text = label + " " });

                // The first block of an item continues on the label's line.
                context.SuppressBreak = true;
                foreach (var inner in item.Blocks)
                {
                    RenderBlock(context, span, inner, true);
                }

                context.SuppressBreak = false;
            }
        }

        // Mirrors upstream PageBlockCollageContent: consecutive photos are laid out with the
        // album mosaic. Chunks are capped at the Telegram album size because the mosaic
        // only produces layouts for up to 3 items per row and 4 rows.
        private const int MaxMosaicItems = 10;
        private const double MosaicLayoutSize = 432;

        private static void RenderMediaGroup(RenderContext context, Span span, IList<PageBlock> blocks, PageBlockCaption caption, bool tight)
        {
            var first = true;
            var pending = new List<PhotoSize>();

            void Flush()
            {
                for (int start = 0; start < pending.Count; start += MaxMosaicItems)
                {
                    var count = Math.Min(MaxMosaicItems, pending.Count - start);
                    var chunk = pending.GetRange(start, count);

                    BeginBlock(context, span, first ? tight : true);
                    first = false;

                    if (chunk.Count == 1)
                    {
                        AddImage(context, span, chunk[0].Photo, chunk[0].Width, chunk[0].Height);
                    }
                    else
                    {
                        AddMosaic(context, span, chunk);
                    }
                }

                pending.Clear();
            }

            foreach (var inner in blocks)
            {
                var size = inner is PageBlockPhoto photo ? SelectPhotoSize(photo.Photo) : null;
                if (size != null && size.Width > 0 && size.Height > 0)
                {
                    pending.Add(size);
                    continue;
                }

                Flush();
                RenderBlock(context, span, inner, first ? tight : true);
                first = false;
            }

            Flush();
            AddCaption(context, span, caption, true);
        }

        private static void AddMosaic(RenderContext context, Span span, List<PhotoSize> sizes)
        {
            var layout = MosaicAlbumLayout.chatMessageBubbleMosaicLayout(new Size(MosaicLayoutSize, MosaicLayoutSize), sizes.Select(x => new Size(x.Width, x.Height)));
            if (layout.Item1 == null || layout.Item1.Length != sizes.Count || layout.Item2.Width <= 0 || layout.Item2.Height <= 0)
            {
                PushDiagnostics.Write("richmessage.collage.fallback", "items=" + sizes.Count.ToString(CultureInfo.InvariantCulture));

                for (int i = 0; i < sizes.Count; i++)
                {
                    if (i > 0)
                    {
                        BeginBlock(context, span, true);
                    }

                    AddImage(context, span, sizes[i].Photo, sizes[i].Width, sizes[i].Height);
                }

                return;
            }

            var ratio = MaxImageWidth / layout.Item2.Width;
            var panel = new Grid
            {
                Width = Math.Round(layout.Item2.Width * ratio),
                Height = Math.Round(layout.Item2.Height * ratio),
                Margin = new Thickness(0, 2, 0, 2)
            };

            for (int i = 0; i < sizes.Count; i++)
            {
                var rect = layout.Item1[i].Item1;
                var position = layout.Item1[i].Item2;

                var x = Math.Round(rect.X * ratio);
                var y = Math.Round(rect.Y * ratio);
                var width = Math.Round(rect.Width * ratio);
                var height = Math.Round(rect.Height * ratio);

                // Same 1px seams as upstream MessageAlbumBase.GetPositionsForWidth.
                if (height >= 1 && !position.HasFlag(MosaicItemPosition.Top))
                {
                    y += 1;
                    height -= 1;
                }

                if (width >= 1 && !position.HasFlag(MosaicItemPosition.Left))
                {
                    x += 1;
                    width -= 1;
                }

                if (width < 1 || height < 1)
                {
                    continue;
                }

                var tile = CreateImageTile(context, sizes[i].Photo, width, height);
                tile.HorizontalAlignment = HorizontalAlignment.Left;
                tile.VerticalAlignment = VerticalAlignment.Top;
                tile.Margin = new Thickness(x, y, 0, 0);
                panel.Children.Add(tile);
            }

            span.Inlines.Add(new InlineUIContainer { Child = panel });
        }

        private static PhotoSize SelectPhotoSize(Photo photo)
        {
            PhotoSize best = null;
            if (photo?.Sizes != null)
            {
                foreach (var size in photo.Sizes)
                {
                    if (size?.Photo == null)
                    {
                        continue;
                    }

                    if (best == null
                        || (size.Width <= PreferredSourceWidth && (best.Width > PreferredSourceWidth || size.Width > best.Width))
                        || (size.Width > PreferredSourceWidth && best.Width > PreferredSourceWidth && size.Width < best.Width))
                    {
                        best = size;
                    }
                }
            }

            return best;
        }

        private static bool AddPhotoBlock(RenderContext context, Span span, Photo photo, PageBlockCaption caption, bool tight)
        {
            var best = SelectPhotoSize(photo);
            if (best == null)
            {
                return false;
            }

            BeginBlock(context, span, tight);
            AddImage(context, span, best.Photo, best.Width, best.Height);
            AddCaption(context, span, caption, true);
            return true;
        }

        private static void AddThumbnailBlock(RenderContext context, Span span, Thumbnail thumbnail)
        {
            if (thumbnail?.File == null || !(thumbnail.Format is ThumbnailFormatJpeg))
            {
                return;
            }

            BeginBlock(context, span, false);
            AddImage(context, span, thumbnail.File, thumbnail.Width, thumbnail.Height);
        }

        private static void AddCaption(RenderContext context, Span span, PageBlockCaption caption, bool tight)
        {
            if (caption == null)
            {
                return;
            }

            AddTextBlock(context, span, caption.Text, x => { x.FontStyle = FontStyle.Italic; x.FontSize = 13; }, tight);
            AddTextBlock(context, span, caption.Credit, x => x.FontSize = 13, true);
        }

        private const int PreferredSourceWidth = 800;
        private const double MaxImageWidth = 260;
        private const double MaxImageHeight = 360;

        private static void AddImage(RenderContext context, Span span, File file, int sourceWidth, int sourceHeight)
        {
            var width = sourceWidth > 0 ? Math.Min(MaxImageWidth, sourceWidth) : MaxImageWidth;
            var height = sourceWidth > 0 && sourceHeight > 0 ? width * sourceHeight / sourceWidth : width * 2 / 3;
            if (height > MaxImageHeight)
            {
                width = width * MaxImageHeight / height;
                height = MaxImageHeight;
            }

            var border = CreateImageTile(context, file, width, height);
            border.Margin = new Thickness(0, 2, 0, 2);
            span.Inlines.Add(new InlineUIContainer { Child = border });
        }

        private static Border CreateImageTile(RenderContext context, File file, double width, double height)
        {
            var image = new Image { Stretch = Stretch.UniformToFill, Width = width, Height = height };
            var border = new Border
            {
                Width = width,
                Height = height,
                Background = new SolidColorBrush(Color.FromArgb(0x33, 0x80, 0x80, 0x80)),
                Child = image
            };

            var message = context.Message;
            border.Tapped += (s, args) =>
            {
                args.Handled = true;
                message.Delegate?.OpenRichMessage(message);
            };

            LoadImage(message.ProtoService, file, image, (int)Math.Ceiling(width));
            return border;
        }

        private static async void LoadImage(IProtoService protoService, File file, Image image, int decodeWidth)
        {
            try
            {
                if (!file.Local.IsDownloadingCompleted)
                {
                    var response = await protoService.SendAsync(new DownloadFile(file.Id, 1, 0, 0, true));
                    if (!(response is File downloaded))
                    {
                        return;
                    }

                    file = downloaded;
                }

                if (file.Local.IsDownloadingCompleted && !string.IsNullOrEmpty(file.Local.Path))
                {
                    image.Source = new BitmapImage(new Uri("file:///" + file.Local.Path))
                    {
                        DecodePixelType = DecodePixelType.Logical,
                        DecodePixelWidth = decodeWidth
                    };
                }
            }
            catch (Exception ex)
            {
                PushDiagnostics.WriteException("richmessage.image.failed", ex);
            }
        }

        private static void AddMediaBlock(RenderContext context, Span span, string label, PageBlockCaption caption, bool tight)
        {
            context.NeedsFullView = true;

            BeginBlock(context, span, tight);
            AddActionLink(context, span, "[" + label + "]", () => context.Message.Delegate?.OpenRichMessage(context.Message));

            if (caption != null && !caption.Text.IsNullOrEmpty())
            {
                span.Inlines.Add(new Run { Text = " " });

                var inner = new Span { FontStyle = FontStyle.Italic };
                span.Inlines.Add(inner);
                RenderText(context, caption.Text, inner, false);
            }
        }

        private static void AddActionLink(RenderContext context, Span span, string text, Action action)
        {
            var hyperlink = new Hyperlink { Foreground = context.LinkBrush };
            hyperlink.Click += (s, args) => action();
            hyperlink.Inlines.Add(new Run { Text = text });
            span.Inlines.Add(hyperlink);
        }

        private static void AddLink(RenderContext context, Span span, RichText text, bool inLink, Action action)
        {
            if (inLink)
            {
                RenderText(context, text, span, true);
                return;
            }

            var hyperlink = new Hyperlink { Foreground = context.LinkBrush };
            hyperlink.Click += (s, args) => action();
            span.Inlines.Add(hyperlink);
            RenderText(context, text, hyperlink, true);
        }

        private static Span AddStyled(Span span, Action<Span> style)
        {
            var inner = new Span();
            style(inner);
            span.Inlines.Add(inner);
            return inner;
        }

        private static void RenderText(RenderContext context, RichText text, Span span, bool inLink)
        {
            var message = context.Message;

            switch (text)
            {
                case null:
                    break;
                case RichTextPlain plain:
                    if (!string.IsNullOrEmpty(plain.Text))
                    {
                        span.Inlines.Add(new Run { Text = plain.Text });
                    }
                    break;
                case RichTexts texts:
                    foreach (var child in texts.Texts)
                    {
                        RenderText(context, child, span, inLink);
                    }
                    break;
                case RichTextBold bold:
                    RenderText(context, bold.Text, AddStyled(span, x => x.FontWeight = FontWeights.SemiBold), inLink);
                    break;
                case RichTextItalic italic:
                    RenderText(context, italic.Text, AddStyled(span, x => x.FontStyle = FontStyle.Italic), inLink);
                    break;
                case RichTextUnderline underline:
                    RenderText(context, underline.Text, AddStyled(span, x => x.TextDecorations = TextDecorations.Underline), inLink);
                    break;
                case RichTextStrikethrough strikethrough:
                    RenderText(context, strikethrough.Text, AddStyled(span, x => x.TextDecorations = TextDecorations.Strikethrough), inLink);
                    break;
                case RichTextFixed fixedText:
                    RenderText(context, fixedText.Text, AddStyled(span, x => x.FontFamily = new FontFamily("Consolas")), inLink);
                    break;
                case RichTextSubscript subscript:
                    RenderText(context, subscript.Text, AddStyled(span, x => Typography.SetVariants(x, FontVariants.Subscript)), inLink);
                    break;
                case RichTextSuperscript superscript:
                    RenderText(context, superscript.Text, AddStyled(span, x => Typography.SetVariants(x, FontVariants.Superscript)), inLink);
                    break;
                case RichTextMarked marked:
                    RenderText(context, marked.Text, span, inLink);
                    break;
                case RichTextUrl url:
                    var target = url.Url;
                    AddLink(context, span, url.Text, inLink, () => message.Delegate?.OpenUrl(target, true));
                    break;
                case RichTextEmailAddress email:
                    var address = email.EmailAddress;
                    AddLink(context, span, email.Text, inLink, () => message.Delegate?.OpenUrl("mailto:" + address, false));
                    break;
                case RichTextPhoneNumber phoneNumber:
                    RenderText(context, phoneNumber.Text, span, inLink);
                    break;
                case RichTextMention mention:
                    var username = mention.Username;
                    AddLink(context, span, mention.Text, inLink, () => message.Delegate?.OpenUsername(username));
                    break;
                case RichTextMentionName mentionName:
                    var userId = mentionName.UserId;
                    AddLink(context, span, mentionName.Text, inLink, () => message.Delegate?.OpenUser(userId));
                    break;
                case RichTextHashtag hashtag:
                    var tag = hashtag.Hashtag;
                    AddLink(context, span, hashtag.Text, inLink, () => message.Delegate?.OpenHashtag(tag));
                    break;
                case RichTextCashtag cashtag:
                    var cash = cashtag.Cashtag;
                    AddLink(context, span, cashtag.Text, inLink, () => message.Delegate?.OpenHashtag(cash));
                    break;
                case RichTextBotCommand botCommand:
                    var command = botCommand.BotCommand;
                    AddLink(context, span, botCommand.Text, inLink, () => message.Delegate?.SendBotCommand(command));
                    break;
                case RichTextBankCardNumber bankCardNumber:
                    var number = bankCardNumber.BankCardNumber;
                    AddLink(context, span, bankCardNumber.Text, inLink, () => message.Delegate?.OpenBankCardNumber(number));
                    break;
                case RichTextAnchorLink anchorLink:
                    RenderText(context, anchorLink.Text, span, inLink);
                    break;
                case RichTextAnchor _:
                case RichTextIcon _:
                    break;
                default:
                    if (TryUnwrap(text, out RichText inner, out string replacement))
                    {
                        if (inner != null)
                        {
                            RenderText(context, inner, span, inLink);
                        }
                        else if (!string.IsNullOrEmpty(replacement))
                        {
                            span.Inlines.Add(new Run { Text = replacement });
                        }
                    }
                    break;
            }
        }
#endif
    }
}
