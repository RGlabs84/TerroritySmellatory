using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace TerroritySmellatory
{
    // THE GUARDS RULE (0.5.0) - TerritoryFlags.Guards. Ported from ValtimaOnline's town guards (Npcs/NpcPrefabs.cs, Npcs/GuardCalls.cs,
    // Npcs/NpcCombat.cs, Npcs/NpcSeeder.cs); the decisions are in Rules/GuardMath.cs (pure, harness-tested), one guard is
    // Rules/TerritoryGuard.cs, its body Visuals/GuardBodies.cs. This file is the rest:
    //
    //   THE PREFABS, on every machine (ZNetScene.Awake): TS_TerritoryGuard (persistent, posted) and TS_TerritoryGuardCalled (not persistent),
    //   each a clone of the Dvergr - stripped of its loot and its talk, in the Dverger faction, not aggravatable, never after a player's
    //   buildings, armed with the guards' atgeir (Rules/GuardWeapon.cs). The names are PERMANENT: a world's save holds the prefab's hash.
    //
    //   THE POSTS, by the SERVER alone (Reconcile). Every few seconds, and at once when the territory list changes, the server reads every posted
    //   guard in the world (ZDOMan's own iterative prefab scan, a slice a frame) and compares them with GuardMath.Posts of every territory with
    //   the rule: a missing post gets a guard; a guard whose territory is gone or lost the rule, whose post no longer exists or moved (the
    //   territory was reshaped), or that doubles another, is taken away. A guard is a ZDO the server writes, like a door the door closer
    //   closes, so it appears in every game with nothing more to send; the game that is near it simulates it as it would any creature.
    //   Nothing is taken away while the territory file could not be read (TerritoryStore.Writable): a broken file never empties a world.
    //
    //   THE CALL. A player types a call word in chat (GuardCallWords, the client's own); their game finds the nearest monster near them that is
    //   after somebody, and asks the server down its own connection (Protocol.RpcGuardCall: the server knows who asked from the socket, as it
    //   does for every request). The server decides, so a client cannot spam guards: the caller must stand where a territory with guards
    //   governs, within the cooldown, and the target must be a monster near them that is alerted and not tamed. The nearest guard of that
    //   territory within GuardCallRadius is sent (a routed RPC to whichever game owns it: that game runs its AI); with none that close, a guard
    //   appears beside the caller, owned by the caller's game, and goes when the fight is over. The answer goes back as one line of text.
    //
    //   WHAT A GUARD IS NOT. It cannot be hurt (RPC_Damage and ApplyDamage are refused for one: a player with PvP on can hit any Character), it
    //   is not a monster to the barrier or the spawn rules (Hostility.IsHostile), and nobody is its enemy who is not a monster's.
    internal static class Guards
    {
        internal const string PostedPrefab = "TS_TerritoryGuard";
        internal const string CalledPrefab = "TS_TerritoryGuardCalled";
        private const string DonorPrefab = "Dverger";

        internal static readonly int PostedHash = PostedPrefab.GetStableHashCode();
        internal static readonly int CalledHash = CalledPrefab.GetStableHashCode();

        internal static bool IsGuardPrefab(string name) => name == PostedPrefab || name == CalledPrefab;
        internal static bool IsGuardPrefab(int hash) => hash == PostedHash || hash == CalledHash;

        // ─────────────────────────────────────────────────────────────
        // THE PREFABS
        // ─────────────────────────────────────────────────────────────

        private static GameObject _box;
        private static readonly Dictionary<string, GameObject> _built = new Dictionary<string, GameObject>();

        [HarmonyPatch(typeof(ZNetScene), "Awake")]
        internal static class RegisterPatch
        {
            private static void Postfix(ZNetScene __instance)
            {
                try { Register(__instance); }
                catch (Exception e) { Plugin.Log.LogError($"[guards] could not register the guard prefabs, so there are no guards this session: {e}"); }
            }
        }

        // Each world session builds a new ZNetScene, which knows none of ours: they are added again, from the clones made once and kept.
        private static void Register(ZNetScene zns)
        {
            if (zns == null || zns.m_prefabs == null || zns.m_prefabs.Count == 0) return;
            if (_box == null)
            {
                _box = new GameObject("TS_GuardPrefabs");
                _box.SetActive(false);   // under an inactive parent the clones never wake
                UnityEngine.Object.DontDestroyOnLoad(_box);
            }
            foreach (string name in new[] { PostedPrefab, CalledPrefab })
            {
                if (!_built.TryGetValue(name, out GameObject go) || go == null)
                {
                    go = BuildPrefab(zns, name, persistent: name == PostedPrefab);
                    if (go == null) continue;
                    _built[name] = go;
                }
                if (!zns.m_prefabs.Contains(go)) zns.m_prefabs.Add(go);
                zns.m_namedPrefabs[name.GetStableHashCode()] = go;
            }
        }

        /// <summary>The guard prefabs already built get the guards' atgeir (GuardWeapon.Register, once the ObjectDB that holds it has woken).</summary>
        internal static void ArmPrefabs()
        {
            foreach (GameObject go in _built.Values)
                if (go != null) GuardWeapon.Arm(go.GetComponent<Humanoid>());
        }

        private static GameObject BuildPrefab(ZNetScene zns, string name, bool persistent)
        {
            GameObject donor = zns.GetPrefab(DonorPrefab);
            if (donor == null)
            {
                Plugin.Log.LogWarning($"[guards] {name}: the Dvergr is not in this game, so there are no guards");
                return null;
            }
            GameObject go = UnityEngine.Object.Instantiate(donor, _box.transform, false);
            go.name = name;
            // a guard drops no loot, says no Dvergr lines of its own and takes no star colour
            Strip<CharacterDrop>(go);
            Strip<NpcTalk>(go);
            foreach (LevelEffects le in go.GetComponentsInChildren<LevelEffects>(true)) UnityEngine.Object.DestroyImmediate(le);

            Humanoid h = go.GetComponent<Humanoid>();
            h.m_name = "Territory guard";
            h.m_group = "TS_Guard";                   // one group: the guards are friends to each other
            h.m_faction = Character.Faction.Dverger;  // monsters hunt it, players are not its enemies
            h.m_hideHud = true;                       // no health bar over a guard nobody can hurt
            h.m_boss = false;
            GuardWeapon.Arm(h);                       // the game's iron atgeir; if ObjectDB has not woken yet, GuardWeapon arms it when it does

            MonsterAI ai = go.GetComponent<MonsterAI>();
            ai.m_aggravatable = false;               // a player cannot turn it hostile, and a player's hit does not land
            ai.m_attackPlayerObjects = false;        // never goes for a player's structures
            ai.m_fleeIfLowHealth = 0f;
            ai.m_fleeIfNotAlerted = false;
            ai.m_enableHuntPlayer = false;
            ai.m_randomMoveRange = 2f;
            ai.m_randomMoveInterval = 8f;
            ai.m_maxChaseDistance = 60f;             // gives up a chase this far from its post once it has lost sight (a call lifts it)

            ZNetView nv = go.GetComponent<ZNetView>();
            nv.m_persistent = persistent;
            nv.m_distant = false;
            nv.m_type = ZDO.ObjectType.Default;
            nv.m_syncInitialScale = false;
            go.AddComponent<TerritoryGuard>();
            return go;
        }

        private static void Strip<T>(GameObject go) where T : Component
        {
            T c = go.GetComponent<T>();
            if (c != null) UnityEngine.Object.DestroyImmediate(c);
        }

        // ─────────────────────────────────────────────────────────────
        // WHAT A GUARD IS NOT - every patch gated by LiveCount first, and a prefix returns TRUE (the original runs) for anything that is not one
        // of ours: a prefix's value means "run the original", not "handled" (Valkyries Cargo's F1 made every character immortal getting that backwards).
        // ─────────────────────────────────────────────────────────────

        [HarmonyPatch(typeof(Character), "RPC_Damage")]
        internal static class RpcDamagePatch
        {
            [HarmonyPriority(Priority.Low)]
            private static bool Prefix(Character __instance) => !TerritoryGuard.IsGuard(__instance);
        }

        [HarmonyPatch(typeof(Character), nameof(Character.ApplyDamage))]
        internal static class ApplyDamagePatch
        {
            [HarmonyPriority(Priority.Low)]
            private static bool Prefix(Character __instance) => !TerritoryGuard.IsGuard(__instance);
        }

        // Character is itself a Hoverable and wins the hover lookup over the guard's own component, so the guard's name and "[E] Talk" go into
        // Character's own hover text.
        [HarmonyPatch(typeof(Character), nameof(Character.GetHoverText))]
        internal static class HoverTextPatch
        {
            private static void Postfix(Character __instance, ref string __result)
            {
                TerritoryGuard g = TerritoryGuard.Of(__instance);
                if (g != null) __result = g.HoverText();
            }
        }

        [HarmonyPatch(typeof(Character), nameof(Character.GetHoverName))]
        internal static class HoverNamePatch
        {
            private static void Postfix(Character __instance, ref string __result)
            {
                TerritoryGuard g = TerritoryGuard.Of(__instance);
                if (g != null) __result = g.HoverName();
            }
        }

        // ─────────────────────────────────────────────────────────────
        // THE POSTS - the server
        // ─────────────────────────────────────────────────────────────

        private const float ReconcileSeconds = 5f;
        private static readonly List<ZDO> _scan = new List<ZDO>();
        private static int _scanIndex;
        private static bool _scanning;
        private static float _nextReconcile;
        private static int _indexRevision = -1, _configStamp;
        private static ZNetScene _scene;
        private static readonly HashSet<long> _waterWarned = new HashSet<long>();

        /// <summary>Per frame, from Plugin.Update. A client does nothing here; the server keeps the posts.</summary>
        internal static void Tick()
        {
            ZNet net = ZNet.instance;
            ZNetScene scene = ZNetScene.instance;
            if (net == null || scene == null || ZDOMan.instance == null || !net.IsServer())
            {
                if (_scene != null) Reset();
                return;
            }
            if (!ReferenceEquals(scene, _scene))
            {
                Reset();
                _scene = scene;
            }
            if (!scene.HasPrefab(PostedHash) || !TerritoryStore.IsLoaded) return;

            int stamp = TSConfig.GuardsPerPart.Value * 1000 + TSConfig.MaxGuardsPerTerritory.Value;
            if (!_scanning && (Time.time >= _nextReconcile || TerritoryIndex.LocalRevision != _indexRevision || stamp != _configStamp))
            {
                _indexRevision = TerritoryIndex.LocalRevision;
                _configStamp = stamp;
                _scanning = true;
                _scanIndex = 0;
                _scan.Clear();
            }
            if (!_scanning) return;
            // a slice of the world's sectors a frame (ZDOMan's own scan: 400 sectors a call), so a big world never stalls the server
            if (!ZDOMan.instance.GetAllZDOsWithPrefabIterative(PostedPrefab, _scan, ref _scanIndex)) return;
            _scanning = false;
            _nextReconcile = Time.time + ReconcileSeconds;
            Reconcile(_scan);
            _scan.Clear();
        }

        private static void Reset()
        {
            _scene = null;
            _scan.Clear();
            _scanning = false;
            _nextReconcile = 0f;
            _indexRevision = -1;
            _waterWarned.Clear();
            _lastCall.Clear();
        }

        private static long PostKey(int territory, int post) => ((long)territory << 16) | (uint)(post & 0xFFFF);

        private static void Reconcile(List<ZDO> existing)
        {
            // what the territories want: every post of every territory with the rule, by (territory, post)
            var want = new Dictionary<long, GuardPost>();
            var territoryOf = new Dictionary<long, Territory>();
            var seenIds = new HashSet<int>();
            int perPart = TSConfig.GuardsPerPart.Value, max = TSConfig.MaxGuardsPerTerritory.Value;
            foreach (Territory t in TerritoryIndex.All)
            {
                if (t.Id <= 0 || !seenIds.Add(t.Id) || !t.Has(TerritoryFlags.Guards)) continue;
                foreach (GuardPost p in GuardMath.Posts(t, perPart, max))
                {
                    long key = PostKey(t.Id, p.Index);
                    want[key] = p;
                    territoryOf[key] = t;
                }
            }

            bool mayRemove = TerritoryStore.Writable;
            var have = new HashSet<long>();
            int removed = 0, made = 0;
            foreach (ZDO z in existing)
            {
                if (z == null || !z.IsValid()) continue;
                int territory = z.GetInt(TerritoryGuard.TerritoryKey, 0), post = z.GetInt(TerritoryGuard.PostKey, -1);
                long key = PostKey(territory, post);
                Vector3 at = z.GetVec3(TerritoryGuard.AtKey, z.GetPosition());
                bool keep = want.TryGetValue(key, out GuardPost p) && GuardMath.SamePost(p, at.x, at.z) && have.Add(key);
                if (keep) continue;
                if (!mayRemove) { have.Add(key); continue; }
                Remove(z);
                removed++;
            }

            foreach (var kv in want)
            {
                if (have.Contains(kv.Key)) continue;
                if (CreatePosted(territoryOf[kv.Key], kv.Value) != null) made++;
            }
            if (made > 0 || removed > 0)
                Plugin.Log.LogInfo($"[guards] posts: {made} guard(s) posted, {removed} taken away; {want.Count} post(s) in {territoryOf.Values.Distinct().Count()} territory(ies) with guards");
        }

        // The server owns the world: it takes the guard over and destroys it, and every game that has it removes it
        private static void Remove(ZDO z)
        {
            if (!z.IsOwner()) z.SetOwner(ZDOMan.GetSessionID());
            ZDOMan.instance.DestroyZDO(z);
        }

        private static float WaterLevel => ZoneSystem.instance != null ? ZoneSystem.instance.m_waterLevel : 30f;

        // The world's own ground at a point (a dedicated server has no terrain loaded to ask). A floor built over the post is found by the first
        // game that wakes the guard (TerritoryGuard.Settle); ground raised by a hoe is caught by the game's own under-the-world check.
        private static float GroundY(float x, float z)
        {
            Vector3 p = new Vector3(x, 0f, z);
            if (ZoneSystem.instance != null && ZoneSystem.instance.IsZoneLoaded(p)) return ZoneSystem.instance.GetGroundHeight(p);
            return WorldGenerator.instance != null ? WorldGenerator.instance.GetHeight(x, z) : WaterLevel;
        }

        private static ZDO CreatePosted(Territory t, GuardPost p)
        {
            float ground = GroundY(p.X, p.Z);
            if (ground < WaterLevel - 0.3f)
            {
                if (_waterWarned.Add(PostKey(t.Id, p.Index)))
                    Plugin.Log.LogWarning($"[guards] '{t.DisplayName}' post {p.Index + 1} would stand under water at ({p.X:0}, {p.Z:0}): no guard there");
                return null;
            }
            // a Character falls onto the ground: it starts a hand above it
            Vector3 pos = new Vector3(p.X, ground + 0.4f, p.Z);
            GuardMath.LookFor(t.Id, p.Index, out bool female, out string name);
            return Author(PostedPrefab, pos, p.Yaw, persistent: true, z =>
            {
                z.Set(TerritoryGuard.TerritoryKey, t.Id);
                z.Set(TerritoryGuard.PostKey, p.Index);
                z.Set(TerritoryGuard.AtKey, pos);
                z.Set(TerritoryGuard.YawKey, p.Yaw);
                z.Set(TerritoryGuard.NameKey, name);
                z.Set(TerritoryGuard.FemaleKey, female);
                z.Set(ZDOVars.s_spawnPoint, pos);   // BaseAI's own key: its post, where it stands when idle and heads back to
            });
        }

        /// <summary>A persistent or passing ZDO of one of our prefabs: the prefab, the place, and keys the caller fills in.</summary>
        private static ZDO Author(string prefab, Vector3 pos, float yaw, bool persistent, Action<ZDO> keys)
        {
            int hash = prefab.GetStableHashCode();
            ZDO z = ZDOMan.instance.CreateNewZDO(pos, hash);
            z.SetPrefab(hash);                 // CreateNewZDO does not store it
            z.SetPosition(pos);
            z.SetRotation(Quaternion.Euler(0f, yaw, 0f));
            z.Persistent = persistent;         // ZNetView.Awake does not re-apply the prefab's flag to a ZDO it adopts
            z.Distant = false;
            z.Type = ZDO.ObjectType.Default;
            keys(z);
            ZDOMan.instance.SetDirtySector(z);
            return z;
        }

        // ─────────────────────────────────────────────────────────────
        // THE CALL - the caller's game
        // ─────────────────────────────────────────────────────────────

        private const float ThreatRange = 40f;
        private static float _lastLocalCall = -1000f;

        // every line this player types goes through Chat.SendText (the "say" command and a shout too)
        [HarmonyPatch(typeof(Chat), nameof(Chat.SendText))]
        internal static class ChatPatch
        {
            private static void Postfix(string text)
            {
                try
                {
                    if (GuardMath.ContainsCall(text, TSConfig.GuardCallWords.Value)) Call();
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"[guards] reading a chat line for a guard call: {e.Message}");
                }
            }
        }

        /// <summary>The local player calls the guards: finds a threat and asks the server.</summary>
        internal static void Call()
        {
            Player me = Player.m_localPlayer;
            if (me == null || ZNet.instance == null) return;
            Territory here = TerritoryIndex.GoverningAt(me.transform.position);
            if (here == null || !here.Has(TerritoryFlags.Guards))
            {
                Show("No guards watch this place.");
                return;
            }
            if (Time.time - _lastLocalCall < TSConfig.GuardCallCooldown.Value)
            {
                Show("The guards are still catching their breath.");
                return;
            }
            _lastLocalCall = Time.time;
            Character threat = FindThreat(me, ThreatRange);
            if (threat == null)
            {
                Show("The guards see no threat.");
                return;
            }
            ZDOID target = threat.GetZDOID();
            if (target == ZDOID.None) return;

            if (ZNet.instance.IsServer())
            {
                Show(OnCall(ZDOMan.GetSessionID(), me.transform.position, target));
                return;
            }
            ZNetPeer server = ZNet.instance.GetServerPeer();
            if (server?.m_rpc == null) return;
            server.m_rpc.Invoke(Protocol.RpcGuardCall, target);
        }

        // a creature that is after somebody: an enemy of players whose AI is alerted (a Greydwarf that followed the caller in; a Boar grazing by the
        // road is not alerted until somebody hurts it), not tamed and not a guard; the nearest one
        private static Character FindThreat(Player me, float range)
        {
            Character best = null;
            float bestD = range;
            foreach (Character c in Character.GetAllCharacters())
            {
                if (c == null || c == me || c.IsDead() || c.IsTamed() || c is Player || TerritoryGuard.IsGuard(c)) continue;
                float d = Vector3.Distance(c.transform.position, me.transform.position);
                if (d > bestD) continue;
                BaseAI ai = c.GetBaseAI();
                if (ai == null || !ai.IsAlerted() || !IsMonsterFaction(c.GetFaction()) || !BaseAI.IsEnemy(me, c)) continue;
                best = c;
                bestD = d;
            }
            return best;
        }

        private static bool IsMonsterFaction(Character.Faction f) =>
            f != Character.Faction.AnimalsVeg && f != Character.Faction.Players && f != Character.Faction.Dverger &&
            f != Character.Faction.Boss && f != Character.Faction.PlayerSpawned;

        private static void Show(string text)
        {
            if (!string.IsNullOrEmpty(text) && MessageHud.instance != null) MessageHud.instance.ShowMessage(MessageHud.MessageType.TopLeft, text);
        }

        /// <summary>The server's answer to this client's call (Protocol.RpcGuardNotice).</summary>
        internal static void OnNotice(ZRpc rpc, string text)
        {
            if (Player.m_localPlayer != null) Show(text);
        }

        // ─────────────────────────────────────────────────────────────
        // THE CALL - the server
        // ─────────────────────────────────────────────────────────────

        private const float ThreatReach = 70f;
        private static readonly Dictionary<long, float> _lastCall = new Dictionary<long, float>();
        private static readonly List<ZDO> _near = new List<ZDO>();

        /// <summary>A peer's call, down its own connection (Net/ConnectionPatches.cs). Its caller is the peer the call arrived on.</summary>
        internal static void OnPeerCall(ZNetPeer peer, ZDOID target)
        {
            try
            {
                if (peer == null || ZNet.instance == null || !ZNet.instance.IsServer()) return;
                ZDO character = !peer.m_characterID.IsNone() && ZDOMan.instance != null ? ZDOMan.instance.GetZDO(peer.m_characterID) : null;
                Vector3 at = character != null ? character.GetPosition() : peer.GetRefPos();
                string answer = OnCall(peer.m_uid, at, target);
                if (!string.IsNullOrEmpty(answer) && peer.m_rpc != null) peer.m_rpc.Invoke(Protocol.RpcGuardNotice, answer);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[guards] a guard call from peer {peer?.m_uid}: {e}");
            }
        }

        /// <summary>Decides one call and acts on it. Returns the line the caller is shown.</summary>
        internal static string OnCall(long caller, Vector3 at, ZDOID target)
        {
            if (ZDOMan.instance == null || ZNetScene.instance == null || !ZNetScene.instance.HasPrefab(CalledHash)) return "No guard answers.";
            float now = Time.time;
            if (_lastCall.TryGetValue(caller, out float last) && now - last < TSConfig.GuardCallCooldown.Value) return "The guards are still catching their breath.";
            _lastCall[caller] = now;

            Territory t = TerritoryIndex.GoverningAt(at);
            if (t == null || !t.Has(TerritoryFlags.Guards)) return "No guards watch this place.";
            ZDO tz = ZDOMan.instance.GetZDO(target);
            if (!ValidThreat(tz, at)) return "The guards see no threat.";

            ZDO guard = NearestGuard(t.Id, at, TSConfig.GuardCallRadius.Value);
            if (guard != null)
            {
                // the guard's owner runs its AI: the call goes to the object, and that game's copy answers it
                ZRoutedRpc.instance.InvokeRoutedRPC(guard.GetOwner(), guard.m_uid, TerritoryGuard.GoRpc, target);
                Plugin.Debug($"[guards] '{t.DisplayName}': peer {caller} called; {guard.GetString(TerritoryGuard.NameKey, "a guard")} sent against {target}");
                return "A guard answers your call.";
            }

            Vector3 dir = tz.GetPosition() - at;
            dir.y = 0f;
            dir = dir.sqrMagnitude > 0.01f ? dir.normalized : Vector3.forward;
            Vector3 pos = at + dir * 3f + Vector3.up * 0.3f;
            float yaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
            GuardMath.LookFor((uint)(now * 1000f), out bool female, out string name);
            ZDO fresh = Author(CalledPrefab, pos, yaw, persistent: false, z =>
            {
                z.Set(TerritoryGuard.TerritoryKey, t.Id);
                z.Set(TerritoryGuard.PostKey, -1);
                z.Set(TerritoryGuard.NameKey, name);
                z.Set(TerritoryGuard.FemaleKey, female);
                z.Set(TerritoryGuard.TempKey, true);
                z.Set(TerritoryGuard.TargetKey, target);
                z.Set(ZDOVars.s_spawnPoint, pos);
            });
            fresh.SetOwner(caller);   // last: from here the ZDO is the caller's game's to write, which has the fight in view
            Plugin.Debug($"[guards] '{t.DisplayName}': peer {caller} called; no guard within {TSConfig.GuardCallRadius.Value:0} m, {name} came at {pos}");
            return "The guards come.";
        }

        // a monster near the caller that is after somebody: the prefab is a Character with an AI on the monsters' side, the ZDO says it is alerted
        // (BaseAI writes it), and it is not tamed. The server has no instances, so it reads the prefab and the ZDO.
        private static bool ValidThreat(ZDO tz, Vector3 caller)
        {
            if (tz == null || !tz.IsValid() || Vector3.Distance(tz.GetPosition(), caller) > ThreatReach) return false;
            int hash = tz.GetPrefab();
            if (IsGuardPrefab(hash)) return false;
            GameObject prefab = ZNetScene.instance.GetPrefab(hash);
            Character ch = prefab != null ? prefab.GetComponent<Character>() : null;
            BaseAI ai = prefab != null ? prefab.GetComponent<BaseAI>() : null;
            if (ch == null || ai == null || ch is Player || !IsMonsterFaction(ch.m_faction)) return false;
            return tz.GetBool(ZDOVars.s_alert, false) && !tz.GetBool(ZDOVars.s_tamed, false);
        }

        // the guard of this territory (posted or called) nearest the caller within a radius, among the ZDOs of the zones round them
        private static ZDO NearestGuard(int territory, Vector3 at, float radius)
        {
            _near.Clear();
            int zones = Mathf.Clamp(Mathf.CeilToInt(radius / ZoneSystem.c_ZoneSize), 1, 7);
            ZDOMan.instance.FindSectorObjects(ZoneSystem.GetZone(at), new SimulationDistance(zones, 0, true), _near);
            ZDO best = null;
            float bestD = radius;
            foreach (ZDO z in _near)
            {
                if (z == null || !IsGuardPrefab(z.GetPrefab()) || z.GetInt(TerritoryGuard.TerritoryKey, 0) != territory) continue;
                float d = Vector3.Distance(z.GetPosition(), at);
                if (d <= bestD) { bestD = d; best = z; }
            }
            _near.Clear();
            return best;
        }
    }
}
