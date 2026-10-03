#!/usr/bin/env python3
"""Builds the GitHub wiki from docs/user-manual.md: one page per `##` section, plus Home, _Sidebar and _Footer.

Usage: scripts/build-wiki.py <wiki-checkout-dir>
The manual stays the single source; everything in the wiki checkout except .git is replaced.
"""

import re
import shutil
import sys
from pathlib import Path

REPO = "https://github.com/ZaoralJ/MachineDataBrowser"
ROOT = Path(__file__).resolve().parent.parent
MANUAL = ROOT / "docs" / "user-manual.md"
IMAGES = ROOT / "docs" / "images"


def anchor(heading: str) -> str:
    """GitHub's heading anchor: lowercase, punctuation dropped, spaces to dashes."""
    text = re.sub(r"[^\w\- ]", "", heading.strip().lower())
    return text.replace(" ", "-")


def page_name(heading: str) -> str:
    text = heading.replace("&", "and")
    text = re.sub(r"[^\w\- ]", "", text)
    return re.sub(r"\s+", "-", text.strip())


def split(manual: str):
    """Returns the intro and [(title, body)] for every `##` section (`###` stay inside their section)."""
    parts = re.split(r"^## (.+)$", manual, flags=re.MULTILINE)
    intro = re.sub(r"^# .+\n+", "", parts[0]).strip()
    return intro, [(parts[i].strip(), parts[i + 1].strip()) for i in range(1, len(parts), 2)]


def main() -> None:
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    out = Path(sys.argv[1])
    intro, sections = split(MANUAL.read_text(encoding="utf-8"))

    # In-manual anchors point to the page (## heading) or a heading on a page (###).
    targets = {}
    for title, body in sections:
        page = page_name(title)
        targets[anchor(title)] = page
        for sub in re.findall(r"^### (.+)$", body, flags=re.MULTILINE):
            targets[anchor(sub)] = f"{page}#{anchor(sub)}"

    def rewrite(text: str) -> str:
        text = re.sub(r"\]\(#([\w-]+)\)", lambda m: f"]({targets.get(m.group(1), '#' + m.group(1))})", text)
        # Other repository docs open on GitHub; images are copied into the wiki.
        return re.sub(r"\]\((?!https?:|images/|#)([\w./-]+\.md)(#[\w-]+)?\)",
                      lambda m: f"]({REPO}/blob/main/docs/{m.group(1)}{m.group(2) or ''})", text)

    for item in out.iterdir():
        if item.name != ".git":
            shutil.rmtree(item) if item.is_dir() else item.unlink()
    shutil.copytree(IMAGES, out / "images")

    contents = "\n".join(f"- [{title}]({page_name(title)})" for title, _ in sections)
    (out / "Home.md").write_text(f"# Machine Data Browser\n\n{rewrite(intro)}\n\n## Contents\n\n{contents}\n", encoding="utf-8")
    for title, body in sections:
        (out / f"{page_name(title)}.md").write_text(f"# {title}\n\n{rewrite(body)}\n", encoding="utf-8")

    (out / "_Sidebar.md").write_text(f"**[User manual](Home)**\n\n{contents}\n", encoding="utf-8")
    (out / "_Footer.md").write_text(
        f"Generated from [docs/user-manual.md]({REPO}/blob/main/docs/user-manual.md); "
        "edit it there, changes made here are overwritten.\n",
        encoding="utf-8")
    print(f"{len(sections) + 1} pages written to {out}")


if __name__ == "__main__":
    main()
