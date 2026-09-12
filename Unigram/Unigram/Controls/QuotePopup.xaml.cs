using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unigram.Controls
{
    public sealed partial class QuotePopup : ContentPopup
    {
        private readonly string _text;
        private readonly int _maxLength;

        private int[] _map;

        public int Position { get; private set; }
        public int Length { get; private set; }

        public QuotePopup(string text, int maxLength)
        {
            InitializeComponent();

            _text = text;
            _maxLength = maxLength;

            Title = Strings.Additional.QuoteSelectTitle;
            PrimaryButtonText = Strings.Resources.Reply;
            SecondaryButtonText = Strings.Resources.Cancel;

            Hint.Text = Strings.Additional.QuoteSelectHint;
            Counter.Text = Strings.Additional.QuoteTooLong;

            FocusPrimaryButton = false;

            Opened += OnOpened;
        }

        private void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
        {
            Source.Text = _text;
            _map = BuildMap(_text, Source.Text);

            Source.Focus(FocusState.Programmatic);
            Source.SelectionStart = 0;
            Source.SelectionLength = _maxLength > 0 ? Math.Min(Source.Text.Length, _maxLength) : Source.Text.Length;
        }

        private void Source_SelectionChanged(object sender, RoutedEventArgs e)
        {
            var length = Source.SelectionLength;
            var exceeded = _maxLength > 0 && length > _maxLength;

            Counter.Visibility = exceeded ? Visibility.Visible : Visibility.Collapsed;
            IsPrimaryButtonEnabled = length > 0 && !exceeded;
        }

        private void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            int position;
            int length;

            if (!TryResolve(out position, out length))
            {
                args.Cancel = true;
                return;
            }

            Position = position;
            Length = length;

            SetResult(ContentDialogResult.Primary);
        }

        private void ContentDialog_SecondaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
        }

        private bool TryResolve(out int position, out int length)
        {
            position = 0;
            length = 0;

            var selected = Source.SelectedText;
            if (selected.Length == 0 || _map == null)
            {
                return false;
            }

            var hint = Source.SelectionStart;

            var start = _map[Math.Min(Math.Max(hint, 0), _map.Length - 1)];
            var end = _map[Math.Min(Math.Max(hint + selected.Length, 0), _map.Length - 1)];

            if (end > start && string.CompareOrdinal(Normalize(_text.Substring(start, end - start)), selected) == 0)
            {
                position = start;
                length = end - start;
                return true;
            }

            return TryResolveStripped(selected, start, out position, out length);
        }

        private bool TryResolveStripped(string selected, int hint, out int position, out int length)
        {
            position = 0;
            length = 0;

            var stripped = Strip(_text);
            var needle = Strip(selected);

            if (needle.Value.Length == 0 || needle.Value.Length > stripped.Value.Length)
            {
                return false;
            }

            var found = FindNearest(stripped.Value, needle.Value, hint);
            if (found < 0)
            {
                return false;
            }

            var start = stripped.Map[found];
            var end = stripped.Map[found + needle.Value.Length];

            if (end <= start)
            {
                return false;
            }

            position = start;
            length = end - start;
            return true;
        }

        private static int FindNearest(string text, string value, int hint)
        {
            var best = -1;
            var index = text.IndexOf(value, StringComparison.Ordinal);

            while (index >= 0)
            {
                if (best < 0 || Math.Abs(index - hint) < Math.Abs(best - hint))
                {
                    best = index;
                }

                if (index + 1 > text.Length - value.Length)
                {
                    break;
                }

                index = text.IndexOf(value, index + 1, StringComparison.Ordinal);
            }

            return best;
        }

        private static string Normalize(string text)
        {
            return text.Replace("\r\n", "\r").Replace('\n', '\r');
        }

        private static int[] BuildMap(string original, string normalized)
        {
            var map = new int[normalized.Length + 1];

            var i = 0;
            var j = 0;

            while (j < normalized.Length)
            {
                map[j] = i;

                if (i >= original.Length)
                {
                    j++;
                    continue;
                }

                if (normalized[j] == '\r' && original[i] == '\r' && i + 1 < original.Length && original[i + 1] == '\n')
                {
                    i += 2;
                }
                else
                {
                    i++;
                }

                j++;
            }

            map[normalized.Length] = i > original.Length ? original.Length : i;
            return map;
        }

        private static StrippedText Strip(string text)
        {
            var builder = new System.Text.StringBuilder(text.Length);
            var map = new int[text.Length + 1];
            var count = 0;

            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\r' || text[i] == '\n')
                {
                    continue;
                }

                map[count] = i;
                count++;
                builder.Append(text[i]);
            }

            map[count] = text.Length;

            var result = new StrippedText();
            result.Value = builder.ToString();
            result.Map = map;

            return result;
        }

        private struct StrippedText
        {
            public string Value;
            public int[] Map;
        }
    }
}
