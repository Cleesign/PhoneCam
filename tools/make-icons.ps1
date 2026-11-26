# Draws the PhoneCam icon.
#   -IosPng  : 1024x1024 full-bleed PNG for the iPhone app (iOS rounds the corners itself)
#   -WinIco  : multi-size .ico for Windows (rounded square on a transparent background)
#   -Preview : optional 256x256 PNG of the Windows icon, to look at
param([string]$IosPng, [string]$WinIco, [string]$Preview)

Add-Type -AssemblyName System.Drawing

function New-RoundPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

# Draws the full-bleed artwork (1024 design units) into $g at $Size pixels.
function Draw-Art($g, [int]$Size) {
    $S = $Size / 1024.0
    function P([double]$v) { [float]($v * $S) }
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'

    $bgRect = New-Object System.Drawing.RectangleF 0, 0, $Size, $Size
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush $bgRect, ([System.Drawing.Color]::FromArgb(18, 32, 74)), ([System.Drawing.Color]::FromArgb(0, 150, 170)), 45.0
    $g.FillRectangle($bg, $bgRect)

    if ($Size -ge 48) {
        $glowPath = New-Object System.Drawing.Drawing2D.GraphicsPath
        $glowPath.AddEllipse((P 152), (P 152), (P 720), (P 720))
        $glow = New-Object System.Drawing.Drawing2D.PathGradientBrush $glowPath
        $glow.CenterColor = [System.Drawing.Color]::FromArgb(70, 255, 255, 255)
        $glow.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 255, 255, 255))
        $g.FillPath($glow, $glowPath)
    }

    $phone = New-RoundPath (P 302) (P 150) (P 420) (P 724) (P 86)
    $g.FillPath([System.Drawing.Brushes]::White, $phone)

    $cx = P 512; $cy = P 470
    function Circle($color, [double]$r) {
        $b = New-Object System.Drawing.SolidBrush $color
        $g.FillEllipse($b, [float]($cx - (P $r)), [float]($cy - (P $r)), [float](2 * (P $r)), [float](2 * (P $r)))
    }
    Circle ([System.Drawing.Color]::FromArgb(22, 30, 52)) 150
    Circle ([System.Drawing.Color]::FromArgb(60, 72, 100)) 118
    $irisRect = New-Object System.Drawing.RectangleF ([float]($cx - (P 96))), ([float]($cy - (P 96))), ([float](P 192)), ([float](P 192))
    $iris = New-Object System.Drawing.Drawing2D.LinearGradientBrush $irisRect, ([System.Drawing.Color]::FromArgb(0, 190, 210)), ([System.Drawing.Color]::FromArgb(30, 60, 160)), 60.0
    $g.FillEllipse($iris, $irisRect)
    Circle ([System.Drawing.Color]::FromArgb(14, 20, 40)) 48
    if ($Size -ge 32) {
        $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(230, 255, 255, 255))), [float]($cx - (P 62)), [float]($cy - (P 70)), (P 40), (P 40))
    }

    $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(235, 60, 60))), (P 622), (P 214), (P 46), (P 46))

    if ($Size -ge 32) {
        $dark = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(22, 30, 52))
        $g.FillPath($dark, (New-RoundPath (P 462) (P 780) (P 100) (P 34) (P 17)))
        $white = [System.Drawing.Brushes]::White
        $g.FillRectangle($white, (P 488), (P 874), (P 48), (P 60))
        $g.FillRectangle($white, (P 500), (P 934), (P 24), (P 100))
    }
}

function New-IosIcon([int]$Size) {
    $bmp = New-Object System.Drawing.Bitmap $Size, $Size, ([System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    Draw-Art $g $Size
    $g.Dispose()
    return $bmp
}

# Windows: artwork clipped to a rounded square with a small transparent margin.
function New-WinIcon([int]$Size) {
    $art = New-Object System.Drawing.Bitmap $Size, $Size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $ga = [System.Drawing.Graphics]::FromImage($art)
    Draw-Art $ga $Size
    $ga.Dispose()

    $bmp = New-Object System.Drawing.Bitmap $Size, $Size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $m = [Math]::Max(0.5, $Size * 0.04)
    $path = New-RoundPath $m $m ($Size - 2 * $m) ($Size - 2 * $m) (($Size - 2 * $m) * 0.22)
    $tb = New-Object System.Drawing.TextureBrush $art
    $g.FillPath($tb, $path)
    $g.Dispose(); $art.Dispose()
    return $bmp
}

if ($IosPng) {
    New-Item -ItemType Directory -Force (Split-Path $IosPng) | Out-Null
    $b = New-IosIcon 1024; $b.Save($IosPng, [System.Drawing.Imaging.ImageFormat]::Png); $b.Dispose()
    "Saved $IosPng"
}

if ($WinIco) {
    $sizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256
    $images = foreach ($s in $sizes) {
        $b = New-WinIcon $s
        $ms = New-Object System.IO.MemoryStream
        $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $b.Dispose()
        , $ms.ToArray()
    }
    $out = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $out
    $w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $s = $sizes[$i]; $len = $images[$i].Length
        $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
        $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
        $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([UInt16]1); $w.Write([UInt16]32)
        $w.Write([UInt32]$len); $w.Write([UInt32]$offset)
        $offset += $len
    }
    foreach ($img in $images) { $w.Write($img) }
    $w.Flush()
    [System.IO.File]::WriteAllBytes($WinIco, $out.ToArray())
    "Saved $WinIco ($($sizes -join ', ') px)"
}

if ($Preview) {
    $b = New-WinIcon 256; $b.Save($Preview, [System.Drawing.Imaging.ImageFormat]::Png); $b.Dispose()
    "Saved $Preview"
}
