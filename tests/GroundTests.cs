using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace TerroritySmellatory.Tests
{
    // 0.2.0: a territory made of several parts. The ground's questions, its area, precedence between combined
    // territories, the edits the server runs, sanitising, and the version 2 record and request on the wire.
    internal static partial class Program
    {
        static partial void GroundGroups()
        {
            Run("ground: one part answers exactly as its shape", GroundSinglePart);
            Run("ground: several parts are one area - containment, distance, overlap, seams", GroundCompound);
            Run("ground: area counts overlaps once (against a fine grid)", GroundArea);
            Run("ground: area of parts kilometres apart - rows per run of parts, not across the gaps", GroundAreaFarApart);
            Run("index: a combined territory is sized by its union, and governs every part", IndexCompound);
            Run("edits: combine keeps the target, appends the ground, merges members", EditsCombine);
            Run("edits: add, reshape, remove and split a part", EditsParts);
            Run("sanitize: extra parts are clamped, a NaN part spoils the record, clones are deep", SanitizeParts);
            Run("wire v2: parts round-trip; a version 1 record and a protocol 1 request still read", WireParts);
        }

        private static TerritoryShape Part(TerritoryShapeKind kind, float x, float z, float hx, float hz = 0f, float yaw = 0f) =>
            new TerritoryShape(kind, x, z, hx, hz > 0f ? hz : hx, yaw);

        private static Territory Compound(int id, TerritoryShape first, params TerritoryShape[] more)
        {
            var t = new Territory { Id = id, Name = "C" + id, Flags = TerritoryFlags.None };
            t.SetPart(0, first);
            t.ExtraParts.AddRange(more);
            return t;
        }

        private static void GroundSinglePart()
        {
            var s = Part(TerritoryShapeKind.Rectangle, 10f, -20f, 8f, 30f, 25f);
            var t = Compound(1, s);
            TerritoryGround g = t.Ground;
            Check(g.Count == 1 && !g.IsCompound, "one part, not compound");
            Check(g[0].SameAs(s), "part 0 is the shape");
            var probes = new[] { new Vector3(10f, 0f, -20f), new Vector3(17.5f, 0f, -20f), new Vector3(30f, 0f, 5f), new Vector3(-50f, 0f, -80f) };
            foreach (Vector3 p in probes)
            {
                Check(g.Contains(p) == s.Contains(p), $"contains agrees at {p}");
                Near(g.DistanceOutside(p), s.DistanceOutside(p), $"distance agrees at {p}");
                Check(g.Overlaps(p, 4f) == s.Overlaps(p, 4f), $"overlap agrees at {p}");
                Check(g.ContainsCircle(p, 4f) == s.ContainsCircle(p, 4f), $"contains-circle agrees at {p}");
            }
            Near(g.MeasureArea(), s.Area, "area is the shape's own", 1e-2f);
            Check(g.Describe() == s.Describe(), "described as the shape");
            Check(g.OnOuterEdge(0, s.PerimeterPoint(0.3f)), "every edge of a lone part is outer");
        }

        private static void GroundCompound()
        {
            // An L: a 40 x 20 bar along X and a 20 x 40 bar along Z, overlapping in the corner square.
            var bar = Part(TerritoryShapeKind.Rectangle, 0f, 0f, 20f, 10f);      // x -20..20, z -10..10
            var leg = Part(TerritoryShapeKind.Rectangle, 10f, 20f, 10f, 20f);    // x 0..20,   z 0..40
            var farRing = Part(TerritoryShapeKind.Circle, 200f, 0f, 15f);
            TerritoryGround g = Compound(1, bar, leg, farRing).Ground;

            Check(g.Count == 3 && g.IsCompound, "three parts");
            Check(g.PartAt(new Vector3(-15f, 0f, 0f)) == 0, "the bar's own ground is part 0");
            Check(g.PartAt(new Vector3(10f, 0f, 30f)) == 1, "the leg's own ground is part 1");
            Check(g.PartAt(new Vector3(10f, 0f, 5f)) == 0, "the overlap reports the first part holding it");
            Check(g.PartAt(new Vector3(205f, 0f, 0f)) == 2, "a part far from the others still counts");
            Check(!g.Contains(new Vector3(-10f, 0f, 30f)), "the notch of the L is outside");
            Check(!g.Contains(new Vector3(100f, 0f, 0f)), "between the L and the ring is outside");

            Near(g.DistanceOutside(new Vector3(-10f, 0f, 20f)), 10f, "distance into the notch: 10 m to the bar");
            Near(g.DistanceOutside(new Vector3(100f, 0f, 0f)), 80f, "distance to the nearer part (bar edge at 20)");
            Near(g.DistanceOutside(new Vector3(10f, 0f, 30f)), 0f, "inside a part is 0");

            Check(g.Overlaps(new Vector3(-10f, 0f, 14f), 5f), "a stroke 4 m above the bar touches it");
            Check(!g.Overlaps(new Vector3(-10f, 0f, 25f), 5f), "a stroke in the notch, 15 m from both, touches nothing");

            // Conservative contains-circle: wholly inside ONE part answers yes. A circle in the waist of two
            // overlapping circles is inside their union and inside neither, and conservatively answers no.
            Check(g.ContainsCircle(new Vector3(10f, 0f, 30f), 5f), "a circle inside the leg alone");
            TerritoryGround pair = Compound(9, Part(TerritoryShapeKind.Circle, 0f, 0f, 10f), Part(TerritoryShapeKind.Circle, 12f, 0f, 10f)).Ground;
            Check(pair.Contains(new Vector3(6f, 0f, 5f)) && pair.Contains(new Vector3(1f, 0f, 0f)) && pair.Contains(new Vector3(11f, 0f, 0f)),
                "the waist circle's extremes are all in the union");
            Check(!pair.ContainsCircle(new Vector3(6f, 0f, 0f), 5f), "yet no single part holds it: conservatively not contained");

            // Seams are not edges: the bar's top edge inside the leg is a seam; outside it, an outer edge.
            Check(!g.OnOuterEdge(0, new Vector3(10f, 0f, 10f)), "the bar's top edge under the leg is a seam");
            Check(g.OnOuterEdge(0, new Vector3(-10f, 0f, 10f)), "the bar's top edge in the notch is outer");
            Check(!g.OnOuterEdge(1, new Vector3(10f, 0f, 0f)), "the leg's bottom edge inside the bar is a seam");
            Check(g.OnOuterEdge(1, new Vector3(0f, 0f, 30f)), "the leg's left edge above the bar is outer");

            g.Bounds(out float minX, out float minZ, out float maxX, out float maxZ);
            Near(minX, -20f, "bounds min x");
            Near(maxX, 215f, "bounds max x reach the ring");
            Near(minZ, -15f, "bounds min z (the ring)");
            Near(maxZ, 40f, "bounds max z (the leg)");
            Near(g.MaxHalfExtent, 20f, "largest half extent");
            Check(g.Describe().StartsWith("3 parts: "), $"described by its parts ({g.Describe()})");
        }

        /// <summary>A brute-force reference: cell centres of a fine grid over the bounds.</summary>
        private static float GridArea(TerritoryGround g, int n)
        {
            g.Bounds(out float minX, out float minZ, out float maxX, out float maxZ);
            double dx = (maxX - minX) / (double)n, dz = (maxZ - minZ) / (double)n;
            long hits = 0;
            for (int iz = 0; iz < n; iz++)
            {
                float z = (float)(minZ + (iz + 0.5) * dz);
                for (int ix = 0; ix < n; ix++)
                    if (g.Contains(new Vector3((float)(minX + (ix + 0.5) * dx), 0f, z))) hits++;
            }
            return (float)(hits * dx * dz);
        }

        private static void GroundArea()
        {
            var c = Part(TerritoryShapeKind.Circle, 0f, 0f, 30f);
            float circle = Mathf.PI * 900f;
            Near(Compound(1, c, c).Ground.MeasureArea() / circle, 1f, "a circle combined with itself is one circle", 1e-3f);
            Near(Compound(1, c, Part(TerritoryShapeKind.Circle, 500f, 0f, 30f)).Ground.MeasureArea() / (2f * circle), 1f, "two apart are the sum", 1e-3f);

            var bar = Part(TerritoryShapeKind.Rectangle, 0f, 0f, 20f, 10f);
            var leg = Part(TerritoryShapeKind.Rectangle, 10f, 20f, 10f, 20f);
            Near(Compound(1, bar, leg).Ground.MeasureArea() / (800f + 800f - 200f), 1f, "the L: 800 + 800 - the 200 m2 corner", 1e-3f);

            var square = Part(TerritoryShapeKind.Square, 0f, 0f, 50f);
            Near(Compound(1, square, Part(TerritoryShapeKind.Circle, 5f, -5f, 20f)).Ground.MeasureArea() / 10000f, 1f, "a circle inside a square adds nothing", 1e-3f);

            // Rotated boxes and circles in a tangle, against a 1200 x 1200 grid.
            TerritoryGround tangle = Compound(1,
                Part(TerritoryShapeKind.Rectangle, 0f, 0f, 40f, 12f, 33f),
                Part(TerritoryShapeKind.Rectangle, 20f, 15f, 30f, 8f, 118f),
                Part(TerritoryShapeKind.Circle, -25f, 10f, 18f),
                Part(TerritoryShapeKind.Square, 35f, -20f, 14f, 14f, 71f)).Ground;
            float measured = tangle.MeasureArea();
            float reference = GridArea(tangle, 1200);
            Near(measured / reference, 1f, $"a tangle of rotated parts ({measured:0} vs grid {reference:0} m2)", 3e-3f);

            // Same inputs, same answer: every machine has to agree on precedence.
            Check(tangle.MeasureArea() == measured, "measuring twice gives the identical number");
        }

        private static void GroundAreaFarApart()
        {
            // Combining takes any two territories, however far apart. Rows spread over the whole ground's height
            // spent nearly all of themselves on the gaps: three circles of 10 m radius 6 km apart measured 18% too
            // big, two of 5 m radius 24% too big at 10 km and 25% too small at 20 km, and two of 1 m radius 5 km
            // apart measured nothing at all. Each run of parts that overlap in Z now has rows of its own.
            float r10 = Mathf.PI * 100f, r5 = Mathf.PI * 25f, r1 = Mathf.PI;
            Territory safe = Compound(1, Part(TerritoryShapeKind.Circle, 0f, 0f, 10f),
                Part(TerritoryShapeKind.Circle, 0f, 6000f, 10f), Part(TerritoryShapeKind.Circle, 0f, 12000f, 10f));
            Near(safe.Ground.MeasureArea() / (3f * r10), 1f, "three circles of 10 m radius, 6 km apart", 1e-3f);
            Near(Compound(1, Part(TerritoryShapeKind.Circle, 0f, 0f, 5f), Part(TerritoryShapeKind.Circle, 37.3f, 10000f, 5f)).Ground.MeasureArea() / (2f * r5), 1f,
                "two of 5 m radius, 10 km apart", 1e-3f);
            Near(Compound(1, Part(TerritoryShapeKind.Circle, 0f, -10000f, 5f), Part(TerritoryShapeKind.Circle, 0f, 10000f, 5f)).Ground.MeasureArea() / (2f * r5), 1f,
                "and 20 km apart", 1e-3f);
            Near(Compound(1, Part(TerritoryShapeKind.Circle, 0f, 0f, 1f), Part(TerritoryShapeKind.Circle, 0f, 5000f, 1f)).Ground.MeasureArea() / (2f * r1), 1f,
                "two of 1 m radius, 5 km apart, are not nothing", 1e-3f);

            // The Territory tool's starting size (InitialRadius, 20 m) at every distance from 1 to 19 km: off by up to 8.5%.
            float worst = 0f, worstGap = 0f;
            for (float gap = 1000f; gap <= 19000f; gap += 250f)
            {
                float error = Mathf.Abs(Compound(1, Part(TerritoryShapeKind.Circle, 0f, 0f, 20f), Part(TerritoryShapeKind.Circle, 0f, gap, 20f)).Ground.MeasureArea()
                                        / (2f * Mathf.PI * 400f) - 1f);
                if (error > worst) { worst = error; worstGap = gap; }
            }
            Check(worst < 1e-3f, $"two of 20 m radius, 1 to 19 km apart: worst error {worst * 100f:0.###}% (at {worstGap:0} m)");

            // Rotated boxes kilometres apart are exactly the sum of their areas, 4 x halfX x halfZ each.
            TerritoryGround boxes = Compound(1,
                Part(TerritoryShapeKind.Rectangle, 0f, 0f, 12f, 5f, 33f),
                Part(TerritoryShapeKind.Square, -400f, 9000f, 7f, 7f, 71f),
                Part(TerritoryShapeKind.Rectangle, 250f, -7000f, 30f, 2f, 118f)).Ground;
            Near(boxes.MeasureArea() / (240f + 196f + 240f), 1f, "rotated boxes kilometres apart are the sum of their areas", 1e-3f);

            // Parts that overlap in Z share one run, and within it overlapping ground still counts once: the tangle
            // (measured against a fine grid above) and a circle 15 km away are exactly the two added.
            TerritoryShape[] tangle =
            {
                Part(TerritoryShapeKind.Rectangle, 0f, 0f, 40f, 12f, 33f),
                Part(TerritoryShapeKind.Rectangle, 20f, 15f, 30f, 8f, 118f),
                Part(TerritoryShapeKind.Circle, -25f, 10f, 18f),
                Part(TerritoryShapeKind.Square, 35f, -20f, 14f, 14f, 71f)
            };
            float tangleArea = Compound(1, tangle[0], tangle.Skip(1).ToArray()).Ground.MeasureArea();
            float withFar = Compound(1, tangle[0], tangle.Skip(1).Append(Part(TerritoryShapeKind.Circle, 60f, 15000f, 20f)).ToArray()).Ground.MeasureArea();
            Near(withFar / (tangleArea + Mathf.PI * 400f), 1f, "a far part adds its own area to the tangle's, overlaps still counted once", 1e-3f);

            // A long part joins the parts on either side of it into one run, though they share no Z with each other:
            // a circle at each end of a 400 m strip that runs well to the side of both.
            TerritoryGround bridged = Compound(1,
                Part(TerritoryShapeKind.Circle, 0f, 0f, 10f),
                Part(TerritoryShapeKind.Rectangle, 500f, 200f, 5f, 200f),
                Part(TerritoryShapeKind.Circle, 0f, 390f, 10f),
                Part(TerritoryShapeKind.Circle, 0f, 8000f, 10f)).Ground;
            Near(bridged.MeasureArea() / (3f * r10 + 4000f), 1f, "a strip bridging two circles, and a far one on its own", 1e-3f);

            // Every machine must agree on precedence, and runtimes sort ties differently: the same parts in another
            // order, two of them starting at the same Z, give the identical number.
            TerritoryShape p1 = Part(TerritoryShapeKind.Circle, 0f, 0f, 10f), p2 = Part(TerritoryShapeKind.Circle, 300f, 0f, 10f);
            TerritoryShape p3 = Part(TerritoryShapeKind.Rectangle, 150f, 4000f, 20f, 8f, 40f), p4 = Part(TerritoryShapeKind.Circle, 150f, 5f, 12f);
            Check(Compound(1, p1, p2, p3, p4).Ground.MeasureArea() == Compound(1, p3, p2, p4, p1).Ground.MeasureArea(),
                "the order of the parts - and so of a tie in the sort - does not change the number");

            // What the error did: a market of 18 m radius (1,018 m2) over the first of those three circles (943 m2 in
            // all, so the smaller) governed the spot they share, because the three measured 1,112 m2.
            Territory market = Compound(2, Part(TerritoryShapeKind.Circle, 15f, 0f, 18f));
            TerritoryIndex.Set(new[] { market, safe });
            Check(TerritoryIndex.GoverningAt(new Vector3(5f, 0f, 0f))?.Id == 1, "the truly smaller territory governs where the two overlap");
            Check(TerritoryIndex.GoverningAt(new Vector3(25f, 0f, 0f))?.Id == 2, "and the market the rest of its own ground");
        }

        private static void IndexCompound()
        {
            // Two circles of 6,000 m2 each, drawn mostly over each other: the sum (12,000) is bigger than the
            // 10,000 m2 kingdom, the union (about 7,300) is smaller - so the market governs where they overlap.
            float r = Mathf.Sqrt(6000f / Mathf.PI);
            Territory market = Compound(2, Part(TerritoryShapeKind.Circle, 0f, 0f, r), Part(TerritoryShapeKind.Circle, 15f, 0f, r));
            Territory kingdom = Compound(1, Part(TerritoryShapeKind.Square, 0f, 0f, 50f));
            kingdom.Flags = TerritoryFlags.BuildProtect;
            float union = market.Ground.MeasureArea();
            Check(union < 10000f && 12000f > 10000f, $"the market's union ({union:0} m2) is smaller than the kingdom, its sum is not");

            TerritoryIndex.Set(new[] { kingdom, market });
            Check(TerritoryIndex.All[0].Id == 2, "the smaller union comes first in precedence");
            Check(TerritoryIndex.GoverningAt(new Vector3(0f, 0f, 0f))?.Id == 2, "the market governs its first part");
            Check(TerritoryIndex.GoverningAt(new Vector3(15f + r - 1f, 0f, 0f))?.Id == 2, "and its second part");
            Check(TerritoryIndex.GoverningAt(new Vector3(-45f, 0f, 45f))?.Id == 1, "the kingdom governs outside the market");
            Check(TerritoryIndex.Denying(new Vector3(15f + r - 3f, 0f, 0f), 1f, TerritoryFlags.BuildProtect, 999, false) == null,
                "a stroke inside the market's second part is open (the part holds it whole)");

            // East of both: the market's SECOND part reaches x = 15 + r (58.7), past the kingdom's edge at 50.
            Territory near = TerritoryIndex.Nearest(new Vector3(200f, 0f, 0f), out float d);
            Check(near?.Id == 2 && Mathf.Abs(d - (200f - 15f - r)) < 1e-3f, $"nearest is measured to the nearest part of any territory (got #{near?.Id} at {d:0.0} m)");
        }

        private static void EditsCombine()
        {
            Territory into = Compound(1, Part(TerritoryShapeKind.Circle, 0f, 0f, 20f), Part(TerritoryShapeKind.Square, 30f, 0f, 10f));
            into.Name = "Keep";
            into.Flags = TerritoryFlags.BuildProtect | TerritoryFlags.Announce;
            into.OwnerId = 10;
            into.OwnerName = "Owner";
            into.Members.Add(new TerritoryMember(11, "A"));

            Territory from = Compound(2, Part(TerritoryShapeKind.Rectangle, 0f, 50f, 5f, 25f, 90f), Part(TerritoryShapeKind.Circle, 0f, 90f, 12f));
            from.Name = "Courtyard";
            from.Flags = TerritoryFlags.Sanctuary;
            from.OwnerId = 20;
            from.OwnerName = "Other";
            from.Members.Add(new TerritoryMember(11, "A"));   // already a member of the keep
            from.Members.Add(new TerritoryMember(21, "B"));
            from.Members.Add(new TerritoryMember(10, "Owner"));   // the keep's owner

            string err = TerritoryEdits.Combine(into, from, 16, out int joined);
            Check(err == null, "combined: " + err);
            Check(into.Id == 1 && into.Name == "Keep" && into.Flags == (TerritoryFlags.BuildProtect | TerritoryFlags.Announce) && into.OwnerId == 10,
                "the target keeps its id, name, rules and owner");
            Check(into.PartCount == 4, "2 + 2 parts");
            Check(into.GetPart(2).SameAs(from.Shape) && into.GetPart(3).SameAs(from.GetPart(1)), "the other's parts follow the target's, in order");
            Check(joined == 2 && into.Members.Select(m => m.Id).OrderBy(x => x).SequenceEqual(new long[] { 11, 20, 21 }),
                "the other's owner and new members joined, nobody twice, the owner not as a member");
            Check(into.Ground.Contains(new Vector3(0f, 0f, 90f)), "the combined ground holds the other's far part");

            Check(TerritoryEdits.Combine(into, into, 16, out _) != null, "a territory cannot combine with itself");

            Territory big = Compound(3, Part(TerritoryShapeKind.Circle, 0f, 0f, 5f));
            Territory small = Compound(4, Part(TerritoryShapeKind.Circle, 50f, 0f, 5f), Part(TerritoryShapeKind.Circle, 60f, 0f, 5f));
            int before = big.PartCount;
            Check(TerritoryEdits.Combine(big, small, 2, out _) != null && big.PartCount == before, "over MaxParts: refused and untouched");

            Territory crowded = Compound(5, Part(TerritoryShapeKind.Circle, 0f, 0f, 5f));
            for (int i = 0; i < Territory.MaxMembers; i++) crowded.Members.Add(new TerritoryMember(1000 + i, "m" + i));
            Territory newcomer = Compound(6, Part(TerritoryShapeKind.Circle, 50f, 0f, 5f));
            newcomer.OwnerId = 77;
            Check(TerritoryEdits.Combine(crowded, newcomer, 16, out _) != null && crowded.PartCount == 1 && crowded.Members.Count == Territory.MaxMembers,
                "over MaxMembers: refused and untouched");
        }

        private static void EditsParts()
        {
            var a = Part(TerritoryShapeKind.Circle, 0f, 0f, 10f);
            var b = Part(TerritoryShapeKind.Square, 30f, 0f, 10f, 10f, 45f);
            var c = Part(TerritoryShapeKind.Rectangle, 60f, 0f, 5f, 20f, 10f);
            Territory t = Compound(1, a);
            t.Y = 42f;

            Check(TerritoryEdits.AddPart(t, b, 3) == null && TerritoryEdits.AddPart(t, c, 3) == null && t.PartCount == 3, "two parts added");
            Check(TerritoryEdits.AddPart(t, a, 3) != null && t.PartCount == 3, "a fourth over MaxParts 3 is refused");

            var moved = Part(TerritoryShapeKind.Circle, 0f, 100f, 12f);
            Check(TerritoryEdits.ReshapePart(t, 1, moved) == null && t.GetPart(1).SameAs(moved) && t.GetPart(0).SameAs(a), "reshaping part 2 leaves part 1");
            Check(TerritoryEdits.ReshapePart(t, 0, b) == null && t.GetPart(0).SameAs(b) && t.Kind == TerritoryShapeKind.Square && t.Y == 42f,
                "reshaping part 1 writes the record's own fields and keeps its height");
            Check(TerritoryEdits.ReshapePart(t, 3, a) != null, "there is no part 4");

            Check(TerritoryEdits.RemovePart(t, 0) == null && t.PartCount == 2 && t.GetPart(0).SameAs(moved), "removing part 1 moves part 2 into its place");
            Check(TerritoryEdits.RemovePart(t, 1) == null && t.PartCount == 1, "removing the last extra part");
            Check(TerritoryEdits.RemovePart(t, 0) != null && t.PartCount == 1, "the only part cannot be removed");

            Territory keep = Compound(9, a, b, c);
            keep.Name = "Harbour";
            keep.Flags = TerritoryFlags.HarvestProtect;
            keep.OwnerId = 5;
            keep.Members.Add(new TerritoryMember(6, "M"));
            keep.Revision = 12;
            Territory split = TerritoryEdits.Split(keep, 1, out string err);
            Check(split != null && err == null, "split off part 2: " + err);
            Check(split.Id == 0 && split.PartCount == 1 && split.Shape.SameAs(b), "the new territory is that part alone, not yet numbered");
            Check(split.Flags == TerritoryFlags.HarvestProtect && split.OwnerId == 5 && split.Members.Count == 1 && split.Name == "Harbour (split)" && split.Revision == 0,
                "it keeps the rules, owner and members under a new name");
            Check(keep.PartCount == 2 && keep.GetPart(0).SameAs(a) && keep.GetPart(1).SameAs(c), "the original loses exactly that part");
            split.Members.Add(new TerritoryMember(7, "N"));
            Check(keep.Members.Count == 1, "and does not share its member list with the split");

            Territory lone = Compound(10, a);
            Check(TerritoryEdits.Split(lone, 0, out string e2) == null && e2 != null && lone.PartCount == 1, "a single part cannot be split off");
        }

        private static void SanitizeParts()
        {
            Territory t = Compound(1, Part(TerritoryShapeKind.Circle, 0f, 0f, 20f),
                new TerritoryShape(TerritoryShapeKind.Rectangle, 50f, 0f, 99999f, 0.1f, 400f),
                new TerritoryShape((TerritoryShapeKind)77, 80f, 0f, 5f, 9f, 30f));
            Check(t.Sanitize(600f), "sanitises");
            TerritoryShape big = t.GetPart(1), odd = t.GetPart(2);
            Check(big.HalfX == 600f && big.HalfZ == 1f, "an extra part's extents are clamped to [1, max]");
            Near(big.Yaw, 40f, "its yaw wrapped");
            Check(odd.Kind == TerritoryShapeKind.Circle && odd.HalfX == 5f && odd.HalfZ == 5f && odd.Yaw == 0f, "an unknown shape reads as a circle");

            Territory bad = Compound(2, Part(TerritoryShapeKind.Circle, 0f, 0f, 20f), new TerritoryShape(TerritoryShapeKind.Square, float.NaN, 0f, 5f, 5f, 0f));
            Check(!bad.Sanitize(600f), "a part at NaN spoils the whole record");
            Territory inf = Compound(3, Part(TerritoryShapeKind.Circle, 0f, 0f, 20f), new TerritoryShape(TerritoryShapeKind.Square, 0f, float.PositiveInfinity, 5f, 5f, 0f));
            Check(!inf.Sanitize(600f), "and so does one at infinity");

            Territory orig = Compound(4, Part(TerritoryShapeKind.Circle, 0f, 0f, 20f), Part(TerritoryShapeKind.Circle, 50f, 0f, 20f));
            Territory copy = orig.Clone();
            copy.ExtraParts.Add(Part(TerritoryShapeKind.Circle, 90f, 0f, 5f));
            copy.SetPart(1, Part(TerritoryShapeKind.Square, 1f, 1f, 1f));
            Check(orig.PartCount == 2 && orig.GetPart(1).Kind == TerritoryShapeKind.Circle, "a clone's parts are its own");

            var target = new Territory();
            target.CopyGeometryFrom(orig);
            Check(target.PartCount == 2 && target.GetPart(1).SameAs(orig.GetPart(1)), "copying geometry copies every part");
            target.ExtraParts.Clear();
            Check(orig.PartCount == 2, "and not by reference");
        }

        private static Territory SampleCompound()
        {
            Territory t = Sample();
            t.ExtraParts.Add(Part(TerritoryShapeKind.Circle, 1300f, -950f, 25.5f));
            t.ExtraParts.Add(Part(TerritoryShapeKind.Rectangle, 1200f, -1000f, 7.25f, 31f, 271.5f));
            return t;
        }

        private static void WireParts()
        {
            Territory t = SampleCompound();
            var pkg = new ZPackage();
            t.Write(pkg);
            Territory back = Territory.Read(pkg.Transmit());
            SameTerritory(t, back, "compound record");
            Check(back.PartCount == 3 && back.GetPart(2).Yaw == 271.5f && back.GetPart(1).HalfX == 25.5f, "both extra parts, exactly");

            ZPackage snap = Protocol.WriteSnapshot(9L, 4, true, false, new[] { t, Sample() }, null);
            Protocol.Snapshot s = Protocol.ReadSnapshot(snap.Transmit());
            Check(s.Territories.Count == 2 && s.Territories[0].PartCount == 3 && s.Territories[1].PartCount == 1, "a snapshot carries each territory's own parts");

            // What 0.1.x sent: record version 1, no parts count at all. Built from a real record by dropping the
            // trailing parts count and turning the version back to 1.
            var outer = new ZPackage();
            Sample().Write(outer);
            byte[] body = outer.Transmit().ReadPackage().GetArray();
            byte[] v1 = new byte[body.Length - 4];
            Array.Copy(body, v1, v1.Length);
            BitConverter.GetBytes(1).CopyTo(v1, 0);
            var wrapped = new ZPackage();
            wrapped.Write(new ZPackage(v1));
            wrapped.Write(12345);   // whatever follows the record must not be touched
            ZPackage rx = wrapped.Transmit();
            Territory old = Territory.Read(rx);
            SameTerritory(Sample(), old, "version 1 record");
            Check(old.PartCount == 1 && rx.ReadInt() == 12345, "reads with no extra parts and leaves the stream after it");

            ZPackage req = Protocol.WriteRequest(21, RequestOp.Combine, null, 5, otherId: 9, part: 0);
            Protocol.Request r = Protocol.ReadRequest(req.Transmit());
            Check(r.Protocol == Protocol.Version && r.Op == RequestOp.Combine && r.TargetId == 5 && r.OtherId == 9 && r.Record == null, "a combine request");

            ZPackage split = Protocol.WriteRequest(22, RequestOp.SplitPart, null, 5, part: 3, baseRevision: 17);
            Protocol.Request sp = Protocol.ReadRequest(split.Transmit());
            Check(sp.Op == RequestOp.SplitPart && sp.Part == 3 && sp.OtherId == 0 && sp.BaseRevision == 17,
                "a split request names its part and the revision it was counted in");
            Check(r.BaseRevision == 0, "a request that does not say which revision it saw carries 0 (do not check)");

            ZPackage add = Protocol.WriteRequest(23, RequestOp.AddPart, SampleCompound(), 5);
            Protocol.Request ad = Protocol.ReadRequest(add.Transmit());
            Check(ad.Record != null && ad.Record.PartCount == 3 && ad.Part == 0, "a record travels in front of the part fields");

            // A protocol 1 sender (0.1.x) wrote no OtherId/Part: reshape then means the first part.
            var p1 = new ZPackage();
            p1.Write(1);
            p1.Write(24);
            p1.Write((int)RequestOp.Reshape);
            p1.Write(5);
            p1.Write(false);
            Protocol.Request old1 = Protocol.ReadRequest(p1.Transmit());
            Check(old1.Protocol == 1 && old1.Op == RequestOp.Reshape && old1.TargetId == 5 && old1.OtherId == 0 && old1.Part == 0,
                "a protocol 1 request reads, meaning the first part");
        }
    }
}
