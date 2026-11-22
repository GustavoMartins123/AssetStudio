# AssetStudio

## Attribution and maintenance note

This project is based on [Perfare/AssetStudio](https://github.com/Perfare/AssetStudio), originally created and maintained by Perfare. The original repository was archived by its owner on January 21, 2023 and remains the upstream source and credit for AssetStudio.

This update is an independent community maintenance effort. Its purpose is to keep AssetStudio usable on newer .NET versions, reduce crashes during load/export, and experiment with exporter fixes. It is not an official continuation by Perfare, and it does not claim ownership of the original project.

**None of the repo, the tool, nor the repo owner is affiliated with, or sponsored or authorized by, Unity Technologies or its affiliates.**

AssetStudio is a cross-platform tool for exploring, previewing, extracting, and exporting Unity assets and AssetBundles.

## Features

* **Unity Version Support**:
  * **Unity `3.4` – `6000.4`** (Mono & IL2CPP, including `Renderer`/`SkinnedMeshRenderer` `6000.2+`, `Mesh` LOD info, `Shader` `2021.3.10+` `m_PlayerSubPrograms` / `6000.x`, `Texture2D` `m_ColorSpace`, `AnimatorController` `6000.3–6000.4`, `>512 MiB` serialized objects, and cross-platform `UnityPlayer.dll` / `.exe` PE version fallback for stripped `UnityFS` bundles).
* **Supported Asset Types, Previews & Exporters**:
  * **3D Models (`Mesh`, `Avatar`, `Animator`, `AnimationClip`)**:
    * **Export to `.glb` / `.gltf 2.0`** (native pure-C#, zero extra dependencies) — PBR (`pbrMetallicRoughness` with automatic Metallic/Roughness channel packing), skinning (`skins` + `inverseBindMatrices`), morph targets / BlendShapes (`targets` + `extras.targetNames`), TRS + morph animations, `KHR_texture_transform`, and split animation export. Validated via headless **Blender 5.3.0** roundtrip tests.
    * **Export to `.fbx`** (Binary/ASCII, Blender-reimportable with exact `UnitScaleFactor`, multi-UV layers, binormals, Humanoid `MuscleClip` decoding, BlendShape animation curves, Euler unwrapping, `CombineMeshes`, and FBX 7500 64-bit offsets), **`.obj`**, and Unity native **`.anim` / `.meta`**.
    * **Unified Multi-Mesh 3D OpenGL Preview**: shared textured/lit/wireframe pipeline across `Mesh`, `Avatar`, and `AnimationClip` with optional X-Ray skeleton overlay, multi-part skinned + rigid bone-attached object assembly (including rigid-only chains and skeleton-only 3D Avatar fallback), a **Full Object (N parts)** toggle, and a **1-click `Export Model...`** button with async progress bar directly inside *Model Controls*.
  * **Texture2D**: convert to `png`, `tga`, `jpeg`, `bmp` (with `DXT5nm`/`BC5` normal map unpacking, HDR float clamp, Crunch, BC6H/BC7, ETC2, ASTC, PVRTC) and GPU/CPU preview with channel toggles.
  * **Sprite**: crop `Texture2D` / `SpriteAtlas` (with alpha texture & 32-bit support) to `png`, `tga`, `jpeg`, `bmp`.
  * **AudioClip**: native pure-C# **FSB5 Vorbis → Ogg Vorbis** reconstruction + PCM/ADPCM decoding (`wav`, `ogg`, `mp3`, `m4a`, `fsb`) with real-time audio playback preview.
  * **VideoClip, VideoPlayer & MovieTexture**: unified FFmpeg playback preview (thumbnail, Play/Pause/Stop, seek, volume, loop) and direct export.
  * **TextAsset**: dialogue/text viewer plus O(1) header sniffing and interactive preview/export routing for **40+ embedded binary & multimedia formats** (AVI/MP4/WebM/MKV/Ogg/Bink/USM, WAV/OGG/MP3/FLAC/FSB5/Wwise, PNG/JPEG/WebP/DDS/KTX/PSD, TTF/OTF/WOFF, ZIP/7z/SQLite/PDF/UnityFS/Lua/WASM, etc.).
  * **Shader & Material**: Unity `5.3` – `6000.4` ShaderLab conversion (supporting `m_PlayerSubPrograms` and excluding reflection parameter blobs), plus hardware **Direct3D 11 Shader & Material Preview** on Windows (executing Unity 5.3 DXBC passes, `UsePass`, `GrabPass`, `ForwardAdd`, particles/time, deferred lighting, and shadow mapping).
  * **Font & TextMeshPro (`TMP_FontAsset`)**: renders embedded TrueType/OpenType/TTC/symbol fonts, legacy bitmap `Font` atlases (when `m_FontData` is empty), and `TMP_FontAsset` SDF atlases with alpha enhancement and glyph metrics.
  * **MonoBehaviour**: JSON export and TypeTree/assembly-backed inspection (Mono & IL2CPP dummy DLLs, including `[SerializeReference]`, `Gradient` `2022.2+`, generics, and `InvariantCulture` formatting).
* **Cross-Platform Pure MVVM UI (`AssetStudio.Avalonia`, Windows & Linux)**:
  * Built on **.NET 10** and **Avalonia UI** (`x:CompileBindings="True"`, pure MVVM across all 5 windows).
  * **Project Manager & Back to Projects**: per-project profiles, icons, settings, safe teardown, and instant navigation back to the Project Manager.
  * **SQLite Schema v15 Graph Cache & Zero-Blocking Preview**: persistent asset index, mesh/material/texture cache, `ModelGroup` & `AnimationClipTargets` graph, and a non-detaching **`References` (*Used By* / *Uses*)** tab alongside `Preview` and `Dump` with click-to-jump navigation (preserving OpenGL surfaces across tab switches).
  * **Scene Hierarchy, Asset List & Asset Classes**: virtualized list (`IDataGridCollectionView`), search & check propagation, column sorting, type filters, TypeTree class explorer, and Log Viewer.

## Requirements

* **AssetStudio.Avalonia** (Windows / Linux)
  * [.NET 10 Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (only required for framework-dependent builds; default publish scripts and GitHub Releases produce self-contained builds)
  * Tested on **Windows (x64)** and **Ubuntu 24.04 LTS (x64)**

## Usage

### Load Assets / AssetBundles

Use **File → Load file** or **File → Load folder**, or open a game folder from the **Project Manager**.

When AssetStudio loads AssetBundles in eager mode, it decompresses and reads them in memory. For very large projects, use **Safe/Lazy Mode** (offered automatically by the Project Manager) or extract bundles first via **File → Extract file** / **File → Extract folder**.

### Project Root

Use **Options → Set project root...** to choose the root folder used for dependency lookup.

The project root is different from the loaded file or folder:

* **Loaded file/folder** is the subset of assets you want to inspect or export.
* **Project root** is an optional wider search root used when resolving external dependencies, such as streamed texture or mesh data referenced by loaded assets.

For example, you can load only `F:\Unity_Projects\TEST_PROJECT\Assets\Assets_Ripp\scene_objects`, while setting the project root to `F:\Unity_Projects\TEST_PROJECT`. AssetStudio will load only the selected folder, but can still search the project root when exported models need external texture or mesh resources.

### Project Manager & Caches

AssetStudio.Avalonia starts in the **Project Manager** window (and you can return anytime via the **← Projects** button in the top bar). A project profile stores the name/icon, project root, last load/export paths, per-project settings, and scan statistics without copying or modifying the original game files.

Project Manager data is stored in the user local application data folder:

```text
Windows: %LOCALAPPDATA%\AssetStudio\ProjectManager\projects.db
         %LOCALAPPDATA%\AssetStudio\ProjectManager\Icons\

Linux:   ~/.local/share/AssetStudio/ProjectManager/projects.db
         ~/.local/share/AssetStudio/ProjectManager/Icons/
```

Opening a project sets its project root and runs background indexing. When a project has at least 1,000 Unity bundles or its estimated decompressed size exceeds available RAM, AssetStudio offers **Safe/Lazy Mode**, which keeps lightweight `AssetHandle` entries in memory and materializes assets on demand.

Two project-related caches accelerate repeat loads and previews:

* **SQLite Index & Semantic Graph Cache (Schema v15)**:

```text
Windows: %LOCALAPPDATA%\AssetStudio\IndexCache\project_index.db
Linux:   ~/.local/share/AssetStudio/IndexCache/project_index.db
```

  Stores asset handles, resolved mesh/material/texture links, multi-part `ModelGroup`s, `AnimationClipTargets`, reference edges, and inline LZ4 preview caches keyed by folder path and signature.

* **Decompressed Bundle Cache**:

```text
Windows: %LOCALAPPDATA%\AssetStudio\DecompressedCache\<folder-hash>\
Linux:   ~/.local/share/AssetStudio/DecompressedCache/<folder-hash>/
```

  Stores decompressed bundle `.blocks` files on disk for lazy/low-memory loading so unchanged bundles are reused without decompressing again.

Deleting a project from Project Manager removes only AssetStudio's profile, settings, icon, SQLite cache entries, and decompressed cache folder — never the original game files.

### Preview, References & 1-Click Model Export

* **3D Preview (`Mesh`, `Avatar`, `AnimationClip`)**: open *Model Controls* to toggle **Mesh**, **Textures**, **Bones**, **X-Ray**, **Full Object (N parts)** vs single mesh, vertex density, material tint overrides, and **Export Model...** (exports the active preview target directly in the format chosen in *Export options* with an async progress bar).
* **Right Panel Tabs (`Preview` / `Dump` / `References`)**: switch freely between visual preview, full TypeTree/serialized dump, and bidirectional **Referenced By / References** navigation without losing OpenGL preview state.

### Export Models & Assets

* Open **Options → Export options...** to choose **Model format** (`FBX`, `glTF Binary (.glb)`, or `glTF Separate (.gltf)`), skinning, animations (combined or split files), blend shapes, `Combine meshes`, scale factor, texture format, audio WAV conversion, and Unity `.anim` / `.meta` generation.
* Export individual or bulk assets via the **Export** menu, whole hierarchies via the **Model** menu in *Scene Hierarchy*, or the active 3D preview target directly via **Export Model...** in *Model Controls*.
* **With AnimationClip**: select a model in *Scene Hierarchy* and an `AnimationClip` in *Asset List* (**Model → Export selected objects with AnimationClip**), select `Animator` + `AnimationClip` in *Asset List* (**Export → Export Animator with selected AnimationClip**), or preview an `AnimationClip` and click **Export Model...**.

### Export MonoBehaviour (Mono & IL2CPP)

When inspecting or exporting `MonoBehaviour` assets for the first time, AssetStudio prompts for the assembly directory (such as the `Managed` folder). For IL2CPP games, generate dummy DLLs with [Il2CppDumper by Perfare](https://github.com/Perfare/Il2CppDumper) and select the generated dummy DLL folder.

### Known Limitations on Protected / Custom-Engine Titles

* Games with **encrypted `.assets` headers** (e.g., partial header encryption), **proprietary encrypted VFS containers** (e.g., `.blc`/`.chk`), or **modified engine forks without TypeTrees** cannot be decoded without game-specific decryption or custom binary schemas.
* Audio/video stored in external third-party middleware banks (such as FMOD `.bank`, Wwise `.bnk`/`.wem`, or CRIWARE `.acb`/`.awb`/`.usm`) outside Unity's `AudioClip`/`VideoClip` assets is not extracted by Unity asset decoders.

## Build

* **Windows**: .NET 10.0 SDK and Visual Studio 2022 or newer (with C++ desktop workload for `Texture2DDecoderNative`)
* **Linux**: .NET 10.0 SDK, CMake, and GCC/G++

### One-command Windows publish

```powershell
.\scripts\publish-windows.ps1 -Configuration Release -Platform x64
```

Output folder: `AssetStudio.Avalonia\bin\Release\net10.0\win-x64\publish\AssetStudio.Avalonia.exe` (run from `publish`, which contains `x64\Texture2DDecoderNative.dll`).

### One-command Linux publish

```bash
./scripts/publish-linux.sh -c Release
```

Output folder: `AssetStudio.Avalonia/bin/Release/net10.0/linux-x64/publish/AssetStudio.Avalonia` (bundles `x64/libTexture2DDecoderNative.so` and FFmpeg shared libraries; use `./scripts/package-linux.sh` to build `.AppImage` and `.deb`).

### Running Tests

```powershell
dotnet restore AssetStudio.Tests/AssetStudio.Tests.csproj --locked-mode
dotnet test AssetStudio.Tests/AssetStudio.Tests.csproj -c Release --no-restore
```

## Native Dependencies

Compressed texture decoding depends on `Texture2DDecoderNative`. Audio and video previewing in the Avalonia UI use FFmpeg.

```text
Windows: AssetStudio.Avalonia.exe + x64\Texture2DDecoderNative.dll
Linux:   AssetStudio.Avalonia     + x64/libTexture2DDecoderNative.so + x64/ffmpeg/
```

## Open source libraries used

See [`NOTICE.md`](NOTICE.md) for third-party license notices (including FSB5 Vorbis codebook reconstruction attribution).

### Texture2DDecoder
* [Ishotihadus/mikunyan](https://github.com/Ishotihadus/mikunyan)
* [BinomialLLC/crunch](https://github.com/BinomialLLC/crunch)
* [Unity-Technologies/crunch](https://github.com/Unity-Technologies/crunch/tree/unity)
