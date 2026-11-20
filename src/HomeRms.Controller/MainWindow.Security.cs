using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HomeRms.Controller.Services;
using HomeRms.Shared.Security;

namespace HomeRms.Controller;

/// <summary>
/// Console lock and unlock-factor management.
/// Fallback chain: Windows Hello (fingerprint / face / PIN) -> TOTP or backup code -> optional master password.
/// </summary>
public partial class MainWindow
{
    private const string HelloUnlockMessage = "Verify your identity to unlock Home RMS";

    /// <summary>Key used for "remember this device" (the console runs on this machine).</summary>
    private static string LocalDeviceId => Environment.MachineName;

    private bool _helloAutoPromptPending;
    private bool _helloAvailable;
    private bool _helloBusy;
    private bool _unlockBusy;
    private bool _aboutSecurityBusy;
    private bool _lockActivationHooked;
    private DispatcherTimer? _lockThrottleTimer;
    private DateTime _lockThrottleUntilUtc;

    private bool IsConsoleLocked => LockOverlay.Visibility == Visibility.Visible;

    // ---------------- Locking ----------------

    /// <summary>Startup lock: honours "Lock on startup" unless this device is remembered within its window.</summary>
    private void ApplyStartupLock()
    {
        TwoFactorConfig cfg;
        try
        {
            cfg = TwoFactorStore.LoadController();
        }
        catch (Exception ex)
        {
            // Fail closed: if the lock configuration cannot be read, lock the console.
            TwoFactorStore.LogSecurityEvent($"Startup lock config unreadable: {ex.Message}");
            ShowLockOverlay($"Security settings could not be read ({ex.Message}).");
            return;
        }
        if (TwoFactorStore.ControllerLoadWarning is { } warning)
            AddActivity("Auth", warning);

        if (!cfg.RequireOnStartupOrPair) return;
        if (TwoFactorStore.IsDeviceRemembered(cfg, LocalDeviceId))
        {
            var until = TwoFactorStore.GetDeviceTrustExpiry(cfg, LocalDeviceId);
            AddActivity("Auth", $"Startup lock skipped: this device is remembered until {until?.ToLocalTime():yyyy-MM-dd HH:mm}.");
            return;
        }
        ShowLockOverlay(null);
    }

    private void ShowLockOverlay(string? reason)
    {
        if (!_lockActivationHooked)
        {
            Activated += OnWindowActivatedWhileLocked;
            _lockActivationHooked = true;
        }

        // Hide anything showing secrets (TOTP secret / backup codes) behind the lock.
        ClearAboutSecretInputs();
        AboutOverlay.Visibility = Visibility.Collapsed;
        AgentPair2FaOverlay.Visibility = Visibility.Collapsed;
        // A pending pairing prompt would otherwise wait forever (and block every later prompt).
        _agent2FaTcs?.TrySetResult(null);
        CloseWindowsOutsideLock();
        // The scrim is translucent: do not leave the remote screen / shell output readable behind it.
        ShellBodyGrid.Visibility = Visibility.Hidden;

        UnlockPinBox.Clear();
        UnlockPasswordBox.Clear();
        RememberDeviceCheck.IsChecked = false;
        LockOverlay.Visibility = Visibility.Visible;
        SetLockStatus(reason, warn: false);

        _helloAutoPromptPending = true;
        _ = RefreshLockOverlayAsync(autoPrompt: true);
        FocusLockInput();
    }

    /// <summary>
    /// Windows outside the main window are not covered by the lock overlay: close the detached
    /// "Screen 2" viewer (live remote screen) and any open dialog owned by this window.
    /// </summary>
    private void CloseWindowsOutsideLock()
    {
        if (_detachedMonitorWindow is { } detached)
        {
            _detachedMonitorCts?.Cancel();
            detached.Close();
            _detachedMonitorWindow = null;
        }
        foreach (var owned in OwnedWindows.Cast<Window>().ToList())
        {
            try { owned.Close(); }
            catch (InvalidOperationException) { /* already closing */ }
        }
    }

    private async Task RefreshLockOverlayAsync(bool autoPrompt)
    {
        try
        {
            var cfg = TwoFactorStore.LoadController();
            MasterPasswordUnlockPanel.Visibility = TwoFactorStore.HasMasterPassword(cfg) ? Visibility.Visible : Visibility.Collapsed;
            var days = Math.Clamp(cfg.RememberDays <= 0 ? 30 : cfg.RememberDays, 1, 365);
            RememberDeviceCheck.Content = $"Remember this device for {days} days (skip startup lock)";
            UpdateThrottleUi(cfg, null);

            if (!cfg.AllowPasskey)
            {
                _helloAvailable = false;
                _helloAutoPromptPending = false;
                HelloUnlockButton.Visibility = Visibility.Collapsed;
                HelloStatusText.Text = "Windows Hello unlock is turned off in the security settings.";
                return;
            }

            HelloUnlockButton.Visibility = Visibility.Visible;
            HelloUnlockButton.IsEnabled = false;
            HelloStatusText.Text = "Checking Windows Hello...";

            var availability = await WindowsHelloService.CheckAvailabilityAsync();
            if (!IsConsoleLocked) return;

            _helloAvailable = availability.CanAttemptVerification;
            HelloUnlockButton.IsEnabled = _helloAvailable && !_helloBusy;
            HelloStatusText.Text = availability.IsAvailable
                ? "Fingerprint, face or Windows Hello PIN."
                : $"Windows Hello unavailable: {availability.Description}.";

            if (availability.IsAvailable && autoPrompt)
                TryAutoPromptHello();
            else if (!availability.IsAvailable)
                _helloAutoPromptPending = false;
        }
        catch (Exception ex)
        {
            if (!IsConsoleLocked) return;
            _helloAvailable = false;
            _helloAutoPromptPending = false;
            HelloUnlockButton.IsEnabled = false;
            HelloStatusText.Text = $"Security configuration error ({ex.GetType().Name}): {ex.Message}";
            SetLockStatus($"Security settings could not be read ({ex.Message}).", warn: true);
            TwoFactorStore.LogSecurityEvent($"RefreshLockOverlayAsync failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Auto-prompts Windows Hello once per lock, only when the window is visible and in the foreground.</summary>
    private void TryAutoPromptHello()
    {
        if (!_helloAutoPromptPending || !_helloAvailable || !IsConsoleLocked) return;
        // Defer while hidden / minimized / in the background so we never steal focus; Activated retries.
        if (!IsLoaded || !IsVisible || WindowState == WindowState.Minimized || !IsActive) return;

        _helloAutoPromptPending = false;
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => _ = RunHelloUnlockAsync()));
    }

    private void OnWindowActivatedWhileLocked(object? sender, EventArgs e)
    {
        if (IsConsoleLocked) TryAutoPromptHello();
    }

    private async void OnUnlockWithPasskeyClick(object sender, RoutedEventArgs e)
    {
        _helloAutoPromptPending = false;
        await RunHelloUnlockAsync();
    }

    private async Task RunHelloUnlockAsync()
    {
        if (_helloBusy || !IsConsoleLocked) return;

        _helloBusy = true;
        HelloUnlockButton.IsEnabled = false;
        HelloStatusText.Text = "Waiting for Windows Hello...";
        try
        {
            if (!TwoFactorStore.LoadController().AllowPasskey)
            {
                _helloAvailable = false;
                HelloStatusText.Text = "Windows Hello unlock is turned off in the security settings.";
                return;
            }

            var result = await WindowsHelloService.VerifyAsync(this, HelloUnlockMessage);
            if (!IsConsoleLocked) return;
            if (result.IsVerified)
            {
                CompleteUnlock("Windows Hello");
                return;
            }
            HelloStatusText.Text = $"{result.Message} Retry with the button above or use another method below.";
            TwoFactorStore.LogSecurityEvent($"Windows Hello unlock not verified: {result.Result?.ToString() ?? "error"} ({result.Message})");
        }
        catch (Exception ex)
        {
            if (!IsConsoleLocked) return;
            HelloStatusText.Text = $"Windows Hello error ({ex.GetType().Name}): {ex.Message}";
            TwoFactorStore.LogSecurityEvent($"Windows Hello unlock failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _helloBusy = false;
            if (IsConsoleLocked)
            {
                HelloUnlockButton.IsEnabled = _helloAvailable;
                FocusLockInput();
            }
        }
    }

    private void OnUnlockPinBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        OnUnlockDashboardClick(sender, e);
    }

    private void OnUnlockPasswordBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        OnUnlockWithPasswordClick(sender, e);
    }

    private void OnUnlockDashboardClick(object sender, RoutedEventArgs e)
    {
        if (_unlockBusy || !IsConsoleLocked) return;
        try
        {
            var cfg = TwoFactorStore.LoadController();
            if (TwoFactorStore.GetUnlockDelayRemaining(cfg) > TimeSpan.Zero)
            {
                UpdateThrottleUi(cfg, null);
                return;
            }

            var input = UnlockPinBox.Password;
            if (string.IsNullOrWhiteSpace(input))
            {
                SetLockStatus("Enter the 6-digit authenticator code or a backup code.", warn: true);
                FocusLockInput();
                return;
            }

            if (TwoFactorStore.VerifyCodeOrBackup(cfg, input, out var updated, out var usedBackup))
            {
                if (usedBackup)
                {
                    TwoFactorStore.SaveController(updated);
                    StatusText.Text = $"Backup code used. {updated.BackupCodes?.Count ?? 0} backup code(s) remain.";
                }
                CompleteUnlock(usedBackup ? "a backup code" : "an authenticator code");
            }
            else
            {
                UnlockPinBox.Clear();
                RecordUnlockFailure(cfg, "Invalid authenticator or backup code.");
            }
        }
        catch (Exception ex)
        {
            SetLockStatus($"Security settings error ({ex.GetType().Name}): {ex.Message}", warn: true);
            TwoFactorStore.LogSecurityEvent($"Dashboard unlock failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async void OnUnlockWithPasswordClick(object sender, RoutedEventArgs e)
    {
        if (_unlockBusy || !IsConsoleLocked) return;
        try
        {
            var cfg = TwoFactorStore.LoadController();
            if (!TwoFactorStore.HasMasterPassword(cfg)) return;
            if (TwoFactorStore.GetUnlockDelayRemaining(cfg) > TimeSpan.Zero)
            {
                UpdateThrottleUi(cfg, null);
                return;
            }

            var password = UnlockPasswordBox.Password;
            if (string.IsNullOrEmpty(password))
            {
                SetLockStatus("Enter your master password.", warn: true);
                UnlockPasswordBox.Focus();
                return;
            }

            _unlockBusy = true;
            UpdateThrottleUi(cfg, null);
            SetLockStatus("Verifying password...", warn: false);
            bool ok;
            try
            {
                ok = await Task.Run(() => TwoFactorStore.VerifyMasterPassword(cfg, password));
            }
            finally
            {
                _unlockBusy = false;
            }
            if (!IsConsoleLocked) return;

            if (ok)
            {
                CompleteUnlock("the master password");
            }
            else
            {
                UnlockPasswordBox.Clear();
                RecordUnlockFailure(TwoFactorStore.LoadController(), "Incorrect master password.");
                UnlockPasswordBox.Focus();
            }
        }
        catch (Exception ex)
        {
            if (!IsConsoleLocked) return;
            SetLockStatus($"Security settings error ({ex.GetType().Name}): {ex.Message}", warn: true);
            TwoFactorStore.LogSecurityEvent($"Master password unlock failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void CompleteUnlock(string method)
    {
        try
        {
            var cfg = TwoFactorStore.LoadController();
            var updated = TwoFactorStore.RegisterUnlockSuccess(cfg);
            if (RememberDeviceCheck.IsChecked == true)
                updated = TwoFactorStore.TrustDevice(updated, LocalDeviceId);
            if (!ReferenceEquals(updated, cfg))
                TwoFactorStore.SaveController(updated);
        }
        catch (Exception ex)
        {
            SetLockStatus($"Could not complete unlock ({ex.GetType().Name}): {ex.Message}", warn: true);
            TwoFactorStore.LogSecurityEvent($"CompleteUnlock failed: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        _lockThrottleTimer?.Stop();
        _helloAutoPromptPending = false;
        UnlockPinBox.Clear();
        UnlockPasswordBox.Clear();
        RememberDeviceCheck.IsChecked = false;
        SetLockStatus(null, warn: false);
        _lastActivityUtc = DateTime.UtcNow;
        LockOverlay.Visibility = Visibility.Collapsed;
        ShellBodyGrid.Visibility = Visibility.Visible;
        AddActivity("Auth", $"Console unlocked with {method}.");
    }

    private void RecordUnlockFailure(TwoFactorConfig cfg, string message)
    {
        var updated = TwoFactorStore.RegisterUnlockFailure(cfg);
        TwoFactorStore.SaveController(updated);
        TwoFactorStore.LogSecurityEvent($"Console unlock failed ({message}); consecutive failures: {updated.FailedUnlockAttempts}");
        UpdateThrottleUi(updated, message);
        FocusLockInput();
    }

    /// <summary>Enables/disables code and password entry according to the persisted attempt throttle.</summary>
    private void UpdateThrottleUi(TwoFactorConfig cfg, string? failureMessage)
    {
        var wait = TwoFactorStore.GetUnlockDelayRemaining(cfg);
        var blocked = wait > TimeSpan.Zero;
        var enabled = !blocked && !_unlockBusy;
        UnlockPinBox.IsEnabled = enabled;
        UnlockCodeButton.IsEnabled = enabled;
        UnlockPasswordBox.IsEnabled = enabled;
        UnlockPasswordButton.IsEnabled = enabled;

        if (blocked)
        {
            _lockThrottleUntilUtc = DateTime.UtcNow + wait;
            var prefix = failureMessage is null ? "" : failureMessage + " ";
            SetLockStatus($"{prefix}Too many failed attempts. Code and password entry are paused for {FormatWait(wait)}.", warn: true);
            if (_lockThrottleTimer is null)
            {
                _lockThrottleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _lockThrottleTimer.Tick += OnLockThrottleTick;
            }
            _lockThrottleTimer.Start();
            return;
        }

        if (failureMessage is not null)
        {
            var left = Math.Max(0, TwoFactorStore.FreeUnlockAttempts - cfg.FailedUnlockAttempts);
            SetLockStatus(left > 0
                ? $"{failureMessage} {left} attempt(s) left before a delay is enforced."
                : failureMessage, warn: true);
        }
    }

    private void OnLockThrottleTick(object? sender, EventArgs e)
    {
        if (!IsConsoleLocked)
        {
            _lockThrottleTimer?.Stop();
            return;
        }
        var remaining = _lockThrottleUntilUtc - DateTime.UtcNow;
        if (remaining > TimeSpan.Zero)
        {
            SetLockStatus($"Too many failed attempts. Code and password entry are paused for {FormatWait(remaining)}.", warn: true);
            return;
        }
        _lockThrottleTimer?.Stop();
        UpdateThrottleUi(TwoFactorStore.LoadController(), null);
        SetLockStatus("You can try again now.", warn: false);
        FocusLockInput();
    }

    private void SetLockStatus(string? message, bool warn)
    {
        LockStatusText.Text = message ?? "";
        LockStatusText.Foreground = (Brush)FindResource(warn ? "WarnBrush" : "MutedBrush");
        LockStatusText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void FocusLockInput()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (!IsConsoleLocked || _helloBusy) return;
            if (UnlockPinBox.IsEnabled) UnlockPinBox.Focus();
            else if (HelloUnlockButton.IsVisible && HelloUnlockButton.IsEnabled) HelloUnlockButton.Focus();
            else RememberDeviceCheck.Focus();
        }));
    }

    private bool IsInsideLockOverlay(IInputElement? element) =>
        element is Visual v && (ReferenceEquals(v, LockOverlay) || LockOverlay.IsAncestorOf(v));

    private static string FormatWait(TimeSpan wait)
    {
        var seconds = (int)Math.Ceiling(wait.TotalSeconds);
        return seconds < 60 ? $"{seconds}s" : $"{seconds / 60}m {seconds % 60:D2}s";
    }

    // ---------------- About overlay: security section ----------------

    private void ClearAboutSecretInputs()
    {
        CurrentFactorBox.Clear();
        NewMasterPasswordBox.Clear();
        ConfirmMasterPasswordBox.Clear();
    }

    private async Task RefreshSecuritySectionAsync()
    {
        try
        {
            var cfg = TwoFactorStore.LoadController();

            if (TwoFactorStore.ControllerLoadWarning is { } warning)
            {
                SecurityWarningText.Text = warning;
                SecurityWarningBorder.Visibility = Visibility.Visible;
            }
            else
            {
                SecurityWarningBorder.Visibility = Visibility.Collapsed;
            }

            ClearMasterPasswordButton.IsEnabled = TwoFactorStore.HasMasterPassword(cfg) && !_aboutSecurityBusy;
            SetMasterPasswordButton.Content = TwoFactorStore.HasMasterPassword(cfg) ? "Change Password" : "Set Password";
            ForgetDeviceButton.IsEnabled = TwoFactorStore.IsDeviceRemembered(cfg, LocalDeviceId);

            SecurityDiagText.Text = BuildSecurityDiagnostics(cfg, "checking...", null);
            var hello = await WindowsHelloService.CheckAvailabilityAsync();
            cfg = TwoFactorStore.LoadController();
            var helloText = hello.Description + (cfg.AllowPasskey ? "" : " (unlock with Windows Hello turned off)");
            SecurityDiagText.Text = BuildSecurityDiagnostics(cfg, helloText, hello);
        }
        catch (Exception ex)
        {
            SecurityDiagText.Text = $"Security diagnostics unavailable ({ex.GetType().Name}): {ex.Message}";
            TwoFactorStore.LogSecurityEvent($"RefreshSecuritySectionAsync failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string BuildSecurityDiagnostics(TwoFactorConfig cfg, string helloStatus, WindowsHelloAvailability? hello)
    {
        var days = Math.Clamp(cfg.RememberDays <= 0 ? 30 : cfg.RememberDays, 1, 365);
        var expiry = TwoFactorStore.GetDeviceTrustExpiry(cfg, LocalDeviceId);
        var remember = TwoFactorStore.IsDeviceRemembered(cfg, LocalDeviceId)
            ? $"remembered until {expiry!.Value.ToLocalTime():yyyy-MM-dd HH:mm}"
            : $"not remembered ({days}-day window)";
        var totp = string.IsNullOrWhiteSpace(cfg.TotpSecret)
            ? "not enrolled"
            : $"enrolled ({cfg.BackupCodes?.Count ?? 0} backup codes left)";
        var rawState = hello is null
            ? "checking..."
            : hello.Availability is { } avail
                ? avail.ToString()
                : $"Error ({hello.ErrorType ?? "Exception"} 0x{hello.HResult ?? 0:X8})";
        var elevated = WindowsHelloService.IsProcessElevated()
            ? "yes (Administrator / requireAdministrator)"
            : "no (standard user)";
        var throttle = TwoFactorStore.GetUnlockDelayRemaining(cfg) is var w && w > TimeSpan.Zero
            ? $"\nUnlock throttle:  paused for {FormatWait(w)}"
            : cfg.FailedUnlockAttempts > 0 ? $"\nFailed attempts:  {cfg.FailedUnlockAttempts}" : "";
        return
            $"Windows Hello:    {helloStatus}\n" +
            $"Hello raw state:  {rawState}\n" +
            $"Elevated (UAC):   {elevated}\n" +
            $"TOTP:             {totp}\n" +
            $"Master password:  {(TwoFactorStore.HasMasterPassword(cfg) ? "set" : "not set")}\n" +
            $"Remember device:  {remember}\n" +
            $"Startup lock:     {(cfg.RequireOnStartupOrPair ? "on" : "off")}" + throttle;
    }

    private void OnToggleAllowPasskey(object sender, RoutedEventArgs e)
    {
        if (AboutOverlay.Visibility != Visibility.Visible) return;
        var cfg = TwoFactorStore.LoadController() with { AllowPasskey = AllowPasskeyCheck.IsChecked == true };
        TwoFactorStore.SaveController(cfg);
        _ = RefreshSecuritySectionAsync();
    }

    private void OnRememberDaysLostFocus(object sender, RoutedEventArgs e)
    {
        if (AboutOverlay.Visibility != Visibility.Visible) return;
        if (!int.TryParse(RememberDaysBox.Text, out var days)) days = 30;
        days = Math.Clamp(days, 1, 365);
        RememberDaysBox.Text = days.ToString();
        var cfg = TwoFactorStore.LoadController();
        if (cfg.RememberDays == days) return;
        TwoFactorStore.SaveController(cfg with { RememberDays = days });
        _ = RefreshSecuritySectionAsync();
    }

    private void OnForgetDeviceClick(object sender, RoutedEventArgs e)
    {
        TwoFactorStore.SaveController(TwoFactorStore.ForgetDevice(TwoFactorStore.LoadController(), LocalDeviceId));
        StatusText.Text = "This device is no longer remembered; the startup lock applies again.";
        _ = RefreshSecuritySectionAsync();
    }

    private async void OnTestWindowsHelloClick(object sender, RoutedEventArgs e)
    {
        TestHelloButton.IsEnabled = false;
        TestHelloResultText.Visibility = Visibility.Visible;
        TestHelloResultText.Text = "Waiting for Windows Hello...";
        try
        {
            var result = await WindowsHelloService.VerifyAsync(this, "Test Windows Hello for Home RMS");
            TestHelloResultText.Text = result.IsVerified
                ? "Test passed: Windows Hello verified your identity."
                : $"Test result: {result.Message}";
        }
        finally
        {
            TestHelloButton.IsEnabled = true;
        }
    }

    private async void OnSetMasterPasswordClick(object sender, RoutedEventArgs e)
    {
        if (_aboutSecurityBusy) return;
        var newPassword = NewMasterPasswordBox.Password;
        if (newPassword.Length < TwoFactorStore.MasterPasswordMinLength)
        {
            ShowMasterPasswordStatus($"The master password must be at least {TwoFactorStore.MasterPasswordMinLength} characters.", warn: true);
            return;
        }
        if (!string.Equals(newPassword, ConfirmMasterPasswordBox.Password, StringComparison.Ordinal))
        {
            ShowMasterPasswordStatus("The new password and its confirmation do not match.", warn: true);
            return;
        }

        await RunAboutSecurityActionAsync(async () =>
        {
            var (verified, message) = await VerifyCurrentFactorAsync(CurrentFactorBox.Password);
            if (!verified)
            {
                ShowMasterPasswordStatus(message, warn: true);
                return;
            }
            ShowMasterPasswordStatus("Saving password...", warn: false);
            var hadPassword = TwoFactorStore.HasMasterPassword(TwoFactorStore.LoadController());
            var updated = await Task.Run(() => TwoFactorStore.SetMasterPassword(TwoFactorStore.LoadController(), newPassword));
            TwoFactorStore.SaveController(updated);
            TwoFactorStore.LogSecurityEvent(hadPassword ? "Master password changed." : "Master password set.");
            ClearAboutSecretInputs();
            ShowMasterPasswordStatus(hadPassword ? "Master password changed." : "Master password set. It now appears as an unlock option.", warn: false);
        });
    }

    private async void OnClearMasterPasswordClick(object sender, RoutedEventArgs e)
    {
        if (_aboutSecurityBusy) return;
        if (!TwoFactorStore.HasMasterPassword(TwoFactorStore.LoadController()))
        {
            ShowMasterPasswordStatus("No master password is set.", warn: false);
            return;
        }

        await RunAboutSecurityActionAsync(async () =>
        {
            var (verified, message) = await VerifyCurrentFactorAsync(CurrentFactorBox.Password);
            if (!verified)
            {
                ShowMasterPasswordStatus(message, warn: true);
                return;
            }
            TwoFactorStore.SaveController(TwoFactorStore.ClearMasterPassword(TwoFactorStore.LoadController()));
            TwoFactorStore.LogSecurityEvent("Master password removed.");
            ClearAboutSecretInputs();
            ShowMasterPasswordStatus("Master password removed.", warn: false);
        });
    }

    private async Task RunAboutSecurityActionAsync(Func<Task> action)
    {
        _aboutSecurityBusy = true;
        SetMasterPasswordButton.IsEnabled = false;
        ClearMasterPasswordButton.IsEnabled = false;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            ShowMasterPasswordStatus($"Could not update the master password: {ex.Message}", warn: true);
        }
        finally
        {
            _aboutSecurityBusy = false;
            SetMasterPasswordButton.IsEnabled = true;
            await RefreshSecuritySectionAsync();
        }
    }

    /// <summary>
    /// Re-authenticates before a security change: current master password, TOTP / backup code,
    /// or Windows Hello when the field is left empty. Shares the unlock attempt throttle.
    /// </summary>
    private async Task<(bool Verified, string Message)> VerifyCurrentFactorAsync(string input)
    {
        var cfg = TwoFactorStore.LoadController();
        if (string.IsNullOrEmpty(input))
        {
            if (!cfg.AllowPasskey)
                return (false, "Enter your current master password, authenticator code or backup code.");
            var hello = await WindowsHelloService.VerifyAsync(this, "Verify your identity to change the Home RMS master password");
            return hello.IsVerified
                ? (true, "")
                : (false, $"{hello.Message} Enter your current password or an authenticator code instead.");
        }

        var wait = TwoFactorStore.GetUnlockDelayRemaining(cfg);
        if (wait > TimeSpan.Zero)
            return (false, $"Too many failed attempts. Try again in {FormatWait(wait)}.");

        if (TwoFactorStore.HasMasterPassword(cfg) &&
            await Task.Run(() => TwoFactorStore.VerifyMasterPassword(cfg, input)))
        {
            TwoFactorStore.SaveController(TwoFactorStore.RegisterUnlockSuccess(TwoFactorStore.LoadController()));
            return (true, "");
        }

        cfg = TwoFactorStore.LoadController();
        if (TwoFactorStore.VerifyCodeOrBackup(cfg, input, out var updated, out _))
        {
            TwoFactorStore.SaveController(TwoFactorStore.RegisterUnlockSuccess(updated));
            return (true, "");
        }

        var failed = TwoFactorStore.RegisterUnlockFailure(cfg);
        TwoFactorStore.SaveController(failed);
        TwoFactorStore.LogSecurityEvent($"Re-authentication for a security change failed; consecutive failures: {failed.FailedUnlockAttempts}");
        CurrentFactorBox.Clear();
        return (false, "The current password or code is incorrect.");
    }

    private void ShowMasterPasswordStatus(string message, bool warn)
    {
        MasterPasswordStatusText.Text = message;
        MasterPasswordStatusText.Foreground = (Brush)FindResource(warn ? "WarnBrush" : "MutedBrush");
        MasterPasswordStatusText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }
}
