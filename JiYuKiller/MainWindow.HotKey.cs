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
        #region 全局快捷键

        private void RegisterGlobalHotKeys()
        {
            Services.Logger.Instance.FunctionCall("RegisterGlobalHotKeys");

            _hwndSource = System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(this).Handle);
            if (_hwndSource != null)
            {
                _hwndSource.AddHook(HwndHook);
            }

            IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;

            // 快捷键现在可配置：编码沿用设置里既有的方式（高字节修饰键 / 低字节虚拟键码），
            // 与 Win32 RegisterHotKey 的 MOD_* 常量一致 —— WPF 的 ModifierKeys 枚举值恰好就等于 MOD_*。
            int pack1 = (_settings != null && _settings.HotKeyFakeFull != 0) ? _settings.HotKeyFakeFull : 1606;
            int pack2 = (_settings != null && _settings.HotKeyShowHide != 0) ? _settings.HotKeyShowHide : 1604;
            uint mod1 = (uint)((pack1 >> 8) & 0xF); uint vk1 = (uint)(pack1 & 0xFF);
            uint mod2 = (uint)((pack2 >> 8) & 0xF); uint vk2 = (uint)(pack2 & 0xFF);

            bool result1 = vk1 != 0 && RegisterHotKey(hwnd, HOTKEY_FAKEFULL, mod1, vk1);
            int err1 = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            Services.Logger.Instance.Info("[HotKey] 注册紧急全屏 " + HotKeyToText(pack1) + ": " + (vk1 == 0 ? "未设置, 跳过" : (result1 ? "成功" : "失败")));

            bool result2 = vk2 != 0 && RegisterHotKey(hwnd, HOTKEY_SHOWHIDE, mod2, vk2);
            int err2 = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            Services.Logger.Instance.Info("[HotKey] 注册显示/隐藏窗口 " + HotKeyToText(pack2) + ": " + (vk2 == 0 ? "未设置, 跳过" : (result2 ? "成功" : "失败")));

            // vk==0 表示用户故意留空，不该报"注册失败"
            ReportHotKeyWarning(vk1 == 0 || result1, err1, HotKeyToText(pack1) + "（紧急全屏）",
                                vk2 == 0 || result2, err2, HotKeyToText(pack2) + "（显示/隐藏窗口）");
        }

        /// <summary>
        /// 热键注册失败时在「高级设置 → 快捷键」卡片里给出可见提示。
        /// 原来只在日志里写一行 INFO，用户界面上完全看不出快捷键没生效。
        /// </summary>
        private void ReportHotKeyWarning(bool ok1, int err1, string name1, bool ok2, int err2, string name2)
        {
            if (TextHotKeyWarn == null) return;

            if (ok1 && ok2)
            {
                TextHotKeyWarn.Text = "";
                TextHotKeyWarn.Visibility = Visibility.Collapsed;
                return;
            }

            var sb = new System.Text.StringBuilder("注意：");
            if (!ok1) sb.Append(DescribeHotKeyFailure(name1, err1));
            if (!ok1 && !ok2) sb.Append("；");
            if (!ok2) sb.Append(DescribeHotKeyFailure(name2, err2));
            sb.Append("。该快捷键不会生效；多为其它程序已占用同一组合键，关闭占用它的程序后重启本软件即可。");

            TextHotKeyWarn.Text = sb.ToString();
            TextHotKeyWarn.Visibility = Visibility.Visible;
            Services.Logger.Instance.Warn("[HotKey] " + sb.ToString());
        }

        /// <summary>把 RegisterHotKey 的失败原因翻译成人话（1409 = ERROR_HOTKEY_ALREADY_REGISTERED）</summary>
        private static string DescribeHotKeyFailure(string name, int err)
        {
            string reason = (err == 1409) ? "已被其它程序占用" : (err == 0 ? "原因未知" : "系统错误码 " + err);
            return $"{name} 注册失败（{reason}）";
        }

        /// <summary>打包的快捷键编码 -> 可读文本（高字节修饰键 / 低字节虚拟键码）</summary>
        private static string HotKeyToText(int packed)
        {
            int vk = packed & 0xFF;
            if (vk == 0) return "未设置";

            int mods = (packed >> 8) & 0xF;
            var sb = new System.Text.StringBuilder();
            if ((mods & MOD_CONTROL) != 0) sb.Append("Ctrl+");
            if ((mods & MOD_ALT) != 0) sb.Append("Alt+");
            if ((mods & MOD_SHIFT) != 0) sb.Append("Shift+");
            if ((mods & 0x8) != 0) sb.Append("Win+");

            sb.Append(HotKeyKeyName(KeyInterop.KeyFromVirtualKey(vk)));
            return sb.ToString();
        }

        private static string HotKeyKeyName(Key k)
        {
            if (k >= Key.D0 && k <= Key.D9) return ((char)('0' + (k - Key.D0))).ToString();
            if (k >= Key.NumPad0 && k <= Key.NumPad9) return "小键盘" + (k - Key.NumPad0);
            return k.ToString();
        }

        /// <summary>把设置里的快捷键显示到输入框（Tag 存打包值，保存时读回）</summary>
        private void ApplyHotKeysToUI()
        {
            if (_settings == null) return;
            if (TextHotKeyFakeFull != null)
            {
                TextHotKeyFakeFull.Tag = _settings.HotKeyFakeFull;
                TextHotKeyFakeFull.Text = HotKeyToText(_settings.HotKeyFakeFull);
            }
            if (TextHotKeyShowHide != null)
            {
                TextHotKeyShowHide.Tag = _settings.HotKeyShowHide;
                TextHotKeyShowHide.Text = HotKeyToText(_settings.HotKeyShowHide);
            }
        }

        /// <summary>
        /// 快捷键输入框：点进去后直接按下组合键即可。
        /// 必须带修饰键 —— 不带修饰键的全局热键会把那个键从所有程序手里抢走。
        /// </summary>
        private void HotKeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            e.Handled = true;   // 不要让它变成普通文本输入
            try
            {
                Key key = (e.Key == Key.System) ? e.SystemKey : e.Key;

                // 只按修饰键本身（或 Esc）不算一个完整组合
                if (key == Key.LeftCtrl || key == Key.RightCtrl || key == Key.LeftAlt || key == Key.RightAlt ||
                    key == Key.LeftShift || key == Key.RightShift || key == Key.LWin || key == Key.RWin ||
                    key == Key.None || key == Key.Escape)
                {
                    return;
                }

                int mods = (int)Keyboard.Modifiers & 0xF;
                if (mods == 0)
                {
                    TextHotKeyWarn.Text = "快捷键必须包含 Ctrl / Alt / Shift / Win 中的至少一个修饰键。";
                    TextHotKeyWarn.Visibility = Visibility.Visible;
                    return;
                }

                int vk = KeyInterop.VirtualKeyFromKey(key);
                if (vk <= 0) return;

                var box = sender as System.Windows.Controls.TextBox;
                if (box == null) return;

                int packed = (mods << 8) | (vk & 0xFF);
                box.Tag = packed;
                box.Text = HotKeyToText(packed);

                TextHotKeyWarn.Text = "";
                TextHotKeyWarn.Visibility = Visibility.Collapsed;
                Services.Logger.Instance.Info("[HotKey] 界面选择了新快捷键: " + box.Text + "（点保存设置后立刻生效）");
            }
            catch (Exception ex)
            {
                Services.Logger.Instance.Debug("[HotKey] 捕获快捷键失败: " + ex.Message);
            }
        }

        private void UnregisterGlobalHotKeys()
        {
            Services.Logger.Instance.FunctionCall("UnregisterGlobalHotKeys");

            if (_hwndSource != null)
            {
                _hwndSource.RemoveHook(HwndHook);
                _hwndSource = null;
            }

            IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            UnregisterHotKey(hwnd, HOTKEY_FAKEFULL);
            UnregisterHotKey(hwnd, HOTKEY_SHOWHIDE);
            Services.Logger.Instance.Info("[HotKey] 已注销全部快捷键");
        }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY)
            {
                int id = wParam.ToInt32();
                if (id == HOTKEY_FAKEFULL)
                {
                    Services.Logger.Instance.ButtonClick("快捷键-紧急全屏", "GlobalHotKey");
                    bool result = _controller.SwitchFakeFull();
                    Services.Logger.Instance.Info("[HotKey] 紧急全屏切换: " + (result ? "已全屏" : "已恢复"));
                    handled = true;
                }
                else if (id == HOTKEY_SHOWHIDE)
                {
                    Services.Logger.Instance.ButtonClick("快捷键-显示/隐藏", "GlobalHotKey");
                    if (this.Visibility == Visibility.Visible)
                    {
                        HideToTray();
                    }
                    else
                    {
                        ShowMainWindow();
                    }
                    handled = true;
                }
            }
            else if (msg == WM_COPYDATA)
            {
                // 接收DLL回调消息 (对应原项目VSendMessageBack)
                try
                {
                    COPYDATASTRUCT cds = (COPYDATASTRUCT)Marshal.PtrToStructure(lParam, typeof(COPYDATASTRUCT));
                    // 必须校验长度：源缓冲区若不是 NUL 结尾，PtrToStringUni(IntPtr) 会越界读，
                    // 可能抛不可捕获的 AccessViolationException（同用户任意进程都能发这条消息）。
                    if (cds.lpData != IntPtr.Zero && cds.cbData > 0 && cds.cbData <= 4096)
                    {
                        string message = Marshal.PtrToStringUni(cds.lpData, cds.cbData / 2);
                        if (!string.IsNullOrEmpty(message))
                        {
                            Services.Logger.Instance.Info("[DLL回调] " + message);
                            HandleDllCallback(message);
                        }
                    }
                    handled = true;
                }
                catch (Exception ex)
                {
                    Services.Logger.Instance.Error("[DLL回调] 处理WM_COPYDATA异常", ex);
                }
            }
            return IntPtr.Zero;
        }

        /// <summary>
        /// 处理DLL回调消息 (对应原项目hkb:系列消息)
        /// </summary>
        /// <summary>
        /// 处理DLL回调消息 - 薄转发到JiYuController.HandleVirusCallback
        /// 真正的窗口操作在Controller中执行（对应参考实现 HandleMessageFromVirus）
        /// </summary>
        private void HandleDllCallback(string message)
        {
            try
            {
                _controller.HandleVirusCallback(message);
            }
            catch (Exception ex)
            {
                Services.Logger.Instance.Error("[DLL回调] HandleDllCallback异常", ex);
            }
        }

        #endregion
    }
}