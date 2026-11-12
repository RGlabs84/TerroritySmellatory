using System.Collections.Generic;
using UnityEngine;

namespace TerroritySmellatory
{
    /// <summary>
    /// The 0.2.0 ground operations - combine two territories, add a part, reshape one part, remove one, split
    /// one off - as plain edits of <see cref="Territory"/> records, with no game state. The server runs
    /// exactly what the off-game harness checks.
    /// </summary>
    /// <remarks>
    /// Each returns null when it worked, or the reason it refused in words an admin reads in the panel's
    /// status line, and a refusal leaves every record exactly as it was. Nothing here saves or sanitises: the
    /// server edits a clone, runs Territory.Sanitize on it, saves, and only then replaces the stored record
    /// (Net/TerritoryServer.cs), so a refused or unsaveable edit never reaches anyone.
    ///
    /// COMBINING keeps the TARGET's identity: its id, name, colour, rules, priority, banner texts and owner.
    /// The other territory's ground is appended to the target's parts, its owner and members join the target's
    /// members - nobody who could build on that ground loses it in the merge - and it is then deleted. What
    /// the other territory had set for itself (its name and rules) goes with it; to keep those, combine the
    /// other way round.
    /// </remarks>
    internal static class TerritoryEdits
    {
        // ─────────────────────────────────────────────────────────────
        // CREATE AND UPDATE (0.4.0: the sub-territory's parent, and what it follows)
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// A Create request's record, made ready to store: its parent settled, checked and sanitised, and everything it
        /// follows put back to the plain defaults. Null when it worked, otherwise why not (the admin reads it).
        /// </summary>
        /// <param name="stored">The server's stored records.</param>
        /// <param name="inForce">The effective records in precedence order (TerritoryIndex.All): where the new ground's middle is
        /// governed, for <see cref="Territory.AutoParent"/>.</param>
        /// <param name="maxHalfExtent">The server's MaxRadius: the new ground is clamped to it.</param>
        internal static string PrepareNew(IReadOnlyList<Territory> stored, IReadOnlyList<Territory> inForce, Territory t, float maxHalfExtent)
        {
            if (t.ParentId == Territory.AutoParent)
            {
                // The deepest territory holding the middle of its first part: TerritoryIndex puts sub-territories ahead of
                // the ones they sit in, so the first match is the innermost.
                t.ParentId = 0;
                var middle = new Vector3(t.X, t.Y, t.Z);
                foreach (Territory candidate in inForce)
                {
                    if (!candidate.Ground.Contains(middle)) continue;
                    t.ParentId = candidate.Id;
                    break;
                }
            }

            string bad = Inheritance.CheckParent(stored, 0, t.ParentId);
            if (bad != null) return bad;
            if (!t.Sanitize(maxHalfExtent)) return "The territory's position or size was not a number.";
            Inheritance.Canonicalize(t);
            return null;
        }

        /// <summary>
        /// An Update request laid on a clone of the stored record: the admin's settings, parent and follow marks replace its own;
        /// the ground, id and history stay. A parent of <see cref="Territory.AutoParent"/> (or less) means "leave it where it is".
        /// The sender's values for what the territory FOLLOWS are its view of the parent and are dropped, so a stale view can
        /// never overwrite anything. Null when it worked, otherwise why not.
        /// </summary>
        /// <param name="maxHalfExtent">The server's MaxRadius. An edit never shrinks ground placed under a larger one: the
        /// largest part already there is the floor.</param>
        internal static string Update(IReadOnlyList<Territory> stored, Territory existing, Territory incoming, float maxHalfExtent, out Territory updated)
        {
            updated = existing.Clone();
            updated.CopySettingsFrom(incoming);
            if (incoming.ParentId < 0) updated.ParentId = existing.ParentId;

            string bad = Inheritance.CheckParent(stored, existing.Id, updated.ParentId);
            if (bad != null) return bad;
            if (!updated.Sanitize(Mathf.Max(maxHalfExtent, updated.Ground.MaxHalfExtent))) return "The territory did not validate.";
            Inheritance.Canonicalize(updated);
            if (string.IsNullOrEmpty(updated.Name)) updated.Name = $"Territory {updated.Id}";
            updated.Revision = existing.Revision + 1;
            return null;
        }

        /// <summary>
        /// Null when <paramref name="from"/> may be combined into <paramref name="into"/>. A territory and one inside it cannot be
        /// combined: the sub-territory's ground would end up in its own parent, and the link between them has nowhere to go. Take
        /// one out of the other first.
        /// </summary>
        internal static string CombineRefusal(IReadOnlyList<Territory> stored, Territory into, Territory from)
        {
            if (Inheritance.IsInside(stored, into.Id, from.Id))
                return $"'{into.DisplayName}' is inside '{from.DisplayName}', so they cannot be combined - take it out of '{from.DisplayName}' first (Inside, on the Rules tab).";
            if (Inheritance.IsInside(stored, from.Id, into.Id))
                return $"'{from.DisplayName}' is inside '{into.DisplayName}', so they cannot be combined - take it out of '{into.DisplayName}' first (Inside, on the Rules tab).";
            return null;
        }

        /// <summary>Appends <paramref name="from"/>'s whole ground to <paramref name="into"/> and adds its owner and members to <paramref name="into"/>'s members.</summary>
        /// <param name="membersJoined">How many people became members of <paramref name="into"/>.</param>
        internal static string Combine(Territory into, Territory from, int maxParts, out int membersJoined)
        {
            membersJoined = 0;
            if (into == null || from == null) return "Combining needs two territories.";
            if (ReferenceEquals(into, from) || (into.Id != 0 && into.Id == from.Id)) return $"'{into.DisplayName}' cannot be combined with itself.";

            int parts = into.PartCount + from.PartCount;
            if (parts > maxParts)
                return $"Together they would be {parts} parts; a territory may have at most {maxParts} (server setting MaxParts).";

            var joining = new List<TerritoryMember>();
            if (from.OwnerId != 0L) joining.Add(new TerritoryMember(from.OwnerId, from.OwnerName));
            joining.AddRange(from.Members);

            var add = new List<TerritoryMember>();
            foreach (TerritoryMember m in joining)
            {
                if (m == null || m.Id == 0L || into.IsMember(m.Id) || add.Exists(x => x.Id == m.Id)) continue;
                add.Add(new TerritoryMember(m.Id, m.Name));
            }
            if (into.Members.Count + add.Count > Territory.MaxMembers)
                return $"Together they would have {into.Members.Count + add.Count} members; a territory holds at most {Territory.MaxMembers}.";

            into.ExtraParts.Add(from.Shape);
            into.ExtraParts.AddRange(from.ExtraParts);
            into.Members.AddRange(add);
            membersJoined = add.Count;
            return null;
        }

        /// <summary>
        /// <see cref="Combine"/> for a STORED target that may take its people from its parent (0.4.0). Who joins is judged against
        /// the people the target EFFECTIVELY has (<paramref name="intoEffective"/>, <paramref name="fromEffective"/>: the other's
        /// own may be its parent's). If somebody joins, the target takes its parent's people as its own list first - a followed list
        /// is the parent's, not the target's to add to - and keeps them all plus the newcomers; if nobody does, only the ground is
        /// added and the target goes on following. <paramref name="into"/> is left as it was when this refuses.
        /// </summary>
        internal static string CombineStored(Territory into, Territory intoEffective, Territory fromEffective, int maxParts, out int membersJoined)
        {
            membersJoined = 0;
            if (into == null || intoEffective == null || fromEffective == null || !into.Follows(TerritoryFollow.People))
                return Combine(into, fromEffective, maxParts, out membersJoined);

            Territory trial = into.Clone();
            Inheritance.OwnFrom(trial, intoEffective, TerritoryFollow.People);
            string refused = Combine(trial, fromEffective, maxParts, out int would);
            if (refused != null) return refused;

            if (would > 0)
            {
                Inheritance.OwnFrom(into, intoEffective, TerritoryFollow.People);
                return Combine(into, fromEffective, maxParts, out membersJoined);
            }

            Territory groundOnly = fromEffective.Clone();
            groundOnly.OwnerId = 0L;
            groundOnly.OwnerName = "";
            groundOnly.Members.Clear();
            return Combine(into, groundOnly, maxParts, out membersJoined);
        }

        internal static string AddPart(Territory t, TerritoryShape part, int maxParts)
        {
            if (t.PartCount + 1 > maxParts)
                return $"'{t.DisplayName}' already has {t.PartCount} parts; a territory may have at most {maxParts} (server setting MaxParts).";
            t.ExtraParts.Add(part);
            return null;
        }

        /// <param name="index">0 for the first part, 1.. for the added ones - as the panel numbers them, less one.</param>
        internal static string ReshapePart(Territory t, int index, TerritoryShape part)
        {
            if (index < 0 || index >= t.PartCount) return NoSuchPart(t, index);
            t.SetPart(index, part);
            return null;
        }

        internal static string RemovePart(Territory t, int index)
        {
            if (index < 0 || index >= t.PartCount) return NoSuchPart(t, index);
            if (t.PartCount < 2) return $"'{t.DisplayName}' has only the one part - delete the territory instead.";
            t.RemovePartAt(index);
            return null;
        }

        /// <summary>
        /// Takes part <paramref name="index"/> out of <paramref name="t"/> as a territory of its own: the same
        /// rules, owner and members, named "... (split)", id 0 until the server adds it. The undo of a combine,
        /// except that the split-off part keeps this territory's rules, not the ones it had before.
        /// </summary>
        internal static Territory Split(Territory t, int index, out string error)
        {
            error = null;
            if (index < 0 || index >= t.PartCount)
            {
                error = NoSuchPart(t, index);
                return null;
            }
            if (t.PartCount < 2)
            {
                error = $"'{t.DisplayName}' has only the one part - there is nothing to split off.";
                return null;
            }

            Territory split = t.Clone();
            split.Id = 0;
            split.ExtraParts.Clear();
            split.SetPart(0, t.GetPart(index));
            split.Name = Territory.CleanName(t.DisplayName + " (split)", Territory.MaxNameLength);
            split.CreatedBy = "";
            split.CreatedTicks = 0L;
            split.Revision = 0;

            t.RemovePartAt(index);
            return split;
        }

        private static string NoSuchPart(Territory t, int index) =>
            $"'{t.DisplayName}' has no part {index + 1} (it has {t.PartCount}).";
    }
}
