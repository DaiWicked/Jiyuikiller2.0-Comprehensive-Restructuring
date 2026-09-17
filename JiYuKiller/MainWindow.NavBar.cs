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
        #region Liquid Glass

        private void ApplyLiquidGlass()
        {
            Services.Logger.Instance.FunctionCall("ApplyLiquidGlass", $"opacity={_settings.GlassOpacity}");

            double value = _settings.GlassOpacity / 100.0;
            byte whiteAlpha = (byte)((1 - value) * 255);
            double wallpaperOpacity = 1 - value;

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
            if (SliderContentOpacity != null)
            {
                SliderContentOpacity.Value = value;
            }

            Services.Logger.Instance.Debug($"Liquid Glass: value={value:F2}, whiteAlpha={whiteAlpha}, wallpaperOpacity={wallpaperOpacity:F2}");
        }

        /// <summary>
        /// 给GlassContainer加真正的圆角裁剪（RectangleGeometry）。
        /// 修复AllowsTransparency窗口四角变黑问题：
        /// ClipToBounds只裁矩形、CornerRadius不裁子元素，
        /// 而Effect(GlassyEffect/DropShadowEffect)的输出会铺满元素矩形，
        /// 因此必须在祖先容器上用圆角RectangleGeometry裁整棵子树。
        /// </summary>
        private void UpdateGlassClip()
        {
            if (GlassClipRoot == null) return;

            double w = GlassClipRoot.ActualWidth;
            double h = GlassClipRoot.ActualHeight;
            if (w <= 0 || h <= 0) return;

            var geo = GlassClipRoot.Clip as System.Windows.Media.RectangleGeometry;
            if (geo == null)
            {
                geo = new System.Windows.Media.RectangleGeometry();
                GlassClipRoot.Clip = geo;
            }
            geo.Rect = new System.Windows.Rect(0, 0, w, h);
            geo.RadiusX = 15;   // 外圆角16 - 描边1，同心
            geo.RadiusY = 15;

            Services.Logger.Instance.Debug($"[圆角Clip] 已应用到GlassClipRoot: {w:F0}x{h:F0}, Radius=15");
        }

        // ========== 底栏玻璃效果 ==========
        // ========== 底栏玻璃效果（快照裁切方案，无着色器，避免色差偏色） ==========
        // 底栏背景用VisualBrush，不需要缓存字段

        /// <summary>初始化：模糊层 + 液态玻璃折射层（受设置与渲染层级控制）</summary>
        private void InitNavBarGlass()
        {
            try
            {
                if (NavBarGlass != null)
                {
                    NavBarGlass.Effect = new System.Windows.Media.Effects.BlurEffect
                    {
                        Radius = 5,
                        KernelType = System.Windows.Media.Effects.KernelType.Gaussian
                    };
                }

                int tier = System.Windows.Media.RenderCapability.Tier >> 16;
                navLensTier = tier;
                Services.Logger.Instance.Info("[NavBar] 底栏玻璃已启用（快照裁切 + BlurEffect）");
                Services.Logger.Instance.Info("[NavBar] 渲染层级 Tier = " + tier);

                // Tier 0 的逐像素着色器是 CPU 实现，先问系统到底支持不支持 ——
                // 不支持时强行开只会得到错误或空白的渲染结果（"3D 不正常"的老机器/虚拟机正是这一类）。
                bool swShaderOk = System.Windows.Media.RenderCapability.IsPixelShaderVersionSupportedInSoftware(2, 0);   // 旧 API IsShaderEffectSoftwareRenderingSupported 已过时
                bool hwPs2 = System.Windows.Media.RenderCapability.IsPixelShaderVersionSupported(2, 0);
                Services.Logger.Instance.Info(string.Format(
                    "[NavBar] 着色器能力: 软件渲染支持={0}, 硬件PS2.0={1}, 进程渲染模式={2}",
                    swShaderOk, hwPs2, System.Windows.Media.RenderOptions.ProcessRenderMode));

                // 液态玻璃折射层：受设置开关 + 渲染层级闸门控制，
                // 因为 Tier==0 是纯软件渲染，逐像素着色器在老机器上可能拖慢拖动窗口。
                bool wantLens = _settings != null && _settings.NavBarLiquidGlass;
                if (tier == 0 && !_settings.NavBarLiquidGlassForceOnTier0)
                {
                    wantLens = false;
                }
                if (tier == 0 && !swShaderOk)
                {
                    // 软件渲染 + 系统不支持软件着色器：勾了"强制启用"也只是白开，直接关掉并在状态行说明
                    if (wantLens) Services.Logger.Instance.Warn("[NavBar] Tier0 且系统不支持软件着色器, 折射强制关闭");
                    wantLens = false;
                }
                ApplyNavBarLens(wantLens, tier == 0 ? (swShaderOk ? "Tier0 默认关闭" : "Tier0 不支持软件着色器") : "设置允许");
                UpdateNavButtonBackdrops();   // 折射开关状态变了，按钮磨砂要跟着挂/摘
            }
            catch (Exception ex)
            {
                Services.Logger.Instance.Error("[NavBar] 底栏玻璃初始化失败", ex);
                if (NavBarGlass != null) NavBarGlass.Effect = null;
            }
        }

        /// <summary>
        /// 挂上/摘掉液态玻璃折射层，并按当前尺寸刷新它的几何参数。
        /// </summary>
        private void ApplyNavBarLens(bool enable, string reason)
        {
            if (NavBarLens == null) return;

            try
            {
                if (!enable)
                {
                    NavBarLens.Effect = null;
                    navLensActive = false;
                    Services.Logger.Instance.Info("[NavBar] 液态玻璃: 关闭 (" + reason + ")");
                    return;
                }

                if (NavBarLensFx == null)
                {
                    Services.Logger.Instance.Warn("[NavBar] 液态玻璃: 未找到 NavBarLensFx，跳过");
                    return;
                }

                if (navLensActive && NavBarLens.Effect == NavBarLensFx)
                {
                    UpdateNavBarLensParams();
                    return;
                }

                if (!NavBarLensFx.IsShaderLoaded)
                {
                    navLensActive = false;
                    Services.Logger.Instance.Warn("[NavBar] 液态玻璃: 着色器未加载成功, 保持普通模糊");
                    return;
                }

                NavBarLens.Effect = NavBarLensFx;
                navLensActive = true;
                UpdateNavBarLensParams();
                Services.Logger.Instance.Info("[NavBar] 液态玻璃: 已启用 (" + reason + ")");
            }
            catch (Exception ex)
            {
                navLensActive = false;
                NavBarLens.Effect = null;
                Services.Logger.Instance.Error("[NavBar] 液态玻璃启用失败, 已回退普通模糊", ex);
            }

            UpdateNavLensStatusText();
        }

        /// <summary>
        /// 把底栏的实际几何换算成着色器参数（全部用设备像素，DPI 无关）。
        /// 关键：玻璃圆角矩形 = 去掉外扩边距后的那个矩形；折射只发生在它边缘的内侧。
        /// </summary>
        private void UpdateNavBarLensParams()
        {
            if (!navLensActive || NavBarLensFx == null || NavBarLens == null || NavBarClipRoot == null) return;

            try
            {
                // 设备像素换算（100% 缩放时 scale = 1）
                double scale = 1.0;
                var src = System.Windows.PresentationSource.FromVisual(this);
                if (src != null && src.CompositionTarget != null)
                {
                    scale = src.CompositionTarget.TransformToDevice.M11;
                }
                if (scale <= 0) scale = 1.0;

                double lensW = NavBarLens.ActualWidth * scale;      // 外扩后的元素尺寸(px)
                double lensH = NavBarLens.ActualHeight * scale;
                double barW = NavBarClipRoot.ActualWidth * scale;   // 玻璃本体的尺寸(px)
                double barH = NavBarClipRoot.ActualHeight * scale;
                if (lensW <= 1 || lensH <= 1 || barW <= 1 || barH <= 1) return;

                double strength = _settings != null ? _settings.NavBarLiquidGlassStrength : 0.35;
                if (strength < 0) strength = 0;
                if (strength > 1) strength = 1;

                NavBarLensFx.TextureSize = new System.Windows.Point(lensW, lensH);
                NavBarLensFx.GlassHalf = new System.Windows.Point(barW * 0.5, barH * 0.5);
                NavBarLensFx.GlassRadius = 23.0 * scale;
                // 折射带宽 ≈ 21px，强度随设置线性放大（最大 16px 向内位移）
                NavBarLensFx.EdgeWidth = 21.0 * scale;
                NavBarLensFx.RefractStrength = 16.0 * scale * strength;
                // 色散用绝对像素量: 1.2px 的 R/B 分离在边缘才看得出彩边（比例写法只有 0.14px，看不见）
                NavBarLensFx.AberrationPx = 1.2 * scale;
                NavBarLensFx.RimBoost = 0.12;
                // 圆角附加折射（参考实现的 cornerBoost）：按基础折射量的一半给圆角"加料"，
                // 于是强度滑块整体缩放时，圆角凸起始终与基础折射保持比例
                NavBarLensFx.CornerBoostPx = NavBarLensFx.RefractStrength * 0.5;
                NavBarLensFx.CornerFalloff = 20.0 * scale;
                Services.Logger.Instance.Debug(string.Format(
                    "[NavBar] 圆角附加折射: 加料={0:F1}px, 影响范围={1:F1}px",
                    NavBarLensFx.CornerBoostPx, NavBarLensFx.CornerFalloff));
                NavBarLensFx.Strength = strength > 0 ? 1.0 : 0.0;

                Services.Logger.Instance.Debug(string.Format(
                    "[NavBar] 液态玻璃参数: 元素={0:F0}x{1:F0}px, 玻璃={2:F0}x{3:F0}px, 半径={4:F1}px, 折射={5:F1}px, 强度={6:F2}, scale={7:F2}",
                    lensW, lensH, barW, barH, NavBarLensFx.GlassRadius, NavBarLensFx.RefractStrength, strength, scale));
            }
            catch (Exception ex)
            {
                Services.Logger.Instance.Debug("[NavBar] 液态玻璃参数刷新失败: " + ex.Message);
            }
        }

        /// <summary>更新底栏背景：VisualBrush取主程序背景（BackdropContainer）的底栏区域</summary>
        private void UpdateNavBarBackdrop()
        {
            if (NavBarGlassBrush == null || NavBarGlass == null || BackdropContainer == null) return;
            double w = NavBarGlass.ActualWidth;
            double h = NavBarGlass.ActualHeight;
            if (w <= 0 || h <= 0) return;

            try
            {
                // 底栏在BackdropContainer坐标系中的矩形（与主窗口GlassyLayer同一套换算）
                Point p = NavBarGlass.TranslatePoint(new Point(0, 0), BackdropContainer);
                NavBarGlassBrush.ViewboxUnits = System.Windows.Media.BrushMappingMode.Absolute;
                NavBarGlassBrush.Viewbox = new System.Windows.Rect(p.X, p.Y, w, h);
                UpdateNavIndicatorBackdrop();
                UpdateNavButtonBackdrops();
            }
            catch (Exception ex)
            {
                Services.Logger.Instance.Debug("[NavBar] 更新底栏背景失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 更新指示器的"嵌套玻璃"采样区域。
        /// 指示器会左右滑动、宽度也随按钮变化，所以必须在动画过程中持续更新 Viewbox；
        /// 否则背后的内容会跟着指示器一起走 —— 那就成了贴纸，不是玻璃。
        /// </summary>
        private void UpdateNavIndicatorBackdrop()
        {
            if (NavIndicatorBrush == null || NavIndicator == null || BackdropContainer == null) return;

            try
            {
                double w = NavIndicator.ActualWidth;
                double h = NavIndicator.ActualHeight;
                if (w <= 0 || h <= 0) return;

                Point p = NavIndicator.TranslatePoint(new Point(0, 0), BackdropContainer);
                NavIndicatorBrush.ViewboxUnits = System.Windows.Media.BrushMappingMode.Absolute;
                NavIndicatorBrush.Viewbox = new System.Windows.Rect(p.X, p.Y, w, h);
            }
            catch (Exception ex)
            {
                Services.Logger.Instance.Debug("[NavBar] 更新指示器背景失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 给底栏每个按钮铺一层"它自己的磨砂玻璃"。
        ///
        /// 背景：按钮模板里原本就有个 BlurEffect，但它作用在**纯色**背景上 —— 模糊纯色还是纯色，
        /// 所以除了把胶囊边缘糊软之外没有任何模糊效果（这就是"加了模糊却看不出"的原因）。
        /// 要让按钮真的比底栏更"霜"，必须让按钮背景去采样桌面原图再模糊。
        ///
        /// VisualBrush 由代码 new 出来、而不是写在模板里：模板里的 Freezable 是否按实例克隆并不可靠，
        /// 万一被共享，所有按钮会互相抢 Viewbox（全部显示同一块背景）。
        /// </summary>
        private void UpdateNavButtonBackdrops()
        {
            if (BackdropContainer == null || NavStackPanel == null) return;

            // 只在底栏折射层真的在跑时才铺按钮磨砂：这一层是"叠在折射玻璃上的嵌套玻璃"，
            // 折射被关掉时（Tier 0 软件渲染 / 用户关掉开关）再叠一层会让按钮和底栏质感分家（按钮像贴纸）；
            // 而且软件渲染下每个模糊层都走 CPU，11 个按钮各一个模糊会明显拖慢合成。
            if (!navLensActive)
            {
                DetachNavButtonBackdrops();
                return;
            }

            try
            {
                foreach (object child in NavStackPanel.Children)
                {
                    var btn = child as System.Windows.Controls.Button;
                    if (btn == null) continue;

                    System.Windows.Media.VisualBrush brush;
                    if (!_navBtnGlassBrushes.TryGetValue(btn, out brush))
                    {
                        btn.ApplyTemplate();
                        var host = (btn.Template != null)
                            ? btn.Template.FindName("glassBlur", btn) as System.Windows.Controls.Border
                            : null;
                        if (host == null) continue;

                        brush = new System.Windows.Media.VisualBrush
                        {
                            Visual = BackdropContainer,
                            Stretch = System.Windows.Media.Stretch.Fill,
                            ViewboxUnits = System.Windows.Media.BrushMappingMode.Absolute,
                            Viewbox = new System.Windows.Rect(0, 0, 1, 1)
                        };
                        host.Background = brush;
                        _navBtnGlassBrushes[btn] = brush;
                    }

                    double w = btn.ActualWidth, h = btn.ActualHeight;
                    if (w <= 0 || h <= 0) continue;

                    Point p = btn.TranslatePoint(new Point(0, 0), BackdropContainer);
                    brush.Viewbox = new System.Windows.Rect(p.X, p.Y, w, h);
                }
            }
            catch (Exception ex)
            {
                Services.Logger.Instance.Debug("[NavBar] 更新按钮磨砂背景失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 摘掉按钮磨砂层：折射关闭时让按钮与底栏质感保持一致，
        /// 同时省掉软件渲染(Tier 0)下 11 个按钮各一份 CPU 模糊的开销。
        /// </summary>
        private void DetachNavButtonBackdrops()
        {
            if (_navBtnGlassBrushes.Count == 0) return;

            foreach (var kv in _navBtnGlassBrushes)
            {
                try
                {
                    var btn = kv.Key;
                    var host = (btn.Template != null)
                        ? btn.Template.FindName("glassBlur", btn) as System.Windows.Controls.Border
                        : null;
                    if (host != null) host.Background = null;
                }
                catch { }
            }
            _navBtnGlassBrushes.Clear();
        }
        /// <summary>
        /// 更新底栏圆角几何（几何操作，极廉价）。
        /// 描边(Path)与裁剪(Clip)用同一个生成器算出，保证拐角处完全对齐；
        /// 是否使用"超椭圆"由设置决定（false 则退化为普通圆角矩形）。
        /// </summary>
        private void UpdateNavBarClip()
        {
            if (NavBarClipRoot == null) return;
            double w = NavBarClipRoot.ActualWidth, h = NavBarClipRoot.ActualHeight;
            if (w <= 0 || h <= 0) return;

            bool squircle = _settings == null || _settings.NavBarSquircle;
            double ext = _settings != null ? _settings.NavBarSquircleExtension : 1.2819;

            // 内层裁剪：半径 23（外圆角 24 - 描边 1，同心）
            NavBarClipRoot.Clip = Effects.SquircleGeometry.Create(w, h, 23.0, squircle, ext);

            // 外层描边：与外层同尺寸（比裁剪根大 1px），半径 24
            if (NavBarFramePath != null)
            {
                double fw = NavBarFramePath.ActualWidth, fh = NavBarFramePath.ActualHeight;
                if (fw > 0 && fh > 0)
                {
                    NavBarFramePath.Data = Effects.SquircleGeometry.Create(fw, fh, 24.0, squircle, ext);
                }
            }
        }

        // ==================== 底栏文字颜色自适应 ====================
        // 阈值推导：浅字(#F2F5F8)与深字(#1A1A22)等对比度点在背景相对亮度≈0.19
        //          → gamma空间≈0.47；本套scrim平均压暗≈31% → 原始背景阈值≈0.68
        // 迟滞带0.68/0.62：避免临界值附近来回闪烁
        private const double NavLumaToDark = 0.42;   // 漏算WallpaperLayer默认白色28%+WhiteOverlay 27.8%，原0.68有误
        private const double NavLumaToLight = 0.36;  // 迟滞下限

        private bool _navUseDarkText = false;
        private byte[] _navLumaBuffer;
        private bool navLensActive = false;   // 液态玻璃折射层当前是否挂着
        private int navLensTier = 0;          // 记录渲染层级，便于设置变更时重新判定
        private System.Windows.Media.SolidColorBrush _navFgBrush;
        private System.Windows.Media.GradientStop _navIndTop, _navIndBottom;

        /// <summary>只执行一次：抓取资源引用（必须在InitializeComponent之后）</summary>
        private void InitNavBarTextTheme()
        {
            try
            {
                var res = System.Windows.Application.Current.Resources;

                // 文字画刷：冻结则Clone出可变副本
                System.Windows.Media.SolidColorBrush brush = null;
                if (res.Contains("NavForegroundBrush"))
                    brush = res["NavForegroundBrush"] as System.Windows.Media.SolidColorBrush;

                if (brush == null)
                {
                    brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF2, 0xF5, 0xF8));
                }
                else if (brush.IsFrozen)
                {
                    brush = brush.Clone();
                }

                // ⚠️ 这里【绝对不要】写 res["NavForegroundBrush"] = brush;
                //
                // 实测结论(本机用 PowerShell + WPF 复现，两步对照实验):
                //   新建画刷                                        -> IsFrozen = False
                //   放进普通 ResourceDictionary                     -> IsFrozen = False
                //   放进 Application.Resources                      -> IsFrozen = **True**  ← 会自动冻结
                //   把 Clone() 出来的可变副本写进 Application.Resources -> IsFrozen = **True**  ← 又被冻住
                // 所以原先那句 `res["NavForegroundBrush"] = brush;` 会把刚 Clone 出来的可变副本
                // 立刻重新冻住，于是下面给按钮设的本地值也是冻结对象，
                // UpdateNavBarTextTheme 里的 BeginAnimation 必然抛
                //   "无法在 System.Windows.Media.SolidColorBrush 上激活 Color 属性，因为该对象已密封或已冻结"
                // （32 位 VM 实测日志里就是：初始化时 fg冻结=True，之后每次刷新都报"文字动画失败"，
                //   "本次配色未生效，保持原状态待下次重试" 重试了 9 次全部失败。）
                //
                // 11 个导航按钮用的是本地值，优先级高于 Style Setter 里的
                // {DynamicResource NavForegroundBrush}，所以既不需要、也不应该去改资源字典。

                // 关键：Style密封后Setter里的画刷已被冻结，必须直接给按钮设本地值
                // 本地值优先级高于Style Setter，不会被冻结
                var navButtons = new System.Windows.Controls.Button[] {
                    NavQuick, NavSetting, NavCustom, NavUdpAttack, NavChat,
                    NavScreenshot, NavTeacherSim, NavGames, NavHelp, NavDebug, NavAbout
                };
                foreach (var btn in navButtons)
                {
                    if (btn != null) btn.Foreground = brush;
                }
                _navFgBrush = brush;

                // 兜底：万一将来又被冻结(例如有人把画刷重新塞回资源字典)，再换一份可变副本。
                // 不入任何资源字典的 Clone 不会被自动冻结，可以安全动画。
                if (_navFgBrush.IsFrozen)
                {
                    Services.Logger.Instance.Warn("[NavBar] 文字画刷仍为冻结状态，已改用可变副本");
                    _navFgBrush = _navFgBrush.Clone();
                    foreach (var btn in navButtons)
                    {
                        if (btn != null) btn.Foreground = _navFgBrush;
                    }
                }

                // 滑动指示器渐变：同理
                if (NavIndicator != null)
                {
                    // XAML 结构（MainWindow.xaml:1240 起）：
                    //   NavIndicator(自身无 Background) -> Grid -> Border(VisualBrush) + Border(LinearGradientBrush)
                    // 那个渐变的两个 GradientStop 颜色**绑定到静态资源** NavIndicatorBrushTop / NavIndicatorBrushBottom，
                    // 所以直接取资源最稳（不依赖可视树层级；上一版取"第一个子 Border"取到的是 Grid，恒为 null）。
                    var lg = NavIndicator.Background as System.Windows.Media.LinearGradientBrush;
                    if (lg == null)
                    {
                        var topBrush = TryFindResource("NavIndicatorBrushTop") as System.Windows.Media.SolidColorBrush;
                        var bottomBrush = TryFindResource("NavIndicatorBrushBottom") as System.Windows.Media.SolidColorBrush;
                        if (topBrush != null && bottomBrush != null)
                        {
                            // 新建自己的渐变（不改动被冻结的资源画刷），这样后续调色/动画才安全
                            var built = new System.Windows.Media.LinearGradientBrush();
                            built.GradientStops.Add(new System.Windows.Media.GradientStop(topBrush.Color, 0));
                            built.GradientStops.Add(new System.Windows.Media.GradientStop(bottomBrush.Color, 1));
                            lg = built;
                        }
                    }

                    if (lg != null && lg.GradientStops.Count >= 2)
                    {
                        if (lg.IsFrozen) lg = lg.Clone();   // 只读取，不回写（子 Border 的画刷不该被替换）
                        _navIndTop = lg.GradientStops[0];
                        _navIndBottom = lg.GradientStops[1];
                    }
                }

                Services.Logger.Instance.Info(
                    $"[NavBar] 文字主题初始化: fg冻结={_navFgBrush.IsFrozen}, " +
                    $"指示器={(_navIndTop != null ? "已取得" : "未取得")}");
            }
            catch (Exception ex)
            {
                Services.Logger.Instance.Error("[NavBar] 文字主题初始化失败", ex);
            }
        }

        /// <summary>从已冻结的桌面截图求底栏区域平均luma（Rec.601，0~1），零分配</summary>
        private bool TryComputeNavBarLuma(out double luma)
        {
            luma = 1.0;
            var snap = Services.ScreenCaptureHelper.FullScreenSnapshot;
            if (snap == null || NavBarGlass == null) return false;
            if (NavBarGlass.ActualWidth <= 0 || NavBarGlass.ActualHeight <= 0) return false;

            try
            {
                System.Windows.Point onScreen = this.PointToScreen(NavBarGlass.TranslatePoint(new System.Windows.Point(0, 0), this));
                int x = (int)System.Math.Round(onScreen.X - Services.ScreenCaptureHelper.VirtualScreenX);
                int y = (int)System.Math.Round(onScreen.Y - Services.ScreenCaptureHelper.VirtualScreenY);
                int w = (int)System.Math.Round(NavBarGlass.ActualWidth);
                int h = (int)System.Math.Round(NavBarGlass.ActualHeight);

                x = System.Math.Max(0, System.Math.Min(x, snap.PixelWidth - 1));
                y = System.Math.Max(0, System.Math.Min(y, snap.PixelHeight - 1));
                w = System.Math.Max(1, System.Math.Min(w, snap.PixelWidth - x));
                h = System.Math.Max(1, System.Math.Min(h, snap.PixelHeight - y));

                int bytesPerPixel = (snap.Format.BitsPerPixel + 7) / 8;
                int stride = w * bytesPerPixel;
                int need = stride * h;
                if (_navLumaBuffer == null || _navLumaBuffer.Length < need)
                    _navLumaBuffer = new byte[need];

                snap.CopyPixels(new System.Windows.Int32Rect(x, y, w, h), _navLumaBuffer, stride, 0);

                int step = (w * h > 40000) ? 2 : 1;
                long sum = 0; int count = 0;
                for (int row = 0; row < h; row += step)
                {
                    int rowOff = row * stride;
                    for (int col = 0; col < w; col += step)
                    {
                        int i = rowOff + col * bytesPerPixel;
                        byte b = _navLumaBuffer[i];
                        byte g = _navLumaBuffer[i + 1];
                        byte r = _navLumaBuffer[i + 2];
                        sum += (77 * r + 150 * g + 29 * b) >> 8;
                        count++;
                    }
                }
                if (count == 0) return false;
                luma = (sum / (double)count) / 255.0;
                return true;
            }
            catch (Exception ex)
            {
                Services.Logger.Instance.Debug("[NavBar] 亮度计算失败: " + ex.Message);
                return false;
            }
        }

        /// <summary>按亮度切换底栏文字/指示器配色（迟滞+180ms平滑过渡）</summary>
        private void UpdateNavBarTextTheme()
        {
            double luma;
            if (!TryComputeNavBarLuma(out luma)) return;

            bool wantDark = _navUseDarkText ? (luma > NavLumaToLight) : (luma > NavLumaToDark);
            if (wantDark == _navUseDarkText) return;

            System.Windows.Media.Color fg = wantDark
                ? System.Windows.Media.Color.FromRgb(0x1A, 0x1A, 0x22)
                : System.Windows.Media.Color.FromRgb(0xF2, 0xF5, 0xF8);
            System.Windows.Media.Color indTop = wantDark
                ? System.Windows.Media.Color.FromArgb(0x30, 0x00, 0x00, 0x00)
                : System.Windows.Media.Color.FromArgb(0x5A, 0xFF, 0xFF, 0xFF);
            System.Windows.Media.Color indBottom = wantDark
                ? System.Windows.Media.Color.FromArgb(0x18, 0x00, 0x00, 0x00)
                : System.Windows.Media.Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF);

            var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
            System.TimeSpan dur = System.TimeSpan.FromMilliseconds(180);

            // 文字：动画失败退回直接赋值
            bool fgOk = false;
            if (_navFgBrush != null)
            {
                try
                {
                    _navFgBrush.BeginAnimation(System.Windows.Media.SolidColorBrush.ColorProperty,
                        new System.Windows.Media.Animation.ColorAnimation(fg, dur) { EasingFunction = ease });
                    fgOk = true;
                }
                catch (Exception ex)
                {
                    Services.Logger.Instance.Warn("[NavBar] 文字动画失败，退回直接赋值: " + ex.Message);
                    try
                    {
                        if (!_navFgBrush.IsFrozen) { _navFgBrush.Color = fg; fgOk = true; }
                    }
                    catch (Exception ex2)
                    {
                        Services.Logger.Instance.Error("[NavBar] 文字配色赋值失败", ex2);
                    }
                }
            }

            // 指示器两个GradientStop，各自独立try
            try
            {
                if (_navIndTop != null)
                    _navIndTop.BeginAnimation(System.Windows.Media.GradientStop.ColorProperty,
                        new System.Windows.Media.Animation.ColorAnimation(indTop, dur) { EasingFunction = ease });
            }
            catch (Exception ex) { Services.Logger.Instance.Warn("[NavBar] 指示器上色失败: " + ex.Message); }

            try
            {
                if (_navIndBottom != null)
                    _navIndBottom.BeginAnimation(System.Windows.Media.GradientStop.ColorProperty,
                        new System.Windows.Media.Animation.ColorAnimation(indBottom, dur) { EasingFunction = ease });
            }
            catch (Exception ex) { Services.Logger.Instance.Warn("[NavBar] 指示器下色失败: " + ex.Message); }

            // 只有真正切成功才推进状态；失败保持原值，下次刷新自动重试
            if (fgOk)
            {
                _navUseDarkText = wantDark;
                Services.Logger.Instance.Debug(
                    $"[NavBar] 文字配色切换 -> {(wantDark ? "深色" : "浅色")} (luma={luma:F3})");
            }
            else
            {
                Services.Logger.Instance.Warn(
                    $"[NavBar] 本次配色未生效，保持原状态待下次重试 (luma={luma:F3}, wantDark={wantDark})");
            }
        }


        /// <summary>
        /// 首次桌面截图就绪后把窗口淡入（只做一次）。
        /// 这样"窗口可见"与"玻璃有背景可显示"两个时刻就重合了，不会再出现开窗瞬间的白板帧。
        /// </summary>
        private void RevealWindowAfterBackdrop()
        {
            if (_windowRevealed) return;
            _windowRevealed = true;
            try
            {
                if (_glassyManager != null) _glassyManager.BackdropUpdated -= RevealWindowAfterBackdrop;
                Services.Logger.Instance.Info("[启动] 首次桌面截图就绪 -> 窗口淡入");
                var anim = new System.Windows.Media.Animation.DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(160))
                {
                    EasingFunction = new System.Windows.Media.Animation.CubicEase
                    {
                        EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
                    }
                };
                anim.Completed += (s, e) =>
                {
                    try { this.BeginAnimation(UIElement.OpacityProperty, null); this.Opacity = 1.0; } catch { }
                };
                this.BeginAnimation(UIElement.OpacityProperty, anim);
            }
            catch (Exception ex)
            {
                Services.Logger.Instance.Debug("[启动] 窗口淡入失败: " + ex.Message);
                try { this.Opacity = 1.0; } catch { }
            }
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.Info("窗口加载完成，初始化毛玻璃效果管理器");
            try
            {
                _glassyManager = new Effects.GlassyWindowManager(this, BackdropLayer, GlassyLayer);
                Services.Logger.Instance.Info("毛玻璃效果管理器初始化成功");
                // 确保模糊强度默认值生效（Slider ValueChanged在_glassyManager初始化前触发）
                if (SliderBlurIntensity != null)
                {
                    _glassyManager.BlurIntensity = SliderBlurIntensity.Value;
                    Services.Logger.Instance.Debug($"毛玻璃模糊强度已同步: {SliderBlurIntensity.Value:F2}");
                }
                InitWallpaper();
                // 桌面截图刷新时同步底栏背景
                _glassyManager.BackdropUpdated += () => { UpdateNavBarBackdrop(); UpdateNavBarTextTheme(); };
                // 首次桌面截图就绪后再让窗口显形：否则开窗瞬间所有玻璃层都没有背景图，
                // 会先看到约 200ms 的"无背景白板"再跳成玻璃。
                _glassyManager.BackdropUpdated += RevealWindowAfterBackdrop;
                // 管理器在构造过程中可能就把首帧快照拍好了（那次 BackdropUpdated 早于本次订阅），
                // 这时必须立刻显形，否则窗口要白等到兜底计时器才出现。
                if (Services.ScreenCaptureHelper.FullScreenSnapshot != null)
                {
                    UpdateNavBarBackdrop();
                    UpdateNavBarTextTheme();
                    RevealWindowAfterBackdrop();
                }
                // 初始化底栏玻璃效果（必须在UpdateNavBarGlass之前）
                InitNavBarGlass();
                InitNavBarTextTheme();
                UpdateNavIndicatorBackdrop();
                UpdateNavButtonBackdrops();
                InitNoiseLayer();
            }
            catch (Exception ex)
            {
                Services.Logger.Instance.Error("毛玻璃效果管理器初始化失败", ex);
                RevealWindowAfterBackdrop();   // 初始化失败也必须显形，不能留下一个看不见的窗口
            }
            // 初始化导航指示器位置到第一个按钮
            try
            {
                if (NavQuick != null && NavIndicator != null)
                {
                    this.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        AnimateNavIndicator(NavQuick);
                    }), System.Windows.Threading.DispatcherPriority.Loaded);
                }
            }
            catch (Exception ex)
            {
                Services.Logger.Instance.Warn("导航指示器初始化失败: " + ex.Message);
            }

            // 底栏液态玻璃自检（供自动化/无头验证使用；平时不触发）
            //   用法: 设置环境变量 JYKILLER_NAVLENS_SELFTEST=1 后启动本程序，
            //   加载完成后会自动跑一次自检并把结果写进日志，同时导出两张对比 PNG。
            try
            {
                if (Environment.GetEnvironmentVariable("JYKILLER_NAVLENS_SELFTEST") == "1")
                {
                    Services.Logger.Instance.Info("[NavLens自检] 检测到 JYKILLER_NAVLENS_SELFTEST=1, 加载后自动执行");
                    this.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try { DebugNavLensReport(Environment.GetEnvironmentVariable("JYKILLER_NAVLENS_STRENGTH")); }
                        catch (Exception ex2) { Services.Logger.Instance.Error("[NavLens自检] 自动执行失败", ex2); }
                    }), System.Windows.Threading.DispatcherPriority.Background);
                }
            }
            catch (Exception ex)
            {
                Services.Logger.Instance.Debug("[NavLens自检] 触发检查失败: " + ex.Message);
            }
        }
        #endregion
    }
}