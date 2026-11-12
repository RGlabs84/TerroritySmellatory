using System;
using System.Linq;
using UnityEngine;

namespace TerroritySmellatory.Tests
{
    // 0.2.0: the territory file's version 2 - a territory's extra parts as a "parts" list - the store's start-up
    // self-check, which now carries two parts, and how a file from a newer build is told apart. The server's
    // operations themselves are TerritoryEdits (GroundTests) run under TerritoryServer's
    // clone -> sanitise -> save -> roll back, which needs a live ZNet and is not compiled here.
    //
    // 0.3.0: file version 3 (the runic barrier's two flag names; the number moves so a rolled-back 0.2.x server
    // refuses to save over them), the barrier flags' one rule - a flag needs the one before it, cleared by Sanitize -
    // and that both names travel through the file, the wire and the self-check.
    internal static partial class Program
    {
        static partial void NetGroups()
        {
            Run("file v2: parts round-trip exactly - awkward floats, every kind, in order", FilePartsRoundTrip);
            Run("file v2: hand-written parts - names or numbers, any case, missing keys, empty list, no key", FilePartsHandWritten);
            Run("file v2: a broken part is refused, naming the territory and the part", FilePartsErrors);
            Run("file v3: a file written by 0.1.1 loads unchanged, and saves back identical but for its version", FileFromV011);
            Run("file v3: a file 0.2.x wrote loads writable and saves back identical but for its version", FileFromV020);
            Run("file v3: a file a newer build wrote (version 6) still reads, and says so - the store enforces it and never saves over it", FileFromNewerBuild);
            Run("self-check: the shipping round-trip check passes with parts and barrier flags, and fails when one is lost", FileSelfCheck);
            Run("barrier flags: one that needs another is cleared without it - every combination, and the draft's start", NetBarrierRules);
            Run("barrier flags: the panel card and terr_list name the barrier", NetBarrierLines);
            Run("map flags (0.3.2): name-only needs the map rule, is a name in the file, a bit in the int", NetMapNameOnly);
            Run("barrier flags: both names travel through the file (by name, any case) and the wire, and 0.2.x's guard holds", NetBarrierTravel);
            Run("barrier flags: combining keeps the target's barrier, splitting off keeps it too", NetBarrierEdits);
        }

        /// <summary>Same parts, same order, number for number.</summary>
        private static bool NetSameParts(Territory a, Territory b) =>
            a.ExtraParts.Count == b.ExtraParts.Count && a.ExtraParts.Zip(b.ExtraParts, (p, q) => p.SameAs(q)).All(x => x);

        private static int NetCount(string text, string what) => text.Split(what).Length - 1;

        private static void FilePartsRoundTrip()
        {
            // Floats with no short decimal form, and a spread of magnitudes: a third, tenths, a coordinate near the
            // world's edge, a yaw just short of a full turn and one just past zero. Each has to come back as the
            // same float, not a near one - a part that crept on every save would walk its border across the map.
            Territory a = Sample();
            a.ExtraParts.Add(new TerritoryShape(TerritoryShapeKind.Rectangle, -1234.567f, 8765.432f, 22f / 3f, 31.1f, 271.7f));
            a.ExtraParts.Add(new TerritoryShape(TerritoryShapeKind.Circle, 0.1f, -0.3f, 25.123f, 25.123f, 0f));
            a.ExtraParts.Add(new TerritoryShape(TerritoryShapeKind.Square, 10000.123f, -9999.99f, 1.1f, 1.1f, 359.9f));
            a.ExtraParts.Add(new TerritoryShape(TerritoryShapeKind.Rectangle, 3.3333333f, -7.77f, 599.99f, 1f, 0.001f));
            Territory b = Circle(3, -10.125f, 20.5f, 7.75f, TerritoryFlags.None);

            var file = new TerritoryJson.FileData { WorldName = "Midgard", WorldUid = 42L, NextId = 4 };
            file.Territories.Add(a);
            file.Territories.Add(b);

            string json = TerritoryJson.Write(file);
            TerritoryJson.FileData back = TerritoryJson.Read(json);
            Check(back.Territories.Count == 2, "both territories");
            SameTerritory(a, back.Territories[0], "compound record");
            Check(NetSameParts(a, back.Territories[0]), "every extra part exactly, in order");
            Check(back.Territories[0].PartCount == 5 && back.Territories[1].PartCount == 1, "part counts");

            Check(json.Contains("\"version\": 5"), "written as file version 5");
            Check(NetCount(json, "\"parts\"") == 1, "only the compound territory has a \"parts\" key; one marked once has none");
            Check(json.Contains("\"shape\": \"Square\""), "a part's shape is written as its name");
            Check(json.IndexOf("\"yaw\"", StringComparison.Ordinal) < json.IndexOf("\"parts\"", StringComparison.Ordinal) &&
                  json.IndexOf("\"parts\"", StringComparison.Ordinal) < json.IndexOf("\"color\"", StringComparison.Ordinal),
                "the parts sit with the first part's geometry, before the rules");

            // Saving what was loaded writes the same text: nothing drifts from one save to the next.
            Check(TerritoryJson.Write(back) == json, "write -> read -> write is stable, character for character");

            // What the store does to every territory it loads: Sanitize with a cap that never shrinks placed ground.
            Territory loaded = back.Territories[0];
            Check(loaded.Sanitize(100000f) && NetSameParts(a, loaded), "loading sanitises valid parts without changing a digit");
        }

        private static void FilePartsHandWritten()
        {
            // The same leniency as the first part: names in any case or numbers, a missing key its default, an
            // unknown key ignored, trailing commas tolerated. And an absent, empty or null list is simply none.
            string json = @"{ ""version"": 2, ""territories"": [
  { ""id"": 1, ""name"": ""Many"", ""shape"": ""circle"", ""x"": 0, ""z"": 0, ""halfX"": 10,
    ""parts"": [
      { ""shape"": ""RECTANGLE"", ""x"": 30, ""z"": -5.5e1, ""halfX"": 12, ""halfZ"": 4, ""yaw"": 45 },
      { ""shape"": 1, ""x"": 60, ""z"": 0, ""halfX"": 8, ""yaw"": 390 },
      { ""shape"": ""square"", ""x"": 90, ""z"": 0, ""halfX"": 7, ""halfZ"": 99 },
      { ""x"": 5 },
      { ""shape"": 2, ""x"": 1, ""z"": 2, ""halfX"": 3 },
      { ""shape"": ""Circle"", ""x"": 0, ""z"": 0, ""halfX"": 5, ""yaw"": 123, ""y"": 77, ""someFutureKey"": [1, {""a"": null}] },
    ] },
  { ""id"": 2, ""name"": ""Empty list"", ""parts"": [] },
  { ""id"": 3, ""name"": ""No key"" },
  { ""id"": 4, ""name"": ""Null list"", ""parts"": null },
  { ""id"": 5, ""name"": ""Odd numbers"", ""shape"": 77, ""halfX"": 9, ""parts"": [ { ""shape"": 77, ""x"": 1, ""halfX"": 6, ""halfZ"": 9, ""yaw"": 30 } ] }
] }";
            TerritoryJson.FileData f = TerritoryJson.Read(json);
            Check(f.Territories.Count == 5, "five records read");

            Territory t = f.Territories[0];
            Check(t.PartCount == 7, $"the first part and six more (got {t.PartCount})");
            TerritoryShape rect = t.GetPart(1);
            Check(rect.Kind == TerritoryShapeKind.Rectangle && rect.CenterZ == -55f && rect.HalfX == 12f && rect.HalfZ == 4f && rect.Yaw == 45f,
                "a name in capitals, and an exponent");
            TerritoryShape byNumber = t.GetPart(2);
            Check(byNumber.Kind == TerritoryShapeKind.Square && byNumber.HalfZ == 8f, "a shape given as its number; a square's depth is its width");
            Near(byNumber.Yaw, 30f, "a yaw past a full turn is wrapped");
            TerritoryShape square = t.GetPart(3);
            Check(square.Kind == TerritoryShapeKind.Square && square.HalfX == 7f && square.HalfZ == 7f, "a name in lower case; a square ignores halfZ");
            TerritoryShape bare = t.GetPart(4);
            Check(bare.Kind == TerritoryShapeKind.Circle && bare.CenterX == 5f && bare.CenterZ == 0f && bare.HalfX == 20f && bare.Yaw == 0f,
                "a part with only x takes every other default: a 20 m radius circle");
            TerritoryShape noDepth = t.GetPart(5);
            Check(noDepth.Kind == TerritoryShapeKind.Rectangle && noDepth.HalfX == 3f && noDepth.HalfZ == 20f, "a rectangle with no halfZ takes the default depth");
            TerritoryShape circle = t.GetPart(6);
            Check(circle.Kind == TerritoryShapeKind.Circle && circle.HalfX == 5f && circle.Yaw == 0f, "a circle's yaw is held at 0; a stray y and unknown keys are ignored");

            Check(f.Territories[1].PartCount == 1 && f.Territories[1].ExtraParts != null, "an empty list is no extra parts");
            Check(f.Territories[2].PartCount == 1 && f.Territories[2].ExtraParts != null, "no key is no extra parts");
            Check(f.Territories[3].PartCount == 1 && f.Territories[3].ExtraParts != null, "a null list is no extra parts");

            // An undefined shape number is not refused for the first part - Sanitize reads it as a circle - and a
            // part is held to exactly the same rule, at load, as the store does it.
            Territory odd = f.Territories[4];
            Check(odd.Sanitize(100000f), "a record with undefined shape numbers still loads");
            TerritoryShape oddPart = odd.GetPart(1);
            Check(odd.Kind == TerritoryShapeKind.Circle && oddPart.Kind == TerritoryShapeKind.Circle && oddPart.HalfX == 6f && oddPart.HalfZ == 6f && oddPart.Yaw == 0f,
                "the first part and an extra part with an undefined shape both read as circles");

            // The version number is not a gate: an older file an admin added parts to by hand reads them.
            TerritoryJson.FileData old = TerritoryJson.Read(@"{ ""version"": 1, ""territories"": [ { ""id"": 1, ""parts"": [ { ""x"": 9 } ] } ] }");
            Check(old.Version == 1 && old.Territories[0].PartCount == 2, "a version 1 file with parts added by hand reads them");
        }

        private static void FilePartsErrors()
        {
            // Newtonsoft refuses the wrong TYPE itself and names the JSON path. What it lets through - an empty
            // entry, and numbers a JSON reader accepts but a position cannot be - the file reader refuses, naming
            // the territory by its place and id and the part by its place in "parts".
            ExpectError(@"{ ""territories"": [ { ""id"": 1 }, { ""id"": 4, ""parts"": [ { ""x"": 1 }, null ] } ] }",
                "territory #2 (id 4) part #2", "a null entry in parts");
            ExpectError(@"{ ""territories"": [ { ""id"": 4, ""parts"": [ { ""x"": 1 }, null ] } ] }", "empty (null)", "a null entry says what is wrong");
            ExpectError(@"{ ""territories"": [ { ""id"": 9, ""parts"": [ { ""x"": NaN } ] } ] }", "territory #1 (id 9) part #1", "NaN");
            ExpectError(@"{ ""territories"": [ { ""id"": 9, ""parts"": [ { ""x"": NaN } ] } ] }", "'x' is NaN", "NaN names the key");
            ExpectError(@"{ ""territories"": [ { ""id"": 9, ""parts"": [ { ""x"": 1 }, { ""x"": 2 }, { ""z"": 1e39 } ] } ] }",
                "part #3 in \"parts\": 'z' is Infinity", "a number too large for a float");
            ExpectError(@"{ ""territories"": [ { ""id"": 9, ""parts"": [ { ""halfX"": Infinity } ] } ] }", "'halfX' is Infinity", "Infinity");
            ExpectError(@"{ ""territories"": [ { ""id"": 9, ""parts"": [ { ""shape"": ""Rectangle"", ""halfZ"": NaN } ] } ] }", "'halfZ'", "a NaN depth");
            ExpectError(@"{ ""territories"": [ { ""id"": 9, ""parts"": [ { ""yaw"": -Infinity } ] } ] }", "'yaw' is -Infinity", "-Infinity");
            ExpectError(@"{ ""territories"": [ { ""id"": 9, ""parts"": [ { ""shape"": ""hexagon"" } ] } ] }", "territories[0].parts[0].shape", "an unknown shape name");
            ExpectError(@"{ ""territories"": [ { ""id"": 9, ""parts"": [ { ""x"": 1 }, { ""x"": ""abc"" } ] } ] }", "territories[0].parts[1].x", "text where a number belongs");
            ExpectError(@"{ ""territories"": [ { ""id"": 9, ""parts"": { ""x"": 1 } } ] }", "territories[0].parts", "parts not a list");
            ExpectError(@"{ ""territories"": [ { ""id"": 9, ""parts"": [ 5 ] } ] }", "territories[0].parts[0]", "a part that is a number");
            ExpectError(@"{ ""territories"": [ { ""id"": 9, ""parts"": [ [ 1, 2 ] ] } ] }", "territories[0].parts[0]", "a part that is a list");

            // The count the wire carries is the count the file may hold. The widest territory a file accepts must
            // reach every client whole; one part more is refused at load, where it can be named, instead of by
            // every client's snapshot reader, which drops the whole list over one record it cannot read.
            string Many(int n) => "{ \"territories\": [ { \"id\": 9, \"parts\": [" + string.Join(",", Enumerable.Repeat("{ \"x\": 1 }", n)) + "] } ] }";
            Territory widest = TerritoryJson.Read(Many(TerritoryJson.WireMaxExtraParts)).Territories[0];
            var wire = new ZPackage();
            widest.Write(wire);
            Check(Territory.Read(wire.Transmit()).ExtraParts.Count == TerritoryJson.WireMaxExtraParts, "the most parts a file accepts is what a client reads");
            ExpectError(Many(TerritoryJson.WireMaxExtraParts + 1), $"territory #1 (id 9) has {TerritoryJson.WireMaxExtraParts + 1} entries in \"parts\"", "more parts than the wire carries");
            widest.ExtraParts.Add(widest.ExtraParts[0]);
            var over = new ZPackage();
            widest.Write(over);
            bool refused = false;
            try { Territory.Read(over.Transmit()); }
            catch (FormatException) { refused = true; }
            Check(refused, "and the wire reader really does refuse one more: the two limits are the same number");
        }

        // A world file exactly as TerroritySmellatory 0.1.1 wrote it: generated by the 0.1.1 source's own
        // TerritoryJson.Write (kept as a fixture, not rebuilt by this build's writer), from the values below.
        private const string V011File = """
            {
              "version": 1,
              "worldName": "bcterr",
              "worldUid": 1234567890123456789,
              "nextId": 4,
              "territories": [
                {
                  "id": 1,
                  "name": "Hrafnborg \"Keep\"",
                  "shape": "Rectangle",
                  "x": -1520.25,
                  "y": 44.5,
                  "z": 3310.75,
                  "halfX": 40.0,
                  "halfZ": 12.3,
                  "yaw": 33.3,
                  "color": "#3CA5A0",
                  "ownerId": 7654321987654321,
                  "ownerName": "Wubarrk",
                  "members": [
                    {
                      "id": 1111111111111111111,
                      "name": "Astrid"
                    },
                    {
                      "id": -2222222222222,
                      "name": "Bjorn"
                    }
                  ],
                  "flags": [
                    "BuildProtect",
                    "AccessProtect",
                    "Sanctuary",
                    "Announce",
                    "ShowOnMap"
                  ],
                  "pvp": "ForcedOff",
                  "priority": 2,
                  "healthRegen": 1.5,
                  "staminaRegen": 2.0,
                  "eitrRegen": 0.75,
                  "carryBonus": 150.0,
                  "armorBonus": 10.0,
                  "enterMessage": "Welcome to <color=#FFD700>{name}</color>",
                  "leaveMessage": "Safe travels",
                  "createdBy": "Wubarrk (Steam_76561198000000000)",
                  "createdTicks": 638631234567890123,
                  "revision": 4
                },
                {
                  "id": 3,
                  "name": "Market",
                  "shape": "Circle",
                  "x": 0.1,
                  "y": 30.0,
                  "z": -250.6,
                  "halfX": 25.5,
                  "halfZ": 25.5,
                  "yaw": 0.0,
                  "color": "#D6AA48",
                  "ownerId": 0,
                  "ownerName": "",
                  "members": [],
                  "flags": [
                    "Announce",
                    "ShowOnMap",
                    "ShowBorder"
                  ],
                  "pvp": "Unchanged",
                  "priority": 0,
                  "healthRegen": 1.0,
                  "staminaRegen": 1.0,
                  "eitrRegen": 1.0,
                  "carryBonus": 0.0,
                  "armorBonus": 0.0,
                  "enterMessage": "",
                  "leaveMessage": "",
                  "createdBy": "Wubarrk (host)",
                  "createdTicks": 638631239999999999,
                  "revision": 1
                }
              ]
            }
            """;

        /// <summary>The two territories V011File was written from.</summary>
        private static Territory[] V011Territories()
        {
            var keep = new Territory
            {
                Id = 1,
                Name = "Hrafnborg \"Keep\"",
                Kind = TerritoryShapeKind.Rectangle,
                X = -1520.25f,
                Y = 44.5f,
                Z = 3310.75f,
                HalfX = 40f,
                HalfZ = 12.3f,
                Yaw = 33.3f,
                Color = new Color32(60, 165, 160, 255),
                OwnerId = 7654321987654321L,
                OwnerName = "Wubarrk",
                Flags = TerritoryFlags.BuildProtect | TerritoryFlags.AccessProtect | TerritoryFlags.Sanctuary | TerritoryFlags.Announce | TerritoryFlags.ShowOnMap,
                PvP = PvPRule.ForcedOff,
                Priority = 2,
                HealthRegen = 1.5f,
                StaminaRegen = 2f,
                EitrRegen = 0.75f,
                CarryBonus = 150f,
                ArmorBonus = 10f,
                EnterMessage = "Welcome to <color=#FFD700>{name}</color>",
                LeaveMessage = "Safe travels",
                CreatedBy = "Wubarrk (Steam_76561198000000000)",
                CreatedTicks = 638631234567890123L,
                Revision = 4
            };
            keep.Members.Add(new TerritoryMember(1111111111111111111L, "Astrid"));
            keep.Members.Add(new TerritoryMember(-2222222222222L, "Bjorn"));

            var market = new Territory
            {
                Id = 3,
                Name = "Market",
                Kind = TerritoryShapeKind.Circle,
                X = 0.1f,
                Y = 30f,
                Z = -250.6f,
                HalfX = 25.5f,
                HalfZ = 25.5f,
                Flags = TerritoryFlags.Announce | TerritoryFlags.ShowOnMap | TerritoryFlags.ShowBorder,
                CreatedBy = "Wubarrk (host)",
                CreatedTicks = 638631239999999999L,
                Revision = 1
            };
            return new[] { keep, market };
        }

        private static void FileFromV011()
        {
            TerritoryJson.FileData f = TerritoryJson.Read(V011File);
            Check(f.Version == 1 && f.WorldName == "bcterr" && f.WorldUid == 1234567890123456789L && f.NextId == 4, "the header, version 1");
            Check(f.Territories.Count == 2, "both territories");

            Territory[] expected = V011Territories();
            for (int i = 0; i < expected.Length; i++)
            {
                Territory t = f.Territories[i];
                SameTerritory(expected[i], t, $"0.1.1 record {i + 1}");
                Check(t.ExtraParts != null && t.PartCount == 1, $"0.1.1 record {i + 1} is its one part");

                // As the store loads it: sanitised against a cap that never shrinks, and nothing changes.
                Territory before = t.Clone();
                Check(t.Sanitize(100000f), $"0.1.1 record {i + 1} sanitises");
                SameTerritory(before, t, $"0.1.1 record {i + 1} after loading");
            }

            // The strongest form of "unchanged": this build's writer gives the 0.1.1 file back character for
            // character, but for the version it now says. No "parts" key appears on a territory marked once, and
            // no barrier flag appears on a territory that never had one: upgrading turns nothing on.
            string rewritten = TerritoryJson.Write(f).Replace("\r\n", "\n");
            string original = V011File.Replace("\r\n", "\n").Replace("\"version\": 1,", "\"version\": 5,");
            Check(rewritten == original, "saved again, the 0.1.1 file is identical except \"version\": 5");
            Check(!rewritten.Contains("\"parts\"") && !rewritten.Contains("Barrier"), "and has no \"parts\" key and no barrier flag");
        }

        private static void FileFromV020()
        {
            // A file as 0.2.x wrote it: a compound territory with its "parts" list, and no barrier anywhere - 0.2.x has no
            // such flag. Only the version number separates that writer from this one for such data, so the fixture is this
            // build's text with the number put back; what is under test is how the number is read, not the writer.
            Territory keep = Sample();
            keep.Flags = TerritoryFlags.BuildProtect | TerritoryFlags.NoMonsterSpawns | TerritoryFlags.NoRaids | TerritoryFlags.ShowOnMap;
            keep.ExtraParts.Add(new TerritoryShape(TerritoryShapeKind.Square, 70f, 20f, 15f, 15f, 0f));
            keep.ExtraParts.Add(new TerritoryShape(TerritoryShapeKind.Rectangle, -40f, 5.5f, 12f, 4f, 45f));
            var written = new TerritoryJson.FileData { WorldName = "Midgard", WorldUid = 42L, NextId = 43 };
            written.Territories.Add(keep);
            written.Territories.Add(Circle(9, 5f, 6f, 7f, TerritoryFlags.NoMonsterSpawns));
            string v2 = TerritoryJson.Write(written).Replace("\"version\": 5", "\"version\": 2");
            Check(v2.Contains("\"version\": 2") && !v2.Contains("\"version\": 5"), "the fixture says version 2");

            TerritoryJson.FileData f = TerritoryJson.Read(v2);
            Check(f.Version == 2 && !f.FromNewerBuild, "a version 2 file is not from a newer build: the store loads it writable");
            Check(f.Territories.Count == 2 && f.Territories[0].PartCount == 3, "both territories, the compound one whole");
            for (int i = 0; i < f.Territories.Count; i++)
            {
                Territory t = f.Territories[i];
                Territory before = t.Clone();
                Check(t.Sanitize(100000f), $"0.2.x record {i + 1} sanitises");
                SameTerritory(before, t, $"0.2.x record {i + 1} after loading");
                Check(NetSameParts(before, t), $"0.2.x record {i + 1}: every part unchanged by loading");
                Check(!t.Has(TerritoryFlags.RunicBarrier) && !t.Has(TerritoryFlags.BarrierKeepsOutVisitors),
                    $"0.2.x record {i + 1} has no barrier: one that keeps monsters from spawning does not gain one by upgrading");
            }
            Check(TerritoryJson.Write(f) == v2.Replace("\"version\": 2", "\"version\": 5"), "saved again it is identical except \"version\": 5");
        }

        private static void FileFromNewerBuild()
        {
            // What a server rolled back from a later build finds: file version 6 (this build writes 5), with a key and a
            // flag name this build has never heard of. The territories must still read, because they are enforced; and
            // the file must say it is newer, because this build would write it back without both on its first save - so
            // the store keeps it read-only (TerritoryStore.Load). 0.1.x has no such guard, and would drop 0.2.0's parts
            // exactly that way; 0.2.x's guard is what holds it off a version 3 file, and this one is the same guard a
            // step on.
            TerritoryJson.FileData newer = TerritoryJson.Read(@"{ ""version"": 6, ""worldName"": ""Midgard"", ""nextId"": 3, ""territories"": [
  { ""id"": 1, ""name"": ""Keep"", ""shape"": ""Circle"", ""x"": 10, ""z"": 20, ""halfX"": 30,
    ""barrier"": { ""runes"": true, ""colour"": ""#3FA9FF"" }, ""flags"": [ ""BuildProtect"", ""SkyfallWard"" ],
    ""parts"": [ { ""shape"": ""Square"", ""x"": 70, ""z"": 20, ""halfX"": 15, ""glow"": 2 } ] },
  { ""id"": 2, ""name"": ""Market"" }
] }");
            Check(TerritoryJson.FileVersion == 5, "this build writes file version 5");
            Check(newer.Version == 6 && newer.FromNewerBuild, "a version 6 file is from a newer build");
            Check(newer.Territories.Count == 2 && newer.Territories[0].PartCount == 2 && newer.Territories[0].Flags == TerritoryFlags.BuildProtect &&
                  newer.Territories[0].GetPart(1).Kind == TerritoryShapeKind.Square,
                "its territories read whole, skipping the key and the flag name this build does not know");

            // Everything this build or an older one wrote, and what an admin writes by hand, stays writable.
            Check(!TerritoryJson.Read(TerritoryJson.Write(new TerritoryJson.FileData())).FromNewerBuild, "a file this build writes is not newer");
            Check(!TerritoryJson.Read(@"{ ""version"": 3, ""territories"": [ { ""id"": 1, ""flags"": [ ""NoMonsterSpawns"", ""RunicBarrier"" ] } ] }").FromNewerBuild,
                "nor is a version 3 file, barrier flags and all: they are this build's own");
            Check(!TerritoryJson.Read(@"{ ""version"": 4, ""territories"": [ { ""id"": 1, ""flags"": [ ""ShowOnMap"", ""MapNameOnly"" ] } ] }").FromNewerBuild,
                "nor is a version 4 file, map-name flag and all: they are this build's own");
            Check(TerritoryJson.Read(@"{ ""version"": 3, ""territories"": [] }").Version == 3, "a version 3 file (0.3.x) reads as itself");
            Check(!TerritoryJson.Read(@"{ ""version"": 2, ""territories"": [] }").FromNewerBuild, "nor is one 0.2.x wrote");
            Check(!TerritoryJson.Read(V011File).FromNewerBuild, "nor is one 0.1.1 wrote");
            Check(!TerritoryJson.Read(@"{ ""territories"": [ { ""id"": 1 } ] }").FromNewerBuild, "nor a hand-written one with no version at all");
            Check(!TerritoryJson.Read(@"{ ""version"": 0, ""territories"": [] }").FromNewerBuild, "nor one that says version 0");
        }

        private static void FileSelfCheck()
        {
            // The very check the store runs before its first save (TerritoryStore.FormatRoundTrips).
            string ok = TerritoryJson.RoundTripProblem();
            Check(ok == "", $"the shipping format passes its own self-check, parts included (got '{ok}')");

            // Validate the instrument: hand it a writer or reader that loses something, and it has to say so.
            // Each sabotage is one a format bug could really cause - the JsonUtility trap dropped a whole list.
            // The sample's FIRST territory is the one with the parts, members and every flag, and the sabotages below are about it;
            // its second is the sub-territory (0.4.0), spoiled on its own further down.
            TerritoryJson.FileData Read(string json, Action<Territory> spoil)
            {
                TerritoryJson.FileData f = TerritoryJson.Read(json);
                spoil(f.Territories[0]);
                return f;
            }

            TerritoryJson.FileData ReadSub(string json, Action<Territory> spoil)
            {
                TerritoryJson.FileData f = TerritoryJson.Read(json);
                spoil(f.Territories[1]);
                return f;
            }

            string dropped = TerritoryJson.RoundTripProblem(TerritoryJson.Write, json => Read(json, t => t.ExtraParts.RemoveAt(t.ExtraParts.Count - 1)));
            Check(dropped == "parts", $"a reader that drops a part is caught as 'parts' (got '{dropped}')");

            string allGone = TerritoryJson.RoundTripProblem(TerritoryJson.Write, json => Read(json, t => t.ExtraParts.Clear()));
            Check(allGone == "parts", $"a reader that loses every part (got '{allGone}')");

            string swapped = TerritoryJson.RoundTripProblem(TerritoryJson.Write, json => Read(json, t => t.ExtraParts.Reverse()));
            Check(swapped == "parts", $"a reader that reorders the parts - the panel numbers them - (got '{swapped}')");

            string nudged = TerritoryJson.RoundTripProblem(TerritoryJson.Write, json => Read(json, t =>
            {
                TerritoryShape p = t.ExtraParts[0];
                t.ExtraParts[0] = new TerritoryShape(p.Kind, p.CenterX, p.CenterZ, MathF.BitIncrement(p.HalfX), p.HalfZ, p.Yaw);
            }));
            Check(nudged == "parts", $"a part's width off by the smallest step a float has (got '{nudged}')");

            string unwritten = TerritoryJson.RoundTripProblem(f =>
            {
                var copy = new TerritoryJson.FileData { WorldName = f.WorldName, WorldUid = f.WorldUid, NextId = f.NextId };
                foreach (Territory t in f.Territories)
                {
                    Territory c = t.Clone();
                    c.ExtraParts.Clear();
                    copy.Territories.Add(c);
                }
                return TerritoryJson.Write(copy);
            }, TerritoryJson.Read);
            Check(unwritten == "parts", $"a writer that never writes the parts (got '{unwritten}')");

            // 0.4.0: the sub-territory in the sample. Each sabotage is a way a format bug could lose what makes it one.
            string orphaned = TerritoryJson.RoundTripProblem(TerritoryJson.Write, json => ReadSub(json, t => t.ParentId = 0));
            Check(orphaned == "sub-territory: id/name/parent", $"a reader that loses the parent link (got '{orphaned}')");

            string unfollowed = TerritoryJson.RoundTripProblem(TerritoryJson.Write, json => ReadSub(json, t => t.Follow &= ~TerritoryFollow.Color));
            Check(unfollowed == "sub-territory: what it follows", $"a reader that loses one group it follows (got '{unfollowed}')");

            string ruleless = TerritoryJson.RoundTripProblem(TerritoryJson.Write, json => ReadSub(json, t => t.FollowFlags &= ~TerritoryFlags.Announce));
            Check(ruleless == "sub-territory: what it follows", $"a reader that makes a followed rule the territory's own (got '{ruleless}')");

            string forgot = TerritoryJson.RoundTripProblem(TerritoryJson.Write, json => ReadSub(json, t => t.PvP = PvPRule.Unchanged));
            Check(forgot == "sub-territory: its own rules", $"a reader that loses the PvP the sub-territory sets itself (got '{forgot}')");

            string numbers = TerritoryJson.RoundTripProblem(TerritoryJson.Write, json => ReadSub(json, t => t.CarryBonus = 0f));
            Check(numbers == "sub-territory: its own Sanctuary numbers", $"a reader that loses its own Sanctuary numbers (got '{numbers}')");

            string wroteAll = TerritoryJson.RoundTripProblem(f =>
            {
                // A writer that forgets to leave out what is followed: the file then SETS the colour, so the territory stops following it.
                var copy = new TerritoryJson.FileData { WorldName = f.WorldName, WorldUid = f.WorldUid, NextId = f.NextId };
                foreach (Territory t in f.Territories)
                {
                    Territory c = t.Clone();
                    c.ParentId = t.ParentId;
                    c.FollowFlags = TerritoryFlags.None;
                    c.Follow = TerritoryFollow.None;
                    copy.Territories.Add(c);
                }
                return TerritoryJson.Write(copy);
            }, TerritoryJson.Read);
            Check(wroteAll == "sub-territory: what it follows", $"a writer that writes everything, followed or not (got '{wroteAll}')");

            string member = TerritoryJson.RoundTripProblem(TerritoryJson.Write, json => Read(json, t => t.Members.RemoveAt(0)));
            Check(member == "members", $"and it still catches what it caught before: a lost member (got '{member}')");

            // 0.3.0: the sample sets every known flag, so the barrier's two are in it - which is exactly why losing either
            // is caught. (Were the sample to leave them out, none of these four would be noticed.)
            string unwalled = TerritoryJson.RoundTripProblem(TerritoryJson.Write, json => Read(json, t => t.Flags &= ~TerritoryFlags.RunicBarrier));
            Check(unwalled == "rules", $"a reader that loses the runic barrier is caught as 'rules' (got '{unwalled}')");

            string admitted = TerritoryJson.RoundTripProblem(TerritoryJson.Write, json => Read(json, t => t.Flags &= ~TerritoryFlags.BarrierKeepsOutVisitors));
            Check(admitted == "rules", $"a reader that loses the visitors option (got '{admitted}')");

            string unnamed = TerritoryJson.RoundTripProblem(f =>
            {
                var copy = new TerritoryJson.FileData { WorldName = f.WorldName, WorldUid = f.WorldUid, NextId = f.NextId };
                foreach (Territory t in f.Territories)
                {
                    Territory c = t.Clone();
                    c.Flags &= ~(TerritoryFlags.RunicBarrier | TerritoryFlags.BarrierKeepsOutVisitors);
                    copy.Territories.Add(c);
                }
                return TerritoryJson.Write(copy);
            }, TerritoryJson.Read);
            Check(unnamed == "rules", $"a writer that never writes the barrier's names - a 0.2.x server's save - (got '{unnamed}')");
        }

        // ─────────────────────────────────────────────────────────────
        // 0.3.0 - THE RUNIC BARRIER'S FLAGS
        // ─────────────────────────────────────────────────────────────

        private const TerritoryFlags NetNms = TerritoryFlags.NoMonsterSpawns;
        private const TerritoryFlags NetRune = TerritoryFlags.RunicBarrier;
        private const TerritoryFlags NetVisitors = TerritoryFlags.BarrierKeepsOutVisitors;

        /// <summary>What the server does to every territory it loads and every one a client sends: Sanitize. Returns the flags it leaves.</summary>
        private static TerritoryFlags NetSanitised(TerritoryFlags flags)
        {
            Territory t = Circle(1, 0f, 0f, 10f, flags);
            Check(t.Sanitize(600f), $"a territory with the flags {flags} sanitises");
            return t.Flags;
        }

        private static void NetBarrierRules()
        {
            // The bits are PERSISTED (file by name, wire as this int): they are only ever added to, never moved.
            Check((int)NetRune == 1 << 11 && (int)NetVisitors == 1 << 12, "the two flags are bits 11 and 12, the values 0.3.0 wrote to disk and wire");
            Check((Territory.KnownFlags & NetRune) != 0 && (Territory.KnownFlags & NetVisitors) != 0,
                "both are known flags, so sanitising does not strip them as unknown bits");

            // The rule, case by case, through Sanitize itself - the door every record goes through.
            TerritoryFlags bp = TerritoryFlags.BuildProtect;
            Check(NetSanitised(NetNms | NetRune) == (NetNms | NetRune), "the barrier stays on a territory that keeps monsters from spawning");
            Check(NetSanitised(NetNms | NetRune | NetVisitors) == (NetNms | NetRune | NetVisitors), "and so does its visitors option");
            Check(NetSanitised(NetRune) == TerritoryFlags.None, "a barrier without the no-monster rule is cleared");
            Check(NetSanitised(NetRune | NetVisitors) == TerritoryFlags.None, "and takes its visitors option with it: switching the no-monster rule off clears both");
            Check(NetSanitised(NetNms | NetVisitors) == NetNms, "the visitors option without the barrier is cleared; the no-monster rule under it is left");
            Check(NetSanitised(NetVisitors) == TerritoryFlags.None, "the visitors option on its own is cleared");
            Check(NetSanitised(bp | NetRune | NetVisitors) == bp, "every other rule is left exactly as it was when the barrier's go");
            Check(NetSanitised(bp | NetNms | NetRune) == (bp | NetNms | NetRune), "and when they stay");
            Check(NetSanitised(TerritoryFlags.None) == TerritoryFlags.None, "no flags is no flags");
            Check(NetSanitised(NetRune | (TerritoryFlags)(1 << 30)) == TerritoryFlags.None, "an unknown bit and an orphaned barrier go together");
            Check(NetSanitised(NetNms | NetRune | (TerritoryFlags)(1 << 30)) == (NetNms | NetRune), "an unknown bit alone goes, and the barrier next to it stays");

            // Every one of the 2^15 combinations of the flags this build knows, through the rule itself. One check
            // for the lot - a failing case is named in its message - so the tally is not 32,768 checks of one thing.
            // The flags with a prerequisite are the barrier's two and, from 0.3.2, the map's name-only option.
            const int KnownBits = 15;
            int bad = 0;
            string first = "";
            for (int bits = 0; bits < 1 << KnownBits; bits++)
            {
                var f = (TerritoryFlags)bits;
                TerritoryFlags c = Territory.CleanFlags(f);
                TerritoryFlags nameOnly = TerritoryFlags.MapNameOnly, onMap = TerritoryFlags.ShowOnMap;
                TerritoryFlags dependents = NetRune | NetVisitors | nameOnly;
                bool valid = (f & NetRune) == 0 || (f & NetNms) != 0;
                valid &= (f & NetVisitors) == 0 || (f & NetRune) != 0;
                valid &= (f & nameOnly) == 0 || (f & onMap) != 0;

                string why = null;
                if ((c & ~Territory.KnownFlags) != 0) why = "left an unknown bit";
                else if ((c & NetRune) != 0 && (c & NetNms) == 0) why = "left a barrier without the no-monster rule";
                else if ((c & NetVisitors) != 0 && (c & NetRune) == 0) why = "left the visitors option without the barrier";
                else if ((c & nameOnly) != 0 && (c & onMap) == 0) why = "left the name-only option without the map rule";
                else if (Territory.CleanFlags(c) != c) why = "was not stable: a second pass changed it";
                else if ((c & ~dependents) != (f & ~dependents)) why = "touched a flag that has no prerequisite";
                else if (valid && c != f) why = "changed a combination that already obeyed the rule";
                else if (!valid && c == f) why = "kept a flag whose prerequisite is off";

                if (why == null) continue;
                bad++;
                if (first.Length == 0) first = $"{f} {why}";
            }
            Check(bad == 0, $"{bad} of the {1 << KnownBits} flag combinations break the rule; the first: {first}");

            // A NEW territory: the panel's draft starts with the barrier on wherever it starts with no monster spawns on.
            TerritoryFlags draft = Territory.DraftFlags;
            Check((draft & NetNms) != 0 && (draft & NetRune) != 0, "a new draft keeps monsters from spawning and starts with the barrier on");
            Check((draft & NetVisitors) == 0, "but not the visitors option: keeping players out is the admin's choice");
            Check(draft == (Territory.DefaultFlags | NetRune), "it is the defaults plus the barrier, and nothing else");
            Check(Territory.CleanFlags(draft) == draft && NetSanitised(draft) == draft, "and it is a state the server keeps as it stands");

            // An EXISTING one keeps what it has: the defaults - which is what a record with no "flags" key reads as, and
            // what a bare territory is - have no barrier, so upgrading turns nothing on.
            Check((Territory.DefaultFlags & (NetRune | NetVisitors)) == 0, "the defaults carry no barrier");
            Check(new Territory().Flags == Territory.DefaultFlags && !new Territory().Has(NetRune), "nor does a bare territory");
            Territory bare = TerritoryJson.Read(@"{ ""territories"": [ { ""id"": 1 } ] }").Territories[0];
            Check(bare.Flags == Territory.DefaultFlags && !bare.Has(NetRune), "nor does a record with no flags key");
        }

        private static void NetMapNameOnly()
        {
            TerritoryFlags nameOnly = TerritoryFlags.MapNameOnly, onMap = TerritoryFlags.ShowOnMap;

            // PERSISTED (file by name, wire as the int): appended after 0.3.1's bit 13, never moved.
            Check((int)nameOnly == 1 << 14, "MapNameOnly is bit 14, after the door closer's bit 13");
            Check((Territory.KnownFlags & nameOnly) != 0, "it is a known flag, so sanitising does not strip it as an unknown bit");
            Check((Territory.DefaultFlags & nameOnly) == 0 && (Territory.DraftFlags & nameOnly) == 0, "it is in no default set: no territory gains it by upgrading, none starts with it");
            Check(!new Territory().Has(nameOnly), "a bare territory has it off");

            // The rule: a setting of the map rule, so it needs ShowOnMap - and nothing else needs it.
            Check(NetSanitised(onMap | nameOnly) == (onMap | nameOnly), "with the map rule on it survives the server's sanitising");
            Check(NetSanitised(nameOnly) == TerritoryFlags.None, "without the map rule it is cleared");
            Check(NetSanitised(TerritoryFlags.BuildProtect | nameOnly) == TerritoryFlags.BuildProtect, "and a rule beside it is left exactly as it was");
            Check(Territory.CleanFlags(Territory.KnownFlags & ~onMap) == (Territory.KnownFlags & ~onMap & ~nameOnly),
                "switching the map rule off takes the name-only option away and nothing else");
            Check(Territory.CleanFlags(Territory.KnownFlags) == Territory.KnownFlags, "every known flag on is a record the server keeps as it stands");

            // The file, by name.
            Territory a = Circle(1, 0f, 0f, 10f, onMap | nameOnly | TerritoryFlags.Announce);
            Territory b = Circle(2, 50f, 0f, 10f, onMap);
            var file = new TerritoryJson.FileData { WorldName = "Valtima", WorldUid = 1L, NextId = 3 };
            file.Territories.Add(a);
            file.Territories.Add(b);
            string json = TerritoryJson.Write(file);
            Check(NetCount(json, "\"MapNameOnly\"") == 1, "written as a name, on the territory that has it and no other");
            TerritoryJson.FileData back = TerritoryJson.Read(json);
            Check(back.Territories[0].Flags == (onMap | nameOnly | TerritoryFlags.Announce) && back.Territories[1].Flags == onMap, "and read back exactly, none gained or lost");
            Check(TerritoryJson.Write(back) == json, "write -> read -> write is stable");
            Check(TerritoryJson.Read(@"{ ""territories"": [ { ""id"": 1, ""flags"": [ ""showonmap"", ""MAPNAMEONLY"" ] } ] }").Territories[0].Flags == (onMap | nameOnly),
                "a hand-written name in any case is read");
            Check(!TerritoryJson.Read(@"{ ""territories"": [ { ""id"": 1 } ] }").Territories[0].Has(nameOnly), "a record with no flags key does not gain it");
            Check(TerritoryJson.RoundTripProblem() == "", "the store's own round-trip check, which carries every known flag, still passes");

            // The wire: the int carries the bit; and the record is still version 2, so no older reader is surprised by its shape.
            var pkg = new ZPackage();
            a.Write(pkg);
            Check(Territory.Read(pkg.Transmit()).Flags == a.Flags, "the int carries the bit in a record");
            Check(Territory.RecordVersion == 3, "the record is version 3 (0.4.0's parent and follow marks); the flag itself still rides in the flags int");
        }

        private static void NetBarrierLines()
        {
            string[] Lines(TerritoryFlags f) => Circle(1, 0f, 0f, 10f, f).FeatureLines(10f).ToArray();

            Check(Lines(NetNms).SequenceEqual(new[] { "No monster spawns" }), "no barrier, no barrier line");
            Check(Lines(NetNms | NetRune).SequenceEqual(new[] { "No monster spawns", "Runic barrier: monsters cannot walk in" }),
                "the barrier is named right after the no-monster rule it belongs to");
            Check(Lines(NetNms | NetRune | NetVisitors).SequenceEqual(new[] { "No monster spawns", "Runic barrier: monsters and non-members cannot walk in" }),
                "with the visitors option it is still ONE line, and says who else is held out");
            Check(Lines(NetNms | NetRune | TerritoryFlags.NoRaids).SequenceEqual(new[] { "No monster spawns", "Runic barrier: monsters cannot walk in", "No raids" }),
                "and the lines after it keep their order");
            Check(Lines(TerritoryFlags.None).Length == 0, "a territory with no rules lists none");
        }

        private static Territory NetWalled()
        {
            Territory t = Sample();
            t.Flags = TerritoryFlags.BuildProtect | NetNms | NetRune | NetVisitors | TerritoryFlags.ShowBorder;
            return t;
        }

        private static void NetBarrierTravel()
        {
            // THE FILE: flags are listed by name, so a barrier is "RunicBarrier" in the text, and back again from it.
            Territory both = NetWalled();
            Territory monstersOnly = Circle(3, -10.125f, 20.5f, 7.75f, NetNms | NetRune);
            Territory none = Circle(4, 50f, 60f, 9f, NetNms);
            var file = new TerritoryJson.FileData { WorldName = "Midgard", WorldUid = 42L, NextId = 5 };
            file.Territories.Add(both);
            file.Territories.Add(monstersOnly);
            file.Territories.Add(none);

            string json = TerritoryJson.Write(file);
            Check(json.Contains("\"RunicBarrier\"") && json.Contains("\"BarrierKeepsOutVisitors\""), "both names are written as names, not numbers");
            Check(NetCount(json, "\"RunicBarrier\"") == 2 && NetCount(json, "\"BarrierKeepsOutVisitors\"") == 1,
                "each appears only on the territories that have it");
            TerritoryJson.FileData back = TerritoryJson.Read(json);
            Check(back.Territories.Count == 3, "three territories back");
            SameTerritory(both, back.Territories[0], "file: barrier and visitors");
            SameTerritory(monstersOnly, back.Territories[1], "file: barrier alone");
            SameTerritory(none, back.Territories[2], "file: no barrier");
            Check(back.Territories[0].Flags == both.Flags && back.Territories[1].Flags == (NetNms | NetRune) && back.Territories[2].Flags == NetNms,
                "the flags come back exactly, none gained or lost");
            Check(TerritoryJson.Write(back) == json, "write -> read -> write is stable");
            foreach (Territory t in back.Territories)
            {
                TerritoryFlags before = t.Flags;
                Check(t.Sanitize(100000f) && t.Flags == before, $"loading #{t.Id} as the store does leaves its flags alone");
            }

            // Hand-written: any case, next to other names, and a name from a newer build skipped as before.
            TerritoryJson.FileData hand = TerritoryJson.Read(@"{ ""territories"": [
  { ""id"": 1, ""flags"": [ ""nomonsterspawns"", ""RUNICBARRIER"", ""barrierkeepsoutvisitors"", ""SkyfallWard"" ] },
  { ""id"": 2, ""flags"": [ ""RunicBarrier"", ""BarrierKeepsOutVisitors"", ""BuildProtect"" ] } ] }");
            Check(hand.Territories[0].Flags == (NetNms | NetRune | NetVisitors), "names in any case, and an unknown name skipped");
            Check(hand.Territories[1].Flags == (TerritoryFlags.BuildProtect | NetRune | NetVisitors), "the file reader reads what is written, rule or no rule");

            // ... and the rule is the STORE's, at load, not the reader's: the barrier without its prerequisite is
            // cleared, not refused, and the next save writes the file as the server enforces it.
            Territory orphan = hand.Territories[1];
            Check(orphan.Sanitize(100000f) && orphan.Flags == TerritoryFlags.BuildProtect, "a hand edit with a barrier and no no-monster rule loads without the barrier");
            string resaved = TerritoryJson.Write(hand);
            Check(NetCount(resaved, "\"RunicBarrier\"") == 1 && NetCount(resaved, "\"BarrierKeepsOutVisitors\"") == 1,
                "and the file, saved again, says so: only the territory that obeys the rule still lists the barrier");
            Check(TerritoryJson.Read(resaved).Territories[1].Flags == TerritoryFlags.BuildProtect, "the cleared territory reads back cleared");

            // A 0.2.x server, rolled back to, must refuse to save over this file. Its guard is "version > 2" on the
            // number in the file - so what matters is the number this build actually WRITES.
            TerritoryJson.FileData reread = TerritoryJson.Read(json);
            Check(reread.Version == 5 && reread.Version > 3, "the file says version 5, above the 3 that 0.3.0 and 0.3.1 let through and the 2 that 0.2.x does");
            Check(!reread.FromNewerBuild, "and this build itself still saves it");

            // THE WIRE: the int carries both bits, in a record, in a snapshot and in a request.
            Territory w = NetWalled();
            var pkg = new ZPackage();
            w.Write(pkg);
            Territory rec = Territory.Read(pkg.Transmit());
            SameTerritory(w, rec, "wire record");
            Check((rec.Flags & NetRune) != 0 && (rec.Flags & NetVisitors) != 0, "both bits arrive in a record");

            Protocol.Snapshot snap = Protocol.ReadSnapshot(Protocol.WriteSnapshot(777L, 3, youAreAdmin: false, adminsBypass: true,
                new[] { w, Circle(2, 0f, 0f, 5f, NetNms | NetRune) }, null).Transmit());
            Check(snap.Territories.Count == 2 && snap.Territories[0].Flags == w.Flags && snap.Territories[1].Flags == (NetNms | NetRune),
                "and in a snapshot, each territory its own");

            Protocol.Request req = Protocol.ReadRequest(Protocol.WriteRequest(1, RequestOp.Update, w, w.Id).Transmit());
            Check(req.Record != null && req.Record.Flags == w.Flags, "and in an admin's request");

            // A client that ignores the rule is not believed: the wire reader carries whatever it is given, and the
            // server's Sanitize - which runs on every request - is what clears it.
            Territory sneaky = NetWalled();
            sneaky.Flags = NetRune | NetVisitors;
            Protocol.Request lie = Protocol.ReadRequest(Protocol.WriteRequest(2, RequestOp.Create, sneaky, 0).Transmit());
            Check(lie.Record.Flags == (NetRune | NetVisitors), "the reader hands over what was sent");
            Check(lie.Record.Sanitize(600f) && lie.Record.Flags == TerritoryFlags.None, "and the server's Sanitize clears a barrier that has no no-monster rule under it");
        }

        private static void NetBarrierEdits()
        {
            // One territory, one barrier: it stands around the whole ground, and the territory combined INTO decides
            // whether there is one - the joined territory's rules are dropped, this one's included.
            Territory into = Compound(1, Part(TerritoryShapeKind.Circle, 0f, 0f, 20f));
            into.Flags = NetNms | NetRune | NetVisitors;
            Territory plain = Compound(2, Part(TerritoryShapeKind.Circle, 100f, 0f, 20f));
            plain.Flags = TerritoryFlags.None;
            Check(TerritoryEdits.Combine(into, plain, 16, out _) == null && into.Flags == (NetNms | NetRune | NetVisitors),
                "joining a territory with no barrier into one that has it leaves the barrier, visitors option and all");

            Territory bare = Compound(3, Part(TerritoryShapeKind.Circle, 0f, 0f, 20f));
            bare.Flags = NetNms;
            Territory walled = Compound(4, Part(TerritoryShapeKind.Circle, 100f, 0f, 20f));
            walled.Flags = NetNms | NetRune | NetVisitors;
            Check(TerritoryEdits.Combine(bare, walled, 16, out _) == null && bare.Flags == NetNms,
                "joining a walled territory into one without does not bring its barrier along");

            // A part split off is a territory of its own with the same rules - the barrier is one of them.
            Territory keep = Compound(5, Part(TerritoryShapeKind.Circle, 0f, 0f, 20f), Part(TerritoryShapeKind.Square, 60f, 0f, 10f));
            keep.Flags = NetNms | NetRune;
            Territory split = TerritoryEdits.Split(keep, 1, out string err);
            Check(split != null && err == null && split.Flags == (NetNms | NetRune) && keep.Flags == (NetNms | NetRune),
                "a part split off keeps the barrier, and so does the territory it left");
            Check(split != null && split.Sanitize(600f) && split.Flags == (NetNms | NetRune), "and the server keeps it as sent");
        }
    }
}
