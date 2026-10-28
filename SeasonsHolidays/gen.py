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
