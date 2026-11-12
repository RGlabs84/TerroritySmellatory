using System.Collections.Generic;
using UnityEngine;

namespace TerroritySmellatory
{
    // THE ENTRY BANNER'S DECISIONS, AS PLAIN LOGIC.
    //
    // Which territory the local player counts as IN, and what to announce as that changes. Rules/Presence.cs reads
    // the player's position into it four times a second and does what it says: the banner and the goodbye line,
    // and forced PvP and the Sanctuary effect, which follow the same territory. Nothing here touches the running
    // game, so the harness (tests/BannerTests.cs) walks every case through it: in, out, A straight into B,
    // dithering on a border, cooldowns, a respawn, the stinger, both styles, death, a portal ride.
    //
    // 0.1.1 held no state at all: the player was in whatever governed their feet at that instant. On a border every
    // step across it re-announced the territory and flipped forced PvP, vanilla's centre notice and all, up to four
    // times a second. Three rules replace that.
    //
    //  HOLD. Walking in counts at once; walking out counts only HoldMetres clear of the ground, measured on the whole
    //  ground (TerritoryGround.DistanceOutside), so a combined territory is held along its whole outline and its
    //  seams are no edge at all. The hold is on the ONE territory the player counts as in, the one the rules follow,
    //  so the words and the rules cannot disagree: a safe zone stays safe for the same 3 m its banner does. It
    //  yields at once to a territory that OUTRANKS it (TerritoryIndex's precedence): from a kingdom into the market
    //  drawn inside it, the market has you on its first stone; back out, the market holds its 3 m as it would into
    //  open country. Yielding to ANY other territory instead would flap between the market and the kingdom at every
    //  step on the market's edge - the spam the hold exists to stop.
    //
    //  COOLDOWN. A territory whose entry was announced less than Cooldown seconds ago comes in silently. The goodbye
    //  follows the visit: said when this visit's entry was announced, otherwise at most once per cooldown. A path
    //  along a border that really crosses it, 3 m and more each way, gets one banner and one goodbye per cooldown,
    //  and a long stay that began silently still ends with its goodbye.
    //
    //  FREEZE. Dead, or riding the valkyrie in, nothing changes and nothing is said. Death is not leaving: no
    //  "Leaving X", and no PvP-restore notice over vanilla's "You died". Then there is no player at all until the
    //  respawn (Game._RequestRespawn destroys the old one). PlayerGone keeps where the player counts as being, the
    //  cooldowns and the territories seen through that gap, so a respawn inside the territory is no crossing at all,
    //  and a respawn anywhere else arrives without a goodbye. Between portals nothing changes either: the body is at
    //  the far end 2 s into the ride, which lasts 8 s and more, so the crossing waits for the landing and is an
    //  ordinary one there. Crossing at the jump, the banner's MaxWait would run out on the black screen.
    //
    //  NESTING (0.3.2). A territory drawn around another - a continent around its towns - is never left by walking out of
    //  the inner one. Stepping from the town back onto the continent therefore says goodbye to the TOWN and does not
    //  announce the continent again: it was announced when the player first came onto it, and re-announcing it at the
    //  edge of each of 150 towns would bury every other banner. Only a continent the player has not yet been told about
    //  this session is still announced then (they logged in, or came through a portal, inside a town). Walking the other
    //  way, from the continent into a town, was already right when the town announces itself: the town's banner, and no
    //  goodbye to the continent. A town that keeps quiet used to give the continent's goodbye ("Leaving ...") on the way
    //  in; that is gone too, since the player is still inside the continent.
    //
    // The Epic banner waits for a moment it can be seen in (Presence says when it cannot: vanilla's own banner up,
    // the HUD hidden, a black screen). ONE request waits at a time; it is dropped when the player is no longer in
    // that territory or after MaxWait, and the cooldown and the stinger's "seen" mark start only when it shows - a
    // banner nobody saw does not silence the next one.
    //
    // THE PVP NOTCH AND THE PVP LINE (0.4.0). A territory that FORCES PvP says so wherever it announces itself: a small gem
    // on the Epic banner, green where PvP is held off (a safe zone) and red where it is held on (a war zone), and none where
    // the player's own toggle stays in charge. The Simple style, the Epic banner's fallback and the goodbye say it as ONE
    // coloured line on the centre message. BannerNotch, at the end of this file, holds the decisions and the colours;
    // Rules/Presence.cs draws them.

    /// <summary>The player's banner settings (TSConfig), as plain values: BepInEx's config types are out of the harness's reach.</summary>
    internal struct BannerSettings
    {
        /// <summary>EnterLeaveBanners: announce anything at all.</summary>
        public bool Enabled;

        /// <summary>EntryBannerStyle Epic: an entry is the big title, shown once it can be seen. Otherwise one centre line, at once.</summary>
        public bool Epic;

        /// <summary>EntrySound FirstEntry: the stinger plays with each territory's first banner this session.</summary>
        public bool StingerFirst;

        /// <summary>EntrySound EveryEntry: the stinger plays with every banner.</summary>
        public bool StingerEvery;

        /// <summary>BannerCooldownSeconds; 0 turns the cooldown off.</summary>
        public float Cooldown;
    }

    /// <summary>One look at where the player stands (<see cref="BannerPlan.Observe"/>, then Time, Frozen and Blocked).</summary>
    internal struct BannerInput
    {
        /// <summary>Seconds, on a clock that never goes back.</summary>
        public float Time;

        /// <summary>Dead, in the intro, or between portals: nothing changes, so a teleport crosses on landing.</summary>
        public bool Frozen;

        /// <summary>The Epic banner could not be seen right now; a waiting one keeps waiting.</summary>
        public bool Blocked;

        /// <summary>The territory governing where the player stands; 0 for open country.</summary>
        public int Governing;

        /// <summary>It announces itself (TerritoryFlags.Announce).</summary>
        public bool GoverningAnnounces;

        /// <summary>It takes precedence over the territory the player counts as in, whose hold then yields to it.</summary>
        public bool GoverningOutranks;

        /// <summary>
        /// It is drawn AROUND the territory the player counts as in: that territory outranks it and its middle lies in this
        /// one's ground. Walking out of the inner one onto this one is no entry - the player never left it.
        /// </summary>
        public bool GoverningEnclosesEffective;

        /// <summary>
        /// It is drawn INSIDE the territory the player counts as in: it outranks that one and its middle lies in that one's
        /// ground. Walking onto it is no leaving - the player is still inside the bigger one - whether or not it announces itself.
        /// </summary>
        public bool GoverningInsideEffective;

        /// <summary>How far outside the territory the player counts as in they stand; infinity once it no longer exists.</summary>
        public float Outside;

        /// <summary>That territory announces itself - from its last record, once an admin has deleted it.</summary>
        public bool EffectiveAnnounces;
    }

    /// <summary>What one look decided.</summary>
    internal struct BannerStep
    {
        /// <summary>The territory the player counts as in now (Presence.Current); 0 for open country.</summary>
        public int Effective;

        /// <summary>It changed on this look. A banner still up names somewhere the player no longer is.</summary>
        public bool Changed;

        /// <summary>Announce entering this territory now; 0 for nothing.</summary>
        public int Entry;

        /// <summary>...as one centre line (the Simple style) rather than the Epic banner.</summary>
        public bool EntryLine;

        /// <summary>...with vanilla's biome-discovery sound.</summary>
        public bool Stinger;

        /// <summary>Say goodbye to this territory now, on the centre line; 0 for nothing.</summary>
        public int Leave;
    }

    internal sealed class BannerPlan
    {
        /// <summary>How far outside its ground the player still counts as in a territory, in metres.</summary>
        internal const float HoldMetres = 3f;

        /// <summary>How long an Epic banner may wait for a moment it can be seen in before it is dropped, in seconds.</summary>
        internal const float MaxWait = 8f;

        private int _effective;
        private bool _visitAnnounced;
        private bool _arriving;
        private int _pending;
        private float _pendingSince;

        // Per territory id, for the session. PlayerGone keeps them through a death; only ResetSession clears them.
        private readonly Dictionary<int, float> _entered = new Dictionary<int, float>();     // its entry last announced
        private readonly Dictionary<int, float> _farewelled = new Dictionary<int, float>();  // its goodbye last said
        private readonly HashSet<int> _seen = new HashSet<int>();                             // its entry announced at all

        /// <summary>The territory the player counts as in; 0 for open country.</summary>
        internal int Effective => _effective;

        /// <summary>The territory whose Epic banner is waiting to be seen; 0 for none.</summary>
        internal int Pending => _pending;

        /// <summary>
        /// Reads where the player stands, the way the rules read it: what governs there is TerritoryIndex's one
        /// answer, and the territory the player counts as in is ranked against it by the same precedence order.
        /// <paramref name="lastKnown"/> is the last record of the territory the player counts as in, still wanted for
        /// its goodbye after an admin deletes it. Time, Frozen and Blocked are the caller's to fill in.
        /// </summary>
        internal BannerInput Observe(Vector3 position, Territory lastKnown, out Territory governing, out Territory current)
        {
            governing = TerritoryIndex.GoverningAt(position);
            current = null;
            bool governingFirst = false;
            if (_effective != 0)
            {
                // All is in precedence order: whichever of the two comes first would govern where both reach.
                IReadOnlyList<Territory> all = TerritoryIndex.All;
                for (int i = 0; i < all.Count && current == null; i++)
                {
                    if (all[i] == governing) governingFirst = true;
                    if (all[i].Id == _effective) current = all[i];
                }
            }

            Territory known = current ?? (lastKnown != null && lastKnown.Id == _effective ? lastKnown : null);
            return new BannerInput
            {
                Governing = governing?.Id ?? 0,
                GoverningAnnounces = governing != null && governing.Has(TerritoryFlags.Announce),
                GoverningOutranks = governing != null && current != null && governing != current && governingFirst,
                // The one the player counts as in comes first in the precedence order, so it would govern wherever the two
                // overlap, and the territory governing the player's feet holds its middle: the second is drawn around the first.
                // Drawn around it by the ground, or by the link: a sub-territory (Territory.ParentId, 0.4.0) is inside its parent
                // whether or not its middle happens to lie in the parent's ground (a coastal town's rectangle can reach out to sea).
                GoverningEnclosesEffective = governing != null && current != null && governing != current && !governingFirst &&
                                             (governing.Ground.Contains(current.Center) || TerritoryIndex.IsInside(current.Id, governing.Id)),
                GoverningInsideEffective = governing != null && current != null && governing != current && governingFirst &&
                                           (current.Ground.Contains(governing.Center) || TerritoryIndex.IsInside(governing.Id, current.Id)),
                Outside = current != null ? current.Ground.DistanceOutside(position) : float.PositiveInfinity,
                EffectiveAnnounces = known != null && known.Has(TerritoryFlags.Announce),
            };
        }

        /// <summary>One look: moves the player between territories by the rules above, and says what to announce.</summary>
        internal BannerStep Step(in BannerInput look, in BannerSettings settings)
        {
            var step = new BannerStep();
            if (!look.Frozen)
            {
                int next = Next(look);
                if (next != _effective) Cross(ref step, next, look, settings);
                _arriving = false;
            }
            Deliver(ref step, look, settings);
            step.Effective = _effective;
            return step;
        }

        /// <summary>
        /// No player: between a death and the respawn, and before the first spawn. Where the player counts as being,
        /// the cooldowns and the territories seen are all kept, so the respawn is judged against where they died. Only
        /// a banner still waiting is dropped - it was for a player who is gone.
        /// </summary>
        internal void PlayerGone()
        {
            _pending = 0;
            _arriving = true;
        }

        /// <summary>A new session (ZNet.OnDestroy): nothing carries over into the next world.</summary>
        internal void ResetSession()
        {
            _effective = 0;
            _visitAnnounced = false;
            _arriving = false;
            _pending = 0;
            _pendingSince = 0f;
            _entered.Clear();
            _farewelled.Clear();
            _seen.Clear();
        }

        private int Next(in BannerInput look)
        {
            int governing = look.Governing;
            if (governing == _effective || _effective == 0) return governing;   // no change, or walking in: at once
            if (governing != 0 && look.GoverningOutranks) return governing;      // into ground that outranks it: at once
            // Out of it, into open country or onto ground it outranks: held until HoldMetres clear. A deleted
            // territory is infinitely far away, so it lets go at once.
            return look.Outside < HoldMetres ? _effective : governing;
        }

        private void Cross(ref BannerStep step, int next, in BannerInput look, in BannerSettings settings)
        {
            int left = _effective;
            bool leftAnnounced = _visitAnnounced;
            _effective = next;
            _visitAnnounced = false;
            // The waiting banner's re-check: it names where the player no longer is. Every change of territory comes
            // through here, so one that is still waiting is always for where the player stands.
            _pending = 0;
            step.Changed = true;
            if (!settings.Enabled) return;

            // The territory walked into owns the moment whenever it announces itself, shown or on cooldown: stepping
            // straight from one into the next says where you ARE, never where you were. Not when it is the one drawn
            // around the one just left and the player has been told about it already: that was no entry (NESTING above),
            // and the goodbye below is the town's.
            bool resumed = look.GoverningEnclosesEffective && _seen.Contains(next);
            if (next != 0 && look.GoverningAnnounces && !resumed)
            {
                if (Cooling(_entered, next, look.Time, settings.Cooldown)) return;
                if (!settings.Epic)
                {
                    Announce(ref step, next, look.Time, settings, line: true);
                    return;
                }
                _pending = next;
                _pendingSince = look.Time;
                return;
            }

            // Nor is it a goodbye to walk onto a territory drawn inside the one being left, one that keeps quiet included:
            // "Leaving the continent" on the way into a player's plot would be wrong twice over.
            if (left == 0 || !look.EffectiveAnnounces || _arriving || look.GoverningInsideEffective) return;
            if (!leftAnnounced && Cooling(_farewelled, left, look.Time, settings.Cooldown)) return;
            step.Leave = left;
            _farewelled[left] = look.Time;
        }

        private void Deliver(ref BannerStep step, in BannerInput look, in BannerSettings settings)
        {
            if (_pending == 0) return;
            if (!settings.Enabled || !settings.Epic || look.Time - _pendingSince > MaxWait)
            {
                _pending = 0;
                return;
            }
            if (look.Blocked || look.Frozen) return;
            Announce(ref step, _pending, look.Time, settings, line: false);
            _pending = 0;
        }

        private void Announce(ref BannerStep step, int id, float time, in BannerSettings settings, bool line)
        {
            bool first = _seen.Add(id);
            step.Entry = id;
            step.EntryLine = line;
            step.Stinger = !line && (settings.StingerEvery || (settings.StingerFirst && first));
            _entered[id] = time;
            _visitAnnounced = true;
        }

        private static bool Cooling(Dictionary<int, float> said, int id, float time, float cooldown) =>
            cooldown > 0f && said.TryGetValue(id, out float at) && time - at < cooldown;
    }

    /// <summary>What a territory's PvP rule puts on its banner: nothing, or the state the territory holds.</summary>
    internal enum NotchMark
    {
        /// <summary>PvPRule.Unchanged: the player's own toggle stays in charge, so the banner says nothing about PvP.</summary>
        None,

        /// <summary>PvPRule.ForcedOff, a safe zone. Green.</summary>
        Off,

        /// <summary>PvPRule.ForcedOn, a war zone. Red.</summary>
        On,
    }

    /// <summary>
    /// The banner's PvP indicator as plain values: the mark a PvP rule earns, the colours, and the centre message's one PvP line.
    /// Rules/Presence.cs draws the notch and shows the line; every decision and every colour is here, where the harness can
    /// hold them.
    /// </summary>
    /// <remarks>
    /// THE COLOURS. "Green off, red on", as Rohan asked. The Epic banner is a black backdrop (alpha 0.8) with amber braids
    /// (#FFB900) and a white title, so the gem has to hold against black and against amber at once, and in any light, because
    /// the backdrop is not opaque. Saturated hues of middling brightness do that, and the dark rim does the rest: the gem is set
    /// in the braid's knot, and the rim keeps its edge from melting into the amber. WCAG contrast ratios: green 9.4:1 and red
    /// 4.5:1 against the rim, 10.3:1 and 4.9:1 against black, the rim 11.1:1 against the braid - every pair above the 3:1 that
    /// graphical objects are held to. Green is also twice as bright as red (relative luminance 0.46 against 0.20), so the two
    /// stay apart for a player who cannot tell the hues. tests/BannerTests.cs pins all of it.
    /// </remarks>
    internal static class BannerNotch
    {
        /// <summary>PvP off. #3FCF5A.</summary>
        internal static readonly Color32 Green = new Color32(0x3F, 0xCF, 0x5A, 255);

        /// <summary>PvP on. #E23B3B.</summary>
        internal static readonly Color32 Red = new Color32(0xE2, 0x3B, 0x3B, 255);

        /// <summary>The gem's rim, behind it: a warm near-black. #140E08.</summary>
        internal static readonly Color32 Rim = new Color32(0x14, 0x0E, 0x08, 255);

        /// <summary>The mark a PvP rule earns. Anything but the two forced rules leaves the player's toggle alone: none.</summary>
        internal static NotchMark For(PvPRule rule) =>
            rule == PvPRule.ForcedOff ? NotchMark.Off : rule == PvPRule.ForcedOn ? NotchMark.On : NotchMark.None;

        /// <summary>The colour of a PvP state: red for on, green for off, on the notch and in the centre line alike.</summary>
        internal static Color32 Colour(bool pvpOn) => pvpOn ? Red : Green;

        /// <summary>A colour as rich text writes it: #RRGGBB.</summary>
        internal static string Hex(Color32 colour) => "#" + colour.r.ToString("X2") + colour.g.ToString("X2") + colour.b.ToString("X2");

        /// <summary>
        /// The centre message's PvP line: vanilla's own notice ($msg_pvpon / $msg_pvpoff, what Player.SetPVP shows), in the colour
        /// of its state. A $token inside a colour tag is localized all the same: Localization.FindNextWord (assembly_guiutils)
        /// ends a word at the first space, newline, bracket or punctuation mark of its m_endChars, '&lt;' and '&gt;' among them, so the
        /// word stops at the closing tag's '&lt;'; vanilla's own tooltips write "&lt;color=orange&gt;$item_noteleport&lt;/color&gt;" the
        /// same way. MessageHud.ShowMessage localizes the whole text.
        /// </summary>
        internal static string PvPLine(bool pvpOn) =>
            "<color=" + Hex(Colour(pvpOn)) + ">" + (pvpOn ? "$msg_pvpon" : "$msg_pvpoff") + "</color>";

        /// <summary>
        /// Which PvP state, if any, the centre line of an ENTRY names. Ground that forces PvP always names the state it holds, even
        /// when this tick did not change it: a player who was already in that state would otherwise be told nothing, where the
        /// Epic banner's notch tells them every time. Ground that leaves the toggle alone names the state only when this tick
        /// changed it (the player's own choice handed back on the way in from forced ground), which is when vanilla's own notice
        /// fired as well. The GOODBYE keeps that second rule alone: Presence passes it the state it changed to, or nothing.
        /// </summary>
        /// <param name="rule">The PvP rule of the territory entered.</param>
        /// <param name="pvpChangedTo">The state this tick switched the player to, or null if it did not change.</param>
        /// <returns>The state to name (true is on), or null for no PvP line.</returns>
        internal static bool? EntryLine(PvPRule rule, bool? pvpChangedTo)
        {
            NotchMark mark = For(rule);
            return mark == NotchMark.None ? pvpChangedTo : mark == NotchMark.On;
        }

        /// <summary>
        /// The centre message with its PvP line: the text, then ONE more line, or the text as it is when there is nothing to
        /// name. The only place the line is added, so a message cannot carry two.
        /// </summary>
        internal static string WithPvPLine(string text, bool? pvpOn) =>
            pvpOn.HasValue ? text + "\n" + PvPLine(pvpOn.Value) : text;
    }
}
