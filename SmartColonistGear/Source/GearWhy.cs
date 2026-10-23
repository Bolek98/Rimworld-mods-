using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace ResearchedLoadout
{
	// 2.15.0: why did a colonist change gear – and who did it (this mod, the game, the player or another mod)
	public static class GearWhy
	{
		private class Entry
		{
			public int tick;
			public string text;
		}

		private class Tag
		{
			public string text;
			public int tick;
		}

		private static readonly Dictionary<int, List<Entry>> log = new Dictionary<int, List<Entry>>();
		private static readonly ConditionalWeakTable<Job, Tag> tags = new ConditionalWeakTable<Job, Tag>();
		private static Pawn pendingPawn;
		private static string pendingText;

		// gear changes made by other mods in this session: mod name -> count
		public static readonly Dictionary<string, int> OtherMods = new Dictionary<string, int>();

		private static readonly Dictionary<Assembly, string> modOf = new Dictionary<Assembly, string>();
		private static Assembly own;

		public static string Reason(string key, string detail = null)
		{
			string t = key.Translate().ToString();
			return detail.NullOrEmpty() ? t : t + " (" + detail + ")";
		}

		// the next gear job this mod gives (GiveSoftJob) carries this reason
		public static void Pending(Pawn pawn, string text)
		{
			pendingPawn = pawn;
			pendingText = text;
		}

		// the action is done; if no job took the reason (gear dropped right away), note it directly
		public static void Done(Pawn pawn, string action = null)
		{
			if (pendingText != null && pendingPawn == pawn)
				Add(pawn, (action ?? "RL_WhyAct_Change".Translate().ToString()) + " – " + pendingText, "SCG");
			Clear();
		}

		public static void Clear()
		{
			pendingPawn = null;
			pendingText = null;
		}

		public static void TagJob(Pawn pawn, Job job)
		{
			if (job == null || pendingText == null || pendingPawn != pawn)
				return;
			TagJob(job, pendingText);
			Clear();
		}

		public static void TagJob(Job job, string text)
		{
			if (job == null || text == null)
				return;
			try
			{
				tags.Remove(job);
				tags.Add(job, new Tag { text = text, tick = Find.TickManager?.TicksGame ?? 0 });
			}
			catch { }
		}

		private static void Add(Pawn pawn, string text, string source)
		{
			if (pawn == null)
				return;
			List<Entry> l;
			if (!log.TryGetValue(pawn.thingIDNumber, out l))
				log[pawn.thingIDNumber] = l = new List<Entry>();
			l.Add(new Entry { tick = Find.TickManager?.TicksGame ?? 0, text = text });
			if (l.Count > 8)
				l.RemoveAt(0);
		}

		public static string Describe(Pawn pawn)
		{
			List<Entry> l;
			if (pawn == null || !log.TryGetValue(pawn.thingIDNumber, out l) || l.Count == 0)
				return "RL_Why_None".Translate();
			int now = Find.TickManager?.TicksGame ?? 0;
			var lines = new List<string> { "RL_Why_Title".Translate() };
			for (int i = l.Count - 1; i >= 0 && lines.Count <= 6; i--)
			{
				int h = (now - l[i].tick) / 2500;
				string ago = h < 1 ? "RL_Why_Now".Translate().ToString() : "RL_Why_HoursAgo".Translate(h).ToString();
				lines.Add("• " + ago + ": " + l[i].text);
			}
			return string.Join("\n", lines.ToArray());
		}

		// ---------- who started a gear job ----------
		private static HashSet<JobDef> gearJobs;

		private static bool IsGear(JobDef d)
		{
			if (gearJobs == null)
			{
				gearJobs = new HashSet<JobDef> { JobDefOf.Wear, JobDefOf.Equip, JobDefOf.RemoveApparel, JobDefOf.DropEquipment, JobDefOf.TakeInventory };
				if (SidearmsCompat.EquipSecondaryJob != null)
					gearJobs.Add(SidearmsCompat.EquipSecondaryJob);
			}
			return d != null && gearJobs.Contains(d);
		}

		private static string Action(JobDef d)
		{
			if (d == JobDefOf.Wear) return "RL_WhyAct_Wear".Translate();
			if (d == JobDefOf.RemoveApparel) return "RL_WhyAct_Remove".Translate();
			if (d == JobDefOf.TakeInventory) return "RL_WhyAct_Take".Translate();
			if (d == JobDefOf.DropEquipment) return "RL_WhyAct_Drop".Translate();
			return "RL_WhyAct_Equip".Translate();
		}

		public static void OnStartJob(Pawn pawn, Job job, ThinkNode jobGiver)
		{
			if (job == null || pawn == null || !IsGear(job.def))
				return;
			GuestWardrobe.OnGearJob(pawn, job);
			if (!pawn.IsColonistPlayerControlled)
				return;
			// a plain haul of dropped gear is not a gear decision
			if (job.def == JobDefOf.TakeInventory && job.targetA.Thing != null && !job.targetA.Thing.def.IsWeapon)
				return;
			string what = Action(job.def) + (job.targetA.Thing != null ? " " + job.targetA.Thing.LabelShort : "");
			Tag tag;
			int now = Find.TickManager?.TicksGame ?? 0;
			if (tags.TryGetValue(job, out tag))
			{
				tags.Remove(job);
				if (now - tag.tick < 5000)
				{
					Add(pawn, what + " – " + tag.text, "SCG");
					return;
				}
			}
			if (job.playerForced)
			{
				Add(pawn, what + " – " + "RL_Why_Player".Translate(), "player");
				return;
			}
			string mod = null;
			if (jobGiver != null)
			{
				if (jobGiver is JobGiver_OptimizeApparel)
				{
					Add(pawn, what + " – " + "RL_Why_Outfit".Translate(), "game");
					return;
				}
				mod = ModOf(jobGiver.GetType().Assembly);
			}
			else
			{
				mod = ModFromStack();
			}
			if (mod == null)
			{
				Add(pawn, what + " – " + "RL_Why_Game".Translate(), "game");
				return;
			}
			Add(pawn, what + " – " + "RL_Why_OtherMod".Translate(mod), mod);
			int n;
			OtherMods.TryGetValue(mod, out n);
			OtherMods[mod] = n + 1;
			if (n == 0)
				MLog.Info("[Smart Colonist Gear] the mod \"" + mod + "\" also changes colonist gear (" + what + ", " + pawn.LabelShort + "). If colonists swap gear back and forth, let only one of the two decide.");
		}

		// null = the game itself (or this mod)
		private static string ModOf(Assembly a)
		{
			if (a == null)
				return null;
			if (own == null)
				own = typeof(GearWhy).Assembly;
			if (a == own)
				return null;
			string name;
			if (modOf.TryGetValue(a, out name))
				return name;
			name = null;
			try
			{
				foreach (ModContentPack m in LoadedModManager.RunningModsListForReading)
				{
					if (m.IsOfficialMod)
						continue;
					if (m.assemblies.loadedAssemblies.Contains(a))
					{
						name = m.Name;
						break;
					}
				}
			}
			catch { }
			modOf[a] = name;
			return name;
		}

		private static string ModFromStack()
		{
			try
			{
				var st = new System.Diagnostics.StackTrace(2, false);
				foreach (System.Diagnostics.StackFrame f in st.GetFrames() ?? new System.Diagnostics.StackFrame[0])
				{
					MethodBase m = f.GetMethod();
					Type t = m?.DeclaringType;
					if (t == null)
						continue;
					Assembly a = t.Assembly;
					if (a == own || a == typeof(Pawn).Assembly || a == typeof(Harmony).Assembly || a == typeof(object).Assembly)
						continue;
					// Harmony replacements live in dynamic assemblies – skip them
					if (a.IsDynamic)
						continue;
					string mod = ModOf(a);
					if (mod != null)
						return mod;
				}
			}
			catch { }
			return null;
		}

		public static string SeenText()
		{
			if (OtherMods.Count == 0)
				return "RL_OthersSeenNone".Translate();
			return "RL_OthersSeen".Translate(string.Join(", ", OtherMods.OrderByDescending(kv => kv.Value).Select(kv => kv.Key + " (" + kv.Value + "×)").ToArray()));
		}

		// ---------- mods that change how gear is chosen (found once) ----------
		private static List<string> census;

		public static List<string> Census()
		{
			if (census != null)
				return census;
			census = new List<string>();
			try
			{
				var methods = new List<MethodBase>
				{
					AccessTools.Method(typeof(JobGiver_OptimizeApparel), "TryGiveJob"),
					AccessTools.Method(typeof(JobGiver_OptimizeApparel), "ApparelScoreRaw"),
					AccessTools.Method(typeof(JobGiver_OptimizeApparel), "ApparelScoreGain"),
					AccessTools.Method(typeof(JobGiver_PickUpOpportunisticWeapon), "TryGiveJob"),
					AccessTools.Method(typeof(Pawn_ApparelTracker), "Wear"),
					AccessTools.Method(typeof(Pawn_EquipmentTracker), "AddEquipment"),
				};
				var names = new HashSet<string>();
				foreach (MethodBase mb in methods)
				{
					if (mb == null)
						continue;
					Patches info = Harmony.GetPatchInfo(mb);
					if (info == null)
						continue;
					foreach (Patch p in info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers))
					{
						string mod = ModOf(p.PatchMethod?.DeclaringType?.Assembly);
						if (mod != null)
							names.Add(mod);
					}
				}
				census = names.OrderBy(x => x).ToList();
			}
			catch (Exception e)
			{
				MLog.Warn("[Smart Colonist Gear] gear mod check: " + e.Message);
			}
			return census;
		}
	}

	[HarmonyPatch(typeof(Pawn_JobTracker), "StartJob")]
	public static class GearWhyStartJobPatch
	{
		[HarmonyPostfix]
		public static void Postfix(Job newJob, ThinkNode jobGiver, Pawn ___pawn)
		{
			try
			{
				GearWhy.OnStartJob(___pawn, newJob, jobGiver);
			}
			catch { }
		}
	}
}
