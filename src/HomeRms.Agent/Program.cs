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
        // Dev convenience: generate the deployment PKI locally if it is missing.
        // In a real rollout you run tools\New-DevCerts.ps1 once and copy the certs
        // folder to each machine instead of generating per-agent.
        DevCertificates.EnsureDeploymentCertificates(RmsPaths.CertDir, RmsPaths.CertPassword);

        var port = ParsePort(args, RmsPaths.DefaultPort);

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

        log.Write("[agent] started");
        Application.Run(new TrayApplicationContext(consent, log));

        log.Write("[agent] shutting down");
        host.StopAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
    }

    private static int ParsePort(string[] args, int fallback)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] is "--port" or "-p" && int.TryParse(args[i + 1], out var p))
                return p;
        return fallback;
    }
}
