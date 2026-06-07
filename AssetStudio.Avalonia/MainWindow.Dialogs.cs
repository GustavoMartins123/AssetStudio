using Avalonia.Controls;
using Avalonia.Platform.Storage;
using AssetStudio;
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AssetStudio.Avalonia;

public partial class MainWindow
{
    private async Task<IStorageFolder?> TryGetFolder(string? path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
        {
            return null;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null)
        {
            return null;
        }

        try
        {
            var absolutePath = Path.GetFullPath(path).Replace('\\', '/');
            if (!absolutePath.StartsWith("/"))
            {
                absolutePath = "/" + absolutePath;
            }
            var uri = new Uri("file://" + absolutePath);
            return await topLevel.StorageProvider.TryGetFolderFromPathAsync(uri);
        }
        catch
        {
            return null;
        }
    }

    private async Task<FilePickerOpenOptions> CreateOpenFileOptions(string title, bool allowMultiple)
    {
        return new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = allowMultiple,
            SuggestedStartLocation = await TryGetFolder(appSettings.LoadFolderPath)
        };
    }

    private async Task<FolderPickerOpenOptions> CreateLoadFolderOptions(string title, bool allowMultiple = false)
    {
        return new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = allowMultiple,
            SuggestedStartLocation = await TryGetFolder(appSettings.LoadFolderPath)
        };
    }

    private async Task<FolderPickerOpenOptions> CreateExportFolderOptions(string title, bool allowMultiple = false)
    {
        return new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = allowMultiple,
            SuggestedStartLocation = await TryGetFolder(appSettings.ExportFolderPath)
        };
    }

    private async Task<FilePickerSaveOptions> CreateFbxSaveOptions(string title, string suggestedFileName)
    {
        return new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedFileName,
            DefaultExtension = "fbx",
            SuggestedStartLocation = await TryGetFolder(appSettings.ExportFolderPath),
            FileTypeChoices = new[]
            {
                new FilePickerFileType("Fbx file")
                {
                    Patterns = new[] { "*.fbx" }
                }
            }
        };
    }

    private void SaveLoadFolder(string path)
    {
        var folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
        appSettings.LoadFolderPath = folder;
        SaveAppSettings();
    }

    private void SaveExportFolder(string path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
        appSettings.ExportFolderPath = path;
        SaveAppSettings();
    }

    private async Task<RiskyLoadChoice> ConfirmFolderLoadIfRisky(string folderPath)
    {
        StatusStripUpdate("Scanning folder...");
        ProjectScanResult scanResult;
        using var scanCts = new CancellationTokenSource();
        var scanProgress = new Progress<ScanProgress>(p =>
        {
            if (p.TotalFiles > 0)
            {
                StatusStripUpdate($"Scanning folder... {p.ScannedFiles:N0}/{p.TotalFiles:N0} files ({FormatBytes(p.ScannedBytes)})");
            }
            else
            {
                StatusStripUpdate($"Scanning folder... {p.ScannedFiles:N0} files ({FormatBytes(p.ScannedBytes)})");
            }
        });
        try
        {
            scanResult = await Task.Run(() => ProjectScanner.ScanFolder(folderPath, scanCts.Token, scanProgress));
        }
        catch (OperationCanceledException)
        {
            StatusStripUpdate("Folder scan cancelled.");
            return RiskyLoadChoice.Cancel;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Unable to scan folder before loading:\n{ex.Message}", "Folder scan failed");
            return RiskyLoadChoice.EagerLoad;
        }

        StatusStripUpdate($"Scan complete: {scanResult.TotalFiles:N0} files, {FormatBytes(scanResult.TotalBytes)}, {scanResult.UnityBundleCount:N0} bundles.");
        currentScanResult = scanResult;

        if (!scanResult.IsRisky)
        {
            return RiskyLoadChoice.EagerLoad;
        }

        var message = BuildRiskyProjectMessage(scanResult);
        return await ShowRiskyProjectDialog(message);
    }

    private static string BuildRiskyProjectMessage(ProjectScanResult scanResult)
    {
        var sb = new StringBuilder();
        sb.AppendLine("This folder contains a very large number of Unity bundles.");
        sb.AppendLine();
        sb.AppendLine($"Files: {scanResult.TotalFiles:N0}");
        sb.AppendLine($"Size on disk: {FormatBytes(scanResult.TotalBytes)}");
        sb.AppendLine($"Unity bundles: {scanResult.UnityBundleCount:N0}");
        sb.AppendLine($"Serialized files: {scanResult.SerializedFileCount:N0}");
        sb.AppendLine($"Resource files: {scanResult.ResourceFileCount:N0}");
        if (scanResult.ErrorCount > 0)
        {
            sb.AppendLine($"Scan errors: {scanResult.ErrorCount:N0}");
        }
        sb.AppendLine();
        sb.AppendLine($"Estimated RAM to load: {FormatBytes(scanResult.EstimatedMemoryBytes)}");
        if (scanResult.AvailableMemoryBytes > 0)
        {
            sb.AppendLine($"Available RAM: {FormatBytes(scanResult.AvailableMemoryBytes)}");
        }
        if (scanResult.IsMemoryRisky)
        {
            sb.AppendLine();
            sb.AppendLine("⚠ The estimated memory exceeds available RAM. Loading may freeze the system or trigger the OOM killer.");
        }
        sb.AppendLine();
        sb.AppendLine("Loading all bundles at once can use far more memory than the project size on disk and may push Linux into swap.");
        sb.AppendLine("The safer alternative is Safe/Lazy Mode, which index-scans all files and only materializes assets on demand.");
        return sb.ToString();
    }

    private async Task<RiskyLoadChoice> ShowRiskyProjectDialog(string message)
    {
        var dialog = new Window
        {
            Title = "Large Unity project detected",
            Width = 640,
            Height = 440,
            MinWidth = 540,
            MinHeight = 340,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto"),
            Margin = new global::Avalonia.Thickness(16),
            RowSpacing = 12
        };

        var textBlock = new TextBlock
        {
            Text = message,
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap
        };

        var scrollViewer = new ScrollViewer
        {
            Content = textBlock
        };

        var buttonPanel = new StackPanel
        {
            Orientation = global::Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 10
        };

        var cancelButton = new Button
        {
            Content = "Cancel",
            MinWidth = 90
        };
        cancelButton.Click += (_, _) => dialog.Close(RiskyLoadChoice.Cancel);

        var lazyButton = new Button
        {
            Content = "Load in Safe/Lazy Mode (Recommended)",
            MinWidth = 240,
            FontWeight = global::Avalonia.Media.FontWeight.Bold
        };
        lazyButton.Click += (_, _) => dialog.Close(RiskyLoadChoice.LazyLoad);

        var loadButton = new Button
        {
            Content = "Load anyway (Eager)",
            MinWidth = 150
        };
        loadButton.Click += (_, _) => dialog.Close(RiskyLoadChoice.EagerLoad);

        buttonPanel.Children.Add(cancelButton);
        buttonPanel.Children.Add(lazyButton);
        buttonPanel.Children.Add(loadButton);

        Grid.SetRow(scrollViewer, 0);
        Grid.SetRow(buttonPanel, 1);
        grid.Children.Add(scrollViewer);
        grid.Children.Add(buttonPanel);
        dialog.Content = grid;

        return await dialog.ShowDialog<RiskyLoadChoice>(this);
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.##} {units[unit]}";
    }

    private async Task<MemoryPressureResult> ShowMemoryPressureWarningDialog(string message)
    {
        var dialog = new Window
        {
            Title = "Memory pressure warning",
            Width = 600,
            Height = 240,
            MinWidth = 450,
            MinHeight = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto"),
            Margin = new global::Avalonia.Thickness(16),
            RowSpacing = 12
        };

        var textBlock = new TextBlock
        {
            Text = message,
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap
        };

        var scrollViewer = new ScrollViewer
        {
            Content = textBlock
        };

        var buttonPanel = new StackPanel
        {
            Orientation = global::Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 10
        };

        var cancelButton = new Button
        {
            Content = "Cancel loading",
            MinWidth = 120
        };
        cancelButton.Click += (_, _) => dialog.Close(MemoryPressureResult.Cancel);

        var stopButton = new Button
        {
            Content = "Stop and keep loaded",
            MinWidth = 150
        };
        stopButton.Click += (_, _) => dialog.Close(MemoryPressureResult.StopAndKeep);

        var continueButton = new Button
        {
            Content = "Ignore and continue",
            MinWidth = 150,
            FontWeight = global::Avalonia.Media.FontWeight.Bold
        };
        continueButton.Click += (_, _) => dialog.Close(MemoryPressureResult.Continue);

        buttonPanel.Children.Add(cancelButton);
        buttonPanel.Children.Add(stopButton);
        buttonPanel.Children.Add(continueButton);

        Grid.SetRow(scrollViewer, 0);
        Grid.SetRow(buttonPanel, 1);
        grid.Children.Add(scrollViewer);
        grid.Children.Add(buttonPanel);
        dialog.Content = grid;

        return await dialog.ShowDialog<MemoryPressureResult>(this);
    }

    private void ShowMemoryPressureError(MemoryPressureException ex)
    {
        var msg = $"Loading was stopped because system memory usage reached {ex.MemoryLoadPercent}% (limit: {ex.LimitPercent}%).\n\n" +
                  $"Operation: {ex.Operation}\n\n" +
                  "Options:\n" +
                  "• Load fewer bundles at a time\n" +
                  "• Close other applications to free RAM\n" +
                  "• Raise the limit with ASSETSTUDIO_MEMORY_LIMIT_PERCENT (current: " + ex.LimitPercent + ")";
        StatusStripUpdate($"Loading stopped: memory pressure at {ex.MemoryLoadPercent}%.");
        MessageBox.Show(this, msg, "Memory pressure — loading stopped");
    }
}
