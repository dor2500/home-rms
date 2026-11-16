<#
.SYNOPSIS
  Publishes HomeRms.Agent.Linux and HomeRms.Controller.Mac for Linux (linux-x64 and linux-arm64)
  into self-contained archives in dist/ with launcher scripts and .desktop entry.
#>
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$agentProj = Join-Path $root 'src/HomeRms.Agent.Linux/HomeRms.Agent.Linux.csproj'
$ctrlProj = Join-Path $root 'src/HomeRms.Controller.Mac/HomeRms.Controller.Mac.csproj'
$iconPng = Join-Path $root 'src/HomeRms.Controller/Assets/controller.png'
$distDir = Join-Path $root 'dist'
New-Item -ItemType Directory -Force -Path $distDir | Out-Null

foreach ($rid in @('linux-x64', 'linux-arm64')) {
    # 1. Linux Agent
    Write-Host "Publishing Linux Agent for $rid..." -ForegroundColor Cyan
    $agentStage = Join-Path $env:TEMP ("homerms-linux-agent-$rid-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $agentStage | Out-Null

    dotnet publish $agentProj -c Release -r $rid --self-contained true -p:PublishSingleFile=true -o $agentStage

    $agentRunSh = @"
#!/usr/bin/env bash
DIR="`$(cd "`$(dirname "`$0")" && pwd)"
chmod +x "`$DIR/HomeRmsAgent"
exec "`$DIR/HomeRmsAgent" "`$@"
"@
    Set-Content -Path (Join-Path $agentStage 'run-agent.sh') -Value ($agentRunSh -replace "`r`n", "`n") -NoNewline -Encoding UTF8

    $agentZip = Join-Path $distDir "HomeRmsAgent-$rid.zip"
    if (Test-Path $agentZip) { Remove-Item -Force $agentZip }
    Compress-Archive -Path "$agentStage\*" -DestinationPath $agentZip -Force
    Remove-Item -Recurse -Force $agentStage -ErrorAction SilentlyContinue
    Write-Host "Created: $agentZip" -ForegroundColor Green

    # 2. Linux Controller
    Write-Host "Publishing Linux Controller for $rid..." -ForegroundColor Cyan
    $ctrlStage = Join-Path $env:TEMP ("homerms-linux-ctrl-$rid-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $ctrlStage | Out-Null

    dotnet publish $ctrlProj -c Release -r $rid --self-contained true -p:PublishSingleFile=false -o $ctrlStage
    Copy-Item $iconPng (Join-Path $ctrlStage 'controller.png')

    $ctrlRunSh = @"
#!/usr/bin/env bash
DIR="`$(cd "`$(dirname "`$0")" && pwd)"
chmod +x "`$DIR/HomeRmsController"
exec "`$DIR/HomeRmsController" "`$@"
"@
    Set-Content -Path (Join-Path $ctrlStage 'launch-controller.sh') -Value ($ctrlRunSh -replace "`r`n", "`n") -NoNewline -Encoding UTF8

    $desktop = @'
[Desktop Entry]
Name=Home RMS Controller
Comment=Consent-Based LAN Remote Management Console
Exec=bash -c 'DIR="$(dirname "%k")"; chmod +x "$DIR/HomeRmsController"; exec "$DIR/HomeRmsController"'
Icon=controller
Terminal=false
Type=Application
Categories=System;Network;RemoteAccess;
'@
    Set-Content -Path (Join-Path $ctrlStage 'HomeRmsController.desktop') -Value ($desktop -replace "`r`n", "`n") -NoNewline -Encoding UTF8

    $ctrlZip = Join-Path $distDir "HomeRmsController-$rid.zip"
    if (Test-Path $ctrlZip) { Remove-Item -Force $ctrlZip }
    Compress-Archive -Path "$ctrlStage\*" -DestinationPath $ctrlZip -Force
    Remove-Item -Recurse -Force $ctrlStage -ErrorAction SilentlyContinue
    Write-Host "Created: $ctrlZip" -ForegroundColor Green
}
