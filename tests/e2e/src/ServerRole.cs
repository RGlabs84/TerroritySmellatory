using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using TerroritySmellatory;
using UnityEngine;

namespace TerrE2E
{
    /// <summary>
    /// The dedicated server's part: the rig's ONE test seam, observers that count what arrives, and checks of the
    /// server's real state - its TerritoryStore and, independently, the JSON file it wrote to disk.
    /// </summary>
    internal static class ServerRole
    {
        private sealed class SeenPeer
        {
            public string Host;
            public bool VanillaAdmin;
            public bool ModAdmin;
            public long PlayerId;
        }

        private static readonly Dictionary<string, int> _requests = new Dictionary<string, int>();
        private static readonly List<string> _requestLog = new List<string>();
        private static readonly Dictionary<string, SeenPeer> _seen = new Dictionary<string, SeenPeer>();
        private static int _maxTogether;
        private static int _seamHits;

        // ─────────────────────────────────────────────────────────────
        // THE TEST SEAM - the one place the rig bends reality
        // ─────────────────────────────────────────────────────────────
        //
        // Both test clients run on the one Steam account of this machine, so the server's adminlist.txt - which
        // lists Steam ids - makes BOTH of them admins. To have a genuine NON-admin visitor, the server's answer
        // to "is this connection an admin?" is overridden for the peer whose character is named TSPlain, in
        // the mod's own TerritoryServer.IsAdmin (the single function every server decision and every snapshot's
        // YouAreAdmin flag comes from). Nothing on the clients is touched: TSPlain learns "not an admin" from
        // the server, exactly as a real non-admin would. TSAdmin stays an admin by the real adminlist.txt.

        private static void IsAdminSeam(ZNetPeer peer, ref bool __result)
        {
            if (!__result || peer == null || peer.m_playerName != E2EEnv.PlainName) return;
            __result = false;
            if (_seamHits++ == 0) Report.Info($"TEST SEAM: TerritoryServer.IsAdmin answers false for {peer.m_playerName} (vanilla adminlist says admin: same Steam id as TSAdmin)");
        }

        /// <summary>Observer only: counts every territory request by the sender's character name.</summary>
        private static void ObserveRequest(ZNetPeer peer, ZPackage pkg)
        {
            string who = peer?.m_playerName ?? "(host)";
            string op = "?";
            try
            {
                Protocol.Request req = Protocol.ReadRequest(new ZPackage(pkg.GetArray()));
                op = req != null ? req.Op.ToString() : "null";
            }
            catch (Exception ex) { op = "unreadable: " + ex.Message; }
            _requests[who] = (_requests.TryGetValue(who, out int n) ? n : 0) + 1;
            _requestLog.Add($"{who}:{op}");
            Report.Info($"request {op} arrived from {who}");
        }

        internal static void Install(Harmony harmony)
        {
            if (E2EEnv.Role != "server") return;
            harmony.Patch(AccessTools.Method(typeof(TerritoryServer), nameof(TerritoryServer.IsAdmin)),
                postfix: new HarmonyMethod(typeof(ServerRole), nameof(IsAdminSeam)));
            harmony.Patch(AccessTools.Method(typeof(TerritoryServer), nameof(TerritoryServer.OnRequest)),
                prefix: new HarmonyMethod(typeof(ServerRole), nameof(ObserveRequest)) { priority = Priority.First });
            Report.Info("server: test seam on TerritoryServer.IsAdmin (TSPlain only) and a request observer installed");
            Scenario040.SmallParts();
        }

        // ─────────────────────────────────────────────────────────────

        private static IEnumerator WaitForStore(Box<bool> ok)
        {
            yield return Flow.Until(() => ZNet.instance != null && ZNet.instance.IsServer() && ZNet.World != null &&
                                          !string.IsNullOrEmpty(TerritoryStore.FilePath) && TerritoryStore._worldName != null, 300f, ok);
        }

        internal static IEnumerator Run(E2EPlugin plugin)
        {
            var ok = new Box<bool>();
            yield return WaitForStore(ok);
            if (!ok.Value)
            {
                Report.Fail("S01-store", "the territory store loaded for the world", "ZNet/world/store never came up within 300 s");
                Report.Done();
                yield break;
            }
            if (E2EEnv.Role == "server-restart")
            {
                yield return Flow.Step("R00-reload", "reload", Restart());
                Report.Done();
                yield break;
            }

            PluginInfo mod = Chainloader.PluginInfos.TryGetValue(E2EPlugin.ModGuid, out PluginInfo pi) ? pi : null;
            Report.Check("S01-mod-loaded", $"TerroritySmellatory {mod?.Metadata.Version} loaded on the dedicated server from {mod?.Location}",
                mod != null && mod.Instance != null, "not in the chainloader");
            Report.Check("S02-selfcheck", "the territory file format self-check ran and passed in this process", TerritoryStore._selfChecked && TerritoryStore._selfCheckOk,
                $"checked={TerritoryStore._selfChecked} ok={TerritoryStore._selfCheckOk} problem='{TerritoryStore._selfCheckProblem}'");
            string world = ZNet.instance.GetWorldName();
            string expectedPath = Path.Combine(Paths.ConfigPath, "TerroritySmellatory", "worlds", world + ".json");
            Report.Check("S03-store", $"the store for world '{world}' is writable and saves to {TerritoryStore.FilePath}",
                TerritoryStore.Writable && Path.GetFullPath(TerritoryStore.FilePath) == Path.GetFullPath(expectedPath),
                $"writable={TerritoryStore.Writable} reason='{TerritoryStore.ReadOnlyReason}' path={TerritoryStore.FilePath} expected={expectedPath}");
            RunDir.Signal("server.ready", new { world, file = TerritoryStore.FilePath, loaded = TerritoryStore.Territories.Count });

            bool createdChecked = false, phase1Checked = false, subChecked = false;
            float start = Time.realtimeSinceStartup;
            while (!RunDir.Has("plain.final-done"))
            {
                if (Time.realtimeSinceStartup - start > 1200f)
                {
                    Report.Fail("S20-final", "the clients finished the scenario", "plain.final-done never arrived (20 min)");
                    break;
                }
                TrackPeers();
                if (!createdChecked && RunDir.Has("admin.created"))
                {
                    createdChecked = true;
                    yield return Flow.Seconds(1f);
                    yield return Flow.Step("S10-file-created", "file after creates", FileChecks("S10", afterCreates: true));
                }
                if (!phase1Checked && RunDir.Has("admin.phase1-done"))
                {
                    phase1Checked = true;
                    yield return Flow.Seconds(1f);
                    yield return Flow.Step("S12-file-updated", "file after the rules update", FileChecks("S12", afterCreates: false));
                }
                if (!subChecked && RunDir.Has("admin.sub"))
                {
                    subChecked = true;
                    yield return Flow.Seconds(1f);
                    yield return Flow.Step("S40-sub", "sub-territories", Scenario040.ServerChecks());
                    Scenario040.ServerParts();
                }
                yield return Flow.Seconds(0.5f);
            }
            yield return Flow.Seconds(2f);
            TrackPeers();
            yield return Flow.Step("S20-final", "final state", Final());
            Report.Done();
        }

        private static void TrackPeers()
        {
            int together = 0;
            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
            {
                if (peer == null || !peer.IsReady() || peer.m_characterID.IsNone()) continue;
                together++;
                string host = "";
                try { host = peer.m_socket.GetHostName(); } catch { }
                if (!_seen.TryGetValue(peer.m_playerName ?? "?", out SeenPeer s))
                {
                    s = new SeenPeer();
                    _seen[peer.m_playerName ?? "?"] = s;
                    Report.Info($"peer in the world: {peer.m_playerName} host={host} uid={peer.m_uid}");
                }
                s.Host = host;
                s.PlayerId = peer.m_playerID;
                s.VanillaAdmin = ZNet.instance.IsAdmin(host);
                s.ModAdmin = TerritoryServer.IsAdmin(peer);
            }
            _maxTogether = Math.Max(_maxTogether, together);
        }

        // ─────────────────────────────────────────────────────────────
        // The file on disk, parsed independently of the mod's own reader
        // ─────────────────────────────────────────────────────────────

        private static JObject ReadFile(string path) => JObject.Parse(File.ReadAllText(path));

        /// <summary>The file parsed, or null with the reason (missing, unreadable, not JSON).</summary>
        private static JObject TryRead(string path, out string error)
        {
            error = null;
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    error = "no file";
                    return null;
                }
                return ReadFile(path);
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return null;
            }
        }

        private static List<JObject> E2E(JObject file) =>
            ((file?["territories"] as JArray) ?? new JArray()).OfType<JObject>().Where(o => ((string)o["name"] ?? "").StartsWith("E2E")).ToList();

        /// <summary>
        /// A territory as the SERVER'S STORE holds it, written by the driver itself with the file's key names (never
        /// through the mod's TerritoryJson), so it can be held against the file and against a restarted store.
        /// </summary>
        private static JObject StoreRecord(Territory t) => new JObject
        {
            ["id"] = t.Id,
            ["name"] = t.Name,
            ["shape"] = t.Kind.ToString(),
            ["x"] = t.X, ["y"] = t.Y, ["z"] = t.Z,
            ["halfX"] = t.HalfX, ["halfZ"] = t.HalfZ, ["yaw"] = t.Yaw,
            ["color"] = Territory.ColorToHex(t.Color),
            ["ownerId"] = t.OwnerId,
            ["ownerName"] = t.OwnerName ?? "",
            ["members"] = new JArray(t.Members.Select(m => new JObject { ["id"] = m.Id, ["name"] = m.Name ?? "" })),
            ["flags"] = new JArray(Enum.GetValues(typeof(TerritoryFlags)).Cast<TerritoryFlags>()
                .Where(f => f != TerritoryFlags.None && t.Has(f)).Select(f => f.ToString())),
            ["pvp"] = t.PvP.ToString(),
            ["priority"] = t.Priority,
            ["healthRegen"] = t.HealthRegen, ["staminaRegen"] = t.StaminaRegen, ["eitrRegen"] = t.EitrRegen,
            ["carryBonus"] = t.CarryBonus, ["armorBonus"] = t.ArmorBonus,
            ["enterMessage"] = t.EnterMessage ?? "", ["leaveMessage"] = t.LeaveMessage ?? "",
            ["createdBy"] = t.CreatedBy ?? "", ["createdTicks"] = t.CreatedTicks,
            ["revision"] = t.Revision
        };

        /// <summary>
        /// null when the record (the file's, or a StoreRecord) and the store's territory agree on every persisted
        /// field; else the first difference. A key missing from the record, or of the wrong type, is a difference
        /// with its exception as the reason, never an exception that empties the FAIL line.
        /// </summary>
        private static string Diff(JObject o, Territory t)
        {
            try { return DiffFields(o, t); }
            catch (Exception ex) { return $"the record could not be compared (a key missing or of the wrong type): {ex.GetType().Name}: {ex.Message}"; }
        }

        private static string DiffFields(JObject o, Territory t)
        {
            var flags = new HashSet<string>(((JArray)o["flags"] ?? new JArray()).Select(f => (string)f));
            var storeFlags = new HashSet<string>(Enum.GetValues(typeof(TerritoryFlags)).Cast<TerritoryFlags>()
                .Where(f => f != TerritoryFlags.None && t.Has(f)).Select(f => f.ToString()));
            var members = ((JArray)o["members"] ?? new JArray()).OfType<JObject>().Select(m => (long)m["id"] + ":" + ((string)m["name"] ?? "")).OrderBy(x => x, StringComparer.Ordinal);
            if ((int)o["id"] != t.Id) return "id";
            if ((string)o["name"] != t.Name) return $"name '{o["name"]}' vs '{t.Name}'";
            if ((string)o["shape"] != t.Kind.ToString()) return $"shape {o["shape"]} vs {t.Kind}";
            if (Mathf.Abs((float)o["x"] - t.X) > 0.001f || Mathf.Abs((float)o["z"] - t.Z) > 0.001f || Mathf.Abs((float)o["y"] - t.Y) > 0.001f) return "centre";
            if (Mathf.Abs((float)o["halfX"] - t.HalfX) > 0.001f || Mathf.Abs((float)o["halfZ"] - t.HalfZ) > 0.001f) return "size";
            if (Mathf.Abs((float)o["yaw"] - t.Yaw) > 0.001f) return "yaw";
            if (!flags.SetEquals(storeFlags)) return $"flags [{string.Join(",", flags)}] vs [{string.Join(",", storeFlags)}]";
            if ((string)o["pvp"] != t.PvP.ToString()) return "pvp";
            if ((int)o["priority"] != t.Priority) return "priority";
            if (!members.SequenceEqual(t.Members.Select(m => m.Id + ":" + (m.Name ?? "")).OrderBy(x => x, StringComparer.Ordinal)))
                return $"members [{string.Join(",", members)}] vs [{string.Join(",", t.Members.Select(m => m.Id + ":" + m.Name))}]";
            if ((long)o["ownerId"] != t.OwnerId || (string)o["ownerName"] != (t.OwnerName ?? "")) return "owner";
            if (Mathf.Abs((float)o["healthRegen"] - t.HealthRegen) > 0.001f || Mathf.Abs((float)o["carryBonus"] - t.CarryBonus) > 0.001f ||
                Mathf.Abs((float)o["armorBonus"] - t.ArmorBonus) > 0.001f || Mathf.Abs((float)o["staminaRegen"] - t.StaminaRegen) > 0.001f ||
                Mathf.Abs((float)o["eitrRegen"] - t.EitrRegen) > 0.001f) return "sanctuary values";
            if ((string)o["enterMessage"] != t.EnterMessage || (string)o["leaveMessage"] != t.LeaveMessage) return "messages";
            if ((string)o["createdBy"] != (t.CreatedBy ?? "") || (long)o["createdTicks"] != t.CreatedTicks) return "history (createdBy / createdTicks)";
            if ((int)o["revision"] != t.Revision) return $"revision {o["revision"]} vs {t.Revision}";
            if ((string)o["color"] != Territory.ColorToHex(t.Color)) return $"color {o["color"]}";
            return null;
        }

        private static string FileVsStore(JObject file)
        {
            try { return FileVsStoreFields(file); }
            catch (Exception ex) { return $"the file could not be compared (a key missing or of the wrong type): {ex.GetType().Name}: {ex.Message}"; }
        }

        private static string FileVsStoreFields(JObject file)
        {
            List<JObject> disk = E2E(file);
            List<Territory> store = TerritoryStore.Territories.Where(t => t.Name.StartsWith("E2E")).ToList();
            if (disk.Count != store.Count) return $"file has {disk.Count} E2E territories, the store {store.Count}";
            foreach (JObject o in disk)
            {
                Territory t = TerritoryStore.Get((int)o["id"]);
                if (t == null) return $"#{o["id"]} in the file but not in the store";
                string d = Diff(o, t);
                if (d != null) return $"#{t.Id}: {d}";
            }
            if ((int)file["nextId"] <= store.Select(t => t.Id).DefaultIfEmpty(0).Max()) return "nextId not past the largest id";
            return null;
        }

        /// <summary>The file against the admin's INTENT (the scenario's specs), not only against the store.</summary>
        private static string FileVsSpecs(JObject file, bool rectUpdated)
        {
            List<JObject> disk = E2E(file);
            foreach (Spec s in Layout.All)
            {
                JObject o = disk.FirstOrDefault(x => (string)x["name"] == s.Name);
                if (o == null) return $"'{s.Name}' missing from the file";
                TerritoryFlags expect = s.Flags | (rectUpdated && s == Layout.Rect ? TerritoryFlags.HarvestProtect : 0);
                var flags = new HashSet<string>(((JArray)o["flags"]).Select(f => (string)f));
                var want = new HashSet<string>(Enum.GetValues(typeof(TerritoryFlags)).Cast<TerritoryFlags>()
                    .Where(f => f != TerritoryFlags.None && (expect & f) != 0).Select(f => f.ToString()));
                if (!flags.SetEquals(want)) return $"'{s.Name}' flags [{string.Join(",", flags)}], the admin set [{string.Join(",", want)}]";
                if ((string)o["pvp"] != s.PvP.ToString()) return $"'{s.Name}' pvp {o["pvp"]}";
                if ((string)o["shape"] != s.Kind.ToString()) return $"'{s.Name}' shape {o["shape"]}";
                if (Mathf.Abs((float)o["halfX"] - s.HalfX) > 0.001f) return $"'{s.Name}' halfX {o["halfX"]}";
                if (!((string)o["createdBy"] ?? "").StartsWith(E2EEnv.AdminName)) return $"'{s.Name}' createdBy '{o["createdBy"]}'";
            }
            return null;
        }

        private static IEnumerator FileChecks(string id, bool afterCreates)
        {
            string path = TerritoryStore.FilePath;
            var ok = new Box<bool>();
            string why = "";
            yield return Flow.Until(() => File.Exists(path) && (why = FileVsStore(ReadFile(path))) == null, 15f, ok);
            JObject file = TryRead(path, out string readError);
            Report.Check($"{id}a-file-vs-store", $"{Path.GetFileName(path)} on disk holds exactly the server's territories ({E2E(file).Count} E2E records, every field)",
                ok.Value, file == null ? $"no readable file at {path} ({readError})" : why);
            if (file == null)
            {
                // Every check still reports: without a file each one FAILS, it is never silently left out.
                Report.Fail($"{id}b-file-vs-admin", "the file holds what the admin created (shape, size, flags, PvP, creator)", $"no readable file at {path} ({readError})");
                if (afterCreates)
                    Report.Fail($"{id}c-bak", "a .bak exists after the second save and holds the previous save", $"no readable file at {path} ({readError})");
                yield break;
            }
            string intent;
            try { intent = FileVsSpecs(file, rectUpdated: !afterCreates); }
            catch (Exception ex) { intent = $"the file could not be compared (a key missing or of the wrong type): {ex.GetType().Name}: {ex.Message}"; }
            Report.Check($"{id}b-file-vs-admin", "the file holds what the admin created (shape, size, flags, PvP, creator)", intent == null, intent);

            if (afterCreates)
            {
                string bak = path + ".bak";
                JObject bakFile = TryRead(bak, out string bakError);
                int bakCount = bakFile != null ? E2E(bakFile).Count : -1;
                Report.Check($"{id}c-bak", $"a .bak exists after the second save and holds the previous save ({bakCount} E2E record(s) before the third create)",
                    bakFile != null && bakCount == 2, $"bak={(bakFile != null ? "read" : bakError)} records={bakCount}");
                Report.Info($"save path used: {(Plugin_ReplaceWarned() ? "copy/delete/move fallback (File.Replace failed)" : "File.Replace")}");
            }
        }

        private static bool Plugin_ReplaceWarned()
        {
            try { return TerritoryStore._replaceWarned; }
            catch { return false; }
        }

        private static IEnumerator Final()
        {
            string path = TerritoryStore.FilePath;
            yield return Flow.Seconds(1f);
            // The file's two checks never take the others with them: a missing or unreadable file FAILS S20a and S20b,
            // and S21-S24 (which do not read the file) still run and report.
            JObject file = TryRead(path, out string readError);
            string why = file == null ? $"no readable file at {path} ({readError})" : FileVsStore(file);
            Report.Check("S20a-final-file", "the final file matches the store after update, member add, reshape and remove", why == null, why);

            JObject created = RunDir.Read("admin.created");
            JObject plain = RunDir.Read("plain.ready");
            long plainId = plain != null ? (long)plain["id"] : 0L;
            List<JObject> disk = E2E(file);
            bool member = false, moved = false, squareGone = false;
            string contentError = file == null ? $"no readable file ({readError}); " : "";
            try
            {
                JObject circle = disk.FirstOrDefault(o => (string)o["name"] == Layout.CircleName);
                JObject rect = disk.FirstOrDefault(o => (string)o["name"] == Layout.RectName);
                squareGone = file != null && created != null && created["square"] != null && disk.All(o => (int)o["id"] != (int)created["square"]);
                member = circle != null && ((circle["members"] as JArray) ?? new JArray()).OfType<JObject>().Any(m => (long)m["id"] == plainId);
                JObject layout = RunDir.Read("layout");
                moved = rect != null && layout != null && Mathf.Abs((float)rect["x"] - (float)layout["rect2"]["x"]) < 0.05f &&
                        Mathf.Abs((float)rect["halfX"] - Layout.ReshapeHalfX) < 0.001f && Mathf.Abs((float)rect["yaw"] - Layout.ReshapeYaw) < 0.01f;
            }
            catch (Exception ex) { contentError += $"the file could not be read field by field: {ex.GetType().Name}: {ex.Message}; "; }
            Report.Check("S20b-final-content", "on disk: TSPlain is a member of the circle, the rectangle moved (same id), the square is gone",
                member && moved && squareGone && disk.Count == 2, $"{contentError}member={member} moved={moved} squareGone={squareGone} records={disk.Count}");

            JObject combined = Features.Combine ? CombinedChecks(file, readError) : null;
            JObject barrier = Features.Barrier ? BarrierChecks(file, readError) : null;
            JObject doors = Features.Doors ? DoorChecks(file, readError) : null;
            JObject sub = Scenario040.ServerFinal(file, readError);

            int fromPlain = _requests.TryGetValue(E2EEnv.PlainName, out int p) ? p : 0;
            int fromAdmin = _requests.TryGetValue(E2EEnv.AdminName, out int a) ? a : 0;
            Report.Check("S21-no-request-from-plain", $"the server received 0 territory requests from TSPlain ({fromAdmin} from TSAdmin: {string.Join(", ", _requestLog)})",
                fromPlain == 0 && fromAdmin >= 7, $"from TSPlain={fromPlain} from TSAdmin={fromAdmin}");

            _seen.TryGetValue(E2EEnv.AdminName, out SeenPeer sa);
            _seen.TryGetValue(E2EEnv.PlainName, out SeenPeer sp);
            Report.Check("S22-admin-by-server", "TSAdmin is an admin by the server's own adminlist (vanilla ZNet.IsAdmin and the mod agree)",
                sa != null && sa.VanillaAdmin && sa.ModAdmin, sa == null ? "never seen" : $"vanilla={sa.VanillaAdmin} mod={sa.ModAdmin} host={sa.Host}");
            Report.Check("S23-seam", "the test seam made TSPlain a non-admin for the mod only (vanilla still lists its shared Steam id)",
                sp != null && !sp.ModAdmin, sp == null ? "never seen" : $"vanilla={sp.VanillaAdmin} mod={sp.ModAdmin} host={sp.Host}");
            int want = E2EEnv.ExpectedClients;
            Report.Check("S24-peers", $"{want} player(s) in the world at the same time (mode {E2EEnv.Mode})", _maxTogether == want, $"max together {_maxTogether}");

            // What the restarted server must load back: THIS server's store as it stands at the end, written by the
            // driver (StoreRecord), not a copy of the file. Copying the file made R01 compare the file with its own
            // reload, so a field the save dropped (members, say) came back "equal" after the restart.
            var expect = new JArray(TerritoryStore.Territories.Where(t => t.Name.StartsWith("E2E")).Select(StoreRecord));
            var expected = new JObject { ["world"] = ZNet.instance.GetWorldName(), ["territories"] = expect, ["all"] = TerritoryStore.Territories.Count };
            if (combined != null) expected["combined"] = combined;
            if (barrier != null) expected["barrier"] = barrier;
            if (doors != null) expected["doors"] = doors;
            if (sub != null) expected["sub"] = sub;
            RunDir.Signal("server.expected", expected);
        }

        // ─────────────────────────────────────────────────────────────
        // 0.2.0: the combined territory, in the store and in the file ("parts"); the restart checks it again (R30)
        // ─────────────────────────────────────────────────────────────

        private static bool SameParts(List<Features.Part> a, List<Features.Part> b) => a.Count == b.Count && a.Zip(b, (x, y) => x.Same(y)).All(x => x);

        /// <summary>
        /// S30: the SERVER's store holds A with two or more parts (A's ground then B's, as the admin's index showed them) and
        /// B is gone. S31: the file on disk, parsed independently of the mod's reader, has a "parts" array for A that says the
        /// same (the parts BEYOND the first; the first part is A's own x/z/half keys), and no record for B. Returns what the
        /// restarted server must load back (R30), or null when the admin's combine did not complete (both FAIL then).
        /// </summary>
        private static JObject CombinedChecks(JObject file, string readError)
        {
            JObject c = RunDir.Read("admin.combined");
            if (c == null || c["ok"] == null || !(bool)c["ok"])
            {
                string why = "the admin's combine did not complete (admin.combined " + (c == null ? "missing" : "says not ok") + "; see A20a / A20b)";
                Report.Fail("S30-combined-store", "the server's store holds the combined territory with its parts and B is gone", why);
                Report.Fail("S31-parts-file", "the territory file has a \"parts\" array for the combined territory", why);
                return null;
            }

            int aId = (int)c["aId"], bId = (int)c["bId"];
            string aName = (string)c["aName"];
            List<Features.Part> want = ((JArray)c["parts"]).OfType<JObject>().Select(Features.Part.FromJson).ToList();
            Territory a = TerritoryStore.Get(aId);
            List<Features.Part> have = a != null ? Features.Parts(a) : new List<Features.Part>();
            int named = TerritoryStore.Territories.Count(t => t.Name.StartsWith("TS Comb"));
            Report.Check("S30-combined-store",
                $"the server's store holds '{aName}' (#{aId}) with {have.Count} parts [{string.Join(" ; ", have)}] (A's ground, then B's) and '{(string)c["bName"]}' (#{bId}) is gone",
                a != null && a.Name == aName && have.Count >= 2 && SameParts(have, want) && TerritoryStore.Get(bId) == null && named == 1,
                $"A in store: {a != null}, parts {have.Count} vs the admin's {want.Count} (same {SameParts(have, want)}), B in store: {TerritoryStore.Get(bId) != null}, records named 'TS Comb...': {named}");

            string fileWhy;
            try
            {
                JObject rec = file == null ? null : ((JArray)file["territories"]).OfType<JObject>().FirstOrDefault(o => (int)o["id"] == aId);
                JArray extra = rec?["parts"] as JArray;
                if (file == null) fileWhy = $"no readable file ({readError})";
                else if (rec == null) fileWhy = $"#{aId} is not in the file";
                else if (extra == null) fileWhy = $"#{aId} has no \"parts\" key (keys: {string.Join(",", rec.Properties().Select(p => p.Name))})";
                else
                {
                    var onDisk = new List<Features.Part> { new Features.Part { Kind = (string)rec["shape"], X = (float)rec["x"], Z = (float)rec["z"], HalfX = (float)rec["halfX"], HalfZ = (float)rec["halfZ"], Yaw = (float)rec["yaw"] } };
                    onDisk.AddRange(extra.OfType<JObject>().Select(Features.Part.FromJson));
                    fileWhy = extra.Count != have.Count - 1 ? $"\"parts\" has {extra.Count} entries, the store has {have.Count} parts (so {have.Count - 1} beyond the first)"
                        : !SameParts(onDisk, have) ? $"the file's ground [{string.Join(" ; ", onDisk)}] differs from the store's [{string.Join(" ; ", have)}]"
                        : ((JArray)file["territories"]).OfType<JObject>().Any(o => (int)o["id"] == bId) ? $"#{bId} (B) is still a record of its own in the file"
                        : null;
                }
            }
            catch (Exception ex) { fileWhy = $"the file could not be read field by field: {ex.GetType().Name}: {ex.Message}"; }
            Report.Check("S31-parts-file",
                $"the territory file (parsed independently) has a \"parts\" array for '{aName}' with the {Math.Max(0, have.Count - 1)} part(s) beyond the first, equal to the store's, and no record for B",
                fileWhy == null, fileWhy);

            return new JObject
            {
                ["id"] = aId, ["bId"] = bId, ["name"] = aName,
                ["revision"] = a?.Revision ?? 0,
                ["parts"] = new JArray(have.Select(p => p.ToJson()))
            };
        }

        // ─────────────────────────────────────────────────────────────
        // 0.3.0: the runic barrier's flags, in the store and in the file (version 3, flags by name); the restart checks them again (R31)
        // ─────────────────────────────────────────────────────────────

        private static HashSet<string> StoreFlagNames(Territory t) =>
            new HashSet<string>(Enum.GetValues(typeof(TerritoryFlags)).Cast<TerritoryFlags>().Where(f => f != TerritoryFlags.None && t.Has(f)).Select(f => f.ToString()));

        private static readonly string[] BarrierKeys = { "barrier", "guard", "control" };

        private static string[] ExpectedFlagNames(string key) =>
            key == "barrier" ? Scenario030.BarrierFlagNames : key == "guard" ? Scenario030.GuardFlagNames : Scenario030.ControlFlagNames;

        /// <summary>
        /// S31a: the SERVER's store holds the three territories with exactly the flags the admin marked them with (compared with
        /// names written out by hand, never with the enum's own spelling of what it was given), RunicBarrier is 1 &lt;&lt; 11 and
        /// BarrierKeepsOutVisitors 1 &lt;&lt; 12, TSPlain is a member of the guard (the phase 3 add reached the server), and no
        /// territory in the whole store breaks the prerequisites (RunicBarrier needs NoMonsterSpawns, BarrierKeepsOutVisitors needs
        /// RunicBarrier). S31b: the file on disk, parsed independently of the mod's reader, says "version": 3 and carries the same
        /// flag NAMES. Returns what the restarted server must load back (R31), or null when the admin's marking did not complete.
        /// </summary>
        private static JObject BarrierChecks(JObject file, string readError)
        {
            JObject c = RunDir.Read("admin.barriers");
            if (c == null || c["ok"] == null || !(bool)c["ok"])
            {
                string why = "the admin's barrier territories were not all created (admin.barriers " + (c == null ? "missing" : "says not ok") + "; see A30a / A30b / A30c)";
                Report.Fail("S31a-barrier-store", "the server's store holds the barrier, the guard and the control with their flags", why);
                Report.Fail("S31b-barrier-file", "the territory file says version 3 or more and carries the barrier flags by name", why);
                return null;
            }

            long plainId = RunDir.Read("plain.ready") is JObject pr && pr["id"] != null ? (long)pr["id"] : 0L;
            var problems = new List<string>();
            var expected = new JArray();
            var described = new List<string>();
            foreach (string key in BarrierKeys)
            {
                int id = (int)c[key]["id"];
                string name = (string)c[key]["name"];
                string[] want = ExpectedFlagNames(key);
                Territory t = TerritoryStore.Get(id);
                if (t == null)
                {
                    problems.Add($"'{name}' (#{id}) is not in the store");
                    continue;
                }
                HashSet<string> have = StoreFlagNames(t);
                described.Add($"#{id} '{t.Name}' [{string.Join(",", have.OrderBy(x => x))}]");
                if (t.Name != name) problems.Add($"#{id} is named '{t.Name}', not '{name}'");
                if (!have.SetEquals(want)) problems.Add($"'{name}' carries [{string.Join(",", have.OrderBy(x => x))}], the admin marked it [{string.Join(",", want)}]");
                if (key == "guard" && (plainId == 0L || !t.IsMember(plainId))) problems.Add($"TSPlain ({plainId}) is not a member of the guard in the store");
                expected.Add(new JObject
                {
                    ["key"] = key,
                    ["id"] = id,
                    ["name"] = name,
                    ["flags"] = new JArray(want),
                    ["revision"] = t.Revision,
                    ["members"] = new JArray(t.Members.Select(m => m.Id).OrderBy(x => x))
                });
            }
            if (Features.HasBarrierFlag)
            {
                if ((int)Features.RunicBarrier != Scenario030.RunicBarrierBit) problems.Add($"RunicBarrier is {(int)Features.RunicBarrier}, the contract says {Scenario030.RunicBarrierBit}");
                if ((int)Features.KeepsOutVisitors != Scenario030.KeepsOutVisitorsBit) problems.Add($"BarrierKeepsOutVisitors is {(int)Features.KeepsOutVisitors}, the contract says {Scenario030.KeepsOutVisitorsBit}");
            }
            foreach (Territory t in TerritoryStore.Territories)
            {
                HashSet<string> f = StoreFlagNames(t);
                if (f.Contains("RunicBarrier") && !f.Contains("NoMonsterSpawns")) problems.Add($"#{t.Id} '{t.Name}' has RunicBarrier without NoMonsterSpawns");
                if (f.Contains("BarrierKeepsOutVisitors") && !f.Contains("RunicBarrier")) problems.Add($"#{t.Id} '{t.Name}' has BarrierKeepsOutVisitors without RunicBarrier");
            }
            Report.Check("S31a-barrier-store",
                $"the server's store holds the barrier, the guard (TSPlain a member) and the control with exactly the marked flags ({string.Join("; ", described)}), RunicBarrier = 1<<11, BarrierKeepsOutVisitors = 1<<12, and no territory breaks the flags' prerequisites",
                problems.Count == 0, string.Join("; ", problems));

            string fileWhy;
            try
            {
                if (file == null) fileWhy = $"no readable file ({readError})";
                else
                {
                    var bad = new List<string>();
                    // 3 is the version that added these two names; 0.3.2 writes 4 (one more flag name), and a later build more. The check is that the file
                    // is NEW ENOUGH to guard them against a 0.2.x rollback, not that it is one build's number.
                    if (file["version"] == null || (int)file["version"] < 3) bad.Add($"\"version\" is {file["version"]?.ToString() ?? "missing"}, wanted 3 or more");
                    List<JObject> recs = ((JArray)file["territories"]).OfType<JObject>().ToList();
                    foreach (JToken e in expected)
                    {
                        JObject rec = recs.FirstOrDefault(o => (int)o["id"] == (int)e["id"]);
                        if (rec == null) { bad.Add($"#{e["id"]} '{e["name"]}' is not in the file"); continue; }
                        HashSet<string> names = FileFlagNames(rec);
                        if (!names.SetEquals(((JArray)e["flags"]).Select(x => (string)x)))
                            bad.Add($"'{e["name"]}' has flags [{string.Join(",", names.OrderBy(x => x))}] in the file, wanted [{string.Join(",", ((JArray)e["flags"]).Select(x => (string)x))}]");
                        if ((string)e["key"] == "guard" && !((JArray)rec["members"]).OfType<JObject>().Any(m => (long)m["id"] == plainId))
                            bad.Add("the guard's members in the file do not include TSPlain");
                    }
                    fileWhy = bad.Count == 0 ? null : string.Join("; ", bad);
                }
            }
            catch (Exception ex) { fileWhy = $"the file could not be read field by field: {ex.GetType().Name}: {ex.Message}"; }
            Report.Check("S31b-barrier-file",
                "the territory file (parsed independently) says \"version\": 3 or more and lists RunicBarrier and BarrierKeepsOutVisitors by NAME on the barrier and the guard, only NoMonsterSpawns on the control, and TSPlain as the guard's member",
                fileWhy == null, fileWhy);

            return problems.Count == 0 || expected.Count == 3 ? new JObject { ["records"] = expected } : null;
        }

        /// <summary>R31: the restarted server loads the three territories back with the same flags (by name), members and revision.</summary>
        private static void RestartBarrier(JObject bm)
        {
            if (bm == null)
            {
                Report.Fail("R31-reload-barrier", "the restarted store holds the barrier territories with their flags", "the first server expected none (server.expected has none: the admin's marking did not complete, see S31a)");
                return;
            }
            var why = new List<string>();
            var described = new List<string>();
            foreach (JObject e in ((JArray)bm["records"]).OfType<JObject>())
            {
                Territory t = TerritoryStore.Get((int)e["id"]);
                if (t == null) { why.Add($"#{e["id"]} '{e["name"]}' did not load"); continue; }
                HashSet<string> have = StoreFlagNames(t);
                described.Add($"#{t.Id} '{t.Name}' [{string.Join(",", have.OrderBy(x => x))}]");
                if (t.Name != (string)e["name"]) why.Add($"#{t.Id} is named '{t.Name}'");
                if (!have.SetEquals(((JArray)e["flags"]).Select(x => (string)x))) why.Add($"'{t.Name}' loaded with [{string.Join(",", have.OrderBy(x => x))}], wanted [{string.Join(",", ((JArray)e["flags"]).Select(x => (string)x))}]");
                if (t.Revision != (int)e["revision"]) why.Add($"'{t.Name}' revision {t.Revision}, wanted {(int)e["revision"]}");
                if (!t.Members.Select(m => m.Id).OrderBy(x => x).SequenceEqual(((JArray)e["members"]).Select(x => (long)x))) why.Add($"'{t.Name}' members differ");
            }
            Report.Check("R31-reload-barrier",
                $"after the restart the store holds the barrier, the guard and the control with the same flags, members and revision as the first server's store ({string.Join("; ", described)})",
                why.Count == 0 && ((JArray)bm["records"]).Count == 3, string.Join("; ", why));
        }

        // ─────────────────────────────────────────────────────────────
        // 0.3.1: the auto-close doors' flag, in the store and in the file; the restart checks it again (R32)
        // ─────────────────────────────────────────────────────────────

        private static readonly string[] DoorKeys = { "doors", "nested", "off" };

        private static string[] ExpectedDoorFlagNames(string key) => key == "doors" ? Scenario031.DoorsFlagNames : Scenario031.OtherFlagNames;

        /// <summary>
        /// S32a: the SERVER's store holds the three territories with exactly the marked flags (names written out by hand), only the
        /// doors territory carries AutoCloseDoors, AutoCloseDoors is 1 &lt;&lt; 13, and the server's own AutoCloseDelay is the one
        /// run-e2e.sh wrote (else the door checks' clock would be someone else's). S32b: the file, parsed independently, lists
        /// AutoCloseDoors BY NAME on the doors territory and on no other record. Returns what the restart must load back (R32).
        /// </summary>
        private static JObject DoorChecks(JObject file, string readError)
        {
            JObject c = RunDir.Read("admin.doors");
            if (c == null || c["ok"] == null || !(bool)c["ok"])
            {
                string why = "the admin's door territories were not all created (admin.doors " + (c == null ? "missing" : "says not ok") + "; see A32a / A32b / A32c)";
                Report.Fail("S32a-doors-store", "the server's store holds the doors territory with AutoCloseDoors and the two without", why);
                Report.Fail("S32b-doors-file", "the territory file lists AutoCloseDoors by name on the doors territory only", why);
                return null;
            }

            var problems = new List<string>();
            var expected = new JArray();
            var described = new List<string>();
            foreach (string key in DoorKeys)
            {
                int id = (int)c[key]["id"];
                string name = (string)c[key]["name"];
                string[] want = ExpectedDoorFlagNames(key);
                Territory t = TerritoryStore.Get(id);
                if (t == null)
                {
                    problems.Add($"'{name}' (#{id}) is not in the store");
                    continue;
                }
                HashSet<string> have = StoreFlagNames(t);
                described.Add($"#{id} '{t.Name}' [{string.Join(",", have.OrderBy(x => x))}]");
                if (t.Name != name) problems.Add($"#{id} is named '{t.Name}', not '{name}'");
                if (!have.SetEquals(want)) problems.Add($"'{name}' carries [{string.Join(",", have.OrderBy(x => x))}], the admin marked it [{string.Join(",", want)}]");
                expected.Add(new JObject { ["key"] = key, ["id"] = id, ["name"] = name, ["flags"] = new JArray(want), ["revision"] = t.Revision });
            }
            if (Features.HasAutoCloseFlag && (int)Features.AutoCloseDoors != Scenario031.AutoCloseDoorsBit)
                problems.Add($"AutoCloseDoors is {(int)Features.AutoCloseDoors}, the contract says {Scenario031.AutoCloseDoorsBit}");
            float serverDelay = Features.AutoCloseDelayConfigured;
            if (Features.HasAutoCloseFlag && Math.Abs(serverDelay - E2EEnv.DoorDelay) > 0.01f)
                problems.Add($"the server's AutoCloseDelay is {serverDelay:0.##} s, not the {E2EEnv.DoorDelay:0.##} s run-e2e.sh wrote into its config");
            Report.Check("S32a-doors-store",
                $"the server's store holds the doors territory (AutoCloseDoors), the nested one and the one beside it (neither has it) with exactly the marked flags ({string.Join("; ", described)}), AutoCloseDoors = 1<<13, and the server's AutoCloseDelay is {serverDelay:0.##} s",
                problems.Count == 0, string.Join("; ", problems));

            string fileWhy;
            try
            {
                if (file == null) fileWhy = $"no readable file ({readError})";
                else
                {
                    var bad = new List<string>();
                    List<JObject> recs = ((JArray)file["territories"]).OfType<JObject>().ToList();
                    foreach (JToken e in expected)
                    {
                        JObject rec = recs.FirstOrDefault(o => (int)o["id"] == (int)e["id"]);
                        if (rec == null) { bad.Add($"#{e["id"]} '{e["name"]}' is not in the file"); continue; }
                        HashSet<string> names = FileFlagNames(rec);
                        if (!names.SetEquals(((JArray)e["flags"]).Select(x => (string)x)))
                            bad.Add($"'{e["name"]}' has flags [{string.Join(",", names.OrderBy(x => x))}] in the file, wanted [{string.Join(",", ((JArray)e["flags"]).Select(x => (string)x))}]");
                    }
                    int withFlag = recs.Count(o => FileFlagNames(o).Contains("AutoCloseDoors"));
                    if (withFlag != 1) bad.Add($"{withFlag} record(s) in the file carry AutoCloseDoors, wanted exactly the doors territory");
                    fileWhy = bad.Count == 0 ? null : string.Join("; ", bad);
                }
            }
            catch (Exception ex) { fileWhy = $"the file could not be read field by field: {ex.GetType().Name}: {ex.Message}"; }
            Report.Check("S32b-doors-file",
                "the territory file (parsed independently) lists \"AutoCloseDoors\" by NAME on the doors territory and on no other record",
                fileWhy == null, fileWhy);

            return expected.Count == 3 ? new JObject { ["records"] = expected } : null;
        }

        /// <summary>
        /// The rule names a file record turns ON: "flags" as a list names them (every territory since 0.1.0), as an object (0.4.0: a
        /// sub-territory that sets only some rules) names those set to true, and absent (a sub-territory that follows them all) names none.
        /// </summary>
        private static HashSet<string> FileFlagNames(JObject rec)
        {
            JToken flags = rec["flags"];
            if (flags is JArray list) return new HashSet<string>(list.Select(x => (string)x));
            if (flags is JObject set) return new HashSet<string>(set.Properties().Where(p => p.Value.Type == JTokenType.Boolean && (bool)p.Value).Select(p => p.Name));
            return new HashSet<string>();
        }

        /// <summary>R32: the restarted server loads the doors territory back with AutoCloseDoors (and the two others without it).</summary>
        private static void RestartDoors(JObject dm)
        {
            if (dm == null)
            {
                Report.Fail("R32-reload-doors", "the restarted store holds the doors territory with AutoCloseDoors", "the first server expected none (server.expected has none: the admin's marking did not complete, see S32a)");
                return;
            }
            var why = new List<string>();
            var described = new List<string>();
            foreach (JObject e in ((JArray)dm["records"]).OfType<JObject>())
            {
                Territory t = TerritoryStore.Get((int)e["id"]);
                if (t == null) { why.Add($"#{e["id"]} '{e["name"]}' did not load"); continue; }
                HashSet<string> have = StoreFlagNames(t);
                described.Add($"#{t.Id} '{t.Name}' [{string.Join(",", have.OrderBy(x => x))}]");
                if (t.Name != (string)e["name"]) why.Add($"#{t.Id} is named '{t.Name}'");
                if (!have.SetEquals(((JArray)e["flags"]).Select(x => (string)x))) why.Add($"'{t.Name}' loaded with [{string.Join(",", have.OrderBy(x => x))}], wanted [{string.Join(",", ((JArray)e["flags"]).Select(x => (string)x))}]");
                if (t.Revision != (int)e["revision"]) why.Add($"'{t.Name}' revision {t.Revision}, wanted {(int)e["revision"]}");
            }
            Report.Check("R32-reload-doors",
                $"after the restart the store holds the doors territory with AutoCloseDoors and the two without, same flags and revision as the first server's store ({string.Join("; ", described)})",
                why.Count == 0 && ((JArray)dm["records"]).Count == 3, string.Join("; ", why));
        }

        // ─────────────────────────────────────────────────────────────
        // server-restart: the territory file must come back exactly
        // ─────────────────────────────────────────────────────────────

        private static IEnumerator Restart()
        {
            JObject expected = RunDir.Read("server.expected");
            if (expected == null)
            {
                Report.Fail("R01-reload", "territories reload after a restart", "no server.expected from the first boot");
                yield break;
            }
            yield return Flow.Seconds(1f);
            var want = ((expected["territories"] as JArray) ?? new JArray()).OfType<JObject>().ToList();
            int all = expected["all"] != null ? (int)expected["all"] : -1;
            var why = new List<string>();
            // Never vacuous: the scenario ends with two E2E territories, and the whole store must come back.
            if (want.Count == 0) why.Add("the first server expected no E2E territory back, so nothing would be tested");
            if (TerritoryStore.Territories.Count != all) why.Add($"{TerritoryStore.Territories.Count} territories loaded in all, the first server held {all}");
            foreach (JObject o in want)
            {
                Territory t = TerritoryStore.Get((int)o["id"]);
                if (t == null) { why.Add($"#{o["id"]} '{o["name"]}' did not load"); continue; }
                string d = Diff(o, t);
                if (d != null) why.Add($"#{t.Id}: {d}");
            }
            int e2e = TerritoryStore.Territories.Count(t => t.Name.StartsWith("E2E"));
            if (e2e != want.Count) why.Add($"{e2e} E2E territories loaded, {want.Count} expected");
            if (Features.Combine) RestartCombined(expected["combined"] as JObject);
            if (Features.Barrier) RestartBarrier(expected["barrier"] as JObject);
            if (Features.Doors) RestartDoors(expected["doors"] as JObject);
            Scenario040.ServerRestart(expected["sub"] as JObject);
            Report.Check("R01-reload", $"after the restart the store loaded {TerritoryStore.Territories.Count} territories that equal, field by field (ids, names, geometry, rules, members, owner, history, revision), what the FIRST server's store held at the end ({string.Join(", ", want.Select(o => $"#{o["id"]} {o["name"]}"))})",
                why.Count == 0 && TerritoryStore.Writable, string.Join("; ", why) + (TerritoryStore.Writable ? "" : " read-only: " + TerritoryStore.ReadOnlyReason));
        }

        /// <summary>R30: the restarted server loads the combined territory back with every part, as the first server's store held it.</summary>
        private static void RestartCombined(JObject cm)
        {
            if (cm == null)
            {
                Report.Fail("R30-reload-parts", "the restarted store holds the combined territory with its parts", "the first server expected no combined territory (server.expected has none: the admin's combine did not complete, see S30)");
                return;
            }
            int id = (int)cm["id"], bId = (int)cm["bId"];
            List<Features.Part> want = ((JArray)cm["parts"]).OfType<JObject>().Select(Features.Part.FromJson).ToList();
            Territory t = TerritoryStore.Get(id);
            List<Features.Part> have = t != null ? Features.Parts(t) : new List<Features.Part>();
            Report.Check("R30-reload-parts",
                $"after the restart the store holds '{(string)cm["name"]}' (#{id}) with its {want.Count} parts [{string.Join(" ; ", want)}], revision {(int)cm["revision"]}, and B (#{bId}) is still gone",
                t != null && t.Name == (string)cm["name"] && have.Count >= 2 && SameParts(have, want) && t.Revision == (int)cm["revision"] && TerritoryStore.Get(bId) == null,
                $"loaded: {(t != null ? $"'{t.Name}' with {have.Count} parts [{string.Join(" ; ", have)}] revision {t.Revision}" : "nothing under that id")}; wanted {want.Count} parts; B in store: {TerritoryStore.Get(bId) != null}");
        }
    }
}
