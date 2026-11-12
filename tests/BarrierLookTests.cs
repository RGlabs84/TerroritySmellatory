using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;

namespace TerroritySmellatory.Tests
{
    // 0.3.0: the runic barrier's LOOK (Visuals/BarrierPattern.cs) - the numbers behind the flare, checked without a
    // graphics device: the same every time, in range, a ripple that grows and then is gone, runes that stay inside
    // their cells, and a lattice that tiles with no seam. What it looks like is judged on the PNG previews, not here;
    // what these guard is the arithmetic a wrong constant would break silently (a NaN colour, a ring that never ends,
    // a rune cut in half by a cell edge).
    internal static partial class Program
    {
        static partial void BarrierLookGroups()
        {
            Run("barrier look: the texture and the flare are the same every time", LookDeterministic);
            Run("barrier look: colours, alpha and the field stay in range and finite, whatever comes in", LookInRange);
            Run("barrier look: the ripple grows, fades and is gone after the lifetime", LookRipple);
            Run("barrier look: every rune sits inside its cell, on its own, none repeated", LookRunes);
            Run("barrier look: the lattice tiles with no seam and its edge distance is continuous", LookLattice);
            Run("barrier look: the mesh - sizes by strength and height, valid triangles, UVs that repeat", LookMesh);
            Run("barrier look: Njord's rune burst - count and size by strength, the dissolve curve, the tint per shader family", LookRuneBurst);
            Run("barrier look: Njord's 16 rune glyphs - each drawn, inside its box, glow under stroke, the same every time", LookGlyphs);
            Run("barrier look: no texture is ever uploaded raw (the 0.3.0 bug: LoadRawTextureData with a mip chain)", LookNoRawUploads);
        }

        // 0.3.1: the rune burst's plain numbers (Njord's helm-claim burst, re-timed for a wall that lives 1.2 s).
        private static void LookRuneBurst()
        {
            Check(BarrierPattern.RuneCount(0f) == 3 && BarrierPattern.RuneCount(1f) == 6, "a touch throws 3 runes and a slam 6");
            int prev = 0;
            for (float st = 0f; st <= 1f; st += 0.05f)
            {
                int c = BarrierPattern.RuneCount(st);
                Check(c >= prev && c >= 3 && c <= 6, "the count only grows with strength and stays within 3-6");
                prev = c;
            }
            Check(BarrierPattern.RuneCount(float.NaN) >= 3 && BarrierPattern.RuneCount(-4f) == 3 && BarrierPattern.RuneCount(9f) == 6, "a NaN or absurd strength still gives a sane burst");
            Check(BarrierPattern.RuneSize(1f) > BarrierPattern.RuneSize(0f) && BarrierPattern.RuneSize(0f) > 0.3f, "a harder push throws bigger runes, none of them tiny");

            // Njord's curve: steady for 30 % of the life, then an ease-in fade that ends at exactly 0.
            Near(BarrierPattern.RuneFade(0f), 1f, "a rune starts at full brightness (never a fade-in)", 1e-6f);
            Near(BarrierPattern.RuneFade(0.3f), 1f, "and holds it to 30 % of its life", 1e-6f);
            Near(BarrierPattern.RuneFade(1f), 0f, "and is out at the end of it", 1e-6f);
            Near(BarrierPattern.RuneFade(2f), 0f, "and stays out", 1e-6f);
            float last = 2f;
            bool mono = true, inRange = true;
            for (float t = 0f; t <= 1.0001f; t += 0.01f)
            {
                float f = BarrierPattern.RuneFade(t);
                mono &= f <= last + 1e-6f;
                inRange &= f >= 0f && f <= 1f;
                last = f;
            }
            Check(mono && inRange, "the fade never brightens again and stays 0-1");
            Near(BarrierPattern.RuneFade(0.65f), 1f - 0.5f * 0.5f, "half way through the dissolve window it has lost a quarter (ease-in)", 1e-5f);
            Near(BarrierPattern.RuneScale(0f), 1f, "a rune starts full size", 1e-6f);
            Near(BarrierPattern.RuneScale(1f), 0f, "and dissolves to nothing, not to a dot", 1e-6f);
            Near(BarrierPattern.RuneScale(0.65f), 0.5f, "shrinking linearly through the dissolve window", 1e-5f);
            Near(BarrierPattern.RuneShimmer(0.1f, 0.2f), 1f, "no shimmer while it is still at full brightness", 1e-6f);
            bool shimmerOk = true;
            for (float a = 0f; a < 2f; a += 0.013f) shimmerOk &= Math.Abs(BarrierPattern.RuneShimmer(a, 0.6f) - 1f) <= 0.28f + 1e-5f;
            Check(shimmerOk, "and the shimmer is within +-28 % after");
            Check(BarrierPattern.RuneLifetime > BarrierPattern.Lifetime, "the last rune outlasts the wall by a beat");

            foreach (int mode in new[] { BarrierPattern.ModeAdditiveOne, BarrierPattern.ModeAdditiveAlpha, BarrierPattern.ModeBlend })
                foreach (float k in new[] { 0f, 0.4f, 1f, 3f, -1f, float.NaN })
                {
                    BarrierPattern.RuneTint(k, mode, out float r, out float g, out float b, out float a);
                    Check(r >= 0f && r <= 1f && g >= 0f && g <= 1f && b >= 0f && b <= 1f && a >= 0f && a <= 1f && Fin(r) && Fin(g) && Fin(b) && Fin(a), "a rune's tint is a valid colour, whatever the brightness");
                }
            BarrierPattern.RuneTint(0f, BarrierPattern.ModeAdditiveOne, out float r0, out float g0, out float b0, out _);
            Check(r0 == 0f && g0 == 0f && b0 == 0f, "an additive-one rune at zero brightness adds nothing (its brightness is in its colour)");
            BarrierPattern.RuneTint(0.5f, BarrierPattern.ModeAdditiveAlpha, out float r1, out float g1, out float b1, out float a1);
            Near(a1, 0.5f, "an alpha-driven rune carries its brightness in alpha", 1e-6f);
            Check(b1 >= g1 && g1 >= r1 && b1 > 0.9f, "the rune is the barrier's blue washed toward white");
        }

        // 0.3.1: Njord's glyph generator, as bytes.
        private static void LookGlyphs()
        {
            const int size = BarrierRuneGlyphs.Size;
            var px = new byte[size * size * 4];
            var again = new byte[size * size * 4];
            var seen = new System.Collections.Generic.HashSet<string>();
            for (int g = 0; g < BarrierRuneGlyphs.Count; g++)
            {
                int lit = BarrierRuneGlyphs.Draw(g, px);
                Check(lit > 400 && lit < size * size / 2, $"glyph {g} draws something, and not the whole box ({lit} texels)");
                BarrierRuneGlyphs.Extent(g, out int lo, out int hi);
                Check(lo >= 0 && hi <= size - 1, $"glyph {g} fits its box, glow discs included ({lo}..{hi})");
                Check(BarrierRuneGlyphs.Segments(g) >= 1, $"glyph {g} has segments");

                int glow = 0, full = 0;
                bool tidy = true;
                for (int i = 0; i < px.Length; i += 4)
                {
                    byte a = px[i + 3];
                    if (a == 153) glow++;
                    else if (a == 255) full++;
                    tidy &= (a == 0 || a == 153 || a == 255) && px[i] == a && px[i + 1] == a && px[i + 2] == a;   // Njord's texels: transparent black, the 60 % glow, the full stroke
                }
                Check(tidy, $"glyph {g} has only Njord's three texel kinds");
                Check(glow > 0 && full > 0 && glow > full / 4, $"glyph {g} has a glow AND a stroke ({glow} glow, {full} stroke)");

                BarrierRuneGlyphs.Draw(g, again);
                Check(px.SequenceEqual(again), $"glyph {g} draws the same every time");
                Check(seen.Add(Convert.ToBase64String(px)), $"glyph {g} is not a copy of another");
            }

            // Parity with Njord's own switch: isa (index 4) is one vertical bar, glow radius 8 and stroke radius 4, at x = 64, y 16..112.
            BarrierRuneGlyphs.Draw(4, px);
            byte At(int x, int y) => px[(y * size + x) * 4 + 3];
            Check(At(64, 64) == 255 && At(68, 64) == 255 && At(69, 64) == 153 && At(72, 64) == 153 && At(73, 64) == 0, "isa: stroke to 4 texels from its spine, glow to 8, then nothing, as Njord draws it");
            Check(At(64, 10) == 153 && At(64, 120) == 153 && At(64, 4) == 0, "and its glow disc caps the ends");
            Check(At(64, 64) == At(60, 64) && At(64, 64) == At(64 - 4, 64), "and is symmetric about the spine");

            // Out-of-range indices fall to Njord's default glyph instead of throwing; a wrong-size buffer is refused.
            Check(BarrierRuneGlyphs.Draw(99, px) > 0 && BarrierRuneGlyphs.Draw(-1, px) > 0, "an out-of-range glyph index draws the default rune");
            bool threw = false;
            try { BarrierRuneGlyphs.Draw(0, new byte[10]); } catch (ArgumentException) { threw = true; }
            Check(threw, "a buffer of the wrong size is refused");
        }

        // 0.3.0's flare threw on its first call - Texture2D.LoadRawTextureData: not enough data provided (will result in
        // overread) - because the texture had a mip chain and only the top level's bytes were given. The unit harness cannot
        // run Unity, so this is the guard it can have: no line of code in the barrier's Visuals files uploads raw bytes.
        private static string ThisFile([CallerFilePath] string path = "") => path;

        private static void LookNoRawUploads()
        {
            string here = ThisFile();
            string dir = Path.Combine(Path.GetDirectoryName(here) ?? ".", "..", "Visuals");
            string[] files = Directory.Exists(dir) ? Directory.GetFiles(dir, "Barrier*.cs") : new string[0];
            Check(files.Length >= 5, $"the barrier's Visuals files were found ({files.Length} in {dir})");
            bool sawPixels32 = false;
            foreach (string f in files)
            {
                foreach (string raw in File.ReadAllLines(f))
                {
                    string code = raw.Contains("//") ? raw.Substring(0, raw.IndexOf("//", StringComparison.Ordinal)) : raw;
                    Check(!code.Contains("LoadRawTextureData") && !code.Contains("GetRawTextureData"), $"{Path.GetFileName(f)} never touches raw texture bytes: {raw.Trim()}");
                    sawPixels32 |= code.Contains(".SetPixels32(");
                }
            }
            Check(sawPixels32, "the textures are filled with SetPixels32, Njord's way");
            byte[] tex = BarrierPattern.TextureBytes();
            Check(tex.Length == BarrierPattern.TexSize * BarrierPattern.TexSize * 4, "the lattice bytes are exactly the top level (the game copies them into Color32s and lets Apply(true) make the mips)");
        }

        private static void LookDeterministic()
        {
            byte[] a = BarrierPattern.TextureBytes();
            byte[] b = BarrierPattern.TextureBytes();
            Check(a.Length == BarrierPattern.TexSize * BarrierPattern.TexSize * 4, "the texture is 256 x 256 RGBA");
            Check(a.SequenceEqual(b), "two bakes of the texture are byte-identical");

            BarrierPattern.Look x = BarrierPattern.MakeLook(2.5f, 0.6f, 42), y = BarrierPattern.MakeLook(2.5f, 0.6f, 42);
            Check(x.Width == y.Width && x.Height == y.Height && x.OffX == y.OffX && x.OffY == y.OffY && x.Nx == y.Nx && x.Ny == y.Ny && x.Gain == y.Gain,
                "the same push and seed give the same flare");
            BarrierPattern.Look other = BarrierPattern.MakeLook(2.5f, 0.6f, 43);
            Check(other.OffX != x.OffX || other.OffY != x.OffY, "another seed puts the runes somewhere else in the tile");

            bool same = true;
            foreach (int mode in new[] { BarrierPattern.ModeAdditiveAlpha, BarrierPattern.ModeAdditiveOne, BarrierPattern.ModeBlend })
                for (float t = 0f; t < 1.25f; t += 0.05f)
                    for (float d = 0f; d < 5f; d += 0.37f)
                    {
                        BarrierPattern.Shade(in x, d, 0.8f, 12345, t, mode, out byte r1, out byte g1, out byte b1, out byte a1);
                        BarrierPattern.Shade(in y, d, 0.8f, 12345, t, mode, out byte r2, out byte g2, out byte b2, out byte a2);
                        same &= r1 == r2 && g1 == g2 && b1 == b2 && a1 == a2;
                    }
            Check(same, "Shade gives the same bytes for the same inputs, in every shader mode");

            // The texture is not empty, and is not a flat wash either.
            int lit = 0, bright = 0;
            for (int i = 0; i < a.Length; i += 4)
            {
                if (a[i + 2] > 40) lit++;
                if (a[i + 2] > 230) bright++;
                if (a[i + 3] != 255) { Check(false, "every texel is fully opaque (the glow lives in RGB)"); break; }
            }
            Check(lit > a.Length / 4 / 3, $"a good share of the tile carries some light (got {lit} of {a.Length / 4})");
            Check(bright > 500 && bright < a.Length / 4 / 4, $"the bright edges and runes are a small part of it (got {bright})");
        }

        private static void LookInRange()
        {
            // The envelope.
            for (float t = -1f; t <= 3f; t += 0.01f)
            {
                float e = BarrierPattern.Envelope(t);
                Check(e >= 0f && e <= 1f && !float.IsNaN(e), $"envelope at {t} is within 0..1");
            }
            Check(BarrierPattern.Envelope(float.NaN) == 0f, "an envelope for a NaN time is nothing");
            Check(BarrierPattern.Envelope(0f) > 0.3f, "the flare starts already lit - the impact is instant, never a fade-in");

            // The field and every vertex byte, over pushes from a touch to a slam and heights from a mouse to a giant.
            foreach (float strength in new[] { -3f, 0f, 0.2f, 0.5f, 1f, 4f, float.NaN })
                foreach (float height in new[] { -1f, 0f, 0.4f, 1.8f, 4f, 9f, 60f, float.NaN, float.PositiveInfinity })
                {
                    BarrierPattern.Look k = BarrierPattern.MakeLook(height, strength, 7);
                    bool sane = Fin(k.Width) && Fin(k.Height) && Fin(k.Gain) && Fin(k.ImpactV) && Fin(k.OffX) && Fin(k.OffY)
                                && k.Width > 3f && k.Height >= 3f && k.Height <= 11f && k.Nx >= 4 && k.Nx <= BarrierPattern.MaxNx && k.Ny >= 4 && k.Ny <= BarrierPattern.MaxNy
                                && k.ImpactV >= 0.5f && k.ImpactV <= k.V0 + k.Height;
                    Check(sane, $"a flare for height {height}, strength {strength} is finite and sized within the mesh limits");

                    for (float t = 0f; t <= 1.3f; t += 0.025f)
                        for (float d = 0f; d <= 9f; d += 0.3f)
                        {
                            BarrierPattern.Field(in k, d, t, out float intensity, out float heat);
                            if (!(intensity >= 0f && intensity <= 1f && heat >= 0f && heat <= 1f))
                            {
                                Check(false, $"field out of 0..1 at height {height}, strength {strength}, t {t}, d {d}: {intensity}, {heat}");
                                return;
                            }
                        }
                }

            // Mask and distance from Precompute: a share of 0..1, and 0 well outside the oval.
            BarrierPattern.Look m = BarrierPattern.MakeLook(2f, 0.5f, 1);
            bool masks = true;
            for (int j = 0; j <= m.Ny; j++)
                for (int i = 0; i <= m.Nx; i++)
                {
                    BarrierPattern.VertexPos(in m, i, j, out float u, out float v);
                    BarrierPattern.Precompute(in m, u, v, out float dist, out float mask, out _);
                    masks &= mask >= 0f && mask <= 1f && dist >= 0f && !float.IsNaN(dist);
                }
            Check(masks, "every vertex has a distance and an edge-fade share in range");
            BarrierPattern.Precompute(in m, m.Width * 0.5f, m.V0, out _, out float corner, out _);
            Check(corner == 0f, "a corner of the patch, outside its oval, takes no light");
            BarrierPattern.Precompute(in m, 0f, m.ImpactV, out float zero, out float centre, out _);
            Check(zero == 0f && centre == 1f, "the struck point is at distance 0 and takes full light");

            // The bytes: brightness rides where the shader multiplies it, so both additive modes carry the same light.
            BarrierPattern.Shade(in m, 0.2f, 1f, 5, 0.03f, BarrierPattern.ModeAdditiveAlpha, out byte ar, out byte ag, out byte ab, out byte aa);
            BarrierPattern.Shade(in m, 0.2f, 1f, 5, 0.03f, BarrierPattern.ModeAdditiveOne, out byte pr, out byte pg, out byte pb, out byte pa);
            Check(aa > 200, $"at the impact the additive-alpha alpha is near full (got {aa})");
            Check(Math.Abs(ar * aa / 255 - pr) <= 2 && Math.Abs(ag * aa / 255 - pg) <= 2 && Math.Abs(ab * aa / 255 - pb) <= 2 && pa == 255,
                "the SrcAlpha family (colour x alpha) and the One One family (colour carries the light) add the same light");
            Check(ab == 255 && ar < 255 * 0.95f + 1 && ag >= ar, "the colour is the barrier's blue, whiter as it gets hotter, never past white");
            BarrierPattern.Shade(in m, 0.2f, 1f, 5, 0.03f, BarrierPattern.ModeBlend, out _, out _, out _, out byte ba);
            Check(ba > 150 && ba <= 217, $"the alpha-blended fallback stays translucent (got {ba})");
            BarrierPattern.Shade(in m, 0.2f, 0f, 5, 0.03f, BarrierPattern.ModeAdditiveAlpha, out byte zr, out byte zg, out byte zb, out byte za);
            Check(zr == 0 && zg == 0 && zb == 0 && za == 0, "no edge-fade share, no light");
        }

        private static void LookRipple()
        {
            float prev = -1f;
            for (float t = 0f; t <= BarrierPattern.RingTime + 1e-4f; t += 0.02f)
            {
                float r = BarrierPattern.RingRadius(t, 0.5f);
                Check(r > prev, $"the ring's radius grows at t={t:0.00} ({r} after {prev})");
                prev = r;
            }
            Near(BarrierPattern.RingRadius(0f, 0.5f), 0f, "the ring starts at the point of impact");
            Near(BarrierPattern.RingRadius(BarrierPattern.RingTime, 0.5f), BarrierPattern.RingReach(0.5f), "it reaches its full radius when its time is up");
            Near(BarrierPattern.RingRadius(BarrierPattern.RingTime * 3f, 0.5f), BarrierPattern.RingReach(0.5f), "and does not grow past it");
            Check(BarrierPattern.RingReach(1f) > BarrierPattern.RingReach(0.5f) && BarrierPattern.RingReach(0.5f) > BarrierPattern.RingReach(0f),
                "a harder push throws the ring farther");
            Check(BarrierPattern.RingRadius(0.2f, 1f) > BarrierPattern.RingRadius(0.2f, 0f), "and faster");

            float amp = 2f;
            for (float t = 0f; t < BarrierPattern.RingTime; t += 0.02f)
            {
                float a = BarrierPattern.RingAmplitude(t);
                Check(a > 0f && a <= 1f && a < amp, $"the ring dims as it spreads (t={t:0.00}: {a})");
                amp = a;
            }
            Near(BarrierPattern.RingAmplitude(BarrierPattern.RingTime), 0f, "the ring has no light left at the end of its time");
            Near(BarrierPattern.RingAmplitude(1f), 0f, "and none afterwards");

            // The lit spot travels: where the wall is brightest beyond the flash moves outward as time passes.
            BarrierPattern.Look k = BarrierPattern.MakeLook(2f, 0.7f, 3);
            float PeakAt(float t)
            {
                float best = -1f, at = 0f;
                for (float d = 0.9f; d < 6f; d += 0.02f)
                {
                    BarrierPattern.Field(in k, d, t, out float i, out _);
                    if (i > best) { best = i; at = d; }
                }
                return at;
            }
            float p1 = PeakAt(0.15f), p2 = PeakAt(0.3f);
            Check(p2 > p1 + 0.3f, $"the brightest ring of the wall moves outward (0.15 s: {p1:0.00} m, 0.30 s: {p2:0.00} m)");
            float r3 = BarrierPattern.RingRadius(0.3f, k.Strength);
            Check(Math.Abs(p2 - r3) < 0.35f, $"and it is where the ring is ({p2:0.00} m vs radius {r3:0.00} m)");
            BarrierPattern.Field(in k, r3, 0.3f, out float onRing, out _);
            BarrierPattern.Field(in k, r3 + 1.3f, 0.3f, out float ahead, out _);
            BarrierPattern.Field(in k, r3 - 1.3f, 0.3f, out float behind, out _);
            Check(onRing > ahead * 2f && onRing > behind, $"the front is brighter than the wall ahead of it and the afterglow behind it ({onRing:0.00} vs {ahead:0.00} and {behind:0.00})");

            // The flash at the struck spot is hot early and cool by the end.
            BarrierPattern.Field(in k, 0.1f, 0.03f, out float early, out float earlyHeat);
            BarrierPattern.Field(in k, 0.1f, 0.9f, out float late, out float lateHeat);
            Check(early > late && earlyHeat > 0.6f && lateHeat < 0.2f, $"white-hot at first ({early:0.00}, heat {earlyHeat:0.00}), dim blue by 0.9 s ({late:0.00}, heat {lateHeat:0.00})");

            // Gone after the lifetime: not a value in the field, not a byte in the mesh.
            Near(BarrierPattern.Lifetime, 1.2f, "the lifetime is 1.2 s");
            Check(BarrierPattern.Envelope(BarrierPattern.Lifetime - 0.01f) < 0.05f, "the envelope has faded almost to nothing just before the end");
            bool gone = true;
            for (float t = BarrierPattern.Lifetime; t < 3f; t += 0.05f)
                for (float d = 0f; d < 8f; d += 0.25f)
                {
                    BarrierPattern.Field(in k, d, t, out float i, out float h);
                    BarrierPattern.Shade(in k, d, 1f, 99, t, BarrierPattern.ModeAdditiveAlpha, out byte r, out byte g, out byte b, out byte a);
                    gone &= i == 0f && h == 0f && r == 0 && g == 0 && b == 0 && a == 0;
                }
            Check(gone, "from the lifetime on, every point of the wall is exactly dark");
            Check(BarrierPattern.Envelope(-0.5f) == 0f, "and before the push there is nothing");
        }

        private static void LookRunes()
        {
            Check(BarrierPattern.GlyphCount == 12, "twelve runes");
            for (int g = 0; g < BarrierPattern.GlyphCount; g++)
            {
                float reach = BarrierPattern.GlyphReach(g);
                Check(reach + 0.03f < BarrierPattern.Apothem, $"rune {g}'s strokes (reach {reach:0.000} m) leave room for their glow inside a cell (apothem {BarrierPattern.Apothem:0.000})");
                float sum = 0f;
                for (float y = -0.3f; y <= 0.3f; y += 0.01f)
                    for (float x = -0.3f; x <= 0.3f; x += 0.01f)
                        sum += BarrierPattern.RuneCoverage(g, x, y);
                Check(sum > 20f, $"rune {g} actually draws something");
            }
            Check(BarrierPattern.RuneCoverage(-1, 0f, 0f) == 0f && BarrierPattern.RuneCoverage(99, 0f, 0f) == 0f, "no rune, no coverage");

            // The tile: which cells carry a rune.
            var seen = new System.Collections.Generic.HashSet<int>();
            int carved = 0;
            for (int slot = 0; slot < BarrierPattern.TileCols * BarrierPattern.TileRows; slot++)
            {
                int g = BarrierPattern.GlyphAt(slot);
                if (g < 0) continue;
                carved++;
                Check(g < BarrierPattern.GlyphCount, "a tile's rune is one of the twelve");
                Check(seen.Add(g), $"rune {g} is carved once in the tile");
            }
            Check(carved >= 5 && carved <= 9, $"about half the cells of the tile are carved (got {carved} of 16)");

            // Carved cells are scattered, not clumped: none has more than two carved neighbours, across the tile's wrap as well.
            bool scattered = true;
            int[][] around = { new[] { 1, 0 }, new[] { -1, 0 }, new[] { 0, 1 }, new[] { 0, -1 }, new[] { 1, -1 }, new[] { -1, 1 } };
            for (int r = -4; r < 8; r++)
                for (int q = -8; q < 8; q++)
                {
                    if (BarrierPattern.GlyphAt(BarrierPattern.TileSlot(q, r)) < 0) continue;
                    int touching = 0;
                    foreach (int[] n in around)
                        if (BarrierPattern.GlyphAt(BarrierPattern.TileSlot(q + n[0], r + n[1])) >= 0) touching++;
                    if (touching > 2) scattered = false;
                }
            Check(scattered, "no carved cell has more than two carved neighbours, so the runes read as marks and not as a band");

            // Pixel by pixel: where a rune's core is drawn, that texel is inside a carved cell and off its edge lines.
            int strays = 0, coreTexels = 0;
            for (int py = 0; py < BarrierPattern.TexSize; py++)
                for (int px = 0; px < BarrierPattern.TexSize; px++)
                {
                    float x = (px + 0.5f) / BarrierPattern.TexSize * BarrierPattern.TileW, y = (py + 0.5f) / BarrierPattern.TexSize * BarrierPattern.TileH;
                    BarrierPattern.Cell(x, y, out int q, out int r, out float cx, out float cy, out float edge);
                    int g = BarrierPattern.GlyphAt(BarrierPattern.TileSlot(q, r));
                    if (g < 0) continue;
                    float c = BarrierPattern.RuneCoverage(g, x - cx, y - cy);
                    if (c > 0.6f)
                    {
                        coreTexels++;
                        if (edge < 0.05f) strays++;
                    }
                }
            Check(coreTexels > 400, $"the runes' cores cover a fair number of texels (got {coreTexels})");
            Check(strays == 0, $"no rune core reaches within 5 cm of its cell's edge line (got {strays} texels)");
        }

        private static void LookLattice()
        {
            float span = BarrierPattern.TileW;
            Near(BarrierPattern.TileW, BarrierPattern.TileCols * BarrierPattern.ColStep, "the tile is a whole number of columns wide", 1e-5f);
            Near(BarrierPattern.TileH, BarrierPattern.TileRows * BarrierPattern.RowStep, "and of rows tall", 1e-5f);

            // Neighbouring centres are one column apart in a row, and a hexagon's own distance apart across rows.
            BarrierPattern.Cell(0f, 0f, out int q0, out int r0, out float cx0, out float cy0, out _);
            BarrierPattern.Cell(BarrierPattern.ColStep, 0f, out _, out _, out float cx1, out float cy1, out _);
            Near(cx1 - cx0, BarrierPattern.ColStep, "the next cell along a row is one column step away", 1e-4f);
            Near(cy1 - cy0, 0f, "on the same row", 1e-4f);
            BarrierPattern.Cell(0f, BarrierPattern.RowStep, out _, out _, out float cx2, out float cy2, out _);
            Near(cy2 - cy0, BarrierPattern.RowStep, "the next row is one row step up", 1e-4f);
            Near(Math.Abs(cx2 - cx0), BarrierPattern.ColStep / 2f, "and half a column across", 1e-4f);
            Near((float)Math.Sqrt((cx2 - cx0) * (cx2 - cx0) + (cy2 - cy0) * (cy2 - cy0)), BarrierPattern.ColStep, "so all six neighbours are the same distance away", 1e-4f);

            // Shifting a point by the tile in either direction lands on the same cell of the tile at the same depth.
            int mismatched = 0, badEdge = 0;
            for (float y = 0.013f; y < BarrierPattern.TileH; y += 0.041f)
                for (float x = 0.007f; x < BarrierPattern.TileW; x += 0.043f)
                {
                    BarrierPattern.Cell(x, y, out int q, out int r, out _, out _, out float e);
                    BarrierPattern.Cell(x + BarrierPattern.TileW, y, out int qa, out int ra, out _, out _, out float ea);
                    BarrierPattern.Cell(x, y + BarrierPattern.TileH, out int qb, out int rb, out _, out _, out float eb);
                    BarrierPattern.Cell(x - 3f * BarrierPattern.TileW, y - 2f * BarrierPattern.TileH, out int qc, out int rc, out _, out _, out float ec);
                    int slot = BarrierPattern.TileSlot(q, r);
                    if (BarrierPattern.TileSlot(qa, ra) != slot || BarrierPattern.TileSlot(qb, rb) != slot || BarrierPattern.TileSlot(qc, rc) != slot) mismatched++;
                    if (Math.Abs(ea - e) > 1e-3f || Math.Abs(eb - e) > 1e-3f || Math.Abs(ec - e) > 2e-3f) badEdge++;
                    if (e < -1e-3f || e > BarrierPattern.Apothem + 1e-3f) badEdge++;
                }
            Check(mismatched == 0, $"a point and the same point a tile away are the same cell of the tile (got {mismatched} mismatches)");
            Check(badEdge == 0, $"and the same distance from its edge, always within 0..apothem (got {badEdge} disagreements)");

            // The distance to the nearest edge has no jump anywhere, in particular none where one cell ends and the next begins.
            float worst = 0f;
            const float step = 0.002f;
            for (float y = 0.05f; y < BarrierPattern.TileH; y += 0.173f)
            {
                BarrierPattern.Cell(-span, y, out _, out _, out _, out _, out float last);
                for (float x = -span + step; x < span * 2f; x += step)
                {
                    BarrierPattern.Cell(x, y, out _, out _, out _, out _, out float e);
                    worst = Math.Max(worst, Math.Abs(e - last));
                    last = e;
                }
            }
            Check(worst <= step * 1.01f, $"walking across cells, the edge distance never changes faster than the walk (worst step {worst:0.00000} for {step})");

            // The texture itself: the texel past the tile's right/top edge is the one at its left/bottom, so neither seam shows.
            int wrapOff = 0;
            for (int i = 0; i < BarrierPattern.TexSize; i++)
                foreach (var pair in new[] { new[] { BarrierPattern.TexSize, i, 0, i }, new[] { i, BarrierPattern.TexSize, i, 0 } })
                {
                    BarrierPattern.Pixel(pair[0], pair[1], out byte r1, out byte g1, out byte b1, out _);
                    BarrierPattern.Pixel(pair[2], pair[3], out byte r2, out byte g2, out byte b2, out _);
                    if (Math.Abs(r1 - r2) > 3 || Math.Abs(g1 - g2) > 3 || Math.Abs(b1 - b2) > 3) wrapOff++;
                }
            Check(wrapOff == 0, $"texel 256 is texel 0 in both directions (got {wrapOff} that differ)");

            // And across the wrap the step between neighbouring texels is no bigger than the steps inside the tile.
            byte[] tex = BarrierPattern.TextureBytes();
            int N = BarrierPattern.TexSize;
            int insideMax = 0, seamMax = 0;
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    int here = tex[(y * N + x) * 4 + 2];
                    if (x + 1 < N) insideMax = Math.Max(insideMax, Math.Abs(here - tex[(y * N + x + 1) * 4 + 2]));
                    if (y + 1 < N) insideMax = Math.Max(insideMax, Math.Abs(here - tex[((y + 1) * N + x) * 4 + 2]));
                }
            for (int i = 0; i < N; i++)
            {
                seamMax = Math.Max(seamMax, Math.Abs(tex[(i * N + N - 1) * 4 + 2] - tex[(i * N) * 4 + 2]));
                seamMax = Math.Max(seamMax, Math.Abs(tex[((N - 1) * N + i) * 4 + 2] - tex[i * 4 + 2]));
            }
            Check(seamMax <= insideMax, $"the biggest jump across the tile's wrap ({seamMax}) is no bigger than any inside it ({insideMax})");
        }

        private static void LookMesh()
        {
            BarrierPattern.Look small = BarrierPattern.MakeLook(1.0f, 0f, 1), mid = BarrierPattern.MakeLook(1.8f, 0.5f, 1), big = BarrierPattern.MakeLook(4f, 1f, 1);
            Near(mid.Width, 6f, "a middling push is 6 m wide", 1e-4f);
            Near(mid.Height, Math.Max(3f, 1.8f * 1.6f) * 1.125f, "and a little over max(3, 1.6 x height) tall", 1e-4f);
            Check(small.Height >= 3f - 1e-4f, "never under 3 m tall, however small the pusher");
            Near(BarrierPattern.MakeLook(4f, 0f, 1).Height, 4f * 1.6f, "a 4 m pusher's flare is 1.6 x its height tall at a touch", 1e-4f);
            Check(big.Width > mid.Width && mid.Width > small.Width, "a stronger push makes a wider flare");
            Check(big.Height > mid.Height, "and a taller one");
            Check(big.Gain > mid.Gain && mid.Gain > small.Gain, "and a brighter one");
            Check(BarrierPattern.MakeLook(40f, 1f, 1).Height <= 11f, "however tall the pusher, the flare stops at 11 m");
            Check(Math.Abs(small.V0) < 1f && small.ImpactV >= 0.5f, "the patch dips a little under the ground and the impact is off it");

            foreach (BarrierPattern.Look k in new[] { small, mid, big, BarrierPattern.MakeLook(40f, 1f, 1) })
            {
                int n = BarrierPattern.VertexCount(in k);
                Check(n == (k.Nx + 1) * (k.Ny + 1) && n <= BarrierPattern.MaxVertices, "the vertex count matches the grid and fits the buffers");
                var tris = new int[BarrierPattern.MaxIndices];
                int count = BarrierPattern.Triangles(k.Nx, k.Ny, tris);
                Check(count == k.Nx * k.Ny * 12 && count <= BarrierPattern.MaxIndices && count % 3 == 0, "two faces of two triangles a quad");
                bool valid = true;
                for (int i = 0; i < count; i++) valid &= tris[i] >= 0 && tris[i] < n;
                Check(valid, "every index names a vertex");

                // The grid covers the patch edge to edge, and the texture is sampled in metres over the tile.
                BarrierPattern.VertexPos(in k, 0, 0, out float u0, out float v0);
                BarrierPattern.VertexPos(in k, k.Nx, k.Ny, out float u1, out float v1);
                Near(u0, -k.Width / 2f, "the grid starts at the patch's left", 1e-4f);
                Near(u1, k.Width / 2f, "and ends at its right", 1e-4f);
                Near(v0, k.V0, "starts at its foot", 1e-4f);
                Near(v1 - v0, k.Height, "and is as tall as the patch", 1e-4f);
                BarrierPattern.VertexUv(in k, u0, v0, out float ua, out float va);
                BarrierPattern.VertexUv(in k, u0 + BarrierPattern.TileW, v0 + BarrierPattern.TileH, out float ub, out float vb);
                Near(ub - ua, 1f, "a tile's width of wall is one full repeat of the texture across", 1e-4f);
                Near(vb - va, 1f, "and a tile's height one full repeat up", 1e-4f);
                Check(Fin(ua) && Fin(va), "UVs are finite");
                // Hex cells stay a fixed size in metres whatever the patch: 0.42 m is what a cell measures on the wall.
                Near(BarrierPattern.CellR, 0.42f, "a cell is 0.42 m corner to centre on every flare", 1e-6f);
            }

            // Finer than the lattice, so the ripple's band spans several vertices.
            Check(mid.Width / mid.Nx < BarrierPattern.ColStep / 2f, "the grid is finer than half a cell across");
        }

        private static bool Fin(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
    }
}
