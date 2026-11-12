using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace TerroritySmellatory.Tests
{
    // 0.4.0: sub-territories. A territory inside another that STARTS as a copy of its settings, follows them until a setting is
    // changed on it, and may differ in any one. Everything here is pure (Core/Inheritance.cs, Core/TerritoryEdits.cs, the file and
    // the wire), so the harness runs the same code the server does - the part of the server that is only wiring
    // (Net/TerritoryServer.cs) calls these functions in the order the tests walk them.
    internal static partial class Program
    {
        static partial void SubGroups()
        {
            Run("sub: the record travels - the parent and the follow marks ride in wire record 3, and a 0.3.x record reads as top level", SubWire);
            Run("sub: resolve - a sub-territory takes what it follows, keeps what it sets, and never follows its name or ground", SubResolve);
            Run("sub: resolve - rules merge one by one and are cleaned after (a barrier needs its no-monster rule, which may be followed)", SubResolveFlags);
            Run("sub: resolve - a change at the top flows down three levels; a level that sets a rule stops it there", SubResolveChain);
            Run("sub: resolve - people follow as a copy, and a bad link leaves a top-level territory instead of a loop", SubResolveOrphans);
            Run("sub: the tree - validate, check a new parent, descendants, height, ancestors, tree order", SubTree);
            Run("sub: stored records - canonical defaults, bake, own, pull, and what happens to the ones inside a removed territory", SubStored);
            Run("sub: precedence - a sub-territory governs its own ground over what it sits inside; nothing else moves", SubPrecedence);
            Run("sub: create and update - the automatic parent, the checks, the follow marks, a stale view dropped", SubEdits);
            Run("sub: sanitise - the marks need a parent; a followed rule is not judged on the stored bits alone", SubSanitize);
            Run("sub: the file - parent by id or name, absent keys follow, flags as a list or an object, written with only what it sets", SubFile);
            Run("sub: the file refuses a bad parent, naming where - and the tree check refuses loops, orphans and a chain too deep", SubFileErrors);
            Run("sub: the continent's members are members of its towns until a town sets its own people", SubPeople);
            Run("sub: banners follow the link, not the ground - a coastal town whose middle is outside its continent is still inside it", SubBanner);
            Run("check: --link turns a 0.3.x file's nested towns into sub-territories without changing anything they do", SubLink);
            Run("check: sub-territories - the tree it prints, the loop it refuses, the towns it says are not linked, the keys that freeze a parent's value", SubChecker);
        }

        /// <summary>A circle that sits inside <paramref name="parent"/> and follows everything it has: what a new sub-territory is.</summary>
        private static Territory Sub(int id, int parent, float x, float z, float r, string name = null)
        {
            Territory t = Circle(id, x, z, r, TerritoryFlags.None);
            t.Name = name ?? "Sub" + id;
            t.ParentId = parent;
            t.FollowFlags = Territory.KnownFlags;
            t.Follow = TerritoryFollow.All;
            return t;
        }

        /// <summary>A territory with every setting given a value, so a copy of it shows in every field.</summary>
        private static Territory Continent(int id = 1, float r = 1000f)
        {
            Territory t = Circle(id, 0f, 0f, r,
                TerritoryFlags.BuildProtect | TerritoryFlags.AccessProtect | TerritoryFlags.NoMonsterSpawns | TerritoryFlags.RunicBarrier |
                TerritoryFlags.Announce | TerritoryFlags.ShowOnMap | TerritoryFlags.ShowBorder, priority: 5);
            t.Name = "Svartland";
            t.PvP = PvPRule.ForcedOn;
            t.Color = new Color32(200, 70, 60, 255);
            t.OwnerId = 111L;
            t.OwnerName = "Owner";
            t.Members.Add(new TerritoryMember(222L, "Kin"));
            t.HealthRegen = 1.5f;
            t.StaminaRegen = 2f;
            t.EitrRegen = 0.5f;
            t.CarryBonus = 100f;
            t.ArmorBonus = 20f;
            t.EnterMessage = "Welcome to {name}";
            t.LeaveMessage = "Bye from {name}";
            t.CreatedBy = "Test";
            t.Revision = 3;
            return t;
        }

        private static Territory EffectiveOf(IReadOnlyList<Territory> stored, int id) => Inheritance.Resolve(stored).First(t => t.Id == id);

        private static void SubWire()
        {
            Check(Territory.RecordVersion == 3, "the record is version 3");

            Territory s = Sample();
            s.ParentId = 7;
            s.FollowFlags = TerritoryFlags.BuildProtect | TerritoryFlags.Announce;
            s.Follow = TerritoryFollow.Color | TerritoryFollow.People;
            var pkg = new ZPackage();
            s.Write(pkg);
            Territory back = Territory.Read(pkg.Transmit());
            SameTerritory(s, back, "a sub-territory's record");

            ZPackage snap = Protocol.WriteSnapshot(9L, 4, true, false, new[] { s, Sample() }, null);
            Protocol.Snapshot read = Protocol.ReadSnapshot(snap.Transmit());
            Check(read.Territories[0].ParentId == 7 && read.Territories[0].Follow == (TerritoryFollow.Color | TerritoryFollow.People), "in a snapshot");
            Check(read.Territories[1].ParentId == 0 && read.Territories[1].Follow == TerritoryFollow.None, "and the territory beside it is still top level");

            // What 0.3.x sent: record version 2, which stops after the parts - the same body less its last three ints.
            var outer = new ZPackage();
            Sample().Write(outer);
            byte[] body = outer.Transmit().ReadPackage().GetArray();
            byte[] v2 = new byte[body.Length - 12];
            Array.Copy(body, v2, v2.Length);
            BitConverter.GetBytes(2).CopyTo(v2, 0);
            var wrapped = new ZPackage();
            wrapped.Write(new ZPackage(v2));
            wrapped.Write(777);
            ZPackage rx = wrapped.Transmit();
            Territory old = Territory.Read(rx);
            SameTerritory(Sample(), old, "a version 2 record");
            Check(old.ParentId == 0 && old.FollowFlags == TerritoryFlags.None && old.Follow == TerritoryFollow.None && !old.IsSub,
                "it reads as a top-level territory that follows nothing");
            Check(rx.ReadInt() == 777, "and leaves the stream exactly after it");
        }

        private static void SubResolve()
        {
            Territory k = Continent();
            Territory c = Sub(2, 1, 100f, 0f, 50f, "Hrafnborg");
            c.Revision = 4;
            c.CreatedBy = "Admin";
            var stored = new List<Territory> { k, c };
            List<Territory> eff = Inheritance.Resolve(stored);

            Check(eff.Count == 2 && eff[0].Id == 1 && eff[1].Id == 2, "same territories, same order");
            Check(!ReferenceEquals(eff[0], k) && !ReferenceEquals(eff[1], c), "fresh records: nothing stored is handed out");
            SameTerritory(k, eff[0], "a top-level territory is unchanged");

            Territory e = eff[1];
            Check(e.Name == "Hrafnborg" && e.X == 100f && e.Z == 0f && e.HalfX == 50f && e.Kind == TerritoryShapeKind.Circle, "its name and ground are its own");
            Check(e.Revision == 4 && e.CreatedBy == "Admin", "and its history");
            Check(e.ParentId == 1 && e.FollowFlags == Territory.KnownFlags && e.Follow == TerritoryFollow.All, "the marks stay on the effective record, so the panel can show them");
            Check(e.Flags == k.Flags, "it has all its parent's rules");
            Check(e.PvP == PvPRule.ForcedOn && e.Priority == 5, "its PvP and priority");
            Check(e.Color.r == 200 && e.Color.g == 70 && e.Color.b == 60, "its colour");
            Check(e.OwnerId == 111L && e.OwnerName == "Owner" && e.Members.Count == 1 && e.Members[0].Id == 222L, "its owner and members");
            Check(e.HealthRegen == 1.5f && e.StaminaRegen == 2f && e.EitrRegen == 0.5f && e.CarryBonus == 100f && e.ArmorBonus == 20f, "its Sanctuary numbers");
            Check(e.EnterMessage == "Welcome to {name}" && e.LeaveMessage == "Bye from {name}", "its banner texts...");
            Check(e.FillPlaceholders(e.EnterMessage) == "Welcome to Hrafnborg", "...with {name} filled in by the sub-territory's own name");

            Check(c.Flags == TerritoryFlags.None && c.PvP == PvPRule.Unchanged && c.OwnerId == 0L && c.Members.Count == 0, "the stored record was not touched");
            e.Members.Add(new TerritoryMember(999L, "Stranger"));
            Check(k.Members.Count == 1 && eff[0].Members.Count == 1, "an effective list of people is a copy: editing it cannot reach the parent's");

            // Differ: it sets its PvP, its priority and two rules itself; everything else still follows.
            c.Follow &= ~(TerritoryFollow.PvP | TerritoryFollow.Priority);
            c.PvP = PvPRule.ForcedOff;
            c.Priority = -2;
            c.FollowFlags &= ~(TerritoryFlags.BuildProtect | TerritoryFlags.NoRaids);
            c.Flags = TerritoryFlags.NoRaids;
            e = EffectiveOf(stored, 2);
            Check(e.PvP == PvPRule.ForcedOff && e.Priority == -2, "what it sets is its own");
            Check(e.Flags == ((k.Flags & ~TerritoryFlags.BuildProtect) | TerritoryFlags.NoRaids), "building off and raids off-limits are its own; the other rules are the parent's");
            Check(e.OwnerId == 111L && e.Color.r == 200 && e.EnterMessage == "Welcome to {name}", "and what it did not touch still follows");
            Check(e.Follows(TerritoryFlags.AccessProtect) && !e.Follows(TerritoryFlags.BuildProtect) && !e.Follows(TerritoryFollow.PvP) && e.Follows(TerritoryFollow.Color),
                "Follows() reads the marks");
            Check(!k.Follows(TerritoryFlags.BuildProtect), "a top-level territory follows nothing");
        }

        private static void SubResolveFlags()
        {
            const TerritoryFlags NoMonsters = TerritoryFlags.NoMonsterSpawns, Barrier = TerritoryFlags.RunicBarrier, Keeps = TerritoryFlags.BarrierKeepsOutVisitors;
            Territory p = Circle(1, 0f, 0f, 500f, NoMonsters | Barrier | Keeps);

            // Sets the barrier off, follows the rest: the visitors option goes with it (it needs the barrier).
            Territory a = Sub(2, 1, 0f, 0f, 50f);
            a.FollowFlags &= ~Barrier;
            Check(EffectiveOf(new[] { p, a }, 2).Flags == NoMonsters, "its own 'barrier off' takes the visitors option under it along");

            // Sets the no-monster rule off: the barrier and the option under it are cleared, though both follow.
            Territory b = Sub(3, 1, 0f, 0f, 50f);
            b.FollowFlags &= ~NoMonsters;
            Check(EffectiveOf(new[] { p, b }, 3).Flags == TerritoryFlags.None, "its own 'no monsters off' clears the barrier it follows, and the option");

            // Sets the barrier ON while the parent has no no-monster rule: nothing to build on, so it stands off - and comes back when it has one.
            Territory q = Circle(10, 0f, 0f, 500f, TerritoryFlags.BuildProtect);
            Territory c = Sub(11, 10, 0f, 0f, 50f);
            c.FollowFlags &= ~Barrier;
            c.Flags = Barrier;
            Check(EffectiveOf(new[] { q, c }, 11).Flags == TerritoryFlags.BuildProtect, "a barrier it sets needs a no-monster rule: none, so it stands off");
            q.Flags |= NoMonsters;
            Check(EffectiveOf(new[] { q, c }, 11).Flags == (TerritoryFlags.BuildProtect | NoMonsters | Barrier), "and it is there when the parent gains the rule");

            // Its own no-monster rule makes the barrier it sets real.
            Territory d = Sub(12, 10, 0f, 0f, 50f);
            d.FollowFlags &= ~(NoMonsters | Barrier);
            d.Flags = NoMonsters | Barrier;
            Check(EffectiveOf(new[] { q, d }, 12).Flags == (TerritoryFlags.BuildProtect | NoMonsters | Barrier), "its own pair of rules stands on its own");
        }

        private static void SubResolveChain()
        {
            Territory k = Continent(1, 5000f);
            k.Flags = TerritoryFlags.BuildProtect | TerritoryFlags.ShowOnMap | TerritoryFlags.Announce | TerritoryFlags.MapNameOnly;
            Territory r = Sub(2, 1, 0f, 0f, 2000f, "Region");
            Territory t = Sub(3, 2, 0f, 0f, 100f, "Town");
            var stored = new List<Territory> { k, r, t };

            Territory town = EffectiveOf(stored, 3);
            Check(town.Flags == k.Flags && town.PvP == PvPRule.ForcedOn && town.Priority == 5, "a town follows its region, which follows the continent");

            k.Flags |= TerritoryFlags.NoRaids;
            k.PvP = PvPRule.ForcedOff;
            town = EffectiveOf(stored, 3);
            Check((town.Flags & TerritoryFlags.NoRaids) != 0 && town.PvP == PvPRule.ForcedOff, "a change on the continent reaches the town through the region");

            // The region sets map-name-only off for itself; the town follows the REGION, not the continent.
            r.FollowFlags &= ~TerritoryFlags.MapNameOnly;
            town = EffectiveOf(stored, 3);
            Check((town.Flags & TerritoryFlags.MapNameOnly) == 0 && (EffectiveOf(stored, 2).Flags & TerritoryFlags.MapNameOnly) == 0, "a level that sets a rule stops it there");
            Check((EffectiveOf(stored, 1).Flags & TerritoryFlags.MapNameOnly) != 0, "the continent keeps it");
            Check((town.Flags & TerritoryFlags.BuildProtect) != 0, "while the rest still flows through");

            // And the town sets one rule itself: nothing below the continent changes for the region.
            t.FollowFlags &= ~TerritoryFlags.BuildProtect;
            Check((EffectiveOf(stored, 3).Flags & TerritoryFlags.BuildProtect) == 0 && (EffectiveOf(stored, 2).Flags & TerritoryFlags.BuildProtect) != 0, "a town's own 'building open' touches only the town");
        }

        private static void SubResolveOrphans()
        {
            // A parent that does not exist: the territory stands on what it has, with no parent and no marks.
            Territory lost = Sub(5, 99, 0f, 0f, 20f);
            lost.Flags = TerritoryFlags.NoRaids;
            Territory e = Inheritance.Resolve(new[] { lost })[0];
            Check(e.ParentId == 0 && e.FollowFlags == TerritoryFlags.None && e.Follow == TerritoryFollow.None && e.Flags == TerritoryFlags.NoRaids, "a missing parent leaves a top-level territory with its own values");

            Territory self = Sub(6, 6, 0f, 0f, 20f);
            Check(Inheritance.Resolve(new[] { self })[0].ParentId == 0, "a territory inside itself is top level");

            // A loop must neither hang nor throw, and loses no territory.
            Territory x = Sub(7, 8, 0f, 0f, 20f);
            Territory y = Sub(8, 7, 0f, 0f, 20f);
            List<Territory> loop = Inheritance.Resolve(new[] { x, y });
            Check(loop.Count == 2 && loop[0].Id == 7 && loop[1].Id == 8, "a loop of two resolves to two territories");

            // A very long chain (nobody validated it) is cut, not recursed to the bottom.
            var chain = new List<Territory> { Circle(100, 0f, 0f, 10f, TerritoryFlags.BuildProtect) };
            for (int i = 1; i < 300; i++) chain.Add(Sub(100 + i, 99 + i, 0f, 0f, 10f));
            List<Territory> deep = Inheritance.Resolve(chain);
            Check(deep.Count == 300, "a 300-deep chain resolves without running out of stack");
        }

        private static void SubTree()
        {
            Territory k1 = Circle(1, 0f, 0f, 1000f, TerritoryFlags.None);
            Territory r = Sub(2, 1, 0f, 0f, 500f);
            Territory t = Sub(3, 2, 0f, 0f, 100f);
            Territory t2 = Sub(4, 2, 300f, 0f, 100f);
            Territory k2 = Circle(5, 5000f, 0f, 1000f, TerritoryFlags.None);
            Territory x = Sub(6, 5, 5000f, 0f, 100f);
            var all = new List<Territory> { k1, r, t, t2, k2, x };

            Check(Inheritance.Validate(all) == null, "a sound tree validates");

            string dangling = Inheritance.Validate(new[] { k1, Sub(7, 99, 0f, 0f, 5f) });
            Check(dangling != null && dangling.Contains("#7") && dangling.Contains("#99") && dangling.Contains("does not exist"), $"a parent that does not exist is named ({dangling})");
            string self = Inheritance.Validate(new[] { Sub(8, 8, 0f, 0f, 5f) });
            Check(self != null && self.Contains("inside itself"), $"a territory inside itself ({self})");
            string loop = Inheritance.Validate(new[] { Sub(9, 10, 0f, 0f, 5f), Sub(10, 9, 0f, 0f, 5f) });
            Check(loop != null && loop.Contains("inside itself through its parents"), $"a loop of two ({loop})");
            string outer = Inheritance.Validate(new[] { k1, Sub(11, 12, 0f, 0f, 5f), Sub(12, 13, 0f, 0f, 5f), Sub(13, 12, 0f, 0f, 5f) });
            Check(outer != null, $"a territory that leads into a loop it is not part of is still refused ({outer})");
            string negative = Inheritance.Validate(new[] { new Territory { Id = 14, ParentId = -5 } });
            Check(negative != null, "and a negative parent");

            List<Territory> ladder = Ladder(9);   // eight levels below the top: the most allowed
            Check(Inheritance.Validate(ladder) == null, "eight levels below the top territory are allowed");
            string tooDeep = Inheritance.Validate(Ladder(10));
            Check(tooDeep != null && tooDeep.Contains("9 levels down") && tooDeep.Contains("at most 8"), $"nine are not ({tooDeep})");

            // CheckParent: the same rules, asked before the change is made.
            Check(Inheritance.CheckParent(all, 3, 5) == null, "a town may move under another continent");
            Check(Inheritance.CheckParent(all, 3, 0) == null, "or to the top level");
            string intoChild = Inheritance.CheckParent(all, 2, 3);
            Check(intoChild != null && intoChild.Contains("own sub-territories"), $"a region cannot be put inside its own town ({intoChild})");
            Check(Inheritance.CheckParent(all, 1, 4) != null, "nor a continent inside something three levels down in it");
            Check(Inheritance.CheckParent(all, 2, 2) != null, "nor inside itself");
            string missing = Inheritance.CheckParent(all, 3, 99);
            Check(missing != null && missing.Contains("#99"), $"nor inside nothing ({missing})");
            Check(Inheritance.CheckParent(all, 0, 3) == null, "a territory that does not exist yet may be created inside any of them");

            // Depth is counted over the whole subtree that moves with the territory.
            List<Territory> chain = Ladder(7);                                // ids 1..7, 7 is six levels below the top
            Territory q = Circle(50, 9000f, 0f, 10f, TerritoryFlags.None);   // q > q1 > q2: two levels below q
            Territory q1 = Sub(51, 50, 9000f, 0f, 9f);
            Territory q2 = Sub(52, 51, 9000f, 0f, 8f);
            var world = new List<Territory>(chain) { q, q1, q2 };
            Check(Inheritance.CheckParent(world, 50, 6) == null, "a subtree two levels deep fits under a territory five levels down (it ends at the 7th)...");
            string tooLow = Inheritance.CheckParent(world, 50, 7);
            Check(tooLow != null && tooLow.Contains("levels down"), $"...and not under one six levels down, which would put its bottom at the 9th ({tooLow})");

            // The walks.
            Check(string.Join(",", Inheritance.Descendants(all, 1).Select(d => d.Id)) == "2,3,4", "descendants, nearest first");
            Check(Inheritance.Descendants(all, 3).Count == 0 && Inheritance.Descendants(all, 0).Count == 0, "none for a leaf, and none for 'no territory'");
            Check(Inheritance.Height(all, 1) == 2 && Inheritance.Height(all, 2) == 1 && Inheritance.Height(all, 3) == 0, "height: levels below");
            Check(string.Join(",", Inheritance.Ancestors(all, 3).Select(d => d.Id)) == "2,1", "ancestors, nearest first");
            Check(Inheritance.Ancestors(all, 1).Count == 0, "a top-level territory has none");
            Check(Inheritance.IsInside(all, 3, 1) && Inheritance.IsInside(all, 3, 2) && !Inheritance.IsInside(all, 1, 3) && !Inheritance.IsInside(all, 6, 1) && !Inheritance.IsInside(all, 3, 3),
                "IsInside is the ancestor relation, not 'overlaps'");
            Check(string.Join(",", Inheritance.ChildrenOf(all, 2).Select(d => d.Id)) == "3,4", "children, in list order");

            // The tree as the panel lists it.
            string rows = string.Join(" ", Inheritance.TreeOrder(all).Select(row => new string('>', row.Depth) + row.Territory.Id));
            Check(rows == "1 >2 >>3 >>4 5 >6", $"a tree, depth first ({rows})");
            string byName = string.Join(" ", Inheritance.TreeOrder(all, (a, b) => b.Id.CompareTo(a.Id)).Select(row => new string('>', row.Depth) + row.Territory.Id));
            Check(byName == "5 >6 1 >2 >>4 >>3", $"siblings in the order asked for ({byName})");

            var odd = new List<Territory>(all) { Sub(20, 99, 0f, 0f, 5f), Sub(21, 22, 0f, 0f, 5f), Sub(22, 21, 0f, 0f, 5f) };
            List<Inheritance.TreeRow> oddRows = Inheritance.TreeOrder(odd);
            Check(oddRows.Count == odd.Count && oddRows.Select(row => row.Territory.Id).Distinct().Count() == odd.Count, "an orphan and a loop are shown too: nothing is ever left out of the list");
            Check(oddRows.First(row => row.Territory.Id == 20).Depth == 0, "an orphan is shown at the top level");
        }

        /// <summary>Territories 1..n, each inside the one before it: n-1 levels below the top.</summary>
        private static List<Territory> Ladder(int n)
        {
            var list = new List<Territory> { Circle(1, 0f, 0f, 1000f, TerritoryFlags.None) };
            for (int i = 2; i <= n; i++) list.Add(Sub(i, i - 1, 0f, 0f, 1000f - 10f * i));
            return list;
        }

        private static void SubStored()
        {
            // Canonical: what a sub-territory follows is the plain default, whatever it was holding.
            Territory s = Sub(2, 1, 0f, 0f, 50f);
            s.Follow = TerritoryFollow.All & ~TerritoryFollow.Priority;
            s.FollowFlags = Territory.KnownFlags & ~TerritoryFlags.NoRaids;
            s.Flags = TerritoryFlags.BuildProtect | TerritoryFlags.NoRaids;     // BuildProtect is followed: stale. NoRaids is its own.
            s.PvP = PvPRule.ForcedOn;
            s.Priority = 7;                                                    // its own
            s.Color = new Color32(1, 2, 3, 255);
            s.OwnerId = 9L;
            s.OwnerName = "Old";
            s.Members.Add(new TerritoryMember(5L, "Old"));
            s.HealthRegen = 4f;
            s.CarryBonus = 50f;
            s.EnterMessage = "stale";
            s.LeaveMessage = "stale";
            Inheritance.Canonicalize(s);
            Check(s.Flags == TerritoryFlags.NoRaids, "a followed rule's stored bit is cleared; the one it sets stays");
            Check(s.PvP == PvPRule.Unchanged && s.Priority == 7, "a followed group goes back to its default; the one it sets stays");
            Check(s.OwnerId == 0L && s.OwnerName == "" && s.Members.Count == 0, "people");
            Check(s.HealthRegen == 1f && s.StaminaRegen == 1f && s.EitrRegen == 1f && s.CarryBonus == 0f && s.ArmorBonus == 0f, "Sanctuary numbers");
            Check(s.EnterMessage == "" && s.LeaveMessage == "" && s.Color.r == Territory.DefaultColor.r && s.Color.g == Territory.DefaultColor.g, "texts and colour");
            Territory again = s.Clone();
            Inheritance.Canonicalize(again);
            SameTerritory(s, again, "canonicalising twice");

            Territory top = Circle(3, 0f, 0f, 5f, TerritoryFlags.BuildProtect);
            top.Follow = TerritoryFollow.All;
            top.FollowFlags = TerritoryFlags.BuildProtect;
            Inheritance.Canonicalize(top);
            Check(top.Follow == TerritoryFollow.None && top.FollowFlags == TerritoryFlags.None && top.Flags == TerritoryFlags.BuildProtect, "a territory with no parent follows nothing, and keeps all it has");

            // Bake and OwnFrom.
            Territory k = Continent();
            Territory c = Sub(2, 1, 100f, 0f, 50f);
            Territory eff = EffectiveOf(new[] { k, c }, 2);
            Territory baked = c.Clone();
            Inheritance.Bake(baked, eff);
            Check(baked.Follow == TerritoryFollow.None && baked.FollowFlags == TerritoryFlags.None, "baked: it follows nothing now");
            Check(baked.Flags == k.Flags && baked.PvP == k.PvP && baked.OwnerId == 111L && baked.Members.Count == 1 && baked.EnterMessage == k.EnterMessage, "baked: it holds what it had been following");
            Check(baked.Name == c.Name && baked.X == 100f && baked.ParentId == 1, "baked: name, ground and parent are as they were");

            Territory half = c.Clone();
            Inheritance.OwnFrom(half, eff, TerritoryFollow.People);
            Check((half.Follow & TerritoryFollow.People) == 0 && (half.Follow & TerritoryFollow.PvP) != 0 && half.OwnerId == 111L && half.Members.Count == 1, "OwnFrom takes one group as its own and leaves the rest following");

            // The panel's moves, on an effective copy, against its effective parent.
            Territory view = eff.Clone();
            view.FollowFlags &= ~TerritoryFlags.BuildProtect;       // the admin switches building off on the town...
            view.Flags &= ~TerritoryFlags.BuildProtect;
            view.Follow &= ~TerritoryFollow.PvP;                    // ...and picks its own PvP
            view.PvP = PvPRule.ForcedOff;
            Inheritance.Pull(view, k, TerritoryFlags.BuildProtect);
            Check(view.Follows(TerritoryFlags.BuildProtect) && (view.Flags & TerritoryFlags.BuildProtect) != 0, "Pull: the rule follows again and shows the parent's value");
            Inheritance.Pull(view, k, TerritoryFollow.PvP);
            Check(view.Follows(TerritoryFollow.PvP) && view.PvP == PvPRule.ForcedOn, "Pull: so does a group");
            Inheritance.Own(view, TerritoryFollow.PvP);
            Check(!view.Follows(TerritoryFollow.PvP) && view.PvP == PvPRule.ForcedOn, "Own: sets it as it stands");
            Inheritance.Own(view, TerritoryFlags.NoRaids);
            Check(!view.Follows(TerritoryFlags.NoRaids), "Own on a rule");
            Inheritance.PullAll(view, k);
            Check(view.Follow == TerritoryFollow.All && view.FollowFlags == Territory.KnownFlags && view.Flags == k.Flags && view.PvP == k.PvP && view.Name == c.Name && view.X == 100f,
                "PullAll: a copy of the parent again, apart from name and ground");
            Inheritance.Pull(null, k, TerritoryFollow.PvP);
            Inheritance.Pull(view, null, TerritoryFollow.PvP);
            Check(true, "a missing territory is ignored, not thrown at");

            // What a sub-territory sets itself, by name (terr_list prints it).
            Territory named = Sub(2, 1, 0f, 0f, 5f);
            Check(Inheritance.SetsItself(named).Count == 0, "a sub-territory that follows everything sets nothing");
            named.FollowFlags &= ~(TerritoryFlags.BuildProtect | TerritoryFlags.MapNameOnly);
            named.Follow &= ~(TerritoryFollow.PvP | TerritoryFollow.People);
            Check(string.Join("; ", Inheritance.SetsItself(named)) == "Building; Name only on the map; PvP; Owner and members", $"rules first, then groups ({string.Join("; ", Inheritance.SetsItself(named))})");
            Check(Inheritance.SetsItself(Circle(3, 0f, 0f, 5f, TerritoryFlags.None)).Count == 0 && Inheritance.SetsItself(null).Count == 0, "a top-level territory sets 'nothing' (it sets everything: there is no parent to compare with)");
            bool allLabelled = true;
            foreach (TerritoryFlags rule in Enum.GetValues(typeof(TerritoryFlags)))
                if (rule != TerritoryFlags.None && Inheritance.RuleLabel(rule) == rule.ToString() && rule != TerritoryFlags.Sanctuary) allLabelled = false;
            Check(allLabelled, "every rule has a label of its own");

            // Refresh: a copy being edited follows the parent's CURRENT values.
            Territory live = EffectiveOf(new[] { k, c }, 2);
            live.FollowFlags &= ~TerritoryFlags.BuildProtect;
            live.Flags &= ~TerritoryFlags.BuildProtect;
            live.Follow &= ~TerritoryFollow.PvP;
            live.PvP = PvPRule.ForcedOff;
            Territory newer = Continent();
            newer.Flags = TerritoryFlags.NoRaids | TerritoryFlags.Announce | TerritoryFlags.BuildProtect;
            newer.Priority = 11;
            newer.EnterMessage = "Hail";
            Inheritance.Refresh(live, newer);
            Check(live.Priority == 11 && live.EnterMessage == "Hail" && (live.Flags & TerritoryFlags.NoRaids) != 0, "Refresh: what it follows moves to the parent's new values");
            Check(live.PvP == PvPRule.ForcedOff && (live.Flags & TerritoryFlags.BuildProtect) == 0, "Refresh: and what it sets itself stays");
            Territory topView = Circle(3, 0f, 0f, 5f, TerritoryFlags.BuildProtect);
            Inheritance.Refresh(topView, newer);
            Check(topView.Flags == TerritoryFlags.BuildProtect, "Refresh: a territory with no parent is left alone");

            // FollowWhatIsUnchanged: a draft going inside something keeps what the admin changed.
            Territory fresh = new Territory { Flags = Territory.DraftFlags };
            Territory draft = fresh.Clone();
            draft.Flags &= ~TerritoryFlags.BuildProtect;
            draft.PvP = PvPRule.ForcedOff;
            draft.OwnerId = 5L;
            draft.OwnerName = "Me";
            Inheritance.FollowWhatIsUnchanged(draft, fresh);
            Check(draft.FollowFlags == (Territory.KnownFlags & ~TerritoryFlags.BuildProtect), "FollowWhatIsUnchanged: every rule but the one changed follows");
            Check(draft.Follow == (TerritoryFollow.All & ~(TerritoryFollow.PvP | TerritoryFollow.People)), "FollowWhatIsUnchanged: every group but the two changed follows");
            Territory untouched = fresh.Clone();
            Inheritance.FollowWhatIsUnchanged(untouched, fresh);
            Check(untouched.FollowFlags == Territory.KnownFlags && untouched.Follow == TerritoryFollow.All, "FollowWhatIsUnchanged: an untouched draft follows everything");

            // Removing a territory: the ones inside keep what they do and move up.
            Territory K = Continent();
            Territory R = Sub(2, 1, 0f, 0f, 500f, "Region");
            R.Follow &= ~(TerritoryFollow.PvP | TerritoryFollow.Priority);
            R.PvP = PvPRule.ForcedOff;
            R.Priority = 9;
            R.FollowFlags &= ~TerritoryFlags.ShowBorder;
            R.Flags = TerritoryFlags.None;
            Territory T = Sub(3, 2, 0f, 0f, 100f, "Town");
            T.Revision = 6;
            var stored = new List<Territory> { K, R, T };
            List<Territory> before = Inheritance.Resolve(stored);

            List<Territory> freed = Inheritance.ReleaseChildren(stored, before, 2, R.ParentId);
            Check(freed.Count == 1 && freed[0].Id == 3 && freed[0].ParentId == 1 && freed[0].Revision == 7, "the town moves under the region's parent, and its record changed (revision up)");
            Check(ReferenceEquals(stored[2], T) && T.ParentId == 2 && T.Revision == 6, "the stored town is not edited: the caller swaps in the copy");
            var after = new List<Territory> { K, freed[0] };
            Territory t0 = before.First(e => e.Id == 3), t1 = Inheritance.Resolve(after).First(e => e.Id == 3);
            Check(t1.Flags == t0.Flags && t1.PvP == t0.PvP && t1.Priority == t0.Priority && t1.OwnerId == t0.OwnerId && t1.EnterMessage == t0.EnterMessage && t1.Color.r == t0.Color.r &&
                  t1.HealthRegen == t0.HealthRegen && t1.Members.Count == t0.Members.Count,
                "after the region is gone the town means exactly what it meant");
            Check(t1.PvP == PvPRule.ForcedOff && t1.Priority == 9, "(including what it had from the REGION, not the continent)");
            Check(freed[0].Follow == TerritoryFollow.None && freed[0].FollowFlags == TerritoryFlags.None, "it follows nothing any more");

            // Taking a top-level territory away: its sub-territories become top level.
            List<Territory> orphans = Inheritance.ReleaseChildren(new List<Territory> { K, R }, Inheritance.Resolve(new[] { K, R }), 1, 0);
            Territory topRegion = Inheritance.Resolve(new[] { orphans[0] })[0];
            Check(orphans.Count == 1 && topRegion.ParentId == 0 && topRegion.Flags == Inheritance.Resolve(new[] { K, R })[1].Flags, "a removed top-level territory leaves its regions top level with their rules");

            // Combined away: the other's sub-territories go under the target.
            List<Territory> moved = Inheritance.ReleaseChildren(stored, before, 2, 77);
            Check(moved.Count == 1 && moved[0].ParentId == 77, "or under whatever the caller names");
        }

        private static void SubPrecedence()
        {
            // A town bigger than its parent, and one with a lower priority, still govern their own ground.
            Territory p = Circle(1, 0f, 0f, 100f, TerritoryFlags.BuildProtect);
            Territory big = Sub(2, 1, 0f, 0f, 150f);
            TerritoryIndex.Set(new[] { p, Circle(2, 0f, 0f, 150f, TerritoryFlags.None) });
            Check(TerritoryIndex.All[0].Id == 1, "(control: with no link the smaller one governs)");
            TerritoryIndex.Set(new[] { p, big });
            Check(TerritoryIndex.All[0].Id == 2 && TerritoryIndex.GoverningAt(new Vector3(0f, 0f, 0f)).Id == 2 && TerritoryIndex.GoverningAt(new Vector3(120f, 0f, 0f)).Id == 2,
                "a sub-territory bigger than its parent still governs");

            Territory high = Circle(1, 0f, 0f, 500f, TerritoryFlags.BuildProtect, priority: 10);
            Territory low = Sub(2, 1, 0f, 0f, 50f);
            low.Priority = 0;
            TerritoryIndex.Set(new[] { high, low });
            Check(TerritoryIndex.GoverningAt(new Vector3(10f, 0f, 0f)).Id == 2, "and so does one whose parent has the higher priority");

            // A sub-territory the usual order puts below its top territory takes the place just ahead of it, so against outsiders it
            // ranks where its top does; unrelated ones keep their places.
            Territory t = Circle(1, 0f, 0f, 300f, TerritoryFlags.None, priority: 10);
            Territory u = Circle(2, 100f, 0f, 300f, TerritoryFlags.None, priority: 5);
            Territory s = Sub(3, 1, 0f, 0f, 20f);
            s.Priority = 0;
            TerritoryIndex.Set(new[] { s, u, t });
            Check(string.Join(",", TerritoryIndex.All.Select(x => x.Id)) == "3,1,2", "the town moves up to just before its own territory; the unrelated one stays below both");
            Check(TerritoryIndex.GoverningAt(new Vector3(200f, 0f, 0f)).Id == 1, "(so where the two bigger ones overlap, the higher priority still wins)");

            // One whose own priority beats its top's keeps that higher place (it is only ever lifted above its ancestors, never lowered to them).
            Territory top2 = Circle(21, 0f, 0f, 300f, TerritoryFlags.None, priority: 5);
            Territory out2 = Circle(22, 100f, 0f, 300f, TerritoryFlags.None, priority: 6);
            Territory kid2 = Sub(23, 21, 0f, 0f, 20f);
            kid2.Priority = 7;
            TerritoryIndex.Set(new[] { top2, out2, kid2 });
            Check(string.Join(",", TerritoryIndex.All.Select(x => x.Id)) == "23,22,21", "a sub-territory whose priority beats an outsider's outranks it, though its top territory does not");

            // Siblings keep their order among themselves; grandchildren come first of all.
            Territory a = Sub(11, 10, 0f, 0f, 30f), b = Sub(12, 10, 0f, 0f, 20f), g = Sub(13, 11, 0f, 0f, 10f);
            Territory root = Circle(10, 0f, 0f, 100f, TerritoryFlags.None, priority: 3);
            root.Name = "Root";
            TerritoryIndex.Set(new[] { root, a, b, g });
            string order = string.Join(",", TerritoryIndex.All.Select(x => x.Id));
            Check(order.IndexOf("13") < order.IndexOf("11") && order.IndexOf("11") < order.IndexOf("10") && order.IndexOf("12") < order.IndexOf("10"), $"a grandchild before its parent before its grandparent ({order})");
            Check(order == "13,12,11,10" || order == "12,13,11,10" || order == "13,11,12,10", $"and nothing else is shuffled ({order})");

            // With no links at all the index is exactly what it was.
            var plain = new List<Territory> { Circle(1, 0f, 0f, 100f, TerritoryFlags.None), Circle(2, 20f, 0f, 10f, TerritoryFlags.None) };
            TerritoryIndex.Set(plain);
            Check(TerritoryIndex.All[0].Id == 2 && TerritoryIndex.All[1].Id == 1, "no links: smaller first, as always");
            var sorted = new List<Territory> { plain[1], plain[0] };
            Check(ReferenceEquals(Inheritance.ChildrenFirst(sorted), sorted), "and the list is not even copied");

            // A list with a loop in it still produces every territory once.
            var loop = new List<Territory> { Sub(30, 31, 0f, 0f, 5f), Sub(31, 30, 0f, 0f, 5f), Circle(32, 0f, 0f, 9f, TerritoryFlags.None) };
            Check(Inheritance.ChildrenFirst(loop).Count == 3, "a loop does not lose or repeat a territory");
        }

        private static void SubEdits()
        {
            Territory k = Continent(1, 1000f);
            Territory town = Sub(2, 1, 100f, 0f, 50f, "Hrafnborg");
            TerritoryIndex.Set(Inheritance.Resolve(new[] { k, town }));
            IReadOnlyList<Territory> stored = new List<Territory> { k, town };

            // A new territory marked inside the town: the automatic parent is the innermost one.
            Territory inTown = new Territory { Name = "Bank", Kind = TerritoryShapeKind.Circle, X = 110f, Z = 0f, HalfX = 8f, HalfZ = 8f, ParentId = Territory.AutoParent, Flags = Territory.DraftFlags };
            inTown.FollowFlags = Territory.KnownFlags;
            inTown.Follow = TerritoryFollow.All;
            Check(TerritoryEdits.PrepareNew(stored, TerritoryIndex.All, inTown, 600f) == null && inTown.ParentId == 2, "marked inside a town inside a continent: the parent is the town");
            Territory inLand = new Territory { Name = "Camp", Kind = TerritoryShapeKind.Circle, X = 600f, Z = 0f, HalfX = 8f, HalfZ = 8f, ParentId = Territory.AutoParent, Follow = TerritoryFollow.All, FollowFlags = Territory.KnownFlags };
            Check(TerritoryEdits.PrepareNew(stored, TerritoryIndex.All, inLand, 600f) == null && inLand.ParentId == 1, "marked on the continent only: the continent");
            Territory open = new Territory { Name = "Far", Kind = TerritoryShapeKind.Circle, X = 9000f, Z = 0f, HalfX = 8f, HalfZ = 8f, ParentId = Territory.AutoParent, Follow = TerritoryFollow.All, FollowFlags = Territory.KnownFlags, Flags = Territory.DraftFlags };
            Check(TerritoryEdits.PrepareNew(stored, TerritoryIndex.All, open, 600f) == null && open.ParentId == 0, "marked in open country: top level...");
            Check(open.Follow == TerritoryFollow.None && open.FollowFlags == TerritoryFlags.None && open.Flags == Territory.DraftFlags, "...with the settings it was given, which it has nothing to follow instead of");
            Territory none = new Territory { Name = "Alone", X = 100f, Z = 0f, HalfX = 8f, ParentId = 0 };
            Check(TerritoryEdits.PrepareNew(stored, TerritoryIndex.All, none, 600f) == null && none.ParentId == 0, "a territory marked as top level stays top level even inside one");
            Territory explicitly = new Territory { Name = "Named", X = 9000f, Z = 0f, HalfX = 8f, ParentId = 1, Follow = TerritoryFollow.All, FollowFlags = Territory.KnownFlags };
            Check(TerritoryEdits.PrepareNew(stored, TerritoryIndex.All, explicitly, 600f) == null && explicitly.ParentId == 1, "a named parent is taken whether or not the ground lies inside it");
            Territory lost = new Territory { Name = "Lost", X = 0f, Z = 0f, HalfX = 8f, ParentId = 77 };
            string refused = TerritoryEdits.PrepareNew(stored, TerritoryIndex.All, lost, 600f);
            Check(refused != null && refused.Contains("#77"), $"a parent that does not exist is refused ({refused})");
            Check(TerritoryEdits.PrepareNew(stored, TerritoryIndex.All, new Territory { X = float.NaN, HalfX = 5f }, 600f) != null, "and so is ground that is not a number");

            // STARTS with the big territory's settings...
            Territory fresh = new Territory { Name = "Newtown", Kind = TerritoryShapeKind.Circle, X = 600f, Z = 100f, HalfX = 40f, ParentId = Territory.AutoParent, Flags = Territory.DraftFlags,
                FollowFlags = Territory.KnownFlags, Follow = TerritoryFollow.All };
            Check(TerritoryEdits.PrepareNew(stored, TerritoryIndex.All, fresh, 600f) == null, "a draft that follows everything is accepted");
            fresh.Id = 3;
            Check(fresh.Flags == TerritoryFlags.None && fresh.PvP == PvPRule.Unchanged && fresh.OwnerId == 0L, "and stored holding nothing of the draft's own values");
            Territory fresher = EffectiveOf(new[] { k, town, fresh }, 3);
            Check(fresher.Flags == k.Flags && fresher.PvP == k.PvP && fresher.Priority == k.Priority && fresher.OwnerId == k.OwnerId && fresher.EnterMessage == k.EnterMessage,
                "it starts as a copy of its parent's settings");

            // ...and is allowed to differ.
            Territory differ = new Territory { Name = "Marketville", Kind = TerritoryShapeKind.Circle, X = 650f, Z = 100f, HalfX = 40f, ParentId = Territory.AutoParent, Flags = TerritoryFlags.None,
                FollowFlags = Territory.KnownFlags & ~TerritoryFlags.BuildProtect, Follow = TerritoryFollow.All & ~TerritoryFollow.PvP, PvP = PvPRule.ForcedOff };
            Check(TerritoryEdits.PrepareNew(stored, TerritoryIndex.All, differ, 600f) == null, "a draft with a few settings of its own is accepted");
            differ.Id = 4;
            Territory differing = EffectiveOf(new[] { k, town, differ }, 4);
            Check((differing.Flags & TerritoryFlags.BuildProtect) == 0 && (differing.Flags & TerritoryFlags.AccessProtect) != 0 && differing.PvP == PvPRule.ForcedOff && differing.OwnerId == 111L,
                "building is open and PvP is off, as it set; everything else is the continent's");

            // Update.
            Territory view = EffectiveOf(new[] { k, town }, 2);                   // what the admin's panel holds
            view.Follow &= ~TerritoryFollow.PvP;
            view.PvP = PvPRule.ForcedOff;
            view.Flags = TerritoryFlags.Announce;                                  // a stale, wrong view of the followed rules: must be dropped
            view.Name = "Hrafnborg II";
            string bad = TerritoryEdits.Update(stored, town, view, 600f, out Territory updated);
            Check(bad == null && updated.Name == "Hrafnborg II" && updated.PvP == PvPRule.ForcedOff && updated.Revision == town.Revision + 1, "the admin's change is applied and the revision moves");
            Check((updated.Flags & ~(updated.FollowFlags ^ Territory.KnownFlags)) == updated.Flags && (updated.Flags & updated.FollowFlags) == TerritoryFlags.None, "bits of followed rules are cleared from the stored record");
            Check(updated.Flags == TerritoryFlags.None, "(the stale view of the continent's rules is gone, not saved)");
            Check(ReferenceEquals(stored[1], town) && town.PvP == PvPRule.Unchanged, "the stored record the request was laid on is untouched");

            // A new parent: allowed, and the marks stay.
            Territory other = Circle(9, 5000f, 0f, 500f, TerritoryFlags.None);
            IReadOnlyList<Territory> two = new List<Territory> { k, town, other };
            Territory moveTo = town.Clone();
            moveTo.ParentId = 9;
            Check(TerritoryEdits.Update(two, town, moveTo, 600f, out Territory moved) == null && moved.ParentId == 9 && moved.Follow == TerritoryFollow.All, "a sub-territory moved under another top-level territory keeps following");
            Territory leave = town.Clone();
            leave.ParentId = 0;
            Check(TerritoryEdits.Update(two, town, leave, 600f, out Territory freed) == null && freed.ParentId == 0 && freed.Follow == TerritoryFollow.None, "taken out to the top level, the marks go");
            Territory keep = town.Clone();
            keep.ParentId = Territory.AutoParent;
            Check(TerritoryEdits.Update(two, town, keep, 600f, out Territory kept) == null && kept.ParentId == 1, "an update that names no parent leaves it where it is");

            // A top-level territory put inside another: its settings stay as they are (it follows nothing until asked to).
            Territory topNow = other.Clone();
            topNow.ParentId = 1;
            Check(TerritoryEdits.Update(two, other, topNow, 600f, out Territory nested) == null && nested.ParentId == 1 && nested.Follow == TerritoryFollow.None, "a top-level territory put inside another still means what it did");

            // Loops and depth are refused, and nothing is half done.
            Territory loop = k.Clone();
            loop.ParentId = 2;
            string refusedLoop = TerritoryEdits.Update(stored, k, loop, 600f, out Territory none2);
            Check(refusedLoop != null && refusedLoop.Contains("own sub-territories"), $"a continent cannot be put inside its own town ({refusedLoop})");
            List<Territory> ladder = Ladder(9);
            Territory lifted = new Territory { Id = 99, Name = "Top", Kind = TerritoryShapeKind.Circle, X = 0f, Z = 0f, HalfX = 10f };
            var withTop = new List<Territory>(ladder) { lifted };
            Territory deepOk = lifted.Clone();
            deepOk.ParentId = 8;
            Check(TerritoryEdits.Update(withTop, lifted, deepOk, 600f, out Territory _) == null, "a territory may go under the eighth: it is then eight levels below the top");
            Territory deepBad = lifted.Clone();
            deepBad.ParentId = 9;
            string tooDeep = TerritoryEdits.Update(withTop, lifted, deepBad, 600f, out Territory _);
            Check(tooDeep != null && tooDeep.Contains("at most 8"), $"under the ninth it would be nine levels down: refused ({tooDeep})");

            // Combine: a territory and one inside it cannot be combined, either way round.
            string a1 = TerritoryEdits.CombineRefusal(stored, k, town), a2 = TerritoryEdits.CombineRefusal(stored, town, k);
            Check(a1 != null && a1.Contains("inside") && a2 != null && a2.Contains("inside"), "a continent and its town cannot be combined, whichever is kept");
            Check(TerritoryEdits.CombineRefusal(two, k, other) == null && TerritoryEdits.CombineRefusal(two, town, other) == null, "unrelated territories can");

            // Combining into a town that follows its continent's people: if nobody new joins it goes on following; if somebody does,
            // it takes the continent's people as its own list first, so the newcomer is kept and not followed away.
            List<Territory> storedY = new List<Territory> { Continent(1), Sub(2, 1, 100f, 0f, 50f, "Hrafnborg") };
            List<Territory> effY = Inheritance.Resolve(storedY);
            Territory sameFolk = Circle(3, 5000f, 0f, 50f, TerritoryFlags.None);
            sameFolk.OwnerId = 111L;
            sameFolk.OwnerName = "Owner";
            sameFolk.Members.Add(new TerritoryMember(222L, "Kin"));
            Territory into1 = storedY[1].Clone();
            string r1 = TerritoryEdits.CombineStored(into1, effY[1], sameFolk, 4, out int j1);
            Check(r1 == null && j1 == 0 && into1.PartCount == 2 && (into1.Follow & TerritoryFollow.People) != 0 && into1.Members.Count == 0 && into1.OwnerId == 0L,
                "combining people the town already has: only the ground is added, and the town goes on following its continent's people");

            Territory newcomer = Circle(4, 6000f, 0f, 50f, TerritoryFlags.None);
            newcomer.Members.Add(new TerritoryMember(444L, "New"));
            Territory into2 = storedY[1].Clone();
            string r2 = TerritoryEdits.CombineStored(into2, effY[1], newcomer, 4, out int j2);
            Check(r2 == null && j2 == 1 && into2.PartCount == 2 && (into2.Follow & TerritoryFollow.People) == 0 && into2.OwnerId == 111L &&
                  string.Join(",", into2.Members.Select(m => m.Id).OrderBy(x => x)) == "222,444",
                "combining somebody new: the town takes its continent's people as its own list first, and adds them");

            Territory into3 = storedY[1].Clone();
            string r3 = TerritoryEdits.CombineStored(into3, effY[1], newcomer, 1, out int _);
            Check(r3 != null && into3.PartCount == 1 && (into3.Follow & TerritoryFollow.People) != 0 && into3.Members.Count == 0, "a refusal (over MaxParts) leaves the target as it was, still following");

            Territory loner = Circle(5, 7000f, 0f, 50f, TerritoryFlags.None), lonerCopy = loner.Clone();
            string r4 = TerritoryEdits.CombineStored(loner, loner, newcomer, 4, out int j4), r5 = TerritoryEdits.Combine(lonerCopy, newcomer, 4, out int j5);
            Check(r4 == null && r5 == null && j4 == j5 && loner.Members.Count == lonerCopy.Members.Count && loner.PartCount == lonerCopy.PartCount, "a territory that follows no people combines exactly as it always did");
        }

        private static void SubSanitize()
        {
            var junk = new Territory { Id = 1, Name = "Top", HalfX = 10f, Flags = TerritoryFlags.BuildProtect, ParentId = 0, FollowFlags = TerritoryFlags.BuildProtect, Follow = TerritoryFollow.All };
            Check(junk.Sanitize(100f) && junk.FollowFlags == TerritoryFlags.None && junk.Follow == TerritoryFollow.None, "marks without a parent are dropped");

            var neg = new Territory { Id = 2, Name = "Neg", HalfX = 10f, ParentId = -1, Follow = TerritoryFollow.PvP };
            Check(neg.Sanitize(100f) && neg.ParentId == 0 && neg.Follow == TerritoryFollow.None, "a negative parent (a request's 'automatic') is not stored");

            var unknown = new Territory { Id = 3, Name = "Unk", HalfX = 10f, ParentId = 1, FollowFlags = (TerritoryFlags)(1 << 30) | TerritoryFlags.BuildProtect, Follow = (TerritoryFollow)(1 << 20) | TerritoryFollow.Color };
            Check(unknown.Sanitize(100f) && unknown.FollowFlags == TerritoryFlags.BuildProtect && unknown.Follow == TerritoryFollow.Color, "marks this build does not know are dropped");

            // A barrier it sets itself, with the no-monster rule followed: the stored bits alone say 'barrier without no-monsters'.
            var follows = new Territory { Id = 4, Name = "Follows", HalfX = 10f, ParentId = 1, FollowFlags = TerritoryFlags.NoMonsterSpawns, Flags = TerritoryFlags.RunicBarrier };
            Check(follows.Sanitize(100f) && follows.Flags == TerritoryFlags.RunicBarrier, "a barrier set on a territory that follows its no-monster rule is kept: it is judged once the rule is filled in");
            var alone = new Territory { Id = 5, Name = "Alone", HalfX = 10f, Flags = TerritoryFlags.RunicBarrier };
            Check(alone.Sanitize(100f) && alone.Flags == TerritoryFlags.None, "(and the same bits on a territory that follows nothing are cleared, as always)");
            var ownBoth = new Territory { Id = 6, Name = "Own", HalfX = 10f, ParentId = 1, FollowFlags = TerritoryFlags.Announce, Flags = TerritoryFlags.RunicBarrier };
            Check(ownBoth.Sanitize(100f) && ownBoth.Flags == TerritoryFlags.RunicBarrier, "a territory that follows any rule is never cleaned on its stored bits");
            var unknownBit = new Territory { Id = 7, Name = "Bits", HalfX = 10f, ParentId = 1, FollowFlags = TerritoryFlags.Announce, Flags = (TerritoryFlags)(1 << 29) | TerritoryFlags.NoRaids };
            Check(unknownBit.Sanitize(100f) && unknownBit.Flags == TerritoryFlags.NoRaids, "but an unknown bit is dropped from it all the same");

            Check(Territory.CleanFlags(TerritoryFlags.MapNameOnly) == TerritoryFlags.None, "(CleanFlags itself is as before: name-only needs the map rule)");
            Territory sub = Sub(8, 1, 0f, 0f, 20f);
            Check(sub.Follows(TerritoryFlags.BuildProtect) && sub.Follows(TerritoryFollow.Color), "Follows reads the marks on a sub-territory");
            sub.ParentId = 0;
            Check(!sub.Follows(TerritoryFlags.BuildProtect) && !sub.Follows(TerritoryFollow.Color) && !sub.IsSub, "and says no without a parent, whatever the marks hold");
        }

        private const string SubFileText = @"{ ""version"": 5, ""worldName"": ""W"", ""worldUid"": 1, ""nextId"": 6, ""territories"": [
  { ""id"": 1, ""name"": ""Svartland"", ""shape"": ""Rectangle"", ""x"": 0, ""z"": 0, ""halfX"": 20000, ""halfZ"": 9000,
    ""pvp"": ""ForcedOn"", ""color"": ""#C8463C"", ""priority"": 5,
    ""ownerId"": 111, ""ownerName"": ""Owner"", ""members"": [ { ""id"": 222, ""name"": ""Kin"" } ],
    ""healthRegen"": 1.5, ""carryBonus"": 100, ""enterMessage"": ""Welcome to {name}"", ""leaveMessage"": ""Bye"",
    ""flags"": [ ""BuildProtect"", ""AccessProtect"", ""NoMonsterSpawns"", ""RunicBarrier"", ""Announce"", ""ShowOnMap"", ""MapNameOnly"", ""ShowBorder"" ] },
  { ""id"": 2, ""name"": ""Hrafnborg"", ""parent"": 1, ""shape"": ""Circle"", ""x"": 100, ""z"": 100, ""halfX"": 80 },
  { ""id"": 3, ""name"": ""Ulfheim"", ""parent"": ""Svartland"", ""shape"": ""Circle"", ""x"": 500, ""z"": 100, ""halfX"": 60,
    ""pvp"": ""ForcedOff"", ""priority"": 3, ""flags"": { ""MapNameOnly"": false, ""BuildProtect"": false } },
  { ""id"": 4, ""name"": ""Bank"", ""parent"": 2, ""shape"": ""Circle"", ""x"": 110, ""z"": 100, ""halfX"": 10,
    ""flags"": [ ""BuildProtect"", ""AccessProtect"" ], ""members"": [] },
  { ""id"": 5, ""name"": ""Free"", ""shape"": ""Circle"", ""x"": 9000, ""z"": 0, ""halfX"": 40 }
] }";

        private static void SubFile()
        {
            TerritoryJson.FileData file = TerritoryJson.Read(SubFileText);
            Check(file.Version == 5 && !file.FromNewerBuild && file.Territories.Count == 5, "a version 5 file with a tree in it reads");
            Territory k = file.Territories[0], hraf = file.Territories[1], ulf = file.Territories[2], bank = file.Territories[3], free = file.Territories[4];

            Check(k.ParentId == 0 && k.Follow == TerritoryFollow.None && k.FollowFlags == TerritoryFlags.None && k.PvP == PvPRule.ForcedOn && k.OwnerId == 111L, "no 'parent': a top-level territory, read as ever");
            Check(hraf.ParentId == 1 && hraf.Follow == TerritoryFollow.All && hraf.FollowFlags == Territory.KnownFlags, "'parent': 1 with nothing else: it follows everything");
            Check(hraf.Flags == TerritoryFlags.None && hraf.PvP == PvPRule.Unchanged && hraf.OwnerId == 0L && hraf.Members.Count == 0 && hraf.Color.r == Territory.DefaultColor.r, "and its stored record holds the defaults");
            Check(ulf.ParentId == 1, "'parent': \"Svartland\" names the territory in the file, whatever its place in it");
            Check((ulf.Follow & (TerritoryFollow.PvP | TerritoryFollow.Priority)) == TerritoryFollow.None && ulf.PvP == PvPRule.ForcedOff && ulf.Priority == 3 &&
                  (ulf.Follow & TerritoryFollow.People) != 0 && (ulf.Follow & TerritoryFollow.Color) != 0, "a key that is there sets that setting; the others follow");
            Check(ulf.FollowFlags == (Territory.KnownFlags & ~(TerritoryFlags.MapNameOnly | TerritoryFlags.BuildProtect)) && ulf.Flags == TerritoryFlags.None,
                "'flags' as an object sets the rules it names, true or false, and follows the rest");
            Check(bank.ParentId == 2 && bank.FollowFlags == TerritoryFlags.None && bank.Flags == (TerritoryFlags.BuildProtect | TerritoryFlags.AccessProtect), "'flags' as a list on a sub-territory sets ALL its rules");
            Check((bank.Follow & TerritoryFollow.People) == 0 && bank.OwnerId == 0L && bank.Members.Count == 0, "an empty 'members' is its own, empty, people - not the parent's");
            Check((bank.Follow & TerritoryFollow.PvP) != 0 && (bank.Follow & TerritoryFollow.SanctuaryValues) != 0 && (bank.Follow & TerritoryFollow.EnterText) != 0, "and the rest still follow");
            Check(free.ParentId == 0 && free.Flags == Territory.DefaultFlags && free.Follow == TerritoryFollow.None, "a top-level territory with no 'flags' has the defaults, as ever");

            Check(Inheritance.Validate(file.Territories) == null, "the tree is sound");
            List<Territory> eff = Inheritance.Resolve(file.Territories);
            Territory eh = eff[1], eu = eff[2], eb = eff[3];
            Check(eh.Flags == k.Flags && eh.PvP == PvPRule.ForcedOn && eh.Priority == 5 && eh.OwnerId == 111L && eh.Members.Count == 1 && eh.Color.g == 70 && eh.HealthRegen == 1.5f && eh.EnterMessage == "Welcome to {name}",
                "Hrafnborg means exactly what Svartland does");
            Check(eu.PvP == PvPRule.ForcedOff && eu.Priority == 3 && (eu.Flags & TerritoryFlags.BuildProtect) == 0 && (eu.Flags & TerritoryFlags.MapNameOnly) == 0 &&
                  (eu.Flags & TerritoryFlags.AccessProtect) != 0 && (eu.Flags & TerritoryFlags.RunicBarrier) != 0 && eu.OwnerId == 111L, "Ulfheim differs in the four things it says, and follows the rest");
            Check(eb.Flags == (TerritoryFlags.BuildProtect | TerritoryFlags.AccessProtect) && eb.PvP == PvPRule.ForcedOn && eb.OwnerId == 0L && eb.Members.Count == 0 && eb.Color.g == 70,
                "the Bank follows Hrafnborg (which follows Svartland) for what it does not set");

            // Written: only what each sets, and 'parent' as the id.
            string json = TerritoryJson.Write(file);
            JArray list = (JArray)JObject.Parse(json)["territories"];
            JObject o1 = (JObject)list[0], o2 = (JObject)list[1], o3 = (JObject)list[2], o4 = (JObject)list[3], o5 = (JObject)list[4];
            Check(o1["parent"] == null && o1["flags"] is JArray && o1["pvp"] != null && o1["color"] != null && o1["ownerId"] != null && o1["healthRegen"] != null, "a top-level territory is written whole, with no 'parent'");
            Check(o5["parent"] == null && o5["flags"] is JArray, "(and so is the other)");
            string[] keys2 = o2.Properties().Select(p => p.Name).ToArray();
            Check((int)o2["parent"] == 1 && !keys2.Contains("flags") && !keys2.Contains("pvp") && !keys2.Contains("color") && !keys2.Contains("ownerId") && !keys2.Contains("members") &&
                  !keys2.Contains("priority") && !keys2.Contains("healthRegen") && !keys2.Contains("enterMessage") && !keys2.Contains("leaveMessage"),
                $"a sub-territory that follows everything is written as its ground and nothing else [{string.Join(",", keys2)}]");
            Check((int)o3["parent"] == 1 && o3["flags"] is JObject && ((JObject)o3["flags"]).Count == 2 && !(bool)o3["flags"]["BuildProtect"] && !(bool)o3["flags"]["MapNameOnly"] &&
                  (string)o3["pvp"] == "ForcedOff" && (int)o3["priority"] == 3 && o3["color"] == null && o3["ownerId"] == null,
                "one that sets two rules, its PvP and priority is written with exactly those, the rules as true/false");
            Check((int)o4["parent"] == 2 && o4["flags"] is JArray && ((JArray)o4["flags"]).Count == 2 && o4["members"] is JArray && ((JArray)o4["members"]).Count == 0 && o4["ownerId"] != null &&
                  o4["pvp"] == null && o4["color"] == null,
                "one that sets every rule writes the list, and its own (empty) people");
            TerritoryJson.FileData again = TerritoryJson.Read(json);
            Check(TerritoryJson.Write(again) == json, "write -> read -> write is stable");
            for (int i = 0; i < file.Territories.Count; i++)
            {
                Territory a = file.Territories[i], b = again.Territories[i];
                SameTerritory(a, b, "file round trip of #" + a.Id);
                Check(a.ParentId == b.ParentId && a.FollowFlags == b.FollowFlags && a.Follow == b.Follow, "marks of #" + a.Id);
            }

            // A file of top-level territories is written exactly as before: no new key anywhere.
            var plain = new TerritoryJson.FileData { WorldName = "P", NextId = 3 };
            plain.Territories.Add(Circle(1, 0f, 0f, 10f, TerritoryFlags.BuildProtect));
            plain.Territories.Add(Circle(2, 50f, 0f, 10f, TerritoryFlags.NoRaids));
            string plainJson = TerritoryJson.Write(plain);
            Check(!plainJson.Contains("\"parent\""), "no 'parent' key for territories that have none");
            Check(TerritoryJson.Read(@"{ ""territories"": [ { ""id"": 1, ""parent"": 0 }, { ""id"": 2, ""parent"": """" }, { ""id"": 3, ""parent"": null } ] }").Territories.All(t => t.ParentId == 0 && t.Follow == TerritoryFollow.None),
                "'parent': 0, empty or null is none");

            // Sanctuary numbers are one group: any one key sets it, the rest of its numbers at their defaults.
            Territory partial = TerritoryJson.Read(@"{ ""territories"": [ { ""id"": 1 }, { ""id"": 2, ""parent"": 1, ""staminaRegen"": 2 } ] }").Territories[1];
            Check((partial.Follow & TerritoryFollow.SanctuaryValues) == 0 && partial.StaminaRegen == 2f && partial.HealthRegen == 1f && partial.CarryBonus == 0f, "one Sanctuary key makes the five its own");
            // The owner, ownerName and members are one group too.
            Territory owner = TerritoryJson.Read(@"{ ""territories"": [ { ""id"": 1 }, { ""id"": 2, ""parent"": 1, ""ownerId"": 9 } ] }").Territories[1];
            Check((owner.Follow & TerritoryFollow.People) == 0 && owner.OwnerId == 9L && owner.Members.Count == 0, "an owner alone makes the people its own");
            // A rule object on a territory with no parent has nothing to follow: what it leaves out is off.
            Territory objectTop = TerritoryJson.Read(@"{ ""territories"": [ { ""id"": 1, ""flags"": { ""BuildProtect"": true, ""NoRaids"": false } } ] }").Territories[0];
            Check(objectTop.Flags == TerritoryFlags.BuildProtect && objectTop.FollowFlags == TerritoryFlags.None, "a top-level territory may use the object form too");
            Territory lower = TerritoryJson.Read(@"{ ""territories"": [ { ""id"": 1 }, { ""id"": 2, ""parent"": 1, ""flags"": { ""buildprotect"": true, ""Nonsense"": true } } ] }").Territories[1];
            Check(lower.Flags == TerritoryFlags.BuildProtect && lower.FollowFlags == (Territory.KnownFlags & ~TerritoryFlags.BuildProtect), "names are read in any case, and an unknown one is skipped");

            // A list of rule names that 0.3.x loaded loads now, read the way it read it: a null is skipped, a number is the text of the
            // number (so it names the rule bits it sets), and a word that is no rule is skipped. (A file that loaded must still load.)
            List<Territory> odd = TerritoryJson.Read(@"{ ""territories"": [ { ""id"": 1, ""flags"": [ ""BuildProtect"", null ] }, { ""id"": 2, ""flags"": [ 1, 2 ] }, { ""id"": 3, ""flags"": [ true, 1.5, ""Nonsense"", """" ] } ] }").Territories;
            Check(odd[0].Flags == TerritoryFlags.BuildProtect, "a null in the list of rule names is skipped");
            Check(odd[1].Flags == (TerritoryFlags.BuildProtect | TerritoryFlags.AccessProtect), "a number in it sets the bits it names, as 0.3.x read it");
            Check(odd[2].Flags == TerritoryFlags.None, "and a yes/no, a fraction, an empty word and a word that is no rule set nothing");

            // The old files: a sub-territory is an addition, nothing about them changed.
            TerritoryJson.FileData old = TerritoryJson.Read(@"{ ""version"": 4, ""territories"": [ { ""id"": 1, ""flags"": [ ""ShowOnMap"", ""MapNameOnly"" ], ""pvp"": ""ForcedOn"" }, { ""id"": 2, ""name"": ""Town"", ""pvp"": ""ForcedOff"" } ] }");
            Check(old.Territories.All(t => t.ParentId == 0 && t.Follow == TerritoryFollow.None) && old.Territories[1].PvP == PvPRule.ForcedOff && old.Territories[1].Flags == Territory.DefaultFlags && !old.FromNewerBuild,
                "a version 4 file reads as it always did, every territory top level");
        }

        private static void SubFileErrors()
        {
            ExpectError(@"{ ""territories"": [ { ""id"": 1, ""parent"": true } ] }", "territories[0].parent", "a parent that is a yes/no");
            ExpectError(@"{ ""territories"": [ { ""id"": 1, ""parent"": { ""id"": 2 } } ] }", "territories[0].parent", "a parent that is an object");
            ExpectError(@"{ ""territories"": [ { ""id"": 1, ""parent"": -3 } ] }", "territories[0].parent", "a negative parent");
            ExpectError(@"{ ""territories"": [ { ""id"": 1, ""parent"": 1.5 } ] }", "whole number", "a parent that is not a whole number");
            ExpectError(@"{ ""territories"": [ { ""id"": 1, ""name"": ""A"" }, { ""id"": 2, ""parent"": ""Nowhere"" } ] }", "is not the name of any territory", "a parent named but not in the file");
            ExpectError(@"{ ""territories"": [ { ""id"": 1, ""name"": ""Dup"" }, { ""id"": 2, ""name"": ""dup"" }, { ""id"": 3, ""parent"": ""DUP"" } ] }", "is the name of 2 territories", "a parent name two territories share");
            ExpectError(@"{ ""territories"": [ { ""id"": 1, ""flags"": { ""BuildProtect"": ""yes"" } } ] }", "territories[0].flags", "a rule that is not true or false");
            ExpectError(@"{ ""territories"": [ { ""id"": 1, ""flags"": [ [ ""BuildProtect"" ] ] } ] }", "territories[0].flags", "a list inside the list of rule names");
            ExpectError(@"{ ""territories"": [ { ""id"": 1, ""flags"": [ { ""BuildProtect"": true } ] } ] }", "territories[0].flags", "an object inside the list of rule names");
            ExpectError(@"{ ""territories"": [ { ""id"": 1, ""flags"": 5 } ] }", "territories[0].flags", "flags that are a bare number");

            // The reader takes the links as written; the store's tree check is what refuses a loop, a missing parent and a long chain.
            TerritoryJson.FileData loop = TerritoryJson.Read(@"{ ""territories"": [ { ""id"": 1, ""parent"": 2 }, { ""id"": 2, ""parent"": 1 } ] }");
            string loopText = Inheritance.Validate(loop.Territories);
            Check(loopText != null && loopText.Contains("inside itself"), $"a file whose territories are inside each other is refused by the tree check ({loopText})");
            TerritoryJson.FileData orphan = TerritoryJson.Read(@"{ ""territories"": [ { ""id"": 1, ""parent"": 8 } ] }");
            Check(Inheritance.Validate(orphan.Territories) != null, "and one with a parent id that is not in it");
            var deepText = new System.Text.StringBuilder(@"{ ""territories"": [ { ""id"": 1 }");
            for (int i = 2; i <= 10; i++) deepText.Append($", {{ \"id\": {i}, \"parent\": {i - 1} }}");
            deepText.Append(" ] }");
            Check(Inheritance.Validate(TerritoryJson.Read(deepText.ToString()).Territories) != null, "and a chain of ten");

            // The server's own check at startup is the same text the checker prints: a file the store refuses is named by territory.
            Territory duplicate = Circle(1, 0f, 0f, 5f, TerritoryFlags.None);
            Check(Inheritance.Validate(new[] { duplicate, Sub(2, 1, 0f, 0f, 3f) }) == null, "(control: one parent, one child)");
        }

        private static void SubBanner()
        {
            // The continent ends at x = 1000. The town reaches from 980 out to sea at 1100: its middle (1040) is OUTSIDE the continent.
            Territory land = BannerBox(1, -1000f, 1000f, -1000f, 1000f);
            Territory coast = BannerBox(2, 980f, 1100f, -20f, 20f);

            string Round(bool linked)
            {
                coast.ParentId = linked ? 1 : 0;
                TerritoryIndex.Set(new[] { land, coast });
                var w = new BannerWalk();
                w.At(-1500f, 0f);
                w.Walk(-1500f, 0f, -900f, 0f);
                w.Walk(-900f, 0f, 1050f, 0f);
                w.Stay(1050f, 0f, 200f);                 // long enough for any cooldown
                w.Walk(1050f, 0f, 500f, 0f);
                return w.Log;
            }

            string unlinked = Round(false), linked = Round(true);
            Check(unlinked == "enter 1 +sound, enter 2 +sound, enter 1", $"(control) with no link the continent is announced again on the way out of the town (a straight crossing says no goodbye): its middle is not on it [{unlinked}]");
            Check(linked == "enter 1 +sound, enter 2 +sound, leave 2", $"linked, the town's goodbye alone: it never left the continent [{linked}]");

            // The other way, onto the continent from the town: a silent sub-territory is no leaving of its parent either.
            coast.Flags = TerritoryFlags.None;
            coast.ParentId = 1;
            TerritoryIndex.Set(new[] { land, coast });
            var s = new BannerWalk();
            s.At(-1500f, 0f);
            s.Walk(-1500f, 0f, -900f, 0f);
            s.Walk(-900f, 0f, 1050f, 0f);
            Check(s.Effective == 2 && s.Log == "enter 1 +sound", $"onto a silent sub-territory whose middle is off the continent: no goodbye to the continent [{s.Log}]");
            Check(TerritoryIndex.IsInside(2, 1) && !TerritoryIndex.IsInside(1, 2) && !TerritoryIndex.IsInside(2, 2) && !TerritoryIndex.IsInside(2, 0),
                "the index answers 'is it inside' by the link");
            Check(TerritoryIndex.Find(2) == coast && TerritoryIndex.Find(1) == land && TerritoryIndex.Find(99) == null, "and finds a territory by its id");
            coast.ParentId = 0;
            TerritoryIndex.Set(new Territory[0]);
        }

        private static void SubLink()
        {
            // A 0.3.x continent with towns that restate its rules, a region with a town inside it, a town that differs, and one that stands alone.
            string flags = @"[ ""BuildProtect"", ""AccessProtect"", ""NoMonsterSpawns"", ""Announce"", ""ShowOnMap"" ]";
            string text = @"{ ""version"": 4, ""worldName"": ""W"", ""worldUid"": 7, ""nextId"": 7, ""territories"": [
  { ""id"": 1, ""name"": ""Land"", ""shape"": ""Rectangle"", ""x"": 0, ""z"": 0, ""halfX"": 9000, ""halfZ"": 9000, ""color"": ""#C8463C"", ""pvp"": ""ForcedOn"", ""priority"": 2,
    ""ownerId"": 111, ""ownerName"": ""Owner"", ""members"": [ { ""id"": 222, ""name"": ""Kin"" } ], ""healthRegen"": 1.5, ""enterMessage"": ""Welcome to {name}"", ""flags"": " + flags + @" },
  { ""id"": 2, ""name"": ""Same"", ""shape"": ""Circle"", ""x"": 100, ""z"": 100, ""halfX"": 50, ""color"": ""#C8463C"", ""pvp"": ""ForcedOn"", ""priority"": 2,
    ""ownerId"": 111, ""ownerName"": ""Owner"", ""members"": [ { ""id"": 222, ""name"": ""Kin"" } ], ""healthRegen"": 1.5, ""enterMessage"": ""Welcome to {name}"", ""flags"": " + flags + @" },
  { ""id"": 3, ""name"": ""Differs"", ""shape"": ""Circle"", ""x"": 2000, ""z"": 100, ""halfX"": 60, ""color"": ""#3FA9FF"", ""pvp"": ""ForcedOff"", ""priority"": 2,
    ""ownerId"": 111, ""ownerName"": ""Owner"", ""members"": [ { ""id"": 222, ""name"": ""Kin"" } ], ""healthRegen"": 1.5, ""enterMessage"": ""Welcome to {name}"",
    ""flags"": [ ""AccessProtect"", ""NoMonsterSpawns"", ""Announce"", ""ShowOnMap"", ""NoRaids"" ] },
  { ""id"": 4, ""name"": ""Region"", ""shape"": ""Rectangle"", ""x"": 4000, ""z"": 4000, ""halfX"": 2000, ""halfZ"": 2000, ""color"": ""#C8463C"", ""pvp"": ""ForcedOn"", ""priority"": 2,
    ""ownerId"": 111, ""ownerName"": ""Owner"", ""members"": [ { ""id"": 222, ""name"": ""Kin"" } ], ""healthRegen"": 1.5, ""enterMessage"": ""Welcome to {name}"", ""flags"": " + flags + @" },
  { ""id"": 5, ""name"": ""RegionTown"", ""shape"": ""Circle"", ""x"": 4100, ""z"": 4100, ""halfX"": 80, ""color"": ""#C8463C"", ""pvp"": ""ForcedOn"", ""priority"": 2,
    ""ownerId"": 333, ""ownerName"": ""Mayor"", ""members"": [], ""healthRegen"": 1.5, ""enterMessage"": ""Welcome to {name}"", ""flags"": " + flags + @" },
  { ""id"": 6, ""name"": ""Alone"", ""shape"": ""Circle"", ""x"": 50000, ""z"": 0, ""halfX"": 40, ""flags"": [ ""BuildProtect"" ] }
] }";
            string linked = WorldFileCheck.Link(text, out string report);
            Check(linked != null, $"it links ({report})");
            if (linked == null) return;
            Check(report.StartsWith("linked 4 of 6 territories"), $"four of the six are inside another [{report}]");

            // Nothing any territory DOES changed.
            var before = Inheritance.Resolve(TerritoryJson.Read(text).Territories.Select(t => { t.Sanitize(100000f); return t; }).ToList());
            TerritoryJson.FileData after = TerritoryJson.Read(linked);
            var afterEff = Inheritance.Resolve(after.Territories);
            for (int i = 0; i < before.Count; i++)
            {
                Territory a = before[i], b = afterEff.First(t => t.Id == a.Id);
                Check(a.Flags == b.Flags && a.PvP == b.PvP && a.Priority == b.Priority && a.OwnerId == b.OwnerId && a.Members.Count == b.Members.Count && a.HealthRegen == b.HealthRegen &&
                      a.EnterMessage == b.EnterMessage && a.Color.g == b.Color.g, $"'{a.Name}' does what it did");
            }

            // Who is inside whom: the SMALLEST territory around each middle.
            Territory same = after.Territories.First(t => t.Id == 2), differs = after.Territories.First(t => t.Id == 3), region = after.Territories.First(t => t.Id == 4),
                      regionTown = after.Territories.First(t => t.Id == 5), alone = after.Territories.First(t => t.Id == 6);
            Check(same.ParentId == 1 && differs.ParentId == 1 && region.ParentId == 1, "the three towns in the continent are its sub-territories");
            Check(regionTown.ParentId == 4, "the town in the region is the REGION's, not the continent's");
            Check(alone.ParentId == 0 && after.Territories.First(t => t.Id == 1).ParentId == 0, "the continent and the territory on its own are not linked to anything");
            Check(same.Follow == TerritoryFollow.All && same.FollowFlags == Territory.KnownFlags, "'Same' restated everything, so it now follows everything");
            Check((differs.Follow & TerritoryFollow.PvP) == 0 && (differs.Follow & TerritoryFollow.Color) == 0 && (differs.Follow & TerritoryFollow.People) != 0 &&
                  !differs.Follows(TerritoryFlags.BuildProtect) && !differs.Follows(TerritoryFlags.NoRaids) && differs.Follows(TerritoryFlags.AccessProtect),
                "'Differs' keeps its own PvP, colour and the two rules it changed, and follows the rest");
            Check((regionTown.Follow & TerritoryFollow.People) == 0 && regionTown.OwnerId == 333L && (regionTown.Follow & TerritoryFollow.PvP) != 0, "a town with a mayor of its own keeps its people and follows the rest");

            // The written file says so, and a second pass changes nothing.
            JArray list = (JArray)JObject.Parse(linked)["territories"];
            JObject o2 = (JObject)list[1], o3 = (JObject)list[2];
            Check((int)o2["parent"] == 1 && o2["pvp"] == null && o2["flags"] == null && o2["color"] == null && o2["enterMessage"] == null, "'Same' is written as its ground and its parent");
            Check(o3["flags"] is JObject changed && changed.Count == 2 && (string)o3["pvp"] == "ForcedOff" && o3["color"] != null && o3["ownerId"] == null, "'Differs' is written with what it sets");
            Check((int)JObject.Parse(linked)["version"] == 5 && (long)JObject.Parse(linked)["worldUid"] == 7, "version 5, the header kept");
            string again = WorldFileCheck.Link(linked, out string second);
            Check(again == linked && second.StartsWith("linked 0 of"), $"linking the result again changes nothing [{second}]");

            // The checker is happy with it, and says nothing about unlinked towns any more.
            WorldFileCheck.Report ok = WorldFileCheck.Run(linked);
            Check(ok.Ok && !ok.Has("not-linked") && !ok.Has("pvp-lift") && !ok.Has("spawn-lift"), $"the checker passes it [{string.Join(" | ", ok.Findings.Select(f => f.Text))}]");

            // A file the loader refuses cannot be linked.
            Check(WorldFileCheck.Link(@"{ ""territories"": [ { ""id"": 1, ""parent"": 5 } ] }", out string refused) == null && refused.StartsWith("cannot link"), $"a file with a parent that is not there is refused [{refused}]");
        }

        private static void SubChecker()
        {
            // A linked file: no errors, no warnings, the real tree, the probe names the PvP.
            WorldFileCheck.Report ok = WorldFileCheck.Run(SubFileText, new[] { new Vector3(100f, 0f, 100f), new Vector3(9000f, 0f, 0f) }, tree: true);
            Check(ok.Ok && ok.Count(WorldFileCheck.Severity.Warn) == 0, $"the sub-territory file passes [{string.Join(" | ", ok.Findings.Select(f => f.Text))}]");
            Check(ok.Header[0].Contains("5 territories") && ok.Header[0].Contains("3 of them sub-territories"), "its header counts the sub-territories");
            string tree = string.Join("\n", ok.Nesting);
            Check(tree.Contains("by parent links") && tree.Contains("- 'Svartland' #1") && tree.Contains("    - 'Bank' #4"), $"the tree is the one the links make ({tree})");
            Check(tree.Contains("'Hrafnborg' #2") && tree.Contains("follows everything") && tree.Contains("'Ulfheim' #3") && tree.Contains("sets 4 itself"), "and says how much each sets itself");
            Check(ok.Probes[0].Contains("'Bank'") || ok.Probes[0].Contains("'Hrafnborg'"), "a probe inside the town names the sub-territory that governs it");
            Check(ok.Probes[1].Contains("'Svartland' (#1, pvp ForcedOn) governs") && ok.Probes[1].Contains("inside 'Free'"),
                $"and one on the unlinked 'Free', which Svartland's priority 5 outranks, names Svartland and the PvP it forces [{string.Join(" / ", ok.Probes)}]");

            // A loop, and a parent that is not in the file: the server refuses both.
            WorldFileCheck.Report loop = WorldFileCheck.Run(@"{ ""version"": 5, ""territories"": [ { ""id"": 1, ""parent"": 2 }, { ""id"": 2, ""parent"": 1 } ] }");
            Check(!loop.Ok && loop.Has("tree") && loop.Findings.First(f => f.Kind == "tree").Text.Contains("inside itself"), "a loop is an ERROR");
            WorldFileCheck.Report lost = WorldFileCheck.Run(@"{ ""version"": 5, ""territories"": [ { ""id"": 1 }, { ""id"": 2, ""parent"": 9 } ] }");
            Check(!lost.Ok && lost.Findings.First(f => f.Kind == "tree").Text.Contains("#9"), "so is a parent that is not in the file");
            WorldFileCheck.Report nameless = WorldFileCheck.Run(@"{ ""version"": 5, ""territories"": [ { ""id"": 1, ""name"": ""A"" }, { ""id"": 2, ""parent"": ""B"" } ] }");
            Check(!nameless.Ok && nameless.Has("refused") && nameless.Findings[0].Text.Contains("is not the name of any territory"), "and a parent named but not there");

            // Towns that lie inside a continent with no link are said to REPLACE its rules; the note says how to link them.
            string unlinked = @"{ ""version"": 4, ""territories"": [
  { ""id"": 1, ""name"": ""Land"", ""shape"": ""Rectangle"", ""x"": 0, ""z"": 0, ""halfX"": 5000, ""halfZ"": 5000, ""pvp"": ""ForcedOn"", ""flags"": [ ""NoMonsterSpawns"" ] },
  { ""id"": 2, ""name"": ""Town"", ""shape"": ""Circle"", ""x"": 100, ""z"": 100, ""halfX"": 50, ""flags"": [ ""BuildProtect"" ] } ] }";
            WorldFileCheck.Report plain = WorldFileCheck.Run(unlinked);
            Check(plain.Ok && plain.Has("not-linked") && plain.Findings.First(f => f.Kind == "not-linked").Text.Contains("\"parent\"") && plain.Has("pvp-lift") && plain.Has("spawn-lift"),
                "an unlinked town: the note offers the link, and the two 'replaces the rules' notes still say what it loses");
            WorldFileCheck.Report linkedNow = WorldFileCheck.Run(unlinked.Replace("\"name\": \"Town\",", "\"name\": \"Town\", \"parent\": 1,").Replace("\"version\": 4", "\"version\": 5"));
            Check(linkedNow.Ok && !linkedNow.Has("not-linked") && !linkedNow.Has("pvp-lift") && !linkedNow.Has("spawn-lift"), "with \"parent\" added, none of the three apply: it follows what it does not write");

            // Keys that write what the parent has FREEZE the value.
            string frozen = @"{ ""version"": 5, ""territories"": [
  { ""id"": 1, ""name"": ""Land"", ""shape"": ""Rectangle"", ""x"": 0, ""z"": 0, ""halfX"": 5000, ""halfZ"": 5000, ""pvp"": ""ForcedOn"", ""priority"": 4, ""flags"": [ ""NoMonsterSpawns"", ""Announce"" ] },
  { ""id"": 2, ""name"": ""Town"", ""parent"": 1, ""shape"": ""Circle"", ""x"": 100, ""z"": 100, ""halfX"": 50, ""pvp"": ""ForcedOn"", ""priority"": 4, ""flags"": [ ""NoMonsterSpawns"", ""Announce"" ] } ] }";
            WorldFileCheck.Report same = WorldFileCheck.Run(frozen);
            Check(same.Ok && same.Has("same-as-parent") && same.Findings.First(f => f.Kind == "same-as-parent").Text.Contains("follow the parent"), "a town that writes exactly the continent's PvP, priority and rules is told it freezes them");
            Check(!WorldFileCheck.Run(frozen.Replace("\"pvp\": \"ForcedOn\", \"priority\": 4, \"flags\": [ \"NoMonsterSpawns\", \"Announce\" ] } ] }", "\"flags\": { \"BuildProtect\": true } } ] }")).Has("same-as-parent"),
                "and one that writes only what differs is not");

            // A file that has links but says version 4 would be dropped to unlinked towns by a rolled-back 0.3.x.
            WorldFileCheck.Report versionNote = WorldFileCheck.Run(frozen.Replace("\"version\": 5", "\"version\": 4"));
            Check(versionNote.Ok && versionNote.Has("version"), "version 4 with a \"parent\" in it: a note to write 5");

            // A key near \"parent\" is suggested; a rule object with a wrong name is named.
            WorldFileCheck.Report typo = WorldFileCheck.Run(@"{ ""territories"": [ { ""id"": 1 }, { ""id"": 2, ""parents"": 1, ""flags"": { ""BuildProtct"": true } } ] }");
            Check(typo.Findings.Any(f => f.Kind == "unknown-key" && f.Text.Contains("did you mean \"parent\"")) && typo.Findings.Any(f => f.Kind == "unknown-flag" && f.Text.Contains("BuildProtct")), "\"parents\" and a mistyped rule in an object are named");
        }

        private static void SubPeople()
        {
            Territory k = Continent();
            k.Flags = TerritoryFlags.BuildProtect | TerritoryFlags.AccessProtect;
            Territory town = Sub(2, 1, 100f, 0f, 50f, "Town");
            var stored = new List<Territory> { k, town };
            TerritoryIndex.Set(Inheritance.Resolve(stored));
            var inTown = new Vector3(100f, 0f, 0f);

            Territory governing = TerritoryIndex.GoverningAt(inTown);
            Check(governing.Id == 2 && governing.IsMember(111L) && governing.IsMember(222L) && !governing.IsMember(333L), "the continent's owner and members are members of the town");
            Check(TerritoryIndex.Denying(inTown, 0f, TerritoryFlags.BuildProtect, 222L, false) == null, "a continent member builds in the town");
            Check(TerritoryIndex.Denying(inTown, 0f, TerritoryFlags.BuildProtect, 333L, false)?.Id == 2, "a stranger does not - and it is the TOWN that says no");

            // Bulk membership: add someone to the continent and they are a member of every town that follows its people.
            k.Members.Add(new TerritoryMember(333L, "New"));
            TerritoryIndex.Set(Inheritance.Resolve(stored));
            Check(TerritoryIndex.Denying(inTown, 0f, TerritoryFlags.BuildProtect, 333L, false) == null, "adding one person to the continent makes them a member of the town");

            // The town sets its own people: the continent's no longer count.
            town.Follow &= ~TerritoryFollow.People;
            town.OwnerId = 444L;
            town.OwnerName = "Mayor";
            TerritoryIndex.Set(Inheritance.Resolve(stored));
            Check(TerritoryIndex.Denying(inTown, 0f, TerritoryFlags.BuildProtect, 444L, false) == null, "the town's own owner builds there");
            Check(TerritoryIndex.Denying(inTown, 0f, TerritoryFlags.BuildProtect, 222L, false)?.Id == 2, "and a member of the continent no longer does");
            Check(TerritoryIndex.Denying(new Vector3(-500f, 0f, 0f), 0f, TerritoryFlags.BuildProtect, 222L, false) == null, "(on the continent itself they still do)");
            TerritoryIndex.Set(new Territory[0]);
        }
    }
}
