// Masks & Headgear Together – Bolek98
// Any mask (gas mask, respirator, face mask, glasses, visor) can be worn together with any hat or
// helmet. Nothing to set: masks and headgear are recognised by their stats, layers and names.
using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace MasksAndHeadgear
{
	[StaticConstructorOnStartup]
	public static class Startup
	{
		static Startup()
		{
			try
			{
				var h = new Harmony("misak.masksandheadgear");
				h.Patch(AccessTools.Method(typeof(ApparelUtility), nameof(ApparelUtility.CanWearTogether), new[] { typeof(ThingDef), typeof(ThingDef), typeof(BodyDef) }),
					postfix: new HarmonyMethod(typeof(Startup), nameof(CanWearTogetherPostfix)));
				int masks = 0;
				foreach (ThingDef d in DefDatabase<ThingDef>.AllDefsListForReading)
					if (d.IsApparel && Face.IsMask(d))
						masks++;
				Log.Message("[Masks & Headgear] v1.0.0 loaded – " + masks + " masks can be worn with any headgear.");
			}
			catch (Exception ex)
			{
				Log.Warning("[Masks & Headgear] could not start: " + ex.Message);
			}
		}

		public static void CanWearTogetherPostfix(ThingDef A, ThingDef B, ref bool __result)
		{
			if (__result || A == null || B == null || A == B)
				return;
			try
			{
				if ((Face.IsMask(A) && Face.IsHeadgear(B)) || (Face.IsMask(B) && Face.IsHeadgear(A)))
					__result = true;
			}
			catch
			{
			}
		}
	}

	public static class Face
	{
		private static readonly Dictionary<ThingDef, bool> maskCache = new Dictionary<ThingDef, bool>();
		private static readonly Dictionary<ThingDef, bool> headCache = new Dictionary<ThingDef, bool>();

		private static readonly string[] MaskWords = { "mask", "respirator", "rebreather", "filter", "visor", "goggles", "glasses", "spectacles", "monocle", "veil", "bandana", "scarf", "balaclava", "muzzle", "maska", "respirátor", "brýle", "šátek" };
		private static readonly HashSet<string> FaceGroups = new HashSet<string> { "Eyes", "Teeth", "Mouth", "Nose", "Jaw", "Ears", "LeftEye", "RightEye" };

		private static bool Has(ThingDef d, string word)
		{
			string a = d.defName ?? "";
			string b = d.label ?? "";
			return a.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0 || b.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0;
		}

		private static bool CoversHead(ThingDef d)
		{
			var g = d.apparel?.bodyPartGroups;
			if (g == null)
				return false;
			for (int i = 0; i < g.Count; i++)
				if (g[i] != null && (g[i] == BodyPartGroupDefOf.UpperHead || g[i] == BodyPartGroupDefOf.FullHead || g[i].defName == "Eyes" || g[i].defName == "Teeth" || FaceGroups.Contains(g[i].defName)))
					return true;
			return false;
		}

		private static bool OnlyFace(ThingDef d)
		{
			var g = d.apparel?.bodyPartGroups;
			if (g == null || g.Count == 0)
				return false;
			for (int i = 0; i < g.Count; i++)
				if (g[i] == null || !FaceGroups.Contains(g[i].defName))
					return false;
			return true;
		}

		private static float Armor(ThingDef d)
		{
			try
			{
				ThingDef stuff = d.MadeFromStuff ? GenStuff.DefaultStuffFor(d) : null;
				return Math.Max(d.GetStatValueAbstract(StatDefOf.ArmorRating_Sharp, stuff), d.GetStatValueAbstract(StatDefOf.ArmorRating_Blunt, stuff));
			}
			catch
			{
				return 0f;
			}
		}

		private static bool Toxic(ThingDef d)
		{
			var o = d.equippedStatOffsets;
			if (o == null)
				return false;
			for (int i = 0; i < o.Count; i++)
				if (o[i]?.stat != null && o[i].value > 0f && (o[i].stat.defName == "ToxicEnvironmentResistance" || o[i].stat.defName == "ToxicResistance"))
					return true;
			return false;
		}

		// a mask: worn on the face, little armour (a real helmet is never a mask)
		public static bool IsMask(ThingDef d)
		{
			if (d == null || !d.IsApparel)
				return false;
			if (maskCache.TryGetValue(d, out bool v))
				return v;
			v = false;
			if (CoversHead(d) && Armor(d) < 0.3f)
			{
				bool eyeCover = d.apparel.layers != null && d.apparel.layers.Exists(l => l != null && l.defName == "EyeCover");
				bool word = false;
				foreach (string w in MaskWords)
					if (Has(d, w)) { word = true; break; }
				v = eyeCover || OnlyFace(d) || Toxic(d) || word;
				// hats, helmets and hoods that only mention a word in passing stay headgear
				if (v && !eyeCover && !OnlyFace(d) && !Toxic(d) && (Has(d, "helmet") || Has(d, "hat") || Has(d, "hood") || Has(d, "helma") || Has(d, "klobouk")))
					v = false;
			}
			maskCache[d] = v;
			return v;
		}

		// headgear: a hat, helmet, hood, crown, headset … – anything on the head that is not a mask
		public static bool IsHeadgear(ThingDef d)
		{
			if (d == null || !d.IsApparel)
				return false;
			if (headCache.TryGetValue(d, out bool v))
				return v;
			v = CoversHead(d) && !IsMask(d) && !OnlyFace(d);
			headCache[d] = v;
			return v;
		}
	}
}
