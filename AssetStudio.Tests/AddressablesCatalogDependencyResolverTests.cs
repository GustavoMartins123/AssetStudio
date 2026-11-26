#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using AssetStudio.Avalonia;
using AssetStudio.Avalonia.ViewModels;
using Xunit;

namespace AssetStudio.Tests
{
    public class AddressablesCatalogDependencyResolverTests
    {
        [Fact]
        public void CatalogJson_ResolvesOneHopTransitiveTwoHopCyclesMissingFilesAndPlaceholders()
        {
            var tempRoot = CreateTempDirectory("catalog_json_deps");
            try
            {
                var aaDir = Path.Combine(tempRoot, "Sample_Data", "StreamingAssets", "aa");
                var winDir = Path.Combine(aaDir, "StandaloneWindows64");
                Directory.CreateDirectory(winDir);

                var charBundle = Path.Combine(winDir, "char_hero.bundle");
                var matBundle = Path.Combine(winDir, "mat_hero.bundle");
                var texBundle = Path.Combine(winDir, "tex_hero.bundle");
                var shaderBundle = Path.Combine(winDir, "shader_common.bundle");

                File.WriteAllBytes(charBundle, BuildMinimalUnityFsBundle("CAB-11111111111111111111111111111111"));
                File.WriteAllBytes(matBundle, BuildMinimalUnityFsBundle("CAB-22222222222222222222222222222222"));
                File.WriteAllBytes(texBundle, BuildMinimalUnityFsBundle("CAB-33333333333333333333333333333333"));
                File.WriteAllBytes(shaderBundle, BuildMinimalUnityFsBundle("CAB-44444444444444444444444444444444"));
                // Intentionally do NOT create missing_optional.bundle on disk

                // Catalog dependency graph:
                //   char_hero.bundle -> mat_hero.bundle, missing_optional.bundle
                //   mat_hero.bundle  -> tex_hero.bundle, char_hero.bundle (cycle back to char_hero!)
                //   tex_hero.bundle  -> shader_common.bundle, mat_hero.bundle (cycle back to mat_hero!)
                var catalogPath = Path.Combine(aaDir, "catalog.json");
                var catalogJson = BuildAddressablesV1CatalogJson(
                    internalIds: new[]
                    {
                        "{UnityEngine.AddressableAssets.Addressables.RuntimePath}\\StandaloneWindows64\\char_hero.bundle",
                        "0#mat_hero.bundle",
                        "{UnityEngine.AddressableAssets.Addressables.RuntimePath}/StandaloneWindows64/tex_hero.bundle",
                        "{CustomRuntimePath}/StandaloneWindows64/shader_common.bundle",
                        "{UnityEngine.AddressableAssets.Addressables.RuntimePath}/StandaloneWindows64/missing_optional.bundle",
                        "Assets/Characters/Hero/Hero.prefab",
                        "Assets/Characters/Hero/Hero.mat",
                        "Assets/Characters/Hero/Hero_Albedo.png"
                    },
                    prefixes: new[]
                    {
                        "{UnityEngine.AddressableAssets.Addressables.RuntimePath}/StandaloneWindows64/"
                    },
                    buckets: new[]
                    {
                        // Bucket 0: dependencies for Hero.prefab (primary = char_hero [0], deps = mat_hero [1], missing_optional [4])
                        new[] { 0, 1, 4 },
                        // Bucket 1: dependencies for Hero.mat (primary = mat_hero [1], deps = tex_hero [2], char_hero [0])
                        new[] { 1, 2, 0 },
                        // Bucket 2: dependencies for Hero_Albedo.png (primary = tex_hero [2], deps = shader_common [3], mat_hero [1])
                        new[] { 2, 3, 1 }
                    },
                    entries: new (int internalIdIdx, int providerIdx, int depBucketIdx, string? bundleName)[]
                    {
                        (0, 0, -1, "11111111111111111111111111111111"),
                        (1, 0, -1, "22222222222222222222222222222222"),
                        (2, 0, -1, "33333333333333333333333333333333"),
                        (3, 0, -1, "44444444444444444444444444444444"),
                        (4, 0, -1, "55555555555555555555555555555555"),
                        (5, 1, 0, null),
                        (6, 1, 1, null),
                        (7, 1, 2, null)
                    });
                File.WriteAllText(catalogPath, catalogJson, Encoding.UTF8);

                AddressablesCatalogDependencyResolver.ClearCache();

                // 1. Full transitive resolution from char_hero.bundle
                var resolved = AddressablesCatalogDependencyResolver.ResolveDependencies(new[] { charBundle });
                Assert.Equal(3, resolved.Count);
                Assert.Equal(Path.GetFullPath(matBundle), resolved[0], ignoreCase: true);
                Assert.Equal(Path.GetFullPath(texBundle), resolved[1], ignoreCase: true);
                Assert.Equal(Path.GetFullPath(shaderBundle), resolved[2], ignoreCase: true);
                Assert.DoesNotContain(resolved, p => string.Equals(p, Path.GetFullPath(charBundle), StringComparison.OrdinalIgnoreCase));

                // 2. Safety cap enforcement (maxDependencies: 2 stops after 2 hops)
                var capped = AddressablesCatalogDependencyResolver.ResolveDependencies(new[] { charBundle }, maxDependencies: 2);
                Assert.Equal(2, capped.Count);
                Assert.Equal(Path.GetFullPath(matBundle), capped[0], ignoreCase: true);
                Assert.Equal(Path.GetFullPath(texBundle), capped[1], ignoreCase: true);
            }
            finally
            {
                AddressablesCatalogDependencyResolver.ClearCache();
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void CatalogBin_AddressablesV2BinaryFormat_ResolvesDynamicStringsAndTransitiveDependencies()
        {
            var tempRoot = CreateTempDirectory("catalog_bin_deps");
            try
            {
                var aaDir = Path.Combine(tempRoot, "Sample_Data", "StreamingAssets", "aa");
                var winDir = Path.Combine(aaDir, "StandaloneWindows64");
                Directory.CreateDirectory(winDir);

                var modelBundle = Path.Combine(winDir, "model_unit.bundle");
                var rigBundle = Path.Combine(winDir, "rig_unit.bundle");
                var sharedTexBundle = Path.Combine(winDir, "shared_textures.bundle");

                File.WriteAllBytes(modelBundle, BuildMinimalUnityFsBundle("CAB-aaaa1111aaaa1111aaaa1111aaaa1111"));
                File.WriteAllBytes(rigBundle, BuildMinimalUnityFsBundle("CAB-bbbb2222bbbb2222bbbb2222bbbb2222"));
                File.WriteAllBytes(sharedTexBundle, BuildMinimalUnityFsBundle("CAB-cccc3333cccc3333cccc3333cccc3333"));

                var binCatalogPath = Path.Combine(aaDir, "catalog.bin");
                var binBytes = BuildSyntheticAddressablesV2CatalogBin(
                    locatorId: "AddressablesMainContentCatalog",
                    bundles: new (string internalId, string bundleName, int[] depBundleIndices)[]
                    {
                        ("{UnityEngine.AddressableAssets.Addressables.RuntimePath}/StandaloneWindows64/model_unit.bundle", "aaaa1111aaaa1111aaaa1111aaaa1111", new[] { 1 }),
                        ("{UnityEngine.AddressableAssets.Addressables.RuntimePath}/StandaloneWindows64/rig_unit.bundle", "bbbb2222bbbb2222bbbb2222bbbb2222", new[] { 2, 0 }),
                        ("{UnityEngine.AddressableAssets.Addressables.RuntimePath}/StandaloneWindows64/shared_textures.bundle", "cccc3333cccc3333cccc3333cccc3333", Array.Empty<int>())
                    });
                File.WriteAllBytes(binCatalogPath, binBytes);

                AddressablesCatalogDependencyResolver.ClearCache();

                Assert.True(AddressablesCatalogDependencyResolver.TryParseBinaryCatalog(binCatalogPath, out var parsed));
                Assert.NotNull(parsed);
                Assert.Equal("AddressablesMainContentCatalog", parsed!.LocatorId);
                Assert.Equal(3, parsed.Bundles.Count);

                var resolved = AddressablesCatalogDependencyResolver.ResolveDependencies(new[] { modelBundle });
                Assert.Equal(2, resolved.Count);
                Assert.Equal(Path.GetFullPath(rigBundle), resolved[0], ignoreCase: true);
                Assert.Equal(Path.GetFullPath(sharedTexBundle), resolved[1], ignoreCase: true);
            }
            finally
            {
                AddressablesCatalogDependencyResolver.ClearCache();
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void AssetsManager_LoadFiles_AutoLoadsCatalogAndFallbackExternalDependencies_RespectingToggle()
        {
            var tempRoot = CreateTempDirectory("assets_manager_autoload");
            try
            {
                var aaDir = Path.Combine(tempRoot, "Game_Data", "StreamingAssets", "aa");
                var winDir = Path.Combine(aaDir, "StandaloneWindows64");
                Directory.CreateDirectory(winDir);

                const string primaryCab = "CAB-10101010101010101010101010101010";
                const string depCab = "CAB-20202020202020202020202020202020";
                const string fallbackCab = "CAB-30303030303030303030303030303030";

                var primaryBundlePath = Path.Combine(winDir, "primary_char.bundle");
                var catalogDepBundlePath = Path.Combine(winDir, "catalog_mat.bundle");
                var fallbackTexBundlePath = Path.Combine(winDir, "fallback_tex.bundle");

                // primary_char references catalog_mat (via catalog.json) and fallback_tex (only via m_Externals CAB-..., not in catalog dependency bucket!)
                File.WriteAllBytes(primaryBundlePath, BuildMinimalUnityFsBundle(
                    primaryCab,
                    externals: new[] { $"archive:/{depCab}/{depCab}", $"archive:/{fallbackCab}/{fallbackCab}" }));
                File.WriteAllBytes(catalogDepBundlePath, BuildMinimalUnityFsBundle(depCab));
                File.WriteAllBytes(fallbackTexBundlePath, BuildMinimalUnityFsBundle(fallbackCab));

                var catalogPath = Path.Combine(aaDir, "catalog.json");
                File.WriteAllText(catalogPath, BuildAddressablesV1CatalogJson(
                    internalIds: new[]
                    {
                        "{UnityEngine.AddressableAssets.Addressables.RuntimePath}/StandaloneWindows64/primary_char.bundle",
                        "{UnityEngine.AddressableAssets.Addressables.RuntimePath}/StandaloneWindows64/catalog_mat.bundle",
                        "Assets/Char/Primary.prefab"
                    },
                    prefixes: Array.Empty<string>(),
                    buckets: new[]
                    {
                        new[] { 0, 1 }
                    },
                    entries: new (int internalIdIdx, int providerIdx, int depBucketIdx, string? bundleName)[]
                    {
                        (0, 0, -1, "10101010101010101010101010101010"),
                        (1, 0, -1, "20202020202020202020202020202020"),
                        (2, 1, 0, null)
                    }), Encoding.UTF8);

                // 1. With AutoLoadCatalogDependencies = false: only primary_char.bundle is loaded
                using (var managerDisabled = new AssetsManager { AutoLoadCatalogDependencies = false })
                {
                    managerDisabled.LoadFiles(primaryBundlePath);
                    Assert.Single(managerDisabled.assetsFileList);
                    Assert.Equal(primaryCab, managerDisabled.assetsFileList[0].fileName);
                }

                // 2. With AutoLoadCatalogDependencies = true (default): primary_char + catalog_mat + fallback_tex are all loaded!
                using (var managerEnabled = new AssetsManager())
                {
                    Assert.True(managerEnabled.AutoLoadCatalogDependencies);
                    managerEnabled.LoadFiles(primaryBundlePath);

                    var loadedNames = managerEnabled.assetsFileList.Select(f => f.fileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    Assert.Equal(3, loadedNames.Count);
                    Assert.Contains(primaryCab, loadedNames);
                    Assert.Contains(depCab, loadedNames);
                    Assert.Contains(fallbackCab, loadedNames);

                    // Verify PPtr external file resolution succeeds for both FileID=1 (depCab) and FileID=2 (fallbackCab)
                    var primaryFile = managerEnabled.assetsFileList.First(f => f.fileName == primaryCab);
                    var pptrToCatalogDep = new PPtr<Object>(fileID: 1, pathID: 1, primaryFile);
                    var pptrToFallbackDep = new PPtr<Object>(fileID: 2, pathID: 1, primaryFile);

                    Assert.True(pptrToCatalogDep.TryGetAssetsFile(out var resolvedDepFile));
                    Assert.Equal(depCab, resolvedDepFile!.fileName);

                    Assert.True(pptrToFallbackDep.TryGetAssetsFile(out var resolvedFallbackFile));
                    Assert.Equal(fallbackCab, resolvedFallbackFile!.fileName);
                }
            }
            finally
            {
                AddressablesCatalogDependencyResolver.ClearCache();
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void WindowOptionsViewModel_PersistsAutoLoadCatalogDependenciesSetting()
        {
            var settings = new AvaloniaAppSettings();
            Assert.True(settings.AutoLoadCatalogDependencies);

            var vm = new WindowOptionsViewModel();
            Assert.True(vm.AutoLoadCatalogDependencies);

            bool? observedChange = null;
            vm.AutoLoadCatalogDependenciesChanged += val => observedChange = val;

            vm.AutoLoadCatalogDependencies = false;
            Assert.Equal(false, observedChange);

            vm.ApplyToSettings(settings);
            Assert.False(settings.AutoLoadCatalogDependencies);

            var vm2 = new WindowOptionsViewModel();
            vm2.LoadFromSettings(settings);
            Assert.False(vm2.AutoLoadCatalogDependencies);
        }

        [Fact]
        public void FallbackResolution_WithoutCatalog_ResolvesManifestAndCabSiblingBundles()
        {
            var tempRoot = CreateTempDirectory("fallback_manifest_and_cab");
            try
            {
                var bundleDir = Path.Combine(tempRoot, "Bundles");
                Directory.CreateDirectory(bundleDir);

                const string charCab = "CAB-aaaa0000aaaa0000aaaa0000aaaa0001";
                const string matCab = "CAB-bbbb0000bbbb0000bbbb0000bbbb0002";
                const string texCab = "CAB-cccc0000cccc0000cccc0000cccc0003";

                var charBundle = Path.Combine(bundleDir, "character.bundle");
                var matBundle = Path.Combine(bundleDir, "materials.bundle");
                var texBundle = Path.Combine(bundleDir, "textures.bundle");

                File.WriteAllBytes(charBundle, BuildMinimalUnityFsBundle(charCab, new[] { $"archive:/{matCab}/{matCab}" }));
                File.WriteAllBytes(matBundle, BuildMinimalUnityFsBundle(matCab, new[] { $"archive:/{texCab}/{texCab}" }));
                File.WriteAllBytes(texBundle, BuildMinimalUnityFsBundle(texCab));

                // Sidecar .manifest for character.bundle -> materials.bundle (while textures.bundle is only discoverable via 2nd-wave CAB m_Externals probe)
                File.WriteAllText(charBundle + ".manifest", """
                ManifestFileVersion: 0
                CRC: 123456789
                Dependencies:
                - Assets/AssetBundles/materials.bundle
                """, Encoding.UTF8);

                using var manager = new AssetsManager();
                manager.LoadFiles(charBundle);

                var loadedNames = manager.assetsFileList.Select(f => f.fileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                Assert.Equal(3, loadedNames.Count);
                Assert.Contains(charCab, loadedNames);
                Assert.Contains(matCab, loadedNames);
                Assert.Contains(texCab, loadedNames);
            }
            finally
            {
                AddressablesCatalogDependencyResolver.ClearCache();
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public void ResolveDependenciesForLoadedFiles_SkipsUnreferencedLargeGroupSiblingWhenCabMatchExists()
        {
            var tempRoot = CreateTempDirectory("skip_large_unreferenced");
            try
            {
                var aaDir = Path.Combine(tempRoot, "Sample_Data", "StreamingAssets", "aa");
                var winDir = Path.Combine(aaDir, "StandaloneWindows64");
                Directory.CreateDirectory(winDir);

                const string primaryCab = "CAB-11112222333344445555666677778888";
                const string neededCab = "CAB-99990000aaaabbbbccccddddeeeeffff";
                const string unreferencedCab = "CAB-fedcba9876543210fedcba9876543210";

                var primaryPath = Path.Combine(winDir, "primary.bundle");
                var neededPath = Path.Combine(winDir, "needed_shared.bundle");
                var hugeUnreferencedPath = Path.Combine(winDir, "huge_unreferenced.bundle");

                File.WriteAllBytes(primaryPath, BuildMinimalUnityFsBundle(primaryCab, new[] { $"archive:/{neededCab}/{neededCab}" }));
                File.WriteAllBytes(neededPath, BuildMinimalUnityFsBundle(neededCab));
                File.WriteAllBytes(hugeUnreferencedPath, BuildMinimalUnityFsBundle(unreferencedCab));

                // Expand huge_unreferenced.bundle file length slightly above UnreferencedLargeDependencyThresholdBytes (sparse file)
                using (var fs = new FileStream(hugeUnreferencedPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                {
                    fs.SetLength(AddressablesCatalogDependencyResolver.UnreferencedLargeDependencyThresholdBytes + 1024);
                }

                var catalogPath = Path.Combine(aaDir, "catalog.json");
                File.WriteAllText(catalogPath, BuildAddressablesV1CatalogJson(
                    internalIds: new[]
                    {
                        "{UnityEngine.AddressableAssets.Addressables.RuntimePath}/StandaloneWindows64/primary.bundle",
                        "{UnityEngine.AddressableAssets.Addressables.RuntimePath}/StandaloneWindows64/needed_shared.bundle",
                        "{UnityEngine.AddressableAssets.Addressables.RuntimePath}/StandaloneWindows64/huge_unreferenced.bundle",
                        "Assets/Sample/Main.prefab"
                    },
                    prefixes: Array.Empty<string>(),
                    buckets: new[]
                    {
                        new[] { 0, 1, 2 }
                    },
                    entries: new (int internalIdIdx, int providerIdx, int depBucketIdx, string? bundleName)[]
                    {
                        (0, 0, -1, "11112222333344445555666677778888"),
                        (1, 0, -1, "99990000aaaabbbbccccddddeeeeffff"),
                        (2, 0, -1, "fedcba9876543210fedcba9876543210"),
                        (3, 1, 0, null)
                    }), Encoding.UTF8);

                using var manager = new AssetsManager();
                manager.LoadFiles(primaryPath);

                var loadedNames = manager.assetsFileList.Select(f => f.fileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                Assert.Equal(2, loadedNames.Count);
                Assert.Contains(primaryCab, loadedNames);
                Assert.Contains(neededCab, loadedNames);
                Assert.DoesNotContain(unreferencedCab, loadedNames);
            }
            finally
            {
                AddressablesCatalogDependencyResolver.ClearCache();
                TryDeleteDirectory(tempRoot);
            }
        }

        [SkippableFact]
        public void ExternalBundleSmokeProbe_WhenEnvironmentVariableProvided_ResolvesExternalMaterialMainTex()
        {
            var bundlePath = Environment.GetEnvironmentVariable("ASSETSTUDIO_SMOKE_BUNDLE_PATH");
            var materialName = Environment.GetEnvironmentVariable("ASSETSTUDIO_SMOKE_MATERIAL_NAME");
            if (string.IsNullOrWhiteSpace(bundlePath) || !File.Exists(bundlePath))
            {
                throw new SkipException("Set ASSETSTUDIO_SMOKE_BUNDLE_PATH to an existing bundle to run this probe.");
            }

            var catalogDeps = AddressablesCatalogDependencyResolver.ResolveDependencies(new[] { bundlePath });
            Assert.NotEmpty(catalogDeps);

            using var manager = new AssetsManager();
            manager.LoadFiles(bundlePath);

            Assert.True(manager.assetsFileList.Count >= 2, $"Expected >= 2 loaded SerializedFiles, got {manager.assetsFileList.Count}");

            if (!string.IsNullOrWhiteSpace(materialName))
            {
                var material = manager.assetsFileList
                    .SelectMany(f => f.Objects)
                    .OfType<Material>()
                    .FirstOrDefault(m => string.Equals(m.m_Name, materialName, StringComparison.OrdinalIgnoreCase));

                Assert.NotNull(material);
                var mainTexEntry = material!.m_SavedProperties.m_TexEnvs
                    .FirstOrDefault(kv => string.Equals(kv.Key, "_MainTex", StringComparison.OrdinalIgnoreCase));
                Assert.NotNull(mainTexEntry.Value);
                Assert.True(mainTexEntry.Value.m_Texture.m_FileID > 0, "Expected _MainTex to be an external PPtr (m_FileID > 0).");
                Assert.True(mainTexEntry.Value.m_Texture.TryGet(out var resolvedTex), "Expected external _MainTex PPtr to resolve automatically!");
                Assert.NotNull(resolvedTex);
                Assert.False(string.IsNullOrWhiteSpace(resolvedTex!.m_Name));
            }
        }

        private static byte[] BuildMinimalUnityFsBundle(string cabName, IReadOnlyList<string>? externals = null)
        {
            var serializedFileBytes = BuildMinimalSerializedFileBytes("2021.3.16f1", externals ?? Array.Empty<string>());

            using var blocksInfoMs = new MemoryStream();
            blocksInfoMs.Write(new byte[16]); // hash
            WriteInt32BE(blocksInfoMs, 1); // 1 block
            WriteUInt32BE(blocksInfoMs, (uint)serializedFileBytes.Length);
            WriteUInt32BE(blocksInfoMs, (uint)serializedFileBytes.Length);
            WriteUInt16BE(blocksInfoMs, (ushort)CompressionType.None);
            WriteInt32BE(blocksInfoMs, 1); // 1 directory node
            WriteInt64BE(blocksInfoMs, 0L);
            WriteInt64BE(blocksInfoMs, serializedFileBytes.Length);
            WriteUInt32BE(blocksInfoMs, 4u);
            WriteNullTerminatedUtf8(blocksInfoMs, cabName);
            var blocksInfoBytes = blocksInfoMs.ToArray();

            using var headerMs = new MemoryStream();
            WriteNullTerminatedUtf8(headerMs, "UnityFS");
            WriteUInt32BE(headerMs, 6u);
            WriteNullTerminatedUtf8(headerMs, "5.x.x");
            WriteNullTerminatedUtf8(headerMs, "2021.3.16f1");
            long totalSize = headerMs.Length + 8 + 4 + 4 + 4 + blocksInfoBytes.Length + serializedFileBytes.Length;
            WriteInt64BE(headerMs, totalSize);
            WriteUInt32BE(headerMs, (uint)blocksInfoBytes.Length);
            WriteUInt32BE(headerMs, (uint)blocksInfoBytes.Length);
            WriteUInt32BE(headerMs, (uint)ArchiveFlags.BlocksAndDirectoryInfoCombined);
            headerMs.Write(blocksInfoBytes, 0, blocksInfoBytes.Length);
            headerMs.Write(serializedFileBytes, 0, serializedFileBytes.Length);
            return headerMs.ToArray();
        }

        private static byte[] BuildMinimalSerializedFileBytes(string unityVersion, IReadOnlyList<string> externalPaths)
        {
            byte[] metadata;
            using (var metaMs = new MemoryStream())
            using (var metaBw = new BinaryWriter(metaMs, Encoding.UTF8, leaveOpen: true))
            {
                metaBw.Write(Encoding.UTF8.GetBytes(unityVersion));
                metaBw.Write((byte)0);
                metaBw.Write((int)BuildTarget.StandaloneWindows64);
                metaBw.Write(false); // m_EnableTypeTree
                metaBw.Write(0); // typeCount
                metaBw.Write(0); // objectCount
                metaBw.Write(0); // scriptCount
                metaBw.Write(externalPaths.Count); // externalsCount
                foreach (var extPath in externalPaths)
                {
                    metaBw.Write((byte)0); // tempEmpty
                    metaBw.Write(new byte[16]); // guid
                    metaBw.Write(0); // type
                    metaBw.Write(Encoding.UTF8.GetBytes(extPath));
                    metaBw.Write((byte)0);
                }
                metaBw.Write(0); // refTypesCount (SupportsRefObject)
                metaBw.Write((byte)0); // userInformation
                metaBw.Flush();
                metadata = metaMs.ToArray();
            }

            const int headerSize = 20;
            uint totalSize = (uint)(headerSize + metadata.Length);
            var fullBytes = new byte[totalSize];
            BinaryPrimitives.WriteUInt32BigEndian(fullBytes.AsSpan(0, 4), (uint)metadata.Length);
            BinaryPrimitives.WriteUInt32BigEndian(fullBytes.AsSpan(4, 4), totalSize);
            BinaryPrimitives.WriteUInt32BigEndian(fullBytes.AsSpan(8, 4), (uint)SerializedFileFormatVersion.RefactorTypeData);
            BinaryPrimitives.WriteUInt32BigEndian(fullBytes.AsSpan(12, 4), totalSize);
            fullBytes[16] = 0; // LittleEndian
            Buffer.BlockCopy(metadata, 0, fullBytes, headerSize, metadata.Length);
            return fullBytes;
        }

        private static string BuildAddressablesV1CatalogJson(
            string[] internalIds,
            string[] prefixes,
            int[][] buckets,
            (int internalIdIdx, int providerIdx, int depBucketIdx, string? bundleName)[] entries)
        {
            using var bucketMs = new MemoryStream();
            using (var bbw = new BinaryWriter(bucketMs, Encoding.UTF8, leaveOpen: true))
            {
                bbw.Write(buckets.Length);
                for (int i = 0; i < buckets.Length; i++)
                {
                    bbw.Write(0); // keyDataOffset
                    bbw.Write(buckets[i].Length);
                    foreach (var entryIdx in buckets[i])
                    {
                        bbw.Write(entryIdx);
                    }
                }
            }

            using var extraMs = new MemoryStream();
            using var entryMs = new MemoryStream();
            using (var ebw = new BinaryWriter(entryMs, Encoding.UTF8, leaveOpen: true))
            using (var xbw = new BinaryWriter(extraMs, Encoding.UTF8, leaveOpen: true))
            {
                ebw.Write(entries.Length);
                foreach (var entry in entries)
                {
                    int dataIdx = -1;
                    if (!string.IsNullOrEmpty(entry.bundleName))
                    {
                        dataIdx = (int)extraMs.Position;
                        var json = JsonSerializer.Serialize(new
                        {
                            m_BundleName = entry.bundleName,
                            m_Hash = entry.bundleName,
                            m_Crc = 123456u
                        });
                        xbw.Write((byte)7); // JsonObject
                        var asm = Encoding.ASCII.GetBytes("Unity.ResourceManager");
                        xbw.Write((byte)asm.Length);
                        xbw.Write(asm);
                        var cls = Encoding.ASCII.GetBytes("UnityEngine.ResourceManagement.ResourceProviders.AssetBundleRequestOptions");
                        xbw.Write((byte)cls.Length);
                        xbw.Write(cls);
                        var jsonBytes = Encoding.Unicode.GetBytes(json);
                        xbw.Write(jsonBytes.Length);
                        xbw.Write(jsonBytes);
                        xbw.Flush();
                    }

                    ebw.Write(entry.internalIdIdx);
                    ebw.Write(entry.providerIdx);
                    ebw.Write(entry.depBucketIdx);
                    ebw.Write(0); // depHash
                    ebw.Write(dataIdx);
                    ebw.Write(0); // primaryKey
                    ebw.Write(0); // resourceType
                }
            }

            var root = new
            {
                m_LocatorId = "AddressablesMainContentCatalog",
                m_ProviderIds = new[]
                {
                    "UnityEngine.ResourceManagement.ResourceProviders.AssetBundleProvider",
                    "UnityEngine.ResourceManagement.ResourceProviders.BundledAssetProvider"
                },
                m_InternalIdPrefixes = prefixes,
                m_InternalIds = internalIds,
                m_KeyDataString = Convert.ToBase64String(new byte[] { 1, 0, 0, 0, 0, 0, 0, 0, 0 }),
                m_BucketDataString = Convert.ToBase64String(bucketMs.ToArray()),
                m_EntryDataString = Convert.ToBase64String(entryMs.ToArray()),
                m_ExtraDataString = Convert.ToBase64String(extraMs.ToArray())
            };

            return JsonSerializer.Serialize(root);
        }

        private static byte[] BuildSyntheticAddressablesV2CatalogBin(
            string locatorId,
            (string internalId, string bundleName, int[] depBundleIndices)[] bundles)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

            // Reserve 32-byte header + 4-byte keysByteSize + keys array (bundles.Length * 8 bytes)
            const uint keysOffset = 36u;
            int keysByteSize = bundles.Length * 8;
            bw.Write(new byte[32]);
            bw.Write(keysByteSize);
            bw.Write(new byte[keysByteSize]);

            uint WriteAsciiString(string s)
            {
                var bytes = Encoding.ASCII.GetBytes(s);
                bw.Write(bytes.Length);
                uint offset = (uint)ms.Position;
                bw.Write(bytes);
                return offset;
            }

            uint WriteDynamicPathString(string path, char sep)
            {
                var segments = path.Split(new[] { sep }, StringSplitOptions.RemoveEmptyEntries);
                uint nextId = uint.MaxValue;
                for (int i = 0; i < segments.Length; i++)
                {
                    uint partStrId = WriteAsciiString(segments[i]);
                    uint nodeOffset = (uint)ms.Position;
                    bw.Write(partStrId);
                    bw.Write(nextId);
                    nextId = nodeOffset | 0x40000000u; // kDynamicStringFlag
                }
                return nextId;
            }

            uint WriteUInt32Array(uint[] values)
            {
                bw.Write(values.Length * 4);
                uint offset = (uint)ms.Position;
                foreach (var v in values)
                {
                    bw.Write(v);
                }
                return offset;
            }

            uint idOffset = WriteAsciiString(locatorId);
            uint providerOffset = WriteDynamicPathString("UnityEngine.ResourceManagement.ResourceProviders.AssetBundleProvider", '.');

            // Reserve 28 bytes per bundle location so dependencySetOffset can reference them by offset
            var locationOffsets = new uint[bundles.Length];
            for (int i = 0; i < bundles.Length; i++)
            {
                locationOffsets[i] = (uint)ms.Position;
                bw.Write(new byte[28]);
            }

            for (int i = 0; i < bundles.Length; i++)
            {
                var b = bundles[i];
                uint primaryKeyOff = WriteAsciiString(b.bundleName);
                uint internalIdOff = WriteDynamicPathString(b.internalId.Replace('\\', '/'), '/');

                uint depSetOff = uint.MaxValue;
                if (b.depBundleIndices.Length > 0)
                {
                    var depOffsets = new uint[1 + b.depBundleIndices.Length];
                    depOffsets[0] = locationOffsets[i];
                    for (int j = 0; j < b.depBundleIndices.Length; j++)
                    {
                        depOffsets[j + 1] = locationOffsets[b.depBundleIndices[j]];
                    }
                    depSetOff = WriteUInt32Array(depOffsets);
                }

                // Write AssetBundleRequestOptions SerializedData
                uint hashOff = (uint)ms.Position;
                bw.Write(new byte[16]);
                uint bundleNameOff = WriteAsciiString(b.bundleName);
                uint serializedDataOff = (uint)ms.Position;
                bw.Write(hashOff);
                bw.Write(bundleNameOff);
                bw.Write(12345u); // crc
                bw.Write(4096u);  // bundleSize
                bw.Write(uint.MaxValue); // commonId

                uint extraDataOff = (uint)ms.Position;
                bw.Write(uint.MaxValue); // typeId
                bw.Write(serializedDataOff); // objectId

                // Fill 28-byte ResourceLocation.Serializer.Data
                long savedPos = ms.Position;
                ms.Position = locationOffsets[i];
                bw.Write(primaryKeyOff);
                bw.Write(internalIdOff);
                bw.Write(providerOffset);
                bw.Write(depSetOff);
                bw.Write(0); // dependencyHashValue
                bw.Write(extraDataOff);
                bw.Write(uint.MaxValue); // typeId
                ms.Position = savedPos;

                // Fill KeyData[i]
                uint locSetOff = WriteUInt32Array(new[] { locationOffsets[i] });
                savedPos = ms.Position;
                ms.Position = keysOffset + i * 8;
                bw.Write(primaryKeyOff);
                bw.Write(locSetOff);
                ms.Position = savedPos;
            }

            // Fill 32-byte header at offset 0
            ms.Position = 0;
            bw.Write(AddressablesCatalogDependencyResolver.BinaryCatalogMagic);
            bw.Write(2); // version
            bw.Write(keysOffset);
            bw.Write(idOffset);
            bw.Write(uint.MaxValue);
            bw.Write(uint.MaxValue);
            bw.Write(uint.MaxValue);
            bw.Write(uint.MaxValue);

            bw.Flush();
            return ms.ToArray();
        }

        [Fact]
        public void AddressablesCatalogAnalyzer_IsBundleProvider_ExcludesBundledAssetAndBundledSceneProviders()
        {
            Assert.True(AssetStudio.Avalonia.Services.ProjectAnalysis.AddressablesCatalogAnalyzer.IsBundleProvider(
                "UnityEngine.ResourceManagement.ResourceProviders.AssetBundleProvider"));
            Assert.True(AssetStudio.Avalonia.Services.ProjectAnalysis.AddressablesCatalogAnalyzer.IsBundleProvider(
                "Custom.ResourceProviders.CustomAssetBundleProvider"));
            Assert.True(AssetStudio.Avalonia.Services.ProjectAnalysis.AddressablesCatalogAnalyzer.IsBundleProvider(
                "Custom.ResourceProviders.CustomDlcAssetBundleProvider"));

            Assert.False(AssetStudio.Avalonia.Services.ProjectAnalysis.AddressablesCatalogAnalyzer.IsBundleProvider(
                "UnityEngine.ResourceManagement.ResourceProviders.BundledAssetProvider"));
            Assert.False(AssetStudio.Avalonia.Services.ProjectAnalysis.AddressablesCatalogAnalyzer.IsBundleProvider(
                "UnityEngine.ResourceManagement.ResourceProviders.BundledSceneProvider"));
            Assert.False(AssetStudio.Avalonia.Services.ProjectAnalysis.AddressablesCatalogAnalyzer.IsBundleProvider(null));
            Assert.False(AssetStudio.Avalonia.Services.ProjectAnalysis.AddressablesCatalogAnalyzer.IsBundleProvider(string.Empty));
        }

        private static string CreateTempDirectory(string prefix)
        {
            var path = Path.Combine(Path.GetTempPath(), "AssetStudioTests_" + prefix + "_" + Guid.NewGuid().ToString("N"));
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

        private static void WriteNullTerminatedUtf8(Stream stream, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            stream.Write(bytes, 0, bytes.Length);
            stream.WriteByte(0);
        }

        private static void WriteUInt16BE(Stream stream, ushort value)
        {
            Span<byte> buf = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(buf, value);
            stream.Write(buf);
        }

        private static void WriteInt32BE(Stream stream, int value)
        {
            Span<byte> buf = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(buf, value);
            stream.Write(buf);
        }

        private static void WriteUInt32BE(Stream stream, uint value)
        {
            Span<byte> buf = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(buf, value);
            stream.Write(buf);
        }

        private static void WriteInt64BE(Stream stream, long value)
        {
            Span<byte> buf = stackalloc byte[8];
            BinaryPrimitives.WriteInt64BigEndian(buf, value);
            stream.Write(buf);
        }
    }
}
