param()
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$pngPath = [System.IO.Path]::GetFullPath((Join-Path $root 'BeatzOnKeys.UI\Assets\BeatzOnKeys.png'))
$icoPath = [System.IO.Path]::GetFullPath((Join-Path $root 'BeatzOnKeys.UI\Assets\BeatzOnKeys.ico'))
if ($pngPath -ne ($root + '\BeatzOnKeys.UI\Assets\BeatzOnKeys.png') -or
    $icoPath -ne ($root + '\BeatzOnKeys.UI\Assets\BeatzOnKeys.ico')) { throw 'Unexpected icon target.' }
if (-not (Test-Path -LiteralPath $pngPath -PathType Leaf)) { throw 'Source logo is missing.' }
# Observe the existing binary before replacing it.
if (Test-Path -LiteralPath $icoPath) {
    $previous = [System.IO.File]::ReadAllBytes($icoPath)
    Write-Host "Existing icon contains $([BitConverter]::ToUInt16($previous, 4)) frame(s)."
}
$sourceStream = [System.IO.MemoryStream]::new([System.IO.File]::ReadAllBytes($pngPath))
$source = [System.Drawing.Bitmap]::new($sourceStream)
$logo = [System.Drawing.Bitmap]::new(256, 256, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$graphics = [System.Drawing.Graphics]::FromImage($logo)
$path = [System.Drawing.Drawing2D.GraphicsPath]::new()
try {
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $path.AddEllipse(0, 0, 256, 256)
    $graphics.SetClip($path)
    $graphics.DrawImage($source, 0, 0, 256, 256)
}
finally {
    $path.Dispose()
    $graphics.Dispose()
    $source.Dispose()
    $sourceStream.Dispose()
}
$png = [System.IO.MemoryStream]::new()
try {
    $logo.Save($png, [System.Drawing.Imaging.ImageFormat]::Png)
    [System.IO.File]::WriteAllBytes($pngPath, $png.ToArray())
}
finally { $png.Dispose() }
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$frames = [System.Collections.Generic.List[byte[]]]::new()
try {
    foreach ($size in $sizes) {
        $bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $draw = [System.Drawing.Graphics]::FromImage($bitmap)
        $stream = [System.IO.MemoryStream]::new()
        try {
            $draw.Clear([System.Drawing.Color]::Transparent)
            $draw.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $draw.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $draw.DrawImage($logo, 0, 0, $size, $size)
            $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
            $frames.Add($stream.ToArray())
        }
        finally { $draw.Dispose(); $bitmap.Dispose(); $stream.Dispose() }
    }
}
finally { $logo.Dispose() }
$icon = [System.IO.MemoryStream]::new()
$writer = [System.IO.BinaryWriter]::new($icon)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
        $offset += $frames[$i].Length
    }
    foreach ($frame in $frames) { $writer.Write($frame) }
    $writer.Flush()
    [System.IO.File]::WriteAllBytes($icoPath, $icon.ToArray())
}
finally { $writer.Dispose(); $icon.Dispose() }
Write-Host "Created transparent round logo and $($sizes.Count) icon sizes: $($sizes -join ', ')."
