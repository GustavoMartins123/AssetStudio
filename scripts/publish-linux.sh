#!/bin/bash

set -euo pipefail

CONFIGURATION="Release"
FRAMEWORK="net10.0"
RUNTIME="linux-x64"
SELF_CONTAINED="true"
OUTPUT_DIR=""
SKIP_NATIVE="false"
SKIP_FFMPEG="false"
FFMPEG_SOURCE_OVERRIDE="${ASSETSTUDIO_FFMPEG_SOURCE_DIR:-}"

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
        --skip-ffmpeg)
            SKIP_FFMPEG="true"
            shift
            ;;
        --ffmpeg-source)
            FFMPEG_SOURCE_OVERRIDE="$2"
            shift 2
            ;;
        -h|--help)
            echo "Usage: $0 [-c Release|Debug] [-f net10.0] [-r linux-x64] [--self-contained true|false] [-o output-dir] [--skip-native] [--skip-ffmpeg] [--ffmpeg-source DIR]"
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
FFMPEG_TARGET_DIR="$NATIVE_TARGET_DIR/ffmpeg"
FFMPEG_SOURCE_DIR="$REPO_ROOT/AssetStudio.Avalonia/Libraries/x64/ffmpeg"

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
    -m:1 \
    -o "$PUBLISH_DIR"

if [[ "$SKIP_NATIVE" != "true" ]]; then
    if [[ ! -f "$NATIVE_BUILD_DIR/libTexture2DDecoderNative.so" ]]; then
        echo "libTexture2DDecoderNative.so is required for texture export. Build Texture2DDecoderNative or rerun with --skip-native to publish the managed app only." >&2
        exit 1
    fi
    mkdir -p "$NATIVE_TARGET_DIR"
    cp "$NATIVE_BUILD_DIR/libTexture2DDecoderNative.so" "$NATIVE_TARGET_DIR/"
    cp "$NATIVE_BUILD_DIR/libTexture2DDecoderNative.so" "$PUBLISH_DIR/"
    echo "Copied libTexture2DDecoderNative.so to $NATIVE_TARGET_DIR and $PUBLISH_DIR"
fi

copy_ffmpeg_libs() {
    local source_dir="$1"
    local target_dir="$2"

    if [[ ! -d "$source_dir" ]]; then
        return 1
    fi

    local codec
    codec="$(find "$source_dir" -maxdepth 3 \( -name 'libavcodec.so.62' -o -name 'libavcodec.so.62.*' \) -print -quit)"
    if [[ -z "$codec" ]]; then
        return 1
    fi

    rm -rf "$target_dir"
    mkdir -p "$target_dir"
    while IFS= read -r lib; do
        cp -P "$lib" "$target_dir/"
    done < <(find "$source_dir" -maxdepth 3 \( \
        -name 'libavcodec.so*' -o \
        -name 'libavdevice.so*' -o \
        -name 'libavfilter.so*' -o \
        -name 'libavformat.so*' -o \
        -name 'libavutil.so*' -o \
        -name 'libswscale.so*' -o \
        -name 'libswresample.so*' \
    \))

    while IFS= read -r tool; do
        cp -P "$tool" "$target_dir/"
    done < <(find "$source_dir" -maxdepth 3 -type f -name 'ffplay' -perm -111)

    while IFS= read -r notice; do
        cp "$notice" "$target_dir/"
    done < <(find "$source_dir" -maxdepth 1 -type f \( \
        -iname 'LICENSE*' -o \
        -iname 'COPYING*' -o \
        -iname 'NOTICE*' -o \
        -iname 'README*' \
    \))

    ensure_ffmpeg_soname_alias "$target_dir" "libavcodec.so.62" "libavcodec.so.62.*"
    ensure_ffmpeg_soname_alias "$target_dir" "libavdevice.so.62" "libavdevice.so.62.*"
    ensure_ffmpeg_soname_alias "$target_dir" "libavfilter.so.11" "libavfilter.so.11.*"
    ensure_ffmpeg_soname_alias "$target_dir" "libavformat.so.62" "libavformat.so.62.*"
    ensure_ffmpeg_soname_alias "$target_dir" "libavutil.so.60" "libavutil.so.60.*"
    ensure_ffmpeg_soname_alias "$target_dir" "libswscale.so.9" "libswscale.so.9.*"
    ensure_ffmpeg_soname_alias "$target_dir" "libswresample.so.6" "libswresample.so.6.*"

    return 0
}

ensure_ffmpeg_soname_alias() {
    local target_dir="$1"
    local alias_name="$2"
    local version_pattern="$3"
    local source_file

    if [[ -e "$target_dir/$alias_name" ]]; then
        return
    fi

    source_file="$(find "$target_dir" -maxdepth 1 -name "$version_pattern" -print -quit)"
    if [[ -n "$source_file" ]]; then
        cp "$source_file" "$target_dir/$alias_name"
    fi
}

ensure_ffmpeg_libs() {
    if [[ "$RUNTIME" != "linux-x64" || "$SKIP_FFMPEG" == "true" ]]; then
        return
    fi

    if [[ -n "$FFMPEG_SOURCE_OVERRIDE" ]]; then
        if copy_ffmpeg_libs "$FFMPEG_SOURCE_OVERRIDE" "$FFMPEG_TARGET_DIR"; then
            echo "Copied bundled FFmpeg libraries from $FFMPEG_SOURCE_OVERRIDE to $FFMPEG_TARGET_DIR"
            return
        fi

        echo "FFmpeg libraries were not found in --ffmpeg-source '$FFMPEG_SOURCE_OVERRIDE'." >&2
        echo "Expected libavcodec.so.62, libavformat.so.*, libavutil.so.*, libswscale.so.*, and libswresample.so.*." >&2
        exit 1
    fi

    if copy_ffmpeg_libs "$FFMPEG_SOURCE_DIR" "$FFMPEG_TARGET_DIR"; then
        echo "Copied bundled FFmpeg libraries from $FFMPEG_SOURCE_DIR to $FFMPEG_TARGET_DIR"
        return
    fi

    echo "Bundled FFmpeg libraries were not found in $FFMPEG_SOURCE_DIR" >&2
    echo "Place the ready-to-ship Linux x64 FFmpeg shared libraries there, or pass --ffmpeg-source DIR." >&2
    echo "Required ABI for FFmpegVideoPlayer 2.8.0: libavcodec.so.62 plus matching libavformat/libavutil/libswscale/libswresample." >&2
    echo "Use --skip-ffmpeg only if you intentionally want to rely on system FFmpeg." >&2
    exit 1
}

ensure_ffmpeg_libs

ICON_SRC="$REPO_ROOT/AssetStudio.Avalonia/Assets/as.png"
if [[ -f "$ICON_SRC" ]]; then
    cp "$ICON_SRC" "$PUBLISH_DIR/as.png"
    echo "Copied as.png icon to $PUBLISH_DIR"
fi

DESKTOP_FILE="$PUBLISH_DIR/AssetStudio.desktop"
cat > "$DESKTOP_FILE" <<DESKTOP_EOF
[Desktop Entry]
Type=Application
Name=AssetStudio
Comment=Unity asset viewer and extractor
Exec="$PUBLISH_DIR/AssetStudio.Avalonia" %F
Icon=$PUBLISH_DIR/as.png
Terminal=false
Categories=Development;Utility;
StartupWMClass=AssetStudio.Avalonia
MimeType=application/octet-stream;
DESKTOP_EOF
chmod +x "$DESKTOP_FILE"
echo "Generated $DESKTOP_FILE"

INSTALL_SCRIPT="$PUBLISH_DIR/install-desktop.sh"
cat > "$INSTALL_SCRIPT" <<'INSTALL_HEADER'
#!/bin/bash
set -euo pipefail
SCRIPT_DIR="$(dirname "$(readlink -f "$0")")"
INSTALL_HEADER

cat >> "$INSTALL_SCRIPT" <<INSTALL_BODY
DESKTOP_DIR="\$HOME/.local/share/applications"
ICON_DIR="\$HOME/.local/share/icons/hicolor/256x256/apps"
mkdir -p "\$DESKTOP_DIR" "\$ICON_DIR"

cp "\$SCRIPT_DIR/as.png" "\$ICON_DIR/assetstudio.png"
gtk-update-icon-cache -f -t "\$HOME/.local/share/icons/hicolor" 2>/dev/null || true

cat > "\$DESKTOP_DIR/assetstudio.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=AssetStudio
Comment=Unity asset viewer and extractor
Exec="\$SCRIPT_DIR/AssetStudio.Avalonia" %F
Icon=assetstudio
Terminal=false
Categories=Development;Utility;
StartupWMClass=AssetStudio.Avalonia
MimeType=application/octet-stream;
EOF

chmod +x "\$DESKTOP_DIR/assetstudio.desktop"
update-desktop-database "\$DESKTOP_DIR" 2>/dev/null || true

echo "Done! AssetStudio should now appear in your application launcher."
INSTALL_BODY

chmod +x "$INSTALL_SCRIPT"
echo "Generated $INSTALL_SCRIPT"

echo ""
echo "Done: $PUBLISH_DIR"
echo "You can run the app with: $PUBLISH_DIR/AssetStudio.Avalonia"
echo ""
echo "To add AssetStudio to your application launcher, run:"
echo "  bash \"$INSTALL_SCRIPT\""
