#!/usr/bin/env bash
# Regenerate the PiWeft application icon assets from piweft.svg.
#
# Only needed when the icon changes; the generated files are committed.
# Requires rsvg-convert (librsvg) and ImageMagick (magick) on PATH.
#
# Kept in the repo:
#   piweft.ico          Windows exe icon and Window.Icon
#   png/piweft-1024.png macOS iconset source (sips downsizes it at package time)
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
dir="$root/src/PiWeft.Desktop/Resources/appicon"
svg="$dir/piweft.svg"

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

for size in 16 24 32 48 64 128 256 512 1024; do
  rsvg-convert -w "$size" -h "$size" "$svg" -o "$tmp/piweft-$size.png"
done

mkdir -p "$dir/png"
cp "$tmp/piweft-1024.png" "$dir/png/"

# ICO caps at 256; keep small-to-large order, which is what Windows expects.
magick "$tmp"/piweft-{16,24,32,48,64,128,256}.png "$dir/piweft.ico"

echo "wrote $dir/piweft.ico and png/piweft-1024.png"
