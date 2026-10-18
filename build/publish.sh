#!/usr/bin/env bash
# Publishes PoorMansFeed as a self-contained single-file Release build.
#
#   build/publish.sh [all|win-x64|osx-arm64]     (default: all)
#
# Output:
#   artifacts/win-x64/PoorMansFeed.exe
#   artifacts/osx-arm64/PoorMansFeed.app      (macOS only; the bundle holds the single-file executable)
#
# Restore runs once in locked mode (packages.lock.json must not change); the publishes use --no-restore.
# Run on macOS for the .app bundle. On other hosts the osx-arm64 build is left as a bare executable in
# artifacts/osx-arm64/ because the bundle needs plutil, codesign and the macOS icon files.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$ROOT/src/PoorMansFeed/PoorMansFeed.csproj"
ASSETS="$ROOT/src/PoorMansFeed/Assets"
ARTIFACTS="$ROOT/artifacts"
STAGING="$ARTIFACTS/.staging"
APP_NAME="PoorMansFeed"
BUNDLE_ID="io.github.asfritecx.poormansfeed"
MIN_MACOS="14.0"

case "${1:-all}" in
    all) RUNTIMES=(win-x64 osx-arm64) ;;
    win-x64|osx-arm64) RUNTIMES=("$1") ;;
    *) echo "Usage: $0 [all|win-x64|osx-arm64]" >&2; exit 2 ;;
esac

[[ -f "$PROJECT" ]] || { echo "Project not found: $PROJECT" >&2; exit 1; }
[[ -f "$ASSETS/$APP_NAME.ico" && -f "$ASSETS/$APP_NAME.icns" ]] || { echo "Icons missing from $ASSETS" >&2; exit 1; }

# <Version> in Directory.Build.props is the one version source.
VERSION="$(dotnet msbuild "$PROJECT" -nologo -getProperty:Version)"
if [[ ! "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    echo "Could not read a numeric major.minor.patch <Version> (got '$VERSION')." >&2
    exit 1
fi

file_size() {
    local bytes
    bytes="$(wc -c < "$1" | tr -d ' ')"
    awk -v b="$bytes" 'BEGIN { printf "%s bytes (%.1f MB)", b, b / 1048576 }'
}

echo "Restoring (locked mode)..."
dotnet restore "$PROJECT" --locked-mode

rm -rf "$STAGING"
mkdir -p "$ARTIFACTS"
RESULTS=()

for RID in "${RUNTIMES[@]}"; do
    echo "Publishing $RID..."
    STAGE_DIR="$STAGING/$RID"
    OUT_DIR="$ARTIFACTS/$RID"
    rm -rf "$OUT_DIR"
    mkdir -p "$STAGE_DIR"

    dotnet publish "$PROJECT" \
        --configuration Release \
        --runtime "$RID" \
        --self-contained true \
        --no-restore \
        --output "$STAGE_DIR" \
        -p:PublishSingleFile=true \
        -p:IncludeNativeLibrariesForSelfExtract=true \
        -p:PublishTrimmed=false \
        -p:DebugType=embedded

    # Symbols are embedded in the single file; drop any stray PDB or createdump helper.
    find "$STAGE_DIR" -name '*.pdb' -delete
    rm -f "$STAGE_DIR"/createdump*

    EXE_NAME="$APP_NAME"
    [[ "$RID" == win-x64 ]] && EXE_NAME="$APP_NAME.exe"
    if [[ ! -f "$STAGE_DIR/$EXE_NAME" ]]; then
        echo "The app host was not produced at $STAGE_DIR/$EXE_NAME" >&2
        exit 1
    fi
    FILE_COUNT="$(find "$STAGE_DIR" -type f | wc -l | tr -d ' ')"
    if [[ "$FILE_COUNT" != 1 ]]; then
        echo "Expected one file for $RID but found $FILE_COUNT:" >&2
        find "$STAGE_DIR" -type f >&2
        exit 1
    fi

    mkdir -p "$OUT_DIR"
    if [[ "$RID" == win-x64 ]]; then
        mv "$STAGE_DIR/$EXE_NAME" "$OUT_DIR/$EXE_NAME"
        RESULTS+=("$OUT_DIR/$EXE_NAME")
    elif [[ "$(uname -s)" != Darwin ]]; then
        mv "$STAGE_DIR/$EXE_NAME" "$OUT_DIR/$EXE_NAME"
        echo "Skipping the .app bundle: it is assembled on macOS only. Bare executable kept." >&2
        RESULTS+=("$OUT_DIR/$EXE_NAME")
    else
        BUNDLE="$OUT_DIR/$APP_NAME.app"
        CONTENTS="$BUNDLE/Contents"
        mkdir -p "$CONTENTS/MacOS" "$CONTENTS/Resources"
        mv "$STAGE_DIR/$EXE_NAME" "$CONTENTS/MacOS/$APP_NAME"
        chmod +x "$CONTENTS/MacOS/$APP_NAME"
        cp "$ASSETS/$APP_NAME.icns" "$CONTENTS/Resources/$APP_NAME.icns"

        # CFBundleName is limited to 15 characters, which the app name fits.
        cat > "$CONTENTS/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleDevelopmentRegion</key>
  <string>en</string>
  <key>CFBundleDisplayName</key>
  <string>$APP_NAME</string>
  <key>CFBundleIconFile</key>
  <string>$APP_NAME.icns</string>
  <key>CFBundleExecutable</key>
  <string>$APP_NAME</string>
  <key>CFBundleIdentifier</key>
  <string>$BUNDLE_ID</string>
  <key>CFBundleInfoDictionaryVersion</key>
  <string>6.0</string>
  <key>CFBundleName</key>
  <string>$APP_NAME</string>
  <key>CFBundlePackageType</key>
  <string>APPL</string>
  <key>CFBundleShortVersionString</key>
  <string>$VERSION</string>
  <key>CFBundleVersion</key>
  <string>$VERSION</string>
  <key>LSMinimumSystemVersion</key>
  <string>$MIN_MACOS</string>
  <key>NSHighResolutionCapable</key>
  <true/>
</dict>
</plist>
PLIST
        plutil -lint "$CONTENTS/Info.plist"

        # Ad-hoc signing only (no Developer ID, no notarisation).
        codesign --force --deep --sign - "$BUNDLE"
        codesign --verify --deep --strict --verbose=2 "$BUNDLE"
        RESULTS+=("$CONTENTS/MacOS/$APP_NAME")
        RESULTS+=("$BUNDLE")
    fi
done

rm -rf "$STAGING"

echo
echo "Created:"
for PATH_OUT in "${RESULTS[@]}"; do
    if [[ -d "$PATH_OUT" ]]; then
        echo "  $PATH_OUT  (bundle, version $VERSION)"
    else
        echo "  $PATH_OUT  $(file_size "$PATH_OUT")"
    fi
done
