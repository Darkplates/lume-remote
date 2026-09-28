$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $root 'assets'
$source = [Drawing.Image]::FromFile((Join-Path $assets 'brand-source.png'))
try {
    foreach ($size in @(128, 256)) {
        $bitmap = New-Object Drawing.Bitmap($size, $size)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
            $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.DrawImage($source, (New-Object Drawing.Rectangle(0, 0, $size, $size)))
            if ($size -eq 128) { $bitmap.Save((Join-Path $assets 'brand.png'), [Drawing.Imaging.ImageFormat]::Png) }
            else {
                $png = New-Object IO.MemoryStream
                try {
                    $bitmap.Save($png, [Drawing.Imaging.ImageFormat]::Png)
                    $file = [IO.File]::Create((Join-Path $assets 'lume.ico'))
                    $writer = New-Object IO.BinaryWriter($file)
                    try {
                        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]1)
                        $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([byte]0)
                        $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$png.Length); $writer.Write([uint32]22)
                        $writer.Write($png.ToArray())
                    } finally { $writer.Dispose(); $file.Dispose() }
                } finally { $png.Dispose() }
            }
        } finally { $graphics.Dispose(); $bitmap.Dispose() }
    }
} finally { $source.Dispose() }
