using System.Windows;
using System.Windows.Media;

namespace ChatRoom
{
    /// <summary>
    /// 明暗两套配色 + 运行时切换。
    ///
    /// 实现方式（以及为什么这样选）：
    ///   · XAML 里一律用 <c>{DynamicResource 键名}</c>；切主题时把新画刷写进
    ///     <c>Application.Current.Resources</c>，DynamicResource 会自动重新解析 ⇒ **不需要重建窗口**。
    ///   · <c>Application.Resources</c> 会把 Freezable **自动冻结**（主程序 JiYuKiller 上踩过这个坑：
    ///     冻结后 BeginAnimation 必抛异常）。所以这里每次切换都是**新建画刷实例**再写入，
    ///     绝不去修改已冻结对象的属性。
    ///
    /// 配色结构照 Telegram 的组织方式（窗口底/侧栏/消息区/气泡/分隔线/次要文字/服务胶囊/列表选中态），
    /// 色值是我们自己定的；唯一直接取自官方源码的是深色模式下"自己发出"的气泡 <c>#2B5278</c>
    /// （tdesktop：Telegram\SourceFiles\window\themes\window_themes_embedded.cpp）。
    /// </summary>
    public static class Theme
    {
        /// <summary>当前是否暗色（供设置窗口回显）</summary>
        public static bool IsDark { get; private set; }

        /// <summary>浅色 / 深色 两套值：键名 -> (浅色, 深色)</summary>
        private static readonly string[,] Palette = new string[,]
        {
            //  键名                浅色        深色
            { "WindowBg",         "#FFFFFFFF", "#FF17212B" },   // 窗口/面板底
            { "WindowBorder",     "#FFD1D1D6", "#FF0B1118" },   // 窗口外描边
            { "SideBg",           "#FFFFFFFF", "#FF17212B" },   // 侧栏底
            { "HeaderBg",         "#FFFFFFFF", "#FF17212B" },   // 顶栏底
            { "ChatBg",           "#FFF0F2F5", "#FF0E1621" },   // 消息区底（与白色来消息气泡拉开对比）
            { "ComposerBg",       "#FFFFFFFF", "#FF17212B" },   // 输入区底
            { "Divider",          "#FFE4E6EB", "#FF101921" },   // 分隔线
            { "TextPrimary",      "#FF1C1C2E", "#FFFFFFFF" },   // 主文字
            { "TextSecondary",    "#FF8E8E93", "#FF7D8B99" },   // 次要文字
            { "Accent",           "#FF5B8DEF", "#FF5B8DEF" },   // 主色（沿用原有品牌色）
            { "AccentFg",         "#FFFFFFFF", "#FFFFFFFF" },   // 主色上的文字
            { "InputBg",          "#FFFFFFFF", "#FF0E1621" },   // 输入框底
            { "InputBorder",      "#FFD1D1D6", "#FF2B3A4A" },   // 输入框描边
            { "RowHoverBg",       "#FFF0F2F5", "#FF202B36" },   // 列表悬停
            { "RowSelectedBg",    "#FFE7F0FE", "#FF2B5278" },   // 列表选中
            { "BubbleInBg",       "#FFFFFFFF", "#FF182533" },   // 收到：气泡底
            { "BubbleInBorder",   "#FFE4E6EB", "#FF2B3A4A" },   // 收到：气泡描边（白色气泡压在浅灰底上必须靠它）
            { "BubbleInFg",       "#FF000000", "#FFFFFFFF" },   // 收到：文字
            { "BubbleOutBg",      "#FF5B8DEF", "#FF2B5278" },   // 发出：气泡底（深色值取自官方源码）
            { "BubbleOutBorder",  "#FF5B8DEF", "#FF2B5278" },   // 发出：气泡描边
            { "BubbleOutFg",      "#FFFFFFFF", "#FFFFFFFF" },   // 发出：文字
            { "ServiceBg",        "#FFE8E8ED", "#FF1E2C3A" },   // 服务消息（上线/离线/日期）胶囊底
            { "ServiceFg",        "#FF8E8E93", "#FF8B9AA8" },   // 服务消息文字
            { "TimeFg",           "#FF9A9AA0", "#FF7D8B99" },   // 气泡内时间戳
            // —— 按钮/滚动条（2026-09-18 加：默认 WPF 按钮模板是方角 + Aero 悬停，观感差） ——
            { "AccentHover",      "#FF4A7DE0", "#FF4A7DE0" },   // 主色按钮悬停
            { "AccentPressed",    "#FF3C69BE", "#FF3C69BE" },   // 主色按钮按下
            { "GhostHover",       "#14000000", "#22FFFFFF" },   // 图标按钮悬停底
            { "GhostPressed",     "#22000000", "#38FFFFFF" },   // 图标按钮按下底
            { "ScrollThumb",      "#40000000", "#40FFFFFF" },   // 细滚动条滑块
            // —— 玻璃拟态（豆包 Q2 规格：卡片/侧栏/弹窗 85% 不透明 + 1px 主色 20% 微光边框 + 圆角 12）——
            { "GlassPanel",       "#D9FFFFFF", "#D917212B" },   // 85% 不透明面板
            { "GlassBorder",      "#335B8DEF", "#335B8DEF" },   // 主色 20% 微光边框
            { "GlassPanelHover",  "#E6FFFFFF", "#E61C2836" },   // 玻璃卡片悬停
            // —— 文件分享（豆包需求 #6）——
            { "WarnFg",           "#FFD93025", "#FFFF6B6B" },   // 警告红字（可执行文件、超限提示）
            { "FileCardBg",       "#0F000000", "#14FFFFFF" },   // 文件卡片底（与气泡拉开一点层次）
            { "FileCardBorder",   "#24000000", "#2BFFFFFF" },   // 文件卡片描边
        };

        public static void Apply(bool dark)
        {
            IsDark = dark;
            ResourceDictionary r = Application.Current.Resources;
            for (int i = 0; i < Palette.GetLength(0); i++)
            {
                // 每次都新建画刷：不去动可能已被冻结的旧实例
                r[Palette[i, 0]] = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString(dark ? Palette[i, 2] : Palette[i, 1]));
            }
        }

        /// <summary>取当前主题下的画刷（气泡由代码着色时用；取不到就返回透明，不抛异常）</summary>
        public static Brush Get(string key)
        {
            object o = Application.Current != null ? Application.Current.TryFindResource(key) : null;
            return o as Brush ?? Brushes.Transparent;
        }

        /// <summary>取当前主题下的颜色字符串（例如把图片占位文字着色）</summary>
        public static Color GetColor(string key)
        {
            SolidColorBrush b = Get(key) as SolidColorBrush;
            return b != null ? b.Color : Colors.Transparent;
        }
    }
}
