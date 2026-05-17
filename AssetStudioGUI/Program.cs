using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AssetStudioGUI
{
    static class Program
    {
        private static string logPath;
        private static string localLogPath;
        private static bool closedNormally;

        /// <summary>
        ///  The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {
            logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "session.log");
            localLogPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AssetStudio",
                "session.log");
            WriteSessionLog("Started");

            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (sender, e) => WriteCrashLog("UI thread exception", e.Exception);
            Application.ApplicationExit += (sender, e) =>
            {
                closedNormally = true;
                WriteSessionLog("ApplicationExit");
            };
            AppDomain.CurrentDomain.ProcessExit += (sender, e) =>
            {
                WriteSessionLog(closedNormally ? "ProcessExit after normal close" : "ProcessExit without ApplicationExit");
            };
            AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
            {
                WriteCrashLog("Unhandled exception", e.ExceptionObject as Exception);
                WriteSessionLog("Unhandled exception");
            };
            TaskScheduler.UnobservedTaskException += (sender, e) =>
            {
                WriteCrashLog("Unobserved task exception", e.Exception);
                WriteSessionLog("Unobserved task exception");
                e.SetObserved();
            };

#if !NETFRAMEWORK
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
#endif
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                Application.Run(new AssetStudioGUIForm());
            }
            catch (Exception ex)
            {
                WriteCrashLog("Application.Run exception", ex);
                WriteSessionLog("Application.Run exception");
                throw;
            }
        }

        private static void WriteCrashLog(string title, Exception exception)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("AssetStudio crash report");
                sb.AppendLine($"Created at: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine(title);
                sb.AppendLine(exception?.ToString() ?? "No managed exception object was available.");
                sb.AppendLine(new string('=', 80));
                AppendAllTextSafe(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log"), sb.ToString());
                AppendAllTextSafe(Path.Combine(Path.GetDirectoryName(localLogPath), "crash.log"), sb.ToString());
            }
            catch
            {
                // Last-chance logging must never trigger a second crash.
            }
        }

        private static void WriteSessionLog(string message)
        {
            try
            {
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}";
                AppendAllTextSafe(logPath, line);
                AppendAllTextSafe(localLogPath, line);
            }
            catch
            {
                // Session logging is diagnostic only.
            }
        }

        private static void AppendAllTextSafe(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.AppendAllText(path, text, Encoding.UTF8);
        }
    }
}
