#!/usr/bin/env bash
# Build TextBatchEdit.
# - Dynamic develop builds: generate .temp/TextBatchEdit.DynamicVersion.props first.
# - Fixed main/tag builds: commit EA.EplAddIn.TextBatchEdit/release-version.props, and it takes precedence.

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
PROJECT_REL="EA.EplAddIn.TextBatchEdit/EA.EplAddIn.TextBatchEdit.csproj"
DYNAMIC_PROPS="$ROOT/.temp/TextBatchEdit.DynamicVersion.props"
GENERATOR="$ROOT/scripts/generate-dynamic-version.ps1"
DOTNET_EXE="${DOTNET_EXE:-/mnt/c/Program Files/dotnet/dotnet.exe}"
DEVELOP_BRANCH="develop"

CONFIG="${1:-Debug}"
shift $(( $# > 0 ? 1 : 0 )) || true

EXTRA_ARGS=()
BRANCH="$(git -C "$ROOT" branch --show-current 2>/dev/null || true)"
if [[ "$BRANCH" == "$DEVELOP_BRANCH" ]]; then
  # develop 上强制动态：把固化文件路径指到不存在的文件，使 csproj 回落到动态 props。
  EXTRA_ARGS+=("-p:ReleaseVersionFile=$(wslpath -w "$ROOT/.temp/__no-release.props")")
fi

if [[ ! -f "$ROOT/EA.EplAddIn.TextBatchEdit/release-version.props" || "$BRANCH" == "develop" ]]; then
  win_generator="$(wslpath -w "$GENERATOR")"
  win_props="$(wslpath -w "$DYNAMIC_PROPS")"
  mkdir -p "$ROOT/.temp"
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$win_generator" -PropsPath "$win_props"
fi

"$DOTNET_EXE" build "$PROJECT_REL" -c "$CONFIG" "${EXTRA_ARGS[@]}" "$@"
