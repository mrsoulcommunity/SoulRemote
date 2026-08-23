<#
.SYNOPSIS
    Generates Soul Remote's icon and the installer's bitmaps from code.

.DESCRIPTION
    The app had no icon of its own: the window, the taskbar and the tray all fell
    back to the generic Windows application icon, and the installer would have used
    WiX's stock white artwork. Rather than commit opaque binaries nobody can edit,
    the artwork is drawn here from the same palette the WPF app uses
    (Resources/Palette.xaml), so a palette change can be followed through by
    re-running this script.

    Outputs:
      src/SoulRemote/Assets/app.ico   16..256px, PNG-compressed frames
      installer/banner.bmp            493x58  WixUI top banner
      installer/dialog.bmp            493x312 WixUI welcome/exit background
      installer/logo.png              64x64   setup.exe theme mark
      installer/logoside.png          220x460 setup.exe left rail
#>
[CmdletBinding()]
param(
    [string]$RepoRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# $PSScriptRoot is not bound yet while parameter defaults are evaluated, so the
# repository root is resolved here instead of in the param block.
if (-not $RepoRoot) { $RepoRoot = Split-Path -Parent $PSScriptRoot }

Add-Type -AssemblyName System.Drawing

# Palette.xaml, verbatim.
$Void       = [System.Drawing.Color]::FromArgb(255, 0x0B, 0x0F, 0x17)
$Deck       = [System.Drawing.Color]::FromArgb(255, 0x18, 0x20, 0x31)
$Signal     = [System.Drawing.Color]::FromArgb(255, 0x35, 0xE0, 0xC8)
$SignalDeep = [System.Drawing.Color]::FromArgb(255, 0x15, 0xA8, 0x96)
$Relay      = [System.Drawing.Color]::FromArgb(255, 0x7C, 0x6B, 0xFF)
$Ink        = [System.Drawing.Color]::FromArgb(255, 0xE8, 0xED, 0xF7)
$InkDim     = [System.Drawing.Color]::FromArgb(255, 0x8A, 0x97, 0xAD)

function New-RoundedPath {
    param([single]$X, [single]$Y, [single]$W, [single]$H, [single]$R)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $R * 2
    $path.AddArc($X, $Y, $d, $d, 180, 90)
    $path.AddArc($X + $W - $d, $Y, $d, $d, 270, 90)
    $path.AddArc($X + $W - $d, $Y + $H - $d, $d, $d, 0, 90)
    $path.AddArc($X, $Y + $H - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function Set-Quality {
    param([System.Drawing.Graphics]$G)
    $G.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $G.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $G.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $G.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit
}

# The mark: a dark tile carrying a power glyph in signal teal, with the relay violet
# picked up as a rim light. Drawn fresh at every size rather than downscaled from one
# master, because a 16px frame needs proportionally heavier strokes to survive.
function New-IconBitmap {
    param([int]$Size)

    $bmp = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    Set-Quality $g
    $g.Clear([System.Drawing.Color]::Transparent)

    $s = [single]$Size
    $inset  = [Math]::Max(0.5, $s * 0.02)
    $radius = $s * 0.235

    $tile = New-RoundedPath $inset $inset ($s - $inset * 2) ($s - $inset * 2) $radius
    $fill = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, 0)),
        (New-Object System.Drawing.PointF($s, $s)),
        $Deck, $Void)
    $g.FillPath($fill, $tile)
    $fill.Dispose()

    # A hairline rim keeps the tile from dissolving into a dark taskbar.
    if ($Size -ge 24) {
        $rimColour = [System.Drawing.Color]::FromArgb(90, $Relay.R, $Relay.G, $Relay.B)
        $rim = New-Object System.Drawing.Pen($rimColour, [Math]::Max(1.0, $s * 0.012))
        $g.DrawPath($rim, $tile)
        $rim.Dispose()
    }
    $tile.Dispose()

    # Power glyph: a ring broken at the top, closed by a vertical stem.
    $cx = $s / 2
    $cy = $s * 0.545
    $r  = $s * 0.255
    $stroke = [Math]::Max(1.6, $s * 0.105)

    $glyph = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, ($cy - $r))),
        (New-Object System.Drawing.PointF(0, ($cy + $r))),
        $Signal, $SignalDeep)
    $pen = New-Object System.Drawing.Pen($glyph, $stroke)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round

    $g.DrawArc($pen, ($cx - $r), ($cy - $r), ($r * 2), ($r * 2), -62, 304)
    $g.DrawLine($pen, $cx, ($cy - $r * 1.30), $cx, ($cy - $r * 0.10))

    $pen.Dispose()
    $glyph.Dispose()
    $g.Dispose()
    return $bmp
}

# A frame stored as a bottom-up 32bpp DIB: the format every version of Windows
# understands, including System.Drawing.Icon, which is what the tray uses.
function ConvertTo-IconDib {
    param([System.Drawing.Bitmap]$Bitmap)

    $w = $Bitmap.Width
    $h = $Bitmap.Height
    $rect = New-Object System.Drawing.Rectangle(0, 0, $w, $h)
    $data = $Bitmap.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
                             [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $pixels = New-Object byte[] ($data.Stride * $h)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $pixels, 0, $pixels.Length)
    $Bitmap.UnlockBits($data)

    # The AND mask is redundant for a 32bpp frame - alpha already carries the
    # transparency - but the structure is not optional, so it is written as zeroes.
    $maskStride = [int][Math]::Floor((($w + 31) / 32)) * 4
    $maskLength = $maskStride * $h

    $ms = New-Object System.IO.MemoryStream
    $w2 = New-Object System.IO.BinaryWriter($ms)
    $w2.Write([uint32]40)               # BITMAPINFOHEADER size
    $w2.Write([int32]$w)
    $w2.Write([int32]($h * 2))          # height covers the colour data and the mask
    $w2.Write([uint16]1)                # planes
    $w2.Write([uint16]32)               # bits per pixel
    $w2.Write([uint32]0)                # BI_RGB
    $w2.Write([uint32]($w * $h * 4 + $maskLength))
    $w2.Write([int32]0); $w2.Write([int32]0)    # pixels per metre
    $w2.Write([uint32]0); $w2.Write([uint32]0)  # palette

    for ($y = $h - 1; $y -ge 0; $y--) {
        $w2.Write($pixels, $y * $data.Stride, $w * 4)
    }
    $w2.Write((New-Object byte[] $maskLength))
    $w2.Flush()
    $bytes = $ms.ToArray()
    $w2.Dispose(); $ms.Dispose()
    # The leading comma stops PowerShell unrolling the array into loose bytes on the
    # way out, which would leave the caller holding object[] and BinaryWriter picking
    # an overload that writes almost nothing.
    return ,$bytes
}

function Save-Ico {
    param([string]$Path, [int[]]$Sizes)

    $frames = @()
    foreach ($size in $Sizes) {
        $bmp = New-IconBitmap -Size $size
        # PNG frames keep the large sizes small, but GDI+ cannot decode one back into
        # an Icon - and the tray does exactly that - so only 128px and above use PNG.
        if ($size -ge 128) {
            $ms = New-Object System.IO.MemoryStream
            $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
            $bytes = $ms.ToArray()
            $ms.Dispose()
        }
        else {
            $bytes = ConvertTo-IconDib -Bitmap $bmp
        }
        $bmp.Dispose()
        $frames += [pscustomobject]@{ Size = $size; Data = $bytes }
    }

    $out = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter($out)
    $w.Write([uint16]0)                 # reserved
    $w.Write([uint16]1)                 # type: icon
    $w.Write([uint16]$frames.Count)

    # Every frame is a PNG, which the ICO directory addresses by absolute offset, so
    # the whole directory has to be sized before the first byte of image data lands.
    $offset = 6 + (16 * $frames.Count)
    foreach ($frame in $frames) {
        $dim = if ($frame.Size -ge 256) { 0 } else { $frame.Size }
        $w.Write([byte]$dim)            # width (0 means 256)
        $w.Write([byte]$dim)            # height
        $w.Write([byte]0)               # palette size
        $w.Write([byte]0)               # reserved
        $w.Write([uint16]1)             # colour planes
        $w.Write([uint16]32)            # bits per pixel
        $w.Write([uint32]$frame.Data.Length)
        $w.Write([uint32]$offset)
        $offset += $frame.Data.Length
    }
    foreach ($frame in $frames) { $w.Write([byte[]]$frame.Data) }
    $w.Flush()

    [System.IO.File]::WriteAllBytes($Path, $out.ToArray())
    $w.Dispose(); $out.Dispose()
    Write-Host "  icon    $Path ($($frames.Count) frames)"
}

# WixUI blits both bitmaps at a fixed size and reads them as 24-bit BMP; anything
# else is either stretched or rejected outright.
function Save-Bmp {
    param([System.Drawing.Bitmap]$Bitmap, [string]$Path)
    $flat = New-Object System.Drawing.Bitmap($Bitmap.Width, $Bitmap.Height, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g = [System.Drawing.Graphics]::FromImage($flat)
    Set-Quality $g
    $g.Clear($Void)
    $g.DrawImage($Bitmap, 0, 0, $Bitmap.Width, $Bitmap.Height)
    $g.Dispose()
    $flat.Save($Path, [System.Drawing.Imaging.ImageFormat]::Bmp)
    $flat.Dispose()
    Write-Host "  bitmap  $Path ($($Bitmap.Width)x$($Bitmap.Height))"
}

function New-Backdrop {
    param([int]$Width, [int]$Height)
    $bmp = New-Object System.Drawing.Bitmap($Width, $Height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    Set-Quality $g

    $corner = [System.Drawing.Color]::FromArgb(255, 0x12, 0x18, 0x26)
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, 0)),
        (New-Object System.Drawing.PointF($Width, $Height)),
        $Void, $corner)
    $g.FillRectangle($bg, 0, 0, $Width, $Height)
    $bg.Dispose()
    return @{ Bitmap = $bmp; Graphics = $g }
}

# The three hops the app itself draws on its relay line: this PC, the Cloudflare
# edge, Telegram. Repeating it here ties the installer to what people see next.
function Add-RelayLine {
    param([System.Drawing.Graphics]$G, [single]$X, [single]$Y, [single]$Width, [single]$Dot)

    $trace = [System.Drawing.Color]::FromArgb(120, $Signal.R, $Signal.G, $Signal.B)
    $line = New-Object System.Drawing.Pen($trace, [Math]::Max(1.0, $Dot * 0.22))
    $line.DashStyle = [System.Drawing.Drawing2D.DashStyle]::Dot
    $G.DrawLine($line, $X, $Y, ($X + $Width), $Y)
    $line.Dispose()

    $colours = @($Signal, $Relay, $Signal)
    for ($i = 0; $i -lt 3; $i++) {
        $cx = $X + ($Width * $i / 2.0)
        $glow = [System.Drawing.Color]::FromArgb(60, $colours[$i].R, $colours[$i].G, $colours[$i].B)
        $halo = New-Object System.Drawing.SolidBrush($glow)
        $G.FillEllipse($halo, ($cx - $Dot), ($Y - $Dot), ($Dot * 2), ($Dot * 2))
        $halo.Dispose()
        $core = New-Object System.Drawing.SolidBrush($colours[$i])
        $G.FillEllipse($core, ($cx - $Dot / 2), ($Y - $Dot / 2), $Dot, $Dot)
        $core.Dispose()
    }
}

function Save-Banner {
    param([string]$Path)
    $b = New-Backdrop -Width 493 -Height 58
    $g = $b.Graphics
    # WixUI writes its own heading over the left of the banner, so the mark sits right.
    $icon = New-IconBitmap -Size 128
    $g.DrawImage($icon, 425, 9, 40, 40)
    $icon.Dispose()
    Add-RelayLine -G $g -X 330 -Y 29 -Width 70 -Dot 6
    $g.Dispose()
    Save-Bmp -Bitmap $b.Bitmap -Path $Path
    $b.Bitmap.Dispose()
}

function Save-Dialog {
    param([string]$Path)
    $b = New-Backdrop -Width 493 -Height 312
    $g = $b.Graphics
    # Only the leftmost ~164px stays uncovered on the welcome and exit dialogs.
    $icon = New-IconBitmap -Size 256
    $g.DrawImage($icon, 34, 62, 96, 96)
    $icon.Dispose()

    $title = New-Object System.Drawing.Font('Segoe UI', 15, [System.Drawing.FontStyle]::Bold)
    $sub   = New-Object System.Drawing.Font('Segoe UI', 8.5, [System.Drawing.FontStyle]::Regular)
    $inkBrush = New-Object System.Drawing.SolidBrush($Ink)
    $dimBrush = New-Object System.Drawing.SolidBrush($InkDim)
    $g.DrawString('Soul Remote', $title, $inkBrush, 30, 174)
    $g.DrawString('Your PC, from Telegram.', $sub, $dimBrush, 33, 205)
    $title.Dispose(); $sub.Dispose(); $inkBrush.Dispose(); $dimBrush.Dispose()

    Add-RelayLine -G $g -X 36 -Y 245 -Width 110 -Dot 9
    $g.Dispose()
    Save-Bmp -Bitmap $b.Bitmap -Path $Path
    $b.Bitmap.Dispose()
}


# --- setup.exe artwork -------------------------------------------------------
# Burn's theme blits these at a fixed size, so they are drawn at exactly the size
# SoulRemoteTheme.xml asks for. PNG rather than BMP: the sidebar is the whole left
# edge of the window and a 24-bit BMP would have no alpha for the mark to sit on.
#
# The names are not a choice. Burn payloads LogoFile and LogoSideFile under the name
# they already have, and the theme loads them by name, so logo.png and logoside.png is
# what both ends have to agree on - a mismatch is not a build error, it is setup.exe
# refusing to open at all.

function Save-Png {
    param([System.Drawing.Bitmap]$Bitmap, [string]$Path)
    $Bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    Write-Host "  png     $Path ($($Bitmap.Width)x$($Bitmap.Height))"
}

# The 64px mark the theme shows on its help and options pages.
function Save-BundleLogo {
    param([string]$Path)
    $bmp = New-IconBitmap -Size 64
    Save-Png -Bitmap $bmp -Path $Path
    $bmp.Dispose()
}

# The left rail of the setup window: the same dark ground, mark and wordmark the app
# opens with, so the installer and the app read as one thing.
function Save-BundleSide {
    param([string]$Path, [int]$Width = 220, [int]$Height = 460)

    $b = New-Backdrop -Width $Width -Height $Height
    $g = $b.Graphics

    # A relay-violet hairline down the right edge, exactly as the app draws between
    # its navigation rail and the stage.
    $edge = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, 0)),
        (New-Object System.Drawing.PointF(0, $Height)),
        [System.Drawing.Color]::FromArgb(0, $Relay.R, $Relay.G, $Relay.B),
        [System.Drawing.Color]::FromArgb(0, $Signal.R, $Signal.G, $Signal.B))
    $blend = New-Object System.Drawing.Drawing2D.ColorBlend(3)
    $blend.Colors = @(
        [System.Drawing.Color]::FromArgb(0, $Relay.R, $Relay.G, $Relay.B),
        [System.Drawing.Color]::FromArgb(128, $Relay.R, $Relay.G, $Relay.B),
        [System.Drawing.Color]::FromArgb(0, $Signal.R, $Signal.G, $Signal.B))
    $blend.Positions = @(0.0, 0.5, 1.0)
    $edge.InterpolationColors = $blend
    $g.FillRectangle($edge, ($Width - 1), 0, 1, $Height)
    $edge.Dispose()

    $icon = New-IconBitmap -Size 256
    $g.DrawImage($icon, 62, 96, 96, 96)
    $icon.Dispose()

    # Bahnschrift is the app's display face; Segoe UI is the fallback on a machine
    # that predates it, which is the same order Typography.xaml lists.
    $display = New-Object System.Drawing.FontFamily('Bahnschrift')
    $title = New-Object System.Drawing.Font($display, 19, [System.Drawing.FontStyle]::Bold)
    $sub   = New-Object System.Drawing.Font('Segoe UI', 8.5, [System.Drawing.FontStyle]::Regular)
    $inkBrush = New-Object System.Drawing.SolidBrush($Ink)
    $dimBrush = New-Object System.Drawing.SolidBrush($InkDim)

    $centred = New-Object System.Drawing.StringFormat
    $centred.Alignment = [System.Drawing.StringAlignment]::Center
    $box = New-Object System.Drawing.RectangleF(0, 210, $Width, 30)
    $g.DrawString('SOUL REMOTE', $title, $inkBrush, $box, $centred)
    $box = New-Object System.Drawing.RectangleF(0, 242, $Width, 22)
    $g.DrawString('Your PC, from Telegram.', $sub, $dimBrush, $box, $centred)

    $centred.Dispose(); $title.Dispose(); $display.Dispose(); $sub.Dispose()
    $inkBrush.Dispose(); $dimBrush.Dispose()

    Add-RelayLine -G $g -X 55 -Y 310 -Width 110 -Dot 9

    $g.Dispose()
    Save-Png -Bitmap $b.Bitmap -Path $Path
    $b.Bitmap.Dispose()
}

$assets = Join-Path $RepoRoot 'src\SoulRemote\Assets'
$installer = Join-Path $RepoRoot 'installer'
foreach ($dir in @($assets, $installer)) {
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
}

Write-Host 'Generating brand assets:'
Save-Ico    -Path (Join-Path $assets 'app.ico') -Sizes @(16, 20, 24, 32, 40, 48, 64, 128, 256)
Save-Banner -Path (Join-Path $installer 'banner.bmp')
Save-Dialog -Path (Join-Path $installer 'dialog.bmp')
Save-BundleLogo -Path (Join-Path $installer 'logo.png')
Save-BundleSide -Path (Join-Path $installer 'logoside.png')
Write-Host 'Done.'
