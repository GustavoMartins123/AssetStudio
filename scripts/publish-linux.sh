#!/bin/bash

set -euo pipefail

CONFIGURATION="Release"
FRAMEWORK="net10.0"
RUNTIME="linux-x64"
SELF_CONTAINED="false"
OUTPUT_DIR=""
SKIP_NATIVE="false"

while [[ $# -gt 0 ]]; do
    case "$1" in
        -c|--configuration)
            CONFIGURATION="$2"
            shift 2
            ;;
        -f|--framework)
            FRAMEWORK="$2"
            shift 2
            ;;
        -r|--runtime)
            RUNTIME="$2"
            shift 2
            ;;
        --self-contained)
            SELF_CONTAINED="$2"
            shift 2
            ;;
        -o|--output)
            OUTPUT_DIR="$2"
            shift 2
            ;;
        --skip-native)
            SKIP_NATIVE="true"
            shift
            ;;
        -h|--help)
            echo "Usage: $0 [-c Release|Debug] [-f net10.0] [-r linux-x64] [--self-contained true|false] [-o output-dir] [--skip-native]"
            exit 0
            ;;
        *)
            echo "Unknown argument: $1" >&2
            exit 1
            ;;
    esac
done

REPO_ROOT="$(dirname "$(dirname "$(readlink -f "$0")")")"
DEFAULT_PUBLISH_DIR="$REPO_ROOT/AssetStudio.Avalonia/bin/$CONFIGURATION/$FRAMEWORK/$RUNTIME/publish"
PUBLISH_DIR="${OUTPUT_DIR:-$DEFAULT_PUBLISH_DIR}"
RUNTIME_FOLDER="x64"
NATIVE_TARGET_DIR="$PUBLISH_DIR/$RUNTIME_FOLDER"
NATIVE_BUILD_DIR="$REPO_ROOT/Texture2DDecoderNative/build"

echo "Publishing AssetStudio.Avalonia ($CONFIGURATION, $FRAMEWORK, $RUNTIME, self-contained=$SELF_CONTAINED)"

if [[ "$SKIP_NATIVE" != "true" ]]; then
    echo "Building Texture2DDecoderNative.so..."
    mkdir -p "$NATIVE_BUILD_DIR"
    cmake -S "$REPO_ROOT/Texture2DDecoderNative" -B "$NATIVE_BUILD_DIR" -DCMAKE_BUILD_TYPE="$CONFIGURATION"
    cmake --build "$NATIVE_BUILD_DIR"
fi

dotnet publish "$REPO_ROOT/AssetStudio.Avalonia/AssetStudio.Avalonia.csproj" \
    -c "$CONFIGURATION" \
    -f "$FRAMEWORK" \
    -r "$RUNTIME" \
    --self-contained "$SELF_CONTAINED" \
    -o "$PUBLISH_DIR"

if [[ "$SKIP_NATIVE" != "true" ]]; then
    if [[ ! -f "$NATIVE_BUILD_DIR/libTexture2DDecoderNative.so" ]]; then
        echo "libTexture2DDecoderNative.so is required for texture export. Build Texture2DDecoderNative or rerun with --skip-native to publish the managed app only." >&2
        exit 1
    fi
    mkdir -p "$NATIVE_TARGET_DIR"
    cp "$NATIVE_BUILD_DIR/libTexture2DDecoderNative.so" "$NATIVE_TARGET_DIR/"
    echo "Copied libTexture2DDecoderNative.so to $NATIVE_TARGET_DIR"
fi

echo ""
echo "Done: $PUBLISH_DIR"
echo "You can run the app with: $PUBLISH_DIR/AssetStudio.Avalonia"
