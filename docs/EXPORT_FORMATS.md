# Export formats: what is kept and what is lost

No export format holds everything a Unity asset had. This page lists, per format, what comes out, what is approximated and what is dropped, so you can pick the right one. Items marked **main** were added after release `v0.20.0`.

## Images

| Source | Output | Notes |
| --- | --- | --- |
| `Texture2D`, `Sprite`, `Texture2DArray` slices | `png`, `tga`, `jpeg`, `bmp`, `webp` (8 bits per channel) | Flipped to the usual top-left origin. Normal maps stored as DXT5nm/BC5 are unpacked to a regular blue tangent-space map. HDR and float formats are clamped to 0–1 in these formats. `jpeg` and lossy `webp` lose detail. |
| HDR textures (`RGBAHalf`, `RGBAFloat`, BC6H, ...) | OpenEXR or 32-bit float TIFF (**main**, from the texture preview) | Keeps values above 1. |
| Any texture, "convert" off | raw `.tex` data | Exactly the bytes Unity stored. |
| Sprites | image cut from the texture or atlas, plus a Unity `.meta` file | |

Mipmaps: only the top level is exported, except with the mipmap export of the texture preview (**main**).

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

Unity shaders are not converted. A custom or URP/HDRP shader becomes the closest PBR material, and the result can look different from the game. FBX export was checked by re-importing in Blender 5.3. glTF export is checked by the Blender round-trip tests when Blender is installed (see [COMPATIBILITY](COMPATIBILITY.md#how-to-reproduce)).

## Animation and data

| Source | Output | Notes |
| --- | --- | --- |
| `AnimationClip` | Unity `.anim` (YAML) with `.meta` | Bone paths and blend shape names are rebuilt from the loaded Transforms, Avatars and Meshes. Curves whose path can't be found keep the numeric hash. |
| `AnimatorController`, `AnimatorOverrideController` | `.controller`, `.overrideController` | |
| `MonoBehaviour`, `ScriptableObject` | `.json` with the serialized fields | Needs TypeTrees or the game's assemblies. `[SerializeReference]` objects are written with their class name. |
| `TextAsset`, `Font`, `VideoClip`, `MovieTexture` | original bytes | Not converted. |
| `Shader` | ShaderLab text (`.shader`) | Rebuilt from the compiled shader. Compiled programs stay compiled (disassembly where available). This is not the original source. |

## Unity project, prefab and `.unitypackage` (main, experimental)

Rebuilds scenes and prefabs as Unity YAML files with `.meta` files and stable GUIDs:

- textures become `.png`;
- static meshes become `.obj`, while skinned meshes and terrain become `.fbx`;
- colliders and LOD groups are written as components, with an option to keep only one LOD level;
- `ExportManifest.json` lists what was exported and which dependencies were missing.

Limits:

- The result has not yet been validated by importing it into the Unity Editor.
- Scripts are referenced but not recreated.
- Large `MonoBehaviour` values can be cut in the YAML output (backlog item COR-01). Check the export report for warnings.

## C# scripts (main)

| Source | Output | What you get |
| --- | --- | --- |
| Mono builds (`Managed/*.dll`) | C# project (`.sln`, `.csproj`, `.cs`) | Decompiled source with method bodies. It is close to the original but not identical: comments, local names and some syntax are lost. |
| IL2CPP builds (`global-metadata.dat` + `GameAssembly.dll`, or `DummyDll/`, `dump.cs`, `script.json`) | C# project in stub mode (`Il2CppStubs`) | Types, fields with offsets, method signatures and native addresses (RVAs). Method bodies are native code and are **not** recovered. |
| Either | Ghidra symbol table and Python script | Names and addresses to label the native binary in Ghidra. No decompilation happens inside AssetStudio. |

## Reports

GUI exports write `errors.txt` next to the output when an asset fails. The CLI prints a summary with exported, skipped and failed counts (see [CLI](CLI.md#exit-codes-and-messages)). An asset that is skipped or fails is never counted as exported.
