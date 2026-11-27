# AssetStudio

## Attribution and maintenance note

This project is based on [Perfare/AssetStudio](https://github.com/Perfare/AssetStudio), originally created and maintained by Perfare. The original repository was archived by its owner on January 21, 2023 and remains the upstream source and credit for AssetStudio.

This update is an independent community maintenance effort. Its purpose is to keep AssetStudio usable on newer .NET versions, reduce crashes during load/export, and experiment with exporter fixes. It is not an official continuation by Perfare, and it does not claim ownership of the original project.

**None of the repo, the tool, nor the repo owner is affiliated with, or sponsored or authorized by, Unity Technologies or its affiliates.**

AssetStudio is a tool for exploring, extracting and exporting assets and AssetBundles.

For a plain-language overview of what is currently supported, known limits, and features still under study, see [`SUPPORTED_FEATURES.md`](SUPPORTED_FEATURES.md). It also marks which features are in the latest release (`v0.20.0`) and which are only in the `main` branch so far.

More details:

* [`docs/COMPATIBILITY.md`](docs/COMPATIBILITY.md): which Unity versions, build types and platforms are verified, and how.
* [`docs/EXPORT_FORMATS.md`](docs/EXPORT_FORMATS.md): what each export format keeps and what it loses.
* [`docs/CLI.md`](docs/CLI.md): the command-line tool (including `AssetStudio.CLI --version` to identify dev builds vs. releases).

## Features

* Supported Unity versions:
  * 3.4 – 6000.5 (Mono and IL2CPP) on `main`; release `v0.20.0` reads up to 6000.4. See [`docs/COMPATIBILITY.md`](docs/COMPATIBILITY.md) for what is verified per version.
* Supported asset types:
  * **Texture2D**: convert to `png`, `tga`, `jpeg`, `bmp`, with preview and color channel toggles.
  * **Sprite**: crop `Texture2D` / `SpriteAtlas` to `png`, `tga`, `jpeg`, `bmp`.
  * **AudioClip**: export to `wav`, `ogg`, `mp3`, `m4a`, `fsb` (including FSB5 Vorbis decoding), with audio playback preview.
  * **Font**: export `ttf`, `otf`; preview embedded fonts, bitmap font atlases, and TextMeshPro font atlases.
  * **Mesh, Avatar, Animator & AnimationClip**: export to `glb`, `gltf`, `fbx`, `obj`, and Unity `.anim` / `.meta`; 3D OpenGL preview with textures, skeleton overlay, multi-part model groups, synchronized animation playback with frame scrubber, batch animation export, and direct export from the preview panel.
  * **TextAsset**: text viewer plus detection, preview, and export for common embedded video, audio, image, font, and binary formats.
  * **Shader & Material**: ShaderLab export, plus Direct3D 11 shader and material preview on Windows.
  * **MovieTexture & VideoClip**: video playback preview and export.
  * **MonoBehaviour & GameObject**: searchable tree and JSON viewer with clickable asset links, Scene Hierarchy GameObject component inspector, and export to `json`.
* Cross-Platform Support (Windows & Linux):
  * **AssetStudio.Avalonia**
  * Avalonia UI frontend on .NET 10 with Light/Dark theme, Project Manager, Scene Hierarchy search & GameObject inspector, Asset List sorting & filters, Asset Classes explorer, Preview / Dump / References tabs with Back/Forward navigation, Log Viewer, and saved settings.

## Requirements

* **AssetStudio.Avalonia** (Windows / Linux)
  * [.NET 10 Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (only required for framework-dependent builds; default publish scripts and releases produce self-contained builds)
  * Tested on Windows (x64) and Ubuntu 24.04 LTS (x64)

## Usage

### Load Assets/AssetBundles

Use **File → Load file** or **File → Load folder**.

When AssetStudio loads AssetBundles directly in memory, large projects can use a significant amount of RAM. For large folders, use **Safe/Lazy Mode** from the Project Manager, or use **File → Extract file** / **File → Extract folder** to decompress AssetBundles to disk first.

### Project Root

Use **Options → Set project root...** to choose the root folder used for dependency lookup.

The project root is different from the loaded file or folder:

* **Loaded file/folder** is the subset of assets you want to inspect or export.
* **Project root** is an optional wider search root used when resolving external dependencies, such as streamed texture or mesh data referenced by loaded assets.

For example, you can load only `F:\Unity_Projects\TEST_PROJECT\Assets\Assets_Ripp\scene_objects`, while setting the project root to `F:\Unity_Projects\TEST_PROJECT`. AssetStudio will load only the selected folder, but can still search the project root when exported models need external texture or mesh resources.

### Project Manager

AssetStudio.Avalonia starts in the **Project Manager** window, and you can return to it at any time using the **← Projects** button. A project is a profile for a game or Unity folder that stores the project name/icon, project root, last load/export paths, per-project settings, and scan statistics. It does not copy or modify the original game files.

Project Manager data is stored in the user local application data folder:

```text
%LOCALAPPDATA%\AssetStudio\ProjectManager\projects.db
%LOCALAPPDATA%\AssetStudio\ProjectManager\Icons\
```

On Linux this maps to:

```text
~/.local/share/AssetStudio/ProjectManager/projects.db
~/.local/share/AssetStudio/ProjectManager/Icons/
```

Opening a project sets its project root and starts background indexing. If the selected root is large, AssetStudio scans the folder first and may offer **Safe/Lazy Mode**. A project is considered risky when it has at least 1,000 Unity bundles, or when the estimated decompressed load size exceeds available RAM.

Safe/Lazy Mode indexes files in batches and keeps lightweight entries in the project index instead of holding every bundle in memory. When an asset is previewed or exported later, AssetStudio reloads only the files needed for that asset.

There are two project-related caches:

* **Index cache**: stored in SQLite at:

```text
%LOCALAPPDATA%\AssetStudio\IndexCache\project_index.db
~/.local/share/AssetStudio/IndexCache/project_index.db
```

  Stores the indexed asset list and asset relationships (materials, textures, model groups, animation targets, and references) keyed by folder path and folder signature so repeat opens skip full re-indexing.

* **Decompressed cache**: stored per project folder under:

```text
%LOCALAPPDATA%\AssetStudio\DecompressedCache\<folder-hash>\
~/.local/share/AssetStudio/DecompressedCache/<folder-hash>/
```

  Stores decompressed bundle block data on disk as `.blocks` files when lazy or low-memory loading is active, so unchanged bundles do not need to be decompressed again.

Deleting a project from Project Manager removes only AssetStudio's project entry, saved settings, copied icons, index cache, and decompressed cache folder for that project root. The original game or Unity project folder is not deleted.

### Preview, Dump & References

* The right panel provides **Preview**, **Dump**, and **References** tabs.
* In **3D Preview** (`Mesh`, `Avatar`, `AnimationClip`), *Model Controls* lets you toggle mesh visibility, textures, bones, X-ray bones, full multi-part object vs single mesh, vertex density, material tint, and **Export Model...** directly from the preview.
* The **References** tab lists assets that use the selected asset and assets it depends on, with click-to-jump navigation.

### Extract/Decompress AssetBundles

Use **File → Extract file** or **File → Extract folder**.

### Export Assets

Use the **Export** menu.

### Export Model

Choose the model format (**glTF Binary (`.glb`)** by default, **glTF Separate (`.gltf`)**, or **FBX**) and options under **Options → Export options...**.

* Export from **Scene Hierarchy** using the **Model** menu.
* Export an `Animator` or model group from **Asset List** using the **Export** menu.
* Export the active 3D preview target directly using **Export Model...** in *Model Controls*.

#### With AnimationClip

* Select a model in **Scene Hierarchy**, then select an `AnimationClip` in **Asset List** and use **Model → Export selected objects with AnimationClip**.
* Or select an `Animator` and an `AnimationClip` in **Asset List** and use **Export → Export Animator with selected AnimationClip**.
* Or preview an `AnimationClip` and use **Export Model...** in *Model Controls*.

### Export MonoBehaviour

When you select an asset of the `MonoBehaviour` type for the first time, AssetStudio asks for the assembly directory, such as the `Managed` folder.

#### For IL2CPP

Generate dummy DLLs with [Il2CppDumper by Perfare](https://github.com/Perfare/Il2CppDumper), then select the generated dummy DLL folder when prompted for the assembly directory.

### Command line

`AssetStudio.CLI` (in `main`, packaged from the next release) scans, lists, inspects and exports assets without the GUI:

```bash
AssetStudio.CLI list "D:\Games\My Game\Game_Data" --type Texture2D --format csv
AssetStudio.CLI export "D:\Games\My Game\Game_Data" --output out --type all
```

See [`docs/CLI.md`](docs/CLI.md) for every command, export type, option and exit code.

### Limitations

* Games using encrypted `.assets` headers, proprietary encrypted container formats, or modified engine formats without TypeTrees are not supported out of the box.
* Audio and video stored in third-party middleware banks (such as FMOD `.bank`, Wwise `.bnk`/`.wem`, or CRIWARE `.acb`/`.awb`/`.usm`) outside Unity assets are not extracted.
* What each export format keeps and loses is listed in [`docs/EXPORT_FORMATS.md`](docs/EXPORT_FORMATS.md).

## Build

* Windows: .NET 10.0 SDK and Visual Studio 2022 or newer (with C++ desktop workload for the native texture decoder)
* Linux: .NET 10.0 SDK, CMake, and GCC/G++

### One-command Windows publish

Use the publish script to build the managed GUI, build the native DLLs, and copy the native dependencies into the runtime folder expected by AssetStudio. The default publish is self-contained, so the target machine does not need the .NET SDK or runtime installed.

```powershell
.\scripts\publish-windows.ps1 -Configuration Release -Platform x64
```

Output folder:

```text
AssetStudio.Avalonia\bin\Release\net10.0\win-x64\publish\AssetStudio.Avalonia.exe
```

Run the executable from inside `publish` (not from `AssetStudio.Avalonia\bin\Release\net10.0\win-x64`, which does not receive native dependencies).

Use `-SelfContained $false` for a framework-dependent build, `-SkipNative` to skip building the native texture decoder, and `-OutputDir <path>` for a custom output folder.

### One-command Linux publish

Use the publish script to build the native C++ decoder library, publish the Avalonia GUI application, and bundle `libTexture2DDecoderNative.so` into the target publish folder. The default publish is self-contained.

```bash
./scripts/publish-linux.sh -c Release
```

Output folder:

```text
AssetStudio.Avalonia/bin/Release/net10.0/linux-x64/publish/AssetStudio.Avalonia
```

Use `--self-contained false` for a framework-dependent Linux build, or `./scripts/package-linux.sh` to create `.AppImage` and `.deb` packages.

## Native Dependencies

Compressed texture conversion depends on the native texture decoder library. Audio and video previewing in the Avalonia UI use FFmpeg.

### Windows Layout

```text
AssetStudio.Avalonia.exe
x64\Texture2DDecoderNative.dll
```

### Linux Layout

```text
AssetStudio.Avalonia
x64/libTexture2DDecoderNative.so
x64/ffmpeg/
```

## Open source libraries used

See [`NOTICE.md`](NOTICE.md) for third-party license notices.

### Texture2DDecoder
* [Ishotihadus/mikunyan](https://github.com/Ishotihadus/mikunyan)
* [BinomialLLC/crunch](https://github.com/BinomialLLC/crunch)
* [Unity-Technologies/crunch](https://github.com/Unity-Technologies/crunch/tree/unity)
