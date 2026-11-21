param(
    [ValidateSet('vertical', 'horizontal')] [string]$Mode = 'vertical',
    [string]$AssetDir = '.',
    [string]$OutDir = '.'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# ---------- 尺寸与配色 ----------
$isV = ($Mode -eq 'vertical')
$W = if ($isV) { 1080 } else { 1920 }
$H = if ($isV) { 1920 } else { 1080 }

$bgTop   = [System.Drawing.Color]::FromArgb(255, 20, 22, 28)
$bgBot   = [System.Drawing.Color]::FromArgb(255, 10, 11, 15)
$accent  = [System.Drawing.Color]::FromArgb(255, 76, 194, 255)
$white   = [System.Drawing.Color]::FromArgb(255, 255, 255, 255)
$grey    = [System.Drawing.Color]::FromArgb(255, 154, 163, 178)
$fontN   = 'Microsoft YaHei UI'

# ---------- 基础绘图 ----------
function New-Canvas {
    $bmp = New-Object System.Drawing.Bitmap -ArgumentList $W, $H
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    return @{ Bmp = $bmp; G = $g }
}

function Add-Glow($g, [single]$cx, [single]$cy, [single]$r, $color, [int]$alpha) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddEllipse(($cx - $r), ($cy - $r), (2 * $r), (2 * $r))
    $pbr = New-Object System.Drawing.Drawing2D.PathGradientBrush($path)
    $pbr.CenterColor = [System.Drawing.Color]::FromArgb($alpha, $color.R, $color.G, $color.B)
    $pbr.SurroundColors = @([System.Drawing.Color]::FromArgb(0, $color.R, $color.G, $color.B))
    $g.FillPath($pbr, $path)
    $pbr.Dispose(); $path.Dispose()
}

function Fill-Bg($g) {
    $rect = New-Object System.Drawing.Rectangle -ArgumentList 0, 0, $W, $H
    $lg = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $bgTop, $bgBot, 90)
    $g.FillRectangle($lg, $rect)
    $lg.Dispose()

    if ($isV) {
        Add-Glow $g ($W * 0.78) ($H * 0.10) ($W * 0.85) $accent 40
        Add-Glow $g ($W * 0.12) ($H * 0.88) ($W * 0.70) $accent 22
    } else {
        Add-Glow $g ($W * 0.80) ($H * 0.06) ($W * 0.55) $accent 40
        Add-Glow $g ($W * 0.10) ($H * 0.95) ($W * 0.45) $accent 22
    }
}

function New-Font([single]$size, [bool]$bold) {
    $style = if ($bold) { [System.Drawing.FontStyle]::Bold } else { [System.Drawing.FontStyle]::Regular }
    return New-Object System.Drawing.Font -ArgumentList $fontN, $size, $style, ([System.Drawing.GraphicsUnit]::Pixel)
}

function Measure-Text($g, [string]$text, [single]$size, [bool]$bold) {
    $f = New-Font $size $bold
    $sz = $g.MeasureString($text, $f)
    $f.Dispose()
    return $sz
}

# $h 为 0 时按字高自动；非 0 时在给定高度内垂直居中
function Draw-Text($g, [string]$text, [single]$size, [bool]$bold, $color,
                    [single]$x, [single]$y, [single]$w, [string]$align = 'near', [single]$h = 0) {
    $f = New-Font $size $bold
    $brush = New-Object System.Drawing.SolidBrush($color)
    $sf = New-Object System.Drawing.StringFormat
    $sf.Alignment = switch ($align) {
        'center' { [System.Drawing.StringAlignment]::Center }
        'right'  { [System.Drawing.StringAlignment]::Far }
        default  { [System.Drawing.StringAlignment]::Near }
    }
    $lineH = if ($h -gt 0) { $h } else { $size * 2.0 }
    if ($h -gt 0) { $sf.LineAlignment = [System.Drawing.StringAlignment]::Center }
    $rect = New-Object System.Drawing.RectangleF -ArgumentList ([single]$x), ([single]$y), ([single]$w), ([single]$lineH)
    $g.DrawString($text, $f, $brush, $rect, $sf)
    $f.Dispose(); $brush.Dispose(); $sf.Dispose()
}

function New-RoundPath([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    if ($d -gt $h) { $d = $h }
    if ($d -gt $w) { $d = $w }
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc(($x + $w - $d), $y, $d, $d, 270, 90)
    $p.AddArc(($x + $w - $d), ($y + $h - $d), $d, $d, 0, 90)
    $p.AddArc($x, ($y + $h - $d), $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Draw-Shadow($g, [single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    for ($i = 14; $i -ge 1; $i--) {
        $a = [int](3 + 2.6 * $i)
        if ($a -gt 34) { $a = 34 }
        $p = New-RoundPath ($x - $i) ($y - $i + 3) ($w + 2 * $i) ($h + 2 * $i) ($r + $i)
        $b = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb($a, 0, 0, 0))
        $g.FillPath($b, $p)
        $b.Dispose(); $p.Dispose()
    }
}

function Draw-Card($g, [string]$path, [single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    Draw-Shadow $g $x $y $w $h $r

    $p = New-RoundPath $x $y $w $h $r
    $state = $g.Save()
    $g.SetClip($p, [System.Drawing.Drawing2D.CombineMode]::Intersect)
    $img = [System.Drawing.Image]::FromFile($path)
    $g.DrawImage($img, $x, $y, $w, $h)
    $img.Dispose()
    $g.Restore($state)

    $pen = New-Object System.Drawing.Pen -ArgumentList ([System.Drawing.Color]::FromArgb(46, 255, 255, 255)), 1.6
    $g.DrawPath($pen, $p)
    $pen.Dispose(); $p.Dispose()
}

# 半透明强调色圆角框，用于指出任务栏上的控件
function Draw-Marker($g, [single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-RoundPath $x $y $w $h $r
    $fill = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(26, $accent.R, $accent.G, $accent.B))
    $g.FillPath($fill, $p)
    $fill.Dispose()
    $pen = New-Object System.Drawing.Pen -ArgumentList ([System.Drawing.Color]::FromArgb(190, $accent.R, $accent.G, $accent.B)), 2.4
    $g.DrawPath($pen, $p)
    $pen.Dispose(); $p.Dispose()
}

# 一排居中的标签
function Draw-ChipRow($g, [string[]]$texts, [single]$y, [single]$size) {
    $gap = 18
    $widths = @()
    $total = 0
    foreach ($t in $texts) {
        $sz = Measure-Text $g $t $size $false
        $cw = $sz.Width + 48
        $widths += $cw
        $total += $cw
    }
    $total += $gap * ($texts.Count - 1)

    $x = ($W - $total) / 2
    for ($i = 0; $i -lt $texts.Count; $i++) {
        Draw-Chip $g $texts[$i] ($x + $widths[$i] / 2) $y $size
        $x += $widths[$i] + $gap
    }
}

function Draw-Chip($g, [string]$text, [single]$cx, [single]$y, [single]$size) {
    $sz = Measure-Text $g $text $size $false
    $padX = 24
    $h = $size + 28
    $w = $sz.Width + 2 * $padX
    $x = $cx - $w / 2

    $p = New-RoundPath $x $y $w $h ($h / 2)
    $fill = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(26, $accent.R, $accent.G, $accent.B))
    $g.FillPath($fill, $p)
    $fill.Dispose()
    $pen = New-Object System.Drawing.Pen -ArgumentList ([System.Drawing.Color]::FromArgb(80, $accent.R, $accent.G, $accent.B)), 1.4
    $g.DrawPath($pen, $p)
    $pen.Dispose(); $p.Dispose()

    Draw-Text $g $text $size $false $accent $x $y $w 'center' $h
}

function Save-Canvas($canvas, [string]$path) {
    $canvas.Bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $canvas.G.Dispose()
    $canvas.Bmp.Dispose()
}

$tbReal   = Join-Path $AssetDir 'promo/tb_real.png'
$uiDevice = Join-Path $AssetDir 'ui_device.png'
$uiOver   = Join-Path $AssetDir 'ui_overview.png'
$alert    = Join-Path $AssetDir 'alert_clean.png'
$widget   = Join-Path $AssetDir 'widget_snap.png'

# =====================================================================
# 场景 1：开场
# =====================================================================
$c = New-Canvas; Fill-Bg $c.G
if ($isV) {
    Draw-Chip $c.G '开源 · 免安装 · 0.9 MB' ($W / 2) 640 28
    Draw-Text $c.G '任务栏上' 94 $true $white 0 800 $W 'center'
    Draw-Text $c.G '直接看电量' 94 $true $accent 0 915 $W 'center'
    Draw-Text $c.G 'WLmouse 无线鼠标 · 不用再打开官方驱动' 30 $false $grey 0 1075 $W 'center'
} else {
    Draw-Chip $c.G '开源 · 免安装 · 0.9 MB' ($W / 2) 300 30
    Draw-Text $c.G '任务栏上直接看电量' 102 $true $white 0 420 $W 'center'
    Draw-Text $c.G 'WLmouse 无线鼠标 · 不用再打开官方驱动' 34 $false $grey 0 590 $W 'center'
}
Save-Canvas $c (Join-Path $OutDir 's1.png')

# =====================================================================
# 场景 2：任务栏实拍
# =====================================================================
$c = New-Canvas; Fill-Bg $c.G
# 控件在 tb_real.png（1600x248）里的实测包围盒：x 108..363，y 96..155
$wx0 = 108; $wx1 = 363; $wy0 = 96; $wy1 = 155

if ($isV) {
    Draw-Text $c.G '电量常驻任务栏' 58 $true $white 0 600 $W 'center'
    Draw-Text $c.G '和系统图标同一行，不占额外空间' 28 $false $grey 0 690 $W 'center'

    $tw = 940; $th = [single](248 * $tw / 1408)
    $tx = ($W - $tw) / 2; $ty = 780
    Draw-Card $c.G $tbReal $tx $ty $tw $th 10

    $sx = $tw / 1408
    Draw-Marker $c.G ($tx + $wx0 * $sx - 14) ($ty + $wy0 * $sx - 10) `
        (($wx1 - $wx0) * $sx + 28) (($wy1 - $wy0) * $sx + 20) 9

    Draw-ChipRow $c.G @('悬停看详情', '右键切 DPI', '充电显示闪电') 1000 25
    Draw-Text $c.G '自动避让任务栏上的其他挂件' 25 $false $grey 0 1090 $W 'center'
} else {
    Draw-Text $c.G '电量常驻任务栏' 58 $true $white 0 110 $W 'center'
    Draw-Text $c.G '和系统图标同一行，不占额外空间' 28 $false $grey 0 195 $W 'center'

    $tw = 1240; $th = [single](248 * $tw / 1408)
    $tx = ($W - $tw) / 2; $ty = 290
    Draw-Card $c.G $tbReal $tx $ty $tw $th 11

    $sx = $tw / 1408
    Draw-Marker $c.G ($tx + $wx0 * $sx - 16) ($ty + $wy0 * $sx - 12) `
        (($wx1 - $wx0) * $sx + 32) (($wy1 - $wy0) * $sx + 24) 10

    Draw-ChipRow $c.G @('悬停看详情', '右键切 DPI', '充电显示闪电', '自动避让其他挂件') 570 25
}
Save-Canvas $c (Join-Path $OutDir 's2.png')

# =====================================================================
# 场景 3：设备设置
# =====================================================================
$c = New-Canvas; Fill-Bg $c.G
if ($isV) {
    Draw-Text $c.G '设备设置，一页搞定' 54 $true $white 0 440 $W 'center'
    Draw-Text $c.G 'DPI · 回报率 · 抬升高度 · 去抖 · 休眠' 27 $false $grey 0 520 $W 'center'

    $iw = 940; $ih = [single](1050 * $iw / 1500)
    Draw-Card $c.G $uiDevice (($W - $iw) / 2) 610 $iw $ih 14
    Draw-Text $c.G '改完立刻生效，写进鼠标板载内存' 27 $false $grey 0 1330 $W 'center'
} else {
    $lx = 110; $lw = 700
    Draw-Text $c.G '设备设置' 58 $true $white $lx 300 $lw
    Draw-Text $c.G '一页搞定' 58 $true $accent $lx 380 $lw
    Draw-Text $c.G 'DPI · 回报率 · 抬升高度' 28 $false $grey $lx 500 $lw
    Draw-Text $c.G '角度吸附 · Motion Sync' 28 $false $grey $lx 545 $lw
    Draw-Text $c.G '去抖 · 休眠时间' 28 $false $grey $lx 590 $lw
    Draw-Text $c.G '改完立刻生效，写进板载内存' 26 $false $accent $lx 690 $lw

    $iw = 860; $ih = [single](1050 * $iw / 1500)
    Draw-Card $c.G $uiDevice ($W - $iw - 110) (($H - $ih) / 2) $iw $ih 13
}
Save-Canvas $c (Join-Path $OutDir 's3.png')

# =====================================================================
# 场景 4：低电量提示
# =====================================================================
$c = New-Canvas; Fill-Bg $c.G
if ($isV) {
    Draw-Text $c.G '低电量自动提醒' 58 $true $white 0 640 $W 'center'
    Draw-Text $c.G '跌破阈值时弹在任务栏上方' 28 $false $grey 0 730 $W 'center'

    $aw = 880; $ah = [single](175 * $aw / 744)
    Draw-Card $c.G $alert (($W - $aw) / 2) 830 $aw $ah 14
    Draw-Text $c.G '只在需要时弹一次，不打扰' 27 $false $grey 0 1090 $W 'center'
} else {
    $lx = 110; $lw = 720
    Draw-Text $c.G '低电量' 58 $true $white $lx 380 $lw
    Draw-Text $c.G '自动提醒' 58 $true $accent $lx 460 $lw
    Draw-Text $c.G '跌破阈值时弹在任务栏上方' 28 $false $grey $lx 580 $lw
    Draw-Text $c.G '只在需要时弹一次，不打扰' 26 $false $accent $lx 640 $lw

    $aw = 800; $ah = [single](175 * $aw / 744)
    Draw-Card $c.G $alert ($W - $aw - 110) (($H - $ah) / 2) $aw $ah 13
}
Save-Canvas $c (Join-Path $OutDir 's4.png')

# =====================================================================
# 场景 5：结尾
# =====================================================================
$c = New-Canvas; Fill-Bg $c.G
if ($isV) {
    Draw-Text $c.G '免费开源' 76 $true $white 0 760 $W 'center'
    Draw-Text $c.G 'github.com/chaofan666b-png/WLMouseTaskbar' 30 $false $accent 0 890 $W 'center'
    Draw-Text $c.G 'Windows 11 · 需要 .NET 9 桌面运行时' 26 $false $grey 0 980 $W 'center'
    Draw-Chip $c.G '下载即用，无需安装' ($W / 2) 1070 27
} else {
    Draw-Text $c.G '免费开源' 78 $true $white 0 380 $W 'center'
    Draw-Text $c.G 'github.com/chaofan666b-png/WLMouseTaskbar' 32 $false $accent 0 500 $W 'center'
    Draw-Text $c.G 'Windows 11 · 需要 .NET 9 桌面运行时' 26 $false $grey 0 570 $W 'center'
    Draw-Chip $c.G '下载即用，无需安装' ($W / 2) 650 27
}
Save-Canvas $c (Join-Path $OutDir 's5.png')

Write-Output "scenes done: $Mode ${W}x${H}"
