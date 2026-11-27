# Contributing to AssetStudio

Thank you for contributing to AssetStudio. This guide documents the minimum development environment, build and test commands, architectural rules, and release checks expected for changes.

## 1. Prerequisites

- **Windows (x64):**
  - [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
  - Visual Studio 2022+ (with **Desktop development with C++** workload) to build `Texture2DDecoderNative`
- **Linux (x64):**
  - [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
  - `cmake`, `g++`, `dpkg-dev`, `file`, `tar`, `xz-utils`

## 2. Building & Running Tests

Build the managed projects and test suite through `AssetStudio.Tests.csproj` (note: running `dotnet build AssetStudio.sln` attempts to invoke MSBuild on the C++ `.vcxproj`, which requires Visual Studio MSBuild on Windows):

```bash
# Restore using locked dependency files
dotnet restore AssetStudio.Tests/AssetStudio.Tests.csproj --locked-mode

# Build in Release with warnings treated as errors
dotnet build AssetStudio.Tests/AssetStudio.Tests.csproj -c Release --no-restore -p:TreatWarningsAsErrors=true

# Run the test suite
dotnet test AssetStudio.Tests/AssetStudio.Tests.csproj -c Release --no-build
```

### Building the native texture decoder

- **Windows (Developer PowerShell):**
  ```powershell
  msbuild Texture2DDecoderNative/Texture2DDecoderNative.vcxproj /p:Configuration=Release /p:Platform=x64 /m
  ```
- **Linux:**
  ```bash
  cmake -S Texture2DDecoderNative -B Texture2DDecoderNative/build -DCMAKE_BUILD_TYPE=Release
  cmake --build Texture2DDecoderNative/build --parallel
  ```

### Running the CLI smoke test & synthetic benchmarks

- **CLI smoke test (Windows):**
  ```powershell
  pwsh -File scripts/smoke-test-cli.ps1 -CliExecutable AssetStudio.CLI/bin/Release/net10.0/AssetStudio.CLI.exe
  ```
- **CLI smoke test (Linux):**
  ```bash
  bash scripts/smoke-test-cli.sh AssetStudio.CLI/bin/Release/net10.0/AssetStudio.CLI
  ```
- **Synthetic benchmarks:** see [`docs/BENCHMARKS.md`](docs/BENCHMARKS.md).

## 3. Code Quality & Architecture Rules

Before opening a pull request, confirm all of the following:

1. **Zero compiler warnings:** `Directory.Build.props` enforces `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`.
2. **Locked NuGet restore:** `<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>` is enabled; do not add NuGet dependencies without clear justification and updated lockfiles.
3. **Pure-MVVM in `AssetStudio.Avalonia`:**
   - Zero `using Avalonia` directives inside `Services/` or `ViewModels/` (except `AvaloniaDialogService.cs`).
   - Zero `Click=` event handlers in `.axaml` files — use `Command="{Binding ...}"`.
   - Always enable `x:CompileBindings="True"` with explicit `x:DataType`.
   - Verify with `MainWindowPureMvvmTests` and `ArchitectureLifecycleAndCoordinatorTests`.
4. **Reuse shared pipelines:** place shared conversion, validation, export reporting, and lifecycle code in `AssetStudioUtility/` so both `AssetStudio.Avalonia` and `AssetStudio.CLI` share one implementation. See [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md).
5. **100% synthetic, portable tests:**
   - Every new test must construct synthetic data in memory or temporary directories (`Path.GetTempPath()`).
   - Never hardcode local machine paths or reference proprietary/commercial game assets.
   - Keep commits, PR descriptions, code comments, and docs written in clear, self-contained plain language.

## 4. Release Verification Checklist

When preparing a tagged release (`vX.Y.Z`):

- [ ] CI test suites pass on both **Windows x64** and **Linux x64** with zero warnings.
- [ ] GUI packages (`win-x64 .zip`, `linux-x64 .tar.gz`, `.AppImage`, `.deb`) include the native texture decoder (and bundled FFmpeg on Linux).
- [ ] Packaged CLI archives (`AssetStudio.CLI-win-x64-<ver>.zip`, `AssetStudio.CLI-linux-x64-<ver>.tar.gz`) pass `smoke-test-cli` (`--help`, `--version`, `scan`, `list`, `export --report`, `export --format json`, exit codes) after extraction.
- [ ] `SHA256SUMS.txt` covers every published archive and passes `sha256sum -c SHA256SUMS.txt`.
- [ ] `SUPPORTED_FEATURES.md`, `docs/COMPATIBILITY.md`, `docs/EXPORT_FORMATS.md`, and `docs/CLI.md` accurately match the binaries shipped in the release (no unreleased features claimed as shipped).
- [ ] Release notes on GitHub Releases list fixes, additions, known limits, and compatibility notes accurately.
