using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using AssetStudio;
using AssetStudio.Avalonia;
using AssetStudio.Avalonia.Services;
using AssetStudio.Avalonia.ViewModels;
using Xunit;

namespace AssetStudio.Tests
{
    public class CommandAndDialogServiceTests
    {
        [Fact]
        public void RelayCommand_ExecuteAndCanExecute_RespectsPredicateAndNotifiesChanges()
        {
            var executedCount = 0;
            var canRun = false;
            var canExecuteChangedCount = 0;

            var command = new RelayCommand(
                () => executedCount++,
                () => canRun);

            command.CanExecuteChanged += (_, _) => canExecuteChangedCount++;

            Assert.False(command.CanExecute());
            command.Execute();
            Assert.Equal(0, executedCount);

            canRun = true;
            command.NotifyCanExecuteChanged();
            Assert.Equal(1, canExecuteChangedCount);
            Assert.True(command.CanExecute());

            command.Execute();
            Assert.Equal(1, executedCount);
        }

        [Fact]
        public void RelayCommand_Generic_HandlesNullReferenceValueAndConvertibleParametersSafely()
        {
            var receivedStrings = new List<string?>();
            var stringCommand = new RelayCommand<string>(
                s => receivedStrings.Add(s),
                s => s != "blocked");

            var changedCount = 0;
            stringCommand.CanExecuteChanged += (_, _) => changedCount++;

            Assert.True(stringCommand.CanExecute(null));
            stringCommand.Execute(null);
            Assert.Single(receivedStrings);
            Assert.Null(receivedStrings[0]);

            Assert.False(stringCommand.CanExecute("blocked"));
            stringCommand.Execute("blocked");
            Assert.Single(receivedStrings);

            Assert.True(stringCommand.CanExecute("allowed"));
            stringCommand.Execute("allowed");
            Assert.Equal(2, receivedStrings.Count);
            Assert.Equal("allowed", receivedStrings[1]);

            stringCommand.NotifyCanExecuteChanged();
            Assert.Equal(1, changedCount);

            // Value type parameter safety: null becomes default(int) == 0, convertible string "42" converts to 42,
            // and incompatible object returns false from CanExecute and does not execute.
            var receivedInts = new List<int>();
            var intCommand = new RelayCommand<int>(
                n => receivedInts.Add(n),
                n => n >= 0);

            Assert.True(intCommand.CanExecute(null));
            intCommand.Execute(null);
            Assert.Equal(new[] { 0 }, receivedInts);

            Assert.True(intCommand.CanExecute("42"));
            intCommand.Execute("42");
            Assert.Equal(new[] { 0, 42 }, receivedInts);

            Assert.False(intCommand.CanExecute(-5));
            intCommand.Execute(-5);
            Assert.Equal(new[] { 0, 42 }, receivedInts);

            Assert.False(intCommand.CanExecute(new object()));
            intCommand.Execute(new object());
            Assert.Equal(new[] { 0, 42 }, receivedInts);
        }

        [Fact]
        public async Task AsyncRelayCommand_SequentialMode_PreventsReentrancy_TracksIsRunning_AndRaisesCanExecuteChanged()
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var executionCount = 0;
            var isRunningTransitions = new List<bool>();
            var canExecuteSnapshotsOnEvent = new List<bool>();

            AsyncRelayCommand? command = null;
            command = new AsyncRelayCommand(
                async _ =>
                {
                    Interlocked.Increment(ref executionCount);
                    await tcs.Task;
                },
                allowConcurrentExecutions: false);

            command.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(AsyncRelayCommand.IsRunning))
                {
                    isRunningTransitions.Add(command.IsRunning);
                }
            };

            command.CanExecuteChanged += (_, _) =>
            {
                canExecuteSnapshotsOnEvent.Add(command.CanExecute());
            };

            Assert.False(command.IsRunning);
            Assert.True(command.CanExecute());

            var firstRun = command.ExecuteAsync();
            Assert.True(command.IsRunning);
            Assert.False(command.CanExecute());

            // Attempt reentrant execution via ExecuteAsync and ICommand.Execute while running
            await command.ExecuteAsync();
            ((ICommand)command).Execute(null);
            Assert.Equal(1, executionCount);

            tcs.SetResult(true);
            await firstRun;

            Assert.False(command.IsRunning);
            Assert.True(command.CanExecute());
            Assert.Equal(new[] { true, false }, isRunningTransitions);
            Assert.Equal(new[] { false, true }, canExecuteSnapshotsOnEvent);
        }

        [Fact]
        public async Task AsyncRelayCommand_ConcurrentMode_AllowsOverlapAndMaintainsIsRunningUntilAllComplete()
        {
            var gate1 = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var gate2 = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var activeCalls = 0;

            var command = new AsyncRelayCommand(
                async _ =>
                {
                    var callIndex = Interlocked.Increment(ref activeCalls);
                    if (callIndex == 1)
                    {
                        await gate1.Task;
                    }
                    else
                    {
                        await gate2.Task;
                    }
                },
                allowConcurrentExecutions: true);

            Assert.True(command.AllowConcurrentExecutions);

            var run1 = command.ExecuteAsync();
            Assert.True(command.IsRunning);
            Assert.True(command.CanExecute());

            var run2 = command.ExecuteAsync();
            Assert.Equal(2, activeCalls);
            Assert.True(command.IsRunning);

            gate1.SetResult(true);
            await run1;

            // Still running because run2 has not finished yet
            Assert.True(command.IsRunning);

            gate2.SetResult(true);
            await run2;

            Assert.False(command.IsRunning);
        }

        [Fact]
        public async Task AsyncRelayCommand_Cancel_SignalsCancellationTokenAndResetsIsRunningCleanly()
        {
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var observedCancellation = false;
            Exception? routedException = null;

            var command = new AsyncRelayCommand(
                async ct =>
                {
                    started.TrySetResult(true);
                    try
                    {
                        await Task.Delay(Timeout.Infinite, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        observedCancellation = true;
                        throw;
                    }
                },
                onException: ex => routedException = ex);

            var runTask = command.ExecuteAsync();
            await started.Task;
            Assert.True(command.IsRunning);

            command.Cancel();
            await runTask;

            Assert.True(observedCancellation);
            Assert.False(command.IsRunning);
            Assert.Null(routedException);
        }

        [Fact]
        public async Task AsyncRelayCommand_ExceptionRouting_InvokesOnExceptionCallbackAndResetsIsRunning()
        {
            Exception? captured = null;
            var expected = new InvalidOperationException("Simulated command failure");

            var command = new AsyncRelayCommand(
                () => Task.FromException(expected),
                onException: ex => captured = ex);

            await command.ExecuteAsync();

            Assert.Same(expected, captured);
            Assert.False(command.IsRunning);
            Assert.True(command.CanExecute());
        }

        [Fact]
        public async Task AsyncRelayCommand_Generic_SupportsTypedExecutionCancellationReentrancyAndExceptionRouting()
        {
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var received = new List<string?>();
            var canExecuteEvents = 0;
            Exception? capturedError = null;

            var command = new AsyncRelayCommand<string>(
                async (arg, ct) =>
                {
                    if (arg == "throw")
                    {
                        throw new ArgumentException("Invalid argument");
                    }

                    received.Add(arg);
                    await gate.Task.WaitAsync(ct);
                },
                canExecute: arg => arg != "forbidden",
                allowConcurrentExecutions: false,
                onException: ex => capturedError = ex);

            command.CanExecuteChanged += (_, _) => canExecuteEvents++;

            Assert.False(command.CanExecute("forbidden"));
            await command.ExecuteAsync("forbidden");
            Assert.Empty(received);

            var firstTask = command.ExecuteAsync("first");
            Assert.True(command.IsRunning);
            Assert.False(command.CanExecute("second"));

            // Blocked while running because allowConcurrentExecutions == false
            await command.ExecuteAsync("second");
            Assert.Single(received);

            gate.SetResult(true);
            await firstTask;

            Assert.False(command.IsRunning);
            Assert.True(command.CanExecute("second"));
            Assert.Equal(2, canExecuteEvents);

            // Null parameter safety
            await command.ExecuteAsync(null);
            Assert.Equal(new string?[] { "first", null }, received);

            // Exception routing
            await command.ExecuteAsync("throw");
            Assert.IsType<ArgumentException>(capturedError);
            Assert.False(command.IsRunning);
        }

        [Fact]
        public void WindowOptionsViewModel_BooleanTogglesAndUnityVersion_RaisePropertyChangedAndCallbacks()
        {
            var vm = new WindowOptionsViewModel();
            var changedProperties = new List<string?>();
            var settingsChangedCount = 0;
            bool? lastDisplayAll = null;
            bool? lastEnablePreview = null;
            bool? lastDisplayInfo = null;
            bool? lastUseDiskCache = null;
            bool? lastShowError = null;
            string? lastUnityVersion = null;

            vm.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);
            vm.SettingsChanged += () => settingsChangedCount++;
            vm.DisplayAllChanged += v => lastDisplayAll = v;
            vm.EnablePreviewChanged += v => lastEnablePreview = v;
            vm.DisplayInfoChanged += v => lastDisplayInfo = v;
            vm.UseDiskDecompressionCacheChanged += v => lastUseDiskCache = v;
            vm.ShowErrorMessageChanged += v => lastShowError = v;
            vm.SpecifyUnityVersionChanged += v => lastUnityVersion = v;

            // Verify defaults match AvaloniaAppSettings
            Assert.False(vm.DisplayAll);
            Assert.True(vm.EnablePreview);
            Assert.True(vm.DisplayInfo);
            Assert.True(vm.UseDiskDecompressionCache);
            Assert.False(vm.ShowErrorMessage);
            Assert.Equal(string.Empty, vm.SpecifyUnityVersion);

            vm.DisplayAll = true;
            vm.EnablePreview = false;
            vm.DisplayInfo = false;
            vm.UseDiskDecompressionCache = false;
            vm.ShowErrorMessage = true;
            vm.SpecifyUnityVersion = "2021.3.28f1";

            Assert.True(lastDisplayAll);
            Assert.False(lastEnablePreview);
            Assert.False(lastDisplayInfo);
            Assert.False(lastUseDiskCache);
            Assert.True(lastShowError);
            Assert.Equal("2021.3.28f1", lastUnityVersion);
            Assert.Equal(6, settingsChangedCount);

            Assert.Contains(nameof(WindowOptionsViewModel.DisplayAll), changedProperties);
            Assert.Contains(nameof(WindowOptionsViewModel.EnablePreview), changedProperties);
            Assert.Contains(nameof(WindowOptionsViewModel.DisplayInfo), changedProperties);
            Assert.Contains(nameof(WindowOptionsViewModel.UseDiskDecompressionCache), changedProperties);
            Assert.Contains(nameof(WindowOptionsViewModel.ShowErrorMessage), changedProperties);
            Assert.Contains(nameof(WindowOptionsViewModel.SpecifyUnityVersion), changedProperties);
        }

        [Fact]
        public void WindowOptionsViewModel_ThemeSelection_MutuallyExclusivePropertiesAndCommandsNotifyAllThemeFlags()
        {
            var vm = new WindowOptionsViewModel();
            var propertyNotifications = new List<string?>();
            var themeEvents = new List<AppThemePreference>();

            vm.PropertyChanged += (_, e) => propertyNotifications.Add(e.PropertyName);
            vm.ThemeChanged += t => themeEvents.Add(t);

            Assert.Equal(AppThemePreference.Default, vm.SelectedTheme);
            Assert.True(vm.IsThemeDefault);
            Assert.False(vm.IsThemeLight);
            Assert.False(vm.IsThemeDark);

            // Setting IsThemeLight = true updates SelectedTheme and notifies all 3 flags + SelectedTheme
            vm.IsThemeLight = true;
            Assert.Equal(AppThemePreference.Light, vm.SelectedTheme);
            Assert.False(vm.IsThemeDefault);
            Assert.True(vm.IsThemeLight);
            Assert.False(vm.IsThemeDark);
            Assert.Contains(nameof(WindowOptionsViewModel.SelectedTheme), propertyNotifications);
            Assert.Contains(nameof(WindowOptionsViewModel.IsThemeDefault), propertyNotifications);
            Assert.Contains(nameof(WindowOptionsViewModel.IsThemeLight), propertyNotifications);
            Assert.Contains(nameof(WindowOptionsViewModel.IsThemeDark), propertyNotifications);

            // Setting Dark via command
            propertyNotifications.Clear();
            vm.SetThemeDarkCommand.Execute();
            Assert.Equal(AppThemePreference.Dark, vm.SelectedTheme);
            Assert.False(vm.IsThemeDefault);
            Assert.False(vm.IsThemeLight);
            Assert.True(vm.IsThemeDark);
            Assert.Equal(4, propertyNotifications.Count);

            // Setting Default via command
            vm.SetThemeDefaultCommand.Execute();
            Assert.Equal(AppThemePreference.Default, vm.SelectedTheme);
            Assert.True(vm.IsThemeDefault);
            Assert.False(vm.IsThemeLight);
            Assert.False(vm.IsThemeDark);

            // Setting Light via command
            vm.SetThemeLightCommand.Execute();
            Assert.Equal(AppThemePreference.Light, vm.SelectedTheme);

            Assert.Equal(
                new[]
                {
                    AppThemePreference.Light,
                    AppThemePreference.Dark,
                    AppThemePreference.Default,
                    AppThemePreference.Light
                },
                themeEvents);
        }

        [Fact]
        public void WindowOptionsViewModel_LoadFromSettingsAndApplyToSettings_RoundTripsAvaloniaAppSettingsFidelity()
        {
            var sourceSettings = new AvaloniaAppSettings
            {
                DisplayAll = true,
                EnablePreview = false,
                DisplayInfo = false,
                UseDiskDecompressionCache = false,
                ShowErrorMessage = true,
                SpecifyUnityVersion = "2022.3.10f1",
                SelectedTheme = "Dark"
            };

            var vm = new WindowOptionsViewModel();
            vm.LoadFromSettings(sourceSettings);

            Assert.True(vm.DisplayAll);
            Assert.False(vm.EnablePreview);
            Assert.False(vm.DisplayInfo);
            Assert.False(vm.UseDiskDecompressionCache);
            Assert.True(vm.ShowErrorMessage);
            Assert.Equal("2022.3.10f1", vm.SpecifyUnityVersion);
            Assert.Equal(AppThemePreference.Dark, vm.SelectedTheme);
            Assert.True(vm.IsThemeDark);

            var targetSettings = new AvaloniaAppSettings();
            vm.ApplyToSettings(targetSettings);

            Assert.Equal(sourceSettings.DisplayAll, targetSettings.DisplayAll);
            Assert.Equal(sourceSettings.EnablePreview, targetSettings.EnablePreview);
            Assert.Equal(sourceSettings.DisplayInfo, targetSettings.DisplayInfo);
            Assert.Equal(sourceSettings.UseDiskDecompressionCache, targetSettings.UseDiskDecompressionCache);
            Assert.Equal(sourceSettings.ShowErrorMessage, targetSettings.ShowErrorMessage);
            Assert.Equal(sourceSettings.SpecifyUnityVersion, targetSettings.SpecifyUnityVersion);
            Assert.Equal("Dark", targetSettings.SelectedTheme);

            // Verify Light and Default theme string round-trip
            vm.IsThemeLight = true;
            vm.ApplyToSettings(targetSettings);
            Assert.Equal("Light", targetSettings.SelectedTheme);

            vm.IsThemeDefault = true;
            vm.ApplyToSettings(targetSettings);
            Assert.Equal("Default", targetSettings.SelectedTheme);
        }

        [Fact]
        public async Task IDialogService_FakeContractVerification_ExercisesAllRequestResultModelsAndMethodsCrossPlatform()
        {
            var tempRoot = Path.Combine(Path.GetTempPath(), "AssetStudioDialogTests");
            var sampleFile1 = Path.Combine(tempRoot, "bundle1.unity3d");
            var sampleFile2 = Path.Combine(tempRoot, "bundle2.unity3d");
            var sampleSaveFile = Path.Combine(tempRoot, "merged.fbx");

            var fake = new FakeDialogService
            {
                OpenFilesResult = new[] { sampleFile1, sampleFile2 },
                OpenFolderResult = tempRoot,
                SaveFileResult = sampleSaveFile,
                ConfirmResult = true,
                RiskyLoadResult = RiskyLoadChoice.LazyLoad,
                MemoryPressureResult = MemoryPressureResult.StopAndKeep,
                ExportOptionsResult = true,
                ProjectManagerResult = new ProjectManagerDialogResult(
                    projectPath: tempRoot,
                    projectId: "proj-001",
                    projectName: "SampleProject",
                    openedWithoutProject: false)
            };

            IDialogService service = fake;

            var fbxFilter = new FileTypeFilterSpec("Fbx file", "*.fbx");
            Assert.Equal("Fbx file", fbxFilter.Name);
            Assert.Equal(new[] { "*.fbx" }, fbxFilter.Patterns);

            var openRequest = new FilePickerRequest(
                title: "Select Game File",
                allowMultiple: true,
                suggestedStartDirectory: tempRoot,
                fileTypeFilters: new[] { new FileTypeFilterSpec("Unity bundles", "*.unity3d", "*.bundle") });

            var pickedFiles = await service.PickOpenFilesAsync(openRequest);
            Assert.Equal(2, pickedFiles.Count);
            Assert.Equal(sampleFile1, pickedFiles[0]);
            Assert.Same(openRequest, fake.LastFilePickerRequest);

            var folderRequest = new FolderPickerRequest(
                title: "Select Game Folder",
                allowMultiple: false,
                suggestedStartDirectory: tempRoot);

            var pickedFolder = await service.PickOpenFolderAsync(folderRequest);
            Assert.Equal(tempRoot, pickedFolder);
            Assert.Same(folderRequest, fake.LastFolderPickerRequest);

            var saveRequest = new SaveFilePickerRequest(
                title: "Save merged FBX",
                suggestedFileName: "merged.fbx",
                defaultExtension: "fbx",
                suggestedStartDirectory: tempRoot,
                fileTypeFilters: new[] { fbxFilter },
                showOverwritePrompt: true);

            var savedFile = await service.PickSaveFileAsync(saveRequest);
            Assert.Equal(sampleSaveFile, savedFile);
            Assert.Same(saveRequest, fake.LastSaveFilePickerRequest);

            await service.ShowMessageAsync("Info", "Indexing finished");
            Assert.Equal(("Info", "Indexing finished"), fake.LastMessage);

            await service.ShowErrorAsync("Load failed", "Corrupt header");
            Assert.Equal(("Load failed", "Corrupt header"), fake.LastError);

            Assert.True(await service.ConfirmAsync("Delete project", "Are you sure?"));
            Assert.Equal(RiskyLoadChoice.LazyLoad, await service.ShowRiskyLoadDialogAsync("Large project", "Use Safe/Lazy Mode?"));
            Assert.Equal(MemoryPressureResult.StopAndKeep, await service.ShowMemoryPressureDialogAsync("Memory pressure", "92% used"));

            var appSettings = new AvaloniaAppSettings();
            Assert.True(await service.ShowExportOptionsAsync(appSettings));

            var pmResult = await service.ShowProjectManagerAsync(tempRoot);
            Assert.NotNull(pmResult);
            Assert.Equal(tempRoot, pmResult!.ProjectPath);
            Assert.Equal("proj-001", pmResult.ProjectId);
            Assert.Equal("SampleProject", pmResult.ProjectName);
            Assert.False(pmResult.OpenedWithoutProject);

            Assert.True(await service.CopyTextToClipboardAsync("PathID: 12345"));
            Assert.Equal("PathID: 12345", fake.ClipboardText);

            Assert.True(await service.OpenPathInExplorerAsync(tempRoot));
            Assert.Equal(tempRoot, fake.LastOpenedExplorerPath);

            Assert.True(await service.RevealFileInExplorerAsync(sampleFile1));
            Assert.Equal(sampleFile1, fake.LastOpenedExplorerPath);

            Assert.True(await service.OpenUrlAsync("https://github.com"));
            Assert.Equal("https://github.com", fake.LastOpenedUrl);
        }

        private sealed class FakeDialogService : IDialogService
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
            public string? ClipboardText { get; private set; }
            public string? LastOpenedExplorerPath { get; private set; }
            public string? LastOpenedUrl { get; private set; }

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
            {
                return Task.FromResult(ConfirmResult);
            }

            public Task<RiskyLoadChoice> ShowRiskyLoadDialogAsync(string title, string message)
            {
                return Task.FromResult(RiskyLoadResult);
            }

            public Task<MemoryPressureResult> ShowMemoryPressureDialogAsync(string title, string message)
            {
                return Task.FromResult(MemoryPressureResult);
            }

            public Task<bool> ShowExportOptionsAsync(AvaloniaAppSettings settings)
            {
                return Task.FromResult(ExportOptionsResult);
            }

            public Task<ProjectManagerDialogResult?> ShowProjectManagerAsync(string? activeProjectPath)
            {
                return Task.FromResult(ProjectManagerResult);
            }

            public Task<bool> CopyTextToClipboardAsync(string text)
            {
                ClipboardText = text;
                return Task.FromResult(true);
            }

            public Task<bool> OpenPathInExplorerAsync(string path)
            {
                LastOpenedExplorerPath = path;
                return Task.FromResult(!string.IsNullOrWhiteSpace(path));
            }

            public Task<bool> OpenUrlAsync(string url)
            {
                LastOpenedUrl = url;
                return Task.FromResult(!string.IsNullOrWhiteSpace(url));
            }
        }
    }
}
