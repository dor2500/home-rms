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

public sealed record TwoFactorConfig(string TotpSecret, bool RequireOnStartupOrPair, bool AllowPasskey);

public static class TwoFactorStore
{
    public static TwoFactorConfig LoadAgent() =>
        LoadOrCreate(Path.Combine(ControllerIdentity.SecurityDir, "agent-2fa.dpapi"), defaultRequired: true);

    public static void SaveAgent(TwoFactorConfig cfg) =>
        Save(Path.Combine(ControllerIdentity.SecurityDir, "agent-2fa.dpapi"), cfg);

    public static TwoFactorConfig LoadController() =>
        LoadOrCreate(Path.Combine(ControllerIdentity.SecurityDir, "controller-2fa.dpapi"), defaultRequired: false);

    public static void SaveController(TwoFactorConfig cfg) =>
        Save(Path.Combine(ControllerIdentity.SecurityDir, "controller-2fa.dpapi"), cfg);

    private static TwoFactorConfig LoadOrCreate(string path, bool defaultRequired)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            try
            {
                var blob = File.ReadAllBytes(path);
                var raw = OperatingSystem.IsWindows()
                    ? ProtectedData.Unprotect(blob, null, DataProtectionScope.CurrentUser)
                    : blob;
                var cfg = JsonSerializer.Deserialize<TwoFactorConfig>(raw);
                if (cfg is not null && !string.IsNullOrWhiteSpace(cfg.TotpSecret))
                    return cfg;
            }
            catch { }
        }

        var created = new TwoFactorConfig(TotpHelper.GenerateSecretBase32(), defaultRequired, true);
        Save(path, created);
        return created;
    }

    private static void Save(string path, TwoFactorConfig cfg)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var raw = JsonSerializer.SerializeToUtf8Bytes(cfg);
        var blob = OperatingSystem.IsWindows()
            ? ProtectedData.Protect(raw, null, DataProtectionScope.CurrentUser)
            : raw;
        File.WriteAllBytes(path, blob);
    }
}
