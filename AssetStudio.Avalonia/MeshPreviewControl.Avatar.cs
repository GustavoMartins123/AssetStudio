using System;
using System.Collections.Generic;
using System.Linq;

namespace AssetStudio.Avalonia
{
    using Vector3 = OpenTK.Mathematics.Vector3;
    using Vector4 = OpenTK.Mathematics.Vector4;
    using Matrix4 = OpenTK.Mathematics.Matrix4;

    public partial class MeshPreviewControl
    {
        internal sealed class PreparedAvatarPreviewGLData
        {
            public required Mesh Mesh { get; init; }
            public required Vector3[] SourceVertexData { get; init; }
            public required List<uint> SourceIndices { get; init; }
            public required Vector3 SourceMeshMin { get; init; }
            public required Vector3 SourceMeshMax { get; init; }
            public required PreviewMeshGeometry ReferenceMesh { get; init; }
            public required Vector3[] SkeletonVertices { get; init; }
            public required int BoneLinesCount { get; init; }
            public required int JointPointsCount { get; init; }
            public required Matrix4 ModelMatrixData { get; init; }
            public required Matrix4 AutoViewMatrix { get; init; }
            public required int[] ParentIndices { get; init; }
            public Vector3[]? StaticBonePositions { get; init; }
            public Vector3[][]? AnimationFrames { get; init; }
            public Matrix4[][]? BoneMatrices { get; init; }
            public Vector3[][]? AnimatedMeshVertices { get; init; }
            public float Fps { get; init; } = 30f;
            public bool IsAnimated => AnimationFrames != null && AnimationFrames.Length > 0;
        }

        internal static PreparedAvatarPreviewGLData? PrepareStaticAvatarGLData(
            Mesh m_Mesh,
            Vector3[] bonePositions,
            int[] parentIndices,
            string[]? boneNames,
            float densityPercent,
            float boneScale,
            Vector3[]? precomputedSourceVertices = null,
            Vector3? precomputedMin = null,
            Vector3? precomputedMax = null,
            PreviewMeshGeometry? precomputedReferenceMesh = null)
        {
            m_Mesh.EnsureProcessed();
            if (m_Mesh.m_VertexCount <= 0 || m_Mesh.m_Vertices == null || m_Mesh.m_Vertices.Length == 0)
            {
                return null;
            }

            Vector3 min;
            Vector3 max;
            Vector3[] localVertexData;
            if (precomputedSourceVertices != null && precomputedMin.HasValue && precomputedMax.HasValue)
            {
                localVertexData = precomputedSourceVertices;
                min = precomputedMin.Value;
                max = precomputedMax.Value;
            }
            else
            {
                localVertexData = ExtractMeshVertices(m_Mesh.m_Vertices, m_Mesh.m_VertexCount, out min, out max);
            }

            var sourceMeshMin = min;
            var sourceMeshMax = max;
            var m_Indices = m_Mesh.m_Indices ?? new List<uint>();
            var referenceMesh = precomputedReferenceMesh
                ?? PreviewMeshSimplifier.Build(localVertexData, m_Indices, sourceMeshMin, sourceMeshMax, densityPercent);

            int boneLinesCount = 0;
            int jointPointsCount = 0;
            Vector3[] skeletonVerts = Array.Empty<Vector3>();
            if (bonePositions != null && parentIndices != null)
            {
                for (int i = 0; i < bonePositions.Length; i++)
                {
                    ExpandBounds(bonePositions[i], ref min, ref max);
                }

                skeletonVerts = BuildSkeletonVerticesStatic(bonePositions, parentIndices, boneScale, out boneLinesCount, out jointPointsCount);
            }

            var localModelMatrixData = CreatePreviewModelMatrix(min, max);
            var autoViewMatrix = bonePositions != null
                ? CreateAvatarInitialViewMatrix(bonePositions, boneNames)
                : Matrix4.Identity;

            return new PreparedAvatarPreviewGLData
            {
                Mesh = m_Mesh,
                SourceVertexData = localVertexData,
                SourceIndices = m_Indices,
                SourceMeshMin = sourceMeshMin,
                SourceMeshMax = sourceMeshMax,
                ReferenceMesh = referenceMesh,
                SkeletonVertices = skeletonVerts,
                BoneLinesCount = boneLinesCount,
                JointPointsCount = jointPointsCount,
                ModelMatrixData = localModelMatrixData,
                AutoViewMatrix = autoViewMatrix,
                ParentIndices = parentIndices ?? Array.Empty<int>(),
                StaticBonePositions = bonePositions
            };
        }

        internal static PreparedAvatarPreviewGLData? PrepareAnimatedAvatarGLData(
            Mesh m_Mesh,
            Vector3[][] frames,
            Matrix4[][] boneMatrices,
            int[] parentIndices,
            float fps,
            string[]? boneNames,
            float densityPercent,
            float boneScale,
            Vector3[]? precomputedSourceVertices = null,
            Vector3? precomputedMin = null,
            Vector3? precomputedMax = null,
            PreviewMeshGeometry? precomputedReferenceMesh = null)
        {
            m_Mesh.EnsureProcessed();
            if (m_Mesh.m_VertexCount <= 0
                || frames.Length == 0
                || m_Mesh.m_Vertices == null
                || m_Mesh.m_Vertices.Length == 0)
            {
                return null;
            }

            Vector3 min;
            Vector3 max;
            Vector3[] sourceVertexData;
            if (precomputedSourceVertices != null && precomputedMin.HasValue && precomputedMax.HasValue)
            {
                sourceVertexData = precomputedSourceVertices;
                min = precomputedMin.Value;
                max = precomputedMax.Value;
            }
            else
            {
                sourceVertexData = ExtractMeshVertices(m_Mesh.m_Vertices, m_Mesh.m_VertexCount, out min, out max);
            }

            var sourceMeshMin = min;
            var sourceMeshMax = max;
            var m_Indices = m_Mesh.m_Indices ?? new List<uint>();
            var referenceMesh = precomputedReferenceMesh
                ?? PreviewMeshSimplifier.Build(sourceVertexData, m_Indices, sourceMeshMin, sourceMeshMax, densityPercent);

            var animMin = new Vector3(float.MaxValue);
            var animMax = new Vector3(float.MinValue);
            Vector3[][]? localAnimatedMeshVerts = BuildAnimatedAvatarReferenceVertices(
                m_Mesh,
                sourceVertexData,
                referenceMesh,
                boneMatrices,
                parentIndices,
                ref animMin,
                ref animMax,
                expandBounds: true);

            var firstFrameBones = frames[0];
            foreach (var frame in frames)
            {
                foreach (var bonePosition in frame)
                {
                    ExpandBounds(bonePosition, ref animMin, ref animMax);
                }
            }

            if (localAnimatedMeshVerts == null || !IsFinitePoint(animMin) || !IsFinitePoint(animMax) || animMin.X > animMax.X)
            {
                ExpandBounds(sourceMeshMin, ref animMin, ref animMax);
                ExpandBounds(sourceMeshMax, ref animMin, ref animMax);
            }

            if (!IsFinitePoint(animMin) || !IsFinitePoint(animMax) || animMin.X > animMax.X)
            {
                animMin = sourceMeshMin;
                animMax = sourceMeshMax;
            }

            var skeletonVerts = BuildSkeletonVerticesStatic(firstFrameBones, parentIndices, boneScale, out int boneLines, out int jointPoints);
            var localModelMatrixData = CreatePreviewModelMatrix(animMin, animMax);
            var autoViewMatrix = CreateAvatarInitialViewMatrix(firstFrameBones, boneNames);

            return new PreparedAvatarPreviewGLData
            {
                Mesh = m_Mesh,
                SourceVertexData = sourceVertexData,
                SourceIndices = m_Indices,
                SourceMeshMin = sourceMeshMin,
                SourceMeshMax = sourceMeshMax,
                ReferenceMesh = referenceMesh,
                SkeletonVertices = skeletonVerts,
                BoneLinesCount = boneLines,
                JointPointsCount = jointPoints,
                ModelMatrixData = localModelMatrixData,
                AutoViewMatrix = autoViewMatrix,
                ParentIndices = parentIndices,
                AnimationFrames = frames,
                BoneMatrices = boneMatrices,
                AnimatedMeshVertices = localAnimatedMeshVerts,
                Fps = fps > 0 ? fps : 30f
            };
        }

        internal void ApplyPreparedAvatarPreview(PreparedAvatarPreviewGLData prepared)
        {
            isAvatarMode = true;
            ClearMeshPreviewSource();
            StopAnimation();
            previewMaterialMode = false;
            ++meshLoadCounter;

            avatarReferenceMeshSource = prepared.Mesh;
            avatarReferenceSourceVertices = prepared.SourceVertexData;
            avatarReferenceSourceIndices = prepared.SourceIndices;
            avatarReferenceMin = prepared.SourceMeshMin;
            avatarReferenceMax = prepared.SourceMeshMax;
            vertexData = prepared.IsAnimated && prepared.AnimatedMeshVertices != null && prepared.AnimatedMeshVertices.Length > 0
                ? prepared.AnimatedMeshVertices[0]
                : prepared.ReferenceMesh.Vertices;
            SetInitialPreviewFrame(prepared.AutoViewMatrix, prepared.ModelMatrixData);
            indiceData = prepared.ReferenceMesh.Indices;
            normalData = null;
            normal2Data = null;
            colorData = null;
            uvData = null;

            boneLinesVertexCount = prepared.BoneLinesCount;
            jointPointsVertexCount = prepared.JointPointsCount;

            if (prepared.IsAnimated && prepared.AnimationFrames != null)
            {
                animationFrames = prepared.AnimationFrames;
                animParentIndices = prepared.ParentIndices;
                animCurrentFrame = 0;
                animFps = prepared.Fps > 0 ? prepared.Fps : 30f;
                avatarReferenceBoneMatrices = prepared.BoneMatrices;
                avatarReferenceParentIndices = prepared.ParentIndices;
                animBoneLinesPerFrame = prepared.BoneLinesCount;
                animJointPointsPerFrame = prepared.JointPointsCount;
                animatedMeshVertices = prepared.AnimatedMeshVertices;

                lock (skeletonLock)
                {
                    pendingSkeletonVertices = prepared.SkeletonVertices;
                }

                vao = 0;
                RequestNextFrameRendering();

                animTimer = new global::Avalonia.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(1000.0 / animFps)
                };
                animTimer.Tick += OnAnimationTick;
                animTimer.Start();
                isAnimPlaying = true;
            }
            else
            {
                staticBonePositions = prepared.StaticBonePositions;
                staticParentIndices = prepared.ParentIndices;
                avatarReferenceBoneMatrices = null;
                avatarReferenceParentIndices = null;
                animatedMeshVertices = null;

                lock (skeletonLock)
                {
                    pendingSkeletonVertices = prepared.SkeletonVertices;
                }

                vao = 0;
                RequestNextFrameRendering();
            }
        }
        public void SetAvatar(Mesh m_Mesh, Vector3[] bonePositions, int[] parentIndices, string[]? boneNames = null)
        {
            isAvatarMode = true;
            ClearMeshPreviewSource();
            StopAnimation();
            previewMaterialMode = false;
            m_Mesh.EnsureProcessed();
            if (m_Mesh.m_VertexCount <= 0)
            {
                return;
            }

            staticBonePositions = bonePositions;
            staticParentIndices = parentIndices;

            int currentLoadId = ++meshLoadCounter;
            var avatarBonePositions = bonePositions;
            var m_Vertices = m_Mesh.m_Vertices;
            var m_VertexCount = m_Mesh.m_VertexCount;
            var m_Indices = m_Mesh.m_Indices;

            System.Threading.Tasks.Task.Run(() =>
            {
                if (m_Vertices == null || m_Vertices.Length == 0)
                {
                    return;
                }

                var localVertexData = ExtractMeshVertices(m_Vertices, m_VertexCount, out var min, out var max);
                var sourceMeshMin = min;
                var sourceMeshMax = max;
                var referenceMesh = PreviewMeshSimplifier.Build(localVertexData, m_Indices, sourceMeshMin, sourceMeshMax, previewMeshDensityPercent);
                var localIndiceData = referenceMesh.Indices;

                var skeletonVerts = new List<Vector3>();
                int boneLinesCount = 0;
                int jointPointsCount = 0;

                if (bonePositions != null && parentIndices != null)
                {
                    for (int i = 0; i < bonePositions.Length; i++)
                    {
                        ExpandBounds(bonePositions[i], ref min, ref max);
                    }

                    var verts = BuildSkeletonVertices(bonePositions, parentIndices, out boneLinesCount, out jointPointsCount);
                    skeletonVerts.AddRange(verts);
                }

                var localModelMatrixData = CreatePreviewModelMatrix(min, max);

                global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (currentLoadId != meshLoadCounter)
                    {
                        return;
                    }

                    var autoViewMatrix = CreateAvatarInitialViewMatrix(avatarBonePositions, boneNames);
                    avatarReferenceMeshSource = m_Mesh;
                    avatarReferenceSourceVertices = localVertexData;
                    avatarReferenceSourceIndices = m_Indices;
                    avatarReferenceMin = sourceMeshMin;
                    avatarReferenceMax = sourceMeshMax;
                    avatarReferenceBoneMatrices = null;
                    avatarReferenceParentIndices = null;
                    vertexData = referenceMesh.Vertices;
                    SetInitialPreviewFrame(autoViewMatrix, localModelMatrixData);
                    indiceData = localIndiceData;
                    normalData = null;
                    normal2Data = null;
                    colorData = null;
                    uvData = null;

                    boneLinesVertexCount = boneLinesCount;
                    jointPointsVertexCount = jointPointsCount;

                    lock (skeletonLock)
                    {
                        pendingSkeletonVertices = skeletonVerts.ToArray();
                    }
                    vao = 0;
                    RequestNextFrameRendering();
                });
            });
        }

        public void SetAnimatedAvatar(Mesh m_Mesh, Vector3[][] frames, Matrix4[][] boneMatrices, int[] parentIndices, float fps, string[]? boneNames = null)
        {
            isAvatarMode = true;
            ClearMeshPreviewSource();
            StopAnimation();
            previewMaterialMode = false;
            m_Mesh.EnsureProcessed();
            if (m_Mesh.m_VertexCount <= 0 || frames.Length == 0)
            {
                return;
            }

            animationFrames = frames;
            animParentIndices = parentIndices;
            animCurrentFrame = 0;
            animFps = fps > 0 ? fps : 30f;

            int currentLoadId = ++meshLoadCounter;
            var m_Vertices = m_Mesh.m_Vertices;
            var m_VertexCount = m_Mesh.m_VertexCount;
            var m_Indices = m_Mesh.m_Indices;

            System.Threading.Tasks.Task.Run(() =>
            {
                if (m_Vertices == null || m_Vertices.Length == 0)
                {
                    return;
                }

                var sourceVertexData = ExtractMeshVertices(m_Vertices, m_VertexCount, out var min, out var max);
                var sourceMeshMin = min;
                var sourceMeshMax = max;
                var referenceMesh = PreviewMeshSimplifier.Build(sourceVertexData, m_Indices, sourceMeshMin, sourceMeshMax, previewMeshDensityPercent);
                var localVertexData = referenceMesh.Vertices;
                var localIndiceData = referenceMesh.Indices;

                // Compute skinned mesh vertices for all frames of the animation
                var animMin = new Vector3(float.MaxValue);
                var animMax = new Vector3(float.MinValue);
                Vector3[][]? localAnimatedMeshVerts = BuildAnimatedAvatarReferenceVertices(m_Mesh, sourceVertexData, referenceMesh, boneMatrices, parentIndices, ref animMin, ref animMax, true);

                var firstFrameBones = frames[0];
                foreach (var frame in frames)
                {
                    foreach (var bonePosition in frame)
                    {
                        ExpandBounds(bonePosition, ref animMin, ref animMax);
                    }
                }

                if (localAnimatedMeshVerts == null || !IsFinitePoint(animMin) || !IsFinitePoint(animMax) || animMin.X > animMax.X)
                {
                    ExpandBounds(sourceMeshMin, ref animMin, ref animMax);
                    ExpandBounds(sourceMeshMax, ref animMin, ref animMax);
                }

                if (!IsFinitePoint(animMin) || !IsFinitePoint(animMax) || animMin.X > animMax.X)
                {
                    animMin = sourceMeshMin;
                    animMax = sourceMeshMax;
                }

                var skeletonVerts = BuildSkeletonVertices(firstFrameBones, parentIndices, out int boneLines, out int jointPoints);
                var localModelMatrixData = CreatePreviewModelMatrix(animMin, animMax);

                global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (currentLoadId != meshLoadCounter)
                    {
                        return;
                    }

                    var autoViewMatrix = CreateAvatarInitialViewMatrix(frames[0], boneNames);
                    avatarReferenceMeshSource = m_Mesh;
                    avatarReferenceSourceVertices = sourceVertexData;
                    avatarReferenceSourceIndices = m_Indices;
                    avatarReferenceMin = sourceMeshMin;
                    avatarReferenceMax = sourceMeshMax;
                    avatarReferenceBoneMatrices = boneMatrices;
                    avatarReferenceParentIndices = parentIndices;
                    vertexData = localAnimatedMeshVerts != null && localAnimatedMeshVerts.Length > 0
                        ? localAnimatedMeshVerts[0]
                        : localVertexData;
                    SetInitialPreviewFrame(autoViewMatrix, localModelMatrixData);
                    indiceData = localIndiceData;
                    normalData = null;
                    normal2Data = null;
                    colorData = null;
                    uvData = null;

                    animBoneLinesPerFrame = boneLines;
                    animJointPointsPerFrame = jointPoints;
                    boneLinesVertexCount = boneLines;
                    jointPointsVertexCount = jointPoints;

                    animatedMeshVertices = localAnimatedMeshVerts;

                    lock (skeletonLock)
                    {
                        pendingSkeletonVertices = skeletonVerts;
                    }
                    vao = 0;
                    RequestNextFrameRendering();

                    animTimer = new global::Avalonia.Threading.DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(1000.0 / animFps)
                    };
                    animTimer.Tick += OnAnimationTick;
                    animTimer.Start();
                    isAnimPlaying = true;
                });
            });
        }

        public void PlayAnimation()
        {
            if (animationFrames == null || animTimer == null)
            {
                return;
            }

            if (!isAnimPlaying)
            {
                animTimer.Start();
                isAnimPlaying = true;
            }
        }

        public void PauseAnimation()
        {
            if (animTimer != null && isAnimPlaying)
            {
                animTimer.Stop();
                isAnimPlaying = false;
            }
        }

        public void RestartAnimation()
        {
            if (animationFrames == null)
            {
                return;
            }

            animCurrentFrame = 0;
            if (animTimer != null && !isAnimPlaying)
            {
                animTimer.Start();
                isAnimPlaying = true;
            }
            OnAnimationTick(null, EventArgs.Empty);
        }

        public void StopAnimation()
        {
            if (animTimer != null)
            {
                animTimer.Stop();
                animTimer.Tick -= OnAnimationTick;
                animTimer = null;
            }
            animationFrames = null;
            animParentIndices = null;
            animCurrentFrame = 0;
            isAnimPlaying = false;
            animatedMeshVertices = null;
            staticBonePositions = null;
            staticParentIndices = null;
            avatarReferenceBoneMatrices = null;
            avatarReferenceParentIndices = null;

            lock (meshLock)
            {
                pendingMeshVertices = null;
            }

            lock (skeletonLock)
            {
                pendingSkeletonVertices = null;
            }
        }

        private void ClearAvatarPreviewState()
        {
            isAvatarMode = false;
            StopAnimation();
            animatedMeshVertices = null;
            avatarReferenceMeshSource = null;
            avatarReferenceSourceVertices = null;
            avatarReferenceSourceIndices = null;
            avatarReferenceBoneMatrices = null;
            avatarReferenceParentIndices = null;
            boneLinesVertexCount = 0;
            jointPointsVertexCount = 0;
            animBoneLinesPerFrame = 0;
            animJointPointsPerFrame = 0;

            lock (skeletonLock)
            {
                pendingSkeletonVertices = null;
            }

            lock (meshLock)
            {
                pendingMeshVertices = null;
            }
        }

        private static Vector3[][]? BuildAnimatedAvatarReferenceVertices(Mesh mesh, Vector3[] sourceVertexData, PreviewMeshGeometry referenceMesh, Matrix4[][] boneMatrices, int[] parentIndices, ref Vector3 min, ref Vector3 max, bool expandBounds)
        {
            if (referenceMesh.SourceVertexIndices.Length == 0
                || mesh.m_Skin == null
                || mesh.m_Skin.Length < sourceVertexData.Length
                || mesh.m_BindPose == null
                || mesh.m_BindPose.Length < parentIndices.Length
                || boneMatrices.Length == 0)
            {
                return null;
            }

            var localAnimatedMeshVerts = new Vector3[boneMatrices.Length][];
            for (int f = 0; f < boneMatrices.Length; f++)
            {
                var frameVerts = new Vector3[referenceMesh.SourceVertexIndices.Length];
                var frameMatrices = boneMatrices[f];
                var skinningMatrices = new Matrix4[frameMatrices.Length];

                for (int b = 0; b < frameMatrices.Length; b++)
                {
                    if (b < mesh.m_BindPose.Length)
                    {
                        var bp = mesh.m_BindPose[b];
                        var otkMat = new Matrix4(
                            bp.M00, bp.M01, bp.M02, bp.M03,
                            bp.M10, bp.M11, bp.M12, bp.M13,
                            bp.M20, bp.M21, bp.M22, bp.M23,
                            bp.M30, bp.M31, bp.M32, bp.M33
                        );
                        skinningMatrices[b] = otkMat * frameMatrices[b];
                    }
                    else
                    {
                        skinningMatrices[b] = Matrix4.Identity;
                    }
                }

                for (int v = 0; v < referenceMesh.SourceVertexIndices.Length; v++)
                {
                    int sourceVertexIndex = referenceMesh.SourceVertexIndices[v];
                    var sourceVertex = sourceVertexData[sourceVertexIndex];
                    var skin = mesh.m_Skin[sourceVertexIndex];
                    Vector3 skinnedPos = Vector3.Zero;
                    float totalWeight = 0f;

                    var weights = skin?.weight;
                    var boneIndices = skin?.boneIndex;
                    int influenceCount = weights != null && boneIndices != null
                        ? Math.Min(weights.Length, boneIndices.Length)
                        : 0;
                    for (int j = 0; j < influenceCount; j++)
                    {
                        float w = weights![j];
                        if (w <= 0)
                        {
                            continue;
                        }

                        int bIdx = boneIndices![j];
                        if (bIdx >= 0 && bIdx < skinningMatrices.Length)
                        {
                            var posB = Vector3.TransformPosition(sourceVertex, skinningMatrices[bIdx]);
                            skinnedPos += posB * w;
                            totalWeight += w;
                        }
                    }

                    frameVerts[v] = totalWeight > 0f ? skinnedPos / totalWeight : sourceVertex;
                    if (expandBounds)
                    {
                        ExpandBounds(frameVerts[v], ref min, ref max);
                    }
                }

                localAnimatedMeshVerts[f] = frameVerts;
            }

            return localAnimatedMeshVerts;
        }

        private void RebuildAvatarReferenceMeshPreview()
        {
            if (!isAvatarMode || avatarReferenceSourceVertices == null || avatarReferenceSourceIndices == null)
            {
                return;
            }

            int currentLoadId = ++meshLoadCounter;
            var sourceVertices = avatarReferenceSourceVertices;
            var sourceIndices = avatarReferenceSourceIndices;
            var sourceMesh = avatarReferenceMeshSource;
            var boneMatrices = avatarReferenceBoneMatrices;
            var parentIndices = avatarReferenceParentIndices;
            var min = avatarReferenceMin;
            var max = avatarReferenceMax;
            var densityPercent = previewMeshDensityPercent;

            System.Threading.Tasks.Task.Run(() =>
            {
                var referenceMesh = PreviewMeshSimplifier.Build(sourceVertices, sourceIndices, min, max, densityPercent);
                Vector3[][]? rebuiltAnimatedVertices = null;

                if (sourceMesh != null && boneMatrices != null && parentIndices != null)
                {
                    var ignoredMin = min;
                    var ignoredMax = max;
                    rebuiltAnimatedVertices = BuildAnimatedAvatarReferenceVertices(sourceMesh, sourceVertices, referenceMesh, boneMatrices, parentIndices, ref ignoredMin, ref ignoredMax, false);
                }

                global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (currentLoadId != meshLoadCounter || !isAvatarMode)
                    {
                        return;
                    }

                    vertexData = rebuiltAnimatedVertices != null && animCurrentFrame < rebuiltAnimatedVertices.Length
                        ? rebuiltAnimatedVertices[animCurrentFrame]
                        : referenceMesh.Vertices;
                    indiceData = referenceMesh.Indices;
                    animatedMeshVertices = rebuiltAnimatedVertices;

                    lock (meshLock)
                    {
                        pendingMeshVertices = rebuiltAnimatedVertices != null && animCurrentFrame < rebuiltAnimatedVertices.Length
                            ? rebuiltAnimatedVertices[animCurrentFrame]
                            : null;
                    }

                    vao = 0;
                    RequestNextFrameRendering();
                });
            });
        }

        private void OnAnimationTick(object? sender, EventArgs e)
        {
            if (animationFrames == null || animParentIndices == null || animationFrames.Length == 0)
            {
                return;
            }

            animCurrentFrame = (animCurrentFrame + 1) % animationFrames.Length;
            var bonePositions = animationFrames[animCurrentFrame];
            var skeletonVerts = BuildSkeletonVertices(bonePositions, animParentIndices, out int boneLines, out int jointPoints);

            boneLinesVertexCount = boneLines;
            jointPointsVertexCount = jointPoints;

            lock (skeletonLock)
            {
                pendingSkeletonVertices = skeletonVerts;
            }

            if (animatedMeshVertices != null && animCurrentFrame < animatedMeshVertices.Length)
            {
                lock (meshLock)
                {
                    pendingMeshVertices = animatedMeshVertices[animCurrentFrame];
                }
            }

            RequestNextFrameRendering();
            AnimationFrameChanged?.Invoke(animCurrentFrame, animationFrames.Length);
        }

        private void UpdateSkeletonVertices()
        {
            Vector3[]? bones = null;
            int[]? parents = null;

            if (animationFrames != null && animParentIndices != null && animCurrentFrame < animationFrames.Length)
            {
                bones = animationFrames[animCurrentFrame];
                parents = animParentIndices;
            }
            else if (staticBonePositions != null && staticParentIndices != null)
            {
                bones = staticBonePositions;
                parents = staticParentIndices;
            }

            if (bones != null && parents != null)
            {
                var skeletonVerts = BuildSkeletonVertices(bones, parents, out int boneLines, out int jointPoints);
                boneLinesVertexCount = boneLines;
                jointPointsVertexCount = jointPoints;
                lock (skeletonLock)
                {
                    pendingSkeletonVertices = skeletonVerts;
                }
            }
        }

        private static void AddOctahedronBone(List<Vector3> verts, Vector3 b, Vector3 a, float scale)
        {
            Vector3 v = a - b;
            float len = v.Length;
            if (len < 1e-5f)
            {
                return;
            }

            Vector3 temp = Math.Abs(v.X) < Math.Abs(v.Y) ? Vector3.UnitX : Vector3.UnitY;
            Vector3 u1 = Vector3.Normalize(Vector3.Cross(v, temp));
            Vector3 u2 = Vector3.Normalize(Vector3.Cross(v, u1));

            float thickness = Math.Min(len * 0.15f, 0.02f * scale);
            if (thickness < 1e-5f)
            {
                thickness = 1e-5f;
            }

            Vector3 c = b + v * 0.2f;

            Vector3 p1 = c + u1 * thickness;
            Vector3 p2 = c + u2 * thickness;
            Vector3 p3 = c - u1 * thickness;
            Vector3 p4 = c - u2 * thickness;

            verts.Add(b); verts.Add(p1);
            verts.Add(b); verts.Add(p2);
            verts.Add(b); verts.Add(p3);
            verts.Add(b); verts.Add(p4);

            verts.Add(p1); verts.Add(p2);
            verts.Add(p2); verts.Add(p3);
            verts.Add(p3); verts.Add(p4);
            verts.Add(p4); verts.Add(p1);

            verts.Add(p1); verts.Add(a);
            verts.Add(p2); verts.Add(a);
            verts.Add(p3); verts.Add(a);
            verts.Add(p4); verts.Add(a);
        }

        private static void AddOctahedronJoint(List<Vector3> verts, Vector3 p, float scale)
        {
            float r = 0.005f * scale;
            if (r < 1e-5f)
            {
                r = 1e-5f;
            }

            Vector3 px1 = p + Vector3.UnitX * r;
            Vector3 px2 = p - Vector3.UnitX * r;
            Vector3 py1 = p + Vector3.UnitY * r;
            Vector3 py2 = p - Vector3.UnitY * r;
            Vector3 pz1 = p + Vector3.UnitZ * r;
            Vector3 pz2 = p - Vector3.UnitZ * r;

            verts.Add(px1); verts.Add(py1);
            verts.Add(py1); verts.Add(px2);
            verts.Add(px2); verts.Add(py2);
            verts.Add(py2); verts.Add(px1);

            verts.Add(px1); verts.Add(pz1);
            verts.Add(py1); verts.Add(pz1);
            verts.Add(px2); verts.Add(pz1);
            verts.Add(py2); verts.Add(pz1);

            verts.Add(px1); verts.Add(pz2);
            verts.Add(py1); verts.Add(pz2);
            verts.Add(px2); verts.Add(pz2);
            verts.Add(py2); verts.Add(pz2);
        }

        private Vector3[] BuildSkeletonVertices(Vector3[] bonePositions, int[] parentIndices, out int boneLinesCount, out int jointPointsCount)
        {
            return BuildSkeletonVerticesStatic(bonePositions, parentIndices, boneScale, out boneLinesCount, out jointPointsCount);
        }

        private static Vector3[] BuildSkeletonVerticesStatic(Vector3[] bonePositions, int[] parentIndices, float scale, out int boneLinesCount, out int jointPointsCount)
        {
            var skeletonVerts = new List<Vector3>(bonePositions.Length * 48);
            boneLinesCount = 0;
            jointPointsCount = 0;

            for (int i = 0; i < bonePositions.Length; i++)
            {
                int pIdx = parentIndices[i];
                if (pIdx >= 0 && pIdx < bonePositions.Length)
                {
                    var a = bonePositions[i];
                    var b = bonePositions[pIdx];
                    AddOctahedronBone(skeletonVerts, b, a, scale);
                    boneLinesCount += 24;
                }
            }

            for (int i = 0; i < bonePositions.Length; i++)
            {
                AddOctahedronJoint(skeletonVerts, bonePositions[i], scale);
                jointPointsCount += 24;
            }

            return skeletonVerts.ToArray();
        }

        private static string NormalizeBoneName(string name)
        {
            return name.Replace("\\", "/", StringComparison.Ordinal)
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault()
                ?.Replace("_", string.Empty, StringComparison.Ordinal)
                .Replace("-", string.Empty, StringComparison.Ordinal)
                .Replace(" ", string.Empty, StringComparison.Ordinal)
                .ToLowerInvariant() ?? string.Empty;
        }

        private static int FindBoneIndex(string[]? boneNames, params string[] candidates)
        {
            if (boneNames == null)
            {
                return -1;
            }

            for (int c = 0; c < candidates.Length; c++)
            {
                string candidate = candidates[c].ToLowerInvariant();
                for (int i = 0; i < boneNames.Length; i++)
                {
                    if (NormalizeBoneName(boneNames[i]).Contains(candidate, StringComparison.Ordinal))
                    {
                        return i;
                    }
                }
            }

            return -1;
        }

        private static bool TryGetBonePosition(Vector3[] bonePositions, string[]? boneNames, out Vector3 position, params string[] candidates)
        {
            int index = FindBoneIndex(boneNames, candidates);
            if (index >= 0 && index < bonePositions.Length)
            {
                position = bonePositions[index];
                return true;
            }

            position = Vector3.Zero;
            return false;
        }

        private static bool TryNormalize(Vector3 value, out Vector3 normalized)
        {
            if (value.LengthSquared > 1e-8f)
            {
                normalized = Vector3.Normalize(value);
                return true;
            }

            normalized = Vector3.Zero;
            return false;
        }

        private static Matrix4 CreateAvatarInitialViewMatrix(Vector3[] bonePositions, string[]? boneNames)
        {
            if (TryGetBonePosition(bonePositions, boneNames, out var hips, "hips", "pelvis", "bip001pelvis")
                && TryGetBonePosition(bonePositions, boneNames, out var head, "head", "neck")
                && TryNormalize(head - hips, out var up))
            {
                bool hasLeft = TryGetBonePosition(bonePositions, boneNames, out var left, "leftshoulder", "leftupperarm", "leftarm", "lefthand", "lshoulder", "lupperarm");
                bool hasRight = TryGetBonePosition(bonePositions, boneNames, out var right, "rightshoulder", "rightupperarm", "rightarm", "righthand", "rshoulder", "rupperarm");

                if (hasLeft && hasRight && TryNormalize(right - left, out var side))
                {
                    var front = Vector3.Cross(side, up);
                    if (TryNormalize(front, out front))
                    {
                        var view = Matrix4.LookAt(front, Vector3.Zero, up);
                        view.Row3 = new Vector4(0, 0, 0, 1);
                        return view;
                    }
                }

                var fallbackFront = Math.Abs(Vector3.Dot(up, Vector3.UnitZ)) > 0.9f ? Vector3.UnitY : Vector3.UnitZ;
                var fallbackView = Matrix4.LookAt(fallbackFront, Vector3.Zero, up);
                fallbackView.Row3 = new Vector4(0, 0, 0, 1);
                return fallbackView;
            }

            return Matrix4.Identity;
        }
    }
}
