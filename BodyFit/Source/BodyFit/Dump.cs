using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;

namespace BodyFit;

// 2.3.1: a data dump for fitting alien races: races, bodies, heads, every worn piece and where its
// pictures are on disk. Written once per game start, after a save is loaded.
public static class Dump
{
	private static bool done;

	public static string FilePath => Path.Combine(GenFilePaths.SaveDataFolderPath, "Zalohy_modu", "BodyFit_dump.txt");

	public static void Run()
	{
		if (done || Find.Maps == null || Find.CurrentMap == null)
			return;
		done = true;
		try
		{
			var sb = new StringBuilder();
			var wanted = new HashSet<string>();
			sb.AppendLine("# Body Fit dump " + DateTime.Now.ToString("s") + " game " + VersionControl.CurrentVersionString);

			sb.AppendLine("## BODYTYPES");
			foreach (BodyTypeDef b in DefDatabase<BodyTypeDef>.AllDefsListForReading)
			{
				sb.AppendLine("body\t" + b.defName + "\t" + (b.modContentPack?.Name ?? "?") + "\theadOffset=" + V(b.headOffset) + "\tnaked=" + b.bodyNakedGraphicPath + "\tscale=" + V(b.bodyGraphicScale));
				if (!b.bodyNakedGraphicPath.NullOrEmpty()) wanted.Add(b.bodyNakedGraphicPath);
			}

			sb.AppendLine("## RACES");
			foreach (ThingDef r in DefDatabase<ThingDef>.AllDefsListForReading.Where(d => d.race != null && d.race.Humanlike))
			{
				sb.AppendLine("race\t" + r.defName + "\t" + (r.modContentPack?.Name ?? "?") + "\tclass=" + r.GetType().Name + "\tbodySize=" + r.race.baseBodySize);
				FieldInfo fi = r.GetType().GetField("alienRace");
				if (fi != null)
				{
					object ar = fi.GetValue(r);
					Obj(sb, "  ", ar, 0, wanted);
				}
			}

			sb.AppendLine("## PAWNS");
			foreach (Map m in Find.Maps)
				foreach (Pawn p in m.mapPawns.AllPawnsSpawned.Where(x => x.RaceProps.Humanlike && x.story != null).Take(200))
				{
					sb.AppendLine("pawn\t" + p.LabelShort + "\t" + p.def.defName + "\t" + p.gender + "\tbody=" + p.story.bodyType?.defName + "\thead=" + p.story.headType?.defName + "\theadPath=" + p.story.headType?.graphicPath + "\thair=" + p.story.hairDef?.defName + "\thairPath=" + p.story.hairDef?.texPath + "\tlife=" + p.ageTracker?.CurLifeStage?.defName + "\tfaction=" + (p.Faction?.IsPlayer == true ? "player" : "other"));
					if (p.story.headType?.graphicPath != null) wanted.Add(p.story.headType.graphicPath);
					if (p.story.hairDef?.texPath != null) wanted.Add(p.story.hairDef.texPath);
					try
					{
						PawnRenderer pr = p.Drawer?.renderer;
						if (pr != null)
						{
							Graphic bg = pr.BodyGraphic, hg = pr.HeadGraphic;
							sb.AppendLine("  bodyGraphic\t" + bg?.path + "\tsize=" + V(bg?.drawSize ?? Vector2.zero) + "\tbad=" + (bg != null && bg.MatSouth == BaseContent.BadMat));
							sb.AppendLine("  headGraphic\t" + hg?.path + "\tsize=" + V(hg?.drawSize ?? Vector2.zero));
							if (bg?.path != null) wanted.Add(bg.path);
							if (hg?.path != null) wanted.Add(hg.path);
							PawnRenderTree tree = pr.renderTree;
							if (tree?.rootNode != null)
								Node(sb, "  ", tree.rootNode, wanted, 0);
						}
					}
					catch (Exception e) { sb.AppendLine("  render error " + e.Message); }
					if (p.apparel != null)
						foreach (Apparel a in p.apparel.WornApparel)
						{
							string got = "-";
							try
							{
								if (ApparelGraphicRecordGetter.TryGetGraphicApparel(a, p.story.bodyType, false, out ApparelGraphicRecord rec))
									got = rec.graphic?.path + "\tsize=" + V(rec.graphic?.drawSize ?? Vector2.zero) + "\tbad=" + (rec.graphic != null && rec.graphic.MatSouth == BaseContent.BadMat);
							}
							catch (Exception e) { got = "error " + e.Message; }
							sb.AppendLine("  worn\t" + a.def.defName + "\t" + (a.def.modContentPack?.Name ?? "?") + "\tlayers=" + string.Join(",", a.def.apparel.layers.Select(l => l.defName)) + "\tworn=" + a.def.apparel.wornGraphicPath + "\tgraphic=" + got);
							if (!a.def.apparel.wornGraphicPath.NullOrEmpty()) wanted.Add(a.def.apparel.wornGraphicPath);
						}
				}

			sb.AppendLine("## APPAREL");
			foreach (ThingDef d in DefDatabase<ThingDef>.AllDefsListForReading.Where(d => d.IsApparel && d.apparel != null && !d.apparel.wornGraphicPath.NullOrEmpty()))
			{
				sb.AppendLine("apparel\t" + d.defName + "\t" + (d.modContentPack?.Name ?? "?") + "\tlayers=" + string.Join(",", d.apparel.layers.Select(l => l.defName)) + "\tparts=" + string.Join(",", d.apparel.bodyPartGroups.Select(g => g.defName)) + "\tworn=" + d.apparel.wornGraphicPath + "\tshader=" + d.apparel.useWornGraphicMask);
				wanted.Add(d.apparel.wornGraphicPath);
			}

			sb.AppendLine("## HAIR");
			foreach (HairDef h in DefDatabase<HairDef>.AllDefsListForReading)
			{
				sb.AppendLine("hair\t" + h.defName + "\t" + (h.modContentPack?.Name ?? "?") + "\t" + h.texPath);
				wanted.Add(h.texPath);
			}
			sb.AppendLine("## HEADTYPES");
			foreach (HeadTypeDef h in DefDatabase<HeadTypeDef>.AllDefsListForReading)
			{
				sb.AppendLine("headtype\t" + h.defName + "\t" + (h.modContentPack?.Name ?? "?") + "\t" + h.graphicPath + "\tnarrow=" + h.narrow);
				if (!h.graphicPath.NullOrEmpty()) wanted.Add(h.graphicPath);
			}

			// where the pictures are: every PNG under each running mod's Textures folders whose path starts with a wanted path
			sb.AppendLine("## FILES");
			var prefixes = new HashSet<string>();
			foreach (string w in wanted)
			{
				if (w.NullOrEmpty()) continue;
				string x = w.Replace('\\', '/').ToLowerInvariant().TrimEnd('/');
				int sl = x.LastIndexOf('/');
				if (sl > 0) prefixes.Add(x.Substring(0, sl));
			}
			foreach (ModContentPack mod in LoadedModManager.RunningModsListForReading)
			{
				IEnumerable<string> folders = mod.foldersToLoadDescendingOrder ?? new List<string> { mod.RootDir };
				foreach (string f in folders.Distinct())
				{
					string tex = Path.Combine(f, "Textures");
					if (!Directory.Exists(tex))
						continue;
					foreach (string file in Directory.GetFiles(tex, "*.png", SearchOption.AllDirectories))
					{
						string rel = file.Substring(tex.Length + 1).Replace('\\', '/');
						string relNoExt = rel.Substring(0, rel.Length - 4).ToLowerInvariant();
						if (Matches(relNoExt, prefixes))
							sb.AppendLine("file\t" + rel + "\t" + file);
					}
				}
			}
			Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
			File.WriteAllText(FilePath, sb.ToString());
			MLog.Info("[Body Fit] data dump written: " + FilePath);
		}
		catch (Exception e)
		{
			MLog.Warn("[Body Fit] dump: " + e);
		}
	}

	private static bool Matches(string relNoExt, HashSet<string> dirs)
	{
		int slash = relNoExt.LastIndexOf('/');
		return slash > 0 && dirs.Contains(relNoExt.Substring(0, slash));
	}

	private static void Node(StringBuilder sb, string ind, PawnRenderNode n, HashSet<string> wanted, int depth)
	{
		if (n == null || depth > 6)
			return;
		Graphic g = null;
		try { g = n.PrimaryGraphic; } catch { }
		string props = "";
		try
		{
			PawnRenderNodeProperties pp = n.Props;
			if (pp != null)
				props = "\tworker=" + pp.workerClass?.Name + "\tparent=" + pp.parentTagDef?.defName + "\ttag=" + pp.tagDef?.defName + "\tdrawSize=" + V(pp.drawSize) + "\tlayer=" + pp.baseLayer + "\ttex=" + pp.texPath;
		}
		catch { }
		sb.AppendLine(ind + "node\t" + n.GetType().Name + "\t" + (g?.path ?? "-") + "\tgsize=" + V(g?.drawSize ?? Vector2.zero) + props);
		if (g?.path != null) wanted.Add(g.path);
		if (n.children != null)
			foreach (PawnRenderNode c in n.children)
				Node(sb, ind + "  ", c, wanted, depth + 1);
	}

	private static string V(Vector2 v) => v.x.ToString("0.###") + "," + v.y.ToString("0.###");

	private static string V(Vector3 v) => v.x.ToString("0.###") + "," + v.y.ToString("0.###") + "," + v.z.ToString("0.###");

	// a generic look into HAR's settings objects (field names and values, a few levels deep)
	private static void Obj(StringBuilder sb, string ind, object o, int depth, HashSet<string> wanted)
	{
		if (o == null || depth > 4)
			return;
		Type t = o.GetType();
		foreach (FieldInfo f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
		{
			object v;
			try { v = f.GetValue(o); } catch { continue; }
			if (v == null)
				continue;
			string name = f.Name;
			if (v is string s)
			{
				sb.AppendLine(ind + name + " = " + s);
				if (s.Contains("/")) wanted.Add(s);
			}
			else if (v is Def d)
				sb.AppendLine(ind + name + " = def:" + d.defName);
			else if (v is Vector2 v2)
				sb.AppendLine(ind + name + " = " + V(v2));
			else if (v is Vector3 v3)
				sb.AppendLine(ind + name + " = " + V(v3));
			else if (t.IsPrimitive || v is bool || v is int || v is float || v is Enum)
				sb.AppendLine(ind + name + " = " + v);
			else if (v.GetType().IsPrimitive || v is Enum)
				sb.AppendLine(ind + name + " = " + v);
			else if (v is IDictionary dict)
			{
				sb.AppendLine(ind + name + " = dict[" + dict.Count + "]");
				int k = 0;
				foreach (DictionaryEntry de in dict)
				{
					if (k++ > 30) break;
					sb.AppendLine(ind + "  [" + de.Key + "]");
					Obj(sb, ind + "    ", de.Value, depth + 1, wanted);
				}
			}
			else if (v is IEnumerable e && !(v is string))
			{
				var items = e.Cast<object>().Take(40).ToList();
				sb.AppendLine(ind + name + " = list[" + items.Count + "]");
				foreach (object x in items)
				{
					if (x == null) continue;
					if (x is string xs) { sb.AppendLine(ind + "  - " + xs); if (xs.Contains("/")) wanted.Add(xs); }
					else if (x is Def xd) sb.AppendLine(ind + "  - def:" + xd.defName);
					else if (x.GetType().IsPrimitive || x is Enum) sb.AppendLine(ind + "  - " + x);
					else if (x is Vector2 xv2) sb.AppendLine(ind + "  - " + V(xv2));
					else if (x is Vector3 xv3) sb.AppendLine(ind + "  - " + V(xv3));
					else
					{
						sb.AppendLine(ind + "  - " + x.GetType().Name);
						Obj(sb, ind + "    ", x, depth + 1, wanted);
					}
				}
			}
			else if (v.GetType().Namespace != null && (v.GetType().Namespace.StartsWith("UnityEngine") || v.GetType().Namespace.StartsWith("System")))
				sb.AppendLine(ind + name + " = " + v);
			else
			{
				sb.AppendLine(ind + name + " : " + v.GetType().Name);
				Obj(sb, ind + "  ", v, depth + 1, wanted);
			}
		}
	}
}
