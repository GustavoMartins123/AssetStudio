using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Text;
using AssetStudio;

namespace AssetStudio.Avalonia.Services
{
    public readonly record struct TmpPPtrRef(int FileId, long PathId)
    {
        public bool IsValid => PathId != 0;
    }

    public sealed class TmpFontAssetInfo
    {
        public string AssetName { get; init; } = string.Empty;
        public string FamilyName { get; init; } = string.Empty;
        public string StyleName { get; init; } = string.Empty;
        public float PointSize { get; init; }
        public float LineHeight { get; init; }
        public float AscentLine { get; init; }
        public float DescentLine { get; init; }
        public int AtlasWidth { get; init; }
        public int AtlasHeight { get; init; }
        public int AtlasPadding { get; init; }
        public string AtlasRenderMode { get; init; } = string.Empty;
        public string AtlasPopulationMode { get; init; } = string.Empty;
        public int GlyphCount { get; init; }
        public int CharacterCount { get; init; }
        public int AtlasPageCount { get; init; }
        public IReadOnlyList<TmpPPtrRef> AtlasTextureRefs { get; init; } = Array.Empty<TmpPPtrRef>();
        public TmpPPtrRef? MaterialRef { get; init; }
        public IReadOnlyList<TmpPPtrRef> FallbackFontRefs { get; init; } = Array.Empty<TmpPPtrRef>();

        public string DisplayName => !string.IsNullOrWhiteSpace(FamilyName)
            ? (!string.IsNullOrWhiteSpace(StyleName) ? $"{FamilyName} ({StyleName})" : FamilyName)
            : (!string.IsNullOrWhiteSpace(AssetName) ? AssetName : "TMP_FontAsset");

        public string FormatInfoOverlay(
            Texture2D? resolvedAtlas = null,
            IReadOnlyList<string>? fallbackFontNames = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Type: TextMeshPro Font (TMP_FontAsset)");
            if (!string.IsNullOrWhiteSpace(AssetName))
            {
                sb.AppendLine($"Asset: {AssetName}");
            }

            if (!string.IsNullOrWhiteSpace(FamilyName))
            {
                sb.AppendLine($"Family: {FamilyName}");
            }

            if (!string.IsNullOrWhiteSpace(StyleName))
            {
                sb.AppendLine($"Style: {StyleName}");
            }

            if (PointSize > 0f)
            {
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"Point Size: {PointSize:0.##}"));
            }

            if (LineHeight > 0f)
            {
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"Line Height: {LineHeight:0.##}"));
            }

            var effectiveWidth = resolvedAtlas != null && resolvedAtlas.m_Width > 0 ? resolvedAtlas.m_Width : AtlasWidth;
            var effectiveHeight = resolvedAtlas != null && resolvedAtlas.m_Height > 0 ? resolvedAtlas.m_Height : AtlasHeight;
            if (effectiveWidth > 0 && effectiveHeight > 0)
            {
                var pagesText = AtlasPageCount > 1 ? $" ({AtlasPageCount} pages)" : string.Empty;
                sb.AppendLine($"Atlas Size: {effectiveWidth}x{effectiveHeight}{pagesText}");
            }

            if (resolvedAtlas != null)
            {
                if (!string.IsNullOrWhiteSpace(resolvedAtlas.m_Name))
                {
                    sb.AppendLine($"Atlas Texture: {resolvedAtlas.m_Name}");
                }
                sb.AppendLine($"Atlas Format: {resolvedAtlas.m_TextureFormat}");
            }

            if (AtlasPadding > 0)
            {
                sb.AppendLine($"Atlas Padding: {AtlasPadding}px");
            }

            if (!string.IsNullOrWhiteSpace(AtlasRenderMode))
            {
                sb.AppendLine($"Sampling Mode: {AtlasRenderMode}");
            }

            if (!string.IsNullOrWhiteSpace(AtlasPopulationMode))
            {
                sb.AppendLine($"Population Mode: {AtlasPopulationMode}");
            }

            if (GlyphCount > 0 || CharacterCount > 0)
            {
                sb.AppendLine($"Glyphs: {GlyphCount:N0} | Characters: {CharacterCount:N0}");
            }

            if (FallbackFontRefs.Count > 0)
            {
                if (fallbackFontNames != null && fallbackFontNames.Count > 0)
                {
                    var previewList = string.Join(", ", fallbackFontNames);
                    sb.AppendLine($"Fallback Fonts ({FallbackFontRefs.Count}): {previewList}");
                }
                else
                {
                    sb.AppendLine($"Fallback Fonts: {FallbackFontRefs.Count}");
                }
            }

            sb.Append("Full serialized data available in Dump tab.");
            return sb.ToString();
        }
    }

    public static class TmpFontPreviewService
    {
        public static bool IsTmpFontScriptName(string? scriptClassName)
        {
            if (string.IsNullOrWhiteSpace(scriptClassName))
            {
                return false;
            }

            var trimmed = scriptClassName.Trim();
            return string.Equals(trimmed, "TMP_FontAsset", StringComparison.OrdinalIgnoreCase)
                || trimmed.EndsWith(".TMP_FontAsset", StringComparison.OrdinalIgnoreCase)
                || string.Equals(trimmed, "TextMeshProFont", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsTmpFontAssetFields(IDictionary? fields)
        {
            if (fields == null || fields.Count == 0)
            {
                return false;
            }

            var hasFaceInfo = HasKey(fields, "m_FaceInfo") || HasKey(fields, "m_fontInfo");
            if (!hasFaceInfo)
            {
                return false;
            }

            return HasKey(fields, "atlas")
                || HasKey(fields, "m_AtlasTextures")
                || HasKey(fields, "m_AtlasTexture")
                || HasKey(fields, "m_GlyphTable")
                || HasKey(fields, "m_CharacterTable")
                || HasKey(fields, "m_glyphInfoList");
        }

        public static bool TryExtract(
            MonoBehaviour? monoBehaviour,
            IDictionary? fields,
            out TmpFontAssetInfo? info,
            string? scriptClassNameOverride = null)
        {
            info = null;
            var scriptClassName = scriptClassNameOverride;
            if (string.IsNullOrWhiteSpace(scriptClassName)
                && monoBehaviour?.m_Script != null
                && !monoBehaviour.m_Script.IsNull
                && monoBehaviour.m_Script.TryGet(out var script)
                && script != null)
            {
                scriptClassName = script.m_ClassName;
            }

            var matchedByScript = IsTmpFontScriptName(scriptClassName);
            var matchedByFields = IsTmpFontAssetFields(fields);
            if (!matchedByScript && !matchedByFields)
            {
                return false;
            }

            var assetName = ReadString(fields, "m_Name");
            if (string.IsNullOrWhiteSpace(assetName))
            {
                assetName = monoBehaviour?.m_Name ?? string.Empty;
            }

            var familyName = string.Empty;
            var styleName = string.Empty;
            var pointSize = 0f;
            var lineHeight = 0f;
            var ascentLine = 0f;
            var descentLine = 0f;
            var atlasWidth = ReadInt(fields, "m_AtlasWidth");
            var atlasHeight = ReadInt(fields, "m_AtlasHeight");
            var atlasPadding = ReadInt(fields, "m_AtlasPadding");
            var glyphCount = 0;
            var characterCount = 0;

            if (TryGetDictionary(fields, "m_FaceInfo", out var faceInfo))
            {
                familyName = ReadString(faceInfo, "m_FamilyName");
                styleName = ReadString(faceInfo, "m_StyleName");
                pointSize = ReadFloat(faceInfo, "m_PointSize");
                lineHeight = ReadFloat(faceInfo, "m_LineHeight");
                ascentLine = ReadFloat(faceInfo, "m_AscentLine");
                descentLine = ReadFloat(faceInfo, "m_DescentLine");
            }
            else if (TryGetDictionary(fields, "m_fontInfo", out var legacyFontInfo))
            {
                familyName = ReadString(legacyFontInfo, "Name");
                pointSize = ReadFloat(legacyFontInfo, "PointSize");
                lineHeight = ReadFloat(legacyFontInfo, "LineHeight");
                ascentLine = ReadFloat(legacyFontInfo, "Ascender");
                descentLine = ReadFloat(legacyFontInfo, "Descender");
                if (atlasWidth <= 0) atlasWidth = (int)MathF.Round(ReadFloat(legacyFontInfo, "AtlasWidth"));
                if (atlasHeight <= 0) atlasHeight = (int)MathF.Round(ReadFloat(legacyFontInfo, "AtlasHeight"));
                if (atlasPadding <= 0) atlasPadding = (int)MathF.Round(ReadFloat(legacyFontInfo, "Padding"));
                characterCount = ReadInt(legacyFontInfo, "CharacterCount");
            }

            var glyphTableCount = GetCollectionCount(fields, "m_GlyphTable");
            var legacyGlyphCount = GetCollectionCount(fields, "m_glyphInfoList");
            glyphCount = glyphTableCount > 0 ? glyphTableCount : legacyGlyphCount;

            var charTableCount = GetCollectionCount(fields, "m_CharacterTable");
            if (charTableCount > 0)
            {
                characterCount = charTableCount;
            }
            else if (characterCount <= 0 && glyphCount > 0)
            {
                characterCount = glyphCount;
            }

            var renderModeRaw = ReadNullableInt(fields, "m_AtlasRenderMode");
            var atlasRenderMode = renderModeRaw.HasValue
                ? FormatGlyphRenderMode(renderModeRaw.Value)
                : FormatLegacyFontCreationMode(ReadNullableInt(fields, "m_CreationSettings"));

            var popModeRaw = ReadNullableInt(fields, "m_AtlasPopulationMode");
            var atlasPopulationMode = popModeRaw.HasValue
                ? FormatAtlasPopulationMode(popModeRaw.Value)
                : string.Empty;

            var atlasRefs = new List<TmpPPtrRef>();
            var seenAtlas = new HashSet<TmpPPtrRef>();

            void AddAtlasRef(TmpPPtrRef ptr)
            {
                if (ptr.IsValid && seenAtlas.Add(ptr))
                {
                    atlasRefs.Add(ptr);
                }
            }

            if (TryReadPPtr(fields, "atlas", out var directAtlas))
            {
                AddAtlasRef(directAtlas);
            }

            if (TryReadPPtr(fields, "m_AtlasTexture", out var singleAtlas))
            {
                AddAtlasRef(singleAtlas);
            }

            var atlasArrayRefs = ReadPPtrList(fields, "m_AtlasTextures");
            for (var i = 0; i < atlasArrayRefs.Count; i++)
            {
                AddAtlasRef(atlasArrayRefs[i]);
            }

            var atlasPageCount = Math.Max(atlasArrayRefs.Count, atlasRefs.Count);

            TmpPPtrRef? materialRef = null;
            if (TryReadPPtr(fields, "material", out var matPtr) && matPtr.IsValid)
            {
                materialRef = matPtr;
            }
            else if (TryReadPPtr(fields, "m_Material", out var altMatPtr) && altMatPtr.IsValid)
            {
                materialRef = altMatPtr;
            }

            var fallbackRefs = ReadPPtrList(fields, "m_FallbackFontAssetTable");
            if (fallbackRefs.Count == 0)
            {
                fallbackRefs = ReadPPtrList(fields, "fallbackFontAssets");
            }

            info = new TmpFontAssetInfo
            {
                AssetName = assetName,
                FamilyName = familyName,
                StyleName = styleName,
                PointSize = pointSize,
                LineHeight = lineHeight,
                AscentLine = ascentLine,
                DescentLine = descentLine,
                AtlasWidth = atlasWidth,
                AtlasHeight = atlasHeight,
                AtlasPadding = atlasPadding,
                AtlasRenderMode = atlasRenderMode,
                AtlasPopulationMode = atlasPopulationMode,
                GlyphCount = glyphCount,
                CharacterCount = characterCount,
                AtlasPageCount = atlasPageCount,
                AtlasTextureRefs = atlasRefs,
                MaterialRef = materialRef,
                FallbackFontRefs = fallbackRefs
            };
            return true;
        }

        public static string FormatGlyphRenderMode(int mode)
        {
            return mode switch
            {
                4116 => "RASTER",
                4117 => "SMOOTH",
                4122 => "RASTER_HINTED",
                4125 => "SMOOTH_HINTED",
                4133 => "SDF16",
                4137 => "SDF32",
                4161 => "SDF",
                4162 => "SDF8",
                4163 => "SDF16",
                4164 => "SDF32",
                4165 => "SDFAA",
                4169 => "SDFAA_HINTED",
                4170 => "COLOR",
                4172 => "COLOR_HINTED",
                _ => mode > 0 ? $"Mode {mode}" : string.Empty
            };
        }

        public static string FormatAtlasPopulationMode(int mode)
        {
            return mode switch
            {
                0 => "Static",
                1 => "Dynamic",
                2 => "DynamicOS",
                _ => $"Mode {mode}"
            };
        }

        private static string FormatLegacyFontCreationMode(int? _)
        {
            return string.Empty;
        }

        private static bool HasKey(IDictionary? dict, string key)
        {
            return TryGetValueCaseInsensitive(dict, key, out _);
        }

        private static bool TryGetValueCaseInsensitive(IDictionary? dict, string key, out object? value)
        {
            value = null;
            if (dict == null || string.IsNullOrEmpty(key))
            {
                return false;
            }

            if (dict.Contains(key))
            {
                value = dict[key];
                return true;
            }

            foreach (DictionaryEntry entry in dict)
            {
                if (entry.Key is string k && string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
                {
                    value = entry.Value;
                    return true;
                }
            }

            return false;
        }

        private static bool TryGetDictionary(IDictionary? dict, string key, out IDictionary? nested)
        {
            nested = null;
            if (TryGetValueCaseInsensitive(dict, key, out var raw) && raw is IDictionary d)
            {
                nested = d;
                return true;
            }

            return false;
        }

        private static string ReadString(IDictionary? dict, string key)
        {
            return TryGetValueCaseInsensitive(dict, key, out var raw) && raw is string s
                ? s.Trim()
                : string.Empty;
        }

        private static int ReadInt(IDictionary? dict, string key)
        {
            return ReadNullableInt(dict, key) ?? 0;
        }

        private static int? ReadNullableInt(IDictionary? dict, string key)
        {
            if (!TryGetValueCaseInsensitive(dict, key, out var raw) || raw == null)
            {
                return null;
            }

            return raw switch
            {
                int i => i,
                uint ui => unchecked((int)ui),
                short s => s,
                ushort us => us,
                byte b => b,
                sbyte sb => sb,
                long l => (int)Math.Clamp(l, int.MinValue, int.MaxValue),
                float f when float.IsFinite(f) => (int)MathF.Round(f),
                double d when double.IsFinite(d) => (int)Math.Round(d),
                _ => null
            };
        }

        private static long ReadLong(IDictionary? dict, string key)
        {
            if (!TryGetValueCaseInsensitive(dict, key, out var raw) || raw == null)
            {
                return 0;
            }

            return raw switch
            {
                long l => l,
                int i => i,
                uint ui => ui,
                ulong ul => unchecked((long)ul),
                short s => s,
                ushort us => us,
                _ => 0
            };
        }

        private static float ReadFloat(IDictionary? dict, string key)
        {
            if (!TryGetValueCaseInsensitive(dict, key, out var raw) || raw == null)
            {
                return 0f;
            }

            return raw switch
            {
                float f when float.IsFinite(f) => f,
                double d when double.IsFinite(d) => (float)d,
                int i => i,
                uint ui => ui,
                long l => l,
                _ => 0f
            };
        }

        private static int GetCollectionCount(IDictionary? dict, string key)
        {
            if (!TryGetValueCaseInsensitive(dict, key, out var raw) || raw == null)
            {
                return 0;
            }

            if (raw is ICollection col)
            {
                return col.Count;
            }

            if (raw is IDictionary nested && TryGetValueCaseInsensitive(nested, "Array", out var arr) && arr is ICollection arrCol)
            {
                return arrCol.Count;
            }

            return 0;
        }

        private static bool TryReadPPtr(IDictionary? dict, string key, out TmpPPtrRef ptr)
        {
            ptr = default;
            if (!TryGetDictionary(dict, key, out var pptrDict) || pptrDict == null)
            {
                return false;
            }

            return TryParsePPtrDictionary(pptrDict, out ptr);
        }

        private static bool TryParsePPtrDictionary(IDictionary? pptrDict, out TmpPPtrRef ptr)
        {
            ptr = default;
            if (pptrDict == null)
            {
                return false;
            }

            if (!HasKey(pptrDict, "m_PathID"))
            {
                return false;
            }

            var fileId = ReadInt(pptrDict, "m_FileID");
            var pathId = ReadLong(pptrDict, "m_PathID");
            ptr = new TmpPPtrRef(fileId, pathId);
            return pathId != 0;
        }

        private static List<TmpPPtrRef> ReadPPtrList(IDictionary? dict, string key)
        {
            var result = new List<TmpPPtrRef>();
            if (!TryGetValueCaseInsensitive(dict, key, out var raw) || raw == null)
            {
                return result;
            }

            IEnumerable? items = raw as IEnumerable;
            if (raw is IDictionary nested && TryGetValueCaseInsensitive(nested, "Array", out var arr) && arr is IEnumerable arrEnum)
            {
                items = arrEnum;
            }

            if (items == null || items is string)
            {
                return result;
            }

            foreach (var element in items)
            {
                if (element is IDictionary elemDict && TryParsePPtrDictionary(elemDict, out var ptr) && ptr.IsValid)
                {
                    result.Add(ptr);
                }
            }

            return result;
        }
    }
}
