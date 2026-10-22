using System;
using Verse;

namespace ColonyForeman;

// New values are appended (saved maps keep the bits).
[Flags]
public enum Crisis : long
{
	None = 0,
	Medical = 1,
	Food = 2,
	Winter = 4,
	// Steward 2.13
	Fire = 8,
	Raid = 16,
	Weather = 32,
	Toxic = 64,
	Blight = 128,
	Mood = 256,
	Power = 512,
	Medicine = 1024,
	Breakdown = 2048,
	// Steward 2.29
	Manhunters = 1L << 12,
	Epidemic = 1L << 13,
	PrisonBreak = 1L << 14,
	DangerousBreak = 1L << 15,
	IndoorTemp = 1L << 16,
	NoBeds = 1L << 17,
	FreezerThaw = 1L << 18,
	Siege = 1L << 19,
	Infestation = 1L << 20,
	RoofCollapse = 1L << 21,
	Death = 1L << 22,
	QuestDeadline = 1L << 23,
	AnimalSick = 1L << 24,
	Labor = 1L << 25,
	EntityEscape = 1L << 26,
	Darkness = 1L << 27,
	VacuumBreach = 1L << 28,
	ToxGas = 1L << 29,
	LowAmmo = 1L << 30,
	WaterShortage = 1L << 31,
	Hygiene = 1L << 32,
	NoFirefighters = 1L << 33
}

public static class CrisisText
{
	public static string Label(Crisis c)
	{
		var list = new System.Collections.Generic.List<string>();
		foreach (Crisis f in Enum.GetValues(typeof(Crisis)))
			if (f != Crisis.None && (c & f) != 0)
				list.Add(("CF_Crisis_" + f).Translate());
		return string.Join(", ", list);
	}
}
