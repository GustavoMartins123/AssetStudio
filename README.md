# AssetStudio
[![Build status](https://ci.appveyor.com/api/projects/status/rnu7l90422pdewx4?svg=true)](https://ci.appveyor.com/project/Perfare/assetstudio/branch/master/artifacts)

**None of the repo, the tool, nor the repo owner is affiliated with, or sponsored or authorized by, Unity Technologies or its affiliates.**

AssetStudio is a tool for exploring, extracting and exporting assets and assetbundles.

## Features
* Support version:
  * 3.4 - 2022.1
* Support asset types:
  * **Texture2D** : convert to png, tga, jpeg, bmp
  * **Sprite** : crop Texture2D to png, tga, jpeg, bmp
  * **AudioClip** : mp3, ogg, wav, m4a, fsb. support convert FSB file to WAV(PCM)
  * **Font** : ttf, otf
  * **Mesh** : obj
  * **TextAsset**
  * **Shader**
  * **MovieTexture**
  * **VideoClip**
  * **MonoBehaviour** : json
  * **Animator** : export to FBX file with bound AnimationClip

## Requirements

- AssetStudio.net472
   - [.NET Framework 4.7.2](https://dotnet.microsoft.com/download/dotnet-framework/net472)
- AssetStudio.net10
   - [.NET Desktop Runtime 10](https://dotnet.microsoft.com/download/dotnet/10.0)


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

First, use my another program [Il2CppDumper](https://github.com/Perfare/Il2CppDumper) to generate dummy dll, then when using AssetStudio to select the assembly directory, select the dummy dll folder.

## Build

* Visual Studio 2022 or newer
* **AssetStudioFBXNative** uses [FBX SDK 2020.2.1](https://www.autodesk.com/developer-network/platform-technologies/fbx-sdk-2020-2-1), before building, you need to install the FBX SDK and modify the project file, change include directory and library directory to point to the FBX SDK directory

### One-command Windows publish

Use the publish script to build the managed GUI, build the native DLLs, and copy the native dependencies into the runtime folder expected by AssetStudio:

```powershell
.\scripts\publish-windows.ps1 -Configuration Release -Platform x64
```

The script uses the normal `dotnet publish` output folder:

```text
AssetStudioGUI\bin\Release\net10.0-windows\win-x64\publish
```

Run the executable from inside `publish`:

```text
AssetStudioGUI\bin\Release\net10.0-windows\win-x64\publish\AssetStudioGUI.exe
```

Do not run the executable from `AssetStudioGUI\bin\Release\net10.0-windows\win-x64`; that folder is an intermediate build output and does not receive native dependencies.

The script is equivalent to running:

```powershell
dotnet publish AssetStudioGUI\AssetStudioGUI.csproj -f net10.0-windows -c Release -r win-x64 --self-contained true
```

and then copying the native DLLs into the publish folder.

If you only need texture export and do not have the Autodesk FBX SDK installed yet, skip the FBX native project:

```powershell
.\scripts\publish-windows.ps1 -Configuration Release -Platform x64 -SkipFbxNative
```

The script requires Visual Studio with the C++ workload for native builds. Use `-SkipNative` only when you want to publish the managed app without texture decoder or FBX native support. Use `-OutputDir <path>` only when you intentionally want a custom publish folder.

### Native Dependencies

Texture conversion and FBX export depend on native DLLs. The managed application can build without them, but those features will fail at runtime if the DLLs are missing.

Expected runtime layout on 64-bit Windows:

```text
AssetStudioGUI.exe
x64\Texture2DDecoderNative.dll
x64\AssetStudioFBXNative.dll
x64\libfbxsdk.dll
```

`Texture2DDecoderNative.dll` is required for compressed texture formats such as DXT, BC, ETC, PVRTC, ASTC and Crunch. `AssetStudioFBXNative.dll` and `libfbxsdk.dll` are required for FBX export.

## Open source libraries used

### Texture2DDecoder
* [Ishotihadus/mikunyan](https://github.com/Ishotihadus/mikunyan)
* [BinomialLLC/crunch](https://github.com/BinomialLLC/crunch)
* [Unity-Technologies/crunch](https://github.com/Unity-Technologies/crunch/tree/unity)
