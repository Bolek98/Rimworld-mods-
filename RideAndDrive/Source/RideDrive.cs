using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace RideDrive
{
    // what happens to the animal when its rider gets into a vehicle
    public enum AnimalPlace { Cargo, Wait, Free }

    public class RDSettings : ModSettings
    {
        public bool recommended = true;

        // safe riding (Giddy-Up)
        public bool safeMounting = true;      // repair a broken Giddy-Up start, catch failed mounting
        public bool mountPause = true;        // a rider whose mounting keeps failing waits before trying again
        public float pauseHours = 2f;

        // riders and vehicles
        public bool dismountToBoard = true;   // a rider gets off before boarding a vehicle
        public AnimalPlace animalPlace = AnimalPlace.Cargo;
        public bool rideToVehicle = true;     // ride right up to the vehicle instead of parking 25 cells away
        public bool remountAfter = true;      // get back on the own animal after leaving a vehicle
        public bool caravanMounts = true;     // vehicle caravans: walkers ride their animals, remount on arrival
        public bool keepOwnership = true;     // the animal stays "yours" while you are in the vehicle

        // seats
        public bool seats = true;
        public float passengerMult = 2f;      // passenger seats ×
        public bool addSeats = true;          // vehicles with no passenger seats get some
        public Dictionary<string, int> seatExtra = new Dictionary<string, int>();   // per vehicle: + / − seats

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref recommended, "recommended", true);
            Scribe_Values.Look(ref safeMounting, "safeMounting", true);
            Scribe_Values.Look(ref mountPause, "mountPause", true);
            Scribe_Values.Look(ref pauseHours, "pauseHours", 2f);
            Scribe_Values.Look(ref dismountToBoard, "dismountToBoard", true);
            Scribe_Values.Look(ref animalPlace, "animalPlace", AnimalPlace.Cargo);
            Scribe_Values.Look(ref rideToVehicle, "rideToVehicle", true);
            Scribe_Values.Look(ref remountAfter, "remountAfter", true);
            Scribe_Values.Look(ref caravanMounts, "caravanMounts", true);
            Scribe_Values.Look(ref keepOwnership, "keepOwnership", true);
            Scribe_Values.Look(ref seats, "seats", true);
            Scribe_Values.Look(ref passengerMult, "passengerMult", 2f);
            Scribe_Values.Look(ref addSeats, "addSeats", true);
            Scribe_Collections.Look(ref seatExtra, "seatExtra", LookMode.Value, LookMode.Value);
            if (seatExtra == null) seatExtra = new Dictionary<string, int>();
        }
    }

    public class RDMod : Mod
    {
        public const string Version = "0.1.0";
        public static RDSettings S;
        public static Harmony H;
        private static List<string> recBullets;
        private static int page;
        private static Vector2 scroll;
        private static float viewH = 1200f;

        public RDMod(ModContentPack content) : base(content)
        {
            S = GetSettings<RDSettings>();
            MUI.Init(new RDSettings(), null, "recommended");
            if (S.recommended) MUI.ApplyAll(S);
            MNews.Register(content, Version);
            H = new Harmony("bolek98.rideanddrive");
            try { MUI.PatchWindow(H, typeof(RDMod)); } catch { }
            Log.Message("[Ride & Drive] v" + Version + " loaded.");
        }

        public override string SettingsCategory() => "Ride & Drive";

        public override void WriteSettings()
        {
            base.WriteSettings();
            try { if (Compat.Vehicles) SeatsApply(); } catch (Exception e) { Log.Warning("[Ride & Drive] seats: " + e.Message); }
        }

        private static void Check(Listing_Standard l, string key, ref bool v, string field) =>
            MUI.Check(l, key.Translate(), (key + "_Desc").Translate(), ref v, S, field);

        public override void DoSettingsWindowContents(Rect r)
        {
            MUI.Version(r, "v" + Version + " · Bolek98");
            if (recBullets == null)
                recBullets = new List<string> { "RD_Rec1".Translate(), "RD_Rec2".Translate(), "RD_Rec3".Translate(), "RD_Rec4".Translate() };
            float y = r.y;
            y += MUI.RecommendedCard(new Rect(r.x, y, r.width, 0f), S, S.recommended, v => { S.recommended = v; if (v) MUI.ApplyAll(S); }, recBullets) + 6f;
            bool lockable = page != 3;
            if (S.recommended && lockable) y += MUI.LockNote(new Rect(r.x, y, r.width, 0f)) + 4f;

            string[] pages = { "RD_PageSafety", "RD_PageVehicles", "RD_PageSeats", "RD_PageStatus" };
            float bw = r.width / pages.Length;
            for (int i = 0; i < pages.Length; i++)
            {
                Rect b = new Rect(r.x + i * bw + 2f, y, bw - 4f, 30f);
                MUI.Fill(b, page == i ? MUI.OnBg : MUI.CardBg);
                MUI.Frame(b, page == i ? MUI.Accent : MUI.Border);
                Widgets.DrawHighlightIfMouseover(b);
                Text.Anchor = TextAnchor.MiddleCenter;
                GUI.color = page == i ? MUI.Accent : MUI.Text1;
                Widgets.Label(b, pages[i].Translate());
                GUI.color = Color.white;
                Text.Anchor = TextAnchor.UpperLeft;
                if (Widgets.ButtonInvisible(b) && page != i) { page = i; scroll = Vector2.zero; }
            }
            y += 38f;

            Rect rest = new Rect(r.x, y, r.width, r.yMax - y);
            Rect view = new Rect(0f, 0f, rest.width - 18f, viewH);
            bool locked = S.recommended && lockable;
            Widgets.BeginScrollView(rest, ref scroll, view);
            var l = new Listing_Standard { maxOneColumn = true };
            MUI.BeginLock(locked);
            try
            {
                l.Begin(new Rect(0f, 0f, view.width, 99999f));
                switch (page)
                {
                    case 0: PageSafety(l); break;
                    case 1: PageVehicles(l); break;
                    case 2: PageSeats(l); break;
                    default: PageStatus(l); break;
                }
                viewH = l.CurHeight + 20f;
                l.End();
            }
            finally
            {
                MUI.EndLock();
                Widgets.EndScrollView();
            }
            if (locked) MUI.Fill(rest, new Color(0.09f, 0.1f, 0.12f, 0.35f));
        }

        private static void Missing(Listing_Standard l, string key)
        {
            GUI.color = MUI.Warn;
            l.Label(key.Translate());
            GUI.color = Color.white;
            l.Gap(6f);
        }

        private static void PageSafety(Listing_Standard l)
        {
            MUI.Section(l, "RD_SecSafety".Translate());
            if (!Compat.GiddyUp) Missing(l, "RD_NoGiddyUp");
            Check(l, "RD_SafeMounting", ref S.safeMounting, "safeMounting");
            Check(l, "RD_MountPause", ref S.mountPause, "mountPause");
            if (S.mountPause)
                S.pauseHours = Mathf.Round(MUI.Slider(l, "RD_PauseHours".Translate(S.pauseHours.ToString("0.#")), S.pauseHours, 0.5f, 12f, "RD_PauseHours_Desc".Translate(), S, "pauseHours") * 2f) / 2f;
        }

        private static void PageVehicles(Listing_Standard l)
        {
            MUI.Section(l, "RD_SecVehicles".Translate());
            if (!Compat.GiddyUp || !Compat.Vehicles) Missing(l, "RD_NeedBoth");
            Check(l, "RD_DismountToBoard", ref S.dismountToBoard, "dismountToBoard");
            if (S.dismountToBoard)
            {
                MUI.RowLabel(l, "RD_AnimalPlace".Translate(), "RD_AnimalPlace_Desc".Translate(), S, "animalPlace");
                Rect row = l.GetRect(30f);
                var vals = (AnimalPlace[])Enum.GetValues(typeof(AnimalPlace));
                float w = row.width / vals.Length;
                for (int i = 0; i < vals.Length; i++)
                {
                    Rect b = new Rect(row.x + i * w + 2f, row.y, w - 4f, row.height);
                    bool on = S.animalPlace == vals[i];
                    MUI.Fill(b, on ? MUI.OnBg : MUI.CardBg);
                    MUI.Frame(b, on ? MUI.Accent : MUI.Border);
                    Widgets.DrawHighlightIfMouseover(b);
                    Text.Anchor = TextAnchor.MiddleCenter;
                    GUI.color = on ? MUI.Accent : MUI.Text1;
                    Widgets.Label(b, ("RD_Place_" + vals[i]).Translate());
                    GUI.color = Color.white;
                    Text.Anchor = TextAnchor.UpperLeft;
                    TooltipHandler.TipRegion(b, ("RD_Place_" + vals[i] + "_Desc").Translate());
                    if (Widgets.ButtonInvisible(b)) S.animalPlace = vals[i];
                }
                l.Gap(6f);
            }
            Check(l, "RD_RideToVehicle", ref S.rideToVehicle, "rideToVehicle");
            Check(l, "RD_RemountAfter", ref S.remountAfter, "remountAfter");
            Check(l, "RD_KeepOwnership", ref S.keepOwnership, "keepOwnership");
            MUI.Section(l, "RD_SecCaravans".Translate());
            Check(l, "RD_CaravanMounts", ref S.caravanMounts, "caravanMounts");
        }

        private static void PageSeats(Listing_Standard l)
        {
            MUI.Section(l, "RD_SecSeats".Translate());
            if (!Compat.Vehicles) { Missing(l, "RD_NoVehicles"); return; }
            Check(l, "RD_Seats", ref S.seats, "seats");
            if (!S.seats) return;
            S.passengerMult = Mathf.Round(MUI.Slider(l, "RD_PassengerMult".Translate(S.passengerMult.ToString("0.##")), S.passengerMult, 1f, 4f, "RD_PassengerMult_Desc".Translate(), S, "passengerMult") * 4f) / 4f;
            Check(l, "RD_AddSeats", ref S.addSeats, "addSeats");
            l.Gap(8f);
            SeatsEditor(l);
        }

        private static void PageStatus(Listing_Standard l)
        {
            MUI.Section(l, "RD_SecStatus".Translate());
            Line(l, "RD_StGiddyUp", Compat.GiddyUp);
            Line(l, "RD_StVehicles", Compat.Vehicles);
            Line(l, "RD_StCompanion", Compat.CompanionMounts);
            Line(l, "RD_StPassengers", Compat.MountPassengers);
            l.Gap(6f);
            if (Compat.GiddyUp)
            {
                GUI.color = GuSafety.SetupBroken ? MUI.Warn : MUI.Accent;
                l.Label((GuSafety.SetupBroken ? "RD_StSetupRepaired" : "RD_StSetupOk").Translate());
                GUI.color = MUI.Muted;
                l.Label("RD_StCounts".Translate(GuSafety.Rollbacks, GuSafety.Paused, VehicleBridge.Dismounts, VehicleBridge.Remounts));
                GUI.color = Color.white;
            }
            if (Compat.Vehicles)
            {
                GUI.color = MUI.Muted;
                l.Label(SeatsStatus());
                GUI.color = Color.white;
            }
        }

        // kept apart so the game never has to look at Vehicle Framework types when it is not there
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void SeatsApply() => Seats.Apply();
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void SeatsEditor(Listing_Standard l) => Seats.DrawEditor(l);
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static string SeatsStatus() => "RD_StSeats".Translate(Seats.ChangedVehicles, Seats.AddedVehicles);

        private static void Line(Listing_Standard l, string key, bool ok)
        {
            GUI.color = ok ? MUI.Accent : MUI.Muted;
            l.Label((ok ? "✓ " : "– ") + key.Translate().ToString() + (ok ? "" : " (" + "RD_StNotFound".Translate().ToString() + ")"));
            GUI.color = Color.white;
        }
    }

    // which frameworks are in the game – everything that touches them is installed only when they are
    public static class Compat
    {
        public static bool GiddyUp, Vehicles, CompanionMounts, MountPassengers;

        public static void Detect()
        {
            GiddyUp = AccessTools.TypeByName("GiddyUp.MountUtility") != null;
            Vehicles = AccessTools.TypeByName("Vehicles.VehiclePawn") != null;
            CompanionMounts = AccessTools.TypeByName("CompanionMounts.BondedMountUtility") != null;
            MountPassengers = AccessTools.TypeByName("MountPassengers.MountPassengerUtility") != null;
        }
    }

    [StaticConstructorOnStartup]
    public static class RDStartup
    {
        static RDStartup()
        {
            Compat.Detect();
            Log.Message("[Ride & Drive] found: Giddy-Up " + Compat.GiddyUp + ", Vehicle Framework " + Compat.Vehicles
                + ", Companion Mounts " + Compat.CompanionMounts + ", Mount Passengers " + Compat.MountPassengers + ".");
            if (Compat.GiddyUp)
                Try("riding safety", () => GuSafety.Install(RDMod.H));
            if (Compat.Vehicles)
                Try("seats", () => Seats.Install(RDMod.H));
            if (Compat.GiddyUp && Compat.Vehicles)
                Try("riders and vehicles", () => VehicleBridge.Install(RDMod.H));
        }

        private static void Try(string what, Action a)
        {
            try { a(); }
            catch (Exception e) { Log.Error("[Ride & Drive] " + what + " could not start: " + e); }
        }
    }
}
