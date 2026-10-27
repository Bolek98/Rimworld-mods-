// Masks & Headgear Together – Bolek98
// Any mask (gas mask, respirator, face mask, glasses, visor) can be worn together with any hat or
// helmet. Nothing to set: masks and headgear are recognised by their stats, layers and names.
using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
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
				// 1.1.0: per-hat hair visibility and "hide all headgear"
				HairPatches.Apply(h);
				HairPatches.Rebuild();
				int masks = 0;
				foreach (ThingDef d in DefDatabase<ThingDef>.AllDefsListForReading)
					if (d.IsApparel && Face.IsMask(d))
						masks++;
				Log.Message("[Masks & Headgear] v" + MasksMod.Version + " loaded – " + masks + " masks can be worn with any headgear.");
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

namespace MasksAndHeadgear
{
	// 1.1.0: settings – hair visibility per headgear (0 = like the game, 1 = show hair, 2 = hide hair)
	// and one switch that hides all headgear (hats, helmets, masks) on every pawn.
	public class MasksSettings : ModSettings
	{
		public bool hideAllHeadgear;
		public Dictionary<string, int> hairMode = new Dictionary<string, int>();

		public override void ExposeData()
		{
			base.ExposeData();
			Scribe_Values.Look(ref hideAllHeadgear, "hideAllHeadgear", false);
			Scribe_Collections.Look(ref hairMode, "hairMode", LookMode.Value, LookMode.Value);
			if (hairMode == null)
				hairMode = new Dictionary<string, int>();
		}
	}

	public class MasksMod : Mod
	{
		public const string Version = "1.1.0";
		public static MasksSettings Settings;
		private Vector2 scroll;
		private string filter = "";
		private List<ThingDef> heads;

		public MasksMod(ModContentPack content) : base(content)
		{
			Settings = GetSettings<MasksSettings>();
		}

		public override string SettingsCategory() => "Masks & Headgear Together";

		public override void WriteSettings()
		{
			base.WriteSettings();
			HairPatches.Rebuild();
			HairPatches.RedrawPawns();
		}

		private List<ThingDef> Heads()
		{
			if (heads == null)
			{
				heads = new List<ThingDef>();
				foreach (ThingDef d in DefDatabase<ThingDef>.AllDefsListForReading)
					if (d.IsApparel && (Face.IsHeadgear(d) || Face.IsMask(d)))
						heads.Add(d);
				heads.Sort((a, b) => string.Compare(a.label ?? a.defName, b.label ?? b.defName, StringComparison.OrdinalIgnoreCase));
			}
			return heads;
		}

		public override void DoSettingsWindowContents(Rect inRect)
		{
			MasksSettings s = Settings;
			var top = new Listing_Standard();
			top.Begin(new Rect(inRect.x, inRect.y, inRect.width, 120f));
			top.CheckboxLabeled("MH_HideAll".Translate(), ref s.hideAllHeadgear, "MH_HideAll_Desc".Translate());
			top.Gap(4f);
			GUI.color = Color.gray;
			top.Label("MH_HairIntro".Translate());
			GUI.color = Color.white;
			top.End();

			float y = inRect.y + 70f;
			Rect searchRect = new Rect(inRect.x, y, Mathf.Min(300f, inRect.width - 200f), 28f);
			filter = Widgets.TextField(searchRect, filter ?? "");
			if (string.IsNullOrEmpty(filter))
			{
				GUI.color = Color.gray;
				Widgets.Label(new Rect(searchRect.x + 6f, searchRect.y + 4f, searchRect.width, 24f), "MH_Search".Translate());
				GUI.color = Color.white;
			}
			Rect resetRect = new Rect(inRect.xMax - 190f, y, 190f, 28f);
			if (s.hairMode.Count > 0 && Widgets.ButtonText(resetRect, "MH_ResetAll".Translate()))
				s.hairMode.Clear();
			y += 34f;

			List<ThingDef> list = Heads();
			var shown = new List<ThingDef>();
			foreach (ThingDef d in list)
				if (string.IsNullOrEmpty(filter) || (d.label ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 || d.defName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
					shown.Add(d);
			Rect outRect = new Rect(inRect.x, y, inRect.width, inRect.yMax - y);
			Rect view = new Rect(0f, 0f, outRect.width - 16f, Math.Max(shown.Count * 28f, outRect.height));
			Widgets.BeginScrollView(outRect, ref scroll, view);
			for (int i = 0; i < shown.Count; i++)
			{
				ThingDef d = shown[i];
				Rect row = new Rect(0f, i * 28f, view.width, 26f);
				if (i % 2 == 1)
					Widgets.DrawLightHighlight(row);
				int mode = s.hairMode.TryGetValue(d.defName, out int m) ? m : 0;
				bool vanillaHides = HairPatches.VanillaHidesHair(d);
				Rect label = new Rect(row.x + 4f, row.y + 2f, row.width - 200f, 24f);
				Widgets.Label(label, d.LabelCap + (d.modContentPack != null && !d.modContentPack.IsOfficialMod ? "  (" + d.modContentPack.Name + ")" : ""));
				TooltipHandler.TipRegion(label, (TipSignal)(d.defName + "\n" + (vanillaHides ? "MH_GameHides" : "MH_GameShows").Translate()));
				string text = mode == 1 ? "MH_ShowHair".Translate() : mode == 2 ? "MH_HideHair".Translate()
					: "MH_Default".Translate() + " (" + (vanillaHides ? "MH_HideHair" : "MH_ShowHair").Translate() + ")";
				if (mode != 0)
					GUI.color = new Color(1f, 0.85f, 0.5f);
				if (Widgets.ButtonText(new Rect(row.xMax - 190f, row.y, 190f, 26f), text))
				{
					mode = (mode + 1) % 3;
					if (mode == 0)
						s.hairMode.Remove(d.defName);
					else
						s.hairMode[d.defName] = mode;
				}
				GUI.color = Color.white;
			}
			Widgets.EndScrollView();
		}
	}

	// 1.1.0: hair under headgear and hiding all headgear.
	// PawnRenderTree.AdjustParms runs in parallel render threads in 1.7, so the patches only read
	// an immutable snapshot (rebuilt on the main thread when settings are saved).
	public static class HairPatches
	{
		private static volatile Dictionary<ThingDef, int> modes = new Dictionary<ThingDef, int>();
		private static volatile bool hideAll;
		private static bool active;

		public static void Apply(Harmony h)
		{
			try
			{
				var adjust = AccessTools.Method(typeof(PawnRenderTree), "AdjustParms");
				if (adjust != null)
					h.Patch(adjust, prefix: new HarmonyMethod(typeof(HairPatches), nameof(AdjustPrefix)), postfix: new HarmonyMethod(typeof(HairPatches), nameof(AdjustPostfix)));
				else
					Log.Warning("[Masks & Headgear] PawnRenderTree.AdjustParms not found – hair settings inactive.");
				var canDraw = AccessTools.Method(typeof(PawnRenderNodeWorker_Apparel_Head), nameof(PawnRenderNodeWorker_Apparel_Head.CanDrawNow));
				if (canDraw != null)
					h.Patch(canDraw, postfix: new HarmonyMethod(typeof(HairPatches), nameof(CanDrawNowPostfix)));
				active = true;
			}
			catch (Exception ex)
			{
				Log.Warning("[Masks & Headgear] hair settings inactive: " + ex.Message);
			}
		}

		public static void Rebuild()
		{
			var s = MasksMod.Settings;
			var d = new Dictionary<ThingDef, int>();
			if (s != null)
				foreach (var kv in s.hairMode)
				{
					ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(kv.Key);
					if (def != null && kv.Value != 0)
						d[def] = kv.Value;
				}
			modes = d;
			hideAll = s != null && s.hideAllHeadgear;
		}

		public static void RedrawPawns()
		{
			try
			{
				if (Current.ProgramState != ProgramState.Playing || Find.Maps == null)
					return;
				foreach (Map map in Find.Maps)
					foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
						if (p.RaceProps.Humanlike)
							p.Drawer?.renderer?.SetAllGraphicsDirty();
				PortraitsCache.Clear();
			}
			catch
			{
			}
		}

		// does the game hide hair under this apparel (same rule as PawnRenderTree.AdjustParms)
		public static bool VanillaHidesHair(ThingDef d)
		{
			var a = d?.apparel;
			if (a == null)
				return false;
			if (a.renderSkipFlags != null)
				return a.renderSkipFlags.Contains(RenderSkipFlagDefOf.Hair);
			return a.bodyPartGroups != null && (a.bodyPartGroups.Contains(BodyPartGroupDefOf.UpperHead) || a.bodyPartGroups.Contains(BodyPartGroupDefOf.FullHead));
		}

		private static ulong VanillaFlags(ThingDef d)
		{
			ulong f = 0;
			var a = d.apparel;
			if (a.renderSkipFlags != null)
			{
				foreach (RenderSkipFlagDef r in a.renderSkipFlags)
					if (r != RenderSkipFlagDefOf.None)
						f |= r;
			}
			else if (a.bodyPartGroups != null)
			{
				if (a.bodyPartGroups.Contains(BodyPartGroupDefOf.UpperHead))
					f |= RenderSkipFlagDefOf.Hair;
				if (a.bodyPartGroups.Contains(BodyPartGroupDefOf.FullHead))
					f |= (ulong)RenderSkipFlagDefOf.Hair | RenderSkipFlagDefOf.Beard | RenderSkipFlagDefOf.Eyes;
			}
			return f;
		}

		public static void AdjustPrefix(ref PawnDrawParms parms, out ulong __state)
		{
			__state = parms.skipFlags;
		}

		public static void AdjustPostfix(PawnRenderTree __instance, ref PawnDrawParms parms, ulong __state)
		{
			if (!active)
				return;
			Dictionary<ThingDef, int> m = modes;
			bool all = hideAll;
			if (!all && m.Count == 0)
				return;
			try
			{
				Pawn pawn = __instance.pawn;
				if (pawn?.apparel == null || !pawn.RaceProps.Humanlike)
					return;
				List<Apparel> worn = pawn.apparel.WornApparel;
				if (all)
				{
					// the game added the flags of worn headgear – take back those that were not there before
					ulong added = 0;
					for (int i = 0; i < worn.Count; i++)
						if (worn[i]?.def?.apparel != null)
							added |= VanillaFlags(worn[i].def);
					parms.skipFlags &= ~(added & ~__state);
					return;
				}
				if (!PawnRenderNodeWorker_Apparel_Head.HeadgearVisible(parms))
					return;
				bool hide = (__state & RenderSkipFlagDefOf.Hair) != 0;
				bool touched = false;
				for (int i = 0; i < worn.Count && !hide; i++)
				{
					ThingDef d = worn[i]?.def;
					if (d?.apparel == null)
						continue;
					if (m.TryGetValue(d, out int mode))
					{
						touched = true;
						if (mode == 2)
							hide = true;
					}
					else if ((VanillaFlags(d) & RenderSkipFlagDefOf.Hair) != 0)
						hide = true;
				}
				if (!touched && !hide)
					return;
				if (hide)
					parms.skipFlags |= RenderSkipFlagDefOf.Hair;
				else
					parms.skipFlags &= ~(ulong)RenderSkipFlagDefOf.Hair;
			}
			catch
			{
			}
		}

		public static void CanDrawNowPostfix(ref bool __result)
		{
			if (__result && hideAll)
				__result = false;
		}
	}
}
