using System;
using System.Collections.Generic;
using UnityEngine;

namespace TerroritySmellatory
{
    /// <summary>One place a posted guard stands: which part of the territory it serves, where, and the way it faces (out).</summary>
    internal readonly struct GuardPost
    {
        /// <summary>The post's number in its territory, 0 up: the key a guard's ZDO carries (Guards.PostKey).</summary>
        public readonly int Index;

        /// <summary>The part of the territory it stands in (0 = the first).</summary>
        public readonly int Part;

        public readonly float X;
        public readonly float Z;

        /// <summary>Degrees about Y; 0 faces +Z. Out from the part's middle.</summary>
        public readonly float Yaw;

        public GuardPost(int index, int part, float x, float z, float yaw)
        {
            Index = index;
            Part = part;
            X = x;
            Z = z;
            Yaw = yaw;
        }
    }

    // THE GUARDS RULE, THE DECISIONS (0.5.0). Rules/Guards.cs is the part that touches the game; what it decides is here,
    // pure, and checked off-game (tests/GuardTests.cs): where a territory's guards stand, whether a chat line calls them,
    // and what each one is called. Ported from ValtimaOnline's town guards (Npcs/ValtimaGuard.cs, Npcs/GuardCalls.cs),
    // where a guard's post came from UO's town gates; a territory has no gates, so its posts are a ring round the
    // middle of each part.
    internal static class GuardMath
    {
        /// <summary>A post is this share of the way from a part's middle to its edge (along that axis).</summary>
        internal const float RingShare = 0.4f;

        /// <summary>Never further than this from the middle: a big territory's guards stay where its heart is.</summary>
        internal const float RingMax = 12f;

        /// <summary>Never nearer than this to the middle, so two guards do not stand in each other...</summary>
        internal const float RingMin = 1.5f;

        /// <summary>...unless the part is so small that this share of its half-extent is less: a post always stands inside.</summary>
        internal const float RingInside = 0.8f;

        /// <summary>
        /// Where a territory's posted guards stand: <paramref name="perPart"/> in each part, in order of the parts, until
        /// <paramref name="max"/> are placed. In each part they are spread evenly round an ellipse inside it (a circle for a circle
        /// or a square), turned with the part, starting 45 degrees off its own +Z so two guards stand on a diagonal rather than
        /// on an axis, each facing out from the middle. The same territory always gives the same posts, so the server can tell a
        /// guard that stands where it should from one whose territory moved (Guards.Reconcile).
        /// </summary>
        internal static List<GuardPost> Posts(Territory t, int perPart, int max)
        {
            var posts = new List<GuardPost>();
            if (t == null || perPart <= 0 || max <= 0) return posts;
            for (int p = 0; p < t.PartCount && posts.Count < max; p++)
            {
                TerritoryShape s = t.GetPart(p);
                if (s.IsEmpty) continue;
                float rx = Ring(s.HalfX), rz = Ring(s.HalfZ);
                for (int i = 0; i < perPart && posts.Count < max; i++)
                {
                    float a = (45f + 360f * i / perPart) * Mathf.Deg2Rad;
                    var local = new Vector3(rx * Mathf.Sin(a), 0f, rz * Mathf.Cos(a));
                    // A circle's yaw is held at 0 (TerritoryShape), so this is the same turn Contains and the outline use.
                    Vector3 world = Quaternion.Euler(0f, s.Yaw, 0f) * local;
                    float yaw = Mathf.Atan2(world.x, world.z) * Mathf.Rad2Deg;
                    posts.Add(new GuardPost(posts.Count, p, s.CenterX + world.x, s.CenterZ + world.z, yaw));
                }
            }
            return posts;
        }

        private static float Ring(float half) => Mathf.Min(Mathf.Clamp(half * RingShare, RingMin, RingMax), half * RingInside);

        /// <summary>
        /// Does this chat line call the guards? A call word as a whole word, in any case: "Guards!", "help, guards" - not
        /// "safeguards". <paramref name="words"/> is the client's GuardCallWords: comma, semicolon or space separated.
        /// </summary>
        internal static bool ContainsCall(string text, string words)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(words)) return false;
            string[] calls = words.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries);
            if (calls.Length == 0) return false;
            foreach (string token in text.Split(TextSeparators, StringSplitOptions.RemoveEmptyEntries))
                foreach (string w in calls)
                    if (string.Equals(token, w, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>The first call word, for the panel's hint; "" when there is none.</summary>
        internal static string FirstWord(string words)
        {
            if (string.IsNullOrEmpty(words)) return "";
            string[] calls = words.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries);
            return calls.Length > 0 ? calls[0] : "";
        }

        private static readonly char[] WordSeparators = { ',', ';', ' ' };
        private static readonly char[] TextSeparators = { ' ', ',', '.', '!', '?', ';', ':', '\n', '\t', '"', '\'', '(', ')' };

        private static readonly string[] MaleNames =
        {
            "Arnbjorn", "Bersi", "Eyvind", "Geirmund", "Ingolf", "Ketil", "Leif", "Njal", "Sigtrygg", "Ulfar", "Yngvar", "Hrafn",
        };

        private static readonly string[] FemaleNames =
        {
            "Dagny", "Freydis", "Hallbera", "Jorunn", "Oddny", "Ragna", "Thorgerd", "Vigdis", "Astrid", "Gudrun", "Sigrid", "Ylva",
        };

        /// <summary>
        /// A posted guard's look and first name: the same for the same post of the same territory, on every machine and after every
        /// restart. About one in three is a woman (ValtimaOnline's town guards: guard_warrior_f beside the pilot guard, guard_v2).
        /// </summary>
        internal static void LookFor(int territoryId, int post, out bool female, out string name)
        {
            uint h = Mix((uint)territoryId, (uint)(post + 1));
            female = h % 3u == 0u;
            string[] names = female ? FemaleNames : MaleNames;
            name = names[(int)((h >> 8) % (uint)names.Length)];
        }

        /// <summary>A called guard's look and name, from any seed (the server's clock when it was called).</summary>
        internal static void LookFor(uint seed, out bool female, out string name) => LookFor(unchecked((int)seed), -7, out female, out name);

        private static uint Mix(uint a, uint b)
        {
            unchecked
            {
                uint h = a * 2654435761u ^ b * 40503u;
                h ^= h >> 15;
                h *= 2246822519u;
                h ^= h >> 13;
                return h;
            }
        }

        /// <summary>A guard is never seen called anything longer than this, however long its territory's name.</summary>
        internal const int MaxTitleTerritory = 32;

        /// <summary>"guard of Hrafnborg"; just "guard" when the territory has no name to give.</summary>
        internal static string Title(string territoryName)
        {
            string n = (territoryName ?? "").Trim();
            if (n.Length == 0) return "guard";
            if (n.Length > MaxTitleTerritory) n = n.Substring(0, MaxTitleTerritory - 3).TrimEnd() + "...";
            return "guard of " + n;
        }

        /// <summary>
        /// Is a posted guard still where its territory wants it? Off its post's spot by more than this (metres, across the
        /// ground) and the server takes it away and posts a new one: the territory was moved or reshaped. Generous, because
        /// the post's spot is what is compared (the ZDO key), not where the guard happens to stand.
        /// </summary>
        internal const float MovedSlack = 0.5f;

        internal static bool SamePost(GuardPost want, float x, float z) =>
            Mathf.Abs(want.X - x) <= MovedSlack && Mathf.Abs(want.Z - z) <= MovedSlack;
    }
}
