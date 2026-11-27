using System.Text;
using Microsoft.UI.Xaml;

namespace Tonewise.Desktop;

public partial class App : Application
{
    private static readonly object CrashLogLock = new();
    private const long MaxCrashLogBytes = 512 * 1024;
    private Window? _window;

    internal static string InitialAppTheme { get; private set; } = "dark";

    public App()
    {
        try
        {
            var saved = Tonewise.SyncHost.Workbench.DesktopSettingsStore.Load();
            InitialAppTheme = saved.AppTheme is "light" or "dark" or "system" ? saved.AppTheme : "dark";
            if (InitialAppTheme == "light")
            {
                RequestedTheme = ApplicationTheme.Light;
            }
            else if (InitialAppTheme == "dark")
            {
                RequestedTheme = ApplicationTheme.Dark;
            }
        }
        catch
        {
            InitialAppTheme = "dark";
        }

        InitializeComponent();
        UnhandledException += OnAppUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }

    private static void OnAppUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        WriteCrashLog(
            string.IsNullOrWhiteSpace(e.Message)
                ? "WinUI.UnhandledException"
                : $"WinUI.UnhandledException ({e.Message})",
            e.Exception);
        if (e.Exception is not OutOfMemoryException and not AccessViolationException)
        {
            e.Handled = true;
        }
    }

    private static void OnDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
    {
        WriteCrashLog($"AppDomain.UnhandledException(Terminating={e.IsTerminating})", e.ExceptionObject as Exception, e.ExceptionObject?.ToString());
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        WriteCrashLog("TaskScheduler.UnobservedTaskException", e.Exception);
        e.SetObserved();
    }

    internal static void WriteCrashLog(string source, Exception? ex, string? fallbackText = null)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Tonewise");
            Directory.CreateDirectory(dir);
            string logPath = Path.Combine(dir, "crash.log");

            var sb = new StringBuilder();
            sb.Append('[').Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz")).Append("] ");
            sb.Append(source).Append(": ");
            if (ex is not null)
            {
                sb.AppendLine($"{ex.GetType().FullName} (HResult=0x{ex.HResult:X8}): {ex.Message}");
                if (!string.IsNullOrWhiteSpace(ex.StackTrace))
                {
                    sb.AppendLine(ex.StackTrace);
                }
                if (ex.InnerException is not null)
                {
                    sb.AppendLine($"  --> {ex.InnerException.GetType().FullName}: {ex.InnerException.Message}");
                    if (!string.IsNullOrWhiteSpace(ex.InnerException.StackTrace))
                    {
                        sb.AppendLine(ex.InnerException.StackTrace);
                    }
                }
            }
            else
            {
                sb.AppendLine(fallbackText ?? "(null exception)");
            }
            sb.AppendLine(new string('-', 60));

            lock (CrashLogLock)
            {
                if (File.Exists(logPath) && new FileInfo(logPath).Length >= MaxCrashLogBytes)
                {
                    string prevPath = Path.Combine(dir, "crash.prev.log");
                    File.Move(logPath, prevPath, overwrite: true);
                }
                File.AppendAllText(logPath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
        }
        catch
        {
            // Never throw from crash diagnostic logger
        }
    }
}
