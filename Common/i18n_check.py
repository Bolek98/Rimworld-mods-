#!/usr/bin/env python3
"""Checks the translations of a mod against English (Keyed) and Czech (DefInjected).

  python3 Common/i18n_check.py <ModFolder> [Language ...]

For every language (default: all folders in Languages/ except English and Czech):
- every English Keyed file exists in the language with exactly the same keys,
- every key keeps the same placeholders ({0}, {PAWN_label} …) and the same number of literal \\n,
- every Czech DefInjected file exists in the language with the same keys,
- all files are valid XML.
Exit code 1 when something is wrong; prints one line per problem.
"""
import pathlib, re, sys
import xml.etree.ElementTree as ET

root = pathlib.Path(__file__).resolve().parent.parent
PH = re.compile(r"\{[^{}]*\}")


def keys(path):
    tree = ET.parse(path)
    return {el.tag: (el.text or "") for el in tree.getroot()}


def main():
    mod = root / sys.argv[1]
    langs_dir = mod / "Languages"
    langs = sys.argv[2:] or sorted(p.name for p in langs_dir.iterdir() if p.is_dir() and p.name not in ("English", "Czech"))
    bad = 0

    def problem(msg):
        nonlocal bad
        bad += 1
        print(msg)

    en_keyed = sorted((langs_dir / "English" / "Keyed").glob("*.xml")) if (langs_dir / "English" / "Keyed").exists() else []
    cz_def = sorted((langs_dir / "Czech" / "DefInjected").rglob("*.xml")) if (langs_dir / "Czech" / "DefInjected").exists() else []
    for lang in langs:
        for f in en_keyed:
            target = langs_dir / lang / "Keyed" / f.name
            if not target.exists():
                problem("%s: missing %s" % (lang, target.relative_to(mod)))
                continue
            try:
                src, dst = keys(f), keys(target)
            except ET.ParseError as e:
                problem("%s: bad XML %s: %s" % (lang, target.relative_to(mod), e))
                continue
            for k in src:
                if k not in dst:
                    problem("%s: %s missing key %s" % (lang, f.name, k))
                    continue
                if sorted(PH.findall(src[k])) != sorted(PH.findall(dst[k])):
                    problem("%s: %s key %s placeholders %s -> %s" % (lang, f.name, k, PH.findall(src[k]), PH.findall(dst[k])))
                if src[k].count("\\n") != dst[k].count("\\n"):
                    problem("%s: %s key %s line breaks %d -> %d" % (lang, f.name, k, src[k].count("\\n"), dst[k].count("\\n")))
                if src[k].strip() and not dst[k].strip():
                    problem("%s: %s key %s is empty" % (lang, f.name, k))
            for k in dst:
                if k not in src:
                    problem("%s: %s extra key %s" % (lang, f.name, k))
        for f in cz_def:
            rel = f.relative_to(langs_dir / "Czech")
            target = langs_dir / lang / rel
            if not target.exists():
                problem("%s: missing %s" % (lang, target.relative_to(mod)))
                continue
            try:
                src, dst = keys(f), keys(target)
            except ET.ParseError as e:
                problem("%s: bad XML %s: %s" % (lang, target.relative_to(mod), e))
                continue
            if set(src) != set(dst):
                problem("%s: %s keys differ (missing %s, extra %s)" % (lang, rel, sorted(set(src) - set(dst))[:5], sorted(set(dst) - set(src))[:5]))
            for k in src:
                if k in dst and sorted(PH.findall(src[k])) != sorted(PH.findall(dst[k])):
                    problem("%s: %s key %s placeholders differ" % (lang, rel, k))
    print("%s: %d problem(s) in %s" % (sys.argv[1], bad, ", ".join(langs)))
    sys.exit(1 if bad else 0)


if __name__ == "__main__":
    main()
