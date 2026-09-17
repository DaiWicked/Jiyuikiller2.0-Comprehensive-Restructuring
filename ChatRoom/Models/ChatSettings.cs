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

        private static string _dataDir;

        /// <summary>
        /// 数据目录：优先 %APPDATA%\ChatRoom。
        /// 原来设置/历史/图片全写程序目录 ⇒ 装到 Program Files 或非管理员运行时**写入全部静默失败**
        /// （设置存不下、历史丢失、图片显示"已过期"）。不可写时退回程序目录（绿色版仍可用）。
        /// </summary>
        public static string DataDir
        {
            get
            {
                if (_dataDir != null) return _dataDir;
                try
                {
                    string dir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ChatRoom");
                    Directory.CreateDirectory(dir);
                    string probe = Path.Combine(dir, ".writable");
                    File.WriteAllText(probe, "1");
                    File.Delete(probe);
                    _dataDir = dir;
                }
                catch
                {
                    _dataDir = AppDomain.CurrentDomain.BaseDirectory;
                }
                return _dataDir;
            }
        }

        /// <summary>
        /// 老版本数据在程序目录，升级后第一次运行搬过来（只搬一次，不覆盖已存在的新文件）。
        /// </summary>
        public static void MigrateLegacyData()
        {
            try
            {
                string legacyDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
                string newDir = DataDir.TrimEnd(Path.DirectorySeparatorChar);
                if (string.Equals(legacyDir, newDir, StringComparison.OrdinalIgnoreCase)) return;   // 没换目录，不用搬

                foreach (string name in new[] { "chat_settings.ini", "chat_history.txt" })
                {
                    string from = Path.Combine(legacyDir, name);
                    string to = Path.Combine(newDir, name);
                    if (File.Exists(from) && !File.Exists(to)) File.Copy(from, to);
                }

                string fromImg = Path.Combine(legacyDir, "chat_images");
                string toImg = Path.Combine(newDir, "chat_images");
                if (Directory.Exists(fromImg) && !Directory.Exists(toImg))
                {
                    Directory.CreateDirectory(toImg);
                    foreach (string file in Directory.GetFiles(fromImg))
                        File.Copy(file, Path.Combine(toImg, Path.GetFileName(file)));
                }
            }
            catch { }
        }

        private static string SettingsPath
        {
            get { return Path.Combine(DataDir, "chat_settings.ini"); }
        }

        public static ChatSettings Load()
        {
            MigrateLegacyData();   // 老版本数据在程序目录，升级后第一次运行搬到 %APPDATA%
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
