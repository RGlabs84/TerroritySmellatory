using System;
using System.Collections.Generic;
using UnityEngine;

namespace TerroritySmellatory
{
    // AUTO-CLOSE DOORS (0.3.1) - a door or gate left open inside a territory with AutoCloseDoors shuts itself after the
    // server's AutoCloseDelay (10 s), unless a player is within AutoClosePlayerClearance (3 m) of it. Taken from Get Off
    // My Lawn's DoorCloser; the decisions are in Rules/DoorMath.cs (pure, harness-tested), this file is the part that
    // touches the game.
    //
    // RUN BY THE SERVER ALONE (ZNet.IsServer: a dedicated server, a listen host and single player), unlike every other
    // rule here, which each player's own game enforces. A door's whole replicated state is one int on its ZDO
    // (ZDOVars.s_state: 0 closed, 1 or -1 open either way), and every game's Door re-reads it whenever the ZDO's
    // DataRevision moves - five polls a second - and plays the animation. So ONE write of 0, made by the server, closes
    // the door on every screen with nothing more to send, and no client decides anything. (Closing it from whichever game
    // happens to own the door would work only while a player is near enough to own it, and would make every game
    // enforce a rule the server owns the settings of.)
    //
    // WHERE. Only doors in the scan (the sectors each player's game is simulating, GOML's sector scan: a door far from
    // every player is not looked at, and starts counting when a player comes near). A door counts when its spot is
    // GOVERNED by a territory with the rule (DoorMath.ClosingTerritory); the timer of a door that leaves the scan is
    // dropped. With no territory holding the rule the scan does not run and the door catalog is not even built.
    //
    // THE WRITE. A door is owned by whichever client is near it, and a plain server write can lose the race against
    // that client's own writes (a player using the door as we close it), so a door the server does not own is taken
    // through OwnershipHold: claim, jump the revision, write 0, hand it back 0.75 s later. That timing was tuned live in
    // GOML and is not changed. While a door is held it is left alone. Doors nobody owns and doors this game owns (a
    // listen host's own) are written directly.
    //
    // LEFT ALONE. Drawbridges and window shutters (both carry a Door), and any prefab an admin lists in
    // AutoCloseExcludedPrefabs; doors that need a key (crypts, dungeon gates) and one-way doors, which players cannot
    // close either; dungeon interiors (DoorMath.InteriorHeight).
    internal static class DoorCloser
    {
        /// <summary>
        /// Seconds between scans. Creatures need GOML's 0.3 s; a door has a delay of at least a second, so half a second
        /// costs nothing in closing time and a listen host a third less time in the sector scan.
        /// </summary>
        private const float ScanSeconds = 0.5f;

        /// <summary>GOML's OwnershipHandBackDelay default, tuned live: below ~0.3 the hand-back can be lost on a laggy link.</summary>
        private const float HandBackSeconds = 0.75f;

        private const float SummarySeconds = 300f;

        private struct DoorType
        {
            public string Name;
            public bool Closable;
        }

        // The door catalog: every prefab in the world's ZNetScene that carries a Door, keyed by the hash a ZDO carries.
        private static readonly Dictionary<int, DoorType> _types = new Dictionary<int, DoorType>();
        private static readonly Dictionary<string, int> _hashByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<int> _excluded = new HashSet<int>();
        private static string _excludedRaw;
        private static int _excludedGeneration = -1;
        private static int _generation;
        private static bool _catalogBuilt;

        private static readonly DoorClock<ZDOID> _clock = new DoorClock<ZDOID>();
        private static readonly List<ZDO> _sector = new List<ZDO>();
        private static readonly HashSet<int> _zones = new HashSet<int>();
        private static readonly HashSet<ZDOID> _seenDoors = new HashSet<ZDOID>();
        private static readonly List<Vector3> _players = new List<Vector3>();

        private static ZNetScene _scene;
        private static int _indexRevision = -1;
        private static bool _anyCloses;
        private static float _scanTimer;
        private static float _summaryTimer;
        private static int _closed;

        internal static void Tick()
        {
            ZNet net = ZNet.instance;
            ZNetScene scene = ZNetScene.instance;
            if (net == null || scene == null || ZDOMan.instance == null || ZoneSystem.instance == null || !net.IsServer())
            {
                // Not the server (a client does nothing at all), or no world: forget the last one.
                if (_scene != null) Reset();
                return;
            }
            if (!ReferenceEquals(_scene, scene))
            {
                Reset();
                _scene = scene;
            }

            float now = Time.time;
            OwnershipHold.Update(now); // every frame, and even with the rule off: a hold in flight is always handed back

            float dt = Time.deltaTime;
            _summaryTimer += dt;
            if (_summaryTimer >= SummarySeconds)
            {
                _summaryTimer = 0f;
                if (_closed > 0) Plugin.Log.LogMessage($"doors: {_closed} auto-closed in the last {SummarySeconds / 60f:0} min.");
                _closed = 0;
            }

            _scanTimer += dt;
            if (_scanTimer < ScanSeconds) return;
            _scanTimer = 0f;

            if (TerritoryIndex.LocalRevision != _indexRevision)
            {
                _indexRevision = TerritoryIndex.LocalRevision;
                _anyCloses = DoorMath.AnyCloses(TerritoryIndex.All);
            }
            if (!_anyCloses)
            {
                _clock.Clear(); // switched off while doors were counting: they start afresh if it comes back
                return;
            }

            if (!_catalogBuilt) BuildCatalog(scene);
            Scan(net, now);
        }

        private static void Reset()
        {
            _scene = null;
            OwnershipHold.Clear();
            _clock.Clear();
            _types.Clear();
            _hashByName.Clear();
            _excluded.Clear();
            _excludedRaw = null;
            _catalogBuilt = false;
            _scanTimer = 0f;
            _indexRevision = -1;
        }

        private static void Scan(ZNet net, float now)
        {
            _players.Clear();
            foreach (ZDO character in net.GetAllCharacterZDOS())
                if (character != null && character.IsValid()) _players.Add(character.GetPosition());

            // Nothing is simulated anywhere without a player, so nothing can be opened or shut. Timers go too: a door
            // that stood open through an empty world would be closed on a stale clock the moment somebody arrived.
            if (_players.Count == 0)
            {
                _clock.Clear();
                return;
            }

            RefreshExcluded();
            float delay = TSConfig.AutoCloseDelay.Value;
            float clearance = TSConfig.AutoClosePlayerClearance.Value;

            _zones.Clear();
            _seenDoors.Clear();
            _clock.BeginScan();

            // The same sectors each client simulates: everything a player's machine could be using.
            int near = Mathf.Clamp(net.GetSyncedSimulationDistance().NearSimulationDistance, 1, 3);
            var distance = new SimulationDistance(near, 0, classic: true);
            ZDOMan man = ZDOMan.instance;

            foreach (Vector3 playerPos in _players)
            {
                Vector2s zone = ZoneSystem.GetZone(playerPos);
                if (!_zones.Add((zone.x << 16) | (zone.y & 0xFFFF))) continue; // several players in one zone share a scan

                _sector.Clear();
                man.FindSectorObjects(zone, distance, _sector);
                foreach (ZDO zdo in _sector)
                {
                    if (zdo == null || !zdo.IsValid()) continue;
                    int prefab = zdo.GetPrefab();
                    if (!_types.TryGetValue(prefab, out DoorType type) || !type.Closable) continue;
                    if (!_seenDoors.Add(zdo.m_uid)) continue; // overlapping player areas

                    ZDOID id = zdo.m_uid;
                    // Held or excluded: not looked at, so no timer runs for it (EndScan drops any that did).
                    if (OwnershipHold.IsHeld(id) || _excluded.Contains(prefab)) continue;

                    bool open = zdo.GetInt(ZDOVars.s_state) != 0;
                    Vector3 position = zdo.GetPosition();
                    Territory territory = open ? DoorMath.ClosingTerritory(position) : null;
                    DoorStage stage = _clock.Observe(id, open, territory != null, now, delay, out float openFor);
                    if (stage != DoorStage.Due) continue;
                    if (DoorMath.AnyWithin(_players, position, clearance)) continue; // keep counting; it closes the moment they step away

                    Close(zdo, now);
                    _clock.Closed(id);
                    _closed++;
                    if (TSConfig.Verbose.Value)
                        Plugin.Debug($"auto-closed {type.Name} at ({position.x:0.#}, {position.y:0.#}, {position.z:0.#}) in '{territory.DisplayName}' after {openFor:0}s open.");
                }
            }

            // Doors that dropped out of the scan (their player left, or the territory's rule was switched off) restart their timer next time.
            _clock.EndScan();
        }

        private static void Close(ZDO zdo, float now)
        {
            if (!zdo.IsOwner()) OwnershipHold.Claim(zdo, now, HandBackSeconds);
            zdo.Set(ZDOVars.s_state, 0);
        }

        private static void BuildCatalog(ZNetScene scene)
        {
            _types.Clear();
            _hashByName.Clear();
            _generation++;
            int closable = 0;
            foreach (GameObject prefab in scene.m_prefabs)
            {
                if (prefab == null) continue;
                Door door = prefab.GetComponent<Door>();
                if (door == null) continue;

                // Key doors (crypts, dungeon gates) and one-way doors cannot be closed by players either; leave them.
                bool can = door.m_keyItem == null && !door.m_canNotBeClosed;
                int hash = prefab.name.GetStableHashCode();
                _types[hash] = new DoorType { Name = prefab.name, Closable = can };
                _hashByName[prefab.name] = hash;
                if (can) closable++;
            }
            _catalogBuilt = true;
            Plugin.Log.LogInfo($"door catalog: {_types.Count} door type(s), {closable} closable (key doors and one-way doors are never closed).");
        }

        /// <summary>The excluded names as prefab hashes, resolved again only when the setting or the catalog changes.</summary>
        private static void RefreshExcluded()
        {
            string raw = TSConfig.AutoCloseExcludedPrefabs.Value ?? "";
            if (_excludedGeneration == _generation && string.Equals(raw, _excludedRaw, StringComparison.Ordinal)) return;
            _excludedGeneration = _generation;
            _excludedRaw = raw;
            _excluded.Clear();

            HashSet<string> shipped = new HashSet<string>(DoorMath.ParseNames(DoorMath.DefaultExcluded), StringComparer.OrdinalIgnoreCase);
            foreach (string name in DoorMath.ParseNames(raw))
            {
                if (_hashByName.TryGetValue(name, out int hash))
                {
                    _excluded.Add(hash);
                    continue;
                }
                // Still honoured by its exact spelling: a door from a mod that registers after our catalog was built. A typo
                // is reported once instead of silently matching nothing - except the shipped names, which name mods'
                // prefabs and would be a warning at every world load on a vanilla server.
                _excluded.Add(name.GetStableHashCode());
                if (!shipped.Contains(name))
                    Plugin.Log.LogWarning($"AutoCloseExcludedPrefabs: no door prefab named '{name}' is known to this world - kept as an exact-name match only.");
            }
        }
    }
}
