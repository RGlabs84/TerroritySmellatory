using HarmonyLib;

namespace TerroritySmellatory
{
    /// <summary>
    /// Registers this mod's RPCs on every new connection's own ZRpc - the server side of a client's
    /// connection, or a client's connection to its server. See Net/Protocol.cs for why ZRpc and not routed.
    /// </summary>
    /// <remarks>
    /// OnNewConnection runs the moment a socket becomes a peer, before any handshake traffic, so the handler
    /// is in place long before either side could send - the same seam ServerSync registers on. It is called
    /// again for every connection of every session, so nothing here can go stale across world joins the way
    /// a one-shot ZRoutedRpc registration does (12-CHAT-AND-CUSTOM-RPC.md §1).
    /// </remarks>
    [HarmonyPatch(typeof(ZNet), "OnNewConnection")]
    internal static class ZNetOnNewConnectionPatch
    {
        private static void Postfix(ZNet __instance, ZNetPeer peer)
        {
            if (peer?.m_rpc == null) return;

            if (__instance.IsServer())
            {
                // The peer is captured, not looked up: the request arrived on THIS connection, which is the
                // whole of its identity.
                peer.m_rpc.Register<ZPackage>(Protocol.RpcRequest, (rpc, pkg) => TerritoryServer.OnRequest(peer, pkg));
                peer.m_rpc.Register<ZDOID>(Protocol.RpcGuardCall, (rpc, target) => Guards.OnPeerCall(peer, target));
            }
            else
            {
                peer.m_rpc.Register<ZPackage>(Protocol.RpcSnapshot, TerritoryClient.OnSnapshot);
                peer.m_rpc.Register<ZPackage>(Protocol.RpcSnapshotPart, TerritoryClient.OnSnapshotPart);
                peer.m_rpc.Register<ZPackage>(Protocol.RpcResult, TerritoryClient.OnResult);
                peer.m_rpc.Register<string>(Protocol.RpcGuardNotice, Guards.OnNotice);
            }
        }
    }

    /// <summary>A new ZNet is a new session: nothing from the last world may leak into this one.</summary>
    [HarmonyPatch(typeof(ZNet), "Awake")]
    internal static class ZNetAwakePatch
    {
        private static void Postfix()
        {
            TerritoryClient.Reset();
            TerritoryServer.Reset();
            TerritoryIndex.Clear();
        }
    }

    [HarmonyPatch(typeof(ZNet), "OnDestroy")]
    internal static class ZNetOnDestroyPatch
    {
        private static void Postfix()
        {
            TerritoryClient.Reset();
            TerritoryServer.Reset();
            TerritoryStore.Unload();
            TerritoryIndex.Clear();
            // The whole session's presence, banner cooldowns and "seen" territories included. A death clears none
            // of them (Presence.Tick keeps them through the respawn); leaving the world clears all of them.
            Presence.ResetSession();
        }
    }
}
