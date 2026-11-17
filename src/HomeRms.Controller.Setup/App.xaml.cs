using System.Windows;

namespace HomeRms.Controller.Setup;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Any(a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase)))
        {
            var silent = e.Args.Any(a => a.Equals("--silent", StringComparison.OrdinalIgnoreCase) || a.Equals("/S", StringComparison.OrdinalIgnoreCase));
            SetupEngine.Uninstall(silent);
            Shutdown(0);
            return;
        }

        if (e.Args.Any(a => a.Equals("--silent", StringComparison.OrdinalIgnoreCase) || a.Equals("/S", StringComparison.OrdinalIgnoreCase)))
        {
            SetupEngine.Install(SetupEngine.DefaultInstallDir, createDesktopShortcut: true);
            Shutdown(0);
            return;
        }

        var win = new SetupWindow();
        win.Show();
        base.OnStartup(e);
    }
}
