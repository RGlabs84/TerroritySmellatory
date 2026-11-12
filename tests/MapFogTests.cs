using System;
using System.Collections.Generic;

namespace TerroritySmellatory.Tests
{
    // 0.4.1: a territory is drawn on the map only over ground the player has uncovered, and its name in the minimap's label stays on one line.
    // The parts that can be wrong without a sign are pure and run here as shipped: the copy of vanilla's explored bits into the mask
    // (Visuals/ExploredBits.cs), the box of changed cells, and the name fitting (Visuals/NameFit.cs). What the game adds - the Mask
    // clipping the graphics, the postfixes noting which cells changed - is in Visuals/ExploredMask.cs and MapOverlay.cs, and the rig checks it.
    internal static partial class Program
    {
        static partial void FogGroups()
        {
            Run("fog: the mask is covered exactly where either explored set has the cell - random sets, every texture size", FogMaskMatchesBits);
            Run("fog: a box of cells touches only those cells, and clamps to the texture", FogMaskBox);
            Run("fog: the changed-cell box grows around what was marked, empties when taken, and 'all' beats it", FogBoxLifecycle);
            Run("fog: filling box by box ends where one full fill does - the way the game keeps the mask up to date", FogIncrementalEqualsFull);
            Run("name: a name that fits is left alone", NameFitsUnchanged);
            Run("name: a long name shrinks to the largest size that fits, in steps", NameShrinks);
            Run("name: a name too long even at the smallest size is cut short with dots and still fits", NameTruncates);
            Run("name: whatever the name and the room, the result fits when it can, and is never wider than the room otherwise", NameNeverOverflows);
        }

        // ── the mask ──────────────────────────────────────────────────

        private static int[] FogWords(int size, Random rng, double density, out bool[] cells)
        {
            int n = size * size;
            cells = new bool[n];
            var words = new int[ExploredBits.Words(n)];
            for (int i = 0; i < n; i++)
            {
                if (rng.NextDouble() >= density) continue;
                cells[i] = true;
                words[i >> 5] |= 1 << (i & 31);
            }
            return words;
        }

        private static void FogMaskMatchesBits()
        {
            var rng = new Random(41);
            foreach (int size in new[] { 1, 2, 5, 31, 32, 33, 64, 100, 257 })
            {
                foreach (double density in new[] { 0.0, 0.02, 0.5, 1.0 })
                {
                    int[] self = FogWords(size, rng, density, out bool[] a);
                    int[] others = FogWords(size, rng, density / 2, out bool[] b);
                    var mask = new byte[size * size];
                    for (int i = 0; i < mask.Length; i++) mask[i] = 77;   // would show a cell the fill skipped
                    ExploredBits.Fill(self, others, size, 0, 0, size - 1, size - 1, mask);

                    int wrong = 0;
                    for (int i = 0; i < mask.Length; i++)
                    {
                        byte want = a[i] || b[i] ? ExploredBits.Covered : ExploredBits.Fogged;
                        if (mask[i] != want) wrong++;
                    }
                    Check(wrong == 0, $"size {size}, density {density}: {wrong} of {mask.Length} cells wrong");
                }
            }
            Check(ExploredBits.Covered > 0 && ExploredBits.Fogged == 0, "covered passes a UI Mask's alpha test (above 0.001), fog does not");
            Check(ExploredBits.Words(2048 * 2048) == 131072, "a 2048 x 2048 map is 131072 words");
            Check(ExploredBits.Words(1) == 1 && ExploredBits.Words(32) == 1 && ExploredBits.Words(33) == 2, "words round up");
        }

        private static void FogMaskBox()
        {
            const int size = 40;
            var rng = new Random(7);
            int[] self = FogWords(size, rng, 1.0, out _);                 // everything explored
            int[] others = new int[ExploredBits.Words(size * size)];
            var mask = new byte[size * size];
            ExploredBits.Fill(self, others, size, 10, 20, 14, 22, mask);
            int inside = 0, outside = 0;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    bool box = x >= 10 && x <= 14 && y >= 20 && y <= 22;
                    if (mask[y * size + x] != 0) { if (box) inside++; else outside++; }
                }
            Check(inside == 5 * 3, $"the box's 15 cells are filled (got {inside})");
            Check(outside == 0, $"nothing outside the box is touched (got {outside})");

            // Past the edges: clamped, and a box wholly outside does nothing.
            var m2 = new byte[size * size];
            ExploredBits.Fill(self, others, size, -5, -5, 2, 1, m2);
            int filled = 0;
            foreach (byte b in m2) if (b != 0) filled++;
            Check(filled == 3 * 2, $"a box over the corner is clamped to the 3 x 2 cells that exist (got {filled})");
            var m3 = new byte[size * size];
            ExploredBits.Fill(self, others, size, size, size, size + 4, size + 4, m3);
            ExploredBits.Fill(self, others, size, -9, -9, -1, -1, m3);
            foreach (byte b in m3) Check(b == 0, "a box wholly off the texture touches nothing");
            ExploredBits.Fill(self, others, 0, 0, 0, 3, 3, new byte[0]);   // a map with no size: no crash
        }

        private static void FogBoxLifecycle()
        {
            var box = new ExploredBox();
            Check(box.Dirty && box.All, "a new box starts as 'everything' - the first copy is the whole map");
            Check(box.Take(100, out int x0, out int y0, out int x1, out int y1) && x0 == 0 && y0 == 0 && x1 == 99 && y1 == 99, "taking 'all' gives the whole texture");
            Check(!box.Dirty && !box.All, "taking empties it");
            Check(!box.Take(100, out _, out _, out _, out _), "nothing to take");

            box.Mark(30, 40);
            box.Mark(10, 55);
            box.Mark(25, 20);
            Check(box.Dirty && !box.All, "marked cells make it dirty");
            Check(box.Take(100, out x0, out y0, out x1, out y1) && x0 == 10 && x1 == 30 && y0 == 20 && y1 == 55, $"the box is the least rectangle round the marks (got {x0},{y0} to {x1},{y1})");
            Check(!box.Dirty, "emptied again");

            box.Mark(5, 5);
            box.MarkAll();
            box.Mark(90, 90);
            Check(box.Take(100, out x0, out y0, out x1, out y1) && x0 == 0 && y0 == 0 && x1 == 99 && y1 == 99, "'all' beats whatever was marked before and after");

            box.Mark(-20, -20);
            box.Mark(500, 500);
            Check(box.Take(100, out x0, out y0, out x1, out y1) && x0 == 0 && y0 == 0 && x1 == 99 && y1 == 99, "marks past the texture clamp to it");
            box.Mark(-3, -3);
            Check(!box.Take(100, out _, out _, out _, out _), "a mark wholly off the texture is nothing to copy");

            box.MarkAround(50, 50, 9);
            Check(box.Take(100, out x0, out y0, out x1, out y1) && x0 == 41 && x1 == 59 && y0 == 41 && y1 == 59, "MarkAround is the square of cells within the radius");
        }

        /// <summary>The game copies only what was marked, again and again; it must end up where a copy of everything would.</summary>
        private static void FogIncrementalEqualsFull()
        {
            const int size = 96;
            var rng = new Random(99);
            var self = new int[ExploredBits.Words(size * size)];
            var others = new int[self.Length];
            var kept = new byte[size * size];        // the mask as the game keeps it
            var box = new ExploredBox();

            for (int step = 0; step < 400; step++)
            {
                int kind = rng.Next(10);
                if (kind < 8)
                {
                    // the player walks: a few cells of their own
                    for (int k = 0; k < 6; k++)
                    {
                        int x = rng.Next(size), y = rng.Next(size), i = y * size + x;
                        self[i >> 5] |= 1 << (i & 31);
                        box.Mark(x, y);
                    }
                }
                else if (kind == 8)
                {
                    int x = rng.Next(size), y = rng.Next(size), i = y * size + x;   // a shared map adds a cell
                    others[i >> 5] |= 1 << (i & 31);
                    box.Mark(x, y);
                }
                else if (rng.Next(6) == 0)
                {
                    // a reset: both sets emptied, then some of one loaded
                    Array.Clear(self, 0, self.Length);
                    Array.Clear(others, 0, others.Length);
                    for (int i = 0; i < size * size / 3; i++) { int c = rng.Next(size * size); self[c >> 5] |= 1 << (c & 31); }
                    box.MarkAll();
                }

                if (rng.Next(3) == 0 && box.Take(size, out int x0, out int y0, out int x1, out int y1))
                    ExploredBits.Fill(self, others, size, x0, y0, x1, y1, kept);
            }
            if (box.Take(size, out int ax0, out int ay0, out int ax1, out int ay1)) ExploredBits.Fill(self, others, size, ax0, ay0, ax1, ay1, kept);

            var full = new byte[size * size];
            ExploredBits.Fill(self, others, size, 0, 0, size - 1, size - 1, full);
            int wrong = 0;
            for (int i = 0; i < full.Length; i++) if (full[i] != kept[i]) wrong++;
            Check(wrong == 0, $"after 400 random steps the kept mask differs from a full copy in {wrong} cells");
        }

        // ── the name ──────────────────────────────────────────────────

        /// <summary>A label's width for a piece of rich text: 9 units a letter at the label's own size, scaled by the size tag.</summary>
        private static float NameWidth(string rich)
        {
            float percent = 100f;
            string text = rich;
            if (rich.StartsWith("<size=", StringComparison.Ordinal))
            {
                int close = rich.IndexOf("%>", StringComparison.Ordinal);
                percent = float.Parse(rich.Substring(6, close - 6));
                text = rich.Substring(close + 2, rich.Length - close - 2 - "</size>".Length);
            }
            return text.Length * 9f * percent / 100f;
        }

        private static string NameVisible(string rich)
        {
            if (!rich.StartsWith("<size=", StringComparison.Ordinal)) return rich;
            int close = rich.IndexOf("%>", StringComparison.Ordinal);
            return rich.Substring(close + 2, rich.Length - close - 2 - "</size>".Length);
        }

        private static void NameFitsUnchanged()
        {
            Check(NameFit.Fit("Meadows", 183f, NameWidth) == "Meadows", "a short name is the name");
            Check(NameFit.Fit("Exactly Twenty Chars!", 189f, NameWidth) == "Exactly Twenty Chars!", "a name exactly as wide as the room fits");
            Check(NameFit.Fit("Anything at all", 0f, NameWidth) == "Anything at all", "a label with no width yet is left alone");
            Check(NameFit.Fit("Anything at all", -4f, NameWidth) == "Anything at all", "a label with a negative width yet is left alone");
            Check(NameFit.Fit("", 183f, NameWidth) == "" && NameFit.Fit(null, 183f, NameWidth) == "", "no name, no text");
        }

        private static void NameShrinks()
        {
            // "A Carrot Field in Britain 2" - 27 letters: 243 units at full size; the user's screenshot.
            string name = "A Carrot Field in Britain 2";
            string one = NameFit.Fit(name, 183f, NameWidth);
            Check(one == "<size=75%>" + name + "</size>", $"27 letters in 183 units: the largest size that fits is 75% (got {one})");
            Check(NameWidth(one) <= 183f && NameWidth("<size=80%>" + name + "</size>") > 183f, "75% fits and the step above it does not");
            Check(!one.Contains("\n"), "no line break in it");

            string felucca = "Felucca Buccaneer's Den";   // 23 letters, 207 units
            string two = NameFit.Fit(felucca, 183f, NameWidth);
            Check(two == "<size=85%>" + felucca + "</size>", $"23 letters in 183 units: 85% (got {two})");
            Check(NameFit.Fit(felucca, 120f, NameWidth) == "<size=55%>" + felucca + "</size>", "in 120 units the same name goes to the smallest size, whole");
        }

        private static void NameTruncates()
        {
            string name = "The Very Long Name Of A Territory Nobody Could Fit In Any Label";   // 63 letters
            string cut = NameFit.Fit(name, 183f, NameWidth);
            string small = "<size=" + NameFit.MinPercent + "%>";
            Check(cut.StartsWith(small) && cut.EndsWith("</size>"), "a cut name is at the smallest size");
            string shown = NameVisible(cut);
            Check(shown.EndsWith(NameFit.Dots), $"cut with dots (got '{shown}')");
            Check(NameWidth(cut) <= 183f, "it fits");
            Check(!shown.Contains(" ..."), "no space hangs before the dots");

            // The longest start that fits, found the slow way.
            int best = 0;
            for (int n = 0; n < name.Length; n++)
                if (NameWidth(small + name.Substring(0, n).TrimEnd() + NameFit.Dots + "</size>") <= 183f) best = n;
            Check(cut == small + name.Substring(0, best).TrimEnd() + NameFit.Dots + "</size>", $"it is the longest start of the name that fits (expected {best} letters, got '{shown}')");

            // A room too small for anything: the dots alone, still a result and still on one line.
            Check(NameVisible(NameFit.Fit(name, 5f, NameWidth)) == NameFit.Dots, "no room: just the dots");
        }

        private static void NameNeverOverflows()
        {
            var rng = new Random(2026);
            const string alphabet = "abcdefghijklmnopqrstuvwxyz ABCDEFGHIJKLMNOPQRSTUVWXYZ'1234567890";
            int overflow = 0, bad = 0, runs = 0;
            for (int i = 0; i < 4000; i++)
            {
                int length = 1 + rng.Next(70);
                var chars = new char[length];
                for (int k = 0; k < length; k++) chars[k] = alphabet[rng.Next(alphabet.Length)];
                string name = new string(chars).Trim();
                if (name.Length == 0) continue;
                float room = 20f + (float)rng.NextDouble() * 400f;
                string fitted = NameFit.Fit(name, room, NameWidth);
                runs++;

                float dotsAtMin = NameWidth("<size=" + NameFit.MinPercent + "%>" + NameFit.Dots + "</size>");
                if (NameWidth(fitted) > room && room >= dotsAtMin) overflow++;   // anything the dots can fit in must fit
                // A result is one of: the name; the name sized; a start of the name with dots, sized. Never anything else.
                string shown = NameVisible(fitted);
                bool ok = shown == name || (shown.EndsWith(NameFit.Dots) && name.StartsWith(shown.Substring(0, shown.Length - NameFit.Dots.Length).TrimEnd()));
                if (!ok) bad++;
                if (fitted.Contains("\n") || fitted.Contains("<nobr>")) bad++;
            }
            Check(runs > 3000, "enough random names ran");
            Check(overflow == 0, $"{overflow} of {runs} results were wider than a room they could have fitted");
            Check(bad == 0, $"{bad} results were not the name, a shrunk name or a cut name");
        }
    }
}
