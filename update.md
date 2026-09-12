# 学习不通2.0 (JiYuKiller 2.0) - 更新日志

## QD_V2.3_JiYuRebuild_CoUI-Glass (2026-09-12) - 窗口控制模块修复 + teacher_sim集成

### 窗口控制模块修复（对照JiYuTrainer_Next-master）
- **根因修复**: DLL的hkb:*回调之前只打日志不执行动作，现在全部接线到实际窗口操作
- **HandleDllCallback改为薄转发**: 统一交给JiYuController.HandleVirusCallback处理
- **新增ManualTop/ManualFull方法**: 对应参考实现，执行真正的SetWindowLong/SetWindowPos
- **AllowGbTop改被动**: true=不干预TOPMOST，false=去掉TOPMOST（不再强制置顶）
- **新增gbFullManual状态**: 用户从DLL菜单进全屏后，轮询不再把边框加回去
- **严格窗口控制模式**: setAutoIncludeFullWindow已实现，其他极域全屏窗口也处理
- **跳过Virus Window**: 枚举窗口时跳过"JiYu Trainer Virus Window"
- **hk:ckstat握手**: 注入成功后补发，DLL做版本探测+键盘解锁
- **hs:回发**: hkb:succ时回发主窗口句柄给DLL
- **hkb:immck**: 立即刷新窗口枚举
- **去掉自动打开AllowGbTop**: 用户手动置顶不再自动打开设置开关

### teacher_sim集成
- 修复日志路径: 从桌面改为exe目录/teacher_sim/teacher_sim.log（与teacher_sim.py一致）
- 修复日志等待误报: 等待时间从10秒增加到30秒，进程仍在运行时不报警告
- 保留32位teacher_sim.exe（15400270 bytes）
- 已知限制: teacher_sim是极域V6.0协议，与v4.0 2016豪华版不兼容，学生无法登录

### 广播窗口修复
- 去掉周期性75%×80%缩放（原项目FixWindow从不调整尺寸）
- FixBroadcastWindow只改样式位，不移动/缩放窗口
- 补发hw:消息通知DLL接管窗口过程
# 学习不通2.0 (JiYuKiller 2.0) - 更新日志

## QD_V2.3_JiYuRebuild_CoUI-Glass (2026-09-10) - 用户协议窗口 + 玻璃效果统一

### 用户许可协议窗口 (AgreementWindow)
- 移植原jiyukiller的IDD_DIALOG_ARGEEMENT协议对话框
- 首次启动时弹出, 同意后才进入主界面
- 协议内容: 免责声明/法律声明/版权声明, 保留原文案
- 按钮: "极域给我爬"(同意) / "杰哥不要"(拒绝)
- 协议窗口添加start.png图片(118KB)
- AppSettings新增Argeed字段, 同意后保存到i.chaoxing.xml
- 拒绝协议则程序退出

### 协议窗口毛玻璃效果
- 分层结构: 桌面截图(Blur Radius=20) -> 半透明白色覆盖(#B8FFFFFF) -> 顶部高光渐变 -> 内容
- Window_Loaded中捕获全屏桌面, 按窗口位置裁剪区域作为背景
- 标题栏/按钮区改为半透明, 文字颜色加深保证可读性

### 彩蛋窗口毛玻璃效果
- 背景: #FF1A1A1A -> #C81A1A1A(78%不透明深色, 可透视主窗口)
- 新增顶部高光层: LinearGradient 30%白->透明
- 边框/阴影/标题栏/按钮区统一调整为半透明玻璃风格
- 视频区域保持黑色不透明(视频播放需要)

### 启动流程修复
- 移除App.xaml的StartupUri, 改为ShutdownMode=OnExplicitShutdown
- 同意协议后手动new MainWindow().Show(), 避免协议窗口关闭后应用退出
- 拒绝协议时关闭日志后Shutdown

## QD_V2.3_JiYuRebuild_CoUI-Glass (2026-09-09) - COUI UI/动画三阶段升级

### 第一阶段: 按钮动画 + 滑块样式 + 页面过渡
- GlassButton: Storyboard按压缩放0.92+回弹1.02过冲, 悬停上移1px+状态层8%黑
- NavGlassButton: 动画作用在Grid根元素整体缩放(不受BlurEffect影响), 悬停玻璃变亮
- GlassSlider: 新样式, Thumb悬停放大1.1/拖动放大1.25, 带DropShadow阴影
- 4个滑块引用GlassSlider样式
- ShowPage页面淡入过渡: Opacity 0->1, 300ms CubicEase EaseOut
- CrashReportService添加UI/动效状态诊断

### 第二阶段: 视觉增强
- 光感层: GlassyLayer上方RadialGradient, 顶部光源顶部亮边缘暗
- 噪声抗色带: 程序化生成128x128 Gray8噪声纹理, ImageBrush平铺, Opacity=1%
- 5级Surface配色资源
- 渐进模糊底栏: 底栏顶部LinearGradient遮罩
- 修复: 噪声层透明度从5%降到1%, 修复玻璃背景变暗

### 第三阶段: 动画增强
- 页面方向过渡: 根据导航顺序从左/右滑入+淡入, 280ms CubicEase
- GlassSwitch样式: 开关滑块滑动动画
- BloomStroke简化版: GlassContainer边框LinearGradient顶部亮底部暗
- 修复: 页面方向过渡用BeginAnimation替代Storyboard

### 按钮动画修复
- 修复底栏按钮无动效: 动画移到Grid根元素整体缩放
- 修复按钮变蓝色: 移除背景色ColorAnimation, 只通过stateLayer叠加暗色
- 修复GlassSlider缺少SliderRepeatButtonTransparent样式

## QD_V2.3_JiYuRebuild_CoUI-Glass (2026-09-09) - 自定义壁纸功能

### 自定义壁纸 (WallpaperService)
- 新增WallpaperService服务：壁纸加载/cover裁切/10MB文件验证/缩略图/缓存
- 支持格式：.jpg/.jpeg/.png/.bmp，最大10MB
- 壁纸裁切到窗口尺寸450x600（cover模式，保持宽高比居中裁切）
- 三按钮：选择壁纸/应用壁纸/恢复默认
- 壁纸丢失检测：原文件删除时显示红色提示
- 启动自动加载已保存壁纸

### Liquid Glass分层结构重构
- 原分层：BackdropLayer(桌面) → GlassyLayer(绑定桌面) → ContentLayer(半透明白+UI)
- 新分层：BackdropContainer(桌面+白色覆盖层+壁纸层) → GlassyLayer(VisualBrush绑定整个容器) → ContentLayer(透明+UI)
- 壁纸和桌面截图都被玻璃效果模糊
- 白色覆盖层从ContentLayer移到BackdropContainer内部

### 滑块正向逻辑
- 0% → 白色层alpha=255 + 壁纸层opacity=1.0 → 壁纸遮住白色和桌面 → 看到壁纸
- 100% → 白色层alpha=0 + 壁纸层opacity=0 → 看到桌面玻璃效果
- 无壁纸时，0%看到白色默认背景（不是透明）
- 数值越大，白色层和壁纸层越透明，桌面越可见

### CrashReportService壁纸诊断
- 错误报告新增【自定义壁纸状态】段落
- 包含：壁纸路径、是否有效、壁纸层Opacity、白色覆盖层Alpha、玻璃桌面可见度
- MainWindow壁纸相关方法(ApplyWallpaper/Reset/滑块)实时更新诊断信息

## QD_V2.3_JiYuRebuild_CoUI-Glass (2026-09-09) - 代码审查与稳定性修复

### 调试模式修复
- 修复调试模式关闭后重启仍为开启状态的bug
- 原因：DebugMode_Checked/Unchecked只设置了值，未调用_settings.Save()保存到文件
- 添加_settings.Save()确保设置持久化
- 添加Logger.Instance.Enable()/Disable()实时启用/禁用日志
- 关闭调试模式时先记录日志再禁用

### 退出进程清理
- 修复退出程序后teacher_sim.exe仍在后台运行的问题
- ExitApplication()添加_teacherSimService.Stop()调用
- 退出前检查教师端模拟进程是否运行并停止

### 代码审查结果
- 总代码量：6338行（19个.cs + 2个.xaml）
- 70个Click事件全部有对应方法
- 12个页面控件XAML和CS引用完整
- 11个服务文件全部在csproj中编译
- 编译结果：0错误，0警告
- 15处CheckBox空引用保护
- 全局异常双保险：DispatcherUnhandledException + AppDomain.UnhandledException
- 日志覆盖：147次调用（ButtonClick 53 + Info 41 + Debug 14 + Error 8等）

## QD_V2.3_JiYuRebuild_CoUI-Glass (2026-09-09) - 最终审核与资源修复

### 代码最终审核
- 总代码量：6032行（C# + XAML），16个.cs文件 + 2个.xaml文件
- 70个Click事件全部有对应方法
- 12个页面控件XAML和CS引用完整
- 11个服务文件全部在csproj中编译
- 编译结果：0错误，0警告

### 修复ShowPage页面切换bug
- 修复case "screenshot"被case "teachersim"吞掉的问题
- 导致截图替换页面无法切换，代码无法访问

### Assets图片嵌入资源修复
- 将6个Assets文件从Content改为Resource，嵌入exe内部
- 修复发布版缺少Assets文件夹导致XamlParseException启动崩溃
- XAML图片引用改为pack URI格式：pack://application:,,,/学习不通;component/Assets/xxx
- 修复系统托盘图标从文件路径加载改为GetResourceStream从嵌入资源加载
- 修复StreamResourceInfo命名空间（System.Windows.Resources）
- exe体积：813KB → 1291KB（含图片资源）

### 隐藏所有滚动条
- 将所有ScrollViewer的VerticalScrollBarVisibility从Auto改为Hidden
- 涉及4个页面：UDP攻击、小小私聊、截图替换、教师端模拟

### 单文件打包
- 驱动JiYuTrainerDriver.sys和JiYuTrainerHooks.dll嵌入为EmbeddedResource
- 程序启动时自动释放到%TEMP%\学习不通\目录
- 文件已存在且大小相同时跳过释放
- 发布包仅需：学习不通.exe + Drivers\teacher_sim.exe

## QD_V2.3_JiYuRebuild_CoUI-Glass (2026-09-09) - 教师端模拟控制台修复与增强

### 教师端模拟控制台修复
- 修复控制台无反馈问题：teacher_sim.py的print()输出到stdout，之前只监控日志文件导致收不到输出
- 修复编码问题：teacher_sim.exe输出是GBK编码，改为Encoding.GetEncoding("GB2312")解码
- 修复Python stdout全缓冲问题：重新打包teacher_sim.exe，在文件开头添加sys.stdout.reconfigure(line_buffering=True)
- 添加OutputDataReceived事件异步接收stdout输出
- 添加ErrorDataReceived事件接收stderr输出
- 启动前验证teacher_sim.exe是否存在，不存在时显示错误提示
- 启动后等待2秒检查进程是否存活，启动失败显示退出码

### 教师端模拟功能增强
- 新增目标操作区域：目标IP输入框
- 新增快捷操作按钮：发送消息、黑屏锁定、解锁、关机、重启、屏幕预览、学生信息
- 关机/重启操作添加确认弹窗
- 发送消息自动填充命令到控制台输入框

### teacher_sim.py修改说明
- 仅添加3行：sys.stdout.reconfigure(line_buffering=True)和sys.stderr.reconfigure(line_buffering=True)
- 目的：确保重定向stdout时print()实时输出，不修改任何业务逻辑

## QD_V2.3_JiYuRebuild_CoUI-Glass (2026-09-09) - 教师端模拟与单文件打包

### 极域教师端模拟 (TeacherSimService)
- 新增TeacherSimService服务，管理teacher_sim.exe进程
- 通过重定向stdin发送命令，通过日志文件获取输出
- 模拟控制台UI：黑色终端风格，支持命令输入和实时日志显示
- 快捷命令按钮：列出学生、全部预览、黑屏全体、解锁全体、帮助
- 状态指示灯：未启动(灰)/运行中(绿)
- 频道配置：支持1-32频道
- 底栏新增"教师模拟"入口
- teacher_sim.exe放在Drivers目录，随程序分发

### 单文件打包 (EmbeddedResourceService)
- JiYuTrainerDriver.sys和JiYuTrainerHooks.dll嵌入为程序资源
- 程序启动时自动释放到%TEMP%\学习不通\目录
- 驱动和DLL从临时目录加载，不再需要外部Drivers文件夹
- exe体积从228KB增至808KB（含驱动+DLL）
- teacher_sim.exe(52MB)保持外部文件，不嵌入
- 文件已存在且大小相同时跳过释放，提升启动速度

## QD_V2.3_JiYuRebuild_CoUI-Glass (2026-09-09) - 错误报告与调试模式

### 错误报告服务 (CrashReportService)
- 程序异常时自动生成错误报告文件到exe目录
- 报告文件名: i.chaoxing_Crash_时间戳.txt (致命错误) / i.chaoxing_Error_时间戳.txt (功能异常)
- 报告内容: 异常位置(模块/功能)、异常信息、堆栈跟踪、内部异常链、系统信息、进程信息、加载的程序集
- 弹窗显示错误信息和报告路径
- 不需要在线提交
- UI线程异常 → 功能异常报告, 尝试继续运行
- 非UI线程异常 → 致命错误报告, 可能导致程序终止

### 调试模式控制日志
- DebugMode默认开启(true)
- 开启时: 生成i.chaoxing.log, 记录所有日志
- 关闭时: 不生成日志文件, 删除已有日志
- Logger新增Enable()/Disable()方法
- 启动时根据DebugMode决定是否启用日志
- 切换调试模式时实时启用/禁用并保存设置

### 修复: 初始化时设置保存错误
- 新增_isInitializing标志
- ApplySettingsToUI期间跳过SaveSettingsFromUI
- 避免初始化过程中CheckBox事件触发导致DebugMode被错误保存为false

---

## QD_V2.3_JiYuRebuild_CoUI-Glass (2026-09-09)

### 程序重命名
- exe文件名: i.chaoxing.exe → 学习不通.exe
- 进程名: i.chaoxing → 学习不通
- 窗口标题: 学习不通 → 学习不通2.0
- 界面显示文字统一为"学习不通2.0"
- GlassyEffect pack URI同步修改(关键,否则毛玻璃失效)
- 日志/设置/INI文件名保持 i.chaoxing.* 不变

### 文件命名统一
- 日志文件: JiYuKiller.log → i.chaoxing.log
- 设置文件: JiYuKillerSettings.xml → i.chaoxing.xml
- 与INI文件(i.chaoxing.ini)统一命名

### 截图替换功能增强
- 新增状态监测: 状态指示灯(圆点)+状态文字(已替换/未替换)
- 已替换: 绿色圆点+绿色文字+显示当前图片路径
- 未替换: 灰色圆点+灰色文字+提示尚未设置
- 新增"取消替换"按钮: 清除图片并立即应用,弹窗反馈
- 原"取消"按钮改名为"放弃选择"
- 按钮布局: 选择图片(蓝)→应用替换(绿)→取消替换(红)→放弃选择(灰)

### 底栏按钮排序与页面逻辑修复
- 底栏按钮重新排序: 快捷栏/控制台和设置/自定义设置/UDP攻击/小小私聊/截图替换/帮助文档/调试/关于
- "关于"按钮移到最后面
- 修复ShowPage()缺少PageChat/PageScreenshot隐藏导致页面不隐藏的bug
- 修复ShowPage()缺少NavChat/NavScreenshot样式重置导致按钮状态异常的bug

### 极域UDP攻击模块
- 完整移植JyUdpAttack协议(4个硬编码DMOC包)
- 支持发送消息/命令/关机/重启
- 局域网扫描: Ping并行+SendARP获取MAC,结果格式[MAC]-IP
- 本机信息显示: MAC+IP
- 发送成功/失败弹窗反馈
- 操作日志实时显示

### 小小私聊模块
- 座位号↔IP换算(6人一排布局,算法保持原样)
- 本机IP/座位号自动获取
- Ping检测同学在线状态
- UDP消息发送(格式「X号同学:消息」,UTF-16LE,端口4705)
- 聊天记录显示

### 核心功能完整移植(驱动 + DLL注入 + 控制器)

#### 内核驱动交互(DriverService.cs)
- 驱动加载/卸载: SCM服务管理,自动创建/删除服务
- 全部IOCTL控制码: 杀进程/终止/挂起/恢复/关机/重启/自我保护/初始化参数
- 驱动文件: JiYuTrainerDriver.sys(复用原项目)

#### DLL注入服务(DllInjectService.cs)
- CreateRemoteThread + LoadLibraryW注入/卸载
- WM_COPYDATA与DLL通信(消息格式hk:xxx,与原JiYuTrainerHooks.dll兼容)
- DLL回调消息处理: hkb:succ/jyk/gbmfull
- DLL文件: JiYuTrainerHooks.dll(复用原项目)

#### JiYuController完整重构
- 进程监控: 定时检测StudentMain.exe,CKInterval可配置
- DLL自动注入: 检测到极域后自动注入Hooks DLL
- 窗口处理: 广播窗口化(移除TOPMOST,调整为屏幕3/4大小居中)、黑屏处理
- 驱动管理: LoadDriver/UnloadDriver,自动发送系统版本参数
- 内核级杀进程: 驱动IOCTL + NtTerminateProcess原生API(3种模式)
- 解除网络控制: 卸载TDNetFilter驱动(sc stop/delete)
- 极域定位: 注册表+6种常见安装路径
- 停止时自动卸载注入的DLL

#### DLL设置传递机制(关键修复)
- 设置通过INI配置文件传递,非DLL消息
- 主程序写入i.chaoxing.ini的[JTSettings]节
- 发送hk:inipath:路径让DLL调用VInitSettings()重新读取
- 支持11个DLL设置项: AutoForceKill/AllowAllRunOp/BandAllRunOp/ProhibitKillProcess/ProhibitCloseWindow/DoNotShowVirusWindow/ForceDisableWatchDog/AllowGbTop/AllowMonitor/AllowControl/FakeScreenImage

### 软件高级设置(17项)
- 监控极域运行进程
- 禁止极域运行进程
- 允许屏幕广播窗口置顶
- 禁止极域结束进程
- 允许教师监视你的电脑
- 禁止极域关闭窗口
- 允许老师控制你的电脑
- 解除网络控制
- 选择极域主进程位置
- 检查间隔(CKInterval)
- 结束进程模式(KillProcess/TaskKill/NtTerminateProcess)
- 禁用内核驱动
- 驱动自我保护
- DLL注入模式(InjectMasterHelper)
- 禁止所有运行操作
- 强制关闭看门狗
- 不显示病毒窗口

### UI功能
- 快捷栏页面: 当前状态/极域控制/窗口控制/电源控制
- 本窗口置顶、杀死极域、重启极域
- 帮助文档页面: 介绍/快捷键/其他/免责声明
- 调试命令执行
- 关于页面 + 关于我页面
- 全局快捷键: Ctrl+Alt+F紧急全屏, Ctrl+Alt+H显示/隐藏
- 系统托盘: 关闭隐藏到托盘,双击显示/隐藏

### 液态玻璃悬浮底栏
- 悬浮式设计,7层液态玻璃结构
- 9个导航按钮: 快捷栏/控制台和设置/自定义设置/UDP攻击/小小私聊/截图替换/帮助文档/调试/关于
- iOS风格滑动指示器(CubicEase 350ms)
- 鼠标滚轮横向滚动,所有滚动条隐藏
- 每个按钮独立玻璃效果,悬停/按下动效

### 真正毛玻璃效果(移植自WPF-Liquid-Glass-Effect)
- 桌面截图背景 + HLSL像素着色器折射
- 自动刷新背景(窗口移动/大小变化/激活失活)
- 光感效果: 渐变边框 + 顶部高光
- 外层/中层光晕,圆角窗口
- 自定义设置: 模糊强度/内容透明度/光晕透明度

### 代码质量修复
- 修复_studentControlled永远为false
- 修复KillProcessMode设置未生效
- 修复UpdateSettings只发hk:reset
- 实现MasterHelper注入功能
- 实现NtTerminateProcess原生API调用(ntdll.dll P/Invoke)
- 清除已删除的自动更新功能残留代码
- 修复XAML中不存在的GlassCard样式导致启动崩溃

## 移植进度统计

### 已移植功能 (36/39 = 92.3%)
- 核心控制: 12/12 ✅
- 驱动/DLL: 8/8 ✅
- 增强功能: 3/4 (UDP攻击✅, 小小私聊✅, 截图替换✅, 教师端模拟❌)
- UI功能: 9/9 ✅
- 系统功能: 4/4 ✅

### 未移植/已删除功能 (3/39)
1. 教师端模拟(IMTeacher) - 原Python项目,需外部exe调用,复杂度高
2. 错误报告(BugReport) - 原项目有BugReportWindow,优先级低
3. 自动更新(Updater) - 用户要求删除

### 新增功能 (非原项目)
- 关于我页面
- 悬浮液态玻璃底栏
- 真正毛玻璃效果(HLSL着色器)
- 截图替换状态监测
- 统一日志系统(详细到行号/方法名)

## 程序信息
- 程序显示名称: 学习不通2.0
- exe文件名: 学习不通.exe
- 进程名: 学习不通
- 程序图标: JiYuTrainerLogo.ico
- 版本号: QD_V2.3_JiYuRebuild_CoUI-Glass
- 目标框架: .NET Framework 4.7.2
- 代码规模: C# 4402行(16文件) + XAML 997行(2文件)
- 日志文件: i.chaoxing.log(exe同目录)
- 设置文件: i.chaoxing.xml(exe同目录)
- INI文件: i.chaoxing.ini(exe同目录,DLL设置)

## 历史版本

### QD_V1.1_TrayIcon (2026-09-07)
- 系统托盘支持: 关闭/最小化隐藏到托盘
- 托盘双击显示/隐藏,右键菜单
- 窗口不可最大化

### QD_V1.0_WPF-Rebuild (2026-09-07)
- 从C++/Sciter迁移至C# .NET 4.7.2 + WPF
- 统一日志系统、设置持久化、进程监控、禁止运行、广播置顶控制
