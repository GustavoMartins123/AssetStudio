# Feature Support & Roadmap Status

This document gives a plain-language overview of what **AssetStudio** supports today, where partial limits still exist, and which features are still being studied or planned for future updates.

---

## Currently Supported (Available Now)

### Opening Projects & Managing Large Folders
- **Unity versions**: Reads standard asset files and AssetBundles from Unity **3.4 through 6000.4** (both Mono and IL2CPP builds).
- **Project Manager**: Save folders as named projects with remembered settings, icons, and fast reopen caches without modifying or copying the original files.
- **Safe / Lazy Mode**: Opens very large folders in lightweight batches so memory usage stays low and files are loaded only when previewed or exported.
- **Automatic dependency lookup**: Resolves linked textures, materials, and models across folders using the configured **Project Root** and Unity Addressables catalogs (`catalog.json` / `catalog.bin`).
- **Custom decryption profiles**: Supports user-supplied decryption keys and generic stream decryption profiles for folders that require them.

### Browsing, Inspection & Navigation
- **Asset List & Asset Classes**: Fast search, sorting, type filters, and class overview.
- **Scene Hierarchy & GameObject Inspector**: Browse object trees and click any object to inspect its position, rotation, scale, active state, and attached components, with clickable links to jump straight to related meshes, materials, scripts, or assets.
- **MonoBehaviour / ScriptableObject Viewer**: Inspect script data in a searchable tree view or raw JSON view, with clickable links on asset references so you can jump directly to referenced textures, audio clips, meshes, materials, or prefabs.
- **Built-in Script Code Viewer & Ghidra Export**: Open the **Script** tab (or click **View C#** on a MonoBehaviour or select a `MonoScript`) to view reconstructed C# source code directly inside AssetStudio — decompiling real C# from Mono `Managed/` assemblies and slicing class definitions + method addresses from IL2CPP `dump.cs`, `script.json`, or `DummyDll/` folders, with in-code search/line filtering, copy/save `.cs`, and one-click export of a symbol table and ready-to-run Python script for **Ghidra**.
- **References Tab & Navigation History**: See what uses an asset and what it depends on, and step back or forward through visited assets (`Alt+Left` / `Alt+Right` or header buttons).
- **Light & Dark Theme**: Shared interface theme that adapts across cards, inspectors, overlays, and media controls.

### Previews & Playback
- **Images, Sprites & Fonts**: Texture preview with red/green/blue/alpha channel toggles, cropped sprites, standard fonts, bitmap font atlases, and TextMeshPro font atlases.
- **3D Models, Avatars & Animations**: Interactive 3D preview with textures, wireframe/solid modes, skeleton bones and X-ray overlay, multi-part assembly (**Full Object** toggle), material color tinting, and synchronized **Play / Pause / Stop / Replay** controls with an interactive frame scrubber.
- **2D Sprite Animations**: Animated playback with frame scrubbing and pause/stop controls.
- **Audio & Video**: Built-in audio and video playback with unified **Play / Pause / Stop**, loop toggle, volume slider, accurate synchronized timers, and smooth seeking.
- **Text & Embedded Media**: Plain text viewing plus automatic detection and preview of common image, audio, video, and font files stored inside text assets.
- **Shader & Material Preview (Windows)**: Visual Direct3D 11 preview for many classic Built-in Unity shaders and effects on Windows.

### Exporting
- **3D Models & Animations**: Export to **glTF Binary (`.glb`, default)**, **glTF Separate (`.gltf`)**, **FBX (`.fbx`)**, **OBJ (`.obj`)**, and Unity `.anim` files, including direct export from the 3D preview and **Batch Export Animations** to export multiple animations with one model.
- **Full Scene Export to `.glb` (with Instancing)**: Export an entire Unity scene (`levelN` or scene bundle) or selected GameObject subtree as one `.glb` / `.gltf` file, preserving the full Transform hierarchy and automatically reusing repeated meshes and geometry buffers via glTF 2.0 instancing.
- **Images & Sprites**: Export to `png`, `tga`, `jpeg`, and `bmp`.
- **Audio & Video**: Export audio to `wav`, `ogg`, `mp3`, `m4a`, or `fsb` (including FSB5 Vorbis decoding), and export video clips directly.
- **Other Assets**: Export fonts (`ttf`/`otf`), shaders, text files, MonoBehaviour JSON, raw binary data, text dumps, and XML.
- **Bundle Extraction**: Decompress and extract files from AssetBundles to disk.

---

## Partial Support or Known Limits (Still Being Improved)

- **Shader & Material Visual Preview**: Many classic Built-in shaders render visually on Windows, while newer scriptable render pipeline shaders (URP/HDRP), heavily customized shaders, and Linux systems fall back to basic shading or text view.
- **Missing External Files**: When a model, texture, or scene depends on optional or missing bundles that are not present in the folder, only the files actually available on disk can be previewed or exported.
- **Protected or Third-Party Formats**: Files using undocumented proprietary encryption without a user profile, or audio/video stored inside external middleware banks (such as FMOD `.bank`, Wwise, or CRIWARE containers), are not extracted automatically.
- **Stripped Script Metadata**: Viewing fields inside custom `MonoBehaviour` assets works automatically when type trees are present; when stripped, external assembly files (or dummy assemblies for IL2CPP projects) are still required.

---

## Under Study & Planned (Not Available Yet)

The capabilities below are actively being designed or studied for upcoming releases and are **not yet implemented**:

1. **Global Content Search & Version Comparison**: Searching text across the contents of an entire project and comparing two builds or updates side by side to list added, removed, and modified assets.
2. **Safe Asset Editing & Repacking**: Replacing textures, text files, or script fields with automatic `.bak` backups, repacking bundle files, and updating Addressables catalog checksums.
