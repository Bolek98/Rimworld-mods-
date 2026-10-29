using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace QoLFW
{
    // Recruiting: the drop of resistance in one talk is multiplied.
    [HarmonyPatch(typeof(InteractionWorker_RecruitAttempt), "Interacted")]
    public static class Patch_Recruit
    {
        public static void Prefix(Pawn recipient, out float __state)
        {
            __state = recipient?.guest != null ? recipient.guest.resistance : -1f;
        }

        public static void Postfix(Pawn recipient, float __state)
        {
            try
            {
                QSettings s = QoLMod.S;
                if (!s.prisoners || __state <= 0f || recipient?.guest == null || !recipient.IsPrisonerOfColony) return;
                float now = recipient.guest.resistance;
                float drop = __state - now;
                if (drop <= 0f || (now <= 0f && s.recruitMult >= 1f)) return;
                recipient.guest.resistance = Mathf.Max(0f, __state - drop * s.recruitMult);
            }
            catch (Exception e) { Log.WarningOnce("[QoL Faster Work] recruit: " + e.Message, 81723401); }
        }
    }

    // Enslaving: the drop of will in one talk is multiplied.
    [HarmonyPatch(typeof(InteractionWorker_EnslaveAttempt), "Interacted")]
    public static class Patch_Enslave
    {
        public static void Prefix(Pawn recipient, out float __state)
        {
            __state = recipient?.guest != null ? recipient.guest.will : -1f;
        }

        public static void Postfix(Pawn recipient, float __state)
        {
            try
            {
                QSettings s = QoLMod.S;
                if (!s.prisoners || __state <= 0f || recipient?.guest == null || !recipient.IsPrisonerOfColony) return;
                float now = recipient.guest.will;
                float drop = __state - now;
                if (drop <= 0f || (now <= 0f && s.enslaveMult >= 1f)) return;
                recipient.guest.will = Mathf.Max(0f, __state - drop * s.enslaveMult);
            }
            catch (Exception e) { Log.WarningOnce("[QoL Faster Work] enslave: " + e.Message, 81723402); }
        }
    }

    // Remembers since when every prisoner is held, makes unwavering prisoners
    // recruitable (now or after X days).
    public class QoLGameComp : GameComponent
    {
        public static QoLGameComp Instance;
        private Dictionary<int, int> heldSince = new Dictionary<int, int>();
        private List<int> tmpK;
        private List<int> tmpV;

        public QoLGameComp(Game game) { Instance = this; }

        public override void ExposeData()
        {
            Scribe_Collections.Look(ref heldSince, "heldSince", LookMode.Value, LookMode.Value, ref tmpK, ref tmpV);
            if (heldSince == null) heldSince = new Dictionary<int, int>();
        }

        public override void FinalizeInit() { Instance = this; }

        public int HeldSince(Pawn p)
        {
            int t;
            return heldSince.TryGetValue(p.thingIDNumber, out t) ? t : -1;
        }

        public float DaysHeld(Pawn p)
        {
            int t = HeldSince(p);
            return t < 0 ? 0f : (Find.TickManager.TicksGame - t) / 60000f;
        }

        // days left until an unwavering prisoner becomes recruitable (-1 = never / not unwavering)
        public float DaysLeft(Pawn p)
        {
            QSettings s = QoLMod.S;
            if (p.guest == null || p.guest.Recruitable || !s.prisoners || s.unwavering != Unwavering.AfterDays) return -1f;
            return Mathf.Max(0f, s.unwaveringDays - DaysHeld(p));
        }

        public override void GameComponentTick()
        {
            if (Find.TickManager.TicksGame % 2500 != 77) return;
            try { CheckPrisoners(); }
            catch (Exception e) { Log.WarningOnce("[QoL Faster Work] prisoners: " + e.Message, 81723403); }
        }

        private void CheckPrisoners()
        {
            int now = Find.TickManager.TicksGame;
            QSettings s = QoLMod.S;
            foreach (Map map in Find.Maps)
            {
                List<Pawn> list = map.mapPawns.PrisonersOfColony;
                for (int i = 0; i < list.Count; i++)
                {
                    Pawn p = list[i];
                    if (p?.guest == null) continue;
                    if (!heldSince.ContainsKey(p.thingIDNumber)) heldSince[p.thingIDNumber] = now;
                    if (!s.prisoners || s.unwavering == Unwavering.Off || p.guest.Recruitable) continue;
                    bool go = s.unwavering == Unwavering.Always || now - heldSince[p.thingIDNumber] >= s.unwaveringDays * 60000;
                    if (!go) continue;
                    p.guest.Recruitable = true;
                    Messages.Message("QFW_NowRecruitable".Translate(p.LabelShort), p, MessageTypeDefOf.PositiveEvent, false);
                }
            }
            if (heldSince.Count > 400)
            {
                // forget the oldest half (long gone prisoners)
                var keys = new List<int>(heldSince.Keys);
                keys.Sort((a, b) => heldSince[a].CompareTo(heldSince[b]));
                for (int i = 0; i < keys.Count / 2; i++) heldSince.Remove(keys[i]);
            }
        }
    }
}
