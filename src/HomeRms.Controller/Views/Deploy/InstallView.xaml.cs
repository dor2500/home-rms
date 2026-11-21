using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using HomeRms.Controller.Services;

namespace HomeRms.Controller.Views.Deploy;

public enum InstallTab { Deploy, Batch, PowerShell, WingetImport }

/// <summary>
/// "Your apps are ready": Deploy to endpoints | Batch | PowerShell | Winget Import, Default Options, then the
/// selected apps with a version dropdown and Advanced Options each. MainWindow.Deploy.cs fills the scripts
/// and runs the deployment.
/// </summary>
public partial class InstallView : UserControl
{
    private bool _optionsOpen;

    public InstallView()
    {
        InitializeComponent();
        foreach (var rb in new[] { TabDeploy, TabBatch, TabPowerShell, TabImport })
            rb.Checked += (_, _) => { ApplyTab(); OptionsChanged?.Invoke(this, EventArgs.Empty); };
        foreach (var rb in new[] { ScopeDefault, ScopeUser, ScopeMachine })
            rb.Checked += (_, _) => RaiseOptions();
        foreach (var cb in new[] { SilentCheck, UpgradeCheck, InteractiveCheck, ForceCheck })
        {
            cb.Checked += (_, _) => RaiseOptions();
            cb.Unchecked += (_, _) => RaiseOptions();
        }
        OptionsToggleBtn.Click += (_, _) => SetOptionsOpen(!_optionsOpen);
        WingetLink.Click += (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://learn.microsoft.com/windows/package-manager/winget/") { UseShellExecute = true }); }
            catch { }
        };
        ApplyTab();
        UpdateSummary();
        OptionsPanel.Visibility = Visibility.Collapsed; // winstall: "Default Options" starts collapsed
    }

    /// <summary>Raised when a tab or a default option changes (scripts regenerate).</summary>
    public event EventHandler? OptionsChanged;

    public InstallTab ActiveTab =>
        TabBatch.IsChecked == true ? InstallTab.Batch
        : TabPowerShell.IsChecked == true ? InstallTab.PowerShell
        : TabImport.IsChecked == true ? InstallTab.WingetImport
        : InstallTab.Deploy;

    public void SelectTab(InstallTab tab)
    {
        (tab switch
        {
            InstallTab.Batch => TabBatch,
            InstallTab.PowerShell => TabPowerShell,
            InstallTab.WingetImport => TabImport,
            _ => TabDeploy,
        }).IsChecked = true;
    }

    public WingetScriptOptions Options => new(
        ScopeMachine.IsChecked == true ? InstallScope.Machine : ScopeUser.IsChecked == true ? InstallScope.User : InstallScope.Default,
        InteractiveCheck.IsChecked == true,
        ForceCheck.IsChecked == true,
        SilentCheck.IsChecked == true,
        UpgradeCheck.IsChecked == true);

    private void RaiseOptions()
    {
        UpdateSummary();
        OptionsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateSummary()
    {
        if (OptionsSummary is null) return;
        var o = Options;
        var parts = new System.Collections.Generic.List<string> { $"scope: {o.Scope.ToString().ToLowerInvariant()}" };
        if (o.Silent) parts.Add("silent");
        if (o.UpgradeIfInstalled) parts.Add("upgrade");
        if (o.Interactive) parts.Add("interactive");
        if (o.Force) parts.Add("force");
        OptionsSummary.Text = string.Join(" · ", parts);
    }

    private void ApplyTab()
    {
        var deploy = ActiveTab == InstallTab.Deploy;
        DeployTabPanel.Visibility = deploy ? Visibility.Visible : Visibility.Collapsed;
        ScriptPanel.Visibility = deploy ? Visibility.Collapsed : Visibility.Visible;
        DownloadScriptText.Text = ActiveTab switch
        {
            InstallTab.PowerShell => "Download .ps1",
            InstallTab.WingetImport => "Download .json",
            _ => "Download .bat",
        };
        CopyScriptText.Text = "Copy to clipboard";
    }

    public void SetOptionsOpen(bool open)
    {
        _optionsOpen = open;
        OptionsPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        ChevronRotate.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty,
            new DoubleAnimation(open ? 180 : 0, TimeSpan.FromMilliseconds(150)));
    }
}
