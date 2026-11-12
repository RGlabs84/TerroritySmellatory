using System.Collections.Generic;
using TerroritySmellatory;
using UnityEngine;

namespace TerrE2E
{
    /// <summary>One territory the scenario creates: the panel draft it is marked with, and where.</summary>
    internal sealed class Spec
    {
        public string Key;
        public string Name;
        public TerritoryShapeKind Kind;
        public Vector2 Offset;          // from the admin's spawn point, before the layout rotation
        public float HalfX, HalfZ, Yaw;
        public TerritoryFlags Flags;
        public PvPRule PvP;
        public float HealthRegen = 1f, StaminaRegen = 1f, EitrRegen = 1f, CarryBonus, ArmorBonus;
        public string EnterMessage = "", LeaveMessage = "";
        public Color32 Color;

        /// <summary>The terr_place line Infinity Hammer's Territory tool would run for this ground.</summary>
        public string PlaceCommand(Vector3 centre)
        {
            string xz = $"x={World.F(centre.x)} z={World.F(centre.z)} y={World.F(centre.y)}";
            switch (Kind)
            {
                case TerritoryShapeKind.Circle: return $"terr_place {xz} r={World.F(HalfX)}";
                case TerritoryShapeKind.Square: return $"terr_place {xz} w={World.F(HalfX)} d={World.F(HalfX)} a={World.F(Yaw)}";
                default: return $"terr_place {xz} w={World.F(HalfX)} d={World.F(HalfZ)} a={World.F(Yaw)}";
            }
        }
    }

    /// <summary>
    /// The scenario's territories - one per shape, each with a different rule set, so every rule the plain
    /// visitor checks has exactly one territory that switches it on.
    /// </summary>
    internal static class Layout
    {
        internal const string CircleName = "E2E Circle";
        internal const string SquareName = "E2E Square";
        internal const string RectName = "E2E Rect";

        /// <summary>T1 - the protected sanctuary: build/access/harvest protection, Sanctuary with bonuses, PvP forced OFF, custom banner.</summary>
        internal static readonly Spec Circle = new Spec
        {
            Key = "circle",
            Name = CircleName,
            Kind = TerritoryShapeKind.Circle,
            Offset = new Vector2(30f, 0f),
            HalfX = 10f,
            HalfZ = 10f,
            Flags = TerritoryFlags.BuildProtect | TerritoryFlags.AccessProtect | TerritoryFlags.HarvestProtect |
                    TerritoryFlags.Sanctuary | TerritoryFlags.Announce | TerritoryFlags.ShowOnMap | TerritoryFlags.ShowBorder,
            PvP = PvPRule.ForcedOff,
            HealthRegen = 2f,
            StaminaRegen = 1.5f,
            CarryBonus = 150f,
            ArmorBonus = 10f,
            EnterMessage = "Welcome to {name}",
            Color = new Color32(60, 165, 160, 255)
        };

        /// <summary>T2 - the war zone: structures cannot be damaged, no monster spawns, PvP forced ON, default banners.</summary>
        internal static readonly Spec Square = new Spec
        {
            Key = "square",
            Name = SquareName,
            Kind = TerritoryShapeKind.Square,
            Offset = new Vector2(-30f, 0f),
            HalfX = 8f,
            HalfZ = 8f,
            Yaw = 0f,
            Flags = TerritoryFlags.StructureProtect | TerritoryFlags.NoMonsterSpawns | TerritoryFlags.Announce | TerritoryFlags.ShowOnMap,
            PvP = PvPRule.ForcedOn,
            Color = new Color32(200, 70, 60, 255)
        };

        /// <summary>T3 - the rotated rectangle: build/access protection, no raids, no skill loss, border only (no banner, not on the map).</summary>
        internal static readonly Spec Rect = new Spec
        {
            Key = "rect",
            Name = RectName,
            Kind = TerritoryShapeKind.Rectangle,
            Offset = new Vector2(0f, 34f),
            HalfX = 12f,
            HalfZ = 5f,
            Yaw = 30f,
            Flags = TerritoryFlags.BuildProtect | TerritoryFlags.AccessProtect | TerritoryFlags.NoRaids |
                    TerritoryFlags.NoSkillLoss | TerritoryFlags.ShowBorder,
            PvP = PvPRule.Unchanged,
            Color = new Color32(150, 95, 200, 255)
        };

        /// <summary>Where T3 is moved to by the Reshape step (Move / reshape in the panel, then the hammer).</summary>
        internal static readonly Vector2 ReshapeOffset = new Vector2(0f, -34f);
        internal const float ReshapeHalfX = 9f, ReshapeHalfZ = 6f, ReshapeYaw = 75f;

        internal static readonly Spec[] All = { Circle, Square, Rect };

        // ── 0.2.0: the two territories the combine phase joins. Named "TS ...", not "E2E ...", on purpose: the
        //    checks of the three scenario territories (H03, S10-S20, R01) select by the "E2E" prefix and keep their
        //    meaning on every version; the combined territory has ids of its own (A20 / P20 / S30 / R30).
        internal const string CombAName = "TS Comb A";
        internal const string CombBName = "TS Comb B";

        /// <summary>The territory that STAYS: access and build protected, announces itself, on the map, PvP untouched.</summary>
        internal static readonly Spec CombA = new Spec
        {
            Key = "combA",
            Name = CombAName,
            Kind = TerritoryShapeKind.Circle,
            HalfX = 8f,
            HalfZ = 8f,
            Flags = TerritoryFlags.BuildProtect | TerritoryFlags.AccessProtect | TerritoryFlags.Announce | TerritoryFlags.ShowOnMap,
            PvP = PvPRule.Unchanged,
            EnterMessage = "Welcome to {name}",
            Color = new Color32(90, 140, 220, 255)
        };

        /// <summary>
        /// The territory that JOINS: no protection at all and words of its own. The combine drops its name and rules, so a
        /// visitor refused on its ground afterwards can only be refused by A's rules.
        /// </summary>
        internal static readonly Spec CombB = new Spec
        {
            Key = "combB",
            Name = CombBName,
            Kind = TerritoryShapeKind.Circle,
            HalfX = 8f,
            HalfZ = 8f,
            Flags = TerritoryFlags.Announce,
            PvP = PvPRule.Unchanged,
            EnterMessage = "B welcomes you",
            Color = new Color32(220, 170, 60, 255)
        };

        /// <summary>Where A goes, from the admin's spawn in the layout's own frame; B is <see cref="CombGap"/> m further along x.</summary>
        internal static readonly Vector2 CombAOffset = new Vector2(-26f, 26f);

        /// <summary>Centre to centre, in metres: two circles of r=8 overlap by 3 m, so the seam lies inside the ground.</summary>
        internal const float CombGap = 13f;

        /// <summary>
        /// The two centres of the combine phase: the layout's rotation first, then 45 degree steps, taking the first that is
        /// dry (as <see cref="Place"/> reads dry) and at least 24 m from every centre in <paramref name="keepClear"/> (the
        /// spawn, the circle, the rectangle's reshape target), so the pair never sits on another territory or the home.
        /// </summary>
        internal static void PlaceCombine(Vector3 origin, float baseRotation, IList<Vector3> keepClear, out Vector3 a, out Vector3 b)
        {
            float water = World.WaterLevel;
            a = b = origin;
            float bestScore = float.MinValue;
            for (int step = 0; step < 8; step++)
            {
                Quaternion q = Quaternion.Euler(0f, baseRotation + step * 45f, 0f);
                Vector3 ca = origin + q * new Vector3(CombAOffset.x, 0f, CombAOffset.y);
                Vector3 cb = ca + q * new Vector3(CombGap, 0f, 0f);
                ca.y = World.Ground(ca);
                cb.y = World.Ground(cb);
                float dry = Mathf.Min(Dryness(ca, water), Dryness(cb, water));
                bool clear = true;
                foreach (Vector3 k in keepClear)
                    if (Flat(ca, k) < 24f || Flat(cb, k) < 24f) clear = false;
                float score = clear ? dry : dry - 1000f;
                if (score > bestScore)
                {
                    bestScore = score;
                    a = ca;
                    b = cb;
                }
                if (clear && dry > 0.5f) break;
            }
        }

        // ── 0.3.0: the runic barrier's three circles (r=10), named "TS ..." for the same reason as the combine pair:
        //    the checks of the three scenario territories (H03, S10-S20, R01) select by the "E2E" prefix and keep their
        //    meaning on every version; the barrier territories have ids of their own (A30 / A31 / P30 / S31 / R31).
        internal const string BarrierName = "TS Barrier";
        internal const string GuardName = "TS Barrier Guard";
        internal const string ControlName = "TS No Barrier";

        /// <summary>The plain barrier: no monster spawns and the runic barrier (monsters cannot walk in; players can).</summary>
        internal static readonly Spec Barrier = new Spec
        {
            Key = "barrier",
            Name = BarrierName,
            Kind = TerritoryShapeKind.Circle,
            HalfX = 10f,
            HalfZ = 10f,
            Flags = TerritoryFlags.NoMonsterSpawns | Features.RunicBarrier,
            PvP = PvPRule.Unchanged,
            Color = new Color32(60, 120, 235, 255)
        };

        /// <summary>The visitor barrier: as the barrier, and players who are not members are held out too.</summary>
        internal static readonly Spec Guard = new Spec
        {
            Key = "guard",
            Name = GuardName,
            Kind = TerritoryShapeKind.Circle,
            HalfX = 10f,
            HalfZ = 10f,
            Flags = TerritoryFlags.NoMonsterSpawns | Features.RunicBarrier | Features.KeepsOutVisitors,
            PvP = PvPRule.Unchanged,
            Color = new Color32(120, 90, 235, 255)
        };

        /// <summary>The control: the same no-monster rule and nothing else, so what the barrier adds is what the checks see.</summary>
        internal static readonly Spec NoBarrier = new Spec
        {
            Key = "nobarrier",
            Name = ControlName,
            Kind = TerritoryShapeKind.Circle,
            HalfX = 10f,
            HalfZ = 10f,
            Flags = TerritoryFlags.NoMonsterSpawns,
            PvP = PvPRule.Unchanged,
            Color = new Color32(150, 150, 150, 255)
        };

        /// <summary>
        /// Three centres 120 degrees apart on a ring around the admin's spawn (radius 46, then 40 and 52 when the coast
        /// or a neighbour is in the way), rotated in 15 degree steps from the layout's own rotation until all three are
        /// dry to 13 m around (a circle of r=10 and the ground a creature is spawned on) and at least 32 m from every
        /// centre in <paramref name="keepClear"/>. They sit far apart on purpose: a creature spawned 14 m beyond one
        /// edge must find no other territory on its way.
        /// </summary>
        internal static Vector3[] PlaceBarriers(Vector3 origin, float baseRotation, IList<Vector3> keepClear)
        {
            float water = World.WaterLevel;
            Vector3[] best = null;
            float bestScore = float.MinValue;
            foreach (float radius in new[] { 46f, 40f, 52f })
            {
                for (int step = 0; step < 24; step++)
                {
                    var cs = new Vector3[3];
                    float dry = float.MaxValue;
                    bool clear = true;
                    for (int k = 0; k < 3; k++)
                    {
                        Quaternion q = Quaternion.Euler(0f, baseRotation + step * 15f + k * 120f, 0f);
                        Vector3 c = origin + q * new Vector3(0f, 0f, radius);
                        c.y = World.Ground(c);
                        cs[k] = c;
                        float low = c.y;
                        foreach (Vector3 o in new[] { Vector3.forward, Vector3.back, Vector3.left, Vector3.right })
                            low = Mathf.Min(low, World.Ground(c + o * 13f));
                        dry = Mathf.Min(dry, low - water);
                        foreach (Vector3 kc in keepClear)
                            if (Flat(c, kc) < 32f) clear = false;
                    }
                    float score = clear ? dry : dry - 1000f;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = cs;
                    }
                    if (clear && dry > 0.5f) return cs;
                }
            }
            return best;
        }

        // ── 0.3.1: the auto-close doors' three circles. Named "TS ..." for the same reason as the others (the E2E-prefixed
        //    checks keep their meaning); ids of their own (A32 / P31 / S32 / R32).
        internal const string DoorsName = "TS Doors";
        internal const string NestedName = "TS Doors Nested";
        internal const string DoorsOffName = "TS Doors Off";

        /// <summary>The territory whose doors close by themselves: AutoCloseDoors, plus the one harmless flag the two below carry, so the flag is the ONLY difference.</summary>
        internal static readonly Spec Doors = new Spec
        {
            Key = "doors",
            Name = DoorsName,
            Kind = TerritoryShapeKind.Circle,
            HalfX = 12f,
            HalfZ = 12f,
            Flags = TerritoryFlags.NoRaids | Features.AutoCloseDoors,
            PvP = PvPRule.Unchanged,
            Color = new Color32(70, 190, 110, 255)
        };

        /// <summary>A small territory nested INSIDE the doors territory, without the flag: the smaller territory governs, so its doors stay as they are.</summary>
        internal static readonly Spec Nested = new Spec
        {
            Key = "nested",
            Name = NestedName,
            Kind = TerritoryShapeKind.Circle,
            HalfX = 4f,
            HalfZ = 4f,
            Flags = TerritoryFlags.NoRaids,
            PvP = PvPRule.Unchanged,
            Color = new Color32(230, 190, 60, 255)
        };

        /// <summary>A territory beside it that does not have the flag.</summary>
        internal static readonly Spec DoorsOff = new Spec
        {
            Key = "doorsoff",
            Name = DoorsOffName,
            Kind = TerritoryShapeKind.Circle,
            HalfX = 8f,
            HalfZ = 8f,
            Flags = TerritoryFlags.NoRaids,
            PvP = PvPRule.Unchanged,
            Color = new Color32(190, 110, 70, 255)
        };

        /// <summary>Where the nested territory's centre lies from the doors territory's, in metres along x (7 + 4 = 11 keeps it inside r=12).</summary>
        internal const float NestedOffsetX = 7f;

        /// <summary>
        /// The doors territory (r=12) on a ring around the spawn (radius 46, then 40, 52, 34), and the one without the flag
        /// 38 m from it in the first of eight directions that is dry, both dry to 4 m beyond their edge and at least 32 m
        /// from every centre in <paramref name="keepClear"/>. Close together on purpose: the player stands in one and the
        /// doors of the other must be inside the client's loaded area and the server's sector block around that player.
        /// </summary>
        internal static bool PlaceDoors(Vector3 origin, float baseRotation, IList<Vector3> keepClear, out Vector3 doors, out Vector3 off)
        {
            float water = World.WaterLevel;
            doors = off = origin;
            float bestScore = float.MinValue;
            foreach (float radius in new[] { 46f, 40f, 52f, 34f })
            {
                for (int step = 0; step < 24; step++)
                {
                    Quaternion q = Quaternion.Euler(0f, baseRotation + step * 15f, 0f);
                    Vector3 d = origin + q * new Vector3(0f, 0f, radius);
                    d.y = World.Ground(d);
                    float dryD = DrynessRing(d, 16f, water);
                    bool clearD = true;
                    foreach (Vector3 k in keepClear)
                        if (Flat(d, k) < 32f) clearD = false;
                    for (int dir = 0; dir < 8; dir++)
                    {
                        Vector3 o = d + Quaternion.Euler(0f, dir * 45f, 0f) * new Vector3(0f, 0f, 38f);
                        o.y = World.Ground(o);
                        float dry = Mathf.Min(dryD, DrynessRing(o, 12f, water));
                        bool clear = clearD;
                        foreach (Vector3 k in keepClear)
                            if (Flat(o, k) < 32f) clear = false;
                        float score = clear ? dry : dry - 1000f;
                        if (score > bestScore)
                        {
                            bestScore = score;
                            doors = d;
                            off = o;
                        }
                        if (clear && dry > 0.5f) return true;
                    }
                }
            }
            return bestScore > -500f;
        }

        /// <summary>
        /// A dry site for a circle of <paramref name="radius"/> m (and what is marked inside it) whose ground is at least
        /// <paramref name="clearance"/> m from the ground of EVERY territory there already - asked of the client's own list, not
        /// of the centres the rig remembers, because a territory is bigger than its centre and the 0.4.0 phase must not be placed
        /// inside an older one (the server picks the parent of a territory marked inside two as the one that governs there).
        /// Rings of 58 to 106 m round <paramref name="origin"/>, so the ground is inside what the client has loaded; false, with the
        /// roomiest site found, when none is clear.
        /// </summary>
        internal static bool PlaceClear(Vector3 origin, float baseRotation, float radius, float clearance, out Vector3 site)
        {
            float water = World.WaterLevel;
            site = origin;
            float bestRoom = float.MinValue;
            foreach (float ring in new[] { 58f, 70f, 82f, 94f, 106f })
            {
                for (int step = 0; step < 24; step++)
                {
                    Quaternion q = Quaternion.Euler(0f, baseRotation + step * 15f, 0f);
                    Vector3 c = origin + q * new Vector3(0f, 0f, ring);
                    c.y = World.Ground(c);
                    float room = float.MaxValue;
                    foreach (Territory t in TerritoryIndex.All) room = Mathf.Min(room, t.Ground.DistanceOutside(c) - radius);
                    float dry = DrynessRing(c, radius + 4f, water);
                    float score = dry > 0.5f ? room : room - 1000f;
                    if (score > bestRoom)
                    {
                        bestRoom = score;
                        site = c;
                    }
                    if (room >= clearance && dry > 0.5f) return true;
                }
            }
            return bestRoom >= clearance;
        }

        /// <summary>The lowest of the centre and eight points <paramref name="radius"/> m around it, above the water line.</summary>
        private static float DrynessRing(Vector3 c, float radius, float water)
        {
            float low = World.Ground(c);
            for (int i = 0; i < 8; i++)
            {
                float a = i * 45f * Mathf.Deg2Rad;
                low = Mathf.Min(low, World.Ground(c + new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a)) * radius));
            }
            return low - water;
        }

        private static float Flat(Vector3 p, Vector3 q) => Vector2.Distance(new Vector2(p.x, p.z), new Vector2(q.x, q.z));

        /// <summary>The lowest of the centre and four points 9 m around it (a circle of r=8 and a metre), above the water line.</summary>
        private static float Dryness(Vector3 c, float water)
        {
            float low = World.Ground(c);
            foreach (Vector3 o in new[] { Vector3.forward, Vector3.back, Vector3.left, Vector3.right })
                low = Mathf.Min(low, World.Ground(c + o * 9f));
            return low - water;
        }

        /// <summary>
        /// Turns the offsets into world centres around the admin's spawn, rotating the whole layout in 45 degree
        /// steps until every centre (and the reshape target) is on dry land - a Better Continents world's spawn
        /// can sit on any coast.
        /// </summary>
        internal static Dictionary<string, Vector3> Place(Vector3 origin, out float rotation)
        {
            float water = World.WaterLevel;
            var offsets = new List<KeyValuePair<string, Vector2>>();
            foreach (Spec s in All) offsets.Add(new KeyValuePair<string, Vector2>(s.Key, s.Offset));
            offsets.Add(new KeyValuePair<string, Vector2>("rect2", ReshapeOffset));

            Dictionary<string, Vector3> best = null;
            float bestDry = -1f;
            rotation = 0f;
            for (int step = 0; step < 8; step++)
            {
                float rot = step * 45f;
                var centres = new Dictionary<string, Vector3>();
                float dry = float.MaxValue;
                foreach (KeyValuePair<string, Vector2> kv in offsets)
                {
                    Vector3 d = Quaternion.Euler(0f, rot, 0f) * new Vector3(kv.Value.x, 0f, kv.Value.y);
                    var c = new Vector3(origin.x + d.x, 0f, origin.z + d.z);
                    c.y = World.Ground(c);
                    centres[kv.Key] = c;
                    // The lowest of the centre and four points 6 m around it, above the water line.
                    float low = c.y;
                    foreach (Vector3 o in new[] { Vector3.forward, Vector3.back, Vector3.left, Vector3.right })
                        low = Mathf.Min(low, World.Ground(c + o * 6f));
                    dry = Mathf.Min(dry, low - water);
                }
                if (dry > bestDry)
                {
                    bestDry = dry;
                    best = centres;
                    rotation = rot;
                }
                if (dry > 0.5f) break;
            }
            return best;
        }
    }
}
