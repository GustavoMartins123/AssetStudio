using AssetStudio;
using AssetStudio.Avalonia.Services;
using AssetStudio.Avalonia.ViewModels;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AssetStudio.Avalonia;

public partial class MainWindow
{
    private sealed class PreparedTmpFontPreviewResult
    {
        public required WriteableBitmap Bitmap { get; init; }
        public required string InfoText { get; init; }
        public required string StatusMessage { get; init; }
    }

    private sealed class PreparedMonoBehaviourTreeResult
    {
        public required MonoBehaviourTreeBuildResult TreeResult { get; init; }
        public required string FullJsonText { get; init; }
        public required string DisplayJsonText { get; init; }
        public required string StatusMessage { get; init; }
    }

    private void PreviewMonoBehaviour(AssetItem assetItem, MonoBehaviour m_MonoBehaviour, string fbxHeader)
    {
        var token = BeginAsyncPreviewRequest(out var requestId, out var previewVersion);
        PreviewSession.ClearMonoBehaviourTree();
        PreviewSession.ActivateSurface(
            PreviewSurfaceKind.MonoBehaviour,
            assetItem,
            isLoading: true,
            loadingStatusText: $"Loading MonoBehaviour preview: {assetItem.Name}...");
        PreviewSession.TextContent = "Loading MonoBehaviour preview...";
        TextPreviewBox.Text = PreviewSession.TextContent;
        TextPreviewBox.IsVisible = true;
        PreviewLabel.IsVisible = false;
        StatusStripUpdate($"Loading MonoBehaviour preview: {assetItem.Name}...");

        _ = PreviewMonoBehaviourAsync(assetItem, m_MonoBehaviour, fbxHeader, requestId, previewVersion, token);
    }

    private async Task PreviewMonoBehaviourAsync(
        AssetItem assetItem,
        MonoBehaviour m_MonoBehaviour,
        string fbxHeader,
        long requestId,
        long previewVersion,
        CancellationToken token)
    {
        var scanResultSnapshot = currentScanResult;
        var folderPath = GetCurrentCacheFolderPath();

        try
        {
            // Fast path: attempt direct ToType() off the UI thread, checking for TextMeshPro TMP_FontAsset first.
            var directResult = await Task.Run<(bool Succeeded, PreparedMonoBehaviourTreeResult? Tree, PreparedTmpFontPreviewResult? TmpFont)>(() =>
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var obj = m_MonoBehaviour.ToType();
                    if (obj != null)
                    {
                        token.ThrowIfCancellationRequested();
                        var tmpFontPreview = TryPrepareTmpFontPreviewBackground(m_MonoBehaviour, obj, token);
                        if (tmpFontPreview != null)
                        {
                            return (true, null, tmpFontPreview);
                        }

                        var preparedTree = BuildPreparedMonoBehaviourTreeBackground(
                            assetItem,
                            m_MonoBehaviour,
                            obj,
                            fbxHeader,
                            scanResultSnapshot,
                            folderPath,
                            token);
                        return (true, preparedTree, null);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Fall through to TypeTree / Dump fallback.
                }

                return (false, null, null);
            }, token);

            if (directResult.TmpFont != null)
            {
                ApplyTmpFontPreview(assetItem, requestId, previewVersion, token, directResult.TmpFont);
                return;
            }

            if (directResult.Succeeded && directResult.Tree != null)
            {
                ApplyMonoBehaviourTreePreview(assetItem, requestId, previewVersion, token, directResult.Tree);
                return;
            }

            if (!IsPreviewRequestCurrent(requestId, previewVersion, token))
            {
                return;
            }

            // TypeTree path: assembly folder prompt (if needed) on UI thread, assembly load + ConvertToTypeTree off UI thread.
            TypeTree? typeTree = null;
            try
            {
                typeTree = await MonoBehaviourToTypeTree(m_MonoBehaviour, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                typeTree = null;
            }

            if (!IsPreviewRequestCurrent(requestId, previewVersion, token))
            {
                return;
            }

            var resolved = await Task.Run<(PreparedMonoBehaviourTreeResult? Tree, string? Text, string Status, PreparedTmpFontPreviewResult? TmpFont)>(() =>
            {
                token.ThrowIfCancellationRequested();

                if (typeTree != null)
                {
                    try
                    {
                        var obj = m_MonoBehaviour.ToType(typeTree);
                        if (obj != null)
                        {
                            token.ThrowIfCancellationRequested();
                            var tmpFontPreview = TryPrepareTmpFontPreviewBackground(m_MonoBehaviour, obj, token);
                            if (tmpFontPreview != null)
                            {
                                return (null, null, tmpFontPreview.StatusMessage, tmpFontPreview);
                            }

                            var preparedTree = BuildPreparedMonoBehaviourTreeBackground(
                                assetItem,
                                m_MonoBehaviour,
                                obj,
                                fbxHeader,
                                scanResultSnapshot,
                                folderPath,
                                token);
                            return (preparedTree, null, preparedTree.StatusMessage, null);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                        // Fall through to text dump fallback.
                    }
                }

                string? dumpStr = null;
                try
                {
                    dumpStr = m_MonoBehaviour.Dump();
                    if (dumpStr == null && typeTree != null)
                    {
                        dumpStr = m_MonoBehaviour.Dump(typeTree);
                    }
                }
                catch (Exception dumpEx)
                {
                    dumpStr = $"Failed to dump MonoBehaviour: {dumpEx.Message}";
                }

                token.ThrowIfCancellationRequested();
                return dumpStr != null
                    ? (null, TruncatePreviewText(fbxHeader + dumpStr), "MonoBehaviour loaded (text dump).", null)
                    : (null, null, "MonoBehaviour loaded (no dump/types available).", null);
            }, token);

            if (resolved.TmpFont != null)
            {
                ApplyTmpFontPreview(assetItem, requestId, previewVersion, token, resolved.TmpFont);
                return;
            }

            if (resolved.Tree != null)
            {
                ApplyMonoBehaviourTreePreview(assetItem, requestId, previewVersion, token, resolved.Tree);
                return;
            }

            ApplyMonoBehaviourPreviewText(requestId, previewVersion, token, resolved.Text, resolved.Status);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.Log(LoggerEvent.Error, $"MonoBehaviour preview failed for {assetItem.Name}: {ex}");
            var errorText = TruncatePreviewText($"Failed to preview MonoBehaviour {assetItem.Name}:{Environment.NewLine}{ex.Message}");
            ApplyMonoBehaviourPreviewText(requestId, previewVersion, token, errorText, $"MonoBehaviour preview error: {ex.Message}");
        }
    }

    private PreparedMonoBehaviourTreeResult BuildPreparedMonoBehaviourTreeBackground(
        AssetItem assetItem,
        MonoBehaviour monoBehaviour,
        OrderedDictionary fields,
        string fbxHeader,
        ProjectScanResult? scanResultSnapshot,
        string? folderPath,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        var sourceFile = monoBehaviour.assetsFile;
        var semanticLookup = BuildSemanticOutgoingLookupBackground(sourceFile, monoBehaviour.m_PathID, scanResultSnapshot, folderPath);
        var scriptTitle = ResolveMonoBehaviourScriptTitle(monoBehaviour, assetItem, sourceFile);

        var treeResult = MonoBehaviourTreeBuilder.Build(
            fields,
            pptrResolver: (fileId, pathId) => ResolvePPtrMetadataForInspector(
                sourceFile,
                fileId,
                pathId,
                ClassIDType.UnknownType,
                relation: string.Empty,
                semanticLookup),
            scriptTitle: scriptTitle,
            options: MonoBehaviourTreeBuildOptions.Default,
            cancellationToken: token);

        token.ThrowIfCancellationRequested();
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(fields, Newtonsoft.Json.Formatting.Indented);
        token.ThrowIfCancellationRequested();
        var fullJson = string.IsNullOrEmpty(fbxHeader) ? json : fbxHeader + json;
        var displayJson = TruncatePreviewText(fullJson);

        var status = $"MonoBehaviour loaded: {scriptTitle} ({treeResult.SummaryText})";
        return new PreparedMonoBehaviourTreeResult
        {
            TreeResult = treeResult,
            FullJsonText = fullJson,
            DisplayJsonText = displayJson,
            StatusMessage = status
        };
    }

    internal Dictionary<string, AssetReferenceItem>? BuildSemanticOutgoingLookupBackground(
        SerializedFile? sourceFile,
        long pathId,
        ProjectScanResult? scanResultSnapshot,
        string? folderPath)
    {
        if (sourceFile == null || pathId == 0 || scanResultSnapshot == null || string.IsNullOrWhiteSpace(folderPath))
        {
            return null;
        }

        try
        {
            var selfUid = AssetHandle.BuildUniqueID(sourceFile, pathId);
            if (string.IsNullOrWhiteSpace(selfUid))
            {
                return null;
            }

            var signature = _sqliteCache.GetFolderSignature(scanResultSnapshot);
            var usedBy = new List<AssetReferenceItem>();
            var uses = new List<AssetReferenceItem>();
            _sqliteCache.LoadAssetReferencesFromSemanticCache(folderPath, signature, selfUid, usedBy, uses, maxPerDirection: 256);
            if (uses.Count == 0)
            {
                return null;
            }

            var lookup = new Dictionary<string, AssetReferenceItem>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < uses.Count; i++)
            {
                var item = uses[i];
                if (!string.IsNullOrWhiteSpace(item.AssetUniqueId) && !lookup.ContainsKey(item.AssetUniqueId))
                {
                    lookup[item.AssetUniqueId] = item;
                }
            }

            return lookup.Count > 0 ? lookup : null;
        }
        catch
        {
            return null;
        }
    }

    internal AssetReferenceItem? ResolvePPtrMetadataForInspector(
        SerializedFile? sourceFile,
        int fileId,
        long pathId,
        ClassIDType fallbackType,
        string relation,
        Dictionary<string, AssetReferenceItem>? semanticLookup)
    {
        if (pathId == 0)
        {
            return null;
        }

        // 1) In-memory already-loaded object (NEVER triggers disk/bundle loads in Lazy Mode).
        AssetStudio.Object? loadedObj = null;
        string serializedFileName = string.Empty;
        string uniqueId = string.Empty;

        if (sourceFile != null)
        {
            if (fileId == 0)
            {
                serializedFileName = sourceFile.fileName ?? string.Empty;
                uniqueId = AssetHandle.BuildUniqueID(sourceFile, pathId);
                sourceFile.TryGetObject(pathId, out loadedObj);
            }
            else if (fileId > 0 && sourceFile.m_Externals != null && fileId - 1 < sourceFile.m_Externals.Count)
            {
                var ext = sourceFile.m_Externals[fileId - 1];
                serializedFileName = NormalizeExternalFileNameForInspector(ext?.fileName, ext?.pathName);
                if (assetsManager.TryFindSerializedFile(ext?.fileName, ext?.pathName, out var extFile) && extFile != null)
                {
                    serializedFileName = extFile.fileName ?? serializedFileName;
                    uniqueId = AssetHandle.BuildUniqueID(extFile, pathId);
                    extFile.TryGetObject(pathId, out loadedObj);
                }
                else if (!string.IsNullOrWhiteSpace(serializedFileName))
                {
                    uniqueId = $"{serializedFileName}#{pathId.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
                }
            }
        }

        if (loadedObj != null)
        {
            var objName = loadedObj switch
            {
                NamedObject named when !string.IsNullOrWhiteSpace(named.m_Name) => named.m_Name,
                GameObject go when !string.IsNullOrWhiteSpace(go.m_Name) => go.m_Name,
                MonoBehaviour mb when !string.IsNullOrWhiteSpace(mb.m_Name) => mb.m_Name,
                _ => $"{AssetItem.GetCachedTypeString(loadedObj.type)} ({loadedObj.m_PathID.ToString(System.Globalization.CultureInfo.InvariantCulture)})"
            };

            return new AssetReferenceItem
            {
                AssetUniqueId = loadedObj.assetsFile != null ? AssetHandle.BuildUniqueID(loadedObj.assetsFile, loadedObj.m_PathID) : uniqueId,
                Name = objName,
                AssetType = loadedObj.type,
                TypeName = AssetItem.GetCachedTypeString(loadedObj.type),
                Relation = relation,
                PathId = loadedObj.m_PathID,
                SerializedFileName = loadedObj.assetsFile?.fileName ?? serializedFileName,
                Direction = AssetReferenceDirection.References
            };
        }

        // 2) ProjectIndex O(1) in-memory lookup (zero disk I/O).
        AssetHandle? handle = !string.IsNullOrWhiteSpace(uniqueId)
            ? assetsManager.ProjectIndex.GetHandle(uniqueId)
            : null;
        if (handle == null && !string.IsNullOrWhiteSpace(serializedFileName))
        {
            handle = assetsManager.ProjectIndex.GetHandlesForFile(serializedFileName)
                .FirstOrDefault(h => h.PathID == pathId);
        }

        if (handle != null)
        {
            return new AssetReferenceItem
            {
                AssetUniqueId = handle.UniqueID,
                Name = !string.IsNullOrWhiteSpace(handle.Name) ? handle.Name : $"{handle.Type} ({handle.PathID})",
                AssetType = handle.Type,
                TypeName = AssetItem.GetCachedTypeString(handle.Type),
                Relation = relation,
                PathId = handle.PathID,
                SerializedFileName = handle.SerializedFileName ?? serializedFileName,
                Direction = AssetReferenceDirection.References
            };
        }

        // 3) SQLite semantic cache fallback (preloaded for this asset without touching bundles).
        if (semanticLookup != null && !string.IsNullOrWhiteSpace(uniqueId) && semanticLookup.TryGetValue(uniqueId, out var cachedRef))
        {
            return new AssetReferenceItem
            {
                AssetUniqueId = cachedRef.AssetUniqueId,
                Name = cachedRef.Name,
                AssetType = cachedRef.AssetType,
                TypeName = cachedRef.TypeName,
                Relation = !string.IsNullOrWhiteSpace(relation) ? relation : cachedRef.Relation,
                PathId = cachedRef.PathId != 0 ? cachedRef.PathId : pathId,
                SerializedFileName = !string.IsNullOrWhiteSpace(cachedRef.SerializedFileName) ? cachedRef.SerializedFileName : serializedFileName,
                Direction = AssetReferenceDirection.References
            };
        }

        // 4) Local SerializedFile ObjectInfo metadata fallback.
        var resolvedType = fallbackType;
        if (fileId == 0 && sourceFile != null && sourceFile.ObjectInfoDic != null && sourceFile.ObjectInfoDic.TryGetValue(pathId, out var objInfo))
        {
            resolvedType = (ClassIDType)objInfo.classID;
        }

        var typeLabel = resolvedType != ClassIDType.UnknownType
            ? AssetItem.GetCachedTypeString(resolvedType)
            : "Object";
        return new AssetReferenceItem
        {
            AssetUniqueId = uniqueId,
            Name = $"{typeLabel} ({pathId.ToString(System.Globalization.CultureInfo.InvariantCulture)})",
            AssetType = resolvedType,
            TypeName = typeLabel,
            Relation = relation,
            PathId = pathId,
            SerializedFileName = serializedFileName,
            Direction = AssetReferenceDirection.References
        };
    }

    private string ResolveMonoBehaviourScriptTitle(MonoBehaviour monoBehaviour, AssetItem assetItem, SerializedFile? sourceFile)
    {
        if (monoBehaviour.m_Script != null && !monoBehaviour.m_Script.IsNull && sourceFile != null)
        {
            AssetStudio.Object? scriptObj = null;
            if (monoBehaviour.m_Script.m_FileID == 0)
            {
                sourceFile.TryGetObject(monoBehaviour.m_Script.m_PathID, out scriptObj);
            }
            else if (monoBehaviour.m_Script.m_FileID > 0
                && sourceFile.m_Externals != null
                && monoBehaviour.m_Script.m_FileID - 1 < sourceFile.m_Externals.Count)
            {
                var ext = sourceFile.m_Externals[monoBehaviour.m_Script.m_FileID - 1];
                if (assetsManager.TryFindSerializedFile(ext?.fileName, ext?.pathName, out var extFile) && extFile != null)
                {
                    extFile.TryGetObject(monoBehaviour.m_Script.m_PathID, out scriptObj);
                }
            }

            if (scriptObj is MonoScript ms && !string.IsNullOrWhiteSpace(ms.m_ClassName))
            {
                var fullType = !string.IsNullOrWhiteSpace(ms.m_Namespace)
                    ? $"{ms.m_Namespace}.{ms.m_ClassName}"
                    : ms.m_ClassName;
                if (!string.IsNullOrWhiteSpace(assetItem.Name)
                    && !string.Equals(assetItem.Name, ms.m_ClassName, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(assetItem.Name, fullType, StringComparison.OrdinalIgnoreCase))
                {
                    return $"{assetItem.Name} ({fullType})";
                }

                return fullType;
            }
        }

        if (!string.IsNullOrWhiteSpace(assetItem.Name))
        {
            return assetItem.Name;
        }

        if (!string.IsNullOrWhiteSpace(monoBehaviour.m_Name))
        {
            return monoBehaviour.m_Name;
        }

        return "MonoBehaviour";
    }

    private static string NormalizeExternalFileNameForInspector(string? fileName, string? pathName)
    {
        var candidate = !string.IsNullOrWhiteSpace(fileName) ? fileName! : (pathName ?? string.Empty);
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return string.Empty;
        }

        var normalized = candidate.Replace('\\', '/').TrimEnd('/');
        var slash = normalized.LastIndexOf('/');
        return slash >= 0 ? normalized.Substring(slash + 1) : normalized;
    }

    private void ApplyMonoBehaviourTreePreview(
        AssetItem assetItem,
        long requestId,
        long previewVersion,
        CancellationToken token,
        PreparedMonoBehaviourTreeResult prepared)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsPreviewRequestCurrent(requestId, previewVersion, token))
            {
                return;
            }

            PreviewSession.ShowMonoBehaviourTree(
                assetItem,
                prepared.TreeResult,
                prepared.FullJsonText,
                prepared.DisplayJsonText);
            TextPreviewBox.Text = string.Empty;
            TextPreviewBox.IsVisible = false;
            PreviewLabel.IsVisible = false;
            StatusStripUpdate(prepared.StatusMessage);
        });
    }

    private PreparedTmpFontPreviewResult? TryPrepareTmpFontPreviewBackground(
        MonoBehaviour monoBehaviour,
        OrderedDictionary fields,
        CancellationToken token)
    {
        if (!TmpFontPreviewService.TryExtract(monoBehaviour, fields, out var tmpInfo) || tmpInfo == null)
        {
            return null;
        }

        token.ThrowIfCancellationRequested();
        var atlasTexture = ResolveTmpFontAtlasTexture(monoBehaviour, tmpInfo);
        if (atlasTexture == null)
        {
            return null;
        }

        token.ThrowIfCancellationRequested();
        SetActivePreviewDependencyFiles(monoBehaviour, atlasTexture);

        using var previewImage = LoadTexturePreviewThumbnail(atlasTexture, MaxInlinePreviewTextureDimension);
        var image = previewImage?.Image;
        if (image == null)
        {
            return null;
        }

        FontAssetPreviewRenderer.EnhanceFontAtlasVisibility(image);
        token.ThrowIfCancellationRequested();

        var bitmap = CreateWriteableBitmapFromSKBitmap(image);
        var fallbackNames = ResolveTmpFallbackFontNames(monoBehaviour, tmpInfo);
        var infoText = tmpInfo.FormatInfoOverlay(atlasTexture, fallbackNames);
        var status = $"TMP_FontAsset loaded: {tmpInfo.DisplayName} ({atlasTexture.m_Width}x{atlasTexture.m_Height})";

        return new PreparedTmpFontPreviewResult
        {
            Bitmap = bitmap,
            InfoText = infoText,
            StatusMessage = status
        };
    }

    private Texture2D? ResolveTmpFontAtlasTexture(MonoBehaviour monoBehaviour, TmpFontAssetInfo tmpInfo)
    {
        var sourceFile = monoBehaviour.assetsFile;
        if (sourceFile == null)
        {
            return null;
        }

        for (var i = 0; i < tmpInfo.AtlasTextureRefs.Count; i++)
        {
            var texRef = tmpInfo.AtlasTextureRefs[i];
            if (!texRef.IsValid)
            {
                continue;
            }

            var texPtr = new PPtr<Texture>(texRef.FileId, texRef.PathId, sourceFile);
            if (texPtr.TryGet<Texture2D>(out var directTex) && directTex != null)
            {
                return directTex;
            }

            var lazyTex = ResolveTexturePPtrForOwnerPreview(monoBehaviour, texPtr);
            if (lazyTex != null)
            {
                return lazyTex;
            }
        }

        if (tmpInfo.MaterialRef is { IsValid: true } matRef)
        {
            var matPtr = new PPtr<Material>(matRef.FileId, matRef.PathId, sourceFile);
            if (matPtr.TryGet(out var mat) && mat != null)
            {
                var matTex = FindTextureForMaterial(mat);
                if (matTex != null)
                {
                    return matTex;
                }
            }
        }

        return null;
    }

    private List<string> ResolveTmpFallbackFontNames(MonoBehaviour monoBehaviour, TmpFontAssetInfo tmpInfo)
    {
        var names = new List<string>();
        var sourceFile = monoBehaviour.assetsFile;
        if (sourceFile == null || tmpInfo.FallbackFontRefs.Count == 0)
        {
            return names;
        }

        var maxNames = Math.Min(tmpInfo.FallbackFontRefs.Count, 8);
        for (var i = 0; i < maxNames; i++)
        {
            var fbRef = tmpInfo.FallbackFontRefs[i];
            if (!fbRef.IsValid)
            {
                continue;
            }

            string? name = null;
            if (fbRef.FileId == 0)
            {
                if (sourceFile.TryGetObject(fbRef.PathId, out var obj))
                {
                    name = obj is MonoBehaviour fbMb && !string.IsNullOrWhiteSpace(fbMb.m_Name)
                        ? fbMb.m_Name
                        : (obj as NamedObject)?.m_Name;
                }

                if (string.IsNullOrWhiteSpace(name))
                {
                    var key = AssetHandle.BuildUniqueID(sourceFile, fbRef.PathId);
                    name = assetsManager.ProjectIndex.GetHandle(key)?.Name;
                }
            }
            else if (fbRef.FileId > 0 && sourceFile.m_Externals != null && fbRef.FileId - 1 < sourceFile.m_Externals.Count)
            {
                var ext = sourceFile.m_Externals[fbRef.FileId - 1];
                if (assetsManager.TryFindSerializedFile(ext?.fileName, ext?.pathName, out var extFile) && extFile != null)
                {
                    if (extFile.TryGetObject(fbRef.PathId, out var extObj))
                    {
                        name = extObj is MonoBehaviour fbMb && !string.IsNullOrWhiteSpace(fbMb.m_Name)
                            ? fbMb.m_Name
                            : (extObj as NamedObject)?.m_Name;
                    }

                    if (string.IsNullOrWhiteSpace(name))
                    {
                        var key = AssetHandle.BuildUniqueID(extFile, fbRef.PathId);
                        name = assetsManager.ProjectIndex.GetHandle(key)?.Name;
                    }
                }

                if (string.IsNullOrWhiteSpace(name))
                {
                    var extName = !string.IsNullOrWhiteSpace(ext?.fileName) ? ext!.fileName : ext?.pathName;
                    if (!string.IsNullOrWhiteSpace(extName))
                    {
                        var normalized = extName.Replace('\\', '/').TrimEnd('/');
                        var slash = normalized.LastIndexOf('/');
                        var leaf = slash >= 0 ? normalized.Substring(slash + 1) : normalized;
                        name = assetsManager.ProjectIndex
                            .GetHandlesForFile(leaf)
                            .FirstOrDefault(h => h.PathID == fbRef.PathId)?.Name;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(name))
            {
                names.Add(name!);
            }
        }

        return names;
    }

    private void ApplyTmpFontPreview(
        AssetItem assetItem,
        long requestId,
        long previewVersion,
        CancellationToken token,
        PreparedTmpFontPreviewResult tmpPreview)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsPreviewRequestCurrent(requestId, previewVersion, token))
            {
                tmpPreview.Bitmap.Dispose();
                return;
            }

            PreviewSession.ActivateSurface(
                PreviewSurfaceKind.Font,
                assetItem,
                metadataSummary: tmpPreview.InfoText,
                isLoading: false);
            ImagePreviewBox.Source = tmpPreview.Bitmap;
            ImagePreviewBox.IsVisible = true;
            TextPreviewBox.IsVisible = false;
            PreviewLabel.IsVisible = false;
            SetPreviewInfoOverlay(tmpPreview.InfoText);
            StatusStripUpdate(tmpPreview.StatusMessage);
        });
    }

    private void ApplyMonoBehaviourPreviewText(
        long requestId,
        long previewVersion,
        CancellationToken token,
        string? truncatedText,
        string statusMessage)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsPreviewRequestCurrent(requestId, previewVersion, token))
            {
                return;
            }

            if (truncatedText != null)
            {
                PreviewSession.ShowTextPreview(truncatedText, PreviewSurfaceKind.MonoBehaviour, truncate: false);
                TextPreviewBox.Text = truncatedText;
                TextPreviewBox.IsVisible = true;
                PreviewLabel.IsVisible = false;
            }
            else
            {
                PreviewSession.ShowTextPreview(string.Empty, PreviewSurfaceKind.MonoBehaviour, truncate: false);
                TextPreviewBox.Text = string.Empty;
                TextPreviewBox.IsVisible = false;
                PreviewLabel.IsVisible = ViewModel.Options.DisplayInfo;
            }

            StatusStripUpdate(statusMessage);
        });
    }
}
