"""Package a skill folder into a distributable .skill file (a zip of the folder).

Usage:
    python -m scripts.package_skill <path/to/skill-folder> [--output-dir DIR]

The archive is named after the skill's `name` frontmatter (falling back to the folder name) and
contains the folder itself, so it unpacks to <skill-name>/SKILL.md, etc.
"""
from __future__ import annotations

import argparse
import re
import zipfile
from pathlib import Path


def _skill_name(folder: Path) -> str:
    skill_md = folder / "SKILL.md"
    if skill_md.exists():
        text = skill_md.read_text(encoding="utf-8", errors="ignore")
        fm = re.search(r"^---\s*\n(.*?)\n---", text, re.DOTALL)
        if fm:
            nm = re.search(r"^name:\s*(.+)$", fm.group(1), re.MULTILINE)
            if nm:
                return nm.group(1).strip().strip("\"'")
    return folder.name


def package(folder: Path, output_dir: Path | None = None) -> Path:
    folder = folder.resolve()
    if not (folder / "SKILL.md").exists():
        raise SystemExit(f"No SKILL.md in {folder} — not a skill folder.")

    name = _skill_name(folder)
    out_dir = (output_dir or folder.parent).resolve()
    out_dir.mkdir(parents=True, exist_ok=True)
    out = out_dir / f"{name}.skill"

    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
        for p in sorted(folder.rglob("*")):
            if p.is_file() and "__pycache__" not in p.parts:
                z.write(p, p.relative_to(folder.parent))
    return out


def main():
    ap = argparse.ArgumentParser(description="Package a skill folder into a .skill file.")
    ap.add_argument("folder", type=Path)
    ap.add_argument("--output-dir", type=Path, default=None)
    args = ap.parse_args()
    out = package(args.folder, args.output_dir)
    print(f"Packaged: {out}")


if __name__ == "__main__":
    main()
