<#
.SYNOPSIS
  One-line Windows Endpoint Agent installer: downloads HomeRmsAgent.exe from the latest
  GitHub Release (dor2500/home-rms) and installs it with Windows Startup + auto-updates.
.EXAMPLE
  irm https://raw.githubusercontent.com/dor2500/home-rms/master/tools/install-agent.ps1 | iex
#>
$ErrorActionPreference = 'Stop'
Write-Host "Fetching latest Home RMS Agent release from GitHub..." -ForegroundColor Cyan

$release = Invoke-RestMethod -Uri "https://api.github.com/repos/dor2500/home-rms/releases/latest" -Headers @{ "User-Agent" = "HomeRMS-Installer" }
$asset = $release.assets | Where-Object { $_.name -eq "HomeRmsAgent.exe" } | Select-Object -First 1
if (-not $asset) { throw "HomeRmsAgent.exe not found in latest release $($release.tag_name)." }

$tmp = Join-Path $env:TEMP "HomeRmsAgent-$($release.tag_name).exe"
Write-Host "Downloading $($release.tag_name) ($($asset.name))..." -ForegroundColor Cyan
Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $tmp

Write-Host "Installing Home RMS Agent (Startup + Firewall + Auto-Update)..." -ForegroundColor Green
Start-Process -FilePath $tmp -ArgumentList "--install --silent" -Wait
Write-Host "Home RMS Agent installed and running!" -ForegroundColor Green
