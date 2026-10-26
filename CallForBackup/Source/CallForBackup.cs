using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace CallForBackup
{
    // ---------------------------------------------------------------------------
    // Call for Backup – call your allies into a battle yourself, where you need them.
    // Vanilla sends 800–1000 points of aid that walk in from the edge; here the help is as strong
    // as the enemies on the map (small / medium / large) and lands where you click: drop pods for
    // industrial and higher factions, a quick march from the nearest edge for tribes and medievals.
    // ---------------------------------------------------------------------------
    public class CFBSettings : ModSettings
    {
        public bool recommended = true;
        public bool needConsole = true;      // a powered comms console somewhere in your colony
        public bool allowFriends = true;     // also factions that are friendly but not allied (dearer)
        public int cooldownHours = 24;       // per faction
        public int costSmall = 10, costMedium = 20, costLarge = 35;
        public int walkInDelayMin = 1;       // in-game hours for factions that come on foot

        public override void ExposeData()
        {
            Scribe_Values.Look(ref recommended, "recommended", true);
            Scribe_Values.Look(ref needConsole, "needConsole", true);
            Scribe_Values.Look(ref allowFriends, "allowFriends", true);
            Scribe_Values.Look(ref cooldownHours, "cooldownHours", 24);
            Scribe_Values.Look(ref costSmall, "costSmall", 10);
            Scribe_Values.Look(ref costMedium, "costMedium", 20);
            Scribe_Values.Look(ref costLarge, "costLarge", 35);
            Scribe_Values.Look(ref walkInDelayMin, "walkInDelayMin", 1);
        }
    }

    public class CFBMod : Mod
    {
        public const string Version = "0.1.0";
        public static CFBSettings S;
        private static List<string> recBullets;

        public CFBMod(ModContentPack content) : base(content)
        {
            S = GetSettings<CFBSettings>();
            MUI.Init(new CFBSettings(), null, "recommended");
            if (S.recommended) MUI.ApplyAll(S);
            MNews.Register(content, Version);
            var h = new Harmony("bolek98.callforbackup");
            try { MUI.PatchWindow(h, typeof(CFBMod)); } catch { }
            try
            {
                h.Patch(AccessTools.Method(typeof(Pawn), nameof(Pawn.GetGizmos)), postfix: new HarmonyMethod(typeof(CFBMod), nameof(GizmoPostfix)));
            }
            catch (Exception e) { Log.Warning("[Call for Backup] button not added: " + e.Message); }
            Log.Message("[Call for Backup] v" + Version + " loaded.");
        }

        public override string SettingsCategory() => "Call for Backup";

        private static void Check(Listing_Standard l, string key, ref bool v, string field) =>
            MUI.Check(l, key.Translate(), (key + "_Desc").Translate(), ref v, S, field);

        private static int IntSlider(Listing_Standard l, string key, int v, int min, int max, string field) =>
            Mathf.RoundToInt(MUI.Slider(l, key.Translate(v), v, min, max, (key + "_Desc").Translate(), S, field));

        public override void DoSettingsWindowContents(Rect r)
        {
            MUI.Version(r, "v" + Version + " · Bolek98");
            if (recBullets == null)
                recBullets = new List<string> { "CFB_Rec1".Translate(), "CFB_Rec2".Translate(), "CFB_Rec3".Translate() };
            float y = r.y;
            y += MUI.RecommendedCard(new Rect(r.x, y, r.width, 0f), S, S.recommended, v => { S.recommended = v; if (v) MUI.ApplyAll(S); }, recBullets) + 6f;
            if (S.recommended) y += MUI.LockNote(new Rect(r.x, y, r.width, 0f)) + 4f;
            Rect rest = new Rect(r.x, y, r.width, r.yMax - y);
            var l = new Listing_Standard { maxOneColumn = true };
            MUI.BeginLock(S.recommended);
            try
            {
                l.Begin(rest);
                Check(l, "CFB_NeedConsole", ref S.needConsole, "needConsole");
                Check(l, "CFB_AllowFriends", ref S.allowFriends, "allowFriends");
                S.cooldownHours = IntSlider(l, "CFB_Cooldown", S.cooldownHours, 0, 72, "cooldownHours");
                S.costSmall = IntSlider(l, "CFB_CostSmall", S.costSmall, 0, 50, "costSmall");
                S.costMedium = IntSlider(l, "CFB_CostMedium", S.costMedium, 0, 60, "costMedium");
                S.costLarge = IntSlider(l, "CFB_CostLarge", S.costLarge, 0, 75, "costLarge");
                S.walkInDelayMin = IntSlider(l, "CFB_WalkDelay", S.walkInDelayMin, 0, 6, "walkInDelayMin");
                l.End();
            }
            finally { MUI.EndLock(rest); }
        }

        // the button on drafted colonists
        public static IEnumerable<Gizmo> GizmoPostfix(IEnumerable<Gizmo> __result, Pawn __instance)
        {
            foreach (Gizmo g in __result) yield return g;
            Pawn p = __instance;
            if (p == null || !p.Spawned || !p.Drafted || !p.IsColonistPlayerControlled || p.Map == null) yield break;
            yield return new Command_CallBackup(p.Map);
        }
    }

    public enum Size { Small, Medium, Large }

    public static class Backup
    {
        public static bool Console(Map here, out string why)
        {
            why = null;
            if (!CFBMod.S.needConsole) return true;
            foreach (Map m in Find.Maps)
            {
                if (m != here && !m.IsPlayerHome) continue;
                foreach (Building_CommsConsole c in m.listerBuildings.AllBuildingsColonistOfClass<Building_CommsConsole>())
                    if (c.CanUseCommsNow) return true;
            }
            why = "CFB_NoConsole".Translate();
            return false;
        }

        public static IEnumerable<Faction> Candidates()
        {
            foreach (Faction f in Find.FactionManager.AllFactionsVisibleInViewOrder)
            {
                if (f.IsPlayer || f.defeated || f.temporary || f.Hidden || !f.def.humanlikeFaction) continue;
                FactionRelationKind k = f.RelationKindWith(Faction.OfPlayer);
                if (k == FactionRelationKind.Ally || (CFBMod.S.allowFriends && k == FactionRelationKind.Neutral && f.PlayerGoodwill >= 40))
                    yield return f;
            }
        }

        public static int Cost(Faction f, Size s)
        {
            int c = s == Size.Small ? CFBMod.S.costSmall : s == Size.Medium ? CFBMod.S.costMedium : CFBMod.S.costLarge;
            if (f.RelationKindWith(Faction.OfPlayer) != FactionRelationKind.Ally) c = Mathf.RoundToInt(c * 1.5f);
            return c;
        }

        public static int CooldownLeft(Faction f)
        {
            int until = f.lastMilitaryAidRequestTick + CFBMod.S.cooldownHours * 2500;
            return Math.Max(0, until - Find.TickManager.TicksGame);
        }

        // how strong the enemies on this map are
        public static float EnemyPoints(Map map)
        {
            float sum = 0f;
            foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
                if (!p.Dead && !p.Downed && p.HostileTo(Faction.OfPlayer) && !p.IsPrisoner && p.kindDef != null)
                    sum += p.kindDef.combatPower;
            return sum;
        }

        public static float Points(Map map, Faction f, Size s)
        {
            float enemies = EnemyPoints(map);
            float mult = s == Size.Small ? 0.5f : s == Size.Medium ? 0.9f : 1.4f;
            float floor = s == Size.Small ? 300f : s == Size.Medium ? 600f : 1000f;
            float good = f.PlayerGoodwill >= 90 ? 1.15f : 1f;
            return Mathf.Clamp(Mathf.Max(floor, enemies * mult) * good, 200f, 20000f);
        }

        public static bool Drops(Faction f) => f.def.techLevel >= TechLevel.Industrial;

        public static void Call(Map map, Faction f, Size s, IntVec3 where)
        {
            float points = Points(map, f, s);
            var parms = new IncidentParms
            {
                target = map,
                faction = f,
                points = points,
                raidStrategy = RaidStrategyDefOf.ImmediateAttackFriendly,
                forced = true
            };
            int delay = 0;
            if (Drops(f))
            {
                parms.raidArrivalMode = PawnsArrivalModeDefOf.CenterDrop;
                parms.spawnCenter = where;
            }
            else
            {
                parms.raidArrivalMode = PawnsArrivalModeDefOf.EdgeWalkIn;
                if (CellFinder.TryFindRandomEdgeCellNearWith(where, 40f, map, c => c.Standable(map) && !c.Fogged(map) && map.reachability.CanReachColony(c), out IntVec3 edge))
                    parms.spawnCenter = edge;
                delay = CFBMod.S.walkInDelayMin * 2500;
            }
            bool ok;
            if (delay <= 0)
                ok = IncidentDefOf.RaidFriendly.Worker.TryExecute(parms);
            else
            {
                Find.Storyteller.incidentQueue.Add(new QueuedIncident(new FiringIncident(IncidentDefOf.RaidFriendly, null, parms), Find.TickManager.TicksGame + delay));
                ok = true;
                Messages.Message("CFB_OnTheWay".Translate(f.Name, Mathf.RoundToInt(delay / 2500f)), new LookTargets(where, map), MessageTypeDefOf.PositiveEvent, false);
            }
            if (!ok)
            {
                Messages.Message("CFB_Failed".Translate(f.Name), MessageTypeDefOf.RejectInput, false);
                return;
            }
            int cost = Cost(f, s);
            if (cost > 0) Faction.OfPlayer.TryAffectGoodwillWith(f, -cost, true, true, HistoryEventDefOf.RequestedMilitaryAid);
            f.lastMilitaryAidRequestTick = Find.TickManager.TicksGame;
        }
    }

    public class Command_CallBackup : Command
    {
        private readonly Map map;

        public Command_CallBackup(Map m)
        {
            map = m;
            defaultLabel = "CFB_Button".Translate();
            defaultDesc = "CFB_ButtonDesc".Translate();
            icon = ContentFinder<Texture2D>.Get("UI/Commands/CallAid", false) ?? TexCommand.Attack;
            if (!Backup.Console(map, out string why)) Disable(why);
            else if (!Backup.Candidates().Any()) Disable("CFB_NoAllies".Translate());
            else if (Backup.EnemyPoints(map) < 50f) Disable("CFB_NoEnemies".Translate());
        }

        public override bool GroupsWith(Gizmo other) => other is Command_CallBackup;

        public override void ProcessInput(Event ev)
        {
            base.ProcessInput(ev);
            var opts = new List<FloatMenuOption>();
            float enemies = Backup.EnemyPoints(map);
            foreach (Faction f in Backup.Candidates())
            {
                int left = Backup.CooldownLeft(f);
                string how = Backup.Drops(f) ? "CFB_ByPods".Translate() : "CFB_OnFoot".Translate();
                foreach (Size s in new[] { Size.Small, Size.Medium, Size.Large })
                {
                    int cost = Backup.Cost(f, s);
                    string label = "CFB_Option".Translate(f.Name, ("CFB_Size_" + s).Translate(), Mathf.RoundToInt(Backup.Points(map, f, s)), cost, how);
                    if (left > 0)
                    {
                        opts.Add(new FloatMenuOption(label + " — " + "CFB_Wait".Translate(Mathf.CeilToInt(left / 2500f)), null));
                        break;
                    }
                    Faction ff = f; Size ss = s;
                    opts.Add(new FloatMenuOption(label, () => Target(ff, ss), f.def.FactionIcon, f.Color));
                }
            }
            if (opts.Count == 0) opts.Add(new FloatMenuOption("CFB_NoAllies".Translate(), null));
            Find.WindowStack.Add(new FloatMenu(opts, "CFB_MenuTitle".Translate(Mathf.RoundToInt(enemies))));
        }

        private void Target(Faction f, Size s)
        {
            var tp = new TargetingParameters { canTargetLocations = true, canTargetPawns = false, canTargetBuildings = false };
            Find.Targeter.BeginTargeting(tp, t =>
            {
                if (!t.Cell.IsValid || !t.Cell.InBounds(map)) return;
                if (Backup.Drops(f) && (t.Cell.Roofed(map) && t.Cell.GetRoof(map).isThickRoof))
                {
                    Messages.Message("CFB_ThickRoof".Translate(), MessageTypeDefOf.RejectInput, false);
                    return;
                }
                Backup.Call(map, f, s, t.Cell);
                SoundDefOf.Tick_High.PlayOneShotOnCamera();
            }, caster: null, actionWhenFinished: null, mouseAttachment: null, requiresCastedSelected: false);
        }
    }
}
