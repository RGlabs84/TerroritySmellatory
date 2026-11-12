using System;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace TerroritySmellatory
{
    /// <summary>
    /// While the player is inside a territory, the minimap's biome name gives way to the territory's name, the
    /// way it changes when you walk into a new biome.
    /// </summary>
    /// <remarks>
    /// The minimap's label only (m_biomeNameSmall, Small map mode). The large map's label is a readout of the
    /// ground under the cursor that vanilla rewrites every frame; a territory's name there would contradict it,
    /// so it stays vanilla's.
    ///
    /// Vanilla writes the small label only when the player's biome CHANGES: Minimap.UpdateBiome compares
    /// player.GetCurrentBiomeData() with its cached m_biome and, when they differ, writes the name and fires the
    /// label's "pulse" animation. That cache is the whole mechanism here, set in a prefix:
    ///   - inside a territory it is set to the player's own biome, so vanilla sees no change, neither rewrites
    ///     nor pulses whatever biome border the player crosses in there, and the postfix writes the name;
    ///   - on the frame the player enters (or steps straight into another territory) it is set to null
    ///     instead: vanilla runs its own change path - text and pulse - and the postfix writes the territory's
    ///     name over the biome's before the frame is drawn. The pulse is vanilla's own call on its own Animator,
    ///     so this build needs no reference to Unity's animation module for it;
    ///   - on the way out it is set to null ONCE, and vanilla writes the real biome name back and pulses, that
    ///     same frame - an alt-biome or Better Continents sector's name included, because vanilla names it.
    ///     Nulling it every frame would rewrite and pulse the label every frame.
    ///
    /// The name is rich text (the label has rich text on): tinted with the territory's colour lerped half-way
    /// to white, as the map names are, and always ON ONE LINE: inside a no-break tag, shrunk in steps until it
    /// fits the label's width, and cut short with dots below a readable size (NameFit) - a long name used to wrap
    /// into lines drawn on top of each other. A tag rather than the label's own colour, scale or wrapping,
    /// because the pulse's Animator owns the first two and vanilla's label is not ours to reconfigure, and a tag
    /// needs no undoing: vanilla's next write replaces it.
    ///
    /// Server Devcommands appends coordinates to this label in a default-priority postfix, every frame. This
    /// patch's methods run at Priority.High, before it, and compare the label's text each frame rather than
    /// assigning it, taking "our text, a new line, anything" as ours: SDC's lines stay, and a write that is not
    /// ours (Localization.OnLanguageChange sets the biome name directly) is put right the next frame.
    /// </remarks>
    internal static class MapBiomeLabel
    {
        private static Minimap _owner;

        /// <summary>This frame's territory: decided by the prefix, written by the postfix.</summary>
        private static Territory _want;

        /// <summary>
        /// The territory whose name the minimap's label is showing, or null. MapOverlay leaves out its own name for
        /// that territory on the minimap, so the name is not shown twice; read from here so the two cannot
        /// disagree. Minimap.Update runs UpdateMap before UpdateBiome, so the overlay reads the previous frame's.
        /// </summary>
        internal static Territory Showing => _want;

        /// <summary>m_biome is being held for a territory, and must be handed back once when that ends.</summary>
        private static bool _holding;

        /// <summary>The territory the label last pulsed for; 0 for none.</summary>
        private static int _pulsedFor;

        /// <summary>
        /// This frame's prefix asked vanilla for a pulse, and what _pulsedFor was before it did: if vanilla's body
        /// turns out not to have run, the postfix hands the pulse back for the next frame the body does run.
        /// </summary>
        private static bool _pulseAsked;
        private static int _pulsedBefore;

        private static Territory _textFor;
        private static int _textRevision;
        private static string _text;

        internal static void BeforeUpdate(Minimap map, Player player, bool originalRuns)
        {
            if (map != _owner)
            {
                // A new world has a new Minimap; whatever was held belonged to the old one.
                _owner = map;
                _holding = false;
                _pulsedFor = 0;
                _textFor = null;
                _text = null;
            }

            _want = null;
            _pulseAsked = false;
            Territory t = Wanted();
            if (t == null)
            {
                if (!_holding) return;
                // Left, or the name was switched off: hand the label back, once. A null cache differs from any
                // biome, so vanilla writes the biome's name back, and pulses it, below.
                map.m_biome = null;
                _holding = false;
                _pulsedFor = 0;
                return;
            }

            _want = t;
            _holding = true;
            // Only where vanilla's body will run and the label is on screen: a pulse asked for anywhere else
            // would be spent on nothing. Once per territory, whatever becomes of it - asked for every frame, it
            // would rewrite and pulse the label every frame. originalRuns knows only the prefixes that ran before
            // this one, so a later prefix can still skip the body: the postfix, which knows for certain, hands the
            // pulse back then.
            if (t.Id != _pulsedFor && originalRuns && map.m_mode == Minimap.MapMode.Small)
            {
                map.m_biome = null;
                _pulsedBefore = _pulsedFor;
                _pulseAsked = true;
                _pulsedFor = t.Id;
            }
            else if (player != null)
            {
                map.m_biome = player.GetCurrentBiomeData();
            }
        }

        internal static void AfterUpdate(Minimap map, bool originalRan)
        {
            // The pulse was asked for but vanilla's body never ran: nothing pulsed. Ask again next frame (m_biome is
            // still null, so the label is not held in the meantime).
            if (_pulseAsked && !originalRan) _pulsedFor = _pulsedBefore;
            _pulseAsked = false;

            Territory t = _want;
            if (t == null || map != _owner || map.m_mode != Minimap.MapMode.Small) return;
            TMP_Text label = map.m_biomeNameSmall;
            if (label == null) return;

            // Compared, not assigned: an assignment that changes nothing still costs the label a new layout.
            string text = TextFor(t, label);
            if (!Shows(label.text, text)) label.text = text;
        }

        /// <summary>
        /// Hands the label back to vanilla if a territory's name holds it. For the plugin's teardown: once the
        /// patch is gone nothing would rewrite a name left on the label until the player next changed biome.
        /// </summary>
        internal static void Release()
        {
            if (!_holding) return;
            _holding = false;
            _pulsedFor = 0;
            Minimap map = Minimap.instance;
            if (map != null) map.m_biome = null;
        }

        private static Territory Wanted()
        {
            if (!TSConfig.MinimapName.Value) return null;
            Territory t = Presence.Current;
            // A territory hidden from the map keeps its name off the minimap too: its admin chose not to show it.
            return t != null && t.Has(TerritoryFlags.ShowOnMap) ? t : null;
        }

        /// <summary>Is the label showing this text - alone, or with another mod's lines after it?</summary>
        private static bool Shows(string now, string text) =>
            now != null && now.StartsWith(text, StringComparison.Ordinal) && (now.Length == text.Length || now[text.Length] == '\n');

        /// <summary>The label's text for a territory, made once per territory and revision (a rename, a new colour).</summary>
        private static string TextFor(Territory t, TMP_Text label)
        {
            if (t == _textFor && t.Revision == _textRevision && _text != null) return _text;
            _textFor = t;
            _textRevision = t.Revision;

            Color c = t.Color;
            Color32 tint = new Color(Mathf.Lerp(c.r, 1f, 0.5f), Mathf.Lerp(c.g, 1f, 0.5f), Mathf.Lerp(c.b, 1f, 0.5f), 1f);
            // Measured on the label itself, so its font, size and rich text decide what fits. The label's width, not the room's
            // shape: a frame drawn round around the minimap (another mod's) is that mod's to size the label for.
            string fitted = NameFit.Fit(t.DisplayName, label.rectTransform.rect.width, piece => label.GetPreferredValues(piece).x);
            _text = "<color=" + Territory.ColorToHex(tint) + "><nobr>" + fitted + "</nobr></color>";
            return _text;
        }
    }

    /// <summary>
    /// Priority.High: before Server Devcommands' and ZenMap's postfixes on this method (default priority), which
    /// add to the labels - run after them, the name would wipe SDC's coordinates every frame.
    /// </summary>
    /// <remarks>
    /// The priority sits on each METHOD, not on the class. HarmonyX 2.9.0 (BepInExPack 5.4.2350) merges a class's
    /// attributes into its methods' and drops the class's priority whenever the method has none of its own, so a
    /// class-level [HarmonyPriority] is silently ignored and the patch runs at the default, in plugin-load order -
    /// which put us after SDC ('server_devcommands' loads before 'wubarrk...').
    /// </remarks>
    [HarmonyPatch(typeof(Minimap), nameof(Minimap.UpdateBiome))]
    internal static class MinimapUpdateBiomePatch
    {
        private static float _lastError = -1000f;

        // Void, reading __runOriginal. The prefix must see every frame whatever another mod's prefix decided,
        // because the hold on m_biome is state that has to be let go of exactly once; HarmonyX calls every
        // prefix and only ever skips the original. A prefix's __runOriginal knows only the prefixes that ran
        // before it - one at a lower priority can still skip the original - so the postfix's copy, which is
        // final, settles whether the call that fires the pulse ran.
        [HarmonyPriority(Priority.High)]
        private static void Prefix(Minimap __instance, Player player, bool __runOriginal)
        {
            try
            {
                MapBiomeLabel.BeforeUpdate(__instance, player, __runOriginal);
            }
            catch (Exception ex)
            {
                Report(ex);
            }
        }

        [HarmonyPriority(Priority.High)]
        private static void Postfix(Minimap __instance, bool __runOriginal)
        {
            try
            {
                MapBiomeLabel.AfterUpdate(__instance, __runOriginal);
            }
            catch (Exception ex)
            {
                Report(ex);
            }
        }

        private static void Report(Exception ex)
        {
            // Cosmetic, inside a vanilla per-frame method: never let it throw into the map, and never
            // write the same failure sixty times a second.
            if (Time.realtimeSinceStartup - _lastError < 30f) return;
            _lastError = Time.realtimeSinceStartup;
            Plugin.Log.LogError($"Territory name on the minimap failed (cosmetic only, repeats suppressed for 30 s): {ex}");
        }
    }
}
