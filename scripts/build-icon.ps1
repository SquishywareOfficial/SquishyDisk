$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$repo = Split-Path $PSScriptRoot -Parent
$frames = @()
foreach ($size in @(32,64,256)) {
    $bitmap = [Drawing.Bitmap]::new($size,$size)
    $g = [Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.ScaleTransform($size / 64.0, $size / 64.0)
    $brush = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#2563eb'))
    $path = [Drawing.Drawing2D.GraphicsPath]::new()
    $path.AddArc(2,2,30,30,180,90); $path.AddArc(32,2,30,30,270,90); $path.AddArc(32,32,30,30,0,90); $path.AddArc(2,32,30,30,90,90); $path.CloseFigure()
    $g.FillPath($brush,$path)
    $outline = [Drawing.Pen]::new([Drawing.Color]::White,3)
    $g.DrawRectangle($outline,17,19,30,26)
    $bars = [Drawing.Pen]::new([Drawing.Color]::White,4)
    $bars.StartCap = $bars.EndCap = [Drawing.Drawing2D.LineCap]::Round
    $g.DrawLine($bars,23,38,23,29); $g.DrawLine($bars,32,38,32,23); $g.DrawLine($bars,41,38,41,32)
    $base = [Drawing.Pen]::new([Drawing.ColorTranslator]::FromHtml('#9dc3ff'),3)
    $base.StartCap = $base.EndCap = [Drawing.Drawing2D.LineCap]::Round
    $g.DrawLine($base,23,50,41,50)
    $stream = [IO.MemoryStream]::new()
    $bitmap.Save($stream,[Drawing.Imaging.ImageFormat]::Png)
    $frames += [pscustomobject]@{Size=$size;Bytes=$stream.ToArray()}
    $stream.Dispose(); $base.Dispose(); $bars.Dispose(); $outline.Dispose(); $path.Dispose(); $brush.Dispose(); $g.Dispose(); $bitmap.Dispose()
}
$output = [IO.File]::Create((Join-Path $repo 'src\SquishyDisk.App\Assets\app.ico'))
$writer = [IO.BinaryWriter]::new($output)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$frame.Bytes.Length); $writer.Write([uint32]$offset)
        $offset += $frame.Bytes.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
} finally { $writer.Dispose(); $output.Dispose() }
