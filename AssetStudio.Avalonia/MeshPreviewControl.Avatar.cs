using System;
using System.Collections.Generic;
using System.Linq;

namespace AssetStudio.Avalonia
{
    using Vector2 = OpenTK.Mathematics.Vector2;
    using Vector3 = OpenTK.Mathematics.Vector3;
    using Vector4 = OpenTK.Mathematics.Vector4;
    using Matrix3 = OpenTK.Mathematics.Matrix3;
    using Matrix4 = OpenTK.Mathematics.Matrix4;

    public partial class MeshPreviewControl
    {
        private const long MaxPrecomputedNormalVertexFrames = 8_000_000;
        internal const int MaxCompositeAvatarParts = 256;
        internal const int MaxCompositeAvatarVertices = 500_000;
        internal const int MaxCompositeAvatarIndices = 2_000_000;

        internal sealed class AvatarPreviewPartSpec
        {
            public required Mesh Mesh { get; init; }
            public string? MeshAssetId { get; init; }
            public string? RendererAssetId { get; init; }
            public string? RendererType { get; init; }
            public string? GameObjectName { get; init; }
            public bool IsSkinned { get; init; }
            public int[]? BoneToSkeletonNodeMap { get; init; }
            public int RigidAttachNodeIndex { get; init; } = -1;
            public Matrix4 RigidLocalOffset { get; init; } = Matrix4.Identity;
        }

        internal sealed class PreparedAvatarPartRuntime
        {
            public required Mesh Mesh { get; init; }
            public string? MeshAssetId { get; init; }
            public string? RendererAssetId { get; init; }
            public required bool IsSkinned { get; init; }
            public required Vector3[] SourceVertices { get; init; }
            public Vector3[]? SourceNormals { get; init; }
            public required List<uint> SourceIndices { get; init; }
            public required Vector3 SourceMin { get; init; }
            public required Vector3 SourceMax { get; init; }
            public required PreviewMeshGeometry ReferenceMesh { get; init; }
            public int[]? BoneToSkeletonNodeMap { get; init; }
            public int RigidAttachNodeIndex { get; init; } = -1;
            public Matrix4 RigidLocalOffset { get; init; } = Matrix4.Identity;
            public int VertexOffset { get; init; }
            public int VertexCount { get; init; }
            public int IndexOffset { get; init; }
            public int IndexCount { get; init; }
            public int SubMeshOffset { get; init; }
            public int SubMeshCount { get; init; }
        }

        internal sealed class PreparedAvatarPreviewGLData
        {
            public Mesh? Mesh { get; init; }
            public required Vector3[] SourceVertexData { get; init; }
            public required List<uint> SourceIndices { get; init; }
            public required Vector3 SourceMeshMin { get; init; }
            public required Vector3 SourceMeshMax { get; init; }
            public required PreviewMeshGeometry ReferenceMesh { get; init; }
            public Vector3[]? SourceNormals { get; init; }
            public Vector3[]? Normals { get; init; }
            public Vector3[] CalculatedNormals { get; init; } = Array.Empty<Vector3>();
            public Vector4[] Colors { get; init; } = Array.Empty<Vector4>();
            public Vector2[]? Uvs { get; init; }
            public int[]? SubMeshIndexCounts { get; init; }
            public required Vector3[] SkeletonVertices { get; init; }
            public required int BoneLinesCount { get; init; }
            public required int JointPointsCount { get; init; }
            public required Matrix4 ModelMatrixData { get; init; }
            public required Matrix4 AutoViewMatrix { get; init; }
            public required int[] ParentIndices { get; init; }
            public Vector3[]? StaticBonePositions { get; init; }
            public Matrix4[]? StaticSkeletonWorldMatrices { get; init; }
            public Vector3[][]? AnimationFrames { get; init; }
            public Matrix4[][]? BoneMatrices { get; init; }
            public Vector3[][]? AnimatedMeshVertices { get; init; }
            public Vector3[][]? AnimatedMeshNormals { get; init; }
            public IReadOnlyList<PreparedAvatarPartRuntime>? CompositeParts { get; init; }
            public float Fps { get; init; } = 30f;
            public bool IsAnimated => AnimationFrames != null && AnimationFrames.Length > 0;
            public bool IsComposite => CompositeParts != null && CompositeParts.Count > 0;
            public bool IsSkeletonOnly => ReferenceMesh.Indices.Length == 0 && SkeletonVertices.Length > 0;
            public int PartCount => CompositeParts?.Count ?? (Mesh != null ? 1 : 0);
        }

        private static void BuildReferenceSurfaceAttributes(
            Mesh m_Mesh,
            PreviewMeshGeometry referenceMesh,
            Vector3[]? initialVertices,
            out Vector3[]? sourceNormals,
            out Vector3[]? normals,
            out Vector3[] calculatedNormals,
            out Vector4[] colors,
            out Vector2[]? uvs)
        {
            sourceNormals = ExtractMeshNormals(m_Mesh.m_Normals, m_Mesh.m_VertexCount);
            normals = RemapVector3Data(sourceNormals, referenceMesh.SourceVertexIndices);
            var sourceColors = ExtractMeshColors(m_Mesh.m_Colors, m_Mesh.m_VertexCount);
            colors = RemapVector4Data(sourceColors, referenceMesh.SourceVertexIndices) ?? sourceColors;
            var sourceUvs = BuildPreviewUvData(m_Mesh.m_UV0, m_Mesh.m_VertexCount);
            uvs = RemapVector2Data(sourceUvs, referenceMesh.SourceVertexIndices);
            calculatedNormals = BuildCalculatedNormals(initialVertices ?? referenceMesh.Vertices, referenceMesh.Indices);
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
            var subMeshIndexCounts = BuildSubMeshIndexCounts(m_Mesh, m_Indices.Count);
            var referenceMesh = precomputedReferenceMesh
                ?? PreviewMeshSimplifier.Build(localVertexData, m_Indices, sourceMeshMin, sourceMeshMax, densityPercent, subMeshIndexCounts);
            BuildReferenceSurfaceAttributes(
                m_Mesh,
                referenceMesh,
                referenceMesh.Vertices,
                out var sourceNormals,
                out var normals,
                out var calculatedNormals,
                out var colors,
                out var uvs);

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
                SourceNormals = sourceNormals,
                Normals = normals,
                CalculatedNormals = calculatedNormals,
                Colors = colors,
                Uvs = uvs,
                SubMeshIndexCounts = referenceMesh.SubMeshIndexCounts ?? subMeshIndexCounts,
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
            var subMeshIndexCounts = BuildSubMeshIndexCounts(m_Mesh, m_Indices.Count);
            var referenceMesh = precomputedReferenceMesh
                ?? PreviewMeshSimplifier.Build(sourceVertexData, m_Indices, sourceMeshMin, sourceMeshMax, densityPercent, subMeshIndexCounts);
            var sourceNormals = ExtractMeshNormals(m_Mesh.m_Normals, m_Mesh.m_VertexCount);
            if (sourceNormals == null && sourceVertexData.Length > 0 && m_Indices.Count >= 3)
            {
                sourceNormals = BuildCalculatedNormals(sourceVertexData, ExtractMeshIndices(m_Indices));
            }

            var animMin = new Vector3(float.MaxValue);
            var animMax = new Vector3(float.MinValue);
            Vector3[][]? localAnimatedMeshVerts = BuildAnimatedAvatarReferenceVerticesAndNormals(
                m_Mesh,
                sourceVertexData,
                sourceNormals,
                referenceMesh,
                boneMatrices,
                parentIndices,
                ref animMin,
                ref animMax,
                expandBounds: true,
                out var localAnimatedMeshNormals);

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

            var frame0Vertices = localAnimatedMeshVerts != null && localAnimatedMeshVerts.Length > 0
                ? localAnimatedMeshVerts[0]
                : referenceMesh.Vertices;
            BuildReferenceSurfaceAttributes(
                m_Mesh,
                referenceMesh,
                frame0Vertices,
                out _,
                out var remappedNormals,
                out var calculatedNormals,
                out var colors,
                out var uvs);

            Vector3[]? initialSkinnedNormals = null;
            if (localAnimatedMeshNormals != null && localAnimatedMeshNormals.Length > 0)
            {
                initialSkinnedNormals = localAnimatedMeshNormals[0];
            }
            else if (sourceNormals != null && boneMatrices.Length > 0 && localAnimatedMeshVerts != null)
            {
                initialSkinnedNormals = SkinFrameNormals(m_Mesh, sourceNormals, referenceMesh.SourceVertexIndices, boneMatrices[0]);
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
                SourceNormals = sourceNormals,
                Normals = initialSkinnedNormals ?? remappedNormals,
                CalculatedNormals = calculatedNormals,
                Colors = colors,
                Uvs = uvs,
                SubMeshIndexCounts = referenceMesh.SubMeshIndexCounts ?? subMeshIndexCounts,
                SkeletonVertices = skeletonVerts,
                BoneLinesCount = boneLines,
                JointPointsCount = jointPoints,
                ModelMatrixData = localModelMatrixData,
                AutoViewMatrix = autoViewMatrix,
                ParentIndices = parentIndices,
                AnimationFrames = frames,
                BoneMatrices = boneMatrices,
                AnimatedMeshVertices = localAnimatedMeshVerts,
                AnimatedMeshNormals = localAnimatedMeshNormals,
                Fps = fps > 0 ? fps : 30f
            };
        }

        internal static PreparedAvatarPreviewGLData? PrepareSkeletonOnlyAvatarGLData(
            Vector3[] bonePositions,
            int[] parentIndices,
            string[]? boneNames,
            float boneScale)
        {
            if (bonePositions == null || parentIndices == null || bonePositions.Length < 2)
            {
                return null;
            }

            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            var finiteCount = 0;
            for (var i = 0; i < bonePositions.Length; i++)
            {
                if (IsFinitePoint(bonePositions[i]))
                {
                    ExpandBounds(bonePositions[i], ref min, ref max);
                    finiteCount++;
                }
            }

            if (finiteCount < 2 || !IsFinitePoint(min) || !IsFinitePoint(max))
            {
                return null;
            }

            var skeletonVerts = BuildSkeletonVerticesStatic(bonePositions, parentIndices, boneScale, out int boneLinesCount, out int jointPointsCount);
            if (skeletonVerts.Length == 0)
            {
                return null;
            }

            var modelMatrix = CreatePreviewModelMatrix(min, max);
            var autoViewMatrix = CreateAvatarInitialViewMatrix(bonePositions, boneNames);
            var emptyRef = new PreviewMeshGeometry(Array.Empty<Vector3>(), Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>());

            return new PreparedAvatarPreviewGLData
            {
                Mesh = null,
                SourceVertexData = Array.Empty<Vector3>(),
                SourceIndices = new List<uint>(),
                SourceMeshMin = min,
                SourceMeshMax = max,
                ReferenceMesh = emptyRef,
                SourceNormals = null,
                Normals = null,
                CalculatedNormals = Array.Empty<Vector3>(),
                Colors = Array.Empty<Vector4>(),
                Uvs = null,
                SubMeshIndexCounts = Array.Empty<int>(),
                SkeletonVertices = skeletonVerts,
                BoneLinesCount = boneLinesCount,
                JointPointsCount = jointPointsCount,
                ModelMatrixData = modelMatrix,
                AutoViewMatrix = autoViewMatrix,
                ParentIndices = parentIndices,
                StaticBonePositions = bonePositions
            };
        }

        private static bool BuildCompositePartRuntimes(
            IReadOnlyList<AvatarPreviewPartSpec> partSpecs,
            float densityPercent,
            out List<PreparedAvatarPartRuntime> parts,
            out int totalVertexCount,
            out int[] combinedIndices,
            out Vector4[] combinedColors,
            out Vector2[]? combinedUvs,
            out int[] combinedSubMeshCounts,
            out Vector3 rawMin,
            out Vector3 rawMax)
        {
            parts = new List<PreparedAvatarPartRuntime>();
            totalVertexCount = 0;
            combinedIndices = Array.Empty<int>();
            combinedColors = Array.Empty<Vector4>();
            combinedUvs = null;
            combinedSubMeshCounts = Array.Empty<int>();
            rawMin = new Vector3(float.MaxValue);
            rawMax = new Vector3(float.MinValue);

            if (partSpecs == null || partSpecs.Count == 0)
            {
                return false;
            }

            var seenPartKeys = new HashSet<string>(StringComparer.Ordinal);
            var partUvsList = new List<Vector2[]?>();
            var partColorsList = new List<Vector4[]>();
            var subMeshCounts = new List<int>();
            var totalIndexCount = 0;
            var anyHasUvs = false;

            for (var specIdx = 0; specIdx < partSpecs.Count && parts.Count < MaxCompositeAvatarParts; specIdx++)
            {
                var spec = partSpecs[specIdx];
                var mesh = spec?.Mesh;
                if (mesh == null)
                {
                    continue;
                }

                // Deduplicate identical (MeshAssetId, RendererAssetId) pairs while preserving distinct instanced renderers.
                if (!string.IsNullOrWhiteSpace(spec!.RendererAssetId))
                {
                    var meshIdKey = !string.IsNullOrWhiteSpace(spec.MeshAssetId)
                        ? spec.MeshAssetId
                        : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(mesh).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    var dedupKey = $"{meshIdKey}\u001f{spec.RendererAssetId}";
                    if (!seenPartKeys.Add(dedupKey))
                    {
                        continue;
                    }
                }

                mesh.EnsureProcessed();
                if (mesh.m_VertexCount <= 0
                    || mesh.m_Vertices == null
                    || mesh.m_Vertices.Length == 0
                    || mesh.m_Indices == null
                    || mesh.m_Indices.Count < 3)
                {
                    continue;
                }

                if (parts.Count > 0
                    && (totalVertexCount + mesh.m_VertexCount > MaxCompositeAvatarVertices
                        || totalIndexCount + mesh.m_Indices.Count > MaxCompositeAvatarIndices))
                {
                    continue;
                }

                var sourceVerts = ExtractMeshVertices(mesh.m_Vertices, mesh.m_VertexCount, out var partMin, out var partMax);
                var mIndices = mesh.m_Indices;
                var partSubMeshCounts = BuildSubMeshIndexCounts(mesh, mIndices.Count) ?? new[] { mIndices.Count };
                var referenceMesh = PreviewMeshSimplifier.Build(sourceVerts, mIndices, partMin, partMax, densityPercent, partSubMeshCounts);
                if (referenceMesh.Vertices.Length == 0 || referenceMesh.Indices.Length == 0)
                {
                    continue;
                }

                var sourceNormals = ExtractMeshNormals(mesh.m_Normals, mesh.m_VertexCount);
                if (sourceNormals == null && sourceVerts.Length > 0 && mIndices.Count >= 3)
                {
                    sourceNormals = BuildCalculatedNormals(sourceVerts, ExtractMeshIndices(mIndices));
                }

                var sourceColors = ExtractMeshColors(mesh.m_Colors, mesh.m_VertexCount);
                var remappedColors = RemapVector4Data(sourceColors, referenceMesh.SourceVertexIndices) ?? ExtractMeshColors(null, referenceMesh.Vertices.Length);
                var sourceUvs = BuildPreviewUvData(mesh.m_UV0, mesh.m_VertexCount);
                var remappedUvs = RemapVector2Data(sourceUvs, referenceMesh.SourceVertexIndices);
                if (remappedUvs != null)
                {
                    anyHasUvs = true;
                }

                var effectiveSubMeshCounts = referenceMesh.SubMeshIndexCounts is { Length: > 0 }
                    ? referenceMesh.SubMeshIndexCounts
                    : new[] { referenceMesh.Indices.Length };

                var isSkinned = spec.IsSkinned
                    && mesh.m_BindPose is { Length: > 0 }
                    && mesh.m_Skin is { Length: > 0 }
                    && mesh.m_Skin.Length >= sourceVerts.Length;

                var runtime = new PreparedAvatarPartRuntime
                {
                    Mesh = mesh,
                    MeshAssetId = spec.MeshAssetId,
                    RendererAssetId = spec.RendererAssetId,
                    IsSkinned = isSkinned,
                    SourceVertices = sourceVerts,
                    SourceNormals = sourceNormals,
                    SourceIndices = mIndices,
                    SourceMin = partMin,
                    SourceMax = partMax,
                    ReferenceMesh = referenceMesh,
                    BoneToSkeletonNodeMap = isSkinned ? spec.BoneToSkeletonNodeMap : null,
                    RigidAttachNodeIndex = isSkinned ? -1 : spec.RigidAttachNodeIndex,
                    RigidLocalOffset = spec.RigidLocalOffset,
                    VertexOffset = totalVertexCount,
                    VertexCount = referenceMesh.Vertices.Length,
                    IndexOffset = totalIndexCount,
                    IndexCount = referenceMesh.Indices.Length,
                    SubMeshOffset = subMeshCounts.Count,
                    SubMeshCount = effectiveSubMeshCounts.Length
                };

                parts.Add(runtime);
                partColorsList.Add(remappedColors);
                partUvsList.Add(remappedUvs);
                subMeshCounts.AddRange(effectiveSubMeshCounts);
                totalVertexCount += runtime.VertexCount;
                totalIndexCount += runtime.IndexCount;
                ExpandBounds(partMin, ref rawMin, ref rawMax);
                ExpandBounds(partMax, ref rawMin, ref rawMax);
            }

            if (parts.Count == 0 || totalVertexCount <= 0 || totalIndexCount <= 0)
            {
                return false;
            }

            combinedIndices = new int[totalIndexCount];
            combinedColors = new Vector4[totalVertexCount];
            combinedUvs = anyHasUvs ? new Vector2[totalVertexCount] : null;
            for (var p = 0; p < parts.Count; p++)
            {
                var part = parts[p];
                Array.Copy(partColorsList[p], 0, combinedColors, part.VertexOffset, part.VertexCount);
                if (combinedUvs != null && partUvsList[p] != null)
                {
                    Array.Copy(partUvsList[p]!, 0, combinedUvs, part.VertexOffset, part.VertexCount);
                }

                var srcIndices = part.ReferenceMesh.Indices;
                var vOff = part.VertexOffset;
                var iOff = part.IndexOffset;
                for (var i = 0; i < srcIndices.Length; i++)
                {
                    combinedIndices[iOff + i] = srcIndices[i] + vOff;
                }
            }

            combinedSubMeshCounts = subMeshCounts.ToArray();
            if (!IsFinitePoint(rawMin) || !IsFinitePoint(rawMax))
            {
                rawMin = Vector3.Zero;
                rawMax = Vector3.Zero;
            }

            return true;
        }

        private static void TransformCompositePartPose(
            PreparedAvatarPartRuntime part,
            Matrix4[]? skeletonWorldMatrices,
            Vector3[] destVertices,
            Vector3[]? destNormals,
            ref Vector3 min,
            ref Vector3 max,
            bool expandBounds)
        {
            var refSourceIndices = part.ReferenceMesh.SourceVertexIndices;
            var sourceVerts = part.SourceVertices;
            var sourceNorms = part.SourceNormals;
            var vOff = part.VertexOffset;

            if (part.IsSkinned
                && part.Mesh.m_BindPose is { Length: > 0 } bindPoses
                && part.Mesh.m_Skin is { Length: > 0 } skinWeights
                && skinWeights.Length >= sourceVerts.Length)
            {
                var boneCount = bindPoses.Length;
                var skinningMatrices = new Matrix4[boneCount];
                var normalMatrices = destNormals != null ? new Matrix3[boneCount] : null;
                var boneMap = part.BoneToSkeletonNodeMap;

                for (var b = 0; b < boneCount; b++)
                {
                    var skelIdx = boneMap != null
                        ? (b < boneMap.Length ? boneMap[b] : -1)
                        : b;
                    if (skeletonWorldMatrices != null && skelIdx >= 0 && skelIdx < skeletonWorldMatrices.Length)
                    {
                        var bp = bindPoses[b];
                        var otkMat = new Matrix4(
                            bp.M00, bp.M01, bp.M02, bp.M03,
                            bp.M10, bp.M11, bp.M12, bp.M13,
                            bp.M20, bp.M21, bp.M22, bp.M23,
                            bp.M30, bp.M31, bp.M32, bp.M33);
                        var skinMat = otkMat * skeletonWorldMatrices[skelIdx];
                        skinningMatrices[b] = skinMat;
                        if (normalMatrices != null)
                        {
                            normalMatrices[b] = ComputeNormalMatrix(skinMat);
                        }
                    }
                    else
                    {
                        skinningMatrices[b] = Matrix4.Identity;
                        if (normalMatrices != null)
                        {
                            normalMatrices[b] = Matrix3.Identity;
                        }
                    }
                }

                for (var v = 0; v < refSourceIndices.Length; v++)
                {
                    var srcIdx = refSourceIndices[v];
                    var srcPos = sourceVerts[srcIdx];
                    var srcNorm = sourceNorms != null && srcIdx < sourceNorms.Length ? sourceNorms[srcIdx] : Vector3.UnitY;
                    var skin = skinWeights[srcIdx];
                    var skinnedPos = Vector3.Zero;
                    var skinnedNorm = Vector3.Zero;
                    var totalWeight = 0f;

                    var weights = skin?.weight;
                    var boneIndices = skin?.boneIndex;
                    var influenceCount = weights != null && boneIndices != null
                        ? Math.Min(weights.Length, boneIndices.Length)
                        : 0;
                    for (var j = 0; j < influenceCount; j++)
                    {
                        var w = weights![j];
                        if (w <= 0f)
                        {
                            continue;
                        }

                        var bIdx = boneIndices![j];
                        if (bIdx >= 0 && bIdx < skinningMatrices.Length)
                        {
                            skinnedPos += Vector3.TransformPosition(srcPos, skinningMatrices[bIdx]) * w;
                            if (normalMatrices != null)
                            {
                                skinnedNorm += Vector3.TransformRow(srcNorm, normalMatrices[bIdx]) * w;
                            }
                            totalWeight += w;
                        }
                    }

                    var outPos = totalWeight > 0f ? skinnedPos / totalWeight : srcPos;
                    destVertices[vOff + v] = outPos;
                    if (destNormals != null)
                    {
                        if (IsFinitePoint(skinnedNorm) && skinnedNorm.LengthSquared > 1e-10f)
                        {
                            destNormals[vOff + v] = Vector3.Normalize(skinnedNorm);
                        }
                        else if (IsFinitePoint(srcNorm) && srcNorm.LengthSquared > 1e-10f)
                        {
                            destNormals[vOff + v] = Vector3.Normalize(srcNorm);
                        }
                        else
                        {
                            destNormals[vOff + v] = Vector3.UnitY;
                        }
                    }

                    if (expandBounds)
                    {
                        ExpandBounds(outPos, ref min, ref max);
                    }
                }

                return;
            }

            // Rigid part (MeshRenderer / non-skinned attachment): transform by localOffset * attachedNodeWorld (or static localOffset).
            Matrix4 rigidMat;
            if (skeletonWorldMatrices != null
                && part.RigidAttachNodeIndex >= 0
                && part.RigidAttachNodeIndex < skeletonWorldMatrices.Length)
            {
                rigidMat = part.RigidLocalOffset * skeletonWorldMatrices[part.RigidAttachNodeIndex];
            }
            else
            {
                rigidMat = part.RigidLocalOffset;
            }

            var rigidNormalMat = destNormals != null ? ComputeNormalMatrix(rigidMat) : Matrix3.Identity;
            for (var v = 0; v < refSourceIndices.Length; v++)
            {
                var srcIdx = refSourceIndices[v];
                var outPos = Vector3.TransformPosition(sourceVerts[srcIdx], rigidMat);
                destVertices[vOff + v] = outPos;

                if (destNormals != null)
                {
                    var srcNorm = sourceNorms != null && srcIdx < sourceNorms.Length ? sourceNorms[srcIdx] : Vector3.UnitY;
                    var outNorm = Vector3.TransformRow(srcNorm, rigidNormalMat);
                    if (IsFinitePoint(outNorm) && outNorm.LengthSquared > 1e-10f)
                    {
                        destNormals[vOff + v] = Vector3.Normalize(outNorm);
                    }
                    else if (IsFinitePoint(srcNorm) && srcNorm.LengthSquared > 1e-10f)
                    {
                        destNormals[vOff + v] = Vector3.Normalize(srcNorm);
                    }
                    else
                    {
                        destNormals[vOff + v] = Vector3.UnitY;
                    }
                }

                if (expandBounds)
                {
                    ExpandBounds(outPos, ref min, ref max);
                }
            }
        }

        internal static Vector3[]? SkinCompositeFrameNormals(
            IReadOnlyList<PreparedAvatarPartRuntime> parts,
            int totalVertexCount,
            Matrix4[]? frameSkeletonWorldMatrices,
            Vector3[]? destination = null)
        {
            if (parts == null || parts.Count == 0 || totalVertexCount <= 0)
            {
                return null;
            }

            var result = destination != null && destination.Length == totalVertexCount
                ? destination
                : new Vector3[totalVertexCount];

            for (var p = 0; p < parts.Count; p++)
            {
                var part = parts[p];
                var refSourceIndices = part.ReferenceMesh.SourceVertexIndices;
                var sourceNorms = part.SourceNormals;
                var vOff = part.VertexOffset;

                if (part.IsSkinned
                    && part.Mesh.m_BindPose is { Length: > 0 } bindPoses
                    && part.Mesh.m_Skin is { Length: > 0 } skinWeights
                    && skinWeights.Length >= part.SourceVertices.Length)
                {
                    var boneCount = bindPoses.Length;
                    var normalMatrices = new Matrix3[boneCount];
                    var boneMap = part.BoneToSkeletonNodeMap;
                    for (var b = 0; b < boneCount; b++)
                    {
                        var skelIdx = boneMap != null
                            ? (b < boneMap.Length ? boneMap[b] : -1)
                            : b;
                        if (frameSkeletonWorldMatrices != null && skelIdx >= 0 && skelIdx < frameSkeletonWorldMatrices.Length)
                        {
                            var bp = bindPoses[b];
                            var otkMat = new Matrix4(
                                bp.M00, bp.M01, bp.M02, bp.M03,
                                bp.M10, bp.M11, bp.M12, bp.M13,
                                bp.M20, bp.M21, bp.M22, bp.M23,
                                bp.M30, bp.M31, bp.M32, bp.M33);
                            normalMatrices[b] = ComputeNormalMatrix(otkMat * frameSkeletonWorldMatrices[skelIdx]);
                        }
                        else
                        {
                            normalMatrices[b] = Matrix3.Identity;
                        }
                    }

                    for (var v = 0; v < refSourceIndices.Length; v++)
                    {
                        var srcIdx = refSourceIndices[v];
                        var srcNorm = sourceNorms != null && srcIdx < sourceNorms.Length ? sourceNorms[srcIdx] : Vector3.UnitY;
                        var skin = skinWeights[srcIdx];
                        var skinnedNorm = Vector3.Zero;
                        var weights = skin?.weight;
                        var boneIndices = skin?.boneIndex;
                        var influenceCount = weights != null && boneIndices != null
                            ? Math.Min(weights.Length, boneIndices.Length)
                            : 0;
                        for (var j = 0; j < influenceCount; j++)
                        {
                            var w = weights![j];
                            if (w <= 0f)
                            {
                                continue;
                            }

                            var bIdx = boneIndices![j];
                            if (bIdx >= 0 && bIdx < normalMatrices.Length)
                            {
                                skinnedNorm += Vector3.TransformRow(srcNorm, normalMatrices[bIdx]) * w;
                            }
                        }

                        if (IsFinitePoint(skinnedNorm) && skinnedNorm.LengthSquared > 1e-10f)
                        {
                            result[vOff + v] = Vector3.Normalize(skinnedNorm);
                        }
                        else if (IsFinitePoint(srcNorm) && srcNorm.LengthSquared > 1e-10f)
                        {
                            result[vOff + v] = Vector3.Normalize(srcNorm);
                        }
                        else
                        {
                            result[vOff + v] = Vector3.UnitY;
                        }
                    }
                }
                else
                {
                    Matrix4 rigidMat;
                    if (frameSkeletonWorldMatrices != null
                        && part.RigidAttachNodeIndex >= 0
                        && part.RigidAttachNodeIndex < frameSkeletonWorldMatrices.Length)
                    {
                        rigidMat = part.RigidLocalOffset * frameSkeletonWorldMatrices[part.RigidAttachNodeIndex];
                    }
                    else
                    {
                        rigidMat = part.RigidLocalOffset;
                    }

                    var rigidNormalMat = ComputeNormalMatrix(rigidMat);
                    for (var v = 0; v < refSourceIndices.Length; v++)
                    {
                        var srcIdx = refSourceIndices[v];
                        var srcNorm = sourceNorms != null && srcIdx < sourceNorms.Length ? sourceNorms[srcIdx] : Vector3.UnitY;
                        var outNorm = Vector3.TransformRow(srcNorm, rigidNormalMat);
                        if (IsFinitePoint(outNorm) && outNorm.LengthSquared > 1e-10f)
                        {
                            result[vOff + v] = Vector3.Normalize(outNorm);
                        }
                        else if (IsFinitePoint(srcNorm) && srcNorm.LengthSquared > 1e-10f)
                        {
                            result[vOff + v] = Vector3.Normalize(srcNorm);
                        }
                        else
                        {
                            result[vOff + v] = Vector3.UnitY;
                        }
                    }
                }
            }

            return result;
        }

        internal static PreparedAvatarPreviewGLData? PrepareCompositeStaticAvatarGLData(
            IReadOnlyList<AvatarPreviewPartSpec> partSpecs,
            Vector3[] bonePositions,
            Matrix4[]? restSkeletonWorldMatrices,
            int[] parentIndices,
            string[]? boneNames,
            float densityPercent,
            float boneScale)
        {
            if (!BuildCompositePartRuntimes(
                    partSpecs,
                    densityPercent,
                    out var parts,
                    out var totalVertexCount,
                    out var combinedIndices,
                    out var combinedColors,
                    out var combinedUvs,
                    out var combinedSubMeshCounts,
                    out var rawMin,
                    out var rawMax))
            {
                return PrepareSkeletonOnlyAvatarGLData(bonePositions, parentIndices, boneNames, boneScale);
            }

            var combinedVerts = new Vector3[totalVertexCount];
            var combinedNormals = new Vector3[totalVertexCount];
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);

            for (var p = 0; p < parts.Count; p++)
            {
                TransformCompositePartPose(
                    parts[p],
                    restSkeletonWorldMatrices,
                    combinedVerts,
                    combinedNormals,
                    ref min,
                    ref max,
                    expandBounds: true);
            }

            int boneLinesCount = 0;
            int jointPointsCount = 0;
            Vector3[] skeletonVerts = Array.Empty<Vector3>();
            if (bonePositions != null && parentIndices != null && bonePositions.Length > 0)
            {
                for (var i = 0; i < bonePositions.Length; i++)
                {
                    ExpandBounds(bonePositions[i], ref min, ref max);
                }

                skeletonVerts = BuildSkeletonVerticesStatic(bonePositions, parentIndices, boneScale, out boneLinesCount, out jointPointsCount);
            }

            if (!IsFinitePoint(min) || !IsFinitePoint(max) || min.X > max.X)
            {
                min = rawMin;
                max = rawMax;
            }

            var calculatedNormals = BuildCalculatedNormals(combinedVerts, combinedIndices);
            var modelMatrix = CreatePreviewModelMatrix(min, max);
            var autoViewMatrix = bonePositions != null && bonePositions.Length > 0
                ? CreateAvatarInitialViewMatrix(bonePositions, boneNames)
                : CreateUnityAutoViewMatrix();
            var combinedReference = new PreviewMeshGeometry(
                combinedVerts,
                combinedIndices,
                Enumerable.Range(0, totalVertexCount).ToArray(),
                combinedSubMeshCounts);

            return new PreparedAvatarPreviewGLData
            {
                Mesh = parts[0].Mesh,
                SourceVertexData = combinedVerts,
                SourceIndices = new List<uint>(),
                SourceMeshMin = min,
                SourceMeshMax = max,
                ReferenceMesh = combinedReference,
                SourceNormals = combinedNormals,
                Normals = combinedNormals,
                CalculatedNormals = calculatedNormals,
                Colors = combinedColors,
                Uvs = combinedUvs,
                SubMeshIndexCounts = combinedSubMeshCounts,
                SkeletonVertices = skeletonVerts,
                BoneLinesCount = boneLinesCount,
                JointPointsCount = jointPointsCount,
                ModelMatrixData = modelMatrix,
                AutoViewMatrix = autoViewMatrix,
                ParentIndices = parentIndices ?? Array.Empty<int>(),
                StaticBonePositions = bonePositions,
                StaticSkeletonWorldMatrices = restSkeletonWorldMatrices,
                CompositeParts = parts
            };
        }

        internal static PreparedAvatarPreviewGLData? PrepareCompositeAnimatedAvatarGLData(
            IReadOnlyList<AvatarPreviewPartSpec> partSpecs,
            Vector3[][] frames,
            Matrix4[][] skeletonWorldMatrices,
            int[] parentIndices,
            float fps,
            string[]? boneNames,
            float densityPercent,
            float boneScale)
        {
            if (frames == null || frames.Length == 0 || skeletonWorldMatrices == null || skeletonWorldMatrices.Length == 0)
            {
                return null;
            }

            var hasParts = BuildCompositePartRuntimes(
                partSpecs,
                densityPercent,
                out var parts,
                out var totalVertexCount,
                out var combinedIndices,
                out var combinedColors,
                out var combinedUvs,
                out var combinedSubMeshCounts,
                out var rawMin,
                out var rawMax);

            var frameCount = Math.Min(frames.Length, skeletonWorldMatrices.Length);
            var animMin = new Vector3(float.MaxValue);
            var animMax = new Vector3(float.MinValue);

            Vector3[][]? localAnimatedVerts = null;
            Vector3[][]? localAnimatedNormals = null;
            Vector3[] initialVerts = Array.Empty<Vector3>();
            Vector3[]? initialNormals = null;

            if (hasParts)
            {
                var precomputeNormals = (long)frameCount * totalVertexCount <= MaxPrecomputedNormalVertexFrames;
                localAnimatedVerts = new Vector3[frameCount][];
                localAnimatedNormals = precomputeNormals ? new Vector3[frameCount][] : null;

                for (var f = 0; f < frameCount; f++)
                {
                    var frameVerts = new Vector3[totalVertexCount];
                    var frameNormals = precomputeNormals ? new Vector3[totalVertexCount] : null;
                    var frameMats = skeletonWorldMatrices[f];
                    for (var p = 0; p < parts.Count; p++)
                    {
                        TransformCompositePartPose(
                            parts[p],
                            frameMats,
                            frameVerts,
                            frameNormals,
                            ref animMin,
                            ref animMax,
                            expandBounds: true);
                    }

                    localAnimatedVerts[f] = frameVerts;
                    if (localAnimatedNormals != null && frameNormals != null)
                    {
                        localAnimatedNormals[f] = frameNormals;
                    }
                }

                initialVerts = localAnimatedVerts[0];
                initialNormals = localAnimatedNormals != null
                    ? localAnimatedNormals[0]
                    : SkinCompositeFrameNormals(parts, totalVertexCount, skeletonWorldMatrices[0]);
            }

            var firstFrameBones = frames[0];
            for (var f = 0; f < frameCount; f++)
            {
                foreach (var bonePos in frames[f])
                {
                    ExpandBounds(bonePos, ref animMin, ref animMax);
                }
            }

            if (!IsFinitePoint(animMin) || !IsFinitePoint(animMax) || animMin.X > animMax.X)
            {
                animMin = rawMin;
                animMax = rawMax;
            }

            if (!IsFinitePoint(animMin) || !IsFinitePoint(animMax) || animMin.X > animMax.X)
            {
                animMin = Vector3.Zero;
                animMax = Vector3.Zero;
            }

            var skeletonVerts = BuildSkeletonVerticesStatic(firstFrameBones, parentIndices, boneScale, out int boneLines, out int jointPoints);
            if (!hasParts && skeletonVerts.Length == 0)
            {
                return null;
            }

            var calculatedNormals = hasParts
                ? BuildCalculatedNormals(initialVerts, combinedIndices)
                : Array.Empty<Vector3>();
            var modelMatrix = CreatePreviewModelMatrix(animMin, animMax);
            var autoViewMatrix = CreateAvatarInitialViewMatrix(firstFrameBones, boneNames);
            var combinedReference = hasParts
                ? new PreviewMeshGeometry(initialVerts, combinedIndices, Enumerable.Range(0, totalVertexCount).ToArray(), combinedSubMeshCounts)
                : new PreviewMeshGeometry(Array.Empty<Vector3>(), Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>());

            return new PreparedAvatarPreviewGLData
            {
                Mesh = hasParts ? parts[0].Mesh : null,
                SourceVertexData = initialVerts,
                SourceIndices = new List<uint>(),
                SourceMeshMin = animMin,
                SourceMeshMax = animMax,
                ReferenceMesh = combinedReference,
                SourceNormals = initialNormals,
                Normals = initialNormals,
                CalculatedNormals = calculatedNormals,
                Colors = hasParts ? combinedColors : Array.Empty<Vector4>(),
                Uvs = hasParts ? combinedUvs : null,
                SubMeshIndexCounts = hasParts ? combinedSubMeshCounts : Array.Empty<int>(),
                SkeletonVertices = skeletonVerts,
                BoneLinesCount = boneLines,
                JointPointsCount = jointPoints,
                ModelMatrixData = modelMatrix,
                AutoViewMatrix = autoViewMatrix,
                ParentIndices = parentIndices,
                AnimationFrames = frames,
                BoneMatrices = skeletonWorldMatrices,
                AnimatedMeshVertices = localAnimatedVerts,
                AnimatedMeshNormals = localAnimatedNormals,
                CompositeParts = hasParts ? parts : null,
                Fps = fps > 0 ? fps : 30f
            };
        }

        internal void ApplyPreparedAvatarPreview(PreparedAvatarPreviewGLData prepared)
        {
            isAvatarMode = true;
            ClearMeshPreviewSource();
            StopAnimation();
            ApplyMaterialResetState(resetMaterialOverrides: true);
            ++meshLoadCounter;

            meshPreviewSource = prepared.Mesh ?? prepared.CompositeParts?.FirstOrDefault()?.Mesh;
            meshPreviewSourceUvs = prepared.Uvs;
            avatarReferenceMeshSource = prepared.Mesh;
            avatarReferenceSourceVertices = prepared.SourceVertexData;
            avatarReferenceSourceNormals = prepared.SourceNormals;
            avatarReferenceSourceVertexIndices = prepared.ReferenceMesh.SourceVertexIndices;
            avatarReferenceSourceIndices = prepared.SourceIndices;
            avatarReferenceMin = prepared.SourceMeshMin;
            avatarReferenceMax = prepared.SourceMeshMax;
            avatarCompositeParts = prepared.CompositeParts;
            avatarStaticSkeletonWorldMatrices = prepared.StaticSkeletonWorldMatrices;
            vertexData = prepared.IsAnimated && prepared.AnimatedMeshVertices != null && prepared.AnimatedMeshVertices.Length > 0
                ? prepared.AnimatedMeshVertices[0]
                : prepared.ReferenceMesh.Vertices;
            SetInitialPreviewFrame(prepared.AutoViewMatrix, prepared.ModelMatrixData);
            indiceData = prepared.ReferenceMesh.Indices;
            normalData = prepared.Normals;
            normal2Data = prepared.CalculatedNormals.Length == vertexData.Length
                ? prepared.CalculatedNormals
                : BuildCalculatedNormals(vertexData, indiceData);
            colorData = prepared.Colors.Length == vertexData.Length
                ? prepared.Colors
                : ExtractMeshColors(null, vertexData.Length);
            uvData = prepared.Uvs;
            previewSubMeshIndexCounts = prepared.SubMeshIndexCounts;

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
                animatedMeshNormals = prepared.AnimatedMeshNormals;
                dynamicSkinNormalsScratch = null;

                lock (skeletonLock)
                {
                    pendingSkeletonVertices = prepared.SkeletonVertices;
                }

                vao = 0;
                RequestNextFrameRendering();

                StartAnimationTimer();
            }
            else
            {
                staticBonePositions = prepared.StaticBonePositions;
                staticParentIndices = prepared.ParentIndices;
                avatarReferenceBoneMatrices = null;
                avatarReferenceParentIndices = null;
                animatedMeshVertices = null;
                animatedMeshNormals = null;
                dynamicSkinNormalsScratch = null;

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
            ApplyMaterialResetState(resetMaterialOverrides: true);
            m_Mesh.EnsureProcessed();
            if (m_Mesh.m_VertexCount <= 0)
            {
                return;
            }

            int currentLoadId = ++meshLoadCounter;
            var density = previewMeshDensityPercent;
            var scale = boneScale;

            System.Threading.Tasks.Task.Run(() =>
            {
                var prepared = PrepareStaticAvatarGLData(
                    m_Mesh,
                    bonePositions,
                    parentIndices,
                    boneNames,
                    density,
                    scale);
                if (prepared == null)
                {
                    return;
                }

                global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (currentLoadId != meshLoadCounter)
                    {
                        return;
                    }

                    ApplyPreparedAvatarPreview(prepared);
                });
            });
        }

        public void SetAnimatedAvatar(Mesh m_Mesh, Vector3[][] frames, Matrix4[][] boneMatrices, int[] parentIndices, float fps, string[]? boneNames = null)
        {
            isAvatarMode = true;
            ClearMeshPreviewSource();
            StopAnimation();
            ApplyMaterialResetState(resetMaterialOverrides: true);
            m_Mesh.EnsureProcessed();
            if (m_Mesh.m_VertexCount <= 0 || frames.Length == 0)
            {
                return;
            }

            int currentLoadId = ++meshLoadCounter;
            var density = previewMeshDensityPercent;
            var scale = boneScale;

            System.Threading.Tasks.Task.Run(() =>
            {
                var prepared = PrepareAnimatedAvatarGLData(
                    m_Mesh,
                    frames,
                    boneMatrices,
                    parentIndices,
                    fps,
                    boneNames,
                    density,
                    scale);
                if (prepared == null)
                {
                    return;
                }

                global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (currentLoadId != meshLoadCounter)
                    {
                        return;
                    }

                    ApplyPreparedAvatarPreview(prepared);
                });
            });
        }

        private void StartAnimationTimer()
        {
            animTimer = new global::Avalonia.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1000.0 / animFps)
            };
            animTimer.Tick += OnAnimationTick;
            PlayAnimation();
        }

        internal void StartMaterialShaderAnimation(int frames, float fps)
        {
            StopAnimation();
            materialShaderFrameCount = frames;
            animFps = fps;
            StartAnimationTimer();
        }

        public void PlayAnimation()
        {
            if (AnimTotalFrames == 0 || animTimer == null)
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
            animPausedByHiddenTab = false;
            if (animTimer != null && isAnimPlaying)
            {
                animTimer.Stop();
                isAnimPlaying = false;
            }
        }

        public void SuspendForHiddenTab()
        {
            if (animTimer != null && isAnimPlaying)
            {
                animTimer.Stop();
                isAnimPlaying = false;
                animPausedByHiddenTab = true;
            }
        }

        public void ResumeAfterHiddenTab()
        {
            if (animPausedByHiddenTab)
            {
                animPausedByHiddenTab = false;
                if (AnimTotalFrames > 0 && animTimer != null && !isAnimPlaying)
                {
                    animTimer.Start();
                    isAnimPlaying = true;
                }
            }

            if (IsVisible)
            {
                RequestNextFrameRendering();
            }
        }

        public void RestartAnimation()
        {
            if (materialShaderFrameCount > 0)
            {
                animCurrentFrame = 0;
                AnimationFrameChanged?.Invoke(0, materialShaderFrameCount);
                PlayAnimation();
                return;
            }
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
            materialShaderFrameCount = 0;
            animParentIndices = null;
            animCurrentFrame = 0;
            isAnimPlaying = false;
            animPausedByHiddenTab = false;
            animatedMeshVertices = null;
            animatedMeshNormals = null;
            dynamicSkinNormalsScratch = null;
            staticBonePositions = null;
            staticParentIndices = null;
            avatarReferenceBoneMatrices = null;
            avatarReferenceParentIndices = null;

            lock (meshLock)
            {
                pendingMeshVertices = null;
                pendingMeshNormals = null;
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
            animatedMeshNormals = null;
            dynamicSkinNormalsScratch = null;
            avatarReferenceMeshSource = null;
            avatarReferenceSourceVertices = null;
            avatarReferenceSourceNormals = null;
            avatarReferenceSourceVertexIndices = null;
            avatarReferenceSourceIndices = null;
            avatarReferenceBoneMatrices = null;
            avatarReferenceParentIndices = null;
            avatarCompositeParts = null;
            avatarStaticSkeletonWorldMatrices = null;
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
                pendingMeshNormals = null;
            }
        }

        private static Vector3[][]? BuildAnimatedAvatarReferenceVertices(Mesh mesh, Vector3[] sourceVertexData, PreviewMeshGeometry referenceMesh, Matrix4[][] boneMatrices, int[] parentIndices, ref Vector3 min, ref Vector3 max, bool expandBounds)
        {
            return BuildAnimatedAvatarReferenceVerticesAndNormals(
                mesh,
                sourceVertexData,
                null,
                referenceMesh,
                boneMatrices,
                parentIndices,
                ref min,
                ref max,
                expandBounds,
                out _);
        }

        private static Matrix3 ComputeNormalMatrix(in Matrix4 skinningMatrix)
        {
            var upper = new Matrix3(
                skinningMatrix.M11, skinningMatrix.M12, skinningMatrix.M13,
                skinningMatrix.M21, skinningMatrix.M22, skinningMatrix.M23,
                skinningMatrix.M31, skinningMatrix.M32, skinningMatrix.M33);
            try
            {
                var det = upper.Determinant;
                if (float.IsFinite(det) && Math.Abs(det) > 1e-8f)
                {
                    return Matrix3.Transpose(upper.Inverted());
                }
            }
            catch
            {
            }

            return upper;
        }

        internal static Vector3[]? SkinFrameNormals(
            Mesh mesh,
            Vector3[] sourceNormalData,
            int[] sourceVertexIndices,
            Matrix4[] frameMatrices,
            Vector3[]? destination = null)
        {
            if (sourceVertexIndices.Length == 0
                || mesh.m_Skin == null
                || mesh.m_Skin.Length < sourceNormalData.Length
                || mesh.m_BindPose == null
                || frameMatrices.Length == 0)
            {
                return null;
            }

            var normalMatrices = new Matrix3[frameMatrices.Length];
            for (int b = 0; b < frameMatrices.Length; b++)
            {
                if (b < mesh.m_BindPose.Length)
                {
                    var bp = mesh.m_BindPose[b];
                    var otkMat = new Matrix4(
                        bp.M00, bp.M01, bp.M02, bp.M03,
                        bp.M10, bp.M11, bp.M12, bp.M13,
                        bp.M20, bp.M21, bp.M22, bp.M23,
                        bp.M30, bp.M31, bp.M32, bp.M33);
                    normalMatrices[b] = ComputeNormalMatrix(otkMat * frameMatrices[b]);
                }
                else
                {
                    normalMatrices[b] = Matrix3.Identity;
                }
            }

            var result = destination != null && destination.Length == sourceVertexIndices.Length
                ? destination
                : new Vector3[sourceVertexIndices.Length];

            for (int v = 0; v < sourceVertexIndices.Length; v++)
            {
                int sourceVertexIndex = sourceVertexIndices[v];
                var sourceNormal = sourceVertexIndex >= 0 && sourceVertexIndex < sourceNormalData.Length
                    ? sourceNormalData[sourceVertexIndex]
                    : Vector3.UnitY;
                var skin = mesh.m_Skin[sourceVertexIndex];
                var skinnedNormal = Vector3.Zero;
                var weights = skin?.weight;
                var boneIndices = skin?.boneIndex;
                int influenceCount = weights != null && boneIndices != null
                    ? Math.Min(weights.Length, boneIndices.Length)
                    : 0;
                for (int j = 0; j < influenceCount; j++)
                {
                    float w = weights![j];
                    if (w <= 0f)
                    {
                        continue;
                    }

                    int bIdx = boneIndices![j];
                    if (bIdx >= 0 && bIdx < normalMatrices.Length)
                    {
                        skinnedNormal += Vector3.TransformRow(sourceNormal, normalMatrices[bIdx]) * w;
                    }
                }

                if (IsFinitePoint(skinnedNormal) && skinnedNormal.LengthSquared > 1e-10f)
                {
                    result[v] = Vector3.Normalize(skinnedNormal);
                }
                else if (IsFinitePoint(sourceNormal) && sourceNormal.LengthSquared > 1e-10f)
                {
                    result[v] = Vector3.Normalize(sourceNormal);
                }
                else
                {
                    result[v] = Vector3.UnitY;
                }
            }

            return result;
        }

        private static Vector3[][]? BuildAnimatedAvatarReferenceVerticesAndNormals(
            Mesh mesh,
            Vector3[] sourceVertexData,
            Vector3[]? sourceNormalData,
            PreviewMeshGeometry referenceMesh,
            Matrix4[][] boneMatrices,
            int[] parentIndices,
            ref Vector3 min,
            ref Vector3 max,
            bool expandBounds,
            out Vector3[][]? animatedNormals)
        {
            animatedNormals = null;
            if (referenceMesh.SourceVertexIndices.Length == 0
                || mesh.m_Skin == null
                || mesh.m_Skin.Length < sourceVertexData.Length
                || mesh.m_BindPose == null
                || mesh.m_BindPose.Length < parentIndices.Length
                || boneMatrices.Length == 0)
            {
                return null;
            }

            bool precomputeNormals = sourceNormalData != null
                && sourceNormalData.Length >= sourceVertexData.Length
                && (long)boneMatrices.Length * referenceMesh.SourceVertexIndices.Length <= MaxPrecomputedNormalVertexFrames;

            var localAnimatedMeshVerts = new Vector3[boneMatrices.Length][];
            var localAnimatedMeshNormals = precomputeNormals ? new Vector3[boneMatrices.Length][] : null;

            for (int f = 0; f < boneMatrices.Length; f++)
            {
                var frameVerts = new Vector3[referenceMesh.SourceVertexIndices.Length];
                var frameNormals = precomputeNormals ? new Vector3[referenceMesh.SourceVertexIndices.Length] : null;
                var frameMatrices = boneMatrices[f];
                var skinningMatrices = new Matrix4[frameMatrices.Length];
                var normalMatrices = precomputeNormals ? new Matrix3[frameMatrices.Length] : null;

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
                        var skinMat = otkMat * frameMatrices[b];
                        skinningMatrices[b] = skinMat;
                        if (normalMatrices != null)
                        {
                            normalMatrices[b] = ComputeNormalMatrix(skinMat);
                        }
                    }
                    else
                    {
                        skinningMatrices[b] = Matrix4.Identity;
                        if (normalMatrices != null)
                        {
                            normalMatrices[b] = Matrix3.Identity;
                        }
                    }
                }

                for (int v = 0; v < referenceMesh.SourceVertexIndices.Length; v++)
                {
                    int sourceVertexIndex = referenceMesh.SourceVertexIndices[v];
                    var sourceVertex = sourceVertexData[sourceVertexIndex];
                    var sourceNormal = frameNormals != null ? sourceNormalData![sourceVertexIndex] : Vector3.UnitY;
                    var skin = mesh.m_Skin[sourceVertexIndex];
                    Vector3 skinnedPos = Vector3.Zero;
                    Vector3 skinnedNormal = Vector3.Zero;
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
                            if (normalMatrices != null)
                            {
                                skinnedNormal += Vector3.TransformRow(sourceNormal, normalMatrices[bIdx]) * w;
                            }
                            totalWeight += w;
                        }
                    }

                    frameVerts[v] = totalWeight > 0f ? skinnedPos / totalWeight : sourceVertex;
                    if (frameNormals != null)
                    {
                        if (IsFinitePoint(skinnedNormal) && skinnedNormal.LengthSquared > 1e-10f)
                        {
                            frameNormals[v] = Vector3.Normalize(skinnedNormal);
                        }
                        else if (IsFinitePoint(sourceNormal) && sourceNormal.LengthSquared > 1e-10f)
                        {
                            frameNormals[v] = Vector3.Normalize(sourceNormal);
                        }
                        else
                        {
                            frameNormals[v] = Vector3.UnitY;
                        }
                    }

                    if (expandBounds)
                    {
                        ExpandBounds(frameVerts[v], ref min, ref max);
                    }
                }

                localAnimatedMeshVerts[f] = frameVerts;
                if (localAnimatedMeshNormals != null && frameNormals != null)
                {
                    localAnimatedMeshNormals[f] = frameNormals;
                }
            }

            animatedNormals = localAnimatedMeshNormals;
            return localAnimatedMeshVerts;
        }

        private void RebuildAvatarReferenceMeshPreview()
        {
            if (!isAvatarMode)
            {
                return;
            }

            if (avatarCompositeParts != null && avatarCompositeParts.Count > 0)
            {
                RebuildCompositeAvatarReferenceMeshPreview();
                return;
            }

            if (avatarReferenceSourceVertices == null
                || avatarReferenceSourceIndices == null
                || avatarReferenceSourceIndices.Count == 0)
            {
                return;
            }

            int currentLoadId = ++meshLoadCounter;
            var sourceVertices = avatarReferenceSourceVertices;
            var sourceNormals = avatarReferenceSourceNormals;
            var sourceIndices = avatarReferenceSourceIndices;
            var sourceMesh = avatarReferenceMeshSource;
            var boneMatrices = avatarReferenceBoneMatrices;
            var parentIndices = avatarReferenceParentIndices;
            var min = avatarReferenceMin;
            var max = avatarReferenceMax;
            var densityPercent = previewMeshDensityPercent;

            System.Threading.Tasks.Task.Run(() =>
            {
                var subMeshIndexCounts = sourceMesh != null
                    ? BuildSubMeshIndexCounts(sourceMesh, sourceIndices.Count)
                    : null;
                var referenceMesh = PreviewMeshSimplifier.Build(sourceVertices, sourceIndices, min, max, densityPercent, subMeshIndexCounts);
                Vector3[][]? rebuiltAnimatedVertices = null;
                Vector3[][]? rebuiltAnimatedNormals = null;

                if (sourceMesh != null && boneMatrices != null && parentIndices != null)
                {
                    var ignoredMin = min;
                    var ignoredMax = max;
                    rebuiltAnimatedVertices = BuildAnimatedAvatarReferenceVerticesAndNormals(
                        sourceMesh,
                        sourceVertices,
                        sourceNormals,
                        referenceMesh,
                        boneMatrices,
                        parentIndices,
                        ref ignoredMin,
                        ref ignoredMax,
                        false,
                        out rebuiltAnimatedNormals);
                }

                Vector3[]? rebuiltNormals = null;
                Vector3[]? rebuiltCalcNormals = null;
                Vector4[]? rebuiltColors = null;
                Vector2[]? rebuiltUvs = null;
                if (sourceMesh != null)
                {
                    var initialVerts = rebuiltAnimatedVertices != null && rebuiltAnimatedVertices.Length > 0
                        ? rebuiltAnimatedVertices[0]
                        : referenceMesh.Vertices;
                    BuildReferenceSurfaceAttributes(
                        sourceMesh,
                        referenceMesh,
                        initialVerts,
                        out _,
                        out rebuiltNormals,
                        out rebuiltCalcNormals,
                        out rebuiltColors,
                        out rebuiltUvs);
                }

                global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (currentLoadId != meshLoadCounter || !isAvatarMode)
                    {
                        return;
                    }

                    int activeFrame = animCurrentFrame;
                    Vector3[] activeVertices = rebuiltAnimatedVertices != null && activeFrame < rebuiltAnimatedVertices.Length
                        ? rebuiltAnimatedVertices[activeFrame]
                        : referenceMesh.Vertices;
                    Vector3[]? activeNormals = rebuiltAnimatedNormals != null && activeFrame < rebuiltAnimatedNormals.Length
                        ? rebuiltAnimatedNormals[activeFrame]
                        : (sourceMesh != null && sourceNormals != null && boneMatrices != null && activeFrame < boneMatrices.Length
                            ? SkinFrameNormals(sourceMesh, sourceNormals, referenceMesh.SourceVertexIndices, boneMatrices[activeFrame])
                            : rebuiltNormals);

                    keepExistingTextures = true;
                    avatarReferenceSourceVertexIndices = referenceMesh.SourceVertexIndices;
                    vertexData = activeVertices;
                    indiceData = referenceMesh.Indices;
                    var activeCalcNormals = BuildCalculatedNormals(activeVertices, referenceMesh.Indices);
                    normalData = activeNormals ?? rebuiltNormals;
                    normal2Data = activeCalcNormals;
                    colorData = rebuiltColors ?? ExtractMeshColors(null, activeVertices.Length);
                    uvData = rebuiltUvs;
                    previewSubMeshIndexCounts = referenceMesh.SubMeshIndexCounts ?? subMeshIndexCounts;
                    animatedMeshVertices = rebuiltAnimatedVertices;
                    animatedMeshNormals = rebuiltAnimatedNormals;
                    dynamicSkinNormalsScratch = null;

                    lock (meshLock)
                    {
                        pendingMeshVertices = rebuiltAnimatedVertices != null && activeFrame < rebuiltAnimatedVertices.Length
                            ? rebuiltAnimatedVertices[activeFrame]
                            : null;
                        pendingMeshNormals = normalMode == 0
                            ? (normalData ?? normal2Data)
                            : (normal2Data ?? normalData);
                    }

                    vao = 0;
                    RequestNextFrameRendering();
                });
            });
        }

        private void RebuildCompositeAvatarReferenceMeshPreview()
        {
            var existingParts = avatarCompositeParts;
            if (existingParts == null || existingParts.Count == 0)
            {
                return;
            }

            int currentLoadId = ++meshLoadCounter;
            var boneMatrices = avatarReferenceBoneMatrices;
            var staticWorldMatrices = avatarStaticSkeletonWorldMatrices;
            var densityPercent = previewMeshDensityPercent;

            var specs = new List<AvatarPreviewPartSpec>(existingParts.Count);
            for (var i = 0; i < existingParts.Count; i++)
            {
                var p = existingParts[i];
                specs.Add(new AvatarPreviewPartSpec
                {
                    Mesh = p.Mesh,
                    MeshAssetId = p.MeshAssetId,
                    RendererAssetId = p.RendererAssetId,
                    IsSkinned = p.IsSkinned,
                    BoneToSkeletonNodeMap = p.BoneToSkeletonNodeMap,
                    RigidAttachNodeIndex = p.RigidAttachNodeIndex,
                    RigidLocalOffset = p.RigidLocalOffset
                });
            }

            System.Threading.Tasks.Task.Run(() =>
            {
                if (!BuildCompositePartRuntimes(
                        specs,
                        densityPercent,
                        out var rebuiltParts,
                        out var totalVertexCount,
                        out var combinedIndices,
                        out var combinedColors,
                        out var combinedUvs,
                        out var combinedSubMeshCounts,
                        out var ignoredMin,
                        out var ignoredMax))
                {
                    return;
                }

                Vector3[][]? rebuiltAnimatedVertices = null;
                Vector3[][]? rebuiltAnimatedNormals = null;
                Vector3[]? staticVerts = null;
                Vector3[]? staticNormals = null;

                if (boneMatrices != null && boneMatrices.Length > 0)
                {
                    var precomputeNormals = (long)boneMatrices.Length * totalVertexCount <= MaxPrecomputedNormalVertexFrames;
                    rebuiltAnimatedVertices = new Vector3[boneMatrices.Length][];
                    rebuiltAnimatedNormals = precomputeNormals ? new Vector3[boneMatrices.Length][] : null;
                    for (var f = 0; f < boneMatrices.Length; f++)
                    {
                        var frameVerts = new Vector3[totalVertexCount];
                        var frameNormals = precomputeNormals ? new Vector3[totalVertexCount] : null;
                        var frameMats = boneMatrices[f];
                        for (var p = 0; p < rebuiltParts.Count; p++)
                        {
                            TransformCompositePartPose(
                                rebuiltParts[p],
                                frameMats,
                                frameVerts,
                                frameNormals,
                                ref ignoredMin,
                                ref ignoredMax,
                                expandBounds: false);
                        }

                        rebuiltAnimatedVertices[f] = frameVerts;
                        if (rebuiltAnimatedNormals != null && frameNormals != null)
                        {
                            rebuiltAnimatedNormals[f] = frameNormals;
                        }
                    }
                }
                else
                {
                    staticVerts = new Vector3[totalVertexCount];
                    staticNormals = new Vector3[totalVertexCount];
                    for (var p = 0; p < rebuiltParts.Count; p++)
                    {
                        TransformCompositePartPose(
                            rebuiltParts[p],
                            staticWorldMatrices,
                            staticVerts,
                            staticNormals,
                            ref ignoredMin,
                            ref ignoredMax,
                            expandBounds: false);
                    }
                }

                global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (currentLoadId != meshLoadCounter || !isAvatarMode)
                    {
                        return;
                    }

                    int activeFrame = animCurrentFrame;
                    Vector3[] activeVertices = rebuiltAnimatedVertices != null && activeFrame < rebuiltAnimatedVertices.Length
                        ? rebuiltAnimatedVertices[activeFrame]
                        : (staticVerts ?? Array.Empty<Vector3>());
                    Vector3[]? activeNormals = rebuiltAnimatedNormals != null && activeFrame < rebuiltAnimatedNormals.Length
                        ? rebuiltAnimatedNormals[activeFrame]
                        : (boneMatrices != null && activeFrame < boneMatrices.Length
                            ? SkinCompositeFrameNormals(rebuiltParts, totalVertexCount, boneMatrices[activeFrame])
                            : staticNormals);

                    keepExistingTextures = true;
                    avatarCompositeParts = rebuiltParts;
                    vertexData = activeVertices;
                    indiceData = combinedIndices;
                    var activeCalcNormals = BuildCalculatedNormals(activeVertices, combinedIndices);
                    normalData = activeNormals ?? activeCalcNormals;
                    normal2Data = activeCalcNormals;
                    colorData = combinedColors;
                    uvData = combinedUvs;
                    previewSubMeshIndexCounts = combinedSubMeshCounts;
                    animatedMeshVertices = rebuiltAnimatedVertices;
                    animatedMeshNormals = rebuiltAnimatedNormals;
                    dynamicSkinNormalsScratch = null;

                    lock (meshLock)
                    {
                        pendingMeshVertices = rebuiltAnimatedVertices != null && activeFrame < rebuiltAnimatedVertices.Length
                            ? rebuiltAnimatedVertices[activeFrame]
                            : null;
                        pendingMeshNormals = normalMode == 0
                            ? (normalData ?? normal2Data)
                            : (normal2Data ?? normalData);
                    }

                    vao = 0;
                    RequestNextFrameRendering();
                });
            });
        }

        private void OnAnimationTick(object? sender, EventArgs e)
        {
            if (materialShaderFrameCount > 0)
            {
                animCurrentFrame = (animCurrentFrame + 1) % materialShaderFrameCount;
                AnimationFrameChanged?.Invoke(animCurrentFrame, materialShaderFrameCount);
                return;
            }
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
                var frameVerts = animatedMeshVertices[animCurrentFrame];
                vertexData = frameVerts;

                Vector3[]? skinnedNormals = null;
                if (animatedMeshNormals != null && animCurrentFrame < animatedMeshNormals.Length)
                {
                    skinnedNormals = animatedMeshNormals[animCurrentFrame];
                }
                else if (avatarCompositeParts != null
                    && avatarCompositeParts.Count > 0
                    && avatarReferenceBoneMatrices != null
                    && animCurrentFrame < avatarReferenceBoneMatrices.Length)
                {
                    dynamicSkinNormalsScratch = SkinCompositeFrameNormals(
                        avatarCompositeParts,
                        frameVerts.Length,
                        avatarReferenceBoneMatrices[animCurrentFrame],
                        dynamicSkinNormalsScratch);
                    skinnedNormals = dynamicSkinNormalsScratch;
                }
                else if (avatarReferenceMeshSource != null
                    && avatarReferenceSourceNormals != null
                    && avatarReferenceSourceVertexIndices != null
                    && avatarReferenceBoneMatrices != null
                    && animCurrentFrame < avatarReferenceBoneMatrices.Length)
                {
                    dynamicSkinNormalsScratch = SkinFrameNormals(
                        avatarReferenceMeshSource,
                        avatarReferenceSourceNormals,
                        avatarReferenceSourceVertexIndices,
                        avatarReferenceBoneMatrices[animCurrentFrame],
                        dynamicSkinNormalsScratch);
                    skinnedNormals = dynamicSkinNormalsScratch;
                }

                if (skinnedNormals != null)
                {
                    normalData = skinnedNormals;
                }

                Vector3[]? frameNormals;
                if (normalMode == 1 && indiceData != null)
                {
                    normal2Data = BuildCalculatedNormals(frameVerts, indiceData);
                    frameNormals = normal2Data;
                }
                else if (skinnedNormals != null)
                {
                    frameNormals = skinnedNormals;
                }
                else if (indiceData != null)
                {
                    normal2Data = BuildCalculatedNormals(frameVerts, indiceData);
                    frameNormals = normal2Data;
                }
                else
                {
                    frameNormals = null;
                }

                lock (meshLock)
                {
                    pendingMeshVertices = frameVerts;
                    if (frameNormals != null)
                    {
                        pendingMeshNormals = frameNormals;
                    }
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
