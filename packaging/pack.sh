#!/usr/bin/env bash
# Build the release files for one platform.
#
#   packaging/pack.sh <version> <rid> [output dir]      e.g.  packaging/pack.sh 2.0.0 linux-x64
#
# Makes, in the output directory (default: dist/):
#   lux-v<version>-<rid>.tar.gz|.zip          the app and lux-light, plain archive (no installer, no self-update)
#   lux-light-v<version>-<rid>.tar.gz|.zip    the command line tool alone
#   the installed build with self-update:     Lux-win-Setup.exe (win-x64)  /  Lux.AppImage (linux-x64),
#                                             Lux-win-arm64-Setup.exe (win-arm64)  /  Lux-linux-arm64.AppImage (linux-arm64),
#                                             plus the update feed files (*.nupkg, releases.<channel>.json, …)
# For macOS (osx-arm64, osx-x64) the first archive contains Lux.app. There is no installed build with self-update for macOS.
# Runs on Linux for all platforms (the Windows installer is cross-built), in Git Bash on Windows, and on macOS.
# A macOS build must be signed before macOS on Apple silicon can start it. On macOS this script signs the files ad hoc.
# On other systems it cannot sign: sign the files on a Mac, as the last message of the script tells you.
set -euo pipefail

VERSION="${1:?version, e.g. 2.0.0}"; VERSION="${VERSION#v}"
RID="${2:?runtime id: linux-x64, linux-arm64, win-x64, win-arm64, osx-arm64 or osx-x64}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$(mkdir -p "${3:-$ROOT/dist}" && cd "${3:-$ROOT/dist}" && pwd)"
WORK="$ROOT/artifacts/pack/$RID"

case "$RID" in
  linux-x64) EXE=""; CHANNEL=linux; DIRECTIVE="[linux]" ;;
  win-x64)   EXE=".exe"; CHANNEL=win; DIRECTIVE="[win]" ;;
  # Each ARM64 build has its own update channel, so that it gets ARM64 updates only.
  linux-arm64) EXE=""; CHANNEL=linux-arm64; DIRECTIVE="[linux]" ;;
  win-arm64)   EXE=".exe"; CHANNEL=win-arm64; DIRECTIVE="[win]" ;;
  osx-arm64|osx-x64) EXE=""; CHANNEL=""; DIRECTIVE="" ;;
  *) echo "unsupported runtime id '$RID' (linux-x64, linux-arm64, win-x64, win-arm64, osx-arm64, osx-x64)" >&2; exit 2 ;;
esac

rm -rf "$WORK"; mkdir -p "$WORK/app" "$WORK/cli" "$WORK/feed"
cd "$ROOT"

publish() {   # <project> <output dir> [extra properties…]
  local proj="$1" dir="$2"; shift 2
  dotnet publish "$proj" -c Release -r "$RID" --self-contained true \
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=none \
    -p:Version="$VERSION" --artifacts-path "$WORK/build" -o "$dir" -v q "$@"
}

echo "== publish lux-light ($RID $VERSION)"
publish Lux.Cli/Lux.Cli.csproj "$WORK/cli"
echo "== publish lux-app"
publish Lux.App/Lux.App.csproj "$WORK/app" -p:IncludeNativeLibrariesForSelfExtract=true
rm -f "$WORK/app"/*.xml "$WORK/cli"/*.xml           # API documentation files are not part of a release

# the app folder also carries the command line tool, the licence and the notes
cp "$WORK/cli/lux-light$EXE" "$WORK/app/"
cp LICENSE README.md "$WORK/app/"
cp LICENSE README.md "$WORK/cli/"
if [[ "$RID" == linux-* ]]; then
  cp packaging/lux.desktop "$WORK/app/lux.desktop"
  cp Lux.App/Assets/Icon/lux-256.png "$WORK/app/lux.png"
  cp Lux.App/Assets/Icon/lux.svg "$WORK/app/lux.svg"
fi

APPDIR="$WORK/app"
if [[ "$RID" == osx-* ]]; then
  # macOS: put the app folder into the bundle layout. The executables and their files go to Contents/MacOS.
  BUNDLE="$WORK/bundle/Lux.app"
  mkdir -p "$BUNDLE/Contents/Resources"
  cp "$WORK/cli"/*.dylib "$WORK/app/" 2>/dev/null || true
  mv "$WORK/app" "$BUNDLE/Contents/MacOS"
  # Only executables and libraries can stay in Contents/MacOS: the signature of the bundle does not accept other files there.
  for f in "$BUNDLE/Contents/MacOS"/*; do
    case "$(basename "$f")" in lux-app|lux-light|*.dylib) ;; *) mv "$f" "$BUNDLE/Contents/Resources/" ;; esac
  done
  cp Lux.App/Assets/Icon/lux.icns "$BUNDLE/Contents/Resources/lux.icns"
  sed "s/@VERSION@/$VERSION/g" packaging/Info.plist > "$BUNDLE/Contents/Info.plist"
  APPDIR="$WORK/bundle"
  if command -v codesign >/dev/null 2>&1; then
    # Sign the libraries first, then the executables, then the bundle.
    find "$BUNDLE/Contents/MacOS" "$WORK/cli" -name '*.dylib' -exec codesign --force -s - {} \;
    codesign --force -s - "$WORK/cli/lux-light" "$BUNDLE/Contents/MacOS/lux-light" "$BUNDLE/Contents/MacOS/lux-app"
    codesign --force -s - "$BUNDLE"
    codesign --verify --strict "$BUNDLE"
    SIGNED=yes
  fi
fi

archive() {   # <name> <folder>
  if [[ "$RID" == win-* ]]; then
    rm -f "$OUT/$1.zip"
    if command -v zip >/dev/null 2>&1; then (cd "$2" && zip -q -r "$OUT/$1.zip" .)
    else powershell -NoProfile -Command "Compress-Archive -Path '$(cygpath -w "$2")\\*' -DestinationPath '$(cygpath -w "$OUT/$1.zip")'"; fi
  else
    tar -C "$2" -czf "$OUT/$1.tar.gz" .
  fi
}
echo "== archives"
archive "lux-v$VERSION-$RID" "$APPDIR"
archive "lux-light-v$VERSION-$RID" "$WORK/cli"

if [[ "$RID" == osx-* ]]; then
  [ -n "${SIGNED:-}" ] || echo "== NOT SIGNED: on a Mac, unpack the archives and run 'codesign --force -s -' on each .dylib, on lux-light and lux-app, then on Lux.app"
  echo "== done: $OUT"; ls -l "$OUT"; exit 0
fi

echo "== installed build with self-update"
dotnet tool restore >/dev/null
ICON=Lux.App/Assets/Icon/lux.ico; [[ "$RID" == linux-* ]] && ICON=Lux.App/Assets/Icon/lux-256.png
EXTRA=(); [[ "$RID" == linux-* ]] && EXTRA=(--categories "Graphics;Photography")
dotnet vpk "$DIRECTIVE" pack --packId Lux --packTitle Lux --packAuthors "Lux contributors" \
  --packVersion "$VERSION" --packDir "$WORK/app" --mainExe "lux-app$EXE" --icon "$ICON" \
  --runtime "$RID" --channel "$CHANNEL" --outputDir "$WORK/feed" "${EXTRA[@]}"
rm -f "$WORK/feed"/*-Portable.zip        # the plain archive above is the portable build
rm -f "$WORK/feed"/assets.*.json        # only the packer's own upload command reads these lists
cp "$WORK/feed"/* "$OUT/"

echo "== done: $OUT"
ls -l "$OUT"
