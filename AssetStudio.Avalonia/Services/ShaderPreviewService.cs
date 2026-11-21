using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using NumericsVector4 = System.Numerics.Vector4;
using NumericsVector3 = System.Numerics.Vector3;
using NumericsMatrix4x4 = System.Numerics.Matrix4x4;

namespace AssetStudio.Avalonia.Services;

public sealed record ShaderPreviewFrame(int Width, int Height, byte[] RgbaPixels);

internal sealed record ShaderPreviewScene(
    LegacyShaderPreviewPass Pass,
    IReadOnlyDictionary<string, NumericsVector4> Values,
    IReadOnlyDictionary<string, ShaderPreviewTexture> Textures);

internal sealed record ShaderPreviewTexture(
    int Width, int Height, int MipCount, int Format, int BytesPerBlock, bool BlockCompressed, byte[] Pixels,
    int Filter, int Wrap, int Anisotropy, float MipBias);

internal readonly record struct ShaderPreviewCamera(float Yaw, float Pitch, float Distance)
{
    public static ShaderPreviewCamera Default => new(0, 0, 4);
}

internal static class ShaderPreviewService
{
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
        Func<Material, PPtr<Texture>, Texture2D?> resolveTexture, CancellationToken cancellationToken)
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
        var pass = LegacyShaderPreview.Read(shader, material?.m_ValidKeywords ?? Array.Empty<string>());
        var values = new Dictionary<string, NumericsVector4>(pass.Values, StringComparer.Ordinal);
        var textureSlots = new Dictionary<string, UnityTexEnv>(StringComparer.Ordinal);
        if (material != null)
        {
            foreach (var (name, value) in material.m_SavedProperties.m_Floats)
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
        foreach (string name in pass.TextureDefaults.Keys.Concat(textureSlots.Keys).Distinct(StringComparer.Ordinal))
        {
            values[name + "_ST"] = textureSlots.TryGetValue(name, out var slot)
                ? new NumericsVector4(slot.m_Scale.X, slot.m_Scale.Y, slot.m_Offset.X, slot.m_Offset.Y)
                : new NumericsVector4(1, 1, 0, 0);
        }

        var textures = new Dictionary<string, ShaderPreviewTexture>(StringComparer.Ordinal);
        var boundNames = pass.Vertex.Textures.Concat(pass.Fragment.Textures).Select(binding => binding.Name).Distinct(StringComparer.Ordinal);
        foreach (string name in boundNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (material != null && textureSlots.TryGetValue(name, out var slot) && !slot.m_Texture.IsNull)
            {
                Texture2D texture = resolveTexture(material, slot.m_Texture)
                    ?? throw new InvalidDataException($"Texture '{name}' could not be resolved from material '{material.m_Name}'.");
                textures.Add(name, ReadTexture(texture));
            }
            else if (pass.TextureDefaults.TryGetValue(name, out string? declared))
            {
                textures.Add(name, CreateDeclaredTexture(name, declared));
            }
            else
            {
                throw new NotSupportedException($"No material texture or declared default is available for '{name}'.");
            }
        }
        return new ShaderPreviewScene(pass, values, textures);
    }

    private static ShaderPreviewTexture CreateDeclaredTexture(string name, string declared)
    {
        byte value = declared switch
        {
            "white" => 255,
            "gray" or "grey" => 128,
            "black" => 0,
            _ => throw new NotSupportedException($"Texture '{name}' has unsupported ShaderLab default '{declared}'.")
        };
        return new ShaderPreviewTexture(1, 1, 1, 28 /* R8G8B8A8_UNORM */, 4, false, [value, value, value, 255], 0, 0, 1, 0);
    }

    private static ShaderPreviewTexture ReadTexture(Texture2D texture)
    {
        GLTextureSettings settings = texture.m_TextureSettings;
        int width = texture.m_Width;
        int height = texture.m_Height;
        int mips = texture.m_MipCount;
        if (width is <= 0 or > 16384 || height is <= 0 or > 16384 ||
            mips < 1 || mips > 1 + BitOperations.Log2((uint)Math.Max(width, height)) ||
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
            TextureFormat.DXT1 => (71 /* BC1_UNORM */, 8, true),
            TextureFormat.DXT5 => (77 /* BC3_UNORM */, 16, true),
            TextureFormat.BGRA32 => (87 /* B8G8R8A8_UNORM */, 4, false),
            TextureFormat.RGBA32 or TextureFormat.ARGB32 or TextureFormat.RGB24 => (28 /* R8G8B8A8_UNORM */, 4, false),
            _ => throw new NotSupportedException($"Unsupported preview texture format {texture.m_TextureFormat} on '{texture.m_Name}'.")
        };

        byte[] data = texture.image_data.GetData();
        if (texture.m_TextureFormat == TextureFormat.RGB24)
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
            settings.m_FilterMode, settings.m_WrapMode, settings.m_Aniso, settings.m_MipBias);
    }

    internal static byte[] BindConstants(ShaderPreviewScene scene, ShaderPreviewBuffer buffer, NumericsMatrix4x4 view,
        NumericsMatrix4x4 projection, NumericsVector3 eye, int size)
    {
        var data = new byte[(buffer.Size + 15) & ~15];
        foreach (var parameter in buffer.Parameters)
        {
            if (parameter.Type != 0 || parameter.ArraySize != 1 || parameter.Offset < 0)
            {
                throw new NotSupportedException($"Unsupported shader parameter format for '{parameter.Name}'.");
            }
            if (parameter.IsMatrix)
            {
                if (parameter.Rows != 4 || parameter.Columns != 4 || parameter.Offset > data.Length - 64)
                {
                    throw new NotSupportedException($"Unsupported shader matrix layout for '{parameter.Name}'.");
                }
                NumericsMatrix4x4 matrix = parameter.Name switch
                {
                    "glstate_matrix_mvp" or "unity_MatrixMVP" or "unity_MatrixVP" => view * projection,
                    "glstate_matrix_modelview0" or "unity_MatrixV" or "unity_MatrixMV" => view,
                    "glstate_matrix_projection" or "unity_MatrixP" => projection,
                    "_Object2World" or "_World2Object" or "unity_ObjectToWorld" or "unity_WorldToObject" => NumericsMatrix4x4.Identity,
                    _ => throw MissingValue(parameter.Name)
                };
                MemoryMarshal.Write(data.AsSpan(parameter.Offset), in matrix);
            }
            else
            {
                int length = parameter.Columns * sizeof(float);
                if (parameter.Rows != 1 || parameter.Columns is < 1 or > 4 || parameter.Offset > data.Length - length)
                {
                    throw new NotSupportedException($"Unsupported shader vector layout for '{parameter.Name}'.");
                }
                NumericsVector4 value = parameter.Name switch
                {
                    "_WorldSpaceCameraPos" => new NumericsVector4(eye, 1),
                    "_ScreenParams" => new NumericsVector4(size, size, 1f + 1f / size, 1f + 1f / size),
                    "_ProjectionParams" => new NumericsVector4(1, 0.1f, 100, 0.01f),
                    "_ZBufferParams" => new NumericsVector4(-999, 1000, -9.99f, 10),
                    "_Time" or "_SinTime" => NumericsVector4.Zero,
                    "_CosTime" => NumericsVector4.One,
                    _ => scene.Values.TryGetValue(parameter.Name, out var property) ? property : throw MissingValue(parameter.Name)
                };
                MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in value, 1))[..length].CopyTo(data.AsSpan(parameter.Offset));
            }
        }
        return data;
    }

    private static NotSupportedException MissingValue(string name) =>
        new($"The shader requires '{name}', which is unavailable in this preview scene.");
}
