#!/usr/bin/env python3
"""Stamp the version through HexiumDist and pack the release zip.

Run by the csproj after every build, so the package on disk is never a build behind
the DLL. That staleness was a real hazard: HexiumDist/plugins/TerroritySmellatory.dll and the
versioned zip were both copied by hand, and "is the fold current?" had to be answered
by hashing rather than by knowing.

Every zip entry name is written explicitly with forward slashes. This is not
decoration. Compress-Archive - and ZipFile.CreateFromDirectory on some runtimes -
writes backslash-separated entry names, which mod loaders treat as one flat filename:
plugins/<Mod>.dll arrives as a file literally called "plugins\\<Mod>.dll" sitting in
the archive root, and BepInEx never sees a plugins folder at all. The mod then looks
installed and does nothing.

Timestamps are fixed rather than taken from the filesystem so that rebuilding
unchanged sources produces a byte-identical zip. A fold that churns its own hash on
every build cannot be used to answer "is what I deployed what I built?".
"""

import argparse
import subprocess
import hashlib
import json
import pathlib
import shutil
import sys
import zipfile

# Deterministic zip timestamp. 1980-01-01 is the earliest the zip format can encode.
FIXED_DATE = (1980, 1, 1, 0, 0, 0)

# Order matters only for reproducibility, not for correctness.
PACKAGE_FILES = ["manifest.json", "README.md", "CHANGELOG.md", "LICENSE.md", "icon.png"]


def stamp_manifest(manifest_path: pathlib.Path, version: str) -> None:
    """Write version_number into the manifest, leaving every other key untouched.

    Parsed and re-serialised rather than regex-replaced so a malformed manifest fails
    here, loudly, instead of shipping.
    """
    data = json.loads(manifest_path.read_text(encoding="utf-8"))
    if data.get("version_number") == version:
        return
    data["version_number"] = version
    manifest_path.write_text(
        json.dumps(data, indent=2, ensure_ascii=False) + "\n", encoding="utf-8"
    )
    print(f"fold: manifest version_number -> {version}")


def stage_licence(dist: pathlib.Path) -> None:
    """Copy the repo-root LICENSE.md into the dist folder.

    The zip used to reach past HexiumDist to the repo root for this one file, which left
    the staging folder missing a file the artifact contained. Anyone deploying by copying
    the folder - which is how these mods actually get onto a test rig - silently shipped
    without a licence, and the folder could not be checked against the zip. The staging
    folder is supposed to BE the package.
    """
    src = dist.parent / "LICENSE.md"
    if not src.exists():
        return
    dest = dist / "LICENSE.md"
    if not dest.exists() or dest.read_bytes() != src.read_bytes():
        shutil.copy2(src, dest)
        print("fold: staged LICENSE.md")


def stage_dll(built_dll: pathlib.Path, dist: pathlib.Path) -> pathlib.Path:
    staged = dist / "plugins" / built_dll.name
    staged.parent.mkdir(parents=True, exist_ok=True)
    # Compare before copying so an unchanged build leaves the mtime alone.
    if not staged.exists() or staged.read_bytes() != built_dll.read_bytes():
        shutil.copy2(built_dll, staged)
        print(f"fold: staged {staged.relative_to(dist.parent)}")
    return staged


def stage_pdb(built_dll: pathlib.Path, dist: pathlib.Path) -> pathlib.Path:
    """0.5.2: the PDB that matches the DLL ships beside it (owner rule 2026-11-20). libs-Tools/pdb_match.py checks the pair
    (same build id, no home-folder path) before it is staged; a mismatch stops the fold."""
    pdb = built_dll.with_suffix(".pdb")
    if not pdb.exists():
        raise SystemExit(f"fold: no PDB beside {built_dll}")
    check = pathlib.Path.home() / "WubarrkCODING" / "libs-Tools" / "pdb_match.py"
    if check.exists():
        r = subprocess.run([sys.executable, str(check), str(built_dll)], capture_output=True, text=True)
        print("fold: " + (r.stdout.strip().splitlines() or ["pdb_match: no output"])[-1])
        if r.returncode != 0:
            raise SystemExit(f"fold: pdb_match failed for {built_dll}:\n{r.stdout}{r.stderr}")
    else:
        print(f"fold: WARNING {check} not found: PDB staged unchecked")
    staged = dist / "plugins" / pdb.name
    if not staged.exists() or staged.read_bytes() != pdb.read_bytes():
        shutil.copy2(pdb, staged)
        print(f"fold: staged {staged.relative_to(dist.parent)}")
    return staged


def pack(dist: pathlib.Path, version: str, staged_dll: pathlib.Path, staged_pdb: pathlib.Path) -> pathlib.Path:
    out = dist / f"TerroritySmellatory-v{version}.zip"
    entries = []
    for name in PACKAGE_FILES:
        src = dist / name
        if src.exists():
            entries.append((name, src))
    entries.append(("plugins/" + staged_dll.name, staged_dll))
    entries.append(("plugins/" + staged_pdb.name, staged_pdb))

    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
        for arcname, src in entries:
            assert "\\" not in arcname, f"backslash in entry name: {arcname!r}"
            info = zipfile.ZipInfo(arcname, date_time=FIXED_DATE)
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o644 << 16
            z.writestr(info, src.read_bytes())

    digest = hashlib.sha256(staged_dll.read_bytes()).hexdigest()
    print(f"fold: {out.name}  ({len(entries)} entries, dll sha256 {digest[:16]}...)")
    return out


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--version", required=True)
    ap.add_argument("--dist", required=True, type=pathlib.Path)
    ap.add_argument("--dll", required=True, type=pathlib.Path)
    args = ap.parse_args()

    dist = args.dist.resolve()
    dll = args.dll.resolve()

    if not dll.exists():
        print(f"fold: no built DLL at {dll}", file=sys.stderr)
        return 1
    if not dist.is_dir():
        print(f"fold: no dist folder at {dist}", file=sys.stderr)
        return 1

    stamp_manifest(dist / "manifest.json", args.version)
    stage_licence(dist)
    staged = stage_dll(dll, dist)
    staged_pdb = stage_pdb(dll, dist)
    pack(dist, args.version, staged, staged_pdb)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
