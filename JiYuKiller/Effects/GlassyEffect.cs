using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace JiYuKiller.Effects
{
    /// <summary>
    /// 毛玻璃像素着色器效果
    /// 移植自 WPF-Liquid-Glass-Effect-main
    /// 对桌面截图背景进行折射、模糊、边缘光晕处理
    /// </summary>
    public sealed class GlassyEffect : ShaderEffect
    {
        public static readonly DependencyProperty InputProperty =
            RegisterPixelShaderSamplerProperty("Input", typeof(GlassyEffect), 0);

        public static readonly DependencyProperty TextureSizeProperty =
            DependencyProperty.Register(
                "TextureSize",
                typeof(Point),
                typeof(GlassyEffect),
                new UIPropertyMetadata(new Point(1.0, 1.0), PixelShaderConstantCallback(0)));

        public static readonly DependencyProperty GlassCenterProperty =
            DependencyProperty.Register(
                "GlassCenter",
                typeof(Point),
                typeof(GlassyEffect),
                new UIPropertyMetadata(new Point(0.0, 0.0), PixelShaderConstantCallback(1)));

        public static readonly DependencyProperty GlassSizeProperty =
            DependencyProperty.Register(
                "GlassSize",
                typeof(Point),
                typeof(GlassyEffect),
                new UIPropertyMetadata(new Point(120.0, 80.0), PixelShaderConstantCallback(2)));

        public static readonly DependencyProperty BlurIntensityProperty =
            DependencyProperty.Register(
                "BlurIntensity",
                typeof(double),
                typeof(GlassyEffect),
                new UIPropertyMetadata(0.2, PixelShaderConstantCallback(3)));

        /// <summary>
        /// 着色器是否加载成功
        /// </summary>
        public bool IsShaderLoaded { get; private set; }

        public GlassyEffect()
        {
            try
            {
                PixelShader shader = new PixelShader
                {
                    UriSource = new Uri("pack://application:,,,/i.chaoxing;component/Effects/GlassyEffect.ps", UriKind.Absolute)
                };

                // 通过设置属性来验证着色器可用
                this.PixelShader = shader;
                UpdateShaderValue(InputProperty);
                UpdateShaderValue(TextureSizeProperty);
                UpdateShaderValue(GlassCenterProperty);
                UpdateShaderValue(GlassSizeProperty);
                UpdateShaderValue(BlurIntensityProperty);
                IsShaderLoaded = true;
            }
            catch (Exception ex)
            {
                // 像素着色器加载失败（如老显卡/虚拟机不支持Pixel Shader 2.0）
                Services.Logger.Instance.Warn($"GlassyEffect 像素着色器加载失败: {ex.Message}");
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

        public Point GlassCenter
        {
            get { return (Point)GetValue(GlassCenterProperty); }
            set { SetValue(GlassCenterProperty, value); }
        }

        public Point GlassSize
        {
            get { return (Point)GetValue(GlassSizeProperty); }
            set { SetValue(GlassSizeProperty, value); }
        }

        public double BlurIntensity
        {
            get { return (double)GetValue(BlurIntensityProperty); }
            set { SetValue(BlurIntensityProperty, value); }
        }
    }
}
