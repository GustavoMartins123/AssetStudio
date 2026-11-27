# AssetStudio.CLI

`AssetStudio.CLI` reads the same Unity files as the GUI, without a window. Use it to inventory a game folder, look at one asset, or export many assets from a script.

> **Availability:** the CLI is in the `main` branch only. Release `v0.20.0` does not include it. The release workflow publishes it as `AssetStudio.CLI-win-x64-<version>.zip` and `AssetStudio.CLI-linux-x64-<version>.tar.gz`, starting with the next release. Until then, build it from source (see below).

## Getting the CLI

### From a release package

Unpack the archive and run `AssetStudio.CLI` (Linux) or `AssetStudio.CLI.exe` (Windows). The package is self-contained, so no .NET runtime is needed. Keep the `x64/` folder next to the executable: it holds the native texture decoder (`Texture2DDecoderNative.dll` or `libTexture2DDecoderNative.so`). Without it, compressed textures can't be converted.

### From source

Build the native texture decoder first (see [Build](../README.md#build) in the README). The CLI build copies it into `x64/` automatically when it exists. Then use either of these:

```bash
dotnet run --project AssetStudio.CLI -c Release -- --help
```

```bash
dotnet publish AssetStudio.CLI/AssetStudio.CLI.csproj -c Release -p:RuntimeIdentifier=linux-x64 --self-contained true -o out/cli
mkdir -p out/cli/x64 && cp Texture2DDecoderNative/build/libTexture2DDecoderNative.so out/cli/x64/
```

On Windows, use `-p:RuntimeIdentifier=win-x64` and copy `Texture2DDecoderNative/bin/x64/Release/Texture2DDecoderNative.dll` into `out/cli/x64/`.

## Commands

Every command takes a file or a folder. Put paths with spaces or accents in quotes, for example `"D:\Jogos\Meu Jogo\Game_Data"`.

| Command | What it does |
| --- | --- |
| `scan <path> [--format text\|json\|jsonl\|csv]` | Lists the files AssetStudio recognizes: bundles, serialized files, resource files, encrypted files. Nothing is decompressed. |
| `list <path> [--type <ClassID>] [--filter <text>] [--format text\|json\|jsonl\|csv]` | Lists the assets inside the files. `--type` takes a class name or number (`Texture2D`, `28`). `--filter` matches part of the asset name, container path or serialized file name, ignoring case. |
| `inspect <path> --path-id <id> [--file <serialized file name>] [--format text\|json]` | Shows one asset: source file, container path, byte range, Unity version and its TypeTree dump. |
| `export <path> --output <dir> [options]` | Converts and writes assets (see below). |
| `--version` | Prints the CLI version and build revision (`release` or `dev+<commit>`). |
| `--help` | Prints the usage. |

### Examples

```text
$ AssetStudio.CLI scan fixture
Root: ...\lz4
Files: 1 | Bytes: 450465 | Bundles: 1 | Serialized: 0 | Resources: 0 | Encrypted: 0 | Other: 0 | Coverage: 100.0%
UnityBundle          450465  ...\lz4\fixture

$ AssetStudio.CLI list fixture --filter tex_rgba --format csv
pathId,type,classId,name,container,sourceFile,byteSize,unityVersion
-8534923478801789395,Texture2D,28,tex_rgba32,assets/fixture/textures/tex_rgba32.png,CAB-5976fa93cd5840156543c9067460ab4e,212,6000.5.10f1

$ AssetStudio.CLI inspect fixture --path-id 6536223139857824099
PathID:       6536223139857824099
Name:         notes
Type:         TextAsset (49)
SourceFile:   CAB-5976fa93cd5840156543c9067460ab4e
Container:    assets/fixture/text/notes.txt
ByteRange:    259744..259820 (76 bytes)
UnityVersion: 6000.5.10f1
HasTypeTree:  True
--- TypeTree Dump ---
...
```

`--format json` prints one JSON document (`{"count": ..., "assets": [...]}` for `list`). `--format jsonl` prints one JSON object per line. `--format csv` prints a header row and one row per item. The `list` fields are `pathId`, `type`, `classId`, `name`, `container`, `sourceFile`, `byteSize` and `unityVersion`.

## Export

```text
AssetStudio.CLI export <path> --output <dir>
    [--type <type>[,<type>...] | all]
    [--image-format png|webp|tga|jpeg|bmp]
    [--model-format glb|gltf|fbx]
    [--assemblies <Managed folder>]
    [--report <json file>]
    [--dry-run]
```

Without `--type`, the CLI exports `Texture2D`, `Texture2DArray`, `TextAsset` and `Mesh`. Use `--type all` for every type below. Type names are not case sensitive.

| Type | Output | Notes |
| --- | --- | --- |
| `Texture2D` | image (`--image-format`, default `png`) | Same decoder as the GUI. Raw `.tex` output is GUI only. |
| `Texture2DArray` | one image per slice, `<name>_slice000.png`, ... | |
| `Sprite` | image cropped from its texture or atlas | |
| `TextAsset` | `.txt` with the original bytes | The GUI can pick other extensions from the content. |
| `Mesh` | `.obj` | Geometry only. For rigs and materials use `Animator`. |
| `Animator` | model (`--model-format`, default `glb`) | Skin and blend shapes included, animations not. Named after its GameObject. |
| `AnimationClip` | Unity `.anim` | Bone paths are resolved from the Transforms, Avatars and Meshes found in `<path>`. Point `<path>` at a folder that also holds the model. |
| `AudioClip` | `.wav` when the clip can be decoded to PCM; otherwise the Vorbis stream as `.ogg`; otherwise the original data | The GUI can also transcode with FFmpeg. The CLI never re-encodes, so FSB5 Vorbis clips come out as `.ogg`. |
| `Font` | `.ttf` or `.otf` | |
| `Shader` | `.shader` (ShaderLab text) | Named after the shader. |
| `MonoBehaviour` | `.json` with the serialized fields; `.txt` dump as a fallback | Builds without TypeTrees need `--assemblies` pointing to the game's `Managed` folder (or IL2CPP dummy DLLs). Otherwise the asset is skipped with a message. |
| `VideoClip` | the embedded video file (`.mp4`, `.webm`, ...) | |

These exporters are the same code the GUI export uses (`AssetStudioUtility`): image decoding, sprite cropping, audio decoding, ShaderLab conversion, `.anim` writer, model converter and glTF/FBX writers.

### Output layout

Files go to `<output>/<Type>/<name>.<ext>`. Names are cleaned for the file system. A second asset with the same name gets `_1`, `_2`, ... The CLI never writes outside `--output`, even when an asset name contains `..` or path separators. Assets without a name are named after their GameObject, script class or shader. They get a generic `<Type>_<PathID>` style name only when nothing else is known.

`--dry-run` prints the planned paths (`[dry-run] Texture2D 123 -> ...`) and writes nothing.

`--report <json file>` writes a structured `ExportReport` JSON file sharing the same schema as GUI exports:

```json
{
  "schemaVersion": 1,
  "operation": "CliExport",
  "status": "Partial",
  "exporterVersion": "0.20.0-dev+4e0dcad",
  "commitHash": "4e0dcad",
  "buildChannel": "dev",
  "exportedCount": 12,
  "skippedCount": 2,
  "failedCount": 1,
  "missingDependencies": [
    "Managed assemblies (--assemblies <Managed folder>)"
  ],
  "diagnostics": [
    {
      "severity": "Warning",
      "outcome": "Skipped",
      "code": "MissingAssembly",
      "assetName": "PlayerConfig",
      "assetType": "MonoBehaviour",
      "pathId": 1024,
      "message": "Skipped MonoBehaviour 1024: no TypeTree in the file (pass --assemblies <Managed folder>)."
    }
  ]
}
```

### What the CLI does not do (GUI only)

Scene export to `.glb`, Unity project / prefab / `.unitypackage` export, C# project export, build comparison, global search and previews. The CLI also does not load Addressables catalog dependencies or external `.assets` files that sit outside `<path>`.

## Exit codes and messages

| Code | Meaning |
| --- | --- |
| `0` | The command ran. For `export`, check the summary line. |
| `1` | Runtime error, for example a path that does not exist or a file that can't be read. |
| `2` | Invalid arguments: unknown command, option or type, or a missing value. |

`export` ends with `Export complete: <n> exported, <n> skipped.` (or `Dry-run complete: <n> planned, ...`). When an asset fails, the line adds `, <n> failed`, and each failure is printed on standard error as `Failed: <Type> <PathID> (<name>): <reason>`. A run with failures still exits with `0`, so scripts that need every asset should check for `failed` in the summary.

Exported paths are printed on standard output (`Exported: <path>`). Errors and skip reasons go to standard error.
