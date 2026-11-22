using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AssetStudio;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;

namespace AssetStudio.Avalonia.Services
{
    public sealed class AvaloniaDialogService : IDialogService
    {
        private readonly Func<Window?> _ownerProvider;

        public AvaloniaDialogService(Window? owner = null)
        {
            _ownerProvider = () => owner ?? ResolveActiveMainWindow();
        }

        public AvaloniaDialogService(Func<Window?> ownerProvider)
        {
            ArgumentNullException.ThrowIfNull(ownerProvider);
            _ownerProvider = () => ownerProvider() ?? ResolveActiveMainWindow();
        }

        public async Task<IReadOnlyList<string>> PickOpenFilesAsync(FilePickerRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);

            var topLevel = GetTopLevel();
            if (topLevel == null)
            {
                return Array.Empty<string>();
            }

            var options = new FilePickerOpenOptions
            {
                Title = request.Title,
                AllowMultiple = request.AllowMultiple,
                SuggestedStartLocation = await TryGetFolderAsync(topLevel, request.SuggestedStartDirectory),
                FileTypeFilter = BuildFileTypes(request.FileTypeFilters)
            };

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(options);
            if (files == null || files.Count == 0)
            {
                return Array.Empty<string>();
            }

            return files
                .Select(f => f.Path.LocalPath)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToArray();
        }

        public async Task<string?> PickOpenFolderAsync(FolderPickerRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);

            var topLevel = GetTopLevel();
            if (topLevel == null)
            {
                return null;
            }

            var options = new FolderPickerOpenOptions
            {
                Title = request.Title,
                AllowMultiple = request.AllowMultiple,
                SuggestedStartLocation = await TryGetFolderAsync(topLevel, request.SuggestedStartDirectory)
            };

            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(options);
            if (folders == null || folders.Count == 0)
            {
                return null;
            }

            return folders[0].Path.LocalPath;
        }

        public async Task<string?> PickSaveFileAsync(SaveFilePickerRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);

            var topLevel = GetTopLevel();
            if (topLevel == null)
            {
                return null;
            }

            var options = new FilePickerSaveOptions
            {
                Title = request.Title,
                SuggestedFileName = request.SuggestedFileName,
                DefaultExtension = request.DefaultExtension,
                ShowOverwritePrompt = request.ShowOverwritePrompt,
                SuggestedStartLocation = await TryGetFolderAsync(topLevel, request.SuggestedStartDirectory),
                FileTypeChoices = BuildFileTypes(request.FileTypeFilters)
            };

            var file = await topLevel.StorageProvider.SaveFilePickerAsync(options);
            return file?.Path.LocalPath;
        }

        public Task ShowMessageAsync(string title, string message)
        {
            var effectiveTitle = string.IsNullOrWhiteSpace(title) ? "Message" : title;
            MessageBox.Show(GetOwner(), message ?? string.Empty, effectiveTitle);
            return Task.CompletedTask;
        }

        public Task ShowErrorAsync(string title, string message)
        {
            var effectiveTitle = string.IsNullOrWhiteSpace(title) ? "Error" : title;
            MessageBox.Show(GetOwner(), message ?? string.Empty, effectiveTitle);
            return Task.CompletedTask;
        }

        public async Task<bool> ConfirmAsync(string title, string message)
        {
            var owner = GetOwner();
            if (owner == null || !owner.IsVisible)
            {
                return false;
            }

            return await MessageBox.ShowConfirmationAsync(
                owner: owner,
                text: message ?? string.Empty,
                title: string.IsNullOrWhiteSpace(title) ? "Confirm" : title);
        }

        public async Task<RiskyLoadChoice> ShowRiskyLoadDialogAsync(string title, string message)
        {
            var owner = GetOwner();
            if (owner == null || !owner.IsVisible)
            {
                return RiskyLoadChoice.Cancel;
            }

            return await MessageBox.ShowRiskyLoadAsync(
                owner: owner,
                message: message ?? string.Empty,
                title: string.IsNullOrWhiteSpace(title) ? "Large Unity project detected" : title);
        }

        public async Task<MemoryPressureResult> ShowMemoryPressureDialogAsync(string title, string message)
        {
            var owner = GetOwner();
            if (owner == null || !owner.IsVisible)
            {
                return MemoryPressureResult.Continue;
            }

            return await MessageBox.ShowMemoryPressureWarningAsync(
                owner: owner,
                message: message ?? string.Empty,
                title: string.IsNullOrWhiteSpace(title) ? "Memory pressure warning" : title);
        }

        public async Task<bool> ShowExportOptionsAsync(AvaloniaAppSettings settings)
        {
            ArgumentNullException.ThrowIfNull(settings);

            var owner = GetOwner();
            if (owner == null || !owner.IsVisible)
            {
                return false;
            }

            settings.ExportOptions ??= new ExportOptionsState();
            var dialog = new ExportOptionsWindow(settings.ExportOptions.Clone());
            var result = await dialog.ShowDialog<ExportOptionsState?>(owner);
            if (result == null)
            {
                return false;
            }

            settings.ExportOptions.CopyFrom(result);
            return true;
        }

        public async Task<ProjectManagerDialogResult?> ShowProjectManagerAsync(string? activeProjectPath)
        {
            var owner = GetOwner();
            var dialog = new ProjectManagerWindow();
            if (owner != null && owner.IsVisible)
            {
                return await dialog.ShowDialog<ProjectManagerDialogResult?>(owner);
            }

            dialog.Show();
            return string.IsNullOrWhiteSpace(activeProjectPath)
                ? null
                : new ProjectManagerDialogResult(projectPath: activeProjectPath);
        }

        public async Task<ViewModels.DecryptionKeyDialogResult?> ShowDecryptionKeyDialogAsync(ViewModels.DecryptionDialogContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            var owner = GetOwner();
            if (owner == null || !owner.IsVisible)
            {
                return null;
            }

            var vm = new ViewModels.DecryptionKeyDialogViewModel(context, this);
            var dialog = new DecryptionKeyDialog(vm);
            return await dialog.ShowDialog<ViewModels.DecryptionKeyDialogResult?>(owner);
        }

        public async Task<bool> CopyTextToClipboardAsync(string text)
        {
            var clipboard = GetTopLevel()?.Clipboard;
            if (clipboard == null)
            {
                return false;
            }

            await clipboard.SetTextAsync(text ?? string.Empty);
            return true;
        }

        public Task<bool> OpenPathInExplorerAsync(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return Task.FromResult(false);
            }

            if (Directory.Exists(path))
            {
                try
                {
                    if (OperatingSystem.IsWindows())
                    {
                        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                    }
                    else if (OperatingSystem.IsMacOS())
                    {
                        var startInfo = new ProcessStartInfo("open") { UseShellExecute = false };
                        startInfo.ArgumentList.Add(path);
                        Process.Start(startInfo);
                    }
                    else
                    {
                        var startInfo = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
                        startInfo.ArgumentList.Add(path);
                        Process.Start(startInfo);
                    }

                    return Task.FromResult(true);
                }
                catch
                {
                    return Task.FromResult(false);
                }
            }

            if (File.Exists(path))
            {
                return RevealFileInExplorerAsync(path);
            }

            return Task.FromResult(false);
        }

        public Task<bool> RevealFileInExplorerAsync(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || (!File.Exists(filePath) && !Directory.Exists(filePath)))
            {
                return Task.FromResult(false);
            }

            try
            {
                if (OperatingSystem.IsWindows())
                {
                    var cleanPath = filePath.Replace("\"", "");
                    if (cleanPath.EndsWith("\\", StringComparison.Ordinal))
                    {
                        cleanPath += ".";
                    }

                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{cleanPath}\"") { UseShellExecute = true });
                    return Task.FromResult(true);
                }

                if (OperatingSystem.IsMacOS())
                {
                    var startInfo = new ProcessStartInfo("open") { UseShellExecute = false };
                    startInfo.ArgumentList.Add("-R");
                    startInfo.ArgumentList.Add(filePath);
                    Process.Start(startInfo);
                    return Task.FromResult(true);
                }

                var folder = Directory.Exists(filePath) ? filePath : Path.GetDirectoryName(filePath);
                if (string.IsNullOrEmpty(folder))
                {
                    return Task.FromResult(false);
                }

                var linuxStartInfo = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
                linuxStartInfo.ArgumentList.Add(folder);
                Process.Start(linuxStartInfo);
                return Task.FromResult(true);
            }
            catch
            {
                return Task.FromResult(false);
            }
        }

        public Task<bool> OpenUrlAsync(string url)
        {
            if (string.IsNullOrWhiteSpace(url)
                || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return Task.FromResult(false);
            }

            try
            {
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
                return Task.FromResult(true);
            }
            catch
            {
                return Task.FromResult(false);
            }
        }

        private Window? GetOwner()
        {
            return _ownerProvider();
        }

        private TopLevel? GetTopLevel()
        {
            var owner = GetOwner();
            return owner != null ? TopLevel.GetTopLevel(owner) : null;
        }

        private static Window? ResolveActiveMainWindow()
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                return desktop.MainWindow;
            }

            return null;
        }

        private static async Task<IStorageFolder?> TryGetFolderAsync(TopLevel topLevel, string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                return null;
            }

            try
            {
                var absolutePath = Path.GetFullPath(path).Replace('\\', '/');
                if (!absolutePath.StartsWith("/", StringComparison.Ordinal))
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

        private static IReadOnlyList<FilePickerFileType>? BuildFileTypes(IReadOnlyList<FileTypeFilterSpec>? specs)
        {
            if (specs == null || specs.Count == 0)
            {
                return null;
            }

            var result = new List<FilePickerFileType>(specs.Count);
            for (var i = 0; i < specs.Count; i++)
            {
                var spec = specs[i];
                if (spec == null)
                {
                    continue;
                }

                result.Add(new FilePickerFileType(spec.Name)
                {
                    Patterns = spec.Patterns?.ToArray(),
                    MimeTypes = spec.MimeTypes?.ToArray(),
                    AppleUniformTypeIdentifiers = spec.AppleUniformTypeIdentifiers?.ToArray()
                });
            }

            return result.Count > 0 ? result : null;
        }
    }
}
