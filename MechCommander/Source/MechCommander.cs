// Mech Commander – Bolek98
// Upgrade modules for mechanoids (vanilla, DLC and modded mechs): crafted at the fabrication bench
// after research, installed on a mech with a button. Plus a mech overview tab.
using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace MechCommander
{
	// links a module item to the hediff it installs
	public class ModuleExtension : DefModExtension
	{
		public HediffDef hediff;
	}

	[StaticConstructorOnStartup]
	public static class Startup
	{
		public static readonly List<ThingDef> Modules = new List<ThingDef>();
		public const int Slots = 3;

		static Startup()
		{
			try
			{
				foreach (ThingDef d in DefDatabase<ThingDef>.AllDefsListForReading)
					if (d.GetModExtension<ModuleExtension>()?.hediff != null)
						Modules.Add(d);
				var h = new Harmony("misak.mechcommander");
				h.Patch(AccessTools.Method(typeof(MechanitorUtility), nameof(MechanitorUtility.InMechanitorCommandRange)),
					postfix: new HarmonyMethod(typeof(Startup), nameof(CommandRangePostfix)));
				int sites = PatchCallSites(h);
				// 0.1.3: mechs stay controllable while their mechanitor is away (setting, off)
				System.Reflection.MethodInfo cdm = AccessTools.Method(typeof(MechanitorUtility), nameof(MechanitorUtility.CanDraftMech));
				if (cdm != null)
					h.Patch(cdm, postfix: new HarmonyMethod(typeof(Startup), nameof(CanDraftPostfix)));
				h.Patch(AccessTools.Method(typeof(Pawn), nameof(Pawn.GetGizmos)),
					postfix: new HarmonyMethod(typeof(Startup), nameof(GizmosPostfix)));
				// 0.1.2: each mechanitor's command radius in its own colour
				System.Reflection.MethodInfo dcr = AccessTools.Method(typeof(Pawn_MechanitorTracker), nameof(Pawn_MechanitorTracker.DrawCommandRadius));
				if (dcr != null)
					h.Patch(dcr, prefix: new HarmonyMethod(typeof(RadiusColors), nameof(RadiusColors.Prefix)));
				int mechs = DefDatabase<ThingDef>.AllDefsListForReading.Count(d => d.race != null && d.race.IsMechanoid);
				Log.Message("[Mech Commander] v0.1.3 loaded – " + Modules.Count + " modules, " + mechs + " mech kinds (vanilla and mods), relay in " + sites + " order checks.");
			}
			catch (Exception ex)
			{
				Log.Warning("[Mech Commander] could not start: " + ex.Message);
			}
		}

		// 0.1.1: the game may inline the small range check into the code that gives orders,
		// so a patch on it alone is not always seen. Every place that asks it is pointed to
		// our own check instead (relay module included).
		private static int PatchCallSites(Harmony h)
		{
			System.Reflection.MethodInfo orig = AccessTools.Method(typeof(MechanitorUtility), nameof(MechanitorUtility.InMechanitorCommandRange));
			if (orig == null)
				return 0;
			var types = new List<Type>();
			foreach (string n in new[] { "RimWorld.FloatMenuUtility", "RimWorld.FloatMenuOptionProvider_DraftedMove", "RimWorld.FloatMenuOptionProvider_DraftedAttack", "RimWorld.MultiPawnGotoController" })
			{
				Type t = AccessTools.TypeByName(n);
				if (t == null)
					continue;
				types.Add(t);
				types.AddRange(t.GetNestedTypes(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic));
			}
			int n2 = 0;
			var tr = new HarmonyMethod(typeof(Startup), nameof(RangeTranspiler));
			foreach (Type t in types)
				foreach (System.Reflection.MethodInfo m in t.GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly))
				{
					if (m.IsAbstract || m.ContainsGenericParameters)
						continue;
					try
					{
						var body = PatchProcessor.GetOriginalInstructions(m);
						if (body == null || !body.Any(ci => ci.operand is System.Reflection.MethodInfo mi && mi == orig))
							continue;
						h.Patch(m, transpiler: tr);
						n2++;
					}
					catch (Exception ex)
					{
						Log.Warning("[Mech Commander] relay could not hook " + t.Name + "." + m.Name + ": " + ex.Message);
					}
				}
			return n2;
		}

		public static IEnumerable<CodeInstruction> RangeTranspiler(IEnumerable<CodeInstruction> code)
		{
			System.Reflection.MethodInfo orig = AccessTools.Method(typeof(MechanitorUtility), nameof(MechanitorUtility.InMechanitorCommandRange));
			System.Reflection.MethodInfo mine = AccessTools.Method(typeof(Startup), nameof(InRange));
			foreach (CodeInstruction ci in code)
			{
				if (ci.operand is System.Reflection.MethodInfo mi && mi == orig)
					ci.operand = mine;
				yield return ci;
			}
		}

		public static bool InRange(Pawn mech, LocalTargetInfo target)
		{
			if (MechanitorUtility.InMechanitorCommandRange(mech, target))
				return true;
			bool r = false;
			CommandRangePostfix(mech, ref r);
			return r;
		}

		public static bool Has(Pawn p, string hediffDefName)
		{
			var hs = p?.health?.hediffSet?.hediffs;
			if (hs == null)
				return false;
			for (int i = 0; i < hs.Count; i++)
				if (hs[i].def.defName == hediffDefName)
					return true;
			return false;
		}

		public static int Installed(Pawn p) => p?.health?.hediffSet?.hediffs.Count(h => h.def.GetModExtension<ModuleExtension>() != null || Modules.Any(m => m.GetModExtension<ModuleExtension>().hediff == h.def)) ?? 0;

		// relay module: the mech obeys anywhere on its overseer's map
		public static void CommandRangePostfix(Pawn mech, ref bool __result)
		{
			if (__result || mech == null)
				return;
			try
			{
				Pawn o = mech.GetOverseer();
				if (o != null && o.MapHeld == mech.MapHeld && Has(mech, "MC_Hediff_Relay"))
					__result = true;
				// 0.1.3: the mechanitor is in a caravan or on another map
				else if (RemoteControl(mech))
					__result = true;
			}
			catch
			{
			}
		}

		// 0.1.3: "Mechs stay controllable when the mechanitor is away" – the mech's
		// overseer is not on the mech's map (caravan, another map) but is alive,
		// conscious, free, sane and has the mech in his bandwidth.
		public static bool RemoteControl(Pawn mech)
		{
			if (MechCommanderMod.Settings == null || !MechCommanderMod.Settings.remoteControl || mech == null || !mech.IsColonyMech)
				return false;
			Pawn o = mech.GetOverseer();
			if (o?.mechanitor == null || o.Dead || o.Downed || o.IsPrisoner || o.InMentalState)
				return false;
			if (o.Spawned && o.MapHeld == mech.MapHeld)
				return false;   // not away – the game's own rules
			if (!o.Spawned && o.SpawnedParentOrMe is Building)
				return false;   // in a casket, pod … – as the game says
			if (mech.needs?.energy != null && mech.needs.energy.IsLowEnergySelfShutdown)
				return false;
			return o.mechanitor.ControlledPawns.Contains(mech);
		}

		public static void CanDraftPostfix(Pawn mech, ref AcceptanceReport __result)
		{
			if (__result.Accepted)
				return;
			try
			{
				if (RemoteControl(mech))
					__result = AcceptanceReport.WasAccepted;
			}
			catch (Exception ex)
			{
				Log.WarningOnce("[Mech Commander] remote control: " + ex.Message, 0x4d4301);
			}
		}

		public static IEnumerable<Gizmo> GizmosPostfix(IEnumerable<Gizmo> __result, Pawn __instance)
		{
			foreach (Gizmo g in __result)
				yield return g;
			Pawn p = __instance;
			if (p == null || !p.Spawned || p.Faction != Faction.OfPlayer || p.RaceProps == null || !p.RaceProps.IsMechanoid || Find.Selector.NumSelected != 1)
				yield break;
			int used = Installed(p);
			yield return new Command_Action
			{
				defaultLabel = "MC_Install".Translate(used, Slots),
				defaultDesc = "MC_InstallDesc".Translate(),
				icon = ThingDefOf.ComponentIndustrial.uiIcon,
				action = () => InstallMenu(p)
			};
		}

		private static void InstallMenu(Pawn mech)
		{
			var opts = new List<FloatMenuOption>();
			int used = Installed(mech);
			foreach (ThingDef mod in Modules)
			{
				HediffDef hd = mod.GetModExtension<ModuleExtension>().hediff;
				Hediff have = mech.health.hediffSet.GetFirstHediffOfDef(hd);
				if (have != null)
				{
					opts.Add(new FloatMenuOption("MC_Remove".Translate(mod.LabelCap), () =>
					{
						mech.health.RemoveHediff(have);
						GenPlace.TryPlaceThing(ThingMaker.MakeThing(mod), mech.Position, mech.Map, ThingPlaceMode.Near);
					}, mod.uiIcon, mod.uiIconColor));
					continue;
				}
				Thing item = mech.Map.listerThings.ThingsOfDef(mod).FirstOrDefault(t => t.Spawned && !t.IsForbidden(Faction.OfPlayer));
				if (item == null)
				{
					opts.Add(new FloatMenuOption(mod.LabelCap + " – " + "MC_NoneOnMap".Translate(), null, mod.uiIcon, mod.uiIconColor));
					continue;
				}
				if (used >= Slots)
				{
					opts.Add(new FloatMenuOption(mod.LabelCap + " – " + "MC_NoSlot".Translate(Slots), null, mod.uiIcon, mod.uiIconColor));
					continue;
				}
				opts.Add(new FloatMenuOption("MC_InstallOne".Translate(mod.LabelCap), () =>
				{
					Thing one = item.SplitOff(1);
					one.Destroy(DestroyMode.Vanish);
					mech.health.AddHediff(hd);
					Messages.Message("MC_Installed".Translate(mod.LabelCap, mech.LabelShortCap), mech, MessageTypeDefOf.PositiveEvent, false);
				}, mod.uiIcon, mod.uiIconColor));
			}
			if (opts.Count == 0)
				opts.Add(new FloatMenuOption("MC_NoModules".Translate(), null));
			Find.WindowStack.Add(new FloatMenu(opts));
		}
	}

	// 0.1.2: settings (only the radius colours so far)
	public class MCSettings : ModSettings
	{
		public bool colorRadius = true;
		// 0.1.3
		public bool remoteControl;

		public override void ExposeData()
		{
			Scribe_Values.Look(ref colorRadius, "colorRadius", true);
			Scribe_Values.Look(ref remoteControl, "remoteControl", false);
		}
	}

	public class MechCommanderMod : Mod
	{
		public static MCSettings Settings;

		public MechCommanderMod(ModContentPack content) : base(content)
		{
			Settings = GetSettings<MCSettings>();
		}

		public override string SettingsCategory() => "Mech Commander";

		public override void DoSettingsWindowContents(Rect r)
		{
			var l = new Listing_Standard();
			l.Begin(r);
			l.CheckboxLabeled("MC_ColorRadius".Translate(), ref Settings.colorRadius, "MC_ColorRadiusDesc".Translate());
			l.CheckboxLabeled("MC_RemoteControl".Translate(), ref Settings.remoteControl, "MC_RemoteControlDesc".Translate());
			l.End();
		}
	}

	// 0.1.2: with several mechanitors on one map their command radii are told apart by
	// colour – the first (oldest) one keeps the game's white, the others get a colour
	// from a fixed palette by their order. Only the colour of the ring changes.
	public static class RadiusColors
	{
		private static readonly Color[] Palette =
		{
			Color.white,
			new Color(0.35f, 0.8f, 1f),
			new Color(1f, 0.65f, 0.25f),
			new Color(0.55f, 1f, 0.45f),
			new Color(1f, 0.45f, 0.8f),
			new Color(1f, 0.95f, 0.35f),
			new Color(0.7f, 0.55f, 1f),
		};
		private static readonly List<Pawn> order = new List<Pawn>();
		private static Map orderMap;
		private static int orderFrame = -1;
		private static bool failed;

		public static bool Prefix(Pawn_MechanitorTracker __instance)
		{
			if (failed || MechCommanderMod.Settings == null || !MechCommanderMod.Settings.colorRadius)
				return true;
			try
			{
				Pawn p = __instance.Pawn;
				if (p == null || !p.Spawned || !__instance.AnySelectedDraftedMechs)
					return false;
				Color c = Palette[Index(p) % Palette.Length];
				GenDraw.DrawRadiusRing(p.Position, 24.9f, c, (IntVec3 cell) => __instance.CanCommandTo(cell));
				return false;
			}
			catch (Exception ex)
			{
				failed = true;
				Log.Warning("[Mech Commander] radius colours turned off: " + ex.Message);
				return true;
			}
		}

		private static int Index(Pawn p)
		{
			Map map = p.Map;
			if (orderFrame != Time.frameCount || orderMap != map)
			{
				orderFrame = Time.frameCount;
				orderMap = map;
				order.Clear();
				foreach (Pawn q in map.mapPawns.FreeColonistsSpawned)
					if (q.mechanitor != null)
						order.Add(q);
				order.Sort((a, b) => a.thingIDNumber.CompareTo(b.thingIDNumber));
			}
			int i = order.IndexOf(p);
			return i < 0 ? 0 : i;
		}
	}

	// self-repair nanites: injuries heal slowly on their own
	public class HediffCompProperties_SelfRepair : HediffCompProperties
	{
		public int interval = 600;
		public float amount = 2f;
		public HediffCompProperties_SelfRepair() { compClass = typeof(HediffComp_SelfRepair); }
	}

	public class HediffComp_SelfRepair : HediffComp
	{
		public HediffCompProperties_SelfRepair Props => (HediffCompProperties_SelfRepair)props;

		public override void CompPostTick(ref float severityAdjustment)
		{
			Pawn p = Pawn;
			if (p == null || !p.IsHashIntervalTick(Props.interval) || p.Dead)
				return;
			Hediff_Injury worst = null;
			foreach (Hediff h in p.health.hediffSet.hediffs)
				if (h is Hediff_Injury inj && (worst == null || inj.Severity > worst.Severity))
					worst = inj;
			worst?.Heal(Props.amount);
		}
	}

	// tactical processor: a badly hurt mech that is not drafted leaves the fight and goes to its overseer
	public class HediffCompProperties_Tactics : HediffCompProperties
	{
		public float retreatBelow = 0.4f;
		public HediffCompProperties_Tactics() { compClass = typeof(HediffComp_Tactics); }
	}

	public class HediffComp_Tactics : HediffComp
	{
		private int lastRetreat = -99999;
		public HediffCompProperties_Tactics Props => (HediffCompProperties_Tactics)props;

		public override void CompPostTick(ref float severityAdjustment)
		{
			Pawn p = Pawn;
			if (p == null || !p.Spawned || p.Dead || p.Downed || !p.IsHashIntervalTick(250) || p.Drafted)
				return;
			int now = Find.TickManager.TicksGame;
			if (now - lastRetreat < 2500 || p.health.summaryHealth.SummaryHealthPercent >= Props.retreatBelow)
				return;
			if (!GenAI.EnemyIsNear(p, 20f))
				return;
			IntVec3 to = IntVec3.Invalid;
			Pawn o = p.GetOverseer();
			if (o != null && o.Spawned && o.Map == p.Map && !GenAI.EnemyIsNear(o, 15f))
				to = o.Position;
			if (!to.IsValid && !RCellFinder.TryFindDirectFleeDestination(p.Position, 25f, p, out to))
				return;
			lastRetreat = now;
			Job j = JobMaker.MakeJob(JobDefOf.Goto, to);
			j.locomotionUrgency = LocomotionUrgency.Sprint;
			j.expiryInterval = 1200;
			p.jobs.StartJob(j, JobCondition.InterruptForced);
			Messages.Message("MC_Retreat".Translate(p.LabelShortCap), p, MessageTypeDefOf.NeutralEvent, false);
		}
	}

	// ---------- overview tab
	public class MainTabWindow_Mechs : MainTabWindow
	{
		private Vector2 scroll;
		public override Vector2 RequestedTabSize => new Vector2(980f, 520f);

		public override void DoWindowContents(Rect inRect)
		{
			Map map = Find.CurrentMap;
			var mechs = map?.mapPawns.SpawnedColonyMechs?.ToList() ?? new List<Pawn>();
			Text.Font = GameFont.Medium;
			Widgets.Label(new Rect(inRect.x, inRect.y, 400f, 32f), "MC_TabTitle".Translate(mechs.Count));
			Text.Font = GameFont.Small;
			float y = inRect.y + 38f;
			float[] cols = { 0f, 200f, 330f, 430f, 530f, 680f };
			string[] heads = { "MC_ColName", "MC_ColOverseer", "MC_ColEnergy", "MC_ColHealth", "MC_ColWork", "MC_ColModules" };
			GUI.color = Color.gray;
			for (int i = 0; i < cols.Length; i++)
				Widgets.Label(new Rect(inRect.x + cols[i], y, 160f, 24f), heads[i].Translate());
			GUI.color = Color.white;
			y += 26f;
			Rect outer = new Rect(inRect.x, y, inRect.width, inRect.yMax - y);
			Rect view = new Rect(0f, 0f, outer.width - 18f, mechs.Count * 28f);
			Widgets.BeginScrollView(outer, ref scroll, view);
			for (int i = 0; i < mechs.Count; i++)
			{
				Pawn m = mechs[i];
				Rect row = new Rect(0f, i * 28f, view.width, 26f);
				if (i % 2 == 1)
					Widgets.DrawLightHighlight(row);
				Widgets.DrawHighlightIfMouseover(row);
				Widgets.Label(new Rect(cols[0] + 4f, row.y + 3f, 190f, 24f), m.LabelShortCap);
				Widgets.Label(new Rect(cols[1], row.y + 3f, 125f, 24f), m.GetOverseer()?.LabelShortCap ?? "-");
				float e = m.needs?.energy?.CurLevelPercentage ?? 0f;
				GUI.color = e < 0.2f ? new Color(1f, 0.5f, 0.4f) : Color.white;
				Widgets.Label(new Rect(cols[2], row.y + 3f, 95f, 24f), (e * 100f).ToString("0") + " %");
				float hp = m.health.summaryHealth.SummaryHealthPercent;
				GUI.color = hp < 0.5f ? new Color(1f, 0.5f, 0.4f) : Color.white;
				Widgets.Label(new Rect(cols[3], row.y + 3f, 95f, 24f), (hp * 100f).ToString("0") + " %");
				GUI.color = Color.white;
				Widgets.Label(new Rect(cols[4], row.y + 3f, 145f, 24f), m.GetMechWorkMode()?.LabelCap ?? "-");
				string mods = string.Join(", ", m.health.hediffSet.hediffs.Where(h => Startup.Modules.Any(x => x.GetModExtension<ModuleExtension>().hediff == h.def)).Select(h => h.LabelCap).ToArray());
				Widgets.Label(new Rect(cols[5], row.y + 3f, view.width - cols[5], 24f), mods.NullOrEmpty() ? "-" : mods);
				if (Widgets.ButtonInvisible(row))
				{
					CameraJumper.TryJumpAndSelect(m);
					Close();
				}
			}
			Widgets.EndScrollView();
		}
	}
}
