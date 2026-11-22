using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace AssetStudio.Avalonia.Services.ProjectAnalysis;

public sealed class ProjectAnalysisService
{
    public const string AnalysisFileName = "analysis.json";
    public const string Il2CppScriptIndexFileName = "il2cpp-script-index.json";
    public const string AddressablesIndexFileName = "addressables-index.json";
    public const string DecryptionHintsFileName = "decryption-hints.json";
    private const string ExternalDumperOutputFolderName = "il2cppdumper";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static ProjectAnalysisService Shared { get; } = new();

    private readonly string _cacheBaseDirectory;
    private readonly ProjectRuntimeDetector _runtimeDetector;
    private readonly Il2CppDumperArtifactImporter _dumperImporter;
    private readonly AddressablesCatalogAnalyzer _addressablesAnalyzer;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _projectLocks = new(StringComparer.OrdinalIgnoreCase);

    public event Action<string, ProjectAnalysisReport>? AnalysisUpdated;

    public ProjectAnalysisService()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AssetStudio",
            "ProjectAnalysis"))
    {
    }

    internal ProjectAnalysisService(
        string cacheBaseDirectory,
        ProjectRuntimeDetector? runtimeDetector = null,
        Il2CppDumperArtifactImporter? dumperImporter = null,
        AddressablesCatalogAnalyzer? addressablesAnalyzer = null)
    {
        if (string.IsNullOrWhiteSpace(cacheBaseDirectory))
        {
            throw new ArgumentException("Cache base directory must not be empty.", nameof(cacheBaseDirectory));
        }

        _cacheBaseDirectory = Path.GetFullPath(cacheBaseDirectory);
        _runtimeDetector = runtimeDetector ?? new ProjectRuntimeDetector();
        _dumperImporter = dumperImporter ?? new Il2CppDumperArtifactImporter();
        _addressablesAnalyzer = addressablesAnalyzer ?? new AddressablesCatalogAnalyzer();
    }

    public string CacheBaseDirectory => _cacheBaseDirectory;

    public string GetProjectCacheDirectory(string projectRoot)
    {
        var hash = ProjectRuntimeDetector.ComputeProjectHash(projectRoot);
        return Path.Combine(_cacheBaseDirectory, hash);
    }

    public ProjectAnalysisReport? TryLoadCachedReport(string projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            return null;
        }

        var path = Path.Combine(GetProjectCacheDirectory(projectRoot), AnalysisFileName);
        return TryReadJson<ProjectAnalysisReport>(path);
    }

    public ProjectAnalysisResult? TryLoadCachedResult(string projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            return null;
        }

        var dir = GetProjectCacheDirectory(projectRoot);
        var report = TryReadJson<ProjectAnalysisReport>(Path.Combine(dir, AnalysisFileName));
        if (report == null)
        {
            return null;
        }

        var scriptIndex = TryReadJson<Il2CppScriptIndex>(Path.Combine(dir, Il2CppScriptIndexFileName));
        var addressables = TryReadJson<AddressablesIndex>(Path.Combine(dir, AddressablesIndexFileName));
        var hints = TryReadJson<DecryptionHintsReport>(Path.Combine(dir, DecryptionHintsFileName));

        if (scriptIndex == null || addressables == null || hints == null)
        {
            return null;
        }

        return new ProjectAnalysisResult
        {
            Report = report,
            ScriptIndex = scriptIndex,
            AddressablesIndex = addressables,
            DecryptionHints = hints,
            FromCache = true
        };
    }

    public void DeleteProjectAnalysisCache(string projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            return;
        }

        try
        {
            var baseFull = Path.GetFullPath(_cacheBaseDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var targetFull = Path.GetFullPath(GetProjectCacheDirectory(projectRoot));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

            if (targetFull.StartsWith(baseFull, comparison) && Directory.Exists(targetFull))
            {
                Directory.Delete(targetFull, recursive: true);
            }
        }
        catch
        {
        }
    }

    public async Task<ProjectAnalysisResult> AnalyzeProjectAsync(
        string projectRoot,
        ProjectAnalysisOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new ProjectAnalysisOptions();

        if (string.IsNullOrWhiteSpace(projectRoot) || !Directory.Exists(projectRoot))
        {
            var missingRoot = projectRoot ?? string.Empty;
            var failedReport = new ProjectAnalysisReport
            {
                ProjectRoot = missingRoot,
                ProjectHash = ProjectRuntimeDetector.ComputeProjectHash(missingRoot),
                Status = ProjectAnalysisStatus.Failed,
                AnalyzedAtUtc = DateTime.UtcNow,
                ErrorMessage = string.IsNullOrWhiteSpace(missingRoot)
                    ? "Project root path is empty."
                    : $"Project root directory does not exist: {missingRoot}"
            };

            PublishUpdate(missingRoot, failedReport);
            return new ProjectAnalysisResult
            {
                Report = failedReport,
                ScriptIndex = new Il2CppScriptIndex { ProjectRoot = missingRoot },
                AddressablesIndex = new AddressablesIndex { ProjectRoot = missingRoot },
                DecryptionHints = new DecryptionHintsReport { ProjectRoot = missingRoot },
                FromCache = false
            };
        }

        var fullRoot = Path.GetFullPath(projectRoot);
        var projectHash = ProjectRuntimeDetector.ComputeProjectHash(fullRoot);
        var gate = _projectLocks.GetOrAdd(projectHash, static _ => new SemaphoreSlim(1, 1));

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await AnalyzeProjectCoreAsync(fullRoot, projectHash, options, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<ProjectAnalysisResult> AnalyzeProjectCoreAsync(
        string fullRoot,
        string projectHash,
        ProjectAnalysisOptions options,
        CancellationToken cancellationToken)
    {
        var cacheDir = Path.Combine(_cacheBaseDirectory, projectHash);
        var externalOutputDir = Path.Combine(cacheDir, ExternalDumperOutputFolderName);

        var pendingReport = new ProjectAnalysisReport
        {
            ProjectRoot = fullRoot,
            ProjectHash = projectHash,
            Status = ProjectAnalysisStatus.Pending
        };
        PublishUpdate(fullRoot, pendingReport);

        try
        {
            var extraArtifactDirs = new List<string> { externalOutputDir };
            if (!string.IsNullOrWhiteSpace(options.CustomIl2CppArtifactsDirectory))
            {
                extraArtifactDirs.Add(options.CustomIl2CppArtifactsDirectory);
            }

            // Lightweight signature & runtime detection
            var detection = await Task.Run(
                () => _runtimeDetector.Detect(fullRoot, extraArtifactDirs, cancellationToken),
                cancellationToken).ConfigureAwait(false);

            // Check cached analysis before doing deeper parsing or tool execution
            if (!options.ForceRefresh)
            {
                var cached = TryLoadCachedResult(fullRoot);
                if (cached != null
                    && cached.Report.Status == ProjectAnalysisStatus.Ready
                    && string.Equals(cached.Report.Signature, detection.Signature, StringComparison.Ordinal)
                    && !ShouldRunExternalDumperForCacheBypass(detection, options))
                {
                    PublishUpdate(fullRoot, cached.Report);
                    return cached;
                }
            }

            // Transition to Running
            var runningReport = BuildReportFromDetection(detection, ProjectAnalysisStatus.Running);
            PublishUpdate(fullRoot, runningReport);

            var warnings = new List<string>(detection.Warnings);
            var externalDumperExecuted = false;

            // Optional external Il2CppDumper execution ONLY when explicitly enabled and artifacts do not already exist
            if (options.AllowExternalIl2CppDumper
                && detection.HasGameAssembly
                && detection.HasGlobalMetadata
                && detection.Il2CppDumperArtifactPaths.Count == 0)
            {
                if (string.IsNullOrWhiteSpace(options.Il2CppDumperExecutablePath) || !File.Exists(options.Il2CppDumperExecutablePath))
                {
                    warnings.Add("External Il2CppDumper execution is enabled, but no valid Il2CppDumper executable path is configured.");
                }
                else
                {
                    var dumpSucceeded = await TryRunExternalIl2CppDumperAsync(
                        options.Il2CppDumperExecutablePath,
                        detection.GameAssemblyPath,
                        detection.GlobalMetadataPath,
                        externalOutputDir,
                        options.ExternalToolTimeout,
                        warnings,
                        cancellationToken).ConfigureAwait(false);

                    if (dumpSucceeded)
                    {
                        externalDumperExecuted = true;
                        detection = await Task.Run(
                            () => _runtimeDetector.Detect(fullRoot, extraArtifactDirs, cancellationToken),
                            cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            // Import Il2CppDumper artifacts (streaming, OOM-safe)
            var scriptIndex = await Task.Run(
                () => _dumperImporter.ImportArtifacts(fullRoot, detection.Il2CppDumperArtifactPaths, warnings, cancellationToken),
                cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            // Parse Addressables catalogs & emit decryption hints
            var (addressablesIndex, decryptionHints) = await Task.Run(
                () => _addressablesAnalyzer.AnalyzeCatalogs(fullRoot, detection.AddressablesCatalogPaths, warnings, cancellationToken),
                cancellationToken).ConfigureAwait(false);

            if (detection.HasGlobalMetadata && detection.Il2CppMetadataObfuscated)
            {
                decryptionHints.Hints.Insert(0, new DecryptionHintEntry
                {
                    Provider = "Il2CppGlobalMetadata",
                    CatalogPath = detection.GlobalMetadataPath,
                    Bundle = Path.GetFileName(detection.GlobalMetadataPath),
                    Reason = detection.Il2CppMetadataAnomalyReason
                        ?? "global-metadata.dat header is invalid or uses an anomalous version value outside 16..100.",
                    Confidence = 0.92,
                    MetadataSummary = "IL2CPP global-metadata.dat header anomaly"
                });
            }

            foreach (var barrier in detection.EncryptionBarriers)
            {
                decryptionHints.Hints.Add(new DecryptionHintEntry
                {
                    Provider = barrier.ProviderId,
                    CatalogPath = barrier.FilePath,
                    Bundle = Path.GetFileName(barrier.FilePath),
                    Reason = barrier.Reason,
                    Confidence = barrier.Confidence,
                    MetadataSummary = $"Header prefix: {barrier.HeaderPrefixHex}"
                });
            }

            cancellationToken.ThrowIfCancellationRequested();

            var finalReport = BuildReportFromDetection(detection, ProjectAnalysisStatus.Ready);
            finalReport.AnalyzedAtUtc = DateTime.UtcNow;
            finalReport.ExternalIl2CppDumperExecuted = externalDumperExecuted;
            finalReport.Il2CppScriptCount = scriptIndex.ComputeTotalScriptCount();
            finalReport.AddressablesBundleCount = addressablesIndex.TotalBundleCount;
            finalReport.DecryptionHintCount = decryptionHints.Hints.Count;
            finalReport.Warnings = warnings;

            // Persist all 4 JSON files atomically
            Directory.CreateDirectory(cacheDir);
            WriteJsonAtomic(Path.Combine(cacheDir, Il2CppScriptIndexFileName), scriptIndex);
            WriteJsonAtomic(Path.Combine(cacheDir, AddressablesIndexFileName), addressablesIndex);
            WriteJsonAtomic(Path.Combine(cacheDir, DecryptionHintsFileName), decryptionHints);
            WriteJsonAtomic(Path.Combine(cacheDir, AnalysisFileName), finalReport);

            PublishUpdate(fullRoot, finalReport);

            return new ProjectAnalysisResult
            {
                Report = finalReport,
                ScriptIndex = scriptIndex,
                AddressablesIndex = addressablesIndex,
                DecryptionHints = decryptionHints,
                FromCache = false
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var failedReport = new ProjectAnalysisReport
            {
                ProjectRoot = fullRoot,
                ProjectHash = projectHash,
                Status = ProjectAnalysisStatus.Failed,
                AnalyzedAtUtc = DateTime.UtcNow,
                ErrorMessage = ex.Message
            };

            PublishUpdate(fullRoot, failedReport);
            return new ProjectAnalysisResult
            {
                Report = failedReport,
                ScriptIndex = new Il2CppScriptIndex { ProjectRoot = fullRoot },
                AddressablesIndex = new AddressablesIndex { ProjectRoot = fullRoot },
                DecryptionHints = new DecryptionHintsReport { ProjectRoot = fullRoot },
                FromCache = false
            };
        }
    }

    private static bool ShouldRunExternalDumperForCacheBypass(
        ProjectRuntimeDetectionResult detection,
        ProjectAnalysisOptions options)
    {
        return options.AllowExternalIl2CppDumper
            && detection.HasGameAssembly
            && detection.HasGlobalMetadata
            && detection.Il2CppDumperArtifactPaths.Count == 0
            && !string.IsNullOrWhiteSpace(options.Il2CppDumperExecutablePath)
            && File.Exists(options.Il2CppDumperExecutablePath);
    }

    private static ProjectAnalysisReport BuildReportFromDetection(
        ProjectRuntimeDetectionResult detection,
        ProjectAnalysisStatus status)
    {
        return new ProjectAnalysisReport
        {
            ProjectRoot = detection.ProjectRoot,
            ProjectHash = detection.ProjectHash,
            Signature = detection.Signature,
            Status = status,
            UnityVersion = detection.UnityVersion,
            ScriptingBackend = detection.ScriptingBackend,
            HasGameAssembly = detection.HasGameAssembly,
            GameAssemblyPath = detection.GameAssemblyPath,
            HasGlobalMetadata = detection.HasGlobalMetadata,
            GlobalMetadataPath = detection.GlobalMetadataPath,
            Il2CppMetadataVersion = detection.Il2CppMetadataVersion,
            Il2CppMetadataObfuscated = detection.Il2CppMetadataObfuscated,
            HasManagedAssemblies = detection.HasManagedAssemblies,
            ManagedAssemblyCount = detection.ManagedAssemblyCount,
            HasAddressables = detection.HasAddressables,
            AddressablesCatalogPaths = new List<string>(detection.AddressablesCatalogPaths),
            Il2CppDumperArtifactsDetected = detection.Il2CppDumperArtifactPaths.Count > 0,
            Il2CppDumperArtifactPaths = new List<string>(detection.Il2CppDumperArtifactPaths),
            Warnings = new List<string>(detection.Warnings)
        };
    }

    private static async Task<bool> TryRunExternalIl2CppDumperAsync(
        string executablePath,
        string gameAssemblyPath,
        string globalMetadataPath,
        string outputDirectory,
        TimeSpan timeout,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(outputDirectory);
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                WorkingDirectory = outputDirectory
            };
            startInfo.ArgumentList.Add(gameAssemblyPath);
            startInfo.ArgumentList.Add(globalMetadataPath);
            startInfo.ArgumentList.Add(outputDirectory);

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                warnings.Add("Failed to start external Il2CppDumper process.");
                return false;
            }

            try
            {
                // Unblock interactive "Press any key to exit" prompts if Il2CppDumper pauses at end
                await process.StandardInput.WriteLineAsync().ConfigureAwait(false);
                process.StandardInput.Close();
            }
            catch
            {
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            var effectiveTimeout = timeout > TimeSpan.Zero ? timeout : TimeSpan.FromMinutes(3);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(effectiveTimeout);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKillProcessTree(process);
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                warnings.Add($"External Il2CppDumper timed out after {effectiveTimeout.TotalSeconds:0}s.");
                return false;
            }

            var stderr = string.Empty;
            try
            {
                _ = await stdoutTask.ConfigureAwait(false);
                stderr = await stderrTask.ConfigureAwait(false);
            }
            catch
            {
            }

            if (process.ExitCode != 0)
            {
                var detail = string.IsNullOrWhiteSpace(stderr) ? $"exit code {process.ExitCode}" : stderr.Trim();
                if (detail.Length > 240)
                {
                    detail = detail.Substring(0, 240) + "...";
                }

                warnings.Add($"External Il2CppDumper exited with error ({detail}).");
                return false;
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            warnings.Add($"External Il2CppDumper execution failed: {ex.Message}");
            return false;
        }
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    private void PublishUpdate(string projectRoot, ProjectAnalysisReport report)
    {
        var handlers = AnalysisUpdated;
        if (handlers == null)
        {
            return;
        }

        var snapshot = report.Clone();
        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<string, ProjectAnalysisReport>)handler).Invoke(projectRoot, snapshot);
            }
            catch
            {
            }
        }
    }

    private static T? TryReadJson<T>(string filePath) where T : class
    {
        if (!File.Exists(filePath))
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return JsonSerializer.Deserialize<T>(stream, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static void WriteJsonAtomic<T>(string targetPath, T value)
    {
        var directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var json = JsonSerializer.Serialize(value, JsonOptions);
            File.WriteAllText(tempPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(tempPath, targetPath, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
            }

            throw;
        }
    }
}
