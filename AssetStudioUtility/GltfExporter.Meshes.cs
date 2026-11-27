using SkiaSharp;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AssetStudio
{
    public sealed partial class GltfExporter
    {
        // ------------------------------------------------------------------
        // Meshes, Skinning & Morph Targets
        // ------------------------------------------------------------------

        private (Dictionary<string, ImportedMorph> ByPath, Dictionary<int, ImportedMorph> ByNode) BuildMorphLookup(List<ImportedMorph>? morphList)
        {
            var byPath = new Dictionary<string, ImportedMorph>(StringComparer.Ordinal);
            var byNode = new Dictionary<int, ImportedMorph>();
            if (!_exportBlendShape || morphList == null)
            {
                return (byPath, byNode);
            }

            for (int i = 0; i < morphList.Count; i++)
            {
                var morph = morphList[i];
                if (morph == null || morph.Channels == null || morph.Channels.Count == 0)
                {
                    continue;
                }

                var normPath = NormalizeFramePath(morph.Path);
                if (!byPath.ContainsKey(normPath))
                {
                    byPath[normPath] = morph;
                }

                if (TryResolveNodeIndex(morph.Path, out int morphNodeIdx) && !byNode.ContainsKey(morphNodeIdx))
                {
                    byNode[morphNodeIdx] = morph;
                }
            }

            return (byPath, byNode);
        }

        private void BuildSkeletonGroups(List<ImportedMesh>? meshList)
        {
            _meshIndexToSkeletonGroup.Clear();
            if (!_exportSkins || meshList == null || meshList.Count == 0)
            {
                return;
            }

            var entries = new List<(int MeshIndex, HashSet<int> Joints, int CommonAncestor)>();
            for (int m = 0; m < meshList.Count; m++)
            {
                var mesh = meshList[m];
                if (mesh?.BoneList == null || mesh.BoneList.Count == 0)
                {
                    continue;
                }

                var joints = new HashSet<int>();
                for (int b = 0; b < mesh.BoneList.Count; b++)
                {
                    if (TryResolveNodeIndex(mesh.BoneList[b]?.Path, out int jointIdx))
                    {
                        joints.Add(jointIdx);
                    }
                }

                if (joints.Count == 0
                    || (mesh.BoneList.Count > 1 && joints.Count == 1 && _sceneRootNodes.Count > 0 && joints.Contains(_sceneRootNodes[0])))
                {
                    continue;
                }

                int? ca = FindCommonAncestor(joints.ToList());
                if (ca.HasValue)
                {
                    entries.Add((m, joints, ca.Value));
                }
            }

            if (entries.Count == 0)
            {
                return;
            }

            int count = entries.Count;
            var parent = new int[count];
            for (int i = 0; i < count; i++) parent[i] = i;

            int Find(int x)
            {
                while (parent[x] != x)
                {
                    parent[x] = parent[parent[x]];
                    x = parent[x];
                }
                return x;
            }

            void Union(int a, int b)
            {
                int ra = Find(a);
                int rb = Find(b);
                if (ra != rb) parent[ra] = rb;
            }

            for (int a = 0; a < count; a++)
            {
                for (int b = a + 1; b < count; b++)
                {
                    if (Find(a) == Find(b))
                    {
                        continue;
                    }

                    bool merge = entries[a].Joints.Overlaps(entries[b].Joints);
                    if (!merge)
                    {
                        int caA = entries[a].CommonAncestor;
                        int caB = entries[b].CommonAncestor;
                        if (IsAncestorOrSame(caA, caB) || IsAncestorOrSame(caB, caA))
                        {
                            merge = true;
                        }
                        else
                        {
                            int? shared = FindCommonAncestor(new[] { caA, caB });
                            if (shared.HasValue && _nodeParentIndices[shared.Value] >= 0)
                            {
                                // Share a non-scene-root skeleton container (e.g. Character/root).
                                merge = true;
                            }
                            else if (_nodeParentIndices[caA] >= 0 && _nodeParentIndices[caA] == _nodeParentIndices[caB])
                            {
                                // Direct sibling bone roots under the same immediate parent.
                                merge = true;
                            }
                        }
                    }

                    if (merge)
                    {
                        Union(a, b);
                    }
                }
            }

            var grouped = new Dictionary<int, List<int>>();
            for (int i = 0; i < count; i++)
            {
                int r = Find(i);
                if (!grouped.TryGetValue(r, out var list))
                {
                    list = new List<int>();
                    grouped[r] = list;
                }
                list.Add(i);
            }

            foreach (var list in grouped.Values)
            {
                var allJoints = new HashSet<int>();
                for (int i = 0; i < list.Count; i++)
                {
                    allJoints.UnionWith(entries[list[i]].Joints);
                }

                int? groupCommon = FindCommonAncestor(allJoints.ToList());
                if (!groupCommon.HasValue)
                {
                    continue;
                }

                // Blender (glTF-Blender-IO) creates one Armature at the deepest common ancestor of
                // (joints + skeleton), stepping up to parent if that ancestor is itself a joint.
                // Setting skin.skeleton to the non-joint Armature container keeps multi-part characters
                // unified under one Armature with consistent Armature-space inverse bind matrices.
                int armatureNode = allJoints.Contains(groupCommon.Value)
                    ? _nodeParentIndices[groupCommon.Value]
                    : groupCommon.Value;
                int skeletonNode = armatureNode >= 0 ? armatureNode : groupCommon.Value;
                var group = new SkinSkeletonGroup(skeletonNode, armatureNode);

                for (int i = 0; i < list.Count; i++)
                {
                    _meshIndexToSkeletonGroup[entries[list[i]].MeshIndex] = group;
                }
            }
        }

        private void ExportMesh(
            ImportedMesh? mesh,
            int meshIndex,
            Dictionary<string, ImportedMorph> morphByPath,
            Dictionary<int, ImportedMorph> morphByNode)
        {
            if (mesh == null || mesh.VertexList == null || mesh.VertexList.Count == 0)
            {
                return;
            }

            int vertexCount = mesh.VertexList.Count;

            // 1. Collect valid primitives per submesh first so we skip meshes with 0 valid triangles.
            var submeshList = mesh.SubmeshList;
            var primitivePayloads = new List<(List<int> Indices, int MaxIndex, int? MaterialIndex)>();

            if (submeshList != null && submeshList.Count > 0)
            {
                for (int s = 0; s < submeshList.Count; s++)
                {
                    var sub = submeshList[s];
                    var faceList = sub?.FaceList;
                    if (faceList == null || faceList.Count == 0)
                    {
                        continue;
                    }

                    var indices = new List<int>(faceList.Count * 3);
                    int maxIdx = 0;
                    int baseVertex = sub!.BaseVertex;

                    for (int f = 0; f < faceList.Count; f++)
                    {
                        if (!TryResolveFaceIndices(faceList[f], baseVertex, vertexCount, out int i0, out int i1, out int i2))
                        {
                            continue;
                        }

                        // ModelConverter already reversed Unity CW -> Right-Handed CCW winding.
                        indices.Add(i0);
                        indices.Add(i1);
                        indices.Add(i2);
                        if (i0 > maxIdx) maxIdx = i0;
                        if (i1 > maxIdx) maxIdx = i1;
                        if (i2 > maxIdx) maxIdx = i2;
                    }

                    if (indices.Count == 0)
                    {
                        continue;
                    }

                    int? matIdx = null;
                    if (!string.IsNullOrEmpty(sub.Material) && _materialIndexByName.TryGetValue(sub.Material, out int foundMat))
                    {
                        matIdx = foundMat;
                    }

                    primitivePayloads.Add((indices, maxIdx, matIdx));
                }
            }

            if (primitivePayloads.Count == 0)
            {
                return;
            }

            string meshName = !string.IsNullOrWhiteSpace(mesh.SourceMeshName)
                ? mesh.SourceMeshName!
                : (string.IsNullOrWhiteSpace(mesh.Path)
                    ? $"Mesh_{_meshes.Count}"
                    : mesh.Path.Substring(mesh.Path.LastIndexOf('/') + 1));

            // 2. Resolve skinning joints, mesh attachment node, and optional Armature-space canonicalization.
            bool isSkinned = _exportSkins && mesh.BoneList != null && mesh.BoneList.Count > 0;
            List<int>? skinJointNodes = null;
            int resolvedTargetNode = mesh.TargetFrame != null && _nodeByFrame.TryGetValue(mesh.TargetFrame, out int exactFrameNode)
                ? exactFrameNode
                : (TryResolveNodeIndex(mesh.Path, out int foundTargetNode) ? foundTargetNode : -1);

            if (isSkinned)
            {
                int boneCount = mesh.BoneList!.Count;
                var distinctResolvedJoints = new HashSet<int>();
                for (int b = 0; b < boneCount; b++)
                {
                    if (TryResolveNodeIndex(mesh.BoneList[b]?.Path, out int rj))
                    {
                        distinctResolvedJoints.Add(rj);
                    }
                }

                bool collapsedToSceneRootOnly = boneCount > 1
                    && distinctResolvedJoints.Count == 1
                    && _sceneRootNodes.Count > 0
                    && distinctResolvedJoints.Contains(_sceneRootNodes[0]);

                if (distinctResolvedJoints.Count == 0 || collapsedToSceneRootOnly)
                {
                    // When no real Transform skeleton nodes exist for the mesh's bones, export as a clean static mesh
                    // in bind-pose rest space rather than spawning identity *_Dup_N or unparented placeholder nodes.
                    isSkinned = false;
                }
            }

            if (isSkinned)
            {
                int boneCount = mesh.BoneList!.Count;
                skinJointNodes = new List<int>(boneCount);

                var meshWorldFallback = resolvedTargetNode >= 0
                    ? _nodeGlobalRowMatrices[resolvedTargetNode]
                    : System.Numerics.Matrix4x4.Identity;

                int fallbackParent = _sceneRootNodes.Count > 0 ? _sceneRootNodes[0] : -1;
                var usedJointSet = new HashSet<int>();

                for (int b = 0; b < boneCount; b++)
                {
                    var bone = mesh.BoneList[b];
                    if (TryResolveNodeIndex(bone.Path, out int resolvedJoint))
                    {
                        if (usedJointSet.Add(resolvedJoint))
                        {
                            skinJointNodes.Add(resolvedJoint);
                        }
                        else
                        {
                            // glTF 2.0 requires unique node indices in skin.joints; create an identity child
                            // under the resolved joint so duplicate bone slots still inherit all animations.
                            int dupNode = CreateExtraNode($"{_nodes[resolvedJoint].Name}_Dup_{b}", resolvedJoint);
                            usedJointSet.Add(dupNode);
                            skinJointNodes.Add(dupNode);
                        }
                    }
                    else
                    {
                        string boneName = string.IsNullOrWhiteSpace(bone.Path)
                            ? $"Bone_{b}"
                            : bone.Path.Substring(bone.Path.LastIndexOf('/') + 1);

                        System.Numerics.Matrix4x4? localHint = null;
                        var ibmRow = ConvertUnityBindPoseToRowMatrix(bone.Matrix);
                        if (System.Numerics.Matrix4x4.Invert(ibmRow, out var invIbm))
                        {
                            var jointGlobal = invIbm * meshWorldFallback;
                            var parentGlobal = fallbackParent >= 0 ? _nodeGlobalRowMatrices[fallbackParent] : System.Numerics.Matrix4x4.Identity;
                            if (System.Numerics.Matrix4x4.Invert(parentGlobal, out var invParent))
                            {
                                localHint = jointGlobal * invParent;
                            }
                        }

                        int placeholderNode = CreateExtraNode(boneName, fallbackParent, localHint);
                        usedJointSet.Add(placeholderNode);
                        skinJointNodes.Add(placeholderNode);
                    }
                }
            }

            // Determine the node that will own this mesh (creating a clean child/root node when needed).
            int meshNodeIdx;
            if (resolvedTargetNode >= 0)
            {
                bool targetIsAncestorOfOwnJoint = false;
                if (isSkinned && skinJointNodes != null)
                {
                    for (int j = 0; j < skinJointNodes.Count; j++)
                    {
                        if (IsAncestorOrSame(resolvedTargetNode, skinJointNodes[j]))
                        {
                            targetIsAncestorOfOwnJoint = true;
                            break;
                        }
                    }
                }

                if (_nodes[resolvedTargetNode].Mesh.HasValue || targetIsAncestorOfOwnJoint)
                {
                    meshNodeIdx = CreateExtraNode($"{meshName}_Mesh", resolvedTargetNode);
                }
                else
                {
                    meshNodeIdx = resolvedTargetNode;
                }
            }
            else
            {
                int parentForOrphan = _sceneRootNodes.Count > 0 ? _sceneRootNodes[0] : -1;
                meshNodeIdx = CreateExtraNode(meshName, parentForOrphan);
            }

            // Compute Armature-space canonicalization transform A = G_mesh * inv(G_arma) for skinned meshes.
            // Because (v * A) * (inv(A) * IBM) * G_joint(t) === v * IBM * G_joint(t), this is an exact
            // algebraic identity for runtime glTF skinning while unifying multi-part Unity characters
            // (whose SkinnedMeshRenderers carry differing local offsets/rotations/scales) into a single
            // consistent Armature rest space for Blender and other DCC tools.
            bool applyMeshToArmature = false;
            var meshToArmature = System.Numerics.Matrix4x4.Identity;
            var invMeshToArmature = System.Numerics.Matrix4x4.Identity;
            var meshNormalMatrix = System.Numerics.Matrix4x4.Identity;
            bool flipWinding = false;
            int armatureNode = -1;
            int? skinSkeletonNode = null;
            System.Numerics.Matrix4x4[]? ibmMatrices = null;

            if (isSkinned && skinJointNodes != null && mesh.BoneList != null)
            {
                if (_meshIndexToSkeletonGroup.TryGetValue(meshIndex, out var skelGroup))
                {
                    skinSkeletonNode = skelGroup.SkeletonNode;
                    armatureNode = skelGroup.ArmatureNode;
                }
                else
                {
                    int? localCommon = FindCommonAncestor(skinJointNodes);
                    if (localCommon.HasValue)
                    {
                        var jointSet = new HashSet<int>(skinJointNodes);
                        armatureNode = jointSet.Contains(localCommon.Value)
                            ? _nodeParentIndices[localCommon.Value]
                            : localCommon.Value;
                        skinSkeletonNode = armatureNode >= 0 ? armatureNode : localCommon.Value;
                    }
                }

                int boneCount = mesh.BoneList.Count;
                ibmMatrices = new System.Numerics.Matrix4x4[boneCount];
                var ibmFinite = new bool[boneCount];
                var meshGlobalRow = _nodeGlobalRowMatrices[meshNodeIdx];
                var armaGlobalRow = armatureNode >= 0 ? _nodeGlobalRowMatrices[armatureNode] : System.Numerics.Matrix4x4.Identity;

                for (int b = 0; b < boneCount; b++)
                {
                    var ibmRow = ConvertUnityBindPoseToRowMatrix(mesh.BoneList[b].Matrix);
                    if (IsFiniteInvertibleAffineMatrix(ibmRow))
                    {
                        ibmMatrices[b] = ibmRow;
                        ibmFinite[b] = true;
                    }
                }

                if (System.Numerics.Matrix4x4.Invert(armaGlobalRow, out var invArmaGlobal))
                {
                    var candidateA = meshGlobalRow * invArmaGlobal;
                    candidateA.M14 = 0f;
                    candidateA.M24 = 0f;
                    candidateA.M34 = 0f;
                    candidateA.M44 = 1f;

                    if (!IsMatrixNearlyIdentity(candidateA)
                        && IsFiniteInvertibleAffineMatrix(candidateA)
                        && System.Numerics.Matrix4x4.Invert(candidateA, out var candidateInvA))
                    {
                        candidateInvA.M14 = 0f;
                        candidateInvA.M24 = 0f;
                        candidateInvA.M34 = 0f;
                        candidateInvA.M44 = 1f;

                        double errBefore = 0.0;
                        double errAfter = 0.0;
                        int validCount = 0;

                        for (int b = 0; b < boneCount; b++)
                        {
                            if (!ibmFinite[b])
                            {
                                continue;
                            }

                            var localJointInArma = _nodeGlobalRowMatrices[skinJointNodes[b]] * invArmaGlobal;
                            errBefore += MatrixDistanceFromIdentity(ibmMatrices[b] * localJointInArma);
                            errAfter += MatrixDistanceFromIdentity((candidateInvA * ibmMatrices[b]) * localJointInArma);
                            validCount++;
                        }

                        if (validCount > 0)
                        {
                            double avgBefore = errBefore / validCount;
                            double avgAfter = errAfter / validCount;
                            if (avgAfter < avgBefore * 0.5 || (avgAfter < 1e-2 && avgBefore > 1e-2))
                            {
                                applyMeshToArmature = true;
                                meshToArmature = candidateA;
                                invMeshToArmature = candidateInvA;
                                meshNormalMatrix = BuildNormalMatrix(candidateA, out flipWinding);
                            }
                        }
                    }
                }

                // Finalize IBMs (apply inv(A) when canonicalized, and compute clean fallbacks for any non-finite IBM).
                var effectiveMeshRefRow = applyMeshToArmature ? armaGlobalRow : meshGlobalRow;
                for (int b = 0; b < boneCount; b++)
                {
                    if (ibmFinite[b])
                    {
                        if (applyMeshToArmature)
                        {
                            var adjusted = invMeshToArmature * ibmMatrices[b];
                            adjusted.M14 = 0f;
                            adjusted.M24 = 0f;
                            adjusted.M34 = 0f;
                            adjusted.M44 = 1f;
                            ibmMatrices[b] = adjusted;
                        }
                    }
                    else
                    {
                        var jointGlobalRow = _nodeGlobalRowMatrices[skinJointNodes[b]];
                        if (System.Numerics.Matrix4x4.Invert(jointGlobalRow, out var invJoint))
                        {
                            var fallbackIbm = effectiveMeshRefRow * invJoint;
                            fallbackIbm.M14 = 0f;
                            fallbackIbm.M24 = 0f;
                            fallbackIbm.M34 = 0f;
                            fallbackIbm.M44 = 1f;
                            ibmMatrices[b] = fallbackIbm;
                        }
                        else
                        {
                            ibmMatrices[b] = System.Numerics.Matrix4x4.Identity;
                        }
                    }
                }
            }

            if (flipWinding)
            {
                for (int p = 0; p < primitivePayloads.Count; p++)
                {
                    var idxList = primitivePayloads[p].Indices;
                    for (int t = 0; t + 2 < idxList.Count; t += 3)
                    {
                        (idxList[t + 1], idxList[t + 2]) = (idxList[t + 2], idxList[t + 1]);
                    }
                }
            }

            // 2b. Check morph presence up front so non-skinned, non-morph meshes can reuse shared glTF meshes/buffers.
            var normMeshPath = NormalizeFramePath(mesh.Path);
            ImportedMorph? preResolvedMorph = null;
            if (_exportBlendShape)
            {
                if (!morphByPath.TryGetValue(normMeshPath, out preResolvedMorph) && resolvedTargetNode >= 0)
                {
                    morphByNode.TryGetValue(resolvedTargetNode, out preResolvedMorph);
                }
            }

            bool hasMorphChannels = preResolvedMorph != null && preResolvedMorph.Channels != null && preResolvedMorph.Channels.Count > 0;
            bool canUseInstancing = _enableInstancing && !isSkinned && !hasMorphChannels && !flipWinding;
            string? geometryCacheKey = null;
            string? meshInstanceCacheKey = null;

            if (canUseInstancing)
            {
                geometryCacheKey = ComputeGeometryCacheKey(mesh, primitivePayloads);
                meshInstanceCacheKey = BuildMeshInstanceCacheKey(geometryCacheKey, primitivePayloads);

                // Tier 1: Exact same geometry + exact same materials -> direct glTF node mesh instancing!
                if (_sharedMeshIndexByInstanceKey.TryGetValue(meshInstanceCacheKey, out int existingMeshIdx))
                {
                    _nodes[meshNodeIdx].Mesh = existingMeshIdx;
                    _totalMeshInstances++;
                    _reusedMeshCount++;
                    _reusedGeometryCount++;
                    return;
                }

                // Tier 2: Exact same geometry + different material slots -> reuse vertex & index accessors with zero duplicate bytes!
                if (_sharedGeometryByKey.TryGetValue(geometryCacheKey, out var cachedGeometry)
                    && cachedGeometry.PrimitiveIndicesAccessors.Count == primitivePayloads.Count)
                {
                    var variantMesh = new GltfMesh
                    {
                        Name = meshName
                    };

                    if ((_exportVertexColors || ExportVertexColors) && mesh.hasColor)
                    {
                        EnsureVertexColorAccessorsForGeometry(mesh, vertexCount, primitivePayloads, cachedGeometry);
                    }

                    for (int p = 0; p < primitivePayloads.Count; p++)
                    {
                        var matIdx = primitivePayloads[p].MaterialIndex;
                        var primAttributes = new Dictionary<string, int>(cachedGeometry.SharedAttributes, StringComparer.Ordinal);
                        if (cachedGeometry.OpaqueVertexColorAccessor.HasValue || cachedGeometry.BlendVertexColorAccessor.HasValue)
                        {
                            bool isBlend = matIdx.HasValue
                                && matIdx.Value >= 0
                                && matIdx.Value < _materials.Count
                                && string.Equals(_materials[matIdx.Value].AlphaMode, "BLEND", StringComparison.Ordinal);
                            int? colorAccessor = isBlend
                                ? (cachedGeometry.BlendVertexColorAccessor ?? cachedGeometry.OpaqueVertexColorAccessor)
                                : (cachedGeometry.OpaqueVertexColorAccessor ?? cachedGeometry.BlendVertexColorAccessor);
                            if (colorAccessor.HasValue)
                            {
                                primAttributes["COLOR_0"] = colorAccessor.Value;
                            }
                        }

                        variantMesh.Primitives.Add(new GltfPrimitive
                        {
                            Attributes = primAttributes,
                            Indices = cachedGeometry.PrimitiveIndicesAccessors[p],
                            Material = matIdx
                        });
                    }

                    int variantMeshIdx = _meshes.Count;
                    _meshes.Add(variantMesh);
                    _nodes[meshNodeIdx].Mesh = variantMeshIdx;
                    _sharedMeshIndexByInstanceKey[meshInstanceCacheKey] = variantMeshIdx;
                    _totalMeshInstances++;
                    _reusedGeometryCount++;
                    return;
                }
            }

            // 3. Build shared vertex attribute accessors.
            var sharedAttributes = new Dictionary<string, int>(StringComparer.Ordinal);

            var positions = new System.Numerics.Vector3[vertexCount];
            for (int i = 0; i < vertexCount; i++)
            {
                var v = mesh.VertexList[i].Vertex;
                var pos = new System.Numerics.Vector3(
                    SanitizeFloat(v.X * _scaleFactor),
                    SanitizeFloat(v.Y * _scaleFactor),
                    SanitizeFloat(v.Z * _scaleFactor));
                if (applyMeshToArmature)
                {
                    pos = System.Numerics.Vector3.Transform(pos, meshToArmature);
                }
                positions[i] = new System.Numerics.Vector3(
                    SanitizeFloat(pos.X),
                    SanitizeFloat(pos.Y),
                    SanitizeFloat(pos.Z));
            }
            sharedAttributes["POSITION"] = _buffer.AddVec3Accessor(positions, includeMinMax: true, target: TargetArrayBuffer);

            if (mesh.hasNormal)
            {
                var normals = new System.Numerics.Vector3[vertexCount];
                for (int i = 0; i < vertexCount; i++)
                {
                    var n = mesh.VertexList[i].Normal;
                    var nv = new System.Numerics.Vector3(SanitizeFloat(n.X), SanitizeFloat(n.Y), SanitizeFloat(n.Z));
                    if (applyMeshToArmature)
                    {
                        nv = System.Numerics.Vector3.TransformNormal(nv, meshNormalMatrix);
                    }
                    normals[i] = NormalizeVector3Safe(nv, System.Numerics.Vector3.UnitY);
                }
                sharedAttributes["NORMAL"] = _buffer.AddVec3Accessor(normals, includeMinMax: false, target: TargetArrayBuffer);
            }

            if (mesh.hasTangent)
            {
                var tangents = new System.Numerics.Vector4[vertexCount];
                for (int i = 0; i < vertexCount; i++)
                {
                    var t = mesh.VertexList[i].Tangent;
                    var tv = new System.Numerics.Vector3(SanitizeFloat(t.X), SanitizeFloat(t.Y), SanitizeFloat(t.Z));
                    if (applyMeshToArmature)
                    {
                        tv = System.Numerics.Vector3.TransformNormal(tv, meshToArmature);
                    }
                    var xyz = NormalizeVector3Safe(tv, System.Numerics.Vector3.UnitX);
                    float w = SanitizeFloat(t.W, 1f) < 0f ? -1f : 1f;
                    if (flipWinding) w = -w;
                    tangents[i] = new System.Numerics.Vector4(xyz.X, xyz.Y, xyz.Z, w);
                }
                sharedAttributes["TANGENT"] = _buffer.AddVec4Accessor(tangents, target: TargetArrayBuffer);
            }

            if (mesh.hasUV != null)
            {
                int gltfTexCoordSlot = 0;
                for (int uv = 0; uv < mesh.hasUV.Length; uv++)
                {
                    if (!mesh.hasUV[uv])
                    {
                        continue;
                    }

                    bool hasAnyUvData = false;
                    var uvs = new System.Numerics.Vector2[vertexCount];
                    for (int i = 0; i < vertexCount; i++)
                    {
                        var uvChannels = mesh.VertexList[i].UV;
                        if (uvChannels != null && uv < uvChannels.Length && uvChannels[uv] != null && uvChannels[uv].Length >= 2)
                        {
                            hasAnyUvData = true;
                            float u = SanitizeFloat(uvChannels[uv][0]);
                            // Unity textures are exported flipped top-down (flip=true), and glTF (0,0) is TOP-LEFT:
                            // v_gltf = 1 - v_unity.
                            float v = SanitizeFloat(1f - uvChannels[uv][1]);
                            uvs[i] = new System.Numerics.Vector2(u, v);
                        }
                        else if (uvChannels != null && uv < uvChannels.Length && uvChannels[uv] != null && uvChannels[uv].Length == 1)
                        {
                            hasAnyUvData = true;
                            uvs[i] = new System.Numerics.Vector2(SanitizeFloat(uvChannels[uv][0]), 1f);
                        }
                    }

                    if (hasAnyUvData)
                    {
                        sharedAttributes[$"TEXCOORD_{gltfTexCoordSlot}"] = _buffer.AddVec2Accessor(uvs, target: TargetArrayBuffer);
                        gltfTexCoordSlot++;
                    }
                }
            }

            int? opaqueVertexColorAccessor = null;
            int? blendVertexColorAccessor = null;
            if ((_exportVertexColors || ExportVertexColors) && mesh.hasColor)
            {
                bool needsOpaqueColors = false;
                bool needsBlendColors = false;
                for (int p = 0; p < primitivePayloads.Count; p++)
                {
                    var matIdx = primitivePayloads[p].MaterialIndex;
                    bool isBlend = matIdx.HasValue
                        && matIdx.Value >= 0
                        && matIdx.Value < _materials.Count
                        && string.Equals(_materials[matIdx.Value].AlphaMode, "BLEND", StringComparison.Ordinal);
                    if (isBlend)
                    {
                        needsBlendColors = true;
                    }
                    else
                    {
                        needsOpaqueColors = true;
                    }
                }

                if (needsOpaqueColors)
                {
                    var opaqueColors = new System.Numerics.Vector4[vertexCount];
                    for (int i = 0; i < vertexCount; i++)
                    {
                        var c = mesh.VertexList[i].Color;
                        opaqueColors[i] = new System.Numerics.Vector4(
                            Clamp01(SanitizeFloat(c.R, 1f)),
                            Clamp01(SanitizeFloat(c.G, 1f)),
                            Clamp01(SanitizeFloat(c.B, 1f)),
                            1f);
                    }
                    opaqueVertexColorAccessor = _buffer.AddVec4Accessor(opaqueColors, target: TargetArrayBuffer);
                }

                if (needsBlendColors)
                {
                    var blendColors = new System.Numerics.Vector4[vertexCount];
                    for (int i = 0; i < vertexCount; i++)
                    {
                        var c = mesh.VertexList[i].Color;
                        blendColors[i] = new System.Numerics.Vector4(
                            Clamp01(SanitizeFloat(c.R, 1f)),
                            Clamp01(SanitizeFloat(c.G, 1f)),
                            Clamp01(SanitizeFloat(c.B, 1f)),
                            Clamp01(SanitizeFloat(c.A, 1f)));
                    }
                    blendVertexColorAccessor = _buffer.AddVec4Accessor(blendColors, target: TargetArrayBuffer);
                }
            }

            if (isSkinned && mesh.BoneList != null)
            {
                int boneCount = mesh.BoneList.Count;
                var jointsData = new ushort[vertexCount * 4];
                var weightsData = new System.Numerics.Vector4[vertexCount];

                for (int i = 0; i < vertexCount; i++)
                {
                    var vert = mesh.VertexList[i];
                    ExtractNormalizedTop4Influences(vert.BoneIndices, vert.Weights, boneCount, out var j4, out var w4);

                    int jOff = i * 4;
                    jointsData[jOff + 0] = j4.J0;
                    jointsData[jOff + 1] = j4.J1;
                    jointsData[jOff + 2] = j4.J2;
                    jointsData[jOff + 3] = j4.J3;
                    weightsData[i] = w4;
                }

                sharedAttributes["JOINTS_0"] = _buffer.AddUShort4Accessor(jointsData, target: TargetArrayBuffer);
                sharedAttributes["WEIGHTS_0"] = _buffer.AddVec4Accessor(weightsData, target: TargetArrayBuffer);
            }

            // 4. Morph targets (BlendShapes).
            List<Dictionary<string, int>>? morphTargets = null;
            List<string>? targetNames = null;
            List<float>? targetFullWeights = null;

            ImportedMorph? morph = preResolvedMorph;

            if (morph != null && morph.Channels != null && morph.Channels.Count > 0)
            {
                morphTargets = new List<Dictionary<string, int>>(morph.Channels.Count);
                targetNames = new List<string>(morph.Channels.Count);
                targetFullWeights = new List<float>(morph.Channels.Count);

                for (int c = 0; c < morph.Channels.Count; c++)
                {
                    var channel = morph.Channels[c];
                    if (channel == null || channel.KeyframeList == null || channel.KeyframeList.Count == 0)
                    {
                        continue;
                    }

                    // Select the primary full-weight keyframe (usually the last/highest-weight keyframe).
                    var keyframe = channel.KeyframeList[channel.KeyframeList.Count - 1];
                    for (int k = 0; k < channel.KeyframeList.Count; k++)
                    {
                        if (channel.KeyframeList[k].Weight >= keyframe.Weight)
                        {
                            keyframe = channel.KeyframeList[k];
                        }
                    }

                    var posDeltas = new System.Numerics.Vector3[vertexCount];
                    var normDeltas = (keyframe.hasNormals && mesh.hasNormal) ? new System.Numerics.Vector3[vertexCount] : null;
                    var tanDeltas = (keyframe.hasTangents && mesh.hasTangent) ? new System.Numerics.Vector3[vertexCount] : null;

                    if (keyframe.VertexList != null)
                    {
                        for (int mvIdx = 0; mvIdx < keyframe.VertexList.Count; mvIdx++)
                        {
                            var mv = keyframe.VertexList[mvIdx];
                            if (mv == null || mv.Index >= (uint)vertexCount)
                            {
                                continue;
                            }

                            int vIdx = (int)mv.Index;
                            var basePos = mesh.VertexList[vIdx].Vertex;
                            // ImportedMorphVertex.Vertex.Vertex is absolute (base + delta), already in RH space.
                            var dp = new System.Numerics.Vector3(
                                SanitizeFloat((mv.Vertex.Vertex.X - basePos.X) * _scaleFactor),
                                SanitizeFloat((mv.Vertex.Vertex.Y - basePos.Y) * _scaleFactor),
                                SanitizeFloat((mv.Vertex.Vertex.Z - basePos.Z) * _scaleFactor));
                            if (applyMeshToArmature)
                            {
                                dp = System.Numerics.Vector3.TransformNormal(dp, meshToArmature);
                            }
                            posDeltas[vIdx] = new System.Numerics.Vector3(
                                SanitizeFloat(dp.X),
                                SanitizeFloat(dp.Y),
                                SanitizeFloat(dp.Z));

                            if (normDeltas != null)
                            {
                                // ImportedMorphVertex.Vertex.Normal is already a delta in RH space.
                                var dn = new System.Numerics.Vector3(
                                    SanitizeFloat(mv.Vertex.Normal.X),
                                    SanitizeFloat(mv.Vertex.Normal.Y),
                                    SanitizeFloat(mv.Vertex.Normal.Z));
                                if (applyMeshToArmature)
                                {
                                    dn = System.Numerics.Vector3.TransformNormal(dn, meshNormalMatrix);
                                }
                                normDeltas[vIdx] = new System.Numerics.Vector3(
                                    SanitizeFloat(dn.X),
                                    SanitizeFloat(dn.Y),
                                    SanitizeFloat(dn.Z));
                            }

                            if (tanDeltas != null)
                            {
                                // glTF morph target TANGENT is VEC3 (no W component).
                                var dt = new System.Numerics.Vector3(
                                    SanitizeFloat(mv.Vertex.Tangent.X),
                                    SanitizeFloat(mv.Vertex.Tangent.Y),
                                    SanitizeFloat(mv.Vertex.Tangent.Z));
                                if (applyMeshToArmature)
                                {
                                    dt = System.Numerics.Vector3.TransformNormal(dt, meshToArmature);
                                }
                                tanDeltas[vIdx] = new System.Numerics.Vector3(
                                    SanitizeFloat(dt.X),
                                    SanitizeFloat(dt.Y),
                                    SanitizeFloat(dt.Z));
                            }
                        }
                    }

                    var targetDict = new Dictionary<string, int>(StringComparer.Ordinal)
                    {
                        ["POSITION"] = _buffer.AddVec3Accessor(posDeltas, includeMinMax: true, target: TargetArrayBuffer)
                    };
                    if (normDeltas != null)
                    {
                        targetDict["NORMAL"] = _buffer.AddVec3Accessor(normDeltas, includeMinMax: false, target: TargetArrayBuffer);
                    }
                    if (tanDeltas != null)
                    {
                        targetDict["TANGENT"] = _buffer.AddVec3Accessor(tanDeltas, includeMinMax: false, target: TargetArrayBuffer);
                    }

                    morphTargets.Add(targetDict);
                    targetNames.Add(string.IsNullOrEmpty(channel.Name) ? $"Target_{morphTargets.Count - 1}" : channel.Name);
                    float fullWeight = SanitizeFloat(keyframe.Weight, 100f);
                    targetFullWeights.Add(fullWeight > 1e-4f ? fullWeight : 100f);
                }

                if (morphTargets.Count == 0)
                {
                    morphTargets = null;
                    targetNames = null;
                    targetFullWeights = null;
                }
            }

            // 5. Build glTF primitives & mesh object.
            var gltfMesh = new GltfMesh
            {
                Name = meshName
            };

            if (morphTargets != null && targetNames != null)
            {
                gltfMesh.Weights = new float[morphTargets.Count]; // default rest pose = 0.0
                gltfMesh.TargetNames = targetNames;
            }

            var primitiveIndicesAccessors = new List<int>(primitivePayloads.Count);
            for (int p = 0; p < primitivePayloads.Count; p++)
            {
                var (indices, maxIdx, matIdx) = primitivePayloads[p];
                int indicesAccessor = _buffer.AddIndicesAccessor(indices, maxIdx);
                primitiveIndicesAccessors.Add(indicesAccessor);
                var primAttributes = new Dictionary<string, int>(sharedAttributes, StringComparer.Ordinal);
                if (opaqueVertexColorAccessor.HasValue || blendVertexColorAccessor.HasValue)
                {
                    bool isBlend = matIdx.HasValue
                        && matIdx.Value >= 0
                        && matIdx.Value < _materials.Count
                        && string.Equals(_materials[matIdx.Value].AlphaMode, "BLEND", StringComparison.Ordinal);
                    int? colorAccessor = isBlend
                        ? (blendVertexColorAccessor ?? opaqueVertexColorAccessor)
                        : (opaqueVertexColorAccessor ?? blendVertexColorAccessor);
                    if (colorAccessor.HasValue)
                    {
                        primAttributes["COLOR_0"] = colorAccessor.Value;
                    }
                }

                gltfMesh.Primitives.Add(new GltfPrimitive
                {
                    Attributes = primAttributes,
                    Indices = indicesAccessor,
                    Material = matIdx,
                    Targets = morphTargets
                });
            }

            int meshIdx = _meshes.Count;
            _meshes.Add(gltfMesh);
            _nodes[meshNodeIdx].Mesh = meshIdx;
            _totalMeshInstances++;
            _uniqueGeometryBuffers++;

            if (canUseInstancing && geometryCacheKey != null && meshInstanceCacheKey != null)
            {
                _sharedGeometryByKey[geometryCacheKey] = new SharedGeometryEntry
                {
                    SharedAttributes = sharedAttributes,
                    OpaqueVertexColorAccessor = opaqueVertexColorAccessor,
                    BlendVertexColorAccessor = blendVertexColorAccessor,
                    PrimitiveIndicesAccessors = primitiveIndicesAccessors
                };
                _sharedMeshIndexByInstanceKey[meshInstanceCacheKey] = meshIdx;
            }

            if (morphTargets != null && targetNames != null && targetFullWeights != null)
            {
                var info = new MorphMeshTargetInfo(meshNodeIdx, targetNames, targetFullWeights);
                _morphTargetsByNode[meshNodeIdx] = info;
                if (!string.IsNullOrEmpty(normMeshPath) && !_morphTargetsByPath.ContainsKey(normMeshPath))
                {
                    _morphTargetsByPath[normMeshPath] = info;
                }

                for (int t = 0; t < targetNames.Count; t++)
                {
                    var chanName = targetNames[t];
                    if (_uniqueMorphChannelLookup.TryGetValue(chanName, out var existing))
                    {
                        if (existing.HasValue && (existing.Value.MeshNodeIndex != meshNodeIdx || existing.Value.TargetIndex != t))
                        {
                            _uniqueMorphChannelLookup[chanName] = null;
                        }
                    }
                    else
                    {
                        _uniqueMorphChannelLookup[chanName] = (meshNodeIdx, t, targetFullWeights[t]);
                    }
                }
            }

            // 6. Create glTF skin & inverseBindMatrices.
            if (isSkinned && skinJointNodes != null && ibmMatrices != null)
            {
                int ibmAccessor = _buffer.AddMat4Accessor(ibmMatrices);
                int skinIdx = _skins.Count;
                _skins.Add(new GltfSkin
                {
                    Name = $"{meshName}_Skin",
                    InverseBindMatrices = ibmAccessor,
                    Skeleton = skinSkeletonNode,
                    Joints = skinJointNodes
                });
                _nodes[meshNodeIdx].Skin = skinIdx;
            }
        }

        private System.Numerics.Matrix4x4 ConvertUnityBindPoseToRowMatrix(Matrix4x4 matrix)
        {
            // See FbxSharpieExporter.UnityBindPoseToNumerics:
            // AssetStudio.Matrix4x4 stores Unity's translation in row 3 (M30, M31, M32),
            // which maps directly to System.Numerics row-vector M41, M42, M43.
            // Because glTF MAT4 is column-major and P_row = GlobalMesh_row * inv(GlobalJoint_row),
            // (P_row)^T = inv(GlobalJoint_col) * GlobalMesh_col, whose column-major floats are
            // identically [M11,M12,M13,M14, M21,M22,M23,M24, M31,M32,M33,M34, M41,M42,M43,M44]!
            // Under uniform scaleFactor s, IBM' = S * IBM * S^-1 scales only M41, M42, M43 by s.
            return new System.Numerics.Matrix4x4(
                SanitizeFloat(matrix[0, 0]), SanitizeFloat(matrix[0, 1]), SanitizeFloat(matrix[0, 2]), 0f,
                SanitizeFloat(matrix[1, 0]), SanitizeFloat(matrix[1, 1]), SanitizeFloat(matrix[1, 2]), 0f,
                SanitizeFloat(matrix[2, 0]), SanitizeFloat(matrix[2, 1]), SanitizeFloat(matrix[2, 2]), 0f,
                SanitizeFloat(matrix[3, 0] * _scaleFactor), SanitizeFloat(matrix[3, 1] * _scaleFactor), SanitizeFloat(matrix[3, 2] * _scaleFactor), 1f);
        }

        private static bool IsFiniteInvertibleAffineMatrix(System.Numerics.Matrix4x4 m)
        {
            float det = m.GetDeterminant();
            return float.IsFinite(det) && MathF.Abs(det) > 1e-12f;
        }

        private static bool IsMatrixNearlyIdentity(System.Numerics.Matrix4x4 m, float eps = 1e-5f)
        {
            return MathF.Abs(m.M11 - 1f) < eps && MathF.Abs(m.M12) < eps && MathF.Abs(m.M13) < eps && MathF.Abs(m.M14) < eps
                && MathF.Abs(m.M21) < eps && MathF.Abs(m.M22 - 1f) < eps && MathF.Abs(m.M23) < eps && MathF.Abs(m.M24) < eps
                && MathF.Abs(m.M31) < eps && MathF.Abs(m.M32) < eps && MathF.Abs(m.M33 - 1f) < eps && MathF.Abs(m.M34) < eps
                && MathF.Abs(m.M41) < eps && MathF.Abs(m.M42) < eps && MathF.Abs(m.M43) < eps && MathF.Abs(m.M44 - 1f) < eps;
        }

        private static double MatrixDistanceFromIdentity(System.Numerics.Matrix4x4 m)
        {
            double d = 0.0;
            d += Math.Abs(m.M11 - 1.0) + Math.Abs(m.M12) + Math.Abs(m.M13) + Math.Abs(m.M14);
            d += Math.Abs(m.M21) + Math.Abs(m.M22 - 1.0) + Math.Abs(m.M23) + Math.Abs(m.M24);
            d += Math.Abs(m.M31) + Math.Abs(m.M32) + Math.Abs(m.M33 - 1.0) + Math.Abs(m.M34);
            d += Math.Abs(m.M41) + Math.Abs(m.M42) + Math.Abs(m.M43) + Math.Abs(m.M44 - 1.0);
            return double.IsFinite(d) ? d : double.PositiveInfinity;
        }

        private static System.Numerics.Matrix4x4 BuildNormalMatrix(System.Numerics.Matrix4x4 affineMatrix, out bool flipWinding)
        {
            var upper = new System.Numerics.Matrix4x4(
                affineMatrix.M11, affineMatrix.M12, affineMatrix.M13, 0f,
                affineMatrix.M21, affineMatrix.M22, affineMatrix.M23, 0f,
                affineMatrix.M31, affineMatrix.M32, affineMatrix.M33, 0f,
                0f, 0f, 0f, 1f);

            float det = upper.GetDeterminant();
            flipWinding = det < -1e-9f;
            if (MathF.Abs(det) > 1e-12f && System.Numerics.Matrix4x4.Invert(upper, out var invUpper))
            {
                return System.Numerics.Matrix4x4.Transpose(invUpper);
            }

            return upper;
        }

        private static void ExtractNormalizedTop4Influences(
            int[]? boneIndices,
            float[]? weights,
            int boneCount,
            out (ushort J0, ushort J1, ushort J2, ushort J3) joints,
            out System.Numerics.Vector4 normalizedWeights)
        {
            Span<int> topJ = stackalloc int[4];
            Span<float> topW = stackalloc float[4];
            int count = 0;

            if (boneIndices != null && weights != null)
            {
                int len = Math.Min(boneIndices.Length, weights.Length);
                for (int k = 0; k < len; k++)
                {
                    int bi = boneIndices[k];
                    float w = weights[k];
                    if (bi < 0 || bi >= boneCount || bi >= ushort.MaxValue || !float.IsFinite(w) || w <= 0f)
                    {
                        continue;
                    }

                    // Merge duplicate bone indices on the same vertex
                    int existingSlot = -1;
                    for (int s = 0; s < count; s++)
                    {
                        if (topJ[s] == bi)
                        {
                            existingSlot = s;
                            break;
                        }
                    }

                    if (existingSlot >= 0)
                    {
                        topW[existingSlot] += w;
                        continue;
                    }

                    if (count < 4)
                    {
                        topJ[count] = bi;
                        topW[count] = w;
                        count++;
                    }
                    else
                    {
                        int minIdx = 0;
                        for (int s = 1; s < 4; s++)
                        {
                            if (topW[s] < topW[minIdx])
                            {
                                minIdx = s;
                            }
                        }
                        if (w > topW[minIdx])
                        {
                            topJ[minIdx] = bi;
                            topW[minIdx] = w;
                        }
                    }
                }
            }

            // Sort descending by weight for deterministic output
            for (int a = 0; a < count; a++)
            {
                for (int b = a + 1; b < count; b++)
                {
                    if (topW[b] > topW[a])
                    {
                        (topW[a], topW[b]) = (topW[b], topW[a]);
                        (topJ[a], topJ[b]) = (topJ[b], topJ[a]);
                    }
                }
            }

            double sum = 0.0;
            for (int i = 0; i < count; i++)
            {
                sum += topW[i];
            }

            if (count == 0 || sum <= 1e-8)
            {
                joints = (0, 0, 0, 0);
                normalizedWeights = new System.Numerics.Vector4(1f, 0f, 0f, 0f);
                return;
            }

            float w0 = (float)(topW[0] / sum);
            float w1 = count > 1 ? (float)(topW[1] / sum) : 0f;
            float w2 = count > 2 ? (float)(topW[2] / sum) : 0f;
            float w3 = count > 3 ? (float)(topW[3] / sum) : 0f;

            // Adjust largest weight slightly so floating-point sum is as close to 1.0 as representable.
            float total = w0 + w1 + w2 + w3;
            w0 = Math.Max(0f, w0 + (1f - total));

            joints = (
                w0 > 0f ? (ushort)topJ[0] : (ushort)0,
                w1 > 0f ? (ushort)topJ[1] : (ushort)0,
                w2 > 0f ? (ushort)topJ[2] : (ushort)0,
                w3 > 0f ? (ushort)topJ[3] : (ushort)0);
            normalizedWeights = new System.Numerics.Vector4(w0, w1, w2, w3);
        }

        private static bool TryResolveFaceIndices(ImportedFace? face, int baseVertex, int vertexCount, out int i0, out int i1, out int i2)
        {
            i0 = i1 = i2 = 0;
            if (face?.VertexIndices == null || face.VertexIndices.Length < 3)
            {
                return false;
            }

            i0 = face.VertexIndices[0] + baseVertex;
            i1 = face.VertexIndices[1] + baseVertex;
            i2 = face.VertexIndices[2] + baseVertex;
            if ((uint)i0 < (uint)vertexCount && (uint)i1 < (uint)vertexCount && (uint)i2 < (uint)vertexCount)
            {
                return true;
            }

            i0 = face.VertexIndices[0];
            i1 = face.VertexIndices[1];
            i2 = face.VertexIndices[2];
            return (uint)i0 < (uint)vertexCount && (uint)i1 < (uint)vertexCount && (uint)i2 < (uint)vertexCount;
        }

    }
}
