using Avalonia.Threading;
using AssetStudio;
using AssetStudio.Avalonia.Services;
using AssetStudio.Avalonia.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AssetStudio.Avalonia;

internal sealed class AnimationPreviewLoadCoordinator : IDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource? _activeCts;
    private long _currentRequestId;
    private long _cancelledRequestCount;

    public long CurrentRequestId => Interlocked.Read(ref _currentRequestId);
    public long CancelledRequestCount => Interlocked.Read(ref _cancelledRequestCount);

    public CancellationToken BeginNewRequest(out long requestId)
    {
        lock (_gate)
        {
            if (_activeCts != null)
            {
                if (!_activeCts.IsCancellationRequested)
                {
                    Interlocked.Increment(ref _cancelledRequestCount);
                    try
                    {
                        _activeCts.Cancel();
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                }

                _activeCts.Dispose();
            }

            _activeCts = new CancellationTokenSource();
            requestId = Interlocked.Increment(ref _currentRequestId);
            return _activeCts.Token;
        }
    }

    public void CancelCurrent()
    {
        lock (_gate)
        {
            Interlocked.Increment(ref _currentRequestId);
            if (_activeCts != null)
            {
                if (!_activeCts.IsCancellationRequested)
                {
                    Interlocked.Increment(ref _cancelledRequestCount);
                    try
                    {
                        _activeCts.Cancel();
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                }

                _activeCts.Dispose();
                _activeCts = null;
            }
        }
    }

    public bool IsCurrent(long requestId)
    {
        return Interlocked.Read(ref _currentRequestId) == requestId;
    }

    public async Task<(bool Applied, long RequestId, T? Result)> RunLatestAsync<T>(
        Func<long, CancellationToken, Task<T>> backgroundWork,
        Action<long, T>? onCompleted = null)
    {
        var token = BeginNewRequest(out var requestId);
        try
        {
            var result = await Task.Run(() => backgroundWork(requestId, token), token).ConfigureAwait(false);
            if (token.IsCancellationRequested || !IsCurrent(requestId))
            {
                return (false, requestId, default);
            }

            onCompleted?.Invoke(requestId, result);
            return (true, requestId, result);
        }
        catch (OperationCanceledException)
        {
            return (false, requestId, default);
        }
    }

    public void Dispose()
    {
        CancelCurrent();
    }
}

internal sealed class PreparedAnimationRigData
{
    private readonly object _meshGeometryGate = new();
    private PreviewMeshGeometry? _cachedReferenceMesh;
    private float _cachedDensityPercent = -1f;

    public required string RigKey { get; init; }
    public Avatar? Avatar { get; init; }
    public required Mesh Mesh { get; init; }
    public required AvatarMeshSkeletonData Skeleton { get; init; }
    public required Dictionary<string, int> MeshBonePathToIdx { get; init; }
    public required Dictionary<uint, int> DirectBindingPathToMeshBoneIdx { get; init; }
    public required bool[] WeightedBoneMask { get; init; }
    public required bool[] DeformChainMask { get; init; }
    public global::OpenTK.Mathematics.Vector3[]? SourceVertexData { get; init; }
    public global::OpenTK.Mathematics.Vector3 SourceMeshMin { get; init; }
    public global::OpenTK.Mathematics.Vector3 SourceMeshMax { get; init; }

    public bool TryResolveBindingBone(GenericBinding binding, out int meshBoneIdx)
    {
        if (DirectBindingPathToMeshBoneIdx.TryGetValue(binding.path, out meshBoneIdx))
        {
            return meshBoneIdx >= 0;
        }

        if (Avatar != null)
        {
            var path = Avatar.FindBonePath(binding.path);
            if (!string.IsNullOrEmpty(path)
                && MeshPreviewSkeletonBuilder.TryResolvePathAlias(MeshBonePathToIdx, path, out meshBoneIdx))
            {
                return true;
            }
        }

        return Skeleton.MeshBoneHashToIdx.TryGetValue(binding.path, out meshBoneIdx);
    }

    public bool ShouldUseBinding(int meshBoneIdx)
    {
        return MeshPreviewSkeletonBuilder.ShouldUseAnimationBinding(
            meshBoneIdx,
            Skeleton.MeshBoneCount,
            DeformChainMask,
            WeightedBoneMask,
            Skeleton.MeshBoneNames);
    }

    public PreviewMeshGeometry? GetOrBuildReferenceMesh(float densityPercent)
    {
        if (SourceVertexData == null || SourceVertexData.Length == 0)
        {
            return null;
        }

        lock (_meshGeometryGate)
        {
            if (_cachedReferenceMesh != null && Math.Abs(_cachedDensityPercent - densityPercent) < 0.001f)
            {
                return _cachedReferenceMesh;
            }

            var built = PreviewMeshSimplifier.Build(
                SourceVertexData,
                Mesh.m_Indices,
                SourceMeshMin,
                SourceMeshMax,
                densityPercent);
            _cachedReferenceMesh = built;
            _cachedDensityPercent = densityPercent;
            return built;
        }
    }

    public static PreparedAnimationRigData? TryBuild(string rigKey, Avatar avatar, Mesh mesh)
    {
        mesh.EnsureProcessed();
        if (!MeshPreviewSkeletonBuilder.TryBuildAvatarMeshSkeleton(avatar, mesh, out var skeleton))
        {
            return null;
        }

        var meshBonePathToIdx = MeshPreviewSkeletonBuilder.BuildBonePathAliasMap(skeleton.MeshBoneNames, skeleton.MeshBoneCount);
        MeshPreviewSkeletonBuilder.BuildSkinWeightMasks(
            mesh,
            skeleton.MeshBoneCount,
            skeleton.MeshParentIndices,
            out var weightedBoneMask,
            out var deformChainMask);

        var directBindingPathToMeshBoneIdx = new Dictionary<uint, int>(
            (avatar.m_TOS?.Length ?? 0) + skeleton.MeshBoneHashToIdx.Count);
        foreach (var kvp in skeleton.MeshBoneHashToIdx)
        {
            directBindingPathToMeshBoneIdx[kvp.Key] = kvp.Value;
        }

        if (avatar.m_TOS != null)
        {
            foreach (var pair in avatar.m_TOS)
            {
                if (pair.Key == 0 || string.IsNullOrEmpty(pair.Value))
                {
                    continue;
                }

                if (MeshPreviewSkeletonBuilder.TryResolvePathAlias(meshBonePathToIdx, pair.Value, out var resolvedIdx))
                {
                    directBindingPathToMeshBoneIdx[pair.Key] = resolvedIdx;
                }
            }
        }

        global::OpenTK.Mathematics.Vector3[]? sourceVertexData = null;
        var min = global::OpenTK.Mathematics.Vector3.Zero;
        var max = global::OpenTK.Mathematics.Vector3.Zero;
        if (mesh.m_Vertices != null && mesh.m_Vertices.Length >= 3 && mesh.m_VertexCount > 0)
        {
            sourceVertexData = ExtractRigMeshVertices(mesh.m_Vertices, mesh.m_VertexCount, out min, out max);
        }

        return new PreparedAnimationRigData
        {
            RigKey = rigKey,
            Avatar = avatar,
            Mesh = mesh,
            Skeleton = skeleton,
            MeshBonePathToIdx = meshBonePathToIdx,
            DirectBindingPathToMeshBoneIdx = directBindingPathToMeshBoneIdx,
            WeightedBoneMask = weightedBoneMask,
            DeformChainMask = deformChainMask,
            SourceVertexData = sourceVertexData,
            SourceMeshMin = min,
            SourceMeshMax = max
        };
    }

    private static global::OpenTK.Mathematics.Vector3[] ExtractRigMeshVertices(
        float[] vertices,
        int vertexCount,
        out global::OpenTK.Mathematics.Vector3 min,
        out global::OpenTK.Mathematics.Vector3 max)
    {
        var count = Math.Min(vertexCount, vertices.Length / 3);
        var result = new global::OpenTK.Mathematics.Vector3[count];
        min = new global::OpenTK.Mathematics.Vector3(float.MaxValue);
        max = new global::OpenTK.Mathematics.Vector3(float.MinValue);
        for (var i = 0; i < count; i++)
        {
            var offset = i * 3;
            var v = new global::OpenTK.Mathematics.Vector3(-vertices[offset], vertices[offset + 1], vertices[offset + 2]);
            result[i] = v;
            if (float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z))
            {
                min = global::OpenTK.Mathematics.Vector3.ComponentMin(min, v);
                max = global::OpenTK.Mathematics.Vector3.ComponentMax(max, v);
            }
        }

        if (count == 0 || !float.IsFinite(min.X))
        {
            min = global::OpenTK.Mathematics.Vector3.Zero;
            max = global::OpenTK.Mathematics.Vector3.Zero;
        }

        return result;
    }
}

internal sealed class AnimationRigPreviewCache
{
    private readonly object _gate = new();
    private readonly int _maxRigEntries;
    private readonly int _maxClipEntries;
    private readonly Dictionary<string, LinkedListNode<(string Key, PreparedAnimationRigData Value)>> _rigMap;
    private readonly LinkedList<(string Key, PreparedAnimationRigData Value)> _rigLru = new();
    private readonly Dictionary<string, LinkedListNode<(string ClipKey, string RigKey)>> _clipTargetMap;
    private readonly LinkedList<(string ClipKey, string RigKey)> _clipTargetLru = new();
    private long _hitCount;
    private long _missCount;

    public AnimationRigPreviewCache(int maxRigEntries = 16, int maxClipEntries = 64)
    {
        _maxRigEntries = Math.Max(1, maxRigEntries);
        _maxClipEntries = Math.Max(1, maxClipEntries);
        _rigMap = new Dictionary<string, LinkedListNode<(string Key, PreparedAnimationRigData Value)>>(StringComparer.Ordinal);
        _clipTargetMap = new Dictionary<string, LinkedListNode<(string ClipKey, string RigKey)>>(StringComparer.Ordinal);
    }

    public long HitCount => Interlocked.Read(ref _hitCount);
    public long MissCount => Interlocked.Read(ref _missCount);

    public int RigCount
    {
        get
        {
            lock (_gate)
            {
                return _rigMap.Count;
            }
        }
    }

    public bool TryGetRig(string rigKey, out PreparedAnimationRigData? rig)
    {
        rig = null;
        if (string.IsNullOrWhiteSpace(rigKey))
        {
            return false;
        }

        lock (_gate)
        {
            if (_rigMap.TryGetValue(rigKey, out var node))
            {
                _rigLru.Remove(node);
                _rigLru.AddFirst(node);
                rig = node.Value.Value;
                Interlocked.Increment(ref _hitCount);
                return true;
            }
        }

        Interlocked.Increment(ref _missCount);
        return false;
    }

    public PreparedAnimationRigData? GetOrAddRig(string rigKey, Func<PreparedAnimationRigData?> factory, out bool fromCache)
    {
        if (TryGetRig(rigKey, out var cached) && cached != null)
        {
            fromCache = true;
            return cached;
        }

        fromCache = false;
        var built = factory();
        if (built == null || string.IsNullOrWhiteSpace(rigKey))
        {
            return built;
        }

        lock (_gate)
        {
            if (_rigMap.TryGetValue(rigKey, out var existingNode))
            {
                _rigLru.Remove(existingNode);
                existingNode.Value = (rigKey, built);
                _rigLru.AddFirst(existingNode);
                return built;
            }

            while (_rigMap.Count >= _maxRigEntries && _rigLru.Last != null)
            {
                var tail = _rigLru.Last;
                _rigLru.RemoveLast();
                _rigMap.Remove(tail.Value.Key);
            }

            var node = new LinkedListNode<(string Key, PreparedAnimationRigData Value)>((rigKey, built));
            _rigLru.AddFirst(node);
            _rigMap[rigKey] = node;
        }

        return built;
    }

    public void RememberClipTarget(string clipKey, string rigKey)
    {
        if (string.IsNullOrWhiteSpace(clipKey) || string.IsNullOrWhiteSpace(rigKey))
        {
            return;
        }

        lock (_gate)
        {
            if (_clipTargetMap.TryGetValue(clipKey, out var existingNode))
            {
                _clipTargetLru.Remove(existingNode);
                existingNode.Value = (clipKey, rigKey);
                _clipTargetLru.AddFirst(existingNode);
                return;
            }

            while (_clipTargetMap.Count >= _maxClipEntries && _clipTargetLru.Last != null)
            {
                var tail = _clipTargetLru.Last;
                _clipTargetLru.RemoveLast();
                _clipTargetMap.Remove(tail.Value.ClipKey);
            }

            var node = new LinkedListNode<(string ClipKey, string RigKey)>((clipKey, rigKey));
            _clipTargetLru.AddFirst(node);
            _clipTargetMap[clipKey] = node;
        }
    }

    public bool TryGetClipTarget(string clipKey, out string rigKey)
    {
        rigKey = string.Empty;
        if (string.IsNullOrWhiteSpace(clipKey))
        {
            return false;
        }

        lock (_gate)
        {
            if (_clipTargetMap.TryGetValue(clipKey, out var node))
            {
                _clipTargetLru.Remove(node);
                _clipTargetLru.AddFirst(node);
                rigKey = node.Value.RigKey;
                return true;
            }
        }

        return false;
    }

    public void Clear()
    {
        lock (_gate)
        {
            _rigMap.Clear();
            _rigLru.Clear();
            _clipTargetMap.Clear();
            _clipTargetLru.Clear();
            Interlocked.Exchange(ref _hitCount, 0);
            Interlocked.Exchange(ref _missCount, 0);
        }
    }
}

public partial class MainWindow
{
    private AnimationPreviewLoadCoordinator _animationPreviewCoordinator => PreviewSession.AnimationCoordinator;
    private AnimationRigPreviewCache _animationRigPreviewCache => MeshPreview.RigCache;

    private sealed class PreparedAnimationClipPreviewOutcome
    {
        public string ClipName { get; init; } = string.Empty;
        public float SampleRate { get; init; } = 30f;
        public IReadOnlyList<PreviewCandidateItem>? Candidates { get; init; }
        public PreviewCandidateItem? SelectedCandidate { get; init; }
        public Avatar? Avatar { get; init; }
        public Mesh? Mesh { get; init; }
        public MeshPreviewControl.PreparedAvatarPreviewGLData? GlData { get; init; }
        public int FrameCount { get; init; }
        public string StatusMessage { get; init; } = string.Empty;
    }

    private void PreviewAnimationClip(
        AnimationClip clip,
        Avatar? preferredAvatar = null,
        Mesh? preferredMesh = null,
        string? preferredAvatarId = null,
        string? preferredMeshId = null,
        bool rebuildCandidateControls = true,
        PreviewCandidateItem? selectedCandidate = null)
    {
        QueueAnimationClipPreviewAsync(
            clip,
            preferredAvatar,
            preferredMesh,
            preferredAvatarId,
            preferredMeshId,
            rebuildCandidateControls,
            selectedCandidate);
    }

    private void QueueAnimationClipPreviewAsync(
        AnimationClip clip,
        Avatar? preferredAvatar,
        Mesh? preferredMesh,
        string? preferredAvatarId,
        string? preferredMeshId,
        bool rebuildCandidateControls,
        PreviewCandidateItem? selectedCandidate)
    {
        var token = _animationPreviewCoordinator.BeginNewRequest(out var requestId);
        var previewId = texturePreviewIdCounter;

        if (clip.m_PPtrCurves != null && clip.m_PPtrCurves.Length > 0)
        {
            Preview2DAnimationClipAsync(clip, requestId, previewId, token);
            return;
        }

        Stop2DAnimation();
        var selectedAssetSnapshot = AssetListDataGrid?.SelectedItem;
        var currentAvatarSnapshot = currentPreviewAvatar;
        var currentMeshSnapshot = currentPreviewMesh;
        var densityPercentSnapshot = GLPreviewControl?.PreviewMeshDensityPercent
            ?? (float)Math.Clamp(appSettings.AvatarPreviewMeshDensityPercent, MinAvatarPreviewMeshDensityPercent, MaxAvatarPreviewMeshDensityPercent);
        var boneScaleSnapshot = GLPreviewControl?.BoneScale
            ?? (float)Math.Clamp(appSettings.AvatarPreviewBoneScale, MinAvatarPreviewBoneScale, MaxAvatarPreviewBoneScale);

        StatusStripUpdate($"Resolving rig & loading animation '{clip.m_Name}'...");

        _ = Task.Run(() =>
        {
            var shouldPauseIndexing = assetsManager.LazyLoading;
            if (shouldPauseIndexing)
            {
                Interlocked.Increment(ref foregroundLazyLoadCount);
            }

            try
            {
                token.ThrowIfCancellationRequested();
                PrioritizeUserInteraction(UserPreviewPriorityMilliseconds);

                var outcome = PrepareAnimationClipPreviewBackground(
                    clip,
                    preferredAvatar,
                    preferredMesh,
                    preferredAvatarId,
                    preferredMeshId,
                    rebuildCandidateControls,
                    selectedCandidate,
                    currentAvatarSnapshot,
                    currentMeshSnapshot,
                    densityPercentSnapshot,
                    boneScaleSnapshot,
                    token);

                token.ThrowIfCancellationRequested();

                Dispatcher.UIThread.Post(() =>
                {
                    if (token.IsCancellationRequested
                        || !_animationPreviewCoordinator.IsCurrent(requestId)
                        || previewId != texturePreviewIdCounter
                        || (selectedAssetSnapshot != null && !ReferenceEquals(AssetListDataGrid?.SelectedItem, selectedAssetSnapshot)))
                    {
                        return;
                    }

                    ApplyPreparedAnimationClipPreviewOnUI(outcome, rebuildCandidateControls);
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                logger.Log(LoggerEvent.Error, $"Animation preview failed for {clip.m_Name}: {ex}");
                Dispatcher.UIThread.Post(() =>
                {
                    if (!token.IsCancellationRequested
                        && _animationPreviewCoordinator.IsCurrent(requestId)
                        && previewId == texturePreviewIdCounter)
                    {
                        if (rebuildCandidateControls)
                        {
                            ClearPreviewCandidateControls();
                        }

                        StatusStripUpdate($"AnimationClip preview error: {ex.Message}");
                    }
                });
            }
            finally
            {
                if (shouldPauseIndexing)
                {
                    Interlocked.Decrement(ref foregroundLazyLoadCount);
                }
            }
        }, token);
    }

    private void ApplyPreparedAnimationClipPreviewOnUI(
        PreparedAnimationClipPreviewOutcome outcome,
        bool rebuildCandidateControls)
    {
        PreviewSession.ActivateSurface(
            PreviewSurfaceKind.Animation3D,
            AssetListDataGrid?.SelectedItem as AssetItem,
            isLoading: false);
        MeshPreview.SetMeshAndRigSummary(
            meshSummary: null,
            rigSummary: outcome.StatusMessage,
            meshName: outcome.Mesh?.m_Name,
            avatarName: outcome.Avatar?.m_Name);

        if (rebuildCandidateControls && outcome.Candidates != null)
        {
            BuildPreviewCandidateControls(outcome.Candidates, outcome.SelectedCandidate, "Animation Target");
        }

        if (outcome.GlData != null && GLPreviewControl != null)
        {
            currentPreviewAvatar = outcome.Avatar;
            currentPreviewMesh = outcome.Mesh;
            GLPreviewControl.ApplyPreparedAvatarPreview(outcome.GlData);
            GLPreviewControl.IsVisible = true;
            ShowPreviewGeometryControls(showBoneControls: true);
            GLPreviewControl.Focus();
            if (TextPreviewBox != null)
            {
                TextPreviewBox.IsVisible = false;
            }
            if (PreviewLabel != null)
            {
                PreviewLabel.IsVisible = false;
            }

            if (outcome.FrameCount > 0)
            {
                var fps = outcome.SampleRate > 0f ? outcome.SampleRate : 30f;
                MeshPreview.SetAnimationState(
                    isLoaded: true,
                    clipName: outcome.ClipName,
                    avatarName: outcome.Avatar?.m_Name ?? string.Empty,
                    durationSeconds: outcome.FrameCount / (double)fps,
                    currentTimeSeconds: 0.0,
                    isPlaying: true,
                    totalFrames: outcome.FrameCount,
                    sampleRate: fps,
                    availableAnimations: outcome.Candidates,
                    selectedAnimation: outcome.SelectedCandidate);
                ShowAnimationPlayback(outcome.FrameCount);
            }
            else
            {
                MeshPreview.SetAnimationState(isLoaded: false);
                HideAnimationPlayback();
            }
        }
        else
        {
            MeshPreview.SetAnimationState(isLoaded: false);
        }

        if (!string.IsNullOrEmpty(outcome.StatusMessage))
        {
            StatusStripUpdate(outcome.StatusMessage);
        }
    }

    private PreparedAnimationClipPreviewOutcome PrepareAnimationClipPreviewBackground(
        AnimationClip clip,
        Avatar? preferredAvatar,
        Mesh? preferredMesh,
        string? preferredAvatarId,
        string? preferredMeshId,
        bool rebuildCandidateControls,
        PreviewCandidateItem? selectedCandidate,
        Avatar? currentAvatarSnapshot,
        Mesh? currentMeshSnapshot,
        float densityPercent,
        float boneScale,
        CancellationToken token)
    {
        IReadOnlyList<PreviewCandidateItem>? candidates = null;
        PreviewCandidateItem? activeCandidate = selectedCandidate;

        if (rebuildCandidateControls)
        {
            candidates = BuildAnimationClipPreviewCandidates(
                clip,
                preferredAvatar,
                preferredMesh,
                preferredAvatarId,
                preferredMeshId,
                currentAvatarSnapshot,
                currentMeshSnapshot);
            token.ThrowIfCancellationRequested();

            activeCandidate = selectedCandidate
                ?? SelectAnimationClipPreviewCandidate(
                    clip,
                    candidates,
                    preferredAvatar,
                    preferredMesh,
                    preferredAvatarId,
                    preferredMeshId);
        }
        else if (activeCandidate == null)
        {
            activeCandidate = new PreviewCandidateItem
            {
                AnimationClip = clip,
                Avatar = preferredAvatar,
                Mesh = preferredMesh,
                AvatarId = preferredAvatarId ?? string.Empty,
                MeshId = preferredMeshId ?? string.Empty
            };
        }

        token.ThrowIfCancellationRequested();
        var resolvedCandidate = ResolveAnimationClipPreviewCandidate(activeCandidate);
        token.ThrowIfCancellationRequested();

        Avatar? avatar = resolvedCandidate?.Avatar;
        Mesh? avatarMesh = resolvedCandidate?.Mesh;

        if (avatar == null && avatarMesh != null)
        {
            if (TryPrepareAnimationClipWithRendererSkeletonBackground(
                    clip,
                    avatarMesh,
                    resolvedCandidate?.MeshId,
                    currentMeshSnapshot,
                    densityPercent,
                    boneScale,
                    token,
                    out var fallbackOutcome,
                    candidates,
                    resolvedCandidate ?? activeCandidate))
            {
                return fallbackOutcome!;
            }

            return new PreparedAnimationClipPreviewOutcome
            {
                Candidates = candidates,
                SelectedCandidate = resolvedCandidate ?? activeCandidate,
                StatusMessage = $"AnimationClip: {clip.m_Name} | Mesh target found, but no usable animation tracks matched {avatarMesh.m_Name}."
            };
        }

        if (avatar == null)
        {
            if (TryPrepareAnimationClipWithRendererSkeletonBackground(
                    clip,
                    preferredMesh,
                    preferredMeshId,
                    currentMeshSnapshot,
                    densityPercent,
                    boneScale,
                    token,
                    out var fallbackOutcome,
                    candidates,
                    resolvedCandidate ?? activeCandidate))
            {
                return fallbackOutcome!;
            }

            return new PreparedAnimationClipPreviewOutcome
            {
                Candidates = candidates,
                SelectedCandidate = resolvedCandidate ?? activeCandidate,
                StatusMessage = $"AnimationClip: No compatible Avatar or mesh target found for {clip.m_Name}."
            };
        }

        if (avatar.m_Avatar?.m_AvatarSkeleton?.m_Node == null)
        {
            return new PreparedAnimationClipPreviewOutcome
            {
                Candidates = candidates,
                SelectedCandidate = resolvedCandidate ?? activeCandidate,
                StatusMessage = "AnimationClip: No Avatar found to preview animation."
            };
        }

        if (avatarMesh == null)
        {
            avatarMesh = ResolveFirstMeshForAvatar(avatar);
        }

        token.ThrowIfCancellationRequested();
        avatarMesh?.EnsureProcessed();

        if (avatarMesh == null)
        {
            return new PreparedAnimationClipPreviewOutcome
            {
                Candidates = candidates,
                SelectedCandidate = resolvedCandidate ?? activeCandidate,
                StatusMessage = "AnimationClip: No suitable mesh with bind poses found."
            };
        }

        var avatarKey = GetPreviewObjectKey(avatar);
        var meshKey = GetPreviewObjectKey(avatarMesh);
        var rigKey = $"{avatarKey}|{meshKey}";
        var rigData = _animationRigPreviewCache.GetOrAddRig(
            rigKey,
            () => PreparedAnimationRigData.TryBuild(rigKey, avatar, avatarMesh),
            out _);

        if (rigData == null)
        {
            return new PreparedAnimationClipPreviewOutcome
            {
                Candidates = candidates,
                SelectedCandidate = resolvedCandidate ?? activeCandidate,
                StatusMessage = "AnimationClip: No suitable mesh with bind poses found."
            };
        }

        var clipKey = GetPreviewObjectKey(clip);
        if (!string.IsNullOrEmpty(clipKey))
        {
            _animationRigPreviewCache.RememberClipTarget(clipKey, rigKey);
        }

        token.ThrowIfCancellationRequested();

        var skeleton = rigData.Skeleton;
        var muscleClip = clip.m_MuscleClip;
        var tracks = new AnimationTrackSet();

        if (muscleClip?.m_Clip != null)
        {
            var innerClip = muscleClip.m_Clip;
            var bindings = clip.m_ClipBindingConstant ?? innerClip.ConvertValueArrayToGenericBinding();
            if (bindings?.genericBindings != null)
            {
                MeshPreviewAnimationEvaluator.ExtractMuscleClipTracks(
                    innerClip,
                    bindings,
                    muscleClip.m_StopTime,
                    rigData.TryResolveBindingBone,
                    rigData.ShouldUseBinding,
                    tracks,
                    updateMaxTimeBeforeBoundsCheck: true);
            }

            if (AssetExportHelper.IsHumanoidAvatar(avatar) && muscleClip.m_IndexArray?.Length > HumanoidMuscleDecoder.RootSlotOffset)
            {
                MeshPreviewAnimationEvaluator.ExtractHumanoidMuscleTracks(
                    clip,
                    avatar,
                    skeleton,
                    rigData.MeshBonePathToIdx,
                    rigData.ShouldUseBinding,
                    tracks);
            }
        }

        if (tracks.TotalTrackCount == 0)
        {
            bool TryResolveCurvePath(string? path, out int meshBoneIdx)
            {
                return MeshPreviewSkeletonBuilder.TryResolvePathAlias(rigData.MeshBonePathToIdx, path, out meshBoneIdx);
            }

            MeshPreviewAnimationEvaluator.ExtractLegacyCurves(clip, TryResolveCurvePath, rigData.ShouldUseBinding, tracks);
        }

        if (tracks.TotalTrackCount == 0 && muscleClip?.m_Clip == null)
        {
            return new PreparedAnimationClipPreviewOutcome
            {
                Candidates = candidates,
                SelectedCandidate = resolvedCandidate ?? activeCandidate,
                StatusMessage = "AnimationClip: No muscle clip data."
            };
        }

        tracks.SortAll();
        token.ThrowIfCancellationRequested();

        var referenceMesh = rigData.GetOrBuildReferenceMesh(densityPercent);
        if (tracks.TotalTrackCount == 0)
        {
            var staticGlData = MeshPreviewControl.PrepareStaticAvatarGLData(
                avatarMesh,
                skeleton.RestBonePositions,
                skeleton.MeshParentIndices,
                skeleton.MeshBoneNames,
                densityPercent,
                boneScale,
                rigData.SourceVertexData,
                rigData.SourceMeshMin,
                rigData.SourceMeshMax,
                referenceMesh);

            return new PreparedAnimationClipPreviewOutcome
            {
                Candidates = candidates,
                SelectedCandidate = resolvedCandidate ?? activeCandidate,
                Avatar = avatar,
                Mesh = avatarMesh,
                GlData = staticGlData,
                FrameCount = 0,
                StatusMessage = $"AnimationClip: {clip.m_Name} | No animation tracks extracted, showing bind pose | Bones: {skeleton.MeshBoneCount}"
            };
        }

        var defaultPose = avatar.m_Avatar.m_DefaultPose?.m_X ?? avatar.m_Avatar.m_AvatarSkeletonPose?.m_X;
        var sampleRate = clip.m_SampleRate > 0 ? clip.m_SampleRate : 30f;
        var stopTime = muscleClip?.m_StopTime ?? 0f;
        var maxTime = tracks.MaxTime > 0 ? tracks.MaxTime : stopTime > 0 ? stopTime : 1f;

        MeshPreviewAnimationEvaluator.EvaluateFramesFromTracks(
            avatarMesh,
            skeleton.RestBonePositions,
            skeleton.MeshParentIndices,
            skeleton.MeshBoneNames,
            skeleton.BindPoseInverses,
            rigData.DeformChainMask,
            rigData.WeightedBoneMask,
            tracks.PosTracks,
            tracks.RotTracks,
            tracks.ScaleTracks,
            sampleRate,
            maxTime,
            defaultPose,
            skeleton.MeshBoneToSkelNode,
            useCeilingFrameCount: false,
            failIfAllTracksFiltered: false,
            out var allFrames,
            out var allBoneMatrices,
            out var renderParentIndices,
            out var resolvedSampleRate,
            out _);

        token.ThrowIfCancellationRequested();

        var animatedGlData = MeshPreviewControl.PrepareAnimatedAvatarGLData(
            avatarMesh,
            allFrames,
            allBoneMatrices,
            renderParentIndices,
            resolvedSampleRate,
            skeleton.MeshBoneNames,
            densityPercent,
            boneScale,
            rigData.SourceVertexData,
            rigData.SourceMeshMin,
            rigData.SourceMeshMax,
            referenceMesh);

        var targetStatus = resolvedCandidate?.IsModelGroup == true
            ? $" | Model group: {(!string.IsNullOrWhiteSpace(resolvedCandidate.ModelGroupName) ? resolvedCandidate.ModelGroupName : resolvedCandidate.ModelGroupId)} ({resolvedCandidate.ModelGroupMeshCount:N0} parts) | Representative mesh: {avatarMesh.m_Name}"
            : $" | Mesh: {avatarMesh.m_Name}";

        return new PreparedAnimationClipPreviewOutcome
        {
            ClipName = clip.m_Name ?? string.Empty,
            SampleRate = resolvedSampleRate,
            Candidates = candidates,
            SelectedCandidate = resolvedCandidate ?? activeCandidate,
            Avatar = avatar,
            Mesh = avatarMesh,
            GlData = animatedGlData,
            FrameCount = allFrames.Length,
            StatusMessage = $"Animation Preview | Clip: {clip.m_Name}{targetStatus} | Frames: {allFrames.Length} | FPS: {resolvedSampleRate} | Tracks: {tracks.TotalTrackCount}"
        };
    }

    private bool TryPrepareAnimationClipWithRendererSkeletonBackground(
        AnimationClip clip,
        Mesh? preferredMesh,
        string? preferredMeshId,
        Mesh? currentMeshSnapshot,
        float densityPercent,
        float boneScale,
        CancellationToken token,
        out PreparedAnimationClipPreviewOutcome? outcome,
        IReadOnlyList<PreviewCandidateItem>? candidates,
        PreviewCandidateItem? selectedCandidate)
    {
        outcome = null;
        var meshes = new List<Mesh>();
        void AddMesh(Mesh? mesh)
        {
            if (mesh != null && !meshes.Any(existing => AreSamePreviewObject(existing, mesh)))
            {
                meshes.Add(mesh);
            }
        }

        AddMesh(preferredMesh);
        if (preferredMesh == null && !string.IsNullOrWhiteSpace(preferredMeshId))
        {
            AddMesh(ResolveMeshPreviewCandidate(preferredMeshId));
        }

        AddMesh(currentMeshSnapshot);
        if (meshes.Count == 0)
        {
            var firstMeshId = LoadAnimationClipMeshAssetIdsForPreview(clip).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(firstMeshId))
            {
                AddMesh(ResolveMeshPreviewCandidate(firstMeshId));
            }
        }

        Mesh? staticMesh = null;
        global::OpenTK.Mathematics.Vector3[]? staticBonePositions = null;
        int[]? staticParentIndices = null;
        string[]? staticBoneNames = null;
        string? lastReason = null;

        foreach (var mesh in meshes)
        {
            token.ThrowIfCancellationRequested();
            mesh.EnsureProcessed();
            var renderer = FindSkinnedRendererForAvatarMesh(null, mesh);
            if (renderer == null && !CanUseLazySemanticRelationCache(GetCurrentCacheFolderPath()))
            {
                EnsureMeshPreviewDependenciesLoaded(mesh);
                renderer = FindSkinnedRendererForAvatarMesh(null, mesh);
            }

            if (renderer == null)
            {
                continue;
            }

            if (!TryBuildSkeletonFromSkinnedRenderer(
                    null,
                    mesh,
                    renderer,
                    out var bonePositions,
                    out var parentIndices,
                    out var boneNames))
            {
                continue;
            }

            if (staticMesh == null)
            {
                staticMesh = mesh;
                staticBonePositions = bonePositions;
                staticParentIndices = parentIndices;
                staticBoneNames = boneNames;
            }

            if (TryBuildAnimationFramesFromRendererSkeleton(
                    clip,
                    mesh,
                    bonePositions,
                    parentIndices,
                    boneNames,
                    out var allFrames,
                    out var allBoneMatrices,
                    out var renderParentIndices,
                    out var sampleRate,
                    out var trackCount,
                    out lastReason))
            {
                token.ThrowIfCancellationRequested();
                var animatedGlData = MeshPreviewControl.PrepareAnimatedAvatarGLData(
                    mesh,
                    allFrames,
                    allBoneMatrices,
                    renderParentIndices,
                    sampleRate,
                    boneNames,
                    densityPercent,
                    boneScale);

                outcome = new PreparedAnimationClipPreviewOutcome
                {
                    Candidates = candidates,
                    SelectedCandidate = selectedCandidate,
                    Avatar = null,
                    Mesh = mesh,
                    GlData = animatedGlData,
                    FrameCount = allFrames.Length,
                    StatusMessage = $"Animation Preview | Clip: {clip.m_Name} | Mesh target: {mesh.m_Name} | Frames: {allFrames.Length} | FPS: {sampleRate:0.##} | Tracks: {trackCount}"
                };
                return true;
            }
        }

        if (staticMesh != null && staticBonePositions != null && staticParentIndices != null)
        {
            token.ThrowIfCancellationRequested();
            var staticGlData = MeshPreviewControl.PrepareStaticAvatarGLData(
                staticMesh,
                staticBonePositions,
                staticParentIndices,
                staticBoneNames,
                densityPercent,
                boneScale);

            outcome = new PreparedAnimationClipPreviewOutcome
            {
                Candidates = candidates,
                SelectedCandidate = selectedCandidate,
                Avatar = null,
                Mesh = staticMesh,
                GlData = staticGlData,
                FrameCount = 0,
                StatusMessage = $"AnimationClip: {clip.m_Name} | No matching animation tracks for mesh {staticMesh.m_Name}; showing renderer skeleton. {lastReason}"
            };
            return true;
        }

        return false;
    }

    private bool TryBuildAnimationFramesFromRendererSkeleton(
        AnimationClip clip,
        Mesh mesh,
        global::OpenTK.Mathematics.Vector3[] restBonePositions,
        int[] meshParentIndices,
        string[] meshBoneNames,
        out global::OpenTK.Mathematics.Vector3[][] allFrames,
        out global::OpenTK.Mathematics.Matrix4[][] allBoneMatrices,
        out int[] renderParentIndices,
        out float sampleRate,
        out int trackCount,
        out string reason)
    {
        allFrames = Array.Empty<global::OpenTK.Mathematics.Vector3[]>();
        allBoneMatrices = Array.Empty<global::OpenTK.Mathematics.Matrix4[]>();
        renderParentIndices = Array.Empty<int>();
        sampleRate = clip.m_SampleRate > 0 ? clip.m_SampleRate : 30f;
        trackCount = 0;
        reason = string.Empty;

        if (mesh.m_BindPose == null || mesh.m_BindPose.Length == 0)
        {
            reason = "Mesh has no bind poses.";
            return false;
        }

        var meshBoneCount = Math.Min(mesh.m_BindPose.Length, Math.Min(restBonePositions.Length, meshParentIndices.Length));
        if (meshBoneCount == 0)
        {
            reason = "Renderer skeleton has no bones.";
            return false;
        }

        var bindPoseInverses = MeshPreviewSkeletonBuilder.BuildBindPoseInverses(mesh.m_BindPose, meshBoneCount, out _);

        var meshBoneHashToIdx = new Dictionary<uint, int>();
        if (mesh.m_BoneNameHashes != null)
        {
            for (var i = 0; i < Math.Min(mesh.m_BoneNameHashes.Length, meshBoneCount); i++)
            {
                meshBoneHashToIdx[mesh.m_BoneNameHashes[i]] = i;
            }
        }

        var meshBonePathToIdx = MeshPreviewSkeletonBuilder.BuildBonePathAliasMap(meshBoneNames, meshBoneCount);
        MeshPreviewSkeletonBuilder.BuildSkinWeightMasks(
            mesh,
            meshBoneCount,
            meshParentIndices,
            out var weightedBoneMask,
            out var deformChainMask);

        bool TryResolveCurvePath(string? path, out int meshBoneIdx)
        {
            return MeshPreviewSkeletonBuilder.TryResolvePathAlias(meshBonePathToIdx, path, out meshBoneIdx);
        }

        bool TryResolveBindingBone(GenericBinding binding, out int meshBoneIdx)
        {
            return meshBoneHashToIdx.TryGetValue(binding.path, out meshBoneIdx);
        }

        bool ShouldUseBinding(int meshBoneIdx)
        {
            return MeshPreviewSkeletonBuilder.ShouldUseAnimationBinding(
                meshBoneIdx,
                meshBoneCount,
                deformChainMask,
                weightedBoneMask,
                meshBoneNames);
        }

        var tracks = new AnimationTrackSet();
        MeshPreviewAnimationEvaluator.ExtractLegacyCurves(clip, TryResolveCurvePath, ShouldUseBinding, tracks);

        var muscleClip = clip.m_MuscleClip;
        var innerClip = muscleClip?.m_Clip;
        AnimationClipBindingConstant? bindings = null;
        if (innerClip != null)
        {
            bindings = clip.m_ClipBindingConstant;
            if (bindings == null && innerClip.m_Binding != null)
            {
                bindings = innerClip.ConvertValueArrayToGenericBinding();
            }
        }

        if (innerClip != null && bindings?.genericBindings != null)
        {
            MeshPreviewAnimationEvaluator.ExtractMuscleClipTracks(
                innerClip,
                bindings,
                muscleClip?.m_StopTime,
                TryResolveBindingBone,
                ShouldUseBinding,
                tracks);
        }

        tracks.SortAll();
        trackCount = tracks.TotalTrackCount;
        if (trackCount == 0)
        {
            reason = bindings?.genericBindings != null
                ? "Clip bindings did not match renderer bone hashes/names."
                : "Clip has no readable transform binding table.";
            return false;
        }

        return BuildAnimationFramesFromTracks(
            mesh,
            restBonePositions,
            meshParentIndices,
            meshBoneNames,
            bindPoseInverses,
            deformChainMask,
            weightedBoneMask,
            tracks.PosTracks,
            tracks.RotTracks,
            tracks.ScaleTracks,
            sampleRate,
            tracks.MaxTime > 0f ? tracks.MaxTime : muscleClip?.m_StopTime ?? 1f,
            out allFrames,
            out allBoneMatrices,
            out renderParentIndices,
            out reason);
    }

    private static string NormalizePreviewAnimationPath(string? path)
    {
        return MeshPreviewSkeletonBuilder.NormalizeAnimationPath(path);
    }

    private static void AddPreviewBonePathAlias(Dictionary<string, int> bonePathToIndex, string? path, int meshBoneIdx)
    {
        MeshPreviewSkeletonBuilder.AddBonePathAlias(bonePathToIndex, path, meshBoneIdx);
    }

    private static global::OpenTK.Mathematics.Vector3 ToOtkVector3Preview(AssetStudio.Vector3 value)
    {
        return MeshPreviewSkeletonBuilder.ToOtkVector3(value);
    }

    private static global::OpenTK.Mathematics.Quaternion ToOtkQuaternionPreview(AssetStudio.Quaternion value)
    {
        return MeshPreviewSkeletonBuilder.ToOtkQuaternion(value);
    }

    private static global::OpenTK.Mathematics.Matrix4 CreatePreviewLocalMatrix(
        global::OpenTK.Mathematics.Vector3 position,
        global::OpenTK.Mathematics.Quaternion rotation,
        global::OpenTK.Mathematics.Vector3 scale)
    {
        return MeshPreviewSkeletonBuilder.CreateLocalMatrix(position, rotation, scale);
    }

    private static bool IsFinitePreviewVector(global::OpenTK.Mathematics.Vector3 value)
    {
        return MeshPreviewSkeletonBuilder.IsFiniteVector(value);
    }

    private static bool IsAuxiliaryPreviewAnimationBone(string? path)
    {
        return MeshPreviewSkeletonBuilder.IsAuxiliaryAnimationBone(path);
    }

    private static bool BuildAnimationFramesFromTracks(
        Mesh mesh,
        global::OpenTK.Mathematics.Vector3[] restBonePositions,
        int[] meshParentIndices,
        string[] meshBoneNames,
        global::OpenTK.Mathematics.Matrix4[] bindPoseInverses,
        bool[] deformChainMask,
        bool[] weightedBoneMask,
        Dictionary<int, List<(float time, global::OpenTK.Mathematics.Vector3 value)>> posTracks,
        Dictionary<int, List<(float time, global::OpenTK.Mathematics.Quaternion value)>> rotTracks,
        Dictionary<int, List<(float time, global::OpenTK.Mathematics.Vector3 value)>> scaleTracks,
        float sampleRate,
        float maxTime,
        out global::OpenTK.Mathematics.Vector3[][] allFrames,
        out global::OpenTK.Mathematics.Matrix4[][] allBoneMatrices,
        out int[] renderParentIndices,
        out string reason)
    {
        return MeshPreviewAnimationEvaluator.EvaluateFramesFromTracks(
            mesh,
            restBonePositions,
            meshParentIndices,
            meshBoneNames,
            bindPoseInverses,
            deformChainMask,
            weightedBoneMask,
            posTracks,
            rotTracks,
            scaleTracks,
            sampleRate,
            maxTime,
            avatarDefaultPose: null,
            meshBoneToSkelNode: null,
            useCeilingFrameCount: true,
            failIfAllTracksFiltered: true,
            out allFrames,
            out allBoneMatrices,
            out renderParentIndices,
            out _,
            out reason);
    }
}
