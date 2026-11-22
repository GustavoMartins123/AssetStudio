using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using NumericsVector4 = System.Numerics.Vector4;

namespace AssetStudio.Avalonia.Services;

internal sealed record NoisePreviewSettings(float GrainMin, float GrainMax, float GrainSize,
    float ScratchMin, float ScratchMax, float ScratchFps, float ScratchJitter)
{
    internal NumericsVector4 GetValue(string name, float time, int size, IReadOnlyDictionary<string, ShaderPreviewTexture> textures)
    {
        uint frame = (uint)Math.Max(0, (int)MathF.Round(time * ShaderPreviewService.FramesPerSecond));
        uint scratch = (uint)Math.Max(0, (int)(time * ScratchFps));
        var grainTexture = textures[NoisePreview.GrainTexture];
        var scratchTexture = textures[NoisePreview.ScratchTexture];
        return name switch
        {
            "_GrainOffsetScale" => new NumericsVector4(RandomUnit(frame, 0), RandomUnit(frame, 1),
                size / (grainTexture.Width * GrainSize), size / (grainTexture.Height * GrainSize)),
            "_ScratchOffsetScale" => new NumericsVector4(RandomUnit(scratch, 2) + RandomUnit(frame, 3) * ScratchJitter,
                RandomUnit(scratch, 4) + RandomUnit(frame, 5) * ScratchJitter,
                (float)size / scratchTexture.Width, (float)size / scratchTexture.Height),
            "_Intensity" => new NumericsVector4(GrainMin + RandomUnit(frame, 6) * (GrainMax - GrainMin),
                ScratchMin + RandomUnit(frame, 7) * (ScratchMax - ScratchMin), 0, 0),
            _ => throw new ArgumentException($"Unknown noise parameter '{name}'.", nameof(name))
        };
    }

    private static float RandomUnit(uint frame, uint channel)
    {
        // Seed from the timeline so playback and replay sample the same texture coordinates.
        uint value = unchecked((frame + 1) * 0x9e3779b9u + channel * 0x85ebca6bu);
        value ^= value >> 16;
        value = unchecked(value * 0x7feb352du);
        value ^= value >> 15;
        return (value >> 8) / 16777216f;
    }
}

internal static class NoisePreview
{
    internal const string GrainTexture = "_GrainTex";
    internal const string ScratchTexture = "_ScratchTex";

    internal static (NoisePreviewSettings Settings, Texture Grain, Texture Scratch) Read(Shader shader,
        Func<SerializedFile, PPtr<Texture>, Texture> resolveTexture, CancellationToken token)
    {
        // Legacy standalone players store camera components in level files and their PPtrs in the serialized file table.
        var source = shader.assetsFile;
        string? directory = Path.GetDirectoryName(source.reader.FullPath);
        if (directory == null || !Directory.Exists(directory) || source.reader.FileType != FileType.AssetsFile)
        {
            throw new NotSupportedException("The noise preview needs the original Unity 5.3 scene asset files.");
        }

        var scriptTypes = new Dictionary<string, HashSet<long>>(StringComparer.OrdinalIgnoreCase);
        var files = Directory.EnumerateFiles(directory, "level*")
            .Where(path => int.TryParse(Path.GetFileName(path).AsSpan("level".Length), out _))
            .Concat(Directory.EnumerateFiles(directory, "*.assets"))
            .Order(StringComparer.OrdinalIgnoreCase);
        foreach (string path in files)
        {
            token.ThrowIfCancellationRequested();
            using var fileReader = new FileReader(path);
            if (fileReader.FileType != FileType.AssetsFile)
            {
                continue;
            }
            var file = new SerializedFile(fileReader, source.assetsManager);
            if (file.version.Length < 2 || file.version[0] != 5 || file.version[1] != 3 ||
                (!file.fileName.Equals(source.fileName, StringComparison.OrdinalIgnoreCase) &&
                 !file.m_Externals.Any(external => external.fileName.Equals(source.fileName, StringComparison.OrdinalIgnoreCase))))
            {
                continue;
            }
            foreach (var info in file.m_Objects)
            {
                token.ThrowIfCancellationRequested();
                if (info.classID != (int)ClassIDType.MonoBehaviour)
                {
                    continue;
                }
                var reader = new ObjectReader(fileReader, file, info);
                var component = new MonoBehaviour(reader);
                string? scriptFile = ReferenceFileName(file, component.m_Script.m_FileID);
                if (component.m_Script.IsNull || scriptFile == null || !IsNoiseEffect(scriptFile, component.m_Script.m_PathID))
                {
                    continue;
                }

                reader.ReadUInt32(); // monochrome flag, aligned before the float settings.
                var settings = new NoisePreviewSettings(
                    ReadSetting(0, 5), ReadSetting(0, 5), ReadSetting(0.1f, 50),
                    ReadSetting(0, 5), ReadSetting(0, 5), ReadSetting(1, 30), ReadSetting(0, 1));
                var grain = new PPtr<Texture>(reader);
                var scratch = new PPtr<Texture>(reader);
                var rgb = new PPtr<Shader>(reader);
                var yuv = new PPtr<Shader>(reader);
                if (!ReferencesShader(rgb) && !ReferencesShader(yuv))
                {
                    continue;
                }
                if (reader.Position != info.byteStart + info.byteSize || grain.IsNull || scratch.IsNull)
                {
                    throw new InvalidDataException("The linked NoiseEffect does not contain a valid pair of texture references.");
                }
                return (settings, resolveTexture(file, grain), resolveTexture(file, scratch));

                bool ReferencesShader(PPtr<Shader> reference) => reference.m_PathID == shader.m_PathID &&
                    string.Equals(ReferenceFileName(file, reference.m_FileID), source.fileName, StringComparison.OrdinalIgnoreCase);

                float ReadSetting(float minimum, float maximum)
                {
                    float value = reader.ReadSingle();
                    if (!float.IsFinite(value))
                    {
                        throw new InvalidDataException("The linked NoiseEffect contains a non-finite setting.");
                    }
                    return Math.Clamp(value, minimum, maximum);
                }
            }
        }
        throw new NotSupportedException("No NoiseEffect component linked to this shader supplies the grain and scratch textures.");

        bool IsNoiseEffect(string fileName, long id)
        {
            if (!scriptTypes.TryGetValue(fileName, out var matches))
            {
                matches = [];
                string path = Path.Combine(directory, fileName);
                if (File.Exists(path))
                {
                    using var scriptReader = new FileReader(path);
                    if (scriptReader.FileType == FileType.AssetsFile)
                    {
                        var file = new SerializedFile(scriptReader, source.assetsManager);
                        foreach (var info in file.m_Objects.Where(item => item.classID == (int)ClassIDType.MonoScript))
                        {
                            var script = new MonoScript(new ObjectReader(scriptReader, file, info));
                            if (script.m_ClassName == "NoiseEffect" && script.m_Namespace.Length == 0 &&
                                script.m_AssemblyName == "Assembly-CSharp-firstpass.dll")
                            {
                                matches.Add(info.m_PathID);
                            }
                        }
                    }
                }
                scriptTypes.Add(fileName, matches);
            }
            return matches.Contains(id);
        }
    }

    private static string? ReferenceFileName(SerializedFile file, int id) => id == 0 ? file.fileName :
        id > 0 && id <= file.m_Externals.Count ? Path.GetFileName(file.m_Externals[id - 1].fileName) : null;
}
