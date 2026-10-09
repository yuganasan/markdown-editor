# Builds MarkdownEditor.exe - a native WinForms app.
# Uses the C# compiler that ships with Windows. Nothing to install.

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

$icoPath = Join-Path $root "MarkdownEditor.ico"
$exePath = Join-Path $root "MarkdownEditor.exe"
$sources = @((Join-Path $root "MdRender.cs"), (Join-Path $root "MdApp.cs"))

foreach ($f in $sources) { if (-not (Test-Path $f)) { throw "Missing source file: $f" } }

# ---- icon ------------------------------------------------------------------
if (-not (Test-Path $icoPath)) {
    Write-Host "Drawing icon..."
    Add-Type -AssemblyName System.Drawing
    $pngs = @()
    foreach ($size in 16, 32, 48, 256) {
        $bmp = New-Object System.Drawing.Bitmap($size, $size)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.SmoothingMode = "AntiAlias"
        $g.TextRenderingHint = "AntiAliasGridFit"
        $g.Clear([System.Drawing.Color]::Transparent)

        # black box, with a faint grey edge so it stays visible on dark taskbars
        $r = [Math]::Max(1, [int]($size * 0.12))
        $pad = [Math]::Max(0, [int]($size * 0.03))
        $w = $size - 1 - ($pad * 2)
        $path = New-Object System.Drawing.Drawing2D.GraphicsPath
        $path.AddArc($pad, $pad, $r * 2, $r * 2, 180, 90)
        $path.AddArc($pad + $w - $r * 2, $pad, $r * 2, $r * 2, 270, 90)
        $path.AddArc($pad + $w - $r * 2, $pad + $w - $r * 2, $r * 2, $r * 2, 0, 90)
        $path.AddArc($pad, $pad + $w - $r * 2, $r * 2, $r * 2, 90, 90)
        $path.CloseFigure()
        $black = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(9, 9, 11))
        $g.FillPath($black, $path)
        if ($size -ge 32) {
            $edge = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(70, 72, 80)), ([float][Math]::Max(1, $size / 64))
            $g.DrawPath($edge, $path)
            $edge.Dispose()
        }

        # "md" in grey, centred, sized to the box
        $grey = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(154, 160, 170))
        $emSize = [float]($size * 0.46)
        $font = New-Object System.Drawing.Font("Segoe UI", $emSize, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
        $fmt = New-Object System.Drawing.StringFormat
        $fmt.Alignment = [System.Drawing.StringAlignment]::Center
        $fmt.LineAlignment = [System.Drawing.StringAlignment]::Center
        $box = New-Object System.Drawing.RectangleF(0, [float]($size * 0.02), [float]$size, [float]$size)
        $g.DrawString("md", $font, $grey, $box, $fmt)
        $font.Dispose(); $grey.Dispose(); $black.Dispose()
        $g.Dispose()
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        $pngs += , @{ size = $size; data = $ms.ToArray() }
        $ms.Dispose()
    }
    $ico = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ico)
    $bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$pngs.Count)
    $offset = 6 + (16 * $pngs.Count)
    foreach ($p in $pngs) {
        $dim = if ($p.size -ge 256) { 0 } else { $p.size }
        $bw.Write([Byte]$dim); $bw.Write([Byte]$dim); $bw.Write([Byte]0); $bw.Write([Byte]0)
        $bw.Write([UInt16]1); $bw.Write([UInt16]32)
        $bw.Write([UInt32]$p.data.Length); $bw.Write([UInt32]$offset)
        $offset += $p.data.Length
    }
    foreach ($p in $pngs) { $bw.Write($p.data) }
    $bw.Flush()
    [System.IO.File]::WriteAllBytes($icoPath, $ico.ToArray())
    $bw.Dispose()
}

# ---- manifest: per-monitor DPI awareness + modern common controls ----------
$manifest = Join-Path $root "app.manifest"
@'
<?xml version="1.0" encoding="utf-8"?>
<assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">
  <assemblyIdentity type="win32" name="MarkdownEditor" version="1.0.0.0" />
  <dependency>
    <dependentAssembly>
      <assemblyIdentity type="win32" name="Microsoft.Windows.Common-Controls"
        version="6.0.0.0" processorArchitecture="*" publicKeyToken="6595b64144ccf1df" language="*" />
    </dependentAssembly>
  </dependency>
  <application xmlns="urn:schemas-microsoft-com:asm.v3">
    <windowsSettings>
      <dpiAware xmlns="http://schemas.microsoft.com/SMI/2005/WindowsSettings">true/pm</dpiAware>
      <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">permonitorv2,permonitor</dpiAwareness>
      <longPathAware xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">true</longPathAware>
    </windowsSettings>
  </application>
  <compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1">
    <application>
      <supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}" />
      <supportedOS Id="{1f676c76-80e1-4239-95bb-83d0f6d0da78}" />
    </application>
  </compatibility>
</assembly>
'@ | Set-Content -Path $manifest -Encoding UTF8

# ---- compile ---------------------------------------------------------------
$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe" }
if (-not (Test-Path $csc)) { throw "No C# compiler found under $env:WINDIR\Microsoft.NET" }

Write-Host "Compiling..."
$cscArgs = @(
    "/nologo", "/target:winexe", "/optimize+", "/platform:anycpu",
    "/out:$exePath", "/win32icon:$icoPath", "/win32manifest:$manifest",
    "/reference:System.dll", "/reference:System.Drawing.dll",
    "/reference:System.Windows.Forms.dll", "/reference:System.Core.dll"
) + $sources
& $csc $cscArgs
if ($LASTEXITCODE -ne 0) { throw "Compile failed (exit $LASTEXITCODE)" }

$kb = [Math]::Round((Get-Item $exePath).Length / 1KB, 1)
Write-Host "Built $exePath ($kb KB)" -ForegroundColor Green
