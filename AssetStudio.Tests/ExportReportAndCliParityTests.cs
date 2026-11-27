#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using AssetStudio;
using AssetStudio.Avalonia;
using AssetStudio.Avalonia.Services;
using AssetStudio.CLI;
using Xunit;

namespace AssetStudio.Tests
{
    public sealed class ExportReportAndCliParityTests
    {
        [Fact]
        public void BuildIdentity_StampsCommitAndChannel_WithoutBreakingVersionDisplay()
        {
            var identity = ExportBuildIdentity.Resolve(typeof(AppVersionInfo).Assembly);

            Assert.False(string.IsNullOrWhiteSpace(identity.Version));
            Assert.DoesNotContain("+", identity.Version, StringComparison.Ordinal);
            Assert.True(identity.BuildChannel is "dev" or "release", $"Unexpected channel: {identity.BuildChannel}");
            Assert.False(string.IsNullOrWhiteSpace(identity.DisplayVersion));
            Assert.Equal(identity.DisplayVersion, AppVersionInfo.FullBuildVersion);

            if (identity.BuildChannel == "dev" && !string.IsNullOrEmpty(identity.CommitHash))
            {
                Assert.Contains("-dev", identity.DisplayVersion, StringComparison.Ordinal);
                Assert.Contains(identity.CommitHash, identity.DisplayVersion, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void CliVersion_PrintsIdentifiableVersionAndExitsZero()
        {
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            int exit = CliRunner.Run(new[] { "--version" }, stdout, stderr);

            Assert.Equal(CliRunner.ExitSuccess, exit);
            Assert.Equal(string.Empty, stderr.ToString());
            Assert.StartsWith("AssetStudio.CLI v", stdout.ToString().Trim(), StringComparison.Ordinal);
        }

        [Fact]
        public void ExportReport_ComputeOutcomeStatus_DistinguishesCompletedPartialFailedAndCancelled()
        {
            Assert.Equal(ExportOutcomeStatus.Completed, ExportReport.ComputeOutcomeStatus(5, 0, 0, 0));
            Assert.Equal(ExportOutcomeStatus.Completed, ExportReport.ComputeOutcomeStatus(3, 2, 0, 0));
            Assert.Equal(ExportOutcomeStatus.Partial, ExportReport.ComputeOutcomeStatus(3, 0, 1, 0));
            Assert.Equal(ExportOutcomeStatus.Partial, ExportReport.ComputeOutcomeStatus(3, 0, 0, 1));
            Assert.Equal(ExportOutcomeStatus.Failed, ExportReport.ComputeOutcomeStatus(0, 0, 0, 2));
            Assert.Equal(ExportOutcomeStatus.Failed, ExportReport.ComputeOutcomeStatus(2, 0, 0, 0, hasFatalError: true));
            Assert.Equal(ExportOutcomeStatus.Cancelled, ExportReport.ComputeOutcomeStatus(2, 0, 0, 0, wasCancelled: true));
            Assert.Equal(ExportOutcomeStatus.Cancelled, ExportReport.ComputeOutcomeStatus(2, 0, 0, 1, cancelledCount: 1));
        }

        [Fact]
        public void GuiBulkExportReport_MatchesCountersAndOutcomesOneToOne()
        {
            var partialSummary = new BulkExportSummary(
                Exported: 4,
                Skipped: 2,
                Failed: 1,
                BatchCount: 1,
                Errors: new[] { "Mesh 'Broken' (PathID 99): invalid index buffer" },
                WasCancelled: false,
                ErrorReportPath: "errors.txt");

            Assert.Equal(ExportOutcomeStatus.Partial, partialSummary.OutcomeStatus);
            var report = AssetExportService.BuildBulkExportReport(partialSummary, @"C:\ExportOut", new ExportOptionsState(), elapsedMs: 123);

            Assert.Equal(ExportOutcomeStatus.Partial, report.Status);
            Assert.Equal(4, report.ExportedCount);
            Assert.Equal(2, report.SkippedCount);
            Assert.Equal(1, report.FailedCount);
            Assert.Equal(0, report.CancelledCount);
            Assert.Single(report.Diagnostics);
            Assert.Equal("ExportError", report.Diagnostics[0].Code);
            Assert.Equal(ExportItemOutcome.Failed, report.Diagnostics[0].Outcome);
            Assert.False(string.IsNullOrWhiteSpace(report.ExporterVersion));

            var cancelledSummary = new BulkExportSummary(2, 0, 0, 1, Array.Empty<string>(), WasCancelled: true);
            Assert.Equal(ExportOutcomeStatus.Cancelled, cancelledSummary.OutcomeStatus);
            var cancelledReport = AssetExportService.BuildBulkExportReport(cancelledSummary, @"C:\ExportOut");
            Assert.Equal(ExportOutcomeStatus.Cancelled, cancelledReport.Status);
            Assert.Equal(1, cancelledReport.CancelledCount);
        }

        [Fact]
        public void CliExport_WithReportOption_HandlesDuplicateNamesPathTraversalAndWritesStructuredReport()
        {
            var tempRoot = CreateTempDir("cli_report_clean");
            try
            {
                var input = Path.Combine(tempRoot, "level0");
                var outDir = Path.Combine(tempRoot, "out");
                var reportPath = Path.Combine(tempRoot, "reports", "export-report.json");

                File.WriteAllBytes(input, SyntheticSerializedFile.BuildTextAssets(new (long, string, string)[]
                {
                    (10, "duplicate", "first"),
                    (11, "duplicate", "second"),
                    (12, "../../escape", "safe")
                }, out _));

                var (exit, stdout, stderr) = RunCli("export", input, "--output", outDir, "--type", "TextAsset", "--report", reportPath);

                Assert.True(exit == CliRunner.ExitSuccess, stderr);
                Assert.Contains("Export complete: 3 exported, 0 skipped.", stdout, StringComparison.Ordinal);

                // Verify duplicate disambiguation and strict output confinement
                var textDir = Path.Combine(outDir, "TextAsset");
                Assert.Equal("first", File.ReadAllText(Path.Combine(textDir, "duplicate.txt")));
                Assert.Equal("second", File.ReadAllText(Path.Combine(textDir, "duplicate_1.txt")));
                var allExported = Directory.GetFiles(outDir, "*", SearchOption.AllDirectories);
                Assert.Equal(3, allExported.Length);
                var outPrefix = Path.GetFullPath(outDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                Assert.All(allExported, p => Assert.StartsWith(outPrefix, Path.GetFullPath(p), StringComparison.OrdinalIgnoreCase));

                // Verify structured ExportReport JSON
                Assert.True(File.Exists(reportPath));
                using var doc = JsonDocument.Parse(File.ReadAllText(reportPath));
                var root = doc.RootElement;
                Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
                Assert.Equal("CliExport", root.GetProperty("operation").GetString());
                Assert.Equal("Completed", root.GetProperty("status").GetString());
                Assert.Equal(3, root.GetProperty("exportedCount").GetInt32());
                Assert.Equal(0, root.GetProperty("skippedCount").GetInt32());
                Assert.Equal(0, root.GetProperty("failedCount").GetInt32());
                Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("exporterVersion").GetString()));
                Assert.Equal("TextAsset", root.GetProperty("options").GetProperty("types").GetString());
            }
            finally
            {
                TryDelete(tempRoot);
            }
        }

        [Fact]
        public void CliExport_PartialFailure_ContinuesRemainingAssetsAndMarksReportPartial()
        {
            var tempRoot = CreateTempDir("cli_report_partial");
            try
            {
                var input = Path.Combine(tempRoot, "level0");
                var outDir = Path.Combine(tempRoot, "out");
                var reportPath = Path.Combine(tempRoot, "partial-report.json");

                File.WriteAllBytes(input, SyntheticSerializedFile.BuildTextAssets(new (long, string, string)[]
                {
                    (1, "blocked", "cannot overwrite directory"),
                    (2, "healthy", "survived partial failure")
                }, out _));

                // Pre-create a directory at the exact target file path of "blocked.txt" so writing it fails
                // with UnauthorizedAccessException / IOException while "healthy.txt" still succeeds!
                Directory.CreateDirectory(Path.Combine(outDir, "TextAsset", "blocked.txt"));

                var (exit, stdout, stderr) = RunCli("export", input, "--output", outDir, "--type", "TextAsset", "--report", reportPath);

                Assert.Equal(CliRunner.ExitPartialFailure, exit);
                Assert.Contains("Export complete: 1 exported, 0 skipped, 1 failed.", stdout, StringComparison.Ordinal);
                Assert.Contains("Failed: TextAsset 1 (blocked):", stderr, StringComparison.Ordinal);
                Assert.Equal("survived partial failure", File.ReadAllText(Path.Combine(outDir, "TextAsset", "healthy.txt")));

                Assert.True(File.Exists(reportPath));
                using var doc = JsonDocument.Parse(File.ReadAllText(reportPath));
                var root = doc.RootElement;
                Assert.Equal("Partial", root.GetProperty("status").GetString());
                Assert.Equal(1, root.GetProperty("exportedCount").GetInt32());
                Assert.Equal(0, root.GetProperty("skippedCount").GetInt32());
                Assert.Equal(1, root.GetProperty("failedCount").GetInt32());

                var diags = root.GetProperty("diagnostics");
                Assert.Equal(1, diags.GetArrayLength());
                Assert.Equal("Failed", diags[0].GetProperty("outcome").GetString());
                Assert.Equal("ExportError", diags[0].GetProperty("code").GetString());
                Assert.Equal("blocked", diags[0].GetProperty("assetName").GetString());
                Assert.Equal(1L, diags[0].GetProperty("pathId").GetInt64());
            }
            finally
            {
                TryDelete(tempRoot);
            }
        }

        [Fact]
        public void CliExport_TotalFailure_ExitsRuntimeErrorAndMarksReportFailed()
        {
            var tempRoot = CreateTempDir("cli_report_total_fail");
            try
            {
                var input = Path.Combine(tempRoot, "level0");
                var outDir = Path.Combine(tempRoot, "out");
                var reportPath = Path.Combine(tempRoot, "failed-report.json");

                File.WriteAllBytes(input, SyntheticSerializedFile.BuildTextAssets(new (long, string, string)[]
                {
                    (1, "blocked_only", "cannot write")
                }, out _));

                Directory.CreateDirectory(Path.Combine(outDir, "TextAsset", "blocked_only.txt"));

                var (exit, _, stderr) = RunCli("export", input, "--output", outDir, "--type", "TextAsset", "--report", reportPath);

                Assert.Equal(CliRunner.ExitRuntimeError, exit);
                Assert.Contains("Failed: TextAsset 1 (blocked_only):", stderr, StringComparison.Ordinal);
                using var doc = JsonDocument.Parse(File.ReadAllText(reportPath));
                Assert.Equal("Failed", doc.RootElement.GetProperty("status").GetString());
                Assert.Equal(0, doc.RootElement.GetProperty("exportedCount").GetInt32());
                Assert.Equal(1, doc.RootElement.GetProperty("failedCount").GetInt32());
            }
            finally
            {
                TryDelete(tempRoot);
            }
        }

        [Fact]
        public void CliExport_JsonFormat_KeepsStdoutStrictlyStructuredJsonAndRoutesLogsToStderr()
        {
            var tempRoot = CreateTempDir("cli_json_stdout");
            try
            {
                var input = Path.Combine(tempRoot, "level0");
                var outDir = Path.Combine(tempRoot, "out");

                File.WriteAllBytes(input, SyntheticSerializedFile.BuildTextAssets(new (long, string, string)[]
                {
                    (1, "blocked", "fails"),
                    (2, "ok", "succeeds")
                }, out _));

                Directory.CreateDirectory(Path.Combine(outDir, "TextAsset", "blocked.txt"));

                var (exit, stdout, stderr) = RunCli("export", input, "--output", outDir, "--type", "TextAsset", "--format", "json");

                Assert.Equal(CliRunner.ExitPartialFailure, exit);
                Assert.Contains("Failed: TextAsset 1 (blocked):", stderr, StringComparison.Ordinal);
                Assert.DoesNotContain("Exported:", stdout, StringComparison.Ordinal);

                // stdout must parse cleanly as a single valid ExportReport JSON object
                using var doc = JsonDocument.Parse(stdout);
                var root = doc.RootElement;
                Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
                Assert.Equal("Partial", root.GetProperty("status").GetString());
                Assert.Equal(1, root.GetProperty("exportedCount").GetInt32());
                Assert.Equal(1, root.GetProperty("failedCount").GetInt32());
            }
            finally
            {
                TryDelete(tempRoot);
            }
        }

        [Fact]
        public void CliExport_CancelledToken_WritesCancelledReportAndExits130()
        {
            var tempRoot = CreateTempDir("cli_cancelled");
            try
            {
                var input = Path.Combine(tempRoot, "level0");
                var outDir = Path.Combine(tempRoot, "out");
                var reportPath = Path.Combine(tempRoot, "cancelled-report.json");

                File.WriteAllBytes(input, SyntheticSerializedFile.BuildTextAssets(new (long, string, string)[]
                {
                    (1, "alpha", "hello"),
                    (2, "beta", "world")
                }, out _));

                using var cts = new System.Threading.CancellationTokenSource();
                cts.Cancel();

                using var stdout = new StringWriter();
                using var stderr = new StringWriter();
                int exit = CliRunner.Run(
                    new[] { "export", input, "--output", outDir, "--type", "TextAsset", "--format", "json", "--report", reportPath },
                    stdout,
                    stderr,
                    cts.Token);

                Assert.Equal(CliRunner.ExitCancelled, exit);
                Assert.Contains("cancelled", stderr.ToString(), StringComparison.OrdinalIgnoreCase);

                using var stdoutDoc = JsonDocument.Parse(stdout.ToString());
                Assert.Equal("Cancelled", stdoutDoc.RootElement.GetProperty("status").GetString());
                Assert.True(stdoutDoc.RootElement.GetProperty("cancelledCount").GetInt32() >= 1);

                Assert.True(File.Exists(reportPath));
                using var fileDoc = JsonDocument.Parse(File.ReadAllText(reportPath));
                Assert.Equal("Cancelled", fileDoc.RootElement.GetProperty("status").GetString());
            }
            finally
            {
                TryDelete(tempRoot);
            }
        }

        [Fact]
        public void ShaderPreviewDiagnostics_ClassifiesAllFallbackModesAndUpdatesPreviewSession()
        {
            var d3d11 = ShaderConversionClassifier.ClassifyPreview(
                "Standard", new[] { 2020, 3, 40 }, isWindows: true, d3d11Rendered: true, hasPreviewTexture: false, isMaterialPreview: true);
            Assert.Equal(ShaderPreviewRenderMode.InterpretedD3D11Shader, d3d11.RenderMode);
            Assert.Equal("D3D11 Shader", d3d11.BadgeLabel);

            var pbrApprox = ShaderConversionClassifier.ClassifyPreview(
                "Universal Render Pipeline/Lit", new[] { 6000, 3, 18 }, isWindows: true, d3d11Rendered: false, hasPreviewTexture: true, isMaterialPreview: true);
            Assert.Equal(ShaderPreviewRenderMode.MaterialPbrApproximation, pbrApprox.RenderMode);
            Assert.Equal("PBR Approximation", pbrApprox.BadgeLabel);
            Assert.Equal("URP / LWRP", pbrApprox.ShaderFamily);
            Assert.Contains("glTF", pbrApprox.ExportAlignmentNote, StringComparison.OrdinalIgnoreCase);

            var basicFallback = ShaderConversionClassifier.ClassifyPreview(
                "HDRP/Lit", new[] { 2022, 3, 10 }, isWindows: false, d3d11Rendered: false, hasPreviewTexture: false, isMaterialPreview: true);
            Assert.Equal(ShaderPreviewRenderMode.BasicFallback, basicFallback.RenderMode);
            Assert.Equal("Basic Fallback", basicFallback.BadgeLabel);
            Assert.Contains("Windows", basicFallback.Reason, StringComparison.OrdinalIgnoreCase);

            var shaderText = ShaderConversionClassifier.ClassifyPreview(
                "Custom/StylizedToon", new[] { 2020, 3, 1 }, isWindows: true, d3d11Rendered: false, hasPreviewTexture: false, isMaterialPreview: false,
                d3d11FailureReason: "No D3D11 pass found.");
            Assert.Equal(ShaderPreviewRenderMode.ShaderLabText, shaderText.RenderMode);
            Assert.Equal("ShaderLab Text", shaderText.BadgeLabel);
            Assert.Contains("No D3D11 pass found.", shaderText.Reason, StringComparison.Ordinal);

            // Verify PreviewSessionViewModel surfaces and clears the shader mode badge cleanly (Pure-MVVM)
            using var session = new AssetStudio.Avalonia.ViewModels.PreviewSessionViewModel();
            Assert.False(session.IsShaderPreviewModeBadgeVisible);

            session.UpdateShaderPreviewDiagnostic(pbrApprox);
            Assert.True(session.IsShaderPreviewModeBadgeVisible);
            Assert.Equal("PBR Approximation", session.ShaderPreviewModeBadgeText);
            Assert.Contains("URP / LWRP", session.ShaderPreviewModeTooltip, StringComparison.Ordinal);

            session.ClearShaderPreviewDiagnostic();
            Assert.False(session.IsShaderPreviewModeBadgeVisible);
            Assert.Equal(string.Empty, session.ShaderPreviewModeBadgeText);
        }

        private static (int Exit, string Stdout, string Stderr) RunCli(params string[] args)
        {
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            int exit = CliRunner.Run(args, stdout, stderr);
            return (exit, stdout.ToString(), stderr.ToString());
        }

        private static string CreateTempDir(string prefix)
        {
            var path = Path.Combine(Path.GetTempPath(), $"AssetStudio_{prefix}_{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return path;
        }

        private static void TryDelete(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
        }
    }
}
