<#
.SYNOPSIS
  Publishes HomeRms.Controller.Mac for macOS (Apple Silicon arm64 and Intel x64),
  packages each into a native macOS .app bundle ("Home RMS Controller.app") with
  Info.plist and a multi-resolution .icns icon, and creates zip archives in dist/.
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root 'src/HomeRms.Controller.Mac/HomeRms.Controller.Mac.csproj'
$pngPath = Join-Path $root 'src/HomeRms.Controller/Assets/controller.png'
$distDir = Join-Path $root 'dist'
New-Item -ItemType Directory -Force -Path $distDir | Out-Null

function Write-IcnsFile([string]$sourcePng, [string]$outIcns) {
    # Build a valid Apple ICNS file containing ic07 (128x128) and ic08 (256x256) PNG blocks
    $srcBmp = [System.Drawing.Bitmap]::new($sourcePng)
    $entries = [System.Collections.Generic.List[byte[]]]::new()
    $types = @(
        @{ Tag = 'ic07'; Size = 128 },
        @{ Tag = 'ic08'; Size = 256 }
    )

    foreach ($t in $types) {
        $sz = $t.Size
        $bmp = [System.Drawing.Bitmap]::new($sz, $sz, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.DrawImage($srcBmp, 0, 0, $sz, $sz)
        $g.Dispose()

        $ms = [System.IO.MemoryStream]::new()
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $pngBytes = $ms.ToArray()
        $ms.Dispose()
        $bmp.Dispose()

        $blockLen = 8 + $pngBytes.Length
        $block = [System.IO.MemoryStream]::new()
        $tagBytes = [System.Text.Encoding]::ASCII.GetBytes($t.Tag)
        $lenBytes = [System.BitConverter]::GetBytes([uint32]$blockLen)
        [Array]::Reverse($lenBytes) # big-endian
        $block.Write($tagBytes, 0, 4)
        $block.Write($lenBytes, 0, 4)
        $block.Write($pngBytes, 0, $pngBytes.Length)
        $entries.Add($block.ToArray())
        $block.Dispose()
    }
    $srcBmp.Dispose()

    $totalLen = 8 + ($entries | Measure-Object -Property Length -Sum).Sum
    $fs = [System.IO.File]::Create($outIcns)
    $magic = [System.Text.Encoding]::ASCII.GetBytes('icns')
    $totalBytes = [System.BitConverter]::GetBytes([uint32]$totalLen)
    [Array]::Reverse($totalBytes)
    $fs.Write($magic, 0, 4)
    $fs.Write($totalBytes, 0, 4)
    foreach ($e in $entries) {
        $fs.Write($e, 0, $e.Length)
    }
    $fs.Flush()
    $fs.Dispose()
}

$infoPlist = @"
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>
    <string>Home RMS Controller</string>
    <key>CFBundleDisplayName</key>
    <string>Home RMS Controller</string>
    <key>CFBundleIdentifier</key>
    <string>com.homerms.controller</string>
    <key>CFBundleVersion</key>
    <string>1.0.0</string>
    <key>CFBundleShortVersionString</key>
    <string>1.0.0</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleExecutable</key>
    <string>HomeRmsController</string>
    <key>CFBundleIconFile</key>
    <string>controller.icns</string>
    <key>LSMinimumSystemVersion</key>
    <string>11.0</string>
    <key>NSHighResolutionCapable</key>
    <true/>
</dict>
</plist>
"@

foreach ($rid in @('osx-arm64', 'osx-x64')) {
    Write-Host "Publishing macOS Controller for $rid..." -ForegroundColor Cyan
    $stageRoot = Join-Path $env:TEMP ("homerms-mac-$rid-" + [guid]::NewGuid().ToString('N'))
    $appDir = Join-Path $stageRoot 'HomeRmsController.app'
    $contentsDir = Join-Path $appDir 'Contents'
    $macOsDir = Join-Path $contentsDir 'MacOS'
    $resDir = Join-Path $contentsDir 'Resources'

    New-Item -ItemType Directory -Force -Path $macOsDir | Out-Null
    New-Item -ItemType Directory -Force -Path $resDir | Out-Null

    dotnet publish $proj -c Release -r $rid --self-contained true -p:PublishSingleFile=false -o $macOsDir

    Set-Content -Path (Join-Path $contentsDir 'Info.plist') -Value $infoPlist -Encoding UTF8
    Write-IcnsFile $pngPath (Join-Path $resDir 'controller.icns')

    # Include a small README/helper for macOS Gatekeeper + chmod +x on first launch
    $setupSh = @"
#!/bin/bash
DIR="`$(cd "`$(dirname "`$0")" && pwd)"
chmod +x "`$DIR/HomeRmsController.app/Contents/MacOS/HomeRmsController"
xattr -cr "`$DIR/HomeRmsController.app" 2>/dev/null || true
open "`$DIR/HomeRmsController.app"
"@
    Set-Content -Path (Join-Path $stageRoot 'Launch-HomeRMS-Mac.command') -Value ($setupSh -replace "`r`n", "`n") -NoNewline -Encoding UTF8

    $zipPath = Join-Path $distDir "HomeRmsController-macOS-$rid.zip"
    if (Test-Path $zipPath) { Remove-Item -Force $zipPath }
    Compress-Archive -Path "$stageRoot\*" -DestinationPath $zipPath -Force
    Remove-Item -Recurse -Force $stageRoot -ErrorAction SilentlyContinue
    Write-Host "Created: $zipPath" -ForegroundColor Green
}
