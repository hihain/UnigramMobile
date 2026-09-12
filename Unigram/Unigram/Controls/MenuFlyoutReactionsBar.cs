using System;
using System.Collections.Generic;
using Telegram.Td.Api;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unigram.Controls
{
    public class MenuFlyoutReactionsBar : MenuFlyoutSeparator
    {
        private StackPanel Presenter;

        private IList<ReactionType> _reactions;

        // Emoji buttons are at least 40px wide (template MinWidth, minimum
        // touch target) and the bar adds 4px padding on either side. The cap
        // aims at five and a half visible buttons; since the actual button
        // width follows the emoji glyph (~44px on 15254), about five fit and
        // the rest pans horizontally.
        private const double ButtonWidth = 40;
        private const double BarPadding = 2 * 4;
        private const double VisibleButtons = 5.5;

        public MenuFlyoutReactionsBar()
        {
            DefaultStyleKey = typeof(MenuFlyoutReactionsBar);
            MaxWidth = BarPadding + VisibleButtons * ButtonWidth;
        }

        public event EventHandler<ReactionType> ReactionSelected;

        public void SetReactions(IList<ReactionType> reactions)
        {
            _reactions = reactions;
            UpdateReactions();
        }

        protected override void OnApplyTemplate()
        {
            Presenter = GetTemplateChild(nameof(Presenter)) as StackPanel;

            UpdateReactions();
            base.OnApplyTemplate();
        }

        private void UpdateReactions()
        {
            if (Presenter == null)
            {
                return;
            }

            Presenter.Children.Clear();

            if (_reactions == null)
            {
                return;
            }

            foreach (var item in _reactions)
            {
                var emoji = item as ReactionTypeEmoji;
                if (emoji == null)
                {
                    continue;
                }

                var button = new Button();
                button.Content = emoji.Emoji;
                button.Tag = item;
                button.Click += Reaction_Click;

                Presenter.Children.Add(button);
            }
        }

        private void Reaction_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var reaction = button?.Tag as ReactionType;

            if (reaction != null)
            {
                ReactionSelected?.Invoke(this, reaction);
            }
        }
    }
}
