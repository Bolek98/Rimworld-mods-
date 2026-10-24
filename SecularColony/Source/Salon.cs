// Secular Colony 1.4.0 – barber's table (Vanilla Hair Expanded and similar), styles by gender, hair colours
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace SecularColony
{
	public static class Salon
	{
		private static List<ThingDef> tables;
		private static List<Color> palette;
		private static List<Color> natural;
		public static int modsWithHair, hairCount, colourCount;

		// every building that restyles hair: the vanilla styling station plus barber's tables of other mods
		public static List<ThingDef> Tables
		{
			get
			{
				if (tables == null)
				{
					tables = new List<ThingDef>();
					foreach (ThingDef d in DefDatabase<ThingDef>.AllDefsListForReading)
					{
						if (d.category != ThingCategory.Building || d.thingClass == null || !d.hasInteractionCell)
							continue;
						string c = d.thingClass.Name;
						string n = d.defName.ToLowerInvariant();
						if (d == ThingDefOf.StylingStation || c.Contains("Hairdressing") || c.Contains("StylingStation") || n.Contains("barber") || n.Contains("hairdress"))
							tables.Add(d);
					}
				}
				return tables;
			}
		}

		public static bool IsBarberTable(ThingDef d)
		{
			return d != null && d != ThingDefOf.StylingStation && Tables.Contains(d);
		}

		public static IEnumerable<Thing> AllTables(Map map)
		{
			if (map == null || map.listerBuildings == null)
				yield break;
			List<ThingDef> t = Tables;
			for (int i = 0; i < t.Count; i++)
				foreach (Building b in map.listerBuildings.AllBuildingsColonistOfDef(t[i]))
					yield return b;
		}

		public static bool AnyTable(Map map)
		{
			return AllTables(map).Any();
		}

		// ---- gender ---------------------------------------------------------------------------
		public static bool GenderOn(Pawn pawn)
		{
			SecularSettings s = SecularMod.Settings;
			return s != null && s.genderStyles && Secular.Active && pawn != null && pawn.Faction == Faction.OfPlayer
			       && pawn.RaceProps != null && pawn.RaceProps.Humanlike && (pawn.gender == Gender.Male || pawn.gender == Gender.Female);
		}

		public static bool WrongGender(Pawn pawn, StyleItemDef item)
		{
			if (!(item is HairDef) || item == HairDefOf.Bald)
				return false;
			StyleGender g = item.styleGender;
			if (pawn.gender == Gender.Male)
				return g == StyleGender.Female || g == StyleGender.FemaleUsually;
			if (pawn.gender == Gender.Female)
				return g == StyleGender.Male || g == StyleGender.MaleUsually;
			return false;
		}

		// ---- colours from every mod ------------------------------------------------------------
		private static void BuildColours()
		{
			palette = new List<Color>();
			natural = new List<Color>();
			// hair dyes of the game and of mods (ColorDef, type Hair)
			foreach (ColorDef c in DefDatabase<ColorDef>.AllDefsListForReading)
				if (c.colorType == ColorType.Hair)
					Add(palette, c.color);
			// presets of Vanilla Hair Expanded and any mod using its ColourPresetDef
			Type preset = GenTypes.GetTypeInAnyAssembly("VanillaHairExpanded.ColourPresetDef");
			if (preset != null)
			{
				FieldInfo f = preset.GetField("colour");
				foreach (Def d in AllDefsOf(preset))
					if (f != null)
						Add(palette, (Color)f.GetValue(d));
			}
			// natural colours from hair colour genes (Biotech and gene mods)
			if (ModsConfig.BiotechActive)
				foreach (GeneDef g in DefDatabase<GeneDef>.AllDefsListForReading)
					if (g.hairColorOverride.HasValue)
						Add(natural, g.hairColorOverride.Value);
			colourCount = palette.Count + natural.Count;
		}

		private static IEnumerable<Def> AllDefsOf(Type t)
		{
			try
			{
				object list = GenGeneric.GetStaticPropertyOnGenericType(typeof(DefDatabase<>), t, "AllDefsListForReading");
				System.Collections.IEnumerable e = list as System.Collections.IEnumerable;
				if (e != null)
					return e.OfType<Def>().ToList();
			}
			catch
			{
			}
			return Enumerable.Empty<Def>();
		}

		private static void Add(List<Color> list, Color c)
		{
			c.a = 1f;
			for (int i = 0; i < list.Count; i++)
				if (list[i].IndistinguishableFrom(c))
					return;
			list.Add(c);
		}

		// a new hair colour: women like dyes more, men mostly keep natural colours
		public static Color? NewColour(Pawn p)
		{
			if (palette == null)
				BuildColours();
			float dye = p.gender == Gender.Female ? 0.6f : 0.25f;
			Color? c = null;
			if (palette.Count > 0 && Rand.Value < dye)
				c = palette.RandomElement();
			else if (natural.Count > 0 && Rand.Value < 0.5f)
				c = natural.RandomElement();
			else
				c = PawnHairColors.RandomHairColor(p, p.story.SkinColor, p.ageTracker.AgeBiologicalYears);
			if (c.HasValue && c.Value.IndistinguishableFrom(p.story.HairColor))
				return null;
			return c;
		}

		// the barber's table of VHE gets the colours of all mods too (its own presets + the dyes, at most 4 rows)
		public static void FeedVhePresets()
		{
			Type preset = GenTypes.GetTypeInAnyAssembly("VanillaHairExpanded.ColourPresetDef");
			if (preset == null)
				return;
			try
			{
				if (palette == null)
					BuildColours();
				FieldInfo f = preset.GetField("colour");
				List<Def> have = AllDefsOf(preset).ToList();
				var known = have.Select(d => (Color)f.GetValue(d)).ToList();
				int room = 24 - have.Count;
				int n = 0;
				foreach (Color c in palette.Concat(natural))
				{
					if (room <= 0)
						break;
					if (known.Any(k => k.IndistinguishableFrom(c)))
						continue;
					Def d = (Def)Activator.CreateInstance(preset);
					d.defName = "SC_HairColour_" + n++;
					f.SetValue(d, c);
					// 1.4.1: DefDatabase<T>.Add has two overloads – pick Add(T) explicitly
					typeof(DefDatabase<>).MakeGenericType(preset).GetMethod("Add", new[] { preset }).Invoke(null, new object[] { d });
					known.Add(c);
					room--;
				}
			}
			catch (Exception ex)
			{
				Log.Warning("[Secular Colony] barber's table colours: " + ex.Message);
			}
		}

		public static void Startup()
		{
			try
			{
				BuildColours();
				FeedVhePresets();
				hairCount = DefDatabase<HairDef>.AllDefsListForReading.Count;
				modsWithHair = DefDatabase<HairDef>.AllDefsListForReading.Select(h => h.modContentPack).Where(m => m != null).Distinct().Count();
				Log.Message("[Secular Colony] styling: " + Tables.Count + " table type(s), " + hairCount + " hairstyles from " + modsWithHair + " mods, " + colourCount + " hair colours.");
			}
			catch (Exception ex)
			{
				Log.Warning("[Secular Colony] styling setup: " + ex.Message);
			}
		}
	}

	[StaticConstructorOnStartup]
	public static class SalonStartup
	{
		static SalonStartup()
		{
			Salon.Startup();
		}
	}

	public static class SalonPatches
	{
		public static void Apply(Harmony h)
		{
			Try(h, AccessTools.Method(typeof(JobGiver_UseStylingStationAutomatic), "TryGiveJob"), "UseStation_Prefix", null);
			Try(h, AccessTools.Method(typeof(PawnStyleItemChooser), "WantsToUseStyle"), null, "Gender_Postfix", Priority.Last);
			Try(h, AccessTools.Method(typeof(Toils_StyleChange), "FinalizeLookChange"), null, "Finalize_Postfix");
			Type dlg = AccessTools.TypeByName("VanillaHairExpanded.Dialog_ChangeHairstyle");
			if (dlg != null)
			{
				hairList = AccessTools.Field(dlg, "orderedHairDefs");
				beardList = AccessTools.Field(dlg, "orderedBeardDefs");
				jobDriverField = AccessTools.Field(dlg, "jobDriver");
				MethodInfo draw = AccessTools.Method(dlg, "DoWindowContents");
				if (draw != null)
				{
					try
					{
						h.Patch(draw, new HarmonyMethod(typeof(SalonPatches), "VheDialog_Prefix"), null, null, new HarmonyMethod(typeof(SalonPatches), "VheDialog_Finalizer"));
					}
					catch (Exception ex)
					{
						Log.Warning("[Secular Colony] barber's table dialog patch failed: " + ex.Message);
					}
				}
			}
		}

		private static void Try(Harmony h, MethodBase m, string pre, string post, int priority = -1)
		{
			if (m == null)
				return;
			try
			{
				HarmonyMethod postM = post == null ? null : new HarmonyMethod(typeof(SalonPatches), post);
				if (postM != null && priority >= 0)
					postM.priority = priority;
				h.Patch(m, pre == null ? null : new HarmonyMethod(typeof(SalonPatches), pre), postM);
			}
			catch (Exception ex)
			{
				Log.Warning("[Secular Colony] styling patch " + m.Name + " failed: " + ex.Message);
			}
		}

		private static bool On
		{
			get { return Secular.Active && SecularMod.Settings.barberTable; }
		}

		// a colonist who wants a new look goes to the nearest styling station OR barber's table
		public static bool UseStation_Prefix(Pawn pawn, ref Job __result)
		{
			if (!On || pawn == null || pawn.Map == null || pawn.style == null)
				return true;
			List<Thing> all = Salon.AllTables(pawn.Map).ToList();
			if (!all.Any(t => Salon.IsBarberTable(t.def)))
				return true;   // only styling stations – vanilla does it
			__result = null;
			if (Find.TickManager.TicksGame < pawn.style.nextStyleChangeAttemptTick)
				return false;
			Thing thing = GenClosest.ClosestThing_Global_Reachable(pawn.Position, pawn.Map, all, PathEndMode.InteractionCell, TraverseParms.For(pawn), 9999f,
				x => !x.IsForbidden(pawn) && pawn.CanReserve(x));
			if (thing == null)
			{
				pawn.style.ResetNextStyleChangeAttemptTick();
				return false;
			}
			__result = JobMaker.MakeJob(JobDefOf.UseStylingStationAutomatic, thing);
			return false;
		}

		// women only women's (and unisex) hairstyles, men only men's (and unisex)
		public static void Gender_Postfix(Pawn pawn, StyleItemDef styleItemDef, ref bool __result)
		{
			if (__result && styleItemDef != null && Salon.GenderOn(pawn) && Salon.WrongGender(pawn, styleItemDef))
				__result = false;
		}

		// the automatic restyle also changes the hair colour now and then (vanilla never applies it)
		public static void Finalize_Postfix(Toil __result)
		{
			if (__result == null)
				return;
			Action orig = __result.initAction;
			Toil toil = __result;
			__result.initAction = delegate
			{
				if (orig != null)
					orig();
				try
				{
					Pawn p = toil.actor;
					SecularSettings s = SecularMod.Settings;
					if (p == null || p.story == null || !Secular.Active || s == null || !s.hairColours || p.Faction != Faction.OfPlayer)
						return;
					if (Rand.Value >= s.hairColourChance)
						return;
					Color? c = Salon.NewColour(p);
					if (!c.HasValue)
						return;
					p.story.HairColor = c.Value;
					p.Drawer.renderer.SetAllGraphicsDirty();
					PortraitsCache.SetDirty(p);
				}
				catch (Exception ex)
				{
					Log.ErrorOnce("[Secular Colony] hair colour: " + ex.Message, 0x5ec0a1);
				}
			};
		}

		// VHE barber's table dialog: list only what fits the colonist (gender, beard, race, genes)
		private static FieldInfo hairList, beardList, jobDriverField;
		private static object savedHair, savedBeard;

		public static void VheDialog_Prefix(object __instance)
		{
			savedHair = savedBeard = null;
			if (!On || hairList == null || jobDriverField == null)
				return;
			try
			{
				JobDriver jd = jobDriverField.GetValue(__instance) as JobDriver;
				Pawn p = jd == null ? null : jd.pawn;
				if (p == null)
					return;
				List<HairDef> hairs = hairList.GetValue(null) as List<HairDef>;
				List<BeardDef> beards = beardList == null ? null : beardList.GetValue(null) as List<BeardDef>;
				if (hairs != null)
				{
					HairDef cur = p.story != null ? p.story.hairDef : null;
					List<HairDef> fit = hairs.Where(x => x == cur || PawnStyleItemChooser.WantsToUseStyle(p, x)).ToList();
					if (fit.Count > 1)
					{
						savedHair = hairs;
						hairList.SetValue(null, fit);
					}
				}
				if (beards != null && p.style != null)
				{
					List<BeardDef> fitB = p.style.CanWantBeard ? beards : beards.Where(x => x == BeardDefOf.NoBeard || x == p.style.beardDef).ToList();
					if (fitB != beards && fitB.Count > 0)
					{
						savedBeard = beards;
						beardList.SetValue(null, fitB);
					}
				}
			}
			catch
			{
			}
		}

		public static Exception VheDialog_Finalizer(Exception __exception)
		{
			VheDialog_Restore();
			return __exception;
		}

		private static void VheDialog_Restore()
		{
			if (savedHair != null)
				hairList.SetValue(null, savedHair);
			if (savedBeard != null)
				beardList.SetValue(null, savedBeard);
			savedHair = savedBeard = null;
		}
	}
}
