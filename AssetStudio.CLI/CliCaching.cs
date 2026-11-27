using System;
using System.Collections.Generic;
using System.IO;

namespace AssetStudio.CLI
{
    internal static class CliCaching
    {
        public const string DisableOption = "--no-decompression-cache";
        public const string CacheDirectoryVariable = "ASSETSTUDIO_DECOMPRESSION_CACHE_DIR";

        public static string[] RemoveOption(string[] args, out bool enabled)
        {
            enabled = true;
            var kept = new List<string>(args.Length);
            foreach (string arg in args)
            {
                if (string.Equals(arg, DisableOption, StringComparison.Ordinal))
                {
                    enabled = false;
                }
                else
                {
                    kept.Add(arg);
                }
            }

            return kept.ToArray();
        }

        public static AssetsManager Attach(AssetsManager assetsManager, string targetPath, TextWriter stderr)
        {
            try
            {
                string? scope = Directory.Exists(targetPath)
                    ? targetPath
                    : Path.GetDirectoryName(Path.GetFullPath(targetPath));
                assetsManager.DecompressionCache = new DecompressedBundleCache(
                    Environment.GetEnvironmentVariable(CacheDirectoryVariable),
                    gameScopePath: scope);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                stderr.WriteLine($"Warning: decompression cache unavailable ({ex.GetType().Name}); continuing without it.");
            }

            return assetsManager;
        }
    }
}
