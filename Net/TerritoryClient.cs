using System;
using System.Collections.Generic;
using UnityEngine;

namespace TerroritySmellatory
{
    /// <summary>
    /// A player's end of the wire: takes the server's territory list, remembers what the server said about
    /// this player, and carries an admin's requests up.
    /// </summary>
    internal static class TerritoryClient
    {
        /// <summary>A list has arrived this session (or this machine is the server and has its own).</summary>
        internal static bool HasSnapshot { get; private set; }

        /// <summary>
        /// Whether the SERVER says this player is an admin. Never guessed locally: on a client
        /// ZNet.GetAdminList() comes back empty even for a listed admin (CONSOLE-COMMAND-ROUTING-FACTS),
        /// so the only trustworthy answer is the server's, delivered with every snapshot.
        /// </summary>
        internal static bool IsAdmin { get; private set; }

        internal static bool AdminsBypass { get; private set; }

        internal static int ServerRevision { get; private set; }

        /// <summary>Who is online, as the server resolved them. Only ever filled for admins.</summary>
        internal static readonly List<TerritoryMember> OnlinePlayers = new List<TerritoryMember>();

        internal static string LastResult { get; private set; } = "";
        internal static bool LastResultOk { get; private set; }
        internal static float LastResultAt { get; private set; } = -1000f;

        /// <summary>(ok, message, territory id) for the most recent answer to one of this player's requests.</summary>
        internal static event Action<bool, string, int> ResultReceived;

        private static int _nextRequestId = 1;

        /// <summary>A territory list that comes in pieces (SnapshotParts) is put back together here, one piece at a time.</summary>
        private static readonly SnapshotParts.Assembler _parts = new SnapshotParts.Assembler();

        /// <summary>The lists this session that arrived in pieces and were put back together (the end-to-end rig reads it).</summary>
        internal static int PartedSnapshots { get; private set; }

        /// <summary>A list that has not had a piece for this long is dropped: the server sends another whenever anything changes.</summary>
        private const float PartsTimeoutSeconds = 60f;

        /// <summary>Does this player walk through territory protections? Only when the server says both "admin" and "allowed".</summary>
        internal static bool Bypass => IsAdmin && AdminsBypass;

        internal static long LocalPlayerId => Player.m_localPlayer != null ? Player.m_localPlayer.GetPlayerID() : 0L;

        internal static void Reset()
        {
            HasSnapshot = false;
            IsAdmin = false;
            AdminsBypass = false;
            ServerRevision = 0;
            OnlinePlayers.Clear();
            LastResult = "";
            LastResultOk = false;
            LastResultAt = -1000f;
            _parts.Clear();
            PartedSnapshots = 0;
        }

        internal static void Tick(float dt)
        {
            // Left the world without ZNet.OnDestroy reaching us (it should, but a stale list that keeps
            // enforcing in the main menu's next world would be far worse than a redundant clear).
            if (ZNet.instance == null && HasSnapshot)
            {
                Reset();
                TerritoryIndex.Clear();
            }

            if (_parts.Pending && _parts.Expire(Time.realtimeSinceStartup, PartsTimeoutSeconds))
                Plugin.Log.LogWarning("A territory list that came in pieces stopped arriving; it is dropped, and the server's next list replaces it.");
        }

        // ─────────────────────────────────────────────────────────────
        // RECEIVING (registered on the server connection in ConnectionPatches)
        // ─────────────────────────────────────────────────────────────

        /// <summary>One piece of a list too big for one message (Net/SnapshotParts.cs): gathered, and handled as a snapshot when the last arrives.</summary>
        internal static void OnSnapshotPart(ZRpc rpc, ZPackage pkg)
        {
            // As OnSnapshot: a server never takes a territory list from anybody.
            if (ZNet.instance == null || ZNet.instance.IsServer()) return;

            ZPackage whole = _parts.Add(pkg, Time.realtimeSinceStartup, out string problem);
            if (problem != null) Plugin.Log.LogWarning($"A piece of the territory list was refused: {problem}.");
            if (whole == null) return;

            PartedSnapshots++;
            Plugin.Debug($"the territory list arrived in {_parts.Parts} pieces ({whole.Size()} bytes)");
            OnSnapshot(rpc, whole);
        }

        internal static void OnSnapshot(ZRpc rpc, ZPackage pkg)
        {
            // A server never takes a territory list from anybody. This handler is only registered on a
            // client's server connection anyway; the check costs nothing and closes the listen-host case.
            if (ZNet.instance == null || ZNet.instance.IsServer()) return;

            Protocol.Snapshot s;
            try
            {
                s = Protocol.ReadSnapshot(pkg);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Could not read the server's territory list ({ex.Message}). Is the server running a much newer TerroritySmellatory?");
                return;
            }
            if (s == null) return;

            HasSnapshot = true;
            IsAdmin = s.YouAreAdmin;
            AdminsBypass = s.AdminsBypass;
            ServerRevision = s.Revision;
            SetOnlinePlayers(s.OnlinePlayers);
            // What each sub-territory follows is filled in here, by the code the server runs on the same records, so every game
            // enforces the same picture; the marks stay on the records for the panel (Core/Inheritance.cs).
            TerritoryIndex.Set(Inheritance.Resolve(s.Territories));

            Plugin.Debug($"received {s.Territories.Count} territories (rev {s.Revision}, admin {s.YouAreAdmin})");
        }

        internal static void OnResult(ZRpc rpc, ZPackage pkg)
        {
            if (ZNet.instance == null || ZNet.instance.IsServer()) return;
            try
            {
                if (Protocol.ReadResult(pkg, out int _, out bool ok, out string message, out int id))
                    Deliver(ok, message, id);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Unreadable answer from the server: {ex.Message}");
            }
        }

        /// <summary>On a listen host or in single player the server IS this machine, and its host is its admin.</summary>
        internal static void ApplyLocalHost(int revision, List<TerritoryMember> online)
        {
            HasSnapshot = true;
            IsAdmin = true;
            AdminsBypass = TSConfig.AdminsBypassProtection.Value;
            ServerRevision = revision;
            SetOnlinePlayers(online);
        }

        internal static void SetOnlinePlayers(List<TerritoryMember> players)
        {
            OnlinePlayers.Clear();
            if (players != null) OnlinePlayers.AddRange(players);
        }

        internal static void Deliver(bool ok, string message, int territoryId)
        {
            LastResult = message ?? "";
            LastResultOk = ok;
            LastResultAt = Time.realtimeSinceStartup;

            Print((ok ? "" : "Refused: ") + LastResult);
            MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, "Territory: " + LastResult);

            try { ResultReceived?.Invoke(ok, LastResult, territoryId); }
            catch (Exception ex) { Plugin.Log.LogError($"A territory result listener threw (non-fatal): {ex}"); }
        }

        // ─────────────────────────────────────────────────────────────
        // SENDING (admins only)
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// May this player send requests at all? The server's own verdict on this player, when it has given
        /// one, is final - and sending anyway is not harmless: a server running Wonderland's RPC whitelist
        /// kicks a non-admin who sends an RPC it does not know. With no verdict yet (no list received) the
        /// request goes up and the server decides.
        /// </summary>
        internal static bool MayRequest(out string why)
        {
            why = "";
            if (ZNet.instance == null)
            {
                why = "You are not in a world.";
                return false;
            }
            if (!ZNet.instance.IsServer() && HasSnapshot && !IsAdmin)
            {
                why = "Only server admins can change territories, and the server says you are not one.";
                return false;
            }
            return true;
        }

        /// <summary>Asks the server to do something. False when it could not even be sent.</summary>
        internal static bool Send(RequestOp op, Territory record = null, int targetId = 0, int otherId = 0, int part = 0,
            int baseRevision = 0)
        {
            if (!MayRequest(out string why))
            {
                Print(why);
                return false;
            }

            ZPackage pkg = Protocol.WriteRequest(_nextRequestId++, op, record, targetId, otherId, part, baseRevision);

            if (ZNet.instance.IsServer())
            {
                TerritoryServer.OnLocalRequest(pkg);
                return true;
            }

            ZNetPeer server = ZNet.instance.GetServerPeer();
            if (server == null || server.m_rpc == null)
            {
                Print("There is no server connection.");
                return false;
            }

            server.m_rpc.Invoke(Protocol.RpcRequest, pkg);
            Print($"{op} sent to the server - the answer will follow.");
            return true;
        }

        /// <summary>
        /// The console, not the log: a command that answers only in the BepInEx log looks exactly like a
        /// command that does not exist (CONSOLE-COMMAND-ROUTING-FACTS, "The visibility trap").
        /// </summary>
        internal static void Print(string text)
        {
            try { Console.instance?.Print("[Territory] " + text); }
            catch { }
            Plugin.Log.LogInfo(text);
        }
    }
}
