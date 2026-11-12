using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using TerroritySmellatory;
using UnityEngine;

namespace TerrE2E
{
    /// <summary>
    /// The 0.3.1 phase: doors that close by themselves (TerritoryFlags.AutoCloseDoors). Gated by reflection
    /// (<see cref="Features.Doors"/>: TerritoryFlags has AutoCloseDoors); on a DLL without it the admin logs ONE INFO line and
    /// nothing else happens. The server side (S32a, S32b, R32) lives in ServerRole, the server's own log line in run-e2e.sh (H14).
    ///
    /// TSAdmin marks three circles at the end of its phase 2 (through the draft and terr_place): DOORS (r=12, AutoCloseDoors +
    /// NoRaids), NESTED (r=4, inside DOORS, NoRaids only) and OFF (r=8, 38 m away, NoRaids only), so the flag is the only
    /// difference between DOORS and the two that must leave doors alone.
    ///
    /// TSPlain then lays real wood_doors (its own ZDOs, so this client owns them and the SERVER has to take them over to close
    /// them - Rules/OwnershipHold.cs) and opens them all in one frame through the door's own RPC (UseDoor, as a Use press does),
    /// with the server's AutoCloseDelay set to 3 s by run-e2e.sh:
    ///   P31a  a door inside DOORS with nobody within 3 m closes, ZDO state 0 and the animator's "state" 0, after about the delay;
    ///   P31b  the same door outside every territory stays open;   P31c  inside OFF (no flag): stays open;
    ///   P31d  a wood_window (and a drawbridge if the world has one) inside DOORS stays open (both are excluded by default);
    ///   P31e  a door inside the NESTED unflagged territory stays open (the smaller territory governs);
    ///   P31f  a door with the player within 2 m stays open past delay + 4 s, and closes within 4 s of the player walking away.
    /// A control that never opened proves nothing, so it FAILS (the door is known to open headless); a window or drawbridge that
    /// cannot be opened is SOFT.
    /// </summary>
    internal static class Scenario031
    {
        private static float T => E2EEnv.StepTimeout;

        internal static readonly string[] DoorsFlagNames = { "NoRaids", "AutoCloseDoors" };
        internal static readonly string[] OtherFlagNames = { "NoRaids" };

        /// <summary>The contract's number for AutoCloseDoors: 1 &lt;&lt; 13.</summary>
        internal const int AutoCloseDoorsBit = 1 << 13;

        private static readonly string[] Ids =
        {
            "P31a-door-closes", "P31b-door-outside-stays", "P31c-door-flag-off-stays", "P31d-door-excluded-stays",
            "P31e-door-nested-stays", "P31f-door-player-near"
        };

        private static JObject V(Vector3 v) => new JObject { ["x"] = v.x, ["y"] = v.y, ["z"] = v.z };

        // ─────────────────────────────────────────────────────────────
        // TSAdmin: mark the three circles
        // ─────────────────────────────────────────────────────────────

        internal static IEnumerator AdminPart()
        {
            if (!Features.Doors)
            {
                Report.Info("0.3.1 door phase skipped: TerritoryFlags has no AutoCloseDoors in this DLL, so the auto-close door checks do not apply");
                yield break;
            }

            Vector3 origin = ScenarioAdmin.Origin;
            JObject layout = RunDir.Read("layout");
            float rotation = layout != null && layout["rotation"] != null ? (float)layout["rotation"] : 0f;
            var keepClear = new List<Vector3> { origin };
            foreach (string k in new[] { "circle", "rect2", Layout.CombA.Key, Layout.CombB.Key, Layout.Barrier.Key, Layout.Guard.Key, Layout.NoBarrier.Key })
                if (ScenarioAdmin.Centres.TryGetValue(k, out Vector3 kc)) keepClear.Add(kc);
            bool placed = Layout.PlaceDoors(origin, rotation, keepClear, out Vector3 cDoors, out Vector3 cOff);
            var cNested = new Vector3(cDoors.x + Layout.NestedOffsetX, 0f, cDoors.z);
            cNested.y = World.Ground(cNested);
            ScenarioAdmin.Centres[Layout.Doors.Key] = cDoors;
            ScenarioAdmin.Centres[Layout.Nested.Key] = cNested;
            ScenarioAdmin.Centres[Layout.DoorsOff.Key] = cOff;
            Report.Info($"0.3.1 door phase ({(E2EEnv.Force031 && !Features.HasAutoCloseFlag ? "DRY RUN on a DLL WITHOUT AutoCloseDoors: P31a and P31f must go RED; " : "")}the server closes after {E2EEnv.DoorDelay:0.#} s): " +
                        $"'{Layout.DoorsName}' {World.P(cDoors)} flags {Layout.Doors.Flags}, nested '{Layout.NestedName}' {World.P(cNested)} flags {Layout.Nested.Flags}, " +
                        $"'{Layout.DoorsOffName}' {World.P(cOff)} flags {Layout.DoorsOff.Flags}; placement clear of the others: {placed}");

            var made = new Dictionary<string, Territory>();
            yield return Flow.Step("A32a-create-doors", "create the doors territory", Scenario030.CreateOne(Layout.Doors, cDoors, "A32a-create-doors", made));
            yield return Flow.Step("A32b-create-nested", "create the nested territory", Scenario030.CreateOne(Layout.Nested, cNested, "A32b-create-nested", made));
            yield return Flow.Step("A32c-create-off", "create the territory without the flag", Scenario030.CreateOne(Layout.DoorsOff, cOff, "A32c-create-off", made));

            JObject Rec(Spec s, Vector3 c)
            {
                made.TryGetValue(s.Key, out Territory t);
                return new JObject { ["id"] = t?.Id ?? 0, ["name"] = s.Name, ["centre"] = V(c), ["revision"] = t?.Revision ?? 0 };
            }
            RunDir.Signal("admin.doors", new JObject
            {
                ["ok"] = made.Count == 3,
                ["why"] = made.Count == 3 ? "" : "not all three territories were created (see A32a / A32b / A32c)",
                ["doors"] = Rec(Layout.Doors, cDoors),
                ["nested"] = Rec(Layout.Nested, cNested),
                ["off"] = Rec(Layout.DoorsOff, cOff)
            });
        }

        // ─────────────────────────────────────────────────────────────
        // TSPlain: real doors, opened, watched
        // ─────────────────────────────────────────────────────────────

        private sealed class Fixture
        {
            public string Role;             // P, Q, W, B, M, C0, F0
            public string Where;
            public string Prefab;
            public GameObject Go;
            public Door Door;
            public Vector3 At;
            public bool Opened;             // ZDO state was not 0 after the open RPC
            public float OpenedAt = -1f;    // seconds after the open RPC
            public float ClosedAt = -1f;    // first time its ZDO state read 0 again
            public float AnimClosedAt = -1f;// first time the animator's "state" read 0 again
            public string Governing = "";

            public int State => World.DoorState(Door);

            public int Anim => Door != null && Door.m_animator != null ? Door.m_animator.GetInteger("state") : int.MinValue;

            public override string ToString() =>
                $"{Role} {Prefab} {Where}: {(Go == null ? "NOT SPAWNED" : Opened ? "opened" : "did NOT open")}" +
                $"{(ClosedAt >= 0f ? $", closed {ClosedAt:0.0} s after the open (animator {(AnimClosedAt >= 0f ? AnimClosedAt.ToString("0.0") + " s" : "never")})" : ", still open at the end")}, governed by {Governing}";
        }

        private static readonly List<Fixture> _fixtures = new List<Fixture>();
        private static readonly HashSet<string> _reported = new HashSet<string>();

        private static void Rep(string id, string what, bool ok, string why)
        {
            _reported.Add(id);
            Report.Check(id, what, ok, why);
        }

        private static void RepSoft(string id, string what, string why)
        {
            _reported.Add(id);
            Report.Soft(id, what, why);
        }

        private static void DestroyFixtures()
        {
            foreach (Fixture f in _fixtures) Fixtures.Destroy(f.Go);
            _fixtures.Clear();
        }

        internal static IEnumerator PlainPart(Vector3 home)
        {
            if (!Features.Doors) yield break;

            _reported.Clear();
            var barrier = new Box<JObject>();
            yield return RunDir.Wait("admin.doors", 300f, barrier);
            JObject c = barrier.Value;
            if (c == null || c["ok"] == null || !(bool)c["ok"])
            {
                string reason = c == null ? "admin.doors never signalled" : $"the admin's territories were not all created ({(string)c["why"] ?? "see A32a / A32b / A32c"})";
                foreach (string id in Ids) Report.Fail(id, "the auto-close doors as a visitor sees them", reason);
                yield break;
            }
            var ok = new Box<bool>();
            yield return Flow.Until(() => World.ByName(Layout.DoorsName) != null && World.ByName(Layout.NestedName) != null && World.ByName(Layout.DoorsOffName) != null, T, ok);
            if (!ok.Value)
            {
                foreach (string id in Ids)
                    Report.Fail(id, "the auto-close doors as a visitor sees them", $"the territories did not reach TSPlain's index within {T:0} s");
                yield break;
            }

            DestroyFixtures();
            yield return Flow.Step("P31a-door-closes", "doors", Scenario());
            DestroyFixtures();
            foreach (string id in Ids.Where(i => !_reported.Contains(i)))
                Report.Fail(id, "the auto-close doors as a visitor sees them", "the door scenario ended before this check reported (see its own failure line)");
            yield return World.Go(home, 1f);
        }

        private static Fixture Lay(string role, string prefab, string where, Vector3 at)
        {
            var f = new Fixture { Role = role, Prefab = prefab, Where = where, At = at };
            GameObject go = World.Spawn(prefab, at);
            f.Go = go;
            f.Door = go != null ? go.GetComponent<Door>() : null;
            if (go != null) _fixtures.Add(f);
            return f;
        }

        /// <summary>The first of the prefab names that exists in the world and carries a Door.</summary>
        private static string DoorPrefab(params string[] names)
        {
            foreach (string n in names)
            {
                GameObject p = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(n) : null;
                if (p != null && p.GetComponent<Door>() != null) return n;
            }
            return null;
        }

        private static IEnumerator Scenario()
        {
            Territory d = World.ByName(Layout.DoorsName), n = World.ByName(Layout.NestedName), off = World.ByName(Layout.DoorsOffName);
            float delay = E2EEnv.DoorDelay;
            var cD = new Vector3(d.X, 0f, d.Z);
            Report.Info($"door checks as TSPlain: '{d.Name}' {World.Describe(d)} | '{n.Name}' {World.Describe(n)} | '{off.Name}' {World.Describe(off)}; " +
                        $"the server closes after {delay:0.#} s (this client's own AutoCloseDelay, as ServerSync left it: {Features.AutoCloseDelayConfigured:0.#})");

            yield return Scenario030.Arrive(cD, 0.8f);

            // Where each door goes (all wood_door but the window and the drawbridge). The player stands 2 m from Q and 7+ m from every other.
            Vector3 atP = World.Local(d, -8f, 0f), atQ = World.Local(d, 0f, 8f), atW = World.Local(d, 0f, -8f), atB = World.Local(d, -4f, -9f);
            var atM = new Vector3(n.X, 0f, n.Z);
            Vector3 atC = Scenario030.OutsidePoint(d, 8f, out string outNote);
            var atF = new Vector3(off.X, 0f, off.Z);

            string windowPrefab = DoorPrefab("wood_window");
            string bridgePrefab = DoorPrefab("piece_drawbridge", "piece_drawbridge_log", "drawbridge");
            Fixture p = Lay("P", "wood_door", $"in '{d.Name}', nobody near", atP);
            Fixture q = Lay("Q", "wood_door", $"in '{d.Name}', TSPlain 2 m away", atQ);
            Fixture m = Lay("M", "wood_door", $"in the unflagged '{n.Name}' nested in '{d.Name}'", atM);
            Fixture c0 = Lay("C0", "wood_door", $"outside every territory ({outNote})", atC);
            Fixture f0 = Lay("F0", "wood_door", $"in '{off.Name}' (no AutoCloseDoors)", atF);
            Fixture w = windowPrefab != null ? Lay("W", windowPrefab, $"in '{d.Name}' (excluded by default)", atW) : null;
            Fixture b = bridgePrefab != null ? Lay("B", bridgePrefab, $"in '{d.Name}' (excluded by default)", atB) : null;
            var all = new List<Fixture> { p, q, m, c0, f0 };
            if (w != null) all.Add(w);
            if (b != null) all.Add(b);
            yield return Flow.Seconds(1.6f);   // the door's animator must settle before it can be used (CanInteract)
            foreach (Fixture f in all)
            {
                Territory g = f.Go != null ? TerritoryIndex.GoverningAt(f.Go.transform.position) : null;
                f.Governing = g != null ? $"'{g.Name}' ({(g.Has(Features.AutoCloseDoors) && Features.AutoCloseDoors != 0 ? "AutoCloseDoors" : "no AutoCloseDoors")})" : "no territory";
            }

            // The player beside Q, then every door opened in the one frame, the way a Use press opens it (an RPC to the owner: this client).
            World.MoveTo(World.Local(d, 0f, 6f));
            yield return Flow.Seconds(0.3f);
            float tOpen = Time.realtimeSinceStartup;
            foreach (Fixture f in all.Where(x => x.Door != null && x.Door.m_nview != null && x.Door.m_nview.IsValid()))
                f.Door.m_nview.InvokeRPC("UseDoor", true);
            yield return Flow.Frames(3);
            foreach (Fixture f in all.Where(x => x.Door != null))
            {
                f.Opened = f.State != 0;
                if (!f.Opened && f.Door.m_nview != null && f.Door.m_nview.IsValid())
                {
                    f.Door.m_nview.InvokeRPC("UseDoor", true);   // one more try: the animator may not have been settled
                    yield return Flow.Frames(3);
                    f.Opened = f.State != 0;
                }
            }
            foreach (Fixture f in all) f.OpenedAt = f.Opened ? Time.realtimeSinceStartup - tOpen : -1f;

            // Stage 1: until delay + 4 s after the open. Stage 2: the player walks 8 m away from Q and everything is watched 4 s more.
            float stage1End = tOpen + delay + 4f;
            while (Time.realtimeSinceStartup < stage1End)
            {
                Watch(all, tOpen);
                yield return null;
            }
            bool qOpenAtEnd1 = q.Opened && q.State != 0;
            World.MoveTo(cD);
            float tWalk = Time.realtimeSinceStartup;
            float stage2End = tWalk + 4f;
            while (Time.realtimeSinceStartup < stage2End)
            {
                Watch(all, tOpen);
                yield return null;
            }
            Watch(all, tOpen);
            yield return Flow.Seconds(0.5f);
            Watch(all, tOpen);

            Report.Info("door evidence - " + string.Join(" || ", all.Select(x => x.ToString())) +
                        $"; the player stood 2 m from Q until {delay + 4f:0.#} s after the open, then walked to the centre ({(tWalk - tOpen):0.0} s after the open); Q was open at that moment: {qOpenAtEnd1}");

            // P31a: closes, after about the delay, animator too.
            var why = new List<string>();
            if (p.Go == null || !p.Opened) why.Add($"the door never opened (spawned {p.Go != null}, state {(p.Door != null ? p.State.ToString() : "n/a")}), so nothing was tested");
            else
            {
                if (p.ClosedAt < 0f) why.Add($"it was still open {Time.realtimeSinceStartup - tOpen:0.0} s after it was opened (delay {delay:0.#} s, allowed up to delay + 4 s)");
                else
                {
                    if (p.ClosedAt < delay - 0.3f) why.Add($"it closed after only {p.ClosedAt:0.0} s, before the {delay:0.#} s delay: something other than the timer closed it");
                    if (p.ClosedAt > delay + 4f) why.Add($"it closed after {p.ClosedAt:0.0} s (allowed up to delay + 4 s = {delay + 4f:0.#} s)");
                    if (p.AnimClosedAt < 0f || p.AnimClosedAt > p.ClosedAt + 1.5f) why.Add($"its animator's state went to 0 {(p.AnimClosedAt < 0f ? "never" : p.AnimClosedAt.ToString("0.0") + " s after the open")} (ZDO state 0 at {p.ClosedAt:0.0} s), so the door did not close on screen");
                }
            }
            Rep("P31a-door-closes",
                $"a wood_door left open inside '{d.Name}' with nobody within 3 m is closed by the server after about the {delay:0.#} s delay (ZDO state 0 after {p.ClosedAt:0.0} s, animator state 0 after {p.AnimClosedAt:0.0} s)",
                why.Count == 0, string.Join("; ", why));

            // The negative controls: opened, and still open at the very end.
            StaysOpen("P31b-door-outside-stays", c0, $"the same wood_door outside every territory stays open for {Time.realtimeSinceStartup - tOpen:0} s, three times the delay and more", true);
            StaysOpen("P31c-door-flag-off-stays", f0, $"a wood_door inside '{off.Name}' (a territory without AutoCloseDoors) stays open for {Time.realtimeSinceStartup - tOpen:0} s", true);
            StaysOpen("P31e-door-nested-stays", m, $"a wood_door inside '{n.Name}', an unflagged territory nested in the flagged '{d.Name}', stays open for {Time.realtimeSinceStartup - tOpen:0} s (the smaller territory governs)", true);

            // P31d: the window and the drawbridge (excluded by default). Not present or not openable headless is SOFT.
            var excluded = new List<Fixture>();
            if (w != null) excluded.Add(w);
            if (b != null) excluded.Add(b);
            if (excluded.Count == 0)
                RepSoft("P31d-door-excluded-stays", $"a wood_window inside '{d.Name}' stays open", "this world has neither a wood_window nor a drawbridge prefab with a Door");
            else if (excluded.Any(x => x.Go == null || !x.Opened))
                RepSoft("P31d-door-excluded-stays", $"a {string.Join(" and a ", excluded.Select(x => x.Prefab))} inside '{d.Name}' stays open",
                        $"the fixture did not open headless, so nothing was tested: {string.Join(" || ", excluded.Select(x => x.ToString()))}");
            else
                Rep("P31d-door-excluded-stays", $"a {string.Join(" and a ", excluded.Select(x => x.Prefab))} left open inside '{d.Name}' stays open for {Time.realtimeSinceStartup - tOpen:0} s (excluded by default)",
                    excluded.All(x => x.ClosedAt < 0f && x.State != 0), string.Join(" || ", excluded.Select(x => x.ToString())));

            // P31f: kept open while the player is within 3 m; closes soon after they leave.
            var whyQ = new List<string>();
            if (q.Go == null || !q.Opened) whyQ.Add($"the door never opened (state {(q.Door != null ? q.State.ToString() : "n/a")}), so nothing was tested");
            else
            {
                if (q.ClosedAt >= 0f && q.ClosedAt < tWalk - tOpen) whyQ.Add($"it closed after {q.ClosedAt:0.0} s while the player stood 2 m away (the walk away came at {tWalk - tOpen:0.0} s)");
                else if (!qOpenAtEnd1) whyQ.Add("it was not open when the player walked away");
                if (q.ClosedAt < 0f) whyQ.Add($"it was still open {Time.realtimeSinceStartup - tWalk:0.0} s after the player walked 8 m away (allowed 4 s)");
                else if (q.ClosedAt - (tWalk - tOpen) > 4f) whyQ.Add($"it closed {q.ClosedAt - (tWalk - tOpen):0.0} s after the player walked away (allowed 4 s)");
            }
            Rep("P31f-door-player-near",
                $"a wood_door with the player 2 m away stays open past the delay ({delay + 4f:0.#} s after the open) and closes {(q.ClosedAt >= 0f ? (q.ClosedAt - (tWalk - tOpen)).ToString("0.0") : "never")} s after the player walks away",
                whyQ.Count == 0, string.Join("; ", whyQ));
        }

        private static void Watch(List<Fixture> all, float tOpen)
        {
            float now = Time.realtimeSinceStartup - tOpen;
            foreach (Fixture f in all)
            {
                if (f.Door == null || !f.Opened) continue;
                if (f.ClosedAt < 0f && f.State == 0) f.ClosedAt = now;
                if (f.ClosedAt >= 0f && f.AnimClosedAt < 0f && f.Anim == 0) f.AnimClosedAt = now;
            }
        }

        private static void StaysOpen(string id, Fixture f, string what, bool fixtureMustOpen)
        {
            if (f.Go == null || !f.Opened)
            {
                Report.Fail(id, what, $"the door never opened ({f}), so 'still open' proves nothing (a wood_door is known to open headless)");
                _reported.Add(id);
                return;
            }
            Rep(id, what, f.ClosedAt < 0f && f.State != 0, f.ToString());
        }
    }
}
