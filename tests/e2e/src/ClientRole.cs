using System.Collections;
using UnityEngine;

namespace TerrE2E
{
    /// <summary>
    /// A client process: dual mode plays one character (admin or plain) from start to end; solo mode plays both
    /// in turn on one process, logging out and back in between phases:
    ///   TSAdmin phase 1 -> TSPlain phase 1 -> TSAdmin phase 2 -> TSPlain phase 2.
    /// The phases are the same code in both modes; only the waits on the other character's barriers differ.
    /// </summary>
    internal static class ClientRole
    {
        internal static IEnumerator Run(E2EPlugin plugin)
        {
            World.InstallObservers(E2EPlugin.Harmony);
            var ok = new Box<bool>();
            yield return ClientBoot.ReachMenu(ok);
            string role = E2EEnv.Role;

            if (!ok.Value || role == "probe")
            {
                if (role == "probe")
                {
                    yield return Flow.Seconds(3f);
                    // Spike (a) on a graphics client: the barrier's flare in front of the menu's camera, seen in real pixels.
                    if (ok.Value && FlareShot.Available) yield return Flow.Step("F02-flare-retry", "a failed build is retried", FlareShot.RetryProof("F02-flare-retry"));
                    if (ok.Value && FlareShot.Available) yield return Flow.Step("F01-flare-pixels", "flare pixels in the menu", FlareShot.RunIsolated("F01-flare-pixels", "menu"));
                    if (ok.Value && MapFogShot.Available) yield return Flow.Step("F04-fog-gate-pixels", "the explored-ground gate clips real pixels", MapFogShot.Run("F04-fog-gate-pixels"));
                    if (ok.Value && E2EEnv.Booth) yield return Flow.Step("F03-panel-booth", "the panel, drawn over the menu", Scenario040.PanelBooth());
                }
                Finish(plugin);
                yield break;
            }

            if (role == "join")
            {
                // Spikes (b) and (c): create the character, join, prove the local player exists, stay a while, quit.
                string name = E2EEnv.CharName ?? E2EEnv.AdminName;
                yield return ClientBoot.Join(name, name == E2EEnv.AdminName ? "J1" : "J2", ok);
                if (ok.Value)
                {
                    RunDir.Signal($"join.{name}", new { id = Player.m_localPlayer.GetPlayerID(), status = ZNet.GetConnectionStatus().ToString() });
                    float until = Time.realtimeSinceStartup + E2EEnv.Hold;
                    while (Time.realtimeSinceStartup < until && Player.m_localPlayer != null) yield return Flow.Seconds(1f);
                    Report.Check(name == E2EEnv.AdminName ? "J1-stayed" : "J2-stayed", $"{name} stayed in the world for {E2EEnv.Hold:0} s",
                        Player.m_localPlayer != null && ZNet.GetConnectionStatus() == ZNet.ConnectionStatus.Connected, $"status {ZNet.GetConnectionStatus()}");
                }
                Finish(plugin);
                yield break;
            }

            if (role == "admin" || role == "plain")
            {
                bool admin = role == "admin";
                string name = E2EEnv.CharName ?? (admin ? E2EEnv.AdminName : E2EEnv.PlainName);
                yield return ClientBoot.Join(name, admin ? "A00" : "P00", ok);
                if (ok.Value)
                {
                    GodMode();
                    if (admin)
                    {
                        yield return Flow.Step("A-phase1", "admin phase 1", ScenarioAdmin.Phase1());
                        yield return Flow.Step("A-phase2", "admin phase 2", ScenarioAdmin.Phase2());
                        if (Features.Barrier) yield return Flow.Step("A-phase3", "admin phase 3", ScenarioAdmin.Phase3());
                    }
                    else
                    {
                        yield return Flow.Step("P-phase1", "plain phase 1", ScenarioPlain.Phase1());
                        yield return Flow.Step("P-phase2", "plain phase 2", ScenarioPlain.Phase2());
                        if (Features.Barrier) yield return Flow.Step("P-phase3", "plain phase 3", ScenarioPlain.Phase3());
                    }
                }
                else
                {
                    RunDir.Signal(admin ? "admin.join-failed" : "plain.join-failed", new { status = ZNet.GetConnectionStatus().ToString() });
                }
                Finish(plugin);
                yield break;
            }

            if (role == "solo")
            {
                yield return Solo(ok);
                Finish(plugin);
            }
        }

        private static IEnumerator Solo(Box<bool> ok)
        {
            yield return ClientBoot.Join(E2EEnv.AdminName, "A00", ok);
            if (!ok.Value) yield break;
            GodMode();
            yield return Flow.Step("A-phase1", "admin phase 1", ScenarioAdmin.Phase1());
            yield return ClientBoot.Logout(ok);
            if (!ok.Value) yield break;

            yield return ClientBoot.Join(E2EEnv.PlainName, "P00", ok);
            if (!ok.Value) yield break;
            GodMode();
            yield return Flow.Step("P-phase1", "plain phase 1", ScenarioPlain.Phase1());
            yield return ClientBoot.Logout(ok);
            if (!ok.Value) yield break;

            yield return ClientBoot.Join(E2EEnv.AdminName, "A00b", ok);
            if (!ok.Value) yield break;
            GodMode();
            yield return Flow.Step("A-phase2", "admin phase 2", ScenarioAdmin.Phase2());
            yield return ClientBoot.Logout(ok);
            if (!ok.Value) yield break;

            yield return ClientBoot.Join(E2EEnv.PlainName, "P00b", ok);
            if (!ok.Value) yield break;
            GodMode();
            yield return Flow.Step("P-phase2", "plain phase 2", ScenarioPlain.Phase2());
            if (!Features.Barrier) yield break;

            // 0.3.0: the admin makes TSPlain a member of the visitor barrier, and TSPlain walks in (two more logins).
            yield return ClientBoot.Logout(ok);
            if (!ok.Value) yield break;
            yield return ClientBoot.Join(E2EEnv.AdminName, "A00c", ok);
            if (!ok.Value) yield break;
            GodMode();
            yield return Flow.Step("A-phase3", "admin phase 3", ScenarioAdmin.Phase3());
            yield return ClientBoot.Logout(ok);
            if (!ok.Value) yield break;

            yield return ClientBoot.Join(E2EEnv.PlainName, "P00c", ok);
            if (!ok.Value) yield break;
            GodMode();
            yield return Flow.Step("P-phase3", "plain phase 3", ScenarioPlain.Phase3());
        }

        /// <summary>
        /// Wildlife must not end a run: the test characters take no damage. No territory rule reads god mode
        /// (ward access, pickables, structure damage, PvP flag, status effects and banners are all independent of it).
        /// </summary>
        private static void GodMode()
        {
            if (Player.m_localPlayer != null) Player.m_localPlayer.SetGodMode(true);
        }

        private static void Finish(E2EPlugin plugin)
        {
            Report.Info($"client finished; Steam Cloud safety trips this session: {ClientSafety.Trips}");
            if (ClientSafety.Trips > 0)
                Report.Fail("X01-cloud-safety", "no Steam Cloud write was attempted", $"{ClientSafety.Trips} write(s) refused - see SAFETY-TRIP lines");
            Report.Done();
            if (Game.instance != null && E2EEnv.Role != "probe")
                Game.instance.Logout(save: true, changeToStartScene: false);
            plugin.StartCoroutine(plugin.QuitSoon(3f));
        }
    }
}
