#!/usr/bin/env python3
# The page says what the latest release is: its version and codename on the Download button and over the downloads,
# and what's new in it beside them. pages.yml runs this on the copy of the site it uploads, with the latest release as
# GitHub's API gives it, so the page says the release without asking GitHub anything from the visitor's browser.
#
#   python3 site-release.py release.json site/index.html
#
# What the page says between <!-- release:key --> and <!-- /release:key --> is put in place of; what's there in the
# repository is what the page says when this doesn't run (opened from disk). A marker that's gone fails the deploy
# rather than leave the page saying an old release.

import html
import json
import re
import sys


def main(release_path, page_path):
    with open(release_path, encoding="utf-8") as f:
        release = json.load(f)
    version = release["tag_name"].removeprefix("v")
    # The release is titled the way its notes are, Hex 3.1.1 Amphetamine; one titled only Hex v3.1.1 has no codename
    name = (release.get("name") or "").strip()
    prefix = f"Hex {version}"
    codename = name[len(prefix):].strip() if name.startswith(prefix) else ""
    title = f"{prefix} {codename}".strip()

    with open(page_path, encoding="utf-8", newline="") as f:
        page = f.read()

    page = put(page, "button", html.escape(f"Download {title}"))
    page = put(page, "title", html.escape(title))

    notes = section((release.get("body") or "").replace("\r\n", "\n"), version)
    if notes:
        page = put(page, "notes", render(notes, version, codename, release.get("html_url") or ""))
    else:
        print(f"::warning::The notes of {title} have nothing for {version}; the page keeps the notes it has.")

    with open(page_path, "w", encoding="utf-8", newline="") as f:
        f.write(page)
    print(f"The page says {title}.")


def put(page, key, text):
    marker = re.compile(rf"(<!-- release:{key} -->).*?(<!-- /release:{key} -->)", re.S)
    page, count = marker.subn(lambda m: m[1] + text + m[2], page)
    if count == 0:
        sys.exit(f"The page has no <!-- release:{key} --> to say the release in.")
    return page


def section(body, version):
    """The notes' own part for this version: RELEASE_NOTES.md keeps every version, newest first, each under a # title."""
    lines = body.split("\n")
    title = re.compile(rf"^#\s+Hex\s+{re.escape(version)}(\s|$)")
    start = next((i for i, line in enumerate(lines) if title.match(line)), None)
    if start is None:
        return ""
    end = next((i for i in range(start + 1, len(lines)) if re.match(r"^#\s", lines[i])), len(lines))
    # GitHub's own footer on a generated release, not the notes
    part = [line for line in lines[start + 1:end] if not line.startswith("**Full Changelog**")]
    return "\n".join(part).strip()


def render(notes, version, codename, url):
    """The heading, the notes' first words open, and their sections folded away when there are any (an x.y.0's are long)."""
    heading = f"What's new in {version}" + (f" {codename}" if codename else "")
    split = re.search(r"^##\s", notes, re.M)
    intro, rest = (notes[:split.start()], notes[split.start():]) if split else (notes, "")
    out = [f'<h3>{html.escape(heading, quote=False)}</h3>', markdown(intro)]
    if rest.strip():
        out.append(f'<details><summary>Everything in {html.escape(version)}</summary>{markdown(rest)}</details>')
    if url:
        out.append(f'<p class="note"><a href="{html.escape(url)}">The release on GitHub</a></p>')
    return "\n".join(out)


def markdown(text):
    """The Markdown the notes are written in: ## headings, paragraphs, - lists whose items go on over indented lines,
    `code`, **bold**, [links](url) and bare links. Headings go down two, under the page's Get it and What's new."""
    out, para, items = [], [], []

    def flush():
        if para:
            out.append(f"<p>{inline(' '.join(para))}</p>")
            para.clear()
        if items:
            out.append("<ul>" + "".join(f"<li>{inline(' '.join(item))}</li>" for item in items) + "</ul>")
            items.clear()

    for line in text.split("\n"):
        stripped = line.strip()
        heading = re.match(r"^(#{1,4})\s+(.*)", line)
        if not stripped:
            flush()
        elif heading:
            flush()
            level = min(len(heading[1]) + 2, 6)
            out.append(f"<h{level}>{inline(heading[2])}</h{level}>")
        elif re.match(r"^[-*]\s", line):
            if para:
                flush()
            items.append([stripped[2:]])
        elif items and line[:1].isspace():
            items[-1].append(stripped)
        else:
            if items:
                flush()
            para.append(stripped)
    flush()
    return "\n".join(out)


def inline(text):
    # Code first and kept as it is, the rest escaped, then bold, links and bare links
    parts = re.split(r"(`[^`]+`)", text)
    out = []
    for part in parts:
        if len(part) > 1 and part.startswith("`") and part.endswith("`"):
            out.append(f"<code>{html.escape(part[1:-1])}</code>")
            continue
        part = html.escape(part, quote=False)
        part = re.sub(r"\*\*(.+?)\*\*", r"<strong>\1</strong>", part)
        part = re.sub(r"\[([^\]]+)\]\((https?://[^)\s]+)\)", r'<a href="\2">\1</a>', part)
        part = re.sub(r'(?<!href=")(?<![">])(https?://[^\s<]+)', r'<a href="\1">\1</a>', part)
        out.append(part)
    return "".join(out)


if __name__ == "__main__":
    if len(sys.argv) != 3:
        sys.exit("site-release.py release.json index.html")
    main(sys.argv[1], sys.argv[2])
