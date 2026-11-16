using HomeRms.Agent.Linux.Core;
using HomeRms.Agent.Linux.Server;
using HomeRms.Shared;
using HomeRms.Shared.Security;
using Microsoft.Extensions.Hosting;

DevCertificates.EnsureDeploymentCertificates(RmsPaths.CertDir, RmsPaths.CertPassword);

var port = ParsePort(args, RmsPaths.DefaultPort);
var autoConsent = args.Any(a => string.Equals(a, "--auto-consent", StringComparison.OrdinalIgnoreCase));

var log = new ActivityLog();
var consent = new ConsentCoordinator(log, autoConsent);

var host = AgentHost.Build(consent, log, port);
await host.StartAsync();

using var discovery = new DiscoveryResponder(port, log);
discovery.Start();

log.Write($"[agent-linux] started (PKI synced to {RmsPaths.CertDir}, autoConsent={autoConsent})");

var exit = new ManualResetEventSlim(false);
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    exit.Set();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => exit.Set();

exit.Wait();
log.Write("[agent-linux] shutting down");
await host.StopAsync(TimeSpan.FromSeconds(3));

static int ParsePort(string[] args, int fallback)
{
    for (var i = 0; i < args.Length - 1; i++)
        if (args[i] is "--port" or "-p" && int.TryParse(args[i + 1], out var p))
            return p;
    return fallback;
}
