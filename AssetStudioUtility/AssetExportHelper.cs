using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace AssetStudio
{
    public static class AssetExportHelper
    {
        private static readonly StringComparer PathComparer =
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        private static readonly ConcurrentDictionary<string, Lazy<Dictionary<string, string>>> ShaderGuidCacheByProjectRoot =
            new(PathComparer);

        private static readonly ConcurrentDictionary<string, string?> UnityProjectRootByDirectoryCache =
            new(PathComparer);

        private static int shaderGuidDirectoryScanCount;

        public static int ShaderGuidDirectoryScanCount => Volatile.Read(ref shaderGuidDirectoryScanCount);

        public static void ClearShaderGuidCache(string? projectRoot = null)
        {
            if (string.IsNullOrWhiteSpace(projectRoot))
            {
                ShaderGuidCacheByProjectRoot.Clear();
                UnityProjectRootByDirectoryCache.Clear();
                Interlocked.Exchange(ref shaderGuidDirectoryScanCount, 0);
                return;
            }

            var resolvedRoot = FindUnityProjectRoot(projectRoot);
            if (string.IsNullOrEmpty(resolvedRoot))
            {
                try
                {
                    resolvedRoot = Path.GetFullPath(projectRoot);
                }
                catch
                {
                    resolvedRoot = projectRoot;
                }
            }

            ShaderGuidCacheByProjectRoot.TryRemove(resolvedRoot, out _);
            UnityProjectRootByDirectoryCache.Clear();
        }

        public static bool ExportMaterial(Material material, string fallbackName, string exportPath, ImageFormat textureFormat)
        {
            var exportName = string.IsNullOrEmpty(material.m_Name) ? fallbackName : material.m_Name;
            Directory.CreateDirectory(exportPath);
            var filePath = Path.Combine(exportPath, FixFileName(exportName) + ".mat");
            if (File.Exists(filePath)) return false;

            return ExportMaterialToFile(material, fallbackName, exportPath, filePath, textureFormat);
        }

        public static bool ExportMaterialToFile(
            Material material,
            string fallbackName,
            string exportPath,
            string targetFilePath,
            ImageFormat textureFormat)
        {
            var exportName = string.IsNullOrEmpty(material.m_Name) ? fallbackName : material.m_Name;
            EnsureParentDirectory(targetFilePath);
            WriteTextFileAtomic(targetFilePath, BuildUnityMaterial(material, exportName, exportPath, textureFormat));
            return true;
        }

        private static string BuildUnityMaterial(Material material, string materialName, string exportPath, ImageFormat textureFormat)
        {
            var sb = new StringBuilder();
            sb.AppendLine("%YAML 1.1");
            sb.AppendLine("%TAG !u! tag:unity3d.com,2011:");
            sb.AppendLine("--- !u!21 &2100000");
            sb.AppendLine("Material:");
            sb.AppendLine("  serializedVersion: 8");
            sb.AppendLine("  m_ObjectHideFlags: 0");
            sb.AppendLine("  m_CorrespondingSourceObject: {fileID: 0}");
            sb.AppendLine("  m_PrefabInstance: {fileID: 0}");
            sb.AppendLine("  m_PrefabAsset: {fileID: 0}");
            sb.AppendLine($"  m_Name: {YamlString(materialName)}");
            sb.AppendLine($"  m_Shader: {GetShaderReference(material, exportPath)}");
            sb.AppendLine("  m_Parent: {fileID: 0}");
            sb.AppendLine("  m_ModifiedSerializedProperties: 0");
            sb.AppendLine("  m_ValidKeywords: []");
            sb.AppendLine("  m_InvalidKeywords: []");
            sb.AppendLine("  m_LightmapFlags: 4");
            sb.AppendLine("  m_EnableInstancingVariants: 0");
            sb.AppendLine("  m_DoubleSidedGI: 0");
            sb.AppendLine("  m_CustomRenderQueue: -1");
            sb.AppendLine("  stringTagMap: {}");
            sb.AppendLine("  disabledShaderPasses: []");
            sb.AppendLine("  m_LockedProperties: ");
            sb.AppendLine("  m_SavedProperties:");
            sb.AppendLine("    serializedVersion: 3");

            var texEnvs = material.m_SavedProperties?.m_TexEnvs;
            if (texEnvs == null || texEnvs.Length == 0)
            {
                sb.AppendLine("    m_TexEnvs: []");
            }
            else
            {
                sb.AppendLine("    m_TexEnvs:");
                foreach (var texEnv in texEnvs)
                {
                    AppendTextureEnv(sb, texEnv.Key, texEnv.Value, exportPath, textureFormat);
                }
            }

            var ints = material.m_SavedProperties?.m_Ints;
            if (ints == null || ints.Length == 0)
            {
                sb.AppendLine("    m_Ints: []");
            }
            else
            {
                sb.AppendLine("    m_Ints:");
                foreach (var value in ints)
                {
                    sb.AppendLine($"    - {value.Key}: {value.Value}");
                }
            }

            var floats = material.m_SavedProperties?.m_Floats;
            if (floats == null || floats.Length == 0)
            {
                sb.AppendLine("    m_Floats: []");
            }
            else
            {
                sb.AppendLine("    m_Floats:");
                foreach (var value in floats)
                {
                    sb.AppendLine($"    - {value.Key}: {F(value.Value)}");
                }
            }

            var colors = material.m_SavedProperties?.m_Colors;
            if (colors == null || colors.Length == 0)
            {
                sb.AppendLine("    m_Colors: []");
            }
            else
            {
                sb.AppendLine("    m_Colors:");
                foreach (var value in colors)
                {
                    sb.AppendLine($"    - {value.Key}: {{r: {F(value.Value.R)}, g: {F(value.Value.G)}, b: {F(value.Value.B)}, a: {F(value.Value.A)}}}");
                }
            }

            sb.AppendLine("  m_BuildTextureStacks: []");
            sb.AppendLine("  m_AllowLocking: 1");
            AppendRenderPipelineAssetVersion(sb, material);
            return sb.ToString();
        }

        private static void AppendTextureEnv(StringBuilder sb, string property, UnityTexEnv texEnv, string exportPath, ImageFormat textureFormat)
        {
            sb.AppendLine($"    - {property}:");
            sb.Append("        m_Texture: ");
            if (texEnv != null
                && texEnv.m_Texture.TryGet<Texture2D>(out var texture)
                && TryGetExportedTextureGuid(exportPath, texture, textureFormat, out var guid))
            {
                sb.AppendLine($"{{fileID: 2800000, guid: {guid}, type: 3}}");
            }
            else
            {
                sb.AppendLine("{fileID: 0}");
            }

            var scale = texEnv?.m_Scale ?? new Vector2(1, 1);
            var offset = texEnv?.m_Offset ?? Vector2.Zero;
            sb.AppendLine($"        m_Scale: {{x: {F(scale.X)}, y: {F(scale.Y)}}}");
            sb.AppendLine($"        m_Offset: {{x: {F(offset.X)}, y: {F(offset.Y)}}}");
        }

        private static string GetShaderReference(Material material, string exportPath)
        {
            var shaderName = material.m_Shader.TryGet(out var shader)
                ? shader.m_ParsedForm?.m_Name ?? shader.m_Name
                : InferShaderName(material);

            if (shaderName == "Standard")
            {
                return "{fileID: 46, guid: 0000000000000000f0000000000000000, type: 0}";
            }

            if (TryGetShaderGuid(exportPath, shaderName, out var guid))
            {
                return $"{{fileID: 4800000, guid: {guid}, type: 3}}";
            }

            return "{fileID: 0}";
        }

        private static string? InferShaderName(Material material)
        {
            var texEnvNames = material.m_SavedProperties?.m_TexEnvs?.Select(x => x.Key) ?? Enumerable.Empty<string>();
            var floatNames = material.m_SavedProperties?.m_Floats?.Select(x => x.Key) ?? Enumerable.Empty<string>();
            var colorNames = material.m_SavedProperties?.m_Colors?.Select(x => x.Key) ?? Enumerable.Empty<string>();
            var names = new HashSet<string>(texEnvNames.Concat(floatNames).Concat(colorNames));

            if (names.Contains("_BaseMap")
                && (names.Contains("_WorkflowMode")
                    || names.Contains("_MetallicGlossMap")
                    || names.Contains("_SpecGlossMap")
                    || names.Contains("_BumpMap")))
            {
                return "Universal Render Pipeline/Lit";
            }

            if (names.Contains("_MainTex") || names.Contains("_Color"))
            {
                return "Standard";
            }

            return null;
        }

        private static void AppendRenderPipelineAssetVersion(StringBuilder sb, Material material)
        {
            var shaderName = material.m_Shader.TryGet(out var shader)
                ? shader.m_ParsedForm?.m_Name ?? shader.m_Name
                : null;

            if (shaderName == null || !shaderName.StartsWith("Universal Render Pipeline/", StringComparison.Ordinal))
            {
                return;
            }

            sb.AppendLine("--- !u!114 &8810925001771847065");
            sb.AppendLine("MonoBehaviour:");
            sb.AppendLine("  m_ObjectHideFlags: 11");
            sb.AppendLine("  m_CorrespondingSourceObject: {fileID: 0}");
            sb.AppendLine("  m_PrefabInstance: {fileID: 0}");
            sb.AppendLine("  m_PrefabAsset: {fileID: 0}");
            sb.AppendLine("  m_GameObject: {fileID: 0}");
            sb.AppendLine("  m_Enabled: 1");
            sb.AppendLine("  m_EditorHideFlags: 0");
            sb.AppendLine("  m_Script: {fileID: 11500000, guid: d0353a89b1f911e48b9e16bdc9f2e058, type: 3}");
            sb.AppendLine("  m_Name: ");
            sb.AppendLine("  m_EditorClassIdentifier: Unity.RenderPipelines.Universal.Editor::UnityEditor.Rendering.Universal.AssetVersion");
            sb.AppendLine("  version: 10");
        }

        private static bool TryGetExportedTextureGuid(string materialExportPath, Texture2D texture, ImageFormat textureFormat, [NotNullWhen(true)] out string? guid)
        {
            guid = null;
            var textureFolder = GetTextureExportFolder(materialExportPath);
            var extension = "." + textureFormat.ToString().ToLowerInvariant();
            var texturePath = Path.Combine(textureFolder, FixFileName(texture.m_Name) + extension);
            var metaPath = texturePath + ".meta";
            if (File.Exists(metaPath) && TryReadGuid(metaPath, out guid))
            {
                return true;
            }
            guid = GenerateStableGuid(texture);
            return true;
        }

        public static bool TryGetShaderGuid(string path, string? shaderName, [NotNullWhen(true)] out string? guid)
        {
            guid = null;
            if (string.IsNullOrEmpty(shaderName) || string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            var projectRoot = FindUnityProjectRootCached(path);
            if (projectRoot == null)
            {
                return false;
            }

            var shaderFileName = shaderName.Split('/').Last() + ".shader.meta";
            var lazyIndex = ShaderGuidCacheByProjectRoot.GetOrAdd(
                projectRoot,
                root => new Lazy<Dictionary<string, string>>(
                    () => BuildShaderGuidIndexForProjectRoot(root),
                    LazyThreadSafetyMode.ExecutionAndPublication));

            return lazyIndex.Value.TryGetValue(shaderFileName, out guid) && !string.IsNullOrEmpty(guid);
        }

        private static Dictionary<string, string> BuildShaderGuidIndexForProjectRoot(string projectRoot)
        {
            Interlocked.Increment(ref shaderGuidDirectoryScanCount);
            var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var roots = new[]
            {
                Path.Combine(projectRoot, "Assets"),
                Path.Combine(projectRoot, "Packages"),
                Path.Combine(projectRoot, "Library", "PackageCache")
            };

            foreach (var root in roots.Where(Directory.Exists))
            {
                var matches = ImportHelper.GetFilesSafe(root, "*.shader.meta", true);
                foreach (var match in matches)
                {
                    var fileName = Path.GetFileName(match);
                    if (string.IsNullOrEmpty(fileName) || index.ContainsKey(fileName))
                    {
                        continue;
                    }

                    if (TryReadGuid(match, out var parsedGuid))
                    {
                        index[fileName] = parsedGuid;
                    }
                }
            }

            return index;
        }

        private static string? FindUnityProjectRootCached(string path)
        {
            string normalized;
            try
            {
                normalized = Path.GetFullPath(path);
            }
            catch
            {
                normalized = path;
            }

            return UnityProjectRootByDirectoryCache.GetOrAdd(normalized, FindUnityProjectRoot);
        }

        private static bool TryReadGuid(string metaPath, [NotNullWhen(true)] out string? guid)
        {
            guid = null;
            try
            {
                foreach (var line in File.ReadLines(metaPath))
                {
                    if (line.StartsWith("guid: ", StringComparison.Ordinal))
                    {
                        guid = line.Substring("guid: ".Length).Trim();
                        return guid.Length == 32;
                    }
                }
            }
            catch
            {
            }
            return false;
        }

        private static string? FindUnityProjectRoot(string path)
        {
            DirectoryInfo? directory;
            try
            {
                directory = new DirectoryInfo(path);
            }
            catch
            {
                return null;
            }

            while (directory != null)
            {
                if (string.Equals(directory.Name, "Assets", StringComparison.OrdinalIgnoreCase))
                {
                    return directory.Parent?.FullName;
                }
                if (Directory.Exists(Path.Combine(directory.FullName, "Assets"))
                    && Directory.Exists(Path.Combine(directory.FullName, "ProjectSettings")))
                {
                    return directory.FullName;
                }
                directory = directory.Parent;
            }
            return null;
        }

        private static string GetTextureExportFolder(string materialExportPath)
        {
            var normalized = materialExportPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(Path.GetFileName(normalized), "Material", StringComparison.OrdinalIgnoreCase))
            {
                return Path.Combine(Directory.GetParent(normalized)?.FullName ?? materialExportPath, "Texture2D");
            }
            return materialExportPath;
        }

        public static void WriteTextureMetaIfMissing(string texturePath)
        {
            WriteTextureMetaIfMissing(texturePath, null, false);
        }

        public static void WriteTextureMetaIfMissing(string texturePath, Object? obj)
        {
            WriteTextureMetaIfMissing(texturePath, obj, false);
        }

        public static void WriteTextureMetaIfMissing(string texturePath, Object? obj, bool isNormalMap)
        {
            var metaPath = texturePath + ".meta";
            if (File.Exists(metaPath))
            {
                return;
            }

            File.WriteAllText(metaPath, BuildTextureMeta(GenerateStableGuid(obj), obj, isNormalMap), Encoding.UTF8);
        }

        public static string GenerateStableGuid(Object? obj)
        {
            if (obj == null)
                return GenerateUnityGuid();

            var fileName = obj.assetsFile?.fileName ?? "";
            var name = "";
            if (obj is NamedObject namedObj)
            {
                name = namedObj.m_Name;
            }
            var pathID = obj.m_PathID;

            var input = $"{fileName}_{name}_{pathID}";
            return Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(input)));
        }

        private static string GenerateUnityGuid()
        {
            Span<byte> bytes = stackalloc byte[16];
            RandomNumberGenerator.Fill(bytes);
            return Convert.ToHexStringLower(bytes);
        }

        private static bool IsLikelyNormalMapTexture(Texture2D tex)
        {
            if (tex.m_TextureFormat == TextureFormat.BC5)
            {
                return true;
            }

            var name = tex.m_Name;
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            var lower = name.ToLowerInvariant();
            return lower.EndsWith("_n", StringComparison.Ordinal)
                || lower.EndsWith("_nrm", StringComparison.Ordinal)
                || lower.EndsWith("_norm", StringComparison.Ordinal)
                || lower.EndsWith("_normal", StringComparison.Ordinal)
                || lower.EndsWith("_normalmap", StringComparison.Ordinal)
                || lower.EndsWith("_bump", StringComparison.Ordinal)
                || lower.EndsWith("_bumpmap", StringComparison.Ordinal)
                || lower.Contains("normalmap", StringComparison.Ordinal)
                || lower.Contains("_normal_", StringComparison.Ordinal);
        }

        private static string BuildTextureMeta(string guid, Object? obj = null, bool isNormalMap = false)
        {
            int enableMipMap = 1;
            int sRGBTexture = 1;
            int linearTexture = 0;
            int filterMode = 1;
            int aniso = 1;
            float mipBias = 0f;
            int wrapU = 0;
            int wrapV = 0;
            int wrapW = 0;
            int textureType = 0;

            if (obj is Sprite)
            {
                textureType = 8; // Sprite (2D and UI)
            }
            else if (obj is Texture2D tex)
            {
                bool normalMap = isNormalMap || IsLikelyNormalMapTexture(tex);
                if (normalMap)
                {
                    textureType = 1; // Normal map
                    sRGBTexture = 0;
                    linearTexture = 1;
                }
                else
                {
                    // Unity Texture2D.m_ColorSpace: 0 = Linear, 1 = sRGB
                    bool isLinear = tex.m_ColorSpace == 0;
                    sRGBTexture = isLinear ? 0 : 1;
                    linearTexture = isLinear ? 1 : 0;
                }

                if (tex.m_MipCount == 1)
                {
                    enableMipMap = 0;
                }

                if (tex.m_TextureSettings != null)
                {
                    if (tex.m_TextureSettings.m_FilterMode >= 0 && tex.m_TextureSettings.m_FilterMode <= 2)
                        filterMode = tex.m_TextureSettings.m_FilterMode;
                    if (tex.m_TextureSettings.m_Aniso >= 0)
                        aniso = tex.m_TextureSettings.m_Aniso;
                    if (float.IsFinite(tex.m_TextureSettings.m_MipBias))
                        mipBias = tex.m_TextureSettings.m_MipBias;
                    wrapU = tex.m_TextureSettings.m_WrapMode;
                    wrapV = tex.m_TextureSettings.m_WrapMode;
                    wrapW = tex.m_TextureSettings.m_WrapMode;
                }
            }
            else if (isNormalMap)
            {
                textureType = 1;
                sRGBTexture = 0;
                linearTexture = 1;
            }

            return $@"fileFormatVersion: 2
guid: {guid}
TextureImporter:
  internalIDToNameTable: []
  externalObjects: {{}}
  serializedVersion: 13
  mipmaps:
    mipMapMode: 0
    enableMipMap: {enableMipMap}
    sRGBTexture: {sRGBTexture}
    linearTexture: {linearTexture}
    fadeOut: 0
    borderMipMap: 0
    mipMapsPreserveCoverage: 0
    alphaTestReferenceValue: 0.5
    mipMapFadeDistanceStart: 1
    mipMapFadeDistanceEnd: 3
  textureSettings:
    serializedVersion: 2
    filterMode: {filterMode}
    aniso: {aniso}
    mipBias: {F(mipBias)}
    wrapU: {wrapU}
    wrapV: {wrapV}
    wrapW: {wrapW}
  textureType: {textureType}
  textureShape: 1
  platformSettings: []
  spriteSheet:
    serializedVersion: 2
    sprites: []
    outline: []
    physicsShape: []
    bones: []
    spriteID: 
    internalID: 0
    vertices: []
    indices: 
    edges: []
    weights: []
  userData: 
  assetBundleName: 
  assetBundleVariant: 
";
        }

        private static string YamlString(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }
            if (value.IndexOfAny(new[] { ':', '#', '{', '}', '[', ']', ',', '"', '\r', '\n', '\t', '\\' }) >= 0)
            {
                return "\"" + value
                    .Replace("\\", "\\\\")
                    .Replace("\"", "\\\"")
                    .Replace("\r", "\\r")
                    .Replace("\n", "\\n")
                    .Replace("\t", "\\t") + "\"";
            }
            return value;
        }

        private static string F(float value)
        {
            return value.ToString("0.########", CultureInfo.InvariantCulture);
        }


        public static string FixFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "unnamed";
            }

            var invalidChars = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder(name.Length);
            foreach (var character in name)
            {
                if (character < 32
                    || character == '<' || character == '>' || character == ':'
                    || character == '"' || character == '/' || character == '\\'
                    || character == '|' || character == '?' || character == '*'
                    || Array.IndexOf(invalidChars, character) >= 0)
                {
                    builder.Append('_');
                }
                else
                {
                    builder.Append(character);
                }
            }

            name = builder.ToString().Replace("..", "_").Trim().TrimEnd('.');
            if (string.IsNullOrWhiteSpace(name))
            {
                return "unnamed";
            }

            var dotIndex = name.IndexOf('.');
            var deviceName = (dotIndex >= 0 ? name.Substring(0, dotIndex) : name).ToUpperInvariant();
            switch (deviceName)
            {
                case "CON":
                case "PRN":
                case "AUX":
                case "NUL":
                case "COM1":
                case "COM2":
                case "COM3":
                case "COM4":
                case "COM5":
                case "COM6":
                case "COM7":
                case "COM8":
                case "COM9":
                case "LPT1":
                case "LPT2":
                case "LPT3":
                case "LPT4":
                case "LPT5":
                case "LPT6":
                case "LPT7":
                case "LPT8":
                case "LPT9":
                    name = "_" + name;
                    break;
            }

            if (name.Length > 180)
            {
                var ext = Path.GetExtension(name);
                if (!string.IsNullOrEmpty(ext) && ext.Length <= 16)
                {
                    var stem = name.Substring(0, Math.Max(1, 180 - ext.Length)).TrimEnd(' ', '.');
                    name = (string.IsNullOrWhiteSpace(stem) ? "unnamed" : stem) + ext;
                }
                else
                {
                    name = name.Substring(0, 180).TrimEnd(' ', '.');
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        return "unnamed";
                    }
                }
            }

            return name;
        }

        #region Animation

        private class CustomCurve<T>
        {
            public required string Path;
            public string Attribute = string.Empty;
            public List<CustomKeyframe<T>> Keyframes = new List<CustomKeyframe<T>>();
        }

        private class CustomKeyframe<T>
        {
            public float Time;
            public required T Value;
        }

        public static Dictionary<uint, string> BuildBonePathHash(List<SerializedFile> assetsFileList)
        {
            var bonePathHash = new Dictionary<uint, string>();
            foreach (var assetsFile in assetsFileList)
            {
                foreach (var obj in assetsFile.Objects)
                {
                    if (obj is Transform transform)
                    {
                        var path = GetTransformPath(transform);
                        if (!string.IsNullOrEmpty(path))
                        {
                            AddPathToHash(bonePathHash, path);
                        }
                    }
                    else if (obj is Avatar avatar && avatar.m_TOS != null)
                    {
                        foreach (var pair in avatar.m_TOS)
                        {
                            if (!string.IsNullOrEmpty(pair.Value))
                            {
                                bonePathHash[pair.Key] = pair.Value;
                            }
                        }
                    }
                }
            }
            return bonePathHash;
        }

        private static string GetTransformPath(Transform transform)
        {
            return GetTransformPath(transform, new HashSet<Transform>());
        }

        private static string GetTransformPath(Transform transform, HashSet<Transform> visited)
        {
            if (transform == null || !visited.Add(transform))
            {
                return "";
            }
            if (transform.m_GameObject.TryGet(out var m_GameObject))
            {
                if (transform.m_Father.TryGet(out var father) && !visited.Contains(father))
                {
                    var fatherPath = GetTransformPath(father, visited);
                    return string.IsNullOrEmpty(fatherPath) ? m_GameObject.m_Name : fatherPath + "/" + m_GameObject.m_Name;
                }
                return m_GameObject.m_Name;
            }
            return "";
        }

        private static void AddPathToHash(Dictionary<uint, string> dict, string path)
        {
            var crc = new SevenZip.CRC();
            var bytes = Encoding.UTF8.GetBytes(path);
            crc.Update(bytes, 0, (uint)bytes.Length);
            dict[crc.GetDigest()] = path;

            int index;
            var subPath = path;
            while ((index = subPath.IndexOf("/", StringComparison.Ordinal)) >= 0)
            {
                subPath = subPath.Substring(index + 1);
                crc = new SevenZip.CRC();
                bytes = Encoding.UTF8.GetBytes(subPath);
                crc.Update(bytes, 0, (uint)bytes.Length);
                dict[crc.GetDigest()] = subPath;
            }
        }

        public static Dictionary<uint, string> BuildMorphChannelNames(List<SerializedFile> assetsFileList)
        {
            var morphChannelNames = new Dictionary<uint, string>();
            foreach (var assetsFile in assetsFileList)
            {
                foreach (var obj in assetsFile.Objects)
                {
                    if (obj is Mesh mesh && mesh.m_Shapes?.channels != null)
                    {
                        foreach (var channel in mesh.m_Shapes.channels)
                        {
                            if (!string.IsNullOrEmpty(channel.name))
                            {
                                var blendShapeName = channel.name.StartsWith("blendShape.", StringComparison.OrdinalIgnoreCase)
                                    ? channel.name
                                    : "blendShape." + channel.name;
                                if (channel.nameHash != 0)
                                {
                                    morphChannelNames[channel.nameHash] = blendShapeName;
                                }
                                morphChannelNames[ComputeUnityCrc32(channel.name)] = blendShapeName;
                                morphChannelNames[ComputeUnityCrc32(blendShapeName)] = blendShapeName;
                            }
                        }
                    }
                }
            }
            return morphChannelNames;
        }

        private static uint ComputeUnityCrc32(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }
            var crc = new SevenZip.CRC();
            var bytes = Encoding.UTF8.GetBytes(text);
            crc.Update(bytes, 0, (uint)bytes.Length);
            return crc.GetDigest();
        }

        public static bool ExportAnimationClip(AnimationClip clip, string fallbackName, string exportPath, Dictionary<uint, string> bonePathHash, Dictionary<uint, string> morphChannelNames)
        {
            var exportName = string.IsNullOrEmpty(clip.m_Name) ? fallbackName : clip.m_Name;
            Directory.CreateDirectory(exportPath);
            var filePath = Path.Combine(exportPath, FixFileName(exportName) + ".anim");
            if (File.Exists(filePath)) return false;

            return ExportAnimationClipToFile(clip, filePath, bonePathHash, morphChannelNames);
        }

        public static bool ExportAnimationClipToFile(
            AnimationClip clip,
            string targetFilePath,
            Dictionary<uint, string> bonePathHash,
            Dictionary<uint, string> morphChannelNames)
        {
            EnsureParentDirectory(targetFilePath);
            WriteTextFileAtomic(targetFilePath, BuildUnityAnimationClip(clip, bonePathHash, morphChannelNames));
            return true;
        }

        private static void WriteTextFileAtomic(string filePath, string content)
        {
            try
            {
                using var file = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan);
                using var writer = new StreamWriter(file, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                writer.Write(content);
            }
            catch
            {
                try
                {
                    if (File.Exists(filePath))
                    {
                        File.Delete(filePath);
                    }
                }
                catch
                {
                }
                throw;
            }
        }

        private static string BuildUnityAnimationClip(AnimationClip clip, Dictionary<uint, string> bonePathHash, Dictionary<uint, string> morphChannelNames)
        {
            var sb = new StringBuilder();
            sb.AppendLine("%YAML 1.1");
            sb.AppendLine("%TAG !u! tag:unity3d.com,2011:");
            sb.AppendLine("--- !u!74 &7400000");
            sb.AppendLine("AnimationClip:");
            sb.AppendLine("  m_ObjectHideFlags: 0");
            sb.AppendLine("  m_CorrespondingSourceObject: {fileID: 0}");
            sb.AppendLine("  m_PrefabInstance: {fileID: 0}");
            sb.AppendLine("  m_PrefabAsset: {fileID: 0}");
            sb.AppendLine($"  m_Name: {YamlString(clip.m_Name)}");
            sb.AppendLine("  serializedVersion: 6");
            sb.AppendLine($"  m_Legacy: {(clip.m_Legacy ? 1 : 0)}");
            sb.AppendLine($"  m_Compressed: {(clip.m_Compressed ? 1 : 0)}");
            sb.AppendLine($"  m_UseHighQualityCurve: {(clip.m_UseHighQualityCurve ? 1 : 0)}");

            var posCurves = new List<CustomCurve<Vector3>>();
            var rotCurves = new List<CustomCurve<Quaternion>>();
            var eulerCurves = new List<CustomCurve<Vector3>>();
            var scaleCurves = new List<CustomCurve<Vector3>>();
            var floatCurves = new List<CustomCurve<float>>();

            if (clip.m_PositionCurves != null)
            {
                foreach (var c in clip.m_PositionCurves)
                {
                    var curve = new CustomCurve<Vector3> { Path = c.path };
                    foreach (var k in c.curve.m_Curve)
                    {
                        curve.Keyframes.Add(new CustomKeyframe<Vector3> { Time = k.time, Value = k.value });
                    }
                    posCurves.Add(curve);
                }
            }

            if (clip.m_RotationCurves != null)
            {
                foreach (var c in clip.m_RotationCurves)
                {
                    var curve = new CustomCurve<Quaternion> { Path = c.path };
                    foreach (var k in c.curve.m_Curve)
                    {
                        curve.Keyframes.Add(new CustomKeyframe<Quaternion> { Time = k.time, Value = k.value });
                    }
                    rotCurves.Add(curve);
                }
            }

            if (clip.m_CompressedRotationCurves != null)
            {
                foreach (var crc in clip.m_CompressedRotationCurves)
                {
                    var curve = new CustomCurve<Quaternion> { Path = crc.m_Path };
                    var numKeys = crc.m_Times.m_NumItems;
                    var timeData = crc.m_Times.UnpackInts();
                    var quats = crc.m_Values.UnpackQuats();
                    int t = 0;
                    for (int i = 0; i < numKeys; i++)
                    {
                        t += timeData[i];
                        float time = t * 0.01f;
                        curve.Keyframes.Add(new CustomKeyframe<Quaternion> { Time = time, Value = quats[i] });
                    }
                    rotCurves.Add(curve);
                }
            }

            if (clip.m_EulerCurves != null)
            {
                foreach (var c in clip.m_EulerCurves)
                {
                    var curve = new CustomCurve<Vector3> { Path = c.path };
                    foreach (var k in c.curve.m_Curve)
                    {
                        curve.Keyframes.Add(new CustomKeyframe<Vector3> { Time = k.time, Value = k.value });
                    }
                    eulerCurves.Add(curve);
                }
            }

            if (clip.m_ScaleCurves != null)
            {
                foreach (var c in clip.m_ScaleCurves)
                {
                    var curve = new CustomCurve<Vector3> { Path = c.path };
                    foreach (var k in c.curve.m_Curve)
                    {
                        curve.Keyframes.Add(new CustomKeyframe<Vector3> { Time = k.time, Value = k.value });
                    }
                    scaleCurves.Add(curve);
                }
            }

            if (clip.m_FloatCurves != null)
            {
                foreach (var c in clip.m_FloatCurves)
                {
                    var curve = new CustomCurve<float> { Path = c.path, Attribute = c.attribute };
                    foreach (var k in c.curve.m_Curve)
                    {
                        curve.Keyframes.Add(new CustomKeyframe<float> { Time = k.time, Value = k.value });
                    }
                    floatCurves.Add(curve);
                }
            }

            if (clip.m_MuscleClip != null && clip.m_MuscleClip.m_Clip != null)
            {
                var m_Clip = clip.m_MuscleClip.m_Clip;
                var m_ClipBindingConstant = clip.m_ClipBindingConstant ?? m_Clip.ConvertValueArrayToGenericBinding();

                string GetPath(uint hash)
                {
                    if (hash == 0)
                    {
                        return string.Empty;
                    }
                    if (bonePathHash != null && bonePathHash.TryGetValue(hash, out var p))
                    {
                        return p;
                    }
                    return "unknown_" + hash;
                }

                string GetMorphChannelName(uint hash)
                {
                    if (morphChannelNames != null && morphChannelNames.TryGetValue(hash, out var name))
                    {
                        return name;
                    }
                    return "blendShape.unknown_" + hash;
                }

                var trackDict = new Dictionary<string, (List<CustomKeyframe<Vector3>> pos, List<CustomKeyframe<Quaternion>> rot, List<CustomKeyframe<Vector3>> euler, List<CustomKeyframe<Vector3>> scale, Dictionary<string, List<CustomKeyframe<float>>> floats)>();

                (List<CustomKeyframe<Vector3>> pos, List<CustomKeyframe<Quaternion>> rot, List<CustomKeyframe<Vector3>> euler, List<CustomKeyframe<Vector3>> scale, Dictionary<string, List<CustomKeyframe<float>>> floats) GetTrack(string path)
                {
                    if (!trackDict.TryGetValue(path, out var t))
                    {
                        t = (new List<CustomKeyframe<Vector3>>(), new List<CustomKeyframe<Quaternion>>(), new List<CustomKeyframe<Vector3>>(), new List<CustomKeyframe<Vector3>>(), new Dictionary<string, List<CustomKeyframe<float>>>());
                        trackDict[path] = t;
                    }
                    return t;
                }

                void AddCurveData(int index, float time, float[] data, int offset, ref int curveIndex)
                {
                    var binding = m_ClipBindingConstant.FindBinding(index);
                    if (binding == null)
                    {
                        curveIndex++;
                        return;
                    }

                    if (binding.typeID == ClassIDType.Transform)
                    {
                        var path = GetPath(binding.path);
                        var track = GetTrack(path);

                        switch (binding.attribute)
                        {
                            case 1:
                                if ((uint)(curveIndex + 2 + offset) < (uint)data.Length)
                                {
                                    track.pos.Add(new CustomKeyframe<Vector3>
                                    {
                                        Time = time,
                                        Value = new Vector3(
                                            data[curveIndex + offset],
                                            data[curveIndex + 1 + offset],
                                            data[curveIndex + 2 + offset]
                                        )
                                    });
                                }
                                curveIndex += 3;
                                break;
                            case 2:
                                if ((uint)(curveIndex + 3 + offset) < (uint)data.Length)
                                {
                                    track.rot.Add(new CustomKeyframe<Quaternion>
                                    {
                                        Time = time,
                                        Value = new Quaternion(
                                            data[curveIndex + offset],
                                            data[curveIndex + 1 + offset],
                                            data[curveIndex + 2 + offset],
                                            data[curveIndex + 3 + offset]
                                        )
                                    });
                                }
                                curveIndex += 4;
                                break;
                            case 3:
                                if ((uint)(curveIndex + 2 + offset) < (uint)data.Length)
                                {
                                    track.scale.Add(new CustomKeyframe<Vector3>
                                    {
                                        Time = time,
                                        Value = new Vector3(
                                            data[curveIndex + offset],
                                            data[curveIndex + 1 + offset],
                                            data[curveIndex + 2 + offset]
                                        )
                                    });
                                }
                                curveIndex += 3;
                                break;
                            case 4:
                                if ((uint)(curveIndex + 2 + offset) < (uint)data.Length)
                                {
                                    track.euler.Add(new CustomKeyframe<Vector3>
                                    {
                                        Time = time,
                                        Value = new Vector3(
                                            data[curveIndex + offset],
                                            data[curveIndex + 1 + offset],
                                            data[curveIndex + 2 + offset]
                                        )
                                    });
                                }
                                curveIndex += 3;
                                break;
                            default:
                                curveIndex++;
                                break;
                        }
                    }
                    else if (binding.typeID == ClassIDType.SkinnedMeshRenderer)
                    {
                        var path = GetPath(binding.path);
                        var track = GetTrack(path);
                        var attribute = GetMorphChannelName(binding.attribute);
                        if (!track.floats.TryGetValue(attribute, out var list))
                        {
                            list = new List<CustomKeyframe<float>>();
                            track.floats[attribute] = list;
                        }
                        int valIdx = curveIndex++ + offset;
                        if ((uint)valIdx < (uint)data.Length)
                        {
                            list.Add(new CustomKeyframe<float>
                            {
                                Time = time,
                                Value = data[valIdx]
                            });
                        }
                    }
                    else if (binding.typeID == ClassIDType.GameObject && binding.attribute == 2086281974)
                    {
                        var path = GetPath(binding.path);
                        var track = GetTrack(path);
                        const string visAttr = "m_IsActive";
                        if (!track.floats.TryGetValue(visAttr, out var list))
                        {
                            list = new List<CustomKeyframe<float>>();
                            track.floats[visAttr] = list;
                        }
                        int valIdx = curveIndex++ + offset;
                        if ((uint)valIdx < (uint)data.Length)
                        {
                            list.Add(new CustomKeyframe<float>
                            {
                                Time = time,
                                Value = data[valIdx] >= 0.5f ? 1f : 0f
                            });
                        }
                    }
                    else
                    {
                        curveIndex++;
                    }
                }

                if (m_Clip.m_StreamedClip != null)
                {
                    var streamedFrames = m_Clip.m_StreamedClip.ReadData();
                    for (int frameIndex = 1; frameIndex < streamedFrames.Count - 1; frameIndex++)
                    {
                        var frame = streamedFrames[frameIndex];
                        var streamedValues = frame.keyList.Select(x => x.value).ToArray();
                        for (int curveIndex = 0; curveIndex < frame.keyList.Length;)
                        {
                            AddCurveData(frame.keyList[curveIndex].index, frame.time, streamedValues, 0, ref curveIndex);
                        }
                    }
                }

                if (m_Clip.m_DenseClip != null && m_Clip.m_DenseClip.m_FrameCount > 0 && m_Clip.m_DenseClip.m_CurveCount > 0)
                {
                    var m_DenseClip = m_Clip.m_DenseClip;
                    var streamCount = m_Clip.m_StreamedClip?.curveCount ?? 0;
                    float denseRate = m_DenseClip.m_SampleRate > 0f && float.IsFinite(m_DenseClip.m_SampleRate)
                        ? m_DenseClip.m_SampleRate
                        : (clip.m_SampleRate > 0f && float.IsFinite(clip.m_SampleRate) ? clip.m_SampleRate : 30f);
                    for (int frameIndex = 0; frameIndex < m_DenseClip.m_FrameCount; frameIndex++)
                    {
                        var time = m_DenseClip.m_BeginTime + frameIndex / denseRate;
                        var frameOffset = frameIndex * m_DenseClip.m_CurveCount;
                        for (int curveIndex = 0; curveIndex < m_DenseClip.m_CurveCount;)
                        {
                            var index = streamCount + curveIndex;
                            AddCurveData((int)index, time, m_DenseClip.m_SampleArray, (int)frameOffset, ref curveIndex);
                        }
                    }
                }

                if (m_Clip.m_ConstantClip?.data != null && m_Clip.m_ConstantClip.data.Length > 0)
                {
                    var m_ConstantClip = m_Clip.m_ConstantClip;
                    var denseCount = m_Clip.m_DenseClip?.m_CurveCount ?? 0;
                    var streamCount = m_Clip.m_StreamedClip?.curveCount ?? 0;
                    float constStartTime = clip.m_MuscleClip != null && float.IsFinite(clip.m_MuscleClip.m_StartTime) && clip.m_MuscleClip.m_StartTime > 0f
                        ? clip.m_MuscleClip.m_StartTime
                        : 0f;
                    float constStopTime = clip.m_MuscleClip != null && float.IsFinite(clip.m_MuscleClip.m_StopTime) && clip.m_MuscleClip.m_StopTime > constStartTime
                        ? clip.m_MuscleClip.m_StopTime
                        : constStartTime;
                    int passes = constStopTime > constStartTime + 1e-6f ? 2 : 1;
                    for (int i = 0; i < passes; i++)
                    {
                        float time2 = i == 0 ? constStartTime : constStopTime;
                        for (int curveIndex = 0; curveIndex < m_ConstantClip.data.Length;)
                        {
                            var index = streamCount + denseCount + curveIndex;
                            AddCurveData((int)index, time2, m_ConstantClip.data, 0, ref curveIndex);
                        }
                    }
                }

                foreach (var kvp in trackDict)
                {
                    var path = kvp.Key;
                    var track = kvp.Value;

                    if (track.pos.Count > 0)
                    {
                        posCurves.Add(new CustomCurve<Vector3> { Path = path, Keyframes = track.pos });
                    }
                    if (track.rot.Count > 0)
                    {
                        rotCurves.Add(new CustomCurve<Quaternion> { Path = path, Keyframes = track.rot });
                    }
                    if (track.euler.Count > 0)
                    {
                        eulerCurves.Add(new CustomCurve<Vector3> { Path = path, Keyframes = track.euler });
                    }
                    if (track.scale.Count > 0)
                    {
                        scaleCurves.Add(new CustomCurve<Vector3> { Path = path, Keyframes = track.scale });
                    }
                    foreach (var fKvp in track.floats)
                    {
                        floatCurves.Add(new CustomCurve<float> { Path = path, Attribute = fKvp.Key, Keyframes = fKvp.Value });
                    }
                }
            }

            if (rotCurves.Count == 0)
            {
                sb.AppendLine("  m_RotationCurves: []");
            }
            else
            {
                sb.AppendLine("  m_RotationCurves:");
                foreach (var c in rotCurves)
                {
                    sb.AppendLine("  - curve:");
                    sb.AppendLine("      serializedVersion: 2");
                    sb.AppendLine("      m_Curve:");
                    foreach (var k in c.Keyframes)
                    {
                        sb.AppendLine("      - serializedVersion: 3");
                        sb.AppendLine($"        time: {F(k.Time)}");
                        sb.AppendLine($"        value: {{x: {F(k.Value.X)}, y: {F(k.Value.Y)}, z: {F(k.Value.Z)}, w: {F(k.Value.W)}}}");
                        sb.AppendLine($"        inSlope: {{x: 0, y: 0, z: 0, w: 0}}");
                        sb.AppendLine($"        outSlope: {{x: 0, y: 0, z: 0, w: 0}}");
                        sb.AppendLine($"        tangentMode: 0");
                        sb.AppendLine($"        weightedMode: 0");
                        sb.AppendLine($"        inWeight: {{x: 0.33333334, y: 0.33333334, z: 0.33333334, w: 0.33333334}}");
                        sb.AppendLine($"        outWeight: {{x: 0.33333334, y: 0.33333334, z: 0.33333334, w: 0.33333334}}");
                    }
                    sb.AppendLine("      m_PreInfinity: 2");
                    sb.AppendLine("      m_PostInfinity: 2");
                    sb.AppendLine("      m_RotationOrder: 4");
                    sb.AppendLine($"    path: {YamlString(c.Path)}");
                }
            }

            sb.AppendLine("  m_CompressedRotationCurves: []");

            if (eulerCurves.Count == 0)
            {
                sb.AppendLine("  m_EulerCurves: []");
            }
            else
            {
                sb.AppendLine("  m_EulerCurves:");
                foreach (var c in eulerCurves)
                {
                    sb.AppendLine("  - curve:");
                    sb.AppendLine("      serializedVersion: 2");
                    sb.AppendLine("      m_Curve:");
                    foreach (var k in c.Keyframes)
                    {
                        sb.AppendLine("      - serializedVersion: 3");
                        sb.AppendLine($"        time: {F(k.Time)}");
                        sb.AppendLine($"        value: {{x: {F(k.Value.X)}, y: {F(k.Value.Y)}, z: {F(k.Value.Z)}}}");
                        sb.AppendLine($"        inSlope: {{x: 0, y: 0, z: 0}}");
                        sb.AppendLine($"        outSlope: {{x: 0, y: 0, z: 0}}");
                        sb.AppendLine($"        tangentMode: 0");
                        sb.AppendLine($"        weightedMode: 0");
                        sb.AppendLine($"        inWeight: {{x: 0.33333334, y: 0.33333334, z: 0.33333334}}");
                        sb.AppendLine($"        outWeight: {{x: 0.33333334, y: 0.33333334, z: 0.33333334}}");
                    }
                    sb.AppendLine("      m_PreInfinity: 2");
                    sb.AppendLine("      m_PostInfinity: 2");
                    sb.AppendLine("      m_RotationOrder: 4");
                    sb.AppendLine($"    path: {YamlString(c.Path)}");
                }
            }

            if (posCurves.Count == 0)
            {
                sb.AppendLine("  m_PositionCurves: []");
            }
            else
            {
                sb.AppendLine("  m_PositionCurves:");
                foreach (var c in posCurves)
                {
                    sb.AppendLine("  - curve:");
                    sb.AppendLine("      serializedVersion: 2");
                    sb.AppendLine("      m_Curve:");
                    foreach (var k in c.Keyframes)
                    {
                        sb.AppendLine("      - serializedVersion: 3");
                        sb.AppendLine($"        time: {F(k.Time)}");
                        sb.AppendLine($"        value: {{x: {F(k.Value.X)}, y: {F(k.Value.Y)}, z: {F(k.Value.Z)}}}");
                        sb.AppendLine($"        inSlope: {{x: 0, y: 0, z: 0}}");
                        sb.AppendLine($"        outSlope: {{x: 0, y: 0, z: 0}}");
                        sb.AppendLine($"        tangentMode: 0");
                        sb.AppendLine($"        weightedMode: 0");
                        sb.AppendLine($"        inWeight: {{x: 0.33333334, y: 0.33333334, z: 0.33333334}}");
                        sb.AppendLine($"        outWeight: {{x: 0.33333334, y: 0.33333334, z: 0.33333334}}");
                    }
                    sb.AppendLine("      m_PreInfinity: 2");
                    sb.AppendLine("      m_PostInfinity: 2");
                    sb.AppendLine("      m_RotationOrder: 4");
                    sb.AppendLine($"    path: {YamlString(c.Path)}");
                }
            }

            if (scaleCurves.Count == 0)
            {
                sb.AppendLine("  m_ScaleCurves: []");
            }
            else
            {
                sb.AppendLine("  m_ScaleCurves:");
                foreach (var c in scaleCurves)
                {
                    sb.AppendLine("  - curve:");
                    sb.AppendLine("      serializedVersion: 2");
                    sb.AppendLine("      m_Curve:");
                    foreach (var k in c.Keyframes)
                    {
                        sb.AppendLine("      - serializedVersion: 3");
                        sb.AppendLine($"        time: {F(k.Time)}");
                        sb.AppendLine($"        value: {{x: {F(k.Value.X)}, y: {F(k.Value.Y)}, z: {F(k.Value.Z)}}}");
                        sb.AppendLine($"        inSlope: {{x: 0, y: 0, z: 0}}");
                        sb.AppendLine($"        outSlope: {{x: 0, y: 0, z: 0}}");
                        sb.AppendLine($"        tangentMode: 0");
                        sb.AppendLine($"        weightedMode: 0");
                        sb.AppendLine($"        inWeight: {{x: 0.33333334, y: 0.33333334, z: 0.33333334}}");
                        sb.AppendLine($"        outWeight: {{x: 0.33333334, y: 0.33333334, z: 0.33333334}}");
                    }
                    sb.AppendLine("      m_PreInfinity: 2");
                    sb.AppendLine("      m_PostInfinity: 2");
                    sb.AppendLine("      m_RotationOrder: 4");
                    sb.AppendLine($"    path: {YamlString(c.Path)}");
                }
            }

            if (floatCurves.Count == 0)
            {
                sb.AppendLine("  m_FloatCurves: []");
            }
            else
            {
                sb.AppendLine("  m_FloatCurves:");
                foreach (var c in floatCurves)
                {
                    sb.AppendLine("  - curve:");
                    sb.AppendLine("      serializedVersion: 2");
                    sb.AppendLine("      m_Curve:");
                    foreach (var k in c.Keyframes)
                    {
                        sb.AppendLine("      - serializedVersion: 3");
                        sb.AppendLine($"        time: {F(k.Time)}");
                        sb.AppendLine($"        value: {F(k.Value)}");
                        sb.AppendLine($"        inSlope: 0");
                        sb.AppendLine($"        outSlope: 0");
                        sb.AppendLine($"        tangentMode: 0");
                        sb.AppendLine($"        weightedMode: 0");
                        sb.AppendLine($"        inWeight: 0.33333334");
                        sb.AppendLine($"        outWeight: 0.33333334");
                    }
                    sb.AppendLine("      m_PreInfinity: 2");
                    sb.AppendLine("      m_PostInfinity: 2");
                    sb.AppendLine("      m_RotationOrder: 4");
                    sb.AppendLine($"    attribute: {YamlString(c.Attribute)}");
                    sb.AppendLine($"    path: {YamlString(c.Path)}");
                    sb.AppendLine("    classID: 137");
                    sb.AppendLine("    script: {fileID: 0}");
                }
            }

            if (clip.m_PPtrCurves == null || clip.m_PPtrCurves.Length == 0)
            {
                sb.AppendLine("  m_PPtrCurves: []");
            }
            else
            {
                sb.AppendLine("  m_PPtrCurves:");
                foreach (var pptrCurve in clip.m_PPtrCurves)
                {
                    sb.AppendLine("  - serializedVersion: 2");
                    sb.AppendLine("    curve:");
                    if (pptrCurve.curve != null)
                    {
                        foreach (var kf in pptrCurve.curve)
                        {
                            sb.AppendLine("    - serializedVersion: 2");
                            sb.AppendLine($"      time: {F(kf.time)}");
                            
                            string valStr = "{fileID: 0}";
                            if (kf.value != null && !kf.value.IsNull)
                            {
                                if (kf.value.TryGet(out var targetObj))
                                {
                                    var fileID = (int)targetObj.type * 100000;
                                    var targetGuid = GenerateStableGuid(targetObj);
                                    valStr = $"{{fileID: {fileID}, guid: {targetGuid}, type: 3}}";
                                }
                            }
                            sb.AppendLine($"      value: {valStr}");
                        }
                    }
                    sb.AppendLine($"    attribute: {pptrCurve.attribute}");
                    sb.AppendLine($"    path: {YamlString(pptrCurve.path)}");
                    sb.AppendLine($"    classID: {pptrCurve.classID}");
                    sb.AppendLine("    script: {fileID: 0}");
                }
            }
            sb.AppendLine($"  m_SampleRate: {F(clip.m_SampleRate)}");
            sb.AppendLine($"  m_WrapMode: {clip.m_WrapMode}");

            var boundsCenter = clip.m_Bounds?.m_Center ?? Vector3.Zero;
            var boundsExtent = clip.m_Bounds?.m_Extent ?? Vector3.Zero;
            sb.AppendLine("  m_Bounds:");
            sb.AppendLine($"    m_Center: {{x: {F(boundsCenter.X)}, y: {F(boundsCenter.Y)}, z: {F(boundsCenter.Z)}}}");
            sb.AppendLine($"    m_Extent: {{x: {F(boundsExtent.X)}, y: {F(boundsExtent.Y)}, z: {F(boundsExtent.Z)}}}");

            sb.AppendLine("  m_ClipBindingConstant:");
            var pptrCurvesCount = clip.m_PPtrCurves?.Length ?? 0;
            var totalBindings = posCurves.Count + rotCurves.Count + eulerCurves.Count + scaleCurves.Count + floatCurves.Count + pptrCurvesCount;
            if (totalBindings == 0)
            {
                sb.AppendLine("    genericBindings: []");
            }
            else
            {
                sb.AppendLine("    genericBindings:");
                foreach (var c in posCurves)
                {
                    sb.AppendLine("    - serializedVersion: 2");
                    sb.AppendLine($"      path: {GetCrc(c.Path)}");
                    sb.AppendLine("      attribute: 1");
                    sb.AppendLine("      script: {fileID: 0}");
                    sb.AppendLine("      typeID: 4");
                    sb.AppendLine("      customType: 0");
                    sb.AppendLine("      isPPtrCurve: 0");
                }
                foreach (var c in rotCurves)
                {
                    sb.AppendLine("    - serializedVersion: 2");
                    sb.AppendLine($"      path: {GetCrc(c.Path)}");
                    sb.AppendLine("      attribute: 2");
                    sb.AppendLine("      script: {fileID: 0}");
                    sb.AppendLine("      typeID: 4");
                    sb.AppendLine("      customType: 0");
                    sb.AppendLine("      isPPtrCurve: 0");
                }
                foreach (var c in eulerCurves)
                {
                    sb.AppendLine("    - serializedVersion: 2");
                    sb.AppendLine($"      path: {GetCrc(c.Path)}");
                    sb.AppendLine("      attribute: 4");
                    sb.AppendLine("      script: {fileID: 0}");
                    sb.AppendLine("      typeID: 4");
                    sb.AppendLine("      customType: 4");
                    sb.AppendLine("      isPPtrCurve: 0");
                }
                foreach (var c in scaleCurves)
                {
                    sb.AppendLine("    - serializedVersion: 2");
                    sb.AppendLine($"      path: {GetCrc(c.Path)}");
                    sb.AppendLine("      attribute: 3");
                    sb.AppendLine("      script: {fileID: 0}");
                    sb.AppendLine("      typeID: 4");
                    sb.AppendLine("      customType: 0");
                    sb.AppendLine("      isPPtrCurve: 0");
                }
                foreach (var c in floatCurves)
                {
                    sb.AppendLine("    - serializedVersion: 2");
                    sb.AppendLine($"      path: {GetCrc(c.Path)}");
                    sb.AppendLine($"      attribute: {GetCrc(c.Attribute)}");
                    sb.AppendLine("      script: {fileID: 0}");
                    sb.AppendLine("      typeID: 137");
                    sb.AppendLine("      customType: 0");
                    sb.AppendLine("      isPPtrCurve: 0");
                }
                if (clip.m_PPtrCurves != null)
                {
                    foreach (var pptrCurve in clip.m_PPtrCurves)
                    {
                        var pathHash = GetCrc(pptrCurve.path);
                        var attrHash = GetCrc(pptrCurve.attribute);
                        
                        GenericBinding? match = null;
                        if (clip.m_ClipBindingConstant?.genericBindings != null)
                        {
                            match = clip.m_ClipBindingConstant.genericBindings.FirstOrDefault(b => b.path == pathHash && b.isPPtrCurve == 1);
                        }
                        
                        var typeID = match != null ? (int)match.typeID : pptrCurve.classID;
                        var customType = match != null ? match.customType : 0;
                        var attribute = match != null ? match.attribute : attrHash;
                        
                        sb.AppendLine("    - serializedVersion: 2");
                        sb.AppendLine($"      path: {pathHash}");
                        sb.AppendLine($"      attribute: {attribute}");
                        sb.AppendLine("      script: {fileID: 0}");
                        sb.AppendLine($"      typeID: {typeID}");
                        sb.AppendLine($"      customType: {customType}");
                        sb.AppendLine("      isPPtrCurve: 1");
                    }
                }
            }
            if (clip.m_ClipBindingConstant?.pptrCurveMapping == null || clip.m_ClipBindingConstant.pptrCurveMapping.Length == 0)
            {
                sb.AppendLine("    pptrCurveMapping: []");
            }
            else
            {
                sb.AppendLine("    pptrCurveMapping:");
                foreach (var pptr in clip.m_ClipBindingConstant.pptrCurveMapping)
                {
                    string valStr = "{fileID: 0}";
                    if (pptr != null && !pptr.IsNull)
                    {
                        if (pptr.TryGet(out var targetObj))
                        {
                            var fileID = (int)targetObj.type * 100000;
                            var targetGuid = GenerateStableGuid(targetObj);
                            valStr = $"{{fileID: {fileID}, guid: {targetGuid}, type: 3}}";
                        }
                    }
                    sb.AppendLine($"    - {valStr}");
                }
            }

            var startTime = clip.m_MuscleClip?.m_StartTime ?? 0f;
            var stopTime = clip.m_MuscleClip?.m_StopTime ?? 0f;
            var orientationOffset = clip.m_MuscleClip?.m_OrientationOffsetY ?? 0f;
            var level = clip.m_MuscleClip?.m_Level ?? 0f;
            var cycleOffset = clip.m_MuscleClip?.m_CycleOffset ?? 0f;
            var loopTime = clip.m_MuscleClip != null && clip.m_MuscleClip.m_LoopTime ? 1 : 0;
            var loopBlend = clip.m_MuscleClip != null && clip.m_MuscleClip.m_LoopBlend ? 1 : 0;
            var loopBlendOri = clip.m_MuscleClip != null && clip.m_MuscleClip.m_LoopBlendOrientation ? 1 : 0;
            var loopBlendPosY = clip.m_MuscleClip != null && clip.m_MuscleClip.m_LoopBlendPositionY ? 1 : 0;
            var loopBlendPosXZ = clip.m_MuscleClip != null && clip.m_MuscleClip.m_LoopBlendPositionXZ ? 1 : 0;
            var keepOri = clip.m_MuscleClip != null && clip.m_MuscleClip.m_KeepOriginalOrientation ? 1 : 0;
            var keepPosY = clip.m_MuscleClip != null && clip.m_MuscleClip.m_KeepOriginalPositionY ? 1 : 0;
            var keepPosXZ = clip.m_MuscleClip != null && clip.m_MuscleClip.m_KeepOriginalPositionXZ ? 1 : 0;
            var heightFromFeet = clip.m_MuscleClip != null && clip.m_MuscleClip.m_HeightFromFeet ? 1 : 0;
            var mirror = clip.m_MuscleClip != null && clip.m_MuscleClip.m_Mirror ? 1 : 0;

            sb.AppendLine("  m_AnimationClipSettings:");
            sb.AppendLine("    serializedVersion: 2");
            sb.AppendLine("    m_AdditiveReferencePoseClip: {fileID: 0}");
            sb.AppendLine("    m_AdditiveReferencePoseTime: 0");
            sb.AppendLine($"    m_StartTime: {F(startTime)}");
            sb.AppendLine($"    m_StopTime: {F(stopTime)}");
            sb.AppendLine($"    m_OrientationOffsetY: {F(orientationOffset)}");
            sb.AppendLine($"    m_Level: {F(level)}");
            sb.AppendLine($"    m_CycleOffset: {F(cycleOffset)}");
            sb.AppendLine("    m_HasAdditiveReferencePose: 0");
            sb.AppendLine($"    m_LoopTime: {loopTime}");
            sb.AppendLine($"    m_LoopBlend: {loopBlend}");
            sb.AppendLine($"    m_LoopBlendOrientation: {loopBlendOri}");
            sb.AppendLine($"    m_LoopBlendPositionY: {loopBlendPosY}");
            sb.AppendLine($"    m_LoopBlendPositionXZ: {loopBlendPosXZ}");
            sb.AppendLine($"    m_KeepOriginalOrientation: {keepOri}");
            sb.AppendLine($"    m_KeepOriginalPositionY: {keepPosY}");
            sb.AppendLine($"    m_KeepOriginalPositionXZ: {keepPosXZ}");
            sb.AppendLine($"    m_HeightFromFeet: {heightFromFeet}");
            sb.AppendLine($"    m_Mirror: {mirror}");

            sb.AppendLine("  m_EditorCurves: []");
            sb.AppendLine("  m_EulerEditorCurves: []");
            sb.AppendLine("  m_HasGenericRootTransform: 0");
            sb.AppendLine("  m_HasMotionFloatCurves: 0");

            if (clip.m_Events == null || clip.m_Events.Length == 0)
            {
                sb.AppendLine("  m_Events: []");
            }
            else
            {
                sb.AppendLine("  m_Events:");
                foreach (var ev in clip.m_Events)
                {
                    sb.AppendLine("  - time: " + F(ev.time));
                    sb.AppendLine("    functionName: " + YamlString(ev.functionName));
                    sb.AppendLine("    data: " + YamlString(ev.data));
                    sb.AppendLine("    objectReferenceParameter: {fileID: 0}");
                    sb.AppendLine("    floatParameter: " + F(ev.floatParameter));
                    sb.AppendLine("    intParameter: " + ev.intParameter);
                    sb.AppendLine("    messageOptions: " + ev.messageOptions);
                }
            }

            return sb.ToString();
        }

        private static uint GetCrc(string value)
        {
            if (string.IsNullOrEmpty(value)) return 0;
            var crc = new SevenZip.CRC();
            var bytes = Encoding.UTF8.GetBytes(value);
            crc.Update(bytes, 0, (uint)bytes.Length);
            return crc.GetDigest();
        }

        #endregion

        #region Skeleton
        private static void WriteSkeleton(StringBuilder sb, string indent, Skeleton? skeleton)
        {
            if (skeleton == null)
            {
                sb.AppendLine($"{indent}m_Node: []");
                sb.AppendLine($"{indent}m_ID: []");
                sb.AppendLine($"{indent}m_AxesArray: []");
                return;
            }

            if (skeleton.m_Node == null || skeleton.m_Node.Length == 0)
            {
                sb.AppendLine($"{indent}m_Node: []");
            }
            else
            {
                sb.AppendLine($"{indent}m_Node:");
                foreach (var node in skeleton.m_Node)
                {
                    sb.AppendLine($"{indent}- m_ParentId: {node.m_ParentId}");
                    sb.AppendLine($"{indent}  m_AxesId: {node.m_AxesId}");
                }
            }

            if (skeleton.m_ID == null || skeleton.m_ID.Length == 0)
            {
                sb.AppendLine($"{indent}m_ID: []");
            }
            else
            {
                sb.AppendLine($"{indent}m_ID:");
                foreach (var id in skeleton.m_ID)
                {
                    sb.AppendLine($"{indent}- {id}");
                }
            }

            if (skeleton.m_AxesArray == null || skeleton.m_AxesArray.Length == 0)
            {
                sb.AppendLine($"{indent}m_AxesArray: []");
            }
            else
            {
                sb.AppendLine($"{indent}m_AxesArray:");
                foreach (var axes in skeleton.m_AxesArray)
                {
                    sb.AppendLine($"{indent}- m_PreQ: {{x: {F(axes.m_PreQ.X)}, y: {F(axes.m_PreQ.Y)}, z: {F(axes.m_PreQ.Z)}, w: {F(axes.m_PreQ.W)}}}");
                    sb.AppendLine($"{indent}  m_PostQ: {{x: {F(axes.m_PostQ.X)}, y: {F(axes.m_PostQ.Y)}, z: {F(axes.m_PostQ.Z)}, w: {F(axes.m_PostQ.W)}}}");
                    if (axes.m_Sgn is Vector3 sgn3)
                    {
                        sb.AppendLine($"{indent}  m_Sgn: {{x: {F(sgn3.X)}, y: {F(sgn3.Y)}, z: {F(sgn3.Z)}}}");
                    }
                    else if (axes.m_Sgn is Vector4 sgn4)
                    {
                        sb.AppendLine($"{indent}  m_Sgn: {{x: {F(sgn4.X)}, y: {F(sgn4.Y)}, z: {F(sgn4.Z)}, w: {F(sgn4.W)}}}");
                    }
                    else
                    {
                        sb.AppendLine($"{indent}  m_Sgn: {{x: 0, y: 0, z: 0}}");
                    }
                    sb.AppendLine($"{indent}  m_Limit:");
                    if (axes.m_Limit != null)
                    {
                        if (axes.m_Limit.m_Min is Vector3 min3 && axes.m_Limit.m_Max is Vector3 max3)
                        {
                            sb.AppendLine($"{indent}    m_Min: {{x: {F(min3.X)}, y: {F(min3.Y)}, z: {F(min3.Z)}}}");
                            sb.AppendLine($"{indent}    m_Max: {{x: {F(max3.X)}, y: {F(max3.Y)}, z: {F(max3.Z)}}}");
                        }
                        else if (axes.m_Limit.m_Min is Vector4 min4 && axes.m_Limit.m_Max is Vector4 max4)
                        {
                            sb.AppendLine($"{indent}    m_Min: {{x: {F(min4.X)}, y: {F(min4.Y)}, z: {F(min4.Z)}, w: {F(min4.W)}}}");
                            sb.AppendLine($"{indent}    m_Max: {{x: {F(max4.X)}, y: {F(max4.Y)}, z: {F(max4.Z)}, w: {F(max4.W)}}}");
                        }
                        else
                        {
                            sb.AppendLine($"{indent}    m_Min: {{x: 0, y: 0, z: 0}}");
                            sb.AppendLine($"{indent}    m_Max: {{x: 0, y: 0, z: 0}}");
                        }
                    }
                    else
                    {
                        sb.AppendLine($"{indent}    m_Min: {{x: 0, y: 0, z: 0}}");
                        sb.AppendLine($"{indent}    m_Max: {{x: 0, y: 0, z: 0}}");
                    }
                    sb.AppendLine($"{indent}  m_Length: {F(axes.m_Length)}");
                    sb.AppendLine($"{indent}  m_Type: {axes.m_Type}");
                }
            }
        }

        private static void WriteSkeletonPose(StringBuilder sb, string indent, SkeletonPose? pose)
        {
            if (pose == null || pose.m_X == null || pose.m_X.Length == 0)
            {
                sb.AppendLine($"{indent}m_X: []");
                return;
            }
            sb.AppendLine($"{indent}m_X:");
            foreach (var xf in pose.m_X)
            {
                sb.AppendLine($"{indent}- t: {{x: {F(xf.t.X)}, y: {F(xf.t.Y)}, z: {F(xf.t.Z)}}}");
                sb.AppendLine($"{indent}  q: {{x: {F(xf.q.X)}, y: {F(xf.q.Y)}, z: {F(xf.q.Z)}, w: {F(xf.q.W)}}}");
                sb.AppendLine($"{indent}  s: {{x: {F(xf.s.X)}, y: {F(xf.s.Y)}, z: {F(xf.s.Z)}}}");
            }
        }

        private static void WriteHuman(StringBuilder sb, string indent, Human? human)
        {
            if (human == null)
            {
                sb.AppendLine($"{indent}m_RootX:");
                sb.AppendLine($"{indent}  t: {{x: 0, y: 0, z: 0}}");
                sb.AppendLine($"{indent}  q: {{x: 0, y: 0, z: 0, w: 1}}");
                sb.AppendLine($"{indent}  s: {{x: 1, y: 1, z: 1}}");
                sb.AppendLine($"{indent}m_Skeleton:");
                WriteSkeleton(sb, indent + "  ", null);
                sb.AppendLine($"{indent}m_SkeletonPose:");
                WriteSkeletonPose(sb, indent + "  ", null);
                sb.AppendLine($"{indent}m_LeftHand:");
                sb.AppendLine($"{indent}  m_HandBoneIndex: []");
                sb.AppendLine($"{indent}m_RightHand:");
                sb.AppendLine($"{indent}  m_HandBoneIndex: []");
                sb.AppendLine($"{indent}m_Handles: []");
                sb.AppendLine($"{indent}m_ColliderArray: []");
                sb.AppendLine($"{indent}m_HumanBoneIndex: []");
                sb.AppendLine($"{indent}m_HumanBoneMass: []");
                sb.AppendLine($"{indent}m_ColliderIndex: []");
                sb.AppendLine($"{indent}m_Scale: 1");
                sb.AppendLine($"{indent}m_ArmTwist: 0.5");
                sb.AppendLine($"{indent}m_ForeArmTwist: 0.5");
                sb.AppendLine($"{indent}m_UpperLegTwist: 0.5");
                sb.AppendLine($"{indent}m_LegTwist: 0.5");
                sb.AppendLine($"{indent}m_ArmStretch: 0.05");
                sb.AppendLine($"{indent}m_LegStretch: 0.05");
                sb.AppendLine($"{indent}m_FeetSpacing: 0");
                sb.AppendLine($"{indent}m_HasLeftHand: 0");
                sb.AppendLine($"{indent}m_HasRightHand: 0");
                sb.AppendLine($"{indent}m_HasTDoF: 0");
                return;
            }

            sb.AppendLine($"{indent}m_RootX:");
            sb.AppendLine($"{indent}  t: {{x: {F(human.m_RootX.t.X)}, y: {F(human.m_RootX.t.Y)}, z: {F(human.m_RootX.t.Z)}}}");
            sb.AppendLine($"{indent}  q: {{x: {F(human.m_RootX.q.X)}, y: {F(human.m_RootX.q.Y)}, z: {F(human.m_RootX.q.Z)}, w: {F(human.m_RootX.q.W)}}}");
            sb.AppendLine($"{indent}  s: {{x: {F(human.m_RootX.s.X)}, y: {F(human.m_RootX.s.Y)}, z: {F(human.m_RootX.s.Z)}}}");

            sb.AppendLine($"{indent}m_Skeleton:");
            WriteSkeleton(sb, indent + "  ", human.m_Skeleton);

            sb.AppendLine($"{indent}m_SkeletonPose:");
            WriteSkeletonPose(sb, indent + "  ", human.m_SkeletonPose);

            sb.AppendLine($"{indent}m_LeftHand:");
            if (human.m_LeftHand?.m_HandBoneIndex == null || human.m_LeftHand.m_HandBoneIndex.Length == 0)
                sb.AppendLine($"{indent}  m_HandBoneIndex: []");
            else
            {
                sb.AppendLine($"{indent}  m_HandBoneIndex:");
                foreach (var val in human.m_LeftHand.m_HandBoneIndex)
                    sb.AppendLine($"{indent}  - {val}");
            }

            sb.AppendLine($"{indent}m_RightHand:");
            if (human.m_RightHand?.m_HandBoneIndex == null || human.m_RightHand.m_HandBoneIndex.Length == 0)
                sb.AppendLine($"{indent}  m_HandBoneIndex: []");
            else
            {
                sb.AppendLine($"{indent}  m_HandBoneIndex:");
                foreach (var val in human.m_RightHand.m_HandBoneIndex)
                    sb.AppendLine($"{indent}  - {val}");
            }

            if (human.m_Handles == null || human.m_Handles.Length == 0)
            {
                sb.AppendLine($"{indent}m_Handles: []");
            }
            else
            {
                sb.AppendLine($"{indent}m_Handles:");
                foreach (var handle in human.m_Handles)
                {
                    sb.AppendLine($"{indent}- m_X:");
                    sb.AppendLine($"{indent}    t: {{x: {F(handle.m_X.t.X)}, y: {F(handle.m_X.t.Y)}, z: {F(handle.m_X.t.Z)}}}");
                    sb.AppendLine($"{indent}    q: {{x: {F(handle.m_X.q.X)}, y: {F(handle.m_X.q.Y)}, z: {F(handle.m_X.q.Z)}, w: {F(handle.m_X.q.W)}}}");
                    sb.AppendLine($"{indent}    s: {{x: {F(handle.m_X.s.X)}, y: {F(handle.m_X.s.Y)}, z: {F(handle.m_X.s.Z)}}}");
                    sb.AppendLine($"{indent}  m_ParentHumanIndex: {handle.m_ParentHumanIndex}");
                    sb.AppendLine($"{indent}  m_ID: {handle.m_ID}");
                }
            }

            if (human.m_ColliderArray == null || human.m_ColliderArray.Length == 0)
            {
                sb.AppendLine($"{indent}m_ColliderArray: []");
            }
            else
            {
                sb.AppendLine($"{indent}m_ColliderArray:");
                foreach (var col in human.m_ColliderArray)
                {
                    sb.AppendLine($"{indent}- m_X:");
                    sb.AppendLine($"{indent}    t: {{x: {F(col.m_X.t.X)}, y: {F(col.m_X.t.Y)}, z: {F(col.m_X.t.Z)}}}");
                    sb.AppendLine($"{indent}    q: {{x: {F(col.m_X.q.X)}, y: {F(col.m_X.q.Y)}, z: {F(col.m_X.q.Z)}, w: {F(col.m_X.q.W)}}}");
                    sb.AppendLine($"{indent}    s: {{x: {F(col.m_X.s.X)}, y: {F(col.m_X.s.Y)}, z: {F(col.m_X.s.Z)}}}");
                    sb.AppendLine($"{indent}  m_Type: {col.m_Type}");
                    sb.AppendLine($"{indent}  m_XMotionType: {col.m_XMotionType}");
                    sb.AppendLine($"{indent}  m_YMotionType: {col.m_YMotionType}");
                    sb.AppendLine($"{indent}  m_ZMotionType: {col.m_ZMotionType}");
                    sb.AppendLine($"{indent}  m_MinLimitX: {F(col.m_MinLimitX)}");
                    sb.AppendLine($"{indent}  m_MaxLimitX: {F(col.m_MaxLimitX)}");
                    sb.AppendLine($"{indent}  m_MaxLimitY: {F(col.m_MaxLimitY)}");
                    sb.AppendLine($"{indent}  m_MaxLimitZ: {F(col.m_MaxLimitZ)}");
                }
            }

            if (human.m_HumanBoneIndex == null || human.m_HumanBoneIndex.Length == 0)
                sb.AppendLine($"{indent}m_HumanBoneIndex: []");
            else
            {
                sb.AppendLine($"{indent}m_HumanBoneIndex:");
                foreach (var idx in human.m_HumanBoneIndex)
                    sb.AppendLine($"{indent}- {idx}");
            }

            if (human.m_HumanBoneMass == null || human.m_HumanBoneMass.Length == 0)
                sb.AppendLine($"{indent}m_HumanBoneMass: []");
            else
            {
                sb.AppendLine($"{indent}m_HumanBoneMass:");
                foreach (var mass in human.m_HumanBoneMass)
                    sb.AppendLine($"{indent}- {F(mass)}");
            }

            if (human.m_ColliderIndex == null || human.m_ColliderIndex.Length == 0)
                sb.AppendLine($"{indent}m_ColliderIndex: []");
            else
            {
                sb.AppendLine($"{indent}m_ColliderIndex:");
                foreach (var idx in human.m_ColliderIndex)
                    sb.AppendLine($"{indent}- {idx}");
            }

            sb.AppendLine($"{indent}m_Scale: {F(human.m_Scale)}");
            sb.AppendLine($"{indent}m_ArmTwist: {F(human.m_ArmTwist)}");
            sb.AppendLine($"{indent}m_ForeArmTwist: {F(human.m_ForeArmTwist)}");
            sb.AppendLine($"{indent}m_UpperLegTwist: {F(human.m_UpperLegTwist)}");
            sb.AppendLine($"{indent}m_LegTwist: {F(human.m_LegTwist)}");
            sb.AppendLine($"{indent}m_ArmStretch: {F(human.m_ArmStretch)}");
            sb.AppendLine($"{indent}m_LegStretch: {F(human.m_LegStretch)}");
            sb.AppendLine($"{indent}m_FeetSpacing: {F(human.m_FeetSpacing)}");
            sb.AppendLine($"{indent}m_HasLeftHand: {(human.m_HasLeftHand ? 1 : 0)}");
            sb.AppendLine($"{indent}m_HasRightHand: {(human.m_HasRightHand ? 1 : 0)}");
            sb.AppendLine($"{indent}m_HasTDoF: {(human.m_HasTDoF ? 1 : 0)}");
        }

        public static bool ExportAvatar(Avatar avatar, string avatarFullPath)
        {
            if (File.Exists(avatarFullPath)) return false;

            var sb = new StringBuilder();
            sb.AppendLine("%YAML 1.1");
            sb.AppendLine("%TAG !u! tag:unity3d.com,2011:");
            sb.AppendLine("--- !u!90 &9000000");
            sb.AppendLine("Avatar:");
            sb.AppendLine("  m_ObjectHideFlags: 0");
            sb.AppendLine("  m_CorrespondingSourceObject: {fileID: 0}");
            sb.AppendLine("  m_PrefabInstance: {fileID: 0}");
            sb.AppendLine("  m_PrefabAsset: {fileID: 0}");
            sb.AppendLine($"  m_Name: {YamlString(avatar.m_Name)}");
            sb.AppendLine($"  m_AvatarSize: {avatar.m_AvatarSize}");

            var m_Avatar = avatar.m_Avatar;
            if (m_Avatar == null)
            {
                sb.AppendLine("  m_Avatar: {}");
            }
            else
            {
                sb.AppendLine("  m_Avatar:");
                sb.AppendLine("    m_AvatarSkeleton:");
                WriteSkeleton(sb, "      ", m_Avatar.m_AvatarSkeleton);
                sb.AppendLine("    m_AvatarSkeletonPose:");
                WriteSkeletonPose(sb, "      ", m_Avatar.m_AvatarSkeletonPose);
                sb.AppendLine("    m_DefaultPose:");
                WriteSkeletonPose(sb, "      ", m_Avatar.m_DefaultPose);

                if (m_Avatar.m_SkeletonNameIDArray == null || m_Avatar.m_SkeletonNameIDArray.Length == 0)
                    sb.AppendLine("    m_SkeletonNameIDArray: []");
                else
                {
                    sb.AppendLine("    m_SkeletonNameIDArray:");
                    foreach (var val in m_Avatar.m_SkeletonNameIDArray)
                        sb.AppendLine($"    - {val}");
                }

                sb.AppendLine("    m_Human:");
                WriteHuman(sb, "      ", m_Avatar.m_Human);

                if (m_Avatar.m_HumanSkeletonIndexArray == null || m_Avatar.m_HumanSkeletonIndexArray.Length == 0)
                    sb.AppendLine("    m_HumanSkeletonIndexArray: []");
                else
                {
                    sb.AppendLine("    m_HumanSkeletonIndexArray:");
                    foreach (var val in m_Avatar.m_HumanSkeletonIndexArray)
                        sb.AppendLine($"    - {val}");
                }

                if (m_Avatar.m_HumanSkeletonReverseIndexArray == null || m_Avatar.m_HumanSkeletonReverseIndexArray.Length == 0)
                    sb.AppendLine("    m_HumanSkeletonReverseIndexArray: []");
                else
                {
                    sb.AppendLine("    m_HumanSkeletonReverseIndexArray:");
                    foreach (var val in m_Avatar.m_HumanSkeletonReverseIndexArray)
                        sb.AppendLine($"    - {val}");
                }

                sb.AppendLine($"    m_RootMotionBoneIndex: {m_Avatar.m_RootMotionBoneIndex}");
                sb.AppendLine("    m_RootMotionBoneX:");
                sb.AppendLine($"      t: {{x: {F(m_Avatar.m_RootMotionBoneX.t.X)}, y: {F(m_Avatar.m_RootMotionBoneX.t.Y)}, z: {F(m_Avatar.m_RootMotionBoneX.t.Z)}}}");
                sb.AppendLine($"      q: {{x: {F(m_Avatar.m_RootMotionBoneX.q.X)}, y: {F(m_Avatar.m_RootMotionBoneX.q.Y)}, z: {F(m_Avatar.m_RootMotionBoneX.q.Z)}, w: {F(m_Avatar.m_RootMotionBoneX.q.W)}}}");
                sb.AppendLine($"      s: {{x: {F(m_Avatar.m_RootMotionBoneX.s.X)}, y: {F(m_Avatar.m_RootMotionBoneX.s.Y)}, z: {F(m_Avatar.m_RootMotionBoneX.s.Z)}}}");

                sb.AppendLine("    m_RootMotionSkeleton:");
                WriteSkeleton(sb, "      ", m_Avatar.m_RootMotionSkeleton);

                sb.AppendLine("    m_RootMotionSkeletonPose:");
                WriteSkeletonPose(sb, "      ", m_Avatar.m_RootMotionSkeletonPose);

                if (m_Avatar.m_RootMotionSkeletonIndexArray == null || m_Avatar.m_RootMotionSkeletonIndexArray.Length == 0)
                    sb.AppendLine("    m_RootMotionSkeletonIndexArray: []");
                else
                {
                    sb.AppendLine("    m_RootMotionSkeletonIndexArray:");
                    foreach (var val in m_Avatar.m_RootMotionSkeletonIndexArray)
                        sb.AppendLine($"    - {val}");
                }
            }

            if (avatar.m_TOS == null || avatar.m_TOS.Length == 0)
            {
                sb.AppendLine("  m_TOS: []");
            }
            else
            {
                sb.AppendLine("  m_TOS:");
                foreach (var pair in avatar.m_TOS)
                {
                    sb.AppendLine($"  - first: {pair.Key}");
                    sb.AppendLine($"    second: {YamlString(pair.Value)}");
                }
            }

            sb.AppendLine("  m_HumanDescription:");
            sb.AppendLine("    m_Human: []");
            if (m_Avatar?.m_AvatarSkeleton?.m_Node == null || m_Avatar.m_AvatarSkeleton.m_Node.Length == 0)
            {
                sb.AppendLine("    m_Skeleton: []");
            }
            else
            {
                sb.AppendLine("    m_Skeleton:");
                for (int i = 0; i < m_Avatar.m_AvatarSkeleton.m_Node.Length; i++)
                {
                    var node = m_Avatar.m_AvatarSkeleton.m_Node[i];
                    var nameHash = m_Avatar.m_AvatarSkeleton.m_ID[i];
                    var path = avatar.FindBonePath(nameHash) ?? string.Empty;
                    var name = path;
                    var slashIdx = path.LastIndexOf('/');
                    if (slashIdx >= 0)
                    {
                        name = path.Substring(slashIdx + 1);
                    }
                    if (string.IsNullOrEmpty(name))
                    {
                        name = i == 0 ? (avatar.m_Name.EndsWith("Avatar") ? avatar.m_Name.Substring(0, avatar.m_Name.Length - 6) : avatar.m_Name) : $"Bone_{i}";
                    }

                    var parentName = string.Empty;
                    if (node.m_ParentId >= 0 && node.m_ParentId < m_Avatar.m_AvatarSkeleton.m_Node.Length)
                    {
                        var parentHash = m_Avatar.m_AvatarSkeleton.m_ID[node.m_ParentId];
                        var parentPath = avatar.FindBonePath(parentHash) ?? string.Empty;
                        parentName = parentPath;
                        var pSlashIdx = parentPath.LastIndexOf('/');
                        if (pSlashIdx >= 0)
                        {
                            parentName = parentPath.Substring(pSlashIdx + 1);
                        }
                        if (string.IsNullOrEmpty(parentName))
                        {
                            parentName = node.m_ParentId == 0 ? (avatar.m_Name.EndsWith("Avatar") ? avatar.m_Name.Substring(0, avatar.m_Name.Length - 6) : avatar.m_Name) : $"Bone_{node.m_ParentId}";
                        }
                    }

                    var pos = Vector3.Zero;
                    var rot = new Quaternion(0, 0, 0, 1);
                    var scale = Vector3.One;
                    if (m_Avatar.m_AvatarSkeletonPose?.m_X != null && i < m_Avatar.m_AvatarSkeletonPose.m_X.Length)
                    {
                        var xf = m_Avatar.m_AvatarSkeletonPose.m_X[i];
                        pos = xf.t;
                        rot = xf.q;
                        scale = xf.s;
                    }

                    sb.AppendLine("    - m_Name: " + YamlString(name));
                    sb.AppendLine("      m_ParentName: " + YamlString(parentName));
                    sb.AppendLine($"      m_Position: {{x: {F(pos.X)}, y: {F(pos.Y)}, z: {F(pos.Z)}}}");
                    sb.AppendLine($"      m_Rotation: {{x: {F(rot.X)}, y: {F(rot.Y)}, z: {F(rot.Z)}, w: {F(rot.W)}}}");
                    sb.AppendLine($"      m_Scale: {{x: {F(scale.X)}, y: {F(scale.Y)}, z: {F(scale.Z)}}}");
                }
            }

            sb.AppendLine("    m_ArmTwist: 0.5");
            sb.AppendLine("    m_ForeArmTwist: 0.5");
            sb.AppendLine("    m_UpperLegTwist: 0.5");
            sb.AppendLine("    m_LegTwist: 0.5");
            sb.AppendLine("    m_ArmStretch: 0.05");
            sb.AppendLine("    m_LegStretch: 0.05");
            sb.AppendLine("    m_FeetSpacing: 0");
            sb.AppendLine("    m_GlobalScale: 1");
            string rootMotionBoneName = string.Empty;
            if (m_Avatar != null && m_Avatar.m_RootMotionBoneIndex >= 0 && m_Avatar.m_RootMotionBoneIndex < m_Avatar.m_AvatarSkeleton?.m_Node.Length)
            {
                var hash = m_Avatar.m_AvatarSkeleton.m_ID[m_Avatar.m_RootMotionBoneIndex];
                var rootPath = avatar.FindBonePath(hash) ?? string.Empty;
                rootMotionBoneName = rootPath;
                var slashIdx = rootPath.LastIndexOf('/');
                if (slashIdx >= 0)
                {
                    rootMotionBoneName = rootPath.Substring(slashIdx + 1);
                }
            }
            sb.AppendLine("    m_RootMotionBoneName: " + YamlString(rootMotionBoneName));
            sb.AppendLine("    m_HasTranslationDoF: 0");
            sb.AppendLine("    m_HasExtraRoot: 1");
            sb.AppendLine("    m_SkeletonHasParents: 1");

            EnsureParentDirectory(avatarFullPath);
            File.WriteAllText(avatarFullPath, sb.ToString(), Encoding.UTF8);
            return true;
        }

        public static bool ExportAnimatorController(AnimatorController controller, string exportFullPath)
        {
            if (File.Exists(exportFullPath)) return false;

            var sb = new StringBuilder();
            sb.AppendLine("%YAML 1.1");
            sb.AppendLine("%TAG !u! tag:unity3d.com,2011:");
            sb.AppendLine("--- !u!91 &9100000");
            sb.AppendLine("AnimatorController:");
            sb.AppendLine("  m_ObjectHideFlags: 0");
            sb.AppendLine("  m_CorrespondingSourceObject: {fileID: 0}");
            sb.AppendLine("  m_PrefabInstance: {fileID: 0}");
            sb.AppendLine("  m_PrefabAsset: {fileID: 0}");
            sb.AppendLine($"  m_Name: {YamlString(controller.m_Name)}");
            sb.AppendLine("  serializedVersion: 5");
            sb.AppendLine("  m_AnimatorParameters: []");
            sb.AppendLine("  m_AnimatorLayers: []");
            if (controller.m_AnimationClips != null && controller.m_AnimationClips.Length > 0)
            {
                sb.AppendLine("  # Animation Clips referenced by this controller:");
                foreach (var clipPtr in controller.m_AnimationClips)
                {
                    if (clipPtr.TryGet(out var clip))
                    {
                        sb.AppendLine($"  # - Name: {clip.m_Name}, PathID: {clipPtr.m_PathID}");
                    }
                }
            }

            EnsureParentDirectory(exportFullPath);
            File.WriteAllText(exportFullPath, sb.ToString(), Encoding.UTF8);
            return true;
        }

        public static bool ExportAnimatorOverrideController(AnimatorOverrideController overrideController, string exportFullPath)
        {
            if (File.Exists(exportFullPath)) return false;

            var sb = new StringBuilder();
            sb.AppendLine("%YAML 1.1");
            sb.AppendLine("%TAG !u! tag:unity3d.com,2011:");
            sb.AppendLine("--- !u!221 &22100000");
            sb.AppendLine("AnimatorOverrideController:");
            sb.AppendLine("  m_ObjectHideFlags: 0");
            sb.AppendLine("  m_CorrespondingSourceObject: {fileID: 0}");
            sb.AppendLine("  m_PrefabInstance: {fileID: 0}");
            sb.AppendLine("  m_PrefabAsset: {fileID: 0}");
            sb.AppendLine($"  m_Name: {YamlString(overrideController.m_Name)}");
            if (overrideController.m_Controller.TryGet(out var baseController))
            {
                sb.AppendLine($"  m_Controller: {{fileID: 0}} # Base Controller: {baseController.m_Name}");
            }
            else
            {
                sb.AppendLine("  m_Controller: {fileID: 0}");
            }
            sb.AppendLine("  m_Clips:");
            if (overrideController.m_Clips != null)
            {
                foreach (var clipOverride in overrideController.m_Clips)
                {
                    string origName = clipOverride.m_OriginalClip.TryGet(out var origClip) ? origClip.m_Name : "None";
                    string overrideName = clipOverride.m_OverrideClip.TryGet(out var overClip) ? overClip.m_Name : "None";
                    sb.AppendLine($"  - m_OriginalClip: {{fileID: 0}} # {origName}");
                    sb.AppendLine($"    m_OverrideClip: {{fileID: 0}} # {overrideName}");
                }
            }

            EnsureParentDirectory(exportFullPath);
            File.WriteAllText(exportFullPath, sb.ToString(), Encoding.UTF8);
            return true;
        }
        #endregion

        private static void EnsureParentDirectory(string filePath)
        {
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }

        #region Unity Model and Animation Helpers
        public static void WriteModelMetaIfMissing(string modelFbxPath, Object? obj = null)
        {
            WriteModelMetaIfMissing(modelFbxPath, obj, imported: null, avatar: obj as Avatar);
        }

        public static void WriteModelMetaIfMissing(string modelFbxPath, Object? obj, IImported? imported, Avatar? avatar = null)
        {
            var metaPath = modelFbxPath + ".meta";
            if (File.Exists(metaPath)) return;
            var guid = GenerateStableGuid(obj);
            var resolvedAvatar = avatar ?? obj as Avatar ?? (imported as ModelConverter)?.SourceAvatar;
            int animType = DetermineModelAnimationType(imported, resolvedAvatar, isAnimationClipFbx: false);
            var metaContent = BuildModelMeta(guid, animType, importAnimation: imported?.AnimationList?.Count > 0, isAnimationClipFbx: false, resolvedAvatar);
            File.WriteAllText(metaPath, metaContent, Encoding.UTF8);
        }

        public static void WriteAnimationMetaIfMissing(string animFbxPath, Object? obj = null)
        {
            WriteAnimationMetaIfMissing(animFbxPath, obj, imported: null, avatar: obj as Avatar);
        }

        public static void WriteAnimationMetaIfMissing(string animFbxPath, Object? obj, IImported? imported, Avatar? avatar = null)
        {
            var metaPath = animFbxPath + ".meta";
            if (File.Exists(metaPath)) return;
            var guid = GenerateStableGuid(obj);
            var resolvedAvatar = avatar ?? obj as Avatar ?? (imported as ModelConverter)?.SourceAvatar;
            int animType = DetermineModelAnimationType(imported, resolvedAvatar, isAnimationClipFbx: true);
            var metaContent = BuildModelMeta(guid, animType, importAnimation: true, isAnimationClipFbx: true, resolvedAvatar);
            File.WriteAllText(metaPath, metaContent, Encoding.UTF8);
        }

        public static bool IsHumanoidAvatar(Avatar? avatar)
        {
            var human = avatar?.m_Avatar?.m_Human;
            if (human?.m_HumanBoneIndex == null || human.m_HumanBoneIndex.Length == 0)
            {
                return false;
            }
            for (int i = 0; i < human.m_HumanBoneIndex.Length; i++)
            {
                if (human.m_HumanBoneIndex[i] >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        private static int DetermineModelAnimationType(IImported? imported, Avatar? avatar, bool isAnimationClipFbx)
        {
            // 3 = Humanoid ONLY when a genuine Humanoid Avatar is present
            if (IsHumanoidAvatar(avatar))
            {
                return 3;
            }

            if (isAnimationClipFbx || avatar != null)
            {
                return 2; // Generic
            }

            if (imported != null)
            {
                bool hasBones = imported.MeshList != null && imported.MeshList.Any(m => m?.BoneList != null && m.BoneList.Count > 0);
                bool hasAnims = imported.AnimationList != null && imported.AnimationList.Count > 0;
                if (hasBones || hasAnims)
                {
                    return 2; // Generic
                }
            }

            return 0; // None (static mesh)
        }

        private static string BuildModelMeta(string guid, int animationType, bool importAnimation, bool isAnimationClipFbx, Avatar? avatar)
        {
            var sb = new StringBuilder();
            sb.AppendLine("fileFormatVersion: 2");
            sb.AppendLine($"guid: {guid}");
            sb.AppendLine("ModelImporter:");
            sb.AppendLine("  serializedVersion: 24501");
            sb.AppendLine("  internalIDToNameTable: []");
            sb.AppendLine("  externalObjects: {}");
            sb.AppendLine("  materials:");
            sb.AppendLine($"    materialImportMode: {(isAnimationClipFbx ? 0 : 2)}");
            sb.AppendLine("    materialName: 0");
            sb.AppendLine("    materialSearch: 1");
            sb.AppendLine("    materialLocation: 1");
            sb.AppendLine("    searchTexturesGlobally: 0");
            sb.AppendLine("  animations:");
            sb.AppendLine("    legacyGenerateAnimations: 4");
            sb.AppendLine("    bakeSimulation: 0");
            sb.AppendLine("    resampleCurves: 1");
            sb.AppendLine("    optimizeGameObjects: 0");
            sb.AppendLine("    removeConstantScaleCurves: 0");
            sb.AppendLine("    motionNodeName: ");
            sb.AppendLine("    animationImportErrors: ");
            sb.AppendLine("    animationImportWarnings: ");
            sb.AppendLine("    animationRetargetingWarnings: ");
            sb.AppendLine("    animationDoRetargetingWarnings: 0");
            sb.AppendLine("    importAnimatedCustomProperties: 0");
            sb.AppendLine("    importConstraints: 0");
            sb.AppendLine($"    animationCompression: {(isAnimationClipFbx ? 1 : 3)}");
            sb.AppendLine("    animationRotationError: 0.5");
            sb.AppendLine("    animationPositionError: 0.5");
            sb.AppendLine("    animationScaleError: 0.5");
            sb.AppendLine("    animationWrapMode: 0");
            sb.AppendLine("    extraExposedTransformPaths: []");
            sb.AppendLine("    extraUserProperties: []");
            sb.AppendLine("    clipAnimations: []");
            sb.AppendLine("    isReadable: 0");
            sb.AppendLine("  meshes:");
            sb.AppendLine("    lODScreenPercentages: []");
            sb.AppendLine("    globalScale: 1");
            sb.AppendLine("    meshCompression: 0");
            sb.AppendLine("    addColliders: 0");
            sb.AppendLine("    useSRGBMaterialColor: 1");
            sb.AppendLine("    sortHierarchyByName: 1");
            sb.AppendLine("    importPhysicalCameras: 1");
            sb.AppendLine("    importVisibility: 1");
            sb.AppendLine("    importBlendShapes: 1");
            sb.AppendLine("    importCameras: 1");
            sb.AppendLine("    importLights: 1");
            sb.AppendLine("    nodeNameCollisionStrategy: 1");
            sb.AppendLine("    fileIdsGeneration: 2");
            sb.AppendLine("    swapUVChannels: 0");
            sb.AppendLine("    generateSecondaryUV: 0");
            sb.AppendLine("    useFileUnits: 1");
            sb.AppendLine("    keepQuads: 0");
            sb.AppendLine("    weldVertices: 1");
            sb.AppendLine("    bakeAxisConversion: 0");
            sb.AppendLine("    preserveHierarchy: 0");
            sb.AppendLine("    skinWeightsMode: 0");
            sb.AppendLine("    maxBonesPerVertex: 4");
            sb.AppendLine("    minBoneWeight: 0.001");
            sb.AppendLine("    optimizeBones: 1");
            sb.AppendLine("    generateMeshLods: 0");
            sb.AppendLine("    meshLodGenerationFlags: 0");
            sb.AppendLine("    maximumMeshLod: -1");
            sb.AppendLine("    importUVs: -1");
            sb.AppendLine("    importVertexColors: 1");
            sb.AppendLine("    meshOptimizationFlags: -1");
            sb.AppendLine("    indexFormat: 0");
            sb.AppendLine("    secondaryUVAngleDistortion: 8");
            sb.AppendLine("    secondaryUVAreaDistortion: 15.000001");
            sb.AppendLine("    secondaryUVHardAngle: 88");
            sb.AppendLine("    secondaryUVMarginMethod: 1");
            sb.AppendLine("    secondaryUVMinLightmapResolution: 40");
            sb.AppendLine("    secondaryUVMinObjectScale: 1");
            sb.AppendLine("    secondaryUVPackMargin: 4");
            sb.AppendLine("    useFileScale: 1");
            sb.AppendLine("    strictVertexDataChecks: 0");
            sb.AppendLine("  tangentSpace:");
            sb.AppendLine("    normalSmoothAngle: 60");
            sb.AppendLine("    normalImportMode: 0");
            sb.AppendLine("    tangentImportMode: 3");
            sb.AppendLine("    normalCalculationMode: 4");
            sb.AppendLine("    legacyComputeAllNormalsFromSmoothingGroupsWhenMeshHasBlendShapes: 0");
            sb.AppendLine("    blendShapeNormalImportMode: 1");
            sb.AppendLine("    normalSmoothingSource: 0");
            sb.AppendLine("    calculateBlendshapeNormalsDeltaFromImportedNormals: 0");
            sb.AppendLine("  referencedClips: []");
            sb.AppendLine($"  importAnimation: {(importAnimation ? 1 : 0)}");
            sb.AppendLine("  humanDescription:");
            sb.AppendLine("    serializedVersion: 3");
            AppendHumanDescriptionYaml(sb, animationType == 3 ? avatar : null);
            sb.AppendLine("  lastHumanDescriptionAvatarSource: {instanceID: 0}");
            sb.AppendLine("  autoGenerateAvatarMappingIfUnspecified: 1");
            sb.AppendLine($"  animationType: {animationType}");
            sb.AppendLine("  humanoidOversampling: 1");
            sb.AppendLine($"  avatarSetup: {(animationType == 0 ? 0 : 1)}");
            sb.AppendLine("  addHumanoidExtraRootOnlyWhenUsingAvatar: 1");
            sb.AppendLine("  importBlendShapeDeformPercent: 1");
            sb.AppendLine("  remapMaterialsIfMaterialImportModeIsNone: 0");
            sb.AppendLine("  additionalBone: 0");
            sb.AppendLine("  userData: ");
            sb.AppendLine("  assetBundleName: ");
            sb.AppendLine("  assetBundleVariant: ");
            return sb.ToString();
        }

        private static void AppendHumanDescriptionYaml(StringBuilder sb, Avatar? avatar)
        {
            var desc = avatar?.m_HumanDescription;
            if (desc?.m_Human != null && desc.m_Human.Length > 0)
            {
                sb.AppendLine("    human:");
                foreach (var hb in desc.m_Human)
                {
                    sb.AppendLine($"    - boneName: {YamlString(hb.m_BoneName)}");
                    sb.AppendLine($"      humanName: {YamlString(hb.m_HumanName)}");
                    var l = hb.m_Limit;
                    if (l != null)
                    {
                        sb.AppendLine($"      limit: {{min: {{x: {F(l.m_Min.X)}, y: {F(l.m_Min.Y)}, z: {F(l.m_Min.Z)}}}, max: {{x: {F(l.m_Max.X)}, y: {F(l.m_Max.Y)}, z: {F(l.m_Max.Z)}}}, value: {{x: {F(l.m_Value.X)}, y: {F(l.m_Value.Y)}, z: {F(l.m_Value.Z)}}}, length: {F(l.m_Length)}, modified: {(l.m_Modified ? 1 : 0)}}}");
                    }
                    else
                    {
                        sb.AppendLine("      limit: {min: {x: 0, y: 0, z: 0}, max: {x: 0, y: 0, z: 0}, value: {x: 0, y: 0, z: 0}, length: 0, modified: 0}");
                    }
                }
                sb.AppendLine("    skeleton: []");
                sb.AppendLine($"    armTwist: {F(desc.m_ArmTwist)}");
                sb.AppendLine($"    foreArmTwist: {F(desc.m_ForeArmTwist)}");
                sb.AppendLine($"    upperLegTwist: {F(desc.m_UpperLegTwist)}");
                sb.AppendLine($"    legTwist: {F(desc.m_LegTwist)}");
                sb.AppendLine($"    armStretch: {F(desc.m_ArmStretch)}");
                sb.AppendLine($"    legStretch: {F(desc.m_LegStretch)}");
                sb.AppendLine($"    feetSpacing: {F(desc.m_FeetSpacing)}");
                sb.AppendLine("    globalScale: 1");
                sb.AppendLine($"    rootMotionBoneName: {YamlString(desc.m_RootMotionBoneName)}");
                sb.AppendLine($"    hasTranslationDoF: {(desc.m_HasTranslationDoF ? 1 : 0)}");
                sb.AppendLine($"    hasExtraRoot: {(desc.m_HasExtraRoot ? 1 : 0)}");
                sb.AppendLine($"    skeletonHasParents: {(desc.m_SkeletonHasParents ? 1 : 0)}");
                return;
            }

            var human = avatar?.m_Avatar?.m_Human;
            var avSkel = avatar?.m_Avatar?.m_AvatarSkeleton;
            var hIdxArr = avatar?.m_Avatar?.m_HumanSkeletonIndexArray;
            var mappedBones = new List<(string BoneName, string HumanName)>();

            if (avatar != null && human?.m_HumanBoneIndex != null && avSkel?.m_ID != null && hIdxArr != null)
            {
                string? ResolveLeafBoneName(int hSkelIdx)
                {
                    if (hSkelIdx < 0 || hSkelIdx >= hIdxArr.Length) return null;
                    int avNode = hIdxArr[hSkelIdx];
                    if (avNode < 0 || avNode >= avSkel.m_ID.Length) return null;
                    var path = avatar.FindBonePath(avSkel.m_ID[avNode]);
                    if (string.IsNullOrEmpty(path)) return null;
                    int slash = path.LastIndexOf('/');
                    return slash >= 0 ? path.Substring(slash + 1) : path;
                }

                // Hips
                var hipsLeaf = ResolveLeafBoneName(human.m_HumanBoneIndex[0]);
                if (!string.IsNullOrEmpty(hipsLeaf))
                {
                    mappedBones.Add((hipsLeaf, "Hips"));
                }

                bool isLegacy24 = human.m_HumanBoneIndex.Length < 25;
                foreach (var def in HumanoidMuscleDecoder.BodyBoneDefs)
                {
                    int bodyIdx = isLegacy24 ? def.BodyIndexLegacy : def.BodyIndex56;
                    if (bodyIdx < 0 || bodyIdx >= human.m_HumanBoneIndex.Length) continue;
                    var leaf = ResolveLeafBoneName(human.m_HumanBoneIndex[bodyIdx]);
                    if (!string.IsNullOrEmpty(leaf))
                    {
                        mappedBones.Add((leaf, def.UnityName));
                    }
                }

                if (human.m_LeftHand?.m_HandBoneIndex != null)
                {
                    int count = Math.Min(human.m_LeftHand.m_HandBoneIndex.Length, HumanoidMuscleDecoder.LeftHandBoneNames.Length);
                    for (int i = 0; i < count; i++)
                    {
                        var leaf = ResolveLeafBoneName(human.m_LeftHand.m_HandBoneIndex[i]);
                        if (!string.IsNullOrEmpty(leaf))
                        {
                            mappedBones.Add((leaf, HumanoidMuscleDecoder.LeftHandBoneNames[i]));
                        }
                    }
                }

                if (human.m_RightHand?.m_HandBoneIndex != null)
                {
                    int count = Math.Min(human.m_RightHand.m_HandBoneIndex.Length, HumanoidMuscleDecoder.RightHandBoneNames.Length);
                    for (int i = 0; i < count; i++)
                    {
                        var leaf = ResolveLeafBoneName(human.m_RightHand.m_HandBoneIndex[i]);
                        if (!string.IsNullOrEmpty(leaf))
                        {
                            mappedBones.Add((leaf, HumanoidMuscleDecoder.RightHandBoneNames[i]));
                        }
                    }
                }
            }

            if (mappedBones.Count == 0)
            {
                sb.AppendLine("    human: []");
            }
            else
            {
                sb.AppendLine("    human:");
                foreach (var (boneName, humanName) in mappedBones)
                {
                    sb.AppendLine($"    - boneName: {YamlString(boneName)}");
                    sb.AppendLine($"      humanName: {YamlString(humanName)}");
                    sb.AppendLine("      limit: {min: {x: 0, y: 0, z: 0}, max: {x: 0, y: 0, z: 0}, value: {x: 0, y: 0, z: 0}, length: 0, modified: 0}");
                }
            }

            sb.AppendLine("    skeleton: []");
            sb.AppendLine($"    armTwist: {F(human?.m_ArmTwist ?? 0.5f)}");
            sb.AppendLine($"    foreArmTwist: {F(human?.m_ForeArmTwist ?? 0.5f)}");
            sb.AppendLine($"    upperLegTwist: {F(human?.m_UpperLegTwist ?? 0.5f)}");
            sb.AppendLine($"    legTwist: {F(human?.m_LegTwist ?? 0.5f)}");
            sb.AppendLine($"    armStretch: {F(human?.m_ArmStretch ?? 0.05f)}");
            sb.AppendLine($"    legStretch: {F(human?.m_LegStretch ?? 0.05f)}");
            sb.AppendLine($"    feetSpacing: {F(human?.m_FeetSpacing ?? 0f)}");
            sb.AppendLine("    globalScale: 1");
            sb.AppendLine("    rootMotionBoneName: ");
            sb.AppendLine($"    hasTranslationDoF: {(human?.m_HasTDoF == true ? 1 : 0)}");
            sb.AppendLine("    hasExtraRoot: 0");
            sb.AppendLine("    skeletonHasParents: 1");
        }

        public static void ExportUnityAnimationClip(ImportedKeyframedAnimation anim, string outputPath)
        {
            ExportUnityAnimationClip(anim, outputPath, rootFrameName: null);
        }

        public static void ExportUnityAnimationClip(ImportedKeyframedAnimation anim, string outputPath, string? rootFrameName)
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();

            string? effectiveRootPrefix = !string.IsNullOrWhiteSpace(rootFrameName)
                ? rootFrameName.Replace('\\', '/').Trim('/')
                : InferCommonRootPrefix(anim.TrackList);

            string ToUnityRelativePath(string? rawPath)
            {
                var p = (rawPath ?? string.Empty).Replace('\\', '/').Trim('/');
                if (string.IsNullOrEmpty(p) || string.IsNullOrEmpty(effectiveRootPrefix))
                {
                    return p;
                }
                if (string.Equals(p, effectiveRootPrefix, StringComparison.Ordinal))
                {
                    return string.Empty;
                }
                if (p.StartsWith(effectiveRootPrefix + "/", StringComparison.Ordinal))
                {
                    return p.Substring(effectiveRootPrefix.Length + 1);
                }
                return p;
            }

            var genericBindings = new List<(uint PathHash, uint Attribute, int TypeId, int CustomType)>();
            bool hasGenericRootTransform = false;
            float maxStopTime = 0f;

            sb.AppendLine("%YAML 1.1");
            sb.AppendLine("%TAG !u! tag:unity3d.com,2011:");
            sb.AppendLine("--- !u!74 &7400000");
            sb.AppendLine("AnimationClip:");
            sb.AppendLine("  m_ObjectHideFlags: 0");
            sb.AppendLine("  m_CorrespondingSourceObject: {fileID: 0}");
            sb.AppendLine("  m_PrefabInstance: {fileID: 0}");
            sb.AppendLine("  m_PrefabAsset: {fileID: 0}");
            sb.AppendLine($"  m_Name: {YamlString(anim.Name)}");
            sb.AppendLine("  serializedVersion: 7");
            sb.AppendLine("  m_Legacy: 0");
            sb.AppendLine("  m_Compressed: 0");
            sb.AppendLine("  m_UseHighQualityCurve: 1");

            // Convert FBX right-handed eEulerXYZ rotations back into Unity left-handed quaternion m_RotationCurves
            // (eliminates Euler-order and gimbal ambiguity completely in Unity!)
            var rotTracks = anim.TrackList?.Where(t => t?.Rotations != null && t.Rotations.Count > 0).ToList();
            if (rotTracks == null || rotTracks.Count == 0)
            {
                sb.AppendLine("  m_RotationCurves: []");
            }
            else
            {
                sb.AppendLine("  m_RotationCurves:");
                foreach (var track in rotTracks)
                {
                    var relPath = ToUnityRelativePath(track.Path);
                    if (string.IsNullOrEmpty(relPath)) hasGenericRootTransform = true;
                    genericBindings.Add((ComputeUnityCrc32(relPath), 2u, 4, 0));

                    sb.AppendLine("  - curve:");
                    sb.AppendLine("      serializedVersion: 2");
                    sb.AppendLine("      m_Curve:");
                    Quaternion? prevQ = null;
                    foreach (var k in track.Rotations)
                    {
                        if (k.time > maxStopTime) maxStopTime = k.time;
                        var qU = HumanoidMuscleDecoder.FbxEulerXYZDegreesToUnityQuaternion(k.value);
                        // Keep quaternion hemisphere continuous for smooth Unity SLERP/cubic interpolation
                        if (prevQ.HasValue)
                        {
                            float dot = prevQ.Value.X * qU.X + prevQ.Value.Y * qU.Y + prevQ.Value.Z * qU.Z + prevQ.Value.W * qU.W;
                            if (dot < 0f)
                            {
                                qU = new Quaternion(-qU.X, -qU.Y, -qU.Z, -qU.W);
                            }
                        }
                        prevQ = qU;
                        sb.AppendLine("      - serializedVersion: 3");
                        sb.AppendLine($"        time: {k.time.ToString("0.######", inv)}");
                        sb.AppendLine($"        value: {{x: {qU.X.ToString("0.######", inv)}, y: {qU.Y.ToString("0.######", inv)}, z: {qU.Z.ToString("0.######", inv)}, w: {qU.W.ToString("0.######", inv)}}}");
                        sb.AppendLine("        inSlope: {x: 0, y: 0, z: 0, w: 0}");
                        sb.AppendLine("        outSlope: {x: 0, y: 0, z: 0, w: 0}");
                        sb.AppendLine("        tangentMode: 0");
                        sb.AppendLine("        weightedMode: 0");
                        sb.AppendLine("        inWeight: {x: 0.33333334, y: 0.33333334, z: 0.33333334, w: 0.33333334}");
                        sb.AppendLine("        outWeight: {x: 0.33333334, y: 0.33333334, z: 0.33333334, w: 0.33333334}");
                    }
                    sb.AppendLine("      m_PreInfinity: 2");
                    sb.AppendLine("      m_PostInfinity: 2");
                    sb.AppendLine("      m_RotationOrder: 4");
                    sb.AppendLine($"    path: {YamlString(relPath)}");
                }
            }

            sb.AppendLine("  m_CompressedRotationCurves: []");
            sb.AppendLine("  m_EulerCurves: []");

            // Convert FBX RH positions back to Unity LH by negating X
            var posTracks = anim.TrackList?.Where(t => t?.Translations != null && t.Translations.Count > 0).ToList();
            if (posTracks == null || posTracks.Count == 0)
            {
                sb.AppendLine("  m_PositionCurves: []");
            }
            else
            {
                sb.AppendLine("  m_PositionCurves:");
                foreach (var track in posTracks)
                {
                    var relPath = ToUnityRelativePath(track.Path);
                    if (string.IsNullOrEmpty(relPath)) hasGenericRootTransform = true;
                    genericBindings.Add((ComputeUnityCrc32(relPath), 1u, 4, 0));

                    sb.AppendLine("  - curve:");
                    sb.AppendLine("      serializedVersion: 2");
                    sb.AppendLine("      m_Curve:");
                    foreach (var k in track.Translations)
                    {
                        if (k.time > maxStopTime) maxStopTime = k.time;
                        float ux = -k.value.X;
                        if (MathF.Abs(ux) < 1e-12f) ux = 0f;
                        sb.AppendLine("      - serializedVersion: 3");
                        sb.AppendLine($"        time: {k.time.ToString("0.######", inv)}");
                        sb.AppendLine($"        value: {{x: {ux.ToString("0.######", inv)}, y: {k.value.Y.ToString("0.######", inv)}, z: {k.value.Z.ToString("0.######", inv)}}}");
                        sb.AppendLine("        inSlope: {x: 0, y: 0, z: 0}");
                        sb.AppendLine("        outSlope: {x: 0, y: 0, z: 0}");
                        sb.AppendLine("        tangentMode: 0");
                        sb.AppendLine("        weightedMode: 0");
                        sb.AppendLine("        inWeight: {x: 0.33333334, y: 0.33333334, z: 0.33333334}");
                        sb.AppendLine("        outWeight: {x: 0.33333334, y: 0.33333334, z: 0.33333334}");
                    }
                    sb.AppendLine("      m_PreInfinity: 2");
                    sb.AppendLine("      m_PostInfinity: 2");
                    sb.AppendLine("      m_RotationOrder: 4");
                    sb.AppendLine($"    path: {YamlString(relPath)}");
                }
            }

            var scaleTracks = anim.TrackList?.Where(t => t?.Scalings != null && t.Scalings.Count > 0).ToList();
            if (scaleTracks == null || scaleTracks.Count == 0)
            {
                sb.AppendLine("  m_ScaleCurves: []");
            }
            else
            {
                sb.AppendLine("  m_ScaleCurves:");
                foreach (var track in scaleTracks)
                {
                    var relPath = ToUnityRelativePath(track.Path);
                    if (string.IsNullOrEmpty(relPath)) hasGenericRootTransform = true;
                    genericBindings.Add((ComputeUnityCrc32(relPath), 3u, 4, 0));

                    sb.AppendLine("  - curve:");
                    sb.AppendLine("      serializedVersion: 2");
                    sb.AppendLine("      m_Curve:");
                    foreach (var k in track.Scalings)
                    {
                        if (k.time > maxStopTime) maxStopTime = k.time;
                        sb.AppendLine("      - serializedVersion: 3");
                        sb.AppendLine($"        time: {k.time.ToString("0.######", inv)}");
                        sb.AppendLine($"        value: {{x: {k.value.X.ToString("0.######", inv)}, y: {k.value.Y.ToString("0.######", inv)}, z: {k.value.Z.ToString("0.######", inv)}}}");
                        sb.AppendLine("        inSlope: {x: 0, y: 0, z: 0}");
                        sb.AppendLine("        outSlope: {x: 0, y: 0, z: 0}");
                        sb.AppendLine("        tangentMode: 0");
                        sb.AppendLine("        weightedMode: 0");
                        sb.AppendLine("        inWeight: {x: 0.33333334, y: 0.33333334, z: 0.33333334}");
                        sb.AppendLine("        outWeight: {x: 0.33333334, y: 0.33333334, z: 0.33333334}");
                    }
                    sb.AppendLine("      m_PreInfinity: 2");
                    sb.AppendLine("      m_PostInfinity: 2");
                    sb.AppendLine("      m_RotationOrder: 4");
                    sb.AppendLine($"    path: {YamlString(relPath)}");
                }
            }

            // Export BlendShape & Visibility m_FloatCurves
            var floatEntries = new List<(string RelPath, string Attribute, int ClassId, uint AttrHash, int CustomType, List<ImportedKeyframe<float>> Keys)>();
            if (anim.TrackList != null)
            {
                foreach (var track in anim.TrackList)
                {
                    if (track == null) continue;
                    var relPath = ToUnityRelativePath(track.Path);
                    if (track.BlendShapes != null)
                    {
                        foreach (var bs in track.BlendShapes)
                        {
                            if (bs == null || bs.Keyframes == null || bs.Keyframes.Count == 0 || string.IsNullOrEmpty(bs.ChannelName))
                            {
                                continue;
                            }
                            const string bsPrefix = "blendShape.";
                            string cleanChannel = bs.ChannelName.StartsWith(bsPrefix, StringComparison.OrdinalIgnoreCase)
                                ? bs.ChannelName.Substring(bsPrefix.Length)
                                : bs.ChannelName;
                            floatEntries.Add((relPath, bsPrefix + cleanChannel, (int)ClassIDType.SkinnedMeshRenderer, ComputeUnityCrc32(cleanChannel), 20, bs.Keyframes));
                        }
                    }
                    if (track.Visibilities != null && track.Visibilities.Count > 0)
                    {
                        floatEntries.Add((relPath, "m_IsActive", (int)ClassIDType.GameObject, 2086281974u, 0, track.Visibilities));
                    }
                }
            }

            if (floatEntries.Count == 0)
            {
                sb.AppendLine("  m_FloatCurves: []");
            }
            else
            {
                sb.AppendLine("  m_FloatCurves:");
                foreach (var fe in floatEntries)
                {
                    genericBindings.Add((ComputeUnityCrc32(fe.RelPath), fe.AttrHash, fe.ClassId, fe.CustomType));
                    sb.AppendLine("  - serializedVersion: 2");
                    sb.AppendLine("    curve:");
                    sb.AppendLine("      serializedVersion: 2");
                    sb.AppendLine("      m_Curve:");
                    foreach (var k in fe.Keys)
                    {
                        if (k.time > maxStopTime) maxStopTime = k.time;
                        string slopeStr = k.isStepped ? "Infinity" : "0";
                        sb.AppendLine("      - serializedVersion: 3");
                        sb.AppendLine($"        time: {k.time.ToString("0.######", inv)}");
                        sb.AppendLine($"        value: {k.value.ToString("0.######", inv)}");
                        sb.AppendLine($"        inSlope: {slopeStr}");
                        sb.AppendLine($"        outSlope: {slopeStr}");
                        sb.AppendLine("        tangentMode: 0");
                        sb.AppendLine("        weightedMode: 0");
                        sb.AppendLine("        inWeight: 0.33333334");
                        sb.AppendLine("        outWeight: 0.33333334");
                    }
                    sb.AppendLine("      m_PreInfinity: 2");
                    sb.AppendLine("      m_PostInfinity: 2");
                    sb.AppendLine("      m_RotationOrder: 4");
                    sb.AppendLine($"    attribute: {YamlString(fe.Attribute)}");
                    sb.AppendLine($"    path: {YamlString(fe.RelPath)}");
                    sb.AppendLine($"    classID: {fe.ClassId}");
                    sb.AppendLine("    script: {fileID: 0}");
                    sb.AppendLine("    flags: 0");
                }
            }

            sb.AppendLine("  m_PPtrCurves: []");
            sb.AppendLine($"  m_SampleRate: {(anim.SampleRate > 0 ? anim.SampleRate.ToString("0.######", inv) : "30")}");
            sb.AppendLine("  m_WrapMode: 0");
            sb.AppendLine("  m_Bounds:");
            sb.AppendLine("    m_Center: {x: 0, y: 0, z: 0}");
            sb.AppendLine("    m_Extent: {x: 0, y: 0, z: 0}");
            sb.AppendLine("  m_ClipBindingConstant:");
            if (genericBindings.Count == 0)
            {
                sb.AppendLine("    genericBindings: []");
            }
            else
            {
                sb.AppendLine("    genericBindings:");
                foreach (var gb in genericBindings)
                {
                    sb.AppendLine("    - serializedVersion: 2");
                    sb.AppendLine($"      path: {gb.PathHash}");
                    sb.AppendLine($"      attribute: {gb.Attribute}");
                    sb.AppendLine("      script: {fileID: 0}");
                    sb.AppendLine($"      typeID: {gb.TypeId}");
                    sb.AppendLine($"      customType: {gb.CustomType}");
                    sb.AppendLine("      isPPtrCurve: 0");
                    sb.AppendLine("      isIntCurve: 0");
                    sb.AppendLine("      isSerializeReferenceCurve: 0");
                }
            }
            sb.AppendLine("    pptrCurveMapping: []");

            sb.AppendLine("  m_AnimationClipSettings:");
            sb.AppendLine("    serializedVersion: 2");
            sb.AppendLine("    m_AdditiveReferencePoseClip: {fileID: 0}");
            sb.AppendLine("    m_AdditiveReferencePoseTime: 0");
            sb.AppendLine("    m_StartTime: 0");
            sb.AppendLine($"    m_StopTime: {maxStopTime.ToString("0.######", inv)}");
            sb.AppendLine("    m_OrientationOffsetY: 0");
            sb.AppendLine("    m_Level: 0");
            sb.AppendLine("    m_CycleOffset: 0");
            sb.AppendLine("    m_HasAdditiveReferencePose: 0");
            sb.AppendLine("    m_LoopTime: 1");
            sb.AppendLine("    m_LoopBlend: 0");
            sb.AppendLine("    m_LoopBlendOrientation: 0");
            sb.AppendLine("    m_LoopBlendPositionY: 0");
            sb.AppendLine("    m_LoopBlendPositionXZ: 0");
            sb.AppendLine("    m_KeepOriginalOrientation: 0");
            sb.AppendLine("    m_KeepOriginalPositionY: 1");
            sb.AppendLine("    m_KeepOriginalPositionXZ: 0");
            sb.AppendLine("    m_HeightFromFeet: 0");
            sb.AppendLine("    m_Mirror: 0");
            sb.AppendLine("  m_EditorCurves: []");
            sb.AppendLine("  m_EulerEditorCurves: []");
            sb.AppendLine($"  m_HasGenericRootTransform: {(hasGenericRootTransform ? 1 : 0)}");
            sb.AppendLine("  m_HasMotionFloatCurves: 0");
            sb.AppendLine("  m_Events: []");

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.WriteAllText(outputPath, sb.ToString(), Encoding.UTF8);
        }

        private static string? InferCommonRootPrefix(List<ImportedAnimationKeyframedTrack>? tracks)
        {
            if (tracks == null || tracks.Count < 2)
            {
                return null;
            }
            string? candidate = null;
            foreach (var t in tracks)
            {
                var p = (t?.Path ?? string.Empty).Replace('\\', '/').Trim('/');
                if (string.IsNullOrEmpty(p))
                {
                    return null; // Already has root-relative empty path
                }
                int slash = p.IndexOf('/');
                string seg0 = slash >= 0 ? p.Substring(0, slash) : p;
                if (candidate == null)
                {
                    candidate = seg0;
                }
                else if (!string.Equals(candidate, seg0, StringComparison.Ordinal))
                {
                    return null;
                }
            }
            return candidate;
        }
        #endregion
    }
}
