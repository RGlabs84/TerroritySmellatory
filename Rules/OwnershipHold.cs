using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace TerroritySmellatory
{
    /// <summary>
    /// The one mechanism that lets the SERVER change something a client is simulating. Taken from Get Off My Lawn's
    /// OwnershipHold (verified live there, 211 corrections in one 5-minute window, no errors); the door closer
    /// (Rules/DoorCloser.cs) is its only user here.
    ///
    /// Nothing near a player is instantiated on a dedicated server (Game.FixedUpdate pins the reference position at
    /// (1e6, 0, 1e6)), so a door exists there only as a ZDO owned by some client. Writing to that ZDO naively goes
    /// wrong for two reasons, read from vanilla ZDOMan.RPC_ZDOData and ZSyncTransform:
    ///
    ///  1. REVISION RACE. A receiver discards an incoming update whose DataRevision is not above its own, and applies
    ///     everything - owner, data, position - unconditionally when it is. The server's copy of an object a client owns
    ///     lags that client by at least one send interval, so a plain server write arrives stale and is dropped; worse,
    ///     the owner's in-flight packets (still saying "I own this", with a higher revision) then reach the server AFTER
    ///     its claim and revert the owner field there, while the client has already accepted the claim and stopped
    ///     simulating. Fix: after claiming, jump the server copy's DataRevision by RevisionJump. Every in-flight client
    ///     packet is then below it and is ignored, and every peer accepts the server's write. Revisions are only ever
    ///     compared for ordering, never saved, so the jump costs nothing.
    ///
    ///  2. THE OWNER MUST SEE A NON-OWNER FRAME. An owner re-reads its object from the ZDO only on the frame it BECOMES
    ///     owner again; a hand-back in the same packet as the claim is invisible to it. So ownership is held for
    ///     HandBackSeconds and returned in a later packet. Vanilla's ReleaseNearbyZDOS sweep (every 2 s) would otherwise
    ///     sometimes hand it back within the same send tick - the server's reference position is never inside any
    ///     active area, so it treats a server-owned ZDO as abandoned - which is what HeldOwnerPatch blocks.
    ///
    /// For a door the point is the first trap: the client-side Door re-reads its state whenever the ZDO's DataRevision
    /// moves (Door.UpdateState), so a server write of 0 closes it everywhere, but only if that write cannot lose a race
    /// against a player using the door at the same moment. The hold timing is kept exactly as tuned live in Get Off My
    /// Lawn. What a player sees: a Use press during the hold is routed to the owner - the server, which has no Door
    /// instance - and is dropped; press again.
    ///
    /// Held ZDOs are tracked by ZDOID and looked up through ZDOMan every time: ZDO objects are pooled, and a destroyed
    /// one can be recycled for a different object within a frame.
    /// </summary>
    internal static class OwnershipHold
    {
        private struct Hold
        {
            public long PreviousOwner;
            public float Due;
        }

        /// <summary>
        /// Comfortably above anything the owning client can have in flight (a moving owner adds roughly 100-150
        /// revisions a second, so this is ~30 s of backlog; a door's owner adds almost none). A uint wraps after 2^32,
        /// about a million jumps on one object: RevisionCeiling stops jumping well before that.
        /// </summary>
        internal const uint RevisionJump = 4096u;
        private const uint RevisionCeiling = uint.MaxValue - 1000000u;

        private static readonly Dictionary<ZDOID, Hold> _holds = new Dictionary<ZDOID, Hold>();
        private static readonly List<ZDOID> _due = new List<ZDOID>();
        private static bool _releasing;
        private static bool _warnedCeiling;

        internal static bool IsHeld(ZDOID id) => _holds.ContainsKey(id);

        internal static void Clear() => _holds.Clear();

        /// <summary>
        /// Take a client-owned (or unowned) ZDO for this server session and make the server's copy authoritative. Call
        /// only when <c>!zdo.IsOwner()</c>; the caller then writes what it wants, and Update hands ownership back after
        /// <paramref name="handBackSeconds"/>.
        /// </summary>
        internal static void Claim(ZDO zdo, float now, float handBackSeconds)
        {
            long previous = zdo.GetOwner();
            zdo.SetOwner(ZDOMan.GetSessionID()); // OwnerRevision+1: every peer applies an owner change even when it rejects the data
            if (zdo.DataRevision < RevisionCeiling)
            {
                zdo.DataRevision += RevisionJump;
            }
            else if (!_warnedCeiling)
            {
                _warnedCeiling = true;
                Plugin.Log.LogWarning($"ZDO {zdo.m_uid} has DataRevision {zdo.DataRevision}, too close to the uint ceiling to jump safely - closing it may lose the race against its owner until the world is reloaded.");
            }
            if (!_holds.ContainsKey(zdo.m_uid))
                _holds[zdo.m_uid] = new Hold { PreviousOwner = previous, Due = now + handBackSeconds };
        }

        /// <summary>Hands back every hold whose time is up. Runs every frame, before any scan.</summary>
        internal static void Update(float now)
        {
            if (_holds.Count == 0) return;

            _due.Clear();
            foreach (KeyValuePair<ZDOID, Hold> pair in _holds)
                if (pair.Value.Due <= now) _due.Add(pair.Key);
            if (_due.Count == 0) return;

            ZDOMan man = ZDOMan.instance;
            foreach (ZDOID id in _due)
            {
                Hold hold = _holds[id];
                _holds.Remove(id);
                ZDO zdo = man?.GetZDO(id);
                if (zdo == null || !zdo.IsValid() || !zdo.IsOwner()) continue; // destroyed meanwhile, or taken by someone else: nothing to hand back

                long target = HandBackTarget(hold.PreviousOwner, zdo.GetPosition());
                _releasing = true;
                try
                {
                    zdo.SetOwner(target);
                }
                finally
                {
                    _releasing = false;
                }
            }
        }

        /// <summary>
        /// The previous owner if it is still connected and still simulating that spot, else the nearest connected peer
        /// that is, else 0 (unowned - the ReleaseNearbyZDOS sweep gives it to whoever is nearest within 2 s, which is
        /// also what happens to anything a player walks away from).
        /// </summary>
        private static long HandBackTarget(long previous, Vector3 position)
        {
            ZNet net = ZNet.instance;
            if (net == null) return 0L;

            if (previous != 0L)
            {
                ZNetPeer prev = net.GetPeer(previous);
                if (prev != null && prev.IsReady() && ZNetScene.InActiveArea(position, prev.GetRefPos())) return previous;
            }

            long best = 0L;
            float bestDistance = float.MaxValue;
            foreach (ZNetPeer peer in net.GetPeers())
            {
                if (peer == null || !peer.IsReady()) continue;
                Vector3 refPos = peer.GetRefPos();
                if (!ZNetScene.InActiveArea(position, refPos)) continue;
                float distance = (refPos - position).sqrMagnitude;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = peer.m_uid;
                }
            }
            return best;
        }

        /// <summary>
        /// Should <see cref="HeldOwnerPatch"/> refuse this owner change? While a ZDO is held nothing but this class may
        /// move its ownership away from the server. The only other server-side caller of ZDO.SetOwner is
        /// ZDOMan.ReleaseNearbyZDOS; incoming client packets go through SetOwnerInternal and are ordered out by the
        /// revision jump instead. Setting the owner TO the server session is always allowed (repeat claims).
        /// </summary>
        internal static bool Refuses(ZDO zdo, long newOwner)
        {
            if (_holds.Count == 0 || _releasing || ZDOMan.instance == null) return false;
            if (newOwner == ZDOMan.GetSessionID()) return false;
            return _holds.ContainsKey(zdo.m_uid);
        }
    }

    /// <summary>
    /// The hold's one Harmony target. On a game that is not the server, or with nothing held (nearly always), the
    /// prefix is one integer compare.
    /// </summary>
    [HarmonyPatch(typeof(ZDO), nameof(ZDO.SetOwner))]
    internal static class HeldOwnerPatch
    {
        private static bool Prefix(ZDO __instance, long uid) => !OwnershipHold.Refuses(__instance, uid);
    }
}
