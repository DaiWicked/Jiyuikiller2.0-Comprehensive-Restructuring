# DeepSeek 完整集成提示词：小游戏功能（扫雷 + Chrome恐龙跳）

## 任务
请完整生成以下所有文件的代码，用于在一个已有的C# WPF项目中添加"小游戏"功能。所有代码必须可直接编译，无需修改。

---

## 项目信息
- 项目名：JiYuKiller（程序名：学习不通.exe）
- 技术栈：C# WPF，.NET Framework 4.7.2
- 项目根命名空间：`JiYuKiller`
- 游戏命名空间：`JiYuKiller.Games`
- 主窗口类：`MainWindow`（在 `JiYuKiller` 命名空间下）
- 主窗口有一个 `ContentControl` 名为 `GameContent`，用于承载游戏内容
- 主窗口有一个 `ShowPage(string pageName)` 方法，用于切换页面

---

## 需要生成的文件清单（共7个）

### 文件1：`Games/GameMenu.xaml`
游戏菜单页面，显示两个游戏入口卡片。

**要求**：
- 标题"选择游戏"
- 两个卡片：扫雷（红色，💣图标）、恐龙跳（灰色，🦖图标）
- 卡片样式：圆角12，阴影，上半部分彩色背景+emoji图标，下半部分游戏名+简介
- 点击卡片切换到对应游戏
- 切换代码：
```csharp
var parent = this.Parent as ContentControl ?? (Window.GetWindow(this) as MainWindow)?.GameContent;
if (parent != null) parent.Content = new Minesweeper(); // 或 new DinoGame()
```

### 文件2：`Games/GameMenu.xaml.cs`
对应代码隐藏。

### 文件3：`Games/Minesweeper.xaml`
扫雷游戏界面。

**要求**：
- 顶部栏：「← 返回」按钮 + "扫雷"标题 + 剩余雷数显示 + 「重新开始」按钮
- 游戏区：9×9 UniformGrid，270×270，浅灰色背景
- 底部提示文字："左键挖雷，右键标旗"
- 返回按钮调用：
```csharp
var parent = this.Parent as ContentControl ?? (Window.GetWindow(this) as MainWindow)?.GameContent;
if (parent != null) parent.Content = new GameMenu();
```

### 文件4：`Games/Minesweeper.xaml.cs`
扫雷游戏逻辑。

**功能要求**：
- 9×9网格，10个雷
- 左键挖雷，右键标旗（显示🚩）
- **首次点击保证安全**（第一次点击后才放置雷，且避开点击位置）
- 数字颜色：1=蓝、2=绿、3=红、4=深蓝、5=棕、6=青
- 空白格自动递归展开
- 顶部显示"剩余: X"（10 - 已标旗数）
- 踩雷：显示所有雷（红色背景+💣），MessageBox.Show("游戏结束！")
- 胜利：所有非雷格翻开后，MessageBox.Show("恭喜胜利！")
- 重新开始：重置所有状态
- 用 `DispatcherTimer` 不需要，扫雷是事件驱动的

### 文件5：`Games/DinoGame.xaml`
恐龙跳游戏界面。

**要求**：
- 顶部栏：「← 返回」按钮 + "恐龙跳"标题 + 分数显示 + 「重新开始」按钮
- 游戏区：Canvas，360×180，背景 #F7F7F7
- Canvas的 `MouseLeftButtonDown` 事件触发跳跃
- 底部提示："空格/点击跳跃，躲避仙人掌"
- UserControl的 `KeyDown` 事件处理空格跳跃（注意：需要在Loaded时Focus()）

### 文件6：`Games/DinoGame.xaml.cs`
恐龙跳游戏逻辑。

**功能要求**：
- **纯C# + WPF形状实现，禁止WebBrowser，禁止外部资源**
- 游戏参数：画布宽360高180，地面Y=150，恐龙X=40，恐龙宽44高44
- 恐龙绘制：用Rectangle和Ellipse组合（身体+头+眼睛+两条腿），深灰色 #535353
- 仙人掌：深绿色圆角矩形，高度随机20-45，宽度随机14-22
- 物理：跳跃初速度-10，重力+0.5/帧，地面检测
- 移动：仙人掌从右向左移动，速度初始4，每100分+0.3
- 碰撞检测：带内边距（恐龙左右各缩6px，上下各缩4px），不要太严格
- 分数：每帧+1，顶部实时显示
- 开始状态：未开始时显示"点击或按空格开始"，恐龙不动
- 游戏结束：半透明黑色遮罩 + "GAME OVER"文字 + MessageBox.Show("游戏结束！分数: " + score)
- 重新开始：重置所有状态
- 用 `DispatcherTimer`，Interval=20ms
- 返回菜单时停止timer

### 文件7：集成代码片段（MainWindow.xaml / MainWindow.xaml.cs / .csproj 需要添加的内容）

请分别给出：

**7a. MainWindow.xaml 需要添加的内容**：
- 底栏导航按钮（在已有"教师模拟"按钮后面添加"小游戏"按钮）：
```xml
<Button x:Name="NavGame" Content="小游戏" Click="NavGame_Click" Style="{StaticResource NavGlassButton}" Padding="14,8" Margin="3,0" MinWidth="64" Height="44" />
```
- 游戏页面（在"帮助文档页面"之前添加）：
```xml
<ScrollViewer x:Name="PageGame" VerticalScrollBarVisibility="Hidden" Visibility="Collapsed">
    <StackPanel Margin="16,12">
        <Grid Margin="0,0,0,10">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="Auto" />
                <ColumnDefinition Width="*" />
            </Grid.ColumnDefinitions>
            <Button Content="←" Click="BtnGameBack_Click" Style="{StaticResource GlassButton}" Width="36" Height="32" FontSize="14" Padding="0" />
            <TextBlock Grid.Column="1" Text="小游戏" FontSize="18" FontWeight="Bold" VerticalAlignment="Center" Margin="8,0,0,0" Foreground="#FF1a1a2e" />
        </Grid>
        <ContentControl x:Name="GameContent" />
    </StackPanel>
</ScrollViewer>
```

**7b. MainWindow.xaml.cs 需要添加的内容**：
- 在 `ShowPage` 方法中：
  - 页面隐藏部分添加：`PageGame.Visibility = Visibility.Collapsed;`
  - 导航按钮重置部分添加：`NavGame.FontWeight = FontWeights.Normal;`
  - switch中添加：
```csharp
case "game":
    PageGame.Visibility = Visibility.Visible;
    NavGame.FontWeight = FontWeights.Bold;
    if (GameContent.Content == null) GameContent.Content = new Games.GameMenu();
    break;
```
- 事件处理方法：
```csharp
private void NavGame_Click(object sender, RoutedEventArgs e)
{
    Services.Logger.Instance.ButtonClick("小游戏", "NavGame");
    ShowPage("game");
}

private void BtnGameBack_Click(object sender, RoutedEventArgs e)
{
    ShowPage("quick");
}
```

**7c. .csproj 需要添加的内容**：
```xml
<Page Include="Games\GameMenu.xaml">
  <Generator>MSBuild:Compile</Generator>
  <SubType>Designer</SubType>
</Page>
<Page Include="Games\Minesweeper.xaml">
  <Generator>MSBuild:Compile</Generator>
  <SubType>Designer</SubType>
</Page>
<Page Include="Games\DinoGame.xaml">
  <Generator>MSBuild:Compile</Generator>
  <SubType>Designer</SubType>
</Page>
<Compile Include="Games\GameMenu.xaml.cs">
  <DependentUpon>GameMenu.xaml</DependentUpon>
</Compile>
<Compile Include="Games\Minesweeper.xaml.cs">
  <DependentUpon>Minesweeper.xaml</DependentUpon>
</Compile>
<Compile Include="Games\DinoGame.xaml.cs">
  <DependentUpon>DinoGame.xaml</DependentUpon>
</Compile>
```

---

## 重要约束
1. 所有XAML必须声明 `xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"` 和 `xmlns:d="http://schemas.microsoft.com/expression/blend/2008"`
2. 禁止使用WebBrowser控件
3. 禁止引用任何外部URL/网络资源
4. 禁止使用图片资源，全部用WPF形状（Rectangle/Ellipse/Line/TextBlock）绘制
5. 代码要有异常处理，游戏不能导致主程序崩溃
6. 返回菜单时必须停止DispatcherTimer
7. 每个文件的代码要完整，包含所有using语句
8. 命名空间必须是 `JiYuKiller.Games`
9. x:Class 必须是 `JiYuKiller.Games.类名`

---

## 输出格式
请按以下顺序输出，每个文件用 `=== 文件名 ===` 分隔：

=== Games/GameMenu.xaml ===
[完整代码]

=== Games/GameMenu.xaml.cs ===
[完整代码]

=== Games/Minesweeper.xaml ===
[完整代码]

=== Games/Minesweeper.xaml.cs ===
[完整代码]

=== Games/DinoGame.xaml ===
[完整代码]

=== Games/DinoGame.xaml.cs ===
[完整代码]

=== 集成说明 ===
[7a/7b/7c的修改说明，标注在文件的哪个位置插入]
