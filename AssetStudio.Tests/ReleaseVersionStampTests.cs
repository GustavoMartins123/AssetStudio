using System;
using System.Reflection;
using AssetStudio.Avalonia;
using Xunit;

namespace AssetStudio.Tests
{
    public class ReleaseVersionStampTests
    {
        [Theory]
        [InlineData("0.18.0-rc2+abc123", "0.18.0-rc2", "v0.18.0-rc2")]
        [InlineData("v0.18.0", "0.18.0", "v0.18.0")]
        [InlineData("V0.18.0-rc2+deadbeef", "0.18.0-rc2", "v0.18.0-rc2")]
        [InlineData("0.17.1", "0.17.1", "v0.17.1")]
        [InlineData("1.2.3.4", "1.2.3.4", "v1.2.3.4")]
        [InlineData("  v1.2.3-beta.1+sha.99  ", "1.2.3-beta.1", "v1.2.3-beta.1")]
        public void ResolveVersion_FormatsInformationalVersionsCleanly(
            string informationalVersion,
            string expectedVersion,
            string expectedPrefixed)
        {
            var resolved = AppVersionInfo.ResolveVersion(informationalVersion, new Version(0, 17, 1, 0));
            var prefixed = AppVersionInfo.FormatPrefixedVersion(informationalVersion, new Version(0, 17, 1, 0));

            Assert.Equal(expectedVersion, resolved);
            Assert.Equal(expectedPrefixed, prefixed);
        }

        [Theory]
        [InlineData(null, 0, 18, 0, 0, "0.18.0")]
        [InlineData("", 0, 17, 1, 0, "0.17.1")]
        [InlineData("   ", 1, 2, 3, 4, "1.2.3.4")]
        [InlineData("+abc123", 2, 0, 1, 0, "2.0.1")]
        [InlineData("v", 3, 4, 5, 6, "3.4.5.6")]
        public void ResolveVersion_FallsBackToAssemblyVersionWhenInformationalMissingOrEmpty(
            string? informationalVersion,
            int major,
            int minor,
            int build,
            int revision,
            string expectedVersion)
        {
            var assemblyVersion = new Version(major, minor, build, revision);

            var resolved = AppVersionInfo.ResolveVersion(informationalVersion, assemblyVersion);

            Assert.Equal(expectedVersion, resolved);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("+commitonly")]
        [InlineData("vv")]
        public void ResolveVersion_FallsBackToDefaultWhenBothVersionsMissing(string? informationalVersion)
        {
            var resolved = AppVersionInfo.ResolveVersion(informationalVersion, null);
            var prefixed = AppVersionInfo.FormatPrefixedVersion(informationalVersion, null);

            Assert.Equal(AppVersionInfo.FallbackVersion, resolved);
            Assert.Equal($"v{AppVersionInfo.FallbackVersion}", prefixed);
        }

        [Fact]
        public void GetVersion_ReadsExecutingUiAssemblyWithoutCommitHashSuffix()
        {
            var uiAssembly = typeof(AppVersionInfo).Assembly;
            var version = AppVersionInfo.GetVersion(uiAssembly);

            Assert.False(string.IsNullOrWhiteSpace(version));
            Assert.DoesNotContain("+", version, StringComparison.Ordinal);
            Assert.Equal(version, AppVersionInfo.DisplayVersion);
            Assert.Equal($"v{version}", AppVersionInfo.PrefixedDisplayVersion);

            var infoAttr = uiAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            Assert.NotNull(infoAttr);
            Assert.Equal(AppVersionInfo.FormatInformationalVersion(infoAttr!.InformationalVersion), version);
        }

        [Fact]
        public void GetVersion_HandlesNullAssemblyGracefully()
        {
            Assert.Equal(AppVersionInfo.FallbackVersion, AppVersionInfo.GetVersion(null));
        }
    }
}
