using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using AssetStudio;
using AssetStudio.Avalonia;
using AssetStudio.Avalonia.Services;
using Xunit;

namespace AssetStudio.Tests
{
    public sealed class ConnectionsPerformanceAndActiveFilterTests
    {
        private static SerializedFile CreateSyntheticSerializedFile(AssetsManager assetsManager, string fileName, string? originalPath = null)
        {
            var file = (SerializedFile)RuntimeHelpers.GetUninitializedObject(typeof(SerializedFile));
            typeof(SerializedFile).GetField("objectCollectionLock", BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(file, new object());
            file.assetsManager = assetsManager;
            file.fileName = fileName;
            file.originalPath = originalPath ?? Path.Combine(Path.GetTempPath(), fileName);
            file.fullName = file.originalPath;
            file.unityVersion = "2022.3.40f1";
            file.version = new[] { 2022, 3, 40, 1 };
            file.header = new SerializedFileHeader { m_Version = SerializedFileFormatVersion.LargeFilesSupport };
            file.Objects = new List<AssetStudio.Object>();
            file.ObjectsDic = new Dictionary<long, AssetStudio.Object>();
            file.ObjectInfoDic = new Dictionary<long, ObjectInfo>();
            file.m_Objects = new List<ObjectInfo>();
            file.m_Externals = new List<FileIdentifier>();
            return file;
        }

        private static T RegisterSyntheticObject<T>(SerializedFile file, long pathId, ClassIDType classId, string? name = null)
            where T : AssetStudio.Object
        {
            var obj = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
            obj.assetsFile = file;
            obj.m_PathID = pathId;
            obj.type = classId;
            if (obj is NamedObject named && name != null)
            {
                named.m_Name = name;
            }
            if (obj is GameObject go)
            {
                go.m_Name = name ?? string.Empty;
                go.m_IsActive = true;
                go.m_Components = Array.Empty<PPtr<Component>>();
            }
            if (obj is Renderer renderer)
            {
                renderer.m_Enabled = true;
                renderer.m_Materials = Array.Empty<PPtr<Material>>();
            }

            file.AddObject(obj);
            var info = new ObjectInfo { m_PathID = pathId, classID = (int)classId };
            file.m_Objects.Add(info);
            file.ObjectInfoDic[pathId] = info;
            return obj;
        }

        private static (GameObject Go, Transform Tr) CreateNode(
            SerializedFile file,
            long goPathId,
            long trPathId,
            string name,
            Transform? parentTransform = null,
            bool isActive = true)
        {
            var go = RegisterSyntheticObject<GameObject>(file, goPathId, ClassIDType.GameObject, name);
            go.m_IsActive = isActive;

            var tr = RegisterSyntheticObject<Transform>(file, trPathId, ClassIDType.Transform);
            tr.m_GameObject = new PPtr<GameObject>(0, goPathId, file);
            tr.m_Father = parentTransform != null
                ? new PPtr<Transform>(0, parentTransform.m_PathID, file)
                : new PPtr<Transform>(0, 0, file);
            tr.m_LocalPosition = Vector3.Zero;
            tr.m_LocalRotation = new Quaternion(0, 0, 0, 1);
            tr.m_LocalScale = Vector3.One;
            tr.m_Children = Array.Empty<PPtr<Transform>>();

            go.m_Transform = tr;
            go.m_Components = new[] { new PPtr<Component>(0, trPathId, file) };
            return (go, tr);
        }

        private static string CreateTempDirectory(string prefix)
        {
            var path = Path.Combine(Path.GetTempPath(), $"assetstudio_{prefix}_{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return path;
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch
            {
                // Ignore temp cleanup errors.
            }
        }

        [Fact]
        public void Renderer_DisabledFlag_MarksMeshRendererAndSkinnedMeshRendererInactive_InAnimatorAndSceneObjectModelGroups()
        {
            using var am = new AssetsManager();
            var file = CreateSyntheticSerializedFile(am, "character_and_props.assets");

            // --- Case 1: Animator hierarchy with active + disabled SkinnedMeshRenderer ---
            var (heroRootGo, heroRootTr) = CreateNode(file, 100, 101, "HeroRoot");
            var animator = RegisterSyntheticObject<Animator>(file, 102, ClassIDType.Animator);
            animator.m_GameObject = new PPtr<GameObject>(0, heroRootGo.m_PathID, file);
            animator.m_Avatar = new PPtr<Avatar>(0, 0, file);
            animator.m_Controller = new PPtr<RuntimeAnimatorController>(0, 0, file);
            heroRootGo.m_Animator = animator;

            var (bodyGo, _) = CreateNode(file, 110, 111, "HeroBody", heroRootTr, isActive: true);
            var bodyMesh = RegisterSyntheticObject<Mesh>(file, 112, ClassIDType.Mesh, "HeroBodyMesh");
            var bodySmr = RegisterSyntheticObject<SkinnedMeshRenderer>(file, 113, ClassIDType.SkinnedMeshRenderer);
            bodySmr.m_Enabled = true;
            bodySmr.m_GameObject = new PPtr<GameObject>(0, bodyGo.m_PathID, file);
            bodySmr.m_Mesh = new PPtr<Mesh>(0, bodyMesh.m_PathID, file);

            var (proxyGo, _) = CreateNode(file, 120, 121, "HeroProxy_cbvh", heroRootTr, isActive: true);
            var proxyMesh = RegisterSyntheticObject<Mesh>(file, 122, ClassIDType.Mesh, "HeroProxyMesh_cbvh");
            var proxySmr = RegisterSyntheticObject<SkinnedMeshRenderer>(file, 123, ClassIDType.SkinnedMeshRenderer);
            proxySmr.m_Enabled = false;
            proxySmr.m_GameObject = new PPtr<GameObject>(0, proxyGo.m_PathID, file);
            proxySmr.m_Mesh = new PPtr<Mesh>(0, proxyMesh.m_PathID, file);

            // --- Case 2: SceneObject hierarchy with active + disabled MeshRenderer ---
            var (propRootGo, propRootTr) = CreateNode(file, 200, 201, "PropRoot");

            var (visualGo, _) = CreateNode(file, 210, 211, "PropVisual", propRootTr, isActive: true);
            var visualMesh = RegisterSyntheticObject<Mesh>(file, 212, ClassIDType.Mesh, "PropVisualMesh");
            var visualMf = RegisterSyntheticObject<MeshFilter>(file, 213, ClassIDType.MeshFilter);
            visualMf.m_GameObject = new PPtr<GameObject>(0, visualGo.m_PathID, file);
            visualMf.m_Mesh = new PPtr<Mesh>(0, visualMesh.m_PathID, file);
            var visualMr = RegisterSyntheticObject<MeshRenderer>(file, 214, ClassIDType.MeshRenderer);
            visualMr.m_Enabled = true;
            visualMr.m_GameObject = new PPtr<GameObject>(0, visualGo.m_PathID, file);
            visualGo.m_Components = new[]
            {
                new PPtr<Component>(0, 211, file),
                new PPtr<Component>(0, 213, file),
                new PPtr<Component>(0, 214, file)
            };

            var (colliderGo, _) = CreateNode(file, 220, 221, "PropCollider_cplh", propRootTr, isActive: true);
            var colliderMesh = RegisterSyntheticObject<Mesh>(file, 222, ClassIDType.Mesh, "PropColliderMesh_cplh");
            var colliderMf = RegisterSyntheticObject<MeshFilter>(file, 223, ClassIDType.MeshFilter);
            colliderMf.m_GameObject = new PPtr<GameObject>(0, colliderGo.m_PathID, file);
            colliderMf.m_Mesh = new PPtr<Mesh>(0, colliderMesh.m_PathID, file);
            var colliderMr = RegisterSyntheticObject<MeshRenderer>(file, 224, ClassIDType.MeshRenderer);
            colliderMr.m_Enabled = false;
            colliderMr.m_GameObject = new PPtr<GameObject>(0, colliderGo.m_PathID, file);
            colliderGo.m_Components = new[]
            {
                new PPtr<Component>(0, 221, file),
                new PPtr<Component>(0, 223, file),
                new PPtr<Component>(0, 224, file)
            };

            // Verify IsRendererActiveInHierarchy directly
            Assert.True(SemanticGraphService.IsRendererActiveInHierarchy(file, bodySmr, bodyGo, heroRootGo));
            Assert.False(SemanticGraphService.IsRendererActiveInHierarchy(file, proxySmr, proxyGo, heroRootGo));
            Assert.True(SemanticGraphService.IsRendererActiveInHierarchy(file, visualMr, visualGo, propRootGo));
            Assert.False(SemanticGraphService.IsRendererActiveInHierarchy(file, colliderMr, colliderGo, propRootGo));

            var buildResult = SemanticGraphService.BuildAssetReferenceIndexForFile(file);
            var relations = buildResult.SemanticRelations;

            // Animator ModelGroup checks
            var animatorGroup = relations.ModelGroups.Single(g => g.GroupKind == "Animator");
            Assert.Equal(100, animatorGroup.Confidence);

            var animatorParts = relations.ModelGroupMeshes.Where(m => m.GroupId == animatorGroup.GroupId).ToList();
            var bodyPart = animatorParts.Single(p => p.GameObjectName == "HeroBody");
            var proxyPart = animatorParts.Single(p => p.GameObjectName == "HeroProxy_cbvh");

            Assert.Equal(100, bodyPart.Confidence);
            Assert.DoesNotContain("(inactive)", bodyPart.ConfidenceReason, StringComparison.OrdinalIgnoreCase);

            Assert.True(proxyPart.Confidence <= 40, $"Expected disabled SkinnedMeshRenderer confidence <= 40, got {proxyPart.Confidence}");
            Assert.Contains("(inactive)", proxyPart.ConfidenceReason, StringComparison.OrdinalIgnoreCase);

            // SceneObject ModelGroup checks (both HeroRoot and PropRoot)
            var propGroup = relations.ModelGroups.Single(g => g.GroupKind == "SceneObject" && g.GroupName == "PropRoot");
            Assert.Equal(100, propGroup.Confidence);

            var propParts = relations.ModelGroupMeshes.Where(m => m.GroupId == propGroup.GroupId).ToList();
            var visualPart = propParts.Single(p => p.GameObjectName == "PropVisual");
            var colliderPart = propParts.Single(p => p.GameObjectName == "PropCollider_cplh");

            Assert.Equal(100, visualPart.Confidence);
            Assert.DoesNotContain("(inactive)", visualPart.ConfidenceReason, StringComparison.OrdinalIgnoreCase);

            Assert.True(colliderPart.Confidence <= 40, $"Expected disabled MeshRenderer confidence <= 40, got {colliderPart.Confidence}");
            Assert.Contains("(inactive)", colliderPart.ConfidenceReason, StringComparison.OrdinalIgnoreCase);

            var heroSceneGroup = relations.ModelGroups.Single(g => g.GroupKind == "SceneObject" && g.GroupName == "HeroRoot");
            var heroSceneProxyPart = relations.ModelGroupMeshes.Single(m => m.GroupId == heroSceneGroup.GroupId && m.GameObjectName == "HeroProxy_cbvh");
            Assert.True(heroSceneProxyPart.Confidence <= 40);
            Assert.Contains("(inactive)", heroSceneProxyPart.ConfidenceReason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void MeshRenderer_BinaryDeserialization_ReadsEnabledFlagAccurately()
        {
            static byte[] BuildMeshRendererPayload(bool enabled)
            {
                using var ms = new MemoryStream();
                using var bw = new BinaryWriter(ms);
                // Component: m_GameObject PPtr (fileID=0, pathID=10)
                bw.Write(0);
                bw.Write(10L);
                // Renderer (Unity 2022.3):
                bw.Write(enabled); // m_Enabled
                bw.Write((byte)1); // m_CastShadows
                bw.Write((byte)1); // m_ReceiveShadows
                bw.Write((byte)1); // m_DynamicOccludee
                bw.Write((byte)0); // m_StaticShadowCaster
                bw.Write((byte)1); // m_MotionVectors
                bw.Write((byte)1); // m_LightProbeUsage
                bw.Write((byte)1); // m_ReflectionProbeUsage
                bw.Write((byte)0); // m_RayTracingMode
                bw.Write((byte)0); // m_RayTraceProcedural
                // Align to 4 bytes (10 bytes written -> pad 2 bytes)
                bw.Write((byte)0);
                bw.Write((byte)0);
                bw.Write(1u); // m_RenderingLayerMask
                bw.Write(0);  // m_RendererPriority
                bw.Write((ushort)0xFFFF); // m_LightmapIndex
                bw.Write((ushort)0xFFFF); // m_LightmapIndexDynamic
                // m_LightmapTilingOffset (Vector4)
                bw.Write(1f); bw.Write(1f); bw.Write(0f); bw.Write(0f);
                // m_LightmapTilingOffsetDynamic (Vector4)
                bw.Write(1f); bw.Write(1f); bw.Write(0f); bw.Write(0f);
                // m_Materials (0 entries)
                bw.Write(0);
                // m_StaticBatchInfo (firstSubMesh, subMeshCount)
                bw.Write((ushort)0);
                bw.Write((ushort)0);
                // m_StaticBatchRoot PPtr
                bw.Write(0); bw.Write(0L);
                // m_ProbeAnchor PPtr
                bw.Write(0); bw.Write(0L);
                // m_LightProbeVolumeOverride PPtr
                bw.Write(0); bw.Write(0L);
                // m_SortingLayerID, m_SortingLayer, m_SortingOrder, align
                bw.Write(0u);
                bw.Write((short)0);
                bw.Write((short)0);
                // MeshRenderer: m_AdditionalVertexStreams PPtr
                bw.Write(0); bw.Write(0L);
                // MeshRenderer (2020+): m_EnlightenVertexStream PPtr
                bw.Write(0); bw.Write(0L);
                return ms.ToArray();
            }

            using var am = new AssetsManager();
            var file = CreateSyntheticSerializedFile(am, "renderer_enabled.assets");

            var disabledBytes = BuildMeshRendererPayload(enabled: false);
            using (var ms = new MemoryStream(disabledBytes))
            using (var fr = new FileReader(file.originalPath, ms))
            {
                fr.Endian = EndianType.LittleEndian;
                var info = new ObjectInfo { m_PathID = 1, byteStart = 0, byteSize = (uint)disabledBytes.Length, classID = (int)ClassIDType.MeshRenderer };
                var mr = new MeshRenderer(new ObjectReader(fr, file, info));
                Assert.False(mr.m_Enabled);
            }

            var enabledBytes = BuildMeshRendererPayload(enabled: true);
            using (var ms = new MemoryStream(enabledBytes))
            using (var fr = new FileReader(file.originalPath, ms))
            {
                fr.Endian = EndianType.LittleEndian;
                var info = new ObjectInfo { m_PathID = 2, byteStart = 0, byteSize = (uint)enabledBytes.Length, classID = (int)ClassIDType.MeshRenderer };
                var mr = new MeshRenderer(new ObjectReader(fr, file, info));
                Assert.True(mr.m_Enabled);
            }
        }

        [Fact]
        public void SemanticGraphService_SelectsEyeIrisOverHighlight_AndMainTextureOverMaskTexture()
        {
            using var am = new AssetsManager();
            var file = CreateSyntheticSerializedFile(am, "materials.assets");

            static UnityTexEnv MakeTexEnv(SerializedFile sf, long pathId)
            {
                var env = (UnityTexEnv)RuntimeHelpers.GetUninitializedObject(typeof(UnityTexEnv));
                env.m_Texture = new PPtr<Texture>(0, pathId, sf);
                env.m_Scale = new Vector2(1f, 1f);
                env.m_Offset = new Vector2(0f, 0f);
                return env;
            }

            // Eye material: _BaseEyeHighlightMap appears BEFORE _BaseEyeIrisMap
            var eyeHighlightTex = RegisterSyntheticObject<Texture2D>(file, 10, ClassIDType.Texture2D, "EyeHighlight");
            var eyePupilTex = RegisterSyntheticObject<Texture2D>(file, 11, ClassIDType.Texture2D, "EyePupil");
            var eyeIrisTex = RegisterSyntheticObject<Texture2D>(file, 12, ClassIDType.Texture2D, "EyeIris");
            var eyeMat = RegisterSyntheticObject<Material>(file, 13, ClassIDType.Material, "EyeMaterial");
            eyeMat.m_SavedProperties = new UnityPropertySheet
            {
                m_TexEnvs = new[]
                {
                    new KeyValuePair<string, UnityTexEnv>("_BaseEyeHighlightMap", MakeTexEnv(file, 10)),
                    new KeyValuePair<string, UnityTexEnv>("_BaseEyePupilMap", MakeTexEnv(file, 11)),
                    new KeyValuePair<string, UnityTexEnv>("_BaseEyeIrisMap", MakeTexEnv(file, 12))
                },
                m_Colors = Array.Empty<KeyValuePair<string, Color>>()
            };

            Assert.Equal("_BaseEyeIrisMap", SemanticGraphService.SelectMainTextureSlotNameForRelations(eyeMat));
            var eyeSlots = new Dictionary<string, Texture2D?>(StringComparer.OrdinalIgnoreCase)
            {
                ["_BaseEyeHighlightMap"] = eyeHighlightTex,
                ["_BaseEyePupilMap"] = eyePupilTex,
                ["_BaseEyeIrisMap"] = eyeIrisTex
            };
            Assert.Same(eyeIrisTex, SemanticGraphService.SelectMainTextureForMaterialBackground(eyeMat, eyeSlots));

            // Character material: _MaskTexture and _MatcapMap appear BEFORE _MainTexture
            var maskTex = RegisterSyntheticObject<Texture2D>(file, 20, ClassIDType.Texture2D, "MaskTex");
            var matcapTex = RegisterSyntheticObject<Texture2D>(file, 21, ClassIDType.Texture2D, "MatcapTex");
            var mainTex = RegisterSyntheticObject<Texture2D>(file, 22, ClassIDType.Texture2D, "AlbedoMainTex");
            var bodyMat = RegisterSyntheticObject<Material>(file, 23, ClassIDType.Material, "BodyMaterial");
            bodyMat.m_SavedProperties = new UnityPropertySheet
            {
                m_TexEnvs = new[]
                {
                    new KeyValuePair<string, UnityTexEnv>("_MaskTexture", MakeTexEnv(file, 20)),
                    new KeyValuePair<string, UnityTexEnv>("_MatcapMap", MakeTexEnv(file, 21)),
                    new KeyValuePair<string, UnityTexEnv>("_MainTexture", MakeTexEnv(file, 22))
                },
                m_Colors = Array.Empty<KeyValuePair<string, Color>>()
            };

            Assert.Equal("_MainTexture", SemanticGraphService.SelectMainTextureSlotNameForRelations(bodyMat));
            var bodySlots = new Dictionary<string, Texture2D?>(StringComparer.OrdinalIgnoreCase)
            {
                ["_MaskTexture"] = maskTex,
                ["_MatcapMap"] = matcapTex,
                ["_MainTexture"] = mainTex
            };
            Assert.Same(mainTex, SemanticGraphService.SelectMainTextureForMaterialBackground(bodyMat, bodySlots));

            // Non-preferred fallback skips NonDiffuseSlots (_MaskTexture, _WeatheringMask, _UVScrollTexture1)
            var fallbackMat = RegisterSyntheticObject<Material>(file, 30, ClassIDType.Material, "FallbackMat");
            fallbackMat.m_SavedProperties = new UnityPropertySheet
            {
                m_TexEnvs = new[]
                {
                    new KeyValuePair<string, UnityTexEnv>("_MaskTexture", MakeTexEnv(file, 20)),
                    new KeyValuePair<string, UnityTexEnv>("_WeatheringMask", MakeTexEnv(file, 21)),
                    new KeyValuePair<string, UnityTexEnv>("_UVScrollTexture1", MakeTexEnv(file, 10)),
                    new KeyValuePair<string, UnityTexEnv>("_CustomColorAtlas", MakeTexEnv(file, 22))
                },
                m_Colors = Array.Empty<KeyValuePair<string, Color>>()
            };

            Assert.Equal("_CustomColorAtlas", SemanticGraphService.SelectMainTextureSlotNameForRelations(fallbackMat));
        }

        [Fact]
        public void AddContainerMaterialRelations_PreservesNarrowContainers_AndSkipsLargeSceneCartesianExplosion()
        {
            using var am = new AssetsManager();
            var file = CreateSyntheticSerializedFile(am, "bundle_containers.assets");

            // Register 10 meshes (PathIDs 1..10) and 25 materials (PathIDs 101..125)
            for (long m = 1; m <= 10; m++)
            {
                RegisterSyntheticObject<Mesh>(file, m, ClassIDType.Mesh, $"Mesh_{m}");
            }
            for (long mat = 101; mat <= 125; mat++)
            {
                RegisterSyntheticObject<Material>(file, mat, ClassIDType.Material, $"Mat_{mat}");
            }

            // Narrow asset container: 2 meshes x 3 materials -> should emit 6 MeshMaterials rows
            var narrowRefs = new List<PPtr<AssetStudio.Object>>
            {
                new(0, 1, file),
                new(0, 2, file),
                new(0, 101, file),
                new(0, 102, file),
                new(0, 103, file)
            };

            // Large scene container: 8 meshes x 20 materials -> exceeds narrow cap -> must emit 0 Container rows
            var sceneRefs = new List<PPtr<AssetStudio.Object>>();
            for (long m = 3; m <= 10; m++)
            {
                sceneRefs.Add(new PPtr<AssetStudio.Object>(0, m, file));
            }
            for (long mat = 104; mat <= 123; mat++)
            {
                sceneRefs.Add(new PPtr<AssetStudio.Object>(0, mat, file));
            }

            var relations = new SemanticAssetRelations();
            var containerReferences = new List<(string Container, List<PPtr<AssetStudio.Object>> References)>
            {
                ("assets/props/narrow_prop.prefab", narrowRefs),
                ("assets/scenes/huge_level.unity", sceneRefs)
            };

            SemanticGraphService.AddContainerMaterialRelations(relations, file, containerReferences);

            Assert.Equal(2, relations.MeshRenderers.Count);
            Assert.Equal(6, relations.MeshMaterials.Count);
            Assert.All(relations.MeshMaterials, r => Assert.Equal("Container", r.RendererType));

            var mesh1Id = AssetHandle.BuildUniqueID(file, 1);
            var mesh1Rows = relations.MeshMaterials.Where(r => r.MeshAssetId == mesh1Id).OrderBy(r => r.SubMeshIndex).ToList();
            Assert.Equal(3, mesh1Rows.Count);
            Assert.Equal(new[] { 0, 1, 2 }, mesh1Rows.Select(r => r.SubMeshIndex).ToArray());
            Assert.Equal(new[] { 1, 2, 3 }, mesh1Rows.Select(r => r.MaterialScore).ToArray());

            // Verify none of the large scene container meshes (PathIDs 3..10) got synthetic Container material rows
            var mesh3Id = AssetHandle.BuildUniqueID(file, 3);
            Assert.DoesNotContain(relations.MeshMaterials, r => r.MeshAssetId == mesh3Id);
        }

        [Fact]
        public void GetLazyConnectionSourcePaths_ArenaFastPath_MatchesHandleEnumeration()
        {
            var tempRoot = CreateTempDirectory("arena_lazy_sources");
            try
            {
                var mediaOnlyPath = Path.Combine(tempRoot, "media_only.bundle");
                var prefabBundlePath = Path.Combine(tempRoot, "prefab_a.bundle");
                var meshBundlePath = Path.Combine(tempRoot, "mesh_b.bundle");
                var missingBundlePath = Path.Combine(tempRoot, "missing_c.bundle");
                File.WriteAllBytes(mediaOnlyPath, new byte[8]);
                File.WriteAllBytes(prefabBundlePath, new byte[8]);
                File.WriteAllBytes(meshBundlePath, new byte[8]);

                var index = new ProjectIndex();
                // Pure media bundle (50 textures + AssetBundle) -> must be excluded from sources, but remembered
                index.AddRecord("CAB-media", mediaOnlyPath, 1, "bundle", ClassIDType.AssetBundle);
                for (long id = 2; id <= 50; id++)
                {
                    index.AddRecord("CAB-media", mediaOnlyPath, id, $"Tex_{id}", ClassIDType.Texture2D);
                }

                // Prefab bundle with many textures before a single GameObject + Material
                index.AddRecord("CAB-prefab", prefabBundlePath, 1, "bundle", ClassIDType.AssetBundle);
                for (long id = 2; id <= 20; id++)
                {
                    index.AddRecord("CAB-prefab", prefabBundlePath, id, $"Tex_{id}", ClassIDType.Texture2D);
                }
                index.AddRecord("CAB-prefab", prefabBundlePath, 21, "Root", ClassIDType.GameObject);
                index.AddRecord("CAB-prefab", prefabBundlePath, 22, "Mat", ClassIDType.Material);

                // Mesh bundle
                index.AddRecord("CAB-mesh", meshBundlePath, 1, "MeshA", ClassIDType.Mesh);

                // Missing bundle on disk
                index.AddRecord("CAB-missing", missingBundlePath, 1, "MissingGo", ClassIDType.GameObject);

                var rememberedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var resolveCalls = 0;
                var actual = SemanticGraphService.GetLazyConnectionSourcePaths(
                    index,
                    rememberHandleSourcePath: h => rememberedFiles.Add(h.SerializedFileName),
                    resolveHandleSourcePath: h =>
                    {
                        resolveCalls++;
                        return h.OriginalPath;
                    });

                // Compute reference result via handle enumeration
                var expected = index.GetHandles()
                    .Where(h => (SemanticGraphService.LazyConnectionReferenceTypes.Contains(h.Type) && h.Type != ClassIDType.AssetBundle)
                        || h.Type == ClassIDType.Mesh
                        || h.Type == ClassIDType.MonoBehaviour)
                    .Select(h => h.OriginalPath)
                    .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
                    .Select(Path.GetFullPath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                Assert.Equal(expected, actual);
                Assert.Contains("CAB-media", rememberedFiles);
                Assert.Contains("CAB-prefab", rememberedFiles);
                Assert.Contains("CAB-mesh", rememberedFiles);
                Assert.Contains("CAB-missing", rememberedFiles);
                // Arena fast path should resolve at most once per eligible origin (3 origins, NOT 74 handles!)
                Assert.True(resolveCalls <= 3, $"Expected <= 3 origin resolve calls, got {resolveCalls}");
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void BuildAssetReferenceIndex_ResolvesExternalShaderNameFromArena_WithoutTriggeringLazyDeserialization()
        {
            using var am = new AssetsManager { LazyLoading = true };
            var matFile = CreateSyntheticSerializedFile(am, "CAB-matfile");
            matFile.m_Externals.Add(new FileIdentifier
            {
                fileName = "CAB-shaderfile",
                pathName = "archive:/CAB-shaderfile/CAB-shaderfile"
            });

            // Index external shader & texture in ProjectIndex Arena only (NOT loaded into assetsFileList)
            am.ProjectIndex.AddRecord("CAB-shaderfile", Path.Combine(Path.GetTempPath(), "shaders.bundle"), 500, "Custom/CharacterEye", ClassIDType.Shader);
            am.ProjectIndex.AddRecord("CAB-shaderfile", Path.Combine(Path.GetTempPath(), "shaders.bundle"), 600, "HeroIris_D", ClassIDType.Texture2D);

            var mat = RegisterSyntheticObject<Material>(matFile, 100, ClassIDType.Material, "HeroEyeMat");
            mat.m_Shader = new PPtr<Shader>(1, 500, matFile);
            var eyeTexEnv = (UnityTexEnv)RuntimeHelpers.GetUninitializedObject(typeof(UnityTexEnv));
            eyeTexEnv.m_Texture = new PPtr<Texture>(1, 600, matFile);
            eyeTexEnv.m_Scale = new Vector2(1f, 1f);
            eyeTexEnv.m_Offset = new Vector2(0f, 0f);
            mat.m_SavedProperties = new UnityPropertySheet
            {
                m_TexEnvs = new[]
                {
                    new KeyValuePair<string, UnityTexEnv>("_BaseEyeIrisMap", eyeTexEnv)
                },
                m_Colors = Array.Empty<KeyValuePair<string, Color>>()
            };

            var buildResult = SemanticGraphService.BuildAssetReferenceIndexForFile(matFile);
            var matCache = Assert.Single(buildResult.SemanticRelations.MaterialCaches);
            Assert.Equal("Custom/CharacterEye", matCache.ShaderName);
            Assert.Equal("_BaseEyeIrisMap", matCache.MainTextureSlotName);
            Assert.False(string.IsNullOrEmpty(matCache.MainTextureAssetId));
            Assert.Contains("CAB-shaderfile", matCache.MainTextureAssetId);
        }
    }
}
