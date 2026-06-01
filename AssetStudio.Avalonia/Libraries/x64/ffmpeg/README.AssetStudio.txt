FFmpeg Linux x64 bundle
=======================

These shared libraries and the ffplay executable are bundled for
AssetStudio.Avalonia Linux x64 audio/video preview support.

Source binary package:
https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n8.1-latest-linux64-lgpl-shared-8.1.tar.xz

Build:
ffmpeg version n8.1.1-9-g58d4114d36-20260531

License:
The included FFmpeg libraries and ffplay executable report
"LGPL version 3 or later".
The LGPLv3 license text is included in LICENSE.txt.

Validation:
The build configuration includes --enable-version3, --enable-shared,
--disable-static, --disable-libx264, --disable-libx265, --disable-libxvid,
--disable-libdavs2, --disable-frei0r, --disable-librubberband,
--disable-libvidstab, and does not include --enable-gpl or --enable-nonfree.

Release note:
When publishing AssetStudio release downloads with these binaries, keep this
notice and LICENSE.txt next to the FFmpeg libraries and provide the matching
FFmpeg source/build information required by the LGPL.
