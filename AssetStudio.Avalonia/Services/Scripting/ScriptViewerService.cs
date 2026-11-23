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
    private readonly Il2CppMetadataReader _metadataReader;
    private readonly GhidraExportService _ghidraExportService;

    public ScriptViewerService(
        MonoDecompilerService? monoDecompiler = null,
        Il2CppDumpScriptService? il2CppService = null,
        GhidraExportService? ghidraExportService = null,
        Il2CppMetadataReader? metadataReader = null)
    {
        _monoDecompiler = monoDecompiler ?? new MonoDecompilerService();
        _il2CppService = il2CppService ?? new Il2CppDumpScriptService();
        _metadataReader = metadataReader ?? new Il2CppMetadataReader();
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
            loadedAssemblyDirectory: null,
            globalMetadataPath: request.GlobalMetadataPath,
            projectAnalysisCacheDirectory: request.ProjectAnalysisCacheDirectory);

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
        var globalMetadataPath = FirstExistingFile(request.GlobalMetadataPath, discovered.GlobalMetadataPath);

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
                return EnrichWithScriptJson(dummyResult, scriptJsonPath, cancellationToken);
            }
        }

        // 4. Built-in pure-C# IL2CPP global-metadata.dat reader (automatic, zero external tool required)
        string? metadataDiagnostic = null;
        if (!string.IsNullOrWhiteSpace(globalMetadataPath))
        {
            var metadataResult = _metadataReader.TryExtractType(
                globalMetadataPath,
                request,
                out metadataDiagnostic,
                cancellationToken);
            if (metadataResult != null && metadataResult.Success)
            {
                return EnrichWithScriptJson(metadataResult, scriptJsonPath, cancellationToken);
            }
        }

        // 5. Fallback: script.json alone (method list + RVAs outline)
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

        var hint = BuildUnavailableHint(
            request,
            managedDirs.Count > 0,
            dummyDirs.Count > 0,
            !string.IsNullOrWhiteSpace(dumpCsPath),
            !string.IsNullOrWhiteSpace(globalMetadataPath),
            !string.IsNullOrWhiteSpace(scriptJsonPath),
            metadataDiagnostic);
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
        _metadataReader.ClearCache();
    }

    public void Dispose()
    {
        _monoDecompiler.Dispose();
        _il2CppService.ClearCache();
        _metadataReader.ClearCache();
    }

    private ScriptDecompilationResult EnrichWithScriptJson(
        ScriptDecompilationResult baseResult,
        string? scriptJsonPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(scriptJsonPath) || !File.Exists(scriptJsonPath))
        {
            return baseResult;
        }

        var methods = _il2CppService.QueryScriptJsonMethodsForClass(
            scriptJsonPath,
            baseResult.Namespace,
            baseResult.ClassName,
            cancellationToken);
        if (methods.Count == 0)
        {
            return baseResult;
        }

        var sb = new StringBuilder(baseResult.SourceCode.Length + 512);
        sb.AppendLine(baseResult.SourceCode);
        sb.AppendLine();
        sb.AppendLine("// --- Matched script.json RVAs (for Ghidra / Native Analysis) ---");
        foreach (var sym in methods.Take(200))
        {
            var sig = string.IsNullOrWhiteSpace(sym.Signature) ? string.Empty : $" | {sym.Signature}";
            sb.AppendLine($"// 0x{sym.Address:X8}  {sym.FullName}{sig}");
        }

        return new ScriptDecompilationResult
        {
            Success = baseResult.Success,
            SourceKind = baseResult.SourceKind,
            BackendBadge = baseResult.BackendBadge,
            Title = baseResult.Title,
            AssemblyName = baseResult.AssemblyName,
            Namespace = baseResult.Namespace,
            ClassName = baseResult.ClassName,
            SourceFilePath = baseResult.SourceFilePath,
            SourceCode = sb.ToString().TrimEnd(),
            SummaryText = $"{baseResult.SummaryText} • {methods.Count} script.json RVA{(methods.Count == 1 ? string.Empty : "s")}",
            DiagnosticMessage = baseResult.DiagnosticMessage,
            TypeDefIndex = baseResult.TypeDefIndex,
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
        bool hadGlobalMetadata,
        bool hadScriptJson,
        string? metadataDiagnostic)
    {
        if (!string.IsNullOrWhiteSpace(metadataDiagnostic) && !hadManagedDirs && !hadDummyDirs && !hadDumpCs && !hadScriptJson)
        {
            return $"{metadataDiagnostic} Click 'Scripts Folder...' to select a decrypted Il2CppDumper output folder (dump.cs / script.json / DummyDll).";
        }

        if (hadManagedDirs || hadDummyDirs || hadDumpCs || hadGlobalMetadata || hadScriptJson)
        {
            var searched = new List<string>();
            if (hadManagedDirs) searched.Add("Managed DLLs");
            if (hadDumpCs) searched.Add("dump.cs");
            if (hadDummyDirs) searched.Add("DummyDll");
            if (hadGlobalMetadata) searched.Add("global-metadata.dat");
            if (hadScriptJson) searched.Add("script.json");

            var extra = !string.IsNullOrWhiteSpace(metadataDiagnostic) ? $" ({metadataDiagnostic})" : string.Empty;
            return $"Type '{request.FullTypeName}' was not found in available sources ({string.Join(", ", searched)}){extra}. Use 'Scripts Folder...' to choose another folder.";
        }

        return request.ScriptingBackend switch
        {
            UnityScriptingBackend.Il2Cpp =>
                "IL2CPP project detected, but no global-metadata.dat, dump.cs, script.json, or DummyDll folder was found. Click 'Scripts Folder...' to select an Il2CppDumper output folder.",
            UnityScriptingBackend.Mono =>
                "Mono project detected, but the target assembly was not found. Click 'Scripts Folder...' to select the game's Managed/ folder.",
            _ =>
                "No Managed assemblies, global-metadata.dat, or IL2CPP dump artifacts were detected. Click 'Scripts Folder...' to select a Managed/ or Il2CppDumper folder."
        };
    }
}
