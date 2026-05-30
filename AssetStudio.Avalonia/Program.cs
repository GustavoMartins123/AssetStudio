using Avalonia;
using FFmpegVideoPlayer.Core;
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace AssetStudio.Avalonia;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        RegisterCrashHandlers();

        try
        {
            FFmpegInitializer.Initialize();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to initialize FFmpeg: {ex.Message}");
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            WriteCrashLog("Avalonia lifetime exception", ex);
            throw;
        }
    }

    private static void RegisterCrashHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            WriteCrashLog("Unhandled exception", e.ExceptionObject as Exception);
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteCrashLog("Unobserved task exception", e.Exception);
            e.SetObserved();
        };
    }

    private static void WriteCrashLog(string title, Exception? exception)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("AssetStudio Avalonia crash report");
            sb.AppendLine($"Created at: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine(title);
            sb.AppendLine(exception?.ToString() ?? "No managed exception object was available.");
            sb.AppendLine(new string('=', 80));
            File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log"), sb.ToString());
        }
        catch
        {
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
