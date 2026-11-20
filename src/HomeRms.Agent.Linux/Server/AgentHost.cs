using System.Security.Authentication;
using HomeRms.Agent.Linux.Core;
using HomeRms.Shared;
using HomeRms.Shared.Security;
using HomeRms.Shared.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HomeRms.Agent.Linux.Server;

/// <summary>Builds the Kestrel + gRPC host on Linux with strict mutual TLS and HTTP/2 keepalive.</summary>
public static class AgentHost
{
    public static WebApplication Build(ConsentCoordinator consent, ActivityLog log, int port, AgentSecurityOptions? security = null)
    {
        var agentCert = DevCertificates.Load(RmsPaths.CertDir, DevCertificates.AgentFileName, RmsPaths.CertPassword);
        var caCert = DevCertificates.LoadCaPublicOnly(RmsPaths.CertDir, RmsPaths.CertPassword);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        security ??= new AgentSecurityOptions();
        var authorizer = new PinnedControllerAuthorizer(security, log.Write);

        builder.Services.AddGrpc(o =>
        {
            o.MaxReceiveMessageSize = 64 * 1024 * 1024;
            // Every RPC except GetAgentInfo / PairWith2Fa requires a pinned controller certificate
            o.Interceptors.Add<PinnedControllerInterceptor>();
        });
        builder.Services.AddSingleton(security);
        builder.Services.AddSingleton(authorizer);
        builder.Services.AddSingleton(new PairingRateLimiter(security));
        builder.Services.AddSingleton<ControllerPairing>();
        builder.Services.AddSingleton(new FileManagerOps(log.Write));
        builder.Services.AddSingleton(consent);
        builder.Services.AddSingleton(log);

        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Limits.MaxRequestBodySize = 512L * 1024 * 1024;
            k.Limits.Http2.KeepAlivePingDelay = TimeSpan.FromSeconds(15);
            k.Limits.Http2.KeepAlivePingTimeout = TimeSpan.FromSeconds(5);
            k.ListenAnyIP(port, listen =>
            {
                listen.Protocols = HttpProtocols.Http2;
                listen.UseHttps(agentCert, https =>
                {
                    https.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
                    https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
                    // Same policy as the Windows agent: CA-issued or per-controller identity certificates are
                    // accepted until a controller is pinned; afterwards only pinned fingerprints.
                    https.ClientCertificateValidation = (cert, _, _) =>
                        TrustedControllerStore.ValidateAgentPeer(
                            cert, caCert, security.TrustedStorePath, onRejected: reason => log.Write($"[security] REJECTED connection: {reason}"));
                });
            });
        });

        var app = builder.Build();
        app.MapGrpcService<RemoteManagementService>();
        log.Write($"[host] listening on port {port} (Linux mutual TLS required)");
        return app;
    }
}
