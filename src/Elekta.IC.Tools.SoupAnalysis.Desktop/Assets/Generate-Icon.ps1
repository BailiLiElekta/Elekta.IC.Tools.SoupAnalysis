# Run with Windows PowerShell in STA mode; no external graphics tools are needed.
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') {
    throw 'Use powershell.exe -NoProfile -STA -File .\Generate-Icon.ps1'
}
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

$source = Join-Path $PSScriptRoot 'SoupAnalysis.xaml'
$resources = [Windows.Markup.XamlReader]::Parse([IO.File]::ReadAllText($source))
$image = $resources['SoupAnalysisLogo']
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$frames = foreach ($size in $sizes) {
    # Supersampling keeps diagonal edges legible in small taskbar/title-bar frames.
    $visual = New-Object Windows.Media.DrawingVisual
    $context = $visual.RenderOpen()
    $context.DrawImage($image, (New-Object Windows.Rect(0, 0, ($size * 4), ($size * 4))))
    $context.Close()
    $large = New-Object Windows.Media.Imaging.RenderTargetBitmap(($size * 4), ($size * 4), 96, 96, ([Windows.Media.PixelFormats]::Pbgra32))
    $large.Render($visual)
    $scaled = New-Object Windows.Media.Imaging.TransformedBitmap($large, (New-Object Windows.Media.ScaleTransform(0.25, 0.25)))
    $encoder = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($scaled))
    $memory = New-Object IO.MemoryStream
    try {
        $encoder.Save($memory)
        [pscustomobject]@{ Size = $size; Bytes = $memory.ToArray() }
    }
    finally { $memory.Dispose() }
}

$output = Join-Path $PSScriptRoot 'SoupAnalysis.ico'
$stream = [IO.File]::Create($output)
$writer = New-Object IO.BinaryWriter($stream)
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
}
finally { $writer.Dispose() }
Write-Output "Generated $output with sizes: $($sizes -join ', ')"
