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
            Theme.Apply(Models.ChatSettings.Load().DarkMode);
        }
    }
}
