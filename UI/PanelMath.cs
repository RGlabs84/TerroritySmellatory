using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace TerroritySmellatory
{
    /// <summary>
    /// The panel's arithmetic, apart from anything it draws: how far apart two territories are, how an edit
    /// in progress is carried onto a newer record from the server, and (0.4.0) what the panel says about where a
    /// territory sits and what it follows. Pure - no game state, no GUI - so it can be checked off-game like Core/.
    /// </summary>
    internal static class PanelMath
    {
        /// <summary>Hand-placed edges only roughly meet: within this of each other, two grounds "touch".</summary>
        internal const float TouchSlack = 1f;

        /// <summary>"overlaps", "touches" or "N m away" for a <see cref="Gap"/>.</summary>
        internal static string Where(float gap) =>
            gap < -TouchSlack ? "overlaps"
            : gap <= TouchSlack ? "touches"
            : gap.ToString("0", CultureInfo.InvariantCulture) + " m away";

        /// <summary>"20 x 60 m at (120, -340)": one part, as the Ground list and terr_list show it.</summary>
        internal static string PartSummary(TerritoryShape p) =>
            $"{p.Describe()} at ({p.CenterX.ToString("0", CultureInfo.InvariantCulture)}, {p.CenterZ.ToString("0", CultureInfo.InvariantCulture)})";

        // ─────────────────────────────────────────────────────────────
        // GAP - how far apart two grounds are
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// The clear distance between two grounds at their nearest parts, in metres; 0 or less where they meet or
        /// overlap (then, roughly, how deep). Exact for every pair of shapes the hammer marks.
        /// </summary>
        internal static float Gap(TerritoryGround a, TerritoryGround b)
        {
            float best = float.MaxValue;
            for (int i = 0; i < a.Count; i++)
                for (int j = 0; j < b.Count; j++)
                    best = Mathf.Min(best, PartGap(a[i], b[j]));
            return best;
        }

        internal static float PartGap(TerritoryShape a, TerritoryShape b)
        {
            if (a.IsEmpty || b.IsEmpty) return float.MaxValue;
            // A circle is a point grown by its radius, so its gap to anything is the centre's, less the radius.
            if (a.Kind == TerritoryShapeKind.Circle) return Signed(b, a.CenterX, a.CenterZ) - a.HalfX;
            if (b.Kind == TerritoryShapeKind.Circle) return Signed(a, b.CenterX, b.CenterZ) - b.HalfX;
            return BoxGap(a, b);
        }

        /// <summary>From a point to a part's edge: positive outside, negative inside (how deep).</summary>
        private static float Signed(TerritoryShape s, float x, float z)
        {
            float dx = x - s.CenterX, dz = z - s.CenterZ;
            if (s.Kind == TerritoryShapeKind.Circle) return Mathf.Sqrt(dx * dx + dz * dz) - s.HalfX;

            // Into the box's own frame through the same rotation TerritoryShape.Contains uses.
            Vector3 l = Quaternion.Euler(0f, -s.Yaw, 0f) * new Vector3(dx, 0f, dz);
            float ox = Mathf.Abs(l.x) - s.HalfX, oz = Mathf.Abs(l.z) - s.HalfZ;
            if (ox <= 0f && oz <= 0f) return Mathf.Max(ox, oz);
            float cx = Mathf.Max(ox, 0f), cz = Mathf.Max(oz, 0f);
            return Mathf.Sqrt(cx * cx + cz * cz);
        }

        /// <summary>
        /// Two boxes, turned any way. They are apart exactly when one of their four edge directions separates
        /// them (separating axes); and when apart, the nearest two points of two convex outlines include a
        /// corner of one of them, so the distance is the nearest corner's.
        /// </summary>
        private static float BoxGap(TerritoryShape a, TerritoryShape b)
        {
            float apart = Mathf.Max(Mathf.Max(AxisGap(a, b, a.Yaw), AxisGap(a, b, a.Yaw + 90f)),
                Mathf.Max(AxisGap(a, b, b.Yaw), AxisGap(a, b, b.Yaw + 90f)));
            if (apart <= 0f) return apart;

            float best = float.MaxValue;
            for (int k = 0; k < 4; k++)
                best = Mathf.Min(best, Mathf.Min(b.DistanceOutside(a.Corner(k)), a.DistanceOutside(b.Corner(k))));
            return best;
        }

        /// <summary>The gap between the two boxes' shadows on the line at <paramref name="yaw"/>; negative where the shadows overlap.</summary>
        private static float AxisGap(TerritoryShape a, TerritoryShape b, float yaw)
        {
            Vector3 n = Quaternion.Euler(0f, yaw, 0f) * new Vector3(1f, 0f, 0f);
            float centres = Mathf.Abs((b.CenterX - a.CenterX) * n.x + (b.CenterZ - a.CenterZ) * n.z);
            return centres - Reach(a, n) - Reach(b, n);
        }

        /// <summary>Half the length of a box's shadow on the direction <paramref name="n"/>.</summary>
        private static float Reach(TerritoryShape s, Vector3 n)
        {
            Quaternion q = Quaternion.Euler(0f, s.Yaw, 0f);
            Vector3 ux = q * new Vector3(1f, 0f, 0f);
            Vector3 uz = q * new Vector3(0f, 0f, 1f);
            return s.HalfX * Mathf.Abs(ux.x * n.x + ux.z * n.z) + s.HalfZ * Mathf.Abs(uz.x * n.x + uz.z * n.z);
        }

        // ─────────────────────────────────────────────────────────────
        // SUB-TERRITORIES (0.4.0) - where a territory sits, and what it follows
        // ─────────────────────────────────────────────────────────────

        /// <summary>The words in the follow marks column: "follows" is the parent's setting, "own" is this territory's.</summary>
        internal const string MarkFollows = "follows", MarkOwn = "own";

        /// <summary>The marks column is drawn: the territory has a parent, or (a draft) will be given one by the server.</summary>
        internal static bool HasMarks(Territory t) => t != null && t.ParentId != 0;

        // Territory.Follows() answers no for the automatic parent (it knows only ids above 0), and the panel has to read a
        // draft's marks while its parent is still to be picked.
        internal static bool FollowsRule(Territory t, TerritoryFlags rule) => HasMarks(t) && (t.FollowFlags & rule) != 0;

        internal static bool FollowsGroup(Territory t, TerritoryFollow group) => HasMarks(t) && (t.Follow & group) != 0;

        /// <summary>This rule follows a parent that is not known yet (the automatic one), so a row cannot say what its value is.</summary>
        internal static bool UnknownRule(Territory t, TerritoryFlags rule) => t != null && t.ParentId < 0 && FollowsRule(t, rule);

        /// <summary>This group follows a parent that is not known yet.</summary>
        internal static bool UnknownGroup(Territory t, TerritoryFollow group) => t != null && t.ParentId < 0 && FollowsGroup(t, group);

        /// <summary>Every rule and every group follows the parent: "follow all" has nothing left to do.</summary>
        internal static bool AllFollow(Territory t) =>
            HasMarks(t) && (t.FollowFlags & Territory.KnownFlags) == Territory.KnownFlags && (t.Follow & TerritoryFollow.All) == TerritoryFollow.All;

        /// <summary>The Inside row's text: where the territory sits. <paramref name="parent"/> is the parent's record, or null when it is not in the list.</summary>
        internal static string ParentLine(Territory t, Territory parent)
        {
            if (t.ParentId < 0) return "Automatic: inside whatever it is marked in";
            if (t.ParentId == 0) return "Nothing (top level)";
            return parent != null ? $"{parent.DisplayName} (#{parent.Id})" : $"#{t.ParentId} (no longer exists)";
        }

        /// <summary>"inside Svartland" / "at the top level": where the next placement will put the draft, to finish a sentence.</summary>
        internal static string PlacedWhere(Territory t, Territory parent)
        {
            if (t.ParentId < 0) return "inside whatever it is marked in";
            if (t.ParentId == 0) return "at the top level";
            return parent != null ? $"inside {parent.DisplayName}" : $"inside #{t.ParentId}";
        }

        /// <summary>The parent's name for a sentence, or a stand-in while it is automatic or gone.</summary>
        internal static string ParentName(Territory t, Territory parent) =>
            t.ParentId < 0 ? "the territory it is marked in" : parent != null ? parent.DisplayName : $"#{t.ParentId}";

        /// <summary>The caption over the Members tab: whose people these are.</summary>
        internal static string PeopleCaption(Territory t, Territory parent, bool follows) =>
            !follows ? "Its own owner and members"
            : t.ParentId < 0 ? "Following the people of the territory it is marked in"
            : $"Following {ParentName(t, parent)}'s people";

        /// <summary>The line under the Members tab's caption: what is on the page, and what the button does about it.</summary>
        internal static string PeopleHint(Territory t, Territory parent, bool follows)
        {
            if (follows)
                return t.ParentId < 0
                    ? "Its owner and members are those of the territory it is marked in. Setting its own list stops that."
                    : $"The owner and members below are {ParentName(t, parent)}'s, now and whenever they change. Setting its own list copies them here and stops following.";
            return t.ParentId < 0
                ? "These are this territory's own. Following drops them: the territory it is marked in supplies the people."
                : $"These are this territory's own. Following {ParentName(t, parent)} drops them and takes {ParentName(t, parent)}'s people instead.";
        }

        /// <summary>The line under "Follow all": what the marks column says, for the parent the territory has (or will be given).</summary>
        internal static string MarksHint(Territory t, Territory parent) =>
            t.ParentId < 0
                ? "A setting marked 'follows' takes the value of the territory it is marked in. Change one and it becomes 'own'; click 'own' to follow again."
                : $"A setting marked 'follows' is {ParentName(t, parent)}'s, now and when it changes. Change one and it becomes 'own'; click 'own' to follow again.";

        /// <summary>"Inside: Hrafnborg > Svartland": what a territory sits in, nearest first (Inheritance.Ancestors); empty for a top-level one.</summary>
        internal static string InsideChain(IReadOnlyList<Territory> all, Territory t)
        {
            if (t == null || t.ParentId <= 0) return "";
            var names = new List<string>();
            foreach (Territory above in Inheritance.Ancestors(all, t.Id)) names.Add(above.DisplayName);
            return string.Join(" > ", names);
        }

        /// <summary>The order siblings are listed in, in the All tab and the Inside picker: by name, then by id.</summary>
        internal static int ByNameThenId(Territory a, Territory b)
        {
            int c = string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : a.Id.CompareTo(b.Id);
        }

        /// <summary>One line of the Inside picker: a territory, how deep it sits, and whether it can be the parent.</summary>
        internal struct PickRow
        {
            public Territory Territory;
            public int Depth;

            /// <summary>Why it cannot be the parent, in the server's own words (Inheritance.CheckParent); null when it can.</summary>
            public string Refusal;

            /// <summary>The refusal in a word or two for the row itself: "itself", "inside it" or "too deep"; null when it can.</summary>
            public string Tag;
        }

        /// <summary>
        /// The picker's rows in tree order, every territory listed: the ones the server would refuse as the parent of territory
        /// <paramref name="targetId"/> (0 for a draft) carry the reason and are greyed by the panel, never left out.
        /// </summary>
        internal static List<PickRow> PickerRows(IReadOnlyList<Territory> all, int targetId)
        {
            var rows = new List<PickRow>(all.Count);
            foreach (Inheritance.TreeRow row in Inheritance.TreeOrder(all, ByNameThenId))
            {
                int id = row.Territory.Id;
                string why = Inheritance.CheckParent(all, targetId, id);
                string tag = null;
                if (why != null)
                    tag = targetId != 0 && id == targetId ? "itself"
                        : targetId != 0 && Inheritance.IsInside(all, id, targetId) ? "inside it"
                        : "too deep";
                rows.Add(new PickRow { Territory = row.Territory, Depth = row.Depth, Refusal = why, Tag = tag });
            }
            return rows;
        }

        /// <summary>"1 sub-territory", "3 sub-territories".</summary>
        internal static string Plural(int n, string one, string many) => n == 1 ? $"{n} {one}" : $"{n} {many}";

        /// <summary>The Delete button's face for a territory with territories inside it: what happens to them.</summary>
        internal static string DeleteKeepsLabel(int inside) =>
            inside == 1 ? "Delete (its 1 sub-territory keeps its rules)" : $"Delete (its {inside} sub-territories keep their rules)";

        // ─────────────────────────────────────────────────────────────
        // THE COPY BEING EDITED - what a pick or a click does to it (the panel's draft, or the placed territory it edits)
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Brings the followed settings of a copy up to date with where they come from: its parent's effective record as
        /// <paramref name="find"/> has it NOW, or <paramref name="fresh"/> (a fresh draft) for the automatic parent, which has no
        /// record to look at. What the territory sets itself is never touched; a parent that has gone leaves the copy as it was.
        /// Safe to repeat.
        /// </summary>
        internal static void Resync(Territory t, Func<int, Territory> find, Territory fresh)
        {
            if (t == null) return;
            if (t.ParentId > 0)
            {
                Inheritance.Refresh(t, find(t.ParentId));
            }
            else if (t.ParentId < 0 && fresh != null)
            {
                // Marked in open country the server keeps these values, so what follows reads as a fresh draft's.
                Inheritance.Pull(t, fresh, t.FollowFlags);
                Inheritance.Pull(t, fresh, t.Follow);
            }
        }

        /// <summary>
        /// The Inside picker's choice: <paramref name="parentId"/> is a territory, 0 for the top level, or Territory.AutoParent
        /// (a draft only). Out of any parent, the copy keeps what it shows as its own and follows nothing. A plain top-level
        /// DRAFT put inside something starts following everything the admin has not changed (Inheritance.FollowWhatIsUnchanged
        /// against <paramref name="fresh"/>, the draft before it was touched); a PLACED territory put inside something - from the
        /// top level or from another parent - follows nothing until asked, its settings stay exactly as they are (moving it never
        /// changes how it behaves, as taking its parent away never does). Choosing where it already is changes nothing.
        /// </summary>
        internal static void PutInside(Territory t, int parentId, bool draft, Territory fresh, Func<int, Territory> find)
        {
            int was = t.ParentId;
            if (parentId == was) return;

            if (parentId == 0)
            {
                t.ParentId = 0;
                t.FollowFlags = TerritoryFlags.None;
                t.Follow = TerritoryFollow.None;
                return;
            }

            t.ParentId = parentId;
            if (draft)
            {
                if (was == 0) Inheritance.FollowWhatIsUnchanged(t, fresh);
            }
            else if (was > 0)
            {
                // Out of one parent into another: what it showed (what it followed from the old one included) is its own now.
                t.FollowFlags = TerritoryFlags.None;
                t.Follow = TerritoryFollow.None;
            }
            Resync(t, find, fresh);
        }

        /// <summary>
        /// A click on a rule: what it followed is the territory's own from now on, and the rule is switched. Switching one off
        /// takes the ones that need it along (the barrier under No monster spawns), so the copy is never one the server would
        /// have to correct - Territory.CleanFlags, the server's own rule - and the followed rules are then read from the parent
        /// again, so a barrier that follows reads what the parent has the moment the rule under it is switched back on.
        /// </summary>
        internal static void ToggleRule(Territory t, TerritoryFlags rule, Territory fresh, Func<int, Territory> find)
        {
            bool on = t.Has(rule);
            Inheritance.Own(t, rule);
            t.Set(rule, !on);
            t.Flags = Territory.CleanFlags(t.Flags);
            Resync(t, find, fresh);
        }

        // ─────────────────────────────────────────────────────────────
        // WIDTHS - a row of buttons that must not be wider than the panel
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Widths for a row of buttons that has <paramref name="avail"/> to share. All the <paramref name="preferred"/> width when
        /// there is room; else all alike, as wide as the room allows, while that is enough for the longest face; else each its
        /// <paramref name="mins"/> (the face and its padding) plus an equal share of what is left; else just the mins, which no
        /// arithmetic can fit. A row that is too wide gets a sideways scroll bar, and every column after it falls off the edge.
        /// </summary>
        internal static float[] FitWidths(float avail, float preferred, float[] mins)
        {
            int n = mins.Length;
            var widths = new float[n];
            if (n == 0) return widths;

            float longest = 0f, sum = 0f;
            foreach (float m in mins)
            {
                longest = Mathf.Max(longest, m);
                sum += m;
            }

            // A preferred width under the longest face would clip it: the faces always come first.
            preferred = Mathf.Max(preferred, longest);
            float equal = (float)Math.Floor(avail / n);
            for (int i = 0; i < n; i++)
            {
                if (equal >= preferred) widths[i] = preferred;
                else if (equal >= longest) widths[i] = equal;
                else if (avail >= sum) widths[i] = mins[i] + (float)Math.Floor((avail - sum) / n);
                else widths[i] = mins[i];
            }
            return widths;
        }

        // ─────────────────────────────────────────────────────────────
        // REBASE - an edit in progress, carried onto the server's newer record
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// The server's record with the admin's unsaved changes laid over it: every setting the admin changed
        /// since <paramref name="baseline"/> (the record the edit started from) keeps the admin's value, and
        /// everything else - the ground, the history, whatever the server or another admin changed - is the
        /// server's. Members merge one by one: the server's list, less whoever the admin took off, plus whoever
        /// the admin put on - so the members a combine brought in survive the admin's next Save.
        /// </summary>
        /// <remarks>
        /// 0.4.0: the same rule for where it sits and what it follows. The parent is the admin's if the admin moved it,
        /// else the server's; so is each rule's mark, bit by bit, and each group's, group by group (a mark is a choice
        /// like any other: whether the setting follows the parent). The values are merged as before and are the merge's
        /// last word only for what the territory sets itself: what it follows is the parent's, which this does not look
        /// up (it is pure), so the caller brings the followed values up to date from the parent afterwards
        /// (Inheritance.Refresh), as it does after every change to the list.
        /// </remarks>
        internal static Territory Rebase(Territory mine, Territory baseline, Territory server)
        {
            Territory t = server.Clone();

            if (mine.ParentId != baseline.ParentId) t.ParentId = mine.ParentId;
            TerritoryFlags ruleMarks = mine.FollowFlags ^ baseline.FollowFlags;
            TerritoryFollow groupMarks = mine.Follow ^ baseline.Follow;
            t.FollowFlags = (server.FollowFlags & ~ruleMarks) | (mine.FollowFlags & ruleMarks);
            t.Follow = (server.Follow & ~groupMarks) | (mine.Follow & groupMarks);
            // A territory with no parent follows nothing, whichever side that came from (the model's own rule).
            if (t.ParentId == 0)
            {
                t.FollowFlags = TerritoryFlags.None;
                t.Follow = TerritoryFollow.None;
            }

            if (mine.Name != baseline.Name) t.Name = mine.Name;
            if (!SameColour(mine.Color, baseline.Color)) t.Color = mine.Color;
            TerritoryFlags changed = mine.Flags ^ baseline.Flags;
            // Cleaned: the admin's switch of a rule and another admin's switch of a rule that needs it can meet here,
            // and the merge must not leave a barrier standing on a rule that is off (Territory.CleanFlags).
            t.Flags = Territory.CleanFlags((server.Flags & ~changed) | (mine.Flags & changed));
            if (mine.PvP != baseline.PvP) t.PvP = mine.PvP;
            if (mine.Priority != baseline.Priority) t.Priority = mine.Priority;
            if (mine.HealthRegen != baseline.HealthRegen) t.HealthRegen = mine.HealthRegen;
            if (mine.StaminaRegen != baseline.StaminaRegen) t.StaminaRegen = mine.StaminaRegen;
            if (mine.EitrRegen != baseline.EitrRegen) t.EitrRegen = mine.EitrRegen;
            if (mine.CarryBonus != baseline.CarryBonus) t.CarryBonus = mine.CarryBonus;
            if (mine.ArmorBonus != baseline.ArmorBonus) t.ArmorBonus = mine.ArmorBonus;
            if (mine.EnterMessage != baseline.EnterMessage) t.EnterMessage = mine.EnterMessage;
            if (mine.LeaveMessage != baseline.LeaveMessage) t.LeaveMessage = mine.LeaveMessage;

            if (mine.OwnerId != baseline.OwnerId || mine.OwnerName != baseline.OwnerName)
            {
                t.OwnerId = mine.OwnerId;
                t.OwnerName = mine.OwnerName;
            }

            t.Members.RemoveAll(m => Listed(baseline, m.Id) && !Listed(mine, m.Id));
            foreach (TerritoryMember m in mine.Members)
                if (!Listed(baseline, m.Id) && !Listed(t, m.Id)) t.Members.Add(new TerritoryMember(m.Id, m.Name));

            // The owner is never also listed as a member: the server drops it, and the panel's Make owner takes
            // them off the list, which the merge above reads as the admin taking them off.
            if (t.OwnerId != 0L) t.Members.RemoveAll(m => m.Id == t.OwnerId);
            return t;
        }

        private static bool Listed(Territory t, long id) => t.Members.Exists(m => m.Id == id);

        private static bool SameColour(Color32 a, Color32 b) => a.r == b.r && a.g == b.g && a.b == b.b && a.a == b.a;
    }
}
