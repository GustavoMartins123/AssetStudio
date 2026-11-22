using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using NumericsMatrix = System.Numerics.Matrix4x4;
using NumericsVector2 = System.Numerics.Vector2;
using NumericsVector4 = System.Numerics.Vector4;

namespace AssetStudio.Avalonia.Services;

internal readonly record struct ShaderPreviewFog(int Mode, float Density, float Start, float End)
{
    internal float Transmittance(float distance)
    {
        float attenuation = Density * distance;
        return Mode switch
        {
            1 => Math.Clamp((End - distance) / (End - Start), 0, 1),
            2 => MathF.Exp(-attenuation),
            3 => MathF.Exp(-attenuation * attenuation),
            _ => throw new NotSupportedException($"The light preview does not support fog mode {Mode}.")
        };
    }
}

internal readonly record struct ShaderPreviewBillboard(float Position, float Size, NumericsVector4 Color,
    bool Rotate = false, bool Zoom = false, bool Fade = false, ShaderPreviewFog? Fog = null)
{
    internal bool Project(NumericsMatrix viewProjection, float time, out NumericsMatrix transform, out NumericsVector4 color)
    {
        var light = new NumericsVector4(-0.36f + 0.64f * MathF.Sin(time * MathF.Tau / 4), 0.24f, 0.25f, 1);
        var clip = NumericsVector4.Transform(light, viewProjection);
        transform = default;
        color = default;
        if (clip.W <= ShaderPreviewCamera.Near)
        {
            return false;
        }

        var screen = new NumericsVector2(clip.X, clip.Y) / clip.W;
        var center = screen * (1 - Position);
        float visibility = Math.Clamp(1 - screen.Length() * 0.65f, 0, 1);
        float size = Size / ShaderPreviewService.RenderSize * (Zoom ? visibility : 1);
        if (size <= 0 || MathF.Abs(center.X) > 1 + size || MathF.Abs(center.Y) > 1 + size)
        {
            return false;
        }
        float rotation = Rotate ? MathF.Atan2(screen.Y, screen.X) : 0;
        transform = NumericsMatrix.CreateScale(size, size, 1) * NumericsMatrix.CreateRotationZ(rotation) *
            NumericsMatrix.CreateTranslation(center.X, center.Y, 0.5f);
        color = Color * (Fade ? visibility : 1) * (Fog?.Transmittance(clip.W) ?? 1);
        return true;
    }
}

internal static class LegacyLightEffectsPreview
{
    private const string FlareShader = "Hidden/Internal-Flare";
    private const string HaloShader = "Hidden/Internal-Halo";
    private const string FlareTexture = "_FlareTexture";
    private const string HaloTexture = "_HaloFalloff";

    internal static bool IsLightEffect(string name) => name is FlareShader or HaloShader;

    internal static (string TextureName, ShaderPreviewTexture Texture, IReadOnlyList<ShaderPreviewDraw> Draws) Prepare(Shader shader,
        string name, ShaderPreviewDraw draw, Func<AssetHandle, AssetStudio.Object?> resolveAsset,
        Func<SerializedFile, PPtr<AssetStudio.Object>, AssetStudio.Object> resolveObject,
        Func<SerializedFile, PPtr<Texture>, Texture> resolveTexture, CancellationToken token)
    {
        var index = shader.assetsFile.assetsManager.ProjectIndex;
        if (name == FlareShader)
        {
            var (source, flare) = ReadIndexed(ClassIDType.LensFlare)
                .Where(asset => asset.assetsFile.m_Objects.Any(info => info.classID == (int)ClassIDType.RenderSettings))
                .Select(asset => Read(asset, reader => new LensFlare(reader)))
                .Where(component => component.m_Enabled != 0 && !component.Flare.IsNull && component.Brightness > 0)
                .Select(component => (component, flare: Read(resolveObject(component.assetsFile, component.Flare), reader => new Flare(reader))))
                .OrderByDescending(item => item.flare.Elements.Select(element => element.Position).Distinct().Count())
                .ThenByDescending(item => item.flare.Elements.Length)
                .FirstOrDefault();
            if (source == null)
            {
                throw new NotSupportedException("The project has no enabled LensFlare with a flare asset and scene settings.");
            }
            var settingsInfo = source.assetsFile.m_Objects.Single(info => info.classID == (int)ClassIDType.RenderSettings);
            var settings = Read(source.assetsFile, settingsInfo, ReadSettings);
            var atlas = ShaderPreviewService.ReadTexture(resolveTexture(flare.assetsFile, flare.Texture), ShaderPreviewTextureDimension.Texture2D);
            float texelX = 1f / atlas.Width, texelY = 1f / atlas.Height;

            var draws = new List<ShaderPreviewDraw>();
            foreach (var element in flare.Elements)
            {
                token.ThrowIfCancellationRequested();
                if (element.Size == 0)
                {
                    continue;
                }
                var region = Cell(flare.Layout, element.Image);
                var uv = region + new NumericsVector4(-texelX, -texelY, texelX * 0.5f, texelY * 0.5f);
                var tint = element.Color * (element.UseLightColor ? source.Color : NumericsVector4.One);
                tint *= source.Brightness * settings.FlareStrength;
                draws.Add(BillboardDraw(draw, FlareTexture, uv,
                    new ShaderPreviewBillboard(element.Position, element.Size, tint, element.Rotate, element.Zoom, element.Fade,
                        flare.UseFog ? settings.Fog : null)));
            }
            if (draws.Count == 0)
            {
                throw new InvalidDataException("The LensFlare asset has no visible-sized elements.");
            }
            return (FlareTexture, atlas, draws);
        }

        var halo = ReadIndexed(ClassIDType.Halo).Select(asset => Read(asset, reader => new Halo(reader)))
            .FirstOrDefault(component => component.m_Enabled != 0 && component.Size > 0)
            ?? throw new NotSupportedException("The project has no enabled Halo component.");
        var (settingsSource, haloSettings) = ReadIndexed(ClassIDType.RenderSettings)
            .Select(asset => (asset.assetsFile, Settings: Read(asset, ReadSettings)))
            .FirstOrDefault(item => item.Settings.HaloStrength > 0);
        if (haloSettings == null)
        {
            throw new NotSupportedException("The project has no scene RenderSettings with enabled halos.");
        }
        // An empty RenderSettings slot selects the UnityHalo texture generated by the Built-in renderer.
        var falloff = haloSettings.HaloTexture.IsNull ? CreateUnityHalo() :
            ShaderPreviewService.ReadTexture(resolveTexture(settingsSource, haloSettings.HaloTexture), ShaderPreviewTextureDimension.Texture2D);
        var haloDraw = BillboardDraw(draw, HaloTexture, new NumericsVector4(1, 1, 0, 0),
            new ShaderPreviewBillboard(0, halo.Size, halo.Color * haloSettings.HaloStrength));
        return (HaloTexture, falloff, [haloDraw]);

        IEnumerable<AssetStudio.Object> ReadIndexed(ClassIDType type)
        {
            var arena = index.Arena;
            int count = arena.Count;
            var matches = new List<AssetHandle>();
            for (int i = 0; i < count; i++)
            {
                token.ThrowIfCancellationRequested();
                if (arena.GetType(i) == type)
                {
                    matches.Add(arena.MaterializeHandle(i));
                }
            }
            foreach (var handle in matches.OrderBy(handle => handle.SerializedFileName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(handle => handle.PathID))
            {
                token.ThrowIfCancellationRequested();
                var asset = resolveAsset(handle);
                if (asset == null || asset.type != type)
                {
                    throw new InvalidDataException($"The indexed {type} in '{handle.SerializedFileName}' could not be read.");
                }
                if (asset.version is [5, 3, ..] or [2017, 4, ..] or [2018, 2 or 4, ..] or [2019, 3, ..])
                {
                    yield return asset;
                }
            }
        }
    }

    private static ShaderPreviewTexture CreateUnityHalo()
    {
        const int size = 64;
        var alpha = new byte[size * size];
        for (int y = 0; y < size; y++)
        {
            float v = 2f * (y + 0.5f) / size - 1;
            for (int x = 0; x < size; x++)
            {
                float u = 2f * (x + 0.5f) / size - 1;
                alpha[y * size + x] = (byte)(255 * MathF.Max(0, 1 - (u * u + v * v)) + 0.5f);
            }
        }
        return new ShaderPreviewTexture(size, size, 1, 65 /* A8_UNORM */, 1, false, alpha, 1, 1, 1, 0);
    }

    private static ShaderPreviewDraw BillboardDraw(ShaderPreviewDraw draw, string texture, NumericsVector4 uv,
        ShaderPreviewBillboard billboard) => draw with
    {
        Values = new Dictionary<string, NumericsVector4> { [texture + "_ST"] = uv },
        Billboard = billboard
    };

    private static NumericsVector4 Cell(int layout, uint image)
    {
        // Split layouts reserve the lower half for one square and subdivide the upper half.
        return layout switch
        {
            0 or 1 when image == 0 => new(1, 0.5f, 0, 0.5f),
            0 when image is >= 1 and <= 4 => new(0.5f, 0.25f, (image - 1) % 2 * 0.5f, (1 - (image - 1) / 2) * 0.25f),
            1 when image is 1 or 2 => new(0.5f, 0.25f, 0, (2 - image) * 0.25f),
            1 when image is >= 3 and <= 10 => new(0.25f, 0.125f, 0.5f + (image - 3) % 2 * 0.25f, (3 - (image - 3) / 2) * 0.125f),
            >= 2 and <= 5 when image < (layout - 1) * (layout - 1) => Grid((int)image, layout - 1),
            _ => throw new InvalidDataException($"Flare atlas layout {layout} has no image {image}.")
        };

        static NumericsVector4 Grid(int image, int cells)
        {
            float step = 1f / cells;
            return new NumericsVector4(step, step, image % cells * step, (cells - 1 - image / cells) * step);
        }
    }

    private static T Read<T>(AssetStudio.Object asset, Func<ObjectReader, T> read) =>
        Read(asset.assetsFile, asset.assetsFile.ObjectInfoDic[asset.m_PathID], read);

    private static T Read<T>(SerializedFile source, ObjectInfo info, Func<ObjectReader, T> read)
    {
        using var file = source.reader.Clone();
        return read(new ObjectReader(file, source, info));
    }

    private sealed record LightingSettings(PPtr<Texture> HaloTexture, float HaloStrength, float FlareStrength, ShaderPreviewFog? Fog);

    private static LightingSettings ReadSettings(ObjectReader reader)
    {
        _ = new AssetStudio.Object(reader);
        bool useFog = reader.ReadBoolean();
        reader.AlignStream();
        reader.ReadColor4();
        var fog = new ShaderPreviewFog(reader.ReadInt32(), ReadFinite(reader), ReadFinite(reader), ReadFinite(reader));
        for (int i = 0; i < 3; i++)
        {
            reader.ReadColor4();
        }
        reader.ReadSingle(); // Ambient intensity.
        reader.ReadInt32(); // Ambient mode.
        reader.AlignStream();
        if (reader.version[0] >= 2017)
        {
            reader.ReadColor4(); // Subtractive shadow color.
        }
        _ = new PPtr<Material>(reader);
        float halo = ReadFinite(reader);
        float flare = ReadFinite(reader);
        reader.ReadSingle(); // Flare fade speed.
        var texture = new PPtr<Texture>(reader);
        if (reader.Position > reader.byteStart + reader.byteSize || (useFog && fog.Mode == 1 && fog.End <= fog.Start))
        {
            throw new InvalidDataException("The scene lighting settings contain an invalid fog or texture binding.");
        }
        return new LightingSettings(texture, halo, flare, useFog ? fog : null);
    }

    private static NumericsVector4 ReadColor(ObjectReader reader) =>
        new(ReadFinite(reader), ReadFinite(reader), ReadFinite(reader), ReadFinite(reader));

    private static float ReadFinite(ObjectReader reader)
    {
        float value = reader.ReadSingle();
        return float.IsFinite(value) ? value : throw new InvalidDataException("A light effect contains a non-finite setting.");
    }

    private sealed class LensFlare : Behaviour
    {
        internal LensFlare(ObjectReader reader) : base(reader)
        {
            Flare = new PPtr<AssetStudio.Object>(reader);
            Color = ReadColor(reader);
            Brightness = ReadFinite(reader);
        }

        internal PPtr<AssetStudio.Object> Flare { get; }
        internal NumericsVector4 Color { get; }
        internal float Brightness { get; }
    }

    private sealed class Halo : Behaviour
    {
        internal Halo(ObjectReader reader) : base(reader)
        {
            Color = new NumericsVector4(reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), reader.ReadByte()) / 255;
            Size = ReadFinite(reader);
        }

        internal NumericsVector4 Color { get; }
        internal float Size { get; }
    }

    private sealed class Flare : NamedObject
    {
        internal Flare(ObjectReader reader) : base(reader)
        {
            if (reader.type != ClassIDType.Flare)
            {
                throw new InvalidDataException("The LensFlare reference does not point to a Flare asset.");
            }
            Texture = new PPtr<Texture>(reader);
            Layout = reader.ReadInt32();
            int count = reader.ReadInt32();
            if (count <= 0 || count > 256 || reader.Position + 32L * count + 1 != reader.byteStart + reader.byteSize)
            {
                throw new InvalidDataException("The Flare asset has an invalid element array.");
            }
            Elements = new FlareElement[count];
            for (int i = 0; i < count; i++)
            {
                Elements[i] = new FlareElement(reader.ReadUInt32(), ReadFinite(reader), ReadFinite(reader), ReadColor(reader),
                    reader.ReadBoolean(), reader.ReadBoolean(), reader.ReadBoolean(), reader.ReadBoolean());
            }
            UseFog = reader.ReadBoolean();
        }

        internal PPtr<Texture> Texture { get; }
        internal int Layout { get; }
        internal FlareElement[] Elements { get; }
        internal bool UseFog { get; }
    }

    private readonly record struct FlareElement(uint Image, float Position, float Size, NumericsVector4 Color,
        bool UseLightColor, bool Rotate, bool Zoom, bool Fade);
}
