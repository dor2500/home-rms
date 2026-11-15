namespace HomeRms.Shared;

/// <summary>Well-known locations shared by the agent and controller.</summary>
public static class RmsPaths
{
    /// <summary>
    /// Shared trust material lives here. On Windows this is %ProgramData%\HomeRMS\certs;
    /// on macOS it uses ~/Library/Application Support/HomeRMS/certs so no root privileges are needed.
    /// </summary>
    public static string CertDir =>
        Path.Combine(
            Environment.GetFolderPath(
                OperatingSystem.IsWindows()
                    ? Environment.SpecialFolder.CommonApplicationData
                    : Environment.SpecialFolder.ApplicationData),
            "HomeRMS", "certs");

    /// <summary>Dev-only PFX password. Override via the HOMERMS_CERT_PASSWORD env var.</summary>
    public static string CertPassword =>
        Environment.GetEnvironmentVariable("HOMERMS_CERT_PASSWORD") ?? "homerms-dev";

    public const int DefaultPort = 7443;
}
