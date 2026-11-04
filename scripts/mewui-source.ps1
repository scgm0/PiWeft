# Fetch/update the MewUI source used by PiWeft (source is the only supported build).
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$target = Join-Path $root 'external/MewUI'
$ref = if ($env:MEWUI_REF) { $env:MEWUI_REF } else { 'main' }

if (Test-Path (Join-Path $target '.git')) {
    git -C $target fetch --depth 1 origin $ref
    git -C $target reset --hard FETCH_HEAD
} else {
    # Not initialized yet: fetch the pinned submodule commit (and its nested submodules).
    git -C $root submodule update --init --recursive --depth 1 external/MewUI
}

# MewUI.Svg compiles its SvgNet fork from a submodule; keep every nested submodule in sync.
git -C $target submodule update --init --recursive --depth 1

git -C $target log -1 --format='MewUI source: %h %ad %s' --date=short
