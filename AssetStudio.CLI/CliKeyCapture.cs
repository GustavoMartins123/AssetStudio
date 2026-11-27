using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace AssetStudio.CLI
{
    internal static class CliKeyCapture
    {
        private const string CommandName = "capture-keys";
        private const int ExitCaptureFailed = CliRunner.ExitRuntimeError;

        private sealed record Report(
            string Status,
            string Message,
            string? GameName,
            string? Executable,
            string? ProfileId,
            string? ProfilePath,
            int ValidatedFiles,
            int CheckedFiles);

        public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
        {
            string? targetPath = null;
            string? executable = null;
            TimeSpan? timeout = null;
            bool json = false;
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (string.Equals(arg, "--exe", StringComparison.Ordinal))
                {
                    if (!CliRunner.TryReadOptionValue(args, ref i, "--exe", stderr, out executable))
                    {
                        return CliRunner.ExitInvalidArguments;
                    }
                }
                else if (string.Equals(arg, "--timeout", StringComparison.Ordinal))
                {
                    if (!CliRunner.TryReadOptionValue(args, ref i, "--timeout", stderr, out string? value)
                        || !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
                        || !double.IsFinite(seconds) || seconds < 5 || seconds > 3600)
                    {
                        return CliRunner.InvalidArg(stderr, "Invalid --timeout value. Use a number of seconds between 5 and 3600.");
                    }

                    timeout = TimeSpan.FromSeconds(seconds);
                }
                else if (string.Equals(arg, "--format", StringComparison.Ordinal))
                {
                    if (!CliRunner.TryReadOptionValue(args, ref i, "--format", stderr, out string? value))
                    {
                        return CliRunner.ExitInvalidArguments;
                    }

                    if (string.Equals(value, "json", StringComparison.OrdinalIgnoreCase))
                    {
                        json = true;
                    }
                    else if (!string.Equals(value, "text", StringComparison.OrdinalIgnoreCase))
                    {
                        return CliRunner.InvalidArg(stderr, "Invalid --format value. Use text or json.");
                    }
                }
                else if (arg.StartsWith('-'))
                {
                    return CliRunner.InvalidArg(stderr, $"Unknown option '{arg}' for '{CommandName}'.");
                }
                else if (targetPath == null)
                {
                    targetPath = arg;
                }
                else
                {
                    return CliRunner.InvalidArg(stderr, $"Unexpected positional argument '{arg}' for '{CommandName}'.");
                }
            }

            if (string.IsNullOrWhiteSpace(targetPath))
            {
                return CliRunner.InvalidArg(stderr, $"Missing required <path> argument for '{CommandName}'.");
            }

            if (!Directory.Exists(targetPath) && !File.Exists(targetPath))
            {
                return CliRunner.InvalidArg(stderr, $"path does not exist: {targetPath}");
            }

            if (!GameKeyCapture.IsSupported)
            {
                return Fail(stderr, json, "Capturing keys from a running game is only available on Windows and Linux.", null);
            }

            GameCaptureTarget? target = ResolveTarget(targetPath, executable);
            if (target == null)
            {
                return Fail(stderr, json, "No game executable was found next to the encrypted files. Pass --exe <game executable>.", null);
            }

            string? lastMessage = null;
            var progress = new InlineProgress<GameCaptureProgress>(value =>
            {
                if (json || string.Equals(value.Message, lastMessage, StringComparison.Ordinal))
                {
                    return;
                }

                lastMessage = value.Message;
                stdout.WriteLine(value.Message);
            });
            GameCaptureResult result = GameKeyCapture.RunAsync(target, progress, timeout, CancellationToken.None).GetAwaiter().GetResult();
            if (result.Status != GameCaptureStatus.Succeeded || result.Profile == null)
            {
                return Fail(stderr, json, result.Message, CreateReport(result, target, null));
            }

            string profilePath = GameKeyCapture.SaveProfile(result.Profile);
            Report report = CreateReport(result, target, profilePath);
            if (json)
            {
                stdout.WriteLine(JsonSerializer.Serialize(report, CliRunner.JsonIndentedOptions));
            }
            else
            {
                stdout.WriteLine(result.Message);
                stdout.WriteLine($"Profile '{report.ProfileId}' saved to {profilePath}");
            }

            return CliRunner.ExitSuccess;
        }

        private static GameCaptureTarget? ResolveTarget(string targetPath, string? executable)
        {
            if (File.Exists(targetPath))
            {
                string file = Path.GetFullPath(targetPath);
                return string.IsNullOrWhiteSpace(executable) ? GameCaptureTarget.TryResolve(file) : GameCaptureTarget.FromExecutable(executable, file);
            }

            foreach (string file in new DirectoryInfo(targetPath).EnumerateFiles("*", SearchOption.AllDirectories).Select(static file => file.FullName))
            {
                if (!LooksEncrypted(file))
                {
                    continue;
                }

                GameCaptureTarget? target = string.IsNullOrWhiteSpace(executable) ? GameCaptureTarget.TryResolve(file) : GameCaptureTarget.FromExecutable(executable, file);
                if (target != null)
                {
                    return target;
                }
            }

            return null;
        }

        private static bool LooksEncrypted(string path)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                long length = stream.Length;
                if (length < 64 || length % 16 != 0)
                {
                    return false;
                }

                Span<byte> header = stackalloc byte[512];
                int read = 0;
                while (read < header.Length)
                {
                    int count = stream.Read(header.Slice(read));
                    if (count <= 0)
                    {
                        break;
                    }

                    read += count;
                }

                ReadOnlySpan<byte> probe = header.Slice(0, read);
                return !UnityHeaderProbe.TryParseUnityBundleHeader(probe, 0, length, out _, out _, out _, out _)
                    && !UnityHeaderProbe.IsSerializedFileHeader(probe, length);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static int Fail(TextWriter stderr, bool json, string message, Report? report)
        {
            if (json)
            {
                stderr.WriteLine(JsonSerializer.Serialize(report ?? new Report("failed", message, null, null, null, null, 0, 0), CliRunner.JsonCompactOptions));
            }
            else
            {
                stderr.WriteLine($"Error: {message}");
            }

            return ExitCaptureFailed;
        }

        private static Report CreateReport(GameCaptureResult result, GameCaptureTarget target, string? profilePath)
            => new(result.Status.ToString(), result.Message, target.GameName, target.ExecutablePath, result.Profile?.Id, profilePath, result.ValidatedFiles, result.CheckedFiles);

        private sealed class InlineProgress<T> : IProgress<T>
        {
            private readonly Action<T> handler;

            public InlineProgress(Action<T> handler) => this.handler = handler;

            public void Report(T value) => handler(value);
        }
    }
}
