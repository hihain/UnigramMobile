using Telegram.Td.Api;
using Unigram.Common;
using Unigram.Converters;
using Unigram.ViewModels;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;

namespace Unigram.Controls.Messages
{
    public class ReactionButton : ToggleButton
    {
        // Big enough to tell people apart at arm's length on a 5.7" screen, small enough
        // that three of them do not make the button wider than the bubble it sits under.
        private const int PhotoSide = 18;

        private TextBlock Presenter;
        private TextBlock Count;
        private StackPanel Recent;

        public ReactionButton()
        {
            DefaultStyleKey = typeof(ReactionButton);
            Click += OnClick;
        }

        private MessageViewModel _message;
        private MessageReaction _interaction;

        public MessageReaction Reaction => _interaction;

        public void SetReaction(MessageViewModel message, MessageReaction interaction)
        {
            _message = message;
            _interaction = interaction;

            if (Presenter == null || interaction == null)
            {
                return;
            }

            IsChecked = interaction.IsChosen;

            Presenter.Text = interaction.Type.ToKey();

            if (Count != null)
            {
                Count.Text = BindConvert.ShortNumber(interaction.TotalCount);
            }

            UpdateRecentSenders(message, interaction);
        }

        /// <summary>
        /// Faces instead of a number while there are few enough people to show. TDLib ships
        /// the senders of the most recent reactions with the message itself and caps that at
        /// three, so this costs no request of its own -- and the cap is also what decides
        /// where the number takes over, rather than a threshold picked here.
        ///
        /// It leaves them empty in channels, and short of the total once a fourth person
        /// reacts, which is why anything other than "we have all of them" falls back to the
        /// count. Three faces standing for eleven people would be a worse answer than 11.
        /// </summary>
        private void UpdateRecentSenders(MessageViewModel message, MessageReaction interaction)
        {
            if (Recent == null || Count == null)
            {
                return;
            }

            var senders = interaction.RecentSenderIds;
            var known = message != null && senders != null && senders.Count > 0 && senders.Count == interaction.TotalCount;

            Count.Visibility = known ? Visibility.Collapsed : Visibility.Visible;
            Recent.Visibility = known ? Visibility.Visible : Visibility.Collapsed;

            if (!known)
            {
                // These buttons are pooled by ReactionsPanel and the bubbles around them are
                // recycled by the list, so a face left behind here turns up on somebody
                // else's message.
                Recent.Children.Clear();
                return;
            }

            while (Recent.Children.Count > senders.Count)
            {
                Recent.Children.RemoveAt(Recent.Children.Count - 1);
            }

            for (int i = 0; i < senders.Count; i++)
            {
                var photo = i < Recent.Children.Count ? Recent.Children[i] as ProfilePicture : null;
                if (photo == null)
                {
                    photo = new ProfilePicture
                    {
                        Width = PhotoSide,
                        Height = PhotoSide,
                        // It is a HyperlinkButton underneath, and it would otherwise eat the
                        // taps meant for the reaction it is sitting in.
                        IsHitTestVisible = false
                    };

                    Recent.Children.Add(photo);
                }

                // Overlapping reads as one group of people rather than a row of separate
                // ones, and it is what buys the room for the third face.
                photo.Margin = new Thickness(i == 0 ? 0 : -6, 0, 0, 0);
                photo.Source = PlaceholderHelper.GetMessageSender(message.ProtoService, senders[i], PhotoSide);
            }
        }

        protected override void OnApplyTemplate()
        {
            Presenter = GetTemplateChild(nameof(Presenter)) as TextBlock;
            Count = GetTemplateChild(nameof(Count)) as TextBlock;
            Recent = GetTemplateChild(nameof(Recent)) as StackPanel;

            SetReaction(_message, _interaction);
            base.OnApplyTemplate();
        }

        protected override void OnToggle()
        {
            // The checked state is driven by the server, not by the button
        }

        private void OnClick(object sender, RoutedEventArgs e)
        {
            var message = _message;
            var chosen = _interaction;

            if (message == null || chosen == null)
            {
                return;
            }

            if (chosen.IsChosen)
            {
                message.ProtoService.Send(new RemoveMessageReaction(message.ChatId, message.Id, chosen.Type));
            }
            else
            {
                message.ProtoService.Send(new AddMessageReaction(message.ChatId, message.Id, chosen.Type, false, true));
            }
        }
    }
}
