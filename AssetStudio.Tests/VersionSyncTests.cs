using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AssetStudio;
using AssetStudio.Avalonia;
using AssetStudio.Avalonia.ViewModels;
using Texture2DDecoder;
using Xunit;

namespace AssetStudio.Tests
{
    public class VersionSyncTests
    {
        private static readonly Regex ValidReleaseTagRegex = new(
            @"^[vV][0-9]+(?:\.[0-9]+){1,3}(?:-[0-9A-Za-z.-]+)?$",
            RegexOptions.Compiled);

        private static readonly Regex NumericPrefixRegex = new(
            @"^([0-9]+(?:\.[0-9]+){0,3})",
            RegexOptions.Compiled);

        [Fact]
        public void CompiledUiAssembly_StampsSynchronizedReleaseVersion_NeverLegacy017()
        {
            var uiAssembly = typeof(AppVersionInfo).Assembly;
            var expectedVersion = ResolveExpectedRepoReleaseVersion();
            var expectedNumeric = ParseFourPartVersion(expectedVersion);

            var infoAttr = uiAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            var fileAttr = uiAssembly.GetCustomAttribute<AssemblyFileVersionAttribute>();
            var assemblyVersion = uiAssembly.GetName().Version;

            Assert.NotNull(infoAttr);
            Assert.NotNull(fileAttr);
            Assert.NotNull(assemblyVersion);

            var actualInfo = AppVersionInfo.FormatInformationalVersion(infoAttr!.InformationalVersion);
            Assert.Equal(expectedVersion, actualInfo);
            Assert.NotEqual("0.17.1", actualInfo);
            Assert.False(actualInfo!.StartsWith("0.17.", StringComparison.Ordinal));
            Assert.DoesNotContain("+", infoAttr.InformationalVersion, StringComparison.Ordinal);

            Assert.Equal(expectedNumeric, assemblyVersion);
            Assert.Equal(expectedNumeric.ToString(), fileAttr!.Version);

            Assert.Equal(expectedVersion, AppVersionInfo.Version);
            Assert.Equal(expectedVersion, AppVersionInfo.DisplayVersion);
            Assert.Equal($"v{expectedVersion}", AppVersionInfo.PrefixedDisplayVersion);

            var emptyTitle = ProjectSessionViewModel.FormatWindowTitle();
            var projectTitle = ProjectSessionViewModel.FormatWindowTitle("SampleProject");
            var loadedTitle = ProjectSessionViewModel.FormatLoadedWindowTitle("SampleGame", "2022.3.10f1", "StandaloneWindows64");

            Assert.Equal($"AssetStudio v{expectedVersion}", emptyTitle);
            Assert.Equal($"AssetStudio v{expectedVersion} - SampleProject", projectTitle);
            Assert.Equal($"AssetStudio v{expectedVersion} - SampleGame - 2022.3.10f1 - StandaloneWindows64", loadedTitle);
            Assert.DoesNotContain("0.17.1", emptyTitle, StringComparison.Ordinal);
        }

        [Fact]
        public void CompiledUiAssembly_PeFileVersionMetadata_MatchesSynchronizedReleaseVersion()
        {
            var uiAssembly = typeof(AppVersionInfo).Assembly;
            Assert.False(string.IsNullOrWhiteSpace(uiAssembly.Location));
            Assert.True(File.Exists(uiAssembly.Location));

            var expectedVersion = ResolveExpectedRepoReleaseVersion();
            var expectedNumeric = ParseFourPartVersion(expectedVersion);

            var fileVersionInfo = FileVersionInfo.GetVersionInfo(uiAssembly.Location);
            Assert.Equal(expectedNumeric.ToString(), fileVersionInfo.FileVersion);
            Assert.Equal(expectedVersion, AppVersionInfo.FormatInformationalVersion(fileVersionInfo.ProductVersion));
        }

        [Fact]
        public void CompiledSolutionAssemblies_ShareSynchronizedReleaseVersion()
        {
            var expectedVersion = ResolveExpectedRepoReleaseVersion();
            var expectedNumeric = ParseFourPartVersion(expectedVersion);

            var assemblies = new[]
            {
                typeof(AppVersionInfo).Assembly,
                typeof(AssetsManager).Assembly,
                typeof(AssetExportHelper).Assembly,
                typeof(TextureDecoder).Assembly
            };

            foreach (var assembly in assemblies)
            {
                var infoVersion = assembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                    ?.InformationalVersion;
                var fileVersion = assembly
                    .GetCustomAttribute<AssemblyFileVersionAttribute>()
                    ?.Version;
                var asmVersion = assembly.GetName().Version;

                Assert.Equal(expectedVersion, AppVersionInfo.FormatInformationalVersion(infoVersion));
                Assert.Equal(expectedNumeric, asmVersion);
                Assert.Equal(expectedNumeric.ToString(), fileVersion);
            }
        }

        [Fact]
        public void DirectoryBuildProps_PinsLockfileVersion_AndSyncsDefaultReleaseVersion()
        {
            var repoRoot = FindRepoRoot();
            var propsPath = Path.Combine(repoRoot, "Directory.Build.props");
            var doc = XDocument.Load(propsPath);

            var lockfileVersion = doc.Descendants("Version").Select(e => e.Value.Trim()).FirstOrDefault(v => !string.IsNullOrEmpty(v));
            var defaultReleaseVersion = doc.Descendants("DefaultReleaseVersion").Select(e => e.Value.Trim()).FirstOrDefault(v => !string.IsNullOrEmpty(v));

            Assert.Equal("0.17.1", lockfileVersion);
            Assert.False(string.IsNullOrWhiteSpace(defaultReleaseVersion));
            Assert.NotEqual("0.17.1", defaultReleaseVersion);
            Assert.Equal(AppVersionInfo.FallbackVersion, defaultReleaseVersion);
        }

        [Fact]
        public void PublishAndReleaseScripts_ResolveVersionAutomaticallyWithoutLegacy017Fallback()
        {
            var repoRoot = FindRepoRoot();
            var scriptFiles = new[]
            {
                Path.Combine(repoRoot, "scripts", "publish-windows.ps1"),
                Path.Combine(repoRoot, "scripts", "publish-linux.sh"),
                Path.Combine(repoRoot, "scripts", "package-linux.sh"),
                Path.Combine(repoRoot, ".github", "workflows", "release.yml")
            };

            foreach (var file in scriptFiles)
            {
                Assert.True(File.Exists(file), $"Expected script/workflow file at '{file}'.");
                var content = File.ReadAllText(file);

                Assert.Contains("DefaultReleaseVersion", content, StringComparison.Ordinal);
                Assert.Contains("versionsort.suffix=-", content, StringComparison.Ordinal);
                Assert.DoesNotContain("0.17.1", content, StringComparison.Ordinal);
            }
        }

        private static string ResolveExpectedRepoReleaseVersion()
        {
            var envOverride = NormalizeVersion(Environment.GetEnvironmentVariable("RELEASE_VERSION"));
            if (!string.IsNullOrEmpty(envOverride))
            {
                return envOverride;
            }

            var repoRoot = FindRepoRoot();
            var defaultVersion = ReadDefaultReleaseVersion(repoRoot);

            var exactTag = QueryGitReleaseTag(repoRoot, "--points-at", "HEAD");
            if (!string.IsNullOrEmpty(exactTag))
            {
                return exactTag;
            }

            var gitTag = QueryGitReleaseTag(repoRoot, "--merged", "HEAD")
                ?? QueryGitReleaseTag(repoRoot);

            return SelectPreferredReleaseVersion(gitTag, defaultVersion);
        }

        private static string SelectPreferredReleaseVersion(string? gitVersion, string defaultVersion)
        {
            if (string.IsNullOrWhiteSpace(gitVersion))
            {
                return defaultVersion;
            }

            if (string.IsNullOrWhiteSpace(defaultVersion))
            {
                return gitVersion;
            }

            var gitNumeric = ParseFourPartVersion(gitVersion);
            var defaultNumeric = ParseFourPartVersion(defaultVersion);

            if (gitNumeric > defaultNumeric)
            {
                return gitVersion;
            }

            if (gitNumeric < defaultNumeric)
            {
                return defaultVersion;
            }

            var gitIsPre = gitVersion.Contains('-', StringComparison.Ordinal);
            var defaultIsPre = defaultVersion.Contains('-', StringComparison.Ordinal);
            return (!gitIsPre || defaultIsPre) ? gitVersion : defaultVersion;
        }

        private static string ReadDefaultReleaseVersion(string repoRoot)
        {
            var propsPath = Path.Combine(repoRoot, "Directory.Build.props");
            var doc = XDocument.Load(propsPath);
            var raw = doc.Descendants("DefaultReleaseVersion")
                .Select(e => e.Value.Trim())
                .FirstOrDefault(v => !string.IsNullOrEmpty(v));
            var normalized = NormalizeVersion(raw);
            Assert.False(string.IsNullOrEmpty(normalized), "Directory.Build.props must define <DefaultReleaseVersion>.");
            return normalized;
        }

        private static string? QueryGitReleaseTag(string repoRoot, params string[] extraArgs)
        {
            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "git",
                        WorkingDirectory = repoRoot,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                process.StartInfo.ArgumentList.Add("-c");
                process.StartInfo.ArgumentList.Add("versionsort.suffix=-");
                process.StartInfo.ArgumentList.Add("tag");
                foreach (var arg in extraArgs)
                {
                    process.StartInfo.ArgumentList.Add(arg);
                }
                process.StartInfo.ArgumentList.Add("--list");
                process.StartInfo.ArgumentList.Add("v[0-9]*");
                process.StartInfo.ArgumentList.Add("--sort=-v:refname");

                if (!process.Start())
                {
                    return null;
                }

                var stdout = process.StandardOutput.ReadToEnd();
                _ = process.StandardError.ReadToEnd();
                process.WaitForExit(5000);

                if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
                {
                    return null;
                }

                foreach (var line in stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var trimmed = line.Trim();
                    if (ValidReleaseTagRegex.IsMatch(trimmed))
                    {
                        return NormalizeVersion(trimmed);
                    }
                }
            }
            catch
            {
                return null;
            }

            return null;
        }

        private static string NormalizeVersion(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            return Regex.Replace(value.Trim(), @"^[vV]+(?=[0-9])", string.Empty);
        }

        private static Version ParseFourPartVersion(string versionText)
        {
            var match = NumericPrefixRegex.Match(versionText);
            Assert.True(match.Success, $"Version '{versionText}' does not start with numeric components.");

            var parts = match.Groups[1].Value.Split('.');
            int major = parts.Length > 0 ? int.Parse(parts[0]) : 0;
            int minor = parts.Length > 1 ? int.Parse(parts[1]) : 0;
            int build = parts.Length > 2 ? int.Parse(parts[2]) : 0;
            int revision = parts.Length > 3 ? int.Parse(parts[3]) : 0;
            return new Version(major, minor, build, revision);
        }

        private static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
                {
                    return dir.FullName;
                }

                dir = dir.Parent;
            }

            throw new InvalidOperationException("Could not locate repository root containing Directory.Build.props.");
        }
    }
}
