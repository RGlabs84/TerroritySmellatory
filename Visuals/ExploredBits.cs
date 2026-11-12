using System;

namespace TerroritySmellatory
{
    /// <summary>
    /// The arithmetic behind the map's "only where it is explored" mask, with no Unity in it (so the off-game harness can run it):
    /// vanilla's two explored bit sets turned into one byte per map cell, and the box of cells that changed since the last copy.
    /// </summary>
    /// <remarks>
    /// Vanilla keeps what the player has uncovered as two BitArrays of textureSize x textureSize bits, indexed y * size + x with
    /// (x, y) the map texture's own pixel (row 0 at the bottom): m_explored for what this player walked, and m_exploredOthers for
    /// what other players' shared map added. The fog texture the map shader samples is set from the same two. A cell is
    /// "uncovered" when either is set - Minimap.IsExplored's own rule.
    /// </remarks>
    internal static class ExploredBits
    {
        /// <summary>The byte a covered cell gets in the mask (its alpha: anything above 0.001 passes a UI Mask), and an uncovered one.</summary>
        internal const byte Covered = 255;
        internal const byte Fogged = 0;

        /// <summary>How many 32-bit words hold <paramref name="cells"/> bits - what BitArray.CopyTo(int[]) fills.</summary>
        internal static int Words(int cells) => (cells + 31) / 32;

        /// <summary>
        /// Sets <paramref name="dest"/> for the cells x0..x1, y0..y1 (inclusive, clamped to the texture) from the two packed bit sets:
        /// Covered where either has the cell's bit, Fogged elsewhere. <paramref name="dest"/> is indexed like the bits, y * size + x.
        /// </summary>
        internal static void Fill(int[] self, int[] others, int size, int x0, int y0, int x1, int y1, byte[] dest)
        {
            if (size <= 0) return;
            if (x0 < 0) x0 = 0;
            if (y0 < 0) y0 = 0;
            if (x1 > size - 1) x1 = size - 1;
            if (y1 > size - 1) y1 = size - 1;
            for (int y = y0; y <= y1; y++)
            {
                int row = y * size;
                for (int x = x0; x <= x1; x++)
                {
                    int i = row + x;
                    int word = (self[i >> 5] | others[i >> 5]) >> (i & 31);
                    dest[i] = (word & 1) != 0 ? Covered : Fogged;
                }
            }
        }
    }

    /// <summary>
    /// The cells that changed since the mask was last brought up to date: one box around them, or "everything" (a reset, a loaded
    /// map). One box costs a little extra copying when two far-apart cells change at once, and never more than the whole texture.
    /// </summary>
    internal sealed class ExploredBox
    {
        private int _x0 = int.MaxValue, _y0 = int.MaxValue, _x1 = -1, _y1 = -1;

        /// <summary>Everything must be copied again (the map was reset, loaded or replaced).</summary>
        internal bool All { get; private set; } = true;

        /// <summary>Anything to copy at all?</summary>
        internal bool Dirty => All || _x1 >= 0;

        internal void MarkAll() => All = true;

        internal void Mark(int x, int y)
        {
            if (All) return;
            if (x < _x0) _x0 = x;
            if (x > _x1) _x1 = x;
            if (y < _y0) _y0 = y;
            if (y > _y1) _y1 = y;
        }

        /// <summary>Marks the square of cells within <paramref name="radius"/> of (cx, cy).</summary>
        internal void MarkAround(int cx, int cy, int radius)
        {
            if (All) return;
            Mark(cx - radius, cy - radius);
            Mark(cx + radius, cy + radius);
        }

        /// <summary>What to copy now, as cells x0..x1, y0..y1 inclusive of a size x size texture; then forgets it. False when nothing changed.</summary>
        internal bool Take(int size, out int x0, out int y0, out int x1, out int y1)
        {
            if (All)
            {
                x0 = 0;
                y0 = 0;
                x1 = size - 1;
                y1 = size - 1;
            }
            else
            {
                x0 = Math.Max(0, _x0);
                y0 = Math.Max(0, _y0);
                x1 = Math.Min(size - 1, _x1);
                y1 = Math.Min(size - 1, _y1);
            }
            bool any = All || (_x1 >= 0 && x0 <= x1 && y0 <= y1);
            All = false;
            _x0 = _y0 = int.MaxValue;
            _x1 = _y1 = -1;
            return any;
        }
    }
}
