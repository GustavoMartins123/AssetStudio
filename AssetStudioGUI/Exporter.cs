using AssetStudio;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace AssetStudioGUI
{
    internal static class Exporter
    {
        public static bool ExportTexture2D(AssetItem item, string exportPath)
        {
            var m_Texture2D = (Texture2D)item.Asset;
            if (Properties.Settings.Default.convertTexture)
            {
                var type = Properties.Settings.Default.convertType;
                if (!TryExportFile(exportPath, item, "." + type.ToString().ToLower(), out var exportFullPath))
                    return false;
                var image = m_Texture2D.ConvertToImage(true);
                if (image == null)
                    return false;
                using (image)
                {
                    using (var file = File.OpenWrite(exportFullPath))
                    {
                        image.WriteToStream(file, type);
                    }
                    WriteTextureMetaIfMissing(exportFullPath);
                    return true;
                }
            }
            else
            {
                if (!TryExportFile(exportPath, item, ".tex", out var exportFullPath))
                    return false;
                File.WriteAllBytes(exportFullPath, m_Texture2D.image_data.GetData());
                WriteTextureMetaIfMissing(exportFullPath);
                return true;
            }
        }

        public static bool ExportMaterial(AssetItem item, string exportPath)
        {
            var material = (Material)item.Asset;
            Directory.CreateDirectory(exportPath);
            File.WriteAllText(
                Path.Combine(exportPath, FixFileName(item.Text) + ".mat"),
                BuildUnityMaterial(material, item.Text, exportPath),
                Encoding.UTF8);
            return true;
        }

        private static string BuildUnityMaterial(Material material, string materialName, string exportPath)
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
                    AppendTextureEnv(sb, texEnv.Key, texEnv.Value, exportPath);
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

        private static void AppendTextureEnv(StringBuilder sb, string property, UnityTexEnv texEnv, string exportPath)
        {
            sb.AppendLine($"    - {property}:");
            sb.Append("        m_Texture: ");
            if (texEnv != null
                && texEnv.m_Texture.TryGet<Texture2D>(out var texture)
                && TryGetExportedTextureGuid(exportPath, texture, out var guid))
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
                : null;

            if (TryGetShaderGuid(exportPath, shaderName, out var guid))
            {
                return $"{{fileID: 4800000, guid: {guid}, type: 3}}";
            }

            return "{fileID: 0}";
        }

        private static bool TryGetShaderGuid(string exportPath, string shaderName, out string guid)
        {
            guid = null;
            if (string.IsNullOrEmpty(shaderName))
            {
                return false;
            }

            var projectRoot = FindUnityProjectRoot(exportPath);
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

        private static bool TryGetExportedTextureGuid(string materialExportPath, Texture2D texture, out string guid)
        {
            guid = null;
            var textureFolder = GetTextureExportFolder(materialExportPath);
            var extension = "." + Properties.Settings.Default.convertType.ToString().ToLower();
            var texturePath = Path.Combine(textureFolder, FixFileName(texture.m_Name) + extension);
            var metaPath = texturePath + ".meta";
            return File.Exists(metaPath) && TryReadGuid(metaPath, out guid);
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

        private static void WriteTextureMetaIfMissing(string texturePath)
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
  bumpmap:
    convertToNormalMap: 0
    externalNormalMap: 0
    heightScale: 0.25
    normalMapFilter: 0
    flipGreenChannel: 0
  isReadable: 0
  streamingMipmaps: 0
  streamingMipmapsPriority: 0
  vTOnly: 0
  ignoreMipmapLimit: 0
  grayScaleToAlpha: 0
  generateCubemap: 6
  cubemapConvolution: 0
  seamlessCubemap: 0
  textureFormat: 1
  maxTextureSize: 2048
  textureSettings:
    serializedVersion: 2
    filterMode: 1
    aniso: 1
    mipBias: 0
    wrapU: 0
    wrapV: 0
    wrapW: 0
  nPOTScale: 1
  lightmap: 0
  compressionQuality: 50
  spriteMode: 0
  spriteExtrude: 1
  spriteMeshType: 1
  alignment: 0
  spritePivot: {{x: 0.5, y: 0.5}}
  spritePixelsToUnits: 100
  spriteBorder: {{x: 0, y: 0, z: 0, w: 0}}
  spriteGenerateFallbackPhysicsShape: 1
  alphaUsage: 1
  alphaIsTransparency: 0
  spriteTessellationDetail: -1
  textureType: 0
  textureShape: 1
  singleChannelComponent: 0
  flipbookRows: 1
  flipbookColumns: 1
  maxTextureSizeSet: 0
  compressionQualitySet: 0
  textureFormatSet: 0
  ignorePngGamma: 0
  applyGammaDecoding: 0
  swizzle: 50462976
  cookieLightType: 0
  platformSettings:
  - serializedVersion: 4
    buildTarget: DefaultTexturePlatform
    maxTextureSize: 2048
    resizeAlgorithm: 0
    textureFormat: -1
    textureCompression: 1
    compressionQuality: 50
    crunchedCompression: 0
    allowsAlphaSplitting: 0
    overridden: 0
    ignorePlatformSupport: 0
    androidETC2FallbackOverride: 0
    forceMaximumCompressionQuality_BC6H_BC7: 0
  - serializedVersion: 4
    buildTarget: Standalone
    maxTextureSize: 2048
    resizeAlgorithm: 0
    textureFormat: -1
    textureCompression: 1
    compressionQuality: 50
    crunchedCompression: 0
    allowsAlphaSplitting: 0
    overridden: 0
    ignorePlatformSupport: 0
    androidETC2FallbackOverride: 0
    forceMaximumCompressionQuality_BC6H_BC7: 0
  spriteSheet:
    serializedVersion: 2
    sprites: []
    outline: []
    customData: 
    physicsShape: []
    bones: []
    spriteID: 
    internalID: 0
    vertices: []
    indices: 
    edges: []
    weights: []
    secondaryTextures: []
    spriteCustomMetadata:
      entries: []
    nameFileIdTable: {{}}
  mipmapLimitGroupName: 
  pSDRemoveMatte: 0
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
            return value.ToString("0.########", System.Globalization.CultureInfo.InvariantCulture);
        }

        public static bool ExportAudioClip(AssetItem item, string exportPath)
        {
            var m_AudioClip = (AudioClip)item.Asset;
            var m_AudioData = m_AudioClip.m_AudioData.GetData();
            if (m_AudioData == null || m_AudioData.Length == 0)
                return false;
            var converter = new AudioClipConverter(m_AudioClip);
            if (Properties.Settings.Default.convertAudio && converter.IsSupport)
            {
                if (!TryExportFile(exportPath, item, ".wav", out var exportFullPath))
                    return false;
                var buffer = converter.ConvertToWav();
                if (buffer == null)
                    return false;
                File.WriteAllBytes(exportFullPath, buffer);
            }
            else
            {
                if (!TryExportFile(exportPath, item, converter.GetExtensionName(), out var exportFullPath))
                    return false;
                File.WriteAllBytes(exportFullPath, m_AudioData);
            }
            return true;
        }

        public static bool ExportShader(AssetItem item, string exportPath)
        {
            if (!TryExportFile(exportPath, item, ".shader", out var exportFullPath))
                return false;
            var m_Shader = (Shader)item.Asset;
            var str = m_Shader.Convert();
            File.WriteAllText(exportFullPath, str);
            return true;
        }

        public static bool ExportTextAsset(AssetItem item, string exportPath)
        {
            var m_TextAsset = (TextAsset)(item.Asset);
            var extension = ".txt";
            if (Properties.Settings.Default.restoreExtensionName)
            {
                if (!string.IsNullOrEmpty(item.Container))
                {
                    extension = Path.GetExtension(item.Container);
                }
            }
            if (!TryExportFile(exportPath, item, extension, out var exportFullPath))
                return false;
            File.WriteAllBytes(exportFullPath, m_TextAsset.m_Script);
            return true;
        }

        public static bool ExportMonoBehaviour(AssetItem item, string exportPath)
        {
            if (!TryExportFile(exportPath, item, ".json", out var exportFullPath))
                return false;
            var m_MonoBehaviour = (MonoBehaviour)item.Asset;
            var type = m_MonoBehaviour.ToType();
            if (type == null)
            {
                var m_Type = Studio.MonoBehaviourToTypeTree(m_MonoBehaviour);
                type = m_MonoBehaviour.ToType(m_Type);
            }
            var str = JsonConvert.SerializeObject(type, Formatting.Indented);
            File.WriteAllText(exportFullPath, str);
            return true;
        }

        public static bool ExportFont(AssetItem item, string exportPath)
        {
            var m_Font = (Font)item.Asset;
            if (m_Font.m_FontData != null)
            {
                var extension = ".ttf";
                if (m_Font.m_FontData[0] == 79 && m_Font.m_FontData[1] == 84 && m_Font.m_FontData[2] == 84 && m_Font.m_FontData[3] == 79)
                {
                    extension = ".otf";
                }
                if (!TryExportFile(exportPath, item, extension, out var exportFullPath))
                    return false;
                File.WriteAllBytes(exportFullPath, m_Font.m_FontData);
                return true;
            }
            return false;
        }

        public static bool ExportMesh(AssetItem item, string exportPath)
        {
            var m_Mesh = (Mesh)item.Asset;
            if (m_Mesh.m_VertexCount <= 0)
                return false;
            if (!TryExportFile(exportPath, item, ".obj", out var exportFullPath))
                return false;
            if (m_Mesh.m_Vertices == null || m_Mesh.m_Vertices.Length == 0)
            {
                return false;
            }
            using (var writer = new StreamWriter(exportFullPath, false, Encoding.UTF8))
            {
                writer.WriteLine("g " + m_Mesh.m_Name);
                #region Vertices
                int c = 3;
                if (m_Mesh.m_Vertices.Length == m_Mesh.m_VertexCount * 4)
                {
                    c = 4;
                }
                for (int v = 0; v < m_Mesh.m_VertexCount; v++)
                {
                    writer.WriteLine("v {0} {1} {2}", CleanFloat(-m_Mesh.m_Vertices[v * c]), CleanFloat(m_Mesh.m_Vertices[v * c + 1]), CleanFloat(m_Mesh.m_Vertices[v * c + 2]));
                }
                #endregion

                #region UV
                if (m_Mesh.m_UV0?.Length > 0)
                {
                    c = 4;
                    if (m_Mesh.m_UV0.Length == m_Mesh.m_VertexCount * 2)
                    {
                        c = 2;
                    }
                    else if (m_Mesh.m_UV0.Length == m_Mesh.m_VertexCount * 3)
                    {
                        c = 3;
                    }
                    for (int v = 0; v < m_Mesh.m_VertexCount; v++)
                    {
                        writer.WriteLine("vt {0} {1}", CleanFloat(m_Mesh.m_UV0[v * c]), CleanFloat(m_Mesh.m_UV0[v * c + 1]));
                    }
                }
                #endregion

                #region Normals
                if (m_Mesh.m_Normals?.Length > 0)
                {
                    if (m_Mesh.m_Normals.Length == m_Mesh.m_VertexCount * 3)
                    {
                        c = 3;
                    }
                    else if (m_Mesh.m_Normals.Length == m_Mesh.m_VertexCount * 4)
                    {
                        c = 4;
                    }
                    for (int v = 0; v < m_Mesh.m_VertexCount; v++)
                    {
                        writer.WriteLine("vn {0} {1} {2}", CleanFloat(-m_Mesh.m_Normals[v * c]), CleanFloat(m_Mesh.m_Normals[v * c + 1]), CleanFloat(m_Mesh.m_Normals[v * c + 2]));
                    }
                }
                #endregion

                #region Face
                int sum = 0;
                for (var i = 0; i < m_Mesh.m_SubMeshes.Length; i++)
                {
                    writer.WriteLine($"g {m_Mesh.m_Name}_{i}");
                    int indexCount = (int)m_Mesh.m_SubMeshes[i].indexCount;
                    var end = sum + indexCount / 3;
                    for (int f = sum; f < end; f++)
                    {
                        writer.WriteLine("f {0}/{0}/{0} {1}/{1}/{1} {2}/{2}/{2}", m_Mesh.m_Indices[f * 3 + 2] + 1, m_Mesh.m_Indices[f * 3 + 1] + 1, m_Mesh.m_Indices[f * 3] + 1);
                    }
                    sum = end;
                }
                #endregion
            }
            return true;
        }

        private static string CleanFloat(float value)
        {
            return float.IsNaN(value) || float.IsInfinity(value) ? "0" : value.ToString();
        }

        public static bool ExportVideoClip(AssetItem item, string exportPath)
        {
            var m_VideoClip = (VideoClip)item.Asset;
            if (m_VideoClip.m_ExternalResources.m_Size > 0)
            {
                if (!TryExportFile(exportPath, item, Path.GetExtension(m_VideoClip.m_OriginalPath), out var exportFullPath))
                    return false;
                m_VideoClip.m_VideoData.WriteData(exportFullPath);
                return true;
            }
            return false;
        }

        public static bool ExportMovieTexture(AssetItem item, string exportPath)
        {
            var m_MovieTexture = (MovieTexture)item.Asset;
            if (!TryExportFile(exportPath, item, ".ogv", out var exportFullPath))
                return false;
            File.WriteAllBytes(exportFullPath, m_MovieTexture.m_MovieData);
            return true;
        }

        public static bool ExportSprite(AssetItem item, string exportPath)
        {
            var type = Properties.Settings.Default.convertType;
            if (!TryExportFile(exportPath, item, "." + type.ToString().ToLower(), out var exportFullPath))
                return false;
            var image = ((Sprite)item.Asset).GetImage();
            if (image != null)
            {
                using (image)
                {
                    using (var file = File.OpenWrite(exportFullPath))
                    {
                        image.WriteToStream(file, type);
                    }
                    return true;
                }
            }
            return false;
        }

        public static bool ExportRawFile(AssetItem item, string exportPath)
        {
            if (!TryExportFile(exportPath, item, ".dat", out var exportFullPath))
                return false;
            File.WriteAllBytes(exportFullPath, item.Asset.GetRawData());
            return true;
        }

        private static bool TryExportFile(string dir, AssetItem item, string extension, out string fullPath)
        {
            var fileName = FixFileName(item.Text);
            fullPath = Path.Combine(dir, fileName + extension);
            if (!File.Exists(fullPath))
            {
                Directory.CreateDirectory(dir);
                return true;
            }
            fullPath = Path.Combine(dir, fileName + item.UniqueID + extension);
            if (!File.Exists(fullPath))
            {
                Directory.CreateDirectory(dir);
                return true;
            }
            return false;
        }

        public static bool ExportAnimator(AssetItem item, string exportPath, List<AssetItem> animationList = null)
        {
            var exportFullPath = Path.Combine(exportPath, item.Text, item.Text + ".fbx");
            if (File.Exists(exportFullPath))
            {
                exportFullPath = Path.Combine(exportPath, item.Text + item.UniqueID, item.Text + ".fbx");
            }
            var m_Animator = (Animator)item.Asset;
            var convert = animationList != null
                ? new ModelConverter(m_Animator, Properties.Settings.Default.convertType, animationList.Select(x => (AnimationClip)x.Asset).ToArray())
                : new ModelConverter(m_Animator, Properties.Settings.Default.convertType);
            ExportFbx(convert, exportFullPath);
            return true;
        }

        public static void ExportGameObject(GameObject gameObject, string exportPath, List<AssetItem> animationList = null)
        {
            var convert = animationList != null
                ? new ModelConverter(gameObject, Properties.Settings.Default.convertType, animationList.Select(x => (AnimationClip)x.Asset).ToArray())
                : new ModelConverter(gameObject, Properties.Settings.Default.convertType);
            exportPath = exportPath + FixFileName(gameObject.m_Name) + ".fbx";
            ExportFbx(convert, exportPath);
        }

        public static void ExportGameObjectMerge(List<GameObject> gameObject, string exportPath, List<AssetItem> animationList = null)
        {
            IImported convert;
            if (gameObject.Count == 1)
            {
                convert = animationList != null
                    ? new ModelConverter(gameObject[0], Properties.Settings.Default.convertType, animationList.Select(x => (AnimationClip)x.Asset).ToArray())
                    : new ModelConverter(gameObject[0], Properties.Settings.Default.convertType);
            }
            else
            {
                var rootName = Path.GetFileNameWithoutExtension(exportPath);
                convert = animationList != null
                    ? new ModelConverter(rootName, gameObject, Properties.Settings.Default.convertType, animationList.Select(x => (AnimationClip)x.Asset).ToArray())
                    : new ModelConverter(rootName, gameObject, Properties.Settings.Default.convertType);
            }
            ExportFbx(convert, exportPath);
        }

        private static void ExportFbx(IImported convert, string exportPath)
        {
            var eulerFilter = Properties.Settings.Default.eulerFilter;
            var filterPrecision = (float)Properties.Settings.Default.filterPrecision;
            var exportAllNodes = Properties.Settings.Default.exportAllNodes;
            var exportSkins = Properties.Settings.Default.exportSkins;
            var exportAnimations = Properties.Settings.Default.exportAnimations;
            var exportBlendShape = Properties.Settings.Default.exportBlendShape;
            var castToBone = Properties.Settings.Default.castToBone;
            var boneSize = (int)Properties.Settings.Default.boneSize;
            var exportAllUvsAsDiffuseMaps = Properties.Settings.Default.exportAllUvsAsDiffuseMaps;
            var scaleFactor = (float)Properties.Settings.Default.scaleFactor;
            var fbxVersion = Properties.Settings.Default.fbxVersion;
            var fbxFormat = Properties.Settings.Default.fbxFormat;
            ModelExporter.ExportFbx(exportPath, convert, eulerFilter, filterPrecision,
                exportAllNodes, exportSkins, exportAnimations, exportBlendShape, castToBone, boneSize, exportAllUvsAsDiffuseMaps, scaleFactor, fbxVersion, fbxFormat == 1);
        }

        public static bool ExportDumpFile(AssetItem item, string exportPath)
        {
            if (!TryExportFile(exportPath, item, ".txt", out var exportFullPath))
                return false;
            var str = item.Asset.Dump();
            if (str == null && item.Asset is MonoBehaviour m_MonoBehaviour)
            {
                var m_Type = Studio.MonoBehaviourToTypeTree(m_MonoBehaviour);
                str = m_MonoBehaviour.Dump(m_Type);
            }
            if (str != null)
            {
                File.WriteAllText(exportFullPath, str);
                return true;
            }
            return false;
        }

        public static bool ExportConvertFile(AssetItem item, string exportPath)
        {
            switch (item.Type)
            {
                case ClassIDType.Texture2D:
                    return ExportTexture2D(item, exportPath);
                case ClassIDType.Material:
                    return ExportMaterial(item, exportPath);
                case ClassIDType.AudioClip:
                    return ExportAudioClip(item, exportPath);
                case ClassIDType.Shader:
                    return ExportShader(item, exportPath);
                case ClassIDType.TextAsset:
                    return ExportTextAsset(item, exportPath);
                case ClassIDType.MonoBehaviour:
                    return ExportMonoBehaviour(item, exportPath);
                case ClassIDType.Font:
                    return ExportFont(item, exportPath);
                case ClassIDType.Mesh:
                    return ExportMesh(item, exportPath);
                case ClassIDType.VideoClip:
                    return ExportVideoClip(item, exportPath);
                case ClassIDType.MovieTexture:
                    return ExportMovieTexture(item, exportPath);
                case ClassIDType.Sprite:
                    return ExportSprite(item, exportPath);
                case ClassIDType.Animator:
                    return ExportAnimator(item, exportPath);
                case ClassIDType.AnimationClip:
                    return false;
                default:
                    return ExportRawFile(item, exportPath);
            }
        }

        public static string FixFileName(string str)
        {
            if (str.Length >= 260) return Path.GetRandomFileName();
            return Path.GetInvalidFileNameChars().Aggregate(str, (current, c) => current.Replace(c, '_'));
        }
    }
}
