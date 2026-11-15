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
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(consent);
        builder.Services.AddSingleton(log);

        builder.WebHost.ConfigureKestrel(k =>
        {
            k.ListenAnyIP(port, listen =>
            {
                listen.Protocols = HttpProtocols.Http2;
                listen.UseHttps(agentCert, https =>
                {
                    https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
                    https.ClientCertificateValidation = (cert, _, _) => ValidateAgainstCa(cert, caCert);
                });
            });
        });

        var app = builder.Build();
        app.MapGrpcService<RemoteManagementService>();
        log.Write($"[host] listening on port {port} (mutual TLS required)");
        return app;
    }

    /// <summary>Accepts a client certificate only if it chains to our deployment CA.</summary>
    private static bool ValidateAgainstCa(X509Certificate2 clientCert, X509Certificate2 ca)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        return chain.Build(clientCert);
    }
}
