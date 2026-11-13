#!/bin/bash
#
# Builds Account Deck for macOS: dist/Account Deck.app, plus dist/AccountDeck-mac.zip to hand out.
# The app is self-contained (includes .NET), so the Mac it runs on needs nothing else installed.
#
#   ./build-mac.sh              # test, then build for this Mac's processor (Apple silicon or Intel)
#   ./build-mac.sh --install    # same, then copy to ~/Applications and open it
#   ./build-mac.sh --arch x64   # build for Intel Macs (arm64 for Apple silicon)
#
# Needs the .NET 10 SDK: https://dotnet.microsoft.com/download (or ~/.dotnet from dotnet-install.sh).
set -euo pipefail

repo="$(cd "$(dirname "$0")" && pwd)"
arch="$(uname -m | sed 's/x86_64/x64/')"
install=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --install) install=1 ;;
    --arch) arch="$2"; shift ;;
    *) echo "Unknown option: $1" >&2; exit 2 ;;
  esac
  shift
done

dotnet="$(command -v dotnet || true)"
[[ -z "$dotnet" && -x "$HOME/.dotnet/dotnet" ]] && dotnet="$HOME/.dotnet/dotnet"
[[ -n "$dotnet" ]] || { echo "The .NET 10 SDK is required: https://dotnet.microsoft.com/download" >&2; exit 1; }

version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$repo/src/AccountDeck.Mac/AccountDeck.Mac.csproj")"
build="$(mktemp -d "${TMPDIR:-/tmp}/AccountDeck-build.XXXXXX")"
trap 'rm -rf "$build"' EXIT
dist="$repo/dist"
app="$dist/Account Deck.app"

echo "Running tests..."
"$dotnet" test "$repo/tests/AccountDeck.Tests" --nologo -v q

echo "Building Account Deck $version for osx-$arch..."
"$dotnet" publish "$repo/src/AccountDeck.Mac" -c Release -r "osx-$arch" --self-contained true \
  -p:DebugType=none -p:UseAppHost=true --nologo -v q -o "$build/out"

echo "Making the app bundle..."
rm -rf "$app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp -R "$build/out/." "$app/Contents/MacOS/"

# The icon is drawn by the app itself, so the repository carries no binary assets.
iconset="$build/AccountDeck.iconset"
mkdir -p "$iconset"
"$app/Contents/MacOS/AccountDeck" --export-icon "$build/icon.png"
for size in 16 32 128 256 512; do
  sips -z $size $size "$build/icon.png" --out "$iconset/icon_${size}x${size}.png" >/dev/null
  sips -z $((size * 2)) $((size * 2)) "$build/icon.png" --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$iconset" -o "$app/Contents/Resources/AccountDeck.icns"

cat > "$app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>Account Deck</string>
    <key>CFBundleDisplayName</key><string>Account Deck</string>
    <key>CFBundleIdentifier</key><string>io.github.novruzaliyevdev.accountdeck</string>
    <key>CFBundleExecutable</key><string>AccountDeck</string>
    <key>CFBundleIconFile</key><string>AccountDeck</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleShortVersionString</key><string>$version</string>
    <key>CFBundleVersion</key><string>$version</string>
    <key>LSMinimumSystemVersion</key><string>12.0</string>
    <key>LSApplicationCategoryType</key><string>public.app-category.utilities</string>
    <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
PLIST

# Ad-hoc signature: required for Apple silicon to run it. It is not notarized, so the first start
# needs right-click > Open (or System Settings > Privacy & Security > Open Anyway).
codesign --force --deep --sign - "$app"

rm -f "$dist/AccountDeck-mac.zip"
ditto -c -k --keepParent "$app" "$dist/AccountDeck-mac.zip"
echo "Built: $app"
echo "       $dist/AccountDeck-mac.zip"

if [[ $install -eq 1 ]]; then
  target="$HOME/Applications/Account Deck.app"
  pkill -f "$target/Contents/MacOS/AccountDeck" 2>/dev/null || true
  mkdir -p "$HOME/Applications"
  rm -rf "$target"
  cp -R "$app" "$target"
  echo "Installed to $target"
  open "$target"
fi
