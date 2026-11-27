using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace AssetStudio.CLI
{
    internal sealed class CliExportSettings
    {
        public ImageFormat ImageFormat { get; init; } = ImageFormat.Png;
        public ModelExportFormat ModelFormat { get; init; } = ModelExportFormat.Glb;
        public string? AssembliesDirectory { get; init; }
        public bool DryRun { get; init; }
    }

    /// <summary>
    /// Export of the asset types that go beyond textures, text and OBJ meshes, using the same converters as the GUI
    /// export (AssetStudioUtility): sprites, audio, fonts, shaders, MonoBehaviour data, video, animation clips and
    /// Animator models.
    /// </summary>
    internal sealed class CliAssetExporter
    {
        public static readonly IReadOnlyList<ClassIDType> AllTypes = new[]
        {
            ClassIDType.Texture2D, ClassIDType.Texture2DArray, ClassIDType.TextAsset, ClassIDType.Mesh,
            ClassIDType.Sprite, ClassIDType.AudioClip, ClassIDType.Font, ClassIDType.Shader,
            ClassIDType.MonoBehaviour, ClassIDType.VideoClip, ClassIDType.AnimationClip, ClassIDType.Animator
        };

        public const string SupportedTypeList =
            "Texture2D, Texture2DArray, TextAsset, Mesh, Sprite, AudioClip, Font, Shader, MonoBehaviour, VideoClip, AnimationClip, Animator, all";

        private readonly AssetsManager _assetsManager;
        private readonly CliExportSettings _settings;
        private readonly TextWriter _stdout;
        private readonly TextWriter _stderr;
        private readonly IList<ExportDiagnostic>? _diagnostics;
        private readonly ISet<string>? _missingDependencies;
        private AssemblyLoader? _assemblyLoader;
        private Dictionary<uint, string>? _bonePathHash;
        private Dictionary<uint, string>? _morphChannelNames;

        public CliAssetExporter(
            AssetsManager assetsManager,
            CliExportSettings settings,
            TextWriter stdout,
            TextWriter stderr,
            IList<ExportDiagnostic>? diagnostics = null,
            ISet<string>? missingDependencies = null)
        {
            _assetsManager = assetsManager;
            _settings = settings;
            _stdout = stdout;
            _stderr = stderr;
            _diagnostics = diagnostics;
            _missingDependencies = missingDependencies;
        }

        private void RecordSkip(AssetHandle handle, string baseName, string code, string message, string fieldOrResource = "", string? missingDependency = null)
        {
            if (!string.IsNullOrWhiteSpace(missingDependency))
            {
                _missingDependencies?.Add(missingDependency);
            }

            _diagnostics?.Add(new ExportDiagnostic
            {
                Severity = ExportDiagnosticSeverity.Warning,
                Outcome = ExportItemOutcome.Skipped,
                Code = code,
                AssetName = baseName,
                AssetType = handle.Type.ToString(),
                SourceFile = handle.SerializedFileName ?? string.Empty,
                PathId = handle.PathID,
                FieldOrResource = fieldOrResource,
                Message = message
            });
        }

        public static bool TryParseType(string raw, out ClassIDType type)
        {
            foreach (var candidate in AllTypes)
            {
                if (string.Equals(candidate.ToString(), raw, StringComparison.OrdinalIgnoreCase))
                {
                    type = candidate;
                    return true;
                }
            }

            type = ClassIDType.UnknownType;
            return false;
        }

        public static bool TryParseModelFormat(string raw, out ModelExportFormat format)
        {
            switch (raw.ToLowerInvariant())
            {
                case "glb":
                    format = ModelExportFormat.Glb;
                    return true;
                case "gltf":
                    format = ModelExportFormat.Gltf;
                    return true;
                case "fbx":
                    format = ModelExportFormat.Fbx;
                    return true;
                default:
                    format = ModelExportFormat.Glb;
                    return false;
            }
        }

        /// <summary>Exports one asset of an additional type. Returns the number of files written (or planned on dry run).</summary>
        public int Export(AssetHandle handle, Object obj, string outputRoot, string baseName, ISet<string> reservedPaths)
        {
            string typeFolder = handle.Type.ToString();
            bool genericName = baseName.StartsWith("Unnamed_", StringComparison.Ordinal)
                || baseName == $"{handle.TypeString}_{handle.PathID.ToString(CultureInfo.InvariantCulture)}";
            if (genericName && GetDisplayName(obj) is { Length: > 0 } displayName)
            {
                baseName = displayName;
            }

            string Reserve(string extension) => CliRunner.ResolveSafeOutputPath(outputRoot, typeFolder, baseName, extension, reservedPaths);

            switch (obj)
            {
                case Sprite sprite:
                {
                    var path = Reserve(_settings.ImageFormat.GetFileExtension());
                    if (_settings.DryRun) return Planned(handle, path);
                    using var image = sprite.GetImage();
                    if (image == null)
                    {
                        RecordSkip(handle, baseName, "UnreadableSprite", "Sprite texture could not be decoded or resolved.");
                        return 0;
                    }
                    return Write(path, stream => image.WriteToStream(stream, _settings.ImageFormat));
                }

                case AudioClip clip:
                    return ExportAudio(handle, clip, baseName, Reserve);

                case Font font:
                {
                    if (font.m_FontData == null || font.m_FontData.Length == 0)
                    {
                        RecordSkip(handle, baseName, "EmptyAsset", "Font has no embedded font data.");
                        return 0;
                    }
                    bool isOpenType = font.m_FontData.Length >= 4 && font.m_FontData.AsSpan(0, 4).SequenceEqual("OTTO"u8);
                    var path = Reserve(isOpenType ? ".otf" : ".ttf");
                    return _settings.DryRun ? Planned(handle, path) : WriteBytes(path, font.m_FontData);
                }

                case Shader shader:
                {
                    var path = Reserve(".shader");
                    return _settings.DryRun ? Planned(handle, path) : WriteText(path, shader.Convert());
                }

                case MonoBehaviour behaviour:
                    return ExportMonoBehaviour(handle, behaviour, baseName, Reserve);

                case VideoClip video:
                {
                    if (video.m_ExternalResources.m_Size <= 0)
                    {
                        var extSrc = video.m_ExternalResources?.m_Source ?? string.Empty;
                        RecordSkip(
                            handle,
                            baseName,
                            "MissingStreamedResource",
                            string.IsNullOrEmpty(extSrc)
                                ? "VideoClip has no embedded or streamed media data."
                                : $"VideoClip external resource is empty or missing: {extSrc}",
                            fieldOrResource: extSrc,
                            missingDependency: string.IsNullOrEmpty(extSrc) ? null : extSrc);
                        return 0;
                    }
                    var path = Reserve(SafeExtension(Path.GetExtension(video.m_OriginalPath), ".mp4"));
                    if (_settings.DryRun) return Planned(handle, path);
                    return Write(path, _ => { }, () => video.m_VideoData.WriteData(path));
                }

                case AnimationClip animationClip:
                {
                    var path = Reserve(".anim");
                    if (_settings.DryRun) return Planned(handle, path);
                    EnsureAnimationMaps();
                    AssetExportHelper.ExportAnimationClipToFile(animationClip, path, _bonePathHash!, _morphChannelNames!);
                    _stdout.WriteLine($"Exported: {path}");
                    return 1;
                }

                case Animator animator:
                {
                    var path = Reserve(ModelExporter.GetModelFileExtension(_settings.ModelFormat));
                    if (_settings.DryRun) return Planned(handle, path);
                    var convert = new ModelConverter(animator, _settings.ImageFormat);
                    EnsureParentDirectory(path);
                    ModelExporter.ExportModel(path, convert, _settings.ModelFormat,
                        skins: true, animation: false, blendShape: true, castToBone: false,
                        boneSize: 10f, scaleFactor: 1f, versionIndex: 3, isAscii: false);
                    _stdout.WriteLine($"Exported: {path}");
                    return 1;
                }

                default:
                    return 0;
            }
        }

        // Shaders keep their name in the parsed form; components are named after their GameObject (or script class).
        private static string? GetDisplayName(Object obj)
        {
            switch (obj)
            {
                case Shader shader:
                    return shader.m_ParsedForm?.m_Name;
                case MonoBehaviour behaviour:
                    if (behaviour.m_GameObject.TryGet(out var owner) && !string.IsNullOrEmpty(owner.m_Name))
                    {
                        return owner.m_Name;
                    }
                    return behaviour.m_Script.TryGet(out var script) ? script.m_ClassName : null;
                case Component component:
                    return component.m_GameObject.TryGet(out var gameObject) ? gameObject.m_Name : null;
                default:
                    return null;
            }
        }

        private int ExportAudio(AssetHandle handle, AudioClip clip, string baseName, Func<string, string> reserve)
        {
            if (_settings.DryRun)
            {
                return Planned(handle, reserve(".wav"));
            }

            var data = clip.m_AudioData.GetData();
            if (data == null || data.Length == 0)
            {
                var resSrc = clip.m_Source ?? string.Empty;
                RecordSkip(
                    handle,
                    baseName,
                    string.IsNullOrEmpty(resSrc) ? "EmptyAsset" : "MissingStreamedResource",
                    string.IsNullOrEmpty(resSrc)
                        ? "AudioClip contains no audio payload."
                        : $"AudioClip external resource could not be loaded: {resSrc}",
                    fieldOrResource: resSrc,
                    missingDependency: string.IsNullOrEmpty(resSrc) ? null : resSrc);
                return 0;
            }

            var converter = new AudioClipConverter(clip);
            bool isFsb5 = data.Length >= 4 && data.AsSpan(0, 4).SequenceEqual("FSB5"u8);
            var wav = converter.IsSupport || isFsb5 ? UnityAudioDecoder.ConvertToWav(data, clip.m_SubsoundIndex) : null;
            if (wav != null && wav.Length > 0)
            {
                return WriteBytes(reserve(".wav"), wav);
            }

            var ogg = UnityAudioDecoder.ConvertToOgg(data, clip.m_SubsoundIndex);
            if (ogg != null && ogg.Length > 0)
            {
                return WriteBytes(reserve(".ogg"), ogg);
            }

            return WriteBytes(reserve(converter.GetExtensionName()), data);
        }

        private int ExportMonoBehaviour(AssetHandle handle, MonoBehaviour behaviour, string baseName, Func<string, string> reserve)
        {
            if (_settings.DryRun)
            {
                return Planned(handle, reserve(".json"));
            }

            var loader = GetAssemblyLoader();
            TypeTree? fromAssemblies = null;
            TypeTree? AssemblyTree()
            {
                if (fromAssemblies == null && loader != null)
                {
                    fromAssemblies = behaviour.ConvertToTypeTree(loader);
                }
                return fromAssemblies;
            }

            object? data = behaviour.ToType();
            if (data == null && AssemblyTree() is { } tree)
            {
                data = behaviour.ToType(tree);
            }

            if (data != null)
            {
                return WriteText(reserve(".json"), Newtonsoft.Json.JsonConvert.SerializeObject(data, Newtonsoft.Json.Formatting.Indented));
            }

            var dump = behaviour.Dump() ?? (AssemblyTree() is { } dumpTree ? behaviour.Dump(dumpTree) : null);
            if (dump != null)
            {
                return WriteText(reserve(".txt"), dump);
            }

            string reason = $"no TypeTree in the file{(loader == null ? " (pass --assemblies <Managed folder>)" : " and its script was not found in the assemblies")}";
            string scriptLabel = behaviour.m_Script.TryGet(out var script) && !string.IsNullOrWhiteSpace(script.m_ClassName)
                ? (string.IsNullOrWhiteSpace(script.m_AssemblyName) ? script.m_ClassName : $"{script.m_AssemblyName}:{script.m_ClassName}")
                : string.Empty;
            string missingDep = loader == null
                ? "Managed assemblies (--assemblies <Managed folder>)"
                : (!string.IsNullOrEmpty(scriptLabel) ? scriptLabel : $"MonoScript for PathID {handle.PathID}");

            RecordSkip(
                handle,
                baseName,
                loader == null ? "MissingAssembly" : "MissingTypeTree",
                $"Skipped MonoBehaviour {handle.PathID}: {reason}.",
                fieldOrResource: scriptLabel,
                missingDependency: missingDep);
            _stderr.WriteLine($"Skipped MonoBehaviour {handle.PathID}: {reason}.");
            return 0;
        }

        private AssemblyLoader? GetAssemblyLoader()
        {
            if (_assemblyLoader == null && !string.IsNullOrWhiteSpace(_settings.AssembliesDirectory))
            {
                var loader = new AssemblyLoader();
                loader.Load(_settings.AssembliesDirectory);
                _assemblyLoader = loader;
            }

            return _assemblyLoader;
        }

        // Clip curves store bone paths and blend shape names as hashes; resolve them from the loaded Transforms,
        // Avatars and Meshes the same way the GUI export does.
        private void EnsureAnimationMaps()
        {
            if (_bonePathHash != null)
            {
                return;
            }

            var handles = _assetsManager.ProjectIndex.GetHandles()
                .Where(h => h.Type is ClassIDType.GameObject or ClassIDType.Transform or ClassIDType.RectTransform or ClassIDType.Avatar or ClassIDType.Mesh)
                .ToList();
            foreach (var handle in handles)
            {
                _assetsManager.ResolveHandle(handle);
            }

            _bonePathHash = AssetExportHelper.BuildBonePathHash(_assetsManager.assetsFileList);
            _morphChannelNames = AssetExportHelper.BuildMorphChannelNames(_assetsManager.assetsFileList);
        }

        private int Planned(AssetHandle handle, string path)
        {
            _stdout.WriteLine($"[dry-run] {handle.Type} {handle.PathID} -> {path}");
            return 1;
        }

        private int WriteBytes(string path, byte[] bytes) => Write(path, stream => stream.Write(bytes));

        private int WriteText(string path, string text) => WriteBytes(path, new UTF8Encoding(false).GetBytes(text));

        private int Write(string path, Action<Stream> writeStream, Action? writeFile = null)
        {
            EnsureParentDirectory(path);
            try
            {
                if (writeFile != null)
                {
                    writeFile();
                }
                else
                {
                    using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
                    writeStream(stream);
                }
            }
            catch
            {
                TryDelete(path);
                throw;
            }

            _stdout.WriteLine($"Exported: {path}");
            return 1;
        }

        private static string SafeExtension(string? extension, string fallback)
        {
            if (string.IsNullOrEmpty(extension) || extension.Length > 8 || !extension.Skip(1).All(char.IsLetterOrDigit))
            {
                return fallback;
            }

            return extension.ToLowerInvariant();
        }

        private static void EnsureParentDirectory(string path)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
