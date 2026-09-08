using System;
using System.Windows;

namespace JiYuKiller
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // 全局异常捕获 - UI线程
            this.DispatcherUnhandledException += App_DispatcherUnhandledException;

            // 全局异常捕获 - 非UI线程
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

            // 初始化日志系统
            Services.Logger.Instance.Info("应用程序启动");
            Services.Logger.Instance.Info($"当前目录: {AppDomain.CurrentDomain.BaseDirectory}");
            Services.Logger.Instance.Info($"操作系统: {Environment.OSVersion}");
            Services.Logger.Instance.Info($".NET版本: {Environment.Version}");
            Services.Logger.Instance.Info($"64位系统: {Environment.Is64BitOperatingSystem}");
            Services.Logger.Instance.Info($"64位进程: {Environment.Is64BitProcess}");

            base.OnStartup(e);
        }

        private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            Services.Logger.Instance.Error($"UI线程未处理异常: {e.Exception.Message}", e.Exception);
            Services.Logger.Instance.Error($"异常堆栈: {e.Exception.StackTrace}");

            // 尝试继续运行，不崩溃
            e.Handled = true;

            try
            {
                MessageBox.Show($"程序发生错误：\n{e.Exception.Message}\n\n详情请查看日志文件。",
                    "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch { }
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Exception ex = e.ExceptionObject as Exception;
            if (ex != null)
            {
                Services.Logger.Instance.Error($"非UI线程未处理异常: {ex.Message}", ex);
                Services.Logger.Instance.Error($"异常堆栈: {ex.StackTrace}");
            }
            else
            {
                Services.Logger.Instance.Error($"非UI线程未处理异常: {e.ExceptionObject}");
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Services.Logger.Instance.Info($"应用程序退出，退出码: {e.ApplicationExitCode}");
            base.OnExit(e);
        }
    }
}
