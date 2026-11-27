using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AssetStudio
{
    public partial class ModelConverter
    {
        public void CombineMeshes(string? combinedMeshName = null)
        {
            if (MeshList == null || MeshList.Count <= 1)
            {
                return;
            }

            string? effectiveName = combinedMeshName;
            if (string.IsNullOrWhiteSpace(effectiveName) && hasRootFrame && RootFrame != null && HasSkinnedMeshesWithRootAsBoneAncestor(MeshList, RootFrame))
            {
                effectiveName = "CombinedMesh";
            }

            ImportedFrame? targetFrame = RootFrame;
            if (RootFrame != null && !string.IsNullOrWhiteSpace(effectiveName))
            {
                var normalizedRootName = RootFrame.Name ?? string.Empty;
                var normalizedRootPath = NormalizeFramePath(RootFrame.Path);
                var normalizedTarget = NormalizeFramePath(effectiveName);
                if (!string.Equals(normalizedRootName, effectiveName, StringComparison.Ordinal)
                    && !string.Equals(normalizedRootPath, normalizedTarget, StringComparison.Ordinal))
                {
                    var existing = FindFrameForMesh(RootFrame, effectiveName);
                    if (existing != null && !IsFrameAncestorOfAnyMeshBone(existing, MeshList, RootFrame))
                    {
                        targetFrame = existing;
                    }
                    else
                    {
                        targetFrame = GetOrCreateDedicatedCombinedMeshFrame(RootFrame, effectiveName);
                    }
                }
            }

            var combinedMesh = CombineMeshesInternal(
                MeshList,
                RootFrame,
                targetFrame,
                effectiveName,
                MorphList,
                AnimationList,
                out var combinedMorphs,
                out var updatedAnimations,
                cloneAnimationsForView: false);

            MeshList = new List<ImportedMesh> { combinedMesh };
            MorphList = combinedMorphs;
            AnimationList = updatedAnimations;
        }

        public static ImportedMesh CombineMeshList(IReadOnlyList<ImportedMesh>? meshes, ImportedFrame? rootFrame, string? combinedName = null)
        {
            if (meshes == null || meshes.Count == 0)
            {
                return new ImportedMesh
                {
                    Path = !string.IsNullOrWhiteSpace(combinedName)
                        ? combinedName
                        : !string.IsNullOrWhiteSpace(rootFrame?.Path)
                            ? rootFrame.Path
                            : "CombinedMesh",
                    VertexList = new List<ImportedVertex>(),
                    SubmeshList = new List<ImportedSubmesh>(),
                    hasUV = new bool[8]
                };
            }

            ImportedFrame? targetFrame = rootFrame;
            if (rootFrame != null && !string.IsNullOrWhiteSpace(combinedName))
            {
                var existing = FindFrameForMesh(rootFrame, combinedName);
                if (existing != null)
                {
                    targetFrame = existing;
                }
            }

            return CombineMeshesInternal(
                meshes,
                rootFrame,
                targetFrame,
                combinedName,
                morphList: null,
                animationList: null,
                out _,
                out _,
                cloneAnimationsForView: false);
        }

        [return: NotNullIfNotNull(nameof(imported))]
        public static IImported? CreateCombinedView(IImported? imported, string? combinedMeshName = null)
        {
            if (imported == null || imported.MeshList == null || imported.MeshList.Count <= 1)
            {
                return imported;
            }

            string? effectiveName = combinedMeshName;
            bool needsDedicatedChild = imported.RootFrame != null
                && HasSkinnedMeshesWithRootAsBoneAncestor(imported.MeshList, imported.RootFrame);
            if (string.IsNullOrWhiteSpace(effectiveName) && needsDedicatedChild)
            {
                effectiveName = "CombinedMesh";
            }

            ImportedFrame? targetFrame = imported.RootFrame;
            if (imported.RootFrame != null && !string.IsNullOrWhiteSpace(effectiveName))
            {
                var existing = FindFrameForMesh(imported.RootFrame, effectiveName);
                if (existing != null && !IsFrameAncestorOfAnyMeshBone(existing, imported.MeshList, imported.RootFrame))
                {
                    targetFrame = existing;
                }
                else if (needsDedicatedChild
                    || (!string.Equals(imported.RootFrame.Name, effectiveName, StringComparison.Ordinal)
                        && !string.Equals(NormalizeFramePath(imported.RootFrame.Path), NormalizeFramePath(effectiveName), StringComparison.Ordinal)))
                {
                    targetFrame = GetOrCreateDedicatedCombinedMeshFrame(imported.RootFrame, effectiveName);
                }
            }

            var combinedMesh = CombineMeshesInternal(
                imported.MeshList,
                imported.RootFrame,
                targetFrame,
                effectiveName,
                imported.MorphList,
                imported.AnimationList,
                out var combinedMorphs,
                out var updatedAnimations,
                cloneAnimationsForView: true);

            return new ImportedWrapper(imported)
            {
                MeshList = new List<ImportedMesh> { combinedMesh },
                MorphList = combinedMorphs,
                AnimationList = updatedAnimations
            };
        }

        private static bool HasSkinnedMeshesWithRootAsBoneAncestor(IReadOnlyList<ImportedMesh> meshes, ImportedFrame rootFrame)
        {
            return IsFrameAncestorOfAnyMeshBone(rootFrame, meshes, rootFrame);
        }

        private static bool IsFrameAncestorOfAnyMeshBone(ImportedFrame candidateAncestor, IReadOnlyList<ImportedMesh> meshes, ImportedFrame rootFrame)
        {
            if (candidateAncestor == null || meshes == null)
            {
                return false;
            }

            var ancestorPath = NormalizeFramePath(candidateAncestor.Path);
            for (int m = 0; m < meshes.Count; m++)
            {
                var boneList = meshes[m]?.BoneList;
                if (boneList == null) continue;
                for (int b = 0; b < boneList.Count; b++)
                {
                    var bonePath = NormalizeFramePath(boneList[b]?.Path);
                    if (string.IsNullOrEmpty(bonePath)) continue;
                    var boneFrame = FindFrameForMesh(rootFrame, bonePath);
                    if (boneFrame != null)
                    {
                        var cur = boneFrame.Parent;
                        while (cur != null)
                        {
                            if (ReferenceEquals(cur, candidateAncestor))
                            {
                                return true;
                            }
                            cur = cur.Parent;
                        }
                    }
                    else if (!string.IsNullOrEmpty(ancestorPath)
                        && bonePath.Length > ancestorPath.Length
                        && bonePath[ancestorPath.Length] == '/'
                        && bonePath.StartsWith(ancestorPath, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static ImportedFrame GetOrCreateDedicatedCombinedMeshFrame(ImportedFrame rootFrame, string preferredName)
        {
            var leafName = preferredName;
            int lastSlash = leafName.LastIndexOf('/');
            if (lastSlash >= 0 && lastSlash + 1 < leafName.Length)
            {
                leafName = leafName.Substring(lastSlash + 1);
            }
            if (string.IsNullOrWhiteSpace(leafName) || string.Equals(leafName, rootFrame.Name, StringComparison.Ordinal))
            {
                leafName = "CombinedMesh";
            }

            var existingChild = rootFrame.FindChild(leafName, recursive: false);
            if (existingChild != null && existingChild.Count == 0)
            {
                return existingChild;
            }

            string uniqueName = leafName;
            for (int i = 1; rootFrame.FindChild(uniqueName, recursive: false) != null; i++)
            {
                uniqueName = $"{leafName}_{i}";
            }

            var childFrame = CreateFrame(uniqueName, Vector3.Zero, new Quaternion(0, 0, 0, 1), Vector3.One);
            rootFrame.AddChild(childFrame);
            return childFrame;
        }

        private readonly struct CombinedPartTransformMeta
        {
            public CombinedPartTransformMeta(
                string normalizedPath,
                int vertexOffset,
                int vertexCount,
                System.Numerics.Matrix4x4 relativeMatrix,
                System.Numerics.Matrix4x4 normalMatrix)
            {
                NormalizedPath = normalizedPath;
                VertexOffset = vertexOffset;
                VertexCount = vertexCount;
                RelativeMatrix = relativeMatrix;
                NormalMatrix = normalMatrix;
            }

            public string NormalizedPath { get; }
            public int VertexOffset { get; }
            public int VertexCount { get; }
            public System.Numerics.Matrix4x4 RelativeMatrix { get; }
            public System.Numerics.Matrix4x4 NormalMatrix { get; }
        }

        private static ImportedMesh CombineMeshesInternal(
            IReadOnlyList<ImportedMesh> meshes,
            ImportedFrame? rootFrame,
            ImportedFrame? targetFrame,
            string? combinedName,
            List<ImportedMorph>? morphList,
            List<ImportedKeyframedAnimation>? animationList,
            out List<ImportedMorph> combinedMorphs,
            out List<ImportedKeyframedAnimation> updatedAnimations,
            bool cloneAnimationsForView)
        {
            int totalVertices = 0;
            int totalSubmeshes = 0;
            bool anyNormal = false;
            bool anyTangent = false;
            bool anyColor = false;
            var anyUV = new bool[8];
            string? firstNonEmptyMeshPath = null;

            for (int m = 0; m < meshes.Count; m++)
            {
                var mesh = meshes[m];
                if (mesh == null) continue;

                if (firstNonEmptyMeshPath == null && !string.IsNullOrWhiteSpace(mesh.Path))
                {
                    firstNonEmptyMeshPath = mesh.Path;
                }

                totalVertices += mesh.VertexList?.Count ?? 0;
                totalSubmeshes += mesh.SubmeshList?.Count ?? 0;
                if (mesh.hasNormal) anyNormal = true;
                if (mesh.hasTangent) anyTangent = true;
                if (mesh.hasColor) anyColor = true;
                if (mesh.hasUV != null)
                {
                    int uvLen = Math.Min(8, mesh.hasUV.Length);
                    for (int u = 0; u < uvLen; u++)
                    {
                        if (mesh.hasUV[u]) anyUV[u] = true;
                    }
                }
            }

            string targetPath;
            if (targetFrame != null && !string.IsNullOrWhiteSpace(targetFrame.Path))
            {
                if (!string.IsNullOrWhiteSpace(combinedName)
                    && !string.Equals(targetFrame.Name, combinedName, StringComparison.Ordinal)
                    && !string.Equals(NormalizeFramePath(targetFrame.Path), NormalizeFramePath(combinedName), StringComparison.Ordinal))
                {
                    targetPath = combinedName;
                }
                else
                {
                    targetPath = targetFrame.Path;
                }
            }
            else if (!string.IsNullOrWhiteSpace(combinedName))
            {
                targetPath = combinedName;
            }
            else
            {
                targetPath = firstNonEmptyMeshPath ?? "CombinedMesh";
            }

            var unifiedBones = new List<ImportedBone>();
            var boneIndexByNormalizedPath = new Dictionary<string, int>(StringComparer.Ordinal);
            var perMeshBoneRemaps = new int[meshes.Count][];
            var partMetas = new List<CombinedPartTransformMeta>(meshes.Count);
            var partRelMatrices = new System.Numerics.Matrix4x4[meshes.Count];
            var partNormalMatrices = new System.Numerics.Matrix4x4[meshes.Count];
            var partFlipWinding = new bool[meshes.Count];

            // Precompute relative transform matrices and unified bone table
            for (int m = 0; m < meshes.Count; m++)
            {
                var mesh = meshes[m];
                if (mesh == null) continue;

                var meshFrame = FindFrameForMesh(rootFrame, mesh.Path);
                var relMatrix = ComputeRelativeFrameMatrix(meshFrame, targetFrame);
                var normalMatrix = BuildNormalMatrix(relMatrix, out var flipWinding);
                partRelMatrices[m] = relMatrix;
                partNormalMatrices[m] = normalMatrix;
                partFlipWinding[m] = flipWinding;

                if (mesh.BoneList != null && mesh.BoneList.Count > 0)
                {
                    var remap = new int[mesh.BoneList.Count];
                    for (int b = 0; b < mesh.BoneList.Count; b++)
                    {
                        var bone = mesh.BoneList[b];
                        var normBonePath = NormalizeFramePath(bone?.Path);
                        if (!string.IsNullOrEmpty(normBonePath) && boneIndexByNormalizedPath.TryGetValue(normBonePath, out var existingIdx))
                        {
                            remap[b] = existingIdx;
                        }
                        else
                        {
                            var newIdx = unifiedBones.Count;
                            var adjustedBindPose = bone != null
                                ? TransformBindPoseMatrix(bone.Matrix, relMatrix)
                                : default(Matrix4x4);
                            unifiedBones.Add(new ImportedBone
                            {
                                Path = bone?.Path,
                                Matrix = adjustedBindPose
                            });
                            if (!string.IsNullOrEmpty(normBonePath))
                            {
                                boneIndexByNormalizedPath[normBonePath] = newIdx;
                            }
                            remap[b] = newIdx;
                        }
                    }
                    perMeshBoneRemaps[m] = remap;
                }
            }

            bool hasAnySkinning = unifiedBones.Count > 0;
            var rigidMeshBoneIndices = new int[meshes.Count];
            for (int m = 0; m < meshes.Count; m++)
            {
                rigidMeshBoneIndices[m] = -1;
            }

            if (hasAnySkinning)
            {
                var targetGlobal = targetFrame != null
                    ? ComputeFrameGlobalMatrix(targetFrame)
                    : System.Numerics.Matrix4x4.Identity;

                for (int m = 0; m < meshes.Count; m++)
                {
                    var mesh = meshes[m];
                    if (mesh == null || (perMeshBoneRemaps[m] != null && perMeshBoneRemaps[m].Length > 0))
                    {
                        continue;
                    }

                    var meshFrame = FindFrameForMesh(rootFrame, mesh.Path);
                    if (meshFrame == null)
                    {
                        continue;
                    }

                    // 1. Walk up from meshFrame to find the nearest skeleton ancestor bone in unifiedBones
                    int foundBoneIdx = -1;
                    var cur = meshFrame;
                    var visited = new HashSet<ImportedFrame>();
                    while (cur != null && !ReferenceEquals(cur, rootFrame) && visited.Add(cur))
                    {
                        var normCurPath = NormalizeFramePath(cur.Path);
                        if (!string.IsNullOrEmpty(normCurPath) && boneIndexByNormalizedPath.TryGetValue(normCurPath, out var existingBoneIdx))
                        {
                            foundBoneIdx = existingBoneIdx;
                            break;
                        }
                        cur = cur.Parent;
                    }

                    // 2. If no ancestor is in unifiedBones yet, add meshFrame itself as a rigid bone when it is a
                    // non-mirrored child in a full model hierarchy (e.g., Shield / accessory under character sub-root)
                    if (foundBoneIdx < 0
                        && !partFlipWinding[m]
                        && !ReferenceEquals(meshFrame, rootFrame)
                        && (!ReferenceEquals(meshFrame.Parent, rootFrame) || animationList != null || morphList != null))
                    {
                        var boneGlobal = ComputeFrameGlobalMatrix(meshFrame);
                        if (System.Numerics.Matrix4x4.Invert(boneGlobal, out var invBoneGlobal))
                        {
                            var normMeshPath = NormalizeFramePath(meshFrame.Path);
                            var bindPose = FromNumericsMatrix(targetGlobal * invBoneGlobal);
                            foundBoneIdx = unifiedBones.Count;
                            unifiedBones.Add(new ImportedBone
                            {
                                Path = meshFrame.Path,
                                Matrix = bindPose
                            });
                            if (!string.IsNullOrEmpty(normMeshPath))
                            {
                                boneIndexByNormalizedPath[normMeshPath] = foundBoneIdx;
                            }
                        }
                    }

                    rigidMeshBoneIndices[m] = foundBoneIdx;
                }
            }

            var combinedVertices = new List<ImportedVertex>(totalVertices);
            var combinedSubmeshes = new List<ImportedSubmesh>(totalSubmeshes);

            for (int m = 0; m < meshes.Count; m++)
            {
                var mesh = meshes[m];
                if (mesh?.VertexList == null || mesh.VertexList.Count == 0)
                {
                    continue;
                }

                int vertexOffset = combinedVertices.Count;
                int srcVertCount = mesh.VertexList.Count;
                var relMatrix = partRelMatrices[m];
                var normalMatrix = partNormalMatrices[m];
                bool flipWinding = partFlipWinding[m];
                var boneRemap = perMeshBoneRemaps[m];
                bool meshHasSkinning = boneRemap != null && boneRemap.Length > 0;
                int rigidBoneIdx = rigidMeshBoneIndices[m];

                partMetas.Add(new CombinedPartTransformMeta(
                    NormalizeFramePath(mesh.Path),
                    vertexOffset,
                    srcVertCount,
                    relMatrix,
                    normalMatrix));

                for (int v = 0; v < srcVertCount; v++)
                {
                    var src = mesh.VertexList[v];
                    var dst = new ImportedVertex();

                    var transformedPos = System.Numerics.Vector3.Transform(
                        new System.Numerics.Vector3(src.Vertex.X, src.Vertex.Y, src.Vertex.Z),
                        relMatrix);
                    dst.Vertex = new Vector3(
                        SanitizeFloat(transformedPos.X, 0f),
                        SanitizeFloat(transformedPos.Y, 0f),
                        SanitizeFloat(transformedPos.Z, 0f));

                    if (anyNormal)
                    {
                        dst.Normal = mesh.hasNormal
                            ? TransformDirectionNormalized(src.Normal, normalMatrix)
                            : new Vector3(0f, 1f, 0f);
                    }

                    dst.UV = new float[8][];
                    for (int u = 0; u < 8; u++)
                    {
                        if (!anyUV[u]) continue;
                        if (mesh.hasUV != null && u < mesh.hasUV.Length && mesh.hasUV[u]
                            && src.UV != null && u < src.UV.Length && src.UV[u] != null && src.UV[u].Length >= 2)
                        {
                            dst.UV[u] = new[]
                            {
                                SanitizeFloat(src.UV[u][0], 0f),
                                SanitizeFloat(src.UV[u][1], 0f)
                            };
                        }
                        else
                        {
                            dst.UV[u] = new[] { 0f, 0f };
                        }
                    }

                    if (anyTangent)
                    {
                        if (mesh.hasTangent)
                        {
                            var transformedTangent = TransformDirectionNormalized(
                                new Vector3(src.Tangent.X, src.Tangent.Y, src.Tangent.Z),
                                relMatrix);
                            float tangentW = SanitizeFloat(src.Tangent.W, 1f);
                            dst.Tangent = new Vector4(
                                transformedTangent.X,
                                transformedTangent.Y,
                                transformedTangent.Z,
                                flipWinding ? -tangentW : tangentW);
                        }
                        else
                        {
                            dst.Tangent = new Vector4(1f, 0f, 0f, 1f);
                        }
                    }

                    if (anyColor)
                    {
                        dst.Color = mesh.hasColor
                            ? src.Color
                            : new Color(1f, 1f, 1f, 1f);
                    }

                    if (hasAnySkinning)
                    {
                        if (meshHasSkinning && boneRemap != null && src.BoneIndices != null && src.Weights != null)
                        {
                            int rawCount = Math.Min(src.BoneIndices.Length, src.Weights.Length);
                            int slotCount = Math.Max(4, rawCount);
                            dst.BoneIndices = new int[slotCount];
                            dst.Weights = new float[slotCount];
                            for (int k = 0; k < rawCount; k++)
                            {
                                int oldBoneIdx = src.BoneIndices[k];
                                float weight = SanitizeFloat(src.Weights[k], 0f);
                                if (oldBoneIdx >= 0 && oldBoneIdx < boneRemap.Length)
                                {
                                    dst.BoneIndices[k] = boneRemap[oldBoneIdx];
                                    dst.Weights[k] = weight;
                                }
                            }
                        }
                        else
                        {
                            dst.BoneIndices = new int[4];
                            dst.Weights = new float[4];
                            if (rigidBoneIdx >= 0)
                            {
                                dst.BoneIndices[0] = rigidBoneIdx;
                                dst.Weights[0] = 1.0f;
                            }
                        }
                    }

                    combinedVertices.Add(dst);
                }

                if (mesh.SubmeshList != null)
                {
                    for (int s = 0; s < mesh.SubmeshList.Count; s++)
                    {
                        var srcSub = mesh.SubmeshList[s];
                        if (srcSub?.FaceList == null) continue;

                        var dstSub = new ImportedSubmesh
                        {
                            Material = srcSub.Material,
                            BaseVertex = 0,
                            FaceList = new List<ImportedFace>(srcSub.FaceList.Count)
                        };

                        for (int f = 0; f < srcSub.FaceList.Count; f++)
                        {
                            var face = srcSub.FaceList[f];
                            if (!TryResolveCombinedFaceIndices(face, srcSub.BaseVertex, srcVertCount, out int i0, out int i1, out int i2))
                            {
                                continue;
                            }

                            var newFace = new ImportedFace
                            {
                                VertexIndices = flipWinding
                                    ? new[] { i0 + vertexOffset, i2 + vertexOffset, i1 + vertexOffset }
                                    : new[] { i0 + vertexOffset, i1 + vertexOffset, i2 + vertexOffset }
                            };
                            dstSub.FaceList.Add(newFace);
                        }

                        combinedSubmeshes.Add(dstSub);
                    }
                }
            }

            var combinedMesh = new ImportedMesh
            {
                Path = targetPath,
                VertexList = combinedVertices,
                SubmeshList = combinedSubmeshes,
                BoneList = hasAnySkinning ? unifiedBones : null,
                hasNormal = anyNormal,
                hasUV = anyUV,
                hasTangent = anyTangent,
                hasColor = anyColor
            };

            combinedMorphs = MergeMorphsForCombinedMesh(morphList, partMetas, targetPath);
            updatedAnimations = RemapBlendShapeAnimationTracks(animationList, partMetas, targetPath, cloneAnimationsForView);

            return combinedMesh;
        }

        private static List<ImportedMorph> MergeMorphsForCombinedMesh(
            List<ImportedMorph>? morphList,
            List<CombinedPartTransformMeta> partMetas,
            string combinedMeshPath)
        {
            if (morphList == null || morphList.Count == 0)
            {
                return morphList ?? new List<ImportedMorph>();
            }

            var metaByPath = new Dictionary<string, List<CombinedPartTransformMeta>>(StringComparer.Ordinal);
            for (int i = 0; i < partMetas.Count; i++)
            {
                var key = partMetas[i].NormalizedPath ?? string.Empty;
                if (!metaByPath.TryGetValue(key, out var list))
                {
                    list = new List<CombinedPartTransformMeta>(1);
                    metaByPath[key] = list;
                }
                list.Add(partMetas[i]);
            }

            var consumedCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var remainingMorphs = new List<ImportedMorph>();
            var mergedChannels = new List<ImportedMorphChannel>();
            var channelByName = new Dictionary<string, ImportedMorphChannel>(StringComparer.Ordinal);

            for (int i = 0; i < morphList.Count; i++)
            {
                var morph = morphList[i];
                if (morph == null) continue;

                var normPath = NormalizeFramePath(morph.Path) ?? string.Empty;
                if (!metaByPath.TryGetValue(normPath, out var candidates) || candidates.Count == 0)
                {
                    remainingMorphs.Add(morph);
                    continue;
                }

                consumedCounts.TryGetValue(normPath, out var useIndex);
                var meta = candidates[Math.Min(useIndex, candidates.Count - 1)];
                consumedCounts[normPath] = useIndex + 1;

                if (morph.Channels == null) continue;

                for (int c = 0; c < morph.Channels.Count; c++)
                {
                    var srcChannel = morph.Channels[c];
                    if (srcChannel == null) continue;

                    var channelName = srcChannel.Name ?? string.Empty;
                    if (!channelByName.TryGetValue(channelName, out var dstChannel))
                    {
                        dstChannel = new ImportedMorphChannel
                        {
                            Name = channelName,
                            KeyframeList = new List<ImportedMorphKeyframe>()
                        };
                        channelByName[channelName] = dstChannel;
                        mergedChannels.Add(dstChannel);
                    }

                    if (srcChannel.KeyframeList == null) continue;

                    for (int k = 0; k < srcChannel.KeyframeList.Count; k++)
                    {
                        var srcKf = srcChannel.KeyframeList[k];
                        if (srcKf == null) continue;

                        ImportedMorphKeyframe? dstKf = null;
                        for (int ek = 0; ek < dstChannel.KeyframeList.Count; ek++)
                        {
                            if (Math.Abs(dstChannel.KeyframeList[ek].Weight - srcKf.Weight) <= 1e-4f)
                            {
                                dstKf = dstChannel.KeyframeList[ek];
                                break;
                            }
                        }

                        if (dstKf == null)
                        {
                            dstKf = new ImportedMorphKeyframe
                            {
                                Weight = srcKf.Weight,
                                hasNormals = srcKf.hasNormals,
                                hasTangents = srcKf.hasTangents,
                                VertexList = new List<ImportedMorphVertex>(srcKf.VertexList?.Count ?? 0)
                            };
                            dstChannel.KeyframeList.Add(dstKf);
                        }
                        else
                        {
                            dstKf.hasNormals |= srcKf.hasNormals;
                            dstKf.hasTangents |= srcKf.hasTangents;
                        }

                        if (srcKf.VertexList == null) continue;

                        for (int v = 0; v < srcKf.VertexList.Count; v++)
                        {
                            var mv = srcKf.VertexList[v];
                            if (mv?.Vertex == null || mv.Index >= (uint)meta.VertexCount)
                            {
                                continue;
                            }

                            var transformedPos = System.Numerics.Vector3.Transform(
                                new System.Numerics.Vector3(mv.Vertex.Vertex.X, mv.Vertex.Vertex.Y, mv.Vertex.Vertex.Z),
                                meta.RelativeMatrix);

                            var dstVert = new ImportedVertex
                            {
                                Vertex = new Vector3(
                                    SanitizeFloat(transformedPos.X, 0f),
                                    SanitizeFloat(transformedPos.Y, 0f),
                                    SanitizeFloat(transformedPos.Z, 0f))
                            };

                            if (srcKf.hasNormals)
                            {
                                var transformedNormal = System.Numerics.Vector3.TransformNormal(
                                    new System.Numerics.Vector3(mv.Vertex.Normal.X, mv.Vertex.Normal.Y, mv.Vertex.Normal.Z),
                                    meta.NormalMatrix);
                                dstVert.Normal = new Vector3(
                                    SanitizeFloat(transformedNormal.X, 0f),
                                    SanitizeFloat(transformedNormal.Y, 0f),
                                    SanitizeFloat(transformedNormal.Z, 0f));
                            }

                            if (srcKf.hasTangents)
                            {
                                var transformedTangent = System.Numerics.Vector3.TransformNormal(
                                    new System.Numerics.Vector3(mv.Vertex.Tangent.X, mv.Vertex.Tangent.Y, mv.Vertex.Tangent.Z),
                                    meta.NormalMatrix);
                                dstVert.Tangent = new Vector4(
                                    SanitizeFloat(transformedTangent.X, 0f),
                                    SanitizeFloat(transformedTangent.Y, 0f),
                                    SanitizeFloat(transformedTangent.Z, 0f),
                                    SanitizeFloat(mv.Vertex.Tangent.W, 0f));
                            }

                            dstKf.VertexList.Add(new ImportedMorphVertex
                            {
                                Index = mv.Index + (uint)meta.VertexOffset,
                                Vertex = dstVert
                            });
                        }
                    }
                }
            }

            if (mergedChannels.Count > 0)
            {
                remainingMorphs.Add(new ImportedMorph
                {
                    Path = combinedMeshPath,
                    Channels = mergedChannels
                });
            }

            return remainingMorphs;
        }

        private static List<ImportedKeyframedAnimation> RemapBlendShapeAnimationTracks(
            List<ImportedKeyframedAnimation>? animationList,
            List<CombinedPartTransformMeta> partMetas,
            string combinedMeshPath,
            bool cloneForView)
        {
            if (animationList == null || animationList.Count == 0 || string.IsNullOrEmpty(combinedMeshPath))
            {
                return animationList ?? new List<ImportedKeyframedAnimation>();
            }

            var combinedNormalizedPaths = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < partMetas.Count; i++)
            {
                if (!string.IsNullOrEmpty(partMetas[i].NormalizedPath))
                {
                    combinedNormalizedPaths.Add(partMetas[i].NormalizedPath);
                }
            }

            var normalizedTargetPath = NormalizeFramePath(combinedMeshPath);
            bool anyRemapNeeded = false;
            for (int a = 0; a < animationList.Count && !anyRemapNeeded; a++)
            {
                var tracks = animationList[a]?.TrackList;
                if (tracks == null) continue;
                for (int t = 0; t < tracks.Count; t++)
                {
                    var tr = tracks[t];
                    if (tr?.BlendShapes != null && tr.BlendShapes.Count > 0)
                    {
                        var normTrackPath = NormalizeFramePath(tr.Path);
                        if (!string.IsNullOrEmpty(normTrackPath)
                            && combinedNormalizedPaths.Contains(normTrackPath)
                            && !string.Equals(normTrackPath, normalizedTargetPath, StringComparison.Ordinal))
                        {
                            anyRemapNeeded = true;
                            break;
                        }
                    }
                }
            }

            if (!anyRemapNeeded)
            {
                return animationList;
            }

            var result = cloneForView ? new List<ImportedKeyframedAnimation>(animationList.Count) : animationList;
            for (int a = 0; a < animationList.Count; a++)
            {
                var srcAnim = animationList[a];
                if (srcAnim == null)
                {
                    continue;
                }

                var dstAnim = srcAnim;
                if (cloneForView)
                {
                    dstAnim = new ImportedKeyframedAnimation
                    {
                        Name = srcAnim.Name,
                        SampleRate = srcAnim.SampleRate,
                        TrackList = new List<ImportedAnimationKeyframedTrack>(srcAnim.TrackList?.Count ?? 0)
                    };
                    if (srcAnim.TrackList != null)
                    {
                        for (int t = 0; t < srcAnim.TrackList.Count; t++)
                        {
                            var tr = srcAnim.TrackList[t];
                            dstAnim.TrackList.Add(new ImportedAnimationKeyframedTrack
                            {
                                Path = tr.Path,
                                Translations = tr.Translations,
                                Rotations = tr.Rotations,
                                Scalings = tr.Scalings,
                                Visibilities = tr.Visibilities,
                                BlendShapes = tr.BlendShapes != null
                                    ? new List<ImportedBlendShape>(tr.BlendShapes)
                                    : new List<ImportedBlendShape>()
                            });
                        }
                    }
                    result.Add(dstAnim);
                }

                if (dstAnim.TrackList == null) continue;

                int trackCount = dstAnim.TrackList.Count;
                for (int t = 0; t < trackCount; t++)
                {
                    var tr = dstAnim.TrackList[t];
                    if (tr?.BlendShapes == null || tr.BlendShapes.Count == 0) continue;

                    var normTrackPath = NormalizeFramePath(tr.Path);
                    if (!string.IsNullOrEmpty(normTrackPath)
                        && combinedNormalizedPaths.Contains(normTrackPath)
                        && !string.Equals(normTrackPath, normalizedTargetPath, StringComparison.Ordinal))
                    {
                        var targetTrack = dstAnim.FindTrack(combinedMeshPath);
                        for (int b = 0; b < tr.BlendShapes.Count; b++)
                        {
                            var srcBs = tr.BlendShapes[b];
                            if (srcBs == null) continue;
                            var dstBs = targetTrack.FindOrCreateBlendShape(NormalizeBlendShapeChannelName(srcBs.ChannelName));
                            if (dstBs.Keyframes.Count == 0 && srcBs.Keyframes.Count > 0)
                            {
                                dstBs.Keyframes.AddRange(srcBs.Keyframes);
                            }
                        }
                        tr.BlendShapes.Clear();
                    }
                }
            }

            return result;
        }

        private static bool TryResolveCombinedFaceIndices(ImportedFace face, int baseVertex, int vertexCount, out int index0, out int index1, out int index2)
        {
            index0 = index1 = index2 = 0;
            if (face?.VertexIndices == null || face.VertexIndices.Length < 3)
                return false;

            index0 = face.VertexIndices[0] + baseVertex;
            index1 = face.VertexIndices[1] + baseVertex;
            index2 = face.VertexIndices[2] + baseVertex;
            if ((uint)index0 < (uint)vertexCount && (uint)index1 < (uint)vertexCount && (uint)index2 < (uint)vertexCount)
                return true;

            index0 = face.VertexIndices[0];
            index1 = face.VertexIndices[1];
            index2 = face.VertexIndices[2];
            return (uint)index0 < (uint)vertexCount && (uint)index1 < (uint)vertexCount && (uint)index2 < (uint)vertexCount;
        }

        private static ImportedFrame? FindFrameForMesh(ImportedFrame? rootFrame, string? meshPath)
        {
            if (rootFrame == null || string.IsNullOrWhiteSpace(meshPath))
            {
                return null;
            }

            var normalizedTarget = NormalizeFramePath(meshPath);
            if (string.IsNullOrEmpty(normalizedTarget))
            {
                return null;
            }

            var visited = new HashSet<ImportedFrame>();
            var stack = new Stack<ImportedFrame>();
            stack.Push(rootFrame);

            ImportedFrame? uniqueSuffixMatch = null;
            int suffixMatchCount = 0;
            var suffix = "/" + normalizedTarget;

            while (stack.Count > 0)
            {
                var frame = stack.Pop();
                if (frame == null || !visited.Add(frame))
                {
                    continue;
                }

                var framePath = NormalizeFramePath(frame.Path);
                if (string.Equals(framePath, normalizedTarget, StringComparison.Ordinal))
                {
                    return frame;
                }

                if (framePath != null && framePath.EndsWith(suffix, StringComparison.Ordinal))
                {
                    uniqueSuffixMatch = frame;
                    suffixMatchCount++;
                }

                for (int i = frame.Count - 1; i >= 0; i--)
                {
                    stack.Push(frame[i]);
                }
            }

            return suffixMatchCount == 1 ? uniqueSuffixMatch : null;
        }

        private static System.Numerics.Matrix4x4 ComputeRelativeFrameMatrix(ImportedFrame? meshFrame, ImportedFrame? targetFrame)
        {
            if (meshFrame == null || ReferenceEquals(meshFrame, targetFrame))
            {
                return System.Numerics.Matrix4x4.Identity;
            }

            // Fast path: walk up parent chain looking for targetFrame (cycle-safe)
            var rel = System.Numerics.Matrix4x4.Identity;
            var visited = new HashSet<ImportedFrame>();
            var current = meshFrame;
            while (current != null && visited.Add(current))
            {
                rel = rel * BuildImportedFrameLocalMatrix(current);
                if (ReferenceEquals(current.Parent, targetFrame))
                {
                    return rel;
                }
                current = current.Parent;
            }

            // Fallback when targetFrame is not a direct ancestor of meshFrame
            if (targetFrame == null)
            {
                return rel;
            }

            var targetGlobal = ComputeFrameGlobalMatrix(targetFrame);
            return System.Numerics.Matrix4x4.Invert(targetGlobal, out var invTarget)
                ? rel * invTarget
                : rel;
        }

        private static System.Numerics.Matrix4x4 ComputeFrameGlobalMatrix(ImportedFrame frame)
        {
            var global = System.Numerics.Matrix4x4.Identity;
            var visited = new HashSet<ImportedFrame>();
            var current = frame;
            while (current != null && visited.Add(current))
            {
                global = global * BuildImportedFrameLocalMatrix(current);
                current = current.Parent;
            }
            return global;
        }

        private static System.Numerics.Matrix4x4 BuildImportedFrameLocalMatrix(ImportedFrame frame)
        {
            var scale = System.Numerics.Matrix4x4.CreateScale(
                SanitizeFloat(frame.LocalScale.X, 0f),
                SanitizeFloat(frame.LocalScale.Y, 0f),
                SanitizeFloat(frame.LocalScale.Z, 0f));
            var rotation =
                System.Numerics.Matrix4x4.CreateRotationX(DegreesToRadians(frame.LocalRotation.X)) *
                System.Numerics.Matrix4x4.CreateRotationY(DegreesToRadians(frame.LocalRotation.Y)) *
                System.Numerics.Matrix4x4.CreateRotationZ(DegreesToRadians(frame.LocalRotation.Z));
            var translation = System.Numerics.Matrix4x4.CreateTranslation(
                SanitizeFloat(frame.LocalPosition.X, 0f),
                SanitizeFloat(frame.LocalPosition.Y, 0f),
                SanitizeFloat(frame.LocalPosition.Z, 0f));
            return scale * rotation * translation;
        }

        private static System.Numerics.Matrix4x4 BuildNormalMatrix(System.Numerics.Matrix4x4 relMatrix, out bool flipWinding)
        {
            var upper = new System.Numerics.Matrix4x4(
                relMatrix.M11, relMatrix.M12, relMatrix.M13, 0f,
                relMatrix.M21, relMatrix.M22, relMatrix.M23, 0f,
                relMatrix.M31, relMatrix.M32, relMatrix.M33, 0f,
                0f, 0f, 0f, 1f);

            var det = upper.GetDeterminant();
            flipWinding = det < -1e-9f;
            if (Math.Abs(det) > 1e-12f && System.Numerics.Matrix4x4.Invert(upper, out var invUpper))
            {
                return System.Numerics.Matrix4x4.Transpose(invUpper);
            }

            return upper;
        }

        private static Vector3 TransformDirectionNormalized(Vector3 dir, System.Numerics.Matrix4x4 normalMatrix)
        {
            var transformed = System.Numerics.Vector3.TransformNormal(
                new System.Numerics.Vector3(
                    SanitizeFloat(dir.X, 0f),
                    SanitizeFloat(dir.Y, 0f),
                    SanitizeFloat(dir.Z, 0f)),
                normalMatrix);
            var lenSq = transformed.LengthSquared();
            if (lenSq > 1e-12f && !float.IsNaN(lenSq) && !float.IsInfinity(lenSq))
            {
                var invLen = 1f / (float)Math.Sqrt(lenSq);
                return new Vector3(transformed.X * invLen, transformed.Y * invLen, transformed.Z * invLen);
            }

            var origLenSq = dir.X * dir.X + dir.Y * dir.Y + dir.Z * dir.Z;
            if (origLenSq > 1e-12f && !float.IsNaN(origLenSq) && !float.IsInfinity(origLenSq))
            {
                var invLen = 1f / (float)Math.Sqrt(origLenSq);
                return new Vector3(dir.X * invLen, dir.Y * invLen, dir.Z * invLen);
            }

            return Vector3.Zero;
        }

        private static Matrix4x4 TransformBindPoseMatrix(Matrix4x4 bindPose, System.Numerics.Matrix4x4 relMatrix)
        {
            if (relMatrix.IsIdentity)
            {
                return bindPose;
            }

            if (System.Numerics.Matrix4x4.Invert(relMatrix, out var invRel))
            {
                var adjusted = invRel * ToNumericsMatrix(bindPose);
                return FromNumericsMatrix(adjusted);
            }

            return bindPose;
        }

        private static System.Numerics.Matrix4x4 ToNumericsMatrix(Matrix4x4 m)
        {
            return new System.Numerics.Matrix4x4(
                m[0, 0], m[0, 1], m[0, 2], m[0, 3],
                m[1, 0], m[1, 1], m[1, 2], m[1, 3],
                m[2, 0], m[2, 1], m[2, 2], m[2, 3],
                m[3, 0], m[3, 1], m[3, 2], m[3, 3]);
        }

        private static Matrix4x4 FromNumericsMatrix(System.Numerics.Matrix4x4 m)
        {
            var result = new Matrix4x4();
            result[0, 0] = SanitizeFloat(m.M11, 0f); result[0, 1] = SanitizeFloat(m.M12, 0f); result[0, 2] = SanitizeFloat(m.M13, 0f); result[0, 3] = SanitizeFloat(m.M14, 0f);
            result[1, 0] = SanitizeFloat(m.M21, 0f); result[1, 1] = SanitizeFloat(m.M22, 0f); result[1, 2] = SanitizeFloat(m.M23, 0f); result[1, 3] = SanitizeFloat(m.M24, 0f);
            result[2, 0] = SanitizeFloat(m.M31, 0f); result[2, 1] = SanitizeFloat(m.M32, 0f); result[2, 2] = SanitizeFloat(m.M33, 0f); result[2, 3] = SanitizeFloat(m.M34, 0f);
            result[3, 0] = SanitizeFloat(m.M41, 0f); result[3, 1] = SanitizeFloat(m.M42, 0f); result[3, 2] = SanitizeFloat(m.M43, 0f); result[3, 3] = SanitizeFloat(m.M44, 0f);
            return result;
        }

        private static float DegreesToRadians(float degrees)
        {
            return (float)(SanitizeFloat(degrees, 0f) * Math.PI / 180.0);
        }

        private static float SanitizeFloat(float value, float fallback)
        {
            return (float.IsNaN(value) || float.IsInfinity(value)) ? fallback : value;
        }

        private static string NormalizeFramePath(string? path)
        {
            return string.IsNullOrEmpty(path) ? string.Empty : path.Replace('\\', '/').Trim('/');
        }
    }
}
