$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$assets = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\Assets'))
$drawing = [Windows.Markup.XamlReader]::Parse([IO.File]::ReadAllText((Join-Path $assets 'AeroStart.xaml')))
$drawing.Freeze()
$sizes = @(16, 20, 24, 32, 48, 64, 256)
$frames = @()
foreach ($size in $sizes) {
	$visual = New-Object Windows.Media.DrawingVisual
	$context = $visual.RenderOpen()
	$context.DrawImage($drawing, [Windows.Rect]::new(0, 0, $size, $size))
	$context.Close()
	$bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($size, $size, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
	$bitmap.Render($visual)
	$encoder = New-Object Windows.Media.Imaging.PngBitmapEncoder
	$encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
	$memory = New-Object IO.MemoryStream
	try {
		$encoder.Save($memory)
		$bytes = $memory.ToArray()
		$frames += [pscustomobject]@{ Size = $size; Bytes = $bytes }
		[IO.File]::WriteAllBytes((Join-Path $assets ("AeroStart-{0}.png" -f $size)), $bytes)
	} finally { $memory.Dispose() }
}
$file = [IO.File]::Create((Join-Path $assets 'AeroStart.ico'))
$writer = [IO.BinaryWriter]::new($file)
try {
	$writer.Write([uint16]0)
	$writer.Write([uint16]1)
	$writer.Write([uint16]$frames.Count)
	$offset = 6 + 16 * $frames.Count
	foreach ($frame in $frames) {
		$dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
		$writer.Write([byte]$dimension)
		$writer.Write([byte]$dimension)
		$writer.Write([byte]0)
		$writer.Write([byte]0)
		$writer.Write([uint16]1)
		$writer.Write([uint16]32)
		$writer.Write([uint32]$frame.Bytes.Length)
		$writer.Write([uint32]$offset)
		$offset += $frame.Bytes.Length
	}
	foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
} finally { $writer.Dispose() }
[IO.File]::Copy((Join-Path $assets 'AeroStart-256.png'), (Join-Path $assets 'AeroStart-preview.png'), $true)
Write-Output ('Generated ICO sizes: ' + ($sizes -join ', ') + '; transparent PNG preview ready.')
