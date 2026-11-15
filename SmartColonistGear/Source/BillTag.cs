using RimWorld;
using Verse;

namespace ResearchedLoadout
{
	// 2.17.1: bills the mod adds by itself get a name that says who added them and why –
	// "Kiiro Simple Coat – SCG: cold, 2 missing". It is the game's own bill name (rename), so it is saved
	// with the bill, shows in every bill list and you can rename it.
	public static class BillTag
	{
		public static void Set(Bill_Production bill, ThingDef product, string whyKey, int count)
		{
			try
			{
				if (bill == null || product == null) return;
				string why = whyKey.Translate();
				if (count > 0) why = "RL_BillTagMissing".Translate(why, count);
				bill.RenamableLabel = "RL_BillTag".Translate(product.LabelCap, why);
			}
			catch { }
		}

		public static string WhyStat(StatDef stat)
		{
			if (stat == StatDefOf.Insulation_Cold) return "RL_BillWhy_Cold";
			if (stat == StatDefOf.Insulation_Heat) return "RL_BillWhy_Heat";
			if (stat == StatDefOf.ToxicEnvironmentResistance) return "RL_BillWhy_Toxic";
			if (stat == StatDefOf.ArmorRating_Sharp) return "RL_BillWhy_Attack";
			if (stat?.defName == "VacuumResistance") return "RL_BillWhy_Vacuum";
			return "RL_BillWhy_Protect";
		}

		public static string WhyKind(int kind)
		{
			if ((kind & EmergencyGear.Toxic) != 0) return "RL_BillWhy_Toxic";
			if ((kind & EmergencyGear.Cold) != 0) return "RL_BillWhy_Cold";
			if ((kind & EmergencyGear.Heat) != 0) return "RL_BillWhy_Heat";
			if ((kind & EmergencyGear.Raid) != 0) return "RL_BillWhy_Attack";
			return "RL_BillWhy_Protect";
		}
	}
}
