<#
.SYNOPSIS
    Runs AssetStudio's 100% synthetic, portable benchmark suite and emits JSON + Markdown reports.
.DESCRIPTION
    Generates deterministic Unity serialized files and synthetic meshes in temporary storage (requiring zero
    external or proprietary assets), executes warm-up and measured iterations for cold indexing, warm cache reopen,
    first preview geometry extraction, and CLI batch export with byte-exact output integrity verification, and
    writes benchmark-report.json and benchmark-summary.md to the output directory.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [Parameter(Mandatory = $false)]
    [ValidateRange(1, 128)]
    [int]$Files = 12,

    [Parameter(Mandatory = $false)]
    [ValidateRange(1, 500)]
    [int]$AssetsPerFile = 40,

    [Parameter(Mandatory = $false)]
    [ValidateRange(32, 65536)]
    [int]$PayloadBytes = 256,

    [Parameter(Mandatory = $false)]
    [ValidateRange(1, 128)]
    [int]$MeshCount = 12,

    [Parameter(Mandatory = $false)]
    [ValidateRange(2, 64)]
    [int]$MeshSubdivisions = 16,

    [Parameter(Mandatory = $false)]
    [ValidateRange(0, 10)]
    [int]$WarmupIterations = 1,

    [Parameter(Mandatory = $false)]
    [ValidateRange(1, 50)]
    [int]$Iterations = 5,

    [Parameter(Mandatory = $false)]
    [string]$OutputDir = "",

    [Parameter(Mandatory = $false)]
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$testProject = Join-Path $repoRoot "AssetStudio.Tests/AssetStudio.Tests.csproj"

if (-not (Test-Path -LiteralPath $testProject)) {
    throw "Test project not found at '$testProject'."
}

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $repoRoot "benchmark-results"
}
else {
    $OutputDir = [System.IO.Path]::GetFullPath($OutputDir)
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$envOverrides = @{
    "ASSETSTUDIO_BENCHMARK_OUTPUT_DIR"      = $OutputDir
    "ASSETSTUDIO_BENCHMARK_FILES"           = $Files.ToString([System.Globalization.CultureInfo]::InvariantCulture)
    "ASSETSTUDIO_BENCHMARK_ASSETS_PER_FILE" = $AssetsPerFile.ToString([System.Globalization.CultureInfo]::InvariantCulture)
    "ASSETSTUDIO_BENCHMARK_PAYLOAD_BYTES"   = $PayloadBytes.ToString([System.Globalization.CultureInfo]::InvariantCulture)
    "ASSETSTUDIO_BENCHMARK_MESHES"          = $MeshCount.ToString([System.Globalization.CultureInfo]::InvariantCulture)
    "ASSETSTUDIO_BENCHMARK_MESH_SUBDIV"     = $MeshSubdivisions.ToString([System.Globalization.CultureInfo]::InvariantCulture)
    "ASSETSTUDIO_BENCHMARK_WARMUP"          = $WarmupIterations.ToString([System.Globalization.CultureInfo]::InvariantCulture)
    "ASSETSTUDIO_BENCHMARK_ITERATIONS"      = $Iterations.ToString([System.Globalization.CultureInfo]::InvariantCulture)
}

$previousEnv = @{}
foreach ($key in $envOverrides.Keys) {
    $previousEnv[$key] = [System.Environment]::GetEnvironmentVariable($key)
    [System.Environment]::SetEnvironmentVariable($key, $envOverrides[$key])
}

try {
    $dotnetArgs = @(
        "test",
        $testProject,
        "-c", $Configuration,
        "--filter", "FullyQualifiedName~SyntheticBenchmarkHarness_RunsEndToEndAndVerifiesIntegrity",
        "--logger", "console;verbosity=normal"
    )
    if ($NoBuild) {
        $dotnetArgs += "--no-build"
    }

    Write-Host "[bench] Running AssetStudio synthetic benchmark suite ($Files files x $AssetsPerFile assets, $MeshCount meshes, $WarmupIterations warmup + $Iterations iterations)..."
    & dotnet @dotnetArgs
    if ($LASTEXITCODE -ne 0) {
        throw "Synthetic benchmark execution failed with exit code $LASTEXITCODE."
    }

    $jsonPath = Join-Path $OutputDir "benchmark-report.json"
    $mdPath = Join-Path $OutputDir "benchmark-summary.md"

    if (-not (Test-Path -LiteralPath $jsonPath)) {
        throw "Expected JSON benchmark report was not written: '$jsonPath'."
    }

    $report = Get-Content -LiteralPath $jsonPath -Raw | ConvertFrom-Json
    if (-not $report.integrity.integrityVerified -or $report.integrity.failedExports -ne 0) {
        throw "Benchmark integrity verification failed: $($report.integrity.failureReason)"
    }

    if (Test-Path -LiteralPath $mdPath) {
        Write-Host ""
        Get-Content -LiteralPath $mdPath -Raw | Write-Host
    }

    Write-Host "[bench] Reports written to:"
    Write-Host "  JSON:     $jsonPath"
    Write-Host "  Markdown: $mdPath"
}
finally {
    foreach ($key in $previousEnv.Keys) {
        [System.Environment]::SetEnvironmentVariable($key, $previousEnv[$key])
    }
}
