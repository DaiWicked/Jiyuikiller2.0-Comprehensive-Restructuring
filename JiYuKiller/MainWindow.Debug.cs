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
        #region 调试

        private void DebugMode_Checked(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            _settings.DebugMode = true;
            _settings.Save();
            Services.Logger.Instance.Enable();
            Services.Logger.Instance.Info("调试模式已开启，日志已启用");
        }

        private void DebugMode_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            _settings.DebugMode = false;
            _settings.Save();
            Services.Logger.Instance.Info("调试模式已关闭，即将禁用日志");
            Services.Logger.Instance.Disable();
        }

        private void BtnRefreshLog_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("刷新日志", "BtnRefreshLog");
            RefreshLog();
        }

        private void BtnOpenLogFolder_Click(object sender, RoutedEventArgs e)
        {
            Services.Logger.Instance.ButtonClick("打开日志目录", "BtnOpenLogFolder");
            string logDir = AppDomain.CurrentDomain.BaseDirectory;
            Process.Start("explorer.exe", logDir);
        }

        private void RefreshLog()
        {
            try
            {
                string logPath = Services.Logger.Instance.LogPath;
                if (File.Exists(logPath))
                {
                    // 读取最后 500 行 (用FileShare.ReadWrite避免文件被占用)
                    var lines = new System.Collections.Generic.List<string>();
                    using (var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var sr = new StreamReader(fs))
                    {
                        while (!sr.EndOfStream)
                        {
                            lines.Add(sr.ReadLine());
                        }
                    }
                    int start = Math.Max(0, lines.Count - 500);
                    DebugLogBox.Text = string.Join("\n", lines.ToArray(), start, lines.Count - start);
                    DebugLogBox.ScrollToEnd();
                    Services.Logger.Instance.Debug($"日志已刷新，显示最后 {lines.Count - start} 行");
                }
                else
                {
                    DebugLogBox.Text = "日志文件不存在";
                }
            }
            catch (Exception ex)
            {
                DebugLogBox.Text = $"读取日志失败: {ex.Message}";
                Services.Logger.Instance.Error("读取日志失败", ex);
            }
        }

        #endregion

        #region 调试命令

        private void BtnRunCmd_Click(object sender, RoutedEventArgs e)
        {
            ExecuteDebugCommand();
        }

        private void TextDebugCmd_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                ExecuteDebugCommand();
            }
        }

        private void ExecuteDebugCommand()
        {
            string cmd = TextDebugCmd.Text.Trim();
            if (string.IsNullOrEmpty(cmd))
            {
                Services.Logger.Instance.Warn("调试命令为空");
                AppendDebugOutput("[错误] 请输入命令！输入 help 查看可用命令");
                return;
            }

            Services.Logger.Instance.Info($"执行调试命令: {cmd}");
            AppendDebugOutput($"> {cmd}");

            string[] parts = cmd.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string command = parts[0].ToLower();

            try
            {
                switch (command)
                {
                    case "help":
                        AppendDebugOutput("可用命令:");
                        AppendDebugOutput("  help          - 显示帮助");
                        AppendDebugOutput("  killst        - 杀死极域进程");
                        AppendDebugOutput("  rerunst       - 重启极域进程");
                        AppendDebugOutput("  status        - 显示当前状态");
                        AppendDebugOutput("  whereisi      - 显示程序路径");
                        AppendDebugOutput("  shutdown      - 关闭计算机");
                        AppendDebugOutput("  reboot        - 重新启动计算机");
                        AppendDebugOutput("  exit          - 退出软件");
                        AppendDebugOutput("  clear         - 清空日志显示");
                        AppendDebugOutput("  refresh       - 刷新日志");
                        AppendDebugOutput("  navlens [强度] - 底栏液态玻璃自检(导出对比图并打印量化结果)");
                        break;

                    case "killst":
                        var procs = Process.GetProcessesByName("StudentMain");
                        if (procs.Length > 0)
                        {
                            foreach (var p in procs)
                            {
                                try { p.Kill(); }
                                catch { /* 进程可能已退出 */ }
                            }
                            AppendDebugOutput($"[成功] 已杀死 {procs.Length} 个极域进程");
                        }
                        else
                        {
                            AppendDebugOutput("[提示] 未找到极域进程");
                        }
                        // Process 对象持有进程句柄, 必须释放
                        foreach (var p in procs) p.Dispose();
                        UpdateJiYuStatus();
                        break;

                    case "rerunst":
                        BtnRestartJiYu_Click(null, null);
                        AppendDebugOutput("[成功] 已执行重启极域命令");
                        break;

                    case "status":
                        AppendDebugOutput($"当前状态: {StatusText.Text}");
                        AppendDebugOutput($"极域状态: {TextJiYuStatus.Text}");
                        break;

                    case "whereisi":
                        string appPath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                        AppendDebugOutput($"程序路径: {appPath}");
                        break;

                    case "shutdown":
                        AppendDebugOutput("[执行] 正在关闭计算机...");
                        BtnShutdown_Click(null, null);
                        break;

                    case "reboot":
                        AppendDebugOutput("[执行] 正在重新启动计算机...");
                        BtnReboot_Click(null, null);
                        break;

                    case "exit":
                        AppendDebugOutput("[执行] 正在退出软件...");
                        ExitApplication();
                        break;

                    case "clear":
                        DebugLogBox.Clear();
                        break;

                    case "refresh":
                        BtnRefreshLog_Click(null, null);
                        AppendDebugOutput("[成功] 日志已刷新");
                        break;

                    case "navlens":
                        DebugNavLensReport(parts.Length > 1 ? parts[1] : null);
                        break;

                    default:
                        AppendDebugOutput($"[错误] 未知命令: {command}，输入 help 查看可用命令");
                        break;
                }
            }
            catch (Exception ex)
            {
                AppendDebugOutput($"[错误] 命令执行失败: {ex.Message}");
                Services.Logger.Instance.Error($"调试命令执行失败: {cmd}", ex);
            }

            TextDebugCmd.Clear();
            TextDebugCmd.Focus();
        }

        private void AppendDebugOutput(string text)
        {
            DebugLogBox.AppendText(text + Environment.NewLine);
            DebugLogBox.ScrollToEnd();
        }

        // ========== 底栏液态玻璃自检 ==========
        // 因为无法"用眼睛看"渲染结果，这里用可量化的方式验证三件事:
        //   ① 中心区域与"关折射"的差异应 ≈ 0      -> 证明没有整片偏色（上次的坑）
        //   ② 边缘带与"关折射"的差异应明显 > 0      -> 证明真的在折射
        //   ③ 边缘带的 RGB 通道离散应 > 中心        -> 证明色散存在且只在边缘
        // 同时导出两张 PNG 供人工目视对比。

        /// <summary>等一个渲染周期，确保刚改的依赖属性已经反映到画面上</summary>
        private static void FlushRenderPass()
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Render,
                new Action(() => { frame.Continue = false; }));
            System.Windows.Threading.Dispatcher.PushFrame(frame);
        }

        private System.Windows.Media.Imaging.BitmapSource RenderNavBarToBitmap(double w, double h)
        {
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                Math.Max(1, (int)Math.Round(w)), Math.Max(1, (int)Math.Round(h)), 96, 96,
                System.Windows.Media.PixelFormats.Pbgra32);
            rtb.Render(NavBarClipRoot);
            rtb.Freeze();
            return rtb;
        }

        private static void SaveBitmapPng(System.Windows.Media.Imaging.BitmapSource bmp, string fileName)
        {
            try
            {
                string path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                using (var fs = new System.IO.FileStream(path, System.IO.FileMode.Create, System.IO.FileAccess.Write))
                {
                    enc.Save(fs);
                }
                Services.Logger.Instance.Info("[NavLens自检] 已导出: " + path);
            }
            catch (Exception ex)
            {
                Services.Logger.Instance.Warn("[NavLens自检] 导出 PNG 失败: " + ex.Message);
            }
        }

        private static byte[] ReadBgra(System.Windows.Media.Imaging.BitmapSource bmp, out int stride)
        {
            int w = bmp.PixelWidth, h = bmp.PixelHeight;
            stride = w * 4;
            var buf = new byte[stride * h];
            bmp.CopyPixels(buf, stride, 0);
            return buf;
        }

        /// <summary>两个位图在指定矩形内的平均通道绝对差（0~255）</summary>
        private static double MeanAbsDiff(byte[] a, byte[] b, int stride, int x0, int y0, int x1, int y1)
        {
            long sum = 0; long n = 0;
            for (int y = y0; y < y1; y++)
            {
                int row = y * stride;
                for (int x = x0; x < x1; x++)
                {
                    int i = row + x * 4;
                    sum += Math.Abs(a[i] - b[i]);         // B
                    sum += Math.Abs(a[i + 1] - b[i + 1]); // G
                    sum += Math.Abs(a[i + 2] - b[i + 2]); // R
                    n += 3;
                }
            }
            return n == 0 ? 0 : (double)sum / n;
        }

        /// <summary>位图在指定矩形内的平均通道离散度 (|R-G| + |G-B|)/2 —— 用于度量色散</summary>
        private static double MeanChannelSpread(byte[] px, int stride, int x0, int y0, int x1, int y1)
        {
            long sum = 0; long n = 0;
            for (int y = y0; y < y1; y++)
            {
                int row = y * stride;
                for (int x = x0; x < x1; x++)
                {
                    int i = row + x * 4;
                    int b = px[i], g = px[i + 1], r = px[i + 2];
                    sum += (Math.Abs(r - g) + Math.Abs(g - b)) / 2;
                    n++;
                }
            }
            return n == 0 ? 0 : (double)sum / n;
        }

        /// <summary>
        /// 在指定通道上，找把 on 对齐到 off 所需的水平位移（整数像素）。
        /// 返回 argmin_d Σ|on(x+d) - off(x)|。
        /// 若 on(x) = off(x+k)（即采样点被偏移了 k），则最佳 d = -k。
        /// </summary>
        private static int BestHorizontalShift(byte[] on, byte[] off, int stride,
            int chOffset, int x0, int y0, int x1, int y1, int maxShift)
        {
            int bestD = 0;
            long bestCost = long.MaxValue;
            for (int d = -maxShift; d <= maxShift; d++)
            {
                long cost = 0;
                for (int y = y0; y < y1; y++)
                {
                    int row = y * stride;
                    for (int x = x0; x < x1; x++)
                    {
                        int xs = x + d;
                        if (xs < 0 || xs >= (stride / 4)) continue;
                        cost += Math.Abs(on[row + xs * 4 + chOffset] - off[row + x * 4 + chOffset]);
                    }
                }
                if (cost < bestCost) { bestCost = cost; bestD = d; }
            }
            return bestD;
        }

        /// <summary>
        /// 合成测试背景：一个"非周期"的灰阶台阶（在元素宽度约 6.5% 处从黑跳到白，即玻璃左边缘内侧几像素）。
        /// 为什么不用周期条纹：12px 周期配 ~5px 位移会产生周期性歧义，相关性函数变平，
        /// 实测就会顶到搜索边界（上一版就是这么测出 -10px 的假值的）。
        /// 单一台阶 + 梯度质心法可以给出稳定、可测亚像素的位移量。
        /// </summary>
        private static System.Windows.Media.Brush CreateTestStepBrush()
        {
            var lgb = new System.Windows.Media.LinearGradientBrush
            {
                StartPoint = new System.Windows.Point(0, 0),
                EndPoint = new System.Windows.Point(1, 0),
                MappingMode = System.Windows.Media.BrushMappingMode.RelativeToBoundingBox
            };
            // 台阶落在元素宽度的 5.5%~7.5%（元素=底栏+左右各24px，所以对应玻璃左边缘内侧约 1~10px）
            lgb.GradientStops.Add(new System.Windows.Media.GradientStop(System.Windows.Media.Colors.Black, 0.0));
            lgb.GradientStops.Add(new System.Windows.Media.GradientStop(System.Windows.Media.Colors.Black, 0.055));
            lgb.GradientStops.Add(new System.Windows.Media.GradientStop(System.Windows.Media.Colors.White, 0.075));
            lgb.GradientStops.Add(new System.Windows.Media.GradientStop(System.Windows.Media.Colors.White, 1.0));
            return lgb;
        }

        /// <summary>
        /// 在指定通道上求 |dI/dx| 的质心（水平像素坐标）。
        /// 单一台阶时，质心就是"那条边在哪儿"，对高斯模糊鲁棒（模糊对称，不移动质心）。
        /// </summary>
        private static double GradientCentroid(byte[] px, int stride, int ch, int x0, int y0, int x1, int y1)
        {
            double num = 0, den = 0;
            for (int y = y0; y < y1; y++)
            {
                int row = y * stride;
                for (int x = x0; x < x1 - 1; x++)
                {
                    double g = Math.Abs(px[row + (x + 1) * 4 + ch] - px[row + x * 4 + ch]);
                    num += g * x;
                    den += g;
                }
            }
            return den <= 0 ? 0 : num / den;
        }

        /// <summary>
        /// 合成台阶图模式自检：逐通道测量"那条边移动了多少像素"。
        /// 这是唯一能"不用眼睛"确认折射方向、量级与色散的办法。
        /// </summary>
        private void DebugNavLensPatternTest(double strength)
        {
            if (NavBarGlass == null || NavBarLensFx == null || NavBarClipRoot == null)
            {
                AppendDebugOutput("[错误] 底栏未初始化");
                return;
            }

            var savedBackground = NavBarGlass.Background;
            double savedAberr = NavBarLensFx.AberrationPx;
            try
            {
                // 几何参数先按正式设置刷一次；之后【绝对不要】再调用 UpdateNavBarLensParams()，
                // 因为它会按设置把 Strength / RimBoost / RefractStrength 覆盖回去，
                // 那样 A/B 两组就变成同一种渲染、测出来永远是 0（上一版自检就是这么错的）。
                UpdateNavBarLensParams();

                NavBarGlass.Background = CreateTestStepBrush();
                FlushRenderPass();

                double w = NavBarClipRoot.ActualWidth, h = NavBarClipRoot.ActualHeight;

                // A: 关折射（只改 DP，不碰几何）
                NavBarLensFx.RimBoost = 0.0;
                NavBarLensFx.AberrationPx = 0.0;
                NavBarLensFx.Strength = 0.0;
                FlushRenderPass();
                var off = RenderNavBarToBitmap(w, h);

                // B: 纯折射（无色散、无柔光，专测位移方向与量级）
                NavBarLensFx.RefractStrength = 16.0 * strength;
                NavBarLensFx.Strength = 1.0;
                FlushRenderPass();
                var refractOnly = RenderNavBarToBitmap(w, h);

                // C: 折射 + 色散
                NavBarLensFx.AberrationPx = 1.2;
                FlushRenderPass();
                var withAberr = RenderNavBarToBitmap(w, h);

                int s1, s2, s3;
                byte[] pOff = ReadBgra(off, out s1);
                byte[] pRef = ReadBgra(refractOnly, out s2);
                byte[] pAbr = ReadBgra(withAberr, out s3);

                int iw = withAberr.PixelWidth, ih = withAberr.PixelHeight;
                int cy0 = ih / 2 - 12, cy1 = ih / 2 + 12;

                // ★ 坐标基准（很容易搞错，这里写明）：
                //   1) 台阶画在 NavBarGlass 上、按元素宽度 458 的 5.5%~7.5% 定位 => 元素 x ≈ 25~34
                //   2) 而渲染图是"被 Clip 过的 410x64"，x=0 就是玻璃左边缘（元素 x=24）
                //      => 台阶在渲染图里位于 x ≈ 1~10
                //   3) 底栏正文内容（按钮/文字）是另一层，从 x≈12 才开始
                //   所以测量窗口取 [0,14]，正好只框住台阶、不碰内容。
                int wx0 = 0, wx1 = Math.Min(iw, 14);

                double gOff = GradientCentroid(pOff, s1, 1, wx0, cy0, wx1, cy1);
                double gRef = GradientCentroid(pRef, s2, 1, wx0, cy0, wx1, cy1);
                double refractShift = gRef - gOff;   // 正=内容右移, 负=内容左移(向左=向外放大)

                // 色散：R/G/B 各自的边位置（都在"折射+色散"那张图上分别量，再互相比较）
                double cR = GradientCentroid(pAbr, s3, 2, wx0, cy0, wx1, cy1);
                double cG = GradientCentroid(pAbr, s3, 1, wx0, cy0, wx1, cy1);
                double cB = GradientCentroid(pAbr, s3, 0, wx0, cy0, wx1, cy1);

                // 中心区应当完全不位移（台阶不在中心，这里量的是"有没有意外位移"）
                double cOffMid = GradientCentroid(pOff, s1, 1, iw / 2 - 30, cy0, iw / 2 + 30, cy1);
                double cRefMid = GradientCentroid(pRef, s2, 1, iw / 2 - 30, cy0, iw / 2 + 30, cy1);

                // 理论预期：位移 = RefractStrength * edge，edge 取台阶处 depth≈2~8px 的平均
                double edgeAvg = (Math.Pow(1.0 - 2.0 / 21.0, 2) + Math.Pow(1.0 - 8.0 / 21.0, 2)) / 2.0;
                double theoShift = 16.0 * strength * edgeAvg;

                SaveBitmapPng(off, "底栏台阶_关折射.png");
                SaveBitmapPng(refractOnly, "底栏台阶_开折射.png");
                SaveBitmapPng(withAberr, "底栏台阶_折射加色散.png");

                AppendDebugOutput($"[台阶自检] 底栏 {iw}x{ih}px, 强度={strength:F2}, 台阶窗口 x∈[{wx0},{wx1}]");
                AppendDebugOutput($"[台阶自检] ① 边缘位移 = {refractShift:F2} px  (理论 -{theoShift:F1}px; 负=内容向外=透镜放大)");
                AppendDebugOutput($"[台阶自检] ② 中心位移 = {(cRefMid - cOffMid):F2} px  (期望 ≈ 0)");
                AppendDebugOutput($"[台阶自检] ③ 色散边位置 R={cR:F2}, G={cG:F2}, B={cB:F2}  -> R-G={cR - cG:F2}px, B-G={cB - cG:F2}px (期望两者反号)");

                Services.Logger.Instance.Info(string.Format(
                    "[NavLens台阶自检] 尺寸={0}x{1}, 强度={2:F2}, 边缘位移={3:F2}px(理论-{4:F2}), 中心位移={5:F2}px, 色散R-G={6:F2}px, B-G={7:F2}px",
                    iw, ih, strength, refractShift, theoShift, (cRefMid - cOffMid), (cR - cG), (cB - cG)));
            }
            catch (Exception ex)
            {
                AppendDebugOutput("[错误] 条纹自检失败: " + ex.Message);
                Services.Logger.Instance.Error("[NavLens条纹自检] 失败", ex);
            }
            finally
            {
                NavBarGlass.Background = savedBackground;
                NavBarLensFx.AberrationPx = savedAberr;
                UpdateNavBarLensParams();
                FlushRenderPass();
            }
        }

        private void DebugNavLensReport(string strengthArg)
        {
            try
            {
                if (NavBarClipRoot == null || NavBarLens == null || NavBarLensFx == null)
                {
                    AppendDebugOutput("[错误] 底栏未初始化");
                    return;
                }

                if (!string.IsNullOrEmpty(strengthArg))
                {
                    double v;
                    if (double.TryParse(strengthArg, out v) && _settings != null)
                    {
                        _settings.NavBarLiquidGlassStrength = Math.Max(0, Math.Min(1, v));
                        ApplyNavBarLens(true, "自检临时开启");
                        AppendDebugOutput($"[信息] 临时把强度设为 {_settings.NavBarLiquidGlassStrength:F2}");
                    }
                }

                var savedEffect = NavBarLens.Effect;
                bool attachedHere = false;

                // 先确保桌面截图已就绪：否则底栏背景是一片纯色，折射再怎么算也看不出变化，
                // 导出的对比图会毫无意义（上一版自检"边缘差异只有 0.9/255"就是这个原因）。
                try
                {
                    if (_glassyManager != null)
                    {
                        _glassyManager.RefreshBackdrop();
                        FlushRenderPass();
                    }
                }
                catch (Exception ex0)
                {
                    Services.Logger.Instance.Debug("[NavLens自检] 刷新背景失败: " + ex0.Message);
                }
                if (NavBarLens.Effect != NavBarLensFx)
                {
                    NavBarLens.Effect = NavBarLensFx;
                    attachedHere = true;
                    UpdateNavBarLensParams();
                }
                if (!navLensActive)
                {
                    navLensActive = true;
                    UpdateNavBarLensParams();
                }

                double w = NavBarClipRoot.ActualWidth, h = NavBarClipRoot.ActualHeight;
                if (w < 32 || h < 16)
                {
                    AppendDebugOutput($"[错误] 底栏尺寸异常: {w:F0}x{h:F0}");
                    return;
                }

                // A: 关折射（Strength=0 → 着色器退化为恒等变换，模糊层不变，是干净的对照组）
                NavBarLensFx.Strength = 0.0;
                FlushRenderPass();
                var bmpOff = RenderNavBarToBitmap(w, h);
                double savedStrength = _settings != null ? _settings.NavBarLiquidGlassStrength : 0.35;

                // B: 开折射（按设置强度）
                NavBarLensFx.Strength = savedStrength > 0 ? 1.0 : 0.0;
                UpdateNavBarLensParams();
                FlushRenderPass();
                var bmpOn = RenderNavBarToBitmap(w, h);

                int strideOff, strideOn;
                byte[] pxOff = ReadBgra(bmpOff, out strideOff);
                byte[] pxOn = ReadBgra(bmpOn, out strideOn);

                int iw = bmpOn.PixelWidth, ih = bmpOn.PixelHeight;
                int band = 24;                                   // 24px 边缘带（略大于折射带宽 21px）
                if (iw < band * 3 || ih < band * 3)
                {
                    AppendDebugOutput($"[警告] 底栏太小({iw}x{ih})，无法分区比较");
                    band = Math.Max(4, Math.Min(iw, ih) / 6);
                }

                // 中心区（四边各内缩 band）
                double centerDiff = MeanAbsDiff(pxOff, pxOn, strideOff,
                    0 + band, 0 + band, iw - band, ih - band);

                // 边缘带（四边各 band 宽，减去中心区）
                long rimSum = 0; long rimN = 0;
                Action<int, int, int, int> addRim = (x0, y0, x1, y1) =>
                {
                    for (int y = y0; y < y1; y++)
                    {
                        int row = y * strideOff;
                        for (int x = x0; x < x1; x++)
                        {
                            int i = row + x * 4;
                            rimSum += Math.Abs(pxOff[i] - pxOn[i]);
                            rimSum += Math.Abs(pxOff[i + 1] - pxOn[i + 1]);
                            rimSum += Math.Abs(pxOff[i + 2] - pxOn[i + 2]);
                            rimN += 3;
                        }
                    }
                };
                addRim(0, 0, iw, band);                    // 上
                addRim(0, ih - band, iw, ih);              // 下
                addRim(0, band, band, ih - band);          // 左
                addRim(iw - band, band, iw, ih - band);    // 右
                double rimDiff = rimN == 0 ? 0 : (double)rimSum / rimN;

                SaveBitmapPng(bmpOff, "底栏对比_关折射.png");
                SaveBitmapPng(bmpOn, "底栏对比_开折射.png");

                AppendDebugOutput($"[结果] 底栏 {iw}x{ih}px, 强度={savedStrength:F2}, 边缘带宽={band}px");
                AppendDebugOutput($"[结果] ① 中心区平均差异 = {centerDiff:F3} / 255   (期望 ≈ 0，证明无整片偏色)");
                AppendDebugOutput($"[结果] ② 边缘带平均差异 = {rimDiff:F3} / 255   (期望明显 > ①，证明边缘在折射)");
                AppendDebugOutput($"[结果] 注意: ②的大小取决于「背后画面有多少细节」。背后是纯色时折射看不出来是物理必然(位移纯色仍是纯色)，");
                AppendDebugOutput($"[结果]       此时靠边缘柔光体现玻璃感。要看色散量请用台阶自检里逐通道的数字。");
                AppendDebugOutput($"[结果] 已导出 底栏对比_关折射.png / 底栏对比_开折射.png 到程序目录，可自行对比");

                Services.Logger.Instance.Info(string.Format(
                    "[NavLens自检] 尺寸={0}x{1}, 强度={2:F2}, 中心差异={3:F3}, 边缘差异={4:F3}",
                    iw, ih, savedStrength, centerDiff, rimDiff));

                // 再做一次"合成条纹图"自检：把背景换成灰阶竖条，逐通道精确测量位移与色散
                DebugNavLensPatternTest(savedStrength > 0 ? savedStrength : 0.35);

                NavBarLensFx.Strength = savedStrength > 0 ? 1.0 : 0.0;
                if (attachedHere) NavBarLens.Effect = savedEffect;
                UpdateNavBarLensParams();
            }
            catch (Exception ex)
            {
                AppendDebugOutput("[错误] 自检失败: " + ex.Message);
                Services.Logger.Instance.Error("[NavLens自检] 失败", ex);
            }
        }

        #endregion
    }
}