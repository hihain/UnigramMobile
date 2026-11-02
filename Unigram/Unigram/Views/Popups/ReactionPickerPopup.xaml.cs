using System;
using System.Collections.Generic;
using Unigram.Controls;
using Windows.UI.Xaml.Controls;

namespace Unigram.Views.Popups
{
    public sealed partial class ReactionPickerPopup : ContentPopup
    {
        public ReactionPickerPopup(IList<string> emojis)
        {
            InitializeComponent();

            Title = Strings.Resources.Emoji1;
            CloseButtonText = Strings.Resources.Cancel;
            FocusPrimaryButton = false;
            ReactionList.ItemsSource = emojis;
        }

        public event EventHandler<string> ReactionSelected;

        private void ReactionList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is string emoji)
            {
                ReactionSelected?.Invoke(this, emoji);
                Hide(ContentDialogResult.None);
            }
        }
    }
}
