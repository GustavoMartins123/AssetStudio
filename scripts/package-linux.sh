#!/bin/bash

set -euo pipefail

CONFIGURATION="Release"
FRAMEWORK="net10.0"
RUNTIME="linux-x64"
SELF_CONTAINED="true"

REPO_ROOT="$(dirname "$(dirname "$(readlink -f "$0")")")"
DIST_DIR="$REPO_ROOT/dist"
BUILD_DIR="$REPO_ROOT/build"
PUBLISH_DIR="$REPO_ROOT/AssetStudio.Avalonia/bin/$CONFIGURATION/$FRAMEWORK/$RUNTIME/publish"

echo "=== Linux Packaging Script for AssetStudio.Avalonia ==="

echo "--> Compiling and publishing AssetStudio..."
"$REPO_ROOT/scripts/publish-linux.sh" -c "$CONFIGURATION" --self-contained "$SELF_CONTAINED"

echo "--> Cleaning up unused/Windows-only artifacts from publish folder..."
if [[ -d "$PUBLISH_DIR/runtimes/win-x64" ]]; then
    echo "    Removing Windows runtimes (saves ~100MB)..."
    rm -rf "$PUBLISH_DIR/runtimes/win-x64"
    rmdir "$PUBLISH_DIR/runtimes" 2>/dev/null || true
fi

if [[ -d "$PUBLISH_DIR/temp" ]]; then
    echo "    Removing temporary files..."
    rm -rf "$PUBLISH_DIR/temp"
fi

if [[ -d "$PUBLISH_DIR/debug" ]]; then
    echo "    Removing debug/player log files..."
    rm -rf "$PUBLISH_DIR/debug"
fi

mkdir -p "$DIST_DIR"
rm -rf "$BUILD_DIR"
mkdir -p "$BUILD_DIR"

if [[ -n "${RELEASE_VERSION:-}" ]]; then
    VERSION="${RELEASE_VERSION#[vV]}"
elif [[ -z "${VERSION:-}" ]]; then
    VERSION=$(grep -h -oP '(?<=<Version>)[^<]+' "$REPO_ROOT/AssetStudio.Avalonia/AssetStudio.Avalonia.csproj" "$REPO_ROOT/Directory.Build.props" 2>/dev/null | head -n 1 || true)
    VERSION="${VERSION:-0.17.1}"
fi
echo "--> Detected version: $VERSION"

build_appimage() {
    echo "--> Packaging AppImage..."
    local appdir="$BUILD_DIR/AppDir"
    mkdir -p "$appdir"

    cp -r "$PUBLISH_DIR"/* "$appdir/"

    cat > "$appdir/AppRun" <<'APPRUN_EOF'
#!/bin/sh
HERE="$(dirname "$(readlink -f "${0}")")"
export LD_LIBRARY_PATH="${HERE}:${HERE}/x64:${HERE}/x64/ffmpeg:${LD_LIBRARY_PATH:-}"
exec "${HERE}/AssetStudio.Avalonia" "$@"
APPRUN_EOF
    chmod +x "$appdir/AppRun"

    cp "$REPO_ROOT/AssetStudio.Avalonia/Assets/as.png" "$appdir/as.png"

    cat > "$appdir/AssetStudio.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=AssetStudio
Comment=Unity asset viewer and extractor
Exec=AssetStudio.Avalonia %F
Icon=as
Terminal=false
Categories=Development;Utility;
StartupWMClass=AssetStudio.Avalonia
MimeType=application/octet-stream;
EOF
    chmod +x "$appdir/AssetStudio.desktop"

    local tool_version="${APPIMAGETOOL_VERSION:-1.9.1}"
    local expected_sha256="${APPIMAGETOOL_SHA256:-ed4ce84f0d9caff66f50bcca6ff6f35aae54ce8135408b3fa33abfc3cb384eb0}"
    local tool_path="$BUILD_DIR/appimagetool-x86_64.AppImage"
    local tool_url="https://github.com/AppImage/appimagetool/releases/download/${tool_version}/appimagetool-x86_64.AppImage"
    echo "    Obtaining appimagetool ${tool_version}..."
    if command -v curl >/dev/null; then
        curl -fsSL -o "$tool_path" "$tool_url"
    elif command -v wget >/dev/null; then
        wget -q -O "$tool_path" "$tool_url"
    else
        echo "Error: curl or wget is required to download appimagetool." >&2
        return 1
    fi

    echo "    Verifying appimagetool SHA-256 checksum..."
    echo "${expected_sha256}  ${tool_path}" | sha256sum --check --strict
    chmod +x "$tool_path"

    echo "    Running appimagetool..."
    local output_appimage="$DIST_DIR/AssetStudio-${VERSION}-x86_64.AppImage"
    
    ARCH=x86_64 APPIMAGE_EXTRACT_AND_RUN=1 "$tool_path" "$appdir" "$output_appimage"
    
    echo "--> AppImage created at: $output_appimage"
}

build_deb() {
    echo "--> Packaging .deb..."
    local debdir="$BUILD_DIR/deb_root"
    mkdir -p "$debdir/DEBIAN"
    mkdir -p "$debdir/opt/assetstudio"
    mkdir -p "$debdir/usr/bin"
    mkdir -p "$debdir/usr/share/applications"
    mkdir -p "$debdir/usr/share/icons/hicolor/256x256/apps"

    cp -r "$PUBLISH_DIR"/* "$debdir/opt/assetstudio/"

    cat > "$debdir/usr/bin/assetstudio" <<'WRAPPER_EOF'
#!/bin/sh
export LD_LIBRARY_PATH="/opt/assetstudio:/opt/assetstudio/x64:/opt/assetstudio/x64/ffmpeg:${LD_LIBRARY_PATH:-}"
exec /opt/assetstudio/AssetStudio.Avalonia "$@"
WRAPPER_EOF
    chmod +x "$debdir/usr/bin/assetstudio"

    cat > "$debdir/usr/share/applications/assetstudio.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=AssetStudio
Comment=Unity asset viewer and extractor
Exec=assetstudio %F
Icon=assetstudio
Terminal=false
Categories=Development;Utility;
StartupWMClass=AssetStudio.Avalonia
MimeType=application/octet-stream;
EOF
    chmod +x "$debdir/usr/share/applications/assetstudio.desktop"

    cp "$REPO_ROOT/AssetStudio.Avalonia/Assets/as.png" "$debdir/usr/share/icons/hicolor/256x256/apps/assetstudio.png"

    cat > "$debdir/DEBIAN/control" <<EOF
Package: assetstudio
Version: ${VERSION}
Architecture: amd64
Maintainer: assetstudio
Section: utils
Priority: optional
Depends: libc6, libgcc-s1, libstdc++6, libgl1, libx11-6, libxcursor1, libxext6, libxi6, libxinerama1, libxrandr2, libxrender1, libfontconfig1, libfreetype6
Description: AssetStudio is a tool for exploring, extracting and exporting assets and assetbundles.
 It supports cross-platform asset viewing and exporting using Avalonia UI.
EOF

    echo "    Running dpkg-deb..."
    local output_deb="$DIST_DIR/assetstudio_${VERSION}_amd64.deb"
    dpkg-deb --build "$debdir" "$output_deb"

    echo "--> .deb package created at: $output_deb"
}

build_appimage
build_deb

rm -rf "$BUILD_DIR"

echo "=== Packaging Complete! ==="
ls -lh "$DIST_DIR"
