$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class AeroIconResources { [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] public static extern IntPtr LoadLibraryEx(string file, IntPtr reserved, uint flags); [DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type); [DllImport("kernel32.dll")] public static extern uint SizeofResource(IntPtr module, IntPtr resource); [DllImport("kernel32.dll")] public static extern IntPtr LoadResource(IntPtr module, IntPtr resource); [DllImport("kernel32.dll")] public static extern IntPtr LockResource(IntPtr resource); [DllImport("kernel32.dll")] public static extern bool FreeLibrary(IntPtr module); }'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$ico = [IO.File]::ReadAllBytes((Join-Path $root 'Assets\AeroStart.ico'))
Add-Type -AssemblyName PresentationCore, WindowsBase
Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class AeroIconDecode { [DllImport("user32.dll", SetLastError=true)] public static extern IntPtr CreateIconFromResourceEx(byte[] bits, uint size, bool icon, uint version, int width, int height, uint flags); [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr icon); }'
$stream = [IO.MemoryStream]::new($ico)
try {
	$decoder = [Windows.Media.Imaging.IconBitmapDecoder]::new($stream, [Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat, [Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
	if ($decoder.Frames.Count -ne 7) { throw 'WPF dekodiert nicht sieben Frames.' }
	foreach ($i in 0..6) {
		$entry = 6 + 16 * $i
		$size = if ($ico[$entry] -eq 0) { 256 } else { [int]$ico[$entry] }
		$length = [BitConverter]::ToUInt32($ico, $entry + 8)
		$offset = [BitConverter]::ToUInt32($ico, $entry + 12)
		$frame = New-Object byte[] $length
		[Array]::Copy($ico, $offset, $frame, 0, $length)
		$handle = [AeroIconDecode]::CreateIconFromResourceEx($frame, $length, $true, 0x30000, $size, $size, 0)
		if ($handle -eq [IntPtr]::Zero) { throw "Native HICON-Dekodierung fehlgeschlagen: $size" }
		[void][AeroIconDecode]::DestroyIcon($handle)
		$converted = [Windows.Media.Imaging.FormatConvertedBitmap]::new($decoder.Frames[$i], [Windows.Media.PixelFormats]::Bgra32, $null, 0)
		$pixels = New-Object byte[] ($size * $size * 4)
		$converted.CopyPixels($pixels, $size * 4, 0)
		if ($pixels[3] -ne 0) { throw "Hintergrund nicht transparent: $size" }
		$visible = $false
		for ($p = 3; $p -lt $pixels.Length; $p += 4) { if ($pixels[$p] -ne 0) { $visible = $true; break } }
		if (!$visible) { throw "Leeres Icon: $size" }
		Write-Output "WPF-/HICON-Dekodierung, Transparenz und sichtbarer Inhalt geprueft: $size"
	}
} finally { $stream.Dispose() }
function Read-Resource($module, $name, $type) {
	$resource = [AeroIconResources]::FindResource($module, [IntPtr]$name, [IntPtr]$type)
	if ($resource -eq [IntPtr]::Zero) { throw "Native Resource $type/$name fehlt" }
	$size = [AeroIconResources]::SizeofResource($module, $resource)
	$pointer = [AeroIconResources]::LockResource([AeroIconResources]::LoadResource($module, $resource))
	$bytes = New-Object byte[] $size
	[Runtime.InteropServices.Marshal]::Copy($pointer, $bytes, 0, $bytes.Length)
	return ,$bytes
}
foreach ($configuration in @('Debug', 'Release')) {
	$directory = Join-Path $root "bin\$configuration\net10.0-windows"
	foreach ($extension in @('exe', 'dll')) {
		$file = Join-Path $directory "AeroStart.$extension"
		# Data-file loading does not execute or load the product's managed assembly.
		$module = [AeroIconResources]::LoadLibraryEx($file, [IntPtr]::Zero, 0x22)
		if ($module -eq [IntPtr]::Zero) { throw "LoadLibraryEx(DATAFILE): $file" }
		try {
			$group = Read-Resource $module 32512 14
			if ([BitConverter]::ToUInt16($group, 4) -ne 7) { throw "Sieben Frames fehlen: $file" }
			for ($i = 0; $i -lt 7; $i++) {
				$entry = 6 + 14 * $i
				$source = 6 + 16 * $i
				$size = if ($group[$entry] -eq 0) { 256 } else { [int]$group[$entry] }
				if ($size -ne @(16,20,24,32,48,64,256)[$i]) { throw "Falsche Framegroesse: $file" }
				$id = [BitConverter]::ToUInt16($group, $entry + 12)
				$actual = Read-Resource $module $id 3
				$length = [BitConverter]::ToUInt32($ico, $source + 8)
				$offset = [BitConverter]::ToUInt32($ico, $source + 12)
				$expected = New-Object byte[] $length
				[Array]::Copy($ico, $offset, $expected, 0, $length)
				if ([Convert]::ToBase64String($actual) -ne [Convert]::ToBase64String($expected)) { throw "Abweichender Iconinhalt: $file, $size" }
			}
			Write-Output "$configuration $extension : native Icongruppe und alle sieben Frames identisch zur ICO."
		} finally { [AeroIconResources]::FreeLibrary($module) | Out-Null }
	}
	$binary = [Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $directory 'AeroStart.dll')))
	# Embedded .resources contain the unchanged ICO; search independent of Base64 alignment.
	$found = $false
	foreach ($padding in @(0,1,2)) {
		$probe = New-Object byte[] ($ico.Length + $padding)
		[Array]::Copy($ico, 0, $probe, $padding, $ico.Length)
		$pattern = [Convert]::ToBase64String($probe)
		$pattern = $pattern.Substring(4, $pattern.Length - 8)
		if ($binary.Contains($pattern)) { $found = $true; break }
	}
	if (!$found) { throw "Eingebettete WPF-ICO nicht gefunden: $configuration" }
	Write-Output "$configuration : unveraenderte ICO in WPF-Assembly enthalten."
}
