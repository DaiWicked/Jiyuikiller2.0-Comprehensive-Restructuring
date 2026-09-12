using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace JiYuKiller.Games
{
    /// <summary>
    /// 小游戏菜单：游戏页面的入口，选择后把游戏 UserControl 放进宿主 ContentControl。
    /// </summary>
    public partial class GameMenu : UserControl
    {
        private bool _opening;

        public GameMenu()
        {
            InitializeComponent();
        }

        #region 卡片与按钮

        private void OpenMinesweeper_Click(object sender, RoutedEventArgs e)
        {
            LogClick("开始扫雷");
            OpenGame(new Minesweeper());
        }

        private void OpenDino_Click(object sender, RoutedEventArgs e)
        {
            LogClick("开始恐龙跳");
            OpenGame(new DinoGame());
        }

        private void CardMinesweeper_Click(object sender, MouseButtonEventArgs e)
        {
            LogClick("点击扫雷卡片");
            OpenGame(new Minesweeper());
        }

        private void CardDino_Click(object sender, MouseButtonEventArgs e)
        {
            LogClick("点击恐龙跳卡片");
            OpenGame(new DinoGame());
        }

        #endregion

        #region 宿主与导航

        /// <summary>取出承载游戏的 ContentControl：优先用 Parent，其次回落到 MainWindow.GameContent。</summary>
        private ContentControl GetHost()
        {
            var host = this.Parent as ContentControl;
            if (host != null) return host;

            var window = Window.GetWindow(this) as MainWindow;
            return window != null ? window.GameContent : null;
        }

        private void OpenGame(UserControl game)
        {
            // 卡片自身的点击与卡片内按钮的点击可能先后触发，这里做一次去重
            if (_opening) return;
            _opening = true;

            try
            {
                ContentControl host = GetHost();
                if (host != null)
                {
                    host.Content = game;
                }
                else
                {
                    LogError("OpenGame", new InvalidOperationException("未找到游戏宿主 ContentControl"));
                }
            }
            catch (Exception ex) { LogError("OpenGame", ex); }
            finally { _opening = false; }
        }

        #endregion

        #region 工具

        private static void LogClick(string name)
        {
            try { Services.Logger.Instance.ButtonClick(name, "GameMenu"); }
            catch { }
        }

        private static void LogError(string where, Exception ex)
        {
            try { Services.Logger.Instance.Debug("小游戏菜单异常 [" + where + "]: " + ex.Message); }
            catch { }
        }

        #endregion
    }
}
