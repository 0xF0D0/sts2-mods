#!/usr/bin/env bash
#
# package.sh - Build and package Slay the Spire 2 mods for release.
#
# For each mod name given (or UndoSync + PeerView by default), this script:
#   1. Runs `dotnet build -c Release` for the mod.
#   2. Reads the mod's version out of its <Mod>.json manifest.
#   3. Verifies the built <Mod>.dll exists.
#   4. Stages <Mod>.dll + <Mod>.json into dist/stage/<Mod>/.
#   5. Zips dist/stage/<Mod>/ into dist/<Mod>-v<version>.zip, with the
#      <Mod>/ folder itself as the zip's single top-level entry so it can
#      be extracted directly into the game's mods/ directory.
#
# It does NOT touch git and does NOT create a GitHub release - it only
# prints the `gh release create` command you'd run to do that yourself.
#
# Usage:
#   ./tools/package.sh [ModName ...]
#
# Examples:
#   ./tools/package.sh                  # package UndoSync and PeerView
#   ./tools/package.sh UndoSync         # package only UndoSync
#   ./tools/package.sh PeerView UndoSync  # package both, PeerView first
#
set -euo pipefail

# Resolve the repo root from this script's own location, not $PWD, so the
# script behaves the same no matter what directory it's invoked from.
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$SCRIPT_DIR/.." && pwd)"

# Resolve the dotnet binary: prefer the Homebrew path, fall back to PATH.
DOTNET="${DOTNET:-/opt/homebrew/bin/dotnet}"
if [ ! -f "$DOTNET" ]; then
  DOTNET="dotnet"
fi

# Mods to package: positional args if given, otherwise both known mods.
if [ "$#" -gt 0 ]; then
  mods=("$@")
else
  mods=("UndoSync" "PeerView")
fi

package_mod() {
  local mod="$1"
  local mod_dir="$repo_root/$mod"
  local manifest="$mod_dir/$mod.json"
  local dll_path="$mod_dir/bin/Release/net9.0/$mod.dll"
  local stage_dir="$repo_root/dist/stage/$mod"

  echo "==> Building $mod"
  (cd "$mod_dir" && "$DOTNET" build -c Release)

  local version
  version=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["version"])' "$manifest")

  echo "==> Packaging $mod v$version"

  if [ ! -f "$dll_path" ]; then
    echo "error: expected build output not found for $mod: $dll_path" >&2
    exit 1
  fi

  # Stage a clean copy of exactly the two files that make up a full install.
  rm -rf "$stage_dir"
  mkdir -p "$stage_dir"
  cp "$dll_path" "$stage_dir/$mod.dll"
  cp "$manifest" "$stage_dir/$mod.json"

  local zip_path="$repo_root/dist/$mod-v$version.zip"
  rm -f "$zip_path"

  # Zip from inside dist/stage/ so the archive's single top-level entry is
  # the "<Mod>/" folder itself (containing <Mod>.dll + <Mod>.json). That
  # way a user can extract the zip directly into the game's mods/
  # directory and end up with mods/<Mod>/<Mod>.dll + mods/<Mod>/<Mod>.json.
  (cd "$repo_root/dist/stage" && zip -r "../$mod-v$version.zip" "$mod")

  echo "==> Done: dist/$mod-v$version.zip"
}

for mod in "${mods[@]}"; do
  package_mod "$mod"
done

# Scratch space only - remove it now that every mod has been staged and zipped.
rm -rf "$repo_root/dist/stage"

echo
echo "==> Packaged files:"
(cd "$repo_root" && ls -lh dist/*.zip)

echo
echo "==> Suggested release commands (not run automatically):"
for mod in "${mods[@]}"; do
  manifest="$repo_root/$mod/$mod.json"
  version=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["version"])' "$manifest")
  echo "gh release create $mod-v$version dist/$mod-v$version.zip --title \"$mod v$version\" --notes \"...\""
done
