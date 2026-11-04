# Sync the MewUI source submodule (the only supported build) and its nested submodules.
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$target = Join-Path $root 'external/MewUI'
$ref = if ($env:MEWUI_REF) { $env:MEWUI_REF } else { 'main' }

if (Test-Path (Join-Path $target '.git')) {
    git -C $target fetch --depth 1 origin $ref
    git -C $target reset --hard FETCH_HEAD
} else {
    git -C $root submodule update --init --recursive --depth 1 external/MewUI
}
git -C $target submodule update --init --recursive --depth 1
git -C $target log -1 --format='MewUI source: %h %ad %s' --date=short
