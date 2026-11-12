using System;
using System.Collections.Generic;

namespace TerroritySmellatory
{
    /// <summary>
    /// The map outline of a territory made of several parts (0.2.0), as plain numbers: ONE rim along the outer
    /// edge of the whole ground and ONE faint fill, so a combined territory reads as one territory. No Unity
    /// types, so the off-game harness checks it, as it does OutlineGeometry.
    /// </summary>
    /// <remarks>
    /// WHY NOT ONE OUTLINE PER PART. Drawn over each other, two parts show a rim across the middle of the
    /// territory wherever they meet, and their fills add up to a darker lens where they overlap: two
    /// territories, to the eye.
    ///
    /// EACH PART'S OWN CELLS. A part is cut into exactly the cells OutlineGeometry.Build makes for it alone -
    /// taken from Build itself, so the same four loops, segment count and clamps of rim and feather - and a
    /// stretch of outer edge looks exactly as a lone territory's does. The cells are the quads of the three
    /// bands between neighbouring loops, and the fill: a circle's centre fan, one thin triangle per segment,
    /// or a box's fill-edge rectangle. Every cell is convex, and across each the tone is an affine function
    /// of position (a band quad's two long sides are parallel), so a cell can be cut anywhere, the tone
    /// interpolated at the cut, and nothing changes to the eye. Each vertex carries two weights, solid and
    /// fill (clear is what is left), which the graphic mixes into colours: exact, because every cell only
    /// ever interpolates linearly.
    ///
    /// COVERAGE. Part k draws its cells minus the outer feather loop F of every EARLIER part, so a point is
    /// drawn once, by the first part whose F holds it: overlaps are never painted twice and the fill does not
    /// darken. A convex cell minus a convex polygon is cut into convex pieces. Against a box, by a walk along
    /// its four edges (Walk): at each, what is left beyond the edge is kept and the walk goes on inside it.
    /// Against a circle's polygon, by its sectors (Slice): the cell is sliced along the rays from the circle's
    /// centre through its vertices, and each slice keeps what lies beyond its own sector's edge - but only
    /// across the angles where the cell meets the thin ring the polygon's edge lies in (between its inscribed
    /// and circumscribed circles); on either side of those the cell is wholly inside or wholly beyond, and is
    /// dropped or kept in one piece. Walking a circle's hundreds of edges would be as exact, but a cell
    /// reaching far outside the circle is cut at every tangent line it crosses: sixteen overlapping
    /// 512-segment circles came to 133 000 vertices, twice what Unity's UI accepts, 100 000 of them fill
    /// slivers. Slicing every sector the cell passes still cut a long cell that crossed the edge once into
    /// fifty; across the ring only, it is two or three.
    ///
    /// TONE: THE FOOT RULE. A band cell of part k tones the ground along one edge of k. Where that edge runs
    /// inside another part - a short step outward from it lands in that part's true shape - the edge is a seam,
    /// not the territory's border, and the band there is drawn as fill; elsewhere it keeps the rim. The band is
    /// cut across, square to a box's edge or through a circle's centre, where its edge enters the other part.
    /// Deciding by the EDGE rather than by where the band itself lies is what makes this right where parts
    /// TOUCH (each edge of the seam runs inside the other part, so both bands go to fill, though neither band
    /// lies in the other part) and along a straight edge two parts SHARE (a step out of the bar of an L lands
    /// outside the leg too, so the rim runs on unbroken, though the bar's band lies inside the leg). The first
    /// design, "fill where a band lies inside a LATER part's true shape", failed both in the harness: a rim
    /// down every seam of touching parts, and a gap the length of the overlap in a shared edge.
    ///
    /// BOX CORNERS. A box's bands meet at its corners on the diagonal. Where one edge at a corner is a seam and
    /// the other a border - the bar of an L, at the corner the leg stands on - the border's band takes the
    /// whole corner square instead (a butt join): with the diagonal, the seam's half of the square would go to
    /// fill and bite a triangle out of the border's rim.
    ///
    /// ACCEPTED: at a concave corner of the whole ground the two rims end square, each where its own edge
    /// enters the other part, which leaves a notch about the rim's size at the inside of the corner; and the
    /// feather-wide slack between F and the true edge.
    ///
    /// SIZE. Unity's UI refuses a mesh of 65 000 vertices (VertexHelper.FillMesh throws), and a combined
    /// territory is one mesh: sixteen parts of 512 segments are 33 000 vertices in their loops alone, and a
    /// server may allow 64 parts. So when the loops would pass a budget, the whole mesh is built at a smaller
    /// scale - rim and feather scaled with it - and scaled back: the same look with fewer segments per circle.
    ///
    /// SPEED. Every test is by bounding box first; against a circle's polygon, by its analytic circles next
    /// (wholly inside the inscribed one, wholly beyond the circumscribed one); a circle's segment is tested as
    /// a whole column (its bands and its fill triangle) before its cells are, and cut first by the earlier part
    /// that holds most of it; a column whose edge is a seam all along is all fill, and is cut as one cell, not
    /// four; only what straddles an edge is cut. Cells nothing touches keep sharing their loop vertices, as
    /// Build's do. The worst case the harness builds - sixteen 512-segment circles, every one overlapping every
    /// other - takes about 3 ms; two to four parts, well under a tenth of that.
    /// </remarks>
    internal static class UnionOutlineGeometry
    {
        /// <summary>One part, in the frame the mesh is built in: map UI units, +Y up the map (world +Z).</summary>
        internal readonly struct Part
        {
            public readonly bool Round;
            public readonly float X;
            public readonly float Y;
            public readonly float HalfW;
            public readonly float HalfH;

            /// <summary>Degrees counter-clockwise: the rotation the single-part path gives its rect (minus the yaw).</summary>
            public readonly float Angle;

            public Part(bool round, float x, float y, float halfW, float halfH, float angle)
            {
                Round = round;
                X = x;
                Y = y;
                HalfW = halfW;
                HalfH = halfH;
                Angle = angle;
            }
        }

        /// <summary>Pieces cut smaller than this (unit², a hundredth of a unit square) are dropped: no one can see them.</summary>
        internal const double Sliver = 1e-4;

        /// <summary>The most vertices a mesh is built with: under the 65 000 Unity's UI accepts, with room to spare.</summary>
        internal const int MaxVertices = 60000;

        /// <summary>The most the parts' loops may take of that before the mesh is built at a smaller scale.</summary>
        internal const int LoopBudget = 36000;

        /// <summary>
        /// The most parts drawn. Every part is asked about every other, so the work grows with the square of
        /// their number; in-game edits stop at MaxParts (64 at most), and only a hand-made file (the wire allows
        /// 4096) could pass this - its outline is left off the map rather than stall it for seconds.
        /// </summary>
        internal const int MaxParts = 256;

        /// <summary>A vertex this close to a cutting line is taken as on it, so a cut never makes a needle.</summary>
        private const double Snap = 1e-7;

        private const int Loops = OutlineGeometry.Loops;
        private const int Outside = 0;
        private const int Inside = 1;
        private const int Straddles = 2;
        private const int Mitre = 0;
        private const int FavourIn = 1;
        private const int FavourOut = 2;

        /// <summary>The scale the last Build ran at: 1, or less when the parts were too many or too big for the budget.</summary>
        internal static float LastScale { get; private set; } = 1f;

        /// <summary>
        /// Builds the mesh for the parts, in the parts' frame. Output: positions, the solid and fill weight of
        /// each vertex (clear = 1 - solid - fill), and triangles, always under MaxVertices. Parts that cannot be
        /// drawn (no size, not finite) are left out, as Build leaves them out.
        /// </summary>
        internal static void Build(IList<Part> parts, float rim, float feather,
            List<float> xs, List<float> ys, List<float> solid, List<float> fill, List<int> tris)
        {
            double scale = 1.0;
            if (parts != null && parts.Count > MaxParts) parts = null;
            if (parts != null)
            {
                while (scale > 1e-3 && LoopVertices(parts, scale) > LoopBudget) scale *= 0.5;
            }

            for (int attempt = 0; ; attempt++)
            {
                BuildAt(parts, rim, feather, scale, xs, ys, solid, fill, tris);
                LastScale = (float)scale;
                if (xs.Count < MaxVertices) return;
                if (attempt == 6)
                {
                    // Thousands of parts from a hand-made file: nothing rather than a mesh Unity throws on.
                    xs.Clear();
                    ys.Clear();
                    solid.Clear();
                    fill.Clear();
                    tris.Clear();
                    return;
                }
                scale *= 0.5;
            }
        }

        private static int LoopVertices(IList<Part> parts, double scale)
        {
            long total = 0;
            for (int p = 0; p < parts.Count; p++)
            {
                Part part = parts[p];
                total += Loops * OutlineGeometry.Segments(part.Round, (float)(part.HalfW * scale), (float)(part.HalfH * scale)) + 1;
            }
            return total > int.MaxValue ? int.MaxValue : (int)total;
        }

        private static void BuildAt(IList<Part> parts, float rim, float feather, double scale,
            List<float> xs, List<float> ys, List<float> solid, List<float> fill, List<int> tris)
        {
            xs.Clear();
            ys.Clear();
            solid.Clear();
            fill.Clear();
            tris.Clear();
            _shapes.Clear();
            if (parts == null) return;

            float r = (float)(rim * scale), f = (float)(feather * scale);
            for (int p = 0; p < parts.Count; p++)
            {
                Shape s = Prepare(parts[p], r, f, scale);
                if (s != null) _shapes.Add(s);
            }

            // How far out from an edge "does it run inside another part?" is asked: half the feather, so parts
            // that touch, or all but touch, join (a boundary is not a line when what bounds it has width).
            double step = float.IsNaN(f) || float.IsInfinity(f) ? 0.0 : f * 0.5;
            _step = Math.Max(step, 1e-3 * scale);
            _unscale = 1.0 / scale;
            _sliver = Sliver * scale * scale;
            _snap = Snap * scale;

            _xs = xs;
            _ys = ys;
            _solid = solid;
            _fill = fill;
            _tris = tris;
            try
            {
                for (int k = 0; k < _shapes.Count; k++) Draw(k);
            }
            finally
            {
                _xs = _ys = _solid = _fill = null;
                _tris = null;
                _shapes.Clear();
                _top = 0;
            }
        }

        // ─────────────────────────────────────────────────────────────
        // PARTS
        // ─────────────────────────────────────────────────────────────

        /// <summary>A convex polygon of a part (its outer feather loop F, or its true shape T), with its edges as half-planes.</summary>
        private sealed class Poly
        {
            public int N;
            public double[] X = new double[0];
            public double[] Y = new double[0];

            /// <summary>Edge i runs from vertex i to i + 1; inside it, A x + B y + C >= 0, (A, B) a unit normal.</summary>
            public double[] A = new double[0];
            public double[] B = new double[0];
            public double[] C = new double[0];

            public double MinX, MinY, MaxX, MaxY;

            // A round part's polygon, in the frame where it is the regular N-gon on the unit circle (vertex i at
            // angle 2 pi i / N, as Build lays it): the frame the circle tests and the sectors are read in.
            public bool Round;
            public double Cx, Cy, Cos, Sin, InvRx, InvRy, Inner;

            public void Size(int n)
            {
                N = n;
                if (X.Length >= n) return;
                X = new double[n];
                Y = new double[n];
                A = new double[n];
                B = new double[n];
                C = new double[n];
            }

            public void Finish()
            {
                double twice = 0.0;
                for (int i = 0; i < N; i++)
                {
                    int j = i + 1 == N ? 0 : i + 1;
                    twice += X[i] * Y[j] - X[j] * Y[i];
                }
                // Build lays a circle's loops anticlockwise and a box's clockwise: the inside is on the left of
                // the first and the right of the second.
                double o = twice >= 0.0 ? 1.0 : -1.0;

                MinX = MinY = double.MaxValue;
                MaxX = MaxY = double.MinValue;
                for (int i = 0; i < N; i++)
                {
                    int j = i + 1 == N ? 0 : i + 1;
                    double dx = X[j] - X[i], dy = Y[j] - Y[i];
                    double len = Math.Sqrt(dx * dx + dy * dy);
                    double a = len > 0.0 ? -dy * o / len : 0.0;
                    double b = len > 0.0 ? dx * o / len : 0.0;
                    A[i] = a;
                    B[i] = b;
                    C[i] = -(a * X[i] + b * Y[i]);
                    if (X[i] < MinX) MinX = X[i];
                    if (X[i] > MaxX) MaxX = X[i];
                    if (Y[i] < MinY) MinY = Y[i];
                    if (Y[i] > MaxY) MaxY = Y[i];
                }
            }
        }

        private sealed class Shape
        {
            public bool Round;
            public int N;
            public double Cx, Cy, Cos, Sin;

            /// <summary>Build's four loops in the common frame, loop l at l * N; then the centre.</summary>
            public double[] LX = new double[0];
            public double[] LY = new double[0];

            /// <summary>Each loop's half-extents in the part's own frame (a box's corner; a circle's radii).</summary>
            public readonly double[] Rx = new double[Loops];
            public readonly double[] Ry = new double[Loops];

            public readonly float[] S = new float[Loops];
            public readonly float[] F = new float[Loops];
            public readonly Poly Feather = new Poly();
            public readonly Poly True = new Poly();

            /// <summary>The output vertex of each loop vertex once a whole cell has used it (-1 before), as rim and as fill.</summary>
            public int[] Shared = new int[0];
            public int[] SharedFill = new int[0];

            public readonly int[] Join = new int[4];
        }

        private static readonly List<Shape> _shapes = new List<Shape>();
        private static readonly List<Shape> _pool = new List<Shape>();
        private static readonly List<float> _bx = new List<float>();
        private static readonly List<float> _by = new List<float>();
        private static readonly List<byte> _bt = new List<byte>();
        private static readonly List<int> _btris = new List<int>();

        private static Shape Prepare(Part p, float rim, float feather, double scale)
        {
            if (!Finite(p.X) || !Finite(p.Y) || !Finite(p.Angle)) return null;
            float halfW = (float)(p.HalfW * scale), halfH = (float)(p.HalfH * scale);
            OutlineGeometry.Build(p.Round, halfW, halfH, rim, feather, _bx, _by, _bt, _btris);
            int n = OutlineGeometry.Segments(p.Round, halfW, halfH);
            if (_bx.Count != Loops * n + 1) return null;

            if (_pool.Count <= _shapes.Count) _pool.Add(new Shape());
            Shape s = _pool[_shapes.Count];
            s.Round = p.Round;
            s.N = n;
            s.Cx = p.X * scale;
            s.Cy = p.Y * scale;
            double angle = p.Angle * Math.PI / 180.0;
            s.Cos = Math.Cos(angle);
            s.Sin = Math.Sin(angle);

            int count = Loops * n + 1;
            if (s.LX.Length < count)
            {
                s.LX = new double[count];
                s.LY = new double[count];
                s.Shared = new int[count];
                s.SharedFill = new int[count];
            }
            for (int v = 0; v < count; v++)
            {
                ToCommon(s, _bx[v], _by[v], out s.LX[v], out s.LY[v]);
                s.Shared[v] = -1;
                s.SharedFill[v] = -1;
            }

            // A circle's vertex 0 lies on its X axis (angle 0) and the one nearest a quarter turn gives the Y
            // radius; a box's corner 2 is (+X, +Y).
            int q = Math.Max(1, (int)Math.Round(n / 4.0));
            double sinQ = Math.Sin(2.0 * Math.PI * q / n);
            for (int l = 0; l < Loops; l++)
            {
                byte tone = _bt[l * n];
                s.S[l] = tone == OutlineGeometry.Solid ? 1f : 0f;
                s.F[l] = tone == OutlineGeometry.Fill ? 1f : 0f;
                if (p.Round)
                {
                    s.Rx[l] = _bx[l * n];
                    s.Ry[l] = _by[l * n + q] / sinQ;
                }
                else
                {
                    s.Rx[l] = _bx[l * n + 2];
                    s.Ry[l] = _by[l * n + 2];
                }
            }

            // F is the outer feather loop. T, the true shape, is midway between it and the rim's outer edge: those
            // two loops are the edge pulled out and in by the same half feather.
            Poly f = s.Feather, t = s.True;
            f.Size(n);
            t.Size(n);
            for (int i = 0; i < n; i++)
            {
                f.X[i] = s.LX[i];
                f.Y[i] = s.LY[i];
                t.X[i] = (s.LX[i] + s.LX[n + i]) * 0.5;
                t.Y[i] = (s.LY[i] + s.LY[n + i]) * 0.5;
            }
            f.Finish();
            t.Finish();
            Frame(s, f, s.Rx[0], s.Ry[0]);
            Frame(s, t, (s.Rx[0] + s.Rx[1]) * 0.5, (s.Ry[0] + s.Ry[1]) * 0.5);
            return s;
        }

        private static void Frame(Shape s, Poly p, double rx, double ry)
        {
            p.Round = s.Round && rx > 0.0 && ry > 0.0;
            p.Cx = s.Cx;
            p.Cy = s.Cy;
            p.Cos = s.Cos;
            p.Sin = s.Sin;
            p.InvRx = rx > 0.0 ? 1.0 / rx : 0.0;
            p.InvRy = ry > 0.0 ? 1.0 / ry : 0.0;
            p.Inner = Math.Cos(Math.PI / p.N);
        }

        private static void ToCommon(Shape s, double lx, double ly, out double x, out double y)
        {
            x = s.Cx + s.Cos * lx - s.Sin * ly;
            y = s.Cy + s.Sin * lx + s.Cos * ly;
        }

        private static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

        // ─────────────────────────────────────────────────────────────
        // PIECES: convex polygons in one scratch store, reset for every cell
        // ─────────────────────────────────────────────────────────────

        private struct Piece
        {
            public int At;
            public int Count;

            /// <summary>
            /// At least 0: still a whole cell of a round part, which shares its loop vertices - band b of segment i
            /// as b * N + i, the fill triangle as 3 * N + i, the whole column of a seam as 4 * N + i. -1 once cut.
            /// </summary>
            public int Whole;

            /// <summary>Its edge runs inside another part: drawn as fill whatever its weights say.</summary>
            public bool Filled;

            public double MinX, MinY, MaxX, MaxY;
        }

        private static double[] _x = new double[1024];
        private static double[] _y = new double[1024];
        private static float[] _s = new float[1024];
        private static float[] _f = new float[1024];
        private static int _top;

        /// <summary>One piece's vertices' signed distances to a line, or their coordinates in a round polygon's frame.</summary>
        private static double[] _d = new double[256];
        private static double[] _u = new double[256];
        private static double[] _w = new double[256];

        private static Piece Begin()
        {
            return new Piece
            {
                At = _top,
                Whole = -1,
                MinX = double.MaxValue,
                MinY = double.MaxValue,
                MaxX = double.MinValue,
                MaxY = double.MinValue
            };
        }

        private static void Push(ref Piece p, double x, double y, float s, float f)
        {
            if (_top == _x.Length)
            {
                int size = _x.Length * 2;
                Array.Resize(ref _x, size);
                Array.Resize(ref _y, size);
                Array.Resize(ref _s, size);
                Array.Resize(ref _f, size);
            }
            _x[_top] = x;
            _y[_top] = y;
            _s[_top] = s;
            _f[_top] = f;
            _top++;
            p.Count++;
            if (x < p.MinX) p.MinX = x;
            if (x > p.MaxX) p.MaxX = x;
            if (y < p.MinY) p.MinY = y;
            if (y > p.MaxY) p.MaxY = y;
        }

        private static void Scratch(int n)
        {
            if (_d.Length >= n) return;
            int size = Math.Max(n, _d.Length * 2);
            _d = new double[size];
            _u = new double[size];
            _w = new double[size];
        }

        private static double Area(Piece p)
        {
            double twice = 0.0;
            int end = p.At + p.Count;
            for (int v = p.At; v < end; v++)
            {
                int n = v + 1 == end ? p.At : v + 1;
                twice += _x[v] * _y[n] - _x[n] * _y[v];
            }
            return Math.Abs(twice) * 0.5;
        }

        private static bool Kept(Piece p) => p.Count >= 3 && Area(p) > _sliver;

        /// <summary>Sliver and Snap in the frame the mesh is being built in (scaled with it).</summary>
        private static double _sliver = Sliver, _snap = Snap;

        /// <summary>Signed distances of the piece's vertices to the line a x + b y + c = 0, into _d; returns their range.</summary>
        private static void Distances(Piece p, double a, double b, double c, out double lo, out double hi)
        {
            Scratch(p.Count);
            lo = double.MaxValue;
            hi = double.MinValue;
            for (int v = 0; v < p.Count; v++)
            {
                double d = a * _x[p.At + v] + b * _y[p.At + v] + c;
                if (d > -_snap && d < _snap) d = 0.0;
                _d[v] = d;
                if (d < lo) lo = d;
                if (d > hi) hi = d;
            }
        }

        /// <summary>
        /// Sutherland-Hodgman against one line, from the distances in _d: keeps the side where sign * d >= 0, with
        /// the weights interpolated at the cut points. A convex piece stays convex. The two sides of one cut are
        /// made from the same distances, so their cut points are the same numbers and they meet without a gap.
        /// </summary>
        private static Piece Keep(Piece p, double sign)
        {
            Piece r = Begin();
            r.Filled = p.Filled;
            for (int v = 0; v < p.Count; v++)
            {
                int nv = v + 1 == p.Count ? 0 : v + 1;
                double dv = _d[v] * sign, dn = _d[nv] * sign;
                int iv = p.At + v, inx = p.At + nv;
                if (dv >= 0.0) Push(ref r, _x[iv], _y[iv], _s[iv], _f[iv]);
                if ((dv > 0.0 && dn < 0.0) || (dv < 0.0 && dn > 0.0))
                {
                    double t = dv / (dv - dn);
                    Push(ref r,
                        _x[iv] + t * (_x[inx] - _x[iv]),
                        _y[iv] + t * (_y[inx] - _y[iv]),
                        (float)(_s[iv] + t * (_s[inx] - _s[iv])),
                        (float)(_f[iv] + t * (_f[inx] - _f[iv])));
                }
            }
            return r;
        }

        /// <summary>The piece's part where a x + b y + c >= 0: itself when wholly there, empty when wholly not.</summary>
        private static Piece ClipHalfPlane(Piece p, double a, double b, double c)
        {
            Distances(p, a, b, c, out double lo, out double hi);
            if (lo >= 0.0) return p;
            if (hi <= 0.0) return Begin();
            return Keep(p, 1.0);
        }

        /// <summary>
        /// Q minus the convex polygon P, walked along P's edges first .. first + count - 1: at each edge, what is
        /// left of Q beyond it is emitted, and the walk goes on with what is inside it. What is left at the end
        /// is Q inside P, and is dropped. Used against boxes: four edges, four pieces at most.
        /// </summary>
        private static void Walk(Piece q, Poly p, int first, int count, List<Piece> outside)
        {
            Piece rem = q;
            for (int k = 0; k < count; k++)
            {
                int e = first + k;
                if (e >= p.N) e -= p.N;
                Distances(rem, p.A[e], p.B[e], p.C[e], out double lo, out double hi);
                if (lo >= 0.0) continue;
                if (hi <= 0.0)
                {
                    outside.Add(rem);
                    return;
                }
                Piece beyond = Keep(rem, -1.0);
                if (Kept(beyond)) outside.Add(beyond);
                rem = Keep(rem, 1.0);
                if (!Kept(rem)) return;
            }
        }

        /// <summary>
        /// Q minus a round polygon P, by P's sectors: a point in the sector between the rays from P's centre
        /// through vertices e and e + 1 is outside P exactly when it is beyond edge e. P's edge lies in the ring
        /// between its inscribed and circumscribed circles, so Q is sliced along the rays only across the angles
        /// where it meets that ring, and each slice keeps what lies beyond its own edge. What lies before and
        /// after those angles cannot meet the ring, so it is wholly inside P or wholly beyond it, and is dropped
        /// or kept in one piece: a long piece crossing the edge once is cut two or three times, not once per
        /// sector it passes. _u/_w hold Q in P's frame, from Classify.
        /// </summary>
        private static void Slice(Piece q, Poly p, List<Piece> outside)
        {
            if (!Reach(q, p)) return;
            double per = p.N / (2.0 * Math.PI);
            int e0 = (int)Math.Floor(_ringFrom * per);
            int e1 = (int)Math.Floor(_ringTo * per);
            if (e1 - e0 >= p.N) e1 = e0 + p.N - 1;

            Piece rem = q;
            // Before the first sector: all inside or all beyond.
            if (Ray(p, e0, per, out double a, out double b, out double c))
            {
                Distances(rem, a, b, c, out double lo, out double hi);
                if (lo < 0.0 && hi > 0.0)
                {
                    Uniform(Keep(rem, -1.0), p, outside);
                    rem = Keep(rem, 1.0);
                }
                else if (hi <= 0.0)
                {
                    Uniform(rem, p, outside);
                    return;
                }
            }

            for (int e = e0; e <= e1 && rem.Count >= 3; e++)
            {
                int edge = ((e % p.N) + p.N) % p.N;
                Piece slice = rem;
                if (Ray(p, e + 1, per, out a, out b, out c))
                {
                    Distances(rem, a, b, c, out double lo, out double hi);
                    if (hi <= 0.0)
                    {
                        rem = Begin();
                    }
                    else if (lo < 0.0)
                    {
                        slice = Keep(rem, -1.0);
                        rem = Keep(rem, 1.0);
                    }
                    else
                    {
                        continue;   // all of it lies further round
                    }
                }
                else
                {
                    rem = Begin();
                }

                if (slice.Count < 3) continue;
                Distances(slice, p.A[edge], p.B[edge], p.C[edge], out double elo, out double ehi);
                if (ehi <= 0.0) outside.Add(slice);
                else if (elo < 0.0)
                {
                    Piece beyond = Keep(slice, -1.0);
                    if (Kept(beyond)) outside.Add(beyond);
                }
            }

            // After the last sector: all inside or all beyond.
            if (rem.Count >= 3) Uniform(rem, p, outside);
        }

        /// <summary>
        /// The ray from P's centre through vertex v as a half-plane (inside = further round), when that ray
        /// crosses the piece's own span of angles: a ray outside it would, as a whole line, cut the piece
        /// wherever the line's other half crosses it.
        /// </summary>
        private static bool Ray(Poly p, int v, double per, out double a, out double b, out double c)
        {
            a = b = c = 0.0;
            double angle = v / per - _spanRef;
            angle -= 2.0 * Math.PI * Math.Floor((angle + Math.PI) / (2.0 * Math.PI));
            if (angle <= _spanFrom || angle >= _spanTo) return false;
            int i = ((v % p.N) + p.N) % p.N;
            double rx = p.X[i] - p.Cx, ry = p.Y[i] - p.Cy;
            double len = Math.Sqrt(rx * rx + ry * ry);
            if (len <= 0.0) return false;
            a = -ry / len;
            b = rx / len;
            c = -(a * p.Cx + b * p.Cy);
            return true;
        }

        /// <summary>A piece clear of P's ring: kept whole when beyond it, dropped when inside. Told by its middle.</summary>
        private static void Uniform(Piece q, Poly p, List<Piece> outside)
        {
            if (q.Count < 3) return;
            double mx = 0.0, my = 0.0;
            for (int v = 0; v < q.Count; v++)
            {
                mx += _x[q.At + v];
                my += _y[q.At + v];
            }
            double dx = mx / q.Count - p.Cx, dy = my / q.Count - p.Cy;
            double u = (dx * p.Cos + dy * p.Sin) * p.InvRx, w = (dy * p.Cos - dx * p.Sin) * p.InvRy;
            if (u * u + w * w > p.Inner * p.Inner && Kept(q)) outside.Add(q);
        }

        /// <summary>
        /// The angles, in P's frame and from P's centre, that Q spans (_span*, relative to _spanRef) and that
        /// the part of Q in P's ring spans (_ring*, absolute): the extremes of the ring's part are among Q's
        /// vertices in the ring and the points where Q's edges cross either circle. False when Q misses the
        /// ring. Q must not hold P's centre.
        /// </summary>
        private static bool Reach(Piece q, Poly p)
        {
            int n = q.Count;
            double inner2 = p.Inner * p.Inner;
            double ru = 0.0, rw = 0.0;
            for (int v = 0; v < n; v++)
            {
                ru += _u[v];
                rw += _w[v];
            }

            // Pseudo-angles about the piece's own direction (monotonic in the true angle, no trigonometry);
            // only the extremes are turned into angles.
            _reachAny = false;
            for (int v = 0; v < n; v++) Widen(_u[v], _w[v], ru, rw);
            if (!_reachAny) return false;
            _spanRef = Math.Atan2(rw, ru);
            _spanFrom = Math.Atan2(ru * _loW - rw * _loU, ru * _loU + rw * _loW);
            _spanTo = Math.Atan2(ru * _hiW - rw * _hiU, ru * _hiU + rw * _hiW);

            _reachAny = false;
            for (int v = 0; v < n; v++)
            {
                double au = _u[v], aw = _w[v];
                double r = au * au + aw * aw;
                if (r > inner2 && r <= 1.0) Widen(au, aw, ru, rw);
                int nv = v + 1 == n ? 0 : v + 1;
                double eu = _u[nv] - au, ew = _w[nv] - aw;
                double qa = eu * eu + ew * ew;
                if (qa <= 0.0) continue;
                double qb = 2.0 * (au * eu + aw * ew);
                Cross(au, aw, eu, ew, qa, qb, r - inner2, ru, rw);
                Cross(au, aw, eu, ew, qa, qb, r - 1.0, ru, rw);
            }
            if (!_reachAny) return false;
            _ringFrom = _spanRef + Math.Atan2(ru * _loW - rw * _loU, ru * _loU + rw * _loW);
            _ringTo = _spanRef + Math.Atan2(ru * _hiW - rw * _hiU, ru * _hiU + rw * _hiW);
            if (_ringTo < _ringFrom) _ringTo = _ringFrom;
            return true;
        }

        /// <summary>Where the edge from (au, aw) along (eu, ew) crosses a circle about the centre, widened into the range.</summary>
        private static void Cross(double au, double aw, double eu, double ew, double qa, double qb, double qc, double ru, double rw)
        {
            double disc = qb * qb - 4.0 * qa * qc;
            if (disc < 0.0) return;
            double root = Math.Sqrt(disc);
            double t0 = (-qb - root) / (2.0 * qa), t1 = (-qb + root) / (2.0 * qa);
            if (t0 >= 0.0 && t0 <= 1.0) Widen(au + t0 * eu, aw + t0 * ew, ru, rw);
            if (t1 >= 0.0 && t1 <= 1.0) Widen(au + t1 * eu, aw + t1 * ew, ru, rw);
        }

        private static bool _reachAny;
        private static double _lo, _hi, _loU, _loW, _hiU, _hiW;
        private static double _spanRef, _spanFrom, _spanTo, _ringFrom, _ringTo;

        private static void Widen(double u, double w, double ru, double rw)
        {
            // Pseudo-angle of (u, w) from the reference direction (ru, rw), in (-2, 2], rising with the angle.
            double c = ru * w - rw * u, d = ru * u + rw * w;
            double sum = Math.Abs(c) + Math.Abs(d);
            if (sum <= 0.0) return;
            double a = d >= 0.0 ? c / sum : c >= 0.0 ? 2.0 - c / sum : -2.0 - c / sum;
            if (!_reachAny)
            {
                _reachAny = true;
                _lo = _hi = a;
                _loU = _hiU = u;
                _loW = _hiW = w;
                return;
            }
            if (a < _lo)
            {
                _lo = a;
                _loU = u;
                _loW = w;
            }
            if (a > _hi)
            {
                _hi = a;
                _hiU = u;
                _hiW = w;
            }
        }

        /// <summary>
        /// Where a piece stands against a polygon without cutting it, when that can be told: wholly outside by
        /// bounding box; for a round polygon, wholly inside its inscribed circle or wholly beyond its circumscribed
        /// one. Otherwise it straddles. For a round polygon, leaves the piece in the polygon's frame in _u/_w and
        /// says whether it holds the polygon's centre.
        /// </summary>
        private static int Classify(Piece q, Poly p, out bool holdsCentre)
        {
            holdsCentre = false;
            if (q.MaxX < p.MinX || q.MinX > p.MaxX || q.MaxY < p.MinY || q.MinY > p.MaxY) return Outside;
            if (!p.Round) return Straddles;

            int n = q.Count;
            Scratch(n);
            double inner = p.Inner * (1.0 - 1e-6);
            inner *= inner;
            const double outer = (1.0 + 1e-6) * (1.0 + 1e-6);
            bool inside = true, within = false;
            for (int v = 0; v < n; v++)
            {
                double dx = _x[q.At + v] - p.Cx, dy = _y[q.At + v] - p.Cy;
                double u = (dx * p.Cos + dy * p.Sin) * p.InvRx;
                double w = (dy * p.Cos - dx * p.Sin) * p.InvRy;
                _u[v] = u;
                _w[v] = w;
                double r = u * u + w * w;
                if (r > inner) inside = false;
                if (r <= outer) within = true;
            }
            if (inside) return Inside;

            // Does the piece hold the centre (every edge faces it)? If not, and no vertex is within the
            // circumscribed circle, how near does an edge come?
            bool pos = false, neg = false;
            double near = double.MaxValue;
            for (int v = 0; v < n; v++)
            {
                int nv = v + 1 == n ? 0 : v + 1;
                double ax = _u[v], ay = _w[v], ex = _u[nv] - ax, ey = _w[nv] - ay;
                double cross = ey * ax - ex * ay;
                if (cross > 0.0) pos = true;
                else if (cross < 0.0) neg = true;
                if (within) continue;
                double len = ex * ex + ey * ey;
                double t = len > 0.0 ? -(ax * ex + ay * ey) / len : 0.0;
                t = t < 0.0 ? 0.0 : t > 1.0 ? 1.0 : t;
                double px = ax + t * ex, py = ay + t * ey;
                double d = px * px + py * py;
                if (d < near) near = d;
            }
            holdsCentre = !(pos && neg);
            if (!holdsCentre && !within && near > outer) return Outside;
            return Straddles;
        }

        /// <summary>Q minus P, the pieces outside P added to the list. Classify must have said it straddles.</summary>
        private static void Subtract(Piece q, Poly p, bool holdsCentre, List<Piece> outside)
        {
            if (!p.Round)
            {
                Walk(q, p, 0, p.N, outside);
                return;
            }
            if (!holdsCentre)
            {
                Slice(q, p, outside);
                return;
            }

            // A piece around the centre faces every sector: halve it along the line through the centre, and
            // each half spans at most half a turn.
            double a = -p.Sin, b = p.Cos, c = -(a * p.Cx + b * p.Cy);
            Distances(q, a, b, c, out double lo, out double hi);
            Piece upper = lo >= 0.0 ? q : hi <= 0.0 ? Begin() : Keep(q, 1.0);
            Piece lower = hi <= 0.0 ? q : lo >= 0.0 ? Begin() : Keep(q, -1.0);
            SliceHalf(upper, p, outside);
            SliceHalf(lower, p, outside);
        }

        private static void SliceHalf(Piece half, Poly p, List<Piece> outside)
        {
            if (half.Count < 3) return;
            if (Classify(half, p, out _) != Straddles) return;   // an Inside half drops; Outside cannot happen
            Slice(half, p, outside);
        }

        // ─────────────────────────────────────────────────────────────
        // ONE PART'S CELLS
        // ─────────────────────────────────────────────────────────────

        private static List<float> _xs, _ys, _solid, _fill;
        private static List<int> _tris;
        private static double _step, _unscale = 1.0;

        private static readonly List<Piece> _now = new List<Piece>();
        private static readonly List<Piece> _next = new List<Piece>();

        private static double[] _seamLo = new double[16];
        private static double[] _seamHi = new double[16];

        /// <summary>The earlier parts a column must be cut by; the rest cannot touch it.</summary>
        private static int[] _near = new int[16];
        private static int _nearCount;

        private static void Draw(int k)
        {
            Shape s = _shapes[k];
            int n = s.N;
            if (!s.Round) Joins(k);

            for (int i = 0; i < n; i++)
            {
                if (!Near(k, i)) continue;   // the whole column is under an earlier part
                int seams = Seams(k, i);
                if (s.Round && seams == 1 && _seamLo[0] <= 0.0 && _seamHi[0] >= 1.0)
                {
                    // A seam all along: bands and fill are all fill, so the column is one cell and is cut once, not
                    // four times. Where parts pile up, most columns are this, and cutting them band by band made
                    // three quarters of the pieces.
                    Wedge(k, i);
                    continue;
                }
                for (int b = 0; b < Loops - 1; b++)
                {
                    // The stretches of this edge between seams alternate border, seam, border ...
                    double from = 0.0;
                    for (int m = 0; m <= seams; m++)
                    {
                        double lo = m < seams ? _seamLo[m] : 1.0;
                        double hi = m < seams ? _seamHi[m] : 1.0;
                        if (lo > from) Column(k, i, b, from, lo, false, seams == 0);
                        if (m < seams && hi > lo) Column(k, i, b, lo, hi, true, false);
                        from = hi;
                    }
                }
                if (s.Round) FillColumn(k, i);
            }

            if (s.Round) return;
            _top = 0;
            Piece cell = Begin();
            for (int i = 0; i < n; i++) Push(ref cell, s.LX[3 * n + i], s.LY[3 * n + i], s.S[3], s.F[3]);
            cell.Filled = true;
            _nearCount = 0;
            for (int j = 0; j < k; j++) AddNear(j);
            Cover(k, cell);
        }

        /// <summary>
        /// Which earlier parts can touch column i of part k (for a box, all of them): into _near. False when one
        /// holds the whole column, bands and fill, so there is nothing of it to draw.
        /// </summary>
        private static bool Near(int k, int i)
        {
            _nearCount = 0;
            Shape s = _shapes[k];
            if (!s.Round)
            {
                for (int j = 0; j < k; j++) AddNear(j);
                return true;
            }
            if (k == 0) return true;

            _top = 0;
            int n = s.N, next = i + 1 == n ? 0 : i + 1;
            Piece wedge = Begin();
            Push(ref wedge, s.LX[Loops * n], s.LY[Loops * n], 0f, 0f);
            Push(ref wedge, s.LX[i], s.LY[i], 0f, 0f);
            Push(ref wedge, s.LX[next], s.LY[next], 0f, 0f);
            for (int j = 0; j < k; j++)
            {
                int where = Classify(wedge, _shapes[j].Feather, out _);
                if (where == Inside) return false;
                if (where == Straddles) AddNear(j);
            }

            // Deepest first: the part holding most of the column cuts it first and takes the most, so fewer
            // pieces are cut again by the rest only to be dropped later. Measured by the column's outer end.
            double mx = (s.LX[i] + s.LX[next]) * 0.5, my = (s.LY[i] + s.LY[next]) * 0.5;
            for (int m = 0; m < _nearCount; m++) _depth[m] = Depth(_shapes[_near[m]].Feather, mx, my);
            for (int m = 1; m < _nearCount; m++)
            {
                int j = _near[m];
                double d = _depth[m];
                int at = m;
                while (at > 0 && _depth[at - 1] > d)
                {
                    _near[at] = _near[at - 1];
                    _depth[at] = _depth[at - 1];
                    at--;
                }
                _near[at] = j;
                _depth[at] = d;
            }
            return true;
        }

        private static double[] _depth = new double[16];

        /// <summary>How far outside the polygon a point is, roughly (negative inside): only ever compared.</summary>
        private static double Depth(Poly p, double x, double y)
        {
            if (p.Round)
            {
                double dx = x - p.Cx, dy = y - p.Cy;
                double u = (dx * p.Cos + dy * p.Sin) * p.InvRx, w = (dy * p.Cos - dx * p.Sin) * p.InvRy;
                return Math.Sqrt(u * u + w * w) - 1.0;
            }
            double worst = double.MinValue;
            for (int e = 0; e < p.N; e++) worst = Math.Max(worst, -(p.A[e] * x + p.B[e] * y + p.C[e]));
            return worst / Math.Max(1e-9, Math.Max(p.MaxX - p.MinX, p.MaxY - p.MinY));
        }

        private static void AddNear(int j)
        {
            if (_nearCount == _near.Length)
            {
                Array.Resize(ref _near, _nearCount * 2);
                Array.Resize(ref _depth, _nearCount * 2);
            }
            _near[_nearCount++] = j;
        }

        /// <summary>Band b of edge i's column, between parameters from and to along the edge.</summary>
        private static void Column(int k, int i, int b, double from, double to, bool seam, bool whole)
        {
            Shape s = _shapes[k];
            _top = 0;
            Piece q = s.Round ? RoundQuad(s, i, b) : BoxQuad(s, i, b);
            if (from > 0.0)
            {
                Cut(s, i, from, out double a, out double bb, out double c);
                q = ClipHalfPlane(q, a, bb, c);
            }
            if (to < 1.0 && q.Count >= 3)
            {
                Cut(s, i, to, out double a, out double bb, out double c);
                q = ClipHalfPlane(q, -a, -bb, -c);
            }
            if (q.Count < 3) return;
            if (!whole && Area(q) <= _sliver) return;
            q.Filled = seam;
            q.Whole = whole && s.Round ? b * s.N + i : -1;
            Cover(k, q);
        }

        /// <summary>A circle's fill triangle for segment i: Build's own centre fan.</summary>
        private static void FillColumn(int k, int i)
        {
            Shape s = _shapes[k];
            _top = 0;
            int n = s.N, next = i + 1 == n ? 0 : i + 1;
            Piece q = Begin();
            Push(ref q, s.LX[Loops * n], s.LY[Loops * n], s.S[3], s.F[3]);
            Push(ref q, s.LX[3 * n + i], s.LY[3 * n + i], s.S[3], s.F[3]);
            Push(ref q, s.LX[3 * n + next], s.LY[3 * n + next], s.S[3], s.F[3]);
            q.Filled = true;
            q.Whole = 3 * n + i;
            Cover(k, q);
        }

        /// <summary>A circle's whole column as one fill cell: from the centre to the outer feather loop.</summary>
        private static void Wedge(int k, int i)
        {
            Shape s = _shapes[k];
            _top = 0;
            int n = s.N, next = i + 1 == n ? 0 : i + 1;
            Piece q = Begin();
            Push(ref q, s.LX[Loops * n], s.LY[Loops * n], 0f, 1f);
            Push(ref q, s.LX[i], s.LY[i], 0f, 1f);
            Push(ref q, s.LX[next], s.LY[next], 0f, 1f);
            q.Filled = true;
            q.Whole = Loops * n + i;
            Cover(k, q);
        }

        private static Piece RoundQuad(Shape s, int i, int b)
        {
            int n = s.N, j = i + 1 == n ? 0 : i + 1;
            int o = b * n, inner = (b + 1) * n;
            Piece q = Begin();
            Push(ref q, s.LX[o + i], s.LY[o + i], s.S[b], s.F[b]);
            Push(ref q, s.LX[o + j], s.LY[o + j], s.S[b], s.F[b]);
            Push(ref q, s.LX[inner + j], s.LY[inner + j], s.S[b + 1], s.F[b + 1]);
            Push(ref q, s.LX[inner + i], s.LY[inner + i], s.S[b + 1], s.F[b + 1]);
            return q;
        }

        /// <summary>
        /// Band b of a box's edge e, from corner e to corner e + 1 (Build's order: bottom-left, top-left,
        /// top-right, bottom-right), with its ends as the corners' joins say.
        /// </summary>
        private static Piece BoxQuad(Shape s, int e, int b)
        {
            int ca = e, cb = (e + 1) & 3;
            Piece q = Begin();
            BoxEnd(s, e, ca, b, false, out double x, out double y);
            Push(ref q, x, y, s.S[b], s.F[b]);
            BoxEnd(s, e, cb, b, true, out x, out y);
            Push(ref q, x, y, s.S[b], s.F[b]);
            BoxEnd(s, e, cb, b + 1, true, out x, out y);
            Push(ref q, x, y, s.S[b + 1], s.F[b + 1]);
            BoxEnd(s, e, ca, b + 1, false, out x, out y);
            Push(ref q, x, y, s.S[b + 1], s.F[b + 1]);
            return q;
        }

        /// <summary>
        /// Where edge e's loop at <paramref name="loop"/> ends at corner c: on the diagonal (the loop's own corner);
        /// or, at a butt join, on the other edge's outer feather line when e is the border taking the corner, or on
        /// its fill-edge line when the other edge takes it.
        /// </summary>
        private static void BoxEnd(Shape s, int e, int c, int loop, bool endsHere, out double x, out double y)
        {
            int join = s.Join[c];
            int other = join == Mitre ? loop : (join == FavourIn) == endsHere ? 0 : Loops - 1;
            double sx = c >= 2 ? 1.0 : -1.0;
            double sy = c == 1 || c == 2 ? 1.0 : -1.0;
            bool horizontal = (e & 1) == 1;
            double lx = sx * s.Rx[horizontal ? other : loop];
            double ly = sy * s.Ry[horizontal ? loop : other];
            ToCommon(s, lx, ly, out x, out y);
        }

        /// <summary>The line across edge i's column at parameter t: square to a box's edge, through a circle's centre. Inside = further along.</summary>
        private static void Cut(Shape s, int i, double t, out double a, out double b, out double c)
        {
            Poly p = s.True;
            int j = i + 1 == p.N ? 0 : i + 1;
            double ex = p.X[j] - p.X[i], ey = p.Y[j] - p.Y[i];
            double px = p.X[i] + t * ex, py = p.Y[i] + t * ey;
            double nx, ny;
            if (s.Round)
            {
                nx = -(py - s.Cy);
                ny = px - s.Cx;
                if (nx * ex + ny * ey < 0.0)
                {
                    nx = -nx;
                    ny = -ny;
                }
            }
            else
            {
                nx = ex;
                ny = ey;
            }
            double len = Math.Sqrt(nx * nx + ny * ny);
            if (len <= 0.0)
            {
                a = b = 0.0;
                c = 1.0;
                return;
            }
            a = nx / len;
            b = ny / len;
            c = -(a * px + b * py);
        }

        /// <summary>
        /// The corners of box part k: a butt join where exactly one of its two edges runs inside another part near
        /// the corner, handing the corner square to the other (the border); the diagonal otherwise.
        /// </summary>
        private static void Joins(int k)
        {
            Shape s = _shapes[k];
            for (int c = 0; c < 4; c++)
            {
                bool inSeam = SeamNear(k, (c + 3) & 3, true);
                bool outSeam = SeamNear(k, c, false);
                s.Join[c] = inSeam == outSeam ? Mitre : inSeam ? FavourOut : FavourIn;
            }
        }

        private static bool SeamNear(int k, int e, bool atEnd)
        {
            int seams = Seams(k, e);
            Poly p = _shapes[k].True;
            int j = e + 1 == p.N ? 0 : e + 1;
            double len = Math.Sqrt((p.X[j] - p.X[e]) * (p.X[j] - p.X[e]) + (p.Y[j] - p.Y[e]) * (p.Y[j] - p.Y[e]));
            double t = len > 0.0 ? Math.Min(0.5, _step / len) : 0.5;
            double at = atEnd ? 1.0 - t : t;
            for (int m = 0; m < seams; m++)
                if (_seamLo[m] <= at && at <= _seamHi[m]) return true;
            return false;
        }

        /// <summary>
        /// The stretches of part k's edge i, as parameters along its true edge, where a step outward lands inside
        /// another part's true shape: sorted and merged into _seamLo/_seamHi. Returns how many.
        /// </summary>
        private static int Seams(int k, int i)
        {
            Shape s = _shapes[k];
            Poly t = s.True;
            int j = i + 1 == t.N ? 0 : i + 1;
            double ox = -t.A[i] * _step, oy = -t.B[i] * _step;
            double x0 = t.X[i] + ox, y0 = t.Y[i] + oy, x1 = t.X[j] + ox, y1 = t.Y[j] + oy;
            double minX = Math.Min(x0, x1), maxX = Math.Max(x0, x1), minY = Math.Min(y0, y1), maxY = Math.Max(y0, y1);

            int count = 0;
            for (int o = 0; o < _shapes.Count; o++)
            {
                if (o == k) continue;
                Poly p = _shapes[o].True;
                if (maxX < p.MinX || minX > p.MaxX || maxY < p.MinY || minY > p.MaxY) continue;
                if (!SegmentInside(p, x0, y0, x1, y1, out double lo, out double hi)) continue;

                if (count == _seamLo.Length)
                {
                    Array.Resize(ref _seamLo, count * 2);
                    Array.Resize(ref _seamHi, count * 2);
                }
                // Insertion by start, then merged below.
                int at = count++;
                while (at > 0 && _seamLo[at - 1] > lo)
                {
                    _seamLo[at] = _seamLo[at - 1];
                    _seamHi[at] = _seamHi[at - 1];
                    at--;
                }
                _seamLo[at] = lo;
                _seamHi[at] = hi;
            }
            if (count < 2) return count;

            int merged = 0;
            for (int m = 1; m < count; m++)
            {
                if (_seamLo[m] <= _seamHi[merged])
                {
                    if (_seamHi[m] > _seamHi[merged]) _seamHi[merged] = _seamHi[m];
                    continue;
                }
                merged++;
                _seamLo[merged] = _seamLo[m];
                _seamHi[merged] = _seamHi[m];
            }
            return merged + 1;
        }

        /// <summary>Which stretch of the segment lies inside the polygon (a round one read as its circle): false when none.</summary>
        private static bool SegmentInside(Poly p, double x0, double y0, double x1, double y1, out double lo, out double hi)
        {
            lo = 0.0;
            hi = 1.0;
            if (p.Round)
            {
                double dx0 = x0 - p.Cx, dy0 = y0 - p.Cy, dx1 = x1 - p.Cx, dy1 = y1 - p.Cy;
                double u0 = (dx0 * p.Cos + dy0 * p.Sin) * p.InvRx, w0 = (dy0 * p.Cos - dx0 * p.Sin) * p.InvRy;
                double u1 = (dx1 * p.Cos + dy1 * p.Sin) * p.InvRx, w1 = (dy1 * p.Cos - dx1 * p.Sin) * p.InvRy;
                double du = u1 - u0, dw = w1 - w0;
                double qa = du * du + dw * dw;
                double qb = 2.0 * (u0 * du + w0 * dw);
                double qc = u0 * u0 + w0 * w0 - 1.0;
                if (qa <= 1e-18) return qc <= 0.0;
                double disc = qb * qb - 4.0 * qa * qc;
                if (disc <= 0.0) return false;
                double r = Math.Sqrt(disc);
                lo = Math.Max(0.0, (-qb - r) / (2.0 * qa));
                hi = Math.Min(1.0, (-qb + r) / (2.0 * qa));
                return hi > lo;
            }

            for (int e = 0; e < p.N; e++)
            {
                double f0 = p.A[e] * x0 + p.B[e] * y0 + p.C[e];
                double f1 = p.A[e] * x1 + p.B[e] * y1 + p.C[e];
                if (f0 < 0.0 && f1 < 0.0) return false;
                if (f0 < 0.0) lo = Math.Max(lo, f0 / (f0 - f1));
                else if (f1 < 0.0) hi = Math.Min(hi, f0 / (f0 - f1));
                if (hi <= lo) return false;
            }
            return true;
        }

        // ─────────────────────────────────────────────────────────────
        // COVERAGE AND OUTPUT
        // ─────────────────────────────────────────────────────────────

        /// <summary>The piece minus the outer feather loop of each earlier part in _near, drawn.</summary>
        private static void Cover(int k, Piece q)
        {
            List<Piece> now = _now, next = _next;
            now.Clear();
            now.Add(q);
            for (int m = 0; m < _nearCount && now.Count > 0; m++)
            {
                Poly f = _shapes[_near[m]].Feather;
                if (q.MaxX < f.MinX || q.MinX > f.MaxX || q.MaxY < f.MinY || q.MinY > f.MaxY) continue;
                next.Clear();
                for (int c = 0; c < now.Count; c++)
                {
                    Piece p = now[c];
                    int where = Classify(p, f, out bool holdsCentre);
                    if (where == Outside) next.Add(p);
                    else if (where == Straddles) Subtract(p, f, holdsCentre, next);
                }
                List<Piece> swap = now;
                now = next;
                next = swap;
            }
            Shape s = _shapes[k];
            for (int c = 0; c < now.Count; c++) Emit(s, now[c]);
            _now.Clear();
            _next.Clear();
        }

        private static void Emit(Shape s, Piece q)
        {
            if (q.Whole >= 0)
            {
                int n = s.N, b = q.Whole / n, c = q.Whole % n, d = c + 1 == n ? 0 : c + 1;
                if (b == Loops - 1)
                {
                    // The fill triangle: Build's centre fan.
                    Triangle(Vertex(s, Loops * n, false), Vertex(s, 3 * n + c, false), Vertex(s, 3 * n + d, false));
                    return;
                }
                if (b == Loops)
                {
                    // A whole seam column, all fill: the centre and the outer feather loop.
                    Triangle(Vertex(s, Loops * n, false), Vertex(s, c, true), Vertex(s, d, true));
                    return;
                }
                int o0 = Vertex(s, b * n + c, q.Filled), o1 = Vertex(s, b * n + d, q.Filled);
                int i1 = Vertex(s, (b + 1) * n + d, q.Filled), i0 = Vertex(s, (b + 1) * n + c, q.Filled);
                // Build's own split of a band quad.
                Triangle(o0, o1, i1);
                Triangle(o0, i1, i0);
                return;
            }

            int first = _xs.Count;
            for (int v = 0; v < q.Count; v++)
            {
                int at = q.At + v;
                _xs.Add((float)(_x[at] * _unscale));
                _ys.Add((float)(_y[at] * _unscale));
                _solid.Add(q.Filled ? 0f : _s[at]);
                _fill.Add(q.Filled ? 1f : _f[at]);
            }
            for (int v = 1; v + 1 < q.Count; v++) Triangle(first, first + v, first + v + 1);
        }

        private static int Vertex(Shape s, int index, bool filled)
        {
            int loop = Math.Min(index / s.N, Loops - 1);
            if (loop == Loops - 1) filled = false;   // the fill edge and the centre are fill either way: one vertex serves both
            int[] shared = filled ? s.SharedFill : s.Shared;
            if (shared[index] >= 0) return shared[index];
            int v = _xs.Count;
            _xs.Add((float)(s.LX[index] * _unscale));
            _ys.Add((float)(s.LY[index] * _unscale));
            _solid.Add(filled ? 0f : s.S[loop]);
            _fill.Add(filled ? 1f : s.F[loop]);
            shared[index] = v;
            return v;
        }

        private static void Triangle(int a, int b, int c)
        {
            _tris.Add(a);
            _tris.Add(b);
            _tris.Add(c);
        }

        // ─────────────────────────────────────────────────────────────
        // FOR THE HARNESS: the primitives on their own
        // ─────────────────────────────────────────────────────────────

        /// <summary>The polygon (x, y, and a weight carried as solid) clipped to a x + b y + c >= 0, in place. False when nothing is left.</summary>
        internal static bool ClipForTests(List<double> xs, List<double> ys, List<float> ws, double a, double b, double c)
        {
            _top = 0;
            _sliver = Sliver;
            _snap = Snap;
            Piece p = Begin();
            for (int i = 0; i < xs.Count; i++) Push(ref p, xs[i], ys[i], ws[i], 0f);
            Piece r = ClipHalfPlane(p, a, b, c);
            xs.Clear();
            ys.Clear();
            ws.Clear();
            for (int v = 0; v < r.Count; v++)
            {
                xs.Add(_x[r.At + v]);
                ys.Add(_y[r.At + v]);
                ws.Add(_s[r.At + v]);
            }
            _top = 0;
            return r.Count >= 3;
        }

        /// <summary>
        /// Convex Q minus convex P, by the walk along P's edges or, with <paramref name="round"/>, by P's sectors
        /// about <paramref name="cx"/>, <paramref name="cy"/> (P then a regular polygon laid anticlockwise from
        /// angle 0, as Build lays a circle). The pieces outside go to <paramref name="outside"/>, each a flat list
        /// of x, y pairs.
        /// </summary>
        internal static void SubtractForTests(List<double> qx, List<double> qy, List<double> px, List<double> py,
            bool round, double cx, double cy, double radius, List<List<double>> outside)
        {
            var poly = new Poly();
            poly.Size(px.Count);
            for (int i = 0; i < px.Count; i++)
            {
                poly.X[i] = px[i];
                poly.Y[i] = py[i];
            }
            poly.Finish();
            poly.Round = round;
            poly.Cx = cx;
            poly.Cy = cy;
            poly.Cos = 1.0;
            poly.Sin = 0.0;
            poly.InvRx = poly.InvRy = radius > 0 ? 1.0 / radius : 0.0;
            poly.Inner = Math.Cos(Math.PI / poly.N);

            _top = 0;
            _sliver = Sliver;
            _snap = Snap;
            Piece q = Begin();
            for (int i = 0; i < qx.Count; i++) Push(ref q, qx[i], qy[i], 0f, 0f);
            _next.Clear();
            int where = Classify(q, poly, out bool holdsCentre);
            if (where == Outside) _next.Add(q);
            else if (where == Straddles) Subtract(q, poly, holdsCentre, _next);
            outside.Clear();
            foreach (Piece piece in _next)
            {
                var flat = new List<double>();
                for (int v = 0; v < piece.Count; v++)
                {
                    flat.Add(_x[piece.At + v]);
                    flat.Add(_y[piece.At + v]);
                }
                outside.Add(flat);
            }
            _next.Clear();
            _top = 0;
        }
    }
}
