using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace ColonyForeman;

// How well a pawn suits a work type (about 0 … 1.5). Read from the game:
// the work's own skills (mods' skills too), passions, learning, work speed,
// health and your preferences. Nothing is fixed per work type.
public static class Fitness
{
	public static float Of(Pawn p, WorkTypeDef w, ForemanSettings s, ForemanGameComponent game)
	{
		float f;
		List<SkillDef> skills = w.relevantSkills;
		if (skills == null || skills.Count == 0 || p.skills == null)
		{
			// no skill: everybody alike, the faster walker a little better
			f = 0.35f + 0.1f * (Moving(p) - 1f);
		}
		else
		{
			float level = 0f, passion = 0f;
			int n = 0;
			foreach (SkillDef sd in skills)
			{
				SkillRecord r = p.skills.GetSkill(sd);
				if (r == null || r.TotallyDisabled)
					continue;
				level += r.Level;
				passion += PassionValue(r);
				n++;
			}
			if (n == 0)
				return 0f;
			level /= n;
			passion /= n;
			f = level / 20f + passion * (s != null && s.moodPassion ? 0.3f : 0.2f);
			// learning: a young colonist with passion grows into the work
			if (p.ageTracker != null && !p.ageTracker.Adult)
				f += passion * 0.1f;
		}
		// work speed (tools, bionics, genes, traits – whatever mods add to the stat)
		try
		{
			float speed = p.GetStatValue(StatDefOf.WorkSpeedGlobal, true, 600);
			f += Mathf.Clamp(speed - 1f, -0.5f, 0.5f) * 0.15f;
		}
		catch
		{
		}
		// hurt or sick: lighter work
		if (s != null && s.lightWorkInjured && p.health != null)
		{
			float hp = p.health.summaryHealth.SummaryHealthPercent;
			if (hp < 0.75f)
				f -= (0.75f - hp) * 0.5f;
		}
		if (WorkCatalog.Opposed(p, w))
			f -= 0.4f;
		if (game != null && game.Prefers(p, w))
			f += s?.preferenceBonus ?? 0.25f;
		return f;
	}

	// passion value: vanilla None/Minor/Major, mods' extra passions by their learning factor
	private static float PassionValue(SkillRecord r)
	{
		switch (r.passion)
		{
			case Passion.None: return 0f;
			case Passion.Minor: return 0.5f;
			case Passion.Major: return 1f;
		}
		try
		{
			// unknown passion from a mod: judge by how fast it learns
			float lf = r.LearnRateFactor(true);
			return Mathf.Clamp((lf - 0.35f) / 1.15f, -0.5f, 1.5f);
		}
		catch
		{
			return 0f;
		}
	}

	private static float Moving(Pawn p)
	{
		try
		{
			return p.health?.capacities?.GetLevel(PawnCapacityDefOf.Moving) ?? 1f;
		}
		catch
		{
			return 1f;
		}
	}
}
