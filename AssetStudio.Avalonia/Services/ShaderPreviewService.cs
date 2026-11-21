using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Texture2DDecoder;
using NumericsVector4 = System.Numerics.Vector4;
using NumericsVector3 = System.Numerics.Vector3;
using NumericsMatrix4x4 = System.Numerics.Matrix4x4;

namespace AssetStudio.Avalonia.Services;

public sealed record ShaderPreviewFrame(int Width, int Height, byte[] RgbaPixels);

internal abstract record ShaderPreviewOperation;
internal sealed record ShaderPreviewDraw(LegacyShaderPreviewPass Pass, ShaderPreviewLight Light, int SkyboxSide = -1) : ShaderPreviewOperation;
internal sealed record ShaderPreviewGrab(string TextureName, bool Once) : ShaderPreviewOperation;
internal readonly record struct ShaderPreviewLight(NumericsVector3 Direction, NumericsVector3 Color);
internal enum ShaderPreviewGeometry { Sphere, Plane, Volume, Skybox, Particles }

internal sealed record ShaderPreviewScene(
    string ShaderName, ShaderPreviewGeometry Geometry, bool IsAnimated, IReadOnlyList<ShaderPreviewOperation> Operations,
    NumericsVector3 AmbientLinear, IReadOnlyList<ShaderPreviewLight> Lights,
    IReadOnlyDictionary<string, NumericsVector4> Values,
    IReadOnlyDictionary<string, ShaderPreviewTexture> Textures, IReadOnlyList<string> UnusedMaterialKeywords);

internal sealed record ShaderPreviewTexture(
    int Width, int Height, int MipCount, int Format, int BytesPerBlock, bool BlockCompressed, byte[] Pixels,
    int Filter, int Wrap, int Anisotropy, float MipBias, int Faces = 1);

internal readonly record struct ShaderPreviewCamera(float Yaw, float Pitch, float Distance, System.Numerics.Vector2 Pan = default)
{
    public static ShaderPreviewCamera Default => new(0, 0, 4);
    public const float Near = 0.1f, Far = 100;
}

internal static class ShaderPreviewService
{
    internal const int RenderSize = 384;
    private static readonly ShaderPreviewLight[] PreviewLights =
    [
        new(NumericsVector3.Normalize(new NumericsVector3(-0.45f, 0.7f, 0.65f)), new NumericsVector3(0.82f))
    ];
    private static readonly NumericsVector3 PreviewAmbientLinear = new(0.045f, 0.05f, 0.06f);
    private static readonly string[] PreviewKeywords =
    [
        "DIRECTIONAL", "SHADOWS_OFF", "LIGHTMAP_OFF", "DIRLIGHTMAP_OFF", "DYNAMICLIGHTMAP_OFF", "SOFTPARTICLES_OFF"
    ];

    internal static bool UsesShaderPreview(int[] version) => OperatingSystem.IsWindows() && version.Length >= 2 && version[0] == 5 && version[1] == 3;

    internal static T ResolveReference<T>(PPtr<T> reference, SerializedFile source,
        Action<SerializedFile, CancellationToken> loadExternalSources, Func<AssetHandle, AssetStudio.Object?> resolveAsset,
        CancellationToken token) where T : AssetStudio.Object
    {
        token.ThrowIfCancellationRequested();
        if (reference.IsNull)
        {
            throw new InvalidDataException($"The material has an empty {typeof(T).Name} reference.");
        }
        if (!reference.TryGetAssetsFile(out var target) && source.assetsManager.LazyLoading)
        {
            loadExternalSources(source, token);
            reference.ResetCache();
            reference.TryGetAssetsFile(out target);
        }
        if (target == null)
        {
            throw new InvalidDataException($"The source file for the {typeof(T).Name} reference could not be loaded.");
        }
        if (target.TryGetObject(reference.m_PathID, out var asset) && asset is T typed)
        {
            return typed;
        }
        var handle = source.assetsManager.ProjectIndex.GetHandle(target, reference.m_PathID);
        if (handle != null && resolveAsset(handle) is T resolved)
        {
            return resolved;
        }
        throw new InvalidDataException($"The {typeof(T).Name} reference {reference.m_PathID} could not be resolved in '{target.fileName}'.");
    }

    internal static ShaderPreviewScene Prepare(Shader shader, Material? material,
        Func<Shader, PPtr<Shader>, Shader> resolveShader, Func<Material, PPtr<Texture>, Texture> resolveTexture,
        CancellationToken cancellationToken, IReadOnlyList<ShaderPreviewLight>? lights = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (material != null)
        {
            var reference = material.m_Shader;
            if (reference.IsNull || reference.m_PathID != shader.m_PathID ||
                !reference.TryGetAssetsFile(out var file) ||
                AssetHandle.BuildUniqueID(file, reference.m_PathID) != AssetHandle.BuildUniqueID(shader.assetsFile, shader.m_PathID))
            {
                throw new InvalidOperationException("The selected material does not reference this shader.");
            }
        }
        lights ??= PreviewLights;
        if (lights.Count == 0 || lights.Count > 8)
        {
            throw new ArgumentException("The Built-in preview scene requires between one and eight directional lights.", nameof(lights));
        }
        var reader = LegacyShaderPreview.Read(shader);
        var values = new Dictionary<string, NumericsVector4>(reader.Values, StringComparer.Ordinal);
        var textureSlots = new Dictionary<string, UnityTexEnv>(StringComparer.Ordinal);
        if (material != null)
        {
            foreach (var (name, value) in material.m_SavedProperties.m_Floats)
            {
                values[name] = new NumericsVector4(value, 0, 0, 0);
            }
            foreach (var (name, value) in material.m_SavedProperties.m_Ints)
            {
                values[name] = new NumericsVector4(value, 0, 0, 0);
            }
            foreach (var (name, value) in material.m_SavedProperties.m_Colors)
            {
                values[name] = new NumericsVector4(value.R, value.G, value.B, value.A);
            }
            foreach (var (name, slot) in material.m_SavedProperties.m_TexEnvs)
            {
                textureSlots.Add(name, slot);
            }
        }
        foreach (string name in reader.TextureDefaults.Keys.Concat(textureSlots.Keys).Distinct(StringComparer.Ordinal))
        {
            values[name + "_ST"] = textureSlots.TryGetValue(name, out var slot)
                ? new NumericsVector4(slot.m_Scale.X, slot.m_Scale.Y, slot.m_Offset.X, slot.m_Offset.Y)
                : new NumericsVector4(1, 1, 0, 0);
        }

        var subShader = reader.SubShaders.FirstOrDefault(sub => IsBuiltIn(sub) &&
            sub.Passes.Any(pass => pass.Type == PassType.Use || pass.HasDirect3D11))
            ?? throw new NotSupportedException($"'{reader.Name}' has no Built-in SubShader with a D3D11 color pass.");
        var geometry = subShader.Tags.GetValueOrDefault("PreviewType", "sphere").ToLowerInvariant() switch
        {
            "sphere" when reader.Name.StartsWith("Particles/", StringComparison.OrdinalIgnoreCase) => ShaderPreviewGeometry.Particles,
            "sphere" when reader.Name.Equals("Hidden/FogVolume", StringComparison.Ordinal) => ShaderPreviewGeometry.Volume,
            "sphere" when subShader.Tags.ContainsKey("WaterMode") || reader.Name.StartsWith("UI/", StringComparison.Ordinal) => ShaderPreviewGeometry.Plane,
            "sphere" => ShaderPreviewGeometry.Sphere,
            "plane" => ShaderPreviewGeometry.Plane,
            "skybox" => ShaderPreviewGeometry.Skybox,
            var type => throw new NotSupportedException($"Unsupported ShaderLab PreviewType '{type}'.")
        };
        // Screen-space preview geometry is independent from the game's depth ordering.
        values.TryAdd("unity_GUIZTestMode", new NumericsVector4(8, 0, 0, 0));
        var passes = ExpandPasses(shader, reader, subShader, resolveShader, cancellationToken).ToArray();
        bool forward = passes.Any(item => IsMode(item.Pass, "ForwardBase"));
        var materialKeywords = new HashSet<string>(material?.m_ValidKeywords ?? [], StringComparer.Ordinal);
        var enabled = new HashSet<string>(materialKeywords, StringComparer.Ordinal);
        enabled.UnionWith(PreviewKeywords);
        if (subShader.Tags.TryGetValue("WaterMode", out string? waterMode) && !enabled.Overlaps(["WATER_REFRACTIVE", "WATER_REFLECTIVE", "WATER_SIMPLE"]))
        {
            enabled.Add("WATER_" + waterMode.ToUpperInvariant());
        }
        if (!enabled.Contains("FOG_VOLUME_INSCATTERING_ON"))
        {
            enabled.Add("FOG_VOLUME_INSCATTERING_OFF");
        }
        var known = new HashSet<string>(passes.Where(item => item.Pass.Type == PassType.Normal && item.Pass.HasDirect3D11)
            .SelectMany(item => item.Reader.GetKeywords(item.Pass)), StringComparer.Ordinal);
        // Unity's keyword list belongs to the material. A program observes only the keywords its shader declares.
        string[] unusedKeywords = materialKeywords.Except(known, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        string[] missingKeywords = unusedKeywords.Where(reader.PropertyKeywords.Contains).ToArray();
        if (missingKeywords.Length != 0)
        {
            throw new NotSupportedException($"There is no compiled D3D11 variant for declared material keywords [{string.Join(", ", missingKeywords)}].");
        }

        var operations = new List<ShaderPreviewOperation>();
        var textures = new Dictionary<string, ShaderPreviewTexture>(StringComparer.Ordinal);
        var captures = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (owner, ownerSub, definition) in passes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (definition.Type == PassType.Grab)
            {
                operations.Add(new ShaderPreviewGrab(definition.Name, definition.CaptureOnce));
                captures.Add(definition.Name);
                continue;
            }
            bool additive = IsMode(definition, "ForwardAdd");
            if (!IsColorPass(definition) || (forward && IsMode(definition, "Vertex")) || (additive && lights.Count == 1)
                || IsShadowOnly(owner, definition))
            {
                continue;
            }
            try
            {
                var pass = owner.PreparePass(ownerSub, definition, enabled, values);
                foreach (var binding in pass.Vertex.Textures.Concat(pass.Fragment.Textures))
                {
                    PrepareTexture(binding.Name, binding.Dimension);
                }
                foreach (string name in pass.Vertex.Buffers.Concat(pass.Fragment.Buffers).SelectMany(buffer => buffer.Parameters)
                    .Select(parameter => parameter.Name).Where(name => name.EndsWith("_TexelSize", StringComparison.Ordinal)))
                {
                    string textureName = name[..^"_TexelSize".Length];
                    if (!textures.ContainsKey(textureName) && !captures.Contains(textureName) &&
                        (reader.TextureDefaults.ContainsKey(textureName) || textureSlots.ContainsKey(textureName)))
                    {
                        PrepareTexture(textureName, ShaderPreviewTextureDimension.Texture2D);
                    }
                }
                if (additive)
                {
                    for (int i = 1; i < lights.Count; i++)
                    {
                        operations.Add(new ShaderPreviewDraw(pass, lights[i]));
                    }
                }
                else
                {
                    operations.Add(new ShaderPreviewDraw(pass, lights[0], geometry == ShaderPreviewGeometry.Skybox ? GetSkyboxSide(pass) : -1));
                }
            }
            catch (Exception error) when (error is NotSupportedException or InvalidDataException)
            {
                throw new NotSupportedException($"{owner.Name} · {definition.DisplayName}. {error.Message}", error);
            }
        }
        if (!operations.OfType<ShaderPreviewDraw>().Any())
        {
            throw new NotSupportedException($"'{reader.Name}' has no D3D11 pass for the Built-in preview scene.");
        }
        bool animated = geometry == ShaderPreviewGeometry.Particles || operations.OfType<ShaderPreviewDraw>().Any(draw =>
            draw.Pass.Vertex.Buffers.Concat(draw.Pass.Fragment.Buffers).SelectMany(buffer => buffer.Parameters)
                .Any(parameter => parameter.Name is "_Time" or "_SinTime" or "_CosTime" or "unity_DeltaTime" or "_WaveOffset"));
        return new ShaderPreviewScene(reader.Name, geometry, animated, operations, PreviewAmbientLinear, lights, values, textures, unusedKeywords);

        void PrepareTexture(string name, ShaderPreviewTextureDimension dimension)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool cube = dimension == ShaderPreviewTextureDimension.Cube;
            if (captures.Contains(name))
            {
                if (cube)
                {
                    throw new InvalidDataException($"GrabPass texture '{name}' must be a 2D resource.");
                }
                return;
            }
            if (textures.TryGetValue(name, out var prepared))
            {
                if ((prepared.Faces == 6) != cube)
                {
                    throw new InvalidDataException($"Conflicting texture dimensions for '{name}'.");
                }
                return;
            }
            ShaderPreviewTexture texture;
            if (material != null && textureSlots.TryGetValue(name, out var slot) && !slot.m_Texture.IsNull)
            {
                texture = ReadTexture(resolveTexture(material, slot.m_Texture), dimension);
            }
            else if (reader.TextureDefaults.TryGetValue(name, out string? declared))
            {
                texture = CreateDeclaredTexture(name, declared, cube);
            }
            else if (cube && name is "unity_SpecCube0" or "unity_SpecCube1")
            {
                texture = CreateReflectionProbe(PreviewAmbientLinear);
            }
            else if (!cube && name is "_CameraDepthTexture" or "_DepthTex")
            {
                // Depth of the preview's backdrop, cleared to the camera's far plane.
                var depth = new byte[sizeof(float)];
                BinaryPrimitives.WriteSingleLittleEndian(depth, 1f);
                texture = new ShaderPreviewTexture(1, 1, 1, 41 /* R32_FLOAT */, 4, false, depth, 0, 1, 1, 0);
            }
            else if (!cube && name is "_ClipTex" or "_GUIClipTexture")
            {
                texture = CreateClipMask();
            }
            else
            {
                throw new NotSupportedException($"No material texture or scene resource is available for '{name}'.");
            }
            textures.Add(name, texture);
        }
    }

    private static bool IsBuiltIn(ShaderPreviewSubShader subShader) =>
        !subShader.Tags.TryGetValue("RenderPipeline", out string? pipeline) || pipeline.Length == 0;

    private static bool IsMode(ShaderPreviewPassDefinition pass, string mode) =>
        pass.LightMode.Equals(mode, StringComparison.OrdinalIgnoreCase);

    private static bool IsColorPass(ShaderPreviewPassDefinition pass) =>
        pass.Type == PassType.Normal && (IsMode(pass, "Always") || IsMode(pass, "ForwardBase")
            || IsMode(pass, "ForwardAdd") || IsMode(pass, "Vertex"));

    private static bool IsShadowOnly(LegacyShaderPreview reader, ShaderPreviewPassDefinition pass) =>
        IsMode(pass, "Always") && pass.HasDirect3D11 && reader.GetVariantKeywords(pass, vertex: true)
            .All(keywords => keywords.Contains("SHADOWS_DEPTH") || keywords.Contains("SHADOWS_CUBE"));

    private static int GetSkyboxSide(LegacyShaderPreviewPass pass)
    {
        string[] faces = ["_FrontTex", "_BackTex", "_LeftTex", "_RightTex", "_UpTex", "_DownTex"];
        return Array.FindIndex(faces, face => pass.Fragment.Textures.Any(texture => texture.Name == face));
    }

    private static IEnumerable<(LegacyShaderPreview Reader, ShaderPreviewSubShader Sub, ShaderPreviewPassDefinition Pass)> ExpandPasses(
        Shader shader, LegacyShaderPreview reader, ShaderPreviewSubShader subShader, Func<Shader, PPtr<Shader>, Shader> resolve,
        CancellationToken token)
    {
        var dependencies = new Dictionary<string, LegacyShaderPreview>(StringComparer.Ordinal);
        foreach (var pass in subShader.Passes)
        {
            token.ThrowIfCancellationRequested();
            if (pass.Type != PassType.Use)
            {
                yield return (reader, subShader, pass);
                continue;
            }

            int separator = pass.Name.LastIndexOf('/');
            if (separator <= 0 || separator == pass.Name.Length - 1)
            {
                throw new InvalidDataException($"Invalid ShaderLab UsePass reference '{pass.Name}'.");
            }
            string shaderName = pass.Name[..separator];
            string passName = pass.Name[(separator + 1)..];
            if (!dependencies.TryGetValue(shaderName, out var referenced))
            {
                referenced = ResolveReferencedShader(shaderName);
                dependencies.Add(shaderName, referenced);
            }
            var sourceSub = referenced.SubShaders.FirstOrDefault(sub => IsBuiltIn(sub) &&
                sub.Passes.Any(p => p.HasDirect3D11 && p.Name.Equals(passName, StringComparison.OrdinalIgnoreCase)));
            if (sourceSub == null)
            {
                throw new NotSupportedException($"UsePass '{pass.Name}' has no named D3D11 pass in its referenced shader.");
            }
            foreach (var candidate in sourceSub.Passes)
            {
                if (candidate.Type == PassType.Normal && candidate.Name.Equals(passName, StringComparison.OrdinalIgnoreCase))
                {
                    yield return (referenced, sourceSub, candidate);
                }
            }
        }

        LegacyShaderPreview ResolveReferencedShader(string shaderName)
        {
            Shader? match = null;
            foreach (var dependency in shader.m_Dependencies)
            {
                token.ThrowIfCancellationRequested();
                Shader linked;
                string actual;
                try
                {
                    linked = resolve(shader, dependency);
                    actual = LegacyShaderPreview.ReadName(linked);
                }
                catch (Exception error) when (error is InvalidDataException or EndOfStreamException or DecoderFallbackException)
                {
                    continue;
                }
                if (!actual.Equals(shaderName, StringComparison.Ordinal))
                {
                    continue;
                }
                if (match != null && AssetHandle.BuildUniqueID(match.assetsFile, match.m_PathID) !=
                    AssetHandle.BuildUniqueID(linked.assetsFile, linked.m_PathID))
                {
                    throw new InvalidDataException($"UsePass shader '{shaderName}' resolves to multiple asset references.");
                }
                match = linked;
            }
            return match != null ? LegacyShaderPreview.Read(match)
                : throw new InvalidDataException($"UsePass shader '{shaderName}' could not be resolved from the shader dependencies.");
        }
    }

    private static ShaderPreviewTexture CreateDeclaredTexture(string name, string declared, bool cube)
    {
        if (cube && declared.Equals("_Skybox", StringComparison.Ordinal))
        {
            return CreateReflectionProbe(PreviewAmbientLinear);
        }
        byte[] value = declared switch
        {
            "" when cube => [128, 128, 128, 255],
            "white" or "" => [255, 255, 255, 255],
            "gray" or "grey" => [128, 128, 128, 255],
            "black" => [0, 0, 0, 255],
            "red" => [255, 0, 0, 255],
            "bump" when !cube => [128, 128, 255, 128],
            _ => throw new NotSupportedException($"Texture '{name}' has unsupported ShaderLab default '{declared}'.")
        };
        return CreateSolidTexture(value, cube ? 6 : 1);
    }

    private static ShaderPreviewTexture CreateClipMask()
    {
        const int size = 32;
        var pixels = new byte[size * size * 4];
        for (int y = 1; y < size - 1; y++)
        {
            pixels.AsSpan((y * size + 1) * 4, (size - 2) * 4).Fill(255);
        }
        return new ShaderPreviewTexture(size, size, 1, 28, 4, false, pixels, 1, 1, 1, 0);
    }

    private static ShaderPreviewTexture CreateSolidTexture(byte[] color, int faces)
    {
        var pixels = new byte[4 * faces];
        for (int face = 0; face < faces; face++)
        {
            color.CopyTo(pixels, face * 4);
        }
        return new ShaderPreviewTexture(1, 1, 1, 28 /* R8G8B8A8_UNORM */, 4, false, pixels, 0, 1, 1, 0, faces);
    }

    private static ShaderPreviewTexture CreateReflectionProbe(NumericsVector3 ambient)
    {
        // A constant environment has the same value in every direction and at every roughness.
        static byte Encode(float value) => (byte)Math.Clamp(MathF.Round(MathF.Pow(value, 1 / 2.2f) * 255), 0, 255);
        return CreateSolidTexture([Encode(ambient.X), Encode(ambient.Y), Encode(ambient.Z), 255], 6);
    }

    private static ShaderPreviewTexture ReadTexture(Texture asset, ShaderPreviewTextureDimension dimension)
    {
        Texture2D texture = (asset, dimension) switch
        {
            (Texture2D image, ShaderPreviewTextureDimension.Texture2D) => image,
            (Cubemap cubemap, ShaderPreviewTextureDimension.Cube) => cubemap.Image,
            _ => throw new NotSupportedException($"Texture '{asset.m_Name}' does not match the required {dimension} resource.")
        };
        GLTextureSettings settings = texture.m_TextureSettings;
        int width = texture.m_Width;
        int height = texture.m_Height;
        int mips = texture.m_MipCount;
        int faces = dimension == ShaderPreviewTextureDimension.Cube ? 6 : 1;
        if (width is <= 0 or > 16384 || height is <= 0 or > 16384 ||
            mips < 1 || mips > 1 + BitOperations.Log2((uint)Math.Max(width, height)) ||
            (faces == 6 && (width != height || texture.m_ImageCount != faces || texture.m_TextureDimension is not (2 or 4))) ||
            texture.image_data.Size is <= 0 or > 128 * 1024 * 1024)
        {
            throw new InvalidDataException($"Invalid texture dimensions or mip data for '{texture.m_Name}'.");
        }
        if (settings.m_FilterMode is < 0 or > 2 || settings.m_WrapMode is < 0 or > 3 ||
            settings.m_Aniso is < 0 or > 16 || !float.IsFinite(settings.m_MipBias))
        {
            throw new NotSupportedException($"Unsupported sampling settings on texture '{texture.m_Name}'.");
        }

        var (format, bytesPerBlock, blockCompressed) = texture.m_TextureFormat switch
        {
            TextureFormat.DXT1 or TextureFormat.DXT1Crunched => (71 /* BC1_UNORM */, 8, true),
            TextureFormat.DXT5 or TextureFormat.DXT5Crunched => (77 /* BC3_UNORM */, 16, true),
            TextureFormat.BC4 => (80 /* BC4_UNORM */, 8, true),
            TextureFormat.BC5 => (83 /* BC5_UNORM */, 16, true),
            TextureFormat.BC6H => (95 /* BC6H_UF16 */, 16, true),
            TextureFormat.BC7 => (98 /* BC7_UNORM */, 16, true),
            TextureFormat.Alpha8 => (65 /* A8_UNORM */, 1, false),
            TextureFormat.RGB565 => (85 /* B5G6R5_UNORM */, 2, false),
            TextureFormat.ARGB4444 => (115 /* B4G4R4A4_UNORM */, 2, false),
            TextureFormat.BGRA32 => (87 /* B8G8R8A8_UNORM */, 4, false),
            TextureFormat.RGBA32 or TextureFormat.ARGB32 or TextureFormat.RGB24 => (28 /* R8G8B8A8_UNORM */, 4, false),
            _ => throw new NotSupportedException($"Unsupported preview texture format {texture.m_TextureFormat} on '{texture.m_Name}'.")
        };

        byte[] data = texture.image_data.GetData();
        if (texture.m_TextureFormat is TextureFormat.DXT1Crunched or TextureFormat.DXT5Crunched)
        {
            if (faces != 1)
            {
                throw new NotSupportedException($"Crunch cubemap '{texture.m_Name}' is not a supported preview texture.");
            }
            data = UnpackCrunchedMips(texture.m_Name, data, width, height, mips, bytesPerBlock);
        }
        else if (texture.m_TextureFormat == TextureFormat.RGB24)
        {
            if (data.Length % 3 != 0)
            {
                throw new InvalidDataException($"Invalid RGB24 data on '{texture.m_Name}'.");
            }
            var rgba = new byte[data.Length / 3 * 4];
            for (int source = 0, destination = 0; source < data.Length; source += 3, destination += 4)
            {
                data.AsSpan(source, 3).CopyTo(rgba.AsSpan(destination, 3));
                rgba[destination + 3] = 255;
            }
            data = rgba;
        }
        else if (texture.m_TextureFormat == TextureFormat.ARGB32)
        {
            if (data.Length % 4 != 0)
            {
                throw new InvalidDataException($"Invalid ARGB32 data on '{texture.m_Name}'.");
            }
            for (int i = 0; i < data.Length; i += 4)
            {
                (data[i], data[i + 1], data[i + 2], data[i + 3]) = (data[i + 1], data[i + 2], data[i + 3], data[i]);
            }
        }
        return new ShaderPreviewTexture(width, height, mips, format, bytesPerBlock, blockCompressed, data,
            settings.m_FilterMode, settings.m_WrapMode, settings.m_Aniso, settings.m_MipBias, faces);
    }

    private static byte[] UnpackCrunchedMips(string name, byte[] data, int width, int height, int mips, int bytesPerBlock)
    {
        var mipLengths = new int[mips];
        for (int level = 0; level < mips; level++)
        {
            int columns = (Math.Max(1, width >> level) + 3) / 4;
            int rows = (Math.Max(1, height >> level) + 3) / 4;
            mipLengths[level] = checked(columns * rows * bytesPerBlock);
        }
        int total = mipLengths.Sum();
        if (total > 128 * 1024 * 1024)
        {
            throw new InvalidDataException($"Crunch texture '{name}' exceeds the preview texture size limit.");
        }
        var result = new byte[total];
        int offset = 0;
        for (int level = 0; level < mipLengths.Length; level++)
        {
            byte[]? mip = TextureDecoder.UnpackCrunchMip(data, (uint)level);
            if (mip == null || mip.Length != mipLengths[level])
            {
                throw new InvalidDataException($"Could not unpack Crunch texture '{name}' at mip {level}.");
            }
            mip.CopyTo(result, offset);
            offset += mip.Length;
        }
        return result;
    }

    internal static byte[] BindConstants(ShaderPreviewScene scene, ShaderPreviewBuffer buffer, ShaderPreviewLight light,
        NumericsMatrix4x4 view, NumericsMatrix4x4 projection, NumericsVector3 eye, int size, IReadOnlySet<string> captures,
        float time, float deltaTime)
    {
        var data = new byte[(buffer.Size + 15) & ~15];
        foreach (var parameter in buffer.Parameters)
        {
            if (parameter.Type is not (0 or 1) || parameter.ArraySize < 1 || parameter.Offset < 0)
            {
                throw new NotSupportedException($"Unsupported shader parameter format for '{parameter.Name}'.");
            }
            if (parameter.IsMatrix)
            {
                if (parameter.Type != 0 || parameter.ArraySize != 1 || parameter.Rows != 4
                    || parameter.Columns != 4 || parameter.Offset > data.Length - 64)
                {
                    throw new NotSupportedException($"Unsupported shader matrix layout for '{parameter.Name}'.");
                }
                NumericsMatrix4x4 matrix = parameter.Name switch
                {
                    "glstate_matrix_mvp" or "unity_MatrixMVP" or "unity_MatrixVP" => view * projection,
                    "glstate_matrix_modelview0" or "unity_MatrixV" or "unity_MatrixMV" => view,
                    "glstate_matrix_projection" or "unity_MatrixP" => projection,
                    "glstate_matrix_invtrans_modelview0" or "unity_MatrixITMV" => NumericsMatrix4x4.Transpose(Inverse(view)),
                    "unity_MatrixInvV" or "_CameraToWorld" => Inverse(view),
                    "_GUIClipTextureMatrix" => NumericsMatrix4x4.CreateScale(0.5f, 0.5f, 1) * NumericsMatrix4x4.CreateTranslation(0.5f, 0.5f, 0),
                    "_TerrainEngineBendTree" => NumericsMatrix4x4.CreateRotationZ(0.08f * MathF.Sin(time)),
                    "_Object2World" or "_World2Object" or "unity_ObjectToWorld" or "unity_WorldToObject" => NumericsMatrix4x4.Identity,
                    _ => throw MissingValue(parameter.Name)
                };
                MemoryMarshal.Write(data.AsSpan(parameter.Offset), in matrix);
            }
            else
            {
                int length = parameter.Columns * sizeof(float);
                if (parameter.Rows != 1 || parameter.Columns is < 1 or > 4 ||
                    parameter.Offset > data.Length - length || parameter.ArraySize - 1 > (data.Length - length - parameter.Offset) / 16)
                {
                    throw new NotSupportedException($"Unsupported shader vector layout for '{parameter.Name}'.");
                }
                for (int element = 0; element < parameter.ArraySize; element++)
                {
                    NumericsVector4 value = ResolveVector(parameter.Name, parameter.ArraySize, element);
                    var destination = data.AsSpan(parameter.Offset + 16 * element, length);
                    var components = MemoryMarshal.Cast<NumericsVector4, float>(MemoryMarshal.CreateReadOnlySpan(in value, 1));
                    if (parameter.Type == 0)
                    {
                        MemoryMarshal.AsBytes(components)[..length].CopyTo(destination);
                    }
                    else
                    {
                        for (int component = 0; component < parameter.Columns; component++)
                        {
                            float integer = components[component];
                            if (!float.IsFinite(integer) || MathF.Truncate(integer) != integer || integer < int.MinValue || integer >= 2147483648f)
                            {
                                throw new InvalidDataException($"Shader parameter '{parameter.Name}' requires a 32-bit integer.");
                            }
                            BinaryPrimitives.WriteInt32LittleEndian(destination[(component * 4)..], (int)integer);
                        }
                    }
                }
            }
        }
        return data;

        NumericsVector4 ResolveVector(string name, int arraySize, int element)
        {
            if (TryVertexLight(name, arraySize, element, scene.Lights, view, out var vertexLight) ||
                TryTerrainImposterLight(buffer.Name, name, scene.Lights, out vertexLight))
            {
                return vertexLight;
            }
            if (arraySize != 1)
            {
                throw MissingValue(name);
            }
            if (scene.Values.TryGetValue(name, out var property))
            {
                return property;
            }
            if (name.EndsWith("_TexelSize", StringComparison.Ordinal))
            {
                string textureName = name[..^"_TexelSize".Length];
                if (captures.Contains(textureName))
                {
                    return new NumericsVector4(1f / size, 1f / size, size, size);
                }
                if (scene.Textures.TryGetValue(textureName, out var texture))
                {
                    return new NumericsVector4(1f / texture.Width, 1f / texture.Height, texture.Width, texture.Height);
                }
            }
            if (name.EndsWith("_HDR", StringComparison.Ordinal) && scene.Textures.ContainsKey(name[..^"_HDR".Length]))
            {
                // The preview samples unencoded LDR color or linear BC6H.
                return new NumericsVector4(1, 1, 0, 0);
            }
            float near = ShaderPreviewCamera.Near, far = ShaderPreviewCamera.Far;
            const float detailDistanceSquared = 100 * 100;
            return name switch
            {
                "_WorldSpaceCameraPos" => new NumericsVector4(eye, 1),
                "_ScreenParams" => new NumericsVector4(size, size, 1f + 1f / size, 1f + 1f / size),
                "_ClipRange" or "_ClipRange0" or "_ClipRange1" or "_ClipRange2" or "_ClipRange3" => new NumericsVector4(0, 0, 1, 1),
                "_ClipArgs0" or "_ClipArgs1" or "_ClipArgs2" or "_ClipArgs3" => new NumericsVector4(16, 16, 0, 1),
                "_ClipSharpness" => new NumericsVector4(16, 16, 0, 0),
                "_ClipRect" => new NumericsVector4(-1, -1, 1, 1),
                "_TextureSampleAdd" => scene.Textures.TryGetValue("_MainTex", out var main) && main.Format == 65
                    ? new NumericsVector4(1, 1, 1, 0) : NumericsVector4.Zero,
                "_Level" when scene.ShaderName == "Hidden/CubeBlend" => NumericsVector4.Zero,
                "_BoxMin" when scene.Geometry == ShaderPreviewGeometry.Volume => new NumericsVector4(-1.0102f, -1.0102f, -1.0102f, 0),
                "_BoxMax" when scene.Geometry == ShaderPreviewGeometry.Volume => new NumericsVector4(1.0102f, 1.0102f, 1.0102f, 0),
                "_Color" when scene.Geometry == ShaderPreviewGeometry.Volume => new NumericsVector4(0.5f, 0.6f, 0.7f, 1),
                "_Visibility" when scene.Geometry == ShaderPreviewGeometry.Volume => new NumericsVector4(5, 0, 0, 0),
                "_WaveScale4" => WaveScale(),
                "_WaveOffset" => WaveOffset(),
                "_HalfOverCutoff" => new NumericsVector4(0.5f / scene.Values["_Cutoff"].X),
                "_Wind" => new NumericsVector4(0.09f, 0, 0.04f, 0.4f),
                "_SquashAmount" => NumericsVector4.One,
                "_SquashPlaneNormal" => new NumericsVector4(0, 1, 0, 0),
                "_TreeInstanceColor" or "_TreeInstanceScale" or "_WavingTint" => NumericsVector4.One,
                "_TranslucencyColor" or "_VertexLitTranslucencyColor" when scene.ShaderName.StartsWith("Nature/", StringComparison.Ordinal) ||
                    scene.ShaderName.StartsWith("Hidden/TerrainEngine/", StringComparison.Ordinal) => new NumericsVector4(0.7f, 0.8f, 0.65f, 1),
                "_TranslucencyViewDependency" when scene.ShaderName.StartsWith("Nature/", StringComparison.Ordinal) => new NumericsVector4(0.7f, 0, 0, 0),
                "_CameraPosition" => new NumericsVector4(eye, 1f / detailDistanceSquared),
                "_CameraRight" or "_TreeBillboardCameraRight" => new NumericsVector4(view.M11, view.M21, view.M31, 0),
                "_CameraUp" or "_TreeBillboardCameraUp" => new NumericsVector4(view.M12, view.M22, view.M32, 0),
                "_TreeBillboardCameraFront" => new NumericsVector4(view.M13, view.M23, view.M33, 0),
                "_TreeBillboardCameraPos" => new NumericsVector4(eye, MathF.Abs(view.M23)),
                "_TreeBillboardDistances" => new NumericsVector4(detailDistanceSquared, 0, 0, 0),
                "_WaveAndDistance" => new NumericsVector4(time * 0.45f, 1, 0.22f, detailDistanceSquared),
                "_ProjectionParams" => new NumericsVector4(1, near, far, 1f / far),
                "_ZBufferParams" => new NumericsVector4(1 - far / near, far / near, (1 - far / near) / far, 1f / near),
                "_Time" => new NumericsVector4(time / 20, time, time * 2, time * 3),
                "_SinTime" => new NumericsVector4(MathF.Sin(time / 8), MathF.Sin(time / 4), MathF.Sin(time / 2), MathF.Sin(time)),
                "_CosTime" => new NumericsVector4(MathF.Cos(time / 8), MathF.Cos(time / 4), MathF.Cos(time / 2), MathF.Cos(time)),
                "unity_DeltaTime" => deltaTime > 0 ? new NumericsVector4(deltaTime, 1 / deltaTime, deltaTime, 1 / deltaTime) : NumericsVector4.Zero,
                "_WorldSpaceLightPos0" => new NumericsVector4(light.Direction, 0),
                "_LightColor0" => new NumericsVector4(light.Color, 1),
                "unity_ColorSpaceLuminance" => new NumericsVector4(0.22f, 0.707f, 0.071f, 0),
                "unity_ColorSpaceDielectricSpec" => new NumericsVector4(0.2209163f, 0.2209163f, 0.2209163f, 0.7790837f),
                "unity_ColorSpaceDouble" => new NumericsVector4(2),
                "unity_WorldTransformParams" => new NumericsVector4(0, 0, 0, 1),
                "unity_SHAr" => new NumericsVector4(0, 0, 0, scene.AmbientLinear.X),
                "unity_SHAg" => new NumericsVector4(0, 0, 0, scene.AmbientLinear.Y),
                "unity_SHAb" => new NumericsVector4(0, 0, 0, scene.AmbientLinear.Z),
                "unity_SHBr" or "unity_SHBg" or "unity_SHBb" or "unity_SHC" => NumericsVector4.Zero,
                "unity_VertexLightParams" => new NumericsVector4(scene.Lights.Count, 0, 0, 0),
                "glstate_lightmodel_ambient" or "UNITY_LIGHTMODEL_AMBIENT" or "unity_AmbientSky"
                    or "unity_AmbientEquator" or "unity_AmbientGround" => new NumericsVector4(ToGamma(scene.AmbientLinear), 1),
                "unity_SpecCube0_HDR" or "unity_SpecCube1_HDR" => new NumericsVector4(1, 1, 0, 0),
                "unity_SpecCube0_ProbePosition" or "unity_SpecCube1_ProbePosition" => NumericsVector4.Zero,
                "unity_SpecCube0_BoxMin" or "unity_SpecCube1_BoxMin" => new NumericsVector4(-1, -1, -1, 1),
                "unity_SpecCube0_BoxMax" or "unity_SpecCube1_BoxMax" => new NumericsVector4(1, 1, 1, 1),
                _ => throw MissingValue(name)
            };
        }

        NumericsVector4 WaveScale()
        {
            float scale = scene.Values["_WaveScale"].X;
            return new NumericsVector4(scale, scale, scale * 0.4f, scale * 0.45f);
        }

        NumericsVector4 WaveOffset()
        {
            var movement = scene.Values["WaveSpeed"] * WaveScale() * (time / 20);
            return new NumericsVector4(movement.X % 1, movement.Y % 1, movement.Z % 1, movement.W % 1);
        }
    }

    private static bool TryTerrainImposterLight(string buffer, string name, IReadOnlyList<ShaderPreviewLight> lights, out NumericsVector4 value)
    {
        value = default;
        if (buffer != "UnityTerrainImposter")
        {
            return false;
        }
        const string directions = "_TerrainTreeLightDirections";
        const string colors = "_TerrainTreeLightColors";
        bool direction = name.StartsWith(directions, StringComparison.Ordinal);
        if (!(direction || name.StartsWith(colors, StringComparison.Ordinal)) ||
            !int.TryParse(name.AsSpan((direction ? directions : colors).Length), out int index) || index is < 0 or > 3)
        {
            return false;
        }
        if (index < lights.Count)
        {
            value = direction ? new NumericsVector4(lights[index].Direction, 0) : new NumericsVector4(lights[index].Color, 1);
        }
        return true;
    }

    private static bool TryVertexLight(string name, int count, int element, IReadOnlyList<ShaderPreviewLight> lights,
        NumericsMatrix4x4 view, out NumericsVector4 value)
    {
        string[] uniforms = ["unity_LightColor", "unity_LightPosition", "unity_LightAtten", "unity_SpotDirection"];
        foreach (string uniform in uniforms)
        {
            if (!name.StartsWith(uniform, StringComparison.Ordinal))
            {
                continue;
            }
            var suffix = name.AsSpan(uniform.Length);
            if (!(suffix.IsEmpty && count is > 1 and <= 8) &&
                !(count == 1 && int.TryParse(suffix, out element) && element is >= 0 and < 8))
            {
                break;
            }
            if (element >= lights.Count)
            {
                value = NumericsVector4.Zero;
                return true;
            }
            ShaderPreviewLight light = lights[element];
            var viewDirection = NumericsVector3.TransformNormal(light.Direction, view);
            value = uniform switch
            {
                "unity_LightColor" => new NumericsVector4(light.Color, 1),
                "unity_LightPosition" => new NumericsVector4(viewDirection, 0),
                "unity_LightAtten" => new NumericsVector4(-1, 1, 0, 0),
                _ => NumericsVector4.Zero
            };
            return true;
        }
        value = default;
        return false;
    }

    private static NumericsVector3 ToGamma(NumericsVector3 value) =>
        new(MathF.Pow(value.X, 1 / 2.2f), MathF.Pow(value.Y, 1 / 2.2f), MathF.Pow(value.Z, 1 / 2.2f));

    private static NumericsMatrix4x4 Inverse(NumericsMatrix4x4 matrix) =>
        NumericsMatrix4x4.Invert(matrix, out var inverse) ? inverse
            : throw new InvalidOperationException("The shader-preview camera matrix is singular.");

    private static NotSupportedException MissingValue(string name) =>
        new($"The shader requires '{name}', which is unavailable in this preview scene.");
}
