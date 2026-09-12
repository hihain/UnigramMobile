using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Telegram.Td;
using Telegram.Td.Api;
using Unigram.Common;
using Unigram.Controls.Chats;
using Unigram.Converters;
using Unigram.Views.Popups;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Foundation.Metadata;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Text;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace Unigram.Controls
{
    public class FormattedTextBox : RichEditBox
    {
        private MenuFlyoutSubItem _proofingMenu;

        public FormattedTextBox()
        {
            DefaultStyleKey = typeof(FormattedTextBox);

            ClipboardCopyFormat = RichEditClipboardFormat.PlainText;

            _proofingMenu = new MenuFlyoutSubItem();
            _proofingMenu.Text = "Spelling";

            ContextFlyout = new MenuFlyout();
            ContextFlyout.Opening += OnContextFlyoutOpening;
            ContextFlyout.Closing += OnContextFlyoutClosing;

            if (ApiInfo.CanUseAccelerators)
            {
                DisabledFormattingAccelerators = DisabledFormattingAccelerators.All;
            }

            if (ApiInfo.CanUseKeyboardAcceleratorPlacementMode)
            {
                KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;

                CreateKeyboardAccelerator(VirtualKey.B);
                CreateKeyboardAccelerator(VirtualKey.I);
                CreateKeyboardAccelerator(VirtualKey.U);
                CreateKeyboardAccelerator(VirtualKey.X, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift);
                CreateKeyboardAccelerator(VirtualKey.M, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift);
                CreateKeyboardAccelerator(VirtualKey.P, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift);
                CreateKeyboardAccelerator(VirtualKey.K);
                CreateKeyboardAccelerator(VirtualKey.N, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift);
            }

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Window.Current.CoreWindow.CharacterReceived += OnCharacterReceived;
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            Window.Current.CoreWindow.CharacterReceived -= OnCharacterReceived;
        }

        public bool IsReplaceEmojiEnabled { get; set; } = true;

        private void OnCharacterReceived(CoreWindow sender, CharacterReceivedEventArgs args)
        {
            if (FocusState == FocusState.Unfocused || !IsReplaceEmojiEnabled || string.Equals(Document.Selection.CharacterFormat.Name, "Consolas", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var character = Encoding.UTF32.GetString(BitConverter.GetBytes(args.KeyCode));

            //var matches = Emoticon.Data.Keys.Where(x => x.EndsWith(character)).ToArray();
            if (Emoticon.Matches.TryGetValue(character[0], out string[] matches))
            {
                var length = matches.Max(x => x.Length);
                var start = Math.Max(Document.Selection.EndPosition - length, 0);

                var range = Document.GetRange(start, Document.Selection.EndPosition);
                range.GetText(TextGetOptions.NoHidden, out string value);

                var emoticon = matches.FirstOrDefault(x => value.EndsWith(x));
                if (emoticon != null)
                {
                    Document.BeginUndoGroup();
                    range.SetRange(range.EndPosition - emoticon.Length, range.EndPosition);
                    range.SetText(TextSetOptions.None, Emoticon.Data[emoticon]);
                    range.SetRange(range.EndPosition, range.EndPosition);
                    range.SetText(TextSetOptions.None, emoticon);
                    range.CharacterFormat.Hidden = FormatEffect.On;
                    Document.EndUndoGroup();

                    Document.Selection.SetRange(range.EndPosition, range.EndPosition);
                    args.Handled = true;
                }
            }
        }

        protected override void OnKeyDown(KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Back && IsReplaceEmojiEnabled)
            {
                Document.GetText(TextGetOptions.None, out string text);

                var range = Document.Selection.GetClone();
                if (range.Expand(TextRangeUnit.Hidden) != 0 && Emoticon.Data.TryGetValue(range.Text, out string emoji))
                {
                    var emoticon = range.Text;

                    Document.BeginUndoGroup();
                    range.SetRange(range.StartPosition - emoji.Length, range.EndPosition);
                    range.SetText(TextSetOptions.Unhide, emoticon);
                    Document.EndUndoGroup();

                    Document.Selection.SetRange(range.EndPosition, range.EndPosition);
                    return;
                }
            }

            base.OnKeyDown(e);
        }

        public event TypedEventHandler<FormattedTextBox, EventArgs> ShowFormatting;
        public event TypedEventHandler<FormattedTextBox, EventArgs> HideFormatting;

        private bool _isFormattingVisible = false;
        public bool IsFormattingVisible
        {
            get => _isFormattingVisible;
            set
            {
                if (_isFormattingVisible != value)
                {
                    _isFormattingVisible = value;

                    if (value)
                    {
                        ShowFormatting?.Invoke(this, EventArgs.Empty);
                    }
                    else
                    {
                        HideFormatting?.Invoke(this, EventArgs.Empty);
                    }
                }
            }
        }

        /// <summary>
        /// The colour a quoted stretch is painted in the composer, and the mark by which it
        /// is recognised again on the way out.
        ///
        /// A RichEditBox only knows character formats, and a quote is not one. The box
        /// already solves that twice over: monospace rides on the font name, a spoiler on
        /// the background colour. This is the same trick on the one property those two leave
        /// alone, which is why a quote can still contain bold, a link or a spoiler.
        /// </summary>
        public static readonly Color QuoteColor = Colors.SteelBlue;

        private ChatTextFormatting _formatting;
        public ChatTextFormatting Formatting
        {
            get => _formatting;
            set => _formatting = value;
        }

        #region Context menu

        private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            e.Handled = true;
        }

        private void OnContextFlyoutOpening(object sender, object e)
        {
            var flyout = ContextFlyout as MenuFlyout;
            if (flyout == null)
            {
                return;
            }

            flyout.Items.Clear();

            var selection = Document.Selection;
            var format = Document.Selection.CharacterFormat;

            var length = Math.Abs(selection.Length) > 0;

            //var clipboard = Clipboard.GetContent();

            var link = GetSelectedLink();
            var mention = TryGetUserId(link, out long userId);

            var formatting = new MenuFlyoutSubItem { Text = Strings.Additional.Formatting, Icon = new FontIcon { Glyph = "\uE8D3" } }; // Icons.Fontcolor
            CreateFlyoutItem(formatting.Items, length && format.Bold == FormatEffect.Off, ContextBold_Click, Strings.Resources.Bold, new FontIcon { Glyph = Icons.Bold }, VirtualKey.B);
            CreateFlyoutItem(formatting.Items, length && format.Italic == FormatEffect.Off, ContextItalic_Click, Strings.Resources.Italic, new FontIcon { Glyph = Icons.Italic }, VirtualKey.I);
            CreateFlyoutItem(formatting.Items, length && format.Underline == UnderlineType.None, ContextUnderline_Click, Strings.Resources.Underline, new FontIcon { Glyph = Icons.Underline }, VirtualKey.U);
            CreateFlyoutItem(formatting.Items, length && format.Strikethrough == FormatEffect.Off, ContextStrikethrough_Click, Strings.Resources.Strike, new FontIcon { Glyph = Icons.Strikethrough, FontFamily = App.Current.Resources["TelegramThemeFontFamily"] as FontFamily }, VirtualKey.X, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift);
            CreateFlyoutItem(formatting.Items, length && format.Name != "Consolas", ContextMonospace_Click, Strings.Resources.Mono, new FontIcon { Glyph = Icons.Monospace }, VirtualKey.M, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift);
            CreateFlyoutItem(formatting.Items, length && format.BackgroundColor != Colors.Gray, ContextSpoiler_Click, Strings.Resources.Spoiler, new FontIcon { Glyph = Icons.Spoiler, FontFamily = App.Current.Resources["TelegramIconsFontFamily"] as FontFamily }, VirtualKey.P, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift);
            CreateFlyoutItem(formatting.Items, length && format.ForegroundColor != QuoteColor, ContextQuote_Click, Strings.Additional.QuoteBlock, new FontIcon { Glyph = Icons.QuoteBlock, FontFamily = App.Current.Resources["TelegramIconsFontFamily"] as FontFamily }, VirtualKey.Q, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift);
            formatting.Items.Add(new MenuFlyoutSeparator());
            CreateFlyoutItem(formatting.Items, !mention, ContextLink_Click, link.Link.Length > 0 ? Strings.Additional.EditLink : Strings.Resources.CreateLink, new FontIcon { Glyph = Icons.Link }, VirtualKey.K);
            formatting.Items.Add(new MenuFlyoutSeparator());
            CreateFlyoutItem(formatting.Items, length && !IsDefault(format), ContextPlain_Click, Strings.Resources.Regular, null, VirtualKey.N, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift);
            formatting.Items.Add(new MenuFlyoutSeparator());
            CreateFlyoutItem(formatting.Items, true, () => IsFormattingVisible = !_isFormattingVisible, _isFormattingVisible ? Strings.Additional.FormattingHide : Strings.Additional.FormattingShow, new FontIcon { Glyph = "\uE8D2" });

            CreateFlyoutItem(flyout.Items, Document.CanUndo(), ContextUndo_Click, Strings.Additional.Undo, new FontIcon { Glyph = Icons.Undo }, VirtualKey.Z);
            CreateFlyoutItem(flyout.Items, Document.CanRedo(), ContextRedo_Click, Strings.Additional.Redo, new FontIcon { Glyph = Icons.Redo }, VirtualKey.Y);
            flyout.Items.Add(new MenuFlyoutSeparator());
            CreateFlyoutItem(flyout.Items, length && Document.CanCopy(), ContextCut_Click, Strings.Additional.Cut, new FontIcon { Glyph = Icons.Cut }, VirtualKey.X);
            CreateFlyoutItem(flyout.Items, length && Document.CanCopy(), ContextCopy_Click, Strings.Resources.Copy, new FontIcon { Glyph = Icons.Copy }, VirtualKey.C);
            CreateFlyoutItem(flyout.Items, Document.CanPaste(), ContextPaste_Click, Strings.Additional.Paste, new FontIcon { Glyph = Icons.Paste }, VirtualKey.V);
            CreateFlyoutItem(flyout.Items, length, ContextDelete_Click, Strings.Resources.Delete, new FontIcon { Glyph = Icons.Delete });
            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(formatting);
            flyout.Items.Add(new MenuFlyoutSeparator());
            CreateFlyoutItem(flyout.Items, !IsEmpty, ContextSelectAll_Click, Strings.Additional.SelectAll, new FontIcon { Glyph = "\uE8B3" }, VirtualKey.A); //Icons.SelectAll

            if (ApiInformation.IsPropertyPresent("Windows.UI.Xaml.Controls.RichEditBox", "ProofingMenuFlyout") && ProofingMenuFlyout is MenuFlyout proofing && proofing.Items.Count > 0)
            {
                flyout.CreateFlyoutSeparator();
                //flyout.Items.Add(_proofingMenu);

                foreach (var item in proofing.Items)
                {
                    flyout.Items.Add(item);
                }
            }
        }

        private void OnContextFlyoutClosing(FlyoutBase sender, FlyoutBaseClosingEventArgs args)
        {
            _proofingMenu.Items.Clear();

            if (sender is MenuFlyout flyout)
            {
                flyout.Items.Clear();
            }
        }

        public void ToggleBold()
        {
            ContextBold_Click();
        }

        private void ContextBold_Click()
        {
            //if (Math.Abs(Document.Selection.Length) < 1)
            //{
            //    return;
            //}

            Document.BatchDisplayUpdates();
            ClearStyle(Document.Selection, false);
            Document.Selection.CharacterFormat.Bold = FormatEffect.Toggle;
            Document.ApplyDisplayUpdates();

            _formatting?.Update(Document.Selection.CharacterFormat);
        }

        public void ToggleItalic()
        {
            ContextItalic_Click();
        }

        private void ContextItalic_Click()
        {
            //if (Math.Abs(Document.Selection.Length) < 1)
            //{
            //    return;
            //}

            Document.BatchDisplayUpdates();
            ClearStyle(Document.Selection, false);
            Document.Selection.CharacterFormat.Italic = FormatEffect.Toggle;
            Document.ApplyDisplayUpdates();

            _formatting?.Update(Document.Selection.CharacterFormat);
        }

        public void ToggleUnderline()
        {
            ContextUnderline_Click();
        }

        private void ContextUnderline_Click()
        {
            //if (Math.Abs(Document.Selection.Length) < 1)
            //{
            //    return;
            //}

            Document.BatchDisplayUpdates();
            ClearStyle(Document.Selection, false);
            Document.Selection.CharacterFormat.Underline = Document.Selection.CharacterFormat.Underline != UnderlineType.Single ? UnderlineType.Single : UnderlineType.None;
            Document.ApplyDisplayUpdates();

            _formatting?.Update(Document.Selection.CharacterFormat);
        }

        public void ToggleStrikethrough()
        {
            ContextStrikethrough_Click();
        }

        private void ContextStrikethrough_Click()
        {
            //if (Math.Abs(Document.Selection.Length) < 1)
            //{
            //    return;
            //}

            Document.BatchDisplayUpdates();
            ClearStyle(Document.Selection, false);
            Document.Selection.CharacterFormat.Strikethrough = FormatEffect.Toggle;
            Document.ApplyDisplayUpdates();

            _formatting?.Update(Document.Selection.CharacterFormat);
        }

        public void ToggleMonospace()
        {
            ContextMonospace_Click();
        }

        private void ContextMonospace_Click()
        {
            //if (Math.Abs(Document.Selection.Length) < 1)
            //{
            //    return;
            //}

            Document.BatchDisplayUpdates();
            ClearStyle(Document.Selection, true);
            Document.Selection.CharacterFormat.Name = "Consolas";
            Document.ApplyDisplayUpdates();

            _formatting?.Update(Document.Selection.CharacterFormat);
        }

        public void ToggleSpoiler()
        {
            ContextSpoiler_Click();
        }

        private void ContextSpoiler_Click()
        {
            //if (Math.Abs(Document.Selection.Length) < 1)
            //{
            //    return;
            //}

            Document.BatchDisplayUpdates();
            ClearStyle(Document.Selection, true);
            Document.Selection.CharacterFormat.BackgroundColor = Colors.Gray;
            Document.ApplyDisplayUpdates();

            _formatting?.Update(Document.Selection.CharacterFormat);
        }

        public void ToggleQuote()
        {
            ContextQuote_Click();
        }

        private void ContextQuote_Click()
        {
            var format = Document.Selection.CharacterFormat;

            // Unlike the spoiler, this does not clear what is already there. A quote is the
            // one format people routinely put other formatting inside.
            Document.BatchDisplayUpdates();
            format.ForegroundColor = format.ForegroundColor == QuoteColor
                ? Document.GetDefaultCharacterFormat().ForegroundColor
                : QuoteColor;
            Document.ApplyDisplayUpdates();

            _formatting?.Update(Document.Selection.CharacterFormat);
        }

        public void CreateLink()
        {
            ContextLink_Click();
        }

        private async void ContextLink_Click()
        {
            var range = Document.Selection.GetClone();

            if (GetSelectedLink().Link.Length > 0)
            {
                range.Expand(TextRangeUnit.Link);
            }

            range.GetText(TextGetOptions.NoHidden, out string text);

            var start = Math.Min(range.StartPosition, range.EndPosition);
            var end = Math.Max(range.StartPosition, range.EndPosition);

            var dialog = new CreateLinkPopup();
            dialog.Text = text;
            dialog.Link = range.Link.Trim('"');

            var confirm = await dialog.ShowQueuedAsync();
            if (confirm != ContentDialogResult.Primary)
            {
                return;
            }

            Document.BatchDisplayUpdates();
            range.SetRange(start, end);

            // Everything over the link is reset to plain below, deliberately, so that a link
            // looks like a link. That threw the quote marker away with it, cutting a quote
            // into three around any link put inside one. Noted here and restored after the
            // link is assigned, because assigning it is the last thing to touch the format.
            var quoted = range.CharacterFormat.ForegroundColor == QuoteColor;

            range.CharacterFormat = Document.GetDefaultCharacterFormat();

            range.SetText(end > start ? TextSetOptions.Unlink : TextSetOptions.None, dialog.Text);
            range.SetRange(start, start + dialog.Text.Length);
            range.Link = $"\"{dialog.Link}\"";

            if (quoted)
            {
                range.CharacterFormat.ForegroundColor = QuoteColor;
            }

            Document.Selection.SetRange(range.EndPosition, range.EndPosition);
            Document.ApplyDisplayUpdates();
        }

        private void ContextPlain_Click()
        {
            if (Math.Abs(Document.Selection.Length) < 1)
            {
                return;
            }

            Document.BatchDisplayUpdates();
            ClearStyle(Document.Selection, true);
            Document.ApplyDisplayUpdates();
        }

        private void ClearStyle(ITextRange range, bool monospace)
        {
            if (monospace)
            {
                var start = Math.Min(range.StartPosition, range.EndPosition);
                var end = Math.Max(range.StartPosition, range.EndPosition);

                range.SetRange(start, end);
                range.CharacterFormat = Document.GetDefaultCharacterFormat();

                range.GetText(TextGetOptions.NoHidden, out string text);
                range.SetText(TextSetOptions.Unlink, text);
                range.SetRange(start, start + text.Length);
            }
            else
            {
                range.CharacterFormat.Name = Document.GetDefaultCharacterFormat().Name;
            }
        }

        protected bool IsDefault(ITextCharacterFormat format)
        {
            return IsEqual(format, Document.GetDefaultCharacterFormat());
        }

        protected bool IsEqual(ITextCharacterFormat format, ITextCharacterFormat document)
        {
            return document.AllCaps == format.AllCaps &&
                document.BackgroundColor == format.BackgroundColor &&
                document.Bold == format.Bold &&
                document.FontStretch == format.FontStretch &&
                document.FontStyle == format.FontStyle &&
                document.ForegroundColor == format.ForegroundColor &&
                document.Hidden == format.Hidden &&
                document.Italic == format.Italic &&
                document.Kerning == format.Kerning &&
                //document.LanguageTag == format.LanguageTag &&
                document.LinkType == format.LinkType &&
                document.Name == format.Name &&
                document.Outline == format.Outline &&
                document.Position == format.Position &&
                document.ProtectedText == format.ProtectedText &&
                document.Size == format.Size &&
                document.SmallCaps == format.SmallCaps &&
                document.Spacing == format.Spacing &&
                document.Strikethrough == format.Strikethrough &&
                document.Subscript == format.Subscript &&
                document.Superscript == format.Superscript &&
                //document.TextScript == format.TextScript &&
                document.Underline == format.Underline &&
                document.Weight == format.Weight;
        }

        /// <summary>
        /// The one character whose link decides whether the link dialog edits a link that is
        /// already there or makes a new one: the last selected one, or the one the caret sits
        /// on when nothing is selected.
        ///
        /// It used to ask the selection to walk to the start of its own link. Where two links
        /// touch there is one position that is both the end of the first and the start of the
        /// second, and the walk answers with the first. Selecting the second word and asking
        /// for a link then opened the dialog on the first word's link, with both words as its
        /// text -- and confirming replaced the pair with a single link. The last selected
        /// character is inside the selection and so belongs to one link only.
        /// </summary>
        private ITextRange GetSelectedLink()
        {
            var start = Math.Min(Document.Selection.StartPosition, Document.Selection.EndPosition);
            var end = Math.Max(Document.Selection.StartPosition, Document.Selection.EndPosition);

            var from = end > start ? end - 1 : start;
            return Document.GetRange(from, from + 1);
        }

        protected bool TryGetUserId(ITextRange range, out long userId)
        {
            var link = range.Link.Trim('"');
            if (link.StartsWith("tg-user://") && long.TryParse(link.Substring("tg-user://".Length), out userId))
            {
                return true;
            }

            userId = 0;
            return false;
        }

        protected bool TryGetEntityType(string link, out TextEntityType type)
        {
            link = link.Trim('"');

            if (IsUrlValid(link))
            {
                type = new TextEntityTypeTextUrl(link);
                return true;
            }
            else if (link.StartsWith("tg-user://") && long.TryParse(link.Substring("tg-user://".Length), out long userId))
            {
                type = new TextEntityTypeMentionName(userId);
                return true;
            }

            type = null;
            return false;
        }

        private bool IsUrlValid(string url)
        {
            var response = Client.Execute(new GetTextEntities(url));
            if (response is TextEntities entities)
            {
                return entities.Entities.Count == 1 && entities.Entities[0].Offset == 0 && entities.Entities[0].Length == url.Length && entities.Entities[0].Type is TextEntityTypeUrl;
            }

            return false;
        }

        private void ContextUndo_Click()
        {
            Document.Undo();
        }

        private void ContextRedo_Click()
        {
            Document.Redo();
        }

        private void ContextCut_Click()
        {
            Document.Selection.Cut();
        }

        private void ContextCopy_Click()
        {
            Document.Selection.Copy();
        }

        private void ContextPaste_Click()
        {
            OnPaste();
        }

        protected virtual void OnPaste()
        {
            Document.Selection.Paste(0);
        }

        private void ContextDelete_Click()
        {
            Document.Selection.SetText(TextSetOptions.None, string.Empty);
        }

        protected void ContextDeleteLast()
        {
            Document.GetText(TextGetOptions.None, out string text);
            var textParts = Emoji.EnumerateByComposedCharacterSequence(text); // Note: one emoji is one part
            Document.Selection.SetRange(text.Length - textParts.Last(t => t != "\r").Length - 1, text.Length - 1);
            ContextDelete_Click();
        }

        private void ContextSelectAll_Click()
        {
            Document.Selection.Expand(TextRangeUnit.Paragraph);
        }

        private void CreateFlyoutItem(IList<MenuFlyoutItemBase> flyout, bool create, Action command, string text, IconElement icon = null, VirtualKey? key = null, VirtualKeyModifiers modifiers = VirtualKeyModifiers.Control)
        {
            var flyoutItem = new MenuFlyoutItem();
            flyoutItem.IsEnabled = create;
            flyoutItem.Command = new RelayCommand(command);
            flyoutItem.Text = text;

            if (icon != null)
            {
                flyoutItem.Icon = icon;
            }

            if (key.HasValue && ApiInfo.CanUseAccelerators)
            {
                flyoutItem.KeyboardAccelerators.Add(new KeyboardAccelerator { Modifiers = modifiers, Key = key.Value, IsEnabled = false });
            }

            flyout.Add(flyoutItem);
        }

        private void CreateKeyboardAccelerator(VirtualKey key, VirtualKeyModifiers modifiers = VirtualKeyModifiers.Control)
        {
            if (ApiInfo.CanUseAccelerators)
            {
                var accelerator = new KeyboardAccelerator { Modifiers = modifiers, Key = key, ScopeOwner = this };
                accelerator.Invoked += FlyoutAccelerator_Invoked;

                KeyboardAccelerators.Add(accelerator);
            }
        }

        private void FlyoutAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
        {
            args.Handled = true;

            var selection = Document.Selection;
            var format = Document.Selection.CharacterFormat;

            var length = Math.Abs(selection.Length) > 0;

            if (sender.Key == VirtualKey.B && sender.Modifiers == VirtualKeyModifiers.Control)
            {
                ContextBold_Click();
            }
            else if (sender.Key == VirtualKey.I && sender.Modifiers == VirtualKeyModifiers.Control && length && format.Italic == FormatEffect.Off)
            {
                ContextItalic_Click();
            }
            else if (sender.Key == VirtualKey.U && sender.Modifiers == VirtualKeyModifiers.Control && length && format.Underline == UnderlineType.None)
            {
                ContextUnderline_Click();
            }
            else if (sender.Key == VirtualKey.X && sender.Modifiers == (VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift) && length && format.Strikethrough == FormatEffect.Off)
            {
                ContextStrikethrough_Click();
            }
            else if (sender.Key == VirtualKey.M && sender.Modifiers == (VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift) && length && format.Name != "Consolas")
            {
                ContextMonospace_Click();
            }
            else if (sender.Key == VirtualKey.P && sender.Modifiers == (VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift) && length && format.BackgroundColor != Colors.Gray)
            {
                ContextSpoiler_Click();
            }
            else if (sender.Key == VirtualKey.K && sender.Modifiers == VirtualKeyModifiers.Control)
            {
                ContextLink_Click();
            }
            else if (sender.Key == VirtualKey.N && sender.Modifiers == (VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift) && length && !IsDefault(format))
            {
                ContextPlain_Click();
            }
        }

        #endregion

        public FormattedText GetFormattedText(bool clear = false)
        {
            OnGettingFormattedText();

            if (IsEmpty)
            {
                return new FormattedText(string.Empty, new TextEntity[0]);
            }

            Document.BatchDisplayUpdates();
            Document.GetText(TextGetOptions.None, out string value);

            value = value.TrimEnd('\v', '\r');

            var builder = new StringBuilder(value);
            var runs = new List<TextStyleRun>();

            var last = default(TextStyleRun);

            var hidden = 0;

            for (int i = 0; i < value.Length; i++)
            {
                var range = Document.GetRange(i, i + 1);
                var flags = default(TextStyle);

                // Belongs to this character and no other. It used to live outside the loop
                // and was cleared only when a run was created, so a link left its type
                // standing: the run that started where the link ended picked it up, and the
                // link grew to cover everything after it that shared the surrounding
                // formatting. Invisible until a quote, or bold, carried on past the link.
                var type = default(TextEntityType);

                if (string.Equals(range.CharacterFormat.Name, "Consolas", StringComparison.OrdinalIgnoreCase))
                {
                    flags = TextStyle.Monospace;
                }
                else
                {
                    if (range.CharacterFormat.Hidden == FormatEffect.On)
                    {
                        builder.Remove(i - hidden, 1);

                        hidden++;
                        continue;
                    }

                    if (range.CharacterFormat.Bold == FormatEffect.On)
                    {
                        flags |= TextStyle.Bold;
                    }
                    if (range.CharacterFormat.Italic == FormatEffect.On)
                    {
                        flags |= TextStyle.Italic;
                    }
                    if (range.CharacterFormat.Strikethrough == FormatEffect.On)
                    {
                        flags |= TextStyle.Strikethrough;
                    }
                    if (range.CharacterFormat.Underline == UnderlineType.Single)
                    {
                        flags |= TextStyle.Underline;
                    }
                    if (range.CharacterFormat.BackgroundColor == Colors.Gray)
                    {
                        flags |= TextStyle.Spoiler;
                    }
                    if (range.CharacterFormat.ForegroundColor == QuoteColor)
                    {
                        flags |= TextStyle.Quote;
                    }

                    if (range.Link.Length > 0 && TryGetEntityType(range.Link, out TextEntityType linkType))
                    {
                        flags |= TextStyle.Url;
                        flags &= ~TextStyle.Underline;

                        type = linkType;
                    }
                }

                // The type has to match too, not just the flags. Two links that touch carry
                // the same one flag and nothing between them but the hidden characters that
                // mark where each begins, so on flags alone they read as one run -- and the
                // run keeps the first link's address, which quietly points the second link
                // at the first one's target.
                if (last != null && last.Flags == flags && TextStyleRun.AreEqual(last.Type, type))
                {
                    last.End++;
                }
                else
                {
                    if (last != null)
                    {
                        runs.Add(last);
                        last = null;
                    }

                    if (flags != 0)
                    {
                        last = new TextStyleRun { Start = i - hidden, End = i - hidden + 1, Flags = flags, Type = type };
                    }
                }
            }

            if (last != null)
            {
                runs.Add(last);
            }

            Document.GetText(TextGetOptions.NoHidden, out string text);

            if (clear)
            {
                Document.LoadFromStream(TextSetOptions.None, new InMemoryRandomAccessStream());
            }

            Document.ApplyDisplayUpdates();

            var entities = TextStyleRun.GetEntities(text, runs);

            text = text.Replace('\v', '\n').Replace('\r', '\n');
            return Client.Execute(new ParseMarkdown(new FormattedText(text, entities))) as FormattedText;
        }

        protected virtual void OnGettingFormattedText()
        {

        }

        protected virtual void OnSettingText()
        {

        }

        protected bool _wasEmpty;
        public virtual bool IsEmpty
        {
            get
            {
                var end = Document.GetRange(int.MaxValue, int.MaxValue);
                var empty = end.EndPosition == 0;

                if (empty && !_wasEmpty)
                {
                    Document.Selection.CharacterFormat = Document.GetDefaultCharacterFormat();
                }

                _wasEmpty = empty;
                return empty;
            }
        }

        public void SetText(FormattedText formattedText)
        {
            if (formattedText != null)
            {
                SetText(formattedText.Text, formattedText.Entities);
            }
            else
            {
                OnSettingText();
                Document.LoadFromStream(TextSetOptions.None, new InMemoryRandomAccessStream());
            }
        }

        public void SetText(string text, IList<TextEntity> entities)
        {
            OnSettingText();

            Document.BeginUndoGroup();
            Document.BatchDisplayUpdates();
            Document.LoadFromStream(TextSetOptions.None, new InMemoryRandomAccessStream());

            if (!string.IsNullOrEmpty(text))
            {
                Document.SetText(TextSetOptions.None, text);

                if (entities != null && entities.Count > 0)
                {
                    // A quote is the one entity that legitimately contains others, so it
                    // cannot wait its turn below. Formatting a link inserts hidden characters
                    // into the document, and from there on an offset no longer means what
                    // TDLib meant by it: a quote applied afterwards counts those hidden
                    // characters towards its own length and stops short of its end. Applied
                    // here, while the document still matches the text that was sent.
                    foreach (var entity in entities)
                    {
                        if (entity.Type is TextEntityTypeBlockQuote)
                        {
                            Document.GetRange(entity.Offset, entity.Offset + entity.Length)
                                .CharacterFormat.ForegroundColor = QuoteColor;
                        }
                    }

                    // We want to enumerate entities from last to first to not
                    // fuck up ranges due to hidden texts when formatting a link
                    foreach (var entity in entities.Reverse())
                    {
                        var range = Document.GetRange(entity.Offset, entity.Offset + entity.Length);

                        if (entity.Type is TextEntityTypeBold)
                        {
                            range.CharacterFormat.Bold = FormatEffect.On;
                        }
                        else if (entity.Type is TextEntityTypeItalic)
                        {
                            range.CharacterFormat.Italic = FormatEffect.On;
                        }
                        else if (entity.Type is TextEntityTypeUnderline)
                        {
                            range.CharacterFormat.Underline = UnderlineType.Single;
                        }
                        else if (entity.Type is TextEntityTypeStrikethrough)
                        {
                            range.CharacterFormat.Strikethrough = FormatEffect.On;
                        }
                        else if (entity.Type is TextEntityTypeSpoiler)
                        {
                            range.CharacterFormat.BackgroundColor = Colors.Gray;
                        }
                        else if (entity.Type is TextEntityTypeCode || entity.Type is TextEntityTypePre || entity.Type is TextEntityTypePreCode)
                        {
                            range.CharacterFormat.Name = "Consolas";
                        }
                        else if (entity.Type is TextEntityTypeTextUrl textUrl)
                        {
                            try
                            {
                                range.Link = $"\"{textUrl.Url}\"";
                            }
                            catch (Exception)
                            {
                                range.Text = "."; //TODO: Hacky.
                                range.Link = $"\"{textUrl.Url}\"";
                            }
                        }
                        else if (entity.Type is TextEntityTypeMentionName mentionName)
                        {
                            range.Link = $"\"tg-user://{mentionName.UserId}\"";
                        }
                    }
                }

                // We need to get full text as hidden content has been added and we don't want to hardcode lengths
                Document.GetText(TextGetOptions.None, out string result);
                Document.Selection.SetRange(result.Length, result.Length);
            }

            Document.ApplyDisplayUpdates();
            Document.EndUndoGroup();
        }

        public void InsertText(string text, IList<TextEntity> entities)
        {
            Document.BeginUndoGroup();
            Document.BatchDisplayUpdates();

            if (!string.IsNullOrEmpty(text))
            {
                var index = Math.Min(Document.Selection.StartPosition, Document.Selection.EndPosition);

                Document.Selection.SetText(TextSetOptions.None, text);

                if (entities != null && entities.Count > 0)
                {
                    // A quote goes on before everything else, for the reason SetText gives:
                    // it is the one entity that contains others, and a link applied inside it
                    // first would have moved the end of it out of reach.
                    foreach (var entity in entities)
                    {
                        if (entity.Type is TextEntityTypeBlockQuote)
                        {
                            Document.GetRange(index + entity.Offset, index + entity.Offset + entity.Length)
                                .CharacterFormat.ForegroundColor = QuoteColor;
                        }
                    }

                    // We want to enumerate entities from last to first to not
                    // fuck up ranges due to hidden texts when formatting a link
                    foreach (var entity in entities.Reverse())
                    {
                        var range = Document.GetRange(index + entity.Offset, index + entity.Offset + entity.Length);

                        if (entity.Type is TextEntityTypeBold)
                        {
                            range.CharacterFormat.Bold = FormatEffect.On;
                        }
                        else if (entity.Type is TextEntityTypeItalic)
                        {
                            range.CharacterFormat.Italic = FormatEffect.On;
                        }
                        else if (entity.Type is TextEntityTypeUnderline)
                        {
                            range.CharacterFormat.Underline = UnderlineType.Single;
                        }
                        else if (entity.Type is TextEntityTypeStrikethrough)
                        {
                            range.CharacterFormat.Strikethrough = FormatEffect.On;
                        }
                        else if (entity.Type is TextEntityTypeSpoiler)
                        {
                            range.CharacterFormat.BackgroundColor = Colors.Gray;
                        }
                        else if (entity.Type is TextEntityTypeCode || entity.Type is TextEntityTypePre || entity.Type is TextEntityTypePreCode)
                        {
                            range.CharacterFormat.Name = "Consolas";
                        }
                        else if (entity.Type is TextEntityTypeTextUrl textUrl)
                        {
                            try
                            {
                                range.Link = $"\"{textUrl.Url}\"";
                            }
                            catch
                            {
                                // Do nothing, just ignore the link
                            }
                        }
                        else if (entity.Type is TextEntityTypeMentionName mentionName)
                        {
                            range.Link = $"\"tg-user://{mentionName.UserId}\"";
                        }
                    }
                }

                Document.Selection.SetRange(Document.Selection.EndPosition, Document.Selection.EndPosition);
            }

            Document.ApplyDisplayUpdates();
            Document.EndUndoGroup();
        }

        public void InsertText(string text, bool allowPreceding = false, bool allowTrailing = false)
        {
            var start = Document.Selection.StartPosition;
            var end = Document.Selection.EndPosition;

            var preceding = start > 0 && !char.IsWhiteSpace(Document.GetRange(start - 1, start).Character);
            var trailing = !char.IsWhiteSpace(Document.GetRange(end, end + 1).Character) || Document.GetRange(end, end + 1).Character == '\r';

            var block = string.Format("{0}{1}{2}",
                preceding && allowPreceding ? " " : "",
                text,
                trailing && allowTrailing ? " " : "");

            Document.Selection.SetText(TextSetOptions.None, block);
            Document.Selection.StartPosition = Document.Selection.EndPosition;
        }
    }
}
