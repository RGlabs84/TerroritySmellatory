using System;

namespace TerroritySmellatory
{
    // 0.3.1: Njord's rune glyphs, as plain numbers and plain bytes - no Unity types - so the off-game harness can prove
    // every rune fits its 128 px box and draws something, and Visuals/BarrierRunes.cs only wraps the bytes in a Texture2D.
    //
    // A COPY, on purpose, of Njord's VFX/NjordRuneTextureGenerator.cs (the live, in-game-proven generator behind its rune
    // trail): the same 16 hand-drawn glyphs, the same 128 px box (y up, row 0 at the bottom), the same two passes - a
    // thick dim "glow" line for every stroke first, then a thinner full-strength "stroke" line over it - and the same
    // Bresenham walk stamping a filled disc at every step. Njord draws with Texture2D.SetPixel one texel at a time and
    // fills the rest with (0,0,0,0); this fills a byte array and the caller uploads it with SetPixels32 + Apply(true).
    // The glyph is white: the sprite's colour tints it, exactly as Njord's SpriteRenderer.color does.
    internal static class BarrierRuneGlyphs
    {
        internal const int Size = 128;
        internal const int Count = 16;

        // Njord's texels: the glow is 60 % of the colour at 60 % alpha, the stroke is the colour at full alpha.
        private const byte Glow = 153, Full = 255;

        // Per glyph, six numbers a line: x0, y0, x1, y1, glow radius, stroke radius - the radii Njord's own switch
        // uses for that segment (spines 6/3, arms 5/2, the lone isa bar 8/4). Lines are copied verbatim.
        private static readonly int[][] Lines =
        {
            /* 0  gebo, an X       */ new[] { 20, 20, 108, 108, 6, 3,   108, 20, 20, 108, 6, 3 },
            /* 1  algiz            */ new[] { 64, 16, 64, 112, 6, 3,   64, 64, 32, 96, 5, 2,   64, 64, 96, 96, 5, 2 },
            /* 2  fehu             */ new[] { 40, 20, 40, 108, 6, 3,   40, 96, 96, 112, 5, 2,   40, 72, 96, 88, 5, 2 },
            /* 3  raidho           */ new[] { 40, 20, 40, 112, 6, 3,   40, 96, 88, 112, 5, 2,   88, 112, 88, 80, 5, 2,   88, 80, 40, 96, 5, 2 },
            /* 4  isa              */ new[] { 64, 16, 64, 112, 8, 4 },
            /* 5  tiwaz            */ new[] { 64, 16, 64, 112, 6, 3,   64, 40, 32, 72, 5, 2,   64, 40, 96, 72, 5, 2 },
            /* 6  hagalaz          */ new[] { 36, 16, 36, 112, 6, 3,   92, 16, 92, 112, 6, 3,   36, 36, 92, 92, 5, 2 },
            /* 7  sowilo           */ new[] { 80, 20, 40, 20, 5, 2,   80, 20, 40, 64, 5, 2,   40, 64, 80, 108, 5, 2,   80, 108, 40, 108, 5, 2 },
            /* 8  nauthiz          */ new[] { 64, 16, 64, 112, 6, 3,   96, 96, 32, 32, 5, 2 },
            /* 9  uruz             */ new[] { 32, 48, 32, 112, 6, 3,   96, 48, 96, 112, 6, 3,   32, 48, 64, 16, 5, 2,   64, 16, 96, 48, 5, 2 },
            /* 10 kenaz            */ new[] { 48, 16, 48, 112, 6, 3,   48, 80, 96, 48, 5, 2 },
            /* 11 othala           */ new[] { 64, 112, 96, 72, 5, 2,   96, 72, 64, 32, 5, 2,   64, 32, 32, 72, 5, 2,   32, 72, 64, 112, 5, 2,   32, 72, 16, 16, 5, 2,   96, 72, 112, 16, 5, 2 },
            /* 12 dagaz            */ new[] { 20, 20, 64, 64, 5, 2,   20, 108, 64, 64, 5, 2,   108, 20, 64, 64, 5, 2,   108, 108, 64, 64, 5, 2 },
            /* 13 berkanan         */ new[] { 36, 16, 36, 112, 6, 3,   36, 96, 80, 80, 5, 2,   80, 80, 36, 64, 5, 2,   36, 64, 80, 48, 5, 2,   80, 48, 36, 32, 5, 2 },
            /* 14 mannaz           */ new[] { 32, 16, 32, 112, 6, 3,   96, 16, 96, 112, 6, 3,   32, 112, 96, 64, 5, 2,   96, 112, 32, 64, 5, 2 },
            /* 15 ingwaz           */ new[] { 64, 16, 108, 64, 6, 3,   108, 64, 64, 112, 6, 3,   64, 112, 20, 64, 6, 3,   20, 64, 64, 16, 6, 3 },
        };

        /// <summary>How many line segments glyph <paramref name="glyph"/> has.</summary>
        internal static int Segments(int glyph) => Lines[glyph].Length / 6;

        /// <summary>The lowest and highest texel (x or y) glyph <paramref name="glyph"/> can touch, glow discs included; the tests keep both inside the 128 px box so nothing is clipped.</summary>
        internal static void Extent(int glyph, out int min, out int max)
        {
            int[] s = Lines[glyph];
            min = int.MaxValue;
            max = int.MinValue;
            for (int i = 0; i + 5 < s.Length; i += 6)
            {
                int r = s[i + 4];   // the glow disc is the widest thing a segment draws
                min = Math.Min(min, Math.Min(Math.Min(s[i], s[i + 2]), Math.Min(s[i + 1], s[i + 3])) - r);
                max = Math.Max(max, Math.Max(Math.Max(s[i], s[i + 2]), Math.Max(s[i + 1], s[i + 3])) + r);
            }
        }

        /// <summary>
        /// Draws glyph <paramref name="glyph"/> (0-15) into <paramref name="rgba"/>, Size x Size RGBA32 bytes, row 0 first:
        /// cleared to transparent, then every segment's glow, then every segment's stroke. Returns the number of texels
        /// that ended up non-transparent (the tests expect a real drawing, not an empty box).
        /// </summary>
        internal static int Draw(int glyph, byte[] rgba)
        {
            if (rgba == null || rgba.Length != Size * Size * 4) throw new ArgumentException("rgba must be Size * Size * 4 bytes", nameof(rgba));
            if (glyph < 0 || glyph >= Count) glyph = Count - 1;   // Njord's own switch falls to a default for an out-of-range index
            Array.Clear(rgba, 0, rgba.Length);
            int[] s = Lines[glyph];
            for (int i = 0; i + 5 < s.Length; i += 6) Line(rgba, s[i], s[i + 1], s[i + 2], s[i + 3], s[i + 4], Glow, Glow);
            for (int i = 0; i + 5 < s.Length; i += 6) Line(rgba, s[i], s[i + 1], s[i + 2], s[i + 3], s[i + 5], Full, Full);
            int lit = 0;
            for (int i = 3; i < rgba.Length; i += 4) if (rgba[i] != 0) lit++;
            return lit;
        }

        // Njord's DrawLine: Bresenham, a filled disc of radius r stamped at every step.
        private static void Line(byte[] px, int x0, int y0, int x1, int y1, int r, byte rgb, byte alpha)
        {
            int dx = Math.Abs(x1 - x0), dy = Math.Abs(y1 - y0);
            int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
            int err = dx - dy;
            while (true)
            {
                Disc(px, x0, y0, r, rgb, alpha);
                if (x0 == x1 && y0 == y1) break;
                int e2 = 2 * err;
                if (e2 > -dy) { err -= dy; x0 += sx; }
                if (e2 < dx) { err += dx; y0 += sy; }
            }
        }

        // Njord's DrawCircle: every texel within r of the centre is SET (not blended), clipped to the box.
        private static void Disc(byte[] px, int cx, int cy, int r, byte rgb, byte alpha)
        {
            for (int y = -r; y <= r; y++)
                for (int x = -r; x <= r; x++)
                {
                    if (x * x + y * y > r * r) continue;
                    int X = cx + x, Y = cy + y;
                    if (X < 0 || X >= Size || Y < 0 || Y >= Size) continue;
                    int o = (Y * Size + X) * 4;
                    px[o] = rgb; px[o + 1] = rgb; px[o + 2] = rgb; px[o + 3] = alpha;
                }
        }
    }
}
