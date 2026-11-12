using System;
using System.Collections.Generic;
using UnityEngine;

namespace TerroritySmellatory
{
    // THE RUNIC BARRIER (0.3.0) - keeps hostile creatures out of a territory, and with the visitor option players who
    // are not members. The decisions are in Rules/BarrierMath.cs (pure, harness-tested); this file is the part that
    // touches the game. Two jobs, one loop, run by every player's own game and by nobody else:
    //
    //   THE HOLD. Whichever game SIMULATES a thing - its ZNetView is owned here - keeps it out. Physics has just
    //   moved it (this runs in Update, after the fixed steps); if it now stands inside a barrier's ground it is put
    //   back where it was last seen outside, and the part of its velocity that heads into the edge is cancelled,
    //   so it slides along the edge instead of shaking against it. A thing FOUND inside - spawned before the
    //   barrier, loaded in, the barrier just switched on, a player who logged in there or came through a portal -
    //   goes to the nearest way out instead (BarrierMath.NearestOutside). The move is the Rigidbody's:
    //   transform.position alone is reverted from a non-kinematic body on the next physics step
    //   (HEADLESS-AND-EMPTY-SERVER-FACTS #4), so the body is set as well as the transform, and Unity is told to
    //   sync once after the loop. The owner's ZSyncTransform then writes the corrected body position into the ZDO in LateUpdate, so
    //   every other game sees the correction and no ZDO is ever written from here: no ownership claim, no
    //   revision jump, none of GetOffMyLawn's server-side machinery (that exists because a server owns nothing).
    //
    //   THE FLARES. Every game, including one that owns nothing near the edge, shows the barrier where something
    //   pushes on it, with no network traffic at all (Wonderland's RPC whitelist kicks a client that sends an
    //   RPC it does not know, and only admins may send this mod's requests). A push is read off the thing itself:
    //   outside the edge, within reach of it, and moving in. Character.GetVelocity is the owner's body velocity
    //   for a thing this game owns and the owner's SYNCED body velocity for any other - and the owner writes that
    //   from the velocity the AI asked for, before the hold cancels it, so a monster leaning on the barrier
    //   keeps reporting an inward velocity to every other game while its position sits still at the edge. Our
    //   own holds flare too, from the exact crossing.
    //
    // STOPPED, AND NOT. Hostile creatures by the mod's existing definition (Hostility, Rules/SpawnRules.cs:
    // MonsterAI, not the players' side) that are not tamed; the local player only with BarrierKeepsOutVisitors and
    // only if not a member and not an admin who bypasses. Passive animals, tamed creatures, summons and members
    // pass. Another player is never moved from here - their own game does that - but their pressing flares here;
    // whether they are an admin is not told to us, so a stranger flares only after pressing for
    // BarrierMath.RemotePlayerDwell, by which time an admin has walked through.
    //
    // NESTING (0.3.2). A barrier holds the ground its territory GOVERNS (BarrierMath's NESTING note): a town drawn inside a
    // barrier territory is a pocket the barrier does not reach into, so a visitor who logs in inside a public town is not
    // thrown to the kingdom's far edge, and an arena town inside a monster-free kingdom keeps its monsters. Each zone carries
    // the pockets it has, found when the index changes; every "is it inside?" below is Governs, not the ground's Contains.
    //
    // OUT OF SCOPE (a player who is not held here, and why): riding a tamed mount, aboard a ship, attached to
    // a chair or a bed, teleporting, in a cutscene - moving the body would strand or unseat them - and
    // projectiles, which are not characters. Anything not held still flares from a push if it is a character.
    //
    // COST. Nothing runs without a barrier territory in the index. With one, only barrier territories within
    // NearRange of the local player are looked at (refreshed four times a second), and each character in the
    // loaded area costs a position read and a few float compares unless it is within TrackMargin of a barrier's
    // edge; only those get a Track, hold a position and reach the real containment tests. Allocation happens once
    // per newly watched character, never per frame. Measured only by reasoning (no game here): a base with 100
    // characters loaded and two barrier territories nearby is ~200 bounding-box tests per frame.
    internal static class Barrier
    {
        private sealed class Zone
        {
            public Territory T;
            public TerritoryGround Ground;
            public TerritoryFlags Flags;
            public float MinX, MinZ, MaxX, MaxZ;

            /// <summary>The grounds of the territories that outrank this one and overlap it: where its barrier does not reach.</summary>
            public BarrierMath.Pocket[] Pockets;

            /// <summary>Is the point in the ground this barrier's territory governs?</summary>
            public bool Governs(Vector3 p) => BarrierMath.Governs(Ground, Pockets, p);
        }

        /// <summary>What is remembered about a character that is near a barrier's edge.</summary>
        private sealed class Track
        {
            public Vector3 Last;        // where it was last seen outside every barrier that stops it
            public bool HaveLast;
            public float LastAt;
            public int Seen;            // Time.frameCount it was last looked at: stale entries are dropped
            public float NextFlare;     // BarrierMath.FlareDue's clock
            public float Dwell;         // how long it has been pressing, for other players
            public bool Resolved;
            public bool Hostile;
        }

        /// <summary>Barrier territories within this of the local player are looked at; loaded characters are never further.</summary>
        private const float NearRange = 220f;
        private const float NearSeconds = 0.25f;
        private const float PruneSeconds = 2f;
        private const int StaleFrames = 180;
        private const float HoldMessageSeconds = 6f;
        private const float EvictStrength = 0.6f;

        private static readonly List<Zone> _zones = new List<Zone>();
        private static readonly List<Zone> _near = new List<Zone>();
        private static readonly Dictionary<int, Track> _tracks = new Dictionary<int, Track>();
        private static readonly List<int> _stale = new List<int>();
        private static Zone[] _reach = new Zone[4];
        private static Zone[] _stop = new Zone[4];

        private static int _revision = -1;
        private static float _nearTimer;
        private static float _pruneTimer;
        private static float _lastSay = -1000f;
        private static float _lastCosmeticError = -1000f;
        private static float _lastError = -1000f;
        private static bool _moved;

        // The local player's standing with the barrier, read once a frame.
        private static long _playerId;
        private static bool _isAdmin;
        private static bool _bypass;

        internal static void Tick(float dt)
        {
            if (TerritoryIndex.LocalRevision != _revision) Rebuild();
            if (_zones.Count == 0) return;

            // No body: between a death and the respawn, and before the first spawn. The creatures this game owns
            // keep their last positions until the new body exists; whatever walked in meanwhile is found inside
            // and taken out then.
            Player player = Player.m_localPlayer;
            if (player == null) return;

            _nearTimer -= dt;
            if (_nearTimer <= 0f)
            {
                _nearTimer = NearSeconds;
                RefreshNear(player.transform.position);
            }
            if (_near.Count == 0)
            {
                if (_tracks.Count > 0) _tracks.Clear();
                return;
            }

            _playerId = player.GetPlayerID();
            _isAdmin = TerritoryClient.IsAdmin;
            _bypass = TerritoryClient.AdminsBypass;

            float now = Time.time;
            int frame = Time.frameCount;
            List<Character> all = Character.GetAllCharacters();
            for (int i = 0; i < all.Count; i++)
            {
                Character c = all[i];
                if (c == null) continue;
                try
                {
                    Process(c, player, dt, now, frame);
                }
                catch (Exception ex)
                {
                    Failed(ex);
                }
            }

            if (_moved)
            {
                _moved = false;
                Physics.SyncTransforms();
            }

            _pruneTimer -= dt;
            if (_pruneTimer <= 0f)
            {
                _pruneTimer = PruneSeconds;
                Prune(frame);
            }
        }

        // ─────────────────────────────────────────────────────────────
        // WHICH BARRIERS
        // ─────────────────────────────────────────────────────────────

        private static void Rebuild()
        {
            _revision = TerritoryIndex.LocalRevision;
            _zones.Clear();
            _near.Clear();
            _tracks.Clear();
            _nearTimer = 0f;

            IReadOnlyList<Territory> all = TerritoryIndex.All;
            for (int i = 0; i < all.Count; i++)
            {
                Territory t = all[i];
                if (!BarrierMath.Active(t.Flags)) continue;

                var zone = new Zone { T = t, Ground = t.Ground, Flags = t.Flags };
                zone.Ground.Bounds(out zone.MinX, out zone.MinZ, out zone.MaxX, out zone.MaxZ);
                // TerritoryIndex.All is in precedence order, so every territory before this one outranks it.
                zone.Pockets = BarrierMath.PocketsFor(all, i, zone.MinX, zone.MinZ, zone.MaxX, zone.MaxZ);
                _zones.Add(zone);
            }

            if (_reach.Length < _zones.Count)
            {
                _reach = new Zone[_zones.Count];
                _stop = new Zone[_zones.Count];
            }
        }

        private static void RefreshNear(Vector3 at)
        {
            _near.Clear();
            for (int i = 0; i < _zones.Count; i++)
            {
                Zone z = _zones[i];
                if (at.x >= z.MinX - NearRange && at.x <= z.MaxX + NearRange && at.z >= z.MinZ - NearRange && at.z <= z.MaxZ + NearRange)
                    _near.Add(z);
            }
        }

        private static void Prune(int frame)
        {
            _stale.Clear();
            foreach (KeyValuePair<int, Track> pair in _tracks)
                if (frame - pair.Value.Seen > StaleFrames) _stale.Add(pair.Key);
            for (int i = 0; i < _stale.Count; i++) _tracks.Remove(_stale[i]);
        }

        // ─────────────────────────────────────────────────────────────
        // ONE CHARACTER
        // ─────────────────────────────────────────────────────────────

        private static void Process(Character c, Player local, float dt, float now, int frame)
        {
            Vector3 p = c.transform.position;
            // A crypt sits above y 3000 under the very ground a territory covers: never the barrier's business.
            if (Character.InInterior(p) || c.IsDead()) return;

            int reachCount = 0;
            for (int i = 0; i < _near.Count; i++)
            {
                Zone z = _near[i];
                float m = BarrierMath.TrackMargin;
                if (p.x < z.MinX - m || p.x > z.MaxX + m || p.z < z.MinZ - m || p.z > z.MaxZ + m) continue;
                if (BarrierMath.WithinReach(z.Ground, p, m)) _reach[reachCount++] = z;
            }
            if (reachCount == 0) return;

            bool isPlayer = c.IsPlayer();
            bool isLocal = isPlayer && c == local;
            Track t = GetTrack(c, frame);

            bool hostile = false;
            if (!isPlayer)
            {
                if (!t.Resolved && !Resolve(c, t)) return;
                hostile = t.Hostile;
            }
            bool tamed = !isPlayer && c.IsTamed();
            long id = isPlayer ? ((Player)c).GetPlayerID() : 0L;

            int stopCount = 0;
            Zone inside = null;
            for (int i = 0; i < reachCount; i++)
            {
                Zone z = _reach[i];
                bool member = isPlayer && z.T.IsMember(id);
                // Another player's admin status is not known here (the server tells each client only its own).
                if (!BarrierMath.Stops(z.Flags, isPlayer, tamed, hostile, member, isLocal && _isAdmin, isLocal && _bypass)) continue;
                _stop[stopCount++] = z;
                if (inside == null && z.Governs(p)) inside = z;
            }
            if (stopCount == 0)
            {
                t.HaveLast = false;
                t.Dwell = 0f;
                return;
            }

            if (inside == null)
            {
                t.Last = p;
                t.HaveLast = true;
                t.LastAt = now;
                Press(c, t, p, stopCount, isPlayer && !isLocal, dt, now);
                return;
            }

            t.Dwell = 0f;
            bool owned = isLocal || (c.m_nview != null && c.m_nview.IsValid() && c.m_nview.IsOwner());
            if (!owned) return;   // the game that simulates it holds it; here it only shows the flare (Press, from outside)

            if (isLocal && !Holdable(local))
            {
                t.HaveLast = false;
                return;
            }

            Enforce(c, t, inside, stopCount, isPlayer, isLocal, p, now);
        }

        private static Track GetTrack(Character c, int frame)
        {
            int key = c.GetInstanceID();
            if (!_tracks.TryGetValue(key, out Track t))
            {
                t = new Track();
                _tracks[key] = t;
            }
            t.Seen = frame;
            return t;
        }

        /// <summary>
        /// Is this prefab hostile? The prefab, not the instance: Hostility caches by object id, and instances come and
        /// go. False (and not yet decided) while the ZNetView is not ready.
        /// </summary>
        private static bool Resolve(Character c, Track t)
        {
            ZNetView view = c.m_nview;
            ZNetScene scene = ZNetScene.instance;
            if (view == null || !view.IsValid() || scene == null) return false;

            t.Hostile = Hostility.IsHostile(scene.GetPrefab(view.GetZDO().GetPrefab()));
            t.Resolved = true;
            return true;
        }

        /// <summary>The local player can be moved: not dead, teleporting, in a cutscene or a bed, seated, riding or on a ship (out of scope).</summary>
        private static bool Holdable(Player p)
        {
            if (p.IsDead() || p.IsTeleporting() || p.InCutscene() || p.InIntro() || p.InBed()) return false;
            if (p.IsAttached() || p.IsRiding() || p.IsAttachedToShip()) return false;
            return p.GetStandingOnShip() == null;
        }

        // ─────────────────────────────────────────────────────────────
        // THE HOLD
        // ─────────────────────────────────────────────────────────────

        private static void Enforce(Character c, Track t, Zone hit, int stopCount, bool isPlayer, bool isLocal, Vector3 p, float now)
        {
            HoldKind kind = BarrierMath.Decide(hit.Ground, hit.Pockets, t.HaveLast, t.Last, now - t.LastAt, p, BarrierMath.ExitMargin, out Vector3 to);
            if (kind == HoldKind.Free) return;

            // Wherever it goes must be outside every barrier that stops it, not only the one that caught it: two
            // barriers can touch or overlap. A few passes; each moves it out of one more.
            for (int pass = 0; pass < 3; pass++)
            {
                Zone other = null;
                for (int i = 0; i < stopCount; i++)
                    if (_stop[i].Governs(to)) { other = _stop[i]; break; }
                if (other == null) break;
                to = BarrierMath.NearestOutside(other.Ground, to, BarrierMath.ExitMargin);
                kind = HoldKind.Evict;
            }

            if (kind == HoldKind.Evict) to = Settle(to, c.IsFlying(), isPlayer);

            // The step that crossed the edge: from the place it goes back to, in to where it stands.
            BarrierMath.EdgeCrossing(hit.Ground, to, p, out Vector3 edge, out Vector3 outward);

            Rigidbody body = c.m_body;
            Vector3 velocity = body != null ? body.linearVelocity : Vector3.zero;
            float inward = BarrierMath.InwardSpeed(velocity, outward);

            if (body != null)
            {
                body.position = to;
                body.linearVelocity = BarrierMath.CancelInward(velocity, outward);
            }
            c.transform.position = to;
            _moved = true;   // Unity is told once, after the loop: a raid held at the wall is one sync, not twenty
            // Put down on new ground it should not be hurt by the drop between the two places (vanilla's own teleport does this).
            if (kind == HoldKind.Evict) c.m_maxAirAltitude = to.y;

            t.Last = to;
            t.HaveLast = true;
            t.LastAt = now;

            if (kind == HoldKind.Evict && TSConfig.Verbose.Value)
                Plugin.Debug($"barrier of '{hit.T.DisplayName}' moved {c.name} out: {p} -> {to}");

            float strength = kind == HoldKind.Hold ? Mathf.Max(BarrierMath.Strength(inward), 0.2f) : EvictStrength;
            if (BarrierMath.FlareDue(ref t.NextFlare, now)) Flare(c, edge, outward, strength);

            if (isLocal) Say(hit, kind == HoldKind.Evict, now);
        }

        /// <summary>Puts a thing moved out onto the ground it now stands over: a player onto the floor, a flyer no lower than 0.3 m up.</summary>
        private static Vector3 Settle(Vector3 to, bool flying, bool probeFloor)
        {
            float ground = GroundHeight(to);
            if (flying)
            {
                to.y = Mathf.Max(to.y, ground + 0.3f);
                return to;
            }

            // The floor at or below the ground, so a bridge or a raised floor outside the edge is stood on: probe
            // from above whichever is higher, the old height or the terrain.
            if (probeFloor && ZoneSystem.instance != null)
            {
                Vector3 probe = to;
                probe.y = Mathf.Max(to.y, ground) + 1f;
                if (ZoneSystem.instance.FindFloor(probe, out float floor))
                {
                    to.y = floor + 0.2f;
                    return to;
                }
            }
            to.y = Mathf.Max(to.y, ground + 0.3f);
            return to;
        }

        /// <summary>The terrain under a point: the loaded terrain's own answer where there is one, the world generator's otherwise (it needs no collider).</summary>
        private static float GroundHeight(Vector3 p)
        {
            ZoneSystem zones = ZoneSystem.instance;
            if (zones != null && zones.GetGroundHeight(p, out float h)) return h;
            WorldGenerator generator = WorldGenerator.instance;
            return generator != null ? generator.GetHeight(p.x, p.z) : p.y;
        }

        // ─────────────────────────────────────────────────────────────
        // THE FLARES
        // ─────────────────────────────────────────────────────────────

        /// <summary>A thing outside every barrier that stops it: is it pushing on the nearest one?</summary>
        private static void Press(Character c, Track t, Vector3 p, int stopCount, bool remotePlayer, float dt, float now)
        {
            Vector3 edge = p, outward = Vector3.zero;
            float distance = float.MaxValue;
            for (int i = 0; i < stopCount; i++)
            {
                if (!BarrierMath.ClosestEdge(_stop[i].Ground, p, out Vector3 e, out Vector3 o, out float d) || d >= distance) continue;
                edge = e;
                outward = o;
                distance = d;
            }
            if (distance == float.MaxValue) return;

            float inward = BarrierMath.InwardSpeed(c.GetVelocity(), outward);
            bool pressing = BarrierMath.Pressing(distance, BarrierMath.ReachFor(c.GetRadius()), inward);

            t.Dwell = BarrierMath.AdvanceDwell(t.Dwell, pressing, dt);
            if (!pressing) return;
            if (remotePlayer && t.Dwell < BarrierMath.RemotePlayerDwell) return;

            if (BarrierMath.FlareDue(ref t.NextFlare, now)) Flare(c, edge, outward, BarrierMath.Strength(inward));
        }

        /// <summary>Hands one flare to the visuals. Cosmetic (master pattern 12): whatever it does, the hold has already happened.</summary>
        private static void Flare(Character c, Vector3 edge, Vector3 outward, float strength)
        {
            if (!TSConfig.BarrierEffects.Value) return;
            try
            {
                // The contact point's y is the ground under the edge; how tall the thing is says how tall the flare is,
                // and a flyer's altitude counts, so the patch reaches up to it.
                float ground = GroundHeight(edge);
                float height = c.GetHeight();
                if (c.IsFlying()) height = Mathf.Min(height + Mathf.Max(0f, c.transform.position.y - ground), 40f);

                BarrierFlare.Flare(new Vector3(edge.x, ground, edge.z), outward, height, Mathf.Clamp01(strength));
            }
            catch (Exception ex)
            {
                CosmeticFailed(ex);
            }
        }

        /// <summary>The one line the local player reads: always when they were moved out, at most every few seconds when they were only held.</summary>
        private static void Say(Zone zone, bool always, float now)
        {
            if (!always && now - _lastSay < HoldMessageSeconds) return;
            _lastSay = now;
            try
            {
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, $"The runes of {zone.T.DisplayName} turn you back");
            }
            catch (Exception ex)
            {
                CosmeticFailed(ex);
            }
        }

        private static void CosmeticFailed(Exception ex)
        {
            if (Time.realtimeSinceStartup - _lastCosmeticError < 30f) return;
            _lastCosmeticError = Time.realtimeSinceStartup;
            Plugin.Log.LogError($"Runic barrier flare failed (cosmetic only, the barrier still holds; repeats suppressed for 30 s): {ex}");
        }

        private static void Failed(Exception ex)
        {
            if (Time.realtimeSinceStartup - _lastError < 30f) return;
            _lastError = Time.realtimeSinceStartup;
            Plugin.Log.LogError($"Runic barrier failed on one character (the rest are still held; repeats suppressed for 30 s): {ex}");
        }
    }
}
