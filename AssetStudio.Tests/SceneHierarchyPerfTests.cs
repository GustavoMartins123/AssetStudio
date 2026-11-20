using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AssetStudio;
using AssetStudio.Avalonia;
using Xunit;
using Xunit.Abstractions;

namespace AssetStudio.Tests
{
    public class SceneHierarchyPerfTests
    {
        private readonly ITestOutputHelper output;

        public SceneHierarchyPerfTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        // ------------------------------------------------------------------ helpers

        private static GameObject NewGameObject() => (GameObject)RuntimeHelpers.GetUninitializedObject(typeof(GameObject));

        private static GameObjectNode GoNode(string name) => new GameObjectNode { Name = name, GameObject = NewGameObject() };

        private static GameObjectNode FileNode(string name) => new GameObjectNode { Name = name };

        private static GameObjectNode Add(GameObjectNode parent, GameObjectNode child)
        {
            parent.AddChild(child);
            return child;
        }

        /// <summary>Verbatim copy of the pre-PERF-ACT-06 algorithm (recursive DFS per asset).</summary>
        private static class Legacy
        {
            public static string? GetFbxContainerPath(string container)
            {
                if (string.IsNullOrEmpty(container))
                {
                    return null;
                }

                var parts = container.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
                var path = string.Empty;
                foreach (var part in parts)
                {
                    path = string.IsNullOrEmpty(path) ? part : Path.Combine(path, part);
                    if (string.Equals(Path.GetExtension(part), ".fbx", StringComparison.OrdinalIgnoreCase))
                    {
                        return path;
                    }
                }

                return null;
            }

            public static GameObjectNode? FindNodeByName(GameObjectNode node, string name)
            {
                if (node.GameObject != null && string.Equals(node.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return node;
                }

                foreach (var child in node.Children)
                {
                    var match = FindNodeByName(child, name);
                    if (match != null)
                    {
                        return match;
                    }
                }

                return null;
            }

            private static GameObjectNode GetFbxRootNode(GameObjectNode node, string fbxContainer)
            {
                var fbxName = Path.GetFileNameWithoutExtension(fbxContainer);
                var current = node;
                while (current.Parent?.GameObject != null)
                {
                    current = current.Parent;
                }

                return FindNodeByName(current, fbxName) ?? current;
            }

            private static GameObjectNode? FindSceneNodeByName(IReadOnlyList<GameObjectNode> roots, string name)
            {
                foreach (var root in roots)
                {
                    var match = FindNodeByName(root, name);
                    if (match != null)
                    {
                        return match;
                    }
                }

                return null;
            }

            public static Dictionary<string, GameObjectNode> ResolveFbxNodes(
                IReadOnlyList<GameObjectNode?> treeNodes,
                IReadOnlyList<string> containers,
                IReadOnlyList<GameObjectNode> roots)
            {
                var fbxNodes = new Dictionary<string, GameObjectNode>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < treeNodes.Count; i++)
                {
                    var treeNode = treeNodes[i];
                    if (treeNode?.GameObject == null)
                    {
                        continue;
                    }

                    var fbxContainer = GetFbxContainerPath(containers[i]);
                    if (fbxContainer == null)
                    {
                        continue;
                    }

                    fbxNodes.TryAdd(fbxContainer, GetFbxRootNode(treeNode, fbxContainer));
                }

                for (int i = 0; i < treeNodes.Count; i++)
                {
                    var fbxContainer = GetFbxContainerPath(containers[i]);
                    if (fbxContainer == null || fbxNodes.ContainsKey(fbxContainer))
                    {
                        continue;
                    }

                    var node = FindSceneNodeByName(roots, Path.GetFileNameWithoutExtension(fbxContainer));
                    if (node?.GameObject != null)
                    {
                        fbxNodes[fbxContainer] = node;
                    }
                }

                return fbxNodes;
            }

            public static List<GameObjectNode> Search(IReadOnlyList<GameObjectNode> roots, string query)
            {
                var results = new List<GameObjectNode>();
                foreach (var root in roots)
                {
                    SearchNode(root, query, results);
                }

                return results;
            }

            private static void SearchNode(GameObjectNode node, string query, List<GameObjectNode> results)
            {
                if (node.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(node);
                }

                foreach (var child in node.Children)
                {
                    SearchNode(child, query, results);
                }
            }
        }

        private static Dictionary<string, GameObjectNode> ResolveIndexed(
            IReadOnlyList<GameObjectNode?> treeNodes,
            IReadOnlyList<string> containers,
            IReadOnlyList<GameObjectNode> roots)
        {
            var resolved = containers.Select(c => MainWindow.ResolveFbxContainerFast(c)).ToArray();
            return FbxSceneNodeIndex.ResolveFbxNodes(treeNodes, resolved, roots);
        }

        private static void AssertSameMapping(Dictionary<string, GameObjectNode> expected, Dictionary<string, GameObjectNode> actual)
        {
            Assert.Equal(expected.Count, actual.Count);
            foreach (var (key, node) in expected)
            {
                Assert.True(actual.TryGetValue(key, out var actualNode), $"missing key '{key}'");
                Assert.Same(node, actualNode);
            }
        }

        /// <summary>Wide + moderately deep random hierarchy split under file nodes, with a small name pool to force duplicates.</summary>
        private static List<GameObjectNode> BuildRandomTree(int nodeCount, int seed, string[] namePool, out List<GameObjectNode> allGoNodes)
        {
            var random = new Random(seed);
            var roots = new List<GameObjectNode>();
            allGoNodes = new List<GameObjectNode>(nodeCount);
            var fileCount = Math.Max(1, nodeCount / 5000);
            for (int f = 0; f < fileCount; f++)
            {
                roots.Add(FileNode($"file_{f}.assets"));
            }

            for (int i = 0; i < nodeCount; i++)
            {
                var name = namePool[random.Next(namePool.Length)];
                if (random.Next(4) == 0)
                {
                    name = name.ToUpperInvariant();
                }

                var node = GoNode(name);
                var pick = random.Next(20);
                GameObjectNode parent = allGoNodes.Count == 0 || pick == 0
                    ? roots[random.Next(roots.Count)]
                    : pick < 9
                        ? allGoNodes[random.Next(allGoNodes.Count)]
                        : allGoNodes[random.Next(Math.Max(0, allGoNodes.Count - 64), allGoNodes.Count)];
                parent.AddChild(node);
                allGoNodes.Add(node);
            }

            return roots;
        }

        // ------------------------------------------------------------------ FBX container fast path

        [Theory]
        [InlineData("")]
        [InlineData("assets/characters/hero.prefab")]
        [InlineData("assets/characters/hero.fbx")]
        [InlineData("Assets/Characters/Hero.FBX/Body")]
        [InlineData("assets\\models\\tree.Fbx\\leaf.fbx\\mesh")]
        [InlineData("assets//double//slash.fbx")]
        [InlineData("assets/notfbx.fbxmeta/inner")]
        [InlineData("assets/.fbx/x")]
        [InlineData("/leading/slash.fbx")]
        [InlineData("assets/name.fbx.bak/sub")]
        public void ResolveFbxContainerFast_MatchesLegacyContainerParsing(string container)
        {
            Assert.Equal(Legacy.GetFbxContainerPath(container), MainWindow.ResolveFbxContainerFast(container));
        }

        // ------------------------------------------------------------------ FBX linking parity (edge cases)

        [Fact]
        public void FbxLinking_DuplicateNames_FirstPreOrderNodeAcrossRootsWins()
        {
            var fileA = FileNode("a.assets");
            var fileB = FileNode("b.assets");
            var aRoot = Add(fileA, GoNode("Env"));
            var deepFirst = Add(Add(aRoot, GoNode("Group")), GoNode("Hero"));
            var shallowLater = Add(aRoot, GoNode("hero"));
            var otherRoot = Add(fileB, GoNode("HERO"));
            var roots = new List<GameObjectNode> { fileA, fileB };

            var treeNodes = new GameObjectNode?[] { null, null };
            var containers = new[] { "assets/models/Hero.fbx/mesh", "assets/models/HERO.FBX/anim" };

            var expected = Legacy.ResolveFbxNodes(treeNodes, containers, roots);
            var actual = ResolveIndexed(treeNodes, containers, roots);
            AssertSameMapping(expected, actual);
            Assert.Single(actual);
            Assert.Same(deepFirst, actual.Values.Single());
            Assert.NotSame(shallowLater, actual.Values.Single());
            Assert.NotSame(otherRoot, actual.Values.Single());
        }

        [Fact]
        public void FbxLinking_SkipsNodesWithoutGameObject_AndMissingNodesStayUnlinked()
        {
            var file = FileNode("Hero");
            var nonGo = Add(file, new GameObjectNode { Name = "Hero" });
            var goMatch = Add(nonGo, GoNode("hero"));
            var roots = new List<GameObjectNode> { file };

            var treeNodes = new GameObjectNode?[] { null, null, null };
            var containers = new[] { "a/Hero.fbx/x", "a/Missing.fbx/y", "a/plain/prefab" };

            var expected = Legacy.ResolveFbxNodes(treeNodes, containers, roots);
            var actual = ResolveIndexed(treeNodes, containers, roots);
            AssertSameMapping(expected, actual);
            Assert.Same(goMatch, actual[Path.Combine("a", "Hero.fbx")]);
            Assert.False(actual.ContainsKey(Path.Combine("a", "Missing.fbx")));
        }

        [Fact]
        public void FbxLinking_NestedContainersUseFirstFbxSegment_AndPass1PrefersSubtreeNamedNode()
        {
            var file = FileNode("scene.unity");
            var topRoot = Add(file, GoNode("Prefab_Root"));
            var namedInSubtree = Add(Add(topRoot, GoNode("Rig")), GoNode("Outer"));
            var leaf = Add(namedInSubtree, GoNode("Leaf"));
            var unrelated = Add(file, GoNode("Lonely"));
            var lonelyChild = Add(unrelated, GoNode("Child"));
            var sceneWideOuter = Add(file, GoNode("Outer"));
            var roots = new List<GameObjectNode> { file };

            var treeNodes = new GameObjectNode?[] { leaf, lonelyChild, null, null };
            var containers = new[]
            {
                "assets/Outer.fbx/Inner.fbx/mesh",   // pass 1: named node inside leaf's top-level subtree
                "assets/NoSuch.fbx/mesh",            // pass 1: no named node -> falls back to top-level root
                "assets/outer.FBX/Inner.fbx/other",  // case-insensitive container key already linked
                "assets/sub/Outer.fbx/x",            // pass 2: scene-wide first pre-order 'Outer'
            };

            var expected = Legacy.ResolveFbxNodes(treeNodes, containers, roots);
            var actual = ResolveIndexed(treeNodes, containers, roots);
            AssertSameMapping(expected, actual);
            Assert.Same(namedInSubtree, actual[Path.Combine("assets", "Outer.fbx")]);
            Assert.Same(unrelated, actual[Path.Combine("assets", "NoSuch.fbx")]);
            Assert.Same(namedInSubtree, actual[Path.Combine("assets", "sub", "Outer.fbx")]);
            Assert.NotSame(sceneWideOuter, actual[Path.Combine("assets", "sub", "Outer.fbx")]);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(7)]
        [InlineData(42)]
        [InlineData(1337)]
        public void FbxLinking_RandomizedHierarchies_MatchLegacyExactly(int seed)
        {
            var pool = Enumerable.Range(0, 60).Select(i => $"Node{i}").ToArray();
            var roots = BuildRandomTree(4000, seed, pool, out var goNodes);
            var random = new Random(seed * 31 + 5);
            var treeNodes = new List<GameObjectNode?>();
            var containers = new List<string>();
            for (int i = 0; i < 1500; i++)
            {
                var fbxName = random.Next(5) == 0 ? $"Absent{random.Next(20)}" : pool[random.Next(pool.Length)];
                if (random.Next(3) == 0)
                {
                    fbxName = fbxName.ToLowerInvariant();
                }

                containers.Add(random.Next(6) switch
                {
                    0 => $"assets/props/{fbxName}.prefab",
                    1 => $"Assets\\Models\\{fbxName}.FBX\\Sub.fbx\\m{i}",
                    _ => $"assets/models/{fbxName}.fbx/m{i}",
                });
                treeNodes.Add(random.Next(3) == 0 ? goNodes[random.Next(goNodes.Count)] : null);
            }

            var expected = Legacy.ResolveFbxNodes(treeNodes, containers, roots);
            var actual = ResolveIndexed(treeNodes, containers, roots);
            AssertSameMapping(expected, actual);
            Assert.NotEmpty(actual);
        }

        // ------------------------------------------------------------------ FBX linking benchmark

        [Fact]
        public void FbxLinking_50kNodes_IndexedIsSubFiftyMsAndFarFasterThanLegacy()
        {
            var pool = Enumerable.Range(0, 5000).Select(i => $"Obj_{i}").ToArray();
            var roots = BuildRandomTree(50_000, 2024, pool, out _);

            // Worst case for the legacy DFS: containers whose FBX name is absent (full traversal each).
            var treeNodes = new GameObjectNode?[2000];
            var containers = new string[2000];
            for (int i = 0; i < containers.Length; i++)
            {
                containers[i] = i % 4 == 0 ? $"assets/models/Obj_{i}.fbx/mesh" : $"assets/models/Missing_{i}.fbx/mesh";
            }

            // Warm-up (JIT) on a tiny input.
            ResolveIndexed(treeNodes.Take(8).ToArray(), containers.Take(8).ToArray(), roots);

            var sw = Stopwatch.StartNew();
            var actual = ResolveIndexed(treeNodes, containers, roots);
            sw.Stop();
            var indexedMs = sw.Elapsed.TotalMilliseconds;

            sw.Restart();
            var expected = Legacy.ResolveFbxNodes(treeNodes, containers, roots);
            sw.Stop();
            var legacyMs = sw.Elapsed.TotalMilliseconds;

            AssertSameMapping(expected, actual);
            output.WriteLine($"FBX linking 50K nodes x 2000 containers: legacy {legacyMs:F1} ms, indexed {indexedMs:F2} ms, speedup {legacyMs / Math.Max(indexedMs, 0.001):F0}x");
            Assert.True(indexedMs < 50, $"indexed FBX linking took {indexedMs:F2} ms");
            Assert.True(indexedMs * 5 < legacyMs, $"expected large speedup: legacy {legacyMs:F1} ms vs indexed {indexedMs:F2} ms");
        }

        [Fact]
        public void FbxLinking_DeepChain_IsStackSafe()
        {
            var file = FileNode("deep.assets");
            var current = file;
            GameObjectNode? leaf = null;
            for (int i = 0; i < 100_000; i++)
            {
                current = Add(current, GoNode(i == 99_999 ? "DeepTarget" : $"Level{i}"));
                leaf = current;
            }

            var roots = new List<GameObjectNode> { file };
            var map = ResolveIndexed(new GameObjectNode?[] { leaf, null }, new[] { "a/Level0.fbx/x", "a/DeepTarget.fbx/y" }, roots);
            Assert.Same(file.Children[0], map[Path.Combine("a", "Level0.fbx")]);
            Assert.Same(leaf, map[Path.Combine("a", "DeepTarget.fbx")]);
        }

        // ------------------------------------------------------------------ tree search

        [Fact]
        public void TreeSearch_IterativeMatches_EqualLegacyRecursiveOrder()
        {
            var pool = new[] { "Body", "Head", "LeftHand", "RightHand", "Camera", "Light", "hand_ik" };
            var roots = BuildRandomTree(6000, 99, pool, out _);
            foreach (var query in new[] { "hand", "HAND", "o", "Camera", "zzz", "Body" })
            {
                var expected = Legacy.Search(roots, query);
                var actual = SceneTreeSearch.FindMatches(roots, query, CancellationToken.None);
                Assert.Equal(expected, actual);
            }

            // File nodes (no GameObject) are searchable exactly as before.
            Assert.Contains(roots[0], SceneTreeSearch.FindMatches(roots, "file_0", CancellationToken.None));
        }

        [Fact]
        public void TreeSearch_DeepChain_5000AndBeyond_IsStackSafe()
        {
            foreach (var depth in new[] { 5_000, 200_000 })
            {
                var file = FileNode("chain.assets");
                var current = file;
                for (int i = 0; i < depth; i++)
                {
                    current = Add(current, GoNode(i % 1000 == 999 ? $"Marker{i}" : $"N{i}"));
                }

                var matches = SceneTreeSearch.FindMatches(new[] { file }, "marker", CancellationToken.None);
                Assert.Equal(depth / 1000, matches.Length);
                Assert.Same(current, matches[^1]);
            }
        }

        [Fact]
        public async Task TreeSearch_50kNodes_AsyncSearchSubFiftyMs_AndRepeatCyclingIsCached()
        {
            var pool = Enumerable.Range(0, 500).Select(i => $"Mesh_{i}").ToArray();
            var roots = BuildRandomTree(50_000, 7, pool, out _);
            var session = new SceneTreeSearchSession();

            // Warm-up JIT + thread pool on a different query.
            Assert.NotNull(await session.SearchAsync(roots, "warm"));

            var sw = Stopwatch.StartNew();
            var matches = await session.SearchAsync(roots, "mesh_42");
            sw.Stop();
            var searchMs = sw.Elapsed.TotalMilliseconds;
            Assert.NotNull(matches);
            Assert.Equal(Legacy.Search(roots, "mesh_42"), matches!);
            Assert.NotEmpty(matches!);
            Assert.Equal(2, session.TraversalCount);

            // Cycling (Enter, Enter, ...) is served from the (query, tree generation) cache.
            sw.Restart();
            for (int i = 0; i < 100_000; i++)
            {
                Assert.True(session.TryGetCached(roots, "mesh_42", out var cached));
                Assert.Same(matches, cached);
            }

            var again = await session.SearchAsync(roots, "mesh_42");
            sw.Stop();
            Assert.Same(matches, again);
            Assert.Equal(2, session.TraversalCount);

            output.WriteLine($"Async tree search 50K nodes: {searchMs:F2} ms; 100K cached cycles: {sw.Elapsed.TotalMilliseconds:F2} ms");
            Assert.True(searchMs < 50, $"async 50K-node search took {searchMs:F2} ms");

            // Tree generation change (root added / list swapped) invalidates the cache.
            roots.Add(FileNode("mesh_42_extra"));
            Assert.False(session.TryGetCached(roots, "mesh_42", out _));
            Assert.False(session.TryGetCached(new List<GameObjectNode>(roots), "mesh_42", out _));
            var refreshed = await session.SearchAsync(roots, "mesh_42");
            Assert.Equal(matches!.Length + 1, refreshed!.Length);
        }

        [Fact]
        public async Task TreeSearch_RapidQueryChanges_CancelSupersededSearches()
        {
            var pool = new[] { "Alpha", "Beta", "Gamma", "Delta" };
            var roots = BuildRandomTree(300_000, 3, pool, out _);
            var session = new SceneTreeSearchSession();

            var first = session.SearchAsync(roots, "alp");
            var second = session.SearchAsync(roots, "alph");
            var third = session.SearchAsync(roots, "alpha");

            Assert.Null(await first);
            Assert.Null(await second);
            var final = await third;
            Assert.NotNull(final);
            Assert.Equal(Legacy.Search(roots, "alpha"), final!);
            Assert.True(session.TryGetCached(roots, "alpha", out _));
            Assert.False(session.TryGetCached(roots, "alp", out _));
            Assert.False(session.IsSearching);

            // Invalidate (query text changed) cancels the in-flight search and drops the cache.
            var pending = session.SearchAsync(roots, "gam");
            session.Invalidate();
            Assert.Null(await pending);
            Assert.False(session.TryGetCached(roots, "alpha", out _));
            Assert.False(session.IsSearching);
        }

        [Fact]
        public async Task TreeSearch_SameQueryWhileInFlight_JoinsSingleTraversal()
        {
            var roots = BuildRandomTree(100_000, 11, new[] { "Root", "Bone", "Mesh" }, out _);
            var session = new SceneTreeSearchSession();

            var a = session.SearchAsync(roots, "bone");
            var b = session.SearchAsync(roots, "bone");
            Assert.Same(a, b);
            var result = await a;
            Assert.NotNull(result);
            Assert.Same(result, await b);
            Assert.Equal(1, session.TraversalCount);
        }

        [Fact]
        public void TreeSearch_PreCancelledToken_Throws()
        {
            var roots = BuildRandomTree(5000, 5, new[] { "A", "B" }, out _);
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => SceneTreeSearch.FindMatches(roots, "a", cts.Token));
        }
    }
}
