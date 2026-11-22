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
internal sealed record ShaderPreviewRenderTarget(string Name, int Size, int Filter = 1);
internal enum ShaderPreviewPointPass { None, Collect, Draw }
internal sealed record ShaderPreviewDraw(LegacyShaderPreviewPass Pass, ShaderPreviewLight Light, int SkyboxSide = -1) : ShaderPreviewOperation
{
    internal ShaderPreviewRenderTarget? Output { get; init; }
    internal IReadOnlyDictionary<string, ShaderPreviewRenderTarget>? Inputs { get; init; }
    internal IReadOnlyDictionary<string, NumericsVector4>? Values { get; init; }
    internal IReadOnlyDictionary<string, NumericsVector4[]>? VectorArrays { get; init; }
    internal IReadOnlyDictionary<string, NumericsMatrix4x4>? Matrices { get; init; }
    internal IReadOnlyDictionary<string, string>? TextureSources { get; init; }
    internal ShaderPreviewGeometry? Geometry { get; init; }
    internal NumericsVector4? VertexColor { get; init; }
    internal ShaderPreviewBillboard? Billboard { get; init; }
    internal ShaderPreviewPointPass PointPass { get; init; }
    internal string TextureSource(string name) => TextureSources?.GetValueOrDefault(name) ?? name;
}
internal sealed record ShaderPreviewGrab(string TextureName, bool Once) : ShaderPreviewOperation;
internal readonly record struct ShaderPreviewLight(NumericsVector3 Direction, NumericsVector3 Color);
internal enum ShaderPreviewGeometry { Sphere, Plane, Volume, Skybox, Particles, ScreenQuad, Text }

internal sealed record ShaderPreviewScene(
    string ShaderName, ShaderPreviewGeometry Geometry, bool IsAnimated, IReadOnlyList<ShaderPreviewOperation> Operations,
    NumericsVector3 AmbientLinear, IReadOnlyList<ShaderPreviewLight> Lights,
    IReadOnlyDictionary<string, NumericsVector4> Values,
    IReadOnlyDictionary<string, ShaderPreviewTexture> Textures, IReadOnlyList<string> UnusedMaterialKeywords,
    BuiltInLightingPass LightingPass, ImageEffectPass? ImageEffect, IReadOnlySet<string> CameraTextures)
{
    internal NoisePreviewSettings? NoiseSettings { get; init; }
    internal bool SerializedShader { get; init; }
    internal uint DepthBlitSamples { get; init; }
}

internal sealed record ShaderPreviewTexture(
    int Width, int Height, int MipCount, int Format, int BytesPerBlock, bool BlockCompressed, byte[] Pixels,
    int Filter, int Wrap, int Anisotropy, float MipBias, int Faces = 1, bool Comparison = false, int VolumeDepth = 0)
{
    internal ShaderPreviewTextureDimension Dimension => VolumeDepth > 0 ? ShaderPreviewTextureDimension.Texture3D :
        Faces == 6 ? ShaderPreviewTextureDimension.Cube : ShaderPreviewTextureDimension.Texture2D;
}

internal readonly record struct ShaderPreviewCamera(float Yaw, float Pitch, float Distance, System.Numerics.Vector2 Pan = default)
{
    public static ShaderPreviewCamera Default => new(0, 0, 4);
    public const float Near = 0.1f, Far = 100;
}

internal static class ShaderPreviewService
{
    internal const int RenderSize = 384;
    internal const int FramesPerSecond = 30;
    private static readonly ShaderPreviewLight[] PreviewLights =
    [
        new(NumericsVector3.Normalize(new NumericsVector3(-0.45f, 0.7f, 0.65f)), new NumericsVector3(0.82f))
    ];
    private static readonly NumericsVector3 PreviewAmbientLinear = new(0.045f, 0.05f, 0.06f);
    private static readonly string[] PreviewKeywords =
    [
        "DIRECTIONAL", "SHADOWS_OFF", "LIGHTMAP_OFF", "DIRLIGHTMAP_OFF", "DYNAMICLIGHTMAP_OFF", "SOFTPARTICLES_OFF"
    ];

    internal static bool UsesShaderPreview(int[] version) => OperatingSystem.IsWindows() && ShaderPreviewReader.SupportsVersion(version);

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
        Action<SerializedFile, CancellationToken> loadExternalSources, Func<AssetHandle, AssetStudio.Object?> resolveAsset,
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
        var reader = ShaderPreviewReader.Read(shader);
        bool serialized = reader is not LegacyShaderPreview;
        var cameraPreview = serialized ? SerializedCameraShaderPreview.Find(reader.Name) : null;
        uint depthBlitSamples = cameraPreview?.DepthBlitSamples ?? 0;
        bool textShader = serialized && TextMeshProShaderPreview.IsTextShader(reader.Name);
        bool guiShader = serialized && SerializedGuiShaderPreview.IsGuiDraw(reader.Name);
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
        if (serialized)
        {
            UnityChanToonPreview.SetGlobals(reader, values);
        }
        foreach (string name in reader.TextureDefaults.Keys.Concat(textureSlots.Keys).Distinct(StringComparer.Ordinal))
        {
            string uniform = serialized ? name + "_ST" : Unity53ShaderPasses.TextureTransform(reader.Name, name);
            values[uniform] = textureSlots.TryGetValue(name, out var slot)
                ? new NumericsVector4(slot.m_Scale.X, slot.m_Scale.Y, slot.m_Offset.X, slot.m_Offset.Y)
                : new NumericsVector4(1, 1, 0, 0);
        }

        string? shadowFilter = serialized ? SerializedCameraShaderPreview.ShadowFilter(reader.Name) : Unity53ShaderPasses.ShadowFilter(reader.Name);
        var lightingPass = shadowFilter != null ? BuiltInLightingPass.CollectShadows :
            reader.Name == "Hidden/Internal-DeferredShading" ? BuiltInLightingPass.DeferredLighting : BuiltInLightingPass.None;
        var subShader = reader.SubShaders.FirstOrDefault(sub => IsBuiltIn(sub) &&
            (shadowFilter == null || sub.Tags.GetValueOrDefault("ShadowmapFilter") == shadowFilter) &&
            sub.Passes.Any(pass => pass.Type == PassType.Use || pass.HasDirect3D11))
            ?? throw new NotSupportedException($"'{reader.Name}' has no D3D11 SubShader for the Built-in preview scene.");
        var geometry = lightingPass != BuiltInLightingPass.None ? ShaderPreviewGeometry.ScreenQuad :
            subShader.Tags.GetValueOrDefault("PreviewType", "sphere").ToLowerInvariant() switch
        {
            "sphere" or "plane" when reader.Name.StartsWith("Particles/", StringComparison.OrdinalIgnoreCase) ||
                reader.Name.Contains("/Particles/", StringComparison.OrdinalIgnoreCase) => ShaderPreviewGeometry.Particles,
            "sphere" when reader.Name.Equals("Hidden/FogVolume", StringComparison.Ordinal) => ShaderPreviewGeometry.Volume,
            "sphere" when subShader.Tags.ContainsKey("WaterMode") || reader.Name.StartsWith("UI/", StringComparison.Ordinal) => ShaderPreviewGeometry.Plane,
            "sphere" => ShaderPreviewGeometry.Sphere,
            "plane" => ShaderPreviewGeometry.Plane,
            "skybox" => ShaderPreviewGeometry.Skybox,
            var type => throw new NotSupportedException($"Unsupported ShaderLab PreviewType '{type}'.")
        };
        // Screen-space preview geometry is independent from the game's depth ordering.
        values.TryAdd("unity_GUIZTestMode", new NumericsVector4(8, 0, 0, 0));
        if (textShader)
        {
            geometry = ShaderPreviewGeometry.Text;
            TextMeshProShaderPreview.SetDrawProperties(values);
        }
        if (guiShader)
        {
            geometry = ShaderPreviewGeometry.Plane;
            SerializedGuiShaderPreview.SetProperties(reader.Name, values);
        }
        var passes = ExpandPasses(shader, reader, subShader, (owner, reference) => Resolve(owner.assetsFile, reference), cancellationToken)
            .Select(item => (item.Reader, item.Sub, Pass: item.Reader is LegacyShaderPreview legacy ?
                Unity53ShaderPasses.ReadLightMode(legacy, item.Pass) : item.Pass))
            .ToArray();
        bool forward = passes.Any(item => IsMode(item.Pass, "ForwardBase"));
        var materialKeywords = new HashSet<string>(material?.m_ValidKeywords ?? [], StringComparer.Ordinal);
        var enabled = new HashSet<string>(materialKeywords, StringComparer.Ordinal);
        if (material == null)
        {
            enabled.UnionWith(reader.DefaultKeywords);
        }
        enabled.UnionWith(PreviewKeywords);
        if (serialized)
        {
            enabled.Add("UNITY_COLORSPACE_GAMMA");
        }
        if (cameraPreview?.SceneKeyword is { } sceneKeyword)
        {
            enabled.Add(sceneKeyword);
        }
        if (lightingPass == BuiltInLightingPass.CollectShadows)
        {
            enabled.Remove("SHADOWS_OFF");
            enabled.UnionWith(["SHADOWS_NATIVE", "SHADOWS_SINGLE_CASCADE"]);
        }
        else if (lightingPass == BuiltInLightingPass.DeferredLighting)
        {
            enabled.Add("UNITY_HDR_ON");
        }
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
        for (int passIndex = 0; passIndex < passes.Length; passIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (cameraPreview != null && !cameraPreview.PassIndices.Contains(passIndex))
            {
                continue;
            }
            var (owner, ownerSub, definition) = passes[passIndex];
            if (definition.Type == PassType.Grab)
            {
                operations.Add(new ShaderPreviewGrab(definition.Name, definition.CaptureOnce));
                continue;
            }
            bool additive = IsMode(definition, "ForwardAdd");
            if (!IsColorPass(definition) || (forward && IsMode(definition, "Vertex")) || (additive && lights.Count == 1)
                || IsShadowOnly(owner, definition) || (lightingPass == BuiltInLightingPass.DeferredLighting &&
                    !owner.GetKeywords(definition).Contains("UNITY_HDR_ON", StringComparer.Ordinal)))
            {
                continue;
            }
            try
            {
                var passKeywords = serialized ? UnityChanToonPreview.PassKeywords(owner, definition, values, enabled) : enabled;
                var pass = owner.PreparePass(ownerSub, definition, passKeywords, values);
                if (additive)
                {
                    for (int i = 1; i < lights.Count; i++)
                    {
                        operations.Add(new ShaderPreviewDraw(pass, lights[i]));
                    }
                }
                else
                {
                    var draw = new ShaderPreviewDraw(pass, lights[0], geometry == ShaderPreviewGeometry.Skybox ? GetSkyboxSide(pass) : -1);
                    if (guiShader && SerializedGuiShaderPreview.IsRoundedRect(reader.Name))
                    {
                        draw = draw with { VectorArrays = SerializedGuiShaderPreview.RoundedRectArrays };
                    }
                    operations.Add(draw);
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

        var textures = new Dictionary<string, ShaderPreviewTexture>(StringComparer.Ordinal);
        if (textShader && material == null)
        {
            textures.Add(ImageEffectPreview.MainTexture, TextMeshProShaderPreview.CreateGlyphAtlas(values));
        }
        ImageEffectPass? imageEffect = null;
        if (cameraPreview != null)
        {
            operations = [.. cameraPreview.Prepare(operations, values, textures, RenderSize)];
            PostProcessingV2Preview.LoadResources(reader.Name, shader, values, textures, RenderSize,
                resolveAsset, Resolve<MonoScript>, Resolve<Texture>, cancellationToken);
            geometry = ShaderPreviewGeometry.ScreenQuad;
            imageEffect = cameraPreview.Effect;
        }
        else if (SplashShadowPreview.IsShadowEffect(reader.Name))
        {
            operations = [.. SplashShadowPreview.Prepare(shader, reader.Name, operations.Cast<ShaderPreviewDraw>().ToArray(), lights[0], RenderSize, cancellationToken)];
        }
        else if (LegacyLightEffectsPreview.IsLightEffect(reader.Name))
        {
            var effect = LegacyLightEffectsPreview.Prepare(shader, reader.Name, operations.Cast<ShaderPreviewDraw>().Single(),
                resolveAsset, Resolve<AssetStudio.Object>, Resolve<Texture>, cancellationToken);
            textures.Add(effect.TextureName, effect.Texture);
            operations = [.. effect.Draws];
            geometry = ShaderPreviewGeometry.ScreenQuad;
        }
        else if (material == null && reader.Name == TreeBillboardPreview.ShaderName)
        {
            TreeBillboardPreview.Prepare(shader, values, textures, Resolve<Shader>, Resolve<Texture>, cancellationToken);
        }
        else if (geometry is ShaderPreviewGeometry.Sphere or ShaderPreviewGeometry.Plane &&
            operations.All(operation => operation is ShaderPreviewDraw draw && draw.Pass.LightMode.Equals("Always", StringComparison.OrdinalIgnoreCase)))
        {
            // An image effect chooses a pass for its blit. Its passes are not successive draws on the material sphere.
            var draws = operations.Cast<ShaderPreviewDraw>();
            var cameraPasses = draws.Where(draw => draw.Pass.Fragment.Textures.Any(texture =>
                BuiltInLightingPreview.IsSurfaceTexture(texture.Name))).ToArray();
            if (cameraPasses.Length > 0)
            {
                // The legacy light prepass has separate encoded and linear accumulation passes.
                // The preview displays the linear light buffer.
                var draw = cameraPasses.Length == 1 ? cameraPasses[0] : cameraPasses.FirstOrDefault(candidate =>
                    candidate.Pass.Keywords.Contains("DIRECTIONAL") && candidate.Pass.State is
                        { Blend: true, Source: ShaderPreviewBlend.One, Destination: ShaderPreviewBlend.One });
                if (draw == null)
                {
                    throw new NotSupportedException($"'{reader.Name}' requires a camera driver to choose its fullscreen pass.");
                }
                geometry = ShaderPreviewGeometry.ScreenQuad;
                operations = [draw];
            }
            else
            {
                var (effectDraws, effectPass) = ImageEffectPreview.SelectDraws(draws, RenderSize);
                if (effectDraws != null && effectPass is { } selected)
                {
                    imageEffect = selected;
                    geometry = selected == ImageEffectPass.ObjectMotion ? ShaderPreviewGeometry.Sphere : ShaderPreviewGeometry.ScreenQuad;
                    operations = [.. effectDraws];
                    ImageEffectPreview.SetParameters(selected, values, RenderSize);
                }
            }
        }

        var cameraTextures = new HashSet<string>(StringComparer.Ordinal);
        if (geometry == ShaderPreviewGeometry.ScreenQuad && !ImageEffectPreview.HasAnimatedInput(imageEffect))
        {
            foreach (string name in operations.OfType<ShaderPreviewDraw>()
                .SelectMany(draw => draw.Pass.Stages.SelectMany(stage => stage.Textures).Select(binding => draw.TextureSource(binding.Name))))
            {
                if (BuiltInLightingPreview.IsSurfaceTexture(name) || name == BuiltInLightingPreview.CameraDepth ||
                    (imageEffect == ImageEffectPass.Bokeh && name == ImageEffectPreview.BokehBlurredColor) ||
                    (imageEffect == ImageEffectPass.MotionBlur && name == MotionPreview.MotionTexture) ||
                    (MotionPreview.UsesUnityMotionTexture(imageEffect) && name == MotionPreview.UnityMotionTexture) ||
                    (imageEffect == ImageEffectPass.ScreenSpaceReflection && name == ScreenSpaceReflectionPreview.CameraReflections) ||
                    (imageEffect == ImageEffectPass.SkinDeformation && MotionPreview.IsSkinTexture(name)) ||
                    (lightingPass == BuiltInLightingPass.CollectShadows && name == BuiltInLightingPreview.ShadowMap))
                {
                    cameraTextures.Add(name);
                }
            }
            bool needsCameraColor = cameraPreview?.UsesCameraColor ?? (imageEffect.HasValue && imageEffect != ImageEffectPass.SkinDeformation);
            if (needsCameraColor && (imageEffect is ImageEffectPass.Bokeh or ImageEffectPass.ExposureHistogram or ImageEffectPass.TemporalAntialiasing ||
                !textureSlots.TryGetValue(ImageEffectPreview.MainTexture, out var source) || source.m_Texture.IsNull))
            {
                cameraTextures.Add(ImageEffectPreview.MainTexture);
            }
        }
        NoisePreviewSettings? noiseSettings = null;
        if (imageEffect == ImageEffectPass.Noise)
        {
            var noise = NoisePreview.Read(shader, Resolve<Texture>, cancellationToken);
            noiseSettings = noise.Settings;
            textures.Add(NoisePreview.GrainTexture, ReadTexture(noise.Grain, ShaderPreviewTextureDimension.Texture2D));
            textures.Add(NoisePreview.ScratchTexture, ReadTexture(noise.Scratch, ShaderPreviewTextureDimension.Texture2D));
        }
        var captures = new HashSet<string>(StringComparer.Ordinal);
        var renderedTargets = new HashSet<ShaderPreviewRenderTarget>();
        foreach (var operation in operations)
        {
            if (operation is ShaderPreviewGrab grab)
            {
                captures.Add(grab.TextureName);
                continue;
            }

            var draw = (ShaderPreviewDraw)operation;
            var pass = draw.Pass;
            try
            {
                foreach (var binding in pass.Stages.SelectMany(stage => stage.Textures))
                {
                    if (depthBlitSamples != 0 && binding.Name == ImageEffectPreview.MainTexture)
                    {
                        continue;
                    }
                    if (draw.Inputs?.TryGetValue(binding.Name, out var target) == true)
                    {
                        if (binding.Dimension != ShaderPreviewTextureDimension.Texture2D || target == draw.Output || !renderedTargets.Contains(target))
                        {
                            throw new InvalidDataException($"Render target '{target.Name}' is incompatible or unavailable for '{binding.Name}'.");
                        }
                        continue;
                    }
                    PrepareTexture(draw.TextureSource(binding.Name), binding.Dimension);
                }
                foreach (string name in pass.Stages.SelectMany(stage => stage.Buffers).SelectMany(buffer => buffer.Parameters)
                    .Select(parameter => parameter.Name).Where(name => name.EndsWith("_TexelSize", StringComparison.Ordinal)))
                {
                    string slot = name[..^"_TexelSize".Length];
                    string textureName = draw.TextureSource(slot);
                    if (draw.Inputs?.ContainsKey(slot) != true && !textures.ContainsKey(textureName) && !captures.Contains(textureName) &&
                        (reader.TextureDefaults.ContainsKey(textureName) || textureSlots.ContainsKey(textureName)))
                    {
                        PrepareTexture(textureName, ShaderPreviewTextureDimension.Texture2D);
                    }
                }
                if (draw.Output is { } output)
                {
                    bool depthIndependent = pass.State.ZTest is ShaderPreviewDepth.Always or ShaderPreviewDepth.Off &&
                        pass.State.Stencil == null;
                    if ((!depthIndependent && ((draw.Geometry ?? geometry) != ShaderPreviewGeometry.ScreenQuad || pass.State.ZWrite)) ||
                        pass.State.AlphaToMask ||
                        output.Size is <= 0 or > RenderSize)
                    {
                        throw new NotSupportedException($"Unsupported state for render target '{output.Name}'.");
                    }
                    renderedTargets.Add(output);
                }
            }
            catch (Exception error) when (error is NotSupportedException or InvalidDataException)
            {
                throw new NotSupportedException($"{pass.ShaderName} · {pass.LightMode} {pass.PassName}. {error.Message}", error);
            }
        }

        bool animated = ImageEffectPreview.HasAnimatedInput(imageEffect) || imageEffect is ImageEffectPass.SunShafts or ImageEffectPass.Noise
                or ImageEffectPass.SkinDeformation or ImageEffectPass.PostProcessingGrain ||
            MotionPreview.UsesHistory(imageEffect) ||
            geometry == ShaderPreviewGeometry.Particles || operations.OfType<ShaderPreviewDraw>().Any(draw =>
            draw.Billboard.HasValue || draw.Pass.Stages.SelectMany(stage => stage.Buffers).SelectMany(buffer => buffer.Parameters)
                .Any(parameter => parameter.Name is "_Time" or "_SinTime" or "_CosTime" or "unity_DeltaTime" or "_WaveOffset"));
        return new ShaderPreviewScene(reader.Name, geometry, animated, operations, PreviewAmbientLinear, lights, values, textures,
            unusedKeywords, lightingPass, imageEffect, cameraTextures)
        {
            NoiseSettings = noiseSettings, SerializedShader = serialized, DepthBlitSamples = depthBlitSamples
        };

        T Resolve<T>(SerializedFile source, PPtr<T> reference) where T : AssetStudio.Object =>
            ResolveReference(reference, source, loadExternalSources, resolveAsset, cancellationToken);

        void PrepareTexture(string name, ShaderPreviewTextureDimension dimension)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool cube = dimension == ShaderPreviewTextureDimension.Cube;
            bool image = dimension == ShaderPreviewTextureDimension.Texture2D;
            if (cameraTextures.Contains(name) || ImageEffectPreview.ProvidesTexture(imageEffect, name) ||
                imageEffect == ImageEffectPass.TemporalAntialiasing && name == TemporalAntialiasingPreview.HistoryTexture)
            {
                if (!image)
                {
                    throw new InvalidDataException($"The preview's camera texture '{name}' requires a 2D binding.");
                }
                return;
            }
            if (captures.Contains(name))
            {
                if (!image)
                {
                    throw new InvalidDataException($"GrabPass texture '{name}' must be a 2D resource.");
                }
                return;
            }
            if (textures.TryGetValue(name, out var prepared))
            {
                if (prepared.Dimension != dimension)
                {
                    throw new InvalidDataException($"Conflicting texture dimensions for '{name}'.");
                }
                return;
            }
            ShaderPreviewTexture texture;
            if (material != null && textureSlots.TryGetValue(name, out var slot) && !slot.m_Texture.IsNull)
            {
                texture = ReadTexture(Resolve(material.assetsFile, slot.m_Texture), dimension);
            }
            else if (cameraPreview?.CreateSceneTexture(name, dimension) is { } sceneTexture)
            {
                texture = sceneTexture;
            }
            else if (reader.TextureDefaults.TryGetValue(name, out string? declared))
            {
                if (!(image || cube))
                {
                    throw new NotSupportedException($"No {dimension} default texture is available for '{name}'.");
                }
                texture = CreateDeclaredTexture(name, declared, cube);
            }
            else if (cube && name is "unity_SpecCube0" or "unity_SpecCube1")
            {
                texture = CreateReflectionProbe(PreviewAmbientLinear);
            }
            else if (dimension == ShaderPreviewTextureDimension.Texture3D && name == "unity_ProbeVolumeSH")
            {
                texture = CreateProbeVolume(PreviewAmbientLinear);
            }
            else if (image && name is "_CameraDepthTexture" or "_DepthTex")
            {
                // Depth of the preview's backdrop, cleared to the camera's far plane.
                var depth = new byte[sizeof(float)];
                BinaryPrimitives.WriteSingleLittleEndian(depth, serialized ? 0f : 1f);
                texture = new ShaderPreviewTexture(1, 1, 1, 41 /* R32_FLOAT */, 4, false, depth, 0, 1, 1, 0);
            }
            else if (image && name is "_ClipTex" or "_GUIClipTexture")
            {
                texture = CreateClipMask();
                if (name == "_GUIClipTexture")
                {
                    // GUI.color starts white for the preview's GUI draw.
                    values.TryAdd("_Color", NumericsVector4.One);
                }
            }
            else if (image && imageEffect == ImageEffectPass.Bokeh && name == ImageEffectPreview.BokehAperture)
            {
                texture = material != null && textureSlots.TryGetValue(ImageEffectPreview.MainTexture, out var aperture) && !aperture.m_Texture.IsNull
                    ? ReadTexture(Resolve(material.assetsFile, aperture.m_Texture), dimension)
                    : ImageEffectPreview.CreateBokehAperture();
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

    private static bool IsShadowOnly(ShaderPreviewReader reader, ShaderPreviewPassDefinition pass) =>
        IsMode(pass, "Always") && pass.HasDirect3D11 && reader.GetVariantKeywords(pass, vertex: true)
            .All(keywords => keywords.Contains("SHADOWS_DEPTH") || keywords.Contains("SHADOWS_CUBE"));

    private static int GetSkyboxSide(LegacyShaderPreviewPass pass)
    {
        string[] faces = ["_FrontTex", "_BackTex", "_LeftTex", "_RightTex", "_UpTex", "_DownTex"];
        return Array.FindIndex(faces, face => pass.Fragment.Textures.Any(texture => texture.Name == face));
    }

    private static IEnumerable<(ShaderPreviewReader Reader, ShaderPreviewSubShader Sub, ShaderPreviewPassDefinition Pass)> ExpandPasses(
        Shader shader, ShaderPreviewReader reader, ShaderPreviewSubShader subShader, Func<Shader, PPtr<Shader>, Shader> resolve,
        CancellationToken token)
    {
        var dependencies = new Dictionary<string, ShaderPreviewReader>(StringComparer.Ordinal);
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
                referenced = ShaderPreviewReader.Read(ResolveUsePassShader(shader, shaderName, resolve, token));
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
    }

    private static Shader ResolveUsePassShader(Shader shader, string shaderName,
        Func<Shader, PPtr<Shader>, Shader> resolve, CancellationToken token)
    {
        bool unity53 = shader.version is [5, 3, ..];
        string referencedName = UsePassShaderName(shaderName);
        Shader? match = null;
        foreach (var dependency in shader.m_Dependencies)
        {
            token.ThrowIfCancellationRequested();
            Shader linked;
            string actual;
            try
            {
                linked = resolve(shader, dependency);
                actual = ShaderPreviewReader.ReadName(linked);
            }
            catch (Exception error) when (error is InvalidDataException or EndOfStreamException or DecoderFallbackException)
            {
                continue;
            }
            if (!UsePassShaderName(actual).Equals(referencedName, StringComparison.Ordinal))
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
        return match ?? throw new InvalidDataException($"UsePass shader '{shaderName}' could not be resolved from the shader dependencies.");

        string UsePassShaderName(string name) => unity53 ? Unity53ShaderPasses.UsePassShaderName(name) : name;
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
            "green" => [0, 255, 0, 255],
            "bump" when !cube => [128, 128, 255, 128],
            _ => throw new NotSupportedException($"Texture '{name}' has unsupported ShaderLab default '{declared}'.")
        };
        return CreateSolidTexture(value, cube ? 6 : 1);
    }

    internal static ShaderPreviewTexture CreateClipMask()
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

    private static ShaderPreviewTexture CreateProbeVolume(NumericsVector3 ambient)
    {
        // Built-in probe volumes pack the RGB spherical harmonics and the occlusion mask in four X slices.
        float[] coefficients = [0, 0, 0, ambient.X, 0, 0, 0, ambient.Y, 0, 0, 0, ambient.Z, 1, 1, 1, 1];
        return new ShaderPreviewTexture(4, 1, 1, 2 /* R32G32B32A32_FLOAT */, 16, false,
            MemoryMarshal.AsBytes(coefficients.AsSpan()).ToArray(), 1, 1, 1, 0, VolumeDepth: 1);
    }

    internal static ShaderPreviewTexture ReadTexture(Texture asset, ShaderPreviewTextureDimension dimension)
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

    internal static byte[] BindConstants(ShaderPreviewScene scene, ShaderPreviewDraw draw, ShaderPreviewBuffer buffer,
        NumericsMatrix4x4 view, NumericsMatrix4x4 projection, NumericsVector3 eye, int size, IReadOnlySet<string> captures,
        float time, float deltaTime, BuiltInLightingPreview? lighting, IReadOnlyDictionary<string, ShaderPreviewTexture>? cameraTextures,
        MotionPreviewFrame? motion, NumericsMatrix4x4? billboardTransform, ShaderPreviewParameter? unusedVertexParameter)
    {
        var data = new byte[(buffer.Size + 15) & ~15];
        var light = draw.Light;
        foreach (var parameter in buffer.Parameters)
        {
            if (ReferenceEquals(parameter, unusedVertexParameter))
            {
                continue;
            }
            if (parameter.Type is not (0 or 1 or 2) || parameter.ArraySize < 1 || parameter.Offset < 0)
            {
                throw new NotSupportedException($"Unsupported shader parameter format for '{parameter.Name}'.");
            }
            if (parameter.IsMatrix)
            {
                const int matrixBytes = 16 * sizeof(float);
                if (parameter.Type != 0 || parameter.Rows != 4 || parameter.Columns != 4 ||
                    parameter.ArraySize > (data.Length - parameter.Offset) / matrixBytes)
                {
                    throw new NotSupportedException($"Unsupported shader matrix layout for '{parameter.Name}'.");
                }
                for (int element = 0; element < parameter.ArraySize; element++)
                {
                    var matrix = ResolveMatrix(parameter.Name, parameter.ArraySize, element);
                    MemoryMarshal.Write(data.AsSpan(parameter.Offset + matrixBytes * element), in matrix);
                }
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
                            if (parameter.Type == 2 && integer is not (0f or 1f))
                            {
                                throw new InvalidDataException($"Shader parameter '{parameter.Name}' requires a Boolean.");
                            }
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

        NumericsMatrix4x4 ResolveMatrix(string name, int arraySize, int element)
        {
            if (arraySize != 1)
            {
                if (name == "unity_WorldToShadow" && arraySize == 4 && scene.LightingPass == BuiltInLightingPass.CollectShadows)
                {
                    // Only cascade zero is active in the preview.
                    return element == 0 ? lighting!.WorldToShadow : default;
                }
                throw MissingValue(name);
            }
            return draw.Matrices?.TryGetValue(name, out var matrixValue) == true ? matrixValue : name switch
            {
                "glstate_matrix_mvp" or "unity_MatrixVP" when billboardTransform.HasValue => billboardTransform.Value,
                "unity_MatrixVP" when scene.SerializedShader && (draw.Geometry ?? scene.Geometry) == ShaderPreviewGeometry.ScreenQuad =>
                    NumericsMatrix4x4.Identity,
                "glstate_matrix_mvp" when (draw.Geometry ?? scene.Geometry) == ShaderPreviewGeometry.ScreenQuad => NumericsMatrix4x4.Identity,
                "glstate_matrix_mvp" when motion is { } frame => frame.Model * view * projection,
                "glstate_matrix_mvp" or "unity_MatrixMVP" or "unity_MatrixVP" => view * projection,
                "glstate_matrix_modelview0" or "unity_MatrixV" or "unity_MatrixMV" => view,
                "glstate_matrix_projection" or "unity_MatrixP" => projection,
                "glstate_matrix_invtrans_modelview0" or "unity_MatrixITMV" => NumericsMatrix4x4.Transpose(Inverse(view)),
                "_CameraToWorld" or "unity_CameraToWorld" when lighting != null => NumericsMatrix4x4.CreateScale(1, 1, -1) * Inverse(view),
                "unity_MatrixInvV" or "_CameraToWorld" => Inverse(view),
                "_WorldToCamera" or "unity_WorldToCamera" when lighting != null => view * NumericsMatrix4x4.CreateScale(1, 1, -1),
                "unity_CameraProjection" when lighting != null => BuiltInLightingPreview.DepthProjection(projection, scene.SerializedShader),
                "_WorldToCameraMatrix" when scene.ImageEffect == ImageEffectPass.ScreenSpaceReflection => view,
                "_CameraToWorldMatrix" when scene.ImageEffect == ImageEffectPass.ScreenSpaceReflection => Inverse(view),
                "_ProjectToPixelMatrix" when scene.ImageEffect == ImageEffectPass.ScreenSpaceReflection => ScreenSpaceReflectionPreview.ProjectToPixel(projection, RenderSize),
                "_NonJitteredVP" when scene.SerializedShader && motion.HasValue => view * projection,
                "_PreviousVP" when scene.SerializedShader && motion is { } frame => frame.PreviousModelViewProjection,
                "_FrustumCornersWS" when scene.ImageEffect == ImageEffectPass.GlobalFog => FrustumCorners(view, projection),
                "_AM_MATRIX_CURR_REPROJ" when motion is { } frame => frame.Reprojection,
                "_AM_MATRIX_PREV_MVP" when motion is { } frame => frame.PreviousModelViewProjection,
                "_AM_WORLD_TO_LOCAL_MATRIX" when scene.ImageEffect == ImageEffectPass.SkinDeformation => NumericsMatrix4x4.Identity,
                "unity_CameraInvProjection" when lighting != null => Inverse(BuiltInLightingPreview.DepthProjection(projection, scene.SerializedShader)),
                "unity_World2Shadow0" when scene.LightingPass == BuiltInLightingPass.CollectShadows => lighting!.WorldToShadow,
                "unity_World2Shadow1" or "unity_World2Shadow2" or "unity_World2Shadow3"
                    when scene.LightingPass == BuiltInLightingPass.CollectShadows => default,
                "unity_ProbeVolumeWorldToObject" => NumericsMatrix4x4.Identity,
                "_EnvMatrix" when scene.Geometry == ShaderPreviewGeometry.Text && scene.Values.TryGetValue("_EnvMatrixRotation", out var rotation) =>
                    TextMeshProShaderPreview.EnvironmentMatrix(rotation),
                "_GUIClipTextureMatrix" or "unity_GUIClipTextureMatrix" =>
                    NumericsMatrix4x4.CreateScale(0.5f, 0.5f, 1) * NumericsMatrix4x4.CreateTranslation(0.5f, 0.5f, 0),
                "_TerrainEngineBendTree" => NumericsMatrix4x4.CreateRotationZ(0.08f * MathF.Sin(time)),
                "_Object2World" or "_World2Object" or "unity_ObjectToWorld" or "unity_WorldToObject" => NumericsMatrix4x4.Identity,
                _ => throw MissingValue(name)
            };
        }

        NumericsVector4 ResolveVector(string name, int arraySize, int element)
        {
            if (draw.VectorArrays?.TryGetValue(name, out var elements) == true && elements.Length == arraySize)
            {
                return elements[element];
            }
            if (arraySize == 1 && draw.Values?.TryGetValue(name, out var perDraw) == true)
            {
                return perDraw;
            }
            if (TryVertexLight(name, arraySize, element, scene.Lights, view, out var vertexLight) ||
                (arraySize == 1 && TryTerrainImposterLight(name, scene.Lights, out vertexLight)))
            {
                return vertexLight;
            }
            if (arraySize != 1)
            {
                throw MissingValue(name);
            }
            if (scene.NoiseSettings is { } noise && name is "_GrainOffsetScale" or "_ScratchOffsetScale" or "_Intensity")
            {
                return noise.GetValue(name, time, size, scene.Textures);
            }
            if (scene.Values.TryGetValue(name, out var property))
            {
                return property;
            }
            if (name.EndsWith("_TexelSize", StringComparison.Ordinal))
            {
                string slot = name[..^"_TexelSize".Length];
                string textureName = draw.TextureSource(slot);
                if (draw.Inputs?.TryGetValue(slot, out var target) == true)
                {
                    return new NumericsVector4(1f / target.Size, 1f / target.Size, target.Size, target.Size);
                }
                if (captures.Contains(textureName))
                {
                    return new NumericsVector4(1f / RenderSize, 1f / RenderSize, RenderSize, RenderSize);
                }
                var resources = scene.CameraTextures.Contains(textureName) || ImageEffectPreview.ProvidesTexture(scene.ImageEffect, textureName)
                    ? cameraTextures! : scene.Textures;
                if (resources.TryGetValue(textureName, out var texture))
                {
                    return new NumericsVector4(1f / texture.Width, 1f / texture.Height, texture.Width, texture.Height);
                }
            }
            if (name.EndsWith("_ST", StringComparison.Ordinal) && scene.CameraTextures.Contains(name[..^3]))
            {
                // Camera targets cover the whole viewport with an unscaled UV rectangle.
                return new NumericsVector4(1, 1, 0, 0);
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
                "_CameraWS" when scene.ImageEffect == ImageEffectPass.GlobalFog => new NumericsVector4(eye, 1),
                "_HeightParams" when scene.ImageEffect == ImageEffectPass.GlobalFog =>
                    new NumericsVector4(1, eye.Y - 1, eye.Y <= 1 ? 1 : 0, 1),
                "_SunPosition" when scene.ImageEffect == ImageEffectPass.SunShafts => ImageEffectPreview.SunPosition(view, projection, time),
                "_SunColor" when scene.ImageEffect == ImageEffectPass.SunShafts => ImageEffectPreview.SunPosition(view, projection, time).Z >= 0
                    ? new NumericsVector4(1.15f) : NumericsVector4.Zero,
                "_CurveParams" when scene.ImageEffect == ImageEffectPass.DepthOfField => ImageEffectPreview.DepthOfFieldCurve(view),
                "_Screen" when draw.PointPass == ShaderPreviewPointPass.Draw => new NumericsVector4(1f / size, 1f / size, 2, 0),
                "_AM_OBJECT_ID" when motion.HasValue => new NumericsVector4(1f / 255, 0, 0, 0),
                "_AM_MOTION_SCALE" when motion is { } frame => new NumericsVector4(frame.Scale, 0, 0, 0),
                "_AM_MIN_VELOCITY" when motion.HasValue => new NumericsVector4(MotionPreview.MinVelocity, 0, 0, 0),
                "_AM_MAX_VELOCITY" when motion.HasValue => new NumericsVector4(MotionPreview.MaxVelocity, 0, 0, 0),
                "_AM_RCP_TOTAL_VELOCITY" when motion.HasValue => new NumericsVector4(1f / (MotionPreview.MaxVelocity - MotionPreview.MinVelocity), 0, 0, 0),
                "_AM_BONE_TEXEL_SIZE" when scene.ImageEffect == ImageEffectPass.SkinDeformation => BoneTexelSize(),
                "_AM_BONE_TEXEL_HALFSIZE" when scene.ImageEffect == ImageEffectPass.SkinDeformation => BoneTexelSize() * 0.5f,
                "_ScreenParams" => new NumericsVector4(size, size, 1f + 1f / size, 1f + 1f / size),
                "_ClipRange" or "_ClipRange0" or "_ClipRange1" or "_ClipRange2" or "_ClipRange3" => new NumericsVector4(0, 0, 1, 1),
                "_ClipArgs0" or "_ClipArgs1" or "_ClipArgs2" or "_ClipArgs3" => new NumericsVector4(16, 16, 0, 1),
                "_ClipSharpness" => new NumericsVector4(16, 16, 0, 0),
                "_ClipRect" => new NumericsVector4(-1, -1, 1, 1),
                "_TextureSampleAdd" => scene.Textures.TryGetValue("_MainTex", out var main) && main.Format == 65
                    ? new NumericsVector4(1, 1, 1, 0) : NumericsVector4.Zero,
                "_UndoOutputSRGB" when scene.ImageEffect == ImageEffectPass.RenderTargetDisplay => NumericsVector4.Zero,
                "_Phase" when scene.ImageEffect == ImageEffectPass.PostProcessingGrain =>
                    new NumericsVector4(scene.ShaderName == PostProcessingV2Preview.GrainBaker ? time % 10 : time / 20f),
                // Othercide's BOHColorCorrection sets (1 / gamma, contrast, color-blind shift, brightness).
                "_BOHUserSettingsColors" when scene.SerializedShader => new NumericsVector4(1, 1, 0, 1),
                "_ProjInfo" when scene.ImageEffect == ImageEffectPass.ScreenSpaceReflection => ScreenSpaceReflectionPreview.ProjectionInfo(projection, RenderSize),
                "_PixelsPerMeterAtOneMeter" when scene.ImageEffect == ImageEffectPass.ScreenSpaceReflection =>
                    new NumericsVector4(-RenderSize * projection.M22 / 2f),
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
                "_ProjectionParams" => new NumericsVector4(lighting != null ? -1 : 1, near, far, 1f / far),
                "_ZBufferParams" => BuiltInLightingPreview.DepthParameters(scene.SerializedShader),
                "unity_OrthoParams" when lighting != null => NumericsVector4.Zero,
                "unity_LightmapFade" or "unity_ShadowFadeCenterAndType" when lighting != null => NumericsVector4.Zero,
                "_LightSplitsNear" when scene.SerializedShader && scene.LightingPass == BuiltInLightingPass.CollectShadows => new(near, far, far, far),
                "_LightSplitsFar" when scene.SerializedShader && scene.LightingPass == BuiltInLightingPass.CollectShadows => new(far, 0, 0, 0),
                "_LightShadowData" when lighting != null => new NumericsVector4(0.25f, 0, 0.25f, -3),
                "_LightAsQuad" when lighting != null => new NumericsVector4(1, 0, 0, 0),
                "_LightDir" when lighting != null => new NumericsVector4(-light.Direction, 0),
                "_LightColor" when lighting != null => new NumericsVector4(light.Color, 1),
                "_Time" => new NumericsVector4(time / 20, time, time * 2, time * 3),
                // The editor-panning offset is zero during playback. Player time comes from _Time.
                "_TimeEditor" when scene.SerializedShader => NumericsVector4.Zero,
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
                // The shader dots the selector with the unoccluded mask even with the proxy volume disabled.
                "unity_OcclusionMaskSelector" => new NumericsVector4(1, 0, 0, 0),
                "unity_ProbesOcclusion" => NumericsVector4.One,
                "unity_ProbeVolumeParams" or "unity_ProbeVolumeMin" => NumericsVector4.Zero,
                "unity_ProbeVolumeSizeInv" => NumericsVector4.One,
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

        NumericsVector4 BoneTexelSize()
        {
            var texture = cameraTextures![MotionPreview.BoneTexture];
            return new NumericsVector4(1f / texture.Width, 1f / texture.Height, texture.Width, texture.Height);
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

    private static NumericsMatrix4x4 FrustumCorners(NumericsMatrix4x4 view, NumericsMatrix4x4 projection)
    {
        const float far = ShaderPreviewCamera.Far;
        var right = new NumericsVector3(view.M11, view.M21, view.M31) * (far / projection.M11);
        var up = new NumericsVector3(view.M12, view.M22, view.M32) * (far / projection.M22);
        var forward = -new NumericsVector3(view.M13, view.M23, view.M33) * far;
        var topLeft = forward - right + up;
        var topRight = forward + right + up;
        var bottomRight = forward + right - up;
        var bottomLeft = forward - right - up;
        // The DXBC loads the x, y and z components of all four corner rays in separate registers.
        return new NumericsMatrix4x4(topLeft.X, topRight.X, bottomRight.X, bottomLeft.X,
            topLeft.Y, topRight.Y, bottomRight.Y, bottomLeft.Y,
            topLeft.Z, topRight.Z, bottomRight.Z, bottomLeft.Z, 0, 0, 0, 0);
    }

    private static bool TryTerrainImposterLight(string name, IReadOnlyList<ShaderPreviewLight> lights, out NumericsVector4 value)
    {
        value = default;
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
