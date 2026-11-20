using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using AssetStudio;
using AssetStudio.Avalonia;
using AssetStudio.Avalonia.Services.ProjectAnalysis;
using AssetStudio.Avalonia.ViewModels;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AssetStudio.Tests
{
    public class SecondaryWindowViewModelTests
    {
        // ------------------------------------------------------------------
        // Canonical RelayCommand / AsyncRelayCommand tests for secondary VMs
        // ------------------------------------------------------------------

        [Fact]
        public void SecondaryRelayCommand_RespectsCanExecuteAndRaisesChanged()
        {
            var allow = false;
            var executed = 0;
            var canExecuteChangedCount = 0;

            var cmd = new RelayCommand(() => executed++, () => allow);
            cmd.CanExecuteChanged += (_, _) => canExecuteChangedCount++;

            Assert.False(cmd.CanExecute(null));
            cmd.Execute(null);
            Assert.Equal(0, executed);

            allow = true;
            cmd.NotifyCanExecuteChanged();
            Assert.Equal(1, canExecuteChangedCount);
            Assert.True(cmd.CanExecute(null));

            cmd.Execute(null);
            Assert.Equal(1, executed);
        }

        [Fact]
        public async Task SecondaryAsyncCommand_BlocksReentrancyAndCapturesFireAndForgetErrors()
        {
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var invocations = 0;

            var cmd = new AsyncRelayCommand(async () =>
            {
                invocations++;
                await gate.Task;
            });

            var firstRun = cmd.ExecuteAsync();
            Assert.True(cmd.IsRunning);
            Assert.False(cmd.CanExecute(null));

            // Re-entrant call while executing should be ignored
            await cmd.ExecuteAsync();
            Assert.Equal(1, invocations);

            gate.SetResult(true);
            await firstRun;

            Assert.False(cmd.IsRunning);
            Assert.True(cmd.CanExecute(null));

            Exception? captured = null;
            var errorRecorded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var failingCmd = new AsyncRelayCommand(
                execute: () => Task.FromException(new InvalidOperationException("boom")),
                onException: ex =>
                {
                    captured = ex;
                    errorRecorded.TrySetResult(true);
                });

            failingCmd.Execute(null);
            var completed = await Task.WhenAny(errorRecorded.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.Same(errorRecorded.Task, completed);
            Assert.IsType<InvalidOperationException>(captured);
            Assert.Equal("boom", captured!.Message);
        }

        // ------------------------------------------------------------------
        // ExportOptionsViewModel tests
        // ------------------------------------------------------------------

        [Fact]
        public void ExportOptionsViewModel_LoadModifySave_AppliesAllOptionsToSettingsAndEmitsCloseTrue()
        {
            var settings = new AvaloniaAppSettings
            {
                ExportOptions = new ExportOptionsState
                {
                    AssetGrouping = AssetGroupOption.Container,
                    ConvertTexture = false,
                    ConvertTextureFormat = ImageFormat.Bmp,
                    ConvertAudio = false,
                    RestoreExtensionName = false,
                    OpenAfterExport = false,
                    ExportSkins = false,
                    ExportAnimations = false,
                    ExportAnimationsSplit = true,
                    ExportBlendShape = false,
                    CastToBone = true,
                    CombineMeshes = true,
                    BoneSize = 25.5m,
                    ScaleFactor = 2.5m,
                    FbxVersion = 5,
                    FbxFormat = 0,
                    ExportUnityMeta = false,
                    ExportUnityAnimClips = false
                }
            };

            var vm = new ExportOptionsViewModel(settings);

            // Verify every option loaded from settings
            Assert.Equal(AssetGroupOption.Container, vm.AssetGrouping);
            Assert.Equal(1, vm.AssetGroupingIndex);
            Assert.False(vm.ConvertTexture);
            Assert.Equal(ImageFormat.Bmp, vm.ConvertTextureFormat);
            Assert.Equal(nameof(ImageFormat.Bmp), vm.SelectedTextureFormatName);
            Assert.False(vm.ConvertAudio);
            Assert.False(vm.RestoreExtensionName);
            Assert.False(vm.OpenAfterExport);
            Assert.False(vm.ExportSkins);
            Assert.False(vm.ExportAnimations);
            Assert.True(vm.ExportAnimationsSplit);
            Assert.False(vm.ExportBlendShape);
            Assert.True(vm.CastToBone);
            Assert.True(vm.CombineMeshes);
            Assert.Equal(25.5m, vm.BoneSize);
            Assert.Equal("25.5", vm.BoneSizeText);
            Assert.Equal(2.5m, vm.ScaleFactor);
            Assert.Equal("2.5", vm.ScaleFactorText);
            Assert.Equal(5, vm.FbxVersion);
            Assert.Equal(0, vm.FbxFormat);
            Assert.False(vm.ExportUnityMeta);
            Assert.False(vm.ExportUnityAnimClips);
            Assert.True(vm.CanSave);
            Assert.False(vm.HasValidationMessage);

            // Modify every single option
            vm.AssetGroupingIndex = (int)AssetGroupOption.SourceFile;
            vm.ConvertTexture = true;
            vm.SelectedTextureFormatName = nameof(ImageFormat.Tga);
            vm.ConvertAudio = true;
            vm.RestoreExtensionName = true;
            vm.OpenAfterExport = true;
            vm.ExportSkins = true;
            vm.ExportAnimations = true;
            vm.ExportAnimationsSplit = false;
            vm.ExportBlendShape = true;
            vm.CastToBone = false;
            vm.CombineMeshes = false;
            vm.BoneSizeText = "12.25";
            vm.ScaleFactorText = "0.75";
            vm.FbxVersion = 2;
            vm.FbxFormat = 1;
            vm.ExportUnityMeta = true;
            vm.ExportUnityAnimClips = true;

            // Settings must NOT be mutated before SaveCommand executes
            Assert.Equal(AssetGroupOption.Container, settings.ExportOptions.AssetGrouping);
            Assert.Equal(25.5m, settings.ExportOptions.BoneSize);

            bool? closeResult = null;
            vm.RequestClose += saved => closeResult = saved;

            Assert.True(vm.SaveCommand.CanExecute(null));
            vm.SaveCommand.Execute(null);

            Assert.True(closeResult);
            Assert.Same(settings.ExportOptions, vm.Result);

            var applied = settings.ExportOptions;
            Assert.Equal(AssetGroupOption.SourceFile, applied.AssetGrouping);
            Assert.True(applied.ConvertTexture);
            Assert.Equal(ImageFormat.Tga, applied.ConvertTextureFormat);
            Assert.True(applied.ConvertAudio);
            Assert.True(applied.RestoreExtensionName);
            Assert.True(applied.OpenAfterExport);
            Assert.True(applied.ExportSkins);
            Assert.True(applied.ExportAnimations);
            Assert.False(applied.ExportAnimationsSplit);
            Assert.True(applied.ExportBlendShape);
            Assert.False(applied.CastToBone);
            Assert.False(applied.CombineMeshes);
            Assert.Equal(12.25m, applied.BoneSize);
            Assert.Equal(0.75m, applied.ScaleFactor);
            Assert.Equal(2, applied.FbxVersion);
            Assert.Equal(1, applied.FbxFormat);
            Assert.True(applied.ExportUnityMeta);
            Assert.True(applied.ExportUnityAnimClips);
        }

        [Fact]
        public void ExportOptionsViewModel_CancelDoesNotMutateSettings_AndResetDefaultsRestoresDefaults()
        {
            var state = new ExportOptionsState
            {
                AssetGrouping = AssetGroupOption.None,
                ConvertTexture = false,
                ConvertTextureFormat = ImageFormat.Jpeg,
                CombineMeshes = true,
                BoneSize = 42m,
                ScaleFactor = 3.5m,
                FbxVersion = 0,
                FbxFormat = 0,
                ExportUnityMeta = false
            };

            var vm = new ExportOptionsViewModel(state);
            vm.AssetGrouping = AssetGroupOption.TypeName;
            vm.ConvertTexture = true;
            vm.BoneSize = 99m;
            vm.ScaleFactor = 8m;

            bool? closeResult = null;
            vm.RequestClose += saved => closeResult = saved;
            vm.CancelCommand.Execute(null);

            Assert.False(closeResult);
            Assert.Null(vm.Result);
            // Original state remains untouched
            Assert.Equal(AssetGroupOption.None, state.AssetGrouping);
            Assert.False(state.ConvertTexture);
            Assert.Equal(ImageFormat.Jpeg, state.ConvertTextureFormat);
            Assert.True(state.CombineMeshes);
            Assert.Equal(42m, state.BoneSize);
            Assert.Equal(3.5m, state.ScaleFactor);

            // ResetDefaults resets ViewModel properties to factory defaults without touching original state yet
            vm.ResetDefaultsCommand.Execute(null);
            var defaults = new ExportOptionsState();
            Assert.Equal(defaults.AssetGrouping, vm.AssetGrouping);
            Assert.Equal(defaults.ConvertTexture, vm.ConvertTexture);
            Assert.Equal(defaults.ConvertTextureFormat, vm.ConvertTextureFormat);
            Assert.Equal(defaults.CombineMeshes, vm.CombineMeshes);
            Assert.Equal(defaults.BoneSize, vm.BoneSize);
            Assert.Equal(defaults.ScaleFactor, vm.ScaleFactor);
            Assert.Equal(defaults.FbxVersion, vm.FbxVersion);
            Assert.Equal(defaults.FbxFormat, vm.FbxFormat);
            Assert.Equal(defaults.ExportUnityMeta, vm.ExportUnityMeta);
            Assert.Equal(defaults.ExportUnityAnimClips, vm.ExportUnityAnimClips);
            Assert.Equal(42m, state.BoneSize);
        }

        [Fact]
        public void ExportOptionsViewModel_ValidationBlocksInvalidNumericInputsAndClampsIndices()
        {
            var vm = new ExportOptionsViewModel();

            vm.BoneSizeText = "not-a-number";
            Assert.False(vm.CanSave);
            Assert.False(vm.SaveCommand.CanExecute(null));
            Assert.True(vm.HasValidationMessage);
            Assert.Contains("Bone size", vm.ValidationMessage, StringComparison.OrdinalIgnoreCase);

            vm.BoneSizeText = "-5";
            Assert.False(vm.CanSave);

            vm.BoneSizeText = "0";
            Assert.True(vm.CanSave);
            Assert.Equal(0m, vm.BoneSize);

            vm.ScaleFactorText = "0";
            Assert.False(vm.CanSave);
            Assert.Contains("Scale factor", vm.ValidationMessage, StringComparison.OrdinalIgnoreCase);

            vm.ScaleFactorText = "-1.5";
            Assert.False(vm.CanSave);

            vm.ScaleFactorText = "2.25";
            Assert.True(vm.CanSave);
            Assert.False(vm.HasValidationMessage);
            Assert.Equal(2.25m, vm.ScaleFactor);

            vm.AssetGroupingIndex = 99;
            Assert.Equal(3, vm.AssetGroupingIndex);
            vm.AssetGroupingIndex = -5;
            Assert.Equal(0, vm.AssetGroupingIndex);

            vm.FbxVersion = 100;
            Assert.Equal(vm.FbxVersionOptions.Count - 1, vm.FbxVersion);
            vm.FbxFormat = -1;
            Assert.Equal(0, vm.FbxFormat);
        }

        // ------------------------------------------------------------------
        // ProjectEditorViewModel tests
        // ------------------------------------------------------------------

        [Fact]
        public async Task ProjectEditorViewModel_ValidationBrowseSaveAndCancel_WorkCrossPlatform()
        {
            var tempRoot = CreateTempDirectory("ProjectEditorVm");
            try
            {
                var gameDir = Path.Combine(tempRoot, "StarfallChronicles");
                Directory.CreateDirectory(gameDir);
                var iconFile = Path.Combine(gameDir, "game_icon.png");
                File.WriteAllBytes(iconFile, new byte[] { 0x89, 0x50, 0x4E, 0x47 });

                var missingDir = Path.Combine(tempRoot, "DoesNotExistFolder");

                var vm = new ProjectEditorViewModel(
                    store: null,
                    project: null,
                    pickRootFolderAsync: () => Task.FromResult<string?>(gameDir),
                    pickIconFileAsync: () => Task.FromResult<string?>(iconFile),
                    findProjectIcon: root => root == gameDir ? iconFile : null);

                // Empty name + empty root -> cannot save
                Assert.Equal("Add project", vm.WindowTitle);
                Assert.False(vm.IsEditMode);
                Assert.False(vm.CanSave);
                Assert.False(vm.SaveCommand.CanExecute(null));
                Assert.True(vm.HasValidationMessage);

                // Invalid / non-existent project root -> cannot save
                vm.Name = "CustomName";
                Assert.True(vm.CanSave); // name-only project without root is valid

                vm.ProjectRoot = missingDir;
                Assert.False(vm.CanSave);
                Assert.True(vm.HasValidationMessage);

                // Null character is an invalid path char on both Windows and Linux
                vm.ProjectRoot = "bad\0path";
                Assert.False(vm.CanSave);
                Assert.True(vm.HasValidationMessage);

                // Reset and test browse root injection (auto-fills name & detects icon)
                vm.Name = string.Empty;
                vm.ProjectRoot = string.Empty;
                vm.IconPath = string.Empty;
                Assert.False(vm.CanSave);

                await vm.BrowseRootAsync();

                Assert.Equal(gameDir, vm.ProjectRoot);
                Assert.Equal("StarfallChronicles", vm.Name);
                Assert.Equal(iconFile, vm.IconPath);
                Assert.Equal(iconFile, vm.IconPreviewPath);
                Assert.True(vm.CanSave);
                Assert.False(vm.HasValidationMessage);

                // Save emits RequestClose(true) + populated model
                bool? closedWith = null;
                vm.RequestClose += saved => closedWith = saved;

                vm.UseAutoName = false;
                Assert.Contains("custom name", vm.StatusText, StringComparison.OrdinalIgnoreCase);

                vm.SaveCommand.Execute(null);
                Assert.True(closedWith);
                Assert.NotNull(vm.Result);
                Assert.Equal("StarfallChronicles", vm.Result!.Name);
                Assert.False(vm.Result.UseAutoName);
                Assert.Equal(gameDir, vm.Result.ProjectRoot);
                Assert.Equal(gameDir, vm.Result.LastLoadPath);
                Assert.Equal(iconFile, vm.Result.PendingIconSourcePath);

                // Edit mode + Cancel does not mutate original project and emits RequestClose(false)
                var original = new ManagedProject
                {
                    Id = "proj-1",
                    Name = "OriginalName",
                    ProjectRoot = gameDir,
                    UseAutoName = true
                };

                var editVm = new ProjectEditorViewModel(store: null, project: original);
                Assert.Equal("Edit project", editVm.WindowTitle);
                Assert.True(editVm.IsEditMode);
                Assert.True(editVm.CanSave);

                editVm.Name = "ModifiedButCancelled";
                bool? editClosedWith = null;
                editVm.RequestClose += saved => editClosedWith = saved;
                editVm.CancelCommand.Execute(null);

                Assert.False(editClosedWith);
                Assert.Null(editVm.Result);
                Assert.Equal("OriginalName", original.Name);
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public async Task ProjectEditorViewModel_BrowseIconAndAutoNameFromRootOnSave()
        {
            var tempRoot = CreateTempDirectory("ProjectEditorIcon");
            try
            {
                var projectDir = Path.Combine(tempRoot, "DungeonCrawler");
                Directory.CreateDirectory(projectDir);
                var pickedIcon = Path.Combine(tempRoot, "chosen.png");
                File.WriteAllBytes(pickedIcon, new byte[] { 1, 2, 3, 4 });

                var vm = new ProjectEditorViewModel(
                    store: null,
                    project: null,
                    pickIconFileAsync: () => Task.FromResult<string?>(pickedIcon),
                    findProjectIcon: _ => null);

                // DetectIcon without existing root sets status message
                vm.DetectIconCommand.Execute(null);
                Assert.Contains("existing project root", vm.StatusText, StringComparison.OrdinalIgnoreCase);

                // Setting only an existing ProjectRoot allows Save and derives Name from folder
                vm.ProjectRoot = projectDir;
                Assert.True(vm.CanSave);

                vm.DetectIconCommand.Execute(null);
                Assert.Contains("No icon-like image", vm.StatusText, StringComparison.OrdinalIgnoreCase);

                await vm.BrowseIconAsync();
                Assert.Equal(pickedIcon, vm.IconPath);
                Assert.Equal(pickedIcon, vm.IconPreviewPath);

                vm.SaveCommand.Execute(null);
                Assert.NotNull(vm.Result);
                Assert.Equal("DungeonCrawler", vm.Result!.Name);
                Assert.Equal(pickedIcon, vm.Result.PendingIconSourcePath);
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        // ------------------------------------------------------------------
        // ProjectManagerViewModel tests
        // ------------------------------------------------------------------

        [Fact]
        public async Task ProjectManagerViewModel_FullLifecycle_CreateFilterEditOpenDeleteAndPreferences()
        {
            var tempRoot = CreateTempDirectory("ProjectManagerVm");
            try
            {
                var storeDir = Path.Combine(tempRoot, "Store");
                var alphaDir = Path.Combine(tempRoot, "AlphaGame");
                var betaDir = Path.Combine(tempRoot, "BetaGame");
                var dumperPath = Path.Combine(tempRoot, "Il2CppDumper");
                Directory.CreateDirectory(alphaDir);
                Directory.CreateDirectory(betaDir);

                var store = new ProjectManagerStore(storeDir);

                ManagedProject? nextEditorResult = null;
                bool confirmDeleteAnswer = true;
                string? pickedDumperPath = dumperPath;
                var errors = new List<(string Message, string Title)>();
                ProjectLaunchContext? launchedContext = null;
                var launchCount = 0;
                bool? closeRequested = null;

                using var vm = new ProjectManagerViewModel(
                    store: store,
                    dispatchToUi: action => action(),
                    editProjectAsync: _ => Task.FromResult(nextEditorResult),
                    confirmDeleteAsync: _ => Task.FromResult(confirmDeleteAnswer),
                    pickIl2CppDumperPathAsync: () => Task.FromResult<string?>(pickedDumperPath),
                    showError: (msg, title) => errors.Add((msg, title)),
                    launchProject: ctx =>
                    {
                        launchCount++;
                        launchedContext = ctx;
                    });

                vm.RequestClose += launched => closeRequested = launched;

                // 1) Initial empty store state
                await vm.RefreshProjectsAsync();
                Assert.True(vm.IsEmpty);
                Assert.Equal("No projects yet.", vm.EmptyListMessage);
                Assert.Null(vm.SelectedProject);
                Assert.False(vm.CanOpen);
                Assert.False(vm.CanEdit);
                Assert.False(vm.CanDelete);
                Assert.False(vm.CanRerunAnalysis);
                Assert.False(vm.CanEditAnalysisPreferences);
                Assert.True(vm.CanAdd);
                Assert.True(vm.CanOpenWithoutProject);
                Assert.Equal("Select a project", vm.DetailName);
                Assert.Equal("-", vm.DetailRoot);

                // 2) Create two projects through injected editor callback
                nextEditorResult = new ManagedProject
                {
                    Name = "Alpha Project",
                    UseAutoName = false,
                    ProjectRoot = alphaDir
                };
                await vm.AddProjectAsync();

                nextEditorResult = new ManagedProject
                {
                    Name = "Beta RPG",
                    UseAutoName = true,
                    ProjectRoot = betaDir
                };
                await vm.AddProjectAsync();

                Assert.Equal(2, vm.Projects.Count);
                Assert.False(vm.IsEmpty);
                Assert.NotNull(vm.SelectedProject);
                Assert.Equal("Beta RPG", vm.SelectedProject!.DisplayName);
                Assert.True(vm.CanOpen);
                Assert.True(vm.CanEdit);
                Assert.True(vm.CanDelete);
                Assert.True(vm.CanRerunAnalysis);
                Assert.True(vm.CanEditAnalysisPreferences);
                Assert.Equal(betaDir, vm.DetailRoot);

                // 3) SearchText filtering
                vm.SearchText = "alpha";
                Assert.Single(vm.Projects);
                Assert.Equal("Alpha Project", vm.Projects[0].DisplayName);
                Assert.Equal("Alpha Project", vm.SelectedProject!.DisplayName);
                Assert.Equal("Custom project name.", vm.DetailSubtitle);

                vm.SearchText = "nonexistent-match-xyz";
                Assert.Empty(vm.Projects);
                Assert.True(vm.IsEmpty);
                Assert.Equal("No matching projects.", vm.EmptyListMessage);
                Assert.Null(vm.SelectedProject);
                Assert.False(vm.CanOpen);
                Assert.False(vm.CanEdit);
                Assert.False(vm.CanDelete);

                vm.SearchText = string.Empty;
                Assert.Equal(2, vm.Projects.Count);
                Assert.NotNull(vm.SelectedProject);

                // 4) Edit selected project
                var alphaEntry = vm.Projects.Single(p => p.DisplayName == "Alpha Project");
                vm.SelectedProject = alphaEntry;

                var editedClone = alphaEntry.Project.Clone();
                editedClone.Name = "Alpha Remastered";
                nextEditorResult = editedClone;
                await vm.EditSelectedProjectAsync();

                Assert.Equal("Alpha Remastered", vm.SelectedProject!.DisplayName);
                Assert.Equal("Alpha Remastered", vm.DetailName);

                // 5) Analysis preferences (Il2CppDumper toggle + browse path persistence)
                vm.AllowIl2CppDumper = true;
                if (vm.LastSavePreferencesTask != null)
                {
                    await vm.LastSavePreferencesTask;
                }
                await vm.BrowseIl2CppDumperAsync();
                Assert.Equal(dumperPath, vm.Il2CppDumperPath);

                var persistedAlpha = store.GetProject(alphaEntry.Id);
                Assert.NotNull(persistedAlpha);
                Assert.True(persistedAlpha!.AllowAutoIl2CppDumper);
                Assert.Equal(dumperPath, persistedAlpha.Il2CppDumperPath);

                // 6) Delete confirmation message + cancel vs confirm delete
                var confirmMessage = ProjectManagerViewModel.BuildDeleteConfirmationMessage(vm.SelectedProject);
                Assert.Contains("Alpha Remastered", confirmMessage);
                Assert.Contains(alphaDir, confirmMessage);

                confirmDeleteAnswer = false;
                await vm.DeleteSelectedProjectAsync();
                Assert.Equal(2, vm.Projects.Count);

                confirmDeleteAnswer = true;
                await vm.DeleteSelectedProjectAsync();
                if (vm.LastBackgroundCleanupTask != null)
                {
                    await vm.LastBackgroundCleanupTask;
                }

                Assert.Single(vm.Projects);
                Assert.Equal("Beta RPG", vm.Projects[0].DisplayName);
                Assert.Equal("Beta RPG", vm.SelectedProject!.DisplayName);
                Assert.Null(store.GetProject(alphaEntry.Id));

                // 7) OpenCommand launches selected project and emits RequestClose(true)
                await vm.OpenSelectedProjectAsync();

                Assert.Equal(1, launchCount);
                Assert.True(vm.HasLaunched);
                Assert.NotNull(launchedContext);
                Assert.NotNull(vm.OpenedLaunchContext);
                Assert.Equal("Beta RPG", launchedContext!.Project.DisplayName);
                Assert.Equal(betaDir, launchedContext.Settings.ProjectRoot);
                Assert.True(closeRequested);

                // 8) CloseCommand emits RequestClose(false)
                closeRequested = null;
                vm.CloseCommand.Execute(null);
                Assert.False(closeRequested);
                Assert.Empty(errors);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void ProjectManagerViewModel_OpenWithoutProject_EmitsNullLaunchContext()
        {
            var tempRoot = CreateTempDirectory("ProjectManagerNoProj");
            try
            {
                var store = new ProjectManagerStore(Path.Combine(tempRoot, "Store"));
                var called = false;
                ProjectLaunchContext? received = null;
                bool? closed = null;

                using var vm = new ProjectManagerViewModel(
                    store: store,
                    dispatchToUi: action => action(),
                    launchProject: ctx =>
                    {
                        called = true;
                        received = ctx;
                    });

                vm.RequestClose += launched => closed = launched;

                Assert.True(vm.OpenWithoutProjectCommand.CanExecute(null));
                vm.OpenWithoutProjectCommand.Execute(null);

                Assert.True(called);
                Assert.Null(received);
                Assert.True(vm.HasLaunched);
                Assert.Null(vm.OpenedLaunchContext);
                Assert.True(closed);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        // ------------------------------------------------------------------
        // MessageBoxViewModel tests
        // ------------------------------------------------------------------

        [Fact]
        public async Task MessageBoxViewModel_OkVariant_AppendMessageCopyAndClose()
        {
            string? copiedText = null;
            var linesAppendedCount = 0;
            MessageBoxResult? closeResult = null;

            var vm = MessageBoxViewModel.CreateOk(
                message: "Line 1\r\nLine 2",
                title: "Error",
                showCopyButton: true,
                useMonospaceLines: true,
                dispatchToUi: action => action(),
                copyToClipboardAsync: text =>
                {
                    copiedText = text;
                    return Task.CompletedTask;
                });

            vm.LinesAppended += () => linesAppendedCount++;
            vm.RequestClose += r => closeResult = r;

            Assert.Equal("Error", vm.Title);
            Assert.Equal("Line 1\r\nLine 2", vm.Message);
            Assert.Equal(2, vm.Lines.Count);
            Assert.True(vm.UseMonospaceLines);
            Assert.False(vm.UseWrappedMessage);
            Assert.True(vm.ShowCopyButton);
            Assert.True(vm.ShowPrimaryButton);
            Assert.False(vm.ShowSecondaryButton);
            Assert.False(vm.ShowCancelButton);
            Assert.Equal("OK", vm.PrimaryButtonLabel);
            Assert.False(vm.SecondaryCommand.CanExecute(null));
            Assert.False(vm.CancelCommand.CanExecute(null));

            vm.AppendMessage("Second failure");
            Assert.Equal(2, vm.ErrorCount);
            Assert.Equal("Errors (2)", vm.Title);
            Assert.Contains("Error 2:", vm.Message);
            Assert.Contains("Second failure", vm.Message);
            Assert.Equal(1, linesAppendedCount);
            Assert.True(vm.Lines.Count > 2);

            await vm.CopyMessageAsync();
            Assert.Equal(vm.Message, copiedText);

            vm.PrimaryCommand.Execute(null);
            Assert.Equal(MessageBoxResult.Ok, vm.Result);
            Assert.Equal(MessageBoxResult.Ok, closeResult);
        }

        [Fact]
        public void MessageBoxViewModel_OkCancelVariant_HandlesOkAndCancel()
        {
            MessageBoxResult? closeResult = null;
            var vm = MessageBoxViewModel.CreateOkCancel(
                message: "Delete project?",
                title: "Delete project",
                okLabel: "Delete",
                cancelLabel: "Cancel",
                isDestructive: true);
            vm.RequestClose += r => closeResult = r;

            Assert.True(vm.UseWrappedMessage);
            Assert.False(vm.UseMonospaceLines);
            Assert.False(vm.ShowCopyButton);
            Assert.True(vm.ShowPrimaryButton);
            Assert.False(vm.ShowSecondaryButton);
            Assert.True(vm.ShowCancelButton);
            Assert.True(vm.IsPrimaryDestructive);
            Assert.Equal("Delete", vm.PrimaryButtonLabel);
            Assert.Equal("Cancel", vm.CancelButtonLabel);

            vm.CancelCommand.Execute(null);
            Assert.Equal(MessageBoxResult.Cancel, vm.Result);
            Assert.Equal(MessageBoxResult.Cancel, closeResult);

            vm.PrimaryCommand.Execute(null);
            Assert.Equal(MessageBoxResult.Ok, vm.Result);
            Assert.Equal(MessageBoxResult.Ok, closeResult);
        }

        [Fact]
        public void MessageBoxViewModel_YesNoVariant_HandlesYesAndNo()
        {
            MessageBoxResult? closeResult = null;
            var vm = MessageBoxViewModel.CreateYesNo("Proceed?", "Confirm");
            vm.RequestClose += r => closeResult = r;

            Assert.True(vm.ShowPrimaryButton);
            Assert.True(vm.ShowSecondaryButton);
            Assert.False(vm.ShowCancelButton);
            Assert.Equal("Yes", vm.PrimaryButtonLabel);
            Assert.Equal("No", vm.SecondaryButtonLabel);
            Assert.False(vm.CancelCommand.CanExecute(null));

            vm.PrimaryCommand.Execute(null);
            Assert.Equal(MessageBoxResult.Yes, vm.Result);
            Assert.Equal(MessageBoxResult.Yes, closeResult);

            vm.SecondaryCommand.Execute(null);
            Assert.Equal(MessageBoxResult.No, vm.Result);
            Assert.Equal(MessageBoxResult.No, closeResult);
        }

        [Fact]
        public void MessageBoxViewModel_RiskyLoadVariant_MapsAllThreeChoicesAndEmitsClose()
        {
            MessageBoxResult? closeResult = null;
            var vm = MessageBoxViewModel.CreateRiskyLoad("Huge project detected");
            vm.RequestClose += r => closeResult = r;

            Assert.Equal("Large Unity project detected", vm.Title);
            Assert.True(vm.ShowPrimaryButton);
            Assert.True(vm.ShowSecondaryButton);
            Assert.True(vm.ShowCancelButton);
            Assert.True(vm.IsPrimaryBold);
            Assert.Contains("Safe/Lazy", vm.PrimaryButtonLabel);
            Assert.Contains("Eager", vm.SecondaryButtonLabel);
            Assert.Equal("Cancel", vm.CancelButtonLabel);

            vm.PrimaryCommand.Execute(null);
            Assert.Equal(MessageBoxResult.Primary, vm.Result);
            Assert.Equal(RiskyLoadChoice.LazyLoad, vm.RiskyLoadResult);
            Assert.Equal(MessageBoxResult.Primary, closeResult);

            vm.SecondaryCommand.Execute(null);
            Assert.Equal(MessageBoxResult.Secondary, vm.Result);
            Assert.Equal(RiskyLoadChoice.EagerLoad, vm.RiskyLoadResult);
            Assert.Equal(MessageBoxResult.Secondary, closeResult);

            vm.CancelCommand.Execute(null);
            Assert.Equal(MessageBoxResult.Cancel, vm.Result);
            Assert.Equal(RiskyLoadChoice.Cancel, vm.RiskyLoadResult);
            Assert.Equal(MessageBoxResult.Cancel, closeResult);
        }

        [Fact]
        public void MessageBoxViewModel_MemoryPressureVariant_MapsAllThreeChoices()
        {
            var vm = MessageBoxViewModel.CreateMemoryPressureWarning("High RAM usage");

            vm.PrimaryCommand.Execute(null);
            Assert.Equal(MemoryPressureResult.Continue, vm.MemoryPressureChoiceResult);

            vm.SecondaryCommand.Execute(null);
            Assert.Equal(MemoryPressureResult.StopAndKeep, vm.MemoryPressureChoiceResult);

            vm.CancelCommand.Execute(null);
            Assert.Equal(MemoryPressureResult.Cancel, vm.MemoryPressureChoiceResult);
        }

        // ------------------------------------------------------------------
        // Pure MVVM boundary verification (zero Avalonia UI types in ViewModels)
        // ------------------------------------------------------------------

        [Fact]
        public void SecondaryWindowViewModels_HaveZeroAvaloniaTypeDependencies()
        {
            var vmTypes = new[]
            {
                typeof(ExportOptionsViewModel),
                typeof(ProjectEditorViewModel),
                typeof(ProjectManagerViewModel),
                typeof(ProjectEntryViewModel),
                typeof(MessageBoxViewModel),
                typeof(RelayCommand),
                typeof(AsyncRelayCommand)
            };

            foreach (var type in vmTypes)
            {
                var members = type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
                foreach (var member in members)
                {
                    switch (member)
                    {
                        case PropertyInfo prop:
                            AssertNotAvaloniaType(prop.PropertyType, $"{type.Name}.{prop.Name}");
                            break;
                        case FieldInfo field:
                            AssertNotAvaloniaType(field.FieldType, $"{type.Name}.{field.Name}");
                            break;
                        case MethodInfo method:
                            AssertNotAvaloniaType(method.ReturnType, $"{type.Name}.{method.Name}()");
                            foreach (var param in method.GetParameters())
                            {
                                AssertNotAvaloniaType(param.ParameterType, $"{type.Name}.{method.Name}({param.Name})");
                            }
                            break;
                    }
                }
            }
        }

        private static void AssertNotAvaloniaType(Type type, string memberName)
        {
            var ns = type.Namespace ?? string.Empty;
            Assert.False(
                ns.Equals("Avalonia", StringComparison.Ordinal) || ns.StartsWith("Avalonia.", StringComparison.Ordinal),
                $"ViewModel member '{memberName}' must not reference Avalonia UI type '{type.FullName}'.");

            if (type.IsGenericType)
            {
                foreach (var arg in type.GetGenericArguments())
                {
                    AssertNotAvaloniaType(arg, memberName);
                }
            }
        }

        private static string CreateTempDirectory(string prefix)
        {
            var path = Path.Combine(Path.GetTempPath(), $"AssetStudio_{prefix}_{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return path;
        }

        private static void TryDeleteDirectory(string path)
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch
            {
                // Best effort cleanup for transient SQLite handles on Windows
            }
        }
    }
}
