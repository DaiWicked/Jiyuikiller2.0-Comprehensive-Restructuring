using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using ChatRoom.Models;

namespace ChatRoom
{
    /// <summary>
    /// 主窗口的动效（豆包《下一阶段任务》动效剩余 4 项）。
    ///
    /// ★ 统一约定：全部先问 <see cref="Anim.Enabled"/>（附加属性，由设置里的"界面动效"开关灌入）。
    ///   关闭时**直接跳过整段逻辑**（不是把时长设成 0）—— 这样连元素都不会被创建/显示，
    ///   既不会闪一下，也不会有多余渲染开销。
    /// </summary>
    public partial class MainWindow
    {
        // ==================== 动效 1：发送成功光点扩散 ====================

        /// <summary>
        /// 从「发送」按钮位置扩出一圈半透明光圈，300ms 淡出。
        /// 位置用 TranslatePoint 实时算，不写死坐标 —— 改布局/换字号也不会错位。
        /// </summary>
        private void PlaySendRipple()
        {
            if (!Anim.Enabled || SendRipple == null || RootGrid == null || BtnSend == null) return;
            try
            {
                Point p = BtnSend.TranslatePoint(
                    new Point(BtnSend.ActualWidth / 2, BtnSend.ActualHeight / 2), RootGrid);

                SendRipple.Margin = new Thickness(
                    p.X - SendRipple.Width / 2, p.Y - SendRipple.Height / 2, 0, 0);
                SendRipple.Visibility = Visibility.Visible;

                TimeSpan dur = Anim.Span(300);
                SendRipple.BeginAnimation(OpacityProperty, new DoubleAnimation(0.45, 0, dur));

                var grow = new DoubleAnimation(0.5, 2.4, dur)
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                grow.Completed += (s, e) =>
                {
                    // 播完藏起来：不留一个不可见的元素参与命中测试
                    try { SendRipple.Visibility = Visibility.Collapsed; } catch { }
                };
                if (RippleScale != null)
                {
                    RippleScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
                    RippleScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
                }
            }
            catch { }
        }

        // ==================== 动效 2：对方上线，侧栏行 3s 渐入 ====================

        /// <summary>刚上线的 IP（只对这些行播放渐入，避免"每次刷新所有行都在闪"）</summary>
        private readonly Dictionary<string, DateTime> _justJoined = new Dictionary<string, DateTime>();

        /// <summary>由 OnUserJoined 调用：标记这个 IP 是新上线的</summary>
        private void MarkJustJoined(string ip)
        {
            if (string.IsNullOrEmpty(ip)) return;
            try
            {
                lock (_justJoined) { _justJoined[ip] = DateTime.Now; }
            }
            catch { }
        }

        /// <summary>侧栏行容器生成时：若是"刚上线的人"，播放 3 秒渐入</summary>
        private void UserRow_Loaded(object sender, RoutedEventArgs e)
        {
            if (!Anim.Enabled) return;
            var row = sender as ListBoxItem;
            if (row == null) return;
            var user = row.DataContext as ChatUser;
            if (user == null || string.IsNullOrEmpty(user.IP)) return;

            bool fresh;
            lock (_justJoined)
            {
                DateTime t;
                fresh = _justJoined.TryGetValue(user.IP, out t) && (DateTime.Now - t).TotalSeconds <= 6;
                // 取出来就删：容器复用/刷新时会再次触发 Loaded，不删就会反复渐入
                _justJoined.Remove(user.IP);
            }
            if (!fresh) return;

            try { row.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Anim.Span(3000))); }
            catch { }
            SyncMarquees();   // 行是新出现的，顺便把跑马灯状态对齐
        }

        // ==================== 动效 3：收到图片，模糊 → 清晰 0.2s ====================

        /// <summary>
        /// 收到的新图片先以 BlurEffect.Radius=8 出现，0.2s 内动画到 0（"对焦"感）。
        /// 只对**本次收到**的图片播（历史加载的、自己发的都不播）。
        /// 动画结束把 Effect 摘掉：ShaderEffect 会让元素走离屏渲染，留着是白花性能。
        /// </summary>
        private void BubbleImage_Loaded(object sender, RoutedEventArgs e)
        {
            if (!Anim.Enabled) return;
            var img = sender as Image;
            if (img == null) return;
            var item = img.DataContext as ChatMessageItem;
            if (item == null || item.IsHistory || item.Kind != BubbleKind.Incoming) return;

            try
            {
                var blur = new BlurEffect
                {
                    Radius = 8,
                    KernelType = KernelType.Gaussian,
                    RenderingBias = RenderingBias.Performance   // 200ms 的一次性动画，别用 Quality 换画质
                };
                img.Effect = blur;

                var a = new DoubleAnimation(8, 0, Anim.Span(200))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                a.Completed += (s, e2) => { try { img.Effect = null; } catch { } };
                blur.BeginAnimation(BlurEffect.RadiusProperty, a);
            }
            catch { }
        }

        // ==================== 动效 4：未读行底部 2px 跑马灯 ====================

        /// <summary>正在滚动跑马灯的行（避免每次同步都重复 BeginAnimation）</summary>
        private readonly HashSet<ListBoxItem> _marqueeRunning = new HashSet<ListBoxItem>();

        /// <summary>
        /// 把侧栏各行跑马灯状态对齐：该滚的滚、不该滚的停。
        /// 为什么放在同步循环里：ChatUser 没有 INPC，未读数是外部改的，
        /// 只在 Loaded 时启停的话，"收到消息后才出现未读"这种情况永远不会开始滚。
        /// 动效关闭时不启动滚动 —— 那条 2px 主色条仍然按未读显示（静态），未读提示不丢。
        /// </summary>
        private void SyncMarquees()
        {
            if (UserList == null) return;
            try
            {
                var seen = new HashSet<ListBoxItem>();
                for (int i = 0; i < UserList.Items.Count; i++)
                {
                    var item = UserList.ItemContainerGenerator.ContainerFromIndex(i) as ListBoxItem;
                    if (item == null) continue;                       // 未实现（虚拟化外）的行跳过
                    seen.Add(item);
                    var user = UserList.Items[i] as ChatUser;
                    bool need = user != null && user.Unread > 0;

                    Border bar = item.Template != null
                        ? item.Template.FindName("unreadMarquee", item) as Border
                        : null;
                    if (bar == null) continue;

                    var tr = bar.RenderTransform as TranslateTransform;
                    if (tr == null) continue;

                    if (need && Anim.Enabled)
                    {
                        if (_marqueeRunning.Contains(item)) continue;  // 已经在滚
                        double w = Math.Max(40.0, item.ActualWidth - 24);
                        var a = new DoubleAnimation(-60, w, TimeSpan.FromSeconds(1.6))
                        {
                            RepeatBehavior = RepeatBehavior.Forever
                        };
                        tr.BeginAnimation(TranslateTransform.XProperty, a);
                        _marqueeRunning.Add(item);
                    }
                    else if (_marqueeRunning.Remove(item))
                    {
                        tr.BeginAnimation(TranslateTransform.XProperty, null);
                        tr.X = 0;
                    }
                }

                // ★ 第三轮审查（这条是我自己引入的）：行被移除/容器被回收后，
                //   _marqueeRunning 会一直攥着那些 ListBoxItem —— 既漏内存，
                //   又让"已经在滚"的判断对**复用出来的新行**失效（新行被当成旧的，永远不滚）。
                if (_marqueeRunning.Count > seen.Count)
                    _marqueeRunning.RemoveWhere(it => !seen.Contains(it));
            }
            catch { }
        }
    }
}
