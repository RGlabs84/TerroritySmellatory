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
    /// TSAdmin: a real admin as the SERVER decides it (adminlist.txt, by the connection). It drives the mod the way
    /// the hammer and the panel do: the panel's draft (TerritoryPanel.NewDraft) is set as the panel's controls set
    /// it, and the ground is marked with the exact terr_place / terr_remove_at / terr_edit_at lines Infinity
    /// Hammer's tools run. Every assertion reads what came BACK from the server (the snapshot in TerritoryIndex).
    /// </summary>
    internal static class ScenarioAdmin
    {
        internal static Vector3 Origin;
        internal static Dictionary<string, Vector3> Centres = new Dictionary<string, Vector3>();
        internal static readonly Dictionary<string, int> Ids = new Dictionary<string, int>();

        private static float T => E2EEnv.StepTimeout;

        // ─────────────────────────────────────────────────────────────
        // PHASE 1: snapshot, create three territories, the admin's own view, one rules edit
        // ─────────────────────────────────────────────────────────────

        internal static IEnumerator Phase1()
        {
            var ok = new Box<bool>();
            yield return Flow.Until(() => TerritoryClient.HasSnapshot, T, ok);
            Report.Check("A01-snapshot-admin", "admin's snapshot arrived and the SERVER says IsAdmin=true, AdminsBypass=true",
                ok.Value && TerritoryClient.IsAdmin && TerritoryClient.AdminsBypass,
                $"hasSnapshot={TerritoryClient.HasSnapshot} isAdmin={TerritoryClient.IsAdmin} bypass={TerritoryClient.AdminsBypass}");

            Player me = World.Me;
            Origin = me.transform.position;
            RunDir.Signal("admin.ready", new { id = me.GetPlayerID(), name = me.GetPlayerName(), x = Origin.x, y = Origin.y, z = Origin.z });

            if (!E2EEnv.Solo)
            {
                var plain = new Box<JObject>();
                yield return RunDir.Wait("plain.ready", 300f, plain);
                long plainId = plain.Value != null ? (long)plain.Value["id"] : 0L;
                yield return Flow.Until(() => TerritoryClient.OnlinePlayers.Any(m => m.Name == E2EEnv.PlainName), 30f, ok);
                TerritoryMember listed = TerritoryClient.OnlinePlayers.FirstOrDefault(m => m.Name == E2EEnv.PlainName);
                Report.Check("A02-roster", "the server's online roster (admins only) lists TSPlain with its real player id",
                    listed != null && listed.Id == plainId && plainId != 0L,
                    $"roster=[{string.Join(", ", TerritoryClient.OnlinePlayers.Select(m => m.Name + ":" + m.Id))}] plain.ready id={plainId}");
            }

            Centres = Layout.Place(Origin, out float rotation);
            var layout = new JObject { ["rotation"] = rotation, ["origin"] = Vec(Origin) };
            foreach (KeyValuePair<string, Vector3> kv in Centres) layout[kv.Key] = Vec(kv.Value);
            RunDir.Signal("layout", layout);
            Report.Info($"layout around {World.P(Origin)} rotated {rotation} deg: " + string.Join(", ", Centres.Select(kv => $"{kv.Key} {World.P(kv.Value)}")));

            yield return Flow.Step("A03-create-circle", "create", Create(Layout.Circle, "A03-create-circle"));
            yield return Flow.Step("A04-create-square", "create", Create(Layout.Square, "A04-create-square"));
            yield return Flow.Step("A05-create-rect", "create", Create(Layout.Rect, "A05-create-rect"));
            RunDir.Signal("admin.created", new JObject(Ids.Select(kv => new JProperty(kv.Key, kv.Value))));

            yield return Flow.Step("A06-bypass", "admin bypass", AdminBypass());
            yield return Flow.Step("A07-update-rules", "rules update", UpdateRules());
            RunDir.Signal("admin.phase1-done");
        }

        private static JObject Vec(Vector3 v) => new JObject { ["x"] = v.x, ["y"] = v.y, ["z"] = v.z };

        /// <summary>The panel's draft, set as its controls set it, then the hammer's Territory tool.</summary>
        internal static void SetDraft(Spec s)
        {
            Territory d = TerritoryPanel.NewDraft;
            d.Name = s.Name;
            d.Color = s.Color;
            d.Flags = s.Flags;
            d.PvP = s.PvP;
            d.Priority = 0;
            d.HealthRegen = s.HealthRegen;
            d.StaminaRegen = s.StaminaRegen;
            d.EitrRegen = s.EitrRegen;
            d.CarryBonus = s.CarryBonus;
            d.ArmorBonus = s.ArmorBonus;
            d.EnterMessage = s.EnterMessage;
            d.LeaveMessage = s.LeaveMessage;
            d.OwnerId = 0L;
            d.OwnerName = "";
            d.Members.Clear();
            // 0.4.0: a new draft starts INSIDE whatever it is marked in and follows it. Every territory these phases mark is top level
            // with all of its settings given (as the panel's Inside row set to Nothing makes it); the sub-territory phase sets its own.
            Scenario040.TopLevel(d);
        }

        private static IEnumerator Create(Spec s, string id)
        {
            SetDraft(s);

            Vector3 c = Centres[s.Key];
            float before = TerritoryClient.LastResultAt;
            World.Run(IhCommand.Place(IhCommand.ShapeOf(s.Kind), c, s.HalfX, s.HalfZ, s.Yaw));

            var ok = new Box<bool>();
            yield return Flow.Until(() => World.ByName(s.Name) != null && TerritoryClient.LastResultAt > before, T, ok);
            Territory t = World.ByName(s.Name);
            if (!Report.Check(id, $"'{s.Name}' created through terr_place and back in the admin's snapshot: {World.Describe(t)}",
                    ok.Value && t != null && TerritoryClient.LastResultOk && TerritoryClient.LastResult.StartsWith("Created #"),
                    $"result ok={TerritoryClient.LastResultOk} '{TerritoryClient.LastResult}', in index: {t != null}"))
                yield break;
            Ids[s.Key] = t.Id;

            var why = new List<string>();
            if (t.Kind != s.Kind) why.Add($"kind {t.Kind} != {s.Kind}");
            if (Mathf.Abs(t.X - c.x) > 0.05f || Mathf.Abs(t.Z - c.z) > 0.05f) why.Add($"centre ({t.X},{t.Z}) != ({c.x},{c.z})");
            if (!Mathf.Approximately(t.HalfX, s.HalfX) || !Mathf.Approximately(t.HalfZ, s.Kind == TerritoryShapeKind.Rectangle ? s.HalfZ : s.HalfX)) why.Add($"half ({t.HalfX},{t.HalfZ})");
            if (s.Kind != TerritoryShapeKind.Circle && Mathf.Abs(Mathf.DeltaAngle(t.Yaw, s.Yaw)) > 0.01f) why.Add($"yaw {t.Yaw} != {s.Yaw}");
            if (t.Flags != s.Flags) why.Add($"flags {t.Flags} != {s.Flags}");
            if (t.PvP != s.PvP) why.Add($"pvp {t.PvP} != {s.PvP}");
            if (!Mathf.Approximately(t.HealthRegen, s.HealthRegen) || !Mathf.Approximately(t.StaminaRegen, s.StaminaRegen) ||
                !Mathf.Approximately(t.CarryBonus, s.CarryBonus) || !Mathf.Approximately(t.ArmorBonus, s.ArmorBonus)) why.Add("sanctuary values");
            if (t.EnterMessage != s.EnterMessage) why.Add($"enter message '{t.EnterMessage}'");
            if (!t.CreatedBy.StartsWith(E2EEnv.AdminName)) why.Add($"createdBy '{t.CreatedBy}'");
            if (t.Revision != 1) why.Add($"revision {t.Revision}");
            Report.Check(id + "-rules", $"'{s.Name}' carries exactly the draft's shape and rules (server-sanitised)", why.Count == 0, string.Join("; ", why));
        }

        /// <summary>AdminsBypassProtection is on and TSAdmin is in no territory's member list: protections must not stop it.</summary>
        private static IEnumerator AdminBypass()
        {
            Territory circle = World.ByName(Layout.CircleName);
            Territory square = World.ByName(Layout.SquareName);
            if (circle == null || square == null)
            {
                Report.Fail("A06-bypass", "admin bypass checks", "circle/square missing from the index");
                yield break;
            }
            long me = World.Me.GetPlayerID();
            Report.Info($"TSAdmin (id {me}) member of circle: {circle.IsMember(me)}, Bypass={TerritoryClient.Bypass}");
            Vector3 inCircle = World.Local(circle, 2f, 1f);
            Report.Check("A06a-bypass-access", "admin (not a member) passes the ward access check inside the protected circle",
                World.CheckAccess(inCircle, build: false), "PrivateArea.CheckAccess answered no");
            Report.Check("A06b-bypass-build", "admin (not a member) passes the ward BUILD check inside the protected circle (BuildScope)",
                World.CheckAccess(inCircle, build: true), "PrivateArea.CheckAccess (build) answered no");

            yield return Fixtures.PickTest("A06c-bypass-harvest", "admin (not a member) can pick a pickable inside the harvest-protected circle",
                World.Local(circle, -3f, 2f), expectPicked: true);

            // StructureProtect stops EVERY hit, the admin's included (the 0.1.1 fix); a control piece outside shows the hit lands.
            yield return Fixtures.DamageTest("A06d-structure-admin", "an admin's hit on a piece inside the StructureProtect square does no damage",
                World.Local(square, 2f, -2f), expectDamage: false);
            yield return Fixtures.DamageTest("A06e-structure-control", "control: the same hit on a piece in open country does damage",
                Fixtures.OpenGround(), expectDamage: true);
        }

        /// <summary>Territory edit tool (terr_edit_at) on the rectangle, change its rules in the panel, Save changes.</summary>
        private static IEnumerator UpdateRules()
        {
            Territory rect = World.ByName(Layout.RectName);
            if (rect == null)
            {
                Report.Fail("A07-update-rules", "rules update", "rect missing");
                yield break;
            }
            World.Run(IhCommand.Edit(new Vector3(rect.X, rect.Y, rect.Z)));
            yield return Flow.Frames(2);
            Territory editing = TerritoryPanel._editing;
            if (!Report.Check("A07a-edit-tool", $"terr_edit_at opened the panel on '{rect.Name}' (#{rect.Id})",
                    TerritoryPanel.IsOpen && editing != null && editing.Id == rect.Id, $"open={TerritoryPanel.IsOpen} editing={editing?.Id}"))
                yield break;

            // The Rules tab's controls: tick Harvesting, raise Priority to 5.
            editing.Set(TerritoryFlags.HarvestProtect, true);
            editing.Priority = 5;
            int rev = rect.Revision;
            float before = TerritoryClient.LastResultAt;
            TerritoryClient.Send(RequestOp.Update, editing.Clone(), editing.Id);   // what "Save changes" sends
            TerritoryPanel.Close();

            var ok = new Box<bool>();
            yield return Flow.Until(() => TerritoryIndex.Find(rect.Id)?.Revision > rev && TerritoryClient.LastResultAt > before, T, ok);
            Territory now = TerritoryIndex.Find(rect.Id);
            Report.Check("A07b-update-rules", $"Update saved on the server and came back: {World.Describe(now)}",
                ok.Value && now != null && TerritoryClient.LastResultOk && now.Has(TerritoryFlags.HarvestProtect) && now.Priority == 5 &&
                now.Revision == rev + 1 && Mathf.Approximately(now.X, rect.X) && Mathf.Approximately(now.HalfX, rect.HalfX),
                $"result '{TerritoryClient.LastResult}' now={World.Describe(now)}");
        }

        // ─────────────────────────────────────────────────────────────
        // PHASE 2: add TSPlain as a member, reshape, remove
        // ─────────────────────────────────────────────────────────────

        internal static IEnumerator Phase2()
        {
            if (!E2EEnv.Solo)
            {
                var b = new Box<JObject>();
                yield return RunDir.Wait("plain.visitor-done", 600f, b);
            }
            if (TerritoryIndex.All.Count == 0)
            {
                var ok0 = new Box<bool>();
                yield return Flow.Until(() => TerritoryClient.HasSnapshot && TerritoryIndex.All.Count > 0, T, ok0);
            }
            if (Centres.Count == 0) LoadLayout();

            yield return Flow.Step("A08-member", "add member", AddMember());
            RunDir.Signal("admin.t1-updated");
            if (!E2EEnv.Solo)
            {
                var b = new Box<JObject>();
                yield return RunDir.Wait("plain.member-done", 300f, b);
            }

            yield return Flow.Step("A09-reshape", "reshape", Reshape());
            RunDir.Signal("admin.t3-reshaped");
            if (!E2EEnv.Solo)
            {
                var b = new Box<JObject>();
                yield return RunDir.Wait("plain.reshape-done", 300f, b);
            }

            yield return Flow.Step("A10-remove", "remove", Remove());
            RunDir.Signal("admin.t2-removed");
            // 0.2.0 only (Scenario020 says so once, as INFO, on a DLL without combined territories): the two extra
            // territories, the hammer's combine tool, and the barrier the plain client and the server wait for.
            yield return Flow.Step("A20-combine", "combine", Scenario020.AdminPart());
            // 0.3.0 only (Scenario030 says so once, as INFO, on a DLL without the runic barrier): the barrier, the guard and
            // the control, then phase 3 (the admin's member add) once the plain client has been held out of the guard.
            yield return Flow.Step("A30-barriers", "barriers", Scenario030.AdminPart());
            // 0.3.1 only (Scenario031 says so once, as INFO, on a DLL without AutoCloseDoors): the doors territory, the nested one and the one without the flag.
            yield return Flow.Step("A32-doors", "doors", Scenario031.AdminPart());
            // 0.4.0 only (Scenario040 says so once, as INFO, on a DLL without sub-territories): a land, a town inside it that follows it and sets two
            // things itself, and an edit of the land that the town follows.
            yield return Flow.Step("A40-sub", "sub-territories", Scenario040.AdminPart());
            if (!E2EEnv.Solo)
            {
                var b = new Box<JObject>();
                yield return RunDir.Wait(Features.Barrier ? "plain.held-done" : "plain.final-done", 300f, b);
            }
        }

        /// <summary>0.3.0 only: TSAdmin makes TSPlain a member of the visitor barrier (the panel's Add member + Save changes).</summary>
        internal static IEnumerator Phase3()
        {
            if (TerritoryIndex.All.Count == 0)
            {
                var ok0 = new Box<bool>();
                yield return Flow.Until(() => TerritoryClient.HasSnapshot && TerritoryIndex.All.Count > 0, T, ok0);
            }
            if (Centres.Count == 0) LoadLayout();
            yield return Flow.Step("A31-guard-member", "guard member", Scenario030.AdminMember());
            // 0.4.0 only: the town follows building again, the land is deleted (the town keeps its rules and moves up), a pair is deleted in one request.
            yield return Flow.Step("A40-later", "sub-territories, later", Scenario040.AdminLater());
            if (!E2EEnv.Solo)
            {
                var b = new Box<JObject>();
                yield return RunDir.Wait("plain.final-done", 300f, b);
            }
        }

        private static void LoadLayout()
        {
            JObject l = RunDir.Read("layout");
            if (l == null) return;
            foreach (JProperty p in l.Properties())
                if (p.Value is JObject v && v["x"] != null)
                    Centres[p.Name] = new Vector3((float)v["x"], (float)v["y"], (float)v["z"]);
            if (l["origin"] is JObject o) Origin = new Vector3((float)o["x"], (float)o["y"], (float)o["z"]);
        }

        /// <summary>Territory edit on the circle; Members tab: Add (online roster) or Add by id (offline); Save changes.</summary>
        private static IEnumerator AddMember()
        {
            Territory circle = World.ByName(Layout.CircleName);
            JObject plain = RunDir.Read("plain.ready");
            long plainId = plain != null ? (long)plain["id"] : 0L;
            if (circle == null || plainId == 0L)
            {
                Report.Fail("A08-member", "add TSPlain as a member", $"circle={circle != null} plainId={plainId}");
                yield break;
            }

            World.Run(IhCommand.Edit(new Vector3(circle.X, circle.Y, circle.Z)));
            yield return Flow.Frames(2);
            Territory editing = TerritoryPanel._editing;
            if (!Report.Check("A08a-edit-tool", $"terr_edit_at opened the panel on '{circle.Name}' (#{circle.Id})",
                    TerritoryPanel.IsOpen && editing != null && editing.Id == circle.Id, $"open={TerritoryPanel.IsOpen} editing={editing?.Id}"))
                yield break;

            TerritoryMember online = TerritoryClient.OnlinePlayers.FirstOrDefault(m => m.Name == E2EEnv.PlainName);
            if (online != null)
            {
                Report.Info($"adding TSPlain from the server's online roster ({online.Id}) - the Members tab's Add");
                TerritoryPanel.AddMember(online.Id, online.Name);
            }
            else
            {
                Report.Info($"TSPlain is offline; adding by id {plainId} - the Members tab's 'Add by id'");
                TerritoryPanel.AddMember(plainId, E2EEnv.PlainName);
            }
            editing.EnterMessage = "Welcome back to {name}";
            int rev = circle.Revision;
            float before = TerritoryClient.LastResultAt;
            TerritoryClient.Send(RequestOp.Update, editing.Clone(), editing.Id);
            TerritoryPanel.Close();

            var ok = new Box<bool>();
            yield return Flow.Until(() => TerritoryIndex.Find(circle.Id)?.Revision > rev && TerritoryClient.LastResultAt > before, T, ok);
            Territory now = TerritoryIndex.Find(circle.Id);
            Report.Check("A08b-member", $"Update added TSPlain ({plainId}) as a member and changed the banner: {World.Describe(now)}",
                ok.Value && now != null && TerritoryClient.LastResultOk && now.IsMember(plainId) && now.EnterMessage == "Welcome back to {name}" &&
                now.Flags == circle.Flags && Mathf.Approximately(now.HalfX, circle.HalfX),
                $"result '{TerritoryClient.LastResult}' now={World.Describe(now)}");
        }

        /// <summary>Panel "Move / reshape" arms the move (TerritoryPanel.ReshapeId); the hammer's Territory tool gives the new ground.</summary>
        private static IEnumerator Reshape()
        {
            Territory rect = World.ByName(Layout.RectName);
            if (rect == null || !Centres.TryGetValue("rect2", out Vector3 target))
            {
                Report.Fail("A09-reshape", "reshape the rectangle", "rect or its target missing");
                yield break;
            }
            int rev = rect.Revision;
            TerritoryFlags flags = rect.Flags;
            int priority = rect.Priority;
            TerritoryPanel.ReshapeId = rect.Id;   // what the panel's Move / reshape button arms before handing over to the hammer
            float before = TerritoryClient.LastResultAt;
            World.Run(IhCommand.Place(IhCommand.RulerShape.Rectangle, target, Layout.ReshapeHalfX, Layout.ReshapeHalfZ, Layout.ReshapeYaw));

            var ok = new Box<bool>();
            yield return Flow.Until(() => TerritoryIndex.Find(rect.Id)?.Revision > rev && TerritoryClient.LastResultAt > before, T, ok);
            Territory now = TerritoryIndex.Find(rect.Id);
            Report.Check("A09-reshape", $"Reshape moved #{rect.Id} to the new ground and kept its id and rules: {World.Describe(now)}",
                ok.Value && now != null && TerritoryClient.LastResultOk && TerritoryPanel.ReshapeId == 0 &&
                Mathf.Abs(now.X - target.x) < 0.05f && Mathf.Abs(now.Z - target.z) < 0.05f &&
                Mathf.Approximately(now.HalfX, Layout.ReshapeHalfX) && Mathf.Approximately(now.HalfZ, Layout.ReshapeHalfZ) &&
                Mathf.Abs(Mathf.DeltaAngle(now.Yaw, Layout.ReshapeYaw)) < 0.01f && now.Flags == flags && now.Priority == priority &&
                now.Name == Layout.RectName && TerritoryIndex.All.Count(t => t.Name == Layout.RectName) == 1,
                $"result '{TerritoryClient.LastResult}' armed={TerritoryPanel.ReshapeId} now={World.Describe(now)}");
        }

        /// <summary>The Territory remove tool, twice: the first use only arms, the second removes.</summary>
        private static IEnumerator Remove()
        {
            Territory square = World.ByName(Layout.SquareName);
            if (square == null)
            {
                Report.Fail("A10-remove", "remove the square", "square missing");
                yield break;
            }
            Vector3 at = World.Local(square, 1f, 1f);
            float t0 = Time.realtimeSinceStartup;
            World.Run(IhCommand.Remove(at));
            yield return Flow.Seconds(1.5f);
            bool stillThere = TerritoryIndex.Find(square.Id) != null;
            bool asked = World.ConsoleSince(t0).Any(l => l.Contains("to REMOVE it"));
            Report.Check("A10a-remove-arms", "the first terr_remove_at only arms (asks to place again) and removes nothing",
                stillThere && asked, $"stillThere={stillThere} asked={asked}");

            float before = TerritoryClient.LastResultAt;
            World.Run(IhCommand.Remove(at));
            var ok = new Box<bool>();
            yield return Flow.Until(() => TerritoryIndex.Find(square.Id) == null && TerritoryClient.LastResultAt > before, T, ok);
            Report.Check("A10b-remove", $"the second terr_remove_at within 6 s deleted #{square.Id} '{square.Name}' on the server and from the admin's index",
                ok.Value && TerritoryClient.LastResultOk && TerritoryClient.LastResult.StartsWith("Deleted #"),
                $"result '{TerritoryClient.LastResult}', still in index: {TerritoryIndex.Find(square.Id) != null}");
        }
    }
}
