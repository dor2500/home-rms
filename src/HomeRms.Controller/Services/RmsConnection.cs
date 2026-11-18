using System.Net.Http;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Grpc.Net.Client;
using HomeRms.Shared;
using HomeRms.Shared.Protocol;
using HomeRms.Shared.Security;

namespace HomeRms.Controller.Services;

/// <summary>A mutual-TLS gRPC connection to one agent.</summary>
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

        var clientCert = ControllerIdentity.GetOrCreateUniqueControllerCert();
        var ca = DevCertificates.LoadCaPublicOnly(RmsPaths.CertDir, RmsPaths.CertPassword);

        // SocketsHttpHandler (not HttpClientHandler): it does managed ALPN for HTTP/2,
        // which gRPC requires, and supports mutual-TLS client certs cleanly.
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(3),
            EnableMultipleHttp2Connections = true,
            KeepAlivePingDelay = TimeSpan.FromSeconds(15),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(5),
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
            PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
            SslOptions = new SslClientAuthenticationOptions
            {
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                ClientCertificates = new X509CertificateCollection { clientCert },
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                    cert is not null && DevCertificates.ValidateAgainstCa(
                        cert as X509Certificate2 ?? X509CertificateLoader.LoadCertificate(cert.Export(X509ContentType.Cert)),
                        ca,
                        DevCertificates.ServerAuthOid)
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
