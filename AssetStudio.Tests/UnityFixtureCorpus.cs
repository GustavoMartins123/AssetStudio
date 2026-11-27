using System.IO;
using Xunit;

namespace AssetStudio.Tests;

internal static class UnityFixtureCorpus
{
    public static string Root { get; } = Path.Combine(SyntheticSerializedFile.LocateRepositoryRoot(), "AssetStudio.Tests", "Fixtures", "Unity");

    public static string FiraCodeFont { get; } = Path.Combine(SyntheticSerializedFile.LocateRepositoryRoot(), "tools", "unity-fixtures", "Fonts", "FiraCode-Regular.ttf");

    public static string File(string relativePath)
    {
        var path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(System.IO.File.Exists(path), $"Unity corpus file missing: {relativePath} (regenerate with tools/unity-fixtures/generate.sh).");
        return path;
    }
}
