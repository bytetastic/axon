# Erzeugt src\Axon\Assets\axon.ico: ein "A" aus Neuronen-Knoten auf dunkelgruenem Grund.
# Laeuft mit Windows PowerShell 5.1 (System.Drawing), reines ASCII.
param([string]$Ziel = (Join-Path $PSScriptRoot '..\src\Axon\Assets\axon.ico'))

Add-Type -AssemblyName System.Drawing

function New-RoundRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-IconPng([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $rect = New-RoundRect 0 0 ($s - 1) ($s - 1) ($s * 0.23)
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, 0)), (New-Object System.Drawing.PointF($s, $s)),
        [System.Drawing.Color]::FromArgb(255, 14, 38, 28), [System.Drawing.Color]::FromArgb(255, 4, 11, 8))
    $g.FillPath($bg, $rect)
    if ($s -ge 32) {
        $rand = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(150, 46, 120, 86), [single]([Math]::Max(1, $s / 64)))
        $g.DrawPath($rand, $rect)
    }

    $apex = New-Object System.Drawing.PointF(($s * 0.5), ($s * 0.21))
    $bl   = New-Object System.Drawing.PointF(($s * 0.23), ($s * 0.79))
    $br   = New-Object System.Drawing.PointF(($s * 0.77), ($s * 0.79))
    $ml   = New-Object System.Drawing.PointF(($s * 0.355), ($s * 0.56))
    $mr   = New-Object System.Drawing.PointF(($s * 0.645), ($s * 0.56))

    $klein = $s -le 24
    $breite = if ($klein) { $s * 0.10 } else { $s * 0.05 }
    $mint = [System.Drawing.Color]::FromArgb(255, 92, 255, 176)
    $smaragd = [System.Drawing.Color]::FromArgb(255, 31, 191, 122)

    if (-not $klein) {
        $glow = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(55, 92, 255, 176), [single]($s * 0.13))
        $glow.StartCap = 'Round'; $glow.EndCap = 'Round'
        $g.DrawLine($glow, $apex, $bl); $g.DrawLine($glow, $apex, $br); $g.DrawLine($glow, $ml, $mr)
    }
    $linie = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, ($s * 0.2))), (New-Object System.Drawing.PointF(0, ($s * 0.8))), $mint, $smaragd)
    $stift = New-Object System.Drawing.Pen($linie, [single]$breite)
    $stift.StartCap = 'Round'; $stift.EndCap = 'Round'
    $g.DrawLine($stift, $apex, $bl); $g.DrawLine($stift, $apex, $br); $g.DrawLine($stift, $ml, $mr)

    foreach ($p in @($apex, $bl, $br, $ml, $mr)) {
        $r = if ($klein) { $s * 0.085 } else { $s * 0.06 }
        if (-not $klein) {
            $gr = $s * 0.12
            $hof = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(60, 92, 255, 176))
            $g.FillEllipse($hof, ($p.X - $gr), ($p.Y - $gr), (2 * $gr), (2 * $gr))
        }
        $farbe = if ($p -eq $apex) { [System.Drawing.Color]::FromArgb(255, 214, 255, 233) } else { $mint }
        $kern = New-Object System.Drawing.SolidBrush($farbe)
        $g.FillEllipse($kern, ($p.X - $r), ($p.Y - $r), (2 * $r), (2 * $r))
    }

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return ,$ms.ToArray()
}

$groessen = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$bilder = @()
foreach ($s in $groessen) { $bilder += ,(New-IconPng $s) }

$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($out)
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$groessen.Count)
$offset = 6 + 16 * $groessen.Count
for ($i = 0; $i -lt $groessen.Count; $i++) {
    $s = $groessen[$i]; $daten = $bilder[$i]
    $b = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$b); $w.Write([byte]$b); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$daten.Length); $w.Write([UInt32]$offset)
    $offset += $daten.Length
}
foreach ($daten in $bilder) { $w.Write($daten) }
$w.Flush()

$ordner = Split-Path $Ziel -Parent
if (-not (Test-Path $ordner)) { New-Item -ItemType Directory -Force $ordner | Out-Null }
[System.IO.File]::WriteAllBytes($Ziel, $out.ToArray())
"Icon geschrieben: $Ziel"
