# 宣传视频素材

20 秒宣传短片，两个画幅各一版：

| 文件 | 画幅 | 用在哪里 |
| --- | --- | --- |
| `WLMouse-promo-vertical.mp4` | 1080×1920（9:16） | 抖音 / 小红书 / 视频号 / 朋友圈 |
| `WLMouse-promo-horizontal.mp4` | 1920×1080（16:9） | B站 / YouTube / 官网 |

规格：H.264、25 fps、20.4 秒、约 3 MB。无音轨。

## 分镜

| 时间 | 画面 | 文案 |
| --- | --- | --- |
| 0–3.8s | 标题 | 任务栏上 / 直接看电量 |
| 3.8–8.4s | 任务栏实拍，控件高亮 | 电量常驻任务栏 |
| 8.4–13.2s | 设置界面 | 设备设置，一页搞定 |
| 13.2–17.0s | 低电量提示 | 低电量自动提醒 |
| 17.0–20.4s | 结尾 | 免费开源 + 仓库地址 |

转场为 0.45 秒交叉淡化，每段带轻微推镜。

## 重新生成

素材改动后重跑两个脚本即可（需要 ffmpeg）：

```powershell
powershell -ExecutionPolicy Bypass -File promo_build.ps1 -Mode vertical -OutDir v
powershell -ExecutionPolicy Bypass -File promo_build.ps1 -Mode horizontal -OutDir h
powershell -ExecutionPolicy Bypass -File promo_video.ps1 -Mode vertical -FFmpeg <ffmpeg路径>
powershell -ExecutionPolicy Bypass -File promo_video.ps1 -Mode horizontal -FFmpeg <ffmpeg路径>
```

`promo_build.ps1` 负责排版各场景静态图（`v/`、`h/` 下的 `s1..s5.png`），
`promo_video.ps1` 负责推镜与拼接。

## 素材来源

- 任务栏实拍：`WLMouseTaskbar.exe --fake-percent 85` 运行后截屏（鼠标休眠时用这个参数，渲染路径与真实读数一致）
- 设置界面：`--shot-ui ... --page device`
- 低电量卡片：`--shot-alert ... --alert-percent 15`

## 改文案时注意

- 中文必须用 UTF-8 **带 BOM** 保存，否则 PowerShell 会按 GBK 读，显示成乱码
- PowerShell 里拼接 ffmpeg 滤镜时，`"[$i:v]"` 会被当成带作用域的变量而变空，要写 `"[${i}:v]"`
- `xfade` 的 offset 是相对**左侧输入流起点**，不是成片长度：

  ```
  offset(n) = 前 n 段时长之和 - n × 转场时长
  ```
