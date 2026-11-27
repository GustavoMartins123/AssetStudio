#!/bin/bash
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$HERE/../.." && pwd)"
EDITORS="${UNITY_EDITORS:?Set UNITY_EDITORS to the folder that holds <version>/Editor/Unity.exe}"
WORK="${FIXTURE_WORK:-${TMPDIR:-/tmp}/assetstudio-unity-fixtures}"
OUT="${FIXTURE_OUT:-$REPO/AssetStudio.Tests/Fixtures/Unity}"
LOGS="$WORK/asfix-logs"
V5=6000.5.10f1
V3=6000.3.18f1
WIN=StandaloneWindows64
MANIFEST_ROWS=()

mkdir -p "$LOGS"

native_path() {
    if command -v cygpath >/dev/null 2>&1; then cygpath -m "$1"; else printf '%s' "$1"; fi
}

unity() {
    local ver=$1 log=$2 exe="$EDITORS/$1/Editor/Unity.exe"
    shift 2
    if [ ! -f "$exe" ]; then exe="$EDITORS/$ver/Editor/Unity"; fi
    if ! "$exe" -batchmode -nographics "$@" -quit -logFile "$(native_path "$LOGS/$log.log")"; then
        echo "Unity $ver failed ($*); see $LOGS/$log.log" >&2
        exit 1
    fi
}

run_method() {
    local ver=$1 project=$2 target=$3 method=$4 log=$5
    echo "== $ver $target $method"
    unity "$ver" "$log" -projectPath "$(native_path "$project")" -buildTarget "$target" -executeMethod "$method"
}

ensure_project() {
    local ver=$1 project=$2 sources=$3
    if [ ! -d "$project/Assets" ]; then
        echo "== $ver create $(basename "$project")"
        unity "$ver" "$(basename "$project")-create" -createProject "$(native_path "$project")"
    fi
    cp -r "$sources"/. "$project/Assets/"
}

ensure_scriptable_build_pipeline() {
    local manifest="$1/Packages/manifest.json"
    if ! grep -q '"com.unity.scriptablebuildpipeline"' "$manifest"; then
        sed -i 's/"dependencies": {/"dependencies": {\n    "com.unity.scriptablebuildpipeline": "2.6.1",/' "$manifest"
    fi
}

fixture_project() {
    local ver=$1 project="$WORK/asfix-$1"
    ensure_project "$ver" "$project" "$HERE/Project"
    cp "$HERE/Fonts/FiraCode-Regular.ttf" "$project/FixtureFont.ttf"
    if [ -d "$project/Assets/Fixture/Fonts" ]; then
        cp "$HERE/Fonts/FiraCode-Regular.ttf" "$project/Assets/Fixture/Fonts/fixture_font.ttf"
    fi
    if [ "$ver" = "$V5" ]; then
        ensure_scriptable_build_pipeline "$project"
    fi
    if [ ! -f "$project/Truth/pattern64.png" ]; then
        run_method "$ver" "$project" "$WIN" MakeAllFixture.Build "$ver-prepare"
    fi
}

sprite_project() {
    local ver=$1 project="$WORK/asfix-sprites-$1"
    ensure_project "$ver" "$project" "$HERE/Sprites"
    if [ ! -d "$project/SourcePng" ]; then
        run_method "$ver" "$project" "$WIN" MakeSpriteFixture.Build "$ver-sprites-prepare"
    fi
}

editor_roundtrip() {
    local ver=$1 stage="$WORK/asfix-roundtrip-export" project="$WORK/asfix-roundtrip-$1"
    rm -rf "$stage" "$project"
    mkdir -p "$stage"
    echo "== roundtrip export"
    ASSETSTUDIO_ROUNDTRIP_EXPORT_DIR="$(native_path "$stage")" dotnet test "$(native_path "$REPO/AssetStudio.Tests/AssetStudio.Tests.csproj")" \
        -c Release --filter "FullyQualifiedName~UnityEditorRoundtripTests" > "$LOGS/roundtrip-export.log" 2>&1 || true
    local name
    for name in Roundtrip_Scene.unitypackage Roundtrip_KnightPrefab.unitypackage scene-package.json prefab-package.json; do
        if [ ! -f "$stage/$name" ]; then
            echo "Roundtrip export did not write $name; see $LOGS/roundtrip-export.log" >&2
            exit 1
        fi
    done
    echo "== $ver roundtrip import and verify"
    unity "$ver" roundtrip-import-scene -createProject "$(native_path "$project")" -importPackage "$(native_path "$stage/Roundtrip_Scene.unitypackage")"
    unity "$ver" roundtrip-import-prefab -projectPath "$(native_path "$project")" -importPackage "$(native_path "$stage/Roundtrip_KnightPrefab.unitypackage")"
    mkdir -p "$project/Assets/Editor"
    cp "$HERE/EditorRoundtrip/Editor/UnityRoundtripVerifier.cs" "$project/Assets/Editor/"
    unity "$ver" roundtrip-verify -projectPath "$(native_path "$project")" -executeMethod UnityRoundtripVerifier.Run
    if grep -l -e "ImportFBX Errors" -e "File is corrupted" -e "error CS" "$LOGS"/roundtrip-import-scene.log "$LOGS"/roundtrip-import-prefab.log "$LOGS"/roundtrip-verify.log; then
        echo "Unity reported import or compile errors in the roundtrip logs above" >&2
        exit 1
    fi
    if [ ! -f "$project/roundtrip_report.json" ]; then
        echo "Unity verifier did not write roundtrip_report.json; see $LOGS/roundtrip-verify.log" >&2
        exit 1
    fi
}

put() {
    local src=$1 rel=$2 unity_version=$3 target=$4 build=$5
    if [ ! -f "$src" ]; then
        echo "Missing generated file: $src" >&2
        exit 1
    fi
    mkdir -p "$(dirname "$OUT/$rel")"
    cp "$src" "$OUT/$rel"
    local hash size
    hash=$(sha256sum "$OUT/$rel" | cut -d' ' -f1)
    size=$(wc -c < "$OUT/$rel" | tr -d ' ')
    MANIFEST_ROWS+=("    { \"path\": \"$rel\", \"unity\": \"$unity_version\", \"target\": \"$target\", \"build\": \"$build\", \"size\": $size, \"sha256\": \"$hash\" }")
}

write_manifest() {
    {
        echo "{"
        echo "  \"generator\": \"tools/unity-fixtures/generate.sh\","
        echo "  \"font\": \"Fira Code 5.002 (SIL Open Font License 1.1, see FONT-OFL.txt)\","
        echo "  \"files\": ["
        local i last=$((${#MANIFEST_ROWS[@]} - 1))
        for i in "${!MANIFEST_ROWS[@]}"; do
            if [ "$i" -lt "$last" ]; then echo "${MANIFEST_ROWS[$i]},"; else echo "${MANIFEST_ROWS[$i]}"; fi
        done
        echo "  ]"
        echo "}"
    } > "$OUT/manifest.json"
}

P5="$WORK/asfix-$V5"
P3="$WORK/asfix-$V3"
PS="$WORK/asfix-sprites-$V5"

rm -rf "$P5/Out/$WIN/lz4" "$P5/Out/$WIN/lzma" "$P5/Out/$WIN/raw-notypetree" "$P5/Out/$WIN/extracted" \
    "$P5/Out/Android/lz4" "$P5/Out/Android/lzma" "$P5/Out/Android/raw-notypetree" "$P5/Out/Player-Mono" \
    "$P3/Out/$WIN/lz4" "$P3/Out/$WIN/lzma" "$P3/Out/$WIN/raw-notypetree" "$PS/Out"

fixture_project "$V5"
run_method "$V5" "$P5" "$WIN" MakeAllFixture.BuildBundles "$V5-$WIN-bundles"
run_method "$V5" "$P5" "$WIN" MakeExtractedFixture.Build "$V5-extracted"
run_method "$V5" "$P5" "$WIN" MakePlayerFixture.BuildMono "$V5-player-mono"
run_method "$V5" "$P5" Android MakeAllFixture.BuildBundles "$V5-Android-bundles"

fixture_project "$V3"
run_method "$V3" "$P3" "$WIN" MakeAllFixture.BuildBundles "$V3-$WIN-bundles"

sprite_project "$V5"
run_method "$V5" "$PS" "$WIN" MakeSpriteFixture.BuildBundles "$V5-sprites-$WIN"
run_method "$V5" "$PS" Android MakeSpriteFixture.BuildBundles "$V5-sprites-Android"

editor_roundtrip "$V3"

rm -rf "$OUT"
mkdir -p "$OUT"
put "$P5/Out/$WIN/lz4/fixture" 6000.5/fixture/$WIN/lz4/fixture "$V5" "$WIN" "ChunkBasedCompression (LZ4)"
put "$P5/Out/$WIN/lzma/fixture" 6000.5/fixture/$WIN/lzma/fixture "$V5" "$WIN" "None (LZMA)"
put "$P5/Out/$WIN/raw-notypetree/fixture" 6000.5/fixture/$WIN/raw-notypetree/fixture "$V5" "$WIN" "UncompressedAssetBundle, DisableWriteTypeTree"
put "$P5/Out/$WIN/extracted/fixture" 6000.5/fixture/$WIN/extracted/fixture "$V5" "$WIN" "Scriptable Build Pipeline, ContentBuildFlags.ExtractTypeTree"
put "$P5/Out/$WIN/extracted/typetrees.bin" 6000.5/fixture/$WIN/extracted/typetrees.bin "$V5" "$WIN" "CombineExtractedTypeTreeData"
put "$P5/Out/Android/lz4/fixture" 6000.5/fixture/Android/lz4/fixture "$V5" Android "ChunkBasedCompression (LZ4)"
for name in sharedassets0.assets sharedassets0.assets.resS sharedassets0.resource; do
    put "$P5/Out/Player-Mono/FixtureGame_Data/$name" 6000.5/player-mono/$name "$V5" "$WIN" "Player, Mono"
done
put "$PS/Out/$WIN/sprites" 6000.5/$WIN/sprites "$V5" "$WIN" "ChunkBasedCompression (LZ4), SpriteAtlas"
put "$PS/Out/Android/sprites" 6000.5/Android/sprites "$V5" Android "ChunkBasedCompression (LZ4), SpriteAtlas"
put "$P3/Out/$WIN/lz4/fixture" 6000.3/fixture/$WIN/lz4/fixture "$V3" "$WIN" "ChunkBasedCompression (LZ4)"
put "$WORK/asfix-roundtrip-export/scene-package.json" editor-roundtrip/scene-package.json "$V3" "$WIN" "UnityProjectExporterService scene package contents imported by Unity"
put "$WORK/asfix-roundtrip-export/prefab-package.json" editor-roundtrip/prefab-package.json "$V3" "$WIN" "UnityProjectExporterService prefab package contents imported by Unity"
put "$WORK/asfix-roundtrip-$V3/roundtrip_report.json" editor-roundtrip/roundtrip_report.json "$V3" "$WIN" "UnityRoundtripVerifier report after importing both packages"
for truth in "$P5"/Truth/*; do
    put "$truth" "6000.5/fixture/Truth/$(basename "$truth")" "$V5" source "source data written by MakeAllFixture.Build"
done
cp "$HERE/Fonts/OFL.txt" "$OUT/FONT-OFL.txt"
write_manifest
echo "Corpus written to $OUT (${#MANIFEST_ROWS[@]} files)"
