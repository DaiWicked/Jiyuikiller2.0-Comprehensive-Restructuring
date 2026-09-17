using System;
using System.IO;
using System.Xml.Serialization;

namespace JiYuKiller.Models
{
    /// <summary>
    /// 应用程序设置模型
    /// 参考原项目 SettingHlp，使用 XML 文件持久化
    /// </summary>
    [Serializable]
    public class AppSettings
    {
        // === 极域控制设置 ===

        /// <summary>禁止极域运行进程</summary>
        public bool BanJiYuRunOp { get; set; } = false;

        /// <summary>允许屏幕广播窗口置顶</summary>
        public bool AllowGbTop { get; set; } = false;

        /// <summary>禁止极域结束进程</summary>
        public bool ProhibitKillProcess { get; set; } = true;

        /// <summary>允许教师监视你的电脑</summary>
        public bool AllowMonitor { get; set; } = true;

        /// <summary>禁止极域关闭窗口</summary>
        public bool ProhibitCloseWindow { get; set; } = true;

        /// <summary>允许老师控制你的电脑</summary>
        public bool AllowControl { get; set; } = false;

        // === 其他设置 ===
        /// <summary>极域主进程位置</summary>
        public string JiYuMainPath { get; set; } = "";

        /// <summary>主窗口置顶</summary>
        public bool TopMost { get; set; } = false;

        /// <summary>调试模式</summary>
        public bool DebugMode { get; set; } = true;

        // === 软件高级设置（参考原项目 ConfigWindow）===
        /// <summary>禁用软件内核驱动</summary>
        public bool DisableDriver { get; set; } = false;

        /// <summary>驱动层自我保护（32位系统有效）</summary>
        public bool SelfProtect { get; set; } = true;

        /// <summary>不客气模式（自动强制清除）</summary>
        public bool AutoForceKill { get; set; } = false;

        /// <summary>严格窗口控制模式</summary>
        public bool AutoIncludeFullWindow { get; set; } = false;

        /// <summary>隐藏极域端控制输出窗口</summary>
        public bool DoNotShowVirusWindow { get; set; } = true;

        /// <summary>隐藏本软件任务栏图标</summary>
        public bool DoNotShowTrayIcon { get; set; } = false;


        /// <summary>强制安装在当前目录</summary>
        public bool ForceInstallInCurrentDir { get; set; } = false;

        /// <summary>强制禁用看门狗</summary>
        public bool ForceDisableWatchDog { get; set; } = false;


        /// <summary>结束进程模式: TerminateProcess / NtTerminateProcess / KernelMode</summary>
        public string KillProcessMode { get; set; } = "NtTerminateProcess";


        /// <summary>检查间隔（毫秒，1000-10000）</summary>
        public int CKInterval { get; set; } = 3100;

        /// <summary>紧急全屏快捷键</summary>
        public int HotKeyFakeFull { get; set; } = 1606;

        /// <summary>显示/隐藏窗口快捷键</summary>
        public int HotKeyShowHide { get; set; } = 1604;

        /// <summary>启用控制器</summary>
        public bool EnableController { get; set; } = true;

        // === Liquid Glass 设置 ===
        /// <summary>玻璃透明度 (0-100)</summary>
        public int GlassOpacity { get; set; } = 25;

        /// <summary>玻璃背景色 (white/blue/gray/dark/purple/green)</summary>
        public string GlassBgColor { get; set; } = "white";

        /// <summary>自定义壁纸路径</summary>
        public string WallpaperPath { get; set; } = "";

        /// <summary>壁纸透明度 (0-100)</summary>
        public int WallpaperOpacity { get; set; } = 80;

        // === 小小私聊布局配置 ===
        /// <summary>座位1号对应的IP末段（机房起始IP）</summary>
        public int ChatBaseIPLast { get; set; } = 127;

        /// <summary>机房列数</summary>
        public int ChatColumns { get; set; } = 5;

        /// <summary>每列排数</summary>
        public int ChatRowsPerColumn { get; set; } = 11;

        /// <summary>聊天昵称</summary>
        public string ChatNickname { get; set; } = "神秘人";

        // === 底栏液态玻璃（折射）===
        /// <summary>底栏圆角用超椭圆(squircle)而非正圆弧；false 则退化为普通圆角矩形</summary>
        public bool NavBarSquircle { get; set; } = true;

        /// <summary>超椭圆顺滑度 1.0~2.0（1.28≈COUI 默认，越接近 2 拐角越"软"）</summary>
        public double NavBarSquircleExtension { get; set; } = 1.2819;

        /// <summary>启用底栏液态玻璃折射效果</summary>
        public bool NavBarLiquidGlass { get; set; } = true;

        /// <summary>液态玻璃强度 0~1（控制向内折射量，即"水滴放大"的幅度）
        /// 默认 0.35 → 边缘折射约 5.6px。强度 1.0 时折射 16px、色散彩边明显，更像"水波"而不是玻璃。</summary>
        public double NavBarLiquidGlassStrength { get; set; } = 0.35;

        /// <summary>
        /// 在渲染层级 Tier=0（纯软件渲染：老机器 / 无 D3D 的虚拟机）上也强制启用。
        /// 默认 false —— 软件渲染下逐像素着色器可能拖慢拖动窗口。
        /// </summary>
        public bool NavBarLiquidGlassForceOnTier0 { get; set; } = false;

        // === 版本信息 ===
        public string Version { get; set; } = "QD_V3.0_JiYuRebuild_JustForYou";

        // === 用户协议 ===
        public bool Argeed { get; set; } = false;

        [XmlIgnore]
        private static readonly string SettingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "i.chaoxing.xml");

        /// <summary>
        /// 加载设置
        /// </summary>
        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    XmlSerializer serializer = new XmlSerializer(typeof(AppSettings));
                    using (FileStream fs = new FileStream(SettingsPath, FileMode.Open))
                    {
                        AppSettings settings = (AppSettings)serializer.Deserialize(fs);
                        // 强制更新版本号（版本号总是使用编译时的默认值，不使用保存的旧值）
                        string oldVersion = settings.Version;
                        settings.Version = new AppSettings().Version;
                        // 版本升级时，调试模式默认开启（用户可手动关闭）
                        if (oldVersion != settings.Version)
                        {
                            settings.DebugMode = true;
                        }
                        Services.Logger.Instance.Info($"设置加载成功，从: {SettingsPath}，旧版本: {oldVersion}，新版本: {settings.Version}，调试模式: {settings.DebugMode}");
                        return settings;
                    }
                }
            }
            catch (Exception ex)
            {
                Services.Logger.Instance.Error($"设置加载失败，使用默认设置", ex);
            }

            Services.Logger.Instance.Info("使用默认设置");
            return new AppSettings();
        }

        /// <summary>
        /// 保存设置
        /// </summary>
        /// <remarks>
        /// 先写临时文件再原子替换。
        /// 原实现直接 FileMode.Create 打开目标文件，一旦序列化中途抛异常(或进程被杀)，
        /// 原配置文件就已被截断成 0 字节，下次启动会静默回落到默认设置。
        /// </remarks>
        public void Save()
        {
            string tempPath = SettingsPath + ".tmp";
            try
            {
                XmlSerializer serializer = new XmlSerializer(typeof(AppSettings));
                using (FileStream fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    serializer.Serialize(fs, this);
                }

                if (File.Exists(SettingsPath))
                {
                    File.Replace(tempPath, SettingsPath, null);
                }
                else
                {
                    File.Move(tempPath, SettingsPath);
                }

                Services.Logger.Instance.Info($"设置保存成功，到: {SettingsPath}");
            }
            catch (Exception ex)
            {
                Services.Logger.Instance.Error($"设置保存失败", ex);
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch
                {
                    // 清理临时文件失败无碍
                }
            }
        }

        /// <summary>
        /// 恢复默认设置
        /// </summary>
        /// <remarks>
        /// 只重置"设置项"，不重置"用户数据":
        ///   Version       —— 版本号由编译期默认值决定
        ///   Argeed        —— 用户协议是否已同意(清掉会导致协议窗口再次弹出)
        ///   JiYuMainPath  —— 用户手动指定的极域安装路径(清掉会丢失定位结果)
        /// </remarks>
        public void ResetToDefault()
        {
            AppSettings def = new AppSettings();
            string ver = this.Version;
            bool argeed = this.Argeed;
            string jiYuPath = this.JiYuMainPath;

            CopyFrom(def);

            this.Version = ver;
            this.Argeed = argeed;
            this.JiYuMainPath = jiYuPath;
            Services.Logger.Instance.Warn("设置已恢复默认（已保留版本号、协议同意状态与极域路径）");
        }

        private void CopyFrom(AppSettings other)
        {
            var props = typeof(AppSettings).GetProperties();
            foreach (var prop in props)
            {
                if (prop.CanWrite && prop.Name != "Version")
                {
                    prop.SetValue(this, prop.GetValue(other));
                }
            }
        }
    }
}
