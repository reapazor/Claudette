#!/usr/bin/env bash
# Builds Claudette.app and a .dmg for one architecture (DESIGN.md §2, "Packaging"), then signs and notarizes them when
# credentials are given. Without them it still builds, unsigned, which is what pull request checks do.
#
#   packaging/macos/build-dmg.sh <version> <arm64|x64> [output folder]
#
# Signing:       MACOS_SIGNING_IDENTITY  "Developer ID Application: Name (TEAMID)", in the default or an unlocked keychain
# Notarization:  APPLE_API_KEY_PATH, APPLE_API_KEY_ID, APPLE_API_ISSUER   (an App Store Connect API key), or
#                APPLE_ID, APPLE_TEAM_ID, APPLE_APP_PASSWORD             (an app-specific password)
#
# Off macOS (no iconutil, codesign or hdiutil) it stops after building the .app, which checks the bundle layout.
set -euo pipefail

version="${1:?version, such as 1.2.3}"
arch="${2:?arm64 or x64}"
root="$(cd "$(dirname "$0")/../.." && pwd)"
output="${3:-$root/artifacts}"
here="$root/packaging/macos"
work="$root/obj/package/osx-$arch"
app="$work/Claudette.app"
mkdir -p "$output"
rm -rf "$work"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"

echo "Publishing Claudette $version for osx-$arch"
dotnet publish "$root/src/Claudette.App/Claudette.App.csproj" -c Release -r "osx-$arch" --self-contained \
  -p:Version="$version" -p:DebugType=none -o "$app/Contents/MacOS"
sed "s/[$]VERSION[$]/$version/g" "$here/Info.plist" > "$app/Contents/Info.plist"

if ! command -v iconutil >/dev/null; then
  echo "Not on macOS: built $app without an icon, signature or .dmg."
  exit 0
fi

# The icon, from the 1024-pixel master.
iconset="$work/Claudette.iconset"
mkdir -p "$iconset"
for size in 16 32 128 256 512; do
  sips -z "$size" "$size" "$root/packaging/icon/claudette-1024.png" --out "$iconset/icon_${size}x${size}.png" >/dev/null
  sips -z $((size * 2)) $((size * 2)) "$root/packaging/icon/claudette-1024.png" --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$iconset" -o "$app/Contents/Resources/Claudette.icns"

identity="${MACOS_SIGNING_IDENTITY:-}"
if [[ -n "$identity" ]]; then
  echo "Signing with $identity"
  sign() { codesign --force --timestamp --options runtime --entitlements "$here/Claudette.entitlements" --sign "$identity" "$@"; }
  # Inside out: every native library and executable, then the bundle.
  while IFS= read -r -d '' file; do
    if file -b "$file" | grep -q 'Mach-O'; then
      sign "$file"
    fi
  done < <(find "$app/Contents/MacOS" -type f -print0)
  sign "$app"
  codesign --verify --deep --strict --verbose=2 "$app"
else
  echo "warning: MACOS_SIGNING_IDENTITY isn't set, so the app is unsigned and Gatekeeper will block it." >&2
fi

dmg="$output/Claudette-$version-$arch.dmg"
staging="$work/dmg"
mkdir -p "$staging"
cp -R "$app" "$staging/"
ln -s /Applications "$staging/Applications"
hdiutil create -volname "Claudette" -srcfolder "$staging" -ov -format UDZO "$dmg"

if [[ -n "$identity" ]]; then
  codesign --force --timestamp --sign "$identity" "$dmg"
  if [[ -n "${APPLE_API_KEY_PATH:-}" ]]; then
    xcrun notarytool submit "$dmg" --key "$APPLE_API_KEY_PATH" --key-id "$APPLE_API_KEY_ID" --issuer "$APPLE_API_ISSUER" --wait
  elif [[ -n "${APPLE_ID:-}" ]]; then
    xcrun notarytool submit "$dmg" --apple-id "$APPLE_ID" --team-id "$APPLE_TEAM_ID" --password "$APPLE_APP_PASSWORD" --wait
  else
    echo "warning: no notarization credentials, so the .dmg isn't notarized." >&2
  fi
  if [[ -n "${APPLE_API_KEY_PATH:-}${APPLE_ID:-}" ]]; then
    xcrun stapler staple "$dmg"
  fi
fi
echo "Built $dmg"
