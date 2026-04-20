# Generates src/GamePartyHud/app.ico — a red HP bar (75% full) inside a dark
# rounded square. Matches the in-app HUD aesthetic so the taskbar, Alt-Tab,
# and tray presence feel like one product.
#
# Usage (from repo root):
#   pwsh tools/generate-icon.ps1
#
# The script is idempotent: re-running overwrites the .ico. Commit the output.

param(
    [string]$OutPath = "src/GamePartyHud/app.ico"
)

Add-Type -AssemblyName System.Drawing

function New-PartyIconPng {
    param([int]$Size)

    $bmp = New-Object System.Drawing.Bitmap($Size, $Size)
    $g   = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.Clear([System.Drawing.Color]::Transparent)

    # Rounded-square dark background (matches MemberCard gradient).
    $radius = [Math]::Max(2, [int]($Size * 0.18))
    $bgRect = [System.Drawing.Rectangle]::new(0, 0, $Size, $Size)
    $path   = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($bgRect.X,                       $bgRect.Y,                        $radius * 2, $radius * 2, 180, 90)
    $path.AddArc($bgRect.Right  - $radius * 2,    $bgRect.Y,                        $radius * 2, $radius * 2, 270, 90)
    $path.AddArc($bgRect.Right  - $radius * 2,    $bgRect.Bottom - $radius * 2,     $radius * 2, $radius * 2,   0, 90)
    $path.AddArc($bgRect.X,                       $bgRect.Bottom - $radius * 2,     $radius * 2, $radius * 2,  90, 90)
    $path.CloseFigure()

    $bgBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $bgRect,
        [System.Drawing.Color]::FromArgb(0xFF, 0x26, 0x26, 0x29),
        [System.Drawing.Color]::FromArgb(0xFF, 0x15, 0x15, 0x18),
        [System.Drawing.Drawing2D.LinearGradientMode]::Vertical)
    $g.FillPath($bgBrush, $path)

    # Subtle outer stroke so the rounded square reads on any wallpaper.
    $strokePen = New-Object System.Drawing.Pen(
        [System.Drawing.Color]::FromArgb(0x55, 0xFF, 0xFF, 0xFF),
        [Math]::Max(1.0, $Size / 128.0))
    $g.DrawPath($strokePen, $path)

    # HP bar geometry — centered vertically, ~12.5% horizontal padding.
    $pad       = [Math]::Max(1, [int]($Size * 0.125))
    $barHeight = [Math]::Max(2, [int]($Size * 0.28))
    $barY      = [int](($Size - $barHeight) / 2)
    $barWidth  = $Size - 2 * $pad

    # Empty track behind the fill.
    $trackRect = [System.Drawing.Rectangle]::new($pad, $barY, $barWidth, $barHeight)
    $trackBrush = New-Object System.Drawing.SolidBrush(
        [System.Drawing.Color]::FromArgb(0xFF, 0x0E, 0x0E, 0x11))
    $g.FillRectangle($trackBrush, $trackRect)

    # Red fill — 75% of bar width. Gradient mirrors MemberCard's #FFFF3B3B → #FFC81919.
    $fillWidth = [int]($barWidth * 0.75)
    if ($fillWidth -lt 1) { $fillWidth = 1 }
    $fillRect  = [System.Drawing.Rectangle]::new($pad, $barY, $fillWidth, $barHeight)
    $fillBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $fillRect,
        [System.Drawing.Color]::FromArgb(0xFF, 0xFF, 0x3B, 0x3B),
        [System.Drawing.Color]::FromArgb(0xFF, 0xC8, 0x19, 0x19),
        [System.Drawing.Drawing2D.LinearGradientMode]::Vertical)
    $g.FillRectangle($fillBrush, $fillRect)

    # Inner white highlight on top of the fill.
    if ($Size -ge 32) {
        $hlH = [Math]::Max(1, [int]($barHeight * 0.22))
        $hlRect  = [System.Drawing.Rectangle]::new($pad + 1, $barY + 1, $fillWidth - 2, $hlH)
        $hlBrush = New-Object System.Drawing.SolidBrush(
            [System.Drawing.Color]::FromArgb(0x55, 0xFF, 0xFF, 0xFF))
        $g.FillRectangle($hlBrush, $hlRect)
    }

    # Thin border around the bar for definition.
    $barPen = New-Object System.Drawing.Pen(
        [System.Drawing.Color]::FromArgb(0x66, 0x00, 0x00, 0x00),
        1.0)
    $g.DrawRectangle($barPen, $trackRect)

    $strokePen.Dispose()
    $bgBrush.Dispose()
    $trackBrush.Dispose()
    $fillBrush.Dispose()
    $barPen.Dispose()
    $g.Dispose()

    $stream = New-Object System.IO.MemoryStream
    $bmp.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $bytes = $stream.ToArray()
    $stream.Dispose()
    # Comma operator prevents PowerShell 5.1 from unrolling the byte[] return value.
    return ,$bytes
}

function Write-Ico {
    param(
        [string]$Path,
        [int[]]$Sizes
    )

    $entries = @()
    foreach ($sz in $Sizes) {
        [byte[]]$png = New-PartyIconPng -Size $sz
        $entries += [pscustomobject]@{ Size = $sz; Png = $png }
    }

    $dir = [System.IO.Path]::GetDirectoryName($Path)
    if ($dir -and -not (Test-Path $dir)) {
        New-Item -ItemType Directory -Path $dir | Out-Null
    }

    $fs = [System.IO.File]::Create($Path)
    try {
        $bw = New-Object System.IO.BinaryWriter($fs)

        # ICONDIR header: reserved, type=1 (icon), count.
        $bw.Write([uint16]0)
        $bw.Write([uint16]1)
        $bw.Write([uint16]$entries.Count)

        # ICONDIRENTRY for each image.
        $offset = 6 + 16 * $entries.Count
        foreach ($e in $entries) {
            $sz = $e.Size
            $byteW = if ($sz -ge 256) { 0 } else { $sz }   # 0 means "256"
            $byteH = $byteW
            $bw.Write([byte]$byteW)           # width
            $bw.Write([byte]$byteH)           # height
            $bw.Write([byte]0)                # palette (0 = no palette)
            $bw.Write([byte]0)                # reserved
            $bw.Write([uint16]1)              # color planes
            $bw.Write([uint16]32)             # bits per pixel
            $bw.Write([uint32]$e.Png.Length)  # image byte size
            $bw.Write([uint32]$offset)        # image byte offset from file start
            $offset += $e.Png.Length
        }

        foreach ($e in $entries) {
            $bw.Write($e.Png)
        }

        $bw.Flush()
    } finally {
        $fs.Close()
    }
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
Write-Ico -Path $OutPath -Sizes $sizes
$fi = Get-Item $OutPath
Write-Output "Wrote $($fi.FullName) ($($fi.Length) bytes; sizes: $($sizes -join ', '))"
