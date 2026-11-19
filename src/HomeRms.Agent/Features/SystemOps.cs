using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using HomeRms.Shared.Protocol;

namespace HomeRms.Agent.Features;

/// <summary>
/// Allowlisted system-control operations (Windows Update, services, startup, tasks, power,
/// registry, firewall, general settings). Each operation maps to a FIXED PowerShell script;
/// user-supplied values are validated against strict patterns and passed through environment
/// variables (never concatenated into script text), so there is no command-injection surface.
/// </summary>
[SupportedOSPlatform("windows")]
public static class SystemOps
{
    private sealed record Op(string Script, bool Json, TimeSpan Timeout);

    private static readonly TimeSpan Short = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan Update = TimeSpan.FromMinutes(45);
    private static readonly TimeSpan Scan = TimeSpan.FromMinutes(30);

    private const string Prefix = """
        $ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue'
        [Console]::OutputEncoding=[Text.Encoding]::UTF8
        $t=$env:HRMS_TARGET; $x=$env:HRMS_EXTRA; $n=$env:HRMS_NAME; $ty=$env:HRMS_TYPE; $v=$env:HRMS_VALUE
        function KV($o){ $o.PSObject.Properties | ForEach-Object { [pscustomobject]@{Setting=$_.Name;Value="$($_.Value)"} } }
        $wu='HKLM:\SOFTWARE\Microsoft\WindowsUpdate\UX\Settings'
        $pol='HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU'
        """;

    private static readonly Dictionary<string, Op> Ops = new()
    {
        // ---------------- Windows Update ----------------
        ["updates/status"] = new("""
            $i=New-Object -ComObject Microsoft.Update.SystemInfo
            $svc=Get-Service wuauserv
            $p=(Get-ItemProperty $pol -ErrorAction SilentlyContinue).NoAutoUpdate
            $u=Get-ItemProperty $wu -ErrorAction SilentlyContinue
            KV ([pscustomobject]@{RebootRequired=$i.RebootRequired;UpdateService=$svc.Status;StartType=$svc.StartType;AutoUpdateDisabledByPolicy=($p -eq 1);PausedUntil=$u.PauseUpdatesExpiryTime}) | ConvertTo-Json -Compress
            """, true, Short),
        ["updates/list-pending"] = new("""
            $s=New-Object -ComObject Microsoft.Update.Session
            $r=$s.CreateUpdateSearcher().Search('IsInstalled=0 and IsHidden=0')
            @($r.Updates | ForEach-Object { [pscustomobject]@{KB=($_.KBArticleIDs -join ',');Title=$_.Title;SizeMB=[math]::Round($_.MaxDownloadSize/1MB,1);Reboot=$_.RebootRequired} }) | ConvertTo-Json -Compress
            """, true, Update),
        ["updates/install-all"] = new(InstallScript, false, Update),
        ["updates/install-kb"] = new(InstallScript, false, Update),
        ["updates/history"] = new("""
            $s=New-Object -ComObject Microsoft.Update.Session
            $q=$s.CreateUpdateSearcher(); $c=$q.GetTotalHistoryCount()
            @($q.QueryHistory(0,[math]::Min($c,60)) | ForEach-Object { [pscustomobject]@{Date=$_.Date.ToString('yyyy-MM-dd HH:mm');Result=@{1='In progress';2='Succeeded';3='Succeeded with errors';4='Failed';5='Aborted'}[[int]$_.ResultCode];Title=$_.Title} }) | ConvertTo-Json -Compress
            """, true, Short),
        ["updates/pause"] = new("""
            $d=[int]$x; $s=(Get-Date).ToUniversalTime(); $e=$s.AddDays($d)
            $fs=$s.ToString('yyyy-MM-ddTHH:mm:ssZ'); $fe=$e.ToString('yyyy-MM-ddTHH:mm:ssZ')
            New-Item $wu -Force | Out-Null
            foreach($k in 'PauseUpdatesStartTime','PauseFeatureUpdatesStartTime','PauseQualityUpdatesStartTime'){ Set-ItemProperty $wu $k $fs }
            foreach($k in 'PauseUpdatesExpiryTime','PauseFeatureUpdatesEndTime','PauseQualityUpdatesEndTime'){ Set-ItemProperty $wu $k $fe }
            "Updates paused for $d day(s), until $fe."
            """, false, Short),
        ["updates/resume"] = new("""
            foreach($k in 'PauseUpdatesStartTime','PauseUpdatesExpiryTime','PauseFeatureUpdatesStartTime','PauseFeatureUpdatesEndTime','PauseQualityUpdatesStartTime','PauseQualityUpdatesEndTime'){ Remove-ItemProperty $wu $k -ErrorAction SilentlyContinue }
            'Updates resumed.'
            """, false, Short),
        ["updates/disable-auto"] = new("""
            New-Item $pol -Force | Out-Null
            Set-ItemProperty $pol NoAutoUpdate 1 -Type DWord
            'Automatic Windows Update disabled by policy (NoAutoUpdate=1).'
            """, false, Short),
        ["updates/enable-auto"] = new("""
            Remove-ItemProperty $pol NoAutoUpdate -ErrorAction SilentlyContinue
            'Automatic Windows Update policy removed (default behaviour restored).'
            """, false, Short),
        ["updates/restart-service"] = new("""
            Restart-Service wuauserv -Force; Restart-Service bits -Force -ErrorAction SilentlyContinue
            'Windows Update + BITS services restarted.'
            """, false, Short),
        ["updates/reset"] = new("""
            Stop-Service wuauserv,bits -Force -ErrorAction SilentlyContinue
            $sd="$env:SystemRoot\SoftwareDistribution"
            if(Test-Path "$sd.old"){ Remove-Item "$sd.old" -Recurse -Force -ErrorAction SilentlyContinue }
            if(Test-Path $sd){ Rename-Item $sd 'SoftwareDistribution.old' -ErrorAction SilentlyContinue }
            Start-Service bits,wuauserv
            'Windows Update components reset (SoftwareDistribution renamed to .old).'
            """, false, Short),

        // ---------------- Services ----------------
        ["services/list"] = new("""
            @(Get-CimInstance Win32_Service | Sort-Object Name | Select-Object Name,DisplayName,State,StartMode,StartName) | ConvertTo-Json -Compress
            """, true, Short),
        ["services/start"] = new("Start-Service -Name $t; \"Service '$t' started.\"", false, Short),
        ["services/stop"] = new("Stop-Service -Name $t -Force; \"Service '$t' stopped.\"", false, Short),
        ["services/restart"] = new("Restart-Service -Name $t -Force; \"Service '$t' restarted.\"", false, Short),
        ["services/set-startup"] = new("""
            if($x -eq 'AutomaticDelayed'){ & sc.exe config $t start= delayed-auto | Out-Null; if($LASTEXITCODE -ne 0){ throw "sc.exe failed ($LASTEXITCODE)" } }
            else { Set-Service -Name $t -StartupType $x }
            "Startup type of '$t' set to $x."
            """, false, Short),

        // ---------------- Power ----------------
        ["power/restart"] = new("& shutdown.exe /r /t ([int]$x) /c 'Home RMS: restart requested'; \"Restart scheduled in $x second(s).\"", false, Short),
        ["power/shutdown"] = new("& shutdown.exe /s /t ([int]$x) /c 'Home RMS: shutdown requested'; \"Shutdown scheduled in $x second(s).\"", false, Short),
        ["power/cancel"] = new("& shutdown.exe /a; 'Pending shutdown/restart cancelled.'", false, Short),
        ["power/logoff"] = new("& shutdown.exe /l; 'User sign-out requested.'", false, Short),
        ["power/lock"] = new("& rundll32.exe user32.dll,LockWorkStation; 'Workstation locked.'", false, Short),
        ["power/sleep"] = new("& rundll32.exe powrprof.dll,SetSuspendState 0,1,0; 'Sleep requested.'", false, Short),
        ["power/plans"] = new("""
            @(powercfg /list | ForEach-Object { if($_ -match '([0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12})\s+\((.*?)\)(\s*\*)?'){ [pscustomobject]@{Guid=$matches[1];Name=$matches[2];Active="$([bool]$matches[3])"} } }) | ConvertTo-Json -Compress
            """, true, Short),
        ["power/set-plan"] = new("& powercfg /setactive $t; if($LASTEXITCODE -ne 0){ throw 'powercfg failed' }; \"Power plan $t activated.\"", false, Short),

        // ---------------- Startup apps ----------------
        ["startup/list"] = new("""
            @(Get-CimInstance Win32_StartupCommand | Select-Object Name,Command,Location,User) | ConvertTo-Json -Compress
            """, true, Short),
        ["startup/remove"] = new("""
            $found=$false
            foreach($p in 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run','HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run','HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'){
              if(Get-ItemProperty $p -Name $t -ErrorAction SilentlyContinue){ Remove-ItemProperty $p -Name $t; "Removed '$t' from $p"; $found=$true }
            }
            if(-not $found){ throw "Startup entry '$t' was not found in any Run key." }
            """, false, Short),

        // ---------------- Scheduled tasks ----------------
        ["tasks/list"] = new("""
            @(Get-ScheduledTask | Sort-Object TaskPath,TaskName | ForEach-Object { [pscustomobject]@{TaskPath=$_.TaskPath;TaskName=$_.TaskName;State="$($_.State)"} }) | ConvertTo-Json -Compress
            """, true, Short),
        ["tasks/run"] = new(TaskScript("Start-ScheduledTask", "started"), false, Short),
        ["tasks/enable"] = new(TaskScript("Enable-ScheduledTask", "enabled"), false, Short),
        ["tasks/disable"] = new(TaskScript("Disable-ScheduledTask", "disabled"), false, Short),

        // ---------------- Registry ----------------
        ["registry/list-keys"] = new("""
            @(Get-ChildItem -Path $t | ForEach-Object { [pscustomobject]@{Name=$_.PSChildName} }) | ConvertTo-Json -Compress
            """, true, Short),
        ["registry/get"] = new("""
            if($x){ @([pscustomobject]@{Name=$x;Value="$((Get-ItemProperty -Path $t -Name $x).$x)"}) | ConvertTo-Json -Compress }
            else { @((Get-ItemProperty -Path $t).PSObject.Properties | Where-Object { $_.Name -notlike 'PS*' } | ForEach-Object { [pscustomobject]@{Name=$_.Name;Value="$($_.Value)"} }) | ConvertTo-Json -Compress }
            """, true, Short),
        ["registry/set"] = new("""
            if(-not (Test-Path $t)){ New-Item -Path $t -Force | Out-Null }
            if($ty -eq 'DWord'){ $val=[int]$v } elseif($ty -eq 'QWord'){ $val=[long]$v } else { $val=$v }
            New-ItemProperty -Path $t -Name $n -Value $val -PropertyType $ty -Force | Out-Null
            "Wrote $t\$n ($ty)."
            """, false, Short),
        ["registry/delete"] = new("Remove-ItemProperty -Path $t -Name $x; \"Deleted $t\\$x.\"", false, Short),

        // ---------------- Firewall ----------------
        ["firewall/status"] = new("""
            @(Get-NetFirewallProfile | Select-Object Name,Enabled,DefaultInboundAction,DefaultOutboundAction | ForEach-Object { [pscustomobject]@{Name=$_.Name;Enabled="$($_.Enabled)";Inbound="$($_.DefaultInboundAction)";Outbound="$($_.DefaultOutboundAction)"} }) | ConvertTo-Json -Compress
            """, true, Short),
        ["firewall/enable-all"] = new("Set-NetFirewallProfile -Profile Domain,Private,Public -Enabled True; 'Windows Firewall enabled on all profiles.'", false, Short),
        ["firewall/disable-all"] = new("Set-NetFirewallProfile -Profile Domain,Private,Public -Enabled False; 'Windows Firewall DISABLED on all profiles.'", false, Short),

        // ---------------- General system settings ----------------
        ["system/info"] = new("""
            $o=Get-CimInstance Win32_OperatingSystem; $c=Get-CimInstance Win32_ComputerSystem
            KV ([pscustomobject]@{Computer=$env:COMPUTERNAME;OS=$o.Caption;Version=$o.Version;Build=$o.BuildNumber;Architecture=$o.OSArchitecture;Manufacturer=$c.Manufacturer;Model=$c.Model;Domain=$c.Domain;RAM_GB=[math]::Round($c.TotalPhysicalMemory/1GB,1);LastBoot=$o.LastBootUpTime.ToString('yyyy-MM-dd HH:mm');UptimeHours=[math]::Round(((Get-Date)-$o.LastBootUpTime).TotalHours,1);TimeZone=(Get-TimeZone).Id}) | ConvertTo-Json -Compress
            """, true, Short),
        ["system/hotfixes"] = new("""
            @(Get-HotFix | Sort-Object InstalledOn -Descending | ForEach-Object { [pscustomobject]@{HotFixID=$_.HotFixID;Description=$_.Description;InstalledOn=$(if($_.InstalledOn){([datetime]$_.InstalledOn).ToString('yyyy-MM-dd')})} }) | ConvertTo-Json -Compress
            """, true, Short),
        ["system/users"] = new("""
            @(Get-LocalUser | ForEach-Object { [pscustomobject]@{Name=$_.Name;Enabled="$($_.Enabled)";LastLogon=$(if($_.LastLogon){$_.LastLogon.ToString('yyyy-MM-dd HH:mm')});Description=$_.Description} }) | ConvertTo-Json -Compress
            """, true, Short),
        ["system/defender-status"] = new("""
            $d=Get-MpComputerStatus
            KV ([pscustomobject]@{AntivirusEnabled=$d.AntivirusEnabled;RealTimeProtection=$d.RealTimeProtectionEnabled;SignatureVersion=$d.AntivirusSignatureVersion;SignaturesUpdated=$d.AntivirusSignatureLastUpdated.ToString('yyyy-MM-dd HH:mm');LastQuickScan=$d.QuickScanEndTime.ToString('yyyy-MM-dd HH:mm')}) | ConvertTo-Json -Compress
            """, true, Short),
        ["system/defender-update"] = new("Update-MpSignature; 'Defender signatures updated.'", false, Short),
        ["system/defender-scan"] = new("Start-MpScan -ScanType QuickScan; 'Defender quick scan finished.'", false, Scan),
        ["system/rdp-on"] = new("""
            Set-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server' fDenyTSConnections 0 -Type DWord
            Enable-NetFirewallRule -DisplayGroup 'Remote Desktop'
            'Remote Desktop (RDP) enabled.'
            """, false, Short),
        ["system/rdp-off"] = new("""
            Set-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server' fDenyTSConnections 1 -Type DWord
            Disable-NetFirewallRule -DisplayGroup 'Remote Desktop'
            'Remote Desktop (RDP) disabled.'
            """, false, Short),
        ["system/rename"] = new("Rename-Computer -NewName $t -Force; \"Computer renamed to '$t'. A restart is required.\"", false, Short),
        ["system/timezone"] = new("Set-TimeZone -Id $t; \"Time zone set to '$t'.\"", false, Short),
        ["system/timezones"] = new("@(Get-TimeZone -ListAvailable | Select-Object Id,DisplayName) | ConvertTo-Json -Compress", true, Short),
        ["system/time-sync"] = new("Start-Service w32time -ErrorAction SilentlyContinue; & w32tm.exe /resync /force; 'Windows clock synchronized.'", false, Short),
        ["system/theme-dark"] = new("""
            $p='HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize'
            Set-ItemProperty $p AppsUseLightTheme 0 -Type DWord; Set-ItemProperty $p SystemUsesLightTheme 0 -Type DWord
            'Windows theme set to Dark mode.'
            """, false, Short),
        ["system/theme-light"] = new("""
            $p='HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize'
            Set-ItemProperty $p AppsUseLightTheme 1 -Type DWord; Set-ItemProperty $p SystemUsesLightTheme 1 -Type DWord
            'Windows theme set to Light mode.'
            """, false, Short),
        ["system/taskbar-center"] = new("Set-ItemProperty 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced' TaskbarAl 1 -Type DWord; 'Taskbar aligned Center.'", false, Short),
        ["system/taskbar-left"] = new("Set-ItemProperty 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced' TaskbarAl 0 -Type DWord; 'Taskbar aligned Left.'", false, Short),
        ["system/file-ext-on"] = new("Set-ItemProperty 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced' HideFileExt 0 -Type DWord; 'File extensions shown.'", false, Short),
        ["system/file-ext-off"] = new("Set-ItemProperty 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced' HideFileExt 1 -Type DWord; 'File extensions hidden.'", false, Short),
        ["system/gamemode-on"] = new("New-Item 'HKCU:\\Software\\Microsoft\\GameBar' -Force | Out-Null; Set-ItemProperty 'HKCU:\\Software\\Microsoft\\GameBar' AutoGameModeEnabled 1 -Type DWord; 'Game Mode enabled.'", false, Short),
        ["system/gamemode-off"] = new("New-Item 'HKCU:\\Software\\Microsoft\\GameBar' -Force | Out-Null; Set-ItemProperty 'HKCU:\\Software\\Microsoft\\GameBar' AutoGameModeEnabled 0 -Type DWord; 'Game Mode disabled.'", false, Short),
        ["system/network-list"] = new("@(Get-NetAdapter | Select-Object Name,InterfaceDescription,Status,LinkSpeed,MacAddress) | ConvertTo-Json -Compress", true, Short),
        ["system/dns-flush"] = new("& ipconfig.exe /flushdns; 'DNS resolver cache flushed.'", false, Short),
        ["system/devices-list"] = new("@(Get-PnpDevice -PresentOnly | Where-Object { $_.FriendlyName } | Select-Object -First 120 Class,FriendlyName,Status) | ConvertTo-Json -Compress", true, Short),
        ["agent/autostart-status"] = new("""
            $tk = Get-ScheduledTask -TaskName 'HomeRmsAgent' -ErrorAction SilentlyContinue
            $tkBoot = Get-ScheduledTask -TaskName 'HomeRmsAgentBoot' -ErrorAction SilentlyContinue
            $rk = (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'HomeRmsAgent' -ErrorAction SilentlyContinue).HomeRmsAgent
            KV ([pscustomobject]@{
                LogonTask = if ($tk) { "$($tk.State)" } else { 'Not registered' }
                BootTask = if ($tkBoot) { "$($tkBoot.State)" } else { 'Not registered' }
                RunRegistry = if ($rk) { $rk } else { 'Not set' }
                Executable = [System.Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
            }) | ConvertTo-Json -Compress
            """, true, Short),
        ["agent/autostart-enable"] = new("""
            $exe = [System.Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
            schtasks /Create /TN "HomeRmsAgent" /TR "`"$exe`"" /SC ONLOGON /RL HIGHEST /F | Out-Null
            schtasks /Create /TN "HomeRmsAgentBoot" /TR "`"$exe`"" /SC ONSTART /RU SYSTEM /RL HIGHEST /F | Out-Null
            'Agent Auto-Start enabled (Task Scheduler ONLOGON + ONSTART).'
            """, false, Short),
        ["agent/autostart-disable"] = new("""
            schtasks /Delete /TN "HomeRmsAgent" /F 2>$null | Out-Null
            schtasks /Delete /TN "HomeRmsAgentBoot" /F 2>$null | Out-Null
            Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'HomeRmsAgent' -ErrorAction SilentlyContinue
            'Agent Auto-Start disabled.'
            """, false, Short),
    };

    private const string InstallScript = """
        $s=New-Object -ComObject Microsoft.Update.Session
        $r=$s.CreateUpdateSearcher().Search('IsInstalled=0 and IsHidden=0')
        $c=New-Object -ComObject Microsoft.Update.UpdateColl
        $kb=$t -replace '^KB',''
        foreach($u in $r.Updates){
          if((-not $kb) -or (@($u.KBArticleIDs) -contains $kb)){ if(-not $u.EulaAccepted){ $u.AcceptEula() }; [void]$c.Add($u) }
        }
        if($c.Count -eq 0){ 'No matching updates found.' }
        else {
          $d=$s.CreateUpdateDownloader(); $d.Updates=$c; [void]$d.Download()
          $i=$s.CreateUpdateInstaller(); $i.Updates=$c; $res=$i.Install()
          "Installed $($c.Count) update(s). ResultCode=$($res.ResultCode) (2=OK) RebootRequired=$($res.RebootRequired)"
        }
        """;

    private static string TaskScript(string cmdlet, string verb) =>
        "$i=$t.LastIndexOf('\\'); $p=$t.Substring(0,$i+1); $nm=$t.Substring($i+1)\n" +
        $"{cmdlet} -TaskPath $p -TaskName $nm | Out-Null\n" +
        $"\"Task '$t' {verb}.\"";

    // ------------------------------------------------------------------ validation
    private static readonly Regex ServiceName = new(@"^[A-Za-z0-9_.\-$@ ]{1,256}$", RegexOptions.Compiled);
    private static readonly Regex StartupName = new(@"^[A-Za-z0-9_ .\-{}()&]{1,128}$", RegexOptions.Compiled);
    private static readonly Regex TaskPath = new(@"^\\[A-Za-z0-9_ .\-\\{}()]{1,300}$", RegexOptions.Compiled);
    private static readonly Regex KbNumber = new(@"^(KB)?\d{4,9}$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex RegPath = new(@"^(HKLM|HKCU):\\[A-Za-z0-9_\\ .\-{}()]{1,500}$", RegexOptions.Compiled);
    private static readonly Regex RegName = new(@"^[A-Za-z0-9_ .\-{}()]{1,200}$", RegexOptions.Compiled);
    private static readonly Regex HostName = new(@"^[A-Za-z0-9\-]{1,15}$", RegexOptions.Compiled);
    private static readonly Regex Guid36 = new(@"^[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}$", RegexOptions.Compiled);
    private static readonly Regex ZoneId = new(@"^[A-Za-z0-9 .\-+()/,_']{1,64}$", RegexOptions.Compiled);
    private static readonly string[] StartupTypes = ["Automatic", "AutomaticDelayed", "Manual", "Disabled"];
    private static readonly string[] RegTypes = ["String", "DWord", "QWord", "ExpandString"];

    public static bool IsElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Returns an error message if the request is invalid, otherwise null. Fills env vars.</summary>
    private static string? Validate(SystemOpRequest r, IDictionary<string, string?> env)
    {
        var t = (r.Target ?? "").Trim();
        var x = (r.Extra ?? "").Trim();
        env["HRMS_TARGET"] = t;
        env["HRMS_EXTRA"] = x;

        switch ($"{r.Area}/{r.Action}")
        {
            case "services/start" or "services/stop" or "services/restart":
                return ServiceName.IsMatch(t) ? null : "Invalid service name.";
            case "services/set-startup":
                if (!ServiceName.IsMatch(t)) return "Invalid service name.";
                return StartupTypes.Contains(x) ? null : "Startup type must be Automatic, AutomaticDelayed, Manual or Disabled.";
            case "updates/install-kb":
                return KbNumber.IsMatch(t) ? null : "Enter a KB number such as KB5030211.";
            case "updates/pause":
                return int.TryParse(x, out var days) && days is >= 1 and <= 35 ? null : "Pause duration must be 1-35 days.";
            case "power/restart" or "power/shutdown":
                if (x.Length == 0) x = "30";
                if (!int.TryParse(x, out var delay) || delay is < 0 or > 3600) return "Delay must be 0-3600 seconds.";
                env["HRMS_EXTRA"] = delay.ToString();
                return null;
            case "power/set-plan":
                return Guid36.IsMatch(t) ? null : "Enter a power-plan GUID (see 'Power plans').";
            case "startup/remove":
                return StartupName.IsMatch(t) ? null : "Invalid startup entry name.";
            case "tasks/run" or "tasks/enable" or "tasks/disable":
                return TaskPath.IsMatch(t) && !t.EndsWith('\\') && !t.Contains("..") ? null : @"Enter the full task path, e.g. \Microsoft\Windows\Foo\Bar.";
            case "registry/list-keys" or "registry/get" or "registry/set" or "registry/delete":
                if (!RegPath.IsMatch(t) || t.Contains("..")) return "Registry path must start with HKLM:\\ or HKCU:\\ and contain only safe characters.";
                if (Regex.IsMatch(t, @"^HKLM:\\(SAM|SECURITY)(\\|$)", RegexOptions.IgnoreCase)) return "Access to the SAM/SECURITY hives is blocked.";
                if (r.Action == "get" && x.Length > 0 && !RegName.IsMatch(x)) return "Invalid value name.";
                if (r.Action == "delete" && !RegName.IsMatch(x)) return "Enter the value name to delete in the 'extra' field.";
                if (r.Action == "set")
                {
                    var parts = x.Split('|', 3);
                    if (parts.Length != 3 || !RegName.IsMatch(parts[0].Trim()) || !RegTypes.Contains(parts[1].Trim()))
                        return "Use: name|type|value  (type: String, DWord, QWord, ExpandString).";
                    if (parts[2].Length > 4096) return "Value too long.";
                    if (parts[1].Trim() == "DWord" && !int.TryParse(parts[2], out _)) return "DWord value must be a 32-bit integer.";
                    if (parts[1].Trim() == "QWord" && !long.TryParse(parts[2], out _)) return "QWord value must be a 64-bit integer.";
                    env["HRMS_NAME"] = parts[0].Trim();
                    env["HRMS_TYPE"] = parts[1].Trim();
                    env["HRMS_VALUE"] = parts[2];
                }
                return null;
            case "system/rename":
                return HostName.IsMatch(t) ? null : "Computer name: 1-15 letters, digits or hyphens.";
            case "system/timezone":
                return ZoneId.IsMatch(t) ? null : "Invalid time-zone id (e.g. 'Israel Standard Time').";
            default:
                return null;
        }
    }

    public static async Task<SystemOpResult> RunAsync(SystemOpRequest request, CancellationToken ct)
    {
        var elevated = IsElevated();
        var key = $"{request.Area}/{request.Action}";
        if (!Ops.TryGetValue(key, out var op))
            return new SystemOpResult { Success = false, Message = $"Unknown or disallowed operation '{key}'.", IsElevated = elevated };

        var env = new Dictionary<string, string?>();
        var error = Validate(request, env);
        if (error is not null)
            return new SystemOpResult { Success = false, Message = error, IsElevated = elevated };

        var script = Prefix + "\n" + "try {\n" + op.Script + "\n} catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var (k, v) in env) psi.Environment[k] = v ?? "";

        using var proc = new Process { StartInfo = psi };
        proc.Start();
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(op.Timeout);
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            return new SystemOpResult
            {
                Success = false,
                Message = $"Operation '{key}' timed out or was cancelled after {op.Timeout.TotalMinutes:0.#} min.",
                IsElevated = elevated
            };
        }

        var stdout = (await outTask).Trim();
        var stderr = (await errTask).Trim();
        var ok = proc.ExitCode == 0;

        var result = new SystemOpResult { Success = ok, IsElevated = elevated };
        if (!ok)
        {
            result.Message = string.IsNullOrWhiteSpace(stderr) ? $"Operation failed (exit {proc.ExitCode})." : stderr;
            if (!elevated)
                result.Message += "\n(The agent is NOT running as Administrator — restart it with '--elevate' or 'Run as administrator' for system-level changes.)";
        }
        else if (op.Json)
        {
            result.JsonData = stdout;
            result.Message = "OK";
        }
        else
        {
            result.Message = string.IsNullOrWhiteSpace(stdout) ? "Done." : stdout;
        }
        return result;
    }
}
