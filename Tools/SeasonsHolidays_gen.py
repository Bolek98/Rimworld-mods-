#!/usr/bin/env python3
# Generates Defs and language files of Seasons & Holidays (run from the mod folder).
import pathlib
from xml.sax.saxutils import escape as e

R = pathlib.Path(__file__).resolve().parent

# id, label EN, label CS, mood, days, thought EN, thought CS
HOL = [
 ("NewYear", "New Year", "Nový rok", 6, 3, "A new year, a fresh start. Who knows what it brings!", "Nový rok, nový začátek. Kdo ví, co přinese!"),
 ("Valentine", "Valentine's Day", "Valentýn", 8, 2, "A day just for the two of us.", "Den jen pro nás dva."),
 ("Carnival", "Carnival", "Masopust", 6, 3, "Masks, music and a big feast before the lean weeks.", "Masky, hudba a velká hostina před půstem."),
 ("Easter", "Easter", "Velikonoce", 5, 3, "Painted eggs, spring flowers and a good mood all around.", "Malovaná vajíčka, jarní květiny a všude dobrá nálada."),
 ("Witches", "Witches' Night", "Pálení čarodějnic", 6, 3, "We burned the witch and chased the winter away. What a bonfire!", "Upálili jsme čarodějnici a zahnali zimu. To byl oheň!"),
 ("MayDay", "May Day of Love", "První máj", 8, 2, "A kiss under the blossoming tree – love is in the air.", "Polibek pod rozkvetlým stromem – láska je ve vzduchu."),
 ("ChildrenDay", "Children's Day", "Den dětí", 10, 2, "Today is all about us kids! Games and sweets!", "Dneska je to celé o nás dětech! Hry a sladkosti!"),
 ("ChildrenDayAdult", "Children's Day", "Den dětí", 3, 2, "Seeing the kids so happy warms my heart.", "Vidět děti tak šťastné mě hřeje u srdce."),
 ("Solstice", "Midsummer", "Slunovrat", 5, 3, "The longest day of the year. We celebrated under the open sky.", "Nejdelší den v roce. Slavili jsme pod širým nebem."),
 ("Halloween", "Halloween", "Halloween", 5, 2, "Pumpkins, spooky stories and a bit of fear – just right.", "Dýně, strašidelné příběhy a trocha strachu – tak akorát."),
 ("AllSouls", "All Souls' Day", "Dušičky", 3, 3, "We remembered those who are no longer with us. It helps to remember together.", "Vzpomněli jsme na ty, kdo už tu nejsou. Vzpomínat spolu pomáhá."),
 ("Thanksgiving", "Thanksgiving", "Díkůvzdání", 5, 3, "We are thankful for what we have and for each other.", "Jsme vděční za to, co máme, a jeden za druhého."),
 ("StNicholas", "St. Nicholas", "Mikuláš", 5, 2, "St. Nicholas came with gifts. The kids were thrilled.", "Přišel Mikuláš s nadílkou. Děti byly nadšené."),
 ("StNicholasChild", "St. Nicholas", "Mikuláš", 10, 3, "St. Nicholas brought me sweets! I was good this year. Mostly.", "Mikuláš mi přinesl sladkosti! Letos jsem byl hodný. Většinou."),
 ("Christmas", "Christmas", "Vánoce", 12, 5, "Christmas Eve, gifts, a feast and everyone together. The best night of the year.", "Štědrý večer, dárky, hostina a všichni spolu. Nejkrásnější večer v roce."),
 ("NewYearsEve", "New Year's Eve", "Silvestr", 6, 2, "Fireworks lit up the sky. Goodbye, old year!", "Ohňostroj rozzářil oblohu. Sbohem, starý roku!"),
 # 0.3.0: world holiday packs
 ("LunarNewYear", "Lunar New Year", "Lunární Nový rok", 6, 3, "Red lanterns, dumplings and dragons – a lucky new year!", "Červené lucerny, knedlíčky a draci – šťastný nový rok!"),
 ("Holi", "Holi", "Holi", 6, 2, "Everyone covered in colours from head to toe. What a mess, what a joy!", "Všichni od hlavy k patě v barvách. Ten nepořádek, ta radost!"),
 ("Diwali", "Diwali", "Diválí", 6, 3, "Lamps in every window – light wins over darkness.", "Lampičky v každém okně – světlo vítězí nad tmou."),
 ("MidAutumn", "Mid-Autumn Festival", "Svátek středu podzimu", 5, 2, "Mooncakes under the full moon with the whole family.", "Měsíční koláčky pod úplňkem s celou rodinou."),
 ("DayOfDead", "Day of the Dead", "Den mrtvých", 4, 3, "Marigolds, candles and stories of those we lost. They are with us today.", "Aksamitníky, svíčky a příběhy o těch, které jsme ztratili. Dnes jsou s námi."),
 ("FoundersDay", "Founders' Day", "Den založení", 6, 2, "We remembered how it all began. Look how far we've come!", "Vzpomněli jsme, jak to všechno začalo. Kam jsme to dotáhli!"),
 ("StPatrick", "St. Patrick's Day", "Den sv. Patrika", 5, 2, "Everything green, a pint in hand and a song on the lips.", "Všechno zelené, pivo v ruce a písnička na rtech."),
 ("Oktoberfest", "Oktoberfest", "Oktoberfest", 6, 2, "Beer, pretzels and brass music – prost!", "Pivo, preclíky a dechovka – na zdraví!"),
 ("StMartin", "St. Martin's Day", "Svatý Martin", 5, 2, "Roast goose and young wine – Martin came on a white horse.", "Pečená husa a svatomartinské víno – Martin přijel na bílém koni."),
]
TRAD = [("", ""), (" (tradition)", " (tradice)"), (" (our tradition)", " (naše tradice)"), (" (old tradition)", " (stará tradice)")]

OTHER = [
 # id, labelEN, labelCS, mood, days, descEN, descCS
 ("Birthday", "my birthday", "moje narozeniny", 10, 2, "It's my birthday! Everyone remembered.", "Mám narozeniny! Všichni si vzpomněli."),
 ("FirstSnow", "first snow", "první sníh", 4, 2, "The first snow of the year! Everything is white and quiet.", "První sníh v roce! Všechno je bílé a tiché."),
 ("SpringMood", "spring in the air", "jarní nálada", 3, 4, "The days are longer and everything is coming back to life.", "Dny jsou delší a všechno se probouzí k životu."),
 ("SummerEvenings", "warm summer evenings", "letní večery", 2, 4, "Long warm evenings – the best part of summer.", "Dlouhé teplé večery – to nejlepší z léta."),
 ("WinterBlues", "winter blues", "zimní splín", -2, 4, "Short days, long dark nights. I miss the sun.", "Krátké dny, dlouhé tmavé noci. Chybí mi slunce."),
]

def thoughts():
    o = ['<?xml version="1.0" encoding="utf-8"?>', "<Defs>"]
    inj = ['<?xml version="1.0" encoding="utf-8"?>', "<LanguageData>"]
    for hid, en, cs, mood, days, den, dcs in HOL:
        o += ["  <ThoughtDef>", f"    <defName>SH_H_{hid}</defName>", f"    <durationDays>{days}</durationDays>",
              "    <stackLimit>1</stackLimit>", "    <stages>"]
        for i, (ten, tcs) in enumerate(TRAD):
            o += ["      <li>", f"        <label>{e(en.lower() if hid not in ('Halloween',) else en)}{e(ten)}</label>",
                  f"        <description>{e(den)}</description>", f"        <baseMoodEffect>{mood + i}</baseMoodEffect>", "      </li>"]
            inj += [f"  <SH_H_{hid}.stages.{i}.label>{e(cs.lower() if hid not in ('Halloween',) else cs)}{e(tcs)}</SH_H_{hid}.stages.{i}.label>",
                    f"  <SH_H_{hid}.stages.{i}.description>{e(dcs)}</SH_H_{hid}.stages.{i}.description>"]
        o += ["    </stages>", "  </ThoughtDef>", ""]
    # harvest: by the food in storage
    hv = [("harvest festival (poor harvest)", "dožínky (slabá úroda)", 2, "We celebrated, but the stores are thin.", "Slavili jsme, ale zásob je málo."),
          ("harvest festival", "dožínky", 5, "A good harvest! We celebrated with a feast.", "Dobrá úroda! Oslavili jsme to hostinou."),
          ("harvest festival (rich harvest)", "dožínky (bohatá úroda)", 8, "The stores are full – we won't go hungry this winter!", "Sklady jsou plné – v zimě hlady neumřeme!")]
    o += ["  <ThoughtDef>", "    <defName>SH_H_Harvest</defName>", "    <durationDays>4</durationDays>", "    <stackLimit>1</stackLimit>", "    <stages>"]
    for i, (en, cs, m, den, dcs) in enumerate(hv):
        o += ["      <li>", f"        <label>{e(en)}</label>", f"        <description>{e(den)}</description>", f"        <baseMoodEffect>{m}</baseMoodEffect>", "      </li>"]
        inj += [f"  <SH_H_Harvest.stages.{i}.label>{e(cs)}</SH_H_Harvest.stages.{i}.label>", f"  <SH_H_Harvest.stages.{i}.description>{e(dcs)}</SH_H_Harvest.stages.{i}.description>"]
    o += ["    </stages>", "  </ThoughtDef>", ""]
    for tid, en, cs, mood, days, den, dcs in OTHER:
        o += ["  <ThoughtDef>", f"    <defName>SH_{tid}</defName>", f"    <durationDays>{days}</durationDays>", "    <stackLimit>1</stackLimit>",
              "    <stages>", "      <li>", f"        <label>{e(en)}</label>", f"        <description>{e(den)}</description>",
              f"        <baseMoodEffect>{mood}</baseMoodEffect>", "      </li>", "    </stages>", "  </ThoughtDef>", ""]
        inj += [f"  <SH_{tid}.stages.0.label>{e(cs)}</SH_{tid}.stages.0.label>", f"  <SH_{tid}.stages.0.description>{e(dcs)}</SH_{tid}.stages.0.description>"]
    o += ["</Defs>", ""]
    inj += ["</LanguageData>", ""]
    (R / "Defs" / "Thoughts_SeasonsHolidays.xml").write_text("\n".join(o), encoding="utf-8")
    d = R / "Languages" / "Czech" / "DefInjected" / "ThoughtDef"
    d.mkdir(parents=True, exist_ok=True)
    (d / "Thoughts_SeasonsHolidays.xml").write_text("\n".join(inj), encoding="utf-8")

SKY_CLEAR = """    <skyColorsDay>
      <sky>{d}</sky>
      <shadow>(0.718,0.745,0.757)</shadow>
      <overlay>(1,1,1)</overlay>
      <saturation>{s}</saturation>
    </skyColorsDay>
    <skyColorsDusk>
      <sky>{k}</sky>
      <shadow>(0.955,0.886,0.914)</shadow>
      <overlay>(0.8,0.8,0.8)</overlay>
      <saturation>{s}</saturation>
    </skyColorsDusk>
    <skyColorsNightEdge>
      <sky>(0.482,0.603,0.682)</sky>
      <shadow>(0.92,0.92,0.92)</shadow>
      <overlay>(0.6,0.6,0.6)</overlay>
      <saturation>{s}</saturation>
    </skyColorsNightEdge>
    <skyColorsNightMid>
      <sky>(0.482,0.603,0.682)</sky>
      <shadow>(0.85, 0.85, 0.85)</shadow>
      <overlay>(0.6,0.6,0.6)</overlay>
      <saturation>{s}</saturation>
    </skyColorsNightMid>"""

SKY_GREY = """    <skyColorsDay>
      <sky>(0.86,0.88,0.86)</sky>
      <shadow>(0.92,0.92,0.92)</shadow>
      <overlay>(0.8,0.8,0.8)</overlay>
      <saturation>1</saturation>
    </skyColorsDay>
    <skyColorsDusk>
      <sky>(1,0.95,0.9)</sky>
      <shadow>(0.92,0.92,0.92)</shadow>
      <overlay>(0.65,0.65,0.65)</overlay>
      <saturation>1</saturation>
    </skyColorsDusk>
    <skyColorsNightEdge>
      <sky>(0.35,0.40,0.45)</sky>
      <shadow>(0.92,0.92,0.92)</shadow>
      <overlay>(0.5,0.5,0.5)</overlay>
      <saturation>0.9</saturation>
    </skyColorsNightEdge>
    <skyColorsNightMid>
      <sky>(0.35,0.40,0.45)</sky>
      <shadow>(0.92,0.92,0.92)</shadow>
      <overlay>(0.5,0.5,0.5)</overlay>
      <saturation>0.9</saturation>
    </skyColorsNightMid>"""

WEATHER = [
 ("SH_Hoarfrost", "hoarfrost", "jinovatka", "A frosty, still, crystal-clear day. Everything outside is covered in white frost.",
  "Mrazivý, tichý a průzračný den. Všechno venku pokrývá bílá jinovatka.",
  "    <temperatureRange>-999~2</temperatureRange>\n    <favorability>Good</favorability>\n    <perceivePriority>0</perceivePriority>\n    <windSpeedFactor>0.3</windSpeedFactor>\n    <ambientSounds>\n      <li>Ambient_Wind_Clear</li>\n    </ambientSounds>\n"
  + SKY_CLEAR.format(d="(0.88,0.95,1.05)", k="(0.80,0.78,0.92)", s="0.85")),
 ("SH_IndianSummer", "Indian summer", "babí léto", "A warm, golden autumn day full of cobwebs drifting in the sun.",
  "Teplý zlatý podzimní den, ve slunci poletují pavučinky.",
  "    <temperatureRange>8~999</temperatureRange>\n    <favorability>Good</favorability>\n    <perceivePriority>0</perceivePriority>\n    <windSpeedFactor>0.6</windSpeedFactor>\n    <ambientSounds>\n      <li>Ambient_Wind_Clear</li>\n    </ambientSounds>\n"
  + SKY_CLEAR.format(d="(1.05,0.98,0.85)", k="(0.95,0.68,0.40)", s="1.35")),
 ("SH_SpringShowers", "spring showers", "jarní přeháňky", "Short light showers with sun in between. Good for the fields.",
  "Krátké slabé přeháňky a mezi nimi slunce. Polím to prospívá.",
  "    <temperatureRange>2~100</temperatureRange>\n    <windSpeedFactor>1.1</windSpeedFactor>\n    <accuracyMultiplier>0.9</accuracyMultiplier>\n    <favorability>Neutral</favorability>\n    <perceivePriority>1</perceivePriority>\n    <rainRate>0.4</rainRate>\n    <moveSpeedMultiplier>0.95</moveSpeedMultiplier>\n    <ambientSounds>\n      <li>Ambient_Rain</li>\n    </ambientSounds>\n    <overlayClasses>\n      <li>WeatherOverlay_Rain</li>\n    </overlayClasses>\n"
  + SKY_GREY),
]

def sky(day, dusk, night, sat, nightMid=None):
    nm = nightMid or night
    return f"""    <skyColorsDay>
      <sky>{day}</sky>
      <shadow>(0.85,0.85,0.85)</shadow>
      <overlay>(0.85,0.85,0.85)</overlay>
      <saturation>{sat}</saturation>
    </skyColorsDay>
    <skyColorsDusk>
      <sky>{dusk}</sky>
      <shadow>(0.92,0.92,0.92)</shadow>
      <overlay>(0.7,0.7,0.7)</overlay>
      <saturation>{sat}</saturation>
    </skyColorsDusk>
    <skyColorsNightEdge>
      <sky>{night}</sky>
      <shadow>(0.92,0.92,0.92)</shadow>
      <overlay>(0.55,0.55,0.55)</overlay>
      <saturation>{sat}</saturation>
    </skyColorsNightEdge>
    <skyColorsNightMid>
      <sky>{nm}</sky>
      <shadow>(0.88,0.88,0.88)</shadow>
      <overlay>(0.55,0.55,0.55)</overlay>
      <saturation>{sat}</saturation>
    </skyColorsNightMid>"""

def body(temp, fav, prio, wind, extra="", sounds=("Ambient_Wind_Clear",), overlays=(), events=""):
    o = f"    <temperatureRange>{temp}</temperatureRange>\n    <favorability>{fav}</favorability>\n    <perceivePriority>{prio}</perceivePriority>\n    <windSpeedFactor>{wind}</windSpeedFactor>\n" + extra
    o += "    <ambientSounds>\n" + "".join(f"      <li>{x}</li>\n" for x in sounds) + "    </ambientSounds>\n"
    if overlays:
        o += "    <overlayClasses>\n" + "".join(f"      <li>{x}</li>\n" for x in overlays) + "    </overlayClasses>\n"
    return o + events

LIGHTNING = "    <eventMakers>\n      <li>\n        <averageInterval>2400</averageInterval>\n        <eventClass>WeatherEvent_LightningFlash</eventClass>\n      </li>\n    </eventMakers>\n"

WEATHER += [
 ("SH_Aurora", "aurora", "polární záře", "A cold, clear night lit by dancing green and violet lights.",
  "Studená jasná noc, kterou rozsvěcují tančící zelená a fialová světla.",
  body("-999~5", "Good", "0", "0.4") + sky("(0.95,1.0,1.05)", "(0.70,0.80,0.95)", "(0.42,0.70,0.60)", "1.3", "(0.50,0.62,0.78)")),
 ("SH_Blizzard", "blizzard", "vánice", "A howling wind full of snow. You can barely see your own hand; best stay inside.",
  "Kvílející vítr plný sněhu. Není vidět na krok; nejlíp zůstat uvnitř.",
  body("-999~-4", "VeryBad", "2", "2.6", "    <isBad>true</isBad>\n    <snowRate>2</snowRate>\n    <rainRate>1</rainRate>\n    <accuracyMultiplier>0.55</accuracyMultiplier>\n    <moveSpeedMultiplier>0.65</moveSpeedMultiplier>\n    <durationRange>8000~20000</durationRange>\n",
       ("Ambient_Wind_Storm",), ("WeatherOverlay_SnowHard", "WeatherOverlay_Fog"))
  + sky("(0.88,0.90,0.94)", "(0.80,0.80,0.86)", "(0.40,0.44,0.50)", "0.6")),
 ("SH_FreezingRain", "freezing rain", "mrznoucí déšť", "Rain that freezes on everything it touches. Slippery paths and icy coats.",
  "Déšť, který namrzá na všem, čeho se dotkne. Kluzké cesty a zledovatělé kabáty.",
  body("-3~2", "Bad", "1", "1.3", "    <isBad>true</isBad>\n    <rainRate>1</rainRate>\n    <snowRate>0.2</snowRate>\n    <weatherThought>SoakingWet</weatherThought>\n    <accuracyMultiplier>0.8</accuracyMultiplier>\n    <moveSpeedMultiplier>0.75</moveSpeedMultiplier>\n    <durationRange>6000~15000</durationRange>\n",
       ("Ambient_Rain",), ("WeatherOverlay_Rain",)) + SKY_GREY),
 ("SH_Hail", "hailstorm", "krupobití", "A short, violent storm of ice balls. It drums on the roofs and stings outside.",
  "Krátká prudká bouře ledových kroupů. Bubnuje na střechy a venku to štípe.",
  body("2~35", "Bad", "2", "1.8", "    <isBad>true</isBad>\n    <rainRate>1</rainRate>\n    <accuracyMultiplier>0.75</accuracyMultiplier>\n    <moveSpeedMultiplier>0.85</moveSpeedMultiplier>\n    <durationRange>2500~6000</durationRange>\n",
       ("Ambient_Rain", "Ambient_Wind_Storm"), ("WeatherOverlay_Rain", "WeatherOverlay_SnowHard"), LIGHTNING) + SKY_GREY),
 ("SH_Heatwave", "heat haze", "parno", "The air shimmers above the ground. Not a breath of wind; the sun burns.",
  "Vzduch se nad zemí tetelí. Ani větřík; slunce pálí.",
  body("28~999", "Neutral", "0", "0.2") + sky("(1.10,1.02,0.86)", "(1.0,0.66,0.40)", "(0.50,0.56,0.62)", "1.35")),
 ("SH_Sandstorm", "sandstorm", "písečná bouře", "A wall of sand and dust. It gets into everything and you can't see far.",
  "Stěna písku a prachu. Dostane se všude a není vidět daleko.",
  body("5~999", "Bad", "2", "2.6", "    <isBad>true</isBad>\n    <accuracyMultiplier>0.5</accuracyMultiplier>\n    <moveSpeedMultiplier>0.85</moveSpeedMultiplier>\n    <durationRange>6000~18000</durationRange>\n",
       ("Ambient_Wind_Storm",), ("WeatherOverlay_Fog",)) + sky("(1.02,0.84,0.58)", "(0.92,0.62,0.38)", "(0.48,0.42,0.36)", "0.7")),
 ("SH_Monsoon", "monsoon rain", "monzunový liják", "Warm rain pours down for hours. Everything is soaked and the ground turns to mud.",
  "Hodiny leje teplý déšť. Všechno je promočené a zem se mění v bláto.",
  body("15~999", "Bad", "2", "1.6", "    <isBad>true</isBad>\n    <rainRate>1</rainRate>\n    <weatherThought>SoakingWet</weatherThought>\n    <accuracyMultiplier>0.6</accuracyMultiplier>\n    <moveSpeedMultiplier>0.75</moveSpeedMultiplier>\n    <durationRange>20000~50000</durationRange>\n",
       ("Ambient_Rain", "Ambient_Wind_Storm"), ("WeatherOverlay_Rain", "WeatherOverlay_Fog"), LIGHTNING) + SKY_GREY),
]

def weathers():
    o = ['<?xml version="1.0" encoding="utf-8"?>', "<Defs>"]
    inj = ['<?xml version="1.0" encoding="utf-8"?>', "<LanguageData>"]
    for dn, en, cs, den, dcs, body in WEATHER:
        o += ["  <WeatherDef>", f"    <defName>{dn}</defName>", f"    <label>{e(en)}</label>", f"    <description>{e(den)}</description>",
              "    <repeatable>true</repeatable>", body, "  </WeatherDef>", ""]
        inj += [f"  <{dn}.label>{e(cs)}</{dn}.label>", f"  <{dn}.description>{e(dcs)}</{dn}.description>"]
    o += ["</Defs>", ""]
    inj += ["</LanguageData>", ""]
    (R / "Defs" / "Weathers_SeasonsHolidays.xml").write_text("\n".join(o), encoding="utf-8")
    d = R / "Languages" / "Czech" / "DefInjected" / "WeatherDef"
    d.mkdir(parents=True, exist_ok=True)
    (d / "Weathers_SeasonsHolidays.xml").write_text("\n".join(inj), encoding="utf-8")

def misc():
    o = """<?xml version="1.0" encoding="utf-8"?>
<Defs>

  <MainButtonDef>
    <defName>SH_Calendar</defName>
    <label>calendar</label>
    <description>The year at a glance: months, holidays, birthdays, temperatures and the growing season.</description>
    <tabWindowClass>SeasonsHolidays.MainTabWindow_Calendar</tabWindowClass>
    <order>58</order>
  </MainButtonDef>

  <HediffDef>
    <defName>SH_Resolution</defName>
    <label>New Year's resolution</label>
    <description>This year I'm going to learn more! Learns faster for a few days.</description>
    <hediffClass>HediffWithComps</hediffClass>
    <isBad>false</isBad>
    <defaultLabelColor>(0.6, 0.9, 0.6)</defaultLabelColor>
    <comps>
      <li Class="HediffCompProperties_Disappears">
        <disappearsAfterTicks>300000</disappearsAfterTicks>
        <showRemainingTime>true</showRemainingTime>
      </li>
    </comps>
    <stages>
      <li>
        <statOffsets>
          <GlobalLearningFactor>0.25</GlobalLearningFactor>
        </statOffsets>
      </li>
    </stages>
  </HediffDef>

  <ThingDef ParentName="MealBaseIngredientless">
    <defName>SH_ChristmasCookies</defName>
    <label>Christmas cookies</label>
    <description>A box of homemade Christmas cookies. Sweet, festive and they keep for weeks.</description>
    <graphicData>
      <texPath>Things/Item/Meal/Fine</texPath>
      <graphicClass>Graphic_MealVariants</graphicClass>
      <color>(0.95,0.78,0.52)</color>
    </graphicData>
    <tradeability>Sellable</tradeability>
    <statBases>
      <MarketValue>18</MarketValue>
      <Nutrition>0.5</Nutrition>
    </statBases>
    <comps>
      <li Class="CompProperties_Rottable">
        <daysToRotStart>30</daysToRotStart>
        <rotDestroys>true</rotDestroys>
      </li>
    </comps>
    <ingestible>
      <preferability>MealFine</preferability>
      <tasteThought>AteFineMeal</tasteThought>
      <joy>0.25</joy>
      <joyKind>Gluttonous</joyKind>
      <ingestSound>Meal_Eat</ingestSound>
      <optimalityOffsetFeedingAnimals>-75</optimalityOffsetFeedingAnimals>
    </ingestible>
  </ThingDef>

</Defs>
"""
    (R / "Defs" / "Misc_SeasonsHolidays.xml").write_text(o, encoding="utf-8")
    base = R / "Languages" / "Czech" / "DefInjected"
    for sub, body in [("MainButtonDef", "  <SH_Calendar.label>kalendář</SH_Calendar.label>\n  <SH_Calendar.description>Celý rok na jednom místě: měsíce, svátky, narozeniny, teploty a vegetační období.</SH_Calendar.description>\n"),
                      ("HediffDef", "  <SH_Resolution.label>novoroční předsevzetí</SH_Resolution.label>\n  <SH_Resolution.description>Letos se budu víc učit! Pár dní se učí rychleji.</SH_Resolution.description>\n"),
                      ("ThingDef", "  <SH_ChristmasCookies.label>vánoční cukroví</SH_ChristmasCookies.label>\n  <SH_ChristmasCookies.description>Krabice domácího vánočního cukroví. Sladké, sváteční a vydrží týdny.</SH_ChristmasCookies.description>\n")]:
        (base / sub).mkdir(parents=True, exist_ok=True)
        (base / sub / "SeasonsHolidays.xml").write_text('<?xml version="1.0" encoding="utf-8"?>\n<LanguageData>\n' + body + "</LanguageData>\n", encoding="utf-8")

thoughts(); weathers(); misc()
print("ok")
