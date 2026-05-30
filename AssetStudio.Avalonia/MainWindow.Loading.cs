using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AssetStudio;

namespace AssetStudio.Avalonia
{
    public partial class MainWindow
    {
        private struct BuildAssetStructuresResult
        {
            public string? ProductName;
            public List<AssetItem> ExportableAssets;
            public List<AssetItem> NewExportableAssets;
            public List<GameObjectNode> SceneTreeNodes;
            public Dictionary<AssetStudio.Object, AssetItem> ObjectToAssetItemCache;
            public Dictionary<Mesh, List<Material?>> MeshToMaterialsCache;
            public Dictionary<Mesh, List<string>> MeshAssociatedRenderersCache;
            public Dictionary<Mesh, HashSet<string>> MeshSourceTypesCache;
            public Dictionary<Material, Texture2D?> MaterialMainTextureCache;
            public Dictionary<Material, Material?> MaterialPreviewMaterialCache;
            public Dictionary<Material, Dictionary<string, Texture2D?>> MaterialTextureSlotsCache;
            public Dictionary<AnimationClip, Avatar?> AnimationClipAvatarCache;
            public Dictionary<Avatar, Mesh?> AvatarMeshCache;
            public Dictionary<Mesh, Avatar?> MeshAvatarCache;
            public Dictionary<AnimationClip, HashSet<uint>> AnimationClipTransformBindingsCache;
            public List<AssetClassItem> AssetClassItems;
        }

        private static void LinkAssetItemsToSceneNodesBackground(
            List<SerializedFile> assetsFileList,
            Dictionary<GameObject, GameObjectNode> treeNodeDictionary,
            Dictionary<Object, AssetItem> objectAssetItemDic)
        {
            foreach (var assetsFile in assetsFileList)
            {
                foreach (var asset in assetsFile.Objects)
                {
                    if (asset is not GameObject gameObject || !treeNodeDictionary.TryGetValue(gameObject, out var node))
                    {
                        continue;
                    }

                    if (objectAssetItemDic.TryGetValue(gameObject, out var gameObjectItem))
                    {
                        gameObjectItem.TreeNode = node;
                    }

                    foreach (var pptr in gameObject.m_Components)
                    {
                        if (!pptr.TryGet(out var component))
                        {
                            continue;
                        }

                        if (objectAssetItemDic.TryGetValue(component, out var componentItem))
                        {
                            componentItem.TreeNode = node;
                        }

                        if (component is MeshFilter meshFilter
                            && meshFilter.m_Mesh.TryGet(out var mesh)
                            && objectAssetItemDic.TryGetValue(mesh, out var meshItem))
                        {
                            meshItem.TreeNode = node;
                        }
                        else if (component is SkinnedMeshRenderer skinnedMeshRenderer
                            && skinnedMeshRenderer.m_Mesh.TryGet(out var skinnedMesh)
                            && objectAssetItemDic.TryGetValue(skinnedMesh, out var skinnedMeshItem))
                        {
                            skinnedMeshItem.TreeNode = node;
                        }
                    }
                }
            }
        }

        private static void LinkFbxSubAssetsToSceneNodesBackground(
            List<AssetItem> localExportableAssets,
            List<GameObjectNode> localSceneTreeNodes)
        {
            var fbxNodes = new Dictionary<string, GameObjectNode>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in localExportableAssets)
            {
                if (item.TreeNode?.GameObject == null)
                {
                    continue;
                }

                var fbxContainer = GetFbxContainerPath(item.Container);
                if (fbxContainer == null)
                {
                    continue;
                }

                fbxNodes.TryAdd(fbxContainer, GetFbxRootNode(item.TreeNode, fbxContainer));
            }

            foreach (var item in localExportableAssets)
            {
                var fbxContainer = GetFbxContainerPath(item.Container);
                if (fbxContainer == null || fbxNodes.ContainsKey(fbxContainer))
                {
                    continue;
                }

                var fbxName = Path.GetFileNameWithoutExtension(fbxContainer);
                var node = FindSceneNodeByNameBackground(localSceneTreeNodes, fbxName);
                if (node?.GameObject != null)
                {
                    fbxNodes[fbxContainer] = node;
                }
            }

            foreach (var item in localExportableAssets)
            {
                var fbxContainer = GetFbxContainerPath(item.Container);
                if (fbxContainer == null || !fbxNodes.TryGetValue(fbxContainer, out var node))
                {
                    continue;
                }

                item.TreeNode = node;
                if (item.Asset is Mesh or Animator)
                {
                    var fbxName = Path.GetFileNameWithoutExtension(fbxContainer);
                    if (!string.IsNullOrEmpty(fbxName))
                    {
                        item.Name = fbxName;
                    }
                }
            }
        }

        private static GameObjectNode? FindSceneNodeByNameBackground(List<GameObjectNode> localSceneTreeNodes, string name)
        {
            foreach (var root in localSceneTreeNodes)
            {
                var match = FindNodeByName(root, name);
                if (match != null)
                {
                    return match;
                }
            }

            return null;
        }

        private static void BuildAssetReferenceIndexesBackground(
            List<SerializedFile> assetsFileList,
            List<AssetItem> localExportableAssets,
            out Dictionary<AssetStudio.Object, AssetItem> objectToAssetItemCacheOut,
            out Dictionary<Mesh, List<Material?>> meshToMaterialsCacheOut,
            out Dictionary<Mesh, List<string>> meshAssociatedRenderersCacheOut,
            out Dictionary<Mesh, HashSet<string>> meshSourceTypesCacheOut,
            out Dictionary<Material, Texture2D?> materialMainTextureCacheOut,
            out Dictionary<Material, Material?> materialPreviewMaterialCacheOut,
            out Dictionary<Material, Dictionary<string, Texture2D?>> materialTextureSlotsCacheOut)
        {
            var localMeshToMaterialsCache = new Dictionary<Mesh, List<Material?>>();
            var localMeshAssociatedRenderersCache = new Dictionary<Mesh, List<string>>();
            var localMeshSourceTypesCache = new Dictionary<Mesh, HashSet<string>>();
            var localMaterialMainTextureCache = new Dictionary<Material, Texture2D?>();
            var localMaterialPreviewMaterialCache = new Dictionary<Material, Material?>();
            var localMaterialTextureSlotsCache = new Dictionary<Material, Dictionary<string, Texture2D?>>();

            var localObjectToAssetItemCache = new Dictionary<AssetStudio.Object, AssetItem>(localExportableAssets.Count);
            foreach (var item in localExportableAssets)
            {
                localObjectToAssetItemCache[item.Asset] = item;
            }

            void AddMeshMaterials(Mesh mesh, List<Material?> materials)
            {
                if (!localMeshToMaterialsCache.TryGetValue(mesh, out var existingList)
                    || ScoreMaterialsStatic(materials) > ScoreMaterialsStatic(existingList))
                {
                    localMeshToMaterialsCache[mesh] = materials;
                }
            }

            void AddMeshAssociation(Mesh mesh, string sourceType, string? description)
            {
                if (!localMeshSourceTypesCache.TryGetValue(mesh, out var sourceTypes))
                {
                    sourceTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    localMeshSourceTypesCache[mesh] = sourceTypes;
                }
                sourceTypes.Add(sourceType);

                if (string.IsNullOrEmpty(description))
                {
                    return;
                }

                if (!localMeshAssociatedRenderersCache.TryGetValue(mesh, out var renderers))
                {
                    renderers = new List<string>();
                    localMeshAssociatedRenderersCache[mesh] = renderers;
                }

                renderers.Add(description);
            }

            GameObject? ResolveGameObject(SerializedFile sourceFile, PPtr<GameObject> pptr)
            {
                if (pptr.TryGet(out var go))
                {
                    return go;
                }
                return pptr.m_FileID == 0 ? ResolveObject(sourceFile, pptr.m_PathID) as GameObject : null;
            }

            Mesh? ResolveMesh(SerializedFile sourceFile, PPtr<Mesh> pptr)
            {
                if (pptr.TryGet(out var mesh))
                {
                    return mesh;
                }
                return pptr.m_FileID == 0 ? ResolveObject(sourceFile, pptr.m_PathID) as Mesh : null;
            }

            Material? ResolveRendererMaterial(PPtr<Material> pptr)
            {
                if (pptr.TryGet(out var material))
                {
                    return material;
                }
                return null;
            }

            AssetStudio.Object? ResolveObject(SerializedFile sourceFile, long pathID)
            {
                if (sourceFile.ObjectsDic.TryGetValue(pathID, out var obj))
                {
                    return obj;
                }
                return null;
            }

            foreach (var file in assetsFileList)
            {
                foreach (var obj in file.Objects)
                {
                    if (obj is Material material)
                    {
                        IndexMaterialTexturesBackground(material, localMaterialPreviewMaterialCache, localMaterialTextureSlotsCache, localMaterialMainTextureCache);
                    }
                    else if (obj is SkinnedMeshRenderer smr)
                    {
                        var smrMesh = ResolveMesh(file, smr.m_Mesh);

                        if (smrMesh != null)
                        {
                            var go = ResolveGameObject(file, smr.m_GameObject);
                            AddMeshAssociation(
                                smrMesh,
                                "SkinnedMeshRenderer",
                                go != null ? $"SkinnedMeshRenderer on GameObject \"{go.m_Name}\" (PathID: {smr.m_PathID})" : null);

                            if (smr.m_Materials != null)
                            {
                                var list = new List<Material?>();
                                foreach (var matPtr in smr.m_Materials)
                                {
                                    list.Add(ResolveRendererMaterial(matPtr));
                                }
                                AddMeshMaterials(smrMesh, list);
                            }
                        }
                    }
                    else if (obj is MeshRenderer mr)
                    {
                        var go = ResolveGameObject(file, mr.m_GameObject);

                        if (go?.m_Components != null)
                        {
                            foreach (var compPtr in go.m_Components)
                            {
                                Component? comp = null;
                                if (compPtr.TryGet(out var cp))
                                {
                                    comp = cp;
                                }
                                else if (compPtr.m_FileID == 0)
                                {
                                    comp = ResolveObject(file, compPtr.m_PathID) as Component;
                                }

                                if (comp is MeshFilter mf)
                                {
                                    var mfMesh = ResolveMesh(file, mf.m_Mesh);

                                    if (mfMesh != null)
                                    {
                                        AddMeshAssociation(
                                            mfMesh,
                                            "MeshFilter",
                                            $"MeshFilter on GameObject \"{go.m_Name}\" (PathID: {mf.m_PathID})");

                                        if (mr.m_Materials != null)
                                        {
                                            var list = new List<Material?>();
                                            foreach (var matPtr in mr.m_Materials)
                                            {
                                                list.Add(ResolveRendererMaterial(matPtr));
                                            }
                                            AddMeshMaterials(mfMesh, list);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            objectToAssetItemCacheOut = localObjectToAssetItemCache;
            meshToMaterialsCacheOut = localMeshToMaterialsCache;
            meshAssociatedRenderersCacheOut = localMeshAssociatedRenderersCache;
            meshSourceTypesCacheOut = localMeshSourceTypesCache;
            materialMainTextureCacheOut = localMaterialMainTextureCache;
            materialPreviewMaterialCacheOut = localMaterialPreviewMaterialCache;
            materialTextureSlotsCacheOut = localMaterialTextureSlotsCache;
        }

        private static void BuildAnimationPreviewIndexesBackground(
            List<SerializedFile> assetsFileList,
            out Dictionary<AnimationClip, Avatar?> animationClipAvatarCacheOut,
            out Dictionary<Avatar, Mesh?> avatarMeshCacheOut,
            out Dictionary<Mesh, Avatar?> meshAvatarCacheOut,
            out Dictionary<AnimationClip, HashSet<uint>> animationClipTransformBindingsCacheOut)
        {
            var clips = assetsFileList.SelectMany(f => f.Objects).OfType<AnimationClip>().ToArray();
            var avatars = assetsFileList.SelectMany(f => f.Objects).OfType<Avatar>().ToArray();
            var meshes = assetsFileList.SelectMany(f => f.Objects).OfType<Mesh>()
                .Where(m => m.m_BoneNameHashes != null && m.m_BoneNameHashes.Length > 0
                    && m.m_BindPose != null && m.m_BindPose.Length > 0)
                .ToArray();

            var animationClipTransformBindingsCache = new Dictionary<AnimationClip, HashSet<uint>>(clips.Length);
            foreach (var clip in clips)
            {
                animationClipTransformBindingsCache[clip] = GetTransformBindingPathsBackground(clip);
            }

            var avatarMeshCache = new Dictionary<Avatar, Mesh?>(avatars.Length);
            foreach (var avatar in avatars)
            {
                avatarMeshCache[avatar] = FindBestMeshForAvatarBackground(avatar, meshes);
            }

            var meshAvatarCache = new Dictionary<Mesh, Avatar?>(meshes.Length);
            foreach (var mesh in meshes)
            {
                meshAvatarCache[mesh] = FindBestAvatarForMeshBackground(mesh, avatars);
            }

            var animationClipAvatarCache = new Dictionary<AnimationClip, Avatar?>(clips.Length);
            foreach (var clip in clips)
            {
                animationClipTransformBindingsCache.TryGetValue(clip, out var bindingPaths);
                animationClipAvatarCache[clip] = FindBestAvatarForAnimationClipBackground(clip, bindingPaths ?? new HashSet<uint>(), avatars);
            }

            animationClipAvatarCacheOut = animationClipAvatarCache;
            avatarMeshCacheOut = avatarMeshCache;
            meshAvatarCacheOut = meshAvatarCache;
            animationClipTransformBindingsCacheOut = animationClipTransformBindingsCache;
        }

        private static HashSet<uint> GetTransformBindingPathsBackground(AnimationClip clip)
        {
            var result = new HashSet<uint>();
            var bindings = clip.m_ClipBindingConstant;
            if (bindings == null && clip.m_MuscleClip?.m_Clip != null)
            {
                bindings = clip.m_MuscleClip.m_Clip.ConvertValueArrayToGenericBinding();
            }

            if (bindings?.genericBindings != null)
            {
                foreach (var binding in bindings.genericBindings)
                {
                    if (binding.typeID == ClassIDType.Transform)
                    {
                        result.Add(binding.path);
                    }
                }
            }

            return result;
        }

        private static Avatar? FindBestAvatarForAnimationClipBackground(AnimationClip clip, HashSet<uint> bindingPaths, Avatar[] avatars)
        {
            if (bindingPaths.Count == 0)
            {
                return null;
            }

            Avatar? bestAvatar = null;
            int bestScore = 0;
            var clipName = NormalizeAnimatorSearchKey(clip.m_Name);

            foreach (var avatar in avatars)
            {
                if (avatar.m_TOS == null || avatar.m_TOS.Length == 0)
                {
                    continue;
                }

                var avatarPathHashes = new HashSet<uint>(avatar.m_TOS.Select(x => x.Key));
                var overlap = bindingPaths.Count(avatarPathHashes.Contains);
                if (!IsStrongAnimationAvatarMatch(bindingPaths.Count, overlap))
                {
                    continue;
                }

                var score = overlap * 100;
                if (avatar.assetsFile == clip.assetsFile) score += 20;

                var avatarName = NormalizeAnimatorSearchKey(avatar.m_Name.Replace("Avatar", string.Empty));
                if (!string.IsNullOrEmpty(avatarName) && clipName.Contains(avatarName, StringComparison.OrdinalIgnoreCase))
                {
                    score += 15;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestAvatar = avatar;
                }
            }

            return bestAvatar;
        }

        private static Mesh? FindBestMeshForAvatarBackground(Avatar avatar, Mesh[] meshes)
        {
            var avatarBoneIds = avatar.m_Avatar?.m_AvatarSkeleton?.m_ID != null
                ? new HashSet<uint>(avatar.m_Avatar.m_AvatarSkeleton.m_ID)
                : new HashSet<uint>();
            if (avatarBoneIds.Count == 0)
            {
                return null;
            }

            Mesh? bestMesh = null;
            int bestScore = 0;
            var avatarName = avatar.m_Name.Replace("Avatar", string.Empty).Trim();

            foreach (var mesh in meshes)
            {
                var overlap = mesh.m_BoneNameHashes.Count(avatarBoneIds.Contains);
                if (!IsStrongMeshAvatarMatch(mesh.m_BoneNameHashes.Length, overlap))
                {
                    continue;
                }

                var score = overlap * 100 + Math.Min(mesh.m_BoneNameHashes.Length, 40);
                if (mesh.assetsFile == avatar.assetsFile) score += 20;
                if (!string.IsNullOrEmpty(avatarName) && mesh.m_Name.Contains(avatarName, StringComparison.OrdinalIgnoreCase)) score += 15;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestMesh = mesh;
                }
            }

            return bestMesh;
        }

        private static Avatar? FindBestAvatarForMeshBackground(Mesh mesh, Avatar[] avatars)
        {
            var meshBoneHashes = mesh.m_BoneNameHashes != null
                ? new HashSet<uint>(mesh.m_BoneNameHashes)
                : new HashSet<uint>();
            if (meshBoneHashes.Count == 0)
            {
                return null;
            }

            Avatar? bestAvatar = null;
            int bestScore = 0;
            var meshName = mesh.m_Name.ToLowerInvariant();

            foreach (var avatar in avatars)
            {
                if (avatar.m_Avatar?.m_AvatarSkeleton?.m_ID == null)
                {
                    continue;
                }

                var overlap = avatar.m_Avatar.m_AvatarSkeleton.m_ID.Count(meshBoneHashes.Contains);
                if (!IsStrongMeshAvatarMatch(meshBoneHashes.Count, overlap))
                {
                    continue;
                }

                var score = overlap * 100;
                if (avatar.assetsFile == mesh.assetsFile) score += 20;

                var avatarName = avatar.m_Name.Replace("Avatar", string.Empty).Trim().ToLowerInvariant();
                if (!string.IsNullOrEmpty(avatarName) && meshName.Contains(avatarName)) score += 15;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestAvatar = avatar;
                }
            }

            return bestAvatar;
        }

        private static bool IsStrongAnimationAvatarMatch(int bindingCount, int overlap)
        {
            if (bindingCount <= 0 || overlap <= 0)
            {
                return false;
            }

            var minimum = bindingCount < 6 ? bindingCount : Math.Max(6, bindingCount / 20);
            return overlap >= minimum;
        }

        private static bool IsStrongMeshAvatarMatch(int boneCount, int overlap)
        {
            if (boneCount <= 0 || overlap <= 0)
            {
                return false;
            }

            var minimum = boneCount < 6 ? boneCount : Math.Max(6, boneCount / 10);
            return overlap >= minimum;
        }

        private static void IndexMaterialTexturesBackground(
            Material material,
            Dictionary<Material, Material?> localMaterialPreviewMaterialCache,
            Dictionary<Material, Dictionary<string, Texture2D?>> localMaterialTextureSlotsCache,
            Dictionary<Material, Texture2D?> localMaterialMainTextureCache)
        {
            if (localMaterialTextureSlotsCache.ContainsKey(material) && localMaterialMainTextureCache.ContainsKey(material))
            {
                return;
            }

            var displayMaterial = ResolveMaterialForPreviewBackground(material, localMaterialPreviewMaterialCache) ?? material;
            if (!localMaterialTextureSlotsCache.TryGetValue(displayMaterial, out var slots))
            {
                slots = new Dictionary<string, Texture2D?>(StringComparer.OrdinalIgnoreCase);
                foreach (var texEnv in displayMaterial.m_SavedProperties?.m_TexEnvs ?? Array.Empty<KeyValuePair<string, UnityTexEnv>>())
                {
                    var textureRef = texEnv.Value?.m_Texture;
                    slots[texEnv.Key] = textureRef != null && !textureRef.IsNull
                        ? ResolveTexturePPtrBackground(displayMaterial, textureRef)
                        : null;
                }

                localMaterialTextureSlotsCache[displayMaterial] = slots;
                localMaterialMainTextureCache[displayMaterial] = SelectMainTextureForMaterialBackground(displayMaterial, slots);
            }

            if (!ReferenceEquals(displayMaterial, material))
            {
                localMaterialTextureSlotsCache[material] = slots;
                localMaterialMainTextureCache[material] = localMaterialMainTextureCache[displayMaterial];
            }
        }

        private static Material? ResolveMaterialForPreviewBackground(
            Material material,
            Dictionary<Material, Material?> localMaterialPreviewMaterialCache)
        {
            if (localMaterialPreviewMaterialCache.TryGetValue(material, out var cachedMaterial))
            {
                return cachedMaterial;
            }

            var resolvedMaterial = ResolveMaterialForPreviewUncachedBackground(material);
            localMaterialPreviewMaterialCache[material] = resolvedMaterial;
            return resolvedMaterial;
        }

        private static Material? ResolveMaterialForPreviewUncachedBackground(Material material)
        {
            var visited = new HashSet<Material>();
            while (material != null && visited.Add(material))
            {
                var hasTextureReference = (material.m_SavedProperties?.m_TexEnvs ?? Array.Empty<KeyValuePair<string, UnityTexEnv>>())
                    .Any(x => x.Value?.m_Texture != null && !x.Value.m_Texture.IsNull);
                if (hasTextureReference)
                {
                    return material;
                }

                if (material.m_Parent != null)
                {
                    if (material.m_Parent.TryGet(out var parent))
                    {
                        material = parent;
                        continue;
                    }
                }

                break;
            }

            return null;
        }

        private static Texture2D? ResolveTexturePPtrBackground(Material material, PPtr<Texture> textureRef)
        {
            if (textureRef.TryGet<Texture2D>(out var directTex))
            {
                return directTex;
            }

            if (textureRef.m_FileID == 0
                && material.assetsFile.ObjectsDic.TryGetValue(textureRef.m_PathID, out var localObj)
                && localObj is Texture2D localTex)
            {
                return localTex;
            }

            return null;
        }

        private static Texture2D? SelectMainTextureForMaterialBackground(Material displayMaterial, IReadOnlyDictionary<string, Texture2D?> textureSlots)
        {
            if (displayMaterial.m_SavedProperties?.m_TexEnvs == null) return null;

            var slots = new[] { "_MainTex", "_BaseMap", "_BaseColorMap", "_BaseColorTexture", "_Diffuse", "_AlbedoMap" };
            foreach (var slot in slots)
            {
                if (textureSlots.TryGetValue(slot, out var tex) && tex != null)
                {
                    return tex;
                }
            }

            foreach (var env in displayMaterial.m_SavedProperties.m_TexEnvs)
            {
                if (NonDiffuseSlots.Contains(env.Key)) continue;
                if (textureSlots.TryGetValue(env.Key, out var tex) && tex != null)
                {
                    return tex;
                }
            }

            return null;
        }

        private static void AddSerializedTypesBackground(SerializedFile assetsFile, IEnumerable<SerializedType>? types, string sourceKind,
            Dictionary<(string UnityVersion, int ClassID), int> objectCounts, HashSet<string> seen, List<AssetClassItem> localAssetClassItems)
        {
            if (types == null)
                return;

            foreach (var type in types)
            {
                var name = GetSerializedTypeName(type);
                var ns = type.m_NameSpace ?? string.Empty;
                var asm = type.m_AsmName ?? string.Empty;
                var key = string.Join("\u001f", assetsFile.unityVersion, type.classID.ToString(CultureInfo.InvariantCulture), name, ns, asm, sourceKind);
                if (!seen.Add(key))
                    continue;

                objectCounts.TryGetValue((assetsFile.unityVersion, type.classID), out var objectCount);

                var item = new AssetClassItem
                {
                    ClassID = type.classID,
                    Name = name,
                    Namespace = ns,
                    Assembly = asm,
                    UnityVersion = assetsFile.unityVersion,
                    SourceFile = assetsFile.fileName,
                    ObjectCount = objectCount,
                    SourceKind = type.m_IsStrippedType ? $"{sourceKind} stripped" : sourceKind,
                    SerializedType = type
                };
                localAssetClassItems.Add(item);
            }
        }

        private static int ScoreMaterialsStatic(List<Material?> mats)
        {
            if (mats == null || mats.Count == 0) return 0;
            int score = 0;
            foreach (var mat in mats)
            {
                if (mat == null)
                {
                    continue;
                }

                score += 1;
                if (!mat.m_Name.StartsWith("Material", StringComparison.OrdinalIgnoreCase)
                    && !mat.m_Name.Equals("Default", StringComparison.OrdinalIgnoreCase))
                {
                    score += 5;
                }
            }
            return score;
        }
    }
}
