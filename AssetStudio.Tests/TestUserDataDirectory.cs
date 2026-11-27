using System;
using System.IO;
using System.Runtime.CompilerServices;
using AssetStudio;

namespace AssetStudio.Tests
{
    internal static class TestUserDataDirectory
    {
        [ModuleInitializer]
        internal static void Initialize()
        {
            string root = Path.Combine(Path.GetTempPath(), "AssetStudioTests-" + Environment.ProcessId.ToString("x"));
            string directory = Path.Combine(root, "userdata");
            Directory.CreateDirectory(directory);
            Environment.SetEnvironmentVariable(DecryptionHelper.UserDataDirectoryVariable, directory);
            Environment.SetEnvironmentVariable("ASSETSTUDIO_DECOMPRESSION_CACHE_DIR", Path.Combine(root, "decompressedcache"));
        }
    }
}
