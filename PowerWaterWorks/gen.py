# Generates the Defs, Czech DefInjected and Textures_TODO.txt of Power & Water Works.
# Run from the mod folder: python3 gen.py
from xml.sax.saxutils import escape as E
import os

W = []   # (defType, defName, label_cz, desc_cz) for DefInjected
TEX = [] # (final texPath, size, rotatable, placeholder)

def li(items): return "".join(f"<li>{x}</li>" for x in items)

def graphic(ph, cls, ds, color):
    return f"""    <graphicData>
      <texPath>{ph}</texPath>
      <graphicClass>{cls}</graphicClass>
      <drawSize>{ds}</drawSize>
      <color>{color}</color>
      <damageData><rect>(0.1,0.1,0.8,0.8)</rect></damageData>
    </graphicData>"""

# a real picture (from Grok) in Textures/ replaces the tinted placeholder
def real_tex(final, d):
    sx, sz = d['size']
    ds = d.get('ds', f'({sx},{sz})')
    if os.path.exists(f"Textures/{final}_south.png"):
        return (final, 'Graphic_Multi', ds, '(1,1,1)')
    if os.path.exists(f"Textures/{final}.png"):
        return (final, 'Graphic_Single', ds, '(1,1,1)')
    return (d['ph'], d['cls'], ds, d['color'])

def item_tex(name, ph, cls, color):
    final = f"Things/Item/PowerWaterWorks/{name}"
    if os.path.exists(f"Textures/{final}.png"):
        ph, cls, color = final, 'Graphic_Single', '(1,1,1)'
    return f"""      <texPath>{ph}</texPath>
      <graphicClass>{cls}</graphicClass>
      <color>{color}</color>"""

def dbh_tex(dn, ph, cls, ds, size):
    final = f"Things/Building/PowerWaterWorks/{dn[4:]}"
    if os.path.exists(f"Textures/{final}.png"):
        ph, cls, color, ds = final, 'Graphic_Single', '(1,1,1)', f"({size[0]},{size[1]})"
    else:
        color = '(0.8,0.9,1.0)'
    return f"""      <texPath>{ph}</texPath>
      <graphicClass>{cls}</graphicClass>
      <color>{color}</color>
      <drawSize>{ds}</drawSize>"""

def building(d):
    dn = d['defName']; sx, sz = d['size']
    final = f"Things/Building/PowerWaterWorks/{dn[4:]}"
    TEX.append((final, f"{sx}x{sz}", 'yes' if d.get('rot') else 'no', d['ph'], f"{sx*128}x{sz*128}" if d['cls']=='Graphic_Single' else f"{sx*128}x{sz*128} (_north/_east/_south)"))
    W.append(('ThingDef', dn, d['cz'], d['czd']))
    parent = d.get('parent', 'BuildingBase')
    comps = d.get('comps', '')
    pw = d.get('placeWorkers', [])
    cost = "".join(f"<{k}>{v}</{k}>" for k, v in d['cost'].items())
    rp = d.get('research', [])
    extra = d.get('extra', '')
    aff = d.get('aff', 'Medium')
    return f"""
  <!-- FINAL texture: {final} ({sx}x{sz}, {'rotatable' if d.get('rot') else 'single'}) – placeholder below -->
  <ThingDef ParentName="{parent}">
    <defName>{dn}</defName>
    <label>{E(d['label'])}</label>
    <description>{E(d['desc'])}</description>
    <thingClass>{d.get('thingClass','Building')}</thingClass>
{graphic(*real_tex(final, d))}
    <size>({sx},{sz})</size>
    <rotatable>{'true' if d.get('rot') else 'false'}</rotatable>
    <altitudeLayer>Building</altitudeLayer>
    <passability>{d.get('passab','Impassable')}</passability>
    <fillPercent>{d.get('fill',0.8)}</fillPercent>
    <blockWind>{d.get('wind','true')}</blockWind>
    <castEdgeShadows>true</castEdgeShadows>
    <tickerType>Normal</tickerType>
    <canOverlapZones>false</canOverlapZones>
    <statBases>
      <MaxHitPoints>{d.get('hp',300)}</MaxHitPoints>
      <WorkToBuild>{d.get('work',4000)}</WorkToBuild>
      <Flammability>{d.get('flam',0.5)}</Flammability>
      <Beauty>{d.get('beauty',-10)}</Beauty>
    </statBases>
    <costList>{cost}</costList>
    <constructionSkillPrerequisite>{d.get('skill',6)}</constructionSkillPrerequisite>
    <terrainAffordanceNeeded>{aff}</terrainAffordanceNeeded>
    <designationCategory>{d.get('cat','Power')}</designationCategory>
    <constructEffect>ConstructMetal</constructEffect>
    <researchPrerequisites>{li(rp)}</researchPrerequisites>
    <placeWorkers>{li(pw)}</placeWorkers>{extra}
    <comps>{comps}
      <li Class="PowerWaterWorks.CompProperties_GridOverview"/>
    </comps>
  </ThingDef>"""

def plant(watts, fuel=None, flick=True, breakdown=True, cls='CompPowerPlant'):
    s = f"""
      <li Class="CompProperties_Power">
        <compClass>{cls}</compClass>
        <basePowerConsumption>-{watts}</basePowerConsumption>
        <transmitsPower>true</transmitsPower>
      </li>"""
    if flick: s += '\n      <li Class="CompProperties_Flickable"/>'
    if breakdown: s += '\n      <li Class="CompProperties_Breakdownable"/>'
    if fuel: s += fuel
    return s

def trader(watts, flick=True, breakdown=True):
    s = f"""
      <li Class="CompProperties_Power">
        <compClass>CompPowerTrader</compClass>
        <basePowerConsumption>{watts}</basePowerConsumption>
        <shortCircuitInRain>false</shortCircuitInRain>
      </li>"""
    if flick: s += '\n      <li Class="CompProperties_Flickable"/>'
    if breakdown: s += '\n      <li Class="CompProperties_Breakdownable"/>'
    return s

def refuel(rate, cap, cats=(), defs=(), label='fuel', extra=''):
    c = li(cats); t = li(defs)
    return f"""
      <li Class="CompProperties_Refuelable">
        <fuelConsumptionRate>{rate}</fuelConsumptionRate>
        <fuelCapacity>{cap}</fuelCapacity>
        <fuelFilter>{'<categories>'+c+'</categories>' if c else ''}{'<thingDefs>'+t+'</thingDefs>' if t else ''}</fuelFilter>
        <showAllowAutoRefuelToggle>true</showAllowAutoRefuelToggle>{extra}
      </li>"""

B = []
B.append(dict(defName='PWW_HydroPlant', label='hydro power plant', cz='malá vodní elektrárna',
    desc='A small run-of-river power plant with several turbines. At least three of its cells must stand on moving water, the rest on firm ground. Gives steady power day and night; when the river freezes in hard winter it gives only 40 %.',
    czd='Malá průtočná elektrárna s několika turbínami. Aspoň tři její políčka musí stát na tekoucí vodě, zbytek na pevné zemi. Dává stálý proud ve dne i v noci; když řeka v tuhé zimě zamrzne, dává jen 40 %.',
    size=(3,3), rot=True, ph='Things/Building/Misc/MoisturePump', cls='Graphic_Single', color='(0.55,0.75,1.0)',
    cost={'Steel':250,'ComponentIndustrial':5,'WoodLog':50}, work=9000, research=['PWW_PowerWorks'],
    comps=plant(2400, cls='PowerWaterWorks.CompPowerPlantHydro'), placeWorkers=['PowerWaterWorks.PlaceWorker_Hydro'], aff='Medium', wind='false'))
B.append(dict(defName='PWW_BiogasPlant', label='biogas plant', cz='bioplynová stanice',
    desc='Ferments raw food, meat, plant matter and (with Dubs Bad Hygiene) fecal sludge into gas that drives a generator.',
    czd='Nechá zkvasit syrové jídlo, maso, rostlinnou hmotu a (s Dubs Bad Hygiene) fekální kal na plyn, který pohání generátor.',
    size=(3,3), ph='Things/Building/Misc/GeneProcessor', cls='Graphic_Single', color='(0.6,0.9,0.5)',
    cost={'Steel':180,'ComponentIndustrial':3}, work=7000, research=['PWW_PowerWorks'],
    comps=plant(1500, refuel(25, 120, cats=['FoodRaw','PlantMatter'], defs=['<!--DBH-->'][:0])), ))
B.append(dict(defName='PWW_DeepGeothermal', label='deep geothermal plant', cz='hlubinná geotermální elektrárna',
    desc='Drills kilometres down to hot rock – no steam geyser needed. Very expensive, but gives strong power forever.',
    czd='Vrtá kilometry hluboko k horké skále – nepotřebuje parní gejzír. Velmi drahá, ale dává silný proud navždy.',
    size=(4,4), ph='Things/Building/Misc/LongRangeMineralScanner', cls='Graphic_Multi', color='(1.0,0.6,0.45)',
    cost={'Steel':600,'Plasteel':150,'ComponentIndustrial':10,'ComponentSpacer':2}, work=30000, skill=10, research=['PWW_Industry','GeothermalPower'],
    comps=plant(5000), aff='Heavy'))
B.append(dict(defName='PWW_Incinerator', label='incinerator generator', cz='spalovna',
    desc='Burns stone chunks and steel slag at very high temperature and turns the heat into power. Also tidies up the base.',
    czd='Spaluje kamenné úlomky a ocelovou strusku při velmi vysoké teplotě a teplo mění na proud. Zároveň uklidí základnu.',
    size=(2,2), ph='Things/Building/Power/ToxifierGenerator', cls='Graphic_Single', color='(0.55,0.55,0.55)',
    cost={'Steel':150,'ComponentIndustrial':2}, work=5000, research=['PWW_PowerWorks'],
    comps=plant(800, refuel(6, 12, cats=['StoneChunks'], defs=['ChunkSlagSteel'])) +
      '\n      <li Class="CompProperties_HeatPusher"><compClass>CompHeatPusherPowered</compClass><heatPerSecond>4</heatPerSecond></li>'))
B.append(dict(defName='PWW_Diesel', label='diesel generator', cz='dieselagregát',
    desc='Burns chemfuel. With "Auto-start" on it switches itself on when the batteries of its grid run low and off again when they are full – a backup that only runs when needed.',
    czd='Spaluje chemické palivo. Se zapnutým „Automatickým startem“ se sám zapne, když baterie v síti docházejí, a zase vypne, když jsou plné – záloha, která běží jen v případě potřeby.',
    size=(2,2), ph='Things/Building/Power/MechBooster', cls='Graphic_Single', color='(1.0,0.85,0.4)',
    cost={'Steel':140,'ComponentIndustrial':3}, work=4500, research=['PWW_PowerWorks'],
    comps=plant(1800, refuel(8, 60, defs=['Chemfuel'])) + '\n      <li Class="PowerWaterWorks.CompProperties_AutoStart"/>'))
B.append(dict(defName='PWW_FissionReactor', label='fission reactor', cz='jaderný reaktor',
    desc='A compact fission reactor burning fuel rods. Huge steady power, but it heats up: it needs working cooling towers within 10 cells. At 100 % heat it shuts itself down (or melts down, if that is switched on in the settings). The SCRAM button stops it at once.',
    czd='Kompaktní štěpný reaktor na palivové tyče. Obrovský stálý výkon, ale zahřívá se: potřebuje fungující chladicí věže do 10 políček. Při 100 % tepla se sám odstaví (nebo se roztaví, pokud je to zapnuté v nastavení). Tlačítko SCRAM ho okamžitě zastaví.',
    size=(4,4), ph='Things/Building/Misc/MultiAnalyzer', cls='Graphic_Single', color='(0.7,1.0,0.7)',
    cost={'Steel':500,'Plasteel':200,'Uranium':60,'ComponentIndustrial':12,'ComponentSpacer':3}, work=40000, skill=12, research=['PWW_Nuclear'], aff='Heavy', hp=600, flam=0,
    comps=plant(12000, refuel(1, 5, defs=['PWW_FuelRod'])) + '\n      <li Class="PowerWaterWorks.CompProperties_Reactor"><heatPerHour>10</heatPerHour><coolingRadius>8</coolingRadius></li>',
    placeWorkers=['PowerWaterWorks.PlaceWorker_CoolingRadius']))
B.append(dict(defName='PWW_CoolingTower', label='cooling tower', cz='chladicí věž',
    desc='Cools a fission reactor within 10 cells. One working tower is enough for one reactor.',
    czd='Chladí jaderný reaktor do 10 políček. Na jeden reaktor stačí jedna fungující věž.',
    size=(3,3), ph='Things/Building/Misc/GroundPenetratingScanner', cls='Graphic_Single', color='(0.85,0.9,0.95)',
    cost={'Steel':250,'BlocksGranite':150,'ComponentIndustrial':4}, work=12000, research=['PWW_Nuclear'], aff='Heavy', flam=0,
    comps=trader(300) + '\n      <li Class="PowerWaterWorks.CompProperties_CoolingTower"><cooling>12</cooling></li>'))
B.append(dict(defName='PWW_FusionReactor', label='fusion reactor', cz='fúzní reaktor',
    desc='A spacer-age fusion reactor. Enormous power from a fusion cell that lasts many days. Safe – it cannot melt down.',
    czd='Fúzní reaktor z vesmírné doby. Obrovský výkon z fúzního článku, který vydrží mnoho dní. Bezpečný – nemůže se roztavit.',
    size=(5,5), ph='Things/Building/Misc/BandNode', cls='Graphic_Single', color='(0.5,0.9,1.0)',
    cost={'Plasteel':400,'Uranium':100,'ComponentSpacer':10,'Gold':50}, work=60000, skill=14, research=['PWW_Fusion'], aff='Heavy', hp=800, flam=0,
    comps=plant(30000, refuel(0.2, 2, defs=['PWW_FusionCell']))))
B.append(dict(defName='PWW_BigBattery', label='large battery', cz='velká baterie',
    desc='A bank of industrial cells: stores 6000 Wd and loses less energy than small batteries.',
    czd='Sada průmyslových článků: uloží 6000 Wd a ztrácí méně energie než malé baterie.',
    size=(3,2), rot=True, ph='Things/Building/Misc/WastepackAtomizer', cls='Graphic_Multi', color='(0.6,0.7,0.9)',
    cost={'Steel':300,'ComponentIndustrial':6}, work=6000, research=['PWW_Industry'],
    comps="""
      <li Class="CompProperties_Battery">
        <storedEnergyMax>6000</storedEnergyMax>
        <efficiency>0.7</efficiency>
        <shortCircuitInRain>false</shortCircuitInRain>
        <transmitsPower>true</transmitsPower>
      </li>
      <li Class="CompProperties_Breakdownable"/>"""))
B.append(dict(defName='PWW_Flywheel', label='flywheel', cz='setrvačník',
    desc='A cheap mechanical store for 300 Wd. Good for short gaps, but it loses 10 % of its energy every day.',
    czd='Levné mechanické úložiště na 300 Wd. Dobré na krátké výpadky, ale každý den ztratí 10 % energie.',
    size=(1,1), ph='Things/Building/Production/PollutionPump', cls='Graphic_Single', color='(0.75,0.75,0.75)',
    cost={'Steel':60,'ComponentIndustrial':1}, work=1500, research=['PWW_PowerWorks'],
    comps="""
      <li Class="CompProperties_Battery">
        <storedEnergyMax>300</storedEnergyMax>
        <efficiency>0.85</efficiency>
        <shortCircuitInRain>false</shortCircuitInRain>
        <transmitsPower>true</transmitsPower>
      </li>
      <li Class="PowerWaterWorks.CompProperties_SelfDischarge"><perDay>0.1</perDay></li>"""))
B.append(dict(defName='PWW_CircuitBreaker', label='circuit breaker', cz='jistič',
    desc='Works like a power switch. When a short circuit happens anywhere on its grid, the breaker trips instead – no explosion, no fire. Switch it back on by hand.',
    czd='Funguje jako vypínač. Když kdekoli v jeho síti dojde ke zkratu, jistič vypadne – žádný výbuch, žádný požár. Zpět ho zapneš ručně.',
    size=(1,1), ph='Things/Building/Misc/FirefoamPopper', cls='Graphic_Single', color='(1.0,0.45,0.4)',
    cost={'Steel':25,'ComponentIndustrial':1}, work=800, research=['PWW_PowerWorks'], thingClass='Building_PowerSwitch', passab='Standable', fill=0.2, wind='false', aff='Light',
    comps="""
      <li Class="CompProperties_Power">
        <compClass>CompPowerTransmitter</compClass>
        <transmitsPower>true</transmitsPower>
      </li>
      <li Class="CompProperties_Flickable"/>"""))
B.append(dict(defName='PWW_AutoStonecutter', label='automatic stonecutter', cz='automatický kameník',
    desc='Takes stone chunks lying next to it and cuts them into blocks on its own – no colonist needed. One chunk per in-game hour.',
    czd='Bere kamenné úlomky ležící vedle něj a sám z nich řeže bloky – nepotřebuje kolonistu. Jeden úlomek za herní hodinu.',
    size=(2,2), ph='Things/Building/Misc/GeneProcessor', cls='Graphic_Single', color='(0.8,0.7,0.55)', cat='Production',
    cost={'Steel':150,'ComponentIndustrial':3}, work=5000, research=['PWW_Industry','Stonecutting'],
    comps=trader(400) + '\n      <li Class="PowerWaterWorks.CompProperties_AutoStonecutter"><ticksPerChunk>2500</ticksPerChunk></li>'))
def bench(dn, label, cz, desc, czd, ph, cls, color, cost, research, watts, size=(3,2)):
    return dict(defName=dn, label=label, cz=cz, desc=desc, czd=czd, size=size, rot=True, ph=ph, cls=cls, color=color, cat='Production',
        cost=cost, work=5000, research=research, thingClass='Building_WorkTable', parent='BenchBase', passab='PassThroughOnly', fill=0.5, wind='false',
        extra="""
    <hasInteractionCell>true</hasInteractionCell>
    <interactionCellOffset>(0,0,-1)</interactionCellOffset>
    <surfaceType>Item</surfaceType>
    <inspectorTabs><li>ITab_Bills</li></inspectorTabs>
    <building><spawnedConceptLearnOpportunity>BillsTab</spawnedConceptLearnOpportunity></building>""",
        comps=trader(watts))
B.append(bench('PWW_InductionFurnace','induction furnace','indukční pec',
    'An electric furnace: gets more steel from slag and makes fuel rods and fusion cells.',
    'Elektrická pec: získá ze strusky víc oceli a vyrábí palivové tyče a fúzní články.',
    'Things/Building/Misc/GeneAssembler/GeneAssembler','Graphic_Multi','(1.0,0.65,0.4)',{'Steel':200,'ComponentIndustrial':4},['PWW_Industry'],1200))
B.append(bench('PWW_ComponentLine','component line','linka na součástky',
    'An automated line that makes components five at a time, a bit cheaper than by hand.',
    'Automatizovaná linka, která vyrábí součástky po pěti, o něco levněji než ručně.',
    'Things/Building/Misc/WastepackAtomizer','Graphic_Multi','(1.0,0.9,0.5)',{'Steel':250,'ComponentIndustrial':6},['PWW_Industry','Fabrication'],800))
B.append(bench('PWW_TextileMill','textile mill','textilka',
    'Shreds worn-out clothes back into cloth.',
    'Rozmělní obnošené oblečení zpět na látku.',
    'Things/Building/Misc/GeneAssembler/GeneAssembler','Graphic_Multi','(0.85,0.6,0.95)',{'Steel':150,'ComponentIndustrial':3},['PWW_Industry','ComplexClothing'],400))
B.append(bench('PWW_Cannery','cannery','konzervárna',
    'Cans raw food. Canned food never rots.',
    'Konzervuje syrové jídlo. Konzervy se nikdy nezkazí.',
    'Things/Building/Misc/GeneProcessor','Graphic_Single','(0.95,0.95,0.85)',{'Steel':120,'ComponentIndustrial':2},['PWW_Industry'],300, size=(2,2)))
B[-1]['rot'] = False; B[-1]['extra'] = B[-1]['extra'].replace('(0,0,-1)', '(0,0,-1)')
B.append(dict(defName='PWW_Sprinkler', label='irrigation sprinkler', cz='zavlažovač',
    desc='Waters the plants around it: they grow 15 % faster. Needs power.',
    czd='Zalévá rostliny kolem sebe: rostou o 15 % rychleji. Potřebuje proud.',
    size=(1,1), ph='Things/Building/Production/PollutionPump', cls='Graphic_Single', color='(0.5,0.7,1.0)', cat='Production',
    cost={'Steel':40,'ComponentIndustrial':1}, work=1200, research=['PWW_WaterWorks'], passab='Standable', fill=0.2, wind='false', aff='Light',
    comps=trader(60, breakdown=False) + '\n      <li Class="PowerWaterWorks.CompProperties_Sprinkler"><radius>5.9</radius><bonus>0.15</bonus></li>',
    placeWorkers=['PowerWaterWorks.PlaceWorker_SprinklerRadius']))

# biogas: DBH fecal sludge
for b in B:
    if b['defName'] == 'PWW_BiogasPlant':
        b['comps'] = b['comps'].replace('<fuelFilter><categories>', '<fuelFilter><thingDefs><li MayRequire="Dubwise.DubsBadHygiene">FecalSludge</li></thingDefs><categories>')

R = [  # research
 ('PWW_PowerWorks','power works','energetika','Better generators, storage and a circuit breaker.','Lepší generátory, úložiště a jistič.','Industrial',900,['Electricity','Batteries'],(0.0,0.0)),
 ('PWW_Industry','electric industry','elektrický průmysl','Electric factories, the large battery and the deep geothermal plant.','Elektrické továrny, velká baterie a hlubinná geotermální elektrárna.','Industrial',1500,['PWW_PowerWorks','Machining'],(1.0,0.0)),
 ('PWW_WaterWorks','water works','vodárenství','Irrigation sprinklers and (with Dubs Bad Hygiene) water buildings.','Zavlažovače a (s Dubs Bad Hygiene) vodárenské stavby.','Industrial',800,['PWW_PowerWorks'],(1.0,1.0)),
 ('PWW_Nuclear','nuclear fission','jaderné štěpení','The fission reactor, cooling towers and fuel rods.','Jaderný reaktor, chladicí věže a palivové tyče.','Industrial',4000,['PWW_Industry','MultiAnalyzer'],(2.0,0.0)),
 ('PWW_Fusion','nuclear fusion','jaderná fúze','The fusion reactor and fusion cells.','Fúzní reaktor a fúzní články.','Spacer',8000,['PWW_Nuclear','AdvancedFabrication'],(3.0,0.0)),
]

def research_xml():
    out = []
    for dn, label, cz, desc, czd, tech, cost, pre, (x, y) in R:
        W.append(('ResearchProjectDef', dn, cz, czd))
        hb = '\n    <requiredResearchBuilding>HiTechResearchBench</requiredResearchBuilding>' if dn in ('PWW_Nuclear','PWW_Fusion') else ''
        fac = '\n    <requiredResearchFacilities><li>MultiAnalyzer</li></requiredResearchFacilities>' if dn in ('PWW_Nuclear','PWW_Fusion') else ''
        out.append(f"""
  <ResearchProjectDef>
    <defName>{dn}</defName>
    <label>{E(label)}</label>
    <description>{E(desc)}</description>
    <baseCost>{cost}</baseCost>
    <techLevel>{tech}</techLevel>
    <prerequisites>{li(pre)}</prerequisites>{hb}{fac}
    <tab>PWW_ResearchTab</tab>
    <researchViewX>{x}</researchViewX>
    <researchViewY>{y}</researchViewY>
  </ResearchProjectDef>""")
    W.append(('ResearchTabDef', 'PWW_ResearchTab', 'Elektřina a voda', None))
    return """
  <ResearchTabDef>
    <defName>PWW_ResearchTab</defName>
    <label>power &amp; water</label>
  </ResearchTabDef>""" + "".join(out)

ITEMS = f"""
  <!-- FINAL texture: Things/Item/PowerWaterWorks/FuelRod -->
  <ThingDef ParentName="ResourceBase">
    <defName>PWW_FuelRod</defName>
    <label>fuel rod</label>
    <description>A sealed rod of enriched uranium. Fuel for the fission reactor; one lasts about a day.</description>
    <graphicData>
{item_tex('FuelRod','Things/Item/Resource/Wastepack','Graphic_StackCount','(0.55,1.0,0.5)')}
    </graphicData>
    <stackLimit>10</stackLimit>
    <statBases>
      <MarketValue>380</MarketValue>
      <Mass>3</Mass>
      <MaxHitPoints>200</MaxHitPoints>
      <Flammability>0</Flammability>
    </statBases>
    <thingCategories><li>Manufactured</li></thingCategories>
    <tradeability>Sellable</tradeability>
    <techLevel>Industrial</techLevel>
  </ThingDef>
  <!-- FINAL texture: Things/Item/PowerWaterWorks/FusionCell -->
  <ThingDef ParentName="ResourceBase">
    <defName>PWW_FusionCell</defName>
    <label>fusion cell</label>
    <description>A magnetic fuel cell for the fusion reactor. One lasts about five days.</description>
    <graphicData>
{item_tex('FusionCell','Things/Item/Resource/SignalChip','Graphic_Single','(0.5,0.9,1.0)')}
    </graphicData>
    <stackLimit>5</stackLimit>
    <statBases>
      <MarketValue>1400</MarketValue>
      <Mass>2</Mass>
      <MaxHitPoints>200</MaxHitPoints>
      <Flammability>0</Flammability>
    </statBases>
    <thingCategories><li>Manufactured</li></thingCategories>
    <tradeability>Sellable</tradeability>
    <techLevel>Spacer</techLevel>
  </ThingDef>
  <!-- FINAL texture: Things/Item/PowerWaterWorks/CannedFood (Graphic_StackCount) -->
  <ThingDef ParentName="MealBase">
    <defName>PWW_CannedFood</defName>
    <label>canned food</label>
    <description>Raw food cooked and sealed in a tin. It never rots and keeps well even outside.</description>
    <graphicData>
{item_tex('CannedFood','Things/Item/Meal/SurvivalPack','Graphic_MealVariants','(0.9,0.85,0.75)')}
    </graphicData>
    <statBases>
      <DeteriorationRate>0.2</DeteriorationRate>
      <MarketValue>20</MarketValue>
      <Mass>0.35</Mass>
      <WorkToMake>400</WorkToMake>
      <Nutrition>0.9</Nutrition>
    </statBases>
    <ingestible>
      <preferability>MealSimple</preferability>
      <optimalityOffsetHumanlikes>-3</optimalityOffsetHumanlikes>
      <optimalityOffsetFeedingAnimals>-100</optimalityOffsetFeedingAnimals>
      <ingestEffect>EatVegetarian</ingestEffect>
      <ingestSound>Meal_Eat</ingestSound>
    </ingestible>
  </ThingDef>"""
W += [('ThingDef','PWW_FuelRod','palivová tyč','Zapečetěná tyč z obohaceného uranu. Palivo pro jaderný reaktor; jedna vydrží asi den.'),
      ('ThingDef','PWW_FusionCell','fúzní článek','Magnetický palivový článek pro fúzní reaktor. Jeden vydrží asi pět dní.'),
      ('ThingDef','PWW_CannedFood','konzerva','Syrové jídlo uvařené a zatavené v plechovce. Nikdy se nezkazí a dobře vydrží i venku.')]
TEX += [('Things/Item/PowerWaterWorks/FuelRod','item','no','Things/Item/Resource/Wastepack','64x64 (_a/_b/_c stack variants or one 64x64 with Graphic_Single)'),
        ('Things/Item/PowerWaterWorks/FusionCell','item','no','Things/Item/Resource/SignalChip','64x64'),
        ('Things/Item/PowerWaterWorks/CannedFood','item','no','Things/Item/Meal/SurvivalPack','64x64')]

def recipe(dn, label, cz, job, czjob, users, ingr, prods, work, research, skill=None, extra='', effect='Smith', sound='Recipe_Smith', stat='GeneralLaborSpeed', wskill='Crafting', filt=''):
    W.append(('RecipeDef', dn, cz, None, czjob))
    ing = "".join(f"<li><filter>{f}</filter><count>{c}</count></li>" for f, c in ingr)
    pr = "".join(f"<{k}>{v}</{k}>" for k, v in prods.items())
    sk = f"<skillRequirements><{wskill}>{skill}</{wskill}></skillRequirements>" if skill else ''
    return f"""
  <RecipeDef>
    <defName>{dn}</defName>
    <label>{E(label)}</label>
    <description>{E(label)}.</description>
    <jobString>{E(job)}</jobString>
    <workAmount>{work}</workAmount>
    <workSpeedStat>{stat}</workSpeedStat>
    <workSkill>{wskill}</workSkill>
    <effectWorking>{effect}</effectWorking>
    <soundWorking>{sound}</soundWorking>
    <recipeUsers>{li(users)}</recipeUsers>
    <ingredients>{ing}</ingredients>
    <fixedIngredientFilter>{filt}</fixedIngredientFilter>
    <products>{pr}</products>
    <researchPrerequisite>{research}</researchPrerequisite>
    {sk}{extra}
  </RecipeDef>"""

def td(*n): return "<thingDefs>" + li(n) + "</thingDefs>"
RECIPES = "".join([
 recipe('PWW_SteelFromSlag','extract steel from slag','získat ocel ze strusky','Extracting steel from slag.','Získává ocel ze strusky.',['PWW_InductionFurnace'],[(td('ChunkSlagSteel'),1)],{'Steel':20},700,'PWW_Industry',filt=td('ChunkSlagSteel'),effect='Smelt',sound='Recipe_Smelt'),
 recipe('PWW_MakeFuelRod','make fuel rod','vyrobit palivovou tyč','Making a fuel rod.','Vyrábí palivovou tyč.',['PWW_InductionFurnace'],[(td('Uranium'),25),(td('Steel'),20)],{'PWW_FuelRod':1},3500,'PWW_Nuclear',skill=8,filt=td('Uranium','Steel')),
 recipe('PWW_MakeFusionCell','make fusion cell','vyrobit fúzní článek','Making a fusion cell.','Vyrábí fúzní článek.',['PWW_InductionFurnace'],[(td('Uranium'),20),(td('Plasteel'),20),(td('ComponentSpacer'),2),(td('Chemfuel'),50)],{'PWW_FusionCell':1},8000,'PWW_Fusion',skill=12,filt=td('Uranium','Plasteel','ComponentSpacer','Chemfuel')),
 recipe('PWW_MakeComponents5','make 5 components','vyrobit 5 součástek','Making components.','Vyrábí součástky.',['PWW_ComponentLine'],[(td('Steel'),55)],{'ComponentIndustrial':5},18000,'PWW_Industry',skill=8,filt=td('Steel'),sound='Recipe_Machining'),
 recipe('PWW_RecycleApparel','recycle apparel into cloth','rozmělnit oblečení na látku','Recycling apparel.','Rozmělňuje oblečení.',['PWW_TextileMill'],[('<categories><li>Apparel</li></categories>',1)],{},800,'PWW_Industry',filt='<categories><li>Apparel</li></categories>',effect='Tailor',sound='Recipe_Tailor',wskill='Crafting'),
 recipe('PWW_CanFood','can food','konzervovat jídlo','Canning food.','Konzervuje jídlo.',['PWW_Cannery'],[('<categories><li>FoodRaw</li></categories>',0.6)],{'PWW_CannedFood':1},500,'PWW_Industry',filt='<categories><li>FoodRaw</li></categories>',effect='Cook',sound='Recipe_CookMeal',stat='CookSpeed',wskill='Cooking',
        extra='<ingredientValueGetterClass>IngredientValueGetter_Nutrition</ingredientValueGetterClass>'),
])

# ---------------- DBH water buildings (loaded only with Dubs Bad Hygiene) ----------------
def dbh(dn, parent, label, cz, desc, czd, ph, cls, ds, size, cost, comps, power=None, pw=(), extra=''):
    W.append(('ThingDef', dn, cz, czd))
    TEX.append((f"Things/Building/PowerWaterWorks/{dn[4:]}", f"{size[0]}x{size[1]}", 'no', ph, f"{size[0]*128}x{size[1]*128}"))
    pwr = f"""
      <li Class="CompProperties_Power"><compClass>CompPowerTrader</compClass><basePowerConsumption>{power}</basePowerConsumption></li>
      <li Class="CompProperties_Flickable"/>
      <li Class="CompProperties_Breakdownable"/>""" if power else ''
    cost_s = "".join(f"<{k}>{v}</{k}>" for k, v in cost.items())
    return f"""
  <!-- FINAL texture: Things/Building/PowerWaterWorks/{dn[4:]} ({size[0]}x{size[1]}) – placeholder: DBH texture -->
  <ThingDef ParentName="{parent}">
    <defName>{dn}</defName>
    <label>{E(label)}</label>
    <description>{E(desc)}</description>
    <graphicData>
{dbh_tex(dn, ph, cls, ds, size)}
    </graphicData>
    <size>({size[0]},{size[1]})</size>
    <costList>{cost_s}</costList>
    <statBases>
      <MaxHitPoints>250</MaxHitPoints>
      <WorkToBuild>8000</WorkToBuild>
      <Flammability>0.2</Flammability>
    </statBases>
    <researchPrerequisites><li>PWW_WaterWorks</li></researchPrerequisites>
    <placeWorkers>{li(pw)}</placeWorkers>{extra}
    <comps>
      <li Class="DubsBadHygiene.CompProperties_Pipe"><mode>Sewage</mode></li>{pwr}{comps}
    </comps>
  </ThingDef>"""

DBH = "".join([
 dbh('PWW_DeepBorehole','BasedWaterWell','deep borehole','hluboký vrt',
     'A borehole drilled deep below the water table. Reaches a very large area of clean ground water (radius 22) that pumps can use; unaffected by pollution.',
     'Vrt hluboko pod hladinu podzemní vody. Dosáhne na velmi velkou oblast čisté podzemní vody (poloměr 22), kterou mohou čerpat čerpadla; znečištění ho neovlivní.',
     'DBH/Things/Building/Water/DeepWaterWell','Graphic_Single','(1,1)',(1,1),{'Steel':250,'ComponentIndustrial':5},
     '\n      <li Class="DubsBadHygiene.CompProperties_WaterInlet"><Deep>true</Deep><Radius>21.9</Radius></li>',
     pw=['DubsBadHygiene.PlaceWorker_DeepWaterGrid','DubsBadHygiene.PlaceWorker_SewageGrid','DubsBadHygiene.PlaceWorker_UserGrid']),
 dbh('PWW_DeepWellPump','BasedWaterPump','high-capacity pump','vysokokapacitní čerpadlo',
     'Pumps water from wells to water towers: 30,000 L per day.','Čerpá vodu ze studní do vodojemů: 30 000 l za den.',
     'DBH/Things/Building/Water/waterPumpingStation','Graphic_Single','(3,3)',(3,3),{'Steel':300,'ComponentIndustrial':8},
     '\n      <li Class="DubsBadHygiene.CompProperties_WaterPumpingStation"><Capacity>30000</Capacity></li>', power=2000),
 dbh('PWW_WaterTreatment','BasedWaterPump','compact water treatment','kompaktní úpravna vody',
     'Cleans the water in towers and the water used by fixtures, like the standard treatment plant, but needs only half the power.',
     'Čistí vodu ve vodojemech i vodu používanou v koupelnách, stejně jako běžná úpravna, ale potřebuje jen polovinu proudu.',
     'DBH/Things/Building/Water/waterFiltration','Graphic_Single','(2,2)',(2,2),{'Steel':200,'ComponentIndustrial':4,'Silver':50},
     '\n      <li Class="DubsBadHygiene.CompProperties_WaterFiltration"/>', power=400),
 dbh('PWW_Desalination','BasedWaterTower','desalination plant','odsolovací stanice',
     'Makes fresh water from sea water and fills its own tank (about 15,000 L per day while powered). Must stand within 3 cells of the sea.',
     'Vyrábí sladkou vodu z mořské a plní si vlastní nádrž (asi 15 000 l za den, když má proud). Musí stát do 3 políček od moře.',
     'DBH/Things/Building/Spacer/WRS','Graphic_Multi','(3,3)',(3,3),{'Steel':350,'ComponentIndustrial':6},
     '\n      <li Class="DubsBadHygiene.CompProperties_WaterStorage"><WaterStorageCap>20000</WaterStorageCap><Auto>true</Auto><AutoGenRate>15</AutoGenRate></li>', power=1500,
     pw=['PowerWaterWorks.PlaceWorker_NearSaltWater']),
 dbh('PWW_WaterTower','BasedWaterTower','giant water tower','obří vodojem',
     'Stores 150,000 L of water. When the power fails, it still keeps the taps running for a long time.',
     'Pojme 150 000 l vody. Při výpadku proudu ještě dlouho drží vodu v kohoutcích.',
     'DBH/Things/Building/Water/WaterTower','Graphic_Single','(4,5)',(4,4),{'Steel':400,'ComponentIndustrial':2},
     '\n      <li Class="DubsBadHygiene.CompProperties_WaterStorage"><WaterStorageCap>150000</WaterStorageCap></li>'),
])

HDR = '<?xml version="1.0" encoding="utf-8"?>\n<Defs>\n'
os.makedirs('Defs', exist_ok=True); os.makedirs('DBH/Defs', exist_ok=True)
open('Defs/PWW_Research.xml','w',encoding='utf-8').write(HDR + research_xml() + '\n</Defs>\n')
open('Defs/PWW_Buildings.xml','w',encoding='utf-8').write(HDR + "".join(building(b) for b in B) + '\n</Defs>\n')
open('Defs/PWW_Items.xml','w',encoding='utf-8').write(HDR + ITEMS + '\n</Defs>\n')
open('Defs/PWW_Recipes.xml','w',encoding='utf-8').write(HDR + RECIPES + '\n</Defs>\n')
open('DBH/Defs/PWW_Water.xml','w',encoding='utf-8').write(HDR + DBH + '\n</Defs>\n')

# Czech DefInjected
by = {}
for w in W:
    by.setdefault(w[0], []).append(w)
for t, ws in by.items():
    os.makedirs(f'Languages/Czech/DefInjected/{t}', exist_ok=True)
    lines = []
    for w in ws:
        lines.append(f"  <{w[1]}.label>{E(w[2])}</{w[1]}.label>")
        if t == 'RecipeDef':
            lines.append(f"  <{w[1]}.description>{E(w[2])}.</{w[1]}.description>")
            lines.append(f"  <{w[1]}.jobString>{E(w[4])}</{w[1]}.jobString>")
        elif w[3]:
            lines.append(f"  <{w[1]}.description>{E(w[3])}</{w[1]}.description>")
    open(f'Languages/Czech/DefInjected/{t}/PWW.xml','w',encoding='utf-8').write('<?xml version="1.0" encoding="utf-8"?>\n<LanguageData>\n' + "\n".join(lines) + '\n</LanguageData>\n')

with open('Textures_TODO.txt','w',encoding='utf-8') as f:
    f.write("Power & Water Works – textures to make (Grok). Top-down, slightly angled RimWorld style, transparent background, PNG.\n")
    f.write("128 px per cell. Rotatable buildings need _north, _east, _south (west = mirrored east).\n")
    f.write("Put the files under Textures/ at the FINAL path, then change <texPath> (and <graphicClass>, remove <color>) in the Defs.\n\n")
    f.write("FINAL texPath | size (cells) | rotatable | placeholder now | pixels\n")
    for row in TEX:
        f.write(" | ".join(row) + "\n")
print("ok", len(B), "buildings,", len(W), "translated defs")
