<#
.SYNOPSIS
    End-to-end smoke test for a packaged AssetStudio.CLI binary on Windows.
.DESCRIPTION
    Creates a tiny synthetic Unity serialized file (Format 22) in a temporary directory and
    verifies --help, --version, scan, list, export --report, structured JSON output, and exit codes.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$CliExecutable,

    [Parameter(Mandatory = $false)]
    [string]$ExpectedVersion = ""
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $CliExecutable)) {
    throw "CLI executable not found: '$CliExecutable'."
}

function Write-AlignedString([System.IO.BinaryWriter]$bw, [System.IO.MemoryStream]$ms, [string]$text) {
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($text)
    $bw.Write([int]$bytes.Length)
    $bw.Write($bytes)
    while (($ms.Position % 4) -ne 0) {
        $bw.Write([byte]0)
    }
}

function New-SyntheticTextAssetSerializedFile([hashtable[]]$assets) {
    $headerSize = 48
    $blobs = New-Object System.Collections.Generic.List[byte[]]
    foreach ($asset in $assets) {
        $bms = New-Object System.IO.MemoryStream
        $bbw = New-Object System.IO.BinaryWriter($bms, [System.Text.Encoding]::UTF8)
        Write-AlignedString $bbw $bms $asset.Name
        Write-AlignedString $bbw $bms $asset.Script
        $bbw.Flush()
        $blobs.Add($bms.ToArray())
        $bbw.Dispose()
        $bms.Dispose()
    }

    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms, [System.Text.Encoding]::UTF8)
    $bw.Write((New-Object byte[] $headerSize))
    $bw.Write([System.Text.Encoding]::ASCII.GetBytes("2022.3.20f1`0"))
    $bw.Write([int]19) # StandaloneWindows64
    $bw.Write([bool]$false) # no TypeTrees
    $bw.Write([int]1) # type count
    $bw.Write([int]49) # TextAsset
    $bw.Write([bool]$false) # stripped
    $bw.Write([int16]-1) # script type index
    $bw.Write((New-Object byte[] 16)) # old type hash

    $bw.Write([int]$blobs.Count)
    [long]$start = 0
    for ($i = 0; $i -lt $blobs.Count; $i++) {
        while (($ms.Position % 4) -ne 0) { $bw.Write([byte]0) }
        $bw.Write([long]$assets[$i].PathId)
        $bw.Write([long]$start)
        $bw.Write([uint32]$blobs[$i].Length)
        $bw.Write([int]0)
        $start += ($blobs[$i].Length + 7) -band (-bnot 7)
    }

    $bw.Write([int]0) # script types
    $bw.Write([int]0) # externals
    $bw.Write([int]0) # ref types
    $bw.Write([byte]0) # user information

    [uint32]$metadataSize = [uint32]($ms.Position - $headerSize)
    while (($ms.Position % 16) -ne 0) { $bw.Write([byte]0) }
    [long]$dataOffset = $ms.Position
    foreach ($blob in $blobs) {
        $bw.Write($blob)
        while ((($ms.Position - $dataOffset) % 8) -ne 0) { $bw.Write([byte]0) }
    }

    $bw.Flush()
    $bytes = $ms.ToArray()
    $bw.Dispose()
    $ms.Dispose()

    function Write-UInt32BE([byte[]]$buf, [int]$offset, [uint32]$val) {
        $buf[$offset + 0] = [byte](($val -shr 24) -band 0xFF)
        $buf[$offset + 1] = [byte](($val -shr 16) -band 0xFF)
        $buf[$offset + 2] = [byte](($val -shr 8) -band 0xFF)
        $buf[$offset + 3] = [byte]($val -band 0xFF)
    }
    function Write-Int64BE([byte[]]$buf, [int]$offset, [long]$val) {
        for ($b = 0; $b -lt 8; $b++) {
            $shift = 56 - ($b * 8)
            $buf[$offset + $b] = [byte](($val -shr $shift) -band 0xFF)
        }
    }

    Write-UInt32BE $bytes 8 22
    Write-UInt32BE $bytes 20 $metadataSize
    Write-Int64BE  $bytes 24 ([long]$bytes.Length)
    Write-Int64BE  $bytes 32 $dataOffset
    return ,$bytes
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("AssetStudio_CliSmoke_" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null

try {
    # 1. --help
    Write-Host "[smoke] Checking --help..."
    $helpOut = & $CliExecutable --help
    if ($LASTEXITCODE -ne 0) { throw "--help failed with exit code $LASTEXITCODE." }
    if (($helpOut -join "`n") -notmatch "AssetStudio\.CLI") { throw "--help output did not contain AssetStudio.CLI banner." }

    # 2. --version
    Write-Host "[smoke] Checking --version..."
    $verOut = (& $CliExecutable --version | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw "--version failed with exit code $LASTEXITCODE." }
    if (-not $verOut.StartsWith("AssetStudio.CLI v")) { throw "Unexpected --version output: '$verOut'." }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedVersion) -and -not $verOut.Contains($ExpectedVersion)) {
        throw "--version output '$verOut' did not contain expected version '$ExpectedVersion'."
    }
    Write-Host "[smoke] Version verified: $verOut"

    # Build synthetic serialized file
    $fixtureDir = Join-Path $tempRoot "fixture"
    New-Item -ItemType Directory -Force -Path $fixtureDir | Out-Null
    $fixturePath = Join-Path $fixtureDir "level0"
    $fixtureBytes = New-SyntheticTextAssetSerializedFile @(
        @{ PathId = [long]101; Name = "greeting"; Script = "hello-from-cli-smoke" },
        @{ PathId = [long]102; Name = "config";   Script = "mode=release" }
    )
    [System.IO.File]::WriteAllBytes($fixturePath, $fixtureBytes)

    # 3. scan --format json
    Write-Host "[smoke] Checking scan --format json..."
    $scanJsonRaw = (& $CliExecutable scan --no-local-profiles $fixtureDir --format json | Out-String)
    if ($LASTEXITCODE -ne 0) { throw "scan failed with exit code $LASTEXITCODE." }
    $scanJson = $scanJsonRaw | ConvertFrom-Json
    if ($scanJson.serializedFileCount -ne 1) { throw "Expected serializedFileCount=1, got $($scanJson.serializedFileCount)." }

    # 4. list --format json
    Write-Host "[smoke] Checking list --format json..."
    $listJsonRaw = (& $CliExecutable list --no-local-profiles --no-decompression-cache $fixturePath --format json | Out-String)
    if ($LASTEXITCODE -ne 0) { throw "list failed with exit code $LASTEXITCODE." }
    $listJson = $listJsonRaw | ConvertFrom-Json
    if ($listJson.count -ne 2) { throw "Expected list count=2, got $($listJson.count)." }

    # 5. export --type TextAsset --report <json>
    Write-Host "[smoke] Checking export --type TextAsset --report..."
    $outDir = Join-Path $tempRoot "out"
    $reportPath = Join-Path $tempRoot "report.json"
    $exportOut = (& $CliExecutable export --no-local-profiles --no-decompression-cache $fixturePath --output $outDir --type TextAsset --report $reportPath | Out-String)
    if ($LASTEXITCODE -ne 0) { throw "export failed with exit code $LASTEXITCODE. Output: $exportOut" }

    $greetingFile = Join-Path $outDir "TextAsset\greeting.txt"
    $configFile = Join-Path $outDir "TextAsset\config.txt"
    if (-not (Test-Path -LiteralPath $greetingFile)) { throw "Expected exported file missing: $greetingFile" }
    if (-not (Test-Path -LiteralPath $configFile)) { throw "Expected exported file missing: $configFile" }
    if ((Get-Content -LiteralPath $greetingFile -Raw) -ne "hello-from-cli-smoke") { throw "Exported greeting.txt content mismatch." }
    if ((Get-Content -LiteralPath $configFile -Raw) -ne "mode=release") { throw "Exported config.txt content mismatch." }

    if (-not (Test-Path -LiteralPath $reportPath)) { throw "Expected report JSON missing: $reportPath" }
    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    if ($report.schemaVersion -ne 1) { throw "Expected report schemaVersion=1, got $($report.schemaVersion)." }
    if ($report.status -ne "Completed") { throw "Expected report status=Completed, got '$($report.status)'." }
    if ($report.exportedCount -ne 2) { throw "Expected report exportedCount=2, got $($report.exportedCount)." }
    if ($report.failedCount -ne 0) { throw "Expected report failedCount=0, got $($report.failedCount)." }

    # 5b. export --format json (pure structured JSON on stdout)
    Write-Host "[smoke] Checking export --format json..."
    $outJsonDir = Join-Path $tempRoot "out-json"
    $exportJsonRaw = (& $CliExecutable export --no-local-profiles --no-decompression-cache $fixturePath --output $outJsonDir --type TextAsset --format json | Out-String)
    if ($LASTEXITCODE -ne 0) { throw "export --format json failed with exit code $LASTEXITCODE." }
    $exportJson = $exportJsonRaw | ConvertFrom-Json
    if ($exportJson.schemaVersion -ne 1 -or $exportJson.status -ne "Completed" -or $exportJson.exportedCount -ne 2) {
        throw "Unexpected export --format json payload: $exportJsonRaw"
    }

    # 6. Exit code contract checks (invalid args = 2, missing path = 1)
    Write-Host "[smoke] Checking exit code semantics..."
    function Invoke-CliExitCode([string[]]$cliArgs) {
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $CliExecutable
        $psi.Arguments = ($cliArgs | ForEach-Object { if ($_ -match '\s') { "`"$_`"" } else { $_ } }) -join ' '
        $psi.UseShellExecute = $false
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $proc = [System.Diagnostics.Process]::Start($psi)
        $null = $proc.StandardOutput.ReadToEnd()
        $null = $proc.StandardError.ReadToEnd()
        $proc.WaitForExit()
        return $proc.ExitCode
    }

    $rcInvalid = Invoke-CliExitCode @("unknown-command")
    if ($rcInvalid -ne 2) { throw "Expected exit code 2 for unknown command, got $rcInvalid." }

    $rcMissing = Invoke-CliExitCode @("scan", "--no-local-profiles", (Join-Path $tempRoot "does-not-exist"))
    if ($rcMissing -ne 1) { throw "Expected exit code 1 for missing path, got $rcMissing." }

    Write-Host "[smoke] All AssetStudio.CLI smoke checks passed!"
}
finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
