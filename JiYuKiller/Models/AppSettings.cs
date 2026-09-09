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
        /// <summary>监控极域运行进程</summary>
        public bool MonitorJiYuProcess { get; set; } = true;

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

        /// <summary>注入MasterHelper</summary>
        public bool InjectMasterHelper { get; set; } = false;

        /// <summary>注入ProcHelper64</summary>
        public bool InjectProcHelper64 { get; set; } = false;

        /// <summary>结束进程模式: TerminateProcess / NtTerminateProcess / KernelMode</summary>
        public string KillProcessMode { get; set; } = "NtTerminateProcess";

        /// <summary>注入模式: RemoteThread / HookDllStub</summary>
        public string InjectMode { get; set; } = "RemoteThread";

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
        public int GlassOpacity { get; set; } = 72;

        /// <summary>玻璃背景色 (white/blue/gray/dark/purple/green)</summary>
        public string GlassBgColor { get; set; } = "white";

        /// <summary>自定义壁纸路径</summary>
        public string WallpaperPath { get; set; } = "";

        /// <summary>壁纸透明度 (0-100)</summary>
        public int WallpaperOpacity { get; set; } = 80;

        // === 版本信息 ===
        public string Version { get; set; } = "QD_V2.3_JiYuRebuild_CoUI-Glass";

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
        public void Save()
        {
            try
            {
                XmlSerializer serializer = new XmlSerializer(typeof(AppSettings));
                using (FileStream fs = new FileStream(SettingsPath, FileMode.Create))
                {
                    serializer.Serialize(fs, this);
                }
                Services.Logger.Instance.Info($"设置保存成功，到: {SettingsPath}");
            }
            catch (Exception ex)
            {
                Services.Logger.Instance.Error($"设置保存失败", ex);
            }
        }

        /// <summary>
        /// 恢复默认设置
        /// </summary>
        public void ResetToDefault()
        {
            AppSettings def = new AppSettings();
            // 保留版本信息
            string ver = this.Version;
            CopyFrom(def);
            this.Version = ver;
            Services.Logger.Instance.Warn("设置已恢复默认");
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
