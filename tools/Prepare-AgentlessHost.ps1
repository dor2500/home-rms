<#
.SYNOPSIS
    Home RMS — Interactive Agentless Target Preparation Script
.DESCRIPTION
    Prepares a Windows machine for Agentless management via Home RMS Server:
      1. Pre-flight check & auto-fix (WinRM, Firewall, LocalAccountTokenFilterPolicy, RDP)
      2. Interactive dedicated management user (Regular or Hidden from login screen; Admin or Standard)
      3. Optional Concurrent RDP Sessions setup (with automatic backup before modification)
#>
#Requires -RunAsAdministrator
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Header($text) {
    Write-Host ""
    Write-Host "============================================================" -ForegroundColor DarkCyan
    Write-Host "  $text" -ForegroundColor Cyan
    Write-Host "============================================================" -ForegroundColor DarkCyan
}

function Write-Check($name, $ok, $detail) {
    $tag = if ($ok) { "[PASS]" } else { "[FAIL]" }
    $col = if ($ok) { "Green" } else { "Yellow" }
    Write-Host ("  {0,-8} {1,-34} {2}" -f $tag, $name, $detail) -ForegroundColor $col
}

Write-Header "Home RMS — Agentless Host Preparation"

# ==============================================================================
# 1. Pre-flight Check
# ==============================================================================
Write-Host "`n[1/3] Running Pre-flight Checks..." -ForegroundColor White

$winrmSvc = Get-Service -Name WinRM -ErrorAction SilentlyContinue
$winrmOk = ($null -ne $winrmSvc -and $winrmSvc.Status -eq 'Running')
Write-Check "WinRM Service" $winrmOk ($(if ($winrmOk) { "Running" } else { "Stopped or missing" }))

$listenerOk = $false
try {
    $listeners = Get-ChildItem WSMan:\localhost\Listener -ErrorAction SilentlyContinue
    $listenerOk = ($null -ne $listeners -and $listeners.Count -gt 0)
} catch { $listenerOk = $false }
Write-Check "WinRM Listener" $listenerOk ($(if ($listenerOk) { "Configured" } else { "No active listener" }))

$uacPath = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
$tokenVal = (Get-ItemProperty -Path $uacPath -Name 'LocalAccountTokenFilterPolicy' -ErrorAction SilentlyContinue).LocalAccountTokenFilterPolicy
$tokenOk = ($tokenVal -eq 1)
Write-Check "UAC Remote Filter Policy" $tokenOk ($(if ($tokenOk) { "LocalAccountTokenFilterPolicy = 1" } else { "Not set (blocks local admin WinRM)" }))

$tsPath = 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server'
$rdpDeny = (Get-ItemProperty -Path $tsPath -Name 'fDenyTSConnections' -ErrorAction SilentlyContinue).fDenyTSConnections
$rdpOk = ($rdpDeny -eq 0)
Write-Check "Remote Desktop (RDP)" $rdpOk ($(if ($rdpOk) { "Enabled" } else { "Disabled (fDenyTSConnections=$rdpDeny)" }))

$fwWinRm = Get-NetFirewallRule -Name 'WINRM-HTTP-In-TCP' -ErrorAction SilentlyContinue
$fwOk = ($null -ne $fwWinRm -and $fwWinRm.Enabled -eq 'True')
Write-Check "Firewall (WinRM / RDP)" $fwOk ($(if ($fwOk) { "WINRM-HTTP-In-TCP enabled" } else { "Needs firewall rule" }))

if (-not ($winrmOk -and $listenerOk -and $tokenOk -and $rdpOk -and $fwOk)) {
    $fix = Read-Host "`nSome checks failed. Auto-fix all detected issues now? (Y/n)"
    if ($fix -eq '' -or $fix -match '^[Yy]') {
        Write-Host "Applying fixes..." -ForegroundColor Cyan
        Enable-PSRemoting -Force -SkipNetworkProfileCheck | Out-Null
        Set-Item WSMan:\localhost\Service\AllowUnencrypted -Value $false -Force -ErrorAction SilentlyContinue
        Set-Item WSMan:\localhost\Client\TrustedHosts -Value '*' -Force -ErrorAction SilentlyContinue

        New-ItemProperty -Path $uacPath -Name 'LocalAccountTokenFilterPolicy' -Value 1 -PropertyType DWord -Force | Out-Null
        Set-ItemProperty -Path $tsPath -Name 'fDenyTSConnections' -Value 0 -Force

        Enable-NetFirewallRule -DisplayGroup "Windows Remote Management" -ErrorAction SilentlyContinue
        Enable-NetFirewallRule -DisplayGroup "Remote Desktop" -ErrorAction SilentlyContinue
        Enable-NetFirewallRule -DisplayGroup "File and Printer Sharing" -ErrorAction SilentlyContinue
        Write-Host "Pre-flight fixes applied successfully." -ForegroundColor Green
    }
}

# ==============================================================================
# 2. Dedicated Management User
# ==============================================================================
Write-Host "`n[2/3] Dedicated Management User Setup" -ForegroundColor White
$createUsr = Read-Host "Create or configure a dedicated Home RMS management user? (Y/n)"
if ($createUsr -eq '' -or $createUsr -match '^[Yy]') {
    $username = Read-Host "  Enter username (default: HomeRmsAdmin)"
    if ([string]::IsNullOrWhiteSpace($username)) { $username = "HomeRmsAdmin" }

    $secPass = Read-Host "  Enter password for '$username'" -AsSecureString

    Write-Host "  User Visibility:"
    Write-Host "    [1] Hidden User (hidden from Windows sign-in screen)"
    Write-Host "    [2] Regular User (visible on sign-in screen)"
    $visChoice = Read-Host "  Select (1/2, default 1)"
    $isHidden = ($visChoice -ne '2')

    Write-Host "  User Permissions:"
    Write-Host "    [1] Administrator (full WinRM / Deploy / Updates control)"
    Write-Host "    [2] Standard User (Remote Desktop Users only)"
    $roleChoice = Read-Host "  Select (1/2, default 1)"
    $isAdmin = ($roleChoice -ne '2')

    $existing = Get-LocalUser -Name $username -ErrorAction SilentlyContinue
    if ($null -eq $existing) {
        New-LocalUser -Name $username -Password $secPass -FullName "Home RMS Management" -PasswordNeverExpires -UserMayNotChangePassword | Out-Null
        Write-Host "  Created local user '$username'." -ForegroundColor Green
    } else {
        Set-LocalUser -Name $username -Password $secPass -PasswordNeverExpires $true
        Enable-LocalUser -Name $username
        Write-Host "  Updated existing local user '$username'." -ForegroundColor Green
    }

    if ($isAdmin) {
        Add-LocalGroupMember -Group "Administrators" -Member $username -ErrorAction SilentlyContinue
        Write-Host "  Added '$username' to Administrators." -ForegroundColor Green
    } else {
        Remove-LocalGroupMember -Group "Administrators" -Member $username -ErrorAction SilentlyContinue
    }
    Add-LocalGroupMember -Group "Remote Desktop Users" -Member $username -ErrorAction SilentlyContinue

    $specialList = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon\SpecialAccounts\UserList'
    if ($isHidden) {
        if (-not (Test-Path $specialList)) { New-Item -Path $specialList -Force | Out-Null }
        New-ItemProperty -Path $specialList -Name $username -Value 0 -PropertyType DWord -Force | Out-Null
        Write-Host "  Configured '$username' as Hidden User on the Windows login screen." -ForegroundColor Green
    } else {
        if (Test-Path $specialList) {
            Remove-ItemProperty -Path $specialList -Name $username -ErrorAction SilentlyContinue
        }
        Write-Host "  Configured '$username' as Regular visible user." -ForegroundColor Green
    }
}

# ==============================================================================
# 3. Optional Concurrent RDP Sessions
# ==============================================================================
Write-Host "`n[3/3] Concurrent RDP Sessions (Parallel Local + Remote Session)" -ForegroundColor White
$concChoice = Read-Host "Enable Concurrent RDP Sessions so remote admin does not kick out the local user? (y/N)"
if ($concChoice -match '^[Yy]') {
    $backupDir = Join-Path $env:ProgramData "HomeRms\Backups\$(Get-Date -Format 'yyyyMMdd_HHmmss')"
    New-Item -ItemType Directory -Path $backupDir -Force | Out-Null

    # Automatic backup of registry & termsrv.dll before modification
    $termsrv = "$env:SystemRoot\System32\termsrv.dll"
    if (Test-Path $termsrv) {
        Copy-Item $termsrv (Join-Path $backupDir "termsrv.dll.bak") -Force
    }
    reg export "HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server" (Join-Path $backupDir "TerminalServer.reg") /y | Out-Null
    Write-Host "  Backup created at: $backupDir" -ForegroundColor DarkGray

    # Registry policy for multi-session per user
    New-ItemProperty -Path $tsPath -Name 'fSingleSessionPerUser' -Value 0 -PropertyType DWord -Force | Out-Null
    $tsPol = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services'
    if (-not (Test-Path $tsPol)) { New-Item -Path $tsPol -Force | Out-Null }
    New-ItemProperty -Path $tsPol -Name 'fSingleSessionPerUser' -Value 0 -PropertyType DWord -Force | Out-Null
    New-ItemProperty -Path $tsPol -Name 'MaxInstanceCount' -Value 5 -PropertyType DWord -Force | Out-Null

    # Check if RDP Wrapper is already installed; if not, offer to install/configure
    $rdpWrapDir = "$env:ProgramFiles\RDP Wrapper"
    if (Test-Path (Join-Path $rdpWrapDir "RDPWInst.exe")) {
        & (Join-Path $rdpWrapDir "RDPWInst.exe") -r | Out-Null
        Write-Host "  RDP Wrapper refreshed and multi-session policy enabled." -ForegroundColor Green
    } else {
        Write-Host "  Multi-session Terminal Server policies applied (fSingleSessionPerUser=0)." -ForegroundColor Green
        Write-Host "  Note: Windows Client SKUs (Home/Pro) also require RDP Wrapper (stascorp/rdpwrap) for concurrent sessions." -ForegroundColor Yellow
    }
}

Write-Header "Preparation Complete"
Write-Host "  You can now add this machine in Home RMS Console as 'Agentless (WinRM / RDP)'." -ForegroundColor Green
Write-Host "  Host IP(s): $((Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' } | Select-Object -ExpandProperty IPAddress) -join ', ')"
Write-Host ""
