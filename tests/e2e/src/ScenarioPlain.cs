using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using TerroritySmellatory;
using UnityEngine;

namespace TerrE2E
{
    /// <summary>
    /// TSPlain: a plain visitor. Not an admin as the server decides it (the rig's one test seam, server side), and
    /// in no territory's member list until TSAdmin adds it. It never asks the server for anything; it walks into
    /// each territory and asks the GAME what happens: vanilla's ward check, a door, a chest, a pickable, a hit on
    /// a building, the PvP flag and its toggle, the status effects and the stats they change, the HUD's centre
    /// message, the mod's own Presence.
    /// </summary>
    internal static class ScenarioPlain
    {
        private static float T => E2EEnv.StepTimeout;

        private static Vector3 _home;
        private static float _carry0, _armor0, _regen0;
        private static WearNTear _squareChest;
        private static Vector3 _squareChestAt;

        // ─────────────────────────────────────────────────────────────
        // PHASE 1: the snapshot, then a visitor in all three territories
        // ─────────────────────────────────────────────────────────────

        internal static IEnumerator Phase1()
        {
            var ok = new Box<bool>();
            yield return Flow.Until(() => TerritoryClient.HasSnapshot, T, ok);
            Report.Check("P01-snapshot-plain", "plain's snapshot arrived and the SERVER says IsAdmin=false",
                ok.Value && !TerritoryClient.IsAdmin, $"hasSnapshot={TerritoryClient.HasSnapshot} isAdmin={TerritoryClient.IsAdmin}");

            Player me = World.Me;
            _home = me.transform.position;
            Fixtures.Home = _home;
            RunDir.Signal("plain.ready", new { id = me.GetPlayerID(), name = me.GetPlayerName(), x = _home.x, y = _home.y, z = _home.z });

            var created = new Box<JObject>();
            yield return RunDir.Wait("admin.created", 600f, created);
            if (created.Value == null)
            {
                Report.Fail("P02-territories", "the admin's territories arrive", "admin.created never signalled");
                yield break;
            }

            yield return Flow.Step("P02-territories", "territories arrive", SameAsServer("P02-territories", 3));
            yield return Flow.Step("P02b-home", "home", HomeCheck());
            yield return Flow.Step("P03-circle", "visitor in the circle", VisitCircle());
            yield return Flow.Step("P04-square", "visitor in the square", VisitSquare());
            // The rectangle's rules are edited by the admin right after the creates: wait for that edit, so the
            // plain's checks of it cannot race the admin (in solo mode it happened before this phase anyway).
            var p1 = new Box<JObject>();
            yield return RunDir.Wait("admin.phase1-done", 300f, p1);
            yield return Flow.Until(() => (World.ByName(Layout.RectName)?.Revision ?? 0) >= 2, T, ok);
            yield return Flow.Step("P05-rect", "visitor in the rectangle", VisitRect());
            yield return Flow.Step("P06-no-request", "never sends a request", NoRequest());
            RunDir.Signal("plain.visitor-done");
        }

        /// <summary>
        /// The plain client's TerritoryIndex against the SERVER's world file on disk: ids, names, shapes, geometry,
        /// rules, members. Retried for a while, because the file and the snapshot are written moments apart.
        /// </summary>
        private static IEnumerator SameAsServer(string id, int expectCount)
        {
            var ok = new Box<bool>();
            string why = "";
            yield return Flow.Until(() => (why = CompareWithFile(expectCount)) == null, T, ok);
            Report.Check(id, $"plain's snapshot holds the same {expectCount} E2E territories as the server's file (ids, names, shape, geometry, rules, members): " +
                              string.Join("; ", TerritoryIndex.All.Where(t => t.Name.StartsWith("E2E")).Select(World.Describe)),
                ok.Value, why);
        }

        private static string CompareWithFile(int expectCount)
        {
            string file = E2EEnv.WorldFile;
            if (string.IsNullOrEmpty(file) || !File.Exists(file)) return $"no server world file at '{file}'";
            JObject root = JObject.Parse(File.ReadAllText(file));
            var disk = ((JArray)root["territories"]).OfType<JObject>().Where(o => ((string)o["name"] ?? "").StartsWith("E2E")).ToList();
            var mine = TerritoryIndex.All.Where(t => t.Name.StartsWith("E2E")).ToList();
            if (disk.Count != expectCount || mine.Count != expectCount) return $"file has {disk.Count}, snapshot has {mine.Count}, expected {expectCount}";
            foreach (JObject o in disk)
            {
                int tid = (int)o["id"];
                Territory t = TerritoryIndex.Find(tid);
                if (t == null) return $"#{tid} is in the file but not in the snapshot";
                var flags = new HashSet<string>(((JArray)o["flags"] ?? new JArray()).Select(f => (string)f));
                var mineFlags = new HashSet<string>(Enum.GetValues(typeof(TerritoryFlags)).Cast<TerritoryFlags>()
                    .Where(f => f != TerritoryFlags.None && t.Has(f)).Select(f => f.ToString()));
                var members = ((JArray)o["members"] ?? new JArray()).OfType<JObject>().Select(m => (long)m["id"]).OrderBy(x => x).ToList();
                if ((string)o["name"] != t.Name) return $"#{tid} name '{o["name"]}' vs '{t.Name}'";
                if ((string)o["shape"] != t.Kind.ToString()) return $"#{tid} shape {o["shape"]} vs {t.Kind}";
                if (Mathf.Abs((float)o["x"] - t.X) > 0.01f || Mathf.Abs((float)o["z"] - t.Z) > 0.01f) return $"#{tid} centre";
                if (Mathf.Abs((float)o["halfX"] - t.HalfX) > 0.01f || Mathf.Abs((float)o["halfZ"] - t.HalfZ) > 0.01f) return $"#{tid} size";
                if (Mathf.Abs(Mathf.DeltaAngle((float)o["yaw"], t.Yaw)) > 0.01f) return $"#{tid} yaw";
                if (!flags.SetEquals(mineFlags)) return $"#{tid} flags [{string.Join(",", flags)}] vs [{string.Join(",", mineFlags)}]";
                if ((string)o["pvp"] != t.PvP.ToString()) return $"#{tid} pvp {o["pvp"]} vs {t.PvP}";
                if ((int)o["revision"] != t.Revision) return $"#{tid} revision {o["revision"]} vs {t.Revision}";
                if (!members.SequenceEqual(t.Members.Select(m => m.Id).OrderBy(x => x))) return $"#{tid} members";
                if ((int)(o["priority"] ?? 0) != t.Priority) return $"#{tid} priority";
            }
            return null;
        }

        private static IEnumerator HomeCheck()
        {
            Territory here = TerritoryIndex.GoverningAt(_home);
            if (here != null)
            {
                // The spawn point should be open country by construction; if not, find some.
                _home = Fixtures.OpenGround();
                Fixtures.Home = _home;
            }
            yield return World.Go(_home);
            _carry0 = World.Me.GetMaxCarryWeight();
            _armor0 = World.Me.GetBodyArmor();
            _regen0 = Regen();
            Report.Check("P02b-home", $"home {World.P(_home)} is open country: Presence.Current is null", Presence.Current == null,
                $"Presence.Current = {World.Describe(Presence.Current)}");
        }

        private static float Regen()
        {
            float r = 1f;
            World.Me.GetSEMan().ModifyHealthRegen(ref r);
            return r;
        }

        /// <summary>
        /// The Sanctuary effect's identity AS THE GAME KNOWS IT: SEMan adds, finds and removes status effects by
        /// NameHash, the stable hash of the ScriptableObject's .name (StatusEffect.NameHash). The name is the mod's
        /// constant from the DLL under test; the hash is vanilla's own GetStableHashCode.
        /// </summary>
        private static int SanctuaryHash => TerroritySmellatory.Sanctuary.EffectName.GetStableHashCode();

        /// <summary>The mod's Sanctuary effect on the player, looked up the way SEMan itself looks effects up.</summary>
        private static StatusEffect SanctuaryEffect() => World.Me.GetSEMan().GetStatusEffect(SanctuaryHash);

        private static string EffectList() =>
            "[" + string.Join(", ", World.Me.GetSEMan().GetStatusEffects().Select(s => $"{s.name}/{s.m_name} ({s.GetType().Name})")) + "]";

        /// <summary>
        /// Why the first version of P03e never found the effect: every part of its predicate
        /// (OfType&lt;SE_Stats&gt;, m_name == territory name, name.StartsWith("SE_Territor") with the current
        /// culture), evaluated on every effect the player has. INFO only. Answer (2026-09-29): the prefix was
        /// spelled "Territor", the mod's effect is "SE_TerroritySanctuary" (the mod's own spelling), so it never
        /// matched - with either comparison - while type, label and name hash all did.
        /// </summary>
        private static string OldPredicateDiagnosis(string territoryName)
        {
            var parts = new List<string>();
            foreach (StatusEffect s in World.Me.GetSEMan().GetStatusEffects())
            {
                string n = s.name ?? "";
                string m = s.m_name ?? "";
                parts.Add($"{n}: type={s.GetType().FullName} isSE_Stats={s is SE_Stats} alive={(UnityEngine.Object)s != null} " +
                          $"m_name=='{territoryName}':{m == territoryName} (len {m.Length}/{territoryName.Length}) " +
                          $"name==EffectName:{n == TerroritySmellatory.Sanctuary.EffectName} " +
                          $"StartsWith(culture '{System.Globalization.CultureInfo.CurrentCulture.Name}'):{n.StartsWith("SE_Territor")} " +
                          $"StartsWith(ordinal):{n.StartsWith("SE_Territor", StringComparison.Ordinal)} " +
                          $"NameHash==hash:{s.NameHash() == SanctuaryHash}");
            }
            return string.Join(" || ", parts);
        }

        // ─────────────────────────────────────────────────────────────
        // PvP: the player's own choice is made with the inventory's toggle, the way a player makes it
        // ─────────────────────────────────────────────────────────────

        private static bool _toggleWorks = true;

        private static IEnumerator SetOwnPvP(bool on)
        {
            InventoryGui gui = InventoryGui.instance;
            gui.Show(null);
            yield return Flow.Frames(3);
            gui.m_pvp.isOn = on;
            yield return Flow.Frames(4);
            gui.Hide();
            yield return Flow.Frames(3);
            if (World.Me.IsPVPEnabled() != on)
            {
                _toggleWorks = false;
                Report.Info("the inventory's PvP toggle did not reach the player headless; setting the choice directly (SetPVP + toggle)");
                gui.m_pvp.SetIsOnWithoutNotify(on);
                World.Me.SetPVP(on);
                yield return Flow.Frames(2);
            }
        }

        /// <summary>Opens the inventory, reads the toggle, tries to flip it, reads the player's PvP again.</summary>
        private static IEnumerator ToggleLocked(string id, bool forced)
        {
            InventoryGui gui = InventoryGui.instance;
            gui.Show(null);
            yield return Flow.Frames(4);
            bool interactable = gui.m_pvp.interactable;
            bool shownOn = gui.m_pvp.isOn;
            gui.m_pvp.isOn = !forced;   // the player clicks it
            yield return Flow.Frames(4);
            bool after = World.Me.IsPVPEnabled();
            bool toggleAfter = gui.m_pvp.isOn;
            gui.Hide();
            yield return Flow.Frames(3);
            string what = $"PvP toggle locked at {(forced ? "on" : "off")}: greyed out, and a click on it does not change the player's PvP";
            string detail = $"interactable={interactable} shown={shownOn} after click: player pvp={after} toggle={toggleAfter}";
            if (!_toggleWorks)
            {
                // The toggle reaches the player headless (every run since the rig was built), so a toggle that did
                // not work in OPEN COUNTRY (SetOwnPvP, at home) is a broken toggle, whoever broke it - a FAIL, never
                // a SOFT that would let the lock go untested.
                Report.Fail(id, what, "the inventory's PvP toggle did not reach the player in open country (SetOwnPvP had to fall back to SetPVP), so the lock is untested - " + detail);
                yield break;
            }
            Report.Check(id, what, !interactable && shownOn == forced && after == forced && toggleAfter == forced, detail);
        }

        /// <summary>Vanilla's own PvP notice (SetPVP writes it into the centre slot), plain: what stands there above the Epic banner.</summary>
        private static string Line2(bool on) => World.Loc(on ? "$msg_pvpon" : "$msg_pvpoff");

        /// <summary>How a check's text names the PvP line of a centre message: from 0.4.0 it is vanilla's notice in the colour of its state.</summary>
        private static string Line2Desc(bool on) => World.NotchSupported ? $"{Line2(on)} (coloured {(on ? "red" : "green")})" : Line2(on);

        /// <summary>
        /// Whether the centre text is <paramref name="first"/>, then ONE line with vanilla's PvP notice for the state: plain before
        /// 0.4.0, and from 0.4.0 inside a colour tag whose HUE is the state's (red for on, green for off). Judged by hue, like the
        /// notch, so a retuned tint does not break the rig and a swapped one does.
        /// </summary>
        private static bool CentreIs(string centre, string first, bool pvpOn)
        {
            string word = Line2(pvpOn);
            if (!World.NotchSupported) return centre == first + "\n" + word;
            Match m = Regex.Match(centre ?? "", "^" + Regex.Escape(first + "\n") + "<color=#([0-9A-Fa-f]{6})>" + Regex.Escape(word) + "</color>$");
            return m.Success && World.HueOfHex(m.Groups[1].Value) == (pvpOn ? "red" : "green");
        }

        /// <summary>
        /// The ENTRY banner on a 0.2.0 DLL with the default EntryBannerStyle=Epic (Features.EpicBanner). It is no longer the
        /// centre message of 0.1.1: it is vanilla's biome-found banner, an instance in MessageHud.instance.m_biomeMsgInstance
        /// whose "Title" shows the territory's name and whose "TS_EntryText" is the entry text as a subtitle (none when the
        /// territory keeps the default text). The centre slot then carries only vanilla's own PvP notice (SetPVP writes it),
        /// never the entry text. The banner is waited for, up to 12 s: the mod holds it while vanilla's own banner is up
        /// (BannerPlan.MaxWait, 8 s). Leaving is still a centre message (P03m, P04i keep their old form).
        /// </summary>
        /// <param name="subtitle">The entry text the subtitle must read; null when the territory has the default text (no subtitle).</param>
        /// <param name="pvpNotice">Vanilla's PvP notice that must be the centre message; null when no notice is due (no centre message at all).</param>
        /// <param name="notchId">0.4.0: the id of the PvP notch check made on the same banner (<see cref="NotchCheck"/>); null for none.</param>
        private static IEnumerator EpicEntry(string id, Territory t, string subtitle, string pvpNotice, float since, string notchId = null)
        {
            World.BannerView view = null, last = null;
            float end = Time.realtimeSinceStartup + 12f;
            while (true)
            {
                World.BannerView v = World.BannerNow();
                if (v != null) last = v;
                if (v != null && v.Title == t.Name) { view = v; break; }
                if (Time.realtimeSinceStartup > end) break;
                yield return null;
            }
            List<string> shown = World.CenterSince(since);
            Report.Info($"{id} evidence - {(view != null ? $"banner in the slot {Time.realtimeSinceStartup - since:0.0} s after the move: {view}" : "no banner of ours in the slot")}; " +
                        $"HUD centre now '{World.CenterNow}', centre messages since entering [{string.Join(" || ", shown)}]");
            var why = new List<string>();
            if (view == null) why.Add(last == null ? "the biome-found slot stayed empty for 12 s" : $"the slot held another banner: {last}");
            else if (subtitle == null ? !string.IsNullOrEmpty(view.Subtitle) : view.Subtitle != subtitle)
                why.Add($"subtitle '{view.Subtitle ?? "(none)"}', wanted {(subtitle == null ? "none (default text)" : "'" + subtitle + "'")}");
            if (shown.Any(m => m.Contains(t.Name) || m.Contains("Entering") || (subtitle != null && m.Contains(subtitle))))
                why.Add("the entry text was ALSO shown as a centre message (0.1.1's form)");
            if (pvpNotice != null && (World.CenterNow != pvpNotice || shown.LastOrDefault() != pvpNotice))
                why.Add($"vanilla's PvP notice '{pvpNotice}' is not the centre message");
            if (pvpNotice == null && shown.Count != 0) why.Add("a centre message was shown although no PvP notice was due");
            Report.Check(id,
                $"Epic entry banner: vanilla's biome-found banner (MessageHud.m_biomeMsgInstance) is titled '{t.Name}' with {(subtitle == null ? "no subtitle (default text)" : "the entry text '" + subtitle + "' as its subtitle")}; " +
                (pvpNotice == null ? "no centre message" : $"the centre line is only vanilla's PvP notice '{pvpNotice}'"),
                why.Count == 0,
                $"{string.Join("; ", why)} | banner {(view != null ? view.ToString() : last != null ? last.ToString() : "none")}, HUD centre now '{World.CenterNow}', shown since entering: [{string.Join(" || ", shown)}]");
            if (notchId != null && World.NotchSupported) NotchCheck(notchId, t, view);
        }

        /// <summary>
        /// The PvP notch on the banner just read (0.4.0), judged from the territory's own rule and by hue, never from the mod's
        /// colours: green for a territory that forces PvP off, red for one that forces it on, none for one that leaves the toggle to
        /// the player. And where it sits, read in the banner's own local space (anchored positions, sizes and the rotation), which
        /// does not depend on the headless client's screen size: a child of the Title's own parent, so it fades with the banner;
        /// centred on the LOWER braid, with its middle inside the braid's band; its lowest corner above the subtitle's top edge; and
        /// no Image of it a raycast target.
        /// </summary>
        private static void NotchCheck(string id, Territory t, World.BannerView view)
        {
            string want = t.PvP == PvPRule.ForcedOff ? "green" : t.PvP == PvPRule.ForcedOn ? "red" : "none";
            var why = new List<string>();
            if (view == null) why.Add("there was no banner of ours to read a notch from");
            else
            {
                string got = !view.HasNotch ? "none" : view.NotchColour.HasValue ? World.HueOf(view.NotchColour.Value) : "no image";
                if (got != want) why.Add($"the notch is {got}, wanted {want}");
                if (want != "none" && view.HasNotch)
                {
                    // An exception here would end the whole step (and every check after it): it is a FAIL of this check alone.
                    try { why.AddRange(NotchPlacement(view)); }
                    catch (Exception ex) { why.Add($"reading the notch's placement threw {ex.GetType().Name}: {ex.Message}"); }
                }
            }
            Report.Check(id,
                $"the Epic banner's PvP notch for '{t.Name}' (PvP {t.PvP}) is " +
                (want == "none" ? "absent: the player's own toggle stays in charge" : $"{want}, set on the lower braid, clear of the subtitle, drawn over the braids, no raycast target"),
                why.Count == 0,
                $"{string.Join("; ", why)} | banner {(view != null ? view.ToString() : "none")}");
        }

        private static List<string> NotchPlacement(World.BannerView view)
        {
            var why = new List<string>();
            var notch = view.Notch as RectTransform;
            Transform title = World.FindIn(view.Go, "Title");
            Transform subtitle = World.FindIn(view.Go, "TS_EntryText");
            if (notch == null || title == null)
            {
                why.Add($"cannot place the notch: notch rect {(notch != null ? "found" : "missing")}, Title {(title != null ? "found" : "missing")}");
                return why;
            }
            if (notch.parent != title.parent) why.Add("the notch is not a child of the Title's own parent, so it would not fade with the banner");
            foreach (UnityEngine.UI.Image image in notch.GetComponentsInChildren<UnityEngine.UI.Image>(true))
                if (image.raycastTarget) why.Add($"'{image.name}' is a raycast target");

            // After every vanilla child in the draw order (a later sibling is drawn on top), so the gem is not hidden behind a braid.
            if (notch.GetSiblingIndex() != notch.parent.childCount - 1) why.Add($"the notch is not the last child of its parent (index {notch.GetSiblingIndex()} of {notch.parent.childCount}): a later sibling would be drawn over it");

            // The lower of the two braids: the prefab's "braid" at y +40 and "braid (1)" at y -40, flipped. Found by name prefix and height.
            RectTransform lower = null;
            foreach (Transform tr in view.Go.GetComponentsInChildren<Transform>(true))
                if (tr.name.StartsWith("braid") && tr is RectTransform r && (lower == null || r.anchoredPosition.y < lower.anchoredPosition.y)) lower = r;
            float theta = notch.localEulerAngles.z * Mathf.Deg2Rad;
            float half = notch.rect.width * 0.5f * (Mathf.Abs(Mathf.Cos(theta)) + Mathf.Abs(Mathf.Sin(theta)));   // the turned square's reach up and down
            Vector2 at = notch.anchoredPosition;
            if (lower == null) why.Add("no braid found to place the notch on");
            else
            {
                float band = lower.rect.height * 0.5f;
                if (Mathf.Abs(at.x - lower.anchoredPosition.x) > 1f) why.Add($"the notch is {World.F(at.x - lower.anchoredPosition.x)} units off the lower braid's centre");
                if (Mathf.Abs(at.y - lower.anchoredPosition.y) > band) why.Add($"the notch's middle (y {World.F(at.y)}) is outside the lower braid's band (y {World.F(lower.anchoredPosition.y - band)} to {World.F(lower.anchoredPosition.y + band)})");
            }
            if (subtitle is RectTransform sub)
            {
                float top = sub.anchoredPosition.y + sub.rect.yMax;
                if (at.y - half < top) why.Add($"the notch's lowest corner (y {World.F(at.y - half)}) reaches into the subtitle (its top edge is y {World.F(top)})");
            }
            Report.Info($"notch placement - at ({World.F(at.x)}, {World.F(at.y)}), {World.F(notch.rect.width)} units square turned {World.F(notch.localEulerAngles.z)} degrees, reaching {World.F(half)} up and down; " +
                        $"lower braid {(lower != null ? $"y {World.F(lower.anchoredPosition.y)} height {World.F(lower.rect.height)}" : "none")}; subtitle {(subtitle != null ? "present" : "none")}");
            return why;
        }

        // ─────────────────────────────────────────────────────────────
        // T1: the circle - build/access/harvest protected, Sanctuary, PvP forced OFF, custom banner
        // ─────────────────────────────────────────────────────────────

        private static IEnumerator VisitCircle()
        {
            Territory t = World.ByName(Layout.CircleName);
            if (t == null)
            {
                Report.Fail("P03-circle", "visit the circle", "not in the index");
                yield break;
            }
            yield return SetOwnPvP(true);
            Report.Info($"own PvP choice before entering: {World.Me.IsPVPEnabled()}");

            float t0 = Time.realtimeSinceStartup;
            yield return World.Go(World.Local(t, 0f, 0f), 1.2f);
            Report.Check("P03a-presence", $"Presence.Current follows the player into '{t.Name}'", Presence.Current?.Id == t.Id,
                $"Presence.Current = {World.Describe(Presence.Current)}");

            if (Features.EpicBanner)
                yield return EpicEntry("P03b-banner-enter", t, $"Welcome to {t.Name}", Line2(false), t0, notchId: "P03q-banner-notch");
            else
            {
                string first = $"Welcome to {t.Name}";
                List<string> shown = World.CenterSince(t0);
                Report.Check("P03b-banner-enter", $"entry banner shows the custom message with the PvP notice on its second line: '{first}\\n{Line2Desc(false)}'",
                    CentreIs(World.CenterNow, first, false) && CentreIs(shown.LastOrDefault(), first, false),
                    $"HUD centre now '{World.CenterNow}', shown since entering: [{string.Join(" || ", shown)}]");
            }

            Report.Check("P03c-pvp-forced-off", "PvP forced OFF inside (the player's own choice was ON)",
                !World.Me.IsPVPEnabled() && Presence.ForcedPvP == false, $"pvp={World.Me.IsPVPEnabled()} forced={Presence.ForcedPvP}");
            yield return ToggleLocked("P03d-pvp-toggle-locked", false);

            // P03e: the effect as SEMan identifies it (NameHash), its type, the HUD label (m_name) and the admin's values.
            Report.Info("P03e diagnosis of the first version's lookup: " + OldPredicateDiagnosis(t.Name));
            SEMan seman = World.Me.GetSEMan();
            StatusEffect raw = SanctuaryEffect();
            var se = raw as SE_Stats;
            int copies = seman.GetStatusEffects().Count(s => s != null && s.NameHash() == SanctuaryHash);
            Report.Check("P03e-sanctuary", $"Sanctuary status effect '{TerroritySmellatory.Sanctuary.EffectName}' is on the player (SEMan finds it by its name hash, once), labelled '{t.Name}', with the admin's values",
                seman.HaveStatusEffect(SanctuaryHash) && se != null && copies == 1 && se.m_name == t.DisplayName &&
                Mathf.Approximately(se.m_healthRegenMultiplier, 2f) && Mathf.Approximately(se.m_staminaRegenMultiplier, 1.5f) &&
                Mathf.Approximately(se.m_addMaxCarryWeight, 150f) && Mathf.Approximately(se.m_addArmor, 10f),
                raw == null ? $"SEMan has no effect with hash {SanctuaryHash} (HaveStatusEffect={seman.HaveStatusEffect(SanctuaryHash)}); effects: {EffectList()}"
                            : $"type {raw.GetType().Name}, {copies} cop(ies), label '{raw.m_name}'" + (se == null ? "" : $", health x{se.m_healthRegenMultiplier} stamina x{se.m_staminaRegenMultiplier} carry +{se.m_addMaxCarryWeight} armor +{se.m_addArmor}"));
            yield return Flow.Step("P03p-sanctuary-hud", "sanctuary HUD icon", SanctuaryHud(t));

            // P03f: vanilla's own numbers. Carry and armour are sums (SE_Stats adds m_addMaxCarryWeight / m_addArmor),
            // so their change from the home measurement is the effect's. Health regen is a fold over the effect list
            // in order (SE_Stats: a multiplier above 1 ADDS m-1, one at or below 1 MULTIPLIES), and other effects
            // (Cold, Wet, Rested) come and go with the weather and the time of day - so it is compared in the SAME
            // frame with the fold vanilla makes without the Sanctuary effect, and with the admin's x2 in its place.
            float carry = World.Me.GetMaxCarryWeight(), armor = World.Me.GetBodyArmor(), regen = Regen();
            float regenWithout = 1f, regenExpected = 1f;
            foreach (StatusEffect s in seman.GetStatusEffects())
            {
                if (s != null && s.NameHash() == SanctuaryHash)
                {
                    regenExpected += Layout.Circle.HealthRegen - 1f;
                    continue;
                }
                s.ModifyHealthRegen(ref regenWithout);
                s.ModifyHealthRegen(ref regenExpected);
            }
            Report.Check("P03f-sanctuary-bonuses", $"the bonuses reach vanilla's numbers: carry {World.F(_carry0)} -> {World.F(carry)}, armor {World.F(_armor0)} -> {World.F(armor)}, health regen x{World.F(regenWithout)} without the effect -> x{World.F(regen)} with it (x{World.F(Layout.Circle.HealthRegen)} expected at its place in {EffectList()})",
                Mathf.Abs(carry - _carry0 - 150f * Game.m_carryWeightRate) < 0.01f && Mathf.Abs(armor - _armor0 - 10f) < 0.01f &&
                Mathf.Abs(regen - regenExpected) < 0.01f && regen > regenWithout + 0.01f,
                $"carry +{World.F(carry - _carry0)} (want +{World.F(150f * Game.m_carryWeightRate)}), armor +{World.F(armor - _armor0)} (want +10), regen x{World.F(regen)} (want x{World.F(regenExpected)}, x{World.F(regenWithout)} without it)");

            Vector3 inside = World.Local(t, 2f, -1f);
            float t1 = Time.realtimeSinceStartup;
            bool access = World.CheckAccess(inside, build: false);
            string feedback = World.CenterSince(t1).LastOrDefault() ?? "";
            Report.Check("P03g-access-refused", "PrivateArea.CheckAccess answers NO for access inside the circle (visitor), with the territory's own refusal message",
                !access && feedback == $"{t.Name}: only members may use this", $"access={access} message='{feedback}'");
            Report.Check("P03h-build-refused", "PrivateArea.CheckAccess answers NO for building inside the circle (inside the mod's BuildScope)",
                !World.CheckAccess(inside, build: true), "answered yes");
            yield return Fixtures.DoorWithControl("P03i-door-refused", "a door inside the circle stays shut for the visitor", World.Local(t, -2f, 3f), expectOpen: false);
            yield return Fixtures.ChestWithControl("P03j-chest-refused", "a chest inside the circle does not open for the visitor", World.Local(t, 1.5f, 1f), expectOpen: false);
            yield return Fixtures.PickRefusedWithControl("P03k-harvest-refused", "a pickable inside the circle refuses Interact (not picked)", World.Local(t, -3f, -3f));

            float t2 = Time.realtimeSinceStartup;
            yield return World.Go(_home, 1.2f);
            string leave = $"Leaving {t.Name}";
            Report.Check("P03l-presence-leave", "Presence.Current is null again outside", Presence.Current == null, World.Describe(Presence.Current));
            Report.Check("P03m-banner-leave", $"exit banner with the PvP notice: '{leave}\\n{Line2Desc(true)}'", CentreIs(World.CenterNow, leave, true),
                $"HUD centre now '{World.CenterNow}', shown: [{string.Join(" || ", World.CenterSince(t2))}]");
            InventoryGui gui = InventoryGui.instance;
            gui.Show(null);
            yield return Flow.Frames(4);
            bool released = gui.m_pvp.interactable && gui.m_pvp.isOn;
            gui.Hide();
            yield return Flow.Frames(3);
            Report.Check("P03n-pvp-restored", "the player's own PvP choice (ON) comes back outside, and the toggle is usable again",
                World.Me.IsPVPEnabled() && Presence.ForcedPvP == null && released,
                $"pvp={World.Me.IsPVPEnabled()} forced={Presence.ForcedPvP} toggle interactable/on={released}");
            bool seLeft = World.Me.GetSEMan().HaveStatusEffect(SanctuaryHash) || SanctuaryEffect() != null ||
                          World.Me.GetSEMan().GetStatusEffects().Any(s => s != null && s.m_name == t.Name);
            Report.Check("P03o-sanctuary-gone", "the Sanctuary effect (by its name hash, and by its label) and its carry bonus are gone outside",
                !seLeft && Mathf.Abs(World.Me.GetMaxCarryWeight() - _carry0) < 0.01f,
                $"effect still there={seLeft} {EffectList()} carry={World.F(World.Me.GetMaxCarryWeight())} (home {World.F(_carry0)})");
        }

        /// <summary>
        /// What a player SEES of the effect: Hud.UpdateStatusEffects draws one row per effect with an icon, the
        /// row's first text being the effect's localized m_name. The Sanctuary row must name the territory and
        /// carry the guard stone's build icon (Sanctuary.Icon).
        /// </summary>
        private static IEnumerator SanctuaryHud(Territory t)
        {
            yield return Flow.Frames(3);   // Hud.Update rebuilds the rows every frame
            Hud hud = Hud.instance;
            if (hud == null || hud.m_statusEffects == null)
            {
                Report.Soft("P03p-sanctuary-hud", $"the HUD shows the Sanctuary icon labelled '{t.Name}'", "no Hud instance on this headless client");
                yield break;
            }
            GameObject ward = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab("guard_stone") : null;
            Piece wardPiece = ward != null ? ward.GetComponent<Piece>() : null;
            Sprite wardIcon = wardPiece != null ? wardPiece.m_icon : null;
            var rows = new List<string>();
            bool found = false;
            foreach (RectTransform row in hud.m_statusEffects)
            {
                if (row == null) continue;
                TMPro.TMP_Text label = row.GetComponentInChildren<TMPro.TMP_Text>();   // the same component Hud writes m_name into
                Transform iconTf = row.Find("Icon");
                UnityEngine.UI.Image icon = iconTf != null ? iconTf.GetComponent<UnityEngine.UI.Image>() : null;
                string text = label != null ? label.text : "(no label)";
                bool wardSprite = icon != null && wardIcon != null && icon.sprite == wardIcon;
                rows.Add($"'{text}' icon={(icon != null && icon.sprite != null ? icon.sprite.name : "none")}{(wardSprite ? " (guard stone)" : "")} active={row.gameObject.activeSelf}");
                if (text == t.DisplayName && wardSprite && row.gameObject.activeSelf) found = true;
            }
            Report.Check("P03p-sanctuary-hud", $"the HUD's status-effect row shows '{t.Name}' with the guard stone's icon ({(wardIcon != null ? wardIcon.name : "no guard_stone icon")})",
                found, $"HUD rows: [{string.Join("; ", rows)}]");
        }

        // ─────────────────────────────────────────────────────────────
        // T2: the square - structures protected, no monster spawns, PvP forced ON, default banners
        // ─────────────────────────────────────────────────────────────

        private static IEnumerator VisitSquare()
        {
            Territory t = World.ByName(Layout.SquareName);
            if (t == null)
            {
                Report.Fail("P04-square", "visit the square", "not in the index");
                yield break;
            }
            yield return SetOwnPvP(false);

            _squareChestAt = World.Local(t, -3f, 2f);
            var keep = new Box<WearNTear>();
            yield return Fixtures.DamageTest("P04a-structure-protected", "a visitor's hit on a piece inside the StructureProtect square does no damage", _squareChestAt, expectDamage: false, keep);
            _squareChest = keep.Value;
            yield return Fixtures.DamageTest("P04b-structure-control", "control: the same hit on a piece in open country does damage", Fixtures.OpenGround(80f), expectDamage: true);

            float t0 = Time.realtimeSinceStartup;
            yield return World.Go(World.Local(t, 1f, 1f), 1.2f);
            Report.Check("P04c-presence", $"Presence.Current follows the player into '{t.Name}'", Presence.Current?.Id == t.Id, World.Describe(Presence.Current));
            if (Features.EpicBanner)
                yield return EpicEntry("P04d-banner-enter", t, null, Line2(true), t0, notchId: "P04k-banner-notch");
            else
            {
                string first = $"Entering {t.Name}";
                Report.Check("P04d-banner-enter", $"default entry banner with the PvP notice: '{first}\\n{Line2Desc(true)}'", CentreIs(World.CenterNow, first, true),
                    $"HUD centre now '{World.CenterNow}', shown: [{string.Join(" || ", World.CenterSince(t0))}]");
            }
            Report.Check("P04e-pvp-forced-on", "PvP forced ON inside (the player's own choice was OFF)",
                World.Me.IsPVPEnabled() && Presence.ForcedPvP == true, $"pvp={World.Me.IsPVPEnabled()} forced={Presence.ForcedPvP}");
            yield return ToggleLocked("P04f-pvp-toggle-locked", true);

            yield return Flow.Step("P04g-no-monster-spawns", "spawn point", SpawnPointCheck(t));
            yield return Flow.Step("P04h-creature-spawner", "creature spawner", CreatureSpawnerCheck(t));

            float t1 = Time.realtimeSinceStartup;
            yield return World.Go(_home, 1.2f);
            string leave = $"Leaving {t.Name}";
            Report.Check("P04i-banner-leave", $"exit banner: '{leave}\\n{Line2Desc(false)}'", CentreIs(World.CenterNow, leave, false),
                $"HUD centre now '{World.CenterNow}', shown: [{string.Join(" || ", World.CenterSince(t1))}]");
            Report.Check("P04j-pvp-restored", "the player's own PvP choice (OFF) comes back outside",
                !World.Me.IsPVPEnabled() && Presence.ForcedPvP == null, $"pvp={World.Me.IsPVPEnabled()} forced={Presence.ForcedPvP}");
        }

        private static SpawnSystem.SpawnData HostileSpawn() => new SpawnSystem.SpawnData
        {
            m_name = "e2e",
            m_prefab = ZNetScene.instance.GetPrefab("Greyling"),
            m_biome = Heightmap.Biome.All,
            m_biomeArea = Heightmap.BiomeArea.Everything,
            m_minAltitude = -1000f,
            m_maxAltitude = 1000f,
            m_minTilt = 0f,
            m_maxTilt = 90f,
            m_inForest = true,
            m_outsideForest = true,
            m_inLava = true,
            m_outsideLava = true,
            m_canSpawnCloseToPlayer = true,
            m_insidePlayerBase = true
        };

        private static IEnumerator SpawnPointCheck(Territory t)
        {
            SpawnSystem ss = SpawnSystem.m_instances.FirstOrDefault(s => s != null);
            if (ss == null)
            {
                Report.Soft("P04g-no-monster-spawns", "NoMonsterSpawns refuses a hostile spawn point inside", "no SpawnSystem instance on this client");
                yield break;
            }
            SpawnSystem.SpawnData data = HostileSpawn();
            Vector3 inside = World.Local(t, 0f, -3f);
            Vector3 outside = Fixtures.OpenGround(100f);
            bool insideGood = ss.IsSpawnPointGood(data, ref inside);
            bool outsideGood = ss.IsSpawnPointGood(data, ref outside);
            if (!outsideGood)
                Report.Soft("P04g-no-monster-spawns", "NoMonsterSpawns refuses a hostile spawn point inside", $"control point outside was refused by vanilla too (inside={insideGood})");
            else
                Report.Check("P04g-no-monster-spawns", "SpawnSystem.IsSpawnPointGood refuses a Greyling spawn point inside the square and accepts one in open country",
                    !insideGood, $"inside={insideGood} outside={outsideGood}");
            yield break;
        }

        private static IEnumerator CreatureSpawnerCheck(Territory t)
        {
            GameObject prefab = ZNetScene.instance.m_prefabs.FirstOrDefault(p =>
            {
                CreatureSpawner cs = p != null ? p.GetComponent<CreatureSpawner>() : null;
                return cs != null && cs.m_creaturePrefab != null && Hostility.IsHostile(cs.m_creaturePrefab);
            });
            if (prefab == null)
            {
                Report.Soft("P04h-creature-spawner", "a CreatureSpawner inside the square spawns nothing", "no hostile CreatureSpawner prefab found");
                yield break;
            }
            var results = new List<string>();
            bool insideSpawned = false, outsideSpawned = false;
            foreach (bool inside in new[] { true, false })
            {
                Vector3 at = inside ? World.Local(t, 3f, -3f) : Fixtures.OpenGround(120f);
                GameObject go = UnityEngine.Object.Instantiate(prefab, new Vector3(at.x, World.Ground(at), at.z), Quaternion.identity);
                CreatureSpawner cs = go.GetComponent<CreatureSpawner>();
                ZNetView spawned = cs.Spawn();
                bool did = spawned != null;
                results.Add($"{(inside ? "inside" : "outside")}: {(did ? spawned.name : "nothing")}");
                if (inside) insideSpawned = did;
                else outsideSpawned = did;
                if (spawned != null) Fixtures.Destroy(spawned.gameObject);
                Fixtures.Destroy(go);
                yield return Flow.Frames(2);
            }
            if (!outsideSpawned)
                Report.Soft("P04h-creature-spawner", $"a CreatureSpawner ({prefab.name}) inside the square spawns nothing", "the control outside spawned nothing either: " + string.Join("; ", results));
            else
                Report.Check("P04h-creature-spawner", $"CreatureSpawner.Spawn ({prefab.name}) returns nothing inside the square and a creature in open country",
                    !insideSpawned, string.Join("; ", results));
        }

        // ─────────────────────────────────────────────────────────────
        // T3: the rotated rectangle - its ROTATION is enforced, no banner, border only; rules updated by the admin
        // ─────────────────────────────────────────────────────────────

        private static IEnumerator VisitRect()
        {
            Territory t = World.ByName(Layout.RectName);
            if (t == null)
            {
                Report.Fail("P05-rect", "visit the rectangle", "not in the index");
                yield break;
            }

            // Inside only if the yaw is honoured, and inside only if it is NOT: the rotated box must be the enforced one.
            // Which box holds which point is the DRIVER's own maths (the inverse of World.Local, the hammer's ruler
            // frame), never the mod's geometry: asking the mod's Shape.Contains let the mod decide whether half of
            // this check ran at all.
            Vector3 rotatedOnly = World.Local(t, t.HalfX - 1f, 0f);
            var axisOnly = new Vector3(t.X + t.HalfX - 1f, 0f, t.Z + 0f);
            Vector3 la = Quaternion.Inverse(Quaternion.Euler(0f, t.Yaw, 0f)) * new Vector3(axisOnly.x - t.X, 0f, axisOnly.z - t.Z);
            bool axisOnlyInRotated = Mathf.Abs(la.x) <= t.HalfX && Mathf.Abs(la.z) <= t.HalfZ;
            bool rotatedOnlyInAxisBox = Mathf.Abs(rotatedOnly.x - t.X) <= t.HalfX && Mathf.Abs(rotatedOnly.z - t.Z) <= t.HalfZ;
            bool rotatedRefused = !World.CheckAccess(rotatedOnly, false, flash: false);
            bool axisAllowed = World.CheckAccess(axisOnly, false, flash: false);
            Report.Check("P05a-rotation", $"the yaw {World.F(t.Yaw)} is enforced: refused at a point inside only the ROTATED box, allowed at one inside only the unrotated box",
                !axisOnlyInRotated && !rotatedOnlyInAxisBox && rotatedRefused && axisAllowed,
                $"rotatedOnly {World.P(rotatedOnly)} (in the unrotated box: {rotatedOnlyInAxisBox}) refused={rotatedRefused}; axisOnly {World.P(axisOnly)} (in the rotated box: {axisOnlyInRotated}) allowed={axisAllowed}" +
                (axisOnlyInRotated || rotatedOnlyInAxisBox ? " - the probe points do not separate the two boxes (layout)" : ""));

            Report.Check("P05b-updated-rules", "the admin's rules edit (Harvesting on, priority 5) reached the plain client",
                t.Has(TerritoryFlags.HarvestProtect) && t.Priority == 5 && t.Revision >= 2, World.Describe(t));

            float t0 = Time.realtimeSinceStartup;
            yield return World.Go(World.Local(t, 0f, 0f), 1.2f);
            Report.Check("P05c-presence", $"Presence.Current follows the player into '{t.Name}'", Presence.Current?.Id == t.Id, World.Describe(Presence.Current));
            List<string> shown = World.CenterSince(t0);
            World.BannerView slot = Features.EpicBanner ? World.BannerNow() : null;   // 0.2.0: the biome-found slot too
            Report.Check("P05d-no-banner", "no banner on entering a territory without Announce" + (Features.EpicBanner ? " (no centre message and none in the biome-found slot)" : ""),
                !shown.Any(s => s.Contains(t.Name)) && (slot == null || slot.Title != t.Name),
                $"shown: [{string.Join(" || ", shown)}] slot: {(slot != null ? slot.ToString() : "empty")}");
            Report.Check("P05e-access-refused", "access and build refused inside the rectangle",
                !World.CheckAccess(World.Local(t, -2f, 1f), false, false) && !World.CheckAccess(World.Local(t, -2f, 1f), true, false), "answered yes");
            yield return Fixtures.PickRefusedWithControl("P05f-harvest-refused", "harvesting refused inside the rectangle after the rules edit", World.Local(t, 4f, -1f));

            // Ground border near an edge. It is drawn headless (every run: TS_Border_<id> with its segments), so a
            // missing border is a FAIL: a SOFT here would hide a broken ShowBorder behind a green run.
            yield return World.Go(World.Local(t, 0f, t.HalfZ - 1f), 1.5f);
            yield return Flow.Seconds(1f);
            BorderOutline outline = Borders._outlines.TryGetValue(t.Id, out BorderOutline o) ? o : null;
            int segments = 0;
            if (outline != null)
                foreach (Transform seg in outline.transform)
                    if (seg.gameObject.activeSelf) segments++;
            if (outline != null && outline.gameObject.name == $"TS_Border_{t.Id}" && segments > 0)
                Report.Pass("P05g-border", $"ground border TS_Border_{t.Id} drawn near the edge with {segments} segment(s)");
            else
                Report.Fail("P05g-border", "ground border drawn near the edge", $"outline={(outline != null)} segments={segments} (ShowBorder is on, the player stands 1 m inside the edge)");

            // Map outline (SOFT: the minimap only updates with a graphics device).
            int areas = 0;
            if (Minimap.instance != null)
                areas = Minimap.instance.GetComponentsInChildren<RectTransform>(true).Count(r => r.name == "TS_TerritoryArea");
            if (areas > 0) Report.Pass("P05h-map", $"{areas} TS_TerritoryArea map outline object(s) exist under the minimap");
            else Report.Soft("P05h-map", "map outlines (TS_TerritoryArea) under the minimap",
                SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null
                    ? "unobservable with -nographics: Minimap.Update returns before UpdateMap when there is no graphics device"
                    : "none found");

            // 0.4.1: the minimap's name label keeps a name on one line (any client: the label exists headless too).
            if (MapFogShot.LabelAvailable) yield return Flow.Step("P41c-label-one-line", "the minimap's label keeps a name on one line", MapFogShot.LabelCheck("P41c-label-one-line"));
            // 0.4.1: the map's explored-ground mask, against the game's own explored cells (a graphics client only: the minimap updates only then).
            if (MapFogShot.WorldAvailable) yield return Flow.Step("P41b-fog-mask", "the map's explored mask follows the game's explored cells", MapFogShot.WorldCheck("P41b-fog-mask"));

            yield return World.Go(_home, 1f);
        }

        /// <summary>
        /// The negative control of the PvP notch checks (P03q, P04k): TS Comb A leaves PvP to the player (PvPRule.Unchanged) and
        /// announces itself, and the combine phase (Scenario020) waited for its Epic banner by its title, so World.BannerNow saw it
        /// and recorded it (World.BannersSeen). That banner must have carried NO notch - which is what shows the two checks above are
        /// not passing only because every banner has one. Runs only where the notch, the Epic banner and the combine phase all exist.
        /// </summary>
        private static void NoNotchControl()
        {
            if (!World.NotchSupported || !Features.EpicBanner || !Features.Combine) return;
            const string id = "P20f-banner-no-notch";
            string name = Layout.CombAName;
            string what = $"the Epic banner of '{name}' (PvP left to the player) carries no PvP notch";
            if (!World.BannersSeen.TryGetValue(name, out World.BannerView seen))
            {
                Report.Fail(id, what, $"the combine phase never read a banner titled '{name}', so the control did not run (banners seen: [{string.Join(", ", World.BannersSeen.Keys)}])");
                return;
            }
            Report.Check(id, what, !seen.HasNotch, $"the banner carried a notch: {seen}");
        }

        // ─────────────────────────────────────────────────────────────
        // A plain client never sends a request once the server has said "not an admin"
        // ─────────────────────────────────────────────────────────────

        private static IEnumerator NoRequest()
        {
            float t0 = Time.realtimeSinceStartup;
            float lastResult = TerritoryClient.LastResultAt;
            Vector3 p = Fixtures.OpenGround(140f);
            World.Run(IhCommand.Place(IhCommand.RulerShape.Circle, p, 5f, 5f, 0f));
            Territory circle = World.ByName(Layout.CircleName);
            if (circle != null) World.Run(IhCommand.Remove(new Vector3(circle.X, 0f, circle.Z)));
            yield return Flow.Seconds(2f);
            List<string> lines = World.ConsoleSince(t0);
            int refusals = lines.Count(l => l.Contains("Only server admins can change territories, and the server says you are not one."));
            bool anySent = lines.Any(l => l.Contains("sent to the server"));
            Report.Check("P06-no-request", "terr_place and terr_remove_at on the plain client are refused locally (the server said 'not an admin') and nothing is sent",
                refusals >= 2 && !anySent && TerritoryClient.LastResultAt == lastResult && !TerritoryClient.MayRequest(out string _),
                $"console: [{string.Join(" || ", lines)}]");
            RunDir.Signal("plain.no-request-done", new { at = DateTime.UtcNow.ToString("o") });
        }

        // ─────────────────────────────────────────────────────────────
        // PHASE 2: membership, reshape, removal - as the admin makes them
        // ─────────────────────────────────────────────────────────────

        internal static IEnumerator Phase2()
        {
            if (_home == Vector3.zero)
            {
                _home = World.Me.transform.position;
                JObject ready = RunDir.Read("plain.ready");
                if (ready != null) _home = new Vector3((float)ready["x"], (float)ready["y"], (float)ready["z"]);
                Fixtures.Home = _home;
            }
            var ok = new Box<bool>();
            yield return Flow.Until(() => TerritoryClient.HasSnapshot, T, ok);

            var b = new Box<JObject>();
            yield return RunDir.Wait("admin.t1-updated", 600f, b);
            yield return Flow.Step("P07-member", "member", Member());
            RunDir.Signal("plain.member-done");

            yield return RunDir.Wait("admin.t3-reshaped", 300f, b);
            yield return Flow.Step("P08-reshape", "reshape", Reshaped());
            RunDir.Signal("plain.reshape-done");

            yield return RunDir.Wait("admin.t2-removed", 300f, b);
            yield return Flow.Step("P09-removed", "removed", Removed());
            yield return Flow.Step("P20-combined", "combined territory", Scenario020.PlainPart(_home));
            NoNotchControl();
            // 0.3.0 only: the runic barrier as a visitor sees it; the member half is phase 3, after the admin's member add,
            // so "plain.final-done" (what the server waits for) moves to the end of Phase3 on such a DLL.
            yield return Flow.Step("P30-barriers", "runic barrier", Scenario030.PlainPart(_home));
            // 0.3.1 only: doors that close by themselves, closed by the server, watched from here.
            yield return Flow.Step("P31-doors", "auto-close doors", Scenario031.PlainPart(_home));
            // 0.4.0 only: sub-territories as a visitor sees them (the snapshot, the ward funnel, PvP and the banner's notch).
            yield return Flow.Step("P40-sub", "sub-territories", Scenario040.PlainPart(_home));
            RunDir.Signal(Features.Barrier ? "plain.held-done" : "plain.final-done");
        }

        /// <summary>0.3.0 only: TSPlain, made a member of the visitor barrier by the admin, walks in.</summary>
        internal static IEnumerator Phase3()
        {
            if (_home == Vector3.zero)
            {
                _home = World.Me.transform.position;
                JObject ready = RunDir.Read("plain.ready");
                if (ready != null) _home = new Vector3((float)ready["x"], (float)ready["y"], (float)ready["z"]);
                Fixtures.Home = _home;
            }
            var ok = new Box<bool>();
            yield return Flow.Until(() => TerritoryClient.HasSnapshot, T, ok);
            yield return Flow.Step("P30-member", "member passes the barrier", Scenario030.PlainMember());
            yield return World.Go(_home, 1f);
            RunDir.Signal("plain.final-done");
        }

        private static IEnumerator Member()
        {
            long me = World.Me.GetPlayerID();
            var ok = new Box<bool>();
            yield return Flow.Until(() => World.ByName(Layout.CircleName)?.IsMember(me) == true, T, ok);
            Territory t = World.ByName(Layout.CircleName);
            if (!Report.Check("P07a-member-snapshot", $"the admin's Update reached the plain client: TSPlain ({me}) is a member of '{Layout.CircleName}'",
                    ok.Value && t != null, World.Describe(t)))
                yield break;

            float t0 = Time.realtimeSinceStartup;
            yield return World.Go(World.Local(t, 0f, 0f), 1.2f);
            string expected = $"Welcome back to {t.Name}";
            if (Features.EpicBanner)
                yield return EpicEntry("P07b-banner-updated", t, expected, null, t0);
            else if (World.NotchSupported)
                Report.Check("P07b-banner-updated", $"the edited entry banner shows: '{expected}\\n{Line2Desc(false)}' (PvP forced off and the own choice already off: from 0.4.0 a forced entry names its state all the same)",
                    CentreIs(World.CenterNow, expected, false), $"HUD centre now '{World.CenterNow}', shown: [{string.Join(" || ", World.CenterSince(t0))}]");
            else
                Report.Check("P07b-banner-updated", $"the edited entry banner shows: '{expected}' (PvP forced off, own choice already off: no second line)",
                    World.CenterNow == expected, $"HUD centre now '{World.CenterNow}', shown: [{string.Join(" || ", World.CenterSince(t0))}]");
            Vector3 inside = World.Local(t, 2f, -1f);
            Report.Check("P07c-member-access", "as a member, PrivateArea.CheckAccess answers YES for access and for building",
                World.CheckAccess(inside, false) && World.CheckAccess(inside, true), "answered no");
            yield return Fixtures.DoorWithControl("P07d-member-door", "a door inside the circle opens for the member", World.Local(t, -2f, 3f), expectOpen: true);
            yield return Fixtures.ChestWithControl("P07e-member-chest", "a chest inside the circle opens for the member", World.Local(t, 1.5f, 1f), expectOpen: true);
            yield return Fixtures.PickTest("P07f-member-harvest", "a pickable inside the circle picks for the member", World.Local(t, -3f, -3f), expectPicked: true);
            yield return World.Go(_home, 1f);
        }

        private static IEnumerator Reshaped()
        {
            JObject layout = RunDir.Read("layout");
            var target = new Vector3((float)layout["rect2"]["x"], 0f, (float)layout["rect2"]["z"]);
            var old = new Vector3((float)layout["rect"]["x"], 0f, (float)layout["rect"]["z"]);
            var ok = new Box<bool>();
            yield return Flow.Until(() =>
            {
                Territory r = World.ByName(Layout.RectName);
                return r != null && Mathf.Abs(r.X - target.x) < 0.05f && Mathf.Abs(r.Z - target.z) < 0.05f;
            }, T, ok);
            Territory t = World.ByName(Layout.RectName);
            Report.Check("P08a-reshape-snapshot", $"the reshaped rectangle reached the plain client: {World.Describe(t)}",
                ok.Value && t != null && Mathf.Approximately(t.HalfX, Layout.ReshapeHalfX) && Mathf.Approximately(t.HalfZ, Layout.ReshapeHalfZ) &&
                Mathf.Abs(Mathf.DeltaAngle(t.Yaw, Layout.ReshapeYaw)) < 0.01f, World.Describe(t));
            if (t == null) yield break;
            bool oldOpen = World.CheckAccess(old, false, false);
            bool newRefused = !World.CheckAccess(World.Local(t, 1f, 1f), false, false);
            Report.Check("P08b-reshape-enforced", "the old ground is open country now and the new ground is protected",
                oldOpen && newRefused && TerritoryIndex.GoverningAt(old)?.Id != t.Id, $"old open={oldOpen} new refused={newRefused}");
            yield return World.Go(World.Local(t, 0f, 0f), 1.2f);
            Report.Check("P08c-reshape-presence", "Presence.Current follows the player onto the moved rectangle", Presence.Current?.Id == t.Id, World.Describe(Presence.Current));
            yield return World.Go(_home, 1f);
        }

        private static IEnumerator Removed()
        {
            JObject created = RunDir.Read("admin.created");
            int squareId = created != null && created["square"] != null ? (int)created["square"] : 0;
            var ok = new Box<bool>();
            yield return Flow.Until(() => World.ByName(Layout.SquareName) == null, T, ok);
            Report.Check("P09a-removed-snapshot", $"the removed square (#{squareId}) disappeared from the plain client's index",
                ok.Value && TerritoryIndex.Find(squareId) == null, string.Join("; ", TerritoryIndex.All.Select(World.Describe)));

            // The chest that was protected inside it: a hit lands now.
            WearNTear chest = _squareChest != null ? _squareChest : Fixtures.FindPieceNear(_squareChestAt, 3f);
            if (chest == null)
            {
                Report.Soft("P09b-removed-unprotected", "a piece in the removed square takes damage again", "the fixture chest could not be found again");
                yield break;
            }
            // After a solo re-login the chest is a fresh instance of a persistent ZDO: wait until the server has
            // handed its ownership to this client, or the hit would go to a machine without the piece loaded.
            var owned = new Box<bool>();
            yield return Flow.Until(() => chest != null && chest.m_nview != null && chest.m_nview.IsValid() && chest.m_nview.IsOwner(), 15f, owned);
            var before = new Box<float>(); var after = new Box<float>();
            yield return Fixtures.Hit(chest, 20f, before, after);
            Report.Check("P09b-removed-unprotected", $"the chest that was protected in the removed square takes damage now (health {World.F(before.Value)} -> {World.F(after.Value)})",
                after.Value < before.Value - 0.01f, $"governed by {World.Describe(TerritoryIndex.GoverningAt(chest.transform.position))}");
            Fixtures.Destroy(chest.gameObject);
        }
    }
}
