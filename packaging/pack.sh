#!/usr/bin/env bash
# Build the release files for one platform.
#
#   packaging/pack.sh <version> <rid> [output dir]      e.g.  packaging/pack.sh 2.0.0 linux-x64
#
# Makes, in the output directory (default: dist/):
#   lux-v<version>-<rid>.tar.gz|.zip          the app and lux-light, plain archive (no installer, no self-update)
#   lux-light-v<version>-<rid>.tar.gz|.zip    the command line tool alone
#   the installed build with self-update:     Lux-win-Setup.exe (win-x64)  /  Lux.AppImage (linux-x64),
#                                             plus the update feed files (*.nupkg, releases.<channel>.json, …)
# Runs on Linux for both platforms (the Windows installer is cross-built) and in Git Bash on Windows.
set -euo pipefail

VERSION="${1:?version, e.g. 2.0.0}"; VERSION="${VERSION#v}"
RID="${2:?runtime id: linux-x64 or win-x64}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$(mkdir -p "${3:-$ROOT/dist}" && cd "${3:-$ROOT/dist}" && pwd)"
WORK="$ROOT/artifacts/pack/$RID"

case "$RID" in
  linux-x64) EXE=""; CHANNEL=linux; DIRECTIVE="[linux]" ;;
  win-x64)   EXE=".exe"; CHANNEL=win; DIRECTIVE="[win]" ;;
  *) echo "unsupported runtime id '$RID' (linux-x64, win-x64)" >&2; exit 2 ;;
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
if [ "$RID" = linux-x64 ]; then
  cp packaging/lux.desktop "$WORK/app/lux.desktop"
  cp Lux.App/Assets/Icon/lux-256.png "$WORK/app/lux.png"
  cp Lux.App/Assets/Icon/lux.svg "$WORK/app/lux.svg"
fi

archive() {   # <name> <folder>
  if [ "$RID" = win-x64 ]; then
    rm -f "$OUT/$1.zip"
    if command -v zip >/dev/null 2>&1; then (cd "$2" && zip -q -r "$OUT/$1.zip" .)
    else powershell -NoProfile -Command "Compress-Archive -Path '$(cygpath -w "$2")\\*' -DestinationPath '$(cygpath -w "$OUT/$1.zip")'"; fi
  else
    tar -C "$2" -czf "$OUT/$1.tar.gz" .
  fi
}
echo "== archives"
archive "lux-v$VERSION-$RID" "$WORK/app"
archive "lux-light-v$VERSION-$RID" "$WORK/cli"

echo "== installed build with self-update"
dotnet tool restore >/dev/null
ICON=Lux.App/Assets/Icon/lux.ico; [ "$RID" = linux-x64 ] && ICON=Lux.App/Assets/Icon/lux-256.png
EXTRA=(); [ "$RID" = linux-x64 ] && EXTRA=(--categories "Graphics;Photography")
dotnet vpk "$DIRECTIVE" pack --packId Lux --packTitle Lux --packAuthors "Lux contributors" \
  --packVersion "$VERSION" --packDir "$WORK/app" --mainExe "lux-app$EXE" --icon "$ICON" \
  --runtime "$RID" --channel "$CHANNEL" --outputDir "$WORK/feed" "${EXTRA[@]}"
rm -f "$WORK/feed"/*-Portable.zip        # the plain archive above is the portable build
cp "$WORK/feed"/* "$OUT/"

echo "== done: $OUT"
ls -l "$OUT"
