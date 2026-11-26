using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using AssetStudio;
using AssetStudio.Avalonia.Services;
using AssetStudio.Avalonia.Services.ProjectAnalysis;
using Microsoft.Data.Sqlite;

namespace AssetStudio.Avalonia.Services.ProjectAnalysis
{
    public sealed record SemanticGraphUsageItem(
        string AssetUniqueId,
        string Name,
        ClassIDType AssetType,
        string TypeName,
        string Relation,
        string Detail,
        long PathId,
        string SerializedFileName,
        string OriginalPath)
    {
        public string PathIdText => PathId != 0
            ? PathId.ToString(CultureInfo.InvariantCulture)
            : "-";

        public bool IsNavigable => !string.IsNullOrWhiteSpace(AssetUniqueId) || PathId != 0;

        public string DisplaySummary
        {
            get
            {
                var rel = string.IsNullOrWhiteSpace(Relation) ? "Usage" : Relation;
                return string.IsNullOrWhiteSpace(Detail)
                    ? $"{TypeName} • {rel}"
                    : $"{TypeName} • {rel} • {Detail}";
            }
        }
    }

    public sealed class SemanticGraphUsageResult
    {
        public static readonly SemanticGraphUsageResult Empty = new();

        public string QueryKind { get; init; } = "Empty";
        public string TargetAssetUniqueId { get; init; } = string.Empty;
        public string TargetName { get; init; } = string.Empty;
        public ClassIDType TargetType { get; init; }
        public string Summary { get; init; } = "No usages found.";
        public bool IsTruncated { get; init; }
        public IReadOnlyList<SemanticGraphUsageItem> Items { get; init; } = Array.Empty<SemanticGraphUsageItem>();
    }

    public sealed record BuildDiffAssetSnapshot(
        string AssetUniqueId,
        string SerializedFileName,
        string OriginalPath,
        long PathId,
        string Name,
        ClassIDType Type,
        string Container,
        long ByteStart,
        long ByteSize);

    public sealed class BuildDiffSnapshotLoadResult
    {
        public static BuildDiffSnapshotLoadResult Unavailable(string note) => new()
        {
            HasSnapshots = false,
            Source = "None",
            DiagnosticNote = note ?? string.Empty
        };

        public bool HasSnapshots { get; init; }
        public string Source { get; init; } = "None";
        public string Signature { get; init; } = string.Empty;
        public string DiagnosticNote { get; init; } = string.Empty;
        public IReadOnlyList<BuildDiffAssetSnapshot> Snapshots { get; init; } = Array.Empty<BuildDiffAssetSnapshot>();
    }
}

namespace AssetStudio.Avalonia
{
    public partial class SQLiteProjectIndexCache
    {
        internal const int DefaultAnalysisGraphMaxResults = 500;
        private const int HardAnalysisGraphMaxResults = 5000;

        public SemanticGraphUsageResult FindMaterialsUsingShader(
            string folderPath,
            string signature,
            string shaderNameOrAssetId,
            int maxResults = DefaultAnalysisGraphMaxResults)
        {
            if (string.IsNullOrWhiteSpace(folderPath)
                || string.IsNullOrWhiteSpace(signature)
                || string.IsNullOrWhiteSpace(shaderNameOrAssetId))
            {
                return SemanticGraphUsageResult.Empty;
            }

            if (!File.Exists(GetDbPath(folderPath)))
            {
                return new SemanticGraphUsageResult
                {
                    QueryKind = "Shader",
                    TargetAssetUniqueId = shaderNameOrAssetId.Trim(),
                    TargetName = shaderNameOrAssetId.Trim(),
                    TargetType = ClassIDType.Shader,
                    Summary = "No SQLite project cache found for this folder."
                };
            }

            var limit = ClampAnalysisLimit(maxResults);
            var fetchLimit = limit + 1;
            var queryKey = shaderNameOrAssetId.Trim();
            var items = new List<SemanticGraphUsageItem>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var resolvedTargetUid = queryKey;
            var resolvedTargetName = queryKey;
            var truncated = false;

            ExecuteProjectReadQuery(folderPath, signature, "materials using shader", (conn, transaction, projectId) =>
            {
                var shaderIds = new HashSet<string>(StringComparer.Ordinal);
                var shaderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // Resolve whether input is an Asset UniqueID or a Shader name (or both).
                using (var lookupCmd = conn.CreateCommand())
                {
                    lookupCmd.Transaction = transaction;
                    lookupCmd.CommandText = @"
                        SELECT UniqueID, Name, Type
                        FROM Assets
                        WHERE ProjectId = @projectId
                          AND UniqueID = @key
                        LIMIT 1";
                    lookupCmd.Parameters.AddWithValue("@projectId", projectId);
                    lookupCmd.Parameters.AddWithValue("@key", queryKey);
                    using var reader = lookupCmd.ExecuteReader();
                    if (reader.Read())
                    {
                        var uid = reader.GetString(0);
                        var name = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                        shaderIds.Add(uid);
                        resolvedTargetUid = uid;
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            shaderNames.Add(name);
                            resolvedTargetName = name;
                        }
                    }
                }

                if (shaderIds.Count == 0)
                {
                    shaderNames.Add(queryKey);
                    // Also check if queryKey matches any Shader assets by name so AssetEdges can resolve them.
                    using var nameLookupCmd = conn.CreateCommand();
                    nameLookupCmd.Transaction = transaction;
                    nameLookupCmd.CommandText = @"
                        SELECT UniqueID, Name
                        FROM Assets
                        WHERE ProjectId = @projectId
                          AND Type = @shaderType
                          AND Name = @name COLLATE NOCASE
                        LIMIT 32";
                    nameLookupCmd.Parameters.AddWithValue("@projectId", projectId);
                    nameLookupCmd.Parameters.AddWithValue("@shaderType", (int)ClassIDType.Shader);
                    nameLookupCmd.Parameters.AddWithValue("@name", queryKey);
                    using var reader = nameLookupCmd.ExecuteReader();
                    while (reader.Read())
                    {
                        var uid = reader.GetString(0);
                        var name = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                        if (!string.IsNullOrWhiteSpace(uid))
                        {
                            shaderIds.Add(uid);
                        }
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            shaderNames.Add(name);
                        }
                    }

                    // Allow AssetEdges direct lookup even when Assets row wasn't present (e.g. synthetic test DB).
                    shaderIds.Add(queryKey);
                }

                // 1) MaterialCache lookup by ShaderName (exact match prioritized, plus substring fallback).
                foreach (var shaderName in shaderNames)
                {
                    if (items.Count >= limit)
                    {
                        truncated = true;
                        break;
                    }

                    var escapedLike = EscapeLikePattern(shaderName);
                    using var cmd = conn.CreateCommand();
                    cmd.Transaction = transaction;
                    cmd.CommandText = @"
                        SELECT
                            mc.MaterialAssetUniqueID,
                            COALESCE(NULLIF(mc.MaterialName, ''), a.Name, ''),
                            COALESCE(a.Type, 21),
                            COALESCE(a.PathID, 0),
                            COALESCE(sf.SerializedFileName, ''),
                            COALESCE(sf.OriginalPath, ''),
                            mc.ShaderName,
                            mc.TextureSlotCount
                        FROM MaterialCache mc
                        LEFT JOIN Assets a
                          ON a.ProjectId = mc.ProjectId
                         AND a.UniqueID = mc.MaterialAssetUniqueID
                        LEFT JOIN SourceFiles sf
                          ON sf.ProjectId = a.ProjectId
                         AND sf.Id = a.SourceFileId
                        WHERE mc.ProjectId = @projectId
                          AND mc.ShaderName <> ''
                          AND (LOWER(mc.ShaderName) = LOWER(@exactName)
                               OR LOWER(mc.ShaderName) LIKE LOWER(@likePattern) ESCAPE '\')
                        ORDER BY
                            CASE WHEN LOWER(mc.ShaderName) = LOWER(@exactName) THEN 0 ELSE 1 END,
                            COALESCE(NULLIF(mc.MaterialName, ''), a.Name, '') ASC,
                            mc.MaterialAssetUniqueID ASC
                        LIMIT @limit";
                    cmd.Parameters.AddWithValue("@projectId", projectId);
                    cmd.Parameters.AddWithValue("@exactName", shaderName);
                    cmd.Parameters.AddWithValue("@likePattern", $"%{escapedLike}%");
                    cmd.Parameters.AddWithValue("@limit", fetchLimit);
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        var matId = reader.GetString(0);
                        if (string.IsNullOrWhiteSpace(matId))
                        {
                            continue;
                        }

                        var dedupKey = $"MAT|{matId}";
                        if (!seen.Add(dedupKey))
                        {
                            continue;
                        }

                        if (items.Count >= limit)
                        {
                            truncated = true;
                            break;
                        }

                        var name = reader.GetString(1);
                        var type = (ClassIDType)reader.GetInt32(2);
                        if (type == ClassIDType.UnknownType)
                        {
                            type = ClassIDType.Material;
                        }

                        var pathId = reader.GetInt64(3);
                        var sfName = reader.GetString(4);
                        var origPath = reader.GetString(5);
                        var matchedShader = reader.GetString(6);
                        var texSlots = reader.GetInt32(7);
                        var detail = texSlots > 0
                            ? $"Shader: {matchedShader} ({texSlots} texture slot(s))"
                            : $"Shader: {matchedShader}";

                        items.Add(new SemanticGraphUsageItem(
                            AssetUniqueId: matId,
                            Name: string.IsNullOrWhiteSpace(name) ? $"Material ({pathId})" : name,
                            AssetType: type,
                            TypeName: AssetItem.GetCachedTypeString(type),
                            Relation: "Material (ShaderName)",
                            Detail: detail,
                            PathId: pathId,
                            SerializedFileName: sfName,
                            OriginalPath: origPath));
                    }
                }

                // 2) AssetEdges incoming lookup (idx_asset_edges_target_kind) for shader AssetUniqueIDs.
                foreach (var shaderId in shaderIds)
                {
                    if (items.Count >= limit)
                    {
                        truncated = true;
                        break;
                    }

                    using var cmd = conn.CreateCommand();
                    cmd.Transaction = transaction;
                    cmd.CommandText = @"
                        SELECT
                            ae.SourceAssetUniqueID,
                            ae.EdgeKind,
                            ae.SlotName,
                            COALESCE(a.Name, ''),
                            COALESCE(a.Type, 21),
                            COALESCE(a.PathID, ae.SourcePathID),
                            COALESCE(sf.SerializedFileName, ''),
                            COALESCE(sf.OriginalPath, '')
                        FROM AssetEdges ae
                        LEFT JOIN Assets a
                          ON a.ProjectId = ae.ProjectId
                         AND a.UniqueID = ae.SourceAssetUniqueID
                        LEFT JOIN SourceFiles sf
                          ON sf.ProjectId = a.ProjectId
                         AND sf.Id = a.SourceFileId
                        WHERE ae.ProjectId = @projectId
                          AND ae.TargetAssetUniqueID = @shaderId
                          AND ae.SourceAssetUniqueID <> ''
                          AND ae.SourceAssetUniqueID <> @shaderId
                        ORDER BY COALESCE(a.Name, '') ASC, ae.SourceAssetUniqueID ASC
                        LIMIT @limit";
                    cmd.Parameters.AddWithValue("@projectId", projectId);
                    cmd.Parameters.AddWithValue("@shaderId", shaderId);
                    cmd.Parameters.AddWithValue("@limit", fetchLimit);
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        var sourceId = reader.GetString(0);
                        if (string.IsNullOrWhiteSpace(sourceId))
                        {
                            continue;
                        }

                        var dedupKey = $"MAT|{sourceId}";
                        if (!seen.Add(dedupKey))
                        {
                            continue;
                        }

                        if (items.Count >= limit)
                        {
                            truncated = true;
                            break;
                        }

                        var edgeKind = reader.GetString(1);
                        var slotName = reader.GetString(2);
                        var name = reader.GetString(3);
                        var type = (ClassIDType)reader.GetInt32(4);
                        if (type == ClassIDType.UnknownType)
                        {
                            type = ClassIDType.Material;
                        }

                        var pathId = reader.GetInt64(5);
                        var sfName = reader.GetString(6);
                        var origPath = reader.GetString(7);
                        var detail = string.IsNullOrWhiteSpace(slotName)
                            ? $"Edge: {edgeKind}"
                            : $"Edge: {edgeKind} ({slotName})";

                        items.Add(new SemanticGraphUsageItem(
                            AssetUniqueId: sourceId,
                            Name: string.IsNullOrWhiteSpace(name) ? $"{AssetItem.GetCachedTypeString(type)} ({pathId})" : name,
                            AssetType: type,
                            TypeName: AssetItem.GetCachedTypeString(type),
                            Relation: "Material (Shader Edge)",
                            Detail: detail,
                            PathId: pathId,
                            SerializedFileName: sfName,
                            OriginalPath: origPath));
                    }
                }
            });

            var summary = items.Count == 0
                ? $"No materials found using shader '{resolvedTargetName}'."
                : truncated
                    ? $"Found {items.Count:N0}+ materials using shader '{resolvedTargetName}' (truncated at {limit:N0})."
                    : $"Found {items.Count:N0} material(s) using shader '{resolvedTargetName}'.";

            return new SemanticGraphUsageResult
            {
                QueryKind = "Shader",
                TargetAssetUniqueId = resolvedTargetUid,
                TargetName = resolvedTargetName,
                TargetType = ClassIDType.Shader,
                Summary = summary,
                IsTruncated = truncated,
                Items = items
            };
        }

        public SemanticGraphUsageResult FindUsagesOfTexture(
            string folderPath,
            string signature,
            string textureAssetUniqueId,
            int maxResults = DefaultAnalysisGraphMaxResults)
        {
            if (string.IsNullOrWhiteSpace(folderPath)
                || string.IsNullOrWhiteSpace(signature)
                || string.IsNullOrWhiteSpace(textureAssetUniqueId))
            {
                return SemanticGraphUsageResult.Empty;
            }

            var targetId = textureAssetUniqueId.Trim();
            if (!File.Exists(GetDbPath(folderPath)))
            {
                return new SemanticGraphUsageResult
                {
                    QueryKind = "Texture",
                    TargetAssetUniqueId = targetId,
                    TargetName = targetId,
                    TargetType = ClassIDType.Texture2D,
                    Summary = "No SQLite project cache found for this folder."
                };
            }

            var limit = ClampAnalysisLimit(maxResults);
            var fetchLimit = limit + 1;
            var items = new List<SemanticGraphUsageItem>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var materialIds = new List<string>();
            var seenMaterialIds = new HashSet<string>(StringComparer.Ordinal);
            var materialNamesById = new Dictionary<string, string>(StringComparer.Ordinal);
            var targetName = targetId;
            var targetType = ClassIDType.Texture2D;
            var truncated = false;

            ExecuteProjectReadQuery(folderPath, signature, "usages of texture", (conn, transaction, projectId) =>
            {
                ResolveAssetHeader(conn, transaction, projectId, targetId, ref targetName, ref targetType);

                // 1) Direct Materials via MaterialCache (idx_material_cache_main_tex).
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = transaction;
                    cmd.CommandText = @"
                        SELECT
                            mc.MaterialAssetUniqueID,
                            mc.MainTextureSlotName,
                            COALESCE(NULLIF(mc.MaterialName, ''), a.Name, ''),
                            COALESCE(a.Type, 21),
                            COALESCE(a.PathID, 0),
                            COALESCE(sf.SerializedFileName, ''),
                            COALESCE(sf.OriginalPath, ''),
                            mc.ShaderName
                        FROM MaterialCache mc
                        LEFT JOIN Assets a
                          ON a.ProjectId = mc.ProjectId
                         AND a.UniqueID = mc.MaterialAssetUniqueID
                        LEFT JOIN SourceFiles sf
                          ON sf.ProjectId = a.ProjectId
                         AND sf.Id = a.SourceFileId
                        WHERE mc.ProjectId = @projectId
                          AND mc.MainTextureAssetUniqueID = @texId
                          AND mc.MaterialAssetUniqueID <> ''
                        ORDER BY COALESCE(NULLIF(mc.MaterialName, ''), a.Name, '') ASC, mc.MaterialAssetUniqueID ASC
                        LIMIT @limit";
                    cmd.Parameters.AddWithValue("@projectId", projectId);
                    cmd.Parameters.AddWithValue("@texId", targetId);
                    cmd.Parameters.AddWithValue("@limit", fetchLimit);
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        var matId = reader.GetString(0);
                        if (string.IsNullOrWhiteSpace(matId))
                        {
                            continue;
                        }

                        var slotName = reader.GetString(1);
                        var name = reader.GetString(2);
                        var type = (ClassIDType)reader.GetInt32(3);
                        if (type == ClassIDType.UnknownType) type = ClassIDType.Material;
                        var pathId = reader.GetInt64(4);
                        var sfName = reader.GetString(5);
                        var origPath = reader.GetString(6);
                        var shaderName = reader.GetString(7);
                        var displayName = string.IsNullOrWhiteSpace(name) ? $"Material ({pathId})" : name;

                        if (seenMaterialIds.Add(matId))
                        {
                            materialIds.Add(matId);
                            materialNamesById[matId] = displayName;
                        }

                        if (!seen.Add($"MAT|{matId}"))
                        {
                            continue;
                        }

                        if (items.Count >= limit)
                        {
                            truncated = true;
                            break;
                        }

                        var detail = BuildTextureSlotDetail(slotName, isMain: true, shaderName);
                        items.Add(new SemanticGraphUsageItem(
                            AssetUniqueId: matId,
                            Name: displayName,
                            AssetType: type,
                            TypeName: AssetItem.GetCachedTypeString(type),
                            Relation: "Material (Main Texture)",
                            Detail: detail,
                            PathId: pathId,
                            SerializedFileName: sfName,
                            OriginalPath: origPath));
                    }
                }

                // 2) Direct Materials via MaterialTextures (catches non-main texture slots like _BumpMap / _EmissionMap).
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = transaction;
                    cmd.CommandText = @"
                        SELECT
                            mt.MaterialAssetUniqueID,
                            mt.SlotName,
                            mt.IsMainTexture,
                            COALESCE(a.Name, ''),
                            COALESCE(a.Type, 21),
                            COALESCE(a.PathID, 0),
                            COALESCE(sf.SerializedFileName, ''),
                            COALESCE(sf.OriginalPath, '')
                        FROM MaterialTextures mt
                        LEFT JOIN Assets a
                          ON a.ProjectId = mt.ProjectId
                         AND a.UniqueID = mt.MaterialAssetUniqueID
                        LEFT JOIN SourceFiles sf
                          ON sf.ProjectId = a.ProjectId
                         AND sf.Id = a.SourceFileId
                        WHERE mt.ProjectId = @projectId
                          AND mt.TextureAssetUniqueID = @texId
                          AND mt.MaterialAssetUniqueID <> ''
                        ORDER BY mt.IsMainTexture DESC, mt.SlotIndex ASC, mt.MaterialAssetUniqueID ASC
                        LIMIT @limit";
                    cmd.Parameters.AddWithValue("@projectId", projectId);
                    cmd.Parameters.AddWithValue("@texId", targetId);
                    cmd.Parameters.AddWithValue("@limit", fetchLimit);
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        var matId = reader.GetString(0);
                        if (string.IsNullOrWhiteSpace(matId))
                        {
                            continue;
                        }

                        var slotName = reader.GetString(1);
                        var isMain = reader.GetInt32(2) != 0;
                        var name = reader.GetString(3);
                        var type = (ClassIDType)reader.GetInt32(4);
                        if (type == ClassIDType.UnknownType) type = ClassIDType.Material;
                        var pathId = reader.GetInt64(5);
                        var sfName = reader.GetString(6);
                        var origPath = reader.GetString(7);
                        var displayName = string.IsNullOrWhiteSpace(name) ? $"Material ({pathId})" : name;

                        if (seenMaterialIds.Add(matId))
                        {
                            materialIds.Add(matId);
                            materialNamesById[matId] = displayName;
                        }

                        if (!seen.Add($"MAT|{matId}"))
                        {
                            continue;
                        }

                        if (items.Count >= limit)
                        {
                            truncated = true;
                            break;
                        }

                        items.Add(new SemanticGraphUsageItem(
                            AssetUniqueId: matId,
                            Name: displayName,
                            AssetType: type,
                            TypeName: AssetItem.GetCachedTypeString(type),
                            Relation: isMain ? "Material (Main Texture)" : "Material (Texture Slot)",
                            Detail: BuildTextureSlotDetail(slotName, isMain, string.Empty),
                            PathId: pathId,
                            SerializedFileName: sfName,
                            OriginalPath: origPath));
                    }
                }

                // 3) Direct incoming AssetEdges (e.g. Sprite -> Texture2D, SpriteAtlas, or Material edge).
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = transaction;
                    cmd.CommandText = @"
                        SELECT
                            ae.SourceAssetUniqueID,
                            ae.EdgeKind,
                            ae.SlotName,
                            COALESCE(a.Name, ''),
                            COALESCE(a.Type, 0),
                            COALESCE(a.PathID, ae.SourcePathID),
                            COALESCE(sf.SerializedFileName, ''),
                            COALESCE(sf.OriginalPath, '')
                        FROM AssetEdges ae
                        LEFT JOIN Assets a
                          ON a.ProjectId = ae.ProjectId
                         AND a.UniqueID = ae.SourceAssetUniqueID
                        LEFT JOIN SourceFiles sf
                          ON sf.ProjectId = a.ProjectId
                         AND sf.Id = a.SourceFileId
                        WHERE ae.ProjectId = @projectId
                          AND ae.TargetAssetUniqueID = @texId
                          AND ae.SourceAssetUniqueID <> ''
                          AND ae.SourceAssetUniqueID <> @texId
                        LIMIT @limit";
                    cmd.Parameters.AddWithValue("@projectId", projectId);
                    cmd.Parameters.AddWithValue("@texId", targetId);
                    cmd.Parameters.AddWithValue("@limit", fetchLimit);
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        var sourceId = reader.GetString(0);
                        if (string.IsNullOrWhiteSpace(sourceId))
                        {
                            continue;
                        }

                        var edgeKind = reader.GetString(1);
                        var slotName = reader.GetString(2);
                        var name = reader.GetString(3);
                        var type = (ClassIDType)reader.GetInt32(4);
                        var pathId = reader.GetInt64(5);
                        var sfName = reader.GetString(6);
                        var origPath = reader.GetString(7);
                        var displayName = string.IsNullOrWhiteSpace(name)
                            ? $"{AssetItem.GetCachedTypeString(type)} ({pathId})"
                            : name;

                        if (type == ClassIDType.Material && seenMaterialIds.Add(sourceId))
                        {
                            materialIds.Add(sourceId);
                            materialNamesById[sourceId] = displayName;
                        }

                        var dedupKey = type == ClassIDType.Material ? $"MAT|{sourceId}" : $"EDGE|{sourceId}";
                        if (!seen.Add(dedupKey))
                        {
                            continue;
                        }

                        if (items.Count >= limit)
                        {
                            truncated = true;
                            break;
                        }

                        items.Add(new SemanticGraphUsageItem(
                            AssetUniqueId: sourceId,
                            Name: displayName,
                            AssetType: type,
                            TypeName: AssetItem.GetCachedTypeString(type),
                            Relation: $"Direct Reference ({edgeKind})",
                            Detail: string.IsNullOrWhiteSpace(slotName) ? edgeKind : $"Slot: {slotName}",
                            PathId: pathId,
                            SerializedFileName: sfName,
                            OriginalPath: origPath));
                    }
                }

                // 4) Downstream Meshes & Models using those Materials (MeshMaterialResolvedCache + CompositeAssetParts + ModelGroups).
                if (materialIds.Count > 0 && items.Count < limit)
                {
                    CollectDownstreamMeshesAndModelsForMaterials(
                        conn,
                        transaction,
                        projectId,
                        materialIds,
                        materialNamesById,
                        items,
                        seen,
                        limit,
                        ref truncated);
                }
            });

            var matCount = materialIds.Count;
            var downstreamCount = Math.Max(0, items.Count - matCount);
            var summary = items.Count == 0
                ? $"No usages found for '{targetName}'."
                : truncated
                    ? $"Found {items.Count:N0}+ usages for '{targetName}' ({matCount:N0} material(s), {downstreamCount:N0} downstream mesh/model/reference usage(s); truncated)."
                    : $"Found {items.Count:N0} usage(s) for '{targetName}' ({matCount:N0} material(s), {downstreamCount:N0} downstream usage(s)).";

            return new SemanticGraphUsageResult
            {
                QueryKind = "Texture",
                TargetAssetUniqueId = targetId,
                TargetName = targetName,
                TargetType = targetType,
                Summary = summary,
                IsTruncated = truncated,
                Items = items
            };
        }

        public SemanticGraphUsageResult FindModelsUsingMaterial(
            string folderPath,
            string signature,
            string materialAssetUniqueId,
            int maxResults = DefaultAnalysisGraphMaxResults)
        {
            if (string.IsNullOrWhiteSpace(folderPath)
                || string.IsNullOrWhiteSpace(signature)
                || string.IsNullOrWhiteSpace(materialAssetUniqueId))
            {
                return SemanticGraphUsageResult.Empty;
            }

            var targetId = materialAssetUniqueId.Trim();
            if (!File.Exists(GetDbPath(folderPath)))
            {
                return new SemanticGraphUsageResult
                {
                    QueryKind = "Material",
                    TargetAssetUniqueId = targetId,
                    TargetName = targetId,
                    TargetType = ClassIDType.Material,
                    Summary = "No SQLite project cache found for this folder."
                };
            }

            var limit = ClampAnalysisLimit(maxResults);
            var items = new List<SemanticGraphUsageItem>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var targetName = targetId;
            var targetType = ClassIDType.Material;
            var truncated = false;

            ExecuteProjectReadQuery(folderPath, signature, "models using material", (conn, transaction, projectId) =>
            {
                ResolveAssetHeader(conn, transaction, projectId, targetId, ref targetName, ref targetType);

                var materialNamesById = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [targetId] = targetName
                };

                CollectDownstreamMeshesAndModelsForMaterials(
                    conn,
                    transaction,
                    projectId,
                    new[] { targetId },
                    materialNamesById,
                    items,
                    seen,
                    limit,
                    ref truncated);
            });

            var summary = items.Count == 0
                ? $"No meshes or models found using material '{targetName}'."
                : truncated
                    ? $"Found {items.Count:N0}+ mesh/model usages for material '{targetName}' (truncated at {limit:N0})."
                    : $"Found {items.Count:N0} mesh/model usage(s) for material '{targetName}'.";

            return new SemanticGraphUsageResult
            {
                QueryKind = "Material",
                TargetAssetUniqueId = targetId,
                TargetName = targetName,
                TargetType = targetType,
                Summary = summary,
                IsTruncated = truncated,
                Items = items
            };
        }

        public SemanticGraphUsageResult FindAnimationsForAvatarOrRig(
            string folderPath,
            string signature,
            string avatarOrModelGroupId,
            int maxResults = DefaultAnalysisGraphMaxResults)
        {
            if (string.IsNullOrWhiteSpace(folderPath)
                || string.IsNullOrWhiteSpace(signature)
                || string.IsNullOrWhiteSpace(avatarOrModelGroupId))
            {
                return SemanticGraphUsageResult.Empty;
            }

            var targetId = avatarOrModelGroupId.Trim();
            if (!File.Exists(GetDbPath(folderPath)))
            {
                return new SemanticGraphUsageResult
                {
                    QueryKind = "AnimationRig",
                    TargetAssetUniqueId = targetId,
                    TargetName = targetId,
                    TargetType = ClassIDType.Avatar,
                    Summary = "No SQLite project cache found for this folder."
                };
            }

            var limit = ClampAnalysisLimit(maxResults);
            var fetchLimit = limit + 1;
            var items = new List<SemanticGraphUsageItem>();
            var seenClips = new HashSet<string>(StringComparer.Ordinal);
            var targetName = targetId;
            var targetType = ClassIDType.Avatar;
            var truncated = false;

            ExecuteProjectReadQuery(folderPath, signature, "animations for avatar or rig", (conn, transaction, projectId) =>
            {
                ResolveAssetHeader(conn, transaction, projectId, targetId, ref targetName, ref targetType);

                var avatarIds = new HashSet<string>(StringComparer.Ordinal) { targetId };
                var meshIds = new HashSet<string>(StringComparer.Ordinal) { targetId };
                var modelGroupIds = new HashSet<string>(StringComparer.Ordinal) { targetId };
                var animatorIds = new HashSet<string>(StringComparer.Ordinal) { targetId };

                // Expand linked rig identities via indexed ModelGroups & CompositeAssets lookups.
                using (var grpCmd = conn.CreateCommand())
                {
                    grpCmd.Transaction = transaction;
                    grpCmd.CommandText = @"
                        SELECT GroupAssetUniqueID, AvatarAssetUniqueID, AnimatorAssetUniqueID, GroupName
                        FROM ModelGroups
                        WHERE ProjectId = @projectId
                          AND (GroupAssetUniqueID = @id
                               OR AvatarAssetUniqueID = @id
                               OR AnimatorAssetUniqueID = @id
                               OR RootGameObjectAssetUniqueID = @id)
                        LIMIT 32";
                    grpCmd.Parameters.AddWithValue("@projectId", projectId);
                    grpCmd.Parameters.AddWithValue("@id", targetId);
                    using var reader = grpCmd.ExecuteReader();
                    while (reader.Read())
                    {
                        AddNonEmpty(modelGroupIds, reader.IsDBNull(0) ? string.Empty : reader.GetString(0));
                        AddNonEmpty(avatarIds, reader.IsDBNull(1) ? string.Empty : reader.GetString(1));
                        AddNonEmpty(animatorIds, reader.IsDBNull(2) ? string.Empty : reader.GetString(2));
                        if (string.Equals(targetName, targetId, StringComparison.Ordinal) && !reader.IsDBNull(3))
                        {
                            var grpName = reader.GetString(3);
                            if (!string.IsNullOrWhiteSpace(grpName))
                            {
                                targetName = grpName;
                            }
                        }
                    }
                }

                using (var compCmd = conn.CreateCommand())
                {
                    compCmd.Transaction = transaction;
                    compCmd.CommandText = @"
                        SELECT CompositeAssetUniqueID, AvatarAssetUniqueID, AnimatorAssetUniqueID, DisplayName
                        FROM CompositeAssets
                        WHERE ProjectId = @projectId
                          AND (CompositeAssetUniqueID = @id
                               OR AvatarAssetUniqueID = @id
                               OR AnimatorAssetUniqueID = @id
                               OR RootGameObjectAssetUniqueID = @id)
                        LIMIT 32";
                    compCmd.Parameters.AddWithValue("@projectId", projectId);
                    compCmd.Parameters.AddWithValue("@id", targetId);
                    using var reader = compCmd.ExecuteReader();
                    while (reader.Read())
                    {
                        AddNonEmpty(modelGroupIds, reader.IsDBNull(0) ? string.Empty : reader.GetString(0));
                        AddNonEmpty(avatarIds, reader.IsDBNull(1) ? string.Empty : reader.GetString(1));
                        AddNonEmpty(animatorIds, reader.IsDBNull(2) ? string.Empty : reader.GetString(2));
                        if (string.Equals(targetName, targetId, StringComparison.Ordinal) && !reader.IsDBNull(3))
                        {
                            var dispName = reader.GetString(3);
                            if (!string.IsNullOrWhiteSpace(dispName))
                            {
                                targetName = dispName;
                            }
                        }
                    }
                }

                var rawCandidates = new List<(string ClipId, string ClipName, long PathId, string SfName, string OrigPath, string RelKind, int MatchScore, int MatchedBones, int TotalBones)>();

                void QueryTargetsByColumn(string columnName, IEnumerable<string> ids)
                {
                    var idList = ids.Where(static s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.Ordinal).Take(64).ToList();
                    if (idList.Count == 0)
                    {
                        return;
                    }

                    using var cmd = conn.CreateCommand();
                    cmd.Transaction = transaction;
                    var placeholders = new List<string>(idList.Count);
                    for (var i = 0; i < idList.Count; i++)
                    {
                        var pName = "@p" + i.ToString(CultureInfo.InvariantCulture);
                        placeholders.Add(pName);
                        cmd.Parameters.AddWithValue(pName, idList[i]);
                    }

                    cmd.CommandText = $@"
                        SELECT
                            t.ClipAssetUniqueID,
                            COALESCE(c.Name, ''),
                            COALESCE(c.PathID, 0),
                            COALESCE(sf.SerializedFileName, ''),
                            COALESCE(sf.OriginalPath, ''),
                            t.RelationKind,
                            t.MatchScore,
                            t.MatchedBoneCount,
                            t.TotalClipBoneCount
                        FROM AnimationClipTargets t
                        LEFT JOIN Assets c
                          ON c.ProjectId = t.ProjectId
                         AND c.UniqueID = t.ClipAssetUniqueID
                        LEFT JOIN SourceFiles sf
                          ON sf.ProjectId = c.ProjectId
                         AND sf.Id = c.SourceFileId
                        WHERE t.ProjectId = @projectId
                          AND t.{columnName} IN ({string.Join(",", placeholders)})
                          AND t.ClipAssetUniqueID <> ''
                        ORDER BY t.MatchScore DESC, t.MatchedBoneCount DESC, t.ClipAssetUniqueID ASC
                        LIMIT @limit";
                    cmd.Parameters.AddWithValue("@projectId", projectId);
                    cmd.Parameters.AddWithValue("@limit", fetchLimit);

                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        rawCandidates.Add((
                            reader.GetString(0),
                            reader.GetString(1),
                            reader.GetInt64(2),
                            reader.GetString(3),
                            reader.GetString(4),
                            reader.GetString(5),
                            reader.GetInt32(6),
                            reader.GetInt32(7),
                            reader.GetInt32(8)));
                    }
                }

                // 1) Indexed queries on AvatarAssetUniqueID & MeshAssetUniqueID
                QueryTargetsByColumn("AvatarAssetUniqueID", avatarIds);
                QueryTargetsByColumn("MeshAssetUniqueID", meshIds);
                // 2) ModelGroupId & AnimatorAssetUniqueID
                QueryTargetsByColumn("ModelGroupId", modelGroupIds);
                QueryTargetsByColumn("AnimatorAssetUniqueID", animatorIds);

                rawCandidates.Sort(static (a, b) =>
                {
                    var cmp = b.MatchScore.CompareTo(a.MatchScore);
                    if (cmp != 0) return cmp;
                    cmp = b.MatchedBones.CompareTo(a.MatchedBones);
                    if (cmp != 0) return cmp;
                    cmp = string.Compare(a.ClipName, b.ClipName, StringComparison.OrdinalIgnoreCase);
                    if (cmp != 0) return cmp;
                    return string.CompareOrdinal(a.ClipId, b.ClipId);
                });

                foreach (var c in rawCandidates)
                {
                    if (!seenClips.Add(c.ClipId))
                    {
                        continue;
                    }

                    if (items.Count >= limit)
                    {
                        truncated = true;
                        break;
                    }

                    var relLabel = string.IsNullOrWhiteSpace(c.RelKind)
                        ? "Compatible AnimationClip"
                        : $"Animation ({c.RelKind})";
                    var detailParts = new List<string>(2);
                    if (c.MatchScore > 0)
                    {
                        detailParts.Add($"Score {c.MatchScore}");
                    }
                    if (c.TotalBones > 0)
                    {
                        detailParts.Add($"Bones {c.MatchedBones}/{c.TotalBones}");
                    }

                    items.Add(new SemanticGraphUsageItem(
                        AssetUniqueId: c.ClipId,
                        Name: string.IsNullOrWhiteSpace(c.ClipName) ? $"AnimationClip ({c.PathId})" : c.ClipName,
                        AssetType: ClassIDType.AnimationClip,
                        TypeName: AssetItem.GetCachedTypeString(ClassIDType.AnimationClip),
                        Relation: relLabel,
                        Detail: string.Join(" • ", detailParts),
                        PathId: c.PathId,
                        SerializedFileName: c.SfName,
                        OriginalPath: c.OrigPath));
                }
            });

            var summary = items.Count == 0
                ? $"No compatible AnimationClips found for '{targetName}'."
                : truncated
                    ? $"Found {items.Count:N0}+ compatible AnimationClips for '{targetName}' (truncated at {limit:N0})."
                    : $"Found {items.Count:N0} compatible AnimationClip(s) for '{targetName}'.";

            return new SemanticGraphUsageResult
            {
                QueryKind = "AnimationRig",
                TargetAssetUniqueId = targetId,
                TargetName = targetName,
                TargetType = targetType,
                Summary = summary,
                IsTruncated = truncated,
                Items = items
            };
        }

        public SemanticGraphUsageResult GetAssetUsageSummary(
            string folderPath,
            string signature,
            string assetUniqueId,
            ClassIDType assetType,
            string? assetName = null,
            int maxResults = DefaultAnalysisGraphMaxResults)
        {
            if (string.IsNullOrWhiteSpace(folderPath)
                || string.IsNullOrWhiteSpace(signature)
                || (string.IsNullOrWhiteSpace(assetUniqueId) && string.IsNullOrWhiteSpace(assetName)))
            {
                return SemanticGraphUsageResult.Empty;
            }

            var limit = ClampAnalysisLimit(maxResults);
            var effectiveId = assetUniqueId?.Trim() ?? string.Empty;
            var effectiveName = string.IsNullOrWhiteSpace(assetName) ? effectiveId : assetName.Trim();

            SemanticGraphUsageResult primaryResult;
            switch (assetType)
            {
                case ClassIDType.Shader:
                {
                    var key = !string.IsNullOrWhiteSpace(effectiveId) ? effectiveId : effectiveName;
                    primaryResult = FindMaterialsUsingShader(folderPath, signature, key, limit);
                    if (primaryResult.Items.Count == 0
                        && !string.IsNullOrWhiteSpace(assetName)
                        && !string.Equals(key, assetName.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        primaryResult = FindMaterialsUsingShader(folderPath, signature, assetName.Trim(), limit);
                    }
                    break;
                }

                case ClassIDType.Texture2D:
                case ClassIDType.Sprite:
                case ClassIDType.Texture2DArray:
                case ClassIDType.Cubemap:
                    primaryResult = FindUsagesOfTexture(folderPath, signature, effectiveId, limit);
                    break;

                case ClassIDType.Material:
                    primaryResult = FindModelsUsingMaterial(folderPath, signature, effectiveId, limit);
                    break;

                case ClassIDType.Avatar:
                case ClassIDType.Animator:
                case ClassIDType.GameObject:
                    primaryResult = FindAnimationsForAvatarOrRig(folderPath, signature, effectiveId, limit);
                    break;

                case ClassIDType.Mesh:
                {
                    var anims = FindAnimationsForAvatarOrRig(folderPath, signature, effectiveId, limit);
                    var fallbackRefs = BuildFallbackUsageFromReferences(folderPath, signature, effectiveId, assetType, effectiveName, limit);
                    primaryResult = MergeUsageResults("Mesh", effectiveId, effectiveName, assetType, fallbackRefs, anims, limit);
                    break;
                }

                default:
                    return BuildFallbackUsageFromReferences(folderPath, signature, effectiveId, assetType, effectiveName, limit);
            }

            if (primaryResult.Items.Count > 0 || string.IsNullOrWhiteSpace(effectiveId))
            {
                return primaryResult;
            }

            var referencesFallback = BuildFallbackUsageFromReferences(folderPath, signature, effectiveId, assetType, effectiveName, limit);
            return referencesFallback.Items.Count > 0 ? referencesFallback : primaryResult;
        }

        public BuildDiffSnapshotLoadResult LoadAssetDiffSnapshots(
            string folderPath,
            string? signature = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                return BuildDiffSnapshotLoadResult.Unavailable("Folder path is empty.");
            }

            var dbPath = GetDbPath(folderPath);
            var idxPath = GetIdxPath(folderPath);

            // Never create an empty SQLite database on disk just to inspect an unindexed folder.
            if (!File.Exists(idxPath) && !File.Exists(dbPath))
            {
                return BuildDiffSnapshotLoadResult.Unavailable(
                    $"Folder '{folderPath}' has no cached asset index yet (showing file-level diff only).");
            }

            // If only a standalone .idx sidecar exists (no SQLite DB), read it directly when signature matches.
            if (!File.Exists(dbPath))
            {
                if (!string.IsNullOrWhiteSpace(signature)
                    && File.Exists(idxPath)
                    && TryReadSnapshotsFromSidecar(idxPath, signature, cancellationToken, out var standaloneSnapshots))
                {
                    return new BuildDiffSnapshotLoadResult
                    {
                        HasSnapshots = true,
                        Source = "MmapSidecar",
                        Signature = signature,
                        DiagnosticNote = $"Loaded {standaloneSnapshots.Count:N0} asset snapshot(s) from .idx sidecar.",
                        Snapshots = standaloneSnapshots
                    };
                }

                return BuildDiffSnapshotLoadResult.Unavailable(
                    $"Sidecar for '{folderPath}' did not match current signature and no SQLite index database exists.");
            }

            // Verify ProjectIndexingState in SQLite before trusting the .idx sidecar (mirrors LoadIndexCacheIntoProjectIndexCore).
            try
            {
                using var lease = RentReadConnection(dbPath);
                var conn = lease.Connection;
                try
                {
                    long? projectId = null;
                    var resolvedSignature = signature ?? string.Empty;
                    var usedLatestFallback = false;

                    if (!string.IsNullOrWhiteSpace(signature))
                    {
                        projectId = FindProjectId(conn, null, folderPath, signature, _caseSensitivePaths);
                    }

                    if (projectId == null)
                    {
                        projectId = FindLatestProjectId(conn, null, folderPath, _caseSensitivePaths);
                        usedLatestFallback = projectId != null;
                    }

                    if (projectId == null)
                    {
                        return BuildDiffSnapshotLoadResult.Unavailable(
                            $"No indexed project record found in cache for '{folderPath}'.");
                    }

                    var status = LoadProjectIndexingStatus(conn, projectId.Value);
                    if (IsIncompleteIndexCacheStatus(status))
                    {
                        return BuildDiffSnapshotLoadResult.Unavailable(
                            $"Cached index for '{folderPath}' is incomplete (status: {status}); open the folder to finish indexing.");
                    }

                    if (usedLatestFallback || string.IsNullOrWhiteSpace(resolvedSignature))
                    {
                        using var sigCmd = conn.CreateCommand();
                        sigCmd.CommandText = "SELECT SignatureHash FROM Projects WHERE Id = @id LIMIT 1;";
                        sigCmd.Parameters.AddWithValue("@id", projectId.Value);
                        resolvedSignature = sigCmd.ExecuteScalar()?.ToString() ?? string.Empty;
                    }

                    // Fast path: verified completed project -> read memory-mapped .idx sidecar first.
                    if (!string.IsNullOrWhiteSpace(resolvedSignature)
                        && File.Exists(idxPath)
                        && TryReadSnapshotsFromSidecar(idxPath, resolvedSignature, cancellationToken, out var sidecarSnapshots))
                    {
                        var sidecarNote = usedLatestFallback && !string.IsNullOrWhiteSpace(signature)
                            ? $"Loaded {sidecarSnapshots.Count:N0} asset snapshot(s) from latest cached .idx sidecar (folder changed since last index)."
                            : $"Loaded {sidecarSnapshots.Count:N0} asset snapshot(s) from .idx sidecar.";
                        return new BuildDiffSnapshotLoadResult
                        {
                            HasSnapshots = true,
                            Source = "MmapSidecar",
                            Signature = resolvedSignature,
                            DiagnosticNote = sidecarNote,
                            Snapshots = sidecarSnapshots
                        };
                    }

                    var sourceOrigins = LoadSourceFileOriginsForProject(conn, projectId.Value);
                    var snapshots = new List<BuildDiffAssetSnapshot>();

                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"
                        SELECT UniqueID, Name, Type, Container, SourceFileId, PathID, ByteStart, ByteSize
                        FROM Assets
                        WHERE ProjectId = @projectId";
                    cmd.Parameters.AddWithValue("@projectId", projectId.Value);

                    long lastSourceId = long.MinValue;
                    var lastOrigin = AssetHandleOrigin.Empty;
                    var rowCounter = 0;

                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        if ((++rowCounter & 0xFFF) == 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                        }

                        var origin = AssetHandleOrigin.Empty;
                        if (!reader.IsDBNull(4))
                        {
                            var srcId = reader.GetInt64(4);
                            if (srcId != lastSourceId)
                            {
                                lastSourceId = srcId;
                                lastOrigin = sourceOrigins.TryGetValue(srcId, out var resolved)
                                    ? resolved
                                    : AssetHandleOrigin.Empty;
                            }

                            origin = lastOrigin;
                        }

                        snapshots.Add(new BuildDiffAssetSnapshot(
                            AssetUniqueId: reader.GetString(0),
                            SerializedFileName: origin.SerializedFileName,
                            OriginalPath: origin.OriginalPath,
                            PathId: reader.GetInt64(5),
                            Name: reader.GetString(1),
                            Type: (ClassIDType)reader.GetInt32(2),
                            Container: reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                            ByteStart: reader.GetInt64(6),
                            ByteSize: reader.GetInt64(7)));
                    }

                    if (snapshots.Count == 0)
                    {
                        return BuildDiffSnapshotLoadResult.Unavailable(
                            $"Cached SQLite index for '{folderPath}' contains 0 assets.");
                    }

                    var note = usedLatestFallback && !string.IsNullOrWhiteSpace(signature)
                        ? $"Loaded {snapshots.Count:N0} asset snapshot(s) from latest cached SQLite snapshot (folder changed since last index)."
                        : $"Loaded {snapshots.Count:N0} asset snapshot(s) from SQLite cache.";

                    return new BuildDiffSnapshotLoadResult
                    {
                        HasSnapshots = true,
                        Source = "SQLiteCache",
                        Signature = resolvedSignature,
                        DiagnosticNote = note,
                        Snapshots = snapshots
                    };
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    lease.MarkUnhealthy();
                    throw;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to load asset diff snapshots for '{folderPath}': {ex.Message}");
                return BuildDiffSnapshotLoadResult.Unavailable(
                    $"Unable to read cached index for '{folderPath}': {ex.Message}");
            }
        }

        private static bool TryReadSnapshotsFromSidecar(
            string idxPath,
            string expectedSignature,
            CancellationToken cancellationToken,
            out List<BuildDiffAssetSnapshot> snapshots)
        {
            snapshots = new List<BuildDiffAssetSnapshot>();
            if (!MmapProjectIndexSidecar.TryOpenSnapshot(idxPath, expectedSignature, SemanticSchemaVersion, out var sidecar)
                || sidecar == null)
            {
                return false;
            }

            try
            {
                using (sidecar)
                {
                    var sourceRecords = sidecar.SourceFiles;
                    var sourceNames = new string[sourceRecords.Length];
                    var sourcePaths = new string[sourceRecords.Length];
                    for (var i = 0; i < sourceRecords.Length; i++)
                    {
                        ref readonly var src = ref sourceRecords[i];
                        sourceNames[i] = sidecar.GetString(src.SerializedFileNameOffset, src.SerializedFileNameLength);
                        sourcePaths[i] = sidecar.GetString(src.OriginalPathOffset, src.OriginalPathLength);
                    }

                    var records = sidecar.Records;
                    snapshots = new List<BuildDiffAssetSnapshot>(records.Length);
                    for (var i = 0; i < records.Length; i++)
                    {
                        if ((i & 0xFFF) == 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                        }

                        ref readonly var rec = ref records[i];
                        var hasSrc = (uint)rec.SourceFileIndex < (uint)sourceNames.Length;
                        snapshots.Add(new BuildDiffAssetSnapshot(
                            AssetUniqueId: sidecar.GetString(rec.UniqueIdOffset, rec.UniqueIdLength),
                            SerializedFileName: hasSrc ? sourceNames[rec.SourceFileIndex] : string.Empty,
                            OriginalPath: hasSrc ? sourcePaths[rec.SourceFileIndex] : string.Empty,
                            PathId: rec.PathID,
                            Name: sidecar.GetString(rec.NameOffset, rec.NameLength),
                            Type: (ClassIDType)rec.ClassID,
                            Container: sidecar.GetString(rec.ContainerOffset, rec.ContainerLength),
                            ByteStart: rec.ByteStart,
                            ByteSize: rec.ByteSize));
                    }

                    return snapshots.Count > 0;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                snapshots = new List<BuildDiffAssetSnapshot>();
                return false;
            }
        }

        private static void CollectDownstreamMeshesAndModelsForMaterials(
            SqliteConnection conn,
            SqliteTransaction transaction,
            long projectId,
            IReadOnlyList<string> materialIds,
            IReadOnlyDictionary<string, string> materialNamesById,
            List<SemanticGraphUsageItem> items,
            HashSet<string> seen,
            int limit,
            ref bool truncated)
        {
            if (materialIds.Count == 0 || items.Count >= limit)
            {
                return;
            }

            var meshIds = new List<string>();
            var seenMeshIds = new HashSet<string>(StringComparer.Ordinal);
            const int chunkSize = 200;

            // A) MeshMaterialResolvedCache (idx_mesh_mat_resolved_material)
            for (var offset = 0; offset < materialIds.Count && items.Count < limit; offset += chunkSize)
            {
                var chunk = materialIds.Skip(offset).Take(chunkSize).ToList();
                using var cmd = conn.CreateCommand();
                cmd.Transaction = transaction;
                var placeholders = AddChunkParameters(cmd, "@m", chunk);
                cmd.CommandText = $@"
                    SELECT
                        mrc.MeshAssetUniqueID,
                        mrc.SubMeshIndex,
                        mrc.MaterialAssetUniqueID,
                        mrc.RendererType,
                        COALESCE(a.Name, ''),
                        COALESCE(a.Type, 43),
                        COALESCE(a.PathID, 0),
                        COALESCE(sf.SerializedFileName, ''),
                        COALESCE(sf.OriginalPath, '')
                    FROM MeshMaterialResolvedCache mrc
                    LEFT JOIN Assets a
                      ON a.ProjectId = mrc.ProjectId
                     AND a.UniqueID = mrc.MeshAssetUniqueID
                    LEFT JOIN SourceFiles sf
                      ON sf.ProjectId = a.ProjectId
                     AND sf.Id = a.SourceFileId
                    WHERE mrc.ProjectId = @projectId
                      AND mrc.MaterialAssetUniqueID IN ({placeholders})
                      AND mrc.MeshAssetUniqueID <> ''
                    ORDER BY COALESCE(a.Name, '') ASC, mrc.MeshAssetUniqueID ASC, mrc.SubMeshIndex ASC
                    LIMIT @limit";
                cmd.Parameters.AddWithValue("@projectId", projectId);
                cmd.Parameters.AddWithValue("@limit", limit + 1);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var meshId = reader.GetString(0);
                    if (string.IsNullOrWhiteSpace(meshId))
                    {
                        continue;
                    }

                    var subMesh = reader.GetInt32(1);
                    var matId = reader.GetString(2);
                    var rendererType = reader.GetString(3);
                    var name = reader.GetString(4);
                    var type = (ClassIDType)reader.GetInt32(5);
                    if (type == ClassIDType.UnknownType) type = ClassIDType.Mesh;
                    var pathId = reader.GetInt64(6);
                    var sfName = reader.GetString(7);
                    var origPath = reader.GetString(8);

                    if (seenMeshIds.Add(meshId))
                    {
                        meshIds.Add(meshId);
                    }

                    if (!seen.Add($"MESH|{meshId}"))
                    {
                        continue;
                    }

                    if (items.Count >= limit)
                    {
                        truncated = true;
                        break;
                    }

                    var viaMat = materialNamesById.TryGetValue(matId, out var mn) && !string.IsNullOrWhiteSpace(mn)
                        ? $" via {mn}"
                        : string.Empty;
                    var rendererLabel = string.IsNullOrWhiteSpace(rendererType) ? string.Empty : $" ({rendererType})";

                    items.Add(new SemanticGraphUsageItem(
                        AssetUniqueId: meshId,
                        Name: string.IsNullOrWhiteSpace(name) ? $"Mesh ({pathId})" : name,
                        AssetType: type,
                        TypeName: AssetItem.GetCachedTypeString(type),
                        Relation: $"Mesh (SubMesh {subMesh})",
                        Detail: $"Resolved material{viaMat}{rendererLabel}",
                        PathId: pathId,
                        SerializedFileName: sfName,
                        OriginalPath: origPath));
                }
            }

            // B) MeshMaterials (catches secondary/non-winner renderers and meshes)
            for (var offset = 0; offset < materialIds.Count && items.Count < limit; offset += chunkSize)
            {
                var chunk = materialIds.Skip(offset).Take(chunkSize).ToList();
                using var cmd = conn.CreateCommand();
                cmd.Transaction = transaction;
                var placeholders = AddChunkParameters(cmd, "@mm", chunk);
                cmd.CommandText = $@"
                    SELECT
                        mm.MeshAssetUniqueID,
                        mm.SubMeshIndex,
                        mm.MaterialAssetUniqueID,
                        mm.RendererType,
                        COALESCE(a.Name, ''),
                        COALESCE(a.Type, 43),
                        COALESCE(a.PathID, 0),
                        COALESCE(sf.SerializedFileName, ''),
                        COALESCE(sf.OriginalPath, '')
                    FROM MeshMaterials mm
                    LEFT JOIN Assets a
                      ON a.ProjectId = mm.ProjectId
                     AND a.UniqueID = mm.MeshAssetUniqueID
                    LEFT JOIN SourceFiles sf
                      ON sf.ProjectId = a.ProjectId
                     AND sf.Id = a.SourceFileId
                    WHERE mm.ProjectId = @projectId
                      AND mm.MaterialAssetUniqueID IN ({placeholders})
                      AND mm.MeshAssetUniqueID <> ''
                    ORDER BY mm.MaterialScore DESC, COALESCE(a.Name, '') ASC, mm.MeshAssetUniqueID ASC
                    LIMIT @limit";
                cmd.Parameters.AddWithValue("@projectId", projectId);
                cmd.Parameters.AddWithValue("@limit", limit + 1);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var meshId = reader.GetString(0);
                    if (string.IsNullOrWhiteSpace(meshId))
                    {
                        continue;
                    }

                    var subMesh = reader.GetInt32(1);
                    var matId = reader.GetString(2);
                    var rendererType = reader.GetString(3);
                    var name = reader.GetString(4);
                    var type = (ClassIDType)reader.GetInt32(5);
                    if (type == ClassIDType.UnknownType) type = ClassIDType.Mesh;
                    var pathId = reader.GetInt64(6);
                    var sfName = reader.GetString(7);
                    var origPath = reader.GetString(8);

                    if (seenMeshIds.Add(meshId))
                    {
                        meshIds.Add(meshId);
                    }

                    if (!seen.Add($"MESH|{meshId}"))
                    {
                        continue;
                    }

                    if (items.Count >= limit)
                    {
                        truncated = true;
                        break;
                    }

                    var viaMat = materialNamesById.TryGetValue(matId, out var mn) && !string.IsNullOrWhiteSpace(mn)
                        ? $" via {mn}"
                        : string.Empty;
                    var rendererLabel = string.IsNullOrWhiteSpace(rendererType) ? string.Empty : $" ({rendererType})";

                    items.Add(new SemanticGraphUsageItem(
                        AssetUniqueId: meshId,
                        Name: string.IsNullOrWhiteSpace(name) ? $"Mesh ({pathId})" : name,
                        AssetType: type,
                        TypeName: AssetItem.GetCachedTypeString(type),
                        Relation: $"Mesh (SubMesh {subMesh})",
                        Detail: $"Renderer material{viaMat}{rendererLabel}",
                        PathId: pathId,
                        SerializedFileName: sfName,
                        OriginalPath: origPath));
                }
            }

            // C) CompositeAssetParts -> CompositeAssets by PrimaryMaterialAssetUniqueID and by downstream MeshAssetUniqueID (idx_composite_parts_mesh)
            for (var offset = 0; offset < materialIds.Count && items.Count < limit; offset += chunkSize)
            {
                var chunk = materialIds.Skip(offset).Take(chunkSize).ToList();
                using var cmd = conn.CreateCommand();
                cmd.Transaction = transaction;
                var placeholders = AddChunkParameters(cmd, "@cm", chunk);
                cmd.CommandText = $@"
                    SELECT DISTINCT
                        ca.CompositeAssetUniqueID,
                        ca.CompositeKind,
                        ca.DisplayName,
                        ca.RootGameObjectAssetUniqueID,
                        ca.AnimatorAssetUniqueID,
                        ca.AvatarAssetUniqueID,
                        ca.SourceFileName,
                        ca.MeshPartCount,
                        COALESCE(ra.PathID, 0),
                        COALESCE(sf.OriginalPath, '')
                    FROM CompositeAssetParts cap
                    INNER JOIN CompositeAssets ca
                      ON ca.ProjectId = cap.ProjectId
                     AND ca.CompositeAssetUniqueID = cap.CompositeAssetUniqueID
                    LEFT JOIN Assets ra
                      ON ra.ProjectId = ca.ProjectId
                     AND ra.UniqueID = COALESCE(NULLIF(ca.RootGameObjectAssetUniqueID, ''), NULLIF(ca.AnimatorAssetUniqueID, ''), NULLIF(ca.AvatarAssetUniqueID, ''), ca.CompositeAssetUniqueID)
                    LEFT JOIN SourceFiles sf
                      ON sf.ProjectId = ra.ProjectId
                     AND sf.Id = ra.SourceFileId
                    WHERE cap.ProjectId = @projectId
                      AND cap.PrimaryMaterialAssetUniqueID IN ({placeholders})
                    ORDER BY ca.DisplayName ASC, ca.CompositeAssetUniqueID ASC
                    LIMIT @limit";
                cmd.Parameters.AddWithValue("@projectId", projectId);
                cmd.Parameters.AddWithValue("@limit", limit + 1);
                ReadCompositeModelRows(cmd, items, seen, limit, ref truncated);
            }

            for (var offset = 0; offset < meshIds.Count && items.Count < limit; offset += chunkSize)
            {
                var chunk = meshIds.Skip(offset).Take(chunkSize).ToList();
                using var cmd = conn.CreateCommand();
                cmd.Transaction = transaction;
                var placeholders = AddChunkParameters(cmd, "@pm", chunk);
                cmd.CommandText = $@"
                    SELECT DISTINCT
                        ca.CompositeAssetUniqueID,
                        ca.CompositeKind,
                        ca.DisplayName,
                        ca.RootGameObjectAssetUniqueID,
                        ca.AnimatorAssetUniqueID,
                        ca.AvatarAssetUniqueID,
                        ca.SourceFileName,
                        ca.MeshPartCount,
                        COALESCE(ra.PathID, 0),
                        COALESCE(sf.OriginalPath, '')
                    FROM CompositeAssetParts cap
                    INNER JOIN CompositeAssets ca
                      ON ca.ProjectId = cap.ProjectId
                     AND ca.CompositeAssetUniqueID = cap.CompositeAssetUniqueID
                    LEFT JOIN Assets ra
                      ON ra.ProjectId = ca.ProjectId
                     AND ra.UniqueID = COALESCE(NULLIF(ca.RootGameObjectAssetUniqueID, ''), NULLIF(ca.AnimatorAssetUniqueID, ''), NULLIF(ca.AvatarAssetUniqueID, ''), ca.CompositeAssetUniqueID)
                    LEFT JOIN SourceFiles sf
                      ON sf.ProjectId = ra.ProjectId
                     AND sf.Id = ra.SourceFileId
                    WHERE cap.ProjectId = @projectId
                      AND cap.MeshAssetUniqueID IN ({placeholders})
                    ORDER BY ca.DisplayName ASC, ca.CompositeAssetUniqueID ASC
                    LIMIT @limit";
                cmd.Parameters.AddWithValue("@projectId", projectId);
                cmd.Parameters.AddWithValue("@limit", limit + 1);
                ReadCompositeModelRows(cmd, items, seen, limit, ref truncated);
            }

            // D) ModelGroupMeshes -> ModelGroups (idx_model_group_meshes_candidate)
            for (var offset = 0; offset < meshIds.Count && items.Count < limit; offset += chunkSize)
            {
                var chunk = meshIds.Skip(offset).Take(chunkSize).ToList();
                using var cmd = conn.CreateCommand();
                cmd.Transaction = transaction;
                var placeholders = AddChunkParameters(cmd, "@mgm", chunk);
                cmd.CommandText = $@"
                    SELECT DISTINCT
                        mg.GroupAssetUniqueID,
                        mg.GroupKind,
                        mg.GroupName,
                        mg.RootGameObjectAssetUniqueID,
                        mg.AnimatorAssetUniqueID,
                        mg.AvatarAssetUniqueID,
                        mg.SourceFileName,
                        COALESCE(ra.PathID, 0),
                        COALESCE(sf.OriginalPath, '')
                    FROM ModelGroupMeshes mgm
                    INNER JOIN ModelGroups mg
                      ON mg.ProjectId = mgm.ProjectId
                     AND mg.GroupAssetUniqueID = mgm.GroupAssetUniqueID
                    LEFT JOIN Assets ra
                      ON ra.ProjectId = mg.ProjectId
                     AND ra.UniqueID = COALESCE(NULLIF(mg.RootGameObjectAssetUniqueID, ''), NULLIF(mg.AnimatorAssetUniqueID, ''), NULLIF(mg.AvatarAssetUniqueID, ''), mg.GroupAssetUniqueID)
                    LEFT JOIN SourceFiles sf
                      ON sf.ProjectId = ra.ProjectId
                     AND sf.Id = ra.SourceFileId
                    WHERE mgm.ProjectId = @projectId
                      AND mgm.MeshAssetUniqueID IN ({placeholders})
                    ORDER BY mg.GroupName ASC, mg.GroupAssetUniqueID ASC
                    LIMIT @limit";
                cmd.Parameters.AddWithValue("@projectId", projectId);
                cmd.Parameters.AddWithValue("@limit", limit + 1);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var groupId = reader.GetString(0);
                    if (string.IsNullOrWhiteSpace(groupId))
                    {
                        continue;
                    }

                    var kind = reader.GetString(1);
                    var groupName = reader.GetString(2);
                    var rootGoId = reader.GetString(3);
                    var animatorId = reader.GetString(4);
                    var avatarId = reader.GetString(5);
                    var sfName = reader.GetString(6);
                    var pathId = reader.GetInt64(7);
                    var origPath = reader.GetString(8);

                    var (navUid, navType) = ResolveModelNavigationTarget(rootGoId, animatorId, avatarId, groupId);
                    if (!seen.Add($"MODEL|{groupId}") || !seen.Add($"MODELNAV|{navUid}"))
                    {
                        continue;
                    }

                    if (items.Count >= limit)
                    {
                        truncated = true;
                        break;
                    }

                    items.Add(new SemanticGraphUsageItem(
                        AssetUniqueId: navUid,
                        Name: string.IsNullOrWhiteSpace(groupName) ? $"ModelGroup ({groupId})" : groupName,
                        AssetType: navType,
                        TypeName: AssetItem.GetCachedTypeString(navType),
                        Relation: string.IsNullOrWhiteSpace(kind) ? "ModelGroup" : $"ModelGroup ({kind})",
                        Detail: $"Group: {groupId}",
                        PathId: pathId,
                        SerializedFileName: sfName,
                        OriginalPath: origPath));
                }
            }
        }

        private static void ReadCompositeModelRows(
            SqliteCommand cmd,
            List<SemanticGraphUsageItem> items,
            HashSet<string> seen,
            int limit,
            ref bool truncated)
        {
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var compId = reader.GetString(0);
                if (string.IsNullOrWhiteSpace(compId))
                {
                    continue;
                }

                var kind = reader.GetString(1);
                var displayName = reader.GetString(2);
                var rootGoId = reader.GetString(3);
                var animatorId = reader.GetString(4);
                var avatarId = reader.GetString(5);
                var sfName = reader.GetString(6);
                var partCount = reader.GetInt32(7);
                var pathId = reader.GetInt64(8);
                var origPath = reader.GetString(9);

                var (navUid, navType) = ResolveModelNavigationTarget(rootGoId, animatorId, avatarId, compId);
                if (!seen.Add($"MODEL|{compId}") || !seen.Add($"MODELNAV|{navUid}"))
                {
                    continue;
                }

                if (items.Count >= limit)
                {
                    truncated = true;
                    break;
                }

                items.Add(new SemanticGraphUsageItem(
                    AssetUniqueId: navUid,
                    Name: string.IsNullOrWhiteSpace(displayName) ? $"Model ({compId})" : displayName,
                    AssetType: navType,
                    TypeName: AssetItem.GetCachedTypeString(navType),
                    Relation: string.IsNullOrWhiteSpace(kind) ? "Model" : $"Model ({kind})",
                    Detail: $"{partCount:N0} mesh part(s)",
                    PathId: pathId,
                    SerializedFileName: sfName,
                    OriginalPath: origPath));
            }
        }

        private SemanticGraphUsageResult BuildFallbackUsageFromReferences(
            string folderPath,
            string signature,
            string assetUniqueId,
            ClassIDType assetType,
            string targetName,
            int limit)
        {
            if (string.IsNullOrWhiteSpace(assetUniqueId) || !File.Exists(GetDbPath(folderPath)))
            {
                return new SemanticGraphUsageResult
                {
                    QueryKind = "References",
                    TargetAssetUniqueId = assetUniqueId,
                    TargetName = targetName,
                    TargetType = assetType,
                    Summary = $"No semantic references found for '{targetName}'."
                };
            }

            var referencedBy = new List<AssetReferenceItem>();
            var references = new List<AssetReferenceItem>();
            LoadAssetReferencesFromSemanticCache(folderPath, signature, assetUniqueId, referencedBy, references, limit);

            var items = new List<SemanticGraphUsageItem>(Math.Min(limit, referencedBy.Count + references.Count));
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var truncated = false;

            void AppendReferenceList(List<AssetReferenceItem> source, string directionPrefix)
            {
                for (var i = 0; i < source.Count; i++)
                {
                    var r = source[i];
                    var key = $"{directionPrefix}|{r.AssetUniqueId}|{r.Relation}";
                    if (!seen.Add(key))
                    {
                        continue;
                    }

                    if (items.Count >= limit)
                    {
                        truncated = true;
                        break;
                    }

                    var typeName = !string.IsNullOrWhiteSpace(r.TypeName)
                        ? r.TypeName
                        : AssetItem.GetCachedTypeString(r.AssetType);
                    items.Add(new SemanticGraphUsageItem(
                        AssetUniqueId: r.AssetUniqueId,
                        Name: r.Name,
                        AssetType: r.AssetType,
                        TypeName: typeName,
                        Relation: $"{directionPrefix}: {r.Relation}",
                        Detail: r.SerializedFileName,
                        PathId: r.PathId,
                        SerializedFileName: r.SerializedFileName,
                        OriginalPath: string.Empty));
                }
            }

            AppendReferenceList(referencedBy, "Used By");
            AppendReferenceList(references, "Uses");

            var summary = items.Count == 0
                ? $"No semantic graph references found for '{targetName}'."
                : $"Found {items.Count:N0} semantic reference(s) for '{targetName}' ({referencedBy.Count:N0} incoming, {references.Count:N0} outgoing).";

            return new SemanticGraphUsageResult
            {
                QueryKind = "References",
                TargetAssetUniqueId = assetUniqueId,
                TargetName = targetName,
                TargetType = assetType,
                Summary = summary,
                IsTruncated = truncated,
                Items = items
            };
        }

        private static SemanticGraphUsageResult MergeUsageResults(
            string queryKind,
            string targetId,
            string targetName,
            ClassIDType targetType,
            SemanticGraphUsageResult first,
            SemanticGraphUsageResult second,
            int limit)
        {
            var merged = new List<SemanticGraphUsageItem>(Math.Min(limit, first.Items.Count + second.Items.Count));
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var truncated = first.IsTruncated || second.IsTruncated;

            void Append(IReadOnlyList<SemanticGraphUsageItem> list)
            {
                for (var i = 0; i < list.Count; i++)
                {
                    var item = list[i];
                    var key = $"{item.AssetUniqueId}|{item.Relation}";
                    if (!seen.Add(key))
                    {
                        continue;
                    }

                    if (merged.Count >= limit)
                    {
                        truncated = true;
                        break;
                    }

                    merged.Add(item);
                }
            }

            Append(first.Items);
            Append(second.Items);

            var summary = merged.Count == 0
                ? $"No usages found for '{targetName}'."
                : $"Found {merged.Count:N0} usage/relation item(s) for '{targetName}'.";

            return new SemanticGraphUsageResult
            {
                QueryKind = queryKind,
                TargetAssetUniqueId = targetId,
                TargetName = targetName,
                TargetType = targetType,
                Summary = summary,
                IsTruncated = truncated,
                Items = merged
            };
        }

        private static void ResolveAssetHeader(
            SqliteConnection conn,
            SqliteTransaction transaction,
            long projectId,
            string assetUniqueId,
            ref string targetName,
            ref ClassIDType targetType)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                SELECT Name, Type
                FROM Assets
                WHERE ProjectId = @projectId
                  AND UniqueID = @uid
                LIMIT 1";
            cmd.Parameters.AddWithValue("@projectId", projectId);
            cmd.Parameters.AddWithValue("@uid", assetUniqueId);
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                var name = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                var type = (ClassIDType)reader.GetInt32(1);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    targetName = name;
                }
                if (type != ClassIDType.UnknownType)
                {
                    targetType = type;
                }
            }
        }

        private static (string AssetUniqueId, ClassIDType AssetType) ResolveModelNavigationTarget(
            string rootGoId,
            string animatorId,
            string avatarId,
            string fallbackGroupId)
        {
            if (!string.IsNullOrWhiteSpace(rootGoId))
            {
                return (rootGoId, ClassIDType.GameObject);
            }
            if (!string.IsNullOrWhiteSpace(animatorId))
            {
                return (animatorId, ClassIDType.Animator);
            }
            if (!string.IsNullOrWhiteSpace(avatarId))
            {
                return (avatarId, ClassIDType.Avatar);
            }

            return (fallbackGroupId, ClassIDType.GameObject);
        }

        private static string BuildTextureSlotDetail(string slotName, bool isMain, string shaderName)
        {
            var parts = new List<string>(2);
            if (!string.IsNullOrWhiteSpace(slotName))
            {
                parts.Add(isMain ? $"Slot: {slotName} (Main)" : $"Slot: {slotName}");
            }
            else if (isMain)
            {
                parts.Add("Main Texture");
            }

            if (!string.IsNullOrWhiteSpace(shaderName))
            {
                parts.Add($"Shader: {shaderName}");
            }

            return string.Join(" • ", parts);
        }

        private static string AddChunkParameters(SqliteCommand cmd, string prefix, IReadOnlyList<string> values)
        {
            var placeholders = new string[values.Count];
            for (var i = 0; i < values.Count; i++)
            {
                var p = prefix + i.ToString(CultureInfo.InvariantCulture);
                placeholders[i] = p;
                cmd.Parameters.AddWithValue(p, values[i]);
            }

            return string.Join(",", placeholders);
        }

        private static void AddNonEmpty(HashSet<string> set, string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                set.Add(value);
            }
        }

        private static string EscapeLikePattern(string value)
        {
            return value
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("%", "\\%", StringComparison.Ordinal)
                .Replace("_", "\\_", StringComparison.Ordinal);
        }

        private static int ClampAnalysisLimit(int maxResults)
        {
            if (maxResults <= 0)
            {
                return DefaultAnalysisGraphMaxResults;
            }

            return Math.Min(maxResults, HardAnalysisGraphMaxResults);
        }
    }
}
