using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using AssetStudio.Avalonia;
using Avalonia.Collections;
using Xunit;
using Xunit.Abstractions;

namespace AssetStudio.Tests
{
    public class VirtualAssetListTests
    {
        private readonly ITestOutputHelper _output;

        public VirtualAssetListTests(ITestOutputHelper output)
        {
            _output = output;
        }

        private sealed class ReadOnlySliceList<T> : IReadOnlyList<T>
        {
            private readonly T[] _items;

            public ReadOnlySliceList(params T[] items)
            {
                _items = items;
            }

            public T this[int index] => _items[index];

            public int Count => _items.Length;

            public IEnumerator<T> GetEnumerator()
            {
                for (var i = 0; i < _items.Length; i++)
                {
                    yield return _items[i];
                }
            }

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }

        [Fact]
        public void VirtualAssetList_DirectListBacking_SwapsInO1WithZeroCopyAndSingleReset()
        {
            var listA = CreateSlimItems(120, seedOffset: 0);
            var listB = CreateSlimItems(320, seedOffset: 1000);

            var virtualList = new VirtualAssetList();
            var resetCount = 0;
            var propertyNames = new List<string?>();

            virtualList.CollectionChanged += (_, e) =>
            {
                if (e.Action == NotifyCollectionChangedAction.Reset)
                {
                    resetCount++;
                }
            };
            virtualList.PropertyChanged += (_, e) => propertyNames.Add(e.PropertyName);

            virtualList.ReplaceBacking(listA);

            Assert.Equal(120, virtualList.Count);
            Assert.False(virtualList.IsReadOnly);
            Assert.False(virtualList.IsFixedSize);
            Assert.Equal(1, resetCount);
            Assert.Contains("Count", propertyNames);
            Assert.Contains("Item[]", propertyNames);
            for (var i = 0; i < listA.Count; i++)
            {
                Assert.Same(listA[i], virtualList[i]);
            }

            Assert.Equal(0, virtualList.IndexOf(listA[0]));
            Assert.Equal(119, virtualList.IndexOf(listA[119]));
            var containsPresent = virtualList.Contains(listA[42]);
            var containsMissing = virtualList.Contains(listB[0]);
            Assert.True(containsPresent);
            Assert.False(containsMissing);
            Assert.Equal(-1, ((IList)virtualList).IndexOf("not-an-asset-item"));

            var copyTarget = new AssetItem[130];
            virtualList.CopyTo(copyTarget, 5);
            Assert.Null(copyTarget[4]);
            Assert.Same(listA[0], copyTarget[5]);
            Assert.Same(listA[119], copyTarget[124]);

            // Swap to listB with single Reset and zero copies.
            resetCount = 0;
            propertyNames.Clear();
            virtualList.ReplaceBacking(listB);

            Assert.Equal(1, resetCount);
            Assert.Equal(320, virtualList.Count);
            Assert.Same(listB[0], virtualList[0]);
            Assert.Same(listB[319], virtualList[319]);
            var containsOldAfterSwap = virtualList.Contains(listA[0]);
            Assert.False(containsOldAfterSwap);

            // Repeated IndexOf lookups (>= 4 on >= 256 items) exercise lazy O(1) index cache and still preserve exact indices.
            for (var k = 0; k < 10; k++)
            {
                var probeIdx = (k * 31) % listB.Count;
                Assert.Equal(probeIdx, virtualList.IndexOf(listB[probeIdx]));
            }

            // Enumeration parity
            Assert.Equal(listB, virtualList.ToList());

            // BeginUpdate/EndUpdate coalesces mutations into 1 Reset.
            resetCount = 0;
            virtualList.BeginUpdate();
            var extra0 = CreateSlimItems(1, 5000)[0];
            var extra1 = CreateSlimItems(1, 5001)[0];
            virtualList.Add(extra0);
            virtualList.Insert(0, extra1);
            Assert.Equal(0, resetCount);
            virtualList.EndUpdate();
            Assert.Equal(1, resetCount);
            Assert.Same(extra1, virtualList[0]);
            Assert.Same(extra0, virtualList[^1]);

            // Simulate ResetForm(): backing list cleared externally before calling virtualList.Clear().
            resetCount = 0;
            listB.Clear();
            virtualList.Clear();
            var countAfterClear = virtualList.Count;
            Assert.Equal(0, countAfterClear);
            Assert.Empty(virtualList);
            Assert.Equal(1, resetCount);
        }

        [Fact]
        public void VirtualAssetList_DirectArraySliceBacking_ExposesSliceBoundsAndSingleReset()
        {
            var items = CreateSlimItems(20, seedOffset: 0).ToArray();
            var virtualList = new VirtualAssetList();
            var resetCount = 0;
            virtualList.CollectionChanged += (_, e) =>
            {
                if (e.Action == NotifyCollectionChangedAction.Reset)
                {
                    resetCount++;
                }
            };

            Assert.Throws<ArgumentOutOfRangeException>(() => virtualList.ReplaceBacking(items, 21));

            virtualList.ReplaceBacking(items, 12);
            Assert.Equal(1, resetCount);
            Assert.Equal(12, virtualList.Count);
            Assert.True(virtualList.IsReadOnly);
            Assert.True(virtualList.IsFixedSize);
            Assert.Same(items[11], virtualList[11]);
            Assert.Throws<ArgumentOutOfRangeException>(() => _ = virtualList[12]);

            Assert.Equal(5, virtualList.IndexOf(items[5]));
            Assert.Equal(-1, virtualList.IndexOf(items[15]));
            var sliceContainsOutside = virtualList.Contains(items[15]);
            Assert.False(sliceContainsOutside);

            var sliceCopy = new AssetItem[12];
            virtualList.CopyTo(sliceCopy, 0);
            Assert.Equal(items.Take(12), sliceCopy);
            Assert.Equal(items.Take(12), virtualList.ToArray());

            Assert.Throws<NotSupportedException>(() => virtualList.Add(items[15]));
            Assert.Throws<NotSupportedException>(() => virtualList.Insert(0, items[15]));
            Assert.Throws<NotSupportedException>(() => virtualList.RemoveAt(0));
            Assert.Throws<NotSupportedException>(() => virtualList[0] = items[15]);

            resetCount = 0;
            virtualList.Clear();
            var sliceCountAfterClear = virtualList.Count;
            Assert.Equal(0, sliceCountAfterClear);
            Assert.Empty(virtualList);
            Assert.Equal(1, resetCount);
            Assert.False(virtualList.IsReadOnly);
        }

        [Fact]
        public void VirtualAssetList_FlyweightLruProviderMode_BoundsMaterializedCacheAcross100kRows()
        {
            const int totalRows = 100_000;
            const int lruCapacity = 512;

            var materializeCalls = 0;
            var virtualList = new VirtualAssetList();
            var resetCount = 0;
            virtualList.CollectionChanged += (_, e) =>
            {
                if (e.Action == NotifyCollectionChangedAction.Reset)
                {
                    resetCount++;
                }
            };

            virtualList.ReplaceWithProvider(
                totalRows,
                index =>
                {
                    materializeCalls++;
                    return new AssetItem(new AssetHandle
                    {
                        UniqueID = $"arena#{index}",
                        Name = $"VirtualAsset_{index}",
                        Type = (index % 3) switch
                        {
                            0 => ClassIDType.Texture2D,
                            1 => ClassIDType.Mesh,
                            _ => ClassIDType.Material
                        },
                        Container = $"assets/virtual/chunk_{index / 100}/item_{index}.asset",
                        PathID = 10_000L + index,
                        ByteSize = 512 + (index % 4096),
                    }, index);
                },
                lruCapacity);

            Assert.Equal(1, resetCount);
            Assert.Equal(totalRows, virtualList.Count);
            Assert.True(virtualList.IsReadOnly);
            Assert.True(virtualList.IsFixedSize);
            Assert.Equal(0, virtualList.CachedItemCount);
            Assert.Equal(0, materializeCalls);

            // Simulate scrolling across 100,000 rows in multiple viewports (start, middle, end, random jumps).
            for (var start = 0; start < totalRows; start += 2500)
            {
                for (var row = start; row < Math.Min(totalRows, start + 60); row++)
                {
                    var item = virtualList[row];
                    Assert.Equal($"VirtualAsset_{row}", item.Name);
                    Assert.Equal(10_000L + row, item.PathID);
                    Assert.True(virtualList.CachedItemCount <= lruCapacity);
                }
            }

            // Scroll through a contiguous 500-row window at the end and verify cache hit rate is 100% inside window.
            var windowStart = 99_400;
            var windowEnd = windowStart + 400;
            var callsBeforeWindow = materializeCalls;
            var windowItems = new AssetItem[400];
            for (var i = 0; i < 400; i++)
            {
                windowItems[i] = virtualList[windowStart + i];
            }

            Assert.Equal(callsBeforeWindow + 400, materializeCalls);
            Assert.Equal(lruCapacity, virtualList.CachedItemCount);

            // Re-reading the window + IndexOf/Contains must incur ZERO additional materializer calls.
            var callsAfterWarmWindow = materializeCalls;
            for (var i = 0; i < 400; i++)
            {
                Assert.Same(windowItems[i], virtualList[windowStart + i]);
                Assert.Equal(windowStart + i, virtualList.IndexOf(windowItems[i]));
                var containsWindowItem = virtualList.Contains(windowItems[i]);
                Assert.True(containsWindowItem);
            }
            Assert.Equal(callsAfterWarmWindow, materializeCalls);

            // Verify strict LRU eviction order with small capacity = 3.
            var smallMaterializations = 0;
            virtualList.ReplaceWithProvider(
                10,
                idx =>
                {
                    smallMaterializations++;
                    return new AssetItem(new AssetHandle { PathID = idx + 1, Name = $"Small_{idx}" });
                },
                lruCapacity: 3);

            var r0 = virtualList[0];
            var r1 = virtualList[1];
            var r2 = virtualList[2];
            Assert.Equal(3, virtualList.CachedItemCount);
            Assert.Equal(3, smallMaterializations);

            // Touch row 0 -> promotes row 0 to MRU, making row 1 the LRU victim.
            Assert.Same(r0, virtualList[0]);
            Assert.Equal(3, smallMaterializations);

            // Access row 3 -> evicts row 1; rows {0, 2, 3} remain cached.
            var r3 = virtualList[3];
            Assert.Equal(3, virtualList.CachedItemCount);
            Assert.Equal(4, smallMaterializations);
            Assert.Same(r0, virtualList[0]);
            Assert.Same(r2, virtualList[2]);
            Assert.Same(r3, virtualList[3]);
            Assert.Equal(4, smallMaterializations);

            // Accessing evicted row 1 triggers fresh materialization, and IndexOf on old r1 returns -1.
            Assert.Equal(-1, virtualList.IndexOf(r1));
            var r1Reloaded = virtualList[1];
            Assert.NotSame(r1, r1Reloaded);
            Assert.Equal(5, smallMaterializations);
        }

        [Fact]
        public void VirtualAssetList_ImplementsIDataGridCollectionView_CurrencyAndSelectionTrackingWorkCleanly()
        {
            var items = CreateSlimItems(10, seedOffset: 0);
            var virtualList = new VirtualAssetList();
            IDataGridCollectionView view = virtualList;

            Assert.False(view.CanSort);
            Assert.False(view.CanFilter);
            Assert.False(view.CanGroup);
            Assert.False(view.IsGrouping);
            Assert.Equal(0, view.GroupingDepth);
            Assert.True(view.IsEmpty);
            Assert.Same(virtualList, view.SourceCollection);

            virtualList.ReplaceBacking(items);
            Assert.False(view.IsEmpty);
            Assert.Equal(-1, view.CurrentPosition);
            Assert.Null(view.CurrentItem);

            var currentChangedCount = 0;
            view.CurrentChanged += (_, _) => currentChangedCount++;

            Assert.True(view.MoveCurrentToFirst());
            Assert.Equal(0, view.CurrentPosition);
            Assert.Same(items[0], view.CurrentItem);
            Assert.Equal(1, currentChangedCount);

            Assert.True(view.MoveCurrentTo(items[4]));
            Assert.Equal(4, view.CurrentPosition);
            Assert.Same(items[4], view.CurrentItem);
            Assert.True(view.Contains(items[4]));

            Assert.True(view.MoveCurrentToNext());
            Assert.Equal(5, view.CurrentPosition);
            Assert.True(view.MoveCurrentToPrevious());
            Assert.Equal(4, view.CurrentPosition);
            Assert.True(view.MoveCurrentToLast());
            Assert.Equal(9, view.CurrentPosition);

            // Cancellable CurrentChanging
            EventHandler<DataGridCurrentChangingEventArgs> cancelHandler = (_, e) => e.Cancel = true;
            view.CurrentChanging += cancelHandler;
            view.MoveCurrentToPosition(2);
            Assert.Equal(9, view.CurrentPosition);
            view.CurrentChanging -= cancelHandler;

            // Silent currency position update on ReplaceBacking (when current item moves to a new index).
            var currentChangedBeforeSwap = currentChangedCount;
            Assert.True(view.MoveCurrentTo(items[3]));
            currentChangedBeforeSwap = currentChangedCount;
            var reordered = new List<AssetItem> { items[9], items[8], items[3], items[0] };
            virtualList.ReplaceBacking(reordered);
            Assert.Equal(2, view.CurrentPosition);
            Assert.Same(items[3], view.CurrentItem);
            Assert.Equal(currentChangedBeforeSwap, currentChangedCount);
        }

        [Fact]
        public void BulkObservableCollection_ReplaceAll_UpdatesInSingleResetWithoutTemporaryCopy()
        {
            var collection = new BulkObservableCollection<string>(new[] { "old1", "old2" });
            var resetCount = 0;
            var propNames = new List<string?>();
            collection.CollectionChanged += (_, e) =>
            {
                if (e.Action == NotifyCollectionChangedAction.Reset)
                {
                    resetCount++;
                }
                else
                {
                    Assert.Fail($"Unexpected collection action: {e.Action}");
                }
            };
            ((INotifyPropertyChanged)collection).PropertyChanged += (_, e) => propNames.Add(e.PropertyName);

            var customReadOnly = new ReadOnlySliceList<string>("alpha", "beta", "gamma");
            collection.ReplaceAll(customReadOnly);

            Assert.Equal(1, resetCount);
            Assert.Equal(new[] { "Count", "Item[]" }, propNames);
            Assert.Equal(new[] { "alpha", "beta", "gamma" }, collection.ToArray());

            resetCount = 0;
            collection.ReplaceAll(Array.Empty<string>());
            Assert.Equal(1, resetCount);
            Assert.Empty(collection);

            Assert.Throws<ArgumentNullException>(() => collection.ReplaceAll(null!));
        }

        [Fact]
        public void AssetItem_SlimProperties_StaticTypeCacheLazyPathIdAndOrdinalParity()
        {
            var handleA = new AssetHandle
            {
                Type = ClassIDType.Texture2D,
                PathID = -9876543210123L,
                ByteSize = 4096,
                Name = "TexA",
                Container = "assets/tex/a.png"
            };
            var handleB = new AssetHandle
            {
                Type = ClassIDType.Texture2D,
                PathID = 42L,
                ByteSize = 2048,
                Name = "TexB",
                Container = "assets/tex/b.png"
            };
            var itemA = new AssetItem(handleA, ordinal: 7);
            var itemB = new AssetItem(handleB, ordinal: 19);

            // Static cached TypeString & DisplayType: zero alloc and reference identical across items.
            Assert.Equal("Texture2D", itemA.TypeString);
            Assert.Same(itemA.TypeString, itemB.TypeString);
            Assert.Same(itemA.TypeString, itemA.DisplayType);
            Assert.Same(itemB.TypeString, itemB.DisplayType);

            // Sparse enum and undefined enum TypeString caching.
            var prefabA = new AssetItem(new AssetHandle { Type = ClassIDType.PrefabInstance });
            var prefabB = new AssetItem(new AssetHandle { Type = ClassIDType.PrefabInstance });
            Assert.Equal("PrefabInstance", prefabA.TypeString);
            Assert.Same(prefabA.TypeString, prefabB.TypeString);
            Assert.Equal("Prefab (Composite)", prefabA.DisplayType);
            Assert.Same(prefabA.DisplayType, prefabB.DisplayType);

            var unknownA = new AssetItem(new AssetHandle { Type = ClassIDType.UnknownType });
            var unknownB = new AssetItem(new AssetHandle { Type = ClassIDType.UnknownType });
            Assert.Equal("UnknownType", unknownA.TypeString);
            Assert.Same(unknownA.TypeString, unknownB.TypeString);

            var customEnumA = new AssetItem(new AssetHandle { Type = (ClassIDType)987654321 });
            var customEnumB = new AssetItem(new AssetHandle { Type = (ClassIDType)987654321 });
            Assert.Equal("987654321", customEnumA.TypeString);
            Assert.Same(customEnumA.TypeString, customEnumB.TypeString);

            // FBX sub-asset DisplayType is also statically cached.
            var fbxCompA = new AssetItem(new AssetHandle { Type = ClassIDType.MeshRenderer, Container = "assets/m1.FBX/node" });
            var fbxCompB = new AssetItem(new AssetHandle { Type = ClassIDType.MeshRenderer, Container = "assets/m2.fbx/node" });
            Assert.Equal("MeshRenderer (Component) (FBX sub-asset)", fbxCompA.DisplayType);
            Assert.Same(fbxCompA.DisplayType, fbxCompB.DisplayType);

            // PathIDString parity including edge values & mutation.
            Assert.Equal("-9876543210123", itemA.PathIDString);
            Assert.Equal("42", itemB.PathIDString);
            itemB.PathID = long.MinValue;
            Assert.Equal(long.MinValue.ToString(CultureInfo.InvariantCulture), itemB.PathIDString);
            itemB.PathID = long.MaxValue;
            Assert.Equal(long.MaxValue.ToString(CultureInfo.InvariantCulture), itemB.PathIDString);

            // Custom overrides still work cleanly if explicitly assigned.
            itemB.PathIDString = "custom-path-id";
            Assert.Equal("custom-path-id", itemB.PathIDString);
            itemB.TypeString = "CustomType";
            Assert.Equal("CustomType", itemB.TypeString);
            Assert.Equal("CustomType", itemB.DisplayType);
            itemB.TypeString = nameof(ClassIDType.Texture2D);
            Assert.Same(itemA.TypeString, itemB.TypeString);

            // Ordinal -> UniqueID lazy formatting and zero-alloc PathID search parity.
            Assert.Equal(" #7", itemA.UniqueID);
            Assert.Equal(" #19", itemB.UniqueID);

            var filterItems = CreateSlimItems(6000, seedOffset: 0);
            var byPathId = AssetListQuery.Filter(filterItems, null, null, "100499", CancellationToken.None);
            Assert.Contains(byPathId, x => x.PathID == 100499L);

            var pathIdTextFilter = AssetListQuery.TextFilter.Create("__nomatch_needle__")!;
            var probeItem = filterItems[100];
            _ = pathIdTextFilter.Matches(probeItem); // Warm up any one-time JIT/static init
            var allocBefore = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 1000; i++)
            {
                Assert.False(pathIdTextFilter.Matches(probeItem));
            }
            var allocAfter = GC.GetAllocatedBytesForCurrentThread();
            Assert.Equal(0L, allocAfter - allocBefore);

            // Sort tie-breaking parity using packed Ordinal.
            var tieLeft = new AssetItem(new AssetHandle { PathID = 777, Name = "Same", Type = ClassIDType.Mesh }, ordinal: 2);
            var tieRight = new AssetItem(new AssetHandle { PathID = 777, Name = "Same", Type = ClassIDType.Mesh }, ordinal: 10);
            Assert.True(AssetListQuery.Compare(tieRight, tieLeft, AssetListQuery.SortKey.Name, descending: false) < 0); // " #10" < " #2"
        }

        [Fact]
        public void Benchmarks_VirtualAssetList_And_SlimAssetItem_200kItems()
        {
            const int count = 200_000;
            var items = CreateSlimItems(count, seedOffset: 0);

            // 1) Benchmark 200,000-item rebind/swap: legacy BulkObservableCollection(items) vs VirtualAssetList.ReplaceBacking(items)
            var warmupLegacy = new BulkObservableCollection<AssetItem>(items.Take(64));
            Assert.Equal(64, warmupLegacy.Count);
            var virtualList = new VirtualAssetList();
            virtualList.ReplaceBacking(items.Take(64).ToList());

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var legacyAllocBefore = GC.GetAllocatedBytesForCurrentThread();
            var swLegacy = Stopwatch.StartNew();
            var legacyCollection = new BulkObservableCollection<AssetItem>(items);
            swLegacy.Stop();
            var legacyAllocBytes = GC.GetAllocatedBytesForCurrentThread() - legacyAllocBefore;
            Assert.Equal(count, legacyCollection.Count);

            var emptyList = new List<AssetItem>();
            virtualList.ReplaceBacking(emptyList);

            var virtualAllocBefore = GC.GetAllocatedBytesForCurrentThread();
            var swVirtual = Stopwatch.StartNew();
            virtualList.ReplaceBacking(items);
            swVirtual.Stop();
            var virtualAllocBytes = GC.GetAllocatedBytesForCurrentThread() - virtualAllocBefore;
            Assert.Equal(count, virtualList.Count);
            Assert.True(virtualAllocBytes <= 2048, $"Expected O(1) zero-copy ReplaceBacking allocation <= 2048 B, got {virtualAllocBytes} B");

            // 2) Benchmark 200,000 AssetItem heap footprint (slim vs eager display strings) + provider viewport footprint
            var sharedHandles = new AssetHandle[count];
            for (var i = 0; i < count; i++)
            {
                sharedHandles[i] = new AssetHandle
                {
                    Type = (i % 4) switch
                    {
                        0 => ClassIDType.Texture2D,
                        1 => ClassIDType.Mesh,
                        2 => ClassIDType.Material,
                        _ => ClassIDType.Transform
                    },
                    PathID = 100_000_000_000L + i,
                    ByteSize = 1024 + (i % 8192),
                    Name = $"Asset_{i % 500}",
                    Container = i % 3 == 0 ? "assets/models/hero.fbx/part" : "assets/textures/albedo.png"
                };
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var slimHeapBefore = GC.GetTotalMemory(forceFullCollection: true);
            var slimItems = new AssetItem[count];
            for (var i = 0; i < count; i++)
            {
                slimItems[i] = new AssetItem(sharedHandles[i], i);
                _ = slimItems[i].TypeString;
                _ = slimItems[i].DisplayType;
            }
            var slimHeapAfter = GC.GetTotalMemory(forceFullCollection: true);
            GC.KeepAlive(slimItems);
            var slimTotalBytes = slimHeapAfter - slimHeapBefore;

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var eagerHeapBefore = GC.GetTotalMemory(forceFullCollection: true);
            var eagerItems = new LegacyEagerStringsStub[count];
            for (var i = 0; i < count; i++)
            {
                var h = sharedHandles[i];
                var typeStr = h.Type.ToString();
                var pathIdStr = h.PathID.ToString(CultureInfo.InvariantCulture);
                var uniqueIdStr = " #" + i.ToString(CultureInfo.InvariantCulture);
                var displayStr = h.Type switch
                {
                    ClassIDType.Mesh => "Mesh (Geometry) (FBX sub-asset)",
                    ClassIDType.Transform => $"{typeStr} (Component)",
                    _ => typeStr
                };
                eagerItems[i] = new LegacyEagerStringsStub(h, typeStr, pathIdStr, uniqueIdStr, displayStr);
            }
            var eagerHeapAfter = GC.GetTotalMemory(forceFullCollection: true);
            GC.KeepAlive(eagerItems);
            var eagerTotalBytes = eagerHeapAfter - eagerHeapBefore;

            // Measure VirtualAssetList flyweight provider viewport footprint (200k virtual rows, 512 LRU cache)
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var flyweightHeapBefore = GC.GetTotalMemory(forceFullCollection: true);
            var flyweightList = new VirtualAssetList();
            flyweightList.ReplaceWithProvider(count, idx => new AssetItem(sharedHandles[idx], idx), lruCapacity: 512);
            for (var row = 0; row < 200; row++)
            {
                _ = flyweightList[10_000 + row];
            }
            var flyweightHeapAfter = GC.GetTotalMemory(forceFullCollection: true);
            GC.KeepAlive(flyweightList);
            var flyweightBytes = flyweightHeapAfter - flyweightHeapBefore;

            _output.WriteLine(
                $"[Rebind 200K] Legacy BulkObservableCollection: {swLegacy.Elapsed.TotalMilliseconds:F3} ms ({legacyAllocBytes:N0} B) | " +
                $"VirtualAssetList.ReplaceBacking: {swVirtual.Elapsed.TotalMilliseconds:F4} ms ({virtualAllocBytes:N0} B)");
            _output.WriteLine(
                $"[Heap 200K AssetItem] Legacy eager strings+fields: {eagerTotalBytes / (1024.0 * 1024.0):F2} MB ({eagerTotalBytes:N0} B) | " +
                $"Slim AssetItem: {slimTotalBytes / (1024.0 * 1024.0):F2} MB ({slimTotalBytes:N0} B) | " +
                $"Saved: {(eagerTotalBytes - slimTotalBytes) / (1024.0 * 1024.0):F2} MB ({(1.0 - (double)slimTotalBytes / eagerTotalBytes) * 100.0:F1}%)");
            _output.WriteLine(
                $"[Virtual Viewport 200K rows / 512 LRU] Retained heap for 200 scrolled rows: {flyweightBytes:N0} B (Cached={flyweightList.CachedItemCount})");
        }

        [Fact]
        public void ProjectIndexArena_And_VirtualAssetList_FlyweightProviderBridge_100kRecordsParityAndMinimalHeap()
        {
            const int recordCount = 100_000;
            const int bundleCount = 200;
            const int lruCapacity = 512;

            var projectIndex = new ProjectIndex();
            var origins = new AssetHandleOrigin[bundleCount];
            for (var b = 0; b < bundleCount; b++)
            {
                var fileName = $"CAB-{b:x8}aabbccddeeff00112233";
                var originalPath = $@"C:\Games\MegaProject\StreamingAssets\Bundles\chunk_{b:D4}.bundle";
                origins[b] = AssetHandleOrigin.Intern(fileName, originalPath, unityVersion: "2022.3.20f1");
            }

            for (var i = 0; i < recordCount; i++)
            {
                var origin = origins[i % bundleCount];
                var pathId = 500_000L + i;
                var type = (i % 5) switch
                {
                    0 => ClassIDType.Texture2D,
                    1 => ClassIDType.Mesh,
                    2 => ClassIDType.Material,
                    3 => ClassIDType.Transform,
                    _ => ClassIDType.GameObject
                };
                var container = (i % 3 == 0)
                    ? $"assets/art/characters/group_{i % 40}/hero_{i % 200}.fbx/submesh"
                    : $"assets/art/environments/zone_{i % 25}/prop_{i % 500}.prefab";

                var handle = AssetHandle.CreateCanonical(
                    origin,
                    pathId,
                    name: $"ArenaAsset_{i % 1024:D4}",
                    type: type,
                    container: container,
                    byteStart: i * 256L,
                    byteSize: 256L + (i & 1023));

                // Verify zero-alloc key before and after adding to ProjectIndex.
                Assert.True(handle.TryGetKey(out var keyBeforeAdd));
                projectIndex.AddHandle(handle);
                Assert.Equal(i, handle.ArenaIndex);
                Assert.True(handle.TryGetKey(out var keyAfterAdd));
                Assert.Equal(keyBeforeAdd, keyAfterAdd);
            }

            var arena = projectIndex.Arena;
            Assert.Equal(recordCount, arena.Count);
            Assert.Equal(bundleCount, arena.OriginCount);

            // Measure retained heap of VirtualAssetList bound to 100K-record AssetIndexArena via ReplaceWithProvider.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var heapBefore = GC.GetTotalMemory(forceFullCollection: true);

            var virtualList = new VirtualAssetList();
            virtualList.ReplaceWithProvider(
                arena.Count,
                i => new AssetItem(arena.MaterializeHandle(i), i),
                lruCapacity);

            Assert.Equal(recordCount, virtualList.Count);
            Assert.Equal(0, virtualList.CachedItemCount);

            // Simulate scrolling across multiple viewports (start, middle, end) over 100K arena rows.
            int[] sampleOffsets = { 0, 12_345, 49_900, 77_700, 99_750 };
            foreach (var offset in sampleOffsets)
            {
                for (var r = 0; r < 120; r++)
                {
                    var idx = offset + r;
                    var item = virtualList[idx];
                    var expectedOrigin = origins[idx % bundleCount];
                    var expectedPathId = 500_000L + idx;
                    var expectedType = (idx % 5) switch
                    {
                        0 => ClassIDType.Texture2D,
                        1 => ClassIDType.Mesh,
                        2 => ClassIDType.Material,
                        3 => ClassIDType.Transform,
                        _ => ClassIDType.GameObject
                    };
                    var expectedContainer = (idx % 3 == 0)
                        ? $"assets/art/characters/group_{idx % 40}/hero_{idx % 200}.fbx/submesh"
                        : $"assets/art/environments/zone_{idx % 25}/prop_{idx % 500}.prefab";

                    Assert.Equal($"ArenaAsset_{idx % 1024:D4}", item.Name);
                    Assert.Equal(expectedType, item.Type);
                    Assert.Equal(expectedContainer, item.Container);
                    Assert.Equal(expectedPathId, item.PathID);
                    Assert.Equal(expectedPathId.ToString(CultureInfo.InvariantCulture), item.PathIDString);
                    Assert.Equal(256L + (idx & 1023), item.FullSize);
                    Assert.Equal(" #" + idx.ToString(CultureInfo.InvariantCulture), item.UniqueID);
                    Assert.NotNull(item.Handle);
                    Assert.Equal(idx, item.Handle!.ArenaIndex);
                    Assert.Equal(expectedOrigin.SerializedFileName, item.Handle.SerializedFileName);
                    Assert.Equal(expectedOrigin.OriginalPath, item.Handle.OriginalPath);
                    Assert.Same(expectedOrigin, item.Handle.Origin);
                    Assert.True(item.Handle.TryGetKey(out var handleKey));
                    Assert.True(projectIndex.TryGetIndex(handleKey, out var lookedUpIdx));
                    Assert.Equal(idx, lookedUpIdx);
                    Assert.True(virtualList.CachedItemCount <= lruCapacity);
                }
            }

            var heapAfter = GC.GetTotalMemory(forceFullCollection: true);
            GC.KeepAlive(virtualList);
            var retainedViewportBytes = Math.Max(0L, heapAfter - heapBefore);

            _output.WriteLine(
                $"[Arena + VirtualAssetList 100K Bridge] Arena={arena.EstimateMemoryBytes() / (1024.0 * 1024.0):F2} MB | " +
                $"Viewport LRU Retained={retainedViewportBytes / 1024.0:F1} KB ({retainedViewportBytes:N0} B, Cached={virtualList.CachedItemCount}/{lruCapacity})");

            // Retained viewport heap for 512 LRU items must stay well under 512 KB (versus >20 MB if all 100K items were materialized).
            Assert.True(
                retainedViewportBytes < 512 * 1024,
                $"Expected VirtualAssetList flyweight viewport over 100K arena records to retain < 512 KB, got {retainedViewportBytes:N0} B.");

            // Also verify ReplaceWithArena resolves IndexOf in O(1) via ArenaIndex even for evicted rows.
            virtualList.ReplaceWithArena(arena, lruCapacity: 16);
            var firstRow = virtualList[0];
            for (var i = 1; i <= 32; i++)
            {
                _ = virtualList[i];
            }
            Assert.Equal(16, virtualList.CachedItemCount);
            Assert.Equal(0, virtualList.IndexOf(firstRow));
        }

        private sealed class LegacyEagerStringsStub
        {
            public readonly AssetHandle Handle;
            public readonly string Name;
            public readonly string Container;
            public readonly string TypeString;
            public readonly string PathIDString;
            public readonly string UniqueID;
            public readonly string DisplayType;
            public readonly long PathID;
            public readonly long Size;
            public readonly long FullSize;
            public readonly ClassIDType Type;
            public object? Asset;
            public object? SourceFile;
            public object? TreeNode;

            public LegacyEagerStringsStub(AssetHandle handle, string typeStr, string pathIdStr, string uniqueId, string displayStr)
            {
                Asset = null;
                SourceFile = null;
                TreeNode = null;
                Handle = handle;
                Name = handle.Name;
                Container = handle.Container;
                TypeString = typeStr;
                PathIDString = pathIdStr;
                UniqueID = uniqueId;
                DisplayType = displayStr;
                PathID = handle.PathID;
                Size = handle.ByteSize;
                FullSize = handle.ByteSize;
                Type = handle.Type;
            }
        }

        private static List<AssetItem> CreateSlimItems(int count, int seedOffset)
        {
            var types = new[]
            {
                ClassIDType.Texture2D,
                ClassIDType.Mesh,
                ClassIDType.Material,
                ClassIDType.Transform,
                ClassIDType.PrefabInstance,
                ClassIDType.GameObject,
                ClassIDType.MonoBehaviour,
                ClassIDType.AudioClip,
            };

            var list = new List<AssetItem>(count);
            for (var i = 0; i < count; i++)
            {
                var id = seedOffset + i;
                var type = types[i % types.Length];
                var container = (i % 4) switch
                {
                    0 => string.Empty,
                    1 => $"assets/models/pack_{i % 19}/hero.FBX/node_{i % 5}",
                    2 => $"assets/textures/tex_{i}.png",
                    _ => $"assets/prefabs/item_{i}.prefab",
                };

                var handle = new AssetHandle
                {
                    UniqueID = $"file#{id}",
                    Name = $"Asset_{id}",
                    Type = type,
                    Container = container,
                    PathID = 100_000L + id,
                    ByteSize = 1024 + (i % 4096),
                };

                list.Add(new AssetItem(handle, id));
            }

            return list;
        }
    }
}
