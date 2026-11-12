using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace TerroritySmellatory.Tests
{
    // 0.3.1: the auto-close doors rule's decisions (Rules/DoorMath.cs) - the flag itself, which territory's rule counts for a
    // door, how near a player keeps it open, the open-door clock, and the list of excluded prefabs. The part that touches the
    // game (Rules/DoorCloser.cs, Rules/OwnershipHold.cs: the sector scan, the ownership hold, the write) is checked by the
    // e2e rig with a real door; everything it decides is here, and DoorSimulatedScans runs the closer's exact sequence of
    // calls over fake doors, players and a clock.
    internal static partial class Program
    {
        static partial void DoorGroups()
        {
            Run("doors: the flag is bit 13, known, off by default, needs nothing, and travels by name", DoorFlag);
            Run("doors: the rule's line, in the delay the server sets", DoorLines);
            Run("doors: only the territory that GOVERNS a door's spot counts - nesting, priority, compounds, interiors", DoorGoverning);
            Run("doors: a player within the clearance keeps a door open (a straight line, height included)", DoorClearance);
            Run("doors: the clock - first sight, delay, closed, outside, close, prune", DoorClockRules);
            Run("doors: the excluded-prefab list is read the way it is written", DoorNames);
            Run("doors: 60 s of half-second scans over fake doors, players and territories", DoorSimulatedScans);
        }

        private const TerritoryFlags DoorAuto = TerritoryFlags.AutoCloseDoors;

        private static Vector3 DoorAt(float x, float z, float y = 0f) => new Vector3(x, y, z);

        // ─────────────────────────────────────────────────────────────
        // THE FLAG
        // ─────────────────────────────────────────────────────────────

        private static void DoorFlag()
        {
            // PERSISTED (file by name, wire as the int): appended after 0.3.0's bit 12, never moved.
            Check((int)DoorAuto == 1 << 13, "AutoCloseDoors is bit 13, after the barrier's bit 12");
            Check((Territory.KnownFlags & DoorAuto) != 0, "it is a known flag, so sanitising does not strip it as an unknown bit");
            Check((Territory.DefaultFlags & DoorAuto) == 0, "it is not in the defaults: a territory gains no door closer by upgrading");
            Check((Territory.DraftFlags & DoorAuto) == 0, "nor in the panel's draft: a new territory does not start with it");
            Check(!new Territory().Has(DoorAuto), "a bare territory has it off");

            // No prerequisite: the flag alone survives, and no other flag's rule takes it away.
            Check(NetSanitised(DoorAuto) == DoorAuto, "on its own it survives the server's sanitising");
            Check(Territory.CleanFlags(DoorAuto) == DoorAuto, "CleanFlags leaves it alone");
            Check(Territory.CleanFlags(DoorAuto | TerritoryFlags.RunicBarrier | TerritoryFlags.BarrierKeepsOutVisitors) == DoorAuto,
                "switching the no-monster rule off clears the barrier under it and not the door closer");
            Check(Territory.CleanFlags(Territory.KnownFlags) == Territory.KnownFlags, "every known flag on is a record the server keeps as it stands");
            Check(NetSanitised(TerritoryFlags.AccessProtect | DoorAuto) == (TerritoryFlags.AccessProtect | DoorAuto), "it sits beside the access rule");

            // The file, by name.
            Territory a = Circle(1, 0f, 0f, 10f, DoorAuto | TerritoryFlags.BuildProtect);
            Territory b = Circle(2, 50f, 0f, 10f, TerritoryFlags.BuildProtect);
            var file = new TerritoryJson.FileData { WorldName = "Midgard", WorldUid = 1L, NextId = 3 };
            file.Territories.Add(a);
            file.Territories.Add(b);
            string json = TerritoryJson.Write(file);
            Check(json.Contains("\"AutoCloseDoors\"") && NetCount(json, "\"AutoCloseDoors\"") == 1, "written as a name, on the territory that has it and no other");
            TerritoryJson.FileData back = TerritoryJson.Read(json);
            Check(back.Territories[0].Flags == (DoorAuto | TerritoryFlags.BuildProtect) && back.Territories[1].Flags == TerritoryFlags.BuildProtect,
                "and read back exactly, none gained or lost");
            Check(TerritoryJson.Write(back) == json, "write -> read -> write is stable");
            Check(TerritoryJson.Read(@"{ ""territories"": [ { ""id"": 1, ""flags"": [ ""autoclosedoors"" ] } ] }").Territories[0].Flags == DoorAuto,
                "a hand-written name in any case is read");
            Check(!TerritoryJson.Read(@"{ ""territories"": [ { ""id"": 1 } ] }").Territories[0].Has(DoorAuto), "a record with no flags key does not gain it");
            Check(TerritoryJson.RoundTripProblem() == "", "the store's own round-trip check, which carries every known flag, still passes");

            // The wire.
            Territory w = Circle(3, 5f, 5f, 12f, DoorAuto | TerritoryFlags.NoMonsterSpawns);
            var pkg = new ZPackage();
            w.Write(pkg);
            Check(Territory.Read(pkg.Transmit()).Flags == w.Flags, "the int carries the bit in a record");
            Protocol.Snapshot snap = Protocol.ReadSnapshot(Protocol.WriteSnapshot(9L, 1, youAreAdmin: false, adminsBypass: true, new[] { w, b }, null).Transmit());
            Check(snap.Territories[0].Has(DoorAuto) && !snap.Territories[1].Has(DoorAuto), "and in a snapshot, each territory its own");
        }

        private static void DoorLines()
        {
            string[] Lines(TerritoryFlags f, float seconds) => Circle(1, 0f, 0f, 10f, f).FeatureLines(seconds).ToArray();

            Check(Lines(DoorAuto, 10f).SequenceEqual(new[] { "Doors close by themselves after 10 s" }), "the default delay");
            Check(Lines(DoorAuto, 2.5f).SequenceEqual(new[] { "Doors close by themselves after 2.5 s" }), "a fractional delay keeps its fraction");
            Check(Lines(DoorAuto, 600f).SequenceEqual(new[] { "Doors close by themselves after 600 s" }), "the longest delay");
            Check(Lines(TerritoryFlags.None, 10f).Length == 0, "off: no line, and nothing of the delay leaks into other territories");
            Check(Lines(TerritoryFlags.NoRaids | DoorAuto | TerritoryFlags.NoSkillLoss, 10f)
                    .SequenceEqual(new[] { "No raids", "Doors close by themselves after 10 s", "No skill loss on death" }),
                "it sits with the other world rules, in a fixed order");
        }

        // ─────────────────────────────────────────────────────────────
        // WHICH TERRITORY
        // ─────────────────────────────────────────────────────────────

        private static void DoorGoverning()
        {
            TerritoryIndex.Clear();
            Check(DoorMath.ClosingTerritory(DoorAt(0f, 0f)) == null, "no territories: open country, nothing closes");
            Check(!DoorMath.AnyCloses(TerritoryIndex.All), "and nothing to look for");

            // One territory.
            TerritoryIndex.Set(new[] { Circle(1, 0f, 0f, 50f, DoorAuto), Circle(2, 500f, 0f, 50f, TerritoryFlags.BuildProtect) });
            Check(DoorMath.ClosingTerritory(DoorAt(10f, 10f))?.Id == 1, "a door inside a territory with the rule");
            Check(DoorMath.ClosingTerritory(DoorAt(49.9f, 0f))?.Id == 1, "just inside its edge");
            Check(DoorMath.ClosingTerritory(DoorAt(50.1f, 0f)) == null, "just outside it");
            Check(DoorMath.ClosingTerritory(DoorAt(500f, 0f)) == null, "inside a territory without the rule");
            Check(DoorMath.ClosingTerritory(DoorAt(10f, 10f, y: 400f))?.Id == 1, "height does not matter on the surface, as for every rule");
            Check(DoorMath.AnyCloses(TerritoryIndex.All), "one territory has it: the server looks");

            // Nested: the one that governs the spot decides, whichever way round.
            TerritoryIndex.Set(new[] { Circle(1, 0f, 0f, 100f, DoorAuto), Circle(2, 20f, 0f, 10f, TerritoryFlags.None) });
            Check(DoorMath.ClosingTerritory(DoorAt(20f, 0f)) == null, "a market inside a kingdom that closes its doors: the market says no, so its doors stay as they are");
            Check(DoorMath.ClosingTerritory(DoorAt(-50f, 0f))?.Id == 1, "the kingdom's other doors close");
            TerritoryIndex.Set(new[] { Circle(1, 0f, 0f, 100f, TerritoryFlags.BuildProtect), Circle(2, 20f, 0f, 10f, DoorAuto) });
            Check(DoorMath.ClosingTerritory(DoorAt(20f, 0f))?.Id == 2, "a market that closes its doors inside a kingdom that does not: honoured");
            Check(DoorMath.ClosingTerritory(DoorAt(-50f, 0f)) == null, "the kingdom's own doors stay as they are");

            // Priority beats size, as everywhere.
            TerritoryIndex.Set(new[] { Circle(1, 0f, 0f, 100f, DoorAuto, priority: 5), Circle(2, 20f, 0f, 10f, TerritoryFlags.None) });
            Check(DoorMath.ClosingTerritory(DoorAt(20f, 0f))?.Id == 1, "the higher-priority kingdom governs the market's spot, so its rule counts there");
            TerritoryIndex.Set(new[] { Circle(1, 0f, 0f, 100f, TerritoryFlags.None, priority: 5), Circle(2, 20f, 0f, 10f, DoorAuto) });
            Check(DoorMath.ClosingTerritory(DoorAt(20f, 0f)) == null, "and the reverse: the high-priority territory without it keeps its doors");

            // Two overlapping territories both with the rule: either way one of them, and never a throw.
            TerritoryIndex.Set(new[] { Circle(1, 0f, 0f, 30f, DoorAuto), Circle(2, 20f, 0f, 30f, DoorAuto) });
            Check(DoorMath.ClosingTerritory(DoorAt(10f, 0f)) != null, "overlapping territories that both close doors: the door closes");

            // A compound territory is one area: the rule holds in every part, and not in the gap between them.
            Territory ell = Compound(7, Part(TerritoryShapeKind.Rectangle, 0f, 0f, 20f, 10f), Part(TerritoryShapeKind.Rectangle, 10f, 20f, 10f, 20f));
            ell.Flags = DoorAuto;
            TerritoryIndex.Set(new[] { ell });
            Check(DoorMath.ClosingTerritory(DoorAt(-15f, 0f))?.Id == 7, "a door in the first part");
            Check(DoorMath.ClosingTerritory(DoorAt(15f, 35f))?.Id == 7, "a door in a part added later");
            Check(DoorMath.ClosingTerritory(DoorAt(-15f, 30f)) == null, "the notch of the L is outside");

            // A dungeon interior sits thousands of metres up, under the same map coordinates as its entrance; the ground
            // ignores height, so without the interior rule a territory over an entrance would 'contain' the whole dungeon.
            TerritoryIndex.Set(new[] { Circle(1, 0f, 0f, 100f, DoorAuto) });
            Check(DoorMath.ClosingTerritory(DoorAt(0f, 0f, y: DoorMath.InteriorHeight)) != null, "exactly at the interior height is still the surface (vanilla's test is y > 3000)");
            Check(DoorMath.ClosingTerritory(DoorAt(0f, 0f, y: DoorMath.InteriorHeight + 1f)) == null, "a door in a dungeon interior is left alone");
            Check(DoorMath.ClosingTerritory(DoorAt(0f, 0f, y: 5000f)) == null, "the interior's usual height");
            Check(DoorMath.ClosingTerritory(DoorAt(0f, 0f, y: -50f)) != null, "and below the surface (a cellar, a cave mouth) is not an interior");

            Check(!DoorMath.AnyCloses(null), "no list, nothing");
            TerritoryIndex.Clear();
        }

        // ─────────────────────────────────────────────────────────────
        // A PLAYER NEAR
        // ─────────────────────────────────────────────────────────────

        private static void DoorClearance()
        {
            Vector3 door = DoorAt(100f, 100f, 10f);
            List<Vector3> One(Vector3 p) => new List<Vector3> { p };

            Check(DoorMath.AnyWithin(One(DoorAt(101f, 100f, 10f)), door, 3f), "a player 1 m away");
            Check(DoorMath.AnyWithin(One(door), door, 3f), "a player in the doorway");
            Check(DoorMath.AnyWithin(One(DoorAt(103f, 100f, 10f)), door, 3f), "exactly at the clearance counts as near");
            Check(!DoorMath.AnyWithin(One(DoorAt(103.01f, 100f, 10f)), door, 3f), "a hair past it does not");
            Check(!DoorMath.AnyWithin(One(DoorAt(110f, 100f, 10f)), door, 3f), "a player 10 m away");

            // A straight line, height included: someone on the roof or in the cellar is not in the doorway.
            Check(DoorMath.AnyWithin(One(DoorAt(100f, 100f, 12.9f)), door, 3f), "2.9 m straight above");
            Check(!DoorMath.AnyWithin(One(DoorAt(100f, 100f, 13.1f)), door, 3f), "3.1 m straight above");
            Check(!DoorMath.AnyWithin(One(DoorAt(102f, 100f, 12.5f)), door, 3f), "2 m along and 2.5 m up is 3.2 m away, not 3");
            Check(DoorMath.AnyWithin(One(DoorAt(101.5f, 100f, 11.5f)), door, 3f), "1.5 m along and 1.5 m up is 2.1 m away");

            // The rest of the room.
            var players = new List<Vector3> { DoorAt(300f, 300f), DoorAt(-50f, 20f), DoorAt(100f, 102f, 10f), DoorAt(0f, 0f) };
            Check(DoorMath.AnyWithin(players, door, 3f), "any one of several is enough");
            players.RemoveAt(2);
            Check(!DoorMath.AnyWithin(players, door, 3f), "and with that one gone, nobody is near");
            Check(!DoorMath.AnyWithin(new List<Vector3>(), door, 3f), "nobody online");
            Check(!DoorMath.AnyWithin(null, door, 3f), "no list");

            // 0 turns the check off (the server setting's range starts at 0).
            Check(!DoorMath.AnyWithin(One(door), door, 0f), "clearance 0: even a player in the doorway does not keep it open");
            Check(!DoorMath.AnyWithin(One(door), door, -1f), "a negative clearance is off too");
            Check(DoorMath.AnyWithin(One(DoorAt(129f, 100f, 10f)), door, 30f), "the widest clearance, 30 m");
            Check(!DoorMath.AnyWithin(One(DoorAt(131f, 100f, 10f)), door, 30f), "and no further");
        }

        // ─────────────────────────────────────────────────────────────
        // THE CLOCK
        // ─────────────────────────────────────────────────────────────

        private static DoorStage DoorLook(DoorClock<int> clock, int id, bool open, bool governed, float now, float delay, out float openFor)
        {
            clock.BeginScan();
            DoorStage stage = clock.Observe(id, open, governed, now, delay, out openFor);
            clock.EndScan();
            return stage;
        }

        private static void DoorClockRules()
        {
            var clock = new DoorClock<int>();
            float f;

            // First sight starts the clock; it is due when the whole delay has passed, not a scan before.
            Check(DoorLook(clock, 1, true, true, 100f, 10f, out f) == DoorStage.Counting && f == 0f, "first seen open: counting from now, 0 s open");
            Check(clock.Tracked == 1, "one timer runs");
            Check(DoorLook(clock, 1, true, true, 105f, 10f, out f) == DoorStage.Counting && f == 5f, "5 s later: still counting, and says how long");
            Check(DoorLook(clock, 1, true, true, 109.99f, 10f, out f) == DoorStage.Counting, "a hair before the delay: not yet");
            Check(DoorLook(clock, 1, true, true, 110f, 10f, out f) == DoorStage.Due && f == 10f, "exactly the delay: due");
            Check(DoorLook(clock, 1, true, true, 111f, 10f, out f) == DoorStage.Due && f == 11f, "and stays due while the caller does not close it (a player is near)");
            Check(DoorLook(clock, 1, true, true, 500f, 10f, out f) == DoorStage.Due, "for as long as it takes them to leave");

            // Closed by us: the timer ends and a later opening starts a fresh one.
            clock.Closed(1);
            Check(clock.Tracked == 0, "closing it ends its timer");
            Check(DoorLook(clock, 1, true, true, 501f, 10f, out f) == DoorStage.Counting && f == 0f, "opened again: a new full delay");
            Check(DoorLook(clock, 1, true, true, 510f, 10f, out f) == DoorStage.Counting, "9 s into it: not due");
            Check(DoorLook(clock, 1, true, true, 511f, 10f, out f) == DoorStage.Due, "10 s into it: due");

            // Shut by a player: forgotten, so a reopening does not inherit the old clock.
            clock.Clear();
            DoorLook(clock, 2, true, true, 0f, 10f, out f);
            Check(DoorLook(clock, 2, false, true, 6f, 10f, out f) == DoorStage.Ignore && clock.Tracked == 0, "a closed door is ignored and its clock dropped");
            Check(DoorLook(clock, 2, true, true, 9f, 10f, out f) == DoorStage.Counting && f == 0f, "reopened at 9 s: counting from 9, not from 0");
            Check(DoorLook(clock, 2, true, true, 12f, 10f, out f) == DoorStage.Counting, "so at 12 s it is 3 s open, not 12");

            // Out of every territory that closes doors (never in one, or the rule was switched off): nothing runs.
            clock.Clear();
            Check(DoorLook(clock, 3, true, false, 0f, 10f, out f) == DoorStage.Ignore && clock.Tracked == 0, "an open door outside them is ignored");
            DoorLook(clock, 3, true, true, 1f, 10f, out f);
            Check(DoorLook(clock, 3, true, false, 5f, 10f, out f) == DoorStage.Ignore && clock.Tracked == 0, "the rule switched off mid-count drops the clock");
            Check(DoorLook(clock, 3, true, true, 8f, 10f, out f) == DoorStage.Counting && f == 0f, "and switched on again it starts afresh");

            // A door not looked at in a scan (its player left, it is held, it is excluded) loses its timer at the scan's end.
            clock.Clear();
            DoorLook(clock, 4, true, true, 0f, 10f, out f);
            clock.BeginScan();
            clock.EndScan();
            Check(clock.Tracked == 0, "a scan that did not see it drops its clock");
            Check(DoorLook(clock, 4, true, true, 50f, 10f, out f) == DoorStage.Counting && f == 0f, "seen again much later: counting afresh, not closed on the stale clock");

            // Several doors, one clock each; one scan looks at all of them.
            clock.Clear();
            clock.BeginScan();
            clock.Observe(10, true, true, 0f, 10f, out f);
            clock.Observe(11, true, true, 0f, 10f, out f);
            clock.Observe(12, true, true, 0f, 10f, out f);
            clock.EndScan();
            Check(clock.Tracked == 3, "three doors, three clocks");
            clock.BeginScan();
            DoorStage s10 = clock.Observe(10, true, true, 10f, 10f, out f);
            DoorStage s11 = clock.Observe(11, false, true, 10f, 10f, out f);
            // 12 is not looked at this scan
            clock.EndScan();
            Check(s10 == DoorStage.Due && s11 == DoorStage.Ignore, "one due, one shut in the meantime");
            Check(clock.Tracked == 1, "the shut one and the one not looked at are gone; the due one keeps counting");

            // The delay is the server's, read every scan: lengthened mid-count it holds the door longer, shortened it closes sooner.
            clock.Clear();
            DoorLook(clock, 5, true, true, 0f, 10f, out f);
            Check(DoorLook(clock, 5, true, true, 8f, 10f, out f) == DoorStage.Counting, "8 s into a 10 s delay");
            Check(DoorLook(clock, 5, true, true, 8f, 5f, out f) == DoorStage.Due, "the admin shortens the delay to 5 s: due now");
            Check(DoorLook(clock, 5, true, true, 8f, 30f, out f) == DoorStage.Counting, "or lengthens it to 30 s: not yet");

            // ZDOIDs are structs; the clock takes any key.
            var byPair = new DoorClock<(long, uint)>();
            byPair.BeginScan();
            byPair.Observe((7L, 1u), true, true, 0f, 1f, out f);
            byPair.Observe((7L, 2u), true, true, 0f, 1f, out f);
            byPair.EndScan();
            Check(byPair.Tracked == 2, "two keys that differ in one field are two doors");
        }

        // ─────────────────────────────────────────────────────────────
        // THE EXCLUDED LIST
        // ─────────────────────────────────────────────────────────────

        private static void DoorNames()
        {
            List<string> shipped = DoorMath.ParseNames(DoorMath.DefaultExcluded);
            Check(shipped.SequenceEqual(new[] { "piece_drawbridge", "piece_drawbridge_log", "Morkhalla_Drawbridge", "wood_window" }),
                "the shipped list: the two drawbridges, the mod's drawbridge and the window shutters, in order");
            Check(DoorMath.ParseNames("  piece_drawbridge ,, WOOD_window,wood_window , ").SequenceEqual(new[] { "piece_drawbridge", "WOOD_window" }),
                "trimmed, blanks skipped, a repeat in another case dropped, the first spelling kept");
            Check(DoorMath.ParseNames("").Count == 0 && DoorMath.ParseNames(null).Count == 0 && DoorMath.ParseNames(" , ,").Count == 0, "nothing listed: nothing excluded");
            Check(DoorMath.ParseNames("solo").SequenceEqual(new[] { "solo" }), "one name, no commas");
        }

        // ─────────────────────────────────────────────────────────────
        // THE WHOLE LOOP, OFF-GAME
        // ─────────────────────────────────────────────────────────────

        private sealed class DoorSim
        {
            public int Id;
            public Vector3 At;
            public bool Open;
            public bool Excluded;
            public bool Held;
                    }

        private static void DoorSimulatedScans()
        {
            // Two territories on one map: a keep that closes its doors, and a market inside the road that does not.
            //   keep    circle at (0,0) r 60, rule on           market  circle at (30,0) r 8, rule off (governs its own spot)
            //   yard    circle at (300,0) r 40, rule off         open country beyond
            TerritoryIndex.Set(new[]
            {
                Circle(1, 0f, 0f, 60f, DoorAuto),
                Circle(2, 30f, 0f, 8f, TerritoryFlags.None),
                Circle(3, 300f, 0f, 40f, TerritoryFlags.BuildProtect),
            });

            var doors = new List<DoorSim>
            {
                new DoorSim { Id = 1, At = DoorAt(-20f, 0f) },                      // in the keep, nobody near: closes on time
                new DoorSim { Id = 2, At = DoorAt(0f, 30f) },                       // in the keep, a player stands by it until t = 20
                new DoorSim { Id = 3, At = DoorAt(30f, 0f) },                       // in the market: never
                new DoorSim { Id = 4, At = DoorAt(300f, 0f) },                      // in the yard (rule off): never
                new DoorSim { Id = 5, At = DoorAt(1000f, 1000f) },                  // open country: never
                new DoorSim { Id = 6, At = DoorAt(10f, 10f), Excluded = true },     // a drawbridge in the keep: never
                new DoorSim { Id = 7, At = DoorAt(0f, -30f) },                      // in the keep, shut by a player at t = 4 and reopened at t = 8
                new DoorSim { Id = 8, At = DoorAt(0f, 0f, y: 5000f) },              // a dungeon interior under the keep: never
                new DoorSim { Id = 9, At = DoorAt(40f, 0f) },                       // in the keep, held (a hold in flight) for the first 30 s
            };
            const float delay = 10f, clearance = 3f;
            float playerLeavesAt = 20f;

            var clock = new DoorClock<int>();
            var players = new List<Vector3>();
            var closedAt = new Dictionary<int, float>();

            for (float now = 0f; now <= 60f; now += 0.5f)
            {
                // Player actions and the world, between scans.
                if (now == 1f) foreach (DoorSim d in doors) d.Open = true;
                DoorSim seven = doors.First(d => d.Id == 7);
                if (now == 4f) seven.Open = false;
                if (now == 8f) seven.Open = true;
                foreach (DoorSim d in doors) if (d.Id == 9) d.Held = now < 30f;

                players.Clear();
                players.Add(DoorAt(2000f, 2000f)); // somebody is always online, far from everything
                if (now < playerLeavesAt) players.Add(DoorAt(0f, 31.5f)); // 1.5 m from door 2

                // The closer's scan, in the closer's order.
                clock.BeginScan();
                foreach (DoorSim d in doors)
                {
                    if (d.Held || d.Excluded) continue;
                    Territory territory = d.Open ? DoorMath.ClosingTerritory(d.At) : null;
                    DoorStage stage = clock.Observe(d.Id, d.Open, territory != null, now, delay, out float openFor);
                    if (stage != DoorStage.Due) continue;
                    if (DoorMath.AnyWithin(players, d.At, clearance)) continue;
                    d.Open = false;
                    clock.Closed(d.Id);
                    closedAt[d.Id] = now;
                }
                clock.EndScan();
            }

            // Everything opens at t = 1 (the first scan at or after it), and is first SEEN open at that scan.
            Check(closedAt.TryGetValue(1, out float c1) && c1 == 11f, $"door 1: opened at 1, closed at the first scan 10 s after it was first seen open (got {(closedAt.TryGetValue(1, out float x1) ? x1 : -1f)})");
            Check(closedAt.TryGetValue(2, out float c2) && c2 == 20f, $"door 2: due at 11, kept open by the player, closed at the first scan without them, at 20 (got {(closedAt.TryGetValue(2, out float x2) ? x2 : -1f)})");
            Check(!closedAt.ContainsKey(3), "door 3, in the market inside the keep: the market governs, and it has no rule");
            Check(!closedAt.ContainsKey(4), "door 4, in a territory without the rule: never");
            Check(!closedAt.ContainsKey(5), "door 5, in open country: never");
            Check(!closedAt.ContainsKey(6), "door 6, excluded: never");
            Check(!closedAt.ContainsKey(8), "door 8, in an interior: never");
            // 7: shut by a player at 4, reopened at 8 -> first seen open again at 8, due at 18.
            Check(closedAt.TryGetValue(7, out float c7) && c7 == 18f, $"door 7: a player shutting it wipes its clock; reopened at 8 it gets a whole new delay (got {(closedAt.TryGetValue(7, out float x7) ? x7 : -1f)})");
            // 9: held until 30 -> not looked at, first seen open at 30, due at 40.
            Check(closedAt.TryGetValue(9, out float c9) && c9 == 40f, $"door 9: a hold in flight is left alone and its clock starts when it ends (got {(closedAt.TryGetValue(9, out float x9) ? x9 : -1f)})");
            Check(closedAt.Count == 4, "exactly the four doors that should close did");

            TerritoryIndex.Clear();
        }
    }
}
