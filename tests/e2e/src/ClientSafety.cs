using System;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace TerrE2E
{
    /// <summary>
    /// What keeps a test client off Rohan's real data. Three independent layers, so no single mistake leaks:
    ///   1. run-e2e.sh starts the client with HOME / XDG_CONFIG_HOME / XDG_DATA_HOME pointing into a throwaway
    ///      fake home, so Application.persistentDataPath, Unity's PlayerPrefs ("prefs") and Player.log land
    ///      there. This class REFUSES TO RUN if persistentDataPath still points into the real home.
    ///   2. Utils.SetSaveDataPath(TERR_E2E_SAVEDIR): characters, worlds and server lists go to the run's save
    ///      dir (the client ignores -savedir, only the dedicated server parses it).
    ///   3. Steam Cloud is switched off in-process (SteamCloud.IsEnabled answers false, so every save is Local)
    ///      and every Steam Cloud write/delete/copy/move is refused with a SAFETY-TRIP line; Steam achievements
    ///      and stats writes are no-ops. Characters are created FileSource.Local only.
    ///   4. SteamAPI.RestartAppIfNecessary answers false WITHOUT calling Steam: a client started outside Steam
    ///      could otherwise ask Steam to relaunch Valheim - as a normal, windowed game on Rohan's desktop.
    ///      (run-e2e.sh also puts a steam_appid.txt in the client's working directory, which makes Steam's own
    ///      answer false.)
    /// Any failure to install these guards KILLS the process at once, before the start scene can run.
    /// run-e2e.sh (safety.py) independently hashes ~/.config/unity3d/IronGate/Valheim before and after.
    /// </summary>
    internal static class ClientSafety
    {
        internal static int Trips;

        internal static bool Install(Harmony harmony)
        {
            string realHome = E2EEnv.RealHome;
            string saveDir = E2EEnv.SaveDir;
            string home = Environment.GetEnvironmentVariable("HOME") ?? "";
            string xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? "";
            string pdp = Application.persistentDataPath ?? "";

            if (string.IsNullOrEmpty(realHome) || string.IsNullOrEmpty(saveDir))
            {
                Report.Info("SAFETY: TERR_E2E_REALHOME and TERR_E2E_SAVEDIR must both be set for a client role.");
                return false;
            }
            string realConfig = Path.Combine(realHome, ".config");
            bool bad =
                string.IsNullOrEmpty(home) || Norm(home) == Norm(realHome) ||
                string.IsNullOrEmpty(xdg) || Under(xdg, realConfig) ||
                string.IsNullOrEmpty(pdp) || Under(pdp, realConfig) ||
                Under(saveDir, realConfig);
            Report.Info($"SAFETY: HOME={home} XDG_CONFIG_HOME={xdg} persistentDataPath={pdp} saveDir={saveDir}");
            if (bad)
            {
                Report.Info($"SAFETY: a redirect still points at the real home ({realHome}); this client would write Rohan's Valheim data.");
                return false;
            }

            Directory.CreateDirectory(saveDir);
            Utils.SetSaveDataPath(saveDir);
            SaveSystem.SetSessionFlags(SaveSystemSessionFlags.DontSaveServerList | SaveSystemSessionFlags.DontSaveBlockedList);

            int guarded = 0;
            MethodInfo restart = AccessTools.Method(typeof(Steamworks.SteamAPI), nameof(Steamworks.SteamAPI.RestartAppIfNecessary));
            if (Prefix(harmony, restart, nameof(NoSteamRelaunch)) == 0)
            {
                Report.Info("SAFETY: could not guard SteamAPI.RestartAppIfNecessary.");
                return false;
            }
            try { Assembly.Load("Splatform.Steam"); }
            catch (Exception ex) { Report.Info("SAFETY: Splatform.Steam did not load: " + ex.Message); }
            Type cloud = AccessTools.TypeByName("Splatform.Steam.SteamCloud");
            if (cloud != null)
            {
                guarded += Prefix(harmony, AccessTools.PropertyGetter(cloud, "IsEnabled"), nameof(CloudDisabled));
                foreach (string m in new[] { "WriteFile", "DeleteFile", "MoveFile", "CopyFile" })
                    guarded += Prefix(harmony, AccessTools.Method(cloud, m), nameof(CloudWriteRefused));
            }
            Type ach = AccessTools.TypeByName("Splatform.Steam.SteamAchievements");
            if (ach != null)
            {
                foreach (string m in new[] { "Unlock", "IncrementStatProgress", "SetStatProgress", "ResetAchievement", "ResetAllAchievements", "Synchronize" })
                    guarded += Prefix(harmony, AccessTools.Method(ach, m), nameof(AchievementSkipped));
            }
            if (cloud == null || guarded < 5)
            {
                Report.Info($"SAFETY: could not guard Steam Cloud (type {(cloud == null ? "missing" : "found")}, {guarded} guards).");
                return false;
            }

            Housekeeping();
            // (Utils.GetSaveDataPath cannot be asked yet: it consults the distribution platform, which the start
            // scene initialises after this Awake. C02-redirect checks it once the main menu is up.)
            Report.Info($"SAFETY: Steam relaunch guard + {guarded} Steam Cloud / achievement guards installed; saves -> {saveDir}");
            return true;
        }

        private static int Prefix(Harmony harmony, MethodBase target, string prefix)
        {
            if (target == null) return 0;
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(ClientSafety), prefix) { priority = Priority.First });
            return 1;
        }

        private static bool NoSteamRelaunch(ref bool __result)
        {
            Report.Info("SAFETY: SteamAPI.RestartAppIfNecessary answered false without asking Steam (no relaunch through Steam)");
            __result = false;
            return false;
        }

        /// <summary>Refusing to run must be immediate: Application.Quit lets the start scene run its frame first.</summary>
        internal static void KillNow(string why)
        {
            Report.Info("SAFETY: killing this client now - " + why);
            Report.Done();
            System.Diagnostics.Process.GetCurrentProcess().Kill();
        }

        private static bool CloudDisabled(ref bool __result)
        {
            __result = false;
            return false;
        }

        private static bool CloudWriteRefused(MethodBase __originalMethod, object[] __args, ref bool __result)
        {
            Trips++;
            Report.Info($"SAFETY-TRIP: refused Steam Cloud {__originalMethod.Name}({(__args.Length > 0 ? __args[0] : "")})");
            __result = false;
            return false;
        }

        private static bool AchievementSkipped(MethodBase __originalMethod)
        {
            Report.Info($"SAFETY: skipped Steam achievements/stats call {__originalMethod.Name}");
            return false;
        }

        /// <summary>Once a second: no sound out of a test client, and a modest frame rate.</summary>
        internal static void Housekeeping()
        {
            try
            {
                Type listener = Type.GetType("UnityEngine.AudioListener, UnityEngine.AudioModule");
                listener?.GetProperty("volume", BindingFlags.Public | BindingFlags.Static)?.SetValue(null, 0f);
                listener?.GetProperty("pause", BindingFlags.Public | BindingFlags.Static)?.SetValue(null, true);
            }
            catch { }
            if (Application.targetFrameRate != 30) Application.targetFrameRate = 30;
            if (QualitySettings.vSyncCount != 0) QualitySettings.vSyncCount = 0;
        }

        private static string Norm(string p) => string.IsNullOrEmpty(p) ? "" : Path.GetFullPath(p).TrimEnd('/');

        /// <summary>Is <paramref name="path"/> the same as, or inside, <paramref name="root"/>?</summary>
        private static bool Under(string path, string root)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root)) return false;
            string a = Norm(path) + "/";
            string b = Norm(root) + "/";
            return a.StartsWith(b, StringComparison.Ordinal);
        }
    }
}
