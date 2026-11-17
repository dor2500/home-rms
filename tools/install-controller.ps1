<#
.SYNOPSIS
  One-line Windows Controller installer: downloads HomeRmsController-Setup.exe from
  the latest GitHub Release (dor2500/home-rms) and runs the installer.
.EXAMPLE
  irm https://raw.githubusercontent.com/dor2500/home-rms/master/tools/install-controller.ps1 | iex
#>
$ErrorActionPreference = 'Stop'
Write-Host "Fetching latest Home RMS Controller release from GitHub..." -ForegroundColor Cyan

$release = Invoke-RestMethod -Uri "https://api.github.com/repos/dor2500/home-rms/releases/latest" -Headers @{ "User-Agent" = "HomeRMS-Installer" }
$asset = $release.assets | Where-Object { $_.name -eq "HomeRmsController-Setup.exe" } | Select-Object -First 1
if (-not $asset) { throw "HomeRmsController-Setup.exe not found in latest release $($release.tag_name)." }

$tmp = Join-Path $env:TEMP "HomeRmsController-Setup-$($release.tag_name).exe"
Write-Host "Downloading $($release.tag_name) ($($asset.name))..." -ForegroundColor Cyan
Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $tmp

Write-Host "Launching Home RMS Controller Setup..." -ForegroundColor Green
Start-Process -FilePath $tmp
