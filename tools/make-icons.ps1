<#
  ClawHub app icon from an external asset (no hand-drawn art):
  Microsoft Fluent Emoji "Video game" 3D (MIT License, https://github.com/microsoft/fluentui-emoji).
  This script only downloads it and resizes it to the sizes Windows needs.
  Output: src/ClawApp/Assets/ClawHub.ico (16-256) + package logos (Square44x44, Square150x150, StoreLogo, Wide310x150).
#>
Add-Type -AssemblyName System.Drawing
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root 'src\ClawApp\Assets'
New-Item -ItemType Directory -Force $out | Out-Null

$source = Join-Path $out 'fluent-emoji-video-game-3d.png'
if (-not (Test-Path $source)) {
    Invoke-WebRequest 'https://raw.githubusercontent.com/microsoft/fluentui-emoji/main/assets/Video%20game/3D/video_game_3d.png' -OutFile $source -UseBasicParsing
}
$src = [System.Drawing.Image]::FromFile($source)

# Resize onto a transparent canvas; the emoji is centered and scaled to fit (padding = share of the short side).
function Resize([int]$w, [int]$h, [double]$padding = 0.0) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.PixelOffsetMode = 'HighQuality'
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = [Math]::Min($w, $h) * (1 - 2 * $padding)
    $g.DrawImage($src, [float](($w - $s) / 2), [float](($h - $s) / 2), [float]$s, [float]$s)
    $g.Dispose()
    return $bmp
}

function Save-Png($bmp, $name) { $bmp.Save((Join-Path $out $name), [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose() }

# ICO with PNG-compressed entries
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = foreach ($sz in $sizes) {
    $ms = New-Object System.IO.MemoryStream
    $b = Resize $sz $sz
    $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $b.Dispose()
    , $ms.ToArray()
}
$ico = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $ico
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]
    $bw.Write([byte]($sz % 256)); $bw.Write([byte]($sz % 256)); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$pngs[$i].Length); $bw.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
[System.IO.File]::WriteAllBytes((Join-Path $out 'ClawHub.ico'), $ico.ToArray())

# Package logos (Xbox mode home app / Game Bar widget)
Save-Png (Resize 44 44) 'Square44x44Logo.png'
Save-Png (Resize 150 150 0.12) 'Square150x150Logo.png'
Save-Png (Resize 50 50) 'StoreLogo.png'
Save-Png (Resize 310 150 0.1) 'Wide310x150Logo.png'
Save-Png (Resize 256 256) 'ClawHub-256.png'
$src.Dispose()
Get-ChildItem $out | ForEach-Object { "{0,-34} {1,7:N0} bytes" -f $_.Name, $_.Length }
