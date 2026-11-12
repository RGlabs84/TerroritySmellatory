using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace TerroritySmellatory.Tests
{
    // 0.3.2: A COMMAND-LINE CHECK OF A TERRITORY WORLD FILE, for whoever WRITES one (a world-building tool, a script, a hand edit).
    //
    //   dotnet run -c Release --project tests/TerritoryTests.csproj -- --check <file.json> [--at x,z]... [--tree]
    //
    // It reads the file with the mod's REAL reader and runs every territory through the server's REAL load rules
    // (TerritoryStore.Load: Sanitize(100000), then TerritoryIndex), so what it calls an error is what the server would refuse,
    // and what it reports as changed is what the server would change. And it says what the server accepts SILENTLY, which a
    // headless boot cannot: the loader ignores a key it does not know ("part" for "parts" drops a town's other rectangles,
    // "halfx" is accepted but "half_x" is not), skips a flag name it does not know, fills a missing size with 20 m, clamps a
    // half-extent past 100 km, and clears a rule whose prerequisite is off. None of that is an error to the server; all of it is a
    // surprise to the author. The key and flag names come from the reader's own types by reflection, so this cannot drift from it.
    //
    // 0.4.0: it reads sub-territories too ("parent": the id or name of the territory a territory sits inside, and an absent key
    // meaning "as the parent has it"). The server's tree check is run (a parent that is not in the file, a loop and a chain over
    // eight levels deep are ERRORS: the server refuses the file), the tree printed is the one the links make, and the notes say what
    // a file written for 0.3.x is missing out on - territories that lie inside another without following it, and keys that
    // set exactly what the parent already has (which freeze it: left out, they would change with the parent).
    //
    // And LINKING: `-- --link <file> --out <new file>` turns a file written for 0.3.x (towns that merely lie inside a continent and restate its rules)
    // into a 0.4.0 file in which they are its sub-territories, following it for everything that is the same - without changing what any
    // territory does today. See <see cref="Link"/>.
    //
    // Exit code 0 when there is no ERROR (warnings and notes do not fail it), 1 when there is, 2 for a bad command line.
    internal static class WorldFileCheck
    {
        /// <summary>The first file version that has the MapNameOnly flag (0.3.2).</summary>
        private const int MapNameOnlyVersion = 4;

        /// <summary>The first file version that has "parent" (0.4.0).</summary>
        private const int ParentVersion = 5;

        internal enum Severity { Error, Warn, Note }

        internal sealed class Finding
        {
            public Severity Severity;
            public string Kind;
            public string Text;
        }

        internal sealed class Report
        {
            public readonly List<Finding> Findings = new List<Finding>();
            public readonly List<string> Header = new List<string>();
            public readonly List<string> Nesting = new List<string>();
            public readonly List<string> Probes = new List<string>();

            public int Count(Severity s) => Findings.Count(f => f.Severity == s);
            public bool Ok => Count(Severity.Error) == 0;
            public bool Has(string kind) => Findings.Any(f => f.Kind == kind);
            public int CountOf(string kind) => Findings.Count(f => f.Kind == kind);

            internal void Add(Severity s, string kind, string text) => Findings.Add(new Finding { Severity = s, Kind = kind, Text = text });

            private const int PerKind = 8;

            public string Render()
            {
                var sb = new StringBuilder();
                foreach (string h in Header) sb.AppendLine(h);
                // Grouped by kind in the order each first appeared, the first few of each shown: a file with 125 of one slip
                // should say so once, not 125 times.
                foreach (IGrouping<string, Finding> g in Findings.GroupBy(f => f.Kind))
                {
                    int shown = 0;
                    foreach (Finding f in g)
                    {
                        if (shown++ >= PerKind) break;
                        sb.AppendLine(Prefix(f.Severity) + f.Text);
                    }
                    if (g.Count() > PerKind) sb.AppendLine($"       ... and {g.Count() - PerKind} more of this kind ({g.Key})");
                }
                foreach (string n in Nesting) sb.AppendLine(n);
                foreach (string p in Probes) sb.AppendLine(p);
                sb.Append($"RESULT: {(Ok ? "OK" : "FAILED")} - {Count(Severity.Error)} error(s), {Count(Severity.Warn)} warning(s), {Count(Severity.Note)} note(s)");
                return sb.ToString();
            }

            private static string Prefix(Severity s) => s == Severity.Error ? "ERROR  " : s == Severity.Warn ? "WARN   " : "note   ";
        }

        // The reader's own names, from its own types.
        private static string[] Names(string nestedType)
        {
            Type t = typeof(TerritoryJson).GetNestedType(nestedType, BindingFlags.NonPublic | BindingFlags.Public);
            return t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Select(f => f.GetCustomAttribute<JsonPropertyAttribute>()?.PropertyName)
                .Where(n => n != null)
                .ToArray();
        }

        private static readonly Lazy<string[]> FileKeys = new Lazy<string[]>(() => Names("FileDto"));
        private static readonly Lazy<string[]> TerritoryKeys = new Lazy<string[]>(() => Names("TerritoryDto"));
        private static readonly Lazy<string[]> PartKeys = new Lazy<string[]>(() => Names("PartDto"));
        private static readonly Lazy<string[]> MemberKeys = new Lazy<string[]>(() => Names("MemberDto"));

        private const float FileCeiling = 100000f;     // TerritoryStore.Load's Sanitize(100000f)
        private const int DefaultMaxParts = 16;         // [1 - Server] MaxParts
        private const int InGameMaxParts = 64;          // its ceiling
        private const int MapMaxParts = 256;            // UnionOutlineGeometry.MaxParts: past it the outline is left off the map
        private const float WashesTheMap = 25e6f;       // m2; a territory this big, drawn with an outline and a tint, colours the map

        // ─────────────────────────────────────────────────────────────

        internal static Report Run(string text, IReadOnlyList<Vector3> probes = null, bool tree = false)
        {
            var r = new Report();
            text = (text ?? "").TrimStart('﻿');

            JObject root;
            try
            {
                root = JObject.Parse(text);
            }
            catch (Exception ex)
            {
                r.Add(Severity.Error, "parse", "not valid JSON: " + ex.Message);
                return r;
            }

            TerritoryJson.FileData file;
            try
            {
                file = TerritoryJson.Read(text);
            }
            catch (Exception ex)
            {
                r.Add(Severity.Error, "refused", "the server's loader would refuse this file and leave the store read-only: " + ex.Message);
                return r;
            }

            int linked = file.Territories.Count(t => t.ParentId > 0);
            r.Header.Add($"file version {file.Version} (this build writes {TerritoryJson.FileVersion}), world '{file.WorldName}', uid {file.WorldUid}, nextId {file.NextId}, {file.Territories.Count} territories" +
                         (linked > 0 ? $", {linked} of them sub-territories" : ""));

            CheckKeys(r, root, FileKeys.Value, "the file", "unknown-key");
            if (file.FromNewerBuild)
                r.Add(Severity.Warn, "newer", $"file version {file.Version} is newer than this build writes ({TerritoryJson.FileVersion}): the server would enforce it as far as it understands it and never save over it");

            // The raw side: what the author wrote that the reader would take without a word.
            var rawTerritories = (root["territories"] as JArray)?.OfType<JObject>().ToList() ?? new List<JObject>();
            if (rawTerritories.Count == file.Territories.Count)
                for (int i = 0; i < rawTerritories.Count; i++) CheckRaw(r, rawTerritories[i], file.Territories[i], i + 1);

            // The load side: the store's own loop.
            var loaded = new List<Territory>();
            var seen = new HashSet<int>();
            int maxId = 0;
            for (int i = 0; i < file.Territories.Count; i++)
            {
                Territory t = file.Territories[i];
                string who = $"territory #{i + 1} (id {t.Id}, '{t.Name}')";
                if (t.Id <= 0)
                {
                    r.Add(Severity.Error, "no-id", $"{who} has no id: every territory needs a unique positive \"id\", or the server refuses the whole file");
                    continue;
                }

                Territory before = t.Clone();
                if (!t.Sanitize(FileCeiling))
                {
                    r.Add(Severity.Error, "nan", $"{who} has a position or size that is not a number: the server refuses the whole file");
                    continue;
                }
                if (!seen.Add(t.Id))
                {
                    r.Add(Severity.Error, "dup-id", $"{who}: two territories share id {t.Id}: the server refuses the whole file");
                    continue;
                }
                if (string.IsNullOrEmpty(t.Name)) t.Name = $"Territory {t.Id}";
                CompareLoad(r, before, t);
                loaded.Add(t);
                maxId = Math.Max(maxId, t.Id);
            }

            // Sub-territories: the store refuses a file with a parent that is not in it, a territory inside itself, or a chain too deep.
            string treeProblem = Inheritance.Validate(loaded);
            if (treeProblem != null)
                r.Add(Severity.Error, "tree", "the server's loader would refuse this file and leave the store read-only: " + treeProblem);
            else
                foreach (Territory t in loaded) Inheritance.Canonicalize(t);
            if (file.Version < ParentVersion && loaded.Any(t => t.ParentId > 0))
                r.Add(Severity.Note, "version", $"file version {file.Version} but it has sub-territories (0.4.0): write \"version\": {ParentVersion} or more, so a 0.3.x server that is rolled back to refuses to save over it instead of dropping the links");

            if (file.NextId <= maxId && loaded.Count > 0)
                r.Add(Severity.Note, "nextid", $"nextId {file.NextId} is not above the largest id {maxId}: the server raises it to {maxId + 1} (write {maxId + 1} or more)");
            if (file.Version < MapNameOnlyVersion && loaded.Any(t => t.Has(TerritoryFlags.MapNameOnly)))
                r.Add(Severity.Note, "version", $"file version {file.Version} but it uses MapNameOnly (0.3.2): write \"version\": {MapNameOnlyVersion} or more, so a 0.3.1 server that is rolled back to refuses to save over it instead of dropping the flag");

            var dupNames = loaded.Where(t => !t.Name.StartsWith("Territory ", StringComparison.Ordinal)).GroupBy(t => t.Name).Where(g => g.Count() > 1).OrderByDescending(g => g.Count()).ToList();
            if (dupNames.Count > 0)
                r.Add(Severity.Warn, "dup-name", $"{dupNames.Count} name(s) are shared by more than one territory ({string.Join(", ", dupNames.Take(6).Select(g => $"'{g.Key}' x{g.Count()}"))}{(dupNames.Count > 6 ? ", ..." : "")}): " +
                                                  "each is its own territory with its own banner and map label; a town of several rectangles should be ONE territory with \"parts\"");

            Nest(r, loaded, tree);

            if (probes != null)
                foreach (Vector3 p in probes)
                {
                    var chain = TerritoryIndex.All.Where(t => t.Ground.Contains(p)).ToList();
                    r.Probes.Add(chain.Count == 0
                        ? $"at ({F(p.x)}, {F(p.z)}): open country"
                        : $"at ({F(p.x)}, {F(p.z)}): '{chain[0].DisplayName}' (#{chain[0].Id}, pvp {chain[0].PvP}) governs" +
                          (chain.Count > 1 ? $", inside {string.Join(" > ", chain.Skip(1).Select(c => $"'{c.DisplayName}'"))}" : ""));
                }
            return r;
        }

        // ─────────────────────────────────────────────────────────────
        // RAW: keys and names the reader takes silently
        // ─────────────────────────────────────────────────────────────

        private static void CheckRaw(Report r, JObject jt, Territory t, int index)
        {
            string who = $"territory #{index} (id {t.Id}, '{t.Name}')";
            CheckKeys(r, jt, TerritoryKeys.Value, who, "unknown-key");

            bool rectangle = t.Kind == TerritoryShapeKind.Rectangle;
            if (!HasKey(jt, "shape")) r.Add(Severity.Warn, "missing-key", $"{who} has no \"shape\": it is read as a Circle");
            foreach (string k in new[] { "x", "z", "halfX" })
                if (!HasKey(jt, k)) r.Add(Severity.Warn, "missing-key", $"{who} has no \"{k}\": it is read as {(k == "halfX" ? "20" : "0")}");
            if (rectangle && !HasKey(jt, "halfZ")) r.Add(Severity.Warn, "missing-key", $"{who} is a Rectangle with no \"halfZ\": it is read as 20");

            JToken flags = Get(jt, "flags");
            if (flags == null || flags.Type == JTokenType.Null)
            {
                if (!t.IsSub) r.Add(Severity.Note, "no-flags", $"{who} has no \"flags\": the defaults apply ({Territory.DefaultFlags})");
            }
            else if (flags is JObject ruleObject)
            {
                foreach (JProperty rule in ruleObject.Properties())
                {
                    if (!Enum.TryParse(rule.Name.Trim(), true, out TerritoryFlags parsed))
                        r.Add(Severity.Warn, "unknown-flag", $"{who}: unknown flag \"{rule.Name}\" - the loader skips it{Suggest(rule.Name, Enum.GetNames(typeof(TerritoryFlags)))}");
                    else if ((parsed & ~Territory.KnownFlags) != 0)
                        r.Add(Severity.Warn, "unknown-flag", $"{who}: flag \"{rule.Name}\" carries bits this build does not know - the server clears them");
                }
            }
            else if (flags is JArray list)
                foreach (JToken f in list)
                {
                    string name = (f as JValue)?.Value?.ToString() ?? "";
                    if (!Enum.TryParse(name.Trim(), true, out TerritoryFlags parsed))
                        r.Add(Severity.Warn, "unknown-flag", $"{who}: unknown flag \"{name}\" - the loader skips it{Suggest(name, Enum.GetNames(typeof(TerritoryFlags)))}");
                    else if ((parsed & ~Territory.KnownFlags) != 0)
                        r.Add(Severity.Warn, "unknown-flag", $"{who}: flag \"{name}\" carries bits this build does not know - the server clears them");
                }

            if (Get(jt, "parts") is JArray parts)
            {
                int n = 0;
                foreach (JObject part in parts.OfType<JObject>())
                {
                    n++;
                    string where = $"{who} part #{n}";
                    CheckKeys(r, part, PartKeys.Value, where, "unknown-key");
                    foreach (string k in new[] { "x", "z", "halfX" })
                        if (!HasKey(part, k)) r.Add(Severity.Warn, "missing-key", $"{where} has no \"{k}\": it is read as {(k == "halfX" ? "20" : "0")}");
                    bool partRect = t.ExtraParts.Count >= n && t.ExtraParts[n - 1].Kind == TerritoryShapeKind.Rectangle;
                    if (partRect && !HasKey(part, "halfZ")) r.Add(Severity.Warn, "missing-key", $"{where} is a Rectangle with no \"halfZ\": it is read as 20");
                }
            }

            if (Get(jt, "members") is JArray members)
                foreach (JObject m in members.OfType<JObject>())
                    CheckKeys(r, m, MemberKeys.Value, who + " member", "unknown-key");
        }

        private static void CheckKeys(Report r, JObject o, string[] known, string where, string kind)
        {
            foreach (JProperty p in o.Properties())
                if (!known.Any(k => string.Equals(k, p.Name, StringComparison.OrdinalIgnoreCase)))
                    r.Add(Severity.Warn, kind, $"{where}: unknown key \"{p.Name}\" - the loader ignores it{Suggest(p.Name, known)}");
        }

        private static bool HasKey(JObject o, string key) => Get(o, key) != null;

        private static JToken Get(JObject o, string key) =>
            o.Properties().FirstOrDefault(p => string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase))?.Value;

        private static string Suggest(string typed, IEnumerable<string> known)
        {
            string best = null;
            int bestD = 3;
            foreach (string k in known)
            {
                int d = Distance(typed.ToLowerInvariant(), k.ToLowerInvariant());
                if (d < bestD) { bestD = d; best = k; }
            }
            return best == null ? "" : $" (did you mean \"{best}\"?)";
        }

        private static int Distance(string a, string b)
        {
            var d = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) d[0, j] = j;
            for (int i = 1; i <= a.Length; i++)
                for (int j = 1; j <= b.Length; j++)
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            return d[a.Length, b.Length];
        }

        // ─────────────────────────────────────────────────────────────
        // LOAD: what the server changes on the way in
        // ─────────────────────────────────────────────────────────────

        private static void CompareLoad(Report r, Territory before, Territory after)
        {
            string who = $"'{after.Name}' (id {after.Id})";

            if (before.Name != after.Name && !string.IsNullOrEmpty(before.Name) && !(before.Name.Trim() == after.Name))
                r.Add(Severity.Note, "name", $"{who}: the name \"{before.Name}\" is stored as \"{after.Name}\" (the server strips < > $ and control characters, trims, and keeps 48 characters)");
            else if (before.Name.Trim() != before.Name)
                r.Add(Severity.Note, "name", $"{who}: the name has spaces at its ends, which the server trims");

            TerritoryFlags dropped = before.Flags & ~after.Flags;
            if (dropped != 0)
                r.Add(Severity.Warn, "flags-dropped", $"{who}: the server clears {dropped} on load - a rule whose prerequisite is off (the barrier needs NoMonsterSpawns, BarrierKeepsOutVisitors needs the barrier, MapNameOnly needs ShowOnMap)");

            bool clamped = Differs(before.HalfX, after.HalfX) || (before.Kind == TerritoryShapeKind.Rectangle && Differs(before.HalfZ, after.HalfZ));
            for (int i = 0; i < before.ExtraParts.Count && i < after.ExtraParts.Count && !clamped; i++)
                clamped = Differs(before.ExtraParts[i].HalfX, after.ExtraParts[i].HalfX) ||
                          (before.ExtraParts[i].Kind == TerritoryShapeKind.Rectangle && Differs(before.ExtraParts[i].HalfZ, after.ExtraParts[i].HalfZ));
            if (clamped)
                r.Add(Severity.Warn, "clamp", $"{who}: a half-extent past 100 km is cut to 100 km on load (the file's ceiling)");

            if (before.Members.Count != after.Members.Count)
                r.Add(Severity.Note, "members", $"{who}: {before.Members.Count} members listed, {after.Members.Count} kept (no id 0, no duplicates, the owner is a member already, at most {Territory.MaxMembers})");
            if (before.EnterMessage != after.EnterMessage || before.LeaveMessage != after.LeaveMessage)
                r.Add(Severity.Note, "messages", $"{who}: an entry or exit message is cleaned on load (control characters dropped, {Territory.MaxMessageLength} characters kept)");

            if (after.PartCount > MapMaxParts)
                r.Add(Severity.Note, "many-parts", $"{who} has {after.PartCount} parts: past {MapMaxParts} its outline is left off the map (its name still shows)");
            else if (after.PartCount > InGameMaxParts)
                r.Add(Severity.Note, "many-parts", $"{who} has {after.PartCount} parts, more than the {InGameMaxParts} the game lets an admin make: fine in a file, but the map outline compares every part with every other");
            else if (after.PartCount > DefaultMaxParts)
                r.Add(Severity.Note, "many-parts", $"{who} has {after.PartCount} parts, more than MaxParts' default of {DefaultMaxParts}: the file keeps them all; MaxParts only limits what an admin adds in the game");
        }

        private static bool Differs(float a, float b) => Math.Abs(a - b) > 1e-3f;

        // ─────────────────────────────────────────────────────────────
        // NESTING: who is inside whom, and what a town does not inherit
        // ─────────────────────────────────────────────────────────────

        private static void Nest(Report r, List<Territory> loaded, bool tree)
        {
            // What the server publishes and the rules read: every sub-territory with what it follows filled in.
            TerritoryIndex.Set(Inheritance.Resolve(loaded));
            IReadOnlyList<Territory> all = TerritoryIndex.All;
            int n = all.Count;
            if (n == 0) return;

            var at = new Dictionary<int, int>(n);
            for (int k = 0; k < n; k++) at[all[k].Id] = k;
            bool linked = all.Any(t => t.ParentId > 0 && at.ContainsKey(t.ParentId));

            // geo[k]: the territory whose ground holds k's middle - the first LATER one in precedence order, which is the smallest around it.
            // parent[k]: where k sits. A file with parent links means what it says; a file without any (0.3.x) is read off the ground.
            var parent = new int[n];
            var geo = new int[n];
            var children = new List<int>[n];
            for (int k = 0; k < n; k++) children[k] = new List<int>();
            for (int k = 0; k < n; k++)
            {
                geo[k] = -1;
                Vector3 c = all[k].Center;
                for (int j = k + 1; j < n; j++)
                    if (all[j].Ground.Contains(c)) { geo[k] = j; break; }
                parent[k] = linked ? (all[k].ParentId > 0 && at.TryGetValue(all[k].ParentId, out int up) ? up : -1) : geo[k];
                if (parent[k] >= 0) children[parent[k]].Add(k);
            }

            var roots = Enumerable.Range(0, n).Where(k => parent[k] < 0).OrderByDescending(k => all[k].Ground.MeasureArea()).ToList();
            int Size(int k) => 1 + children[k].Sum(Size);
            int Depth(int k) => 1 + (children[k].Count == 0 ? 0 : children[k].Max(Depth));
            int deepest = roots.Count == 0 ? 0 : roots.Max(Depth);

            r.Nesting.Add($"nesting{(linked ? " (by parent links)" : "")}: {roots.Count} top-level territor{(roots.Count == 1 ? "y" : "ies")}, {deepest} level{(deepest == 1 ? "" : "s")} deep");
            foreach (int k in roots.Take(12))
            {
                Territory t = all[k];
                int inside = Size(k) - 1;
                r.Nesting.Add($"  '{t.DisplayName}' (#{t.Id}, {t.PartCount} part{(t.PartCount == 1 ? "" : "s")}, {t.Ground.MeasureArea() / 1e6f:0.##} km2)" +
                              (inside > 0 ? $" holds {inside} territor{(inside == 1 ? "y" : "ies")}" : ""));
            }
            if (roots.Count > 12) r.Nesting.Add($"  ... and {roots.Count - 12} more top-level territories");

            if (tree)
            {
                int lines = 0;
                void Print(int k, int depth)
                {
                    if (lines++ >= 400) return;
                    Territory t = all[k];
                    string own = t.IsSub ? (Inheritance.SetsItself(t).Count == 0 ? ", follows everything" : $", sets {Inheritance.SetsItself(t).Count} itself") : "";
                    r.Nesting.Add($"{new string(' ', 2 + 2 * depth)}- '{t.DisplayName}' #{t.Id} ({t.PartCount} part{(t.PartCount == 1 ? "" : "s")}, pvp {t.PvP}{own})");
                    foreach (int c in children[k].OrderBy(c => all[c].Id)) Print(c, depth + 1);
                }
                foreach (int k in roots) Print(k, 0);
                if (lines > 400) r.Nesting.Add($"  ... {lines - 400} more lines not shown");
            }

            // Continents (name-only roots) and what is outside them.
            var continents = roots.Where(k => all[k].Has(TerritoryFlags.MapNameOnly)).ToList();
            if (continents.Count > 0)
            {
                var loose = roots.Where(k => !all[k].Has(TerritoryFlags.MapNameOnly)).ToList();
                if (loose.Count > 0)
                    r.Add(Severity.Note, "orphan", $"{loose.Count} top-level territor{(loose.Count == 1 ? "y is" : "ies are")} outside every name-only continent: {string.Join(", ", loose.Take(8).Select(k => $"'{all[k].DisplayName}'"))}{(loose.Count > 8 ? ", ..." : "")}");
            }
            foreach (int k in roots)
            {
                Territory t = all[k];
                float area = t.Ground.MeasureArea();
                if (area > WashesTheMap && t.Has(TerritoryFlags.ShowOnMap) && !t.Has(TerritoryFlags.MapNameOnly))
                    r.Add(Severity.Note, "tint", $"'{t.DisplayName}' covers {area / 1e6f:0} km2 and is drawn on the map with an outline and a tint: MapNameOnly would show just its name");
            }

            // A territory with NO parent link that lies inside another REPLACES that one's rules, it does not follow them: the two slips worth naming.
            var unlinked = Enumerable.Range(0, n).Where(k => all[k].ParentId == 0 && geo[k] >= 0).ToList();
            foreach (IGrouping<int, int> group in unlinked.GroupBy(k => geo[k]))
            {
                Territory outer = all[group.Key];
                var inner = group.ToList();
                if (outer.PvP != PvPRule.Unchanged)
                {
                    var lift = inner.Where(c => all[c].PvP == PvPRule.Unchanged).ToList();
                    if (lift.Count > 0)
                        r.Add(Severity.Note, "pvp-lift", $"{lift.Count} of the {inner.Count} territories inside '{outer.DisplayName}' have PvP \"Unchanged\" although it forces PvP {outer.PvP}: " +
                                                       $"a territory with no \"parent\" replaces its surroundings' rules, so players switch PvP themselves there ({string.Join(", ", lift.Take(5).Select(c => $"'{all[c].DisplayName}'"))}{(lift.Count > 5 ? ", ..." : "")})");
                }
                if (outer.Has(TerritoryFlags.NoMonsterSpawns))
                {
                    var spawn = inner.Where(c => !all[c].Has(TerritoryFlags.NoMonsterSpawns)).ToList();
                    if (spawn.Count > 0)
                        r.Add(Severity.Note, "spawn-lift", $"{spawn.Count} territories inside '{outer.DisplayName}' lack NoMonsterSpawns although it has it, so monsters can spawn there ({string.Join(", ", spawn.Take(5).Select(c => $"'{all[c].DisplayName}'"))}{(spawn.Count > 5 ? ", ..." : "")})");
                }
            }
            if (unlinked.Count > 0)
                r.Add(Severity.Note, "not-linked", $"{unlinked.Count} territor{(unlinked.Count == 1 ? "y lies" : "ies lie")} inside another without being its sub-territory (no \"parent\"): each replaces its surroundings' rules instead of following them. " +
                                                  $"On 0.4.0, \"parent\": <the id of the territory around it> makes it start as a copy of it and follow it ({string.Join(", ", unlinked.Take(4).Select(k => $"'{all[k].DisplayName}' in '{all[geo[k]].DisplayName}'"))}{(unlinked.Count > 4 ? ", ..." : "")})");

            // Sub-territories: the keys that set what the parent already has freeze it, and one whose middle is outside its parent is probably a slip.
            var frozen = new List<string>();
            int frozenSettings = 0;
            var outside = new List<string>();
            foreach (Territory t in loaded)
            {
                if (t.ParentId <= 0 || !at.TryGetValue(t.ParentId, out int pk) || !at.TryGetValue(t.Id, out int tk)) continue;
                Territory up = all[pk];
                List<string> same = SameAsParent(t, up);
                if (same.Count > 0)
                {
                    frozenSettings += same.Count;
                    frozen.Add($"'{t.DisplayName}' ({string.Join(", ", same.Take(3))}{(same.Count > 3 ? ", ..." : "")})");
                }
                if (!up.Ground.Contains(all[tk].Center)) outside.Add($"'{t.DisplayName}' in '{up.DisplayName}'");
            }
            if (frozen.Count > 0)
                r.Add(Severity.Note, "same-as-parent", $"{frozen.Count} sub-territor{(frozen.Count == 1 ? "y sets" : "ies set")} {frozenSettings} setting(s) to exactly what its parent has now ({string.Join("; ", frozen.Take(4))}{(frozen.Count > 4 ? "; ..." : "")}): " +
                                                       "left out of the file they would follow the parent and change with it; written, they stay as they are");
            if (outside.Count > 0)
                r.Add(Severity.Note, "outside-parent", $"{outside.Count} sub-territor{(outside.Count == 1 ? "y has" : "ies have")} the middle of the first part outside the ground of the territory they are inside ({string.Join(", ", outside.Take(5))}{(outside.Count > 5 ? ", ..." : "")}) - allowed, but a wrong \"parent\" looks like this");

            // A part of a territory that lies outside the territory around it: a coordinate slip (a flipped or shifted piece) more often than a harbour.
            var spill = new List<string>();
            for (int k = 0; k < n; k++)
            {
                if (parent[k] < 0) continue;
                TerritoryGround outerGround = all[parent[k]].Ground;
                for (int i = 1; i < all[k].PartCount; i++)
                {
                    TerritoryShape part = all[k].GetPart(i);
                    if (!outerGround.Contains(new Vector3(part.CenterX, 0f, part.CenterZ)))
                        spill.Add($"'{all[k].DisplayName}' part #{i + 1}");
                }
            }
            if (spill.Count > 0)
                r.Add(Severity.Warn, "spill", $"{spill.Count} part(s) have their middle outside the territory their first part is inside ({string.Join(", ", spill.Take(6))}{(spill.Count > 6 ? ", ..." : "")}): a shifted or mirrored coordinate?");
        }

        /// <summary>The settings a sub-territory sets itself that are exactly what its (effective) parent has, by name.</summary>
        private static List<string> SameAsParent(Territory child, Territory parent)
        {
            var same = new List<string>();
            foreach (TerritoryFlags rule in Enum.GetValues(typeof(TerritoryFlags)))
                if (rule != TerritoryFlags.None && !child.Follows(rule) && child.Has(rule) == parent.Has(rule)) same.Add(Inheritance.RuleLabel(rule));

            if (!child.Follows(TerritoryFollow.PvP) && child.PvP == parent.PvP) same.Add("PvP");
            if (!child.Follows(TerritoryFollow.Priority) && child.Priority == parent.Priority) same.Add("priority");
            if (!child.Follows(TerritoryFollow.Color) && child.Color.r == parent.Color.r && child.Color.g == parent.Color.g && child.Color.b == parent.Color.b) same.Add("colour");
            if (!child.Follows(TerritoryFollow.EnterText) && child.EnterMessage == parent.EnterMessage) same.Add("entry text");
            if (!child.Follows(TerritoryFollow.LeaveText) && child.LeaveMessage == parent.LeaveMessage) same.Add("exit text");
            if (!child.Follows(TerritoryFollow.SanctuaryValues) && child.HealthRegen == parent.HealthRegen && child.StaminaRegen == parent.StaminaRegen &&
                child.EitrRegen == parent.EitrRegen && child.CarryBonus == parent.CarryBonus && child.ArmorBonus == parent.ArmorBonus) same.Add("Sanctuary numbers");
            if (!child.Follows(TerritoryFollow.People) && child.OwnerId == parent.OwnerId && child.Members.Count == parent.Members.Count &&
                child.Members.Zip(parent.Members, (a, b) => a.Id == b.Id).All(x => x)) same.Add("owner and members");
            return same;
        }

        private static string F(float v) => v.ToString("0.#", CultureInfo.InvariantCulture);

        // ─────────────────────────────────────────────────────────────
        // LINKING: a 0.3.x file's nested towns become sub-territories, losslessly
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Every territory with no "parent" whose middle lies in another territory's ground is made a sub-territory of the SMALLEST one around
        /// it, and follows that one for every rule and setting that is exactly what it has today; what differs stays the territory's own. What
        /// each territory DOES is checked to be the same afterwards (rules, PvP, priority, people, Sanctuary numbers, texts, colour), and a
        /// territory whose result is not is left as it was. Territories that already have a parent are not touched. Returns the new file
        /// text, or null with the reason in <paramref name="report"/>.
        /// </summary>
        internal static string Link(string text, out string report)
        {
            TerritoryJson.FileData file;
            try { file = TerritoryJson.Read((text ?? "").TrimStart('﻿')); }
            catch (Exception ex) { report = "cannot link: the server's loader would refuse this file: " + ex.Message; return null; }

            var stored = new List<Territory>();
            foreach (Territory t in file.Territories)
            {
                if (t.Id <= 0 || !t.Sanitize(FileCeiling)) { report = $"cannot link: territory '{t.Name}' (id {t.Id}) has no id or a position that is not a number"; return null; }
                stored.Add(t);
            }
            string treeProblem = Inheritance.Validate(stored);
            if (treeProblem != null) { report = "cannot link: " + treeProblem; return null; }
            foreach (Territory t in stored) Inheritance.Canonicalize(t);

            List<Territory> before = Inheritance.Resolve(stored);
            var effectiveById = new Dictionary<int, Territory>();
            foreach (Territory e in before) effectiveById[e.Id] = e;
            var index = new Dictionary<int, Territory>();
            foreach (Territory t in stored) index[t.Id] = t;

            TerritoryIndex.Set(before);
            IReadOnlyList<Territory> all = TerritoryIndex.All;
            var linked = new List<string>();
            var left = new List<string>();
            for (int k = 0; k < all.Count; k++)
            {
                Territory eff = all[k];
                if (eff.ParentId != 0) continue;

                // The smallest territory around it: the first LATER one in precedence order that holds its middle.
                Territory around = null;
                for (int j = k + 1; j < all.Count && around == null; j++)
                    if (all[j].Ground.Contains(eff.Center)) around = all[j];
                if (around == null) continue;

                Territory candidate = index[eff.Id].Clone();
                MakeFollower(candidate, effectiveById[around.Id], around.Id);

                // Whatever else it replaces, the result must DO what it did: resolve it in place and compare.
                Territory was = index[eff.Id];
                stored[stored.IndexOf(was)] = candidate;
                string drift = FirstDifference(effectiveById[eff.Id], Inheritance.Resolve(stored).First(e => e.Id == eff.Id));
                if (drift != null)
                {
                    stored[stored.IndexOf(candidate)] = was;
                    left.Add($"'{eff.DisplayName}' ({drift})");
                    continue;
                }
                index[eff.Id] = candidate;
                linked.Add($"'{eff.DisplayName}' in '{around.DisplayName}' (sets {Inheritance.SetsItself(candidate).Count} itself)");
            }

            // The whole result against the whole before: nothing anywhere may have changed.
            List<Territory> after = Inheritance.Resolve(stored);
            var changed = new List<string>();
            foreach (Territory e in after)
            {
                string d = FirstDifference(effectiveById[e.Id], e);
                if (d != null) changed.Add($"'{e.DisplayName}' ({d})");
            }
            if (changed.Count > 0) { report = "cannot link: the result would change what these do: " + string.Join(", ", changed.Take(5)); return null; }

            var outFile = new TerritoryJson.FileData { WorldName = file.WorldName, WorldUid = file.WorldUid, NextId = file.NextId, Territories = stored };
            report = $"linked {linked.Count} of {stored.Count} territories as sub-territories; each does exactly what it did, and now follows what is the same as its parent's" +
                     (linked.Count > 0 ? $": {string.Join("; ", linked.Take(6))}{(linked.Count > 6 ? "; ..." : "")}" : "") +
                     (left.Count > 0 ? $". Left as they were ({left.Count}): {string.Join("; ", left.Take(4))}" : "");
            return TerritoryJson.Write(outFile);
        }

        /// <summary>Makes <paramref name="child"/> a sub-territory of <paramref name="parent"/>, following everything that is exactly the same.</summary>
        private static void MakeFollower(Territory child, Territory parent, int parentId)
        {
            child.ParentId = parentId;
            TerritoryFlags differing = child.Flags ^ parent.Flags;
            child.FollowFlags = Territory.KnownFlags & ~differing;

            TerritoryFollow follow = TerritoryFollow.None;
            if (child.PvP == parent.PvP) follow |= TerritoryFollow.PvP;
            if (child.Priority == parent.Priority) follow |= TerritoryFollow.Priority;
            if (child.OwnerId == parent.OwnerId && child.Members.Count == parent.Members.Count &&
                child.Members.Zip(parent.Members, (a, b) => a.Id == b.Id).All(x => x)) follow |= TerritoryFollow.People;
            if (child.HealthRegen == parent.HealthRegen && child.StaminaRegen == parent.StaminaRegen && child.EitrRegen == parent.EitrRegen &&
                child.CarryBonus == parent.CarryBonus && child.ArmorBonus == parent.ArmorBonus) follow |= TerritoryFollow.SanctuaryValues;
            if (child.EnterMessage == parent.EnterMessage) follow |= TerritoryFollow.EnterText;
            if (child.LeaveMessage == parent.LeaveMessage) follow |= TerritoryFollow.LeaveText;
            if (child.Color.r == parent.Color.r && child.Color.g == parent.Color.g && child.Color.b == parent.Color.b) follow |= TerritoryFollow.Color;
            child.Follow = follow;
            Inheritance.Canonicalize(child);
        }

        /// <summary>null when two effective records DO the same; else the first thing that differs.</summary>
        private static string FirstDifference(Territory a, Territory b)
        {
            if (a.Flags != b.Flags) return $"rules {a.Flags} became {b.Flags}";
            if (a.PvP != b.PvP) return $"PvP {a.PvP} became {b.PvP}";
            if (a.Priority != b.Priority) return "priority";
            if (a.OwnerId != b.OwnerId || a.Members.Count != b.Members.Count || !a.Members.Zip(b.Members, (x, y) => x.Id == y.Id).All(x => x)) return "owner or members";
            if (a.HealthRegen != b.HealthRegen || a.StaminaRegen != b.StaminaRegen || a.EitrRegen != b.EitrRegen || a.CarryBonus != b.CarryBonus || a.ArmorBonus != b.ArmorBonus) return "Sanctuary numbers";
            if (a.EnterMessage != b.EnterMessage || a.LeaveMessage != b.LeaveMessage) return "texts";
            if (a.Color.r != b.Color.r || a.Color.g != b.Color.g || a.Color.b != b.Color.b) return "colour";
            return null;
        }

        // ─────────────────────────────────────────────────────────────
        // COMMAND LINE
        // ─────────────────────────────────────────────────────────────

        internal static int Cli(string[] args)
        {
            string path = null, outPath = null;
            bool tree = false, link = args.Length > 0 && args[0] == "--link";
            var probes = new List<Vector3>();
            for (int i = 1; i < args.Length; i++)
            {
                string a = args[i];
                if (a == "--tree") tree = true;
                else if (a == "--link") link = true;
                else if (a == "--out" && i + 1 < args.Length) outPath = args[++i];
                else if (a == "--at" && i + 1 < args.Length && TryPoint(args[++i], out Vector3 p)) probes.Add(p);
                else if (!a.StartsWith("--") && path == null) path = a;
                else return Usage($"cannot use '{a}'");
            }
            if (path == null) return Usage("no file given");
            if (!File.Exists(path)) return Usage($"no such file: {path}");
            if (link)
            {
                if (outPath == null) return Usage("--link needs --out <the new file> (the original is never overwritten)");
                if (string.Equals(Path.GetFullPath(outPath), Path.GetFullPath(path), StringComparison.Ordinal)) return Usage("--out must not be the file being read");
                string linked = Link(File.ReadAllText(path, Encoding.UTF8), out string linkReport);
                Console.WriteLine($"world file link: {path}");
                Console.WriteLine(linkReport);
                if (linked == null) return 1;
                File.WriteAllText(outPath, linked, new UTF8Encoding(false));
                Console.WriteLine($"written: {outPath}  (check it with --check)");
                return 0;
            }

            Report report = Run(File.ReadAllText(path, Encoding.UTF8), probes, tree);
            Console.WriteLine($"world file check: {path}");
            Console.WriteLine(report.Render());
            return report.Ok ? 0 : 1;
        }

        private static bool TryPoint(string s, out Vector3 p)
        {
            p = default;
            string[] parts = s.Split(',');
            if (parts.Length != 2 ||
                !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) ||
                !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float z)) return false;
            p = new Vector3(x, 0f, z);
            return true;
        }

        private static int Usage(string why)
        {
            Console.Error.WriteLine(why);
            Console.Error.WriteLine("usage: dotnet run -c Release --project tests/TerritoryTests.csproj -- --check <file.json> [--at x,z]... [--tree]");
            Console.Error.WriteLine("   or: ... -- --link <file.json> --out <new file.json>   (make a 0.3.x file's nested towns sub-territories, changing nothing they do)");
            return 2;
        }
    }
}
