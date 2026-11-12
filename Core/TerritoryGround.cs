using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace TerroritySmellatory
{
    // A TERRITORY'S WHOLE GROUND, READ AS ONE AREA.
    //
    // From 0.2.0 a territory can be several pieces of ground: the part it was first marked with, plus any
    // added to it or joined from another territory (Core/TerritoryEdits.cs). That is how odd shapes are made:
    // an L from two rectangles, a keep with a round courtyard, a harbour and the road to it. Every question
    // the rules, the borders and the map ask about where a territory is comes through here, so a combined
    // territory is ONE area everywhere: inside any part is inside the territory, and where two parts overlap
    // or meet there is no edge at all.
    //
    // A VIEW, NOT A COPY. It holds the first part by value and the territory's own list of the others by
    // reference, so making one costs nothing: the ward check behind every placement-ghost frame asks it
    // without allocating. The parts are TerritoryShape structs, immutable, so none can change under it.
    public readonly struct TerritoryGround
    {
        private readonly TerritoryShape _first;
        private readonly List<TerritoryShape> _more;

        public TerritoryGround(TerritoryShape first, List<TerritoryShape> more)
        {
            _first = first;
            _more = more;
        }

        public int Count => 1 + (_more?.Count ?? 0);

        public bool IsCompound => _more != null && _more.Count > 0;

        /// <summary>Part <paramref name="i"/>: 0 is the part the territory was first marked with.</summary>
        public TerritoryShape this[int i] => i == 0 ? _first : _more[i - 1];

        public bool Contains(Vector3 p) => PartAt(p) >= 0;

        /// <summary>The first part holding this point, or -1 when it is outside the whole ground.</summary>
        public int PartAt(Vector3 p)
        {
            if (_first.Contains(p)) return 0;
            if (_more != null)
                for (int i = 0; i < _more.Count; i++)
                    if (_more[i].Contains(p)) return i + 1;
            return -1;
        }

        /// <summary>How far outside the whole ground the point is; 0 inside any part. Exact: the nearest part's distance.</summary>
        public float DistanceOutside(Vector3 p)
        {
            float best = _first.DistanceOutside(p);
            if (_more != null)
                for (int i = 0; i < _more.Count && best > 0f; i++)
                    best = Mathf.Min(best, _more[i].DistanceOutside(p));
            return best;
        }

        /// <summary>Does a circle of <paramref name="radius"/> around the point touch the ground? Exact: does it touch any part.</summary>
        public bool Overlaps(Vector3 p, float radius)
        {
            if (_first.Overlaps(p, radius)) return true;
            if (_more != null)
                for (int i = 0; i < _more.Count; i++)
                    if (_more[i].Overlaps(p, radius)) return true;
            return false;
        }

        /// <summary>
        /// Is the whole circle inside the ground? CONSERVATIVE: yes only when one part holds all of it. A circle
        /// straddling the seam of two overlapping parts may be wholly inside and still answer no. Both callers
        /// can take that: TerritoryIndex.Denying then keeps asking the territories beneath - the "touches" rule
        /// wards use, which errs toward refusing - and Borders merely looks for an edge that is not there.
        /// </summary>
        public bool ContainsCircle(Vector3 p, float radius)
        {
            if (_first.ContainsCircle(p, radius)) return true;
            if (_more != null)
                for (int i = 0; i < _more.Count; i++)
                    if (_more[i].ContainsCircle(p, radius)) return true;
            return false;
        }

        /// <summary>
        /// Is this point, on part <paramref name="part"/>'s outline, on the OUTER edge of the ground - inside no
        /// other part? What decides which stretches of each part's border are drawn: a part's edge that runs
        /// inside another part is a seam, and a seam is not a border.
        /// </summary>
        public bool OnOuterEdge(int part, Vector3 p)
        {
            if (part != 0 && _first.Contains(p)) return false;
            if (_more != null)
                for (int i = 0; i < _more.Count; i++)
                    if (i + 1 != part && _more[i].Contains(p)) return false;
            return true;
        }

        /// <summary>The largest half-extent (radius, half-width or half-depth) of any part.</summary>
        public float MaxHalfExtent
        {
            get
            {
                float max = Mathf.Max(_first.HalfX, _first.HalfZ);
                if (_more != null)
                    for (int i = 0; i < _more.Count; i++)
                        max = Mathf.Max(max, Mathf.Max(_more[i].HalfX, _more[i].HalfZ));
                return max;
            }
        }

        /// <summary>The smallest world-axis box around every part.</summary>
        public void Bounds(out float minX, out float minZ, out float maxX, out float maxZ)
        {
            _first.Bounds(out minX, out minZ, out maxX, out maxZ);
            if (_more == null) return;
            for (int i = 0; i < _more.Count; i++)
            {
                _more[i].Bounds(out float x0, out float z0, out float x1, out float z1);
                minX = Mathf.Min(minX, x0);
                minZ = Mathf.Min(minZ, z0);
                maxX = Mathf.Max(maxX, x1);
                maxZ = Mathf.Max(maxZ, z1);
            }
        }

        // ─────────────────────────────────────────────────────────────
        // AREA - what "the smaller territory governs" compares
        // ─────────────────────────────────────────────────────────────

        /// <summary>Rows the ground is measured in. Each row's covered length is exact, so this only sets the row spacing.</summary>
        internal const int AreaRows = 1024;

        /// <summary>
        /// The area of the whole ground, with overlapping parts counted once. Summing the parts would not do: two
        /// parts drawn over each other would make a territory look twice its size, and it would then lose to a
        /// genuinely bigger neighbour where they overlap.
        /// </summary>
        /// <remarks>
        /// Exact for one part. For several, the parts are gathered into runs whose Z ranges overlap, each run is
        /// cut into its own <see cref="AreaRows"/> rows, and each row's covered length is measured exactly - where
        /// the row crosses each part of the run, those spans merged - so the only error is the row spacing's:
        /// well under a tenth of a percent for any real territory. PER RUN, not over the whole ground's bounds:
        /// combining takes any two territories, however far apart, and rows spread over the gaps between them
        /// cross a small far part a handful of times or not at all. Three circles of 10 m radius 6 km apart came
        /// out 18% too big that way - enough to hand a shared spot to the wrong territory - and two of 1 m radius
        /// 5 km apart came out as nothing. Two runs share no Z, so no ground in one can overlap ground in the other.
        ///
        /// It is plain arithmetic on the same numbers, so the server and every client get the same answer, and
        /// they must: precedence is decided on each of them. That includes how the sort below breaks ties, which
        /// differs between runtimes: parts that start at the same Z always land in the same run, the run's rows
        /// depend only on its lowest and highest Z, and a row's spans merge to the same lengths in any order.
        /// It costs a few hundred microseconds for a territory of many parts - each part is still tried against
        /// AreaRows rows, as before - so it is measured when the territory list changes (TerritoryIndex.Set),
        /// never per frame.
        /// </remarks>
        public float MeasureArea()
        {
            if (!IsCompound) return _first.IsEmpty ? 0f : _first.Area;

            int n = Count, used = 0;
            var corners = new Vector3[n * 4];
            var lowZ = new float[n];     // the sort keys, in step with order: each part's lowest Z
            var highZ = new float[n];    // by part number
            var order = new int[n];
            for (int i = 0; i < n; i++)
            {
                TerritoryShape s = this[i];
                // No ground, so no rows - and a box of no depth still has a Z extent that must not join two runs.
                if (s.IsEmpty) continue;
                s.Bounds(out _, out lowZ[used], out _, out highZ[i]);
                order[used++] = i;
                if (s.Kind == TerritoryShapeKind.Circle) continue;
                for (int k = 0; k < 4; k++) corners[i * 4 + k] = s.Corner(k);
            }
            Array.Sort(lowZ, order, 0, used);

            var members = new int[n];
            var from = new float[n];
            var to = new float[n];
            double total = 0.0;
            for (int at = 0; at < used;)
            {
                // One run: from the lowest part not yet measured, taking every part that starts at or below the
                // highest Z reached so far - so a long part bridges the parts on either side of it.
                float lo = lowZ[at], hi = highZ[order[at]];
                int count = 0;
                while (at < used && (count == 0 || lowZ[at] <= hi))
                {
                    members[count++] = order[at];
                    hi = Mathf.Max(hi, highZ[order[at]]);
                    at++;
                }
                float height = hi - lo;
                if (!(height > 0f)) continue;

                float dz = height / AreaRows;
                for (int row = 0; row < AreaRows; row++)
                {
                    float z = lo + (row + 0.5f) * dz;
                    int spans = 0;
                    for (int m = 0; m < count; m++)
                    {
                        int i = members[m];
                        if (!RowSpan(this[i], corners, i * 4, z, out float a, out float b)) continue;

                        // Insertion by start, so the merge below is one pass. A row crosses at most every part.
                        int j = spans++;
                        while (j > 0 && from[j - 1] > a)
                        {
                            from[j] = from[j - 1];
                            to[j] = to[j - 1];
                            j--;
                        }
                        from[j] = a;
                        to[j] = b;
                    }
                    if (spans == 0) continue;

                    float runFrom = from[0], runTo = to[0];
                    double covered = 0.0;
                    for (int k = 1; k < spans; k++)
                    {
                        if (from[k] <= runTo)
                        {
                            if (to[k] > runTo) runTo = to[k];
                            continue;
                        }
                        covered += runTo - runFrom;
                        runFrom = from[k];
                        runTo = to[k];
                    }
                    covered += runTo - runFrom;
                    total += covered * dz;
                }
            }
            return (float)total;
        }

        /// <summary>Where the line at world Z = <paramref name="z"/> crosses one part, as an X interval. False when it misses.</summary>
        private static bool RowSpan(TerritoryShape s, Vector3[] corners, int c0, float z, out float fromX, out float toX)
        {
            fromX = toX = 0f;
            if (s.IsEmpty) return false;

            if (s.Kind == TerritoryShapeKind.Circle)
            {
                float d = z - s.CenterZ;
                float h = s.HalfX * s.HalfX - d * d;
                if (h < 0f) return false;
                float half = Mathf.Sqrt(h);
                fromX = s.CenterX - half;
                toX = s.CenterX + half;
                return true;
            }

            // A box is convex: a row crosses its outline on two edges (or runs along one), and the span is
            // between the lowest and highest crossing.
            bool any = false;
            float lo = float.MaxValue, hi = float.MinValue;
            Vector3 a = corners[c0 + 3];
            for (int k = 0; k < 4; k++)
            {
                Vector3 b = corners[c0 + k];
                if ((a.z <= z && z <= b.z) || (b.z <= z && z <= a.z))
                {
                    if (a.z == b.z)
                    {
                        lo = Mathf.Min(lo, Mathf.Min(a.x, b.x));
                        hi = Mathf.Max(hi, Mathf.Max(a.x, b.x));
                    }
                    else
                    {
                        float x = a.x + (z - a.z) / (b.z - a.z) * (b.x - a.x);
                        lo = Mathf.Min(lo, x);
                        hi = Mathf.Max(hi, x);
                    }
                    any = true;
                }
                a = b;
            }
            if (!any) return false;
            fromX = lo;
            toX = hi;
            return true;
        }

        // ─────────────────────────────────────────────────────────────

        /// <summary>The ground in one short phrase: the shape itself, or its parts ("3 parts: 40 m circle + 20 x 60 m + 30 m square").</summary>
        public string Describe()
        {
            if (!IsCompound) return _first.Describe();

            int shown = Math.Min(Count, 3);
            var sb = new StringBuilder();
            sb.Append(Count).Append(" parts: ");
            for (int i = 0; i < shown; i++)
            {
                if (i > 0) sb.Append(" + ");
                sb.Append(this[i].Describe());
            }
            if (Count > shown) sb.Append(" + ").Append(Count - shown).Append(" more");
            return sb.ToString();
        }
    }
}
