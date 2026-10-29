using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Vehicles;
using Verse;

namespace RideDrive
{
    // ---------------------------------------------------------------------------
    // More seats. Passenger seats of every vehicle × a multiplier, plus or minus seats per vehicle, and
    // a passenger place for vehicles that had none. It is done on the vehicle types themselves, so new
    // vehicles, vehicles in a loaded save and vehicles already on the map all get it; seats for the
    // driver and gunners are never touched. Nobody already sitting inside is thrown out when seats go
    // down – they just can't be replaced until there is room again.
    // ---------------------------------------------------------------------------
    public static class Seats
    {
        public const string AddedKey = "RD_Passengers";
        public static int ChangedVehicles, AddedVehicles;
        private static readonly Dictionary<VehicleRole, int> orig = new Dictionary<VehicleRole, int>();
        private static readonly Dictionary<VehicleDef, VehicleRole> added = new Dictionary<VehicleDef, VehicleRole>();
        private static List<VehicleDef> all;
        private static RDSettings S => RDMod.S;

        public static void Install(Harmony h)
        {
            all = DefDatabase<VehicleDef>.AllDefsListForReading.OrderBy(d => d.label ?? d.defName).ToList();
            foreach (VehicleDef d in all)
            {
                if (d.properties == null) continue;
                if (d.properties.roles == null) d.properties.roles = new List<VehicleRole>();
                foreach (VehicleRole r in d.properties.roles)
                    if (r.handlingTypes == HandlingType.None) orig[r] = r.slots;
                // a passenger place for vehicles without one – always on the type (with 0 seats when it is
                // switched off), so a save that has people in it still loads after the setting changes
                if (!d.properties.roles.Any(r => r.handlingTypes == HandlingType.None) && d.properties.roles.Count > 0)
                {
                    var role = new VehicleRole
                    {
                        key = AddedKey,
                        label = "RD_PassengerRole".Translate(),
                        handlingTypes = HandlingType.None,
                        slots = 0,
                        slotsToOperate = 0,
                        exposed = false,
                        chanceToHit = 0f,
                        hitbox = new ComponentHitbox { cells = new List<IntVec2> { new IntVec2(9999, 9999) } },
                    };
                    try { role.ResolveReferences(d); } catch { }
                    d.properties.roles.Add(role);
                    added[d] = role;
                    orig[role] = 0;
                }
            }
            h.Patch(AccessTools.Method(typeof(VehiclePawn), nameof(VehiclePawn.SpawnSetup)),
                postfix: new HarmonyMethod(typeof(Seats), nameof(SpawnPostfix)));
            Apply();
            Log.Message("[Ride & Drive] seats: " + ChangedVehicles + " vehicle types changed, " + AddedVehicles + " got a passenger place.");
        }

        // seats for a vehicle without passenger places: by its size, 1–4
        private static int AutoSeats(VehicleDef d)
        {
            int area = Math.Max(1, d.size.x * d.size.z);
            if (area < 4) return 0;
            return Mathf.Clamp(area / 6, 1, 4);
        }

        public static int Extra(VehicleDef d) => S.seatExtra != null && S.seatExtra.TryGetValue(d.defName, out int v) ? v : 0;

        public static int Want(VehicleDef d, VehicleRole r)
        {
            int baseSlots = orig.TryGetValue(r, out int o) ? o : r.slots;
            if (!S.seats) return baseSlots;
            int n;
            added.TryGetValue(d, out VehicleRole ar);
            if (ar != null && ar == r)
                n = S.addSeats ? AutoSeats(d) : 0;
            else
                n = Mathf.CeilToInt(baseSlots * S.passengerMult);
            // the per-vehicle extra goes to the vehicle's first own passenger place, else to the added one
            VehicleRole first = d.properties.roles.FirstOrDefault(x => x.handlingTypes == HandlingType.None && x != ar) ?? ar;
            if (first == r) n += Extra(d);
            return Math.Max(0, n);
        }

        public static void Apply()
        {
            if (all == null) return;
            ChangedVehicles = AddedVehicles = 0;
            foreach (VehicleDef d in all)
            {
                if (d.properties?.roles == null) continue;
                bool changed = false;
                foreach (VehicleRole r in d.properties.roles)
                {
                    if (r.handlingTypes != HandlingType.None || !orig.ContainsKey(r)) continue;
                    int w = Want(d, r);
                    if (w != orig[r]) changed = true;
                    r.slots = w;
                }
                if (changed) ChangedVehicles++;
                if (added.TryGetValue(d, out VehicleRole ar) && ar.slots > 0) AddedVehicles++;
            }
            // vehicles already on the maps
            if (Current.ProgramState == ProgramState.Playing && Find.Maps != null)
                foreach (Map m in Find.Maps)
                    foreach (Pawn p in m.mapPawns.AllPawnsSpawned.ToList())
                        if (p is VehiclePawn v) Refresh(v);
        }

        public static void SpawnPostfix(VehiclePawn __instance)
        {
            try { Refresh(__instance); }
            catch (Exception e) { Log.WarningOnce("[Ride & Drive] seats of " + __instance?.LabelShort + ": " + e.Message, 0x52440010); }
        }

        // the vehicle's own copy of each passenger place gets the type's number; the added passenger place
        // is put in when it should have seats and is missing
        public static void Refresh(VehiclePawn v)
        {
            VehicleDef d = v?.VehicleDef;
            if (d?.properties?.roles == null || v.handlers == null) return;
            foreach (VehicleRoleHandler hd in v.handlers)
            {
                if (hd?.role == null || hd.role.handlingTypes != HandlingType.None) continue;
                VehicleRole def = d.GetRole(hd.role.key);
                if (def != null) hd.role.slots = def.slots;
            }
            if (added.TryGetValue(d, out VehicleRole ar) && ar.slots > 0 && !v.handlers.Any(x => x?.role?.key == AddedKey))
                v.AddRole(d.CreateRole(AddedKey));
        }

        // ---------- per-vehicle editor ----------
        private static string filter = "";

        public static void DrawEditor(Listing_Standard l)
        {
            MUI.Section(l, "RD_SecPerVehicle".Translate());
            GUI.color = MUI.Muted;
            Text.Font = GameFont.Tiny;
            l.Label("RD_PerVehicle_Desc".Translate());
            Text.Font = GameFont.Small;
            GUI.color = Color.white;
            Rect fr = l.GetRect(28f);
            Widgets.Label(new Rect(fr.x, fr.y + 3f, 80f, 24f), "RD_Search".Translate());
            filter = Widgets.TextField(new Rect(fr.x + 80f, fr.y, Mathf.Min(260f, fr.width - 80f), 26f), filter ?? "");
            if (Widgets.ButtonText(new Rect(fr.xMax - 150f, fr.y, 150f, 26f), "RD_ResetAll".Translate()))
            {
                S.seatExtra.Clear();
                Apply();
            }
            l.Gap(6f);
            if (all == null) return;
            int i = 0;
            foreach (VehicleDef d in all)
            {
                if (!filter.NullOrEmpty() && (d.label ?? d.defName).IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                List<VehicleRole> pass = d.properties?.roles?.Where(r => r.handlingTypes == HandlingType.None).ToList();
                int baseN = pass == null ? 0 : pass.Sum(r => orig.TryGetValue(r, out int o) ? o : 0);
                int now = pass == null ? 0 : pass.Sum(r => r.slots);
                int crew = d.properties?.roles?.Where(r => r.handlingTypes != HandlingType.None).Sum(r => r.slots) ?? 0;
                Rect row = l.GetRect(30f);
                if (i++ % 2 == 0) Widgets.DrawLightHighlight(row);
                try { Widgets.DefIcon(new Rect(row.x + 2f, row.y + 3f, 24f, 24f), d); } catch { }
                Widgets.Label(new Rect(row.x + 32f, row.y + 4f, row.width * 0.45f, 24f), (d.LabelCap.ToString()).Truncate(row.width * 0.45f));
                GUI.color = MUI.Muted;
                Widgets.Label(new Rect(row.x + 32f + row.width * 0.45f, row.y + 4f, 190f, 24f), "RD_SeatsRow".Translate(crew, baseN, now));
                GUI.color = Color.white;
                int ex = Extra(d);
                Rect minus = new Rect(row.xMax - 104f, row.y + 2f, 30f, 26f);
                Rect val = new Rect(row.xMax - 72f, row.y + 2f, 40f, 26f);
                Rect plus = new Rect(row.xMax - 30f, row.y + 2f, 30f, 26f);
                if (Widgets.ButtonText(minus, "−") && now > 0) Set(d, ex - 1);
                Text.Anchor = TextAnchor.MiddleCenter;
                GUI.color = ex == 0 ? MUI.Muted : MUI.Accent;
                Widgets.Label(val, ex > 0 ? "+" + ex : ex.ToString());
                GUI.color = Color.white;
                Text.Anchor = TextAnchor.UpperLeft;
                if (Widgets.ButtonText(plus, "+") && ex < 20) Set(d, ex + 1);
            }
        }

        private static void Set(VehicleDef d, int v)
        {
            if (v == 0) S.seatExtra.Remove(d.defName);
            else S.seatExtra[d.defName] = v;
            Apply();
        }
    }
}
