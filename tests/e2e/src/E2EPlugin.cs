using System;
using System.Collections;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace TerrE2E
{
    // THE E2E DRIVER. One plugin, one scenario, played by three processes (a dedicated server and two headless
    // clients) or, when the server refuses a second connection from the same Steam account, by one client
    // that switches characters between phases (TERR_E2E_MODE=solo).
    //
    // INERT unless TERR_E2E_ROLE is set. Roles:
    //   server          the dedicated server of the run: the test seam, request observers, file checks
    //   server-restart  the same server booted again: the territory file must reload unchanged
    //   admin / plain   one client each (TERR_E2E_MODE=dual)
    //   solo            one client playing admin and plain in turn (TERR_E2E_MODE=solo)
    //   probe           spike (a): reach the main menu, prove the redirects, quit
    //
    // Every check prints one line "[E2E] PASS|FAIL|SOFT <id> <what>[: <why>]" and each role ends with
    // "[E2E] DONE role=<r> passed=<n> failed=<m> soft=<k>". run-e2e.sh collects them.
    [BepInPlugin(Guid, "TerrE2E", "1.0.0")]
    [BepInDependency(ModGuid, BepInDependency.DependencyFlags.HardDependency)]
    public sealed class E2EPlugin : BaseUnityPlugin
    {
        public const string Guid = "wubarrk.terroritysmellatory.e2e";
        public const string ModGuid = "wubarrk.terroritysmellatory";

        internal static E2EPlugin Instance { get; private set; }
        internal static Harmony Harmony { get; private set; }

        private string _role;
        private float _nextHousekeeping;
        private bool _orphaned;

        private void Awake()
        {
            Instance = this;
            _role = E2EEnv.Role;
            if (string.IsNullOrEmpty(_role))
            {
                Logger.LogInfo("TerrE2E idle: TERR_E2E_ROLE is not set, so this driver does nothing.");
                enabled = false;
                return;
            }

            Report.Init(Logger, _role);
            Harmony = new Harmony(Guid);
            Report.Info($"driver awake: role={_role} mode={E2EEnv.Mode} rundir={E2EEnv.RunDir} mark={E2EEnv.Mark}");

            try
            {
                if (E2EEnv.IsClientRole)
                {
                    // BEFORE anything else: the redirects and the Steam Cloud / achievements guards. BepInEx runs
                    // plugin Awake before the start scene's objects, so FejdStartup has not read a save yet.
                    bool safe;
                    try { safe = ClientSafety.Install(Harmony); }
                    catch (Exception ex)
                    {
                        Report.Info("SAFETY: guard installation threw: " + ex);
                        safe = false;
                    }
                    if (!safe)
                    {
                        Report.Fail("X00-safety", "client safety guards installed", "refusing to run (see the SAFETY lines above)");
                        ClientSafety.KillNow("the safety guards are not in place");
                        return;
                    }
                    StartCoroutine(Flow.Guard("client", ClientRole.Run(this), OnFatal));
                }
                else if (_role == "server" || _role == "server-restart")
                {
                    ServerRole.Install(Harmony);
                    StartCoroutine(Flow.Guard("server", ServerRole.Run(this), OnFatal));
                }
                else
                {
                    Report.Fail("X00-role", "known role", $"unknown TERR_E2E_ROLE '{_role}'");
                    Report.Done();
                }
            }
            catch (Exception ex)
            {
                OnFatal(ex);
            }
        }

        private void OnFatal(Exception ex)
        {
            Report.Fail("X99-driver", "driver ran to the end", $"unhandled exception: {ex}");
            Report.Done();
            if (E2EEnv.IsClientRole) StartCoroutine(QuitSoon());
        }

        internal IEnumerator QuitSoon(float seconds = 2f)
        {
            yield return new WaitForSecondsRealtime(seconds);
            Report.Info("quitting the game (Application.Quit)");
            Application.Quit(0);
        }

        private void Update()
        {
            if (Time.realtimeSinceStartup < _nextHousekeeping) return;
            _nextHousekeeping = Time.realtimeSinceStartup + 1f;
            if (E2EEnv.IsClientRole) ClientSafety.Housekeeping();
            // Never outlive the rig: if the script that started this process is gone (killed, crashed), quit.
            int rig = E2EEnv.RigPid;
            if (rig > 0 && !System.IO.Directory.Exists("/proc/" + rig) && !_orphaned)
            {
                _orphaned = true;
                Report.Info($"the rig (pid {rig}) is gone - quitting this game process");
                Application.Quit(0);
            }
        }
    }
}
