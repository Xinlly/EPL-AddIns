#!/usr/bin/env bash
# Release TextBatchEdit from develop to main.
#
# Workflow:
#   1. Build develop with scripts/build.sh (dynamic version).
#   2. Read .temp/build-version.json from the tested build.
#   3. Merge develop into main.
#   4. Freeze the tested version into EA.EplAddIn.TextBatchEdit/release-version.props.
#   5. Build Release on main and verify DLL metadata matches the frozen version.
#   6. Commit, tag, and push unless --no-push is specified.

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

PROJECT_REL="EA.EplAddIn.TextBatchEdit/EA.EplAddIn.TextBatchEdit.csproj"
DYNAMIC_VERSION_REL=".temp/build-version.json"
RELEASE_PROPS_REL="EA.EplAddIn.TextBatchEdit/release-version.props"
BUILD_SCRIPT="scripts/build.sh"
TAG_PREFIX="v"
DOTNET_EXE="${DOTNET_EXE:-/mnt/c/Program Files/dotnet/dotnet.exe}"

DRY_RUN=0
NO_PUSH=0
DEVELOP_BRANCH="develop"
MAIN_BRANCH="main"

for arg in "$@"; do
  case "$arg" in
    --dry-run)
      DRY_RUN=1
      NO_PUSH=1
      ;;
    --no-push)
      NO_PUSH=1
      ;;
    -h|--help)
      cat <<'USAGE'
Usage: ./scripts/release-from-develop.sh [--dry-run] [--no-push]

Prerequisites:
  - Local branches develop and main exist.
  - The working tree must be clean before release.
  - The tested develop build is produced with scripts/build.sh.
  - Main release version is frozen in EA.EplAddIn.TextBatchEdit/release-version.props.

--dry-run prints planned operations without changing branches or files.
--no-push performs the local release but does not push main/tag.
USAGE
      exit 0
      ;;
    *)
      echo "Unknown argument: $arg" >&2
      exit 2
      ;;
  esac
done

run() {
  printf '+ '
  printf '%q ' "$@"
  printf '\n'
  if [[ "$DRY_RUN" -eq 0 ]]; then
    "$@"
  fi
}

die() {
  echo "ERROR: $*" >&2
  exit 1
}

require_clean_worktree() {
  if [[ -n "$(git status --porcelain)" ]]; then
    echo "ERROR: working tree is not clean. Commit or stash all changes before release." >&2
    git status --short >&2
    exit 1
  fi
}

require_branch() {
  local branch="$1"
  git show-ref --verify --quiet "refs/heads/$branch" || die "local branch '$branch' does not exist"
}

require_file() {
  local path="$1"
  [[ -f "$path" ]] || die "required file does not exist: $path"
}

json_get() {
  local file="$1"
  local key="$2"
  powershell.exe -NoProfile -ExecutionPolicy Bypass -Command \
    "[string](Get-Content -LiteralPath \$args[0] -Raw -Encoding UTF8 | ConvertFrom-Json).\$args[1]" \
    "$(wslpath -w "$file")" "$key" | tr -d '\r'
}

get_assembly_version() {
  local dll_windows="$1"
  powershell.exe -NoProfile -ExecutionPolicy Bypass -Command \
    "[System.Reflection.AssemblyName]::GetAssemblyName(\$args[0]).Version.ToString()" \
    "$dll_windows" | tr -d '\r'
}

get_file_version_info() {
  local dll_windows="$1"
  local property="$2"
  powershell.exe -NoProfile -ExecutionPolicy Bypass -Command \
    "[string](Get-Item -LiteralPath \$args[0]).VersionInfo.\$args[1]" \
    "$dll_windows" "$property" | tr -d '\r'
}

require_clean_worktree
require_branch "$DEVELOP_BRANCH"
require_branch "$MAIN_BRANCH"
require_file "$PROJECT_REL"
require_file "$BUILD_SCRIPT"
[[ -x "$BUILD_SCRIPT" ]] || chmod +x "$BUILD_SCRIPT"
[[ -f "$DOTNET_EXE" ]] || die "dotnet executable not found: $DOTNET_EXE (set DOTNET_EXE to override)"

original_branch="$(git branch --show-current)"
[[ -n "$original_branch" ]] || die "detached HEAD is not supported"

cleanup() {
  local current
  current="$(git branch --show-current || true)"
  if [[ "$DRY_RUN" -eq 0 && -n "$current" && "$current" != "$original_branch" ]]; then
    echo "Release interrupted; returning to original branch '$original_branch'." >&2
    git switch "$original_branch" >/dev/null 2>&1 || true
  fi
}
trap cleanup EXIT

echo "==> Switching to develop and building the tested Debug artifact"
run git switch "$DEVELOP_BRANCH"
run rm -f "$DYNAMIC_VERSION_REL"
run "$ROOT/$BUILD_SCRIPT" Debug -v minimal

if [[ "$DRY_RUN" -eq 0 ]]; then
  require_file "$DYNAMIC_VERSION_REL"
  assembly_version="$(json_get "$DYNAMIC_VERSION_REL" assemblyVersion)"
  file_version="$(json_get "$DYNAMIC_VERSION_REL" fileVersion)"
  informational_version="$(json_get "$DYNAMIC_VERSION_REL" informationalVersion)"
  build_part="$(json_get "$DYNAMIC_VERSION_REL" buildPart)"
  revision_part="$(json_get "$DYNAMIC_VERSION_REL" revisionPart)"
  generated_at="$(json_get "$DYNAMIC_VERSION_REL" generatedAt)"
  develop_commit="$(git rev-parse HEAD)"
else
  assembly_version="1.0.2609.13202"
  file_version="$assembly_version"
  informational_version="${TAG_PREFIX}${assembly_version}"
  build_part="2609"
  revision_part="13202"
  generated_at="DRY-RUN"
  develop_commit="DRY-RUN"
fi

tag="${TAG_PREFIX}${assembly_version}"

echo "==> Version from tested develop build"
echo "AssemblyVersion      : $assembly_version"
echo "FileVersion          : $file_version"
echo "InformationalVersion : $informational_version"
echo "Git tag              : $tag"

if [[ ! "$assembly_version" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  die "assemblyVersion must be major.minor.build.revision: $assembly_version"
fi

IFS='.' read -r major minor build_part_check revision_part_check <<< "$assembly_version"
for part in "$major" "$minor" "$build_part_check" "$revision_part_check"; do
  (( part >= 0 && part <= 65534 )) || die "assembly version part out of range [0,65534]: $assembly_version"
done

git rev-parse -q --verify "refs/tags/$tag" >/dev/null && die "tag already exists: $tag"

release_props_tmp="$(mktemp)"
cat > "$release_props_tmp" <<PROPS
<Project>
  <PropertyGroup>
    <VersionBuildPart>$build_part</VersionBuildPart>
    <VersionRevisionPart>$revision_part</VersionRevisionPart>
  </PropertyGroup>
</Project>
PROPS

echo "==> Merging develop into main"
run git switch "$MAIN_BRANCH"
run git merge --no-ff "$DEVELOP_BRANCH" -m "merge: develop $tag"

echo "==> Freezing release version"
if [[ "$DRY_RUN" -eq 0 ]]; then
  install -m 0644 "$release_props_tmp" "$RELEASE_PROPS_REL"
else
  printf '+ write %s\n' "$RELEASE_PROPS_REL"
fi
rm -f "$release_props_tmp"

echo "==> Building Release on main"
run "$ROOT/$BUILD_SCRIPT" Release -v minimal

if [[ "$DRY_RUN" -eq 0 ]]; then
  release_dll="$ROOT/EA.EplAddIn.TextBatchEdit/bin/Release/net472/EA.EplAddIn.TextBatchEdit.dll"
  require_file "$release_dll"
  release_dll_windows="$(wslpath -w "$release_dll")"

  actual_assembly_version="$(get_assembly_version "$release_dll_windows")"
  actual_file_version="$(get_file_version_info "$release_dll_windows" FileVersion)"
  actual_product_version="$(get_file_version_info "$release_dll_windows" ProductVersion)"

  echo "Actual AssemblyVersion: $actual_assembly_version"
  echo "Actual FileVersion    : $actual_file_version"
  echo "Actual ProductVersion : $actual_product_version"

  [[ "$actual_assembly_version" == "$assembly_version" ]] \
    || die "DLL AssemblyVersion mismatch: expected $assembly_version, got $actual_assembly_version"
  [[ "$actual_file_version" == "$file_version" ]] \
    || die "DLL FileVersion mismatch: expected $file_version, got $actual_file_version"
  [[ "$actual_product_version" == "$informational_version" ]] \
    || die "DLL InformationalVersion mismatch: expected $informational_version, got $actual_product_version"
fi

commit_message="chore(release): 固化版本 $tag"
echo "==> Creating release commit and tag"
run git add "$RELEASE_PROPS_REL"
run git commit -m "$commit_message"
run git tag -a "$tag" -m "TextBatchEdit $tag

Source develop commit: $develop_commit
Dynamic build record generated at: $generated_at"

if [[ "$NO_PUSH" -eq 0 ]]; then
  echo "==> Pushing main and tag"
  run git push origin "$MAIN_BRANCH"
  run git push origin "$tag"
else
  echo "==> Push skipped (--no-push or --dry-run)"
fi

echo "==> Release preparation finished: $tag"
