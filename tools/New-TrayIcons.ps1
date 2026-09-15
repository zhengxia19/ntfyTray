$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$projectRoot = Split-Path $PSScriptRoot -Parent
$assetPath = Join-Path $projectRoot 'src/NtfyTray/Assets'
foreach ($name in @('connected', 'disconnected')) {
    $source = [Drawing.Image]::FromFile((Join-Path $projectRoot "$name.png"))
    try {
        $frames = foreach ($size in @(16, 20, 24, 32, 40, 48, 64, 256)) {
            $bitmap = [Drawing.Bitmap]::new($size, $size)
            $graphics = [Drawing.Graphics]::FromImage($bitmap)
            $memory = [IO.MemoryStream]::new()
            try {
                $graphics.Clear([Drawing.Color]::Transparent)
                $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $scale = [Math]::Min($size / $source.Width, $size / $source.Height)
                $width = [int][Math]::Round($source.Width * $scale)
                $height = [int][Math]::Round($source.Height * $scale)
                $graphics.DrawImage($source, [int](($size-$width)/2), [int](($size-$height)/2), $width, $height)
                $bitmap.Save($memory, [Drawing.Imaging.ImageFormat]::Png)
                [pscustomobject]@{ Size=$size; Bytes=$memory.ToArray() }
            } finally { $graphics.Dispose(); $bitmap.Dispose(); $memory.Dispose() }
        }
        $file = [IO.File]::Create((Join-Path $assetPath "$name.ico"))
        $writer = [IO.BinaryWriter]::new($file)
        try {
            $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
            $offset = 6 + 16 * $frames.Count
            foreach ($frame in $frames) {
                $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
                $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
                $writer.Write([byte]0); $writer.Write([byte]0)
                $writer.Write([uint16]1); $writer.Write([uint16]32)
                $writer.Write([uint32]$frame.Bytes.Length); $writer.Write([uint32]$offset)
                $offset += $frame.Bytes.Length
            }
            foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
        } finally { $writer.Dispose(); $file.Dispose() }
    } finally { $source.Dispose() }
}

# Keep EXE and notification registration identity aligned with the supplied connected artwork.
Copy-Item -LiteralPath (Join-Path $assetPath 'connected.ico') -Destination (Join-Path $assetPath 'app.ico') -Force
