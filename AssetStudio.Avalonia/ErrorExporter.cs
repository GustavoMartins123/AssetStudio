using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace AssetStudio.Avalonia
{
    internal static class ErrorExporter
    {
        internal static async Task ExportErrorLog(Window window, GUILogger logger, Action<string> statusUpdate)
        {
            var loadErrors = logger.GetMessages(LoggerEvent.Error);
            if (loadErrors.Length == 0)
            {
                statusUpdate("No errors logged to export.");
                return;
            }

            var topLevel = TopLevel.GetTopLevel(window);
            if (topLevel == null) return;

            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save error log",
                SuggestedFileName = "errors.txt",
                DefaultExtension = "txt",
                ShowOverwritePrompt = true
            });

            if (file == null) return;

            var errorReportPath = file.Path.LocalPath;

            statusUpdate("Exporting error log...");

            await Task.Run(() =>
            {
                try
                {
                    using (var writer = new StreamWriter(errorReportPath, false, Encoding.UTF8))
                    {
                        writer.WriteLine("AssetStudio error report");
                        writer.WriteLine($"Created at: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                        writer.WriteLine();

                        writer.WriteLine($"Logged errors ({loadErrors.Length})");
                        writer.WriteLine(new string('=', 80));
                        for (int i = 0; i < loadErrors.Length; i++)
                        {
                            writer.WriteLine($"[{i + 1}]");
                            writer.WriteLine(loadErrors[i]);
                            writer.WriteLine();
                        }
                    }

                    global::Avalonia.Threading.Dispatcher.UIThread.Post(() => statusUpdate($"Error log saved to {Path.GetFileName(errorReportPath)}."));
                }
                catch (Exception ex)
                {
                    global::Avalonia.Threading.Dispatcher.UIThread.Post(() => statusUpdate($"Failed to save error log: {ex.Message}"));
                }
            });
        }
    }
}
