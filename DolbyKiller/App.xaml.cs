using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace DolbyKiller
{
    public partial class App : Application
    {
        private static System.Threading.Mutex _singleInstanceMutex;
        private const string MutexName = "Global\\DolbyKiller_SingleInstance";

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        protected override void OnStartup(StartupEventArgs e)
        {
            // 全局异常捕获
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

            bool createdNew;
            _singleInstanceMutex = new System.Threading.Mutex(true, MutexName, out createdNew);
            if (!createdNew)
            {
                // 尝试找到并激活已有实例窗口
                ActivateExistingInstance();
                Shutdown();
                return;
            }
            base.OnStartup(e);
        }

        private void ActivateExistingInstance()
        {
            try
            {
                var current = Process.GetCurrentProcess();
                foreach (var p in Process.GetProcessesByName(current.ProcessName))
                {
                    if (p.Id != current.Id && p.MainWindowHandle != IntPtr.Zero)
                    {
                        if (IsIconic(p.MainWindowHandle))
                            ShowWindow(p.MainWindowHandle, 9); // SW_RESTORE
                        SetForegroundWindow(p.MainWindowHandle);
                        return;
                    }
                }
            }
            catch { }
            // 找不到窗口时弹提示，确保前台显示
            MessageBox.Show("反制工具已经在运行中。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information,
                MessageBoxResult.OK, MessageBoxOptions.DefaultDesktopOnly);
        }

        private void OnDispatcherUnhandledException(object sender,
            System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            LogFatal("UI线程未处理异常", e.Exception);
            e.Handled = true;
            MessageBox.Show($"程序发生异常：\n{e.Exception.Message}\n\n详情已写入日志文件。",
                "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var ex = e.ExceptionObject as Exception;
            LogFatal("非UI线程未处理异常", ex);
        }

        private static void LogFatal(string title, Exception ex)
        {
            try
            {
                string logPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    "DolbyKiller_crash.log");
                File.AppendAllText(logPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {title}\n{ex}\n\n");
            }
            catch { }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try { _singleInstanceMutex?.ReleaseMutex(); } catch { }
            base.OnExit(e);
        }
    }
}
