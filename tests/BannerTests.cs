using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace TerroritySmellatory.Tests
{
    // 0.2.0: the entry banner's decisions (Rules/BannerPlan.cs) - which territory the player counts as in, and what
    // is announced as that changes - walked through on real territories and the real index, fed exactly as
    // Rules/Presence.cs feeds it: one look every quarter second, Observe, then Step.
    internal static partial class Program
    {
        static partial void BannerGroups()
        {
            Run("banner: walking in counts at once, walking out 3 m past the edge", BannerEnterLeave);
            Run("banner: dithering on an edge - no flapping, no second banner; a real zigzag once per cooldown", BannerDither);
            Run("banner: A straight into B - B's banner only; the hold yields only to what outranks it", BannerAToB);
            Run("banner: cooldown - silent within it, announced after; the goodbye follows the visit", BannerCooldown);
            Run("banner: death freezes; a respawn inside is no crossing, one elsewhere says no goodbye", BannerDeath);
            Run("banner: the stinger - first entry, every entry, off; it goes with the banner shown", BannerStinger);
            Run("banner: Simple style - one centre line at once, the same anti-spam", BannerSimple);
            Run("banner: waiting to be seen - one request, re-checked, dropped after 8 s", BannerWaiting);
            Run("banner: a portal ride - nothing crosses until the landing; a slow one still gets its banner", BannerPortal);
            Run("banner: switched off, silent territories, deleted and combined underfoot", BannerSwitches);
            Run("banner: a combined territory - its seams are no edge, its notch is held like any edge", BannerCompound);
            Run("banner: nesting (0.3.2) - a continent, a region, a town: the inner one's goodbye, the outer one not announced again", BannerNested);
            Run("banner: the PvP notch (0.4.0) - green for a safe zone, red for a war zone, none where the toggle stays the player's; colours that read", BannerNotchMarks);
            Run("banner: the PvP line (0.4.0) - ONE coloured line, a forced entry always names its state, a goodbye only a change", BannerPvPLine);
        }

        /// <summary>
        /// The local player, driven through the plan as Presence drives it. Everything announced is written down the
        /// way the player would see it: "enter 3 +sound" (the Epic banner), "line 3" (the Simple style's centre
        /// line), "leave 3" (the goodbye).
        /// </summary>
        private sealed class BannerWalk
        {
            public readonly BannerPlan Plan = new BannerPlan();
            public BannerSettings Settings = new BannerSettings { Enabled = true, Epic = true, StingerFirst = true, Cooldown = 90f };
            public float Time = 1000f;
            public bool Dead;
            public bool Intro;
            public bool Teleporting;   // Player.IsTeleporting: Presence freezes the plan, and EntryBanner.Blocked holds the banner
            public bool Blocked;
            public int Changes;
            public BannerStep Last;
            public readonly List<string> Said = new List<string>();
            public readonly List<float> SaidAt = new List<float>();
            private Territory _record;

            public int Effective => Plan.Effective;

            public string Log => string.Join(", ", Said);

            /// <summary>One look from (x, z), a quarter second after the last.</summary>
            public BannerStep At(float x, float z)
            {
                Time += 0.25f;
                int was = Plan.Effective;
                BannerInput look = Plan.Observe(new Vector3(x, 0f, z), _record, out Territory governing, out Territory current);
                look.Time = Time;
                look.Frozen = Dead || Intro || Teleporting;
                look.Blocked = Blocked || Dead || Intro || Teleporting;
                BannerStep step = Plan.Step(look, Settings);

                Territory now = step.Effective == 0 ? null : step.Effective == was ? current : governing;
                if (now != null) _record = now;
                if (step.Changed) Changes++;
                if (step.Leave != 0) Note("leave " + step.Leave);
                if (step.Entry != 0) Note((step.EntryLine ? "line " : "enter ") + step.Entry + (step.Stinger ? " +sound" : ""));
                Last = step;
                return step;
            }

            /// <summary>Walks in a straight line, a look every half metre (a jog, 2 m/s).</summary>
            public void Walk(float x0, float z0, float x1, float z1)
            {
                float length = (float)Math.Sqrt((x1 - x0) * (x1 - x0) + (z1 - z0) * (z1 - z0));
                int n = Math.Max(1, (int)Math.Ceiling(length / 0.5f));
                for (int i = 1; i <= n; i++) At(x0 + (x1 - x0) * i / n, z0 + (z1 - z0) * i / n);
            }

            public void Stay(float x, float z, float seconds)
            {
                for (float t = 0f; t < seconds; t += 0.25f) At(x, z);
            }

            /// <summary>No player at all: the gap between Game._RequestRespawn destroying the body and the respawn.</summary>
            public void Gone(float seconds)
            {
                for (float t = 0f; t < seconds; t += 0.25f)
                {
                    Time += 0.25f;
                    Plan.PlayerGone();
                }
            }

            /// <summary>Dies here, lies dead a while, and is gone until the respawn.</summary>
            public void Die(float x, float z)
            {
                Dead = true;
                Stay(x, z, 3f);
                Gone(10f);
                Dead = false;
            }

            private void Note(string what)
            {
                Said.Add(what);
                SaidAt.Add(Time);
            }
        }

        private static Territory BannerBox(int id, float x0, float x1, float z0, float z1,
            TerritoryFlags flags = TerritoryFlags.Announce, int priority = 0) =>
            new Territory
            {
                Id = id, Name = "B" + id, Kind = TerritoryShapeKind.Rectangle, X = (x0 + x1) / 2f, Z = (z0 + z1) / 2f,
                HalfX = (x1 - x0) / 2f, HalfZ = (z1 - z0) / 2f, Flags = flags, Priority = priority
            };

        private static void BannerEnterLeave()
        {
            TerritoryIndex.Set(new[] { Circle(1, 0f, 0f, 20f, TerritoryFlags.Announce) });
            var w = new BannerWalk();
            w.At(-40f, 0f);
            Check(w.Effective == 0 && w.Said.Count == 0, "open country: nothing");

            w.At(-20.5f, 0f);
            Check(w.Effective == 0, "half a metre short of the edge is outside: walking IN is never held");
            w.At(-19.5f, 0f);
            Check(w.Effective == 1 && w.Last.Changed, "the first look inside counts as in");
            Check(w.Log == "enter 1 +sound", $"the Epic banner at once, with the sound of a first entry [{w.Log}]");

            w.Walk(-19.5f, 0f, 22.5f, 0f);
            Check(w.Effective == 1 && w.Said.Count == 1, "through and 2.5 m out of the far side: still in, nothing said");
            w.At(22.9f, 0f);
            Check(w.Effective == 1, "2.9 m out: still in");
            w.At(23.1f, 0f);
            Check(w.Effective == 0 && w.Last.Changed, "3.1 m out: out");
            Check(w.Log == "enter 1 +sound, leave 1", $"one banner in, one goodbye out [{w.Log}]");
            Check(w.Changes == 2, $"two crossings in all (got {w.Changes})");

            w.Walk(23.1f, 0f, 20.5f, 0f);
            Check(w.Effective == 0 && w.Said.Count == 2, "coming back, not yet inside: still out - the hold only ever keeps a player IN");
        }

        private static void BannerDither()
        {
            TerritoryIndex.Set(new[] { Circle(1, 0f, 0f, 20f, TerritoryFlags.Announce) });
            var w = new BannerWalk();
            w.Walk(-40f, 0f, 10f, 0f);
            int changes = w.Changes;

            // Fidgeting on the line: a step each way every quarter second, for a minute.
            bool letGo = false;
            for (int i = 0; i < 240; i++)
            {
                w.At(i % 2 == 0 ? 19.5f : 22.5f, 0f);
                letGo |= w.Effective != 1;
            }
            Check(!letGo && w.Changes == changes, "a minute of stepping back and forth over the edge never lets go");
            Check(w.Log == "enter 1 +sound", $"...and says nothing more [{w.Log}]");

            // A path that really crosses, 5 m either side of the edge, every look for three minutes.
            float firstBanner = w.SaidAt[0];
            float start = w.Time;
            for (int i = 0; i < 720; i++) w.At(i % 2 == 0 ? 25f : 15f, 0f);
            Check(w.Changes - changes == 720, $"the zigzag really crosses on every look (got {w.Changes - changes} crossings)");

            var banners = new List<float> { firstBanner };
            var goodbyes = new List<float>();
            for (int i = 0; i < w.Said.Count; i++)
            {
                if (w.SaidAt[i] <= start) continue;
                if (w.Said[i].StartsWith("enter", StringComparison.Ordinal)) banners.Add(w.SaidAt[i]);
                else if (w.Said[i].StartsWith("leave", StringComparison.Ordinal)) goodbyes.Add(w.SaidAt[i]);
            }
            bool spaced = true;
            for (int i = 1; i < banners.Count; i++) spaced &= banners[i] - banners[i - 1] >= 90f;
            Check(spaced, $"banners at least a cooldown apart: [{string.Join(", ", banners.Select(b => (b - firstBanner).ToString("0.##")))}] s");
            Check(banners.Count == 3, $"three minutes of crossings: the first banner and two more (got {banners.Count})");
            Check(goodbyes.Count == 3, $"one goodbye after each announced visit, none after the silent ones (got {goodbyes.Count})");
            Check(w.Said.All(s => s == "enter 1 +sound" || s == "enter 1" || s == "leave 1") && w.Said.Count(s => s.EndsWith("+sound")) == 1,
                $"only the first banner had the sound [{w.Log}]");
        }

        private static void BannerAToB()
        {
            // Two fields side by side, the same size: on a tie the older one (lower id) outranks.
            Territory a = BannerBox(1, -40f, 0f, -20f, 20f), b = BannerBox(2, 0f, 40f, -20f, 20f);
            TerritoryIndex.Set(new[] { a, b });
            var w = new BannerWalk();
            w.At(-30f, 0f);
            w.Walk(-30f, 0f, 2.5f, 0f);
            Check(w.Effective == 1, "2.5 m into B, A still holds: A outranks B, so B's ground is held like open country");
            w.At(3.25f, 0f);
            Check(w.Effective == 2, "3 m clear of A: in B");
            Check(w.Log == "enter 1 +sound, enter 2 +sound", $"B's banner, and no goodbye from A - B owns the moment [{w.Log}]");

            w.Walk(3.25f, 0f, 0.25f, 0f);
            Check(w.Effective == 2, "back to A's line: still B");
            w.At(-0.25f, 0f);
            Check(w.Effective == 1, "a step over it, A has the player at once: A outranks B");
            Check(w.Said.Count == 2, $"A came back within its cooldown, silently, and B said no goodbye [{w.Log}]");

            // B is a war camp with Priority 1: it outranks A, and takes the player on its first stone.
            b.Priority = 1;
            TerritoryIndex.Set(new[] { a, b });
            w = new BannerWalk();
            w.At(-30f, 0f);
            w.Walk(-30f, 0f, 0.5f, 0f);
            Check(w.Effective == 2, "B outranks A: half a metre into B is in B");
            w.Walk(0.5f, 0f, -2.5f, 0f);
            Check(w.Effective == 2, "back out, B holds 2.5 m into A");
            w.At(-3.25f, 0f);
            Check(w.Effective == 1, "3 m clear of B: in A again");

            // A market drawn inside a kingdom (smaller, so it outranks): in at once, out 3 m later, and its edge
            // never flaps between the two.
            TerritoryIndex.Set(new[] { Circle(10, 0f, 0f, 100f, TerritoryFlags.Announce), Circle(11, 50f, 0f, 10f, TerritoryFlags.Announce) });
            w = new BannerWalk();
            w.At(0f, 0f);
            w.Walk(0f, 0f, 40.5f, 0f);
            Check(w.Effective == 11, "the market's first stone");
            bool flapped = false;
            for (int i = 0; i < 200; i++)
            {
                w.At(i % 2 == 0 ? 38.5f : 41f, 0f);
                flapped |= w.Effective != 11;
            }
            Check(!flapped && w.Changes == 2, "fidgeting on the market's edge never hands the player back to the kingdom");
            w.Walk(41f, 0f, 37.25f, 0f);
            Check(w.Effective == 11, "2.75 m out of the market: still the market");
            w.At(36.75f, 0f);
            Check(w.Effective == 10, "3 m out: the kingdom again");
            // 0.3.0 and before: the kingdom's own cooldown silenced it here and the market said nothing. Since 0.3.2 the kingdom is not
            // entered at all - the market is drawn inside it, so the player never left it - and the market says its goodbye.
            Check(w.Log == "enter 10 +sound, enter 11 +sound, leave 11", $"the market says its goodbye, and the kingdom - never left - is not announced again [{w.Log}]");
        }

        private static void BannerCooldown()
        {
            TerritoryIndex.Set(new[] { Circle(1, 0f, 0f, 20f, TerritoryFlags.Announce) });
            var w = new BannerWalk();
            w.At(-30f, 0f);
            w.At(0f, 0f);
            float shown = w.Time;
            w.At(30f, 0f);
            Check(w.Log == "enter 1 +sound, leave 1", $"in with a banner, out with a goodbye [{w.Log}]");

            w.Stay(30f, 0f, 30f);
            w.At(0f, 0f);
            Check(w.Effective == 1 && w.Said.Count == 2, "back 30 s later: in, silently");
            w.At(30f, 0f);
            Check(w.Effective == 0 && w.Said.Count == 2, "out again with no goodbye: the visit came in silently and the last goodbye was 30 s ago");

            w.Stay(30f, 0f, 90f - (w.Time - shown));
            w.At(0f, 0f);
            Check(w.Time - shown >= 90f && w.Log == "enter 1 +sound, leave 1, enter 1",
                $"a cooldown after the last banner: the banner again, without the sound this time [{w.Log}]");
            w.At(30f, 0f);
            Check(w.Log == "enter 1 +sound, leave 1, enter 1, leave 1", $"...and its goodbye [{w.Log}]");

            w.At(0f, 0f);
            Check(w.Said.Count == 4, "straight back in: silent");
            w.Stay(0f, 0f, 120f);
            w.At(30f, 0f);
            Check(w.Said.Count == 5 && w.Said[4] == "leave 1", $"a long stay that came in silently still says goodbye [{w.Log}]");

            // Cooldown 0: every entry is announced, and every goodbye said.
            w = new BannerWalk();
            w.Settings.Cooldown = 0f;
            for (int i = 0; i < 3; i++)
            {
                w.At(0f, 0f);
                w.At(30f, 0f);
            }
            Check(w.Log == "enter 1 +sound, leave 1, enter 1, leave 1, enter 1, leave 1", $"no cooldown: every time [{w.Log}]");

            // A new session (ZNet.OnDestroy) forgets it all: the cooldown, the goodbye's, the sound's "seen".
            w = new BannerWalk();
            w.At(0f, 0f);
            w.At(30f, 0f);
            w.At(0f, 0f);
            w.Plan.ResetSession();
            Check(w.Effective == 0 && w.Plan.Pending == 0, "the session reset puts the player nowhere");
            w.At(0f, 0f);
            w.At(30f, 0f);
            Check(w.Log == "enter 1 +sound, leave 1, enter 1 +sound, leave 1",
                $"a new session announces it afresh a second later, sound and goodbye and all [{w.Log}]");
            w.Plan.ResetSession();
            w.Blocked = true;
            w.At(0f, 0f);
            w.Stay(0f, 0f, 9f);
            w.Blocked = false;
            w.At(30f, 0f);
            Check(w.Said.Count == 5 && w.Said[4] == "leave 1",
                $"...and a visit whose banner nobody saw gets its goodbye: the last session's goodbye is forgotten too [{w.Log}]");
        }

        private static void BannerDeath()
        {
            TerritoryIndex.Set(new[] { Circle(1, 0f, 0f, 20f, TerritoryFlags.Announce), Circle(2, 300f, 0f, 20f, TerritoryFlags.Announce) });

            // Dead: nothing changes and nothing is said, wherever the body goes.
            var w = new BannerWalk();
            w.At(0f, 0f);
            w.Dead = true;
            w.Stay(0f, 0f, 2f);
            w.At(150f, 0f);
            Check(w.Effective == 1 && w.Changes == 1 && w.Log == "enter 1 +sound", $"dead: no goodbye, no crossing, even with the body 130 m out [{w.Log}]");

            // Respawn inside, long past the cooldown: no crossing at all.
            w.Gone(200f);
            w.Dead = false;
            w.Stay(5f, 0f, 2f);
            Check(w.Effective == 1 && w.Changes == 1 && w.Said.Count == 1, "respawned inside 200 s later: never left, so no banner");

            // Die inside, respawn in open country: out, without a goodbye - death is not leaving.
            w = new BannerWalk();
            w.At(0f, 0f);
            w.Die(0f, 0f);
            w.At(150f, 0f);
            Check(w.Effective == 0 && w.Last.Changed, "respawned in open country: out");
            Check(w.Log == "enter 1 +sound", $"...and no goodbye [{w.Log}]");
            w.Walk(150f, 0f, 150f, 5f);
            w.At(0f, 0f);
            Check(w.Log == "enter 1 +sound", $"walking home again within the cooldown: silent [{w.Log}]");

            // Die at home, respawn at a bed in another territory: its banner, and no goodbye from home.
            w = new BannerWalk();
            w.At(0f, 0f);
            w.Die(0f, 0f);
            w.At(300f, 0f);
            Check(w.Effective == 2 && w.Log == "enter 1 +sound, enter 2 +sound", $"respawned in another territory: its banner [{w.Log}]");

            // The cooldown and the territories seen outlive a death; only the session reset clears them.
            w = new BannerWalk();
            w.At(0f, 0f);
            w.At(150f, 0f);
            w.Die(150f, 0f);
            w.At(0f, 0f);
            Check(w.Effective == 1 && w.Log == "enter 1 +sound, leave 1", $"died outside, respawned into it within its cooldown: silent [{w.Log}]");
            w.At(150f, 0f);
            w.Stay(150f, 0f, 90f);
            w.Die(150f, 0f);
            w.At(0f, 0f);
            Check(w.Said.Last() == "enter 1", $"respawned into it past its cooldown: the banner, without the sound - seen before the death [{w.Log}]");

            // Riding the valkyrie in over two territories says nothing; landing in one announces it.
            w = new BannerWalk { Intro = true };
            w.Walk(-100f, 0f, 320f, 0f);
            w.Walk(320f, 0f, 5f, 0f);
            Check(w.Effective == 0 && w.Said.Count == 0 && w.Changes == 0, "the intro: frozen, whatever it flies over");
            w.Intro = false;
            w.At(5f, 0f);
            Check(w.Effective == 1 && w.Log == "enter 1 +sound", $"dropped off inside: the banner [{w.Log}]");

            // A banner waiting when the player dies is never shown to a corpse, and the respawn inside does not bring it back.
            w = new BannerWalk { Blocked = true };
            w.At(0f, 0f);
            w.Dead = true;
            w.Blocked = false;
            w.Stay(0f, 0f, 3f);
            Check(w.Said.Count == 0, "not shown while dead");
            w.Gone(10f);
            w.Dead = false;
            w.Stay(0f, 0f, 3f);
            Check(w.Said.Count == 0 && w.Plan.Pending == 0 && w.Effective == 1, "gone with the body; the respawn inside is no crossing");

            // However short the gap: a banner that waited for a player who then vanished is not shown to the next one.
            w = new BannerWalk { Blocked = true };
            w.At(0f, 0f);
            w.Gone(1f);
            w.Blocked = false;
            w.Stay(0f, 0f, 2f);
            Check(w.Said.Count == 0 && w.Plan.Pending == 0, "no player for a second: the waiting banner went with them");
        }

        private static void BannerStinger()
        {
            TerritoryIndex.Set(new[] { Circle(1, 0f, 0f, 20f, TerritoryFlags.Announce), Circle(2, 100f, 0f, 20f, TerritoryFlags.Announce) });

            string Tour(bool first, bool every)
            {
                var w = new BannerWalk();
                w.Settings.StingerFirst = first;
                w.Settings.StingerEvery = every;
                w.At(0f, 0f);
                w.At(50f, 0f);
                w.Stay(50f, 0f, 100f);
                w.At(0f, 0f);
                w.At(100f, 0f);
                return w.Log;
            }

            string log = Tour(true, false);
            Check(log == "enter 1 +sound, leave 1, enter 1, enter 2 +sound", $"FirstEntry: each territory's first banner of the session [{log}]");
            log = Tour(false, true);
            Check(log == "enter 1 +sound, leave 1, enter 1 +sound, enter 2 +sound", $"EveryEntry: every banner [{log}]");
            log = Tour(false, false);
            Check(log == "enter 1, leave 1, enter 1, enter 2", $"Off: never [{log}]");

            // The sound goes with a banner SHOWN: one dropped unseen leaves it for the next.
            var d = new BannerWalk { Blocked = true };
            d.At(0f, 0f);
            d.Stay(0f, 0f, 9f);
            d.At(50f, 0f);
            d.Blocked = false;
            d.At(0f, 0f);
            Check(d.Log == "leave 1, enter 1 +sound", $"the first banner actually seen has the sound [{d.Log}]");
        }

        private static void BannerSimple()
        {
            TerritoryIndex.Set(new[] { Circle(1, 0f, 0f, 20f, TerritoryFlags.Announce) });
            var w = new BannerWalk { Blocked = true };
            w.Settings.Epic = false;
            w.Settings.StingerEvery = true;
            w.At(-30f, 0f);
            w.At(0f, 0f);
            Check(w.Log == "line 1" && w.Plan.Pending == 0, $"Simple: one centre line at once, whatever would block the banner, never the sound [{w.Log}]");
            w.Walk(0f, 0f, 22.5f, 0f);
            Check(w.Effective == 1 && w.Said.Count == 1, "held 2.5 m out, as the Epic style is");
            w.At(30f, 0f);
            w.At(0f, 0f);
            w.At(30f, 0f);
            Check(w.Log == "line 1, leave 1", $"the same cooldown: back in and out within it, silently [{w.Log}]");
        }

        private static void BannerWaiting()
        {
            TerritoryIndex.Set(new[] { Circle(1, 0f, 0f, 20f, TerritoryFlags.Announce), Circle(2, 100f, 0f, 20f, TerritoryFlags.Announce) });

            // Blocked (vanilla's banner up, a black screen...): in at once, the banner waits, and shows when it can.
            var w = new BannerWalk { Blocked = true };
            w.At(0f, 0f);
            Check(w.Effective == 1 && w.Said.Count == 0 && w.Plan.Pending == 1, "blocked: in at once, the banner waits");
            w.Stay(0f, 0f, 3f);
            w.Blocked = false;
            w.At(0f, 0f);
            Check(w.Log == "enter 1 +sound" && w.Plan.Pending == 0, $"shown the moment it can be seen [{w.Log}]");

            // Blocked for more than 8 s (BannerPlan.MaxWait): dropped for good, and it put nothing on cooldown.
            w = new BannerWalk { Blocked = true };
            w.At(0f, 0f);
            w.Stay(0f, 0f, 7.5f);
            Check(w.Plan.Pending == 1, "still waiting just short of 8 s");
            w.Stay(0f, 0f, 1f);
            Check(w.Plan.Pending == 0, "dropped after 8 s");
            w.Blocked = false;
            w.Stay(0f, 0f, 5f);
            Check(w.Said.Count == 0, "never shown late, for a moment long gone");
            w.At(50f, 0f);
            w.At(0f, 0f);
            Check(w.Log == "leave 1, enter 1 +sound", $"a banner nobody saw starts no cooldown [{w.Log}]");

            // Walked out before it could be seen: dropped - it would name somewhere the player has left.
            w = new BannerWalk { Blocked = true };
            w.At(0f, 0f);
            w.At(50f, 0f);
            Check(w.Plan.Pending == 0 && w.Effective == 0, "out of it: its banner is dropped");
            w.Blocked = false;
            w.Stay(50f, 0f, 2f);
            Check(w.Said.All(s => !s.StartsWith("enter", StringComparison.Ordinal)), $"and never shown [{w.Log}]");

            // Straight from one into another while blocked: only the second waits, and only it shows.
            w = new BannerWalk { Blocked = true };
            w.At(0f, 0f);
            w.At(100f, 0f);
            Check(w.Plan.Pending == 2, "the new territory's banner replaces the old one's");
            w.Blocked = false;
            w.At(100f, 0f);
            Check(w.Log == "enter 2 +sound", $"only where the player is gets a banner [{w.Log}]");

            // The player switches to the Simple style, or banners off, while one waits: dropped, not shown late in a
            // style they no longer want.
            foreach (bool off in new[] { false, true })
            {
                w = new BannerWalk { Blocked = true };
                w.At(0f, 0f);
                if (off) w.Settings.Enabled = false;
                else w.Settings.Epic = false;
                w.Blocked = false;
                w.Stay(0f, 0f, 2f);
                Check(w.Plan.Pending == 0 && w.Said.Count == 0, $"{(off ? "banners switched off" : "the Simple style chosen")} while it waited: dropped [{w.Log}]");
            }
        }

        private static void BannerPortal()
        {
            // A portal, the map's teleport or goto (Player.UpdateTeleport, distant): the body is at the far end 2 s in,
            // the ride lands no sooner than 8 s in - later while a busy destination loads - and the black screen takes
            // about 0.7 s more to lift. So the plan holds until the landing (Presence.Tick): crossing at the jump, the
            // banner's 8 s wait would run out on the black screen.
            TerritoryIndex.Set(new[] { Circle(1, 0f, 0f, 20f, TerritoryFlags.Announce), Circle(2, 100f, 0f, 20f, TerritoryFlags.Announce) });

            var w = new BannerWalk();
            w.At(-500f, 0f);
            w.Teleporting = true;
            w.Stay(-500f, 0f, 2f);
            w.Stay(0f, 0f, 10f);
            Check(w.Effective == 0 && w.Said.Count == 0 && w.Plan.Pending == 0, "10 s at the far end, still teleporting: nothing has crossed");
            w.Teleporting = false;
            w.Blocked = true;
            w.Stay(0f, 0f, 0.75f);
            Check(w.Effective == 1 && w.Plan.Pending == 1, "landed: in, and the banner waits for the black screen to lift");
            w.Blocked = false;
            w.At(0f, 0f);
            Check(w.Log == "enter 1 +sound", $"a 12 s ride still arrives with its banner and its sound [{w.Log}]");

            // From one announcing territory to another: the arrival's banner, which owns the moment.
            w = new BannerWalk();
            w.At(100f, 0f);
            w.Teleporting = true;
            w.Stay(100f, 0f, 2f);
            w.Stay(0f, 0f, 12f);
            w.Teleporting = false;
            w.Blocked = true;
            w.Stay(0f, 0f, 0.75f);
            w.Blocked = false;
            w.At(0f, 0f);
            Check(w.Log == "enter 2 +sound, enter 1 +sound", $"territory to territory by portal: the arrival's banner [{w.Log}]");

            // Out into open country: the goodbye on landing, not at the jump, where it would fade out on the black screen.
            w = new BannerWalk();
            w.At(0f, 0f);
            w.Teleporting = true;
            w.Stay(0f, 0f, 2f);
            w.Stay(-500f, 0f, 8f);
            Check(w.Effective == 1 && w.Log == "enter 1 +sound", $"still teleporting: still in, no goodbye yet [{w.Log}]");
            w.Teleporting = false;
            float landed = w.Time;
            w.At(-500f, 0f);
            Check(w.Effective == 0 && w.Log == "enter 1 +sound, leave 1" && w.SaidAt[1] > landed, $"landed: out, with the goodbye [{w.Log}]");
        }

        private static void BannerSwitches()
        {
            TerritoryIndex.Set(new[] { Circle(1, 0f, 0f, 20f, TerritoryFlags.Announce) });

            // Banners off: the player is still held in and let out - the rules follow them - but nothing is said.
            var w = new BannerWalk();
            w.Settings.Enabled = false;
            w.At(0f, 0f);
            w.At(22f, 0f);
            Check(w.Effective == 1, "off: still held 2 m out");
            w.At(30f, 0f);
            Check(w.Effective == 0 && w.Changes == 2 && w.Said.Count == 0 && w.Plan.Pending == 0, "off: in and out, silently");
            w.Settings.Enabled = true;
            w.At(0f, 0f);
            Check(w.Log == "enter 1 +sound", $"switched on: the next entry is the first one [{w.Log}]");

            // A territory that does not announce itself is held like any other, says nothing, and walking from an
            // announcing one into it says goodbye to the first.
            TerritoryIndex.Set(new[] { Circle(1, 0f, 0f, 20f, TerritoryFlags.Announce), Circle(2, 45f, 0f, 25f, TerritoryFlags.None) });
            w = new BannerWalk();
            w.At(0f, 0f);
            w.Walk(0f, 0f, 45f, 0f);
            Check(w.Effective == 2 && w.Log == "enter 1 +sound, leave 1", $"into the quiet one: the town's goodbye [{w.Log}]");
            w.Walk(45f, 0f, 90f, 0f);
            Check(w.Effective == 0 && w.Said.Count == 2, "out of the quiet one: nothing");

            // Deleted underfoot: out at once, and the goodbye still knows what it was.
            TerritoryIndex.Set(new[] { Circle(5, 0f, 0f, 20f, TerritoryFlags.Announce) });
            w = new BannerWalk();
            w.At(0f, 0f);
            TerritoryIndex.Set(Array.Empty<Territory>());
            w.At(0f, 0f);
            Check(w.Effective == 0 && w.Log == "enter 5 +sound, leave 5", $"deleted: out at once, with its goodbye [{w.Log}]");

            // Combined into its neighbour (the target keeps its id, the other is gone): in the target at once.
            Territory keep = BannerBox(6, -40f, 0f, -20f, 20f), absorbed = BannerBox(7, 0f, 40f, -20f, 20f);
            TerritoryIndex.Set(new[] { keep, absorbed });
            w = new BannerWalk();
            w.At(20f, 0f);
            Territory combined = keep.Clone();
            combined.ExtraParts.Add(absorbed.Shape);
            TerritoryIndex.Set(new[] { combined });
            w.At(20f, 0f);
            Check(w.Effective == 6 && w.Log == "enter 7 +sound, enter 6 +sound", $"combined away: in the territory that took it, with its banner [{w.Log}]");
        }

        private static void BannerNested()
        {
            // A continent (1) with a region inside it (2) and a town inside that (3). The smaller one governs where they overlap.
            TerritoryIndex.Set(new[]
            {
                BannerBox(1, -1000f, 1000f, -1000f, 1000f),
                BannerBox(2, 0f, 400f, -100f, 100f),
                BannerBox(3, 100f, 140f, -20f, 20f),
            });

            // IN, one level at a time: each banner on the way in, and no goodbye to the one it is inside.
            var w = new BannerWalk();
            w.At(-1500f, 0f);
            w.Walk(-1500f, 0f, -900f, 0f);
            Check(w.Effective == 1 && w.Log == "enter 1 +sound", $"onto the continent: its banner [{w.Log}]");
            w.Walk(-900f, 0f, 50f, 0f);
            Check(w.Effective == 2 && w.Log == "enter 1 +sound, enter 2 +sound", $"into the region: its banner, and no goodbye to the continent [{w.Log}]");
            w.Walk(50f, 0f, 120f, 0f);
            Check(w.Effective == 3 && w.Log == "enter 1 +sound, enter 2 +sound, enter 3 +sound", $"into the town: its banner, and no goodbye to the region [{w.Log}]");

            // OUT, one level at a time: the inner one's goodbye, and the outer one - already announced - NOT announced again.
            w.Walk(120f, 0f, 160f, 0f);
            Check(w.Effective == 2, "out of the town: in the region");
            Check(w.Log == "enter 1 +sound, enter 2 +sound, enter 3 +sound, leave 3", $"the town's goodbye only: the region is not entered again [{w.Log}]");
            w.Walk(160f, 0f, 450f, 0f);
            Check(w.Effective == 1, "out of the region: on the continent");
            Check(w.Log == "enter 1 +sound, enter 2 +sound, enter 3 +sound, leave 3, leave 2", $"the region's goodbye only: no second 'Entering' for the continent [{w.Log}]");
            Check(w.Changes == 5, $"every crossing is still a crossing, announced or not (got {w.Changes})");

            // Much later, and back through the same town: still not one banner for the continent. The town and the region come
            // back after the cooldown, as any territory does.
            w.Stay(450f, 0f, 200f);
            w.Walk(450f, 0f, 120f, 0f);
            w.Walk(120f, 0f, 450f, 0f);
            Check(w.Effective == 1 && !w.Said.Skip(5).Any(s => s == "enter 1" || s == "enter 1 +sound"),
                $"a second round trip never announces the continent again [{w.Log}]");

            // FIRST TOLD ABOUT BY LEAVING: logged in inside the town, so the continent was never announced. Walking out of the
            // town through the region onto the continent announces each of them, as an entry - the player is told where they ARE.
            w = new BannerWalk();
            w.At(120f, 0f);
            Check(w.Effective == 3 && w.Log == "enter 3 +sound", $"logged in inside the town [{w.Log}]");
            w.Walk(120f, 0f, 160f, 0f);
            Check(w.Effective == 2 && w.Log == "enter 3 +sound, enter 2 +sound", $"out into the region it has never been told of: announced, and no goodbye [{w.Log}]");
            w.Walk(160f, 0f, 450f, 0f);
            Check(w.Effective == 1 && w.Log == "enter 3 +sound, enter 2 +sound, enter 1 +sound", $"and onto the continent likewise [{w.Log}]");

            // A SILENT OUTER TERRITORY: the town says goodbye all the same, and nothing is said for the continent.
            TerritoryIndex.Set(new[]
            {
                BannerBox(1, -1000f, 1000f, -1000f, 1000f, TerritoryFlags.None),
                BannerBox(3, 100f, 140f, -20f, 20f),
            });
            w = new BannerWalk();
            w.At(120f, 0f);
            w.Walk(120f, 0f, 160f, 0f);
            Check(w.Effective == 1 && w.Log == "enter 3 +sound, leave 3", $"a continent that does not announce itself: the town's goodbye alone [{w.Log}]");

            // A SILENT TERRITORY INSIDE AN ANNOUNCING ONE (a player's plot in a continent): walking onto it is no leaving of the continent.
            TerritoryIndex.Set(new[]
            {
                BannerBox(1, -1000f, 1000f, -1000f, 1000f),
                BannerBox(3, 100f, 140f, -20f, 20f, TerritoryFlags.None),
            });
            w = new BannerWalk();
            w.At(-1500f, 0f);
            w.Walk(-1500f, 0f, -900f, 0f);
            w.Walk(-900f, 0f, 120f, 0f);
            Check(w.Effective == 3 && w.Log == "enter 1 +sound", $"onto a silent plot inside the continent: no goodbye to the continent [{w.Log}]");
            w.Walk(120f, 0f, 160f, 0f);
            Check(w.Effective == 1 && w.Log == "enter 1 +sound", $"and off it again: nothing said, the continent was never left [{w.Log}]");
            w.Walk(160f, 0f, 1200f, 0f);
            Check(w.Effective == 0 && w.Log == "enter 1 +sound, leave 1", $"leaving the continent itself still says goodbye, once [{w.Log}]");

            // NOT NESTED: two neighbours, and two that overlap without either holding the other's middle, are still entered by their own banners.
            TerritoryIndex.Set(new[] { BannerBox(1, 0f, 100f, 0f, 100f), BannerBox(2, 100f, 200f, 0f, 100f) });
            w = new BannerWalk();
            w.At(50f, 50f);
            w.Walk(50f, 50f, 150f, 50f);
            w.Stay(150f, 50f, 100f);   // past the cooldown, so the way back is not silenced by it
            w.Walk(150f, 50f, 50f, 50f);
            Check(w.Effective == 1 && w.Log == "enter 1 +sound, enter 2 +sound, enter 1", $"next-door territories: straight from one banner to the next, both ways [{w.Log}]");

            // PRIORITY: when the big one is made to win, there is no inner territory to leave and nothing changes.
            TerritoryIndex.Set(new[] { BannerBox(1, -1000f, 1000f, -1000f, 1000f, TerritoryFlags.Announce, priority: 5), BannerBox(3, 100f, 140f, -20f, 20f) });
            w = new BannerWalk();
            w.At(-900f, 0f);
            w.Walk(-900f, 0f, 120f, 0f);
            w.Walk(120f, 0f, 300f, 0f);
            Check(w.Effective == 1 && w.Log == "enter 1 +sound" && w.Changes == 1, $"the big territory forced to win: one banner for the whole walk [{w.Log}]");
        }

        private static void BannerCompound()
        {
            // An L: a 40 x 20 bar along X and a 20 x 40 leg along Z, overlapping in the corner square.
            var t = new Territory { Id = 1, Name = "L", Flags = TerritoryFlags.Announce };
            t.SetPart(0, new TerritoryShape(TerritoryShapeKind.Rectangle, 0f, 0f, 20f, 10f, 0f));    // x -20..20, z -10..10
            t.ExtraParts.Add(new TerritoryShape(TerritoryShapeKind.Rectangle, 10f, 20f, 10f, 20f, 0f)); // x 0..20, z 0..40
            TerritoryIndex.Set(new[] { t });

            var w = new BannerWalk();
            w.At(-15f, 0f);
            w.Walk(-15f, 0f, 10f, 0f);
            w.Walk(10f, 0f, 10f, 35f);
            Check(w.Effective == 1 && w.Changes == 1 && w.Log == "enter 1 +sound", $"bar to leg across the seam: no crossing at all [{w.Log}]");

            // Into the notch of the L, 2 m from both parts: held by the whole ground.
            w.Walk(10f, 35f, -2f, 12f);
            Check(w.Effective == 1, "in the notch, 2 m from both parts: still in");
            w.At(-5f, 14f);
            Check(w.Effective == 0 && w.Log == "enter 1 +sound, leave 1", $"4 m from the nearer part: out [{w.Log}]");

            // A far part of the same territory is the same territory: walking between two parts, the player is in it
            // on each and nowhere in between, and it is announced once.
            var two = new Territory { Id = 2, Name = "Harbour", Flags = TerritoryFlags.Announce };
            two.SetPart(0, new TerritoryShape(TerritoryShapeKind.Circle, 0f, 0f, 15f, 15f, 0f));
            two.ExtraParts.Add(new TerritoryShape(TerritoryShapeKind.Circle, 60f, 0f, 15f, 15f, 0f));
            TerritoryIndex.Set(new[] { two });
            w = new BannerWalk();
            w.At(0f, 0f);
            w.Walk(0f, 0f, 30f, 0f);
            Check(w.Effective == 0, "half way between the two parts: out");
            w.Walk(30f, 0f, 60f, 0f);
            Check(w.Effective == 2 && w.Log == "enter 2 +sound, leave 2", $"its other part, within the cooldown: in, silently [{w.Log}]");
        }

        // THE PVP NOTCH (0.4.0): Rules/BannerPlan.cs's BannerNotch. Rules/Presence.cs draws the gem and shows the line; what is
        // decided - the mark a rule earns, the colours, the words - is here, so these hold it.

        /// <summary>WCAG relative luminance of an sRGB colour.</summary>
        private static double Luminance(Color32 c)
        {
            double Linear(byte v)
            {
                double x = v / 255.0;
                return x <= 0.04045 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Linear(c.r) + 0.7152 * Linear(c.g) + 0.0722 * Linear(c.b);
        }

        /// <summary>WCAG contrast ratio of two colours, 1 to 21.</summary>
        private static double Contrast(Color32 a, Color32 b)
        {
            double la = Luminance(a), lb = Luminance(b);
            return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
        }

        private static int Rgb(Color32 c) => (c.r << 16) | (c.g << 8) | c.b;

        private static void BannerNotchMarks()
        {
            // The rule table. PvPRule is persisted, append only: a fourth rule must be given its mark on purpose, so this fails
            // until somebody decides it (BannerNotch.For gives anything it does not know none).
            Check(Enum.GetValues(typeof(PvPRule)).Length == 3, "PvPRule has the three rules this table decides: Unchanged, ForcedOff, ForcedOn");
            Check(BannerNotch.For(PvPRule.Unchanged) == NotchMark.None, "players choose: no notch");
            Check(BannerNotch.For(PvPRule.ForcedOff) == NotchMark.Off, "forced off (a safe zone): the off mark");
            Check(BannerNotch.For(PvPRule.ForcedOn) == NotchMark.On, "forced on (a war zone): the on mark");
            Check(BannerNotch.For((PvPRule)99) == NotchMark.None, "a rule this build does not know earns no notch");

            // Green off, red on - the colour follows the STATE, so the notch and the centre line cannot disagree.
            Color32 off = BannerNotch.Colour(BannerNotch.For(PvPRule.ForcedOff) == NotchMark.On);
            Color32 on = BannerNotch.Colour(BannerNotch.For(PvPRule.ForcedOn) == NotchMark.On);
            Check(Rgb(off) == Rgb(BannerNotch.Green) && Rgb(on) == Rgb(BannerNotch.Red), "forced off is green and forced on is red");
            Check(off.g > off.r && off.g > off.b, "the off colour is a green: its green channel leads");
            Check(on.r > on.g && on.r > on.b, "the on colour is a red: its red channel leads");
            Check(Rgb(BannerNotch.Green) == 0x3FCF5A && Rgb(BannerNotch.Red) == 0xE23B3B && Rgb(BannerNotch.Rim) == 0x140E08,
                "the colours are the designed ones: green #3FCF5A, red #E23B3B, rim #140E08");
            Check(off.a == 255 && on.a == 255 && BannerNotch.Rim.a == 255, "all three are opaque");

            // They read: against the dark rim behind the gem, against the black backdrop of the banner (alpha 0.8, so the world
            // shows a little through it), and the rim against the amber braid (#FFB900, read from the prefab) it sits across. 3:1
            // is what WCAG asks of a graphical object.
            var amber = new Color32(255, 185, 0, 255);
            var black = new Color32(0, 0, 0, 255);
            Check(Contrast(BannerNotch.Green, BannerNotch.Rim) >= 3.0 && Contrast(BannerNotch.Red, BannerNotch.Rim) >= 3.0,
                $"the gem stands out from its rim: green {Contrast(BannerNotch.Green, BannerNotch.Rim):0.0}:1, red {Contrast(BannerNotch.Red, BannerNotch.Rim):0.0}:1");
            Check(Contrast(BannerNotch.Green, black) >= 3.0 && Contrast(BannerNotch.Red, black) >= 3.0,
                $"the gem stands out from the backdrop: green {Contrast(BannerNotch.Green, black):0.0}:1, red {Contrast(BannerNotch.Red, black):0.0}:1");
            Check(Contrast(BannerNotch.Rim, amber) >= 3.0, $"the rim stands out from the amber braid: {Contrast(BannerNotch.Rim, amber):0.0}:1");
            Check(Contrast(BannerNotch.Green, BannerNotch.Red) >= 2.0,
                $"green is twice as bright as red, so the two stay apart for a player who cannot tell the hues: {Contrast(BannerNotch.Green, BannerNotch.Red):0.0}:1");
            Check(Luminance(BannerNotch.Rim) < 0.02, "the rim is near-black");
        }

        /// <summary>
        /// The words a copy of vanilla's Localization.FindNextWord (assembly_guiutils, 1.0.17) would translate in a text: from each
        /// '$', up to the next character of m_endChars. The characters are the decompile's, copied.
        /// </summary>
        private static List<string> VanillaTokens(string text)
        {
            const string endChars = " (){}[]+-!?/\\\\&%,.:-=<>\n";
            var words = new List<string>();
            int from = 0;
            while (from < text.Length - 1)
            {
                int start = text.IndexOf('$', from);
                if (start == -1) break;
                int end = text.IndexOfAny(endChars.ToCharArray(), start);
                words.Add(end != -1 ? text.Substring(start + 1, end - start - 1) : text.Substring(start + 1));
                from = end != -1 ? end : text.Length;
            }
            return words;
        }

        private static void BannerPvPLine()
        {
            // The line: vanilla's own notice, in the colour of its state.
            Check(BannerNotch.PvPLine(true) == "<color=#E23B3B>$msg_pvpon</color>", $"PvP on: red, in vanilla's own words [{BannerNotch.PvPLine(true)}]");
            Check(BannerNotch.PvPLine(false) == "<color=#3FCF5A>$msg_pvpoff</color>", $"PvP off: green, in vanilla's own words [{BannerNotch.PvPLine(false)}]");
            Check(BannerNotch.Hex(BannerNotch.Rim) == "#140E08" && BannerNotch.Hex(new Color32(0, 10, 255, 255)) == "#000AFF", "colours are written as #RRGGBB, two digits each");

            // Vanilla's localizer must find exactly that one token inside the colour tag, and no other: the word ends at the tag's '<'.
            Check(VanillaTokens(BannerNotch.PvPLine(true)).SequenceEqual(new[] { "msg_pvpon" }), "the localizer reads the one token $msg_pvpon out of the coloured line");
            Check(VanillaTokens(BannerNotch.PvPLine(false)).SequenceEqual(new[] { "msg_pvpoff" }), "the localizer reads the one token $msg_pvpoff out of the coloured line");
            Check(VanillaTokens("Welcome\n" + BannerNotch.PvPLine(true)).SequenceEqual(new[] { "msg_pvpon" }), "and still when the line follows another, as the centre message does");

            // The message as ShowMessage receives it: the text, then ONE line, or the text as it is.
            Check(BannerNotch.WithPvPLine("Entering Hrafnborg", true) == "Entering Hrafnborg\n<color=#E23B3B>$msg_pvpon</color>", "a centre message with the on line");
            Check(BannerNotch.WithPvPLine("Leaving Hrafnborg", false) == "Leaving Hrafnborg\n<color=#3FCF5A>$msg_pvpoff</color>", "a centre message with the off line");
            Check(BannerNotch.WithPvPLine("Entering Hrafnborg", null) == "Entering Hrafnborg", "no state to name: the message is untouched");
            foreach (bool state in new[] { true, false })
            {
                string message = BannerNotch.WithPvPLine("Entering <color=#FFD700>Hrafnborg</color>", state);
                Check(message.Split('\n').Length == 2 && message.Split(new[] { "$msg_pvp" }, StringSplitOptions.None).Length == 2,
                    $"exactly one PvP line, on the second line [{message.Replace("\n", "\\n")}]");
                Check(message.Split('<').Length - 1 == message.Split('>').Length - 1, "the tags are balanced: every '<' has its '>'");
            }

            // What an ENTRY names. Forced ground names its state every time - the player may already have been in it - and the
            // rule wins over a change that says otherwise; ground that leaves the toggle alone names a state only when the tick
            // changed it (the player's own choice handed back on the way in from forced ground).
            bool? none = null;
            Check(BannerNotch.EntryLine(PvPRule.ForcedOff, none) == false, "forced off, the player was already off: the off line all the same");
            Check(BannerNotch.EntryLine(PvPRule.ForcedOff, false) == false, "forced off, switched to off: the off line");
            Check(BannerNotch.EntryLine(PvPRule.ForcedOn, none) == true, "forced on, the player was already on: the on line all the same");
            Check(BannerNotch.EntryLine(PvPRule.ForcedOn, true) == true, "forced on, switched to on: the on line");
            Check(BannerNotch.EntryLine(PvPRule.ForcedOn, false) == true && BannerNotch.EntryLine(PvPRule.ForcedOff, true) == false,
                "the territory's rule decides what an entry names, never a stray change");
            Check(BannerNotch.EntryLine(PvPRule.Unchanged, none) == null, "players choose and nothing changed: no PvP line, as before");
            Check(BannerNotch.EntryLine(PvPRule.Unchanged, true) == true && BannerNotch.EntryLine(PvPRule.Unchanged, false) == false,
                "players choose, and the entry handed their own choice back: the state it changed to, as before");
            foreach (PvPRule rule in Enum.GetValues(typeof(PvPRule)))
            {
                // The two things a forced territory shows must agree: the notch's colour is the entry line's colour.
                bool? line = BannerNotch.EntryLine(rule, null);
                NotchMark mark = BannerNotch.For(rule);
                Check((mark == NotchMark.None) == !line.HasValue && (!line.HasValue || (line.Value == (mark == NotchMark.On))),
                    $"{rule}: the notch and the entry line name the same state (or both name none)");
            }
        }
    }
}
