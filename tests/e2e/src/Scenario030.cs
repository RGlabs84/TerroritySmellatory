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
    /// The 0.3.0 phase: the runic barrier. Played after the 0.2.0 phase (admin phase 2, plain phase 2) and one more
    /// login of each character (phase 3), so no older check sees an extra territory. Gated by reflection
    /// (<see cref="Features.Barrier"/>: TerritoryFlags has RunicBarrier): on a DLL without it the admin logs ONE INFO line
    /// and nothing else happens; the server side (S31a, S31b, R31) lives in ServerRole.
    ///
    /// TSAdmin marks three circles of r=10 through the draft and terr_place, as the rig does everywhere: a BARRIER
    /// (NoMonsterSpawns + RunicBarrier), a GUARD (the same + BarrierKeepsOutVisitors) and a CONTROL (NoMonsterSpawns only).
    /// TSPlain, a visitor and no member, then asks the GAME what happens, reading real positions against the territory's
    /// ground (the mod's own TerritoryGround.DistanceOutside and, independently, the circle's own maths):
    ///   P30a  a hostile Greyling HUNTING TSPlain (BaseAI.SetHuntPlayer: its own MonsterAI walks it to the player at the
    ///         centre) is held outside the barrier; the same hunt into the control territory gets it deep inside;
    ///   P30b  a Greyling placed inside the barrier ends outside within ~2 s and stays out;
    ///   P30c  BarrierFlare.Requested rose while the barrier pushed, and did not in the control;
    ///   P30d  a TAMED creature (and a passive deer, SOFT) stays inside;
    ///   P30e  TSPlain, walking at the guard in 0.3 m Rigidbody steps, is held outside; the plain barrier lets it walk in;
    ///   P30f  TSPlain teleported into the guard ends outside; P30g the barrier flared for it;
    ///   phase 3: the admin adds TSPlain as a member of the guard (A31) and TSPlain now walks in (P30e3).
    ///
    /// The creature is driven the way its own AI drives it (a hunt on the player), never by shoving its body: a hold that
    /// does not cover the AI's own walking is no hold. If the AI cannot move a creature at all headless (no path), the
    /// drive falls back, for the control and the barrier alike, to BaseAI.MoveTowards each frame with the AI off, and
    /// every evidence line says which drive ran.
    /// </summary>
    internal static class Scenario030
    {
        private static float T => E2EEnv.StepTimeout;

        /// <summary>A creature or a player counts as OUTSIDE when at most this far inside the edge (float noise, body size).</summary>
        internal const float InsideEps = 0.1f;

        /// <summary>The deepest a held creature or visitor may ever be seen inside the edge, metres.</summary>
        internal const float HoldSlack = 0.5f;

        /// <summary>"About 2 s": a creature or a visitor placed inside must be outside by then (plus a little slack).</summary>
        internal const float PushOutSeconds = 2.5f;

        /// <summary>A drive counts as having reached the barrier when it got this close to the edge (else the hold is untested).</summary>
        private const float ReachEdge = 3f;

        /// <summary>The control drive must get the creature at least this far inside, or the drive itself is broken.</summary>
        private const float ControlDepth = 4f;

        private const string HostilePrefab = "Greyling";
        private const string PassivePrefab = "Deer";

        // What the admin INTENDS, written out by hand as the file carries the flags (by name): the server side compares
        // the store and the file with these, never with the enum's own spelling of what it was given.
        internal static readonly string[] BarrierFlagNames = { "NoMonsterSpawns", "RunicBarrier" };
        internal static readonly string[] GuardFlagNames = { "NoMonsterSpawns", "RunicBarrier", "BarrierKeepsOutVisitors" };
        internal static readonly string[] ControlFlagNames = { "NoMonsterSpawns" };

        /// <summary>The numbers the contract fixes: RunicBarrier = 1 &lt;&lt; 11, BarrierKeepsOutVisitors = 1 &lt;&lt; 12.</summary>
        internal const int RunicBarrierBit = 1 << 11, KeepsOutVisitorsBit = 1 << 12;

        private static JObject V(Vector3 v) => new JObject { ["x"] = v.x, ["y"] = v.y, ["z"] = v.z };

        // ─────────────────────────────────────────────────────────────
        // TSAdmin: mark the three circles
        // ─────────────────────────────────────────────────────────────

        internal static IEnumerator AdminPart()
        {
            if (!Features.Barrier)
            {
                Report.Info("0.3.0 phase skipped: TerritoryFlags has no RunicBarrier in this DLL, so the runic barrier checks do not apply " +
                            "and the older phases are the whole scenario");
                yield break;
            }

            Vector3 origin = ScenarioAdmin.Origin;
            JObject layout = RunDir.Read("layout");
            float rotation = layout != null && layout["rotation"] != null ? (float)layout["rotation"] : 0f;
            var keepClear = new List<Vector3> { origin };
            foreach (string k in new[] { "circle", "rect2", Layout.CombA.Key, Layout.CombB.Key })
                if (ScenarioAdmin.Centres.TryGetValue(k, out Vector3 kc)) keepClear.Add(kc);
            Vector3[] cs = Layout.PlaceBarriers(origin, rotation, keepClear);
            var specs = new[] { Layout.Barrier, Layout.Guard, Layout.NoBarrier };
            for (int i = 0; i < 3; i++) ScenarioAdmin.Centres[specs[i].Key] = cs[i];
            Report.Info($"0.3.0 barrier phase ({Features.Describe()}{(E2EEnv.Force030 && !Features.HasBarrierFlag ? "; DRY RUN on a DLL WITHOUT the barrier: the barrier checks must go RED" : "")}): " +
                        string.Join(", ", specs.Select((s, i) => $"'{s.Name}' {World.P(cs[i])} flags {s.Flags}")) + ", r=10 each");

            var made = new Dictionary<string, Territory>();
            yield return Flow.Step("A30a-create-barrier", "create the barrier", CreateOne(Layout.Barrier, cs[0], "A30a-create-barrier", made));
            yield return Flow.Step("A30b-create-guard", "create the guard", CreateOne(Layout.Guard, cs[1], "A30b-create-guard", made));
            yield return Flow.Step("A30c-create-control", "create the control", CreateOne(Layout.NoBarrier, cs[2], "A30c-create-control", made));

            Territory Get(Spec s) => made.TryGetValue(s.Key, out Territory t) ? t : null;
            JObject Rec(Spec s, Vector3 c)
            {
                Territory t = Get(s);
                return new JObject { ["id"] = t?.Id ?? 0, ["name"] = s.Name, ["centre"] = V(c), ["revision"] = t?.Revision ?? 0 };
            }
            RunDir.Signal("admin.barriers", new JObject
            {
                ["ok"] = made.Count == 3,
                ["why"] = made.Count == 3 ? "" : "not all three territories were created (see A30a / A30b / A30c)",
                ["force"] = E2EEnv.Force030,
                ["radius"] = Layout.Barrier.HalfX,
                ["barrier"] = Rec(Layout.Barrier, cs[0]),
                ["guard"] = Rec(Layout.Guard, cs[1]),
                ["control"] = Rec(Layout.NoBarrier, cs[2])
            });
        }

        internal static IEnumerator CreateOne(Spec s, Vector3 c, string id, Dictionary<string, Territory> made)
        {
            ScenarioAdmin.SetDraft(s);
            float before = TerritoryClient.LastResultAt;
            World.Run(IhCommand.Place(IhCommand.ShapeOf(s.Kind), c, s.HalfX, s.HalfZ, s.Yaw));
            var ok = new Box<bool>();
            yield return Flow.Until(() => World.ByName(s.Name) != null && TerritoryClient.LastResultAt > before, T, ok);
            Territory t = World.ByName(s.Name);
            string result = TerritoryClient.LastResult ?? "";
            var why = new List<string>();
            if (!ok.Value || t == null || !TerritoryClient.LastResultOk || !result.StartsWith("Created #"))
                why.Add($"not created: result ok={TerritoryClient.LastResultOk} '{result}', in the index: {t != null}");
            else
            {
                made[s.Key] = t;
                if (t.Kind != s.Kind || Mathf.Abs(t.X - c.x) > 0.05f || Mathf.Abs(t.Z - c.z) > 0.05f || !Mathf.Approximately(t.HalfX, s.HalfX))
                    why.Add($"shape or centre differs from the draft ({World.Describe(t)})");
                if (t.Flags != s.Flags) why.Add($"flags {t.Flags} != drafted {s.Flags}");
                if (t.PvP != s.PvP) why.Add($"pvp {t.PvP}");
                if (t.Revision != 1) why.Add($"revision {t.Revision}");
                if (!t.CreatedBy.StartsWith(E2EEnv.AdminName)) why.Add($"createdBy '{t.CreatedBy}'");
            }
            Report.Check(id, $"'{s.Name}' created through the draft and terr_place with flags {s.Flags}, and back in the admin's snapshot exactly as drafted: {World.Describe(t)}",
                why.Count == 0, string.Join("; ", why));
        }

        // ─────────────────────────────────────────────────────────────
        // TSAdmin, phase 3: add TSPlain to the guard
        // ─────────────────────────────────────────────────────────────

        internal static IEnumerator AdminMember()
        {
            if (!Features.Barrier) yield break;
            JObject b = RunDir.Read("admin.barriers");
            if (b == null || !(bool)b["ok"])
            {
                Report.Fail("A31-guard-member", "add TSPlain as a member of the guard", "the barrier territories were not created (admin.barriers " + (b == null ? "missing" : "says not ok") + ")");
                RunDir.Signal("admin.barrier-member", new JObject { ["ok"] = false });
                yield break;
            }
            var ready = new Box<bool>();
            yield return Flow.Until(() => TerritoryClient.HasSnapshot && World.ByName(Layout.GuardName) != null, T, ready);
            JObject plain = RunDir.Read("plain.ready");
            long plainId = plain != null ? (long)plain["id"] : 0L;
            Territory guard = World.ByName(Layout.GuardName);
            if (guard == null || plainId == 0L)
            {
                Report.Fail("A31-guard-member", "add TSPlain as a member of the guard", $"guard in the index: {guard != null}, plain id {plainId}");
                RunDir.Signal("admin.barrier-member", new JObject { ["ok"] = false });
                yield break;
            }

            World.Run(IhCommand.Edit(new Vector3(guard.X, guard.Y, guard.Z)));
            yield return Flow.Frames(2);
            Territory editing = TerritoryPanel._editing;
            if (!(TerritoryPanel.IsOpen && editing != null && editing.Id == guard.Id))
            {
                Report.Fail("A31-guard-member", "add TSPlain as a member of the guard", $"terr_edit_at did not open the panel on '{guard.Name}': open={TerritoryPanel.IsOpen} editing={editing?.Id}");
                RunDir.Signal("admin.barrier-member", new JObject { ["ok"] = false });
                yield break;
            }
            TerritoryMember online = TerritoryClient.OnlinePlayers.FirstOrDefault(m => m.Name == E2EEnv.PlainName);
            TerritoryPanel.AddMember(online != null ? online.Id : plainId, E2EEnv.PlainName);
            TerritoryFlags flags = guard.Flags;
            int rev = guard.Revision;
            float before = TerritoryClient.LastResultAt;
            TerritoryClient.Send(RequestOp.Update, editing.Clone(), editing.Id);   // what "Save changes" sends
            TerritoryPanel.Close();

            var ok = new Box<bool>();
            yield return Flow.Until(() => TerritoryIndex.Find(guard.Id)?.Revision > rev && TerritoryClient.LastResultAt > before, T, ok);
            Territory now = TerritoryIndex.Find(guard.Id);
            bool good = ok.Value && now != null && TerritoryClient.LastResultOk && now.IsMember(plainId) && now.Flags == flags && now.Revision == rev + 1 &&
                        Mathf.Approximately(now.HalfX, guard.HalfX) && Mathf.Abs(now.X - guard.X) < 0.05f;
            Report.Check("A31-guard-member", $"the panel's Add member + Save changes made TSPlain ({plainId}) a member of '{guard.Name}' and kept its barrier flags ({flags}): {World.Describe(now)}",
                good, $"result '{TerritoryClient.LastResult}' now={World.Describe(now)} flags before {flags}");
            RunDir.Signal("admin.barrier-member", new JObject { ["ok"] = good, ["id"] = guard.Id, ["plainId"] = plainId, ["revision"] = now?.Revision ?? 0 });
        }

        // ─────────────────────────────────────────────────────────────
        // Measuring: positions against the ground
        // ─────────────────────────────────────────────────────────────

        /// <summary>How far INSIDE the circle a point is, metres (negative: that far outside). The driver's own maths on the record's centre and radius.</summary>
        internal static float DepthOf(Territory t, Vector3 p) => t.HalfX - Vector2.Distance(new Vector2(p.x, p.z), new Vector2(t.X, t.Z));

        /// <summary>The deeper of an object's transform and its Rigidbody (a body is interpolated: either may be what a rule reads).</summary>
        private static float DepthOf(Territory t, Component c, out Vector3 at)
        {
            at = c.transform.position;
            float d = DepthOf(t, at);
            Rigidbody body = c.GetComponent<Rigidbody>();
            if (body != null) d = Mathf.Max(d, DepthOf(t, body.position));
            return d;
        }

        private sealed class Trace
        {
            public string Label;
            public Vector3 Start, Last;
            public float MaxDepth = float.NegativeInfinity;        // deepest inside seen, m (negative: never inside)
            public float MinDepth = float.PositiveInfinity;        // most outside seen
            public float EdgeGap = float.PositiveInfinity;         // closest to the edge while outside, m
            public float EndDepth = float.NaN;
            public float Seconds, Travel, SpawnDistance;
            public int Frames;
            public float FirstOutsideAt = -1f;                     // first sample at or beyond the edge
            public float MaxDepthAfterOut = float.NegativeInfinity;
            public float ReachedEdgeAt = -1f;                      // first sample within 1.5 m of the edge
            public string Mode = "ai";
            public string Setup = "";
            public bool Ready;                                     // spawned, owned and driven: the trace means something
            public readonly List<string> Path = new List<string>();
            public readonly List<string> Wild = new List<string>();   // hostile creatures NOT ours seen within 8 m of a barrier's edge while this drive ran
            public float NextWild;
            private float _nextPath;

            public void AddWild(string what)
            {
                if (Wild.Count < 6 && !Wild.Contains(what)) Wild.Add(what);
            }

            public void Add(float now, Vector3 p, float depth)
            {
                if (Frames++ == 0) Start = p;
                Seconds = now;
                Last = p;
                EndDepth = depth;
                Travel = Mathf.Max(Travel, Vector3.Distance(Start, p));
                MaxDepth = Mathf.Max(MaxDepth, depth);
                MinDepth = Mathf.Min(MinDepth, depth);
                EdgeGap = Mathf.Min(EdgeGap, Mathf.Max(0f, -depth));
                if (depth <= InsideEps && FirstOutsideAt < 0f) FirstOutsideAt = now;
                if (FirstOutsideAt >= 0f && now > FirstOutsideAt) MaxDepthAfterOut = Mathf.Max(MaxDepthAfterOut, depth);
                if (Mathf.Abs(depth) <= 1.5f && ReachedEdgeAt < 0f) ReachedEdgeAt = now;
                if (now >= _nextPath)
                {
                    _nextPath = now + 1f;
                    Path.Add($"{now:0.0}s d={depth:0.0}");
                }
            }

            public override string ToString() =>
                $"{Label}: {(Ready ? "" : "NOT DRIVEN; ")}{Setup}; over {Seconds:0.0} s ({Frames} samples, drive {Mode}) deepest {MaxDepth:0.00} m inside, " +
                $"closest to the edge from outside {EdgeGap:0.00} m, ended {EndDepth:0.00} m inside at {World.P(Last)}, moved {Travel:0.0} m from {World.P(Start)}; path [{string.Join(", ", Path)}]";
        }

        private static readonly List<GameObject> _spawned = new List<GameObject>();

        /// <summary>The hunt's drive: "ai" (the creature's own MonsterAI), or "assist" once the AI proved unable to move it at all (both hunts then use it).</summary>
        private static string _mode = "ai";

        private static void DestroyAll()
        {
            foreach (GameObject go in _spawned) Fixtures.Destroy(go);
            _spawned.Clear();
        }

        /// <summary>One check as a step: creatures never outlive it, and an exception is a FAIL with the check's own id.</summary>
        internal static IEnumerator Step(string id, string what, IEnumerator body)
        {
            DestroyAll();
            yield return Flow.Step(id, what, body);
            DestroyAll();
        }

        private static GameObject SpawnCreature(string prefabName, Vector3 xz, Vector3 facing)
        {
            GameObject prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefabName) : null;
            if (prefab == null) return null;
            facing.y = 0f;
            Quaternion rot = facing.sqrMagnitude > 0.01f ? Quaternion.LookRotation(facing.normalized) : Quaternion.identity;
            GameObject go = UnityEngine.Object.Instantiate(prefab, new Vector3(xz.x, World.Ground(xz) + 0.4f, xz.z), rot);
            _spawned.Add(go);
            return go;
        }

        /// <summary>A point <paramref name="beyond"/> metres past the edge of t, in the first of eight directions that is dry the whole way in and free of any other territory.</summary>
        internal static Vector3 OutsidePoint(Territory t, float beyond, out string note)
        {
            var c = new Vector3(t.X, 0f, t.Z);
            float water = World.WaterLevel;
            for (int k = 0; k < 8; k++)
            {
                float a = k * 45f * Mathf.Deg2Rad;
                var dir = new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));
                Vector3 p = c + dir * (t.HalfX + beyond);
                bool ok = TerritoryIndex.GoverningAt(p) == null;
                for (int i = 0; i <= 6 && ok; i++)
                {
                    Vector3 q = Vector3.Lerp(p, c, i / 6f);
                    if (World.Ground(q) < water + 0.4f) ok = false;
                    Territory g = TerritoryIndex.GoverningAt(q);
                    if (g != null && g.Id != t.Id) ok = false;
                }
                if (ok)
                {
                    note = $"direction {k * 45} deg";
                    return p;
                }
            }
            note = "NO clear direction (wet or another territory in the way), using direction 0";
            return c + new Vector3(0f, 0f, t.HalfX + beyond);
        }

        /// <summary>Teleports the player there, waits for the area's objects to be ready, and settles.</summary>
        internal static IEnumerator Arrive(Vector3 xz, float settle)
        {
            yield return World.Go(xz, 0.3f);
            var ok = new Box<bool>();
            yield return Flow.Until(() => World.Me != null && ZNetScene.instance != null && ZNetScene.instance.IsAreaReady(World.Me.transform.position), 25f, ok);
            if (!ok.Value) Report.Info($"the area around {World.P(xz)} was not ready after 25 s");
            World.MoveTo(xz);
            yield return Flow.Seconds(settle);
        }

        private static float Now => Time.realtimeSinceStartup;

        /// <summary>A moment in a trace, or "never" when it did not happen (-1 inside the trace).</summary>
        private static string When(float t) => t < 0f ? "never" : $"{t:0.00} s";

        private static string Deep(float d) => float.IsInfinity(d) ? "n/a" : $"{d:0.00} m";

        // ─────────────────────────────────────────────────────────────
        // TSPlain, phase 2
        // ─────────────────────────────────────────────────────────────

        private static Territory _barrier, _guard, _control;
        private static int _flareControl = -1, _flareBarrier = -1, _flareVisitor = -1;
        private static int _drawnControl = -1, _drawnBarrier = -1, _drawnVisitor = -1;   // BarrierFlare.Drawn during the same three drives (only a --graphics client can raise it)
        private static Trace _holdControl, _holdBarrier, _walkHeld;
        private static readonly string[] PlainIds =
        {
            "P30a-hold", "P30b-pushout", "P30c-flare", "P30d-pass-tamed", "P30d2-pass-passive",
            "P30e-visitor-held", "P30e2-open-barrier-free", "P30f-visitor-pushout", "P30g-visitor-flare"
        };
        // A --graphics client also owes the two checks that need a GL device (run-e2e.sh's H12 expects them then).
        private static readonly string[] GraphicsIds = { "P30h-flare-drawn", "P30i-flare-pixels", "P30j-flare-in-game-view" };
        private static IEnumerable<string> OwedIds() => FlareShot.Available ? PlainIds.Concat(GraphicsIds) : PlainIds;

        internal static IEnumerator PlainPart(Vector3 home)
        {
            if (!Features.Barrier) yield break;

            var barrier = new Box<JObject>();
            yield return RunDir.Wait("admin.barriers", 300f, barrier);
            JObject c = barrier.Value;
            if (c == null || c["ok"] == null || !(bool)c["ok"])
            {
                string reason = c == null ? "admin.barriers never signalled" : $"the admin's territories were not all created ({(string)c["why"] ?? "see A30a / A30b / A30c"})";
                foreach (string id in OwedIds()) Report.Fail(id, "the runic barrier as a visitor sees it", reason);
                yield break;
            }

            var ok = new Box<bool>();
            yield return Flow.Until(() => World.ByName(Layout.BarrierName) != null && World.ByName(Layout.GuardName) != null && World.ByName(Layout.ControlName) != null, T, ok);
            _barrier = World.ByName(Layout.BarrierName);
            _guard = World.ByName(Layout.GuardName);
            _control = World.ByName(Layout.ControlName);
            if (_barrier == null || _guard == null || _control == null)
            {
                foreach (string id in OwedIds())
                    Report.Fail(id, "the runic barrier as a visitor sees it",
                        $"the territories did not reach TSPlain's index within {T:0} s (barrier {_barrier != null}, guard {_guard != null}, control {_control != null})");
                yield break;
            }
            Report.Info($"0.3.0 barrier checks as TSPlain (member of none): barrier {World.Describe(_barrier)} | guard {World.Describe(_guard)} | control {World.Describe(_control)}; " +
                        $"BarrierFlare.Requested counter {(Features.HasFlareCounter ? "found, now " + Features.FlareRequested : "NOT FOUND")}; " +
                        $"Greyling hostile by the mod's own Hostility: {Hostility.IsHostile(ZNetScene.instance.GetPrefab(HostilePrefab))}");

            yield return Step("P30a-hold", "hold", HoldStep());
            yield return Step("P30c-flare", "flare", FlareStep());
            yield return Step("P30b-pushout", "push-out", PushOutStep());
            yield return Step("P30d-pass-tamed", "tamed passes", PassTamedStep());
            yield return Step("P30d2-pass-passive", "passive passes", PassPassiveStep());
            yield return Step("P30e-visitor-held", "visitor held", VisitorHeldStep());
            yield return Step("P30g-visitor-flare", "visitor flare", VisitorFlareStep());
            if (FlareShot.Available)
            {
                // Only with a real graphics device (--graphics): did the flares the pushes asked for reach the screen, and does one look like a shield?
                yield return Step("P30h-flare-drawn", "flares drawn", DrawnStep());
                yield return Step("P30i-flare-pixels", "flare pixels", FlareShot.RunIsolated("P30i-flare-pixels", "world"));
                yield return Step("P30j-flare-in-game-view", "flare in the game's own view", FlareShot.RunInGame("P30j-flare-in-game-view", "world-game"));
            }
            yield return Step("P30f-visitor-pushout", "visitor pushed out", VisitorPushOutStep());
            yield return Step("P30e2-open-barrier-free", "plain barrier is open to players", OpenBarrierStep());
            yield return World.Go(home, 1f);
        }

        // ── P30a: a hostile creature hunting the player is held outside; the control lets it in ─────────

        /// <summary>
        /// Spawns a Greyling (this client owns it), makes it hunt the player standing at the territory's centre
        /// (BaseAI.SetHuntPlayer: MonsterAI.FindEnemy then answers the closest player, and UpdateAI walks to it with
        /// MoveTo and pathfinding, exactly as a raid monster does), and samples it every frame.
        /// </summary>
        private static IEnumerator Hunt(Territory t, Trace tr, bool control)
        {
            var centre = new Vector3(t.X, 0f, t.Z);
            yield return Arrive(centre, 0.6f);
            Vector3 spawn = OutsidePoint(t, 14f, out string note);
            GameObject prefab = ZNetScene.instance.GetPrefab(HostilePrefab);
            GameObject go = SpawnCreature(HostilePrefab, spawn, centre - spawn);
            if (go == null)
            {
                tr.Setup = $"no '{HostilePrefab}' prefab in ZNetScene";
                yield break;
            }
            yield return Flow.Frames(5);
            Character ch = go.GetComponent<Character>();
            BaseAI ai = go.GetComponent<BaseAI>();
            ZNetView nv = go.GetComponent<ZNetView>();
            bool owned = nv != null && nv.IsValid() && nv.IsOwner();
            tr.SpawnDistance = Vector2.Distance(new Vector2(go.transform.position.x, go.transform.position.z), new Vector2(t.X, t.Z));
            tr.Setup = $"{go.name} ({ai?.GetType().Name}) spawned {World.F(tr.SpawnDistance)} m from the centre ({note}), owned by this client: {owned}, faction {ch?.m_faction}, " +
                       $"hostile by the mod's own Hostility: {Hostility.IsHostile(prefab)}";
            if (ch == null || ai == null || !owned) yield break;
            ai.SetHuntPlayer(true);
            tr.Ready = true;
            tr.Mode = _mode;
            if (_mode == "assist") ai.enabled = false;

            float t0 = Now;
            float deepAt = -1f;
            while (true)
            {
                float el = Now - t0;
                if (go == null || ch == null || ch.IsDead())
                {
                    tr.Setup += "; the creature is gone or dead";
                    break;
                }
                float depth = DepthOf(t, go.transform, out Vector3 at);
                tr.Add(el, at, depth);
                NoteWild(tr, el);
                if (_mode == "ai" && el > 4f && tr.Travel < 2f)
                {
                    // the AI cannot walk it (no path headless, or it never woke): both hunts use the assisted drive from now on
                    _mode = "assist";
                    tr.Mode = "ai then assist";
                    ai.enabled = false;
                    Report.Info($"{tr.Label}: the creature's own AI moved it {World.F(tr.Travel)} m in 4 s - switching to the assisted drive (AI off, BaseAI.MoveTowards every frame)");
                }
                if (_mode == "assist")
                {
                    Vector3 d = centre - new Vector3(at.x, 0f, at.z);
                    d.y = 0f;
                    if (d.sqrMagnitude > 0.01f) ai.MoveTowards(d.normalized, run: true);
                }
                if (control)
                {
                    if (tr.MaxDepth >= ControlDepth && deepAt < 0f) deepAt = el;
                    if (deepAt >= 0f && el - deepAt > 1f) break;
                    if (el > 22f) break;
                }
                else
                {
                    if (tr.ReachedEdgeAt >= 0f && el - tr.ReachedEdgeAt > 6f) break;
                    if (el > 26f) break;
                }
                yield return null;
            }
        }

        /// <summary>
        /// The flare counter is global: it counts a wild Greydwarf leaning on a barrier as well as ours (run 205053 saw 2 flares
        /// in the control hunt, most likely that). Four times a second, note every hostile creature that is not ours and is
        /// within 8 m of either barrier's edge, so a flare in the control can be told from one the mod raised for nothing.
        /// </summary>
        private static void NoteWild(Trace tr, float el)
        {
            if (el < tr.NextWild) return;
            tr.NextWild = el + 0.25f;
            ZNetScene scene = ZNetScene.instance;
            if (scene == null) return;
            foreach (Character c in Character.GetAllCharacters())
            {
                if (c == null || c.IsPlayer() || c.IsDead() || c.IsTamed() || _spawned.Contains(c.gameObject)) continue;
                ZNetView v = c.m_nview;
                if (v == null || !v.IsValid() || !Hostility.IsHostile(scene.GetPrefab(v.GetZDO().GetPrefab()))) continue;
                foreach (Territory b in new[] { _barrier, _guard })
                {
                    float d = DepthOf(b, c.transform.position);
                    if (d > -8f)
                    {
                        tr.AddWild($"{c.name} {Mathf.Abs(d):0.0} m {(d > 0f ? "inside" : "outside")} '{b.Name}'");
                        break;
                    }
                }
            }
        }

        private static string _controlWild = "";

        private static IEnumerator HoldStep()
        {
            _mode = "ai";
            // The control hunt, measured again ONCE if it saw flares while a wild hostile creature was at a barrier (which could
            // have raised them): a contaminated measurement is inconclusive, not a pass and not a fail.
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                int fa = Features.FlareRequested, da = Features.FlareDrawn;
                _holdControl = new Trace { Label = $"control '{_control.Name}'" };
                yield return Hunt(_control, _holdControl, control: true);
                _flareControl = Features.FlareRequested - fa;
                _drawnControl = Features.FlareDrawn - da;
                _controlWild = string.Join("; ", _holdControl.Wild);
                DestroyAll();
                if (_flareControl == 0 || _holdControl.Wild.Count == 0) break;
                if (attempt == 1) Report.Info($"P30c: {_flareControl} flare(s) requested during the control hunt while wild hostile creature(s) stood at a barrier [{_controlWild}] - measuring the control hunt once more");
            }
            int f1 = Features.FlareRequested, d1 = Features.FlareDrawn;

            _holdBarrier = new Trace { Label = $"barrier '{_barrier.Name}'" };
            yield return Hunt(_barrier, _holdBarrier, control: false);
            int f2 = Features.FlareRequested, d2 = Features.FlareDrawn;
            _flareBarrier = f2 - f1;
            _drawnBarrier = d2 - d1;
            DestroyAll();

            Trace ctl = _holdControl, bar = _holdBarrier;
            float modOutside = Features.DistanceOutside(_barrier, bar.Last);
            Report.Info($"P30a evidence - {ctl} || {bar}; TerritoryGround.DistanceOutside at the barrier drive's last position: {modOutside:0.00}; flares requested: control {_flareControl}, barrier {_flareBarrier}; wild hostile creatures at a barrier during the control hunt [{_controlWild}], during the barrier hunt [{string.Join("; ", bar.Wild)}]");

            var why = new List<string>();
            if (!ctl.Ready || ctl.MaxDepth < ControlDepth)
                why.Add($"the CONTROL drive never got the creature inside the control territory by {ControlDepth:0} m (deepest {ctl.MaxDepth:0.00} m; {(ctl.Ready ? "it was driven" : "it was not driven: " + ctl.Setup)}), " +
                        "so the drive itself is unproven and the barrier result means nothing");
            else if (!bar.Ready) why.Add($"the barrier drive did not start: {bar.Setup}");
            else
            {
                if (bar.EdgeGap > ReachEdge) why.Add($"the creature never came within {ReachEdge:0} m of the barrier's edge (closest {bar.EdgeGap:0.00} m), so the hold was never tested");
                if (bar.MaxDepth > HoldSlack) why.Add($"the creature was seen {bar.MaxDepth:0.00} m INSIDE the barrier (allowed {HoldSlack:0.0} m)");
                if (bar.EndDepth > InsideEps) why.Add($"the creature ended {bar.EndDepth:0.00} m inside the barrier");
                if (!float.IsNaN(modOutside) && modOutside <= 0f && bar.EndDepth > InsideEps) why.Add("the mod's own ground says the last position is inside as well");
            }
            Report.Check("P30a-hold",
                $"a hostile {HostilePrefab} hunting TSPlain at the centre is held outside the barrier for {bar.Seconds:0} s (deepest {bar.MaxDepth:0.00} m, ended {bar.EndDepth:0.00} m, closest to the edge {bar.EdgeGap:0.00} m, drive {bar.Mode}); " +
                $"the same hunt into the plain no-monster control got it {ctl.MaxDepth:0.0} m inside",
                why.Count == 0, string.Join("; ", why));
        }

        // ── P30c: the barrier flared where it pushed, the control did not ────────────────────────────────

        private static IEnumerator FlareStep()
        {
            if (!Features.HasFlareCounter)
            {
                Report.Fail("P30c-flare", "BarrierFlare.Requested counted the barrier's flares during the hold", "this DLL has no BarrierFlare.Requested (static int) to read");
                yield break;
            }
            // The control half: no flare in the control territory - unless a wild hostile creature was leaning on a barrier at the time
            // (the counter is global), in which case that half cannot be measured and the check says so.
            bool controlClean = _flareControl == 0;
            bool controlUnmeasurable = _flareControl > 0 && _controlWild.Length > 0;
            Report.Check("P30c-flare",
                $"BarrierFlare.Requested rose by {_flareBarrier} while the creature pushed at the barrier (P30a) and by {_flareControl} in the control territory" +
                (controlUnmeasurable ? $" (those {_flareControl} came while a wild hostile creature stood at a barrier [{_controlWild}], so the control half is not measurable)" : ""),
                _flareBarrier >= 1 && (controlClean || controlUnmeasurable),
                $"flares requested during the barrier hunt {_flareBarrier} (wanted at least 1), during the control hunt {_flareControl} (wanted 0, no wild creature was at a barrier); counter now {Features.FlareRequested}");
            yield break;
        }

        // ── P30h: with a graphics device the flares the pushes asked for were actually put on screen ─────

        private static IEnumerator DrawnStep()
        {
            var why = new List<string>();
            if (Features.FlareDrawn < 0) why.Add("this DLL has no BarrierFlare.Drawn (static int) to read");
            else
            {
                if (_drawnBarrier < 1) why.Add($"BarrierFlare.Drawn rose by {_drawnBarrier} while the creature pushed at the barrier (wanted at least 1; {_flareBarrier} were requested)");
                if (_drawnControl != 0) why.Add($"BarrierFlare.Drawn rose by {_drawnControl} in the control territory (wanted 0)");
                if (_drawnVisitor < 1) why.Add($"BarrierFlare.Drawn rose by {_drawnVisitor} while TSPlain pressed at the visitor barrier (wanted at least 1; {_flareVisitor} were requested)");
            }
            Report.Check("P30h-flare-drawn",
                $"BarrierFlare.Drawn (flares actually shown, graphics device {SystemInfo.graphicsDeviceType}) rose by {_drawnBarrier} of {_flareBarrier} requested at the barrier, {_drawnControl} in the control, {_drawnVisitor} of {_flareVisitor} for the held visitor",
                why.Count == 0, string.Join("; ", why));
            yield break;
        }

        // ── P30b: a creature inside is moved out ────────────────────────────────────────────────────────

        private static IEnumerator PushOutStep()
        {
            Territory t = _barrier;
            var centre = new Vector3(t.X, 0f, t.Z);
            yield return Arrive(centre, 0.6f);
            var tr = new Trace { Label = $"placed inside '{t.Name}'" };
            GameObject go = SpawnCreature(HostilePrefab, centre + new Vector3(3f, 0f, 0f), Vector3.forward);
            if (go == null)
            {
                Report.Fail("P30b-pushout", "a hostile creature placed inside the barrier ends outside within about 2 s", $"no '{HostilePrefab}' prefab");
                yield break;
            }
            Character ch = go.GetComponent<Character>();
            ZNetView nv = go.GetComponent<ZNetView>();
            bool owned = nv != null && nv.IsValid() && nv.IsOwner();
            tr.Ready = ch != null && owned;
            tr.Setup = $"{go.name} put 3 m from the centre, owned by this client: {owned}, tamed: {ch != null && ch.IsTamed()}";
            float t0 = Now;
            while (Now - t0 < 7f && go != null && ch != null && !ch.IsDead())
            {
                float depth = DepthOf(t, go.transform, out Vector3 at);
                tr.Add(Now - t0, at, depth);
                yield return null;
            }
            float modOutside = Features.DistanceOutside(t, tr.Last);
            Report.Info($"P30b evidence - {tr}; first outside {When(tr.FirstOutsideAt)}, deepest afterwards {Deep(tr.MaxDepthAfterOut)}; TerritoryGround.DistanceOutside at the end {modOutside:0.00}");
            var why = new List<string>();
            if (!tr.Ready) why.Add("the creature was not owned by this client: " + tr.Setup);
            if (tr.FirstOutsideAt < 0f) why.Add("it never got outside");
            else if (tr.FirstOutsideAt > PushOutSeconds) why.Add($"it got outside after {tr.FirstOutsideAt:0.00} s (allowed {PushOutSeconds:0.0} s)");
            if (tr.FirstOutsideAt >= 0f && tr.MaxDepthAfterOut > HoldSlack) why.Add($"it went back {tr.MaxDepthAfterOut:0.00} m inside afterwards");
            if (tr.EndDepth > InsideEps) why.Add($"it ended {tr.EndDepth:0.00} m inside");
            Report.Check("P30b-pushout",
                $"a hostile {HostilePrefab} placed 3 m inside the barrier is outside {When(tr.FirstOutsideAt)} after it was put there (within about 2 s) and stays out for the rest of {tr.Seconds:0} s (ended {tr.EndDepth:0.00} m inside)",
                why.Count == 0, string.Join("; ", why));
        }

        // ── P30d: tamed and passive creatures pass ──────────────────────────────────────────────────────

        private static IEnumerator PassTamedStep()
        {
            Territory t = _barrier;
            var centre = new Vector3(t.X, 0f, t.Z);
            yield return Arrive(centre, 0.6f);
            var tr = new Trace { Label = $"tamed inside '{t.Name}'" };
            GameObject go = SpawnCreature(HostilePrefab, centre + new Vector3(0f, 0f, 2f), Vector3.forward);
            if (go == null)
            {
                Report.Soft("P30d-pass-tamed", "a tamed creature can be inside the barrier", $"no '{HostilePrefab}' prefab to tame");
                yield break;
            }
            Character ch = go.GetComponent<Character>();
            BaseAI ai = go.GetComponent<BaseAI>();
            ZNetView nv = go.GetComponent<ZNetView>();
            // Tamed in the very frame it is created, before any Update: the barrier holds a hostile creature it finds inside
            // (the first run of this check tamed it 3 frames later and found it already put out, 0.8 m past the edge).
            bool owned = nv != null && nv.IsValid() && nv.IsOwner();
            if (ch != null && owned)
            {
                ch.SetTamed(true);                              // the game's own call: an RPC to the owner, which is this client
                if (!ch.IsTamed()) ch.RPC_SetTamed(0L, true);   // (if the routed RPC were not answered at once, do what it does)
                ai?.SetHuntPlayer(false);
            }
            yield return Flow.Frames(3);
            bool tamed = ch != null && ch.IsTamed();
            tr.Ready = ch != null && owned && tamed;
            tr.Setup = $"{go.name} 2 m from the centre, owned: {owned}, IsTamed: {tamed}, faction {ch?.m_faction}";
            float t0 = Now;
            while (Now - t0 < 6f && go != null && ch != null && !ch.IsDead())
            {
                float depth = DepthOf(t, go.transform, out Vector3 at);
                tr.Add(Now - t0, at, depth);
                yield return null;
            }
            Report.Info($"P30d evidence - {tr}; least depth {tr.MinDepth:0.00} m");
            if (!tr.Ready)
            {
                Report.Fail("P30d-pass-tamed", "a tamed creature stays inside the barrier", $"the fixture did not work: {tr.Setup}");
                yield break;
            }
            Report.Check("P30d-pass-tamed",
                $"a tamed {HostilePrefab} (Character.SetTamed, IsTamed true) put 2 m from the barrier's centre stays inside for {tr.Seconds:0} s (never less than {tr.MinDepth:0.00} m inside, ended {tr.EndDepth:0.00} m)",
                tr.Frames > 0 && tr.MinDepth > 0.3f, $"it was {tr.MinDepth:0.00} m inside at its least, ended {tr.EndDepth:0.00} m; {tr}");
        }

        private static IEnumerator PassPassiveStep()
        {
            Territory t = _barrier;
            var centre = new Vector3(t.X, 0f, t.Z);
            yield return Arrive(centre, 0.6f);
            var tr = new Trace { Label = $"passive {PassivePrefab} inside '{t.Name}'" };
            GameObject go = SpawnCreature(PassivePrefab, centre + new Vector3(2f, 0f, 0f), Vector3.forward);
            if (go == null)
            {
                Report.Soft("P30d2-pass-passive", "a passive animal can be inside the barrier", $"no '{PassivePrefab}' prefab in ZNetScene");
                yield break;
            }
            Character ch = go.GetComponent<Character>();
            ZNetView nv = go.GetComponent<ZNetView>();
            yield return Flow.Frames(3);
            bool owned = nv != null && nv.IsValid() && nv.IsOwner();
            // A deer flees from a player: the player goes well away (30 m beyond the edge), or it would just run out by itself.
            Vector3 away = OutsidePoint(t, 30f, out string note);
            yield return Arrive(away, 0.3f);
            tr.Ready = ch != null && owned;
            tr.Setup = $"{go.name} ({go.GetComponent<BaseAI>()?.GetType().Name}) 2 m from the centre, owned: {owned}, hostile by the mod: {Hostility.IsHostile(ZNetScene.instance.GetPrefab(PassivePrefab))}, player {World.F(Vector2.Distance(new Vector2(World.Me.transform.position.x, World.Me.transform.position.z), new Vector2(centre.x, centre.z)))} m away ({note})";
            float t0 = Now;
            while (Now - t0 < 6f && go != null && ch != null && !ch.IsDead())
            {
                float depth = DepthOf(t, go.transform, out Vector3 at);
                tr.Add(Now - t0, at, depth);
                yield return null;
            }
            Report.Info($"P30d2 evidence - {tr}; least depth {tr.MinDepth:0.00} m");
            if (!tr.Ready || tr.Frames == 0)
                Report.Soft("P30d2-pass-passive", $"a passive {PassivePrefab} stays inside the barrier", $"the fixture did not work headless: {tr.Setup}");
            else if (tr.MinDepth > 0.3f)
                Report.Pass("P30d2-pass-passive", $"a passive {PassivePrefab} put 2 m from the barrier's centre stays inside for {tr.Seconds:0} s (never less than {tr.MinDepth:0.00} m inside)");
            else
                Report.Soft("P30d2-pass-passive", $"a passive {PassivePrefab} stays inside the barrier",
                    $"it was outside at some point (least depth {tr.MinDepth:0.00} m): a wandering deer cannot be told from a pushed one; {tr}");
        }

        // ── P30e / P30g / P30f / P30e2: the player, held or free ────────────────────────────────────────

        /// <summary>
        /// The player walks at the territory from 7 m beyond its edge in 0.3 m steps every 0.1 s (3 m/s), each step a
        /// Rigidbody placement from wherever the game LEFT the player after the last one, and is sampled at the end of
        /// every wait, so a hold that puts the player back shows and a step of ours that has not yet been answered
        /// does not. Stops early at <paramref name="stopDepth"/> metres inside.
        /// </summary>
        private static IEnumerator WalkIn(Territory t, Trace tr, int maxSteps, float stopDepth)
        {
            var c = new Vector3(t.X, 0f, t.Z);
            Vector3 start = OutsidePoint(t, 7f, out string note);
            yield return Arrive(start, 0.6f);
            tr.Ready = World.Me != null;
            tr.Setup = $"walked from {World.P(World.Me.transform.position)} ({note}) at '{t.Name}' in 0.3 m steps every 0.1 s";
            float t0 = Now;
            for (int i = 0; i < maxSteps; i++)
            {
                Vector3 cur = World.Me.transform.position;
                Vector3 d = c - new Vector3(cur.x, 0f, cur.z);
                d.y = 0f;
                if (d.magnitude < 0.5f) break;
                World.MoveTo(cur + d.normalized * 0.3f);
                yield return Flow.Seconds(0.1f);
                float depth = DepthOf(t, World.Me, out Vector3 at);
                tr.Add(Now - t0, at, depth);
                if (stopDepth > 0f && depth >= stopDepth) break;
            }
        }

        private static IEnumerator VisitorHeldStep()
        {
            _walkHeld = new Trace { Label = $"TSPlain walking at the guard '{_guard.Name}'" };
            int f0 = Features.FlareRequested, d0 = Features.FlareDrawn;
            float tWalk = Now;
            yield return WalkIn(_guard, _walkHeld, 70, 0f);
            _flareVisitor = Features.FlareRequested - f0;
            _drawnVisitor = Features.FlareDrawn - d0;
            Trace tr = _walkHeld;
            Report.Info($"P30e evidence - {tr}; TSPlain member of the guard: {_guard.IsMember(World.Me.GetPlayerID())}; TerritoryGround.DistanceOutside at the end {Features.DistanceOutside(_guard, tr.Last):0.00}; flares requested {_flareVisitor}; " +
                        $"centre messages during the walk [{string.Join(" || ", World.CenterSince(tWalk))}]; BarrierFlare.Drawn {(Features.FlareDrawn)} (0 headless is expected)");
            var why = new List<string>();
            if (_guard.IsMember(World.Me.GetPlayerID())) why.Add("TSPlain is already a member of the guard, so nothing is being held");
            if (tr.EdgeGap > ReachEdge) why.Add($"the walk never came within {ReachEdge:0} m of the edge (closest {tr.EdgeGap:0.00} m), so the hold was never tested");
            if (tr.MaxDepth > HoldSlack) why.Add($"TSPlain was seen {tr.MaxDepth:0.00} m INSIDE the guard (allowed {HoldSlack:0.0} m)");
            if (tr.EndDepth > InsideEps) why.Add($"TSPlain ended {tr.EndDepth:0.00} m inside the guard");
            Report.Check("P30e-visitor-held",
                $"TSPlain, a non-member, pressing at the visitor barrier '{_guard.Name}' for {tr.Seconds:0} s in 0.3 m Rigidbody steps stays outside (deepest {tr.MaxDepth:0.00} m, ended {tr.EndDepth:0.00} m, closest to the edge {tr.EdgeGap:0.00} m)",
                why.Count == 0, string.Join("; ", why));
        }

        private static IEnumerator VisitorFlareStep()
        {
            if (!Features.HasFlareCounter)
            {
                Report.Fail("P30g-visitor-flare", "BarrierFlare.Requested counted the barrier's flares for the held visitor", "this DLL has no BarrierFlare.Requested (static int) to read");
                yield break;
            }
            Report.Check("P30g-visitor-flare",
                $"BarrierFlare.Requested rose by {_flareVisitor} while TSPlain pressed at the visitor barrier",
                _flareVisitor >= 1, $"flares requested during the walk {_flareVisitor} (wanted at least 1); counter now {Features.FlareRequested}");
            yield break;
        }

        private static IEnumerator VisitorPushOutStep()
        {
            Territory t = _guard;
            var c = new Vector3(t.X, 0f, t.Z);
            Vector3 start = OutsidePoint(t, 7f, out string note);
            yield return Arrive(start, 0.5f);
            var tr = new Trace { Label = $"TSPlain teleported into the guard '{t.Name}'", Ready = true };
            tr.Setup = $"placed at the centre from {World.P(World.Me.transform.position)}";
            World.MoveTo(c);
            float t0 = Now;
            while (Now - t0 < 6f)
            {
                float depth = DepthOf(t, World.Me, out Vector3 at);
                tr.Add(Now - t0, at, depth);
                yield return null;
            }
            // What the player reads when they are moved out: the barrier says so (Rules/Barrier.cs Say, always on an eviction).
            string told = World.CenterSince(t0).FirstOrDefault(m => m.Contains("turn you back")) ?? "";
            Report.Info($"P30f evidence - {tr}; first outside {When(tr.FirstOutsideAt)}, deepest afterwards {Deep(tr.MaxDepthAfterOut)}; TerritoryGround.DistanceOutside at the end {Features.DistanceOutside(t, tr.Last):0.00}; " +
                        $"centre messages [{string.Join(" || ", World.CenterSince(t0))}]");
            var why = new List<string>();
            if (told != $"The runes of {t.Name} turn you back") why.Add($"the player was not told 'The runes of {t.Name} turn you back' (centre messages: [{string.Join(" || ", World.CenterSince(t0))}])");
            if (tr.FirstOutsideAt < 0f) why.Add("TSPlain never got outside");
            else if (tr.FirstOutsideAt > PushOutSeconds) why.Add($"outside only after {tr.FirstOutsideAt:0.00} s (allowed {PushOutSeconds:0.0} s)");
            if (tr.FirstOutsideAt >= 0f && tr.MaxDepthAfterOut > HoldSlack) why.Add($"back {tr.MaxDepthAfterOut:0.00} m inside afterwards");
            if (tr.EndDepth > InsideEps) why.Add($"ended {tr.EndDepth:0.00} m inside");
            Report.Check("P30f-visitor-pushout",
                $"TSPlain, a non-member, placed at the centre of the visitor barrier is outside {When(tr.FirstOutsideAt)} after it was put there (within about 2 s) and stays out (ended {tr.EndDepth:0.00} m inside), with the centre message 'The runes of {t.Name} turn you back'",
                why.Count == 0, string.Join("; ", why));
        }

        private static IEnumerator OpenBarrierStep()
        {
            Territory t = _barrier;
            var tr = new Trace { Label = $"TSPlain walking into the plain barrier '{t.Name}'" };
            yield return WalkIn(t, tr, 70, 5f);
            // and stays: nothing pushes a player out of a barrier that does not keep visitors out
            var c = new Vector3(t.X, 0f, t.Z);
            float t0 = Now;
            while (Now - t0 < 2f)
            {
                float depth = DepthOf(t, World.Me, out Vector3 at);
                tr.Add(tr.Seconds + Time.unscaledDeltaTime, at, depth);
                yield return null;
            }
            Report.Info($"P30e2 evidence - {tr}; TerritoryGround.DistanceOutside at the end {Features.DistanceOutside(t, tr.Last):0.00}");
            Report.Check("P30e2-open-barrier-free",
                $"TSPlain walks into the plain barrier '{t.Name}' (no visitor option) and stays inside: deepest {tr.MaxDepth:0.00} m, ended {tr.EndDepth:0.00} m inside",
                tr.MaxDepth >= ControlDepth && tr.EndDepth >= 3f, $"deepest {tr.MaxDepth:0.00} m (wanted {ControlDepth:0} m), ended {tr.EndDepth:0.00} m inside (wanted 3 m); {tr}");
        }

        // ─────────────────────────────────────────────────────────────
        // TSPlain, phase 3: a member walks in
        // ─────────────────────────────────────────────────────────────

        internal static IEnumerator PlainMember()
        {
            if (!Features.Barrier) yield break;
            yield return Step("P30e3-member-passes", "member passes", MemberStep());
        }

        private static IEnumerator MemberStep()
        {
            JObject m = null;
            var b = new Box<JObject>();
            yield return RunDir.Wait("admin.barrier-member", 300f, b);
            m = b.Value;
            if (m == null || m["ok"] == null || !(bool)m["ok"])
            {
                Report.Fail("P30e3-member-passes", "TSPlain, made a member by the admin, walks into the visitor barrier", "the admin's member add did not complete (" + (m == null ? "admin.barrier-member never signalled" : "see A31") + ")");
                yield break;
            }
            long me = World.Me.GetPlayerID();
            var ok = new Box<bool>();
            yield return Flow.Until(() => TerritoryClient.HasSnapshot && World.ByName(Layout.GuardName)?.IsMember(me) == true, T, ok);
            Territory guard = World.ByName(Layout.GuardName);
            if (!ok.Value || guard == null)
            {
                Report.Fail("P30e3-member-passes", "TSPlain, made a member by the admin, walks into the visitor barrier", $"TSPlain ({me}) is not a member of the guard in its own index after {T:0} s: {World.Describe(guard)}");
                yield break;
            }
            var tr = new Trace { Label = $"member TSPlain walking into the guard '{guard.Name}'" };
            yield return WalkIn(guard, tr, 70, 5f);
            float t0 = Now;
            while (Now - t0 < 2f)
            {
                float depth = DepthOf(guard, World.Me, out Vector3 at);
                tr.Add(tr.Seconds + Time.unscaledDeltaTime, at, depth);
                yield return null;
            }
            Report.Info($"P30e3 evidence - {tr}; TerritoryGround.DistanceOutside at the end {Features.DistanceOutside(guard, tr.Last):0.00}");
            Report.Check("P30e3-member-passes",
                $"TSPlain, now a member of the visitor barrier '{guard.Name}', walks in the same way that held it out before and stays inside: deepest {tr.MaxDepth:0.00} m, ended {tr.EndDepth:0.00} m inside",
                tr.MaxDepth >= ControlDepth && tr.EndDepth >= 3f, $"deepest {tr.MaxDepth:0.00} m (wanted {ControlDepth:0} m), ended {tr.EndDepth:0.00} m inside (wanted 3 m); {tr}");
        }
    }
}
