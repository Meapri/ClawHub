<#
  ClawHub app icon from external assets only (no hand-drawn art):
  Lucide "gamepad-2" glyph (ISC, https://lucide.dev, font src/ClawApp/Fonts/lucide.ttf or lucide-static)
  in white on a shadcn-style zinc-900 rounded square.
  Output: src/ClawApp/Assets/ClawHub.ico (16-256) + package logos (Square44x44, Square150x150, StoreLogo, Wide310x150).
  Usage: tools\make-icons.ps1 [-LucideTtf <path to lucide.ttf>]
#>
param([string]$LucideTtf)
Add-Type -AssemblyName System.Drawing
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root 'src\ClawApp\Assets'
New-Item -ItemType Directory -Force $out | Out-Null

if (-not $LucideTtf) {
    $LucideTtf = @((Join-Path $root 'src\ClawApp\Fonts\lucide.ttf'), (Join-Path $env:TEMP 'lucide-static\package\font\lucide.ttf')) |
        Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $LucideTtf) {
    $meta = Invoke-RestMethod 'https://registry.npmjs.org/lucide-static/latest'
    $tgz = Join-Path $env:TEMP 'lucide-static.tgz'
    Invoke-WebRequest $meta.dist.tarball -OutFile $tgz -UseBasicParsing
    $dir = Join-Path $env:TEMP 'lucide-static'
    New-Item -ItemType Directory -Force $dir | Out-Null
    tar -xzf $tgz -C $dir
    $LucideTtf = Join-Path $dir 'package\font\lucide.ttf'
}
$fonts = New-Object System.Drawing.Text.PrivateFontCollection
$fonts.AddFontFile($LucideTtf)
$family = $fonts.Families[0]
$glyph = [string][char]0xE0DF   # gamepad-2

$plate = [System.Drawing.Color]::FromArgb(255, 24, 24, 27)      # zinc-900 (#18181B)
$edge = [System.Drawing.Color]::FromArgb(255, 63, 63, 70)       # zinc-700 1px edge

function New-Icon([int]$w, [int]$h = $w, [double]$glyphScale = 0.62) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.TextRenderingHint = 'AntiAliasGridFit'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = [Math]::Min($w, $h)
    $x = ($w - $s) / 2; $y = ($h - $s) / 2
    $r = [Math]::Max(2, [int]($s * 0.22))
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($x, $y, $r * 2, $r * 2, 180, 90)
    $path.AddArc($x + $s - $r * 2 - 1, $y, $r * 2, $r * 2, 270, 90)
    $path.AddArc($x + $s - $r * 2 - 1, $y + $s - $r * 2 - 1, $r * 2, $r * 2, 0, 90)
    $path.AddArc($x, $y + $s - $r * 2 - 1, $r * 2, $r * 2, 90, 90)
    $path.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush $plate), $path)
    if ($s -ge 32) {
        $pen = [System.Drawing.Pen]::new($edge, [float][Math]::Max(1.0, $s / 128.0))
        $g.DrawPath($pen, $path)
    }
    $font = New-Object System.Drawing.Font $family, ([float]($s * $glyphScale)), ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
    $fmt = New-Object System.Drawing.StringFormat
    $fmt.Alignment = 'Center'; $fmt.LineAlignment = 'Center'
    $g.DrawString($glyph, $font, [System.Drawing.Brushes]::White, (New-Object System.Drawing.RectangleF 0, 0, $w, $h), $fmt)
    $g.Dispose()
    return $bmp
}

function Save-Png($bmp, $name) { $bmp.Save((Join-Path $out $name), [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose() }

# ICO with PNG-compressed entries
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = foreach ($sz in $sizes) {
    $ms = New-Object System.IO.MemoryStream
    $b = New-Icon $sz
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
Save-Png (New-Icon 44) 'Square44x44Logo.png'
Save-Png (New-Icon 150 150 0.5) 'Square150x150Logo.png'
Save-Png (New-Icon 50) 'StoreLogo.png'
Save-Png (New-Icon 310 150 0.5) 'Wide310x150Logo.png'
Save-Png (New-Icon 256) 'ClawHub-256.png'
Get-ChildItem $out -File | ForEach-Object { "{0,-28} {1,7:N0} bytes" -f $_.Name, $_.Length }
