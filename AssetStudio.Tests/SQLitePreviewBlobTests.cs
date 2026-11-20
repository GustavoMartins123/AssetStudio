using System;
using System.IO;
using AssetStudio.Avalonia;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AssetStudio.Tests
{
    public class SQLitePreviewBlobTests
    {
        [Fact]
        public void SavePreviewCachePayload_SmallPayload_StoresInlineLz4BlobWithZeroDiskFilesAndRoundTripsBitExact()
        {
            var tempRoot = CreateTempDirectory("preview_inline_lz4_blob");
            try
            {
                var (cache, projectDir, signature, payloadDir) = SetupPreviewCacheProject(tempRoot);
                Assert.Equal(128 * 1024, cache.PreviewCacheInlineBlobMaxBytes);

                var rawPayload = CreateCompressiblePayload(32 * 1024, seed: 42);
                var fallbackPath = Path.Combine(payloadDir, "should_not_be_created.meshpreview");

                cache.SavePreviewCachePayload(
                    projectDir,
                    signature,
                    "mesh#100",
                    "mesh-geometry",
                    1,
                    "density=100",
                    rawPayload,
                    fallbackPath);

                // Zero files created on disk in payloadDir
                Assert.False(File.Exists(fallbackPath));
                Assert.Empty(Directory.GetFiles(payloadDir, "*", SearchOption.AllDirectories));

                // Round-trip bit-exact via TryLoadPreviewCacheBytes
                Assert.True(cache.TryLoadPreviewCacheBytes(
                    projectDir,
                    signature,
                    "mesh#100",
                    "mesh-geometry",
                    1,
                    "density=100",
                    out var loadedBytes,
                    out var entry));

                Assert.NotNull(loadedBytes);
                Assert.Equal(rawPayload, loadedBytes!);
                Assert.NotNull(entry);
                Assert.True(entry!.IsInlineBlob);
                Assert.Equal(string.Empty, entry.PayloadPath);
                Assert.NotNull(entry.PayloadBlob);
                Assert.Equal(SQLiteProjectIndexCache.PreviewPayloadCompressionLz4Pickler, entry.PayloadCompression);
                Assert.Equal(rawPayload.LongLength, entry.UncompressedByteSize);
                Assert.True(entry.ByteSize > 0 && entry.ByteSize < rawPayload.LongLength);
                Assert.Equal(entry.PayloadBlob!.LongLength, entry.ByteSize);

                // Direct SQLite verification of columns
                var dbPath = cache.GetDbPath(projectDir);
                using (var conn = new SqliteConnection($"Data Source={dbPath}"))
                {
                    conn.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"
                        SELECT PayloadPath, PayloadBlob, PayloadCompression, ByteSize, UncompressedByteSize
                        FROM PreviewCacheEntries
                        WHERE AssetUniqueID = 'mesh#100'";
                    using var reader = cmd.ExecuteReader();
                    Assert.True(reader.Read());
                    Assert.Equal(string.Empty, reader.GetString(0));
                    Assert.False(reader.IsDBNull(1));
                    var storedBlob = (byte[])reader.GetValue(1);
                    Assert.True(storedBlob.Length < rawPayload.Length);
                    Assert.Equal(1, reader.GetInt32(2));
                    Assert.Equal(storedBlob.LongLength, reader.GetInt64(3));
                    Assert.Equal(rawPayload.LongLength, reader.GetInt64(4));
                }

                var stats = cache.GetPreviewCacheStats(projectDir);
                Assert.Equal(1, stats.EntryCount);
                Assert.Equal(entry.ByteSize, stats.TotalBytes);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void SavePreviewCachePayload_LargePayloadExceeding128KiB_SpillsToExternalFileAtomicallyAndRoundTripsBitExact()
        {
            var tempRoot = CreateTempDirectory("preview_large_disk_spill");
            try
            {
                var (cache, projectDir, signature, payloadDir) = SetupPreviewCacheProject(tempRoot);
                var largePayload = CreateCompressiblePayload(200 * 1024, seed: 99);
                var externalPath = Path.Combine(payloadDir, "large_preview.bin");

                cache.SavePreviewCachePayload(
                    projectDir,
                    signature,
                    "tex#200",
                    "texture-thumbnail-png",
                    2,
                    "max=512",
                    largePayload,
                    externalPath);

                Assert.True(File.Exists(externalPath));
                Assert.Empty(Directory.GetFiles(payloadDir, "*.tmp", SearchOption.AllDirectories));

                Assert.True(cache.TryLoadPreviewCacheBytes(
                    projectDir,
                    signature,
                    "tex#200",
                    "texture-thumbnail-png",
                    2,
                    "max=512",
                    out var loadedBytes,
                    out var entry));

                Assert.NotNull(loadedBytes);
                Assert.Equal(largePayload, loadedBytes!);
                Assert.NotNull(entry);
                Assert.False(entry!.IsInlineBlob);
                Assert.Null(entry.PayloadBlob);
                Assert.Equal(externalPath, entry.PayloadPath);
                Assert.Equal(SQLiteProjectIndexCache.PreviewPayloadCompressionNone, entry.PayloadCompression);
                Assert.Equal(largePayload.LongLength, entry.ByteSize);
                Assert.Equal(largePayload.LongLength, entry.UncompressedByteSize);

                var dbPath = cache.GetDbPath(projectDir);
                using (var conn = new SqliteConnection($"Data Source={dbPath}"))
                {
                    conn.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"
                        SELECT PayloadPath, PayloadBlob, PayloadCompression, ByteSize, UncompressedByteSize
                        FROM PreviewCacheEntries
                        WHERE AssetUniqueID = 'tex#200'";
                    using var reader = cmd.ExecuteReader();
                    Assert.True(reader.Read());
                    Assert.Equal(externalPath, reader.GetString(0));
                    Assert.True(reader.IsDBNull(1));
                    Assert.Equal(0, reader.GetInt32(2));
                    Assert.Equal(largePayload.LongLength, reader.GetInt64(3));
                    Assert.Equal(largePayload.LongLength, reader.GetInt64(4));
                }
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void MixedInlineBlobAndDiskEntries_LruEvictionRespectsTouchAndBudgetsAndDeletesOnlyDiskFiles()
        {
            var tempRoot = CreateTempDirectory("preview_mixed_lru_eviction");
            try
            {
                var (cache, projectDir, signature, payloadDir) = SetupPreviewCacheProject(tempRoot);
                cache.PreviewCacheMaxTotalBytes = 10_000_000;
                cache.PreviewCacheMaxEntryCount = 1000;
                cache.PreviewCacheInlineBlobMaxBytes = 1024; // 1 KiB threshold for fast mixed testing

                // 1) Oldest inline BLOB (<= 1 KiB)
                var inlinePayload1 = CreateCompressiblePayload(512, seed: 1);
                cache.SavePreviewCachePayload(projectDir, signature, "inline_1", "png", 1, "p", inlinePayload1);

                // 2) Old disk entry (> 1 KiB)
                var diskPayload1 = CreateCompressiblePayload(2048, seed: 2);
                var diskPath1 = Path.Combine(payloadDir, "disk_1.bin");
                cache.SavePreviewCachePayload(projectDir, signature, "disk_1", "png", 1, "p", diskPayload1, diskPath1);

                // 3) Second inline BLOB (<= 1 KiB)
                var inlinePayload2 = CreateCompressiblePayload(512, seed: 3);
                cache.SavePreviewCachePayload(projectDir, signature, "inline_2", "png", 1, "p", inlinePayload2);

                // 4) Newest disk entry (> 1 KiB)
                var diskPayload2 = CreateCompressiblePayload(2048, seed: 4);
                var diskPath2 = Path.Combine(payloadDir, "disk_2.bin");
                cache.SavePreviewCachePayload(projectDir, signature, "disk_2", "png", 1, "p", diskPayload2, diskPath2);

                Assert.True(File.Exists(diskPath1));
                Assert.True(File.Exists(diskPath2));

                // Touch "inline_1" via TryLoadPreviewCacheBytes so it becomes the most recently accessed entry!
                Assert.True(cache.TryLoadPreviewCacheBytes(projectDir, signature, "inline_1", "png", 1, "p", out var touchedBytes));
                Assert.Equal(inlinePayload1, touchedBytes);

                // Now LRU order from oldest to newest is: disk_1, inline_2, disk_2, inline_1.
                // Evict with maxEntryCount = 2 -> 85% target = floor(1.7) = 1? Wait: use maxEntryCount = 3 -> floor(2.55) = 2 survivors!
                // With 2 survivors, oldest 2 (disk_1 and inline_2) must be evicted, while disk_2 and inline_1 survive!
                var eviction = cache.EvictPreviewCache(projectDir, maxTotalBytes: 10_000_000, maxEntryCount: 3);
                Assert.Equal(2, eviction.EvictedEntryCount);
                Assert.Equal(1, eviction.DeletedFileCount); // Only disk_1 had a disk file; inline_2 had no disk file
                Assert.Equal(0, eviction.FailedFileDeleteCount);
                Assert.Equal(2, eviction.RemainingEntryCount);

                // Evicted entries: disk_1 and inline_2
                Assert.False(File.Exists(diskPath1));
                Assert.False(cache.TryLoadPreviewCacheBytes(projectDir, signature, "disk_1", "png", 1, "p", out _));
                Assert.False(cache.TryLoadPreviewCacheBytes(projectDir, signature, "inline_2", "png", 1, "p", out _));

                // Surviving entries: disk_2 and touched inline_1
                Assert.True(File.Exists(diskPath2));
                Assert.True(cache.TryLoadPreviewCacheBytes(projectDir, signature, "disk_2", "png", 1, "p", out var loadedDisk2));
                Assert.Equal(diskPayload2, loadedDisk2);
                Assert.True(cache.TryLoadPreviewCacheBytes(projectDir, signature, "inline_1", "png", 1, "p", out var loadedInline1));
                Assert.Equal(inlinePayload1, loadedInline1);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void LegacySavePreviewCacheEntry_AndColumnSelfHealingOnPreBlobTable_RemainFullyCompatible()
        {
            var tempRoot = CreateTempDirectory("preview_legacy_compat");
            try
            {
                var (cache, projectDir, signature, payloadDir) = SetupPreviewCacheProject(tempRoot);
                var legacyBytes = CreateCompressiblePayload(256, seed: 77);
                var legacyPath = Path.Combine(payloadDir, "legacy_disk.png");
                File.WriteAllBytes(legacyPath, legacyBytes);

                cache.SavePreviewCacheEntry(
                    projectDir,
                    signature,
                    "legacy#1",
                    "png",
                    1,
                    "256x256",
                    "legacy_hash_1",
                    legacyPath,
                    legacyBytes.LongLength);

                var loadedEntry = cache.LoadPreviewCacheEntry(projectDir, signature, "legacy#1", "png", 1, "256x256");
                Assert.NotNull(loadedEntry);
                Assert.False(loadedEntry!.IsInlineBlob);
                Assert.Equal(legacyPath, loadedEntry.PayloadPath);
                Assert.Equal(legacyBytes.LongLength, loadedEntry.ByteSize);

                Assert.True(cache.TryLoadPreviewCacheBytes(
                    projectDir,
                    signature,
                    "legacy#1",
                    "png",
                    1,
                    "256x256",
                    out var loadedLegacyBytes));
                Assert.Equal(legacyBytes, loadedLegacyBytes);

                // Incompressible small payload (<= 128 KiB) stores inline raw BLOB with PayloadCompression = 0
                var randomBytes = new byte[1024];
                new Random(12345).NextBytes(randomBytes);
                cache.SavePreviewCachePayload(
                    projectDir,
                    signature,
                    "random#1",
                    "png",
                    1,
                    "1024",
                    randomBytes);

                Assert.True(cache.TryLoadPreviewCacheBytes(
                    projectDir,
                    signature,
                    "random#1",
                    "png",
                    1,
                    "1024",
                    out var loadedRandomBytes,
                    out var randomEntry));
                Assert.Equal(randomBytes, loadedRandomBytes);
                Assert.NotNull(randomEntry);
                Assert.True(randomEntry!.IsInlineBlob);
                Assert.Equal(string.Empty, randomEntry.PayloadPath);
                Assert.Equal(SQLiteProjectIndexCache.PreviewPayloadCompressionNone, randomEntry.PayloadCompression);
                Assert.Equal(randomBytes.LongLength, randomEntry.ByteSize);
                Assert.Equal(randomBytes.LongLength, randomEntry.UncompressedByteSize);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
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

        private static byte[] CreateCompressiblePayload(int length, int seed)
        {
            var data = new byte[length];
            for (int i = 0; i < length; i++)
            {
                // Structured repeating pattern representative of serialized mesh geometry / PNG scanlines
                data[i] = (byte)(((i / 16) + seed) & 0x3F);
            }

            return data;
        }

        private static string CreateTempDirectory(string prefix)
        {
            var path = Path.Combine(Path.GetTempPath(), $"AssetStudio_{prefix}_{Guid.NewGuid():N}");
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
