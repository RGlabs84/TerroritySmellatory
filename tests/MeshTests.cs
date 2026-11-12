using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace TerroritySmellatory.Tests
{
    // 0.2.0: the map outline of a combined territory (Visuals/UnionOutlineGeometry.cs). The two clipping
    // primitives first; then whole meshes rasterised on a fine grid: every point of the ground drawn exactly
    // once and nothing outside it, the rim on the outer edge only, one part exactly as the single-part outline
    // draws it, and the worst case's time and vertex count.
    internal static partial class Program
    {
        private const float MeshRim = 2.5f;
        private const float MeshFeather = 1f;

        static partial void MeshGroups()
        {
            Run("union mesh: the primitives - half-plane clip, convex minus convex", MeshPrimitives);
            Run("union mesh: one part draws exactly as the single-part outline", MeshSinglePart);
            Run("union mesh: every point drawn once, nothing outside - circles, boxes, rotations", MeshCoverage);
            Run("union mesh: the rim on the outer edge only - seams, lens, shared edges, nesting", MeshTones);
            Run("union mesh: degenerate parts, valid indices, no NaN", MeshDegenerate);
            Run("union mesh: 16 overlapping 512-segment circles in time and under Unity's vertex cap", MeshWorstCase);
            Run("union mesh: too many or too big for the budget - built smaller, the rim kept its width", MeshBudget);
        }

        // ─────────────────────────────────────────────────────────────
        // FIXTURES
        // ─────────────────────────────────────────────────────────────

        private sealed class UnionMesh
        {
            public readonly List<float> Xs = new List<float>();
            public readonly List<float> Ys = new List<float>();
            public readonly List<float> Solid = new List<float>();
            public readonly List<float> Fill = new List<float>();
            public readonly List<int> Tris = new List<int>();
            public UnionOutlineGeometry.Part[] Parts;

            /// <summary>The scale the builder chose (under 1 when the parts' loops would pass its vertex budget).</summary>
            public float Scale = 1f;

            public static UnionMesh Of(params UnionOutlineGeometry.Part[] parts)
            {
                var m = new UnionMesh { Parts = parts };
                UnionOutlineGeometry.Build(parts, MeshRim, MeshFeather, m.Xs, m.Ys, m.Solid, m.Fill, m.Tris);
                m.Scale = UnionOutlineGeometry.LastScale;
                return m;
            }

            /// <summary>The single-part outline of one part, turned and moved as the union's frame has it, with its tones as weights.</summary>
            public static UnionMesh Single(UnionOutlineGeometry.Part p)
            {
                var m = new UnionMesh { Parts = new[] { p } };
                var xs = new List<float>();
                var ys = new List<float>();
                var tones = new List<byte>();
                OutlineGeometry.Build(p.Round, p.HalfW, p.HalfH, MeshRim, MeshFeather, xs, ys, tones, m.Tris);
                double a = p.Angle * Math.PI / 180.0, c = Math.Cos(a), s = Math.Sin(a);
                for (int i = 0; i < xs.Count; i++)
                {
                    m.Xs.Add((float)(p.X + c * xs[i] - s * ys[i]));
                    m.Ys.Add((float)(p.Y + s * xs[i] + c * ys[i]));
                    m.Solid.Add(tones[i] == OutlineGeometry.Solid ? 1f : 0f);
                    m.Fill.Add(tones[i] == OutlineGeometry.Fill ? 1f : 0f);
                }
                return m;
            }
        }

        private static UnionOutlineGeometry.Part RoundPart(float x, float y, float r) => new UnionOutlineGeometry.Part(true, x, y, r, r, 0f);

        private static UnionOutlineGeometry.Part BoxPart(float x, float y, float hw, float hh, float angle = 0f) =>
            new UnionOutlineGeometry.Part(false, x, y, hw, hh, angle);

        /// <summary>
        /// A part's loop as Build lays it, turned and moved into the common frame: 0 its outer feather loop F, -1
        /// its true shape (midway between loops 0 and 1). Worked out here from Build, not read from the union
        /// builder, so the checks do not take its word for where the parts are.
        /// </summary>
        /// <remarks>At a <paramref name="scale"/> under 1 the part is laid as the builder lays it then: Build at that scale, scaled back.</remarks>
        private static double[][] PartLoop(UnionOutlineGeometry.Part p, int loop, float scale = 1f)
        {
            var xs = new List<float>();
            var ys = new List<float>();
            float hw = p.HalfW * scale, hh = p.HalfH * scale;
            OutlineGeometry.Build(p.Round, hw, hh, MeshRim * scale, MeshFeather * scale, xs, ys, new List<byte>(), new List<int>());
            int n = OutlineGeometry.Segments(p.Round, hw, hh);
            if (xs.Count == 0) return new[] { new double[0], new double[0] };
            double a = p.Angle * Math.PI / 180.0, c = Math.Cos(a), s = Math.Sin(a);
            var X = new double[n];
            var Y = new double[n];
            for (int i = 0; i < n; i++)
            {
                double lx = (loop < 0 ? (xs[i] + xs[n + i]) * 0.5 : xs[loop * n + i]) / scale;
                double ly = (loop < 0 ? (ys[i] + ys[n + i]) * 0.5 : ys[loop * n + i]) / scale;
                X[i] = p.X + c * lx - s * ly;
                Y[i] = p.Y + s * lx + c * ly;
            }
            return new[] { X, Y };
        }

        /// <summary>Inside a convex polygon laid either way round (on its edge counts as inside).</summary>
        private static bool InConvex(double[][] poly, double x, double y)
        {
            double[] X = poly[0], Y = poly[1];
            int n = X.Length;
            if (n < 3) return false;
            bool pos = false, neg = false;
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                double cross = (X[j] - X[i]) * (y - Y[i]) - (Y[j] - Y[i]) * (x - X[i]);
                if (cross > 0) pos = true;
                else if (cross < 0) neg = true;
            }
            return !(pos && neg);
        }

        private static double EdgeDistance(double[][] poly, double x, double y)
        {
            double[] X = poly[0], Y = poly[1];
            double best = double.MaxValue;
            for (int i = 0; i < X.Length; i++)
            {
                int j = (i + 1) % X.Length;
                double ex = X[j] - X[i], ey = Y[j] - Y[i];
                double len = ex * ex + ey * ey;
                double t = len > 0 ? Math.Max(0, Math.Min(1, ((x - X[i]) * ex + (y - Y[i]) * ey) / len)) : 0;
                double dx = X[i] + t * ex - x, dy = Y[i] + t * ey - y;
                best = Math.Min(best, Math.Sqrt(dx * dx + dy * dy));
            }
            return best;
        }

        /// <summary>
        /// The mesh at one point: how many triangles hold it strictly (further than Hair inside all three edges)
        /// and loosely (no further than Hair outside any), and the weights in the last that holds it loosely.
        /// Brute force over every triangle, for the few probes the tone checks ask about.
        /// </summary>
        private static void Probe(UnionMesh m, double x, double y, out int strict, out int loose, out float solid, out float fill)
        {
            solid = fill = float.NaN;
            strict = loose = 0;
            for (int k = 0; k + 2 < m.Tris.Count; k += 3)
            {
                int held = Holds(m, k, x, y, out double wa, out double wb, out double wc);
                if (held == 0) continue;
                loose++;
                if (held == 2) strict++;
                int a = m.Tris[k], b = m.Tris[k + 1], c = m.Tris[k + 2];
                solid = (float)(wa * m.Solid[a] + wb * m.Solid[b] + wc * m.Solid[c]);
                fill = (float)(wa * m.Fill[a] + wb * m.Fill[b] + wc * m.Fill[c]);
            }
        }

        /// <summary>
        /// Where the cuts leave a T-junction (a vertex of one piece on the side of its neighbour), the float
        /// positions open a gap or an overlap about a millionth of a unit wide. A point that close to a triangle's
        /// edge is counted as neither clearly in nor clearly out.
        /// </summary>
        private const double Hair = 1e-4;

        /// <summary>0: outside the triangle; 1: within a hair of its edge; 2: clearly inside. With its barycentric weights.</summary>
        private static int Holds(UnionMesh m, int k, double x, double y, out double wa, out double wb, out double wc)
        {
            int a = m.Tris[k], b = m.Tris[k + 1], c = m.Tris[k + 2];
            double ax = m.Xs[a], ay = m.Ys[a], bx = m.Xs[b], by = m.Ys[b], cx = m.Xs[c], cy = m.Ys[c];
            double area = (bx - ax) * (cy - ay) - (cx - ax) * (by - ay);
            wa = wb = wc = 0;
            if (Math.Abs(area) < 1e-12) return 0;
            wb = ((x - ax) * (cy - ay) - (cx - ax) * (y - ay)) / area;
            wc = ((bx - ax) * (y - ay) - (x - ax) * (by - ay)) / area;
            wa = 1 - wb - wc;
            // Each weight times the height over its edge is the distance to that edge.
            double la = Math.Sqrt((cx - bx) * (cx - bx) + (cy - by) * (cy - by));
            double lb = Math.Sqrt((ax - cx) * (ax - cx) + (ay - cy) * (ay - cy));
            double lc = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
            double twice = Math.Abs(area);
            double da = wa * twice / la, db = wb * twice / lb, dc = wc * twice / lc;
            double least = Math.Min(da, Math.Min(db, dc));
            return least > Hair ? 2 : least >= -Hair ? 1 : 0;
        }

        /// <summary>A grid of sample points, offset by odd fractions of a step so they do not sit on the meshes' lines.</summary>
        private sealed class Raster
        {
            public readonly double X0, Y0, Step;
            public readonly int W, H;
            public readonly int[] Strict, Loose;
            public readonly float[] Solid, Fill;

            public Raster(double minX, double minY, double maxX, double maxY, double step)
                : this(minX - 2 * step + 0.3719 * step, minY - 2 * step + 0.6143 * step, step,
                    (int)((maxX - minX) / step) + 5, (int)((maxY - minY) / step) + 5)
            {
            }

            private Raster(double x0, double y0, double step, int w, int h)
            {
                X0 = x0;
                Y0 = y0;
                Step = step;
                W = w;
                H = h;
                Strict = new int[W * H];
                Loose = new int[W * H];
                Solid = new float[W * H];
                Fill = new float[W * H];
            }

            /// <summary>An empty raster on the same grid.</summary>
            public Raster Twin() => new Raster(X0, Y0, Step, W, H);

            public double X(int i) => X0 + i * Step;
            public double Y(int j) => Y0 + j * Step;

            /// <summary>Scanline: each triangle visits only the grid points inside it.</summary>
            public void Draw(UnionMesh m)
            {
                for (int k = 0; k + 2 < m.Tris.Count; k += 3)
                {
                    int a = m.Tris[k], b = m.Tris[k + 1], c = m.Tris[k + 2];
                    double[] px = { m.Xs[a], m.Xs[b], m.Xs[c] }, py = { m.Ys[a], m.Ys[b], m.Ys[c] };
                    double lowY = Math.Min(py[0], Math.Min(py[1], py[2])) - 2 * Hair, highY = Math.Max(py[0], Math.Max(py[1], py[2])) + 2 * Hair;
                    int j0 = Math.Max(0, (int)Math.Ceiling((lowY - Y0) / Step)), j1 = Math.Min(H - 1, (int)Math.Floor((highY - Y0) / Step));
                    for (int j = j0; j <= j1; j++)
                    {
                        double y = Y(j), lo = double.MaxValue, hi = double.MinValue;
                        double yc = Math.Max(Math.Min(y, highY - 2 * Hair), lowY + 2 * Hair);   // a row a hair beyond a tip reads the tip
                        for (int e = 0; e < 3; e++)
                        {
                            int f = (e + 1) % 3;
                            if ((py[e] <= yc && yc <= py[f]) || (py[f] <= yc && yc <= py[e]))
                            {
                                double x = py[e] == py[f] ? px[e] : px[e] + (yc - py[e]) / (py[f] - py[e]) * (px[f] - px[e]);
                                lo = Math.Min(lo, x);
                                hi = Math.Max(hi, x);
                                if (py[e] == py[f])
                                {
                                    lo = Math.Min(lo, px[f]);
                                    hi = Math.Max(hi, px[f]);
                                }
                            }
                        }
                        if (lo > hi) continue;
                        lo -= 2 * Hair;
                        hi += 2 * Hair;
                        int i0 = Math.Max(0, (int)Math.Ceiling((lo - X0) / Step)), i1 = Math.Min(W - 1, (int)Math.Floor((hi - X0) / Step));
                        for (int i = i0; i <= i1; i++)
                        {
                            int held = Holds(m, k, X(i), y, out double wa, out double wb, out double wc);
                            if (held == 0) continue;
                            int at = j * W + i;
                            Loose[at]++;
                            if (held == 2) Strict[at]++;
                            Solid[at] = (float)(wa * m.Solid[a] + wb * m.Solid[b] + wc * m.Solid[c]);
                            Fill[at] = (float)(wa * m.Fill[a] + wb * m.Fill[b] + wc * m.Fill[c]);
                        }
                    }
                }
            }
        }

        private static void Bounds(IEnumerable<double[][]> polys, out double minX, out double minY, out double maxX, out double maxY)
        {
            minX = minY = double.MaxValue;
            maxX = maxY = double.MinValue;
            foreach (double[][] p in polys)
            {
                if (p[0].Length == 0) continue;
                minX = Math.Min(minX, p[0].Min());
                maxX = Math.Max(maxX, p[0].Max());
                minY = Math.Min(minY, p[1].Min());
                maxY = Math.Max(maxY, p[1].Max());
            }
        }

        /// <summary>Valid indices, whole triangles, one weight pair per vertex, nothing NaN, weights in range.</summary>
        private static void MeshValid(UnionMesh m, string what)
        {
            int n = m.Xs.Count;
            Check(m.Ys.Count == n && m.Solid.Count == n && m.Fill.Count == n, $"{what}: one position and two weights per vertex");
            Check(m.Tris.Count % 3 == 0, $"{what}: whole triangles");
            Check(m.Tris.All(i => i >= 0 && i < n), $"{what}: every index names a vertex");
            bool finite = true, ranged = true;
            for (int i = 0; i < n; i++)
            {
                finite &= !float.IsNaN(m.Xs[i]) && !float.IsInfinity(m.Xs[i]) && !float.IsNaN(m.Ys[i]) && !float.IsInfinity(m.Ys[i]);
                float s = m.Solid[i], f = m.Fill[i];
                ranged &= s >= -1e-4f && f >= -1e-4f && s + f <= 1f + 1e-4f;
            }
            Check(finite, $"{what}: every position is finite");
            Check(ranged, $"{what}: every weight is in range (solid, fill >= 0, their sum <= 1)");
            Check(n < UnionOutlineGeometry.MaxVertices, $"{what}: under the vertex budget ({n})");
        }

        /// <summary>
        /// Rasterises the mesh and checks every sample point: inside the union of the parts' outer feather loops
        /// (and further than <paramref name="margin"/> from any of them) drawn exactly once; outside all of them,
        /// never. Returns the raster for tone checks.
        /// </summary>
        private static Raster MeshCovers(UnionMesh m, string what, double step, double margin = 0.02)
        {
            List<double[][]> fs = m.Parts.Select(p => PartLoop(p, 0, m.Scale)).Where(p => p[0].Length > 0).ToList();
            Bounds(fs, out double minX, out double minY, out double maxX, out double maxY);
            var r = new Raster(minX, minY, maxX, maxY, step);
            r.Draw(m);

            int inside = 0, twice = 0, missed = 0, stray = 0;
            string first = null;
            for (int j = 0; j < r.H; j++)
            {
                double y = r.Y(j);
                for (int i = 0; i < r.W; i++)
                {
                    double x = r.X(i);
                    if (fs.Any(f => EdgeDistance(f, x, y) < margin)) continue;
                    bool inUnion = fs.Any(f => InConvex(f, x, y));
                    int strict = r.Strict[j * r.W + i], loose = r.Loose[j * r.W + i];
                    if (inUnion) inside++;
                    if (inUnion && loose == 0) missed++;
                    else if (strict > 1) twice++;
                    else if (!inUnion && loose > 0) stray++;
                    else continue;
                    first = first ?? $"({x:0.####}, {y:0.####}) strictly {strict}x, loosely {loose}x, in union {inUnion}";
                }
            }
            Check(inside > 100, $"{what}: the grid samples the ground ({inside} points inside)");
            Check(missed == 0 && twice == 0 && stray == 0,
                $"{what}: every point inside drawn once, none outside (missed {missed}, twice {twice}, outside {stray}; first {first})");
            return r;
        }

        /// <summary>The mesh at a point: drawn once, with these weights.</summary>
        private static void Tone(UnionMesh m, double x, double y, float solid, float fill, string what, float eps = 2e-3f)
        {
            Probe(m, x, y, out int strict, out int loose, out float s, out float f);
            Check(strict <= 1 && loose >= 1 && Math.Abs(s - solid) <= eps && Math.Abs(f - fill) <= eps,
                $"{what} at ({x}, {y}): drawn {strict}x ({loose}x within a hair), solid {s:0.###} fill {f:0.###}, expected solid {solid} fill {fill}");
        }

        private static void Rim(UnionMesh m, double x, double y, string what) => Tone(m, x, y, 1f, 0f, $"{what}: rim");

        private static void Filled(UnionMesh m, double x, double y, string what) => Tone(m, x, y, 0f, 1f, $"{what}: fill");

        // ─────────────────────────────────────────────────────────────
        // THE PRIMITIVES
        // ─────────────────────────────────────────────────────────────

        private static double PolyArea(IList<double> xs, IList<double> ys)
        {
            double twice = 0;
            for (int i = 0; i < xs.Count; i++)
            {
                int j = (i + 1) % xs.Count;
                twice += xs[i] * ys[j] - xs[j] * ys[i];
            }
            return Math.Abs(twice) / 2;
        }

        private static void MeshPrimitives()
        {
            // A 10 x 10 square with a weight running 0 -> 1 left to right, cut at x = 3.
            var xs = new List<double> { 0, 10, 10, 0 };
            var ys = new List<double> { 0, 0, 10, 10 };
            var ws = new List<float> { 0f, 1f, 1f, 0f };
            Check(UnionOutlineGeometry.ClipForTests(xs, ys, ws, 1, 0, -3), "clip: something is left of x >= 3");
            Near((float)PolyArea(xs, ys), 70f, "clip: 7 x 10 is left", 1e-4f);
            Check(xs.All(x => x >= 3 - 1e-9), "clip: nothing left of the line");
            bool linear = true;
            for (int i = 0; i < xs.Count; i++) linear &= Math.Abs(ws[i] - xs[i] / 10) < 1e-6;
            Check(linear, "clip: the weight at every cut point is interpolated along its edge");

            xs = new List<double> { 0, 10, 10, 0 };
            ys = new List<double> { 0, 0, 10, 10 };
            ws = new List<float> { 0f, 0f, 0f, 0f };
            Check(UnionOutlineGeometry.ClipForTests(xs, ys, ws, 1, 0, 1) && xs.Count == 4, "clip: a polygon wholly inside comes back whole");
            Check(!UnionOutlineGeometry.ClipForTests(xs, ys, ws, -1, 0, -20), "clip: a polygon wholly outside comes back empty");

            // Walk (against a box, or any convex polygon): a 10 x 10 square minus a diamond at its centre
            // (radius 3, area 18).
            var qx = new List<double> { 0, 10, 10, 0 };
            var qy = new List<double> { 0, 0, 10, 10 };
            var dx = new List<double> { 5, 8, 5, 2 };
            var dy = new List<double> { 2, 5, 8, 5 };
            var outside = new List<List<double>>();
            double Left() => outside.Sum(o => PolyArea(Evens(o), Odds(o)));
            UnionOutlineGeometry.SubtractForTests(qx, qy, dx, dy, false, 0, 0, 0, outside);
            Near((float)Left(), 100f - 18f, "walk: the pieces outside are the square less the diamond", 1e-3f);
            Check(outside.Count == 4, $"walk: one piece per edge of the diamond ({outside.Count})");
            Check(outside.All(o => IsConvex(Evens(o), Odds(o))), "walk: every piece is convex");
            UnionOutlineGeometry.SubtractForTests(qx, qy, Enumerable.Reverse(dx).ToList(), Enumerable.Reverse(dy).ToList(), false, 0, 0, 0, outside);
            Near((float)Left(), 82f, "walk: a clockwise P gives the same", 1e-3f);

            // A 6 x 6 square over the diamond's right corner: 4 of its 36 lie inside.
            UnionOutlineGeometry.SubtractForTests(new List<double> { 6, 12, 12, 6 }, new List<double> { 2, 2, 8, 8 }, dx, dy, false, 0, 0, 0, outside);
            Near((float)Left(), 36f - 4f, "walk: square 36 less the 4 inside the diamond", 1e-3f);
            // Wholly outside: the square itself. Wholly inside: nothing.
            UnionOutlineGeometry.SubtractForTests(new List<double> { 20, 30, 30, 20 }, new List<double> { 2, 2, 8, 8 }, dx, dy, false, 0, 0, 0, outside);
            Check(outside.Count == 1 && Math.Abs(Left() - 60) < 1e-6, "walk: a square clear of the diamond comes back whole");
            UnionOutlineGeometry.SubtractForTests(new List<double> { 4.5, 5.5, 5.5, 4.5 }, new List<double> { 4.5, 4.5, 5.5, 5.5 }, dx, dy, false, 0, 0, 0, outside);
            Check(outside.Count == 0, "walk: a square inside the diamond leaves nothing");

            // Slice (against a circle's polygon): a regular 64-gon of radius 20 about (3, -2), laid as Build
            // lays a circle.
            const int n = 64;
            var cx = new List<double>();
            var cy = new List<double>();
            for (int i = 0; i < n; i++)
            {
                cx.Add(3 + 20 * Math.Cos(2 * Math.PI * i / n));
                cy.Add(-2 + 20 * Math.Sin(2 * Math.PI * i / n));
            }
            double polygon = PolyArea(cx, cy);

            // A square holding the whole polygon: what is left is the square less the polygon.
            UnionOutlineGeometry.SubtractForTests(new List<double> { -30, 40, 40, -30 }, new List<double> { -40, -40, 30, 30 }, cx, cy, true, 3, -2, 20, outside);
            Near((float)Left(), (float)(70 * 70 - polygon), "slice: a square around the polygon keeps the square less it", 1e-2f);
            Check(outside.All(o => IsConvex(Evens(o), Odds(o))), "slice: every piece is convex");
            Check(outside.Count <= 2 * n, $"slice: about one piece per sector ({outside.Count})");

            // A needle from inside the polygon to 60 units out: cut once, where it leaves, into a piece or two -
            // walking the edges would cut it at every tangent line it crosses, dozens of slivers.
            var nx = new List<double> { 3, 63, 63, 3 };
            var ny = new List<double> { -2.3, -2.3, -1.7, -1.7 };
            UnionOutlineGeometry.SubtractForTests(nx, ny, cx, cy, true, 3, -2, 20, outside);
            Near((float)Left(), (float)(0.6 * (63 - 3 - 20)), "slice: the needle keeps its 40 units beyond the edge", 0.02f);
            Check(outside.Count <= 3, $"slice: the needle is cut where it leaves, not along its length ({outside.Count} pieces)");

            // A band quad straddling the edge, and one clear of it.
            UnionOutlineGeometry.SubtractForTests(new List<double> { 21, 25, 25, 21 }, new List<double> { -3, -3, -1, -1 }, cx, cy, true, 3, -2, 20, outside);
            Near((float)Left(), (float)(4 * 2 - 2 * (23 - 21)), "slice: a quad over the edge keeps what lies beyond it", 0.05f);
            UnionOutlineGeometry.SubtractForTests(new List<double> { 30, 35, 35, 30 }, new List<double> { -3, -3, -1, -1 }, cx, cy, true, 3, -2, 20, outside);
            Check(outside.Count == 1 && Math.Abs(Left() - 10) < 1e-6, "slice: a quad clear of the circle comes back whole");
        }

        private static List<double> Evens(List<double> flat) => flat.Where((v, i) => i % 2 == 0).ToList();

        private static List<double> Odds(List<double> flat) => flat.Where((v, i) => i % 2 == 1).ToList();

        private static bool IsConvex(List<double> xs, List<double> ys)
        {
            bool pos = false, neg = false;
            for (int i = 0; i < xs.Count; i++)
            {
                int j = (i + 1) % xs.Count, k = (i + 2) % xs.Count;
                double cross = (xs[j] - xs[i]) * (ys[k] - ys[j]) - (ys[j] - ys[i]) * (xs[k] - xs[j]);
                if (cross > 1e-9) pos = true;
                else if (cross < -1e-9) neg = true;
            }
            return !(pos && neg);
        }

        // ─────────────────────────────────────────────────────────────
        // WHOLE MESHES
        // ─────────────────────────────────────────────────────────────

        private static void MeshSinglePart()
        {
            var cases = new[]
            {
                ("circle r100", RoundPart(3f, -4f, 100f)),
                ("box 80 x 30", BoxPart(10f, 5f, 40f, 15f)),
                ("box turned 37 degrees", BoxPart(-6f, 2f, 25f, 9f, 37f)),
                ("box 6 x 2, rim clamped", BoxPart(0f, 0f, 3f, 1f, 12f)),
                ("circle r12, the fewest segments", RoundPart(0f, 0f, 12f)),
            };
            foreach ((string what, UnionOutlineGeometry.Part p) in cases)
            {
                UnionMesh union = UnionMesh.Of(p);
                UnionMesh single = UnionMesh.Single(p);
                MeshValid(union, what);
                Raster ru = MeshCovers(union, what, p.HalfH < 5f ? 0.05 : 0.5, 1e-3);
                Raster rs = ru.Twin();
                rs.Draw(single);
                int differ = 0;
                string first = null;
                for (int at = 0; at < ru.Loose.Length; at++)
                {
                    bool drawn = ru.Loose[at] > 0;
                    bool same = drawn == rs.Loose[at] > 0 && (!drawn ||
                        (Math.Abs(ru.Solid[at] - rs.Solid[at]) < 1e-3 && Math.Abs(ru.Fill[at] - rs.Fill[at]) < 1e-3));
                    if (same) continue;
                    differ++;
                    first = first ?? $"#{at}: union {ru.Loose[at]}x {ru.Solid[at]:0.###}/{ru.Fill[at]:0.###}, single {rs.Loose[at]}x {rs.Solid[at]:0.###}/{rs.Fill[at]:0.###}";
                }
                Check(differ == 0, $"{what}: the same coverage and tones as OutlineGeometry.Build at every point ({differ} differ; {first})");
                Check(union.Tris.Count / 3 <= single.Tris.Count / 3, $"{what}: no more triangles than the single outline ({union.Tris.Count / 3} vs {single.Tris.Count / 3})");
            }
        }

        private static readonly (string What, UnionOutlineGeometry.Part[] Parts, double Step)[] CoverageCases =
        {
            ("the lens of two circles", new[] { RoundPart(0f, 0f, 30f), RoundPart(40f, 0f, 30f) }, 0.25),
            ("the L: bar and leg overlapping", new[] { BoxPart(0f, 0f, 20f, 10f), BoxPart(10f, 20f, 10f, 20f) }, 0.25),
            ("two boxes touching", new[] { BoxPart(-10f, 0f, 10f, 10f), BoxPart(10f, 0f, 10f, 10f) }, 0.25),
            ("a cross of turned boxes", new[] { BoxPart(0f, 0f, 40f, 8f, 30f), BoxPart(5f, 5f, 30f, 6f, 120f) }, 0.25),
            ("three circles and a turned box", new[] { RoundPart(0f, 0f, 25f), RoundPart(20f, 10f, 20f), RoundPart(5f, 25f, 18f), BoxPart(-15f, 10f, 20f, 5f, 70f) }, 0.25),
            ("a circle nested in a circle", new[] { RoundPart(0f, 0f, 40f), RoundPart(5f, 5f, 10f) }, 0.25),
            ("the nest the other way round", new[] { RoundPart(5f, 5f, 10f), RoundPart(0f, 0f, 40f) }, 0.25),
            ("a box nested in a circle, sharing none of its edge", new[] { BoxPart(3f, -2f, 8f, 5f, 20f), RoundPart(0f, 0f, 40f) }, 0.25),
            ("two circles far apart", new[] { RoundPart(0f, 0f, 20f), RoundPart(100f, 0f, 20f) }, 0.5),
            ("a big circle and a road across its edge", new[] { RoundPart(0f, 0f, 300f), BoxPart(300f, 20f, 60f, 4f, 10f) }, 1.0),
            ("the same part twice", new[] { BoxPart(0f, 0f, 20f, 10f, 15f), BoxPart(0f, 0f, 20f, 10f, 15f) }, 0.25),
            ("a chain of four circles", new[] { RoundPart(0f, 0f, 20f), RoundPart(30f, 0f, 20f), RoundPart(60f, 5f, 20f), RoundPart(90f, 0f, 20f) }, 0.25),
        };

        private static void MeshCoverage()
        {
            foreach ((string what, UnionOutlineGeometry.Part[] parts, double step) in CoverageCases)
            {
                UnionMesh m = UnionMesh.Of(parts);
                MeshValid(m, what);
                MeshCovers(m, what, step);
            }
        }

        private static void MeshTones()
        {
            // TWO BOXES TOUCHING at x = 0 (x -20..0 and 0..20, y -10..10): no seam, and the rims top and bottom
            // run straight across it.
            UnionMesh touch = UnionMesh.Of(BoxPart(-10f, 0f, 10f, 10f), BoxPart(10f, 0f, 10f, 10f));
            double[][] rightTrue = PartLoop(touch.Parts[1], -1);
            Check(!InConvex(rightTrue, -1.25, 0), "premise: (-1.25, 0) lies in no later part's true shape");
            Filled(touch, -1.25, 0, "the seam, left of it (a rim there under the first design, which filled only inside a later part)");
            Filled(touch, 1.25, 0, "the seam, right of it");
            Filled(touch, 0.3, 4, "on the seam");
            foreach (double x in new[] { -15.0, -2.0, -0.2, 0.0, 0.2, 0.7, 2.0, 15.0 })
            {
                Rim(touch, x, 8.75, $"the top edge across the seam, x {x}");
                Rim(touch, x, -8.75, $"the bottom edge across the seam, x {x}");
            }
            Tone(touch, 0.25, 10.0, 0.5f, 0f, "the top edge itself, over the seam: half way from clear to solid");
            Rim(touch, -18.75, 0, "the left edge");
            Rim(touch, 18.75, 0, "the right edge");
            Filled(touch, -10, 0, "the middle of the first box");
            Filled(touch, 10, 0, "the middle of the second");

            // THE L: bar x -20..20, y -10..10, and leg x 0..20, y 0..40, overlapping in the corner square.
            UnionMesh ell = UnionMesh.Of(BoxPart(0f, 0f, 20f, 10f), BoxPart(10f, 20f, 10f, 20f));
            double[][] legTrue = PartLoop(ell.Parts[1], -1);
            Check(InConvex(legTrue, 18.75, 5), "premise: (18.75, 5) lies inside the leg, a later part");
            foreach (double y in new[] { -5.0, 0.0, 5.0, 9.0, 10.0, 10.4, 10.6, 11.0, 15.0, 30.0 })
                Rim(ell, 18.75, y, $"the shared right edge x = 20 runs unbroken, y {y} (the first design cut a gap here the length of the overlap)");
            Tone(ell, 20.0, 5.0, 0.5f, 0f, "the shared right edge itself");
            Filled(ell, 10, 8.75, "the bar's top edge under the leg is a seam, below it");
            Filled(ell, 10, 11.25, "and above it");
            Filled(ell, 10, 1.25, "the leg's bottom edge inside the bar, above it");
            Filled(ell, 10, -1.25, "and below it");
            Rim(ell, -10, 8.75, "the bar's top edge in the notch");
            Rim(ell, 1.25, 25, "the leg's left edge above the bar");
            Rim(ell, 10, 38.75, "the leg's far end");
            Rim(ell, -18.75, 0, "the bar's far end");
            Rim(ell, 5, -8.75, "the bar's bottom");

            // THE LENS of two overlapping circles: fill inside it, no darker, the rim all round the outside.
            UnionMesh lens = UnionMesh.Of(RoundPart(0f, 0f, 30f), RoundPart(40f, 0f, 30f));
            foreach ((double x, double y) in new[] { (20.0, 0.0), (20.0, 12.0), (12.0, 0.0), (28.75, 0.0), (29.5, 0.0), (31.25, 0.0), (11.25, 0.0), (20.0, -15.0) })
                Filled(lens, x, y, "the lens");
            foreach ((double x, double y) in new[] { (0.0, 28.75), (-28.75, 0.0), (0.0, -28.75), (40.0, 28.75), (68.75, 0.0), (40.0, -28.75), (-20.33, 20.33) })
                Rim(lens, x, y, "the outside of the lens");

            // NESTED: a small part wholly inside a big one draws no rim, whichever came first.
            foreach (bool smallFirst in new[] { false, true })
            {
                UnionOutlineGeometry.Part big = RoundPart(0f, 0f, 40f), small = RoundPart(5f, 5f, 10f);
                UnionMesh nest = smallFirst ? UnionMesh.Of(small, big) : UnionMesh.Of(big, small);
                string order = smallFirst ? "small part first" : "big part first";
                Filled(nest, 5 + 8.75, 5, $"{order}: the small part's edge");
                Filled(nest, 5, 5 - 10, $"{order}: on the small part's edge");
                Filled(nest, 5, 5, $"{order}: its middle");
                Rim(nest, 38.75, 0, $"{order}: the big part's edge");
            }
            UnionMesh boxInCircle = UnionMesh.Of(BoxPart(3f, -2f, 8f, 5f, 20f), RoundPart(0f, 0f, 40f));
            Filled(boxInCircle, 3, -2 + 5, "a box inside a circle, first: its edge");
            Rim(boxInCircle, 0, -38.75, "and the circle's edge");

            // FAR APART: each keeps its own whole outline.
            UnionMesh apart = UnionMesh.Of(RoundPart(0f, 0f, 20f), RoundPart(100f, 0f, 20f));
            Rim(apart, 18.75, 0, "the first circle's edge");
            Rim(apart, 81.25, 0, "the second circle's edge facing it");
            Probe(apart, 50, 0, out _, out int between, out _, out _);
            Check(between == 0, "nothing between them");

            // TURNED BOXES in a cross: the rim on the outside of each arm, fill where they cross.
            UnionOutlineGeometry.Part a = BoxPart(0f, 0f, 40f, 8f, 30f), b = BoxPart(0f, 0f, 30f, 6f, 120f);
            UnionMesh cross = UnionMesh.Of(a, b);
            (double x, double y) At(UnionOutlineGeometry.Part p, double lx, double ly)
            {
                double r = p.Angle * Math.PI / 180;
                return (p.X + Math.Cos(r) * lx - Math.Sin(r) * ly, p.Y + Math.Sin(r) * lx + Math.Cos(r) * ly);
            }
            (double x, double y) q = At(a, 30, 8 - 1.25);
            Rim(cross, q.x, q.y, "the first arm's long side");
            q = At(a, 40 - 1.25, 0);
            Rim(cross, q.x, q.y, "the first arm's end");
            q = At(b, 20, -6 + 1.25);
            Rim(cross, q.x, q.y, "the second arm's long side");
            q = At(a, 3, 8 - 1.25);
            Filled(cross, q.x, q.y, "the first arm's side where the second crosses it");
            q = At(b, 3, 6 - 1.25);
            Filled(cross, q.x, q.y, "the second arm's side where the first crosses it");
            Filled(cross, 0, 0, "the crossing");
        }

        private static void MeshDegenerate()
        {
            UnionMesh none = UnionMesh.Of();
            Check(none.Xs.Count == 0 && none.Tris.Count == 0, "no parts draw nothing");
            foreach (float bad in new[] { 0f, -5f, float.NaN, float.PositiveInfinity })
            {
                UnionMesh m = UnionMesh.Of(RoundPart(0f, 0f, 20f), new UnionOutlineGeometry.Part(true, 10f, 0f, bad, 10f, 0f));
                MeshValid(m, $"a part of half-width {bad} beside a good one");
                UnionMesh alone = UnionMesh.Of(RoundPart(0f, 0f, 20f));
                Check(m.Xs.Count == alone.Xs.Count && m.Tris.Count == alone.Tris.Count, $"a part of half-width {bad} is left out, as Build leaves it out");
            }
            UnionMesh lost = UnionMesh.Of(new UnionOutlineGeometry.Part(false, float.NaN, 0f, 10f, 10f, 0f), BoxPart(0f, 0f, 10f, 10f));
            MeshValid(lost, "a part at a NaN position beside a good one");
            Check(lost.Tris.Count > 0, "the good part is still drawn");

            // Tiny parts: the rim clamps per part, and the mesh stays whole and valid.
            UnionMesh tiny = UnionMesh.Of(BoxPart(0f, 0f, 1f, 0.5f), BoxPart(1.2f, 0.3f, 0.8f, 0.8f, 45f), RoundPart(-0.5f, 0.5f, 0.7f));
            MeshValid(tiny, "three tiny parts");
            MeshCovers(tiny, "three tiny parts", 0.01, 1e-3);

            // A thin sliver of a box across a circle's edge, and a part sitting exactly on another's edge.
            UnionMesh thin = UnionMesh.Of(RoundPart(0f, 0f, 50f), BoxPart(50f, 0f, 20f, 0.3f, 5f));
            MeshValid(thin, "a hairline box across a circle's edge");
            MeshCovers(thin, "a hairline box across a circle's edge", 0.05);
            UnionMesh edge = UnionMesh.Of(BoxPart(0f, 0f, 20f, 20f), RoundPart(20f, 0f, 8f));
            MeshValid(edge, "a circle centred on a box's edge");
            MeshCovers(edge, "a circle centred on a box's edge", 0.25);
        }

        private static void MeshBudget()
        {
            // Forty big circles in two rows, every one overlapping its neighbours: 82 000 loop vertices at full
            // size, so the builder goes down in scale until they fit, and the rim is still the rim's width.
            var parts = new UnionOutlineGeometry.Part[40];
            for (int k = 0; k < 40; k++) parts[k] = RoundPart((k % 20) * 1800f, (k / 20) * 1800f, 1100f);
            var sw = Stopwatch.StartNew();
            UnionMesh m = UnionMesh.Of(parts);
            double ms = sw.Elapsed.TotalMilliseconds;
            Console.WriteLine($"    40 big circles: {ms:0.0} ms, {m.Xs.Count} vertices, scale {m.Scale}");
            Check(m.Scale < 1f, $"built at a smaller scale ({m.Scale})");
            MeshValid(m, "40 big circles");
            MeshCovers(m, "40 big circles", 8.0, 0.1);
            Rim(m, -1100 + 1.25, 0, "the far left edge, at the rim's own width");
            Rim(m, 19 * 1800 + 1100 - 1.25, 1800, "the far right edge of the top row");
            Filled(m, 900, 0, "between the first two circles");
            Filled(m, 0, 900, "between the rows");

            // Past MaxParts: nothing, at once, rather than a stall.
            var many = Enumerable.Range(0, UnionOutlineGeometry.MaxParts + 1).Select(k => BoxPart(k * 3f, 0f, 1f, 1f)).ToArray();
            sw.Restart();
            UnionMesh none = UnionMesh.Of(many);
            Check(none.Xs.Count == 0 && sw.Elapsed.TotalMilliseconds < 50, $"{many.Length} parts draw nothing, at once ({sw.Elapsed.TotalMilliseconds:0.0} ms)");
            UnionMesh most = UnionMesh.Of(many.Take(UnionOutlineGeometry.MaxParts).ToArray());
            MeshValid(most, $"{UnionOutlineGeometry.MaxParts} small boxes in a row");
            Check(most.Tris.Count > 0, $"{UnionOutlineGeometry.MaxParts} parts still draw");
        }

        private static void MeshWorstCase()
        {
            // Sixteen circles of 1100 units (512 segments each, the most there are), centred round a ring small
            // enough that every one overlaps every other: the worst the map can be asked for.
            var worst = new UnionOutlineGeometry.Part[16];
            for (int k = 0; k < 16; k++)
            {
                double a = 2 * Math.PI * k / 16;
                worst[k] = RoundPart((float)(300 * Math.Cos(a)), (float)(300 * Math.Sin(a)), 1100f);
            }
            Check(OutlineGeometry.Segments(true, 1100f, 1100f) == OutlineGeometry.MaxSegments, "the worst case's circles have 512 segments");

            double Time(UnionOutlineGeometry.Part[] parts, out UnionMesh mesh)
            {
                mesh = UnionMesh.Of(parts);   // warm-up
                var best = double.MaxValue;
                for (int run = 0; run < 7; run++)
                {
                    var sw = Stopwatch.StartNew();
                    UnionOutlineGeometry.Build(parts, MeshRim, MeshFeather, mesh.Xs, mesh.Ys, mesh.Solid, mesh.Fill, mesh.Tris);
                    best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
                }
                return best;
            }

            double ms = Time(worst, out UnionMesh m);
            MeshValid(m, "16 overlapping 512-segment circles");
            Console.WriteLine($"    worst case: {ms:0.00} ms, {m.Xs.Count} vertices, {m.Tris.Count / 3} triangles, built at scale {UnionOutlineGeometry.LastScale}");
            m.Scale = UnionOutlineGeometry.LastScale;
            Check(ms < 20.0, $"the worst case builds well within a frame's budget ({ms:0.00} ms, best of 7)");
            // Every point of it still drawn once, sampled coarsely (it is 2800 units across).
            MeshCovers(m, "16 overlapping 512-segment circles", 4.0, 0.05);

            var typical = new (string, UnionOutlineGeometry.Part[])[]
            {
                ("2 parts, minimap size", new[] { RoundPart(0f, 0f, 30f), BoxPart(25f, 5f, 20f, 8f, 20f) }),
                ("2 parts, large map zoomed in", new[] { RoundPart(0f, 0f, 600f), BoxPart(500f, 100f, 400f, 150f, 20f) }),
                ("4 parts, large map", new[] { RoundPart(0f, 0f, 200f), RoundPart(250f, 50f, 150f), BoxPart(-150f, 150f, 120f, 60f, 35f), BoxPart(100f, -200f, 60f, 60f) }),
                ("16 circles in a chain, 512 segments", Enumerable.Range(0, 16).Select(k => RoundPart(k * 1800f, 0f, 1100f)).ToArray()),
            };
            foreach ((string what, UnionOutlineGeometry.Part[] parts) in typical)
            {
                double t = Time(parts, out UnionMesh tm);
                MeshValid(tm, what);
                Console.WriteLine($"    {what}: {t:0.000} ms, {tm.Xs.Count} vertices, {tm.Tris.Count / 3} triangles, scale {UnionOutlineGeometry.LastScale}");
                Check(t < 20.0, $"{what}: in time ({t:0.000} ms)");
            }
        }
    }
}
