using System;
using System.Collections.Generic;
using UnityEngine;

namespace TerroritySmellatory.Tests
{
    // 0.3.0: the runic barrier's decisions (Rules/BarrierMath.cs) - who is stopped, where a thing that got in is put,
    // where a step crossed the edge, how hard a push was and when the next flare may fire. The part that touches the
    // game (Rules/Barrier.cs) is tested by the e2e rig; everything it decides is here.
    internal static partial class Program
    {
        static partial void BarrierGroups()
        {
            Run("barrier: who is stopped - flags, hostility, tamed, member, admin and bypass", BarrierWhoIsStopped);
            Run("barrier: nearest way out of every single shape", BarrierOutShapes);
            Run("barrier: nearest way out of compounds - lens, nested part, seam, L, far parts", BarrierOutCompounds);
            Run("barrier: nearest way out, random compounds against a brute-force search", BarrierOutRandom);
            Run("barrier: the hold decision - free, hold, evict", BarrierDecideHold);
            Run("barrier: where a step crosses the edge, and which way is out there", BarrierEdgeCrossings);
            Run("barrier: the nearest edge point to something outside", BarrierClosest);
            Run("barrier: push speed, cancelling the inward part, strength and reach", BarrierPushMath);
            Run("barrier: the flare throttle and the stranger's dwell", BarrierThrottle);
            Run("barrier: the per-frame reach test never misses ground within the pad", BarrierReachTest);
            Run("barrier: a thing pushed at an L for 400 frames is never left inside and slides along it", BarrierSimulatedWalk);
            Run("barrier: nesting (0.3.2) - a town inside a barrier territory is a pocket; held at its edge, never thrown to the far one", BarrierNesting);
        }

        private const float Margin = BarrierMath.ExitMargin;

        private static TerritoryGround Ground(TerritoryShape first, params TerritoryShape[] more) => Compound(1, first, more).Ground;

        // Program's Near for vectors has one fixed tolerance; the crossings are looser where a chord meets a curve.
        private static void Near(Vector3 a, Vector3 b, string what, float eps) => Check((a - b).magnitude <= eps, $"{what} (got {a}, expected {b})");

        private static Vector3 At(float x, float z) => new Vector3(x, 0f, z);

        private static float Flat(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        // ─────────────────────────────────────────────────────────────
        // NESTING (0.3.2)
        // ─────────────────────────────────────────────────────────────

        private static BarrierMath.Pocket[] PocketsOf(Territory t)
        {
            IReadOnlyList<Territory> all = TerritoryIndex.All;
            int index = -1;
            for (int i = 0; i < all.Count; i++) if (all[i].Id == t.Id) index = i;
            Check(index >= 0, $"territory {t.Id} is in the index");
            t.Ground.Bounds(out float x0, out float z0, out float x1, out float z1);
            return BarrierMath.PocketsFor(all, index, x0, z0, x1, z1);
        }

        private static void BarrierNesting()
        {
            const TerritoryFlags monsters = TerritoryFlags.NoMonsterSpawns | TerritoryFlags.RunicBarrier;
            const TerritoryFlags visitors = monsters | TerritoryFlags.BarrierKeepsOutVisitors;

            // A kingdom sealed against monsters and visitors (1); a public town inside it with no barrier (2); a keep inside it
            // with a barrier of its own (3); a cairn far outside it, which is smaller but nowhere near (4); and a realm round
            // the lot, which is bigger and so cannot govern any of it (5).
            Territory kingdom = Circle(1, 0f, 0f, 1000f, visitors);
            Territory town = Circle(2, 300f, 0f, 60f, TerritoryFlags.BuildProtect);
            Territory keep = Circle(3, -300f, 0f, 40f, monsters);
            Territory cairn = Circle(4, 5000f, 0f, 10f, monsters);
            Territory realm = Circle(5, 0f, 0f, 3000f, TerritoryFlags.BuildProtect);
            TerritoryIndex.Set(new[] { kingdom, town, keep, cairn, realm });

            BarrierMath.Pocket[] pockets = PocketsOf(kingdom);
            Check(pockets.Length == 2, $"the kingdom's pockets are the town and the keep, not the cairn (far) or the realm (bigger) [{pockets.Length}]");
            Check(PocketsOf(keep).Length == 0, "the keep has none: nothing outranking it is anywhere near");
            Check(PocketsOf(realm).Length == 3, "the realm is outranked by the kingdom, the town and the keep - everything in its box, which the cairn is not");
            Check(BarrierMath.Governs(kingdom.Ground, null, At(500f, 0f)) && !BarrierMath.Governs(kingdom.Ground, null, At(1500f, 0f)), "with no pockets it is just the ground");

            // Who governs where: the same answer the index gives everyone else.
            foreach (Vector3 p in new[] { At(0f, 0f), At(500f, 500f), At(300f, 0f), At(-300f, 10f), At(359f, 0f), At(361f, 0f), At(-259f, 0f), At(990f, 0f), At(1005f, 0f) })
            {
                bool governs = BarrierMath.Governs(kingdom.Ground, pockets, p);
                Check(governs == (TerritoryIndex.GoverningAt(p) == kingdom), $"Governs agrees with TerritoryIndex.GoverningAt at {p}");
            }

            // A visitor who logged in inside the town: free, not thrown to the kingdom's edge a kilometre away.
            HoldKind kind = BarrierMath.Decide(kingdom.Ground, pockets, false, default, 0f, At(300f, 0f), Margin, out Vector3 to);
            Check(kind == HoldKind.Free && Flat(to, At(300f, 0f)) < 1e-4f, "found inside the town: free, and left where it is");
            kind = BarrierMath.Decide(kingdom.Ground, false, default, 0f, At(300f, 0f), Margin, out to);
            Check(kind == HoldKind.Evict && Flat(to, At(0f, 0f)) > 1000f, "(without pockets it was Evict to the kingdom's far edge - the bug this fixes)");

            // The same visitor steps out of the town onto the kingdom's ground: held back to where it was seen, at the town's edge.
            Vector3 last = At(359f, 0f), now = At(361f, 0f);
            kind = BarrierMath.Decide(kingdom.Ground, pockets, true, last, 0.2f, now, Margin, out to);
            Check(kind == HoldKind.Hold && Flat(to, last) < 1e-4f, "stepping out of the town: Hold, back to the last place seen");
            kind = BarrierMath.Decide(kingdom.Ground, pockets, true, last, BarrierMath.MaxHoldAge + 1f, now, Margin, out to);
            Check(kind == HoldKind.Evict && !kingdom.Ground.Contains(to) && Flat(to, At(0f, 0f)) < 1000f + Margin + 0.01f && Flat(to, At(0f, 0f)) > 1000f,
                "last seen too long ago: Evict - to the nearest way out of the OUTER edge, never into the town");
            kind = BarrierMath.Decide(kingdom.Ground, pockets, true, At(200f, 0f), 0.2f, now, Margin, out to);
            Check(kind == HoldKind.Evict, "a last place that is itself in the governed ground is no place to hold it back to");

            // A thing outside the kingdom, and one in the open middle of it: as before.
            kind = BarrierMath.Decide(kingdom.Ground, pockets, false, default, 0f, At(1500f, 0f), Margin, out to);
            Check(kind == HoldKind.Free, "outside the kingdom: free");
            kind = BarrierMath.Decide(kingdom.Ground, pockets, true, At(0f, 1004f), 0.2f, At(0f, 998f), Margin, out to);
            Check(kind == HoldKind.Hold && Flat(to, At(0f, 1004f)) < 1e-4f, "crossing the kingdom's own edge: held back outside, as ever");

            // A barrier inside a barrier: the keep's thing is the keep's to move, and where it is put is the kingdom's ground, still held.
            BarrierMath.Pocket[] keepPockets = PocketsOf(keep);
            kind = BarrierMath.Decide(keep.Ground, keepPockets, false, default, 0f, At(-300f, 0f), Margin, out to);
            Check(kind == HoldKind.Evict && !keep.Ground.Contains(to), "found in the keep: out of the keep");
            Check(BarrierMath.Governs(kingdom.Ground, pockets, to), "...onto ground the kingdom governs, so the kingdom's barrier takes it the rest of the way (Barrier.Enforce's passes)");
            Check(!BarrierMath.Governs(kingdom.Ground, pockets, At(-300f, 0f)), "while inside the keep it is not the kingdom's to move");

            // The flare at a pocket's edge: EdgeCrossing finds no entry to the OUTER edge from inside, and answers with the way it came.
            bool crossed = BarrierMath.EdgeCrossing(kingdom.Ground, last, now, out Vector3 edge, out Vector3 outward);
            Check(!crossed && Flat(edge, last) < 1e-4f && Mathf.Abs(outward.magnitude - 1f) < 1e-4f && outward.x < 0f,
                "at a pocket's edge the 'crossing' falls back to the last place seen and the direction it came from");

            // Precedence is what makes a pocket: raise the kingdom's priority and the town is no pocket of it any more.
            kingdom.Priority = 10;
            TerritoryIndex.Set(new[] { kingdom, town, keep, cairn, realm });
            Check(PocketsOf(kingdom).Length == 0, "forced to win, the kingdom governs the town's ground as well: no pockets");
            kind = BarrierMath.Decide(kingdom.Ground, PocketsOf(kingdom), false, default, 0f, At(300f, 0f), Margin, out to);
            Check(kind == HoldKind.Evict, "and its barrier does reach the town, as the admin asked");
        }

        // ─────────────────────────────────────────────────────────────
        // WHO
        // ─────────────────────────────────────────────────────────────

        private static void BarrierWhoIsStopped()
        {
            const TerritoryFlags nm = TerritoryFlags.NoMonsterSpawns, rb = TerritoryFlags.RunicBarrier, vis = TerritoryFlags.BarrierKeepsOutVisitors;
            TerritoryFlags monsters = nm | rb, visitors = nm | rb | vis;

            // Creatures: hostile and not tamed, and only where the barrier is on.
            Check(BarrierMath.Stops(monsters, false, false, true, false, false, false), "a wild hostile creature is stopped");
            Check(!BarrierMath.Stops(monsters, false, true, true, false, false, false), "a tamed one is not, hostile prefab or not");
            Check(!BarrierMath.Stops(monsters, false, false, false, false, false, false), "a passive animal is not");
            Check(!BarrierMath.Stops(monsters, false, true, false, false, false, false), "a tamed passive animal is not");
            Check(BarrierMath.Stops(visitors, false, false, true, false, false, false), "the visitor option changes nothing for creatures");
            Check(!BarrierMath.Stops(nm, false, false, true, false, false, false), "no RunicBarrier: nothing is stopped");
            Check(!BarrierMath.Stops(rb, false, false, true, false, false, false), "RunicBarrier without NoMonsterSpawns is off (it needs it)");
            Check(!BarrierMath.Stops(TerritoryFlags.None, false, false, true, false, false, false), "no flags");
            Check(BarrierMath.Stops(monsters, false, false, true, true, true, true), "membership never lets a creature through (it is about players)");

            // Players: only with the visitor option, never members, never an admin who bypasses.
            Check(!BarrierMath.Stops(monsters, true, false, false, false, false, false), "a stranger is not stopped without the visitor option");
            Check(BarrierMath.Stops(visitors, true, false, false, false, false, false), "a stranger is stopped with it");
            Check(!BarrierMath.Stops(visitors, true, false, false, true, false, false), "a member passes");
            Check(!BarrierMath.Stops(visitors, true, false, false, false, true, true), "an admin passes while admins bypass");
            Check(BarrierMath.Stops(visitors, true, false, false, false, true, false), "an admin is stopped when the server switched the bypass off");
            Check(BarrierMath.Stops(visitors, true, false, false, false, false, true), "the bypass is only for admins");
            Check(!BarrierMath.Stops(vis | rb, true, false, false, false, false, false), "the visitor option needs NoMonsterSpawns too");
            Check(!BarrierMath.Stops(vis | nm, true, false, false, false, false, false), "the visitor option needs RunicBarrier");
            Check(BarrierMath.Stops(visitors, true, true, true, false, false, false), "'tamed' and 'hostile' say nothing about a player");
            Check(BarrierMath.Active(monsters) && !BarrierMath.Active(rb) && !BarrierMath.Active(nm), "Active needs both bits");
            Check(BarrierMath.KeepsOutVisitors(visitors) && !BarrierMath.KeepsOutVisitors(monsters), "KeepsOutVisitors needs the third");

            // The whole table, once, against the rule written out.
            int rows = 0;
            for (int f = 0; f < 8; f++)
            for (int bits = 0; bits < 64; bits++)
            {
                var flags = (f & 1) != 0 ? nm : TerritoryFlags.None;
                if ((f & 2) != 0) flags |= rb;
                if ((f & 4) != 0) flags |= vis;
                bool player = (bits & 1) != 0, tamed = (bits & 2) != 0, hostile = (bits & 4) != 0, member = (bits & 8) != 0, admin = (bits & 16) != 0, bypass = (bits & 32) != 0;
                bool on = (f & 3) == 3;
                bool expect = player ? on && (f & 4) != 0 && !member && !(admin && bypass) : on && hostile && !tamed;
                if (BarrierMath.Stops(flags, player, tamed, hostile, member, admin, bypass) != expect)
                    Check(false, $"table row flags={flags} bits={bits}");
                rows++;
            }
            Check(rows == 512, "the table ran in full");
        }

        // ─────────────────────────────────────────────────────────────
        // NEAREST WAY OUT
        // ─────────────────────────────────────────────────────────────

        private static void BarrierOutShapes()
        {
            // A circle: straight out along the radius, margin past the edge, exactly the nearest.
            TerritoryGround c = Ground(Part(TerritoryShapeKind.Circle, 100f, -50f, 30f));
            var p = new Vector3(112f, 7f, -50f);
            Vector3 q = BarrierMath.NearestOutside(c, p, Margin);
            Check(!c.Contains(q), "circle: the result is outside");
            Near(Flat(q, At(100f, -50f)), 30f + Margin, "circle: exactly margin past the edge", 1e-3f);
            Near(q.z, -50f, "circle: along the radius", 1e-3f);
            Near(q.y, 7f, "circle: height is kept");
            Near(Flat(q, p), 18f + Margin, "circle: distance from the start is the way to the edge plus the margin", 1e-3f);

            // Dead centre: any way out is as short as any other, but it must be a way out.
            q = BarrierMath.NearestOutside(c, At(100f, -50f), Margin);
            Check(!c.Contains(q) && Math.Abs(Flat(q, At(100f, -50f)) - (30f + Margin)) < 1e-3f, "circle: from the exact centre still gets out, by the radius");

            // Already outside: untouched, even when it is closer than the margin.
            var outside = new Vector3(131f, 3f, -50f);
            Near(BarrierMath.NearestOutside(c, outside, Margin), outside, "circle: a point outside is returned as it is");
            var onEdge = new Vector3(130.2f, 0f, -50f);
            Near(BarrierMath.NearestOutside(c, onEdge, Margin), onEdge, "circle: so is one just outside the edge");

            // A square, every face and near a corner.
            TerritoryGround sq = Ground(Part(TerritoryShapeKind.Square, 0f, 0f, 10f));
            foreach (Vector3 inside in new[] { At(9f, 1f), At(-9f, -2f), At(2f, 9.5f), At(-3f, -9.2f) })
            {
                q = BarrierMath.NearestOutside(sq, inside, Margin);
                Check(!sq.Contains(q), $"square: outside from {inside}");
                Near(sq.DistanceOutside(q), Margin, $"square: margin past the face from {inside}", 1e-3f);
                float best = Math.Min(10f - Math.Abs(inside.x), 10f - Math.Abs(inside.z));
                Near(Flat(q, inside), best + Margin, $"square: the nearest face from {inside}", 1e-3f);
            }
            q = BarrierMath.NearestOutside(sq, At(9.8f, 9.7f), Margin);
            Check(!sq.Contains(q) && Flat(q, At(9.8f, 9.7f)) <= 0.3f + Margin + 1e-3f, "square: near a corner it leaves by the nearer face");

            // A rotated rectangle, in the ruler's frame: local X is the width, local Z the depth.
            const float yaw = 30f, hx = 10f, hz = 30f;
            var centre = At(5f, 7f);
            TerritoryGround r = Ground(Part(TerritoryShapeKind.Rectangle, centre.x, centre.z, hx, hz, yaw));
            Quaternion rot = Quaternion.Euler(0f, yaw, 0f);
            foreach (Vector3 local in new[] { new Vector3(8f, 0f, 3f), new Vector3(-9.5f, 0f, -12f), new Vector3(2f, 0f, 27f), new Vector3(-1f, 0f, -28f), new Vector3(0f, 0f, 0f) })
            {
                Vector3 world = centre + rot * local;
                q = BarrierMath.NearestOutside(r, world, Margin);
                Check(!r.Contains(q), $"rectangle: outside from local {local}");
                float best = Math.Min(hx - Math.Abs(local.x), hz - Math.Abs(local.z));
                Near(Flat(q, world), best + Margin, $"rectangle: the nearest face from local {local}", 1e-3f);
                Near(r.DistanceOutside(q), Margin, $"rectangle: margin past the face from local {local}", 1e-3f);
            }

            // The margin is honoured, and a silly one is raised to a sane floor instead of returning a point on the edge.
            q = BarrierMath.NearestOutside(c, At(110f, -50f), 3f);
            Near(Flat(q, At(100f, -50f)), 33f, "a 3 m margin", 1e-3f);
            q = BarrierMath.NearestOutside(c, At(110f, -50f), 0f);
            Check(!c.Contains(q), "a zero margin still ends outside");
            q = BarrierMath.NearestOutside(c, At(110f, -50f), -4f);
            Check(!c.Contains(q), "and so does a negative one");

            // An empty part holds nothing and cannot be the way out of anything.
            TerritoryGround withEmpty = Ground(Part(TerritoryShapeKind.Circle, 0f, 0f, 10f), new TerritoryShape(TerritoryShapeKind.Circle, 0f, 0f, 0f, 0f, 0f));
            Check(!withEmpty.Contains(BarrierMath.NearestOutside(withEmpty, At(1f, 1f), Margin)), "a part of no size changes nothing");
        }

        private static void BarrierOutCompounds()
        {
            // Two overlapping circles, a point in the lens: its own nearest edge of either circle is inside the other,
            // so the way out is around, and it must be outside the UNION.
            TerritoryGround lens = Ground(Part(TerritoryShapeKind.Circle, -1f, 0f, 2f), Part(TerritoryShapeKind.Circle, 1f, 0f, 2f));
            foreach (Vector3 p in new[] { At(0f, 0f), At(0f, 1.5f), At(0.3f, -1.2f), At(0f, 1.7f) })
            {
                Vector3 q = BarrierMath.NearestOutside(lens, p, Margin);
                Check(!lens.Contains(q), $"lens: outside from {p}");
                Check(Flat(q, p) <= BruteExit(lens, p) * 1.5f + 2f * Margin, $"lens: close from {p} (got {Flat(q, p)}, brute {BruteExit(lens, p)})");
            }

            // A part nested in another: deep inside the small one, the small one's own edge is a seam. The way out is the big one's.
            TerritoryGround nested = Ground(Part(TerritoryShapeKind.Circle, 0f, 0f, 50f), Part(TerritoryShapeKind.Circle, 5f, 0f, 10f));
            Vector3 n = BarrierMath.NearestOutside(nested, At(5f, 0f), Margin);
            Check(!nested.Contains(n), "nested: outside");
            Near(n, new Vector3(50f + Margin, 0f, 0f), "nested: out through the big circle's edge, not the small one's");
            n = BarrierMath.NearestOutside(nested, At(-3f, 2f), Margin);
            Check(!nested.Contains(n) && Math.Abs(Flat(n, At(0f, 0f)) - (50f + Margin)) < 1e-3f, "nested: from the small part's far side too");

            // The same the other way round in the list: the order of the parts changes nothing.
            TerritoryGround flipped = Ground(Part(TerritoryShapeKind.Circle, 5f, 0f, 10f), Part(TerritoryShapeKind.Circle, 0f, 0f, 50f));
            Near(BarrierMath.NearestOutside(flipped, At(5f, 0f), Margin), new Vector3(50f + Margin, 0f, 0f), "nested: listed second, same answer");

            // A rectangle inside a rotated rectangle, deep inside the inner one.
            TerritoryGround boxes = Ground(Part(TerritoryShapeKind.Rectangle, 0f, 0f, 40f, 25f, 20f), Part(TerritoryShapeKind.Square, 3f, 2f, 6f, 6f, 55f));
            Vector3 b = BarrierMath.NearestOutside(boxes, At(3f, 2f), Margin);
            Check(!boxes.Contains(b), "boxes: outside from the middle of the inner one");
            Check(Flat(b, At(3f, 2f)) <= BruteExit(boxes, At(3f, 2f)) + Margin + 0.2f, "boxes: by the outer part's nearest face");

            // Seam: two boxes side by side make one bar. Near the seam the way out is a long side, never the seam.
            TerritoryGround bar = Ground(Part(TerritoryShapeKind.Rectangle, 5f, 5f, 5f, 5f), Part(TerritoryShapeKind.Rectangle, 15f, 5f, 5f, 5f));
            Vector3 s = BarrierMath.NearestOutside(bar, At(9.5f, 5f), Margin);
            Check(!bar.Contains(s), "seam: outside");
            Near(Flat(s, At(9.5f, 5f)), 5f + Margin, "seam: five metres to a long side, not half a metre to the seam", 1e-3f);
            s = BarrierMath.NearestOutside(bar, At(10.5f, 9f), Margin);
            Near(Flat(s, At(10.5f, 9f)), 1f + Margin, "seam: a point beside the seam near the top leaves by the top", 1e-3f);

            // An L: the inner corner. From the overlap square the two parts' own nearest edges both land inside the other part.
            TerritoryGround ell = Ground(Part(TerritoryShapeKind.Rectangle, 0f, 0f, 20f, 10f), Part(TerritoryShapeKind.Rectangle, 10f, 20f, 10f, 20f));
            Vector3 p2 = At(2f, 8f);
            Vector3 e = BarrierMath.NearestOutside(ell, p2, Margin);
            Check(!ell.Contains(e), "L: outside from beside the inner corner");
            Check(Flat(e, p2) <= BruteExit(ell, p2) * 1.5f + 2f * Margin, $"L: near the corner (got {Flat(e, p2)}, brute {BruteExit(ell, p2)})");
            e = BarrierMath.NearestOutside(ell, At(-15f, 5f), Margin);
            Near(Flat(e, At(-15f, 5f)), 5f + Margin, "L: the bar's own edge, far from the corner", 1e-3f);

            // Parts far apart are separate grounds: a point in one leaves that one.
            TerritoryGround far = Ground(Part(TerritoryShapeKind.Circle, 0f, 0f, 10f), Part(TerritoryShapeKind.Circle, 5000f, 0f, 10f));
            Vector3 f = BarrierMath.NearestOutside(far, At(5003f, 1f), Margin);
            Check(!far.Contains(f) && Flat(f, At(5000f, 0f)) < 12f, "far parts: leaves the part it is in");

            // A chain of five overlapping circles (a road): from the middle, out by the side.
            var chainParts = new TerritoryShape[4];
            for (int i = 0; i < 4; i++) chainParts[i] = Part(TerritoryShapeKind.Circle, 12f * (i + 1), 0f, 8f);
            TerritoryGround chain = Ground(Part(TerritoryShapeKind.Circle, 0f, 0f, 8f), chainParts);
            Vector3 mid = At(24f, 0.5f);
            Vector3 cq = BarrierMath.NearestOutside(chain, mid, Margin);
            Check(!chain.Contains(cq) && Flat(cq, mid) <= BruteExit(chain, mid) * 1.5f + 2f * Margin, "chain: out by the side of the road");
        }

        /// <summary>The distance to the nearest point outside the ground by marching rays: the yardstick, slow and simple.</summary>
        private static float BruteExit(TerritoryGround g, Vector3 p)
        {
            float best = float.MaxValue;
            const int rays = 240;
            for (int k = 0; k < rays; k++)
            {
                float a = k * (2f * Mathf.PI / rays);
                float dx = Mathf.Cos(a), dz = Mathf.Sin(a);
                for (float d = 0f; d < best; d += 0.05f)
                {
                    if (!g.Contains(new Vector3(p.x + dx * d, 0f, p.z + dz * d)))
                    {
                        best = d;
                        break;
                    }
                }
            }
            return best;
        }

        private static void BarrierOutRandom()
        {
            var rng = new Random(20260929);
            int cases = 0, worse = 0;
            float worst = 0f;
            for (int trial = 0; trial < 60; trial++)
            {
                int n = 2 + rng.Next(4);
                var parts = new TerritoryShape[n];
                for (int i = 0; i < n; i++)
                {
                    float x = (float)(rng.NextDouble() * 60 - 30), z = (float)(rng.NextDouble() * 60 - 30);
                    float sizeA = 6f + (float)rng.NextDouble() * 22f, sizeB = 6f + (float)rng.NextDouble() * 22f;
                    float yaw = (float)(rng.NextDouble() * 360);
                    switch (rng.Next(3))
                    {
                        case 0: parts[i] = Part(TerritoryShapeKind.Circle, x, z, sizeA); break;
                        case 1: parts[i] = Part(TerritoryShapeKind.Square, x, z, sizeA, 0f, yaw); break;
                        default: parts[i] = Part(TerritoryShapeKind.Rectangle, x, z, sizeA, sizeB, yaw); break;
                    }
                }
                var rest = new TerritoryShape[n - 1];
                Array.Copy(parts, 1, rest, 0, n - 1);
                TerritoryGround g = Ground(parts[0], rest);

                int inside = 0;
                for (int k = 0; k < 400 && inside < 4; k++)
                {
                    var p = new Vector3((float)(rng.NextDouble() * 100 - 50), (float)(rng.NextDouble() * 30), (float)(rng.NextDouble() * 100 - 50));
                    if (!g.Contains(p)) continue;
                    inside++;
                    cases++;

                    Vector3 q = BarrierMath.NearestOutside(g, p, Margin);
                    if (g.Contains(q)) { Check(false, $"trial {trial}: {q} is still inside"); continue; }
                    if (float.IsNaN(q.x) || float.IsNaN(q.z)) { Check(false, $"trial {trial}: NaN"); continue; }
                    Near(q.y, p.y, "height kept");

                    float brute = BruteExit(g, p);
                    float got = Flat(q, p);
                    // The result is a way out (above) and as short as the yardstick's, margin aside. The yardstick is
                    // itself coarse (240 lines, so it can miss a narrow corner and overshoot), so this is one-sided:
                    // the method must never be WORSE than it. It usually beats it.
                    float excess = got - Margin - brute;
                    worst = Math.Max(worst, excess);
                    if (excess > 0.1f) worse++;
                }
            }
            Check(cases > 100, $"enough inside points were drawn ({cases})");
            Check(worse == 0, $"{worse} of {cases} ways out were longer than the brute-force nearest by more than 10 cm (worst excess {worst:0.###} m)");
            Console.WriteLine($"    {cases} random inside points: the ray search was never more than {worst:0.###} m longer than the brute-force nearest");
        }

        // ─────────────────────────────────────────────────────────────
        // HOLD DECISION
        // ─────────────────────────────────────────────────────────────

        private static void BarrierDecideHold()
        {
            TerritoryGround g = Ground(Part(TerritoryShapeKind.Circle, 0f, 0f, 20f));

            // Outside: free, and the target is where it stands.
            HoldKind k = BarrierMath.Decide(g, true, At(30f, 0f), 0.02f, At(25f, 0f), Margin, out Vector3 to);
            Check(k == HoldKind.Free, "outside is free");
            Near(to, At(25f, 0f), "free: nowhere to go");

            // A step in from a place outside a moment ago: back to that place.
            k = BarrierMath.Decide(g, true, At(20.4f, 0f), 0.02f, At(19.8f, 0f), Margin, out to);
            Check(k == HoldKind.Hold, "stepped in from just outside: hold");
            Near(to, At(20.4f, 0f), "hold: back to where it was");

            // Nothing seen outside yet (first sight is inside): found inside, out by the nearest way.
            k = BarrierMath.Decide(g, false, default, 0f, At(5f, 0f), Margin, out to);
            Check(k == HoldKind.Evict, "no last position: evict");
            Near(Flat(to, At(0f, 0f)), 20f + Margin, "evict: to the nearest edge plus the margin");
            Check(!g.Contains(to), "evict: outside");

            // The last position is too old (a hitch, a long load) or too far (a portal, a spawn): the thing did not walk in.
            k = BarrierMath.Decide(g, true, At(21f, 0f), BarrierMath.MaxHoldAge + 0.1f, At(19f, 0f), Margin, out to);
            Check(k == HoldKind.Evict, "a stale last position is not a step");
            k = BarrierMath.Decide(g, true, At(21f + BarrierMath.MaxHoldStep + 1f, 0f), 0.02f, At(19f, 0f), Margin, out to);
            Check(k == HoldKind.Evict, "a last position across the map is a jump, not a step");
            k = BarrierMath.Decide(g, true, At(21f, 0f), BarrierMath.MaxHoldAge, At(19f, 0f), Margin, out to);
            Check(k == HoldKind.Hold, "exactly at the age limit still holds");
            k = BarrierMath.Decide(g, true, At(21f, 0f), -1f, At(19f, 0f), Margin, out to);
            Check(k == HoldKind.Evict, "a last position from the future (clock reset) is not trusted");

            // The last position is inside NOW - the barrier moved, grew or came on since: no going back there.
            k = BarrierMath.Decide(g, true, At(10f, 0f), 0.02f, At(5f, 0f), Margin, out to);
            Check(k == HoldKind.Evict && !g.Contains(to), "a last position that is now inside is evicted from");

            // A compound: back to the last place, which may be in the notch of the L; a step into a notch is a step in.
            TerritoryGround ell = Ground(Part(TerritoryShapeKind.Rectangle, 0f, 0f, 20f, 10f), Part(TerritoryShapeKind.Rectangle, 10f, 20f, 10f, 20f));
            k = BarrierMath.Decide(ell, true, At(-3f, 15f), 0.02f, At(0.5f, 15f), Margin, out to);
            Check(k == HoldKind.Hold && Flat(to, At(-3f, 15f)) < 1e-4f, "into the L's notch wall from the notch: held there");
            k = BarrierMath.Decide(ell, true, At(-3f, 15f), 0.02f, At(-3f, 9f), Margin, out to);
            Check(k == HoldKind.Hold && Flat(to, At(-3f, 15f)) < 1e-4f, "and down onto the bar from the notch");
            k = BarrierMath.Decide(ell, true, At(5f, 12f), 0.02f, At(5f, 8f), Margin, out to);
            Check(k == HoldKind.Evict && !ell.Contains(to), "from a seam-side position that was itself inside: evicted, not put back inside");
        }

        // ─────────────────────────────────────────────────────────────
        // EDGE CROSSING
        // ─────────────────────────────────────────────────────────────

        private static void BarrierEdgeCrossings()
        {
            // A circle: the step from (30,0) to (10,0) enters at x = 20 heading in; the way out is +X.
            TerritoryGround c = Ground(Part(TerritoryShapeKind.Circle, 0f, 0f, 20f));
            Check(BarrierMath.EdgeCrossing(c, new Vector3(30f, 1f, 0f), new Vector3(10f, 3f, 0f), out Vector3 pt, out Vector3 nrm), "circle: the step enters");
            Near(pt, new Vector3(20f, 2f, 0f), "circle: the crossing is on the edge, height interpolated");
            Near(nrm, new Vector3(1f, 0f, 0f), "circle: the way out is +X");

            // A glancing step still finds the chord's first end, and the normal is radial there.
            Check(BarrierMath.EdgeCrossing(c, At(-30f, 15f), At(30f, 15f), out pt, out nrm), "circle: a chord");
            Near(Flat(pt, At(0f, 0f)), 20f, "circle: on the edge", 1e-3f);
            Check(pt.x < 0f && nrm.x < 0f, "circle: the first end of the chord, its normal pointing back out");
            Near(nrm.x * nrm.x + nrm.z * nrm.z, 1f, "circle: a unit normal", 1e-4f);

            // A step that never reaches it, and one that stops short.
            Check(!BarrierMath.EdgeCrossing(c, At(40f, 0f), At(30f, 0f), out pt, out nrm), "circle: a step that stops short does not enter");
            Near(pt, At(40f, 0f), "circle: (the start is reported)");
            Near(nrm, new Vector3(1f, 0f, 0f), "circle: (and the way back the step came from)");
            Check(!BarrierMath.EdgeCrossing(c, At(40f, 30f), At(30f, 30f), out pt, out nrm), "circle: a step past it");
            Check(!BarrierMath.EdgeCrossing(c, At(10f, 0f), At(-5f, 0f), out pt, out nrm), "circle: a step that starts inside does not ENTER");

            // A rotated rectangle: a step along its own X axis from outside meets the +X face, whose normal is the rotated X axis.
            const float yaw = 40f;
            var centre = At(100f, -20f);
            TerritoryGround r = Ground(Part(TerritoryShapeKind.Rectangle, centre.x, centre.z, 10f, 30f, yaw));
            Quaternion rot = Quaternion.Euler(0f, yaw, 0f);
            Vector3 from = centre + rot * new Vector3(25f, 0f, 4f), to = centre + rot * new Vector3(5f, 0f, 4f);
            Check(BarrierMath.EdgeCrossing(r, from, to, out pt, out nrm), "rectangle: a step in through the width face");
            Near(pt, centre + rot * new Vector3(10f, 0f, 4f), "rectangle: on the face");
            Near(nrm, rot * new Vector3(1f, 0f, 0f), "rectangle: the face's normal in the ruler frame");
            from = centre + rot * new Vector3(-3f, 0f, -50f);
            to = centre + rot * new Vector3(-3f, 0f, -10f);
            Check(BarrierMath.EdgeCrossing(r, from, to, out pt, out nrm), "rectangle: a step in through the depth face");
            Near(nrm, rot * new Vector3(0f, 0f, -1f), "rectangle: the -Z face's normal");
            Near(pt, centre + rot * new Vector3(-3f, 0f, -30f), "rectangle: on that face");

            // Compound: the FIRST edge along the step wins, and a seam is not an edge.
            TerritoryGround bar = Ground(Part(TerritoryShapeKind.Rectangle, 5f, 5f, 5f, 5f), Part(TerritoryShapeKind.Rectangle, 15f, 5f, 5f, 5f));
            Check(BarrierMath.EdgeCrossing(bar, At(40f, 5f), At(-10f, 5f), out pt, out nrm), "bar: a step through both parts");
            Near(pt, At(20f, 5f), "bar: enters at the far end, not at the seam", 1e-3f);
            Near(nrm, new Vector3(1f, 0f, 0f), "bar: the outer face's normal");

            // Two separate parts with a gap: entering the second after leaving the first is a new entry, the nearer one first.
            TerritoryGround gap = Ground(Part(TerritoryShapeKind.Circle, 0f, 0f, 10f), Part(TerritoryShapeKind.Circle, 50f, 0f, 10f));
            Check(BarrierMath.EdgeCrossing(gap, At(80f, 0f), At(-80f, 0f), out pt, out nrm), "gap: a long step");
            Near(pt, At(60f, 0f), "gap: the first edge met is the near part's", 1e-3f);

            // Overlapping circles: entering at the cusp side picks whichever circle is met first.
            TerritoryGround lens = Ground(Part(TerritoryShapeKind.Circle, -1f, 0f, 2f), Part(TerritoryShapeKind.Circle, 1f, 0f, 2f));
            Check(BarrierMath.EdgeCrossing(lens, At(0f, 5f), At(0f, 0f), out pt, out nrm), "lens: straight down the cusp line");
            Check(pt.z > 1.6f && pt.z < 1.8f && nrm.z > 0f, "lens: the cusp's height, normal upward");

            // The normal is always a horizontal unit vector.
            var rng = new Random(5);
            for (int i = 0; i < 200; i++)
            {
                var a = new Vector3((float)(rng.NextDouble() * 200 - 100), 0f, (float)(rng.NextDouble() * 200 - 100));
                var b = new Vector3((float)(rng.NextDouble() * 200 - 100), 0f, (float)(rng.NextDouble() * 200 - 100));
                TerritoryGround any = Ground(Part(TerritoryShapeKind.Rectangle, 0f, 0f, 30f, 20f, 33f), Part(TerritoryShapeKind.Circle, 25f, 10f, 20f));
                if (any.Contains(a)) continue;
                BarrierMath.EdgeCrossing(any, a, b, out Vector3 x, out Vector3 nn);
                Check(Math.Abs(nn.x * nn.x + nn.z * nn.z - 1f) < 1e-3f && nn.y == 0f, "a horizontal unit normal, entered or not");
            }
        }

        private static void BarrierClosest()
        {
            TerritoryGround c = Ground(Part(TerritoryShapeKind.Circle, 10f, 10f, 20f));
            Check(BarrierMath.ClosestEdge(c, At(40f, 10f), out Vector3 edge, out Vector3 nrm, out float d), "circle: outside has a closest edge");
            Near(edge, At(30f, 10f), "circle: on the near side of the edge");
            Near(nrm, new Vector3(1f, 0f, 0f), "circle: normal from the edge toward the point");
            Near(d, 10f, "circle: distance");
            Check(!BarrierMath.ClosestEdge(c, At(15f, 10f), out edge, out nrm, out d), "circle: inside has none");

            // A rotated box: the closest edge point is on a face, and past a corner it is the corner with a diagonal normal.
            const float yaw = 25f;
            TerritoryGround r = Ground(Part(TerritoryShapeKind.Rectangle, 0f, 0f, 10f, 20f, yaw));
            Quaternion rot = Quaternion.Euler(0f, yaw, 0f);
            Vector3 p = rot * new Vector3(14f, 0f, 3f);
            Check(BarrierMath.ClosestEdge(r, p, out edge, out nrm, out d), "box: face case");
            Near(edge, rot * new Vector3(10f, 0f, 3f), "box: the foot on the face");
            Near(nrm, rot * new Vector3(1f, 0f, 0f), "box: the face normal");
            Near(d, 4f, "box: distance to the face", 1e-3f);
            p = rot * new Vector3(13f, 0f, 24f);
            Check(BarrierMath.ClosestEdge(r, p, out edge, out nrm, out d), "box: corner case");
            Near(edge, rot * new Vector3(10f, 0f, 20f), "box: the corner");
            Near(d, 5f, "box: distance to the corner", 1e-3f);
            Near(nrm, rot * new Vector3(0.6f, 0f, 0.8f), "box: the diagonal normal");

            // Compound: the nearer part's edge, and NOT the seam between overlapping parts.
            TerritoryGround bar = Ground(Part(TerritoryShapeKind.Rectangle, 5f, 5f, 5f, 5f), Part(TerritoryShapeKind.Rectangle, 15f, 5f, 5f, 5f));
            Check(BarrierMath.ClosestEdge(bar, At(30f, 5f), out edge, out nrm, out d), "bar: outside");
            Near(edge, At(20f, 5f), "bar: the far end");
            Near(d, 10f, "bar: distance");
            Check(!BarrierMath.ClosestEdge(bar, At(10f, 5f), out edge, out nrm, out d), "bar: the seam is inside, not an edge");
        }

        // ─────────────────────────────────────────────────────────────
        // PUSH MATH
        // ─────────────────────────────────────────────────────────────

        private static void BarrierPushMath()
        {
            var outward = new Vector3(1f, 0f, 0f);
            Near(BarrierMath.InwardSpeed(new Vector3(-5f, 2f, 0f), outward), 5f, "moving in at 5 m/s");
            Near(BarrierMath.InwardSpeed(new Vector3(3f, 0f, 9f), outward), -3f, "moving out is negative");
            Near(BarrierMath.InwardSpeed(new Vector3(0f, 7f, 4f), outward), 0f, "along the edge, or falling, is nothing");

            // Cancelling: only the inward part goes; sliding and leaving are kept, and so is falling.
            Near(BarrierMath.CancelInward(new Vector3(-5f, -2f, 3f), outward), new Vector3(0f, -2f, 3f), "the inward part is cancelled, the slide and the fall are kept");
            Near(BarrierMath.CancelInward(new Vector3(4f, 1f, 3f), outward), new Vector3(4f, 1f, 3f), "leaving is untouched");
            var diag = new Vector3(0.6f, 0f, 0.8f);
            Vector3 v = BarrierMath.CancelInward(new Vector3(-6f, 0f, -2f), diag);
            Near(BarrierMath.InwardSpeed(v, diag), 0f, "a diagonal edge: nothing inward is left", 1e-4f);

            // Strength: none below a stroll, 0.2 at the least push, 1 at a sprint, never falling as the push grows.
            Near(BarrierMath.Strength(0f), 0f, "standing still");
            Near(BarrierMath.Strength(BarrierMath.MinPressSpeed - 0.01f), 0f, "under the least push");
            Near(BarrierMath.Strength(-4f), 0f, "moving out");
            Near(BarrierMath.Strength(float.NaN), 0f, "NaN");
            Check(Math.Abs(BarrierMath.Strength(BarrierMath.MinPressSpeed) - (0.2f + 0.8f * BarrierMath.MinPressSpeed / BarrierMath.FullPressSpeed)) < 1e-4f, "the least push");
            Near(BarrierMath.Strength(BarrierMath.FullPressSpeed), 1f, "a sprint");
            Near(BarrierMath.Strength(50f), 1f, "and past it");
            float prev = 0f;
            bool monotone = true;
            for (float s = 0f; s <= 12f; s += 0.1f)
            {
                float st = BarrierMath.Strength(s);
                monotone &= st >= prev - 1e-6f && st >= 0f && st <= 1f;
                prev = st;
            }
            Check(monotone, "strength never falls and stays in 0..1");

            // Reach: an animal's radius counts, within limits.
            Near(BarrierMath.ReachFor(0f), BarrierMath.PressReach, "a point-sized thing");
            Near(BarrierMath.ReachFor(1f), BarrierMath.PressReach + 1f, "a troll's radius adds");
            Check(BarrierMath.ReachFor(50f) <= 3.5f + 1e-4f, "a huge one is capped");
            Check(BarrierMath.Pressing(1f, 1.5f, 2f), "close and moving in presses");
            Check(!BarrierMath.Pressing(2f, 1.5f, 2f), "too far does not");
            Check(!BarrierMath.Pressing(0.5f, 1.5f, 0.3f), "close but idle does not");
            Check(BarrierMath.Pressing(1.5f, 1.5f, BarrierMath.MinPressSpeed), "exactly at both limits does");
        }

        // ─────────────────────────────────────────────────────────────
        // THROTTLE
        // ─────────────────────────────────────────────────────────────

        private static void BarrierThrottle()
        {
            float next = 0f;
            Check(BarrierMath.FlareDue(ref next, 10f), "the first flare is due");
            Near(next, 10f + BarrierMath.FlareInterval, "and sets the next time");
            Check(!BarrierMath.FlareDue(ref next, 10.1f), "0.1 s later is too soon");
            Check(!BarrierMath.FlareDue(ref next, 10.49f), "0.49 s later is too soon");
            Check(BarrierMath.FlareDue(ref next, 10.5f), "0.5 s later is due");
            Check(!BarrierMath.FlareDue(ref next, 10.5f), "twice in one instant is not");

            // At 60 frames a second, five seconds of pushing is ten flares, never more than one per half second.
            next = 0f;
            int fired = 0;
            float last = -100f;
            bool spaced = true;
            for (int i = 0; i < 300; i++)
            {
                float t = 20f + i / 60f;
                if (!BarrierMath.FlareDue(ref next, t)) continue;
                fired++;
                spaced &= t - last >= BarrierMath.FlareInterval - 1e-4f;
                last = t;
            }
            Check(fired >= 9 && fired <= 10 && spaced, $"five seconds of pushing fire {fired} flares, spaced at least half a second");

            // A clock that ran backwards (a new session with the same thing id) does not leave a thing silent.
            next = 5000f;
            Check(BarrierMath.FlareDue(ref next, 3f), "an old far-future time is discarded");
            next = 0f;
            Check(BarrierMath.FlareDue(ref next, 0f), "time zero is due at zero");

            // Each thing has its own clock.
            float a = 0f, b = 0f;
            Check(BarrierMath.FlareDue(ref a, 1f) && BarrierMath.FlareDue(ref b, 1f), "two things flare in the same instant");
            Check(!BarrierMath.FlareDue(ref a, 1.2f) && !BarrierMath.FlareDue(ref b, 1.2f), "and each waits its own half second");

            // A stranger's dwell: builds while pressing, resets the moment they stop.
            float dwell = 0f;
            for (int i = 0; i < 10; i++) dwell = BarrierMath.AdvanceDwell(dwell, true, 0.05f);
            Near(dwell, 0.5f, "half a second of pressing", 1e-4f);
            Check(dwell >= BarrierMath.RemotePlayerDwell, "an admin passing in a third of a second never gets there; a stranger pressing on does");
            dwell = BarrierMath.AdvanceDwell(dwell, false, 0.05f);
            Near(dwell, 0f, "one frame off resets it");
            dwell = 0f;
            for (int i = 0; i < 5; i++) dwell = BarrierMath.AdvanceDwell(dwell, true, 0.05f);
            Check(dwell < BarrierMath.RemotePlayerDwell, "a quarter second is not enough");
        }

        // ─────────────────────────────────────────────────────────────
        // REACH
        // ─────────────────────────────────────────────────────────────

        private static void BarrierReachTest()
        {
            TerritoryGround g = Ground(
                Part(TerritoryShapeKind.Rectangle, 0f, 0f, 30f, 10f, 27f),
                Part(TerritoryShapeKind.Circle, 80f, 40f, 15f),
                Part(TerritoryShapeKind.Square, -60f, 70f, 12f, 0f, 70f));
            var rng = new Random(11);
            int misses = 0, nears = 0;
            for (int i = 0; i < 4000; i++)
            {
                var p = new Vector3((float)(rng.NextDouble() * 300 - 150), 0f, (float)(rng.NextDouble() * 300 - 150));
                float pad = BarrierMath.TrackMargin;
                if (g.DistanceOutside(p) <= pad)
                {
                    nears++;
                    if (!BarrierMath.WithinReach(g, p, pad)) misses++;
                }
            }
            Check(nears > 200, $"enough points near the ground were drawn ({nears})");
            Check(misses == 0, $"{misses} points within the pad of the ground were reported out of reach");
            Check(!BarrierMath.WithinReach(g, At(1000f, 1000f), 14f), "a far point is out of reach");
            Check(BarrierMath.WithinReach(g, At(0f, 0f), 0f), "the middle is in reach");
        }

        // ─────────────────────────────────────────────────────────────
        // A WHOLE WALK
        // ─────────────────────────────────────────────────────────────

        private static void BarrierSimulatedWalk()
        {
            // The hold as Barrier.Enforce runs it, frame by frame, on plain numbers: a thing at 6 m/s pushed at the L's
            // inner corner from the notch, physics moving it, the hold putting it back and cancelling what pushes in.
            TerritoryGround ell = Ground(Part(TerritoryShapeKind.Rectangle, 0f, 0f, 20f, 10f), Part(TerritoryShapeKind.Rectangle, 10f, 20f, 10f, 20f));
            const float dt = 1f / 50f, speed = 6f;
            Vector3 pos = At(-12f, 30f);
            Vector3 want = new Vector3(0.55f, 0f, -0.83f);   // toward the far side of the bar, through the corner
            float len = Mathf.Sqrt(want.x * want.x + want.z * want.z);
            want = new Vector3(want.x / len, 0f, want.z / len);

            Vector3 last = pos;
            bool haveLast = true;
            float clock = 0f;
            int holds = 0, evicts = 0, insideAtEnd = 0;
            float travelled = 0f;
            Vector3 start = pos;

            for (int frame = 0; frame < 400; frame++)
            {
                clock += dt;
                Vector3 velocity = want * speed;                                  // the AI asks for this every step
                pos = pos + velocity * dt;                                        // physics moves it
                HoldKind kind = BarrierMath.Decide(ell, haveLast, last, 0.02f, pos, Margin, out Vector3 to);
                if (kind != HoldKind.Free)
                {
                    BarrierMath.EdgeCrossing(ell, to, pos, out Vector3 edge, out Vector3 outward);
                    // Cancel what pushes in, and let the thing keep the rest as its next intent (the AI does; here 'want' carries on).
                    Vector3 kept = BarrierMath.CancelInward(velocity, outward);
                    Check(BarrierMath.InwardSpeed(kept, outward) <= 1e-3f, "the velocity left after a hold does not push in");
                    pos = to;
                    if (kind == HoldKind.Hold) holds++; else evicts++;
                    // Slide: the next step's intent is what is left of the wish along the edge.
                    Vector3 slide = kept;
                    float sl = Mathf.Sqrt(slide.x * slide.x + slide.z * slide.z);
                    if (sl > 0.1f) want = new Vector3(slide.x / sl, 0f, slide.z / sl);
                }
                if (ell.Contains(pos)) insideAtEnd++;
                last = pos;
                haveLast = true;
            }
            Check(insideAtEnd == 0, $"never left inside after a hold ({insideAtEnd} frames were)");
            Check(evicts == 0, $"a steady push never looks like a spawn (evicted {evicts} times)");
            Check(holds > 0, "the push was held");
            travelled = Flat(pos, start);
            Check(travelled > 5f, $"it slid along the edge rather than shaking in place (moved {travelled:0.#} m)");
            Check(!ell.Contains(pos), "and ends outside");
        }
    }
}
