# Feature Support & Roadmap Status

This document gives a plain-language overview of what **AssetStudio** supports today, where partial limits still exist, and which features are still being studied or planned for future updates.

**Where each feature is available:**

- **v0.20.0**: in the latest published release.
- **main**: in the `main` branch only. Build from source (see the [README](README.md#build)) or wait for the next release.

For per-version evidence see [`docs/COMPATIBILITY.md`](docs/COMPATIBILITY.md). For what each export format keeps and loses see [`docs/EXPORT_FORMATS.md`](docs/EXPORT_FORMATS.md). For the command-line tool see [`docs/CLI.md`](docs/CLI.md).

---

## Currently Supported

### Opening Projects & Managing Large Folders
- **Unity versions** (v0.20.0: 3.4 – 6000.4; main: 3.4 – 6000.5): Reads standard asset files and AssetBundles, from both Mono and IL2CPP builds. On main, Unity 6000.5 builds made with extracted TypeTrees (`ContentBuildFlags.ExtractTypeTree`) are read together with their TypeTree data archive when it is opened with the bundles.
- **Project Manager** (v0.20.0): Save folders as named projects with remembered settings, icons, and fast reopen caches, without modifying or copying the original files.
- **Safe / Lazy Mode** (v0.20.0): Opens very large folders in lightweight batches, so memory use stays low and files are loaded only when previewed or exported.
- **Automatic dependency lookup** (v0.20.0): Resolves linked textures, materials, and models across folders using the configured **Project Root** and Unity Addressables catalogs (`catalog.json` / `catalog.bin`).
- **More containers** (main): Zstd-compressed bundles, several bundles concatenated in one file, and Unity files read directly from inside `.apk` / `.obb` / `.zip` packages.
- **Decryption profiles** (v0.20.0, extended on main): Supports user-supplied keys and generic stream decryption profiles. Main adds automatic algorithm detection, AES-CTR / Rijndael-CBC / header-mask profiles, and key capture from a running game on Windows.

### Browsing, Inspection & Navigation
- **Asset List & Asset Classes** (v0.20.0): Fast search, sorting, type filters, and class overview.
- **Scene Hierarchy & GameObject Inspector** (v0.20.0): Browse object trees and click any object to inspect its position, rotation, scale, active state, and attached components. Clickable links jump straight to related meshes, materials, scripts, or assets.
- **MonoBehaviour / ScriptableObject Viewer** (v0.20.0): Inspect script data in a searchable tree view or raw JSON view, with clickable links on asset references.
- **Script Code Viewer, Assembly Explorer & Ghidra Export** (main): Open the **Script** tab, click **View C#** on a MonoBehaviour, or select a `MonoScript` to view C# code inside AssetStudio. For Mono builds, the code is real C# decompiled from `Managed/` assemblies. For IL2CPP builds, AssetStudio shows class definitions, field offsets and method addresses read from `dump.cs`, `script.json`, `DummyDll/`, or directly from `global-metadata.dat` + `GameAssembly.dll` (metadata v24–v31, v39 for Unity 6000.3, v107 for Unity 6000.5). IL2CPP method bodies are native code and are not recovered. You can export a symbol table and a ready-to-run Python script for **Ghidra**.
- **References Tab & Navigation History** (v0.20.0): See what uses an asset and what it depends on, and step back or forward through visited assets (`Alt+Left` / `Alt+Right` or header buttons).
- **Global Search** (main): Search the whole project index by name, path, PathID, GUID, type, or file, with regular expressions. It can also search inside `TextAsset` contents, up to 500 TextAssets of at most 2 MiB each per search. It does not search MonoBehaviour fields.
- **Build Comparison** (main): Compare two builds or updates and list added, removed, modified, and moved files and assets. By default, assets are matched by name, type, size and container, and assets whose metadata is equal are reported as "content not verified". Turn on **Verify content** to compare the real bytes, including the texture, audio, mesh and video data stored in `.resS` / `.resource` files. Export the result as JSON, CSV or Markdown.
- **Animator Graph** (main): View the states and transitions of `AnimatorController` assets.
- **Light & Dark Theme** (v0.20.0): Shared interface theme that adapts across cards, inspectors, overlays, and media controls.

### Previews & Playback
- **Images, Sprites & Fonts** (v0.20.0): Texture preview with red/green/blue/alpha channel toggles, cropped sprites, standard fonts, bitmap font atlases, and TextMeshPro font atlases. Main adds a mipmap level selector.
- **3D Models, Avatars & Animations** (v0.20.0): Interactive 3D preview with textures, wireframe/solid modes, skeleton bones and X-ray overlay, multi-part assembly (**Full Object** toggle), material color tinting, and synchronized **Play / Pause / Stop / Replay** controls with a frame scrubber.
- **2D Sprite Animations** (v0.20.0): Animated playback with frame scrubbing and pause/stop controls.
- **Audio & Video** (v0.20.0): Built-in audio and video playback with **Play / Pause / Stop**, loop toggle, volume slider, synchronized timers, and seeking.
- **Text & Embedded Media** (v0.20.0): Plain text viewing plus automatic detection and preview of common image, audio, video, and font files stored inside text assets.
- **Shader & Material Preview** (v0.20.0, Windows): Visual Direct3D 11 preview for many classic Built-in Unity shaders and effects.

### Exporting
- **3D Models & Animations** (v0.20.0): Export to **glTF Binary (`.glb`, default)**, **glTF Separate (`.gltf`)**, **FBX (`.fbx`)**, **OBJ (`.obj`)**, and Unity `.anim` files, including direct export from the 3D preview and **Batch Export Animations**.
- **Full Scene Export to `.glb`** (main): Export an entire Unity scene (`levelN` or scene bundle) or a selected GameObject subtree as one `.glb` / `.gltf` file. The full Transform hierarchy is kept, and repeated meshes are reused through glTF instancing.
- **Unity Project, Prefab & `.unitypackage` Export** (main): Rebuild scenes and prefabs as Unity YAML files with deterministic `.meta` GUIDs, exporting all scene/prefab/terrain meshes as binary `.fbx` via the shared `ModelConverter` pipeline (preserving multi-submeshes, UVs, blend shapes, and 1:1 `SkinnedMeshRenderer` bone/bindpose alignment), colliders (with optional auxiliary `.obj` meshes), LOD groups, script-spawn attachments, and full streaming `MonoBehaviour` YAML without silent truncation. Validated by automated roundtrip import tests in Unity Editor `6000.3.18f1` (`UnityEditorRoundtripTests`).
- **C# Project Export** (main): Export scripts as a `.sln` / `.csproj` project. Mono builds give decompiled code; IL2CPP builds give stubs with fields, offsets and method addresses.
- **Images & Sprites** (v0.20.0; `webp` and `Texture2DArray` slices on main): Export to `png`, `tga`, `jpeg`, `bmp`, and `webp`. Main also exports HDR textures to OpenEXR / TIFF, all mipmap levels, and sprite atlas metadata.
- **Audio & Video** (v0.20.0): Export audio to `wav`, `ogg`, or the original format (`mp3`, `m4a`, `fsb`, ...), including FSB5 Vorbis decoding, and export video clips directly.
- **Other Assets** (v0.20.0): Export fonts (`ttf`/`otf`), shaders, text files, MonoBehaviour JSON, raw binary data, text dumps, and XML.
- **Bundle Extraction** (v0.20.0): Decompress and extract files from AssetBundles to disk.
- **Command-line tool** (main): `AssetStudio.CLI` scans, lists, inspects, identifies its build revision (`--version`), and exports without the GUI: textures, texture arrays, sprites, text, meshes, Animator models (GLB/glTF/FBX), animation clips, audio, fonts, shaders, MonoBehaviour JSON and video, with structured JSON export reports (`--report <path>`). See [`docs/CLI.md`](docs/CLI.md).

---

## Partial Support or Known Limits (Still Being Improved)

- **Shader & Material Visual Preview**: Many classic Built-in shaders render visually on Windows. Newer scriptable render pipeline shaders (URP/HDRP), heavily customized shaders, and Linux systems fall back to basic shading or text view.
- **Missing External Files**: When a model, texture, or scene depends on optional or missing bundles that are not in the folder, only the files actually on disk can be previewed or exported.
- **Protected or Third-Party Formats**: Files using undocumented encryption without a user profile are not opened. Audio/video stored in external middleware banks (FMOD `.bank`, Wwise, CRIWARE) is not extracted.
- **Stripped Script Metadata**: Viewing fields inside custom `MonoBehaviour` assets works automatically when TypeTrees are present. When they are stripped, the game's assemblies (or dummy assemblies for IL2CPP projects) are still required.
- **Unity Project Export** (main): Custom/URP/HDRP shaders fall back to Built-in `Standard` with albedo/normal texture aliasing (custom HLSL/ShaderGraph logic is not translated), and `Terrain` is converted to a mesh `.fbx` + baked splatmap rather than a native `.asset` heightmap. If optional safety limits are configured on `MonoBehaviour` YAML serialization, `ExportManifest.json` marks `status: "Partial"` and lists every truncated field in `truncationDiagnostics`.
- **Platforms**: Windows x64 and Linux x64 are released. Windows x86, macOS and ARM64 are not released or verified (see [`docs/COMPATIBILITY.md`](docs/COMPATIBILITY.md#where-assetstudio-runs)).

---

## Under Study & Planned (Not Available Yet)

1. **Safe Asset Editing & Repacking**: Replacing textures, text files, or script fields with automatic `.bak` backups, repacking bundle files, and updating Addressables catalog checksums.
2. Evaluating: Live2D export, Nintendo Switch texture layouts, Tuanjie engine files, Unity 7000.x.
