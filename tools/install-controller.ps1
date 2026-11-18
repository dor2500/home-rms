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
$zipAsset = $release.assets | Where-Object { $_.name -eq "HomeRmsController-Setup-win-x64.zip" } | Select-Object -First 1
$exeAsset = $release.assets | Where-Object { $_.name -eq "HomeRmsController-Setup.exe" } | Select-Object -First 1
$asset = if ($zipAsset) { $zipAsset } else { $exeAsset }
if (-not $asset) { throw "HomeRmsController-Setup not found in latest release $($release.tag_name)." }

$uid = [Guid]::NewGuid().ToString('N').Substring(0,6)
$tmp = Join-Path $env:TEMP "$($release.tag_name)-$uid-$($asset.name)"
Write-Host "Downloading $($release.tag_name) ($($asset.name))..." -ForegroundColor Cyan
$ProgressPreference = 'SilentlyContinue'
if (Get-Command curl.exe -ErrorAction SilentlyContinue) {
    & curl.exe -fL --progress-bar $asset.browser_download_url -o $tmp
    if ($LASTEXITCODE -ne 0) { throw "Download failed with exit code $LASTEXITCODE." }
} else {
    Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $tmp
}

$setupExe = $tmp
if ($tmp.EndsWith(".zip", [StringComparison]::OrdinalIgnoreCase)) {
    $extractDir = Join-Path $env:TEMP "HomeRmsSetup-$uid"
    Expand-Archive -Path $tmp -DestinationPath $extractDir -Force
    $setupExe = Join-Path $extractDir "HomeRmsController-Setup.exe"
}

Write-Host "Launching Home RMS Controller Setup..." -ForegroundColor Green
Start-Process -FilePath $setupExe
