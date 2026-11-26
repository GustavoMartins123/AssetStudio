#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AssetStudio;
using AssetStudio.Avalonia.Services;
using AssetStudio.Avalonia.Services.Scripting;
using Xunit;

namespace AssetStudio.Tests
{
    public sealed class Block4UnityPackageAndPrefabTests
    {
        // =====================================================================
        // 1. UnityPackageExporterService (SCENE-13)
        // =====================================================================

        [Fact]
        public void UnityPackageExporter_FullProjectWithScripts_PacksExactTarGzLayoutAndPassesRawTarInspection()
        {
            var tempRoot = CreateTempDirectory("block4_pkg_full");
            try
            {
                var projectDir = Path.Combine(tempRoot, "SampleProject");
                var assetsDir = Path.Combine(projectDir, "Assets");
                var scenesDir = Path.Combine(assetsDir, "Scenes");
                var matsDir = Path.Combine(assetsDir, "Materials");
                var meshesDir = Path.Combine(assetsDir, "Meshes");
                var texturesDir = Path.Combine(assetsDir, "Textures");
                var scriptsDir = Path.Combine(assetsDir, "Scripts");
                var projSettingsDir = Path.Combine(projectDir, "ProjectSettings");

                Directory.CreateDirectory(scenesDir);
                Directory.CreateDirectory(matsDir);
                Directory.CreateDirectory(meshesDir);
                Directory.CreateDirectory(texturesDir);
                Directory.CreateDirectory(scriptsDir);
                Directory.CreateDirectory(projSettingsDir);

                var utf8NoBom = new UTF8Encoding(false);

                // ProjectSettings & ExportManifest.json MUST be excluded from .unitypackage
                File.WriteAllText(Path.Combine(projSettingsDir, "ProjectVersion.txt"), "m_EditorVersion: 2022.3.40f1\n", utf8NoBom);
                File.WriteAllText(Path.Combine(projectDir, "ExportManifest.json"), "{\"sceneName\":\"Demo\"}", utf8NoBom);
                File.WriteAllText(Path.Combine(assetsDir, "ExportManifest.json"), "{\"ignore\":true}", utf8NoBom);

                // Folder .meta files
                var guidScenesFolder = "11111111111111111111111111111101";
                var guidMatsFolder = "11111111111111111111111111111102";
                var guidMeshesFolder = "11111111111111111111111111111103";
                var guidTexturesFolder = "11111111111111111111111111111104";
                var guidScriptsFolder = "11111111111111111111111111111105";

                WriteFolderMeta(Path.Combine(assetsDir, "Scenes.meta"), guidScenesFolder);
                WriteFolderMeta(Path.Combine(assetsDir, "Materials.meta"), guidMatsFolder);
                WriteFolderMeta(Path.Combine(assetsDir, "Meshes.meta"), guidMeshesFolder);
                WriteFolderMeta(Path.Combine(assetsDir, "Textures.meta"), guidTexturesFolder);
                WriteFolderMeta(Path.Combine(assetsDir, "Scripts.meta"), guidScriptsFolder);

                // Asset files + matching .meta files
                var expectedAssets = new Dictionary<string, (string Pathname, byte[] AssetBytes, byte[] MetaBytes)>
                {
                    ["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa1"] = ("Assets/Scenes/Demo.unity", utf8NoBom.GetBytes("%YAML 1.1\n--- !u!1 &1000\n"), utf8NoBom.GetBytes(BuildDefaultMeta("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa1"))),
                    ["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa2"] = ("Assets/Materials/Wood.mat", utf8NoBom.GetBytes("%YAML 1.1\nMaterial:\n"), utf8NoBom.GetBytes(BuildDefaultMeta("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa2"))),
                    ["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa3"] = ("Assets/Meshes/Crate.obj", utf8NoBom.GetBytes("o Crate\nv 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n"), utf8NoBom.GetBytes(BuildDefaultMeta("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa3"))),
                    ["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa4"] = ("Assets/Textures/Albedo.png", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4 }, utf8NoBom.GetBytes(BuildDefaultMeta("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa4"))),
                    ["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa5"] = ("Assets/Scripts/PlayerController.cs", utf8NoBom.GetBytes("public class PlayerController {}\n"), utf8NoBom.GetBytes(UnityProjectGuidUtility.BuildMonoScriptMetaContent("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa5"))),
                    ["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa6"] = ("Assets/Scripts/GameScripts.asmdef", utf8NoBom.GetBytes("{\"name\":\"GameScripts\"}\n"), utf8NoBom.GetBytes(UnityProjectGuidUtility.BuildAsmdefMetaContent("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa6")))
                };

                foreach (var kv in expectedAssets)
                {
                    var relFromAssets = kv.Value.Pathname["Assets/".Length..].Replace('/', Path.DirectorySeparatorChar);
                    var fullAssetPath = Path.Combine(assetsDir, relFromAssets);
                    File.WriteAllBytes(fullAssetPath, kv.Value.AssetBytes);
                    File.WriteAllBytes(fullAssetPath + ".meta", kv.Value.MetaBytes);
                }

                // Invalid / edge files to verify graceful skipping + warnings:
                // (a) File without .meta
                File.WriteAllText(Path.Combine(matsDir, "StrayNoMeta.txt"), "stray", utf8NoBom);
                // (b) File with corrupt non-32-hex GUID in .meta
                File.WriteAllText(Path.Combine(matsDir, "CorruptMeta.mat"), "corrupt", utf8NoBom);
                File.WriteAllText(Path.Combine(matsDir, "CorruptMeta.mat.meta"), "fileFormatVersion: 2\nguid: NOT_A_VALID_GUID\n", utf8NoBom);
                // (c) File with duplicate GUID colliding with Wood.mat
                File.WriteAllText(Path.Combine(matsDir, "ZDuplicate.mat"), "dup", utf8NoBom);
                File.WriteAllText(Path.Combine(matsDir, "ZDuplicate.mat.meta"), BuildDefaultMeta("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa2"), utf8NoBom);

                var outputPkg = Path.Combine(tempRoot, "ExportedDemo.unitypackage");
                var exporter = new UnityPackageExporterService();
                var result = exporter.ExportFromUnityProjectDirectory(new UnityPackageExportOptions
                {
                    SourceProjectOrAssetsDirectory = projectDir,
                    OutputPackagePath = outputPkg,
                    IncludeScripts = true,
                    OverwriteExisting = true
                });

                Assert.True(result.Success, result.ErrorMessage);
                Assert.True(File.Exists(outputPkg));
                Assert.Equal(5, result.FolderEntriesPacked);
                Assert.Equal(6, result.AssetEntriesPacked);
                Assert.Equal(11, result.TotalEntriesPacked);
                Assert.Equal(3, result.SkippedEntriesCount);
                Assert.Equal(3, result.Warnings.Count);
                Assert.True(result.TotalBytesWritten > 0);

                // Inspect via built-in InspectUnityPackage API
                var inspection = UnityPackageExporterService.InspectUnityPackage(outputPkg);
                Assert.True(inspection.IsValid, inspection.ErrorMessage);
                Assert.Equal(6, inspection.AssetCount);
                Assert.Equal(5, inspection.FolderCount);
                Assert.Equal(0, inspection.DuplicateGuidCount);
                Assert.Equal(0, inspection.InvalidEntryCount);
                Assert.DoesNotContain(inspection.Pathnames, p => p.Contains("ProjectSettings", StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(inspection.Pathnames, p => p.Contains("ExportManifest.json", StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(inspection.Pathnames, p => p.Contains("StrayNoMeta", StringComparison.OrdinalIgnoreCase));

                // Inspect via raw GZipStream + TarReader to verify byte-for-byte parity & folder spec (NO <guid>/asset!)
                var rawEntries = ReadRawTarGzEntries(outputPkg);
                var folderGuids = new[] { guidScenesFolder, guidMatsFolder, guidMeshesFolder, guidTexturesFolder, guidScriptsFolder };
                foreach (var fg in folderGuids)
                {
                    Assert.True(rawEntries.ContainsKey($"{fg}/pathname"), $"Missing folder pathname for {fg}");
                    Assert.True(rawEntries.ContainsKey($"{fg}/asset.meta"), $"Missing folder meta for {fg}");
                    Assert.False(rawEntries.ContainsKey($"{fg}/asset"), $"Folder {fg} must NOT contain an 'asset' entry!");

                    var folderPathname = Encoding.UTF8.GetString(rawEntries[$"{fg}/pathname"]);
                    Assert.StartsWith("Assets/", folderPathname, StringComparison.Ordinal);
                    Assert.False(folderPathname.EndsWith("/", StringComparison.Ordinal));
                    Assert.DoesNotContain('\\', folderPathname);
                }

                foreach (var (guid, expected) in expectedAssets)
                {
                    Assert.True(rawEntries.ContainsKey($"{guid}/pathname"));
                    Assert.True(rawEntries.ContainsKey($"{guid}/asset"));
                    Assert.True(rawEntries.ContainsKey($"{guid}/asset.meta"));

                    // Verify pathname has NO UTF-8 BOM and exact forward-slash path
                    var pathnameBytes = rawEntries[$"{guid}/pathname"];
                    Assert.False(pathnameBytes.Length >= 3 && pathnameBytes[0] == 0xEF && pathnameBytes[1] == 0xBB && pathnameBytes[2] == 0xBF);
                    Assert.Equal(expected.Pathname, Encoding.UTF8.GetString(pathnameBytes));
                    Assert.Equal(expected.AssetBytes, rawEntries[$"{guid}/asset"]);
                    Assert.Equal(expected.MetaBytes, rawEntries[$"{guid}/asset.meta"]);
                }
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void UnityPackageExporter_IncludeScriptsFalse_OverwriteFalse_AndDirectAssetsDirectory_WorkCleanly()
        {
            var tempRoot = CreateTempDirectory("block4_pkg_opts");
            try
            {
                var assetsOnlyDir = Path.Combine(tempRoot, "LooseAssets");
                var scriptsDir = Path.Combine(assetsOnlyDir, "Scripts");
                Directory.CreateDirectory(scriptsDir);

                File.WriteAllText(Path.Combine(assetsOnlyDir, "RootMat.mat"), "mat");
                File.WriteAllText(Path.Combine(assetsOnlyDir, "RootMat.mat.meta"), BuildDefaultMeta("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb1"));
                WriteFolderMeta(Path.Combine(assetsOnlyDir, "Scripts.meta"), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb2");
                File.WriteAllText(Path.Combine(scriptsDir, "Behavior.cs"), "class Behavior {}");
                File.WriteAllText(Path.Combine(scriptsDir, "Behavior.cs.meta"), UnityProjectGuidUtility.BuildMonoScriptMetaContent("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb3"));

                var outPkg = Path.Combine(tempRoot, "NoScripts.unitypackage");
                var svc = new UnityPackageExporterService();
                var res = svc.ExportFromUnityProjectDirectory(new UnityPackageExportOptions
                {
                    SourceProjectOrAssetsDirectory = assetsOnlyDir,
                    OutputPackagePath = outPkg,
                    IncludeScripts = false,
                    OverwriteExisting = true
                });

                Assert.True(res.Success, res.ErrorMessage);
                Assert.Equal(1, res.AssetEntriesPacked);
                Assert.Equal(0, res.FolderEntriesPacked);

                var inspected = UnityPackageExporterService.InspectUnityPackage(outPkg);
                Assert.True(inspected.IsValid);
                Assert.Single(inspected.Pathnames);
                Assert.Equal("Assets/RootMat.mat", inspected.Pathnames[0]);

                // OverwriteExisting = false must refuse to overwrite existing package
                var refuseRes = svc.ExportFromUnityProjectDirectory(new UnityPackageExportOptions
                {
                    SourceProjectOrAssetsDirectory = assetsOnlyDir,
                    OutputPackagePath = outPkg,
                    OverwriteExisting = false
                });
                Assert.False(refuseRes.Success);
                Assert.Contains("OverwriteExisting", refuseRes.ErrorMessage, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void UnityPackageInspector_DetectsCorruptArchive_TraversalPathname_AndDuplicateGuid()
        {
            var tempRoot = CreateTempDirectory("block4_pkg_invalid");
            try
            {
                // 1. Corrupt non-gzip file
                var corruptPath = Path.Combine(tempRoot, "corrupt.unitypackage");
                File.WriteAllBytes(corruptPath, Encoding.UTF8.GetBytes("not a valid gzip tar file"));
                var corruptResult = UnityPackageExporterService.InspectUnityPackage(corruptPath);
                Assert.False(corruptResult.IsValid);
                Assert.NotNull(corruptResult.ErrorMessage);

                // 2. Archive containing traversal pathname (../evil) and duplicate GUID entry
                var badPkgPath = Path.Combine(tempRoot, "bad.unitypackage");
                var guid = "ccccccccccccccccccccccccccccccc1";
                using (var fs = File.Create(badPkgPath))
                using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
                using (var tw = new TarWriter(gz, TarEntryFormat.Pax))
                {
                    WriteTarTextEntry(tw, $"{guid}/pathname", "Assets/../../evil.txt");
                    WriteTarTextEntry(tw, $"{guid}/pathname", "Assets/Duplicate.txt");
                    WriteTarTextEntry(tw, $"{guid}/asset.meta", BuildDefaultMeta(guid));
                }

                var badResult = UnityPackageExporterService.InspectUnityPackage(badPkgPath);
                Assert.False(badResult.IsValid);
                Assert.True(badResult.InvalidEntryCount > 0);
                Assert.True(badResult.DuplicateGuidCount > 0);
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        // =====================================================================
        // 2. Standalone Prefab Export + MonoScript Parity + .unitypackage (SCENE-03 & SCENE-13)
        // =====================================================================

        [Fact]
        public void ExportPrefabToUnityProject_WritesValidPrefabYamlMetaScriptsAndUnityPackage()
        {
            var tempRoot = CreateTempDirectory("block4_prefab_e2e");
            try
            {
                using var am = new AssetsManager();
                var sf = CreateSyntheticSerializedFile(am, "CAB-prefab-unit", "2022.3.40f1");

                var shader = CreateSyntheticShader(sf, 10, "Standard");
                var albedoTex = CreateSolidRgbaTexture2D(sf, 20, "T_Crate_Albedo", 170, 120, 70, 255);
                var mat = CreateSyntheticMaterial(sf, 30, "Mat_Crate", shader, texEnvs: new[] { ("_MainTex", albedoTex) });
                var mesh = CreateSyntheticBoxDataMesh(sf, 40, "SM_Crate");
                var monoScript = CreateSyntheticMonoScript(sf, 50, "Game.Props", "InteractableCrate", "Assembly-CSharp.dll");

                // Simulate prefab root having an external parent in a scene — prefab export MUST reset root m_Father to 0!
                var sceneParentGo = CreateSyntheticGameObject(sf, 90, "ExternalSceneContainer", true, 0);
                var sceneParentTr = CreateSyntheticTransform(sf, 91, sceneParentGo, null, new Vector3(99f, 0f, 0f));
                AttachComponents(sceneParentGo, sceneParentTr);

                var prefabRootGo = CreateSyntheticGameObject(sf, 100, "InteractiveCrate", true, 8);
                var prefabRootTr = CreateSyntheticTransform(sf, 101, prefabRootGo, sceneParentTr, new Vector3(1f, 2f, 3f));
                prefabRootTr.m_LocalRotation = new Quaternion(0f, 0.7071068f, 0f, 0.7071068f);
                prefabRootTr.m_LocalScale = new Vector3(2f, 2f, 2f);
                var rootMb = CreateSyntheticObject<MonoBehaviour>(sf, 102, ClassIDType.MonoBehaviour);
                rootMb.m_GameObject = CreatePPtr<GameObject>(sf, 0, prefabRootGo.m_PathID);
                rootMb.m_Enabled = 1;
                rootMb.m_Script = CreatePPtr<MonoScript>(sf, 0, monoScript.m_PathID);
                rootMb.m_Name = string.Empty;
                var boxCol = CreateSyntheticObject<AssetStudio.Object>(sf, 103, ClassIDType.BoxCollider);

                var lidChildGo = CreateSyntheticGameObject(sf, 110, "CrateLid", false, 8);
                var lidChildTr = CreateSyntheticTransform(sf, 111, lidChildGo, prefabRootTr, new Vector3(0f, 1f, 0f));
                var lidMf = CreateSyntheticMeshFilter(sf, 112, lidChildGo, 0, mesh.m_PathID);
                var lidMr = CreateSyntheticMeshRenderer(sf, 113, lidChildGo, true, new[] { CreatePPtr<Material>(sf, 0, mat.m_PathID) });
                AttachComponents(lidChildGo, lidChildTr, lidMf, lidMr);

                prefabRootTr.m_Children = new[] { CreatePPtr<Transform>(sf, 0, lidChildTr.m_PathID) };
                AttachComponents(prefabRootGo, prefabRootTr, rootMb, boxCol);

                // Prepare synthetic dump.cs so CSharpProjectExporterService emits matching Assets/Scripts/
                var dumpDir = Path.Combine(tempRoot, "DumpSource");
                Directory.CreateDirectory(dumpDir);
                var dumpCsPath = Path.Combine(dumpDir, "dump.cs");
                File.WriteAllText(dumpCsPath,
                    "// Image 0: Assembly-CSharp.dll - 0\n" +
                    "// Namespace: Game.Props\n" +
                    "public class InteractableCrate : MonoBehaviour // TypeDefIndex: 1\n" +
                    "{\n" +
                    "    public int capacity;\n" +
                    "}\n",
                    new UTF8Encoding(false));

                var projectOut = Path.Combine(tempRoot, "CratePrefabProject");
                var pkgOut = Path.Combine(tempRoot, "InteractiveCrate.unitypackage");
                var exporter = new UnityProjectExporterService();
                var scriptExporter = new CSharpProjectExporterService();

                var result = exporter.ExportPrefabToUnityProject(
                    prefabRootGo,
                    new UnityProjectExportOptions
                    {
                        OutputDirectory = projectOut,
                        ExportColliderAuxiliaryMeshes = true,
                        AlsoBuildUnityPackage = true,
                        UnityPackageOutputPath = pkgOut,
                        WriteManifest = true,
                        ColliderFieldProvider = col => ReferenceEquals(col, boxCol)
                            ? new OrderedDictionary
                            {
                                ["m_Enabled"] = true,
                                ["m_IsTrigger"] = false,
                                ["m_Center"] = new OrderedDictionary { ["x"] = 0f, ["y"] = 0.5f, ["z"] = 0f },
                                ["m_Size"] = new OrderedDictionary { ["x"] = 1f, ["y"] = 1f, ["z"] = 1f }
                            }
                            : null,
                        IncludeScriptsCallback = (scriptsDir, ct) =>
                        {
                            var sr = scriptExporter.Export(new CSharpProjectExportOptions
                            {
                                OutputDirectory = scriptsDir,
                                ProjectName = "InteractiveCrate",
                                LayoutMode = CSharpProjectLayoutMode.UnityAssetsScripts,
                                DumpCsPath = dumpCsPath,
                                GenerateUnityMetaFiles = true,
                                GenerateAsmdefFiles = true
                            }, cancellationToken: ct);
                            return new UnityProjectScriptExportSummary
                            {
                                ExportedScriptFiles = sr.TotalTypesExported,
                                ExportedAssemblies = sr.GameAssembliesCount,
                                OutputDirectory = scriptsDir
                            };
                        }
                    });

                Assert.True(result.Success, result.ErrorMessage);
                Assert.NotNull(result.PrefabFilePath);
                Assert.True(File.Exists(result.PrefabFilePath!));
                Assert.True(File.Exists(result.PrefabFilePath! + ".meta"));
                Assert.True(File.Exists(Path.Combine(projectOut, "Assets", "Prefabs.meta")));
                Assert.Equal(Path.GetFullPath(pkgOut), result.UnityPackagePath);
                Assert.True(File.Exists(pkgOut));

                // Validate .prefab YAML 1.1 contents (NO scene settings; root m_Father: {fileID: 0}; child preserved!)
                var prefabText = File.ReadAllText(result.PrefabFilePath!);
                Assert.StartsWith("%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n", prefabText);
                Assert.DoesNotContain("OcclusionCullingSettings:", prefabText);
                Assert.DoesNotContain("RenderSettings:", prefabText);
                Assert.DoesNotContain("LightmapSettings:", prefabText);
                Assert.DoesNotContain("NavMeshSettings:", prefabText);
                Assert.Contains("m_Name: InteractiveCrate", prefabText);
                Assert.Contains("m_Name: CrateLid", prefabText);
                Assert.Contains("m_IsActive: 0", prefabText);
                Assert.Contains("m_LocalScale: {x: 2, y: 2, z: 2}", prefabText);
                Assert.Contains("m_Father: {fileID: 0}", prefabText);

                // Validate .prefab.meta uses PrefabImporter and exact deterministic GUID
                var prefabMetaText = File.ReadAllText(result.PrefabFilePath! + ".meta");
                var expectedPrefabGuid = UnityProjectExporterService.ComputeUnityAssetGuid("prefab", "CAB-prefab-unit", prefabRootGo.m_PathID, "InteractiveCrate");
                Assert.Contains("PrefabImporter:", prefabMetaText);
                Assert.Contains($"guid: {expectedPrefabGuid}", prefabMetaText);

                // Verify bit-for-bit MonoScript GUID parity between .prefab and CSharpProjectExporterService .cs.meta!
                var expectedScriptGuid = UnityProjectGuidUtility.ComputeScriptGuid("Assembly-CSharp.dll", "Game.Props", "InteractableCrate");
                Assert.Contains($"m_Script: {{fileID: 11500000, guid: {expectedScriptGuid}, type: 3}}", prefabText);
                var scriptMetaFiles = Directory.GetFiles(Path.Combine(projectOut, "Assets", "Scripts"), "InteractableCrate.cs.meta", SearchOption.AllDirectories);
                Assert.Single(scriptMetaFiles);
                Assert.True(UnityPackageExporterService.TryExtractGuidFromMetaFile(scriptMetaFiles[0], out var exportedScriptGuid));
                Assert.Equal(expectedScriptGuid, exportedScriptGuid);

                // Verify auxiliary collider OBJ was exported under Assets/Colliders/ with matching internalIDToNameTable
                var colliderObjPath = Path.Combine(projectOut, "Assets", "Colliders", "InteractiveCrate_Collider_Box.obj");
                Assert.True(File.Exists(colliderObjPath));
                Assert.True(File.Exists(colliderObjPath + ".meta"));
                Assert.Contains("g InteractiveCrate_Collider_Box", File.ReadAllText(colliderObjPath));
                var colliderMetaText = File.ReadAllText(colliderObjPath + ".meta");
                Assert.Contains("43: 4300000", colliderMetaText);
                Assert.Contains("second: InteractiveCrate_Collider_Box", colliderMetaText);
                Assert.NotNull(result.Manifest);
                Assert.Equal(1, result.Manifest!.Statistics.ColliderMeshesExported);

                // Verify mesh OBJ meta also maps fileID 4300000 via internalIDToNameTable (Unity 6000.x + 2019-2022)
                var crateMeshMeta = File.ReadAllText(Path.Combine(projectOut, "Assets", "Meshes", "SM_Crate.obj.meta"));
                Assert.Contains("43: 4300000", crateMeshMeta);
                Assert.Contains("second: SM_Crate", crateMeshMeta);

                // Verify the generated .unitypackage uses classic POSIX USTAR format (zero PAX headers)
                using (var pkgFs = File.OpenRead(pkgOut))
                using (var pkgGz = new GZipStream(pkgFs, CompressionMode.Decompress))
                using (var pkgTar = new TarReader(pkgGz))
                {
                    int entryCount = 0;
                    while (pkgTar.GetNextEntry(copyData: false) is { } e)
                    {
                        Assert.Equal(TarEntryFormat.Ustar, e.Format);
                        entryCount++;
                    }
                    Assert.True(entryCount > 0);
                }

                // Verify the generated .unitypackage contains the prefab, mesh, material, texture, collider OBJ, and C# script!
                var pkgInspection = UnityPackageExporterService.InspectUnityPackage(pkgOut);
                Assert.True(pkgInspection.IsValid, pkgInspection.ErrorMessage);
                Assert.Contains("Assets/Prefabs/InteractiveCrate.prefab", pkgInspection.Pathnames);
                Assert.Contains("Assets/Materials/Mat_Crate.mat", pkgInspection.Pathnames);
                Assert.Contains("Assets/Meshes/SM_Crate.obj", pkgInspection.Pathnames);
                Assert.Contains("Assets/Textures/T_Crate_Albedo.png", pkgInspection.Pathnames);
                Assert.Contains("Assets/Colliders/InteractiveCrate_Collider_Box.obj", pkgInspection.Pathnames);
                Assert.Contains(pkgInspection.Pathnames, p => p.EndsWith("InteractableCrate.cs", StringComparison.Ordinal));
                Assert.Contains(expectedScriptGuid, pkgInspection.Guids);
                Assert.Contains(expectedPrefabGuid, pkgInspection.Guids);
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        // =====================================================================
        // 3. ColliderMeshConverter (MESH-10)
        // =====================================================================

        [Fact]
        public void ColliderMeshConverter_BoxSphereCapsuleAllDirectionsAndMeshCollider_ProduceValidGeometryAndObj()
        {
            var tempRoot = CreateTempDirectory("block4_colliders");
            try
            {
                using var am = new AssetsManager();
                var sf = CreateSyntheticSerializedFile(am, "CAB-colliders", "2022.3.40f1");

                // 1) BoxCollider via fieldProvider -> exact 24 vertices / 12 triangles + outward winding + OBJ
                var boxObj = CreateSyntheticObject<AssetStudio.Object>(sf, 10, ClassIDType.BoxCollider);
                var boxGeo = ColliderMeshConverter.TryBuildColliderMesh(
                    boxObj,
                    "Crate",
                    fieldProvider: _ => new OrderedDictionary
                    {
                        ["m_Center"] = new OrderedDictionary { ["x"] = 1f, ["y"] = 2f, ["z"] = -3f },
                        ["m_Size"] = new OrderedDictionary { ["x"] = 4f, ["y"] = 6f, ["z"] = 2f },
                        ["m_IsTrigger"] = true
                    });

                Assert.NotNull(boxGeo);
                Assert.Equal(ColliderKind.Box, boxGeo!.ColliderKind);
                Assert.True(boxGeo.IsTrigger);
                Assert.Equal(24, boxGeo.VertexCount);
                Assert.Equal(12, boxGeo.TriangleCount);
                AssertFiniteOutwardGeometry(boxGeo);
                AssertBounds(boxGeo, new Vector3(-1f, -1f, -4f), new Vector3(3f, 5f, -2f), 1e-4f);

                var boxObjPath = Path.Combine(tempRoot, "Crate_Box.obj");
                ColliderMeshConverter.WriteObj(boxGeo, boxObjPath);
                var boxObjText = File.ReadAllText(boxObjPath);
                Assert.Equal(24, CountLinesStartingWith(boxObjText, "v "));
                Assert.Equal(24, CountLinesStartingWith(boxObjText, "vn "));
                Assert.Equal(12, CountLinesStartingWith(boxObjText, "f "));
                Assert.DoesNotContain("NaN", boxObjText, StringComparison.OrdinalIgnoreCase);

                // 2) SphereCollider via fieldProvider
                var sphereObj = CreateSyntheticObject<AssetStudio.Object>(sf, 11, ClassIDType.SphereCollider);
                var sphereGeo = ColliderMeshConverter.TryBuildColliderMesh(
                    sphereObj,
                    "Orb",
                    fieldProvider: _ => new OrderedDictionary
                    {
                        ["m_Center"] = new OrderedDictionary { ["x"] = 2f, ["y"] = -1f, ["z"] = 0.5f },
                        ["m_Radius"] = 2.5f,
                        ["m_IsTrigger"] = false
                    });

                Assert.NotNull(sphereGeo);
                Assert.Equal(ColliderKind.Sphere, sphereGeo!.ColliderKind);
                Assert.False(sphereGeo.IsTrigger);
                AssertFiniteOutwardGeometry(sphereGeo);
                foreach (var p in sphereGeo.Positions)
                {
                    var dist = (p - new Vector3(2f, -1f, 0.5f)).Length();
                    Assert.Equal(2.5f, dist, 3);
                }

                // 3) CapsuleCollider for all 3 directions: 0=X, 1=Y, 2=Z
                var capObj = CreateSyntheticObject<AssetStudio.Object>(sf, 12, ClassIDType.CapsuleCollider);
                for (int dir = 0; dir <= 2; dir++)
                {
                    int capturedDir = dir;
                    var capGeo = ColliderMeshConverter.TryBuildColliderMesh(
                        capObj,
                        $"Pillar_{dir}",
                        fieldProvider: _ => new OrderedDictionary
                        {
                            ["m_Center"] = new OrderedDictionary { ["x"] = 0f, ["y"] = 0f, ["z"] = 0f },
                            ["m_Radius"] = 0.5f,
                            ["m_Height"] = 4f,
                            ["m_Direction"] = capturedDir,
                            ["m_IsTrigger"] = false
                        });

                    Assert.NotNull(capGeo);
                    Assert.Equal(ColliderKind.Capsule, capGeo!.ColliderKind);
                    AssertFiniteOutwardGeometry(capGeo);

                    var expectedMax = new Vector3(
                        dir == 0 ? 2f : 0.5f,
                        dir == 1 ? 2f : 0.5f,
                        dir == 2 ? 2f : 0.5f);
                    AssertBounds(capGeo, -expectedMax, expectedMax, 0.05f);
                }

                // 4) MeshCollider (without source m_Normals to verify auto normal synthesis + meshResolver!)
                var colMesh = CreateSyntheticBoxDataMesh(sf, 40, "SM_Ramp", includeNormals: false);
                var meshColObj = CreateSyntheticObject<AssetStudio.Object>(sf, 13, ClassIDType.MeshCollider);
                var meshColGeo = ColliderMeshConverter.TryBuildColliderMesh(
                    meshColObj,
                    "Ramp",
                    fieldProvider: _ => new OrderedDictionary
                    {
                        ["m_IsTrigger"] = false,
                        ["m_Mesh"] = new OrderedDictionary { ["m_FileID"] = 0, ["m_PathID"] = colMesh.m_PathID }
                    });

                Assert.NotNull(meshColGeo);
                Assert.Equal(ColliderKind.Mesh, meshColGeo!.ColliderKind);
                Assert.Equal(colMesh.m_VertexCount, meshColGeo.VertexCount);
                Assert.Equal(colMesh.m_Indices.Count / 3, meshColGeo.TriangleCount);
                Assert.All(meshColGeo.Positions, p => Assert.True(float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z)));
                Assert.All(meshColGeo.Normals, n => Assert.InRange(n.Length(), 0.99f, 1.01f));

                // 5) Binary fallback (no fieldProvider, no TypeTree) for BoxCollider (Unity 2022.3 layout)
                byte[] binaryBoxPayload;
                using (var ms = new MemoryStream())
                using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
                {
                    bw.Write(0); bw.Write(100L); // m_GameObject
                    bw.Write(0); bw.Write(0L);   // m_Material
                    bw.Write(0); bw.Write(0); bw.Write(0); // 2022.2+ layers
                    bw.Write((byte)1); // m_IsTrigger = true
                    bw.Write((byte)0); // m_ProvidesContacts
                    bw.Write((byte)0); bw.Write((byte)0); // align4
                    bw.Write((byte)1); bw.Write(new byte[3]); // m_Enabled + align4
                    bw.Write(3f); bw.Write(4f); bw.Write(5f); // m_Size
                    bw.Write(0.5f); bw.Write(1.5f); bw.Write(-0.5f); // m_Center
                    binaryBoxPayload = ms.ToArray();
                }

                var binBoxObj = CreateRawObjectWithPayload(sf, ClassIDType.BoxCollider, 20, binaryBoxPayload);
                var binBoxGeo = ColliderMeshConverter.TryBuildColliderMesh(binBoxObj, "BinaryBox");
                Assert.NotNull(binBoxGeo);
                Assert.True(binBoxGeo!.IsTrigger);
                Assert.Equal(24, binBoxGeo.VertexCount);
                AssertBounds(binBoxGeo, new Vector3(-1f, -0.5f, -3f), new Vector3(2f, 3.5f, 2f), 1e-4f);

                // 5b) Binary fallback for Unity 2020.3 packed-bool layout (52B BoxCollider, 44B SphereCollider, 52B CapsuleCollider)
                //     AND disposed per-object reader recovering via SerializedFile.reader.Clone()!
                var sf2020 = CreateSyntheticSerializedFile(am, "level1_2020", "2020.3.48f1");
                byte[] box2020Bytes;
                using (var ms = new MemoryStream())
                using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
                {
                    bw.Write(0); bw.Write(100L); // m_GameObject (12B)
                    bw.Write(0); bw.Write(0L);   // m_Material (12B)
                    bw.Write((byte)1); bw.Write((byte)1); bw.Write((byte)0); bw.Write((byte)0); // m_IsTrigger=1, m_Enabled=1, align4 (4B)
                    bw.Write(2f); bw.Write(4f); bw.Write(6f); // m_Size (12B)
                    bw.Write(1f); bw.Write(0f); bw.Write(-1f); // m_Center (12B)
                    box2020Bytes = ms.ToArray();
                }
                Assert.Equal(52, box2020Bytes.Length);
                sf2020.reader = new FileReader("level1_2020", new MemoryStream(box2020Bytes, writable: false)) { Endian = EndianType.LittleEndian };
                var closedLocalStream = new MemoryStream(box2020Bytes, writable: false);
                var closedFileReader = new FileReader("level1_2020", closedLocalStream) { Endian = EndianType.LittleEndian };
                var objInfo2020 = new ObjectInfo
                {
                    byteStart = 0,
                    byteSize = (uint)box2020Bytes.Length,
                    typeID = (int)ClassIDType.BoxCollider,
                    classID = (int)ClassIDType.BoxCollider,
                    m_PathID = 5547
                };
                sf2020.ObjectInfoDic[5547] = objInfo2020;
                var obj2020 = new AssetStudio.Object(new ObjectReader(closedFileReader, sf2020, objInfo2020));
                closedFileReader.Dispose(); // Simulate MaterializeSceneObjectsForFile disposing localReader!

                var bin2020Geo = ColliderMeshConverter.TryBuildColliderMesh(obj2020, "Box2020DisposedReader");
                Assert.NotNull(bin2020Geo);
                Assert.True(bin2020Geo!.IsTrigger);
                Assert.Equal(24, bin2020Geo.VertexCount);
                AssertBounds(bin2020Geo, new Vector3(0f, -2f, -4f), new Vector3(2f, 2f, 2f), 1e-4f);

                // 6) Graceful failure on invalid/NaN/zero/truncated colliders (MUST return null, never throw!)
                Assert.Null(ColliderMeshConverter.TryBuildColliderMesh(null, "Null"));
                Assert.Null(ColliderMeshConverter.BuildBoxMesh("Zero", Vector3.Zero, new Vector3(0f, 1f, 1f)));
                Assert.Null(ColliderMeshConverter.BuildBoxMesh("NaN", Vector3.Zero, new Vector3(float.NaN, 1f, 1f)));
                Assert.Null(ColliderMeshConverter.BuildSphereMesh("ZeroSphere", Vector3.Zero, 0f));
                Assert.Null(ColliderMeshConverter.BuildCapsuleMesh("NegCapsule", Vector3.Zero, 0.5f, -2f));
                var truncatedBox = CreateRawObjectWithPayload(sf, ClassIDType.BoxCollider, 21, new byte[8]);
                Assert.Null(ColliderMeshConverter.TryBuildColliderMesh(truncatedBox, "Truncated"));
                var missingMeshCol = CreateSyntheticObject<AssetStudio.Object>(sf, 22, ClassIDType.MeshCollider);
                Assert.Null(ColliderMeshConverter.TryBuildColliderMesh(
                    missingMeshCol,
                    "MissingMesh",
                    fieldProvider: _ => new OrderedDictionary
                    {
                        ["m_Mesh"] = new OrderedDictionary { ["m_FileID"] = 0, ["m_PathID"] = 999999L }
                    }));
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        // =====================================================================
        // 4. LODGroup Filtering (TargetLodLevel) & Scene Collider Auxiliary Meshes (MESH-11 & MESH-10)
        // =====================================================================

        [Fact]
        public void UnityProjectExporter_LodGroupFiltering_TargetLod0SkipsLowerLodsAndKeepsSharedAndNonLodRenderers()
        {
            var tempRoot = CreateTempDirectory("block4_lod_filter");
            try
            {
                using var am = new AssetsManager();
                var sf = CreateSyntheticSerializedFile(am, "level0", "2022.3.40f1");

                var shader = CreateSyntheticShader(sf, 10, "Standard");
                var mat = CreateSyntheticMaterial(sf, 20, "Mat_Prop", shader);

                var meshLod0 = CreateSyntheticBoxDataMesh(sf, 30, "SM_Tree_LOD0");
                var meshLod1 = CreateSyntheticBoxDataMesh(sf, 31, "SM_Tree_LOD1");
                var meshLod2 = CreateSyntheticBoxDataMesh(sf, 32, "SM_Tree_LOD2");
                var meshShared = CreateSyntheticBoxDataMesh(sf, 33, "SM_Tree_TrunkShared");
                var meshNonLod = CreateSyntheticBoxDataMesh(sf, 34, "SM_StandaloneRock");

                var rootGo = CreateSyntheticGameObject(sf, 100, "ForestRoot", true, 0);
                var rootTr = CreateSyntheticTransform(sf, 101, rootGo, null, Vector3.Zero);

                // Tree node with LODGroup
                var treeGo = CreateSyntheticGameObject(sf, 110, "OakTree", true, 0);
                var treeTr = CreateSyntheticTransform(sf, 111, treeGo, rootTr, Vector3.Zero);

                var lod0Go = CreateSyntheticGameObject(sf, 120, "OakTree_LOD0", true, 0);
                var lod0Tr = CreateSyntheticTransform(sf, 121, lod0Go, treeTr, Vector3.Zero);
                var lod0Mf = CreateSyntheticMeshFilter(sf, 122, lod0Go, 0, meshLod0.m_PathID);
                var lod0Mr = CreateSyntheticMeshRenderer(sf, 123, lod0Go, true, new[] { CreatePPtr<Material>(sf, 0, mat.m_PathID) });
                AttachComponents(lod0Go, lod0Tr, lod0Mf, lod0Mr);

                var lod1Go = CreateSyntheticGameObject(sf, 130, "OakTree_LOD1", true, 0);
                var lod1Tr = CreateSyntheticTransform(sf, 131, lod1Go, treeTr, Vector3.Zero);
                var lod1Mf = CreateSyntheticMeshFilter(sf, 132, lod1Go, 0, meshLod1.m_PathID);
                var lod1Mr = CreateSyntheticMeshRenderer(sf, 133, lod1Go, true, new[] { CreatePPtr<Material>(sf, 0, mat.m_PathID) });
                AttachComponents(lod1Go, lod1Tr, lod1Mf, lod1Mr);

                var lod2Go = CreateSyntheticGameObject(sf, 140, "OakTree_LOD2", true, 0);
                var lod2Tr = CreateSyntheticTransform(sf, 141, lod2Go, treeTr, Vector3.Zero);
                var lod2Mf = CreateSyntheticMeshFilter(sf, 142, lod2Go, 0, meshLod2.m_PathID);
                var lod2Mr = CreateSyntheticMeshRenderer(sf, 143, lod2Go, true, new[] { CreatePPtr<Material>(sf, 0, mat.m_PathID) });
                AttachComponents(lod2Go, lod2Tr, lod2Mf, lod2Mr);

                // Shared renderer referenced by BOTH LOD0 and LOD1 -> must survive LOD0 filtering!
                var sharedGo = CreateSyntheticGameObject(sf, 150, "OakTree_SharedTrunk", true, 0);
                var sharedTr = CreateSyntheticTransform(sf, 151, sharedGo, treeTr, Vector3.Zero);
                var sharedMf = CreateSyntheticMeshFilter(sf, 152, sharedGo, 0, meshShared.m_PathID);
                var sharedMr = CreateSyntheticMeshRenderer(sf, 153, sharedGo, true, new[] { CreatePPtr<Material>(sf, 0, mat.m_PathID) });
                AttachComponents(sharedGo, sharedTr, sharedMf, sharedMr);

                var lodGroup = CreateSyntheticObject<LODGroup>(sf, 112, ClassIDType.LODGroup);
                lodGroup.m_GameObject = CreatePPtr<GameObject>(sf, 0, treeGo.m_PathID);
                lodGroup.m_Enabled = true;
                lodGroup.m_Size = 8f;
                lodGroup.m_LODs = new[]
                {
                    new LOD
                    {
                        screenRelativeHeight = 0.6f,
                        renderers = new[] { CreatePPtr<Renderer>(sf, 0, lod0Mr.m_PathID), CreatePPtr<Renderer>(sf, 0, sharedMr.m_PathID) }
                    },
                    new LOD
                    {
                        screenRelativeHeight = 0.25f,
                        renderers = new[] { CreatePPtr<Renderer>(sf, 0, lod1Mr.m_PathID), CreatePPtr<Renderer>(sf, 0, sharedMr.m_PathID) }
                    },
                    new LOD
                    {
                        screenRelativeHeight = 0.05f,
                        renderers = new[] { CreatePPtr<Renderer>(sf, 0, lod2Mr.m_PathID) }
                    }
                };
                treeTr.m_Children = new[]
                {
                    CreatePPtr<Transform>(sf, 0, lod0Tr.m_PathID),
                    CreatePPtr<Transform>(sf, 0, lod1Tr.m_PathID),
                    CreatePPtr<Transform>(sf, 0, lod2Tr.m_PathID),
                    CreatePPtr<Transform>(sf, 0, sharedTr.m_PathID)
                };
                AttachComponents(treeGo, treeTr, lodGroup);

                // Non-LOD rock with SphereCollider
                var rockGo = CreateSyntheticGameObject(sf, 160, "StandaloneRock", true, 0);
                var rockTr = CreateSyntheticTransform(sf, 161, rockGo, rootTr, new Vector3(5f, 0f, 0f));
                var rockMf = CreateSyntheticMeshFilter(sf, 162, rockGo, 0, meshNonLod.m_PathID);
                var rockMr = CreateSyntheticMeshRenderer(sf, 163, rockGo, true, new[] { CreatePPtr<Material>(sf, 0, mat.m_PathID) });
                var rockCol = CreateSyntheticObject<AssetStudio.Object>(sf, 164, ClassIDType.SphereCollider);
                AttachComponents(rockGo, rockTr, rockMf, rockMr, rockCol);

                rootTr.m_Children = new[]
                {
                    CreatePPtr<Transform>(sf, 0, treeTr.m_PathID),
                    CreatePPtr<Transform>(sf, 0, rockTr.m_PathID)
                };
                AttachComponents(rootGo, rootTr);

                var exporter = new UnityProjectExporterService();

                // Run A: TargetLodLevel = null (All LODs exported)
                var allLodsDir = Path.Combine(tempRoot, "AllLods");
                var allRes = exporter.ExportSceneToUnityProject(
                    new[] { rootGo },
                    new UnityProjectExportOptions
                    {
                        OutputDirectory = allLodsDir,
                        SceneName = "ForestAll",
                        TargetLodLevel = null,
                        WriteManifest = true
                    });

                Assert.True(allRes.Success, allRes.ErrorMessage);
                Assert.Equal(5, allRes.Manifest!.Statistics.MeshRenderers);
                Assert.Equal(5, allRes.Manifest.Statistics.UniqueMeshes);
                Assert.Equal(1, allRes.Manifest.Statistics.LODGroups);
                Assert.Equal(0, allRes.Manifest.Statistics.LodGroupsFiltered);
                Assert.Equal(0, allRes.Manifest.Statistics.RenderersSkippedByLodFilter);

                // Run B: TargetLodLevel = 0 (LOD0 only + ExportColliderAuxiliaryMeshes = true)
                var lod0Dir = Path.Combine(tempRoot, "Lod0Only");
                var lod0Res = exporter.ExportSceneToUnityProject(
                    new[] { rootGo },
                    new UnityProjectExportOptions
                    {
                        OutputDirectory = lod0Dir,
                        SceneName = "ForestLod0",
                        TargetLodLevel = 0,
                        ExportColliderAuxiliaryMeshes = true,
                        WriteManifest = true,
                        ColliderFieldProvider = col => ReferenceEquals(col, rockCol)
                            ? new OrderedDictionary
                            {
                                ["m_Center"] = new OrderedDictionary { ["x"] = 0f, ["y"] = 0f, ["z"] = 0f },
                                ["m_Radius"] = 1.25f,
                                ["m_IsTrigger"] = false
                            }
                            : null
                    });

                Assert.True(lod0Res.Success, lod0Res.ErrorMessage);
                Assert.Equal(3, lod0Res.Manifest!.Statistics.MeshRenderers); // LOD0 + Shared + StandaloneRock
                Assert.Equal(3, lod0Res.Manifest.Statistics.UniqueMeshes);
                Assert.Equal(1, lod0Res.Manifest.Statistics.LODGroups);
                Assert.Equal(1, lod0Res.Manifest.Statistics.LodGroupsFiltered);
                Assert.Equal(2, lod0Res.Manifest.Statistics.RenderersSkippedByLodFilter); // LOD1 + LOD2
                Assert.Equal(1, lod0Res.Manifest.Statistics.ColliderMeshesExported);

                // Verify exact mesh files exported vs filtered
                Assert.True(File.Exists(Path.Combine(lod0Dir, "Assets", "Meshes", "SM_Tree_LOD0.obj")));
                Assert.True(File.Exists(Path.Combine(lod0Dir, "Assets", "Meshes", "SM_Tree_TrunkShared.obj")));
                Assert.True(File.Exists(Path.Combine(lod0Dir, "Assets", "Meshes", "SM_StandaloneRock.obj")));
                Assert.False(File.Exists(Path.Combine(lod0Dir, "Assets", "Meshes", "SM_Tree_LOD1.obj")));
                Assert.False(File.Exists(Path.Combine(lod0Dir, "Assets", "Meshes", "SM_Tree_LOD2.obj")));

                // Verify collider auxiliary mesh exported
                Assert.True(File.Exists(Path.Combine(lod0Dir, "Assets", "Colliders", "StandaloneRock_Collider_Sphere.obj")));
                Assert.True(File.Exists(Path.Combine(lod0Dir, "Assets", "Colliders", "StandaloneRock_Collider_Sphere.obj.meta")));
                Assert.True(File.Exists(Path.Combine(lod0Dir, "Assets", "Colliders.meta")));

                // Verify LODGroup metadata preserved in YAML
                var lod0SceneText = File.ReadAllText(lod0Res.SceneFilePath);
                Assert.Contains("LODGroup:", lod0SceneText);
                Assert.Contains("screenRelativeHeight: 0.6", lod0SceneText);
                Assert.Contains("screenRelativeHeight: 0.25", lod0SceneText);
                Assert.Contains("screenRelativeHeight: 0.05", lod0SceneText);

                // Run C: TargetLodLevel = 1 + AlsoBuildUnityPackage = true with default <OutputDirectory>.unitypackage path!
                var lod1Dir = Path.Combine(tempRoot, "ForestLod1Project");
                var lod1Res = exporter.ExportSceneToUnityProject(
                    new[] { rootGo },
                    new UnityProjectExportOptions
                    {
                        OutputDirectory = lod1Dir,
                        SceneName = "ForestLod1",
                        TargetLodLevel = 1,
                        AlsoBuildUnityPackage = true,
                        UnityPackageOutputPath = null, // defaults to <OutputDirectory>.unitypackage
                        WriteManifest = true
                    });

                Assert.True(lod1Res.Success, lod1Res.ErrorMessage);
                Assert.Equal(3, lod1Res.Manifest!.Statistics.MeshRenderers); // LOD1 + Shared + StandaloneRock
                Assert.Equal(2, lod1Res.Manifest.Statistics.RenderersSkippedByLodFilter); // LOD0 + LOD2
                Assert.True(File.Exists(Path.Combine(lod1Dir, "Assets", "Meshes", "SM_Tree_LOD1.obj")));
                Assert.False(File.Exists(Path.Combine(lod1Dir, "Assets", "Meshes", "SM_Tree_LOD0.obj")));
                Assert.False(File.Exists(Path.Combine(lod1Dir, "Assets", "Meshes", "SM_Tree_LOD2.obj")));

                var expectedDefaultPkg = Path.GetFullPath(lod1Dir) + ".unitypackage";
                Assert.Equal(expectedDefaultPkg, lod1Res.UnityPackagePath);
                Assert.True(File.Exists(expectedDefaultPkg));
                var defaultPkgInspect = UnityPackageExporterService.InspectUnityPackage(expectedDefaultPkg);
                Assert.True(defaultPkgInspect.IsValid, defaultPkgInspect.ErrorMessage);
                Assert.Contains("Assets/Scenes/ForestLod1.unity", defaultPkgInspect.Pathnames);
                Assert.Contains("Assets/Meshes/SM_Tree_LOD1.obj", defaultPkgInspect.Pathnames);
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void ColliderMeshConverter_BinaryFallbackPacked2021AndDegenerateCapsule_WorkSafely()
        {
            using var am = new AssetsManager();
            var sf2021 = CreateSyntheticSerializedFile(am, "CAB-col-2021", "2021.3.20f1");

            // Unity 2021.3 packed-bool SphereCollider binary layout (m_IsTrigger + m_Enabled + single align4)
            byte[] spherePayload;
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                bw.Write(0); bw.Write(100L); // m_GameObject
                bw.Write(0); bw.Write(0L);   // m_Material
                bw.Write((byte)1);           // m_IsTrigger = true
                bw.Write((byte)1);           // m_Enabled = true
                bw.Write((byte)0); bw.Write((byte)0); // align4
                bw.Write(3.5f);              // m_Radius
                bw.Write(1f); bw.Write(-2f); bw.Write(4f); // m_Center
                spherePayload = ms.ToArray();
            }

            var sphereObj = CreateRawObjectWithPayload(sf2021, ClassIDType.SphereCollider, 30, spherePayload);
            var sphereGeo = ColliderMeshConverter.TryBuildColliderMesh(sphereObj, "PackedSphere");
            Assert.NotNull(sphereGeo);
            Assert.Equal(ColliderKind.Sphere, sphereGeo!.ColliderKind);
            Assert.True(sphereGeo.IsTrigger);
            AssertFiniteOutwardGeometry(sphereGeo);
            AssertBounds(sphereGeo, new Vector3(-2.5f, -5.5f, 0.5f), new Vector3(4.5f, 1.5f, 7.5f), 0.1f);

            // Unity 2021.3 packed-bool CapsuleCollider binary layout
            byte[] capsulePayload;
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                bw.Write(0); bw.Write(100L); // m_GameObject
                bw.Write(0); bw.Write(0L);   // m_Material
                bw.Write((byte)0);           // m_IsTrigger = false
                bw.Write((byte)1);           // m_Enabled = true
                bw.Write((byte)0); bw.Write((byte)0); // align4
                bw.Write(1f);                // m_Radius
                bw.Write(1f);                // m_Height < 2*radius -> degenerates cleanly into a radius-1 sphere!
                bw.Write(2);                 // m_Direction = Z
                bw.Write(0f); bw.Write(0f); bw.Write(0f); // m_Center
                capsulePayload = ms.ToArray();
            }

            var capObj = CreateRawObjectWithPayload(sf2021, ClassIDType.CapsuleCollider, 31, capsulePayload);
            var capGeo = ColliderMeshConverter.TryBuildColliderMesh(capObj, "DegenerateCapsule");
            Assert.NotNull(capGeo);
            Assert.Equal(ColliderKind.Capsule, capGeo!.ColliderKind);
            AssertFiniteOutwardGeometry(capGeo);
            AssertBounds(capGeo, new Vector3(-1f, -1f, -1f), new Vector3(1f, 1f, 1f), 0.05f);

            // Unity 2021.3 packed-bool MeshCollider binary layout resolving custom mesh via meshResolver
            var targetMesh = CreateSyntheticBoxDataMesh(sf2021, 77, "SM_ColBinary");
            byte[] meshColPayload;
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                bw.Write(0); bw.Write(100L); // m_GameObject
                bw.Write(0); bw.Write(0L);   // m_Material
                bw.Write((byte)0); bw.Write((byte)1); bw.Write((byte)0); bw.Write((byte)0); // packed flags + align4
                bw.Write((byte)1); bw.Write(new byte[3]); // m_Convex + align4
                bw.Write(30);                // m_CookingOptions (2017.3+)
                bw.Write(0); bw.Write(77L);  // m_Mesh PPtr
                meshColPayload = ms.ToArray();
            }

            var meshColObj = CreateRawObjectWithPayload(sf2021, ClassIDType.MeshCollider, 32, meshColPayload);
            var resolvedByDelegate = false;
            var meshColGeo = ColliderMeshConverter.TryBuildColliderMesh(
                meshColObj,
                "BinaryMeshCol",
                meshResolver: pptr =>
                {
                    resolvedByDelegate = pptr.m_PathID == 77L;
                    return resolvedByDelegate ? targetMesh : null;
                });
            Assert.True(resolvedByDelegate);
            Assert.NotNull(meshColGeo);
            Assert.Equal(ColliderKind.Mesh, meshColGeo!.ColliderKind);
            Assert.Equal(4, meshColGeo.VertexCount);
        }

        // =====================================================================
        // 5. Pure-MVVM Audit (ZERO Avalonia references in Block 4 Services)
        // =====================================================================

        [Fact]
        public void Block4Services_ContainZeroAvaloniaUsings()
        {
            var repoRoot = FindRepoRoot();
            var serviceFiles = new[]
            {
                Path.Combine(repoRoot, "AssetStudio.Avalonia", "Services", "UnityPackageExporterService.cs"),
                Path.Combine(repoRoot, "AssetStudio.Avalonia", "Services", "ColliderMeshConverter.cs"),
                Path.Combine(repoRoot, "AssetStudio.Avalonia", "Services", "UnityProjectExporterService.cs")
            };

            foreach (var file in serviceFiles)
            {
                Assert.True(File.Exists(file), $"Expected service file not found: {file}");
                var content = File.ReadAllText(file);
                Assert.DoesNotContain("using Avalonia", content, StringComparison.Ordinal);
            }
        }

        // =====================================================================
        // Synthetic Fixture & Inspection Helpers
        // =====================================================================

        private static Dictionary<string, byte[]> ReadRawTarGzEntries(string packagePath)
        {
            var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            using var fs = File.OpenRead(packagePath);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            using var reader = new TarReader(gz);

            while (reader.GetNextEntry(copyData: false) is { } entry)
            {
                if (entry.EntryType == TarEntryType.Directory)
                {
                    continue;
                }

                using var ms = new MemoryStream();
                entry.DataStream?.CopyTo(ms);
                result[entry.Name] = ms.ToArray();
            }

            return result;
        }

        private static void WriteTarTextEntry(TarWriter writer, string name, string content)
        {
            var bytes = new UTF8Encoding(false).GetBytes(content);
            using var ms = new MemoryStream(bytes, writable: false);
            var entry = new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = ms };
            writer.WriteEntry(entry);
        }

        private static void WriteFolderMeta(string metaPath, string guid)
        {
            File.WriteAllText(metaPath, UnityProjectGuidUtility.BuildFolderMetaContent(guid), new UTF8Encoding(false));
        }

        private static string BuildDefaultMeta(string guid)
        {
            return $"fileFormatVersion: 2\nguid: {guid}\nDefaultImporter:\n  externalObjects: {{}}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n";
        }

        private static void AssertFiniteOutwardGeometry(ColliderMeshGeometry geo)
        {
            Assert.True(geo.VertexCount > 0);
            Assert.True(geo.TriangleCount > 0);
            Assert.Equal(geo.Positions.Length, geo.Normals.Length);
            Assert.Equal(0, geo.TriangleIndices.Length % 3);

            for (int i = 0; i < geo.Positions.Length; i++)
            {
                var p = geo.Positions[i];
                var n = geo.Normals[i];
                Assert.True(float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z));
                Assert.True(float.IsFinite(n.X) && float.IsFinite(n.Y) && float.IsFinite(n.Z));
                Assert.InRange(n.Length(), 0.99f, 1.01f);
            }

            for (int t = 0; t < geo.TriangleIndices.Length; t += 3)
            {
                int a = geo.TriangleIndices[t];
                int b = geo.TriangleIndices[t + 1];
                int c = geo.TriangleIndices[t + 2];
                Assert.InRange(a, 0, geo.VertexCount - 1);
                Assert.InRange(b, 0, geo.VertexCount - 1);
                Assert.InRange(c, 0, geo.VertexCount - 1);

                var e1 = geo.Positions[b] - geo.Positions[a];
                var e2 = geo.Positions[c] - geo.Positions[a];
                var cross = new Vector3(
                    e1.Y * e2.Z - e1.Z * e2.Y,
                    e1.Z * e2.X - e1.X * e2.Z,
                    e1.X * e2.Y - e1.Y * e2.X);
                var avgN = geo.Normals[a] + geo.Normals[b] + geo.Normals[c];
                float dot = cross.X * avgN.X + cross.Y * avgN.Y + cross.Z * avgN.Z;
                Assert.True(dot > 0f, $"Triangle {t / 3} has inverted or degenerate winding (dot={dot}).");
            }
        }

        private static void AssertBounds(ColliderMeshGeometry geo, Vector3 expectedMin, Vector3 expectedMax, float tolerance)
        {
            float minX = geo.Positions.Min(static p => p.X);
            float minY = geo.Positions.Min(static p => p.Y);
            float minZ = geo.Positions.Min(static p => p.Z);
            float maxX = geo.Positions.Max(static p => p.X);
            float maxY = geo.Positions.Max(static p => p.Y);
            float maxZ = geo.Positions.Max(static p => p.Z);

            Assert.InRange(minX, expectedMin.X - tolerance, expectedMin.X + tolerance);
            Assert.InRange(minY, expectedMin.Y - tolerance, expectedMin.Y + tolerance);
            Assert.InRange(minZ, expectedMin.Z - tolerance, expectedMin.Z + tolerance);
            Assert.InRange(maxX, expectedMax.X - tolerance, expectedMax.X + tolerance);
            Assert.InRange(maxY, expectedMax.Y - tolerance, expectedMax.Y + tolerance);
            Assert.InRange(maxZ, expectedMax.Z - tolerance, expectedMax.Z + tolerance);
        }

        private static int CountLinesStartingWith(string text, string prefix)
        {
            int count = 0;
            using var sr = new StringReader(text);
            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                if (line.StartsWith(prefix, StringComparison.Ordinal))
                {
                    count++;
                }
            }
            return count;
        }

        private static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "AssetStudio.sln")))
                {
                    return dir.FullName;
                }
                dir = dir.Parent;
            }
            throw new DirectoryNotFoundException("Could not locate AssetStudio.sln from test directory.");
        }

        private static SerializedFile CreateSyntheticSerializedFile(AssetsManager am, string fileName, string unityVersion)
        {
            var sf = (SerializedFile)RuntimeHelpers.GetUninitializedObject(typeof(SerializedFile));
            sf.assetsManager = am;
            sf.fileName = fileName;
            sf.fullName = fileName;
            sf.originalPath = fileName;
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

        private static AssetStudio.Object CreateRawObjectWithPayload(SerializedFile sf, ClassIDType classId, long pathId, byte[] payload)
        {
            var ms = new MemoryStream(payload);
            var fr = new FileReader(sf.fileName, ms) { Endian = EndianType.LittleEndian };
            var info = new ObjectInfo
            {
                m_PathID = pathId,
                byteStart = 0,
                byteSize = (uint)payload.Length,
                typeID = (int)classId,
                classID = (int)classId
            };
            var objReader = new ObjectReader(fr, sf, info);
            var obj = new AssetStudio.Object(objReader);
            sf.AddObject(obj);
            sf.ObjectInfoDic[pathId] = info;
            return obj;
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
            => new PPtr<T>(fileId, pathId, sf);

        private static GameObject CreateSyntheticGameObject(SerializedFile sf, long pathId, string name, bool isActive, int layer, ushort tag = 0)
        {
            var go = CreateSyntheticObject<GameObject>(sf, pathId, ClassIDType.GameObject);
            go.m_Name = name;
            go.m_IsActive = isActive;
            go.m_Layer = layer;
            go.m_Tag = tag;
            go.m_Components = Array.Empty<PPtr<Component>>();
            return go;
        }

        private static Transform CreateSyntheticTransform(SerializedFile sf, long pathId, GameObject go, Transform? father, Vector3 pos)
        {
            var tr = CreateSyntheticObject<Transform>(sf, pathId, ClassIDType.Transform);
            tr.m_GameObject = CreatePPtr<GameObject>(sf, 0, go.m_PathID);
            tr.m_LocalPosition = pos;
            tr.m_LocalRotation = new Quaternion(0f, 0f, 0f, 1f);
            tr.m_LocalScale = Vector3.One;
            tr.m_Father = father != null ? CreatePPtr<Transform>(sf, 0, father.m_PathID) : CreatePPtr<Transform>(sf, 0, 0);
            tr.m_Children = Array.Empty<PPtr<Transform>>();
            return tr;
        }

        private static MeshFilter CreateSyntheticMeshFilter(SerializedFile sf, long pathId, GameObject go, int fileId, long meshPathId)
        {
            var mf = CreateSyntheticObject<MeshFilter>(sf, pathId, ClassIDType.MeshFilter);
            mf.m_GameObject = CreatePPtr<GameObject>(sf, 0, go.m_PathID);
            mf.m_Mesh = CreatePPtr<Mesh>(sf, fileId, meshPathId);
            return mf;
        }

        private static MeshRenderer CreateSyntheticMeshRenderer(
            SerializedFile sf,
            long pathId,
            GameObject go,
            bool enabled,
            PPtr<Material>[] materials)
        {
            var mr = CreateSyntheticObject<MeshRenderer>(sf, pathId, ClassIDType.MeshRenderer);
            mr.m_GameObject = CreatePPtr<GameObject>(sf, 0, go.m_PathID);
            mr.m_Enabled = enabled;
            mr.m_CastShadows = Renderer.ShadowCastingModeOn;
            mr.m_ReceiveShadows = 1;
            mr.m_Materials = materials;
            mr.m_SubsetIndices = Array.Empty<uint>();
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
            var pixels = new byte[2 * 2 * 4];
            for (int i = 0; i < 4; i++)
            {
                pixels[i * 4 + 0] = r;
                pixels[i * 4 + 1] = g;
                pixels[i * 4 + 2] = b;
                pixels[i * 4 + 3] = a;
            }
            var tex = CreateSyntheticObject<Texture2D>(sf, pathId, ClassIDType.Texture2D);
            tex.m_Name = name;
            tex.m_Width = 2;
            tex.m_Height = 2;
            tex.m_TextureFormat = TextureFormat.RGBA32;
            tex.m_MipCount = 1;
            tex.m_ImageCount = 1;
            tex.m_TextureDimension = 2;
            tex.image_data = new ResourceReader(pixels);
            return tex;
        }

        private static Material CreateSyntheticMaterial(
            SerializedFile sf,
            long pathId,
            string name,
            Shader? shader = null,
            (string Key, Texture2D Texture)[]? texEnvs = null)
        {
            var mat = CreateSyntheticObject<Material>(sf, pathId, ClassIDType.Material);
            mat.m_Name = name;
            mat.m_Shader = shader != null ? CreatePPtr<Shader>(sf, 0, shader.m_PathID) : CreatePPtr<Shader>(sf, 0, 0);
            mat.m_Parent = CreatePPtr<Material>(sf, 0, 0);
            mat.m_CustomRenderQueue = 2000;
            mat.m_ValidKeywords = Array.Empty<string>();
            mat.m_InvalidKeywords = Array.Empty<string>();
            mat.disabledShaderPasses = Array.Empty<string>();
            mat.stringTagMap = Array.Empty<KeyValuePair<string, string>>();
            mat.m_SavedProperties = new UnityPropertySheet
            {
                m_TexEnvs = texEnvs != null
                    ? texEnvs.Select(t => new KeyValuePair<string, UnityTexEnv>(
                        t.Key,
                        new UnityTexEnv(CreatePPtr<Texture>(sf, 0, t.Texture.m_PathID), new Vector2(1f, 1f), Vector2.Zero))).ToArray()
                    : Array.Empty<KeyValuePair<string, UnityTexEnv>>(),
                m_Ints = Array.Empty<KeyValuePair<string, int>>(),
                m_Floats = Array.Empty<KeyValuePair<string, float>>(),
                m_Colors = Array.Empty<KeyValuePair<string, Color>>()
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

        private static Mesh CreateSyntheticBoxDataMesh(SerializedFile sf, long pathId, string name, bool includeNormals = true)
        {
            var mesh = CreateSyntheticObject<Mesh>(sf, pathId, ClassIDType.Mesh);
            mesh.m_Name = name;
            mesh.m_VertexCount = 4;
            mesh.m_Vertices = new float[]
            {
                0f, 0f, 0f,
                1f, 0f, 0f,
                1f, 1f, 0f,
                0f, 1f, 0f
            };
            mesh.m_Normals = includeNormals
                ? new float[]
                {
                    0f, 0f, -1f,
                    0f, 0f, -1f,
                    0f, 0f, -1f,
                    0f, 0f, -1f
                }
                : Array.Empty<float>();
            mesh.m_UV0 = new float[]
            {
                0f, 0f,
                1f, 0f,
                1f, 1f,
                0f, 1f
            };
            mesh.m_Indices = new List<uint> { 0, 1, 2, 0, 2, 3 };

            var sub0 = (SubMesh)RuntimeHelpers.GetUninitializedObject(typeof(SubMesh));
            sub0.firstByte = 0; sub0.indexCount = 6; sub0.topology = GfxPrimitiveType.Triangles; sub0.firstVertex = 0; sub0.vertexCount = 4;
            mesh.m_SubMeshes = new[] { sub0 };

            typeof(Mesh).GetField("m_DataProcessed", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(mesh, true);
            typeof(Mesh).GetField("m_ProcessLock", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(mesh, new object());
            return mesh;
        }

        private static void AttachComponents(GameObject go, params AssetStudio.Object[] components)
        {
            go.m_Components = components.Select(c => CreatePPtr<Component>(go.assetsFile, 0, c.m_PathID)).ToArray();
        }

        private static string CreateTempDirectory(string prefix)
        {
            var path = Path.Combine(Path.GetTempPath(), $"AssetStudio_{prefix}_{Guid.NewGuid():N}");
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
            }
        }
    }
}
