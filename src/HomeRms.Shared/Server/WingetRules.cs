using System.Text.RegularExpressions;
using Grpc.Core;
using HomeRms.Shared.Protocol;

namespace HomeRms.Shared.Server;

/// <summary>Input validation and exit-code meanings for winget runs (shared so controllers can reuse them).</summary>
public static partial class WingetRules
{
    public const int MaxPackagesPerRequest = 50;

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9.\-_+]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex PackageIdRegex();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9.\-_+]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionRegex();

    public static bool IsValidPackageId(string? id) => id is not null && PackageIdRegex().IsMatch(id);

    public static bool IsValidVersion(string? v) => v is not null && VersionRegex().IsMatch(v);

    /// <summary>
    /// Validates the whole request; throws INVALID_ARGUMENT (nothing is run) when any ID or the
    /// version is invalid. Returns the de-duplicated package IDs in request order.
    /// </summary>
    public static List<string> ValidateRequest(WingetRequest request)
    {
        if (request.PackageIds.Count == 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "At least one package id is required."));
        if (request.PackageIds.Count > MaxPackagesPerRequest)
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"At most {MaxPackagesPerRequest} packages per request."));

        var bad = request.PackageIds.Where(id => !IsValidPackageId(id)).ToList();
        if (bad.Count > 0)
        {
            var shown = string.Join(", ", bad.Take(5).Select(b => $"'{Sanitize(b)}'"));
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"Invalid winget package id(s): {shown}. IDs may contain only letters, digits and . - _ + (max 128 chars)."));
        }

        if (request.HasVersion)
        {
            if (!IsValidVersion(request.Version))
                throw new RpcException(new Status(StatusCode.InvalidArgument, $"Invalid version '{Sanitize(request.Version)}'."));
            if (request.PackageIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                throw new RpcException(new Status(StatusCode.InvalidArgument, "A version can only be specified for a single package."));
        }

        return request.PackageIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string Sanitize(string s)
    {
        var clean = new string(s.Where(c => !char.IsControl(c)).ToArray());
        return clean.Length <= 60 ? clean : clean[..60] + "…";
    }

    // --- winget exit codes (APPINSTALLER_CLI_ERROR_* / APPINSTALLER_CLI_ERROR_INSTALL_*) ---
    public const uint InternalError = 0x8A150001;
    public const uint InvalidArguments = 0x8A150002;
    public const uint CommandFailed = 0x8A150003;
    public const uint ManifestFailed = 0x8A150004;
    public const uint CtrlSignalReceived = 0x8A150005;
    public const uint ShellExecInstallFailed = 0x8A150006;
    public const uint DownloadFailed = 0x8A150008;
    public const uint SourcesInvalid = 0x8A15000B;
    public const uint SourceDataMissing = 0x8A15000F;
    public const uint NoApplicableInstaller = 0x8A150010;
    public const uint InstallerHashMismatch = 0x8A150011;
    public const uint NoPackageFound = 0x8A150014;
    public const uint NoSources = 0x8A150015;
    public const uint MultiplePackagesFound = 0x8A150016;
    public const uint NoManifestFound = 0x8A150017;
    public const uint CommandRequiresAdmin = 0x8A150019;
    public const uint MsStoreBlockedByPolicy = 0x8A15001B;
    public const uint MsStoreAppBlockedByPolicy = 0x8A15001C;
    public const uint MsStoreInstallFailed = 0x8A15001E;
    public const uint UpdateNotApplicable = 0x8A15002B;
    public const uint UpdateAllHasFailure = 0x8A15002C;
    public const uint InstallerSecurityCheckFailed = 0x8A15002D;
    public const uint DownloadSizeMismatch = 0x8A15002E;
    public const uint PackageAlreadyInstalled = 0x8A150061;
    public const uint InstallPackageInUse = 0x8A150101;
    public const uint InstallInProgress = 0x8A150102;
    public const uint InstallFileInUse = 0x8A150103;
    public const uint InstallMissingDependency = 0x8A150104;
    public const uint InstallDiskFull = 0x8A150105;
    public const uint InstallInsufficientMemory = 0x8A150106;
    public const uint InstallNoNetwork = 0x8A150107;
    public const uint InstallContactSupport = 0x8A150108;
    public const uint InstallRebootRequiredToFinish = 0x8A150109;
    public const uint InstallRebootRequiredForInstall = 0x8A15010A;
    public const uint InstallRebootInitiated = 0x8A15010B;
    public const uint InstallCancelledByUser = 0x8A15010C;
    public const uint InstallAlreadyInstalled = 0x8A15010D;
    public const uint InstallDowngrade = 0x8A15010E;
    public const uint InstallBlockedByPolicy = 0x8A15010F;
    public const uint InstallDependencies = 0x8A150110;
    public const uint InstallPackageInUseByApplication = 0x8A150111;
    public const uint InstallInvalidParameter = 0x8A150112;
    public const uint InstallSystemNotSupported = 0x8A150113;

    private static readonly Dictionary<uint, string> Messages = new()
    {
        [0] = "Success",
        [InternalError] = "winget internal error",
        [InvalidArguments] = "Invalid winget command-line arguments",
        [CommandFailed] = "winget command failed",
        [ManifestFailed] = "Opening the package manifest failed",
        [CtrlSignalReceived] = "Cancelled (Ctrl+C / termination signal)",
        [ShellExecInstallFailed] = "Running the installer failed",
        [DownloadFailed] = "Downloading the installer failed",
        [SourcesInvalid] = "The configured winget sources are invalid or corrupt",
        [SourceDataMissing] = "winget source data is missing (try 'winget source update')",
        [NoApplicableInstaller] = "No applicable installer for this system (architecture / scope / locale)",
        [InstallerHashMismatch] = "Installer hash does not match the manifest",
        [NoPackageFound] = "No package found matching the ID (or not installed, for upgrades)",
        [NoSources] = "No winget sources are configured",
        [MultiplePackagesFound] = "Multiple packages match the ID",
        [NoManifestFound] = "No manifest found for the requested package / version",
        [CommandRequiresAdmin] = "This operation requires administrator rights",
        [MsStoreBlockedByPolicy] = "Microsoft Store source is blocked by policy",
        [MsStoreAppBlockedByPolicy] = "Microsoft Store app is blocked by policy",
        [MsStoreInstallFailed] = "Microsoft Store install failed",
        [UpdateNotApplicable] = "No applicable upgrade found (already up to date)",
        [UpdateAllHasFailure] = "One or more upgrades failed",
        [InstallerSecurityCheckFailed] = "Installer failed the security check",
        [DownloadSizeMismatch] = "Downloaded size does not match the expected size",
        [PackageAlreadyInstalled] = "Package is already installed",
        [InstallPackageInUse] = "The application is currently running; close it and retry",
        [InstallInProgress] = "Another installation is already in progress",
        [InstallFileInUse] = "One or more files are in use",
        [InstallMissingDependency] = "A dependency is missing",
        [InstallDiskFull] = "Not enough disk space",
        [InstallInsufficientMemory] = "Not enough memory",
        [InstallNoNetwork] = "No network connection",
        [InstallContactSupport] = "Installation error; contact the vendor's support",
        [InstallRebootRequiredToFinish] = "Installed; a restart is required to finish",
        [InstallRebootRequiredForInstall] = "A restart is required before installing",
        [InstallRebootInitiated] = "Installed; the installer initiated a restart",
        [InstallCancelledByUser] = "Installation was cancelled",
        [InstallAlreadyInstalled] = "Another version is already installed",
        [InstallDowngrade] = "A newer version is already installed (downgrade refused)",
        [InstallBlockedByPolicy] = "Installation is blocked by policy",
        [InstallDependencies] = "Installing dependencies failed",
        [InstallPackageInUseByApplication] = "The package is in use by another application",
        [InstallInvalidParameter] = "Invalid installer parameter",
        [InstallSystemNotSupported] = "The package is not supported on this system",
        [1641] = "Installed; restart initiated",
        [3010] = "Installed; a restart is required",
    };

    public static string Describe(int exitCode)
    {
        var code = unchecked((uint)exitCode);
        var hex = $"0x{code:X8}";
        return Messages.TryGetValue(code, out var msg)
            ? (code == 0 ? msg : $"{msg} ({hex})")
            : $"winget exited with code {hex}";
    }

    public static bool IsSuccess(int exitCode) =>
        unchecked((uint)exitCode) is 0 or InstallRebootRequiredToFinish or InstallRebootInitiated or 1641 or 3010;

    /// <summary>"Nothing to do" outcomes that are reported as Skipped rather than Failed.</summary>
    public static bool IsAlreadyInstalled(int exitCode) =>
        unchecked((uint)exitCode) is UpdateNotApplicable or PackageAlreadyInstalled or InstallAlreadyInstalled;
}
