#!/usr/bin/env bash
# Build, test, package and publish a WeaponPaints release from this machine.
#
# There is no CI: releases are cut locally and pushed to origin
# (github.com/mrc4tt/cs2-WeaponPaints) with the gh CLI.
#
#   ./release.sh                 bump patch (4.1.3 -> 4.1.4) and release
#   ./release.sh minor|major     bump minor/major and release
#   ./release.sh 4.2.0           release an explicit version
#   ./release.sh --dry-run [..]  build, test and package only; nothing is committed or pushed
#
# VERSION is the single source of truth; a release is tagged v<VERSION>.
# If VERSION is not tagged yet (bumped by hand), it is released as-is.
# Release notes come from the "## [x.y.z]" section of CHANGELOG.md. When that
# section is missing it is generated from the commit subjects since the last tag
# (feat: -> Added, fix: -> Fixed, everything else -> Changed).
#
# Requires: dotnet 10 SDK, zip, gh (logged in). All packages come from nuget.org.
set -euo pipefail
# Never exit silently: say which command stopped the release.
trap 'echo "release: failed at line $LINENO: $BASH_COMMAND" >&2' ERR

NAME="WeaponPaints"
PROJECT="WeaponPaints.csproj"
REMOTE="origin"
BRANCH="main"
# Provided by the CSSharp runtime / MenuManagerCore; must never ship.
RUNTIME_DLLS=(
    CounterStrikeSharp.API.dll
    McMaster.NETCore.Plugins.dll
    Microsoft.DotNet.PlatformAbstractions.dll
    Microsoft.Extensions.DependencyModel.dll
    CS2MenuManager.dll
)

fail() { echo "release: $*" >&2; exit 1; }
run() { echo "+ $*"; "$@"; }
tag_exists() { git rev-parse -q --verify "refs/tags/$1" >/dev/null; }

cd "$(dirname "$(readlink -f "$0")")"

# A release tag on origin can end up on the wrong commit (gh made v4.1.3 itself, on the 4.1.2
# commit, while the release hung as a draft); git fetch --tags then refuses to clobber the local
# one. Repair it: when a local v<x.y.z> tag sits on this repo's own "chore: release x.y.z" commit
# and origin's does not, origin's is the broken one and is overwritten. Anything else is left
# alone for a human to sort out (git fetch fails below with git's own message).
sync_release_tags() {
    local sha ref tag version local_commit remote_commit
    while read -r sha ref; do
        [[ $ref == refs/tags/v* && $ref != *'^{}' ]] || continue
        tag=${ref#refs/tags/}
        version=${tag#v}
        tag_exists "$tag" || continue
        local_commit=$(git rev-parse "$tag^{commit}")
        remote_commit=$(git ls-remote "$REMOTE" "refs/tags/$tag^{}" | cut -f1)
        [[ -n $remote_commit ]] || remote_commit=$sha    # lightweight tag on origin
        [[ $local_commit == "$remote_commit" ]] && continue
        if [[ $(git log -1 --format=%s "$local_commit") == "chore: release $version" ]] \
            && [[ $(git log -1 --format=%s "$remote_commit" 2>/dev/null) != "chore: release $version" ]]; then
            echo "release: $REMOTE's $tag points at ${remote_commit:0:7}, not the $version release commit ${local_commit:0:7} - fixing it"
            run git push --force "$REMOTE" "refs/tags/$tag"
        fi
    done < <(git ls-remote --tags "$REMOTE" 'v*')
}

dry_run=0
bump="patch"
for arg in "$@"; do
    case "$arg" in
        --dry-run) dry_run=1 ;;
        patch|minor|major) bump="$arg" ;;
        [0-9]*.[0-9]*.[0-9]*) bump="$arg" ;;
        -h|--help) sed -n '2,18p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) fail "unknown argument '$arg' (see --help)" ;;
    esac
done

for tool in dotnet zip git; do
    command -v "$tool" >/dev/null || fail "$tool not found"
done
[[ $dry_run == 1 ]] || command -v gh >/dev/null || fail "gh not found"

# --- repo checks -------------------------------------------------------------
if [[ $dry_run == 0 ]]; then
    [[ $(git rev-parse --abbrev-ref HEAD) == "$BRANCH" ]] || fail "not on $BRANCH"
    [[ -z $(git status --porcelain --untracked-files=no) ]] \
        || fail "working tree has uncommitted changes to tracked files - commit or stash them"
    sync_release_tags
    # Not --quiet: a tag that still differs from origin's is rejected ("would clobber existing
    # tag"), and that message is the only hint at what went wrong.
    run git fetch "$REMOTE" --tags \
        || fail "git fetch failed - a local tag that differs from $REMOTE's is the usual cause (git fetch $REMOTE --tags shows which)"
    git merge-base --is-ancestor "$REMOTE/$BRANCH" HEAD \
        || fail "$BRANCH is behind $REMOTE/$BRANCH - pull first"
    gh auth status >/dev/null 2>&1 || fail "gh is not logged in (gh auth login)"
fi

# --- version -----------------------------------------------------------------
current=$(tr -d '[:space:]' < VERSION)
[[ $current =~ ^([0-9]+)\.([0-9]+)\.([0-9]+)$ ]] || fail "VERSION '$current' is not x.y.z"
major=${BASH_REMATCH[1]} minor=${BASH_REMATCH[2]} patch=${BASH_REMATCH[3]}

if [[ $bump =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    version="$bump"
elif ! tag_exists "v$current"; then
    version="$current"    # bumped by hand, not released yet
else
    case "$bump" in
        patch) version="$major.$minor.$((patch + 1))" ;;
        minor) version="$major.$((minor + 1)).0" ;;
        major) version="$((major + 1)).0.0" ;;
    esac
fi
tag="v$version"
tag_exists "$tag" && fail "tag $tag already exists"

prev_tag=$(git describe --tags --abbrev=0 --match 'v[0-9]*' 2>/dev/null || true)
echo "release: $NAME $version (previous: ${prev_tag:-none})"

# VERSION/CHANGELOG.md are edited before the build (the csproj embeds VERSION),
# so put them back if anything fails before the release commit.
committed=0
work=$(mktemp -d -t wp-release-XXXXXX)
cleanup() {
    if [[ $committed == 0 ]]; then
        git checkout -q -- VERSION CHANGELOG.md 2>/dev/null || true
    fi
}
trap cleanup EXIT

# --- changelog ---------------------------------------------------------------
section_body() {
    awk -v v="$1" '
        index($0, "## [" v "]") == 1 { on = 1; next }
        on && /^## \[/ { exit }
        on { print }
    ' CHANGELOG.md | sed -e '/./,$!d' | tac | sed -e '/./,$!d' | tac
}

notes=$(section_body "$version")
if [[ -z $notes ]]; then
    range="${prev_tag:+$prev_tag..}HEAD"
    added=() fixed=() changed=()
    while IFS= read -r subject; do
        [[ -z $subject || $subject =~ ^chore:\ release ]] && continue
        text=$(sed -E 's/^[a-z]+(\([^)]*\))?!?: //' <<< "$subject")
        text="${text^}"
        case "$subject" in
            feat:*|feat\(*) added+=("$text") ;;
            fix:*|fix\(*) fixed+=("$text") ;;
            *) changed+=("$text") ;;
        esac
    done < <(git log --no-merges --reverse --format=%s "$range")

    (( ${#added[@]} + ${#fixed[@]} + ${#changed[@]} )) || fail "no commits since ${prev_tag:-the start} - nothing to release"

    notes=""
    for kind in Added Fixed Changed; do
        declare -n items="${kind,,}"
        (( ${#items[@]} )) || continue
        notes+="### $kind"$'\n\n'
        for item in "${items[@]}"; do notes+="- $item"$'\n'; done
        notes+=$'\n'
    done
    while [[ $notes == *$'\n' ]]; do notes=${notes%$'\n'}; done

    # Insert the new section above the newest existing one.
    section="## [$version] - $(date +%F)"$'\n\n'"$notes"$'\n'
    awk -v s="$section" '!done && /^## \[/ { print s; done = 1 } { print } END { if (!done) print "\n" s }' \
        CHANGELOG.md > "$work/CHANGELOG.md"
    cp "$work/CHANGELOG.md" CHANGELOG.md
fi
echo "$version" > VERSION

echo "----- release notes -----"
echo "$notes"
echo "-------------------------"

# --- build, test, package ----------------------------------------------------
out="$work/build"
staging="$work/staging"
zip_path="$work/$NAME.zip"

run dotnet restore
run dotnet build "$PROJECT" -c Release -o "$out"
run dotnet test tests/WeaponPaints.Tests -c Release

for dll in "${RUNTIME_DLLS[@]}"; do rm -f "$out/$dll"; done
# gamedata ships to addons/counterstrikesharp/gamedata/, never inside the plugin folder.
rm -rf "$out/gamedata"

mkdir -p "$staging/addons/counterstrikesharp/plugins"
cp -r "$out" "$staging/addons/counterstrikesharp/plugins/$NAME"
[[ -d gamedata ]] && cp -r gamedata "$staging/addons/counterstrikesharp/gamedata"
(cd "$staging" && zip -qr "$zip_path" addons)

if [[ $dry_run == 1 ]]; then
    echo "dry run: $zip_path"
    exit 0    # cleanup restores VERSION/CHANGELOG.md
fi

# --- publish -----------------------------------------------------------------
if ! git diff --quiet -- VERSION CHANGELOG.md; then
    run git commit -q -m "chore: release $version" -- VERSION CHANGELOG.md
fi
committed=1

printf '%s\n' "$notes" > "$work/release-notes.md"
run git tag -a "$tag" -m "$NAME $version"
run git push --atomic "$REMOTE" "$BRANCH" "refs/tags/$tag"
run gh release create "$tag" "$zip_path" --verify-tag --latest \
    --title "$NAME $version" --notes-file "$work/release-notes.md"
# gh creates a release with assets as a draft, uploads, then publishes; if that
# last step fails the release is left as a draft (v4.1.3). Publish explicitly.
if [[ $(gh release view "$tag" --json isDraft --jq .isDraft) == true ]]; then
    run gh release edit "$tag" --draft=false --latest
fi
[[ $(gh release view "$tag" --json isDraft --jq .isDraft) == false ]] \
    || fail "release $tag is still a draft"
rm -rf "$work"
echo "released $tag"
