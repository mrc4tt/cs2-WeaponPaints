#!/usr/bin/env python3
"""Build, test, package and publish a WeaponPaints release from this machine.

There is no CI: releases are cut locally and pushed to origin
(github.com/mrc4tt/cs2-WeaponPaints) with the gh CLI.

VERSION is the single source of truth. A release is tagged v<VERSION> and needs a
non-empty "## [VERSION]" section in CHANGELOG.md, which becomes the release notes.

  python3 release.py            build, test, package, tag, push main + tag, gh release
  python3 release.py --dry-run  build, test and package only; prints the zip path

Requires: dotnet 10 SDK and gh (logged in). All packages come from nuget.org.
"""
import os
import re
import shutil
import subprocess
import sys
import tempfile

PROJECT = "WeaponPaints.csproj"
NAME = "WeaponPaints"
REMOTE = "origin"
BRANCH = "main"
# Provided by the CSSharp runtime / MenuManagerCore; must never ship.
RUNTIME_DLLS = [
    "CounterStrikeSharp.API.dll",
    "McMaster.NETCore.Plugins.dll",
    "Microsoft.DotNet.PlatformAbstractions.dll",
    "Microsoft.Extensions.DependencyModel.dll",
    "CS2MenuManager.dll",
]


def run(*cmd, **kw):
    print("+ " + " ".join(cmd))
    subprocess.run(cmd, check=True, **kw)


def out(*cmd):
    return subprocess.run(cmd, check=True, stdout=subprocess.PIPE,
                          universal_newlines=True).stdout.strip()


def fail(msg):
    sys.exit("release: " + msg)


def release_notes(version):
    text = open("CHANGELOG.md").read()
    m = re.search(r"^## \[" + re.escape(version) + r"\][^\n]*\n", text, re.M)
    if not m:
        return None
    nxt = re.search(r"^## \[", text[m.end():], re.M)
    body = text[m.end():m.end() + nxt.start()] if nxt else text[m.end():]
    return body.strip() or None


def check_repo(tag):
    if out("git", "rev-parse", "--abbrev-ref", "HEAD") != BRANCH:
        fail("not on %s" % BRANCH)
    if out("git", "status", "--porcelain", "--untracked-files=no"):
        fail("working tree has uncommitted changes to tracked files - commit or stash them")
    run("git", "fetch", REMOTE, "--tags", "--quiet")
    if subprocess.run(["git", "merge-base", "--is-ancestor", "%s/%s" % (REMOTE, BRANCH), "HEAD"]).returncode:
        fail("%s is behind %s/%s - pull first" % (BRANCH, REMOTE, BRANCH))
    if subprocess.run(["git", "rev-parse", "-q", "--verify", "refs/tags/" + tag],
                      stdout=subprocess.DEVNULL).returncode == 0:
        fail("tag %s already exists - bump VERSION and add a CHANGELOG section" % tag)


def build(work):
    output = os.path.join(work, "build")
    run("dotnet", "restore")
    run("dotnet", "build", PROJECT, "-c", "Release", "-o", output)
    run("dotnet", "test", "tests/WeaponPaints.Tests", "-c", "Release")

    for dll in RUNTIME_DLLS:
        path = os.path.join(output, dll)
        if os.path.exists(path):
            os.remove(path)
    # gamedata ships to addons/counterstrikesharp/gamedata/, never inside the plugin folder.
    shutil.rmtree(os.path.join(output, "gamedata"), ignore_errors=True)

    staging = os.path.join(work, "staging")
    shutil.copytree(output, os.path.join(staging, "addons", "counterstrikesharp", "plugins", NAME))
    if os.path.isdir("gamedata"):
        shutil.copytree("gamedata", os.path.join(staging, "addons", "counterstrikesharp", "gamedata"))
    return shutil.make_archive(os.path.join(work, NAME), "zip", staging)


def main():
    dry_run = "--dry-run" in sys.argv[1:]
    os.chdir(os.path.dirname(os.path.abspath(__file__)))

    version = open("VERSION").read().strip()
    tag = "v" + version
    notes = release_notes(version)
    if not notes:
        fail("CHANGELOG.md has no (or an empty) '## [%s]' section" % version)
    if not dry_run:
        check_repo(tag)

    work = tempfile.mkdtemp(prefix="wp-release-")
    zip_path = build(work)
    if dry_run:
        print("dry run: %s" % zip_path)
        return

    notes_path = os.path.join(work, "release-notes.md")
    with open(notes_path, "w") as f:
        f.write(notes + "\n")

    run("git", "tag", "-a", tag, "-m", "%s %s" % (NAME, version))
    run("git", "push", REMOTE, BRANCH, "refs/tags/" + tag)
    run("gh", "release", "create", tag, zip_path, "--verify-tag", "--latest",
        "--title", "%s %s" % (NAME, version), "--notes-file", notes_path)
    shutil.rmtree(work, ignore_errors=True)
    print("released %s" % tag)


if __name__ == "__main__":
    try:
        main()
    except subprocess.CalledProcessError as e:
        fail("command failed (exit %d): %s" % (e.returncode, " ".join(e.cmd)))
