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

                Assert.Equal(CliRunner.ExitSuccess, exit);
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
