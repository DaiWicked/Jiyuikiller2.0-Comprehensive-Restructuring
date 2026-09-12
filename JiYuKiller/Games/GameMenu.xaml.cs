using System.Windows;
using System.Windows.Controls;

namespace JiYuKiller.Games
{
    public partial class GameMenu : UserControl
    {
        public GameMenu()
        {
            InitializeComponent();
        }

        private void Minesweeper_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var parent = this.Parent as ContentControl ?? (Window.GetWindow(this) as MainWindow)?.GameContent;
            if (parent != null) parent.Content = new Minesweeper();
        }

        private void Dino_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var parent = this.Parent as ContentControl ?? (Window.GetWindow(this) as MainWindow)?.GameContent;
            if (parent != null) parent.Content = new DinoGame();
        }
    }
}
