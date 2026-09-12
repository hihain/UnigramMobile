using System;
using System.Collections.Generic;
using System.Linq;
using Telegram.Td.Api;
using Unigram.Common;
using Unigram.ViewModels;
using Windows.Foundation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unigram.Controls.Messages
{
    public class ReactionsPanel : Panel
    {
        private readonly Dictionary<string, ReactionButton> _reactions = new Dictionary<string, ReactionButton>();

        public bool HasReactions => _reactions.Count > 0;

        public void UpdateMessageReactions(MessageViewModel message)
        {
            var interaction = message?.InteractionInfo?.Reactions;
            var reactions = interaction != null && !interaction.AreTags ? interaction.Reactions : null;
            if (reactions != null)
            {
                var added = new HashSet<string>();

                foreach (var item in reactions)
                {
                    if (item.Type is ReactionTypeEmoji == false)
                    {
                        continue;
                    }

                    var key = item.Type.ToKey();
                    if (string.IsNullOrEmpty(key))
                    {
                        continue;
                    }

                    added.Add(key);

                    if (_reactions.TryGetValue(key, out ReactionButton button))
                    {
                        button.SetReaction(message, item);
                    }
                    else
                    {
                        button = new ReactionButton();
                        button.SetReaction(message, item);

                        _reactions[key] = button;
                        Children.Add(button);
                    }
                }

                foreach (var reaction in _reactions.ToArray())
                {
                    if (added.Contains(reaction.Key))
                    {
                        continue;
                    }

                    _reactions.Remove(reaction.Key);
                    Children.Remove(reaction.Value);
                }
            }
            else
            {
                _reactions.Clear();
                Children.Clear();
            }
        }

        private const double Spacing = 4;

        public Thickness Padding { get; set; }

        protected override Size MeasureOverride(Size availableSize)
        {
            var totalMeasure = new Size();
            var parentMeasure = new Size(availableSize.Width, availableSize.Height);
            var lineMeasure = new Size(Padding.Left, 0);

            void Measure(Size currentMeasure)
            {
                if (parentMeasure.Width > currentMeasure.Width + lineMeasure.Width)
                {
                    lineMeasure.Width += currentMeasure.Width + Spacing;
                    lineMeasure.Height = Math.Max(lineMeasure.Height, currentMeasure.Height);
                }
                else
                {
                    // new line should be added
                    // to get the max U to provide it correctly to ui width ex: ---| or -----|
                    totalMeasure.Width = Math.Max(lineMeasure.Width - Spacing, totalMeasure.Width);
                    totalMeasure.Height += lineMeasure.Height + Spacing;

                    // if the next new row still can handle more controls
                    if (parentMeasure.Width > currentMeasure.Width)
                    {
                        // set lineMeasure initial values to the currentMeasure to be calculated later on the new loop
                        lineMeasure = currentMeasure;
                    }

                    // the control will take one row alone
                    else
                    {
                        // validate the new control measures
                        totalMeasure.Width = Math.Max(currentMeasure.Width, totalMeasure.Width);
                        totalMeasure.Height += currentMeasure.Height + Spacing;

                        // add new empty line
                        lineMeasure = new Size(Padding.Left, 0);
                    }
                }
            }

            foreach (var child in Children)
            {
                child.Measure(availableSize);
                Measure(new Size(child.DesiredSize.Width, child.DesiredSize.Height));
            }

            if (Children.Count > 0)
            {
                // update value with the last line
                // if the the last loop is(parentMeasure.U > currentMeasure.U + lineMeasure.U) the total isn't calculated then calculate it
                // if the last loop is (parentMeasure.U > currentMeasure.U) the currentMeasure isn't added to the total so add it here
                // for the last condition it is zeros so adding it will make no difference
                // this way is faster than an if condition in every loop for checking the last item
                totalMeasure.Width = Math.Max(lineMeasure.Width - Spacing, totalMeasure.Width) + Padding.Right;
                totalMeasure.Height += lineMeasure.Height + Padding.Bottom + Padding.Top;
            }

            return new Size(totalMeasure.Width, totalMeasure.Height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var parentMeasure = new Size(finalSize.Width, finalSize.Height);
            var position = new Size(Padding.Left, Padding.Top);

            double currentV = 0;
            foreach (var child in Children)
            {
                var desiredMeasure = new Size(child.DesiredSize.Width, child.DesiredSize.Height);
                if ((desiredMeasure.Width + position.Width) > parentMeasure.Width)
                {
                    // next row!
                    position.Width = Padding.Left;
                    position.Height += currentV + Spacing;
                    currentV = 0;
                }

                // Place the item
                child.Arrange(new Rect(position.Width, position.Height, child.DesiredSize.Width, child.DesiredSize.Height));

                // adjust the location for the next items
                position.Width += desiredMeasure.Width + Spacing;
                currentV = Math.Max(desiredMeasure.Height, currentV);
            }

            return finalSize;
        }
    }
}
