using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using HomeRms.Controller.Services;
using HomeRms.Controller.Themes;

namespace HomeRms.Controller;

public partial class App : Application
{
    private bool _errorDialogOpen;

    /// <summary>%LocalAppData%\HomeRMS\logs\controller-errors.log (full stack traces of unexpected errors).</summary>
    public static string ErrorLogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HomeRMS", "logs", "controller-errors.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Themes.Palette.Warm();

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            args.SetObserved();
            Report("Background task", args.Exception, showDialog: false);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) WriteErrorLog("Unhandled (fatal)", ex);
        };
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        Report("UI", e.Exception, showDialog: true);
    }

    private void Report(string where, Exception ex, bool showDialog)
    {
        WriteErrorLog(where, ex);
        var root = ex is AggregateException { InnerExceptions.Count: 1 } agg ? agg.InnerException! : ex;
        LogBus.Publish(LogBus.Alerts, "Error", $"{where}: {root.GetType().Name}: {root.Message}", PaletteTone.Danger);

        // Never stack dialogs: a timer that keeps failing would otherwise open one per tick.
        if (!showDialog || _errorDialogOpen) return;
        _errorDialogOpen = true;
        try
        {
            MessageBox.Show(
                $"An unexpected error occurred:\n\n{root.Message}\n\nDetails were written to:\n{ErrorLogPath}",
                "Home RMS — Warning",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            _errorDialogOpen = false;
        }
    }

    private static void WriteErrorLog(string where, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ErrorLogPath)!);
            var info = new FileInfo(ErrorLogPath);
            if (info.Exists && info.Length > 2 * 1024 * 1024) // keep the log bounded
                File.Move(ErrorLogPath, ErrorLogPath + ".old", overwrite: true);
            File.AppendAllText(ErrorLogPath, $"{DateTimeOffset.Now:O} [{where}] {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Error logging must never throw from an exception handler.
        }
    }
}
