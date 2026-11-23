using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AssetStudio;
using AssetStudio.Avalonia;
using AssetStudio.Avalonia.Services;
using AssetStudio.Avalonia.ViewModels;
using Xunit;

namespace AssetStudio.Tests
{
    public class Phase1InspectorTests
    {
        private static readonly Regex AvaloniaUsingRegex = new(
            @"^\s*using\s+(?:global::)?Avalonia(?:\.[A-Za-z0-9_]+)?\s*;",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);

        private static readonly Regex AxamlClickAttributeRegex = new(
            @"\bClick\s*=",
            RegexOptions.CultureInvariant);

        #region MonoBehaviourTreeBuilder Tests

        [Fact]
        public void MonoBehaviourTreeBuilder_BuildsNestedObjectsArraysScalarsAndResolvesPPtrs()
        {
            var fields = new OrderedDictionary
            {
                ["m_Name"] = "WeaponConfig",
                ["damage"] = 42,
                ["critChance"] = 0.25f,
                ["isEnabled"] = true,
                ["rawBytes"] = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF },
                ["offset"] = new OrderedDictionary { ["x"] = 1.5f, ["y"] = 2.0f, ["z"] = -3.25f },
                ["tint"] = new OrderedDictionary { ["r"] = 1f, ["g"] = 0.5f, ["b"] = 0.25f, ["a"] = 1f },
                ["iconTexture"] = new OrderedDictionary { ["m_FileID"] = 0, ["m_PathID"] = 1001L },
                ["nullRef"] = new OrderedDictionary { ["m_FileID"] = 0, ["m_PathID"] = 0L },
                ["unresolvedRef"] = new OrderedDictionary { ["m_FileID"] = 2, ["m_PathID"] = 9999L },
                ["materials"] = new List<object>
                {
                    new OrderedDictionary { ["m_FileID"] = 1, ["m_PathID"] = 2001L },
                    new OrderedDictionary { ["m_FileID"] = 1, ["m_PathID"] = 2002L }
                },
                ["nested"] = new OrderedDictionary
                {
                    ["audioClip"] = new OrderedDictionary { ["m_FileID"] = 0, ["m_PathID"] = 3001L },
                    ["label"] = "HeavySword"
                }
            };

            var resolverCalls = 0;
            AssetReferenceItem? Resolver(int fileId, long pathId)
            {
                resolverCalls++;
                return (fileId, pathId) switch
                {
                    (0, 1001L) => new AssetReferenceItem
                    {
                        AssetUniqueId = "sharedassets0.assets#1001",
                        Name = "Sword_Icon",
                        AssetType = ClassIDType.Texture2D,
                        TypeName = "Texture2D",
                        PathId = 1001L,
                        SerializedFileName = "sharedassets0.assets"
                    },
                    (1, 2001L) => new AssetReferenceItem
                    {
                        AssetUniqueId = "sharedassets1.assets#2001",
                        Name = "Blade_Mat",
                        AssetType = ClassIDType.Material,
                        TypeName = "Material",
                        PathId = 2001L,
                        SerializedFileName = "sharedassets1.assets"
                    },
                    (1, 2002L) => new AssetReferenceItem
                    {
                        AssetUniqueId = "sharedassets1.assets#2002",
                        Name = "Grip_Mat",
                        AssetType = ClassIDType.Material,
                        TypeName = "Material",
                        PathId = 2002L,
                        SerializedFileName = "sharedassets1.assets"
                    },
                    (0, 3001L) => new AssetReferenceItem
                    {
                        AssetUniqueId = "sharedassets0.assets#3001",
                        Name = "Swing_SFX",
                        AssetType = ClassIDType.AudioClip,
                        TypeName = "AudioClip",
                        PathId = 3001L,
                        SerializedFileName = "sharedassets0.assets"
                    },
                    _ => null
                };
            }

            var result = MonoBehaviourTreeBuilder.Build(fields, Resolver, scriptTitle: "Game.WeaponConfig");

            Assert.Equal("Game.WeaponConfig", result.ScriptTitle);
            Assert.False(result.WasTruncated);
            Assert.Equal(5, result.PPtrCount); // 4 resolved + 1 fallback non-zero PPtr; nullRef (PathID=0) is excluded
            Assert.Equal(5, resolverCalls);
            Assert.Equal(12, result.Roots.Count);
            Assert.Contains("5 PPtr links", result.SummaryText);

            var nameNode = result.Roots.Single(n => n.Name == "m_Name");
            Assert.Equal("\"WeaponConfig\"", nameNode.ValueText);
            Assert.Equal("string", nameNode.TypeHint);

            var bytesNode = result.Roots.Single(n => n.Name == "rawBytes");
            Assert.Equal("byte[]", bytesNode.TypeHint);
            Assert.Contains("DE AD BE EF", bytesNode.ValueText);

            var offsetNode = result.Roots.Single(n => n.Name == "offset");
            Assert.Equal("(1.5, 2, -3.25)", offsetNode.ValueText);
            Assert.Equal(3, offsetNode.Children.Count);

            var tintNode = result.Roots.Single(n => n.Name == "tint");
            Assert.Equal("RGBA(1, 0.5, 0.25, 1)", tintNode.ValueText);

            var iconNode = result.Roots.Single(n => n.Name == "iconTexture");
            Assert.True(iconNode.IsPPtr);
            Assert.True(iconNode.IsNavigablePPtr);
            Assert.Equal("→ [Texture2D] Sword_Icon", iconNode.PPtrDisplayLabel);
            Assert.Equal("sharedassets0.assets#1001", iconNode.PPtrTarget!.AssetUniqueId);

            var nullRefNode = result.Roots.Single(n => n.Name == "nullRef");
            Assert.False(nullRefNode.IsPPtr);
            Assert.Equal("None (null)", nullRefNode.ValueText);

            var unresolvedNode = result.Roots.Single(n => n.Name == "unresolvedRef");
            Assert.True(unresolvedNode.IsPPtr);
            Assert.True(unresolvedNode.IsNavigablePPtr);
            Assert.Equal(9999L, unresolvedNode.PPtrTarget!.PathId);
            Assert.Contains("9999", unresolvedNode.PPtrDisplayLabel);

            var materialsNode = result.Roots.Single(n => n.Name == "materials");
            Assert.Equal("[2 items]", materialsNode.ValueText);
            Assert.Equal(2, materialsNode.Children.Count);
            Assert.Equal("→ [Material] Blade_Mat", materialsNode.Children[0].PPtrDisplayLabel);
            Assert.Equal("→ [Material] Grip_Mat", materialsNode.Children[1].PPtrDisplayLabel);

            // Search filtering preserves ancestor chain and auto-expands matching branches
            var filteredBySfx = MonoBehaviourTreeBuilder.FilterTree(result.Roots, "Swing_SFX");
            var matchedNested = Assert.Single(filteredBySfx);
            Assert.Equal("nested", matchedNested.Name);
            Assert.True(matchedNested.IsExpanded);
            var matchedAudio = Assert.Single(matchedNested.Children);
            Assert.Equal("audioClip", matchedAudio.Name);
            Assert.Equal("Swing_SFX", matchedAudio.PPtrTarget!.Name);

            // Multi-term case-insensitive search
            var filteredMulti = MonoBehaviourTreeBuilder.FilterTree(result.Roots, "material grip");
            var matchedMats = Assert.Single(filteredMulti);
            Assert.Equal("materials", matchedMats.Name);
            Assert.True(matchedMats.IsExpanded);
            var matchedGrip = Assert.Single(matchedMats.Children);
            Assert.Equal("Grip_Mat", matchedGrip.PPtrTarget!.Name);

            // Clearing filter returns exact original roots
            Assert.Same(result.Roots, MonoBehaviourTreeBuilder.FilterTree(result.Roots, "   "));

            // ExpandAll / CollapseAll toggles all container nodes
            MonoBehaviourTreeBuilder.SetExpandedRecursive(result.Roots, isExpanded: false);
            Assert.All(result.Roots.Where(r => r.HasChildren), r => Assert.False(r.IsExpanded));
            MonoBehaviourTreeBuilder.SetExpandedRecursive(result.Roots, isExpanded: true);
            Assert.All(result.Roots.Where(r => r.HasChildren), r => Assert.True(r.IsExpanded));
        }

        [Fact]
        public void MonoBehaviourTreeBuilder_EnforcesArrayDepthAndTotalNodeCapsCleanly()
        {
            var hugeArray = Enumerable.Range(0, 500).Cast<object>().ToList();

            OrderedDictionary deepRoot = new OrderedDictionary();
            var cursor = deepRoot;
            for (var i = 0; i < 10; i++)
            {
                var next = new OrderedDictionary();
                cursor[$"level_{i}"] = next;
                cursor = next;
            }
            cursor["leaf"] = 123;

            var fields = new OrderedDictionary
            {
                ["hugeArray"] = hugeArray,
                ["deepTree"] = deepRoot
            };

            var options = new MonoBehaviourTreeBuildOptions
            {
                MaxDepth = 4,
                MaxArrayElements = 10,
                MaxTotalNodes = 25
            };

            var result = MonoBehaviourTreeBuilder.Build(fields, options: options);

            Assert.True(result.WasTruncated);
            Assert.True(result.TotalNodeCount <= options.MaxTotalNodes);
            Assert.Contains("(capped)", result.SummaryText);

            var arrNode = result.Roots.Single(n => n.Name == "hugeArray");
            Assert.Equal("[500 items]", arrNode.ValueText);
            Assert.Equal(11, arrNode.Children.Count); // 10 items + 1 truncation notice
            Assert.Equal("truncated", arrNode.Children[^1].TypeHint);
            Assert.Contains("490 more items truncated", arrNode.Children[^1].ValueText);

            // Walk deepTree and verify max depth stopped cleanly
            var deepNode = result.Roots.Single(n => n.Name == "deepTree");
            var depth = 0;
            var cur = deepNode;
            while (cur.HasChildren)
            {
                cur = cur.Children[0];
                depth++;
            }
            Assert.True(depth <= options.MaxDepth);
            Assert.Contains("max depth reached", cur.ValueText);
        }

        #endregion

        #region GameObjectInspectorService Tests

        [Fact]
        public void GameObjectInspectorService_ExtractsTransformComponentsMonoScriptAndLinkedAssets()
        {
            var file = CreateSyntheticSerializedFile("level0");

            var parentGo = CreateObjectInFile<GameObject>(file, 10, ClassIDType.GameObject, "SceneRoot");
            var parentTr = CreateObjectInFile<Transform>(file, 11, ClassIDType.Transform, string.Empty);
            parentTr.m_GameObject = CreatePPtr<GameObject>(file, 10);
            parentGo.m_Transform = parentTr;

            var heroGo = CreateObjectInFile<GameObject>(file, 20, ClassIDType.GameObject, "HeroCharacter");
            heroGo.m_IsActive = true;
            heroGo.m_Layer = 8;
            heroGo.m_Tag = 5;

            var heroTr = CreateObjectInFile<Transform>(file, 21, ClassIDType.Transform, string.Empty);
            heroTr.m_GameObject = CreatePPtr<GameObject>(file, 20);
            heroTr.m_LocalPosition = new Vector3(1.5f, 2.0f, -4.0f);
            heroTr.m_LocalRotation = new Quaternion(0f, 0f, 0f, 1f);
            heroTr.m_LocalScale = new Vector3(1f, 1.25f, 1f);
            heroTr.m_Father = CreatePPtr<Transform>(file, 11);
            heroTr.m_Children = new[] { CreatePPtr<Transform>(file, 99), CreatePPtr<Transform>(file, 100) };

            var mesh = CreateObjectInFile<Mesh>(file, 30, ClassIDType.Mesh, "HeroBody_Mesh");
            mesh.m_VertexCount = 1234;
            var mat0 = CreateObjectInFile<Material>(file, 31, ClassIDType.Material, "HeroArmor_Mat");
            var mat1 = CreateObjectInFile<Material>(file, 32, ClassIDType.Material, "HeroCape_Mat");

            var meshFilter = CreateObjectInFile<MeshFilter>(file, 22, ClassIDType.MeshFilter, string.Empty);
            meshFilter.m_GameObject = CreatePPtr<GameObject>(file, 20);
            meshFilter.m_Mesh = CreatePPtr<Mesh>(file, 30);

            var meshRenderer = CreateObjectInFile<MeshRenderer>(file, 23, ClassIDType.MeshRenderer, string.Empty);
            meshRenderer.m_GameObject = CreatePPtr<GameObject>(file, 20);
            meshRenderer.m_Enabled = true;
            meshRenderer.m_SortingOrder = 3;
            meshRenderer.m_Materials = new[] { CreatePPtr<Material>(file, 31), CreatePPtr<Material>(file, 32) };

            var avatar = CreateObjectInFile<Avatar>(file, 33, ClassIDType.Avatar, "Hero_Avatar");
            var controller = CreateObjectInFile<AnimatorController>(file, 34, ClassIDType.AnimatorController, "Hero_AnimCtrl");
            var animator = CreateObjectInFile<Animator>(file, 24, ClassIDType.Animator, string.Empty);
            animator.m_GameObject = CreatePPtr<GameObject>(file, 20);
            animator.m_Enabled = 1;
            animator.m_Avatar = CreatePPtr<Avatar>(file, 33);
            animator.m_Controller = CreatePPtr<RuntimeAnimatorController>(file, 34);

            var monoScript = CreateObjectInFile<MonoScript>(file, 35, ClassIDType.MonoScript, "PlayerController");
            monoScript.m_Namespace = "Gameplay.Combat";
            monoScript.m_ClassName = "PlayerController";
            monoScript.m_AssemblyName = "Assembly-CSharp.dll";

            var monoBehaviour = CreateObjectInFile<MonoBehaviour>(file, 25, ClassIDType.MonoBehaviour, string.Empty);
            monoBehaviour.m_GameObject = CreatePPtr<GameObject>(file, 20);
            monoBehaviour.m_Enabled = 0;
            monoBehaviour.m_Script = CreatePPtr<MonoScript>(file, 35);

            // Also test an unresolved Lazy Mode component via ProjectIndex!
            var projectIndex = new ProjectIndex();
            projectIndex.AddHandle(new AssetHandle
            {
                UniqueID = AssetHandle.BuildUniqueID(file, 26),
                SerializedFileName = "level0",
                PathID = 26,
                Type = ClassIDType.AudioSource,
                Name = "HeroVoice_AudioSource"
            });

            heroGo.m_Transform = heroTr;
            heroGo.m_Components = new[]
            {
                CreatePPtr<Component>(file, 21),
                CreatePPtr<Component>(file, 22),
                CreatePPtr<Component>(file, 23),
                CreatePPtr<Component>(file, 24),
                CreatePPtr<Component>(file, 25),
                CreatePPtr<Component>(file, 26)
            };

            var rootNode = new GameObjectNode { Name = "SceneRoot", GameObject = parentGo };
            var heroNode = new GameObjectNode { Name = "HeroCharacter", GameObject = heroGo };
            rootNode.AddChild(heroNode);

            var snapshot = GameObjectInspectorService.BuildFromNode(heroNode, projectIndex: projectIndex);

            Assert.True(snapshot.HasGameObjectData);
            Assert.Equal("HeroCharacter", snapshot.Name);
            Assert.True(snapshot.IsActive);
            Assert.Equal("Active", snapshot.ActiveStateText);
            Assert.Equal(8, snapshot.Layer);
            Assert.Equal((ushort)5, snapshot.Tag);
            Assert.Equal("SceneRoot / HeroCharacter", snapshot.HierarchyPath);
            Assert.Equal("level0", snapshot.SourceFileName);
            Assert.Equal(20L, snapshot.PathId);
            Assert.True(snapshot.HasGameObjectTarget);
            Assert.True(snapshot.HasLazyNotice); // 1 unresolved Lazy component

            // Transform summary
            Assert.True(snapshot.Transform.HasTransform);
            Assert.False(snapshot.Transform.IsRectTransform);
            Assert.Equal("(1.5, 2, -4)", snapshot.Transform.PositionText);
            Assert.Equal("(1, 1.25, 1)", snapshot.Transform.ScaleText);
            Assert.Equal(2, snapshot.Transform.ChildCount);
            Assert.Equal("SceneRoot", snapshot.Transform.ParentName);
            Assert.True(snapshot.Transform.HasParentTarget);
            Assert.Equal(10L, snapshot.Transform.ParentTarget!.PathId);

            // Components ordered & enriched
            Assert.Equal(6, snapshot.Components.Count);

            var mfComp = snapshot.Components[1];
            Assert.Equal("MeshFilter", mfComp.TypeName);
            Assert.Single(mfComp.LinkedAssets);
            Assert.Equal("HeroBody_Mesh", mfComp.LinkedAssets[0].Name);

            var mrComp = snapshot.Components[2];
            Assert.Equal("MeshRenderer", mrComp.TypeName);
            Assert.True(mrComp.IsEnabled);
            Assert.Equal(2, mrComp.LinkedAssets.Count);
            Assert.Equal("HeroArmor_Mat", mrComp.LinkedAssets[0].Name);
            Assert.Equal("HeroCape_Mat", mrComp.LinkedAssets[1].Name);

            var animComp = snapshot.Components[3];
            Assert.Equal("Animator", animComp.TypeName);
            Assert.Equal(2, animComp.LinkedAssets.Count);
            Assert.Contains(animComp.LinkedAssets, a => a.Name == "Hero_Avatar" && a.AssetType == ClassIDType.Avatar);
            Assert.Contains(animComp.LinkedAssets, a => a.Name == "Hero_AnimCtrl" && a.AssetType == ClassIDType.AnimatorController);

            var mbComp = snapshot.Components[4];
            Assert.Equal("MonoBehaviour", mbComp.TypeName);
            Assert.Equal("Gameplay.Combat.PlayerController (MonoBehaviour)", mbComp.DisplayTitle);
            Assert.Equal("Assembly-CSharp.dll", mbComp.Subtitle);
            Assert.False(mbComp.IsEnabled);
            Assert.Equal("Disabled", mbComp.EnabledBadge);
            Assert.True(mbComp.HasComponentTarget);
            Assert.Single(mbComp.LinkedAssets);
            Assert.Equal("PlayerController", mbComp.LinkedAssets[0].Name);
            Assert.True(mbComp.MatchesFilter("Combat"));
            Assert.True(mbComp.MatchesFilter("Assembly-CSharp"));

            var lazyAudioComp = snapshot.Components[5];
            Assert.Equal("AudioSource", lazyAudioComp.TypeName);
            Assert.Equal("HeroVoice_AudioSource", lazyAudioComp.DisplayTitle);
            Assert.True(lazyAudioComp.HasComponentTarget);
            Assert.Equal(26L, lazyAudioComp.PathId);
        }

        [Fact]
        public void GameObjectInspectorService_HandlesSceneContainerNodeWithoutGameObjectGracefully()
        {
            var container = new GameObjectNode { Name = "sharedassets0.assets" };
            container.AddChild(new GameObjectNode { Name = "ChildA" });
            container.AddChild(new GameObjectNode { Name = "ChildB" });

            var snapshot = GameObjectInspectorService.BuildFromNode(container);

            Assert.False(snapshot.HasGameObjectData);
            Assert.Equal("sharedassets0.assets", snapshot.Name);
            Assert.Equal("Scene Node", snapshot.ActiveStateText);
            Assert.Equal(2, snapshot.ChildCount);
            Assert.True(snapshot.HasLazyNotice);
            Assert.False(snapshot.Transform.HasTransform);
            Assert.Empty(snapshot.Components);
        }

        [Fact]
        public void GameObjectInspectorService_ExtractsRectTransformDetails()
        {
            var file = CreateSyntheticSerializedFile("ui.assets");
            var uiGo = CreateObjectInFile<GameObject>(file, 50, ClassIDType.GameObject, "HealthBar");
            uiGo.m_IsActive = true;

            var rectTr = CreateObjectInFile<RectTransform>(file, 51, ClassIDType.RectTransform, string.Empty);
            rectTr.m_GameObject = CreatePPtr<GameObject>(file, 50);
            rectTr.m_LocalPosition = new Vector3(10f, 20f, 0f);
            rectTr.m_LocalRotation = new Quaternion(0f, 0f, 0f, 1f);
            rectTr.m_LocalScale = new Vector3(1f, 1f, 1f);
            rectTr.m_AnchoredPosition = new Vector2(12f, -34f);
            rectTr.m_SizeDelta = new Vector2(200f, 32f);
            rectTr.m_AnchorMin = new Vector2(0f, 1f);
            rectTr.m_AnchorMax = new Vector2(0f, 1f);
            rectTr.m_Pivot = new Vector2(0.5f, 0.5f);

            uiGo.m_Transform = rectTr;
            uiGo.m_Components = new[] { CreatePPtr<Component>(file, 51) };

            var snapshot = GameObjectInspectorService.BuildFromGameObject(uiGo);

            Assert.True(snapshot.Transform.HasTransform);
            Assert.True(snapshot.Transform.IsRectTransform);
            Assert.Equal("RectTransform", snapshot.Transform.HeaderTitle);
            Assert.True(snapshot.Transform.HasRectSummary);
            Assert.Contains("200", snapshot.Transform.RectSummaryText);
            Assert.Contains("-34", snapshot.Transform.RectSummaryText);
        }

        #endregion

        #region AssetNavigationHistory Tests

        [Fact]
        public void AssetNavigationHistory_SupportsBackForwardDedupBranchTruncationAndCapacity()
        {
            var history = new AssetNavigationHistory(capacity: 3);

            var e1 = new AssetNavigationEntry { AssetUniqueId = "f#1", Name = "GameObjectA", AssetType = ClassIDType.GameObject, PathId = 1 };
            var e2 = new AssetNavigationEntry { AssetUniqueId = "f#2", Name = "MonoBehaviourB", AssetType = ClassIDType.MonoBehaviour, PathId = 2 };
            var e3 = new AssetNavigationEntry { AssetUniqueId = "f#3", Name = "MaterialC", AssetType = ClassIDType.Material, PathId = 3 };
            var e4 = new AssetNavigationEntry { AssetUniqueId = "f#4", Name = "TextureD", AssetType = ClassIDType.Texture2D, PathId = 4 };
            var e5 = new AssetNavigationEntry { AssetUniqueId = "f#5", Name = "ShaderE", AssetType = ClassIDType.Shader, PathId = 5 };

            Assert.False(history.CanGoBack);
            Assert.False(history.CanGoForward);
            Assert.False(history.Push(null));
            Assert.False(history.Push(new AssetNavigationEntry()));

            // Push sequence + consecutive dedup
            Assert.True(history.Push(e1));
            Assert.False(history.Push(e1)); // consecutive duplicate ignored
            Assert.Equal(1, history.Count);
            Assert.False(history.CanGoBack);

            Assert.True(history.Push(e2));
            Assert.True(history.Push(e3));
            Assert.Equal(3, history.Count);
            Assert.True(history.CanGoBack);
            Assert.False(history.CanGoForward);
            Assert.Equal(e2, history.PreviousEntry);

            // Step back twice
            Assert.True(history.TryGoBack(out var back1));
            Assert.Equal(e2, back1);
            Assert.True(history.CanGoBack);
            Assert.True(history.CanGoForward);
            Assert.Equal(e3, history.NextEntry);

            Assert.True(history.TryGoBack(out var back2));
            Assert.Equal(e1, back2);
            Assert.False(history.CanGoBack);
            Assert.True(history.CanGoForward);
            Assert.False(history.TryGoBack(out _));

            // Step forward once
            Assert.True(history.TryGoForward(out var fwd1));
            Assert.Equal(e2, fwd1);
            Assert.True(history.CanGoForward);

            // Push new branch truncates e3
            Assert.True(history.Push(e4));
            Assert.False(history.CanGoForward);
            Assert.Equal(3, history.Count); // e1, e2, e4
            Assert.Equal(e4, history.Current);

            // Push e5 exceeds capacity (3) -> trims oldest (e1) -> leaves e2, e4, e5
            Assert.True(history.Push(e5));
            Assert.Equal(3, history.Count);
            Assert.Equal(e5, history.Current);
            Assert.True(history.TryGoBack(out var bA));
            Assert.Equal(e4, bA);
            Assert.True(history.TryGoBack(out var bB));
            Assert.Equal(e2, bB);
            Assert.False(history.CanGoBack);

            // Round-trip to AssetReferenceItem
            var refItem = e4.ToReferenceItem();
            Assert.Equal("f#4", refItem.AssetUniqueId);
            Assert.Equal("TextureD", refItem.Name);
            Assert.Equal(ClassIDType.Texture2D, refItem.AssetType);
            Assert.True(refItem.IsNavigable);

            // Clear resets everything
            history.Clear();
            Assert.Equal(0, history.Count);
            Assert.Null(history.Current);
            Assert.False(history.CanGoBack);
            Assert.False(history.CanGoForward);
        }

        #endregion

        #region PreviewSessionViewModel & MainWindowViewModel Phase 1 Tests

        [Fact]
        public async Task PreviewSessionAndMainWindowViewModel_Phase1TransitionsAndDoubleResetInvariants()
        {
            using var mainVm = new MainWindowViewModel();
            var session = mainVm.PreviewSession;

            // 1) MonoBehaviour Tree / JSON viewer transitions
            var mbFields = new OrderedDictionary
            {
                ["title"] = "MainQuest",
                ["icon"] = new OrderedDictionary { ["m_FileID"] = 0, ["m_PathID"] = 777L },
                ["nested"] = new OrderedDictionary { ["reward"] = "GoldSword" }
            };
            var treeResult = MonoBehaviourTreeBuilder.Build(
                mbFields,
                (_, pathId) => new AssetReferenceItem
                {
                    AssetUniqueId = $"sharedassets0.assets#{pathId}",
                    Name = "QuestIcon",
                    AssetType = ClassIDType.Sprite,
                    TypeName = "Sprite",
                    PathId = pathId
                },
                scriptTitle: "Quests.MainQuestData");

            var mbHandle = new AssetHandle
            {
                UniqueID = "sharedassets0.assets#500",
                SerializedFileName = "sharedassets0.assets",
                PathID = 500,
                Type = ClassIDType.MonoBehaviour,
                Name = "MainQuestAsset"
            };
            var mbAsset = new AssetItem(mbHandle, 0);

            session.ShowMonoBehaviourTree(mbAsset, treeResult, "{\"title\":\"MainQuest\"}");
            Assert.True(session.HasMonoBehaviourTree);
            Assert.True(session.IsMonoBehaviourPanelVisible);
            Assert.True(session.IsMonoBehaviourTreeViewVisible);
            Assert.False(session.IsMonoBehaviourJsonViewVisible);
            Assert.False(session.IsTextSurfaceVisible);
            Assert.Equal("Quests.MainQuestData", session.MonoBehaviourScriptTitle);
            Assert.Equal(1, session.MonoBehaviourPPtrCount);
            Assert.Equal(3, session.MonoBehaviourTreeRoots.Count);

            // Filter tree via ViewModel
            session.MonoBehaviourFilterText = "GoldSword";
            var filteredRoot = Assert.Single(session.MonoBehaviourTreeRoots);
            Assert.Equal("nested", filteredRoot.Name);
            Assert.True(filteredRoot.IsExpanded);

            // Toggle to JSON view and back via MainWindowViewModel commands
            mainVm.ShowMonoBehaviourJsonCommand.Execute();
            Assert.False(session.IsMonoBehaviourTreeViewVisible);
            Assert.True(session.IsMonoBehaviourJsonViewVisible);
            mainVm.ShowMonoBehaviourTreeCommand.Execute();
            Assert.True(session.IsMonoBehaviourTreeViewVisible);
            Assert.False(session.IsMonoBehaviourJsonViewVisible);

            // ExpandAll / CollapseAll commands
            session.MonoBehaviourFilterText = string.Empty;
            mainVm.CollapseAllMonoBehaviourCommand.Execute();
            Assert.All(session.MonoBehaviourTreeRoots.Where(n => n.HasChildren), n => Assert.False(n.IsExpanded));
            mainVm.ExpandAllMonoBehaviourCommand.Execute();
            Assert.All(session.MonoBehaviourTreeRoots.Where(n => n.HasChildren), n => Assert.True(n.IsExpanded));

            // Clickable PPtr navigation command
            AssetReferenceItem? navigatedPPtr = null;
            mainVm.CommandHandlers.NavigateToPPtrAsync = target =>
            {
                navigatedPPtr = target;
                return Task.CompletedTask;
            };
            var iconNode = session.MonoBehaviourTreeRoots.Single(n => n.Name == "icon");
            await mainVm.NavigateToPPtrCommand.ExecuteAsync(iconNode.PPtrTarget);
            Assert.Same(iconNode.PPtrTarget, navigatedPPtr);

            // 2) GameObject Inspector transitions & stale request rejection
            _ = session.BeginGameObjectInspectorRequest(out var req1);
            _ = session.BeginGameObjectInspectorRequest(out var req2);
            var inspectorSnap = new GameObjectInspectorSnapshot
            {
                Name = "BossEnemy",
                HasGameObjectData = true,
                IsActive = true,
                Components = new[]
                {
                    new GameObjectComponentInfo { Index = 0, TypeName = "Transform", DisplayTitle = "Transform", Summary = "Pos (0,0,0)" },
                    new GameObjectComponentInfo
                    {
                        Index = 1,
                        TypeName = "MonoBehaviour",
                        DisplayTitle = "BossAI (MonoBehaviour)",
                        ComponentTarget = new AssetReferenceItem { AssetUniqueId = "level0#901", Name = "BossAI", AssetType = ClassIDType.MonoBehaviour, PathId = 901 }
                    }
                }
            };

            Assert.False(session.CompleteGameObjectInspectorRequest(req1, inspectorSnap));
            Assert.True(session.CompleteGameObjectInspectorRequest(req2, inspectorSnap));
            Assert.True(session.IsGameObjectInspectorVisible);
            Assert.False(session.IsMonoBehaviourPanelVisible); // GameObject inspector cleanly takes focus
            Assert.Equal(2, session.InspectorComponents.Count);

            session.InspectorFilterText = "BossAI";
            var filteredComp = Assert.Single(session.InspectorComponents);
            Assert.Equal("BossAI (MonoBehaviour)", filteredComp.DisplayTitle);

            // Navigating from GameObject Inspector dispatches target
            AssetReferenceItem? navigatedInspectorTarget = null;
            mainVm.CommandHandlers.NavigateToInspectorTargetAsync = target =>
            {
                navigatedInspectorTarget = target;
                return Task.CompletedTask;
            };
            await mainVm.NavigateToInspectorTargetCommand.ExecuteAsync(filteredComp.ComponentTarget);
            Assert.Same(filteredComp.ComponentTarget, navigatedInspectorTarget);

            // Switching back to Asset List preview (ActivateSurface) hides GameObject Inspector cleanly
            session.ActivateSurface(PreviewSurfaceKind.Texture);
            Assert.False(session.IsGameObjectInspectorVisible);
            Assert.False(session.HasMonoBehaviourTree);

            // 3) Back / Forward navigation history commands & CanExecuteChanged
            var backCanExecChanged = 0;
            var fwdCanExecChanged = 0;
            mainVm.NavigateBackCommand.CanExecuteChanged += (_, _) => backCanExecChanged++;
            mainVm.NavigateForwardCommand.CanExecuteChanged += (_, _) => fwdCanExecChanged++;

            Assert.False(mainVm.CanNavigateBack);
            Assert.False(mainVm.CanNavigateForward);
            Assert.False(mainVm.NavigateBackCommand.CanExecute());
            Assert.False(mainVm.NavigateForwardCommand.CanExecute());

            session.RecordNavigation(new AssetNavigationEntry { AssetUniqueId = "f#1", Name = "FirstAsset", AssetType = ClassIDType.GameObject, PathId = 1 });
            session.RecordNavigation(new AssetNavigationEntry { AssetUniqueId = "f#2", Name = "SecondAsset", AssetType = ClassIDType.Material, PathId = 2 });

            Assert.True(mainVm.CanNavigateBack);
            Assert.False(mainVm.CanNavigateForward);
            Assert.True(mainVm.NavigateBackCommand.CanExecute());
            Assert.Contains("FirstAsset", session.NavigateBackTooltip);
            Assert.True(backCanExecChanged > 0);

            await mainVm.NavigateBackCommand.ExecuteAsync();
            Assert.False(mainVm.CanNavigateBack);
            Assert.True(mainVm.CanNavigateForward);
            Assert.Contains("SecondAsset", session.NavigateForwardTooltip);
            Assert.True(fwdCanExecChanged > 0);

            await mainVm.NavigateForwardCommand.ExecuteAsync();
            Assert.True(mainVm.CanNavigateBack);
            Assert.False(mainVm.CanNavigateForward);

            // 4) Double Reset() emits ZERO PropertyChanged events!
            session.Reset();
            var firedProps = new List<string>();
            session.PropertyChanged += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.PropertyName))
                {
                    firedProps.Add(e.PropertyName);
                }
            };
            session.Reset();
            Assert.Empty(firedProps);
        }

        [Fact]
        public void Phase1Services_HaveZeroAvaloniaImports_AndMainWindowAxamlHasPureMvvmPhase1Panels()
        {
            var repoRoot = LocateRepositoryRoot();
            var pureServiceFiles = new[]
            {
                Path.Combine(repoRoot, "AssetStudio.Avalonia", "Services", "MonoBehaviourTreeBuilder.cs"),
                Path.Combine(repoRoot, "AssetStudio.Avalonia", "Services", "GameObjectInspectorService.cs"),
                Path.Combine(repoRoot, "AssetStudio.Avalonia", "Services", "AssetNavigationHistory.cs")
            };

            foreach (var file in pureServiceFiles)
            {
                Assert.True(File.Exists(file), $"Expected service file not found: {file}");
                var text = File.ReadAllText(file);
                Assert.False(AvaloniaUsingRegex.IsMatch(text), $"{Path.GetFileName(file)} must not import Avalonia.");
            }

            var axamlPath = Path.Combine(repoRoot, "AssetStudio.Avalonia", "MainWindow.axaml");
            var axaml = File.ReadAllText(axamlPath);
            Assert.Contains("x:Name=\"MonoBehaviourPreviewPanel\"", axaml, StringComparison.Ordinal);
            Assert.Contains("x:Name=\"MonoBehaviourTreeView\"", axaml, StringComparison.Ordinal);
            Assert.Contains("x:Name=\"GameObjectInspectorPanel\"", axaml, StringComparison.Ordinal);
            Assert.Contains("x:Name=\"NavigateBackButton\"", axaml, StringComparison.Ordinal);
            Assert.Contains("x:Name=\"NavigateForwardButton\"", axaml, StringComparison.Ordinal);
            Assert.Contains("NavigateToPPtrCommand", axaml, StringComparison.Ordinal);
            Assert.Contains("NavigateToInspectorTargetCommand", axaml, StringComparison.Ordinal);
            Assert.DoesNotMatch(AxamlClickAttributeRegex, axaml);
        }

        #endregion

        #region Synthetic Helpers

        private static T CreateUninitialized<T>() where T : class
        {
            return (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
        }

        private static SerializedFile CreateSyntheticSerializedFile(string fileName)
        {
            var file = CreateUninitialized<SerializedFile>();
            file.fileName = fileName;
            file.fullName = fileName;
            file.originalPath = fileName;
            file.Objects = new List<AssetStudio.Object>();
            file.ObjectsDic = new Dictionary<long, AssetStudio.Object>();
            file.ObjectInfoDic = new Dictionary<long, ObjectInfo>();
            file.m_Externals = new List<FileIdentifier>();
            var lockField = typeof(SerializedFile).GetField("objectCollectionLock", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(lockField);
            lockField!.SetValue(file, new object());
            return file;
        }

        private static T CreateObjectInFile<T>(SerializedFile file, long pathId, ClassIDType type, string name) where T : AssetStudio.Object
        {
            var obj = CreateUninitialized<T>();
            obj.assetsFile = file;
            obj.m_PathID = pathId;
            obj.type = type;

            if (obj is NamedObject named)
            {
                named.m_Name = name;
            }
            else if (obj is GameObject go)
            {
                go.m_Name = name;
            }
            else if (obj is MonoBehaviour mb)
            {
                mb.m_Name = name;
            }

            file.Objects.Add(obj);
            file.ObjectsDic[pathId] = obj;
            return obj;
        }

        private static PPtr<T> CreatePPtr<T>(SerializedFile file, long pathId, int fileId = 0) where T : AssetStudio.Object
        {
            return new PPtr<T>(fileId, pathId, file);
        }

        private static string LocateRepositoryRoot()
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current != null)
            {
                var candidate = Path.Combine(current.FullName, "AssetStudio.Avalonia", "AssetStudio.Avalonia.csproj");
                if (File.Exists(candidate))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            throw new DirectoryNotFoundException($"Unable to locate repository root from '{AppContext.BaseDirectory}'.");
        }

        #endregion
    }
}
