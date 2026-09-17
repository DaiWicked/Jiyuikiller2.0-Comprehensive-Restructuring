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
                    "TopMost=" + (TopMost ? "1" : "0")
                };
                File.WriteAllLines(SettingsPath, lines, System.Text.Encoding.UTF8);
            }
            catch { }
        }
    }
}
