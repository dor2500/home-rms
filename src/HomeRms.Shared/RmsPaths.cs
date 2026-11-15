namespace HomeRms.Shared;

/// <summary>Well-known locations shared by the agent and controller.</summary>
public static class RmsPaths
{
    /// <summary>
    /// Shared trust material lives here. In a real LAN deployment you generate the
    /// certificates once (tools\New-DevCerts.ps1) and copy this folder to each agent
    /// and to the controller.
    /// </summary>
    public static string CertDir =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "HomeRMS", "certs");

    /// <summary>Dev-only PFX password. Override via the HOMERMS_CERT_PASSWORD env var.</summary>
    public static string CertPassword =>
        Environment.GetEnvironmentVariable("HOMERMS_CERT_PASSWORD") ?? "homerms-dev";

    public const int DefaultPort = 7443;
}
