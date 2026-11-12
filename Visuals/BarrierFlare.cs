using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace TerroritySmellatory
{
    /// <summary>
    /// 0.3.0: the runic barrier made visible - a patch of blue, rune-carved hexagon shield (Wakanda's, carved with Njord's
    /// runes) that flares on the territory's edge where something pushes against it, ripples out from the point of
    /// contact and fades. 0.3.1 rebuilt its drawing on Njord's live, in-game-proven path (see below); the look itself is
    /// Visuals/BarrierPattern.cs (plain numbers, tested and previewed offline).
    /// </summary>
    /// <remarks>
    /// THE PIECES. BarrierFlare (this file) is the front door and the pool: it counts, throttles, builds the assets and
    /// plays the sound. BarrierPatch is one pooled wall patch and its fader. BarrierRunes is Njord's rune burst, with its
    /// glyphs in BarrierRuneGlyphs. BarrierMaterials is Njord's shader-and-blend recipe. Zero shipped assets: every
    /// texture is drawn on the CPU (SetPixels32 + Apply(true), the way Njord builds its runes) and the shader is a stock one.
    ///
    /// WHY THE REBUILD. 0.3.0 uploaded its texture with LoadRawTextureData, on a texture created with a mip chain but
    /// filled with only the top level's bytes; Unity threw "not enough data provided" on the first flare, and the flare
    /// then switched itself off for the whole session, quietly. Nothing was drawn and only a warning in the log said so.
    /// Now nothing here uploads raw bytes, a failure is an ERROR in the log naming the exception, and it is retried
    /// (once at the very next flare, then at most every 60 s) instead of being given up for good.
    ///
    /// MULTIPLAYER. Nothing here is sent anywhere. Every client detects the push itself from the positions and velocities
    /// vanilla already syncs (Rules/Barrier.cs) and draws its own flare - Njord's "derived independently on every client,
    /// no RPC, no ZDO". Wonderland's RPC whitelist kicks a client that sends an RPC it does not know, so an RPC is not
    /// an option anyway. Everything is inside a try/catch: a cosmetic failure never reaches gameplay (pattern 12).
    /// </remarks>
    internal static class BarrierFlare
    {
        /// <summary>
        /// Flare calls accepted so far (a finite position and a usable direction), counted before anything else is
        /// decided - before the config switch, the graphics check, the throttle - so a headless e2e rig, which draws
        /// nothing, can still see that something pushed. Wraps silently, which nothing minds.
        /// </summary>
        internal static int Requested;

        /// <summary>Flares actually put on screen (a new one, or a restarted one). Zero on a machine with no graphics device or with BarrierEffects off.</summary>
        internal static int Drawn;

        private const int MaxActive = 24;          // pool size; past it the oldest flare is taken over
        private const float MergeRadius = 1.5f;    // a push this close to a live flare belongs to it
        private const float RestartAfter = 0.3f;   // ...and starts a fresh ripple in it once its last one has spread this far (a steady push pulses)
        private const float SoundGap = 0.3f;       // one sound at most this often, however many flares
        private const float BurstGap = 0.9f;       // one rune burst at most this often from one patch: a steady push restarts the ripple every 0.3 s, and a burst each time would flood the air
        private const float RetryGap = 60f;        // after the second failed build, try again this often (real seconds)

        private static readonly List<BarrierPatch> _live = new List<BarrierPatch>(MaxActive);
        private static readonly Queue<BarrierPatch> _free = new Queue<BarrierPatch>(MaxActive);
        private static GameObject _root;
        private static int _made;
        private static Texture2D _texture;
        private static Material _material;
        private static int _graphics;                       // 0 unknown, 1 yes, -1 none
        private static float _lastSound = -100f;
        private static bool _soundFailed;
        private static GameObject _soundPrefab;
        private static float _lastError = -100f;
        /// <summary>
        /// TEST SEAM, like the rig's IsAdminSeam: the next this-many wall builds throw before building anything. The e2e
        /// rig's graphics probe (--spike a) sets it to 1 to prove that a failed build is logged as an ERROR and retried at the
        /// very next flare, instead of switching the barrier's light off for the session as 0.3.0 did. Nothing else touches it.
        /// </summary>
        internal static int InjectBuildFaults;

        private static int _wallFailures, _runeFailures;
        private static float _wallRetryAt, _runeRetryAt;

        /// <summary>
        /// Flare the barrier at a point of the territory's edge. <paramref name="at"/> is the point of contact on the
        /// edge (world position; its y is the ground under it), <paramref name="outward"/> the horizontal unit vector
        /// pointing out of the territory there, <paramref name="height"/> roughly how tall the thing pushing is, and
        /// <paramref name="strength"/> 0-1, from a light touch to a hard slam. Cheap to call often: it pools, and
        /// throttles flares at the same spot itself. Does nothing when TSConfig.BarrierEffects is off or there is no
        /// graphics device (a dedicated server, a -nographics client).
        /// </summary>
        internal static void Flare(Vector3 at, Vector3 outward, float height, float strength)
        {
            try
            {
                if (!Finite(at) || !Finite(outward)) return;
                outward.y = 0f;
                float len = outward.magnitude;
                if (len < 1e-4f) return;
                outward /= len;

                Requested++;

                if (TSConfig.BarrierEffects != null && !TSConfig.BarrierEffects.Value) return;
                if (NoGraphics()) return;
                Spawn(at, outward, height, Mathf.Clamp01(float.IsNaN(strength) ? 0.5f : strength));
            }
            catch (Exception ex)
            {
                Failed("flare", ex);
            }
        }

        // ── spawning ─────────────────────────────────────────────────

        private static void Spawn(Vector3 at, Vector3 outward, float height, float strength)
        {
            float now = Time.time;
            if (!EnsureWall()) return;

            BarrierPatch patch = null;
            bool joined = false;
            for (int i = 0; i < _live.Count && !joined; i++)
            {
                BarrierPatch s = _live[i];
                if (s == null || !s.Live) continue;
                if ((s.At - at).sqrMagnitude > MergeRadius * MergeRadius || Vector3.Dot(s.Outward, outward) < 0.3f) continue;
                patch = s;
                joined = true;
            }

            if (joined)
            {
                // Still on its first moments: leave it alone, only let a harder push win.
                if (now - patch.Born < RestartAfter)
                {
                    if (strength > patch.Look.Strength + 0.15f) patch.Layout(height, strength, false, 0);
                    return;
                }
            }
            else
            {
                patch = Take();
                if (patch == null) return;
                patch.At = at;
                patch.Outward = outward;
                patch.LastBurst = -100f;
            }

            patch.Layout(height, strength, true, (int)(patch.At.x * 7.3f) ^ ((int)(patch.At.z * 11.1f) * 31) ^ Drawn);
            patch.Show(now);
            Drawn++;

            // Njord's signature: a few runes rise off the impact and dissolve. Their own build can fail without costing the wall.
            if (now - patch.LastBurst >= BurstGap && EnsureRunes())
            {
                patch.LastBurst = now;
                BarrierRunes.Burst(patch.At + Vector3.up * patch.Look.ImpactV, patch.Outward, strength);
            }
            Sound(patch.At + Vector3.up * Mathf.Min(1.2f, height * 0.5f), strength, now);
        }

        /// <summary>A patch from the pool (a new one while there is room, else the oldest live one, taken over).</summary>
        private static BarrierPatch Take()
        {
            if (!Alive(_root))
            {
                // First use, or the scene took our objects with it: start the pool over.
                _live.Clear();
                _free.Clear();
                _made = 0;
                _root = new GameObject("TS_BarrierFlares");
                Object.DontDestroyOnLoad(_root);
            }

            while (_free.Count > 0)
            {
                BarrierPatch p = _free.Dequeue();
                if (p == null) continue;   // destroyed under us
                _live.Add(p);
                return p;
            }
            if (_made < MaxActive)
            {
                BarrierPatch p = BarrierPatch.Create(_root.transform, _made++, _material);
                _live.Add(p);
                return p;
            }

            BarrierPatch oldest = null;
            for (int i = 0; i < _live.Count; i++)
                if (_live[i] != null && (oldest == null || _live[i].Born < oldest.Born)) oldest = _live[i];
            return oldest;
        }

        /// <summary>The patch's fader calls this when its life is over: off, and back to the pool.</summary>
        internal static void Retire(BarrierPatch p)
        {
            if (p == null) return;
            p.Hide();
            _live.Remove(p);
            if (Alive(_root)) _free.Enqueue(p);
        }

        // ── the texture and the material ─────────────────────────────

        /// <summary>
        /// The wall's texture and material, built once. A failure is logged as an ERROR with its exception and the flare
        /// carries on trying: at the very next flare, and after that at most once a minute, so a transient fault cannot
        /// cost the barrier its light for the whole session, and a permanent one cannot flood the log.
        /// </summary>
        private static bool EnsureWall()
        {
            if (Alive(_texture) && Alive(_material)) return true;
            float real = Time.realtimeSinceStartup;
            if (real < _wallRetryAt) return false;
            try
            {
                if (InjectBuildFaults > 0)
                {
                    InjectBuildFaults--;
                    throw new InvalidOperationException("an injected build fault (BarrierFlare.InjectBuildFaults, the e2e rig's retry proof)");
                }
                if (!Alive(_texture))
                {
                    // Baked on the CPU and uploaded as Color32s, mip chain generated by Apply(true). Never
                    // LoadRawTextureData: it wants the mips' bytes as well, and that is what threw in 0.3.0.
                    byte[] bytes = BarrierPattern.TextureBytes();
                    var px = new Color32[BarrierPattern.TexSize * BarrierPattern.TexSize];
                    for (int i = 0; i < px.Length; i++) px[i] = new Color32(bytes[i * 4], bytes[i * 4 + 1], bytes[i * 4 + 2], bytes[i * 4 + 3]);
                    var tex = new Texture2D(BarrierPattern.TexSize, BarrierPattern.TexSize, TextureFormat.RGBA32, true)
                    {
                        name = "TS_BarrierLattice",
                        wrapMode = TextureWrapMode.Repeat,
                        filterMode = FilterMode.Trilinear,
                        anisoLevel = 4,
                        hideFlags = HideFlags.HideAndDontSave,
                    };
                    tex.SetPixels32(px);
                    tex.Apply(true);
                    _texture = tex;
                }
                if (!Alive(_material))
                {
                    _material = BarrierMaterials.Create("TS_BarrierFlare", _texture, true);
                    // Patches built before a material was lost (a scene change) point at the dead one.
                    foreach (BarrierPatch p in _free) if (p != null) p.Renderer.sharedMaterial = _material;
                    for (int i = 0; i < _live.Count; i++) if (_live[i] != null) _live[i].Renderer.sharedMaterial = _material;
                }
                _wallFailures = 0;
                return true;
            }
            catch (Exception ex)
            {
                _wallFailures++;
                _wallRetryAt = _wallFailures <= 1 ? 0f : real + RetryGap;   // the first failure is retried at the very next flare
                Plugin.Log.LogError($"Barrier flare: could not build its texture or material (failure {_wallFailures}; " +
                                    (_wallFailures <= 1 ? "trying again at the next flare" : "trying again in a minute") + $"), so the barrier will not flash. Gameplay is unaffected. {ex}");
                return false;
            }
        }

        /// <summary>The rune sprites, built once, with the same retry rule as the wall; the wall flashes without them.</summary>
        private static bool EnsureRunes()
        {
            if (BarrierRunes.Ready) return true;
            float real = Time.realtimeSinceStartup;
            if (real < _runeRetryAt) return false;
            try
            {
                BarrierRunes.Ensure();
                _runeFailures = 0;
                return true;
            }
            catch (Exception ex)
            {
                _runeFailures++;
                _runeRetryAt = _runeFailures <= 1 ? 0f : real + RetryGap;
                Plugin.Log.LogError($"Barrier flare: could not build the rune sprites (failure {_runeFailures}; " +
                                    (_runeFailures <= 1 ? "trying again at the next flare" : "trying again in a minute") + $"), so the flare will show its wall without runes. {ex}");
                return false;
            }
        }

        private static bool Alive(Object o) => !ReferenceEquals(o, null) && o != null;

        // ── the sound ────────────────────────────────────────────────

        /// <summary>
        /// The vanilla sound of a force-shield taking a hit: the Mistlands shield generator's own, which is a bare sound
        /// prefab (a ZSFX with six clips, nothing to look at). Found by name in ZNetScene like any prefab, and played
        /// here quietly. Vanilla's effects are networked objects, so each game that plays one creates a ZDO the others
        /// then play too; every client detects the push for itself (no RPCs), so that would multiply the sound by the
        /// player count. The prefab is therefore instantiated with ZNetView's own throwaway-instance switch on
        /// (m_forceDisableInit, what Inventory.AddItem uses), which drops its ZNetView before it can make a ZDO: a plain
        /// local sound that times itself out (its TimedDestruction finds no ZNetView and destroys it).
        /// </summary>
        private const string SoundPrefab = "sfx_shieldgenerator_hit";

        private static void Sound(Vector3 at, float strength, float now)
        {
            if (_soundFailed || now - _lastSound < SoundGap) return;
            _lastSound = now;
            try
            {
                if (ZNetScene.instance == null) return;
                if (!Alive(_soundPrefab)) _soundPrefab = ZNetScene.instance.GetPrefab(SoundPrefab);
                if (!Alive(_soundPrefab) || _soundPrefab.GetComponentInChildren<ZSFX>(true) == null)
                {
                    _soundFailed = true;   // renamed or gone in a game update: silent, and never louder than intended
                    Plugin.Log.LogWarning($"Barrier flare: the sound prefab '{SoundPrefab}' was not found; the barrier will flash silently.");
                    return;
                }

                bool was = ZNetView.m_forceDisableInit;
                GameObject go;
                ZNetView.m_forceDisableInit = true;
                try { go = Object.Instantiate(_soundPrefab, at, Quaternion.identity); }
                finally { ZNetView.m_forceDisableInit = was; }

                ZSFX sfx = go.GetComponentInChildren<ZSFX>(true);
                if (sfx != null)
                {
                    // Vanilla plays it at volume 1.3-1.5. A barrier hum is background: a fifth of that for a touch, under half for a slam.
                    float k = 0.2f + 0.25f * strength;
                    sfx.m_minVol *= k;
                    sfx.m_maxVol *= k;
                    // Its captions read "shield generator absorbing projectile", which this is not.
                    sfx.m_closedCaptionToken = "";
                    sfx.m_secondaryCaptionToken = "";
                }
                Object.Destroy(go, 4f);   // its own timer is 2 s; this only guarantees it if that timer does not run
            }
            catch (Exception ex)
            {
                _soundFailed = true;
                Plugin.Log.LogWarning($"Barrier flare: the sound failed, so the barrier will flash silently. {ex.Message}");
            }
        }

        // ── small things ─────────────────────────────────────────────

        private static bool NoGraphics()
        {
            if (_graphics == 0) _graphics = SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null ? -1 : 1;
            return _graphics < 0;
        }

        private static bool Finite(Vector3 v) => !(float.IsNaN(v.x) || float.IsInfinity(v.x) || float.IsNaN(v.y) || float.IsInfinity(v.y) || float.IsNaN(v.z) || float.IsInfinity(v.z));

        // A cosmetic failure must never reach gameplay, and a throw on every push or frame would bury the log: one line
        // per 30 s. It is an ERROR because a throw here is a bug that costs the player the effect (the e2e rig's H08 fails on it).
        internal static void Failed(string what, Exception ex)
        {
            float now = Time.realtimeSinceStartup;
            if (now - _lastError < 30f) return;
            _lastError = now;
            Plugin.Log.LogError($"Barrier flare {what} failed (non-fatal, repeats suppressed for 30 s): {ex}");
        }
    }
}
