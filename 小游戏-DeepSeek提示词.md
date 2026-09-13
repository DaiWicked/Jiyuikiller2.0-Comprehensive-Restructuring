# DeepSeek 代码生成提示词：扫雷 + Chrome恐龙跳游戏

## 项目背景
- 技术栈：C# WPF，.NET Framework 4.7.2
- 项目名称：JiYuKiller（学习不通）
- 命名空间：`JiYuKiller.Games`
- 游戏将作为UserControl嵌入主程序的小游戏页面

## 通用要求
1. 每个游戏是一个独立的UserControl（.xaml + .xaml.cs）
2. 顶部必须有：「← 返回」按钮 + 游戏名称 + 「重新开始」按钮
3. 返回按钮点击后，通过以下代码返回游戏菜单：
   ```csharp
   var parent = this.Parent as ContentControl ?? (Window.GetWindow(this) as MainWindow)?.GameContent;
   if (parent != null) parent.Content = new GameMenu();
   ```
4. UI风格：圆角、半透明、与主程序液态玻璃风格一致
5. 游戏区域用Canvas或Grid，背景色 #F7F7F7 或 #1a1a2e
6. 游戏逻辑用DispatcherTimer驱动
7. 禁止使用WebBrowser控件（会导致崩溃），全部用C# + WPF原生控件实现
8. 禁止引用任何外部资源/网络资源
9. 代码要有异常处理，不能崩溃

---

## 游戏1：扫雷（Minesweeper）

### 文件
- `Minesweeper.xaml`
- `Minesweeper.xaml.cs`

### 功能要求
- 9×9网格，10个雷
- 左键点击挖雷
- 右键点击标旗（🚩）
- 首次点击保证安全（不会踩雷）
- 数字颜色：1蓝、2绿、3红、4深蓝、5棕、6青
- 空白区域自动展开（递归）
- 顶部显示剩余雷数（10 - 已标旗数）
- 踩雷后显示所有雷，弹窗"游戏结束"
- 全部非雷格子翻开后弹窗"恭喜胜利"
- 重新开始按钮重置游戏

### XAML结构参考
```xml
<UserControl x:Class="JiYuKiller.Games.Minesweeper"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
             xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
             mc:Ignorable="d">
    <Grid>
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>
        <!-- 顶部栏 -->
        <StackPanel Orientation="Horizontal" Margin="0,0,0,10">
            <Button Content="← 返回" Click="Back_Click" Style="{StaticResource GlassButton}" Width="70" Height="28" FontSize="12" Margin="0,0,10,0"/>
            <TextBlock Text="扫雷" FontSize="16" FontWeight="Bold" Foreground="#FF1a1a2e" VerticalAlignment="Center" Margin="0,0,16,0"/>
            <Border Background="#15000000" CornerRadius="6" Padding="10,4" VerticalAlignment="Center">
                <TextBlock x:Name="MineText" Text="剩余: 10" FontSize="13" Foreground="#FF1a1a2e" VerticalAlignment="Center"/>
            </Border>
            <Button Content="重新开始" Click="Restart_Click" Style="{StaticResource GlassButton}" Width="70" Height="28" FontSize="12" Margin="10,0,0,0"/>
        </StackPanel>
        <!-- 游戏区域 -->
        <Border Grid.Row="1" Background="#FFBDBDBD" CornerRadius="8" Padding="6" HorizontalAlignment="Center" VerticalAlignment="Center">
            <UniformGrid x:Name="GameGrid" Rows="9" Columns="9" Width="270" Height="270"/>
        </Border>
        <TextBlock Grid.Row="2" Text="左键挖雷，右键标旗" FontSize="11" Foreground="#FF888899" Margin="0,10,0,0" HorizontalAlignment="Center"/>
    </Grid>
</UserControl>
```

---

## 游戏2：Chrome恐龙跳（DinoGame）

### 文件
- `DinoGame.xaml`
- `DinoGame.xaml.cs`

### 功能要求
- 纯C# Canvas实现，禁止用WebBrowser
- 恐龙在左侧，仙人掌从右向左移动
- 空格或鼠标点击跳跃
- 重力系统（跳跃后下落）
- 碰撞检测（带内边距，不要太严格）
- 分数随时间递增
- 每100分速度加快
- 开始界面：显示"点击或按空格开始"
- 游戏结束：半透明黑色遮罩 + "GAME OVER"文字 + 弹窗显示分数
- 重新开始按钮重置游戏
- 顶部显示当前分数

### 恐龙绘制要求
用WPF形状组合绘制恐龙：
- 身体：圆角矩形（深灰色 #535353）
- 头：小矩形在身体右上方
- 眼睛：白色小圆点
- 两条腿：小矩形
- 不要用图片，全部用形状绘制

### 仙人掌绘制要求
- 深绿色矩形
- 高度随机（20-45）
- 宽度随机（14-22）
- 圆角

### XAML结构参考
```xml
<UserControl x:Class="JiYuKiller.Games.DinoGame"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
             xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
             mc:Ignorable="d">
    <Grid>
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>
        <StackPanel Orientation="Horizontal" Margin="0,0,0,10">
            <Button Content="← 返回" Click="Back_Click" Style="{StaticResource GlassButton}" Width="70" Height="28" FontSize="12" Margin="0,0,10,0"/>
            <TextBlock Text="恐龙跳" FontSize="16" FontWeight="Bold" Foreground="#FF1a1a2e" VerticalAlignment="Center" Margin="0,0,16,0"/>
            <Border Background="#15000000" CornerRadius="6" Padding="10,4" VerticalAlignment="Center">
                <TextBlock x:Name="ScoreText" Text="分数: 0" FontSize="13" Foreground="#FF1a1a2e" VerticalAlignment="Center"/>
            </Border>
            <Button Content="重新开始" Click="Restart_Click" Style="{StaticResource GlassButton}" Width="70" Height="28" FontSize="12" Margin="10,0,0,0"/>
        </StackPanel>
        <Border Grid.Row="1" Background="#FFF7F7F7" CornerRadius="8" Padding="4" HorizontalAlignment="Center" VerticalAlignment="Center" MouseLeftButtonDown="GameCanvas_Click">
            <Canvas x:Name="GameCanvas" Width="360" Height="180" Background="#FFF7F7F7"/>
        </Border>
        <TextBlock Grid.Row="2" Text="空格/点击跳跃，躲避仙人掌" FontSize="11" Foreground="#FF888899" Margin="0,10,0,0" HorizontalAlignment="Center"/>
    </Grid>
</UserControl>
```

---

## 输出格式
请输出完整的4个文件代码：
1. Minesweeper.xaml
2. Minesweeper.xaml.cs
3. DinoGame.xaml
4. DinoGame.xaml.cs

每个文件用清晰的分隔线标注，代码要完整可直接编译。
