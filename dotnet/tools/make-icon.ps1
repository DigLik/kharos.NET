# Собирает kharos.ico (PNG-кадры 16..256) и kharos.png из apps/web/public/favicon.png без внешних программ.
# Использование: pwsh dotnet/tools/make-icon.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$src = [System.Drawing.Image]::FromFile((Join-Path $repo 'apps/web/public/favicon.png'))
$outDir = Join-Path $PSScriptRoot '../apps/Kharos.Desktop'
function Get-Png([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
    $g.DrawImage($src, 0, 0, $size, $size)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}
$sizes = 16, 32, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) { , (Get-Png $s) }
$ms = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $ms
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $d = $pngs[$i]
    $w.Write([byte]($s -band 255)); $w.Write([byte]($s -band 255)); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$d.Length); $w.Write([uint32]$offset)
    $offset += $d.Length
}
foreach ($d in $pngs) { $w.Write($d) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $outDir 'kharos.ico'), $ms.ToArray())
[System.IO.File]::WriteAllBytes((Join-Path $outDir 'kharos.png'), (Get-Png 256))
$src.Dispose()
Write-Host "kharos.ico, kharos.png -> $outDir"
