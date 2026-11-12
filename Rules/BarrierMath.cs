using System;
using System.Collections.Generic;
using UnityEngine;

namespace TerroritySmellatory
{
    /// <summary>What the barrier does about a thing that is inside a barrier's ground right now.</summary>
    internal enum HoldKind
    {
        /// <summary>It is outside: nothing to do.</summary>
        Free,

        /// <summary>It stepped in from a place outside: put it back there.</summary>
        Hold,

        /// <summary>It was found inside (spawned there, loaded in, portalled in, the barrier just came on): take it to the nearest way out.</summary>
        Evict
    }

    // THE RUNIC BARRIER'S DECISIONS, AS PLAIN NUMBERS (0.3.0).
    //
    // Rules/Barrier.cs is the part that talks to the game - characters, rigidbodies, the local player. Everything it
    // has to DECIDE lives here with no game type in sight beyond Vector3, so the off-game harness compiles and tests
    // this file (tests/BarrierTests.cs): where a thing goes back to, where a step crossed the edge and which way is
    // out there, who is stopped, how hard a push was, and when a flare may fire again.
    //
    // A barrier sits at a territory's OUTER edge, the edge of its whole ground (TerritoryGround), so a combined
    // territory has no barrier along a seam and a territory nested inside another has none of its own unless it
    // is a barrier territory itself. Height is ignored like everywhere else: a footprint, so a bat overhead
    // counts the same as a boar.
    //
    // NESTING (0.3.2). A barrier keeps things out of the ground its territory GOVERNS - the territory whose rules apply
    // there, the way every other rule decides (TerritoryIndex.GoverningAt). Ground inside it that a territory which
    // OUTRANKS it governs - a town drawn inside a kingdom - is a POCKET: the kingdom's barrier does not reach into
    // it, and the town's own rules, barrier or none, decide there. Without that, a kingdom sealed against visitors
    // sealed its public town too (whoever logged in or came through a portal inside it was thrown to the kingdom's
    // edge, kilometres away), and a kingdom that keeps monsters out threw out the monsters of an arena town inside
    // it. A thing in a pocket is simply outside the barrier; the pocket's edge is where it is held when it tries to
    // walk out into the governed ground, exactly as it would be at the outer edge, and a thing FOUND in the governed
    // ground is still taken out by the nearest way out of the OUTER edge - never into a pocket, which would put a
    // monster into a town.
    internal static class BarrierMath
    {
        /// <summary>How far outside the edge a thing is put when it is moved out: past the edge, and clear of a body's wobble.</summary>
        internal const float ExitMargin = 0.75f;

        /// <summary>A hold puts a thing back where it was seen outside, if that was this recently (seconds) ...</summary>
        internal const float MaxHoldAge = 1.5f;

        /// <summary>... and this close (metres, on the ground). Anything else jumped: a portal, a spawn, a teleport.</summary>
        internal const float MaxHoldStep = 12f;

        /// <summary>Things within this of a barrier's edge are watched; everything further is not touched at all.</summary>
        internal const float TrackMargin = 14f;

        /// <summary>A thing counts as pressing on the barrier when its centre is this near the edge, plus its own radius.</summary>
        internal const float PressReach = 1.5f;
        private const float PressReachMax = 3.5f;

        /// <summary>Slower than a stroll toward the edge is not a push.</summary>
        internal const float MinPressSpeed = 0.6f;

        /// <summary>A push at this inward speed (a sprinting wolf) flares at full strength.</summary>
        internal const float FullPressSpeed = 8f;

        /// <summary>Per thing: how long between two flares.</summary>
        internal const float FlareInterval = 0.5f;

        /// <summary>
        /// Another player's game tells us where they are and how fast they move, not whether they are an admin. An admin
        /// walks through, so the barrier does not flare for a stranger until they have kept pressing this long.
        /// </summary>
        internal const float RemotePlayerDwell = 0.35f;

        // ─────────────────────────────────────────────────────────────
        // WHO IS STOPPED
        // ─────────────────────────────────────────────────────────────

        /// <summary>The barrier is on: RunicBarrier, which is part of the no-monster rule and so needs NoMonsterSpawns.</summary>
        internal static bool Active(TerritoryFlags flags)
        {
            const TerritoryFlags both = TerritoryFlags.RunicBarrier | TerritoryFlags.NoMonsterSpawns;
            return (flags & both) == both;
        }

        /// <summary>Players who are not members are kept out as well: a setting of the barrier, so it needs the barrier.</summary>
        internal static bool KeepsOutVisitors(TerritoryFlags flags) =>
            Active(flags) && (flags & TerritoryFlags.BarrierKeepsOutVisitors) != 0;

        /// <summary>
        /// Does a barrier with these flags stop this thing? A player: only with the visitor option, and never a
        /// member, and never an admin while admins bypass protections. Anything else: only a hostile creature that is
        /// not tamed - passive animals, tamed creatures and a player's own summons (not hostile) walk through.
        /// </summary>
        internal static bool Stops(TerritoryFlags flags, bool isPlayer, bool isTamed, bool hostile, bool isMember, bool isAdmin, bool bypass)
        {
            if (isPlayer) return KeepsOutVisitors(flags) && !isMember && !(isAdmin && bypass);
            return Active(flags) && hostile && !isTamed;
        }

        // ─────────────────────────────────────────────────────────────
        // POCKETS - the ground inside a barrier that another territory governs
        // ─────────────────────────────────────────────────────────────

        /// <summary>One outranking territory's ground, with the box around it so most points never reach the real test.</summary>
        internal struct Pocket
        {
            public TerritoryGround Ground;
            public float MinX, MinZ, MaxX, MaxZ;

            public bool Contains(Vector3 p) =>
                p.x >= MinX && p.x <= MaxX && p.z >= MinZ && p.z <= MaxZ && Ground.Contains(p);
        }

        /// <summary>
        /// The pockets of the territory at <paramref name="index"/> in <paramref name="all"/>, which TerritoryIndex keeps in
        /// precedence order: every territory before it outranks it, so each one whose box meets the box
        /// (<paramref name="minX"/>..<paramref name="maxZ"/>) around the territory's ground can govern a piece of it.
        /// </summary>
        internal static Pocket[] PocketsFor(IReadOnlyList<Territory> all, int index, float minX, float minZ, float maxX, float maxZ)
        {
            var found = new List<Pocket>();
            for (int i = 0; i < index; i++)
            {
                TerritoryGround ground = all[i].Ground;
                ground.Bounds(out float x0, out float z0, out float x1, out float z1);
                if (x1 < minX || x0 > maxX || z1 < minZ || z0 > maxZ) continue;
                found.Add(new Pocket { Ground = ground, MinX = x0, MinZ = z0, MaxX = x1, MaxZ = z1 });
            }
            return found.ToArray();
        }

        /// <summary>Is the point in the ground the territory governs: inside it, and inside none of its pockets?</summary>
        internal static bool Governs(TerritoryGround ground, Pocket[] pockets, Vector3 p)
        {
            if (!ground.Contains(p)) return false;
            if (pockets != null)
                for (int i = 0; i < pockets.Length; i++)
                    if (pockets[i].Contains(p)) return false;
            return true;
        }

        // ─────────────────────────────────────────────────────────────
        // WHERE
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Is the point within <paramref name="pad"/> of the ground, by a cheap circle around each part? Never
        /// says no to a point the ground is within pad of; may say yes a little early near a corner. It is the
        /// per-frame gate, so a thing far from every part costs a few multiplications.
        /// </summary>
        internal static bool WithinReach(TerritoryGround ground, Vector3 p, float pad)
        {
            for (int i = 0; i < ground.Count; i++)
            {
                TerritoryShape s = ground[i];
                if (s.IsEmpty) continue;
                float dx = p.x - s.CenterX, dz = p.z - s.CenterZ;
                float r = s.OuterRadius + pad;
                if (dx * dx + dz * dz <= r * r) return true;
            }
            return false;
        }

        private static Vector3 ToLocal(TerritoryShape s, Vector3 p)
        {
            var d = new Vector3(p.x - s.CenterX, 0f, p.z - s.CenterZ);
            return s.Kind == TerritoryShapeKind.Circle ? d : Quaternion.Euler(0f, -s.Yaw, 0f) * d;
        }

        private static Vector3 ToWorld(TerritoryShape s, Vector3 local, float y)
        {
            Vector3 d = s.Kind == TerritoryShapeKind.Circle ? local : Quaternion.Euler(0f, s.Yaw, 0f) * local;
            return new Vector3(s.CenterX + d.x, y, s.CenterZ + d.z);
        }

        private static float FlatSqr(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dz = a.z - b.z;
            return dx * dx + dz * dz;
        }

        private const int Rays = 64;

        // Scratch for RayExit: the intervals a line spends inside each part. Main thread only, grown when needed, so
        // moving a thing out allocates nothing after the first time.
        private static float[] _from = new float[16];
        private static float[] _to = new float[16];

        /// <summary>
        /// How far from <paramref name="p"/> (inside the ground) the line in the horizontal direction (dx, dz), a unit
        /// vector, stays inside the UNION: the end of the chain of parts' intervals that are joined to each other from
        /// where it starts. <paramref name="gap"/> is how far past that end the next part begins (infinity if none).
        /// Exact for circles and rotated boxes, and a seam between two parts is not an end.
        /// </summary>
        private static float RayExit(TerritoryGround ground, Vector3 p, float dx, float dz, out float gap)
        {
            int n = ground.Count;
            if (_from.Length < n)
            {
                _from = new float[n];
                _to = new float[n];
            }

            int used = 0;
            for (int i = 0; i < n; i++)
            {
                TerritoryShape s = ground[i];
                if (s.IsEmpty) continue;

                Vector3 l = ToLocal(s, p);
                float a, b;
                if (s.Kind == TerritoryShapeKind.Circle)
                {
                    float ld = l.x * dx + l.z * dz;
                    float disc = ld * ld - (l.x * l.x + l.z * l.z - s.HalfX * s.HalfX);
                    if (disc < 0f) continue;
                    float root = Mathf.Sqrt(disc);
                    a = -ld - root;
                    b = -ld + root;
                }
                else
                {
                    Vector3 d = Quaternion.Euler(0f, -s.Yaw, 0f) * new Vector3(dx, 0f, dz);
                    a = float.MinValue;
                    b = float.MaxValue;
                    if (!Span(l.x, d.x, s.HalfX, ref a, ref b) || !Span(l.z, d.z, s.HalfZ, ref a, ref b) || a > b) continue;
                }
                if (b <= 0f) continue;
                _from[used] = a;
                _to[used] = b;
                used++;
            }

            // Chain: start at the point, and keep taking any interval that begins where the reach ends or before it.
            float reach = 0f;
            for (bool grew = true; grew;)
            {
                grew = false;
                for (int i = 0; i < used; i++)
                {
                    if (_from[i] <= reach + 1e-4f && _to[i] > reach)
                    {
                        reach = _to[i];
                        grew = true;
                    }
                }
            }

            gap = float.PositiveInfinity;
            for (int i = 0; i < used; i++)
                if (_from[i] > reach + 1e-4f && _from[i] - reach < gap) gap = _from[i] - reach;
            return reach;
        }

        /// <summary>The slab of one axis: the part of the line between -half and half, folded into [a, b]. False if the line runs parallel and outside.</summary>
        private static bool Span(float start, float delta, float half, ref float a, ref float b)
        {
            if (Mathf.Abs(delta) < 1e-9f) return Mathf.Abs(start) <= half;
            float t1 = (-half - start) / delta, t2 = (half - start) / delta;
            a = Mathf.Max(a, Mathf.Min(t1, t2));
            b = Mathf.Min(b, Mathf.Max(t1, t2));
            return true;
        }

        /// <summary>
        /// A point just outside the whole ground, near <paramref name="p"/>; <paramref name="p"/> itself when it is
        /// already outside. Height is kept (the caller settles it on the ground).
        /// </summary>
        /// <remarks>
        /// The nearest way out is found by asking, along many straight lines from the point, how far the UNION
        /// keeps it inside (RayExit - exact, and blind to seams: two parts that overlap or touch are one run), and
        /// taking the shortest. The lines are 64 evenly spaced ones plus the directions each part holding the point
        /// calls "out" - the radius for a circle, the four face normals of a box - so the exit through a stretch of
        /// one part's own edge is found exactly, single shapes included; then the best direction is refined by a
        /// golden-section search over the gap to its neighbours, which pulls a way out that is really a corner
        /// (the inner corner of an L, the waist of two circles) onto the corner. So a point deep inside a part nested
        /// in another leaves by the outer part's edge, and one beside a seam leaves by the side, never the seam.
        /// The point is put <paramref name="margin"/> past the edge, less if another part begins sooner. If nothing
        /// works (it cannot happen for finite ground), rings around the point, then the box around the whole ground.
        /// </remarks>
        internal static Vector3 NearestOutside(TerritoryGround ground, Vector3 p, float margin)
        {
            if (!ground.Contains(p)) return p;
            if (!(margin >= 0.05f)) margin = 0.05f;

            float bestT = float.MaxValue, bestGap = 0f, bestDx = 1f, bestDz = 0f;

            for (int k = 0; k < Rays; k++)
            {
                float a = k * (2f * Mathf.PI / Rays);
                Consider(ground, p, Mathf.Cos(a), Mathf.Sin(a), ref bestT, ref bestGap, ref bestDx, ref bestDz);
            }
            for (int i = 0; i < ground.Count; i++)
            {
                TerritoryShape s = ground[i];
                if (!s.Contains(p)) continue;
                if (s.Kind == TerritoryShapeKind.Circle)
                {
                    float dx = p.x - s.CenterX, dz = p.z - s.CenterZ;
                    float len = Mathf.Sqrt(dx * dx + dz * dz);
                    if (len > 1e-4f) Consider(ground, p, dx / len, dz / len, ref bestT, ref bestGap, ref bestDx, ref bestDz);
                    continue;
                }
                Quaternion rot = Quaternion.Euler(0f, s.Yaw, 0f);
                Vector3 ax = rot * new Vector3(1f, 0f, 0f), az = rot * new Vector3(0f, 0f, 1f);
                Consider(ground, p, ax.x, ax.z, ref bestT, ref bestGap, ref bestDx, ref bestDz);
                Consider(ground, p, -ax.x, -ax.z, ref bestT, ref bestGap, ref bestDx, ref bestDz);
                Consider(ground, p, az.x, az.z, ref bestT, ref bestGap, ref bestDx, ref bestDz);
                Consider(ground, p, -az.x, -az.z, ref bestT, ref bestGap, ref bestDx, ref bestDz);
            }

            // Refine around the best line: the exit distance is continuous in the angle and, close to its
            // best, has one low point (a face's foot is a smooth dip, a corner a sharp one).
            if (bestT < float.MaxValue)
            {
                float centre = (float)Math.Atan2(bestDz, bestDx);
                float lo = centre - 2f * Mathf.PI / Rays, hi = centre + 2f * Mathf.PI / Rays;
                const float phi = 0.6180340f;
                float x1 = hi - phi * (hi - lo), x2 = lo + phi * (hi - lo);
                float f1 = Probe(ground, p, x1, ref bestT, ref bestGap, ref bestDx, ref bestDz);
                float f2 = Probe(ground, p, x2, ref bestT, ref bestGap, ref bestDx, ref bestDz);
                for (int it = 0; it < 24; it++)
                {
                    if (f1 < f2)
                    {
                        hi = x2; x2 = x1; f2 = f1;
                        x1 = hi - phi * (hi - lo);
                        f1 = Probe(ground, p, x1, ref bestT, ref bestGap, ref bestDx, ref bestDz);
                    }
                    else
                    {
                        lo = x1; x1 = x2; f1 = f2;
                        x2 = lo + phi * (hi - lo);
                        f2 = Probe(ground, p, x2, ref bestT, ref bestGap, ref bestDx, ref bestDz);
                    }
                }
            }

            if (bestT < float.MaxValue)
            {
                // Past the edge by the margin; less if another part starts sooner, but still clear of it.
                float past = Mathf.Min(margin, bestGap * 0.5f);
                for (int tries = 0; tries < 6; tries++)
                {
                    var q = new Vector3(p.x + bestDx * (bestT + past), p.y, p.z + bestDz * (bestT + past));
                    if (!ground.Contains(q)) return q;
                    past = tries == 0 ? margin : past + margin * 0.5f;
                }
            }

            // Rings, nearest first. Not reachable for real ground; kept so the answer is always outside.
            ground.Bounds(out float minX, out float minZ, out float maxX, out float maxZ);
            float span = Mathf.Max(maxX - minX, maxZ - minZ) + 2f * margin;
            for (float r = margin * 2f; r <= span; r *= 1.5f)
            {
                for (int k = 0; k < 32; k++)
                {
                    float a = k * (2f * Mathf.PI / 32f);
                    var c = new Vector3(p.x + Mathf.Cos(a) * r, p.y, p.z + Mathf.Sin(a) * r);
                    if (!ground.Contains(c)) return c;
                }
            }
            return new Vector3(maxX + margin, p.y, p.z);
        }

        private static void Consider(TerritoryGround ground, Vector3 p, float dx, float dz, ref float bestT, ref float bestGap, ref float bestDx, ref float bestDz)
        {
            float t = RayExit(ground, p, dx, dz, out float gap);
            if (!(t < bestT)) return;
            bestT = t;
            bestGap = gap;
            bestDx = dx;
            bestDz = dz;
        }

        private static float Probe(TerritoryGround ground, Vector3 p, float angle, ref float bestT, ref float bestGap, ref float bestDx, ref float bestDz)
        {
            float dx = Mathf.Cos(angle), dz = Mathf.Sin(angle);
            float t = RayExit(ground, p, dx, dz, out float gap);
            if (t < bestT)
            {
                bestT = t;
                bestGap = gap;
                bestDx = dx;
                bestDz = dz;
            }
            return t;
        }

        /// <summary>
        /// What to do about a thing now at <paramref name="now"/>. Free when it is outside. Hold - back to
        /// <paramref name="last"/> - when it was seen outside recently and nearby (and that place is still outside: the
        /// ground may have changed under it). Otherwise Evict, to <see cref="NearestOutside"/>.
        /// </summary>
        internal static HoldKind Decide(TerritoryGround ground, bool haveLast, Vector3 last, float lastAge, Vector3 now, float margin, out Vector3 to) =>
            Decide(ground, null, haveLast, last, lastAge, now, margin, out to);

        /// <summary>
        /// The same, for a barrier with pockets (see NESTING above): "inside" means the ground the territory GOVERNS, so a thing in a
        /// pocket is Free, a thing that steps from a pocket into the governed ground is Held back to where it was seen, and
        /// a thing found in the governed ground goes to the nearest way out of the OUTER edge.
        /// </summary>
        internal static HoldKind Decide(TerritoryGround ground, Pocket[] pockets, bool haveLast, Vector3 last, float lastAge, Vector3 now, float margin, out Vector3 to)
        {
            if (!Governs(ground, pockets, now))
            {
                to = now;
                return HoldKind.Free;
            }

            if (haveLast && lastAge >= 0f && lastAge <= MaxHoldAge && FlatSqr(last, now) <= MaxHoldStep * MaxHoldStep && !Governs(ground, pockets, last))
            {
                to = last;
                return HoldKind.Hold;
            }

            to = NearestOutside(ground, now, margin);
            return HoldKind.Evict;
        }

        // ─────────────────────────────────────────────────────────────
        // THE EDGE, WHERE SOMETHING PUSHES
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Where the step from <paramref name="from"/> (outside) to <paramref name="to"/> first enters the ground, and
        /// the horizontal unit vector pointing out of the ground there. False - with the point and direction still
        /// filled in, from the nearer end - when the step does not enter it (a start that is inside, or a miss).
        /// </summary>
        internal static bool EdgeCrossing(TerritoryGround ground, Vector3 from, Vector3 to, out Vector3 point, out Vector3 outward)
        {
            float best = float.MaxValue;
            float nx = 0f, nz = 0f;
            for (int i = 0; i < ground.Count && !ground.Contains(from); i++)
            {
                TerritoryShape s = ground[i];
                if (s.IsEmpty) continue;
                if (SegmentEntry(s, from, to, out float t, out float ox, out float oz) && t < best)
                {
                    best = t;
                    nx = ox;
                    nz = oz;
                }
            }

            if (best == float.MaxValue)
            {
                point = from;
                float dx = from.x - to.x, dz = from.z - to.z;
                float len = Mathf.Sqrt(dx * dx + dz * dz);
                outward = len > 1e-5f ? new Vector3(dx / len, 0f, dz / len) : new Vector3(1f, 0f, 0f);
                return false;
            }

            point = new Vector3(from.x + (to.x - from.x) * best, from.y + (to.y - from.y) * best, from.z + (to.z - from.z) * best);
            outward = new Vector3(nx, 0f, nz);
            return true;
        }

        /// <summary>Where the segment first meets one part, as a fraction of the segment, and that part's outward normal there.</summary>
        private static bool SegmentEntry(TerritoryShape s, Vector3 from, Vector3 to, out float t, out float nx, out float nz)
        {
            t = 0f;
            nx = nz = 0f;

            Vector3 lf = ToLocal(s, from);
            Vector3 lt = ToLocal(s, to);
            float dx = lt.x - lf.x, dz = lt.z - lf.z;

            if (s.Kind == TerritoryShapeKind.Circle)
            {
                float c = lf.x * lf.x + lf.z * lf.z - s.HalfX * s.HalfX;
                if (c <= 0f)
                {
                    t = 0f;                                    // already inside: the edge is where it stands
                }
                else
                {
                    float a = dx * dx + dz * dz;
                    if (a < 1e-12f) return false;
                    float b = 2f * (lf.x * dx + lf.z * dz);
                    float disc = b * b - 4f * a * c;
                    if (disc < 0f) return false;
                    t = (-b - Mathf.Sqrt(disc)) / (2f * a);
                    if (t < 0f || t > 1f) return false;
                }
                float px = lf.x + dx * t, pz = lf.z + dz * t;
                float len = Mathf.Sqrt(px * px + pz * pz);
                if (len < 1e-5f) { px = 1f; pz = 0f; len = 1f; }
                nx = px / len;
                nz = pz / len;
                return true;
            }

            // A box: the slab method, remembering which face the ray came through last.
            float tMin = float.MinValue, tMax = float.MaxValue;
            bool xFace = true;
            if (!Slab(lf.x, dx, s.HalfX, ref tMin, ref tMax, ref xFace, true)) return false;
            if (!Slab(lf.z, dz, s.HalfZ, ref tMin, ref tMax, ref xFace, false)) return false;
            if (tMin > tMax || tMax < 0f) return false;

            t = Mathf.Max(tMin, 0f);
            if (t > 1f) return false;
            float ex = lf.x + dx * t, ez = lf.z + dz * t;
            Vector3 n = xFace ? new Vector3(ex >= 0f ? 1f : -1f, 0f, 0f) : new Vector3(0f, 0f, ez >= 0f ? 1f : -1f);
            if (tMin < 0f)
            {
                // Started inside: no face was crossed. The nearer face is the way out, so the one to flare on.
                n = s.HalfX - Mathf.Abs(lf.x) <= s.HalfZ - Mathf.Abs(lf.z)
                    ? new Vector3(lf.x >= 0f ? 1f : -1f, 0f, 0f)
                    : new Vector3(0f, 0f, lf.z >= 0f ? 1f : -1f);
            }
            Vector3 w = Quaternion.Euler(0f, s.Yaw, 0f) * n;
            nx = w.x;
            nz = w.z;
            return true;
        }

        private static bool Slab(float start, float delta, float half, ref float tMin, ref float tMax, ref bool xFace, bool isX)
        {
            if (Mathf.Abs(delta) < 1e-9f) return Mathf.Abs(start) <= half;
            float t1 = (-half - start) / delta, t2 = (half - start) / delta;
            float near = Mathf.Min(t1, t2), far = Mathf.Max(t1, t2);
            if (near > tMin)
            {
                tMin = near;
                xFace = isX;
            }
            if (far < tMax) tMax = far;
            return true;
        }

        /// <summary>
        /// The point of the ground's outer edge nearest a point OUTSIDE it, the horizontal unit vector pointing from
        /// the edge toward the point, and how far away it is. False when the point is inside a part.
        /// </summary>
        internal static bool ClosestEdge(TerritoryGround ground, Vector3 p, out Vector3 edge, out Vector3 outward, out float distance)
        {
            edge = p;
            outward = new Vector3(1f, 0f, 0f);
            distance = 0f;

            float best = float.MaxValue;
            for (int i = 0; i < ground.Count; i++)
            {
                TerritoryShape s = ground[i];
                if (s.IsEmpty) continue;

                Vector3 l = ToLocal(s, p);
                Vector3 b, n;
                float d;
                if (s.Kind == TerritoryShapeKind.Circle)
                {
                    float len = Mathf.Sqrt(l.x * l.x + l.z * l.z);
                    d = len - s.HalfX;
                    if (d <= 0f) return false;
                    n = len > 1e-5f ? new Vector3(l.x / len, 0f, l.z / len) : new Vector3(1f, 0f, 0f);
                    b = n * s.HalfX;
                }
                else
                {
                    float cx = Mathf.Clamp(l.x, -s.HalfX, s.HalfX), cz = Mathf.Clamp(l.z, -s.HalfZ, s.HalfZ);
                    float ox = l.x - cx, oz = l.z - cz;
                    d = Mathf.Sqrt(ox * ox + oz * oz);
                    if (d <= 0f) return false;
                    n = new Vector3(ox / d, 0f, oz / d);
                    b = new Vector3(cx, 0f, cz);
                }

                if (d >= best) continue;
                best = d;
                Vector3 wb = ToWorld(s, b, p.y);
                Vector3 wn = s.Kind == TerritoryShapeKind.Circle ? n : Quaternion.Euler(0f, s.Yaw, 0f) * n;
                edge = wb;
                outward = new Vector3(wn.x, 0f, wn.z);
                distance = d;
            }
            return best != float.MaxValue;
        }

        // ─────────────────────────────────────────────────────────────
        // HOW HARD, AND HOW OFTEN
        // ─────────────────────────────────────────────────────────────

        /// <summary>How fast the velocity heads INTO the ground at an edge whose outward normal is given (m/s; negative when leaving).</summary>
        internal static float InwardSpeed(Vector3 velocity, Vector3 outward) => -(velocity.x * outward.x + velocity.z * outward.z);

        /// <summary>The velocity with its inward part removed; along the edge and away from it are kept.</summary>
        internal static Vector3 CancelInward(Vector3 velocity, Vector3 outward)
        {
            float inward = InwardSpeed(velocity, outward);
            if (!(inward > 0f)) return velocity;
            return new Vector3(velocity.x + outward.x * inward, velocity.y, velocity.z + outward.z * inward);
        }

        /// <summary>How near the edge a thing of this radius must be to count as pressing on it.</summary>
        internal static float ReachFor(float radius) => Mathf.Clamp(PressReach + radius, PressReach, PressReachMax);

        /// <summary>Is a thing this far outside the edge, moving in at this speed, pushing on it?</summary>
        internal static bool Pressing(float distanceOutside, float reach, float inwardSpeed) =>
            distanceOutside <= reach && inwardSpeed >= MinPressSpeed;

        /// <summary>0 for no push; otherwise 0.2 for a light touch up to 1 for a sprinting slam. Never falls as the push gets harder.</summary>
        internal static float Strength(float inwardSpeed)
        {
            if (!(inwardSpeed >= MinPressSpeed)) return 0f;
            return Mathf.Clamp(0.2f + 0.8f * (inwardSpeed / FullPressSpeed), 0.2f, 1f);
        }

        /// <summary>
        /// The flare throttle, one per thing: true - and the next time set - when <paramref name="now"/> has reached
        /// <paramref name="next"/>. A clock that ran backwards (a new session) does not leave a thing silent for good.
        /// </summary>
        internal static bool FlareDue(ref float next, float now)
        {
            if (next - now > FlareInterval) next = now;
            if (now < next) return false;
            next = now + FlareInterval;
            return true;
        }

        /// <summary>How long a thing has been pressing without a break: grows while it presses, starts over when it stops.</summary>
        internal static float AdvanceDwell(float dwell, bool pressing, float dt) => pressing ? dwell + dt : 0f;
    }
}
