using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using UnityEngine.Rendering;

namespace TerroritySmellatory
{
    // THE GUARDS' BODIES (0.5.0) - ValtimaOnline's Meshy town guards (Npcs/MeshyBodies.cs there), carried over for the guard looks only:
    // guard_v2, the approved pilot guard (a GENERIC rig, every clip bought on his own skeleton, plain clip names), and guard_warrior_f (a
    // HUMANOID rig: her own "guard_warrior_f.Walk" / ".Work", the guard pack's shared Humanoid clips "h.<Name>", and Valheim's own idle, walk
    // and run, ValtimaOnline's chosen default). Built by tools/build-guard-bodies.sh into terroritysmellatory-guards.bundle, which the
    // csproj EMBEDS in the DLL - nothing sits beside it.
    //
    // On each client with a graphics device (a dedicated server never opens the bundle):
    // - the bundle is opened once, from the embedded resource, and never unloaded (an unload takes the mesh out from under every body);
    // - a body is the whole rigged prefab, instantiated as the guard's LAST child (the Dvergr's Animator stays first for Character.Awake and
    //   ZSyncAnimation, and its animation events still fire the hits), turned to face the guard's forward as measured from its own bind pose
    //   (FacingTurn), lifted so its lowest POSED vertex is on the ground, and dressed in a COPY of the Dvergr's own material (Valheim's
    //   character shader, lit as the game lights it) with our albedo and normal in it;
    // - the Dvergr's renderers and LOD groups are switched off, never destroyed, and again every 2 s (VisEquipment turns them back on);
    // - GuardBody plays the clips through its own PlayableGraph and plants the feet; GuardHalberd puts the game's iron atgeir in his hands.
    // Without the bundle (a build made without Unity) every guard keeps the Dvergr's own look and says so once in the log.
    internal static class GuardBodies
    {
        internal const string ResourceName = "TerroritySmellatory.terroritysmellatory-guards.bundle";
        internal const string ChildName = "TS_GuardBody";
        internal const string MaleBody = "guard_v2";
        internal const string FemaleBody = "guard_warrior_f";
        private const string SharedPrefix = "h.";

        private static bool s_tried;
        private static AssetBundle s_bundle;
        private static Stream s_stream;   // LoadFromStream reads from it for as long as the bundle is open
        private static readonly HashSet<string> s_available = new HashSet<string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, GameObject> s_prefabs = new Dictionary<string, GameObject>();
        private static readonly Dictionary<string, bool> s_humanoid = new Dictionary<string, bool>();
        private static readonly Dictionary<string, Texture2D> s_textures = new Dictionary<string, Texture2D>();
        private static readonly Dictionary<string, AnimationClip> s_clips = new Dictionary<string, AnimationClip>();
        private static readonly Dictionary<string, Material> s_materials = new Dictionary<string, Material>();
        private static Dictionary<string, AnimationClip> s_valheimClips;

        /// <summary>
        /// The clip a body plays under a name: its own ("guard_warrior_f.Walk") first; then, for a Humanoid body, the shared one ("h.Attack");
        /// the Generic guard has plain names ("Attack").
        /// </summary>
        internal static AnimationClip Clip(string asset, string name)
        {
            if (name == null) return null;
            if (asset != null && s_clips.TryGetValue(asset + "." + name, out var own)) return own;
            if (asset != null && s_humanoid.TryGetValue(asset, out bool human) && human)
                return s_clips.TryGetValue(SharedPrefix + name, out var shared) ? shared : null;
            return s_clips.TryGetValue(name, out var plain) ? plain : null;
        }

        /// <summary>One of the Player controller's own clips by name ("IdleTweaked", "Walk New", "Run New"), or null. A Humanoid body idles, walks and runs with these.</summary>
        internal static AnimationClip ValheimClip(string name)
        {
            if (s_valheimClips == null)
            {
                s_valheimClips = new Dictionary<string, AnimationClip>(StringComparer.Ordinal);
                GameObject player = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab("Player") : null;
                Animator a = player != null ? player.GetComponentInChildren<Animator>(true) : null;
                if (a != null && a.runtimeAnimatorController != null)
                    foreach (AnimationClip c in a.runtimeAnimatorController.animationClips)
                        if (c != null && !s_valheimClips.ContainsKey(c.name)) s_valheimClips[c.name] = c;
            }
            return name != null && s_valheimClips.TryGetValue(name, out var clip) ? clip : null;
        }

        internal static bool IsHumanoid(string asset) => asset != null && s_humanoid.TryGetValue(asset, out bool h) && h;

        /// <summary>Puts the Meshy body on a guard that just woke up on this client. False leaves the Dvergr's own look. Never throws.</summary>
        internal static bool Attach(TerritoryGuard guard, bool female)
        {
            try
            {
                if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null || !Load()) return false;
                string asset = female && s_available.Contains(FemaleBody) ? FemaleBody : MaleBody;
                GameObject prefab = Prefab(asset);
                if (prefab == null) return false;
                Transform root = guard.transform;
                Transform had = root.Find(ChildName);
                if (had != null)
                {
                    if (had.GetComponent<GuardBody>() != null) return true;
                    UnityEngine.Object.Destroy(had.gameObject);
                }
                GameObject go = UnityEngine.Object.Instantiate(prefab);
                go.name = ChildName;
                go.transform.SetParent(root, false);   // the prefab's own scales (an armature's 0.01) are right: never "fixed"
                go.transform.localPosition = Vector3.zero;
                go.transform.localRotation = Quaternion.Euler(0f, FacingTurn(root, go, asset), 0f);
                foreach (Transform t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = root.gameObject.layer;
                Dress(go, MaterialFor(asset, root, go.transform));
                go.transform.localPosition = new Vector3(0f, MeasureLift(root, go), 0f);
                HideDonor(root, go.transform);
                go.AddComponent<GuardBody>().Bind(guard, asset);
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[guards] a guard's body: {e.Message}; it keeps the Dvergr's look");
                return false;
            }
        }

        /// <summary>
        /// Hides the Dvergr's own body: its renderers and LOD groups are disabled, never deactivated or destroyed (Unity's LOD system and
        /// VisEquipment re-enable renderers, so GuardBody calls this again every 2 s); the donor's animator stays alive so its animation
        /// events - a guard's hit landing - keep firing. Our own body (<paramref name="keep"/>) is left alone.
        /// </summary>
        internal static void HideDonor(Transform root, Transform keep)
        {
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
                if (keep == null || !r.transform.IsChildOf(keep)) r.enabled = false;
            foreach (LODGroup g in root.GetComponentsInChildren<LODGroup>(true))
                if (keep == null || !g.transform.IsChildOf(keep)) g.enabled = false;
        }

        private static GameObject Prefab(string asset)
        {
            if (s_prefabs.TryGetValue(asset, out GameObject have)) return have;
            GameObject go = s_available.Contains(asset) ? s_bundle.LoadAsset<GameObject>(asset) : null;
            if (go == null) Plugin.Log.LogWarning($"[guards] the guard bundle has no body '{asset}'");
            s_prefabs[asset] = go;
            if (go != null)
            {
                Animator animator = go.GetComponentInChildren<Animator>(true);
                s_humanoid[asset] = animator != null && animator.avatar != null && animator.avatar.isHuman;
            }
            return go;
        }

        private static Texture2D Tex(string name)
        {
            if (s_textures.TryGetValue(name, out Texture2D have)) return have;
            Texture2D t = s_bundle != null ? s_bundle.LoadAsset<Texture2D>(name) : null;
            s_textures[name] = t;
            return t;
        }

        private static readonly HashSet<string> s_facingLogged = new HashSet<string>();

        // The turn (degrees about the guard's up) that makes a body face its guard's forward, measured from the body's BIND pose: its right runs
        // from the left hip to the right, its forward is right x up, checked against the head's front bone. Measured, not assumed (ValtimaOnline's
        // look walks 4-10: an assumed 180 turned him round, and the bones as the prefab holds them are a frame of a clip, which crabbed his walk).
        private static float FacingTurn(Transform root, GameObject go, string asset)
        {
            SkinnedMeshRenderer smr = go.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .Where(r => r != null && r.sharedMesh != null && r.bones != null && r.bones.Length > 0 && r.sharedMesh.bindposes.Length == r.bones.Length)
                .OrderByDescending(r => r.sharedMesh.vertexCount).FirstOrDefault();
            if (smr == null) return 0f;
            Transform[] bones = smr.bones;
            Matrix4x4[] binds = smr.sharedMesh.bindposes;
            Vector3? Bound(string name)
            {
                int i = Array.FindIndex(bones, b => b != null && b.name == name);
                if (i < 0) return null;
                return root.InverseTransformPoint(smr.transform.TransformPoint(binds[i].inverse.MultiplyPoint3x4(Vector3.zero)));
            }
            Vector3? left = Bound("LeftUpLeg") ?? Bound("LeftArm"), right = Bound("RightUpLeg") ?? Bound("RightArm");
            if (left == null || right == null) return 0f;
            Vector3 forward = Vector3.Cross(right.Value - left.Value, Vector3.up);
            forward.y = 0f;
            if (forward.sqrMagnitude < 1e-8f) return 0f;
            forward.Normalize();
            Vector3? head = Bound("Head"), front = Bound("headfront");
            if (head != null && front != null)
            {
                Vector3 face = front.Value - head.Value;
                face.y = 0f;
                if (face.sqrMagnitude > 1e-8f && Vector3.Dot(face.normalized, forward) < -0.5f) forward = -forward;   // the face wins
            }
            float turn = -Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
            if (s_facingLogged.Add(asset)) Plugin.Debug($"[guards] body {asset} is turned {turn:0} deg to face its guard's forward");
            return turn;
        }

        private static bool Load()
        {
            if (s_bundle != null) return true;
            if (s_tried) return false;
            s_tried = true;
            try
            {
                s_stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
                if (s_stream == null)
                {
                    Plugin.Log.LogInfo("[guards] this build carries no guard bodies (no embedded bundle): guards keep the Dvergr's look");
                    return false;
                }
                s_bundle = AssetBundle.LoadFromStream(s_stream);
                if (s_bundle == null)
                {
                    Plugin.Log.LogError("[guards] the embedded guard bundle did not open: guards keep the Dvergr's look");
                    return false;
                }
                foreach (string name in s_bundle.GetAllAssetNames())
                    if (name.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)) s_available.Add(Path.GetFileNameWithoutExtension(name));
                foreach (var c in s_bundle.LoadAllAssets<AnimationClip>())
                    if (c != null && !c.name.StartsWith("__preview", StringComparison.Ordinal)) s_clips[c.name] = c;
                Plugin.Log.LogInfo($"[guards] guard bodies: {string.Join(", ", s_available.OrderBy(k => k, StringComparer.Ordinal))}; {s_clips.Count} clip(s)");
                return s_available.Contains(MaleBody);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[guards] the embedded guard bundle: {e}");
                return false;
            }
        }

        // The donor's own maps are authored on ITS UVs, and its glow without its mask lights the whole body
        private static readonly string[] DonorMapsToClear = { "_BumpMap", "_SkinBumpMap", "_MetallicGlossMap", "_EmissionMap", "_ChestTex", "_LegsTex", "_MaskTex" };
        private static readonly string[] DonorGlow = { "_EmissionColor", "_EmissiveColor", "_GlowColor" };

        private static Material MaterialFor(string asset, Transform root, Transform ours)
        {
            Material donor = null;
            foreach (var r in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (r == null || r.transform.IsChildOf(ours)) continue;
                if (r.sharedMaterial != null) { donor = r.sharedMaterial; break; }
            }
            if (donor == null) return null;
            string key = asset + "|" + (donor.shader != null ? donor.shader.name : "");
            if (s_materials.TryGetValue(key, out var m) && m != null) return m;
            // A guard keeps the Dvergr's own metal and gloss: armour, and the look approved in ValtimaOnline
            m = new Material(donor) { name = "TS guard body " + asset };
            foreach (string p in DonorMapsToClear) if (m.HasProperty(p)) m.SetTexture(p, null);
            Texture2D albedo = Tex(asset + "_albedo");
            if (albedo != null)
            {
                if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", albedo); else m.mainTexture = albedo;
            }
            Texture2D normal = Tex(asset + "_normal");
            if (normal != null && m.HasProperty("_BumpMap"))
            {
                m.SetTexture("_BumpMap", normal);
                m.EnableKeyword("_NORMALMAP");
            }
            foreach (string p in DonorGlow) if (m.HasProperty(p)) m.SetColor(p, Color.black);
            m.DisableKeyword("_EMISSION");
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.EmissiveIsBlack;
            s_materials[key] = m;
            Plugin.Log.LogInfo($"[guards] body {asset} dressed in a copy of '{donor.name}' ({(donor.shader != null ? donor.shader.name : "?")}), albedo {(albedo != null ? albedo.width + "x" + albedo.height : "missing")}");
            return m;
        }

        // Our skinned renderers get the dressed material; anything else in the prefab (leftover source geometry) is switched off
        private static void Dress(GameObject go, Material dressed)
        {
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;
                if (!(r is SkinnedMeshRenderer smr))
                {
                    r.enabled = false;
                    continue;
                }
                smr.updateWhenOffscreen = true;    // Unity culls by the bind-pose box, which is wrong on these assets
                smr.shadowCastingMode = ShadowCastingMode.On;
                smr.receiveShadows = true;
                if (dressed != null)
                {
                    var mats = smr.sharedMaterials;
                    for (int i = 0; i < mats.Length; i++) mats[i] = dressed;
                    smr.sharedMaterials = mats;
                }
            }
        }

        // How far to lift the body so its lowest POSED vertex sits on the guard's origin (refused past 3 m: a broken bake)
        private static float MeasureLift(Transform parent, GameObject go)
        {
            float lowest = Lowest(parent, go, out bool any);
            if (!any || Mathf.Abs(lowest) > 3f) return 0f;
            return -lowest;
        }

        // The height of a body's lowest posed vertex in its parent's space, from every baked vertex - never the baked mesh's box, which is not the pose's
        internal static float Lowest(Transform parent, GameObject go, out bool any)
        {
            any = false;
            float lowest = 0f;
            foreach (var smr in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr == null || !smr.enabled || smr.sharedMesh == null) continue;
                var baked = new Mesh();
                try
                {
                    smr.BakeMesh(baked);
                    Matrix4x4 toParent = parent.worldToLocalMatrix * smr.transform.localToWorldMatrix;
                    foreach (Vector3 v in baked.vertices)
                    {
                        float y = toParent.MultiplyPoint3x4(v).y;
                        if (!any || y < lowest) { lowest = y; any = true; }
                    }
                }
                catch (Exception e) { Plugin.Debug($"[guards] BakeMesh failed on {smr.name}: {e.Message}"); }
                finally { UnityEngine.Object.Destroy(baked); }
            }
            return lowest;
        }
    }

    // Plays one guard body's clips: locomotion from the body's own displacement, one-shots from its Character (attack, hurt, death), the feet
    // planted after the clips and the atgeir carried after the feet. ValtimaOnline's MeshyBody, for guards only.
    internal sealed class GuardBody : MonoBehaviour
    {
        private enum Slot { Idle, Walk, Run, Attack, SlashRight, SlashLeft, Combo, Hit, Death, Work, Count }
        private const float Blend = 0.15f, RehideSeconds = 2f;

        private Animator _animator;
        private PlayableGraph _graph;
        private AnimationMixerPlayable _mixer;
        private readonly AnimationClipPlayable[] _players = new AnimationClipPlayable[(int)Slot.Count];
        private readonly bool[] _have = new bool[(int)Slot.Count];
        private readonly float[] _weights = new float[(int)Slot.Count];
        private readonly float[] _lengths = new float[(int)Slot.Count];
        private Character _owner;
        private TerritoryGuard _guard;
        private Vector3 _last;
        private bool _hasLast, _wasAttacking, _dead;
        private float _speed, _rehide;
        private Slot _shot = (Slot)(-1);
        private float _shotTime;
        private int _throws;
        private const float GuardPostRadius = 1.2f;
        private float _postCheckAt;
        private bool _atPost = true;

        // the feet: the first frame the idle clip poses the body puts its soles on the guard's origin; then each frame, after the clips, a planted
        // foot stays on the ground under it (a ray per foot, so slopes too), and a foot under its ground comes up by bending its knee
        private const float PlantSeconds = 0.06f, PlantReach = 0.35f;
        private static readonly string[] FootBones = { "LeftFoot", "RightFoot", "LeftToeBase", "RightToeBase" };
        private readonly List<Transform> _feet = new List<Transform>();

        private sealed class Leg
        {
            public Transform Hip, Knee, Ankle, Toe;
            public Vector3 Last;
            public bool HasLast, Planted;
            public float Above;
        }

        private Leg _left, _right;
        private SkinnedMeshRenderer[] _skins;
        private static readonly int GroundMask = LayerMask.GetMask("terrain", "static_solid", "Default", "Default_small", "piece");
        // Casual_Walk's own stride (the pilot guard's planted foot moves back at 0.77 m/s); Valheim's "Walk New" is about 2 m/s, "Run New" 6
        private const float WalkStride = 0.77f, ValheimWalkMps = 2f, ValheimRunMps = 6f, GuardHipsHeight = 1.05f;
        private float _strideScale = 1f;
        private string _asset;
        private float _footAboveSoles, _standLift;
        private bool _measured, _planted;
        private int _age;
        private GuardHalberd _halberd;

        private bool ValheimMotion => GuardBodies.IsHumanoid(_asset);

        internal void Bind(TerritoryGuard guard, string asset)
        {
            _guard = guard;
            _owner = guard.GetComponent<Character>();
            _asset = asset;
            _rehide = RehideSeconds;
            if (_owner != null) _owner.m_onDamaged = (Action<float, Character>)Delegate.Combine(_owner.m_onDamaged, new Action<float, Character>(OnDamaged));
            try { BuildGraph(); }
            catch (Exception e) { Complain("building the animation graph", e); }
        }

        private void BuildGraph()
        {
            _animator = GetComponentInChildren<Animator>(true);
            if (_animator == null) _animator = gameObject.AddComponent<Animator>();
            _animator.applyRootMotion = false;
            _animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;   // until the feet are measured in the idle pose, then culled
            _animator.logWarnings = false;
            _graph = PlayableGraph.Create("TerroritySmellatory.GuardBody");
            _mixer = AnimationMixerPlayable.Create(_graph, (int)Slot.Count);
            var output = AnimationPlayableOutput.Create(_graph, "body", _animator);
            output.SetSourcePlayable(_mixer);
            for (int i = 0; i < (int)Slot.Count; i++)
            {
                AnimationClip clip = Pick((Slot)i);
                if (clip == null) continue;
                _players[i] = AnimationClipPlayable.Create(_graph, clip);
                _players[i].SetApplyFootIK(false);
                _graph.Connect(_players[i], 0, _mixer, i);
                _have[i] = true;
                _lengths[i] = Mathf.Max(clip.length, 0.1f);
                // the standing loops start each guard at his own moment and pace, so a pair at one post do not move as mirror images
                if (i == (int)Slot.Work || i == (int)Slot.Idle)
                {
                    _players[i].SetTime(UnityEngine.Random.value * _lengths[i]);
                    _players[i].SetSpeed(0.92f + UnityEngine.Random.value * 0.16f);
                }
            }
            _weights[(int)Slot.Idle] = 1f;
            Apply();
            _graph.Play();
        }

        private AnimationClip Pick(Slot s)
        {
            if (ValheimMotion)
            {
                // a Humanoid body idles, walks and runs with Valheim's own motions; the watch, the strikes, the flinch and the fall stay Meshy's
                AnimationClip own = s == Slot.Idle ? GuardBodies.ValheimClip("IdleTweaked") : s == Slot.Walk ? GuardBodies.ValheimClip("Walk New")
                                  : s == Slot.Run ? GuardBodies.ValheimClip("Run New") : null;
                if (own != null) return own;
            }
            switch (s)
            {
                case Slot.Idle: return GuardBodies.Clip(_asset, "Idle") ?? GuardBodies.Clip(_asset, "IdleSway");
                case Slot.Walk: return GuardBodies.Clip(_asset, "Walk");   // Casual_Walk and nothing else: no catwalks
                case Slot.Work: return GuardBodies.Clip(_asset, "Work");   // keeping watch at his post
                case Slot.Run: return GuardBodies.Clip(_asset, "Run");
                case Slot.Attack: return GuardBodies.Clip(_asset, "Attack");
                case Slot.SlashRight: return GuardBodies.Clip(_asset, "SlashRight");
                case Slot.SlashLeft: return GuardBodies.Clip(_asset, "SlashLeft");
                case Slot.Combo: return GuardBodies.Clip(_asset, "Combo");
                case Slot.Hit: return GuardBodies.Clip(_asset, "Hit");
                case Slot.Death: return GuardBodies.Clip(_asset, "Death") ?? GuardBodies.Clip(_asset, "Dead");
                default: return null;
            }
        }

        // a posted guard within GuardPostRadius of his post keeps watch; a called guard, or one away from his post, idles
        private bool AtPost()
        {
            if (Time.time < _postCheckAt) return _atPost;
            _postCheckAt = Time.time + 0.5f;
            Transform root = transform.parent;
            BaseAI ai = root != null ? root.GetComponent<BaseAI>() : null;
            _atPost = _guard != null && _guard.HoldsPost && ai != null && Utils.DistanceXZ(ai.m_spawnPoint, root.position) <= GuardPostRadius;
            return _atPost;
        }

        private void OnDamaged(float damage, Character attacker)
        {
            if (!_dead && damage > 0f && (int)_shot < 0) Fire(Slot.Hit);
        }

        private void Fire(Slot s)
        {
            if (!_have[(int)s]) return;
            _shot = s;
            _shotTime = 0f;
            _players[(int)s].SetTime(0.0);
            _players[(int)s].SetSpeed(1.0);
        }

        private void Update()
        {
            try
            {
                float dt = Mathf.Min(Time.deltaTime, 0.25f);
                Vector3 now = transform.position;
                if (_hasLast && dt > 0.0001f)
                {
                    Vector3 moved = now - _last;
                    moved.y = 0f;
                    _speed = Mathf.Lerp(_speed, moved.magnitude / dt, 1f - Mathf.Exp(-dt / 0.15f));
                }
                _last = now;
                _hasLast = true;

                if (_owner != null)
                {
                    if (!_dead && _owner.IsDead())
                    {
                        _dead = true;
                        Fire(Slot.Death);
                    }
                    bool attacking = !_dead && _owner.InAttack();
                    if (attacking && !_wasAttacking)
                    {
                        // a halberd is swung with both hands: the guard pack's two-handed overhead chop only
                        if (_halberd != null && _halberd.TwoHanded && _have[(int)Slot.Attack]) Fire(Slot.Attack);
                        else
                        {
                            Slot[] swings = { Slot.Attack, Slot.SlashRight, Slot.SlashLeft, Slot.Combo };
                            var have = swings.Where(x => _have[(int)x]).ToArray();
                            if (have.Length > 0) Fire(have[UnityEngine.Random.Range(0, have.Length)]);
                        }
                    }
                    _wasAttacking = attacking;
                }

                // the one-shot runs to 90% of its clip, then hands back; the fall holds its last frame
                if ((int)_shot >= 0)
                {
                    _shotTime += dt;
                    float len = _lengths[(int)_shot];
                    if (_shot == Slot.Death && _shotTime >= len)
                    {
                        _players[(int)_shot].SetSpeed(0.0);
                        _players[(int)_shot].SetTime(len);
                    }
                    else if (_shot != Slot.Death && _shotTime >= len * 0.9f) _shot = (Slot)(-1);
                }

                Slot loco = _speed < 0.15f ? (_have[(int)Slot.Work] && AtPost() ? Slot.Work : Slot.Idle) : (_speed < 3.2f || !_have[(int)Slot.Run]) ? Slot.Walk : Slot.Run;
                if (!_have[(int)loco] || !_measured) loco = Slot.Idle;   // the feet are measured in the IDLE pose: nothing else plays before that
                bool vm = ValheimMotion;
                if (_have[(int)Slot.Walk]) _players[(int)Slot.Walk].SetSpeed(Mathf.Clamp(_speed / ((vm ? ValheimWalkMps : WalkStride) * _strideScale), 0.5f, 2.4f));
                if (_have[(int)Slot.Run]) _players[(int)Slot.Run].SetSpeed(Mathf.Clamp(_speed / (vm ? ValheimRunMps * _strideScale : 5f), 0.6f, 1.5f));
                float step = 1f - Mathf.Exp(-dt / (Blend * 0.5f));
                for (int i = 0; i < (int)Slot.Count; i++)
                {
                    float target = (int)_shot >= 0 ? (i == (int)_shot ? 1f : 0f) : (i == (int)loco ? 1f : 0f);
                    _weights[i] = Mathf.Lerp(_weights[i], target, step);
                }
                Apply();

                _rehide -= dt;
                if (_rehide <= 0f) HideDonorNow();
            }
            catch (Exception e) { Complain("ticking", e); }
        }

        /// <summary>The Dvergr's renderers off again (VisEquipment has just hung an item on its hidden hand, or the 2 s re-hide is due).</summary>
        internal void HideDonorNow()
        {
            _rehide = RehideSeconds;
            if (transform.parent != null) GuardBodies.HideDonor(transform.parent, transform);
        }

        private void LateUpdate()
        {
            if (_dead) return;
            if (!_measured)
            {
                if (++_age >= 3) MeasureFeet();   // two frames in, the idle clip has surely posed it
                return;
            }
            if (!Visible()) return;
            float dt = Mathf.Min(Time.deltaTime, 0.25f);
            if (dt <= 0f) return;
            if (_planted) PlantFeet(dt);
            // the arms after the feet: the carry while no one-shot has the arms, both hands on the shaft while he strikes
            if (_halberd != null)
            {
                bool striking = _shot == Slot.Attack || _shot == Slot.SlashRight || _shot == Slot.SlashLeft || _shot == Slot.Combo;
                try { _halberd.Carry(dt, (int)_shot < 0, striking); }
                catch (Exception e) { _halberd = null; Complain("carrying the atgeir", e); }
            }
        }

        private void PlantFeet(float dt)
        {
            try
            {
                Transform root = transform.parent;
                if (_left == null || _right == null)
                {
                    float soles = LowestFoot() - _footAboveSoles;
                    if (Mathf.Abs(soles) < 0.002f) return;
                    Vector3 q = transform.localPosition;
                    q.y = Mathf.Clamp(q.y - soles * (1f - Mathf.Exp(-dt / PlantSeconds)), _standLift - PlantReach, _standLift + PlantReach);
                    transform.localPosition = q;
                    return;
                }
                Stance(_left, dt);
                Stance(_right, dt);
                float need = float.NegativeInfinity;
                foreach (Leg leg in new[] { _left, _right })
                    if (leg.Planted) need = Mathf.Max(need, Sole(leg, root) - Ground(leg, root));
                if (!float.IsNegativeInfinity(need) && Mathf.Abs(need) > 0.002f)
                {
                    Vector3 p = transform.localPosition;
                    p.y = Mathf.Clamp(p.y - need * (1f - Mathf.Exp(-dt / PlantSeconds)), _standLift - PlantReach, _standLift + PlantReach);
                    transform.localPosition = p;
                }
                foreach (Leg leg in new[] { _left, _right })
                {
                    float under = Ground(leg, root) - Sole(leg, root);
                    if (under > 0.003f) Raise(leg, under * root.lossyScale.y);
                }
            }
            catch (Exception e) { _planted = false; Complain("planting the feet", e); }
        }

        private bool Visible()
        {
            if (_skins == null) _skins = GetComponentsInChildren<SkinnedMeshRenderer>(true);
            foreach (var r in _skins) if (r != null && r.isVisible) return true;
            return false;
        }

        private void Stance(Leg leg, float dt)
        {
            Vector3 now = transform.InverseTransformPoint(leg.Toe.position);
            if (leg.HasLast)
            {
                Vector3 v = transform.localRotation * ((now - leg.Last) / dt);
                leg.Planted = v.z < 0.15f && Mathf.Abs(v.y) < 0.3f;
            }
            leg.Last = now;
            leg.HasLast = true;
        }

        private static float Sole(Leg leg, Transform root) =>
            Mathf.Min(root.InverseTransformPoint(leg.Ankle.position).y, root.InverseTransformPoint(leg.Toe.position).y) - leg.Above;

        private static float Ground(Leg leg, Transform root)
        {
            Vector3 at = leg.Toe.position;
            if (!Physics.Raycast(at + Vector3.up * 0.6f, Vector3.down, out RaycastHit hit, 1.4f, GroundMask, QueryTriggerInteraction.Ignore)) return 0f;
            return Mathf.Clamp(root.InverseTransformPoint(hit.point).y, -0.3f, 0.3f);
        }

        // lifts the ankle by d metres (world) by bending the knee and swinging the thigh; the foot keeps its turn in the world
        private static void Raise(Leg leg, float d)
        {
            Vector3 h = leg.Hip.position, k = leg.Knee.position, a = leg.Ankle.position;
            Vector3 target = a + Vector3.up * d;
            float la = (k - h).magnitude, lb = (a - k).magnitude;
            if (la < 1e-4f || lb < 1e-4f) return;
            float lt = Mathf.Clamp((target - h).magnitude, Mathf.Abs(la - lb) + 1e-4f, la + lb - 1e-4f);
            Quaternion foot = leg.Ankle.rotation;
            Vector3 axis = Vector3.Cross(h - k, a - k);
            if (axis.sqrMagnitude > 1e-12f)
            {
                float cur = Vector3.Angle(h - k, a - k);
                float want = Mathf.Acos(Mathf.Clamp((la * la + lb * lb - lt * lt) / (2f * la * lb), -1f, 1f)) * Mathf.Rad2Deg;
                leg.Knee.rotation = Quaternion.AngleAxis(want - cur, axis.normalized) * leg.Knee.rotation;
            }
            leg.Hip.rotation = Quaternion.FromToRotation(leg.Ankle.position - h, target - h) * leg.Hip.rotation;
            leg.Ankle.rotation = foot;
        }

        // the first frame after the idle clip posed the body: soles on the ground, how high the feet stand over them; then the atgeir in his hand
        private void MeasureFeet()
        {
            try { MeasureFeetCore(); }
            catch (Exception e) { _planted = false; Complain("measuring the feet", e); }
            if (transform.parent == null || _halberd != null) return;
            try { _halberd = GuardHalberd.Attach(transform.parent, gameObject, _asset); }
            catch (Exception e) { _halberd = null; Complain("putting the atgeir in his hand", e); }
        }

        private void MeasureFeetCore()
        {
            _measured = true;
            if (_animator != null) _animator.cullingMode = AnimatorCullingMode.CullUpdateTransforms;
            Transform root = transform.parent;
            if (root == null) return;
            float soles = GuardBodies.Lowest(root, gameObject, out bool any);
            if (!any || Mathf.Abs(soles) > 1f) return;
            Vector3 p = transform.localPosition;
            p.y -= soles;
            transform.localPosition = p;
            _standLift = p.y;
            Transform hips = GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "Hips");
            if (hips != null && _asset != GuardBodies.MaleBody)   // the pilot guard's stride is the measure itself
            {
                float hipsHeight = root.InverseTransformPoint(hips.position).y;
                if (hipsHeight > 0.4f && hipsHeight < 2f) _strideScale = Mathf.Clamp(hipsHeight / GuardHipsHeight, 0.7f, 1.25f);
            }
            foreach (var t in GetComponentsInChildren<Transform>(true))
                if (Array.IndexOf(FootBones, t.name) >= 0) _feet.Add(t);
            if (_feet.Count > 0)
            {
                _footAboveSoles = LowestFoot();
                _planted = true;
                _left = MakeLeg("Left");
                _right = MakeLeg("Right");
                if (_left != null && _right != null) MeasureSoles(root);
            }
        }

        // each foot's sole in the idle pose: the lowest baked vertices near the ground, each given to the nearer foot (the rig is lopsided)
        private void MeasureSoles(Transform root)
        {
            float lowL = float.MaxValue, lowR = float.MaxValue;
            Vector3 l = root.InverseTransformPoint((_left.Ankle.position + _left.Toe.position) * 0.5f), r = root.InverseTransformPoint((_right.Ankle.position + _right.Toe.position) * 0.5f);
            var baked = new Mesh();
            try
            {
                foreach (var smr in GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (smr == null || !smr.enabled || smr.sharedMesh == null) continue;
                    smr.BakeMesh(baked);
                    Matrix4x4 m = root.worldToLocalMatrix * smr.transform.localToWorldMatrix;
                    foreach (Vector3 v0 in baked.vertices)
                    {
                        Vector3 v = m.MultiplyPoint3x4(v0);
                        if (v.y > 0.3f) continue;
                        float dl = (v.x - l.x) * (v.x - l.x) + (v.z - l.z) * (v.z - l.z), dr = (v.x - r.x) * (v.x - r.x) + (v.z - r.z) * (v.z - r.z);
                        if (dl <= dr) lowL = Mathf.Min(lowL, v.y);
                        else lowR = Mathf.Min(lowR, v.y);
                    }
                }
            }
            finally { Destroy(baked); }
            float BoneLow(Leg leg) => Mathf.Min(root.InverseTransformPoint(leg.Ankle.position).y, root.InverseTransformPoint(leg.Toe.position).y);
            _left.Above = BoneLow(_left) - (lowL < float.MaxValue ? lowL : 0f);
            _right.Above = BoneLow(_right) - (lowR < float.MaxValue ? lowR : 0f);
        }

        private Leg MakeLeg(string side)
        {
            Transform Find(string name) => GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == name);
            var leg = new Leg { Hip = Find(side + "UpLeg"), Knee = Find(side + "Leg"), Ankle = Find(side + "Foot"), Toe = Find(side + "ToeBase") };
            return leg.Hip != null && leg.Knee != null && leg.Ankle != null && leg.Toe != null ? leg : null;
        }

        private float LowestFoot()
        {
            Transform root = transform.parent;
            float lowest = float.MaxValue;
            foreach (var t in _feet)
                if (t != null) lowest = Mathf.Min(lowest, root.InverseTransformPoint(t.position).y);
            return lowest == float.MaxValue ? _footAboveSoles : lowest;
        }

        private void Apply()
        {
            if (!_mixer.IsValid()) return;
            float sum = 0f;
            for (int i = 0; i < (int)Slot.Count; i++) if (_have[i]) sum += _weights[i];
            for (int i = 0; i < (int)Slot.Count; i++)
                if (_have[i]) _mixer.SetInputWeight(i, sum > 0.0001f ? _weights[i] / sum : (i == (int)Slot.Idle ? 1f : 0f));
        }

        private void OnDestroy()
        {
            if (_owner != null) _owner.m_onDamaged = (Action<float, Character>)Delegate.Remove(_owner.m_onDamaged, new Action<float, Character>(OnDamaged));
            try { if (_graph.IsValid()) _graph.Destroy(); }
            catch (Exception e) { Complain("destroying the animation graph", e); }
        }

        private void Complain(string what, Exception e)
        {
            if (_throws++ < 3) Plugin.Log.LogError($"[guards] a guard's body: {what} threw: {e}");
        }
    }
}
