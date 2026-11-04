#!/usr/bin/env bash
# Sync the MewUI source submodule (the only supported build) and its nested submodules.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
target="$root/external/MewUI"
ref="${MEWUI_REF:-main}"

if [[ -e "$target/.git" ]]; then
  git -C "$target" fetch --depth 1 origin "$ref"
  git -C "$target" reset --hard FETCH_HEAD
else
  git -C "$root" submodule update --init --recursive --depth 1 external/MewUI
fi
git -C "$target" submodule update --init --recursive --depth 1
git -C "$target" log -1 --format='MewUI source: %h %ad %s' --date=short
