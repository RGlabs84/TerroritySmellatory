using System;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace TerroritySmellatory
{
    /// <summary>
    /// Where the local player is, and what follows them around: the entry banner and the goodbye, forced PvP and
    /// the Sanctuary effect. All of them follow ONE territory, the one the player counts as in (<see cref="Current"/>),
    /// which Rules/BannerPlan.cs holds for 3 m past its edge so that a step either way on a border changes nothing.
    /// </summary>
    internal static class Presence
    {
        /// <summary>
        /// The territory the local player counts as in, or null in open country: the one governing where they stand,
        /// held BannerPlan.HoldMetres past its edge. The banner, forced PvP, the Sanctuary effect, the panel and the
        /// minimap's name all read this one, so none of them can disagree about where the player is.
        /// </summary>
        internal static Territory Current { get; private set; }

        /// <summary>What PvP is being held at, or null when the player's own toggle is in charge.</summary>
        internal static bool? ForcedPvP { get; private set; }

        private const float TickSeconds = 0.25f;

        private static readonly BannerPlan Plan = new BannerPlan();

        /// <summary>
        /// Current's record as last seen. The index's records are clones the server never edits in place
        /// (TerritoryServer), so holding one is enough to still have a territory's name and goodbye text after an
        /// admin deletes it from under the player.
        /// </summary>
        private static Territory _last;

        private static float _timer;
        private static bool _haveOwnChoice;
        private static bool _ownChoice;
        private static float _lastCosmeticError = -1000f;

        /// <summary>
        /// A new session (ZNet.OnDestroy). Only here are the banner cooldowns and the territories seen forgotten; a
        /// death keeps them (see Tick).
        /// </summary>
        internal static void ResetSession()
        {
            Plan.ResetSession();
            EntryBanner.Forget();
            Current = null;
            _last = null;
            ForcedPvP = null;
            _haveOwnChoice = false;
            _timer = 0f;
        }

        internal static void Tick(float dt)
        {
            Player player = Player.m_localPlayer;
            if (player == null || ZNet.instance == null)
            {
                // No player: between a death and the respawn (Game._RequestRespawn destroys the old one), and before
                // the first spawn. 0.1.1 reset everything here, so a respawn inside a territory announced it all over
                // again, and dying in a war zone left the inventory's PvP toggle forced on for good. Now the plan keeps
                // where the player counts as being and the cooldowns, and this keeps the PvP choice it owes the player.
                Plan.PlayerGone();
                return;
            }

            _timer += dt;
            if (_timer < TickSeconds) return;
            _timer = 0f;

            // Dead, or riding the valkyrie in - where vanilla's own biome discovery waits too (Player.UpdateBiome
            // returns in the intro, and FixedUpdate never calls it for the dead): the plan changes nothing and
            // nothing below runs. Death is not leaving.
            bool frozen = player.IsDead() || player.InIntro();
            Vector3 pos = player.transform.position;
            BannerSettings settings = Settings();

            int was = Plan.Effective;
            BannerInput look = Plan.Observe(pos, _last, out Territory governing, out Territory counted);
            Territory before = counted ?? (_last != null && _last.Id == was ? _last : null);
            look.Time = Time.time;
            // Between portals the plan holds too. A portal, the map's teleport or goto puts the body at the far end 2 s
            // in, but lands no sooner than 8 s in, later while a busy destination loads (Player.UpdateTeleport):
            // crossing at the jump would spend the Epic banner's MaxWait, and any goodbye, on the black screen. The
            // rules below keep to where the ride began until the landing, which costs nothing - Character.RPC_Damage
            // and Player.UpdateStats both return while the player is teleporting.
            look.Frozen = frozen || player.IsTeleporting();
            look.Blocked = settings.Enabled && settings.Epic && BannerBlocked(player);
            BannerStep step = Plan.Step(look, settings);

            // The plan only ever moves the player INTO what governs where they stand.
            Territory now = step.Effective == 0 ? null : step.Effective == was ? counted : governing;
            Current = now;
            if (now != null) _last = now;
            if (frozen) return;

            // PvP first, the words after (master pattern 32): SetPVP writes its own notice into the centre slot, and
            // the goodbye below carries that notice as its second line. The Epic banner is not in that slot, so on the
            // way in vanilla's one-line notice simply stands above it.
            bool pvpWas = player.IsPVPEnabled();
            ApplyPvP(player, now);
            bool pvpIs = player.IsPVPEnabled();

            if (now != null && now.Has(TerritoryFlags.Sanctuary)) Sanctuary.Apply(player, now);
            else Sanctuary.Remove(player);

            if (step.Changed || step.Entry != 0 || step.Leave != 0)
                Announce(step, before, now, pvpIs != pvpWas ? pvpIs : (bool?)null);
        }

        private static BannerSettings Settings() => new BannerSettings
        {
            Enabled = TSConfig.Banners.Value,
            Epic = TSConfig.EntryBanner.Value == BannerStyle.Epic,
            StingerFirst = TSConfig.EntryStinger.Value == StingerMode.FirstEntry,
            StingerEvery = TSConfig.EntryStinger.Value == StingerMode.EveryEntry,
            Cooldown = TSConfig.BannerCooldown.Value,
        };

        /// <summary>
        /// Everything the player SEES of a crossing, after PvP and Sanctuary have applied. Cosmetic, so it has its own
        /// try/catch (master pattern 12): a banner that throws must never cost a player the rules.
        /// </summary>
        /// <param name="left">The record of the territory the player counted as in before this tick.</param>
        /// <param name="pvpNow">The PvP state this tick switched the player to, or null if it did not change.</param>
        private static void Announce(in BannerStep step, Territory left, Territory entered, bool? pvpNow)
        {
            try
            {
                // Whatever our banner says, it names somewhere the player has just left.
                if (step.Changed) EntryBanner.Dismiss();

                // The goodbye names a PvP state only when this tick changed it: the player's own choice handed back.
                if (step.Leave != 0 && left != null && left.Id == step.Leave)
                    Centre(left.FillPlaceholders(string.IsNullOrEmpty(left.LeaveMessage) ? "Leaving {name}" : left.LeaveMessage), pvpNow);

                if (step.Entry == 0) return;
                Territory t = entered != null && entered.Id == step.Entry ? entered : TerritoryIndex.Find(step.Entry);
                if (t == null) return;

                // The Simple style, and the fallback should the Epic banner fail to build: 0.1.1's centre line. Ground that forces
                // PvP names the state it holds every time, changed on this tick or not (BannerNotch.EntryLine); the Epic banner
                // says it with its notch instead.
                if (step.EntryLine || !EntryBanner.Show(t, step.Stinger))
                    Centre(t.FillPlaceholders(string.IsNullOrEmpty(t.EnterMessage) ? "Entering {name}" : t.EnterMessage),
                        BannerNotch.EntryLine(t.PvP, pvpNow));
            }
            catch (Exception ex)
            {
                CosmeticFailed(ex);
            }
        }

        /// <param name="pvpLine">The PvP state the message names on its one extra line (true is on), or null for no such line.</param>
        private static void Centre(string text, bool? pvpLine)
        {
            if (MessageHud.instance == null) return;

            // The PvP line is vanilla's own notice in vanilla's own words ($msg_pvpon / $msg_pvpoff, what SetPVP just showed), so
            // it reads in the player's language - ShowMessage localizes the whole text, the token inside its colour tag included
            // - and it is coloured by its state, red for on and green for off (BannerNotch.PvPLine). It is the only one: this
            // message replaces the slot's text, and vanilla's own notice with it.
            MessageHud.instance.ShowMessage(MessageHud.MessageType.Center, BannerNotch.WithPvPLine(text, pvpLine));
        }

        private static bool BannerBlocked(Player player)
        {
            try
            {
                return EntryBanner.Blocked(player);
            }
            catch (Exception ex)
            {
                CosmeticFailed(ex);
                return true;
            }
        }

        internal static void CosmeticFailed(Exception ex)
        {
            // Cosmetic only: the rules have applied either way. Once per 30 s, not four times a second.
            if (Time.realtimeSinceStartup - _lastCosmeticError < 30f) return;
            _lastCosmeticError = Time.realtimeSinceStartup;
            Plugin.Log.LogError($"Territory banner failed (cosmetic only, repeats suppressed for 30 s): {ex}");
        }

        // PvP is decided on the TARGET's own machine - Character.RPC_Damage refuses a player's hit on a
        // player whose IsPVPEnabled() is false, and on the owner that is the local m_pvp flag. So holding
        // each player's own flag is the whole of forcing PvP, for attacker and victim alike.
        //
        // The player's own choice is remembered once, when a territory takes over, and survives a respawn: the new
        // Player starts with PvP off (m_pvp is not saved) while the inventory toggle keeps what it showed, so the
        // first tick after the respawn hands the choice back, or holds the new body to the rule it spawned into.
        private static void ApplyPvP(Player player, Territory now)
        {
            PvPRule rule = now?.PvP ?? PvPRule.Unchanged;
            Toggle toggle = InventoryGui.instance != null ? InventoryGui.instance.m_pvp : null;

            if (rule == PvPRule.Unchanged)
            {
                if (ForcedPvP == null) return;
                ForcedPvP = null;

                // Hand the player's own choice back, exactly as it was when the territory took over.
                if (_haveOwnChoice)
                {
                    if (toggle != null) toggle.SetIsOnWithoutNotify(_ownChoice);
                    if (player.IsPVPEnabled() != _ownChoice) player.SetPVP(_ownChoice);
                    _haveOwnChoice = false;
                }
                return;
            }

            bool forced = rule == PvPRule.ForcedOn;
            if (!_haveOwnChoice)
            {
                _ownChoice = toggle != null ? toggle.isOn : player.IsPVPEnabled();
                _haveOwnChoice = true;
            }

            ForcedPvP = forced;
            if (player.IsPVPEnabled() != forced) player.SetPVP(forced);
            if (toggle != null && toggle.isOn != forced) toggle.SetIsOnWithoutNotify(forced);
        }
    }

    /// <summary>
    /// The Epic entry banner: vanilla's own biome-discovery banner (MessageHud.m_biomeFoundPrefab) with the
    /// territory's name as its title and the admin's entry text beneath the lower braid, played with vanilla's
    /// discovery sound when the plan says so.
    /// </summary>
    /// <remarks>
    /// IN VANILLA'S SLOT. MessageHud shows one such banner at a time, the one in m_biomeMsgInstance. Ours goes into
    /// that slot, so vanilla treats it as its own: fades it on the prefab's own 4 s animation, destroys it at the
    /// "done" state, destroys it on Ctrl+F3 (HideAll), and holds its own biome and location discoveries back until
    /// it is gone (UpdateBiomeFound dequeues only into an empty slot). An instance of ours beside the slot would sit
    /// on top of vanilla's banner at the same spot and survive Ctrl+F3.
    ///
    /// NOT ShowBiomeFoundMsg. That only queues, behind every top-left pickup message plus 2 s of quiet, so the banner
    /// could come up long after the player walked through, naming somewhere they have already left; a queued entry
    /// cannot be withdrawn; and other mods hook that method as "a biome was discovered". Vanilla's queue is never
    /// touched here, and never cleared - that would lose its once-ever discoveries for good.
    ///
    /// Whatever sits in the slot must keep the prefab's Animator (vanilla reads it every frame, and an exception
    /// there would stop MessageHud's whole update) and must reach "done" or be destroyed, or vanilla's own
    /// discoveries would wait behind it forever. A clone of vanilla's prefab does both by itself.
    /// </remarks>
    internal static class EntryBanner
    {
        // The prefab (bundle d59cfac, read 2026-09-29): UnlockMessage 700 x 400 carrying the Animator and CanvasGroup;
        // Title 400 x 57 at its centre, Valheim-Norsebold 60 pt, wrapping, no autosize; the braids 550 x 20 at y +40
        // and -40. Measured with the game's own Norsebold, a typical 48-character name is about 1060 units wide at
        // 60 pt and would wrap into the braids; 48 capital Ws fit 540 units at 22 pt. So the title gets the braids'
        // width, one line, and autosize from vanilla's 60 pt down to 22; 80 units of height keep a short name at 60
        // (a Norsebold line is 1.1 em, 66 units at 60 pt).
        private const float TitleWidth = 540f;
        private const float TitleHeight = 80f;
        private const float TitleMinSize = 22f;
        private const float TitleMaxSize = 60f;

        // The admin's entry text in vanilla's message letters (m_messageCenterText's font, material and colour - the
        // look the text had as 0.1.x's centre line) under the lower braid. At most 160 characters (Territory.
        // MaxMessageLength): two lines at 16 pt in 620 units, one line at up to 28 pt for anything short.
        private const float SubtitleTop = -58f;
        private const float SubtitleWidth = 620f;
        private const float SubtitleHeight = 60f;
        private const float SubtitleMinSize = 16f;
        private const float SubtitleMaxSize = 28f;

        // The PvP notch (BannerNotch, 0.4.0): a gem set in the LOWER braid's knot. That braid is the prefab's "braid (1)", the
        // upper one turned upside down: its flat line runs along its top edge (y -31 to -33.5, the side that faces the title)
        // and its knot hangs from the line down to -50, about 80 units wide and centred (BraidLineHorisontalMedium, 504 x 17
        // pixels, read from the bundle 2026-10-11). Centred on the braid, at y -40, a 14-unit square turned 45 degrees (19.8
        // units across) sits in the middle of the knot. Its 2-unit rim (an 18-unit square, 25.5 across) stands 2.7 units clear
        // of the braid above and below, and ends 5.3 above the subtitle's top edge (-58).
        private const string NotchName = "TS_PvPNotch";
        private const float NotchY = -40f;
        private const float NotchGem = 14f;
        private const float NotchRim = 2f;

        // The black screen of a teleport, a respawn or sleep (Hud.m_loadingScreen) sits on the HUD canvas, BELOW
        // MessageHud's: a banner shown under it would play out over black and be gone before the world appears.
        private const float DarkScreen = 0.3f;

        private static GameObject _mine;

        /// <summary>Why ours could not be seen right now. The plan then waits (BannerPlan.MaxWait) instead of dropping it.</summary>
        internal static bool Blocked(Player player)
        {
            MessageHud hud = MessageHud.instance;
            if (hud == null) return true;

            // Ctrl+F3: MessageHud.Update would destroy it the next frame (HideAll).
            if (Hud.IsUserHidden() && !hud.m_showDespiteHiddenHUD) return true;

            // Dead, riding the valkyrie in, asleep, in a cinematic, or between portals. InCutscene covers the middle
            // three, and it is the HUD's own reason to hide itself (Hud.Update: SetVisible(!m_userHidden &&
            // !InCutscene())) - MessageHud's canvas stays up regardless, so the banner has to ask.
            if (player.IsDead() || player.InCutscene() || player.IsTeleporting()) return true;

            Hud screen = Hud.instance;
            if (screen != null && screen.m_loadingScreen != null && screen.m_loadingScreen.gameObject.activeInHierarchy &&
                screen.m_loadingScreen.alpha > DarkScreen)
                return true;

            // Vanilla's own banner - a biome, a location, a dungeon's name - holds the one slot: ours comes after it.
            GameObject up = hud.m_biomeMsgInstance;
            return up != null && up != _mine;
        }

        /// <summary>Shows the banner for <paramref name="t"/>, restarting ours if it is up. False if it could not be built.</summary>
        internal static bool Show(Territory t, bool stinger)
        {
            MessageHud hud = MessageHud.instance;
            if (hud == null || hud.m_biomeFoundPrefab == null) return false;

            // Straight from one territory into the next: the new name starts over, fade-in and all.
            Forget();

            GameObject banner = Object.Instantiate(hud.m_biomeFoundPrefab, hud.transform);
            try
            {
                TMP_Text title = global::Utils.FindChild(banner.transform, "Title").GetComponent<TMP_Text>();
                string entry = string.IsNullOrEmpty(t.EnterMessage) ? null : Localization.instance.Localize(t.FillPlaceholders(t.EnterMessage));
                if (!string.IsNullOrEmpty(entry)) AddSubtitle(hud, title, entry);

                // Last of the children, so it is drawn over the braids, and inside this try like the rest: a failure here
                // destroys the half-built banner and the caller shows the centre line (which then carries the PvP line).
                AddPvPNotch(title.transform.parent, t.PvP);

                // textWrappingMode, not enableWordWrapping: that one is obsolete in this TextMeshPro, and a warning.
                title.textWrappingMode = TextWrappingModes.NoWrap;
                title.enableAutoSizing = true;
                title.fontSizeMin = TitleMinSize;
                title.fontSizeMax = TitleMaxSize;
                title.rectTransform.sizeDelta = new Vector2(TitleWidth, TitleHeight);
                // Names cannot carry markup or a $token (Territory.CleanName strips both), so vanilla's Localize
                // would hand it back unchanged.
                title.text = t.DisplayName;
            }
            catch (Exception ex)
            {
                // Never half a banner in vanilla's slot: gone, and the caller shows the centre line instead.
                Object.Destroy(banner);
                Presence.CosmeticFailed(ex);
                return false;
            }

            hud.m_biomeMsgInstance = banner;
            _mine = banner;

            // Vanilla's own discovery sound, as MessageHud plays it: a one-shot 2D effect that times itself out.
            if (stinger && hud.m_biomeFoundStinger != null) Object.Instantiate(hud.m_biomeFoundStinger);
            return true;
        }

        private static void AddSubtitle(MessageHud hud, TMP_Text title, string text)
        {
            // A copy of the title, so it inherits the UnlockMessage's CanvasGroup fade with it. Renamed, because
            // vanilla finds the title by name ("Title") and the first match wins.
            TMP_Text line = Object.Instantiate(title.gameObject, title.transform.parent).GetComponent<TMP_Text>();
            line.gameObject.name = "TS_EntryText";

            TMP_Text body = hud.m_messageCenterText;
            if (body != null)
            {
                line.font = body.font;
                line.fontSharedMaterial = body.fontSharedMaterial;
                line.color = body.color;
            }
            line.textWrappingMode = TextWrappingModes.Normal;
            line.enableAutoSizing = true;
            line.fontSizeMin = SubtitleMinSize;
            line.fontSizeMax = SubtitleMaxSize;
            line.alignment = TextAlignmentOptions.Top;

            RectTransform rect = line.rectTransform;
            rect.sizeDelta = new Vector2(SubtitleWidth, SubtitleHeight);
            rect.anchoredPosition = new Vector2(0f, SubtitleTop - SubtitleHeight * 0.5f);
            line.text = text;
        }

        /// <summary>
        /// The PvP notch: on a territory that forces PvP, a gem in the lower braid's knot, green where PvP is held off and red
        /// where it is held on (BannerNotch). A territory that leaves the player's toggle alone gets none.
        /// </summary>
        /// <remarks>
        /// ONE object, "TS_PvPNotch", a child of the Title's own parent (the UnlockMessage, where TS_EntryText lives too): it
        /// fades with the banner's CanvasGroup and is destroyed with the banner, so nothing about it needs tidying up. Inside it
        /// the dark rim ("TS_PvPNotchRim") comes first and the coloured gem ("TS_PvPNotchGem") after it, so the gem is drawn over
        /// the rim. Both are plain squares of UnityEngine.UI.Image - no sprite, no file - and the object that holds them is turned
        /// 45 degrees, which makes them diamonds. Neither takes a raycast, and the UnlockMessage has no layout group to disturb,
        /// so the title and the subtitle are exactly as they were.
        /// </remarks>
        private static void AddPvPNotch(Transform parent, PvPRule rule)
        {
            NotchMark mark = BannerNotch.For(rule);
            if (mark == NotchMark.None) return;

            RectTransform notch = NewNotchRect(NotchName, parent, NotchGem + 2f * NotchRim);
            notch.anchoredPosition = new Vector2(0f, NotchY);
            notch.localRotation = Quaternion.Euler(0f, 0f, 45f);

            NewNotchSquare("TS_PvPNotchRim", notch, NotchGem + 2f * NotchRim, BannerNotch.Rim);
            NewNotchSquare("TS_PvPNotchGem", notch, NotchGem, BannerNotch.Colour(mark == NotchMark.On));
        }

        private static RectTransform NewNotchRect(string name, Transform parent, float side)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = parent.gameObject.layer;   // the banner's own (UI): a new object starts on Default
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(side, side);
            return rect;
        }

        private static void NewNotchSquare(string name, Transform parent, float side, Color32 colour)
        {
            var image = NewNotchRect(name, parent, side).gameObject.AddComponent<Image>();
            image.color = colour;
            image.raycastTarget = false;
        }

        /// <summary>Fades ours out from wherever it is in its animation, and is then rid of it. Nothing pops.</summary>
        internal static void Dismiss()
        {
            if (_mine == null || _mine.GetComponent<EntryBannerFade>() != null) return;
            _mine.AddComponent<EntryBannerFade>();
        }

        /// <summary>Destroys ours if it is still up, and forgets it.</summary>
        internal static void Forget()
        {
            if (_mine != null) Object.Destroy(_mine);
            _mine = null;
        }
    }

    /// <summary>
    /// A banner the player has walked away from: faded out over <see cref="Seconds"/>, then destroyed, which empties
    /// vanilla's slot as its own "done" would.
    /// </summary>
    /// <remarks>
    /// Through a CanvasGroup of its own on the banner's ROOT, not through the prefab's Animator. The clip writes the
    /// alpha of the UnlockMessage's CanvasGroup every frame, so that one cannot be faded from outside, and driving
    /// the Animator would need UnityEngine.AnimationModule, which this build does not reference. The prefab's group
    /// does not ignore its parents (m_IgnoreParentGroups 0, bundle d59cfac), so the two alphas multiply: whatever
    /// the clip is doing - fading in, holding, already fading out - the banner is gone within Seconds, and never
    /// snaps off.
    /// </remarks>
    internal sealed class EntryBannerFade : MonoBehaviour
    {
        internal const float Seconds = 0.75f;

        private CanvasGroup _group;
        private float _left = Seconds;

        private void Awake()
        {
            _group = GetComponent<CanvasGroup>();
            if (_group == null) _group = gameObject.AddComponent<CanvasGroup>();
            _group.interactable = false;
            _group.blocksRaycasts = false;
        }

        private void Update()
        {
            // Scaled time, like the clip's own Animator: a paused game holds both.
            _left -= Time.deltaTime;
            if (_left <= 0f)
            {
                Destroy(gameObject);
                return;
            }
            _group.alpha = _left / Seconds;
        }
    }

    /// <summary>
    /// The inventory's PvP toggle writes itself onto the player every frame the inventory is open
    /// (InventoryGui.UpdateCharacterStats: player.SetPVP(m_pvp.isOn)). Left alone it would flip a forced
    /// PvP back each frame - with a centre message each time. The PREFIX sets the toggle to the forced value
    /// before vanilla reads it, so vanilla's own SetPVP is a no-op; the postfix greys the toggle out.
    /// </summary>
    [HarmonyPatch(typeof(InventoryGui), "UpdateCharacterStats")]
    internal static class InventoryPvPTogglePatch
    {
        private static void Prefix(InventoryGui __instance)
        {
            bool? forced = Presence.ForcedPvP;
            if (forced.HasValue && __instance.m_pvp != null && __instance.m_pvp.isOn != forced.Value)
                __instance.m_pvp.SetIsOnWithoutNotify(forced.Value);
        }

        private static void Postfix(InventoryGui __instance)
        {
            if (Presence.ForcedPvP.HasValue && __instance.m_pvp != null)
                __instance.m_pvp.interactable = false;
        }
    }
}
