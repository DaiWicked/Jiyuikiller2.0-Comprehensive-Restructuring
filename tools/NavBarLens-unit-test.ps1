$ErrorActionPreference = 'Stop'
$ps38 = "C:\Users\Administrator\Desktop\Jiyuikiller2.0-Comprehensive-Restructuring\JiYuKiller\Effects\NavBarLens.ps"
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
Add-Type -ReferencedAssemblies PresentationCore,PresentationFramework,WindowsBase -TypeDefinition @'
using System; using System.IO; using System.Windows; using System.Windows.Controls;
using System.Windows.Media; using System.Windows.Media.Effects;
public class LensEffect : ShaderEffect {
    public static readonly DependencyProperty InputProperty =
        RegisterPixelShaderSamplerProperty("Input", typeof(LensEffect), 0);
    public static readonly DependencyProperty TextureSizeProperty =
        DependencyProperty.Register("TextureSize", typeof(Point), typeof(LensEffect),
            new UIPropertyMetadata(new Point(1,1), PixelShaderConstantCallback(0)));
    public static readonly DependencyProperty GlassHalfProperty =
        DependencyProperty.Register("GlassHalf", typeof(Point), typeof(LensEffect),
            new UIPropertyMetadata(new Point(1,1), PixelShaderConstantCallback(1)));
    public static readonly DependencyProperty GlassRadiusProperty =
        DependencyProperty.Register("GlassRadius", typeof(double), typeof(LensEffect),
            new UIPropertyMetadata(20.0, PixelShaderConstantCallback(2)));
    public static readonly DependencyProperty EdgeWidthProperty =
        DependencyProperty.Register("EdgeWidth", typeof(double), typeof(LensEffect),
            new UIPropertyMetadata(21.0, PixelShaderConstantCallback(3)));
    public static readonly DependencyProperty RefractStrengthProperty =
        DependencyProperty.Register("RefractStrength", typeof(double), typeof(LensEffect),
            new UIPropertyMetadata(10.0, PixelShaderConstantCallback(4)));
    public static readonly DependencyProperty AberrationPxProperty =
        DependencyProperty.Register("AberrationPx", typeof(double), typeof(LensEffect),
            new UIPropertyMetadata(0.0, PixelShaderConstantCallback(5)));
    public static readonly DependencyProperty RimBoostProperty =
        DependencyProperty.Register("RimBoost", typeof(double), typeof(LensEffect),
            new UIPropertyMetadata(0.0, PixelShaderConstantCallback(6)));
    public static readonly DependencyProperty StrengthProperty =
        DependencyProperty.Register("Strength", typeof(double), typeof(LensEffect),
            new UIPropertyMetadata(1.0, PixelShaderConstantCallback(7)));
    public LensEffect(string psPath) {
        var ps = new PixelShader();
        using (var fs = File.OpenRead(psPath)) { ps.SetStreamSource(fs); }
        PixelShader = ps;
        UpdateShaderValue(InputProperty); UpdateShaderValue(TextureSizeProperty);
        UpdateShaderValue(GlassHalfProperty); UpdateShaderValue(GlassRadiusProperty);
        UpdateShaderValue(EdgeWidthProperty); UpdateShaderValue(RefractStrengthProperty);
        UpdateShaderValue(AberrationPxProperty); UpdateShaderValue(RimBoostProperty);
        UpdateShaderValue(StrengthProperty);
    }
    public Point TextureSize { get { return (Point)GetValue(TextureSizeProperty); } set { SetValue(TextureSizeProperty, value); } }
    public Point GlassHalf { get { return (Point)GetValue(GlassHalfProperty); } set { SetValue(GlassHalfProperty, value); } }
    public double GlassRadius { get { return (double)GetValue(GlassRadiusProperty); } set { SetValue(GlassRadiusProperty, value); } }
    public double EdgeWidth { get { return (double)GetValue(EdgeWidthProperty); } set { SetValue(EdgeWidthProperty, value); } }
    public double RefractStrength { get { return (double)GetValue(RefractStrengthProperty); } set { SetValue(RefractStrengthProperty, value); } }
    public double AberrationPx { get { return (double)GetValue(AberrationPxProperty); } set { SetValue(AberrationPxProperty, value); } }
    public double RimBoost { get { return (double)GetValue(RimBoostProperty); } set { SetValue(RimBoostProperty, value); } }
    public double Strength { get { return (double)GetValue(StrengthProperty); } set { SetValue(StrengthProperty, value); } }
}
'@
$null = New-Object System.Windows.Application
$W = 400; $S_h = 60

function MakeBorder([double]$stepAt) {
    $b = New-Object System.Windows.Controls.Border
    $b.Width = $W; $b.Height = $S_h
    $lgb = New-Object System.Windows.Media.LinearGradientBrush
    $lgb.StartPoint = [System.Windows.Point]::new(0,0)
    $lgb.EndPoint   = [System.Windows.Point]::new(1,0)
    $lgb.MappingMode = [System.Windows.Media.BrushMappingMode]::RelativeToBoundingBox
    $t0 = $stepAt / $W
    $t1 = ($stepAt + 3) / $W
    $lgb.GradientStops.Add([System.Windows.Media.GradientStop]::new([System.Windows.Media.Colors]::Black, 0.0))
    $lgb.GradientStops.Add([System.Windows.Media.GradientStop]::new([System.Windows.Media.Colors]::Black, $t0))
    $lgb.GradientStops.Add([System.Windows.Media.GradientStop]::new([System.Windows.Media.Colors]::White, $t1))
    $lgb.GradientStops.Add([System.Windows.Media.GradientStop]::new([System.Windows.Media.Colors]::White, 1.0))
    $b.Background = $lgb
    return $b
}

function RenderIt([double]$strength, [double]$refract, [double]$aberr, [double]$stepAt) {
    $b = MakeBorder $stepAt
    $fx = New-Object LensEffect -ArgumentList $ps38
    $fx.TextureSize = [System.Windows.Point]::new($W, $S_h)
    $fx.GlassHalf = [System.Windows.Point]::new(($W - 48)/2, ($S_h - 16)/2)
    $fx.GlassRadius = 20
    $fx.EdgeWidth = 21
    $fx.RefractStrength = $refract
    $fx.AberrationPx = $aberr
    $fx.RimBoost = 0
    $fx.Strength = $strength
    $b.Effect = $fx
    $b.Measure([System.Windows.Size]::new($W,$S_h))
    $b.Arrange([System.Windows.Rect]::new(0,0,$W,$S_h))
    $b.UpdateLayout()
    $rtb = New-Object System.Windows.Media.Imaging.RenderTargetBitmap $W,$S_h,96,96,([System.Windows.Media.PixelFormats]::Pbgra32)
    $rtb.Render($b)
    $buf = New-Object byte[] ($W*$S_h*4)
    $rtb.CopyPixels($buf, $W*4, 0)
    return ,$buf
}

function Centroid($buf, [int]$x0, [int]$x1, [int]$ch) {
    $num = 0.0; $den = 0.0
    for ($y = 26; $y -lt 34; $y++) {
        for ($x = $x0; $x -lt $x1-1; $x++) {
            $g = [Math]::Abs($buf[$y*$W*4 + ($x+1)*4 + $ch] - $buf[$y*$W*4 + $x*4 + $ch])
            $num += $g * $x
            $den += $g
        }
    }
    if ($den -le 0) { return -1.0 }
    return $num/$den
}

"===== 隔离单测：NavBarLens.ps 直接作用于台阶背景 ====="
"元素 ${W}x${S_h}(左右各外扩24) => 玻璃本体 352x44；x=0 即元素左边缘"
""

# 台阶贴近元素左边缘 (x=2..5) —— 注意元素左边缘在玻璃之外 24px，所以这里量的是"外扩带"
foreach ($step in @(2, 20, 26, 30)) {
    $off = RenderIt 0.0 10 0 $step
    $on  = RenderIt 1.0 10 0 $step
    $c1 = Centroid $off 0 40 1
    $c2 = Centroid $on  0 40 1
    # 该台阶深度(相对玻璃左边缘 x=24): depth = step - 24 (负数表示在玻璃外)
    $depth = $step - 24
        # 必须用双精度字面量：写成 Max(0, 1 - $depth/21) 会被 PowerShell 选中 Max(int,int) 重载，
    # 0.905 被取整成 1 ⇒ edge 恒为 1.0，这一列（也正是要验证的边缘因子）完全测不到。
    $edge = if ($depth -gt 0) { [Math]::Pow([Math]::Max(0.0, 1.0 - $depth/21.0), 2) } else { 1.0 }
    # 注意：折射方向已在 c5941fb 由"向内"改为"向外"，位移符号随之取正（这里曾写作 -10*edge 已过期）
    "台阶x={0,3}  depth={1,4}  edge={2:F3}  理论位移={3,6:F2}px   实测位移={4,6:F2}px" -f $step, $depth, $edge, (10*$edge), ($c2-$c1)
}

""
"===== 色散验证（台阶 x=30, depth=6, 理论位移 -6.1px） ====="
$off = RenderIt 0.0 10 0 30
$on  = RenderIt 1.0 10 0 30
$ab  = RenderIt 1.0 10 3.0 30
"  纯折射:      R={0:F2} G={1:F2} B={2:F2}" -f (Centroid $ab 0 40 2), (Centroid $ab 0 40 1), (Centroid $ab 0 40 0)
"  折射+色散3px: R={0:F2} G={1:F2} B={2:F2}" -f (Centroid $ab 0 40 2), (Centroid $ab 0 40 1), (Centroid $ab 0 40 0)
$ab2 = RenderIt 1.0 10 3.0 30
$r = Centroid $ab2 0 40 2; $g = Centroid $ab2 0 40 1; $b = Centroid $ab2 0 40 0
$offR = Centroid $off 0 40 2; $offG = Centroid $off 0 40 1; $offB = Centroid $off 0 40 0
"  关折射基准:  R={0:F2} G={1:F2} B={2:F2}" -f $offR, $offG, $offB
"  => 色散位移 R-G={0:F2}px  B-G={1:F2}px (注：本项期望值未计入 edge 因子；depth=6 时 edge=(1-6/21)^2≈0.51，故实测约为设计值的一半，属正常)" -f (($r-$offR)-($g-$offG)), (($b-$offB)-($g-$offG))
