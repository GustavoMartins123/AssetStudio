[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [ValidateSet("x64", "x86")]
    [string]$Platform = "x64",

    [string]$Framework = "net10.0",

    [bool]$SelfContained = $true,

    [string]$OutputDir,

    [switch]$SkipNative,

    [switch]$KeepBuildServers
)

$ErrorActionPreference = "Stop"

$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$Runtime = if ($Platform -eq "x64") { "win-x64" } else { "win-x86" }
$NativePlatform = if ($Platform -eq "x64") { "x64" } else { "Win32" }
$RuntimeFolder = if ($Platform -eq "x64") { "x64" } else { "x86" }
$RuntimeOutputDir = Join-Path $RepoRoot "AssetStudio.Avalonia\bin\$Configuration\$Framework\$Runtime"
$DefaultPublishDir = Join-Path $RepoRoot "AssetStudio.Avalonia\bin\$Configuration\$Framework\$Runtime\publish"
$PublishDir = if ($OutputDir) { $OutputDir } else { $DefaultPublishDir }

function Find-MSBuild {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere) {
        $installPath = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
        if ($installPath) {
            $candidate = Join-Path $installPath "MSBuild\Current\Bin\MSBuild.exe"
            if (Test-Path $candidate) {
                return $candidate
            }
        }
    }

    $pathCommand = Get-Command "MSBuild.exe" -ErrorAction SilentlyContinue
    if ($pathCommand) {
        return $pathCommand.Source
    }

    $candidates = @(
        "${env:ProgramFiles}\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe",
        "${env:ProgramFiles}\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe",
        "${env:ProgramFiles}\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe",
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\MSBuild.exe",
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2019\Professional\MSBuild\Current\Bin\MSBuild.exe",
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2019\Enterprise\MSBuild\Current\Bin\MSBuild.exe"
    )

    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) {
            return $candidate
        }
    }

    throw "MSBuild.exe was not found. Install Visual Studio 2022 with the Desktop development with C++ workload."
}

function Invoke-Checked {
    param(
        [string]$FilePath,
        [string[]]$Arguments
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath failed with exit code $LASTEXITCODE."
    }
}

function Stop-DotNetBuildServers {
    if ($KeepBuildServers) {
        return
    }

    Write-Host "Stopping .NET build servers..."
    & dotnet build-server shutdown | Out-Host
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "dotnet build-server shutdown failed with exit code $LASTEXITCODE."
    }
}

function Assert-OutputExecutableIsNotRunning {
    $outputRoots = @($PublishDir)
    if (-not $OutputDir) {
        $outputRoots += $RuntimeOutputDir
    }

    $runningProcesses = Get-Process -Name "AssetStudio.Avalonia" -ErrorAction SilentlyContinue
    foreach ($process in $runningProcesses) {
        try {
            $processPath = $process.MainModule.FileName
        }
        catch {
            continue
        }

        foreach ($outputRoot in $outputRoots) {
            $resolvedOutputRoot = [System.IO.Path]::GetFullPath($outputRoot).TrimEnd('\') + '\'
            if ($processPath.StartsWith($resolvedOutputRoot, [StringComparison]::OrdinalIgnoreCase)) {
                throw "Close AssetStudio.Avalonia.exe before publishing. Running process $($process.Id) is using '$processPath'."
            }
        }
    }
}

function Copy-IfExists {
    param(
        [string]$Source,
        [string]$Destination
    )

    if (Test-Path $Source) {
        Copy-Item -LiteralPath $Source -Destination $Destination -Force
        Write-Host "Copied $(Split-Path $Source -Leaf)"
        return $true
    }

    return $false
}

function Copy-NativeDll {
    param(
        [string]$ProjectFolder,
        [string]$DllName
    )

    $nativeTargetDir = Join-Path $PublishDir $RuntimeFolder
    New-Item -ItemType Directory -Force -Path $nativeTargetDir | Out-Null

    $candidates = @(
        (Join-Path $RepoRoot "$ProjectFolder\bin\$NativePlatform\$Configuration\$DllName"),
        (Join-Path $RepoRoot "$ProjectFolder\bin\$Platform\$Configuration\$DllName")
    )

    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) {
            Copy-IfExists $candidate $nativeTargetDir | Out-Null
            return $true
        }
    }

    Write-Warning "$DllName was not found after build."
    return $false
}

try {
    $previousNodeReuse = $env:MSBUILDDISABLENODEREUSE
    if (-not $KeepBuildServers) {
        $env:MSBUILDDISABLENODEREUSE = "1"
    }

    Write-Host "Publishing AssetStudio.Avalonia ($Configuration, $Framework, $Runtime, self-contained=$SelfContained)"
    Assert-OutputExecutableIsNotRunning

    if (-not $SkipNative) {
        $msbuild = Find-MSBuild
        Write-Host "Using MSBuild: $msbuild"

        Invoke-Checked $msbuild @(
            (Join-Path $RepoRoot "Texture2DDecoderNative\Texture2DDecoderNative.vcxproj"),
            "/m",
            "/nr:false",
            "/p:Configuration=$Configuration",
            "/p:Platform=$NativePlatform"
        )
    }

    $publishArgs = @(
        "publish",
        (Join-Path $RepoRoot "AssetStudio.Avalonia\AssetStudio.Avalonia.csproj"),
        "-c", $Configuration,
        "-f", $Framework,
        "-r", $Runtime,
        "--self-contained", $SelfContained.ToString().ToLowerInvariant(),
        "--disable-build-servers",
        "/p:UseSharedCompilation=false"
    )

    if ($OutputDir) {
        $publishArgs += @("-o", $PublishDir)
    }

    Invoke-Checked "dotnet" $publishArgs

    if (-not $SkipNative) {
        if (-not (Copy-NativeDll "Texture2DDecoderNative" "Texture2DDecoderNative.dll")) {
            throw "Texture2DDecoderNative.dll is required for texture export. Build Texture2DDecoderNative or rerun with -SkipNative to publish the managed app only."
        }

    }

    Write-Host ""
    Write-Host "Done: $PublishDir"
}
finally {
    if (-not $KeepBuildServers) {
        Stop-DotNetBuildServers
    }

    if ($null -eq $previousNodeReuse) {
        Remove-Item Env:\MSBUILDDISABLENODEREUSE -ErrorAction SilentlyContinue
    }
    else {
        $env:MSBUILDDISABLENODEREUSE = $previousNodeReuse
    }
}
