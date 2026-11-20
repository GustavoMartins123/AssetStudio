using AssetStudio;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AssetStudio.Avalonia;

public partial class MainWindow
{
    private void PreviewAnimatorGraph(Object asset)
    {
        var token = BeginAsyncPreviewRequest(out var requestId, out var previewVersion);
        TextPreviewBox.Text = "Loading animator preview...";
        TextPreviewBox.IsVisible = true;
        PreviewLabel.IsVisible = false;

        _ = Task.Run(() =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                var text = BuildAnimatorGraphPreviewText(asset, token);
                token.ThrowIfCancellationRequested();
                var truncated = TruncatePreviewText(text);

                Dispatcher.UIThread.Post(() =>
                {
                    if (!IsPreviewRequestCurrent(requestId, previewVersion, token))
                    {
                        return;
                    }

                    TextPreviewBox.Text = truncated;
                    TextPreviewBox.IsVisible = true;
                    PreviewLabel.IsVisible = false;
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                logger.Log(LoggerEvent.Error, $"Animator preview failed: {ex}");
                var errorText = TruncatePreviewText($"Failed to load animator preview:{Environment.NewLine}{ex.Message}");
                Dispatcher.UIThread.Post(() =>
                {
                    if (!IsPreviewRequestCurrent(requestId, previewVersion, token))
                    {
                        return;
                    }

                    TextPreviewBox.Text = errorText;
                    TextPreviewBox.IsVisible = true;
                    PreviewLabel.IsVisible = false;
                    StatusStripUpdate($"Animator preview error: {ex.Message}");
                });
            }
        }, token);
    }

    private string BuildAnimatorGraphPreviewText(Object asset, CancellationToken cancellationToken)
    {
        AnimatorController? controller = null;
        AnimatorOverrideController? overrideController = null;
        string header = string.Empty;

        if (asset is Animator animator)
        {
            var animName = animator.m_GameObject.TryGet(out var go) ? go.m_Name : "Animator";
            header = $"ANIMATOR: {animName}\n";

            if (animator.m_Controller.TryGet(out var rac))
            {
                if (rac is AnimatorController ac)
                {
                    controller = ac;
                }
                else if (rac is AnimatorOverrideController aoc)
                {
                    overrideController = aoc;
                }
            }
            else
            {
                // Single thread-safe snapshot pass over all loaded files/objects:
                // resolves controller by PathID, matching controller by name, fallback Avatar, and candidate AnimationClips in <= 1 scan.
                bool hasDirectAvatar = animator.m_Avatar.TryGet(out var directAvatar);
                Avatar? resolvedAvatar = hasDirectAvatar ? directAvatar : null;
                long controllerPathId = animator.m_Controller.m_PathID;
                long avatarPathId = animator.m_Avatar.m_PathID;

                RuntimeAnimatorController? controllerByPathId = null;
                AnimatorController? matchingNameController = null;
                Avatar? avatarByPathId = null;
                List<AnimationClip>? candidateClips = null;

                List<SerializedFile> filesSnapshot;
                lock (assetsManager.loadLock)
                {
                    filesSnapshot = assetsManager.assetsFileList.ToList();
                }

                foreach (var file in filesSnapshot)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (file == null)
                    {
                        continue;
                    }

                    Object[] objectsSnapshot = SnapshotFileObjects(file);

                    for (int i = 0; i < objectsSnapshot.Length; i++)
                    {
                        var obj = objectsSnapshot[i];
                        if (obj == null)
                        {
                            continue;
                        }

                        if (obj.m_PathID == controllerPathId && obj is RuntimeAnimatorController globalController)
                        {
                            controllerByPathId = globalController;
                            break;
                        }

                        if (matchingNameController == null
                            && obj is AnimatorController acCandidate
                            && !string.IsNullOrEmpty(acCandidate.m_Name)
                            && (acCandidate.m_Name.Contains(animName, StringComparison.OrdinalIgnoreCase)
                                || animName.Contains(acCandidate.m_Name, StringComparison.OrdinalIgnoreCase)))
                        {
                            matchingNameController = acCandidate;
                            candidateClips = null;
                        }

                        if (matchingNameController == null)
                        {
                            if (!hasDirectAvatar && avatarByPathId == null && obj.m_PathID == avatarPathId && obj is Avatar avCandidate)
                            {
                                avatarByPathId = avCandidate;
                            }
                            else if (obj is AnimationClip clipCandidate)
                            {
                                candidateClips ??= new List<AnimationClip>();
                                candidateClips.Add(clipCandidate);
                            }
                        }
                    }

                    if (controllerByPathId != null)
                    {
                        break;
                    }
                }

                if (controllerByPathId is AnimatorController acById)
                {
                    controller = acById;
                }
                else if (controllerByPathId is AnimatorOverrideController aocById)
                {
                    overrideController = aocById;
                }
                else if (matchingNameController != null)
                {
                    controller = matchingNameController;
                }
                else
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    resolvedAvatar ??= avatarByPathId;
                    return BuildFallbackAnimatorReport(animator, header, animName, resolvedAvatar, candidateClips, cancellationToken);
                }
            }
        }
        else if (asset is AnimatorController ac)
        {
            controller = ac;
        }
        else if (asset is AnimatorOverrideController aoc)
        {
            overrideController = aoc;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(header))
        {
            sb.AppendLine(header);
        }

        if (controller != null)
        {
            AppendAnimatorControllerDetails(sb, controller, cancellationToken);
        }
        else if (overrideController != null)
        {
            AppendAnimatorOverrideControllerDetails(sb, overrideController, cancellationToken);
        }

        return sb.ToString();
    }

    private static Object[] SnapshotFileObjects(SerializedFile file)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                lock (file)
                {
                    return file.Objects.ToArray();
                }
            }
            catch (InvalidOperationException)
            {
            }
            catch (ArgumentException)
            {
            }
        }

        lock (file)
        {
            var list = file.Objects;
            int count = list.Count;
            var snapshot = new Object[count];
            for (int i = 0; i < count; i++)
            {
                try
                {
                    snapshot[i] = list[i];
                }
                catch (ArgumentOutOfRangeException)
                {
                    break;
                }
            }
            return snapshot;
        }
    }

    private static string BuildFallbackAnimatorReport(
        Animator animator,
        string header,
        string animName,
        Avatar? avatar,
        List<AnimationClip>? candidateClips,
        CancellationToken cancellationToken)
    {
        var fallbackSb = new StringBuilder();
        fallbackSb.AppendLine(header);
        fallbackSb.AppendLine("=========================================");
        fallbackSb.AppendLine("ANIMATOR COMPONENT (No Controller Referenced)");
        fallbackSb.AppendLine("=========================================");
        fallbackSb.AppendLine();
        fallbackSb.AppendLine("Properties:");
        fallbackSb.AppendLine($"  - Enabled: True");
        fallbackSb.AppendLine($"  - Apply Root Motion: True");
        fallbackSb.AppendLine($"  - Has Transform Hierarchy: {animator.m_HasTransformHierarchy}");
        fallbackSb.AppendLine();

        if (avatar != null)
        {
            fallbackSb.AppendLine($"Referenced Avatar: {avatar.m_Name} (Size: {avatar.m_AvatarSize} bytes)");
            fallbackSb.AppendLine();
            if (avatar.m_Avatar?.m_AvatarSkeleton?.m_Node != null)
            {
                fallbackSb.AppendLine("Avatar Skeleton Nodes:");
                var skeleton = avatar.m_Avatar.m_AvatarSkeleton;
                for (int i = 0; i < skeleton.m_Node.Length; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var node = skeleton.m_Node[i];
                    string name = "Unknown";
                    if (skeleton.m_ID != null && i < skeleton.m_ID.Length)
                    {
                        name = avatar.FindBonePath(skeleton.m_ID[i]);
                        if (string.IsNullOrEmpty(name))
                        {
                            name = $"Hash_{skeleton.m_ID[i]}";
                        }
                    }
                    fallbackSb.AppendLine($"  [{i}] Node: \"{name}\" (Parent ID: {node.m_ParentId}, Axes ID: {node.m_AxesId})");
                }
                fallbackSb.AppendLine();
            }
        }
        else
        {
            fallbackSb.AppendLine("Referenced Avatar: None or unresolved.");
            fallbackSb.AppendLine();
        }

        var siblingClips = RankLikelyAnimatorClips(animator, animName, avatar, candidateClips, cancellationToken);
        if (siblingClips.Count > 0)
        {
            AppendGeneratedAnimatorController(fallbackSb, animName, siblingClips);
        }
        else
        {
            fallbackSb.AppendLine("No matching sibling Animation Clips found in loaded files.");
            fallbackSb.AppendLine("Generated controller was not created because no likely clips were found.");
        }

        return fallbackSb.ToString();
    }

    private static void AppendAnimatorControllerDetails(StringBuilder sb, AnimatorController controller, CancellationToken cancellationToken)
    {
        sb.AppendLine("=========================================");
        sb.AppendLine($"ANIMATOR CONTROLLER: {controller.m_Name}");
        sb.AppendLine("=========================================");
        sb.AppendLine();

        var m_Controller = controller.m_Controller;
        if (m_Controller == null)
        {
            sb.AppendLine("Animator Controller state machine constant is empty.");
            return;
        }

        Dictionary<uint, string>? tosLookup = null;
        if (controller.m_TOS != null && controller.m_TOS.Length > 0)
        {
            tosLookup = new Dictionary<uint, string>(controller.m_TOS.Length);
            foreach (var kv in controller.m_TOS)
            {
                tosLookup.TryAdd(kv.Key, kv.Value);
            }
        }

        sb.AppendLine($"Layers count: {m_Controller.m_LayerArray?.Length ?? 0}");
        sb.AppendLine();

        if (m_Controller.m_LayerArray == null)
        {
            return;
        }

        for (int layerIdx = 0; layerIdx < m_Controller.m_LayerArray.Length; layerIdx++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var layer = m_Controller.m_LayerArray[layerIdx];
            sb.AppendLine("-----------------------------------------");
            sb.AppendLine($"Layer {layerIdx}: State Machine Index: {layer.m_StateMachineIndex}");
            sb.AppendLine("-----------------------------------------");

            if (m_Controller.m_StateMachineArray != null && layer.m_StateMachineIndex < m_Controller.m_StateMachineArray.Length)
            {
                var sm = m_Controller.m_StateMachineArray[layer.m_StateMachineIndex];

                string defaultStateName = "None";
                if (sm.m_StateConstantArray != null && sm.m_DefaultState < sm.m_StateConstantArray.Length)
                {
                    var ds = sm.m_StateConstantArray[sm.m_DefaultState];
                    defaultStateName = GetNameFromTOS(tosLookup, ds.m_NameID);
                }
                sb.AppendLine($"Default State: {defaultStateName}");
                sb.AppendLine();

                if (sm.m_StateConstantArray == null || sm.m_StateConstantArray.Length == 0)
                {
                    sb.AppendLine("  (No states found in this layer)");
                }
                else
                {
                    sb.AppendLine("States & Transitions:");
                    var states = sm.m_StateConstantArray;
                    for (int stateIdx = 0; stateIdx < states.Length; stateIdx++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var state = states[stateIdx];
                        var stateName = GetNameFromTOS(tosLookup, state.m_NameID);

                        var clips = new List<string>();
                        if (state.m_BlendTreeConstantArray != null)
                        {
                            foreach (var bt in state.m_BlendTreeConstantArray)
                            {
                                if (bt.m_NodeArray != null)
                                {
                                    foreach (var node in bt.m_NodeArray)
                                    {
                                        if (node.m_ClipID != 0xFFFFFFFF)
                                        {
                                            clips.Add(GetClipName(controller, node.m_ClipID));
                                        }
                                    }
                                }
                            }
                        }

                        string clipInfo = clips.Count > 0 ? string.Join(", ", clips) : "None";
                        bool isDefault = stateIdx == sm.m_DefaultState;
                        string prefix = isDefault ? "▶ [DEFAULT] " : "  * ";

                        sb.AppendLine($"{prefix}{stateName} (Motion: {clipInfo})");

                        if (state.m_TransitionConstantArray != null && state.m_TransitionConstantArray.Length > 0)
                        {
                            for (int transIdx = 0; transIdx < state.m_TransitionConstantArray.Length; transIdx++)
                            {
                                var trans = state.m_TransitionConstantArray[transIdx];
                                string destName = "Unknown";
                                var statesList = sm.m_StateConstantArray;
                                var selectorStates = sm.m_SelectorStateConstantArray;
                                if (statesList != null && trans.m_DestinationState < statesList.Length)
                                {
                                    var destState = statesList[trans.m_DestinationState];
                                    destName = GetNameFromTOS(tosLookup, destState.m_NameID);
                                }
                                else if (selectorStates != null && statesList != null && trans.m_DestinationState >= statesList.Length && (trans.m_DestinationState - statesList.Length) < selectorStates.Length)
                                {
                                    destName = $"SelectorState_{trans.m_DestinationState - statesList.Length}";
                                }

                                string lineChar = (transIdx == state.m_TransitionConstantArray.Length - 1) ? "└──" : "├──";
                                sb.AppendLine($"    {lineChar} transition ──> {destName}");
                            }
                        }
                        sb.AppendLine();
                    }
                }
            }
            else
            {
                sb.AppendLine("  (State machine not found or index out of range)");
            }
            sb.AppendLine();
        }
    }

    private static void AppendAnimatorOverrideControllerDetails(StringBuilder sb, AnimatorOverrideController overrideController, CancellationToken cancellationToken)
    {
        sb.AppendLine("=========================================");
        sb.AppendLine($"ANIMATOR OVERRIDE CONTROLLER: {overrideController.m_Name}");
        sb.AppendLine("=========================================");
        sb.AppendLine();

        string baseName = "None";
        if (overrideController.m_Controller.TryGet(out var baseC))
        {
            baseName = baseC.m_Name;
        }
        sb.AppendLine($"Base Controller: {baseName}");
        sb.AppendLine();

        sb.AppendLine("Animation Clip Overrides:");
        if (overrideController.m_Clips == null || overrideController.m_Clips.Length == 0)
        {
            sb.AppendLine("  (No clip overrides defined)");
            return;
        }

        foreach (var clipOverride in overrideController.m_Clips)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string origName = "None";
            if (clipOverride.m_OriginalClip.TryGet(out var origClip))
            {
                origName = origClip.m_Name;
            }
            string overrideName = "None";
            if (clipOverride.m_OverrideClip.TryGet(out var overClip))
            {
                overrideName = overClip.m_Name;
            }
            sb.AppendLine($"  * {origName} ──(overridden by)──> {overrideName}");
        }
    }

    private static List<AnimationClip> RankLikelyAnimatorClips(
        Animator animator,
        string animatorName,
        Avatar? avatar,
        List<AnimationClip>? candidateClips,
        CancellationToken cancellationToken)
    {
        if (candidateClips == null || candidateClips.Count == 0)
        {
            return new List<AnimationClip>();
        }

        var keys = BuildAnimatorClipSearchKeys(animatorName, avatar?.m_Name, animator.assetsFile.originalPath);
        var animatorPath = animator.assetsFile.originalPath ?? string.Empty;

        var scored = new List<(AnimationClip Clip, int Score)>();
        for (int i = 0; i < candidateClips.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var clip = candidateClips[i];
            int score = ScoreAnimatorClipMatch(clip, keys, animatorPath);
            if (score > 0)
            {
                scored.Add((clip, score));
            }
        }

        scored.Sort(static (left, right) =>
        {
            int byScore = right.Score.CompareTo(left.Score);
            if (byScore != 0)
            {
                return byScore;
            }

            return StringComparer.OrdinalIgnoreCase.Compare(left.Clip.m_Name, right.Clip.m_Name);
        });

        var result = new List<AnimationClip>(scored.Count);
        for (int i = 0; i < scored.Count; i++)
        {
            result.Add(scored[i].Clip);
        }

        return result;
    }

    private static List<string> BuildAnimatorClipSearchKeys(string animatorName, string? avatarName, string? originalPath)
    {
        var keys = new List<string>();

        void AddKey(string? raw)
        {
            var key = NormalizeAnimatorSearchKey(raw);
            if (key.Length >= 4 && !keys.Any(x => string.Equals(x, key, StringComparison.OrdinalIgnoreCase)))
            {
                keys.Add(key);
            }

            var trimmed = StripAnimatorNameSuffixes(key);
            if (trimmed.Length >= 4 && !keys.Any(x => string.Equals(x, trimmed, StringComparison.OrdinalIgnoreCase)))
            {
                keys.Add(trimmed);
            }
        }

        AddKey(animatorName);
        AddKey(avatarName);
        AddKey(Path.GetFileNameWithoutExtension(originalPath ?? string.Empty));

        foreach (var key in keys.ToArray())
        {
            var parts = key.Split('_', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3)
            {
                AddKey(string.Join("_", parts.Take(3)));
            }
        }

        return keys;
    }

    private static int ScoreAnimatorClipMatch(AnimationClip clip, List<string> keys, string animatorPath)
    {
        int score = 0;
        var clipName = NormalizeAnimatorSearchKey(clip.m_Name);
        var clipPath = clip.assetsFile.originalPath ?? string.Empty;

        if (!string.IsNullOrEmpty(animatorPath)
            && !string.IsNullOrEmpty(clipPath)
            && string.Equals(animatorPath, clipPath, StringComparison.OrdinalIgnoreCase))
        {
            score += 50;
        }

        foreach (var key in keys)
        {
            if (clipName.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                score += 40;
            }
            else if (clipName.StartsWith(key + "_", StringComparison.OrdinalIgnoreCase)
                || clipName.StartsWith(key + "-", StringComparison.OrdinalIgnoreCase))
            {
                score += 30;
            }
            else if (clipName.Contains(key, StringComparison.OrdinalIgnoreCase))
            {
                score += 10;
            }
        }

        return score;
    }

    private static string NormalizeAnimatorSearchKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return Path.GetFileNameWithoutExtension(value)
            .Replace("\\", "/", StringComparison.Ordinal)
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault()
            ?.Trim()
            .ToLowerInvariant() ?? string.Empty;
    }

    private static string StripAnimatorNameSuffixes(string value)
    {
        var suffixes = new[]
        {
            "_avatar", "avatar", "_skin", "_body", "_mesh", "_model", "_prefab", "_animator", "animator"
        };

        string result = value;
        bool changed;
        do
        {
            changed = false;
            foreach (var suffix in suffixes)
            {
                if (result.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && result.Length > suffix.Length)
                {
                    result = result[..^suffix.Length].TrimEnd('_', '-', ' ');
                    changed = true;
                }
            }
        } while (changed);

        return result;
    }

    private static void AppendGeneratedAnimatorController(StringBuilder sb, string animatorName, List<AnimationClip> clips)
    {
        var defaultClip = clips.FirstOrDefault(IsDefaultAnimatorClip) ?? clips.First();

        sb.AppendLine("=========================================");
        sb.AppendLine($"GENERATED ANIMATOR CONTROLLER: {animatorName}");
        sb.AppendLine("=========================================");
        sb.AppendLine("Source: inferred from matching AnimationClip assets.");
        sb.AppendLine("Parameters: Unknown (not present in loaded Animator data).");
        sb.AppendLine("Transitions: Unknown (states are listed without real conditions).");
        sb.AppendLine();
        sb.AppendLine("Layer 0: Base Layer");
        sb.AppendLine($"Default State: {defaultClip.m_Name}");
        sb.AppendLine();
        sb.AppendLine("States:");

        foreach (var clip in clips)
        {
            string prefix = ReferenceEquals(clip, defaultClip) ? "> [DEFAULT] " : "  * ";
            sb.AppendLine($"{prefix}{clip.m_Name} (Motion: {clip.m_Name}, PathID: {clip.m_PathID}, Size: {clip.byteSize} bytes)");
        }

        sb.AppendLine();
        sb.AppendLine("Matching Animation Clips:");
        foreach (var clip in clips)
        {
            var path = string.IsNullOrEmpty(clip.assetsFile.originalPath) ? "[loaded asset]" : clip.assetsFile.originalPath;
            sb.AppendLine($"  * {clip.m_Name} - {path}");
        }
    }

    private static bool IsDefaultAnimatorClip(AnimationClip clip)
    {
        var name = NormalizeAnimatorSearchKey(clip.m_Name);
        return name.Contains("idle", StringComparison.OrdinalIgnoreCase)
            || name.Contains("stand", StringComparison.OrdinalIgnoreCase)
            || name.Contains("wait", StringComparison.OrdinalIgnoreCase)
            || name.Contains("weak", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetNameFromTOS(Dictionary<uint, string>? tosLookup, uint hash)
    {
        if (tosLookup != null && tosLookup.TryGetValue(hash, out var name))
        {
            return name;
        }
        return $"Hash_{hash}";
    }

    private static string GetClipName(AnimatorController controller, uint clipID)
    {
        if (controller.m_AnimationClips != null && clipID < controller.m_AnimationClips.Length)
        {
            var pptr = controller.m_AnimationClips[clipID];
            if (pptr.TryGet(out var clip))
            {
                return clip.m_Name;
            }
        }
        return $"Clip_{clipID}";
    }
}
