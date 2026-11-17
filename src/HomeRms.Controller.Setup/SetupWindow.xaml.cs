using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using Microsoft.Win32;

namespace HomeRms.Controller.Setup;

public partial class SetupWindow : Window
{
    public SetupWindow()
    {
        InitializeComponent();
        var ver = Assembly.GetExecutingAssembly().GetName().Version;
        VersionSubtitle.Text = $"v{ver?.Major}.{ver?.Minor}.{ver?.Build} • Fleet Command Center • GitHub Auto-Update Ready";
        InstallPathBox.Text = SetupEngine.DefaultInstallDir;
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Choose Install Folder",
            InitialDirectory = InstallPathBox.Text
        };
        if (dlg.ShowDialog(this) == true)
            InstallPathBox.Text = dlg.FolderName;
    }

    private async void OnInstallClick(object sender, RoutedEventArgs e)
    {
        var dir = InstallPathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(dir)) return;

        InstallBtn.IsEnabled = false;
        Progress.Value = 25;
        StatusLabel.Text = "Extracting HomeRmsController.exe…";

        try
        {
            var desktop = DesktopShortcutCheck.IsChecked == true;
            var startMenu = StartMenuCheck.IsChecked == true;
            var exePath = await Task.Run(() => SetupEngine.Install(dir, desktop, startMenu));

            Progress.Value = 100;
            StatusLabel.Text = "Installation complete!";
            StatusLabel.Foreground = System.Windows.Media.Brushes.LightGreen;

            if (LaunchAfterCheck.IsChecked == true && File.Exists(exePath))
                Process.Start(new ProcessStartInfo { FileName = exePath, UseShellExecute = true });

            Close();
        }
        catch (Exception ex)
        {
            Progress.Value = 0;
            StatusLabel.Text = "Install failed: " + ex.Message;
            StatusLabel.Foreground = System.Windows.Media.Brushes.IndianRed;
            InstallBtn.IsEnabled = true;
        }
    }

    private void OnUninstallClick(object sender, RoutedEventArgs e)
    {
        SetupEngine.Uninstall(silent: false);
        StatusLabel.Text = "Uninstalled Home RMS Controller.";
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();
}

internal static class SetupEngine
{
    public static string DefaultInstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "HomeRMS", "Controller");

    private const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\HomeRmsController";

    public static string Install(string installDir, bool createDesktopShortcut = true, bool createStartMenu = true)
    {
        // Stop any running instance so the exe can be replaced cleanly
        foreach (var p in Process.GetProcessesByName("HomeRmsController"))
        {
            try { p.Kill(); p.WaitForExit(3000); } catch { }
        }

        Directory.CreateDirectory(installDir);
        var targetExe = Path.Combine(installDir, "HomeRmsController.exe");

        using (var resStream = typeof(SetupEngine).Assembly.GetManifestResourceStream("HomeRmsController.exe")
               ?? throw new InvalidOperationException("Embedded HomeRmsController.exe payload is missing from setup."))
        using (var fs = File.Create(targetExe))
        {
            resStream.CopyTo(fs);
        }

        // Copy setup binary itself as uninstaller
        var selfPath = Environment.ProcessPath;
        var uninstallerPath = Path.Combine(installDir, "Uninstall.exe");
        if (!string.IsNullOrEmpty(selfPath) && File.Exists(selfPath) &&
            !string.Equals(selfPath, uninstallerPath, StringComparison.OrdinalIgnoreCase))
        {
            try { File.Copy(selfPath, uninstallerPath, overwrite: true); } catch { }
        }

        if (createDesktopShortcut)
        {
            var desktopLnk = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                "Home RMS Controller.lnk");
            CreateShortcut(desktopLnk, targetExe, installDir, "Home RMS Fleet Command Center");
        }

        if (createStartMenu)
        {
            var startLnk = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                "Programs", "Home RMS Controller.lnk");
            Directory.CreateDirectory(Path.GetDirectoryName(startLnk)!);
            CreateShortcut(startLnk, targetExe, installDir, "Home RMS Fleet Command Center");

            var ver = Assembly.GetExecutingAssembly().GetName().Version;
            var verStr = ver is not null ? $"{ver.Major}.{ver.Minor}.{ver.Build}" : "1.0.0";
            var sizeKb = (int)(new FileInfo(targetExe).Length / 1024);

            using var key = Registry.CurrentUser.CreateSubKey(UninstallKeyPath);
            key.SetValue("DisplayName", "Home RMS Controller");
            key.SetValue("DisplayVersion", verStr);
            key.SetValue("Publisher", "Home RMS (dor2500)");
            key.SetValue("DisplayIcon", targetExe);
            key.SetValue("InstallLocation", installDir);
            key.SetValue("UninstallString", $"\"{uninstallerPath}\" --uninstall");
            key.SetValue("QuietUninstallString", $"\"{uninstallerPath}\" --uninstall --silent");
            key.SetValue("EstimatedSize", sizeKb, RegistryValueKind.DWord);
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }

        return targetExe;
    }

    public static void Uninstall(bool silent)
    {
        if (!silent && MessageBox.Show("Uninstall Home RMS Controller from this computer?",
                "Home RMS — Uninstall", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        foreach (var p in Process.GetProcessesByName("HomeRmsController"))
        {
            try { p.Kill(); p.WaitForExit(3000); } catch { }
        }

        string? installDir = null;
        using (var key = Registry.CurrentUser.OpenSubKey(UninstallKeyPath))
        {
            installDir = key?.GetValue("InstallLocation") as string;
        }
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKeyPath, throwOnMissingSubKey: false);

        TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Home RMS Controller.lnk"));
        TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "Home RMS Controller.lnk"));

        installDir ??= DefaultInstallDir;
        var exe = Path.Combine(installDir, "HomeRmsController.exe");
        TryDelete(exe);

        if (!silent)
            MessageBox.Show("Home RMS Controller has been uninstalled.", "Home RMS", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void CreateShortcut(string shortcutPath, string targetPath, string workingDir, string description)
    {
        var ps = $@"
$ws = New-Object -ComObject WScript.Shell
$s = $ws.CreateShortcut('{shortcutPath.Replace("'", "''")}')
$s.TargetPath = '{targetPath.Replace("'", "''")}'
$s.WorkingDirectory = '{workingDir.Replace("'", "''")}'
$s.IconLocation = '{targetPath.Replace("'", "''")},0'
$s.Description = '{description.Replace("'", "''")}'
$s.Save()
";
        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoLogo -NoProfile -WindowStyle Hidden -Command \"{ps.Replace("\"", "\\\"")}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        });
        proc?.WaitForExit(5000);
    }
}
