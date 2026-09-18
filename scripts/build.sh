#!/usr/bin/env bash
# Build TextBatchEdit.
# - Non-fixed builds: csproj computes the dynamic 6-minute version itself from the
#   current time; the .temp/TextBatchEdit.DynamicVersion.props written below is kept
#   for traceability only and is NO LONGER IMPORTED by the csproj.
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

# develop 强制动态，或没有固化文件时，写一份 6 分钟桶 props 作为本次构建的可追溯记录
# （csproj 不再导入它；实际版本由 csproj 在非固化分支内按当前时间现算，公式与此处相同）。
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

  # develop 上即使误带固化文件也忽略：指向不存在的路径，使 csproj 进入非固化现算分支。
  EXTRA_ARGS=()
  [[ "$FORCE_DYNAMIC" -eq 1 ]] && \
    EXTRA_ARGS+=("-p:ReleaseVersionFile=$(wslpath -w "$ROOT/.temp/__no-release.props")")
fi

"$DOTNET_EXE" build "$PROJECT_REL" -c "$CONFIG" ${EXTRA_ARGS[@]+"${EXTRA_ARGS[@]}"} "$@"
