using System;
using System.Threading.Tasks;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace JiYuKiller
{
    public partial class MainWindow
    {
        #region 页面导航

        private void NavSetting_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("设置", "NavSetting");
            ShowPage("settings");
        }

        private void NavDebug_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("调试", "NavDebug");
            ShowPage("debug");
            RefreshLog();
        }

        private void NavAbout_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("关于", "NavAbout");
            ShowPage("about");
        }

        private void NavChat_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("小小私聊", "NavChat");
            ShowPage("chat");
        }

        private void NavUdpGhost_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("UdpGhost", "NavUdpGhost");
            ShowPage("udpghost");
        }

        private void NavScreenshot_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("截图替换", "NavScreenshot");
            ShowPage("screenshot");
        }

        private void NavScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            // 将鼠标滚轮垂直滚动转换为横向滚动
            if (NavScrollViewer != null && e.Delta != 0)
            {
                double newOffset = NavScrollViewer.HorizontalOffset - e.Delta;
                NavScrollViewer.ScrollToHorizontalOffset(newOffset);
                e.Handled = true;
            }
        }

        private void AnimateNavIndicator(Button targetBtn)
        {
            if (targetBtn == null || NavIndicator == null || NavIndicatorTransform == null || NavContentGrid == null) return;
            try
            {
                // 指示器和按钮同在 NavContentGrid 内，相对于 NavContentGrid 计算位置
                Point relativePoint = targetBtn.TranslatePoint(new Point(0, 0), NavContentGrid);
                double targetX = relativePoint.X;
                double targetWidth = targetBtn.ActualWidth;
                if (targetWidth <= 0) targetWidth = targetBtn.MinWidth;
                NavIndicator.Width = targetWidth;
                Services.Logger.Instance.Debug("指示器动画: 目标X=" + targetX.ToString("F1") + ", 宽度=" + targetWidth.ToString("F1") + ", 按钮=" + targetBtn.Content);
                DoubleAnimation anim = new DoubleAnimation { To = targetX, Duration = TimeSpan.FromMilliseconds(350), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } };
                NavIndicatorTransform.BeginAnimation(TranslateTransform.XProperty, anim);
                // 滚动到可见区域
                if (NavScrollViewer != null)
                {
                    double scrollTo = targetX - 20;
                    if (scrollTo < 0) scrollTo = 0;
                    NavScrollViewer.ScrollToHorizontalOffset(scrollTo);
                }
            }
            catch (Exception ex)
            {
                Services.Logger.Instance.Warn("导航指示器动画失败: " + ex.Message);
            }
        }

        private void NavCustom_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("自定义设置", "NavCustom");
            ShowPage("custom");
        }

        private void NavUdpAttack_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("UDP攻击", "NavUdpAttack");
            ShowPage("udpattack");
        }

        #region 自定义设置（毛玻璃效果）

        private void SliderBlurIntensity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TextBlurValue != null)
            {
                TextBlurValue.Text = string.Format("{0}%", (int)(e.NewValue * 100));
            }

            if (_glassyManager != null)
            {
                _glassyManager.BlurIntensity = e.NewValue;
                Services.Logger.Instance.Debug($"毛玻璃模糊强度调整: {e.NewValue:F2}");
            }
        }

        // ===== 底栏液态玻璃设置 =====

        /// <summary>强度滑块：实时刷新折射量</summary>
        private void SliderNavLensStrength_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TextNavLensValue != null)
            {
                TextNavLensValue.Text = string.Format("{0}%", (int)(e.NewValue * 100));
            }

            if (_settings == null) return;
            _settings.NavBarLiquidGlassStrength = e.NewValue;

            // 拖滑块时不写盘（避免频繁 IO），松手后由保存按钮统一落盘
            UpdateNavBarLensParams();
            UpdateNavLensStatusText();
            Services.Logger.Instance.Debug(string.Format("液态玻璃强度调整: {0:F2}", e.NewValue));
        }

        /// <summary>顺滑度滑块：实时重建圆角几何</summary>
        private void SliderSquircleExt_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TextSquircleExtValue != null)
            {
                TextSquircleExtValue.Text = e.NewValue.ToString("F2");
            }
            if (_settings == null) return;

            _settings.NavBarSquircleExtension = e.NewValue;
            UpdateNavBarClip();
            Services.Logger.Instance.Debug(string.Format("圆角顺滑度调整: {0:F2}", e.NewValue));
        }

        /// <summary>开关 / Tier0 强制开关：重新挂载或摘除折射层</summary>
        private void NavLensSetting_Changed(object sender, RoutedEventArgs e)
        {
            if (_settings == null || _isInitializing) return;

            _settings.NavBarLiquidGlass = CheckNavBarLiquidGlass.IsChecked ?? false;
            _settings.NavBarLiquidGlassForceOnTier0 = CheckNavLensOnTier0.IsChecked ?? false;
            _settings.NavBarSquircle = CheckNavSquircle != null ? (CheckNavSquircle.IsChecked ?? true) : true;

            UpdateNavBarClip();   // 圆角风格变化要立刻重建几何（裁剪 + 描边共用同一套）
            ApplyNavBarLens(_settings.NavBarLiquidGlass, "用户设置");
            UpdateNavButtonBackdrops();   // 折射开关状态变了，按钮磨砂要跟着挂/摘
            UpdateNavLensStatusText();
            Services.Logger.Instance.Info(string.Format(
                "液态玻璃设置变更: 启用={0}, Tier0强制={1}, 强度={2:F2}, 超椭圆={3}",
                _settings.NavBarLiquidGlass, _settings.NavBarLiquidGlassForceOnTier0,
                _settings.NavBarLiquidGlassStrength, _settings.NavBarSquircle));
        }

        /// <summary>把当前生效状态写到界面上，避免"点了开关却不知道为什么没效果"</summary>
        private void UpdateNavLensStatusText()
        {
            if (TextNavLensStatus == null || _settings == null) return;
            try
            {
                if (!_settings.NavBarLiquidGlass)
                {
                    TextNavLensStatus.Text = "当前状态：已关闭（底栏为普通磨砂玻璃）";
                    return;
                }
                if (navLensTier == 0 && !System.Windows.Media.RenderCapability.IsPixelShaderVersionSupportedInSoftware(2, 0))
                {
                    // 这种情况勾"强制启用"也没用（会被 InitNavBarGlass 的闸门挡掉），所以不要提示用户去勾
                    TextNavLensStatus.Text = "当前状态：未生效 —— 本机是软件渲染(Tier 0)且系统不支持软件着色器，折射无法开启（已自动关闭）。";
                    return;
                }
                if (navLensTier == 0 && !_settings.NavBarLiquidGlassForceOnTier0)
                {
                    TextNavLensStatus.Text = "当前状态：未生效 —— 本机是软件渲染(Tier 0)，默认关闭以避免卡顿。勾选上面的强制启用即可打开。";
                    return;
                }
                if (!navLensActive)
                {
                    TextNavLensStatus.Text = "当前状态：未生效（着色器未加载成功，请查看日志）";
                    return;
                }
                TextNavLensStatus.Text = string.Format(
                    "当前状态：已启用（Tier {0}，折射量 {1:F1}px）",
                    navLensTier, NavBarLensFx != null ? NavBarLensFx.RefractStrength : 0.0);
            }
            catch (Exception ex)
            {
                TextNavLensStatus.Text = "状态获取失败: " + ex.Message;
            }
        }

        // ===== 底栏液态动态效果：指针跟随柔光 + 点击波纹（纯 WPF，无着色器开销）=====

        /// <summary>指针在底栏上移动：柔光团带惯性跟随鼠标（线性插值，不建动画时钟）</summary>
        private void NavBarClipRoot_MouseMove(object sender, MouseEventArgs e)
        {
            if (NavBarSpecularTransform == null || NavBarFxLayer == null) return;
            try
            {
                Point p = e.GetPosition(NavBarFxLayer);

                // 垂直方向只跟 35% 并整体上偏 6px：真实光源来自上方，柔光该落在玻璃上半部，
                // 而不是跟着鼠标在 64px 高的底栏里上下晃（那看起来像探照灯而不是反光）。
                double barH = NavBarFxLayer.ActualHeight;
                double targetY = 6.0 + (p.Y - barH * 0.5) * 0.35;

                // 首次移动直接就位，避免从 (0,0) 滑过来
                if (!_navSpecularInit)
                {
                    _navSpecularX = p.X;
                    _navSpecularY = targetY;
                    _navSpecularInit = true;
                }

                _navSpecularX += (p.X - _navSpecularX) * NavSpecularLag;
                _navSpecularY += (targetY - _navSpecularY) * NavSpecularLag;

                NavBarSpecularTransform.X = _navSpecularX;
                NavBarSpecularTransform.Y = _navSpecularY;
            }
            catch (Exception ex)
            {
                Services.Logger.Instance.Debug("[NavBar] 柔光跟随失败: " + ex.Message);
            }
        }

        private void NavBarClipRoot_MouseEnter(object sender, MouseEventArgs e)
        {
            FadeNavBarSpecular(1.0);
        }

        private void NavBarClipRoot_MouseLeave(object sender, MouseEventArgs e)
        {
            // 复位惯性状态：再次进入时柔光直接在指针处出现，而不是从上次离开的位置滑过来
            _navSpecularInit = false;
            FadeNavBarSpecular(0.0);
        }

        private void FadeNavBarSpecular(double to)
        {
            if (NavBarSpecular == null) return;
            try
            {
                var anim = new System.Windows.Media.Animation.DoubleAnimation(
                    to, TimeSpan.FromMilliseconds(180))
                {
                    EasingFunction = new System.Windows.Media.Animation.CubicEase
                    {
                        EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
                    }
                };
                NavBarSpecular.BeginAnimation(UIElement.OpacityProperty, anim);
            }
            catch (Exception ex)
            {
                Services.Logger.Instance.Debug("[NavBar] 柔光淡入淡出失败: " + ex.Message);
            }
        }

        /// <summary>底栏上按下左键：从按压点扩散一圈波纹，播完自动移除</summary>
        private void NavBarClipRoot_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (NavBarFxLayer == null) return;
            try
            {
                Point p = e.GetPosition(NavBarFxLayer);

                // 波纹几何：直径从 12px 扩散到 76px。
                // 注意不要用 ScaleTransform 做这件事 —— 缩放会连 StrokeThickness 一起放大，
                // 2px 描边在终点会变成约 8.4px，环越扩越"肥"，看起来是一团糊光而不是水波。
                // 改成动画 Width/Height + Canvas.Left/Top，描边恒定 2px；终点 76px 也避免在
                // 64px 高的底栏里被裁得太狠。
                const double startDiameter = 12;
                const double endDiameter = 76;
                var dur = TimeSpan.FromMilliseconds(430);

                var ripple = new System.Windows.Shapes.Ellipse
                {
                    Width = startDiameter,
                    Height = startDiameter,
                    StrokeThickness = 2,
                    Stroke = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)),
                    Fill = new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)),
                    IsHitTestVisible = false
                };

                double left0 = p.X - startDiameter / 2, top0 = p.Y - startDiameter / 2;
                double left1 = p.X - endDiameter / 2, top1 = p.Y - endDiameter / 2;
                Canvas.SetLeft(ripple, left0);
                Canvas.SetTop(ripple, top0);

                // 防止狂点导致子元素无限增长（只保留柔光 + 最近 10 个波纹）
                while (NavBarFxLayer.Children.Count > 11)
                {
                    NavBarFxLayer.Children.RemoveAt(1);
                }
                NavBarFxLayer.Children.Add(ripple);

                var ease = new System.Windows.Media.Animation.CubicEase
                {
                    EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
                };
                var fade = new System.Windows.Media.Animation.DoubleAnimation(0.55, 0.0, dur) { EasingFunction = ease };
                fade.Completed += (s, a) =>
                {
                    try { NavBarFxLayer.Children.Remove(ripple); } catch { }
                };
                // 直接对元素 BeginAnimation：Canvas.Left/Top 虽是附加属性，但依然是 DP，可以挂动画
                // （WPF 的 Timeline 上没有 TargetProperty —— 那是 Silverlight 的写法，编译不过）
                var dW = new System.Windows.Media.Animation.DoubleAnimation(startDiameter, endDiameter, dur) { EasingFunction = ease };
                var dH = new System.Windows.Media.Animation.DoubleAnimation(startDiameter, endDiameter, dur) { EasingFunction = ease };
                var dL = new System.Windows.Media.Animation.DoubleAnimation(left0, left1, dur) { EasingFunction = ease };
                var dT = new System.Windows.Media.Animation.DoubleAnimation(top0, top1, dur) { EasingFunction = ease };
                ripple.BeginAnimation(FrameworkElement.WidthProperty, dW);
                ripple.BeginAnimation(FrameworkElement.HeightProperty, dH);
                ripple.BeginAnimation(Canvas.LeftProperty, dL);
                ripple.BeginAnimation(Canvas.TopProperty, dT);
                ripple.BeginAnimation(UIElement.OpacityProperty, fade);
            }
            catch (Exception ex)
            {
                Services.Logger.Instance.Debug("[NavBar] 点击波纹失败: " + ex.Message);
            }
        }

        private void SliderContentOpacity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)        {
            if (TextContentOpacityValue != null)
            {
                TextContentOpacityValue.Text = string.Format("{0}%", (int)(e.NewValue * 100));
            }

            // 正向逻辑：值越大，白色层和壁纸层越透明，桌面越可见
            byte whiteAlpha = (byte)((1 - e.NewValue) * 255);
            double wallpaperOpacity = 1 - e.NewValue;

            if (WhiteOverlayLayer != null)
            {
                SolidColorBrush whiteBrush = WhiteOverlayLayer.Background as SolidColorBrush;
                if (whiteBrush != null)
                {
                    Color c = whiteBrush.Color;
                    c.A = whiteAlpha;
                    whiteBrush.Color = c;
                }
            }

            if (WallpaperLayer != null)
            {
                WallpaperLayer.Opacity = wallpaperOpacity;
            }

            if (_settings != null)
            {
                _settings.GlassOpacity = (int)(e.NewValue * 100);
            }

            Services.Logger.Instance.Debug($"内容层透明度: {e.NewValue:F2}, whiteAlpha={whiteAlpha}, wallpaperOpacity={wallpaperOpacity:F2}");
            Services.CrashReportService.UpdateWallpaperInfo(_settings?.WallpaperPath ?? "", !string.IsNullOrEmpty(_settings?.WallpaperPath), wallpaperOpacity, whiteAlpha, (int)(e.NewValue * 100));
        }

        private void BtnRefreshGlass_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("刷新玻璃背景", "BtnRefreshGlass");
            if (_glassyManager != null)
            {
                _glassyManager.RefreshBackdrop();
                Services.Logger.Instance.Info("毛玻璃背景已刷新");
            }
            else
            {
                Services.Logger.Instance.Warn("毛玻璃管理器未初始化");
            }
        }

        #endregion

        private void BtnAboutMe_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("关于我", "BtnAboutMe");
            ShowPage("aboutme");
        }

        private void BtnBackFromAboutMe_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("返回(关于我)", "BtnBackFromAboutMe");
            ShowPage("about");
        }

        private void BtnGithub_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("GitHub仓库", "BtnGithub");
            try
            {
                System.Diagnostics.Process.Start("https://github.com/DaiWicked/Jiyuikiller2.0-Comprehensive-Restructuring");
                Services.Logger.Instance.Info("已打开 GitHub 仓库链接");
            }
            catch (Exception ex)
            {
                Services.Logger.Instance.Error("打开 GitHub 链接失败", ex);
                System.Windows.MessageBox.Show("打开浏览器失败，请手动访问:\nhttps://github.com/DaiWicked/Jiyuikiller2.0-Comprehensive-Restructuring", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #region 软件高级设置

        private void BtnAdvancedSettings_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("软件高级设置", "BtnAdvancedSettings");
            LoadAdvancedSettingsToUI();
            ShowPage("advanced");
        }

        private void BtnBackFromAdvanced_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("返回(高级设置)", "BtnBackFromAdvanced");
            ShowPage("settings");
        }

        private void LoadAdvancedSettingsToUI()
        {
            Services.Logger.Instance.FunctionCall("LoadAdvancedSettingsToUI");

            CheckDisableDriver.IsChecked = _settings.DisableDriver;
            CheckSelfProtect.IsChecked = _settings.SelfProtect;
            CheckAutoForceKill.IsChecked = _settings.AutoForceKill;
            CheckAutoIncludeFullWindow.IsChecked = _settings.AutoIncludeFullWindow;
            CheckDoNotShowVirusWindow.IsChecked = _settings.DoNotShowVirusWindow;
            CheckDoNotShowTrayIcon.IsChecked = _settings.DoNotShowTrayIcon;
            CheckForceInstallInCurrentDir.IsChecked = _settings.ForceInstallInCurrentDir;
            CheckForceDisableWatchDog.IsChecked = _settings.ForceDisableWatchDog;

            CheckEnableController.IsChecked = _settings.EnableController;
            TextCKInterval.Text = _settings.CKInterval.ToString();

            // 结束进程模式
            switch (_settings.KillProcessMode)
            {
                case "TerminateProcess": RadioKillTP.IsChecked = true; break;
                case "NtTerminateProcess": RadioKillNTP.IsChecked = true; break;
                case "KernelMode": RadioKillKernel.IsChecked = true; break;
            }


            Services.Logger.Instance.Debug("高级设置已加载到 UI");
        }

        private void BtnSaveAdvancedSettings_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("保存高级设置", "BtnSaveAdvancedSettings");

            _settings.DisableDriver = CheckDisableDriver.IsChecked ?? false;
            _settings.SelfProtect = CheckSelfProtect.IsChecked ?? true;
            _settings.AutoForceKill = CheckAutoForceKill.IsChecked ?? false;
            _settings.AutoIncludeFullWindow = CheckAutoIncludeFullWindow.IsChecked ?? false;
            _settings.DoNotShowVirusWindow = CheckDoNotShowVirusWindow.IsChecked ?? true;
            _settings.DoNotShowTrayIcon = CheckDoNotShowTrayIcon.IsChecked ?? false;
            _settings.ForceInstallInCurrentDir = CheckForceInstallInCurrentDir.IsChecked ?? false;
            // D3: 立刻同步到释放服务（目录缓存会失效，下次释放按新策略走）；驱动/DLL 已释放的需重启才搬家
            Services.EmbeddedResourceService.ForceInstallInCurrentDir = _settings.ForceInstallInCurrentDir;
            Services.Logger.Instance.Info("[高级设置] 释放目录策略: " + (_settings.ForceInstallInCurrentDir ? "当前目录(exe 所在目录)" : "用户目录 %LOCALAPPDATA%"));
            _settings.ForceDisableWatchDog = CheckForceDisableWatchDog.IsChecked ?? false;

            _settings.EnableController = CheckEnableController.IsChecked ?? true;

            // 结束进程模式
            if (RadioKillTP.IsChecked == true) _settings.KillProcessMode = "TerminateProcess";
            else if (RadioKillNTP.IsChecked == true) _settings.KillProcessMode = "NtTerminateProcess";
            else if (RadioKillKernel.IsChecked == true) _settings.KillProcessMode = "KernelMode";


            // 检查间隔
            int ckInterval;
            if (int.TryParse(TextCKInterval.Text, out ckInterval))
            {
                if (ckInterval < 1000) ckInterval = 1000;
                if (ckInterval > 10000) ckInterval = 10000;
                _settings.CKInterval = ckInterval;
            }
            else
            {
                _settings.CKInterval = 3100;
            }

            _settings.Save();
            _controller.UpdateSettings(_settings);

            Services.Logger.Instance.Info($"高级设置已保存: CKInterval={_settings.CKInterval}, KillProcess={_settings.KillProcessMode}, ");
            System.Windows.MessageBox.Show("高级设置已保存！\n部分设置需要重启软件生效。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void CheckDoNotShowTrayIcon_Checked(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.CheckboxChanged("隐藏任务栏图标", true, "CheckDoNotShowTrayIcon");
            if (_trayIcon != null) _trayIcon.Visible = false;
        }

        private void CheckDoNotShowTrayIcon_Unchecked(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.CheckboxChanged("隐藏任务栏图标", false, "CheckDoNotShowTrayIcon");
            if (_trayIcon != null) _trayIcon.Visible = true;
        }

        private void CheckEnableController_Checked(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.CheckboxChanged("启用控制器", true, "CheckEnableController");
            _controller.Start();
            // 教师端模拟服务已在构造函数中初始化, 此处不重复创建
        }

        private void CheckEnableController_Unchecked(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.CheckboxChanged("启用控制器", false, "CheckEnableController");
            _controller.Stop();
        }

        #endregion

        private void ShowPage(string pageName)
        {
            PageQuick.Visibility = Visibility.Collapsed;
            PageSettings.Visibility = Visibility.Collapsed;
            PageAbout.Visibility = Visibility.Collapsed;
            PageAboutMe.Visibility = Visibility.Collapsed;
            PageAdvancedSettings.Visibility = Visibility.Collapsed;
            PageHelp.Visibility = Visibility.Collapsed;
            PageDebug.Visibility = Visibility.Collapsed;
            PageCustom.Visibility = Visibility.Collapsed;
            PageUdpAttack.Visibility = Visibility.Collapsed;
            PageChat.Visibility = Visibility.Collapsed;
            PageUdpGhost.Visibility = Visibility.Collapsed;
            PageScreenshot.Visibility = Visibility.Collapsed;
            PageTeacherSim.Visibility = Visibility.Collapsed;
            PageGames.Visibility = Visibility.Collapsed;

            // 离开小游戏页面时必须清空内容：Visibility.Collapsed 不会触发 Unloaded，
            // 否则游戏定时器会继续空跑（甚至弹窗到别的页面），挂在窗口上的空格键钩子也会一直吞按键
            if (GameContent != null && GameContent.Content != null) GameContent.Content = null;

            // 重置导航按钮样式
            NavQuick.FontWeight = FontWeights.Normal;
            NavSetting.FontWeight = FontWeights.Normal;
            NavCustom.FontWeight = FontWeights.Normal;
            NavHelp.FontWeight = FontWeights.Normal;
            NavDebug.FontWeight = FontWeights.Normal;
            NavAbout.FontWeight = FontWeights.Normal;
            NavUdpAttack.FontWeight = FontWeights.Normal;
            NavChat.FontWeight = FontWeights.Normal;
            if (NavUdpGhost != null) NavUdpGhost.FontWeight = FontWeights.Normal;
            NavScreenshot.FontWeight = FontWeights.Normal;
            NavTeacherSim.FontWeight = FontWeights.Normal;
            NavGames.FontWeight = FontWeights.Normal;

            switch (pageName)
            {
                case "quick":
                    PageQuick.Visibility = Visibility.Visible;
                    NavQuick.FontWeight = FontWeights.Bold;
                    break;
                case "settings":
                    PageSettings.Visibility = Visibility.Visible;
                    NavSetting.FontWeight = FontWeights.Bold;
                    break;
                case "about":
                    PageAbout.Visibility = Visibility.Visible;
                    NavAbout.FontWeight = FontWeights.Bold;
                    break;
                case "aboutme":
                    PageAboutMe.Visibility = Visibility.Visible;
                    break;
                case "advanced":
                    PageAdvancedSettings.Visibility = Visibility.Visible;
                    break;
                case "help":
                    PageHelp.Visibility = Visibility.Visible;
                    NavHelp.FontWeight = FontWeights.Bold;
                    break;
                case "debug":
                    PageDebug.Visibility = Visibility.Visible;
                    NavDebug.FontWeight = FontWeights.Bold;
                    break;
                case "custom":
                    PageCustom.Visibility = Visibility.Visible;
                    NavCustom.FontWeight = FontWeights.Bold;
                    break;
                case "udpattack":
                    PageUdpAttack.Visibility = Visibility.Visible;
                    NavUdpAttack.FontWeight = FontWeights.Bold;
                    UpdateUdpLocalInfo();
                    RegisterUdpLog();
                    break;
                case "chat":
                    PageChat.Visibility = Visibility.Visible;
                    NavChat.FontWeight = FontWeights.Bold;
                    InitChat();
                    break;
                case "udpghost":
                    PageUdpGhost.Visibility = Visibility.Visible;
                    if (NavUdpGhost != null) NavUdpGhost.FontWeight = FontWeights.Bold;
                    InitUdpGhost();
                    break;
                case "screenshot":
                    PageScreenshot.Visibility = Visibility.Visible;
                    NavScreenshot.FontWeight = FontWeights.Bold;
                    InitScreenshot();
                    break;
                case "teachersim":
                    PageTeacherSim.Visibility = Visibility.Visible;
                    NavTeacherSim.FontWeight = FontWeights.Bold;
                    InitTeacherSim();
                    break;
                case "games":
                    PageGames.Visibility = Visibility.Visible;
                    NavGames.FontWeight = FontWeights.Bold;
                    ResetGameContent();
                    break;
            }

            // 页面方向过渡（借鉴COUI NavTransition: 左右滑入+淡入, 根据导航顺序决定方向）
            FrameworkElement targetPage = null;
            int currentIndex = 0;
            switch (pageName)
            {
                case "quick": targetPage = PageQuick; currentIndex = 0; break;
                case "settings": targetPage = PageSettings; currentIndex = 1; break;
                case "custom": targetPage = PageCustom; currentIndex = 2; break;
                case "udpattack": targetPage = PageUdpAttack; currentIndex = 3; break;
                case "chat": targetPage = PageChat; currentIndex = 4; break;
                case "udpghost": targetPage = PageUdpGhost; currentIndex = 4; break;
                case "screenshot": targetPage = PageScreenshot; currentIndex = 5; break;
                case "teachersim": targetPage = PageTeacherSim; currentIndex = 6; break;
                case "games": targetPage = PageGames; currentIndex = 7; break;
                case "help": targetPage = PageHelp; currentIndex = 8; break;
                case "debug": targetPage = PageDebug; currentIndex = 9; break;
                case "about": targetPage = PageAbout; currentIndex = 10; break;
                case "aboutme": targetPage = PageAboutMe; currentIndex = 10; break;
                case "advanced": targetPage = PageAdvancedSettings; currentIndex = 1; break;
            }
            if (targetPage != null)
            {
                try
                {
                    // 方向: 新页面索引 > 旧页面 -> 从右滑入; 否则从左滑入
                    double fromX = (_lastPageIndex >= 0 && currentIndex < _lastPageIndex) ? -20 : 20;
                    if (_lastPageIndex < 0) fromX = 0; // 首次无方向
                    _lastPageIndex = currentIndex;

                    targetPage.Opacity = 0;
                    var trans = new System.Windows.Media.TranslateTransform(fromX, 0);
                    targetPage.RenderTransform = trans;

                    // 直接用BeginAnimation, 不用Storyboard(Storyboard无法对不在可视化树的TranslateTransform动画)
                    // 动画默认 FillBehavior=HoldEnd, 结束后属性会永久停留在"被动画接管"的状态,
                    // 之后再直接赋值 Opacity/RenderTransform 都不会生效, 因此这里在完成时把终值写回基值并摘掉动画。
                    var fadeAnim = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(280));
                    fadeAnim.EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
                    fadeAnim.Completed += (s, a) =>
                    {
                        try
                        {
                            targetPage.Opacity = 1;
                            targetPage.BeginAnimation(UIElement.OpacityProperty, null);
                        }
                        catch (Exception ex)
                        {
                            Services.Logger.Instance.Debug("页面淡入动画收尾失败: " + ex.Message);
                        }
                    };
                    targetPage.BeginAnimation(UIElement.OpacityProperty, fadeAnim);

                    var slideAnim = new System.Windows.Media.Animation.DoubleAnimation(fromX, 0, TimeSpan.FromMilliseconds(280));
                    slideAnim.EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
                    slideAnim.Completed += (s, a) =>
                    {
                        try
                        {
                            trans.X = 0;
                            trans.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
                        }
                        catch (Exception ex)
                        {
                            Services.Logger.Instance.Debug("页面滑动动画收尾失败: " + ex.Message);
                        }
                    };
                    trans.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, slideAnim);

                    Services.Logger.Instance.Debug($"页面方向过渡: {pageName}, fromX={fromX}");
                    Services.CrashReportService.UpdateUIState("page_transition", pageName, true, "");
                }
                catch (Exception ex)
                {
                    Services.Logger.Instance.Error($"页面方向过渡失败: {ex.Message}");
                    Services.CrashReportService.UpdateUIState("page_transition", pageName, false, ex.Message);
                }
            }

            Services.Logger.Instance.Debug($"页面切换: {pageName}");
        }

        #endregion

        #region 设置事件

        private void Setting_Checked(object sender, RoutedEventArgs e)
        {
            CheckBox cb = sender as CheckBox;
            if (cb != null)
            {
                Services.Logger.Instance.CheckboxChanged(cb.Content?.ToString() ?? cb.Name, true, cb.Name);
                SaveSettingsFromUI();
                _controller.UpdateSettings(_settings);
            }
        }

        private void Setting_Unchecked(object sender, RoutedEventArgs e)
        {
            CheckBox cb = sender as CheckBox;
            if (cb != null)
            {
                Services.Logger.Instance.CheckboxChanged(cb.Content?.ToString() ?? cb.Name, false, cb.Name);
                SaveSettingsFromUI();
                _controller.UpdateSettings(_settings);
            }
        }

        private void BtnSaveSettings_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("保存设置", "BtnSaveSettings");
            SaveSettingsFromUI();
            _controller.UpdateSettings(_settings);
            MessageBox.Show("设置已保存！", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnResetSettings_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("恢复默认", "BtnResetSettings");
            var result = MessageBox.Show("确定要恢复默认设置吗？", "确认", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                _settings.ResetToDefault();
                _settings.Save();
                ApplySettingsToUI();
                ApplyLiquidGlass();
                _controller.UpdateSettings(_settings);
            }
        }

        private void BtnUnloadNetFilter_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("解除网络控制", "BtnUnloadNetFilter");
            _controller.UnloadNetFilter();
        }

        private void BtnLoadDriver_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("加载内核驱动", "BtnLoadDriver");
            _controller.LoadDriver();
            UpdateDriverStatus();
        }

        private void BtnUnloadDriver_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("卸载内核驱动", "BtnUnloadDriver");
            _controller.UnloadDriver();
            UpdateDriverStatus();
        }

        private void UpdateDriverStatus()
        {
            if (_controller.IsDriverLoaded)
            {
                DriverStatusText.Text = "驱动状态: 已加载";
                DriverStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x28, 0xA7, 0x45));
            }
            else
            {
                DriverStatusText.Text = "驱动状态: 未加载";
                DriverStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0x35, 0x45));
            }
        }

        private void BtnLocateJiYu_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("选择极域主进程位置", "BtnLocateJiYu");

            OpenFileDialog dlg = new OpenFileDialog
            {
                Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
                Title = "选择极域主进程 (StudentMain.exe)"
            };

            if (!string.IsNullOrEmpty(_settings.JiYuMainPath) && File.Exists(_settings.JiYuMainPath))
            {
                dlg.InitialDirectory = Path.GetDirectoryName(_settings.JiYuMainPath);
                dlg.FileName = Path.GetFileName(_settings.JiYuMainPath);
            }

            if (dlg.ShowDialog() == true)
            {
                _settings.JiYuMainPath = dlg.FileName;
                JiYuPathText.Text = $"极域路径: {dlg.FileName}";
                Services.Logger.Instance.Info($"已选择极域路径: {dlg.FileName}");
                MessageBox.Show("极域路径已设置，点击保存设置生效。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void BtnReadJiYuPassword_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("读取极域密码", "BtnReadJiYuPassword");

            try
            {
                string passwd = Services.JiYuController.Instance.ReadJiYuPassword(false);

                if (!string.IsNullOrEmpty(passwd))
                {
                    MessageBox.Show("已成功读取极域密码：\n\n" + passwd, "极域密码", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show("极域密码读取失败！\n\n您可以尝试万能密码：\nmythware_super_password", "读取失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                Services.Logger.Instance.Error("[MainWindow] 读取极域密码异常: " + ex.Message);
                MessageBox.Show("读取过程中发生异常：\n" + ex.Message + "\n\n您可以尝试万能密码：\nmythware_super_password", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }


        #endregion
    }
}
