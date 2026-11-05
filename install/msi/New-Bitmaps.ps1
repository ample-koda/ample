# Draws the two pictures of the wizard (dark with the neon accents of ample-koda.dev) and the AMPLE icon:
#   dialog.bmp  493x312 (the side picture of the welcome and finish screens)
#   banner.bmp  493x58  (the strip at the top of the other screens)
# The generated .bmp files are committed; run this only when the look changes.
param([string]$Dir = $PSScriptRoot)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$icon = New-Object Drawing.Icon (Join-Path $Dir 'ample.ico'), 64, 64
function New-Picture([int]$w, [int]$h, [string]$path, [bool]$side) {
  $bmp = New-Object Drawing.Bitmap $w, $h, ([Drawing.Imaging.PixelFormat]::Format24bppRgb)
  $g = [Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'ClearTypeGridFit'; $g.InterpolationMode = 'HighQualityBicubic'
  $rect = New-Object Drawing.Rectangle 0, 0, $w, $h
  if ($side) {
    # the welcome picture is only 164 px wide on the left of the 493 px dialog; the rest of the bitmap is covered by the dialog text
    $bg = New-Object Drawing.Drawing2D.LinearGradientBrush $rect, ([Drawing.Color]::FromArgb(12, 14, 29)), ([Drawing.Color]::FromArgb(46, 20, 82)), 90
    $g.FillRectangle($bg, $rect)
    $pink = New-Object Drawing.Pen ([Drawing.Color]::FromArgb(110, 255, 79, 216)), 1
    $cyan = New-Object Drawing.Pen ([Drawing.Color]::FromArgb(90, 45, 226, 255)), 1
    for ($x = -60; $x -le 220; $x += 28) { $g.DrawLine($pink, 82, 215, $x, $h) }   # the lit grid floor
    for ($y = 222; $y -le $h; $y += 16) { $g.DrawLine($cyan, 0, $y, 164, $y) }
    $g.DrawIcon($icon, (New-Object Drawing.Rectangle 50, 36, 64, 64))
    $font = New-Object Drawing.Font 'Segoe UI Semibold', 17
    $g.DrawString('AMPLE', $font, ([Drawing.Brushes]::White), 36, 110)
    $small = New-Object Drawing.Font 'Segoe UI', 8.5
    $g.DrawString('beta', $small, (New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(255, 209, 102))), 38, 140)
    $white = New-Object Drawing.SolidBrush ([Drawing.Color]::White)
    $g.FillRectangle($white, 164, 0, $w - 164, $h)
  }
  else {
    $g.Clear([Drawing.Color]::White)
    $g.DrawIcon($icon, (New-Object Drawing.Rectangle ($w - 54), 4, 48, 48))
    $line = New-Object Drawing.Drawing2D.LinearGradientBrush (New-Object Drawing.Rectangle 0, ($h - 3), $w, 3), ([Drawing.Color]::FromArgb(45, 226, 255)), ([Drawing.Color]::FromArgb(255, 79, 216)), 0
    $g.FillRectangle($line, 0, $h - 3, $w, 3)
  }
  $g.Dispose(); $bmp.Save($path, [Drawing.Imaging.ImageFormat]::Bmp); $bmp.Dispose()
}
New-Picture 493 312 (Join-Path $Dir 'dialog.bmp') $true
New-Picture 493 58 (Join-Path $Dir 'banner.bmp') $false
