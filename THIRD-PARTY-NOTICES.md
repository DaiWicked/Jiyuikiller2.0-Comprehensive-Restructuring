# 第三方组件与许可声明

本项目（学习不通 / Jiyuikiller2.0）以 MIT 许可发布，见 [LICENSE](LICENSE)。
以下是本项目使用或参考的第三方组件、其许可、来源，以及**本项目做了哪些改动**。
需要随附的许可原文都在 [third_party/](third_party/)。

| 组件 | 许可 | 来源 | 本项目如何使用 / 改动了什么 |
|---|---|---|---|
| JiYuTrainer | MIT | https://github.com/imengyu/JiYuTrainer | **本项目的上游**。本项目是其 fork（zsyn666 版）的 C#/WPF 重构二改：原版为 C++(Win32+Sciter)，本版重写为 WPF，界面、着色器、驱动通信层、注入判定均为新实现 |
| WPF-Liquid-Glass-Effect | MIT | https://github.com/dragosniamtu/WPF-Liquid-Glass-Effect | 底栏液态玻璃**像素着色器的原始来源**。本项目的 `Effects/NavBarLens.hlsl` 为自行重写：改用圆角矩形 SDF 几何、折射方向为向外取样、新增圆角加料(cornerBoost)与"边缘因子"（中心严格不变换），色散改为绝对像素量 |
| COUI (colorui) | Apache-2.0 | https://github.com/sshtunnelvision/colorui | 超椭圆(squircle)圆角算法与控件样式思路。本项目的 `Effects/SquircleGeometry.cs` 为 C#/WPF 重写（参数 1.2819 / 控制点 0.643），按钮/滑块样式为参照其设计思路自行实现，**未复制 Kotlin 代码** |
| liquid-glass-js | MIT | https://github.com/Armagan/liquid-glass-js | 仅参考其折射配方思路（外法线方向取样、圆角处额外折射），用于校正本项目底栏折射方向；**未复制其代码** |
| Jiyu_udp_attack | 见上游 | https://github.com/ht0Ruial/Jiyu_udp_attack | UDP 攻击功能原理参考 |
| Jiyu_replay_attack | MIT | https://github.com/weilycoder/Jiyu_replay_attack | 作为子功能合并 |
| MythwareToolkit | 见上游 | https://github.com/BengbuGuards/MythwareToolkit | 作为子功能合并 |
| Third-party-JiYu-Teacher-Endpoint | MIT | https://github.com/yunsjxh/Third-party-JiYu-Teacher-Endpoint | 教师端相关功能参考 / 作为子功能合并 |

## 说明

- 本项目**不使用**任何 NuGet 第三方包，仅依赖 .NET Framework 4.7.2 自带的框架程序集。
- 上游 JiYuTrainer 使用的 mhook / MemoryModule / XZip-XUnZip / curl / Sciter 等 C++ 组件属于**上游 C++ 版**，本 WPF 重构版未包含它们的源码或二进制。
- 若你是权利人且认为本项目中的使用方式不当，请开 issue 联系，我们会立即调整或移除。