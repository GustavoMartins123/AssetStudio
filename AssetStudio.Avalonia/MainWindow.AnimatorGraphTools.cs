using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AssetStudio;
using AssetStudio.Avalonia.Services;
using AssetStudio.Avalonia.ViewModels;
using Avalonia.Threading;
using UnityObject = AssetStudio.Object;

namespace AssetStudio.Avalonia;

public partial class MainWindow
{
    internal async Task OpenAnimatorGraphDialogCommandHandlerAsync()
    {
        if (IsWindowClosingOrClosed)
        {
            return;
        }

        var selectedItem = AssetListDataGrid?.SelectedItem as AssetItem
            ?? assetContextItem
            ?? PreviewSession.ActiveAsset;

        if (selectedItem == null)
        {
            await ViewModel.DialogService.ShowMessageAsync(
                "Animator Graph",
                "Select an AnimatorController, AnimatorOverrideController, or Animator asset first.");
            return;
        }

        if (selectedItem.Type != ClassIDType.AnimatorController
            && selectedItem.Type != ClassIDType.AnimatorOverrideController
            && selectedItem.Type != ClassIDType.Animator)
        {
            await ViewModel.DialogService.ShowMessageAsync(
                "Animator Graph",
                $"Selected asset '{selectedItem.Name}' ({selectedItem.DisplayType}) is not an AnimatorController, AnimatorOverrideController, or Animator.");
            return;
        }

        StatusStripUpdate($"Building Animator Graph for {selectedItem.Name}...");

        AnimatorGraphDocument? document;
        try
        {
            document = await Task.Run(() =>
            {
                var resolvedObject = ResolveAssetItemObjectForAnimatorTools(selectedItem);
                if (resolvedObject == null)
                {
                    return null;
                }

                return AnimatorGraphService.BuildDocument(
                    resolvedObject,
                    controllerResolver: ResolveRuntimeAnimatorControllerAcrossProject,
                    clipResolver: ResolveAnimationClipAcrossProject);
            });
        }
        catch (Exception ex)
        {
            logger.Log(LoggerEvent.Error, $"Failed to build Animator Graph for {selectedItem.Name}: {ex}");
            StatusStripUpdate($"Animator Graph error: {ex.Message}");
            await ViewModel.DialogService.ShowErrorAsync("Animator Graph Error", ex.Message);
            return;
        }

        if (IsWindowClosingOrClosed)
        {
            return;
        }

        if (document == null)
        {
            await ViewModel.DialogService.ShowMessageAsync(
                "Animator Graph",
                $"Unable to materialize asset '{selectedItem.Name}' from its source file.");
            return;
        }

        var vm = new AnimatorGraphViewModel(document);
        vm.NavigateToClipAsync = clipRef =>
        {
            if (clipRef == null || !clipRef.CanNavigate || IsWindowClosingOrClosed)
            {
                return Task.CompletedTask;
            }

            var targetRef = new AssetReferenceItem
            {
                AssetUniqueId = clipRef.UniqueId,
                Name = clipRef.Name,
                AssetType = ClassIDType.AnimationClip,
                TypeName = nameof(ClassIDType.AnimationClip),
                Relation = "Animator Clip",
                PathId = clipRef.PathId,
                SerializedFileName = clipRef.SourceFile,
                Direction = AssetReferenceDirection.References
            };

            return Dispatcher.UIThread.InvokeAsync(() => NavigateToReferencedAssetCoreAsync(targetRef, recordHistory: true));
        };

        var dialog = new AnimatorGraphDialog(vm);
        if (IsVisible)
        {
            dialog.Show(this);
        }
        else
        {
            dialog.Show();
        }

        StatusStripUpdate($"Opened Animator Graph: {document.ControllerName} ({document.Layers.Count} layer(s)).");
    }

    internal async Task ExportSelectedAnimationClipMetadataCommandHandlerAsync()
    {
        if (IsWindowClosingOrClosed)
        {
            return;
        }

        var selectedClips = GetSelectedAssets()
            .Where(item => item.Type == ClassIDType.AnimationClip)
            .ToList();

        if (selectedClips.Count == 0)
        {
            var fallback = AssetListDataGrid?.SelectedItem as AssetItem
                ?? assetContextItem
                ?? PreviewSession.ActiveAsset;
            if (fallback != null && fallback.Type == ClassIDType.AnimationClip)
            {
                selectedClips.Add(fallback);
            }
        }

        if (selectedClips.Count == 0)
        {
            await ViewModel.DialogService.ShowMessageAsync(
                "Export AnimationClip Metadata",
                "Select one or more AnimationClip assets in the Asset List first.");
            return;
        }

        if (selectedClips.Count == 1)
        {
            await ExportSingleAnimationClipMetadataAsync(selectedClips[0]);
            return;
        }

        await ExportBatchAnimationClipMetadataAsync(selectedClips);
    }

    private async Task ExportSingleAnimationClipMetadataAsync(AssetItem clipItem)
    {
        var stem = AnimationClipMetadataService.SanitizeFileStem(clipItem.Name);
        var savePath = await ViewModel.DialogService.PickSaveFileAsync(new SaveFilePickerRequest(
            title: "Export AnimationClip Metadata JSON",
            suggestedFileName: $"{stem}.anim.json",
            defaultExtension: "json",
            suggestedStartDirectory: appSettings.ExportFolderPath,
            fileTypeFilters: new[]
            {
                new FileTypeFilterSpec("JSON Metadata Sidecar (.json)", "*.json")
            }));

        if (string.IsNullOrWhiteSpace(savePath) || IsWindowClosingOrClosed)
        {
            return;
        }

        var parentDir = Path.GetDirectoryName(savePath);
        if (!string.IsNullOrWhiteSpace(parentDir))
        {
            SaveExportFolder(parentDir);
        }

        StatusStripUpdate($"Exporting AnimationClip metadata for {clipItem.Name}...");

        try
        {
            await Task.Run(() =>
            {
                var clipObj = ResolveAssetItemObjectForAnimatorTools(clipItem) as AnimationClip;
                if (clipObj == null)
                {
                    throw new InvalidOperationException($"Could not materialize AnimationClip '{clipItem.Name}'.");
                }

                var bonePathHash = BuildBonePathLookupSafe();
                var doc = AnimationClipMetadataService.ExtractMetadata(clipObj, bonePathHash);
                AnimationClipMetadataService.ExportToJsonFile(doc, savePath);
            });

            StatusStripUpdate($"Exported AnimationClip metadata JSON: {savePath}");
        }
        catch (Exception ex)
        {
            logger.Log(LoggerEvent.Error, $"Failed to export AnimationClip metadata for {clipItem.Name}: {ex}");
            StatusStripUpdate($"AnimationClip metadata export failed: {ex.Message}");
            await ViewModel.DialogService.ShowErrorAsync("AnimationClip Metadata Export Error", ex.Message);
        }
    }

    private async Task ExportBatchAnimationClipMetadataAsync(List<AssetItem> clipItems)
    {
        var outputFolder = await PickExportFolderDialogAsync("Export AnimationClip Metadata JSON Folder");
        if (string.IsNullOrWhiteSpace(outputFolder) || IsWindowClosingOrClosed)
        {
            return;
        }

        SaveExportFolder(outputFolder);
        StatusStripUpdate($"Exporting metadata JSON for {clipItems.Count:N0} AnimationClip(s)...");

        try
        {
            var result = await Task.Run(() =>
            {
                if (assetsManager.LazyLoading)
                {
                    EnsureLazyAssetsLoadedForExport(clipItems);
                }

                var clips = new List<AnimationClip>(clipItems.Count);
                foreach (var item in clipItems)
                {
                    if (ResolveAssetItemObjectForAnimatorTools(item) is AnimationClip clipObj)
                    {
                        clips.Add(clipObj);
                    }
                }

                var bonePathHash = BuildBonePathLookupSafe();
                return AnimationClipMetadataService.ExportBatchMetadataJson(
                    clips,
                    outputFolder,
                    bonePathHash,
                    CancellationToken.None);
            });

            var errorSuffix = result.Errors.Count > 0 ? $" ({result.Errors.Count} error(s))" : string.Empty;
            StatusStripUpdate($"Exported {result.ExportedCount:N0} AnimationClip metadata JSON sidecar(s) to {result.OutputDirectory}{errorSuffix}");
        }
        catch (Exception ex)
        {
            logger.Log(LoggerEvent.Error, $"Batch AnimationClip metadata export failed: {ex}");
            StatusStripUpdate($"Batch AnimationClip metadata export failed: {ex.Message}");
            await ViewModel.DialogService.ShowErrorAsync("AnimationClip Metadata Export Error", ex.Message);
        }
    }

    private UnityObject? ResolveAssetItemObjectForAnimatorTools(AssetItem item)
    {
        if (item.Asset != null)
        {
            return item.Asset;
        }

        if (item.Handle != null)
        {
            if (item.Handle.RealObject != null)
            {
                return item.Handle.RealObject;
            }

            if (assetsManager.LazyLoading)
            {
                EnsureLazyAssetReadyForPreview(item);
                if (item.Asset != null)
                {
                    return item.Asset;
                }
            }

            return assetsManager.ResolveHandle(item.Handle);
        }

        return null;
    }

    private RuntimeAnimatorController? ResolveRuntimeAnimatorControllerAcrossProject(PPtr<RuntimeAnimatorController> pptr)
    {
        if (pptr == null || pptr.IsNull)
        {
            return null;
        }

        long pathId = pptr.m_PathID;
        var loadedFiles = GetLoadedFilesSnapshot();
        for (int f = 0; f < loadedFiles.Count; f++)
        {
            var file = loadedFiles[f];
            if (file != null && file.TryGetObject(pathId, out var obj) && obj is RuntimeAnimatorController rac)
            {
                return rac;
            }
        }

        var arena = assetsManager.ProjectIndex.Arena;
        int count = arena.Count;
        for (int i = 0; i < count; i++)
        {
            if (arena.GetPathId(i) != pathId)
            {
                continue;
            }

            var type = arena.GetType(i);
            if (type != ClassIDType.AnimatorController && type != ClassIDType.AnimatorOverrideController)
            {
                continue;
            }

            var handle = assetsManager.ProjectIndex.MaterializeHandle(i);
            var resolved = ResolveSemanticRelationHandleForPreview(handle) ?? assetsManager.ResolveHandle(handle);
            if (resolved is RuntimeAnimatorController racFromIndex)
            {
                return racFromIndex;
            }
        }

        return null;
    }

    private AnimationClip? ResolveAnimationClipAcrossProject(PPtr<AnimationClip> pptr)
    {
        if (pptr == null || pptr.IsNull)
        {
            return null;
        }

        long pathId = pptr.m_PathID;
        var loadedFiles = GetLoadedFilesSnapshot();
        for (int f = 0; f < loadedFiles.Count; f++)
        {
            var file = loadedFiles[f];
            if (file != null && file.TryGetObject(pathId, out var obj) && obj is AnimationClip clip)
            {
                return clip;
            }
        }

        var arena = assetsManager.ProjectIndex.Arena;
        int count = arena.Count;
        for (int i = 0; i < count; i++)
        {
            if (arena.GetPathId(i) != pathId || arena.GetType(i) != ClassIDType.AnimationClip)
            {
                continue;
            }

            var handle = assetsManager.ProjectIndex.MaterializeHandle(i);
            var resolved = ResolveSemanticRelationHandleForPreview(handle) ?? assetsManager.ResolveHandle(handle);
            if (resolved is AnimationClip clipFromIndex)
            {
                return clipFromIndex;
            }
        }

        return null;
    }

    private IReadOnlyDictionary<uint, string>? BuildBonePathLookupSafe()
    {
        try
        {
            var files = GetLoadedFilesSnapshot();
            if (files.Count == 0)
            {
                return null;
            }

            return AssetExportHelper.BuildBonePathHash(files);
        }
        catch
        {
            return null;
        }
    }
}
