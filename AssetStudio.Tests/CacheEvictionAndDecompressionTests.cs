using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AssetStudio.Avalonia;
using AssetStudio.Avalonia.Services;
using K4os.Compression.LZ4;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AssetStudio.Tests
{
    public class CacheEvictionAndDecompressionTests
    {
        private readonly record struct SyntheticBundleNode(string Path, byte[] Payload, uint Flags = 4);

        [Fact]
        public void PreviewCache_SaveExceedingMaxBytes_EvictsOldestEntriesAndDeletesPayloadFiles()
        {
            var tempRoot = CreateTempDirectory("preview_lru_bytes");
            try
            {
                var (cache, projectDir, signature, payloadDir) = SetupPreviewCacheProject(tempRoot);
                cache.PreviewCacheMaxTotalBytes = 1000;
                cache.PreviewCacheMaxEntryCount = 100;

                // 3 entries of 400 bytes each => 1200 > 1000 budget => 85% target = 850 bytes.
                // Oldest entry ("a") must be evicted so remaining = 800 <= 850.
                var fileA = WritePayloadFile(payloadDir, "a.bin", 400, seed: 1);
                var fileB = WritePayloadFile(payloadDir, "b.bin", 400, seed: 2);
                var fileC = WritePayloadFile(payloadDir, "c.bin", 400, seed: 3);

                // Pass intentionally stale caller byteSize (10) to verify exact disk length (400) is recorded.
                cache.SavePreviewCacheEntry(projectDir, signature, "asset_a", "texture-thumbnail-png", 2, "512", "hash_a", fileA, 10);
                cache.SavePreviewCacheEntry(projectDir, signature, "asset_b", "texture-thumbnail-png", 2, "512", "hash_b", fileB, 10);
                cache.SavePreviewCacheEntry(projectDir, signature, "asset_c", "texture-thumbnail-png", 2, "512", "hash_c", fileC, 10);

                var stats = cache.GetPreviewCacheStats(projectDir);
                Assert.Equal(2, stats.EntryCount);
                Assert.Equal(800L, stats.TotalBytes);
                Assert.False(string.IsNullOrWhiteSpace(stats.OldestLastAccessedUtc));
                Assert.False(string.IsNullOrWhiteSpace(stats.NewestLastAccessedUtc));

                var globalStats = cache.GetPreviewCacheStats();
                Assert.Equal(2, globalStats.EntryCount);
                Assert.Equal(800L, globalStats.TotalBytes);

                Assert.Null(cache.LoadPreviewCacheEntry(projectDir, signature, "asset_a", "texture-thumbnail-png", 2, "512"));
                Assert.False(File.Exists(fileA));

                var loadedB = cache.LoadPreviewCacheEntry(projectDir, signature, "asset_b", "texture-thumbnail-png", 2, "512");
                var loadedC = cache.LoadPreviewCacheEntry(projectDir, signature, "asset_c", "texture-thumbnail-png", 2, "512");
                Assert.NotNull(loadedB);
                Assert.NotNull(loadedC);
                Assert.Equal(400L, loadedB!.ByteSize);
                Assert.Equal(400L, loadedC!.ByteSize);
                Assert.True(File.Exists(fileB));
                Assert.True(File.Exists(fileC));
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void PreviewCache_TouchingOlderEntryUpdatesLastAccessedUtc_SoUntouchedNewerEntryIsEvicted()
        {
            var tempRoot = CreateTempDirectory("preview_lru_touch");
            try
            {
                var (cache, projectDir, signature, payloadDir) = SetupPreviewCacheProject(tempRoot);
                cache.PreviewCacheMaxTotalBytes = 10_000_000;
                cache.PreviewCacheMaxEntryCount = 1000;

                var fileOld = WritePayloadFile(payloadDir, "old.bin", 300, seed: 10);
                var fileMid = WritePayloadFile(payloadDir, "mid.bin", 300, seed: 20);
                var fileNew = WritePayloadFile(payloadDir, "new.bin", 300, seed: 30);

                cache.SavePreviewCacheEntry(projectDir, signature, "asset_old", "mesh-geometry", 1, "d100", "h1", fileOld, 300);
                cache.SavePreviewCacheEntry(projectDir, signature, "asset_mid", "mesh-geometry", 1, "d100", "h2", fileMid, 300);
                cache.SavePreviewCacheEntry(projectDir, signature, "asset_new", "mesh-geometry", 1, "d100", "h3", fileNew, 300);

                // Touch the oldest entry via read so its LastAccessedUtc becomes newest.
                Assert.True(cache.TryLoadPreviewCacheEntry(projectDir, signature, "asset_old", "mesh-geometry", 1, "d100", out var touched));
                Assert.NotNull(touched);

                // Evict down to budget 700 bytes (85% target = 595 -> evicts 2? Wait: 900 - 300 = 600 > 595, so use 750 => target 637 => evicts 1: asset_mid!)
                var eviction = cache.EvictPreviewCache(projectDir, maxTotalBytes: 750, maxEntryCount: 100);
                Assert.Equal(1, eviction.EvictedEntryCount);
                Assert.Equal(300L, eviction.EvictedBytes);
                Assert.Equal(1, eviction.DeletedFileCount);
                Assert.Equal(0, eviction.FailedFileDeleteCount);
                Assert.Equal(2, eviction.RemainingEntryCount);
                Assert.Equal(600L, eviction.RemainingBytes);

                // Untouched "asset_mid" was oldest after touching "asset_old", so "asset_mid" is evicted while "asset_old" and "asset_new" survive.
                Assert.Null(cache.LoadPreviewCacheEntry(projectDir, signature, "asset_mid", "mesh-geometry", 1, "d100"));
                Assert.False(File.Exists(fileMid));

                Assert.NotNull(cache.LoadPreviewCacheEntry(projectDir, signature, "asset_old", "mesh-geometry", 1, "d100"));
                Assert.NotNull(cache.LoadPreviewCacheEntry(projectDir, signature, "asset_new", "mesh-geometry", 1, "d100"));
                Assert.True(File.Exists(fileOld));
                Assert.True(File.Exists(fileNew));
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void PreviewCache_EntryCountCap_EvictsOldestEntriesDownToHighWaterTarget()
        {
            var tempRoot = CreateTempDirectory("preview_lru_count");
            try
            {
                var (cache, projectDir, signature, payloadDir) = SetupPreviewCacheProject(tempRoot);
                cache.PreviewCacheMaxTotalBytes = 10_000_000;
                cache.PreviewCacheMaxEntryCount = 1000;

                var files = new List<string>();
                for (int i = 0; i < 10; i++)
                {
                    var path = WritePayloadFile(payloadDir, $"item_{i}.bin", 64, seed: i + 1);
                    files.Add(path);
                    cache.SavePreviewCacheEntry(projectDir, signature, $"asset_{i}", "texture-thumbnail-png", 2, "256", $"hash_{i}", path, 64);
                }

                // Cap at maxEntryCount = 6 -> 85% high-water target = floor(5.1) = 5 -> 5 oldest entries evicted.
                var result = cache.EvictPreviewCache(maxTotalBytes: 10_000_000, maxEntryCount: 6);
                Assert.Equal(5, result.EvictedEntryCount);
                Assert.Equal(5, result.DeletedFileCount);
                Assert.Equal(5, result.RemainingEntryCount);
                Assert.Equal(5 * 64L, result.RemainingBytes);

                for (int i = 0; i < 5; i++)
                {
                    Assert.Null(cache.LoadPreviewCacheEntry(projectDir, signature, $"asset_{i}", "texture-thumbnail-png", 2, "256"));
                    Assert.False(File.Exists(files[i]));
                }

                for (int i = 5; i < 10; i++)
                {
                    Assert.NotNull(cache.LoadPreviewCacheEntry(projectDir, signature, $"asset_{i}", "texture-thumbnail-png", 2, "256"));
                    Assert.True(File.Exists(files[i]));
                }
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void PreviewCache_Eviction_ToleratesMissingLockedAndSharedPayloadFilesWithoutThrowing()
        {
            var tempRoot = CreateTempDirectory("preview_lru_resilience");
            try
            {
                var (cache, projectDir, signature, payloadDir) = SetupPreviewCacheProject(tempRoot);
                cache.PreviewCacheMaxTotalBytes = 10_000_000;
                cache.PreviewCacheMaxEntryCount = 1000;

                var missingPath = Path.Combine(payloadDir, "already_missing.bin");
                var lockedPath = WritePayloadFile(payloadDir, "locked.bin", 200, seed: 4);
                var sharedPath = WritePayloadFile(payloadDir, "shared.bin", 200, seed: 5);
                var survivorPath = WritePayloadFile(payloadDir, "survivor.bin", 200, seed: 6);

                cache.SavePreviewCacheEntry(projectDir, signature, "asset_missing", "png", 1, "p", "hm", missingPath, 200);
                cache.SavePreviewCacheEntry(projectDir, signature, "asset_locked", "png", 1, "p", "hl", lockedPath, 200);
                cache.SavePreviewCacheEntry(projectDir, signature, "asset_shared_1", "png", 1, "p", "hs", sharedPath, 200);
                cache.SavePreviewCacheEntry(projectDir, signature, "asset_shared_2", "png", 1, "p", "hs", sharedPath, 200);
                cache.SavePreviewCacheEntry(projectDir, signature, "asset_survivor", "png", 1, "p", "hx", survivorPath, 200);

                // Hold exclusive lock on lockedPath (on Windows this causes File.Delete to throw IOException).
                using (var lockStream = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    // Budget for 2 entries (400 bytes) -> evicts asset_missing, asset_locked, asset_shared_1.
                    var result = cache.EvictPreviewCache(projectDir, maxTotalBytes: 500, maxEntryCount: 3);
                    Assert.Equal(3, result.EvictedEntryCount);
                    Assert.Equal(2, result.RemainingEntryCount);
                    if (OperatingSystem.IsWindows())
                    {
                        Assert.Equal(1, result.FailedFileDeleteCount);
                    }
                }

                // Shared payload file must NOT have been deleted because asset_shared_2 still references it!
                Assert.True(File.Exists(sharedPath));
                Assert.True(File.Exists(survivorPath));
                Assert.NotNull(cache.LoadPreviewCacheEntry(projectDir, signature, "asset_shared_2", "png", 1, "p"));
                Assert.NotNull(cache.LoadPreviewCacheEntry(projectDir, signature, "asset_survivor", "png", 1, "p"));
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void PreviewCache_LegacyV11DatabaseUpgrade_AddsLastAccessedUtcAndIndexNonDestructively()
        {
            var tempRoot = CreateTempDirectory("preview_schema_upgrade");
            try
            {
                var cacheDir = Path.Combine(tempRoot, "IndexCache");
                var projectDir = Path.Combine(tempRoot, "GameProject");
                Directory.CreateDirectory(cacheDir);
                Directory.CreateDirectory(projectDir);
                File.WriteAllBytes(Path.Combine(projectDir, "level0"), new byte[16]);

                var scan = ProjectScanner.ScanFolder(projectDir);
                var bootstrapCache = new SQLiteProjectIndexCache(cacheDir);
                var signature = bootstrapCache.GetFolderSignature(scan);
                var dbPath = bootstrapCache.GetDbPath(projectDir);

                // Build a pre-migration v11 database whose PreviewCacheEntries lacks UpdatedUtc and LastAccessedUtc.
                using (var conn = new SqliteConnection($"Data Source={dbPath}"))
                {
                    conn.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"
                        CREATE TABLE CacheSchema (Key TEXT PRIMARY KEY, Value TEXT NOT NULL);
                        INSERT INTO CacheSchema (Key, Value) VALUES ('SemanticSchemaVersion', '11');
                        CREATE TABLE Projects (
                            Id INTEGER PRIMARY KEY AUTOINCREMENT,
                            FolderPath TEXT NOT NULL,
                            SignatureHash TEXT NOT NULL,
                            TotalFiles INTEGER NOT NULL,
                            TotalBytes INTEGER NOT NULL,
                            UnityBundleCount INTEGER NOT NULL,
                            LastIndexed DATETIME DEFAULT CURRENT_TIMESTAMP,
                            UnityVersion TEXT
                        );
                        INSERT INTO Projects (FolderPath, SignatureHash, TotalFiles, TotalBytes, UnityBundleCount, UnityVersion)
                        VALUES (@folder, @sig, 1, 16, 0, '2022.3.10f1');
                        CREATE TABLE PreviewCacheEntries (
                            Id INTEGER PRIMARY KEY AUTOINCREMENT,
                            ProjectId INTEGER NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
                            AssetUniqueID TEXT NOT NULL,
                            PreviewKind TEXT NOT NULL,
                            AlgorithmVersion INTEGER NOT NULL,
                            Parameters TEXT NOT NULL DEFAULT '',
                            PayloadHash TEXT NOT NULL,
                            PayloadPath TEXT,
                            ByteSize INTEGER NOT NULL DEFAULT 0,
                            CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP,
                            LastAccessed DATETIME DEFAULT CURRENT_TIMESTAMP
                        );
                        INSERT INTO PreviewCacheEntries (ProjectId, AssetUniqueID, PreviewKind, AlgorithmVersion, Parameters, PayloadHash, PayloadPath, ByteSize, CreatedAt, LastAccessed)
                        VALUES (1, 'legacy_asset', 'png', 1, '256', 'legacyhash', '/tmp/legacy.png', 128, '2025-01-01 00:00:00', '2025-01-01 00:00:00');";
                    cmd.Parameters.AddWithValue("@folder", projectDir);
                    cmd.Parameters.AddWithValue("@sig", signature);
                    cmd.ExecuteNonQuery();
                }

                SqliteConnection.ClearAllPools();

                // Fresh SQLiteProjectIndexCache instance must upgrade in place without losing the legacy row.
                var upgradedCache = new SQLiteProjectIndexCache(cacheDir);
                var loaded = upgradedCache.LoadPreviewCacheEntry(projectDir, signature, "legacy_asset", "png", 1, "256");
                Assert.NotNull(loaded);
                Assert.Equal("legacyhash", loaded!.PayloadHash);
                Assert.Equal(128L, loaded.ByteSize);

                using (var conn = new SqliteConnection($"Data Source={dbPath}"))
                {
                    conn.Open();
                    using var idxCmd = conn.CreateCommand();
                    idxCmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'IX_PreviewCacheEntries_LastAccessed';";
                    Assert.Equal(1L, Convert.ToInt64(idxCmd.ExecuteScalar()));

                    using var colCmd = conn.CreateCommand();
                    colCmd.CommandText = "SELECT LastAccessedUtc FROM PreviewCacheEntries WHERE AssetUniqueID = 'legacy_asset';";
                    var lastAccessedUtc = colCmd.ExecuteScalar()?.ToString();
                    Assert.False(string.IsNullOrWhiteSpace(lastAccessedUtc));
                }
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void DecompressedBundleCache_SyntheticLz4UnityFsBundle_FirstLoadMissesSecondLoadHitsWithIdenticalPayloads()
        {
            var tempRoot = CreateTempDirectory("bundle_disk_cache_hit");
            try
            {
                var cacheDir = Path.Combine(tempRoot, "DecompressedCache");
                var gameDir = Path.Combine(tempRoot, "Game");
                Directory.CreateDirectory(gameDir);

                var payloadA = CreateCompressiblePayload(4096, seed: 17);
                var payloadB = CreateCompressiblePayload(6144, seed: 31);
                var payloadC = CreateDeterministicPayload(1024, seed: 59);
                var nodes = new[]
                {
                    new SyntheticBundleNode("CAB-alpha/level0", payloadA),
                    new SyntheticBundleNode("CAB-alpha/shared.resS", payloadB),
                    new SyntheticBundleNode("CAB-alpha/meta.bin", payloadC)
                };

                var bundlePath = Path.Combine(gameDir, "character_hero.bundle");
                var bundleBytes = BuildUnityFsV6Bundle(
                    nodes,
                    blocksInfoCompression: CompressionType.Lz4,
                    blockCompression: CompressionType.Lz4,
                    blocksInfoAtEnd: false,
                    splitBlockSize: 2048);
                File.WriteAllBytes(bundlePath, bundleBytes);

                var cache = new DecompressedBundleCache(cacheDir, maxTotalBytes: 64L * 1024 * 1024, gameScopePath: gameDir);

                // 1st load: cache miss, writes blocks.bin + manifest.json + .ready atomically.
                using (var reader1 = new FileReader(bundlePath))
                using (var bundle1 = new BundleFile(reader1, cache))
                {
                    Assert.False(bundle1.LoadedFromDecompressionCache);
                    Assert.Null(bundle1.BlocksStream);
                    Assert.False(string.IsNullOrWhiteSpace(bundle1.CachedBlocksPath));
                    Assert.True(File.Exists(bundle1.CachedBlocksPath));
                    Assert.Equal(1L, cache.CacheMisses);
                    Assert.Equal(0L, cache.CacheHits);

                    AssertStreamFileSubStream(bundle1.fileList[0], "CAB-alpha/level0", "level0", payloadA);
                    AssertStreamFileSubStream(bundle1.fileList[1], "CAB-alpha/shared.resS", "shared.resS", payloadB);
                    AssertStreamFileSubStream(bundle1.fileList[2], "CAB-alpha/meta.bin", "meta.bin", payloadC);
                    DisposeStreamFiles(bundle1.fileList);
                }

                // 2nd load: instant cache hit, zero re-decompression, bit-identical payloads.
                using (var reader2 = new FileReader(bundlePath))
                using (var bundle2 = new BundleFile(reader2, cache))
                {
                    Assert.True(bundle2.LoadedFromDecompressionCache);
                    Assert.Null(bundle2.BlocksStream);
                    Assert.Equal(1L, cache.CacheMisses);
                    Assert.Equal(1L, cache.CacheHits);

                    AssertStreamFileSubStream(bundle2.fileList[0], "CAB-alpha/level0", "level0", payloadA);
                    AssertStreamFileSubStream(bundle2.fileList[1], "CAB-alpha/shared.resS", "shared.resS", payloadB);
                    AssertStreamFileSubStream(bundle2.fileList[2], "CAB-alpha/meta.bin", "meta.bin", payloadC);
                    DisposeStreamFiles(bundle2.fileList);
                }

                // Direct cached SubStream reader helper also hits cache without parsing the bundle.
                Assert.True(cache.TryOpenCachedStreamFiles(bundlePath, out var directFiles, gameDir));
                Assert.Equal(2L, cache.CacheHits);
                Assert.Equal(3, directFiles.Length);
                AssertStreamFileSubStream(directFiles[0], "CAB-alpha/level0", "level0", payloadA);
                AssertStreamFileSubStream(directFiles[1], "CAB-alpha/shared.resS", "shared.resS", payloadB);
                AssertStreamFileSubStream(directFiles[2], "CAB-alpha/meta.bin", "meta.bin", payloadC);
                DisposeStreamFiles(directFiles);

                // AssetsManager.DecompressionCache defaults to null and integrates cleanly when set.
                using (var defaultManager = new AssetsManager())
                {
                    Assert.Null(defaultManager.DecompressionCache);
                }

                using (var cachedManager = new AssetsManager { DecompressionCache = cache })
                {
                    cachedManager.LoadFiles(bundlePath);
                    Assert.Equal(1L, cache.CacheMisses);
                    Assert.Equal(3L, cache.CacheHits);
                }

                // Zero leftover .tmp files anywhere in cache directory.
                Assert.Empty(Directory.GetFiles(cacheDir, "*.tmp", SearchOption.AllDirectories));
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void DecompressedBundleCache_SourceLastWriteTimeOrLengthChange_InvalidatesAndReExtracts()
        {
            var tempRoot = CreateTempDirectory("bundle_disk_cache_invalidate");
            try
            {
                var cacheDir = Path.Combine(tempRoot, "DecompressedCache");
                var gameDir = Path.Combine(tempRoot, "Game");
                Directory.CreateDirectory(gameDir);

                var bundlePath = Path.Combine(gameDir, "patchable.bundle");
                var payloadV1 = CreateCompressiblePayload(2048, seed: 1);
                var payloadV2 = CreateCompressiblePayload(3072, seed: 2);

                File.WriteAllBytes(bundlePath, BuildUnityFsV6Bundle(
                    new[] { new SyntheticBundleNode("CAB-v/data.bin", payloadV1) },
                    CompressionType.Lz4,
                    CompressionType.Lz4,
                    blocksInfoAtEnd: false,
                    splitBlockSize: 1024));

                var cache = new DecompressedBundleCache(cacheDir, gameScopePath: gameDir);

                // Load 1 -> miss
                using (var r1 = new FileReader(bundlePath))
                using (var b1 = new BundleFile(r1, cache))
                {
                    Assert.False(b1.LoadedFromDecompressionCache);
                    AssertStreamFileSubStream(b1.fileList[0], "CAB-v/data.bin", "data.bin", payloadV1);
                    DisposeStreamFiles(b1.fileList);
                }
                Assert.Equal(1L, cache.CacheMisses);
                Assert.Equal(0L, cache.CacheHits);

                // Touch LastWriteTimeUtc -> must invalidate cache and re-extract (miss #2)
                var originalWriteTime = File.GetLastWriteTimeUtc(bundlePath);
                File.SetLastWriteTimeUtc(bundlePath, originalWriteTime.AddMinutes(10));

                using (var r2 = new FileReader(bundlePath))
                using (var b2 = new BundleFile(r2, cache))
                {
                    Assert.False(b2.LoadedFromDecompressionCache);
                    AssertStreamFileSubStream(b2.fileList[0], "CAB-v/data.bin", "data.bin", payloadV1);
                    DisposeStreamFiles(b2.fileList);
                }
                Assert.Equal(2L, cache.CacheMisses);

                // Replace file with different length (and explicitly preserve LastWriteTimeUtc) -> must invalidate (miss #3)
                var preservedWriteTime = File.GetLastWriteTimeUtc(bundlePath);
                File.WriteAllBytes(bundlePath, BuildUnityFsV6Bundle(
                    new[] { new SyntheticBundleNode("CAB-v/data.bin", payloadV2) },
                    CompressionType.Lz4,
                    CompressionType.Lz4,
                    blocksInfoAtEnd: false,
                    splitBlockSize: 1024));
                File.SetLastWriteTimeUtc(bundlePath, preservedWriteTime);

                using (var r3 = new FileReader(bundlePath))
                using (var b3 = new BundleFile(r3, cache))
                {
                    Assert.False(b3.LoadedFromDecompressionCache);
                    AssertStreamFileSubStream(b3.fileList[0], "CAB-v/data.bin", "data.bin", payloadV2);
                    DisposeStreamFiles(b3.fileList);
                }
                Assert.Equal(3L, cache.CacheMisses);
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void DecompressedBundleCache_DiskLruEviction_RespectsMaxTotalBytesAndPreservesRecentlyAccessedBundles()
        {
            var tempRoot = CreateTempDirectory("bundle_disk_cache_lru");
            try
            {
                var cacheDir = Path.Combine(tempRoot, "DecompressedCache");
                var gameDir = Path.Combine(tempRoot, "Game");
                Directory.CreateDirectory(gameDir);

                // Each bundle decompresses to 8,192 bytes + small manifest/ready (~350 bytes) => ~8.5 KB each.
                // Budget 20,000 bytes fits 2 bundles (~17 KB), forcing eviction when a 3rd bundle is added (~25.5 KB > 20 KB).
                var bundle1Path = WriteSyntheticLz4Bundle(gameDir, "b1.bundle", CreateCompressiblePayload(8192, seed: 11));
                var bundle2Path = WriteSyntheticLz4Bundle(gameDir, "b2.bundle", CreateCompressiblePayload(8192, seed: 22));
                var bundle3Path = WriteSyntheticLz4Bundle(gameDir, "b3.bundle", CreateCompressiblePayload(8192, seed: 33));

                var cache = new DecompressedBundleCache(cacheDir, maxTotalBytes: 20_000, gameScopePath: gameDir);

                string dir1;
                string dir2;
                string dir3;

                using (var r1 = new FileReader(bundle1Path))
                using (var b1 = new BundleFile(r1, cache))
                {
                    dir1 = Path.GetDirectoryName(b1.CachedBlocksPath!)!;
                    DisposeStreamFiles(b1.fileList);
                }

                using (var r2 = new FileReader(bundle2Path))
                using (var b2 = new BundleFile(r2, cache))
                {
                    dir2 = Path.GetDirectoryName(b2.CachedBlocksPath!)!;
                    DisposeStreamFiles(b2.fileList);
                }

                // Touch bundle 1 via cache hit so bundle 2 becomes the least-recently-used bundle!
                using (var r1Hit = new FileReader(bundle1Path))
                using (var b1Hit = new BundleFile(r1Hit, cache))
                {
                    Assert.True(b1Hit.LoadedFromDecompressionCache);
                    DisposeStreamFiles(b1Hit.fileList);
                }

                // Extract bundle 3 -> pushes cache over 20,000 bytes -> bundle 2 must be evicted while bundle 1 & 3 survive!
                using (var r3 = new FileReader(bundle3Path))
                using (var b3 = new BundleFile(r3, cache))
                {
                    dir3 = Path.GetDirectoryName(b3.CachedBlocksPath!)!;
                    DisposeStreamFiles(b3.fileList);
                }

                Assert.False(Directory.Exists(dir2));
                Assert.True(Directory.Exists(dir1));
                Assert.True(Directory.Exists(dir3));
                Assert.True(cache.EvictionCount >= 1);

                var stats = cache.GetStats();
                Assert.Equal(2, stats.EntryCount);
                Assert.True(stats.TotalBytes <= 20_000);
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public async Task DecompressedBundleCache_ConcurrentEightThreadAccess_ProducesZeroRacesAndExactPayloads()
        {
            var tempRoot = CreateTempDirectory("bundle_disk_cache_concurrency");
            try
            {
                var cacheDir = Path.Combine(tempRoot, "DecompressedCache");
                var gameDir = Path.Combine(tempRoot, "Game");
                Directory.CreateDirectory(gameDir);

                var payloadA = CreateCompressiblePayload(16384, seed: 73);
                var payloadB = CreateDeterministicPayload(4096, seed: 91);
                var nodes = new[]
                {
                    new SyntheticBundleNode("CAB-conc/mesh.assets", payloadA),
                    new SyntheticBundleNode("CAB-conc/mesh.resS", payloadB)
                };

                var bundlePath = Path.Combine(gameDir, "concurrent_shared.bundle");
                File.WriteAllBytes(bundlePath, BuildUnityFsV6Bundle(
                    nodes,
                    CompressionType.Lz4HC,
                    CompressionType.Lz4HC,
                    blocksInfoAtEnd: true,
                    splitBlockSize: 4096));

                var cache = new DecompressedBundleCache(cacheDir, maxTotalBytes: 64L * 1024 * 1024, gameScopePath: gameDir);
                const int workerCount = 8;
                using var startBarrier = new Barrier(workerCount);

                var tasks = Enumerable.Range(0, workerCount)
                    .Select(_ => Task.Run(() =>
                    {
                        startBarrier.SignalAndWait();
                        using var reader = new FileReader(bundlePath);
                        using var bundle = new BundleFile(reader, cache);
                        Assert.Equal(2, bundle.fileList.Length);
                        AssertStreamFileSubStream(bundle.fileList[0], "CAB-conc/mesh.assets", "mesh.assets", payloadA);
                        AssertStreamFileSubStream(bundle.fileList[1], "CAB-conc/mesh.resS", "mesh.resS", payloadB);
                        DisposeStreamFiles(bundle.fileList);
                    }))
                    .ToArray();

                await Task.WhenAll(tasks);

                // Single-flight keyed lock guarantees exactly 1 extraction and 7 hits across all 8 parallel workers!
                Assert.Equal(1L, cache.CacheMisses);
                Assert.Equal(workerCount - 1L, cache.CacheHits);
                Assert.Empty(Directory.GetFiles(cacheDir, "*.tmp", SearchOption.AllDirectories));
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void DecompressedBundleCache_TruncatedOrCorruptBundle_CleansUpTmpFilesAndLeavesNoPartialEntry()
        {
            var tempRoot = CreateTempDirectory("bundle_disk_cache_atomic_cleanup");
            try
            {
                var cacheDir = Path.Combine(tempRoot, "DecompressedCache");
                var gameDir = Path.Combine(tempRoot, "Game");
                Directory.CreateDirectory(gameDir);

                var payload = CreateCompressiblePayload(4096, seed: 42);
                var validBundle = BuildUnityFsV6Bundle(
                    new[] { new SyntheticBundleNode("CAB-err/data.bin", payload) },
                    CompressionType.Lz4,
                    CompressionType.Lz4,
                    blocksInfoAtEnd: false,
                    splitBlockSize: 2048);

                // 1) Truncated bundle -> EndOfStreamException, zero .tmp files, zero committed cache entries
                var truncatedPath = Path.Combine(gameDir, "truncated.bundle");
                File.WriteAllBytes(truncatedPath, validBundle.AsSpan(0, validBundle.Length - 24).ToArray());

                var cache = new DecompressedBundleCache(cacheDir, gameScopePath: gameDir);
                using (var reader = new FileReader(truncatedPath))
                {
                    Assert.Throws<EndOfStreamException>(() => new BundleFile(reader, cache));
                }

                Assert.Empty(Directory.GetFiles(cacheDir, "*.tmp", SearchOption.AllDirectories));
                Assert.Empty(Directory.GetFiles(cacheDir, ".ready", SearchOption.AllDirectories));
                Assert.Equal(0, cache.GetStats().EntryCount);

                // 2) Corrupt LZ4 payload -> IOException, zero .tmp files, zero committed cache entries
                var corruptBytes = (byte[])validBundle.Clone();
                for (int i = corruptBytes.Length - 24; i < corruptBytes.Length; i++)
                {
                    corruptBytes[i] = 0xFF;
                }
                var corruptPath = Path.Combine(gameDir, "corrupt.bundle");
                File.WriteAllBytes(corruptPath, corruptBytes);

                using (var reader = new FileReader(corruptPath))
                {
                    Assert.Throws<IOException>(() => new BundleFile(reader, cache));
                }

                Assert.Empty(Directory.GetFiles(cacheDir, "*.tmp", SearchOption.AllDirectories));
                Assert.Empty(Directory.GetFiles(cacheDir, ".ready", SearchOption.AllDirectories));
                Assert.Equal(0, cache.GetStats().EntryCount);

                // 3) Cancellation mid-extraction -> OperationCanceledException, zero .tmp leftovers
                var validPath = Path.Combine(gameDir, "cancel_target.bundle");
                File.WriteAllBytes(validPath, validBundle);
                using var cts = new CancellationTokenSource();
                Assert.Throws<OperationCanceledException>(() =>
                    cache.GetOrExtractBlocks(
                        validPath,
                        expectedUncompressedSize: 4096,
                        stream =>
                        {
                            stream.Write(new byte[128], 0, 128);
                            cts.Cancel();
                        },
                        cancellationToken: cts.Token));

                Assert.Empty(Directory.GetFiles(cacheDir, "*.tmp", SearchOption.AllDirectories));
                Assert.Empty(Directory.GetFiles(cacheDir, ".ready", SearchOption.AllDirectories));
                Assert.Equal(0, cache.GetStats().EntryCount);
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        [Theory]
        [InlineData(true, "mesh-geometry", false, "Cached Mesh")]
        [InlineData(true, "mesh-geometry", true, "Cached Mesh • Loading materials...")]
        [InlineData(true, "texture-thumbnail-png", false, "Cached Texture")]
        [InlineData(true, "model-group", true, "Cached Model Group • Loading materials...")]
        [InlineData(true, null, false, "Cached")]
        [InlineData(true, "   ", true, "Cached • Loading materials...")]
        [InlineData(true, "CustomKind", false, "Cached CustomKind")]
        [InlineData(false, "mesh", true, "Mesh • Loading materials...")]
        [InlineData(false, null, true, "Loading materials...")]
        [InlineData(false, "mesh-geometry", false, "")]
        [InlineData(false, null, false, "")]
        public void MainWindow_FormatPreviewCacheBadge_FormatsAllStatesConsistently(
            bool fromCache,
            string? sourceKind,
            bool materialsLoading,
            string expectedText)
        {
            var actual = MainWindow.FormatPreviewCacheBadge(fromCache, sourceKind, materialsLoading);
            Assert.Equal(expectedText, actual);
            Assert.Equal(!string.IsNullOrEmpty(expectedText), MainWindow.ShouldShowPreviewCacheBadge(fromCache, materialsLoading));
        }

        [Fact]
        public void PreviewCache_ReadOnlyDatabase_LoadPreviewCacheEntryToleratesFailedTouchWithoutThrowing()
        {
            var tempRoot = CreateTempDirectory("preview_readonly_db");
            string? dbPath = null;
            try
            {
                var (cache, projectDir, signature, payloadDir) = SetupPreviewCacheProject(tempRoot);
                var fileA = WritePayloadFile(payloadDir, "ro.bin", 128, seed: 9);
                cache.SavePreviewCacheEntry(projectDir, signature, "asset_ro", "png", 1, "128", "hash_ro", fileA, 128);

                dbPath = cache.GetDbPath(projectDir);
                SqliteConnection.ClearAllPools();
                File.SetAttributes(dbPath, File.GetAttributes(dbPath) | FileAttributes.ReadOnly);

                var entry = cache.LoadPreviewCacheEntry(projectDir, signature, "asset_ro", "png", 1, "128");
                Assert.NotNull(entry);
                Assert.Equal("hash_ro", entry!.PayloadHash);
                Assert.Equal(128L, entry.ByteSize);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                if (!string.IsNullOrEmpty(dbPath) && File.Exists(dbPath))
                {
                    try
                    {
                        File.SetAttributes(dbPath, FileAttributes.Normal);
                    }
                    catch
                    {
                    }
                }
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void DecompressedBundleCache_DiskLruEviction_ToleratesLockedFileWithoutThrowing()
        {
            var tempRoot = CreateTempDirectory("bundle_disk_cache_locked_evict");
            try
            {
                var cacheDir = Path.Combine(tempRoot, "DecompressedCache");
                var gameDir = Path.Combine(tempRoot, "Game");
                Directory.CreateDirectory(gameDir);

                var b1Path = WriteSyntheticLz4Bundle(gameDir, "lock1.bundle", CreateCompressiblePayload(8192, seed: 1));
                var b2Path = WriteSyntheticLz4Bundle(gameDir, "lock2.bundle", CreateCompressiblePayload(8192, seed: 2));
                var b3Path = WriteSyntheticLz4Bundle(gameDir, "lock3.bundle", CreateCompressiblePayload(8192, seed: 3));

                var cache = new DecompressedBundleCache(cacheDir, maxTotalBytes: 64L * 1024 * 1024, gameScopePath: gameDir);

                string b1BlocksPath;
                string b2Dir;
                using (var r1 = new FileReader(b1Path))
                using (var b1 = new BundleFile(r1, cache))
                {
                    b1BlocksPath = b1.CachedBlocksPath!;
                    DisposeStreamFiles(b1.fileList);
                }

                using (var r2 = new FileReader(b2Path))
                using (var b2 = new BundleFile(r2, cache))
                {
                    b2Dir = Path.GetDirectoryName(b2.CachedBlocksPath!)!;
                    DisposeStreamFiles(b2.fileList);
                }

                using (var r3 = new FileReader(b3Path))
                using (var b3 = new BundleFile(r3, cache))
                {
                    DisposeStreamFiles(b3.fileList);
                }

                // Lock oldest bundle's blocks.bin exclusively while evicting to a budget that fits 1 bundle.
                using (var locked = new FileStream(b1BlocksPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    var eviction = cache.EvictToBudget(12_000);
                    Assert.True(eviction.EvictedBundleCount >= 1);
                    if (OperatingSystem.IsWindows())
                    {
                        Assert.True(eviction.FailedDeleteCount >= 1);
                    }
                }

                // Bundle 2 (next oldest unlocked entry) must have been evicted cleanly without throwing!
                Assert.False(Directory.Exists(b2Dir));
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void AssetLoadingService_FlushHandleBatches_EmitsBoundedChunksAndFinalRemainder()
        {
            var projectIndex = new ProjectIndex();
            const int totalHandles = 12_345;
            for (int i = 0; i < totalHandles; i++)
            {
                projectIndex.AddHandle(new AssetHandle
                {
                    UniqueID = $"file0#{i}",
                    Name = $"Asset_{i}",
                    Type = ClassIDType.Texture2D,
                    PathID = i
                });
            }

            var buffer = new List<AssetHandle>();
            var emittedBatchSizes = new List<int>();
            var uniqueFlushedIds = new HashSet<string>(StringComparer.Ordinal);

            var flushedBeforeEnd = AssetLoadingService.FlushHandleBatches(
                projectIndex,
                buffer,
                AssetLoadingService.DefaultHandleFlushBatchSize,
                batch =>
                {
                    emittedBatchSizes.Add(batch.Count);
                    foreach (var handle in batch)
                    {
                        uniqueFlushedIds.Add(handle.UniqueID);
                    }
                },
                flushRemainder: false);

            Assert.Equal(10_000, flushedBeforeEnd);
            Assert.Equal(new[] { 5000, 5000 }, emittedBatchSizes);
            Assert.Equal(2345, buffer.Count);

            var flushedAtEnd = AssetLoadingService.FlushHandleBatches(
                projectIndex,
                buffer,
                AssetLoadingService.DefaultHandleFlushBatchSize,
                batch =>
                {
                    emittedBatchSizes.Add(batch.Count);
                    foreach (var handle in batch)
                    {
                        uniqueFlushedIds.Add(handle.UniqueID);
                    }
                },
                flushRemainder: true);

            Assert.Equal(2345, flushedAtEnd);
            Assert.Equal(new[] { 5000, 5000, 2345 }, emittedBatchSizes);
            Assert.Empty(buffer);
            Assert.Equal(totalHandles, uniqueFlushedIds.Count);
        }

        private static (SQLiteProjectIndexCache Cache, string ProjectDir, string Signature, string PayloadDir) SetupPreviewCacheProject(string tempRoot)
        {
            var cacheDir = Path.Combine(tempRoot, "IndexCache");
            var projectDir = Path.Combine(tempRoot, "SampleGame");
            var payloadDir = Path.Combine(tempRoot, "PreviewPayloads");
            Directory.CreateDirectory(projectDir);
            Directory.CreateDirectory(payloadDir);
            File.WriteAllBytes(Path.Combine(projectDir, "level0"), new byte[] { 1, 2, 3, 4 });

            var scanResult = ProjectScanner.ScanFolder(projectDir);
            var cache = new SQLiteProjectIndexCache(cacheDir);
            var signature = cache.GetFolderSignature(scanResult);

            var saved = cache.SaveIndexCache(
                projectDir,
                signature,
                scanResult,
                "2022.3.10f1",
                new[]
                {
                    new AssetHandle
                    {
                        UniqueID = "level0#1",
                        Name = "Dummy",
                        Type = ClassIDType.Texture2D,
                        OriginalPath = Path.Combine(projectDir, "level0"),
                        SerializedFileName = "level0",
                        PathID = 1,
                        ByteStart = 0,
                        ByteSize = 4
                    }
                });
            Assert.True(saved);

            return (cache, projectDir, signature, payloadDir);
        }

        private static string WritePayloadFile(string payloadDir, string fileName, int byteCount, int seed)
        {
            var path = Path.Combine(payloadDir, fileName);
            File.WriteAllBytes(path, CreateDeterministicPayload(byteCount, seed));
            return path;
        }

        private static string WriteSyntheticLz4Bundle(string dir, string fileName, byte[] payload)
        {
            var path = Path.Combine(dir, fileName);
            var bytes = BuildUnityFsV6Bundle(
                new[] { new SyntheticBundleNode($"CAB-{fileName}/payload.bin", payload) },
                CompressionType.Lz4,
                CompressionType.Lz4,
                blocksInfoAtEnd: false,
                splitBlockSize: 4096);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        private static void AssertStreamFileSubStream(StreamFile file, string expectedPath, string expectedFileName, byte[] expectedPayload)
        {
            Assert.Equal(expectedPath, file.path);
            Assert.Equal(expectedFileName, file.fileName);
            Assert.IsType<SubStream>(file.stream);
            Assert.Equal(expectedPayload.Length, file.stream.Length);

            file.stream.Position = 0;
            var actual = new byte[expectedPayload.Length];
            int total = 0;
            while (total < actual.Length)
            {
                int read = file.stream.Read(actual, total, actual.Length - total);
                Assert.True(read > 0);
                total += read;
            }
            Assert.Equal(expectedPayload, actual);
        }

        private static void DisposeStreamFiles(StreamFile[] files)
        {
            foreach (var file in files)
            {
                file.stream?.Dispose();
            }
        }

        private static byte[] BuildUnityFsV6Bundle(
            SyntheticBundleNode[] nodes,
            CompressionType blocksInfoCompression,
            CompressionType blockCompression,
            bool blocksInfoAtEnd,
            int splitBlockSize)
        {
            var combinedPayload = new MemoryStream();
            var dirEntries = new List<(long offset, long size, uint flags, string path)>(nodes.Length);
            foreach (var node in nodes)
            {
                long offset = combinedPayload.Position;
                combinedPayload.Write(node.Payload, 0, node.Payload.Length);
                dirEntries.Add((offset, node.Payload.Length, node.Flags, node.Path));
            }
            var rawBlocks = combinedPayload.ToArray();

            var blockInfos = new List<(uint uncompressedSize, uint compressedSize, StorageBlockFlags flags)>();
            var compressedBlocksStream = new MemoryStream();
            int cursor = 0;
            while (cursor < rawBlocks.Length || blockInfos.Count == 0)
            {
                int chunkLen = Math.Min(splitBlockSize, rawBlocks.Length - cursor);
                var slice = rawBlocks.AsSpan(cursor, chunkLen).ToArray();
                byte[] stored = CompressBlock(slice, blockCompression);
                compressedBlocksStream.Write(stored, 0, stored.Length);
                blockInfos.Add(((uint)chunkLen, (uint)stored.Length, (StorageBlockFlags)blockCompression));
                cursor += chunkLen;
                if (rawBlocks.Length == 0)
                {
                    break;
                }
            }
            var compressedBlocksData = compressedBlocksStream.ToArray();

            var uncompressedBlocksInfo = BuildBlocksInfoBytes(blockInfos, dirEntries);
            var compressedBlocksInfo = CompressBlock(uncompressedBlocksInfo, blocksInfoCompression);

            ArchiveFlags flags = (ArchiveFlags)blocksInfoCompression | ArchiveFlags.BlocksAndDirectoryInfoCombined;
            if (blocksInfoAtEnd)
            {
                flags |= ArchiveFlags.BlocksInfoAtTheEnd;
            }

            byte[] body = blocksInfoAtEnd
                ? Concat(compressedBlocksData, compressedBlocksInfo)
                : Concat(compressedBlocksInfo, compressedBlocksData);

            var probe = BuildUnityFsV6RawHeader(
                declaredTotalSize: 0,
                compressedBlocksInfoSize: (uint)compressedBlocksInfo.Length,
                uncompressedBlocksInfoSize: (uint)uncompressedBlocksInfo.Length,
                flags: flags,
                trailingBytes: body);

            return BuildUnityFsV6RawHeader(
                declaredTotalSize: probe.Length,
                compressedBlocksInfoSize: (uint)compressedBlocksInfo.Length,
                uncompressedBlocksInfoSize: (uint)uncompressedBlocksInfo.Length,
                flags: flags,
                trailingBytes: body);
        }

        private static byte[] CompressBlock(byte[] input, CompressionType compressionType)
        {
            if (compressionType == CompressionType.None || input.Length == 0)
            {
                return input;
            }

            LZ4Level level = compressionType == CompressionType.Lz4HC
                ? LZ4Level.L09_HC
                : LZ4Level.L00_FAST;
            var dst = new byte[LZ4Codec.MaximumOutputSize(input.Length)];
            int encoded = LZ4Codec.Encode(input, 0, input.Length, dst, 0, dst.Length, level);
            Assert.True(encoded > 0);
            return dst.AsSpan(0, encoded).ToArray();
        }

        private static byte[] BuildBlocksInfoBytes(
            IReadOnlyList<(uint uncompressedSize, uint compressedSize, StorageBlockFlags flags)> blocks,
            IReadOnlyList<(long offset, long size, uint flags, string path)> nodes)
        {
            using var ms = new MemoryStream();
            ms.Write(new byte[16]);
            WriteInt32BE(ms, blocks.Count);
            foreach (var b in blocks)
            {
                WriteUInt32BE(ms, b.uncompressedSize);
                WriteUInt32BE(ms, b.compressedSize);
                WriteUInt16BE(ms, (ushort)b.flags);
            }
            WriteInt32BE(ms, nodes.Count);
            foreach (var n in nodes)
            {
                WriteInt64BE(ms, n.offset);
                WriteInt64BE(ms, n.size);
                WriteUInt32BE(ms, n.flags);
                WriteNullTerminatedUtf8(ms, n.path);
            }
            return ms.ToArray();
        }

        private static byte[] BuildUnityFsV6RawHeader(
            long declaredTotalSize,
            uint compressedBlocksInfoSize,
            uint uncompressedBlocksInfoSize,
            ArchiveFlags flags,
            byte[] trailingBytes)
        {
            using var ms = new MemoryStream();
            WriteNullTerminatedUtf8(ms, "UnityFS");
            WriteUInt32BE(ms, 6u);
            WriteNullTerminatedUtf8(ms, "5.x.x");
            WriteNullTerminatedUtf8(ms, "2021.3.16f1");
            WriteInt64BE(ms, declaredTotalSize);
            WriteUInt32BE(ms, compressedBlocksInfoSize);
            WriteUInt32BE(ms, uncompressedBlocksInfoSize);
            WriteUInt32BE(ms, (uint)flags);
            ms.Write(trailingBytes, 0, trailingBytes.Length);
            return ms.ToArray();
        }

        private static byte[] CreateDeterministicPayload(int length, int seed)
        {
            var data = new byte[length];
            for (int i = 0; i < length; i++)
            {
                data[i] = (byte)((i * 37 + seed * 13 + 7) & 0xFF);
            }
            return data;
        }

        private static byte[] CreateCompressiblePayload(int length, int seed)
        {
            var data = new byte[length];
            for (int i = 0; i < length; i++)
            {
                data[i] = (byte)(((i / 16) * 19 + seed + (i % 4)) & 0xFF);
            }
            return data;
        }

        private static byte[] Concat(byte[] a, byte[] b)
        {
            var combined = new byte[a.Length + b.Length];
            Buffer.BlockCopy(a, 0, combined, 0, a.Length);
            Buffer.BlockCopy(b, 0, combined, a.Length, b.Length);
            return combined;
        }

        private static void WriteNullTerminatedUtf8(Stream stream, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            stream.Write(bytes, 0, bytes.Length);
            stream.WriteByte(0);
        }

        private static void WriteUInt16BE(Stream stream, ushort value)
        {
            Span<byte> buf = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(buf, value);
            stream.Write(buf);
        }

        private static void WriteInt32BE(Stream stream, int value)
        {
            Span<byte> buf = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(buf, value);
            stream.Write(buf);
        }

        private static void WriteUInt32BE(Stream stream, uint value)
        {
            Span<byte> buf = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(buf, value);
            stream.Write(buf);
        }

        private static void WriteInt64BE(Stream stream, long value)
        {
            Span<byte> buf = stackalloc byte[8];
            BinaryPrimitives.WriteInt64BigEndian(buf, value);
            stream.Write(buf);
        }

        private static string CreateTempDirectory(string prefix)
        {
            var path = Path.Combine(Path.GetTempPath(), $"AssetStudioTests_{prefix}_{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return path;
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}
