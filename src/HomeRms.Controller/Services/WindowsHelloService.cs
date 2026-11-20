using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Windows.Security.Credentials.UI;

namespace HomeRms.Controller.Services;

/// <summary>Result of a Windows Hello availability probe.</summary>
public sealed record WindowsHelloAvailability(UserConsentVerifierAvailability Availability, string Description)
{
    public bool IsAvailable => Availability == UserConsentVerifierAvailability.Available;
}

/// <summary>Result of a Windows Hello verification attempt. Only <see cref="UserConsentVerificationResult.Verified"/> unlocks.</summary>
public sealed record WindowsHelloVerification(UserConsentVerificationResult? Result, string Message)
{
    public bool IsVerified => Result == UserConsentVerificationResult.Verified;
}

/// <summary>
/// Native Windows Hello (fingerprint / face / Windows Hello PIN) user-consent verification via
/// <see cref="UserConsentVerifier"/>. Desktop (Win32/WPF) apps must use the HWND-parented
/// <see cref="UserConsentVerifierInterop"/> entry point so the system dialog is owned by our window.
/// </summary>
public static class WindowsHelloService
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    public static async Task<WindowsHelloAvailability> CheckAvailabilityAsync()
    {
        try
        {
            var availability = await UserConsentVerifier.CheckAvailabilityAsync();
            return new WindowsHelloAvailability(availability, DescribeAvailability(availability));
        }
        catch (Exception ex)
        {
            return new WindowsHelloAvailability(UserConsentVerifierAvailability.DeviceNotPresent,
                $"Windows Hello could not be queried ({ex.Message}).");
        }
    }

    /// <summary>
    /// Shows the Windows Hello prompt parented to <paramref name="owner"/>. Must be awaited on the UI thread.
    /// </summary>
    public static async Task<WindowsHelloVerification> VerifyAsync(Window owner, string message)
    {
        ArgumentNullException.ThrowIfNull(owner);
        owner.Dispatcher.VerifyAccess();

        var availability = await CheckAvailabilityAsync();
        if (!availability.IsAvailable)
            return new WindowsHelloVerification(null, availability.Description);

        var hwnd = new WindowInteropHelper(owner).EnsureHandle();

        // The consent dialog is only shown in front when its owner is the foreground window.
        if (owner.WindowState == WindowState.Minimized)
            owner.WindowState = WindowState.Normal;
        owner.Activate();
        SetForegroundWindow(hwnd);

        try
        {
            var result = await UserConsentVerifierInterop.RequestVerificationForWindowAsync(hwnd, message);
            return new WindowsHelloVerification(result, DescribeResult(result));
        }
        catch (Exception ex)
        {
            return new WindowsHelloVerification(null, $"Windows Hello failed: {ex.Message}");
        }
    }

    public static string DescribeAvailability(UserConsentVerifierAvailability availability) => availability switch
    {
        UserConsentVerifierAvailability.Available => "Available",
        UserConsentVerifierAvailability.DeviceNotPresent => "No biometric device or Windows Hello support present",
        UserConsentVerifierAvailability.NotConfiguredForUser => "Not set up for this user (configure in Settings > Accounts > Sign-in options)",
        UserConsentVerifierAvailability.DisabledByPolicy => "Disabled by group policy",
        UserConsentVerifierAvailability.DeviceBusy => "Device busy (another verification is in progress)",
        _ => $"Unavailable ({availability})",
    };

    public static string DescribeResult(UserConsentVerificationResult result) => result switch
    {
        UserConsentVerificationResult.Verified => "Verified with Windows Hello.",
        UserConsentVerificationResult.DeviceNotPresent => "Windows Hello device not present.",
        UserConsentVerificationResult.NotConfiguredForUser => "Windows Hello is not set up for this user.",
        UserConsentVerificationResult.DisabledByPolicy => "Windows Hello is disabled by group policy.",
        UserConsentVerificationResult.DeviceBusy => "Windows Hello device is busy. Try again.",
        UserConsentVerificationResult.RetriesExhausted => "Too many failed Windows Hello attempts.",
        UserConsentVerificationResult.Canceled => "Windows Hello prompt was canceled.",
        _ => $"Windows Hello verification failed ({result}).",
    };
}
