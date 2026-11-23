using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace AssetStudio.Avalonia.Services;

internal static class EffectPreviewAssets
{
    internal const string WindowShader = "BoH/Environment/WindowParallax";
    internal const string ShopMaterial = "M_Lvl_Street_WindowParallax_Shops";

    internal static IEnumerable<AssetHandle> Find(Shader shader, ClassIDType type, string name, CancellationToken token)
    {
        var arena = shader.assetsFile.assetsManager.ProjectIndex.Arena;
        for (int i = 0, count = arena.Count; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            if (arena.GetType(i) == type && arena.GetName(i) == name)
            {
                yield return arena.MaterializeHandle(i);
            }
        }
    }

    internal static T Require<T>(Shader shader, ClassIDType type, string name,
        Func<AssetHandle, AssetStudio.Object?> resolve, CancellationToken token) where T : AssetStudio.Object
    {
        using var matches = Find(shader, type, name, token).GetEnumerator();
        if (!matches.MoveNext() || resolve(matches.Current) is not T asset || matches.MoveNext())
        {
            throw new InvalidDataException($"'{ShaderPreviewReader.ReadName(shader)}' requires one readable {type} named '{name}'.");
        }
        return asset;
    }

    internal static IDictionary Fields(IDictionary fields, string name) => fields[name] as IDictionary ??
        throw new InvalidDataException($"The effect is missing its '{name}' settings.");

    internal static float Float(IDictionary fields, string name) =>
        fields[name] is float value && float.IsFinite(value) ? value :
            throw new InvalidDataException($"The effect has an invalid '{name}' value.");
}
