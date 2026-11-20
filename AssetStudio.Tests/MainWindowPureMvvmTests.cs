using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AssetStudio;
using AssetStudio.Avalonia;
using AssetStudio.Avalonia.Services;
using AssetStudio.Avalonia.ViewModels;
using Xunit;

namespace AssetStudio.Tests
{
    public class MainWindowPureMvvmTests
    {
        private static readonly Regex AvaloniaUsingRegex = new(
            @"^\s*using\s+(?:global::)?Avalonia(?:\.[A-Za-z0-9_]+)?\s*;",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);

        private static readonly Regex AxamlClickAttributeRegex = new(
            @"\bClick\s*=",
            RegexOptions.CultureInvariant);

        [Fact]
        public async Task MainWindowViewModel_HeadlessCommands_ExecuteEndToEndWithFakeDialogService()
        {
            var fakeDialogs = new RecordingDialogService();
            using var vm = new MainWindowViewModel(dialogService: fakeDialogs);

            // All 9 sub-ViewModels + IDialogService + CommandHandlers are wired headlessly.
            Assert.NotNull(vm.AssetList);
            Assert.NotNull(vm.IndexingProgress);
            Assert.NotNull(vm.PreviewSession);
            Assert.NotNull(vm.MeshPreview);
            Assert.NotNull(vm.Export);
            Assert.NotNull(vm.SceneHierarchy);
            Assert.NotNull(vm.ProjectSession);
            Assert.NotNull(vm.MediaPlayback);
            Assert.NotNull(vm.Options);
            Assert.Same(fakeDialogs, vm.DialogService);
            Assert.NotNull(vm.CommandHandlers);

            var sampleRoot = Path.Combine(Path.GetTempPath(), "AssetStudioPureMvvmSample");
            var sampleBundle = Path.Combine(sampleRoot, "data.unity3d");
            var sampleExport = Path.Combine(sampleRoot, "out");
            var sampleFbx = Path.Combine(sampleExport, "hero.fbx");
            var sampleLog = Path.Combine(sampleExport, "error.log");

            fakeDialogs.OpenFilesResult = new[] { sampleBundle };
            fakeDialogs.OpenFolderResult = sampleRoot;
            fakeDialogs.SaveFileResult = sampleFbx;
            fakeDialogs.ExportOptionsResult = true;
            fakeDialogs.ProjectManagerResult = new ProjectManagerDialogResult(
                projectPath: sampleRoot,
                projectId: "p-1",
                projectName: "SampleGame");

            // File & Project commands
            await vm.LoadFileCommand.ExecuteAsync();
            Assert.Equal("Select Game File", fakeDialogs.LastFilePickerRequest?.Title);
            Assert.Contains("1 file(s)", vm.StatusText);

            await vm.LoadFolderCommand.ExecuteAsync();
            Assert.Equal(sampleRoot, vm.ProjectSession.ProjectFolderPath);
            Assert.True(vm.ProjectSession.IsLazyMode);

            await vm.LoadFolderEagerCommand.ExecuteAsync();
            Assert.False(vm.ProjectSession.IsLazyMode);

            await vm.ExtractFileCommand.ExecuteAsync();
            Assert.Contains("extract", vm.StatusText, StringComparison.OrdinalIgnoreCase);

            await vm.ExtractFolderCommand.ExecuteAsync();
            Assert.Contains("extract", vm.StatusText, StringComparison.OrdinalIgnoreCase);

            await vm.SetProjectRootCommand.ExecuteAsync();
            Assert.Contains(sampleRoot, vm.StatusText);

            await vm.ShowExportOptionsCommand.ExecuteAsync();
            Assert.Equal(1, fakeDialogs.ShowExportOptionsCalls);
            Assert.Equal("Export options updated.", vm.StatusText);

            await vm.OpenProjectManagerCommand.ExecuteAsync();
            Assert.Equal("SampleGame", vm.ProjectSession.ProjectDisplayName);

            await vm.ClearProjectCacheCommand.ExecuteAsync();
            Assert.Contains("Cleared project cache", vm.StatusText);

            // Clearing cache when no project is loaded calls ShowMessageAsync
            vm.ProjectSession.ResetSession();
            await vm.ClearProjectCacheCommand.ExecuteAsync();
            Assert.Equal("Clear project cache", fakeDialogs.LastMessage.Title);

            // Export commands
            await vm.ExportAllAssetsCommand.ExecuteAsync();
            Assert.Equal("Exporting all assets...", vm.StatusText);

            await vm.ExportModelGroupFbxCommand.ExecuteAsync();
            Assert.Contains(sampleFbx, vm.StatusText);

            fakeDialogs.SaveFileResult = sampleLog;
            await vm.ExportErrorLogCommand.ExecuteAsync();
            Assert.Contains(sampleLog, vm.StatusText);

            // Indexing commands & CanExecute transitions
            var pauseChanged = 0;
            var resumeChanged = 0;
            var stopChanged = 0;
            vm.PauseIndexingCommand.CanExecuteChanged += (_, _) => pauseChanged++;
            vm.ResumeIndexingCommand.CanExecuteChanged += (_, _) => resumeChanged++;
            vm.StopIndexingCommand.CanExecuteChanged += (_, _) => stopChanged++;

            Assert.False(vm.PauseIndexingCommand.CanExecute());
            Assert.False(vm.ResumeIndexingCommand.CanExecute());
            Assert.False(vm.StopIndexingCommand.CanExecute());

            vm.IsIndexingActive = true;
            vm.IndexingProgress.BeginIndexing(totalFiles: 10, currentFile: "level0");
            Assert.True(vm.PauseIndexingCommand.CanExecute());
            Assert.False(vm.ResumeIndexingCommand.CanExecute());
            Assert.True(vm.StopIndexingCommand.CanExecute());
            Assert.True(pauseChanged > 0);
            Assert.True(stopChanged > 0);

            vm.PauseIndexingCommand.Execute();
            Assert.Equal(IndexingStatus.Paused, vm.IndexingProgress.Status);
            Assert.False(vm.PauseIndexingCommand.CanExecute());
            Assert.True(vm.ResumeIndexingCommand.CanExecute());
            Assert.True(resumeChanged > 0);

            vm.ResumeIndexingCommand.Execute();
            Assert.Equal(IndexingStatus.Indexing, vm.IndexingProgress.Status);
            Assert.True(vm.PauseIndexingCommand.CanExecute());
            Assert.False(vm.ResumeIndexingCommand.CanExecute());

            vm.StopIndexingCommand.Execute();
            Assert.False(vm.IsIndexingActive);
            Assert.False(vm.StopIndexingCommand.CanExecute());

            // Filter Type & Asset List commands
            vm.ToggleFilterTypeCommand.Execute(ClassIDType.Texture2D);
            Assert.False(vm.AssetList.IsAllTypesSelected);
            Assert.Contains(ClassIDType.Texture2D, vm.AssetList.SelectedClassTypes);

            vm.ToggleFilterTypeCommand.Execute("Mesh");
            Assert.Contains(ClassIDType.Mesh, vm.AssetList.SelectedClassTypes);

            vm.FilterTypeAllCommand.Execute();
            Assert.True(vm.AssetList.IsAllTypesSelected);
            Assert.Empty(vm.AssetList.SelectedClassTypes);

            var classFilter = new AssetClassItem { ClassID = (int)ClassIDType.Texture2D, Name = "Texture2D", UnityVersion = "2022.3.10f1" };
            vm.AssetList.SetClassFilterOverride(classFilter);
            Assert.True(vm.AssetList.HasClassFilterOverride);
            vm.ClearClassFilterCommand.Execute();
            Assert.False(vm.AssetList.HasClassFilterOverride);

            // Asset context commands (Copy, Reveal Original, Go To Scene Hierarchy)
            var rootNode = new GameObjectNode { Name = "WorldRoot" };
            var childNode = new GameObjectNode { Name = "Hero" };
            rootNode.AddChild(childNode);
            var handle = new AssetHandle
            {
                SerializedFileName = "sharedassets0.assets",
                OriginalPath = sampleBundle,
                PathID = 77,
                Type = ClassIDType.Mesh,
                Name = "HeroMesh",
                ByteSize = 1024,
                UniqueID = "sharedassets0.assets#77"
            };
            var assetItem = new AssetItem(handle, 0) { TreeNode = childNode };
            vm.AssetList.SelectItem(assetItem);

            await vm.CopyAssetCellTextCommand.ExecuteAsync();
            Assert.Equal("HeroMesh", fakeDialogs.LastClipboardText);

            await vm.ShowOriginalFileCommand.ExecuteAsync();
            Assert.Equal(sampleBundle, fakeDialogs.LastRevealedPath);

            vm.GoToSceneHierarchyCommand.Execute();
            Assert.Same(childNode, vm.SceneHierarchy.SelectedNode);
            Assert.True(rootNode.IsExpanded);

            // Preview, TextAsset modes, Info toggle, Animation & Mesh commands
            Assert.True(vm.PreviewSession.IsInfoExpanded);
            Assert.Equal("▼", vm.PreviewSession.InfoToggleGlyph);
            vm.TogglePreviewInfoCommand.Execute();
            Assert.False(vm.PreviewSession.IsInfoExpanded);
            Assert.Equal("▲", vm.PreviewSession.InfoToggleGlyph);

            Assert.True(vm.MeshPreview.IsControlsExpanded);
            Assert.Equal("▲", vm.MeshPreview.ControlsToggleGlyph);
            vm.ToggleMeshControlsCommand.Execute();
            Assert.False(vm.MeshPreview.IsControlsExpanded);
            Assert.Equal("▼", vm.MeshPreview.ControlsToggleGlyph);

            vm.PreviewSession.ShowDialogueCards(
                assetItem,
                cards: Array.Empty<TextAssetDialogueCardViewModel>(),
                formatName: "JSON",
                parsedStringCount: 2,
                plainTextScript: "Line 1\nLine 2",
                detailsText: "Metadata details");
            Assert.True(vm.PreviewSession.HasDialoguePreview);
            Assert.True(vm.PreviewSession.IsDialogueCardsVisible);

            vm.ShowTextAssetDetailsCommand.Execute();
            Assert.Equal(TextAssetDisplayMode.Text, vm.PreviewSession.DialogueDisplayMode);
            Assert.True(vm.PreviewSession.IsDialogueDetailsVisible);
            Assert.Equal("Line 1\nLine 2", vm.PreviewSession.TextContent);

            vm.ShowTextAssetInfoCommand.Execute();
            Assert.Equal(TextAssetDisplayMode.Info, vm.PreviewSession.DialogueDisplayMode);
            Assert.Equal("Metadata details", vm.PreviewSession.TextContent);

            vm.MeshPreview.SetAnimationState(isLoaded: true, clipName: "Walk", durationSeconds: 2.0, isPlaying: true, totalFrames: 60);
            Assert.Equal("Pause", vm.MeshPreview.PlayPauseButtonText);
            vm.ToggleAnimationPlayPauseCommand.Execute();
            Assert.False(vm.MeshPreview.IsPlaying);
            Assert.Equal("Play", vm.MeshPreview.PlayPauseButtonText);

            vm.MeshPreview.Seek(1.25);
            vm.RestartAnimationCommand.Execute();
            Assert.True(vm.MeshPreview.IsPlaying);
            Assert.Equal(0.0, vm.MeshPreview.CurrentTimeSeconds);

            vm.MeshPreview.ViewPreset = MeshPreviewViewPreset.Back;
            vm.ResetMeshViewCommand.Execute();
            Assert.Equal(MeshPreviewViewPreset.Auto, vm.MeshPreview.ViewPreset);

            vm.CycleWireframeModeCommand.Execute();
            Assert.Equal(1, vm.MeshPreview.WireframeMode);
            vm.ToggleShadeModeCommand.Execute();
            Assert.Equal(1, vm.MeshPreview.ShadeMode);
            vm.ToggleNormalModeCommand.Execute();
            Assert.Equal(1, vm.MeshPreview.NormalMode);

            vm.ToggleTextureChannelCommand.Execute(PreviewChannelMask.Red);
            Assert.False(vm.PreviewSession.ShowRed);

            var materialSlot = new MeshMaterialPreviewSlot
            {
                SlotIndex = 0,
                MaterialName = "HeroMat",
                BaseColor = new global::OpenTK.Mathematics.Vector4(0.2f, 0.4f, 0.6f, 1.0f),
                CurrentColor = new global::OpenTK.Mathematics.Vector4(0.9f, 0.8f, 0.7f, 0.5f)
            };
            vm.MeshPreview.SetMaterialSlots(new[] { materialSlot });
            vm.ResetMeshMaterialColorCommand.Execute();
            Assert.Equal(0.2f, materialSlot.CurrentColor.X, 4);
            Assert.Equal(0.4f, materialSlot.CurrentColor.Y, 4);
            Assert.Equal(0.6f, materialSlot.CurrentColor.Z, 4);
            Assert.Equal(0.5f, materialSlot.CurrentColor.W, 4);

            // Media Playback commands (Audio + Video)
            vm.MediaPlayback.SelectAudio("Theme", "44100 Hz");
            Assert.True(vm.MediaPlayback.IsAudioActive);
            await vm.AudioPlayCommand.ExecuteAsync();
            Assert.True(vm.MediaPlayback.IsPlaying);
            Assert.Equal("Pause", vm.MediaPlayback.AudioPauseButtonText);

            vm.AudioPauseCommand.Execute();
            Assert.True(vm.MediaPlayback.IsPaused);
            Assert.Equal("Resume", vm.MediaPlayback.AudioPauseButtonText);

            vm.AudioStopCommand.Execute();
            Assert.Equal(MediaPlaybackState.Stopped, vm.MediaPlayback.PlaybackState);

            vm.MediaPlayback.SelectVideo("Intro", "1920x1080");
            Assert.True(vm.MediaPlayback.IsVideoActive);
            await vm.VideoPlayPauseCommand.ExecuteAsync();
            Assert.True(vm.MediaPlayback.IsPlaying);
            Assert.Equal("Pause", vm.MediaPlayback.VideoPlayButtonText);

            await vm.VideoPlayPauseCommand.ExecuteAsync();
            Assert.True(vm.MediaPlayback.IsPaused);
            Assert.Equal("Play", vm.MediaPlayback.VideoPlayButtonText);

            await vm.VideoStopCommand.ExecuteAsync();
            Assert.Equal(MediaPlaybackState.Stopped, vm.MediaPlayback.PlaybackState);

            await vm.VideoExportCommand.ExecuteAsync();
            Assert.Contains(sampleRoot, vm.StatusText);
        }

        [Fact]
        public async Task MainWindowViewModel_DefaultHeadlessConstructorAndCommandHandlerOverrides_WorkCleanly()
        {
            using var vm = new MainWindowViewModel();
            Assert.NotNull(vm.DialogService);

            // Default NoOpDialogService never throws.
            await vm.LoadFileCommand.ExecuteAsync();
            await vm.LoadFolderCommand.ExecuteAsync();
            await vm.ShowExportOptionsCommand.ExecuteAsync();
            await vm.ClearProjectCacheCommand.ExecuteAsync();

            var rotatedLeft = 0;
            var rotatedRight = 0;
            var rotatedUp = 0;
            var rotatedDown = 0;
            var customLoadCalled = 0;
            vm.CommandHandlers.RotateMeshLeft = () => rotatedLeft++;
            vm.CommandHandlers.RotateMeshRight = () => rotatedRight++;
            vm.CommandHandlers.RotateMeshUp = () => rotatedUp++;
            vm.CommandHandlers.RotateMeshDown = () => rotatedDown++;
            vm.CommandHandlers.LoadFileAsync = () =>
            {
                customLoadCalled++;
                return Task.CompletedTask;
            };

            vm.RotateMeshLeftCommand.Execute();
            vm.RotateMeshRightCommand.Execute();
            vm.RotateMeshUpCommand.Execute();
            vm.RotateMeshDownCommand.Execute();
            await vm.LoadFileCommand.ExecuteAsync();

            Assert.Equal(1, rotatedLeft);
            Assert.Equal(1, rotatedRight);
            Assert.Equal(1, rotatedUp);
            Assert.Equal(1, rotatedDown);
            Assert.Equal(1, customLoadCalled);
        }

        [Fact]
        public void AllWindowOptions_SyncWithMainWindowViewModel()
        {
            using var vm = new MainWindowViewModel();

            var settings = new AvaloniaAppSettings
            {
                DisplayAll = true,
                EnablePreview = false,
                DisplayInfo = false,
                UseDiskDecompressionCache = false,
                ShowErrorMessage = true,
                SpecifyUnityVersion = "2022.3.48f1",
                SelectedTheme = "Dark"
            };

            vm.Options.LoadFromSettings(settings);

            Assert.True(vm.Options.DisplayAll);
            Assert.False(vm.Options.EnablePreview);
            Assert.False(vm.PreviewSession.IsPreviewEnabled);
            Assert.False(vm.Options.DisplayInfo);
            Assert.False(vm.PreviewSession.IsDisplayInfoEnabled);
            Assert.False(vm.Options.UseDiskDecompressionCache);
            Assert.True(vm.Options.ShowErrorMessage);
            Assert.Equal("2022.3.48f1", vm.Options.SpecifyUnityVersion);
            Assert.Equal("2022.3.48f1", vm.SpecifyUnityVersion);
            Assert.Equal(AppThemePreference.Dark, vm.Options.SelectedTheme);
            Assert.True(vm.Options.IsThemeDark);
            Assert.False(vm.Options.IsThemeLight);
            Assert.False(vm.Options.IsThemeDefault);

            // Bidirectional UnityVersion sync & PreviewSession toggle sync
            var changedProps = new List<string?>();
            vm.PropertyChanged += (_, e) => changedProps.Add(e.PropertyName);

            vm.SpecifyUnityVersion = "2023.2.1f1";
            Assert.Equal("2023.2.1f1", vm.Options.SpecifyUnityVersion);
            Assert.Contains(nameof(MainWindowViewModel.SpecifyUnityVersion), changedProps);

            vm.Options.EnablePreview = true;
            vm.Options.DisplayInfo = true;
            Assert.True(vm.PreviewSession.IsPreviewEnabled);
            Assert.True(vm.PreviewSession.IsDisplayInfoEnabled);

            // Theme radio commands are mutually exclusive
            vm.Options.SetThemeLightCommand.Execute();
            Assert.True(vm.Options.IsThemeLight);
            Assert.False(vm.Options.IsThemeDark);
            Assert.False(vm.Options.IsThemeDefault);

            vm.Options.SetThemeDefaultCommand.Execute();
            Assert.True(vm.Options.IsThemeDefault);
            Assert.False(vm.Options.IsThemeLight);
            Assert.False(vm.Options.IsThemeDark);

            // Round-trip back to settings
            var saved = new AvaloniaAppSettings();
            vm.Options.ApplyToSettings(saved);
            Assert.Equal(vm.Options.DisplayAll, saved.DisplayAll);
            Assert.Equal(vm.Options.EnablePreview, saved.EnablePreview);
            Assert.Equal(vm.Options.DisplayInfo, saved.DisplayInfo);
            Assert.Equal(vm.Options.UseDiskDecompressionCache, saved.UseDiskDecompressionCache);
            Assert.Equal(vm.Options.ShowErrorMessage, saved.ShowErrorMessage);
            Assert.Equal("2023.2.1f1", saved.SpecifyUnityVersion);
            Assert.Equal("Default", saved.SelectedTheme);

            // ResetSession preserves user Options and re-applies Preview/DisplayInfo flags to PreviewSession
            vm.Options.EnablePreview = false;
            vm.Options.DisplayInfo = false;
            vm.ResetSession();
            Assert.False(vm.Options.EnablePreview);
            Assert.False(vm.PreviewSession.IsPreviewEnabled);
            Assert.False(vm.Options.DisplayInfo);
            Assert.False(vm.PreviewSession.IsDisplayInfoEnabled);
        }

        [Fact]
        public void AllViewModels_HaveZeroAvaloniaImports()
        {
            var repoRoot = LocateRepositoryRoot();
            var viewModelsDir = Path.Combine(repoRoot, "AssetStudio.Avalonia", "ViewModels");
            Assert.True(Directory.Exists(viewModelsDir), $"ViewModels directory not found: {viewModelsDir}");

            var csFiles = Directory.GetFiles(viewModelsDir, "*.cs", SearchOption.AllDirectories);
            Assert.NotEmpty(csFiles);

            var violations = new List<string>();
            foreach (var file in csFiles)
            {
                var content = File.ReadAllText(file);
                if (AvaloniaUsingRegex.IsMatch(content))
                {
                    violations.Add(Path.GetRelativePath(repoRoot, file));
                }
            }

            Assert.Empty(violations);

            // Also verify via reflection that no type in AssetStudio.Avalonia.ViewModels exposes Avalonia UI types.
            var vmTypes = typeof(MainWindowViewModel).Assembly
                .GetTypes()
                .Where(t => string.Equals(t.Namespace, "AssetStudio.Avalonia.ViewModels", StringComparison.Ordinal))
                .ToArray();
            Assert.NotEmpty(vmTypes);

            foreach (var type in vmTypes)
            {
                foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    AssertNotAvaloniaType(prop.PropertyType, $"{type.Name}.{prop.Name}");
                }

                foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    AssertNotAvaloniaType(field.FieldType, $"{type.Name}.{field.Name}");
                }
            }
        }

        [Fact]
        public void AllPureServices_HaveZeroAvaloniaImports()
        {
            var repoRoot = LocateRepositoryRoot();
            var servicesDir = Path.Combine(repoRoot, "AssetStudio.Avalonia", "Services");
            Assert.True(Directory.Exists(servicesDir), $"Services directory not found: {servicesDir}");

            var csFiles = Directory.GetFiles(servicesDir, "*.cs", SearchOption.AllDirectories);
            Assert.NotEmpty(csFiles);

            var violations = new List<string>();
            foreach (var file in csFiles)
            {
                if (string.Equals(Path.GetFileName(file), "AvaloniaDialogService.cs", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var content = File.ReadAllText(file);
                if (AvaloniaUsingRegex.IsMatch(content))
                {
                    violations.Add(Path.GetRelativePath(repoRoot, file));
                }
            }

            Assert.Empty(violations);
        }

        [Fact]
        public void AllWindowAxamlFiles_UseCompiledBindingsAndZeroClickHandlers()
        {
            var repoRoot = LocateRepositoryRoot();
            var appDir = Path.Combine(repoRoot, "AssetStudio.Avalonia");
            var windowAxamlFiles = new[]
            {
                "MainWindow.axaml",
                "ExportOptionsWindow.axaml",
                "ProjectEditorWindow.axaml",
                "ProjectManagerWindow.axaml",
                "MessageBoxWindow.axaml"
            };

            foreach (var fileName in windowAxamlFiles)
            {
                var fullPath = Path.Combine(appDir, fileName);
                Assert.True(File.Exists(fullPath), $"Window AXAML file not found: {fullPath}");

                var content = File.ReadAllText(fullPath);
                Assert.Contains("x:DataType=", content, StringComparison.Ordinal);
                Assert.Contains("x:CompileBindings=\"True\"", content, StringComparison.Ordinal);
                Assert.False(
                    AxamlClickAttributeRegex.IsMatch(content),
                    $"{fileName} must not contain any Click= attributes (pure MVVM Command/binding architecture).");
            }
        }

        private static string LocateRepositoryRoot()
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current != null)
            {
                var candidate = Path.Combine(current.FullName, "AssetStudio.Avalonia", "AssetStudio.Avalonia.csproj");
                if (File.Exists(candidate))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            throw new DirectoryNotFoundException($"Unable to locate repository root from '{AppContext.BaseDirectory}'.");
        }

        private static void AssertNotAvaloniaType(Type type, string memberName)
        {
            var ns = type.Namespace ?? string.Empty;
            Assert.False(
                string.Equals(ns, "Avalonia", StringComparison.Ordinal) || ns.StartsWith("Avalonia.", StringComparison.Ordinal),
                $"ViewModel member '{memberName}' must not reference Avalonia type '{type.FullName}'.");

            if (type.IsGenericType)
            {
                foreach (var arg in type.GetGenericArguments())
                {
                    AssertNotAvaloniaType(arg, memberName);
                }
            }
        }

        private sealed class RecordingDialogService : IDialogService
        {
            public IReadOnlyList<string> OpenFilesResult { get; set; } = Array.Empty<string>();
            public string? OpenFolderResult { get; set; }
            public string? SaveFileResult { get; set; }
            public bool ConfirmResult { get; set; }
            public RiskyLoadChoice RiskyLoadResult { get; set; } = RiskyLoadChoice.Cancel;
            public MemoryPressureResult MemoryPressureResult { get; set; } = MemoryPressureResult.Continue;
            public bool ExportOptionsResult { get; set; }
            public ProjectManagerDialogResult? ProjectManagerResult { get; set; }

            public FilePickerRequest? LastFilePickerRequest { get; private set; }
            public FolderPickerRequest? LastFolderPickerRequest { get; private set; }
            public SaveFilePickerRequest? LastSaveFilePickerRequest { get; private set; }
            public (string Title, string Message) LastMessage { get; private set; }
            public (string Title, string Message) LastError { get; private set; }
            public string? LastClipboardText { get; private set; }
            public string? LastRevealedPath { get; private set; }
            public int ShowExportOptionsCalls { get; private set; }

            public Task<IReadOnlyList<string>> PickOpenFilesAsync(FilePickerRequest request)
            {
                LastFilePickerRequest = request;
                return Task.FromResult(OpenFilesResult);
            }

            public Task<string?> PickOpenFolderAsync(FolderPickerRequest request)
            {
                LastFolderPickerRequest = request;
                return Task.FromResult(OpenFolderResult);
            }

            public Task<string?> PickSaveFileAsync(SaveFilePickerRequest request)
            {
                LastSaveFilePickerRequest = request;
                return Task.FromResult(SaveFileResult);
            }

            public Task ShowMessageAsync(string title, string message)
            {
                LastMessage = (title, message);
                return Task.CompletedTask;
            }

            public Task ShowErrorAsync(string title, string message)
            {
                LastError = (title, message);
                return Task.CompletedTask;
            }

            public Task<bool> ConfirmAsync(string title, string message)
                => Task.FromResult(ConfirmResult);

            public Task<RiskyLoadChoice> ShowRiskyLoadDialogAsync(string title, string message)
                => Task.FromResult(RiskyLoadResult);

            public Task<MemoryPressureResult> ShowMemoryPressureDialogAsync(string title, string message)
                => Task.FromResult(MemoryPressureResult);

            public Task<bool> ShowExportOptionsAsync(AvaloniaAppSettings settings)
            {
                ShowExportOptionsCalls++;
                return Task.FromResult(ExportOptionsResult);
            }

            public Task<ProjectManagerDialogResult?> ShowProjectManagerAsync(string? activeProjectPath)
                => Task.FromResult(ProjectManagerResult);

            public Task<bool> CopyTextToClipboardAsync(string text)
            {
                LastClipboardText = text;
                return Task.FromResult(true);
            }

            public Task<bool> OpenPathInExplorerAsync(string path)
            {
                LastRevealedPath = path;
                return Task.FromResult(!string.IsNullOrWhiteSpace(path));
            }

            public Task<bool> RevealFileInExplorerAsync(string filePath)
            {
                LastRevealedPath = filePath;
                return Task.FromResult(!string.IsNullOrWhiteSpace(filePath));
            }

            public Task<bool> OpenUrlAsync(string url)
                => Task.FromResult(!string.IsNullOrWhiteSpace(url));
        }
    }
}
