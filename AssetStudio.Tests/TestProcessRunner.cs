using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AssetStudio.Tests;

internal static class TestProcessRunner
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan OutputDrainTimeout = TimeSpan.FromSeconds(15);

    public static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(
        string fileName,
        string arguments,
        string workingDirectory,
        TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (IsDotnetHost(fileName))
        {
            DisableBuildServers(psi.Environment);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {fileName}");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        TimeSpan limit = timeout ?? DefaultTimeout;
        using var cts = new CancellationTokenSource(limit);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            string partialOut = await DrainAsync(stdoutTask);
            string partialErr = await DrainAsync(stderrTask);
            throw new TimeoutException(
                $"'{fileName} {arguments}' did not exit within {limit.TotalSeconds:0} s and was killed.\nSTDOUT:\n{partialOut}\nSTDERR:\n{partialErr}");
        }

        return (process.ExitCode, await DrainAsync(stdoutTask), await DrainAsync(stderrTask));
    }

    private static bool IsDotnetHost(string fileName)
    {
        string name = Path.GetFileNameWithoutExtension(fileName);
        return string.Equals(name, "dotnet", StringComparison.OrdinalIgnoreCase);
    }

    private static void DisableBuildServers(IDictionary<string, string?> environment)
    {
        environment["MSBUILDDISABLENODEREUSE"] = "1";
        environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        environment["UseSharedCompilation"] = "false";
        environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        environment["DOTNET_NOLOGO"] = "1";
    }

    private static async Task<string> DrainAsync(Task<string> readTask)
    {
        Task finished = await Task.WhenAny(readTask, Task.Delay(OutputDrainTimeout));
        return finished == readTask
            ? await readTask
            : "<output pipe still held open by a child process after exit>";
    }
}
