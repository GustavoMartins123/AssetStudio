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
            var exportName = string.IsNullOrEmpty(material.m_Name) ? fallbackName : material.m_Name;
            Directory.CreateDirectory(exportPath);
            var filePath = Path.Combine(exportPath, FixFileName(exportName) + ".mat");
            if (File.Exists(filePath)) return false;

            File.WriteAllText(filePath, BuildUnityMaterial(material, exportName, exportPath, textureFormat), Encoding.UTF8);
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

        #region Animation

        private class CustomCurve<T>
        {
            public string Path;
            public string Attribute;
            public List<CustomKeyframe<T>> Keyframes = new List<CustomKeyframe<T>>();
        }

        private class CustomKeyframe<T>
        {
            public float Time;
            public T Value;
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
                }
            }
            return bonePathHash;
        }

        private static string GetTransformPath(Transform transform)
        {
            if (transform.m_GameObject.TryGet(out var m_GameObject))
            {
                if (transform.m_Father.TryGet(out var father))
                {
                    return GetTransformPath(father) + "/" + m_GameObject.m_Name;
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
                                var blendShapeName = "blendShape." + channel.name;
                                var crc = new SevenZip.CRC();
                                var bytes = Encoding.UTF8.GetBytes(blendShapeName);
                                crc.Update(bytes, 0, (uint)bytes.Length);
                                morphChannelNames[crc.GetDigest()] = blendShapeName;
                            }
                        }
                    }
                }
            }
            return morphChannelNames;
        }

        public static bool ExportAnimationClip(AnimationClip clip, string fallbackName, string exportPath, Dictionary<uint, string> bonePathHash, Dictionary<uint, string> morphChannelNames)
        {
            var exportName = string.IsNullOrEmpty(clip.m_Name) ? fallbackName : clip.m_Name;
            Directory.CreateDirectory(exportPath);
            var filePath = Path.Combine(exportPath, FixFileName(exportName) + ".anim");
            if (File.Exists(filePath)) return false;

            File.WriteAllText(filePath, BuildUnityAnimationClip(clip, bonePathHash, morphChannelNames), Encoding.UTF8);
            return true;
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

                var trackDict = new Dictionary<string, (List<CustomKeyframe<Vector3>> pos, List<CustomKeyframe<Quaternion>> rot, List<CustomKeyframe<Vector3>> scale, Dictionary<string, List<CustomKeyframe<float>>> floats)>();

                (List<CustomKeyframe<Vector3>> pos, List<CustomKeyframe<Quaternion>> rot, List<CustomKeyframe<Vector3>> scale, Dictionary<string, List<CustomKeyframe<float>>> floats) GetTrack(string path)
                {
                    if (!trackDict.TryGetValue(path, out var t))
                    {
                        t = (new List<CustomKeyframe<Vector3>>(), new List<CustomKeyframe<Quaternion>>(), new List<CustomKeyframe<Vector3>>(), new Dictionary<string, List<CustomKeyframe<float>>>());
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
                                track.pos.Add(new CustomKeyframe<Vector3>
                                {
                                    Time = time,
                                    Value = new Vector3(
                                        data[curveIndex++ + offset],
                                        data[curveIndex++ + offset],
                                        data[curveIndex++ + offset]
                                    )
                                });
                                break;
                            case 2:
                                track.rot.Add(new CustomKeyframe<Quaternion>
                                {
                                    Time = time,
                                    Value = new Quaternion(
                                        data[curveIndex++ + offset],
                                        data[curveIndex++ + offset],
                                        data[curveIndex++ + offset],
                                        data[curveIndex++ + offset]
                                    )
                                });
                                break;
                            case 3:
                                track.scale.Add(new CustomKeyframe<Vector3>
                                {
                                    Time = time,
                                    Value = new Vector3(
                                        data[curveIndex++ + offset],
                                        data[curveIndex++ + offset],
                                        data[curveIndex++ + offset]
                                    )
                                });
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
                        list.Add(new CustomKeyframe<float>
                        {
                            Time = time,
                            Value = data[curveIndex++ + offset]
                        });
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

                if (m_Clip.m_DenseClip != null)
                {
                    var m_DenseClip = m_Clip.m_DenseClip;
                    var streamCount = m_Clip.m_StreamedClip?.curveCount ?? 0;
                    for (int frameIndex = 0; frameIndex < m_DenseClip.m_FrameCount; frameIndex++)
                    {
                        var time = m_DenseClip.m_BeginTime + frameIndex / m_DenseClip.m_SampleRate;
                        var frameOffset = frameIndex * m_DenseClip.m_CurveCount;
                        for (int curveIndex = 0; curveIndex < m_DenseClip.m_CurveCount;)
                        {
                            var index = streamCount + curveIndex;
                            AddCurveData((int)index, time, m_DenseClip.m_SampleArray, (int)frameOffset, ref curveIndex);
                        }
                    }
                }

                if (m_Clip.m_ConstantClip != null)
                {
                    var m_ConstantClip = m_Clip.m_ConstantClip;
                    var denseCount = m_Clip.m_DenseClip?.m_CurveCount ?? 0;
                    var streamCount = m_Clip.m_StreamedClip?.curveCount ?? 0;
                    var time2 = 0.0f;
                    for (int i = 0; i < 2; i++)
                    {
                        for (int curveIndex = 0; curveIndex < m_ConstantClip.data.Length;)
                        {
                            var index = streamCount + denseCount + curveIndex;
                            AddCurveData((int)index, time2, m_ConstantClip.data, 0, ref curveIndex);
                        }
                        if (clip.m_MuscleClip != null)
                            time2 = clip.m_MuscleClip.m_StopTime;
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

            sb.AppendLine("  m_PPtrCurves: []");
            sb.AppendLine($"  m_SampleRate: {F(clip.m_SampleRate)}");
            sb.AppendLine($"  m_WrapMode: {clip.m_WrapMode}");

            var boundsCenter = clip.m_Bounds?.m_Center ?? Vector3.Zero;
            var boundsExtent = clip.m_Bounds?.m_Extent ?? Vector3.Zero;
            sb.AppendLine("  m_Bounds:");
            sb.AppendLine($"    m_Center: {{x: {F(boundsCenter.X)}, y: {F(boundsCenter.Y)}, z: {F(boundsCenter.Z)}}}");
            sb.AppendLine($"    m_Extent: {{x: {F(boundsExtent.X)}, y: {F(boundsExtent.Y)}, z: {F(boundsExtent.Z)}}}");

            sb.AppendLine("  m_ClipBindingConstant:");
            var totalBindings = posCurves.Count + rotCurves.Count + eulerCurves.Count + scaleCurves.Count + floatCurves.Count;
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
            }
            sb.AppendLine("    pptrCurveMapping: []");

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
        private static void WriteSkeleton(StringBuilder sb, string indent, Skeleton skeleton)
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

        private static void WriteSkeletonPose(StringBuilder sb, string indent, SkeletonPose pose)
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

        private static void WriteHuman(StringBuilder sb, string indent, Human human)
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

            Directory.CreateDirectory(Path.GetDirectoryName(avatarFullPath));
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

            Directory.CreateDirectory(Path.GetDirectoryName(exportFullPath));
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

            Directory.CreateDirectory(Path.GetDirectoryName(exportFullPath));
            File.WriteAllText(exportFullPath, sb.ToString(), Encoding.UTF8);
            return true;
        }
        #endregion
    }
}
