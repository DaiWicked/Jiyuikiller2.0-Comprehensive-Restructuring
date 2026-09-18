using System;
using System.Windows;

namespace ChatRoom
{
    /// <summary>
    /// 动效总开关（豆包 Q7）：一个设置控制全部动效。
    ///
    /// ★ 走过的弯路（记录下来，别再踩）：我最初想在 App.xaml 里定义三个 Duration 资源，
    ///   让 XAML 里的动画 Duration 引用它们，关闭动效时把资源改成 0。
    ///   **这行不通**：WPF 的 Storyboard 必须能冻结（Seal），而时间线里不允许出现 DynamicResource，
    ///   结果是模板封套时抛 InvalidOperationException，程序直接起不来。
    ///
    /// 现在做法：一个**附加依赖属性** Anim.On（注册时带 Inherits=true，所以在窗口上设一次就向下继承），
    /// XAML 里用 MultiTrigger 同时判断"状态"和"Anim.On"：
    ///   · Anim.On=True  → 走进 EnterActions/ExitActions 的动画（有过渡）
    ///   · Anim.On=False → 走 Setter（瞬间到同一个终态，没有过渡）
    ///   两者终态一致，所以关掉动效不会出现"某个状态没设上"的问题。
    /// </summary>
    public static class Anim
    {
        public static readonly DependencyProperty OnProperty =
            DependencyProperty.RegisterAttached(
                "On", typeof(bool), typeof(Anim),
                new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.Inherits));

        public static bool GetOn(DependencyObject o) { return (bool)o.GetValue(OnProperty); }
        public static void SetOn(DependencyObject o, bool v) { o.SetValue(OnProperty, v); }

        /// <summary>当前设置里动效是否开启</summary>
        public static bool Enabled
        {
            get { try { return Models.ChatSettings.AnimationsOn; } catch { return true; } }
        }

        /// <summary>在窗口显示时调用一次：把设置里的开关灌进附加属性（子元素靠继承拿到）</summary>
        public static void ApplyTo(Window w)
        {
            if (w == null) return;
            try { SetOn(w, Enabled); } catch { }
        }

        /// <summary>给代码里写的动画取时长（关闭动效时是 0，动画瞬间到终态）</summary>
        public static TimeSpan Span(double ms)
        {
            return Enabled ? TimeSpan.FromMilliseconds(ms) : TimeSpan.Zero;
        }
    }
}
