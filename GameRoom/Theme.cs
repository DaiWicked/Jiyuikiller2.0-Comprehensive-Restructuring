using System.Windows;
using System.Windows.Media;

namespace GameRoom
{
    public static class Theme
    {
        private static readonly string[,] Palette = new string[,]
        {
            { "WindowBg",       "#E60B0A14" },
            { "WindowBorder",    "#FF2E2A4A" },
            { "Backdrop1",       "#0B0A14" },
            { "Backdrop2",       "#1A1430" },
            { "GlassBg",         "#1FFFFFFF" },
            { "GlassBorder",     "#2EFFFFFF" },
            { "PanelBg",         "#18FFFFFF" },
            { "TextPrimary",    "#FFFFFFFF" },
            { "TextSecondary",  "#FFB9B3D6" },
            { "TextDim",        "#FF6E6A8A" },
            { "Accent",          "#FF6C5CE7" },
            { "AccentLight",     "#FF8B7BF0" },
            { "AccentDark",      "#FF4B3FB5" },
            { "Gold",            "#FFE8C87A" },
            { "Online",          "#FF2ECC71" },
            { "Busy",            "#FFF5A623" },
            { "Offline",         "#FF6E6A8A" },
            { "Warn",            "#FFFF6B6B" },
        };

        public static void Apply()
        {
            ResourceDictionary r = Application.Current.Resources;
            for (int i = 0; i < Palette.GetLength(0); i++)
            {
                r[Palette[i, 0]] = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString(Palette[i, 1]));
            }
        }
    }
}
