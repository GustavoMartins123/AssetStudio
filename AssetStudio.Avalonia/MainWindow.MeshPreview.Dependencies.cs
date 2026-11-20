using AssetStudio;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace AssetStudio.Avalonia;

public partial class MainWindow
{
    private static readonly HashSet<ClassIDType> MaterialPreviewReferenceTypes = new()
    {
        ClassIDType.Material,
        ClassIDType.Texture2D
    };

    private static readonly HashSet<ClassIDType> MeshPreviewReferenceTypes = new()
    {
        ClassIDType.GameObject,
        ClassIDType.Transform,
        ClassIDType.RectTransform,
        ClassIDType.MeshFilter,
        ClassIDType.MeshRenderer,
        ClassIDType.SkinnedMeshRenderer,
        ClassIDType.Material
    };

    private static readonly HashSet<ClassIDType> SpritePreviewReferenceTypes = new()
    {
        ClassIDType.Sprite,
        ClassIDType.Texture2D,
        ClassIDType.SpriteAtlas
    };

    private static readonly HashSet<ClassIDType> AnimationClip2DPreviewReferenceTypes = new()
    {
        ClassIDType.AnimationClip,
        ClassIDType.Sprite,
        ClassIDType.Texture2D,
        ClassIDType.SpriteAtlas
    };

    private static readonly HashSet<ClassIDType> LazyConnectionReferenceTypes = new()
    {
        ClassIDType.GameObject,
        ClassIDType.Transform,
        ClassIDType.RectTransform,
        ClassIDType.MeshFilter,
        ClassIDType.MeshRenderer,
        ClassIDType.SkinnedMeshRenderer,
        ClassIDType.Material,
        ClassIDType.Avatar,
        ClassIDType.Animator,
        ClassIDType.Animation,
        ClassIDType.AnimationClip,
        ClassIDType.AnimatorController,
        ClassIDType.AnimatorOverrideController,
        ClassIDType.RuntimeAnimatorController
    };

    private void EnsureMeshPreviewDependenciesLoaded(Mesh mesh)
    {
        if (!assetsManager.LazyLoading || mesh.assetsFile == null)
        {
            return;
        }

        using var evictionSuspension = assetsManager.LruCache.SuspendEviction();
        EnsureIndexedExternalSourcesLoaded(mesh.assetsFile);
        MaterializeReferenceObjects(mesh.assetsFile, MeshPreviewReferenceTypes);

        List<SerializedFile> filesSnapshot;
        lock (assetsManager.loadLock)
        {
            filesSnapshot = assetsManager.assetsFileList.ToList();
        }

        foreach (var file in filesSnapshot)
        {
            MaterializeReferenceObjects(file, MeshPreviewReferenceTypes);
        }

        BuildLazyPreviewReferenceIndexes(filesSnapshot);
    }

    private void EnsureMaterialPreviewDependenciesLoaded(Material material)
    {
        if (!assetsManager.LazyLoading || material.assetsFile == null)
        {
            return;
        }

        EnsureIndexedExternalSourcesLoaded(material.assetsFile);

        List<SerializedFile> filesSnapshot;
        lock (assetsManager.loadLock)
        {
            filesSnapshot = assetsManager.assetsFileList.ToList();
        }

        foreach (var file in filesSnapshot)
        {
            MaterializeReferenceObjects(file, MaterialPreviewReferenceTypes);
        }
    }

    private void EnsureSpritePreviewDependenciesLoaded(Sprite sprite)
    {
        if (!assetsManager.LazyLoading || sprite.assetsFile == null)
        {
            return;
        }

        using var evictionSuspension = assetsManager.LruCache.SuspendEviction();
        EnsureIndexedExternalSourcesLoaded(sprite.assetsFile);
        MaterializeReferenceObjects(sprite.assetsFile, SpritePreviewReferenceTypes);

        var filesSnapshot = GetLoadedFilesSnapshot();
        foreach (var file in filesSnapshot)
        {
            MaterializeReferenceObjects(file, SpritePreviewReferenceTypes);
        }

        PrepareLoadedSpriteAtlasReferencesForPreview(filesSnapshot);
    }

    private void EnsureAnimationClip2DPreviewDependenciesLoaded(AnimationClip clip)
    {
        if (!assetsManager.LazyLoading || clip.assetsFile == null)
        {
            return;
        }

        using var evictionSuspension = assetsManager.LruCache.SuspendEviction();
        EnsureIndexedExternalSourcesLoaded(clip.assetsFile);
        MaterializeReferenceObjects(clip.assetsFile, AnimationClip2DPreviewReferenceTypes);

        var filesSnapshot = GetLoadedFilesSnapshot();
        foreach (var file in filesSnapshot)
        {
            MaterializeReferenceObjects(file, AnimationClip2DPreviewReferenceTypes);
        }

        PrepareLoadedSpriteAtlasReferencesForPreview(filesSnapshot);
    }

    private void EnsureIndexedExternalSourcesLoaded(SerializedFile sourceFile)
    {
        if (sourceFile == null)
        {
            return;
        }

        var pending = new Queue<SerializedFile>();
        var processedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var processedExternals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        pending.Enqueue(sourceFile);

        while (pending.Count > 0)
        {
            var currentFile = pending.Dequeue();
            if (currentFile == null || !processedFiles.Add(GetSerializedFileDependencyKey(currentFile)))
            {
                continue;
            }

            if (currentFile.m_Externals == null || currentFile.m_Externals.Count == 0)
            {
                continue;
            }

            foreach (var external in currentFile.m_Externals)
            {
                if (external == null || (string.IsNullOrWhiteSpace(external.fileName) && string.IsNullOrWhiteSpace(external.pathName)))
                {
                    continue;
                }

                var externalKey = $"{external.fileName}|{external.pathName}";
                if (!processedExternals.Add(externalKey))
                {
                    continue;
                }

                if (TryGetLoadedExternalFile(external, out var loadedExternalFile))
                {
                    pending.Enqueue(loadedExternalFile);
                    continue;
                }

                var sourcePath = ResolveIndexedExternalSourcePath(external);
                if (string.IsNullOrWhiteSpace(sourcePath))
                {
                    continue;
                }

                try
                {
                    RemovePendingFileFromProgressiveQueue(sourcePath);
                    assetsManager.LoadFilesForPreview(sourcePath);
                    assetsManager.WaitForAssetsFileLoaded(external.fileName, 5000);
                    if (TryGetLoadedExternalFile(external, out loadedExternalFile))
                    {
                        pending.Enqueue(loadedExternalFile);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Failed to load external preview source {Path.GetFileName(sourcePath)}: {ex.Message}");
                }
            }
        }
    }

    private List<SerializedFile> GetLoadedFilesSnapshot()
    {
        lock (assetsManager.loadLock)
        {
            return assetsManager.assetsFileList.ToList();
        }
    }

    private bool TryGetLoadedExternalFile(FileIdentifier external, out SerializedFile sourceFile)
    {
        sourceFile = null!;
        if (external == null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(external.fileName)
            && ((assetsManager.TryFindSerializedFile(external.fileName, null, out sourceFile) && sourceFile != null)
                || (assetsManager.TryFindSerializedFile(external.fileName, external.pathName, out sourceFile) && sourceFile != null)))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(external.pathName)
            && assetsManager.TryFindSerializedFile(Path.GetFileName(external.pathName), external.pathName, out sourceFile)
            && sourceFile != null)
        {
            return true;
        }

        return false;
    }

    private string? ResolveIndexedExternalSourcePath(FileIdentifier external)
    {
        if (external == null)
        {
            return null;
        }

        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddCandidateFileName(candidates, external.fileName);
        AddCandidateFileName(candidates, external.pathName);

        foreach (var candidate in candidates)
        {
            var direct = TryResolveExistingPath(candidate);
            if (!string.IsNullOrEmpty(direct))
            {
                return direct;
            }

            if (!string.IsNullOrEmpty(assetsManager.ProjectRoot))
            {
                direct = TryResolveExistingPath(Path.Combine(assetsManager.ProjectRoot, candidate));
                if (!string.IsNullOrEmpty(direct))
                {
                    return direct;
                }
            }

            if (lazySourcePathBySerializedFile.TryGetValue(candidate, out var mappedSourcePath))
            {
                direct = TryResolveExistingPath(mappedSourcePath);
                if (!string.IsNullOrEmpty(direct))
                {
                    return direct;
                }
            }

            var indexedPath = assetsManager.ProjectIndex
                .GetHandlesForFile(candidate)
                .Select(ResolveLazyHandleSourcePath)
                .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));
            if (!string.IsNullOrEmpty(indexedPath))
            {
                return indexedPath;
            }
        }

        foreach (var candidate in candidates)
        {
            var found = FindSourceFileByNameInProjectRoot(candidate);
            if (!string.IsNullOrEmpty(found))
            {
                return found;
            }
        }

        return null;
    }

    private static string GetSerializedFileDependencyKey(SerializedFile file)
    {
        if (!string.IsNullOrWhiteSpace(file.originalPath))
        {
            return file.originalPath;
        }

        if (!string.IsNullOrWhiteSpace(file.fullName))
        {
            return file.fullName;
        }

        return file.fileName ?? string.Empty;
    }

    private static void PrepareLoadedSpriteAtlasReferencesForPreview(List<SerializedFile> filesSnapshot)
    {
        if (filesSnapshot.Count == 0)
        {
            return;
        }

        var sprites = new List<Sprite>();
        var atlases = new List<SpriteAtlas>();
        foreach (var file in filesSnapshot)
        {
            AssetStudio.Object[] objects;
            lock (file)
            {
                objects = file.Objects.ToArray();
            }

            foreach (var obj in objects)
            {
                if (obj is Sprite sprite)
                {
                    ResetSpriteReferenceCache(sprite);
                    sprites.Add(sprite);
                }
                else if (obj is SpriteAtlas atlas)
                {
                    ResetSpriteAtlasReferenceCache(atlas);
                    atlases.Add(atlas);
                }
            }
        }

        if (sprites.Count == 0 || atlases.Count == 0)
        {
            return;
        }

        var spriteAtlasCache = new Dictionary<KeyValuePair<Guid, long>, SpriteAtlas>();
        foreach (var atlas in atlases)
        {
            if (atlas.m_RenderDataMap != null)
            {
                foreach (var key in atlas.m_RenderDataMap.Keys)
                {
                    spriteAtlasCache[key] = atlas;
                }
            }

            foreach (var packedSprite in atlas.m_PackedSprites ?? Array.Empty<PPtr<Sprite>>())
            {
                if (packedSprite != null && packedSprite.TryGet(out var sprite) && sprite != null)
                {
                    if (sprite.m_SpriteAtlas == null || sprite.m_SpriteAtlas.IsNull)
                    {
                        sprite.m_SpriteAtlas?.Set(atlas);
                    }
                    else if (sprite.m_SpriteAtlas.TryGet(out var oldAtlas) && oldAtlas?.m_IsVariant == true)
                    {
                        sprite.m_SpriteAtlas.Set(atlas);
                    }
                }
            }
        }

        foreach (var sprite in sprites)
        {
            if (sprite.m_SpriteAtlas != null
                && !sprite.m_SpriteAtlas.IsNull
                && sprite.m_SpriteAtlas.TryGet(out _))
            {
                continue;
            }

            if (sprite.m_RenderDataKey.Key != Guid.Empty
                && spriteAtlasCache.TryGetValue(sprite.m_RenderDataKey, out var atlas)
                && sprite.m_SpriteAtlas != null)
            {
                sprite.m_SpriteAtlas.Set(atlas);
            }
        }
    }

    private static void ResetSpriteReferenceCache(Sprite sprite)
    {
        sprite.m_SpriteAtlas?.ResetCache();
        if (sprite.m_RD == null)
        {
            return;
        }

        sprite.m_RD.texture?.ResetCache();
        sprite.m_RD.alphaTexture?.ResetCache();
        foreach (var secondaryTexture in sprite.m_RD.secondaryTextures ?? Array.Empty<SecondarySpriteTexture>())
        {
            secondaryTexture?.texture?.ResetCache();
        }
    }

    private static void ResetSpriteAtlasReferenceCache(SpriteAtlas atlas)
    {
        foreach (var packedSprite in atlas.m_PackedSprites ?? Array.Empty<PPtr<Sprite>>())
        {
            packedSprite?.ResetCache();
        }

        if (atlas.m_RenderDataMap == null)
        {
            return;
        }

        foreach (var data in atlas.m_RenderDataMap.Values)
        {
            data.texture?.ResetCache();
            data.alphaTexture?.ResetCache();
            foreach (var secondaryTexture in data.secondaryTextures ?? Array.Empty<SecondarySpriteTexture>())
            {
                secondaryTexture?.texture?.ResetCache();
            }
        }
    }

    private void MaterializeReferenceObjects(
        SerializedFile sourceFile,
        HashSet<ClassIDType> referenceTypes,
        LazyConnectionBuildDiagnostics? diagnostics = null)
    {
        var handles = assetsManager.ProjectIndex
            .GetHandlesForFile(sourceFile.fileName)
            .Where(handle => referenceTypes.Contains(handle.Type)
                && (ReferenceEquals(handle.SourceFile, sourceFile)
                    || IsSameLazySource(handle.OriginalPath, sourceFile.originalPath)
                    || string.IsNullOrEmpty(handle.OriginalPath)))
            .ToList();

        diagnostics?.RecordMaterializationCandidateCount(handles.Count);
        foreach (var handle in handles)
        {
            if (handle.SourceFile?.reader == null)
            {
                handle.SourceFile = sourceFile;
            }

            var obj = assetsManager.ResolveHandle(handle);
            if (obj == null)
            {
                diagnostics?.RecordFailedObject();
                continue;
            }

            diagnostics?.RecordResolvedObject(handle.Type);
            if (handle.Tag is AssetItem item)
            {
                UpdateLazyAssetItemHandle(item, handle);
            }
        }
    }

    private void BuildLazyPreviewReferenceIndexes(List<SerializedFile> filesSnapshot)
    {
        BuildAssetReferenceIndexesBackground(
            filesSnapshot,
            new List<AssetItem>(),
            out _,
            out var localMeshToMaterialsCache,
            out var localMeshAssociatedRenderersCache,
            out var localMeshSourceTypesCache,
            out var localMaterialMainTextureCache,
            out var localMaterialPreviewMaterialCache,
            out var localMaterialTextureSlotsCache,
            out _);

        lock (previewCacheLock)
        {
            meshToMaterialsCache = localMeshToMaterialsCache;
            meshAssociatedRenderersCache = localMeshAssociatedRenderersCache;
            meshSourceTypesCache = localMeshSourceTypesCache;
            materialMainTextureCache = localMaterialMainTextureCache;
            materialPreviewMaterialCache = localMaterialPreviewMaterialCache;
            materialTextureSlotsCache = localMaterialTextureSlotsCache;
        }
    }

    private static bool IsSameLazySource(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    private void BuildAssetReferenceIndexes()
    {
        BuildAssetReferenceIndexesBackground(
            assetsManager.assetsFileList,
            exportableAssets,
            out var localObjectToAssetItemCache,
            out var localMeshToMaterialsCache,
            out var localMeshAssociatedRenderersCache,
            out var localMeshSourceTypesCache,
            out var localMaterialMainTextureCache,
            out var localMaterialPreviewMaterialCache,
            out var localMaterialTextureSlotsCache,
            out var semanticRelations);

        objectToAssetItemCache = localObjectToAssetItemCache;
        meshToMaterialsCache = localMeshToMaterialsCache;
        meshAssociatedRenderersCache = localMeshAssociatedRenderersCache;
        meshSourceTypesCache = localMeshSourceTypesCache;
        materialMainTextureCache = localMaterialMainTextureCache;
        materialPreviewMaterialCache = localMaterialPreviewMaterialCache;
        materialTextureSlotsCache = localMaterialTextureSlotsCache;
        if (!assetsManager.LazyLoading)
        {
            _ = Task.Run(() => TrySaveSemanticRelations(semanticRelations));
        }

        BuildAnimationPreviewIndexesBackground(
            assetsManager.assetsFileList,
            out var localAnimationClipAvatarCache,
            out var localAvatarMeshCache,
            out var localMeshAvatarCache,
            out var localAnimationClipTransformBindingsCache);

        animationClipAvatarCache = localAnimationClipAvatarCache;
        avatarMeshCache = localAvatarMeshCache;
        avatarMeshesCache = BuildAvatarMeshListCache(localAvatarMeshCache);
        meshAvatarCache = localMeshAvatarCache;
        animationClipTransformBindingsCache = localAnimationClipTransformBindingsCache;
    }
}
