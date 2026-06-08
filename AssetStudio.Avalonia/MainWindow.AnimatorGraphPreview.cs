using AssetStudio;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AssetStudio.Avalonia;

public partial class MainWindow
{
    private void PreviewAnimatorGraph(Object asset)
    {
        AnimatorController? controller = null;
        AnimatorOverrideController? overrideController = null;
        string header = "";

        if (asset is Animator animator)
        {
            header = $"ANIMATOR: {((animator.m_GameObject.TryGet(out var go)) ? go.m_Name : "Animator")}\n";
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
                var globalController = assetsManager.assetsFileList
                    .SelectMany(x => x.Objects)
                    .FirstOrDefault(x => x.m_PathID == animator.m_Controller.m_PathID && x is RuntimeAnimatorController);
                if (globalController is AnimatorController ac)
                {
                    controller = ac;
                }
                else if (globalController is AnimatorOverrideController aoc)
                {
                    overrideController = aoc;
                }
            }

            if (controller == null && overrideController == null)
            {
                var animName = animator.m_GameObject.TryGet(out var goObj) ? goObj.m_Name : "Animator";
                var matchingController = assetsManager.assetsFileList
                    .SelectMany(x => x.Objects)
                    .OfType<AnimatorController>()
                    .FirstOrDefault(ac => ac.m_Name.Contains(animName, StringComparison.OrdinalIgnoreCase) || 
                                          animName.Contains(ac.m_Name, StringComparison.OrdinalIgnoreCase));
                if (matchingController != null)
                {
                    controller = matchingController;
                }
                else
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

                    Avatar? avatar = null;
                    if (animator.m_Avatar.TryGet(out var av))
                    {
                        avatar = av;
                    }
                    else
                    {
                        avatar = assetsManager.assetsFileList
                            .SelectMany(x => x.Objects)
                            .FirstOrDefault(x => x.m_PathID == animator.m_Avatar.m_PathID) as Avatar;
                    }

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

                    var siblingClips = FindLikelyAnimatorClips(animator, animName, avatar).ToList();

                    if (siblingClips.Count > 0)
                    {
                        AppendGeneratedAnimatorController(fallbackSb, animName, siblingClips);
                    }
                    else
                    {
                        fallbackSb.AppendLine("No matching sibling Animation Clips found in loaded files.");
                        fallbackSb.AppendLine("Generated controller was not created because no likely clips were found.");
                    }

                    SetTextWithTruncation(TextPreviewBox, fallbackSb.ToString());
                    TextPreviewBox.IsVisible = true;
                    PreviewLabel.IsVisible = false;
                    return;
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

        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(header))
        {
            sb.AppendLine(header);
        }

        if (controller != null)
        {
            sb.AppendLine("=========================================");
            sb.AppendLine($"ANIMATOR CONTROLLER: {controller.m_Name}");
            sb.AppendLine("=========================================");
            sb.AppendLine();

            var m_Controller = controller.m_Controller;
            if (m_Controller == null)
            {
                sb.AppendLine("Animator Controller state machine constant is empty.");
            }
            else
            {
                sb.AppendLine($"Layers count: {m_Controller.m_LayerArray?.Length ?? 0}");
                sb.AppendLine();

                if (m_Controller.m_LayerArray != null)
                {
                    for (int layerIdx = 0; layerIdx < m_Controller.m_LayerArray.Length; layerIdx++)
                    {
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
                                defaultStateName = GetNameFromTOS(controller.m_TOS, ds.m_NameID);
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
                                var states = sm.m_StateConstantArray!;
                                for (int stateIdx = 0; stateIdx < states.Length; stateIdx++)
                                {
                                    var state = states[stateIdx];
                                    var stateName = GetNameFromTOS(controller.m_TOS, state.m_NameID);
                                    
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
                                    bool isDefault = (stateIdx == sm.m_DefaultState);
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
                                                destName = GetNameFromTOS(controller.m_TOS, destState.m_NameID);
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
            }
        }
        else if (overrideController != null)
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
            }
            else
            {
                foreach (var clipOverride in overrideController.m_Clips)
                {
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
        }

        SetTextWithTruncation(TextPreviewBox, sb.ToString());
        TextPreviewBox.IsVisible = true;
        PreviewLabel.IsVisible = false;
    }

    private IEnumerable<AnimationClip> FindLikelyAnimatorClips(Animator animator, string animatorName, Avatar? avatar)
    {
        var keys = BuildAnimatorClipSearchKeys(animatorName, avatar?.m_Name, animator.assetsFile.originalPath);
        var animatorPath = animator.assetsFile.originalPath ?? string.Empty;

        return assetsManager.assetsFileList
            .SelectMany(x => x.Objects)
            .OfType<AnimationClip>()
            .Select(clip => new
            {
                Clip = clip,
                Score = ScoreAnimatorClipMatch(clip, keys, animatorPath)
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Clip.m_Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Clip);
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

    private string GetNameFromTOS(KeyValuePair<uint, string>[]? tos, uint hash)
    {
        if (tos != null)
        {
            foreach (var kv in tos)
            {
                if (kv.Key == hash) return kv.Value;
            }
        }
        return $"Hash_{hash}";
    }

    private string GetClipName(AnimatorController controller, uint clipID)
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
