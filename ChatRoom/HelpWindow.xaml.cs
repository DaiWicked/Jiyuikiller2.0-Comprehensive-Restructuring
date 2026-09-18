using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ChatRoom
{
    /// <summary>
    /// 使用教程（豆包需求 #3）。
    ///
    /// 结构照搬主程序帮助页：左侧子导航 + 右侧内容面板互相切换（Visibility 切换，不重建控件）。
    /// 外壳沿用关于页/设置页那套玻璃拟态（85% 面板 + 1px 主色 20% 边框 + 圆角 12），
    /// 全部颜色走 DynamicResource，所以深色模式与主题切换都跟着走。
    ///
    /// ★ 内容只写"程序真的会这么做"的事：写帮助页时逐条对照过代码
    ///   （点击图片是 Process.Start 交给系统看图程序、右键菜单只有复制/引用/删除/导出、
    ///    文件分享尚未实现所以不写）。宁可少写，不能写没做的功能。
    /// </summary>
    public partial class HelpWindow : Window
    {
        public HelpWindow()
        {
            InitializeComponent();
            ShowSection("intro");
            // 同设置窗：CenterOwner 不限制工作区，超出屏幕的部分用户就看不到了
            Loaded += (s, e) => { WindowPlacement.ClampToWorkArea(this); Anim.ApplyTo(this); };
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState != MouseButtonState.Pressed) return;
            try { DragMove(); } catch (System.InvalidOperationException) { }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        /// <summary>
        /// 子导航切换。用 Checked 而不是 Click：
        /// 用键盘方向键在同一 GroupName 的 RadioButton 之间切换时只会触发 Checked、不会触发 Click，
        /// 挂在 Click 上会导致键盘操作时右侧内容不跟着切（只有鼠标点才好使）。
        /// 注意：XAML 里 IsChecked="True" 会在 InitializeComponent 解析到它时就触发一次 Checked，
        /// 那时右侧面板还没构造出来 —— ShowSection 开头的 null 判断就是为这一步兜底的。
        /// </summary>
        private void Nav_Checked(object sender, RoutedEventArgs e)
        {
            var rb = sender as RadioButton;
            if (rb == null || rb.Tag == null) return;
            ShowSection(rb.Tag.ToString());
        }

        private void ShowSection(string page)
        {
            if (PageIntro == null || PageKey == null || PageFaq == null || PageData == null) return;

            PageIntro.Visibility = page == "intro" ? Visibility.Visible : Visibility.Collapsed;
            PageKey.Visibility = page == "key" ? Visibility.Visible : Visibility.Collapsed;
            PageFaq.Visibility = page == "faq" ? Visibility.Visible : Visibility.Collapsed;
            PageData.Visibility = page == "data" ? Visibility.Visible : Visibility.Collapsed;

            // 切换后回到顶部，避免上一页滚到一半的位置"粘"到新页面上
            ScrollViewer sv = page == "intro" ? PageIntro
                : page == "key" ? PageKey
                : page == "faq" ? PageFaq : PageData;
            sv.ScrollToTop();
        }
    }
}
