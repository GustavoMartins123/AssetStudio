using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using AssetStudio;

namespace AssetStudio.Avalonia.Services
{
    public sealed class AnimationClipVector3
    {
        public float X { get; init; }
        public float Y { get; init; }
        public float Z { get; init; }
    }

    public sealed class AnimationClipBounds
    {
        public AnimationClipVector3 Center { get; init; } = new();
        public AnimationClipVector3 Extent { get; init; } = new();
    }

    public sealed class AnimationClipEventMetadata
    {
        public float Time { get; init; }
        public string FunctionName { get; init; } = string.Empty;
        public string StringParameter { get; init; } = string.Empty;
        public float FloatParameter { get; init; }
        public int IntParameter { get; init; }
        public long ObjectReferencePathId { get; init; }
        public int MessageOptions { get; init; }
    }

    public sealed class AnimationClipCurveStatistics
    {
        // Legacy curves
        public int RotationCurves { get; init; }
        public int CompressedRotationCurves { get; init; }
        public int EulerCurves { get; init; }
        public int PositionCurves { get; init; }
        public int ScaleCurves { get; init; }
        public int FloatCurves { get; init; }
        public int PptrCurves { get; init; }

        // Modern MuscleClip & GenericBinding counts
        public int StreamedCurves { get; init; }
        public int DenseCurves { get; init; }
        public int DenseFrames { get; init; }
        public int ConstantCurves { get; init; }
        public int TotalGenericBindings { get; init; }
        public int TransformPositionBindings { get; init; }
        public int TransformRotationBindings { get; init; }
        public int TransformScaleBindings { get; init; }
        public int TransformEulerBindings { get; init; }
        public int BlendShapeBindings { get; init; }
        public int PptrBindings { get; init; }
        public int HumanoidMuscleBindings { get; init; }
        public int OtherFloatBindings { get; init; }
    }

    public sealed class AnimationClipMetadataDocument
    {
        public string Name { get; init; } = string.Empty;
        public long PathId { get; init; }
        public string SourceFile { get; init; } = string.Empty;
        public string UnityVersion { get; init; } = string.Empty;
        public bool IsLegacy { get; init; }
        public string AnimationType { get; init; } = "Generic";
        public bool IsCompressed { get; init; }
        public bool UseHighQualityCurve { get; init; }
        public float SampleRate { get; init; }
        public float StartTime { get; init; }
        public float StopTime { get; init; }
        public float DurationSeconds { get; init; }
        public int EstimatedFrameCount { get; init; }
        public string WrapMode { get; init; } = "Default";
        public bool IsLooping { get; init; }
        public bool LoopBlend { get; init; }
        public bool LoopBlendOrientation { get; init; }
        public bool LoopBlendPositionY { get; init; }
        public bool LoopBlendPositionXZ { get; init; }
        public bool KeepOriginalOrientation { get; init; }
        public bool KeepOriginalPositionY { get; init; }
        public bool KeepOriginalPositionXZ { get; init; }
        public bool HeightFromFeet { get; init; }
        public bool Mirror { get; init; }
        public float CycleOffset { get; init; }
        public bool HasGenericRootTransform { get; init; }
        public bool HasMotionFloatCurves { get; init; }
        public AnimationClipVector3 AverageSpeed { get; init; } = new();
        public float AverageAngularSpeed { get; init; }
        public AnimationClipBounds? Bounds { get; init; }
        public IReadOnlyList<AnimationClipEventMetadata> Events { get; init; } = Array.Empty<AnimationClipEventMetadata>();
        public AnimationClipCurveStatistics Curves { get; init; } = new();
        public IReadOnlyList<string> AnimatedPaths { get; init; } = Array.Empty<string>();
    }

    public sealed class AnimationClipMetadataBatchExportResult
    {
        public string OutputDirectory { get; init; } = string.Empty;
        public int ExportedCount { get; init; }
        public int SkippedCount { get; init; }
        public IReadOnlyList<string> ExportedFiles { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
    }

    public static class AnimationClipMetadataService
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private static readonly HashSet<string> WindowsReservedNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        public static AnimationClipMetadataDocument ExtractMetadata(
            AnimationClip? clip,
            IReadOnlyDictionary<uint, string>? bonePathLookup = null)
        {
            if (clip == null)
            {
                return new AnimationClipMetadataDocument
                {
                    Name = "(Null AnimationClip)",
                    AnimationType = "Generic",
                    WrapMode = "Default"
                };
            }

            var animatedPaths = new HashSet<string>(StringComparer.Ordinal);
            var muscleClip = clip.m_MuscleClip;
            var innerClip = muscleClip?.m_Clip;

            // Resolve bindings (modern m_ClipBindingConstant or legacy ValueArrayConstant conversion)
            GenericBinding[]? genericBindings = clip.m_ClipBindingConstant?.genericBindings;
            if ((genericBindings == null || genericBindings.Length == 0) && innerClip?.m_Binding != null)
            {
                try
                {
                    genericBindings = innerClip.ConvertValueArrayToGenericBinding()?.genericBindings;
                }
                catch
                {
                    genericBindings = null;
                }
            }

            int totalGenericBindings = 0;
            int transformPosBindings = 0;
            int transformRotBindings = 0;
            int transformScaleBindings = 0;
            int transformEulerBindings = 0;
            int blendShapeBindings = 0;
            int pptrBindings = 0;
            int humanoidMuscleBindings = 0;
            int otherFloatBindings = 0;

            if (genericBindings != null)
            {
                for (int i = 0; i < genericBindings.Length; i++)
                {
                    var b = genericBindings[i];
                    if (b == null)
                    {
                        continue;
                    }

                    totalGenericBindings++;

                    if (b.path != 0)
                    {
                        if (bonePathLookup != null && bonePathLookup.TryGetValue(b.path, out var resolvedPath) && !string.IsNullOrWhiteSpace(resolvedPath))
                        {
                            animatedPaths.Add(resolvedPath);
                        }
                        else
                        {
                            animatedPaths.Add($"path_{b.path}");
                        }
                    }

                    if (b.isPPtrCurve == 1)
                    {
                        pptrBindings++;
                    }
                    else if (b.typeID == ClassIDType.Transform)
                    {
                        switch (b.attribute)
                        {
                            case 1:
                                transformPosBindings++;
                                break;
                            case 2:
                                transformRotBindings++;
                                break;
                            case 3:
                                transformScaleBindings++;
                                break;
                            case 4:
                                transformEulerBindings++;
                                break;
                            default:
                                otherFloatBindings++;
                                break;
                        }
                    }
                    else if (b.typeID == ClassIDType.SkinnedMeshRenderer && b.customType == 20)
                    {
                        blendShapeBindings++;
                    }
                    else if (b.typeID == ClassIDType.Animator && (b.path == 0 || b.customType == 8))
                    {
                        humanoidMuscleBindings++;
                    }
                    else
                    {
                        otherFloatBindings++;
                    }
                }
            }

            // Collect legacy animated paths & compute legacy keyframe time span
            bool hasLegacyTime = false;
            float legacyMinTime = float.PositiveInfinity;
            float legacyMaxTime = 0f;

            void RecordTime(float t)
            {
                if (!float.IsFinite(t))
                {
                    return;
                }

                if (!hasLegacyTime)
                {
                    hasLegacyTime = true;
                    legacyMinTime = t;
                    legacyMaxTime = t;
                }
                else
                {
                    if (t < legacyMinTime) legacyMinTime = t;
                    if (t > legacyMaxTime) legacyMaxTime = t;
                }
            }

            void RecordLegacyPath(string? path)
            {
                if (!string.IsNullOrWhiteSpace(path))
                {
                    animatedPaths.Add(path);
                }
            }

            int rotCurveCount = clip.m_RotationCurves?.Length ?? 0;
            if (clip.m_RotationCurves != null)
            {
                foreach (var c in clip.m_RotationCurves)
                {
                    if (c == null) continue;
                    RecordLegacyPath(c.path);
                    if (c.curve?.m_Curve == null) continue;
                    foreach (var kf in c.curve.m_Curve)
                    {
                        if (kf != null) RecordTime(kf.time);
                    }
                }
            }

            int compRotCurveCount = clip.m_CompressedRotationCurves?.Length ?? 0;
            if (clip.m_CompressedRotationCurves != null)
            {
                foreach (var c in clip.m_CompressedRotationCurves)
                {
                    if (c == null) continue;
                    RecordLegacyPath(c.m_Path);
                    if (c.m_Times != null && c.m_Times.m_NumItems > 0)
                    {
                        try
                        {
                            var times = c.m_Times.UnpackInts();
                            int accum = 0;
                            for (int i = 0; i < times.Length; i++)
                            {
                                accum += times[i];
                                RecordTime(accum * 0.01f);
                            }
                        }
                        catch
                        {
                            // Tolerate malformed packed time vectors cleanly.
                        }
                    }
                }
            }

            int eulerCurveCount = clip.m_EulerCurves?.Length ?? 0;
            if (clip.m_EulerCurves != null)
            {
                foreach (var c in clip.m_EulerCurves)
                {
                    if (c == null) continue;
                    RecordLegacyPath(c.path);
                    if (c.curve?.m_Curve == null) continue;
                    foreach (var kf in c.curve.m_Curve)
                    {
                        if (kf != null) RecordTime(kf.time);
                    }
                }
            }

            int posCurveCount = clip.m_PositionCurves?.Length ?? 0;
            if (clip.m_PositionCurves != null)
            {
                foreach (var c in clip.m_PositionCurves)
                {
                    if (c == null) continue;
                    RecordLegacyPath(c.path);
                    if (c.curve?.m_Curve == null) continue;
                    foreach (var kf in c.curve.m_Curve)
                    {
                        if (kf != null) RecordTime(kf.time);
                    }
                }
            }

            int scaleCurveCount = clip.m_ScaleCurves?.Length ?? 0;
            if (clip.m_ScaleCurves != null)
            {
                foreach (var c in clip.m_ScaleCurves)
                {
                    if (c == null) continue;
                    RecordLegacyPath(c.path);
                    if (c.curve?.m_Curve == null) continue;
                    foreach (var kf in c.curve.m_Curve)
                    {
                        if (kf != null) RecordTime(kf.time);
                    }
                }
            }

            int floatCurveCount = clip.m_FloatCurves?.Length ?? 0;
            if (clip.m_FloatCurves != null)
            {
                foreach (var c in clip.m_FloatCurves)
                {
                    if (c == null) continue;
                    RecordLegacyPath(c.path);
                    if (c.curve?.m_Curve == null) continue;
                    foreach (var kf in c.curve.m_Curve)
                    {
                        if (kf != null) RecordTime(kf.time);
                    }
                }
            }

            int pptrCurveCount = clip.m_PPtrCurves?.Length ?? 0;
            if (clip.m_PPtrCurves != null)
            {
                foreach (var c in clip.m_PPtrCurves)
                {
                    if (c == null) continue;
                    RecordLegacyPath(c.path);
                    if (c.curve == null) continue;
                    foreach (var kf in c.curve)
                    {
                        if (kf != null) RecordTime(kf.time);
                    }
                }
            }

            // Events
            var events = new List<AnimationClipEventMetadata>();
            if (clip.m_Events != null)
            {
                for (int i = 0; i < clip.m_Events.Length; i++)
                {
                    var evt = clip.m_Events[i];
                    if (evt == null)
                    {
                        continue;
                    }

                    var evtTime = SanitizeFloat(evt.time);
                    if (!hasLegacyTime && muscleClip == null)
                    {
                        RecordTime(evtTime);
                    }

                    events.Add(new AnimationClipEventMetadata
                    {
                        Time = evtTime,
                        FunctionName = evt.functionName ?? string.Empty,
                        StringParameter = evt.data ?? string.Empty,
                        FloatParameter = SanitizeFloat(evt.floatParameter),
                        IntParameter = evt.intParameter,
                        ObjectReferencePathId = evt.objectReferenceParameter?.m_PathID ?? 0,
                        MessageOptions = evt.messageOptions
                    });
                }
            }

            // Timing & frame count
            float sampleRate = SanitizeFloat(clip.m_SampleRate);
            if (sampleRate < 0f)
            {
                sampleRate = 0f;
            }

            float startTime;
            float stopTime;
            if (muscleClip != null && float.IsFinite(muscleClip.m_StartTime) && float.IsFinite(muscleClip.m_StopTime))
            {
                startTime = muscleClip.m_StartTime;
                stopTime = muscleClip.m_StopTime;
                if (stopTime <= startTime && hasLegacyTime && legacyMaxTime > 0f)
                {
                    startTime = Math.Min(0f, legacyMinTime);
                    stopTime = legacyMaxTime;
                }
            }
            else if (hasLegacyTime)
            {
                startTime = legacyMinTime < 0f ? legacyMinTime : 0f;
                stopTime = legacyMaxTime;
            }
            else
            {
                startTime = 0f;
                stopTime = 0f;
            }

            float durationSeconds = stopTime > startTime ? stopTime - startTime : 0f;
            int denseFrames = Math.Max(0, innerClip?.m_DenseClip?.m_FrameCount ?? 0);
            int estimatedFrameCount;
            if (sampleRate > 0f && durationSeconds > 0f)
            {
                estimatedFrameCount = Math.Max(1, (int)Math.Round(durationSeconds * sampleRate, MidpointRounding.AwayFromZero));
            }
            else
            {
                estimatedFrameCount = denseFrames;
            }

            // Animation type classification
            string animationType;
            if (clip.m_Legacy || clip.m_AnimationType == AnimationType.Legacy)
            {
                animationType = "Legacy";
            }
            else if (clip.m_AnimationType == AnimationType.Humanoid)
            {
                animationType = "Humanoid";
            }
            else if (clip.m_AnimationType == AnimationType.Generic)
            {
                animationType = "Generic";
            }
            else
            {
                animationType = humanoidMuscleBindings > 0 ? "Humanoid" : "Generic";
            }

            string wrapMode = clip.m_WrapMode switch
            {
                0 => "Default",
                1 => "Once",
                2 => "Loop",
                4 => "PingPong",
                8 => "ClampForever",
                _ => $"Unknown({clip.m_WrapMode})"
            };

            bool isLooping = muscleClip?.m_LoopTime == true || clip.m_WrapMode == 2;

            var curveStats = new AnimationClipCurveStatistics
            {
                RotationCurves = rotCurveCount,
                CompressedRotationCurves = compRotCurveCount,
                EulerCurves = eulerCurveCount,
                PositionCurves = posCurveCount,
                ScaleCurves = scaleCurveCount,
                FloatCurves = floatCurveCount,
                PptrCurves = pptrCurveCount,
                StreamedCurves = checked((int)Math.Min(int.MaxValue, innerClip?.m_StreamedClip?.curveCount ?? 0u)),
                DenseCurves = checked((int)Math.Min(int.MaxValue, innerClip?.m_DenseClip?.m_CurveCount ?? 0u)),
                DenseFrames = denseFrames,
                ConstantCurves = innerClip?.m_ConstantClip?.data?.Length ?? 0,
                TotalGenericBindings = totalGenericBindings,
                TransformPositionBindings = transformPosBindings,
                TransformRotationBindings = transformRotBindings,
                TransformScaleBindings = transformScaleBindings,
                TransformEulerBindings = transformEulerBindings,
                BlendShapeBindings = blendShapeBindings,
                PptrBindings = pptrBindings,
                HumanoidMuscleBindings = humanoidMuscleBindings,
                OtherFloatBindings = otherFloatBindings
            };

            AnimationClipBounds? bounds = null;
            if (clip.m_Bounds != null)
            {
                bounds = new AnimationClipBounds
                {
                    Center = ToVector3(clip.m_Bounds.m_Center),
                    Extent = ToVector3(clip.m_Bounds.m_Extent)
                };
            }

            var avgSpeed = muscleClip != null
                ? ToVector3(muscleClip.m_AverageSpeed)
                : new AnimationClipVector3();

            var sortedPaths = animatedPaths.ToList();
            sortedPaths.Sort(StringComparer.Ordinal);

            return new AnimationClipMetadataDocument
            {
                Name = string.IsNullOrWhiteSpace(clip.m_Name) ? $"AnimationClip_{clip.m_PathID}" : clip.m_Name,
                PathId = clip.m_PathID,
                SourceFile = clip.assetsFile?.fileName ?? string.Empty,
                UnityVersion = ResolveUnityVersion(clip),
                IsLegacy = clip.m_Legacy || clip.m_AnimationType == AnimationType.Legacy,
                AnimationType = animationType,
                IsCompressed = clip.m_Compressed,
                UseHighQualityCurve = clip.m_UseHighQualityCurve,
                SampleRate = sampleRate,
                StartTime = SanitizeFloat(startTime),
                StopTime = SanitizeFloat(stopTime),
                DurationSeconds = SanitizeFloat(durationSeconds),
                EstimatedFrameCount = estimatedFrameCount,
                WrapMode = wrapMode,
                IsLooping = isLooping,
                LoopBlend = muscleClip?.m_LoopBlend ?? false,
                LoopBlendOrientation = muscleClip?.m_LoopBlendOrientation ?? false,
                LoopBlendPositionY = muscleClip?.m_LoopBlendPositionY ?? false,
                LoopBlendPositionXZ = muscleClip?.m_LoopBlendPositionXZ ?? false,
                KeepOriginalOrientation = muscleClip?.m_KeepOriginalOrientation ?? false,
                KeepOriginalPositionY = muscleClip?.m_KeepOriginalPositionY ?? false,
                KeepOriginalPositionXZ = muscleClip?.m_KeepOriginalPositionXZ ?? false,
                HeightFromFeet = muscleClip?.m_HeightFromFeet ?? false,
                Mirror = muscleClip?.m_Mirror ?? false,
                CycleOffset = SanitizeFloat(muscleClip?.m_CycleOffset ?? 0f),
                HasGenericRootTransform = clip.m_HasGenericRootTransform,
                HasMotionFloatCurves = clip.m_HasMotionFloatCurves,
                AverageSpeed = avgSpeed,
                AverageAngularSpeed = SanitizeFloat(muscleClip?.m_AverageAngularSpeed ?? 0f),
                Bounds = bounds,
                Events = events,
                Curves = curveStats,
                AnimatedPaths = sortedPaths
            };
        }

        public static string SerializeToJson(AnimationClipMetadataDocument doc)
        {
            ArgumentNullException.ThrowIfNull(doc);
            return JsonSerializer.Serialize(doc, JsonOptions);
        }

        public static void ExportToJsonFile(AnimationClipMetadataDocument doc, string outputPath)
        {
            ArgumentNullException.ThrowIfNull(doc);
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                throw new ArgumentException("Output path cannot be empty.", nameof(outputPath));
            }

            var json = SerializeToJson(doc);
            WriteUtf8FileAtomic(outputPath, json);
        }

        public static AnimationClipMetadataBatchExportResult ExportBatchMetadataJson(
            IEnumerable<AnimationClip> clips,
            string outputDirectory,
            IReadOnlyDictionary<uint, string>? bonePathLookup = null,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(clips);
            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                throw new ArgumentException("Output directory cannot be empty.", nameof(outputDirectory));
            }

            var normalizedRoot = Path.GetFullPath(outputDirectory);
            Directory.CreateDirectory(normalizedRoot);
            var rootPrefix = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
                ? normalizedRoot
                : normalizedRoot + Path.DirectorySeparatorChar;
            var pathComparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

            var exportedFiles = new List<string>();
            var errors = new List<string>();
            var usedFileNames = new HashSet<string>(
                OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
            int skippedCount = 0;

            foreach (var clip in clips)
            {
                ct.ThrowIfCancellationRequested();
                if (clip == null)
                {
                    skippedCount++;
                    continue;
                }

                try
                {
                    var doc = ExtractMetadata(clip, bonePathLookup);
                    var baseStem = SanitizeFileStem(doc.Name);
                    string candidateFileName = baseStem + ".anim.json";

                    if (!usedFileNames.Add(candidateFileName))
                    {
                        var pathIdStem = $"{baseStem}_{clip.m_PathID.ToString(CultureInfo.InvariantCulture)}";
                        candidateFileName = pathIdStem + ".anim.json";
                        int suffix = 2;
                        while (!usedFileNames.Add(candidateFileName))
                        {
                            candidateFileName = $"{pathIdStem}_{suffix.ToString(CultureInfo.InvariantCulture)}.anim.json";
                            suffix++;
                        }
                    }

                    var fullTargetPath = Path.GetFullPath(Path.Combine(normalizedRoot, candidateFileName));
                    if (!fullTargetPath.StartsWith(rootPrefix, pathComparison))
                    {
                        throw new InvalidOperationException($"Resolved export path escaped output directory: '{candidateFileName}'.");
                    }

                    ExportToJsonFile(doc, fullTargetPath);
                    exportedFiles.Add(fullTargetPath);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    errors.Add($"{clip.m_Name ?? $"PathID_{clip.m_PathID}"}: {ex.Message}");
                }
            }

            return new AnimationClipMetadataBatchExportResult
            {
                OutputDirectory = normalizedRoot,
                ExportedCount = exportedFiles.Count,
                SkippedCount = skippedCount,
                ExportedFiles = exportedFiles,
                Errors = errors
            };
        }

        public static string SanitizeFileStem(string? rawName)
        {
            if (string.IsNullOrWhiteSpace(rawName))
            {
                return "unnamed_clip";
            }

            var invalidChars = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(rawName.Length);
            for (int i = 0; i < rawName.Length; i++)
            {
                char ch = rawName[i];
                if (ch < 32
                    || ch == '<' || ch == '>' || ch == ':'
                    || ch == '"' || ch == '/' || ch == '\\'
                    || ch == '|' || ch == '?' || ch == '*'
                    || Array.IndexOf(invalidChars, ch) >= 0)
                {
                    sb.Append('_');
                }
                else
                {
                    sb.Append(ch);
                }
            }

            var cleaned = sb.ToString();
            while (cleaned.Contains("..", StringComparison.Ordinal))
            {
                cleaned = cleaned.Replace("..", "_", StringComparison.Ordinal);
            }

            cleaned = cleaned.Trim().Trim('.');
            if (string.IsNullOrWhiteSpace(cleaned))
            {
                return "unnamed_clip";
            }

            var dotIndex = cleaned.IndexOf('.');
            var deviceBase = dotIndex >= 0 ? cleaned[..dotIndex] : cleaned;
            if (WindowsReservedNames.Contains(deviceBase))
            {
                cleaned = "_" + cleaned;
            }

            if (cleaned.Length > 140)
            {
                cleaned = cleaned[..140].TrimEnd(' ', '.');
                if (string.IsNullOrWhiteSpace(cleaned))
                {
                    return "unnamed_clip";
                }
            }

            return cleaned;
        }

        private static string ResolveUnityVersion(AnimationClip clip)
        {
            if (!string.IsNullOrWhiteSpace(clip.assetsFile?.unityVersion))
            {
                return clip.assetsFile.unityVersion;
            }

            if (clip.version != null && clip.version.Length > 0)
            {
                return string.Join(".", clip.version.Select(v => v.ToString(CultureInfo.InvariantCulture)));
            }

            return string.Empty;
        }

        private static AnimationClipVector3 ToVector3(Vector3 v) => new()
        {
            X = SanitizeFloat(v.X),
            Y = SanitizeFloat(v.Y),
            Z = SanitizeFloat(v.Z)
        };

        private static float SanitizeFloat(float value)
        {
            return float.IsFinite(value) ? value : 0f;
        }

        private static void WriteUtf8FileAtomic(string outputPath, string content)
        {
            var fullPath = Path.GetFullPath(outputPath);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var tempPath = fullPath + ".tmp." + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(tempPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                File.Move(tempPath, fullPath, overwrite: true);
            }
            catch
            {
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch
                {
                }

                throw;
            }
        }
    }
}
