using System;
using System.Windows;

namespace JiYuKiller
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // 全局异常捕获必须先注册:
            // 原实现放在协议窗口之后, 一旦 Load()/协议窗口/资源释放抛异常就完全没有错误报告。
            this.DispatcherUnhandledException += App_DispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

            // 先加载设置，根据DebugMode决定是否启用日志
            Models.AppSettings settings = Models.AppSettings.Load();

            if (settings.DebugMode)
            {
                Services.Logger.Instance.Enable();
            }
            // DebugMode为false时不启用日志，不生成日志文件

            Services.Logger.Instance.Info("应用程序启动");

            // 释放目录策略（对应上游 ForceInstallInCurrentDir）：勾了就释放到 exe 所在目录。
            // 必须在 ExtractAll 之前设置 —— 首次释放就在下面这几行发生，顺序反了策略对本次释放不生效。
            Services.EmbeddedResourceService.ForceInstallInCurrentDir = settings.ForceInstallInCurrentDir;
            Services.Logger.Instance.Info("[启动] 释放目录策略: " + (settings.ForceInstallInCurrentDir ? "当前目录(exe 所在目录)" : "用户目录 %LOCALAPPDATA%"));
            if (!Services.EmbeddedResourceService.ExtractAll())
            {
                Services.Logger.Instance.Warn("[启动] 嵌入资源未全部释放成功, 驱动/DLL 相关功能可能不可用");
            }
            Services.Logger.Instance.Info($"当前目录: {AppDomain.CurrentDomain.BaseDirectory}");
            Services.Logger.Instance.Info($"操作系统(受兼容性垫片影响): {Environment.OSVersion}");
            // Environment.OSVersion 在缺少 supportedOS 清单时会谎报 6.2.9200, 这里额外记录真实版本
            Services.Logger.Instance.Info($"操作系统(真实): {Services.DriverService.DescribeRealWindowsVersion()}");
            Services.Logger.Instance.Info($".NET版本: {Environment.Version}");
            Services.Logger.Instance.Info($"64位系统: {Environment.Is64BitOperatingSystem}");
            Services.Logger.Instance.Info($"64位进程: {Environment.Is64BitProcess}");
            Services.Logger.Instance.Info($"调试模式: {settings.DebugMode}");


            // 自检钩子: JYKILLER_FORCE_SOFTWARE_RENDER=1 强制软件渲染(= Win7 无 D3D 的 Tier 0)
            // 用途: 在快机器上复现老机器/虚拟机的观感问题(模糊、着色器回退、合成差异)。
            // 必须在任何窗口创建之前设置，所以放在这里。
            if (Environment.GetEnvironmentVariable("JYKILLER_FORCE_SOFTWARE_RENDER") == "1")
            {
                System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
                Services.Logger.Instance.Warn("[渲染] 已按环境变量强制软件渲染 (SoftwareOnly / Tier 0)");
            }

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

            // 手动创建并显示主窗口(移除了StartupUri, 避免协议窗口关闭后应用退出)
            Services.Logger.Instance.Info("[启动] 创建主窗口");
            MainWindow mainWindow = new MainWindow();
            mainWindow.Show();
            Services.Logger.Instance.Info("[启动] 主窗口已显示");
        }

        private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            // ★ 处理函数自身必须包 try/catch（豆包 Q12-2）：
            //   这已经在"未处理异常"路径上了，如果 GenerateReport / ShowErrorDialog 自己再抛
            //   （磁盘满、渲染路径复抛…），异常会从处理函数里逃出去 —— 轻则弹不出报告，
            //   重则递归崩溃。catch 里刻意不做任何"可能再抛"的事（不写文件、不弹窗）。
            try
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
            }
            catch
            {
                // 连报告/弹窗都失败：只能保证程序别在这里再崩一次
            }
            finally
            {
                // 尝试继续运行，不崩溃（原来这行在 try 之外、且没有 try 保护）
                try { e.Handled = true; } catch { }
            }
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            // 同 App_DispatcherUnhandledException：处理函数自身必须有 try/catch
            try
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
            catch
            {
                // 处理函数自身失败：不再做任何可能再抛的事
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
