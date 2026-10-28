param([string]$Version = "1.4")

$ErrorActionPreference = "Stop"

$srcDir  = "C:\Users\haruk\BatteryWidget\BOOTH_Release\src"
$distDir = "C:\Users\haruk\BatteryWidget\BOOTH_Release\RingBatteryMonitor_v$Version"
$zipPath = "C:\Users\haruk\BatteryWidget\BOOTH_Release\RingBatteryMonitor_v$Version.zip"

if (-not (Test-Path $distDir)) {
    New-Item -ItemType Directory -Path $distDir -Force | Out-Null
}

# 1. Generate custom app.ico (32x32 Neon Green Ring Icon)
Add-Type -AssemblyName System.Drawing
$icoPath = Join-Path $srcDir "app.ico"
$bmp = New-Object System.Drawing.Bitmap(32, 32)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.Clear([System.Drawing.Color]::Transparent)

$bgBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(240, 24, 24, 27))
$g.FillEllipse($bgBrush, 1, 1, 29, 29)

$ringPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 16, 185, 129), 3.5)
$g.DrawEllipse($ringPen, 4, 4, 23, 23)

$dotBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 16, 185, 129))
$g.FillEllipse($dotBrush, 12, 12, 7, 7)
$g.Dispose()

# Write valid ICO header + PNG payload
$msPng = New-Object System.IO.MemoryStream
$bmp.Save($msPng, [System.Drawing.Imaging.ImageFormat]::Png)
$pngBytes = $msPng.ToArray()
$msPng.Dispose()
$bmp.Dispose()

$fsIco = New-Object System.IO.FileStream($icoPath, [System.IO.FileMode]::Create)
$bw = New-Object System.IO.BinaryWriter($fsIco)
# ICONDIR (6 bytes)
$bw.Write([uint16]0)
$bw.Write([uint16]1)
$bw.Write([uint16]1)
# ICONDIRENTRY (16 bytes)
$bw.Write([byte]32)
$bw.Write([byte]32)
$bw.Write([byte]0)
$bw.Write([byte]0)
$bw.Write([uint16]1)
$bw.Write([uint16]32)
$bw.Write([uint32]$pngBytes.Length)
$bw.Write([uint32]22)
$bw.Write($pngBytes)
$bw.Flush()
$bw.Close()
$fsIco.Close()

Write-Host "1. Generated custom icon: $icoPath"

# 2. Compile RingBatteryMonitor.exe using .NET 4.8 csc.exe
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$wpfDir = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF"
$exeOut = Join-Path $distDir "RingBatteryMonitor.exe"
$csFile = Join-Path $srcDir "Program.cs"

& $csc /nologo /target:winexe /optimize+ `
    "/out:$exeOut" `
    "/win32icon:$icoPath" `
    "/r:$wpfDir\PresentationFramework.dll" `
    "/r:$wpfDir\PresentationCore.dll" `
    "/r:$wpfDir\WindowsBase.dll" `
    "/r:C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.Xaml.dll" `
    "/r:C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.Windows.Forms.dll" `
    "/r:C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.Drawing.dll" `
    "/r:C:\Windows\Microsoft.NET\Framework64\v4.0.30319\Microsoft.CSharp.dll" `
    "/r:C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.IO.Compression.dll" `
    "/r:C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.IO.Compression.FileSystem.dll" `
    $csFile

if ($LASTEXITCODE -ne 0) {
    throw "csc.exe compilation failed with exit code $LASTEXITCODE"
}

Write-Host "2. Successfully compiled standalone EXE: $exeOut"
