using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;
using Orbit.Shell.Services;

namespace Orbit.Shell;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        ShellFaultLog.EnsureInitialized();
        RegisterUnhandledFaultLogging();
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            try { Serilog.Log.CloseAndFlush(); } catch { /* ignore */ }
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();

    /// <summary>
    /// async void / dispatcher faults otherwise vanish — leave a breadcrumb under
    /// %LOCALAPPDATA%/Orbit/logs/shell-*.log (same folder as Runtime/Agent).
    /// </summary>
    private static void RegisterUnhandledFaultLogging()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                ShellFaultLog.Fatal(ex, $"AppDomain.UnhandledException (isTerminating={e.IsTerminating})");
            }
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            ShellFaultLog.Fatal(e.Exception, "TaskScheduler.UnobservedTaskException");
            e.SetObserved();
        };

        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            ShellFaultLog.Fatal(e.Exception, "Dispatcher.UIThread.UnhandledException");
            e.Handled = true;
        };
    }
}
