#!/usr/bin/env bash
# Assemble the Unity 6 deliverable from a CityExporter output directory.
# Usage: bash assemble_unity.sh <export_dir> <out_root>
set -euo pipefail

EXPORT="$1"        # e.g. D:/citypack_export_v2 (contains Meshes/ Textures/ materials.json)
OUT="$2"           # e.g. D:/CityPacks_Unity6
WORK="$(cd "$(dirname "$0")" && pwd)"

ASSETS="$OUT/Assets/CityPacks"
mkdir -p "$ASSETS/Editor" "$OUT/Report"

echo "[1/5] copying meshes (gltf+bin)"
mkdir -p "$ASSETS/Meshes"
(cd "$EXPORT/Meshes" && find . -type f \( -name "*.gltf" -o -name "*.bin" \) -print0) |
  (cd "$EXPORT/Meshes" && xargs -0 -I{} install -D "{}" "$ASSETS/Meshes/{}")

echo "[2/5] copying textures (png)"
mkdir -p "$ASSETS/Textures"
(cd "$EXPORT/Textures" && find . -type f -name "*.png" -print0) |
  (cd "$EXPORT/Textures" && xargs -0 -I{} install -D "{}" "$ASSETS/Textures/{}")

echo "[3/5] editor script + readme"
install -m 644 "$WORK/unity/Editor/TextureImportPostprocessor.cs" "$ASSETS/Editor/"
install -m 644 "$WORK/unity/README_Unity6.md" "$ASSETS/../README_Unity6.md" 2>/dev/null || \
  install -m 644 "$WORK/unity/README_Unity6.md" "$OUT/Assets/README_Unity6.md"

echo "[4/5] report files"
[ -f "$EXPORT/materials.json" ] && install -m 644 "$EXPORT/materials.json" "$OUT/Report/"
[ -f "$EXPORT/export_errors.log" ] && install -m 644 "$EXPORT/export_errors.log" "$OUT/Report/"

echo "[5/5] building .unitypackage (this may take a while)"
python "$WORK/pack_unitypackage.py" "$ASSETS" "$OUT/CityPacks_Unity6.unitypackage" --assets-root Assets/CityPacks

echo "done:"
du -sh "$OUT"/*
