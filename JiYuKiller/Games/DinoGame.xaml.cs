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
    /// Chrome 恐龙跳：纯 C# + WPF 形状绘制，无图片、无 WebBrowser。
    /// 恐龙在左侧，仙人掌从右向左移动，空格或鼠标点击跳跃。
    /// </summary>
    public partial class DinoGame : UserControl
    {
        #region 常量与配色

        private const double CanvasWidth = 360;
        private const double CanvasHeight = 180;
        private const double GroundY = 160;         // 地面线
        private const double DinoLeft = 26;
        private const double DinoWidth = 46;
        private const double DinoHeight = 50;
        private const double Gravity = 0.58;
        private const double JumpVelocity = -10;   // 起跳初速度
        private const double BaseSpeed = 3.4;       // 初始移动速度(px/tick)
        private const double MaxSpeed = 7.5;
        private const double SpeedStep = 0.3;      // 每 100 分增加的速度

        private static readonly Brush DinoBrush = new SolidColorBrush(Color.FromRgb(0x53, 0x53, 0x53));
        private static readonly Brush CactusBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32));
        private static readonly Brush GroundBrush = new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E));

        private enum GameState { Ready, Playing, Over }

        #endregion

        #region 状态

        private sealed class Cactus
        {
            public Border Visual;
            public double X;
            public double Width;
            public double Height;
        }

        private readonly DispatcherTimer _timer = new DispatcherTimer();
        private readonly List<Cactus> _cacti = new List<Cactus>();
        private readonly Random _rng = new Random();

        private Canvas _dinoVisual;
        private Rectangle _legLeft;
        private Rectangle _legRight;
        private Border _overlay;
        private TextBlock _overlayTitle;
        private TextBlock _overlayHint;

        // 必须缓存宿主窗口：控件被移除后 Window.GetWindow(this) 返回 null，就摘不掉键盘钩子了
        private Window _ownerWindow;
        private GameState _state = GameState.Ready;
        private double _dinoY;              // 恐龙顶部 Y
        private double _velocity;           // 垂直速度
        private double _speed = BaseSpeed;  // 当前水平速度
        private double _spawnCountdown;     // 距离生成下一颗仙人掌
        private int _score;
        private int _tickCount;
        private bool _onGround = true;
        private bool _legToggle;

        #endregion

        public DinoGame()
        {
            InitializeComponent();

            _timer.Interval = TimeSpan.FromMilliseconds(16);
            _timer.Tick += Timer_Tick;

            Loaded += DinoGame_Loaded;
            Unloaded += DinoGame_Unloaded;

            BuildScene();
            ResetGame();
        }

        #region 生命周期

        private void DinoGame_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // 挂在窗口上监听空格，保证焦点在任何子控件上都能跳
                _ownerWindow = Window.GetWindow(this);
                if (_ownerWindow != null)
                {
                    _ownerWindow.PreviewKeyDown -= Window_PreviewKeyDown;
                    _ownerWindow.PreviewKeyDown += Window_PreviewKeyDown;
                }
                Focus();
            }
            catch (Exception ex) { LogError("Loaded", ex); }
        }

        private void DinoGame_Unloaded(object sender, RoutedEventArgs e)
        {
            // 离开页面必须停表并摘掉键盘钩子，否则定时器空跑、空格键被一直吞掉
            StopTimer();
            UnhookOwnerKey();
        }

        /// <summary>摘掉窗口级空格键钩子（幂等）。</summary>
        private void UnhookOwnerKey()
        {
            try
            {
                if (_ownerWindow == null) return;
                _ownerWindow.PreviewKeyDown -= Window_PreviewKeyDown;
                _ownerWindow = null;
            }
            catch (Exception ex) { LogError("UnhookOwnerKey", ex); }
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            try
            {
                if (e.Key != Key.Space) return;

                // 页面被隐藏或控件已卸载时不要吞空格，否则其他页面按不了空格
                if (!IsVisible || !IsLoaded) return;

                StartOrJump();
                e.Handled = true;
            }
            catch (Exception ex) { LogError("PreviewKeyDown", ex); }
        }

        #endregion

        #region 场景构建（全部用 WPF 形状，无图片）

        private void BuildScene()
        {
            try
            {
                GameCanvas.Children.Clear();
                _cacti.Clear();

                // 地面线
                var ground = new Border
                {
                    Width = CanvasWidth,
                    Height = 2,
                    Background = GroundBrush,
                    CornerRadius = new CornerRadius(1)
                };
                Canvas.SetLeft(ground, 0);
                Canvas.SetTop(ground, GroundY);
                Panel.SetZIndex(ground, 0);
                GameCanvas.Children.Add(ground);

                // 恐龙
                _dinoVisual = BuildDino();
                Canvas.SetLeft(_dinoVisual, DinoLeft);
                Canvas.SetTop(_dinoVisual, GroundY - DinoHeight);
                Panel.SetZIndex(_dinoVisual, 5);
                GameCanvas.Children.Add(_dinoVisual);

                // 开始 / 结束遮罩（始终在最上层）
                BuildOverlay();
                GameCanvas.Children.Add(_overlay);
            }
            catch (Exception ex) { LogError("BuildScene", ex); }
        }

        /// <summary>用圆角矩形 + 小矩形 + 圆点组合出恐龙。</summary>
        private Canvas BuildDino()
        {
            var canvas = new Canvas { Width = DinoWidth, Height = DinoHeight };

            // 两条腿：小矩形（先加入，位于身体下层）
            _legLeft = new Rectangle { Width = 6, Height = 10, RadiusX = 2, RadiusY = 2, Fill = DinoBrush };
            Canvas.SetLeft(_legLeft, 8);
            Canvas.SetTop(_legLeft, 40);

            _legRight = new Rectangle { Width = 6, Height = 10, RadiusX = 2, RadiusY = 2, Fill = DinoBrush };
            Canvas.SetLeft(_legRight, 20);
            Canvas.SetTop(_legRight, 40);

            // 身体：圆角矩形
            var body = new Rectangle { Width = 30, Height = 26, RadiusX = 7, RadiusY = 7, Fill = DinoBrush };
            Canvas.SetLeft(body, 2);
            Canvas.SetTop(body, 14);

            // 头：身体右上方的小矩形
            var head = new Rectangle { Width = 20, Height = 16, RadiusX = 5, RadiusY = 5, Fill = DinoBrush };
            Canvas.SetLeft(head, 24);
            Canvas.SetTop(head, 4);

            // 眼睛：白色小圆点
            var eye = new Ellipse { Width = 4, Height = 4, Fill = Brushes.White };
            Canvas.SetLeft(eye, 36);
            Canvas.SetTop(eye, 9);

            canvas.Children.Add(_legLeft);
            canvas.Children.Add(_legRight);
            canvas.Children.Add(body);
            canvas.Children.Add(head);
            canvas.Children.Add(eye);
            return canvas;
        }

        private void BuildOverlay()
        {
            _overlayTitle = new TextBlock
            {
                FontSize = 22,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center
            };

            _overlayHint = new TextBlock
            {
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)),
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 8, 0, 0)
            };

            var panel = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            panel.Children.Add(_overlayTitle);
            panel.Children.Add(_overlayHint);

            // 半透明黑色遮罩
            _overlay = new Border
            {
                Width = CanvasWidth,
                Height = CanvasHeight,
                Background = new SolidColorBrush(Color.FromArgb(0xB0, 0x00, 0x00, 0x00)),
                Child = panel
            };
            Canvas.SetLeft(_overlay, 0);
            Canvas.SetTop(_overlay, 0);
            Panel.SetZIndex(_overlay, 100);
        }

        /// <summary>显示遮罩。开始界面用浅遮罩+深色字（还能看清恐龙），结束界面用深遮罩+白字。</summary>
        private void ShowOverlay(string title, string hint, byte maskAlpha, bool darkText)
        {
            if (_overlay == null) return;

            if (_overlayTitle != null)
            {
                _overlayTitle.Text = title ?? "";
                _overlayTitle.Foreground = darkText
                    ? new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33))
                    : Brushes.White;
            }

            if (_overlayHint != null)
            {
                _overlayHint.Text = hint ?? "";
                _overlayHint.Foreground = darkText
                    ? new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55))
                    : new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD));
            }

            _overlay.Background = new SolidColorBrush(Color.FromArgb(maskAlpha, 0x00, 0x00, 0x00));
            _overlay.Visibility = Visibility.Visible;
        }

        private void HideOverlay()
        {
            if (_overlay != null) _overlay.Visibility = Visibility.Collapsed;
        }

        #endregion

        #region 游戏循环

        private void Timer_Tick(object sender, EventArgs e)
        {
            try
            {
                if (_state != GameState.Playing) return;

                _tickCount++;
                UpdateDino();
                UpdateCacti();
                UpdateScore();
                CheckCollision();
            }
            catch (Exception ex) { LogError("Timer_Tick", ex); }
        }

        private void UpdateDino()
        {
            // 重力系统
            _velocity += Gravity;
            _dinoY += _velocity;

            double groundTop = GroundY - DinoHeight;
            if (_dinoY >= groundTop)
            {
                _dinoY = groundTop;
                _velocity = 0;
                _onGround = true;
            }

            if (_dinoVisual != null) Canvas.SetTop(_dinoVisual, _dinoY);
            AnimateLegs();
        }

        private void AnimateLegs()
        {
            if (_legLeft == null || _legRight == null) return;

            // 腾空时两条腿并齐，落地时交替抬起模拟跑步
            if (!_onGround)
            {
                Canvas.SetTop(_legLeft, 40);
                Canvas.SetTop(_legRight, 40);
                return;
            }

            if (_tickCount % 8 == 0) _legToggle = !_legToggle;
            Canvas.SetTop(_legLeft, _legToggle ? 40 : 37);
            Canvas.SetTop(_legRight, _legToggle ? 37 : 40);
        }

        private void UpdateCacti()
        {
            // 移动 + 回收出界的仙人掌
            for (int i = _cacti.Count - 1; i >= 0; i--)
            {
                Cactus cactus = _cacti[i];
                cactus.X -= _speed;
                if (cactus.Visual != null) Canvas.SetLeft(cactus.Visual, cactus.X);

                if (cactus.X + cactus.Width < -10)
                {
                    if (cactus.Visual != null) GameCanvas.Children.Remove(cactus.Visual);
                    _cacti.RemoveAt(i);
                }
            }

            // 生成新的仙人掌：间隔按“时间”而不是“像素”控制，
            // 保证任何速度下两颗仙人掌之间的空档都够落地再起跳（一次跳跃约 28 tick）
            _spawnCountdown -= _speed;
            if (_spawnCountdown <= 0)
            {
                _cacti.Add(CreateCactus());
                _spawnCountdown = _speed * (30 + _rng.Next(0, 14)) + 20;
            }
        }

        private Cactus CreateCactus()
        {
            double height = 20 + _rng.Next(0, 26);  // 20 - 45
            double width = 14 + _rng.Next(0, 9);    // 14 - 22

            var rect = new Border
            {
                Width = width,
                Height = height,
                Background = CactusBrush,
                CornerRadius = new CornerRadius(4)
            };

            double startX = CanvasWidth + width;
            Canvas.SetLeft(rect, startX);
            Canvas.SetTop(rect, GroundY - height);
            Panel.SetZIndex(rect, 1);
            GameCanvas.Children.Add(rect);

            return new Cactus { Visual = rect, X = startX, Width = width, Height = height };
        }

        private void UpdateScore()
        {
            if (_tickCount % 6 != 0) return;   // 约每 100ms 加 1 分

            _score++;
            if (ScoreText != null) ScoreText.Text = "分数: " + _score;

            // 每 100 分速度加快
            int level = _score / 100;
            double target = BaseSpeed + level * SpeedStep;
            if (target > MaxSpeed) target = MaxSpeed;

            if (target > _speed)
            {
                _speed = target;
                UpdateSpeedText();
            }
        }

        private void UpdateSpeedText()
        {
            try
            {
                if (SpeedText != null)
                    SpeedText.Text = "速度: " + (_speed / BaseSpeed).ToString("0.0") + "x";
            }
            catch (Exception ex) { LogError("UpdateSpeedText", ex); }
        }

        /// <summary>碰撞检测：两侧都留内边距，判定宽松一些。</summary>
        private void CheckCollision()
        {
            // 恐龙碰撞盒（左右各内缩 9，上方内缩 8，下方内缩 2）
            double dinoLeft = DinoLeft + 9;
            double dinoRight = DinoLeft + DinoWidth - 9;
            double dinoTop = _dinoY + 8;
            double dinoBottom = _dinoY + DinoHeight - 2;

            for (int i = 0; i < _cacti.Count; i++)
            {
                Cactus cactus = _cacti[i];

                // 仙人掌碰撞盒（四周内缩 4）
                double cLeft = cactus.X + 4;
                double cRight = cactus.X + cactus.Width - 4;
                double cTop = GroundY - cactus.Height + 4;
                double cBottom = GroundY;

                if (dinoRight > cLeft && dinoLeft < cRight && dinoBottom > cTop && dinoTop < cBottom)
                {
                    GameOver();
                    return;
                }
            }
        }

        #endregion

        #region 状态切换

        private void StartOrJump()
        {
            try
            {
                // 开始界面 / 结束界面 -> 开局
                if (_state != GameState.Playing)
                {
                    StartGame();
                    return;
                }

                // 游戏中：只有落地时才能起跳（避免空中二段跳）
                if (_onGround)
                {
                    _velocity = JumpVelocity;
                    _onGround = false;
                }
            }
            catch (Exception ex) { LogError("StartOrJump", ex); }
        }

        private void StartGame()
        {
            try
            {
                ClearCacti();

                _state = GameState.Playing;
                _score = 0;
                _tickCount = 0;
                _speed = BaseSpeed;
                _velocity = 0;
                _dinoY = GroundY - DinoHeight;
                _onGround = true;
                _legToggle = false;
                _spawnCountdown = 150;

                if (_dinoVisual != null) Canvas.SetTop(_dinoVisual, _dinoY);
                if (_legLeft != null) Canvas.SetTop(_legLeft, 40);
                if (_legRight != null) Canvas.SetTop(_legRight, 40);
                if (ScoreText != null) ScoreText.Text = "分数: 0";
                UpdateSpeedText();

                HideOverlay();
                StartTimer();
            }
            catch (Exception ex) { LogError("StartGame", ex); }
        }

        private void GameOver()
        {
            try
            {
                _state = GameState.Over;
                StopTimer();

                ShowOverlay("GAME OVER", "得分: " + _score + "\n点击或按空格重新开始", 0xB0, false);
                ShowMessage("游戏结束", "GAME OVER\n\n最终得分: " + _score);
            }
            catch (Exception ex) { LogError("GameOver", ex); }
        }

        private void ResetGame()
        {
            try
            {
                StopTimer();
                ClearCacti();

                _state = GameState.Ready;
                _score = 0;
                _tickCount = 0;
                _speed = BaseSpeed;
                _velocity = 0;
                _dinoY = GroundY - DinoHeight;
                _onGround = true;
                _legToggle = false;
                _spawnCountdown = 150;

                if (_dinoVisual != null) Canvas.SetTop(_dinoVisual, _dinoY);
                if (_legLeft != null) Canvas.SetTop(_legLeft, 40);
                if (_legRight != null) Canvas.SetTop(_legRight, 40);
                if (ScoreText != null) ScoreText.Text = "分数: 0";
                UpdateSpeedText();

                ShowOverlay("", "点击或按空格开始", 0x3C, true);
            }
            catch (Exception ex) { LogError("ResetGame", ex); }
        }

        private void ClearCacti()
        {
            try
            {
                for (int i = 0; i < _cacti.Count; i++)
                {
                    Cactus cactus = _cacti[i];
                    if (cactus.Visual != null) GameCanvas.Children.Remove(cactus.Visual);
                }
                _cacti.Clear();
            }
            catch (Exception ex) { LogError("ClearCacti", ex); }
        }

        private void StartTimer()
        {
            try
            {
                if (!_timer.IsEnabled) _timer.Start();
            }
            catch (Exception ex) { LogError("StartTimer", ex); }
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

        #region 输入与按钮

        private void GameCanvas_Click(object sender, MouseButtonEventArgs e)
        {
            try
            {
                Focus();
                StartOrJump();
            }
            catch (Exception ex) { LogError("GameCanvas_Click", ex); }
        }

        private void Restart_Click(object sender, RoutedEventArgs e)
        {
            ResetGame();
        }

        private void Back_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                StopTimer();
                UnhookOwnerKey();

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
                Services.Logger.Instance.Debug("恐龙跳异常 [" + where + "]: " + ex.Message);
            }
            catch
            {
                // 日志本身失败时静默，绝不让游戏崩溃
            }
        }

        #endregion
    }
}
