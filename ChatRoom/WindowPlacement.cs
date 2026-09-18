using System;
using System.Windows;

namespace ChatRoom
{
    /// <summary>
    /// 把自绘无边框窗口夹回屏幕工作区内。
    ///
    /// 为什么需要：这些窗口都用 WindowStartupLocation="CenterOwner"，
    /// 而 WPF 的 CenterOwner **不会**把窗口限制在屏幕工作区内 ——
    /// 它只是"相对属主窗口居中"。主窗口一旦被拖到屏幕下半部，
    /// 居中算出来的对话框底部就会跑到屏幕外、或钻到任务栏后面，
    /// 用户看到的就是"按钮和部分文字被遮挡"（窗口本身没问题，是位置出去了）。
    ///
    /// 这里在窗口显示后主动夹一次：只做"超出就挪回来"，完全在屏幕内的窗口一个像素都不动。
    /// 多显示器用 WinForms 的 Screen.FromHandle 取"窗口当前所在那块屏"的工作区，
    /// 避免把副屏上的窗口硬拽回主屏。
    /// </summary>
    internal static class WindowPlacement
    {
        public static void ClampToWorkArea(Window w)
        {
            if (w == null) return;
            try
            {
                double wW = w.ActualWidth > 0 ? w.ActualWidth : w.Width;
                double wH = w.ActualHeight > 0 ? w.ActualHeight : w.Height;
                if (double.IsNaN(wW) || double.IsNaN(wH) || wW <= 0 || wH <= 0) return;

                double left, top, right, bottom;
                if (!TryGetWorkAreaDip(w, out left, out top, out right, out bottom)) return;

                // 窗口比工作区还高时放不下，就贴顶（至少保证标题/关闭键可见），
                // 而不是硬算 bottom-wH 把窗口推到屏幕上方外面去。
                if (wH > bottom - top) w.Top = top;
                else if (w.Top + wH > bottom) w.Top = bottom - wH;

                if (wW > right - left) w.Left = left;
                else if (w.Left + wW > right) w.Left = right - wW;

                if (w.Top < top) w.Top = top;
                if (w.Left < left) w.Left = left;
            }
            catch { }
        }

        /// <summary>取窗口所在显示器的工作区，并换算成 WPF 的 DIP（Left/Top 用的是 DIP）</summary>
        private static bool TryGetWorkAreaDip(Window w, out double left, out double top, out double right, out double bottom)
        {
            left = top = right = bottom = 0;

            double dpiX = 1.0, dpiY = 1.0;
            try
            {
                PresentationSource src = PresentationSource.FromVisual(w);
                if (src != null && src.CompositionTarget != null)
                {
                    dpiX = src.CompositionTarget.TransformToDevice.M11;
                    dpiY = src.CompositionTarget.TransformToDevice.M22;
                }
            }
            catch { }
            if (dpiX <= 0) dpiX = 1.0;
            if (dpiY <= 0) dpiY = 1.0;

            try
            {
                IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    // WorkingArea 是物理像素，除以 DPI 缩放才是 WPF 的 DIP
                    System.Drawing.Rectangle wa = System.Windows.Forms.Screen.FromHandle(hwnd).WorkingArea;
                    left = wa.Left / dpiX; top = wa.Top / dpiY;
                    right = wa.Right / dpiX; bottom = wa.Bottom / dpiY;
                    return true;
                }
            }
            catch { }

            // 兜底：主屏工作区（SystemParameters.WorkArea 本身就是 DIP）
            try
            {
                Rect wa = SystemParameters.WorkArea;
                left = wa.Left; top = wa.Top; right = wa.Right; bottom = wa.Bottom;
                return true;
            }
            catch { return false; }
        }
    }
}
