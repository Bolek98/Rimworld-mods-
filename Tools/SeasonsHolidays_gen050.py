#!/usr/bin/env python3
# Seasons & Holidays 0.5.0: generates Defs/Feast_SeasonsHolidays.xml and its Czech DefInjected files.
# Run from anywhere: python3 Tools/SeasonsHolidays_gen050.py
import pathlib
from xml.sax.saxutils import escape as e

R = pathlib.Path(__file__).resolve().parent.parent / "SeasonsHolidays"
defs = []                      # xml chunks
inj = {}                       # DefType -> list of lines


def cz(dtype, key, text):
    inj.setdefault(dtype, []).append(f"  <{key}>{e(text)}</{key}>")


# ---------------------------------------------------------------- thoughts
TRAD = [("", ""), (" (tradition)", " (tradice)"), (" (our tradition)", " (naše tradice)"), (" (old tradition)", " (stará tradice)")]


def holiday_thought(hid, en, cs, mood, days, den, dcs):
    s = [f"  <ThoughtDef>\n    <defName>SH_H_{hid}</defName>\n    <durationDays>{days}</durationDays>\n    <stackLimit>1</stackLimit>\n    <stages>"]
    for i, (te, tc) in enumerate(TRAD):
        s.append(f"      <li>\n        <label>{e(en + te)}</label>\n        <description>{e(den)}</description>\n        <baseMoodEffect>{mood + i}</baseMoodEffect>\n      </li>")
        cz("ThoughtDef", f"SH_H_{hid}.stages.{i}.label", cs + tc)
        cz("ThoughtDef", f"SH_H_{hid}.stages.{i}.description", dcs)
    s.append("    </stages>\n  </ThoughtDef>\n")
    defs.append("\n".join(s))


holiday_thought("SpringEquinox", "spring equinox", "jarní rovnodennost", 5, 2,
                "Day and night are equal. We blessed the fields for a good year.", "Den a noc jsou stejně dlouhé. Požehnali jsme polím pro dobrý rok.")
holiday_thought("WinterSolstice", "yule", "zimní slunovrat", 6, 3,
                "The longest night is behind us. From now on the sun comes back.", "Nejdelší noc je za námi. Od teď se slunce vrací.")


def thought(name, stages, days=1, stack=1, cls=None, nullify=None):
    """stages: list of (labelEN, labelCS, descEN, descCS, mood)"""
    s = [f"  <ThoughtDef>\n    <defName>{name}</defName>"]
    if cls:
        s.append(f"    <thoughtClass>{cls}</thoughtClass>")
    s.append(f"    <durationDays>{days}</durationDays>\n    <stackLimit>{stack}</stackLimit>")
    if stack > 1:
        s.append("    <stackedEffectMultiplier>0.6</stackedEffectMultiplier>")
    if nullify:
        s.append("    <nullifyingTraits>" + "".join(f"\n      <li>{t}</li>" for t in nullify) + "\n    </nullifyingTraits>")
    s.append("    <stages>")
    for i, (le, lc, de, dc, mood) in enumerate(stages):
        s.append(f"      <li>\n        <label>{e(le)}</label>\n        <description>{e(de)}</description>\n        <baseMoodEffect>{mood}</baseMoodEffect>\n      </li>")
        cz("ThoughtDef", f"{name}.stages.{i}.label", lc)
        cz("ThoughtDef", f"{name}.stages.{i}.description", dc)
    s.append("    </stages>\n  </ThoughtDef>\n")
    defs.append("\n".join(s))


thought("SH_RedEnvelope", [("red envelope", "červená obálka", "A red envelope with a little silver – for luck in the new year.", "Červená obálka s trochou stříbra – pro štěstí v novém roce.", 4)], 2)
thought("SH_HoliColours", [("colour fight", "bitva barev", "Everyone threw colours at everyone. I'm pink, green and blue – and laughing.", "Všichni házeli barvy po všech. Jsem růžový, zelený a modrý – a směju se.", 6)], 2)
thought("SH_DiwaliLights", [("festival of lights", "svátek světel", "Little lamps everywhere and sweets for everybody. Light wins over darkness.", "Všude lampičky a sladkosti pro všechny. Světlo vítězí nad tmou.", 5)], 2)
thought("SH_MoonWatching", [("watched the moon", "pozorovali jsme měsíc", "Mooncakes under the full moon with everyone together. A quiet, happy evening.", "Měsíční koláčky pod úplňkem, všichni pohromadě. Tichý, šťastný večer.", 5)], 2)
thought("SH_RememberedDead", [("remembered {0}", "vzpomínka na {0}", "We put a photo, marigolds and their favourite food on the altar. It felt like they were with us.", "Na oltář jsme dali fotku, aksamitníky a jejich oblíbené jídlo. Bylo to, jako by byli s námi.", 4)], 3)
thought("SH_DayOfDeadAltar", [("altar of the dead", "oltář mrtvých", "Marigolds, candles and sugar skulls. A colourful way to remember.", "Aksamitníky, svíčky a cukrové lebky. Barevný způsob, jak vzpomínat.", 3)], 2)
thought("SH_FoundersSpeech", [
    ("founders' day speech (dull)", "proslov ke Dni založení (nudný)", "The speech was a bit long. But the feast afterwards was good.", "Proslov byl trochu dlouhý. Ale hostina potom byla dobrá.", 3),
    ("founders' day speech", "proslov ke Dni založení", "A good speech about how it all began. Look how far we've come!", "Dobrý proslov o tom, jak to všechno začalo. Kam jsme to dotáhli!", 6),
    ("founders' day speech (moving)", "proslov ke Dni založení (dojemný)", "What a speech! I had tears in my eyes. I'm proud of this colony.", "To byl proslov! Měl jsem slzy v očích. Jsem na tuhle kolonii hrdý.", 9)], 3)
thought("SH_GreenParty", [("green party", "zelená oslava", "Everything green, songs and dancing. Luck of the Irish!", "Všechno zelené, písničky a tanec. Irské štěstí!", 5)], 2)
thought("SH_BeerChampion", [("beer contest champion", "vítěz pivní soutěže", "I won the Oktoberfest beer contest! Nobody drinks a mug faster than me.", "Vyhrál jsem oktoberfestovou pivní soutěž! Nikdo nevypije džbánek rychleji.", 8)], 3)
thought("SH_BeerContest", [("beer contest", "pivní soutěž", "A beer contest with brass music. I lost, but what a laugh!", "Pivní soutěž s dechovkou. Prohrál jsem, ale to bylo smíchu!", 4)], 2)
thought("SH_MartinGoose", [("st. martin's goose", "svatomartinská husa", "A lantern walk in the dark and then roast goose. A proper St. Martin's.", "Průvod s lampiony za tmy a pak pečená husa. Pořádný Martin.", 6)], 2)
thought("SH_MartinLanterns", [("lantern walk", "lampionový průvod", "We walked with paper lanterns in the dark. No goose this year, but it was lovely.", "Šli jsme za tmy s lampiony. Letos bez husy, ale bylo to krásné.", 3)], 2)
thought("SH_EquinoxBlessing", [("blessed the fields", "požehnání polím", "We danced around the fields and asked for a good harvest.", "Tančili jsme kolem polí a prosili o dobrou úrodu.", 4)], 2)
thought("SH_YuleFire", [("yule fire", "slunovratový oheň", "We kept the big fire burning through the longest night. The sun will come back.", "Celou nejdelší noc jsme udržovali velký oheň. Slunce se vrátí.", 6)], 2)
thought("SH_SeasonalTreat", [("holiday treat", "sváteční dobrota", "It tastes even better in its season.", "Ve svou sezónu chutná ještě líp.", 3)], 1, 3)
thought("SH_TreatTaste", [("holiday treat", "sváteční pochoutka", "A little holiday treat. Sweet!", "Malá sváteční pochoutka. Sladké!", 2)], 1, 3)
thought("SH_LookingForward", [("looking forward to {0}", "těším se na {0}", "Last year it was great. I can't wait!", "Loni to bylo skvělé. Už se nemůžu dočkat!", 3)], 3, 5, "SeasonsHolidays.Thought_SHLookingForward")
thought("SH_FamilyTogether", [
    ("celebrated with family", "slavili jsme s rodinou", "We celebrated together, the whole family.", "Slavili jsme spolu, celá rodina.", 2),
    ("our family tradition", "naše rodinná tradice", "Every year together – that is our family tradition.", "Každý rok spolu – to je naše rodinná tradice.", 3),
    ("old family tradition", "stará rodinná tradice", "Year after year together. I wouldn't change it for anything.", "Rok co rok spolu. Za nic bych to neměnil.", 4)], 2, 1, None, ["Psychopath"])
thought("SH_CozyEvening", [("cozy evening by the fire", "útulný večer u ohně", "Cold outside, warm by the fire. Just right.", "Venku zima, u ohně teplo. Tak akorát.", 3)], 0.5)
thought("SH_Mosquitoes", [("mosquitoes", "komáři", "Bzzz… These insects are eating me alive.", "Bzzz… Ti komáři mě sežerou zaživa.", -2)], 0.25)

# ---------------------------------------------------------------- hediffs, condition, trader
defs.append("""  <HediffDef>
    <defName>SH_Colourful</defName>
    <label>covered in colours</label>
    <description>Covered from head to toe in Holi colours. It washes off in a day.</description>
    <hediffClass>HediffWithComps</hediffClass>
    <isBad>false</isBad>
    <defaultLabelColor>(0.95, 0.5, 0.85)</defaultLabelColor>
    <comps>
      <li Class="HediffCompProperties_Disappears">
        <disappearsAfterTicks>30000</disappearsAfterTicks>
        <showRemainingTime>true</showRemainingTime>
      </li>
    </comps>
  </HediffDef>

  <HediffDef>
    <defName>SH_Muddy</defName>
    <label>muddy</label>
    <description>Wading through the mud of the spring thaw. Walks a bit slower.</description>
    <hediffClass>HediffWithComps</hediffClass>
    <isBad>true</isBad>
    <defaultLabelColor>(0.6, 0.45, 0.3)</defaultLabelColor>
    <comps>
      <li Class="HediffCompProperties_Disappears">
        <disappearsAfterTicks>2500</disappearsAfterTicks>
      </li>
    </comps>
    <stages>
      <li>
        <capMods>
          <li>
            <capacity>Moving</capacity>
            <offset>-0.1</offset>
          </li>
        </capMods>
      </li>
    </stages>
  </HediffDef>

  <GameConditionDef>
    <defName>SH_SpringThaw</defName>
    <conditionClass>GameCondition</conditionClass>
    <label>spring thaw</label>
    <description>The snow is melting and the ground by the water is soft mud. Colonists walk slower near water.</description>
    <canBePermanent>false</canBePermanent>
  </GameConditionDef>

  <TraderKindDef>
    <defName>SH_HarvestMarket</defName>
    <label>harvest market</label>
    <commonality>0</commonality>
    <requestable>false</requestable>
    <stockGenerators>
      <li Class="StockGenerator_SingleDef">
        <thingDef>Silver</thingDef>
        <countRange>400~900</countRange>
      </li>
      <li Class="StockGenerator_SingleDef">
        <thingDef>RawPotatoes</thingDef>
        <countRange>150~300</countRange>
      </li>
      <li Class="StockGenerator_SingleDef">
        <thingDef>RawRice</thingDef>
        <countRange>150~300</countRange>
      </li>
      <li Class="StockGenerator_SingleDef">
        <thingDef>RawCorn</thingDef>
        <countRange>150~300</countRange>
      </li>
      <li Class="StockGenerator_SingleDef">
        <thingDef>RawBerries</thingDef>
        <countRange>60~150</countRange>
      </li>
      <li Class="StockGenerator_SingleDef">
        <thingDef>Pemmican</thingDef>
        <countRange>80~200</countRange>
      </li>
      <li Class="StockGenerator_SingleDef">
        <thingDef>Hay</thingDef>
        <countRange>200~400</countRange>
      </li>
      <li Class="StockGenerator_SingleDef">
        <thingDef>SH_PumpkinPie</thingDef>
        <countRange>4~10</countRange>
      </li>
      <li Class="StockGenerator_SingleDef">
        <thingDef>Beer</thingDef>
        <countRange>15~40</countRange>
      </li>
      <li Class="StockGenerator_BuyExpensiveSimple" />
    </stockGenerators>
  </TraderKindDef>
""")
cz("HediffDef", "SH_Colourful.label", "celý v barvách")
cz("HediffDef", "SH_Colourful.description", "Od hlavy k patě v barvách z Holi. Do druhého dne se to smyje.")
cz("HediffDef", "SH_Muddy.label", "zablácený")
cz("HediffDef", "SH_Muddy.description", "Brodí se blátem jarního tání. Chodí trochu pomaleji.")
cz("GameConditionDef", "SH_SpringThaw.label", "jarní tání")
cz("GameConditionDef", "SH_SpringThaw.description", "Sníh taje a půda u vody je měkké bláto. Kolonisté u vody chodí pomaleji.")
cz("TraderKindDef", "SH_HarvestMarket.label", "dožínkový trh")

# ---------------------------------------------------------------- food and drinks
SEASON = {"easter": [2, 3], "xmas": [11, 0], "autumn": [9, 10], "newyear": [11, 0], "mid": [8, 9], "diwali": [9, 10]}


def season(key):
    return "      <li Class=\"SeasonsHolidays.CompProperties_SHSeasonalFood\">\n        <months>" + "".join(f"<li>{m}</li>" for m in SEASON[key]) + "</months>\n      </li>"


def meal(name, en, cs, den, dcs, tex, value, nutr, rot, sea, pref="MealFine", taste="AteFineMeal", joy=0.2):
    defs.append(f"""  <ThingDef ParentName="MealBaseIngredientless">
    <defName>{name}</defName>
    <label>{e(en)}</label>
    <description>{e(den)}</description>
    <graphicData>
      <texPath>Things/SH/{tex}</texPath>
      <graphicClass>Graphic_Single</graphicClass>
      <drawSize>0.85</drawSize>
    </graphicData>
    <tradeability>Sellable</tradeability>
    <statBases>
      <MarketValue>{value}</MarketValue>
      <Nutrition>{nutr}</Nutrition>
    </statBases>
    <comps>
      <li Class="CompProperties_Rottable">
        <daysToRotStart>{rot}</daysToRotStart>
        <rotDestroys>true</rotDestroys>
      </li>
{season(sea)}
    </comps>
    <ingestible>
      <preferability>{pref}</preferability>
      <tasteThought>{taste}</tasteThought>
      <joy>{joy}</joy>
      <joyKind>Gluttonous</joyKind>
      <ingestSound>Meal_Eat</ingestSound>
      <optimalityOffsetFeedingAnimals>-75</optimalityOffsetFeedingAnimals>
    </ingestible>
  </ThingDef>
""")
    cz("ThingDef", f"{name}.label", cs)
    cz("ThingDef", f"{name}.description", dcs)


def snack(name, en, cs, den, dcs, tex, value, sea, fluid=False, stack=25, sound="Meal_Eat"):
    food = "Processed, Fluid" if fluid else "Processed"
    defs.append(f"""  <ThingDef ParentName="ResourceBase">
    <defName>{name}</defName>
    <label>{e(en)}</label>
    <description>{e(den)}</description>
    <graphicData>
      <texPath>Things/SH/{tex}</texPath>
      <graphicClass>Graphic_Single</graphicClass>
      <drawSize>0.8</drawSize>
    </graphicData>
    <stackLimit>{stack}</stackLimit>
    <socialPropernessMatters>true</socialPropernessMatters>
    <statBases>
      <MarketValue>{value}</MarketValue>
      <Mass>0.1</Mass>
      <Nutrition>0.05</Nutrition>
      <DeteriorationRate>2</DeteriorationRate>
    </statBases>
    <thingCategories>
      <li>Foods</li>
    </thingCategories>
    <comps>
      <li Class="CompProperties_Rottable">
        <daysToRotStart>20</daysToRotStart>
        <rotDestroys>true</rotDestroys>
      </li>
{season(sea)}
    </comps>
    <ingestible>
      <foodType>{food}</foodType>
      <preferability>DesperateOnly</preferability>
      <joy>0.12</joy>
      <joyKind>Gluttonous</joyKind>
      <tasteThought>SH_TreatTaste</tasteThought>
      <ingestSound>{sound}</ingestSound>
    </ingestible>
  </ThingDef>
""")
    cz("ThingDef", f"{name}.label", cs)
    cz("ThingDef", f"{name}.description", dcs)


meal("SH_PumpkinPie", "pumpkin pie", "dýňový koláč", "A sweet spiced pie for Thanksgiving and Halloween. Made from pumpkins when you have them, otherwise from corn and berries.",
     "Sladký kořeněný koláč na Díkůvzdání a Halloween. Z dýní, když je máš, jinak z kukuřice a bobulí.", "PumpkinPie", 20, 0.6, 8, "autumn")
meal("SH_Mooncake", "mooncake", "měsíční koláček", "A round pastry with a sweet filling, eaten under the full moon of the Mid-Autumn Festival.",
     "Kulaté pečivo se sladkou náplní, které se jí pod úplňkem o Svátku středu podzimu.", "Mooncake", 10, 0.3, 15, "mid", "MealSimple", "AteFineMeal", 0.15)
snack("SH_DiwaliSweets", "Diwali sweets", "diválí sladkosti", "Little milk and sugar sweets for the festival of lights.",
      "Malé sladkosti z mléka a cukru na svátek světel.", "Sweets", 3, "diwali")
snack("SH_MulledWine", "mulled wine", "svařák", "Hot wine with berries and spices. Warms you up on a frosty Christmas market evening.",
      "Horké víno s bobulemi a kořením. Zahřeje za mrazivého večera na vánočních trzích.", "MulledWine", 6, "xmas", True, 25, "Ingest_Beer")
snack("SH_NewYearPunch", "New Year's punch", "silvestrovský punč", "A big bowl of fruity punch for the New Year's Eve party.",
      "Velká mísa ovocného punče na silvestrovskou oslavu.", "Punch", 5, "newyear", True, 25, "Ingest_Beer")

# ---------------------------------------------------------------- recipes
def recipe(name, en, cs, job_en, job_cs, ingredients, products, users, work, nutrition=False, skill=0, desc_en="", desc_cs=""):
    ing = []
    fixed = []
    for filt, count in ingredients:
        kind, vals = filt
        inner = "".join(f"<li>{v}</li>" for v in vals)
        tag = "thingDefs" if kind == "defs" else "categories"
        ing.append(f"      <li>\n        <filter><{tag}>{inner}</{tag}></filter>\n        <count>{count}</count>\n      </li>")
        fixed.append(f"<{tag}>{inner}</{tag}>")
    getter = "\n    <ingredientValueGetterClass>IngredientValueGetter_Nutrition</ingredientValueGetterClass>" if nutrition else ""
    sk = f"\n    <skillRequirements><Cooking>{skill}</Cooking></skillRequirements>" if skill else ""
    defs.append(f"""  <RecipeDef>
    <defName>{name}</defName>
    <label>{e(en)}</label>
    <description>{e(desc_en or en.capitalize() + ".")}</description>
    <jobString>{e(job_en)}</jobString>
    <workSpeedStat>CookSpeed</workSpeedStat>
    <effectWorking>Cook</effectWorking>
    <soundWorking>Recipe_CookMeal</soundWorking>
    <workSkill>Cooking</workSkill>
    <workAmount>{work}</workAmount>
    <allowMixingIngredients>true</allowMixingIngredients>{getter}{sk}
    <ingredients>
{chr(10).join(ing)}
    </ingredients>
    <fixedIngredientFilter>{"".join(fixed)}</fixedIngredientFilter>
    <products>{"".join(f"<{k}>{v}</{k}>" for k, v in products)}</products>
    <recipeUsers>{"".join(f"<li>{u}</li>" for u in users)}</recipeUsers>
  </RecipeDef>
""")
    cz("RecipeDef", f"{name}.label", cs)
    cz("RecipeDef", f"{name}.description", desc_cs or cs.capitalize() + ".")
    cz("RecipeDef", f"{name}.jobString", job_cs)


STOVES = ["FueledStove", "ElectricStove"]
recipe("SH_DyeEggs", "dye Easter eggs", "obarvit velikonoční vajíčka", "Dyeing Easter eggs.", "Barví velikonoční vajíčka.",
       [(("cats", ["EggsUnfertilized"]), 4)], [("SH_EasterEgg", 4)], STOVES + ["Campfire"], 300,
       desc_en="Boil and paint 4 eggs for Easter.", desc_cs="Uvařit a pomalovat 4 vajíčka na Velikonoce.")
recipe("SH_BakeCookies", "bake Christmas cookies", "upéct vánoční cukroví", "Baking Christmas cookies.", "Peče vánoční cukroví.",
       [(("defs", ["RawCorn", "RawRice", "RawBerries"]), 0.8)], [("SH_ChristmasCookies", 2)], STOVES, 900, True, 4,
       "Bake 2 boxes of Christmas cookies.", "Upéct 2 krabice vánočního cukroví.")
recipe("SH_BakePumpkinPie", "bake pumpkin pie", "upéct dýňový koláč", "Baking a pumpkin pie.", "Peče dýňový koláč.",
       [(("defs", ["RawCorn", "RawBerries"]), 0.5)], [("SH_PumpkinPie", 1)], STOVES, 700, True, 3,
       "Bake a pumpkin pie (from pumpkins when a mod adds them, otherwise from corn and berries).", "Upéct dýňový koláč (z dýní, když je přidá nějaký mod, jinak z kukuřice a bobulí).")
recipe("SH_BakeMooncakes", "bake mooncakes", "upéct měsíční koláčky", "Baking mooncakes.", "Peče měsíční koláčky.",
       [(("defs", ["RawRice", "RawCorn", "RawBerries"]), 0.6)], [("SH_Mooncake", 2)], STOVES, 700, True, 3,
       "Bake 2 mooncakes.", "Upéct 2 měsíční koláčky.")
recipe("SH_MakeDiwaliSweets", "make Diwali sweets", "připravit diválí sladkosti", "Making Diwali sweets.", "Připravuje diválí sladkosti.",
       [(("defs", ["Milk"]), 5), (("defs", ["RawRice", "RawBerries"]), 5)], [("SH_DiwaliSweets", 6)], STOVES, 500,
       desc_en="Make 6 sweets from milk and rice or berries.", desc_cs="Připravit 6 sladkostí z mléka a rýže nebo bobulí.")
recipe("SH_MakeMulledWine", "make mulled wine", "uvařit svařák", "Making mulled wine.", "Vaří svařák.",
       [(("defs", ["Beer"]), 2), (("defs", ["RawBerries"]), 5)], [("SH_MulledWine", 3)], STOVES + ["Campfire"], 400,
       desc_en="Heat 2 beers with berries and spices into 3 mugs of mulled wine.", desc_cs="Ohřát 2 piva s bobulemi a kořením na 3 hrnky svařáku.")
recipe("SH_MakeNewYearPunch", "make New Year's punch", "připravit silvestrovský punč", "Making punch.", "Připravuje punč.",
       [(("defs", ["Beer"]), 1), (("defs", ["RawBerries"]), 10)], [("SH_NewYearPunch", 4)], STOVES + ["Campfire"], 400,
       desc_en="Mix a beer with berries into 4 glasses of punch.", desc_cs="Smíchat pivo s bobulemi na 4 sklenice punče.")

# ---------------------------------------------------------------- decorations
def deco_life(name, en, cs, den, dcs, tex, size, glow, color, life, gather=False, beauty=4, hp=30, flam=1):
    g = f"""      <li Class="CompProperties_Glower">
        <glowRadius>{glow}</glowRadius>
        <glowColor>{color}</glowColor>
      </li>
""" if glow else ""
    gs = "      <li><compClass>CompGatherSpot</compClass></li>\n" if gather else ""
    defs.append(f"""  <ThingDef ParentName="SH_DecoBase">
    <defName>{name}</defName>
    <label>{e(en)}</label>
    <description>{e(den)}</description>
    <graphicData>
      <texPath>Things/SH/{tex}</texPath>
      <graphicClass>Graphic_Single</graphicClass>
      <drawSize>{size}</drawSize>
    </graphicData>
    <passability>Standable</passability>
    <pathCost>10</pathCost>
    <statBases>
      <MaxHitPoints>{hp}</MaxHitPoints>
      <Beauty>{beauty}</Beauty>
      <Flammability>{flam}</Flammability>
    </statBases>
    <comps>
{g}      <li Class="CompProperties_Lifespan">
        <lifespanTicks>{life}</lifespanTicks>
      </li>
{gs}    </comps>
  </ThingDef>
""")
    cz("ThingDef", f"{name}.label", cs)
    cz("ThingDef", f"{name}.description", dcs)


deco_life("SH_RedLantern", "red lantern", "červená lucerna", "A red paper lantern for the Lunar New Year – for luck.", "Červená papírová lucerna na Lunární Nový rok – pro štěstí.",
          "RedLantern", 0.9, 4, "(255,90,60,0)", 120000, True, 6)
deco_life("SH_DiyaLamp", "diya lamp", "lampička diya", "A little clay oil lamp. On Diwali they burn everywhere.", "Malá hliněná olejová lampička. O Diválí hoří všude.",
          "DiyaLamp", 0.6, 3, "(255,190,90,0)", 120000, False, 4, 20, 0)
deco_life("SH_PaperLantern", "paper lantern", "lampion", "A paper lantern on a stick for the St. Martin's lantern walk.", "Papírový lampion na tyčce pro svatomartinský průvod.",
          "PaperLantern", 0.8, 3, "(255,200,120,0)", 60000, False, 4, 20)
deco_life("SH_Ofrenda", "altar of the dead", "oltář mrtvých", "An ofrenda with marigolds, candles, sugar skulls and photos of those we lost.", "Ofrenda s aksamitníky, svíčkami, cukrovými lebkami a fotkami těch, které jsme ztratili.",
          "Ofrenda", 1.4, 4, "(255,170,80,0)", 180000, True, 10, 60)


def deco_build(name, en, cs, den, dcs, tex, size, cost, work, beauty, months, glow=None, gather=False, mass=3, order=2101, offset=None):
    g = f"""      <li Class="CompProperties_Glower">
        <glowRadius>{glow[0]}</glowRadius>
        <glowColor>{glow[1]}</glowColor>
      </li>
""" if glow else ""
    gs = "      <li><compClass>CompGatherSpot</compClass></li>\n" if gather else ""
    off = f"\n      <drawOffset>{offset}</drawOffset>" if offset else ""
    defs.append(f"""  <ThingDef ParentName="BuildingBase">
    <defName>{name}</defName>
    <label>{e(en)}</label>
    <description>{e(den)}</description>
    <graphicData>
      <texPath>Things/SH/{tex}</texPath>
      <graphicClass>Graphic_Single</graphicClass>
      <drawSize>{size}</drawSize>{off}
    </graphicData>
    <altitudeLayer>Building</altitudeLayer>
    <passability>PassThroughOnly</passability>
    <pathCost>30</pathCost>
    <fillPercent>0.2</fillPercent>
    <rotatable>false</rotatable>
    <scatterableOnMapGen>false</scatterableOnMapGen>
    <designationCategory>Misc</designationCategory>
    <uiOrder>{order}</uiOrder>
    <minifiedDef>MinifiedThing</minifiedDef>
    <thingCategories>
      <li>BuildingsFurniture</li>
    </thingCategories>
    <costList>{"".join(f"<{k}>{v}</{k}>" for k, v in cost)}</costList>
    <statBases>
      <MaxHitPoints>60</MaxHitPoints>
      <WorkToBuild>{work}</WorkToBuild>
      <Beauty>{beauty}</Beauty>
      <Mass>{mass}</Mass>
      <Flammability>1</Flammability>
    </statBases>
    <building>
      <ai_chillDestination>true</ai_chillDestination>
      <artificialForMeditationPurposes>false</artificialForMeditationPurposes>
    </building>
    <comps>
{g}{gs}    </comps>
    <modExtensions>
      <li Class="SeasonsHolidays.SHSeasonDeco">
        <months>{"".join(f"<li>{m}</li>" for m in months)}</months>
      </li>
    </modExtensions>
  </ThingDef>
""")
    cz("ThingDef", f"{name}.label", cs)
    cz("ThingDef", f"{name}.description", dcs)


deco_build("SH_XmasWreath", "Christmas wreath", "adventní věnec", "A wreath of fir twigs with a red ribbon and candles. Pretty from Advent to New Year; pack it away afterwards.",
           "Věnec z jedlových větviček s červenou mašlí a svíčkami. Krásný od adventu do Nového roku, pak ho ukliď.", "XmasWreath", 0.9,
           [("WoodLog", 10), ("Cloth", 5)], 400, 10, [11, 0], ("3", "(255,200,120,0)"))
deco_build("SH_LightGarland", "light garland", "světelný řetěz", "A string of little coloured lights. Brightens the long winter evenings.",
           "Řetěz malých barevných světýlek. Rozzáří dlouhé zimní večery.", "LightGarland", 1.2,
           [("Steel", 15), ("ComponentIndustrial", 1)], 600, 8, [11, 0], ("6", "(255,210,160,0)"))
deco_build("SH_PumpkinDecor", "carved pumpkin", "vyřezaná dýně", "A carved pumpkin with a candle inside, to keep. For Halloween and the autumn; pack it away afterwards.",
           "Vyřezaná dýně se svíčkou uvnitř, natrvalo. Na Halloween a podzim, pak ji ukliď.", "Pumpkin", 0.9,
           [("WoodLog", 5)], 250, 6, [9, 10], ("4", "(255,160,60,0)"))
deco_build("SH_MaypoleDecor", "maypole", "májka", "A tall pole with ribbons and a little tree on top. Colonists like to gather around it in spring.",
           "Vysoká tyč s pentlemi a stromečkem navrchu. Kolonisté se kolem ní na jaře rádi scházejí.", "Maypole", "(1,2)",
           [("WoodLog", 30), ("Cloth", 10)], 900, 12, [3, 4], None, True, 8, 2102, "(0,0,0.5)")

# ---------------------------------------------------------------- write
(R / "Defs" / "Feast_SeasonsHolidays.xml").write_text(
    '<?xml version="1.0" encoding="utf-8"?>\n<Defs>\n\n  <!-- 0.5.0: generated by Tools/SeasonsHolidays_gen050.py -->\n\n' + "\n".join(defs) + "\n</Defs>\n", encoding="utf-8")
for dtype, lines in inj.items():
    d = R / "Languages" / "Czech" / "DefInjected" / dtype
    d.mkdir(parents=True, exist_ok=True)
    (d / "Feast_SeasonsHolidays.xml").write_text('<?xml version="1.0" encoding="utf-8"?>\n<LanguageData>\n' + "\n".join(lines) + "\n</LanguageData>\n", encoding="utf-8")
print("ok", len(defs), {k: len(v) for k, v in inj.items()})
