using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Grpc.Net.Client;
using HomeRms.Shared;
using HomeRms.Shared.Protocol;
using HomeRms.Shared.Security;

namespace HomeRms.Controller.Mac.Services;

/// <summary>A mutual-TLS gRPC connection to one Windows agent.</summary>
public sealed class RmsConnection : IDisposable
{
    private readonly GrpcChannel _channel;

    public RemoteManagement.RemoteManagementClient Client { get; }
    public string Host { get; }
    public int Port { get; }

    public RmsConnection(string host, int port)
    {
        Host = host;
        Port = port;

        var clientCert = DevCertificates.Load(RmsPaths.CertDir, DevCertificates.ControllerFileName, RmsPaths.CertPassword);
        var ca = DevCertificates.LoadCaPublicOnly(RmsPaths.CertDir, RmsPaths.CertPassword);

        var handler = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                ClientCertificates = new X509CertificateCollection { clientCert },
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                    cert is not null && DevCertificates.ValidateAgainstCa(
                        cert as X509Certificate2 ?? X509CertificateLoader.LoadCertificate(cert.Export(X509ContentType.Cert)), ca)
            }
        };

        _channel = GrpcChannel.ForAddress($"https://{host}:{port}", new GrpcChannelOptions
        {
            HttpHandler = handler,
            MaxReceiveMessageSize = 32 * 1024 * 1024
        });
        Client = new RemoteManagement.RemoteManagementClient(_channel);
    }

    public void Dispose() => _channel.Dispose();
}
