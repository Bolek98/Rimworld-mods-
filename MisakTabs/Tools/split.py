#!/usr/bin/env python3
"""Make the single-part mods (Clear Bills, Health, …) from Clear Tabs.

Each one is the same code under its own namespace with only its parts switched on
(MisakTabs/Source/BuildInfo.cs is replaced). Run from the repository root:
    python3 MisakTabs/Tools/split.py [--build]
"""
import os, re, shutil, subprocess, sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
SRC = os.path.join(ROOT, 'MisakTabs')

SPLITS = [
    # folder, short id, name, parts, anatomy, en description, cz description
    ('MisakBills', 'bills', 'Clear Bills – Bills Tab Overhaul',
     ['bills', 'replaceOtherBillTabs', 'billDialog', 'recipePicker'], False,
     'The bills tab of work benches in a clear dark look: recipes with pictures, search, categories and filters (locked recipes say which research is missing), the bill queue as cards with who works on it and how many you have, move / pause / copy / paste bills with one click, linked bills, a clear bill window and an "Add bill" picker with groups and search.',
     'Záložka zakázek u pracovních stolů v přehledném tmavém vzhledu: recepty s obrázky, hledání, kategorie a filtry (zamčené recepty řeknou, který výzkum chybí), fronta zakázek jako karty s tím, kdo na nich dělá a kolik toho máš, přesun / pauza / kopírování / vložení jedním klikem, propojené zakázky, přehledné okno zakázky a výběr „Přidat zakázku“ se skupinami a hledáním.'),
    ('MisakHealth', 'health', 'Clear Health – Anatomy Health Tab',
     ['health', 'replaceOtherHealthTabs', 'surgeryPicker'], True,
     'The health tab with a big anatomy figure (body, organs, bones) coloured by the state of every part, race features also for modded races, a card for every part, filters and one row per condition, plus an "Add operation" picker with groups, search and risk.',
     'Záložka zdraví s velkou anatomickou postavou (tělo, orgány, kosti) obarvenou podle stavu každé části, rysy ras i z modů, karta pro každou část, filtry a jeden řádek na každý stav, k tomu výběr „Přidat operaci“ se skupinami, hledáním a rizikem.'),
    ('MisakCharacter', 'character', 'Clear Character – Character, Social & Needs',
     ['character', 'social', 'needs', 'paneLook'], False,
     'The character tab (backstory, traits, skills, a life timeline, records), the social tab (everyone they know with both opinions and why), the needs tab and the inspect pane tabs in one clear dark look.',
     'Záložka postavy (minulost, rysy, dovednosti, časová osa života, záznamy), záložka vztahů (všichni, koho zná, s oběma názory a proč), záložka potřeb a záložky panelu v jednom přehledném tmavém vzhledu.'),
    ('MisakGear', 'gear', 'Clear Gear – Gear Tab Overhaul',
     ['gear'], False,
     'The gear tab as a list by body place (also modded places), weapons with quality, range and DPS, overall armor, inventory by group with eat / use / drop, free mass and quick outfit, battle outfit and drug policy.',
     'Záložka výbavy jako seznam podle místa na těle (i místa z modů), zbraně s kvalitou, dosahem a DPS, celková ochrana, věci v kapsách ve skupinách se sníst / užít / odhodit, volná nosnost a rychlá volba oblékání, bojového oblečení a drog.'),
    ('MisakInfo', 'info', 'Clear Info Card – Colonist Infocentrum',
     ['infoCard'], True,
     'The info card (i) of a colonist: all stats in groups with the colony average (green better, orange worse), comparing with another colonist, key tiles, what a stat is made of, about the race, and pages for life story, health, social, gear and history.',
     'Karta info (i) kolonisty: všechny údaje ve skupinách s průměrem kolonie (zeleně lepší, oranžově horší), porovnání s jiným kolonistou, hlavní dlaždice, z čeho se údaj skládá, o rase a stránky životopis, zdraví, vztahy, výbava a historie.'),
    ('MisakPickers', 'pickers', 'Clear Pickers – Choice Windows Overhaul',
     ['plantPicker', 'ownerPicker', 'policyPicker', 'researchPicker', 'mechPicker', 'animalPicker'], False,
     'Clear choice windows with groups, search and sorting: plants to grow, owner of beds and thrones, outfit / food / drug policy and allowed area, research, mech gestation and animal master, plus an animal overview.',
     'Přehledná výběrová okna se skupinami, hledáním a řazením: rostliny k pěstování, vlastník postele a trůnu, oblékání / jídlo / drogy a povolená oblast, výzkum, líhnutí mechů a pán zvířete, k tomu přehled zvířat.'),
    ('MisakTrade', 'trade', 'Clear Trade – Trade Window Overhaul',
     ['tradeWindow'], False,
     'The trade window with categories, search and sorting, item names with quality, both sides\' counts and prices and a cart.',
     'Obchodní okno s kategoriemi, hledáním a řazením, názvy věcí s kvalitou, počty a cenami obou stran a košíkem.'),
]

ABOUT = '''<?xml version="1.0" encoding="utf-8"?>
<ModMetaData>
  <name>[B98] {name}</name>
  <author>Bolek98</author>
  <packageId>bolek98.misak{sid}</packageId>
  <modVersion>{ver}</modVersion>
  <supportedVersions>
    <li>1.6</li>
    <li>1.7</li>
  </supportedVersions>
  <modDependencies>
    <li>
      <packageId>brrainz.harmony</packageId>
      <displayName>Harmony</displayName>
      <steamWorkshopUrl>steam://url/CommunityFilePage/2009463077</steamWorkshopUrl>
    </li>
  </modDependencies>
  <loadAfter>
    <li>brrainz.harmony</li>
  </loadAfter>
  <description>{en}

One part of Clear Tabs on its own – take only what you want. With Clear Tabs (all parts in one) active, this mod stays off by itself. Requires Harmony. RimWorld 1.6 and 1.7.

---

{cz}

Jedna část Clear Tabs samostatně – vezmi si jen to, co chceš. Když máš zapnutý Clear Tabs (všechno v jednom), tenhle mod se sám vypne. Vyžaduje Harmony. RimWorld 1.6 a 1.7.</description>
</ModMetaData>
'''


def esc(t):
    return t.replace('&', '&amp;').replace('<', '&lt;').replace('>', '&gt;')


def main():
    build = '--build' in sys.argv
    ver = re.search(r'<modVersion>([^<]+)</modVersion>', open(os.path.join(SRC, 'About', 'About.xml'), encoding='utf-8').read()).group(1)
    for folder, sid, name, parts, anatomy, en, cz in SPLITS:
        dst = os.path.join(ROOT, folder)
        ns = folder
        for sub in ('Source', 'Languages', 'Anatomy'):
            shutil.rmtree(os.path.join(dst, sub), ignore_errors=True)
        os.makedirs(os.path.join(dst, 'Source'), exist_ok=True)
        os.makedirs(os.path.join(dst, 'About'), exist_ok=True)
        os.makedirs(os.path.join(dst, 'Assemblies'), exist_ok=True)
        for f in sorted(os.listdir(os.path.join(SRC, 'Source'))):
            if not f.endswith('.cs') or f == 'BuildInfo.cs':
                continue
            s = open(os.path.join(SRC, 'Source', f), encoding='utf-8').read()
            s = re.sub(r'\bnamespace MisakTabs\b', 'namespace ' + ns, s)
            s = s.replace('MisakTabs.Look.', ns + '.Look.')
            s = s.replace('"bolek98.misaktabs"', '"bolek98.misak' + sid + '"')
            s = s.replace('"misak.tabs.', '"misak.' + sid + '.')
            s = '// GENERATED from MisakTabs by MisakTabs/Tools/split.py – edit MisakTabs, not this file.\n' + s
            open(os.path.join(dst, 'Source', f), 'w', encoding='utf-8').write(s)
        parts_cs = ', '.join('"%s"' % p for p in parts)
        open(os.path.join(dst, 'Source', 'BuildInfo.cs'), 'w', encoding='utf-8').write(
            '// GENERATED by MisakTabs/Tools/split.py\nnamespace %s\n{\n    public static class BuildInfo\n    {\n'
            '        public const string Name = "%s";\n        public const bool Split = true;\n'
            '        private static readonly string[] Parts = { %s };\n'
            '        public static bool Has(string field) => Parts == null || System.Array.IndexOf(Parts, field) >= 0;\n    }\n}\n'
            % (ns, name.split(' – ')[0], parts_cs))
        shutil.copytree(os.path.join(SRC, 'Languages'), os.path.join(dst, 'Languages'))
        if anatomy:
            shutil.copytree(os.path.join(SRC, 'Anatomy'), os.path.join(dst, 'Anatomy'))
        shutil.copy(os.path.join(SRC, 'CHANGELOG.txt'), os.path.join(dst, 'CHANGELOG.txt'))
        open(os.path.join(dst, 'About', 'About.xml'), 'w', encoding='utf-8').write(
            ABOUT.format(name=esc(name), sid=sid, ver=ver, en=esc(en), cz=esc(cz)))
        if build:
            out = subprocess.run(['/home/claude/work/mkdll.sh', dst, folder], capture_output=True, text=True)
            print(folder, (out.stdout + out.stderr).strip().splitlines()[-1:] )
        else:
            print(folder, 'written')


if __name__ == '__main__':
    main()
