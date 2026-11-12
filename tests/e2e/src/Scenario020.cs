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
    /// The 0.2.0 phase: combined territories, played after the 0.1.1 scenario has run out (admin phase 2, then plain
    /// phase 2), so none of the older checks sees an extra territory. Skipped with ONE INFO line on a DLL that has no
    /// combined territories (<see cref="Features.Combine"/>); the server side (S30, S31, R30) lives in ServerRole.
    ///
    /// TSAdmin creates two overlapping circles of r=8, 13 m apart, and joins B into A with the hammer command path
    /// (terr_combine_at twice, the way the "Territory combine" tool runs it). A protects (access, build), announces,
    /// and is on the map; B protects nothing and has other words: after the combine, whatever refuses a visitor on B's
    /// old ground can only be A's rules. TSPlain, a visitor, then asks the game what the one combined territory does.
    /// </summary>
    internal static class Scenario020
    {
        private static float T => E2EEnv.StepTimeout;

        private static JObject V(Vector3 v) => new JObject { ["x"] = v.x, ["y"] = v.y, ["z"] = v.z };

        private static Vector3 P(JToken t) => new Vector3((float)t["x"], (float)t["y"], (float)t["z"]);

        // ─────────────────────────────────────────────────────────────
        // TSAdmin: create A and B, combine them with terr_combine_at
        // ─────────────────────────────────────────────────────────────

        internal static IEnumerator AdminPart()
        {
            if (!Features.Combine)
            {
                Report.Info("0.2.0 phase skipped: this DLL has no combined territories (no Territory.PartCount / terr_combine_at), " +
                            "so the 0.1.1 checks are the whole scenario");
                yield break;
            }

            Vector3 origin = ScenarioAdmin.Origin;
            JObject layout = RunDir.Read("layout");
            float rotation = layout != null && layout["rotation"] != null ? (float)layout["rotation"] : 0f;
            var keepClear = new List<Vector3> { origin };
            foreach (string k in new[] { "circle", "rect2" })
                if (ScenarioAdmin.Centres.TryGetValue(k, out Vector3 kc)) keepClear.Add(kc);
            Layout.PlaceCombine(origin, rotation, keepClear, out Vector3 ca, out Vector3 cb);
            ScenarioAdmin.Centres[Layout.CombA.Key] = ca;
            ScenarioAdmin.Centres[Layout.CombB.Key] = cb;
            Report.Info($"0.2.0 combine phase ({Features.Describe()}): A '{Layout.CombAName}' at {World.P(ca)}, B '{Layout.CombBName}' at {World.P(cb)}, " +
                        $"{World.F(Vector3.Distance(new Vector3(ca.x, 0f, ca.z), new Vector3(cb.x, 0f, cb.z)))} m apart, r=8 each");

            // A20a: both created through terr_place, exactly as drafted.
            var made = new Dictionary<string, Territory>();
            var why = new List<string>();
            foreach (Spec s in new[] { Layout.CombA, Layout.CombB })
            {
                ScenarioAdmin.SetDraft(s);
                Vector3 c = ScenarioAdmin.Centres[s.Key];
                float before = TerritoryClient.LastResultAt;
                World.Run(IhCommand.Place(IhCommand.ShapeOf(s.Kind), c, s.HalfX, s.HalfZ, s.Yaw));
                var ok = new Box<bool>();
                yield return Flow.Until(() => World.ByName(s.Name) != null && TerritoryClient.LastResultAt > before, T, ok);
                Territory t = World.ByName(s.Name);
                string result = TerritoryClient.LastResult ?? "";
                if (!ok.Value || t == null || !TerritoryClient.LastResultOk || !result.StartsWith("Created #"))
                {
                    why.Add($"'{s.Name}' not created: result ok={TerritoryClient.LastResultOk} '{result}', in the index: {t != null}");
                    continue;
                }
                made[s.Key] = t;
                if (t.Kind != s.Kind || Mathf.Abs(t.X - c.x) > 0.05f || Mathf.Abs(t.Z - c.z) > 0.05f || !Mathf.Approximately(t.HalfX, s.HalfX) ||
                    t.Flags != s.Flags || t.PvP != s.PvP || t.EnterMessage != s.EnterMessage || Features.PartCount(t) != 1 || t.Revision != 1)
                    why.Add($"'{s.Name}' is not what its draft says: {World.Describe(t)} enter '{t.EnterMessage}' parts {Features.PartCount(t)}");
            }
            bool created = made.Count == 2 && why.Count == 0;
            Report.Check("A20a-comb-create", $"two overlapping circles created through terr_place, exactly as drafted: {string.Join(" / ", made.Values.Select(World.Describe))}",
                created, string.Join("; ", why));
            if (made.Count < 2)
            {
                RunDir.Signal("admin.combined", new JObject { ["ok"] = false, ["why"] = "the two territories were not both created" });
                Report.Fail("A20b-combine", "terr_combine_at twice joins B into A", "the two territories were not both created (A20a)");
                yield break;
            }

            // A20b: the first terr_combine_at picks A and sends nothing; the second, inside B within 15 s, joins B into A.
            Territory a = made[Layout.CombA.Key], b = made[Layout.CombB.Key];
            List<Features.Part> aBefore = Features.Parts(a), bBefore = Features.Parts(b);
            int aRev = a.Revision;
            float t0 = Time.realtimeSinceStartup;
            World.Run(IhCommand.Combine(new Vector3(a.X, 0f, a.Z)));
            yield return Flow.Seconds(1.5f);
            Territory a1 = TerritoryIndex.Find(a.Id);
            bool armedOnly = a1 != null && TerritoryIndex.Find(b.Id) != null && Features.PartCount(a1) == 1 &&
                             World.ConsoleSince(t0).Any(l => l.Contains("stays"));

            float before2 = TerritoryClient.LastResultAt;
            World.Run(IhCommand.Combine(new Vector3(b.X, 0f, b.Z)));
            var done = new Box<bool>();
            yield return Flow.Until(() => TerritoryIndex.Find(b.Id) == null && TerritoryClient.LastResultAt > before2, T, done);
            Territory now = TerritoryIndex.Find(a.Id);
            List<Features.Part> nowParts = now != null ? Features.Parts(now) : new List<Features.Part>();
            string result2 = TerritoryClient.LastResult ?? "";

            var problems = new List<string>();
            if (!armedOnly) problems.Add("the first terr_combine_at did not just arm (both territories one part each and a 'stays' line in the console)");
            if (!done.Value || !TerritoryClient.LastResultOk || !result2.StartsWith("Combined ")) problems.Add($"no 'Combined' answer: done={done.Value} ok={TerritoryClient.LastResultOk} '{result2}'");
            if (now == null || nowParts.Count < 2) problems.Add($"A has {nowParts.Count} part(s), wanted 2 or more");
            if (TerritoryIndex.Find(b.Id) != null || TerritoryIndex.All.Any(t => t.Name == Layout.CombBName)) problems.Add("B is still in the index");
            if (now != null)
            {
                if (now.Name != a.Name || now.Flags != a.Flags || now.EnterMessage != a.EnterMessage || now.PvP != a.PvP) problems.Add($"A's own name or rules changed: {World.Describe(now)}");
                if (now.Revision != aRev + 1) problems.Add($"revision {now.Revision}, wanted {aRev + 1}");
                if (nowParts.Count >= 2 && (!nowParts[0].Same(aBefore[0]) || !nowParts[1].Same(bBefore[0])))
                    problems.Add($"parts [{string.Join(" ; ", nowParts)}] are not A's own ground then B's [{aBefore[0]} ; {bBefore[0]}]");
            }
            Report.Check("A20b-combine",
                $"terr_combine_at inside A only arms; inside B within 15 s joins B into A: the index then holds A with {nowParts.Count} parts, B gone, A's name and rules unchanged: '{result2}'",
                problems.Count == 0, string.Join("; ", problems));

            bool combined = now != null && nowParts.Count >= 2 && TerritoryIndex.Find(b.Id) == null;
            RunDir.Signal("admin.combined", new JObject
            {
                ["ok"] = combined,
                ["aId"] = a.Id,
                ["bId"] = b.Id,
                ["aName"] = a.Name,
                ["bName"] = b.Name,
                ["enter"] = a.EnterMessage,
                ["a"] = V(ca),
                ["b"] = V(cb),
                ["revision"] = now?.Revision ?? 0,
                ["parts"] = new JArray(nowParts.Select(p => p.ToJson()))
            });
        }

        // ─────────────────────────────────────────────────────────────
        // TSPlain: what the game does on the one combined territory
        // ─────────────────────────────────────────────────────────────

        internal static IEnumerator PlainPart(Vector3 home)
        {
            if (!Features.Combine) yield break;

            var barrier = new Box<JObject>();
            yield return RunDir.Wait("admin.combined", 300f, barrier);
            JObject c = barrier.Value;
            if (c == null || c["ok"] == null || !(bool)c["ok"])
            {
                string reason = c == null ? "admin.combined never signalled" : $"the admin's combine did not complete ({(string)c["why"] ?? "see A20a / A20b"})";
                foreach (string id in new[] { "P20a-comb-snapshot", "P20b-comb-access", "P20c-comb-presence", "P20d-comb-seam-quiet", "P20e-comb-minimap" })
                    Report.Fail(id, "the combined territory as a visitor sees it", reason);
                yield break;
            }

            int aId = (int)c["aId"], bId = (int)c["bId"];
            string aName = (string)c["aName"], bName = (string)c["bName"];
            string enterText = ((string)c["enter"] ?? "").Replace("{name}", aName);
            Vector3 ca = P(c["a"]), cb = P(c["b"]);
            List<Features.Part> want = ((JArray)c["parts"]).OfType<JObject>().Select(Features.Part.FromJson).ToList();
            Vector3 axis = new Vector3(cb.x - ca.x, 0f, cb.z - ca.z);
            float gap = axis.magnitude;
            Vector3 outside = cb + axis.normalized * 12f;   // 4 m beyond B's old edge, 25 m from A's centre: in neither part

            // P20a: the one combined record in the plain client's index.
            var ok = new Box<bool>();
            yield return Flow.Until(() =>
            {
                Territory t = TerritoryIndex.Find(aId);
                return t != null && Features.PartCount(t) >= want.Count && TerritoryIndex.Find(bId) == null;
            }, T, ok);
            Territory a = TerritoryIndex.Find(aId);
            List<Features.Part> have = a != null ? Features.Parts(a) : new List<Features.Part>();
            List<Territory> named = TerritoryIndex.All.Where(t => t.Name.StartsWith("TS Comb")).ToList();
            bool sameParts = have.Count == want.Count && have.Zip(want, (x, y) => x.Same(y)).All(x => x);
            bool governs = TerritoryIndex.GoverningAt(ca)?.Id == aId && TerritoryIndex.GoverningAt(cb)?.Id == aId;
            Report.Check("P20a-comb-snapshot",
                $"the plain client's index shows ONE combined record: #{aId} '{aName}' with {have.Count} parts [{string.Join(" ; ", have)}], '{bName}' (#{bId}) gone, and it governs both parts",
                ok.Value && named.Count == 1 && named[0].Id == aId && sameParts && governs,
                $"records named 'TS Comb...': [{string.Join(", ", named.Select(World.Describe))}]; parts {have.Count} vs {want.Count} (same {sameParts}); B in index {TerritoryIndex.Find(bId) != null}; governs A/B ground {governs}");

            // P20b: refusals on the second part's ground, where B alone would have refused nothing.
            float t1 = Time.realtimeSinceStartup;
            bool bAccess = World.CheckAccess(cb, build: false);
            string feedback = World.CenterSince(t1).LastOrDefault() ?? "";
            bool bBuild = World.CheckAccess(cb, build: true, flash: false);
            bool aAccess = World.CheckAccess(ca, build: false, flash: false);
            bool freeAccess = World.CheckAccess(outside, build: false, flash: false);
            bool freeBuild = World.CheckAccess(outside, build: true, flash: false);
            Report.Check("P20b-comb-access",
                $"as a visitor PrivateArea.CheckAccess says NO (access and build) at {World.P(cb)}, {World.F(gap)} m from A's centre and inside only the second part, with A's refusal message; NO in the first part too; YES 4 m beyond it",
                !bAccess && !bBuild && !aAccess && freeAccess && freeBuild && feedback == $"{aName}: only members may use this",
                $"second part: access={bAccess} build={bBuild} message='{feedback}'; first part: access={aAccess}; control {World.P(outside)}: access={freeAccess} build={freeBuild}");

            // P20c / P20d / P20e: walk in from home, across the seam, and out again.
            yield return World.Go(home, 1f);
            float tEnter = Time.realtimeSinceStartup;
            yield return World.Go(ca, 1.2f);
            World.BannerView entry = null, last = null;
            float end = Time.realtimeSinceStartup + (Features.EpicBanner ? 12f : 0f);   // the banner may wait for vanilla's own (BannerPlan.MaxWait 8 s)
            while (Features.EpicBanner)
            {
                World.BannerView v = World.BannerNow();
                if (v != null) last = v;
                if (v != null && v.Title == aName) { entry = v; break; }
                if (Time.realtimeSinceStartup > end) break;
                yield return null;
            }
            bool entryOk = Features.EpicBanner ? entry != null && entry.Subtitle == enterText : World.CenterSince(tEnter).Any(s => s.Contains(enterText));
            bool inFirst = Presence.Current?.Id == aId;

            var samples = new List<int>();
            var stray = new List<string>();
            var seen = new HashSet<GameObject>();
            GameObject entryGo = entry?.Go;
            int steps = Mathf.Max(1, Mathf.CeilToInt(gap));
            float tWalk = Time.realtimeSinceStartup;
            for (int i = 1; i <= steps; i++)
            {
                World.MoveTo(Vector3.Lerp(ca, cb, i / (float)steps));
                float until = Time.realtimeSinceStartup + 0.3f;
                while (Time.realtimeSinceStartup < until)
                {
                    World.BannerView v = World.BannerNow();
                    if (v != null && v.Go != entryGo && v.Title == aName && seen.Add(v.Go)) stray.Add(v.ToString());
                    yield return null;
                }
                samples.Add(Presence.Current?.Id ?? 0);
            }
            yield return Flow.Seconds(0.6f);
            samples.Add(Presence.Current?.Id ?? 0);
            List<string> walkShown = World.CenterSince(tWalk);
            Territory inSecond = Presence.Current;
            bool secondOk = inSecond != null && inSecond.Id == aId && Features.PartCount(inSecond) >= 2;
            Report.Check("P20c-comb-presence",
                $"Presence.Current is the combined territory #{aId} '{aName}' in the first part and again in the second part at {World.P(cb)}",
                inFirst && secondOk,
                $"first part: {World.Describe(TerritoryIndex.GoverningAt(ca))} counted as in: {inFirst}; second part: Presence.Current = {World.Describe(inSecond)}");

            bool quiet = samples.All(s => s == aId) && stray.Count == 0 &&
                         !walkShown.Any(s => s.Contains(aName) || s.Contains("Leaving") || s.Contains("Entering") || (enterText.Length > 0 && s.Contains(enterText)));

            // P20e, inside: the minimap's small label. Minimap.Update does not run headless, so vanilla's own UpdateBiome is called.
            string insideLabel = MinimapLabel(out string mapNote);

            // The way out: the goodbye is a centre message, and the label hands back the biome's name.
            float tLeave = Time.realtimeSinceStartup;
            yield return World.Go(home, 1.2f);
            List<string> leaveShown = World.CenterSince(tLeave);
            bool leaveOk = leaveShown.Any(s => s.StartsWith("Leaving " + aName));
            string outsideLabel = MinimapLabel(out string mapNote2);

            Report.Info($"P20d evidence - Presence along the walk [{string.Join(",", samples)}]; centre messages during the walk [{string.Join(" || ", walkShown)}]; " +
                        $"entry banner {(entry != null ? entry.ToString() : "none")}; centre messages after leaving [{string.Join(" || ", leaveShown)}]");
            Report.Info($"P20e evidence - label inside '{insideLabel}' ({mapNote}); label outside '{outsideLabel}' ({mapNote2})");
            Report.Check("P20d-comb-seam-quiet",
                $"walking across the seam ({steps} steps of about 1 m) shows no leave and no enter banner (no centre message, no new banner, Presence stays on #{aId}); controls: the entry banner showed on the way in" +
                (Features.EpicBanner ? $" (banner in vanilla's slot titled '{aName}', subtitle '{enterText}')" : $" (Simple style: the centre line '{enterText}')") + " and 'Leaving " + aName + "' showed on the way out",
                quiet && entryOk && leaveOk,
                $"Presence along the walk [{string.Join(",", samples)}]; new banners of ours {stray.Count} [{string.Join(" | ", stray)}]; centre messages during the walk [{string.Join(" || ", walkShown)}]; " +
                $"entry banner {(entry != null ? entry.ToString() : last != null ? "not ours: " + last : "none")} (entry control ok={entryOk}); centre messages after leaving [{string.Join(" || ", leaveShown)}] (leave control ok={leaveOk})");

            Report.Check("P20e-comb-minimap",
                $"the minimap's small label (Minimap.UpdateBiome called directly) contains '{aName}' inside the second part and does not outside",
                insideLabel != null && outsideLabel != null && insideLabel.Contains(aName) && !outsideLabel.Contains(aName),
                $"inside: '{insideLabel}' ({mapNote}); outside: '{outsideLabel}' ({mapNote2})");
        }

        /// <summary>vanilla's Minimap.UpdateBiome for the local player, then the small label's text; null with a note when it cannot be read.</summary>
        private static string MinimapLabel(out string note)
        {
            note = "";
            Minimap map = Minimap.instance;
            if (map == null || map.m_biomeNameSmall == null || World.Me == null)
            {
                note = $"no minimap label: instance={map != null} label={map != null && map.m_biomeNameSmall != null} player={World.Me != null}";
                return null;
            }
            try
            {
                map.UpdateBiome(World.Me);
                note = $"mode {map.m_mode}, Presence.Current {Presence.Current?.Name ?? "none"}";
                return map.m_biomeNameSmall.text ?? "";
            }
            catch (Exception ex)
            {
                note = "UpdateBiome threw " + ex.GetType().Name + ": " + ex.Message;
                return null;
            }
        }
    }
}
