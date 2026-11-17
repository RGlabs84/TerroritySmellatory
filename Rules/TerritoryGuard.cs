using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace TerroritySmellatory
{
    // ONE GUARD (0.5.0) - ValtimaOnline's town guard (Npcs/ValtimaGuard.cs), serving a territory instead of a town gate. A clone of a Dvergr
    // (Guards.BuildPrefab): a real Character with an AI, an animator and attacks, in the Dverger faction, so monsters hunt it and players are
    // not its enemies. It stands at its post and, as any such AI does, fights a monster that comes into its sight. This component adds what a
    // guard needs beyond that:
    //
    //   - the post. A posted guard walks back to the spot GuardMath gave it and stands there facing out (PostPatch: BaseAI.IdleMovement, which
    //     runs only while the AI has no target, so never in a fight);
    //   - a call. Guards.OnCall sends a guard at a monster near a player who called; this guard's OWNER (only the owner runs an AI) holds the AI
    //     on it with the leash lifted until the monster is dead or lost;
    //   - a vanishing. A guard made by a call goes when the fight is over, after GuardLingerSeconds;
    //   - talking, a name on hover, and the Meshy body (GuardBodies) on a client with a graphics device.
    //
    // A guard cannot be hurt (Guards: RPC_Damage and ApplyDamage refused for one) and never turns on a player (not aggravatable, no player is
    // its enemy). Everything it is lives in its ZDO (the keys below), so every game reads the same guard.
    internal sealed class TerritoryGuard : MonoBehaviour, Interactable
    {
        // ZDO keys. PERMANENT once a world is saved with them: never renamed.
        internal const string TerritoryKey = "TS_GuardTerritory";   // int: the territory it serves
        internal const string PostKey = "TS_GuardPost";             // int: its post's number (GuardPost.Index); -1 for a called guard
        internal const string AtKey = "TS_GuardAt";                 // Vector3: its post's spot, as the server placed it (Guards.Reconcile compares it)
        internal const string YawKey = "TS_GuardYaw";               // float: the way it faces at its post
        internal const string NameKey = "TS_GuardName";             // string: its first name
        internal const string FemaleKey = "TS_GuardFemale";         // bool: which body it wears
        internal const string TempKey = "TS_GuardTemp";             // bool: made by a call; goes when the fight is over
        internal const string TargetKey = "TS_GuardTarget";         // ZDOID: the monster a called guard comes for
        internal const string SettledKey = "TS_GuardSettled";       // bool: its owner has stood it on whatever floor is at its post
        private const string ShoutKey = "TS_GuardShout";            // string: "<count>|<words>", so every game shows the owner's shout once

        /// <summary>The routed RPC the owner of a guard receives: go and fight this creature (a ZDOID).</summary>
        internal const string GoRpc = "TerroritySmellatory_GuardGo";

        internal static readonly List<TerritoryGuard> Live = new List<TerritoryGuard>();
        private static readonly Dictionary<Character, TerritoryGuard> ByCharacter = new Dictionary<Character, TerritoryGuard>();

        internal static int LiveCount => Live.Count;
        internal static bool IsGuard(Character c) => c != null && LiveCount > 0 && ByCharacter.ContainsKey(c);
        internal static TerritoryGuard Of(Character c) => c != null && LiveCount > 0 && ByCharacter.TryGetValue(c, out TerritoryGuard g) ? g : null;

        private const float CallSeconds = 60f, MaxLifeSeconds = 180f;
        // the post: he walks back to it at a person's pace, which his body's walk matches (the Dvergr's own 2.35 m/s outran the stride)
        private const float PostSlack = 0.6f, PostWalkSpeed = 1.5f;

        private ZNetView _nview;
        private Character _character;
        private MonsterAI _ai;
        private int _territory;
        private string _name = "";
        private bool _female, _temp;
        private float _postYaw = float.NaN;
        private bool _walkingHome;

        private ZDOID _target = ZDOID.None;
        private float _callUntil, _idleSince, _next, _bornAt, _baseChase = -1f;
        private string _shownShout = "";
        private int _shoutCount;
        private float _nextShout, _settleAt;
        private static int s_postFailed;

        internal int TerritoryId => _territory;
        internal bool HoldsPost => !_temp && !float.IsNaN(_postYaw);

        private void Awake()
        {
            _nview = GetComponent<ZNetView>();
            _character = GetComponent<Character>();
            _ai = GetComponent<MonsterAI>();
            Live.Add(this);
            if (_character != null) ByCharacter[_character] = this;
            _bornAt = _idleSince = Time.time;
            ZDO z = (_nview != null ? _nview.GetZDO() : null) ?? ZNetView.m_initZDO;
            if (z != null)
            {
                _territory = z.GetInt(TerritoryKey, 0);
                _name = z.GetString(NameKey, "");
                _female = z.GetBool(FemaleKey, false);
                _temp = z.GetBool(TempKey, false);
            }
            // routed RPCs of an object are registered once per instance, in Awake (ZNetView.Register throws on a repeat)
            if (_nview != null) _nview.Register<ZDOID>(GoRpc, RPC_Go);
        }

        private void Start()
        {
            if (_ai != null) _baseChase = _ai.m_maxChaseDistance;
            GuardBodies.Attach(this, _female);
            if (_nview == null || !_nview.IsValid()) return;
            ZDO z = _nview.GetZDO();
            if (!_temp)
            {
                _postYaw = z.GetFloat(YawKey, float.NaN);
                if (float.IsNaN(_postYaw)) _postYaw = transform.eulerAngles.y;
                _settleAt = Time.time + 2f;   // the pieces round the post have had a moment to load
            }
            else if (_nview.IsOwner())
            {
                ZDOID t = z.GetZDOID(TargetKey);
                if (t != ZDOID.None) StartCall(t);
            }
            _shownShout = z.GetString(ShoutKey, "");   // an old shout is not said again
        }

        private void OnDestroy()
        {
            Live.Remove(this);
            if (_character != null) ByCharacter.Remove(_character);
        }

        // ---- the call ---------------------------------------------------------------------------------------------------------------------

        private void RPC_Go(long sender, ZDOID target)
        {
            if (_nview == null || !_nview.IsOwner() || target == ZDOID.None) return;
            StartCall(target);
        }

        private void StartCall(ZDOID target)
        {
            bool fresh = _target == ZDOID.None;
            _target = target;
            _callUntil = Time.time + CallSeconds;
            if (fresh) Shout(Lines.Answer(_name.Length + (int)(Time.time * 10f)));
            Plugin.Debug($"[guards] {DisplayName} answers a call against {target}");
        }

        private void EndCall()
        {
            _target = ZDOID.None;
            _idleSince = Time.time;
            if (_ai == null) return;
            if (_baseChase >= 0f) _ai.m_maxChaseDistance = _baseChase;
            _ai.m_targetCreature = null;
            _ai.SetAlerted(false);
        }

        private void Update()
        {
            if (_nview == null || !_nview.IsValid() || _ai == null) return;
            if (Time.time >= _nextShout)
            {
                _nextShout = Time.time + 0.3f;
                ShowShout();
            }
            if (!_nview.IsOwner())
            {
                // the owner changed in the middle of a call: this copy no longer runs the AI, and the leash it lifted goes back
                if (_target != ZDOID.None)
                {
                    _target = ZDOID.None;
                    if (_baseChase >= 0f) _ai.m_maxChaseDistance = _baseChase;
                }
                return;
            }
            // walking home is only for the way back to the post: a guard that meets a fight runs (the game's walk flag overrides running)
            if (_walkingHome && (_ai.m_targetCreature != null || _ai.m_targetStatic != null || _target != ZDOID.None))
            {
                _walkingHome = false;
                if (_character != null) _character.SetWalk(false);
            }
            _next -= Time.deltaTime;
            if (_next > 0f) return;
            _next = 0.25f;
            try
            {
                if (_settleAt > 0f && Time.time >= _settleAt) Settle();
                if (_target != ZDOID.None) TickCall();
                else if (_temp && (Time.time - _idleSince > TSConfig.GuardLingerSeconds.Value || Time.time - _bornAt > MaxLifeSeconds)) Vanish();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[guards] {DisplayName}: {e.Message}");
                _target = ZDOID.None;
            }
        }

        // keeps the AI on the creature it was called against: the post's leash lifted, the target held, its last seen place kept current, so
        // the guard keeps coming even round a corner (MonsterAI heads for that place when it cannot see the target)
        private void TickCall()
        {
            if (Time.time > _callUntil) { EndCall(); return; }
            GameObject go = ZNetScene.instance != null ? ZNetScene.instance.FindInstance(_target) : null;
            Character c = go != null ? go.GetComponent<Character>() : null;
            if (c == null)
            {
                ZDO tz = ZDOMan.instance != null ? ZDOMan.instance.GetZDO(_target) : null;
                if (tz == null || !tz.IsValid()) EndCall();   // gone from the world; otherwise only too far for this machine to hold it
                return;
            }
            if (c.IsDead() || c is Player) { EndCall(); return; }
            _ai.m_maxChaseDistance = 0f;
            _ai.m_targetStatic = null;
            _ai.m_targetCreature = c;
            _ai.m_lastKnownTargetPos = c.transform.position;
            _ai.m_beenAtLastPos = false;
            _ai.m_timeSinceSensedTargetCreature = 0f;
            _ai.SetAlerted(true);
        }

        private void Vanish()
        {
            Plugin.Debug($"[guards] {DisplayName} goes");
            ZNetScene.instance.Destroy(gameObject);
        }

        // The server places a post on the world's own ground (it has no pieces to look at). The first owner to wake the guard stands it on a floor
        // built over the post instead, if there is one within a few metres above that ground, so a guard is never shut under a hall's floor.
        private void Settle()
        {
            _settleAt = 0f;
            ZDO z = _nview.GetZDO();
            if (z.GetBool(SettledKey, false)) return;
            z.Set(SettledKey, true);
            Vector3 post = z.GetVec3(AtKey, transform.position);
            float ground = ZoneSystem.instance != null ? ZoneSystem.instance.GetGroundHeight(post) : post.y;
            int mask = LayerMask.GetMask("piece", "static_solid", "Default_small");
            if (!Physics.Raycast(new Vector3(post.x, ground + 4f, post.z), Vector3.down, out RaycastHit hit, 4.2f, mask, QueryTriggerInteraction.Ignore)) return;
            if (hit.point.y <= transform.position.y + 0.3f) return;
            Vector3 to = new Vector3(post.x, hit.point.y + 0.1f, post.z);
            if (_ai != null) _ai.m_spawnPoint = to;
            z.Set(ZDOVars.s_spawnPoint, to);
            transform.position = to;
            Rigidbody body = GetComponent<Rigidbody>();
            if (body != null) body.position = to;
            Plugin.Debug($"[guards] {DisplayName} stood on the floor over his post, {hit.point.y - ground:0.0} m above the ground");
        }

        // The owner says it at once and writes it into the ZDO; every other copy says it when it reads a new one
        private void Shout(string words)
        {
            Say(words);
            string value = ++_shoutCount + "|" + words;
            _nview.GetZDO().Set(ShoutKey, value);
            _shownShout = value;
        }

        private void ShowShout()
        {
            if (_nview.IsOwner()) return;
            string value = _nview.GetZDO().GetString(ShoutKey, "");
            if (value == _shownShout) return;
            _shownShout = value;
            int bar = value.IndexOf('|');
            if (bar >= 0 && bar + 1 < value.Length) Say(value.Substring(bar + 1));
        }

        private void Say(string text)
        {
            if (Chat.instance == null || Hud.instance == null || string.IsNullOrEmpty(text)) return;
            Chat.instance.SetNpcText(gameObject, Vector3.up * 2.6f, 20f, 5f, "", text, false);
        }

        // ---- the post ---------------------------------------------------------------------------------------------------------------------
        // ValtimaOnline's look walks 3-8: a posted guard wandered round his spawn point as the Dvergr does and faced wherever his last step took
        // him. BaseAI.IdleMovement runs only when the AI has no target, so there a posted guard walks back when he is more than PostSlack off his
        // post, then stops and turns to face out. A called guard keeps the game's wander until he goes.

        internal void HoldPost(BaseAI ai, float dt)
        {
            Vector3 post = ai.m_spawnPoint;
            if (Utils.DistanceXZ(post, transform.position) > PostSlack)
            {
                if (_character != null)
                {
                    _character.m_walkSpeed = PostWalkSpeed;
                    _character.SetWalk(true);
                    _walkingHome = true;
                }
                ai.MoveTo(dt, post, PostSlack * 0.5f, false);
                return;
            }
            ai.StopMoving();
            if (_character != null)
            {
                if (_walkingHome) { _character.SetWalk(false); _walkingHome = false; }
                _character.SetLookDir(Quaternion.Euler(0f, _postYaw, 0f) * Vector3.forward);
            }
        }

        [HarmonyPatch(typeof(BaseAI), nameof(BaseAI.IdleMovement))]
        internal static class PostPatch
        {
            private static bool Prefix(BaseAI __instance, float dt)
            {
                try
                {
                    TerritoryGuard g = Of(__instance.m_character);
                    if (g == null || !g.HoldsPost) return true;
                    g.HoldPost(__instance, dt);
                    return false;
                }
                catch (Exception e)
                {
                    if (s_postFailed++ == 0) Plugin.Log.LogWarning($"[guards] holding a guard's post failed, so guards wander as the Dvergr does (logged once): {e}");
                    return true;
                }
            }
        }

        // ---- what a player sees and hears ---------------------------------------------------------------------------------------------

        /// <summary>"Leif, guard of Hrafnborg" - the territory's name as this game knows it now, so a renamed territory's guards follow it.</summary>
        internal string DisplayName
        {
            get
            {
                Territory t = _territory > 0 ? TerritoryIndex.Find(_territory) : null;
                string title = GuardMath.Title(t != null ? t.DisplayName : "");
                return _name.Length > 0 ? _name + ", " + title : char.ToUpperInvariant(title[0]) + title.Substring(1);
            }
        }

        internal string HoverText() => Localization.instance.Localize(DisplayName + "\n[<color=yellow><b>$KEY_Use</b></color>] Talk");

        internal string HoverName() => DisplayName;

        public bool Interact(Humanoid user, bool hold, bool alt)
        {
            if (hold) return false;
            Territory t = _territory > 0 ? TerritoryIndex.Find(_territory) : null;
            Say(Lines.Talk(UnityEngine.Random.Range(0, 1 << 20), t != null ? t.DisplayName : "", GuardMath.FirstWord(TSConfig.GuardCallWords.Value)));
            return false;
        }

        public bool UseItem(Humanoid user, ItemDrop.ItemData item) => false;

        private static class Lines
        {
            private static readonly string[] Answers = { "I'm coming!", "Hold on, I'll deal with it!", "Stand back!", "To arms!" };

            internal static string Answer(int seed) => Answers[Math.Abs(seed) % Answers.Length];

            internal static string Talk(int seed, string territory, string callWord)
            {
                string place = territory.Length > 0 ? territory : "these lands";
                string[] lines =
                {
                    $"All quiet in {place}.",
                    $"Keep your blade clean while you're in {place}.",
                    callWord.Length > 0 ? $"Trouble on your heels? Shout \"{callWord}\" and we'll come." : "Trouble on your heels? We'll be watching.",
                    "Nothing gets past this post.",
                    "Mind the woods at night.",
                };
                return lines[seed % lines.Length];
            }
        }
    }
}
