#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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
                string? dataDirectory = FindDataDirectory(Path.GetDirectoryName(fullPath));
                if (dataDirectory == null)
                {
                    return null;
                }

                string? executable = FindGameExecutable(Path.GetDirectoryName(dataDirectory)!, Path.GetFileName(dataDirectory)[..^5]);
                return executable == null ? null : Create(executable, fullPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return null;
            }
        }

        public static GameCaptureTarget? FromExecutable(string? executablePath, string encryptedFilePath)
        {
            if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath) || string.IsNullOrWhiteSpace(encryptedFilePath))
            {
                return null;
            }

            try
            {
                return Create(Path.GetFullPath(executablePath), Path.GetFullPath(encryptedFilePath));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return null;
            }
        }

        private static GameCaptureTarget? Create(string executablePath, string encryptedFilePath)
        {
            string sourceDirectory = Path.GetDirectoryName(encryptedFilePath) ?? string.Empty;
            if (sourceDirectory.Length == 0)
            {
                return null;
            }

            string extension = Path.GetExtension(encryptedFilePath);
            string[] samples = Directory.GetFiles(sourceDirectory)
                .Where(path => extension.Length == 0 || string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase))
                .Select(path => new FileInfo(path))
                .Where(info => info.Length >= 64 && info.Length % 16 == 0)
                .OrderByDescending(info => info.Length)
                .Take(MaxSampleFiles)
                .Select(info => info.FullName)
                .ToArray();
            string gameDirectory = Path.GetDirectoryName(executablePath) ?? sourceDirectory;
            string? current = gameDirectory;
            for (int depth = 0; current != null && depth < 3; depth++, current = Path.GetDirectoryName(current))
            {
                if (Directory.EnumerateDirectories(current).Any(static dir => Path.GetFileName(dir).EndsWith("_Data", StringComparison.OrdinalIgnoreCase)))
                {
                    gameDirectory = current;
                    break;
                }
            }
            return samples.Length == 0 ? null : new GameCaptureTarget(gameDirectory, executablePath, sourceDirectory, samples);
        }

        private static string? FindDataDirectory(string? directory)
        {
            string? current = directory;
            for (int depth = 0; depth < 8 && current != null; depth++)
            {
                if (Path.GetFileName(current).EndsWith("_Data", StringComparison.OrdinalIgnoreCase))
                {
                    return current;
                }

                current = Path.GetDirectoryName(current);
            }

            return null;
        }

        private static string? FindGameExecutable(string gameDirectory, string dataStem)
        {
            if (OperatingSystem.IsWindows())
            {
                string[] executables = Directory.GetFiles(gameDirectory, "*.exe", SearchOption.TopDirectoryOnly);
                return executables.FirstOrDefault(path => string.Equals(Path.GetFileNameWithoutExtension(path), dataStem, StringComparison.OrdinalIgnoreCase))
                    ?? executables.Where(path => !IsAuxiliaryExecutable(path)).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            }

            if (!OperatingSystem.IsLinux())
            {
                return null;
            }

            return Directory.EnumerateFiles(gameDirectory)
                .Where(IsLinuxExecutableCandidate)
                .OrderBy(path => Path.GetFileName(path) == dataStem ? 0 : string.Equals(Path.GetFileNameWithoutExtension(path), dataStem, StringComparison.OrdinalIgnoreCase) ? 1 : 2)
                .ThenBy(path => IsAuxiliaryExecutable(path) ? 1 : 0)
                .ThenBy(path => HasExecutablePermission(path, ExecutePermission) == 0 ? 0 : 1)
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }

        private const int ExecutePermission = 1;

        [DllImport("libc", EntryPoint = "access", SetLastError = true)]
        private static extern int HasExecutablePermission(string path, int mode);

        private static bool IsLinuxExecutableCandidate(string path)
        {
            string name = Path.GetFileName(path);
            return !name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                && !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                && !name.EndsWith(".so", StringComparison.OrdinalIgnoreCase)
                && !name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
                && !name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);
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
        UnconfirmedKey,
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
        public const string CapturedProfilePrefix = "captured-";
        private static readonly TimeSpan EarlyExitGrace = TimeSpan.FromSeconds(45);
        private static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(20);

        public static TimeSpan DefaultTimeout { get; } = TimeSpan.FromSeconds(150);
        public static bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsLinux();

        public static bool IsRunning(GameCaptureTarget target)
        {
            if (!IsSupported)
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
            CapturedKey key = cipher.Key;
            string algorithm = key.Kind == CapturedCipherKind.Rijndael256 ? "rijndael-cbc"
                : key.Mode == CapturedCipherMode.Cbc ? "aes-cbc"
                : key.Mode == CapturedCipherMode.Ecb ? "aes-ecb"
                : "aes-ctr";
            var profile = new DecryptionProfile
            {
                Id = CapturedProfilePrefix + algorithm + "-" + ScopeFingerprint(target),
                Algorithm = algorithm,
                KeyHex = Convert.ToHexString(key.Key),
                Padding = key.Padding ? "pkcs7" : "none",
                ValidateUnityHeader = true,
            };

            string extension = Path.GetExtension(target.SampleFiles[0]);
            profile.FileNamePattern = extension.Length == 0 ? "*" : "*" + extension;
            string pathPattern = BuildPathPattern(target);
            if (pathPattern.Length > 0)
            {
                profile.PathPattern = pathPattern;
            }

            if (key.Kind == CapturedCipherKind.Rijndael256)
            {
                profile.BlockSizeBits = 256;
            }

            if (cipher.IvFromStreamPrefix)
            {
                profile.IvFromStreamPrefix = true;
            }
            else if (key.Mode != CapturedCipherMode.Ecb)
            {
                profile.IvHex = Convert.ToHexString(cipher.FixedIv);
            }

            if (key.Mode == CapturedCipherMode.Ctr)
            {
                profile.CounterStart = key.CounterStart;
                profile.CounterLittleEndian = key.CounterLittleEndian;
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

        public static string SaveProfile(DecryptionProfile profile, string? directoryOverride = null)
        {
            foreach (DecryptionProfile existing in DecryptionHelper.GetRegisteredProfiles())
            {
                if (IsSupersededCapture(existing, profile))
                {
                    DecryptionHelper.RemoveProfile(existing.Id, deleteUserFile: directoryOverride == null);
                    if (directoryOverride != null)
                    {
                        TryDeleteFile(Path.Combine(directoryOverride, existing.Id + ".json"));
                    }
                }
            }

            return DecryptionHelper.SaveProfileToUserDirectory(profile, directoryOverride);
        }

        public static bool IsCapturedProfile(DecryptionProfile profile)
            => profile.Id.StartsWith(CapturedProfilePrefix, StringComparison.OrdinalIgnoreCase);

        private static bool IsSupersededCapture(DecryptionProfile existing, DecryptionProfile replacement)
            => IsCapturedProfile(existing)
                && !string.Equals(existing.Id, replacement.Id, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(replacement.PathPattern)
                && string.Equals(existing.PathPattern, replacement.PathPattern, StringComparison.OrdinalIgnoreCase);

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        private static string ScopeFingerprint(GameCaptureTarget target)
        {
            string scope = Path.GetExtension(target.SampleFiles[0]) + "\n" + BuildPathPattern(target);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope)))[..12].ToLowerInvariant();
        }

        public static async Task<GameCaptureResult> RunAsync(
            GameCaptureTarget target,
            IProgress<GameCaptureProgress>? progress = null,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(target);
            if (!IsSupported)
            {
                return new GameCaptureResult(GameCaptureStatus.UnsupportedPlatform, "Capturing keys from a running game is only available on Windows and Linux.");
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

            List<FileTail> tails = KeyValidationContext.LoadTails(target.SampleFiles);
            if (tails.Count == 0)
            {
                return new GameCaptureResult(GameCaptureStatus.NoSuitableFiles,
                    "No block-aligned encrypted files were found to validate a captured key.");
            }

            KeyValidationContext validation = KeyValidationContext.Create(tails, target.SampleFiles, null);

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
            int launchedId;
            try
            {
                using Process? launched = Process.Start(new ProcessStartInfo(target.ExecutablePath)
                {
                    WorkingDirectory = target.GameDirectory,
                    UseShellExecute = true,
                });
                launchedId = launched?.Id ?? 0;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
            {
                return new GameCaptureResult(GameCaptureStatus.LaunchFailed, "Could not start the game: " + ex.Message);
            }

            tracker.MarkLaunched(launchedId);
            var status = GameCaptureStatus.TimedOut;
            var message = "The key was not found in the game memory before the timeout.";
            CapturedCipher? cipher = null;
            DecryptionProfile? profile = null;
            int validated = 0;
            int checkedCount = 0;
            var foundKeys = new Dictionary<string, CapturedKey>(StringComparer.Ordinal);
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
                        scan = await Task.Run(() => GameMemoryCapture.ScanProcess(primary.Id, validation, GameMemoryCapture.DefaultDumpBytes, cancellationToken), cancellationToken);
                    }
                    catch (GameMemoryAccessException)
                    {
                        status = GameCaptureStatus.AntiCheatDetected;
                        message = "The game does not allow its memory to be read, so it is treated as protected. Capture aborted.";
                        break;
                    }

                    foreach (CapturedKey key in scan.Keys)
                    {
                        foundKeys[key.Id] = key;
                    }

                    bool newDumps = false;
                    foreach (PlaintextBundleDump dump in scan.Dumps)
                    {
                        newDumps |= !foundDumps.ContainsKey(dump.Id);
                        foundDumps[dump.Id] = dump;
                    }

                    if (newDumps)
                    {
                        validation = KeyValidationContext.Create(tails, target.SampleFiles, foundDumps.Values.ToArray());
                    }

                    if (foundKeys.Count > 0 && foundDumps.Count > 0)
                    {
                        Report("Key found. Recovering IV and header mask…", 85);
                        CapturedCipher? best = null;
                        foreach (CapturedKey key in foundKeys.Values)
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
                            message = $"Captured {best.Key.DisplayName} ({best.Key.Key.Length * 8}-bit) validated on {validated} of {checkedCount} file(s).";
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

            if (status == GameCaptureStatus.TimedOut && foundKeys.Count > 0)
            {
                status = GameCaptureStatus.UnconfirmedKey;
                message = "A key candidate was found, but no decrypted bundle in memory could confirm its IV and header mask.";
            }

            Report(status == GameCaptureStatus.Succeeded ? "Done. The game was closed." : message, status == GameCaptureStatus.Succeeded ? 100 : 0);
            return new GameCaptureResult(status, message, profile, validated, checkedCount, cipher?.VerifiedBytes ?? 0);
        }

        private static string BuildPathPattern(GameCaptureTarget target)
        {
            string relative = Path.GetRelativePath(target.GameDirectory, target.SourceDirectory).Replace('\\', '/');
            string pattern = relative == "." ? $"*/{Path.GetFileName(target.GameDirectory)}/*"
                : relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)
                    ? target.SourceDirectory.Replace('\\', '/') + "/*"
                    : $"*/{Path.GetFileName(target.GameDirectory)}/{relative}/*";
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
            private int launchedId;

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

            public void MarkLaunched(int processId)
            {
                launchUtc = DateTime.UtcNow;
                launchedId = processId;
            }

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
                            RequestGracefulExit(process);
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

            private const int Sigterm = 15;

            [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
            private static extern int SendSignal(int processId, int signal);

            private static void RequestGracefulExit(Process process)
            {
                if (OperatingSystem.IsWindows())
                {
                    process.CloseMainWindow();
                }
                else if (OperatingSystem.IsLinux())
                {
                    SendSignal(process.Id, Sigterm);
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

                    if (process.Id == launchedId && StartedAfterLaunch(process))
                    {
                        return true;
                    }

                    string? path = TryGetMainPath(process);
                    if (path != null)
                    {
                        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
                    }

                    return StartedAfterLaunch(process)
                        && string.Equals(process.ProcessName, mainProcessName, StringComparison.OrdinalIgnoreCase);
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                {
                    return false;
                }
            }

            private bool StartedAfterLaunch(Process process)
                => launchUtc != null && process.StartTime.ToUniversalTime() >= launchUtc.Value.AddSeconds(-1);

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
