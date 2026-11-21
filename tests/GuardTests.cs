using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace TerroritySmellatory.Tests
{
    // 0.5.0: the guards rule's decisions (Rules/GuardMath.cs) - the flag itself, where a territory's guards stand, whether a chat line calls
    // them, and what each is called. The part that touches the game (Rules/Guards.cs: the prefabs, the server's posts, the call; the guard
    // and its body) needs a world; everything it decides is here.
    internal static partial class Program
    {
        static partial void GuardGroups()
        {
            Run("guards: the flag is bit 15, known, off by default, needs nothing, and travels by name", GuardFlag);
            Run("guards: posts stand inside their part, round its middle, facing out", GuardPostsInside);
            Run("guards: posts follow the parts, the count per part and the cap; the same territory gives the same posts", GuardPostsCount);
            Run("guards: a moved territory moves its posts, and only then", GuardPostsMoved);
            Run("guards: a post in the water stands on the nearest dry ground of its part (0.5.2)", GuardDryPosts);
            Run("guards: the call word is a whole word in any case", GuardCallWords);
            Run("guards: names and titles", GuardNames);
        }

        private const TerritoryFlags GuardFlagBit = TerritoryFlags.Guards;

        private static void GuardFlag()
        {
            Check((int)GuardFlagBit == 1 << 15, "Guards is bit 15, after the map's bit 14");
            Check((Territory.KnownFlags & GuardFlagBit) != 0, "it is a known flag, so sanitising does not strip it");
            Check((Territory.DefaultFlags & GuardFlagBit) == 0 && (Territory.DraftFlags & GuardFlagBit) == 0, "no territory gains guards by upgrading, and a new one starts without");
            Check(Territory.CleanFlags(GuardFlagBit) == GuardFlagBit, "on its own it survives CleanFlags: it needs no other rule");
            Check(Territory.CleanFlags(Territory.KnownFlags) == Territory.KnownFlags, "every known flag on is still a record the server keeps as it stands");
            Check(Inheritance.RuleLabel(GuardFlagBit) == "Guards", "its label");

            var file = new TerritoryJson.FileData { WorldName = "Midgard", WorldUid = 1L, NextId = 3 };
            file.Territories.Add(Circle(1, 0f, 0f, 30f, GuardFlagBit | TerritoryFlags.BuildProtect));
            file.Territories.Add(Circle(2, 100f, 0f, 30f, TerritoryFlags.BuildProtect));
            string json = TerritoryJson.Write(file);
            Check(NetCount(json, "\"Guards\"") == 1, "written as a name, on the territory that has it and no other");
            TerritoryJson.FileData back = TerritoryJson.Read(json);
            Check(back.Territories[0].Has(GuardFlagBit) && !back.Territories[1].Has(GuardFlagBit), "and read back by name");
            Check(back.Version == 6 && !back.FromNewerBuild, "in a version 6 file, which this build saves");

            var pkg = new ZPackage();
            Circle(3, 0f, 0f, 10f, GuardFlagBit).Write(pkg);
            Check(Territory.Read(pkg.Transmit()).Has(GuardFlagBit), "the wire carries it in the flags int");
        }

        private static Territory GuardRect(int id, float x, float z, float hx, float hz, float yaw) =>
            new Territory { Id = id, Name = "R" + id, Kind = TerritoryShapeKind.Rectangle, X = x, Z = z, HalfX = hx, HalfZ = hz, Yaw = yaw, Flags = GuardFlagBit };

        private static void GuardPostsInside()
        {
            var shapes = new List<Territory>
            {
                Circle(1, 0f, 0f, 20f, GuardFlagBit),
                Circle(2, 500f, -300f, 1f, GuardFlagBit),       // tiny: the ring shrinks inside it
                Circle(3, 0f, 0f, 2000f, GuardFlagBit),         // huge: the ring stays near the middle
                GuardRect(4, 40f, 40f, 30f, 3f, 37f),           // long and thin, turned
                GuardRect(5, -80f, 10f, 2f, 50f, 300f),
            };
            for (int n = 1; n <= 8; n++)
                foreach (Territory t in shapes)
                {
                    List<GuardPost> posts = GuardMath.Posts(t, n, 64);
                    Check(posts.Count == n, $"#{t.Id}: {n} a part gives {n}");
                    foreach (GuardPost p in posts)
                    {
                        Vector3 at = new Vector3(p.X, 0f, p.Z);
                        TerritoryShape s = t.GetPart(p.Part);
                        Check(s.Contains(at), $"#{t.Id} post {p.Index} stands inside its part");
                        Vector3 outward = at - s.Center;
                        float dist = outward.magnitude;
                        Check(dist <= GuardMath.RingMax + 0.01f, $"#{t.Id} post {p.Index} is within {GuardMath.RingMax} m of the middle ({dist:0.00})");
                        Vector3 facing = Quaternion.Euler(0f, p.Yaw, 0f) * Vector3.forward;
                        Check(Vector3.Dot(facing, outward.normalized) > 0.999f, $"#{t.Id} post {p.Index} faces out from the middle");
                    }
                    // spread: no two posts of one part on the same spot
                    for (int i = 0; i < posts.Count; i++)
                        for (int j = i + 1; j < posts.Count; j++)
                            Check(Vector3.Distance(new Vector3(posts[i].X, 0, posts[i].Z), new Vector3(posts[j].X, 0, posts[j].Z)) > 0.05f, $"#{t.Id}: posts {i} and {j} stand apart");
                }

            // the pair a part gets by default stands on a diagonal, either side of the middle
            List<GuardPost> two = GuardMath.Posts(Circle(9, 0f, 0f, 20f, GuardFlagBit), 2, 8);
            Check(Mathf.Abs(two[0].X + two[1].X) < 0.01f && Mathf.Abs(two[0].Z + two[1].Z) < 0.01f, "two guards stand either side of the middle");
            Check(Mathf.Abs(Mathf.Abs(two[0].X) - Mathf.Abs(two[0].Z)) < 0.01f, "on a diagonal, not on an axis");
            Check(Mathf.Abs(new Vector3(two[0].X, 0f, two[0].Z).magnitude - 8f) < 0.01f, "40% of the way to a 20 m edge: 8 m out");
        }

        private static void GuardPostsCount()
        {
            Territory three = Circle(1, 0f, 0f, 20f, GuardFlagBit);
            three.ExtraParts = new List<TerritoryShape>
            {
                new TerritoryShape(TerritoryShapeKind.Square, 100f, 0f, 15f, 15f, 20f),
                new TerritoryShape(TerritoryShapeKind.Circle, 0f, 100f, 10f, 10f, 0f),
            };
            List<GuardPost> all = GuardMath.Posts(three, 2, 32);
            Check(all.Count == 6, "two in each of three parts");
            Check(all.Select(p => p.Part).SequenceEqual(new[] { 0, 0, 1, 1, 2, 2 }), "in the order of the parts");
            Check(all.Select(p => p.Index).SequenceEqual(Enumerable.Range(0, 6)), "numbered 0 up");
            Check(all.All(p => three.GetPart(p.Part).Contains(new Vector3(p.X, 0f, p.Z))), "each inside its own part");

            List<GuardPost> capped = GuardMath.Posts(three, 2, 3);
            Check(capped.Count == 3 && capped[2].Part == 1, "the cap stops it: the first parts are served first");
            Check(GuardMath.Posts(three, 0, 8).Count == 0 && GuardMath.Posts(three, 2, 0).Count == 0 && GuardMath.Posts(null, 2, 8).Count == 0, "none for nothing");

            List<GuardPost> again = GuardMath.Posts(three.Clone(), 2, 32);
            Check(again.Count == all.Count && again.Zip(all, (a, b) => a.X == b.X && a.Z == b.Z && a.Yaw == b.Yaw && a.Part == b.Part).All(x => x),
                "the same territory gives exactly the same posts (the server compares them every few seconds)");
        }

        private static void GuardDryPosts()
        {
            // New Dawn's The Nook (2026-11-20): a 20 m circle drawn over the harbour, its middle and both ring posts in the water
            Territory nook = Circle(2, 69.98418f, -573.1506f, 20f, GuardFlagBit);
            TerritoryShape part = nook.GetPart(0);
            List<GuardPost> ring = GuardMath.Posts(nook, 2, 8);
            Check(Mathf.Abs(ring[0].X - 75.64f) < 0.05f && Mathf.Abs(ring[0].Z + 567.49f) < 0.05f, "the Nook's post 1 is the one the server warned about (76, -567)");

            // land north of z = -565 only: both ring posts (z -567.5 and -578.8) are in the water
            Func<float, float, bool> north = (x, z) => z >= -565f;
            var taken = new List<GuardPost>();
            var moved = new List<GuardPost>();
            foreach (GuardPost p in ring)
            {
                Check(!north(p.X, p.Z), $"post {p.Index + 1} starts in the water");
                Check(GuardMath.TryDry(part, p, north, taken, out GuardPost d), $"post {p.Index + 1} finds dry ground");
                Check(north(d.X, d.Z), $"post {p.Index + 1} now stands on it ({d.X:0.0}, {d.Z:0.0})");
                Check(part.Contains(new Vector3(d.X, 0f, d.Z)), $"post {p.Index + 1} is still inside its part");
                Check(Vector3.Distance(new Vector3(d.X, 0f, d.Z), part.Center) <= part.HalfX * GuardMath.DryReach + 0.01f, $"post {p.Index + 1} within {GuardMath.DryReach:P0} of the way to the edge");
                Check(d.Index == p.Index && d.Part == p.Part, $"post {p.Index + 1} keeps its number and part (the ZDO key)");
                Vector3 outward = new Vector3(d.X, 0f, d.Z) - part.Center;
                Check(Vector3.Dot(Quaternion.Euler(0f, d.Yaw, 0f) * Vector3.forward, outward.normalized) > 0.999f, $"post {p.Index + 1} faces out from the middle");
                taken.Add(d);
                moved.Add(d);
            }
            Check(Vector3.Distance(new Vector3(moved[0].X, 0f, moved[0].Z), new Vector3(moved[1].X, 0f, moved[1].Z)) >= GuardMath.DrySpacing - 0.01f,
                "two moved posts never share a spot");
            // nearest first: post 1 (north-east) needs only a short step north; it lands no further than the shore line plus one grid step
            Check(moved[0].Z >= -565f && moved[0].Z < -561f, $"post 1 moved just over the shore line, not to the far edge ({moved[0].Z:0.0})");

            // the same ground gives the same spot, every time (the server compares it every 5 s)
            var again = new List<GuardPost>();
            foreach (GuardPost p in ring)
            {
                GuardMath.TryDry(part, p, north, again, out GuardPost d);
                again.Add(d);
            }
            Check(again.Zip(moved, (a, b) => a.X == b.X && a.Z == b.Z && a.Yaw == b.Yaw).All(x => x), "the same answer twice");
            Check(moved.All(d => GuardMath.SamePost(d, d.X, d.Z)), "a guard posted there is on its post");

            // a dry post is left exactly where it is
            Func<float, float, bool> allDry = (x, z) => true;
            Check(GuardMath.TryDry(part, ring[0], allDry, null, out GuardPost same) && same.X == ring[0].X && same.Z == ring[0].Z && same.Yaw == ring[0].Yaw,
                "a dry post does not move");
            Check(GuardMath.TryDry(part, ring[0], null, null, out GuardPost none) && none.X == ring[0].X, "no ground check: the post stands");

            // a part entirely under water has nowhere: no guard (the server's old warning)
            Func<float, float, bool> sea = (x, z) => false;
            Check(!GuardMath.TryDry(part, ring[0], sea, null, out _), "all water: no spot");

            // dry ground only outside the part: still no spot, a guard never leaves its part
            Func<float, float, bool> farShore = (x, z) => z >= -550f;     // 23 m north of the middle, past the 20 m edge
            Check(!GuardMath.TryDry(part, ring[0], farShore, null, out _), "dry ground outside the part does not count");

            // a turned rectangle: the moved post stays inside it
            Territory rect = GuardRect(7, 0f, 0f, 30f, 6f, 37f);
            TerritoryShape rp = rect.GetPart(0);
            foreach (GuardPost p in GuardMath.Posts(rect, 4, 8))
            {
                Func<float, float, bool> east = (x, z) => x >= 15f;
                if (!GuardMath.TryDry(rp, p, east, null, out GuardPost d)) { Check(false, $"rect post {p.Index + 1} finds the dry east end"); continue; }
                Check(east(d.X, d.Z) && rp.Contains(new Vector3(d.X, 0f, d.Z)), $"rect post {p.Index + 1} dry and inside the turned rectangle");
            }

            // crowded: one tiny dry patch takes one guard; a second post that needs the same patch is refused rather than stacked
            Func<float, float, bool> patch = (x, z) => (x - 70f) * (x - 70f) + (z + 560f) * (z + 560f) < 1.2f;
            Check(GuardMath.TryDry(part, ring[0], patch, null, out GuardPost first), "the first post takes the tiny patch");
            Check(!GuardMath.TryDry(part, ring[1], patch, new List<GuardPost> { first }, out _), "the second is not stacked on top of it");
        }

        private static void GuardPostsMoved()
        {
            Territory t = GuardRect(1, 10f, 10f, 20f, 12f, 30f);
            List<GuardPost> before = GuardMath.Posts(t, 2, 8);
            Check(before.All(p => GuardMath.SamePost(p, p.X, p.Z)), "a guard on its own post is on its post");
            Check(before.All(p => GuardMath.SamePost(p, p.X + 0.3f, p.Z - 0.3f)), "and within half a metre of it still is (float noise in the ZDO)");

            Territory moved = t.Clone();
            moved.X += 15f;
            List<GuardPost> after = GuardMath.Posts(moved, 2, 8);
            Check(after.Zip(before, (a, b) => !GuardMath.SamePost(a, b.X, b.Z)).All(x => x), "moved 15 m: every post is somewhere else");

            Territory turned = t.Clone();
            turned.Yaw += 90f;
            Check(GuardMath.Posts(turned, 2, 8).Zip(before, (a, b) => !GuardMath.SamePost(a, b.X, b.Z)).Any(x => x), "turned: the posts turn with it");

            Territory renamed = t.Clone();
            renamed.Name = "Somewhere else entirely";
            renamed.Flags |= TerritoryFlags.BuildProtect;
            Check(GuardMath.Posts(renamed, 2, 8).Zip(before, (a, b) => GuardMath.SamePost(a, b.X, b.Z)).All(x => x), "renamed or its rules changed: nobody moves");
        }

        private static void GuardCallWords()
        {
            const string words = "guards";
            Check(GuardMath.ContainsCall("guards", words), "the word itself");
            Check(GuardMath.ContainsCall("GUARDS!", words) && GuardMath.ContainsCall("Help, guards!", words) && GuardMath.ContainsCall("\"guards\"", words), "in any case, with punctuation round it");
            Check(!GuardMath.ContainsCall("safeguards", words) && !GuardMath.ContainsCall("guardsman", words) && !GuardMath.ContainsCall("guard", words), "only as a whole word");
            Check(GuardMath.ContainsCall("to the watch!", "guards, watch") && GuardMath.ContainsCall("WACHE", "guards;wache"), "several words, comma or semicolon separated");
            Check(!GuardMath.ContainsCall("guards", "") && !GuardMath.ContainsCall("guards", null) && !GuardMath.ContainsCall("", words) && !GuardMath.ContainsCall(null, words),
                "no words, or no text: no call");
            Check(GuardMath.FirstWord("guards, watch") == "guards" && GuardMath.FirstWord(" ; ") == "" && GuardMath.FirstWord(null) == "", "the panel's hint quotes the first word");
        }

        private static void GuardNames()
        {
            GuardMath.LookFor(7, 0, out bool f1, out string n1);
            GuardMath.LookFor(7, 0, out bool f2, out string n2);
            Check(f1 == f2 && n1 == n2 && n1.Length > 0, "the same post of the same territory: the same guard, every time");
            int women = 0, total = 0;
            var names = new HashSet<string>();
            for (int t = 1; t <= 60; t++)
                for (int p = 0; p < 8; p++)
                {
                    GuardMath.LookFor(t, p, out bool female, out string name);
                    if (female) women++;
                    total++;
                    names.Add(name);
                }
            Check(women > total / 5 && women < total / 2, $"about one in three is a woman ({women} of {total})");
            Check(names.Count > 15, $"the names vary ({names.Count} different)");
            GuardMath.LookFor(123456u, out bool _, out string called);
            Check(called.Length > 0, "a called guard has a name too");

            Check(GuardMath.Title("Hrafnborg") == "guard of Hrafnborg", "a title names the territory");
            Check(GuardMath.Title("") == "guard" && GuardMath.Title(null) == "guard" && GuardMath.Title("   ") == "guard", "and none when there is no name");
            string longTitle = GuardMath.Title(new string('x', 60));
            Check(longTitle.Length == "guard of ".Length + GuardMath.MaxTitleTerritory && longTitle.EndsWith("..."), "a long name is cut short");
        }
    }
}
