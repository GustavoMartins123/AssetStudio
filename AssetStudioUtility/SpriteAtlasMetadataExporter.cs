using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace AssetStudio
{
    public sealed record RectMetadata(float X, float Y, float Width, float Height);

    public sealed record Vector2Metadata(float X, float Y);

    public sealed record SpriteBorderMetadata(float Left, float Bottom, float Right, float Top);

    public sealed record SecondaryTextureMetadata(string Name, string? TextureName, long TexturePathId);

    public sealed class SpriteMetadataRecord
    {
        public string Name { get; set; } = string.Empty;
        public long PathId { get; set; }
        public string SourceFile { get; set; } = string.Empty;
        public RectMetadata Rect { get; set; } = new(0f, 0f, 0f, 0f);
        public RectMetadata TextureRect { get; set; } = new(0f, 0f, 0f, 0f);
        public Vector2Metadata TextureRectOffset { get; set; } = new(0f, 0f);
        public Vector2Metadata AtlasRectOffset { get; set; } = new(0f, 0f);
        public SpriteBorderMetadata Border { get; set; } = new(0f, 0f, 0f, 0f);
        public Vector2Metadata Pivot { get; set; } = new(0.5f, 0.5f);
        public float PixelsPerUnit { get; set; }
        public uint Extrude { get; set; }
        public bool Packed { get; set; }
        public string PackingMode { get; set; } = string.Empty;
        public string PackingRotation { get; set; } = string.Empty;
        public string MeshType { get; set; } = string.Empty;
        public float DownscaleMultiplier { get; set; }
        public bool HasAlphaTexture { get; set; }
        public IReadOnlyList<SecondaryTextureMetadata> SecondaryTextures { get; set; } = Array.Empty<SecondaryTextureMetadata>();
        public string? AtlasName { get; set; }
        public IReadOnlyList<string> AtlasTags { get; set; } = Array.Empty<string>();
        public int PhysicsShapeCount { get; set; }
        public int BoneCount { get; set; }
        public string? ExportedImageFile { get; set; }
    }

    public sealed class SpriteAtlasManifest
    {
        public string AtlasName { get; set; } = string.Empty;
        public long PathId { get; set; }
        public string SourceFile { get; set; } = string.Empty;
        public bool IsVariant { get; set; }
        public int PackedSpriteCount { get; set; }
        public int RenderDataCount { get; set; }
        public List<SpriteMetadataRecord> Sprites { get; set; } = new();
    }

    public sealed class SpriteAtlasExportOptions
    {
        public ImageFormat ImageFormat { get; set; } = ImageFormat.Png;
        public bool PreserveOriginalRect { get; set; }
        public bool ApplyAlphaMask { get; set; } = true;
        public bool DrawBoundsAndNineSliceOverlay { get; set; }
        public bool ExportImages { get; set; } = true;
        public bool ExportManifestJson { get; set; } = true;
        public int WebpQuality { get; set; } = 90;
        public bool WebpLossless { get; set; }
    }

    public sealed class SpriteAtlasExportResult
    {
        public string AtlasName { get; init; } = string.Empty;
        public string OutputDirectory { get; init; } = string.Empty;
        public string? ManifestPath { get; init; }
        public int TotalSprites { get; init; }
        public int ExportedImages { get; init; }
        public int FailedImages { get; init; }
        public IReadOnlyList<string> ExportedFilePaths { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
        public SpriteAtlasManifest Manifest { get; init; } = new();
    }

    public static class SpriteAtlasMetadataExporter
    {
        private static readonly JsonSerializerOptions IndentedJsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private static readonly JsonSerializerOptions CompactJsonOptions = new()
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public static SpriteMetadataRecord ExtractSpriteMetadata(Sprite sprite)
        {
            ArgumentNullException.ThrowIfNull(sprite);
            SpriteAtlas? resolvedAtlas = null;
            sprite.m_SpriteAtlas?.TryGet(out resolvedAtlas);
            return ExtractSpriteMetadataCore(sprite, resolvedAtlas);
        }

        private static SpriteMetadataRecord ExtractSpriteMetadataCore(Sprite sprite, SpriteAtlas? fallbackAtlas)
        {
            SpriteAtlas? atlas = null;
            if (sprite.m_SpriteAtlas != null)
            {
                sprite.m_SpriteAtlas.TryGet(out atlas);
            }
            atlas ??= fallbackAtlas;

            SpriteAtlasData? atlasData = null;
            if (atlas?.m_RenderDataMap != null)
            {
                atlas.m_RenderDataMap.TryGetValue(sprite.m_RenderDataKey, out atlasData);
            }

            Rectf? texRect = atlasData?.textureRect ?? sprite.m_RD?.textureRect;
            Vector2 texRectOffset = atlasData != null ? atlasData.textureRectOffset : (sprite.m_RD?.textureRectOffset ?? Vector2.Zero);
            Vector2 atlasRectOffset = atlasData != null ? atlasData.atlasRectOffset : (sprite.m_RD?.atlasRectOffset ?? Vector2.Zero);
            float downscaleMultiplier = atlasData != null ? atlasData.downscaleMultiplier : (sprite.m_RD?.downscaleMultiplier ?? 1f);
            SpriteSettings? settings = atlasData?.settingsRaw ?? sprite.m_RD?.settingsRaw;

            PPtr<Texture2D>? directAlphaPtr = atlasData?.alphaTexture ?? sprite.m_RD?.alphaTexture;
            SecondarySpriteTexture[]? primarySecondary = atlasData?.secondaryTextures;
            SecondarySpriteTexture[]? rdSecondary = sprite.m_RD?.secondaryTextures;
            SecondarySpriteTexture[]? effectiveSecondary = (primarySecondary != null && primarySecondary.Length > 0)
                ? primarySecondary
                : rdSecondary;

            bool hasAlphaTexture = HasAlphaMaskReference(directAlphaPtr, primarySecondary, rdSecondary);

            var secondaryList = new List<SecondaryTextureMetadata>();
            if (effectiveSecondary != null)
            {
                for (int i = 0; i < effectiveSecondary.Length; i++)
                {
                    var sec = effectiveSecondary[i];
                    if (sec == null)
                    {
                        continue;
                    }

                    string? secTexName = null;
                    long secPathId = 0;
                    if (sec.texture != null)
                    {
                        secPathId = sec.texture.m_PathID;
                        if (sec.texture.TryGet(out var secTex) && secTex != null)
                        {
                            secTexName = secTex.m_Name;
                        }
                    }

                    secondaryList.Add(new SecondaryTextureMetadata(
                        Name: sec.name ?? string.Empty,
                        TextureName: secTexName,
                        TexturePathId: secPathId));
                }
            }

            string[] atlasTags = sprite.m_AtlasTags != null && sprite.m_AtlasTags.Length > 0
                ? (string[])sprite.m_AtlasTags.Clone()
                : Array.Empty<string>();

            return new SpriteMetadataRecord
            {
                Name = sprite.m_Name ?? string.Empty,
                PathId = sprite.m_PathID,
                SourceFile = sprite.assetsFile?.fileName ?? string.Empty,
                Rect = ToRectMetadata(sprite.m_Rect),
                TextureRect = ToRectMetadata(texRect),
                TextureRectOffset = ToVector2Metadata(texRectOffset),
                AtlasRectOffset = ToVector2Metadata(atlasRectOffset),
                Border = new SpriteBorderMetadata(
                    Left: SafeFloat(sprite.m_Border.X),
                    Bottom: SafeFloat(sprite.m_Border.Y),
                    Right: SafeFloat(sprite.m_Border.Z),
                    Top: SafeFloat(sprite.m_Border.W)),
                Pivot = ToVector2Metadata(sprite.m_Pivot),
                PixelsPerUnit = SafeFloat(sprite.m_PixelsToUnits),
                Extrude = sprite.m_Extrude,
                Packed = settings != null && settings.packed != 0,
                PackingMode = settings != null ? settings.packingMode.ToString() : SpritePackingMode.Rectangle.ToString(),
                PackingRotation = settings != null ? settings.packingRotation.ToString() : SpritePackingRotation.None.ToString(),
                MeshType = settings != null ? settings.meshType.ToString() : SpriteMeshType.FullRect.ToString(),
                DownscaleMultiplier = SafeFloat(downscaleMultiplier),
                HasAlphaTexture = hasAlphaTexture,
                SecondaryTextures = secondaryList,
                AtlasName = atlas?.m_Name,
                AtlasTags = atlasTags,
                PhysicsShapeCount = sprite.m_PhysicsShape?.Length ?? 0,
                BoneCount = sprite.m_Bones?.Length ?? 0
            };
        }

        public static SpriteAtlasManifest ExtractSpriteAtlasManifest(
            SpriteAtlas atlas,
            IReadOnlyList<Sprite>? resolvedSprites = null)
        {
            ArgumentNullException.ThrowIfNull(atlas);

            var sprites = CollectSprites(atlas, resolvedSprites);
            var records = new List<SpriteMetadataRecord>(sprites.Count);
            for (int i = 0; i < sprites.Count; i++)
            {
                records.Add(ExtractSpriteMetadataCore(sprites[i], atlas));
            }

            return new SpriteAtlasManifest
            {
                AtlasName = atlas.m_Name ?? string.Empty,
                PathId = atlas.m_PathID,
                SourceFile = atlas.assetsFile?.fileName ?? string.Empty,
                IsVariant = atlas.m_IsVariant,
                PackedSpriteCount = atlas.m_PackedSprites?.Length ?? sprites.Count,
                RenderDataCount = atlas.m_RenderDataMap?.Count ?? 0,
                Sprites = records
            };
        }

        public static string SerializeManifestJson(SpriteAtlasManifest manifest, bool indented = true)
        {
            ArgumentNullException.ThrowIfNull(manifest);
            return JsonSerializer.Serialize(manifest, indented ? IndentedJsonOptions : CompactJsonOptions);
        }

        public static SpriteAtlasExportResult ExportSpriteAtlasBatch(
            SpriteAtlas atlas,
            string outputDirectory,
            SpriteAtlasExportOptions? options = null,
            IReadOnlyList<Sprite>? resolvedSprites = null,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(atlas);
            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                throw new ArgumentException("Output directory cannot be empty.", nameof(outputDirectory));
            }

            options ??= new SpriteAtlasExportOptions();
            string fullOutputDir = Path.GetFullPath(outputDirectory);
            Directory.CreateDirectory(fullOutputDir);

            string rawAtlasName = string.IsNullOrWhiteSpace(atlas.m_Name)
                ? $"SpriteAtlas_{atlas.m_PathID}"
                : atlas.m_Name;
            string safeAtlasName = TextureMipmapService.SanitizeFileNameStrict(rawAtlasName, $"SpriteAtlas_{atlas.m_PathID}");

            var sprites = CollectSprites(atlas, resolvedSprites);
            var manifest = new SpriteAtlasManifest
            {
                AtlasName = atlas.m_Name ?? string.Empty,
                PathId = atlas.m_PathID,
                SourceFile = atlas.assetsFile?.fileName ?? string.Empty,
                IsVariant = atlas.m_IsVariant,
                PackedSpriteCount = atlas.m_PackedSprites?.Length ?? sprites.Count,
                RenderDataCount = atlas.m_RenderDataMap?.Count ?? 0,
                Sprites = new List<SpriteMetadataRecord>(sprites.Count)
            };

            var extractionOptions = new SpriteExtractionOptions
            {
                PreserveOriginalRect = options.PreserveOriginalRect,
                ApplyAlphaMask = options.ApplyAlphaMask,
                DrawBoundsAndNineSliceOverlay = options.DrawBoundsAndNineSliceOverlay
            };

            string imageExt = options.ImageFormat.GetFileExtension();
            var usedFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var exportedFilePaths = new List<string>(sprites.Count);
            var warnings = new List<string>();
            int exportedImages = 0;
            int failedImages = 0;

            for (int i = 0; i < sprites.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var sprite = sprites[i];
                var record = ExtractSpriteMetadataCore(sprite, atlas);

                if (options.ExportImages)
                {
                    try
                    {
                        using var bitmap = sprite.GetImage(extractionOptions);
                        if (bitmap != null)
                        {
                            string baseName = TextureMipmapService.SanitizeFileNameStrict(
                                sprite.m_Name,
                                $"Sprite_{sprite.m_PathID}_{i}");
                            string fileName = AllocateUniqueFileName(baseName, imageExt, sprite.m_PathID, usedFileNames);
                            string targetPath = TextureMipmapService.CombineConfinedPath(fullOutputDir, fileName);

                            using (var fs = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None))
                            {
                                bitmap.WriteToStream(fs, options.ImageFormat, options.WebpQuality, options.WebpLossless);
                            }

                            record.ExportedImageFile = Path.GetFileName(targetPath);
                            exportedFilePaths.Add(targetPath);
                            exportedImages++;
                        }
                        else
                        {
                            failedImages++;
                            warnings.Add($"Failed to decode sprite '{sprite.m_Name}' (PathID {sprite.m_PathID}).");
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        failedImages++;
                        warnings.Add($"Error exporting sprite '{sprite.m_Name}' (PathID {sprite.m_PathID}): {ex.Message}");
                    }
                }

                manifest.Sprites.Add(record);
            }

            string? manifestPath = null;
            if (options.ExportManifestJson)
            {
                string manifestFileName = TextureMipmapService.SanitizeFileNameStrict(
                    $"{safeAtlasName}_manifest.json",
                    "sprite_atlas_manifest.json");
                manifestPath = TextureMipmapService.CombineConfinedPath(fullOutputDir, manifestFileName);
                string json = SerializeManifestJson(manifest, indented: true);
                File.WriteAllText(manifestPath, json, Encoding.UTF8);
            }

            return new SpriteAtlasExportResult
            {
                AtlasName = rawAtlasName,
                OutputDirectory = fullOutputDir,
                ManifestPath = manifestPath,
                TotalSprites = sprites.Count,
                ExportedImages = exportedImages,
                FailedImages = failedImages,
                ExportedFilePaths = exportedFilePaths,
                Warnings = warnings,
                Manifest = manifest
            };
        }

        private static List<Sprite> CollectSprites(SpriteAtlas atlas, IReadOnlyList<Sprite>? resolvedSprites)
        {
            var result = new List<Sprite>();
            var seen = new HashSet<Sprite>(ReferenceEqualityComparer.Instance);

            if (resolvedSprites != null)
            {
                for (int i = 0; i < resolvedSprites.Count; i++)
                {
                    var sprite = resolvedSprites[i];
                    if (sprite != null && seen.Add(sprite))
                    {
                        result.Add(sprite);
                    }
                }
                return result;
            }

            if (atlas.m_PackedSprites != null)
            {
                for (int i = 0; i < atlas.m_PackedSprites.Length; i++)
                {
                    var pptr = atlas.m_PackedSprites[i];
                    if (pptr != null && pptr.TryGet(out var sprite) && sprite != null && seen.Add(sprite))
                    {
                        result.Add(sprite);
                    }
                }
            }

            return result;
        }

        private static string AllocateUniqueFileName(
            string safeBaseName,
            string extension,
            long pathId,
            HashSet<string> usedFileNames)
        {
            string candidate = safeBaseName + extension;
            if (usedFileNames.Add(candidate))
            {
                return candidate;
            }

            candidate = $"{safeBaseName}_{pathId}{extension}";
            if (usedFileNames.Add(candidate))
            {
                return candidate;
            }

            int suffix = 2;
            while (true)
            {
                candidate = $"{safeBaseName}_{pathId}_{suffix}{extension}";
                if (usedFileNames.Add(candidate))
                {
                    return candidate;
                }
                suffix++;
            }
        }

        private static bool HasAlphaMaskReference(
            PPtr<Texture2D>? directAlphaPtr,
            SecondarySpriteTexture[]? primarySecondary,
            SecondarySpriteTexture[]? fallbackSecondary)
        {
            if (directAlphaPtr != null && !directAlphaPtr.IsNull)
            {
                return true;
            }

            return HasSecondaryAlphaEntry(primarySecondary) || HasSecondaryAlphaEntry(fallbackSecondary);
        }

        private static bool HasSecondaryAlphaEntry(SecondarySpriteTexture[]? secondaryTextures)
        {
            if (secondaryTextures == null)
            {
                return false;
            }

            for (int i = 0; i < secondaryTextures.Length; i++)
            {
                var entry = secondaryTextures[i];
                if (entry != null
                    && SpriteHelper.IsSecondaryAlphaTextureName(entry.name)
                    && entry.texture != null
                    && !entry.texture.IsNull)
                {
                    return true;
                }
            }

            return false;
        }

        private static RectMetadata ToRectMetadata(Rectf? rect)
        {
            if (rect == null)
            {
                return new RectMetadata(0f, 0f, 0f, 0f);
            }

            return new RectMetadata(
                SafeFloat(rect.x),
                SafeFloat(rect.y),
                SafeFloat(rect.width),
                SafeFloat(rect.height));
        }

        private static Vector2Metadata ToVector2Metadata(Vector2 value)
        {
            return new Vector2Metadata(SafeFloat(value.X), SafeFloat(value.Y));
        }

        private static float SafeFloat(float value)
        {
            return float.IsFinite(value) ? value : 0f;
        }
    }
}
