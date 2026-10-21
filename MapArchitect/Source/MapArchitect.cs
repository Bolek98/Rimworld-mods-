// Map Architect – Bolek98
// Instant, free map editing: terrain, plants, mountains, clearing, deep resources, objects (geysers,
// ancient things, ores …) and animals, with brush shapes and undo (Ctrl+Z).
using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace MapArchitect
{
	// ------------------------------------------------------------------ undo
	public class RemovedThing
	{
		public ThingDef def, stuff;
		public IntVec3 pos;
		public Rot4 rot;
		public int stack = 1;
		public float growth = -1f;
		public int age = -1;
	}

	public class Change
	{
		public Map map;
		public readonly Dictionary<IntVec3, KeyValuePair<TerrainDef, TerrainDef>> terrain = new Dictionary<IntVec3, KeyValuePair<TerrainDef, TerrainDef>>();
		public readonly Dictionary<IntVec3, RoofDef> roofs = new Dictionary<IntVec3, RoofDef>();
		public readonly Dictionary<IntVec3, KeyValuePair<ThingDef, int>> deep = new Dictionary<IntVec3, KeyValuePair<ThingDef, int>>();
		public readonly List<Thing> spawned = new List<Thing>();
		public readonly List<RemovedThing> removed = new List<RemovedThing>();
		public readonly Dictionary<IntVec3, bool> fog = new Dictionary<IntVec3, bool>();
		public readonly Dictionary<IntVec3, float> snow = new Dictionary<IntVec3, float>();
		public readonly Dictionary<IntVec3, bool> pollution = new Dictionary<IntVec3, bool>();
		public readonly List<KeyValuePair<Thing, int>> hp = new List<KeyValuePair<Thing, int>>();
		public bool Empty => terrain.Count == 0 && roofs.Count == 0 && deep.Count == 0 && spawned.Count == 0 && removed.Count == 0 && fog.Count == 0
			&& snow.Count == 0 && pollution.Count == 0 && hp.Count == 0;
	}

	public static class Undo
	{
		private static readonly List<Change> stack = new List<Change>();
		public static Change Current;
		public const int Max = 40;

		public static int Count => stack.Count;

		public static Change Begin(Map map)
		{
			Current = new Change { map = map };
			return Current;
		}

		public static void End()
		{
			if (Current != null && !Current.Empty)
			{
				stack.Add(Current);
				if (stack.Count > Max)
					stack.RemoveAt(0);
			}
			Current = null;
		}

		public static void Clear() => stack.Clear();

		public static void Terrain(Map map, IntVec3 c)
		{
			if (Current != null && !Current.terrain.ContainsKey(c))
				Current.terrain[c] = new KeyValuePair<TerrainDef, TerrainDef>(map.terrainGrid.TopTerrainAt(c), map.terrainGrid.UnderTerrainAt(c));
		}

		public static void Roof(Map map, IntVec3 c)
		{
			if (Current != null && !Current.roofs.ContainsKey(c))
				Current.roofs[c] = map.roofGrid.RoofAt(c);
		}

		public static void Deep(Map map, IntVec3 c)
		{
			if (Current != null && !Current.deep.ContainsKey(c))
				Current.deep[c] = new KeyValuePair<ThingDef, int>(map.deepResourceGrid.ThingDefAt(c), map.deepResourceGrid.CountAt(c));
		}

		public static void Fog(Map map, IntVec3 c)
		{
			if (Current != null && !Current.fog.ContainsKey(c))
				Current.fog[c] = map.fogGrid.IsFogged(c);
		}

		public static void Snow(Map map, IntVec3 c)
		{
			if (Current != null && !Current.snow.ContainsKey(c))
				Current.snow[c] = map.snowGrid.GetDepth(c);
		}

		public static void Pollution(Map map, IntVec3 c)
		{
			if (Current != null && !Current.pollution.ContainsKey(c))
				Current.pollution[c] = map.pollutionGrid.IsPolluted(c);
		}

		public static void Hp(Thing t)
		{
			if (Current != null && t != null)
				Current.hp.Add(new KeyValuePair<Thing, int>(t, t.HitPoints));
		}

		public static void Spawned(Thing t)
		{
			if (Current != null && t != null)
				Current.spawned.Add(t);
		}

		public static void Removing(Thing t)
		{
			if (Current == null || t == null || t is Pawn)
				return;
			var r = new RemovedThing { def = t.def, stuff = t.Stuff, pos = t.Position, rot = t.Rotation, stack = t.stackCount };
			if (t is Plant p)
			{
				r.growth = p.Growth;
				r.age = p.Age;
			}
			Current.removed.Add(r);
		}

		public static void UndoLast()
		{
			if (stack.Count == 0)
			{
				Messages.Message("MA_NothingToUndo".Translate(), MessageTypeDefOf.RejectInput, false);
				return;
			}
			Change ch = stack[stack.Count - 1];
			stack.RemoveAt(stack.Count - 1);
			Map map = ch.map;
			if (map == null || !Find.Maps.Contains(map))
				return;
			foreach (Thing t in ch.spawned)
				if (t != null && !t.Destroyed)
					t.Destroy(DestroyMode.Vanish);
			foreach (var kv in ch.terrain)
				Edit.SetTerrainRaw(map, kv.Key, kv.Value.Key, kv.Value.Value);
			foreach (var kv in ch.roofs)
				map.roofGrid.SetRoof(kv.Key, kv.Value);
			foreach (var kv in ch.snow)
				map.snowGrid.SetDepth(kv.Key, kv.Value);
			foreach (var kv in ch.pollution)
				try { map.pollutionGrid.SetPolluted(kv.Key, kv.Value, true); } catch { }
			foreach (var kv in ch.hp)
				if (kv.Key != null && !kv.Key.Destroyed)
					kv.Key.HitPoints = kv.Value;
			foreach (var kv in ch.fog)
			{
				if (kv.Value)
					map.fogGrid.Refog(CellRect.SingleCell(kv.Key));
				else
					map.fogGrid.Unfog(kv.Key);
			}
			foreach (var kv in ch.deep)
				map.deepResourceGrid.SetAt(kv.Key, kv.Value.Key, kv.Value.Key == null ? 0 : kv.Value.Value);
			foreach (RemovedThing r in ch.removed)
			{
				try
				{
					if (!r.pos.InBounds(map))
						continue;
					Thing t = ThingMaker.MakeThing(r.def, r.def.MadeFromStuff ? (r.stuff ?? GenStuff.DefaultStuffFor(r.def)) : null);
					t.stackCount = Math.Max(1, Math.Min(r.stack, t.def.stackLimit));
					if (t is Plant p && r.growth >= 0f)
					{
						p.Growth = r.growth;
						p.Age = Math.Max(0, r.age);
					}
					GenSpawn.Spawn(t, r.pos, map, r.rot, WipeMode.VanishOrMoveAside);
				}
				catch
				{
				}
			}
			Messages.Message("MA_Undone".Translate(stack.Count), MessageTypeDefOf.SilentInput, false);
		}
	}

	// ------------------------------------------------------------------ editing helpers
	public static class Edit
	{
		public static bool Protected(Thing t)
		{
			if (t is Pawn)
				return true;
			if (t.Faction == Faction.OfPlayer && t.def.category == ThingCategory.Building)
				return true;
			return false;
		}

		public static void SetTerrainRaw(Map map, IntVec3 c, TerrainDef top, TerrainDef under)
		{
			try
			{
				if (under != null)
				{
					map.terrainGrid.SetTerrain(c, under);
					if (top != null && top != under)
						map.terrainGrid.SetTerrain(c, top);
				}
				else if (top != null)
				{
					map.terrainGrid.SetTerrain(c, top);
					if (map.terrainGrid.UnderTerrainAt(c) != null && !top.layerable)
						map.terrainGrid.SetUnderTerrain(c, null);
				}
			}
			catch
			{
			}
		}

		public static void Terrain(Map map, IntVec3 c, TerrainDef def)
		{
			if (map.terrainGrid.TopTerrainAt(c) == def)
				return;
			// don't drown buildings or people: water / impassable terrain only where nothing stands
			if (def.passability == Traversability.Impassable || def.IsWater)
			{
				foreach (Thing t in c.GetThingList(map))
					if (Protected(t))
						return;
			}
			Undo.Terrain(map, c);
			if (def.layerable)
				map.terrainGrid.SetTerrain(c, def);
			else
				SetTerrainRaw(map, c, def, null);
		}

		public static void Remove(Thing t)
		{
			if (t == null || t.Destroyed || Protected(t))
				return;
			Undo.Removing(t);
			t.Destroy(DestroyMode.Vanish);
		}

		public static Thing Spawn(Thing t, IntVec3 c, Map map, Rot4 rot)
		{
			Thing s = GenSpawn.Spawn(t, c, map, rot, WipeMode.Vanish);
			Undo.Spawned(s);
			return s;
		}

		public static void Roof(Map map, IntVec3 c, RoofDef def)
		{
			if (map.roofGrid.RoofAt(c) == def)
				return;
			Undo.Roof(map, c);
			map.roofGrid.SetRoof(c, def);
		}
	}

	// ------------------------------------------------------------------ picker window
	public class PickOption
	{
		public string label;
		public string group;
		public object value;
		public Texture2D icon;
		public Color iconColor = Color.white;
		public string mod;   // 0.4.0: which mod it comes from (filter)
	}

	public class Dialog_Pick : Window
	{
		private readonly List<PickOption> all;
		private readonly Action<PickOption> chosen;
		private readonly string title;
		private string search = "";
		private string group;
		private Vector2 scroll;
		private readonly List<string> groups;
		public static float sliderValue = 0.5f;
		private readonly string sliderLabel;
		private string mod;
		private readonly List<string> mods;
		private bool focused;
		private readonly Func<Rect, float> extra;   // 0.4.0: extra controls under the search (returns used height)

		public override Vector2 InitialSize => new Vector2(Mathf.Min(820f, UI.screenWidth - 40f), Mathf.Min(860f, UI.screenHeight - 40f));

		public Dialog_Pick(string title, List<PickOption> options, Action<PickOption> chosen, string sliderLabel = null, Func<Rect, float> extra = null)
		{
			this.title = title;
			this.extra = extra;
			all = options.OrderBy(o => o.label).ToList();
			foreach (PickOption o in all)
				if (o.mod == null && o.value is Def d)
					o.mod = d.modContentPack?.Name ?? "?";
			mods = all.Select(o => o.mod).Where(m => !m.NullOrEmpty()).Distinct().OrderBy(m => m == "Core" ? "" : m).ToList();
			this.chosen = chosen;
			this.sliderLabel = sliderLabel;
			groups = all.Select(o => o.group).Where(g => !g.NullOrEmpty()).Distinct().OrderBy(g => g).ToList();
			doCloseX = true;
			closeOnClickedOutside = true;
			absorbInputAroundWindow = true;
			forcePause = true;
		}

		public override void DoWindowContents(Rect inRect)
		{
			float y = inRect.y;
			Text.Font = GameFont.Medium;
			Widgets.Label(new Rect(inRect.x, y, inRect.width, 32f), title);
			Text.Font = GameFont.Small;
			y += 36f;
			GUI.SetNextControlName("MA_Search");
			search = Widgets.TextField(new Rect(inRect.x, y, inRect.width, 28f), search);
			if (!focused && Event.current.type == EventType.Layout)
			{
				GUI.FocusControl("MA_Search");
				focused = true;
			}
			y += 32f;
			if (mods.Count > 1)
			{
				Widgets.Label(new Rect(inRect.x, y + 3f, 60f, 24f), "MA_Mod".Translate());
				if (Widgets.ButtonText(new Rect(inRect.x + 60f, y, inRect.width - 60f, 26f), mod ?? (string)"MA_AllMods".Translate()))
				{
					var opts = new List<FloatMenuOption> { new FloatMenuOption("MA_AllMods".Translate(), () => mod = null) };
					foreach (string m in mods)
					{
						string mm = m;
						opts.Add(new FloatMenuOption(m + " (" + all.Count(o => o.mod == mm) + ")", () => mod = mm));
					}
					Find.WindowStack.Add(new FloatMenu(opts));
				}
				y += 30f;
			}
			if (extra != null)
				y += extra(new Rect(inRect.x, y, inRect.width, 30f));
			if (sliderLabel != null)
			{
				Widgets.Label(new Rect(inRect.x, y, 200f, 26f), sliderLabel + ": " + (sliderValue * 100f).ToString("0") + " %");
				sliderValue = Widgets.HorizontalSlider(new Rect(inRect.x + 210f, y + 6f, inRect.width - 210f, 20f), sliderValue, 0.05f, 1f);
				y += 30f;
			}
			if (groups.Count > 1 && groups.Count <= 8)
			{
				float bx = inRect.x;
				float by = y;
				foreach (string g in new[] { (string)null }.Concat(groups))
				{
					string lab = g ?? (string)"MA_All".Translate();
					float w = Text.CalcSize(lab).x + 16f;
					if (bx + w > inRect.xMax)
					{
						bx = inRect.x;
						by += 28f;
					}
					GUI.color = group == g ? new Color(0.55f, 1f, 0.55f) : Color.white;
					if (Widgets.ButtonText(new Rect(bx, by, w, 26f), lab))
						group = g;
					GUI.color = Color.white;
					bx += w + 4f;
				}
				y = by + 32f;
			}
			else if (groups.Count > 8)
			{
				// 0.4.1: many groups (factions, categories…) – a drop-down, so the list keeps its space
				Widgets.Label(new Rect(inRect.x, y + 3f, 60f, 24f), "MA_Group".Translate());
				if (Widgets.ButtonText(new Rect(inRect.x + 60f, y, inRect.width - 60f, 26f), group ?? (string)"MA_All".Translate()))
				{
					var opts = new List<FloatMenuOption> { new FloatMenuOption("MA_All".Translate() + " (" + all.Count + ")", () => group = null) };
					foreach (string g in groups)
					{
						string gg = g;
						opts.Add(new FloatMenuOption(g + " (" + all.Count(o => o.group == gg) + ")", () => group = gg));
					}
					Find.WindowStack.Add(new FloatMenu(opts));
				}
				y += 30f;
			}
			string s = search.Trim();
			var list = all.Where(o => (group == null || o.group == group) && (mod == null || o.mod == mod)
				&& (s.Length == 0 || o.label.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0 || (o.mod != null && o.mod.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0))).ToList();
			// count of results, so it is clear the search works
			GUI.color = new Color(0.6f, 0.65f, 0.7f);
			Text.Font = GameFont.Tiny;
			Widgets.Label(new Rect(inRect.x, y, inRect.width, 20f), "MA_Found".Translate(list.Count));
			Text.Font = GameFont.Small;
			GUI.color = Color.white;
			y += 20f;
			Rect outer = new Rect(inRect.x, y, inRect.width, Mathf.Max(60f, inRect.yMax - y));
			Rect view = new Rect(0f, 0f, outer.width - 18f, list.Count * 30f);
			Widgets.BeginScrollView(outer, ref scroll, view);
			for (int i = 0; i < list.Count; i++)
			{
				Rect row = new Rect(0f, i * 30f, view.width, 28f);
				if (row.yMax < scroll.y || row.y > scroll.y + outer.height)
					continue;
				if (i % 2 == 1)
					Widgets.DrawLightHighlight(row);
				Widgets.DrawHighlightIfMouseover(row);
				float lx = 4f;
				if (list[i].icon != null)
				{
					GUI.color = list[i].iconColor;
					GUI.DrawTexture(new Rect(row.x + 2f, row.y + 2f, 24f, 24f), list[i].icon);
					GUI.color = Color.white;
					lx = 32f;
				}
				Widgets.Label(new Rect(row.x + lx, row.y + 4f, row.width - lx - 180f, 24f), list[i].label.Truncate(row.width - lx - 180f));
				if (!list[i].mod.NullOrEmpty())
				{
					GUI.color = new Color(0.6f, 0.65f, 0.7f);
					Text.Font = GameFont.Tiny;
					Text.Anchor = TextAnchor.MiddleRight;
					Widgets.Label(new Rect(row.xMax - 178f, row.y, 174f, 28f), list[i].mod.Truncate(174f));
					Text.Anchor = TextAnchor.UpperLeft;
					Text.Font = GameFont.Small;
					GUI.color = Color.white;
				}
				if (Widgets.ButtonInvisible(row))
				{
					chosen(list[i]);
					Close();
				}
			}
			Widgets.EndScrollView();
		}
	}

	// ------------------------------------------------------------------ designators
	public abstract class Designator_MA : Designator
	{
		protected object choice;
		protected string choiceLabel;

		protected Designator_MA()
		{
			useMouseIcon = true;
			soundDragSustain = SoundDefOf.Designate_DragStandard;
			soundDragChanged = SoundDefOf.Designate_DragStandard_Changed;
			soundSucceeded = SoundDefOf.Designate_PlaceBuilding;
			defaultIconColor = new Color(0.6f, 0.9f, 1f);
		}

		protected static Texture2D Tex(string path) => ContentFinder<Texture2D>.Get(path, false) ?? BaseContent.WhiteTex;

		protected abstract string BaseLabel { get; }
		protected virtual bool NeedsChoice => true;

		// quick buttons: a fixed choice, their own label and an icon looked up when first drawn
		protected virtual object Preset => null;
		protected virtual string PresetLabel => null;
		protected virtual Texture2D LazyIcon() => null;
		private bool iconDone;

		public override GizmoResult GizmoOnGUI(Vector2 topLeft, float maxWidth, GizmoRenderParms parms)
		{
			if (!iconDone)
			{
				iconDone = true;
				try
				{
					Texture2D t = LazyIcon();
					if (t != null && t != BaseContent.BadTex)
					{
						icon = t;
						defaultIconColor = Color.white;
					}
				}
				catch
				{
				}
			}
			if (Preset != null && choice == null)
			{
				choice = Preset;
				if (PresetLabel != null)
					defaultLabel = PresetLabel;
			}
			return base.GizmoOnGUI(topLeft, maxWidth, parms);
		}
		protected virtual List<PickOption> Options() => new List<PickOption>();
		protected virtual Func<Rect, float> ExtraUI => null;
		// return true when the designator selects itself later (e.g. after a second picker)
		protected virtual bool AfterPick(PickOption o) => false;
		protected virtual string SliderLabel => null;

		public override string Label => PresetLabel ?? (choiceLabel.NullOrEmpty() ? BaseLabel : BaseLabel + ": " + choiceLabel);

		public override DrawStyleCategoryDef DrawStyleCategory => DrawStyleCategoryDefOf.Floors;

		public override bool DragDrawMeasurements => true;

		public override void ProcessInput(Event ev)
		{
			if (Preset == null && NeedsChoice && (choice == null || ev.button == 1))
			{
				Find.WindowStack.Add(new Dialog_Pick(BaseLabel, Options(), o =>
				{
					choice = o.value;
					choiceLabel = o.label;
					if (!AfterPick(o))
						Find.DesignatorManager.Select(this);
				}, SliderLabel, ExtraUI));
				return;
			}
			base.ProcessInput(ev);
		}

		public override AcceptanceReport CanDesignateCell(IntVec3 c) => c.InBounds(Map);

		public override void DesignateSingleCell(IntVec3 c) => DesignateMultiCell(new[] { c });

		public override void DesignateMultiCell(IEnumerable<IntVec3> cells)
		{
			if (choice == null && Preset != null)
				choice = Preset;
			Undo.Begin(Map);
			try
			{
				Apply(cells.Where(c => c.InBounds(Map)).ToList());
			}
			catch (Exception ex)
			{
				Log.Warning("[Map Architect] " + GetType().Name + ": " + ex.Message);
			}
			Undo.End();
		}

		protected abstract void Apply(List<IntVec3> cells);

		public override void SelectedUpdate()
		{
			GenUI.RenderMouseoverBracket();
		}
	}

	public class Designator_MA_Terrain : Designator_MA
	{
		public Designator_MA_Terrain()
		{
			defaultLabel = "MA_Terrain".Translate();
			defaultDesc = "MA_TerrainDesc".Translate();
			icon = Tex("UI/Designators/SmoothSurface");
		}

		protected override string BaseLabel => "MA_Terrain".Translate();

		protected override List<PickOption> Options()
		{
			return DefDatabase<TerrainDef>.AllDefsListForReading.Where(t => !t.label.NullOrEmpty())
				.Select(t => new PickOption
				{
					label = t.LabelCap + (t.modContentPack != null && !t.modContentPack.IsOfficialMod ? " (" + t.modContentPack.Name + ")" : ""),
					group = t.IsWater ? (string)"MA_GroupWater".Translate() : t.layerable ? (string)"MA_GroupFloors".Translate() : t.fertility > 0f ? (string)"MA_GroupSoil".Translate() : (string)"MA_GroupNatural".Translate(),
					value = t,
					icon = BaseContent.WhiteTex,
					iconColor = t.uiIconColor
				}).ToList();
		}

		protected override void Apply(List<IntVec3> cells)
		{
			var def = choice as TerrainDef;
			if (def == null)
				return;
			foreach (IntVec3 c in cells)
				Edit.Terrain(Map, c, def);
		}
	}

	public class Designator_MA_Plants : Designator_MA
	{
		public Designator_MA_Plants()
		{
			defaultLabel = "MA_Plants".Translate();
			defaultDesc = "MA_PlantsDesc".Translate();
			icon = Tex("UI/Designators/ZoneCreate_Growing");
		}

		protected override string BaseLabel => "MA_Plants".Translate();
		protected override string SliderLabel => "MA_Density".Translate();

		protected override List<PickOption> Options()
		{
			return DefDatabase<ThingDef>.AllDefsListForReading.Where(d => d.plant != null && d.category == ThingCategory.Plant && !d.IsCorpse && !d.label.NullOrEmpty())
				.Select(d => new PickOption
				{
					label = d.LabelCap,
					group = d.plant.IsTree ? (string)"MA_GroupTrees".Translate() : d.plant.Sowable ? (string)"MA_GroupCrops".Translate() : (string)"MA_GroupWild".Translate(),
					value = d,
					icon = d.uiIcon,
					iconColor = d.uiIconColor
				}).ToList();
		}

		protected override void Apply(List<IntVec3> cells)
		{
			var def = choice as ThingDef;
			if (def == null)
				return;
			float density = cells.Count == 1 ? 1f : Dialog_Pick.sliderValue;
			foreach (IntVec3 c in cells)
			{
				if (!Rand.Chance(density) || c.GetPlant(Map) != null || c.GetEdifice(Map) != null || c.Impassable(Map))
					continue;
				Plant p = (Plant)ThingMaker.MakeThing(def);
				p.Growth = Rand.Range(0.35f, 1f);
				if (def.plant.LimitedLifespan)
					p.Age = Rand.Range(0, Math.Max(1, def.plant.LifespanTicks / 2));
				Edit.Spawn(p, c, Map, Rot4.North);
			}
		}
	}

	public enum ClearMode { Plants, Rocks, Filth, Items, Everything }

	public class Designator_MA_Clear : Designator_MA
	{
		public Designator_MA_Clear()
		{
			defaultLabel = "MA_Clear".Translate();
			defaultDesc = "MA_ClearDesc".Translate();
			icon = Tex("UI/Designators/Deconstruct");
			soundSucceeded = SoundDefOf.Designate_Deconstruct;
		}

		protected override string BaseLabel => "MA_Clear".Translate();

		protected override List<PickOption> Options()
		{
			return Enum.GetValues(typeof(ClearMode)).Cast<ClearMode>().Select(m => new PickOption { label = ("MA_Clear_" + m).Translate(), value = m }).ToList();
		}

		protected override void Apply(List<IntVec3> cells)
		{
			if (!(choice is ClearMode mode))
				return;
			foreach (IntVec3 c in cells)
			{
				foreach (Thing t in c.GetThingList(Map).ToList())
				{
					bool ok;
					switch (mode)
					{
					case ClearMode.Plants: ok = t is Plant; break;
					case ClearMode.Rocks: ok = t.def.building != null && t.def.building.isNaturalRock || t.def.thingCategories != null && t.def.thingCategories.Any(tc => tc.defName == "StoneChunks"); break;
					case ClearMode.Filth: ok = t is Filth; break;
					case ClearMode.Items: ok = t.def.category == ThingCategory.Item; break;
					default: ok = !(t is Pawn) && t.def.category != ThingCategory.Mote && t.def.category != ThingCategory.Ethereal; break;
					}
					if (ok)
						Edit.Remove(t);
				}
				if ((mode == ClearMode.Rocks || mode == ClearMode.Everything) && Map.roofGrid.RoofAt(c) is RoofDef r && r.isNatural && c.GetEdifice(Map) == null)
					Edit.Roof(Map, c, null);
			}
		}
	}

	public class Designator_MA_Mountain : Designator_MA
	{
		public Designator_MA_Mountain()
		{
			defaultLabel = "MA_Mountain".Translate();
			defaultDesc = "MA_MountainDesc".Translate();
			icon = Tex("UI/Designators/Mine");
		}

		protected override string BaseLabel => "MA_Mountain".Translate();

		protected override List<PickOption> Options()
		{
			var list = DefDatabase<ThingDef>.AllDefsListForReading.Where(d => d.building != null && d.building.isNaturalRock && !d.label.NullOrEmpty())
				.Select(d => new PickOption
				{
					label = d.LabelCap,
					group = d.building.isResourceRock ? (string)"MA_GroupOre".Translate() : (string)"MA_GroupRock".Translate(),
					value = d,
					icon = d.uiIcon,
					iconColor = d.uiIconColor
				}).ToList();
			list.Add(new PickOption { label = "MA_RoofThick".Translate(), group = "MA_GroupRoof".Translate(), value = RoofDefOf.RoofRockThick });
			list.Add(new PickOption { label = "MA_RoofThin".Translate(), group = "MA_GroupRoof".Translate(), value = RoofDefOf.RoofRockThin });
			list.Add(new PickOption { label = "MA_RoofNone".Translate(), group = "MA_GroupRoof".Translate(), value = "noroof" });
			return list;
		}

		protected override void Apply(List<IntVec3> cells)
		{
			if (choice is RoofDef roof)
			{
				foreach (IntVec3 c in cells)
					Edit.Roof(Map, c, roof);
				return;
			}
			if (choice as string == "noroof")
			{
				foreach (IntVec3 c in cells)
					if (Map.roofGrid.RoofAt(c) is RoofDef r && r.isNatural)
						Edit.Roof(Map, c, null);
				return;
			}
			var def = choice as ThingDef;
			if (def == null)
				return;
			var set = new HashSet<IntVec3>(cells);
			foreach (IntVec3 c in cells)
			{
				if (c.GetThingList(Map).Any(Edit.Protected))
					continue;
				Thing old = c.GetEdifice(Map);
				if (old != null)
				{
					if (old.def == def)
						continue;
					Edit.Remove(old);
				}
				foreach (Thing t in c.GetThingList(Map).ToList())
					if (t is Plant || t.def.category == ThingCategory.Item || t is Filth)
						Edit.Remove(t);
				Edit.Spawn(ThingMaker.MakeThing(def), c, Map, Rot4.North);
				// thick roof inside the mountain, thin on its edge – like a generated mountain
				bool inner = GenAdj.CellsAdjacent8Way(new TargetInfo(c, Map)).All(n => set.Contains(n) || n.GetEdifice(Map)?.def.building?.isNaturalRock == true);
				Edit.Roof(Map, c, inner ? RoofDefOf.RoofRockThick : RoofDefOf.RoofRockThin);
			}
		}
	}

	public class Designator_MA_Deep : Designator_MA
	{
		public Designator_MA_Deep()
		{
			defaultLabel = "MA_Deep".Translate();
			defaultDesc = "MA_DeepDesc".Translate();
			icon = Tex("UI/Designators/Mine");
			defaultIconColor = new Color(1f, 0.85f, 0.4f);
		}

		protected override string BaseLabel => "MA_Deep".Translate();

		protected override List<PickOption> Options()
		{
			var list = DefDatabase<ThingDef>.AllDefsListForReading.Where(d => d.deepCommonality > 0f && !d.label.NullOrEmpty())
				.Select(d => new PickOption { label = d.LabelCap, value = d, icon = d.uiIcon, iconColor = d.uiIconColor }).ToList();
			list.Add(new PickOption { label = "MA_DeepRemove".Translate(), value = "none" });
			return list;
		}

		protected override void Apply(List<IntVec3> cells)
		{
			foreach (IntVec3 c in cells)
			{
				Undo.Deep(Map, c);
				if (choice is ThingDef d)
					Map.deepResourceGrid.SetAt(c, d, Math.Max(1, d.deepCountPerCell));
				else
					Map.deepResourceGrid.SetAt(c, null, 0);
			}
			Map.deepResourceGrid.MarkForDraw();
		}
	}

	// 0.4.0: everything that can be on a map, split into families (each its own button)
	public enum ObjFamily { All, Special, Buildings, Items, Animals, People, Filth }

	public class Designator_MA_Object : Designator_MA
	{
		private Rot4 rot = Rot4.North;
		private ThingDef stuff;
		public static int pawnFaction;   // 0 no faction / wild, 1 your colony, 2 hostile
		protected virtual ObjFamily Family => ObjFamily.All;
		protected virtual string Key => "MA_Object";

		public Designator_MA_Object()
		{
			defaultLabel = Key.Translate();
			defaultDesc = (Key + "Desc").Translate();
			icon = Tex("UI/Designators/Claim");
			defaultIconColor = new Color(1f, 0.75f, 0.95f);
		}

		protected override string BaseLabel => Key.Translate();

		public override DrawStyleCategoryDef DrawStyleCategory => null;
		public override bool DragDrawMeasurements => false;

		private static bool IsSpecial(ThingDef d)
		{
			string n = d.defName;
			return n.IndexOf("Geyser", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Vent", StringComparison.OrdinalIgnoreCase) >= 0
				|| n.StartsWith("Ancient") || n.IndexOf("Ruin", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Archotech", StringComparison.OrdinalIgnoreCase) >= 0
				|| n.IndexOf("Archite", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Monolith", StringComparison.OrdinalIgnoreCase) >= 0
				|| (d.thingCategories != null && d.thingCategories.Any(tc => tc.defName == "StoneChunks"));
		}

		private static string SpecialGroup(ThingDef d)
		{
			string n = d.defName;
			if (n.IndexOf("Geyser", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Vent", StringComparison.OrdinalIgnoreCase) >= 0)
				return "MA_GroupSpecial".Translate();
			if (d.thingCategories != null && d.thingCategories.Any(tc => tc.defName == "StoneChunks"))
				return "MA_GroupMinerals".Translate();
			return "MA_GroupAncient".Translate();
		}

		private static string ItemGroup(ThingDef d)
		{
			ThingCategoryDef c = d.FirstThingCategory;
			if (c == null)
				return "MA_GroupOther".Translate();
			// the top category under the root (Foods, Manufactured, Weapons, Apparel …)
			while (c.parent != null && c.parent != ThingCategoryDefOf.Root)
				c = c.parent;
			return c.LabelCap;
		}

		private static string BuildingGroup(ThingDef d)
		{
			if (d.designationCategory != null)
				return d.designationCategory.LabelCap;
			if (d.building != null && d.building.isNaturalRock)
				return "MA_GroupRock".Translate();
			return "MA_GroupOther".Translate();
		}

		private static bool Placeable(ThingDef d)
		{
			return !d.label.NullOrEmpty() && !d.IsBlueprint && !d.IsFrame && !d.isUnfinishedThing && !d.IsCorpse && d.plant == null
				&& d.thingClass != null && !typeof(Pawn).IsAssignableFrom(d.thingClass) && !typeof(MinifiedThing).IsAssignableFrom(d.thingClass)
				&& !(d.building?.isNaturalRock ?? false);
		}

		private static PickOption Opt(ThingDef d, string group) => new PickOption { label = d.LabelCap, group = group, value = d, icon = d.uiIcon, iconColor = d.uiIconColor };

		protected override List<PickOption> Options()
		{
			var list = new List<PickOption>();
			ObjFamily f = Family;
			foreach (ThingDef d in DefDatabase<ThingDef>.AllDefsListForReading)
			{
				try
				{
					if (!Placeable(d))
						continue;
					bool special = (d.category == ThingCategory.Building || d.category == ThingCategory.Item) && IsSpecial(d);
					if (d.category == ThingCategory.Filth)
					{
						if (f == ObjFamily.Filth || f == ObjFamily.All)
							list.Add(Opt(d, "MA_GroupFilth".Translate()));
					}
					else if (special)
					{
						if (f == ObjFamily.Special || f == ObjFamily.All)
							list.Add(Opt(d, SpecialGroup(d)));
					}
					else if (d.category == ThingCategory.Building)
					{
						if (f == ObjFamily.Buildings || f == ObjFamily.All)
							list.Add(Opt(d, BuildingGroup(d)));
					}
					else if (d.category == ThingCategory.Item)
					{
						if (f == ObjFamily.Items || f == ObjFamily.All)
							list.Add(Opt(d, ItemGroup(d)));
					}
				}
				catch
				{
				}
			}
			foreach (PawnKindDef k in DefDatabase<PawnKindDef>.AllDefsListForReading)
			{
				if (k.RaceProps == null || k.label.NullOrEmpty() || k.race == null)
					continue;
				string g = null;
				if (k.RaceProps.Animal && (f == ObjFamily.Animals || f == ObjFamily.All))
					g = k.RaceProps.FleshType == FleshTypeDefOf.Insectoid ? (string)"MA_GroupInsects".Translate() : (string)"MA_GroupAnimals".Translate();
				else if (!k.RaceProps.Animal && (f == ObjFamily.People || f == ObjFamily.All))
				{
					if (k.RaceProps.IsMechanoid) g = "MA_GroupMechs".Translate();
					else if (k.RaceProps.Humanlike) g = k.defaultFactionDef != null ? k.defaultFactionDef.LabelCap.ToString() : (string)"MA_GroupPeople".Translate();
					else g = "MA_GroupCreatures".Translate();
				}
				if (g != null)
					list.Add(new PickOption { label = k.LabelCap, group = g, value = k, icon = k.race.uiIcon, mod = k.modContentPack?.Name });
			}
			return list;
		}

		// people: who they belong to
		protected override Func<Rect, float> ExtraUI => Family == ObjFamily.People || Family == ObjFamily.All ? (Func<Rect, float>)(r =>
		{
			Widgets.Label(new Rect(r.x, r.y + 3f, 90f, 24f), "MA_Faction".Translate());
			float x = r.x + 90f;
			for (int i = 0; i < 3; i++)
			{
				string lab = ("MA_Faction" + i).Translate();
				float w = Text.CalcSize(lab).x + 20f;
				GUI.color = pawnFaction == i ? new Color(0.55f, 1f, 0.55f) : Color.white;
				if (Widgets.ButtonText(new Rect(x, r.y, w, 26f), lab))
					pawnFaction = i;
				GUI.color = Color.white;
				x += w + 4f;
			}
			return 30f;
		}) : null;

		// things made from a material: choose it right after the thing
		protected override bool AfterPick(PickOption o)
		{
			stuff = null;
			if (!(o.value is ThingDef d) || !d.MadeFromStuff)
				return false;
			var stuffs = GenStuff.AllowedStuffsFor(d).ToList();
			if (stuffs.Count <= 1)
			{
				stuff = stuffs.FirstOrDefault();
				return false;
			}
			var opts = stuffs.Select(st => new PickOption { label = st.LabelCap, group = st.stuffProps?.categories?.FirstOrDefault()?.LabelCap.ToString(), value = st, icon = st.uiIcon, iconColor = st.stuffProps?.color ?? Color.white }).ToList();
			Find.WindowStack.Add(new Dialog_Pick("MA_Stuff".Translate(d.LabelCap), opts, so =>
			{
				stuff = so.value as ThingDef;
				choiceLabel = o.label + " (" + so.label + ")";
				Find.DesignatorManager.Select(this);
			}));
			return true;
		}

		public override void SelectedUpdate()
		{
			base.SelectedUpdate();
			if (choice is ThingDef d && d.category == ThingCategory.Building)
			{
				IntVec3 c = UI.MouseCell();
				if (c.InBounds(Map))
					GenDraw.DrawFieldEdges(GenAdj.OccupiedRect(c, rot, d.Size).Cells.Where(x => x.InBounds(Map)).ToList());
				if (KeyBindingDefOf.Designator_RotateRight.JustPressed)
					rot.Rotate(RotationDirection.Clockwise);
				if (KeyBindingDefOf.Designator_RotateLeft.JustPressed)
					rot.Rotate(RotationDirection.Counterclockwise);
			}
		}

		private static Faction HostileFor(PawnKindDef k)
		{
			if (k.RaceProps.IsMechanoid)
				return Faction.OfMechanoids;
			if (!k.RaceProps.Humanlike)
				return ModsConfig.AnomalyActive ? Faction.OfEntities : null;
			if (k.defaultFactionDef != null)
			{
				Faction own = Find.FactionManager.FirstFactionOfDef(k.defaultFactionDef);
				if (own != null && own.HostileTo(Faction.OfPlayer))
					return own;
			}
			return Find.FactionManager.RandomEnemyFaction(false, false, true);
		}

		protected override void Apply(List<IntVec3> cells)
		{
			if (cells.Count == 0)
				return;
			IntVec3 c = cells[0];
			if (choice is PawnKindDef k)
			{
				Faction fac = null;
				if (!k.RaceProps.Animal || pawnFaction != 0)
				{
					if (pawnFaction == 1) fac = Faction.OfPlayer;
					else if (pawnFaction == 2) fac = HostileFor(k);
					else if (k.RaceProps.IsMechanoid) fac = null;
				}
				if (k.RaceProps.Animal && pawnFaction == 2) fac = null;   // hostile animal = manhunter below
				Pawn p = PawnGenerator.GeneratePawn(new PawnGenerationRequest(k, fac, PawnGenerationContext.NonPlayer, forceGenerateNewPawn: true));
				Edit.Spawn(p, c, Map, Rot4.Random);
				if (pawnFaction == 2)
				{
					if (k.RaceProps.Animal)
						p.mindState.mentalStateHandler.TryStartMentalState(MentalStateDefOf.ManhunterPermanent);
					else if (p.Faction != null && p.Faction != Faction.OfPlayer)
						Verse.AI.Group.LordMaker.MakeNewLord(p.Faction, new LordJob_AssaultColony(p.Faction), Map, new[] { p });
				}
				return;
			}
			if (!(choice is ThingDef d))
				return;
			ThingDef st = d.MadeFromStuff ? (stuff ?? GenStuff.DefaultStuffFor(d)) : null;
			Thing t = ThingMaker.MakeThing(d, st);
			if (d.category == ThingCategory.Building)
			{
				CellRect r = GenAdj.OccupiedRect(c, rot, d.Size);
				if (!r.InBounds(Map) || r.Cells.Any(x => x.GetThingList(Map).Any(Edit.Protected)))
				{
					Messages.Message("MA_NoSpace".Translate(), MessageTypeDefOf.RejectInput, false);
					return;
				}
				foreach (IntVec3 x in r.Cells)
					foreach (Thing o in x.GetThingList(Map).ToList())
						if (o.def.category == ThingCategory.Building || o is Plant)
							Edit.Remove(o);
			}
			else if (d.category == ThingCategory.Item)
				t.stackCount = Math.Max(1, Math.Min(d.stackLimit, d.stackLimit >= 75 ? 75 : d.stackLimit));
			Edit.Spawn(t, c, Map, rot);
		}
	}

	public class Designator_MA_ObjSpecial : Designator_MA_Object
	{
		protected override ObjFamily Family => ObjFamily.Special;
		protected override string Key => "MA_ObjSpecial";
		protected override Texture2D LazyIcon() => DefDatabase<ThingDef>.GetNamedSilentFail("AncientCryptosleepCasket")?.uiIcon;
	}

	public class Designator_MA_ObjBuildings : Designator_MA_Object
	{
		protected override ObjFamily Family => ObjFamily.Buildings;
		protected override string Key => "MA_ObjBuildings";
		protected override Texture2D LazyIcon() => DefDatabase<ThingDef>.GetNamedSilentFail("Bed")?.uiIcon;
	}

	public class Designator_MA_ObjItems : Designator_MA_Object
	{
		protected override ObjFamily Family => ObjFamily.Items;
		protected override string Key => "MA_ObjItems";
		protected override Texture2D LazyIcon() => DefDatabase<ThingDef>.GetNamedSilentFail("MedicineIndustrial")?.uiIcon;
	}

	public class Designator_MA_ObjAnimals : Designator_MA_Object
	{
		protected override ObjFamily Family => ObjFamily.Animals;
		protected override string Key => "MA_ObjAnimals";
		protected override Texture2D LazyIcon() => DefDatabase<ThingDef>.GetNamedSilentFail("Muffalo")?.uiIcon;
	}

	public class Designator_MA_ObjPeople : Designator_MA_Object
	{
		protected override ObjFamily Family => ObjFamily.People;
		protected override string Key => "MA_ObjPeople";
		protected override Texture2D LazyIcon() => DefDatabase<ThingDef>.GetNamedSilentFail("Apparel_Parka")?.uiIcon;
	}

	public class Designator_MA_ObjFilth : Designator_MA_Object
	{
		protected override ObjFamily Family => ObjFamily.Filth;
		protected override string Key => "MA_ObjFilth";
		protected override Texture2D LazyIcon() => Pick.Tex("UI/Designators/SnowClearAreaOn");
	}

	internal static class Pick
	{
		public static TerrainDef Terrain(params string[] names)
		{
			foreach (string n in names)
			{
				TerrainDef t = DefDatabase<TerrainDef>.GetNamedSilentFail(n);
				if (t != null)
					return t;
			}
			return null;
		}

		public static Texture2D TerrainIcon(TerrainDef t)
		{
			if (t == null)
				return null;
			return t.uiIcon != null && t.uiIcon != BaseContent.BadTex ? t.uiIcon : null;
		}

		public static Texture2D Tex(string path) => ContentFinder<Texture2D>.Get(path, false);
	}

	// ---------- quick terrain buttons
	public abstract class Designator_MA_TerrainQuick : Designator_MA_Terrain
	{
		protected abstract string[] Names { get; }
		protected abstract string Key { get; }
		private TerrainDef def;
		private TerrainDef Def => def ??= Pick.Terrain(Names);
		protected override object Preset => Def;
		protected override string PresetLabel => Def == null ? null : (string)Def.LabelCap;
		protected override Texture2D LazyIcon() => Pick.TerrainIcon(Def);
		public override bool Visible => Def != null && base.Visible;
	}

	public class Designator_MA_T_Soil : Designator_MA_TerrainQuick { protected override string[] Names => new[] { "Soil" }; protected override string Key => "Soil"; }
	public class Designator_MA_T_RichSoil : Designator_MA_TerrainQuick { protected override string[] Names => new[] { "SoilRich" }; protected override string Key => "SoilRich"; }
	public class Designator_MA_T_Sand : Designator_MA_TerrainQuick { protected override string[] Names => new[] { "Sand" }; protected override string Key => "Sand"; }
	public class Designator_MA_T_Gravel : Designator_MA_TerrainQuick { protected override string[] Names => new[] { "Gravel" }; protected override string Key => "Gravel"; }
	public class Designator_MA_T_Mud : Designator_MA_TerrainQuick { protected override string[] Names => new[] { "Mud" }; protected override string Key => "Mud"; }
	public class Designator_MA_T_Marsh : Designator_MA_TerrainQuick { protected override string[] Names => new[] { "Marsh" }; protected override string Key => "Marsh"; }
	public class Designator_MA_T_WaterShallow : Designator_MA_TerrainQuick { protected override string[] Names => new[] { "WaterShallow" }; protected override string Key => "WaterShallow"; }
	public class Designator_MA_T_WaterDeep : Designator_MA_TerrainQuick { protected override string[] Names => new[] { "WaterDeep" }; protected override string Key => "WaterDeep"; }
	public class Designator_MA_T_Ice : Designator_MA_TerrainQuick { protected override string[] Names => new[] { "Ice" }; protected override string Key => "Ice"; }

	// ---------- smooth surface
	public class Designator_MA_Smooth : Designator_MA
	{
		public Designator_MA_Smooth()
		{
			defaultLabel = "MA_Smooth".Translate();
			defaultDesc = "MA_SmoothDesc".Translate();
		}
		protected override string BaseLabel => "MA_Smooth".Translate();
		protected override bool NeedsChoice => false;
		protected override Texture2D LazyIcon() => Pick.Tex("UI/Designators/SmoothSurface");
		protected override void Apply(List<IntVec3> cells)
		{
			foreach (IntVec3 c in cells)
			{
				TerrainDef t = Map.terrainGrid.TopTerrainAt(c);
				if (t?.smoothedTerrain != null)
					Edit.Terrain(Map, c, t.smoothedTerrain);
			}
		}
	}

	// ---------- fog
	public class Designator_MA_FogAdd : Designator_MA
	{
		public Designator_MA_FogAdd()
		{
			defaultLabel = "MA_FogAdd".Translate();
			defaultDesc = "MA_FogAddDesc".Translate();
			icon = BaseContent.WhiteTex;
			defaultIconColor = new Color(0.25f, 0.25f, 0.3f);
		}
		protected override string BaseLabel => "MA_FogAdd".Translate();
		protected override bool NeedsChoice => false;
		protected override void Apply(List<IntVec3> cells)
		{
			foreach (IntVec3 c in cells)
			{
				if (Map.fogGrid.IsFogged(c) || c.GetThingList(Map).Any(t => t is Pawn p && p.Faction == Faction.OfPlayer))
					continue;
				Undo.Fog(Map, c);
				Map.fogGrid.Refog(CellRect.SingleCell(c));
			}
		}
	}

	public class Designator_MA_FogRemove : Designator_MA
	{
		public Designator_MA_FogRemove()
		{
			defaultLabel = "MA_FogRemove".Translate();
			defaultDesc = "MA_FogRemoveDesc".Translate();
			icon = BaseContent.WhiteTex;
			defaultIconColor = new Color(0.85f, 0.85f, 0.9f);
		}
		protected override string BaseLabel => "MA_FogRemove".Translate();
		protected override bool NeedsChoice => false;
		protected override void Apply(List<IntVec3> cells)
		{
			foreach (IntVec3 c in cells)
			{
				if (!Map.fogGrid.IsFogged(c))
					continue;
				Undo.Fog(Map, c);
				Map.fogGrid.Unfog(c);
			}
		}
	}

	// ---------- quick plants from the map's own biome
	public abstract class Designator_MA_BiomePlants : Designator_MA
	{
		protected abstract bool Trees { get; }
		protected override bool NeedsChoice => false;
		protected override string SliderLabel => "MA_Density".Translate();

		public override void ProcessInput(Event ev)
		{
			if (ev.button == 1)
			{
				Find.WindowStack.Add(new Dialog_Pick(Label, new List<PickOption> { new PickOption { label = "OK", value = 1 } }, o => Find.DesignatorManager.Select(this), SliderLabel));
				return;
			}
			base.ProcessInput(ev);
		}

		protected override Texture2D LazyIcon()
		{
			ThingDef d = Trees ? DefDatabase<ThingDef>.GetNamedSilentFail("Plant_TreeOak") : DefDatabase<ThingDef>.GetNamedSilentFail("Plant_Bush");
			return d?.uiIcon;
		}

		protected override void Apply(List<IntVec3> cells)
		{
			BiomeDef biome = Map.Biome;
			var plants = biome.AllWildPlants.Where(p => p.plant != null && p.plant.IsTree == Trees).ToList();
			if (plants.Count == 0)
				return;
			float density = cells.Count == 1 ? 1f : Dialog_Pick.sliderValue;
			foreach (IntVec3 c in cells)
			{
				if (!Rand.Chance(density) || c.GetPlant(Map) != null || c.GetEdifice(Map) != null || c.Impassable(Map) || Map.fertilityGrid.FertilityAt(c) <= 0f)
					continue;
				var ok = plants.Where(p => p.CanEverPlantAt(c, Map)).ToList();
				if (!ok.TryRandomElementByWeight(p => Math.Max(0.01f, biome.CommonalityOfPlant(p)), out ThingDef def))
					continue;
				Plant pl = (Plant)ThingMaker.MakeThing(def);
				pl.Growth = Rand.Range(0.35f, 1f);
				if (def.plant.LimitedLifespan)
					pl.Age = Rand.Range(0, Math.Max(1, def.plant.LifespanTicks / 2));
				Edit.Spawn(pl, c, Map, Rot4.North);
			}
		}
	}

	public class Designator_MA_BiomeTrees : Designator_MA_BiomePlants
	{
		public Designator_MA_BiomeTrees() { defaultLabel = "MA_BiomeTrees".Translate(); defaultDesc = "MA_BiomeTreesDesc".Translate(); }
		protected override bool Trees => true;
		protected override string BaseLabel => "MA_BiomeTrees".Translate();
	}

	public class Designator_MA_BiomeGrass : Designator_MA_BiomePlants
	{
		public Designator_MA_BiomeGrass() { defaultLabel = "MA_BiomeGrass".Translate(); defaultDesc = "MA_BiomeGrassDesc".Translate(); }
		protected override bool Trees => false;
		protected override string BaseLabel => "MA_BiomeGrass".Translate();
	}

	// ---------- quick mountain / roof
	public class Designator_MA_MountainQuick : Designator_MA_Mountain
	{
		public Designator_MA_MountainQuick() { defaultLabel = "MA_MountainQuick".Translate(); defaultDesc = "MA_MountainQuickDesc".Translate(); }
		protected override string PresetLabel => "MA_MountainQuick".Translate();
		protected override object Preset => "maprock";
		protected override Texture2D LazyIcon() => DefDatabase<ThingDef>.GetNamedSilentFail("Granite")?.uiIcon;

		protected override void Apply(List<IntVec3> cells)
		{
			var rocks = Find.World.NaturalRockTypesIn(Map.Tile).ToList();
			ThingDef rock = rocks.Count > 0 ? rocks.RandomElement() : DefDatabase<ThingDef>.GetNamedSilentFail("Granite");
			choice = rock;
			base.Apply(cells);
			choice = "maprock";
		}
	}

	public class Designator_MA_RoofThick : Designator_MA_Mountain
	{
		public Designator_MA_RoofThick() { defaultLabel = "MA_RoofThick".Translate(); }
		protected override string PresetLabel => "MA_RoofThick".Translate();
		protected override object Preset => RoofDefOf.RoofRockThick;
		protected override Texture2D LazyIcon() => Pick.Tex("UI/Designators/BuildRoofArea");
	}

	public class Designator_MA_RoofRemove : Designator_MA_Mountain
	{
		public Designator_MA_RoofRemove() { defaultLabel = "MA_RoofNone".Translate(); }
		protected override string PresetLabel => "MA_RoofNone".Translate();
		protected override object Preset => "noroof";
		protected override Texture2D LazyIcon() => Pick.Tex("UI/Designators/NoRoofArea");
	}

	// ---------- quick clear
	public class Designator_MA_ClearPlants : Designator_MA_Clear
	{
		protected override string PresetLabel => "MA_ClearPlantsQuick".Translate();
		protected override object Preset => ClearMode.Plants;
		protected override Texture2D LazyIcon() => Pick.Tex("UI/Designators/CutPlants");
	}

	public class Designator_MA_ClearRocks : Designator_MA_Clear
	{
		protected override string PresetLabel => "MA_ClearRocksQuick".Translate();
		protected override object Preset => ClearMode.Rocks;
		protected override Texture2D LazyIcon() => Pick.Tex("UI/Designators/Mine");
	}

	public class Designator_MA_ClearFilth : Designator_MA_Clear
	{
		protected override string PresetLabel => "MA_ClearFilthQuick".Translate();
		protected override object Preset => ClearMode.Filth;
		protected override Texture2D LazyIcon() => Pick.Tex("UI/Designators/SnowClearAreaOn");
	}

	// ---------- quick geyser
	public class Designator_MA_Geyser : Designator_MA_Object
	{
		protected override string PresetLabel => DefDatabase<ThingDef>.GetNamedSilentFail("SteamGeyser")?.LabelCap ?? "Geyser";
		protected override object Preset => DefDatabase<ThingDef>.GetNamedSilentFail("SteamGeyser");
		protected override Texture2D LazyIcon() => DefDatabase<ThingDef>.GetNamedSilentFail("SteamGeyser")?.uiIcon;
	}
}


namespace MapArchitect
{
	// ---------- roofs you build (instant), any roof removed
	public class Designator_MA_RoofBuild : Designator_MA_Mountain
	{
		public Designator_MA_RoofBuild() { defaultLabel = "MA_RoofBuild".Translate(); defaultDesc = "MA_RoofBuildDesc".Translate(); }
		protected override string PresetLabel => "MA_RoofBuild".Translate();
		protected override object Preset => RoofDefOf.RoofConstructed;
		protected override Texture2D LazyIcon() => Pick.Tex("UI/Designators/BuildRoofArea");
		protected override void Apply(List<IntVec3> cells)
		{
			foreach (IntVec3 c in cells)
				if (Map.roofGrid.RoofAt(c) == null)
					Edit.Roof(Map, c, RoofDefOf.RoofConstructed);
		}
	}

	public class Designator_MA_RoofRemoveAll : Designator_MA_Mountain
	{
		public Designator_MA_RoofRemoveAll() { defaultLabel = "MA_RoofRemoveAll".Translate(); defaultDesc = "MA_RoofRemoveAllDesc".Translate(); }
		protected override string PresetLabel => "MA_RoofRemoveAll".Translate();
		protected override object Preset => "any";
		protected override Texture2D LazyIcon() => Pick.Tex("UI/Designators/NoRoofArea");
		protected override void Apply(List<IntVec3> cells)
		{
			foreach (IntVec3 c in cells)
				if (Map.roofGrid.RoofAt(c) != null)
					Edit.Roof(Map, c, null);
		}
	}

	// ---------- simple tools without a choice
	public abstract class Designator_MA_Tool : Designator_MA
	{
		protected abstract string Key { get; }
		protected Designator_MA_Tool()
		{
			defaultLabel = ("MA_" + Key).Translate();
			defaultDesc = ("MA_" + Key + "Desc").Translate();
		}
		protected override string BaseLabel => ("MA_" + Key).Translate();
		protected override bool NeedsChoice => false;
	}

	// finish blueprints and frames in the area at once, for free
	public class Designator_MA_Finish : Designator_MA_Tool
	{
		protected override string Key => "Finish";
		protected override Texture2D LazyIcon() => Pick.Tex("UI/Designators/Claim");
		protected override void Apply(List<IntVec3> cells)
		{
			var seen = new HashSet<Thing>();
			foreach (IntVec3 c in cells)
			{
				foreach (Thing t in c.GetThingList(Map).ToList())
				{
					if (!seen.Add(t) || t.Destroyed)
						continue;
					BuildableDef what = null;
					ThingDef stuff = null;
					if (t is Blueprint_Build b)
					{
						what = b.def.entityDefToBuild;
						stuff = b.EntityToBuildStuff();
					}
					else if (t is Frame f)
					{
						what = f.def.entityDefToBuild;
						stuff = f.EntityToBuildStuff();
					}
					if (what == null)
						continue;
					IntVec3 pos = t.Position;
					Rot4 rot = t.Rotation;
					Faction fac = t.Faction;
					Edit.Remove(t);
					if (what is TerrainDef td)
					{
						Edit.Terrain(Map, pos, td);
						continue;
					}
					if (what is ThingDef thd)
					{
						Thing n = ThingMaker.MakeThing(thd, thd.MadeFromStuff ? (stuff ?? GenStuff.DefaultStuffFor(thd)) : null);
						if (fac != null)
							n.SetFactionDirect(fac);
						Edit.Spawn(n, pos, Map, rot);
					}
				}
			}
		}
	}

	public class Designator_MA_Repair : Designator_MA_Tool
	{
		protected override string Key => "Repair";
		protected override Texture2D LazyIcon() => Pick.Tex("UI/Designators/Deconstruct");
		protected override void Apply(List<IntVec3> cells)
		{
			var seen = new HashSet<Thing>();
			foreach (IntVec3 c in cells)
				foreach (Thing t in c.GetThingList(Map))
					if (seen.Add(t) && !(t is Pawn) && t.def.useHitPoints && t.HitPoints < t.MaxHitPoints)
					{
						Undo.Hp(t);
						t.HitPoints = t.MaxHitPoints;
						Map.listerBuildingsRepairable.Notify_BuildingRepaired(t as Building);
					}
		}
	}

	public class Designator_MA_SnowRemove : Designator_MA_Tool
	{
		protected override string Key => "SnowRemove";
		protected override Texture2D LazyIcon() => Pick.Tex("UI/Designators/SnowClearAreaOn");
		protected override void Apply(List<IntVec3> cells)
		{
			foreach (IntVec3 c in cells)
				if (Map.snowGrid.GetDepth(c) > 0f)
				{
					Undo.Snow(Map, c);
					Map.snowGrid.SetDepth(c, 0f);
				}
		}
	}

	public class Designator_MA_SnowAdd : Designator_MA_Tool
	{
		protected override string Key => "SnowAdd";
		protected override Texture2D LazyIcon() => Pick.Tex("UI/Designators/SnowClearAreaOff");
		protected override void Apply(List<IntVec3> cells)
		{
			foreach (IntVec3 c in cells)
				if (!c.Roofed(Map) && c.GetEdifice(Map) == null)
				{
					Undo.Snow(Map, c);
					Map.snowGrid.SetDepth(c, 1f);
				}
		}
	}

	public class Designator_MA_PollutionRemove : Designator_MA_Tool
	{
		protected override string Key => "PollutionRemove";
		public override bool Visible => ModsConfig.BiotechActive && base.Visible;
		protected override Texture2D LazyIcon() => DefDatabase<ThingDef>.GetNamedSilentFail("Wastepack")?.uiIcon;
		protected override void Apply(List<IntVec3> cells)
		{
			foreach (IntVec3 c in cells)
				if (Map.pollutionGrid.IsPolluted(c))
				{
					Undo.Pollution(Map, c);
					Map.pollutionGrid.SetPolluted(c, false, true);
				}
		}
	}

	public class Designator_MA_PollutionAdd : Designator_MA_Tool
	{
		protected override string Key => "PollutionAdd";
		public override bool Visible => ModsConfig.BiotechActive && base.Visible;
		protected override Texture2D LazyIcon() => DefDatabase<ThingDef>.GetNamedSilentFail("Wastepack")?.uiIcon;
		protected override void Apply(List<IntVec3> cells)
		{
			foreach (IntVec3 c in cells)
				if (!Map.pollutionGrid.IsPolluted(c) && Map.pollutionGrid.EverPollutable(c))
				{
					Undo.Pollution(Map, c);
					Map.pollutionGrid.SetPolluted(c, true, true);
				}
		}
	}

	public class Designator_MA_Extinguish : Designator_MA_Tool
	{
		protected override string Key => "Extinguish";
		protected override Texture2D LazyIcon() => Pick.Tex("UI/Icons/Fire") ?? ThingDefOf.Fire?.uiIcon;
		protected override void Apply(List<IntVec3> cells)
		{
			foreach (IntVec3 c in cells)
				foreach (Thing t in c.GetThingList(Map).ToList())
					if (t is Fire)
						t.Destroy(DestroyMode.Vanish);
		}
	}

	// grow the plants in the area to full size at once
	public class Designator_MA_GrowUp : Designator_MA_Tool
	{
		protected override string Key => "GrowUp";
		protected override Texture2D LazyIcon() => Pick.Tex("UI/Designators/Harvest");
		protected override void Apply(List<IntVec3> cells)
		{
			foreach (IntVec3 c in cells)
				if (c.GetPlant(Map) is Plant p && p.Growth < 1f)
				{
					p.Growth = 1f;
					Map.mapDrawer.MapMeshDirty(c, MapMeshFlagDefOf.Things);
				}
		}
	}
}
