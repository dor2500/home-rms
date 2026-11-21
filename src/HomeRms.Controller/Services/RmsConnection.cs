using System.Net;
using System.Net.Http;
using System.Net.Sockets;
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

    /// <summary>The address the last TCP connection actually reached (null until connected).</summary>
    public IPAddress? RemoteAddress { get; private set; }

    /// <summary>The agent certificate presented on the last TLS handshake that passed validation.</summary>
    public X509Certificate2? ServerCertificate { get; private set; }

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
            // Our own connect step only to learn which address a computer name resolved to.
            ConnectCallback = async (ctx, ct) =>
            {
                // Dual mode (IPv4 + IPv6) unless this PC has no IPv6 stack.
                var socket = Socket.OSSupportsIPv6
                    ? new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true }
                    : new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(ctx.DnsEndPoint, ct);
                    if (socket.RemoteEndPoint is IPEndPoint ep)
                        RemoteAddress = ep.Address.IsIPv4MappedToIPv6 ? ep.Address.MapToIPv4() : ep.Address;
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
            SslOptions = new SslClientAuthenticationOptions
            {
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                ClientCertificates = new X509CertificateCollection { clientCert },
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                {
                    if (cert is null) return false;
                    var x509 = X509CertificateLoader.LoadCertificate(cert.Export(X509ContentType.Cert));
                    var ok = DevCertificates.ValidateAgainstCa(x509, ca, DevCertificates.ServerAuthOid, warning => WarnOnce(host, port, warning));
                    if (ok) ServerCertificate = x509;
                    return ok;
                }
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

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> Warned = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Logs a certificate-validity warning once per endpoint and process (handshakes repeat often).</summary>
    private static void WarnOnce(string host, int port, string warning)
    {
        if (Warned.TryAdd($"{host}:{port}|{warning}", 0))
            LogBus.Publish(LogBus.Alerts, "mTLS", $"{host}:{port}: agent {warning}.", Themes.PaletteTone.Warning);
    }
}
