using System;
using System.Collections.Generic;

namespace TerroritySmellatory
{
    /// <summary>
    /// The mesh behind a territory's map outline, as plain numbers: no Unity types, so the off-game harness
    /// checks it. A rim of constant width just inside the shape's edge, softened on both sides, over a faint
    /// fill.
    /// </summary>
    /// <remarks>
    /// WHY A MESH. 0.1.0 stretched a 256 px ring texture over the territory, so the rim grew with the
    /// territory: fine on the large map, a band a third of the minimap wide on a 600 m territory there. Here
    /// the rim is a width in the map's own UI units, whatever size the territory is drawn at.
    ///
    /// Four nested loops, each the outline pulled in by an inset: the outer feather (clear), the rim's outer
    /// and inner edges (solid), and the fill's edge. Bands of quads join neighbouring loops and a fan from the
    /// centre fills the middle. A box's loop is its four corners, and an inset rectangle is exact, mitred
    /// corners and all. A circle's loop is a polygon fine enough that its chords stay well inside the rim.
    /// Positions are relative to the shape's centre, in the same units as the half-extents.
    /// </remarks>
    internal static class OutlineGeometry
    {
        internal const byte Clear = 0;
        internal const byte Solid = 1;
        internal const byte Fill = 2;

        internal const int MinSegments = 32;
        internal const int MaxSegments = 512;

        /// <summary>Loops per outline; the vertex count is Loops * segments + 1 (the centre).</summary>
        internal const int Loops = 4;

        internal static void Build(bool round, float halfW, float halfH, float rim, float feather,
            List<float> xs, List<float> ys, List<byte> tones, List<int> tris)
        {
            xs.Clear();
            ys.Clear();
            tones.Clear();
            tris.Clear();
            if (!Usable(halfW) || !Usable(halfH)) return;

            // A rim can take at most half of the shape's narrower side, and the feather can be no wider than
            // the rim, which keeps the four insets in order however small the shape is drawn.
            rim = Clamp(Usable(rim) ? rim : 0f, 0f, Math.Min(halfW, halfH) * 0.5f);
            feather = Clamp(Usable(feather) ? feather : 0f, 0f, rim);
            float half = feather * 0.5f;
            int n = Segments(round, halfW, halfH);

            int outerFeather = AddLoop(round, n, halfW, halfH, -half, Clear, xs, ys, tones);
            int rimOuter = AddLoop(round, n, halfW, halfH, half, Solid, xs, ys, tones);
            int rimInner = AddLoop(round, n, halfW, halfH, rim - half, Solid, xs, ys, tones);
            int fillEdge = AddLoop(round, n, halfW, halfH, rim + half, Fill, xs, ys, tones);

            Band(outerFeather, rimOuter, n, tris);
            Band(rimOuter, rimInner, n, tris);
            Band(rimInner, fillEdge, n, tris);

            int centre = xs.Count;
            xs.Add(0f);
            ys.Add(0f);
            tones.Add(Fill);
            for (int i = 0; i < n; i++)
            {
                tris.Add(centre);
                tris.Add(fillEdge + i);
                tris.Add(fillEdge + (i + 1) % n);
            }
        }

        /// <summary>
        /// Four for a box. For a circle, one segment per two units of radius, between 32 and 512: a chord strays
        /// from the true circle by R(1 - cos(pi/n)), which stays under about 0.4 units at every size up to a map
        /// zoomed right in, well inside a 2-unit rim.
        /// </summary>
        internal static int Segments(bool round, float halfW, float halfH)
        {
            if (!round) return 4;
            double r = Math.Max(halfW, halfH) * 0.5;
            if (double.IsNaN(r) || r < MinSegments) return MinSegments;
            return r > MaxSegments ? MaxSegments : (int)Math.Ceiling(r);
        }

        private static int AddLoop(bool round, int n, float halfW, float halfH, float inset, byte tone,
            List<float> xs, List<float> ys, List<byte> tones)
        {
            int first = xs.Count;
            float rx = Math.Max(0f, halfW - inset);
            float ry = Math.Max(0f, halfH - inset);
            for (int i = 0; i < n; i++)
            {
                float x, y;
                if (round)
                {
                    double a = 2.0 * Math.PI * i / n;
                    x = (float)(Math.Cos(a) * rx);
                    y = (float)(Math.Sin(a) * ry);
                }
                else
                {
                    // Corners in order around the box: bottom-left, top-left, top-right, bottom-right.
                    x = i < 2 ? -rx : rx;
                    y = i == 0 || i == 3 ? -ry : ry;
                }
                xs.Add(x);
                ys.Add(y);
                tones.Add(tone);
            }
            return first;
        }

        private static void Band(int outer, int inner, int n, List<int> tris)
        {
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                tris.Add(outer + i);
                tris.Add(outer + j);
                tris.Add(inner + j);
                tris.Add(outer + i);
                tris.Add(inner + j);
                tris.Add(inner + i);
            }
        }

        private static bool Usable(float v) => v > 0f && !float.IsInfinity(v);

        private static float Clamp(float v, float min, float max) => v < min ? min : v > max ? max : v;
    }
}
