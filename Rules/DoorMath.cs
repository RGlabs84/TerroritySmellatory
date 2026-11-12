using System;
using System.Collections.Generic;
using UnityEngine;

namespace TerroritySmellatory
{
    // THE AUTO-CLOSE DOORS RULE, THE DECISIONS (0.3.1). Rules/DoorCloser.cs is the part that touches the game; what it
    // decides is here, pure, and checked off-game (tests/DoorTests.cs): which territory's flag counts for a door, whether a
    // player is close enough to keep it open, how long a door has been open and when it is due, and how the configured
    // list of excluded prefabs is read. The timer logic is Get Off My Lawn's DoorCloser (server-side only), unchanged in
    // what it does; the ONE rule that differs is "inside an active ward" becoming "inside a territory with the rule".
    internal static class DoorMath
    {
        /// <summary>
        /// Shipped as the AutoCloseExcludedPrefabs default, and the names that are NOT warned about when the world has
        /// no such prefab. Drawbridges: raising one under a player halfway across is worse than leaving it down. Window
        /// shutters (wood_window carries a Door too): nobody wants their light shut off. Morkhalla_Drawbridge belongs to a
        /// mod, so on a vanilla world it names nothing - which is right, and not worth a warning at every world load.
        /// </summary>
        internal const string DefaultExcluded = "piece_drawbridge,piece_drawbridge_log,Morkhalla_Drawbridge,wood_window";

        /// <summary>
        /// Height above which a spot is a dungeon interior - vanilla's Character.InInterior (y &gt; 3000). The ground of a
        /// territory ignores height, so one drawn over a burial chamber's entrance would otherwise "contain" the chamber's
        /// doors, thousands of metres above the surface. Those are not doors anyone built.
        /// </summary>
        internal const float InteriorHeight = 3000f;

        /// <summary>
        /// The territory whose AutoCloseDoors rule applies to a door standing at <paramref name="position"/>, or null.
        /// Only the territory that GOVERNS the spot counts (TerritoryIndex.GoverningAt, the rule every other flag uses): a
        /// public market drawn inside a kingdom that closes its doors keeps its own doors as the market says, and a market
        /// that closes them inside a kingdom that does not is honoured. "Any territory containing the door" would let the
        /// big one overrule the small one, which is exactly what precedence exists to prevent.
        /// </summary>
        internal static Territory ClosingTerritory(Vector3 position)
        {
            if (position.y > InteriorHeight) return null;
            Territory governing = TerritoryIndex.GoverningAt(position);
            return governing != null && governing.Has(TerritoryFlags.AutoCloseDoors) ? governing : null;
        }

        /// <summary>Does any territory close its doors? With none, the server does not scan the world at all.</summary>
        internal static bool AnyCloses(IReadOnlyList<Territory> all)
        {
            if (all == null) return false;
            for (int i = 0; i < all.Count; i++)
                if (all[i].Has(TerritoryFlags.AutoCloseDoors)) return true;
            return false;
        }

        /// <summary>
        /// Is any player within <paramref name="clearance"/> metres (a straight line, height included) of the door? Nobody
        /// gets a door shut in their face. 0 or less turns the check off - the door then closes the moment it is due.
        /// </summary>
        internal static bool AnyWithin(List<Vector3> players, Vector3 door, float clearance)
        {
            if (clearance <= 0f || players == null) return false;
            float sqr = clearance * clearance;
            for (int i = 0; i < players.Count; i++)
                if ((players[i] - door).sqrMagnitude <= sqr) return true;
            return false;
        }

        /// <summary>The configured comma-separated prefab names: trimmed, blanks and repeats (any casing) dropped.</summary>
        internal static List<string> ParseNames(string configured)
        {
            var names = new List<string>();
            if (string.IsNullOrEmpty(configured)) return names;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string entry in configured.Split(','))
            {
                string name = entry.Trim();
                if (name.Length > 0 && seen.Add(name)) names.Add(name);
            }
            return names;
        }
    }

    /// <summary>What the clock says about one door in one scan.</summary>
    internal enum DoorStage
    {
        /// <summary>Closed, or not inside a territory that closes doors: nothing is counted for it.</summary>
        Ignore,

        /// <summary>Open, and not open for long enough yet.</summary>
        Counting,

        /// <summary>Open for the whole delay. The caller closes it - unless a player is standing close.</summary>
        Due
    }

    /// <summary>
    /// How long each open door has been open, keyed by whatever names a door (a ZDOID in the game, a number in the
    /// harness). One scan is BeginScan, an Observe for every door looked at, EndScan; the timer of a door that was
    /// not looked at - it left the scan because its player went away, or the territory's rule was switched off, or it
    /// is held or excluded - is dropped at EndScan, so it starts counting afresh the next time it is seen instead of
    /// being closed at once on a stale clock. A door due but with a player near is left counting: it closes the moment
    /// they step away.
    /// </summary>
    internal sealed class DoorClock<TKey>
    {
        private readonly Dictionary<TKey, float> _openSince = new Dictionary<TKey, float>();
        private readonly HashSet<TKey> _seen = new HashSet<TKey>();
        private readonly List<TKey> _prune = new List<TKey>();

        /// <summary>How many doors have a running timer.</summary>
        internal int Tracked => _openSince.Count;

        internal void BeginScan() => _seen.Clear();

        /// <param name="open">The door's state is anything but closed.</param>
        /// <param name="governed">It stands inside a territory that closes doors (DoorMath.ClosingTerritory).</param>
        /// <param name="openFor">Seconds it has been open as of this scan; 0 on the scan that first sees it.</param>
        internal DoorStage Observe(TKey id, bool open, bool governed, float now, float delay, out float openFor)
        {
            openFor = 0f;
            if (!open || !governed)
            {
                _openSince.Remove(id);
                return DoorStage.Ignore;
            }

            _seen.Add(id);
            if (!_openSince.TryGetValue(id, out float since))
            {
                _openSince[id] = now;
                return DoorStage.Counting;
            }

            openFor = now - since;
            return openFor < delay ? DoorStage.Counting : DoorStage.Due;
        }

        /// <summary>The door was closed by us: its timer ends, and a later opening starts a new one.</summary>
        internal void Closed(TKey id) => _openSince.Remove(id);

        internal void EndScan()
        {
            _prune.Clear();
            foreach (KeyValuePair<TKey, float> pair in _openSince)
                if (!_seen.Contains(pair.Key)) _prune.Add(pair.Key);
            foreach (TKey id in _prune) _openSince.Remove(id);
        }

        /// <summary>Forget everything: a scan with nobody to scan around, a new world.</summary>
        internal void Clear()
        {
            _openSince.Clear();
            _seen.Clear();
        }
    }
}
