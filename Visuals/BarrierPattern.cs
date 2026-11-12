using System;

namespace TerroritySmellatory
{
    // 0.3.0: the runic barrier's procedural look as plain numbers - no Unity types, no Mathf - so the off-game harness
    // tests it (tests/BarrierLookTests.cs) and a net8 console tool renders the very same numbers to PNG before the game
    // ever draws them (MASTER_IMPLEMENTATIONS pattern 11). Visuals/BarrierPatch.cs only turns these numbers into a Mesh.
    //
    // THE LOOK, in three layers, cheapest first:
    //   1. A TILEABLE TEXTURE (Pixel / TextureBytes): a pointy-top hexagon lattice - bright line on every cell edge, a
    //      faint fill and a rim glow inside - with Elder Futhark runes carved into about half the cells. Baked once,
    //      256 px square, the game never touches it again. Wakanda's shield, in one repeating tile.
    //   2. A COARSE GRID MESH (MakeLook / VertexPos / Triangles) standing on the territory's edge. Its vertices carry
    //      ALL the animation: Shade() gives each one a colour every frame, and the stock particle shader multiplies
    //      that into the texture. No shader ships (a custom one turns pink in Valheim), and nothing is allocated per
    //      frame because only the vertex colours change.
    //   3. THE FIELD (Field): the impact's white-hot flash, the ripple ring that spreads from it over RingTime, the
    //      afterglow of every cell the ring has passed, and the fade to nothing by Lifetime.
    //
    // Everything the eye reads as "cells lighting up as the wave passes" is the vertex field sliding over the fixed
    // lattice, so the ripple's shape costs one Bump() per vertex per frame and nothing else.
    internal static class BarrierPattern
    {
        // ─────────────────────────────────────────────────────────────
        // THE LATTICE (metres, in the wall's own plane; y is up)
        // ─────────────────────────────────────────────────────────────

        /// <summary>Hex circumradius (centre to corner). 0.42 m makes a cell 0.73 m across: big enough to carve a rune in, small enough that a 6 m patch shows a dozen across.</summary>
        internal const float CellR = 0.42f;
        private const float Sqrt3 = 1.7320508f;
        internal const float ColStep = Sqrt3 * CellR;       // distance between neighbouring centres in a row
        internal const float RowStep = 1.5f * CellR;        // distance between rows
        internal const float Apothem = 0.5f * Sqrt3 * CellR; // centre to edge

        /// <summary>The texture holds 4 columns by 4 rows of cells: the lattice repeats every 2 rows, so 4 is a whole number of periods and the tile wraps in both directions.</summary>
        internal const int TileCols = 4, TileRows = 4;
        internal const float TileW = TileCols * ColStep, TileH = TileRows * RowStep;
        internal const int TexSize = 256;

        /// <summary>
        /// The hex cell holding (x, y): axial cell (q, r), its centre, and how far INSIDE the cell the point is from its
        /// nearest edge (0 on an edge, <see cref="Apothem"/> at the centre). Cube rounding, then a hexagon's distance as
        /// its three edge-normal projections.
        /// </summary>
        internal static void Cell(float x, float y, out int q, out int r, out float cx, out float cy, out float edge)
        {
            float fq = (Sqrt3 / 3f * x - y / 3f) / CellR;
            float fr = (2f / 3f * y) / CellR;
            float fs = -fq - fr;
            float rq = (float)Math.Floor(fq + 0.5f), rr = (float)Math.Floor(fr + 0.5f), rs = (float)Math.Floor(fs + 0.5f);
            float dq = Math.Abs(rq - fq), dr = Math.Abs(rr - fr), ds = Math.Abs(rs - fs);
            // Rounding each axis alone can land on a cell that is not a neighbour; the axis that was rounded the most is
            // the one to rebuild from the other two.
            if (dq > dr && dq > ds) rq = -rr - rs;
            else if (dr > ds) rr = -rq - rs;
            q = (int)rq;
            r = (int)rr;
            cx = CellR * Sqrt3 * (q + r * 0.5f);
            cy = CellR * 1.5f * r;
            float dx = x - cx, dy = y - cy;
            float a = Math.Abs(dx);
            float b = Math.Abs(0.5f * dx + 0.8660254f * dy);
            float c = Math.Abs(-0.5f * dx + 0.8660254f * dy);
            edge = Apothem - Math.Max(a, Math.Max(b, c));
        }

        /// <summary>A cell's place in the tile, as (column 0-3, row 0-3): the same pair on both sides of the tile's wrap, which is what makes the runes tile too.</summary>
        internal static int TileSlot(int q, int r)
        {
            int oddRowShift = (r - (r & 1)) / 2;        // "odd-r" offset column, exact for negative r too
            return Mod(r, TileRows) * TileCols + Mod(q + oddRowShift, TileCols);
        }

        private static int Mod(int a, int n) => ((a % n) + n) % n;

        // ─────────────────────────────────────────────────────────────
        // RUNES (Elder Futhark, as line segments)
        // ─────────────────────────────────────────────────────────────

        /// <summary>How many different runes there are.</summary>
        internal const int GlyphCount = 12;

        // The strokes are the workspace's own hand-tuned rune coordinates, verbatim (Runic/VFX/RunicRuneTexture.cs, which
        // took them from TortalPortal, WingsoftheValkyrie and Njord's rune trail): a 128 x 128 design box, y up, four
        // numbers per line segment. Only the twelve that read best as carved stone are used here.
        private static readonly int[][] Glyphs =
        {
            new[] { 20, 20, 108, 108, 108, 20, 20, 108 },                                                     // 0  gebo
            new[] { 64, 16, 64, 112, 64, 64, 32, 96, 64, 64, 96, 96 },                                          // 1  algiz
            new[] { 40, 20, 40, 108, 40, 96, 96, 112, 40, 72, 96, 88 },                                         // 2  fehu
            new[] { 40, 20, 40, 112, 40, 96, 88, 112, 88, 112, 88, 80, 88, 80, 40, 96 },                        // 3  raidho
            new[] { 64, 16, 64, 112 },                                                                          // 4  isa
            new[] { 64, 16, 64, 112, 64, 40, 32, 72, 64, 40, 96, 72 },                                          // 5  tiwaz
            new[] { 36, 16, 36, 112, 92, 16, 92, 112, 36, 36, 92, 92 },                                         // 6  hagalaz
            new[] { 80, 20, 40, 20, 80, 20, 40, 64, 40, 64, 80, 108, 80, 108, 40, 108 },                        // 7  sowilo
            new[] { 64, 16, 64, 112, 96, 96, 32, 32 },                                                          // 8  nauthiz
            new[] { 32, 48, 32, 112, 96, 48, 96, 112, 32, 48, 64, 16, 64, 16, 96, 48 },                         // 9  uruz
            new[] { 48, 16, 48, 112, 48, 80, 96, 48 },                                                          // 10 laguz
            new[] { 64, 16, 108, 64, 108, 64, 64, 112, 64, 112, 20, 64, 20, 64, 64, 16 },                       // 11 othala-diamond
        };

        /// <summary>The rune size on the wall: the 128 box is this many metres a side. 0.62 keeps the outermost stroke and its glow inside a 0.42 m cell.</summary>
        internal const float GlyphSide = 0.62f;

        // Which cell of the 4 x 4 tile carries which rune; -1 is a plain cell. Six runes, no two alike, scattered so
        // that no carved cell has more than two carved neighbours (a run of them would read as a band). Runes that
        // never touch at all would cap at four per tile, which looked bare in the previews.
        private static readonly sbyte[] TileGlyph =
        {
            -1,  3, -1,  8,
             5, -1, 10, -1,
            -1,  0, -1, -1,
            -1, -1,  6, -1,
        };

        /// <summary>The rune carved in the cell at tile slot <paramref name="slot"/>, or -1.</summary>
        internal static int GlyphAt(int slot) => TileGlyph[slot];

        /// <summary>
        /// The rune at offset (lx, ly) metres from its cell's centre, 0..1: a bright core with a soft halo, from the
        /// distance to the nearest stroke. Zero for a cell with no rune.
        /// </summary>
        internal static float RuneCoverage(int glyph, float lx, float ly)
        {
            if (glyph < 0 || glyph >= GlyphCount) return 0f;
            int[] s = Glyphs[glyph];
            float best = float.MaxValue;
            for (int i = 0; i + 3 < s.Length; i += 4)
            {
                float ax = (s[i] / 128f - 0.5f) * GlyphSide, ay = (s[i + 1] / 128f - 0.5f) * GlyphSide;
                float bx = (s[i + 2] / 128f - 0.5f) * GlyphSide, by = (s[i + 3] / 128f - 0.5f) * GlyphSide;
                float d = SegmentDistance(lx, ly, ax, ay, bx, by);
                if (d < best) best = d;
            }
            float core = 1f - Smooth(0.012f, 0.030f, best);
            float halo = 0.30f * (1f - Smooth(0.03f, 0.075f, best));
            return Math.Max(core, halo);
        }

        /// <summary>The farthest any stroke of <paramref name="glyph"/> reaches from the cell centre, in metres (the tests keep it inside the cell).</summary>
        internal static float GlyphReach(int glyph)
        {
            float far = 0f;
            int[] s = Glyphs[glyph];
            for (int i = 0; i + 1 < s.Length; i += 2)
            {
                float x = (s[i] / 128f - 0.5f) * GlyphSide, y = (s[i + 1] / 128f - 0.5f) * GlyphSide;
                float a = Math.Abs(x), b = Math.Abs(0.5f * x + 0.8660254f * y), c = Math.Abs(-0.5f * x + 0.8660254f * y);
                far = Math.Max(far, Math.Max(a, Math.Max(b, c)));   // the hexagon's own distance measure: <= Apothem means inside
            }
            return far;
        }

        private static float SegmentDistance(float px, float py, float ax, float ay, float bx, float by)
        {
            float vx = bx - ax, vy = by - ay;
            float len2 = vx * vx + vy * vy;
            float t = len2 <= 1e-9f ? 0f : ((px - ax) * vx + (py - ay) * vy) / len2;
            t = t < 0f ? 0f : t > 1f ? 1f : t;
            float dx = px - (ax + vx * t), dy = py - (ay + vy * t);
            return (float)Math.Sqrt(dx * dx + dy * dy);
        }

        // ─────────────────────────────────────────────────────────────
        // THE TEXTURE
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// One texel of the tile: RGBA, alpha always 255 (the coverage lives in RGB, so an additive shader adds it and an
        /// alpha-blended fallback shows a dark-blue pane rather than a black square). Analytic in the point's position,
        /// with no neighbour lookups, so x = 0 and x = 256 are the same function value and the tile cannot have a seam.
        /// Row 0 is the bottom, as Unity's raw texture data wants it.
        /// </summary>
        internal static void Pixel(int px, int py, out byte r, out byte g, out byte b, out byte a)
        {
            float x = (px + 0.5f) / TexSize * TileW, y = (py + 0.5f) / TexSize * TileH;
            Cell(x, y, out int q, out int rr, out float cx, out float cy, out float e);
            int slot = TileSlot(q, rr);

            // The edge: a hard bright core straddling the border (e is the distance to it from either side, so the
            // profile is symmetric and the two neighbours' halves meet without a step) inside a soft halo.
            float line = 1f - Smooth(0.008f, 0.024f, e);
            float halo = 0.40f * (1f - Smooth(0.02f, 0.09f, e));
            // A second, fainter hexagon a hand's breadth inside, the double outline of a glass pane.
            float inner = 0.26f * Bump(Math.Abs(e - 0.085f) / 0.012f);
            // A cool bevel glow that gathers toward the rim, and a per-cell base so the panes are not all one grey.
            float rim = 0.24f * (1f - Smooth(0f, 0.17f, e));
            float baseFill = 0.16f + 0.05f * Hash(slot, 17);

            float lit = Math.Max(line, halo) + inner + rim + baseFill;
            float rune = RuneCoverage(TileGlyph[slot], x - cx, y - cy);
            lit = Math.Max(lit, rune);
            if (lit > 1f) lit = 1f;

            // Fill is a pale sky blue, edges and runes go toward white: the vertex colour then paints the real blue on top.
            float white = Math.Max(line, rune);
            float tr = Lerp(0.55f, 0.92f, white), tg = Lerp(0.82f, 0.98f, white);
            r = ToByte(tr * lit);
            g = ToByte(tg * lit);
            b = ToByte(lit);
            a = 255;
        }

        /// <summary>
        /// The whole tile as RGBA32 bytes, row 0 first. The game copies them into a Color32[] for SetPixels32 + Apply(true):
        /// never LoadRawTextureData, which wants the mip chain's bytes too and threw on 0.3.0's first flare.
        /// </summary>
        internal static byte[] TextureBytes()
        {
            var data = new byte[TexSize * TexSize * 4];
            int o = 0;
            for (int y = 0; y < TexSize; y++)
                for (int x = 0; x < TexSize; x++)
                {
                    Pixel(x, y, out byte r, out byte g, out byte b, out byte a);
                    data[o++] = r; data[o++] = g; data[o++] = b; data[o++] = a;
                }
            return data;
        }

        // ─────────────────────────────────────────────────────────────
        // TIME: the envelope, the ripple and the field
        // ─────────────────────────────────────────────────────────────

        /// <summary>Everything is gone by this many seconds after the push.</summary>
        internal const float Lifetime = 1.2f;
        /// <summary>The ripple ring spreads for this long.</summary>
        internal const float RingTime = 0.4f;

        /// <summary>
        /// Overall brightness over time, 0..1: it starts already lit (the flash of the impact is instant, never a fade-in),
        /// holds through the ripple, then eases to exactly 0 at <see cref="Lifetime"/> and stays there.
        /// </summary>
        internal static float Envelope(float t)
        {
            if (!(t >= 0f) || t >= Lifetime) return 0f;
            float attack = 0.45f + 0.55f * Smooth(0f, 0.04f, t);
            float release = 1f - Smooth(0.45f, Lifetime, t);
            return attack * release;
        }

        /// <summary>How far the ripple's reach is, metres, for a push of <paramref name="strength"/> (0-1).</summary>
        internal static float RingReach(float strength) => 1.6f + 1.6f * Clamp01(strength);

        /// <summary>The ripple ring's radius at time <paramref name="t"/>: 0 at the push, out to <see cref="RingReach"/> over RingTime (a little faster at first), and it stays there.</summary>
        internal static float RingRadius(float t, float strength)
        {
            float x = Clamp01(t / RingTime);
            return RingReach(strength) * x * (1.3f - 0.3f * x);   // a gentle ease-out: a hard one spends the ring's life at the patch's faded rim
        }

        /// <summary>The ring's own brightness, 1 at the push falling to 0 as it reaches its full radius (a slow start, a quick end), and 0 from then on.</summary>
        internal static float RingAmplitude(float t)
        {
            if (!(t >= 0f) || t >= RingTime) return 0f;
            float x = t / RingTime;
            return 1f - x * x;
        }

        /// <summary>
        /// The brightness and whiteness of the wall <paramref name="d"/> metres from the point of impact, at time
        /// <paramref name="t"/>. intensity is 0..1 before the patch's own edge fade; heat is 0 (the barrier's blue) to 1 (white-hot).
        /// </summary>
        internal static void Field(in Look k, float d, float t, out float intensity, out float heat)
        {
            intensity = 0f;
            heat = 0f;
            float env = Envelope(t);
            if (env <= 0f) return;

            float reach = RingReach(k.Strength);
            float r = RingRadius(t, k.Strength);
            float band = 0.42f + 0.16f * k.Strength;                       // the ring's half-width

            float ring = RingAmplitude(t) * Bump(Math.Abs(d - r) / band);

            // Every cell the ring has passed keeps glowing and cools: the moment it passed is the time the ring's
            // (ease-out) radius reached it, so the afterglow follows the front outward.
            float glow = 0f;
            if (d < reach)
            {
                // the inverse of RingRadius: x * (1.3 - 0.3 x) = d / reach
                float passedAt = RingTime * (1.3f - (float)Math.Sqrt(1.69f - 1.2f * Clamp01(d / reach))) / 0.6f;
                float age = t - passedAt;
                if (age > 0f) glow = 0.34f * (1f - Smooth(0f, 0.75f, age)) * (1f - 0.5f * d / reach);
            }
            float behind = Smooth(0f, 0.3f, r - d);                       // soft edge on the front, no step
            glow *= behind;

            // The struck spot: a hot flash that dies in a quarter second.
            float flash = 1.4f * Bump(d / (0.55f + 0.4f * k.Strength)) * (1f - Smooth(0f, 0.30f, t));
            // The patch as a whole, faintly, so the wall is felt to be there and not only where the wave is.
            float sheen = 0.10f * Bump(d / (reach * 1.35f));

            intensity = Clamp01((sheen + 1.5f * ring + glow + flash) * env * k.Gain);
            heat = Clamp01(0.85f * flash + 0.6f * ring + 0.15f * glow);
        }

        // ─────────────────────────────────────────────────────────────
        // THE MESH: one flare's grid, and its per-vertex colour
        // ─────────────────────────────────────────────────────────────

        /// <summary>The shader families' blend maths, which decide what goes in a vertex colour.</summary>
        internal const int ModeAdditiveAlpha = 0;   // Blend SrcAlpha One: the shader adds colour * alpha, so alpha carries the brightness
        internal const int ModeAdditiveOne = 1;     // Blend One One: the shader adds colour, so the colour carries the brightness
        internal const int ModeBlend = 2;           // ordinary alpha blending: a translucent glowing pane

        internal const int MaxNx = 40, MaxNy = 50;
        internal const int MaxVertices = (MaxNx + 1) * (MaxNy + 1);
        internal const int MaxIndices = MaxNx * MaxNy * 12;   // two triangles a quad, and again reversed for the back face

        /// <summary>One flare's size and grid. A plain value: the game keeps one per pooled slot.</summary>
        internal struct Look
        {
            public float Width, Height;       // the patch, metres
            public float V0;                  // where the patch's bottom edge sits, relative to the ground at the contact point
            public float ImpactV;             // height of the struck point above the ground
            public float EllipseA, EllipseB, EllipseV;   // the patch's soft-edged oval: half-width, half-height, centre height
            public float Strength, Gain;
            public float OffX, OffY;          // where in the tile this flare's texture starts, so no two flares carve the same runes at the same spot
            public int Nx, Ny;                // grid quads across and up
        }

        /// <summary>The patch dips this far below the contact point, so on ground that falls away the wall does not float.</summary>
        internal const float Skirt = 0.6f;

        /// <summary>
        /// A flare's dimensions from what pushed: about 6 m wide and max(3, height * 1.6) m tall, growing with strength;
        /// brighter with strength too. <paramref name="seed"/> only varies where in the tile the runes fall. Sanitises its
        /// inputs: a NaN or absurd height or strength still gives a drawable flare.
        /// </summary>
        internal static Look MakeLook(float height, float strength, int seed)
        {
            float s = Clamp01(strength);
            float h = float.IsNaN(height) || float.IsInfinity(height) ? 1.8f : Math.Max(0.5f, Math.Min(height, 14f));
            var k = new Look();
            k.Strength = s;
            k.Gain = 0.75f + 0.5f * s;
            k.Width = 6f * (0.8f + 0.4f * s);
            k.Height = Math.Min(11f, Math.Max(3f, h * 1.6f) * (1f + 0.25f * s));   // never under 3 m: a harder push only adds
            k.V0 = -Skirt;
            k.EllipseA = k.Width * 0.5f;
            k.EllipseB = k.Height * 0.5f;
            k.EllipseV = k.V0 + k.EllipseB;
            k.ImpactV = Math.Max(0.5f, Math.Min(h * 0.5f, k.V0 + k.Height - 0.5f));
            k.OffX = Hash(seed, 1) * TileW;
            k.OffY = Hash(seed, 2) * TileH;
            k.Nx = Math.Max(4, Math.Min(MaxNx, (int)Math.Ceiling(k.Width / 0.2f)));
            k.Ny = Math.Max(4, Math.Min(MaxNy, (int)Math.Ceiling(k.Height / 0.22f)));
            return k;
        }

        internal static int VertexCount(in Look k) => (k.Nx + 1) * (k.Ny + 1);

        /// <summary>Vertex (i, j)'s place on the wall: u across (centred on the contact point), v up from the ground.</summary>
        internal static void VertexPos(in Look k, int i, int j, out float u, out float v)
        {
            u = -k.Width * 0.5f + k.Width * i / k.Nx;
            v = k.V0 + k.Height * j / k.Ny;
        }

        /// <summary>Where the texture is sampled for a vertex: the wall's metres over the tile's size, plus this flare's offset. The texture repeats.</summary>
        internal static void VertexUv(in Look k, float u, float v, out float tu, out float tv)
        {
            tu = (u + k.OffX) / TileW;
            tv = (v + k.OffY) / TileH;
        }

        /// <summary>
        /// What never changes for a vertex over a flare's life, worked out once when the flare is laid out: its distance
        /// from the impact, its share of the patch's oval edge fade (0 outside the oval, so the vertex is skipped every
        /// frame after), and a per-cell number for the shimmer.
        /// </summary>
        internal static void Precompute(in Look k, float u, float v, out float distance, out float maskGain, out int cellSeed)
        {
            float dv = v - k.ImpactV;
            distance = (float)Math.Sqrt(u * u + dv * dv);
            float ru = u / k.EllipseA, rv = (v - k.EllipseV) / k.EllipseB;
            float rho = (float)Math.Sqrt(ru * ru + rv * rv);
            maskGain = 1f - Smooth(0.68f, 1f, rho);
            Cell(u + k.OffX, v + k.OffY, out int q, out int r, out _, out _, out _);
            cellSeed = q * 7919 + r * 104729;
        }

        /// <summary>
        /// A vertex's colour at time <paramref name="t"/>, for the shader family <paramref name="mode"/>. The colour runs
        /// from the barrier's blue (#3FA9FF) to white as the spot gets hotter; the brightness rides in alpha or in the
        /// colour, whichever the shader's blend multiplies by, so both additive families draw the SAME picture.
        /// </summary>
        internal static void Shade(in Look k, float distance, float maskGain, int cellSeed, float t, int mode,
                                   out byte r, out byte g, out byte b, out byte a)
        {
            r = g = b = a = 0;
            if (maskGain <= 0f) return;
            Field(in k, distance, t, out float intensity, out float heat);
            if (intensity <= 0f) return;

            // A quiet crackle: each cell's own brightness jitters ~20 times a second, so the lit lattice shimmers like energy.
            float flicker = 1f + 0.36f * (Hash(cellSeed, (int)(t * 20f)) - 0.5f);
            float I = Clamp01(intensity * maskGain * flicker);

            float cr = Lerp(0.247f, 0.93f, heat), cg = Lerp(0.663f, 0.97f, heat), cb = 1f;   // #3FA9FF is (0.247, 0.663, 1.0)
            switch (mode)
            {
                case ModeAdditiveOne:
                    r = ToByte(cr * I); g = ToByte(cg * I); b = ToByte(cb * I); a = 255;
                    break;
                case ModeBlend:
                    r = ToByte(cr); g = ToByte(cg); b = ToByte(cb); a = ToByte(I * 0.85f);
                    break;
                default:   // ModeAdditiveAlpha
                    r = ToByte(cr); g = ToByte(cg); b = ToByte(cb); a = ToByte(I);
                    break;
            }
        }

        /// <summary>
        /// The grid's triangles into <paramref name="dst"/> (vertex (i, j) is j * (Nx + 1) + i), every quad once facing
        /// out and once reversed, so the wall shows from both sides whatever the shader does about culling. Returns the
        /// index count.
        /// </summary>
        internal static int Triangles(int nx, int ny, int[] dst)
        {
            int n = 0, stride = nx + 1;
            for (int j = 0; j < ny; j++)
                for (int i = 0; i < nx; i++)
                {
                    int a = j * stride + i, b = a + 1, c = a + stride, d = c + 1;
                    dst[n++] = a; dst[n++] = c; dst[n++] = b;
                    dst[n++] = b; dst[n++] = c; dst[n++] = d;
                    dst[n++] = a; dst[n++] = b; dst[n++] = c;
                    dst[n++] = b; dst[n++] = d; dst[n++] = c;
                }
            return n;
        }

        // ─────────────────────────────────────────────────────────────
        // THE RUNE BURST (0.3.1: Njord's signature, the burst of its helm-claim)
        // ─────────────────────────────────────────────────────────────

        /// <summary>A rune sprite lives this long, rising from the impact; a little under the wall's own lifetime plus a beat, so the last one fades just after the wall is gone.</summary>
        internal const float RuneLifetime = 1.5f;

        /// <summary>How many runes one burst throws: 3 for a light touch up to 6 for a slam.</summary>
        internal static int RuneCount(float strength) => 3 + (int)(Clamp01(strength) * 3.999f);

        /// <summary>A rune's size on screen, metres across its 128 px box: 0.45 for a touch to 0.7 for a slam.</summary>
        internal static float RuneSize(float strength) => 0.45f + 0.25f * Clamp01(strength);

        /// <summary>
        /// Njord's dissolve, unchanged: full brightness for the first 30 % of the life, then an ease-in fade (slow start,
        /// fast end) to exactly 0 at t = 1. <paramref name="life01"/> is age over lifetime. No shimmer here, see <see cref="RuneShimmer"/>.
        /// </summary>
        internal static float RuneFade(float life01)
        {
            if (!(life01 > 0.3f)) return 1f;
            if (life01 >= 1f) return 0f;
            float f = (life01 - 0.3f) / 0.7f;
            return 1f - f * f;
        }

        /// <summary>Njord's shimmer pulse on the fade, about 3 flickers a second, ±28 %. It only ever runs while the rune is fading (the first 30 % stays steady).</summary>
        internal static float RuneShimmer(float ageSeconds, float life01) =>
            life01 <= 0.3f ? 1f : 1f + 0.28f * (float)Math.Sin(ageSeconds * 18f);

        /// <summary>Njord's scale: full size for 30 % of the life, then shrinking to nothing, so a rune vanishes rather than dwindling to a dot.</summary>
        internal static float RuneScale(float life01)
        {
            if (!(life01 > 0.3f)) return 1f;
            if (life01 >= 1f) return 0f;
            return 1f - (life01 - 0.3f) / 0.7f;
        }

        /// <summary>
        /// A rune's colour and brightness for the shader family <paramref name="mode"/>, as the sprite's tint: the barrier's
        /// blue washed a third of the way to white (a rune is the hottest thing on the wall). Additive-one shaders add the
        /// colour, so brightness rides in the colour; the others multiply by alpha.
        /// </summary>
        internal static void RuneTint(float brightness, int mode, out float r, out float g, out float b, out float a)
        {
            float k = Clamp01(brightness);
            float cr = Lerp(0.247f, 1f, 0.34f), cg = Lerp(0.663f, 1f, 0.34f), cb = 1f;
            if (mode == ModeAdditiveOne) { r = cr * k; g = cg * k; b = cb * k; a = 1f; }
            else { r = cr; g = cg; b = cb; a = k; }
        }

        // ─────────────────────────────────────────────────────────────
        // small maths, kept local so this file needs nothing from Unity
        // ─────────────────────────────────────────────────────────────

        internal static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : (float.IsNaN(v) ? 0f : v);
        private static float Lerp(float a, float b, float t) => a + (b - a) * t;

        /// <summary>The usual smoothstep: 0 below a, 1 above b, an S between.</summary>
        internal static float Smooth(float a, float b, float x)
        {
            float t = Clamp01((x - a) / (b - a));
            return t * t * (3f - 2f * t);
        }

        /// <summary>A soft bump: 1 at x = 0 falling smoothly to exactly 0 at |x| = 1 and staying 0 (cheap, and it ends, unlike a Gaussian).</summary>
        internal static float Bump(float x)
        {
            x = Math.Abs(x);
            if (!(x < 1f)) return 0f;
            float s = 1f - x * x;
            return s * s;
        }

        private static byte ToByte(float v) => (byte)(Clamp01(v) * 255f + 0.5f);

        /// <summary>A stable hash of two ints to 0..1, the same on every runtime (no System.Random, no string hashes).</summary>
        internal static float Hash(int a, int b)
        {
            unchecked
            {
                uint h = (uint)a * 0x9E3779B1u ^ (uint)b * 0x85EBCA6Bu;
                h ^= h >> 15; h *= 0x2C1B3C6Du;
                h ^= h >> 12; h *= 0x297A2D39u;
                h ^= h >> 15;
                return (h & 0xFFFFFF) / 16777216f;
            }
        }
    }
}
