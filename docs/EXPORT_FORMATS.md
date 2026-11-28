# Export formats: what is kept and what is lost

No export format holds everything a Unity asset had. This page lists, per format, what comes out, what is approximated and what is dropped, so you can pick the right one. All formats and options below are included in release `v0.20.1`.

## Images

| Source | Output | Notes |
| --- | --- | --- |
| `Texture2D`, `Sprite`, `Texture2DArray` slices | `png`, `tga`, `jpeg`, `bmp`, `webp` (8 bits per channel) | Flipped to the usual top-left origin. Normal maps stored as DXT5nm/BC5 are unpacked to a regular blue tangent-space map. HDR and float formats are clamped to 0–1 in these formats. `jpeg` and lossy `webp` lose detail. |
| HDR textures (`RGBAHalf`, `RGBAFloat`, BC6H, ...) | OpenEXR or 32-bit float TIFF (from the texture preview) | Keeps values above 1. |
| Any texture, "convert" off | raw `.tex` data | Exactly the bytes Unity stored. |
| Sprites | image cut from the texture or atlas, plus a Unity `.meta` file | |

Mipmaps: only the top level is exported, except with the mipmap export of the texture preview.

## Audio

| Source | Output | Notes |
| --- | --- | --- |
| PCM clips | `.wav` | Byte-exact. |
| ADPCM clips | `.wav` | Decoded. FMOD pads the clip to whole 64-sample blocks, so a few silent samples can appear at the end. Unity does the same. |
| Vorbis clips (FSB5) | `.wav` when the GUI's audio conversion is on (decoded with FFmpeg), otherwise `.ogg` | The `.ogg` holds the original Vorbis stream without re-encoding. |
| MP3 / AAC clips | original data (`.mp3`, `.m4a`) | Not re-encoded. |
| FMOD `.bank`, Wwise, CRIWARE files outside Unity assets | – | Not supported. |

## 3D models

| | glTF (`.glb` / `.gltf`) | FBX (`.fbx`) | OBJ (`.obj`) |
| --- | --- | --- | --- |
| Hierarchy and transforms | yes | yes | no, one mesh per file |
| Normals, UVs, vertex colors | yes | yes, up to 8 UV sets | normals and first UV set |
| Submeshes | one primitive per submesh | one material slot per submesh | one group per submesh |
| Skinning and bind poses | yes | yes | no |
| Blend shapes | yes (morph targets) | yes | no |
| Animations | yes | yes | no |
| Materials | PBR approximation of the Unity material. Base color, normal, metallic/smoothness, occlusion and emission maps are mapped when they can be identified. Extensions used when needed: `KHR_materials_unlit`, `KHR_materials_transmission`, `KHR_materials_ior`, `KHR_texture_transform`, `KHR_node_visibility`, `KHR_lights_punctual` | textures linked by material | none |
| Repeated meshes in a scene | reused (instancing) in scene export | – | – |

Unity shaders are not converted. A custom or URP/HDRP shader becomes the closest PBR material, and the result can look different from the game. During glTF/GLB export, AssetStudio records explicit per-material conversion diagnostics (`MaterialDiagnostics` in `GltfExportStatistics`, scene `.manifest.json`, and CLI `--report` / `--format json`) detailing which mode was applied (`StandardPbr`, `Unlit`, `PbrApproximation`, or `BasicFallback`), which PBR channels and glTF extensions were preserved, and which shader approximations, unmapped texture slots, or missing textures occurred. FBX export was checked by re-importing in Blender 5.3. glTF export is checked structurally with `Gltf2StructuralValidator` (hierarchy, units, UVs, alpha modes, skinning, morph targets, and animations) and via the Blender round-trip tests when Blender is installed (see [COMPATIBILITY](COMPATIBILITY.md#how-to-reproduce)).

## Animation and data

| Source | Output | Notes |
| --- | --- | --- |
| `AnimationClip` | Unity `.anim` (YAML) with `.meta` | Bone paths and blend shape names are rebuilt from the loaded Transforms, Avatars and Meshes. Curves whose path can't be found keep the numeric hash. |
| `AnimatorController`, `AnimatorOverrideController` | `.controller`, `.overrideController` | |
| `MonoBehaviour`, `ScriptableObject` | `.json` with the serialized fields | Needs TypeTrees or the game's assemblies. `[SerializeReference]` objects are written with their class name. |
| `TextAsset`, `Font`, `VideoClip`, `MovieTexture` | original bytes | Not converted. |
| `Shader` | ShaderLab text (`.shader`) | Rebuilt from the compiled shader. Compiled programs stay compiled (disassembly where available). This is not the original source. |

## Unity project, prefab and `.unitypackage`

Rebuilds scenes and prefabs as Unity YAML 1.1 files with deterministic `.meta` GUIDs (compatible with both Unity 2019–2022 and Unity 6 `internalIDToNameTable`):

- textures become `.png` (normal maps receive `textureType: 1`, `sRGBTexture: 0`) via bounded parallel encoding;
- **all scene meshes, rigged meshes, and converted terrains become binary `.fbx`** through the shared `ModelConverter` + `ModelExporter.ExportFbx` pipeline (preserving multi-submeshes, UV0–UV7, blend shapes, and exact 1:1 `SkinnedMeshRenderer` active bone/bindpose alignment);
- colliders (`BoxCollider`, `SphereCollider`, `CapsuleCollider`, `MeshCollider`) and `LODGroup` components are serialized natively, with options to filter by target LOD level and emit auxiliary `Assets/Colliders/*.obj` meshes;
- materials keep their original serialized properties while aliasing URP/ShaderGraph base color and normal textures (`_BaseMap`/`_BaseColorMap` → `_MainTex`, `_BaseColor` → `_Color`, normal → `_BumpMap`) when falling back to Built-in `Standard`;
- `MonoBehaviour` YAML fields stream collections, dictionaries, and `byte[]` arrays incrementally **with no arbitrary element cutoff by default**; if optional safety guardrails (`MaxYamlSerializationDepth`, `MaxYamlCollectionElements`, `MaxYamlByteArrayLength`) are configured and hit, `ExportManifest.json` sets `status: "Partial"`, `isPartial: true`, and records every affected asset, field path, limit, and omitted count in `truncationDiagnostics` and `diagnostics`;
- script-spawned prefab subtrees discovered by `SceneScriptSpawnAnalyzer` attach cleanly to their anchor Transforms just like Scene `.glb` export;
- optional C# script export writes `Assets/Scripts/` with bit-for-bit matching deterministic `MonoScript` GUIDs;
- `.unitypackage` archives use classic POSIX `USTAR` format (zero PAX headers) with atomic writes.

Exact vs. approximate conversions:

- **Exact / preserved:** Transform hierarchy, active/inactive flags, layers/tags (`TagManager.asset`), deterministic GUIDs, multi-submesh topology, active skinning bones & bindposes, collider dimensions, `LODGroup` transitions, and `MonoBehaviour` serialized values.
- **Approximate / not reconstructed 1:1:** Custom/URP/HDRP shaders fall back to Built-in `Standard` (custom HLSL/ShaderGraph math is not converted); `Terrain` exports as a converted `.fbx` mesh + baked splatmap texture rather than a native `.asset` terrain; IL2CPP scripts export as stubs without native method bodies.
- **Validation:** Verified via the automated Unity Editor roundtrip test suite (`UnityEditorRoundtripTests`) on Unity `6000.3.18f1` and manual batch imports of populated multi-submesh/rigged scenes.

## C# scripts

| Source | Output | What you get |
| --- | --- | --- |
| Mono builds (`Managed/*.dll`) | C# project (`.sln`, `.csproj`, `.cs`) | Decompiled source with method bodies. Close to the original but not identical: comments, local names, and some compiler-lowered syntax differ. |
| IL2CPP builds (`global-metadata.dat` + `GameAssembly.dll`, or `DummyDll/`, `dump.cs`, `script.json`) | C# project in stub mode (`Il2CppStubs`) | Types, fields with offsets, method signatures, and native addresses (RVAs). Method bodies are native code and are **not** recovered. |
| IL2CPP builds containing `*_BackUpThisFolder_ButDontShipItWithYourGame/Managed` | Full Mono decompilation | AssetStudio automatically detects and prefers backup Managed DLLs when present. |
| Either | Ghidra symbol table and Python script | Names and addresses to label the native binary in Ghidra. No decompilation happens inside AssetStudio. |

### Decompiler fallbacks, failures, and unresolved references

- **Per-type fallback & stubs:** When the default ILSpy pass produces syntax anomalies (unbalanced braces, compiler-generated tokens, stripped `set;`-only auto-properties `CS8051`), the conservative fallback re-decompiles that type (`C# 7.3`, tuple syntax disabled). If a type still cannot be decompiled, it is emitted as a commented stub file and counted in `export-report.json` (`failedTypesCount`) so the remaining assembly still exports.
- **Stripped IL2CPP attributes:** Optional sanitization comments out `[CreateAssetMenu(...)]` properties stripped from `UnityEngine.CoreModule.dll` to avoid `CS0617` errors while preserving the original attribute as a comment.
- **Unresolved references:** Engine and third-party DLLs found next to the source are copied into `_References/` (with transitive references). Missing assemblies are listed explicitly in `export-report.json` (`missingReferences`) and in the completion summary.

## Small validated output examples & verification steps

Use these quick checks to verify exported files independently:

1. **Images / HDR (`Texture2D`, `Sprite`):**
   - Expected files: `Texture2D/<name>.png` (8-bit RGBA) or `<name>.exr` / `<name>.tif` (float32 HDR).
   - Verify: confirm image dimensions and alpha channel in any viewer; for `.exr`, inspect pixel values `> 1.0` in Blender / `oiiotool` or check the `hdr` unit tests (`Block5SpritesHdrMipmapsTests`).
2. **Audio (`AudioClip`):**
   - Expected files: `AudioClip/<name>.wav` (PCM/ADPCM) or `<name>.ogg` (FSB5 Vorbis without re-encoding).
   - Verify: header begins with `RIFF....WAVE` (`.wav`) or `OggS` (`.ogg`); check duration/sample rate with `ffprobe` or playback in VLC.
3. **3D Models (`glb`, `fbx`, `obj`):**
   - Expected files: `<name>.glb` (starts with `glTF` magic `0x46546C67`) or `<name>.fbx` (starts with `Kaydara FBX Binary` + `1970-01-01 10:00:00:000`).
   - Verify: open in Blender (`File → Import`) or Unity Editor; check submesh material slot count, UV0/UV2 channels, scale (1:1), and skeleton bone deformation.
4. **Unity Project / Prefab / `.unitypackage`:**
   - Expected files: `Assets/Scenes/<Scene>.unity`, `Assets/Meshes/*.fbx`, `Assets/Materials/*.mat`, `Assets/Textures/*.png`, matching `.meta` files, and `ExportManifest.json`.
   - Verify: import the `.unitypackage` into Unity (**Assets → Import Package → Custom Package...**), confirm `0` Console `ImportFBX Errors` and `0` missing scripts/meshes, and check `ExportManifest.json` (`status`, `isPartial`, `missingAssets`, `truncationDiagnostics`).
5. **CLI structured report (`--report`):**
   - Run: `AssetStudio.CLI export <path> --output out --type all --report out/report.json`
   - Verify: inspect `out/report.json` for `status` (`Completed` / `Partial` / `Failed`), `exportedCount`, `skippedCount`, `failedCount`, `missingDependencies`, and per-asset `diagnostics`.

## Reports

AssetStudio uses a shared `ExportReport` contract (`AssetStudioUtility/ExportReport.cs`) across GUI batch exports, Unity Project/Prefab exports (`ExportManifest.json`), and `AssetStudio.CLI --report <json>`:

- **Outcomes:** `Completed`, `Partial` (some assets exported while others failed, were truncated by a configured guardrail, or had missing dependencies), `Failed`, or `Cancelled`.
- **Metadata:** `exporterVersion`, `commitHash`, `buildChannel` (`release` vs. `dev`), `elapsedMs`, and export `options` for reproducibility.
- **Diagnostics:** Every skipped, partial, or failed item records `severity`, `outcome`, `code`, `assetName`, `assetType`, `sourceFile`, `pathId`, `fieldOrResource`, and `message`.
- GUI batch exports continue to write `errors.txt` (now stamped with version and commit) next to the output when errors occur; the CLI prints the `Export complete: <n> exported, <n> skipped[, <n> failed].` summary and never counts skipped or failed assets as exported.
