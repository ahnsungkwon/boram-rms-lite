# Render the project's own outlined icon into Windows ICO frames. No fonts/network.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName WindowsBase,PresentationCore,PresentationFramework
$assets = Join-Path $PSScriptRoot 'Assets'
$source = Join-Path $assets 'RmsBrand.xaml'
$resources = [System.Windows.Markup.XamlReader]::Parse([System.IO.File]::ReadAllText($source))
$image = $resources['RmsIcon']
if ($image -isnot [System.Windows.Media.DrawingImage]) { throw 'Missing RMS icon drawing.' }
$sizes = @(16,20,24,32,40,48,64,128,256)
$frames = [System.Collections.Generic.List[byte[]]]::new()
foreach ($size in $sizes) {
    $visual = [System.Windows.Media.DrawingVisual]::new()
    $dc = $visual.RenderOpen()
    $dc.PushTransform([System.Windows.Media.ScaleTransform]::new($size / 256.0,$size / 256.0))
    $dc.DrawDrawing($image.Drawing)
    $dc.Pop()
    $dc.Close()
    $bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new($size,$size,96,96,[System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [System.IO.MemoryStream]::new()
    $encoder.Save($stream)
    $frames.Add($stream.ToArray())
    $stream.Dispose()
}
$memory = [System.IO.MemoryStream]::new()
$writer = [System.IO.BinaryWriter]::new($memory)
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($index = 0; $index -lt $sizes.Count; $index++) {
    $edge = $sizes[$index] % 256
    $writer.Write([byte]$edge); $writer.Write([byte]$edge)
    $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([uint32]$frames[$index].Length); $writer.Write([uint32]$offset)
    $offset += $frames[$index].Length
}
foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
$writer.Flush()
$ico = Join-Path $assets 'rms-lite-green.ico'
$preview = Join-Path $assets 'rms-lite-green.png'
foreach ($entry in @(@($ico,$memory.ToArray()),@($preview,$frames[$frames.Count-1]))) {
    $path = [string]$entry[0]; $bytes = [byte[]]$entry[1]
    if ([System.IO.File]::Exists($path)) { throw ('Existing icon asset is not overwritten: ' + $path) }
    $file = [System.IO.File]::Open($path,[System.IO.FileMode]::CreateNew)
    try { $file.Write($bytes,0,$bytes.Length) } finally { $file.Dispose() }
}
$writer.Dispose(); $memory.Dispose()
Write-Output ('Icon created: ' + $ico)
Write-Output ('Frames: ' + ($sizes -join ', '))
