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
            if (!first.Registered)
            {
                var reg = new RegisterWindow();
                if (reg.ShowDialog() != true) { Shutdown(); return; }
            }
        }
    }
}
