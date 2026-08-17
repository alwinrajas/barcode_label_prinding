<#
.SYNOPSIS
    Generates the multi-resolution application icon from the product logo.

.DESCRIPTION
    Kept as a script, not a committed binary, so the icon can never drift from
    the logo it came from: change the logo and re-run this.

    The icon is cut from the ILLUSTRATION half of the logo, not the whole thing.
    The full lockup includes the "BARCODE PRINTER / PRINT LABEL TRACK" wordmark,
    which at 16x16 — a taskbar button, an Explorer row, an Alt-Tab entry — is a
    grey smear under a small picture. Cropping to the printer keeps the mark
    recognisable at every size Windows asks for. The full lockup is still used
    where there is room for it, on the sign-in window.

    Frames are stored as PNG inside the .ico. Windows has supported that since
    Vista, and it avoids hand-building the BMP colour/AND-mask pairs that the
    old format requires.

.EXAMPLE
    .\New-AppIcon.ps1
#>
[CmdletBinding()]
param(
    [string]$LogoPath = (Join-Path (Split-Path $PSScriptRoot) "logo.png"),
    [string]$OutputPath = (Join-Path (Split-Path $PSScriptRoot) "src\client\BarcodePrinter.Wpf\Assets\app.ico"),

    # Fraction of the logo's height occupied by the illustration, measured from
    # the top. The wordmark sits below it.
    [double]$IllustrationHeight = 0.62,

    [int[]]$Sizes = @(16, 24, 32, 48, 64, 128, 256)
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

if (-not (Test-Path $LogoPath)) { throw "Logo not found at $LogoPath" }
New-Item -ItemType Directory -Path (Split-Path $OutputPath) -Force | Out-Null

$logo = [System.Drawing.Image]::FromFile((Resolve-Path $LogoPath))
try {
    Write-Host "Source: $($logo.Width)x$($logo.Height)"

    # A square crop centred horizontally over the illustration. Square matters:
    # a non-square source stretched into a square icon slot looks subtly wrong
    # in a way people notice without being able to say why.
    $side = [int]($logo.Height * $IllustrationHeight)
    if ($side -gt $logo.Width) { $side = $logo.Width }
    $cropX = [int](($logo.Width - $side) / 2)
    $crop  = New-Object System.Drawing.Rectangle $cropX, 0, $side, $side
    Write-Host "Icon crop: $($side)x$side at ($cropX, 0)"

    $frames = [System.Collections.Generic.List[byte[]]]::new()
    foreach ($size in $Sizes) {
        $bitmap = New-Object System.Drawing.Bitmap $size, $size,
            ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.InterpolationMode  = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode    = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.SmoothingMode      = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
            $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
            $graphics.Clear([System.Drawing.Color]::Transparent)
            $graphics.DrawImage($logo, (New-Object System.Drawing.Rectangle 0, 0, $size, $size),
                                $crop, [System.Drawing.GraphicsUnit]::Pixel)
        } finally { $graphics.Dispose() }

        $stream = New-Object System.IO.MemoryStream
        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        $frames.Add($stream.ToArray())
        $stream.Dispose(); $bitmap.Dispose()
    }

    # ---- ICO container ----------------------------------------------------
    # ICONDIR: reserved, type=1 (icon), image count.
    # Then one 16-byte ICONDIRENTRY per frame, then the frame data.
    $output = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter $output
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$Sizes.Count)

    $offset = 6 + (16 * $Sizes.Count)
    for ($i = 0; $i -lt $Sizes.Count; $i++) {
        # 256 is written as 0: the field is one byte and 256 does not fit.
        $dimension = if ($Sizes[$i] -ge 256) { 0 } else { $Sizes[$i] }
        $writer.Write([byte]$dimension)      # width
        $writer.Write([byte]$dimension)      # height
        $writer.Write([byte]0)               # palette size (0 = truecolour)
        $writer.Write([byte]0)               # reserved
        $writer.Write([uint16]1)             # colour planes
        $writer.Write([uint16]32)            # bits per pixel
        $writer.Write([uint32]$frames[$i].Length)
        $writer.Write([uint32]$offset)
        $offset += $frames[$i].Length
    }
    foreach ($frame in $frames) { $writer.Write($frame) }
    $writer.Flush()

    [System.IO.File]::WriteAllBytes($OutputPath, $output.ToArray())
    $writer.Dispose(); $output.Dispose()

    # The same crop as a plain PNG, for use inside the application. WPF can
    # render an .ico, but it picks the nearest stored frame and scales it —
    # noticeably soft at the sizes the sign-in window uses, and worse again on
    # a high-DPI display.
    $markPath = Join-Path (Split-Path $OutputPath) "logo-mark.png"
    $mark = New-Object System.Drawing.Bitmap 512, 512,
        ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($mark)
    try {
        $graphics.InterpolationMode  = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode    = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.DrawImage($logo, (New-Object System.Drawing.Rectangle 0, 0, 512, 512),
                            $crop, [System.Drawing.GraphicsUnit]::Pixel)
    } finally { $graphics.Dispose() }
    $mark.Save($markPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $mark.Dispose()
    Write-Host "Wrote $markPath (512x512)" -ForegroundColor Green

    # The bootstrapper's UI logo. Separate from the .ico: the icon is what
    # Explorer shows for the file, this is the image drawn inside the setup
    # window. Left unset, WiX draws its own placeholder — the red circle with a
    # line through it, which looks like a failure rather than a default.
    # The standard theme reserves a 64x64 slot.
    $installerLogo = Join-Path $PSScriptRoot "installer-logo.png"
    $uiLogo = New-Object System.Drawing.Bitmap 64, 64,
        ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($uiLogo)
    try {
        $graphics.InterpolationMode  = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode    = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        # White, not transparent: the setup window has a light background and an
        # alpha-blended logo picks up whatever the theme paints behind it.
        $graphics.Clear([System.Drawing.Color]::White)
        $graphics.DrawImage($logo, (New-Object System.Drawing.Rectangle 0, 0, 64, 64),
                            $crop, [System.Drawing.GraphicsUnit]::Pixel)
    } finally { $graphics.Dispose() }
    $uiLogo.Save($installerLogo, [System.Drawing.Imaging.ImageFormat]::Png)
    $uiLogo.Dispose()
    Write-Host "Wrote $installerLogo (64x64)" -ForegroundColor Green

} finally { $logo.Dispose() }

Write-Host "Wrote $OutputPath ($([math]::Round((Get-Item $OutputPath).Length / 1KB)) KB, $($Sizes -join '/') px)" -ForegroundColor Green
