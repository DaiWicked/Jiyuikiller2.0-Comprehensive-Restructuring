using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace JiYuKiller.Effects
{
    /// <summary>
    /// 底栏"液态玻璃"折射效果。
    /// 对应 NavBarLens.hlsl（编译产物 NavBarLens.ps）。
    ///
    /// 与 Effects/GlassyEffect.cs 的区别（很重要）：
    ///   GlassyEffect 是给"整个窗口"设计的 —— 它的透镜几何按窗口尺寸/窗口中心算，
    ///   所以套到 432×66 的底栏上透镜错位，才会出现"整片偏色"。
    ///   本类只服务底栏：几何参数全部由底栏自己的尺寸、圆角、边缘带宽决定，
    ///   并且折射量在中心严格为 0（数学上保证），不会污染整片颜色。
    /// </summary>
    public sealed class NavBarLensEffect : ShaderEffect
    {
        private const string ShaderUri = "pack://application:,,,/学习不通;component/Effects/NavBarLens.ps";

        public static readonly DependencyProperty InputProperty =
            RegisterPixelShaderSamplerProperty("Input", typeof(NavBarLensEffect), 0);

        /// <summary>元素尺寸（设备像素）</summary>
        public static readonly DependencyProperty TextureSizeProperty =
            DependencyProperty.Register("TextureSize", typeof(Point), typeof(NavBarLensEffect),
                new UIPropertyMetadata(new Point(1.0, 1.0), PixelShaderConstantCallback(0)));

        /// <summary>玻璃圆角矩形的半尺寸（设备像素）</summary>
        public static readonly DependencyProperty GlassHalfProperty =
            DependencyProperty.Register("GlassHalf", typeof(Point), typeof(NavBarLensEffect),
                new UIPropertyMetadata(new Point(1.0, 1.0), PixelShaderConstantCallback(1)));

        /// <summary>圆角半径（设备像素）</summary>
        public static readonly DependencyProperty GlassRadiusProperty =
            DependencyProperty.Register("GlassRadius", typeof(double), typeof(NavBarLensEffect),
                new UIPropertyMetadata(23.0, PixelShaderConstantCallback(2)));

        /// <summary>边缘折射带宽度（设备像素）</summary>
        public static readonly DependencyProperty EdgeWidthProperty =
            DependencyProperty.Register("EdgeWidth", typeof(double), typeof(NavBarLensEffect),
                new UIPropertyMetadata(16.0, PixelShaderConstantCallback(3)));

        /// <summary>最大向内位移量（设备像素），越大"水滴放大"越明显</summary>
        public static readonly DependencyProperty RefractStrengthProperty =
            DependencyProperty.Register("RefractStrength", typeof(double), typeof(NavBarLensEffect),
                new UIPropertyMetadata(10.0, PixelShaderConstantCallback(4)));

        /// <summary>色散的绝对像素量：R/B 各偏移 ±此值（1px 左右才看得出来）</summary>
        public static readonly DependencyProperty AberrationPxProperty =
            DependencyProperty.Register("AberrationPx", typeof(double), typeof(NavBarLensEffect),
                new UIPropertyMetadata(1.2, PixelShaderConstantCallback(5)));

        /// <summary>边缘柔光强度</summary>
        public static readonly DependencyProperty RimBoostProperty =
            DependencyProperty.Register("RimBoost", typeof(double), typeof(NavBarLensEffect),
                new UIPropertyMetadata(0.10, PixelShaderConstantCallback(6)));

        /// <summary>总强度倍率，0 表示完全关闭（此时本效果是恒等变换）</summary>
        public static readonly DependencyProperty StrengthProperty =
            DependencyProperty.Register("Strength", typeof(double), typeof(NavBarLensEffect),
                new UIPropertyMetadata(1.0, PixelShaderConstantCallback(7)));

        /// <summary>
        /// 着色器是否真的加载成功。
        /// 注意：与 GlassyEffect.cs 一样，这里只反映"资源是否可解析"，
        /// 真正的 GPU 编译发生在首次渲染时；GPU 不支持 ps_2_0 时会由 WPF 走软件渲染回退。
        /// </summary>
        public bool IsShaderLoaded { get; private set; }

        public NavBarLensEffect()
        {
            try
            {
                var shader = new PixelShader
                {
                    UriSource = new Uri(ShaderUri, UriKind.Absolute)
                };
                PixelShader = shader;

                UpdateShaderValue(InputProperty);
                UpdateShaderValue(TextureSizeProperty);
                UpdateShaderValue(GlassHalfProperty);
                UpdateShaderValue(GlassRadiusProperty);
                UpdateShaderValue(EdgeWidthProperty);
                UpdateShaderValue(RefractStrengthProperty);
                UpdateShaderValue(AberrationPxProperty);
                UpdateShaderValue(RimBoostProperty);
                UpdateShaderValue(StrengthProperty);

                IsShaderLoaded = true;
            }
            catch (Exception ex)
            {
                Services.Logger.Instance.Warn("NavBarLensEffect 像素着色器加载失败: " + ex.Message);
                IsShaderLoaded = false;
            }
        }

        public Brush Input
        {
            get { return (Brush)GetValue(InputProperty); }
            set { SetValue(InputProperty, value); }
        }

        public Point TextureSize
        {
            get { return (Point)GetValue(TextureSizeProperty); }
            set { SetValue(TextureSizeProperty, value); }
        }

        public Point GlassHalf
        {
            get { return (Point)GetValue(GlassHalfProperty); }
            set { SetValue(GlassHalfProperty, value); }
        }

        public double GlassRadius
        {
            get { return (double)GetValue(GlassRadiusProperty); }
            set { SetValue(GlassRadiusProperty, value); }
        }

        public double EdgeWidth
        {
            get { return (double)GetValue(EdgeWidthProperty); }
            set { SetValue(EdgeWidthProperty, value); }
        }

        public double RefractStrength
        {
            get { return (double)GetValue(RefractStrengthProperty); }
            set { SetValue(RefractStrengthProperty, value); }
        }

        public double AberrationPx
        {
            get { return (double)GetValue(AberrationPxProperty); }
            set { SetValue(AberrationPxProperty, value); }
        }

        public double RimBoost
        {
            get { return (double)GetValue(RimBoostProperty); }
            set { SetValue(RimBoostProperty, value); }
        }

        public double Strength
        {
            get { return (double)GetValue(StrengthProperty); }
            set { SetValue(StrengthProperty, value); }
        }
    }
}
