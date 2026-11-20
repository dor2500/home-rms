using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HomeRms.Shared.Security;

/// <summary>
/// RFC 6238 TOTP (6-digit, 30-second HMAC-SHA1) compatible with Google Authenticator,
/// Microsoft Authenticator, Bitwarden, and 1Password.
/// </summary>
public static class TotpHelper
{
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string GenerateSecretBase32(int numBytes = 20)
    {
        var bytes = RandomNumberGenerator.GetBytes(numBytes);
        return ToBase32(bytes);
    }

    public static string ComputeCode(string secretBase32, DateTimeOffset? utcNow = null)
    {
        var key = FromBase32(secretBase32);
        var counter = (utcNow ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() / 30;
        var counterBytes = BitConverter.GetBytes(counter);
        if (BitConverter.IsLittleEndian) Array.Reverse(counterBytes);

        using var hmac = new HMACSHA1(key);
        var hash = hmac.ComputeHash(counterBytes);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24)
                   | ((hash[offset + 1] & 0xFF) << 16)
                   | ((hash[offset + 2] & 0xFF) << 8)
                   | (hash[offset + 3] & 0xFF);
        return (binary % 1_000_000).ToString("D6");
    }

    public static bool VerifyCode(string secretBase32, string candidate, int windowSteps = 1)
    {
        var clean = new string((candidate ?? "").Where(char.IsDigit).ToArray());
        if (clean.Length != 6) return false;

        var now = DateTimeOffset.UtcNow;
        for (var i = -windowSteps; i <= windowSteps; i++)
        {
            var expected = ComputeCode(secretBase32, now.AddSeconds(i * 30));
            if (CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(expected),
                    Encoding.ASCII.GetBytes(clean)))
                return true;
        }
        return false;
    }

    public static int SecondsRemaining() => 30 - (int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() % 30);

    public static string BuildOtpAuthUri(string issuer, string account, string secretBase32) =>
        $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}?secret={secretBase32}&issuer={Uri.EscapeDataString(issuer)}&digits=6&period=30";

    public static byte[] GenerateQrPng(string payload, int pixelsPerModule = 6)
    {
        using var gen = new QRCoder.QRCodeGenerator();
        using var data = gen.CreateQrCode(payload, QRCoder.QRCodeGenerator.ECCLevel.M);
        var qr = new QRCoder.PngByteQRCode(data);
        return qr.GetGraphic(pixelsPerModule);
    }

    private static string ToBase32(byte[] data)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = data[0], next = 1, bitsLeft = 8;
        while (bitsLeft > 0 || next < data.Length)
        {
            if (bitsLeft < 5)
            {
                if (next < data.Length)
                {
                    buffer <<= 8;
                    buffer |= data[next++] & 0xFF;
                    bitsLeft += 8;
                }
                else
                {
                    var pad = 5 - bitsLeft;
                    buffer <<= pad;
                    bitsLeft += pad;
                }
            }
            var index = 0x1F & (buffer >> (bitsLeft - 5));
            bitsLeft -= 5;
            sb.Append(Base32Alphabet[index]);
        }
        return sb.ToString();
    }

    private static byte[] FromBase32(string base32)
    {
        var clean = new string(base32.Trim().ToUpperInvariant().Where(c => Base32Alphabet.Contains(c)).ToArray());
        var outBytes = new List<byte>(clean.Length * 5 / 8);
        int buffer = 0, bitsLeft = 0;
        foreach (var c in clean)
        {
            buffer = (buffer << 5) | Base32Alphabet.IndexOf(c);
            bitsLeft += 5;
            if (bitsLeft >= 8)
            {
                outBytes.Add((byte)((buffer >> (bitsLeft - 8)) & 0xFF));
                bitsLeft -= 8;
            }
        }
        return outBytes.ToArray();
    }
}

/// <summary>
/// DPAPI-protected (CurrentUser) 2FA / unlock configuration. All parameters after
/// <see cref="TrustedDevices"/> were added in v2.1 and carry defaults so older config files still load.
/// </summary>
public sealed record TwoFactorConfig(
    string TotpSecret,
    bool RequireOnStartupOrPair,
    bool AllowPasskey,
    List<string>? BackupCodes = null,
    int RememberDays = 30,
    Dictionary<string, DateTimeOffset>? TrustedDevices = null,
    // Optional master password: PBKDF2-HMAC-SHA256, Base64 salt + Base64 derived key.
    string? MasterPasswordSalt = null,
    string? MasterPasswordHash = null,
    int MasterPasswordIterations = 0,
    // Persistent unlock throttling (TOTP / backup code / master password failures).
    int FailedUnlockAttempts = 0,
    DateTimeOffset? UnlockBlockedUntil = null);

public static class TwoFactorStore
{
    public const int MasterPasswordMinIterations = 600_000;
    public const int MasterPasswordMinLength = 8;
    public const int FreeUnlockAttempts = 5;
    private const int MasterPasswordSaltBytes = 16;
    private const int MasterPasswordKeyBytes = 32;
    private static readonly TimeSpan BaseUnlockDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxUnlockDelay = TimeSpan.FromHours(1);

    private static readonly object Gate = new();
    private static readonly Dictionary<string, string> LoadWarnings = new(StringComparer.OrdinalIgnoreCase);

    private static string AgentPath => Path.Combine(ControllerIdentity.SecurityDir, "agent-2fa.dpapi");
    private static string ControllerPath => Path.Combine(ControllerIdentity.SecurityDir, "controller-2fa.dpapi");
    private static string SecurityLogPath => Path.Combine(ControllerIdentity.SecurityDir, "security-events.log");

    public static TwoFactorConfig LoadAgent() => LoadOrCreate(AgentPath, defaultRequired: false);

    public static void SaveAgent(TwoFactorConfig cfg) => Save(AgentPath, cfg);

    public static TwoFactorConfig LoadController() => LoadOrCreate(ControllerPath, defaultRequired: false);

    public static void SaveController(TwoFactorConfig cfg) => Save(ControllerPath, cfg);

    /// <summary>
    /// Non-null when the controller 2FA file could not be decrypted/parsed during this process lifetime
    /// (the unreadable file was preserved as a timestamped backup and a fresh config was created).
    /// </summary>
    public static string? ControllerLoadWarning
    {
        get { lock (Gate) return LoadWarnings.GetValueOrDefault(ControllerPath); }
    }

    public static string? AgentLoadWarning
    {
        get { lock (Gate) return LoadWarnings.GetValueOrDefault(AgentPath); }
    }

    public static List<string> GenerateBackupCodes(int count = 8)
    {
        var list = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var bytes = RandomNumberGenerator.GetBytes(4);
            var hex = Convert.ToHexString(bytes);
            list.Add($"{hex[..4]}-{hex[4..]}");
        }
        return list;
    }

    public static bool VerifyCodeOrBackup(TwoFactorConfig cfg, string input, out TwoFactorConfig updated, out bool usedBackup)
    {
        usedBackup = false;
        updated = cfg;
        var clean = (input ?? "").Trim().ToUpperInvariant();
        if (clean.Length == 0) return false;
        if (TotpHelper.VerifyCode(cfg.TotpSecret, clean))
            return true;

        var codes = cfg.BackupCodes ?? [];
        var candidate = Encoding.ASCII.GetBytes(clean);
        var idx = -1;
        for (var i = 0; i < codes.Count; i++)
        {
            var stored = Encoding.ASCII.GetBytes(codes[i].Trim().ToUpperInvariant());
            if (CryptographicOperations.FixedTimeEquals(stored, candidate) && idx < 0)
                idx = i;
        }
        if (idx >= 0)
        {
            var next = new List<string>(codes);
            next.RemoveAt(idx);
            updated = cfg with { BackupCodes = next };
            usedBackup = true;
            return true;
        }
        return false;
    }

    public static bool IsDeviceRemembered(TwoFactorConfig cfg, string deviceId)
    {
        if (cfg.RememberDays <= 0 || cfg.TrustedDevices is null) return false;
        return cfg.TrustedDevices.TryGetValue(deviceId, out var expires) && expires > DateTimeOffset.UtcNow;
    }

    public static DateTimeOffset? GetDeviceTrustExpiry(TwoFactorConfig cfg, string deviceId) =>
        cfg.TrustedDevices is not null && cfg.TrustedDevices.TryGetValue(deviceId, out var expires) ? expires : null;

    public static TwoFactorConfig TrustDevice(TwoFactorConfig cfg, string deviceId)
    {
        var days = Math.Clamp(cfg.RememberDays <= 0 ? 30 : cfg.RememberDays, 1, 365);
        var map = new Dictionary<string, DateTimeOffset>(cfg.TrustedDevices ?? []);
        map[deviceId] = DateTimeOffset.UtcNow.AddDays(days);
        return cfg with { TrustedDevices = map };
    }

    public static TwoFactorConfig ForgetDevice(TwoFactorConfig cfg, string deviceId)
    {
        if (cfg.TrustedDevices is null || !cfg.TrustedDevices.ContainsKey(deviceId)) return cfg;
        var map = new Dictionary<string, DateTimeOffset>(cfg.TrustedDevices);
        map.Remove(deviceId);
        return cfg with { TrustedDevices = map };
    }

    // ---------------- Master password (PBKDF2-HMAC-SHA256) ----------------

    public static bool HasMasterPassword(TwoFactorConfig cfg) =>
        !string.IsNullOrEmpty(cfg.MasterPasswordHash) && !string.IsNullOrEmpty(cfg.MasterPasswordSalt) && cfg.MasterPasswordIterations > 0;

    /// <summary>Derives and stores a new master password hash. CPU-heavy (~0.5 s): call off the UI thread.</summary>
    public static TwoFactorConfig SetMasterPassword(TwoFactorConfig cfg, string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MasterPasswordMinLength)
            throw new ArgumentException($"Master password must be at least {MasterPasswordMinLength} characters.", nameof(password));

        var salt = RandomNumberGenerator.GetBytes(MasterPasswordSaltBytes);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, MasterPasswordMinIterations, HashAlgorithmName.SHA256, MasterPasswordKeyBytes);
        return cfg with
        {
            MasterPasswordSalt = Convert.ToBase64String(salt),
            MasterPasswordHash = Convert.ToBase64String(key),
            MasterPasswordIterations = MasterPasswordMinIterations,
        };
    }

    public static TwoFactorConfig ClearMasterPassword(TwoFactorConfig cfg) =>
        cfg with { MasterPasswordSalt = null, MasterPasswordHash = null, MasterPasswordIterations = 0 };

    /// <summary>Constant-time master password check. CPU-heavy (~0.5 s): call off the UI thread.</summary>
    public static bool VerifyMasterPassword(TwoFactorConfig cfg, string candidate)
    {
        if (!HasMasterPassword(cfg) || string.IsNullOrEmpty(candidate)) return false;
        try
        {
            var salt = Convert.FromBase64String(cfg.MasterPasswordSalt!);
            var expected = Convert.FromBase64String(cfg.MasterPasswordHash!);
            if (expected.Length != MasterPasswordKeyBytes) return false;
            // Never accept a downgraded work factor, even if the stored value was tampered with.
            var iterations = Math.Max(cfg.MasterPasswordIterations, MasterPasswordMinIterations);
            var actual = Rfc2898DeriveBytes.Pbkdf2(candidate, salt, iterations, HashAlgorithmName.SHA256, MasterPasswordKeyBytes);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    // ---------------- Unlock throttling ----------------

    /// <summary>Time remaining before another TOTP / backup / password attempt is accepted (zero when allowed).</summary>
    public static TimeSpan GetUnlockDelayRemaining(TwoFactorConfig cfg)
    {
        if (cfg.UnlockBlockedUntil is not { } until) return TimeSpan.Zero;
        var remaining = until - DateTimeOffset.UtcNow;
        // Guard against a stored value far beyond the maximum delay (clock changes / tampering).
        if (remaining > MaxUnlockDelay) remaining = MaxUnlockDelay;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    /// <summary>Records a failed attempt; from the 5th failure on, blocks for 30 s, 60 s, 120 s, ... (max 1 h).</summary>
    public static TwoFactorConfig RegisterUnlockFailure(TwoFactorConfig cfg)
    {
        var failures = cfg.FailedUnlockAttempts + 1;
        DateTimeOffset? blockedUntil = null;
        if (failures >= FreeUnlockAttempts)
        {
            var exponent = Math.Min(failures - FreeUnlockAttempts, 10);
            var delay = TimeSpan.FromTicks(Math.Min(BaseUnlockDelay.Ticks << exponent, MaxUnlockDelay.Ticks));
            blockedUntil = DateTimeOffset.UtcNow + delay;
        }
        return cfg with { FailedUnlockAttempts = failures, UnlockBlockedUntil = blockedUntil };
    }

    public static TwoFactorConfig RegisterUnlockSuccess(TwoFactorConfig cfg) =>
        cfg.FailedUnlockAttempts == 0 && cfg.UnlockBlockedUntil is null
            ? cfg
            : cfg with { FailedUnlockAttempts = 0, UnlockBlockedUntil = null };

    // ---------------- Persistence ----------------

    public static void LogSecurityEvent(string message)
    {
        try
        {
            Directory.CreateDirectory(ControllerIdentity.SecurityDir);
            File.AppendAllText(SecurityLogPath, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch { /* logging must never break authentication */ }
    }

    private static TwoFactorConfig LoadOrCreate(string path, bool defaultRequired)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path))
            {
                // A transient (locked / in-use) read failure must never cause the secret to be replaced:
                // retry briefly, then let the IOException propagate with the file left untouched.
                byte[] blob;
                for (var attempt = 1; ; attempt++)
                {
                    try
                    {
                        blob = File.ReadAllBytes(path);
                        break;
                    }
                    catch (IOException ex) when (attempt < 3)
                    {
                        LogSecurityEvent($"2FA config '{Path.GetFileName(path)}' read attempt {attempt} failed: {ex.Message}");
                        Thread.Sleep(100);
                    }
                }

                string failure;
                try
                {
                    var raw = OperatingSystem.IsWindows()
                        ? ProtectedData.Unprotect(blob, null, DataProtectionScope.CurrentUser)
                        : blob;
                    var cfg = JsonSerializer.Deserialize<TwoFactorConfig>(raw);
                    if (cfg is not null && !string.IsNullOrWhiteSpace(cfg.TotpSecret))
                    {
                        if (cfg.BackupCodes is null)
                        {
                            cfg = cfg with { BackupCodes = GenerateBackupCodes(), RememberDays = cfg.RememberDays <= 0 ? 30 : cfg.RememberDays };
                            Save(path, cfg);
                        }
                        return cfg;
                    }
                    failure = "decrypted config has no TOTP secret";
                }
                catch (CryptographicException ex) { failure = $"DPAPI decryption failed ({ex.Message.Trim()})"; }
                catch (JsonException ex) { failure = $"config is not valid JSON ({ex.Message})"; }
                catch (NotSupportedException ex) { failure = $"config could not be deserialized ({ex.Message})"; }

                // Never silently replace an unreadable secret: keep the original for recovery and report it.
                var backup = $"{path}.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}";
                try { File.Move(path, backup); }
                catch (Exception ex) { backup = $"(backup failed: {ex.Message})"; }

                var warning = $"The 2FA configuration could not be read: {failure}. " +
                              $"The original file was preserved as '{Path.GetFileName(backup)}'. " +
                              "A new TOTP secret and backup codes were generated; re-enroll your authenticator app and review the security settings.";
                LoadWarnings[path] = warning;
                LogSecurityEvent($"WARNING {Path.GetFileName(path)}: {failure}; preserved as {backup}");
            }

            var created = NewConfig(defaultRequired);
            Save(path, created);
            return created;
        }
    }

    private static TwoFactorConfig NewConfig(bool defaultRequired) =>
        new(TotpHelper.GenerateSecretBase32(), defaultRequired, true, GenerateBackupCodes(), 30, new());

    private static void Save(string path, TwoFactorConfig cfg)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var raw = JsonSerializer.SerializeToUtf8Bytes(cfg);
            var blob = OperatingSystem.IsWindows()
                ? ProtectedData.Protect(raw, null, DataProtectionScope.CurrentUser)
                : raw;
            // Write-then-replace so a crash mid-write cannot leave a truncated (unreadable) file behind.
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, blob);
            File.Move(tmp, path, overwrite: true);
        }
    }
}

