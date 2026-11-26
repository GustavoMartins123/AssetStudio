using System;
using System.IO;
using System.Linq;
using AssetStudio.CLI;
using Xunit;

namespace AssetStudio.Tests
{
    public class CliExportCoverageTests
    {
        [Fact]
        public void Export_TypeAll_ExportsSyntheticTextAssets()
        {
            using var temp = new TempDir();
            var input = Path.Combine(temp.Path, "level0");
            File.WriteAllBytes(input, SyntheticSerializedFile.BuildTextAssets(new (long, string, string)[] { (1, "readme", "hello"), (2, "config", "a=1") }, out _));
            var output = Path.Combine(temp.Path, "out");

            var (exit, stdout, stderr) = Run("export", input, "--output", output, "--type", "all");

            Assert.True(exit == CliRunner.ExitSuccess, stderr);
            Assert.Contains("Export complete: 2 exported, 0 skipped.", stdout, StringComparison.Ordinal);
            Assert.Equal("hello", File.ReadAllText(Path.Combine(output, "TextAsset", "readme.txt")));
            Assert.Equal("a=1", File.ReadAllText(Path.Combine(output, "TextAsset", "config.txt")));
        }

        [Fact]
        public void Export_UnknownType_ListsEverySupportedType()
        {
            var (exit, _, stderr) = Run("export", Path.GetTempPath(), "--output", Path.GetTempPath(), "--type", "Banana");

            Assert.Equal(CliRunner.ExitInvalidArguments, exit);
            foreach (var type in new[] { "Sprite", "AudioClip", "Font", "Shader", "MonoBehaviour", "VideoClip", "AnimationClip", "Animator", "all" })
            {
                Assert.Contains(type, stderr, StringComparison.Ordinal);
            }
        }

        [Theory]
        [InlineData("Sprite")]
        [InlineData("audioclip")]
        [InlineData("ANIMATOR")]
        public void Export_AdditionalTypes_AreAcceptedCaseInsensitively(string type)
        {
            using var temp = new TempDir();
            var input = Path.Combine(temp.Path, "level0");
            File.WriteAllBytes(input, SyntheticSerializedFile.BuildTextAssets(new (long, string, string)[] { (1, "readme", "hello") }, out _));

            var (exit, stdout, stderr) = Run("export", input, "--output", Path.Combine(temp.Path, "out"), "--type", type, "--dry-run");

            Assert.True(exit == CliRunner.ExitSuccess, stderr);
            Assert.Contains("Dry-run complete: 0 planned", stdout, StringComparison.Ordinal);
        }

        [Fact]
        public void Export_InvalidModelFormat_IsRejected()
        {
            var (exit, _, stderr) = Run("export", Path.GetTempPath(), "--output", Path.GetTempPath(), "--model-format", "blend");

            Assert.Equal(CliRunner.ExitInvalidArguments, exit);
            Assert.Contains("Supported: glb, gltf, fbx", stderr, StringComparison.Ordinal);
        }

        [Fact]
        public void Help_DescribesExportTypesModelFormatAndAssemblies()
        {
            var (exit, stdout, _) = Run("--help");

            Assert.Equal(CliRunner.ExitSuccess, exit);
            Assert.Contains("--model-format glb|gltf|fbx", stdout, StringComparison.Ordinal);
            Assert.Contains("--assemblies <Managed folder>", stdout, StringComparison.Ordinal);
            Assert.Contains("AnimationClip, Animator, all", stdout, StringComparison.Ordinal);
        }

        // Bundle built by the Unity 6000.5 editor from known sources (scratch/unity-fixtures, not in git).
        [SkippableFact]
        public void Export_RealUnity6000_5Bundle_AllTypes_MatchTheSourceFiles()
        {
            var samples = Path.Combine(SyntheticSerializedFile.LocateRepositoryRoot(), "scratch", "unity-fixtures", "samples", "6000.5", "fixture");
            var bundle = Path.Combine(samples, "StandaloneWindows64", "lz4", "fixture");
            Skip.IfNot(File.Exists(bundle), "Unity fixture bundle not generated on this machine (scratch/unity-fixtures/samples).");
            var truth = Path.Combine(samples, "Truth");

            using var temp = new TempDir();
            var (exit, stdout, stderr) = Run("export", bundle, "--output", temp.Path, "--type", "all");

            Assert.True(exit == CliRunner.ExitSuccess, stderr);
            Assert.DoesNotContain("failed", stdout, StringComparison.Ordinal);
            Assert.Equal(File.ReadAllBytes(Path.Combine(truth, "sine_mono.wav")), File.ReadAllBytes(Path.Combine(temp.Path, "AudioClip", "sine_pcm.wav")));
            Assert.Equal(File.ReadAllBytes(Path.Combine(truth, "sine_stereo.wav")), File.ReadAllBytes(Path.Combine(temp.Path, "AudioClip", "stereo_pcm.wav")));
            Assert.Equal(File.ReadAllBytes(Path.Combine(truth, "notes.txt")), File.ReadAllBytes(Path.Combine(temp.Path, "TextAsset", "notes.txt")));
            Assert.True(File.Exists(Path.Combine(temp.Path, "AudioClip", "sine_vorbis.ogg")));
            Assert.True(new FileInfo(Path.Combine(temp.Path, "Font", "fixture_font.ttf")).Length > 0);
            Assert.True(File.Exists(Path.Combine(temp.Path, "Shader", "Fixture_Unlit.shader")));
            Assert.Equal("glTF"u8.ToArray(), File.ReadAllBytes(Path.Combine(temp.Path, "Animator", "FixtureRoot.glb")).Take(4).ToArray());
            Assert.Contains("path: child", File.ReadAllText(Path.Combine(temp.Path, "AnimationClip", "fixture_move.anim")), StringComparison.Ordinal);
            Assert.Contains("\"title\": \"fixture data\"", File.ReadAllText(Path.Combine(temp.Path, "MonoBehaviour", "fixture_data.json")), StringComparison.Ordinal);
        }

        // Player build without TypeTrees: MonoBehaviour fields need the game's Managed assemblies.
        [SkippableFact]
        public void Export_RealPlayerBuild_MonoBehaviourNeedsAssemblies()
        {
            var data = Path.Combine(SyntheticSerializedFile.LocateRepositoryRoot(), "scratch", "unity-fixtures", "samples", "6000.5", "player-mono", "FixtureGame_Data");
            Skip.IfNot(Directory.Exists(data), "Unity player build not generated on this machine (scratch/unity-fixtures/samples).");

            using var temp = new TempDir();
            var (_, withoutStdout, withoutStderr) = Run("export", data, "--output", Path.Combine(temp.Path, "none"), "--type", "MonoBehaviour");
            Assert.Contains("pass --assemblies <Managed folder>", withoutStderr, StringComparison.Ordinal);
            Assert.Contains("0 exported", withoutStdout, StringComparison.Ordinal);

            var output = Path.Combine(temp.Path, "with");
            var (exit, _, stderr) = Run("export", data, "--output", output, "--type", "MonoBehaviour", "--assemblies", Path.Combine(data, "Managed"));
            Assert.True(exit == CliRunner.ExitSuccess, stderr);
            var json = File.ReadAllText(Path.Combine(output, "MonoBehaviour", "FixtureRoot.json"));
            Assert.Contains("\"text\": \"hello fixture\"", json, StringComparison.Ordinal);
            Assert.Contains("\"class\": \"FixtureCircle\"", json, StringComparison.Ordinal);
            Assert.Contains("\"radius\": 4.5", json, StringComparison.Ordinal);
        }

        private static (int Exit, string Stdout, string Stderr) Run(params string[] args)
        {
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            int exit = CliRunner.Run(args, stdout, stderr);
            return (exit, stdout.ToString(), stderr.ToString());
        }

        private sealed class TempDir : IDisposable
        {
            public TempDir()
            {
                Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AssetStudio_CliExport_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path);
            }

            public string Path { get; }

            public void Dispose()
            {
                try
                {
                    Directory.Delete(Path, recursive: true);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }
}
