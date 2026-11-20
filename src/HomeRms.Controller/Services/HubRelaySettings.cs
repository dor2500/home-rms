using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HomeRms.Shared;

namespace HomeRms.Controller.Services;

/// <summary>
/// Central Hub relay settings, persisted to %AppData%\HomeRMS\hub-relay.json. The access key is
/// stored DPAPI-protected (CurrentUser). Defaults are secure: the relay requires sign-in (access key
/// or controller TOTP / backup code) unless the operator explicitly turns that off.
/// </summary>
public sealed class HubRelaySettings
{
    /// <summary>When false the hub answers without any sign-in (explicit opt-out only).</summary>
    public bool RequireAuth { get; set; } = true;

    /// <summary>When false the hub only binds to localhost.</summary>
    public bool AllowLan { get; set; } = true;

    /// <summary>Base64 DPAPI blob of the access key.</summary>
    public string? AccessKeyProtected { get; set; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HomeRMS", "hub-relay.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static HubRelaySettings Load()
    {
        HubRelaySettings? s = null;
        try
        {
            if (File.Exists(FilePath))
                s = JsonSerializer.Deserialize<HubRelaySettings>(File.ReadAllText(FilePath));
        }
        catch (Exception ex)
        {
            // Unreadable: fall back to the secure defaults (auth required, new key).
            LogBus.Publish(LogBus.Activity, "HUB", $"hub-relay.json could not be read ({ex.Message}); using secure defaults with a new access key.");
        }

        s ??= new HubRelaySettings();
        if (s.TryGetAccessKey() is null)
        {
            s.SetAccessKey(GenerateAccessKey());
            s.Save();
        }
        return s;
    }

    public void Save()
    {
        try
        {
            AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception ex)
        {
            LogBus.Publish(LogBus.Activity, "HUB", $"Could not save hub-relay.json: {ex.Message}");
        }
    }

    /// <summary>The plain access key, or null when missing / not decryptable for this user.</summary>
    public string? TryGetAccessKey()
    {
        if (string.IsNullOrEmpty(AccessKeyProtected)) return null;
        try
        {
            var raw = ProtectedData.Unprotect(Convert.FromBase64String(AccessKeyProtected), null, DataProtectionScope.CurrentUser);
            var key = Encoding.UTF8.GetString(raw);
            return key.Length >= 16 ? key : null;
        }
        catch
        {
            return null; // DPAPI mismatch (copied profile) or tampered value: a new key is generated
        }
    }

    public void SetAccessKey(string key) =>
        AccessKeyProtected = Convert.ToBase64String(
            ProtectedData.Protect(Encoding.UTF8.GetBytes(key), null, DataProtectionScope.CurrentUser));

    /// <summary>24 characters from an unambiguous alphabet (~119 bits), grouped for readability.</summary>
    public static string GenerateAccessKey()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var chars = new char[24];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        var s = new string(chars);
        return $"{s[..6]}-{s[6..12]}-{s[12..18]}-{s[18..]}";
    }

    /// <summary>Constant-time comparison that ignores case, spaces and dashes.</summary>
    public static bool KeyMatches(string? expected, string? candidate)
    {
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(candidate)) return false;
        static byte[] Norm(string v) => Encoding.ASCII.GetBytes(
            new string(v.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray()));
        var a = Norm(expected);
        var b = Norm(candidate);
        return a.Length > 0 && CryptographicOperations.FixedTimeEquals(a, b);
    }
}
