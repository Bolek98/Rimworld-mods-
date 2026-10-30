using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ColonyForeman;

public static class PriorityRange
{
	private static int cached = -1;

	public static int Max
	{
		get
		{
			// 2.1: manual setting wins (for priority mods whose range is not
			// detected automatically, e.g. after they renamed their classes).
			// The game itself only accepts 1–4; more levels need a priority mod,
			// otherwise every higher value would be rejected with an error.
			if (!PriorityModActive)
				return 4;
			int manual = ForemanMod.Settings?.maxPriorityOverride ?? 0;
			if (manual >= 4)
				return manual;
			if (cached > 0)
			{
				return cached;
			}
			int val = 4;
			val = Math.Max(val, Read("WorkTab.Settings", "maxPriority", null));
			val = Math.Max(val, Read("PriorityMod.Core.PriorityMaster", "settings", "maxPriority"));
			val = Math.Max(val, Read("PriorityMaster.Core.PriorityMaster", "settings", "maxPriority"));
			val = Math.Max(val, Read("Lauriichan.PriorityMaster.PriorityMaster", "settings", "maxPriority"));
			cached = Mathf.Clamp(val, 1, 99);
			return cached;
		}
	}

	private static int modActive = -1;

	public static bool PriorityModActive
	{
		get
		{
			if (modActive < 0)
			{
				bool found = false;
				foreach (Verse.ModContentPack m in Verse.LoadedModManager.RunningModsListForReading)
				{
					string id = (m.PackageId ?? "").ToLowerInvariant();
					if (id.Contains("prioritymaster") || id.Contains("prioritymod") || id.Contains("worktab"))
						found = true;
				}
				modActive = found ? 1 : 0;
			}
			return modActive == 1;
		}
	}

	private static int Read(string typeName, string member, string inner)
	{
		try
		{
			Type type = AccessTools.TypeByName(typeName);
			if (type == null)
			{
				return 0;
			}
			object obj = Member(type, null, member);
			if (inner != null)
			{
				obj = ((obj != null) ? Member(obj.GetType(), obj, inner) : null);
			}
			return (obj is int) ? ((int)obj) : 0;
		}
		catch
		{
			return 0;
		}
	}

	private static object Member(Type t, object instance, string name)
	{
		FieldInfo fieldInfo = AccessTools.Field(t, name);
		if (fieldInfo != null)
		{
			return fieldInfo.GetValue(instance);
		}
		PropertyInfo propertyInfo = AccessTools.Property(t, name);
		return (!(propertyInfo != null)) ? null : propertyInfo.GetValue(instance, null);
	}
}
