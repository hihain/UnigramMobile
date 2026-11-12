using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unigram.Controls
{
    public class PrefixTextBox : TextBox
    {
        private string _value;

        public PrefixTextBox()
        {
            DefaultStyleKey = typeof(PrefixTextBox);

            // TextBox.BeforeTextChanging needs UniversalApiContract 5 (1709), which Windows 10
            // Mobile lacks: subscribing threw and XAML failed to instantiate this control.
            TextChanging += OnTextChanging;
            TextChanged += OnTextChanged;
        }

        private void OnTextChanging(TextBox sender, TextBoxTextChangingEventArgs args)
        {
            var prefix = Prefix ?? string.Empty;
            var text = Text ?? string.Empty;

            if (text.StartsWith(prefix))
            {
                return;
            }

            var restore = string.IsNullOrWhiteSpace(text) ? prefix : prefix + (_value ?? string.Empty);
            if (text != restore)
            {
                Text = restore;
                SelectionStart = restore.Length;
            }
        }

        private void OnTextChanged(object sender, TextChangedEventArgs e)
        {
            if (Text.Length > Prefix.Length)
            {
                _value = Text.Substring(Prefix.Length);
                Value = _value;
            }
            else
            {
                _value = string.Empty;
                Value = _value;
            }
        }

        #region Prefix

        public string Prefix
        {
            get { return (string)GetValue(PrefixProperty); }
            set { SetValue(PrefixProperty, value); }
        }

        public static readonly DependencyProperty PrefixProperty =
            DependencyProperty.Register("Prefix", typeof(string), typeof(PrefixTextBox), new PropertyMetadata(string.Empty, OnPrefixChanged));

        private static void OnPrefixChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((PrefixTextBox)d).OnPrefixChanged((string)e.NewValue, (string)e.OldValue);
        }

        private void OnPrefixChanged(string newValue, string oldValue)
        {
            Text = (newValue ?? string.Empty) + Value;
        }

        #endregion

        #region Value

        public string Value
        {
            get { return (string)GetValue(ValueProperty); }
            set { SetValue(ValueProperty, value); }
        }

        public static readonly DependencyProperty ValueProperty =
            DependencyProperty.Register("Value", typeof(string), typeof(PrefixTextBox), new PropertyMetadata(string.Empty, OnValueChanged));

        private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((PrefixTextBox)d).OnValueChanged((string)e.NewValue, (string)e.OldValue);
        }

        private void OnValueChanged(string newValue, string oldValue)
        {
            if (newValue == oldValue)
            {
                return;
            }

            if (newValue == _value)
            {
                return;
            }

            _value = Prefix + (newValue ?? string.Empty);
            Text = _value;
        }

        #endregion
    }
}
