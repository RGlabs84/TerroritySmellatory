using System;
using System.Collections;
using System.IO;
using Splatform;
using UnityEngine;

namespace TerrE2E
{
    /// <summary>Main menu -> throwaway character -> join the run's server -> in the world; and back out.</summary>
    internal static class ClientBoot
    {
        private static bool _menuChecked;

        /// <summary>Waits for the start scene (FejdStartup) and, the first time, proves the redirects.</summary>
        internal static IEnumerator ReachMenu(Box<bool> ok)
        {
            var until = new Box<bool>();
            yield return Flow.Until(() => FejdStartup.instance != null, 240f, until);
            if (!until.Value)
            {
                Report.Fail("C01-menu", "main menu (FejdStartup) reached", "FejdStartup.instance never appeared within 240 s");
                ok.Value = false;
                yield break;
            }
            // Start() runs the frame after Awake; give the menu a moment to finish its own set-up.
            yield return Flow.Seconds(2f);
            ok.Value = true;
            if (_menuChecked) yield break;
            _menuChecked = true;

            string persona = "?", steamId = "?";
            try
            {
                persona = Steamworks.SteamFriends.GetPersonaName();
                steamId = PlatformManager.DistributionPlatform?.LocalUser?.PlatformUserID.ToString() ?? "?";
            }
            catch (Exception ex) { persona = "error " + ex.Message; }
            Report.Check("C01-menu", $"main menu (FejdStartup) reached with SteamAPI initialised (persona '{persona}', {steamId})",
                SteamManager.Initialized, "SteamManager.Initialized is false");

            string save = Utils.GetSaveDataPath(FileHelpers.FileSource.Local);
            string pdp = Application.persistentDataPath;
            string real = Path.Combine(E2EEnv.RealHome ?? "/nonexistent", ".config", "unity3d");
            bool redirected = save == E2EEnv.SaveDir && !pdp.StartsWith(real, StringComparison.Ordinal) && !FileHelpers.CloudStorageSupportedAndEnabled;
            Report.Check("C02-redirect", $"saves -> {save}, persistentDataPath/prefs -> {pdp}, Steam Cloud off",
                redirected, $"save={save} expected={E2EEnv.SaveDir} pdp={pdp} cloudEnabled={FileHelpers.CloudStorageSupportedAndEnabled}");
        }

        /// <summary>Creates (once) the local character <paramref name="name"/>, selects it and joins TERR_E2E_JOIN.</summary>
        internal static IEnumerator Join(string name, string id, Box<bool> ok)
        {
            ok.Value = false;
            string file = name.ToLowerInvariant();
            string path = SaveSystem.GetCharacterPath(FileHelpers.FileSource.Local, file);
            if (!File.Exists(path))
            {
                var profile = new PlayerProfile(file, FileHelpers.FileSource.Local);
                profile.SetName(name);
                profile.m_firstSpawn = false;   // no Valkyrie ride and no intro text to dismiss
                bool saved = profile.Save();
                SaveSystem.InvalidateCache(SaveDataType.Character);
                if (!Report.Check($"{id}-character", $"throwaway character '{name}' created as FileSource.Local at {path}",
                        saved && File.Exists(path) && path.StartsWith(E2EEnv.SaveDir, StringComparison.Ordinal),
                        $"saved={saved} exists={File.Exists(path)}"))
                    yield break;
            }

            FejdStartup fs = FejdStartup.instance;
            if (fs == null)
            {
                Report.Fail($"{id}-join", "join the server", "no FejdStartup");
                yield break;
            }
            string target = E2EEnv.Join;
            Report.Info($"joining {target} as {name} (password from -password)");
            fs.m_joinServer = new ServerJoinData(new ServerJoinDataDedicated(target));
            fs.SelectCharacter(file, FileHelpers.FileSource.Local);
            float start = Time.realtimeSinceStartup;
            fs.JoinServer();

            // In the world: the local player exists and has settled. Any connection error, or the start scene
            // coming back, is a failed join.
            bool seenMain = false;
            ZNet.ConnectionStatus last = ZNet.ConnectionStatus.None;
            while (true)
            {
                if (Time.realtimeSinceStartup - start > 240f)
                {
                    Report.Fail($"{id}-join", $"joined {target} as {name}", $"no local player after 240 s (connection status {ZNet.GetConnectionStatus()})");
                    yield break;
                }
                ZNet.ConnectionStatus st = ZNet.instance != null ? ZNet.GetConnectionStatus() : last;
                if (st != last)
                {
                    Report.Info($"connection status {last} -> {st}");
                    last = st;
                }
                if (st >= ZNet.ConnectionStatus.ErrorVersion)
                {
                    Report.Fail($"{id}-join", $"joined {target} as {name}", $"the connection failed: {st}");
                    yield break;
                }
                if (Game.instance != null) seenMain = true;
                if (seenMain && Game.instance == null && FejdStartup.instance != null)
                {
                    Report.Fail($"{id}-join", $"joined {target} as {name}", $"back in the main menu (last status {last})");
                    yield break;
                }
                Player me = Player.m_localPlayer;
                if (me != null && !me.IsTeleporting() && ZNetScene.instance != null && ZNetScene.instance.IsAreaReady(me.transform.position))
                    break;
                yield return null;
            }
            yield return Flow.Seconds(3f);   // let the ground under the spawn point finish loading
            Player p = Player.m_localPlayer;
            ok.Value = Report.Check($"{id}-join", $"joined {target} as {name}: Player.m_localPlayer in the world at {World.P(p.transform.position)}, player id {p.GetPlayerID()}, {Time.realtimeSinceStartup - start:0} s",
                p != null && p.GetPlayerName() == name, $"local player name '{p?.GetPlayerName()}'");
        }

        /// <summary>Back to the main menu (solo mode, between characters).</summary>
        internal static IEnumerator Logout(Box<bool> ok)
        {
            if (Game.instance != null) Game.instance.Logout();
            var until = new Box<bool>();
            yield return Flow.Until(() => Game.instance == null && FejdStartup.instance != null, 120f, until);
            ok.Value = until.Value;
            if (!until.Value) Report.Fail("C09-logout", "logged out to the main menu", "the start scene did not come back within 120 s");
            else yield return Flow.Seconds(2f);
        }
    }
}
