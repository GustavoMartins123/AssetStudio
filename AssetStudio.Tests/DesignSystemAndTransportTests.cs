using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AssetStudio.Avalonia.ViewModels;
using Xunit;

namespace AssetStudio.Tests
{
    public class DesignSystemAndTransportTests
    {
        private static readonly Regex AxamlClickAttributeRegex = new(
            @"\bClick\s*=",
            RegexOptions.CultureInvariant);

        [Fact]
        public void DesignSystemAxaml_DefinesLightAndDarkThemeDictionaries_AndIsIncludedInAppAxaml()
        {
            var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
            var designSystemPath = Path.Combine(repoRoot, "AssetStudio.Avalonia", "Themes", "DesignSystem.axaml");
            var appAxamlPath = Path.Combine(repoRoot, "AssetStudio.Avalonia", "App.axaml");

            Assert.True(File.Exists(designSystemPath), $"Expected DesignSystem.axaml at {designSystemPath}");
            Assert.True(File.Exists(appAxamlPath), $"Expected App.axaml at {appAxamlPath}");

            var designSystemText = File.ReadAllText(designSystemPath);
            Assert.Contains("ResourceDictionary.ThemeDictionaries", designSystemText, StringComparison.Ordinal);
            Assert.Contains("x:Key=\"Dark\"", designSystemText, StringComparison.Ordinal);
            Assert.Contains("x:Key=\"Light\"", designSystemText, StringComparison.Ordinal);
            Assert.Contains("Border.media-card", designSystemText, StringComparison.Ordinal);
            Assert.Contains("Border.overlay-panel", designSystemText, StringComparison.Ordinal);
            Assert.Contains("Button.transport-btn", designSystemText, StringComparison.Ordinal);
            Assert.Contains("Button.pptr-link", designSystemText, StringComparison.Ordinal);
            Assert.DoesNotMatch(AxamlClickAttributeRegex, designSystemText);

            var appAxamlText = File.ReadAllText(appAxamlPath);
            Assert.Contains("avares://AssetStudio.Avalonia/Themes/DesignSystem.axaml", appAxamlText, StringComparison.Ordinal);
        }

        [Fact]
        public async Task AudioAndVideoTransports_ShareUnifiedPlayPauseStopLifecycle()
        {
            using var vm = new MainWindowViewModel();

            // Audio uses unified PlayPauseButtonText + AudioPlayPauseCommand matching Video UX
            vm.MediaPlayback.SelectAudio("BgmTrack", "48000 Hz");
            Assert.Equal("Play", vm.MediaPlayback.PlayPauseButtonText);

            await vm.AudioPlayPauseCommand.ExecuteAsync();
            Assert.True(vm.MediaPlayback.IsPlaying);
            Assert.Equal("Pause", vm.MediaPlayback.PlayPauseButtonText);

            await vm.AudioPlayPauseCommand.ExecuteAsync();
            Assert.True(vm.MediaPlayback.IsPaused);
            Assert.Equal("Play", vm.MediaPlayback.PlayPauseButtonText);

            await vm.AudioPlayPauseCommand.ExecuteAsync();
            Assert.True(vm.MediaPlayback.IsPlaying);
            Assert.Equal("Pause", vm.MediaPlayback.PlayPauseButtonText);

            vm.AudioStopCommand.Execute();
            Assert.Equal(MediaPlaybackState.Stopped, vm.MediaPlayback.PlaybackState);
            Assert.Equal("Play", vm.MediaPlayback.PlayPauseButtonText);

            // Video mirrors the exact same Play/Pause -> Stop transition
            vm.MediaPlayback.SelectVideo("Cutscene", "1920x1080");
            Assert.Equal("Play", vm.MediaPlayback.PlayPauseButtonText);
            await vm.VideoPlayPauseCommand.ExecuteAsync();
            Assert.True(vm.MediaPlayback.IsPlaying);
            Assert.Equal("Pause", vm.MediaPlayback.PlayPauseButtonText);
            await vm.VideoStopCommand.ExecuteAsync();
            Assert.Equal(MediaPlaybackState.Stopped, vm.MediaPlayback.PlaybackState);
            Assert.Equal("Play", vm.MediaPlayback.PlayPauseButtonText);
        }

        [Fact]
        public void AnimationTransport_StopCommand_PausesAndRewindsToFrameZero()
        {
            using var vm = new MainWindowViewModel();

            vm.MeshPreview.SetAnimationState(
                isLoaded: true,
                clipName: "RunCycle",
                durationSeconds: 2.0,
                isPlaying: true,
                totalFrames: 60,
                sampleRate: 30.0);

            vm.MeshPreview.Seek(1.2);
            Assert.True(vm.MeshPreview.IsPlaying);
            Assert.True(vm.MeshPreview.CurrentFrame > 0);
            Assert.True(vm.MeshPreview.CurrentTimeSeconds > 0.0);

            vm.StopAnimationCommand.Execute();
            Assert.False(vm.MeshPreview.IsPlaying);
            Assert.Equal("Play", vm.MeshPreview.PlayPauseButtonText);
            Assert.Equal(0, vm.MeshPreview.CurrentFrame);
            Assert.Equal(0.0, vm.MeshPreview.CurrentTimeSeconds, 4);

            // Custom handler override is respected cleanly
            var stopInvocations = 0;
            vm.CommandHandlers.StopAnimation = () => stopInvocations++;
            vm.StopAnimationCommand.Execute();
            Assert.Equal(1, stopInvocations);
        }
    }
}
