using Avalonia;
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
            FFmpegRuntime.Initialize();
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

    internal static void WriteCrashLog(string title, Exception? exception)
    {
        var sb = new StringBuilder();
        sb.AppendLine("AssetStudio Avalonia crash report");
        sb.AppendLine($"Created at: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine(title);
        sb.AppendLine(exception?.ToString() ?? "No managed exception object was available.");
        sb.AppendLine(new string('=', 80));
        var content = sb.ToString();

        try
        {
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "crash.log"), content);
            return;
        }
        catch
        {
        }

        try
        {
            var fallbackDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AssetStudio");
            Directory.CreateDirectory(fallbackDir);
            File.AppendAllText(Path.Combine(fallbackDir, "crash.log"), content);
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
