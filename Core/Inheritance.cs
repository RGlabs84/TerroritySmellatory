using System;
using System.Collections.Generic;
using UnityEngine;

namespace TerroritySmellatory
{
    /// <summary>
    /// Sub-territories (0.4.0): a territory sitting inside another, starting as a copy of its settings and following them
    /// until one is changed on the sub-territory itself. Plain functions over <see cref="Territory"/> records with no game
    /// state, so the server runs exactly what the off-game harness checks.
    /// </summary>
    /// <remarks>
    /// THE MODEL (Territory.ParentId / FollowFlags / Follow). A sub-territory has a parent. For each of its rules (flags) and
    /// each group of its other settings (<see cref="TerritoryFollow"/>) it either FOLLOWS the parent - it means whatever the
    /// parent means, now and after the parent changes - or sets its own. A new sub-territory follows everything; changing a
    /// setting on it makes that one setting its own; the panel's "follow again" takes it back. A territory's name, ground,
    /// history and id are never followed. The parent may itself be a sub-territory (a continent, a region, a town), and a
    /// sub-territory follows what its parent EFFECTIVELY is, so a change at the top flows all the way down.
    ///
    /// STORED vs EFFECTIVE. The server stores only what a territory sets itself (the followed fields are left at the plain
    /// defaults, <see cref="Canonicalize"/>), and <see cref="Resolve"/> makes the effective records with the followed values
    /// filled in. Those are what the server sends and what TerritoryIndex holds, so every rule keeps reading
    /// Territory.Flags, .PvP, .IsMember(...) exactly as before and none of them knows sub-territories exist. A change to a
    /// parent reaches its sub-territories because the effective list is rebuilt from the stored one after every change.
    ///
    /// PRECEDENCE. A sub-territory governs its own ground over all of its ancestors, whatever the sizes and priorities say
    /// (<see cref="ChildrenFirst"/>); otherwise TerritoryIndex's rule (priority, then the smaller, then the older) is untouched.
    ///
    /// REMOVING A PARENT never changes how its sub-territories behave: each keeps, as its own, everything it was following
    /// (<see cref="ReleaseChildren"/>), and moves up to the removed territory's parent.
    /// </remarks>
    internal static class Inheritance
    {
        /// <summary>How many levels may sit below a top-level territory: continent, region, town, district...</summary>
        internal const int MaxDepth = 8;

        /// <summary>A guard for the resolver's recursion on a list nobody validated.</summary>
        private const int MaxResolveDepth = 64;

        // ─────────────────────────────────────────────────────────────
        // RESOLVING - stored records in, effective records out
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// The effective records for a list of stored ones, in the same order, each a fresh clone. A sub-territory's followed
        /// rules and settings are its parent's EFFECTIVE ones, and the rules are cleaned once merged
        /// (<see cref="Territory.CleanFlags"/>): a barrier it sets itself still needs a no-monsters rule, which may be one it
        /// follows. A parent that is missing, is the territory itself, or loops back leaves the territory standing on its own
        /// values with no parent; <see cref="Validate"/> refuses such a list before it is ever loaded, so this only keeps a
        /// hand-built list sane.
        /// </summary>
        internal static List<Territory> Resolve(IReadOnlyList<Territory> stored)
        {
            var byId = new Dictionary<int, Territory>(stored.Count);
            foreach (Territory t in stored) byId[t.Id] = t;

            var done = new Dictionary<int, Territory>(stored.Count);
            var path = new HashSet<int>();
            var result = new List<Territory>(stored.Count);
            foreach (Territory t in stored) result.Add(Effective(t, byId, done, path));
            return result;
        }

        private static Territory Effective(Territory t, Dictionary<int, Territory> byId, Dictionary<int, Territory> done, HashSet<int> path)
        {
            if (done.TryGetValue(t.Id, out Territory made)) return made;

            Territory e = t.Clone();
            if (t.ParentId > 0)
            {
                if (t.ParentId != t.Id && path.Count < MaxResolveDepth && byId.TryGetValue(t.ParentId, out Territory parent) && path.Add(t.Id))
                {
                    Territory above = Effective(parent, byId, done, path);
                    path.Remove(t.Id);
                    TakeFlags(e, above, e.FollowFlags);
                    TakeGroups(e, above, e.Follow);
                }
                else
                {
                    e.ParentId = 0;
                    e.FollowFlags = TerritoryFlags.None;
                    e.Follow = TerritoryFollow.None;
                    e.Flags = Territory.CleanFlags(e.Flags);
                }
            }
            done[t.Id] = e;
            return e;
        }

        /// <summary>The rules in <paramref name="rules"/> take <paramref name="above"/>'s value; the others stay as they are. Cleaned afterwards.</summary>
        private static void TakeFlags(Territory e, Territory above, TerritoryFlags rules)
        {
            e.Flags = Territory.CleanFlags((above.Flags & rules) | (e.Flags & ~rules));
        }

        private static void TakeGroups(Territory e, Territory above, TerritoryFollow groups)
        {
            if ((groups & TerritoryFollow.PvP) != 0) e.PvP = above.PvP;
            if ((groups & TerritoryFollow.Priority) != 0) e.Priority = above.Priority;
            if ((groups & TerritoryFollow.People) != 0)
            {
                e.OwnerId = above.OwnerId;
                e.OwnerName = above.OwnerName;
                e.Members = new List<TerritoryMember>(above.Members.Count);
                foreach (TerritoryMember m in above.Members) e.Members.Add(new TerritoryMember(m.Id, m.Name));
            }
            if ((groups & TerritoryFollow.SanctuaryValues) != 0)
            {
                e.HealthRegen = above.HealthRegen;
                e.StaminaRegen = above.StaminaRegen;
                e.EitrRegen = above.EitrRegen;
                e.CarryBonus = above.CarryBonus;
                e.ArmorBonus = above.ArmorBonus;
            }
            if ((groups & TerritoryFollow.EnterText) != 0) e.EnterMessage = above.EnterMessage;
            if ((groups & TerritoryFollow.LeaveText) != 0) e.LeaveMessage = above.LeaveMessage;
            if ((groups & TerritoryFollow.Color) != 0) e.Color = above.Color;
        }

        // ─────────────────────────────────────────────────────────────
        // STORED RECORDS
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Puts everything a stored sub-territory follows back to the plain defaults, so a record holds only what it sets and
        /// two that mean the same thing are the same. A top-level territory follows nothing, so nothing changes.
        /// </summary>
        internal static void Canonicalize(Territory stored)
        {
            if (stored.ParentId <= 0)
            {
                stored.FollowFlags = TerritoryFlags.None;
                stored.Follow = TerritoryFollow.None;
                return;
            }

            stored.Flags &= ~stored.FollowFlags;
            TerritoryFollow f = stored.Follow;
            if ((f & TerritoryFollow.PvP) != 0) stored.PvP = PvPRule.Unchanged;
            if ((f & TerritoryFollow.Priority) != 0) stored.Priority = 0;
            if ((f & TerritoryFollow.People) != 0)
            {
                stored.OwnerId = 0L;
                stored.OwnerName = "";
                stored.Members = new List<TerritoryMember>();
            }
            if ((f & TerritoryFollow.SanctuaryValues) != 0)
            {
                stored.HealthRegen = 1f;
                stored.StaminaRegen = 1f;
                stored.EitrRegen = 1f;
                stored.CarryBonus = 0f;
                stored.ArmorBonus = 0f;
            }
            if ((f & TerritoryFollow.EnterText) != 0) stored.EnterMessage = "";
            if ((f & TerritoryFollow.LeaveText) != 0) stored.LeaveMessage = "";
            if ((f & TerritoryFollow.Color) != 0) stored.Color = Territory.DefaultColor;
        }

        /// <summary>
        /// The territory keeps, as its own, everything it was following: its stored record takes the effective one's values and
        /// follows nothing. How a sub-territory outlives the removal of its parent unchanged; the caller sets its new parent.
        /// </summary>
        internal static void Bake(Territory stored, Territory effective)
        {
            stored.CopySettingsFrom(effective);
            stored.FollowFlags = TerritoryFlags.None;
            stored.Follow = TerritoryFollow.None;
        }

        /// <summary>
        /// A stored sub-territory stops following a group and keeps what it effectively had there as its own - the server does this
        /// before it adds to a list the territory had only by following (combining another territory's members into it).
        /// </summary>
        internal static void OwnFrom(Territory stored, Territory effective, TerritoryFollow groups)
        {
            TakeGroups(stored, effective, groups);
            stored.Follow &= ~groups;
        }

        /// <summary>
        /// Territory <paramref name="goneId"/> is about to leave (deleted, or combined into another). Each of its
        /// sub-territories gets a changed COPY that keeps what it does - everything it followed written into it as its own -
        /// and sits under <paramref name="newParentId"/> instead (0: top level). The caller sanitises the copies, replaces them
        /// in the store, and puts the stored ones back if the save fails.
        /// </summary>
        /// <param name="effective">The effective records as they are NOW, before the removal.</param>
        internal static List<Territory> ReleaseChildren(IReadOnlyList<Territory> stored, IReadOnlyList<Territory> effective, int goneId, int newParentId)
        {
            var eff = new Dictionary<int, Territory>(effective.Count);
            foreach (Territory e in effective) eff[e.Id] = e;

            var changed = new List<Territory>();
            foreach (Territory child in stored)
            {
                if (child.ParentId != goneId || child.Id == goneId) continue;
                Territory copy = child.Clone();
                if (eff.TryGetValue(child.Id, out Territory now)) Bake(copy, now);
                copy.ParentId = newParentId;
                copy.Revision = child.Revision + 1;
                changed.Add(copy);
            }
            return changed;
        }

        // ─────────────────────────────────────────────────────────────
        // THE PANEL'S TWO MOVES - on an effective copy, against its effective parent
        // ─────────────────────────────────────────────────────────────

        /// <summary>The rule follows the parent again, and shows the parent's value from now on (the panel's "follow").</summary>
        internal static void Pull(Territory child, Territory parent, TerritoryFlags rule)
        {
            if (child == null || parent == null) return;
            child.FollowFlags |= rule & Territory.KnownFlags;
            TakeFlags(child, parent, rule);
        }

        /// <summary>The group follows the parent again, and shows the parent's values from now on.</summary>
        internal static void Pull(Territory child, Territory parent, TerritoryFollow group)
        {
            if (child == null || parent == null) return;
            child.Follow |= group & TerritoryFollow.All;
            TakeGroups(child, parent, group);
        }

        /// <summary>Everything follows the parent: the sub-territory is a copy of it again, apart from its name and ground.</summary>
        internal static void PullAll(Territory child, Territory parent)
        {
            Pull(child, parent, Territory.KnownFlags);
            Pull(child, parent, TerritoryFollow.All);
        }

        /// <summary>
        /// Brings an effective copy up to date with its (effective) parent: the rules and settings it follows take the parent's values as
        /// they are now. The panel does this to the copy it is editing whenever the server's list changes, so a followed row always shows
        /// what the parent has.
        /// </summary>
        internal static void Refresh(Territory view, Territory parent)
        {
            if (view == null || parent == null || view.ParentId <= 0) return;
            TakeFlags(view, parent, view.FollowFlags);
            TakeGroups(view, parent, view.Follow);
        }

        /// <summary>
        /// A draft that was top level is now going to sit inside something: every setting still exactly as <paramref name="fresh"/> (the
        /// draft before the admin touched it) starts following, and every one the admin changed stays theirs.
        /// </summary>
        internal static void FollowWhatIsUnchanged(Territory draft, Territory fresh)
        {
            draft.FollowFlags = ~(draft.Flags ^ fresh.Flags) & Territory.KnownFlags;

            TerritoryFollow groups = TerritoryFollow.None;
            if (draft.PvP == fresh.PvP) groups |= TerritoryFollow.PvP;
            if (draft.Priority == fresh.Priority) groups |= TerritoryFollow.Priority;
            if (SamePeople(draft, fresh)) groups |= TerritoryFollow.People;
            if (draft.HealthRegen == fresh.HealthRegen && draft.StaminaRegen == fresh.StaminaRegen && draft.EitrRegen == fresh.EitrRegen &&
                draft.CarryBonus == fresh.CarryBonus && draft.ArmorBonus == fresh.ArmorBonus) groups |= TerritoryFollow.SanctuaryValues;
            if (draft.EnterMessage == fresh.EnterMessage) groups |= TerritoryFollow.EnterText;
            if (draft.LeaveMessage == fresh.LeaveMessage) groups |= TerritoryFollow.LeaveText;
            if (draft.Color.r == fresh.Color.r && draft.Color.g == fresh.Color.g && draft.Color.b == fresh.Color.b) groups |= TerritoryFollow.Color;
            draft.Follow = groups;
        }

        private static bool SamePeople(Territory a, Territory b)
        {
            if (a.OwnerId != b.OwnerId || a.Members.Count != b.Members.Count) return false;
            for (int i = 0; i < a.Members.Count; i++)
                if (a.Members[i].Id != b.Members[i].Id) return false;
            return true;
        }

        /// <summary>The rule is the sub-territory's own from now on, as it stands (the panel does this when a rule is switched).</summary>
        internal static void Own(Territory child, TerritoryFlags rule) => child.FollowFlags &= ~rule;

        /// <summary>The group is the sub-territory's own from now on, as it stands.</summary>
        internal static void Own(Territory child, TerritoryFollow group) => child.Follow &= ~group;

        // ─────────────────────────────────────────────────────────────
        // THE TREE
        // ─────────────────────────────────────────────────────────────

        private static string Label(Territory t) => $"'{t.DisplayName}' (#{t.Id})";

        /// <summary>
        /// Null when every parent link in the list is sound; otherwise the first thing wrong, in words: a parent that does not
        /// exist, a territory inside itself or inside its own sub-territory, or a chain more than <see cref="MaxDepth"/> levels
        /// deep. The store refuses a file that fails this - a link that goes nowhere changes what the territory does, so
        /// guessing would be the worse mistake (compare a duplicate id).
        /// </summary>
        internal static string Validate(IReadOnlyList<Territory> all)
        {
            var byId = new Dictionary<int, Territory>(all.Count);
            foreach (Territory t in all) byId[t.Id] = t;

            foreach (Territory t in all)
            {
                if (t.ParentId == 0) continue;
                if (t.ParentId < 0) return $"{Label(t)} has a parent of {t.ParentId}";
                if (t.ParentId == t.Id) return $"{Label(t)} is inside itself";
                if (!byId.ContainsKey(t.ParentId)) return $"{Label(t)} is inside territory #{t.ParentId}, which does not exist";
            }

            foreach (Territory t in all)
            {
                int steps = 0;
                var chain = new List<string>();
                for (Territory at = t; at.ParentId != 0; at = byId[at.ParentId])
                {
                    steps++;
                    chain.Add(Label(byId[at.ParentId]));
                    if (at.ParentId == t.Id) return $"{Label(t)} is inside itself through its parents ({Label(t)} > {string.Join(" > ", chain)})";
                    if (steps > all.Count) return $"{Label(t)} is inside a loop of territories";
                }
                if (steps > MaxDepth) return $"{Label(t)} is {steps} levels down; at most {MaxDepth} are allowed";
            }
            return null;
        }

        /// <summary>
        /// Null when territory <paramref name="childId"/> (it may not exist yet) can sit inside <paramref name="newParentId"/>
        /// (0 is always fine); otherwise why not. Checked on every Create and Update, because a loop made by one admin's edit
        /// would otherwise only be found by the next restart.
        /// </summary>
        internal static string CheckParent(IReadOnlyList<Territory> all, int childId, int newParentId)
        {
            if (newParentId == 0) return null;
            var byId = new Dictionary<int, Territory>(all.Count);
            foreach (Territory t in all) byId[t.Id] = t;

            if (!byId.TryGetValue(newParentId, out Territory parent)) return $"There is no territory #{newParentId} to put it inside.";
            if (newParentId == childId) return $"{Label(parent)} cannot be inside itself.";
            byId.TryGetValue(childId, out Territory child);

            int levels = 0;   // the parent and every territory above it
            for (Territory at = parent; at != null && levels <= all.Count; levels++)
            {
                if (at.Id == childId && childId != 0)
                    return $"{Label(parent)} is inside {Label(child ?? at)}; a territory cannot be put inside one of its own sub-territories.";
                byId.TryGetValue(at.ParentId, out at);
            }

            // levels is now the parent and everything above it, which is how many levels down the new sub-territory sits.
            int below = child == null ? 0 : Height(all, childId);
            if (levels + below > MaxDepth)
                return $"That would put {(child != null ? Label(child) : "it")} {levels + below} levels down; at most {MaxDepth} are allowed.";
            return null;
        }

        /// <summary>The direct sub-territories of <paramref name="id"/>, in list order.</summary>
        internal static List<Territory> ChildrenOf(IReadOnlyList<Territory> all, int id)
        {
            var kids = new List<Territory>();
            if (id <= 0) return kids;
            foreach (Territory t in all)
                if (t.ParentId == id && t.Id != id) kids.Add(t);
            return kids;
        }

        /// <summary>Every territory inside <paramref name="id"/>, at any depth, nearest first.</summary>
        internal static List<Territory> Descendants(IReadOnlyList<Territory> all, int id)
        {
            var found = new List<Territory>();
            if (id <= 0) return found;
            var seen = new HashSet<int> { id };
            var byParent = new Dictionary<int, List<Territory>>();
            foreach (Territory t in all)
            {
                if (t.ParentId <= 0) continue;
                if (!byParent.TryGetValue(t.ParentId, out List<Territory> list)) byParent[t.ParentId] = list = new List<Territory>();
                list.Add(t);
            }
            for (int i = -1; i < found.Count; i++)
            {
                int at = i < 0 ? id : found[i].Id;
                if (!byParent.TryGetValue(at, out List<Territory> kids)) continue;
                foreach (Territory k in kids)
                    if (seen.Add(k.Id)) found.Add(k);
            }
            return found;
        }

        /// <summary>How many levels of sub-territories lie below <paramref name="id"/>: 0 for none.</summary>
        internal static int Height(IReadOnlyList<Territory> all, int id)
        {
            if (id <= 0) return 0;
            var depth = new Dictionary<int, int> { { id, 0 } };
            int height = 0;
            foreach (Territory t in Descendants(all, id))
            {
                int d = depth.TryGetValue(t.ParentId, out int above) ? above + 1 : 1;
                depth[t.Id] = d;
                if (d > height) height = d;
            }
            return height;
        }

        /// <summary>The territories above <paramref name="id"/>, nearest first (its parent, then that one's parent...).</summary>
        internal static List<Territory> Ancestors(IReadOnlyList<Territory> all, int id)
        {
            var byId = new Dictionary<int, Territory>(all.Count);
            foreach (Territory t in all) byId[t.Id] = t;

            var above = new List<Territory>();
            if (!byId.TryGetValue(id, out Territory at)) return above;
            while (at.ParentId > 0 && above.Count <= all.Count && byId.TryGetValue(at.ParentId, out at)) above.Add(at);
            return above;
        }

        /// <summary>Is territory <paramref name="id"/> somewhere inside <paramref name="ancestorId"/>?</summary>
        internal static bool IsInside(IReadOnlyList<Territory> all, int id, int ancestorId)
        {
            if (ancestorId <= 0 || id == ancestorId) return false;
            foreach (Territory a in Ancestors(all, id))
                if (a.Id == ancestorId) return true;
            return false;
        }

        /// <summary>The short name of a rule, as lists and messages call it.</summary>
        internal static string RuleLabel(TerritoryFlags rule)
        {
            switch (rule)
            {
                case TerritoryFlags.BuildProtect: return "Building";
                case TerritoryFlags.AccessProtect: return "Doors, chests & stations";
                case TerritoryFlags.HarvestProtect: return "Harvesting";
                case TerritoryFlags.StructureProtect: return "Buildings cannot be damaged";
                case TerritoryFlags.NoMonsterSpawns: return "No monster spawns";
                case TerritoryFlags.NoRaids: return "No raids";
                case TerritoryFlags.NoSkillLoss: return "No skill loss";
                case TerritoryFlags.Sanctuary: return "Sanctuary effect";
                case TerritoryFlags.Announce: return "Entry / exit banner";
                case TerritoryFlags.ShowOnMap: return "Show on the map";
                case TerritoryFlags.ShowBorder: return "Border on the ground";
                case TerritoryFlags.RunicBarrier: return "Runic barrier";
                case TerritoryFlags.BarrierKeepsOutVisitors: return "Keep non-members out";
                case TerritoryFlags.AutoCloseDoors: return "Doors close by themselves";
                case TerritoryFlags.MapNameOnly: return "Name only on the map";
                case TerritoryFlags.Guards: return "Guards";
                default: return rule.ToString();
            }
        }

        /// <summary>The short name of a group of settings.</summary>
        internal static string GroupLabel(TerritoryFollow group)
        {
            switch (group)
            {
                case TerritoryFollow.PvP: return "PvP";
                case TerritoryFollow.Priority: return "Priority";
                case TerritoryFollow.People: return "Owner and members";
                case TerritoryFollow.SanctuaryValues: return "Sanctuary numbers";
                case TerritoryFollow.EnterText: return "Entry text";
                case TerritoryFollow.LeaveText: return "Exit text";
                case TerritoryFollow.Color: return "Colour";
                default: return group.ToString();
            }
        }

        /// <summary>What a sub-territory sets itself, by name, rules first: the rest it follows. Empty for a top-level one and for one that follows everything.</summary>
        internal static List<string> SetsItself(Territory t)
        {
            var names = new List<string>();
            if (t == null || t.ParentId <= 0) return names;
            foreach (TerritoryFlags rule in Enum.GetValues(typeof(TerritoryFlags)))
                if (rule != TerritoryFlags.None && !t.Follows(rule)) names.Add(RuleLabel(rule));
            foreach (TerritoryFollow group in new[] { TerritoryFollow.PvP, TerritoryFollow.Priority, TerritoryFollow.People, TerritoryFollow.SanctuaryValues,
                                                      TerritoryFollow.EnterText, TerritoryFollow.LeaveText, TerritoryFollow.Color })
                if (!t.Follows(group)) names.Add(GroupLabel(group));
            return names;
        }

        /// <summary>One line of the tree, as the panel's list shows it.</summary>
        internal struct TreeRow
        {
            public Territory Territory;

            /// <summary>0 for a top-level territory, 1 for one inside it...</summary>
            public int Depth;
        }

        /// <summary>
        /// The territories as a tree, each followed by the ones inside it (siblings in the order <paramref name="siblings"/>
        /// gives, or by id). One whose parent is not in the list - or that sits in a loop - is shown at the top level, so
        /// nothing is ever left out.
        /// </summary>
        internal static List<TreeRow> TreeOrder(IReadOnlyList<Territory> all, Comparison<Territory> siblings = null)
        {
            Comparison<Territory> order = siblings ?? ((a, b) => a.Id.CompareTo(b.Id));

            var ids = new HashSet<int>();
            foreach (Territory t in all) ids.Add(t.Id);

            var under = new Dictionary<int, List<Territory>>();   // key 0 holds the top level
            foreach (Territory t in all)
            {
                int p = t.ParentId > 0 && t.ParentId != t.Id && ids.Contains(t.ParentId) ? t.ParentId : 0;
                if (!under.TryGetValue(p, out List<Territory> list)) under[p] = list = new List<Territory>();
                list.Add(t);
            }
            foreach (List<Territory> list in under.Values) list.Sort(order);

            var rows = new List<TreeRow>(all.Count);
            var seen = new HashSet<int>();

            void Walk(Territory t, int depth)
            {
                if (!seen.Add(t.Id)) return;
                rows.Add(new TreeRow { Territory = t, Depth = depth });
                if (under.TryGetValue(t.Id, out List<Territory> kids))
                    foreach (Territory k in kids) Walk(k, depth + 1);
            }

            if (under.TryGetValue(0, out List<Territory> top))
                foreach (Territory t in top) Walk(t, 0);

            // Only a loop is left (nothing above it reaches it): shown at the top level rather than dropped.
            var rest = new List<Territory>();
            foreach (Territory t in all)
                if (!seen.Contains(t.Id)) rest.Add(t);
            rest.Sort(order);
            foreach (Territory t in rest) Walk(t, 0);
            return rows;
        }

        // ─────────────────────────────────────────────────────────────
        // PRECEDENCE
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// A list already in precedence order, with every sub-territory moved up to just before the first of its ancestors and nothing
        /// else moved: a sub-territory governs its own ground over the territories it sits inside, whatever their sizes and
        /// priorities say. (Without it a town bigger than what is left of its continent, or with a lower priority, would lose its
        /// own streets.) One the usual order puts below an ancestor takes the place just ahead of the first of them, so against
        /// everything outside its tree it ranks where its top territory does; one that the usual order already puts above all its
        /// ancestors (a priority of its own that beats theirs) stays where it was, and keeps outranking what its priority outranks.
        /// Territories with no parent link keep their places exactly.
        /// </summary>
        internal static List<Territory> ChildrenFirst(List<Territory> sorted)
        {
            int n = sorted.Count;
            bool any = false;
            for (int i = 0; i < n && !any; i++) any = sorted[i].ParentId > 0;
            if (!any) return sorted;

            var byId = new Dictionary<int, Territory>(n);
            foreach (Territory t in sorted) byId[t.Id] = t;

            var result = new List<Territory>(n);
            foreach (Territory x in sorted)
            {
                // Just before the earliest of its ancestors that has been placed already; at the end if none has.
                int at = result.Count;
                Territory above = x;
                for (int hops = 0; hops <= MaxDepth + 1 && above.ParentId > 0 && byId.TryGetValue(above.ParentId, out Territory parent) && !ReferenceEquals(parent, x); hops++)
                {
                    int place = result.IndexOf(parent);
                    if (place >= 0 && place < at) at = place;
                    above = parent;
                }
                result.Insert(at, x);
            }
            return result;
        }
    }
}
