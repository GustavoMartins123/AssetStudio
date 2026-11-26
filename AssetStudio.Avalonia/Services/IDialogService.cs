using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AssetStudio.Avalonia.Services
{
    public sealed record FileTypeFilterSpec
    {
        public FileTypeFilterSpec(
            string name,
            IReadOnlyList<string>? patterns = null,
            IReadOnlyList<string>? mimeTypes = null,
            IReadOnlyList<string>? appleUniformTypeIdentifiers = null)
        {
            Name = string.IsNullOrWhiteSpace(name) ? "All files" : name;
            Patterns = patterns ?? Array.Empty<string>();
            MimeTypes = mimeTypes;
            AppleUniformTypeIdentifiers = appleUniformTypeIdentifiers;
        }

        public FileTypeFilterSpec(string name, params string[] patterns)
            : this(name, (IReadOnlyList<string>)(patterns ?? Array.Empty<string>()))
        {
        }

        public string Name { get; init; }

        public IReadOnlyList<string> Patterns { get; init; }

        public IReadOnlyList<string>? MimeTypes { get; init; }

        public IReadOnlyList<string>? AppleUniformTypeIdentifiers { get; init; }
    }

    public sealed record FilePickerRequest
    {
        public FilePickerRequest(
            string title = "Select File",
            bool allowMultiple = false,
            string? suggestedStartDirectory = null,
            IReadOnlyList<FileTypeFilterSpec>? fileTypeFilters = null)
        {
            Title = string.IsNullOrWhiteSpace(title) ? "Select File" : title;
            AllowMultiple = allowMultiple;
            SuggestedStartDirectory = suggestedStartDirectory;
            FileTypeFilters = fileTypeFilters;
        }

        public string Title { get; init; }

        public bool AllowMultiple { get; init; }

        public string? SuggestedStartDirectory { get; init; }

        public IReadOnlyList<FileTypeFilterSpec>? FileTypeFilters { get; init; }
    }

    public sealed record FolderPickerRequest
    {
        public FolderPickerRequest(
            string title = "Select Folder",
            bool allowMultiple = false,
            string? suggestedStartDirectory = null)
        {
            Title = string.IsNullOrWhiteSpace(title) ? "Select Folder" : title;
            AllowMultiple = allowMultiple;
            SuggestedStartDirectory = suggestedStartDirectory;
        }

        public string Title { get; init; }

        public bool AllowMultiple { get; init; }

        public string? SuggestedStartDirectory { get; init; }
    }

    public sealed record SaveFilePickerRequest
    {
        public SaveFilePickerRequest(
            string title = "Save File",
            string? suggestedFileName = null,
            string? defaultExtension = null,
            string? suggestedStartDirectory = null,
            IReadOnlyList<FileTypeFilterSpec>? fileTypeFilters = null,
            bool showOverwritePrompt = true)
        {
            Title = string.IsNullOrWhiteSpace(title) ? "Save File" : title;
            SuggestedFileName = suggestedFileName;
            DefaultExtension = defaultExtension;
            SuggestedStartDirectory = suggestedStartDirectory;
            FileTypeFilters = fileTypeFilters;
            ShowOverwritePrompt = showOverwritePrompt;
        }

        public string Title { get; init; }

        public string? SuggestedFileName { get; init; }

        public string? DefaultExtension { get; init; }

        public string? SuggestedStartDirectory { get; init; }

        public IReadOnlyList<FileTypeFilterSpec>? FileTypeFilters { get; init; }

        public bool ShowOverwritePrompt { get; init; }
    }

    public sealed record ProjectManagerDialogResult
    {
        public ProjectManagerDialogResult(
            string? projectPath = null,
            string? projectId = null,
            string? projectName = null,
            ManagedProject? project = null,
            AvaloniaAppSettings? settings = null,
            bool openedWithoutProject = false)
        {
            ProjectPath = projectPath ?? project?.ProjectRoot;
            ProjectId = projectId ?? project?.Id;
            ProjectName = projectName ?? project?.DisplayName;
            Project = project;
            Settings = settings;
            OpenedWithoutProject = openedWithoutProject;
        }

        public string? ProjectPath { get; init; }

        public string? ProjectId { get; init; }

        public string? ProjectName { get; init; }

        public ManagedProject? Project { get; init; }

        public AvaloniaAppSettings? Settings { get; init; }

        public bool OpenedWithoutProject { get; init; }
    }

    public interface IDialogService
    {
        Task<IReadOnlyList<string>> PickOpenFilesAsync(FilePickerRequest request);

        Task<string?> PickOpenFolderAsync(FolderPickerRequest request);

        Task<string?> PickSaveFileAsync(SaveFilePickerRequest request);

        Task ShowMessageAsync(string title, string message);

        Task ShowErrorAsync(string title, string message);

        Task<bool> ConfirmAsync(string title, string message);

        Task<RiskyLoadChoice> ShowRiskyLoadDialogAsync(string title, string message);

        Task<bool> ShowExportOptionsAsync(AvaloniaAppSettings settings);

        Task<ProjectManagerDialogResult?> ShowProjectManagerAsync(string? activeProjectPath);

        Task<bool> CopyTextToClipboardAsync(string text);

        Task<bool> OpenPathInExplorerAsync(string path);

        Task<bool> OpenUrlAsync(string url);

        Task<MemoryPressureResult> ShowMemoryPressureDialogAsync(string title, string message)
            => Task.FromResult(MemoryPressureResult.Continue);

        Task<bool> RevealFileInExplorerAsync(string filePath)
            => OpenPathInExplorerAsync(filePath);

        Task<ViewModels.DecryptionKeyDialogResult?> ShowDecryptionKeyDialogAsync(ViewModels.DecryptionDialogContext context)
            => Task.FromResult<ViewModels.DecryptionKeyDialogResult?>(null);

        Task<ViewModels.SceneExportDialogResult?> ShowSceneExportDialogAsync(ViewModels.SceneExportDialogRequest request)
            => Task.FromResult<ViewModels.SceneExportDialogResult?>(null);

        Task<ViewModels.CSharpProjectExportDialogResult?> ShowCSharpProjectExportDialogAsync(ViewModels.CSharpProjectExportDialogRequest request)
            => Task.FromResult<ViewModels.CSharpProjectExportDialogResult?>(null);
    }
}
