using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using NumericsVector4 = System.Numerics.Vector4;

namespace AssetStudio.Avalonia.Services;

internal static class TreeBillboardPreview
{
    internal const string ShaderName = "Hidden/Nature/Tree Creator Leaves Rendertex";
    private const string LeafShader = "Nature/Tree Creator Leaves";

    internal static void Prepare(Shader billboard, IDictionary<string, NumericsVector4> values,
        IDictionary<string, ShaderPreviewTexture> textures, Func<SerializedFile, PPtr<Shader>, Shader> resolveShader,
        Func<SerializedFile, PPtr<Texture>, Texture> resolveTexture, CancellationToken token)
    {
        var source = billboard.assetsFile;
        using var file = source.reader.Clone();
        var leaf = source.m_Objects.Where(info => info.classID == (int)ClassIDType.Shader)
            .Select(info =>
            {
                token.ThrowIfCancellationRequested();
                return new Shader(new ObjectReader(file, source, info));
            })
            .FirstOrDefault(shader => shader.m_Script is { Length: > 0 } && LegacyShaderPreview.ReadName(shader) == LeafShader)
            ?? throw new NotSupportedException("The tree billboard preview needs the original leaf shader in its serialized file.");
        var leafLab = LegacyShaderPreview.Read(leaf);
        var optimized = Dependency(leaf, leafLab, "OptimizedShader");
        var owner = Dependency(optimized, LegacyShaderPreview.Read(optimized), "BillboardShader");
        if (AssetHandle.BuildUniqueID(owner.assetsFile, owner.m_PathID) != AssetHandle.BuildUniqueID(source, billboard.m_PathID))
        {
            throw new InvalidDataException("The leaf shader dependency does not reference the selected billboard shader.");
        }
        var material = source.m_Objects.Where(info => info.classID == (int)ClassIDType.Material)
            .Select(info =>
            {
                token.ThrowIfCancellationRequested();
                return new Material(new ObjectReader(file, source, info));
            })
            .FirstOrDefault(candidate => candidate.m_Shader.m_FileID == 0 && candidate.m_Shader.m_PathID == leaf.m_PathID &&
                candidate.m_SavedProperties.m_Colors.Any(property => property.Key == "_SpecColor"))
            ?? throw new NotSupportedException("No material for the linked leaf shader supplies '_SpecColor'.");

        var colors = material.m_SavedProperties.m_Colors.ToDictionary(property => property.Key,
            property => new NumericsVector4(property.Value.R, property.Value.G, property.Value.B, property.Value.A), StringComparer.Ordinal);
        var floats = material.m_SavedProperties.m_Floats.ToDictionary(property => property.Key, property => property.Value, StringComparer.Ordinal);
        values["_SpecColor"] = colors["_SpecColor"];
        values["_Cutoff"] = new NumericsVector4(GetFloat("_Cutoff"), 0, 0, 0);
        var tint = colors.TryGetValue("_Color", out var materialColor) ? materialColor : leafLab.Values["_Color"];
        byte shininess = Encode(255 * Math.Clamp(GetFloat("_Shininess"), 0.03f, 1));

        var diffuse = ReadMap("_MainTex") ?? throw new NotSupportedException($"Leaf material '{material.m_Name}' needs its diffuse texture.");
        var normals = ReadMap("_BumpMap", unpackNormal: true);
        var shadow = ReadMap("_ShadowOffset");
        var translucency = ReadMap("_TranslucencyMap");
        var gloss = ReadMap("_GlossMap");
        var diffuseAtlas = new byte[diffuse.Bgra.Length];
        var normalSpecular = new byte[diffuse.Bgra.Length];
        var translucencyGloss = new byte[diffuse.Bgra.Length];
        for (int y = 0; y < diffuse.Height; y++)
        {
            token.ThrowIfCancellationRequested();
            float v = (y + 0.5f) / diffuse.Height;
            for (int x = 0; x < diffuse.Width; x++)
            {
                float u = (x + 0.5f) / diffuse.Width;
                int offset = (y * diffuse.Width + x) * 4;
                diffuseAtlas[offset] = Encode(diffuse.Bgra[offset + 2] * tint.X);
                diffuseAtlas[offset + 1] = Encode(diffuse.Bgra[offset + 1] * tint.Y);
                diffuseAtlas[offset + 2] = Encode(diffuse.Bgra[offset] * tint.Z);
                diffuseAtlas[offset + 3] = diffuse.Bgra[offset + 3];

                // Unity's tree texture combiner stores shininess in R and normal XY in AG.
                normalSpecular[offset] = shininess;
                normalSpecular[offset + 1] = normals?.Sample(u, v, 1) ?? 128;
                normalSpecular[offset + 2] = shadow?.Sample(u, v, 3) ?? 0;
                normalSpecular[offset + 3] = normals?.Sample(u, v, 2) ?? 128;
                translucencyGloss[offset + 2] = translucency?.Sample(u, v, 3) ?? 255;
                translucencyGloss[offset + 3] = gloss?.Sample(u, v, 3) ?? 0;
            }
        }
        textures.Add("_MainTex", Atlas(diffuseAtlas));
        textures.Add("_BumpSpecMap", Atlas(normalSpecular));
        textures.Add("_TranslucencyMap", Atlas(translucencyGloss));

        float GetFloat(string name) => floats.TryGetValue(name, out float value) ? value : leafLab.Values[name].X;

        ShaderPreviewTexture Atlas(byte[] pixels) =>
            new(diffuse.Width, diffuse.Height, 1, 28 /* R8G8B8A8_UNORM */, 4, false, pixels, 1, 1, 1, 0);

        Map? ReadMap(string name, bool unpackNormal = false)
        {
            var slot = material.m_SavedProperties.m_TexEnvs.FirstOrDefault(property => property.Key == name).Value;
            if (slot == null || slot.m_Texture.IsNull)
            {
                return null;
            }
            if (resolveTexture(material.assetsFile, slot.m_Texture) is not Texture2D texture ||
                texture.m_Width <= 0 || texture.m_Height <= 0 || (long)texture.m_Width * texture.m_Height > 32 * 1024 * 1024)
            {
                throw new InvalidDataException($"The leaf texture '{name}' does not have valid 2D dimensions.");
            }
            token.ThrowIfCancellationRequested();
            var bytes = new byte[texture.m_Width * texture.m_Height * 4];
            if (!new Texture2DConverter(texture).DecodeTexture2D(bytes))
            {
                throw new NotSupportedException($"The leaf texture '{texture.m_Name}' could not be decoded.");
            }
            if (unpackNormal)
            {
                Texture2DConverter.UnpackNormalMapBgra32(bytes, texture.m_TextureFormat, forceUnpack: true);
            }
            return new Map(texture.m_Width, texture.m_Height, bytes);
        }

        Shader Dependency(Shader shader, LegacyShaderPreview header, string role)
        {
            if (!header.Dependencies.TryGetValue(role, out var dependency))
            {
                throw new NotSupportedException($"The leaf shader '{header.Name}' has no '{role}' dependency.");
            }
            foreach (var reference in shader.m_Dependencies.Where(reference => !reference.IsNull)
                .OrderBy(reference => reference.m_FileID))
            {
                token.ThrowIfCancellationRequested();
                var linked = resolveShader(shader.assetsFile, reference);
                if (LegacyShaderPreview.ReadName(linked) == dependency)
                {
                    return linked;
                }
            }
            throw new InvalidDataException($"The '{role}' shader '{dependency}' could not be resolved.");
        }
    }

    private static byte Encode(float value) => (byte)Math.Clamp(MathF.Round(value), 0, 255);

    private sealed record Map(int Width, int Height, byte[] Bgra)
    {
        internal byte Sample(float u, float v, int channel)
        {
            float x = Math.Clamp(u * Width - 0.5f, 0, Width - 1);
            float y = Math.Clamp(v * Height - 0.5f, 0, Height - 1);
            int left = (int)x, top = (int)y;
            int right = Math.Min(left + 1, Width - 1), bottom = Math.Min(top + 1, Height - 1);
            float upper = float.Lerp(Bgra[(top * Width + left) * 4 + channel], Bgra[(top * Width + right) * 4 + channel], x - left);
            float lower = float.Lerp(Bgra[(bottom * Width + left) * 4 + channel], Bgra[(bottom * Width + right) * 4 + channel], x - left);
            return Encode(float.Lerp(upper, lower, y - top));
        }
    }
}
