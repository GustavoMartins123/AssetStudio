#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AssetStudio;
using AssetStudio.Avalonia.Services;
using AssetStudio.Avalonia.Services.Scripting;
using Xunit;

namespace AssetStudio.Tests
{
    /// <summary>
    /// Reproducible end-to-end Unity Editor roundtrip validation suite.
    /// Generates 100% synthetic, redistributable scenes and prefabs (no commercial game assets),
    /// exports them as .unitypackage archives using AssetStudio's real UnityProjectExporterService,
    /// imports them into a clean Unity Editor project via -batchmode -importPackage, and verifies
    /// GUIDs, transforms, components, MonoBehaviour values (including >256-element arrays),
    /// colliders, LODGroups, converted terrain meshes, and deformable SkinnedMeshRenderers inside Unity.
    /// Reports as skipped via <see cref="UnityEditorFactAttribute"/> when no Unity Editor executable is installed.
    /// </summary>
    public sealed class UnityEditorRoundtripTests
    {
        private sealed class UnityEditorFactAttribute : FactAttribute
        {
            public UnityEditorFactAttribute()
            {
                if (string.IsNullOrEmpty(LocateUnityEditorExecutable()))
                {
                    Skip = "Unity Editor executable not found (set ASSETSTUDIO_UNITY_EDITOR or install Unity via Unity Hub to run Unity Editor roundtrip tests).";
                }
            }
        }

        [UnityEditorFact]
        public async Task UnityEditor_RealImportRoundtrip_ScenePrefabCollidersLodTerrainAndSkinnedMesh_VerifyExactFidelity()
        {
            var unityExe = LocateUnityEditorExecutable();
            Assert.False(string.IsNullOrEmpty(unityExe), "Unity Editor executable must exist when test is not skipped.");

            var tempRoot = Path.Combine(Path.GetTempPath(), $"AssetStudio_UnityRoundtrip_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempRoot);
            try
            {
                using var am = new AssetsManager();
                var sf = CreateSyntheticSerializedFile(am, "level0", "6000.3.18f1");

                // Prepare a real C# MonoBehaviour source so Unity compiles the script and
                // deserializes our exported MonoBehaviour YAML values natively.
                var expectedScriptGuid = UnityProjectGuidUtility.ComputeScriptGuid("Assembly-CSharp.dll", "", "RoundtripProbeBehaviour");

                // Build synthetic shared assets
                var urpShader = CreateSyntheticShader(sf, 10, "Universal Render Pipeline/Lit");
                var albedoTex = CreateSolidRgbaTexture2D(sf, 20, "T_Roundtrip_Albedo", 210, 140, 80, 255);
                var normalTex = CreateSolidRgbaTexture2D(sf, 21, "T_Roundtrip_Normal", 128, 128, 255, 255);
                var urpMat = CreateSyntheticMaterial(
                    sf, 30, "Mat_Roundtrip_Urp", urpShader,
                    texEnvs: new[] { ("_BaseMap", albedoTex), ("_BumpMap", normalTex) },
                    colors: new[] { ("_BaseColor", new Color(0.9f, 0.7f, 0.5f, 1f)) },
                    floats: new[] { ("_Smoothness", 0.65f) },
                    renderQueue: 3000,
                    tags: new[] { ("RenderType", "Opaque") });

                var multiSubMesh = CreateSyntheticTwoSubmeshMesh(sf, 40, "SM_Roundtrip_Tower");
                var lod0Mesh = CreateSyntheticTwoSubmeshMesh(sf, 41, "SM_Roundtrip_LOD0");
                var lod1Mesh = CreateSyntheticTwoSubmeshMesh(sf, 42, "SM_Roundtrip_LOD1");
                var sharedLodMesh = CreateSyntheticTwoSubmeshMesh(sf, 43, "SM_Roundtrip_SharedTrunk");
                var riggedMesh = CreateSyntheticRiggedMesh(sf, 44, "SK_Roundtrip_Knight");

                // Build synthetic 33x33 non-flat TerrainData (max height 25m)
                const int terrainRes = 33;
                var heights = new short[terrainRes * terrainRes];
                for (int y = 0; y < terrainRes; y++)
                {
                    for (int x = 0; x < terrainRes; x++)
                    {
                        float nx = x / (float)(terrainRes - 1);
                        float ny = y / (float)(terrainRes - 1);
                        float h = (float)(Math.Sin(nx * Math.PI) * Math.Sin(ny * Math.PI));
                        heights[y * terrainRes + x] = (short)Math.Clamp((int)(h * 32767f), 0, 32767);
                    }
                }
                var terrainData = CreateSyntheticTerrainData(sf, 50, "TD_Roundtrip_Hill", terrainRes, terrainRes, new Vector3(1f, 25f, 1f), heights);
                var terrainLayer = CreateSyntheticTerrainLayer(sf, 51, "TL_Grass", albedoTex);
                terrainData.m_SplatDatabase.m_TerrainLayers = new List<PPtr<TerrainLayer>> { CreatePPtr<TerrainLayer>(sf, 0, terrainLayer.m_PathID) };

                // MonoScript matching our exported RoundtripProbeBehaviour.cs
                var probeScript = CreateSyntheticMonoScript(sf, 60, "", "RoundtripProbeBehaviour", "Assembly-CSharp.dll");

                // Build full scene hierarchy
                var sceneRootGo = CreateSyntheticGameObject(sf, 100, "RoundtripSceneRoot", true, 0);
                var sceneRootTr = CreateSyntheticTransform(sf, 101, sceneRootGo, null, Vector3.Zero);

                // 1) Multi-submesh node with non-identity Transform + MonoBehaviour (>256-element array)
                var towerGo = CreateSyntheticGameObject(sf, 110, "TowerNode", true, 0);
                var towerTr = CreateSyntheticTransform(sf, 111, towerGo, sceneRootTr, new Vector3(10f, 2.5f, -4f), new Quaternion(0, 0, 0, 1), new Vector3(2f, 3f, 4f));
                var towerMf = CreateSyntheticMeshFilter(sf, 112, towerGo, 0, multiSubMesh.m_PathID);
                var towerMr = CreateSyntheticMeshRenderer(sf, 113, towerGo, true, new[]
                {
                    CreatePPtr<Material>(sf, 0, urpMat.m_PathID),
                    CreatePPtr<Material>(sf, 0, urpMat.m_PathID)
                });
                var probeMb = CreateSyntheticObject<MonoBehaviour>(sf, 114, ClassIDType.MonoBehaviour);
                probeMb.m_GameObject = CreatePPtr<GameObject>(sf, 0, towerGo.m_PathID);
                probeMb.m_Enabled = 1;
                probeMb.m_Script = CreatePPtr<MonoScript>(sf, 0, probeScript.m_PathID);
                probeMb.m_Name = "RoundtripProbeBehaviour";
                AttachComponents(towerGo, towerTr, towerMf, towerMr, probeMb);

                // 2) Inactive child node
                var hiddenGo = CreateSyntheticGameObject(sf, 120, "HiddenProxy", false, 0);
                var hiddenTr = CreateSyntheticTransform(sf, 121, hiddenGo, sceneRootTr, new Vector3(0f, 5f, 0f));
                AttachComponents(hiddenGo, hiddenTr);

                // 3) Dedicated Colliders node (Box, Sphere, Capsule, MeshCollider)
                var colGo = CreateSyntheticGameObject(sf, 130, "CollidersNode", true, 0);
                var colTr = CreateSyntheticTransform(sf, 131, colGo, sceneRootTr, Vector3.Zero);
                var boxCol = CreateSyntheticObject<AssetStudio.Object>(sf, 132, ClassIDType.BoxCollider);
                var sphCol = CreateSyntheticObject<AssetStudio.Object>(sf, 133, ClassIDType.SphereCollider);
                var capCol = CreateSyntheticObject<AssetStudio.Object>(sf, 134, ClassIDType.CapsuleCollider);
                var meshCol = CreateSyntheticObject<AssetStudio.Object>(sf, 135, ClassIDType.MeshCollider);
                AttachComponents(colGo, colTr, boxCol, sphCol, capCol, meshCol);

                // 4) Dedicated LODGroup node (3 levels: LOD0, LOD1, Shared)
                var lodRootGo = CreateSyntheticGameObject(sf, 140, "LodTreeNode", true, 0);
                var lodRootTr = CreateSyntheticTransform(sf, 141, lodRootGo, sceneRootTr, new Vector3(-8f, 0f, 0f));
                var lod0Go = CreateSyntheticGameObject(sf, 142, "LodTree_LOD0", true, 0);
                var lod0Tr = CreateSyntheticTransform(sf, 143, lod0Go, lodRootTr, Vector3.Zero);
                var lod0Mf = CreateSyntheticMeshFilter(sf, 144, lod0Go, 0, lod0Mesh.m_PathID);
                var lod0Mr = CreateSyntheticMeshRenderer(sf, 145, lod0Go, true, new[] { CreatePPtr<Material>(sf, 0, urpMat.m_PathID) });
                AttachComponents(lod0Go, lod0Tr, lod0Mf, lod0Mr);

                var lod1Go = CreateSyntheticGameObject(sf, 146, "LodTree_LOD1", true, 0);
                var lod1Tr = CreateSyntheticTransform(sf, 147, lod1Go, lodRootTr, Vector3.Zero);
                var lod1Mf = CreateSyntheticMeshFilter(sf, 148, lod1Go, 0, lod1Mesh.m_PathID);
                var lod1Mr = CreateSyntheticMeshRenderer(sf, 149, lod1Go, true, new[] { CreatePPtr<Material>(sf, 0, urpMat.m_PathID) });
                AttachComponents(lod1Go, lod1Tr, lod1Mf, lod1Mr);

                var sharedGo = CreateSyntheticGameObject(sf, 150, "LodTree_Shared", true, 0);
                var sharedTr = CreateSyntheticTransform(sf, 151, sharedGo, lodRootTr, Vector3.Zero);
                var sharedMf = CreateSyntheticMeshFilter(sf, 152, sharedGo, 0, sharedLodMesh.m_PathID);
                var sharedMr = CreateSyntheticMeshRenderer(sf, 153, sharedGo, true, new[] { CreatePPtr<Material>(sf, 0, urpMat.m_PathID) });
                AttachComponents(sharedGo, sharedTr, sharedMf, sharedMr);

                var lodGroup = CreateSyntheticObject<LODGroup>(sf, 154, ClassIDType.LODGroup);
                lodGroup.m_GameObject = CreatePPtr<GameObject>(sf, 0, lodRootGo.m_PathID);
                lodGroup.m_Enabled = true;
                lodGroup.m_Size = 6f;
                lodGroup.m_LODs = new[]
                {
                    new LOD { screenRelativeHeight = 0.6f, renderers = new[] { CreatePPtr<Renderer>(sf, 0, lod0Mr.m_PathID), CreatePPtr<Renderer>(sf, 0, sharedMr.m_PathID) } },
                    new LOD { screenRelativeHeight = 0.25f, renderers = new[] { CreatePPtr<Renderer>(sf, 0, lod1Mr.m_PathID), CreatePPtr<Renderer>(sf, 0, sharedMr.m_PathID) } },
                    new LOD { screenRelativeHeight = 0.05f, renderers = Array.Empty<PPtr<Renderer>>() }
                };
                lodRootTr.m_Children = new[]
                {
                    CreatePPtr<Transform>(sf, 0, lod0Tr.m_PathID),
                    CreatePPtr<Transform>(sf, 0, lod1Tr.m_PathID),
                    CreatePPtr<Transform>(sf, 0, sharedTr.m_PathID)
                };
                AttachComponents(lodRootGo, lodRootTr, lodGroup);

                // 5) Dedicated Converted Terrain node
                var terrainGo = CreateSyntheticGameObject(sf, 160, "HillTerrainNode", true, 0);
                var terrainTr = CreateSyntheticTransform(sf, 161, terrainGo, sceneRootTr, new Vector3(0f, 0f, 20f));
                var terrainComp = CreateSyntheticObject<Terrain>(sf, 162, ClassIDType.Terrain);
                terrainComp.m_GameObject = CreatePPtr<GameObject>(sf, 0, terrainGo.m_PathID);
                terrainComp.m_Enabled = 1;
                terrainComp.m_DrawHeightmap = true;
                terrainComp.m_TerrainData = CreatePPtr<TerrainData>(sf, 0, terrainData.m_PathID);
                terrainComp.m_MaterialTemplate = CreatePPtr<Material>(sf, 0, 0);
                AttachComponents(terrainGo, terrainTr, terrainComp);

                // 6) Dedicated Deformable SkinnedMeshRenderer + 3-bone skeleton (bone 1 zero-weight -> 2 active bones!)
                var rigRootGo = CreateSyntheticGameObject(sf, 170, "KnightRigRoot", true, 0);
                var rigRootTr = CreateSyntheticTransform(sf, 171, rigRootGo, sceneRootTr, new Vector3(0f, 0f, -10f));
                var bone0Go = CreateSyntheticGameObject(sf, 172, "Bone_Pelvis", true, 0);
                var bone0Tr = CreateSyntheticTransform(sf, 173, bone0Go, rigRootTr, Vector3.Zero);
                AttachComponents(bone0Go, bone0Tr);

                var bone1Go = CreateSyntheticGameObject(sf, 174, "Bone_Unused", true, 0);
                var bone1Tr = CreateSyntheticTransform(sf, 175, bone1Go, bone0Tr, new Vector3(0f, 1f, 0f));
                AttachComponents(bone1Go, bone1Tr);

                var bone2Go = CreateSyntheticGameObject(sf, 176, "Bone_Chest", true, 0);
                var bone2Tr = CreateSyntheticTransform(sf, 177, bone2Go, bone1Tr, new Vector3(0f, 1f, 0f));
                AttachComponents(bone2Go, bone2Tr);

                bone0Tr.m_Children = new[] { CreatePPtr<Transform>(sf, 0, bone1Tr.m_PathID) };
                bone1Tr.m_Children = new[] { CreatePPtr<Transform>(sf, 0, bone2Tr.m_PathID) };

                var smrGo = CreateSyntheticGameObject(sf, 178, "KnightSkinnedMesh", true, 0);
                var smrTr = CreateSyntheticTransform(sf, 179, smrGo, rigRootTr, Vector3.Zero);
                var smr = CreateSyntheticObject<SkinnedMeshRenderer>(sf, 180, ClassIDType.SkinnedMeshRenderer);
                smr.m_GameObject = CreatePPtr<GameObject>(sf, 0, smrGo.m_PathID);
                smr.m_Enabled = true;
                smr.m_Mesh = CreatePPtr<Mesh>(sf, 0, riggedMesh.m_PathID);
                smr.m_Materials = new[] { CreatePPtr<Material>(sf, 0, urpMat.m_PathID), CreatePPtr<Material>(sf, 0, urpMat.m_PathID) };
                smr.m_Bones = new[]
                {
                    CreatePPtr<Transform>(sf, 0, bone0Tr.m_PathID),
                    CreatePPtr<Transform>(sf, 0, bone1Tr.m_PathID),
                    CreatePPtr<Transform>(sf, 0, bone2Tr.m_PathID)
                };
                smr.m_RootBone = CreatePPtr<Transform>(sf, 0, bone0Tr.m_PathID);
                AttachComponents(smrGo, smrTr, smr);

                rigRootTr.m_Children = new[]
                {
                    CreatePPtr<Transform>(sf, 0, bone0Tr.m_PathID),
                    CreatePPtr<Transform>(sf, 0, smrTr.m_PathID)
                };
                AttachComponents(rigRootGo, rigRootTr);

                sceneRootTr.m_Children = new[]
                {
                    CreatePPtr<Transform>(sf, 0, towerTr.m_PathID),
                    CreatePPtr<Transform>(sf, 0, hiddenTr.m_PathID),
                    CreatePPtr<Transform>(sf, 0, colTr.m_PathID),
                    CreatePPtr<Transform>(sf, 0, lodRootTr.m_PathID),
                    CreatePPtr<Transform>(sf, 0, terrainTr.m_PathID),
                    CreatePPtr<Transform>(sf, 0, rigRootTr.m_PathID)
                };
                AttachComponents(sceneRootGo, sceneRootTr);

                // MonoBehaviour payload exceeding 256 elements to verify untruncated YAML streaming
                var probeFields = new OrderedDictionary
                {
                    ["label"] = "ROUNDTRIP_VERIFIED_LABEL",
                    ["magicNumber"] = 4242,
                    ["items"] = Enumerable.Range(0, 260).Select(i => (object)(i * 3)).ToArray(),
                    ["linkedMaterial"] = new OrderedDictionary { ["m_FileID"] = 0, ["m_PathID"] = urpMat.m_PathID }
                };

                OrderedDictionary? ColliderFields(AssetStudio.Object o)
                {
                    if (ReferenceEquals(o, boxCol))
                    {
                        return new OrderedDictionary
                        {
                            ["m_Center"] = new OrderedDictionary { ["x"] = 1f, ["y"] = 2f, ["z"] = 3f },
                            ["m_Size"] = new OrderedDictionary { ["x"] = 4f, ["y"] = 5f, ["z"] = 6f },
                            ["m_IsTrigger"] = true
                        };
                    }
                    if (ReferenceEquals(o, sphCol))
                    {
                        return new OrderedDictionary
                        {
                            ["m_Center"] = new OrderedDictionary { ["x"] = 0f, ["y"] = 1f, ["z"] = 0f },
                            ["m_Radius"] = 2.5f,
                            ["m_IsTrigger"] = false
                        };
                    }
                    if (ReferenceEquals(o, capCol))
                    {
                        return new OrderedDictionary
                        {
                            ["m_Center"] = new OrderedDictionary { ["x"] = 0f, ["y"] = 0f, ["z"] = 0f },
                            ["m_Radius"] = 0.75f,
                            ["m_Height"] = 3.5f,
                            ["m_Direction"] = 1,
                            ["m_IsTrigger"] = false
                        };
                    }
                    if (ReferenceEquals(o, meshCol))
                    {
                        return new OrderedDictionary
                        {
                            ["m_Convex"] = true,
                            ["m_IsTrigger"] = false,
                            ["m_Mesh"] = new OrderedDictionary { ["m_FileID"] = 0, ["m_PathID"] = multiSubMesh.m_PathID }
                        };
                    }
                    return null;
                }

                // Script callback emitting real compilable RoundtripProbeBehaviour.cs + deterministic .cs.meta GUID
                UnityProjectScriptExportSummary WriteProbeScript(string scriptsDir)
                {
                    Directory.CreateDirectory(scriptsDir);
                    var csPath = Path.Combine(scriptsDir, "RoundtripProbeBehaviour.cs");
                    var csContent = """
                        using UnityEngine;

                        public class RoundtripProbeBehaviour : MonoBehaviour
                        {
                            public string label;
                            public int magicNumber;
                            public int[] items;
                            public Material linkedMaterial;
                        }
                        """;
                    File.WriteAllText(csPath, csContent, new UTF8Encoding(false));
                    File.WriteAllText(csPath + ".meta",
                        $"fileFormatVersion: 2\nguid: {expectedScriptGuid}\nMonoImporter:\n  externalObjects: {{}}\n  serializedVersion: 2\n  defaultReferences: []\n  executionOrder: 0\n  icon: {{instanceID: 0}}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n",
                        new UTF8Encoding(false));
                    return new UnityProjectScriptExportSummary { ExportedScriptFiles = 1, ExportedAssemblies = 1, OutputDirectory = scriptsDir };
                }

                var exporter = new UnityProjectExporterService();

                // Export 1: Complete Scene .unitypackage (LOD0 filter, collider auxiliary OBJs, terrain FBX, rig FBX, MonoBehaviour + C# script)
                var scenePkgPath = Path.Combine(tempRoot, "Roundtrip_Scene.unitypackage");
                var sceneRes = exporter.ExportSceneToUnityProject(
                    new[] { sceneRootGo },
                    new UnityProjectExportOptions
                    {
                        OutputDirectory = Path.Combine(tempRoot, "StageScene"),
                        SceneName = "RoundtripTestScene",
                        IncludeInactive = true,
                        TargetLodLevel = 0,
                        ExportColliderAuxiliaryMeshes = true,
                        AlsoBuildUnityPackage = true,
                        UnityPackageOutputPath = scenePkgPath,
                        WriteManifest = true,
                        MonoBehaviourFieldProvider = o => ReferenceEquals(o, probeMb) ? probeFields : null,
                        ColliderFieldProvider = ColliderFields,
                        IncludeScriptsCallback = (dir, _) => WriteProbeScript(dir)
                    });
                Assert.True(sceneRes.Success, sceneRes.ErrorMessage);
                Assert.False(sceneRes.IsPartial);
                Assert.Equal(ExportOutcomeStatus.Completed, sceneRes.Status);
                Assert.True(File.Exists(scenePkgPath));

                // Export 2: Standalone Prefab .unitypackage (KnightRigRoot with 2 active bones & bindposes)
                var prefabPkgPath = Path.Combine(tempRoot, "Roundtrip_KnightPrefab.unitypackage");
                var expectedPrefabGuid = UnityProjectExporterService.ComputeUnityAssetGuid("prefab", "level0", rigRootGo.m_PathID, "KnightRigRoot");
                var prefabRes = exporter.ExportPrefabToUnityProject(
                    rigRootGo,
                    new UnityProjectExportOptions
                    {
                        OutputDirectory = Path.Combine(tempRoot, "StagePrefab"),
                        SceneName = "KnightRigRoot",
                        IncludeInactive = true,
                        AlsoBuildUnityPackage = true,
                        UnityPackageOutputPath = prefabPkgPath,
                        WriteManifest = true
                    });
                Assert.True(prefabRes.Success, prefabRes.ErrorMessage);
                Assert.True(File.Exists(prefabPkgPath));

                // Create clean Unity Editor project, import BOTH packages, and run in-Editor verification!
                var unityProjDir = Path.Combine(tempRoot, "UnityProj");
                var importLog1 = Path.Combine(tempRoot, "unity_import1.log");
                var importLog2 = Path.Combine(tempRoot, "unity_import2.log");
                var verifyLog = Path.Combine(tempRoot, "unity_verify.log");

                var (exit1, _, _) = await TestProcessRunner.RunAsync(
                    unityExe!,
                    $"-batchmode -nographics -quit -createProject \"{unityProjDir}\" -importPackage \"{scenePkgPath}\" -logFile \"{importLog1}\"",
                    tempRoot,
                    TimeSpan.FromMinutes(4));
                if (exit1 != 0) Assert.Fail($"Unity import1 failed (exit={exit1}). Log tail:\n{TailFile(importLog1, 50)}");

                var (exit2, _, _) = await TestProcessRunner.RunAsync(
                    unityExe!,
                    $"-batchmode -nographics -quit -projectPath \"{unityProjDir}\" -importPackage \"{prefabPkgPath}\" -logFile \"{importLog2}\"",
                    tempRoot,
                    TimeSpan.FromMinutes(3));
                if (exit2 != 0) Assert.Fail($"Unity import2 failed (exit={exit2}). Log tail:\n{TailFile(importLog2, 50)}");

                // Drop Editor verification script into Assets/Editor/
                var editorDir = Path.Combine(unityProjDir, "Assets", "Editor");
                Directory.CreateDirectory(editorDir);
                File.WriteAllText(Path.Combine(editorDir, "UnityRoundtripVerifier.cs"), BuildUnityVerifierScript(), new UTF8Encoding(false));

                var (exit3, _, _) = await TestProcessRunner.RunAsync(
                    unityExe!,
                    $"-batchmode -nographics -quit -projectPath \"{unityProjDir}\" -executeMethod UnityRoundtripVerifier.Run -logFile \"{verifyLog}\"",
                    tempRoot,
                    TimeSpan.FromMinutes(3));
                if (exit3 != 0) Assert.Fail($"Unity verify failed (exit={exit3}). Log tail:\n{TailFile(verifyLog, 80)}");

                // Check logs for ZERO FBX corruption or script compile errors
                foreach (var logPath in new[] { importLog1, importLog2, verifyLog })
                {
                    var logText = ReadSharedText(logPath);
                    Assert.DoesNotContain("ImportFBX Errors", logText, StringComparison.Ordinal);
                    Assert.DoesNotContain("File is corrupted", logText, StringComparison.Ordinal);
                    Assert.DoesNotContain("error CS", logText, StringComparison.Ordinal);
                }

                // Parse structured JSON report written by real Unity Engine!
                var reportPath = Path.Combine(unityProjDir, "roundtrip_report.json");
                if (!File.Exists(reportPath)) Assert.Fail($"Unity verifier did not emit {reportPath}. Log tail:\n{TailFile(verifyLog, 60)}");

                using var doc = JsonDocument.Parse(File.ReadAllText(reportPath));
                var r = doc.RootElement;

                // 1) GUIDs & Assets
                Assert.Equal(expectedScriptGuid, r.GetProperty("scriptGuid").GetString());
                Assert.Equal(expectedPrefabGuid, r.GetProperty("prefabGuid").GetString());
                Assert.Equal(0, r.GetProperty("missingScripts").GetInt32());
                Assert.Equal(0, r.GetProperty("missingFbxMeshes").GetInt32());
                Assert.True(r.GetProperty("materialHasMainTex").GetBoolean());
                Assert.True(r.GetProperty("materialHasBumpMap").GetBoolean());

                // 2) Scene Hierarchy, Transform, Multi-submesh, Inactive GO, and MonoBehaviour values
                Assert.Equal(10f, r.GetProperty("towerPosX").GetSingle(), 3);
                Assert.Equal(2.5f, r.GetProperty("towerPosY").GetSingle(), 3);
                Assert.Equal(-4f, r.GetProperty("towerPosZ").GetSingle(), 3);
                Assert.Equal(2f, r.GetProperty("towerScaleX").GetSingle(), 3);
                Assert.Equal(3f, r.GetProperty("towerScaleY").GetSingle(), 3);
                Assert.Equal(4f, r.GetProperty("towerScaleZ").GetSingle(), 3);
                Assert.Equal(2, r.GetProperty("towerSubMeshCount").GetInt32());
                Assert.False(r.GetProperty("hiddenIsActive").GetBoolean());

                // Verify real Unity deserialization of 260-element MonoBehaviour array & linked material reference
                Assert.Equal("ROUNDTRIP_VERIFIED_LABEL", r.GetProperty("probeLabel").GetString());
                Assert.Equal(4242, r.GetProperty("probeMagicNumber").GetInt32());
                Assert.Equal(260, r.GetProperty("probeItemsLength").GetInt32());
                Assert.Equal(0, r.GetProperty("probeItemFirst").GetInt32());
                Assert.Equal(777, r.GetProperty("probeItemLast").GetInt32()); // 259 * 3
                Assert.Equal("Mat_Roundtrip_Urp", r.GetProperty("probeLinkedMaterialName").GetString());

                // 3) Colliders (Box, Sphere, Capsule, MeshCollider + auxiliary OBJ imported!)
                Assert.True(r.GetProperty("boxIsTrigger").GetBoolean());
                Assert.Equal(4f, r.GetProperty("boxSizeX").GetSingle(), 3);
                Assert.Equal(2.5f, r.GetProperty("sphereRadius").GetSingle(), 3);
                Assert.Equal(3.5f, r.GetProperty("capsuleHeight").GetSingle(), 3);
                Assert.True(r.GetProperty("meshColliderHasMesh").GetBoolean());
                Assert.True(r.GetProperty("meshColliderConvex").GetBoolean());
                Assert.True(r.GetProperty("auxiliaryColliderMeshesImported").GetInt32() >= 3);

                // 4) LODGroup (3 levels, exact transitions, LOD1 mesh filtered out by TargetLodLevel=0!)
                Assert.Equal(3, r.GetProperty("lodCount").GetInt32());
                Assert.Equal(0.6f, r.GetProperty("lod0Height").GetSingle(), 2);
                Assert.True(r.GetProperty("lod0HasMesh").GetBoolean());
                Assert.True(r.GetProperty("lodSharedHasMesh").GetBoolean());
                Assert.False(r.GetProperty("lod1HasMesh").GetBoolean());

                // 5) Converted Terrain FBX (non-flat elevation bounds verified inside Unity!)
                Assert.True(r.GetProperty("terrainHasMesh").GetBoolean());
                Assert.InRange(r.GetProperty("terrainBoundsSizeY").GetSingle(), 20f, 26f);

                // 6) Deformable SkinnedMeshRenderer (both in Scene AND in standalone Prefab!)
                Assert.True(r.GetProperty("sceneSmrHasMesh").GetBoolean());
                Assert.Equal(2, r.GetProperty("sceneSmrBindposeCount").GetInt32());
                Assert.Equal(2, r.GetProperty("sceneSmrBoneCount").GetInt32());
                Assert.Equal("Bone_Pelvis", r.GetProperty("sceneSmrBone0Name").GetString());
                Assert.Equal("Bone_Chest", r.GetProperty("sceneSmrBone1Name").GetString());
                Assert.Equal("Bone_Pelvis", r.GetProperty("sceneSmrRootBoneName").GetString());

                Assert.True(r.GetProperty("prefabLoaded").GetBoolean());
                Assert.Equal(2, r.GetProperty("prefabSmrBindposeCount").GetInt32());
                Assert.Equal(2, r.GetProperty("prefabSmrBoneCount").GetInt32());
            }
            finally
            {
                try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true); } catch { }
            }
        }

        private static string? LocateUnityEditorExecutable()
        {
            var envPath = Environment.GetEnvironmentVariable("ASSETSTUDIO_UNITY_EDITOR");
            if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath))
            {
                return envPath;
            }

            var hubRoots = new List<string>();
            if (OperatingSystem.IsWindows())
            {
                var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                if (!string.IsNullOrWhiteSpace(programFiles))
                {
                    hubRoots.Add(Path.Combine(programFiles, "Unity", "Hub", "Editor"));
                }

                foreach (var drive in DriveInfo.GetDrives())
                {
                    if (drive.DriveType != DriveType.Fixed) continue;
                    hubRoots.Add(Path.Combine(drive.RootDirectory.FullName, "Unity", "Hub", "Editor"));
                }
            }

            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(userProfile))
            {
                hubRoots.Add(Path.Combine(userProfile, "Unity", "Hub", "Editor"));
            }

            foreach (var hub in hubRoots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(hub)) continue;
                var candidates = Directory.GetDirectories(hub)
                    .OrderByDescending(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)
                    .Select(d => OperatingSystem.IsWindows()
                        ? Path.Combine(d, "Editor", "Unity.exe")
                        : Path.Combine(d, "Editor", "Unity"))
                    .Where(File.Exists)
                    .ToList();
                if (candidates.Count > 0)
                {
                    return candidates[0];
                }
            }

            return null;
        }

        private static string BuildUnityVerifierScript() => """
            using System;
            using System.IO;
            using System.Linq;
            using UnityEditor;
            using UnityEditor.SceneManagement;
            using UnityEngine;

            public static class UnityRoundtripVerifier
            {
                [Serializable]
                private sealed class Report
                {
                    public string scriptGuid;
                    public string prefabGuid;
                    public int missingScripts;
                    public int missingFbxMeshes;
                    public bool materialHasMainTex;
                    public bool materialHasBumpMap;
                    public float towerPosX, towerPosY, towerPosZ;
                    public float towerScaleX, towerScaleY, towerScaleZ;
                    public int towerSubMeshCount;
                    public bool hiddenIsActive;
                    public string probeLabel;
                    public int probeMagicNumber;
                    public int probeItemsLength;
                    public int probeItemFirst;
                    public int probeItemLast;
                    public string probeLinkedMaterialName;
                    public bool boxIsTrigger;
                    public float boxSizeX;
                    public float sphereRadius;
                    public float capsuleHeight;
                    public bool meshColliderHasMesh;
                    public bool meshColliderConvex;
                    public int auxiliaryColliderMeshesImported;
                    public int lodCount;
                    public float lod0Height;
                    public bool lod0HasMesh;
                    public bool lodSharedHasMesh;
                    public bool lod1HasMesh;
                    public bool terrainHasMesh;
                    public float terrainBoundsSizeY;
                    public bool sceneSmrHasMesh;
                    public int sceneSmrBindposeCount;
                    public int sceneSmrBoneCount;
                    public string sceneSmrBone0Name;
                    public string sceneSmrBone1Name;
                    public string sceneSmrRootBoneName;
                    public bool prefabLoaded;
                    public int prefabSmrBindposeCount;
                    public int prefabSmrBoneCount;
                }

                public static void Run()
                {
                    var rep = new Report();
                    rep.scriptGuid = AssetDatabase.AssetPathToGUID("Assets/Scripts/RoundtripProbeBehaviour.cs");
                    rep.prefabGuid = AssetDatabase.AssetPathToGUID("Assets/Prefabs/KnightRigRoot.prefab");

                    foreach (var guid in AssetDatabase.FindAssets("", new[] { "Assets/Meshes" }))
                    {
                        var p = AssetDatabase.GUIDToAssetPath(guid);
                        if (!p.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)) continue;
                        if (!AssetDatabase.LoadAllAssetsAtPath(p).OfType<Mesh>().Any()) rep.missingFbxMeshes++;
                    }

                    foreach (var guid in AssetDatabase.FindAssets("", new[] { "Assets/Colliders" }))
                    {
                        var p = AssetDatabase.GUIDToAssetPath(guid);
                        if (!p.EndsWith(".obj", StringComparison.OrdinalIgnoreCase)) continue;
                        if (AssetDatabase.LoadAllAssetsAtPath(p).OfType<Mesh>().Any()) rep.auxiliaryColliderMeshesImported++;
                    }

                    var mat = AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/Mat_Roundtrip_Urp.mat");
                    if (mat != null)
                    {
                        rep.materialHasMainTex = mat.HasProperty("_MainTex") && mat.GetTexture("_MainTex") != null;
                        rep.materialHasBumpMap = mat.HasProperty("_BumpMap") && mat.GetTexture("_BumpMap") != null;
                    }

                    var scene = EditorSceneManager.OpenScene("Assets/Scenes/RoundtripTestScene.unity", OpenSceneMode.Single);
                    var allGos = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Transform>(true)).Select(t => t.gameObject).ToList();
                    foreach (var go in allGos) rep.missingScripts += GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go);

                    var tower = allGos.First(g => g.name == "TowerNode");
                    rep.towerPosX = tower.transform.localPosition.x;
                    rep.towerPosY = tower.transform.localPosition.y;
                    rep.towerPosZ = tower.transform.localPosition.z;
                    rep.towerScaleX = tower.transform.localScale.x;
                    rep.towerScaleY = tower.transform.localScale.y;
                    rep.towerScaleZ = tower.transform.localScale.z;
                    var towerMf = tower.GetComponent<MeshFilter>();
                    rep.towerSubMeshCount = (towerMf != null && towerMf.sharedMesh != null) ? towerMf.sharedMesh.subMeshCount : 0;

                    var probe = tower.GetComponent<RoundtripProbeBehaviour>();
                    if (probe != null)
                    {
                        rep.probeLabel = probe.label ?? "";
                        rep.probeMagicNumber = probe.magicNumber;
                        rep.probeItemsLength = probe.items != null ? probe.items.Length : 0;
                        rep.probeItemFirst = probe.items != null && probe.items.Length > 0 ? probe.items[0] : -1;
                        rep.probeItemLast = probe.items != null && probe.items.Length > 0 ? probe.items[probe.items.Length - 1] : -1;
                        rep.probeLinkedMaterialName = probe.linkedMaterial != null ? probe.linkedMaterial.name : "";
                    }

                    rep.hiddenIsActive = allGos.First(g => g.name == "HiddenProxy").activeSelf;

                    var col = allGos.First(g => g.name == "CollidersNode");
                    var box = col.GetComponent<BoxCollider>();
                    if (box != null) { rep.boxIsTrigger = box.isTrigger; rep.boxSizeX = box.size.x; }
                    var sph = col.GetComponent<SphereCollider>();
                    if (sph != null) rep.sphereRadius = sph.radius;
                    var cap = col.GetComponent<CapsuleCollider>();
                    if (cap != null) rep.capsuleHeight = cap.height;
                    var mc = col.GetComponent<MeshCollider>();
                    if (mc != null) { rep.meshColliderHasMesh = mc.sharedMesh != null; rep.meshColliderConvex = mc.convex; }

                    var lodNode = allGos.First(g => g.name == "LodTreeNode");
                    var lg = lodNode.GetComponent<LODGroup>();
                    if (lg != null)
                    {
                        var lods = lg.GetLODs();
                        rep.lodCount = lods.Length;
                        if (lods.Length > 0) rep.lod0Height = lods[0].screenRelativeTransitionHeight;
                    }
                    var mfLod0 = allGos.First(g => g.name == "LodTree_LOD0").GetComponent<MeshFilter>();
                    rep.lod0HasMesh = mfLod0 != null && mfLod0.sharedMesh != null;
                    var mfShared = allGos.First(g => g.name == "LodTree_Shared").GetComponent<MeshFilter>();
                    rep.lodSharedHasMesh = mfShared != null && mfShared.sharedMesh != null;
                    var mfLod1 = allGos.First(g => g.name == "LodTree_LOD1").GetComponent<MeshFilter>();
                    rep.lod1HasMesh = mfLod1 != null && mfLod1.sharedMesh != null;

                    var terrainNode = allGos.First(g => g.name == "HillTerrainNode");
                    var tMf = terrainNode.GetComponent<MeshFilter>();
                    rep.terrainHasMesh = tMf != null && tMf.sharedMesh != null;
                    if (rep.terrainHasMesh) rep.terrainBoundsSizeY = tMf.sharedMesh.bounds.size.y;

                    var smr = allGos.First(g => g.name == "KnightSkinnedMesh").GetComponent<SkinnedMeshRenderer>();
                    if (smr != null && smr.sharedMesh != null)
                    {
                        rep.sceneSmrHasMesh = true;
                        rep.sceneSmrBindposeCount = smr.sharedMesh.bindposes != null ? smr.sharedMesh.bindposes.Length : 0;
                        rep.sceneSmrBoneCount = smr.bones != null ? smr.bones.Length : 0;
                        if (smr.bones != null && smr.bones.Length >= 2)
                        {
                            rep.sceneSmrBone0Name = smr.bones[0] != null ? smr.bones[0].name : "";
                            rep.sceneSmrBone1Name = smr.bones[1] != null ? smr.bones[1].name : "";
                        }
                        rep.sceneSmrRootBoneName = smr.rootBone != null ? smr.rootBone.name : "";
                    }

                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/KnightRigRoot.prefab");
                    rep.prefabLoaded = prefab != null;
                    var prefabSmr = prefab != null ? prefab.GetComponentInChildren<SkinnedMeshRenderer>(true) : null;
                    if (prefabSmr != null && prefabSmr.sharedMesh != null)
                    {
                        rep.prefabSmrBindposeCount = prefabSmr.sharedMesh.bindposes != null ? prefabSmr.sharedMesh.bindposes.Length : 0;
                        rep.prefabSmrBoneCount = prefabSmr.bones != null ? prefabSmr.bones.Length : 0;
                    }

                    var projectRoot = Directory.GetParent(Application.dataPath).FullName;
                    File.WriteAllText(Path.Combine(projectRoot, "roundtrip_report.json"), JsonUtility.ToJson(rep, true));
                }
            }
            """;

        private static string ReadSharedText(string path)
        {
            if (!File.Exists(path)) return string.Empty;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            return sr.ReadToEnd();
        }

        private static string TailFile(string path, int lines)
        {
            if (!File.Exists(path)) return "<missing>";
            var all = ReadSharedText(path).Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            return string.Join(Environment.NewLine, all.Skip(Math.Max(0, all.Length - lines)));
        }

        // --- Minimal self-contained synthetic Unity object factories ---

        private static SerializedFile CreateSyntheticSerializedFile(AssetsManager am, string fileName, string unityVersion)
        {
            var sf = (SerializedFile)RuntimeHelpers.GetUninitializedObject(typeof(SerializedFile));
            sf.assetsManager = am;
            sf.fileName = fileName;
            sf.originalPath = fileName;
            sf.fullName = fileName;
            sf.reader = new FileReader(fileName, new MemoryStream(Array.Empty<byte>()));
            sf.m_TargetPlatform = BuildTarget.StandaloneWindows64;
            sf.m_Externals = new List<FileIdentifier>();
            sf.m_Objects = new List<ObjectInfo>();
            sf.m_Types = new List<SerializedType>();
            sf.Objects = new List<AssetStudio.Object>();
            sf.ObjectsDic = new Dictionary<long, AssetStudio.Object>();
            sf.ObjectInfoDic = new Dictionary<long, ObjectInfo>();
            sf.header = new SerializedFileHeader { m_Version = SerializedFileFormatVersion.LargeFilesSupport };
            sf.SetVersion(unityVersion);
            typeof(SerializedFile)
                .GetField("objectCollectionLock", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(sf, new object());
            lock (am.loadLock)
            {
                am.assetsFileList.Add(sf);
            }
            return sf;
        }

        private static T CreateSyntheticObject<T>(SerializedFile sf, long pathId, ClassIDType type) where T : AssetStudio.Object
        {
            var obj = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
            obj.assetsFile = sf;
            obj.m_PathID = pathId;
            obj.type = type;
            obj.version = sf.version;
            obj.platform = BuildTarget.StandaloneWindows64;
            sf.AddObject(obj);
            sf.ObjectInfoDic[pathId] = new ObjectInfo { m_PathID = pathId, classID = (int)type, typeID = (int)type };
            return obj;
        }

        private static PPtr<T> CreatePPtr<T>(SerializedFile sf, int fileId, long pathId) where T : AssetStudio.Object
            => new(fileId, pathId, sf);

        private static GameObject CreateSyntheticGameObject(SerializedFile sf, long pathId, string name, bool isActive, int layer)
        {
            var go = CreateSyntheticObject<GameObject>(sf, pathId, ClassIDType.GameObject);
            go.m_Name = name;
            go.m_IsActive = isActive;
            go.m_Layer = layer;
            go.m_Tag = 0;
            go.m_Components = Array.Empty<PPtr<Component>>();
            return go;
        }

        private static Transform CreateSyntheticTransform(SerializedFile sf, long pathId, GameObject go, Transform? parent, Vector3 pos, Quaternion? rot = null, Vector3? scale = null)
        {
            var tr = CreateSyntheticObject<Transform>(sf, pathId, ClassIDType.Transform);
            tr.m_GameObject = CreatePPtr<GameObject>(sf, 0, go.m_PathID);
            tr.m_LocalPosition = pos;
            tr.m_LocalRotation = rot ?? new Quaternion(0, 0, 0, 1);
            tr.m_LocalScale = scale ?? Vector3.One;
            tr.m_Father = parent != null ? CreatePPtr<Transform>(sf, 0, parent.m_PathID) : CreatePPtr<Transform>(sf, 0, 0);
            tr.m_Children = Array.Empty<PPtr<Transform>>();
            go.m_Transform = tr;
            return tr;
        }

        private static MeshFilter CreateSyntheticMeshFilter(SerializedFile sf, long pathId, GameObject go, int fileId, long meshPathId)
        {
            var mf = CreateSyntheticObject<MeshFilter>(sf, pathId, ClassIDType.MeshFilter);
            mf.m_GameObject = CreatePPtr<GameObject>(sf, 0, go.m_PathID);
            mf.m_Mesh = CreatePPtr<Mesh>(sf, fileId, meshPathId);
            go.m_MeshFilter = mf;
            return mf;
        }

        private static MeshRenderer CreateSyntheticMeshRenderer(SerializedFile sf, long pathId, GameObject go, bool enabled, PPtr<Material>[] mats)
        {
            var mr = CreateSyntheticObject<MeshRenderer>(sf, pathId, ClassIDType.MeshRenderer);
            mr.m_GameObject = CreatePPtr<GameObject>(sf, 0, go.m_PathID);
            mr.m_Enabled = enabled;
            mr.m_CastShadows = 1;
            mr.m_ReceiveShadows = 1;
            mr.m_Materials = mats;
            go.m_MeshRenderer = mr;
            return mr;
        }

        private static Shader CreateSyntheticShader(SerializedFile sf, long pathId, string shaderName)
        {
            var shader = CreateSyntheticObject<Shader>(sf, pathId, ClassIDType.Shader);
            shader.m_Name = shaderName;
            var parsed = (SerializedShader)RuntimeHelpers.GetUninitializedObject(typeof(SerializedShader));
            parsed.m_Name = shaderName;
            parsed.m_SubShaders = Array.Empty<SerializedSubShader>();
            shader.m_ParsedForm = parsed;
            return shader;
        }

        private static Texture2D CreateSolidRgbaTexture2D(SerializedFile sf, long pathId, string name, byte r, byte g, byte b, byte a)
        {
            var pixels = new byte[4 * 4 * 4];
            for (int i = 0; i < 16; i++)
            {
                pixels[i * 4 + 0] = r; pixels[i * 4 + 1] = g; pixels[i * 4 + 2] = b; pixels[i * 4 + 3] = a;
            }
            var tex = CreateSyntheticObject<Texture2D>(sf, pathId, ClassIDType.Texture2D);
            tex.m_Name = name;
            tex.m_Width = 4;
            tex.m_Height = 4;
            tex.m_TextureFormat = TextureFormat.RGBA32;
            tex.m_MipCount = 1;
            tex.m_ImageCount = 1;
            tex.m_TextureDimension = 2;
            tex.image_data = new ResourceReader(pixels);
            return tex;
        }

        private static Material CreateSyntheticMaterial(
            SerializedFile sf, long pathId, string name, Shader shader,
            (string Key, Texture2D Texture)[] texEnvs,
            (string Key, Color Color)[] colors,
            (string Key, float Value)[] floats,
            int renderQueue,
            (string Key, string Value)[] tags)
        {
            var mat = CreateSyntheticObject<Material>(sf, pathId, ClassIDType.Material);
            mat.m_Name = name;
            mat.m_Shader = CreatePPtr<Shader>(sf, 0, shader.m_PathID);
            mat.m_Parent = CreatePPtr<Material>(sf, 0, 0);
            mat.m_CustomRenderQueue = renderQueue;
            mat.m_ValidKeywords = Array.Empty<string>();
            mat.m_InvalidKeywords = Array.Empty<string>();
            mat.disabledShaderPasses = Array.Empty<string>();
            mat.stringTagMap = tags.Select(t => new KeyValuePair<string, string>(t.Key, t.Value)).ToArray();
            mat.m_SavedProperties = new UnityPropertySheet
            {
                m_TexEnvs = texEnvs.Select(t => new KeyValuePair<string, UnityTexEnv>(t.Key, new UnityTexEnv(CreatePPtr<Texture>(sf, 0, t.Texture.m_PathID), new Vector2(1f, 1f), Vector2.Zero))).ToArray(),
                m_Ints = Array.Empty<KeyValuePair<string, int>>(),
                m_Floats = floats.Select(f => new KeyValuePair<string, float>(f.Key, f.Value)).ToArray(),
                m_Colors = colors.Select(c => new KeyValuePair<string, Color>(c.Key, c.Color)).ToArray()
            };
            return mat;
        }

        private static MonoScript CreateSyntheticMonoScript(SerializedFile sf, long pathId, string ns, string className, string assemblyName)
        {
            var ms = CreateSyntheticObject<MonoScript>(sf, pathId, ClassIDType.MonoScript);
            ms.m_Name = className;
            ms.m_Namespace = ns;
            ms.m_ClassName = className;
            ms.m_AssemblyName = assemblyName;
            return ms;
        }

        private static Mesh CreateSyntheticTwoSubmeshMesh(SerializedFile sf, long pathId, string name)
        {
            var mesh = CreateSyntheticObject<Mesh>(sf, pathId, ClassIDType.Mesh);
            mesh.m_Name = name;
            mesh.m_VertexCount = 4;
            mesh.m_Vertices = new float[] { 0, 0, 0,  1, 0, 0,  1, 1, 0,  0, 1, 0 };
            mesh.m_Normals = new float[] { 0, 0, -1,  0, 0, -1,  0, 0, -1,  0, 0, -1 };
            mesh.m_UV0 = new float[] { 0, 0,  1, 0,  1, 1,  0, 1 };
            mesh.m_Indices = new List<uint> { 0, 1, 2, 0, 2, 3 };
            var sub0 = (SubMesh)RuntimeHelpers.GetUninitializedObject(typeof(SubMesh));
            sub0.firstByte = 0; sub0.indexCount = 3; sub0.topology = GfxPrimitiveType.Triangles; sub0.vertexCount = 4;
            var sub1 = (SubMesh)RuntimeHelpers.GetUninitializedObject(typeof(SubMesh));
            sub1.firstByte = 6; sub1.indexCount = 3; sub1.topology = GfxPrimitiveType.Triangles; sub1.vertexCount = 4;
            mesh.m_SubMeshes = new[] { sub0, sub1 };
            typeof(Mesh).GetField("m_DataProcessed", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(mesh, true);
            typeof(Mesh).GetField("m_ProcessLock", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(mesh, new object());
            return mesh;
        }

        private static Mesh CreateSyntheticRiggedMesh(SerializedFile sf, long pathId, string name)
        {
            var mesh = CreateSyntheticTwoSubmeshMesh(sf, pathId, name);
            mesh.m_BindPose = new[]
            {
                new Matrix4x4(new float[] { 1, 0, 0, 0,  0, 1, 0, 0,  0, 0, 1, 0,  0, 0, 0, 1 }),
                new Matrix4x4(new float[] { 1, 0, 0, 0,  0, 1, 0, 0,  0, 0, 1, 0,  0, -1, 0, 1 }),
                new Matrix4x4(new float[] { 1, 0, 0, 0,  0, 1, 0, 0,  0, 0, 1, 0,  0, -2, 0, 1 })
            };
            mesh.m_Skin = new[]
            {
                new BoneWeights4 { weight = new[] { 1f, 0f, 0f, 0f }, boneIndex = new[] { 0, 0, 0, 0 } },
                new BoneWeights4 { weight = new[] { 0.5f, 0.5f, 0f, 0f }, boneIndex = new[] { 0, 2, 0, 0 } },
                new BoneWeights4 { weight = new[] { 1f, 0f, 0f, 0f }, boneIndex = new[] { 2, 0, 0, 0 } },
                new BoneWeights4 { weight = new[] { 1f, 0f, 0f, 0f }, boneIndex = new[] { 2, 0, 0, 0 } }
            };
            return mesh;
        }

        private static TerrainData CreateSyntheticTerrainData(SerializedFile sf, long pathId, string name, int w, int h, Vector3 scale, short[] heights)
        {
            var td = CreateSyntheticObject<TerrainData>(sf, pathId, ClassIDType.TerrainData);
            td.m_Name = name;
            td.m_SplatDatabase = new TerrainSplatDatabase();
            td.m_Heightmap = new TerrainHeightmap
            {
                m_Width = w, m_Height = h, m_Resolution = Math.Max(w, h),
                m_Scale = scale, m_Heights = heights, m_Holes = Array.Empty<byte>()
            };
            return td;
        }

        private static TerrainLayer CreateSyntheticTerrainLayer(SerializedFile sf, long pathId, string name, Texture2D diffuse)
        {
            var layer = CreateSyntheticObject<TerrainLayer>(sf, pathId, ClassIDType.TerrainLayer);
            layer.m_Name = name;
            layer.m_DiffuseTexture = CreatePPtr<Texture2D>(sf, 0, diffuse.m_PathID);
            layer.m_NormalMapTexture = CreatePPtr<Texture2D>(sf, 0, 0);
            layer.m_MaskMapTexture = CreatePPtr<Texture2D>(sf, 0, 0);
            layer.m_TileSize = new Vector2(10f, 10f);
            layer.m_TileOffset = Vector2.Zero;
            return layer;
        }

        private static void AttachComponents(GameObject go, params AssetStudio.Object[] components)
        {
            go.m_Components = components.Select(c => CreatePPtr<Component>(go.assetsFile, 0, c.m_PathID)).ToArray();
        }
    }
}
