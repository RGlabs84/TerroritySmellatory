#!/usr/bin/env python3
"""Rule-2 tripwire for the TerroritySmellatory e2e rig.

The rig's game clients must never write to Rohan's real Valheim data. They run with a fake HOME and
XDG dirs, a redirected save path and Steam Cloud switched off in-process, and this script is the check
that none of that leaked.

  safety.py first    <dir>        once ever: pristine copy of the prefs file + manifest (never overwritten)
  safety.py baseline <dir>        before a run's first client launch: manifest of every watched file
  safety.py check    <dir>        after every client run: compare with the baseline; exit 1 on ANY change

Watched (read-only, never written by this script):
  ~/.config/unity3d/IronGate/Valheim/**                    Rohan's real saves, prefs and Player.log
  ~/.local/share/Steam/userdata/<id>/892970/remote/**      Valheim's Steam Cloud files
  the client and dedicated-server install dirs             must not gain or change a single file
Informational only (Steam's own bookkeeping, rewritten by Steam itself):
  ~/.local/share/Steam/userdata/<id>/892970/remotecache.vdf
"""
import hashlib
import json
import os
import shutil
import sys
import time

HOME = os.path.expanduser("~")
VALHEIM_PREFS_DIR = os.path.join(HOME, ".config/unity3d/IronGate/Valheim")
STEAM = os.path.join(HOME, ".local/share/Steam")
GAME_DIRS = [
    os.path.join(STEAM, "steamapps/common/Valheim"),
    os.path.join(STEAM, "steamapps/common/Valheim dedicated server"),
]


def steam_cloud_dirs():
    out = []
    ud = os.path.join(STEAM, "userdata")
    if os.path.isdir(ud):
        for uid in sorted(os.listdir(ud)):
            d = os.path.join(ud, uid, "892970", "remote")
            if os.path.isdir(d):
                out.append(d)
    return out


def remotecache_files():
    out = []
    ud = os.path.join(STEAM, "userdata")
    if os.path.isdir(ud):
        for uid in sorted(os.listdir(ud)):
            f = os.path.join(ud, uid, "892970", "remotecache.vdf")
            if os.path.isfile(f):
                out.append(f)
    return out


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def manifest(root, hash_files=True):
    """{relpath: [size, mtime_ns, sha256|None]} for every regular file; symlinks recorded, not followed."""
    m = {}
    if not os.path.isdir(root):
        return m
    for dirpath, dirnames, filenames in os.walk(root, followlinks=False):
        dirnames.sort()
        for name in sorted(filenames):
            p = os.path.join(dirpath, name)
            rel = os.path.relpath(p, root)
            try:
                st = os.lstat(p)
            except FileNotFoundError:
                continue
            if os.path.islink(p):
                m[rel] = ["link", os.readlink(p), None]
                continue
            m[rel] = [st.st_size, st.st_mtime_ns, sha256(p) if hash_files else None]
    return m


def game_dir_listing(root):
    """Install dirs are big; record size+mtime (no hashes) - any write changes mtime."""
    return manifest(root, hash_files=False)


def take(dirpath):
    os.makedirs(dirpath, exist_ok=True)
    data = {
        "taken_at": time.strftime("%Y-%m-%d %H:%M:%S"),
        "taken_epoch": time.time(),
        "valheim_prefs_dir": {"root": VALHEIM_PREFS_DIR, "files": manifest(VALHEIM_PREFS_DIR)},
        "steam_cloud": {d: manifest(d) for d in steam_cloud_dirs()},
        "remotecache": {f: [os.path.getsize(f), os.stat(f).st_mtime_ns, sha256(f)] for f in remotecache_files()},
        "game_dirs": {d: game_dir_listing(d) for d in GAME_DIRS},
    }
    with open(os.path.join(dirpath, "manifest.json"), "w") as f:
        json.dump(data, f)
    return data


def diff(old, new):
    changes = []
    for rel in sorted(set(old) | set(new)):
        a, b = old.get(rel), new.get(rel)
        if a == b:
            continue
        if a is None:
            changes.append(f"ADDED    {rel}")
        elif b is None:
            changes.append(f"REMOVED  {rel}")
        else:
            changes.append(f"CHANGED  {rel}  {a[:2]} -> {b[:2]}")
    return changes


def cmd_first(dirpath):
    marker = os.path.join(dirpath, "first", "manifest.json")
    if os.path.exists(marker):
        print(f"[safety] first-ever baseline already exists at {marker} (kept untouched)")
        return 0
    first = os.path.join(dirpath, "first")
    os.makedirs(first, exist_ok=True)
    prefs = os.path.join(VALHEIM_PREFS_DIR, "prefs")
    if os.path.isfile(prefs):
        shutil.copy2(prefs, os.path.join(first, "prefs.copy"))
    take(first)
    print(f"[safety] first-ever baseline + prefs copy written to {first}")
    return 0


def cmd_baseline(dirpath):
    data = take(dirpath)
    n = len(data["valheim_prefs_dir"]["files"])
    prefs = os.path.join(VALHEIM_PREFS_DIR, "prefs")
    if os.path.isfile(prefs):
        shutil.copy2(prefs, os.path.join(dirpath, "prefs.copy"))
    print(f"[safety] baseline: {n} files under {VALHEIM_PREFS_DIR} hashed; "
          f"{sum(len(v) for v in data['steam_cloud'].values())} Steam Cloud files; "
          f"{sum(len(v) for v in data['game_dirs'].values())} install files listed -> {dirpath}/manifest.json")
    return 0


def cmd_check(dirpath):
    with open(os.path.join(dirpath, "manifest.json")) as f:
        old = json.load(f)
    bad = []
    new_prefs = manifest(VALHEIM_PREFS_DIR)
    for c in diff(old["valheim_prefs_dir"]["files"], new_prefs):
        bad.append(f"{VALHEIM_PREFS_DIR}: {c}")
    for d, files in old["steam_cloud"].items():
        for c in diff(files, manifest(d)):
            bad.append(f"{d}: {c}")
    for d in steam_cloud_dirs():
        if d not in old["steam_cloud"]:
            bad.append(f"NEW Steam Cloud dir {d}")
    for d, files in old["game_dirs"].items():
        for c in diff(files, game_dir_listing(d)):
            bad.append(f"{d}: {c}")
    info = []
    for f, meta in old["remotecache"].items():
        if os.path.isfile(f):
            now = [os.path.getsize(f), os.stat(f).st_mtime_ns, sha256(f)]
            if now != meta:
                info.append(f"{f} changed (Steam's own cloud bookkeeping; informational)")
    report = {"checked_at": time.strftime("%Y-%m-%d %H:%M:%S"), "violations": bad, "info": info}
    with open(os.path.join(dirpath, "check.json"), "w") as f:
        json.dump(report, f, indent=1)
    for line in info:
        print(f"[safety] note: {line}")
    if bad:
        print("[safety] !!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!")
        print("[safety] RULE-2 VIOLATION: protected files changed during this run:")
        for line in bad[:200]:
            print(f"[safety]   {line}")
        print("[safety] !!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!")
        return 1
    print(f"[safety] OK: {len(new_prefs)} files under {VALHEIM_PREFS_DIR}, the Steam Cloud dir(s) and both "
          f"install dirs are unchanged since the baseline ({old['taken_at']}).")
    return 0


def main():
    if len(sys.argv) != 3 or sys.argv[1] not in ("first", "baseline", "check"):
        print(__doc__)
        return 2
    return {"first": cmd_first, "baseline": cmd_baseline, "check": cmd_check}[sys.argv[1]](sys.argv[2])


if __name__ == "__main__":
    sys.exit(main())
