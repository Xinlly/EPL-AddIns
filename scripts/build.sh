#!/usr/bin/env bash
# Build TextBatchEdit.
# - Dynamic develop builds: write .temp/TextBatchEdit.DynamicVersion.props (6-minute bucket) first.
# - Fixed main/tag builds: committed EA.EplAddIn.TextBatchEdit/release-version.props takes precedence.

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

PROJECT_REL="EA.EplAddIn.TextBatchEdit/EA.EplAddIn.TextBatchEdit.csproj"
RELEASE_PROPS="$ROOT/EA.EplAddIn.TextBatchEdit/release-version.props"
DYNAMIC_PROPS="$ROOT/.temp/TextBatchEdit.DynamicVersion.props"
DOTNET_EXE="${DOTNET_EXE:-/mnt/c/Program Files/dotnet/dotnet.exe}"
DEVELOP_BRANCH="develop"

CONFIG="${1:-Debug}"
shift $(( $# > 0 ? 1 : 0 )) || true

BRANCH="$(git branch --show-current 2>/dev/null || true)"
FORCE_DYNAMIC=0
[[ "$BRANCH" == "$DEVELOP_BRANCH" ]] && FORCE_DYNAMIC=1

# develop 强制动态，或没有固化文件时，生成 6 分钟粒度的动态版本 props。
if [[ "$FORCE_DYNAMIC" -eq 1 || ! -f "$RELEASE_PROPS" ]]; then
  build_part="$(date +%y%m)"
  day=$((10#$(date +%d)))
  hour=$((10#$(date +%H)))
  minute=$((10#$(date +%M)))
  revision_part=$((day * 1000 + hour * 10 + minute / 6))

  mkdir -p "$ROOT/.temp"
  cat > "$DYNAMIC_PROPS" <<PROPS
<Project>
  <PropertyGroup>
    <VersionBuildPart>$build_part</VersionBuildPart>
    <VersionRevisionPart>$revision_part</VersionRevisionPart>
  </PropertyGroup>
</Project>
PROPS

  # develop 上即使误带固化文件也忽略：指向不存在的路径，使 csproj 回落到动态 props。
  EXTRA_ARGS=()
  [[ "$FORCE_DYNAMIC" -eq 1 ]] && \
    EXTRA_ARGS+=("-p:ReleaseVersionFile=$(wslpath -w "$ROOT/.temp/__no-release.props")")
fi

"$DOTNET_EXE" build "$PROJECT_REL" -c "$CONFIG" ${EXTRA_ARGS[@]+"${EXTRA_ARGS[@]}"} "$@"
