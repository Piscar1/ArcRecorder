# Генератор иконок ArcRecorder (вариант A: тёмная плитка, intel-кольцо, точка записи).
# Запуск из папки проекта:  pwsh -NoProfile -STA -File design\make-icons.ps1
#   Assets\ArcRecorder.ico     — exe и трей во время записи (красная точка)
#   Assets\ArcRecorderIdle.ico — трей в простое (светлая точка)
param([string]$OutDir = (Join-Path $PSScriptRoot '..\Assets'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase

function C([string]$hex) { [System.Windows.Media.ColorConverter]::ConvertFromString($hex) }
function Brush([string]$hex) { $b = [System.Windows.Media.SolidColorBrush]::new((C $hex)); $b.Freeze(); $b }
function Grad([string]$a, [string]$b, [double]$angle) {
    $g = [System.Windows.Media.LinearGradientBrush]::new((C $a), (C $b), $angle); $g.Freeze(); $g
}
function MkPen($brush, [double]$th) {
    $p = [System.Windows.Media.Pen]::new($brush, $th)
    $p.StartLineCap = [System.Windows.Media.PenLineCap]::Round
    $p.EndLineCap = [System.Windows.Media.PenLineCap]::Round
    $p.Freeze(); $p
}
function Pt([double]$x, [double]$y) { [System.Windows.Point]::new($x, $y) }
function Rc([double]$x, [double]$y, [double]$w, [double]$h) { [System.Windows.Rect]::new($x, $y, $w, $h) }

# дуга: 0° = вправо, 90° = вниз (оси WPF), по часовой
function ArcGeom([double]$cx, [double]$cy, [double]$r, [double]$startDeg, [double]$sweepDeg) {
    $s = $startDeg * [Math]::PI / 180; $e = ($startDeg + $sweepDeg) * [Math]::PI / 180
    $fig = [System.Windows.Media.PathFigure]::new()
    $fig.StartPoint = Pt ($cx + $r * [Math]::Cos($s)) ($cy + $r * [Math]::Sin($s))
    $seg = [System.Windows.Media.ArcSegment]::new((Pt ($cx + $r * [Math]::Cos($e)) ($cy + $r * [Math]::Sin($e))),
        [System.Windows.Size]::new($r, $r), 0, ($sweepDeg -gt 180), [System.Windows.Media.SweepDirection]::Clockwise, $true)
    $fig.Segments.Add($seg)
    $g = [System.Windows.Media.PathGeometry]::new(); $g.Figures.Add($fig); $g.Freeze(); $g
}

# рисунок в сетке 256x256
function DrawIcon($dc, [string]$dotHex) {
    $dc.DrawRoundedRectangle((Grad '#FF2C2F37' '#FF121418' 90), $null, (Rc 12 12 232 232), 54, 54)
    $dc.DrawGeometry($null, (MkPen (Grad '#FF00B4EE' '#FF0068B5' 45) 30), (ArcGeom 128 128 70 -30 300))
    $dc.DrawEllipse((Brush $dotHex), $null, (Pt 128 128), 30, 30)
}

# BGRA без премультипликации — так ждёт формат ICO
function RenderBgra([string]$dotHex, [int]$px) {
    $dv = [System.Windows.Media.DrawingVisual]::new()
    $dc = $dv.RenderOpen()
    $dc.PushTransform([System.Windows.Media.ScaleTransform]::new($px / 256.0, $px / 256.0))
    DrawIcon $dc $dotHex
    $dc.Pop(); $dc.Close()
    $rtb = [System.Windows.Media.Imaging.RenderTargetBitmap]::new($px, $px, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $rtb.Render($dv)
    $conv = [System.Windows.Media.Imaging.FormatConvertedBitmap]::new($rtb, [System.Windows.Media.PixelFormats]::Bgra32, $null, 0)
    $conv.Freeze(); $conv
}

# 256 px — PNG внутри ICO; мелкие — классический 32-битный DIB (читается любым API, включая трей)
function IcoEntryBytes($bmp, [int]$px) {
    $ms = [IO.MemoryStream]::new()
    if ($px -ge 256) {
        $enc = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
        $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bmp))
        $enc.Save($ms)
        return , $ms.ToArray()
    }
    $stride = $px * 4
    $pixels = New-Object byte[] ($stride * $px)
    $bmp.CopyPixels($pixels, $stride, 0)
    $maskRow = [int]([Math]::Floor(($px + 31) / 32) * 4)
    $w = [IO.BinaryWriter]::new($ms)
    # BITMAPINFOHEADER: высота удвоена (цвет + AND-маска)
    $w.Write([int]40); $w.Write([int]$px); $w.Write([int]($px * 2))
    $w.Write([int16]1); $w.Write([int16]32)
    $w.Write([int]0); $w.Write([int]($stride * $px + $maskRow * $px))
    $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0)
    for ($y = $px - 1; $y -ge 0; $y--) { $w.Write($pixels, $y * $stride, $stride) } # строки снизу вверх
    $w.Write((New-Object byte[] ($maskRow * $px))) # маска пустая: прозрачность из альфа-канала
    $w.Flush()
    return , $ms.ToArray()
}

function WriteIco([string]$path, [string]$dotHex, [int[]]$sizes) {
    $entries = New-Object System.Collections.Generic.List[object]
    foreach ($px in $sizes) { $entries.Add(@($px, (IcoEntryBytes (RenderBgra $dotHex $px) $px))) }
    $fs = [IO.File]::Create($path)
    $w = [IO.BinaryWriter]::new($fs)
    $w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$entries.Count)
    $offset = 6 + 16 * $entries.Count
    foreach ($e in $entries) {
        $px = $e[0]; $data = [byte[]]$e[1]
        $b = if ($px -ge 256) { 0 } else { $px } # 0 = 256
        $w.Write([byte]$b); $w.Write([byte]$b); $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([int16]1); $w.Write([int16]32); $w.Write([int]$data.Length); $w.Write([int]$offset)
        $offset += $data.Length
    }
    foreach ($e in $entries) { $w.Write([byte[]]$e[1]) }
    $w.Flush(); $fs.Close()
    "$path ($($sizes -join ', ') px)"
}

New-Item -ItemType Directory -Force $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path
WriteIco (Join-Path $OutDir 'ArcRecorder.ico') '#FFE53935' @(16, 20, 24, 32, 40, 48, 64, 256)
WriteIco (Join-Path $OutDir 'ArcRecorderIdle.ico') '#FFD6D8DE' @(16, 20, 24, 32, 40, 48)
