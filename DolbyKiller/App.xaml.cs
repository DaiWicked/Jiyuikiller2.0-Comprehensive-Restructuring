using System;
using System.Windows;

namespace DolbyKiller
{
    public partial class App : Application
    {
        // 单实例互斥体
        private static System.Threading.Mutex _singleInstanceMutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            bool createdNew;
            _singleInstanceMutex = new System.Threading.Mutex(true, "Global\\DolbyKiller_SingleInstance", out createdNew);
            if (!createdNew)
            {
                MessageBox.Show("反制工具已经在运行中。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }
            base.OnStartup(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try { _singleInstanceMutex?.ReleaseMutex(); } catch { }
            base.OnExit(e);
            // 兜底：强制退出，防止WPF渲染线程或后台线程卡住导致僵尸进程
            Environment.Exit(0);
        }
    }
}
