using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace TerroritySmellatory.Tests
{
    internal static partial class Program
    {
        private static int _passed;
        private static int _failed;

        static partial void GroundGroups();   // tests/GroundTests.cs - multi-part ground, edits, wire v2
        static partial void NetGroups();      // tests/NetTests.cs - the file format's parts, the server's operations
        static partial void MeshGroups();     // tests/MeshTests.cs - the combined map outline
        static partial void BannerGroups();   // tests/BannerTests.cs - the entry banner's decisions
        static partial void BarrierGroups();      // tests/BarrierTests.cs - 0.3.0: the runic barrier's decisions
        static partial void BarrierLookGroups();  // tests/BarrierLookTests.cs - 0.3.0: the barrier's procedural pattern
        static partial void DoorGroups();         // tests/DoorTests.cs - 0.3.1: the auto-close doors rule's decisions
        static partial void NestingGroups();      // tests/NestingTests.cs - 0.3.2: continents and the towns inside them
        static partial void SubGroups();          // tests/SubTerritoryTests.cs - 0.4.0: sub-territories that start as copies and follow
        static partial void PanelGroups();        // tests/PanelTests.cs - 0.4.0: the admin panel's arithmetic (UI/PanelMath.cs)
        static partial void PartsGroups();        // tests/PartsTests.cs - 0.4.0: a territory list too big for one message goes in pieces
        static partial void FogGroups();          // tests/MapFogTests.cs - 0.4.1: territories only over uncovered map; the minimap name on one line

        private static int Main(string[] args)
        {
            // 0.3.2: "-- --check <file>" checks a territory world file instead of running the suite (tests/WorldFileCheck.cs).
            // 0.4.0: "-- --link <file> --out <new file>" makes a 0.3.x file's nested towns sub-territories, changing nothing they do.
            if (args.Length > 0 && (args[0] == "--check" || args[0] == "--link")) return WorldFileCheck.Cli(args);

            Run("shape: circle containment and distances", ShapeCircle);
            Run("shape: rotated rectangle matches Infinity Hammer's ruler frame", ShapeRotatedRectangle);
            Run("shape: perimeter points lie on the border, spaced by arc length", ShapePerimeter);
            Run("shape: overlap / contains-circle for sized checks", ShapeCircleChecks);
            Run("index: the smaller nested territory governs", IndexNesting);
            Run("index: priority overrides size", IndexPriority);
            Run("index: Denying - members, bypass, nested public area, sized strokes", IndexDenying);
            Run("wire: territory record round-trips every field", WireRoundTrip);
            Run("wire: a NEWER record version is read by this reader without desync", WireForwardCompatible);
            Run("wire: snapshot round-trips with roster and flags", WireSnapshot);
            Run("wire: request and result round-trip", WireRequestResult);
            Run("sanitize: clamps, cleans names, drops bad members, rejects NaN", SanitizeRules);
            Run("file: write -> read round-trips every field, 64-bit ids and awkward text", FileRoundTrip);
            Run("file: hand-written files - names or numbers, defaults, unknown keys", FileHandWritten);
            Run("file: a broken hand edit is refused, naming where", FileErrors);
            Run("hammer: IH's own substitution of our command, every ruler shape", HammerSubstitution);
            Run("hammer: remove/edit tool commands carry the aim point", HammerAimTools);
            Run("hammer: default IH toolset defines all 17 default tools and parses cleanly", HammerDefaultTools);
            Run("map: outline mesh - valid indices, constant rim, clamped and degenerate sizes", MapOutlineMesh);

            // 0.2.0 groups, each in its own file (a partial method with no body is simply skipped).
            GroundGroups();
            NetGroups();
            MeshGroups();
            BannerGroups();

            // 0.3.0 groups.
            BarrierGroups();
            BarrierLookGroups();

            // 0.3.1 groups.
            DoorGroups();

            // 0.3.2 groups.
            NestingGroups();

            // 0.4.0 groups.
            SubGroups();
            PanelGroups();
            PartsGroups();

            // 0.4.1 groups.
            FogGroups();

            Console.WriteLine();
            Console.WriteLine($"{_passed} passed, {_failed} failed");
            return _failed == 0 ? 0 : 1;
        }

        // ─────────────────────────────────────────────────────────────
        // SHAPE
        // ─────────────────────────────────────────────────────────────

        private static void ShapeCircle()
        {
            var s = new TerritoryShape(TerritoryShapeKind.Circle, 100f, -50f, 20f, 999f, 45f);
            Check(s.HalfZ == 20f, "a circle's HalfZ collapses to its radius");
            Check(s.Yaw == 0f, "a circle's yaw is held at 0");
            Check(s.Contains(new Vector3(100f, 0f, -50f)), "centre is inside");
            Check(s.Contains(new Vector3(119.9f, 500f, -50f)), "height is ignored");
            Check(!s.Contains(new Vector3(120.1f, 0f, -50f)), "just outside the radius");
            Near(s.DistanceOutside(new Vector3(130f, 0f, -50f)), 10f, "distance outside");
            Near(s.DistanceOutside(new Vector3(105f, 0f, -50f)), 0f, "distance inside is 0");
            Near(s.Area, Mathf.PI * 400f, "area");
        }

        private static void ShapeRotatedRectangle()
        {
            // IH's ruler: Projector.rotation = Euler(0, yaw, 0); the box spans local X in [-Width, Width] and
            // local Z in [-Depth, Depth]. A point placed in that frame must be inside iff it is inside the box.
            const float yaw = 30f, hx = 10f, hz = 30f;
            var center = new Vector3(5f, 0f, 7f);
            var s = new TerritoryShape(TerritoryShapeKind.Rectangle, center.x, center.z, hx, hz, yaw);
            Quaternion rot = Quaternion.Euler(0f, yaw, 0f);

            Check(s.Contains(center + rot * new Vector3(hx - 0.1f, 0f, 0f)), "just inside the width edge (ruler frame)");
            Check(!s.Contains(center + rot * new Vector3(hx + 0.1f, 0f, 0f)), "just outside the width edge (ruler frame)");
            Check(s.Contains(center + rot * new Vector3(0f, 0f, hz - 0.1f)), "just inside the depth edge");
            Check(!s.Contains(center + rot * new Vector3(0f, 0f, hz + 0.1f)), "just outside the depth edge");
            Check(s.Contains(center + rot * new Vector3(-hx + 0.2f, 0f, -hz + 0.2f)), "inside near a corner");
            Check(!s.Contains(center + rot * new Vector3(hx + 0.2f, 0f, hz + 0.2f)), "outside past a corner");

            // Unrotated axes would disagree with the ruler: this point is inside the UNrotated box and
            // outside the rotated one, so passing proves the rotation is applied, and in the right direction.
            var unrotatedOnly = center + new Vector3(0f, 0f, hz - 0.5f);
            Check(!s.Contains(unrotatedOnly), "rotation is really applied (point inside only the unrotated box)");
            Near(Quaternion.Euler(0f, 90f, 0f) * new Vector3(0f, 0f, 1f), new Vector3(1f, 0f, 0f), "Unity yaw turns +Z toward +X");
        }

        private static void ShapePerimeter()
        {
            var s = new TerritoryShape(TerritoryShapeKind.Rectangle, 0f, 0f, 5f, 40f, 17f);
            int n = 180;
            Vector3 prev = s.PerimeterPoint(0f);
            float minStep = float.MaxValue, maxStep = 0f;
            for (int i = 1; i <= n; i++)
            {
                Vector3 p = s.PerimeterPoint(i / (float)n);
                Near(s.DistanceOutside(p), 0f, $"perimeter point {i} is not outside", 1e-3f);
                Check(s.ContainsCircle(p, 0.01f) == false, $"perimeter point {i} is ON the border, not deep inside");
                float step = (p - prev).magnitude;
                minStep = Math.Min(minStep, step);
                maxStep = Math.Max(maxStep, step);
                prev = p;
            }
            // Arc-length spacing: straight runs space evenly; only steps across a corner come out shorter
            // (the chord of a turn), never longer.
            float expected = s.Perimeter / n;
            Near(maxStep, expected, "no step is longer than perimeter/n", 1e-3f);

            var c = new TerritoryShape(TerritoryShapeKind.Circle, 3f, 4f, 12f, 12f, 0f);
            for (int i = 0; i < 36; i++)
            {
                Vector3 p = c.PerimeterPoint(i / 36f);
                Near((p - new Vector3(3f, 0f, 4f)).magnitude, 12f, $"circle point {i} at the radius", 1e-3f);
            }
        }

        private static void ShapeCircleChecks()
        {
            var s = new TerritoryShape(TerritoryShapeKind.Square, 0f, 0f, 10f, 10f, 0f);
            Check(s.Overlaps(new Vector3(12f, 0f, 0f), 3f), "a 3 m stroke 2 m outside touches the square");
            Check(!s.Overlaps(new Vector3(14f, 0f, 0f), 3f), "a 3 m stroke 4 m outside does not");
            Check(s.ContainsCircle(new Vector3(0f, 0f, 0f), 9.9f), "a 9.9 m circle at the centre fits in a 20 m square");
            Check(!s.ContainsCircle(new Vector3(5f, 0f, 0f), 6f), "a 6 m circle 5 m off-centre spills over");
            Check(s.Overlaps(new Vector3(5f, 0f, 0f), 0f) == s.Contains(new Vector3(5f, 0f, 0f)), "radius 0 overlap is containment");
        }

        // ─────────────────────────────────────────────────────────────
        // INDEX
        // ─────────────────────────────────────────────────────────────

        private static Territory Circle(int id, float x, float z, float r, TerritoryFlags flags, int priority = 0) =>
            new Territory { Id = id, Name = "T" + id, Kind = TerritoryShapeKind.Circle, X = x, Z = z, HalfX = r, HalfZ = r, Flags = flags, Priority = priority };

        private static void IndexNesting()
        {
            TerritoryIndex.Set(new[]
            {
                Circle(1, 0f, 0f, 100f, TerritoryFlags.BuildProtect),
                Circle(2, 20f, 0f, 10f, TerritoryFlags.None)
            });
            Check(TerritoryIndex.GoverningAt(new Vector3(20f, 0f, 0f))?.Id == 2, "inside both: the small one governs");
            Check(TerritoryIndex.GoverningAt(new Vector3(-50f, 0f, 0f))?.Id == 1, "inside only the big one");
            Check(TerritoryIndex.GoverningAt(new Vector3(500f, 0f, 0f)) == null, "open country");
        }

        private static void IndexPriority()
        {
            TerritoryIndex.Set(new[]
            {
                Circle(1, 0f, 0f, 100f, TerritoryFlags.BuildProtect, priority: 5),
                Circle(2, 20f, 0f, 10f, TerritoryFlags.None)
            });
            Check(TerritoryIndex.GoverningAt(new Vector3(20f, 0f, 0f))?.Id == 1, "higher priority beats smaller");
        }

        private static void IndexDenying()
        {
            Territory kingdom = Circle(1, 0f, 0f, 100f, TerritoryFlags.BuildProtect | TerritoryFlags.AccessProtect);
            kingdom.OwnerId = 111;
            kingdom.Members.Add(new TerritoryMember(222, "Member"));
            Territory market = Circle(2, 50f, 0f, 10f, TerritoryFlags.None);   // public, inside the kingdom
            TerritoryIndex.Set(new[] { kingdom, market });

            var inKingdom = new Vector3(-30f, 0f, 0f);
            Check(TerritoryIndex.Denying(inKingdom, 0f, TerritoryFlags.BuildProtect, 999, false)?.Id == 1, "a visitor may not build in the kingdom");
            Check(TerritoryIndex.Denying(inKingdom, 0f, TerritoryFlags.BuildProtect, 111, false) == null, "the owner may");
            Check(TerritoryIndex.Denying(inKingdom, 0f, TerritoryFlags.BuildProtect, 222, false) == null, "a member may");
            Check(TerritoryIndex.Denying(inKingdom, 0f, TerritoryFlags.BuildProtect, 999, true) == null, "an admin with bypass may");
            Check(TerritoryIndex.Denying(inKingdom, 0f, TerritoryFlags.HarvestProtect, 999, false) == null, "an unset protection denies nobody");

            var inMarket = new Vector3(50f, 0f, 0f);
            Check(TerritoryIndex.Denying(inMarket, 0f, TerritoryFlags.BuildProtect, 999, false) == null, "the public market inside the kingdom is open");
            Check(TerritoryIndex.Denying(inMarket, 3f, TerritoryFlags.BuildProtect, 999, false) == null, "a 3 m hoe stroke wholly inside the market is open");
            Check(TerritoryIndex.Denying(new Vector3(58f, 0f, 0f), 3f, TerritoryFlags.BuildProtect, 999, false)?.Id == 1,
                "a stroke straddling the market's edge touches the kingdom and is refused");
            Check(TerritoryIndex.Denying(new Vector3(101f, 0f, 0f), 3f, TerritoryFlags.BuildProtect, 999, false)?.Id == 1,
                "digging just outside the border reaches in and is refused, as a ward does");
            Check(TerritoryIndex.Denying(new Vector3(110f, 0f, 0f), 3f, TerritoryFlags.BuildProtect, 999, false) == null,
                "digging well outside is fine");
            Check(TerritoryIndex.Denying(new Vector3(150f, 0f, 0f), 32f, TerritoryFlags.BuildProtect, 999, false) == null,
                "a ward placed 150 m out (32 m radius) does not reach");
            Check(TerritoryIndex.Denying(new Vector3(120f, 0f, 0f), 32f, TerritoryFlags.BuildProtect, 999, false)?.Id == 1,
                "a ward whose 32 m radius reaches in is refused, as vanilla refuses overlapping wards");
        }

        // ─────────────────────────────────────────────────────────────
        // WIRE
        // ─────────────────────────────────────────────────────────────

        private static Territory Sample()
        {
            var t = new Territory
            {
                Id = 42,
                Name = "Hrafnborg",
                Kind = TerritoryShapeKind.Rectangle,
                X = 1234.5f,
                Y = 31.25f,
                Z = -987.25f,
                HalfX = 40f,
                HalfZ = 12.5f,
                Yaw = 33.3f,
                Color = new Color32(60, 165, 160, 255),
                OwnerId = 7654321987654321L,
                OwnerName = "Wubarrk",
                Flags = TerritoryFlags.BuildProtect | TerritoryFlags.Sanctuary | TerritoryFlags.ShowBorder,
                PvP = PvPRule.ForcedOff,
                Priority = -3,
                HealthRegen = 1.5f,
                StaminaRegen = 2f,
                EitrRegen = 0.75f,
                CarryBonus = 150f,
                ArmorBonus = 10f,
                EnterMessage = "Welcome to <color=#FFD700>{name}</color>",
                LeaveMessage = "Safe travels",
                CreatedBy = "Admin (Steam_1)",
                CreatedTicks = 638630000000000000L,
                Revision = 9
            };
            t.Members.Add(new TerritoryMember(1111111111111L, "Astrid"));
            t.Members.Add(new TerritoryMember(2222222222222L, "Bjorn"));
            return t;
        }

        private static void SameTerritory(Territory a, Territory b, string what)
        {
            Check(a.Id == b.Id && a.Name == b.Name && a.Kind == b.Kind, what + ": id/name/kind");
            Check(a.X == b.X && a.Y == b.Y && a.Z == b.Z && a.HalfX == b.HalfX && a.HalfZ == b.HalfZ && a.Yaw == b.Yaw, what + ": geometry");
            Check(a.Color.r == b.Color.r && a.Color.g == b.Color.g && a.Color.b == b.Color.b, what + ": colour");
            Check(a.OwnerId == b.OwnerId && a.OwnerName == b.OwnerName, what + ": owner");
            Check(a.Members.Count == b.Members.Count && a.Members.Zip(b.Members, (m, n) => m.Id == n.Id && m.Name == n.Name).All(x => x), what + ": members");
            Check(a.Flags == b.Flags && a.PvP == b.PvP && a.Priority == b.Priority, what + ": rules");
            Check(a.ParentId == b.ParentId && a.FollowFlags == b.FollowFlags && a.Follow == b.Follow, what + ": parent and follow marks");
            Check(a.HealthRegen == b.HealthRegen && a.StaminaRegen == b.StaminaRegen && a.EitrRegen == b.EitrRegen &&
                  a.CarryBonus == b.CarryBonus && a.ArmorBonus == b.ArmorBonus, what + ": sanctuary values");
            Check(a.EnterMessage == b.EnterMessage && a.LeaveMessage == b.LeaveMessage, what + ": messages");
            Check(a.CreatedBy == b.CreatedBy && a.CreatedTicks == b.CreatedTicks && a.Revision == b.Revision, what + ": history");
        }

        private static void WireRoundTrip()
        {
            Territory t = Sample();
            var pkg = new ZPackage();
            t.Write(pkg);
            Territory back = Territory.Read(pkg.Transmit());
            SameTerritory(t, back, "record");
        }

        private static void WireForwardCompatible()
        {
            // Hand-build what a FUTURE build would send: the record version after this build's, every field
            // this build writes, then fields it has never heard of - followed by a second record and the roster.
            // This build must read both records whole and land exactly on the roster: the length prefix is what
            // guarantees it.
            Territory a = Sample();
            Territory b = Sample();
            b.Id = 43;
            b.Name = "Second";

            var snap = new ZPackage();
            snap.Write(Protocol.Version + 1);   // a future protocol
            snap.Write(555L);              // world uid
            snap.Write(17);                // revision
            snap.Write(1);                 // flags: you are admin
            snap.Write(2);                 // two territories
            snap.Write(FutureRecord(a));
            snap.Write(FutureRecord(b));
            snap.Write(1);                 // roster of one
            snap.Write(99L);
            snap.Write("Sigrid");
            snap.Write("future-protocol trailing data this build ignores");

            Protocol.Snapshot s = Protocol.ReadSnapshot(snap.Transmit());
            Check(s != null && s.Territories.Count == 2, "both future records read");
            SameTerritory(a, s.Territories[0], "future record 1");
            SameTerritory(b, s.Territories[1], "future record 2");
            Check(s.OnlinePlayers.Count == 1 && s.OnlinePlayers[0].Id == 99L && s.OnlinePlayers[0].Name == "Sigrid", "the roster after them is intact");
            Check(s.YouAreAdmin && !s.AdminsBypass && s.Revision == 17, "header fields");
        }

        private static ZPackage FutureRecord(Territory t)
        {
            // The real body, with its version bumped past this build's and fields this build has never heard of appended.
            var outer = new ZPackage();
            t.Write(outer);
            byte[] body = outer.Transmit().ReadPackage().GetArray();
            BitConverter.GetBytes(Territory.RecordVersion + 1).CopyTo(body, 0);   // RecordVersion is the first int
            var ms = new System.IO.MemoryStream();
            ms.Write(body, 0, body.Length);
            var w = new System.IO.BinaryWriter(ms);
            w.Write(12345);                                           // an unknown int
            w.Write("an unknown string");                             // an unknown string
            w.Flush();
            return new ZPackage(ms.ToArray());
        }

        private static void WireSnapshot()
        {
            var roster = new List<TerritoryMember> { new TerritoryMember(5, "Eir"), new TerritoryMember(6, "Freya") };
            ZPackage pkg = Protocol.WriteSnapshot(777L, 3, youAreAdmin: false, adminsBypass: true, new[] { Sample() }, roster);
            Protocol.Snapshot s = Protocol.ReadSnapshot(pkg.Transmit());
            Check(s.Protocol == Protocol.Version && s.WorldUid == 777L && s.Revision == 3, "header");
            Check(!s.YouAreAdmin && s.AdminsBypass, "flags");
            Check(s.Territories.Count == 1, "one territory");
            SameTerritory(Sample(), s.Territories[0], "snapshot record");
            Check(s.OnlinePlayers.Count == 2 && s.OnlinePlayers[1].Name == "Freya", "roster");

            ZPackage empty = Protocol.WriteSnapshot(1L, 1, false, false, Array.Empty<Territory>(), null);
            Protocol.Snapshot e = Protocol.ReadSnapshot(empty.Transmit());
            Check(e.Territories.Count == 0 && e.OnlinePlayers.Count == 0, "an empty world and no roster");
        }

        private static void WireRequestResult()
        {
            ZPackage req = Protocol.WriteRequest(11, RequestOp.Update, Sample(), 42);
            Protocol.Request r = Protocol.ReadRequest(req.Transmit());
            Check(r.RequestId == 11 && r.Op == RequestOp.Update && r.TargetId == 42 && r.Record != null, "request header");
            SameTerritory(Sample(), r.Record, "request record");

            ZPackage del = Protocol.WriteRequest(12, RequestOp.Delete, null, 42);
            Protocol.Request d = Protocol.ReadRequest(del.Transmit());
            Check(d.Op == RequestOp.Delete && d.TargetId == 42 && d.Record == null, "a delete carries no record");

            ZPackage res = Protocol.WriteResult(11, true, "Saved #42", 42);
            Check(Protocol.ReadResult(res.Transmit(), out int id, out bool ok, out string msg, out int tid) && id == 11 && ok && msg == "Saved #42" && tid == 42, "result");
        }

        // ─────────────────────────────────────────────────────────────
        // SANITIZE
        // ─────────────────────────────────────────────────────────────

        private static void SanitizeRules()
        {
            Territory t = Sample();
            t.Name = "  <b>Evil</b> $piece_name \u0007 very very very very very very very long name indeed ";
            t.HalfX = 99999f;
            t.HalfZ = -5f;
            t.Yaw = 725f;
            t.Members.Add(new TerritoryMember(0, "zero"));
            t.Members.Add(new TerritoryMember(1111111111111L, "dup"));
            t.Members.Add(new TerritoryMember(t.OwnerId, "owner again"));
            t.Flags |= (TerritoryFlags)(1 << 30);
            t.PvP = (PvPRule)99;
            t.Priority = 50000;
            t.HealthRegen = float.NaN;
            t.CarryBonus = 1e9f;

            Check(t.Sanitize(600f), "a clampable territory sanitises");
            Check(!t.Name.Contains("<") && !t.Name.Contains(">") && !t.Name.Contains("$") && t.Name.Length <= Territory.MaxNameLength, $"name cleaned: '{t.Name}'");
            Check(t.HalfX == 600f && t.HalfZ == 1f, "extents clamped to [1, max]");
            Near(t.Yaw, 5f, "yaw wrapped into [0, 360)");
            Check(t.Members.Count == 2 && t.Members.All(m => m.Id != 0 && m.Id != t.OwnerId), "zero, duplicate and owner entries dropped");
            Check((t.Flags & ~Territory.KnownFlags) == 0, "unknown flag bits dropped");
            Check(t.PvP == PvPRule.Unchanged && t.Priority == 1000, "enum and priority clamped");
            Check(t.HealthRegen == 1f && t.CarryBonus == 2000f, "NaN falls back, huge clamps");

            Territory bad = Sample();
            bad.X = float.NaN;
            Check(!bad.Sanitize(600f), "NaN geometry is refused outright");
        }

        // ─────────────────────────────────────────────────────────────
        // FILE
        // ─────────────────────────────────────────────────────────────

        private static void FileRoundTrip()
        {
            Territory a = Sample();
            a.Name = "Kvel\u00f0 \"Hall\" \\ back\\slash \u2603 tab\there";
            a.Members.Add(new TerritoryMember(long.MaxValue, "Max"));
            a.Members.Add(new TerritoryMember(long.MinValue + 1, "Min"));
            a.OwnerId = -9007199254740993L;   // not representable as a double: proves ids never pass through one
            Territory b = Circle(3, -10.125f, 20.5f, 7.75f, TerritoryFlags.None);

            var file = new TerritoryJson.FileData { WorldName = "Midgard \"test\"", WorldUid = -4611686018427387904L, NextId = 9 };
            file.Territories.Add(a);
            file.Territories.Add(b);

            string json = TerritoryJson.Write(file);
            TerritoryJson.FileData back = TerritoryJson.Read(json);
            Check(back.WorldName == file.WorldName && back.WorldUid == file.WorldUid && back.NextId == 9, "header");
            Check(back.Territories.Count == 2, "both territories");
            SameTerritory(a, back.Territories[0], "file record 1");
            SameTerritory(b, back.Territories[1], "file record 2");
            Check(json.Contains("\"shape\": \"Rectangle\"") && json.Contains("\"BuildProtect\"") && json.Contains("\"pvp\": \"ForcedOff\""),
                "enumerations are written as readable names");

            TerritoryJson.FileData empty = TerritoryJson.Read(TerritoryJson.Write(new TerritoryJson.FileData { WorldName = "e" }));
            Check(empty.Territories.Count == 0 && empty.WorldName == "e", "an empty world round-trips");
        }

        private static void FileHandWritten()
        {
            string json = "\uFEFF" + @"{
  ""worldName"": ""hand"", ""nextId"": 5, ""someFutureKey"": { ""nested"": [1, 2, {""x"": null}] },
  ""territories"": [
    { ""id"": 2, ""name"": ""Numbers"", ""shape"": 2, ""x"": 1, ""z"": -2.5e1, ""halfX"": 40, ""halfZ"": 15, ""yaw"": 30,
      ""flags"": [""BuildProtect"", ""AccessProtect"", ""NoMonsterSpawns"", ""NoRaids"", ""ShowOnMap"", ""1024""], ""pvp"": 1,
      ""ownerId"": 7654321987654321, ""members"": [ { ""id"": 1111111111111111111, ""name"": ""Astrid"" }, ], },
    { ""id"": 4, ""name"": ""Names"", ""shape"": ""square"", ""x"": 0, ""z"": 0, ""halfX"": 12,
      ""flags"": [""buildprotect"", ""NoRaids"", ""SomethingFromTheFuture""], ""pvp"": ""forcedon"", ""color"": ""#FF8800"" },
    { ""id"": 6 }
  ]
}";
            TerritoryJson.FileData f = TerritoryJson.Read(json);
            Check(f.Territories.Count == 3 && f.NextId == 5 && f.WorldName == "hand", "three records, header read, BOM, unknown keys and trailing commas tolerated");

            Territory n = f.Territories[0];
            Check(n.Kind == TerritoryShapeKind.Rectangle && n.Z == -25f && n.PvP == PvPRule.ForcedOff, "numbers for shape, pvp and an exponent");
            Check(n.Flags == (TerritoryFlags)1587, "flag names, plus a flag given as its number (\"1024\" = ShowBorder)");
            Check(n.OwnerId == 7654321987654321L && n.Members[0].Id == 1111111111111111111L, "64-bit ids exact");

            Territory m = f.Territories[1];
            Check(m.Kind == TerritoryShapeKind.Square && m.PvP == PvPRule.ForcedOn, "names, any case");
            Check(m.Flags == (TerritoryFlags.BuildProtect | TerritoryFlags.NoRaids), "flag names; an unknown future name is skipped");
            Check(m.Color.r == 255 && m.Color.g == 136 && m.Color.b == 0, "colour");

            Territory d = f.Territories[2];
            Check(d.Flags == Territory.DefaultFlags && d.HalfX == 20f && d.HealthRegen == 1f && d.Kind == TerritoryShapeKind.Circle,
                "a record with only an id takes every default");
        }

        private static void FileErrors()
        {
            // Newtonsoft names the JSON path and the line and position; the store puts that message in the
            // log and refuses to save, so the admin is told exactly where the hand edit went wrong.
            ExpectError("{\n  \"worldName\": \"a\"\n  \"nextId\": 2\n}", "line 3", "missing comma between keys");
            ExpectError("{ \"territories\": [ { \"id\": \"one\" } ] }", "territories[0].id", "text where a number belongs");
            ExpectError("{ \"territories\": [ { \"id\": 1, \"shape\": \"hexagon\" } ] }", "territories[0].shape", "an unknown shape");
            ExpectError("{ \"territories\": [ { \"id\": 1, \"color\": \"gold\" } ] }", "'color'", "a colour that is not hex");
            ExpectError("{ \"territories\": { } }", "territories", "territories not a list");
            ExpectError("{ \"worldName\": \"a\\q\" }", "escape", "a bad escape");
            ExpectError("{ \"worldName\": \"unterminated }", "Unterminated", "an unterminated string");
            ExpectError("", "empty", "an empty file");
            ExpectError("{ \"territories\": [ { \"id\": 1, \"flags\": 1587 } ] }", "territories[0].flags", "flags as a bare number instead of a list");
        }

        private static void ExpectError(string json, string mustContain, string what)
        {
            try
            {
                TerritoryJson.Read(json);
                Check(false, $"{what}: was accepted");
            }
            catch (Exception ex)
            {
                Check(ex.Message.Contains(mustContain), $"{what}: message '{ex.Message}' should mention '{mustContain}'");
            }
        }

        // ─────────────────────────────────────────────────────────────
        // INFINITY HAMMER
        // ─────────────────────────────────────────────────────────────

        private enum RulerShape { Circle, Ring, Frame, Square, Rectangle }

        /// <summary>
        /// InfinityTools.ToolSelection.HandleCommand + RemoveUnusedShapeParameters, ported from the 1.84
        /// decompile (libs-Tools/1.0/thirdparty/terrain-mods-2026-09-22). Only the placeholders our commands use.
        /// </summary>
        private static string IhSubstitute(string command, RulerShape shape, float X, float Y, float Z, Vector3 pos, float yaw)
        {
            string F(float v) => v.ToString(CultureInfo.InvariantCulture);
            string text = F(X);
            string text2 = F(Math.Min(X, Z));
            string text3 = F(Math.Max(X, Z));
            string d = F(Z);
            string w = F(X);
            if (shape == RulerShape.Circle) { text2 = text; text3 = text; }
            if (shape != RulerShape.Rectangle) d = w;
            if (shape == RulerShape.Square) { text2 = text; text3 = text; }
            if (shape == RulerShape.Rectangle) { text2 = w; text3 = w; }

            string c = command;
            if (c.Contains("<r>") && (c.Contains("<w>") || c.Contains("<d>")))
            {
                bool round = shape == RulerShape.Circle || shape == RulerShape.Ring;
                List<string> parts = c.Split(' ').ToList();
                for (int i = parts.Count - 1; i > -1; i--)
                {
                    if (round && (parts[i].Contains("<w>") || parts[i].Contains("<d>"))) parts.RemoveAt(i);
                    if (!round && parts[i].Contains("<r>")) parts.RemoveAt(i);
                }
                c = string.Join(" ", parts);
            }

            c = shape != RulerShape.Frame ? c.Replace("<d>", d) : c.Replace("<d>", text2 + "-" + text3);
            return c.Replace("<r>", text).Replace("<r2>", text3).Replace("<w>", w).Replace("<w2>", text3)
                .Replace("<a>", F(yaw)).Replace("<x>", F(pos.x)).Replace("<y>", F(pos.y)).Replace("<z>", F(pos.z))
                .Replace("<h>", F(Y));
        }

        private static Territory Placed(string line)
        {
            // Terminal.TryRunCommand splits on single spaces; args[0] is the command itself.
            Check(PlacementArgs.TryParse(line.Split(' '), -1f, 20f, out Territory g), $"parses: {line}");
            return g;
        }

        private static void HammerSubstitution()
        {
            string template = "terr_place x=<x> z=<z> y=<y> r=<r> w=<w> d=<d> a=<a>";
            var pos = new Vector3(-1520.25f, 44.5f, 3310.75f);

            Territory circle = Placed(IhSubstitute(template, RulerShape.Circle, 25f, 0f, 25f, pos, 70f));
            Check(circle.Kind == TerritoryShapeKind.Circle && circle.HalfX == 25f, "circle: r is the radius");
            Check(circle.X == pos.x && circle.Y == pos.y && circle.Z == pos.z, "circle: position");

            Territory square = Placed(IhSubstitute(template, RulerShape.Square, 15f, 0f, 40f, pos, 45f));
            Check(square.Kind == TerritoryShapeKind.Square && square.HalfX == 15f && square.HalfZ == 15f, "square: w = d = X (IH ignores Z for a square)");
            Near(square.Yaw, 45f, "square: yaw");

            Territory rect = Placed(IhSubstitute(template, RulerShape.Rectangle, 10f, 0f, 30f, pos, 120f));
            Check(rect.Kind == TerritoryShapeKind.Rectangle && rect.HalfX == 10f && rect.HalfZ == 30f, "rectangle: w = X, d = Z");
            Near(rect.Yaw, 120f, "rectangle: yaw");

            Territory ring = Placed(IhSubstitute(template, RulerShape.Ring, 18f, 0f, 30f, pos, 0f));
            Check(ring.Kind == TerritoryShapeKind.Circle && ring.HalfX == 18f, "ring: read as its outer circle");

            Territory frame = Placed(IhSubstitute(template, RulerShape.Frame, 12f, 0f, 30f, pos, 10f));
            Check(frame.Kind == TerritoryShapeKind.Square && frame.HalfX == 12f, "frame: 'd=min-max' fails to parse and reads as a square");

            string sep = IhSubstitute(template, RulerShape.Rectangle, 10.5f, 0f, 30.25f, pos, 0f);
            Check(!sep.Contains(",5") && !sep.Contains("<"), $"no placeholder left and invariant decimals: {sep}");

            Territory none = Placed("terr_place x=1 z=2");
            Check(none.Kind == TerritoryShapeKind.Circle && none.HalfX == 20f && none.Y == -1f, "no size: default radius and fallback height");
            Check(!PlacementArgs.TryParse("terr_place z=2 r=5".Split(' '), 0f, 20f, out _), "no x: refused");
        }

        private static void HammerAimTools()
        {
            var pos = new Vector3(10f, 0f, -20f);
            string remove = IhSubstitute("terr_remove_at x=<x> z=<z>", RulerShape.Circle, 20f, 0f, 20f, pos, 0f);
            string edit = IhSubstitute("terr_edit_at x=<x> z=<z>", RulerShape.Circle, 20f, 0f, 20f, pos, 0f);
            Dictionary<string, string> kv = PlacementArgs.KeyValues(remove.Split(' '));
            Check(PlacementArgs.TryFloat(kv, "x", out float x) && PlacementArgs.TryFloat(kv, "z", out float z) && x == 10f && z == -20f, $"remove tool: {remove}");
            kv = PlacementArgs.KeyValues(edit.Split(' '));
            Check(PlacementArgs.TryFloat(kv, "x", out x) && PlacementArgs.TryFloat(kv, "z", out z) && x == 10f && z == -20f, $"edit tool: {edit}");
        }

        private static void HammerDefaultTools()
        {
            string yaml = PlacementArgs.DefaultInfinityHammerToolsYaml;
            // 3 Hammer pipettes
            Check(PlacementArgs.DefinesTool(yaml, "Pipette"), "default tool: Pipette");
            Check(PlacementArgs.DefinesTool(yaml, "Building pipette"), "default tool: Building pipette");
            Check(PlacementArgs.DefinesTool(yaml, "Area pipette"), "default tool: Area pipette");

            // 14 Hoe tools
            Check(PlacementArgs.DefinesTool(yaml, "Level"), "default tool: Level");
            Check(PlacementArgs.DefinesTool(yaml, "Raise"), "default tool: Raise");
            Check(PlacementArgs.DefinesTool(yaml, "Pave"), "default tool: Pave");
            Check(PlacementArgs.DefinesTool(yaml, "Grass"), "default tool: Grass");
            Check(PlacementArgs.DefinesTool(yaml, "Dirt"), "default tool: Dirt");
            Check(PlacementArgs.DefinesTool(yaml, "Cultivate"), "default tool: Cultivate");
            Check(PlacementArgs.DefinesTool(yaml, "DarkGrass"), "default tool: DarkGrass");
            Check(PlacementArgs.DefinesTool(yaml, "PatchyGrass"), "default tool: PatchyGrass");
            Check(PlacementArgs.DefinesTool(yaml, "MossyPaving"), "default tool: MossyPaving");
            Check(PlacementArgs.DefinesTool(yaml, "DarkPaving"), "default tool: DarkPaving");
            Check(PlacementArgs.DefinesTool(yaml, "Reset"), "default tool: Reset");
            Check(PlacementArgs.DefinesTool(yaml, "Slope"), "default tool: Slope");
            Check(PlacementArgs.DefinesTool(yaml, "Remove"), "default tool: Remove");
            Check(PlacementArgs.DefinesTool(yaml, "Tame"), "default tool: Tame");

            // Case insensitivity, whitespace, quotes, comments
            Check(PlacementArgs.DefinesTool(yaml, "pipette"), "case insensitive tool matching");
            Check(PlacementArgs.DefinesTool(yaml, "BUILDING PIPETTE"), "case insensitive upper matching");
            Check(PlacementArgs.DefinesTool("name: \"Quoted Tool\"", "Quoted Tool"), "quoted tool name");
            Check(PlacementArgs.DefinesTool("name: 'Single Quoted'", "Single Quoted"), "single quoted tool name");
            Check(PlacementArgs.DefinesTool("name: ToolWithComment # comment", "ToolWithComment"), "tool with trailing comment");

            // Negative checks
            Check(!PlacementArgs.DefinesTool(yaml, "NonExistentTool"), "non-existent tool returns false");
            Check(!PlacementArgs.DefinesTool("", "Pipette"), "empty yaml returns false");
            Check(!PlacementArgs.DefinesTool(yaml, ""), "empty tool name returns false");
        }

        // ─────────────────────────────────────────────────────────────

        // ─────────────────────────────────────────────────────────────
        // MAP OUTLINE - the mesh TerritoryOutline hands to Unity. A bad index there throws inside the canvas
        // rebuild every frame, outside anything this mod can catch, so it is checked here instead.
        // ─────────────────────────────────────────────────────────────

        private static void MapOutlineMesh()
        {
            var xs = new List<float>();
            var ys = new List<float>();
            var tones = new List<byte>();
            var tris = new List<int>();

            void Valid(string what)
            {
                Check(xs.Count == ys.Count && ys.Count == tones.Count, $"{what}: one position and one tone per vertex");
                Check(tris.Count % 3 == 0, $"{what}: whole triangles");
                Check(tris.All(i => i >= 0 && i < xs.Count), $"{what}: every index names a vertex");
                bool distinct = true;
                for (int k = 0; k + 2 < tris.Count; k += 3)
                    distinct &= tris[k] != tris[k + 1] && tris[k + 1] != tris[k + 2] && tris[k] != tris[k + 2];
                Check(distinct, $"{what}: no triangle repeats a vertex (the loops close on themselves)");
                Check(xs.Concat(ys).All(v => !float.IsNaN(v) && !float.IsInfinity(v)), $"{what}: every position is finite");
            }

            float Radius(int loop, int n, int i = 0) => (float)Math.Sqrt(xs[loop * n + i] * xs[loop * n + i] + ys[loop * n + i] * ys[loop * n + i]);

            // A 100-unit circle: 50 segments, four loops and a centre.
            OutlineGeometry.Build(true, 100f, 100f, 2.5f, 1f, xs, ys, tones, tris);
            Valid("circle r100");
            int n = OutlineGeometry.Segments(true, 100f, 100f);
            Check(n == 50, $"one segment per two units of radius (got {n})");
            Check(xs.Count == OutlineGeometry.Loops * n + 1, $"four loops and a centre (got {xs.Count} vertices)");
            Check(tris.Count == 3 * (3 * 2 * n + n), $"three bands of quads and a fan (got {tris.Count / 3} triangles)");
            Near(Radius(0, n), 100.5f, "the outer feather sits half a feather outside the edge");
            Near(Radius(1, n), 99.5f, "the rim's outer edge");
            Near(Radius(2, n), 98f, "the rim's inner edge");
            Near(Radius(3, n), 97f, "the fill's edge");
            Near(Radius(1, n, n / 3), 99.5f, "every point of a loop is on its circle");
            Check(tones.Take(n).All(t => t == OutlineGeometry.Clear), "the outer feather fades to clear");
            Check(tones.Skip(n).Take(2 * n).All(t => t == OutlineGeometry.Solid), "the rim is solid");
            Check(tones.Skip(3 * n).All(t => t == OutlineGeometry.Fill), "the fill edge and the centre are the faint fill");
            Near(xs[xs.Count - 1], 0f, "the fan's centre is the shape's centre");

            // The point of the mesh: the rim is the same width however big the territory is drawn.
            OutlineGeometry.Build(true, 1000f, 1000f, 2.5f, 1f, xs, ys, tones, tris);
            Valid("circle r1000");
            n = OutlineGeometry.Segments(true, 1000f, 1000f);
            Near(Radius(1, n) - Radius(2, n), 1.5f, "a 1000-unit circle has the same rim as a 100-unit one", 1e-3f);

            Check(OutlineGeometry.Segments(true, 5f, 5f) == OutlineGeometry.MinSegments, "a tiny circle still gets 32 segments");
            Check(OutlineGeometry.Segments(true, 1e7f, 1e7f) == OutlineGeometry.MaxSegments, "a huge one is capped at 512");
            foreach (float r in new[] { 10f, 64f, 300f, 1024f, 20000f })
            {
                int segs = OutlineGeometry.Segments(true, r, r);
                double chord = r * (1.0 - Math.Cos(Math.PI / segs));
                Check(chord < 0.4, $"a {r}-unit circle's chords stay within 0.4 units of it (got {chord:0.###})");
            }

            // A box: four corners per loop, an exact inset rectangle.
            OutlineGeometry.Build(false, 40f, 15f, 2.5f, 1f, xs, ys, tones, tris);
            Valid("box 80x30");
            Check(xs.Count == 17, $"a box is four corners per loop and a centre (got {xs.Count})");
            Near(xs[4], -39.5f, "the rim's outer edge, bottom-left x");
            Near(ys[4], -14.5f, "the rim's outer edge, bottom-left y");
            Near(xs[10], 38f, "the rim's inner edge, top-right x");
            Near(ys[10], 13f, "the rim's inner edge, top-right y");

            // Drawn tiny, the rim gives way to the shape instead of turning it inside out.
            OutlineGeometry.Build(false, 3f, 1f, 2.5f, 1f, xs, ys, tones, tris);
            Valid("box 6x2");
            bool ordered = true;
            for (int loop = 1; loop < OutlineGeometry.Loops; loop++)
                ordered &= Math.Abs(ys[loop * 4]) <= Math.Abs(ys[(loop - 1) * 4]) + 1e-6f && Math.Abs(ys[loop * 4]) >= 0f;
            Check(ordered, "each loop is inside the one before it, even when the rim is wider than the shape");
            Near(Math.Abs(ys[8]), 0.75f, "the rim is clamped to half the narrow side (0.5), its inner edge at 1 - 0.25", 1e-4f);

            // Nothing to draw: no mesh, and no exception.
            foreach (float bad in new[] { 0f, -5f, float.NaN, float.PositiveInfinity })
            {
                OutlineGeometry.Build(true, bad, 10f, 2.5f, 1f, xs, ys, tones, tris);
                Check(xs.Count == 0 && tris.Count == 0, $"a half-width of {bad} draws nothing");
            }
            OutlineGeometry.Build(true, 50f, 50f, float.NaN, float.NaN, xs, ys, tones, tris);
            Valid("circle with a NaN rim");
        }

        private static void Run(string name, Action test)
        {
            int before = _failed;
            try
            {
                test();
            }
            catch (Exception ex)
            {
                _failed++;
                Console.WriteLine($"  EXCEPTION in {name}: {ex}");
            }
            Console.WriteLine($"{(_failed == before ? "PASS" : "FAIL")}  {name}");
        }

        private static void Check(bool ok, string what)
        {
            if (ok) { _passed++; return; }
            _failed++;
            Console.WriteLine($"    failed: {what}");
        }

        private static void Near(float a, float b, string what, float eps = 1e-4f) => Check(Math.Abs(a - b) <= eps, $"{what} (got {a}, expected {b})");

        private static void Near(Vector3 a, Vector3 b, string what) => Check((a - b).magnitude < 1e-4f, $"{what} (got {a}, expected {b})");
    }
}
