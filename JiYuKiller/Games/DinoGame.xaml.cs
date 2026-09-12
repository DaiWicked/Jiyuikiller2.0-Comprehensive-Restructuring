using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace JiYuKiller.Games
{
    public partial class DinoGame : UserControl
    {
        private string _htmlPath;

        public DinoGame()
        {
            InitializeComponent();
            this.Loaded += (s, e) => LoadGame();
        }

        private void LoadGame()
        {
            try
            {
                // 优先使用程序目录下的dino.html，回退到源码目录
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                _htmlPath = Path.Combine(baseDir, "Games", "dino.html");
                if (!File.Exists(_htmlPath))
                {
                    _htmlPath = Path.Combine(baseDir, "dino.html");
                }
                if (File.Exists(_htmlPath))
                {
                    GameBrowser.Navigate(new Uri(_htmlPath));
                }
                else
                {
                    // 内嵌HTML作为兜底
                    string fallback = "<html><body style='margin:0;background:#f7f7f7;display:flex;align-items:center;justify-content:center;height:200px;font-family:Arial'><div style='text-align:center'><div style='font-size:48px'>🦖</div><div style='color:#666;margin-top:8px'>游戏加载中...</div></div></body></html>";
                    GameBrowser.NavigateToString(fallback);
                }
            }
            catch (Exception ex)
            {
                GameBrowser.NavigateToString("<html><body style='display:flex;align-items:center;justify-content:center;height:200px;font-family:Arial;color:#666'>加载失败: " + ex.Message + "</body></html>");
            }
        }

        private void Restart_Click(object sender, RoutedEventArgs e)
        {
            LoadGame();
        }

        private void Back_Click(object sender, RoutedEventArgs e)
        {
            try { GameBrowser.Navigate("about:blank"); } catch { }
            var parent = this.Parent as ContentControl ?? (Window.GetWindow(this) as MainWindow)?.GameContent;
            if (parent != null) parent.Content = new GameMenu();
        }
    }
}
