#!/usr/bin/env python3
"""Mišák Core: copy shared files from Common/ into every mod that uses them.
Each mod keeps its own namespace (no new Workshop dependency). Mods that have MLog.cs
get '#define MISAK_MLOG' so shared code writes to the mod's own log file."""
import pathlib, re, sys
root = pathlib.Path(__file__).resolve().parent.parent
shared = ["MisakUI.cs", "MisakReport.cs", "MisakNews.cs"]
check = "--check" in sys.argv
bad = 0
for name in shared:
    src = (root / "Common" / name).read_text(encoding="utf-8")
    # MisakReport.cs and MisakNews.cs go everywhere MisakUI.cs is (MUI.Version draws the report button)
    targets = sorted(set(root.glob("**/Source/**/" + name)) | set(p.parent / name for p in root.glob("**/Source/**/MisakUI.cs")))
    for target in targets:
        ui = (target.parent / "MisakUI.cs").read_text(encoding="utf-8")
        old = target.read_text(encoding="utf-8") if target.exists() else ""
        ns = re.search(r"^namespace ([A-Za-z0-9_.]+)", ui, re.M).group(1)
        new = src.replace("__NS__", ns)
        if (target.parent / "MLog.cs").exists():
            new = "#define MISAK_MLOG\n" + new
        if new != old:
            bad += 1
            print(("OUTDATED " if check else "updated  ") + str(target.relative_to(root)))
            if not check:
                target.write_text(new, encoding="utf-8")
sys.exit(1 if check and bad else 0)
