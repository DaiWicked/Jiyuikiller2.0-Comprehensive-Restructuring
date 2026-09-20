using System.Windows;
using System.Windows.Media;

namespace GameRoom
{
    /// <summary>深色主题配色（跟ChatRoom一致）</summary>
    public static class Theme
    {
        private static readonly string[,] Palette = new string[,]
        {
            { "WindowBg",      "#E617212B" },
            { "WindowBorder",   "#FF2B3A4A" },
            { "PanelBg",        "#D917212B" },
            { "HeaderBg",       "#D917212B" },
            { "ContentBg",      "#FF0E1621" },
            { "Divider",        "#FF101921" },
            { "TextPrimary",    "#FFFFFFFF" },
            { "TextSecondary",  "#FF7D8B99" },
            { "Accent",         "#FF5B8DEF" },
            { "AccentFg",       "#FFFFFFFF" },
            { "RowHoverBg",    "#FF202B36" },
            { "RowSelectedBg", "#FF2B5278" },
            { "GlassBorder",    "#335B8DEF" },
            { "OnlineGreen",   "#FF2ECC71" },
            { "WarnRed",        "#FFFF6B6B" },
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
