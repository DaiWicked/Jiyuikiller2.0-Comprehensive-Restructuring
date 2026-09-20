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

        /// <summary>聊天区壁纸路径（空=用默认半透明灰）；由 SetWallpaper 复制到数据目录，避免原图被挪走后失效</summary>
        public string WallpaperPath { get; set; } = "";

        /// <summary>
        /// 壁纸模糊度（豆包修正需求 #7）：0 = 壁纸清晰，30 = 很糊（磨砂感），默认 15。
        /// ★ 注意：早期版本这里是"透明度"（WallpaperOpacity），用户要的其实是模糊 ——
        ///   壁纸**始终完全不透明**显示，滑块调的是磨砂程度，不是可见度。
        /// </summary>
        public double WallpaperBlur { get; set; } = 5;

        /// <summary>新消息弹窗提醒（豆包需求 #5：设置里可开关此功能；关掉只是不弹窗，未读红点照常）</summary>
        public bool ToastEnabled { get; set; } = true;

        /// <summary>界面动效总开关（豆包 Q7：一个开关控制全部淡入/滑动；关闭后直接显示终态）</summary>
        public bool Animations { get; set; } = true;

        /// <summary>勿扰模式-群聊：开启后收到群聊消息不弹窗/不闪烁红点，但仍记录历史</summary>
        public bool DoNotDisturbGroup { get; set; } = false;

        /// <summary>勿扰模式-私聊：开启后收到私聊消息不弹窗/不闪烁红点，但仍记录历史</summary>
        public bool DoNotDisturbPrivate { get; set; } = false;

        /// <summary>
        /// 动效开关的进程内镜像（豆包 Q7）。
        /// 各处动画（提醒窗滑动、各窗口淡入、呼吸光）都要读它，但并不是每处都拿得到 Settings 实例，
        /// 所以 Load/Save 时同步这一个静态位，动画代码只读它。
        /// </summary>
        public static bool AnimationsOn { get; set; } = true;

        /// <summary>把选中的图片复制进数据目录并记下路径（返回是否成功）</summary>
        public bool SetWallpaper(string sourcePath)
        {
            try
            {
                if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath)) return false;
                string ext = Path.GetExtension(sourcePath);
                if (string.IsNullOrEmpty(ext)) ext = ".jpg";
                string target = Path.Combine(DataDir, "wallpaper" + ext.ToLowerInvariant());
                if (!string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                    File.Copy(sourcePath, target, true);
                WallpaperPath = target;
                return true;
            }
            catch { return false; }
        }

        public void ClearWallpaper()
        {
            try { if (!string.IsNullOrEmpty(WallpaperPath) && File.Exists(WallpaperPath)) File.Delete(WallpaperPath); } catch { }
            WallpaperPath = "";
        }

        /// <summary>是否已完成首次注册（豆包需求 #2：首次启动弹注册页，之后不再弹，除非在设置里重置）</summary>
        public bool Registered { get; set; } = false;

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
                    string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                    string dir = string.IsNullOrEmpty(appData) ? null : Path.Combine(appData, "ChatRoom");
                    // 必须校验"非空且是绝对路径"：GetFolderPath 返回空串时 Path.Combine 会得到相对路径
                    // ⇒ 数据目录会跟着"当前工作目录"跑（快捷方式起始位置不同就数据分家）。
                    if (!string.IsNullOrEmpty(dir) && Path.IsPathRooted(dir))
                    {
                        Directory.CreateDirectory(dir);
                        _dataDir = dir;
                    }
                    else
                    {
                        _dataDir = AppDomain.CurrentDomain.BaseDirectory;
                    }
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

                // 标记位：迁移过就不再迁。
                // 否则 ClearOnExit=true 的用户"清空退出"后，下次启动又会把程序目录的旧历史拷回来（永久失效）。
                string marker = Path.Combine(newDir, ".migrated");
                if (File.Exists(marker)) return;

                // 逐文件容错：单个文件被占用/失败不能中断其余（否则半拷贝会永久化）
                foreach (string name in new[] { "chat_settings.ini", "chat_history.txt" })
                {
                    try
                    {
                        string from = Path.Combine(legacyDir, name);
                        string to = Path.Combine(newDir, name);
                        if (File.Exists(from) && !File.Exists(to)) File.Copy(from, to);
                    }
                    catch { }
                }

                try
                {
                    string fromImg = Path.Combine(legacyDir, "chat_images");
                    string toImg = Path.Combine(newDir, "chat_images");
                    if (Directory.Exists(fromImg))
                    {
                        try { Directory.CreateDirectory(toImg); } catch { }
                        foreach (string file in Directory.GetFiles(fromImg))
                        {
                            try
                            {
                                string to = Path.Combine(toImg, Path.GetFileName(file));
                                if (!File.Exists(to)) File.Copy(file, to);
                            }
                            catch { }
                        }
                    }
                }
                catch { }

                try { File.WriteAllText(marker, DateTime.Now.ToString("s")); } catch { }
            }
            catch { }
        }

        /// <summary>
        /// "需要重新注册"的标记文件。
        ///
        /// 为什么不用 ini 里的 Registered 字段表达：ini 是**内存对象回写**的 ——
        /// 重置发生在设置窗里，而运行中的主窗口持有启动时那份旧设置，关闭时会把它写回磁盘，
        /// 于是刚做的重置被覆盖（这正是"重置后下次启动没重新注册、直接用神秘人登录"的根因）。
        /// 独立标记文件不会被那条回写路径碰到，因此能可靠表达"下次启动要重新注册"。
        /// </summary>
        public static string NeedRegisterMarker
        {
            get { return Path.Combine(DataDir, "need_register.flag"); }
        }

        public static void MarkNeedRegister()
        {
            try { File.WriteAllText(NeedRegisterMarker, DateTime.Now.ToString("s")); } catch { }
        }

        public static void ClearNeedRegister()
        {
            try { if (File.Exists(NeedRegisterMarker)) File.Delete(NeedRegisterMarker); } catch { }
        }

        public static bool NeedRegister
        {
            get { try { return File.Exists(NeedRegisterMarker); } catch { return false; } }
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
                            case "Registered": s.Registered = val == "1"; break;
                            case "WallpaperPath": s.WallpaperPath = val; break;
                            case "WallpaperBlur": double.TryParse(val, out double wb); s.WallpaperBlur = wb; break;
                            case "ToastEnabled": s.ToastEnabled = val != "0"; break;
                            case "Animations": s.Animations = val != "0"; break;
                            case "DoNotDisturbGroup": s.DoNotDisturbGroup = val == "1"; break;
                            case "DoNotDisturbPrivate": s.DoNotDisturbPrivate = val == "1"; break;
                            case "WindowLeft": double.TryParse(val, out double wl); s.WindowLeft = wl; break;
                            case "WindowTop": double.TryParse(val, out double wt); s.WindowTop = wt; break;
                            case "WindowWidth": double.TryParse(val, out double ww); s.WindowWidth = ww; break;
                            case "WindowHeight": double.TryParse(val, out double wh); s.WindowHeight = wh; break;
                        }
                    }
                }
            }
            catch { }
            AnimationsOn = s.Animations;   // 同步动效总开关的静态镜像
            return s;
        }

        public void Save()
        {
            AnimationsOn = Animations;
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
                    "Registered=" + (Registered ? "1" : "0"),
                    "WallpaperPath=" + WallpaperPath,
                    "WallpaperBlur=" + WallpaperBlur.ToString("0.#"),
                    "ToastEnabled=" + (ToastEnabled ? "1" : "0"),
                    "Animations=" + (Animations ? "1" : "0"),
                    "DoNotDisturbGroup=" + (DoNotDisturbGroup ? "1" : "0"),
                    "DoNotDisturbPrivate=" + (DoNotDisturbPrivate ? "1" : "0"),
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
