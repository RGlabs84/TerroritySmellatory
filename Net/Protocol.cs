using System;
using System.Collections.Generic;

namespace TerroritySmellatory
{
    /// <summary>What an admin asks the server to do. PERSISTED on the wire - append only.</summary>
    internal enum RequestOp
    {
        Create = 1,
        Update = 2,
        Reshape = 3,
        Delete = 4,
        Reload = 5,
        Refresh = 6,

        // 0.2.0 - a territory made of several parts (Core/TerritoryEdits.cs). Reshape above also takes
        // Request.Part now, which a protocol 1 sender never sets, so it reshapes the first part as it did.

        /// <summary>TargetId takes OtherId's ground, owner and members; OtherId is deleted.</summary>
        Combine = 7,

        /// <summary>The record's ground (its first part) becomes a new part of TargetId.</summary>
        AddPart = 8,

        /// <summary>TargetId loses its part number Part (0 = the first); it must keep at least one.</summary>
        RemovePart = 9,

        /// <summary>TargetId's part number Part becomes a territory of its own, with TargetId's rules.</summary>
        SplitPart = 10,

        // 0.4.0 - sub-territories (Core/Inheritance.cs). Delete above keeps a territory's sub-territories (each keeps what it did
        // and moves up a level); this one takes them with it.

        /// <summary>TargetId and every territory inside it, at any depth, are deleted.</summary>
        DeleteTree = 11
    }

    // THE WIRE.
    //
    // Everything travels as a ZPackage over the peer's OWN ZRpc connection - server to one client, or a
    // client to the server - never as a routed RPC. Three reasons, each of which would be a bug otherwise:
    //
    //   * The server knows WHO sent a request from the connection it arrived on. A routed RPC carries its
    //     sender id inside the payload, where a modified client can write whatever it likes; a ZRpc cannot
    //     claim to be anyone but the socket it came down.
    //   * A client knows a snapshot came from the SERVER, because its only ZRpc is to the server. A routed
    //     "Everybody" message is relayed between clients by the server, so a modified client could inject
    //     a fake territory list into everyone else's game.
    //   * A ZRpc method nobody registered is dropped silently (ZRpc.HandlePackage), so a player without
    //     the mod on a mixed server is simply never bothered.
    //
    // Handlers are registered per connection in ZNet.OnNewConnection (Net/ConnectionPatches.cs), the same
    // seam ServerSync uses, and therefore independent of the handshake order - Better Continents defers the
    // client's RPC_PeerInfo until its world data has downloaded, and nothing here waits on or races that.
    //
    // SIZE: Steam refuses a reliable message over 512 KiB, and a ZRpc call is one message however it is registered, so a list too big
    // for one goes in pieces on the same ZRpc, as Better Continents' BakedTransfer does it for a world's maps (Net/SnapshotParts.cs).
    //
    // FORMAT GROWTH: every package starts with the protocol version, and each territory record is its
    // own length-prefixed, versioned sub-package (Core/Territory.cs). New fields are APPENDED and read
    // behind `if (protocol >= N)`; an older reader stops early and loses nothing it knew about.
    internal static class Protocol
    {
        /// <summary>2 (0.2.0): requests carry OtherId, Part and BaseRevision; territory records carry their extra parts.</summary>
        internal const int Version = 2;

        internal const string RpcSnapshot = "TerroritySmellatory_Snapshot";
        internal const string RpcRequest = "TerroritySmellatory_Request";
        internal const string RpcResult = "TerroritySmellatory_Result";

        /// <summary>
        /// 0.4.0: one piece of a territory list that is too big for one message (Net/SnapshotParts.cs). Server to client, on the same
        /// per-peer ZRpc as the snapshot; a client that never registers it (a game without the mod) drops it silently, like the others.
        /// </summary>
        internal const string RpcSnapshotPart = "TerroritySmellatory_SnapshotPart";

        /// <summary>
        /// The most bytes one piece carries, and the size above which a territory list is sent in pieces at all (a smaller one is the
        /// one message it always was). Steam refuses a reliable message over 512 KiB; this is far under it, and under what the socket's
        /// queue should be asked to hold at once. Not a constant only because the end-to-end rig lowers it on its server, so that a
        /// test world of a few kilobytes goes in pieces too.
        /// </summary>
        internal static int PartBytes = 64 * 1024;

        /// <summary>
        /// 0.5.0: a player calls the guards (Rules/Guards.cs). Client to server, on the caller's own ZRpc, carrying the ZDOID of the monster
        /// their game picked; the server checks everything itself and knows the caller from the connection. Not an admin request, so it is
        /// not in the Request set: any player in a territory with guards may send it, and the server's cooldown holds each to one a while.
        /// </summary>
        internal const string RpcGuardCall = "TerroritySmellatory_GuardCall";

        /// <summary>0.5.0: the server's one-line answer to a guard call ("A guard answers your call."). Server to client, on the same ZRpc.</summary>
        internal const string RpcGuardNotice = "TerroritySmellatory_GuardNotice";

        [Flags]
        private enum SnapshotFlags
        {
            None = 0,
            YouAreAdmin = 1,
            AdminsBypass = 2
        }

        internal sealed class Snapshot
        {
            public int Protocol;
            public long WorldUid;
            public int Revision;
            public bool YouAreAdmin;
            public bool AdminsBypass;
            public List<Territory> Territories = new List<Territory>();
            public List<TerritoryMember> OnlinePlayers = new List<TerritoryMember>();
        }

        internal sealed class Request
        {
            public int Protocol;
            public int RequestId;
            public RequestOp Op;
            public Territory Record;
            public int TargetId;

            /// <summary>Protocol 2: the second territory of a Combine (the one that is joined in and deleted).</summary>
            public int OtherId;

            /// <summary>Protocol 2: which part a Reshape, RemovePart or SplitPart means; 0 is the first part.</summary>
            public int Part;

            /// <summary>
            /// Protocol 2: the Revision of the territory as the sender saw it when it counted Part, or 0 for "do not
            /// check". A part is named by its position, and removing or splitting one renumbers every later part, so
            /// a request counted in an older list (a second admin, or a second click inside one round trip) would hit
            /// a different part. The server refuses such a request instead of guessing.
            /// </summary>
            public int BaseRevision;
        }

        // ─────────────────────────────────────────────────────────────
        // SNAPSHOT (server -> client): the whole list, every time. Territories are few and small; a full
        // list is a few KB and means a client can never drift from the server by missing a delta.
        // ─────────────────────────────────────────────────────────────

        internal static ZPackage WriteSnapshot(long worldUid, int revision, bool youAreAdmin, bool adminsBypass,
            IReadOnlyList<Territory> territories, IReadOnlyList<TerritoryMember> onlinePlayers)
        {
            var pkg = new ZPackage();
            pkg.Write(Version);
            pkg.Write(worldUid);
            pkg.Write(revision);

            SnapshotFlags flags = SnapshotFlags.None;
            if (youAreAdmin) flags |= SnapshotFlags.YouAreAdmin;
            if (adminsBypass) flags |= SnapshotFlags.AdminsBypass;
            pkg.Write((int)flags);

            pkg.Write(territories.Count);
            foreach (Territory t in territories) t.Write(pkg);

            // Only admins get the roster: it is what the panel's member picker offers, and nobody else
            // has a panel that edits anything.
            int online = onlinePlayers?.Count ?? 0;
            pkg.Write(online);
            for (int i = 0; i < online; i++)
            {
                pkg.Write(onlinePlayers[i].Id);
                pkg.Write(onlinePlayers[i].Name ?? "");
            }
            return pkg;
        }

        internal static Snapshot ReadSnapshot(ZPackage pkg)
        {
            var s = new Snapshot { Protocol = pkg.ReadInt() };
            if (s.Protocol < 1) return null;

            s.WorldUid = pkg.ReadLong();
            s.Revision = pkg.ReadInt();
            var flags = (SnapshotFlags)pkg.ReadInt();
            s.YouAreAdmin = (flags & SnapshotFlags.YouAreAdmin) != 0;
            s.AdminsBypass = (flags & SnapshotFlags.AdminsBypass) != 0;

            int count = pkg.ReadInt();
            if (count < 0 || count > 100000) throw new FormatException($"snapshot claims {count} territories");
            for (int i = 0; i < count; i++)
            {
                Territory t = Territory.Read(pkg);
                if (t != null) s.Territories.Add(t);
            }

            int online = pkg.ReadInt();
            if (online < 0 || online > 10000) throw new FormatException($"snapshot claims {online} players");
            for (int i = 0; i < online; i++)
                s.OnlinePlayers.Add(new TerritoryMember(pkg.ReadLong(), pkg.ReadString()));

            // protocol >= 2 additions go here.
            return s;
        }

        // ─────────────────────────────────────────────────────────────
        // REQUEST (admin client -> server)
        // ─────────────────────────────────────────────────────────────

        internal static ZPackage WriteRequest(int requestId, RequestOp op, Territory record, int targetId, int otherId = 0, int part = 0,
            int baseRevision = 0)
        {
            var pkg = new ZPackage();
            pkg.Write(Version);
            pkg.Write(requestId);
            pkg.Write((int)op);
            pkg.Write(targetId);
            pkg.Write(record != null);
            record?.Write(pkg);
            pkg.Write(otherId);
            pkg.Write(part);
            pkg.Write(baseRevision);
            return pkg;
        }

        internal static Request ReadRequest(ZPackage pkg)
        {
            var r = new Request { Protocol = pkg.ReadInt() };
            if (r.Protocol < 1) return null;
            r.RequestId = pkg.ReadInt();
            r.Op = (RequestOp)pkg.ReadInt();
            r.TargetId = pkg.ReadInt();
            if (pkg.ReadBool()) r.Record = Territory.Read(pkg);
            if (r.Protocol >= 2)
            {
                r.OtherId = pkg.ReadInt();
                r.Part = pkg.ReadInt();
                r.BaseRevision = pkg.ReadInt();
            }
            return r;
        }

        // ─────────────────────────────────────────────────────────────
        // RESULT (server -> the admin who asked)
        // ─────────────────────────────────────────────────────────────

        internal static ZPackage WriteResult(int requestId, bool ok, string message, int territoryId)
        {
            var pkg = new ZPackage();
            pkg.Write(Version);
            pkg.Write(requestId);
            pkg.Write(ok);
            pkg.Write(message ?? "");
            pkg.Write(territoryId);
            return pkg;
        }

        internal static bool ReadResult(ZPackage pkg, out int requestId, out bool ok, out string message, out int territoryId)
        {
            requestId = 0;
            ok = false;
            message = "";
            territoryId = 0;
            if (pkg.ReadInt() < 1) return false;
            requestId = pkg.ReadInt();
            ok = pkg.ReadBool();
            message = pkg.ReadString();
            territoryId = pkg.ReadInt();
            return true;
        }
    }
}
