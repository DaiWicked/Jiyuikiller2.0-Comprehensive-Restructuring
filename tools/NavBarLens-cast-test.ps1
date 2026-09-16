# ============================================================================
#  NavBarLens-cast-test.ps1 —— 受控验证：底栏折射有没有"整片偏色"
#
#  为什么需要它：App 级 A/B（两次启动、一开一关折射）**不可靠** ——
#  两次运行之间桌面内容会变，差值可能是"桌面变了"而不是"着色器改了颜色"。
#  实测撞过这个坑：中心带 +14、整条 -11（符号相反）⇒ 自相矛盾，不能用。
#
#  本脚本复用 NavBarLens-unit-test.ps1 的 LensEffect 类与几何常量，喂一张
#  **处处有锐边**的彩色条纹图（20px 一块），只切换 Strength，逐像素比较：
#    · 中心区（距玻璃边缘 > EdgeWidth，理应是恒等变换）→ 期望逐像素差 **严格为 0**
#    · 边缘带（EdgeWidth 以内，折射/色散该干活）→ 期望明显非 0
#  （注意：输入必须有锐边。纯色/光滑渐变位移后几乎不变，测不出任何东西。）
#
#  用法: powershell -File tools\NavBarLens-cast-test.ps1
# ============================================================================
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'NavBarLens-unit-test.ps1')   # 复用 LensEffect 类、$W/$S_h 与几何约定

$REFRACT = 5.0    # 强度 0.31 时的基础折射量（16 × 0.31）
$ABERR   = 1.2    # 色散绝对像素量
$CORNER  = 2.5    # 圆角加料（基础折射的 50%）

# ---- 彩色条纹输入：20px 一块，处处有锐边 ----
function MakeStripedInput {
    $g = New-Object System.Windows.Controls.Grid
    $g.Width = $W; $g.Height = $S_h
    $cols = @('#FFE53935','#FF1E88E5','#FFFDD835','#FF43A047','#FF8E24AA','#FFFB8C00')
    $n = [int]($W / 20)
    for ($i = 0; $i -lt $n; $i++) {
        $col = New-Object System.Windows.Controls.ColumnDefinition
        $col.Width = New-Object System.Windows.GridLength(20)
        $g.ColumnDefinitions.Add($col)
    }
    for ($i = 0; $i -lt $n; $i++) {
        $b = New-Object System.Windows.Controls.Border
        $b.Background = New-Object System.Windows.Media.SolidColorBrush(
            [System.Windows.Media.ColorConverter]::ConvertFromString($cols[$i % $cols.Count]))
        [System.Windows.Controls.Grid]::SetColumn($b, $i)
        $g.Children.Add($b) | Out-Null
    }
    return $g
}

function RenderStriped([double]$strength) {
    $root = MakeStripedInput
    $fx = New-Object LensEffect -ArgumentList $ps38
    $fx.TextureSize = [System.Windows.Point]::new($W, $S_h)
    $fx.GlassHalf   = [System.Windows.Point]::new(($W - 48)/2, ($S_h - 16)/2)
    $fx.GlassRadius = 20
    $fx.EdgeWidth   = 21
    $fx.RefractStrength = $REFRACT
    $fx.AberrationPx    = $ABERR
    $fx.RimBoost        = 0.12
    $fx.Strength        = $strength
    $root.Effect = $fx
    $root.Measure([System.Windows.Size]::new($W,$S_h))
    $root.Arrange([System.Windows.Rect]::new(0,0,$W,$S_h))
    $root.UpdateLayout()
    $rtb = New-Object System.Windows.Media.Imaging.RenderTargetBitmap $W,$S_h,96,96,([System.Windows.Media.PixelFormats]::Pbgra32)
    $rtb.Render($root)
    $buf = New-Object byte[] ($W*$S_h*4)
    $rtb.CopyPixels($buf, $W*4, 0)
    return ,$buf
}

""
"===== 受控偏色测试（彩色条纹输入，只切换 Strength） ====="
"元素 ${W}x${S_h}：玻璃 352x44，EdgeWidth=21；RefractStrength=$REFRACT  Aberration=$ABERR"
""

$off = RenderStriped 0.0
$on  = RenderStriped 1.0

$cx = [int]($W/2); $cy = [int]($S_h/2)
$gx = [int](($W-48)/2); $gy = [int](($S_h-16)/2)     # 176, 22
$EW = 21

# 中心区：距玻璃边缘 > EW（玻璃只有 44px 高 ⇒ 垂直只剩 1 行，但横向有 300+ 像素、跨多条色带）
$cx0 = $cx - ($gx - $EW - 1); $cx1 = $cx + ($gx - $EW - 1)
$cy0 = $cy - ($gy - $EW - 1); $cy1 = $cy + ($gy - $EW - 1)
$maxC = 0.0; $sr=0.0; $sg=0.0; $sb=0.0; $nC=0
for ($y=$cy0; $y -le $cy1; $y++) {
  for ($x=$cx0; $x -le $cx1; $x++) {
    $i = ($y*$W + $x)*4
    foreach ($ch in 0,1,2) { $d=[Math]::Abs([int]$on[$i+$ch]-[int]$off[$i+$ch]); if ($d -gt $maxC) { $maxC=$d } }
    $sb += ([int]$on[$i]   - [int]$off[$i])
    $sg += ([int]$on[$i+1] - [int]$off[$i+1])
    $sr += ([int]$on[$i+2] - [int]$off[$i+2])
    $nC++
  }
}
"【中心区】x=$cx0..$cx1  y=$cy0..$cy1  （共 $nC 像素）"
"  逐像素最大通道差 = $maxC"
"  平均通道偏移     = dR {0:N3}   dG {1:N3}   dB {2:N3}" -f ($sr/$nC), ($sg/$nC), ($sb/$nC)
if ($maxC -eq 0) { "  => 判定：**恒等变换，无整片偏色**（逐像素完全相同）" }
else             { "  => 判定：**存在偏色**，最大 $maxC/255" }

# 边缘带：玻璃左边缘内 12px（彩色条纹在这里有锐边 ⇒ 折射必然改变像素）
$maxE = 0.0; $nE = 0; $sumE = 0.0
for ($y=($cy-$gy); $y -le ($cy+$gy); $y++) {
  for ($x=($cx-$gx); $x -le ($cx-$gx+12); $x++) {
    $i = ($y*$W + $x)*4
    $s = 0.0
    foreach ($ch in 0,1,2) { $s += [Math]::Abs([int]$on[$i+$ch]-[int]$off[$i+$ch]) }
    $sumE += $s/3.0; if (($s/3.0) -gt $maxE) { $maxE = $s/3.0 }; $nE++
  }
}
""
"【边缘带】玻璃左边缘内 12px（共 $nE 像素）"
"  平均 |通道差| = {0:N2}    最大 = {1:N2}" -f ($sumE/$nE), $maxE
"  => 期望明显非 0（证明折射/色散确实在干活，否则这个测试等于没测到东西）"
