using System;
using System.Collections.Generic;
using UnityEngine;

namespace TerroritySmellatory
{
    /// <summary>
    /// The territories this process knows about, and the ONE rule for which of them governs a point.
    /// </summary>
    /// <remarks>
    /// On the server it is filled from the store after every load and every change; on a client it is
    /// filled from the last snapshot the server pushed. On a listen host the two are the same process and
    /// the same list. Every rule in Rules/ asks its questions here and nowhere else, so there is exactly
    /// one definition of "who governs this spot".
    ///
    /// PRECEDENCE: highest Priority first; on a tie the SMALLER territory; on a tie the older (lower id).
    /// "Smaller wins" is what makes nesting work without anybody touching a number - a public market
    /// drawn inside a protected kingdom governs its own streets. Priority exists for the rare case where
    /// the big one must win anyway. A SUB-territory (Territory.ParentId, 0.4.0) always governs its own
    /// ground over the territories it sits inside, whatever their sizes and priorities say
    /// (Inheritance.ChildrenFirst); everything else keeps its place. "Smaller" is the area of the whole ground, overlapping parts counted
    /// once (TerritoryGround.MeasureArea), and every question below is asked of the whole ground: a
    /// territory of several parts is one area here, exactly as it is on the map.
    /// </remarks>
    internal static class TerritoryIndex
    {
        private static List<Territory> _all = new List<Territory>();
        private static Dictionary<int, Territory> _byId = new Dictionary<int, Territory>();

        /// <summary>Precedence-ordered and never mutated in place: a new list replaces it, so callers may hold a reference.</summary>
        public static IReadOnlyList<Territory> All => _all;

        public static bool Any => _all.Count > 0;

        /// <summary>Changes every time the list is replaced. Cheap "did anything change?" for the visuals.</summary>
        public static int LocalRevision { get; private set; }

        public static event Action Changed;

        public static void Set(IEnumerable<Territory> territories)
        {
            var list = new List<Territory>(territories ?? Array.Empty<Territory>());

            // Measured once per list, not once per comparison: a many-part territory's area is a few hundred
            // microseconds of arithmetic.
            var areas = new Dictionary<Territory, float>(list.Count);
            foreach (Territory t in list) areas[t] = t.Ground.MeasureArea();
            list.Sort((a, b) => Precedence(a, b, areas));
            _all = Inheritance.ChildrenFirst(list);

            // By id, first in precedence order wins - what a scan of the list has always answered for a repeated id.
            var byId = new Dictionary<int, Territory>(_all.Count);
            foreach (Territory t in _all)
                if (!byId.ContainsKey(t.Id)) byId[t.Id] = t;
            _byId = byId;
            LocalRevision++;

            try { Changed?.Invoke(); }
            catch (Exception ex) { Plugin.Log.LogError($"A territory-change listener threw (non-fatal): {ex}"); }
        }

        public static void Clear()
        {
            if (_all.Count == 0) return;
            Set(Array.Empty<Territory>());
        }

        private static int Precedence(Territory a, Territory b, Dictionary<Territory, float> areas)
        {
            int c = b.Priority.CompareTo(a.Priority);
            if (c != 0) return c;
            c = areas[a].CompareTo(areas[b]);
            if (c != 0) return c;
            return a.Id.CompareTo(b.Id);
        }

        public static Territory Find(int id)
        {
            _byId.TryGetValue(id, out Territory t);
            return t;
        }

        /// <summary>
        /// Is territory <paramref name="id"/> a sub-territory of <paramref name="ancestorId"/>, at any depth (Territory.ParentId, 0.4.0)?
        /// The link, not the ground: a sub-territory whose middle lies outside its parent is still inside it.
        /// </summary>
        public static bool IsInside(int id, int ancestorId)
        {
            if (ancestorId <= 0 || id == ancestorId) return false;
            for (int hops = 0; hops <= Inheritance.MaxDepth + 1; hops++)
            {
                if (!_byId.TryGetValue(id, out Territory t) || t.ParentId <= 0 || t.ParentId == id) return false;
                if (t.ParentId == ancestorId) return true;
                id = t.ParentId;
            }
            return false;
        }

        /// <summary>The territory that decides the rules at this point, or null for open country.</summary>
        public static Territory GoverningAt(Vector3 p)
        {
            List<Territory> all = _all;
            for (int i = 0; i < all.Count; i++)
                if (all[i].Ground.Contains(p)) return all[i];
            return null;
        }

        /// <summary>
        /// Would <paramref name="protection"/> stop this player acting on a circle of <paramref name="radius"/>
        /// around <paramref name="p"/>? Returns the territory that says no, or null.
        /// </summary>
        /// <remarks>
        /// For a point (radius 0) this is simply "does the governing territory protect it against you".
        /// For a circle - a ward being placed, a hoe or pickaxe stroke - the question vanilla's own wards
        /// ask is "does it TOUCH a protected area", and that is kept, with one refinement so nesting still
        /// works: walking down the precedence order, a territory that allows the action AND contains the
        /// whole circle settles it, because nothing below it governs any part of that circle.
        /// </remarks>
        public static Territory Denying(Vector3 p, float radius, TerritoryFlags protection, long playerId, bool bypass)
        {
            if (bypass) return null;

            List<Territory> all = _all;
            for (int i = 0; i < all.Count; i++)
            {
                Territory t = all[i];
                TerritoryGround ground = t.Ground;
                if (!ground.Overlaps(p, radius)) continue;
                if (t.Has(protection) && !t.IsMember(playerId)) return t;
                if (ground.ContainsCircle(p, radius)) return null;
            }
            return null;
        }

        /// <summary>The nearest territory to a point that the point is NOT in, and how far away its edge is.</summary>
        public static Territory Nearest(Vector3 p, out float distance)
        {
            Territory best = null;
            distance = float.MaxValue;
            foreach (Territory t in _all)
            {
                float d = t.Ground.DistanceOutside(p);
                if (d < distance)
                {
                    distance = d;
                    best = t;
                }
            }
            return best;
        }
    }
}
