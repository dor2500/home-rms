using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using System.Windows.Forms;
using HomeRms.Agent.Core;
using HomeRms.Agent.Features;
using HomeRms.Agent.Server;
using HomeRms.Agent.Ui;
using HomeRms.Shared;
using HomeRms.Shared.Security;
using Microsoft.Extensions.Hosting;
using Microsoft.Win32;

namespace HomeRms.Agent;

[SupportedOSPlatform("windows")]
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (ParseStringArg(args, "--pin") is { } pinFp && !string.IsNullOrWhiteSpace(pinFp))
        {
            TrustedControllerStore.AddPinned(pinFp, "Pinned Controller");
        }
        if (args.Any(a => a.Equals("--install", StringComparison.OrdinalIgnoreCase) || a.Equals("--install-service", StringComparison.OrdinalIgnoreCase)))
        {
            InstallAgent(args);
            return;
        }
        if (args.Any(a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase) || a.Equals("--uninstall-service", StringComparison.OrdinalIgnoreCase)))
        {
            UninstallAgent();
            return;
        }

        // Opt-in elevation: "HomeRmsAgent.exe --elevate" relaunches itself via the UAC prompt so
        // system-level controls (services, Windows Update, registry HKLM...) work. By default the
        // agent stays un-elevated (least privilege).
        if (args.Any(a => string.Equals(a, "--elevate", StringComparison.OrdinalIgnoreCase)) && !SystemOps.IsElevated())
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = Environment.ProcessPath!,
                    Arguments = string.Join(" ", args.Where(a => !string.Equals(a, "--elevate", StringComparison.OrdinalIgnoreCase))),
                    UseShellExecute = true,
                    Verb = "runas"
                });
            }
            catch { /* user cancelled the UAC prompt */ }
            return;
        }

        // UI-only preview: opens the main window without the gRPC host, discovery, firewall or
        // certificate changes. Update checks work; installing is refused. Used for screenshots/QA.
        if (args.Any(a => string.Equals(a, "--ui-preview", StringComparison.OrdinalIgnoreCase)))
        {
            RunUiPreview(args);
            return;
        }

        // First-run bootstrap: automatically deploy/sync the embedded deployment PKI
        // into %ProgramData%\HomeRMS\certs so the end-user PC trusts and presents the
        // exact same CA chain as the Controller with zero manual setup.
        DevCertificates.EnsureDeploymentCertificates(RmsPaths.CertDir, RmsPaths.CertPassword);

        var port = ParsePort(args, RmsPaths.DefaultPort);
        var autoConsent = args.Any(a => string.Equals(a, "--auto-consent", StringComparison.OrdinalIgnoreCase));
        var showWindow = args.Any(a => string.Equals(a, "--show", StringComparison.OrdinalIgnoreCase));
        var screenshotPath = ParseStringArg(args, "--screenshot");

        // Ensure Windows Firewall allows incoming LAN connections on the Agent port (best-effort)
        EnsureFirewallRules(port);

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        var log = new ActivityLog();
        var consent = new ConsentCoordinator(log);

        using var updater = new AgentUpdater(log, args);
        var host = AgentHost.Build(consent, log, port, updater: updater);
        try
        {
            host.StartAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Home RMS agent could not start listening:\n\n{ex.Message}",
                "Home RMS", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        using var discovery = new DiscoveryResponder(port, log);
        discovery.Start();

        updater.Start();

        log.Write("[agent] started (PKI certificates synced)");
        Application.Run(new TrayApplicationContext(consent, log, port, updater, autoConsent, screenshotPath, showWindow));

        log.Write("[agent] shutting down");
        host.StopAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
    }

    private static void RunUiPreview(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        var log = new ActivityLog();
        // --preview-version 2.1.0 pretends to be an older build so the "update available" state can be reviewed
        Version? fakeVersion = ParseStringArg(args, "--preview-version") is { } pv && Version.TryParse(pv, out var v) ? v : null;
        using var updater = new AgentUpdater(log, args, fakeVersion, applyDisabledReason: "Installing updates is disabled in UI preview mode.");
        log.Write("[agent] UI preview mode (no gRPC host, discovery or firewall changes)");

        using var form = new MainForm(log, RmsPaths.DefaultPort, updater);
        var shot = ParseStringArg(args, "--screenshot-updates");
        form.Shown += async (_, _) =>
        {
            try { await updater.CheckAsync(); }
            catch (Exception ex) { log.Write($"[updater] preview check failed: {ex.Message}"); }
            if (shot is null) return;
            await Task.Delay(800); // let the BeginInvoke'd status update paint
            form.SaveScreenshot(shot);
            form.ForceClose();
        };
        Application.Run(form);
    }

    private static void EnsureFirewallRules(int port)
    {
        try
        {
            var markerPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HomeRMS", $"fw-v2-{port}.done");
            if (File.Exists(markerPath)) return;

            // v2: accept the mTLS port from every private / VPN range (other subnets, VLANs, Tailscale's
            // 100.64.0.0/10), not just the local subnet, on any network profile (VPN adapters are often
            // "Public"). Every call is still mutually authenticated and limited to pinned Controllers.
            const string privateRanges = "LocalSubnet,10.0.0.0/8,172.16.0.0/12,192.168.0.0/16,100.64.0.0/10,fc00::/7,fe80::/10";
            var tcpRule = $"Home RMS Agent (mTLS TCP {port})";
            RunNetsh($"advfirewall firewall delete rule name=\"{tcpRule}\"");
            RunNetsh($"advfirewall firewall add rule name=\"{tcpRule}\" dir=in action=allow protocol=TCP localport={port} remoteip={privateRanges} profile=any");
            RunNetsh($"advfirewall firewall delete rule name=\"Home RMS Agent (Discovery UDP {DiscoveryResponder.DiscoveryUdpPort})\"");
            RunNetsh($"advfirewall firewall add rule name=\"Home RMS Agent (Discovery UDP {DiscoveryResponder.DiscoveryUdpPort})\" dir=in action=allow protocol=UDP localport={DiscoveryResponder.DiscoveryUdpPort} remoteip=LocalSubnet profile=private,domain");

            Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
            File.WriteAllText(markerPath, DateTimeOffset.UtcNow.ToString("O"));
        }
        catch
        {
            // Non-fatal if running without elevation
        }
    }

    private static void RunNetsh(string arguments) => RunCommand("netsh", arguments);

    private static void RunCommand(string fileName, string arguments)
    {
        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });
        proc?.WaitForExit(3000);
    }

    private static int ParsePort(string[] args, int fallback)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] is "--port" or "-p" && int.TryParse(args[i + 1], out var p))
                return p;
        return fallback;
    }

    private static string? ParseStringArg(string[] args, string flag)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }

    private static void InstallAgent(string[] args)
    {
        var silent = args.Any(a => a is "--silent" or "/S");
        var installDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "HomeRMS", "Agent");
        var targetExe = Path.Combine(installDir, "HomeRmsAgent.exe");

        foreach (var p in Process.GetProcessesByName("HomeRmsAgent"))
        {
            if (p.Id != Environment.ProcessId)
                try { p.Kill(); p.WaitForExit(3000); } catch { }
        }

        Directory.CreateDirectory(installDir);
        var srcExe = Environment.ProcessPath!;
        if (!string.Equals(srcExe, targetExe, StringComparison.OrdinalIgnoreCase))
            File.Copy(srcExe, targetExe, overwrite: true);

        // Register Windows Startup: when installed as Administrator, use an elevated Scheduled Task
        // (RL HIGHEST) so SendInput can control elevated windows (Task Manager, Admin PowerShell, Settings)
        // without being blocked by Windows UIPI.
        var noAutoStart = args.Any(a => string.Equals(a, "--no-autostart", StringComparison.OrdinalIgnoreCase));
        if (!noAutoStart)
        {
            if (SystemOps.IsElevated())
            {
                RunCommand("schtasks.exe", $"/Create /TN \"HomeRmsAgent\" /TR \"\\\"{targetExe}\\\"\" /SC ONLOGON /RL HIGHEST /F");
                RunCommand("schtasks.exe", $"/Create /TN \"HomeRmsAgentBoot\" /TR \"\\\"{targetExe}\\\"\" /SC ONSTART /RU SYSTEM /RL HIGHEST /F");
                using var runKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
                runKey?.DeleteValue("HomeRmsAgent", throwOnMissingValue: false);
            }
            else
            {
                using var runKey = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
                runKey.SetValue("HomeRmsAgent", $"\"{targetExe}\"");
            }
        }

        // Register in Apps & Features
        var ver = Assembly.GetExecutingAssembly().GetName().Version;
        var verStr = ver is not null ? $"{ver.Major}.{ver.Minor}.{ver.Build}" : "1.2.0";
        using (var uninst = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\HomeRmsAgent"))
        {
            uninst.SetValue("DisplayName", "Home RMS Endpoint Agent");
            uninst.SetValue("DisplayVersion", verStr);
            uninst.SetValue("Publisher", "Home RMS (dor2500)");
            uninst.SetValue("DisplayIcon", targetExe);
            uninst.SetValue("InstallLocation", installDir);
            uninst.SetValue("UninstallString", $"\"{targetExe}\" --uninstall");
            uninst.SetValue("NoModify", 1, RegistryValueKind.DWord);
            uninst.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }

        EnsureFirewallRules(RmsPaths.DefaultPort);
        Process.Start(new ProcessStartInfo { FileName = targetExe, UseShellExecute = true });

        if (!silent)
            MessageBox.Show($"Home RMS Agent installed to:\n{installDir}\n\nIt will start automatically with Windows and auto-update from GitHub.",
                "Home RMS Agent — Installed", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static void UninstallAgent()
    {
        foreach (var p in Process.GetProcessesByName("HomeRmsAgent"))
        {
            if (p.Id != Environment.ProcessId)
                try { p.Kill(); p.WaitForExit(3000); } catch { }
        }
        RunCommand("schtasks.exe", "/Delete /TN \"HomeRmsAgent\" /F");
        RunCommand("schtasks.exe", "/Delete /TN \"HomeRmsAgentBoot\" /F");
        using (var runKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true))
            runKey?.DeleteValue("HomeRmsAgent", throwOnMissingValue: false);
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\HomeRmsAgent", throwOnMissingSubKey: false);

        MessageBox.Show("Home RMS Agent has been unregistered from startup and Apps & Features.",
            "Home RMS Agent — Uninstalled", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}
