using AssetStudio;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AssetStudio.Avalonia;

public partial class MainWindow
{
    private CancellationTokenSource? preloaderCts;
    private readonly object preloaderLock = new();
    private readonly HashSet<string> preloadedUniqueIds = new();
    private const int AssetPreloadWindowSize = 50;

    private void PreprocessPreloadedAssetForPreview(AssetItem item, CancellationToken token)
    {
        if (token.IsCancellationRequested || item.Handle?.RealObject is not Mesh mesh)
        {
            return;
        }

        mesh.EnsureProcessed();
    }

    private void UnloadAsset(AssetHandle handle)
    {
        if (handle == null) return;

        lock (handle)
        {
            var obj = handle.RealObject;
            handle.RealObject = null;

            if (handle.Tag is AssetItem assetItem)
            {
                assetItem.Asset = null;
            }

            if (obj != null)
            {
                var assetsFile = handle.SourceFile;
                if (assetsFile != null)
                {
                    assetsFile.RemoveObject(obj);
                }
            }
        }
    }

    private void UpdatePreloadWindow(AssetItem currentItem)
    {
        if (!assetsManager.LazyLoading) return;
        if (IsProgressiveIndexingActive()) return;

        lock (preloaderLock)
        {
            preloaderCts?.Cancel();
            preloaderCts?.Dispose();
            preloaderCts = new CancellationTokenSource();
            var token = preloaderCts.Token;

            var preloadItems = visibleAssetItems
                .Where(item => item.Type == currentItem.Type)
                .ToList();

            var currentIdx = preloadItems.IndexOf(currentItem);
            if (currentIdx < 0) return;

            int total = preloadItems.Count;
            int windowSize = Math.Min(AssetPreloadWindowSize, total);
            int start = currentIdx - windowSize / 2;
            if (start < 0) start = 0;
            if (start + windowSize > total) start = total - windowSize;
            int end = start + windowSize - 1;

            var itemsToKeep = new HashSet<string>();
            var itemsToLoad = new List<AssetItem>();

            for (int i = start; i <= end; i++)
            {
                var item = preloadItems[i];
                if (item.Handle != null && !string.IsNullOrEmpty(item.Handle.UniqueID))
                {
                    itemsToKeep.Add(item.Handle.UniqueID);
                    if (item.Handle.RealObject == null)
                    {
                        itemsToLoad.Add(item);
                    }
                }
            }

            var toUnload = new List<string>();
            foreach (var uid in preloadedUniqueIds)
            {
                if (!itemsToKeep.Contains(uid))
                {
                    toUnload.Add(uid);
                }
            }

            foreach (var uid in toUnload)
            {
                preloadedUniqueIds.Remove(uid);
                var handle = assetsManager.ProjectIndex.GetHandle(uid);
                if (handle != null)
                {
                    UnloadAsset(handle);
                }
            }

            var sortedItemsToLoad = itemsToLoad
                .OrderBy(item =>
                {
                    var idx = preloadItems.IndexOf(item);
                    return idx >= 0 ? Math.Abs(idx - currentIdx) : int.MaxValue;
                })
                .ToList();

            _ = Task.Run(async () =>
            {
                foreach (var item in sortedItemsToLoad)
                {
                    if (token.IsCancellationRequested) break;

                    try
                    {
                        if (token.IsCancellationRequested) break;

                        if (item.Handle?.UniqueID == null) continue;

                        bool alreadyLoaded;
                        lock (preloaderLock)
                        {
                            alreadyLoaded = preloadedUniqueIds.Contains(item.Handle.UniqueID);
                        }

                        if (alreadyLoaded)
                        {
                            continue;
                        }

                        if (item.Handle.RealObject != null)
                        {
                            PreprocessPreloadedAssetForPreview(item, token);
                            lock (preloaderLock)
                            {
                                preloadedUniqueIds.Add(item.Handle.UniqueID);
                            }
                            continue;
                        }

                        EnsureLazyAssetReadyForPreview(item);
                        assetsManager.ResolveHandle(item.Handle);

                        if (item.Handle?.UniqueID != null && item.Handle.RealObject != null)
                        {
                            PreprocessPreloadedAssetForPreview(item, token);
                            lock (preloaderLock)
                            {
                                preloadedUniqueIds.Add(item.Handle.UniqueID);
                            }
                        }
                    }
                    catch
                    {
                        // Ignore individual load errors
                    }

                    await Task.Delay(15, token);
                }
            }, token);
        }
    }
}
