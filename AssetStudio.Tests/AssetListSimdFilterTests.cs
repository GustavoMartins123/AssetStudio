using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AssetStudio.Avalonia;
using AssetStudio.Avalonia.Services;
using Xunit;

namespace AssetStudio.Tests
{
    public class AssetListSimdFilterTests
    {
        [Theory]
        [InlineData("", false)]
        [InlineData(null, false)]
        [InlineData("/", false)]
        [InlineData("\\", false)]
        [InlineData("assets/models/hero.fbx", true)]
        [InlineData(@"assets\models\HERO.FBX\mesh01", true)]
        [InlineData(@"assets//models\\char.FbX/", true)]
        [InlineData("folder.fbx/sub/clip.anim", true)]
        [InlineData("assets/.fbx", true)]
        [InlineData("assets/..fbx", true)]
        [InlineData("assets/models/hero.fbx2", false)]
        [InlineData("assets/models/hero_fbx", false)]
        [InlineData("assets/models/hero.fbx.", false)]
        [InlineData("assets/textures/diffuse.png", false)]
        [InlineData("no_extension", false)]
        [InlineData("a/b.FBX/c.mat", true)]
        public void AssetItem_IsFbxSubAsset_MatchesLegacySplitAndGetExtensionParity(string? container, bool expected)
        {
            var handle = new AssetHandle
            {
                Type = ClassIDType.Mesh,
                Container = container ?? string.Empty
            };
            var item = new AssetItem(handle)
            {
                Container = container!
            };

            Assert.Equal(expected, LegacyIsFbxSubAsset(container));
            Assert.Equal(expected, item.IsFbxSubAsset());
            Assert.Equal(expected, AssetItem.ContainsFbxSegment(container));
        }

        [Fact]
        public void AssetItem_DisplayType_FormatsAllCategoriesCachesResultAndInvalidatesOnMutation()
        {
            var item = new AssetItem(new AssetHandle
            {
                Type = ClassIDType.Mesh,
                Container = "assets/props/crate.prefab"
            });

            var initial = item.DisplayType;
            Assert.Equal("Mesh (Geometry)", initial);
            Assert.Same(initial, item.DisplayType);
            Assert.False(item.IsFbxSubAsset());

            // Mutate Container to FBX sub-asset -> cache must invalidate immediately.
            item.Container = @"assets\characters\Hero.FBX\body_mesh";
            Assert.True(item.IsFbxSubAsset());
            var fbxDisplay = item.DisplayType;
            Assert.Equal("Mesh (Geometry) (FBX sub-asset)", fbxDisplay);
            Assert.Same(fbxDisplay, item.DisplayType);

            // Mutate Container back to non-FBX -> cache must invalidate again.
            item.Container = "assets/props/crate2.prefab";
            Assert.False(item.IsFbxSubAsset());
            Assert.Equal("Mesh (Geometry)", item.DisplayType);

            // Mutate Type and TypeString -> cache must invalidate.
            item.Type = ClassIDType.Transform;
            item.TypeString = nameof(ClassIDType.Transform);
            Assert.Equal("Transform (Component)", item.DisplayType);

            item.Type = ClassIDType.PrefabInstance;
            item.TypeString = nameof(ClassIDType.PrefabInstance);
            Assert.Equal("Prefab (Composite)", item.DisplayType);

            item.Type = ClassIDType.GameObject;
            item.TypeString = nameof(ClassIDType.GameObject);
            Assert.Equal("GameObject (Hierarchy Node)", item.DisplayType);

            item.Type = ClassIDType.MonoBehaviour;
            item.TypeString = nameof(ClassIDType.MonoBehaviour);
            Assert.Equal("MonoBehaviour (Script Instance)", item.DisplayType);

            item.Type = ClassIDType.Material;
            item.TypeString = nameof(ClassIDType.Material);
            Assert.Equal("Material (Shader Settings)", item.DisplayType);

            item.Type = ClassIDType.Texture2D;
            item.TypeString = nameof(ClassIDType.Texture2D);
            Assert.Equal("Texture2D", item.DisplayType);
            Assert.Same(item.TypeString, item.DisplayType);
        }

        [Fact]
        public void AssetItem_DisplayType_ConcurrentParallelReadsAreThreadSafe()
        {
            var items = Enumerable.Range(0, 5000).Select(i => new AssetItem(new AssetHandle
            {
                Type = (i % 4) switch
                {
                    0 => ClassIDType.Mesh,
                    1 => ClassIDType.Material,
                    2 => ClassIDType.Transform,
                    _ => ClassIDType.Texture2D
                },
                Container = i % 3 == 0 ? $"assets/models/m_{i % 17}.FBX/part" : $"assets/tex/t_{i}.png",
                PathID = i + 1,
                ByteSize = 128 + i,
                Name = $"Item_{i}"
            })).ToArray();

            var expected = items.Select(x =>
            {
                var copy = new AssetItem(new AssetHandle { Type = x.Type, Container = x.Container });
                return copy.DisplayType;
            }).ToArray();

            Parallel.For(0, 4, _ =>
            {
                for (var i = 0; i < items.Length; i++)
                {
                    Assert.Equal(expected[i], items[i].DisplayType);
                }
            });
        }

        [Fact]
        public void CommonString_StringBuffer_IsFrozenDictionaryWithExpectedEntries()
        {
            Assert.IsAssignableFrom<FrozenDictionary<uint, string>>(CommonString.StringBuffer);
            IReadOnlyDictionary<uint, string> asReadOnly = CommonString.StringBuffer;
            Assert.True(asReadOnly.Count >= 60);

            Assert.True(CommonString.StringBuffer.TryGetValue(0u, out var v0));
            Assert.Equal("AABB", v0);
            Assert.True(CommonString.StringBuffer.TryGetValue(5u, out var v5));
            Assert.Equal("AnimationClip", v5);
            Assert.True(CommonString.StringBuffer.TryGetValue(49u, out var v49));
            Assert.Equal("Array", v49);
            Assert.True(CommonString.StringBuffer.TryGetValue(161u, out var v161));
            Assert.Equal("float", v161);
            Assert.True(CommonString.StringBuffer.TryGetValue(1161u, out var v1161));
            Assert.Equal("Hash128", v1161);

            Assert.False(CommonString.StringBuffer.TryGetValue(1u, out _));
            Assert.False(CommonString.StringBuffer.TryGetValue(999999u, out _));
        }

        [Fact]
        public void AssetHandle_BuildSourceHash_MatchesReferenceNormalizationAndCachesDeterministically()
        {
            var tempRoot = Path.Combine(Path.GetTempPath(), "as_hash_parity_" + Guid.NewGuid().ToString("N"));
            var nested = Path.Combine(tempRoot, "Data", "level0");
            var forwardSlash = nested.Replace('\\', '/');
            var withTrailingSlash = nested + Path.DirectorySeparatorChar;
            var withDotDot = Path.Combine(tempRoot, "Sub", "..", "Data", "level0");
            var longSegment = new string('a', 300);
            var longPath = Path.Combine(tempRoot, longSegment, longSegment, "sharedassets0.assets");

            var cases = new (string FileName, string? OriginalPath)[]
            {
                ("level0", null),
                ("level0", string.Empty),
                ("level0", "   "),
                ("level0", nested),
                ("level0", forwardSlash),
                ("level0", withTrailingSlash),
                ("level0", withDotDot),
                ("level0", nested.ToLowerInvariant()),
                ("level0", nested.ToUpperInvariant()),
                ("relative.assets", "assets/sub/relative.assets"),
                ("relative.assets", @"assets\sub\relative.assets"),
                ("long.assets", longPath),
                ("invalid.assets", "bad\0path\\file.assets"),
                ("café_日本語", Path.Combine(tempRoot, "café_日本語", "data.unity3d")),
                ("root_slash", "/"),
            };

            foreach (var (fileName, originalPath) in cases)
            {
                var expected = ReferenceBuildSourceHash(fileName, originalPath);
                var first = AssetHandle.BuildSourceHash(fileName, originalPath);
                var second = AssetHandle.BuildSourceHash(fileName, originalPath);

                Assert.Equal(expected, first);
                Assert.Equal(expected, second);
                Assert.Equal(16, first.Length);
                Assert.Matches("^[0-9a-f]{16}$", first);

                var uid = AssetHandle.BuildUniqueID(fileName, originalPath, -42L);
                Assert.Equal($"{fileName}#{expected}#-42", uid);
            }

            // Equivalent normalized paths must produce identical hashes on Windows and Linux.
            Assert.Equal(
                AssetHandle.BuildSourceHash("level0", nested),
                AssetHandle.BuildSourceHash("level0", forwardSlash));
            Assert.Equal(
                AssetHandle.BuildSourceHash("level0", nested),
                AssetHandle.BuildSourceHash("level0", withTrailingSlash));
            Assert.Equal(
                AssetHandle.BuildSourceHash("level0", nested),
                AssetHandle.BuildSourceHash("level0", withDotDot));
            if (OperatingSystem.IsLinux())
            {
                Assert.NotEqual(
                    AssetHandle.BuildSourceHash("level0", nested.ToLowerInvariant()),
                    AssetHandle.BuildSourceHash("level0", nested.ToUpperInvariant()));
            }
            else
            {
                Assert.Equal(
                    AssetHandle.BuildSourceHash("level0", nested.ToLowerInvariant()),
                    AssetHandle.BuildSourceHash("level0", nested.ToUpperInvariant()));
            }

            // Explicit cross-platform check for Linux case-sensitivity vs Windows case-folding.
            Assert.NotEqual(
                AssetHandle.BuildSourceHash("level0", Path.Combine(tempRoot, "a.bundle"), caseSensitive: true),
                AssetHandle.BuildSourceHash("level0", Path.Combine(tempRoot, "A.bundle"), caseSensitive: true));
            Assert.Equal(
                AssetHandle.BuildSourceHash("level0", Path.Combine(tempRoot, "a.bundle"), caseSensitive: false),
                AssetHandle.BuildSourceHash("level0", Path.Combine(tempRoot, "A.bundle"), caseSensitive: false));

            AssetHandle.ClearSourceHashCache();
            Assert.Equal(
                ReferenceBuildSourceHash("level0", nested),
                AssetHandle.BuildSourceHash("level0", nested));
        }

        [Fact]
        public void AssetLoadingService_PriorityKeywords_MatchesLegacySubstringBehavior()
        {
            Assert.Null(AssetLoadingService.GetPriorityKeywordSearch(null));
            Assert.Null(AssetLoadingService.GetPriorityKeywordSearch(Array.Empty<ClassIDType>()));
            Assert.Null(AssetLoadingService.GetPriorityKeywordSearch(new[] { ClassIDType.BoxCollider }));

            var filterSets = new ClassIDType[][]
            {
                new[] { ClassIDType.Texture2D },
                new[] { ClassIDType.Sprite },
                new[] { ClassIDType.AudioClip },
                new[] { ClassIDType.Mesh },
                new[] { ClassIDType.AnimationClip },
                new[] { ClassIDType.Animator },
                new[] { ClassIDType.Shader },
                new[] { ClassIDType.MonoBehaviour },
                new[] { ClassIDType.Texture2D, ClassIDType.AudioClip, ClassIDType.Mesh, ClassIDType.Shader, ClassIDType.MonoBehaviour },
            };

            var samplePaths = new[]
            {
                @"C:\Game\StreamingAssets\hero_TEXTURE_01.bundle",
                @"C:\Game\texture\unrelated_data.bundle", // keyword in directory only, NOT filename
                @"C:\Game\bundles\bgm_SFX_pack.ab",
                @"C:\Game\bundles\player_Model_lod0.bundle",
                @"C:\Game\bundles\player_ANIM_Controller.bundle",
                @"C:\Game\bundles\urp_Standard_Shader.bundle",
                @"C:\Game\bundles\gameplay_Mono_scripts.bundle",
                @"C:\Game\bundles\level0.assets",
                string.Empty,
            };

            foreach (var filters in filterSets)
            {
                var search = AssetLoadingService.GetPriorityKeywordSearch(filters);
                Assert.NotNull(search);

                var legacyKeywords = LegacyGetKeywords(filters);
                foreach (var path in samplePaths)
                {
                    var expected = legacyKeywords.Any(k => Path.GetFileName(path).ToLowerInvariant().Contains(k, StringComparison.Ordinal));
                    var actual = Path.GetFileName(path.AsSpan()).ContainsAny(search!);
                    Assert.Equal(expected, actual);
                }
            }
        }

        [Fact]
        public void AssetListQuery_ParallelFilter_MatchesSequentialReferenceOn25000Items()
        {
            var items = BuildSyntheticAssets(25_000);

            var queries = new string?[]
            {
                null,
                string.Empty,
                "a",
                "Hero_LOD0",
                "hero_lod",
                "café_日本",
                "[lod0]",
                "FBX sub-asset",
                "Shader Settings",
                "19999",
                "__no_such_asset_999__",
                "Hero [LOD0] café",
            };

            var typeFilterSets = new HashSet<ClassIDType>?[]
            {
                null,
                new HashSet<ClassIDType>(),
                new HashSet<ClassIDType> { ClassIDType.Mesh, ClassIDType.Material },
                new HashSet<ClassIDType> { ClassIDType.Texture2D },
            };

            // 1. Small dataset (< ParallelFilterThreshold) and full dataset (>= ParallelFilterThreshold)
            var smallSlice = items.Take(500).ToList();
            foreach (var dataset in new IReadOnlyList<AssetItem?>[] { smallSlice, items })
            {
                foreach (var query in queries)
                {
                    foreach (var types in typeFilterSets)
                    {
                        var expected = SequentialReferenceFilter(dataset, null, types, query);
                        var actual = AssetListQuery.Filter(dataset, null, types, query, CancellationToken.None);

                        Assert.Equal(expected.Count, actual.Count);
                        for (var i = 0; i < expected.Count; i++)
                        {
                            Assert.Same(expected[i], actual[i]);
                        }
                    }
                }
            }

            // 2. ClassFilter override parity
            var classFilter = new AssetClassItem
            {
                ClassID = (int)ClassIDType.Mesh,
                UnityVersion = "2022.3.10f1"
            };
            var expectedClassFiltered = SequentialReferenceFilter(items, classFilter, null, "fbx");
            var actualClassFiltered = AssetListQuery.Filter(items, classFilter, null, "fbx", CancellationToken.None);
            Assert.NotEmpty(actualClassFiltered);
            Assert.Equal(expectedClassFiltered.Count, actualClassFiltered.Count);
            for (var i = 0; i < expectedClassFiltered.Count; i++)
            {
                Assert.Same(expectedClassFiltered[i], actualClassFiltered[i]);
            }

            // 3. Cancellation must throw OperationCanceledException
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() =>
                AssetListQuery.Filter(items, null, null, "hero", cts.Token));
        }

        [Fact]
        public void AssetListQuery_ParallelSort_MatchesSequentialReferenceAcrossAllSortKeys()
        {
            var items = BuildSyntheticAssets(20_000);
            var sortSpecs = new (string SortMember, bool Descending)[]
            {
                ("Name", false),
                ("Name", true),
                ("Container", false),
                ("Container", true),
                ("DisplayType", false),
                ("DisplayType", true),
                ("Type", false),
                ("PathID", false),
                ("PathID", true),
                ("FullSize", false),
                ("Size", true),
                ("UnknownColumn", false),
            };

            foreach (var (sortMember, descending) in sortSpecs)
            {
                var expected = items.ToList();
                var key = AssetListQuery.ResolveSortKey(sortMember);
                expected.Sort((l, r) => AssetListQuery.Compare(l, r, key, descending));

                var actual = AssetListQuery.SortInPlace(items.ToList(), sortMember, descending);

                Assert.Equal(expected.Count, actual.Count);
                for (var i = 0; i < expected.Count; i++)
                {
                    Assert.Same(expected[i], actual[i]);
                }
            }
        }

        [Fact]
        public void MainWindow_DataGrids_CompileBindingsWithoutReflectionBindings()
        {
            var assembly = typeof(MainWindow).Assembly;
            var assemblyPath = assembly.Location;
            Assert.True(File.Exists(assemblyPath));

            using var stream = File.OpenRead(assemblyPath);
            using var peReader = new PEReader(stream);
            var mdReader = peReader.GetMetadataReader();

            var typeRefNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var handle in mdReader.TypeReferences)
            {
                var typeRef = mdReader.GetTypeReference(handle);
                typeRefNames.Add(mdReader.GetString(typeRef.Name));
            }

            Assert.Contains("CompiledBindingExtension", typeRefNames);
            Assert.Contains("CompiledBindingPathBuilder", typeRefNames);

            // Inspect compiled XAML populate method(s) on MainWindow (and any XamlIl helper types) for property getter tokens.
            var compiledXamlMethods = new List<MethodInfo>();
            compiledXamlMethods.AddRange(typeof(MainWindow).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
                .Where(m => m.Name.Contains("XamlIl", StringComparison.Ordinal)));
            foreach (var type in assembly.GetTypes().Where(t => t.FullName?.Contains("XamlIl", StringComparison.Ordinal) == true))
            {
                compiledXamlMethods.AddRange(type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance));
            }

            Assert.NotEmpty(compiledXamlMethods);

            var calledOrLoadedMembers = new HashSet<string>(StringComparer.Ordinal);
            var mainWindowType = typeof(MainWindow);
            var module = mainWindowType.Module;
            foreach (var method in compiledXamlMethods)
            {
                var il = method.GetMethodBody()?.GetILAsByteArray();
                if (il == null || il.Length < 5)
                {
                    continue;
                }

                // Scan IL for 4-byte metadata tokens (MethodDef 0x06xxxxxx, MemberRef 0x0Axxxxxx, MethodSpec 0x2Bxxxxxx).
                for (var i = 0; i <= il.Length - 4; i++)
                {
                    var table = il[i + 3];
                    if (table != 0x06 && table != 0x0A && table != 0x2B)
                    {
                        continue;
                    }

                    var token = BitConverter.ToInt32(il, i);
                    try
                    {
                        var resolved = module.ResolveMember(token);
                        if (resolved?.DeclaringType == typeof(AssetItem) || resolved?.DeclaringType == typeof(AssetClassItem))
                        {
                            calledOrLoadedMembers.Add($"{resolved.DeclaringType.Name}.{resolved.Name}");
                        }
                    }
                    catch
                    {
                        // Not a valid member token at this byte offset
                    }
                }
            }

            Assert.Contains("AssetItem.get_Name", calledOrLoadedMembers);
            Assert.Contains("AssetItem.get_Container", calledOrLoadedMembers);
            Assert.Contains("AssetItem.get_DisplayType", calledOrLoadedMembers);
            Assert.Contains("AssetItem.get_PathID", calledOrLoadedMembers);
            Assert.Contains("AssetItem.get_FullSize", calledOrLoadedMembers);

            Assert.Contains("AssetClassItem.get_ClassID", calledOrLoadedMembers);
            Assert.Contains("AssetClassItem.get_Name", calledOrLoadedMembers);
            Assert.Contains("AssetClassItem.get_Namespace", calledOrLoadedMembers);
            Assert.Contains("AssetClassItem.get_Assembly", calledOrLoadedMembers);
            Assert.Contains("AssetClassItem.get_UnityVersion", calledOrLoadedMembers);
            Assert.Contains("AssetClassItem.get_ObjectCount", calledOrLoadedMembers);
            Assert.Contains("AssetClassItem.get_SourceKind", calledOrLoadedMembers);
        }

        private static bool LegacyIsFbxSubAsset(string? container)
        {
            if (string.IsNullOrEmpty(container))
            {
                return false;
            }

            return container
                .Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
                .Any(part => string.Equals(Path.GetExtension(part), ".fbx", StringComparison.OrdinalIgnoreCase));
        }

        private static string ReferenceBuildSourceHash(string serializedFileName, string? originalPath)
        {
            var caseSensitive = OperatingSystem.IsLinux();
            string normalizedPath;
            if (string.IsNullOrWhiteSpace(originalPath))
            {
                normalizedPath = string.Empty;
            }
            else
            {
                try
                {
                    normalizedPath = Path.GetFullPath(originalPath)
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (!caseSensitive)
                    {
                        normalizedPath = normalizedPath.ToUpperInvariant();
                    }
                }
                catch
                {
                    normalizedPath = originalPath.Replace('\\', '/').Trim();
                    if (!caseSensitive)
                    {
                        normalizedPath = normalizedPath.ToUpperInvariant();
                    }
                }
            }

            var key = string.IsNullOrEmpty(normalizedPath)
                ? serializedFileName ?? string.Empty
                : $"{normalizedPath}\u001f{serializedFileName ?? string.Empty}";
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
            return Convert.ToHexStringLower(bytes.AsSpan(0, 8));
        }

        private static List<string> LegacyGetKeywords(IEnumerable<ClassIDType> activeFilters)
        {
            var filters = activeFilters.ToHashSet();
            var keywords = new List<string>();
            if (filters.Contains(ClassIDType.Texture2D) || filters.Contains(ClassIDType.Sprite))
                keywords.AddRange(new[] { "texture", "sprite", "atlas", "image", "pic" });
            if (filters.Contains(ClassIDType.AudioClip))
                keywords.AddRange(new[] { "audio", "sound", "music", "sfx", "clip" });
            if (filters.Contains(ClassIDType.Mesh))
                keywords.AddRange(new[] { "mesh", "model", "geom", "3d" });
            if (filters.Contains(ClassIDType.AnimationClip) || filters.Contains(ClassIDType.Animator))
                keywords.AddRange(new[] { "anim", "motion", "controller" });
            if (filters.Contains(ClassIDType.Shader))
                keywords.Add("shader");
            if (filters.Contains(ClassIDType.MonoBehaviour))
                keywords.AddRange(new[] { "script", "behavior", "mono" });
            return keywords;
        }

        private static List<AssetItem> SequentialReferenceFilter(
            IReadOnlyList<AssetItem?> snapshot,
            AssetClassItem? classFilter,
            HashSet<ClassIDType>? selectedTypes,
            string? filterText)
        {
            var matches = new List<AssetItem>();
            foreach (var x in snapshot)
            {
                if (x == null)
                {
                    continue;
                }

                if (classFilter != null)
                {
                    if ((int)x.Type != classFilter.ClassID ||
                        !string.Equals(x.SourceFile?.unityVersion ?? string.Empty, classFilter.UnityVersion ?? string.Empty, StringComparison.Ordinal))
                    {
                        continue;
                    }
                }
                else if (selectedTypes != null)
                {
                    if (selectedTypes.Count == 0 || !selectedTypes.Contains(x.Type))
                    {
                        continue;
                    }
                }

                if (!string.IsNullOrEmpty(filterText))
                {
                    var textMatch = ContainsIgnoreCase(x.Name, filterText)
                        || ContainsIgnoreCase(x.Container, filterText)
                        || ContainsIgnoreCase(x.TypeString, filterText)
                        || ContainsIgnoreCase(x.DisplayType, filterText)
                        || ContainsIgnoreCase(x.PathIDString, filterText);
                    if (!textMatch)
                    {
                        continue;
                    }
                }

                matches.Add(x);
            }

            return matches;
        }

        private static bool ContainsIgnoreCase(string? value, string filterText)
        {
            return !string.IsNullOrEmpty(value) && value.Contains(filterText, StringComparison.OrdinalIgnoreCase);
        }

        private static List<AssetItem> BuildSyntheticAssets(int count)
        {
            var types = new[]
            {
                ClassIDType.Texture2D,
                ClassIDType.Mesh,
                ClassIDType.Material,
                ClassIDType.Transform,
                ClassIDType.PrefabInstance,
                ClassIDType.GameObject,
                ClassIDType.AudioClip,
                ClassIDType.MonoBehaviour,
            };

            var fileA = (SerializedFile)RuntimeHelpers.GetUninitializedObject(typeof(SerializedFile));
            fileA.unityVersion = "2022.3.10f1";
            fileA.fileName = "level0";

            var fileB = (SerializedFile)RuntimeHelpers.GetUninitializedObject(typeof(SerializedFile));
            fileB.unityVersion = "2021.3.20f1";
            fileB.fileName = "sharedassets0";

            var items = new List<AssetItem>(count);
            for (var i = 0; i < count; i++)
            {
                var type = types[i % types.Length];
                var container = (i % 5) switch
                {
                    0 => string.Empty,
                    1 => $"assets/models/pack_{i % 23}/Hero_Model.FBX/part_{i % 7}",
                    2 => $@"assets\textures\pack_{i % 11}\albedo_{i}.png",
                    3 => $"assets/shaders/lit_{i % 5}.shader",
                    _ => $"assets/prefabs/item_{i}.prefab",
                };

                var name = (i % 7) switch
                {
                    0 => string.Empty,
                    1 => $"Hero [LOD0] café_日本語_{i}",
                    2 => $"Enemy_Mesh_{i}",
                    3 => $"UI_Atlas_{i}",
                    4 => $"Prop_Crate_{i}",
                    5 => $"Audio_SFX_{i}",
                    _ => $"Common_{i % 50}",
                };

                var pathId = (long)((i * 7919) % 100_000) - 500;
                items.Add(new AssetItem(new AssetHandle
                {
                    UniqueID = $"uid#{i:D6}",
                    Name = name,
                    Type = type,
                    Container = container,
                    PathID = pathId,
                    ByteSize = (i * 31L) % 65536,
                    SourceFile = ((i / 8) % 2 == 0) ? fileA : fileB,
                })
                {
                    UniqueID = $" #{i}",
                });
            }

            return items;
        }
    }
}
