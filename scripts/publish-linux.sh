#!/bin/bash

set -euo pipefail

CONFIGURATION="Release"
FRAMEWORK="net10.0"
RUNTIME="linux-x64"
SELF_CONTAINED="true"
OUTPUT_DIR=""
SKIP_NATIVE="false"
SKIP_FFMPEG="false"
SKIP_FFMPEG_DOWNLOAD="${SKIP_FFMPEG_DOWNLOAD:-0}"
FFMPEG_SOURCE_OVERRIDE="${FFMPEG_LINUX_DIR:-${ASSETSTUDIO_FFMPEG_SOURCE_DIR:-}}"

# Pinned official FFmpeg-Builds (formerly BtbN/FFmpeg-Builds) 8.1 LGPL shared Linux x64 release.
# Required ABI for FFmpegVideoPlayer.Avalonia 2.8.0: libavcodec.so.62, libavformat.so.62,
# libavutil.so.60, libswresample.so.6, libswscale.so.9.
DEFAULT_FFMPEG_LINUX_URL="https://github.com/FFmpeg-Builds/FFmpeg-Builds/releases/download/autobuild-2026-10-31-12-59/ffmpeg-n8.1.3-27-g8dca1586f4-linux64-lgpl-shared-8.1.tar.xz"
DEFAULT_FFMPEG_LINUX_SHA256="493e89a48da02b9a7d217b60f7f60c3c917c9bed73459d8a106e5e987da082ef"
FFMPEG_LINUX_URL_IS_CUSTOM="false"
if [[ -n "${FFMPEG_LINUX_URL:-}" ]]; then
    FFMPEG_LINUX_URL_IS_CUSTOM="true"
else
    FFMPEG_LINUX_URL="$DEFAULT_FFMPEG_LINUX_URL"
fi
if [[ -z "${FFMPEG_LINUX_SHA256:-}" ]]; then
    if [[ "$FFMPEG_LINUX_URL_IS_CUSTOM" == "false" ]]; then
        FFMPEG_LINUX_SHA256="$DEFAULT_FFMPEG_LINUX_SHA256"
    else
        FFMPEG_LINUX_SHA256=""
    fi
fi

FFMPEG_TMP_DIR=""
cleanup_ffmpeg_tmp() {
    if [[ -n "${FFMPEG_TMP_DIR:-}" && -d "$FFMPEG_TMP_DIR" ]]; then
        rm -rf "$FFMPEG_TMP_DIR"
    fi
}
trap cleanup_ffmpeg_tmp EXIT

is_truthy() {
    case "${1,,}" in
        1|true|yes|on) return 0 ;;
        *) return 1 ;;
    esac
}

print_usage() {
    cat <<EOF
Usage: $0 [-c Release|Debug] [-f net10.0] [-r linux-x64] [--self-contained true|false] [-o output-dir] [--skip-native] [--skip-ffmpeg] [--skip-ffmpeg-download] [--ffmpeg-dir DIR]

Options:
  -c, --configuration CFG   Build configuration (default: Release)
  -f, --framework TFM       Target framework (default: net10.0)
  -r, --runtime RID         Target runtime identifier (default: linux-x64)
  --self-contained BOOL     Publish self-contained (default: true)
  -o, --output DIR          Custom publish output directory
  --skip-native             Skip building/copying Texture2DDecoderNative
  --skip-ffmpeg             Do not bundle FFmpeg shared libraries (rely on system FFmpeg)
  --skip-ffmpeg-download    Skip automatic FFmpeg download (use pre-staged libs or system FFmpeg)
  --ffmpeg-dir DIR          Copy Linux FFmpeg shared libraries from a local directory
  --ffmpeg-source DIR       Alias for --ffmpeg-dir

Environment variables:
  FFMPEG_LINUX_DIR          Local FFmpeg directory override (alias: ASSETSTUDIO_FFMPEG_SOURCE_DIR)
  FFMPEG_LINUX_URL          Custom FFmpeg .tar.xz download URL
  FFMPEG_LINUX_SHA256       Expected SHA-256 checksum for FFMPEG_LINUX_URL
  SKIP_FFMPEG_DOWNLOAD=1    Skip automatic FFmpeg download
EOF
}

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
        --skip-ffmpeg-download)
            SKIP_FFMPEG_DOWNLOAD="1"
            shift
            ;;
        --ffmpeg-dir|--ffmpeg-source)
            FFMPEG_SOURCE_OVERRIDE="$2"
            shift 2
            ;;
        -h|--help)
            print_usage
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

has_required_ffmpeg_libs() {
    local dir="$1"
    if [[ ! -d "$dir" ]]; then
        return 1
    fi

    local pattern
    for pattern in \
        'libavcodec.so.62*' \
        'libavformat.so*' \
        'libavutil.so*' \
        'libswresample.so*' \
        'libswscale.so*'; do
        if [[ -z "$(find "$dir" -maxdepth 3 -name "$pattern" -print -quit)" ]]; then
            return 1
        fi
    done

    return 0
}

ensure_ffmpeg_soname_alias() {
    local target_dir="$1"
    local alias_name="$2"
    local version_pattern="$3"
    local alias_path="$target_dir/$alias_name"
    local source_file

    if [[ -L "$alias_path" && -e "$alias_path" ]] || [[ -f "$alias_path" && $(stat -c%s "$alias_path" 2>/dev/null || echo 0) -gt 1024 ]]; then
        return
    fi

    source_file="$(find "$target_dir" -maxdepth 1 -type f -name "$version_pattern" -size +1024c -print -quit)"
    if [[ -n "$source_file" ]]; then
        ln -sf "$(basename "$source_file")" "$alias_path"
    fi
}

copy_ffmpeg_libs() {
    local source_dir="$1"
    local target_dir="$2"

    if ! has_required_ffmpeg_libs "$source_dir"; then
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
    done < <(find "$source_dir" -maxdepth 2 -type f \( \
        -iname 'LICENSE*' -o \
        -iname 'COPYING*' -o \
        -iname 'NOTICE*' -o \
        -iname 'README*' \
    \))

    ensure_ffmpeg_soname_alias "$target_dir" "libavcodec.so.62" "libavcodec.so.62.*"
    ensure_ffmpeg_soname_alias "$target_dir" "libavcodec.so" "libavcodec.so.62.*"
    ensure_ffmpeg_soname_alias "$target_dir" "libavdevice.so.62" "libavdevice.so.62.*"
    ensure_ffmpeg_soname_alias "$target_dir" "libavdevice.so" "libavdevice.so.62.*"
    ensure_ffmpeg_soname_alias "$target_dir" "libavfilter.so.11" "libavfilter.so.11.*"
    ensure_ffmpeg_soname_alias "$target_dir" "libavfilter.so" "libavfilter.so.11.*"
    ensure_ffmpeg_soname_alias "$target_dir" "libavformat.so.62" "libavformat.so.62.*"
    ensure_ffmpeg_soname_alias "$target_dir" "libavformat.so" "libavformat.so.62.*"
    ensure_ffmpeg_soname_alias "$target_dir" "libavutil.so.60" "libavutil.so.60.*"
    ensure_ffmpeg_soname_alias "$target_dir" "libavutil.so" "libavutil.so.60.*"
    ensure_ffmpeg_soname_alias "$target_dir" "libswscale.so.9" "libswscale.so.9.*"
    ensure_ffmpeg_soname_alias "$target_dir" "libswscale.so" "libswscale.so.9.*"
    ensure_ffmpeg_soname_alias "$target_dir" "libswresample.so.6" "libswresample.so.6.*"
    ensure_ffmpeg_soname_alias "$target_dir" "libswresample.so" "libswresample.so.6.*"

    return 0
}

ensure_linux_ffmpeg_bundle() {
    if has_required_ffmpeg_libs "$FFMPEG_SOURCE_DIR"; then
        return 0
    fi

    if is_truthy "$SKIP_FFMPEG_DOWNLOAD"; then
        echo "Skipping automatic FFmpeg download (--skip-ffmpeg-download / SKIP_FFMPEG_DOWNLOAD=1)."
        echo "No pre-staged FFmpeg libraries found in $FFMPEG_SOURCE_DIR; relying on system FFmpeg."
        return 2
    fi

    if ! command -v curl >/dev/null 2>&1; then
        echo "Error: curl is required to download Linux FFmpeg libraries from $FFMPEG_LINUX_URL." >&2
        echo "Install curl, pass --ffmpeg-dir DIR (or FFMPEG_LINUX_DIR), or rerun with --skip-ffmpeg-download." >&2
        return 1
    fi

    if ! command -v tar >/dev/null 2>&1; then
        echo "Error: tar is required to extract Linux FFmpeg libraries." >&2
        return 1
    fi

    FFMPEG_TMP_DIR="$(mktemp -d)"
    local archive_path="$FFMPEG_TMP_DIR/ffmpeg-linux64.tar.xz"
    local archive_list="$FFMPEG_TMP_DIR/archive.list"
    local extract_dir="$FFMPEG_TMP_DIR/extracted"
    local staged_dir="$FFMPEG_TMP_DIR/staged"
    mkdir -p "$extract_dir"

    echo "Downloading Linux x64 FFmpeg shared libraries from:"
    echo "  $FFMPEG_LINUX_URL"
    curl -fsSL --retry 3 --retry-delay 2 -o "$archive_path" "$FFMPEG_LINUX_URL"

    if [[ -n "$FFMPEG_LINUX_SHA256" ]]; then
        if ! command -v sha256sum >/dev/null 2>&1; then
            echo "Error: sha256sum is required to verify the FFmpeg archive checksum." >&2
            return 1
        fi
        echo "Verifying FFmpeg archive SHA-256 checksum..."
        echo "${FFMPEG_LINUX_SHA256}  ${archive_path}" | sha256sum --check --strict
    else
        echo "Warning: FFMPEG_LINUX_SHA256 is not set for custom FFMPEG_LINUX_URL; skipping checksum verification." >&2
    fi

    tar -tJf "$archive_path" > "$archive_list"

    local -a archive_members=()
    mapfile -t archive_members < <(
        grep -vE '(^/|(^|/)\.\.(/|$))' "$archive_list" \
            | grep -E '(^|/)(lib/lib(avcodec|avdevice|avfilter|avformat|avutil|swresample|swscale)\.so(\.[0-9]+)*|bin/ffplay)$|^([^/]+/)?(LICENSE|COPYING|NOTICE|README)[^/]*$' \
            || true
    )

    if [[ ${#archive_members[@]} -eq 0 ]]; then
        echo "Error: Downloaded FFmpeg archive did not contain expected shared libraries (lib/*.so*)." >&2
        return 1
    fi

    echo "Extracting FFmpeg shared libraries, ffplay, and license notices..."
    tar -xJf "$archive_path" -C "$extract_dir" "${archive_members[@]}"

    if ! copy_ffmpeg_libs "$extract_dir" "$staged_dir"; then
        echo "Error: Extracted FFmpeg archive is missing required FFmpeg 8.x shared libraries." >&2
        echo "Required ABI for FFmpegVideoPlayer 2.8.0: libavcodec.so.62 plus libavformat/libavutil/libswresample/libswscale." >&2
        return 1
    fi

    rm -rf "$FFMPEG_SOURCE_DIR"
    mkdir -p "$(dirname "$FFMPEG_SOURCE_DIR")"
    mv "$staged_dir" "$FFMPEG_SOURCE_DIR"

    rm -rf "$FFMPEG_TMP_DIR"
    FFMPEG_TMP_DIR=""
    echo "Staged Linux x64 FFmpeg libraries into $FFMPEG_SOURCE_DIR"
    return 0
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

        echo "FFmpeg libraries were not found in --ffmpeg-dir '$FFMPEG_SOURCE_OVERRIDE'." >&2
        echo "Expected libavcodec.so.62, libavformat.so.*, libavutil.so.*, libswresample.so.*, and libswscale.so.*." >&2
        exit 1
    fi

    local bundle_rc=0
    ensure_linux_ffmpeg_bundle || bundle_rc=$?
    if [[ "$bundle_rc" -eq 2 ]]; then
        return
    elif [[ "$bundle_rc" -ne 0 ]]; then
        exit "$bundle_rc"
    fi

    if copy_ffmpeg_libs "$FFMPEG_SOURCE_DIR" "$FFMPEG_TARGET_DIR"; then
        echo "Copied bundled FFmpeg libraries from $FFMPEG_SOURCE_DIR to $FFMPEG_TARGET_DIR"
        return
    fi

    echo "Bundled FFmpeg libraries were not found in $FFMPEG_SOURCE_DIR" >&2
    echo "Place the ready-to-ship Linux x64 FFmpeg shared libraries there, pass --ffmpeg-dir DIR, or set FFMPEG_LINUX_URL." >&2
    echo "Required ABI for FFmpegVideoPlayer 2.8.0: libavcodec.so.62 plus matching libavformat/libavutil/libswresample/libswscale." >&2
    echo "Use --skip-ffmpeg or --skip-ffmpeg-download to rely on system FFmpeg." >&2
    exit 1
}

ensure_ffmpeg_libs

ICON_SRC="$REPO_ROOT/AssetStudio.Avalonia/Assets/as.png"
if [[ -f "$ICON_SRC" ]]; then
    cp "$ICON_SRC" "$PUBLISH_DIR/as.png"
    echo "Copied as.png icon to $PUBLISH_DIR"
fi

LAUNCHER_SCRIPT="$PUBLISH_DIR/launch-assetstudio.sh"
cat > "$LAUNCHER_SCRIPT" <<'LAUNCHER_EOF'
#!/bin/sh
HERE="$(dirname "$(readlink -f "${0}")")"
export LD_LIBRARY_PATH="${HERE}:${HERE}/x64:${HERE}/x64/ffmpeg:${LD_LIBRARY_PATH:-}"
exec "${HERE}/AssetStudio.Avalonia" "$@"
LAUNCHER_EOF
chmod +x "$LAUNCHER_SCRIPT"
echo "Generated $LAUNCHER_SCRIPT"

DESKTOP_FILE="$PUBLISH_DIR/AssetStudio.desktop"
cat > "$DESKTOP_FILE" <<DESKTOP_EOF
[Desktop Entry]
Type=Application
Name=AssetStudio
Comment=Unity asset viewer and extractor
Exec="$LAUNCHER_SCRIPT" %F
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

chmod +x "\$SCRIPT_DIR/launch-assetstudio.sh" 2>/dev/null || true
cp "\$SCRIPT_DIR/as.png" "\$ICON_DIR/assetstudio.png"
gtk-update-icon-cache -f -t "\$HOME/.local/share/icons/hicolor" 2>/dev/null || true

cat > "\$DESKTOP_DIR/assetstudio.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=AssetStudio
Comment=Unity asset viewer and extractor
Exec="\$SCRIPT_DIR/launch-assetstudio.sh" %F
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
echo "You can run the app with: $LAUNCHER_SCRIPT"
echo ""
echo "To add AssetStudio to your application launcher, run:"
echo "  bash \"$INSTALL_SCRIPT\""
