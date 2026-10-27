using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace BodyFit;

// ---------------------------------------------------------------------------
// 1. Head position. The game puts the head at bodyType.headOffset. Body types
//    from mods sometimes have no offset at all (0, 0) – the head is then drawn
//    in the middle of the body. Here:
//      * your own offset for a body type (settings) wins,
//      * else a body type with no offset uses the offset of the most similar
//        vanilla body (Female → Female, Hulk → Hulk …),
//      * plus an extra height per race (settings).
//    Runs last, adds only the difference – offsets from HAR and other mods
//    stay as they are.
// ---------------------------------------------------------------------------
[HarmonyPatch(typeof(PawnRenderer), nameof(PawnRenderer.BaseHeadOffsetAt))]
public static class Patch_HeadOffset
{
	[HarmonyPriority(Priority.Last)]
	public static void Postfix(PawnRenderer __instance, Rot4 rotation, ref Vector3 __result, Pawn ___pawn)
	{
		if (!BF.Handles(___pawn))
			return;
		try
		{
			Pawn pawn = ___pawn;
			BodyTypeDef b = pawn?.story?.bodyType;
			if (b == null)
				return;
			BodyFitSettings s = BodyFitMod.Settings;
			Vector2 want = HeadFix.Wanted(b, pawn.gender);
			float race = s.RaceHeight(pawn.def);
			if (want == b.headOffset && race == 0f)
				return;
			float size = Mathf.Sqrt(pawn.ageTracker.CurLifeStage.bodySizeFactor);
			Vector2 d = (want - b.headOffset) * size;
			switch (rotation.AsInt)
			{
				case 1: __result += new Vector3(d.x, 0f, d.y + race); break;
				case 3: __result += new Vector3(-d.x, 0f, d.y + race); break;
				default: __result += new Vector3(0f, 0f, d.y + race); break;
			}
		}
		catch
		{
		}
	}
}

public static class HeadFix
{
	private static readonly Dictionary<(BodyTypeDef, Gender), Vector2> cache = new Dictionary<(BodyTypeDef, Gender), Vector2>();

	public static bool Missing(BodyTypeDef b) => Mathf.Abs(b.headOffset.x) < 0.0001f && Mathf.Abs(b.headOffset.y) < 0.0001f;

	public static Vector2 Wanted(BodyTypeDef b, Gender g)
	{
		Vector2 v;
		lock (BF.Gate)
			if (cache.TryGetValue((b, g), out v))
				return v;
		BodyFitSettings s = BodyFitMod.Settings;
		if (s.headOverride.TryGetValue(b.defName, out Vector2 own))
			v = own;
		else if (s.fixHeads && Missing(b))
			v = Bodies.Vanilla(Bodies.KindOf(b, g))?.headOffset ?? b.headOffset;
		else
			v = b.headOffset;
		lock (BF.Gate)
			cache[(b, g)] = v;
		return v;
	}

	public static void Clear() { lock (BF.Gate) cache.Clear(); }
}

// ---------------------------------------------------------------------------
// 2. Apparel with no picture for the wearer's body type (a modded body, or a
//    modded piece of clothing made for fewer bodies). The game would draw
//    nothing / a pink square and log "Failed to find any textures". Here the
//    most similar body type that HAS a picture is used instead – Hulk takes
//    Male before Fat, Thin takes Female or Male by gender, children take
//    Child, Thin … Checked before drawing, so no error, no pink square.
//    For HAR races (which have their own clothing paths) the check runs
//    after HAR, on the real result.
// ---------------------------------------------------------------------------
[HarmonyPatch(typeof(ApparelGraphicRecordGetter), nameof(ApparelGraphicRecordGetter.TryGetGraphicApparel))]
public static class Patch_Apparel
{
	[ThreadStatic] private static bool inside;
	// 1.2.3: keyed by the picture path (styles change it), guarded by BF.Gate
	private static readonly Dictionary<(string, BodyTypeDef), BodyTypeDef> chosen = new Dictionary<(string, BodyTypeDef), BodyTypeDef>();

	public static void Clear() { lock (BF.Gate) { chosen.Clear(); harChosen.Clear(); } }

	private static readonly Dictionary<(string, BodyTypeDef), BodyTypeDef> harChosen = new Dictionary<(string, BodyTypeDef), BodyTypeDef>();

	// Which body's picture is really drawn for this apparel on this body
	// (null = its own). Used by Fit to reshape the borrowed picture.
	public static BodyTypeDef Substitute(Apparel a, BodyTypeDef body)
	{
		if (a == null || body == null)
			return null;
		string worn = a.WornGraphicPath;
		if (worn == null)
			return null;
		lock (BF.Gate)
		{
			if (chosen.TryGetValue((worn, body), out BodyTypeDef u) && u != body)
				return u;
			if (harChosen.TryGetValue((worn, body), out u) && u != body)
				return u;
		}
		return null;
	}

	private static bool UsesBodySuffix(Apparel a)
	{
		ApparelLayerDef last = a.def.apparel.LastLayer;
		return last != ApparelLayerDefOf.Overhead && last != ApparelLayerDefOf.EyeCover && !a.RenderAsPack()
		       && a.WornGraphicPath != BaseContent.PlaceholderImagePath && a.WornGraphicPath != BaseContent.PlaceholderGearImagePath;
	}

	[HarmonyPriority(Priority.First)]
	public static void Prefix(Apparel apparel, ref BodyTypeDef bodyType)
	{
		if (inside || !BodyFitMod.Settings.fixApparel || apparel == null || bodyType == null || (apparel.Wearer != null && !BF.Handles(apparel.Wearer)))
			return;
		try
		{
			Pawn wearer = apparel.Wearer;
			if (wearer != null && Bodies.IsHar(wearer.def))
				return;   // handled after HAR, in the postfix
			if (apparel.WornGraphicPath.NullOrEmpty() || !UsesBodySuffix(apparel))
				return;
			var key = (apparel.WornGraphicPath, bodyType);
			BodyTypeDef use;
			bool known;
			lock (BF.Gate)
				known = chosen.TryGetValue(key, out use);
			if (!known)
			{
				bool wasNotReady = BF.NotReady;
				BF.NotReady = false;
				use = bodyType;
				if (!Bodies.TextureExists(apparel.WornGraphicPath + "_" + bodyType.defName))
				{
					foreach (BodyTypeDef alt in Bodies.Similar(bodyType, wearer?.gender ?? Gender.None))
					{
						if (Bodies.TextureExists(apparel.WornGraphicPath + "_" + alt.defName))
						{
							use = alt;
							break;
						}
					}
				}
				bool guessed = BF.NotReady;
				BF.NotReady = wasNotReady || guessed;
				if (guessed)
					return;   // pictures not checked yet – keep the game's choice for now
				lock (BF.Gate)
					chosen[key] = use;
			}
			bodyType = use;
		}
		catch
		{
		}
	}

	public static void Postfix(Apparel apparel, BodyTypeDef bodyType, bool forStatue, ref ApparelGraphicRecord rec, ref bool __result)
	{
		if (inside || !BodyFitMod.Settings.fixApparel || !__result || apparel == null || bodyType == null || (apparel.Wearer != null && !BF.Handles(apparel.Wearer)))
			return;
		// 1.2.3: building graphics is main-thread work
		if (!BF.Main)
			return;
		try
		{
			if (!(rec.graphic is Graphic_Multi g) || g.MatSouth != BaseContent.BadMat)
				return;
			inside = true;
			// 1.2.3: only try bodies that have a picture – no "Failed to find any textures" errors
			string suf = "_" + bodyType.defName;
			string stem = g.path != null && g.path.EndsWith(suf) ? g.path.Substring(0, g.path.Length - bodyType.defName.Length) : null;
			foreach (BodyTypeDef alt in Bodies.Similar(bodyType, apparel.Wearer?.gender ?? Gender.None))
			{
				if (stem != null && !Bodies.TextureExists(stem + alt.defName))
					continue;
				if (ApparelGraphicRecordGetter.TryGetGraphicApparel(apparel, alt, forStatue, out ApparelGraphicRecord r2)
				    && r2.graphic != null && r2.graphic.MatSouth != BaseContent.BadMat)
				{
					rec = r2;
					if (apparel.WornGraphicPath != null)
						lock (BF.Gate)
							harChosen[(apparel.WornGraphicPath, bodyType)] = alt;
					break;
				}
			}
		}
		catch
		{
		}
		finally
		{
			inside = false;
		}
	}
}

// ---------------------------------------------------------------------------
// 3. A pawn whose body type cannot be drawn: no body picture (a removed mod,
//    a broken modded body), or – for HAR races – a body type the race does
//    not have (e.g. a gene or an editor gave a female-only race "Hulk").
//    That is what puts heads in the middle of bodies and clothes in the air.
//    The pawn gets the most similar body type that works for its race.
//    Checked when a pawn appears and every few hours for everyone on the map.
// ---------------------------------------------------------------------------
[HarmonyPatch(typeof(Pawn), nameof(Pawn.SpawnSetup))]
public static class Patch_Spawn
{
	public static void Postfix(Pawn __instance)
	{
		// 1.2.3: loading and map generation run in a background thread – check later on the main thread
		if (!BF.Main)
		{
			Pawn p = __instance;
			LongEventHandler.ExecuteWhenFinished(() => BodyRepair.Check(p, false));
			return;
		}
		BodyRepair.Check(__instance, false);
	}
}

public static class BodyRepair
{
	public static int repaired;

	public static bool Check(Pawn p, bool tell)
	{
		if (!BodyFitMod.Settings.fixBodies || p?.story == null || !p.RaceProps.Humanlike || !BF.Main || p.Destroyed)
			return false;
		try
		{
			// mutants and creepjoiners draw their own bodies
			if (p.IsMutant || p.IsCreepJoiner)
				return false;
			// 2.3.0: a race Body Fit does not change keeps its body – unless the body really cannot be drawn
			if (!BF.Handles(p) && !DrawnBodyBroken(p))
				return false;
			BodyTypeDef b = p.story.bodyType;
			// the game's own body types (incl. child and baby) are always drawable for non-HAR pawns
			if (b != null && b.modContentPack != null && b.modContentPack.IsOfficialMod && !Bodies.IsHar(p.def))
				return false;
			if (b != null && Bodies.BodyUsable(p, b) && !DrawnBodyBroken(p))
				return false;
			BodyTypeDef use = Bodies.BestUsable(p, b ?? Bodies.Vanilla(Bodies.KindOf(null, p.gender)));
			if (use == null || use == b || !Bodies.BodyUsable(p, use))
				return false;
			p.story.bodyType = use;
			p.Drawer?.renderer?.SetAllGraphicsDirty();
			repaired++;
			MLog.Info("[Body Fit] " + p.LabelShortCap + " (" + p.def.defName + "): body " + (b?.defName ?? "none") + " → " + use.defName);
			if (tell && p.Faction == Faction.OfPlayer)
				Messages.Message("BF_Repaired".Translate(p.LabelShortCap, b?.defName ?? "-", use.defName), p, MessageTypeDefOf.SilentInput, false);
			return true;
		}
		catch (Exception ex)
		{
			MLog.WarnOnce("[Body Fit] " + ex.Message, 0x0b0d1f17);
			return false;
		}
	}

	// 2.2.1: the body picture the game really built could not be loaded (the pawn is
	// drawn without a body) – only for alien races, which build their own body path
	public static bool DrawnBodyBroken(Pawn p)
	{
		try
		{
			if (!Bodies.IsHar(p.def))
				return false;
			Graphic g = p.Drawer?.renderer?.BodyGraphic;
			return g != null && g.MatSouth == BaseContent.BadMat;
		}
		catch
		{
			return false;
		}
	}

	public static int CheckAll(bool tell)
	{
		int n = 0;
		if (Find.Maps == null)
			return 0;
		foreach (Map m in Find.Maps)
			foreach (Pawn p in m.mapPawns.AllPawnsSpawned)
				if (p.RaceProps.Humanlike && Check(p, tell))
					n++;
		return n;
	}
}

public class BodyFitGameComponent : GameComponent
{
	private int next;

	public BodyFitGameComponent(Game game)
	{
	}

	public override void FinalizeInit()
	{
		next = 0;
		BF.ClearPending();
		// 2.3.1: data for fitting alien races
		Shots.Arm();
		if (Shots.Requested)
			LongEventHandler.ExecuteWhenFinished(Dump.Run);
	}

	private int turn;

	public override void GameComponentUpdate()
	{
		// 2.5.3: one shared budget; the queues take turns going first so none waits for ever
		BF.FrameStart();
		turn = (turn + 1) % 3;
		for (int i = 0; i < 3; i++)
		{
			switch ((turn + i) % 3)
			{
				case 0: BF.ProcessPending(); break;
				case 1: Refit.ProcessPending(); break;
				default: LayerClip.ProcessPending(); break;
			}
		}
		Shots.Update();
	}

	public override void GameComponentTick()
	{
		int now = Find.TickManager.TicksGame;
		if (now < next)
			return;
		next = now + 7500;   // every 3 in-game hours (genes, editors and surgery can change bodies)
		BodyRepair.CheckAll(true);
	}
}
