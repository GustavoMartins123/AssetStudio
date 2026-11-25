using System;
using System.Collections.Generic;
using System.IO;

namespace AssetStudio
{
    public interface IImported
    {
        ImportedFrame RootFrame { get; }
        List<ImportedMesh> MeshList { get; }
        List<ImportedMaterial> MaterialList { get; }
        List<ImportedTexture> TextureList { get; }
        List<ImportedKeyframedAnimation> AnimationList { get; }
        List<ImportedMorph> MorphList { get; }
        List<ImportedCamera> CameraList => new List<ImportedCamera>(0);
        List<ImportedLight> LightList => new List<ImportedLight>(0);
    }

    public class ImportedWrapper : IImported
    {
        public ImportedFrame RootFrame { get; set; }
        public List<ImportedMesh> MeshList { get; set; }
        public List<ImportedMaterial> MaterialList { get; set; }
        public List<ImportedTexture> TextureList { get; set; }
        public List<ImportedKeyframedAnimation> AnimationList { get; set; }
        public List<ImportedMorph> MorphList { get; set; }
        public List<ImportedCamera> CameraList { get; set; }
        public List<ImportedLight> LightList { get; set; }

        public ImportedWrapper(IImported source)
        {
            RootFrame = source.RootFrame;
            MeshList = source.MeshList;
            MaterialList = source.MaterialList;
            TextureList = source.TextureList;
            AnimationList = source.AnimationList;
            MorphList = source.MorphList;
            CameraList = source.CameraList;
            LightList = source.LightList;
        }
    }

    public sealed class ImportedScriptSpawnMetadata
    {
        public string ScriptClass { get; set; } = string.Empty;
        public string FieldPath { get; set; } = string.Empty;
        public string SourceKind { get; set; } = string.Empty;
        public string Confidence { get; set; } = string.Empty;
        public long OwnerPathId { get; set; }
        public long AnchorTransformPathId { get; set; }
        public string TargetFileName { get; set; } = string.Empty;
        public long TargetPathId { get; set; }
        public string TargetGuid { get; set; } = string.Empty;
    }

    public sealed class ImportedFrameExtras
    {
        public long PathId { get; set; }
        public string SourceFile { get; set; } = string.Empty;
        public ushort Tag { get; set; }
        public int Layer { get; set; }
        public bool IsActive { get; set; } = true;
        public bool IsScriptSpawned { get; set; }
        public ImportedScriptSpawnMetadata? ScriptSpawn { get; set; }
    }

    public class ImportedCamera
    {
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public ImportedFrame? TargetFrame { get; set; }
        public bool IsOrthographic { get; set; }
        public float FieldOfViewDegrees { get; set; } = 60f;
        public float OrthographicSize { get; set; } = 5f;
        public float AspectRatio { get; set; }
        public float NearClipPlane { get; set; } = 0.3f;
        public float FarClipPlane { get; set; } = 1000f;
        public uint ClearFlags { get; set; } = 1;
        public float Depth { get; set; }
        public bool Enabled { get; set; } = true;
    }

    public enum ImportedLightType
    {
        Directional = 0,
        Point = 1,
        Spot = 2,
        Area = 3
    }

    public class ImportedLight
    {
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public ImportedFrame? TargetFrame { get; set; }
        public ImportedLightType Type { get; set; } = ImportedLightType.Point;
        public Color Color { get; set; } = new Color(1f, 1f, 1f, 1f);
        public float Intensity { get; set; } = 1f;
        public float Range { get; set; } = 10f;
        public float SpotAngleDegrees { get; set; } = 30f;
        public float InnerSpotAngleDegrees { get; set; }
        public int ShadowType { get; set; }
        public bool Enabled { get; set; } = true;
    }

    public class ImportedFrame
    {
        public string Name { get; set; } = string.Empty;
        public Vector3 LocalRotation { get; set; }
        public Vector3 LocalPosition { get; set; }
        public Vector3 LocalScale { get; set; }
        public ImportedFrame? Parent { get; set; }
        public ImportedFrameExtras? Extras { get; set; }
        public bool IsInitiallyHidden { get; set; }
        public string? HiddenReason { get; set; }

        private List<ImportedFrame> children;

        public ImportedFrame this[int i] => children[i];

        public int Count => children.Count;

        public string Path
        {
            get
            {
                var frame = this;
                var path = frame.Name;
                while (frame.Parent != null)
                {
                    frame = frame.Parent;
                    path = frame.Name + "/" + path;
                }
                return path;
            }
        }

        public ImportedFrame(int childrenCount = 0)
        {
            children = new List<ImportedFrame>(childrenCount);
        }

        public void AddChild(ImportedFrame obj)
        {
            children.Add(obj);
            obj.Parent?.Remove(obj);
            obj.Parent = this;
        }

        public void Remove(ImportedFrame frame)
        {
            children.Remove(frame);
        }

        public ImportedFrame? FindFrameByPath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }
            var name = path.Substring(path.LastIndexOf('/') + 1);
            foreach (var frame in FindChilds(name))
            {
                var framePath = frame.Path;
                if (string.Equals(framePath, path, StringComparison.Ordinal)
                    || (framePath.Length > path.Length
                        && framePath[framePath.Length - path.Length - 1] == '/'
                        && framePath.EndsWith(path, StringComparison.Ordinal)))
                {
                    return frame;
                }
            }
            return null;
        }

        public ImportedFrame? FindRelativeFrameWithPath(string path)
        {
            var subs = path.Split(new[] { '/' }, 2);
            foreach (var child in children)
            {
                if (child.Name == subs[0])
                {
                    if (subs.Length == 1)
                    {
                        return child;
                    }
                    else
                    {
                        var result = child.FindRelativeFrameWithPath(subs[1]);
                        if (result != null)
                            return result;
                    }
                }
            }
            return null;
        }

        public ImportedFrame? FindFrame(string name)
        {
            if (Name == name)
            {
                return this;
            }
            foreach (var child in children)
            {
                var frame = child.FindFrame(name);
                if (frame != null)
                {
                    return frame;
                }
            }
            return null;
        }

        public ImportedFrame? FindChild(string name, bool recursive = true)
        {
            foreach (var child in children)
            {
                if (recursive)
                {
                    var frame = child.FindFrame(name);
                    if (frame != null)
                    {
                        return frame;
                    }
                }
                else
                {
                    if (child.Name == name)
                    {
                        return child;
                    }
                }
            }
            return null;
        }

        public IEnumerable<ImportedFrame> FindChilds(string name)
        {
            if (Name == name)
            {
                yield return this;
            }
            foreach (var child in children)
            {
                foreach (var item in child.FindChilds(name))
                {
                    yield return item;
                }
            }
        }
    }

    public class ImportedMesh
    {
        public string Path { get; set; } = string.Empty;
        public string? SourceMeshName { get; set; }
        public string? GeometryKey { get; set; }
        public ImportedFrame? TargetFrame { get; set; }
        public List<ImportedVertex> VertexList { get; set; } = new List<ImportedVertex>();
        public List<ImportedSubmesh> SubmeshList { get; set; } = new List<ImportedSubmesh>();
        public List<ImportedBone>? BoneList { get; set; }
        public bool hasNormal { get; set; }
        public bool[] hasUV { get; set; } = Array.Empty<bool>();
        public bool hasTangent { get; set; }
        public bool hasColor { get; set; }
    }

    public class ImportedSubmesh
    {
        public List<ImportedFace> FaceList { get; set; } = new List<ImportedFace>();
        public string Material { get; set; } = string.Empty;
        public int BaseVertex { get; set; }
    }

    public class ImportedVertex
    {
        public Vector3 Vertex { get; set; }
        public Vector3 Normal { get; set; }
        public float[][] UV { get; set; } = Array.Empty<float[]>();
        public Vector4 Tangent { get; set; }
        public Color Color { get; set; }
        public float[] Weights { get; set; } = Array.Empty<float>();
        public int[] BoneIndices { get; set; } = Array.Empty<int>();
    }

    public class ImportedFace
    {
        public int[] VertexIndices { get; set; } = Array.Empty<int>();
    }

    public class ImportedBone
    {
        public string? Path { get; set; }
        public Matrix4x4 Matrix { get; set; }
    }

    public sealed class ImportedUnityShaderMetadata
    {
        public string ShaderName { get; set; } = string.Empty;
        public string Classification { get; set; } = string.Empty;
        public int? RenderQueue { get; set; }
        public string? RenderType { get; set; }
        public List<string> Keywords { get; set; } = new List<string>();
        public Dictionary<string, float> FloatProperties { get; set; } = new Dictionary<string, float>(StringComparer.Ordinal);
        public Dictionary<string, float[]> ColorProperties { get; set; } = new Dictionary<string, float[]>(StringComparer.Ordinal);
        public Dictionary<string, string> TextureProperties { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);
        public Dictionary<string, string> Tags { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    public class ImportedMaterial
    {
        public string Name { get; set; } = string.Empty;
        public Color Diffuse { get; set; }
        public Color Ambient { get; set; }
        public Color Specular { get; set; }
        public Color Emissive { get; set; }
        public Color Reflection { get; set; }
        public float Shininess { get; set; }
        public float Transparency { get; set; }
        public float? Metallic { get; set; }
        public float? Smoothness { get; set; }
        public float? Roughness { get; set; }
        public float? AlphaCutoff { get; set; }
        public float? SurfaceMode { get; set; }
        public float? BumpScale { get; set; }
        public float? OcclusionStrength { get; set; }
        public float? TransmissionFactor { get; set; }
        public float? Ior { get; set; }
        public bool IsUnlit { get; set; }
        public bool DoubleSided { get; set; }
        public ImportedUnityShaderMetadata? UnityShaderMetadata { get; set; }
        public List<ImportedMaterialTexture> Textures { get; set; } = new List<ImportedMaterialTexture>();
    }

    public class ImportedMaterialTexture
    {
        public string Name { get; set; } = string.Empty;
        public int Dest { get; set; }
        public Vector2 Offset { get; set; }
        public Vector2 Scale { get; set; }
    }

    public class ImportedTexture
    {
        public string Name { get; set; }
        public byte[]? Data { get; set; }

        public ImportedTexture(MemoryStream stream, string name)
        {
            Name = name;
            Data = stream.ToArray();
        }
    }

    public class ImportedKeyframedAnimation
    {
        public string Name { get; set; } = string.Empty;
        public float SampleRate { get; set; }
        public List<ImportedAnimationKeyframedTrack> TrackList { get; set; } = new List<ImportedAnimationKeyframedTrack>();

        public ImportedAnimationKeyframedTrack FindTrack(string? path)
        {
            var track = TrackList.Find(x => x.Path == path);
            if (track == null)
            {
                track = new ImportedAnimationKeyframedTrack { Path = path };
                TrackList.Add(track);
            }

            return track;
        }
    }

    public class ImportedAnimationKeyframedTrack
    {
        public string? Path { get; set; }
        public List<ImportedKeyframe<Vector3>> Scalings = new List<ImportedKeyframe<Vector3>>();
        public List<ImportedKeyframe<Vector3>> Rotations = new List<ImportedKeyframe<Vector3>>();
        public List<ImportedKeyframe<Vector3>> Translations = new List<ImportedKeyframe<Vector3>>();
        public List<ImportedKeyframe<float>> Visibilities = new List<ImportedKeyframe<float>>();
        public List<ImportedBlendShape> BlendShapes { get; set; } = new List<ImportedBlendShape>();

        public ImportedBlendShape? BlendShape
        {
            get => BlendShapes.Count > 0 ? BlendShapes[0] : null;
            set
            {
                if (value == null)
                {
                    BlendShapes.Clear();
                }
                else if (BlendShapes.Count == 0)
                {
                    BlendShapes.Add(value);
                }
                else
                {
                    BlendShapes[0] = value;
                }
            }
        }

        public ImportedBlendShape FindOrCreateBlendShape(string channelName)
        {
            for (int i = 0; i < BlendShapes.Count; i++)
            {
                if (string.Equals(BlendShapes[i].ChannelName, channelName, StringComparison.Ordinal))
                {
                    return BlendShapes[i];
                }
            }

            var blendShape = new ImportedBlendShape { ChannelName = channelName };
            BlendShapes.Add(blendShape);
            return blendShape;
        }
    }

    public class ImportedKeyframe<T>
    {
        public float time { get; set; }
        public T value { get; set; }
        public bool isStepped { get; set; }

        public ImportedKeyframe(float time, T value, bool isStepped = false)
        {
            this.time = time;
            this.value = value;
            this.isStepped = isStepped;
        }
    }

    public class ImportedBlendShape
    {
        public string ChannelName = string.Empty;
        public List<ImportedKeyframe<float>> Keyframes = new List<ImportedKeyframe<float>>();
    }

    public class ImportedMorph
    {
        public string Path { get; set; } = string.Empty;
        public List<ImportedMorphChannel> Channels { get; set; } = new List<ImportedMorphChannel>();
    }

    public class ImportedMorphChannel
    {
        public string Name { get; set; } = string.Empty;
        public List<ImportedMorphKeyframe> KeyframeList { get; set; } = new List<ImportedMorphKeyframe>();
    }

    public class ImportedMorphKeyframe
    {
        public bool hasNormals { get; set; }
        public bool hasTangents { get; set; }
        public float Weight { get; set; }
        public List<ImportedMorphVertex> VertexList { get; set; } = new List<ImportedMorphVertex>();
    }

    public class ImportedMorphVertex
    {
        public uint Index { get; set; }
        public ImportedVertex Vertex { get; set; } = new ImportedVertex();
    }

    public static class ImportedHelpers
    {
        public static ImportedMesh? FindMesh(string path, List<ImportedMesh> importedMeshList)
        {
            foreach (var mesh in importedMeshList)
            {
                if (mesh.Path == path)
                {
                    return mesh;
                }
            }

            return null;
        }

        public static ImportedMaterial? FindMaterial(string name, List<ImportedMaterial> importedMats)
        {
            foreach (var mat in importedMats)
            {
                if (mat.Name == name)
                {
                    return mat;
                }
            }

            return null;
        }

        public static ImportedTexture? FindTexture(string? name, List<ImportedTexture> importedTextureList)
        {
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }

            foreach (var tex in importedTextureList)
            {
                if (tex.Name == name)
                {
                    return tex;
                }
            }

            return null;
        }
    }
}
