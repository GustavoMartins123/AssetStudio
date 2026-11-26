#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AssetStudio
{
    public sealed class GameCaptureTarget
    {
        private const int MaxSampleFiles = 16;

        internal GameCaptureTarget(string gameDirectory, string executablePath, string sourceDirectory, IReadOnlyList<string> sampleFiles)
        {
            GameDirectory = gameDirectory;
            ExecutablePath = executablePath;
            SourceDirectory = sourceDirectory;
            SampleFiles = sampleFiles;
        }

        public string GameDirectory { get; }
        public string ExecutablePath { get; }
        public string SourceDirectory { get; }
        public IReadOnlyList<string> SampleFiles { get; }
        public string GameName => Path.GetFileNameWithoutExtension(ExecutablePath);

        public static GameCaptureTarget? TryResolve(string encryptedFilePath)
        {
            if (string.IsNullOrWhiteSpace(encryptedFilePath) || !File.Exists(encryptedFilePath))
            {
                return null;
            }

            try
            {
                string fullPath = Path.GetFullPath(encryptedFilePath);
                string sourceDirectory = Path.GetDirectoryName(fullPath) ?? string.Empty;
                if (sourceDirectory.Length == 0)
                {
                    return null;
                }

                string? dataDirectory = null;
                string? current = sourceDirectory;
                for (int depth = 0; depth < 8 && current != null; depth++)
                {
                    if (Path.GetFileName(current).EndsWith("_Data", StringComparison.OrdinalIgnoreCase))
                    {
                        dataDirectory = current;
                        break;
                    }

                    current = Path.GetDirectoryName(current);
                }

                string? gameDirectory = dataDirectory == null ? null : Path.GetDirectoryName(dataDirectory);
                if (dataDirectory == null || gameDirectory == null)
                {
                    return null;
                }

                string dataStem = Path.GetFileName(dataDirectory)[..^5];
                string[] executables = Directory.GetFiles(gameDirectory, "*.exe", SearchOption.TopDirectoryOnly);
                string? executable = executables.FirstOrDefault(path => string.Equals(Path.GetFileNameWithoutExtension(path), dataStem, StringComparison.OrdinalIgnoreCase))
                    ?? executables.Where(path => !IsAuxiliaryExecutable(path)).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                if (executable == null)
                {
                    return null;
                }

                string extension = Path.GetExtension(fullPath);
                var samples = Directory.GetFiles(sourceDirectory)
                    .Where(path => extension.Length == 0 || string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase))
                    .Select(path => new FileInfo(path))
                    .Where(info => info.Length >= 64 && info.Length % 16 == 0)
                    .OrderByDescending(info => info.Length)
                    .Take(MaxSampleFiles)
                    .Select(info => info.FullName)
                    .ToArray();
                return samples.Length == 0 ? null : new GameCaptureTarget(gameDirectory, executable, sourceDirectory, samples);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return null;
            }
        }

        private static bool IsAuxiliaryExecutable(string path)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            return name.StartsWith("UnityCrashHandler", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("UnityShaderCompiler", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("UnityPackageManager", StringComparison.OrdinalIgnoreCase);
        }
    }

    public enum GameCaptureStatus
    {
        Succeeded,
        AntiCheatDetected,
        UnsupportedPlatform,
        NoSuitableFiles,
        AlreadyRunning,
        LaunchFailed,
        ExitedEarly,
        TimedOut,
        Cancelled,
    }

    public sealed class GameCaptureResult
    {
        internal GameCaptureResult(GameCaptureStatus status, string message, DecryptionProfile? profile = null, int validatedFiles = 0, int checkedFiles = 0, int verifiedMaskBytes = 0)
        {
            Status = status;
            Message = message;
            Profile = profile;
            ValidatedFiles = validatedFiles;
            CheckedFiles = checkedFiles;
            VerifiedMaskBytes = verifiedMaskBytes;
        }

        public GameCaptureStatus Status { get; }
        public string Message { get; }
        public DecryptionProfile? Profile { get; }
        public int ValidatedFiles { get; }
        public int CheckedFiles { get; }
        public int VerifiedMaskBytes { get; }
    }

    public readonly record struct GameCaptureProgress(string Message, double Percent);

    public static class GameKeyCapture
    {
        private static readonly TimeSpan EarlyExitGrace = TimeSpan.FromSeconds(45);
        private static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(20);

        public static TimeSpan DefaultTimeout { get; } = TimeSpan.FromSeconds(150);
        public static bool IsSupported => OperatingSystem.IsWindows();

        public static bool IsRunning(GameCaptureTarget target)
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            string prefix = Path.GetFullPath(target.GameDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (Process process in Process.GetProcesses())
            {
                using (process)
                {
                    string? path = TryGetMainPath(process);
                    if (path != null && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public static DecryptionProfile BuildProfile(GameCaptureTarget target, CapturedCipher cipher)
        {
            var profile = new DecryptionProfile
            {
                Id = "captured-aes-cbc-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"),
                Algorithm = "aes-cbc",
                KeyHex = Convert.ToHexString(cipher.Key),
                Padding = "pkcs7",
                ValidateUnityHeader = true,
            };

            string extension = Path.GetExtension(target.SampleFiles[0]);
            profile.FileNamePattern = extension.Length == 0 ? "*" : "*" + extension;
            string pathPattern = BuildPathPattern(target);
            if (pathPattern.Length > 0)
            {
                profile.PathPattern = pathPattern;
            }

            if (cipher.IvFromStreamPrefix)
            {
                profile.IvFromStreamPrefix = true;
            }
            else
            {
                profile.IvHex = Convert.ToHexString(cipher.FixedIv);
            }

            if (cipher.Mask.Length > 0)
            {
                profile.HeaderMask = new HeaderMaskProfile
                {
                    Type = "raw",
                    Length = cipher.Mask.Length,
                    DataBase64 = Convert.ToBase64String(cipher.Mask),
                };
            }

            return profile;
        }

        public static async Task<GameCaptureResult> RunAsync(
            GameCaptureTarget target,
            IProgress<GameCaptureProgress>? progress = null,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(target);
            if (!OperatingSystem.IsWindows())
            {
                return new GameCaptureResult(GameCaptureStatus.UnsupportedPlatform, "Capturing keys from a running game is only available on Windows.");
            }

            if (AntiCheatDetector.TryDetect(target.GameDirectory, out string antiCheat))
            {
                return new GameCaptureResult(GameCaptureStatus.AntiCheatDetected, antiCheat);
            }

            if (IsRunning(target))
            {
                return new GameCaptureResult(GameCaptureStatus.AlreadyRunning,
                    $"{target.GameName} is already running. Close it first: the key is only present in memory while the game starts up.");
            }

            List<byte[]> tails = GameMemoryCapture.LoadTails(target.SampleFiles);
            if (tails.Count == 0)
            {
                return new GameCaptureResult(GameCaptureStatus.NoSuitableFiles,
                    "No block-aligned encrypted files were found to validate a captured key.");
            }

            TimeSpan limit = timeout ?? DefaultTimeout;
            using var tracker = new OwnedProcessTracker(target);
            var clock = Stopwatch.StartNew();
            double lastPercent = 0;
            void Report(string message, double percent)
            {
                lastPercent = Math.Max(lastPercent, percent);
                progress?.Report(new GameCaptureProgress(message, lastPercent));
            }

            Report($"Launching {target.GameName}…", 3);
            try
            {
                using Process? launched = Process.Start(new ProcessStartInfo(target.ExecutablePath)
                {
                    WorkingDirectory = target.GameDirectory,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
            {
                return new GameCaptureResult(GameCaptureStatus.LaunchFailed, "Could not start the game: " + ex.Message);
            }

            tracker.MarkLaunched();
            var status = GameCaptureStatus.TimedOut;
            var message = "The key was not found in the game memory before the timeout.";
            CapturedCipher? cipher = null;
            DecryptionProfile? profile = null;
            int validated = 0;
            int checkedCount = 0;
            var foundKeys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var foundDumps = new Dictionary<string, PlaintextBundleDump>(StringComparer.Ordinal);
            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    List<Process> owned = tracker.RefreshOwned();
                    if (owned.Count == 0)
                    {
                        if (clock.Elapsed > EarlyExitGrace)
                        {
                            status = GameCaptureStatus.ExitedEarly;
                            message = "The game closed before its keys could be captured.";
                            break;
                        }

                        Report("Waiting for the game to start…", 5);
                        await Task.Delay(1000, cancellationToken);
                        continue;
                    }

                    if (clock.Elapsed > limit)
                    {
                        break;
                    }

                    Process primary = owned.OrderByDescending(static process => WorkingSet(process)).First();
                    double elapsedPercent = 8 + 70 * clock.Elapsed.TotalSeconds / limit.TotalSeconds;
                    Report(foundKeys.Count > 0
                        ? $"Waiting for the game to load its encrypted bundles ({clock.Elapsed.TotalSeconds:0}s)…"
                        : $"Reading game memory ({clock.Elapsed.TotalSeconds:0}s)…", elapsedPercent);
                    GameMemoryScanResult scan;
                    try
                    {
                        scan = await Task.Run(() => GameMemoryCapture.ScanProcess(primary.Id, tails, GameMemoryCapture.DefaultDumpBytes, cancellationToken), cancellationToken);
                    }
                    catch (GameMemoryAccessException)
                    {
                        status = GameCaptureStatus.AntiCheatDetected;
                        message = "The game does not allow its memory to be read, so it is treated as protected. Capture aborted.";
                        break;
                    }

                    foreach (byte[] key in scan.Keys)
                    {
                        foundKeys[Convert.ToHexString(key)] = key;
                    }

                    foreach (PlaintextBundleDump dump in scan.Dumps)
                    {
                        foundDumps[dump.Id] = dump;
                    }

                    if (foundKeys.Count > 0 && foundDumps.Count > 0)
                    {
                        Report("Key found. Recovering IV and header mask…", 85);
                        CapturedCipher? best = null;
                        foreach (byte[] key in foundKeys.Values)
                        {
                            if (!RuntimeCipherRecovery.TryRecover(key, target.SampleFiles, foundDumps.Values.ToArray(), out CapturedCipher? candidate) || candidate == null)
                            {
                                continue;
                            }

                            int files = RuntimeCipherRecovery.ValidateOnFiles(candidate, target.SampleFiles, out int filesChecked);
                            if (files > validated || (files == validated && filesChecked > checkedCount))
                            {
                                validated = files;
                                checkedCount = filesChecked;
                                best = candidate;
                            }
                        }

                        if (best != null && validated > 0)
                        {
                            cipher = best;
                            profile = BuildProfile(target, best);
                            status = GameCaptureStatus.Succeeded;
                            message = $"Captured a {best.Key.Length * 8}-bit key validated on {validated} of {checkedCount} file(s).";
                            break;
                        }

                        Report("Key found, but the header mask is not confirmed yet. Scanning again…", 85);
                    }

                    await Task.Delay(500, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                status = GameCaptureStatus.Cancelled;
                message = "Capture cancelled.";
            }
            finally
            {
                Report("Closing the game…", 95);
                await tracker.CloseAllAsync();
            }

            Report(status == GameCaptureStatus.Succeeded ? "Done. The game was closed." : message, status == GameCaptureStatus.Succeeded ? 100 : 0);
            return new GameCaptureResult(status, message, profile, validated, checkedCount, cipher?.VerifiedBytes ?? 0);
        }

        private static string BuildPathPattern(GameCaptureTarget target)
        {
            string gameName = Path.GetFileName(target.GameDirectory);
            string relative = Path.GetRelativePath(target.GameDirectory, target.SourceDirectory).Replace('\\', '/');
            string pattern = relative == "." ? $"*/{gameName}/*" : $"*/{gameName}/{relative}/*";
            return pattern.IndexOfAny(new[] { ';', ',' }) >= 0 ? string.Empty : pattern;
        }

        internal static string? TryGetMainPath(Process process)
        {
            try
            {
                return process.MainModule?.FileName;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
            {
                return null;
            }
        }

        private static long WorkingSet(Process process)
        {
            try
            {
                return process.WorkingSet64;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                return 0;
            }
        }

        private sealed class OwnedProcessTracker : IDisposable
        {
            private readonly string prefix;
            private readonly string mainProcessName;
            private readonly HashSet<int> baseline = new();
            private readonly List<Process> owned = new();
            private DateTime? launchUtc;

            public OwnedProcessTracker(GameCaptureTarget target)
            {
                prefix = Path.GetFullPath(target.GameDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                mainProcessName = Path.GetFileNameWithoutExtension(target.ExecutablePath);
                foreach (Process process in Process.GetProcesses())
                {
                    using (process)
                    {
                        try
                        {
                            baseline.Add(process.Id);
                        }
                        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                        {
                        }
                    }
                }
            }

            public void MarkLaunched() => launchUtc = DateTime.UtcNow;

            public List<Process> RefreshOwned()
            {
                var current = new List<Process>();
                foreach (Process process in Process.GetProcesses())
                {
                    if (IsOwned(process))
                    {
                        current.Add(process);
                    }
                    else
                    {
                        process.Dispose();
                    }
                }

                foreach (Process stale in owned)
                {
                    stale.Dispose();
                }

                owned.Clear();
                owned.AddRange(current);
                return owned;
            }

            public async Task CloseAllAsync()
            {
                foreach (Process process in RefreshOwned())
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            process.CloseMainWindow();
                        }
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                    {
                    }
                }

                var deadline = DateTime.UtcNow + CloseGrace;
                while (DateTime.UtcNow < deadline)
                {
                    if (RefreshOwned().Count == 0)
                    {
                        return;
                    }

                    await Task.Delay(500);
                }

                foreach (Process process in RefreshOwned())
                {
                    try
                    {
                        process.Kill();
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                    {
                    }
                }
            }

            private bool IsOwned(Process process)
            {
                try
                {
                    if (baseline.Contains(process.Id))
                    {
                        return false;
                    }

                    string? path = TryGetMainPath(process);
                    if (path != null)
                    {
                        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
                    }

                    return launchUtc != null
                        && string.Equals(process.ProcessName, mainProcessName, StringComparison.OrdinalIgnoreCase)
                        && process.StartTime.ToUniversalTime() >= launchUtc.Value.AddSeconds(-1);
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                {
                    return false;
                }
            }

            public void Dispose()
            {
                foreach (Process process in owned)
                {
                    process.Dispose();
                }

                owned.Clear();
            }
        }
    }
}
