using System;
using System.IO;
using System.Windows;

namespace ChatRoom.Models
{
    public class ChatSettings
    {
        public string Nickname { get; set; } = "神秘人";
        public bool SendWithEnter { get; set; } = true;
        public int FontSize { get; set; } = 13;
        public bool ClearOnExit { get; set; } = false;
        public bool TopMost { get; set; } = false;

        /// <summary>暗色模式（明暗两套配色见 Theme.cs）</summary>
        public bool DarkMode { get; set; } = false;

        // 窗口几何记忆（0 = 未保存过，用 XAML 默认值并居中）
        public double WindowLeft { get; set; } = 0;
        public double WindowTop { get; set; } = 0;
        public double WindowWidth { get; set; } = 0;
        public double WindowHeight { get; set; } = 0;

        private static string SettingsPath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "chat_settings.ini"); }
        }

        public static ChatSettings Load()
        {
            var s = new ChatSettings();
            try
            {
                if (File.Exists(SettingsPath))
                {
                    foreach (string line in File.ReadAllLines(SettingsPath, System.Text.Encoding.UTF8))
                    {
                        int eq = line.IndexOf('=');
                        if (eq < 0) continue;
                        string key = line.Substring(0, eq).Trim();
                        string val = line.Substring(eq + 1).Trim();
                        switch (key)
                        {
                            case "Nickname": s.Nickname = val; break;
                            case "SendWithEnter": s.SendWithEnter = val == "1"; break;
                            case "FontSize": int.TryParse(val, out int fs); s.FontSize = fs > 0 ? fs : 13; break;
                            case "ClearOnExit": s.ClearOnExit = val == "1"; break;
                            case "TopMost": s.TopMost = val == "1"; break;
                            case "DarkMode": s.DarkMode = val == "1"; break;
                            case "WindowLeft": double.TryParse(val, out double wl); s.WindowLeft = wl; break;
                            case "WindowTop": double.TryParse(val, out double wt); s.WindowTop = wt; break;
                            case "WindowWidth": double.TryParse(val, out double ww); s.WindowWidth = ww; break;
                            case "WindowHeight": double.TryParse(val, out double wh); s.WindowHeight = wh; break;
                        }
                    }
                }
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                var lines = new[]
                {
                    "Nickname=" + Nickname,
                    "SendWithEnter=" + (SendWithEnter ? "1" : "0"),
                    "FontSize=" + FontSize,
                    "ClearOnExit=" + (ClearOnExit ? "1" : "0"),
                    "TopMost=" + (TopMost ? "1" : "0"),
                    "DarkMode=" + (DarkMode ? "1" : "0"),
                    "WindowLeft=" + WindowLeft.ToString("F0"),
                    "WindowTop=" + WindowTop.ToString("F0"),
                    "WindowWidth=" + WindowWidth.ToString("F0"),
                    "WindowHeight=" + WindowHeight.ToString("F0")
                };
                File.WriteAllLines(SettingsPath, lines, System.Text.Encoding.UTF8);
            }
            catch { }
        }
    }
}
