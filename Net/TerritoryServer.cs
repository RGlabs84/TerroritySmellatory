using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace TerroritySmellatory
{
    /// <summary>
    /// The authority: validates admin requests, saves, and sends the territory list to everyone.
    /// Runs on the dedicated server, on a listen host, and in single player (all three are IsServer).
    /// </summary>
    internal static class TerritoryServer
    {
        private sealed class PeerState
        {
            public int SentRevision = -1;
            public bool SentAdmin;

            /// <summary>A list that is going out in pieces (SnapshotParts), and when its last piece went; null when nothing is.</summary>
            public SnapshotParts.Sender Outgoing;
            public float OutgoingAt;
        }

        private static readonly Dictionary<long, PeerState> _peers = new Dictionary<long, PeerState>();

        /// <summary>Bumped on every change; a peer is sent the list until it has been sent this revision.</summary>
        private static int _revision;

        private static float _syncTimer;
        private static int _rosterStamp;
        private static bool _syncNow;

        /// <summary>The lists this session that were too big for one message and went in pieces (the end-to-end rig reads it).</summary>
        internal static int PartedSends { get; private set; }

        /// <summary>The ids of those lists: a new list is told apart from one it replaces.</summary>
        private static int _transferId;

        /// <summary>A list that makes no progress for this long - a piece that finds no room in the socket - is given up and sent again at the next sync.</summary>
        private const float PartsTimeoutSeconds = 30f;

        private static bool _partsTold;

        /// <summary>
        /// A piece goes into a player's socket only while it holds less than this: two pieces' worth, and never under 16 KiB - the game's
        /// own ZDO sender keeps about 10 KB queued while it has anything to send, and a limit under that would leave the pieces waiting
        /// behind it for as long as the world is still being sent (the rig's tiny pieces would, a real 64 KiB piece never does).
        /// </summary>
        private static int QueueLimit => Math.Max(2 * Protocol.PartBytes, 16 * 1024);

        internal static bool Active => ZNet.instance != null && ZNet.instance.IsServer();

        internal static void Reset()
        {
            _peers.Clear();
            _syncTimer = 0f;
            _rosterStamp = 0;
            _syncNow = false;
            PartedSends = 0;
        }

        internal static void Tick(float dt)
        {
            if (!Active)
            {
                if (_peers.Count > 0) _peers.Clear();
                return;
            }

            if (TerritoryStore.EnsureLoaded())
            {
                _revision++;
                PublishLocal();
            }

            PumpParts();

            _syncTimer += dt;
            if (!_syncNow && _syncTimer < 1f) return;
            _syncTimer = 0f;
            _syncNow = false;
            SyncPeers();
        }

        // ─────────────────────────────────────────────────────────────
        // SENDING
        // ─────────────────────────────────────────────────────────────

        private static void SyncPeers()
        {
            List<ZNetPeer> peers = ZNet.instance.GetPeers();

            // The roster only matters to admins (their panel's member picker); a join or leave resends to
            // admins alone, and everyone else is sent only what changed for them - the territories.
            int stamp = RosterStamp(peers);
            bool rosterChanged = stamp != _rosterStamp;
            _rosterStamp = stamp;
            List<TerritoryMember> roster = null;

            var seen = new HashSet<long>();
            foreach (ZNetPeer peer in peers)
            {
                // Wait for the character: a peer is "ready" long before it is in the world, and Better
                // Continents holds a joining client in its own download before vanilla's handshake even
                // finishes. The character ZDO is the one signal that the client is fully in.
                if (peer == null || peer.m_rpc == null || !peer.IsReady() || peer.m_characterID.IsNone()) continue;
                seen.Add(peer.m_uid);

                bool admin = IsAdmin(peer);
                if (!_peers.TryGetValue(peer.m_uid, out PeerState state))
                {
                    state = new PeerState();
                    _peers[peer.m_uid] = state;
                }

                bool due = state.SentRevision != _revision || state.SentAdmin != admin || (admin && rosterChanged);
                if (!due) continue;

                if (admin && roster == null) roster = OnlinePlayers();
                Send(peer, state, admin, admin ? roster : null);
                state.SentRevision = _revision;
                state.SentAdmin = admin;
            }

            foreach (long uid in _peers.Keys.Where(k => !seen.Contains(k)).ToList())
                _peers.Remove(uid);

            if (rosterChanged && Player.m_localPlayer != null)
                TerritoryClient.SetOnlinePlayers(roster ?? OnlinePlayers());
        }

        private static void Send(ZNetPeer peer, PeerState state, bool admin, List<TerritoryMember> roster)
        {
            try
            {
                // The STORED records: a sub-territory carries only what it sets itself, and the game that receives them fills in
                // what each follows with the same Inheritance.Resolve the server runs (TerritoryClient.OnSnapshot). Sending the
                // effective records instead would copy a continent's member list into every town that follows it.
                IReadOnlyList<Territory> stored = TerritoryStore.Territories;
                ZPackage pkg = Protocol.WriteSnapshot(TerritoryStore.WorldUid, _revision, admin,
                    TSConfig.AdminsBypassProtection.Value, stored, roster);
                byte[] bytes = pkg.GetArray();

                if (bytes.Length <= Protocol.PartBytes)
                {
                    state.Outgoing = null;   // a list still going out in pieces is out of date now: the player gets this one whole
                    peer.m_rpc.Invoke(Protocol.RpcSnapshot, pkg);
                    Plugin.Debug($"sent {stored.Count} territories (rev {_revision}, admin {admin}, {bytes.Length} bytes) to {peer.m_playerName}");
                    return;
                }

                // Too big for one message: in pieces, one a frame (PumpParts). A list still going out is replaced; the player drops
                // the part-gathered one when the new list's first piece arrives (SnapshotParts.Assembler).
                state.Outgoing = new SnapshotParts.Sender(++_transferId, bytes);
                state.OutgoingAt = Time.realtimeSinceStartup;
                PartedSends++;
                int pieces = SnapshotParts.PartCount(bytes.Length, Protocol.PartBytes);
                if (!_partsTold)
                {
                    _partsTold = true;
                    Plugin.Log.LogInfo($"The territory list is {Size(bytes.Length)}: it goes to each player in {pieces} pieces of at most {Size(Protocol.PartBytes)} " +
                                       "(Steam refuses a message over 512 KiB), one piece a frame.");
                }
                Plugin.Debug($"sending {stored.Count} territories (rev {_revision}, admin {admin}, {bytes.Length} bytes in {pieces} pieces) to {peer.m_playerName}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Sending territories to {peer.m_playerName} failed: {ex}");
            }
        }

        private static string Size(int bytes) => bytes >= 1024 ? $"{bytes / 1024} KiB" : $"{bytes} bytes";

        /// <summary>
        /// Every frame: the next piece of each list that is going out in pieces, one a frame per player, while the player's socket holds
        /// less than two pieces' worth (so a big list never fills the queue in front of the other traffic) - the pace Better
        /// Continents' BakedTransfer keeps for a world's maps. A list that has found no room for 30 s is given up and sent again whole at
        /// the next sync; a player who has left takes his list with him.
        /// </summary>
        private static void PumpParts()
        {
            bool any = false;
            foreach (PeerState s in _peers.Values)
                if (s.Outgoing != null) { any = true; break; }
            if (!any) return;

            float now = Time.realtimeSinceStartup;
            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
            {
                if (peer?.m_rpc == null || !_peers.TryGetValue(peer.m_uid, out PeerState state) || state.Outgoing == null) continue;
                try
                {
                    if (!peer.m_rpc.IsConnected()) { state.Outgoing = null; continue; }

                    if (peer.m_rpc.GetSocket().GetSendQueueSize() >= QueueLimit)
                    {
                        if (now - state.OutgoingAt > PartsTimeoutSeconds)
                        {
                            Plugin.Log.LogWarning($"The territory list found no room in {peer.m_playerName}'s connection for {PartsTimeoutSeconds:0} s; it is sent again at the next sync.");
                            state.Outgoing = null;
                            state.SentRevision = -1;
                            _syncNow = true;
                        }
                        continue;
                    }

                    SnapshotParts.Sender send = state.Outgoing;
                    peer.m_rpc.Invoke(Protocol.RpcSnapshotPart, send.Next(Protocol.PartBytes));
                    state.OutgoingAt = now;
                    if (send.Done)
                    {
                        state.Outgoing = null;
                        Plugin.Debug($"sent all {SnapshotParts.PartCount(send.Total, Protocol.PartBytes)} pieces of list {send.Id} to {peer.m_playerName}");
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"Sending a piece of the territory list to {peer.m_playerName} failed: {ex}");
                    state.Outgoing = null;
                    state.SentRevision = -1;   // sent whole again at the next sync
                }
            }
        }

        /// <summary>This process's own view: the server-side rules, and on a listen host the host's own client.</summary>
        private static void PublishLocal()
        {
            // The effective records, cloned, so nothing on the client side of a listen host can edit them in place.
            TerritoryIndex.Set(TerritoryStore.Effective.Select(t => t.Clone()));
            if (!IsDedicatedServer())
                TerritoryClient.ApplyLocalHost(_revision, OnlinePlayers());
        }

        private static void Changed()
        {
            _revision++;
            PublishLocal();
            _syncNow = true;
        }

        // ─────────────────────────────────────────────────────────────
        // REQUESTS
        // ─────────────────────────────────────────────────────────────

        /// <summary>A request that arrived on a client's connection. The connection IS the identity.</summary>
        internal static void OnRequest(ZNetPeer peer, ZPackage pkg)
        {
            if (!Active || peer == null) return;
            Handle(peer, pkg);
        }

        /// <summary>A request from this machine's own player - the host of a listen server, or single player.</summary>
        internal static void OnLocalRequest(ZPackage pkg)
        {
            if (!Active) return;
            // Read as the wire would deliver it. A ZPackage reads and writes through ONE stream position, so the
            // package TerritoryClient.Send has just written sits at its END, and reading it in place throws on the
            // first int: every request a listen host or single player made was dropped as "unreadable", with no
            // reply (0.1.x; only ever played on a dedicated server). ZRpc hands the server a fresh package over the
            // bytes, positioned at 0 - and so does this.
            Handle(null, new ZPackage(pkg.GetArray()));
        }

        private static void Handle(ZNetPeer peer, ZPackage pkg)
        {
            Protocol.Request req;
            try
            {
                req = Protocol.ReadRequest(pkg);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Unreadable territory request from {Who(peer)}: {ex.Message}");
                return;
            }
            if (req == null) return;

            // The host of a listen server owns the world; everyone else is judged by the server's own
            // adminlist.txt against the socket the request came down (PORTAL-ZDO-AND-DEATH-FACTS §9).
            // Server Devcommands' permissions.yaml admin overrides flow through the same IsAdmin call.
            bool admin = peer == null || IsAdmin(peer);
            string who = Who(peer);

            if (!admin)
            {
                Plugin.Log.LogWarning($"Refused territory {req.Op} from {who}: not an admin.");
                Reply(peer, req.RequestId, false, "Only server admins can change territories.", 0);
                return;
            }

            if (req.Op != RequestOp.Refresh && req.Op != RequestOp.Reload && !TerritoryStore.Writable)
            {
                Reply(peer, req.RequestId, false, TerritoryStore.ReadOnlyReason, 0);
                return;
            }

            try
            {
                switch (req.Op)
                {
                    case RequestOp.Create: Create(peer, req, who); break;
                    case RequestOp.Update: Update(peer, req, who); break;
                    case RequestOp.Reshape: Reshape(peer, req, who); break;
                    case RequestOp.Delete: Delete(peer, req, who); break;
                    case RequestOp.Reload: Reload(peer, req, who); break;
                    case RequestOp.Refresh: Refresh(peer, req); break;
                    case RequestOp.Combine: Combine(peer, req, who); break;
                    case RequestOp.AddPart: AddPart(peer, req, who); break;
                    case RequestOp.RemovePart: RemovePart(peer, req, who); break;
                    case RequestOp.SplitPart: SplitPart(peer, req, who); break;
                    case RequestOp.DeleteTree: DeleteTree(peer, req, who); break;
                    default: Reply(peer, req.RequestId, false, $"Unknown request {(int)req.Op} - is the server running an older TerroritySmellatory?", 0); break;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Territory {req.Op} from {who} failed: {ex}");
                Reply(peer, req.RequestId, false, $"The server hit an error: {ex.Message}", 0);
            }
        }

        private static void Create(ZNetPeer peer, Protocol.Request req, string who)
        {
            Territory t = req.Record;
            if (t == null) { Reply(peer, req.RequestId, false, "The request carried no territory.", 0); return; }

            if (TerritoryStore.Territories.Count >= TSConfig.MaxTerritories.Value)
            {
                Reply(peer, req.RequestId, false, $"This world already has the maximum of {TSConfig.MaxTerritories.Value} territories.", 0);
                return;
            }
            // The hammer marks one part, but the record can carry more, and MaxParts must hold on every path in,
            // not only on combining and adding. Whatever it carries is new ground, clamped to today's limit below.
            if (t.PartCount > 1 && !PartsAllowed(peer, req)) return;
            if (t.PartCount > TSConfig.MaxParts.Value)
            {
                Reply(peer, req.RequestId, false, $"A territory may have at most {TSConfig.MaxParts.Value} parts (server setting MaxParts); this one has {t.PartCount}.", 0);
                return;
            }
            // Where it sits (AutoParent: inside whatever governs its middle), whether that is allowed, the sanitising, and what
            // it follows put back to the defaults - all in Core/TerritoryEdits.cs, where the harness checks it.
            string refused = TerritoryEdits.PrepareNew(TerritoryStore.Territories, TerritoryIndex.All, t, TSConfig.MaxHalfExtent.Value);
            if (refused != null)
            {
                Reply(peer, req.RequestId, false, refused, 0);
                return;
            }

            t.CreatedBy = who;
            t.CreatedTicks = DateTime.UtcNow.Ticks;
            t.Revision = 1;
            TerritoryStore.Add(t);
            if (string.IsNullOrEmpty(t.Name)) t.Name = $"Territory {t.Id}";

            if (!Commit(peer, req, out string error)) { TerritoryStore.Remove(t.Id); Reply(peer, req.RequestId, false, error, 0); return; }

            string inside = t.ParentId > 0 ? $", inside #{t.ParentId} '{TerritoryStore.Get(t.ParentId)?.DisplayName}'" : "";
            Plugin.Log.LogInfo($"{who} created territory #{t.Id} '{t.Name}' ({t.Ground.Describe()}) at {Pos(t)}{inside}.");
            Reply(peer, req.RequestId, true, $"Created #{t.Id} '{t.Name}' ({t.Ground.Describe()}){inside}.", t.Id);
        }

        private static void Update(ZNetPeer peer, Protocol.Request req, string who)
        {
            Territory incoming = req.Record;
            Territory existing = TerritoryStore.Get(req.TargetId);
            if (incoming == null || existing == null) { Reply(peer, req.RequestId, false, $"There is no territory #{req.TargetId}.", 0); return; }

            // An edit of the rules must not shrink the ground, even if MaxRadius was lowered since placement - any part of it,
            // not only the first; and a new parent must not make a loop or go too deep (TerritoryEdits.Update).
            string refused = TerritoryEdits.Update(TerritoryStore.Territories, existing, incoming, TSConfig.MaxHalfExtent.Value, out Territory updated);
            if (refused != null)
            {
                Reply(peer, req.RequestId, false, refused, 0);
                return;
            }

            TerritoryStore.Replace(updated);
            if (!Commit(peer, req, out string error)) { TerritoryStore.Replace(existing); Reply(peer, req.RequestId, false, error, 0); return; }

            Plugin.Log.LogInfo($"{who} updated territory #{updated.Id} '{updated.Name}'.");
            Reply(peer, req.RequestId, true, $"Saved #{updated.Id} '{updated.Name}'.", updated.Id);
        }

        private static void Reshape(ZNetPeer peer, Protocol.Request req, string who)
        {
            Territory incoming = req.Record;
            Territory existing = TerritoryStore.Get(req.TargetId);
            if (incoming == null || existing == null) { Reply(peer, req.RequestId, false, $"There is no territory #{req.TargetId}.", 0); return; }
            if (!Current(peer, req, existing)) return;

            // The record's FIRST part is the new ground for part req.Part - 0 unless a protocol 2 sender names
            // another, so a 0.1.x-style reshape still moves a one-part territory whole. It is new ground, so it is
            // clamped to today's MaxRadius before it joins; the parts it leaves alone keep the size they have.
            if (!Territory.SanitizePart(incoming.Shape, TSConfig.MaxHalfExtent.Value, out TerritoryShape part))
            {
                Reply(peer, req.RequestId, false, "The new position or size was not a number.", 0);
                return;
            }

            Territory updated = existing.Clone();
            string refused = TerritoryEdits.ReshapePart(updated, req.Part, part);
            if (refused != null) { Reply(peer, req.RequestId, false, refused, 0); return; }
            // The territory's height is its first part's, and moving that part takes the new one, as 0.1.x did.
            if (req.Part == 0) updated.Y = incoming.Y;
            if (!updated.Sanitize(KeepLimit(updated)))
            {
                Reply(peer, req.RequestId, false, "The new position or size was not a number.", 0);
                return;
            }
            updated.Revision = existing.Revision + 1;

            TerritoryStore.Replace(updated);
            if (!Commit(peer, req, out string error)) { TerritoryStore.Replace(existing); Reply(peer, req.RequestId, false, error, 0); return; }

            if (updated.PartCount == 1)
            {
                Plugin.Log.LogInfo($"{who} reshaped territory #{updated.Id} '{updated.Name}' to {updated.Ground.Describe()} at {Pos(updated)}.");
                Reply(peer, req.RequestId, true, $"Moved #{updated.Id} '{updated.Name}' ({updated.Ground.Describe()}).", updated.Id);
                return;
            }
            TerritoryShape moved = updated.GetPart(req.Part);
            Plugin.Log.LogInfo($"{who} reshaped part {req.Part + 1} of territory #{updated.Id} '{updated.Name}' to {moved.Describe()} at {Pos(moved)}.");
            Reply(peer, req.RequestId, true, $"Moved part {req.Part + 1} of #{updated.Id} '{updated.Name}' ({moved.Describe()}).", updated.Id);
        }

        private static void Delete(ZNetPeer peer, Protocol.Request req, string who)
        {
            Territory existing = TerritoryStore.Get(req.TargetId);
            if (existing == null) { Reply(peer, req.RequestId, false, $"There is no territory #{req.TargetId}.", 0); return; }

            // Removing a territory never changes how the ones inside it behave: each keeps, as its own, everything it was
            // following, and moves up to the removed territory's parent (Inheritance.ReleaseChildren).
            List<Territory> freed = Inheritance.ReleaseChildren(TerritoryStore.Territories, TerritoryStore.Effective, existing.Id, existing.ParentId);
            var originals = new List<Territory>(freed.Count);
            foreach (Territory copy in freed)
            {
                originals.Add(TerritoryStore.Get(copy.Id));
                if (!copy.Sanitize(KeepLimit(copy)))
                {
                    Reply(peer, req.RequestId, false, $"'{copy.DisplayName}' (inside it) did not validate.", 0);
                    return;
                }
            }

            int at = TerritoryStore.IndexOf(existing.Id);
            TerritoryStore.Remove(existing.Id);
            foreach (Territory copy in freed) TerritoryStore.Replace(copy);
            if (!Commit(peer, req, out string error))
            {
                TerritoryStore.Restore(existing, at);
                foreach (Territory original in originals) TerritoryStore.Replace(original);
                Reply(peer, req.RequestId, false, error, 0);
                return;
            }

            string now = existing.ParentId > 0 ? "inside #" + existing.ParentId : "at the top level";
            string kept = freed.Count == 0 ? ""
                : freed.Count == 1 ? $" Its sub-territory keeps the rules it had and now sits {now}."
                : $" Its {freed.Count} sub-territories keep the rules they had and now sit {now}.";
            Plugin.Log.LogInfo($"{who} deleted territory #{existing.Id} '{existing.Name}'.{kept}");
            Reply(peer, req.RequestId, true, $"Deleted #{existing.Id} '{existing.Name}'.{kept}", existing.Id);
        }

        /// <summary>A territory and everything inside it, at any depth, in one save.</summary>
        private static void DeleteTree(ZNetPeer peer, Protocol.Request req, string who)
        {
            Territory existing = TerritoryStore.Get(req.TargetId);
            if (existing == null) { Reply(peer, req.RequestId, false, $"There is no territory #{req.TargetId}.", 0); return; }

            var gone = new List<Territory> { existing };
            gone.AddRange(Inheritance.Descendants(TerritoryStore.Territories, existing.Id));

            // Where each one sits, so a save that fails puts the list back in its old order.
            var places = new List<KeyValuePair<int, Territory>>(gone.Count);
            foreach (Territory t in gone) places.Add(new KeyValuePair<int, Territory>(TerritoryStore.IndexOf(t.Id), t));
            foreach (Territory t in gone) TerritoryStore.Remove(t.Id);

            if (!Commit(peer, req, out string error))
            {
                places.Sort((a, b) => a.Key.CompareTo(b.Key));
                foreach (KeyValuePair<int, Territory> place in places) TerritoryStore.Restore(place.Value, place.Key);
                Reply(peer, req.RequestId, false, error, 0);
                return;
            }

            string inside = gone.Count == 1 ? "" : $" and the {Count(gone.Count - 1, "territory", "territories")} inside it";
            Plugin.Log.LogInfo($"{who} deleted territory #{existing.Id} '{existing.Name}'{inside}.");
            Reply(peer, req.RequestId, true, $"Deleted #{existing.Id} '{existing.Name}'{inside}.", existing.Id);
        }

        private static void Reload(ZNetPeer peer, Protocol.Request req, string who)
        {
            // A file that fails to read changes nothing but the store's read-only flag - the list in force stays
            // (TerritoryStore.Load) - so there is nothing to send, and no client rebuilds its map for the same list.
            if (TerritoryStore.Reload()) Changed();
            string msg = TerritoryStore.Writable
                ? $"Reloaded {TerritoryStore.Territories.Count} territories from disk."
                : TerritoryStore.ReadOnlyReason;
            Plugin.Log.LogInfo($"{who} reloaded territories: {msg}");
            Reply(peer, req.RequestId, TerritoryStore.Writable, msg, 0);
        }

        private static void Refresh(ZNetPeer peer, Protocol.Request req)
        {
            if (peer != null && _peers.TryGetValue(peer.m_uid, out PeerState state)) state.SentRevision = -1;
            _syncNow = true;
            if (peer == null) PublishLocal();
            Reply(peer, req.RequestId, true, "Territory list refreshed.", 0);
        }

        // ─────────────────────────────────────────────────────────────
        // SEVERAL PARTS (0.2.0) - the edits themselves are Core/TerritoryEdits.cs, pure and harness-tested; what
        // is here is the same clone -> edit -> sanitise -> store -> save -> roll back as every handler above, and
        // for the two that touch two territories, both in ONE save and one snapshot, and both put back on a
        // failed save.
        // ─────────────────────────────────────────────────────────────

        /// <summary>TargetId takes OtherId's ground, owner and members, keeping its own name and rules; OtherId is deleted.</summary>
        private static void Combine(ZNetPeer peer, Protocol.Request req, string who)
        {
            if (!PartsAllowed(peer, req)) return;
            Territory target = TerritoryStore.Get(req.TargetId);
            if (target == null) { Reply(peer, req.RequestId, false, $"There is no territory #{req.TargetId}.", 0); return; }
            Territory other = TerritoryStore.Get(req.OtherId);
            if (other == null) { Reply(peer, req.RequestId, false, $"There is no territory #{req.OtherId}.", 0); return; }

            // A territory and one inside it cannot be combined: the sub-territory's ground would land in its own parent.
            string refused = TerritoryEdits.CombineRefusal(TerritoryStore.Territories, target, other);
            if (refused != null) { Reply(peer, req.RequestId, false, refused, 0); return; }

            // Who joins is who EFFECTIVELY belonged to the other (a sub-territory's people may be its parent's), and a target
            // that follows its parent's people sets its own list first - only if somebody joins - so the new members are kept
            // and not followed away (TerritoryEdits.CombineStored).
            Territory otherNow = EffectiveOf(other.Id) ?? other;
            Territory combined = target.Clone();

            // Only the target is cloned: Combine reads the other and copies what it takes, and refuses - the same
            // territory twice, too many parts, too many members - before it changes anything.
            refused = TerritoryEdits.CombineStored(combined, EffectiveOf(target.Id) ?? target, otherNow, TSConfig.MaxParts.Value, out int joined);
            if (refused != null) { Reply(peer, req.RequestId, false, refused, 0); return; }
            if (!combined.Sanitize(KeepLimit(combined)))
            {
                Reply(peer, req.RequestId, false, "The combined territory did not validate.", 0);
                return;
            }
            combined.Revision = target.Revision + 1;

            // The other's sub-territories keep what they do and move under the target (Inheritance.ReleaseChildren).
            List<Territory> freed = Inheritance.ReleaseChildren(TerritoryStore.Territories, TerritoryStore.Effective, other.Id, target.Id);
            var originals = new List<Territory>(freed.Count);
            foreach (Territory copy in freed)
            {
                originals.Add(TerritoryStore.Get(copy.Id));
                if (!copy.Sanitize(KeepLimit(copy)))
                {
                    Reply(peer, req.RequestId, false, $"'{copy.DisplayName}' (inside '{other.DisplayName}') did not validate.", 0);
                    return;
                }
            }

            // The other's sub-territories now hang one place lower or higher than they did: the whole tree must still be allowed.
            var after = new List<Territory>(TerritoryStore.Territories.Count);
            foreach (Territory t in TerritoryStore.Territories)
            {
                if (t.Id == other.Id) continue;
                Territory moved = t.Id == combined.Id ? combined : freed.Find(f => f.Id == t.Id);
                after.Add(moved ?? t);
            }
            string tooDeep = Inheritance.Validate(after);
            if (tooDeep != null) { Reply(peer, req.RequestId, false, $"That would break the nesting: {tooDeep}.", 0); return; }

            // One save for both, so there is never a file - or a snapshot on anybody's screen - with the other's
            // ground in two territories or in none. Its place in the list is kept for the undo.
            int otherAt = TerritoryStore.IndexOf(other.Id);
            TerritoryStore.Replace(combined);
            TerritoryStore.Remove(other.Id);
            foreach (Territory copy in freed) TerritoryStore.Replace(copy);
            if (!Commit(peer, req, out string error))
            {
                TerritoryStore.Replace(target);
                TerritoryStore.Restore(other, otherAt);
                foreach (Territory original in originals) TerritoryStore.Replace(original);
                Reply(peer, req.RequestId, false, error, 0);
                return;
            }

            string members = Count(joined, "new member", "new members");
            Plugin.Log.LogInfo($"{who} combined territory #{other.Id} '{other.Name}' into #{combined.Id} '{combined.Name}': {combined.Ground.Describe()}, {members}.");
            Reply(peer, req.RequestId, true, $"Combined '{other.Name}' (#{other.Id}) into '{combined.Name}' (#{combined.Id}): {combined.PartCount} parts, {members}.", combined.Id);
        }

        /// <summary>The record's ground - its first part, as the hammer marked it - becomes a new part of TargetId.</summary>
        private static void AddPart(ZNetPeer peer, Protocol.Request req, string who)
        {
            if (!PartsAllowed(peer, req)) return;
            Territory existing = TerritoryStore.Get(req.TargetId);
            if (existing == null) { Reply(peer, req.RequestId, false, $"There is no territory #{req.TargetId}.", 0); return; }
            if (req.Record == null) { Reply(peer, req.RequestId, false, "The request carried no part.", 0); return; }

            // New ground, so held to today's MaxRadius as a new territory is - and before it joins, so the limit the
            // whole is sanitised with below can only ever protect the parts already there.
            if (!Territory.SanitizePart(req.Record.Shape, TSConfig.MaxHalfExtent.Value, out TerritoryShape part))
            {
                Reply(peer, req.RequestId, false, "The new part's position or size was not a number.", 0);
                return;
            }

            Territory updated = existing.Clone();
            string refused = TerritoryEdits.AddPart(updated, part, TSConfig.MaxParts.Value);
            if (refused != null) { Reply(peer, req.RequestId, false, refused, 0); return; }
            if (!updated.Sanitize(KeepLimit(updated)))
            {
                Reply(peer, req.RequestId, false, "The territory did not validate.", 0);
                return;
            }
            updated.Revision = existing.Revision + 1;

            TerritoryStore.Replace(updated);
            if (!Commit(peer, req, out string error)) { TerritoryStore.Replace(existing); Reply(peer, req.RequestId, false, error, 0); return; }

            Plugin.Log.LogInfo($"{who} added part {updated.PartCount} ({part.Describe()}) at {Pos(part)} to territory #{updated.Id} '{updated.Name}'.");
            Reply(peer, req.RequestId, true, $"Added part {updated.PartCount} to #{updated.Id} '{updated.Name}' ({part.Describe()}).", updated.Id);
        }

        /// <summary>TargetId loses part number Part (0 = the first); it must keep at least one.</summary>
        private static void RemovePart(ZNetPeer peer, Protocol.Request req, string who)
        {
            Territory existing = TerritoryStore.Get(req.TargetId);
            if (existing == null) { Reply(peer, req.RequestId, false, $"There is no territory #{req.TargetId}.", 0); return; }
            if (!Current(peer, req, existing)) return;

            Territory updated = existing.Clone();
            string refused = TerritoryEdits.RemovePart(updated, req.Part);
            if (refused != null) { Reply(peer, req.RequestId, false, refused, 0); return; }
            if (!updated.Sanitize(KeepLimit(updated)))
            {
                Reply(peer, req.RequestId, false, "The territory did not validate.", 0);
                return;
            }
            updated.Revision = existing.Revision + 1;

            TerritoryStore.Replace(updated);
            if (!Commit(peer, req, out string error)) { TerritoryStore.Replace(existing); Reply(peer, req.RequestId, false, error, 0); return; }

            TerritoryShape removed = existing.GetPart(req.Part);
            string left = Count(updated.PartCount, "part", "parts");
            Plugin.Log.LogInfo($"{who} removed part {req.Part + 1} ({removed.Describe()}) at {Pos(removed)} from territory #{updated.Id} '{updated.Name}'; {left} left.");
            Reply(peer, req.RequestId, true, $"Removed part {req.Part + 1} of #{updated.Id} '{updated.Name}'; {left} left.", updated.Id);
        }

        /// <summary>TargetId's part number Part becomes a territory of its own, with TargetId's rules. The reply names the NEW territory.</summary>
        private static void SplitPart(ZNetPeer peer, Protocol.Request req, string who)
        {
            Territory existing = TerritoryStore.Get(req.TargetId);
            if (existing == null) { Reply(peer, req.RequestId, false, $"There is no territory #{req.TargetId}.", 0); return; }
            if (!Current(peer, req, existing)) return;
            if (TerritoryStore.Territories.Count >= TSConfig.MaxTerritories.Value)
            {
                Reply(peer, req.RequestId, false, $"This world already has the maximum of {TSConfig.MaxTerritories.Value} territories.", 0);
                return;
            }

            Territory updated = existing.Clone();
            Territory split = TerritoryEdits.Split(updated, req.Part, out string refused);
            if (split == null) { Reply(peer, req.RequestId, false, refused, 0); return; }
            // Neither half is new ground, so each keeps the size it was placed at: its own limit, not today's.
            if (!updated.Sanitize(KeepLimit(updated)) || !split.Sanitize(KeepLimit(split)))
            {
                Reply(peer, req.RequestId, false, "The territory did not validate.", 0);
                return;
            }

            split.CreatedBy = who;
            split.CreatedTicks = DateTime.UtcNow.Ticks;
            split.Revision = 1;
            updated.Revision = existing.Revision + 1;

            TerritoryStore.Replace(updated);
            TerritoryStore.Add(split);
            if (string.IsNullOrEmpty(split.Name)) split.Name = $"Territory {split.Id}";

            if (!Commit(peer, req, out string error))
            {
                TerritoryStore.Remove(split.Id);
                TerritoryStore.Replace(existing);
                Reply(peer, req.RequestId, false, error, 0);
                return;
            }

            Plugin.Log.LogInfo($"{who} split part {req.Part + 1} off territory #{updated.Id} '{updated.Name}' as #{split.Id} '{split.Name}' ({split.Ground.Describe()}) at {Pos(split)}.");
            Reply(peer, req.RequestId, true, $"Split part {req.Part + 1} off #{updated.Id} '{updated.Name}' as #{split.Id} '{split.Name}'.", split.Id);
        }

        /// <summary>The effective record of a territory (what it means once it has taken what it follows), or null.</summary>
        private static Territory EffectiveOf(int id)
        {
            foreach (Territory t in TerritoryStore.Effective)
                if (t.Id == id) return t;
            return null;
        }

        /// <summary>
        /// MaxParts 1 is the server saying "no territories of several parts". Combining and adding are refused in
        /// those words, rather than as a count that is over by one; removing and splitting stay open, so ground
        /// combined before the setting changed can still be taken apart.
        /// </summary>
        private static bool PartsAllowed(ZNetPeer peer, Protocol.Request req)
        {
            if (TSConfig.MaxParts.Value > 1) return true;
            Reply(peer, req.RequestId, false, "Territories of several parts are turned off on this server (MaxParts is 1).", 0);
            return false;
        }

        /// <summary>
        /// Is the part this request names still the part the admin picked? A part is named by its place in the list,
        /// and removing or splitting one renumbers every part after it - so a request counted in an older list (another
        /// admin's edit got in first, or a second click went out within one round trip) would land on a different
        /// part, and its reply, which names the number the admin meant, would hide it. The sender says which Revision
        /// it counted in (Request.BaseRevision). The server keeps no old part lists, so ANY edit since - the rules
        /// alone, too - refuses rather than guesses; picking again costs a click. 0 is "do not check": a 0.1.x sender,
        /// or one that did not count the part from a list it was shown.
        /// </summary>
        private static bool Current(ZNetPeer peer, Protocol.Request req, Territory existing)
        {
            if (req.BaseRevision == 0 || req.BaseRevision == existing.Revision) return true;
            Reply(peer, req.RequestId, false, $"'{existing.DisplayName}' (#{existing.Id}) changed while this was on its way - look at its parts again and pick again.", 0);
            return false;
        }

        /// <summary>
        /// The MaxRadius an EDITED territory is sanitised with: the server's own, or its largest part if that is
        /// bigger, so an edit never shrinks ground placed under a larger limit or loaded from a file (which keeps
        /// whatever it holds). Every piece of NEW ground is clamped to the server's limit before it joins, so the
        /// edited clone's own largest part is exactly the largest ground already placed - for a combine, that
        /// includes the other territory's, which is why this reads the clone rather than the stored record.
        /// </summary>
        private static float KeepLimit(Territory edited) => Mathf.Max(TSConfig.MaxHalfExtent.Value, edited.Ground.MaxHalfExtent);

        /// <summary>Saves, and only then tells everyone. A change that could not be saved is rolled back by the caller.</summary>
        private static bool Commit(ZNetPeer peer, Protocol.Request req, out string error)
        {
            if (!TerritoryStore.Save(out error)) return false;
            Changed();
            return true;
        }

        private static void Reply(ZNetPeer peer, int requestId, bool ok, string message, int territoryId)
        {
            if (peer == null)
            {
                TerritoryClient.Deliver(ok, message, territoryId);
                return;
            }
            try
            {
                peer.m_rpc?.Invoke(Protocol.RpcResult, Protocol.WriteResult(requestId, ok, message, territoryId));
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Could not reply to {peer.m_playerName}: {ex.Message}");
            }
        }

        // ─────────────────────────────────────────────────────────────
        // IDENTITY
        // ─────────────────────────────────────────────────────────────

        internal static bool IsAdmin(ZNetPeer peer)
        {
            try
            {
                string host = peer?.m_socket?.GetHostName();
                return !string.IsNullOrEmpty(host) && ZNet.instance.IsAdmin(host);
            }
            catch
            {
                return false;
            }
        }

        private static string Who(ZNetPeer peer)
        {
            if (peer == null)
            {
                PlayerProfile profile = Game.instance != null ? Game.instance.GetPlayerProfile() : null;
                return profile != null ? $"{profile.GetName()} (host)" : "the host";
            }
            string host = "";
            try { host = peer.m_socket?.GetHostName() ?? ""; } catch { }
            return $"{peer.m_playerName} ({host})";
        }

        /// <summary>
        /// Everyone in the world right now, as (player id, name) - the ids a territory's members are stored
        /// by. peer.m_playerID is what 1.0.16 clients report on join (ZNet.RPC_PlayerID); the character
        /// ZDO's s_playerID is the fallback for a client that has not sent it yet.
        /// </summary>
        internal static List<TerritoryMember> OnlinePlayers()
        {
            var list = new List<TerritoryMember>();
            if (ZNet.instance == null) return list;

            Player local = Player.m_localPlayer;
            if (local != null)
            {
                long id = local.GetPlayerID();
                string name = local.GetPlayerName();
                if (id != 0L && IsRealName(name)) list.Add(new TerritoryMember(id, name));
            }

            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
            {
                if (peer == null || !peer.IsReady() || peer.m_characterID.IsNone()) continue;
                long id = peer.m_playerID;
                if (id == 0L && ZDOMan.instance != null)
                {
                    ZDO zdo = ZDOMan.instance.GetZDO(peer.m_characterID);
                    if (zdo != null) id = zdo.GetLong(ZDOVars.s_playerID, 0L);
                }
                if (id == 0L || !IsRealName(peer.m_playerName) || list.Exists(m => m.Id == id)) continue;
                list.Add(new TerritoryMember(id, peer.m_playerName));
            }

            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return list;
        }

        /// <summary>A dedicated server's own profile is "Stranger" with a random id; "..." is the unset default.</summary>
        private static bool IsRealName(string name) =>
            !string.IsNullOrEmpty(name) && name != "Stranger" && name != "...";

        private static int RosterStamp(List<ZNetPeer> peers)
        {
            unchecked
            {
                // The host's own player counts too: in single player there are no peers at all, and the
                // host's panel must still learn its own name once the character exists.
                int h = 17 + (Player.m_localPlayer != null ? Player.m_localPlayer.GetPlayerID().GetHashCode() : 0);
                foreach (ZNetPeer p in peers)
                {
                    if (p == null || p.m_characterID.IsNone()) continue;
                    h = h * 31 + p.m_uid.GetHashCode();
                    h = h * 31 + p.m_playerID.GetHashCode();
                }
                return h;
            }
        }

        private static bool IsDedicatedServer()
        {
            try { return ZNet.instance != null && ZNet.instance.IsDedicated(); }
            catch { return false; }
        }

        private static string Pos(Territory t) =>
            string.Format(CultureInfo.InvariantCulture, "({0:0.#}, {1:0.#})", t.X, t.Z);

        private static string Pos(TerritoryShape s) =>
            string.Format(CultureInfo.InvariantCulture, "({0:0.#}, {1:0.#})", s.CenterX, s.CenterZ);

        /// <summary>"no new members", "1 new member", "3 new members" - replies are read by people.</summary>
        private static string Count(int n, string one, string many) =>
            n == 0 ? "no " + many : n == 1 ? "1 " + one : n.ToString(CultureInfo.InvariantCulture) + " " + many;
    }
}
