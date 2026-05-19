using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace AssetStudio
{
    public static class AssetExportHelper
    {
        public static bool ExportMaterial(Material material, string fallbackName, string exportPath, ImageFormat textureFormat)
        {
            var exportMaterial = ResolveMaterialForExport(material) ?? material;
            var exportName = string.IsNullOrEmpty(exportMaterial.m_Name) ? fallbackName : exportMaterial.m_Name;
            Directory.CreateDirectory(exportPath);
            var filePath = Path.Combine(exportPath, FixFileName(exportName) + ".mat");
            if (File.Exists(filePath)) return false;

            File.WriteAllText(filePath, BuildUnityMaterial(exportMaterial, exportName, exportPath, textureFormat), Encoding.UTF8);
            return true;
        }

        private static Material ResolveMaterialForExport(Material material)
        {
            var visited = new HashSet<Material>();
            while (material != null && visited.Add(material))
            {
                if (HasMaterialProperties(material))
                {
                    return material;
                }

                if (material.m_Parent != null && material.m_Parent.TryGet(out var parent))
                {
                    material = parent;
                    continue;
                }

                break;
            }

            return null;
        }

        private static bool HasMaterialProperties(Material material)
        {
            if (material == null || string.IsNullOrEmpty(material.m_Name))
            {
                return false;
            }

            var properties = material.m_SavedProperties;
            if (properties == null)
            {
                return true;
            }

            var texEnvs = properties.m_TexEnvs ?? Array.Empty<KeyValuePair<string, UnityTexEnv>>();
            if (texEnvs.Any(x => x.Value?.m_Texture != null && !x.Value.m_Texture.IsNull))
            {
                return true;
            }

            return properties.m_Ints?.Length > 0
                || properties.m_Floats?.Length > 0
                || properties.m_Colors?.Length > 0
                || material.m_Shader != null && !material.m_Shader.IsNull;
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

        private static string InferShaderName(Material material)
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

        private static bool TryGetExportedTextureGuid(string materialExportPath, Texture2D texture, ImageFormat textureFormat, out string guid)
        {
            guid = null;
            var textureFolder = GetTextureExportFolder(materialExportPath);
            var extension = "." + textureFormat.ToString().ToLowerInvariant();
            var texturePath = Path.Combine(textureFolder, FixFileName(texture.m_Name) + extension);
            var metaPath = texturePath + ".meta";
            return File.Exists(metaPath) && TryReadGuid(metaPath, out guid);
        }

        private static bool TryGetShaderGuid(string path, string shaderName, out string guid)
        {
            guid = null;
            if (string.IsNullOrEmpty(shaderName))
            {
                return false;
            }

            var projectRoot = FindUnityProjectRoot(path);
            if (projectRoot == null)
            {
                return false;
            }

            var shaderFileName = shaderName.Split('/').Last() + ".shader.meta";
            var roots = new[]
            {
            Path.Combine(projectRoot, "Assets"),
            Path.Combine(projectRoot, "Packages"),
            Path.Combine(projectRoot, "Library", "PackageCache")
        };

            foreach (var root in roots.Where(Directory.Exists))
            {
                var matches = Directory.GetFiles(root, shaderFileName, SearchOption.AllDirectories);
                foreach (var match in matches)
                {
                    if (TryReadGuid(match, out guid))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool TryReadGuid(string metaPath, out string guid)
        {
            guid = null;
            foreach (var line in File.ReadLines(metaPath))
            {
                if (line.StartsWith("guid: ", StringComparison.Ordinal))
                {
                    guid = line.Substring("guid: ".Length).Trim();
                    return guid.Length == 32;
                }
            }
            return false;
        }

        private static string FindUnityProjectRoot(string path)
        {
            var directory = new DirectoryInfo(path);
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
            var metaPath = texturePath + ".meta";
            if (File.Exists(metaPath))
            {
                return;
            }

            File.WriteAllText(metaPath, BuildTextureMeta(GenerateUnityGuid()), Encoding.UTF8);
        }

        private static string GenerateUnityGuid()
        {
            var bytes = new byte[16];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }
            return string.Concat(bytes.Select(x => x.ToString("x2")));
        }

        private static string BuildTextureMeta(string guid)
        {
            return $@"fileFormatVersion: 2
guid: {guid}
TextureImporter:
  internalIDToNameTable: []
  externalObjects: {{}}
  serializedVersion: 13
  mipmaps:
    mipMapMode: 0
    enableMipMap: 1
    sRGBTexture: 1
    linearTexture: 0
    fadeOut: 0
    borderMipMap: 0
    mipMapsPreserveCoverage: 0
    alphaTestReferenceValue: 0.5
    mipMapFadeDistanceStart: 1
    mipMapFadeDistanceEnd: 3
  textureSettings:
    serializedVersion: 2
    filterMode: 1
    aniso: 1
    mipBias: 0
    wrapU: 0
    wrapV: 0
    wrapW: 0
  textureType: 0
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

        private static string YamlString(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }
            return value.IndexOfAny(new[] { ':', '#', '{', '}', '[', ']', ',', '"' }) >= 0
                ? "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""
                : value;
        }

        private static string F(float value)
        {
            return value.ToString("0.########", CultureInfo.InvariantCulture);
        }


        public static string FixFileName(string name)
        {
            if (name.Length >= 260) return Path.GetRandomFileName();
            return Path.GetInvalidFileNameChars().Aggregate(name, (current, c) => current.Replace(c, '_'));
        }
    }
}
