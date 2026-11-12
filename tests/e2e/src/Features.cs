using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using BepInEx.Configuration;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using TerroritySmellatory;

namespace TerrE2E
{
    /// <summary>
    /// What the DLL under test can do, found by LOOKING at it (reflection), never by trusting its version number:
    /// the driver is compiled against whichever DLL run-e2e.sh is given, 0.1.1 and 0.2.0 alike, so a member that only
    /// 0.2.0 has (combined territories with their parts, the hammer's combine command, the Epic entry banner) is
    /// reached here by name. On 0.1.1 every answer is "no" and the 0.2.0 phase is skipped with one INFO line; on a
    /// 0.2.0 DLL a member that cannot be found makes the phase skip too, and run-e2e.sh's H12 then FAILS the run,
    /// because it knows from the DLL's own "loaded" line which ids must have reported.
    /// </summary>
    internal static class Features
    {
        private static readonly Assembly Mod = typeof(Territory).Assembly;

        private static readonly PropertyInfo PartCountProp = AccessTools.Property(typeof(Territory), "PartCount");
        private static readonly MethodInfo GetPartMethod = AccessTools.Method(typeof(Territory), "GetPart", new[] { typeof(int) });
        private static readonly FieldInfo CombineCommandField = AccessTools.Field(typeof(InfinityHammerBridge), "CombineCommand");
        private static readonly Type EntryBannerType = Mod.GetType("TerroritySmellatory.EntryBanner");
        private static readonly FieldInfo EntryBannerStyleField = Mod.GetType("TerroritySmellatory.TSConfig")?.GetField(
            "EntryBanner", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

        /// <summary>Combined territories: a territory with parts, and the hammer's terr_combine_at (0.2.0).</summary>
        internal static bool Combine => PartCountProp != null && GetPartMethod != null && CombineCommandField != null;

        /// <summary>
        /// The 0.2.0 entry banner is the Epic one: vanilla's biome-found banner in MessageHud.m_biomeMsgInstance (the
        /// class exists) and this client's EntryBannerStyle is Epic (the default). Otherwise the entering text is the
        /// centre message of 0.1.1, and the old checks apply.
        /// </summary>
        internal static bool EpicBanner
        {
            get
            {
                if (EntryBannerType == null || EntryBannerStyleField == null) return false;
                try { return (EntryBannerStyleField.GetValue(null) as ConfigEntryBase)?.BoxedValue?.ToString() == "Epic"; }
                catch { return false; }
            }
        }

        // ── 0.3.0: the runic barrier. Its flags are found by NAME in the enum (the values are the mod's business: the
        //    driver compiles against 0.1.1, 0.2.0 and 0.3.0 alike), and its two statics by reflection.

        private static readonly string[] FlagNames = Enum.GetNames(typeof(TerritoryFlags));
        private static readonly Type FlareType = Mod.GetType("TerroritySmellatory.BarrierFlare");
        private static readonly FieldInfo RequestedField = FlareType?.GetField("Requested", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly PropertyInfo RequestedProp = FlareType?.GetProperty("Requested", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly PropertyInfo GroundProp = typeof(Territory).GetProperty("Ground", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        /// <summary>TerritoryFlags has RunicBarrier (0.3.0). The 0.3.0 phase runs only then.</summary>
        internal static bool HasBarrierFlag => FlagNames.Contains("RunicBarrier");

        /// <summary>
        /// The 0.3.0 phase runs: the DLL has the flag, or the run is a DRY RUN (TERR_E2E_FORCE030=1, run-e2e.sh's
        /// E2E_FORCE_030=1) that plays the phase on a DLL without the barrier to prove the barrier checks can go red.
        /// </summary>
        internal static bool Barrier => HasBarrierFlag || E2EEnv.Force030;

        internal static TerritoryFlags RunicBarrier => FlagNames.Contains("RunicBarrier") ? (TerritoryFlags)Enum.Parse(typeof(TerritoryFlags), "RunicBarrier") : 0;

        internal static TerritoryFlags KeepsOutVisitors => FlagNames.Contains("BarrierKeepsOutVisitors") ? (TerritoryFlags)Enum.Parse(typeof(TerritoryFlags), "BarrierKeepsOutVisitors") : 0;

        // ── 0.3.1: auto-close doors (TerritoryFlags.AutoCloseDoors, closed by the SERVER; Rules/DoorCloser.cs) ──

        /// <summary>TerritoryFlags has AutoCloseDoors (0.3.1). The door phase runs only then.</summary>
        internal static bool HasAutoCloseFlag => FlagNames.Contains("AutoCloseDoors");

        /// <summary>The door phase runs: the DLL has the flag, or the run is a DRY RUN (TERR_E2E_FORCE031=1 / E2E_FORCE_031=1) on a DLL without it.</summary>
        internal static bool Doors => HasAutoCloseFlag || E2EEnv.Force031;

        internal static TerritoryFlags AutoCloseDoors => FlagNames.Contains("AutoCloseDoors") ? (TerritoryFlags)Enum.Parse(typeof(TerritoryFlags), "AutoCloseDoors") : 0;

        /// <summary>This process's own TSConfig.AutoCloseDelay (on a client the value ServerSync handed it), or -1 when the DLL has no such setting.</summary>
        internal static float AutoCloseDelayConfigured
        {
            get
            {
                try
                {
                    FieldInfo f = Mod.GetType("TerroritySmellatory.TSConfig")?.GetField("AutoCloseDelay", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    object entry = f?.GetValue(null);
                    return entry is ConfigEntryBase e ? Convert.ToSingle(e.BoxedValue) : -1f;
                }
                catch { return -1f; }
            }
        }

        internal static bool HasFlareCounter => RequestedField != null || RequestedProp != null;

        /// <summary>BarrierFlare.Requested (accepted flares, counted even headless); -1 when the DLL has no such counter.</summary>
        internal static int FlareRequested
        {
            get
            {
                try
                {
                    if (RequestedField != null) return Convert.ToInt32(RequestedField.GetValue(null));
                    if (RequestedProp != null) return Convert.ToInt32(RequestedProp.GetValue(null, null));
                }
                catch { }
                return -1;
            }
        }

        /// <summary>BarrierFlare.Drawn (flares put on screen; 0 with no graphics device, which is what a headless client is); -1 when absent.</summary>
        internal static int FlareDrawn
        {
            get
            {
                try
                {
                    FieldInfo f = FlareType?.GetField("Drawn", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    return f != null ? Convert.ToInt32(f.GetValue(null)) : -1;
                }
                catch { return -1; }
            }
        }

        /// <summary>The mod's own TerritoryGround.DistanceOutside(p) for the whole territory (0 inside); NaN on a DLL without grounds.</summary>
        internal static float DistanceOutside(Territory t, Vector3 p)
        {
            if (GroundProp == null || t == null) return float.NaN;
            object ground = GroundProp.GetValue(t, null);
            MethodInfo m = ground.GetType().GetMethod("DistanceOutside", new[] { typeof(Vector3) });
            return m == null ? float.NaN : Convert.ToSingle(m.Invoke(ground, new object[] { p }));
        }

        internal static string Describe() =>
            $"combined territories {(Combine ? "yes" : "no")}, Epic entry banner {(EpicBanner ? "yes" : "no")}, runic barrier {(HasBarrierFlag ? "yes" : "no")}";

        /// <summary>The hammer's combine tool line, the way the mod's own template reads (a const in 0.2.0).</summary>
        internal static string CombineTemplate => (string)CombineCommandField.GetValue(null);

        internal static int PartCount(Territory t) => PartCountProp != null ? (int)PartCountProp.GetValue(t, null) : 1;

        /// <summary>One part of a territory's ground, read field by field from the mod's TerritoryShape.</summary>
        internal sealed class Part
        {
            public string Kind;
            public float X, Z, HalfX, HalfZ, Yaw;

            public override string ToString() => $"{Kind}@({World.F(X)},{World.F(Z)}) half=({World.F(HalfX)},{World.F(HalfZ)}) yaw={World.F(Yaw)}";

            public bool Same(Part o) =>
                Kind == o.Kind && Near(X, o.X) && Near(Z, o.Z) && Near(HalfX, o.HalfX) && Near(HalfZ, o.HalfZ) && Near(Yaw, o.Yaw);

            private static bool Near(float a, float b) => Math.Abs(a - b) < 0.01f;

            /// <summary>The keys of a "parts" entry in the territory file (and of the barrier files that carry parts).</summary>
            public JObject ToJson() => new JObject { ["shape"] = Kind, ["x"] = X, ["z"] = Z, ["halfX"] = HalfX, ["halfZ"] = HalfZ, ["yaw"] = Yaw };

            public static Part FromJson(JObject o) =>
                new Part { Kind = (string)o["shape"], X = (float)o["x"], Z = (float)o["z"], HalfX = (float)o["halfX"], HalfZ = (float)o["halfZ"], Yaw = (float)o["yaw"] };
        }

        internal static Part GetPart(Territory t, int index)
        {
            object shape = GetPartMethod.Invoke(t, new object[] { index });
            Type ty = shape.GetType();
            float F(string field) => Convert.ToSingle(ty.GetField(field).GetValue(shape));
            return new Part { Kind = ty.GetField("Kind").GetValue(shape).ToString(), X = F("CenterX"), Z = F("CenterZ"), HalfX = F("HalfX"), HalfZ = F("HalfZ"), Yaw = F("Yaw") };
        }

        internal static List<Part> Parts(Territory t)
        {
            var list = new List<Part>();
            for (int i = 0; i < PartCount(t); i++) list.Add(GetPart(t, i));
            return list;
        }
    }
}
