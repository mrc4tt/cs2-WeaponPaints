#!/usr/bin/env python3
"""Check gamedata/weaponpaints.json against the CS2 signature tracker and/or local binaries.

  tools/check-gamedata.py                      compare with sig.miksen.me's latest published build
  tools/check-gamedata.py --linux libserver.so --windows server.dll
                                               also pattern-scan local binaries (each sig must hit once)
  tools/check-gamedata.py --offline --linux .. only the local scan

Exit codes: 0 ok, 1 mismatch / bad signature, 2 tracker unreachable (only with --require-tracker).
release.sh runs this; an unreachable tracker is a warning there, a mismatch stops the release.

A unique pattern hit is NOT an identification - see docs/loadout-gamedata-notes.md.
"""
import argparse
import hashlib
import json
import re
import sys
import urllib.request
from pathlib import Path

TRACKER = "https://sig.miksen.me"
TRACKER_FILE_ID = "weaponpaints/gamedata/weaponpaints.json"
REQUIRED = ("CAttributeList::SetOrAddAttributeValueByName", "UpdateItemView")
GAMEDATA = Path(__file__).resolve().parent.parent / "gamedata" / "weaponpaints.json"


def fetch(path):
    req = urllib.request.Request(f"{TRACKER}/{path}", headers={"User-Agent": "weaponpaints-gamedata-check", "Accept": "application/json"})
    with urllib.request.urlopen(req, timeout=20) as res:
        return res.read()


def check_tracker(local):
    """Returns (ok, message). Raises OSError/ValueError when the tracker can't be read."""
    latest = json.loads(fetch("latest.json"))["latest"]
    build = latest["gameVersion"]
    index = json.loads(fetch("gamedata/index.json"))
    version = next((v for v in index["versions"] if v["gameVersion"] == build), None)
    if version is None:
        raise ValueError(f"tracker index has no entry for its latest build {build}")
    entry = next((f for f in version["files"] if f["id"] == TRACKER_FILE_ID), None)
    if entry is None:
        return False, f"tracker build {build} does not publish {TRACKER_FILE_ID}"

    payload = fetch(f"gamedata/{entry['content']['url']}")
    if hashlib.sha256(payload).hexdigest() != entry["content"]["sha256"]:
        raise ValueError("tracker payload sha256 does not match its index")

    problems = []
    summary = (entry.get("metadata") or {}).get("summary") or {}
    if summary.get("covered") != summary.get("total"):
        problems.append(f"tracker covers only {summary.get('covered')}/{summary.get('total')} keys on {build}")

    published = json.loads(payload)
    if published != local:
        changed = sorted(k for k in set(published) | set(local) if published.get(k) != local.get(k))
        problems.append(f"repo gamedata differs from tracker build {build}: {', '.join(changed)}")
    else:
        # A deployment the tracker never pushed would leave the repo stale without a diff above.
        deployed = ((latest.get("deployment") or {}).get("targets") or {}).get("weaponpaints") or {}
        if deployed and not deployed.get("pushed", False):
            problems.append(f"tracker build {build} was not pushed to the repo")

    if problems:
        return False, "; ".join(problems)
    return True, f"repo gamedata matches tracker build {build} ({summary.get('covered')}/{summary.get('total')} keys covered)"


def pattern(sig):
    return re.compile(b"".join(b"." if t in ("?", "??") else re.escape(bytes([int(t, 16)])) for t in sig.split()), re.S)


def scan(local, binary, platform):
    data = Path(binary).read_bytes()
    ok = True
    for key, value in local.items():
        sig = (value.get("signatures") or {}).get(platform)
        if not sig:
            continue
        hits = [m.start() for m in pattern(sig).finditer(data)]
        tag = "required" if key in REQUIRED else "unused"
        if len(hits) == 1:
            print(f"  ok   {platform:7} {key} @ file+{hits[0]:#x} ({tag})")
        else:
            ok = ok and key not in REQUIRED
            print(f"  {'FAIL' if key in REQUIRED else 'warn'} {platform:7} {key}: {len(hits)} hits ({tag})")
    return ok


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--linux", help="path to libserver.so to pattern-scan")
    ap.add_argument("--windows", help="path to server.dll to pattern-scan")
    ap.add_argument("--offline", action="store_true", help="skip the tracker comparison")
    ap.add_argument("--require-tracker", action="store_true", help="exit 2 when the tracker is unreachable")
    args = ap.parse_args()

    local = json.loads(GAMEDATA.read_text())
    missing = [k for k in REQUIRED if k not in local and k.replace("::", "_") not in local]
    if missing:
        print(f"gamedata: missing required key(s): {', '.join(missing)}")
        return 1

    status = 0
    if not args.offline:
        try:
            ok, message = check_tracker(local)
            print(f"gamedata: {message}")
            if not ok:
                status = 1
        except (OSError, ValueError, KeyError) as ex:
            print(f"gamedata: tracker {TRACKER} unreachable or malformed: {ex}")
            if args.require_tracker:
                return 2

    for platform, binary in (("linux", args.linux), ("windows", args.windows)):
        if binary:
            print(f"gamedata: scanning {binary}")
            if not scan(local, binary, platform):
                status = 1
    return status


if __name__ == "__main__":
    sys.exit(main())
