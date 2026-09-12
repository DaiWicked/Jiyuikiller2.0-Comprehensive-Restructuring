using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace JiYuKiller.Games
{
    public partial class Minesweeper : UserControl
    {
        private const int Rows = 9, Cols = 9, Mines = 10;
        private Button[,] _cells;
        private bool[,] _isMine, _revealed, _flagged;
        private int _flagCount;
        private bool _gameOver;
        private Random _rand = new Random();

        public Minesweeper()
        {
            InitializeComponent();
            this.Loaded += (s, e) => InitGame();
        }

        private void InitGame()
        {
            _cells = new Button[Rows, Cols];
            _isMine = new bool[Rows, Cols];
            _revealed = new bool[Rows, Cols];
            _flagged = new bool[Rows, Cols];
            _flagCount = 0; _gameOver = false;
            MineText.Text = "剩余: " + Mines;
            GameGrid.Children.Clear();
            for (int i = 0; i < Rows; i++)
                for (int j = 0; j < Cols; j++)
                {
                    var btn = new Button
                    {
                        Tag = (i, j),
                        Background = Brushes.LightGray,
                        BorderBrush = Brushes.Gray,
                        BorderThickness = new Thickness(1),
                        FontSize = 14,
                        FontWeight = FontWeights.Bold,
                        Padding = new Thickness(0),
                        Margin = new Thickness(1)
                    };
                    btn.Click += Cell_Click;
                    btn.MouseRightButtonUp += Cell_RightClick;
                    _cells[i, j] = btn;
                    GameGrid.Children.Add(btn);
                }
        }

        private void PlaceMines(int safeR, int safeC)
        {
            int placed = 0;
            while (placed < Mines)
            {
                int r = _rand.Next(Rows), c = _rand.Next(Cols);
                if (_isMine[r, c] || (r == safeR && c == safeC)) continue;
                _isMine[r, c] = true; placed++;
            }
        }

        private void Cell_Click(object sender, RoutedEventArgs e)
        {
            if (_gameOver) return;
            var btn = sender as Button;
            var (r, c) = ((int, int))btn.Tag;
            if (_flagged[r, c] || _revealed[r, c]) return;
            if (!AnyRevealed()) PlaceMines(r, c);
            Reveal(r, c);
            CheckWin();
        }

        private bool AnyRevealed()
        {
            for (int i = 0; i < Rows; i++)
                for (int j = 0; j < Cols; j++)
                    if (_revealed[i, j]) return true;
            return false;
        }

        private void Reveal(int r, int c)
        {
            if (r < 0 || r >= Rows || c < 0 || c >= Cols || _revealed[r, c] || _flagged[r, c]) return;
            _revealed[r, c] = true;
            var btn = _cells[r, c];
            if (_isMine[r, c])
            {
                btn.Background = Brushes.Red; btn.Content = "💣"; _gameOver = true;
                for (int i = 0; i < Rows; i++)
                    for (int j = 0; j < Cols; j++)
                        if (_isMine[i, j]) { _cells[i, j].Background = Brushes.Red; _cells[i, j].Content = "💣"; }
                MessageBox.Show("游戏结束！");
                return;
            }
            int count = CountMines(r, c);
            btn.Background = Brushes.White;
            if (count > 0)
            {
                btn.Content = count.ToString();
                btn.Foreground = GetColor(count);
            }
            else
            {
                for (int dr = -1; dr <= 1; dr++)
                    for (int dc = -1; dc <= 1; dc++)
                        if (dr != 0 || dc != 0) Reveal(r + dr, c + dc);
            }
        }

        private int CountMines(int r, int c)
        {
            int count = 0;
            for (int dr = -1; dr <= 1; dr++)
                for (int dc = -1; dc <= 1; dc++)
                {
                    int nr = r + dr, nc = c + dc;
                    if (nr >= 0 && nr < Rows && nc >= 0 && nc < Cols && _isMine[nr, nc]) count++;
                }
            return count;
        }

        private Brush GetColor(int n)
        {
            switch (n)
            {
                case 1: return Brushes.Blue;
                case 2: return Brushes.Green;
                case 3: return Brushes.Red;
                case 4: return Brushes.DarkBlue;
                case 5: return Brushes.Brown;
                case 6: return Brushes.Teal;
                default: return Brushes.Black;
            }
        }

        private void Cell_RightClick(object sender, MouseButtonEventArgs e)
        {
            if (_gameOver) return;
            var btn = sender as Button;
            var (r, c) = ((int, int))btn.Tag;
            if (_revealed[r, c]) return;
            _flagged[r, c] = !_flagged[r, c];
            btn.Content = _flagged[r, c] ? "🚩" : "";
            _flagCount += _flagged[r, c] ? 1 : -1;
            MineText.Text = "剩余: " + (Mines - _flagCount);
        }

        private void CheckWin()
        {
            for (int i = 0; i < Rows; i++)
                for (int j = 0; j < Cols; j++)
                    if (!_isMine[i, j] && !_revealed[i, j]) return;
            _gameOver = true;
            MessageBox.Show("恭喜胜利！");
        }

        private void Restart_Click(object sender, RoutedEventArgs e) => InitGame();

        private void Back_Click(object sender, RoutedEventArgs e)
        {
            var parent = this.Parent as ContentControl ?? (Window.GetWindow(this) as MainWindow)?.GameContent;
            if (parent != null) parent.Content = new GameMenu();
        }
    }
}
