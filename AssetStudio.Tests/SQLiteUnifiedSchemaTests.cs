using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetStudio.Avalonia;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AssetStudio.Tests
{
    public class SQLiteUnifiedSchemaTests
    {
        [Fact]
        public void FreshDatabase_UsesUnifiedAssetsTableWithAssetHandlesCompatibilityView()
        {
            var tempRoot = CreateTempDirectory("sqlite_unified_schema");
            try
            {
                Assert.Equal(15, SQLiteProjectIndexCache.SemanticSchemaVersion);

                var (cache, projectDir, _, _, _) = CreateSampleProject(tempRoot);
                cache.EnsureInitialized(projectDir);

                using var conn = cache.CreateReadConnection(projectDir);
                Assert.Equal("15", ReadSchemaVersion(conn));
                Assert.Null(GetSchemaObjectType(conn, "AssetHandles", "table"));
                Assert.Equal("view", GetSchemaObjectType(conn, "AssetHandles", "view"));
                Assert.Contains("WITHOUT ROWID", GetTableDdl(conn, "Assets"), StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("idx_handles_lookup", GetUserIndexNames(conn));

                var previewColumns = GetColumnNames(conn, "PreviewCacheEntries");
                Assert.Contains("PayloadBlob", previewColumns);
                Assert.Contains("PayloadCompression", previewColumns);
                Assert.Contains("UncompressedByteSize", previewColumns);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void SaveAndLoadIndexCache_RoundTripsEveryHandleFieldWithSingleAssetsRowPerHandle()
        {
            var tempRoot = CreateTempDirectory("sqlite_unified_roundtrip");
            try
            {
                var (cache, projectDir, signature, scanResult, sourcePath) = CreateSampleProject(tempRoot);
                var secondSourcePath = Path.Combine(projectDir, "sharedassets0.assets");
                File.WriteAllBytes(secondSourcePath, new byte[32]);

                var handles = new List<AssetHandle>();
                for (var i = 0; i < 40; i++)
                {
                    var useSecondSource = i % 3 == 0;
                    var serializedFileName = useSecondSource ? "sharedassets0.assets" : "level0";
                    var originalPath = useSecondSource ? secondSourcePath : sourcePath;
                    var pathId = 1000L + (i * 7919L);
                    handles.Add(new AssetHandle
                    {
                        UniqueID = AssetHandle.BuildUniqueID(serializedFileName, originalPath, pathId),
                        Name = $"Asset_{i:D3}",
                        Type = (i % 4) switch
                        {
                            0 => ClassIDType.Mesh,
                            1 => ClassIDType.Material,
                            2 => ClassIDType.Texture2D,
                            _ => ClassIDType.AnimationClip
                        },
                        Container = i % 5 == 0 ? string.Empty : $"assets/path/asset_{i:D3}.prefab",
                        OriginalPath = originalPath,
                        SerializedFileName = serializedFileName,
                        PathID = i == 7 ? -4242L : pathId,
                        ByteStart = (long)int.MaxValue + (i * 4096L),
                        ByteSize = 512 + i
                    });
                }

                Assert.True(cache.SaveIndexCache(projectDir, signature, scanResult, "2022.3.10f1", handles, preserveSemanticRelations: false));

                var loaded = cache.LoadIndexCache(projectDir, signature);
                Assert.NotNull(loaded);
                Assert.Equal(handles.Count, loaded!.Count);

                var loadedById = loaded.ToDictionary(h => h.UniqueID, StringComparer.Ordinal);
                foreach (var expected in handles)
                {
                    Assert.True(loadedById.TryGetValue(expected.UniqueID, out var actual), $"Missing handle {expected.UniqueID}");
                    Assert.Equal(expected.Name, actual!.Name);
                    Assert.Equal(expected.Type, actual.Type);
                    Assert.Equal(expected.Container, actual.Container);
                    Assert.Equal(expected.OriginalPath, actual.OriginalPath);
                    Assert.Equal(expected.SerializedFileName, actual.SerializedFileName);
                    Assert.Equal(expected.PathID, actual.PathID);
                    Assert.Equal(expected.ByteStart, actual.ByteStart);
                    Assert.Equal(expected.ByteSize, actual.ByteSize);
                }

                // Origins are shared per source file rather than duplicated per handle.
                var levelHandles = loaded.Where(h => h.SerializedFileName == "level0").ToList();
                Assert.True(levelHandles.Count > 1);
                Assert.All(levelHandles, h => Assert.Same(levelHandles[0].OriginalPath, h.OriginalPath));

                using var conn = cache.CreateReadConnection(projectDir);
                Assert.Equal((long)handles.Count, ExecuteScalarInt64(conn, "SELECT COUNT(*) FROM Assets;"));
                Assert.Equal((long)handles.Count, ExecuteScalarInt64(conn, "SELECT COUNT(*) FROM AssetHandles;"));
                Assert.Equal(2L, ExecuteScalarInt64(conn, "SELECT COUNT(*) FROM SourceFiles;"));
                Assert.Equal(0L, ExecuteScalarInt64(conn, "SELECT COUNT(*) FROM Assets WHERE SourceFileId IS NULL;"));

                // The compatibility view exposes the legacy AssetHandles column shape.
                var sample = handles[7];
                using (var viewCmd = conn.CreateCommand())
                {
                    viewCmd.CommandText = @"
                        SELECT UniqueID, Name, Type, Container, OriginalPath, SerializedFileName, PathID, ByteStart, ByteSize
                        FROM AssetHandles
                        WHERE ProjectId = (SELECT Id FROM Projects LIMIT 1)
                          AND UniqueID = @uniqueId";
                    viewCmd.Parameters.AddWithValue("@uniqueId", sample.UniqueID);
                    using var reader = viewCmd.ExecuteReader();
                    Assert.True(reader.Read());
                    Assert.Equal(sample.UniqueID, reader.GetString(0));
                    Assert.Equal(sample.Name, reader.GetString(1));
                    Assert.Equal((int)sample.Type, reader.GetInt32(2));
                    Assert.Equal(sample.Container, reader.GetString(3));
                    Assert.Equal(sample.OriginalPath, reader.GetString(4));
                    Assert.Equal(sample.SerializedFileName, reader.GetString(5));
                    Assert.Equal(sample.PathID, reader.GetInt64(6));
                    Assert.Equal(sample.ByteStart, reader.GetInt64(7));
                    Assert.Equal(sample.ByteSize, reader.GetInt64(8));
                    Assert.False(reader.Read());
                }
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void SaveIndexCache_DuplicateUniqueIdsCollapseToOneRowAndResaveReplacesRows()
        {
            var tempRoot = CreateTempDirectory("sqlite_unified_duplicates");
            try
            {
                var (cache, projectDir, signature, scanResult, sourcePath) = CreateSampleProject(tempRoot);
                var duplicateId = AssetHandle.BuildUniqueID("level0", sourcePath, 77);
                var handles = new List<AssetHandle>
                {
                    CreateHandle(AssetHandle.BuildUniqueID("level0", sourcePath, 76), "First", ClassIDType.Mesh, sourcePath, 76),
                    CreateHandle(duplicateId, "Original", ClassIDType.Material, sourcePath, 77),
                    CreateHandle(duplicateId, "Replacement", ClassIDType.Material, sourcePath, 77)
                };

                Assert.True(cache.SaveIndexCache(projectDir, signature, scanResult, "2022.3.10f1", handles, preserveSemanticRelations: false));
                var loaded = cache.LoadIndexCache(projectDir, signature);
                Assert.NotNull(loaded);
                Assert.Equal(2, loaded!.Count);
                Assert.Equal("Replacement", Assert.Single(loaded, h => h.UniqueID == duplicateId).Name);

                var resaved = new List<AssetHandle>
                {
                    CreateHandle(AssetHandle.BuildUniqueID("level0", sourcePath, 900), "OnlyOne", ClassIDType.Texture2D, sourcePath, 900)
                };
                Assert.True(cache.SaveIndexCache(projectDir, signature, scanResult, "2022.3.10f1", resaved, preserveSemanticRelations: true));
                var reloaded = cache.LoadIndexCache(projectDir, signature);
                Assert.NotNull(reloaded);
                Assert.Equal("OnlyOne", Assert.Single(reloaded!).Name);

                using var conn = cache.CreateReadConnection(projectDir);
                Assert.Equal(1L, ExecuteScalarInt64(conn, "SELECT COUNT(*) FROM Assets;"));
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void ExplainQueryPlan_UnifiedAssetsAndCompatibilityViewQueriesUseSearchWithZeroScans()
        {
            var tempRoot = CreateTempDirectory("sqlite_unified_eqp");
            try
            {
                var (cache, projectDir, signature, scanResult, sourcePath) = CreateSampleProject(tempRoot);
                var handles = Enumerable.Range(0, 200)
                    .Select(i => CreateHandle(AssetHandle.BuildUniqueID("level0", sourcePath, 10 + i), $"Mesh_{i:D3}", ClassIDType.Mesh, sourcePath, 10 + i))
                    .ToList();
                Assert.True(cache.SaveIndexCache(projectDir, signature, scanResult, "2022.3.10f1", handles, preserveSemanticRelations: false));
                var sampleId = handles[100].UniqueID;

                using var conn = cache.CreateReadConnection(projectDir);
                var projectId = ExecuteScalarInt64(conn, "SELECT Id FROM Projects LIMIT 1;");

                void Bind(SqliteCommand cmd)
                {
                    cmd.Parameters.AddWithValue("@projectId", projectId);
                    cmd.Parameters.AddWithValue("@uniqueId", sampleId);
                    cmd.Parameters.AddWithValue("@groupAssetId", sampleId);
                }

                var loadPlan = AssertSearchOnly(conn, "LoadIndexCache.Assets", @"
                    SELECT UniqueID, Name, Type, Container, SourceFileId, PathID, ByteStart, ByteSize
                    FROM Assets
                    WHERE ProjectId = @projectId", Bind);
                Assert.Contains(loadPlan, line => line.Contains("SEARCH Assets USING PRIMARY KEY", StringComparison.OrdinalIgnoreCase));

                AssertSearchOnly(conn, "LoadIndexCache.SourceFiles", @"
                    SELECT Id, SerializedFileName, OriginalPath
                    FROM SourceFiles
                    WHERE ProjectId = @projectId", Bind);

                var pointPlan = AssertSearchOnly(conn, "Assets point lookup", @"
                    SELECT UniqueID, Name, Type, Container, SourceFileId, PathID, ByteStart, ByteSize
                    FROM Assets
                    WHERE ProjectId = @projectId
                      AND UniqueID = @uniqueId", Bind);
                Assert.Contains(pointPlan, line => line.Contains("SEARCH Assets USING PRIMARY KEY (ProjectId=? AND UniqueID=?)", StringComparison.OrdinalIgnoreCase));

                var viewPointPlan = AssertSearchOnly(conn, "AssetHandles view point lookup", @"
                    SELECT UniqueID, Name, Type, Container, OriginalPath, SerializedFileName, PathID, ByteStart, ByteSize
                    FROM AssetHandles
                    WHERE ProjectId = @projectId
                      AND UniqueID = @uniqueId", Bind);
                Assert.Contains(viewPointPlan, line => line.Contains("SEARCH Assets USING PRIMARY KEY (ProjectId=? AND UniqueID=?)", StringComparison.OrdinalIgnoreCase));
                Assert.Contains(viewPointPlan, line => line.StartsWith("SEARCH SourceFiles USING", StringComparison.OrdinalIgnoreCase));

                AssertSearchOnly(conn, "AssetHandles view project load", @"
                    SELECT UniqueID, Name, Type, Container, OriginalPath, SerializedFileName, PathID, ByteStart, ByteSize
                    FROM AssetHandles
                    WHERE ProjectId = @projectId", Bind);

                AssertSearchOnly(conn, "LoadAssetSourceFileIdMap", @"
                    SELECT UniqueID, SourceFileId
                    FROM Assets
                    WHERE ProjectId = @projectId
                      AND SourceFileId IS NOT NULL", Bind);

                AssertSearchOnly(conn, "MeshSource CTE", @"
                    SELECT SourceFileId
                    FROM Assets
                    WHERE ProjectId = @projectId
                      AND UniqueID = @uniqueId
                    LIMIT 1", Bind);

                AssertSearchOnly(conn, "LoadModelGroupMeshes join", @"
                    SELECT mgm.GroupAssetUniqueID, mgm.MeshAssetUniqueID, COALESCE(mesh.Name, '')
                    FROM ModelGroupMeshes mgm
                    LEFT JOIN Assets mesh
                      ON mesh.ProjectId = mgm.ProjectId
                     AND mesh.UniqueID = mgm.MeshAssetUniqueID
                    WHERE mgm.ProjectId = @projectId
                      AND mgm.GroupAssetUniqueID = @groupAssetId", Bind);

                AssertSearchOnly(conn, "ClearIndexTablesForProject.Assets", @"
                    DELETE FROM Assets WHERE ProjectId = @projectId", Bind);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void LegacySchemaV12Database_UpgradesInPlaceToV13PreservingIndexAndPreviewCache()
        {
            var tempRoot = CreateTempDirectory("sqlite_v12_to_v13");
            try
            {
                var (bootstrapCache, projectDir, signature, scanResult, sourcePath) = CreateSampleProject(tempRoot);
                var dbPath = bootstrapCache.GetDbPath(projectDir);
                var meshId = AssetHandle.BuildUniqueID("level0", sourcePath, 100);
                var materialId = AssetHandle.BuildUniqueID("level0", sourcePath, 200);
                CreateLegacyV12Database(dbPath, projectDir, signature, sourcePath, meshId, materialId, orphanAssetSource: false);
                SqliteConnection.ClearAllPools();

                var upgradedCache = new SQLiteProjectIndexCache(Path.GetDirectoryName(dbPath)!);
                var loaded = upgradedCache.LoadIndexCache(projectDir, signature);
                Assert.NotNull(loaded);
                Assert.Equal(2, loaded!.Count);
                var mesh = Assert.Single(loaded, h => h.UniqueID == meshId);
                Assert.Equal("HeroMesh", mesh.Name);
                Assert.Equal(ClassIDType.Mesh, mesh.Type);
                Assert.Equal("assets/hero.fbx", mesh.Container);
                Assert.Equal(sourcePath, mesh.OriginalPath);
                Assert.Equal("level0", mesh.SerializedFileName);
                Assert.Equal(100L, mesh.PathID);
                Assert.Equal(4096L, mesh.ByteStart);
                Assert.Equal(2048L, mesh.ByteSize);

                AssertPreviewPreservedAndSchemaIsV13(upgradedCache, projectDir, signature);

                // Re-saving on the upgraded database keeps working.
                var handles = new List<AssetHandle>
                {
                    CreateHandle(meshId, "HeroMeshV2", ClassIDType.Mesh, sourcePath, 100)
                };
                Assert.True(upgradedCache.SaveIndexCache(projectDir, signature, scanResult, "2022.3.10f1", handles, preserveSemanticRelations: false));
                Assert.Equal("HeroMeshV2", Assert.Single(upgradedCache.LoadIndexCache(projectDir, signature)!).Name);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void LegacySchemaV12Database_WithOrphanAssetRows_FallsBackToReindexButPreservesPreviewCache()
        {
            var tempRoot = CreateTempDirectory("sqlite_v12_orphan");
            try
            {
                var (bootstrapCache, projectDir, signature, _, sourcePath) = CreateSampleProject(tempRoot);
                var dbPath = bootstrapCache.GetDbPath(projectDir);
                CreateLegacyV12Database(
                    dbPath,
                    projectDir,
                    signature,
                    sourcePath,
                    AssetHandle.BuildUniqueID("level0", sourcePath, 100),
                    AssetHandle.BuildUniqueID("level0", sourcePath, 200),
                    orphanAssetSource: true);
                SqliteConnection.ClearAllPools();

                var upgradedCache = new SQLiteProjectIndexCache(Path.GetDirectoryName(dbPath)!);
                Assert.Null(upgradedCache.LoadIndexCache(projectDir, signature));
                AssertPreviewPreservedAndSchemaIsV13(upgradedCache, projectDir, signature);

                using var conn = upgradedCache.CreateReadConnection(projectDir);
                Assert.Equal(0L, ExecuteScalarInt64(conn, "SELECT COUNT(*) FROM Assets;"));
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(tempRoot);
            }
        }

        private static void AssertPreviewPreservedAndSchemaIsV13(SQLiteProjectIndexCache cache, string projectDir, string signature)
        {
            var preservedPreview = cache.LoadPreviewCacheEntry(projectDir, signature, "preserved_preview", "png", 1, "256");
            Assert.NotNull(preservedPreview);
            Assert.Equal("hash123", preservedPreview!.PayloadHash);

            using var conn = cache.CreateReadConnection(projectDir);
            Assert.Equal("15", ReadSchemaVersion(conn));
            Assert.Null(GetSchemaObjectType(conn, "AssetHandles", "table"));
            Assert.Equal("view", GetSchemaObjectType(conn, "AssetHandles", "view"));
            Assert.Contains("WITHOUT ROWID", GetTableDdl(conn, "Assets"), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("idx_handles_lookup", GetUserIndexNames(conn));
            Assert.Equal(1L, ExecuteScalarInt64(conn, "SELECT COUNT(*) FROM PreviewCacheEntries;"));
            Assert.Equal(512L, ExecuteScalarInt64(conn, "SELECT ByteSize FROM PreviewCacheEntries LIMIT 1;"));

            var previewColumns = GetColumnNames(conn, "PreviewCacheEntries");
            Assert.Contains("PayloadBlob", previewColumns);
            Assert.Contains("PayloadCompression", previewColumns);
            Assert.Contains("UncompressedByteSize", previewColumns);
            Assert.Equal(0L, ExecuteScalarInt64(conn, "SELECT PayloadCompression FROM PreviewCacheEntries LIMIT 1;"));
        }

        private static void CreateLegacyV12Database(
            string dbPath,
            string projectDir,
            string signature,
            string sourcePath,
            string meshId,
            string materialId,
            bool orphanAssetSource)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            using var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE CacheSchema (Key TEXT PRIMARY KEY, Value TEXT NOT NULL);
                INSERT INTO CacheSchema (Key, Value) VALUES ('SemanticSchemaVersion', '12');

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
                CREATE INDEX idx_projects_lookup ON Projects(FolderPath COLLATE NOCASE, SignatureHash, LastIndexed DESC);
                INSERT INTO Projects (FolderPath, SignatureHash, TotalFiles, TotalBytes, UnityBundleCount, UnityVersion)
                VALUES (@folder, @sig, 1, 16, 0, '2022.3.10f1');

                CREATE TABLE AssetHandles (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ProjectId INTEGER NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
                    UniqueID TEXT NOT NULL,
                    Name TEXT NOT NULL,
                    Type INTEGER NOT NULL,
                    Container TEXT,
                    OriginalPath TEXT,
                    SerializedFileName TEXT,
                    PathID INTEGER NOT NULL,
                    ByteStart INTEGER NOT NULL,
                    ByteSize INTEGER NOT NULL
                );
                CREATE INDEX idx_handles_lookup ON AssetHandles(ProjectId, UniqueID);

                CREATE TABLE SourceFiles (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ProjectId INTEGER NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
                    SerializedFileName TEXT NOT NULL,
                    OriginalPath TEXT NOT NULL DEFAULT '',
                    UnityVersion TEXT NOT NULL DEFAULT '',
                    ObjectCount INTEGER NOT NULL DEFAULT 0,
                    LastSeen DATETIME DEFAULT CURRENT_TIMESTAMP
                );
                CREATE UNIQUE INDEX idx_source_files_unique ON SourceFiles(ProjectId, SerializedFileName, OriginalPath);

                CREATE TABLE Assets (
                    ProjectId INTEGER NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
                    UniqueID TEXT NOT NULL,
                    SourceFileId INTEGER REFERENCES SourceFiles(Id) ON DELETE SET NULL,
                    Name TEXT NOT NULL,
                    Type INTEGER NOT NULL,
                    Container TEXT NOT NULL DEFAULT '',
                    PathID INTEGER NOT NULL,
                    ByteStart INTEGER NOT NULL,
                    ByteSize INTEGER NOT NULL,
                    PRIMARY KEY (ProjectId, UniqueID)
                ) WITHOUT ROWID;

                CREATE TABLE ProjectIndexingState (
                    ProjectId INTEGER PRIMARY KEY REFERENCES Projects(Id) ON DELETE CASCADE,
                    Status TEXT NOT NULL DEFAULT 'not_started',
                    TotalFiles INTEGER NOT NULL DEFAULT 0,
                    ProcessedFiles INTEGER NOT NULL DEFAULT 0,
                    PendingFiles INTEGER NOT NULL DEFAULT 0,
                    PercentComplete REAL NOT NULL DEFAULT 0,
                    CurrentFile TEXT NOT NULL DEFAULT '',
                    LastReadFile TEXT NOT NULL DEFAULT '',
                    StartedAt DATETIME DEFAULT CURRENT_TIMESTAMP,
                    UpdatedAt DATETIME DEFAULT CURRENT_TIMESTAMP,
                    CompletedAt DATETIME
                );

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
                    LastAccessed DATETIME DEFAULT CURRENT_TIMESTAMP,
                    UpdatedUtc TEXT NOT NULL DEFAULT '2026-01-01T00:00:00.0000000Z',
                    LastAccessedUtc TEXT NOT NULL DEFAULT '2026-01-01T00:00:00.0000000Z'
                );
                CREATE UNIQUE INDEX idx_preview_cache_unique ON PreviewCacheEntries(ProjectId, AssetUniqueID, PreviewKind, AlgorithmVersion, Parameters);
                INSERT INTO PreviewCacheEntries (ProjectId, AssetUniqueID, PreviewKind, AlgorithmVersion, Parameters, PayloadHash, PayloadPath, ByteSize)
                VALUES (1, 'preserved_preview', 'png', 1, '256', 'hash123', '/tmp/p.png', 512);

                INSERT INTO SourceFiles (ProjectId, SerializedFileName, OriginalPath, UnityVersion, ObjectCount)
                VALUES (1, 'level0', @sourcePath, '', 2);

                INSERT INTO AssetHandles (ProjectId, UniqueID, Name, Type, Container, OriginalPath, SerializedFileName, PathID, ByteStart, ByteSize)
                VALUES (1, @meshId, 'HeroMesh', @meshType, 'assets/hero.fbx', @sourcePath, 'level0', 100, 4096, 2048),
                       (1, @materialId, 'HeroMat', @materialType, '', @sourcePath, 'level0', 200, 8192, 256);

                INSERT INTO Assets (ProjectId, UniqueID, SourceFileId, Name, Type, Container, PathID, ByteStart, ByteSize)
                VALUES (1, @meshId, 1, 'HeroMesh', @meshType, 'assets/hero.fbx', 100, 4096, 2048),
                       (1, @materialId, @materialSourceFileId, 'HeroMat', @materialType, '', 200, 8192, 256);";
            cmd.Parameters.AddWithValue("@folder", projectDir);
            cmd.Parameters.AddWithValue("@sig", signature);
            cmd.Parameters.AddWithValue("@sourcePath", sourcePath);
            cmd.Parameters.AddWithValue("@meshId", meshId);
            cmd.Parameters.AddWithValue("@materialId", materialId);
            cmd.Parameters.AddWithValue("@meshType", (int)ClassIDType.Mesh);
            cmd.Parameters.AddWithValue("@materialType", (int)ClassIDType.Material);
            cmd.Parameters.AddWithValue("@materialSourceFileId", orphanAssetSource ? DBNull.Value : 1L);
            cmd.ExecuteNonQuery();
        }

        private static List<string> AssertSearchOnly(SqliteConnection conn, string queryName, string sql, Action<SqliteCommand> bind)
        {
            var planLines = new List<string>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "EXPLAIN QUERY PLAN " + sql;
                bind(cmd);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    planLines.Add(reader.GetString(3));
                }
            }

            var formattedPlan = string.Join(Environment.NewLine, planLines);
            Assert.True(
                planLines.Any(line => line.Contains("SEARCH ", StringComparison.OrdinalIgnoreCase)),
                $"Expected query '{queryName}' to use SEARCH, but plan was:{Environment.NewLine}{formattedPlan}");
            Assert.False(
                planLines.Any(line => line.StartsWith("SCAN ", StringComparison.OrdinalIgnoreCase)),
                $"Query '{queryName}' performed a SCAN:{Environment.NewLine}{formattedPlan}");
            return planLines;
        }

        private static string? GetSchemaObjectType(SqliteConnection conn, string name, string type)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT type FROM sqlite_master WHERE name = @name AND type = @type LIMIT 1;";
            cmd.Parameters.AddWithValue("@name", name);
            cmd.Parameters.AddWithValue("@type", type);
            return cmd.ExecuteScalar()?.ToString();
        }

        private static string GetTableDdl(SqliteConnection conn, string tableName)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = @name LIMIT 1;";
            cmd.Parameters.AddWithValue("@name", tableName);
            return cmd.ExecuteScalar()?.ToString() ?? string.Empty;
        }

        private static string? ReadSchemaVersion(SqliteConnection conn)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Value FROM CacheSchema WHERE Key = 'SemanticSchemaVersion' LIMIT 1;";
            return cmd.ExecuteScalar()?.ToString();
        }

        private static HashSet<string> GetColumnNames(SqliteConnection conn, string tableName)
        {
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"PRAGMA table_info({tableName});";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                columns.Add(reader.GetString(1));
            }

            return columns;
        }

        private static HashSet<string> GetUserIndexNames(SqliteConnection conn)
        {
            var indexes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index' AND name NOT LIKE 'sqlite_%';";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                indexes.Add(reader.GetString(0));
            }

            return indexes;
        }

        private static long ExecuteScalarInt64(SqliteConnection conn, string sql)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            return Convert.ToInt64(cmd.ExecuteScalar());
        }

        private static (SQLiteProjectIndexCache Cache, string ProjectDir, string Signature, ProjectScanResult ScanResult, string SourcePath)
            CreateSampleProject(string tempRoot)
        {
            var cacheDir = Path.Combine(tempRoot, "IndexCache");
            var projectDir = Path.Combine(tempRoot, "SampleProject");
            Directory.CreateDirectory(projectDir);
            var sourcePath = Path.Combine(projectDir, "level0");
            File.WriteAllBytes(sourcePath, new byte[64]);

            var scanResult = ProjectScanner.ScanFolder(projectDir);
            var cache = new SQLiteProjectIndexCache(cacheDir);
            var signature = cache.GetFolderSignature(scanResult);
            return (cache, projectDir, signature, scanResult, sourcePath);
        }

        private static AssetHandle CreateHandle(string uniqueId, string name, ClassIDType type, string sourcePath, long pathId)
        {
            return new AssetHandle
            {
                UniqueID = uniqueId,
                Name = name,
                Type = type,
                Container = $"assets/{name.ToLowerInvariant()}",
                OriginalPath = sourcePath,
                SerializedFileName = "level0",
                PathID = pathId,
                ByteStart = pathId * 64,
                ByteSize = 1024
            };
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
