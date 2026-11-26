#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace AssetStudio
{
    public static class AntiCheatDetector
    {
        private static readonly string[] NameTokens =
        {
            "easyanticheat", "battleye", "bedaisy", "beservice", "gameguard", "npggnt", "gamemon", "nprotect",
            "xigncode", "xhunter", "xcorona", "wellbia", "anticheatexpert", "sguard", "ace-base", "vanguard",
            "vgtray", "faceit", "equ8", "punkbuster", "pnkbstr", "pbsvc", "hyperion", "byfron", "denuvo", "neacsafe",
        };

        private static readonly string[] ProcessNames =
        {
            "EasyAntiCheat", "EasyAntiCheat_EOS", "BEService", "vgc", "vgtray", "SGuard64", "SGuardSvc64", "GameMon",
            "PnkBstrA", "PnkBstrB", "pbsvc", "FACEITAc", "FACEITClient", "xcorona", "neac",
        };

        public static bool TryDetect(string gameDirectory, out string reason)
        {
            reason = string.Empty;
            try
            {
                foreach (string entry in EnumerateEntries(gameDirectory))
                {
                    string lowered = Path.GetFileName(entry).ToLowerInvariant();
                    string? token = NameTokens.FirstOrDefault(item => lowered.Contains(item, StringComparison.Ordinal));
                    if (token != null)
                    {
                        reason = $"Anti-cheat component '{Path.GetFileName(entry)}' was found in the game folder. Capture aborted.";
                        return true;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            string prefix = Path.GetFullPath(gameDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (string name in ProcessNames)
            {
                foreach (Process process in Process.GetProcessesByName(name))
                {
                    using (process)
                    {
                        string? path = GameKeyCapture.TryGetMainPath(process);
                        if (path == null || path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        {
                            reason = $"Anti-cheat process '{name}' is running. Capture aborted.";
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        private static IEnumerable<string> EnumerateEntries(string gameDirectory)
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(gameDirectory))
            {
                yield return entry;
                if (Directory.Exists(entry) && Path.GetFileName(entry).EndsWith("_Data", StringComparison.OrdinalIgnoreCase))
                {
                    string plugins = Path.Combine(entry, "Plugins");
                    if (Directory.Exists(plugins))
                    {
                        foreach (string plugin in Directory.EnumerateFiles(plugins, "*", SearchOption.AllDirectories))
                        {
                            yield return plugin;
                        }
                    }
                }
            }
        }
    }
}
