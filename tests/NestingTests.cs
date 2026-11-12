using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using UnityEngine;

namespace TerroritySmellatory.Tests
{
    // 0.3.2: continents with towns inside them, the way a world-building tool writes them - the whole world's territories as
    // ONE file, read and held the way the server's store reads and holds a file (TerritoryStore.Load: Newtonsoft ->
    // Sanitize(100000) -> TerritoryIndex). Nothing here is new machinery: it is the proof that what the mod already does for a
    // market inside a kingdom (smaller wins) holds at the scale of a 107 km continent with 150 towns, several of them made of
    // many parts, and that the pieces added in 0.3.2 (name-only map labels, file version 4) travel with it.
    internal static partial class Program
    {
        static partial void NestingGroups()
        {
            Run("nesting: a world file of two continents and 150 towns - read, held, governed and sent as the server does", NestingWorldFile);
            Run("nesting: continent-sized parts - held exactly up to 100 km, clamped past it, never shrunk by the placement limit", NestingBigParts);
            Run("nesting: the example file in the public README reads, sanitises and governs as the text says", () => NestingReadmeExample());
            Run("check: the world-file checker passes the README's example and a 152-territory nested file, and probes a point", () => CheckPassesGoodFiles());
            Run("check: it names what the loader accepts silently - typo'd keys and flags, missing sizes, clamps, cleared rules, a town's lost PvP", CheckNamesSilentSlips);
            Run("check: it refuses what the server refuses - bad JSON, a null entry, no id, a duplicate id - and warns about a newer file", CheckRefusals);
        }

        private static string ReadmeExampleJson(string here)
        {
            string readme = Path.Combine(Path.GetDirectoryName(here), "..", "HexiumDist", "README.md");
            string text = File.ReadAllText(readme).Replace("\r\n", "\n");
            int open = text.IndexOf("```json\n", StringComparison.Ordinal);
            int close = text.IndexOf("\n```", open + 8, StringComparison.Ordinal);
            return text.Substring(open + 8, close - open - 8);
        }

        private static void CheckPassesGoodFiles([CallerFilePath] string here = "")
        {
            WorldFileCheck.Report r = WorldFileCheck.Run(ReadmeExampleJson(here), new[] { new Vector3(-27100f, 0f, 4200f), new Vector3(-20000f, 0f, 0f), new Vector3(0f, 0f, 30000f) });
            Check(r.Ok && r.Count(WorldFileCheck.Severity.Warn) == 0, $"the README's example: no errors and no warnings [{string.Join(" | ", r.Findings.Select(f => f.Text))}]");
            Check(r.Header[0].Contains("3 territories") && r.Header[0].Contains("file version 5") && r.Header[0].Contains("2 of them sub-territories"), "its header names the version, the count and the sub-territories");
            Check(r.Nesting[0].Contains("1 top-level territory") && r.Nesting[0].Contains("by parent links") && r.Nesting[1].Contains("'Svartland'") && r.Nesting[1].Contains("holds 2 territories"), $"it sees one continent holding two [{string.Join(" / ", r.Nesting)}]");
            Check(r.Probes[0].Contains("'Hrafnborg'") && r.Probes[0].Contains("inside 'Svartland'") && r.Probes[1].Contains("'Svartland'") && r.Probes[2].Contains("open country"),
                $"probes: the town inside the continent, the continent, open water [{string.Join(" / ", r.Probes)}]");
            Check(!r.Has("pvp-lift") && !r.Has("not-linked") && !r.Has("same-as-parent"), "the sub-territories raise none of the notes a file of unlinked towns would");
            Check(r.Probes[0].Contains("pvp ForcedOff") && r.Probes[1].Contains("pvp ForcedOff"), "probes name the governing territory's PvP");
            Check(r.Render().EndsWith("RESULT: OK - 0 error(s), 0 warning(s), 0 note(s)"), $"the verdict line [{r.Render().Split('\n').Last()}]");

            // A big one, built as the tool for Valtima would: two continents, 150 towns.
            var entries = new List<string>
            {
                TerritoryJsonText(1, "Trammel", new[] { "Announce", "ShowOnMap", "MapNameOnly" }, "Unchanged", new[] { (-28414f, 3591f, 21000f, 9000f), (-8076f, -42619f, 4000f, 3000f) }),
                TerritoryJsonText(2, "Felucca", new[] { "Announce", "ShowOnMap", "MapNameOnly" }, "ForcedOn", new[] { (28492f, -4279f, 21000f, 9000f) }),
            };
            for (int i = 0; i < 150; i++)
                entries.Add(TerritoryJsonText(3 + i, "Town " + i, new[] { "BuildProtect", "NoMonsterSpawns", "Announce", "ShowOnMap" }, "ForcedOff",
                    new[] { (-28414f - 18000f + (i % 15) * 2400f, 3591f - 7500f + (i / 15) * 1500f, 100f, 80f) }));
            string big = "{ \"version\": 4, \"worldName\": \"V\", \"nextId\": 153, \"territories\": [ " + string.Join(",", entries) + " ] }";
            r = WorldFileCheck.Run(big);
            Check(r.Ok && r.Count(WorldFileCheck.Severity.Warn) == 0 && r.Nesting[0].Contains("2 top-level territories") && r.Nesting[1].Contains("holds 150 territories"),
                $"152 territories, two continents holding the 150 towns: clean [{string.Join(" | ", r.Findings.Select(f => f.Text).Take(3))}] {r.Nesting.FirstOrDefault()}");
        }

        private static void CheckNamesSilentSlips()
        {
            // One file, every slip the loader takes without a word.
            string json = @"{ ""version"": 3, ""worldName"": ""W"", ""nextId"": 2, ""colour"": ""x"", ""territories"": [
  { ""id"": 1, ""name"": ""Svartland"", ""shape"": ""Rectangle"", ""x"": 0, ""z"": 0, ""halfX"": 150000, ""halfZ"": 90000, ""pvp"": ""ForcedOn"",
    ""flags"": [ ""NoMonsterSpawns"", ""ShowOnMap"", ""MapNameOnly"" ] },
  { ""id"": 2, ""name"": ""Hrafnborg"", ""shape"": ""Rectangle"", ""x"": 100, ""z"": 100, ""halfX"": 50, ""halfZ"": 40,
    ""part"": [ { ""shape"": ""Rectangle"", ""x"": 160, ""z"": 100, ""halfX"": 20 } ],
    ""flags"": [ ""BuildProtect"", ""MapNameOnlyy"", ""RunicBarrier"" ], ""half_x"": 30 },
  { ""id"": 3, ""name"": ""Hrafnborg"", ""x"": 300, ""z"": 100, ""parts"": [ { ""x"": 330, ""halfZ"": 9 } ], ""flags"": [ ""ShowOnMap"" ] },
  { ""id"": 4, ""name"": ""<b>Loud</b> $name"", ""shape"": ""Circle"", ""x"": 5000, ""z"": 100, ""halfX"": 30, ""pvp"": ""ForcedOn"" },
  { ""id"": 5, ""name"": ""Ulfheim"", ""shape"": ""Circle"", ""x"": 200, ""z"": -200, ""halfX"": 30 }
] }";
            WorldFileCheck.Report r = WorldFileCheck.Run(json);
            Check(r.Ok, "none of it is an error: the server loads this file");
            Check(r.Has("unknown-key") && r.Findings.Any(f => f.Kind == "unknown-key" && f.Text.Contains("\"colour\"") && f.Text.Contains("the file")), "an unknown top-level key");
            Check(r.Findings.Any(f => f.Kind == "unknown-key" && f.Text.Contains("\"part\"") && f.Text.Contains("did you mean \"parts\"")),
                "\"part\" for \"parts\": named, with the key it was probably meant for - the town would lose its other rectangle without a word");
            Check(r.Findings.Any(f => f.Kind == "unknown-key" && f.Text.Contains("\"half_x\"") && f.Text.Contains("halfX")), "\"half_x\" for \"halfX\"");
            Check(r.Findings.Any(f => f.Kind == "unknown-flag" && f.Text.Contains("MapNameOnlyy") && f.Text.Contains("did you mean \"MapNameOnly\"")), "a flag name with a typo, with the one it was meant for");
            Check(r.Findings.Count(f => f.Kind == "missing-key") >= 4 &&
                  r.Findings.Any(f => f.Kind == "missing-key" && f.Text.Contains("#3") && f.Text.Contains("\"shape\"")) &&
                  r.Findings.Any(f => f.Kind == "missing-key" && f.Text.Contains("part #1") && f.Text.Contains("\"z\"")),
                $"missing shape, sizes and positions, on a territory and on a part [{r.CountOf("missing-key")}]");
            Check(r.Findings.Any(f => f.Kind == "flags-dropped" && f.Text.Contains("RunicBarrier") && f.Text.Contains("Hrafnborg")), "a barrier without NoMonsterSpawns: the server clears it");
            Check(!r.Findings.Any(f => f.Kind == "flags-dropped" && f.Text.Contains("MapNameOnly") && f.Text.Contains("Svartland")), "...and MapNameOnly beside ShowOnMap is kept");
            Check(r.Findings.Any(f => f.Kind == "clamp" && f.Text.Contains("Svartland")), "a half-extent past 100 km: cut");
            Check(r.Findings.Any(f => f.Kind == "dup-name" && f.Text.Contains("'Hrafnborg' x2")), "two territories named alike");
            Check(r.Findings.Any(f => f.Kind == "name" && f.Text.Contains("Loud")), "a name the server cleans of markup");
            Check(r.Findings.Any(f => f.Kind == "version" && f.Text.Contains("MapNameOnly")), "a version 3 file that uses the version 4 flag");
            Check(r.Findings.Any(f => f.Kind == "nextid"), "a nextId that is not above the largest id");
            Check(r.Findings.Any(f => f.Kind == "pvp-lift" && f.Text.Contains("Svartland")), "a territory inside a PvP-forcing one that says nothing about PvP");
            Check(r.Findings.Any(f => f.Kind == "spawn-lift" && f.Text.Contains("Svartland")), "and one inside a monster-free one that does not keep monsters out");
            Check(r.Findings.Any(f => f.Kind == "tint" || f.Kind == "orphan") || r.Nesting.Count > 0, "the nesting section is there");
            Check(!r.Findings.Any(f => f.Kind == "tint"), "Svartland is name-only, so it is not told to be: no tint note");
            Check(r.Render().Contains("RESULT: OK"), "and the verdict is OK with its warnings counted");

            // A file with no flags key says so, and a territory whose second part lies outside the one around it is named.
            string spill = @"{ ""version"": 4, ""nextId"": 3, ""territories"": [
  { ""id"": 1, ""name"": ""Land"", ""shape"": ""Rectangle"", ""x"": 0, ""z"": 0, ""halfX"": 1000, ""halfZ"": 1000, ""flags"": [ ""ShowOnMap"", ""MapNameOnly"" ] },
  { ""id"": 2, ""name"": ""Town"", ""shape"": ""Rectangle"", ""x"": 100, ""z"": 100, ""halfX"": 20, ""halfZ"": 20,
    ""parts"": [ { ""shape"": ""Rectangle"", ""x"": 5000, ""z"": 100, ""halfX"": 20, ""halfZ"": 20 } ] }
] }";
            r = WorldFileCheck.Run(spill);
            Check(r.Has("no-flags") && r.Has("spill") && r.Findings.Any(f => f.Kind == "spill" && f.Text.Contains("'Town' part #2")),
                $"no flags key is named, and a part far outside the continent its first part is in [{string.Join(" | ", r.Findings.Select(f => f.Kind))}]");
        }

        private static void CheckRefusals()
        {
            WorldFileCheck.Report r = WorldFileCheck.Run("{ \"version\": 4, \"territories\": [ ");
            Check(!r.Ok && r.Has("parse"), "a file that is not JSON: an error");
            r = WorldFileCheck.Run(@"{ ""version"": 4, ""territories"": [ null ] }");
            Check(!r.Ok && r.Has("refused") && r.Findings[0].Text.Contains("empty"), $"a null entry: the loader's own refusal [{r.Findings.FirstOrDefault()?.Text}]");
            r = WorldFileCheck.Run(@"{ ""version"": 4, ""territories"": [ { ""id"": 1, ""x"": ""north"" } ] }");
            Check(!r.Ok && r.Has("refused"), "a number that is a word: refused, with Newtonsoft's own words");
            r = WorldFileCheck.Run(@"{ ""version"": 4, ""territories"": [ { ""id"": 1, ""name"": ""A"", ""x"": 0, ""z"": 0, ""halfX"": 10 }, { ""name"": ""B"", ""x"": 50, ""z"": 0, ""halfX"": 10 } ] }");
            Check(!r.Ok && r.Has("no-id") && r.Render().Contains("RESULT: FAILED"), "a territory with no id: an error, and the verdict is FAILED");
            r = WorldFileCheck.Run(@"{ ""version"": 4, ""territories"": [ { ""id"": 7, ""name"": ""A"", ""x"": 0, ""z"": 0, ""halfX"": 10 }, { ""id"": 7, ""name"": ""B"", ""x"": 50, ""z"": 0, ""halfX"": 10 } ] }");
            Check(!r.Ok && r.Has("dup-id"), "two territories with one id: an error");
            r = WorldFileCheck.Run(@"{ ""version"": 9, ""territories"": [ { ""id"": 1, ""name"": ""A"", ""x"": 0, ""z"": 0, ""halfX"": 10 } ] }");
            Check(r.Ok && r.Has("newer"), "a file from a newer build: loads, warns that the server would never save over it");
            r = WorldFileCheck.Run(@"{ ""version"": 4, ""territories"": [] }");
            Check(r.Ok && r.Findings.Count == 0 && r.Header[0].Contains("0 territories"), "an empty file is a valid file");
        }

        /// <summary>The first ```json block of HexiumDist/README.md - the text players and tool writers are told to copy - run through the real reader.</summary>
        private static void NestingReadmeExample([CallerFilePath] string here = "")
        {
            string readme = Path.Combine(Path.GetDirectoryName(here), "..", "HexiumDist", "README.md");
            Check(File.Exists(readme), "the public README is where the test expects it");
            string text = File.ReadAllText(readme).Replace("\r\n", "\n");
            int open = text.IndexOf("```json\n", StringComparison.Ordinal);
            int close = open < 0 ? -1 : text.IndexOf("\n```", open + 8, StringComparison.Ordinal);
            Check(open >= 0 && close > open, "the README has an example file in a json block");
            string json = text.Substring(open + 8, close - open - 8);

            TerritoryJson.FileData file = TerritoryJson.Read(json);
            Check(file.Territories.Count == 3 && !file.FromNewerBuild && file.Version == 5, "three territories, file version 5, and not from a newer build");
            foreach (Territory t in file.Territories) Check(t.Sanitize(100000f), $"'{t.Name}' sanitises as the store does");
            Check(Inheritance.Validate(file.Territories) == null, "the tree of parents is sound");
            Territory landStored = file.Territories[0], townStored = file.Territories[1], roundStored = file.Territories[2];
            Check(townStored.ParentId == 1 && roundStored.ParentId == 1 && landStored.ParentId == 0, "Hrafnborg (by id) and Ulfheim (by name) are sub-territories of Svartland");
            List<Territory> eff = Inheritance.Resolve(file.Territories);
            Territory land = eff[0], town = eff[1], round = eff[2];
            Check(land.PartCount == 2 && land.Has(TerritoryFlags.MapNameOnly) && land.Has(TerritoryFlags.ShowOnMap) && land.PvP == PvPRule.ForcedOff,
                "the continent: two pieces, name only on the map, a safe zone");
            Check(town.PartCount == 2 && town.Has(TerritoryFlags.BuildProtect) && town.Has(TerritoryFlags.NoMonsterSpawns) && town.Has(TerritoryFlags.NoRaids) && town.PvP == PvPRule.ForcedOff,
                "the town: two rectangles, and it follows the continent's protections and its safe zone without writing them");
            Check(town.EnterMessage == "Welcome to {name}." && town.Color.g == 0xB4 && !town.Has(TerritoryFlags.MapNameOnly) && town.Has(TerritoryFlags.ShowBorder) && town.Has(TerritoryFlags.ShowOnMap),
                "and differs in what it writes: its welcome, its colour, an outline on the map and a border on the ground");
            Check(round.Kind == TerritoryShapeKind.Circle && round.HalfX == 150f && round.PvP == PvPRule.ForcedOn && !round.Has(TerritoryFlags.BuildProtect) && !round.Has(TerritoryFlags.MapNameOnly) &&
                  round.Has(TerritoryFlags.NoMonsterSpawns) && round.Has(TerritoryFlags.Announce) && round.EnterMessage == "You are in {name}.",
                "the circle town: a war zone with open building drawn on the map, and still the continent's in everything else, its entry text included");

            TerritoryIndex.Set(eff);
            Check(TerritoryIndex.GoverningAt(new Vector3(-27100f, 0f, 4200f)).Name == "Hrafnborg", "the town governs its own middle");
            Check(TerritoryIndex.GoverningAt(new Vector3(-26900f, 0f, 4300f + 80f)).Name == "Hrafnborg", "and the second rectangle of it");
            Check(TerritoryIndex.GoverningAt(new Vector3(-25000f, 0f, 1000f)).Name == "Ulfheim", "the circle governs its middle");
            Check(TerritoryIndex.GoverningAt(new Vector3(-20000f, 0f, 0f)).Name == "Svartland", "the rest of the continent is the continent's");
            Check(TerritoryIndex.GoverningAt(new Vector3(-8076f, 0f, -42619f)).Name == "Svartland", "and so is its island piece");
            Check(TerritoryIndex.GoverningAt(new Vector3(0f, 0f, 30000f)) == null, "the sea beyond is nobody's");
            Check(TerritoryJson.Write(TerritoryJson.Read(TerritoryJson.Write(file))) == TerritoryJson.Write(file), "and it saves back the same, as the store would");
            Check(!TerritoryJson.Write(file).Contains("\"pvp\": \"ForcedOff\"") || TerritoryJson.Write(file).IndexOf("\"pvp\"", StringComparison.Ordinal) == TerritoryJson.Write(file).LastIndexOf("\"pvp\": \"ForcedOff\"", StringComparison.Ordinal),
                "(the follower's saved entry does not repeat what it follows)");
        }

        private static string RectJson(float x, float z, float hx, float hz) =>
            $"{{ \"shape\": \"Rectangle\", \"x\": {x:0.##}, \"z\": {z:0.##}, \"halfX\": {hx:0.##}, \"halfZ\": {hz:0.##}, \"yaw\": 0 }}";

        /// <summary>One territory object of the file, the way tools/Uo2Bc writes them: the first part inline, the rest under "parts".</summary>
        private static string TerritoryJsonText(int id, string name, string[] flags, string pvp, (float x, float z, float hx, float hz)[] parts)
        {
            var sb = new StringBuilder();
            sb.Append($"{{ \"id\": {id}, \"name\": \"{name}\", \"shape\": \"Rectangle\", \"x\": {parts[0].x:0.##}, \"y\": 0, \"z\": {parts[0].z:0.##}, ");
            sb.Append($"\"halfX\": {parts[0].hx:0.##}, \"halfZ\": {parts[0].hz:0.##}, \"yaw\": 0, ");
            if (parts.Length > 1)
                sb.Append("\"parts\": [ " + string.Join(", ", parts.Skip(1).Select(p => RectJson(p.x, p.z, p.hx, p.hz))) + " ], ");
            sb.Append("\"color\": \"#c9b458\", \"ownerId\": 0, \"ownerName\": \"\", \"members\": [], ");
            sb.Append("\"flags\": [ " + string.Join(", ", flags.Select(f => "\"" + f + "\"")) + " ], ");
            sb.Append($"\"pvp\": \"{pvp}\", \"priority\": 0, \"enterMessage\": \"\", \"leaveMessage\": \"\", \"createdBy\": \"test\" }}");
            return sb.ToString();
        }

        private static void NestingWorldFile()
        {
            // Two continents 3 km apart, each of three pieces (a mainland and two islands); every town on the mainland.
            var trammel = new[] { (-28414f, 3591f, 21000f, 9000f), (-8076f, -42619f, 4000f, 3000f), (-31304f, -34824f, 2500f, 2500f) };
            var felucca = new[] { (28492f, -4279f, 21000f, 9000f), (7847f, 42615f, 4000f, 3000f), (29582f, 42508f, 2500f, 2500f) };
            var entries = new List<string>
            {
                TerritoryJsonText(1, "Trammel", new[] { "Announce", "ShowOnMap", "MapNameOnly" }, "ForcedOff", trammel),
                TerritoryJsonText(2, "Felucca", new[] { "Announce", "ShowOnMap", "MapNameOnly" }, "ForcedOn", felucca),
            };

            // 150 towns on Trammel's mainland in a grid, 40 m to 300 m across, each its own name; one of 23 parts, as Moonglow's rectangles
            // would be if merged. Each is inside the mainland, so it is nested in Trammel.
            var townAt = new Dictionary<string, Vector3>();
            int id = 3;
            for (int i = 0; i < 150; i++)
            {
                float cx = -28414f - 18000f + (i % 15) * 2400f, cz = 3591f - 7500f + (i / 15) * 1500f;
                float half = 40f + (i % 7) * 40f;
                string name = "Town " + i;
                var parts = new List<(float, float, float, float)> { (cx, cz, half, half * 0.8f) };
                if (i == 7) for (int k = 1; k < 23; k++) parts.Add((cx + k * 15f, cz + (k % 3) * 20f, 30f, 20f));
                entries.Add(TerritoryJsonText(id++, name, new[] { "BuildProtect", "StructureProtect", "NoMonsterSpawns", "NoRaids", "Announce", "ShowOnMap", "ShowBorder" }, "ForcedOff", parts.ToArray()));
                townAt[name] = new Vector3(cx, 0f, cz);
            }
            string json = "{ \"version\": 4, \"worldName\": \"Valtima\", \"worldUid\": 0, \"nextId\": " + id + ", \"territories\": [ " + string.Join(",\n", entries) + " ] }";

            // READ and HOLD it as TerritoryStore.Load does.
            TerritoryJson.FileData file = TerritoryJson.Read(json);
            Check(file.Territories.Count == 152 && !file.FromNewerBuild, "152 territories, and the file is not from a newer build");
            foreach (Territory t in file.Territories) Check(t.Sanitize(100000f), $"#{t.Id} '{t.Name}' sanitises as the store does");
            Territory tram = file.Territories[0], felu = file.Territories[1];
            Check(tram.PartCount == 3 && tram.HalfX == 21000f && tram.HalfZ == 9000f && tram.GetPart(1).HalfX == 4000f,
                "Trammel keeps all three pieces and its 21 km x 9 km mainland - the placement limit does not touch a file");
            Check(tram.Has(TerritoryFlags.MapNameOnly) && tram.Has(TerritoryFlags.ShowOnMap) && felu.PvP == PvPRule.ForcedOn, "its rules came through: name only on the map, Felucca a war zone");
            Check(file.Territories[2 + 7].PartCount == 23, "the merged town keeps its 23 parts, more than MaxParts' default 16: a file is never cut down");

            var watch = Stopwatch.StartNew();
            TerritoryIndex.Set(file.Territories);
            watch.Stop();
            IReadOnlyList<Territory> all = TerritoryIndex.All;
            Console.WriteLine($"    index of 152 (two 3-piece continents, a 23-part town): {watch.ElapsedMilliseconds} ms");
            Check(all.Count == 152 && all[150].Id <= 2 && all[151].Id <= 2 && all[150].Id != all[151].Id,
                "precedence: every town before the two continents (which of the two is first does not matter: they never overlap)");
            Check(all.Take(150).All(t => t.Id >= 3), "no continent sits among the towns");

            // WHO GOVERNS.
            Check(TerritoryIndex.GoverningAt(townAt["Town 0"]) .Name == "Town 0", "in a town: the town");
            Check(TerritoryIndex.GoverningAt(townAt["Town 7"] + new Vector3(15f * 22f, 0f, 20f)).Name == "Town 7", "...in the far part of the 23-part town too");
            Check(TerritoryIndex.GoverningAt(new Vector3(-28414f, 0f, 3591f + 8000f)).Name == "Trammel", "on Trammel's mainland, outside every town: Trammel");
            Check(TerritoryIndex.GoverningAt(new Vector3(-8076f, 0f, -42619f)).Name == "Trammel", "on a Trammel island: Trammel too (its second piece)");
            Check(TerritoryIndex.GoverningAt(new Vector3(28492f, 0f, -4279f)).Name == "Felucca", "on Felucca's mainland: Felucca");
            Check(TerritoryIndex.GoverningAt(new Vector3(0f, 0f, 0f)) == null, "in the sea between the two: open water, no territory");
            Check(TerritoryIndex.GoverningAt(new Vector3(-28414f - 21000f - 10f, 0f, 3591f)) == null, "10 m past Trammel's west edge: nothing");

            // THE HAMMER'S WORKING RULES at that scale: building is refused in a town to a stranger, allowed on the open continent
            // (Trammel has no BuildProtect), and a stroke that touches a town is refused even if its centre is outside.
            Vector3 inTown = townAt["Town 3"], onLand = new Vector3(-28414f, 0f, 3591f + 8000f);
            Check(TerritoryIndex.Denying(inTown, 2f, TerritoryFlags.BuildProtect, 99L, false) != null, "a stranger is refused in a town");
            Check(TerritoryIndex.Denying(onLand, 2f, TerritoryFlags.BuildProtect, 99L, false) == null, "and allowed on the open continent: Trammel does not protect building");
            Check(TerritoryIndex.Denying(inTown + new Vector3(40f + 3f * 40f + 5f, 0f, 0f), 12f, TerritoryFlags.BuildProtect, 99L, false) != null,
                "a stroke whose centre is outside the town but touches it is refused");

            // THE WIRE: all 152 in one snapshot, and out the other side as a client reads it.
            Protocol.Snapshot snap = Protocol.ReadSnapshot(Protocol.WriteSnapshot(5L, 1, youAreAdmin: false, adminsBypass: true, file.Territories, null).Transmit());
            Check(snap.Territories.Count == 152, "the snapshot carries all 152");
            Check(snap.Territories[0].PartCount == 3 && snap.Territories[0].GetPart(1).SameAs(tram.GetPart(1)) && snap.Territories[0].Has(TerritoryFlags.MapNameOnly),
                "a continent arrives whole, with its pieces and its name-only flag");
            Check(snap.Territories[9].PartCount == 23, "and the 23-part town too");

            // THE FILE AGAIN: saved and re-read, nothing lost - and version 4.
            string saved = TerritoryJson.Write(file);
            Check(saved.Contains("\"version\": 5"), "saved as file version 5");
            TerritoryJson.FileData again = TerritoryJson.Read(saved);
            Check(again.Territories.Count == 152 && again.Territories[0].PartCount == 3 && again.Territories[0].Has(TerritoryFlags.MapNameOnly), "and read back whole");
            Check(TerritoryJson.Write(again) == saved, "write -> read -> write is stable at this size");
        }

        private static void NestingBigParts()
        {
            // 100 km is the file's ceiling (TerritoryStore.Load sanitises with 100000): exact up to it, clamped past it.
            var t = new Territory { Id = 1, Name = "Realm", Kind = TerritoryShapeKind.Rectangle, HalfX = 99000f, HalfZ = 60000f };
            t.ExtraParts.Add(new TerritoryShape(TerritoryShapeKind.Rectangle, 1000f, 1000f, 150000f, 100000f, 0f));
            Check(t.Sanitize(100000f), "sanitises");
            Check(t.HalfX == 99000f && t.HalfZ == 60000f, "a first part under 100 km is exactly as written");
            Check(t.GetPart(1).HalfX == 100000f && t.GetPart(1).HalfZ == 100000f, "a part past it is clamped to 100 km");

            // The in-game limit (MaxRadius) shrinks nothing that is already there: a later edit of the rules keeps the ground (KeepLimit).
            var small = new Territory { Id = 2, Name = "Continent", Kind = TerritoryShapeKind.Rectangle, HalfX = 53500f, HalfZ = 30700f };
            Check(small.Sanitize(100000f) && small.HalfX == 53500f, "a 107 km x 61 km continent is held whole by the file's ceiling");
            var placed = new Territory { Id = 3, Kind = TerritoryShapeKind.Rectangle, HalfX = 53500f, HalfZ = 30700f };
            placed.Sanitize(600f);
            Check(placed.HalfX == 600f && placed.HalfZ == 600f, "through a 600 m MaxRadius it is cut to 600 m, as a placement is");

            // Precedence at this scale: area is measured exactly enough that a 150 m town beats a 100 km continent, and two continents order by id.
            var continent = new Territory { Id = 10, Name = "C", Kind = TerritoryShapeKind.Rectangle, HalfX = 53500f, HalfZ = 30700f };
            var town = new Territory { Id = 11, Name = "T", Kind = TerritoryShapeKind.Rectangle, X = 1000f, Z = 1000f, HalfX = 75f, HalfZ = 75f };
            TerritoryIndex.Set(new[] { continent, town });
            Check(TerritoryIndex.All[0].Id == 11 && TerritoryIndex.GoverningAt(new Vector3(1000f, 0f, 1000f)).Id == 11, "the town governs its ground");
            Check(TerritoryIndex.GoverningAt(new Vector3(50000f, 0f, 30000f)).Id == 10, "and the continent the rest, out to its corner");
            float area = continent.Ground.MeasureArea();
            Check(Math.Abs(area - 107000f * 61400f) / (107000f * 61400f) < 1e-3f, $"a 107 km x 61 km continent measures {area / 1e6f:0} km2 (expected {107f * 61.4f:0})");
        }
    }
}
