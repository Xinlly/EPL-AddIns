#!/usr/bin/env bash
# Release TextBatchEdit from develop to main.
#
# Workflow:
#   1. Build develop with scripts/build.sh (dynamic 6-minute version).
#   2. Read .temp/build-version.json from the tested build.
#   3. Merge develop into main.
#   4. Freeze the tested version into EA.EplAddIn.TextBatchEdit/release-version.props.
#   5. Build Release on main and verify the real DLL metadata matches (PowerShell -File).
#   6. Commit, tag, and push unless --no-push is specified.

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

PROJECT_REL="EA.EplAddIn.TextBatchEdit/EA.EplAddIn.TextBatchEdit.csproj"
BUILD_VERSION_REL=".temp/build-version.json"
RELEASE_PROPS_REL="EA.EplAddIn.TextBatchEdit/release-version.props"
RELEASE_DLL_REL="EA.EplAddIn.TextBatchEdit/bin/Release/net472/EA.EplAddIn.TextBatchEdit.dll"
BUILD_SCRIPT="scripts/build.sh"
DLL_VERSION_SCRIPT="scripts/get-dll-version.ps1"
TAG_PREFIX="v"
DOTNET_EXE="${DOTNET_EXE:-/mnt/c/Program Files/dotnet/dotnet.exe}"

DRY_RUN=0
NO_PUSH=0
DEVELOP_BRANCH="develop"
MAIN_BRANCH="main"

for arg in "$@"; do
  case "$arg" in
    --dry-run) DRY_RUN=1; NO_PUSH=1 ;;
    --no-push) NO_PUSH=1 ;;
    -h|--help)
      cat <<'USAGE'
Usage: ./scripts/release-from-develop.sh [--dry-run] [--no-push]

Prerequisites:
  - Local branches develop and main exist and the working tree is clean.
  - The tested develop build is produced by scripts/build.sh.
  - Main release version is frozen in EA.EplAddIn.TextBatchEdit/release-version.props.

--dry-run prints planned operations without changing branches or files.
--no-push performs the local release but does not push main/tag.
USAGE
      exit 0 ;;
    *) echo "Unknown argument: $arg" >&2; exit 2 ;;
  esac
done

run() {
  printf '+ '; printf '%q ' "$@"; printf '\n'
  [[ "$DRY_RUN" -eq 0 ]] && "$@"
}

die() { echo "ERROR: $*" >&2; exit 1; }

require_clean_worktree() {
  if [[ -n "$(git status --porcelain)" ]]; then
    echo "ERROR: working tree is not clean. Commit or stash all changes before release." >&2
    git status --short >&2
    exit 1
  fi
}

require_branch() {
  git show-ref --verify --quiet "refs/heads/$1" || die "local branch '$1' does not exist"
}

# Read a value from our single-line build-version.json.
# Handles both "key":"value" and "key":number forms.
json_field() {
  local file="$1" key="$2"
  sed -n "s/.*\"$key\":\"\?\([^\",}]*\)\"\?.*/\1/p" "$file" | head -1
}

require_clean_worktree
require_branch "$DEVELOP_BRANCH"
require_branch "$MAIN_BRANCH"
[[ -f "$PROJECT_REL" ]] || die "missing $PROJECT_REL"
[[ -f "$BUILD_SCRIPT" ]] || die "missing $BUILD_SCRIPT"
[[ -f "$DLL_VERSION_SCRIPT" ]] || die "missing $DLL_VERSION_SCRIPT"
[[ -f "$DOTNET_EXE" ]] || die "dotnet not found: $DOTNET_EXE (set DOTNET_EXE to override)"

ORIGINAL_BRANCH="$(git branch --show-current)"
[[ -n "$ORIGINAL_BRANCH" ]] || die "detached HEAD is not supported"

cleanup() {
  local current
  current="$(git branch --show-current 2>/dev/null || true)"
  if [[ "$DRY_RUN" -eq 0 && -n "$current" && "$current" != "$ORIGINAL_BRANCH" ]]; then
    echo "Release interrupted; returning to '$ORIGINAL_BRANCH'." >&2
    git switch "$ORIGINAL_BRANCH" >/dev/null 2>&1 || true
  fi
}
trap cleanup EXIT

echo "==> Switching to develop and building the tested Debug artifact"
run git switch "$DEVELOP_BRANCH"
run rm -f "$BUILD_VERSION_REL"
run bash "$ROOT/$BUILD_SCRIPT" Debug -v minimal

if [[ "$DRY_RUN" -eq 0 ]]; then
  [[ -f "$BUILD_VERSION_REL" ]] || die "missing $BUILD_VERSION_REL after build"
  assembly_version="$(json_field "$BUILD_VERSION_REL" assemblyVersion)"
  file_version="$(json_field "$BUILD_VERSION_REL" fileVersion)"
  informational_version="$(json_field "$BUILD_VERSION_REL" informationalVersion)"
  build_part="$(json_field "$BUILD_VERSION_REL" buildPart)"
  revision_part="$(json_field "$BUILD_VERSION_REL" revisionPart)"
  generated_at="$(json_field "$BUILD_VERSION_REL" generatedAt)"
  develop_commit="$(git rev-parse HEAD)"
else
  assembly_version="1.0.2609.13202"
  file_version="$assembly_version"
  informational_version="${TAG_PREFIX}${assembly_version}"
  build_part="2609"; revision_part="13202"
  generated_at="DRY-RUN"; develop_commit="DRY-RUN"
fi

tag="${TAG_PREFIX}${assembly_version}"

echo "==> Version from tested develop build"
echo "AssemblyVersion      : $assembly_version"
echo "FileVersion          : $file_version"
echo "InformationalVersion : $informational_version"
echo "Git tag              : $tag"

[[ "$assembly_version" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]] \
  || die "assemblyVersion must be major.minor.build.revision: $assembly_version"

IFS='.' read -r p1 p2 p3 p4 <<< "$assembly_version"
for part in "$p1" "$p2" "$p3" "$p4"; do
  (( part >= 0 && part <= 65534 )) || die "version part out of range [0,65534]: $assembly_version"
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
run bash "$ROOT/$BUILD_SCRIPT" Release -v minimal

if [[ "$DRY_RUN" -eq 0 ]]; then
  [[ -f "$RELEASE_DLL_REL" ]] || die "missing release DLL: $RELEASE_DLL_REL"
  dll_windows="$(wslpath -w "$ROOT/$RELEASE_DLL_REL")"
  version_script_windows="$(wslpath -w "$ROOT/$DLL_VERSION_SCRIPT")"

  declare -A actual
  while IFS='=' read -r k v; do
    actual[$k]="$v"
  done < <(powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$version_script_windows" -Path "$dll_windows" | tr -d '\r')

  echo "Actual AssemblyVersion: ${actual[AssemblyVersion]}"
  echo "Actual FileVersion    : ${actual[FileVersion]}"
  echo "Actual ProductVersion : ${actual[ProductVersion]}"

  [[ "${actual[AssemblyVersion]}" == "$assembly_version" ]] \
    || die "AssemblyVersion mismatch: expected $assembly_version, got ${actual[AssemblyVersion]}"
  [[ "${actual[FileVersion]}" == "$file_version" ]] \
    || die "FileVersion mismatch: expected $file_version, got ${actual[FileVersion]}"
  [[ "${actual[ProductVersion]}" == "$informational_version" ]] \
    || die "InformationalVersion mismatch: expected $informational_version, got ${actual[ProductVersion]}"
fi

echo "==> Creating release commit and tag"
run git add "$RELEASE_PROPS_REL"
run git commit -m "chore(release): 固化版本 $tag"
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
