# Compatibility

This page says what is known to work and how we know it. "Reads" means every object of the file is parsed. "Exports correctly" means the exported file was compared with the original source data. These are different claims. A version can be read without any export ever having been checked.

Evidence levels used below:

- **CI (synthetic)**: a test builds the file in memory and runs on every push, on Windows and Linux.
- **Real files (local)**: real Unity output, checked on the maintainer's machine. The matching tests are skipped in CI with a reason, because the files are not in the repository (see [TESTING](#how-to-reproduce)).
- **Real games (local)**: checked by hand on installed games. It can't be reproduced by others.
- **Not verified**: the code may handle it, but nobody has checked.

The table describes the `main` branch. Release `v0.20.0` reads Unity up to 6000.4. Unity 6000.5 support was added after that release.

## Unity versions

| Unity | Detect | Read | Export | Evidence |
| --- | --- | --- | --- | --- |
| 6000.5 (SerializedFile format 23) | yes | yes, every object byte-exact | yes: textures (uncompressed formats pixel-exact), PCM audio and text/fonts byte-exact, meshes, MonoBehaviour including `[SerializeReference]`, shaders, animation, GLB | CI (synthetic) for the format 23 layout; real files (local) from the 6000.5.10f1 editor: Windows and Android, LZ4, LZMA, uncompressed, no TypeTrees, extracted TypeTrees, Mono and IL2CPP players |
| 6000.3 | yes | yes, every object byte-exact | same checks as 6000.5 on one LZ4 bundle | real files (local) from 6000.3.18f1 |
| 6000.4 | yes | expected (same layout as 6000.3 for the known classes) | not verified | not verified with generated files; the 6000.5 class changes are only applied from 6000.5 on |
| 5.3 – 6000.2 | yes | yes on the games checked | partly | real games (local), checked on 14 installed games during earlier work; the `RealGame…` tests skip when the game is not installed |
| 3.4 – 5.2 | yes | inherited from Perfare/AssetStudio | not verified | not verified in this fork |
| < 3.4, 7000.x, Tuanjie | no | no | no | not supported |

## How the build was made

| Variant | Status | Evidence |
| --- | --- | --- |
| With TypeTrees | read and exported | CI (synthetic), real files (local) |
| Without TypeTrees (player builds, stripped bundles) | read; MonoBehaviour fields need the game's `Managed` assemblies (or IL2CPP dummy DLLs) | real files (local): 6000.3/6000.5 Mono players |
| Extracted TypeTrees (`ContentBuildFlags.ExtractTypeTree`, Unity 6000.5) | read when the TypeTree archive (`typetrees.bin`) is opened with the bundles, in any order | CI (synthetic), real files (local) |
| IL2CPP metadata | read for metadata v24–v31, v39 (6000.3) and v107 (6000.5) | real files (local) for v107 with `GameAssembly.dll` |

## Containers and compression

| Container | Status | Evidence |
| --- | --- | --- |
| UnityFS with LZ4/LZ4HC, LZMA or no compression | read | CI (synthetic), real files (local) |
| UnityFS with Zstd blocks (custom engines) | read | CI (synthetic) |
| Several bundles concatenated in one file | read, each segment | CI (synthetic) |
| `UnityArchive` | read | CI (synthetic) |
| Plain serialized files (`level0`, `sharedassets0.assets`) with `.resS` / `.resource` | read | real files (local) |
| APK / OBB / ZIP (stored entries read in place) | read | CI (synthetic) |
| Addressables `catalog.json` / `catalog.bin` | dependencies resolved | CI (synthetic) |
| Encrypted files | detected; opened only with a user profile or key | CI (synthetic) for the supported cipher modes |
| FMOD `.bank`, Wwise `.bnk`/`.wem`, CRIWARE `.acb`/`.awb`/`.usm` | not extracted | not supported |

## Platform that built the game

| Platform | Status |
| --- | --- |
| Windows (StandaloneWindows64) | real files (local) |
| Android (ETC2/ASTC textures, split alpha sprites) | real files (local) for 6000.5 |
| iOS, WebGL, macOS, Linux players | not verified |
| Nintendo Switch (swizzled textures), other consoles | not supported: swizzled textures are not unswizzled |

## Where AssetStudio runs

| | Windows x64 | Linux x64 | Windows x86 | macOS, ARM64 |
| --- | --- | --- | --- | --- |
| GUI (`AssetStudio.Avalonia`) | released | released (`.AppImage`, `.deb`, `.tar.gz`) | can be built (`publish-windows.ps1 -Platform x86`), not released, not verified | not released, not verified |
| CLI (`AssetStudio.CLI`) | `main`; packaged from the next release | `main`; packaged from the next release | not verified | not verified |
| 3D model preview (OpenGL) | yes | yes | not verified | – |
| Shader and material visual preview | Direct3D 11, many Built-in shaders | falls back to basic shading or text | not verified | – |
| Audio and video preview | yes | yes (FFmpeg libraries bundled in the package) | not verified | – |
| Texture conversion | needs `x64/Texture2DDecoderNative.dll` | needs `x64/libTexture2DDecoderNative.so` | `x86/` decoder | – |

"Not released" means no package is published. "Not verified" means nobody has run it. Neither one means it can't work.

## How to reproduce

The real-file tests read `scratch/unity-fixtures/samples/` (ignored by git). The files there are generated by the Unity editor in batch mode from small sample projects. When the folder is missing, those tests show up as **skipped** with the missing path, not as passed. Tests that need Blender or an installed game skip the same way.

```bash
dotnet test AssetStudio.Tests/AssetStudio.Tests.csproj -c Release
```
