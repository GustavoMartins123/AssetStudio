# Decryption Scope & Special Formats

AssetStudio focuses on standard Unity containers (`UnityFS`, `UnityArchive`, `UnityRaw`, `UnityWeb`, plain `SerializedFile`, and stored `APK`/`OBB`/`ZIP` archives). This document clarifies what is supported for encrypted/obfuscated files, how user-supplied profiles work, and which third-party container formats remain outside scope.

## 1. Capability Levels

Detection, decryption, parsing, and accurate export are distinct stages:

| Stage | Meaning |
| --- | --- |
| **Detects** | `ProjectScanner` and `FileReader` recognize that a file is not a standard plaintext Unity container and classify it as `Encrypted` or `Other` instead of crashing. |
| **Accepts user profile / key** | A user-supplied declarative JSON profile (`DecryptionProfile`) or custom `IStreamTransformProvider` plugin transforms the byte stream on read. |
| **Opens known variant** | After stream transformation, `FileReader` validates a known Unity signature (`UnityFS`, `UnityWeb`, `UnityRaw`, `UnityArchive`, or valid serialized header) and parses objects. |
| **Exports content accurately** | Decoded textures, audio, meshes, and data assets pass content-level checks (verified in synthetic CI tests for every supported cipher mode). |

AssetStudio does **not** claim universal automatic decryption of arbitrary modified engines or proprietary anti-tamper schemes.

## 2. User-Supplied Decryption Profiles (JSON)

Declarative profiles let you describe standard stream transforms without writing code.

### Profile locations

- **Windows:** `%LOCALAPPDATA%\AssetStudio\DecryptionProfiles\*.json`
- **Linux:** `~/.local/share/AssetStudio/DecryptionProfiles/*.json`
- **Override data root:** set environment variable `ASSETSTUDIO_DATA_DIR`
- **CLI options:** `--profiles <file|directory>` (repeatable) and `--no-local-profiles`

### Supported algorithms (`algorithm`)

| Value | Description |
| --- | --- |
| `xor` | Repeating-key XOR (`xorKeyHex`, `keyHex`, or `keyUtf8`). |
| `aes-cbc` | AES-CBC (`keyHex`/`keyUtf8`, `ivHex`/`ivUtf8`/`ivFromStreamPrefix`/`ivFromFileNameHash`, `padding`: `none` or `pkcs7`). |
| `aes-ecb` | AES-ECB (`keyHex`/`keyUtf8`, `padding`: `none` or `pkcs7`). |
| `aes-ctr` | AES-CTR (`keyHex`/`keyUtf8`, `ivHex`/`nonceHex`/`ivFromStreamPrefix`, `counterStart`, `counterLittleEndian`). |
| `chacha20` | ChaCha20 stream cipher (`keyHex`, `nonceHex`/`ivFromStreamPrefix`, `counterStart`). |
| `rijndael-cbc` | Rijndael-CBC with configurable block size (`128`, `192`, or `256` bits). |
| `chacha-512b-xor` | 512-byte block stream XOR keyed via Addressables catalog (`catalogPath`) or explicit token (`block512Token`). |
| `none` | Header offset skip (`skipBytes`) without cipher transformation. |

Optional key derivation (`keyDerivation`): `none` (default), `sha256`, `md5`, `pbkdf2-sha1`, `pbkdf2-sha256`, `pbkdf1-sha1` (with `saltHex`/`saltUtf8`, `kdfIterations`, `derivedKeyLength`, and optional `useFileNameAsSalt` / `useFileNameWithoutExtension`).

### Example: single profile JSON

```json
{
  "id": "sample-aes-cbc-profile",
  "description": "Synthetic AES-CBC PKCS7 bundle transform example",
  "enabled": true,
  "algorithm": "aes-cbc",
  "fileNamePattern": "*.enc;*.bundle",
  "skipBytes": 0,
  "keyHex": "00112233445566778899AABBCCDDEEFF00112233445566778899AABBCCDDEEFF",
  "ivHex": "0102030405060708090A0B0C0D0E0F10",
  "padding": "pkcs7",
  "validateUnityHeader": true
}
```

### Example: multi-profile collection JSON

```json
{
  "profiles": [
    {
      "id": "header-skip-example",
      "algorithm": "none",
      "fileNamePattern": "*.ab",
      "magicHex": "504B4844",
      "skipBytes": 16,
      "validateUnityHeader": true
    },
    {
      "id": "xor-header-example",
      "algorithm": "xor",
      "fileNamePattern": "*.dat",
      "xorKeyHex": "A55A",
      "rangeStart": 0,
      "rangeLength": 256,
      "validateUnityHeader": true
    }
  ]
}
```

### Custom C# stream-transform plugins

Advanced users can place managed `.dll` plugins implementing `AssetStudio.IStreamTransformProvider` in `<UserData>/Plugins/Decryption/` or `<AppDir>/Plugins/Decryption/`.

> Keep personal keys and custom profiles local — never commit proprietary keys or game data into public repositories or attach them to public bug reports.

## 3. Out-of-Scope Third-Party Containers & Platform Layouts

| Format / Platform | Status | Notes |
| --- | --- | --- |
| **FMOD Studio `.bank`** | Not supported | External middleware container outside Unity `AudioClip` assets. |
| **Wwise `.bnk` / `.wem`** | Not supported | External middleware sound banks; use dedicated Wwise tools. |
| **CRIWARE `.acb` / `.awb` / `.usm` / `.cpk`** | Not supported | External middleware audio/video/archive containers. |
| **Nintendo Switch swizzled textures** | Not supported | Block-linear Tegra X1 deswizzling is not implemented; reading may succeed while decoded image pixels remain swizzled. |
| **Custom modified Unity engines (e.g. Tuanjie) / Unity 7000.x** | Not supported | Unverified serialization layout changes are not treated as supported. |

## 4. Interpreting Load & Parsing Diagnostics

When a file fails to open or export, check the diagnostic category before assuming a bug:

1. **Encrypted / profile mismatch:** `scan` classifies the file as `Encrypted`, or a profile with `validateUnityHeader: true` rejects the decrypted header because the key, IV, padding, or `skipBytes` offset does not match.
2. **Missing external data:** objects load, but export/preview logs `MissingStreamedResource` or `MissingAssembly` — this means companion `.resS`/`.resource`, external `.assets`, or `Managed` assemblies are missing (not a decryption error).
3. **Unknown or unsupported format:** `scan` classifies the file as `Other` (for example, FMOD `.bank`, Wwise `.wem`, or non-Unity data files).
4. **Parsing / truncated file failure:** header validates as Unity, but reading an object throws `EndOfStreamException` or `InvalidDataException`, indicating a truncated file, unsupported Unity format version, or modified TypeTree/class layout.
