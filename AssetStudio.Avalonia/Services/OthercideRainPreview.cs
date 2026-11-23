using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using NumericsVector4 = System.Numerics.Vector4;

namespace AssetStudio.Avalonia.Services;

internal static class OthercideRainPreview
{
    private const string RippleTarget = "_RainRippleRT";
    private const string RippleShader = "BoH/Environment/Rain/RainRippleToRT";
    private const string RippleMaterial = "M_RippleUpdateMat";
    private const string RippleSource = "_RippleSourceTex";
    private const string SceneAssetsSuffix = ".sharedAssets";

    internal static bool Handles(string shaderName) => shaderName is "BoH/Water" or "BoH/WaterVColor";

    internal static void AddRipplePass(Shader shader, Material? material, List<ShaderPreviewOperation> operations,
        Dictionary<string, ShaderPreviewTexture> textures, Action<SerializedFile, CancellationToken> loadExternalSources,
        Func<AssetHandle, AssetStudio.Object?> resolveAsset, CancellationToken token)
    {
        if (!operations.OfType<ShaderPreviewDraw>().Any(UsesRipple))
        {
            return;
        }

        var arena = shader.assetsFile.assetsManager.ProjectIndex.Arena;
        var settings = material != null && material.assetsFile.fileName.EndsWith(SceneAssetsSuffix, StringComparison.Ordinal)
            ? ReadSceneSettings(material.assetsFile)
            : ReadIsolatedSettings();
        var source = settings.Material;
        if (!source.m_SavedProperties.m_TexEnvs.Any(slot => slot.Key == RippleSource && !slot.Value.m_Texture.IsNull))
        {
            throw new InvalidDataException($"{RippleTarget} requires the ripple source texture in '{source.m_Name}'.");
        }

        var producer = Resolve(source.assetsFile, source.m_Shader);
        if (producer.version is not [2019, 3, ..] || ShaderPreviewReader.ReadName(producer) != RippleShader)
        {
            throw new InvalidDataException($"{RippleTarget} requires the Unity 2019.3 '{RippleShader}' shader.");
        }
        var scene = ShaderPreviewService.Prepare(producer, source, loadExternalSources, resolveAsset, token);
        if (scene.Operations is not [ShaderPreviewDraw update] || !CustomRenderTexturePreview.UsesUpdateZones(update)
            || update.Pass.State.Stencil != null)
        {
            throw new NotSupportedException($"{RippleTarget} requires a single custom render texture update pass without stencil.");
        }
        update = CustomRenderTexturePreview.Prepare(update, settings.Size);
        var values = new Dictionary<string, NumericsVector4>(scene.Values, StringComparer.Ordinal);
        foreach (var (name, value) in update.Values!)
        {
            values[name] = value;
        }
        values["_Speed"] = new(settings.Speed, 0, 0, 0);
        values["_RainIntensity"] = new(settings.Intensity, 0, 0, 0);

        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, texture) in scene.Textures)
        {
            string resource = $"{RippleTarget}/{name}";
            textures.Add(resource, texture);
            sources.Add(name, resource);
        }
        var output = new ShaderPreviewRenderTarget(RippleTarget, settings.Size)
        {
            Wrap = 0,
            ClearColor = new(0.5f, 0.5f, 1, 1)
        };
        for (int i = 0; i < operations.Count; i++)
        {
            if (operations[i] is ShaderPreviewDraw draw && UsesRipple(draw))
            {
                var inputs = draw.Inputs == null
                    ? new Dictionary<string, ShaderPreviewRenderTarget>(StringComparer.Ordinal)
                    : new Dictionary<string, ShaderPreviewRenderTarget>(draw.Inputs, StringComparer.Ordinal);
                inputs.Add(RippleTarget, output);
                operations[i] = draw with { Inputs = inputs };
            }
        }
        // RainManager creates a color-only render texture. There is no depth buffer for the update pass.
        var pass = update.Pass with { State = update.Pass.State with { ZTest = ShaderPreviewDepth.Off, ZWrite = false } };
        operations.Insert(0, update with { Pass = pass, Output = output, Values = values, TextureSources = sources });

        T Resolve<T>(SerializedFile file, PPtr<T> reference) where T : AssetStudio.Object =>
            ShaderPreviewService.ResolveReference(reference, file, loadExternalSources, resolveAsset, token);

        RainSettings ReadSceneSettings(SerializedFile file)
        {
            // A streamed scene stores its hierarchy in the sibling of its shared assets file.
            string sceneFile = file.fileName[..^SceneAssetsSuffix.Length];
            var nodes = arena.GetIndicesForFile(sceneFile).Where(index =>
                arena.GetType(index) == ClassIDType.GameObject &&
                StringComparer.OrdinalIgnoreCase.Equals(arena.GetOriginalPath(index), file.originalPath) &&
                arena.GetName(index) == "PB_RainManager").Take(2).ToArray();
            if (nodes.Length != 1 || resolveAsset(arena.MaterializeHandle(nodes[0])) is not GameObject { m_IsActive: true } node)
            {
                throw new InvalidDataException($"{RippleTarget} requires one active PB_RainManager in '{sceneFile}'.");
            }
            var components = node.m_Components.Where(reference => reference.m_FileID == 0 &&
                    node.assetsFile.ObjectInfoDic.TryGetValue(reference.m_PathID, out var info) &&
                    info.classID == (int)ClassIDType.MonoBehaviour)
                .Select(reference => Resolve(node.assetsFile, reference))
                .OfType<MonoBehaviour>().Where(component => !component.m_Script.IsNull &&
                    Resolve(component.assetsFile, component.m_Script) is
                        { m_ClassName: "RainManager", m_Namespace: "", m_AssemblyName: "Assembly-CSharp.dll" })
                .Take(2).ToArray();
            if (components is not [MonoBehaviour { m_Enabled: 1 } rain] || rain.version is not [2019, 3, ..] ||
                rain.ToType() is not { } fields ||
                fields["m_RippleTexResolution"] is not int size || size is <= 0 or > ShaderPreviewService.MaxDrawTargetSize ||
                fields["m_RainSpeed"] is not float speed || !float.IsFinite(speed) ||
                fields["m_isRaining"] is not byte raining || raining > 1 ||
                fields["m_RainingIntensity"] is not float intensity || !float.IsFinite(intensity) ||
                fields["m_RippleSourceMaterial"] is not IDictionary reference ||
                reference["m_FileID"] is not int fileId || reference["m_PathID"] is not long pathId)
            {
                throw new InvalidDataException($"{RippleTarget} requires an enabled RainManager with valid ripple settings in '{sceneFile}'.");
            }
            var materialReference = new PPtr<Material>(fileId, pathId, rain.assetsFile);
            return new RainSettings(Resolve(rain.assetsFile, materialReference), size, speed, raining == 1 ? intensity : 0);
        }

        RainSettings ReadIsolatedSettings()
        {
            // The isolated water preview uses the game's shared ripple material at the standard scene resolution.
            int materialIndex = -1;
            for (int i = 0, count = arena.Count; i < count; i++)
            {
                token.ThrowIfCancellationRequested();
                if (arena.GetType(i) != ClassIDType.Material || arena.GetName(i) != RippleMaterial)
                {
                    continue;
                }
                if (materialIndex != -1)
                {
                    throw new InvalidDataException($"Multiple '{RippleMaterial}' assets were found for {RippleTarget}.");
                }
                materialIndex = i;
            }
            if (materialIndex == -1 || resolveAsset(arena.MaterializeHandle(materialIndex)) is not Material sourceMaterial)
            {
                throw new InvalidDataException($"{RippleTarget} requires the project's '{RippleMaterial}' asset.");
            }
            var floats = sourceMaterial.m_SavedProperties.m_Floats;
            if (Array.Find(floats, property => property.Key == "_Speed") is not { Key: not null, Value: var speed } ||
                Array.Find(floats, property => property.Key == "_RainIntensity") is not { Key: not null, Value: var intensity } ||
                !float.IsFinite(speed) || !float.IsFinite(intensity))
            {
                throw new InvalidDataException($"'{RippleMaterial}' has invalid ripple speed or intensity.");
            }
            return new RainSettings(sourceMaterial, 512, speed, intensity);
        }
    }

    private static bool UsesRipple(ShaderPreviewDraw draw) =>
        draw.Stages.Any(stage => stage.Textures.Any(binding => binding.Name == RippleTarget));

    private sealed record RainSettings(Material Material, int Size, float Speed, float Intensity);
}
