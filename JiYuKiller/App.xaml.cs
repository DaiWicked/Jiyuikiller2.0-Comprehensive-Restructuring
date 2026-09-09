using System;
using System.Windows;

namespace JiYuKiller
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // 先加载设置，根据DebugMode决定是否启用日志
            Models.AppSettings settings = Models.AppSettings.Load();

            if (settings.DebugMode)
            {
                Services.Logger.Instance.Enable();
            }
            // DebugMode为false时不启用日志，不生成日志文件

            Services.Logger.Instance.Info("应用程序启动");
            Services.EmbeddedResourceService.ExtractAll();
            Services.Logger.Instance.Info($"当前目录: {AppDomain.CurrentDomain.BaseDirectory}");
            Services.Logger.Instance.Info($"操作系统: {Environment.OSVersion}");
            Services.Logger.Instance.Info($".NET版本: {Environment.Version}");
            Services.Logger.Instance.Info($"64位系统: {Environment.Is64BitOperatingSystem}");
            Services.Logger.Instance.Info($"64位进程: {Environment.Is64BitProcess}");
            Services.Logger.Instance.Info($"调试模式: {settings.DebugMode}");

            // 用户许可协议检查
            if (!settings.Argeed)
            {
                Services.Logger.Instance.Info("[协议] 用户未同意协议, 弹出协议窗口");
                AgreementWindow agreement = new AgreementWindow();
                agreement.ShowDialog();

                if (!agreement.IsAgreed)
                {
                    Services.Logger.Instance.Info("[协议] 用户拒绝协议, 程序退出");
                    Services.Logger.Instance.Close();
                    Application.Current.Shutdown();
                    return;
                }

                settings.Argeed = true;
                settings.Save();
                Services.Logger.Instance.Info("[协议] 用户已同意协议, 已保存");
            }
            else
            {
                Services.Logger.Instance.Debug("[协议] 用户已同意协议, 跳过");
            }

            // 全局异常捕获 - UI线程
            this.DispatcherUnhandledException += App_DispatcherUnhandledException;

            // 全局异常捕获 - 非UI线程
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

            // 手动创建并显示主窗口(移除了StartupUri, 避免协议窗口关闭后应用退出)
            Services.Logger.Instance.Info("[启动] 创建主窗口");
            MainWindow mainWindow = new MainWindow();
            mainWindow.Show();
            Services.Logger.Instance.Info("[启动] 主窗口已显示");
        }

        private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            // 生成错误报告（功能异常，非致命）
            string reportPath = Services.CrashReportService.GenerateReport(
                e.Exception,
                "UI线程",
                "未处理UI异常",
                isFatal: false);

            // 显示错误弹窗
            Services.CrashReportService.ShowErrorDialog(
                e.Exception,
                "UI线程",
                "未处理UI异常",
                reportPath,
                isFatal: false);

            // 尝试继续运行，不崩溃
            e.Handled = true;
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Exception ex = e.ExceptionObject as Exception;
            string moduleName = "非UI线程";
            string functionName = "未处理非UI异常";

            if (ex != null)
            {
                // 生成错误报告（致命错误，可能导致程序终止）
                string reportPath = Services.CrashReportService.GenerateReport(
                    ex,
                    moduleName,
                    functionName,
                    isFatal: e.IsTerminating);

                // 显示错误弹窗
                Services.CrashReportService.ShowErrorDialog(
                    ex,
                    moduleName,
                    functionName,
                    reportPath,
                    isFatal: e.IsTerminating);
            }
            else
            {
                // 非Exception类型的异常
                string reportPath = Services.CrashReportService.GenerateReport(
                    new Exception($"非异常对象: {e.ExceptionObject}"),
                    moduleName,
                    functionName,
                    isFatal: e.IsTerminating);

                System.Windows.MessageBox.Show(
                    $"模块: {moduleName}\n功能: {functionName}\n\n异常对象: {e.ExceptionObject}\n\n错误报告已保存至:\n{reportPath}",
                    e.IsTerminating ? "程序致命错误" : "功能异常",
                    MessageBoxButton.OK,
                    e.IsTerminating ? MessageBoxImage.Stop : MessageBoxImage.Error);
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Services.Logger.Instance.Info($"应用程序退出，退出码: {e.ApplicationExitCode}");
            Services.Logger.Instance.Close();
            base.OnExit(e);
        }
    }
}
