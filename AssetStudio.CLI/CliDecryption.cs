using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AssetStudio.CLI
{
    internal sealed record DecryptionOptions(bool LoadLocalProfiles, IReadOnlyList<string> ProfilePaths);

    internal static class CliDecryption
    {
        public const string LocalProfilesOption = "--no-local-profiles";
        public const string ProfilesOption = "--profiles";

        public static bool TryExtractOptions(string[] args, TextWriter stderr, out string[] remaining, out DecryptionOptions options)
        {
            var profilePaths = new List<string>();
            bool loadLocal = true;
            var kept = new List<string>(args.Length);
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (string.Equals(arg, LocalProfilesOption, StringComparison.Ordinal))
                {
                    loadLocal = false;
                }
                else if (string.Equals(arg, ProfilesOption, StringComparison.Ordinal))
                {
                    if (i + 1 >= args.Length)
                    {
                        stderr.WriteLine($"Error: missing value for {ProfilesOption}.");
                        remaining = Array.Empty<string>();
                        options = new DecryptionOptions(false, Array.Empty<string>());
                        return false;
                    }

                    profilePaths.Add(args[++i]);
                }
                else
                {
                    kept.Add(arg);
                }
            }

            foreach (string path in profilePaths)
            {
                if (!File.Exists(path) && !Directory.Exists(path))
                {
                    stderr.WriteLine($"Error: decryption profiles path does not exist: {path}");
                    remaining = Array.Empty<string>();
                    options = new DecryptionOptions(false, Array.Empty<string>());
                    return false;
                }
            }

            remaining = kept.ToArray();
            options = new DecryptionOptions(loadLocal, profilePaths);
            return true;
        }

        public static void Load(DecryptionOptions options, string[] args)
        {
            if (options.LoadLocalProfiles)
            {
                DecryptionHelper.LoadLocalProfilesAndPlugins(FindProjectRoot(args));
            }

            foreach (string path in options.ProfilePaths)
            {
                if (Directory.Exists(path))
                {
                    DecryptionHelper.LoadProfilesFromDirectory(path);
                }
                else
                {
                    DecryptionHelper.LoadProfilesFromFile(path);
                }
            }
        }

        private static string? FindProjectRoot(string[] args)
        {
            bool expectsValue = false;
            foreach (string arg in args)
            {
                if (arg.StartsWith('-'))
                {
                    expectsValue = arg != "--dry-run";
                    continue;
                }

                if (expectsValue)
                {
                    expectsValue = false;
                    continue;
                }

                string? directory = Directory.Exists(arg) ? arg : File.Exists(arg) ? Path.GetDirectoryName(Path.GetFullPath(arg)) : null;
                if (directory != null)
                {
                    return directory;
                }
            }

            return null;
        }
    }
}
