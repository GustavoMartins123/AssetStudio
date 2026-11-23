using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AssetStudio.Avalonia.Services.ProjectAnalysis;

namespace AssetStudio.Avalonia.Services.Scripting;

public sealed class ScriptViewerService : IDisposable
{
    private readonly MonoDecompilerService _monoDecompiler;
    private readonly Il2CppDumpScriptService _il2CppService;
    private readonly GhidraExportService _ghidraExportService;

    public ScriptViewerService(
        MonoDecompilerService? monoDecompiler = null,
        Il2CppDumpScriptService? il2CppService = null,
        GhidraExportService? ghidraExportService = null)
    {
        _monoDecompiler = monoDecompiler ?? new MonoDecompilerService();
        _il2CppService = il2CppService ?? new Il2CppDumpScriptService();
        _ghidraExportService = ghidraExportService ?? new GhidraExportService();
    }

    public Task<ScriptDecompilationResult> ResolveScriptAsync(
        ScriptLookupRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.Run(() => ResolveScript(request, cancellationToken), cancellationToken);
    }

    public ScriptDecompilationResult ResolveScript(
        ScriptLookupRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(request.ClassName))
        {
            return ScriptDecompilationResult.Unavailable(
                request,
                "This asset does not specify a managed script class name.");
        }

        var discovered = ScriptSourceDiscovery.Discover(
            projectRoot: request.ProjectRoot,
            customScriptsDirectory: request.CustomScriptsDirectory,
            knownIl2CppArtifacts: CollectExplicitArtifacts(request),
            gameAssemblyPath: request.GameAssemblyPath,
            loadedAssemblyDirectory: null);

        var managedDirs = request.ManagedDirectories
            .Concat(discovered.ManagedDirectories)
            .Where(static d => !string.IsNullOrWhiteSpace(d) && Directory.Exists(d))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var dummyDirs = request.DummyDllDirectories
            .Concat(discovered.DummyDllDirectories)
            .Where(static d => !string.IsNullOrWhiteSpace(d) && Directory.Exists(d))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var dumpCsPath = FirstExistingFile(request.DumpCsPath, discovered.DumpCsPath);
        var scriptJsonPath = FirstExistingFile(request.ScriptJsonPath, discovered.ScriptJsonPath);

        // 1. Mono managed assemblies first (produces full real C# method bodies via ICSharpCode.Decompiler)
        if (managedDirs.Count > 0)
        {
            var monoResult = _monoDecompiler.TryDecompile(
                request,
                managedDirs,
                treatAsDummyDll: false,
                cancellationToken: cancellationToken);
            if (monoResult != null && monoResult.Success)
            {
                return monoResult;
            }
        }

        // 2. IL2CPP dump.cs (fast streaming byte-offset slice + script.json RVA enrichment)
        if (!string.IsNullOrWhiteSpace(dumpCsPath))
        {
            var dumpResult = _il2CppService.TryExtractFromDumpCs(
                dumpCsPath,
                request,
                scriptJsonPath: scriptJsonPath,
                cancellationToken: cancellationToken);
            if (dumpResult != null && dumpResult.Success)
            {
                return dumpResult;
            }
        }

        // 3. IL2CPP DummyDll decompilation via ICSharpCode.Decompiler (optionally enriched with script.json RVAs)
        if (dummyDirs.Count > 0)
        {
            var dummyResult = _monoDecompiler.TryDecompile(
                request,
                dummyDirs,
                treatAsDummyDll: true,
                cancellationToken: cancellationToken);
            if (dummyResult != null && dummyResult.Success)
            {
                return EnrichDummyResultWithScriptJson(dummyResult, scriptJsonPath, cancellationToken);
            }
        }

        // 4. Fallback: script.json alone (method list + RVAs outline)
        if (!string.IsNullOrWhiteSpace(scriptJsonPath))
        {
            var outlineResult = _il2CppService.TryBuildScriptJsonOutline(
                scriptJsonPath,
                request,
                cancellationToken: cancellationToken);
            if (outlineResult != null && outlineResult.Success)
            {
                return outlineResult;
            }
        }

        var hint = BuildUnavailableHint(request, managedDirs.Count > 0, dummyDirs.Count > 0, !string.IsNullOrWhiteSpace(dumpCsPath), !string.IsNullOrWhiteSpace(scriptJsonPath));
        return ScriptDecompilationResult.Unavailable(request, hint);
    }

    public GhidraExportResult ExportForGhidra(
        GhidraExportOptions options,
        CancellationToken cancellationToken = default)
    {
        return _ghidraExportService.Export(options, cancellationToken);
    }

    public void ClearCaches()
    {
        _monoDecompiler.ClearCache();
        _il2CppService.ClearCache();
    }

    public void Dispose()
    {
        _monoDecompiler.Dispose();
        _il2CppService.ClearCache();
    }

    private ScriptDecompilationResult EnrichDummyResultWithScriptJson(
        ScriptDecompilationResult dummyResult,
        string? scriptJsonPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(scriptJsonPath) || !File.Exists(scriptJsonPath))
        {
            return dummyResult;
        }

        var methods = _il2CppService.QueryScriptJsonMethodsForClass(
            scriptJsonPath,
            dummyResult.Namespace,
            dummyResult.ClassName,
            cancellationToken);
        if (methods.Count == 0)
        {
            return dummyResult;
        }

        var sb = new StringBuilder(dummyResult.SourceCode.Length + 512);
        sb.AppendLine(dummyResult.SourceCode);
        sb.AppendLine();
        sb.AppendLine("// --- Matched script.json RVAs (for Ghidra / Native Analysis) ---");
        foreach (var sym in methods.Take(200))
        {
            var sig = string.IsNullOrWhiteSpace(sym.Signature) ? string.Empty : $" | {sym.Signature}";
            sb.AppendLine($"// 0x{sym.Address:X8}  {sym.FullName}{sig}");
        }

        return new ScriptDecompilationResult
        {
            Success = dummyResult.Success,
            SourceKind = dummyResult.SourceKind,
            BackendBadge = dummyResult.BackendBadge,
            Title = dummyResult.Title,
            AssemblyName = dummyResult.AssemblyName,
            Namespace = dummyResult.Namespace,
            ClassName = dummyResult.ClassName,
            SourceFilePath = dummyResult.SourceFilePath,
            SourceCode = sb.ToString().TrimEnd(),
            SummaryText = $"{dummyResult.SummaryText} • {methods.Count} script.json RVA{(methods.Count == 1 ? string.Empty : "s")}",
            DiagnosticMessage = dummyResult.DiagnosticMessage,
            TypeDefIndex = dummyResult.TypeDefIndex,
            MethodSymbols = methods
        };
    }

    private static List<string> CollectExplicitArtifacts(ScriptLookupRequest request)
    {
        var list = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(request.DumpCsPath)) list.Add(request.DumpCsPath);
        if (!string.IsNullOrWhiteSpace(request.ScriptJsonPath)) list.Add(request.ScriptJsonPath);
        if (!string.IsNullOrWhiteSpace(request.Il2CppHeaderPath)) list.Add(request.Il2CppHeaderPath);
        return list;
    }

    private static string? FirstExistingFile(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }
        return null;
    }

    private static string BuildUnavailableHint(
        ScriptLookupRequest request,
        bool hadManagedDirs,
        bool hadDummyDirs,
        bool hadDumpCs,
        bool hadScriptJson)
    {
        if (hadManagedDirs || hadDummyDirs || hadDumpCs || hadScriptJson)
        {
            var searched = new List<string>();
            if (hadManagedDirs) searched.Add("Managed DLLs");
            if (hadDumpCs) searched.Add("dump.cs");
            if (hadDummyDirs) searched.Add("DummyDll");
            if (hadScriptJson) searched.Add("script.json");

            return $"Type '{request.FullTypeName}' was not found in the available script sources ({string.Join(", ", searched)}). Try choosing another folder via 'Scripts Folder...'.";
        }

        return request.ScriptingBackend switch
        {
            UnityScriptingBackend.Il2Cpp =>
                "IL2CPP project detected, but no dump.cs, script.json, or DummyDll folder was found yet. Click 'Scripts Folder...' to point to your Il2CppDumper output folder.",
            UnityScriptingBackend.Mono =>
                "Mono project detected, but the target assembly was not found. Click 'Scripts Folder...' to select the game's Managed/ folder.",
            _ =>
                "No Managed assemblies or IL2CPP dump artifacts were detected automatically. Click 'Scripts Folder...' to select a Managed/ folder (Mono) or Il2CppDumper output folder (dump.cs / script.json / DummyDll)."
        };
    }
}
