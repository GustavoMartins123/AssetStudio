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
using AssetStudio.Avalonia.Services.ProjectAnalysis;
using AssetStudio.Avalonia.ViewModels;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AssetStudio.Tests
{
    public class ProjectNavigationTests
    {
        private static readonly Regex AxamlClickAttributeRegex = new(
            @"\bClick\s*=",
            RegexOptions.CultureInvariant);

        [Fact]
        public async Task MainWindowViewModel_BackToProjectsCommand_UpdatesClosingStatePreventsReentrancyAndResetsOnDispose()
        {
            var vm = new MainWindowViewModel();
            try
            {
                var project = new ManagedProject
                {
                    Id = "proj-alpha-01",
                    Name = "Alpha Game",
                    UseAutoName = false,
                    ProjectRoot = Path.Combine(Path.GetTempPath(), "AlphaGameRoot")
                };
                vm.ProjectSession.SetLoadedProject(project, isLazyMode: true);

                Assert.True(vm.ProjectSession.HasLoadedProject);
                Assert.Equal("proj-alpha-01", vm.ProjectSession.ActiveProjectId);
                Assert.False(vm.ProjectSession.IsClosingProject);
                Assert.False(vm.IsReturningToProjectManager);
                Assert.True(vm.CanOpenProjectManager);
                Assert.True(vm.OpenProjectManagerCommand.CanExecute());

                var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var handlerCalls = 0;
                var canExecuteNotifications = 0;
                vm.OpenProjectManagerCommand.CanExecuteChanged += (_, _) => canExecuteNotifications++;
                vm.CommandHandlers.OpenProjectManagerAsync = async () =>
                {
                    handlerCalls++;
                    await gate.Task;
                };

                var firstExecution = vm.OpenProjectManagerCommand.ExecuteAsync();

                Assert.Equal(1, handlerCalls);
                Assert.True(vm.IsReturningToProjectManager);
                Assert.True(vm.ProjectSession.IsClosingProject);
                Assert.False(vm.CanOpenProjectManager);
                Assert.False(vm.OpenProjectManagerCommand.CanExecute());
                Assert.Equal("Returning to Projects...", vm.StatusText);
                Assert.True(canExecuteNotifications > 0);

                // Re-entrant execution while returning must be ignored.
                await vm.OpenProjectManagerCommand.ExecuteAsync();
                Assert.Equal(1, handlerCalls);

                gate.SetResult(true);
                await firstExecution;

                // When handler finishes without disposing VM, navigation flags reset cleanly.
                Assert.False(vm.IsReturningToProjectManager);
                Assert.False(vm.ProjectSession.IsClosingProject);
                Assert.True(vm.CanOpenProjectManager);
                Assert.True(vm.OpenProjectManagerCommand.CanExecute());

                // Simulating teardown via Dispose resets all state and permanently disables command on dead VM.
                vm.IsIndexingActive = true;
                vm.IndexingProgress.BeginIndexing(totalFiles: 12, currentFile: "level0");
                vm.MediaPlayback.SelectAudio("IntroBgm", "48000 Hz");

                vm.Dispose();

                Assert.Null(vm.CommandHandlers.OpenProjectManagerAsync);
                Assert.False(vm.CanOpenProjectManager);
                Assert.False(vm.OpenProjectManagerCommand.CanExecute());
                Assert.False(vm.IsReturningToProjectManager);
                Assert.False(vm.IsIndexingActive);
                Assert.False(vm.ProjectSession.HasLoadedProject);
                Assert.Equal(string.Empty, vm.ProjectSession.ActiveProjectId);
                Assert.False(vm.ProjectSession.IsClosingProject);
                Assert.Equal(ActiveMediaKind.None, vm.MediaPlayback.MediaKind);

                // Executing after Dispose is a safe no-op.
                await vm.OpenProjectManagerCommand.ExecuteAsync();
                Assert.Equal(1, handlerCalls);
            }
            finally
            {
                vm.Dispose();
            }
        }

        [Fact]
        public async Task MainWindowViewModel_BackToProjectsCommand_HeadlessFallbacks_HandleSwitchResetAndNavigationRequest()
        {
            var fakeDialogs = new StubNavigationDialogService();
            using var vm = new MainWindowViewModel(dialogService: fakeDialogs);

            var rootA = Path.Combine(Path.GetTempPath(), "ProjectNav_A");
            var rootB = Path.Combine(Path.GetTempPath(), "ProjectNav_B");
            var projectA = new ManagedProject
            {
                Id = "proj-a",
                Name = "Game A",
                UseAutoName = false,
                ProjectRoot = rootA
            };
            vm.ProjectSession.SetLoadedProject(projectA, isLazyMode: true);

            // 1) Null dialog result raises pure-MVVM ReturnToProjectManagerRequested with active project ID.
            string? requestedProjectId = null;
            var requestCount = 0;
            vm.ReturnToProjectManagerRequested += id =>
            {
                requestCount++;
                requestedProjectId = id;
            };

            fakeDialogs.ProjectManagerResult = null;
            await vm.OpenProjectManagerCommand.ExecuteAsync();

            Assert.Equal(1, requestCount);
            Assert.Equal("proj-a", requestedProjectId);
            Assert.Equal(rootA, fakeDialogs.LastActiveProjectPath);
            Assert.False(vm.IsReturningToProjectManager);
            Assert.False(vm.ProjectSession.IsClosingProject);

            // 2) Returning another project switches ProjectSession to that project.
            fakeDialogs.ProjectManagerResult = new ProjectManagerDialogResult(
                projectPath: rootB,
                projectId: "proj-b",
                projectName: "Game B");

            await vm.OpenProjectManagerCommand.ExecuteAsync();

            Assert.Equal(1, requestCount);
            Assert.Equal(rootB, vm.ProjectSession.ProjectFolderPath);
            Assert.Equal("Game B", vm.ProjectSession.ProjectDisplayName);
            Assert.True(vm.ProjectSession.HasLoadedProject);

            // 3) OpenedWithoutProject resets active session state cleanly.
            vm.StatusText = "Loaded assets";
            vm.IsIndexingActive = true;
            fakeDialogs.ProjectManagerResult = new ProjectManagerDialogResult(openedWithoutProject: true);

            await vm.OpenProjectManagerCommand.ExecuteAsync();

            Assert.False(vm.ProjectSession.HasLoadedProject);
            Assert.Equal(string.Empty, vm.ProjectSession.ProjectFolderPath);
            Assert.Equal(string.Empty, vm.ProjectSession.ProjectDisplayName);
            Assert.False(vm.IsIndexingActive);
            Assert.Equal("Ready", vm.StatusText);
        }

        [Fact]
        public void MainWindowCommandHandlers_Clear_DetachesEveryRegisteredDelegate()
        {
            var handlers = new MainWindowCommandHandlers();
            var props = typeof(MainWindowCommandHandlers)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanWrite && p.CanRead)
                .ToArray();

            Assert.NotEmpty(props);

            foreach (var prop in props)
            {
                object dummy = prop.PropertyType switch
                {
                    Type t when t == typeof(Func<Task>) => (Func<Task>)(() => Task.CompletedTask),
                    Type t when t == typeof(Action) => (Action)(() => { }),
                    Type t when t == typeof(Action<ClassIDType>) => (Action<ClassIDType>)(_ => { }),
                    Type t when t == typeof(Action<TextAssetDisplayMode>) => (Action<TextAssetDisplayMode>)(_ => { }),
                    Type t when t == typeof(Action<int>) => (Action<int>)(_ => { }),
                    Type t when t == typeof(Action<PreviewChannelMask>) => (Action<PreviewChannelMask>)(_ => { }),
                    _ => throw new InvalidOperationException($"Unexpected command handler delegate type: {prop.PropertyType.FullName}")
                };

                prop.SetValue(handlers, dummy);
                Assert.NotNull(prop.GetValue(handlers));
            }

            handlers.Clear();

            foreach (var prop in props)
            {
                Assert.Null(prop.GetValue(handlers));
            }
        }

        [Fact]
        public void AppWindowNavigation_MultiCycleProjectSwitching_NeverShutsDownEarlyAndExitsOnFinalProjectManagerClose()
        {
            var host = new SimulatedDesktopHost();
            var transitionGate = 0;

            var pm1 = new SimulatedWindow("ProjectManager#1");
            host.OpenInitialMainWindow(pm1);

            // Cycle 1: PM#1 -> Main#1 (Project A)
            var mw1 = Switch(ref transitionGate, host, pm1, "MainWindow#1(A)");
            Assert.False(host.AppShutdownTriggered);
            Assert.Same(mw1, host.MainWindow);
            Assert.Single(host.OpenWindows);
            Assert.True(pm1.IsClosed);

            // Cycle 2: Main#1 -> PM#2 (back to projects; verify exact safe step order!)
            var stepOrder = new List<string>();
            SimulatedWindow? pm2 = null;
            var transitioned = AppWindowNavigation.TryTransition(
                ref transitionGate,
                createNextWindow: () =>
                {
                    stepOrder.Add("create");
                    pm2 = new SimulatedWindow("ProjectManager#2");
                    return pm2;
                },
                setMainWindow: next =>
                {
                    stepOrder.Add("setMain");
                    host.SetMainWindow(next);
                },
                showAndActivateNextWindow: next =>
                {
                    stepOrder.Add("show");
                    host.ShowWindow(next);
                },
                teardownCurrentWindow: () =>
                {
                    stepOrder.Add("teardown");
                    mw1.Teardown();
                },
                closeCurrentWindow: () =>
                {
                    stepOrder.Add("close");
                    host.CloseWindow(mw1);
                },
                onAfterTransition: next =>
                {
                    stepOrder.Add("after");
                    next.AfterTransitionRan = true;
                });

            Assert.True(transitioned);
            Assert.NotNull(pm2);
            Assert.Equal(new[] { "create", "setMain", "show", "teardown", "close", "after" }, stepOrder);
            Assert.Equal(1, mw1.TeardownCount);
            Assert.True(mw1.IsClosed);
            Assert.True(pm2!.AfterTransitionRan);
            Assert.False(host.AppShutdownTriggered);
            Assert.Same(pm2, host.MainWindow);
            Assert.Single(host.OpenWindows);

            // Cycle 3..5: PM#2 -> Main#2 (Project B) -> PM#3 -> Main#3 (Project A again) -> PM#4 -> Main#4 (no project) -> PM#5
            var mw2 = Switch(ref transitionGate, host, pm2, "MainWindow#2(B)");
            var pm3 = Switch(ref transitionGate, host, mw2, "ProjectManager#3", runTeardown: true);
            var mw3 = Switch(ref transitionGate, host, pm3, "MainWindow#3(A)");
            var pm4 = Switch(ref transitionGate, host, mw3, "ProjectManager#4", runTeardown: true);
            var mw4 = Switch(ref transitionGate, host, pm4, "MainWindow#4(None)");
            var pm5 = Switch(ref transitionGate, host, mw4, "ProjectManager#5", runTeardown: true);

            Assert.False(host.AppShutdownTriggered);
            Assert.Same(pm5, host.MainWindow);
            Assert.Single(host.OpenWindows);
            Assert.Equal(1, mw2.TeardownCount);
            Assert.Equal(1, mw3.TeardownCount);
            Assert.Equal(1, mw4.TeardownCount);

            // Closing final ProjectManagerWindow without opening a project exits app cleanly.
            host.CloseWindow(pm5);
            Assert.True(host.AppShutdownTriggered);
            Assert.Empty(host.OpenWindows);
        }

        [Fact]
        public void AppWindowNavigation_CreateFailureAndReentrancy_PreserveCurrentWindowAndReleaseGate()
        {
            var gate = 0;
            var setMainCalled = false;
            var showCalled = false;
            var teardownCalled = false;
            var closeCalled = false;
            var afterCalled = false;

            // Failure during next window creation must never tear down or close the active window.
            Assert.Throws<InvalidOperationException>(() =>
                AppWindowNavigation.TryTransition<SimulatedWindow>(
                    ref gate,
                    createNextWindow: () => throw new InvalidOperationException("Simulated creation failure"),
                    setMainWindow: _ => setMainCalled = true,
                    showAndActivateNextWindow: _ => showCalled = true,
                    teardownCurrentWindow: () => teardownCalled = true,
                    closeCurrentWindow: () => closeCalled = true,
                    onAfterTransition: _ => afterCalled = true));

            Assert.False(setMainCalled);
            Assert.False(showCalled);
            Assert.False(teardownCalled);
            Assert.False(closeCalled);
            Assert.False(afterCalled);
            Assert.Equal(0, gate);

            // Nested re-entrant transition must be rejected while active, then allowed again afterward.
            var nestedResult = true;
            var outerResult = AppWindowNavigation.TryTransition(
                ref gate,
                createNextWindow: () =>
                {
                    nestedResult = AppWindowNavigation.TryTransition(
                        ref gate,
                        createNextWindow: () => new SimulatedWindow("Duplicate"),
                        setMainWindow: _ => { },
                        showAndActivateNextWindow: _ => { },
                        teardownCurrentWindow: null,
                        closeCurrentWindow: () => { });
                    return new SimulatedWindow("Primary");
                },
                setMainWindow: _ => setMainCalled = true,
                showAndActivateNextWindow: _ => showCalled = true,
                teardownCurrentWindow: () => teardownCalled = true,
                closeCurrentWindow: () => closeCalled = true,
                onAfterTransition: _ => afterCalled = true);

            Assert.True(outerResult);
            Assert.False(nestedResult);
            Assert.True(setMainCalled);
            Assert.True(showCalled);
            Assert.True(teardownCalled);
            Assert.True(closeCalled);
            Assert.True(afterCalled);
            Assert.Equal(0, gate);
        }

        [Fact]
        public async Task ProjectManagerViewModel_ReentryPreselectsReturnedProject_PreventsDoubleLaunch_AndSupportsRepeatedCycles()
        {
            var tempRoot = Path.Combine(Path.GetTempPath(), "AssetStudioTests_ProjNav_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
            try
            {
                var storeDir = Path.Combine(tempRoot, "Store");
                var alphaDir = Path.Combine(tempRoot, "Alpha");
                var betaDir = Path.Combine(tempRoot, "Beta");
                Directory.CreateDirectory(alphaDir);
                Directory.CreateDirectory(betaDir);

                var store = new ProjectManagerStore(storeDir);
                var alpha = new ManagedProject { Name = "Alpha", UseAutoName = false, ProjectRoot = alphaDir };
                var beta = new ManagedProject { Name = "Beta", UseAutoName = false, ProjectRoot = betaDir };
                store.SaveProject(alpha);
                store.SaveProject(beta);

                // Cycle 1: Open Alpha, verify double-launch protection across Open & OpenWithoutProject.
                var launchedContexts = new List<ProjectLaunchContext?>();
                using (var pm1 = new ProjectManagerViewModel(
                    store: store,
                    dispatchToUi: action => action(),
                    launchProject: ctx => launchedContexts.Add(ctx)))
                {
                    await pm1.RefreshProjectsAsync(selectProjectId: alpha.Id);
                    Assert.NotNull(pm1.SelectedProject);
                    Assert.Equal(alpha.Id, pm1.SelectedProject!.Id);
                    Assert.True(pm1.CanOpen);
                    Assert.True(pm1.CanOpenWithoutProject);

                    await pm1.OpenSelectedProjectAsync();
                    Assert.Single(launchedContexts);
                    Assert.True(pm1.HasLaunched);
                    Assert.False(pm1.CanOpen);
                    Assert.False(pm1.CanOpenWithoutProject);

                    // Repeated invocations after launch must not spawn duplicate MainWindows.
                    await pm1.OpenSelectedProjectAsync();
                    await pm1.OpenProjectByIdAsync(beta.Id);
                    pm1.OpenWithoutProject();
                    Assert.Single(launchedContexts);
                }

                // Cycle 2: Return to fresh ProjectManagerViewModel pre-selecting Alpha, then open Beta.
                using (var pm2 = new ProjectManagerViewModel(
                    store: store,
                    dispatchToUi: action => action(),
                    launchProject: ctx => launchedContexts.Add(ctx)))
                {
                    await pm2.RefreshProjectsAsync(selectProjectId: alpha.Id);
                    Assert.Equal(alpha.Id, pm2.SelectedProject?.Id);

                    var betaEntry = pm2.Projects.Single(p => p.Id == beta.Id);
                    pm2.SelectedProject = betaEntry;
                    await pm2.OpenSelectedProjectAsync();

                    Assert.Equal(2, launchedContexts.Count);
                    Assert.Equal(beta.Id, launchedContexts[1]?.Project.Id);
                }

                // Cycle 3: Return to fresh ProjectManagerViewModel pre-selecting Beta, then open without project.
                using (var pm3 = new ProjectManagerViewModel(
                    store: store,
                    dispatchToUi: action => action(),
                    launchProject: ctx => launchedContexts.Add(ctx)))
                {
                    await pm3.RefreshProjectsAsync(selectProjectId: beta.Id);
                    Assert.Equal(beta.Id, pm3.SelectedProject?.Id);

                    pm3.OpenWithoutProject();
                    pm3.OpenWithoutProject(); // Double click must be ignored.

                    Assert.Equal(3, launchedContexts.Count);
                    Assert.Null(launchedContexts[2]);
                }
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                try
                {
                    if (Directory.Exists(tempRoot))
                    {
                        Directory.Delete(tempRoot, recursive: true);
                    }
                }
                catch
                {
                }
            }
        }

        [Fact]
        public async Task ProjectManagerViewModel_RejectedOrFailedLaunch_ResetsHasLaunchedAndReenablesCommands()
        {
            var tempRoot = Path.Combine(Path.GetTempPath(), "AssetStudioTests_ProjLaunchFail_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
            try
            {
                var storeDir = Path.Combine(tempRoot, "Store");
                var alphaDir = Path.Combine(tempRoot, "Alpha");
                Directory.CreateDirectory(alphaDir);

                var store = new ProjectManagerStore(storeDir);
                var alpha = new ManagedProject { Name = "Alpha", UseAutoName = false, ProjectRoot = alphaDir };
                store.SaveProject(alpha);

                var errors = new List<(string Message, string Title)>();
                var requestCloseCount = 0;
                var mode = "reject";
                var successfulLaunches = 0;

                ProjectManagerViewModel? vmRef = null;
                using var vm = new ProjectManagerViewModel(
                    store: store,
                    dispatchToUi: action => action(),
                    showError: (msg, title) => errors.Add((msg, title)),
                    launchProject: _ =>
                    {
                        if (mode == "reject")
                        {
                            // Mirrors App.NavigateToMainWindow returning false.
                            vmRef!.CancelLaunch();
                            return;
                        }

                        if (mode == "throw")
                        {
                            throw new InvalidOperationException("Window creation failed");
                        }

                        successfulLaunches++;
                    });
                vmRef = vm;
                vm.RequestClose += _ => requestCloseCount++;

                await vm.RefreshProjectsAsync(selectProjectId: alpha.Id);
                Assert.True(vm.CanOpen);
                Assert.True(vm.CanOpenWithoutProject);

                // 1) Rejected transition via CancelLaunch() must re-enable Open / OpenWithoutProject and NOT fire RequestClose.
                await vm.OpenSelectedProjectAsync();
                Assert.False(vm.HasLaunched);
                Assert.Null(vm.OpenedLaunchContext);
                Assert.True(vm.CanOpen);
                Assert.True(vm.CanOpenWithoutProject);
                Assert.Equal(0, requestCloseCount);
                Assert.Empty(errors);

                // 2) Exception thrown during launchProject on OpenSelectedProjectAsync must reset HasLaunched, report error, and stay usable.
                mode = "throw";
                await vm.OpenSelectedProjectAsync();
                Assert.False(vm.HasLaunched);
                Assert.Null(vm.OpenedLaunchContext);
                Assert.True(vm.CanOpen);
                Assert.True(vm.CanOpenWithoutProject);
                Assert.Equal(0, requestCloseCount);
                Assert.Single(errors);

                // 3) Exception thrown during OpenWithoutProject must also reset HasLaunched cleanly.
                Assert.Throws<InvalidOperationException>(() => vm.OpenWithoutProject());
                Assert.False(vm.HasLaunched);
                Assert.True(vm.CanOpen);
                Assert.True(vm.CanOpenWithoutProject);
                Assert.Equal(0, requestCloseCount);

                // 4) Subsequent retry succeeds cleanly.
                mode = "succeed";
                await vm.OpenSelectedProjectAsync();
                Assert.True(vm.HasLaunched);
                Assert.NotNull(vm.OpenedLaunchContext);
                Assert.False(vm.CanOpen);
                Assert.False(vm.CanOpenWithoutProject);
                Assert.Equal(1, successfulLaunches);
                Assert.Equal(1, requestCloseCount);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                try
                {
                    if (Directory.Exists(tempRoot))
                    {
                        Directory.Delete(tempRoot, recursive: true);
                    }
                }
                catch
                {
                }
            }
        }

        [Fact]
        public void MainWindow_BackToProjectsUi_UsesPureMvvmBindingsShortcutTopBarAndSafeStaticTeardown()
        {
            var repoRoot = LocateRepositoryRoot();
            var axamlPath = Path.Combine(repoRoot, "AssetStudio.Avalonia", "MainWindow.axaml");
            var codeBehindPath = Path.Combine(repoRoot, "AssetStudio.Avalonia", "MainWindow.axaml.cs");
            var shortcutsPath = Path.Combine(repoRoot, "AssetStudio.Avalonia", "MainWindow.KeyboardShortcuts.cs");

            var axaml = File.ReadAllText(axamlPath);
            var codeBehind = File.ReadAllText(codeBehindPath);
            var shortcuts = File.ReadAllText(shortcutsPath);

            // Pure MVVM & compiled bindings invariants preserved.
            Assert.Contains("x:CompileBindings=\"True\"", axaml, StringComparison.Ordinal);
            Assert.Contains("x:DataType=\"vm:MainWindowViewModel\"", axaml, StringComparison.Ordinal);
            Assert.False(AxamlClickAttributeRegex.IsMatch(axaml), "MainWindow.axaml must contain zero Click= handlers.");

            // Window KeyBinding + File menu item + always-visible top-bar button all bind to OpenProjectManagerCommand.
            Assert.Contains("Gesture=\"Ctrl+Shift+P\"", axaml, StringComparison.Ordinal);
            Assert.Contains("x:Name=\"backToProjectsMenuItem\"", axaml, StringComparison.Ordinal);
            Assert.Contains("InputGesture=\"Ctrl+Shift+P\"", axaml, StringComparison.Ordinal);
            Assert.Contains("x:Name=\"BackToProjectsButton\"", axaml, StringComparison.Ordinal);
            var commandBindingCount = Regex.Matches(axaml, @"Command=""\{Binding OpenProjectManagerCommand\}""").Count;
            Assert.True(commandBindingCount >= 3, $"Expected KeyBinding + File menu item + top bar button bound to OpenProjectManagerCommand, found {commandBindingCount}.");

            // Shortcut Ctrl+Shift+P does not conflict with Key.P in MainWindow.KeyboardShortcuts.cs.
            Assert.DoesNotContain("Key.P", shortcuts, StringComparison.Ordinal);

            // Code-behind wires OpenProjectManagerAsync and safely restores all static references on teardown.
            Assert.Contains("handlers.OpenProjectManagerAsync = ReturnToProjectManagerCommandHandlerAsync;", codeBehind, StringComparison.Ordinal);
            Assert.Contains("TeardownProjectSession()", codeBehind, StringComparison.Ordinal);
            Assert.Contains("ReferenceEquals(Logger.Default, logger)", codeBehind, StringComparison.Ordinal);
            Assert.Contains("ReferenceEquals(Progress.Default, _progressReporter)", codeBehind, StringComparison.Ordinal);
            Assert.Contains("ReferenceEquals(AssetsManager.ShouldYieldForUserInteraction, _shouldYieldForUserInteractionDelegate)", codeBehind, StringComparison.Ordinal);
            Assert.Contains("ReferenceEquals(AssetsManager.MemoryPressureCallback, _memoryPressureCallback)", codeBehind, StringComparison.Ordinal);
        }

        private static SimulatedWindow Switch(
            ref int gate,
            SimulatedDesktopHost host,
            SimulatedWindow current,
            string nextName,
            bool runTeardown = false)
        {
            SimulatedWindow? created = null;
            var ok = AppWindowNavigation.TryTransition(
                ref gate,
                createNextWindow: () =>
                {
                    created = new SimulatedWindow(nextName);
                    return created;
                },
                setMainWindow: host.SetMainWindow,
                showAndActivateNextWindow: host.ShowWindow,
                teardownCurrentWindow: runTeardown ? current.Teardown : null,
                closeCurrentWindow: () => host.CloseWindow(current),
                onAfterTransition: next => next.AfterTransitionRan = true);

            Assert.True(ok);
            Assert.NotNull(created);
            return created!;
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

        private sealed class SimulatedWindow
        {
            public SimulatedWindow(string name)
            {
                Name = name;
            }

            public string Name { get; }
            public bool IsClosed { get; set; }
            public bool AfterTransitionRan { get; set; }
            public int TeardownCount { get; private set; }
            private bool _tornDown;

            public void Teardown()
            {
                if (_tornDown)
                {
                    return;
                }

                _tornDown = true;
                TeardownCount++;
            }
        }

        private sealed class SimulatedDesktopHost
        {
            public List<SimulatedWindow> OpenWindows { get; } = new();
            public SimulatedWindow? MainWindow { get; private set; }
            public bool AppShutdownTriggered { get; private set; }

            public void OpenInitialMainWindow(SimulatedWindow window)
            {
                MainWindow = window;
                OpenWindows.Add(window);
            }

            public void SetMainWindow(SimulatedWindow window)
            {
                MainWindow = window;
            }

            public void ShowWindow(SimulatedWindow window)
            {
                if (!OpenWindows.Contains(window))
                {
                    OpenWindows.Add(window);
                }
            }

            public void CloseWindow(SimulatedWindow window)
            {
                window.Teardown();
                window.IsClosed = true;
                OpenWindows.Remove(window);

                // Mirrors BOTH OnMainWindowClose AND OnLastWindowClose shutdown triggers.
                if (ReferenceEquals(MainWindow, window) || OpenWindows.Count == 0)
                {
                    AppShutdownTriggered = true;
                }
            }
        }

        private sealed class StubNavigationDialogService : IDialogService
        {
            public ProjectManagerDialogResult? ProjectManagerResult { get; set; }
            public string? LastActiveProjectPath { get; private set; }

            public Task<IReadOnlyList<string>> PickOpenFilesAsync(FilePickerRequest request)
                => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

            public Task<string?> PickOpenFolderAsync(FolderPickerRequest request)
                => Task.FromResult<string?>(null);

            public Task<string?> PickSaveFileAsync(SaveFilePickerRequest request)
                => Task.FromResult<string?>(null);

            public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
            public Task ShowErrorAsync(string title, string message) => Task.CompletedTask;
            public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(false);
            public Task<RiskyLoadChoice> ShowRiskyLoadDialogAsync(string title, string message) => Task.FromResult(RiskyLoadChoice.Cancel);
            public Task<bool> ShowExportOptionsAsync(AvaloniaAppSettings settings) => Task.FromResult(false);

            public Task<ProjectManagerDialogResult?> ShowProjectManagerAsync(string? activeProjectPath)
            {
                LastActiveProjectPath = activeProjectPath;
                return Task.FromResult(ProjectManagerResult);
            }

            public Task<bool> CopyTextToClipboardAsync(string text) => Task.FromResult(false);
            public Task<bool> OpenPathInExplorerAsync(string path) => Task.FromResult(false);
            public Task<bool> OpenUrlAsync(string url) => Task.FromResult(false);
        }
    }
}
