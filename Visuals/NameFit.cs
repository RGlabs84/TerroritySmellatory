using System;

namespace TerroritySmellatory
{
    /// <summary>
    /// Makes a territory's name fit the minimap's label on ONE line: shrunk in steps, and cut short with dots when even the smallest
    /// size is too wide. Pure (the width is asked of the caller), so the off-game harness can run it.
    /// </summary>
    /// <remarks>
    /// The label is vanilla's, made for one short biome name. A name wider than it used to be set to 80% and left to wrap, and a long
    /// one wrapped anyway, its lines drawn on top of each other. Now every result is meant for the label's <c>&lt;nobr&gt;</c>, so it
    /// cannot wrap, and a width that fits is always found: the size is lowered until the name fits, and below <see cref="MinPercent"/>
    /// (where the letters stop being readable) the name is shortened instead.
    ///
    /// The width of a candidate is MEASURED by the label itself (TMP_Text.GetPreferredValues, which reads the size tag), not worked out
    /// from the width at full size: kerning and the font's rounding make a guess a pixel or two off, and a pixel is what wraps.
    /// </remarks>
    internal static class NameFit
    {
        /// <summary>The smallest size a name is shrunk to, in percent of the label's own.</summary>
        internal const int MinPercent = 55;

        /// <summary>One shrink step, in percent.</summary>
        internal const int Step = 5;

        internal const string Dots = "...";

        /// <summary>
        /// The rich text to show for <paramref name="name"/> in a label <paramref name="room"/> wide: the name as it is when it fits, else
        /// the name inside a size tag - the largest size, from 95% down in steps, that fits - else the longest start of the name and dots, at
        /// the smallest size. <paramref name="widthOf"/> is the width of a piece of rich text on the label.
        /// </summary>
        internal static string Fit(string name, float room, Func<string, float> widthOf)
        {
            if (string.IsNullOrEmpty(name)) return "";
            if (room <= 0f || widthOf(name) <= room) return name;

            for (int percent = 100 - Step; percent >= MinPercent; percent -= Step)
            {
                string sized = Sized(percent, name);
                if (widthOf(sized) <= room) return sized;
            }

            // Still too wide at the smallest size: the longest start of the name that fits with the dots after it.
            int lo = 0, hi = name.Length - 1;   // the most that fit, between none and all but the last letter
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (widthOf(Sized(MinPercent, Start(name, mid))) <= room) lo = mid;
                else hi = mid - 1;
            }
            return Sized(MinPercent, Start(name, lo));
        }

        private static string Sized(int percent, string text) => "<size=" + percent + "%>" + text + "</size>";

        /// <summary>The first <paramref name="count"/> letters, with no space left hanging before the dots, and the dots.</summary>
        private static string Start(string name, int count) => name.Substring(0, count).TrimEnd() + Dots;
    }
}
