using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace JiYuKiller.Games
{
    /// <summary>
    /// 扫雷：9x9 网格 / 10 颗雷 / 首次点击保证安全 / 右键标旗 / 空白区域递归展开。
    /// 全部使用 WPF 原生控件，无外部资源。
    /// </summary>
    public partial class Minesweeper : UserControl
    {
        #region 常量

        private const int Rows = 9;
        private const int Cols = 9;
        private const int MineTotal = 10;

        // 数字颜色：1蓝 2绿 3红 4深蓝 5棕 6青
        private static readonly Brush[] NumberBrushes =
        {
            Brushes.Transparent,                                     // 0 (不显示)
            new SolidColorBrush(Color.FromRgb(0x00, 0x00, 0xFF)),    // 1 蓝
            new SolidColorBrush(Color.FromRgb(0x00, 0x80, 0x00)),    // 2 绿
            new SolidColorBrush(Color.FromRgb(0xFF, 0x00, 0x00)),    // 3 红
            new SolidColorBrush(Color.FromRgb(0x00, 0x00, 0x80)),    // 4 深蓝
            new SolidColorBrush(Color.FromRgb(0x80, 0x00, 0x00)),    // 5 棕
            new SolidColorBrush(Color.FromRgb(0x00, 0x80, 0x80)),    // 6 青
            new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),    // 7
            new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33))     // 8
        };

        private static readonly Brush UnrevealedBrush = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0));
        private static readonly Brush UnrevealedBorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
        private static readonly Brush HoverBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
        private static readonly Brush RevealedBrush = new SolidColorBrush(Color.FromRgb(0xD8, 0xD8, 0xD8));
        private static readonly Brush RevealedBorderBrush = new SolidColorBrush(Color.FromRgb(0xC2, 0xC2, 0xC2));
        private static readonly Brush ExplodedBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B));
        private static readonly Brush WrongFlagBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xB0, 0xB0));

        #endregion

        #region 状态

        private readonly bool[,] _isMine = new bool[Rows, Cols];
        private readonly int[,] _adjacent = new int[Rows, Cols];
        private readonly bool[,] _revealed = new bool[Rows, Cols];
        private readonly bool[,] _flagged = new bool[Rows, Cols];
        private readonly bool[,] _exploded = new bool[Rows, Cols];   // 踩中的那颗雷
        private readonly bool[,] _wrongFlag = new bool[Rows, Cols];  // 标错的旗子
        private readonly Border[,] _cells = new Border[Rows, Cols];
        private readonly TextBlock[,] _cellText = new TextBlock[Rows, Cols];

        private readonly Random _rng = new Random();
        private readonly DispatcherTimer _timer = new DispatcherTimer();

        private bool _minesPlaced;      // 雷是否已布置（首次点击后才布置，保证首次安全）
        private bool _gameOver;
        private int _flagsUsed;
        private int _revealedCount;
        private int _elapsedSeconds;

        #endregion

        public Minesweeper()
        {
            InitializeComponent();

            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.Tick += Timer_Tick;

            Loaded += Minesweeper_Loaded;
            Unloaded += Minesweeper_Unloaded;

            BuildBoard();
            ResetGame();
        }

        #region 生命周期

        private void Minesweeper_Loaded(object sender, RoutedEventArgs e)
        {
            // 回到本页面时恢复计时（仅在游戏进行中）
            try
            {
                if (!_gameOver && _minesPlaced && !_timer.IsEnabled) _timer.Start();
            }
            catch (Exception ex) { LogError("Loaded", ex); }
        }

        private void Minesweeper_Unloaded(object sender, RoutedEventArgs e)
        {
            // 离开页面必须停表，否则计时器会一直持有本控件
            StopTimer();
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            try
            {
                if (_gameOver)
                {
                    StopTimer();
                    return;
                }
                _elapsedSeconds++;
                if (TimeText != null) TimeText.Text = "时间: " + _elapsedSeconds + "s";
            }
            catch (Exception ex) { LogError("Timer_Tick", ex); }
        }

        #endregion

        #region 棋盘构建

        private void BuildBoard()
        {
            try
            {
                GameGrid.Children.Clear();

                for (int r = 0; r < Rows; r++)
                {
                    for (int c = 0; c < Cols; c++)
                    {
                        var text = new TextBlock
                        {
                            FontSize = 15,
                            FontWeight = FontWeights.Bold,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center,
                            Text = ""
                        };

                        var cell = new Border
                        {
                            Margin = new Thickness(1),
                            CornerRadius = new CornerRadius(3),
                            BorderThickness = new Thickness(1),
                            Background = UnrevealedBrush,
                            BorderBrush = UnrevealedBorderBrush,
                            Cursor = Cursors.Hand,
                            Child = text,
                            Tag = r * Cols + c
                        };

                        cell.MouseLeftButtonDown += Cell_LeftDown;
                        cell.MouseRightButtonDown += Cell_RightDown;
                        cell.MouseEnter += Cell_MouseEnter;
                        cell.MouseLeave += Cell_MouseLeave;

                        _cells[r, c] = cell;
                        _cellText[r, c] = text;
                        GameGrid.Children.Add(cell);
                    }
                }
            }
            catch (Exception ex) { LogError("BuildBoard", ex); }
        }

        #endregion

        #region 鼠标事件

        private void Cell_LeftDown(object sender, MouseButtonEventArgs e)
        {
            try
            {
                var cell = sender as Border;
                if (cell == null) return;

                int index = (int)cell.Tag;
                LeftClick(index / Cols, index % Cols);
                e.Handled = true;
            }
            catch (Exception ex) { LogError("Cell_LeftDown", ex); }
        }

        private void Cell_RightDown(object sender, MouseButtonEventArgs e)
        {
            try
            {
                var cell = sender as Border;
                if (cell == null) return;

                int index = (int)cell.Tag;
                int r = index / Cols;
                int c = index % Cols;

                if (!_gameOver && !_revealed[r, c])
                {
                    _flagged[r, c] = !_flagged[r, c];
                    _flagsUsed += _flagged[r, c] ? 1 : -1;
                    ShowCell(r, c);
                    UpdateMineText();
                }

                e.Handled = true; // 阻止右键菜单
            }
            catch (Exception ex) { LogError("Cell_RightDown", ex); }
        }

        private void Cell_MouseEnter(object sender, MouseEventArgs e)
        {
            try
            {
                var cell = sender as Border;
                if (cell == null || _gameOver) return;

                int index = (int)cell.Tag;
                int r = index / Cols;
                int c = index % Cols;

                if (!_revealed[r, c] && !_exploded[r, c] && !_wrongFlag[r, c]) cell.Background = HoverBrush;
            }
            catch (Exception ex) { LogError("Cell_MouseEnter", ex); }
        }

        private void Cell_MouseLeave(object sender, MouseEventArgs e)
        {
            try
            {
                var cell = sender as Border;
                if (cell == null) return;

                int index = (int)cell.Tag;
                ShowCell(index / Cols, index % Cols);
            }
            catch (Exception ex) { LogError("Cell_MouseLeave", ex); }
        }

        #endregion

        #region 游戏逻辑

        private void LeftClick(int r, int c)
        {
            if (_gameOver) return;
            if (_revealed[r, c] || _flagged[r, c]) return;

            // 首次点击保证安全：先点击，再排除该格布雷
            if (!_minesPlaced)
            {
                PlaceMines(r, c);
                if (!_timer.IsEnabled) _timer.Start();
            }

            if (_isMine[r, c])
            {
                _revealed[r, c] = true;
                ShowCell(r, c);
                _gameOver = true;
                RevealAllMines(r, c);
                StopTimer();
                ShowMessage("游戏结束", "你踩到雷了！\n\n用时: " + _elapsedSeconds + " 秒");
                return;
            }

            Expand(r, c);
            UpdateMineText();
            CheckWin();
        }

        /// <summary>布置雷，排除首次点击的格子（保证首次点击不会踩雷）。</summary>
        private void PlaceMines(int excludeR, int excludeC)
        {
            _minesPlaced = true;

            int placed = 0;
            int guard = 0;
            while (placed < MineTotal && guard < 100000)
            {
                guard++;
                int r = _rng.Next(Rows);
                int c = _rng.Next(Cols);

                if (r == excludeR && c == excludeC) continue;
                if (_isMine[r, c]) continue;

                _isMine[r, c] = true;
                placed++;
            }

            for (int r = 0; r < Rows; r++)
                for (int c = 0; c < Cols; c++)
                    _adjacent[r, c] = CountAdjacent(r, c);
        }

        private int CountAdjacent(int r, int c)
        {
            int count = 0;
            for (int dr = -1; dr <= 1; dr++)
            {
                for (int dc = -1; dc <= 1; dc++)
                {
                    if (dr == 0 && dc == 0) continue;
                    int nr = r + dr;
                    int nc = c + dc;
                    if (nr < 0 || nr >= Rows || nc < 0 || nc >= Cols) continue;
                    if (_isMine[nr, nc]) count++;
                }
            }
            return count;
        }

        /// <summary>空白区域自动展开。用显式栈代替递归，避免栈溢出。</summary>
        private void Expand(int startR, int startC)
        {
            var stack = new Stack<int>();
            stack.Push(startR * Cols + startC);

            while (stack.Count > 0)
            {
                int index = stack.Pop();
                int r = index / Cols;
                int c = index % Cols;

                if (r < 0 || r >= Rows || c < 0 || c >= Cols) continue;
                if (_revealed[r, c] || _flagged[r, c]) continue;
                if (_isMine[r, c]) continue;

                _revealed[r, c] = true;
                _revealedCount++;
                ShowCell(r, c);

                // 只有周围没有雷的空白格才继续向外扩散
                if (_adjacent[r, c] != 0) continue;

                for (int dr = -1; dr <= 1; dr++)
                {
                    for (int dc = -1; dc <= 1; dc++)
                    {
                        if (dr == 0 && dc == 0) continue;
                        int nr = r + dr;
                        int nc = c + dc;
                        if (nr < 0 || nr >= Rows || nc < 0 || nc >= Cols) continue;
                        if (_revealed[nr, nc] || _flagged[nr, nc]) continue;
                        stack.Push(nr * Cols + nc);
                    }
                }
            }
        }

        /// <summary>踩雷后显示所有雷，并标出错插的旗子。</summary>
        private void RevealAllMines(int hitR, int hitC)
        {
            for (int r = 0; r < Rows; r++)
            {
                for (int c = 0; c < Cols; c++)
                {
                    if (_isMine[r, c] && !_flagged[r, c])
                    {
                        _revealed[r, c] = true;
                        ShowCell(r, c);
                    }
                }
            }

            // 踩中的那颗雷用红色高亮（用状态位记录，鼠标移出重绘时才不会丢）
            if (hitR >= 0 && hitR < Rows && hitC >= 0 && hitC < Cols)
            {
                _exploded[hitR, hitC] = true;
                ShowCell(hitR, hitC);
            }

            // 标错的旗子画叉
            for (int r = 0; r < Rows; r++)
            {
                for (int c = 0; c < Cols; c++)
                {
                    if (!_flagged[r, c] || _isMine[r, c]) continue;
                    _wrongFlag[r, c] = true;
                    ShowCell(r, c);
                }
            }
        }

        private void CheckWin()
        {
            if (_gameOver) return;
            if (_revealedCount < Rows * Cols - MineTotal) return;

            _gameOver = true;
            StopTimer();

            // 胜利时自动把剩下的雷插上旗
            for (int r = 0; r < Rows; r++)
            {
                for (int c = 0; c < Cols; c++)
                {
                    if (_isMine[r, c] && !_flagged[r, c])
                    {
                        _flagged[r, c] = true;
                        _flagsUsed++;
                        ShowCell(r, c);
                    }
                }
            }

            UpdateMineText();
            ShowMessage("恭喜胜利", "全部排雷完成！\n\n用时: " + _elapsedSeconds + " 秒");
        }

        #endregion

        #region 显示刷新

        // === 格子图标改成矢量绘制（豆包 Q12-3）===
        // 原来用 emoji（💣 🚩 ❌）：Win7 没有对应的彩色 emoji 字体，会显示成方框（豆腐块）——
        // 与用户反馈的"Win7 表情看不到"同源。数字仍然用文本（数字不需要字体之外的码位）。
        // 注意：一个 UIElement 只能有一个父级，所以每次都新建实例，不能做静态缓存共享。
        private static UIElement CreateMineIcon()
        {
            var cv = new Canvas { Width = 18, Height = 18 };
            for (int i = 0; i < 8; i++)
            {
                cv.Children.Add(new Line
                {
                    X1 = 9, Y1 = 1.5, X2 = 9, Y2 = 16.5,
                    Stroke = Brushes.Black, StrokeThickness = 1.4,
                    RenderTransform = new RotateTransform(i * 22.5, 9, 9)
                });
            }
            var core = new Ellipse { Width = 10, Height = 10, Fill = Brushes.Black };
            Canvas.SetLeft(core, 4);
            Canvas.SetTop(core, 4);
            cv.Children.Add(core);
            return cv;
        }

        private static UIElement CreateFlagIcon()
        {
            var cv = new Canvas { Width = 18, Height = 18 };
            cv.Children.Add(new Line { X1 = 5, Y1 = 3, X2 = 5, Y2 = 15, Stroke = Brushes.Black, StrokeThickness = 1.8 });
            cv.Children.Add(new Line { X1 = 3, Y1 = 15, X2 = 12, Y2 = 15, Stroke = Brushes.Black, StrokeThickness = 1.8 });
            var tri = new Polygon { Fill = Brushes.Red };
            tri.Points.Add(new Point(6, 3));
            tri.Points.Add(new Point(14, 6));
            tri.Points.Add(new Point(6, 9));
            cv.Children.Add(tri);
            return cv;
        }

        private static UIElement CreateWrongIcon()
        {
            var cv = new Canvas { Width = 18, Height = 18 };
            cv.Children.Add(new Line { X1 = 4, Y1 = 4, X2 = 14, Y2 = 14, Stroke = Brushes.Firebrick, StrokeThickness = 2.4 });
            cv.Children.Add(new Line { X1 = 14, Y1 = 4, X2 = 4, Y2 = 14, Stroke = Brushes.Firebrick, StrokeThickness = 2.4 });
            return cv;
        }

        /// <summary>Border 只有一个 Child：图标与文本互斥，切换时替换 Child</summary>
        private static void SetCellContent(Border cell, TextBlock text, UIElement icon)
        {
            if (icon == null)
            {
                if (!ReferenceEquals(cell.Child, text)) cell.Child = text;
            }
            else if (!ReferenceEquals(cell.Child, icon))
            {
                cell.Child = icon;
            }
        }

        private void ShowCell(int r, int c)
        {
            Border cell = _cells[r, c];
            TextBlock text = _cellText[r, c];
            if (cell == null || text == null) return;

            // 踩中的那颗雷（优先级最高）
            if (_exploded[r, c])
            {
                cell.Background = ExplodedBrush;
                cell.BorderBrush = RevealedBorderBrush;
                cell.Cursor = Cursors.Arrow;
                text.Text = "";
                SetCellContent(cell, text, CreateMineIcon());
                return;
            }

            // 标错的旗子
            if (_wrongFlag[r, c])
            {
                cell.Background = WrongFlagBrush;
                cell.BorderBrush = RevealedBorderBrush;
                cell.Cursor = Cursors.Arrow;
                text.Text = "";
                SetCellContent(cell, text, CreateWrongIcon());
                return;
            }

            if (_revealed[r, c])
            {
                cell.Background = RevealedBrush;
                cell.BorderBrush = RevealedBorderBrush;
                cell.Cursor = Cursors.Arrow;

                if (_isMine[r, c])
                {
                    text.Text = "";
                    SetCellContent(cell, text, CreateMineIcon());
                }
                else if (_adjacent[r, c] > 0)
                {
                    SetCellContent(cell, text, null);
                    text.Text = _adjacent[r, c].ToString();
                    text.FontSize = 15;
                    text.Foreground = NumberBrushes[Math.Min(_adjacent[r, c], 8)];
                }
                else
                {
                    SetCellContent(cell, text, null);
                    text.Text = "";
                }
            }
            else
            {
                cell.Background = UnrevealedBrush;
                cell.BorderBrush = UnrevealedBorderBrush;
                cell.Cursor = Cursors.Hand;
                text.Text = "";
                SetCellContent(cell, text, _flagged[r, c] ? CreateFlagIcon() : null);
                text.FontSize = 13;
                text.Foreground = Brushes.Black;
            }
        }

        private void UpdateMineText()
        {
            try
            {
                // 按任务书原样显示“10 - 已标旗数”（旗子可以插超过 10 个，此时显示负数）
                int remaining = MineTotal - _flagsUsed;
                if (MineText != null) MineText.Text = "剩余: " + remaining;
            }
            catch (Exception ex) { LogError("UpdateMineText", ex); }
        }

        private void StopTimer()
        {
            try
            {
                if (_timer.IsEnabled) _timer.Stop();
            }
            catch (Exception ex) { LogError("StopTimer", ex); }
        }

        #endregion

        #region 按钮

        private void Restart_Click(object sender, RoutedEventArgs e)
        {
            ResetGame();
        }

        private void ResetGame()
        {
            try
            {
                StopTimer();

                _gameOver = false;
                _minesPlaced = false;
                _flagsUsed = 0;
                _revealedCount = 0;
                _elapsedSeconds = 0;

                for (int r = 0; r < Rows; r++)
                {
                    for (int c = 0; c < Cols; c++)
                    {
                        _isMine[r, c] = false;
                        _adjacent[r, c] = 0;
                        _revealed[r, c] = false;
                        _flagged[r, c] = false;
                        _exploded[r, c] = false;
                        _wrongFlag[r, c] = false;
                        ShowCell(r, c);
                    }
                }

                if (TimeText != null) TimeText.Text = "时间: 0s";
                UpdateMineText();
            }
            catch (Exception ex) { LogError("ResetGame", ex); }
        }

        private void Back_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                StopTimer();
                var parent = this.Parent as ContentControl ?? (Window.GetWindow(this) as MainWindow)?.GameContent;
                if (parent != null) parent.Content = new GameMenu();
            }
            catch (Exception ex) { LogError("Back_Click", ex); }
        }

        #endregion

        #region 工具

        private void ShowMessage(string title, string message)
        {
            try
            {
                Window owner = Window.GetWindow(this);
                if (owner != null && owner.IsLoaded)
                    MessageBox.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
                else
                    MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex) { LogError("ShowMessage", ex); }
        }

        private static void LogError(string where, Exception ex)
        {
            try
            {
                Services.Logger.Instance.Debug("扫雷异常 [" + where + "]: " + ex.Message);
            }
            catch
            {
                // 日志本身失败时静默，绝不让游戏崩溃
            }
        }

        #endregion
    }
}
