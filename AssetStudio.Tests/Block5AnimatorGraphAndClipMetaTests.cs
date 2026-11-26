using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AssetStudio;
using AssetStudio.Avalonia.Services;
using AssetStudio.Avalonia.ViewModels;
using Xunit;

namespace AssetStudio.Tests
{
    public class Block5AnimatorGraphAndClipMetaTests
    {
        private static readonly Regex AvaloniaUsingRegex = new(
            @"^\s*using\s+(?:global::)?Avalonia(?:\.[A-Za-z0-9_]+)?\s*;",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);

        private static readonly Regex AxamlClickAttributeRegex = new(
            @"\bClick\s*=",
            RegexOptions.CultureInvariant);

        // =========================================================================
        // 1. AnimatorGraphService — Multi-Layer Controller, BlendTrees, Conditions, Layout
        // =========================================================================

        [Fact]
        public void AnimatorGraph_MultiLayerController_ExtractsParametersLayersBlendTreesConditionsAndNonOverlappingLayout()
        {
            var sf = CreateSyntheticSerializedFile("character_ctrl.assets", "2022.3.20f1");
            var clipIdle = CreateSyntheticClip(sf, 1001, "Locomotion_Idle");
            var clipWalk = CreateSyntheticClip(sf, 1002, "Locomotion_Walk");
            var clipRun = CreateSyntheticClip(sf, 1003, "Locomotion_Run");
            var clipStrafeL = CreateSyntheticClip(sf, 1004, "Strafe_Left");
            var clipStrafeR = CreateSyntheticClip(sf, 1005, "Strafe_Right");
            var clipAttack = CreateSyntheticClip(sf, 1006, "Combat_Slash");
            var clipHitReact = CreateSyntheticClip(sf, 1007, "Upper_HitReact");

            var controller = BuildRichMultiLayerController(
                sf,
                pathId: 5001,
                name: "CharacterController",
                clips: new[] { clipIdle, clipWalk, clipRun, clipStrafeL, clipStrafeR, clipAttack, clipHitReact });

            var doc = AnimatorGraphService.BuildDocument(controller);

            Assert.Equal("CharacterController", doc.ControllerName);
            Assert.Equal("AnimatorController", doc.AssetKind);
            Assert.Equal("character_ctrl.assets", doc.SourceFile);
            Assert.Equal(5001, doc.PathId);
            Assert.True(doc.MultiThreadedStateMachine);

            // Parameters: Float, Int, Bool, Trigger + exact default values
            Assert.Equal(4, doc.Parameters.Count);
            var speedParam = Assert.Single(doc.Parameters, p => p.Name == "Speed");
            Assert.Equal("Float", speedParam.TypeName);
            Assert.Equal(1.25f, speedParam.DefaultFloat);

            var comboParam = Assert.Single(doc.Parameters, p => p.Name == "ComboIndex");
            Assert.Equal("Int", comboParam.TypeName);
            Assert.Equal(2, comboParam.DefaultInt);

            var groundedParam = Assert.Single(doc.Parameters, p => p.Name == "IsGrounded");
            Assert.Equal("Bool", groundedParam.TypeName);
            Assert.Equal(true, groundedParam.DefaultBool);

            var attackTrigger = Assert.Single(doc.Parameters, p => p.Name == "AttackTrigger");
            Assert.Equal("Trigger", attackTrigger.TypeName);
            Assert.Equal(false, attackTrigger.DefaultBool);

            // Layers: 2 layers (Override + Additive)
            Assert.Equal(2, doc.Layers.Count);
            var baseLayer = doc.Layers[0];
            Assert.Equal(0, baseLayer.LayerIndex);
            Assert.Equal("Base Layer", baseLayer.Name);
            Assert.Equal("Override", baseLayer.BlendingMode);
            Assert.Equal(1.0f, baseLayer.DefaultWeight);
            Assert.True(baseLayer.IKPass);
            Assert.Equal(0, baseLayer.DefaultStateIndex);
            Assert.Equal("Idle", baseLayer.DefaultStateName);

            var upperLayer = doc.Layers[1];
            Assert.Equal(1, upperLayer.LayerIndex);
            Assert.Equal("Layer 1", upperLayer.Name); // Fallback when binding hash not in TOS
            Assert.Equal("Additive", upperLayer.BlendingMode);
            Assert.Equal(0.65f, upperLayer.DefaultWeight);
            Assert.True(upperLayer.SyncedLayerAffectsTiming);

            // Base layer states (4 states: Idle, Locomotion1D, Strafe2D, Attack)
            Assert.Equal(4, baseLayer.States.Count);

            var idleState = baseLayer.States[0];
            Assert.True(idleState.IsDefault);
            Assert.Equal(AnimatorMotionKind.Clip, idleState.MotionKind);
            Assert.Equal("Locomotion_Idle", idleState.PrimaryClipName);
            Assert.Equal(1001, idleState.PrimaryClipPathId);
            Assert.Equal("GroundTag", idleState.Tag);

            // Verify all 6 condition modes across Entry, AnyState, and State transitions
            Assert.Single(baseLayer.EntryTransitions);
            Assert.Equal("Locomotion1D", baseLayer.EntryTransitions[0].DestinationStateName);
            Assert.Equal("If", baseLayer.EntryTransitions[0].Conditions[0].ModeName);

            Assert.Single(baseLayer.AnyStateTransitions);
            var anyTr = baseLayer.AnyStateTransitions[0];
            Assert.Equal(AnimatorGraphTransition.AnyStateSourceIndex, anyTr.SourceStateIndex);
            Assert.Equal("Attack", anyTr.DestinationStateName);
            Assert.True(anyTr.CanTransitionToSelf);
            Assert.Equal(2, anyTr.Conditions.Count);
            Assert.Equal("If", anyTr.Conditions[0].ModeName);
            Assert.Equal("AttackTrigger (Trigger)", anyTr.Conditions[0].FormattedExpression);
            Assert.Equal("Equals", anyTr.Conditions[1].ModeName);
            Assert.Equal("ComboIndex == 1", anyTr.Conditions[1].FormattedExpression);

            var allConditionModes = baseLayer.States
                .SelectMany(s => s.Transitions)
                .Concat(baseLayer.AnyStateTransitions)
                .Concat(baseLayer.EntryTransitions)
                .SelectMany(t => t.Conditions)
                .Select(c => c.ModeName)
                .ToHashSet(StringComparer.Ordinal);
            Assert.Contains("If", allConditionModes);
            Assert.Contains("IfNot", allConditionModes);
            Assert.Contains("Greater", allConditionModes);
            Assert.Contains("Less", allConditionModes);
            Assert.Contains("Equals", allConditionModes);
            Assert.Contains("NotEqual", allConditionModes);

            // Verify 1D BlendTree
            var locoState = baseLayer.States[1];
            Assert.Equal(AnimatorMotionKind.BlendTree, locoState.MotionKind);
            Assert.NotNull(locoState.BlendTree);
            Assert.Equal("1D", locoState.BlendTree!.BlendTypeName);
            Assert.Equal("Speed", locoState.BlendTree.ParameterXName);
            Assert.Equal(2, locoState.BlendTree.Children.Count);
            Assert.Equal(0.5f, locoState.BlendTree.Children[0].Threshold);
            Assert.Equal("Locomotion_Walk", locoState.BlendTree.Children[0].Node.Clip?.Name);
            Assert.Equal(1.0f, locoState.BlendTree.Children[1].Threshold);
            Assert.Equal("Locomotion_Run", locoState.BlendTree.Children[1].Node.Clip?.Name);

            // Verify 2D Freeform Directional BlendTree
            var strafeState = baseLayer.States[2];
            Assert.Equal(AnimatorMotionKind.BlendTree, strafeState.MotionKind);
            Assert.NotNull(strafeState.BlendTree);
            Assert.Equal("2D Freeform Directional", strafeState.BlendTree!.BlendTypeName);
            Assert.Equal("Speed", strafeState.BlendTree.ParameterXName);
            Assert.Equal("ComboIndex", strafeState.BlendTree.ParameterYName);
            Assert.Equal(2, strafeState.BlendTree.Children.Count);
            Assert.Equal(-1f, strafeState.BlendTree.Children[0].PositionX);
            Assert.Equal(0f, strafeState.BlendTree.Children[0].PositionY);
            Assert.Equal(1f, strafeState.BlendTree.Children[1].PositionX);
            Assert.Equal(0.5f, strafeState.BlendTree.Children[1].PositionY);

            // Verify deterministic BFS layout guarantees non-overlapping bounding boxes
            Assert.Equal(0, idleState.LayoutColumn);
            AssertNoStateBoxesOverlap(baseLayer.States);
        }

        [Fact]
        public void AnimatorGraph_OverrideControllerAndAnimator_AppliesClipOverridesToStatesAndBlendTrees()
        {
            var sf = CreateSyntheticSerializedFile("override_ctrl.assets", "2022.3.20f1");
            var clipIdle = CreateSyntheticClip(sf, 2001, "Base_Idle");
            var clipWalk = CreateSyntheticClip(sf, 2002, "Base_Walk");
            var clipRun = CreateSyntheticClip(sf, 2003, "Base_Run");
            var clipOverrideIdle = CreateSyntheticClip(sf, 2101, "Variant_Idle");
            var clipOverrideRun = CreateSyntheticClip(sf, 2103, "Variant_Run");

            var baseCtrl = BuildRichMultiLayerController(
                sf,
                pathId: 6001,
                name: "BaseLocomotionCtrl",
                clips: new[] { clipIdle, clipWalk, clipRun, clipWalk, clipRun, clipIdle, clipIdle });

            var overrideCtrl = (AnimatorOverrideController)RuntimeHelpers.GetUninitializedObject(typeof(AnimatorOverrideController));
            overrideCtrl.assetsFile = sf;
            overrideCtrl.m_PathID = 6002;
            overrideCtrl.type = ClassIDType.AnimatorOverrideController;
            overrideCtrl.m_Name = "VariantOverrideCtrl";
            overrideCtrl.m_Controller = new PPtr<RuntimeAnimatorController>(0, baseCtrl.m_PathID, sf);
            overrideCtrl.m_Clips = new[]
            {
                CreateClipOverride(sf, clipIdle.m_PathID, clipOverrideIdle.m_PathID),
                CreateClipOverride(sf, clipRun.m_PathID, clipOverrideRun.m_PathID)
            };
            sf.AddObject(overrideCtrl);

            var doc = AnimatorGraphService.BuildDocument(overrideCtrl);

            Assert.Equal("VariantOverrideCtrl", doc.ControllerName);
            Assert.Equal("AnimatorOverrideController", doc.AssetKind);
            Assert.Equal("BaseLocomotionCtrl", doc.BaseControllerName);
            Assert.Equal(6001, doc.BaseControllerPathId);
            Assert.Equal(2, doc.ClipOverrides.Count);
            Assert.Equal(2, doc.Layers.Count);

            var baseLayer = doc.Layers[0];
            var idleState = baseLayer.States[0];
            Assert.True(idleState.IsOverriddenClip);
            Assert.Equal("Variant_Idle", idleState.PrimaryClipName);
            Assert.Equal(2101, idleState.PrimaryClipPathId);
            Assert.True(idleState.Clips[0].IsOverridden);
            Assert.Equal("Base_Idle", idleState.Clips[0].OriginalClipName);

            // BlendTree leaf node for Base_Run should also be overridden to Variant_Run!
            var locoState = baseLayer.States[1];
            Assert.True(locoState.IsOverriddenClip);
            Assert.Contains(locoState.Clips, c => c.Name == "Base_Walk" && !c.IsOverridden);
            Assert.Contains(locoState.Clips, c => c.Name == "Variant_Run" && c.IsOverridden && c.OriginalClipName == "Base_Run");
            Assert.Equal("Variant_Run", locoState.BlendTree!.Children[1].Node.Clip!.Name);

            // Also verify building from an Animator pointing to the AnimatorOverrideController
            var animatorGo = (GameObject)RuntimeHelpers.GetUninitializedObject(typeof(GameObject));
            animatorGo.assetsFile = sf;
            animatorGo.m_PathID = 7000;
            animatorGo.type = ClassIDType.GameObject;
            animatorGo.m_Name = "HeroRig";
            sf.AddObject(animatorGo);

            var animator = (Animator)RuntimeHelpers.GetUninitializedObject(typeof(Animator));
            animator.assetsFile = sf;
            animator.m_PathID = 7001;
            animator.type = ClassIDType.Animator;
            animator.m_GameObject = new PPtr<GameObject>(0, animatorGo.m_PathID, sf);
            animator.m_Controller = new PPtr<RuntimeAnimatorController>(0, overrideCtrl.m_PathID, sf);
            sf.AddObject(animator);

            var animDoc = AnimatorGraphService.BuildDocument(animator);
            Assert.Equal("HeroRig", animDoc.ControllerName);
            Assert.Equal("Animator", animDoc.AssetKind);
            Assert.Equal(2, animDoc.Layers.Count);
            Assert.Equal(2, animDoc.ClipOverrides.Count);
            Assert.Equal("Variant_Idle", animDoc.Layers[0].States[0].PrimaryClipName);
        }

        [Fact]
        public void AnimatorGraph_CyclicBlendTreeAndMalformedNullArrays_NeverThrows()
        {
            // Cyclic blend tree: node 0 -> node 1 -> node 0
            var node0 = CreateBlendTreeNode(blendType: 0, eventId: 10, eventYId: 0, childIndices: new uint[] { 1 }, clipId: uint.MaxValue);
            node0.m_Blend1dData = CreateBlend1d(0.5f);
            var node1 = CreateBlendTreeNode(blendType: 0, eventId: 10, eventYId: 0, childIndices: new uint[] { 0, 99 }, clipId: uint.MaxValue);
            node1.m_Blend1dData = CreateBlend1d(1.0f);

            var bt = CreateBlendTreeConstant(new[] { node0, node1 });
            var state = CreateStateConstant(nameId: 20, blendTrees: new[] { bt }, transitions: null);
            var sm = CreateStateMachine(new[] { state }, defaultState: 99, anyStateTransitions: null, selectors: null);
            var layer = CreateLayer(binding: 30, stateMachineIndex: 0, blendingMode: 0, weight: float.NaN);
            var badLayer = CreateLayer(binding: 31, stateMachineIndex: 42, blendingMode: 0, weight: 1f);

            var ctrlConst = (ControllerConstant)RuntimeHelpers.GetUninitializedObject(typeof(ControllerConstant));
            ctrlConst.m_LayerArray = new[] { layer, badLayer };
            ctrlConst.m_StateMachineArray = new[] { sm };
            ctrlConst.m_Values = CreateValueArrayConstant(new[]
            {
                CreateValueConstant(10, 1, 999), // Out-of-bounds float index!
                CreateValueConstant(11, 3, 999), // Out-of-bounds int index!
                CreateValueConstant(12, 4, 999)  // Out-of-bounds bool index!
            });
            ctrlConst.m_DefaultValues = (ValueArray)RuntimeHelpers.GetUninitializedObject(typeof(ValueArray));

            var controller = (AnimatorController)RuntimeHelpers.GetUninitializedObject(typeof(AnimatorController));
            controller.m_PathID = 9001;
            controller.m_Name = "CorruptController";
            controller.m_Controller = ctrlConst;
            controller.m_TOS = new[]
            {
                new KeyValuePair<uint, string>(10, "CycleParam"),
                new KeyValuePair<uint, string>(20, "CycleState"),
                new KeyValuePair<uint, string>(30, "SafeLayer")
            };
            controller.m_AnimationClips = null!;

            var doc = AnimatorGraphService.BuildDocument(controller);
            Assert.Equal("CorruptController", doc.ControllerName);
            Assert.Equal(2, doc.Layers.Count);
            Assert.Equal(3, doc.Parameters.Count);
            Assert.Equal(0f, doc.Parameters[0].DefaultFloat);
            Assert.Equal(0, doc.Parameters[1].DefaultInt);
            Assert.Equal(false, doc.Parameters[2].DefaultBool);
            Assert.Single(doc.Layers[0].States);
            Assert.Equal(AnimatorMotionKind.BlendTree, doc.Layers[0].States[0].MotionKind);
            Assert.NotNull(doc.Layers[0].States[0].BlendTree);
            Assert.NotEmpty(doc.Notes);

            // Null asset and controller with null m_Controller also handled safely
            var nullDoc = AnimatorGraphService.BuildDocument(null);
            Assert.NotEmpty(nullDoc.Notes);

            var emptyCtrl = (AnimatorController)RuntimeHelpers.GetUninitializedObject(typeof(AnimatorController));
            emptyCtrl.m_Name = "EmptyCtrl";
            var emptyDoc = AnimatorGraphService.BuildDocument(emptyCtrl);
            Assert.Empty(emptyDoc.Layers);
            Assert.NotEmpty(emptyDoc.Notes);
        }

        [Fact]
        public void AnimatorGraph_Exporters_ProduceValidJsonEscapedGraphvizDotAndReadableSummary()
        {
            var sf = CreateSyntheticSerializedFile("export_test.assets", "2022.3.20f1");
            var clipA = CreateSyntheticClip(sf, 3001, "Clip \"Quoted\" \\ Line\nBreak");
            var clipB = CreateSyntheticClip(sf, 3002, "Clip_Run");

            var ctrl = BuildRichMultiLayerController(
                sf,
                pathId: 8001,
                name: "Ctrl \"Special\" \\ Name",
                clips: new[] { clipA, clipB, clipB, clipB, clipB, clipB, clipB });

            // Inject quotes & newline into a state name in TOS to exercise DOT escaping thoroughly
            ctrl.m_TOS = ctrl.m_TOS
                .Select(kv => kv.Key == 201 ? new KeyValuePair<uint, string>(kv.Key, "Idle \"Quoted\"\n\\Path") : kv)
                .ToArray();

            var doc = AnimatorGraphService.BuildDocument(ctrl);

            var tempDir = CreateTempDirectory("anim_graph_export");
            try
            {
                var jsonPath = Path.Combine(tempDir, "graph.json");
                var dotAllPath = Path.Combine(tempDir, "graph_all.dot");
                var dotLayer0Path = Path.Combine(tempDir, "graph_layer0.dot");

                AnimatorGraphService.ExportToJson(doc, jsonPath);
                AnimatorGraphService.WriteGraphvizDotFile(doc, dotAllPath);
                AnimatorGraphService.WriteGraphvizDotFile(doc, dotLayer0Path, layerIndex: 0);

                var jsonBytes = File.ReadAllBytes(jsonPath);
                Assert.False(HasUtf8Bom(jsonBytes));
                using (var parsed = JsonDocument.Parse(jsonBytes))
                {
                    Assert.Equal("Ctrl \"Special\" \\ Name", parsed.RootElement.GetProperty("controllerName").GetString());
                    Assert.Equal(2, parsed.RootElement.GetProperty("layers").GetArrayLength());
                    Assert.Equal(4, parsed.RootElement.GetProperty("parameters").GetArrayLength());
                }

                var dotAll = File.ReadAllText(dotAllPath);
                Assert.StartsWith("digraph \"Ctrl \\\"Special\\\" \\\\ Name\" {", dotAll, StringComparison.Ordinal);
                Assert.Contains("subgraph cluster_layer_0", dotAll, StringComparison.Ordinal);
                Assert.Contains("subgraph cluster_layer_1", dotAll, StringComparison.Ordinal);
                Assert.Contains("L0_ENTRY [label=\"Entry\"", dotAll, StringComparison.Ordinal);
                Assert.Contains("L0_ANY [label=\"Any State\"", dotAll, StringComparison.Ordinal);
                Assert.Contains("fillcolor=\"#E65100\"", dotAll, StringComparison.Ordinal); // Default state styling
                Assert.Contains("Idle \\\"Quoted\\\"\\n\\\\Path", dotAll, StringComparison.Ordinal);
                Assert.DoesNotContain("\n\\Path\"", dotAll, StringComparison.Ordinal);

                var dotLayer0 = File.ReadAllText(dotLayer0Path);
                Assert.DoesNotContain("subgraph cluster_layer_1", dotLayer0, StringComparison.Ordinal);
                Assert.Contains("L0_S0", dotLayer0, StringComparison.Ordinal);

                var summary = AnimatorGraphService.FormatReadableSummary(doc);
                Assert.Contains("ANIMATORCONTROLLER:", summary, StringComparison.Ordinal);
                Assert.Contains("Parameters (4):", summary, StringComparison.Ordinal);
                Assert.Contains("Layer 0: Base Layer", summary, StringComparison.Ordinal);
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        // =========================================================================
        // 2. AnimationClipMetadataService Tests (ANIM-07)
        // =========================================================================

        [Fact]
        public void AnimationClipMetadata_ModernMuscleClipWithGenericBindings_ExtractsAllStatsAndPaths()
        {
            var sf = CreateSyntheticSerializedFile("modern_clips.assets", "6000.0.30f1");

            uint hipsHash = 111111u;
            uint faceHash = 222222u;
            uint unresolvedHash = 333333u;

            var bindings = new[]
            {
                new GenericBinding { path = hipsHash, attribute = 1, typeID = ClassIDType.Transform },
                new GenericBinding { path = hipsHash, attribute = 2, typeID = ClassIDType.Transform },
                new GenericBinding { path = hipsHash, attribute = 3, typeID = ClassIDType.Transform },
                new GenericBinding { path = unresolvedHash, attribute = 4, typeID = ClassIDType.Transform },
                new GenericBinding { path = faceHash, attribute = 999, typeID = ClassIDType.SkinnedMeshRenderer, customType = 20 },
                new GenericBinding { path = faceHash, attribute = 2015549526, typeID = ClassIDType.SpriteRenderer, isPPtrCurve = 1 },
                new GenericBinding { path = 0, attribute = 14, typeID = ClassIDType.Animator, customType = 8 },
                new GenericBinding { path = hipsHash, attribute = 2086281974, typeID = ClassIDType.GameObject }
            };

            var clip = BuildModernMuscleClip(
                sf,
                pathId: 4001,
                name: "Hero_Run_Loop",
                sampleRate: 60f,
                startTime: 0f,
                stopTime: 1.5f,
                streamedCurves: 12,
                denseCurves: 8,
                denseFrames: 90,
                constantCurves: 5,
                bindings: bindings);

            clip.m_MuscleClip!.m_LoopTime = true;
            clip.m_MuscleClip.m_LoopBlend = true;
            clip.m_MuscleClip.m_LoopBlendOrientation = true;
            clip.m_MuscleClip.m_LoopBlendPositionY = true;
            clip.m_MuscleClip.m_LoopBlendPositionXZ = false;
            clip.m_MuscleClip.m_KeepOriginalOrientation = true;
            clip.m_MuscleClip.m_KeepOriginalPositionY = true;
            clip.m_MuscleClip.m_KeepOriginalPositionXZ = true;
            clip.m_MuscleClip.m_HeightFromFeet = true;
            clip.m_MuscleClip.m_Mirror = true;
            clip.m_MuscleClip.m_CycleOffset = 0.25f;
            clip.m_MuscleClip.m_AverageSpeed = new Vector3(0f, 0f, 4.5f);
            clip.m_MuscleClip.m_AverageAngularSpeed = 0.15f;
            clip.m_HasGenericRootTransform = true;
            clip.m_HasMotionFloatCurves = true;
            clip.m_UseHighQualityCurve = true;
            clip.m_Bounds = CreateBounds(new Vector3(0, 1, 0), new Vector3(0.5f, 1f, 0.5f));
            clip.m_Events = new[]
            {
                CreateAnimationEvent(0.4f, "OnFootstep", "Left", 0.8f, 1, 90001, 0),
                CreateAnimationEvent(1.1f, "OnFootstep", "Right", 0.9f, 2, 0, 1)
            };

            var boneLookup = new Dictionary<uint, string>
            {
                [hipsHash] = "Root/Hips",
                [faceHash] = "Root/Head/FaceMesh"
            };

            var doc = AnimationClipMetadataService.ExtractMetadata(clip, boneLookup);

            Assert.Equal("Hero_Run_Loop", doc.Name);
            Assert.Equal(4001, doc.PathId);
            Assert.Equal("modern_clips.assets", doc.SourceFile);
            Assert.Equal("6000.0.30f1", doc.UnityVersion);
            Assert.False(doc.IsLegacy);
            Assert.Equal("Humanoid", doc.AnimationType);
            Assert.True(doc.UseHighQualityCurve);
            Assert.Equal(60f, doc.SampleRate);
            Assert.Equal(0f, doc.StartTime);
            Assert.Equal(1.5f, doc.StopTime);
            Assert.Equal(1.5f, doc.DurationSeconds, precision: 4);
            Assert.Equal(90, doc.EstimatedFrameCount);
            Assert.True(doc.IsLooping);
            Assert.True(doc.LoopBlend);
            Assert.True(doc.LoopBlendOrientation);
            Assert.True(doc.LoopBlendPositionY);
            Assert.False(doc.LoopBlendPositionXZ);
            Assert.True(doc.KeepOriginalOrientation);
            Assert.True(doc.KeepOriginalPositionY);
            Assert.True(doc.KeepOriginalPositionXZ);
            Assert.True(doc.HeightFromFeet);
            Assert.True(doc.Mirror);
            Assert.Equal(0.25f, doc.CycleOffset);
            Assert.True(doc.HasGenericRootTransform);
            Assert.True(doc.HasMotionFloatCurves);
            Assert.Equal(4.5f, doc.AverageSpeed.Z);
            Assert.Equal(0.15f, doc.AverageAngularSpeed);
            Assert.NotNull(doc.Bounds);
            Assert.Equal(1f, doc.Bounds!.Center.Y);
            Assert.Equal(0.5f, doc.Bounds.Extent.X);

            Assert.Equal(2, doc.Events.Count);
            Assert.Equal("OnFootstep", doc.Events[0].FunctionName);
            Assert.Equal("Left", doc.Events[0].StringParameter);
            Assert.Equal(90001, doc.Events[0].ObjectReferencePathId);

            Assert.Equal(12, doc.Curves.StreamedCurves);
            Assert.Equal(8, doc.Curves.DenseCurves);
            Assert.Equal(90, doc.Curves.DenseFrames);
            Assert.Equal(5, doc.Curves.ConstantCurves);
            Assert.Equal(8, doc.Curves.TotalGenericBindings);
            Assert.Equal(1, doc.Curves.TransformPositionBindings);
            Assert.Equal(1, doc.Curves.TransformRotationBindings);
            Assert.Equal(1, doc.Curves.TransformScaleBindings);
            Assert.Equal(1, doc.Curves.TransformEulerBindings);
            Assert.Equal(1, doc.Curves.BlendShapeBindings);
            Assert.Equal(1, doc.Curves.PptrBindings);
            Assert.Equal(1, doc.Curves.HumanoidMuscleBindings);
            Assert.Equal(1, doc.Curves.OtherFloatBindings);

            Assert.Equal(
                new[] { "Root/Head/FaceMesh", "Root/Hips", $"path_{unresolvedHash}" },
                doc.AnimatedPaths);

            var json = AnimationClipMetadataService.SerializeToJson(doc);
            using var parsed = JsonDocument.Parse(json);
            Assert.Equal("Hero_Run_Loop", parsed.RootElement.GetProperty("name").GetString());
            Assert.Equal(8, parsed.RootElement.GetProperty("curves").GetProperty("totalGenericBindings").GetInt32());
        }

        [Fact]
        public void AnimationClipMetadata_LegacyCurvesWithoutMuscleClip_ComputesDurationFromKeyframesAndHandlesMalformedClips()
        {
            var sf = CreateSyntheticSerializedFile("legacy_clips.assets", "4.7.2f1");
            var clip = (AnimationClip)RuntimeHelpers.GetUninitializedObject(typeof(AnimationClip));
            clip.assetsFile = sf;
            clip.m_PathID = 4100;
            clip.m_Name = "Door_Open_Legacy";
            clip.m_Legacy = true;
            clip.m_AnimationType = AnimationType.Legacy;
            clip.m_SampleRate = 30f;
            clip.m_WrapMode = 2; // Loop
            clip.m_PositionCurves = new[]
            {
                CreateVector3Curve("Pivot/DoorLeaf", 0f, 1.25f, 2.5f)
            };
            clip.m_FloatCurves = new[]
            {
                CreateFloatCurve("Pivot/DoorLight", "m_Intensity", 0f, 3.0f)
            };
            clip.m_Events = new[]
            {
                CreateAnimationEvent(2.8f, "OnDoorOpened", "Done", 1.0f, 7, 0, 0)
            };

            var doc = AnimationClipMetadataService.ExtractMetadata(clip);
            Assert.True(doc.IsLegacy);
            Assert.Equal("Legacy", doc.AnimationType);
            Assert.Equal("Loop", doc.WrapMode);
            Assert.True(doc.IsLooping);
            Assert.Equal(0f, doc.StartTime);
            Assert.Equal(3.0f, doc.StopTime);
            Assert.Equal(3.0f, doc.DurationSeconds);
            Assert.Equal(90, doc.EstimatedFrameCount);
            Assert.Equal(1, doc.Curves.PositionCurves);
            Assert.Equal(1, doc.Curves.FloatCurves);
            Assert.Equal(new[] { "Pivot/DoorLeaf", "Pivot/DoorLight" }, doc.AnimatedPaths);

            // Null & malformed clip with NaN values and corrupt compressed curve never throws
            var nullDoc = AnimationClipMetadataService.ExtractMetadata(null);
            Assert.NotNull(nullDoc);

            var corrupt = (AnimationClip)RuntimeHelpers.GetUninitializedObject(typeof(AnimationClip));
            corrupt.m_Name = "CorruptClip";
            corrupt.m_SampleRate = float.NaN;
            corrupt.m_CompressedRotationCurves = new[]
            {
                CreateCorruptCompressedCurve("Bone/Corrupt")
            };
            var corruptDoc = AnimationClipMetadataService.ExtractMetadata(corrupt);
            Assert.Equal(0f, corruptDoc.SampleRate);
            Assert.Equal(1, corruptDoc.Curves.CompressedRotationCurves);
            Assert.Contains("Bone/Corrupt", corruptDoc.AnimatedPaths);
            var corruptJson = AnimationClipMetadataService.SerializeToJson(corruptDoc);
            using var _ = JsonDocument.Parse(corruptJson);
        }

        [Fact]
        public void AnimationClipMetadata_BatchExport_SanitizesFilenamesEnforcesPathConfinementAndDisambiguatesDuplicates()
        {
            var sf = CreateSyntheticSerializedFile("batch.assets", "2022.3.20f1");
            var clipTraversal = CreateSyntheticClip(sf, 501, "../../escape\\bad:clip*name?");
            var clipReserved = CreateSyntheticClip(sf, 502, "CON");
            var clipDup1 = CreateSyntheticClip(sf, 503, "SharedName");
            var clipDup2 = CreateSyntheticClip(sf, 504, "SharedName");

            var tempDir = CreateTempDirectory("clip_meta_batch");
            try
            {
                var result = AnimationClipMetadataService.ExportBatchMetadataJson(
                    new AnimationClip?[] { clipTraversal, clipReserved, clipDup1, clipDup2, null }!,
                    tempDir);

                Assert.Equal(4, result.ExportedCount);
                Assert.Equal(1, result.SkippedCount);
                Assert.Empty(result.Errors);
                Assert.Equal(4, result.ExportedFiles.Count);

                var normalizedRoot = Path.GetFullPath(tempDir) + Path.DirectorySeparatorChar;
                foreach (var filePath in result.ExportedFiles)
                {
                    Assert.True(File.Exists(filePath));
                    Assert.StartsWith(normalizedRoot, Path.GetFullPath(filePath), StringComparison.OrdinalIgnoreCase);
                    Assert.EndsWith(".anim.json", filePath, StringComparison.OrdinalIgnoreCase);
                    Assert.False(HasUtf8Bom(File.ReadAllBytes(filePath)));
                    using var doc = JsonDocument.Parse(File.ReadAllText(filePath));
                    Assert.True(doc.RootElement.TryGetProperty("name", out _));
                }

                // Reserved Windows name gets prefixed with "_"
                Assert.Contains(result.ExportedFiles, f => Path.GetFileName(f).Equals("_CON.anim.json", StringComparison.OrdinalIgnoreCase));
                // Duplicate names produce two distinct files
                Assert.Equal(4, result.ExportedFiles.Select(Path.GetFileName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        // =========================================================================
        // 3. AnimatorGraphViewModel Pure-MVVM Tests
        // =========================================================================

        [Fact]
        public async Task AnimatorGraphViewModel_LayerSwitchFilterSelectionCanvasAndCommandsWorkHeadless()
        {
            var sf = CreateSyntheticSerializedFile("vm_test.assets", "2022.3.20f1");
            var clips = new[]
            {
                CreateSyntheticClip(sf, 101, "Idle_Clip"),
                CreateSyntheticClip(sf, 102, "Walk_Clip"),
                CreateSyntheticClip(sf, 103, "Run_Clip"),
                CreateSyntheticClip(sf, 104, "StrafeL_Clip"),
                CreateSyntheticClip(sf, 105, "StrafeR_Clip"),
                CreateSyntheticClip(sf, 106, "Attack_Clip"),
                CreateSyntheticClip(sf, 107, "Upper_Clip")
            };
            var ctrl = BuildRichMultiLayerController(sf, 901, "HeroCtrl", clips);
            var doc = AnimatorGraphService.BuildDocument(ctrl);

            var vm = new AnimatorGraphViewModel(doc);

            // Initial state: Layer 0 selected, Default state selected, canvas nodes & edges populated
            Assert.Equal(2, vm.Layers.Count);
            Assert.NotNull(vm.SelectedLayer);
            Assert.Equal(0, vm.SelectedLayer!.LayerIndex);
            Assert.NotNull(vm.SelectedState);
            Assert.Equal("Idle", vm.SelectedState!.Name);
            Assert.Equal(4, vm.FilteredStates.Count);
            Assert.True(vm.CanvasNodes.Count >= 6); // Entry + AnyState + 4 states
            Assert.True(vm.CanvasEdges.Count >= 4);
            Assert.True(vm.CanvasWidth >= 760.0);
            Assert.True(vm.CanvasHeight >= 460.0);

            // Filter by clip/state name dims non-matching canvas nodes
            vm.FilterText = "Walk_Clip";
            Assert.Single(vm.FilteredStates);
            Assert.Equal("Locomotion1D", vm.FilteredStates[0].Name);
            var idleNode = Assert.Single(vm.CanvasNodes, n => !n.IsSpecialNode && n.StateIndex == 0);
            var locoNode = Assert.Single(vm.CanvasNodes, n => !n.IsSpecialNode && n.StateIndex == 1);
            Assert.True(idleNode.IsDimmed);
            Assert.False(locoNode.IsDimmed);

            // ResetFilterCommand clears filter and restores all states
            Assert.True(vm.ResetFilterCommand.CanExecute(null));
            vm.ResetFilterCommand.Execute(null);
            Assert.Equal(string.Empty, vm.FilterText);
            Assert.Equal(4, vm.FilteredStates.Count);
            Assert.False(idleNode.IsDimmed);

            // Selecting a BlendTree state via CanvasNode.SelectCommand updates outgoing/incoming & BlendTree summary
            locoNode.SelectCommand!.Execute(null);
            Assert.Equal("Locomotion1D", vm.SelectedState!.Name);
            Assert.True(locoNode.IsSelected);
            Assert.False(idleNode.IsSelected);
            Assert.True(vm.HasSelectedStateBlendTree);
            Assert.Contains("1D [Speed]", vm.SelectedStateBlendTreeSummary, StringComparison.Ordinal);
            Assert.NotEmpty(vm.SelectedStateOutgoingTransitions);
            Assert.NotEmpty(vm.SelectedStateIncomingTransitions);

            // Switching to Layer 1 rebuilds states & canvas cleanly
            vm.SelectedLayer = vm.Layers[1];
            Assert.Single(vm.FilteredStates);
            Assert.Equal("HitReact", vm.SelectedState!.Name);

            // Switch back to Layer 0 and test all headless async command callbacks
            vm.SelectedLayer = vm.Layers[0];
            string? capturedJson = null;
            string? capturedDot = null;
            string? capturedSummary = null;
            AnimatorGraphClipRef? navigatedClip = null;
            bool closeRequested = false;

            vm.SaveJsonAsync = json => { capturedJson = json; return Task.CompletedTask; };
            vm.SaveDotAsync = dot => { capturedDot = dot; return Task.CompletedTask; };
            vm.CopyTextAsync = text => { capturedSummary = text; return Task.CompletedTask; };
            vm.NavigateToClipAsync = clip => { navigatedClip = clip; return Task.CompletedTask; };
            vm.RequestClose += () => closeRequested = true;

            await vm.ExportJsonCommand.ExecuteAsync();
            await vm.ExportDotCommand.ExecuteAsync();
            await vm.CopySummaryCommand.ExecuteAsync();
            await vm.JumpToClipCommand.ExecuteAsync(vm.SelectedState!.Clips[0]);
            vm.CloseCommand.Execute(null);

            Assert.NotNull(capturedJson);
            using (var parsed = JsonDocument.Parse(capturedJson!))
            {
                Assert.Equal("HeroCtrl", parsed.RootElement.GetProperty("controllerName").GetString());
            }

            Assert.NotNull(capturedDot);
            Assert.StartsWith("digraph", capturedDot!, StringComparison.Ordinal);
            Assert.NotNull(capturedSummary);
            Assert.Contains("HeroCtrl", capturedSummary!, StringComparison.Ordinal);
            Assert.NotNull(navigatedClip);
            Assert.Equal(101, navigatedClip!.PathId);
            Assert.True(closeRequested);
        }

        // =========================================================================
        // 4. Pure MVVM Static & Reflection Verification
        // =========================================================================

        [Fact]
        public void Block5B_PureMvvmGuard_ServicesAndViewModelHaveZeroAvaloniaAndDialogHasZeroClickAttributes()
        {
            var repoRoot = LocateRepositoryRoot();

            var pureFiles = new[]
            {
                Path.Combine(repoRoot, "AssetStudio.Avalonia", "Services", "AnimatorGraphService.cs"),
                Path.Combine(repoRoot, "AssetStudio.Avalonia", "Services", "AnimationClipMetadataService.cs"),
                Path.Combine(repoRoot, "AssetStudio.Avalonia", "ViewModels", "AnimatorGraphViewModel.cs")
            };

            foreach (var file in pureFiles)
            {
                Assert.True(File.Exists(file), $"Expected Block 5B file not found: {file}");
                var content = File.ReadAllText(file);
                Assert.False(AvaloniaUsingRegex.IsMatch(content), $"Pure Service/ViewModel must not import Avalonia: {file}");
            }

            var dialogAxaml = Path.Combine(repoRoot, "AssetStudio.Avalonia", "AnimatorGraphDialog.axaml");
            Assert.True(File.Exists(dialogAxaml), $"Expected AXAML file not found: {dialogAxaml}");
            var axamlContent = File.ReadAllText(dialogAxaml);
            Assert.Contains("x:CompileBindings=\"True\"", axamlContent, StringComparison.Ordinal);
            Assert.Contains("x:DataType=\"vm:AnimatorGraphViewModel\"", axamlContent, StringComparison.Ordinal);
            Assert.False(AxamlClickAttributeRegex.IsMatch(axamlContent), $"AXAML dialog must not contain Click= handlers: {dialogAxaml}");

            // Reflection check: zero Avalonia types anywhere on ViewModel or Canvas DTO properties
            foreach (var type in new[]
            {
                typeof(AnimatorGraphViewModel),
                typeof(AnimatorGraphCanvasNode),
                typeof(AnimatorGraphCanvasEdge),
                typeof(AnimatorGraphDocument),
                typeof(AnimationClipMetadataDocument)
            })
            {
                foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    AssertNotAvaloniaType(prop.PropertyType, $"{type.Name}.{prop.Name}");
                }
            }
        }

        // =========================================================================
        // Helpers
        // =========================================================================

        private static SerializedFile CreateSyntheticSerializedFile(string fileName, string unityVersion)
        {
            var sf = (SerializedFile)RuntimeHelpers.GetUninitializedObject(typeof(SerializedFile));
            sf.fileName = fileName;
            sf.fullName = fileName;
            sf.originalPath = fileName;
            sf.unityVersion = unityVersion;
            sf.Objects = new List<AssetStudio.Object>();
            sf.ObjectsDic = new Dictionary<long, AssetStudio.Object>();
            sf.ObjectInfoDic = new Dictionary<long, ObjectInfo>();
            sf.m_Externals = new List<FileIdentifier>();
            // Initialize private objectCollectionLock needed by SerializedFile.AddObject/TryGetObject
            var lockField = typeof(SerializedFile).GetField("objectCollectionLock", BindingFlags.NonPublic | BindingFlags.Instance);
            lockField?.SetValue(sf, new object());
            return sf;
        }

        private static AnimationClip CreateSyntheticClip(SerializedFile sf, long pathId, string name)
        {
            var clip = (AnimationClip)RuntimeHelpers.GetUninitializedObject(typeof(AnimationClip));
            clip.assetsFile = sf;
            clip.m_PathID = pathId;
            clip.type = ClassIDType.AnimationClip;
            clip.m_Name = name;
            clip.m_SampleRate = 30f;
            clip.version = new[] { 2022, 3, 20, 1 };
            sf.AddObject(clip);
            return clip;
        }

        private static AnimatorController BuildRichMultiLayerController(
            SerializedFile sf,
            long pathId,
            string name,
            AnimationClip[] clips)
        {
            // Hash IDs for TOS
            const uint hSpeed = 101;
            const uint hCombo = 102;
            const uint hGrounded = 103;
            const uint hAttackTrigger = 104;
            const uint hBaseLayer = 200;
            const uint hIdle = 201;
            const uint hLoco1D = 202;
            const uint hStrafe2D = 203;
            const uint hAttack = 204;
            const uint hHitReact = 205;
            const uint hGroundTag = 250;

            // Layer 0 States:
            // 0: Idle (Clip 0) -> Locomotion1D (Greater Speed 0.1) & -> Strafe2D (IfNot IsGrounded)
            // 1: Locomotion1D (1D BlendTree: Walk @0.5, Run @1.0) -> Idle (Less Speed 0.1) & -> Strafe2D (NotEqual ComboIndex 0)
            // 2: Strafe2D (2D Freeform Directional: StrafeL (-1,0), StrafeR (1,0.5)) -> Idle (ExitTime)
            // 3: Attack (Clip 5) -> Idle (ExitTime)
            var idleBt = CreateBlendTreeConstant(new[]
            {
                CreateBlendTreeNode(0, 0, 0, Array.Empty<uint>(), clipId: 0)
            });

            var locoRoot = CreateBlendTreeNode(blendType: 0, eventId: hSpeed, eventYId: 0, childIndices: new uint[] { 1, 2 }, clipId: uint.MaxValue);
            locoRoot.m_Blend1dData = CreateBlend1d(0.5f, 1.0f);
            var locoBt = CreateBlendTreeConstant(new[]
            {
                locoRoot,
                CreateBlendTreeNode(0, 0, 0, Array.Empty<uint>(), clipId: 1),
                CreateBlendTreeNode(0, 0, 0, Array.Empty<uint>(), clipId: 2)
            });

            var strafeRoot = CreateBlendTreeNode(blendType: 2, eventId: hSpeed, eventYId: hCombo, childIndices: new uint[] { 1, 2 }, clipId: uint.MaxValue);
            strafeRoot.m_Blend2dData = CreateBlend2d(new Vector2(-1f, 0f), new Vector2(1f, 0.5f));
            var strafeBt = CreateBlendTreeConstant(new[]
            {
                strafeRoot,
                CreateBlendTreeNode(0, 0, 0, Array.Empty<uint>(), clipId: 3),
                CreateBlendTreeNode(0, 0, 0, Array.Empty<uint>(), clipId: 4)
            });

            var attackBt = CreateBlendTreeConstant(new[]
            {
                CreateBlendTreeNode(0, 0, 0, Array.Empty<uint>(), clipId: 5)
            });

            var stIdle = CreateStateConstant(
                nameId: hIdle,
                blendTrees: new[] { idleBt },
                transitions: new[]
                {
                    CreateTransition(1, hasExitTime: false, exitTime: 0f, duration: 0.15f, conditions: new[] { CreateCondition(3, hSpeed, 0.1f) }),
                    CreateTransition(2, hasExitTime: false, exitTime: 0f, duration: 0.2f, conditions: new[] { CreateCondition(2, hGrounded, 0f) })
                },
                tagId: hGroundTag,
                loop: true);

            var stLoco = CreateStateConstant(
                nameId: hLoco1D,
                blendTrees: new[] { locoBt },
                transitions: new[]
                {
                    CreateTransition(0, hasExitTime: false, exitTime: 0f, duration: 0.2f, conditions: new[] { CreateCondition(4, hSpeed, 0.1f) }),
                    CreateTransition(2, hasExitTime: false, exitTime: 0f, duration: 0.1f, conditions: new[] { CreateCondition(7, hCombo, 0f) })
                },
                loop: true);

            var stStrafe = CreateStateConstant(
                nameId: hStrafe2D,
                blendTrees: new[] { strafeBt },
                transitions: new[]
                {
                    CreateTransition(0, hasExitTime: true, exitTime: 0.9f, duration: 0.15f, conditions: Array.Empty<ConditionConstant>())
                },
                loop: true);

            var stAttack = CreateStateConstant(
                nameId: hAttack,
                blendTrees: new[] { attackBt },
                transitions: new[]
                {
                    CreateTransition(0, hasExitTime: true, exitTime: 0.85f, duration: 0.1f, conditions: Array.Empty<ConditionConstant>())
                },
                loop: false);

            var anyToAttack = CreateTransition(
                dest: 3,
                hasExitTime: false,
                exitTime: 0f,
                duration: 0.05f,
                conditions: new[]
                {
                    CreateCondition(1, hAttackTrigger, 0f),
                    CreateCondition(6, hCombo, 1f)
                },
                canTransitionToSelf: true);

            var entrySelector = (SelectorStateConstant)RuntimeHelpers.GetUninitializedObject(typeof(SelectorStateConstant));
            entrySelector.m_isEntry = true;
            entrySelector.m_FullPathID = hBaseLayer;
            var entryTr = (SelectorTransitionConstant)RuntimeHelpers.GetUninitializedObject(typeof(SelectorTransitionConstant));
            entryTr.m_Destination = 1; // Entry -> Locomotion1D when IsGrounded
            entryTr.m_ConditionConstantArray = new[] { CreateCondition(1, hGrounded, 0f) };
            entrySelector.m_TransitionConstantArray = new[] { entryTr };

            var sm0 = CreateStateMachine(
                states: new[] { stIdle, stLoco, stStrafe, stAttack },
                defaultState: 0,
                anyStateTransitions: new[] { anyToAttack },
                selectors: new[] { entrySelector });

            // Layer 1 (Additive): single state HitReact
            var hitBt = CreateBlendTreeConstant(new[]
            {
                CreateBlendTreeNode(0, 0, 0, Array.Empty<uint>(), clipId: 6)
            });
            var stHit = CreateStateConstant(hHitReact, new[] { hitBt }, Array.Empty<TransitionConstant>());
            var sm1 = CreateStateMachine(new[] { stHit }, defaultState: 0, anyStateTransitions: null, selectors: null);

            var layer0 = CreateLayer(binding: hBaseLayer, stateMachineIndex: 0, blendingMode: 0, weight: 1.0f, ikPass: true, syncedTiming: false);
            var layer1 = CreateLayer(binding: 99999, stateMachineIndex: 1, blendingMode: 1, weight: 0.65f, ikPass: false, syncedTiming: true);

            var defaultValues = (ValueArray)RuntimeHelpers.GetUninitializedObject(typeof(ValueArray));
            defaultValues.m_FloatValues = new[] { 1.25f };
            defaultValues.m_IntValues = new[] { 2 };
            defaultValues.m_BoolValues = new[] { true, false };

            var ctrlConst = (ControllerConstant)RuntimeHelpers.GetUninitializedObject(typeof(ControllerConstant));
            ctrlConst.m_LayerArray = new[] { layer0, layer1 };
            ctrlConst.m_StateMachineArray = new[] { sm0, sm1 };
            ctrlConst.m_Values = CreateValueArrayConstant(new[]
            {
                CreateValueConstant(hSpeed, 1, 0),
                CreateValueConstant(hCombo, 3, 0),
                CreateValueConstant(hGrounded, 4, 0),
                CreateValueConstant(hAttackTrigger, 9, 1)
            });
            ctrlConst.m_DefaultValues = defaultValues;

            var controller = (AnimatorController)RuntimeHelpers.GetUninitializedObject(typeof(AnimatorController));
            controller.assetsFile = sf;
            controller.m_PathID = pathId;
            controller.type = ClassIDType.AnimatorController;
            controller.m_Name = name;
            controller.m_MultiThreadedStateMachine = true;
            controller.m_Controller = ctrlConst;
            controller.m_TOS = new[]
            {
                new KeyValuePair<uint, string>(hSpeed, "Speed"),
                new KeyValuePair<uint, string>(hCombo, "ComboIndex"),
                new KeyValuePair<uint, string>(hGrounded, "IsGrounded"),
                new KeyValuePair<uint, string>(hAttackTrigger, "AttackTrigger"),
                new KeyValuePair<uint, string>(hBaseLayer, "Base Layer"),
                new KeyValuePair<uint, string>(hIdle, "Idle"),
                new KeyValuePair<uint, string>(hLoco1D, "Locomotion1D"),
                new KeyValuePair<uint, string>(hStrafe2D, "Strafe2D"),
                new KeyValuePair<uint, string>(hAttack, "Attack"),
                new KeyValuePair<uint, string>(hHitReact, "HitReact"),
                new KeyValuePair<uint, string>(hGroundTag, "GroundTag")
            };
            controller.m_AnimationClips = clips
                .Select(c => new PPtr<AnimationClip>(0, c.m_PathID, sf))
                .ToArray();

            sf.AddObject(controller);
            return controller;
        }

        private static AnimationClipOverride CreateClipOverride(SerializedFile sf, long origPathId, long overridePathId)
        {
            var ov = (AnimationClipOverride)RuntimeHelpers.GetUninitializedObject(typeof(AnimationClipOverride));
            ov.m_OriginalClip = new PPtr<AnimationClip>(0, origPathId, sf);
            ov.m_OverrideClip = new PPtr<AnimationClip>(0, overridePathId, sf);
            return ov;
        }

        private static LayerConstant CreateLayer(uint binding, uint stateMachineIndex, int blendingMode, float weight, bool ikPass = false, bool syncedTiming = false)
        {
            var layer = (LayerConstant)RuntimeHelpers.GetUninitializedObject(typeof(LayerConstant));
            layer.m_Binding = binding;
            layer.m_StateMachineIndex = stateMachineIndex;
            layer.m_StateMachineMotionSetIndex = 0;
            layer.m_LayerBlendingMode = blendingMode;
            layer.m_DefaultWeight = weight;
            layer.m_IKPass = ikPass;
            layer.m_SyncedLayerAffectsTiming = syncedTiming;
            return layer;
        }

        private static StateMachineConstant CreateStateMachine(
            StateConstant[] states,
            uint defaultState,
            TransitionConstant[]? anyStateTransitions,
            SelectorStateConstant[]? selectors)
        {
            var sm = (StateMachineConstant)RuntimeHelpers.GetUninitializedObject(typeof(StateMachineConstant));
            sm.m_StateConstantArray = states;
            sm.m_DefaultState = defaultState;
            sm.m_AnyStateTransitionConstantArray = anyStateTransitions ?? Array.Empty<TransitionConstant>();
            sm.m_SelectorStateConstantArray = selectors;
            return sm;
        }

        private static StateConstant CreateStateConstant(
            uint nameId,
            BlendTreeConstant[] blendTrees,
            TransitionConstant[]? transitions,
            uint tagId = 0,
            bool loop = false)
        {
            var st = (StateConstant)RuntimeHelpers.GetUninitializedObject(typeof(StateConstant));
            st.m_NameID = nameId;
            st.m_TagID = tagId;
            st.m_Speed = 1f;
            st.m_Loop = loop;
            st.m_WriteDefaultValues = true;
            st.m_BlendTreeConstantArray = blendTrees;
            st.m_BlendTreeConstantIndexArray = new[] { 0 };
            st.m_TransitionConstantArray = transitions ?? Array.Empty<TransitionConstant>();
            return st;
        }

        private static BlendTreeConstant CreateBlendTreeConstant(BlendTreeNodeConstant[] nodes)
        {
            var bt = (BlendTreeConstant)RuntimeHelpers.GetUninitializedObject(typeof(BlendTreeConstant));
            bt.m_NodeArray = nodes;
            return bt;
        }

        private static BlendTreeNodeConstant CreateBlendTreeNode(
            uint blendType,
            uint eventId,
            uint eventYId,
            uint[] childIndices,
            uint clipId)
        {
            var n = (BlendTreeNodeConstant)RuntimeHelpers.GetUninitializedObject(typeof(BlendTreeNodeConstant));
            n.m_BlendType = blendType;
            n.m_BlendEventID = eventId;
            n.m_BlendEventYID = eventYId;
            n.m_ChildIndices = childIndices;
            n.m_ClipID = clipId;
            n.m_Duration = 1f;
            return n;
        }

        private static Blend1dDataConstant CreateBlend1d(params float[] thresholds)
        {
            var data = (Blend1dDataConstant)RuntimeHelpers.GetUninitializedObject(typeof(Blend1dDataConstant));
            data.m_ChildThresholdArray = thresholds;
            return data;
        }

        private static Blend2dDataConstant CreateBlend2d(params Vector2[] positions)
        {
            var data = (Blend2dDataConstant)RuntimeHelpers.GetUninitializedObject(typeof(Blend2dDataConstant));
            data.m_ChildPositionArray = positions;
            return data;
        }

        private static TransitionConstant CreateTransition(
            uint dest,
            bool hasExitTime,
            float exitTime,
            float duration,
            ConditionConstant[] conditions,
            bool canTransitionToSelf = false)
        {
            var tr = (TransitionConstant)RuntimeHelpers.GetUninitializedObject(typeof(TransitionConstant));
            tr.m_DestinationState = dest;
            tr.m_HasExitTime = hasExitTime;
            tr.m_ExitTime = exitTime;
            tr.m_TransitionDuration = duration;
            tr.m_HasFixedDuration = true;
            tr.m_CanTransitionToSelf = canTransitionToSelf;
            tr.m_ConditionConstantArray = conditions;
            return tr;
        }

        private static ConditionConstant CreateCondition(uint mode, uint eventId, float threshold)
        {
            var c = (ConditionConstant)RuntimeHelpers.GetUninitializedObject(typeof(ConditionConstant));
            c.m_ConditionMode = mode;
            c.m_EventID = eventId;
            c.m_EventThreshold = threshold;
            return c;
        }

        private static ValueArrayConstant CreateValueArrayConstant(ValueConstant[] values)
        {
            var vac = (ValueArrayConstant)RuntimeHelpers.GetUninitializedObject(typeof(ValueArrayConstant));
            vac.m_ValueArray = values;
            return vac;
        }

        private static ValueConstant CreateValueConstant(uint id, uint type, uint index)
        {
            var vc = (ValueConstant)RuntimeHelpers.GetUninitializedObject(typeof(ValueConstant));
            vc.m_ID = id;
            vc.m_Type = type;
            vc.m_Index = index;
            return vc;
        }

        private static AnimationClip BuildModernMuscleClip(
            SerializedFile sf,
            long pathId,
            string name,
            float sampleRate,
            float startTime,
            float stopTime,
            uint streamedCurves,
            uint denseCurves,
            int denseFrames,
            int constantCurves,
            GenericBinding[] bindings)
        {
            var sClip = (StreamedClip)RuntimeHelpers.GetUninitializedObject(typeof(StreamedClip));
            sClip.data = Array.Empty<uint>();
            sClip.curveCount = streamedCurves;

            var dClip = (DenseClip)RuntimeHelpers.GetUninitializedObject(typeof(DenseClip));
            dClip.m_CurveCount = denseCurves;
            dClip.m_FrameCount = denseFrames;
            dClip.m_SampleRate = sampleRate;
            dClip.m_SampleArray = Array.Empty<float>();

            var cClip = (ConstantClip)RuntimeHelpers.GetUninitializedObject(typeof(ConstantClip));
            cClip.data = new float[constantCurves];

            var inner = (Clip)RuntimeHelpers.GetUninitializedObject(typeof(Clip));
            inner.m_StreamedClip = sClip;
            inner.m_DenseClip = dClip;
            inner.m_ConstantClip = cClip;

            var mc = (ClipMuscleConstant)RuntimeHelpers.GetUninitializedObject(typeof(ClipMuscleConstant));
            mc.m_Clip = inner;
            mc.m_StartTime = startTime;
            mc.m_StopTime = stopTime;

            var clip = (AnimationClip)RuntimeHelpers.GetUninitializedObject(typeof(AnimationClip));
            clip.assetsFile = sf;
            clip.m_PathID = pathId;
            clip.type = ClassIDType.AnimationClip;
            clip.m_Name = name;
            clip.m_SampleRate = sampleRate;
            clip.m_MuscleClip = mc;
            clip.m_ClipBindingConstant = new AnimationClipBindingConstant
            {
                genericBindings = bindings
            };
            return clip;
        }

        private static AABB CreateBounds(Vector3 center, Vector3 extent)
        {
            var aabb = (AABB)RuntimeHelpers.GetUninitializedObject(typeof(AABB));
            aabb.m_Center = center;
            aabb.m_Extent = extent;
            return aabb;
        }

        private static AnimationEvent CreateAnimationEvent(
            float time,
            string functionName,
            string data,
            float floatParam,
            int intParam,
            long objRefPathId,
            int messageOptions)
        {
            var evt = (AnimationEvent)RuntimeHelpers.GetUninitializedObject(typeof(AnimationEvent));
            evt.time = time;
            evt.functionName = functionName;
            evt.data = data;
            evt.floatParameter = floatParam;
            evt.intParameter = intParam;
            evt.messageOptions = messageOptions;
            evt.objectReferenceParameter = new PPtr<AssetStudio.Object>(0, objRefPathId, null!);
            return evt;
        }

        private static Vector3Curve CreateVector3Curve(string path, params float[] times)
        {
            var curve = (AnimationCurve<Vector3>)RuntimeHelpers.GetUninitializedObject(typeof(AnimationCurve<Vector3>));
            curve.m_Curve = times.Select(t =>
            {
                var kf = (Keyframe<Vector3>)RuntimeHelpers.GetUninitializedObject(typeof(Keyframe<Vector3>));
                kf.time = t;
                return kf;
            }).ToArray();

            var vc = (Vector3Curve)RuntimeHelpers.GetUninitializedObject(typeof(Vector3Curve));
            vc.path = path;
            vc.curve = curve;
            return vc;
        }

        private static FloatCurve CreateFloatCurve(string path, string attribute, params float[] times)
        {
            var curve = (AnimationCurve<float>)RuntimeHelpers.GetUninitializedObject(typeof(AnimationCurve<float>));
            curve.m_Curve = times.Select(t =>
            {
                var kf = (Keyframe<float>)RuntimeHelpers.GetUninitializedObject(typeof(Keyframe<float>));
                kf.time = t;
                return kf;
            }).ToArray();

            var fc = (FloatCurve)RuntimeHelpers.GetUninitializedObject(typeof(FloatCurve));
            fc.path = path;
            fc.attribute = attribute;
            fc.curve = curve;
            return fc;
        }

        private static CompressedAnimationCurve CreateCorruptCompressedCurve(string path)
        {
            var cac = (CompressedAnimationCurve)RuntimeHelpers.GetUninitializedObject(typeof(CompressedAnimationCurve));
            cac.m_Path = path;
            cac.m_Times = new PackedIntVector
            {
                m_NumItems = 10,
                m_BitSize = 99, // Invalid > 32 triggers InvalidDataException in UnpackInts
                m_Data = Array.Empty<byte>()
            };
            return cac;
        }

        private static void AssertNoStateBoxesOverlap(IReadOnlyList<AnimatorGraphState> states)
        {
            double w = AnimatorGraphService.DefaultNodeWidth;
            double h = AnimatorGraphService.DefaultNodeHeight;

            for (int i = 0; i < states.Count; i++)
            {
                for (int j = i + 1; j < states.Count; j++)
                {
                    var a = states[i];
                    var b = states[j];
                    bool separatedX = a.LayoutX + w <= b.LayoutX || b.LayoutX + w <= a.LayoutX;
                    bool separatedY = a.LayoutY + h <= b.LayoutY || b.LayoutY + h <= a.LayoutY;
                    Assert.True(
                        separatedX || separatedY,
                        $"States '{a.Name}' ({a.LayoutX},{a.LayoutY}) and '{b.Name}' ({b.LayoutX},{b.LayoutY}) overlap!");
                }
            }
        }

        private static bool HasUtf8Bom(byte[] bytes)
        {
            return bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        }

        private static string CreateTempDirectory(string prefix)
        {
            var dir = Path.Combine(Path.GetTempPath(), $"AssetStudio_Block5B_{prefix}_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            return dir;
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

        private static string LocateRepositoryRoot()
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current != null)
            {
                if (File.Exists(Path.Combine(current.FullName, "AssetStudio.Avalonia", "AssetStudio.Avalonia.csproj")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            throw new DirectoryNotFoundException("Unable to locate repository root.");
        }

        private static void AssertNotAvaloniaType(Type type, string memberName)
        {
            var ns = type.Namespace ?? string.Empty;
            Assert.False(
                string.Equals(ns, "Avalonia", StringComparison.Ordinal) || ns.StartsWith("Avalonia.", StringComparison.Ordinal),
                $"Type member '{memberName}' must not reference Avalonia type '{type.FullName}'.");
            if (type.IsGenericType)
            {
                foreach (var arg in type.GetGenericArguments())
                {
                    AssertNotAvaloniaType(arg, memberName);
                }
            }
        }
    }
}
