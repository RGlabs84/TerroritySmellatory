using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;

namespace TerroritySmellatory
{
    /// <summary>The server's territories for the loaded world, and the file they live in.</summary>
    /// <remarks>
    /// WHERE: BepInEx/config/TerroritySmellatory/worlds/&lt;world name&gt;.json - deliberately NOT beside the
    /// world save. A 1.0 world is a directory whose housekeeping deletes, drops or orphans any file it does
    /// not recognise: auto-backups copy only its own extensions, a rename deletes strangers, a cloud delete
    /// orphans them (VALHEIM-1.0-SAVE-SYSTEM-FACTS.md). The config folder is outside all of that.
    ///
    /// KEYED BY WORLD NAME, with the world's uid kept inside as a check rather than as the key: rebuilding a
    /// Better Continents world from an export gives it a new uid but the same name and the same map, and
    /// its territories should survive that. A uid mismatch is logged, not refused.
    ///
    /// A FILE THAT FAILED TO READ IS NEVER OVERWRITTEN. Starting empty after a bad read and saving on the
    /// next change is how a typo in a hand edit turns into every territory deleted (PLAYER-IDENTITY-FACTS
    /// checklist #4). The store goes read-only, says why - down to the line and column - and waits for
    /// `terr_reload`. Nor does a bad read switch anything OFF: a `terr_reload` that fails keeps the list
    /// already in force, so the typo costs the admins their edits until it is fixed, not every player
    /// their protection. Only a world's first load, with nothing in force yet, starts empty.
    ///
    /// A FILE A NEWER BUILD WROTE is read and enforced, but never overwritten either: this build would drop
    /// whatever it does not know on its first save (TerritoryJson.FileData.FromNewerBuild).
    /// </remarks>
    internal static class TerritoryStore
    {
        private static readonly List<Territory> _territories = new List<Territory>();
        private static string _worldName;
        private static long _worldUid;
        private static int _nextId = 1;

        /// <summary>
        /// The STORED records: what an admin edits and the file holds - for a sub-territory (0.4.0), only what it sets itself.
        /// What the server's own rules read is <see cref="Effective"/>; players are sent these stored records and work that out themselves.
        /// </summary>
        public static IReadOnlyList<Territory> Territories => _territories;

        private static List<Territory> _effective;

        /// <summary>
        /// The effective records (Inheritance.Resolve): each sub-territory with what it follows filled in from its parent, in the
        /// stored order. Built on first use after any change, so a parent's edit reaches every sub-territory under it, and
        /// never held across one.
        /// </summary>
        public static IReadOnlyList<Territory> Effective => _effective ?? (_effective = Inheritance.Resolve(_territories));

        private static void Stale() => _effective = null;

        /// <summary>
        /// False after a failed read or a failed self-check, or for a file a newer build wrote: nothing is saved until
        /// it is resolved.
        /// </summary>
        public static bool Writable { get; private set; } = true;

        public static string ReadOnlyReason { get; private set; } = "";

        public static string FilePath { get; private set; } = "";

        public static long WorldUid => _worldUid;

        /// <summary>Loads the current world's file if it is not the one already loaded. True when it (re)loaded.</summary>
        public static bool EnsureLoaded()
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer() || ZNet.World == null) return false;
            string world = ZNet.instance.GetWorldName();
            if (string.IsNullOrEmpty(world)) return false;
            if (world == _worldName) return false;

            Load(world, ZNet.instance.GetWorldUID());
            return true;
        }

        public static void Unload()
        {
            Stale();
            _territories.Clear();
            _worldName = null;
            _worldUid = 0L;
            _nextId = 1;
            Writable = true;
            ReadOnlyReason = "";
        }

        /// <summary>
        /// Re-reads the current world's file from disk (an admin edited it by hand). True when what is loaded now is
        /// the file's - it was read, or there is none; false when it could not be read, and the list already in force
        /// stays as it was.
        /// </summary>
        public static bool Reload()
        {
            if (string.IsNullOrEmpty(_worldName)) return false;
            return Load(_worldName, _worldUid);
        }

        /// <summary>True when the list is now the file's (or empty, for no file); false when the file could not be read.</summary>
        private static bool Load(string worldName, long worldUid)
        {
            // Only a NEW world starts empty. The same world again is terr_reload (Unload forgets the name when a
            // session ends), and that keeps the list in force until the file has read cleanly.
            if (worldName != _worldName)
            {
                Stale();
                _territories.Clear();
                _nextId = 1;
            }
            _worldName = worldName;
            _worldUid = worldUid;
            Writable = true;
            ReadOnlyReason = "";

            string dir = Path.Combine(Paths.ConfigPath, Plugin.Name, "worlds");
            FilePath = Path.Combine(dir, SafeFileName(worldName) + ".json");

            // Before this process writes a single territory, prove the file format gives back what it was
            // handed. It is cheap, it runs once, and a format bug that dropped a field would otherwise
            // corrupt every save while every log line said "saved" - which is exactly what JsonUtility did
            // to the first version of this store (libs-Tools/JSONUTILITY-FACTS.md).
            if (!FormatRoundTrips(out string problem))
            {
                Writable = false;
                ReadOnlyReason = $"The territory file format failed its self-check ({problem}). Nothing will be saved; please report this.";
                Plugin.Log.LogError(ReadOnlyReason);
            }

            // Read and check the whole file first, into a list of its own: nothing replaces the list in force until
            // every territory in it has passed. A failure anywhere leaves the world exactly as it was.
            TerritoryJson.FileData file;
            var loaded = new List<Territory>();
            int maxId = 0;
            try
            {
                Directory.CreateDirectory(dir);
                if (!File.Exists(FilePath))
                {
                    // No file is no territories - on a reload too, when an admin has deleted it to start over.
                    Stale();
                    _territories.Clear();
                    _nextId = 1;
                    Plugin.Log.LogInfo($"No territories yet for world '{worldName}' - they will be saved to {FilePath}.");
                    return true;
                }

                file = TerritoryJson.Read(File.ReadAllText(FilePath, Encoding.UTF8));

                if (file.WorldUid != 0L && worldUid != 0L && file.WorldUid != worldUid)
                    Plugin.Log.LogWarning($"{FilePath} was written for world uid {file.WorldUid} and this world is {worldUid}. " +
                                          "Same name, so it is loaded anyway (a rebuilt Better Continents world keeps its name and map); delete the file if this is a different world.");

                foreach (Territory t in file.Territories)
                {
                    // Loading must never shrink what an admin placed, even if MaxRadius has been lowered
                    // since: the cap applies to new placements, the file is the record of old ones. The same
                    // holds for a territory's extra parts, and for their number - MaxParts limits combining and
                    // adding from now on, and a file holding more than it allows keeps them all.
                    if (t.Id <= 0) throw new FormatException($"a territory has no id (name '{t.Name}') - every territory needs a unique positive id");
                    if (!t.Sanitize(100000f)) throw new FormatException($"territory {t.Id} has a position or size that is not a number");
                    if (loaded.Exists(x => x.Id == t.Id)) throw new FormatException($"two territories share id {t.Id}");
                    if (string.IsNullOrEmpty(t.Name)) t.Name = $"Territory {t.Id}";
                    loaded.Add(t);
                    maxId = Math.Max(maxId, t.Id);
                }

                // Sub-territories (0.4.0): every parent must be a territory in the file, with no loop and no more than
                // Inheritance.MaxDepth levels. A link that goes nowhere would change what the territory does, so it is
                // refused like a duplicate id (the list in force stays), not guessed at.
                string tree = Inheritance.Validate(loaded);
                if (tree != null) throw new FormatException(tree);
                foreach (Territory t in loaded) Inheritance.Canonicalize(t);
            }
            catch (Exception ex)
            {
                // Only the store turns read-only. The list already in force stays, with every protection in it (on a
                // world's first load there is none yet), and the file is not touched until a reload reads it.
                Writable = false;
                int kept = _territories.Count;
                string enforced = kept == 0 ? ""
                    : kept == 1 ? " The territory already loaded is still enforced."
                    : $" The {kept} territories already loaded are still enforced.";
                ReadOnlyReason = $"{FilePath} could not be read ({ex.Message}){NewtonsoftHint(ex)}. Nothing will be saved until it is fixed and 'terr_reload' succeeds.{enforced}";
                Plugin.Log.LogError(ReadOnlyReason);
                return false;
            }

            Stale();
            _territories.Clear();
            _territories.AddRange(loaded);
            _nextId = Math.Max(file.NextId, maxId + 1);
            Plugin.Log.LogInfo($"Loaded {_territories.Count} territor{(_territories.Count == 1 ? "y" : "ies")} for world '{worldName}'.");
            int inside = _territories.FindAll(t => t.ParentId > 0).Count;
            if (inside > 0) Plugin.Log.LogInfo($"{inside} of them {(inside == 1 ? "is a sub-territory" : "are sub-territories")} (inside another territory, following it for what they do not set).");

            // A server rolled back from a later build: its file is enforced as far as this build understands it, but
            // saving would write it back without everything this build skipped - a new rule's key, a new flag.
            if (file.FromNewerBuild)
            {
                Writable = false;
                ReadOnlyReason = $"{FilePath} was written by a newer TerroritySmellatory (file version {file.Version}; this one writes " +
                                 $"{TerritoryJson.FileVersion}), so its territories are enforced but nothing will be saved over it - a save would drop " +
                                 $"what the newer version added. To edit it with this version anyway, set its \"version\" to {TerritoryJson.FileVersion} " +
                                 "and run 'terr_reload'.";
                Plugin.Log.LogError(ReadOnlyReason);
            }
            return true;
        }

        /// <summary>
        /// Newtonsoft ships with Valheim 1.0 and comes with the declared ValheimModding-JsonDotNET dependency, so
        /// this should never fire - but if the assembly is missing, the exception names a file, not the fix.
        /// </summary>
        private static string NewtonsoftHint(Exception ex) =>
            (ex is FileNotFoundException || ex is TypeLoadException) && (ex.Message ?? "").Contains("Newtonsoft")
                ? " - Newtonsoft.Json is missing: install ValheimModding-JsonDotNET"
                : "";

        public static bool Save(out string error)
        {
            error = null;
            if (!Writable)
            {
                error = ReadOnlyReason;
                return false;
            }
            if (string.IsNullOrEmpty(_worldName))
            {
                error = "No world is loaded.";
                return false;
            }

            try
            {
                var file = new TerritoryJson.FileData
                {
                    WorldName = _worldName,
                    WorldUid = _worldUid,
                    NextId = _nextId,
                    Territories = new List<Territory>(_territories)
                };
                string json = TerritoryJson.Write(file);
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, json, new UTF8Encoding(false));

                // Replace, not overwrite: the previous good file becomes the .bak in the same step, so a crash
                // mid-write leaves either the old file or the new one, never half of one. File.Replace is
                // Unity-Mono's to implement on each platform, so ANY failure falls back to the plain three-step
                // version - which re-checks what is actually on disk rather than assuming Replace did nothing.
                if (!File.Exists(FilePath) || !TryReplace(tmp, FilePath))
                {
                    if (File.Exists(FilePath))
                    {
                        File.Copy(FilePath, FilePath + ".bak", true);
                        File.Delete(FilePath);
                    }
                    File.Move(tmp, FilePath);
                }
                return true;
            }
            catch (Exception ex)
            {
                error = $"Saving {FilePath} failed: {ex.Message}";
                Plugin.Log.LogError(error);
                return false;
            }
        }

        private static bool _replaceWarned;

        private static bool TryReplace(string tmp, string path)
        {
            try
            {
                File.Replace(tmp, path, path + ".bak");
                return true;
            }
            catch (Exception ex)
            {
                if (!_replaceWarned)
                {
                    _replaceWarned = true;
                    Plugin.Log.LogWarning($"File.Replace is not usable here ({ex.GetType().Name}); saving with copy/delete/move instead.");
                }
                return false;
            }
        }

        public static Territory Get(int id) => _territories.Find(t => t.Id == id);

        /// <summary>Where a territory sits in the list (and so in the file), or -1. Taken before a Remove, for its undo.</summary>
        public static int IndexOf(int id) => _territories.FindIndex(t => t.Id == id);

        public static Territory Add(Territory t)
        {
            Stale();
            t.Id = _nextId++;
            _territories.Add(t);
            return t;
        }

        /// <summary>
        /// Puts a territory back under its own id - the undo of a Remove whose save failed. Given the place it was
        /// taken from (<see cref="IndexOf"/>), it goes back there, so a failed save leaves even the file's order as it was.
        /// </summary>
        public static void Restore(Territory t, int index = -1)
        {
            Stale();
            if (Get(t.Id) != null) return;
            if (index >= 0 && index <= _territories.Count) _territories.Insert(index, t);
            else _territories.Add(t);
        }

        public static void Replace(Territory t)
        {
            Stale();
            int i = _territories.FindIndex(x => x.Id == t.Id);
            if (i >= 0) _territories[i] = t;
        }

        public static bool Remove(int id)
        {
            Stale();
            return _territories.RemoveAll(t => t.Id == id) > 0;
        }

        private static string SafeFileName(string name)
        {
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
                sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 || c == '/' || c == '\\' ? '_' : c);
            string s = sb.ToString().Trim();
            return s.Length == 0 ? "world" : s;
        }

        // ─────────────────────────────────────────────────────────────
        // SELF-CHECK
        // ─────────────────────────────────────────────────────────────

        private static bool _selfChecked;
        private static bool _selfCheckOk;
        private static string _selfCheckProblem = "";

        /// <summary>
        /// Once per process: a territory with every field set - two extra parts included - through Write -> Read,
        /// field by field. The sample and the comparison are TerritoryJson.RoundTripProblem, so the off-game
        /// harness runs this very check, and proves it fails when a writer or reader loses a part.
        /// </summary>
        private static bool FormatRoundTrips(out string problem)
        {
            if (_selfChecked)
            {
                problem = _selfCheckProblem;
                return _selfCheckOk;
            }
            _selfChecked = true;

            try
            {
                problem = TerritoryJson.RoundTripProblem() ?? "no answer";
            }
            catch (Exception ex)
            {
                problem = ex.GetType().Name + ": " + ex.Message + NewtonsoftHint(ex);
            }

            _selfCheckOk = problem.Length == 0;
            _selfCheckProblem = problem;
            if (_selfCheckOk) Plugin.Log.LogInfo("Territory file format self-check passed.");
            return _selfCheckOk;
        }
    }
}
