using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using HomeRms.Agent.Core;
using HomeRms.Shared;
using HomeRms.Shared.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HomeRms.Agent.Server;

/// <summary>Builds the Kestrel + gRPC host that enforces mutual TLS against the deployment CA.</summary>
public static class AgentHost
{
    public static WebApplication Build(ConsentCoordinator consent, ActivityLog log, int port)
    {
        var agentCert = DevCertificates.Load(RmsPaths.CertDir, DevCertificates.AgentFileName, RmsPaths.CertPassword);
        var caCert = DevCertificates.LoadCaPublicOnly(RmsPaths.CertDir, RmsPaths.CertPassword);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddGrpc(o => o.MaxReceiveMessageSize = 64 * 1024 * 1024);
        builder.Services.AddSingleton(consent);
        builder.Services.AddSingleton(log);

        builder.WebHost.ConfigureKestrel(k =>
        {
            k.ListenAnyIP(port, listen =>
            {
                listen.Protocols = HttpProtocols.Http2;
                listen.UseHttps(agentCert, https =>
                {
                    https.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
                    https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
                    https.ClientCertificateValidation = (cert, _, _) =>
                        DevCertificates.ValidateAgainstCa(cert, caCert, DevCertificates.ClientAuthOid);
                });
            });
        });

        var app = builder.Build();
        app.MapGrpcService<RemoteManagementService>();
        log.Write($"[host] listening on port {port} (mutual TLS required)");
        return app;
    }
}
