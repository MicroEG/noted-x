param(
    [string]$Source = "docs\brand\noted-x-logo.png",
    [string]$PngOut = "src\NotedX.Desktop\Assets\appicon.png",
    [string]$IcoOut = "src\NotedX.Desktop\Assets\app.ico",
    [string]$PwaIconDirectory = "src\NotedX.MobileWeb\wwwroot\icons",
    [int]$CropX = 88,
    [int]$CropY = 437,
    [int]$CropWidth = 321,
    [int]$CropHeight = 333,
    [double]$ContentScale = 0.64,
    [double]$MaskableContentScale = 0.54
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$background = [System.Drawing.Color]::FromArgb(255, 0x15, 0x15, 0x14)
$foreground = [System.Drawing.Color]::FromArgb(255, 0xF0, 0xEF, 0xF0)

function Resolve-RepoPath([string]$path) {
    if ([System.IO.Path]::IsPathRooted($path)) { return $path }
    return Join-Path $PSScriptRoot $path
}

function Find-ContentBounds([System.Drawing.Bitmap]$bitmap) {
    $minX = $bitmap.Width
    $minY = $bitmap.Height
    $maxX = -1
    $maxY = -1

    for ($y = 0; $y -lt $bitmap.Height; $y++) {
        for ($x = 0; $x -lt $bitmap.Width; $x++) {
            $pixel = $bitmap.GetPixel($x, $y)
            if ($pixel.A -gt 128 -and $pixel.R -gt 200 -and $pixel.G -gt 200 -and $pixel.B -gt 200) {
                if ($x -lt $minX) { $minX = $x }
                if ($x -gt $maxX) { $maxX = $x }
                if ($y -lt $minY) { $minY = $y }
                if ($y -gt $maxY) { $maxY = $y }
            }
        }
    }

    if ($maxX -lt $minX -or $maxY -lt $minY) {
        throw "The badge crop does not contain the light note mark."
    }

    return [System.Drawing.Rectangle]::new($minX, $minY, $maxX - $minX + 1, $maxY - $minY + 1)
}

function New-BrandIcon([System.Drawing.Bitmap]$logo, [System.Drawing.Rectangle]$crop, [System.Drawing.Rectangle]$markBounds, [int]$size, [double]$scale) {
    $icon = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($icon)
    $graphics.Clear($background)
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality

    $factor = [Math]::Min(($size * $scale) / $markBounds.Width, ($size * $scale) / $markBounds.Height)
    $drawWidth = [int][Math]::Round($crop.Width * $factor)
    $drawHeight = [int][Math]::Round($crop.Height * $factor)
    $markCenterX = ($markBounds.Left + $markBounds.Right) / 2.0
    $markCenterY = ($markBounds.Top + $markBounds.Bottom) / 2.0
    $offsetX = [int][Math]::Round($size / 2.0 - $markCenterX * $factor)
    $offsetY = [int][Math]::Round($size / 2.0 - $markCenterY * $factor)

    $graphics.DrawImage(
        $logo,
        [System.Drawing.Rectangle]::new($offsetX, $offsetY, $drawWidth, $drawHeight),
        $crop,
        [System.Drawing.GraphicsUnit]::Pixel)
    $graphics.Dispose()

    # Flatten everything toward the plate colour so the badge's antialiased
    # outer edge does not show as a lighter frame on the black canvas.
    $rect = [System.Drawing.Rectangle]::new(0, 0, $size, $size)
    $data = $icon.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadWrite, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $stride = $data.Stride
    $bytes = New-Object byte[] ($stride * $size)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)

    for ($y = 0; $y -lt $size; $y++) {
        for ($x = 0; $x -lt $size; $x++) {
            $offset = $y * $stride + $x * 4
            $b = $bytes[$offset]
            $g = $bytes[$offset + 1]
            $r = $bytes[$offset + 2]
            $luma = (0.2126 * $r + 0.7152 * $g + 0.0722 * $b) / 255.0
            $coverage = [Math]::Min(1.0, [Math]::Max(0.0, ($luma - 0.35) / 0.50))

            $bytes[$offset] = [byte][Math]::Round($background.B + ($foreground.B - $background.B) * $coverage)
            $bytes[$offset + 1] = [byte][Math]::Round($background.G + ($foreground.G - $background.G) * $coverage)
            $bytes[$offset + 2] = [byte][Math]::Round($background.R + ($foreground.R - $background.R) * $coverage)
            $bytes[$offset + 3] = 255
        }
    }

    [System.Runtime.InteropServices.Marshal]::Copy($bytes, 0, $data.Scan0, $bytes.Length)
    $icon.UnlockBits($data)
    return $icon
}

function Save-ScaledPng([System.Drawing.Bitmap]$source, [int]$size, [string]$path) {
    $bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.Clear($background)
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $graphics.DrawImage($source, 0, 0, $size, $size)
    $graphics.Dispose()
    $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
}

function Write-Ico([System.Drawing.Bitmap]$source, [int[]]$sizes, [string]$path) {
    $pngBlobs = @()
    foreach ($size in $sizes) {
        $bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $graphics.Clear($background)
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.DrawImage($source, 0, 0, $size, $size)
        $graphics.Dispose()

        $memory = [System.IO.MemoryStream]::new()
        $bitmap.Save($memory, [System.Drawing.Imaging.ImageFormat]::Png)
        $pngBlobs += ,$memory.ToArray()
        $memory.Dispose()
        $bitmap.Dispose()
    }

    $stream = [System.IO.MemoryStream]::new()
    $writer = [System.IO.BinaryWriter]::new($stream)
    $writer.Write([UInt16]0)
    $writer.Write([UInt16]1)
    $writer.Write([UInt16]$sizes.Count)

    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $size = $sizes[$i]
        $blob = $pngBlobs[$i]
        $dimension = if ($size -ge 256) { 0 } else { $size }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([UInt16]1)
        $writer.Write([UInt16]32)
        $writer.Write([UInt32]$blob.Length)
        $writer.Write([UInt32]$offset)
        $offset += $blob.Length
    }

    foreach ($blob in $pngBlobs) { $writer.Write($blob) }
    $writer.Flush()
    [System.IO.File]::WriteAllBytes($path, $stream.ToArray())
    $writer.Dispose()
    $stream.Dispose()
}

$sourcePath = Resolve-RepoPath $Source
$pngPath = Resolve-RepoPath $PngOut
$icoPath = Resolve-RepoPath $IcoOut
$pwaPath = Resolve-RepoPath $PwaIconDirectory

foreach ($directory in @(
    [System.IO.Path]::GetDirectoryName($pngPath),
    [System.IO.Path]::GetDirectoryName($icoPath),
    $pwaPath)) {
    [System.IO.Directory]::CreateDirectory($directory) | Out-Null
}

$loaded = [System.Drawing.Bitmap]::FromFile($sourcePath)
$logo = [System.Drawing.Bitmap]::new($loaded.Width, $loaded.Height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$copy = [System.Drawing.Graphics]::FromImage($logo)
$copy.DrawImage($loaded, 0, 0, $loaded.Width, $loaded.Height)
$copy.Dispose()
$loaded.Dispose()

$crop = [System.Drawing.Rectangle]::new($CropX, $CropY, $CropWidth, $CropHeight)
if ($crop.Left -lt 0 -or $crop.Top -lt 0 -or $crop.Right -gt $logo.Width -or $crop.Bottom -gt $logo.Height) {
    throw "The badge crop lies outside the source image."
}

$badge = $logo.Clone($crop, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$mark = Find-ContentBounds $badge

$icon = New-BrandIcon $logo $crop $mark 1024 $ContentScale
$icon.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)

Save-ScaledPng $icon 180 (Join-Path $pwaPath "icon-180.png")
Save-ScaledPng $icon 192 (Join-Path $pwaPath "icon-192.png")
Save-ScaledPng $icon 512 (Join-Path $pwaPath "icon-512.png")

$maskable = New-BrandIcon $logo $crop $mark 1024 $MaskableContentScale
Save-ScaledPng $maskable 512 (Join-Path $pwaPath "icon-maskable-512.png")
$maskable.Dispose()

Write-Ico $icon @(16, 20, 24, 32, 40, 48, 64, 128, 256) $icoPath
Write-Ico $icon @(16, 32, 48) (Join-Path $pwaPath "favicon.ico")

$icon.Dispose()
$badge.Dispose()
$logo.Dispose()

Write-Output "Icon written: $pngPath (1024)"
Write-Output "Icon written: $icoPath (16, 20, 24, 32, 40, 48, 64, 128, 256)"
Write-Output "PWA icons written: 180, 192, 512, maskable-512, favicon"
Write-Output "Mark bounds inside crop: $($mark.Width)x$($mark.Height)"
