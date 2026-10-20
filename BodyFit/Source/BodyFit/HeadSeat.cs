using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace BodyFit;

// 2.4 (testing first): the head sits on the body. Measured exactly as the game draws it – the body
// and head pictures' outlines placed with the pawn's real draw matrices – and compared with how a
// human head sits on a human body (how far the chin goes below the top of the body, relative to the
// head height). A race whose head clearly floats above the neck or sinks into the chest is moved
// back; the size of the head is left as the race made it. Everything on the head moves with it.
public static class HeadSeat
{
	public static bool Measuring;

	// (race, body type, head type, rotation) -> vertical shift; NaN = not measured yet
	private static readonly Dictionary<(ThingDef, BodyTypeDef, HeadTypeDef, int), float> shifts = new Dictionary<(ThingDef, BodyTypeDef, HeadTypeDef, int), float>();
	private static readonly Dictionary<int, List<float>> human = new Dictionary<int, List<float>>();
	private static readonly Dictionary<int, float> humanRef = new Dictionary<int, float>();
	public static readonly List<string> Notes = new List<string>();

	public static bool Active => Shots.Loose;   // testing only for now

	public static void Clear()
	{
		shifts.Clear();
		human.Clear();
		humanRef.Clear();
		Notes.Clear();
	}

	// main thread, outside drawing (pictures are read through the GPU)
	public static bool Measure(Pawn p, Rot4 rot, out float sink, out float headH, out string info)
	{
		sink = headH = 0f;
		info = null;
		var tree = p?.Drawer?.renderer?.renderTree;
		if (tree == null || p.story?.headType == null || p.story.bodyType == null)
			return false;
		if (!tree.TryGetNodeByTag(PawnRenderNodeTagDefOf.Body, out PawnRenderNode bn) || !tree.TryGetNodeByTag(PawnRenderNodeTagDefOf.Head, out PawnRenderNode hn))
			return false;
		var parms = default(PawnDrawParms);
		parms.pawn = p;
		parms.matrix = Matrix4x4.identity;
		parms.facing = rot;
		parms.rotDrawMode = RotDrawMode.Fresh;
		parms.posture = PawnPosture.Standing;
		parms.flags = PawnRenderFlags.Portrait | PawnRenderFlags.Headgear | PawnRenderFlags.Clothes;
		string bp = bn.PrimaryGraphic?.path, hp = hn.PrimaryGraphic?.path;
		if (bp.NullOrEmpty() || hp.NullOrEmpty())
			return false;
		bool was = BF.TraceAllowed;
		BF.TraceAllowed = true;
		Rect? bs, hs;
		try
		{
			bs = Silhouette.Of(bp, rot);
			hs = Silhouette.Of(hp, rot);
		}
		finally
		{
			BF.TraceAllowed = was;
		}
		if (bs == null || hs == null)
			return false;
		Measuring = true;
		try
		{
			if (!tree.TryGetMatrix(bn, parms, out Matrix4x4 bm) || !tree.TryGetMatrix(hn, parms, out Matrix4x4 hm))
				return false;
			Mesh bmesh = bn.GetMesh(parms), hmesh = hn.GetMesh(parms);
			if (bmesh == null || hmesh == null)
				return false;
			Vector3 bsz = bmesh.bounds.size, hsz = hmesh.bounds.size;
			float bodyTop = bm.MultiplyPoint3x4(new Vector3(0f, 0f, (bs.Value.yMax - 0.5f) * bsz.z)).z;
			float bodyBottom = bm.MultiplyPoint3x4(new Vector3(0f, 0f, (bs.Value.yMin - 0.5f) * bsz.z)).z;
			float chin = hm.MultiplyPoint3x4(new Vector3(0f, 0f, (hs.Value.yMin - 0.5f) * hsz.z)).z;
			float crown = hm.MultiplyPoint3x4(new Vector3(0f, 0f, (hs.Value.yMax - 0.5f) * hsz.z)).z;
			headH = crown - chin;
			if (headH < 0.05f)
				return false;
			sink = (bodyTop - chin) / headH;
			info = $"bodyTop={bodyTop:0.000} bodyBottom={bodyBottom:0.000} chin={chin:0.000} crown={crown:0.000} headH={headH:0.000} sink={sink:0.00} body={bp} head={hp}";
			return true;
		}
		catch (Exception ex)
		{
			info = "error " + ex.Message;
			return false;
		}
		finally
		{
			Measuring = false;
		}
	}

	// called by the picture taker for every pawn (main thread, outside drawing)
	public static void Learn(Pawn p)
	{
		if (p?.story == null)
			return;
		for (int r = 0; r < 4; r++)
		{
			Rot4 rot = new Rot4(r);
			if (!Measure(p, rot, out float sink, out float headH, out string info))
				continue;
			if (p.def == ThingDefOf.Human)
			{
				if (!human.TryGetValue(r, out var l))
					human[r] = l = new List<float>();
				l.Add(sink);
				l.Sort();
				humanRef[r] = l[l.Count / 2];
				Notes.Add("human " + p.LabelShort + " " + rot + ": " + info);
				continue;
			}
			var key = (p.def, p.story.bodyType, p.story.headType, r);
			if (!humanRef.TryGetValue(r, out float refSink))
			{
				Notes.Add("no human yet " + p.LabelShort + " " + rot + ": " + info);
				continue;
			}
			float diff = sink - refSink;   // > 0: sunk in, < 0: floating
			float shift = 0f;
			if (Mathf.Abs(diff) > 0.08f)
				shift = Mathf.Clamp(diff * headH, -0.25f, 0.25f);
			shifts[key] = shift;
			Notes.Add($"{p.def.defName} {p.LabelShort} {rot}: ref={refSink:0.00} diff={diff:0.00} shift={shift:0.000} | {info}");
		}
	}

	public static float ShiftFor(Pawn p, Rot4 rot)
	{
		if (!Active || Measuring || p?.story?.headType == null || p.def == ThingDefOf.Human)
			return 0f;
		return shifts.TryGetValue((p.def, p.story.bodyType, p.story.headType, rot.AsInt), out float s) ? s : 0f;
	}
}

[HarmonyPatch(typeof(PawnRenderNodeWorker_Head), nameof(PawnRenderNodeWorker_Head.OffsetFor))]
public static class Patch_HeadSeat
{
	public static void Postfix(PawnDrawParms parms, ref Vector3 __result)
	{
		try
		{
			if (!HeadSeat.Active || !BF.Handles(parms.pawn))
				return;
			__result.z += HeadSeat.ShiftFor(parms.pawn, parms.facing);
		}
		catch
		{
		}
	}
}
