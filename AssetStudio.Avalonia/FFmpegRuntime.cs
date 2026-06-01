using FFmpegVideoPlayer.Core;
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace AssetStudio.Avalonia;

internal static class FFmpegRuntime
{
    private static readonly object InitLock = new();

    public static void Initialize()
    {
        if (FFmpegInitializer.IsInitialized)
            return;

        lock (InitLock)
        {
            if (FFmpegInitializer.IsInitialized)
                return;

            var localPath = FindBundledPath();
            FFmpegInitializer.Initialize(localPath, autoInstall: false, useBundledBinaries: true);
        }
    }

    private static string? FindBundledPath()
    {
        var baseDir = AppContext.BaseDirectory;

        foreach (var candidate in GetCandidateDirectories(baseDir))
        {
            if (HasRequiredLibraries(candidate))
                return candidate;
        }

        return null;
    }

    private static string[] GetCandidateDirectories(string baseDir)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new[]
            {
                baseDir,
                Path.Combine(baseDir, Environment.Is64BitProcess ? "x64" : "x86"),
                Path.Combine(baseDir, "ffmpeg"),
                Path.Combine(baseDir, "runtimes", Environment.Is64BitProcess ? "win-x64" : "win-x86", "native")
            };
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return new[]
            {
                baseDir,
                Path.Combine(baseDir, "x64", "ffmpeg"),
                Path.Combine(baseDir, "ffmpeg"),
                Path.Combine(baseDir, "runtimes", "linux-x64", "native")
            };
        }

        return new[]
        {
            baseDir,
            Path.Combine(baseDir, "ffmpeg"),
            Path.Combine(baseDir, "runtimes", "osx-x64", "native"),
            Path.Combine(baseDir, "runtimes", "osx-arm64", "native")
        };
    }

    private static bool HasRequiredLibraries(string directory)
    {
        if (!Directory.Exists(directory))
            return false;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return File.Exists(Path.Combine(directory, "avcodec-62.dll"));

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return File.Exists(Path.Combine(directory, "libavcodec.so.62"));

        return File.Exists(Path.Combine(directory, "libavcodec.62.dylib"));
    }
}
