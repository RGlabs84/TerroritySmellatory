using System;
using System.Globalization;

namespace TerrE2E
{
    /// <summary>Everything run-e2e.sh tells a process, through its environment.</summary>
    internal static class E2EEnv
    {
        private static string Get(string name)
        {
            string v = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrEmpty(v) ? null : v;
        }

        /// <summary>
        /// E2E_BOOTH=1 (spike a, a windowed graphics client): the panel is drawn over the main menu on a made-up world and photographed
        /// (Scenario040.PanelBooth), the one way to see it without a full run - a batchmode client never calls its OnGUI.
        /// </summary>
        internal static bool Booth => Get("TERR_E2E_BOOTH") == "1";

        /// <summary>server | server-restart | admin | plain | solo | probe | join; unset = the driver is inert.</summary>
        internal static string Role => Get("TERR_E2E_ROLE");

        /// <summary>dual (two clients at once) or solo (one client plays both characters in turn).</summary>
        internal static string Mode => Get("TERR_E2E_MODE") ?? "dual";

        internal static bool Solo => Mode == "solo";

        /// <summary>
        /// DRY RUN of the 0.3.0 phase on a DLL that has no runic barrier (run-e2e.sh's E2E_FORCE_030=1): the barrier
        /// territories are plain no-monster ones, so the hold, push-out, flare and visitor checks must go RED. Never set
        /// in a real run.
        /// </summary>
        internal static bool Force030 => Get("TERR_E2E_FORCE030") == "1";

        /// <summary>DRY RUN of the 0.3.1 door phase on a DLL without AutoCloseDoors (run-e2e.sh's E2E_FORCE_031=1): the door that must close stays open, so P31a and P31f must go RED. Never set in a real run.</summary>
        internal static bool Force031 => Get("TERR_E2E_FORCE031") == "1";

        /// <summary>The AutoCloseDelay run-e2e.sh writes into the server's config (seconds): the door checks' clock.</summary>
        internal static float DoorDelay => float.TryParse(Get("TERR_E2E_DOORDELAY"), NumberStyles.Float, CultureInfo.InvariantCulture, out float f) && f >= 1f ? f : 3f;

        /// <summary>The shared run directory: barrier files, exchanged state and the per-role result files.</summary>
        internal static string RunDir => Get("TERR_E2E_RUNDIR");

        /// <summary>A unique string per run, also in the environment of every process the rig starts.</summary>
        internal static string Mark => Get("TERR_E2E_MARK") ?? "";

        /// <summary>Client only: the throwaway save directory handed to Utils.SetSaveDataPath.</summary>
        internal static string SaveDir => Get("TERR_E2E_SAVEDIR");

        /// <summary>Client only: Rohan's real home. The driver refuses to run if any redirect still points into it.</summary>
        internal static string RealHome => Get("TERR_E2E_REALHOME");

        /// <summary>Client only: "127.0.0.1:port" of the run's dedicated server.</summary>
        internal static string Join => Get("TERR_E2E_JOIN");

        /// <summary>Client only: the world's territory file on the server, read to compare with what arrived.</summary>
        internal static string WorldFile => Get("TERR_E2E_WORLDFILE");

        /// <summary>Client only, dual mode: this process's character (TSAdmin or TSPlain).</summary>
        internal static string CharName => Get("TERR_E2E_CHAR");

        /// <summary>Server only: how many clients the run starts (2 in dual mode, 1 in solo mode).</summary>
        internal static int ExpectedClients => Solo ? 1 : 2;

        /// <summary>The rig script's pid: every process quits by itself if the rig that started it is gone.</summary>
        internal static int RigPid => int.TryParse(Get("TERR_E2E_RIGPID"), out int p) ? p : 0;

        /// <summary>Spike roles: how long a "join" client stays in the world before quitting.</summary>
        internal static float Hold => float.TryParse(Get("TERR_E2E_HOLD"), NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : 45f;

        internal static float StepTimeout
        {
            get
            {
                string s = Get("TERR_E2E_STEP_TIMEOUT");
                return s != null && float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) && f > 5f ? f : 90f;
            }
        }

        internal static bool IsClientRole
        {
            get
            {
                string r = Role;
                return r == "admin" || r == "plain" || r == "solo" || r == "probe" || r == "join";
            }
        }

        internal const string AdminName = "TSAdmin";
        internal const string PlainName = "TSPlain";
    }
}
