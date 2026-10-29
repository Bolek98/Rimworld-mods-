using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace QoLFW
{
    public enum WorkGroup { Craft, Build, Mine, Plants, Research, Cook, Clean, Medical, Animals }

    // Multiplies one work speed stat by the slider of its group.
    public class StatPart_QoLSpeed : StatPart
    {
        public WorkGroup group;

        public static float For(WorkGroup g, StatRequest req)
        {
            QSettings s = QoLMod.S;
            if (s == null || !s.workSpeed || !req.HasThing) return 1f;
            Pawn p = req.Thing as Pawn;
            if (p == null) return 1f;
            if (s.colonistsOnly && (p.Faction == null || !p.Faction.IsPlayer)) return 1f;
            switch (g)
            {
                case WorkGroup.Craft: return s.spCraft;
                case WorkGroup.Build: return s.spBuild;
                case WorkGroup.Mine: return s.spMine;
                case WorkGroup.Plants: return s.spPlants;
                case WorkGroup.Research: return s.spResearch;
                case WorkGroup.Cook: return s.spCook;
                case WorkGroup.Clean: return s.spClean;
                case WorkGroup.Medical: return s.spMedical;
                default: return s.spAnimals;
            }
        }

        public override void TransformValue(StatRequest req, ref float val)
        {
            float m = For(group, req);
            if (m != 1f) val *= m;
        }

        public override string ExplanationPart(StatRequest req)
        {
            float m = For(group, req);
            if (m == 1f) return null;
            return "QFW_SpeedExplain".Translate() + ": x" + m.ToString("0.##");
        }
    }

    public static class WorkSpeed
    {
        private static readonly Dictionary<StatDef, WorkGroup> installed = new Dictionary<StatDef, WorkGroup>();

        private static readonly string[][] named =
        {
            new[] { "GeneralLaborSpeed", "SmithingSpeed", "TailoringSpeed", "DrugSynthesisSpeed", "ButcheryMechanoidSpeed", "SubcoreEncodingSpeed", "SculptingSpeed" },
            new[] { "ConstructionSpeed", "SmoothingSpeed" },
            new[] { "MiningSpeed", "DeepDrillingSpeed" },
            new[] { "PlantWorkSpeed", "PruningSpeed" },
            new[] { "ResearchSpeed" },
            new[] { "CookSpeed", "ButcheryFleshSpeed", "DrugCookingSpeed" },
            new[] { "CleaningSpeed" },
            new[] { "MedicalTendSpeed", "MedicalOperationSpeed" },
            new[] { "AnimalGatherSpeed" },
        };

        public static void Install()
        {
            for (int g = 0; g < named.Length; g++)
                foreach (string n in named[g])
                    Add(DefDatabase<StatDef>.GetNamedSilentFail(n), (WorkGroup)g);

            // recipe speed stats from vanilla and mods, by the recipe's skill
            foreach (RecipeDef r in DefDatabase<RecipeDef>.AllDefsListForReading)
            {
                StatDef st = r.workSpeedStat;
                if (st == null || installed.ContainsKey(st) || st == StatDefOf.WorkSpeedGlobal) continue;
                SkillDef sk = r.workSkill;
                if (sk == null) continue;
                if (sk == SkillDefOf.Cooking) Add(st, WorkGroup.Cook);
                else if (sk == SkillDefOf.Medicine) Add(st, WorkGroup.Medical);
                else if (sk == SkillDefOf.Construction) Add(st, WorkGroup.Build);
                else if (sk == SkillDefOf.Mining) Add(st, WorkGroup.Mine);
                else if (sk == SkillDefOf.Plants) Add(st, WorkGroup.Plants);
                else if (sk == SkillDefOf.Crafting || sk == SkillDefOf.Artistic || sk == SkillDefOf.Intellectual) Add(st, WorkGroup.Craft);
            }
        }

        private static void Add(StatDef st, WorkGroup g)
        {
            if (st == null || installed.ContainsKey(st)) return;
            if (st.parts == null) st.parts = new List<StatPart>();
            var part = new StatPart_QoLSpeed { group = g, parentStat = st };
            st.parts.Add(part);
            installed[st] = g;
        }

        public static void ClearCaches()
        {
            foreach (StatDef st in installed.Keys)
            {
                try { st.Worker.TryClearCache(); } catch { }
            }
        }

        public static string StatList()
        {
            return string.Join(", ", installed.Keys.Select(s => s.LabelCap.ToString()).Distinct().OrderBy(s => s).ToArray());
        }
    }
}
