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
        private TextBlock Presenter;
        private TextBlock Count;

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
        }

        protected override void OnApplyTemplate()
        {
            Presenter = GetTemplateChild(nameof(Presenter)) as TextBlock;
            Count = GetTemplateChild(nameof(Count)) as TextBlock;

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
