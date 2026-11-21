param(
    [ValidateSet('vertical', 'horizontal')] [string]$Mode = 'vertical',
    [string]$Base = '.',
    [string]$FFmpeg = 'ffmpeg'
)

$ErrorActionPreference = 'Stop'

# 每个场景的时长（秒）：开场 3.4 / 任务栏 4.6 / 设置 4.8 / 提醒 3.8 / 结尾 3.4
$durations = @(3.8, 5.1, 5.3, 4.2, 3.8)
$fade = 0.45

$sub = if ($Mode -eq 'vertical') { 'v' } else { 'h' }
$dir = Join-Path $Base "promo/$sub"
$out = Join-Path $Base ("WLMouse-promo-$Mode.mp4")

# zoompan 的 s 参数要写成 WxH，不能写 iw:ih
if ($Mode -eq 'vertical') {
    $frameW = 1080; $frameH = 1920
} else {
    $frameW = 1920; $frameH = 1080
}

# 缓推幅度与速度（每帧增量），帧率固定 25
$fps = 25
$zoomTo = 1.06

# ---------- 输入与滤镜 ----------
$inputArgs = @()
for ($i = 1; $i -le 5; $i++) {
    $inputArgs += @('-loop', '1', '-t', "$($durations[$i-1])", '-i', (Join-Path $dir "s$i.png"))
}

# 每段做缓慢推镜（Ken Burns），再交叉淡化拼接
$filters = @()
for ($i = 0; $i -lt 5; $i++) {
    $d = $durations[$i]
    # 从 1.00 缓慢推到 1.06，视觉上像镜头轻推
    # ${i} 不能省：PowerShell 会把 "$i:v" 当成带作用域的变量名（$i:v），结果为空
    $frames = [math]::Round($d * $fps)
    # 增量用不变文化格式化，避免某些区域设置把小数点写成逗号，ffmpeg 会解析失败
    $delta = ([math]::Round(($zoomTo - 1) / $frames, 6)).ToString([System.Globalization.CultureInfo]::InvariantCulture)
    $filters += "[${i}:v]zoompan=z='1+${delta}*on':d=1:s=${frameW}x${frameH}:fps=${fps},setsar=1,format=yuv420p[v${i}]"
}

# xfade 拼接。
#
# offset 的含义容易搞错：它是**相对「左侧输入流」自身起点**的时间点，不是相对
# 最终成片的长度。而左侧流每接一段，长度都会短掉一个转场时长（两段重叠），
# 所以第 n 段的 offset = 前 n 段时长之和 - n * fade。
# 上一版把它当成累计成片长度，结果第二段一开始就衔接，总时长被砍到 7.5 秒。
$chain = '[v0][v1]xfade=transition=fade:duration=' + $fade + ':offset=' + [math]::Round($durations[0] - $fade, 3) + '[x1]'
$acc = $durations[0] + $durations[1]
$chain += ';' + '[x1][v2]xfade=transition=fade:duration=' + $fade + ':offset=' + [math]::Round($acc - 2 * $fade, 3) + '[x2]'
$acc = $acc + $durations[2]
$chain += ';' + '[x2][v3]xfade=transition=fade:duration=' + $fade + ':offset=' + [math]::Round($acc - 3 * $fade, 3) + '[x3]'
$acc = $acc + $durations[3]
$chain += ';' + '[x3][v4]xfade=transition=fade:duration=' + $fade + ':offset=' + [math]::Round($acc - 4 * $fade, 3) + '[x4]'

$filter = ($filters -join ';') + ';' + $chain

Write-Output "building $Mode ..."
Write-Output "filter: $filter"

$cmd = @(
    '-y'
    '-hide_banner', '-loglevel', 'warning'
) + $inputArgs + @(
    '-filter_complex', $filter
    '-map', '[x4]'
    '-c:v', 'libx264'
    '-preset', 'slow'
    '-crf', '19'
    '-pix_fmt', 'yuv420p'
    '-r', '25'
    '-movflags', '+faststart'
    $out
)

& $FFmpeg @cmd

if ($LASTEXITCODE -ne 0) { throw "ffmpeg failed with $LASTEXITCODE" }
Write-Output "wrote $out"
