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

if [[ -f "$REPO_ROOT/AssetStudio.Avalonia/Libraries/x64/libfmod.so" ]]; then
    mkdir -p "$NATIVE_TARGET_DIR"
    cp "$REPO_ROOT/AssetStudio.Avalonia/Libraries/x64/libfmod.so" "$NATIVE_TARGET_DIR/"
    echo "Copied libfmod.so to $NATIVE_TARGET_DIR"
fi

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