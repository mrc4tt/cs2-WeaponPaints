#!/usr/bin/env python3
"""Decide whether a push to main releases, and with which version.

VERSION is the single source of truth. A release is tagged v<VERSION>.

  * v<VERSION> does not exist yet  -> release VERSION. CHANGELOG.md must have a
    "## [VERSION]" section; its body becomes the release notes.
  * v<VERSION> exists and the head commit is an automated gamedata update
    ("gamedata: <build> ...") -> bump VERSION (minor when an optional [Unreleased]
    section holds "### Added", patch otherwise), write a new "## [x.y.z] - date"
    section with a gamedata line (plus whatever [Unreleased] held), and release
    that. The caller commits VERSION and CHANGELOG.md.
  * otherwise -> no release.

Usage: release_plan.py <head commit subject>
Writes release=, version=, bumped= to $GITHUB_OUTPUT (stdout when unset) and the
notes to release-notes.md.
"""
import datetime
import os
import re
import subprocess
import sys

CHANGELOG = "CHANGELOG.md"
SECTION_ORDER = ["Added", "Changed", "Deprecated", "Removed", "Fixed", "Security"]


def tag_exists(tag):
    return subprocess.run(["git", "rev-parse", "-q", "--verify", "refs/tags/" + tag],
                          stdout=subprocess.DEVNULL).returncode == 0


def section_bounds(text, version):
    """(start of body, end of body) of '## [version]', or None."""
    m = re.search(r"^## \[" + re.escape(version) + r"\][^\n]*\n", text, re.M)
    if not m:
        return None
    nxt = re.search(r"^## \[", text[m.end():], re.M)
    return m.end(), (m.end() + nxt.start()) if nxt else len(text)


def split_subsections(body):
    """'### Name' -> list of lines (blank-trimmed), keeping unknown text under ''."""
    parts, name = {"": []}, ""
    for line in body.splitlines():
        h = re.match(r"^### (\w+)\s*$", line)
        if h:
            name = h.group(1)
            parts.setdefault(name, [])
        else:
            parts[name].append(line)
    return {k: "\n".join(v).strip("\n") for k, v in parts.items() if "\n".join(v).strip()}


def join_subsections(parts):
    names = [n for n in SECTION_ORDER if n in parts] + \
            [n for n in parts if n and n not in SECTION_ORDER]
    out = [parts[""]] if "" in parts else []
    out += ["### %s\n\n%s" % (n, parts[n]) for n in names]
    return "\n\n".join(out)


def bump(version, minor):
    m = re.match(r"^(\d+)\.(\d+)\.(\d+)$", version)
    if not m:
        sys.exit("VERSION '%s' is not x.y.z, cannot auto-bump" % version)
    major, mi, patch = map(int, m.groups())
    return "%d.%d.0" % (major, mi + 1) if minor else "%d.%d.%d" % (major, mi, patch + 1)


def main():
    subject = sys.argv[1] if len(sys.argv) > 1 else ""
    version = open("VERSION").read().strip()
    text = open(CHANGELOG).read()
    bumped = False

    if tag_exists("v" + version):
        g = re.match(r"^gamedata:\s*(\S+)", subject)
        if not g:
            return emit(False, version, False)

        # An optional [Unreleased] section is folded into the release; without one
        # the new section goes above the newest release.
        unrel = section_bounds(text, "Unreleased")
        if unrel:
            unrel = (text.rindex("## [Unreleased]", 0, unrel[0]), unrel[1])
        else:
            first = re.search(r"^## \[", text, re.M)
            at = first.start() if first else len(text)
            unrel = (at, at)
        parts = split_subsections(text[text.index("\n", unrel[0]) + 1:unrel[1]]) if unrel[0] != unrel[1] else {}
        version = bump(version, "Added" in parts)
        line = "- `gamedata/weaponpaints.json` updated for CS2 build %s (automated)." % g.group(1)
        parts["Changed"] = line + ("\n" + parts["Changed"] if "Changed" in parts else "")

        new = "## [%s] - %s\n\n%s\n\n" % (version, datetime.date.today().isoformat(),
                                          join_subsections(parts))
        text = text[:unrel[0]] + new + text[unrel[1]:]
        open(CHANGELOG, "w").write(text)
        open("VERSION", "w").write(version + "\n")
        bumped = True

    sec = section_bounds(text, version)
    if not sec or not text[sec[0]:sec[1]].strip():
        sys.exit("CHANGELOG.md has no (or an empty) '## [%s]' section - add one before bumping VERSION"
                 % version)
    open("release-notes.md", "w").write(text[sec[0]:sec[1]].strip() + "\n")
    emit(True, version, bumped)


def emit(release, version, bumped):
    lines = "release=%s\nversion=%s\nbumped=%s\n" % (str(release).lower(), version, str(bumped).lower())
    path = os.environ.get("GITHUB_OUTPUT")
    if path:
        with open(path, "a") as f:
            f.write(lines)
    sys.stdout.write(lines)


if __name__ == "__main__":
    main()
