# Reproducible Synthetic Benchmarks

AssetStudio includes a self-contained, 100% synthetic benchmark suite designed to track indexing, cache reopen, preview extraction, batch export throughput, memory footprint, and cache disk usage across commits and machines without requiring any proprietary or external game assets.

## Design Goals

- **100% synthetic and portable:** Every Unity serialized file (Format 22) and test mesh is generated deterministically in temporary storage at runtime and removed automatically after the run.
- **Cold vs. warm cache comparison:** Each iteration explicitly clears the temporary cache before cold indexing and reopens from disk with fresh SQLite and `ProjectIndex` instances to measure genuine warm-start performance.
- **Statistical dispersion:** Every stage runs configurable warm-up rounds followed by multiple measured iterations, reporting **median**, **min**, **p95**, **max**, and **interquartile range (IQR)**.
- **Strict output integrity verification:** Every batch export iteration parses the structured CLI `ExportReport` JSON and verifies exact exported file counts, zero failures, and **byte-exact equality** for every exported asset.

## What Is Measured

| Stage | Description |
| --- | --- |
| **Cold scan + index + cache write** | `ProjectScanner.ScanFolder` + lazy `AssetsManager.LoadFolder` + `SQLiteProjectIndexCache.SaveIndexCache` (SQLite database + verified mmap `.idx` sidecar). |
| **Warm reopen (scan + cache load)** | Fresh `ProjectScanner.ScanFolder` + deterministic folder signature + `SQLiteProjectIndexCache.LoadIndexCacheIntoProjectIndex` using a fresh cache instance and empty `ProjectIndex`. |
| **Warm cache load only** | Isolated timing of `LoadIndexCacheIntoProjectIndex` from the warm on-disk SQLite and `.idx` sidecar cache. |
| **First preview geometry extraction** | Synthetic mesh geometry extraction via `MeshPreviewControl.BuildPreviewGeometrySnapshot` plus binary `MeshPreviewGeometryCacheSerializer` round-trip validation. |
| **Batch export (CLI `export --report`)** | End-to-end `AssetStudio.CLI export` over the synthetic dataset with `--report`, followed by byte-exact validation of every exported file. |
| **Memory & disk usage** | Process peak working set, managed heap delta across warm open, and exact disk breakdown (SQLite `.db` + WAL/SHM, `.idx` sidecar, total cache bytes, exported bytes) via `CacheObservabilityService`. |

## How to Run

### Using the PowerShell script (Windows, Linux, macOS with `pwsh`)

```powershell
pwsh ./scripts/run-benchmarks.ps1
```

Custom dataset scale and iteration counts:

```powershell
pwsh ./scripts/run-benchmarks.ps1 `
    -Configuration Release `
    -Files 16 `
    -AssetsPerFile 50 `
    -MeshCount 16 `
    -WarmupIterations 2 `
    -Iterations 7 `
    -OutputDir ./benchmark-results
```

If the test assembly is already built in `Release` mode, pass `-NoBuild` to skip compilation:

```powershell
pwsh ./scripts/run-benchmarks.ps1 -NoBuild
```

### Using `dotnet test` directly

```bash
dotnet test AssetStudio.Tests/AssetStudio.Tests.csproj -c Release \
  --filter "FullyQualifiedName~SyntheticBenchmarkHarness_RunsEndToEndAndVerifiesIntegrity"
```

Optional environment variables supported by the harness:

| Environment Variable | Default (test) | Script Default | Description |
| --- | ---: | ---: | --- |
| `ASSETSTUDIO_BENCHMARK_FILES` | `4` | `12` | Number of synthetic Unity serialized files generated. |
| `ASSETSTUDIO_BENCHMARK_ASSETS_PER_FILE` | `10` | `40` | Number of `TextAsset` entries per serialized file. |
| `ASSETSTUDIO_BENCHMARK_PAYLOAD_BYTES` | `256` | `256` | Byte length of each synthetic `TextAsset` payload. |
| `ASSETSTUDIO_BENCHMARK_MESHES` | `4` | `12` | Number of procedural grid meshes benchmarked for preview extraction. |
| `ASSETSTUDIO_BENCHMARK_MESH_SUBDIV` | `12` | `16` | Grid subdivisions per axis (`(N+1)^2` vertices, `2*N^2` triangles). |
| `ASSETSTUDIO_BENCHMARK_WARMUP` | `1` | `1` | Warm-up iterations excluded from summary statistics. |
| `ASSETSTUDIO_BENCHMARK_ITERATIONS` | `3` | `5` | Measured iterations included in median / p95 / IQR stats. |
| `ASSETSTUDIO_BENCHMARK_OUTPUT_DIR` | *(unset)* | `./benchmark-results` | When set, writes `benchmark-report.json` and `benchmark-summary.md`. |

## Output Artifacts

When an output directory is configured, each run produces two files:

1. **`benchmark-report.json`** — Machine-readable report containing:
   - `environment`: OS description, CPU architecture, logical processor count, .NET runtime, and build identity (`appVersion`, `displayVersion`, `buildChannel`, `commitHash` via `ExportBuildIdentity`).
   - `dataset`: Exact synthetic file, asset, mesh, vertex, triangle, and iteration specifications.
   - `coldScanAndIndex`, `warmReopenEndToEnd`, `warmCacheLoadOnly`, `firstPreviewGeometry`, `batchExportCli`: Latency metrics (`minMs`, `medianMs`, `meanMs`, `p95Ms`, `maxMs`, `iqrMs`, and raw `samplesMs`).
   - `coldToWarmSpeedup`: Ratio of cold median time to warm reopen median time.
   - `resources`: Peak working set bytes, managed heap warm-open delta, SQLite DB bytes, `.idx` sidecar bytes, total cache bytes, and exported output bytes.
   - `integrity`: Verification pass/fail flag, indexed asset counts, preview mesh verification count, exported file count, byte-exact verified file count, and failed export count.
2. **`benchmark-summary.md`** — Ready-to-share Markdown summary tables for release notes, pull requests, and regression comparisons.

## Comparing Runs Across Commits

1. Run `scripts/run-benchmarks.ps1` on a baseline commit with `-OutputDir ./bench-base` using `Release` configuration and identical `-Files`, `-AssetsPerFile`, and `-Iterations` values.
2. Run again on your candidate branch with `-OutputDir ./bench-head`.
3. Compare **median** and **p95** latencies together with **IQR** (to confirm changes exceed run-to-run noise), plus `resources.managedHeapDeltaBytes`, `resources.totalCacheBytes`, and `integrity.integrityVerified == true`.
