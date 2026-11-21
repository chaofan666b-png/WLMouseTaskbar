# WL Mouse 电量小工具

在 Windows 上直接看 WLmouse 无线鼠标的电量，替代笨重的官方 WL MOUSE HUB
（Electron 应用，122 MB 安装包、开机自启、常驻几百 MB）。

本仓库包含两个程序，共用同一套 HID 协议实现：

| 目录 | 形态 | 说明 |
| --- | --- | --- |
| [`WLMouseTaskbar/`](WLMouseTaskbar/) | **任务栏挂件**（推荐） | 电量胶囊直接长在任务栏上，贴紧通知区域左侧，外观参考 FluentFlyout |
| [`WLMouseTray/`](WLMouseTray/) | 通知区托盘图标 | 早期版本，16×16 托盘图标显示百分比；同时提供协议诊断工具 `Probe/` |

```
┌──────────────────────────────────────────────────────────┐
│  ...  │ [🔋 90] │  ⌃  🔊  📶  🔋  14:29   │
└──────────────────────────────────────────────────────────┘
         ↑ WLMouseTaskbar
```

## 快速开始

```bash
cd WLMouseTaskbar
dotnet run
```

需要 .NET 9 桌面运行时。已发布的单文件版本在
[`WLMouseTaskbar/dist/WLMouseTaskbar.exe`](WLMouseTaskbar/dist/WLMouseTaskbar.exe)。

> 两个程序**不要同时运行**：它们会打开同一个 HID 接口并轮询，互相干扰。
> 用 WLMouseTaskbar 就够了，它已包含托盘图标和详情窗口。

## 技术要点

- 官方驱动是 Electron 应用，HID 逻辑在 `resources/app.asar` 的 WebHID 代码里。
  拆出来的 WLmouse 私有协议：厂商接口 `MI_02`、UsagePage `0xFFFF`、
  65 字节 Feature Report，电量查询命令 `0x83`。
- 接收器 `VID 0x36A7 / PID 0xA868`，**不暴露标准 HID 电量页面**，
  只能走厂商私有命令。
- 严格 UWP 做不了这件事：AppContainer 沙箱没有 `Shell_NotifyIcon`，
  也不能 `SetParent` 到任务栏。所以用 WPF + Win32 嵌入。

详细实现、踩过的坑和实测数据见各目录下的 README。

## 许可

未指定。如需复用请自行判断；其中的 HID 协议部分来自对官方驱动的逆向分析。
