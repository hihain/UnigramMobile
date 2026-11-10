using Telegram.Td.Api;

namespace Unigram.Services
{
    public sealed class UpdateSelectedBackground
    {
        public UpdateSelectedBackground(bool forDarkTheme, Background background)
        {
            ForDarkTheme = forDarkTheme;
            Background = background;
        }

        public bool ForDarkTheme { get; }
        public Background Background { get; }
    }
}
