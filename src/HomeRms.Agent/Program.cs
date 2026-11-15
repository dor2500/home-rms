using System.Diagnostics;
using System.Runtime.Versioning;
using System.Windows.Forms;
using HomeRms.Agent.Core;
using HomeRms.Agent.Server;
using HomeRms.Agent.Ui;
using HomeRms.Shared;
using HomeRms.Shared.Security;
using Microsoft.Extensions.Hosting;

namespace HomeRms.Agent;

[SupportedOSPlatform("windows")]
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
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

        var host = AgentHost.Build(consent, log, port);
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

        log.Write("[agent] started (PKI certificates synced)");
        Application.Run(new TrayApplicationContext(consent, log, port, autoConsent, screenshotPath, showWindow));

        log.Write("[agent] shutting down");
        host.StopAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
    }

    private static void EnsureFirewallRules(int port)
    {
        try
        {
            var markerPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HomeRMS", $"fw-{port}.done");
            if (File.Exists(markerPath)) return;

            RunNetsh($"advfirewall firewall add rule name=\"Home RMS Agent (mTLS TCP {port})\" dir=in action=allow protocol=TCP localport={port} profile=private,domain");
            RunNetsh($"advfirewall firewall add rule name=\"Home RMS Agent (Discovery UDP {DiscoveryResponder.DiscoveryUdpPort})\" dir=in action=allow protocol=UDP localport={DiscoveryResponder.DiscoveryUdpPort} profile=private,domain");

            Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
            File.WriteAllText(markerPath, DateTimeOffset.UtcNow.ToString("O"));
        }
        catch
        {
            // Non-fatal if running without elevation
        }
    }

    private static void RunNetsh(string arguments)
    {
        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName = "netsh",
            Arguments = arguments,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });
        proc?.WaitForExit(2000);
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
}
