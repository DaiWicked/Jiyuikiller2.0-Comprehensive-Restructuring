using System;
using System.Windows;

namespace JiYuKiller
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // 初始化日志系统
            Services.Logger.Instance.Info("应用程序启动");
            Services.Logger.Instance.Info($"当前目录: {AppDomain.CurrentDomain.BaseDirectory}");
            Services.Logger.Instance.Info($"操作系统: {Environment.OSVersion}");

            base.OnStartup(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Services.Logger.Instance.Info($"应用程序退出，退出码: {e.ApplicationExitCode}");
            base.OnExit(e);
        }
    }
}
