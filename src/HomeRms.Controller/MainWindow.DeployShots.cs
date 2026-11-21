using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using HomeRms.Controller.Views.Deploy;

namespace HomeRms.Controller;

public partial class MainWindow
{
    /// <summary>
    /// Screenshot mode (--screenshot-dir): Discover, Apps, app page, Packs, Essentials, Install (3 apps selected,
    /// so the selection bar shows) with its Deploy and PowerShell tabs, the expanded selection list, Files, and a
    /// narrow Apps grid. Nothing is persisted: the selection is in memory only.
    /// </summary>
    private async Task CaptureDeployScreenshotsAsync(string outDir)
    {
        static async Task Settle(Task t, int seconds = 25)
        {
            try { await t.WaitAsync(TimeSpan.FromSeconds(seconds)); } catch { }
        }
        void Shot(string name) => SaveWindowPng(Path.Combine(outDir, $"deploy-{name}.png"));

        NavDeploy.IsChecked = true;
        await Task.Delay(400);
        await Settle(_discoverTask);
        await Task.Delay(2500); // icons
        DeployShell.Show(DeployView.Discover);
        await Task.Delay(500);
        Shot("discover");

        DeployShell.Show(DeployView.Apps);
        await Task.Delay(200);
        await Settle(_appsTask);
        await Task.Delay(2500);
        Shot("apps");

        var app = _appsResults.FirstOrDefault(a => a.Id == "Google.Chrome") ?? _trendingApps.FirstOrDefault();
        if (app is not null)
        {
            OpenAppPage(app);
            await Settle(_detailTask);
            await Task.Delay(900);
            Shot("app-page");
        }

        AddIdsToQueue(["Google.Chrome", "7zip.7zip", "Microsoft.VisualStudioCode"], null);
        DeployShell.Show(DeployView.Packs);
        await Task.Delay(200);
        await Settle(_packsTask);
        await Task.Delay(2500);
        Shot("packs");

        DeployShell.Show(DeployView.Essentials);
        await Task.Delay(1200);
        Shot("essentials");

        DeployShell.Show(DeployView.Install);
        await Task.Delay(3000); // version lists from api.winstall.app
        Shot("install");
        InstallV.Scroll.ScrollToEnd();
        await Task.Delay(600);
        Shot("install-apps");
        InstallV.Scroll.ScrollToTop();
        InstallV.SelectTab(InstallTab.PowerShell);
        await Task.Delay(600);
        Shot("install-powershell");
        InstallV.SelectTab(InstallTab.Deploy);
        ShowUpdateAllDemoRows(); // MainWindow.UpdateAll.cs: sample rows only, nothing runs
        await Task.Delay(600);
        Shot("install-update-all");
        _upgradeRows.Clear();
        ShowUpgradeQueue(false);

        DeployShell.Show(DeployView.Apps);
        await Task.Delay(500);
        BarV.ListPanel.Visibility = Visibility.Visible;
        await Task.Delay(500);
        Shot("selection-list");
        BarV.CloseFlyouts();

        DeployShell.Show(DeployView.Files);
        await Task.Delay(600);
        Shot("files");

        var (w, h) = (Width, Height);
        Width = MinWidth;
        DeployShell.Show(DeployView.Discover);
        await Task.Delay(900);
        Shot("discover-narrow");
        DeployShell.Show(DeployView.Apps);
        await Task.Delay(900);
        Shot("apps-narrow");
        (Width, Height) = (w, h);
        await Task.Delay(300);
    }
}
