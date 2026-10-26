#!/usr/bin/env python3
"""Compile every mod from its Source folder against the RimWorld reference assemblies
(NuGet Krafs.Rimworld.Ref) – no game files needed. Used by GitHub Actions on every push,
and can be run locally:  python3 Common/ci_build.py [ModFolder ...]

RimWorld 1.7 runs on .NET 10 (with a Unity compatibility layer), so the check compiles for
net10.0 like RimRef asks; the DLLs we ship are still built the usual way.
Only checks that everything compiles (the DLLs in Assemblies/ are not touched).
Exit code 1 when a mod does not compile."""
import pathlib, subprocess, sys, tempfile, shutil, os

root = pathlib.Path(__file__).resolve().parent.parent
RIMWORLD_REF = os.environ.get("RIMWORLD_REF", "1.7.4985-unstable")
# --game 1.6: the same check against RimWorld 1.6 (net472 + its reference assemblies) – catches game
# code that exists only in 1.7 (a class moved to another namespace, a new method …)
GAME = "1.7"
TFM = "net10.0"
EXTRA = ""
HARMONY = "2.3.6"

# mods built from another mod's DLL (built first, in this order)
DEPENDS = {
    "Steward2/StewardCrises": ["Steward2/Steward"],
    "Steward2/StewardSchedules": ["Steward2/Steward"],
    "Steward2/StewardSupplies": ["Steward2/Steward"],
}
# old sources kept for history, not built any more
SKIP = {"Steward"}

PROJ = """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>{tfm}</TargetFramework>
    <LangVersion>10</LangVersion>
    <AssemblyName>{name}</AssemblyName>
    <OutputType>Library</OutputType>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
    <OutputPath>{out}</OutputPath>
    <Nullable>disable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
    <NoWarn>CS0618;CS0162;CS0168;CS0219;CS0414;CS0649;CS0169;CS1701;CS1702;CS8981</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="{src}/**/*.cs" Exclude="{src}/**/obj/**;{src}/**/bin/**" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Krafs.Rimworld.Ref" Version="{rw}" />
    <PackageReference Include="Lib.Harmony" Version="{harmony}" ExcludeAssets="runtime" />
{extra}  </ItemGroup>
  <ItemGroup>
{refs}  </ItemGroup>
</Project>
"""


def mods():
    for src in sorted(root.glob("**/Source")):
        m = src.parent
        rel = str(m.relative_to(root))
        if ".git" in m.parts or rel in SKIP or not any(src.rglob("*.cs")):
            continue
        yield rel


def dll_name(rel):
    asm = root / rel / "Assemblies"
    dlls = sorted(asm.glob("*.dll")) if asm.exists() else []
    return dlls[0].stem if len(dlls) == 1 else pathlib.Path(rel).name


def build(rel, outdir, built):
    name = dll_name(rel)
    src = (root / rel / "Source").as_posix()
    refs = ""
    for dep in DEPENDS.get(rel, []):
        if dep not in built:
            return False, "dependency %s did not build" % dep
        refs += '    <Reference Include="%s"><HintPath>%s</HintPath><Private>false</Private></Reference>\n' % (
            pathlib.Path(built[dep]).stem, built[dep])
    work = pathlib.Path(tempfile.mkdtemp(prefix="ci_"))
    out = outdir / rel.replace("/", "_")
    (work / (name + ".csproj")).write_text(PROJ.format(name=name, out=out.as_posix() + "/", src=src, rw=RIMWORLD_REF, tfm=TFM, extra=EXTRA,
                                                     harmony=HARMONY, refs=refs), encoding="utf-8")
    r = subprocess.run(["dotnet", "build", str(work), "-c", "Release", "-nologo", "-v", "q", "-clp:ErrorsOnly"],
                       capture_output=True, text=True)
    shutil.rmtree(work, ignore_errors=True)
    if r.returncode != 0:
        errs = [l.strip() for l in (r.stdout + r.stderr).splitlines() if "error" in l]
        return False, "\n".join(sorted(set(errs))[:25]) or (r.stdout + r.stderr)[-2000:]
    built[rel] = (out / (name + ".dll")).as_posix()
    return True, built[rel]


def main():
    global GAME, TFM, EXTRA, RIMWORLD_REF
    args = sys.argv[1:]
    if "--game" in args:
        i = args.index("--game")
        GAME = args[i + 1]
        del args[i:i + 2]
    if GAME == "1.6":
        RIMWORLD_REF = os.environ.get("RIMWORLD_REF_16", "1.6.4871")
        TFM = "net472"
        EXTRA = '    <PackageReference Include="Microsoft.NETFramework.ReferenceAssemblies" Version="1.0.3" PrivateAssets="all" />\n'
    want = args
    todo = [m for m in mods() if not want or m in want]
    # dependencies first
    todo.sort(key=lambda m: (0 if any(m in d for d in DEPENDS.values()) else 1, m))
    outdir = pathlib.Path(tempfile.mkdtemp(prefix="ci_out_"))
    built, failed = {}, []
    for m in todo:
        ok, msg = build(m, outdir, built)
        print(("OK    " if ok else "FAIL  ") + m)
        if not ok:
            failed.append(m)
            print("      " + msg.replace("\n", "\n      "))
    print("\n%d of %d mods compile against RimWorld %s." % (len(todo) - len(failed), len(todo), GAME))
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
