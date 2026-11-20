using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using AssetStudio;
using AssetStudio.Avalonia;
using AssetStudio.Avalonia.ViewModels;
using Xunit;

namespace AssetStudio.Tests
{
    public class PreviewSessionViewModelTests
    {
        [Fact]
        public void PreviewSessionViewModel_SurfaceTransitions_AcrossAllPreviewSurfaceKinds_UpdateVisibilityAndMetadata()
        {
            var vm = new PreviewSessionViewModel();

            Assert.Equal(PreviewSurfaceKind.None, vm.ActiveKind);
            Assert.False(vm.HasActivePreview);
            Assert.False(vm.IsTextSurfaceVisible);
            Assert.False(vm.IsImageSurfaceVisible);
            Assert.False(vm.Is3DSurfaceVisible);
            Assert.False(vm.IsMediaSurfaceVisible);
            Assert.Equal(PreviewSessionViewModel.DefaultPlaceholderTitle, vm.PreviewTitle);

            var allKinds = Enum.GetValues<PreviewSurfaceKind>();
            Assert.Equal(16, allKinds.Length);

            foreach (var kind in allKinds)
            {
                var asset = CreateSyntheticAssetItem($"Asset_{kind}", ClassIDType.MonoBehaviour);
                var metadata = kind == PreviewSurfaceKind.None ? string.Empty : $"Metadata for {kind}";
                vm.ActivateSurface(
                    kind,
                    asset: kind == PreviewSurfaceKind.None ? null : asset,
                    title: kind == PreviewSurfaceKind.None ? null : $"Title_{kind}",
                    metadataSummary: metadata,
                    isLoading: false,
                    loadingStatusText: $"Loaded {kind}");

                Assert.Equal(kind, vm.ActiveKind);
                Assert.Equal(kind != PreviewSurfaceKind.None, vm.HasActivePreview);

                var expectedTextSurface = kind is PreviewSurfaceKind.Text
                    or PreviewSurfaceKind.Shader
                    or PreviewSurfaceKind.MonoBehaviour
                    or PreviewSurfaceKind.AnimatorGraph
                    or PreviewSurfaceKind.Prefab;
                var expectedImageSurface = kind is PreviewSurfaceKind.Texture
                    or PreviewSurfaceKind.Sprite
                    or PreviewSurfaceKind.Font
                    or PreviewSurfaceKind.Animation2D;
                var expected3DSurface = kind is PreviewSurfaceKind.Mesh3D
                    or PreviewSurfaceKind.Animation3D
                    or PreviewSurfaceKind.Avatar
                    or PreviewSurfaceKind.Material;
                var expectedMediaSurface = kind is PreviewSurfaceKind.Audio
                    or PreviewSurfaceKind.Video;

                Assert.Equal(expectedTextSurface, vm.IsTextSurfaceVisible);
                Assert.Equal(expectedImageSurface, vm.IsImageSurfaceVisible);
                Assert.Equal(expected3DSurface, vm.Is3DSurfaceVisible);
                Assert.Equal(expectedMediaSurface, vm.IsMediaSurfaceVisible);
                Assert.Equal(!string.IsNullOrWhiteSpace(metadata), vm.IsInfoOverlayVisible);
            }

            // Verify IsDisplayInfoEnabled toggles IsInfoOverlayVisible
            vm.PreviewMetadataSummary = "Vertices: 4,096";
            vm.IsDisplayInfoEnabled = true;
            Assert.True(vm.IsInfoOverlayVisible);

            vm.IsDisplayInfoEnabled = false;
            Assert.False(vm.IsInfoOverlayVisible);
        }

        [Fact]
        public async Task PreviewSessionViewModel_20RapidPreviewAndDumpBursts_CancelsStaleRequestsAndTruncatesLargeText()
        {
            using var vm = new PreviewSessionViewModel();

            // 1) Truncation rules at 100,000 characters
            Assert.Equal("Fallback text", PreviewSessionViewModel.TruncatePreviewText(null, "Fallback text"));
            var exactLimit = new string('X', PreviewSessionViewModel.MaxPreviewTextChars);
            Assert.Same(exactLimit, PreviewSessionViewModel.TruncatePreviewText(exactLimit));

            var oversized = new string('Y', PreviewSessionViewModel.MaxPreviewTextChars + 250);
            var truncated = PreviewSessionViewModel.TruncatePreviewText(oversized);
            Assert.True(truncated.Length > PreviewSessionViewModel.MaxPreviewTextChars);
            Assert.StartsWith(new string('Y', 64), truncated, StringComparison.Ordinal);
            Assert.Contains("[Preview truncated: content is too large", truncated, StringComparison.Ordinal);

            // 2) 20 rapid async preview requests -> latest wins, 19+ cancelled
            const int burstCount = 20;
            var appliedPreviews = new ConcurrentQueue<(long RequestId, string Content)>();
            var previewTasks = new Task<(bool Applied, long RequestId, string? Result)>[burstCount];

            for (var i = 0; i < burstCount; i++)
            {
                var idx = i;
                var isLast = idx == burstCount - 1;
                previewTasks[i] = vm.RunLatestPreviewAsync<string>(
                    async (_, ct) =>
                    {
                        if (!isLast)
                        {
                            await Task.Delay(500, ct).ConfigureAwait(false);
                        }
                        else
                        {
                            await Task.Delay(15, ct).ConfigureAwait(false);
                        }

                        ct.ThrowIfCancellationRequested();
                        return $"PreviewPayload_{idx:D2}";
                    },
                    (reqId, text) =>
                    {
                        vm.ShowTextPreview(text, PreviewSurfaceKind.Shader);
                        appliedPreviews.Enqueue((reqId, text));
                    });

                if (!isLast)
                {
                    await Task.Delay(2);
                }
            }

            var previewOutcomes = await Task.WhenAll(previewTasks).WaitAsync(TimeSpan.FromSeconds(10));
            var previewWinner = Assert.Single(appliedPreviews);
            Assert.Equal($"PreviewPayload_{burstCount - 1:D2}", previewWinner.Content);
            Assert.Equal(vm.CurrentPreviewRequestId, previewWinner.RequestId);
            Assert.True(vm.CancelledPreviewRequestCount >= burstCount - 1);
            Assert.True(previewOutcomes[^1].Applied);
            Assert.All(previewOutcomes.Take(burstCount - 1), o => Assert.False(o.Applied));
            Assert.Equal($"PreviewPayload_{burstCount - 1:D2}", vm.TextContent);
            Assert.False(vm.IsLoading);

            // 3) 20 rapid async dump requests -> latest wins, 19+ cancelled
            var appliedDumps = new ConcurrentQueue<(long RequestId, string Content)>();
            var dumpTasks = new Task<(bool Applied, long RequestId, string? Result)>[burstCount];

            for (var i = 0; i < burstCount; i++)
            {
                var idx = i;
                var isLast = idx == burstCount - 1;
                dumpTasks[i] = vm.RunLatestDumpAsync<string>(
                    async (_, ct) =>
                    {
                        if (!isLast)
                        {
                            await Task.Delay(500, ct).ConfigureAwait(false);
                        }
                        else
                        {
                            await Task.Delay(15, ct).ConfigureAwait(false);
                        }

                        ct.ThrowIfCancellationRequested();
                        return $"DumpPayload_{idx:D2}_" + new string('Z', PreviewSessionViewModel.MaxPreviewTextChars + 50);
                    },
                    (reqId, dumpText) =>
                    {
                        vm.CompleteDumpRequest(reqId, dumpText);
                        appliedDumps.Enqueue((reqId, vm.DumpTextContent));
                    });

                if (!isLast)
                {
                    await Task.Delay(2);
                }
            }

            var dumpOutcomes = await Task.WhenAll(dumpTasks).WaitAsync(TimeSpan.FromSeconds(10));
            var dumpWinner = Assert.Single(appliedDumps);
            Assert.Equal(vm.CurrentDumpRequestId, dumpWinner.RequestId);
            Assert.True(vm.CancelledDumpRequestCount >= burstCount - 1);
            Assert.True(dumpOutcomes[^1].Applied);
            Assert.All(dumpOutcomes.Take(burstCount - 1), o => Assert.False(o.Applied));
            Assert.StartsWith($"DumpPayload_{burstCount - 1:D2}_", vm.DumpTextContent, StringComparison.Ordinal);
            Assert.Contains("[Preview truncated: content is too large", vm.DumpTextContent, StringComparison.Ordinal);
            Assert.False(vm.IsDumpLoading);

            // Stale requestId passed to CompleteDumpRequest / FailDumpRequest is ignored
            Assert.False(vm.CompleteDumpRequest(dumpWinner.RequestId - 1, "StaleDump"));
            Assert.False(vm.FailDumpRequest(dumpWinner.RequestId - 1, "StaleError"));
            Assert.StartsWith($"DumpPayload_{burstCount - 1:D2}_", vm.DumpTextContent, StringComparison.Ordinal);
        }

        [Fact]
        public void PreviewSessionViewModel_DialogueCardsAndDisplayModeSwitching_PreservesCollectionInstance()
        {
            var vm = new PreviewSessionViewModel();
            var cardsInstance = vm.DialogueCards;

            var resetCount = 0;
            cardsInstance.CollectionChanged += (_, e) =>
            {
                if (e.Action == NotifyCollectionChangedAction.Reset)
                {
                    resetCount++;
                }
            };

            var sampleCards = new[]
            {
                CreateDialogueCard("001 | Intro", "0x000010", "Welcome to the sanctuary.", "Dialogue | Intro | 25 bytes"),
                CreateDialogueCard("002 | Note", "0x000040", "Tutorial trigger note.", "Note | trigger | 22 bytes"),
            };

            var asset = CreateSyntheticAssetItem("episode_01", ClassIDType.TextAsset);
            vm.ShowDialogueCards(
                asset,
                sampleCards,
                formatName: "Ammo Episode Binary",
                parsedStringCount: 42,
                plainTextScript: "001: Welcome to the sanctuary.\n002: Tutorial trigger note.",
                detailsText: "Format: Ammo Episode Binary\nStrings: 42");

            Assert.Same(cardsInstance, vm.DialogueCards);
            Assert.Equal(1, resetCount);
            Assert.Equal(2, vm.DialogueCards.Count);
            Assert.Equal(PreviewSurfaceKind.Text, vm.ActiveKind);
            Assert.True(vm.IsDialogueCardsVisible);
            Assert.False(vm.IsTextSurfaceVisible);
            Assert.Equal(TextAssetDisplayMode.Cards, vm.DialogueDisplayMode);
            Assert.Equal("episode_01", vm.PreviewTitle);
            Assert.Contains("Ammo Episode Binary", vm.PreviewSubtitle, StringComparison.Ordinal);

            // Switch to Text mode -> hides cards and populates TextContent with script
            vm.SetDialogueDisplayMode(TextAssetDisplayMode.Text);
            Assert.Equal(TextAssetDisplayMode.Text, vm.DialogueDisplayMode);
            Assert.False(vm.IsDialogueCardsVisible);
            Assert.True(vm.IsTextSurfaceVisible);
            Assert.Contains("Welcome to the sanctuary.", vm.TextContent, StringComparison.Ordinal);

            // Switch to Info mode -> populates TextContent with details
            vm.SetDialogueDisplayMode(TextAssetDisplayMode.Info);
            Assert.Equal(TextAssetDisplayMode.Info, vm.DialogueDisplayMode);
            Assert.False(vm.IsDialogueCardsVisible);
            Assert.Contains("Format: Ammo Episode Binary", vm.TextContent, StringComparison.Ordinal);

            // Switch back to Cards mode
            vm.SetDialogueDisplayMode(TextAssetDisplayMode.Cards);
            Assert.Equal(TextAssetDisplayMode.Cards, vm.DialogueDisplayMode);
            Assert.True(vm.IsDialogueCardsVisible);
            Assert.False(vm.IsTextSurfaceVisible);

            // ClearDialogueCards empties collection without replacing instance
            vm.ClearDialogueCards();
            Assert.Same(cardsInstance, vm.DialogueCards);
            Assert.Empty(vm.DialogueCards);
            Assert.False(vm.IsDialogueCardsVisible);
        }

        [Fact]
        public void PreviewSessionViewModel_CandidateSelection_AndTextureChannels_AndPropertyChangedDeduplication()
        {
            var vm = new PreviewSessionViewModel();
            var candidatesInstance = vm.PreviewCandidates;

            // 1) Candidate management
            Assert.False(vm.HasCandidates);
            Assert.Null(vm.SelectedPreviewCandidate);

            var c1 = new PreviewCandidateItem { AvatarId = "av1", MeshId = "m1", Label = "Avatar 1 -> Mesh 1" };
            var c2 = new PreviewCandidateItem { AvatarId = "av1", MeshId = "m2", Label = "Avatar 1 -> Mesh 2" };
            var c3 = new PreviewCandidateItem { AvatarId = "av2", MeshId = "m3", ModelGroupId = "grp1", Label = "Model group: Hero (3 parts)" };

            vm.SetCandidates(new[] { c1, c2 }, selectedCandidate: c2, label: "Animation Target");
            Assert.Same(candidatesInstance, vm.PreviewCandidates);
            Assert.True(vm.HasCandidates);
            Assert.Equal(2, vm.PreviewCandidates.Count);
            Assert.Same(c2, vm.SelectedPreviewCandidate);
            Assert.Equal("Animation Target", vm.CandidateSelectorLabel);

            vm.AppendCandidates(new[] { c3 });
            Assert.Equal(3, vm.PreviewCandidates.Count);
            Assert.Same(c2, vm.SelectedPreviewCandidate);

            Assert.True(vm.SelectCandidateAt(2));
            Assert.Same(c3, vm.SelectedPreviewCandidate);
            Assert.True(vm.SelectedPreviewCandidate!.IsModelGroup);
            Assert.False(vm.SelectCandidateAt(99));

            // 2) Texture channel toggles & mask synchronization
            Assert.Equal(PreviewChannelMask.All, vm.ChannelMask);
            Assert.True(vm.ShowRed && vm.ShowGreen && vm.ShowBlue && vm.ShowAlpha);
            Assert.Equal(4, vm.ActiveChannelCount);
            Assert.Equal("R G B A", vm.ChannelSummaryText);

            Assert.False(vm.ToggleAlpha());
            Assert.False(vm.ShowAlpha);
            Assert.Equal(PreviewChannelMask.Rgb, vm.ChannelMask);
            Assert.Equal(3, vm.ActiveChannelCount);
            Assert.Equal("R G B", vm.ChannelSummaryText);

            vm.ToggleChannel(PreviewChannelMask.Green | PreviewChannelMask.Blue);
            Assert.Equal(PreviewChannelMask.Red, vm.ChannelMask);
            Assert.Equal(new[] { true, false, false, false }, vm.GetChannelsArray());
            Assert.Equal(1, vm.ActiveChannelCount);
            Assert.Equal("R", vm.ChannelSummaryText);

            vm.SetChannels(false, false, false, false);
            Assert.Equal(PreviewChannelMask.None, vm.ChannelMask);
            Assert.Equal(0, vm.ActiveChannelCount);
            Assert.Equal("None", vm.ChannelSummaryText);

            vm.ResetChannels();
            Assert.Equal(PreviewChannelMask.All, vm.ChannelMask);
            Assert.Equal("R G B A", vm.ChannelSummaryText);

            // 3) Cache badge updates
            vm.UpdateCacheBadge(fromCache: true, sourceKind: "mesh-geometry", materialsLoading: true);
            Assert.True(vm.IsCacheBadgeVisible);
            Assert.True(vm.LastPreviewWasFromCache);
            Assert.True(vm.LastPreviewMaterialsLoading);
            Assert.Equal("Cached Mesh • Loading materials...", vm.CacheBadgeText);

            vm.ClearCacheBadge();
            Assert.False(vm.IsCacheBadgeVisible);
            Assert.Equal(string.Empty, vm.CacheBadgeText);

            // 4) PropertyChanged deduplication on no-op assignments and double Reset()
            vm.Reset();
            Assert.Equal(PreviewSurfaceKind.None, vm.ActiveKind);
            Assert.False(vm.HasCandidates);
            Assert.Empty(vm.PreviewCandidates);
            Assert.Null(vm.SelectedPreviewCandidate);
            Assert.Equal(PreviewChannelMask.All, vm.ChannelMask);

            var firedProperties = new List<string>();
            vm.PropertyChanged += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.PropertyName))
                {
                    firedProperties.Add(e.PropertyName);
                }
            };

            // Re-assigning current default values and calling Reset() again must emit 0 PropertyChanged events
            vm.ActiveKind = PreviewSurfaceKind.None;
            vm.PreviewTitle = PreviewSessionViewModel.DefaultPlaceholderTitle;
            vm.TextContent = string.Empty;
            vm.DumpTextContent = string.Empty;
            vm.ShowRed = true;
            vm.ChannelMask = PreviewChannelMask.All;
            vm.Reset();
            Assert.Empty(firedProperties);
        }

        [Fact]
        public void MeshPreviewViewModel_3DRenderToggles_Summaries_AndMaterialSlots_WorkAndDeduplicateEvents()
        {
            var vm = new MeshPreviewViewModel();

            // Default 3D render toggle states
            Assert.Equal(0, vm.WireframeMode);
            Assert.False(vm.IsWireframeEnabled);
            Assert.Equal(0, vm.ShadeMode);
            Assert.Equal(0, vm.NormalMode);
            Assert.False(vm.ShowNormals);
            Assert.True(vm.ShowAxes);
            Assert.True(vm.ShowSkeleton);
            Assert.True(vm.ShowMaterials);
            Assert.True(vm.UseTextures);
            Assert.Equal(MeshPreviewViewPreset.Auto, vm.ViewPreset);
            Assert.Equal(1.0, vm.BoneScale);
            Assert.Equal("1.0x", vm.FormattedBoneScaleText);
            Assert.Equal(100.0, vm.MeshDensityPercent);
            Assert.Equal("Original", vm.FormattedMeshDensityText);

            // Wireframe mode cycles 0 -> 1 -> 2 -> 0
            Assert.Equal(1, vm.ToggleWireframe());
            Assert.True(vm.IsWireframeEnabled);
            Assert.Equal(2, vm.ToggleWireframe());
            Assert.True(vm.IsWireframeEnabled);
            Assert.Equal(0, vm.ToggleWireframe());
            Assert.False(vm.IsWireframeEnabled);

            // Modulo normalization for out-of-range wireframe assignments
            vm.WireframeMode = 5;
            Assert.Equal(2, vm.WireframeMode);
            vm.WireframeMode = -1;
            Assert.Equal(2, vm.WireframeMode);

            // Shading & texture toggle sync
            Assert.Equal(1, vm.ToggleShadeMode());
            Assert.False(vm.UseTextures);
            Assert.True(vm.ToggleUseTextures());
            Assert.Equal(0, vm.ShadeMode);

            // Normals, Axes, Skeleton, Materials toggles
            Assert.True(vm.ToggleNormals());
            Assert.Equal(1, vm.NormalMode);
            Assert.False(vm.ToggleAxes());
            Assert.False(vm.ToggleSkeleton());
            Assert.False(vm.ToggleMaterials());

            // BoneScale and MeshDensityPercent clamping
            vm.BoneScale = 99.0;
            Assert.Equal(MeshPreviewViewModel.MaxBoneScale, vm.BoneScale);
            Assert.Equal("5.0x", vm.FormattedBoneScaleText);
            vm.BoneScale = -3.0;
            Assert.Equal(MeshPreviewViewModel.MinBoneScale, vm.BoneScale);
            Assert.Equal("0.1x", vm.FormattedBoneScaleText);

            vm.MeshDensityPercent = 25.0;
            Assert.Equal(25.0, vm.MeshDensityPercent);
            Assert.Equal("25%", vm.FormattedMeshDensityText);
            vm.MeshDensityPercent = 0.0;
            Assert.Equal(MeshPreviewViewModel.MinMeshDensityPercent, vm.MeshDensityPercent);
            Assert.Equal("1%", vm.FormattedMeshDensityText);

            // Summaries & Material slots
            vm.SetMeshAndRigSummary(
                meshSummary: "Vertices: 12,400 | Submeshes: 3",
                associatedMaterialsSummary: "Associated Materials (2): Hero_Body, Hero_Armor",
                rigSummary: "Avatar: HeroAvatar | Bones: 68",
                meshName: "HeroMesh",
                avatarName: "HeroAvatar");

            Assert.Equal("Vertices: 12,400 | Submeshes: 3", vm.MeshSummaryText);
            Assert.Equal("Associated Materials (2): Hero_Body, Hero_Armor", vm.AssociatedMaterialsSummary);
            Assert.Equal("Avatar: HeroAvatar | Bones: 68", vm.RigSummaryText);
            Assert.Equal("HeroMesh", vm.ActiveMeshName);
            Assert.Equal("HeroAvatar", vm.ActiveAvatarName);

            var slots = new[]
            {
                new MeshMaterialPreviewSlot { SlotIndex = 0, MaterialName = "Hero_Body" },
                new MeshMaterialPreviewSlot { SlotIndex = 1, MaterialName = "Hero_Armor" },
            };
            vm.SetMaterialSlots(slots, selectedIndex: 1);
            Assert.True(vm.HasMaterialSlots);
            Assert.Equal(2, vm.MaterialSlots.Count);
            Assert.Same(slots[1], vm.SelectedMaterialSlot);

            vm.ClearMaterialSlots();
            Assert.False(vm.HasMaterialSlots);
            Assert.Null(vm.SelectedMaterialSlot);
        }

        [Fact]
        public void MeshPreviewViewModel_AnimationPlaybackStateMachine_LoadPlayPauseSeekClampSpeedClampLoopAndReset()
        {
            var vm = new MeshPreviewViewModel();

            // 1) Before animation is loaded, Play / TogglePlayPause are no-ops
            Assert.False(vm.IsAnimationLoaded);
            Assert.False(vm.IsPlaying);
            Assert.False(vm.IsPaused);
            Assert.Equal("Play", vm.PlayPauseButtonText);
            Assert.False(vm.TogglePlayPause());
            Assert.False(vm.Play());

            // 2) Load animation state (2.0s @ 30 FPS = 60 frames)
            var candidate1 = new PreviewCandidateItem { AvatarId = "av1", MeshId = "m1", Label = "HeroRig -> HeroMesh" };
            var candidate2 = new PreviewCandidateItem { AvatarId = "av1", MeshId = "m2", Label = "HeroRig -> AltMesh" };
            vm.SetAnimationState(
                isLoaded: true,
                clipName: "Hero_AttackCombo",
                avatarName: "HeroAvatar",
                durationSeconds: 2.0,
                currentTimeSeconds: 0.0,
                isPlaying: true,
                isLooping: true,
                playbackSpeed: 1.0,
                totalFrames: 60,
                sampleRate: 30.0,
                availableAnimations: new[] { candidate1, candidate2 },
                selectedAnimation: candidate2);

            Assert.True(vm.IsAnimationLoaded);
            Assert.True(vm.IsPlaying);
            Assert.False(vm.IsPaused);
            Assert.Equal("Pause", vm.PlayPauseButtonText);
            Assert.Equal("Hero_AttackCombo", vm.ActiveClipName);
            Assert.Equal("HeroAvatar", vm.ActiveAvatarName);
            Assert.Equal(2.0, vm.DurationSeconds);
            Assert.Equal(0.0, vm.CurrentTimeSeconds);
            Assert.Equal(60, vm.TotalFrames);
            Assert.Equal(0, vm.CurrentFrame);
            Assert.Equal("00:00.00 / 00:02.00", vm.FormattedTimeText);
            Assert.Equal("Frame: 0/60", vm.FormattedFrameText);
            Assert.True(vm.HasAvailableAnimations);
            Assert.Same(candidate2, vm.SelectedAnimation);

            // 3) Play -> Pause -> TogglePlayPause transitions
            Assert.False(vm.TogglePlayPause());
            Assert.False(vm.IsPlaying);
            Assert.True(vm.IsPaused);
            Assert.Equal("Play", vm.PlayPauseButtonText);

            Assert.True(vm.TogglePlayPause());
            Assert.True(vm.IsPlaying);
            Assert.False(vm.IsPaused);
            Assert.Equal("Pause", vm.PlayPauseButtonText);

            // 4) Seek clamping: negative, mid-range, above duration, NaN, Infinity
            Assert.Equal(0.0, vm.Seek(-10.0));
            Assert.Equal(0.0, vm.CurrentTimeSeconds);
            Assert.Equal(0, vm.CurrentFrame);

            Assert.Equal(1.5, vm.Seek(1.5));
            Assert.Equal(1.5, vm.CurrentTimeSeconds);
            Assert.Equal(45, vm.CurrentFrame);
            Assert.Equal("00:01.50 / 00:02.00", vm.FormattedTimeText);
            Assert.Equal("Frame: 45/60", vm.FormattedFrameText);

            Assert.Equal(2.0, vm.Seek(999.0));
            Assert.Equal(2.0, vm.CurrentTimeSeconds);
            Assert.Equal(60, vm.CurrentFrame);
            Assert.Equal("00:02.00 / 00:02.00", vm.FormattedTimeText);
            Assert.Equal("Frame: 60/60", vm.FormattedFrameText);

            Assert.Equal(0.0, vm.Seek(double.NaN));
            Assert.Equal(2.0, vm.Seek(double.PositiveInfinity));
            Assert.Equal(0.0, vm.Seek(double.NegativeInfinity));

            // 5) Playback speed clamping [0.1, 4.0]
            Assert.Equal(MeshPreviewViewModel.MinPlaybackSpeed, vm.SetPlaybackSpeed(0.001));
            Assert.Equal(MeshPreviewViewModel.MinPlaybackSpeed, vm.SetPlaybackSpeed(-5.0));
            Assert.Equal(MeshPreviewViewModel.MinPlaybackSpeed, vm.SetPlaybackSpeed(double.NegativeInfinity));
            Assert.Equal(MeshPreviewViewModel.MaxPlaybackSpeed, vm.SetPlaybackSpeed(25.0));
            Assert.Equal(MeshPreviewViewModel.MaxPlaybackSpeed, vm.SetPlaybackSpeed(double.PositiveInfinity));
            Assert.Equal(MeshPreviewViewModel.DefaultPlaybackSpeed, vm.SetPlaybackSpeed(double.NaN));
            Assert.Equal(2.0, vm.SetPlaybackSpeed(2.0));

            // 6) Looping vs non-looping playback advancement
            vm.Seek(1.25);
            // At 2.0x speed, +0.5s wall time = +1.0s clip time -> (1.25 + 1.0) % 2.0 = 0.25s
            var wrappedTime = vm.AdvanceTime(0.5);
            Assert.Equal(0.25, wrappedTime, precision: 5);
            Assert.True(vm.IsPlaying);

            // Disable loop -> advancing past duration clamps at 2.0s and stops playback
            vm.IsLooping = false;
            var clampedEnd = vm.AdvanceTime(2.0);
            Assert.Equal(2.0, clampedEnd, precision: 5);
            Assert.False(vm.IsPlaying);
            Assert.True(vm.IsPaused);

            // Calling Play() when parked at end of non-looping clip rewinds to 0.0s and plays
            Assert.True(vm.Play());
            Assert.Equal(0.0, vm.CurrentTimeSeconds);
            Assert.True(vm.IsPlaying);

            // 7) Reset() restores all 3D & animation state and deduplicates subsequent Reset()
            vm.Reset();
            Assert.False(vm.IsAnimationLoaded);
            Assert.False(vm.IsPlaying);
            Assert.True(vm.IsLooping);
            Assert.Equal(0.0, vm.CurrentTimeSeconds);
            Assert.Equal(0.0, vm.DurationSeconds);
            Assert.Equal(1.0, vm.PlaybackSpeed);
            Assert.Equal(0, vm.CurrentFrame);
            Assert.Equal(0, vm.TotalFrames);
            Assert.Equal(MeshPreviewViewModel.DefaultFormattedTimeText, vm.FormattedTimeText);
            Assert.Equal(MeshPreviewViewModel.DefaultFormattedFrameText, vm.FormattedFrameText);
            Assert.Equal(string.Empty, vm.ActiveClipName);
            Assert.Equal(string.Empty, vm.ActiveAvatarName);
            Assert.False(vm.HasAvailableAnimations);

            var changeCount = 0;
            vm.PropertyChanged += (_, _) => changeCount++;
            vm.Reset();
            Assert.Equal(0, changeCount);
        }

        private static AssetItem CreateSyntheticAssetItem(string name, ClassIDType type)
        {
            var item = (AssetItem)RuntimeHelpers.GetUninitializedObject(typeof(AssetItem));
            item.Name = name;
            item.Type = type;
            item.TypeString = type.ToString();
            item.Container = string.Empty;
            return item;
        }

        private static TextAssetDialogueCardViewModel CreateDialogueCard(
            string title,
            string offset,
            string body,
            string footer)
        {
            return new TextAssetDialogueCardViewModel
            {
                Title = title,
                OffsetText = offset,
                BodyText = body,
                FooterText = footer,
                CardBackground = Brushes.Black,
                CardBorderBrush = Brushes.Gray,
                TitleForeground = Brushes.White,
                MetadataForeground = Brushes.LightGray,
                BodyForeground = Brushes.White,
                FooterForeground = Brushes.Silver,
            };
        }
    }
}
