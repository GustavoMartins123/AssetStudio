# AssetStudio

## Attribution and maintenance note

This project is based on [Perfare/AssetStudio](https://github.com/Perfare/AssetStudio), originally created and maintained by Perfare. The original repository was archived by its owner on January 21, 2023 and remains the upstream source and credit for AssetStudio.

This update is an independent community maintenance effort. Its purpose is to keep AssetStudio usable on newer .NET versions, reduce crashes during load/export, and experiment with exporter fixes. It is not an official continuation by Perfare, and it does not claim ownership of the original project.

**None of the repo, the tool, nor the repo owner is affiliated with, or sponsored or authorized by, Unity Technologies or its affiliates.**

AssetStudio is a tool for exploring, extracting and exporting assets and assetbundles.

## Features
* Support version:
  * 3.4 - 2022.1
* Support asset types:
  * **Texture2D** : convert to png, tga, jpeg, bmp
  * **Sprite** : crop Texture2D to png, tga, jpeg, bmp
  * **AudioClip** : mp3, ogg, wav, m4a, fsb. Supports converting FSB files to WAV(PCM) and real-time audio playback preview via FMOD.
  * **Font** : ttf, otf
  * **Mesh** : obj, fbx, and real-time 3D preview via OpenGL
  * **TextAsset**
  * **Shader**
  * **MovieTexture**
  * **VideoClip**
  * **MonoBehaviour** : json
  * **Animator** : export to FBX file with bound AnimationClip
* Cross-Platform Linux Support:
  * **AssetStudio.Avalonia**
  * Includes feature-parity with the legacy Windows Forms UI (Scene Hierarchy search & check propagation, Asset List column sorting & filters, Asset Classes TypeTrees explorer, Log Viewer, settings persistence, etc.).

## Requirements

- AssetStudio.net472 (Windows-only)
   - [.NET Framework 4.7.2](https://dotnet.microsoft.com/download/dotnet-framework/net472)
- AssetStudio.net10 (Windows-only)
   - [.NET Desktop Runtime 10](https://dotnet.microsoft.com/download/dotnet/10.0)
- AssetStudio.Avalonia (Linux / Cross-Platform)
   - [.NET Runtime 10](https://dotnet.microsoft.com/download/dotnet/10.0)
   - Tested on: **Ubuntu 24.04.4 LTS**

## Usage

### Load Assets/AssetBundles

Use **File-Load file** or **File-Load folder**.

When AssetStudio loads AssetBundles, it decompresses and reads it directly in memory, which may cause a large amount of memory to be used. You can use **File-Extract file** or **File-Extract folder** to extract AssetBundles to another folder, and then read.

### Project Root

Use **Options-Set project root...** to choose the root folder used for dependency lookup.

The project root is different from the loaded file or folder:

* **Loaded file/folder** is the subset of assets you want to inspect or export.
* **Project root** is an optional wider search root used when resolving external dependencies, such as streamed texture or mesh data referenced by loaded assets.

For example, you can load only `F:\Unity_Projects\TEST_PROJECT\Assets\Assets_Ripp\scene_objects`, while setting the project root to `F:\Unity_Projects\TEST_PROJECT`. AssetStudio will load only the selected folder, but can still search the project root when exported models need external texture or mesh resources.

### Extract/Decompress AssetBundles

Use **File-Extract file** or **File-Extract folder**.

### Export Assets

use **Export** menu.

### Export Model

Export model from "Scene Hierarchy" using the **Model** menu.

Export Animator from "Asset List" using the **Export** menu.

#### With AnimationClip

Select model from "Scene Hierarchy" then select the AnimationClip from "Asset List", using **Model-Export selected objects with AnimationClip** to export.

Export Animator will export bound AnimationClip or use **Ctrl** to select Animator and AnimationClip from "Asset List", using **Export-Export Animator with selected AnimationClip** to export.

### Export MonoBehaviour

When you select an asset of the MonoBehaviour type for the first time, AssetStudio will ask you the directory where the assembly is located, please select the directory where the assembly is located, such as the `Managed` folder.

#### For Il2Cpp

First, use [Il2CppDumper by Perfare](https://github.com/Perfare/Il2CppDumper) to generate dummy dlls, then when using AssetStudio to select the assembly directory, select the dummy dll folder.

## Build

* Windows: Visual Studio 2022 or newer
* Linux: .NET 10.0 SDK, CMake, and GCC/G++

### One-command Windows publish

Use the publish script to build the managed GUI, build the native DLLs, and copy the native dependencies into the runtime folder expected by AssetStudio:

```powershell
.\scripts\publish-windows.ps1 -Configuration Release -Platform x64
```

The script uses the normal `dotnet publish` output folder:

```text
AssetStudio.Avalonia\bin\Release\net10.0\win-x64\publish
```

Run the executable from inside `publish`:

```text
AssetStudio.Avalonia\bin\Release\net10.0\win-x64\publish\AssetStudio.Avalonia.exe
```

Do not run the executable from `AssetStudio.Avalonia\bin\Release\net10.0\win-x64`; that folder is an intermediate build output and does not receive native dependencies.

The script requires Visual Studio with the C++ workload for native texture builds. Use `-SkipNative` only when you want to publish the managed app without texture decoder native support. Use `-OutputDir <path>` only when you intentionally want a custom publish folder.

### One-command Linux publish

Use the publish script to build the native C++ decoder library, publish the Avalonia GUI application, and bundle the native dependencies (`libTexture2DDecoderNative.so` and `libfmod.so`) into the target publish folder:

```bash
./scripts/publish-linux.sh -c Release
```

The script output will be placed in:

```text
AssetStudio.Avalonia/bin/Release/net10.0/linux-x64/publish
```

Run the executable from inside `publish`:

```bash
./AssetStudio.Avalonia/bin/Release/net10.0/linux-x64/publish/AssetStudio.Avalonia
```

## Native Dependencies

Compressed texture conversion and audio previewing depend on native libraries. The managed application can build and run without them, but texture/audio previews will fail at runtime if the libraries are missing.

### Windows Layout:

```text
AssetStudio.Avalonia.exe
x64\Texture2DDecoderNative.dll
```

* `Texture2DDecoderNative.dll` is required for compressed texture formats such as DXT, BC, ETC, PVRTC, ASTC, and Crunch.

### Linux Layout:

```text
AssetStudio.Avalonia
x64/libTexture2DDecoderNative.so
x64/libfmod.so
```

* `libTexture2DDecoderNative.so` is required for compressed texture formats.
* `libfmod.so` is required for audio playback previewing. A precompiled copy of `libfmod.so` is included in this repository under `AssetStudio.Avalonia/Libraries/x64/` and will be automatically copied to the build output. If you need to obtain or update it manually, you can download the Linux Programmer's API from the [FMOD Downloads](https://www.fmod.com/download) page.

## Open source libraries used

### Texture2DDecoder
* [Ishotihadus/mikunyan](https://github.com/Ishotihadus/mikunyan)
* [BinomialLLC/crunch](https://github.com/BinomialLLC/crunch)
* [Unity-Technologies/crunch](https://github.com/Unity-Technologies/crunch/tree/unity)

