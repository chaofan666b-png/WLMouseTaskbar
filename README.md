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
│  ...  │ 🔋 90%  ⌃  🔊  📶  🔋  14:29   │
└──────────────────────────────────────────────────────────┘
         ↑ WLMouseTaskbar
```

除了电量，还能改鼠标本身的设置：**DPI 档位、回报率、抬升高度、角度吸附、
Motion Sync、去抖、休眠时间**——全部写进板载内存，改完立刻生效。
设置界面是 Windows 11「设置」那套 Fluent 布局。

## 下载

到 [Releases](https://github.com/chaofan666b-png/WLMouseTaskbar/releases/latest) 下载，
双击即用，无需安装、无需管理员权限。

| 文件 | 大小 | 说明 |
| --- | --- | --- |
| `...-framework-dependent.exe` | 约 0.9 MB | **推荐**，需要已装 [.NET 9 桌面运行时](https://dotnet.microsoft.com/download/dotnet/9.0) |
| `...-standalone.exe` | 约 69 MB | 不装运行时也能跑 |

## 从源码构建

```bash
cd WLMouseTaskbar
dotnet run
```

发布单文件：

```bash
dotnet publish -c Release -r win-x64 --self-contained false \
  -p:PublishSingleFile=true -p:DebugType=None -o dist
```

需要 .NET 9 桌面运行时。已发布的单文件版本在
[`WLMouseTaskbar/dist/WLMouseTaskbar.exe`](WLMouseTaskbar/dist/WLMouseTaskbar.exe)。

> 两个程序**不要同时运行**：它们会打开同一个 HID 接口并轮询，互相干扰。
> 用 WLMouseTaskbar 就够了，它已包含托盘图标和详情窗口。

## 技术要点

- 官方驱动是 Electron 应用，HID 逻辑在 `resources/app.asar` 的 WebHID 代码里。
  拆出来的 WLmouse 私有协议：厂商接口 `MI_02`、UsagePage `0xFFFF`、
  65 字节 Feature Report。电量是设备页命令 `0x83`，配置（DPI、回报率等）在配置页。
- 接收器 `VID 0x36A7 / PID 0xA868`，**不暴露标准 HID 电量页面**，
  只能走厂商私有命令。
- 严格 UWP 做不了这件事：AppContainer 沙箱没有 `Shell_NotifyIcon`，
  也不能 `SetParent` 到任务栏。所以用 WPF + Win32 嵌入。
- 任务栏挂件要「窗口铺满整条任务栏 + `SetWindowRgn` 裁剪」才能稳定绘制；
  小尺寸的 `WS_EX_LAYERED` + `WS_CHILD` 窗口合成表面会被丢弃，什么都不显示。
- 避让别的任务栏挂件时，要看它们的 **region** 而不是窗口矩形——那些人也是
  「铺满 + 裁剪」，窗口矩形是整条任务栏。

详细实现、踩过的坑和实测数据见各目录下的 README。

## 许可

未指定。如需复用请自行判断；其中的 HID 协议部分来自对官方驱动的逆向分析。
