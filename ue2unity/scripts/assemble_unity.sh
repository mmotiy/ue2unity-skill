#!/usr/bin/env bash
# Usage: bash assemble_unity.sh EXPORT EMPTY_OUTPUT [assembler options]
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PYTHON_BIN="${PYTHON_EXECUTABLE:-python}"
"$PYTHON_BIN" "$SCRIPT_DIR/assemble_unity.py" "$@"
ASSEMBLED_ASSETS="$("$PYTHON_BIN" -c 'import json,sys; print(json.load(open(sys.argv[1], encoding="utf-8"))["assets"])' "$2/Report/Assembly.json")"
"$PYTHON_BIN" "$SCRIPT_DIR/pack_unitypackage.py" "$ASSEMBLED_ASSETS" "$2/CityPacks_Unity6.unitypackage"
