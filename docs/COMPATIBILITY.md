# Compatibility

This page says what is known to work and how we know it. "Reads" means every object of the file is parsed. "Exports correctly" means the exported file was compared with the original source data. These are different claims. A version can be read without any export ever having been checked.

Evidence levels used below:

- **CI (synthetic)**: a test builds the file in memory and runs on every push, on Windows and Linux.
- **Real files (local)**: real Unity output, checked on the maintainer's machine. The matching tests are skipped in CI with a reason, because the files are not in the repository (see [TESTING](#how-to-reproduce)).
- **Real games (local)**: checked by hand on installed games. It can't be reproduced by others.
- **Not verified**: the code may handle it, but nobody has checked.

The table describes the `main` branch. Release `v0.20.0` reads Unity up to 6000.4. Unity 6000.5 support was added after that release.

## Unity versions

| Unity | Detect | Read | Preview | Export | Evidence |
| --- | --- | --- | --- | --- | --- |
| 6000.5 (SerializedFile format 23) | validated | validated (byte-exact objects) | partial (real local GUI checks) | validated: textures (uncompressed pixel-exact), PCM audio & text/fonts byte-exact, meshes, MonoBehaviour incl. `[SerializeReference]`, shaders, animation, GLB | CI (synthetic) for format 23 layout; real files (local) from `6000.5.10f1` editor: Windows & Android, LZ4, LZMA, uncompressed, stripped TypeTrees, extracted TypeTrees, Mono & IL2CPP players |
| 6000.3 | validated | validated (byte-exact objects) | partial (real local GUI checks) | validated: same bundle checks + automated Unity Editor `6000.3.18f1` scene/prefab `.unitypackage` roundtrip (`UnityEditorRoundtripTests`) | real files (local) from `6000.3.18f1` + real Editor import verification |
| 6000.4 | validated | expected (same layout as 6000.3 for known classes) | partial (real games local) | partial (scene `.unitypackage` imported in `6000.3.18f1`) | real games (local); 6000.5 class changes apply only from `6000.5` onward |
| 5.3 – 6000.2 | validated | validated on checked games | partial | partial | real games (local) across 14+ installed games (`RealGame…` tests skip when absent) |
| 3.4 – 5.2 | validated | inherited from Perfare/AssetStudio | not verified | not verified | not verified in this fork |
| < 3.4, 7000.x, Tuanjie | not supported | not supported | not supported | not supported | not supported |

## Asset families

| Asset family | Detect | Read | Preview | Export | Evidence & limits |
| --- | --- | --- | --- | --- | --- |
| `Texture2D`, `Texture2DArray`, `Sprite`, `SpriteAtlas` | validated | validated | validated (channels, mipmaps, 9-slice/pivot) | validated (`png`/`tga`/`jpg`/`bmp`/`webp`, EXR/TIFF for HDR, atlas JSON) | CI (synthetic) + real files (local). Compressed formats need the native decoder in `x64/`. Switch swizzles not supported. |
| `Mesh`, `SkinnedMeshRenderer`, `Avatar`, `Animator` | validated | validated | validated (OpenGL 3D, skeleton, wireframe) | validated (`glb`/`gltf`, binary `fbx`, `obj`, Unity `.unitypackage`) | CI (synthetic) + Blender roundtrip (local) + Unity `6000.3.18f1` FBX/rig import (local). |
| `AnimationClip`, `AnimatorController` | validated | validated | validated (3D playback scrubber + Animator Graph) | validated (`.anim`, `.controller`, clip JSON metadata, Graphviz `.dot`) | CI (synthetic) + real files (local). Humanoid muscle/retargeting edge cases remain partial. |
| `AudioClip`, `VideoClip`, `MovieTexture` | validated | validated | validated (FFmpeg playback) | validated (`wav`/`ogg`/native audio, embedded video bytes) | CI (synthetic) + real files (local). External FMOD `.bank`, Wwise, CRIWARE not supported. |
| `Material`, `Shader` | validated | validated | partial (D3D11 Built-in on Windows; basic fallback URP/HDRP/Linux) | partial (ShaderLab disassembly; PBR approximation in glTF; Standard fallback in Unity `.mat`) | CI (synthetic) + real files (local). Compiled shader bytecode is not decompiled to original HLSL. |
| `MonoBehaviour`, `ScriptableObject`, `MonoScript` | validated | validated (with TypeTrees or Managed/dummy DLLs) | validated (tree/JSON viewer + Script viewer) | validated (JSON, full streaming Unity YAML, C# `.sln`/`.csproj`, Ghidra scripts) | CI (synthetic) + Unity `6000.3.18f1` roundtrip (local). IL2CPP outputs stubs (no native body decompilation). |
| `Terrain`, `TerrainData`, `TerrainLayer` | validated | validated (incl. stripped 5.3–6000.4) | partial (heightmap/mesh via export) | partial (converted mesh `.fbx`/`.glb` + baked splatmap) | CI (synthetic) + real games (local) + Unity `6000.3.18f1` roundtrip (local). Not exported as native `.asset` terrain. |
| `GameObject`, `Transform`, `LODGroup`, Colliders, Scenes & Prefabs | validated | validated | validated (Scene Hierarchy + component inspector) | validated (`.glb` instanced scene, `.unity`, `.prefab`, `.unitypackage`, collider `.obj`) | CI (synthetic) + Unity `6000.3.18f1` roundtrip (`UnityEditorRoundtripTests`). |
| `Font`, `TMP_FontAsset`, `TextAsset` | validated | validated | validated (text, embedded media, font/TMP atlas) | validated (`.ttf`/`.otf`, raw bytes/detected media) | CI (synthetic) + real files (local). |

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
| Texture conversion & image export (`png`/`tga`/`jpg`/`bmp`/`webp`/EXR/TIFF) | yes (needs `x64/Texture2DDecoderNative.dll` for compressed formats) | yes (needs `x64/libTexture2DDecoderNative.so` for compressed formats) | `x86/` decoder, not verified | – |
| 3D model & animation export (`glb`/`gltf`/`fbx`/`obj`/`.anim`) | yes (GUI + CLI) | yes (GUI + CLI) | not verified | – |
| Unity Project, Prefab & `.unitypackage` export | yes (GUI; validated in Unity `6000.3.18f1`) | yes (GUI; POSIX USTAR `.unitypackage`, Editor import not verified on Linux) | not verified | – |
| Audio export (`wav`/`ogg`/native) | yes (GUI + CLI; GUI adds FFmpeg transcode) | yes (GUI + CLI; GUI adds FFmpeg transcode) | not verified | – |
| C# script project export (`.sln`/`.csproj`) & Ghidra export | yes (GUI) | yes (GUI) | not verified | – |

"Not released" means no package is published. "Not verified" means nobody has run it. Neither one means it can't work.

## How to reproduce

The real-file tests read `scratch/unity-fixtures/samples/` (ignored by git). The files there are generated by the Unity editor in batch mode from small sample projects. When the folder is missing, those tests show up as **skipped** with the missing path, not as passed. Tests that need Blender or an installed game skip the same way.

```bash
dotnet test AssetStudio.Tests/AssetStudio.Tests.csproj -c Release
```
