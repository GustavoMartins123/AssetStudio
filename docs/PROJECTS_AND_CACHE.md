# Projects, Caches, Installation & Troubleshooting

This guide explains how AssetStudio binaries are packaged, where native dependencies belong, how loaded folders differ from Project Root and caches, and how to recover from common operational issues.

## 1. Self-Contained vs. Framework-Dependent Builds

| Package type | .NET 10 Runtime required? | Native libraries included? | When to use |
| --- | --- | --- | --- |
| **Release archive / default publish script** (`self-contained`) | No | Yes (`x64/Texture2DDecoderNative.*`, plus Linux `x64/ffmpeg/`) | Recommended for end users and CI automation (`AssetStudio-*.zip`, `.tar.gz`, `.AppImage`, `.deb`, `AssetStudio.CLI-*`). |
| **Framework-dependent build** (`-SelfContained $false` / `--self-contained false` / `dotnet run`) | Yes (`.NET 10 Desktop/Runtime`) | Must be built and placed in `x64/` next to the executable | Local development and debugging from source. |

Always launch published binaries from the `publish/` folder (or extracted release archive), not from the raw `bin/Release/net10.0/<rid>/` build output folder which may not contain the bundled native libraries.

## 2. Expected Native Dependency Layout

Compressed Unity textures (`DXT`, `BCn`, `ETC2`, `ASTC`, `Crunch`) require `Texture2DDecoderNative`. Audio transcoding and video preview/playback require FFmpeg.

### Windows (`win-x64`)

```text
AssetStudio.Avalonia.exe   (or AssetStudio.CLI.exe)
x64/
  Texture2DDecoderNative.dll
```

### Linux (`linux-x64`)

```text
AssetStudio.Avalonia       (or AssetStudio.CLI)
x64/
  libTexture2DDecoderNative.so
  ffmpeg/
    libavcodec.so.62
    libavformat.so.62
    libavutil.so.60
    libswresample.so.6
    libswscale.so.9
```

### Diagnosing missing native dependencies

- **Compressed textures fail to decode or export (`DecodeFailed`):** confirm `x64/Texture2DDecoderNative.dll` (Windows) or `x64/libTexture2DDecoderNative.so` (Linux) sits directly inside the `x64/` subdirectory next to the executable and matches your CPU architecture (`x64`).
- **Audio/video playback unavailable on Linux:** verify `x64/ffmpeg/libavcodec.so.62` is present in the extracted package or install system FFmpeg shared libraries matching your build.

## 3. Loaded Folder vs. Project Root vs. Cache Directories

AssetStudio keeps three directory roles strictly separate and **never modifies or deletes your source project files**:

| Role | What it is | Modified by AssetStudio? |
| --- | --- | --- |
| **Loaded File / Folder** | The specific file(s) or subfolder you open to inspect or export. | Never (read-only). |
| **Project Root** | Optional parent root (**Options -> Set project root...**) used when resolving external references (`.resS`, `.resource`, shared `.assets`, Addressables catalogs). | Never (read-only). |
| **Project Manager & Caches** | AssetStudio's own local application data storing project profiles, SQLite indexes, mmap sidecars, preview thumbnails, and decompressed bundle blocks. | Yes (managed exclusively inside AssetStudio's data folder). |

### Local application data locations

By default, AssetStudio stores its own data under:

- **Windows:** `%LOCALAPPDATA%\AssetStudio\`
- **Linux:** `~/.local/share/AssetStudio/`

You can override the user data root with `ASSETSTUDIO_DATA_DIR`, and the decompression cache root specifically with `ASSETSTUDIO_DECOMPRESSION_CACHE_DIR`.

Subdirectories inside the AssetStudio data folder:

- `ProjectManager/projects.db` and `ProjectManager/Icons/`: saved project entries, roots, and copied icons.
- `IndexCache/`: per-project SQLite databases (`*.db`, `-wal`, `-shm`) and fast memory-mapped sidecar indexes (`*.idx`), plus cached preview entries.
- `DecompressedCache/<folder-hash>/`: decompressed UnityFS block data cached on disk for lazy/low-memory loading and shared with `AssetStudio.CLI` (disable per CLI call with `--no-decompression-cache`).
- `DecryptionProfiles/` and `Plugins/Decryption/`: user-supplied decryption profiles and optional stream-transform plugins.

## 4. Cache Invalidation, Recovery & Safe Cleanup

### Automatic invalidation
- Each project index and preview cache is keyed by a deterministic folder signature incorporating file paths, sizes, timestamps, and header fingerprints.
- When a bundle is replaced, modified, or removed, stale index records, `.idx` sidecars, and `PreviewCacheEntries` for changed bundles are invalidated automatically so outdated previews are not served silently.

### Automatic recovery from corruption or interrupted indexing
- If an indexing session was interrupted (process crash, power loss, or forced cancellation leaving indexing status as `running`), AssetStudio refuses to serve partial index/preview snapshots and triggers a clean recovery/reindex.
- If a SQLite cache database fails integrity validation (`PRAGMA quick_check`) or an `.idx` sidecar is truncated/corrupted, AssetStudio automatically removes the damaged cache file and rebuilds it cleanly on the next open.
- Disk-full errors (`ENOSPC` / `0x80070070`) and read/permission failures are classified separately from corruption so valid caches are not blindly deleted when storage is temporarily full or locked.

### Safe cache cleanup
- **Clear Current Project Cache** (in GUI) or deleting a project in Project Manager removes only files strictly confined inside AssetStudio's cache roots (`CacheObservabilityService` enforces canonical path confinement and rejects symlinks/reparse points or paths outside the cache root).
- Your source Unity/project folders are **never** touched during cache cleanup or recovery.

## 5. Troubleshooting Common Issues

| Symptom | Likely cause | What to check |
| --- | --- | --- |
| `MonoBehaviour` skipped or shows empty fields | File was built with stripped TypeTrees | Pass `--assemblies <Managed folder>` in CLI, or select the `Managed` / IL2CPP `DummyDll` folder when prompted in GUI. |
| `Texture2D` / `AudioClip` / `VideoClip` skipped with `MissingStreamedResource` | Companion `.resS` or `.resource` file is not adjacent or outside Project Root | Set **Project Root** to the top-level data folder containing both the serialized file and its `.resS`/`.resource` companions. |
| Material or Shader shows `PBR Approximation`, `Basic Fallback`, or `ShaderLab Text` | Asset uses URP/HDRP/custom shader, Unity 2021–6000.x, or Linux (non-D3D11) | Expected behavior; hover the preview mode badge for the exact reason and export alignment note. |
| High RAM usage when opening a large folder | Eager loading all bundles into memory | Open via Project Manager using **Safe/Lazy Mode**, or extract bundles first via **File -> Extract folder**. |
| Cache error mentioning disk space or access denied | Low free space on the local AppData drive or locked DB file | Free disk space or redirect `ASSETSTUDIO_DECOMPRESSION_CACHE_DIR` / `ASSETSTUDIO_DATA_DIR` to a drive with more space; close duplicate AssetStudio instances holding the DB lock. |
| Unclear export outcome in batch runs | Partial failure or missing dependencies | Inspect `--report <file.json>` (CLI), `ExportManifest.json` (Unity export), or `errors.txt` (GUI bulk export); check `status`, `missingDependencies`, and `diagnostics`. |
