using System;
using System.IO;
using System.Threading;

namespace AssetStudio.Avalonia.Services;

internal static class PostProcessingResourcesPreview
{
    internal const int PointerSize = sizeof(int) + sizeof(long);

    internal static MonoBehaviour Find(Shader shader, Func<AssetHandle, AssetStudio.Object?> resolveAsset,
        Func<SerializedFile, PPtr<MonoScript>, MonoScript> resolveScript, CancellationToken token)
    {
        var arena = shader.assetsFile.assetsManager.ProjectIndex.Arena;
        MonoBehaviour? resources = null;
        for (int i = 0, count = arena.Count; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            if (arena.GetType(i) != ClassIDType.MonoBehaviour || arena.GetName(i) != "PostProcessResources")
            {
                continue;
            }
            if (resolveAsset(arena.MaterializeHandle(i)) is not MonoBehaviour candidate)
            {
                throw new InvalidDataException("The indexed PostProcessResources asset could not be read.");
            }
            var script = resolveScript(candidate.assetsFile, candidate.m_Script);
            if (script.m_ClassName != "PostProcessResources" || script.m_Namespace != "UnityEngine.Rendering.PostProcessing")
            {
                continue;
            }
            if (resources != null)
            {
                throw new InvalidDataException("The project has multiple Post Processing Stack resource assets.");
            }
            resources = candidate;
        }
        return resources ?? throw new NotSupportedException($"'{ShaderPreviewReader.ReadName(shader)}' requires the project's PostProcessResources asset.");
    }

    internal static (PPtr<Texture>? Noise64, PPtr<Texture>? Noise256, PPtr<Texture> Area, PPtr<Texture> Search)
        ReadTextures(ObjectReader reader)
    {
        _ = new MonoBehaviour(reader);
        var noise64 = ReadFirstTexture(reader);
        var noise256 = ReadFirstTexture(reader);
        reader.CheckArrayLength(2 * PointerSize);
        return (noise64, noise256, new PPtr<Texture>(reader), new PPtr<Texture>(reader));
    }

    internal static (PPtr<Texture>? Noise256, PPtr<Shader> Copy, PPtr<AssetStudio.Object> Compute) ReadComputeReferences(
        MonoBehaviour resources, Shader shader, int shaderField, int computeField)
    {
        var file = resources.assetsFile;
        using var stream = file.reader.Clone();
        var reader = new ObjectReader(stream, file, file.ObjectInfoDic[resources.m_PathID]);
        if (reader.version is not [2019, 3, ..])
        {
            throw new NotSupportedException("The Post Processing compute resource reader supports the Unity 2019.3 layout.");
        }
        var (_, noise256, _, _) = ReadTextures(reader);
        // PostProcessResources serializes Shaders, followed by ComputeShaders, in package field order.
        const int shaderFields = 24, computeFields = 12, copyField = 1;
        long start = reader.Position;
        if (reader.byteStart + reader.byteSize - start != (shaderFields + computeFields) * PointerSize ||
            (uint)shaderField >= shaderFields || (uint)computeField >= computeFields)
        {
            throw new InvalidDataException("The PostProcessResources shader references have an unexpected layout.");
        }
        reader.Position = start + shaderField * PointerSize;
        var reference = new PPtr<Shader>(reader);
        if (reference.m_PathID != shader.m_PathID || !reference.TryGetAssetsFile(out var owner) || owner != shader.assetsFile)
        {
            throw new InvalidDataException("PostProcessResources does not reference the selected shader.");
        }
        reader.Position = start + copyField * PointerSize;
        var copy = new PPtr<Shader>(reader);
        reader.Position = start + (shaderFields + computeField) * PointerSize;
        return (noise256, copy, new PPtr<AssetStudio.Object>(reader));
    }

    private static PPtr<Texture>? ReadFirstTexture(ObjectReader reader)
    {
        reader.CheckArrayLength(sizeof(int));
        int count = reader.ReadInt32();
        long remaining = reader.byteStart + reader.byteSize - reader.Position;
        if (count < 0 || count > remaining / PointerSize)
        {
            throw new InvalidDataException("The Post Processing blue noise array has an invalid length.");
        }
        if (count == 0)
        {
            return null;
        }
        var first = new PPtr<Texture>(reader);
        reader.Position += (count - 1L) * PointerSize;
        return first;
    }
}
