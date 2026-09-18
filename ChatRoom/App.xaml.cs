using System;
using System.Windows;

namespace ChatRoom
{
    public partial class App : Application
    {
        public static string Nickname { get; set; } = "神秘人";
        public static string LocalIP { get; set; } = "";

        /// <summary>
        /// 主题必须在主窗口创建之前应用：XAML 里全是 DynamicResource，
        /// 先有资源再建窗口，才不会出现"画刷为空"的一帧（AllowsTransparency 下会闪一下）。
        /// </summary>
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            var first = Models.ChatSettings.Load();
            Theme.Apply(first.DarkMode);

            // 首次使用：先完成注册（豆包需求 #2），否则不进入主界面
            if (!first.Registered || Models.ChatSettings.NeedRegister)   // 标记文件优先：不受内存回写影响
            {
                // ★ 注册期间必须临时改成"只有显式 Shutdown 才退出"：
                //   主窗口此时还没建出来，注册窗是唯一的窗口。默认的 ShutdownMode.OnLastWindowClose
                //   会在注册窗关闭、Windows.Count 归零的瞬间把整个程序关掉 ——
                //   表现就是"注册完程序自己退了/闪一下没了，还得手动重启"（用户实测反馈）。
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var reg = new RegisterWindow();
                bool ok = reg.ShowDialog() == true;
                if (!ok) { Shutdown(); return; }      // 用户点了"退出"
                ShutdownMode = ShutdownMode.OnMainWindowClose;
            }

            // 主窗口在这里显式创建，不再用 App.xaml 的 StartupUri：
            // StartupUri 的窗口是 OnStartup 返回之后才创建的，中间那段"一个窗口都没有"的空窗期
            // 正是上面那个退出 bug 的根源。显式创建还能顺手把 MainWindow 属性设好，
            // 让 ShutdownMode.OnMainWindowClose 有依据。
            var main = new MainWindow();
            MainWindow = main;
            main.Show();
        }
    }
}
