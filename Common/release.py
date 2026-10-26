#!/usr/bin/env python3
"""Version helper for Bolek98's RimWorld mods.

  release.py check                    – every mod: About.xml <modVersion>, Version constants in the
                                        source, versions written as text in the source, top of
                                        CHANGELOG.txt and the README table must all agree.
  release.py bump <Mod> <x.y.z> "line" ["line" ...]
                                      – sets the version everywhere (About.xml, source, README) and
                                        adds an entry to the "Not on the Workshop yet" block on top
                                        of CHANGELOG.txt.
  release.py readme                   – rewrites the mod table in README.md from About.xml.
  release.py notes <Mod>              – prints the changelog entries not on the Workshop yet
                                        (material for the Workshop change notes).
  release.py release <Mod> "24 Oct 2026"
                                      – after publishing: the waiting entries become a
                                        "=== Workshop release x.y.z (date) ===" block.

  release.py fold <Mod> <x.y.z> <notes.txt>
                                      – before a Workshop release: sets the Workshop version, puts the
                                        player notes from notes.txt on top of the waiting block and
                                        keeps the test versions below as development history.
  release.py footer <Mod>             – writes the bottom of the Workshop description (About.xml):
                                        ideas & suggestions + a short change log (EN, then CZ) from
                                        Common/footer/<ModName>.txt. Run it for every release.

<Mod> is the folder name (e.g. SmoothFPS, Steward2/Steward).
"""
import pathlib, re, sys

root = pathlib.Path(__file__).resolve().parent.parent
VER = r"\d+\.\d+\.\d+"
PENDING = "=== Not on the Workshop yet (test versions) ===\n"


def mods():
    for about in sorted(root.glob("**/About/About.xml")):
        d = about.parent.parent
        if "Source" in d.parts or d.name.startswith("."):
            continue
        yield d


def about_version(d):
    m = re.search(r"<modVersion>\s*(" + VER + r")\s*</modVersion>", (d / "About" / "About.xml").read_text(encoding="utf-8"))
    return m.group(1) if m else None


def source_versions(d):
    """(file, line, version, kind) for version constants and hard-written version text."""
    out = []
    for f in sorted((d / "Source").rglob("*.cs")) if (d / "Source").exists() else []:
        if f.name in ("MisakUI.cs", "MisakReport.cs", "MisakNews.cs"):
            continue
        for i, line in enumerate(f.read_text(encoding="utf-8", errors="replace").splitlines(), 1):
            s = line.strip()
            if s.startswith("//"):
                continue
            m = re.search(r'\b(?:Version|Number)\s*=\s*"(' + VER + r')"', line)
            if m:
                out.append((f, i, m.group(1), "constant"))
                continue
            m = re.search(r'MUI\.Version\([^)]*"v(' + VER + r')', line)
            if m:
                out.append((f, i, m.group(1), "settings text"))
                continue
            m = re.search(r'"[^"]*\bv(' + VER + r')\b[^"]*loaded', line)
            if m:
                out.append((f, i, m.group(1), "log text"))
    return out


def changelog_top(d):
    f = d / "CHANGELOG.txt"
    if not f.exists():
        return None
    # the first version written on its own line: the newest entry ("Not on the Workshop yet" block
    # or the Workshop release block on top)
    for l in f.read_text(encoding="utf-8").splitlines():
        if re.fullmatch(VER, l.strip()):
            return l.strip()
    return None


def readme_versions():
    f = root / "README.md"
    res = {}
    if f.exists():
        for line in f.read_text(encoding="utf-8").splitlines():
            m = re.match(r"\|[^|]+\|\s*(" + VER + r")\s*\|\s*\[[^\]]+\]\(([^)]+)\)", line)
            if m:
                res[m.group(2)] = m.group(1)
    return res


def readme():
    """Rewrites the mod table in README.md from every About.xml (name, version, folder)."""
    f = root / "README.md"
    t = f.read_text(encoding="utf-8")
    rows = ["| Mod | Version | Folder |", "|---|---|---|"]
    for d in mods():
        x = (d / "About" / "About.xml").read_text(encoding="utf-8")
        m = re.search(r"<name>(.*?)</name>", x, re.S)
        name = m.group(1).strip().replace("&amp;", "&") if m else d.name
        rel = str(d.relative_to(root))
        rows.append("| %s | %s | [%s](%s) |" % (name, about_version(d) or "?", rel, rel))
    t = re.sub(r"\| Mod \| Version \| Folder \|\n(?:\|.*\|\n?)+", "\n".join(rows) + "\n", t)
    f.write_text(t, encoding="utf-8")
    print("README.md: %d mods" % (len(rows) - 2))


def check():
    bad = 0
    rd = readme_versions()
    for d in mods():
        rel = str(d.relative_to(root))
        v = about_version(d)
        problems = []
        if v is None:
            problems.append("About.xml has no <modVersion>")
        for f, i, sv, kind in source_versions(d):
            if sv != v:
                problems.append("%s:%d %s %s" % (f.relative_to(root), i, kind, sv))
        top = changelog_top(d)
        if top is not None and top != v:
            problems.append("CHANGELOG.txt top %s" % top)
        if rel in rd and rd[rel] != v:
            problems.append("README.md %s" % rd[rel])
        if problems:
            bad += 1
            print("%-28s %-8s  MISMATCH: %s" % (rel, v, "; ".join(problems)))
        else:
            print("%-28s %-8s  ok" % (rel, v))
    return 1 if bad else 0


def bump(rel, new, lines):
    d = root / rel
    old = about_version(d)
    a = d / "About" / "About.xml"
    a.write_text(re.sub(r"<modVersion>\s*" + VER + r"\s*</modVersion>", "<modVersion>%s</modVersion>" % new, a.read_text(encoding="utf-8")), encoding="utf-8")
    for f in {x[0] for x in source_versions(d)}:
        t = f.read_text(encoding="utf-8")
        t = re.sub(r'(\b(?:Version|Number)\s*=\s*")' + VER + '"', r"\g<1>%s" % new + "\"", t)
        t = re.sub(r'(MUI\.Version\([^)]*"v)' + VER, r"\g<1>%s" % new, t)
        t = re.sub(r'("[^"]*\bv)' + VER + r'(\b[^"]*loaded)', r"\g<1>%s\g<2>" % new, t)
        f.write_text(t, encoding="utf-8")
    f = root / "README.md"
    if f.exists():
        t = f.read_text(encoding="utf-8")
        t = re.sub(r"(\|[^|\n]+\|\s*)" + VER + r"(\s*\|\s*\[[^\]]+\]\(" + re.escape(rel) + r"\))", r"\g<1>%s\g<2>" % new, t)
        f.write_text(t, encoding="utf-8")
    if lines:
        c = d / "CHANGELOG.txt"
        entry = new + "\n" + "".join("- " + l + "\n" for l in lines) + "\n"
        t = c.read_text(encoding="utf-8") if c.exists() else ""
        if t.startswith(PENDING):
            t = PENDING + entry + t[len(PENDING):]
        else:
            t = PENDING + entry + t
        c.write_text(t, encoding="utf-8")
    print("%s: %s -> %s" % (rel, old, new))


def notes(rel):
    """The entries waiting for the next Workshop release."""
    t = (root / rel / "CHANGELOG.txt").read_text(encoding="utf-8")
    if not t.startswith(PENDING):
        print("(nothing new since the last Workshop release)")
        return
    end = t.find("\n=== ", len(PENDING))
    print(t[len(PENDING):end if end > 0 else len(t)].strip())


def release(rel, date):
    """Turns the waiting entries into a Workshop release block (newest version, date)."""
    c = root / rel / "CHANGELOG.txt"
    t = c.read_text(encoding="utf-8")
    if not t.startswith(PENDING):
        print("nothing waiting")
        return
    end = t.find("\n=== ", len(PENDING))
    body = t[len(PENDING):end + 1 if end > 0 else len(t)]
    rest = t[end + 1:] if end > 0 else ""
    v = about_version(root / rel)
    c.write_text("=== Workshop release %s (%s) ===\n" % (v, date) + body.rstrip() + "\n\n" + rest, encoding="utf-8")
    print("%s: Workshop release %s written" % (rel, v))


FOOT = "═══════════════════════"
OLD_INVITES = [
    "Something broken, or want something added? Please write it in the comments – I read them all.",
    "Něco nefunguje, nebo chceš něco přidat? Napiš to do komentářů – čtu je všechny.",
    "Something broken, or want something added? Please write it in the comments.",
]


def fold(rel, new, notes_file):
    d = root / rel
    c = d / "CHANGELOG.txt"
    t = c.read_text(encoding="utf-8")
    body = pathlib.Path(notes_file).read_text(encoding="utf-8").strip() + "\n\n"
    hist = ""
    if t.startswith(PENDING):
        end = t.find("\n=== ", len(PENDING))
        hist = t[len(PENDING):end + 1 if end > 0 else len(t)].strip()
        t = t[end + 1:] if end > 0 else ""
    if hist:
        hist = "=== Development history since the last Workshop release (test versions) ===\n" + hist + "\n\n"
    c.write_text(PENDING + new + "\n" + body + hist + t, encoding="utf-8")
    bump(rel, new, [])


def footer(rel):
    d = root / rel
    a = d / "About" / "About.xml"
    x = a.read_text(encoding="utf-8")
    m = re.search(r"(<description>)(.*?)(</description>)", x, re.S)
    desc = m.group(2)
    i = desc.find(FOOT)
    if i >= 0:
        desc = desc[:i]
    lines = [l for l in desc.rstrip().split("\n") if l.strip() not in OLD_INVITES]
    desc = "\n".join(lines).rstrip()
    v = about_version(d)
    en, cz, cur = [], [], None
    f = root / "Common" / "footer" / (d.name + ".txt")
    if f.exists():
        for l in f.read_text(encoding="utf-8").splitlines():
            if l.strip() in ("EN", "CZ"):
                cur = en if l.strip() == "EN" else cz
            elif l.strip() and cur is not None:
                cur.append(l.rstrip())
    esc = lambda s: s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")
    out = ["", "", FOOT, "IDEAS AND SUGGESTIONS",
           "Want something changed or added? Write it in the comments – I read every idea and take it into account."]
    if en:
        out += ["", "WHAT'S NEW IN %s" % v] + en
    out += ["", "NÁPADY A NÁVRHY",
            "Chceš něco změnit nebo přidat? Napiš to do komentářů – čtu všechny nápady a beru je v úvahu."]
    if cz:
        out += ["", "CO JE NOVÉHO VE VERZI %s" % v] + cz
    desc += esc("\n".join(out))
    a.write_text(x[:m.start(2)] + desc + x[m.end(2):], encoding="utf-8")
    import html
    size = len(html.unescape(desc).encode("utf-8"))
    print("%s: footer written (%d + %d change lines), description %d bytes%s" % (
        rel, len(en), len(cz), size, "  !!! OVER STEAM LIMIT 8000 – shorten it" if size > 8000 else ""))


if __name__ == "__main__":
    a = sys.argv[1:]
    if not a or a[0] == "check":
        sys.exit(check())
    if a[0] == "bump" and len(a) >= 3:
        bump(a[1], a[2], a[3:])
    elif a[0] == "readme":
        readme()
    elif a[0] == "notes" and len(a) == 2:
        notes(a[1])
    elif a[0] == "fold" and len(a) == 4:
        fold(a[1], a[2], a[3])
    elif a[0] == "footer" and len(a) == 2:
        footer(a[1])
    elif a[0] == "release" and len(a) == 3:
        release(a[1], a[2])
    else:
        print(__doc__)
