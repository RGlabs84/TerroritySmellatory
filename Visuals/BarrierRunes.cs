using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;

namespace TerroritySmellatory
{
    /// <summary>
    /// 0.3.1: the rune burst - Njord's signature. A few glowing rune sprites rise from the point of impact, slow to a
    /// hover and dissolve, the way Njord's runes burst off a ship when its helm is claimed.
    /// </summary>
    /// <remarks>
    /// Njord's own machinery, adapted (VFX/NjordRuneSpriteTrail.cs, the path its rune trail draws on in real Valheim):
    /// the 16 glyph textures (BarrierRuneGlyphs, built with SetPixels32 + Apply(true) - the mip chain is generated, never
    /// uploaded by hand), one Sprite each, a pool of pooled SpriteRenderer GameObjects in a Queue under a
    /// DontDestroyOnLoad root, and a fader MonoBehaviour that integrates velocity with drag and a breath of gravity,
    /// holds full brightness for 30 % of the life, then shimmers out while the scale dissolves to nothing.
    /// Like the rest of the flare it is derived independently on every client, with no RPC and no ZDO.
    /// </remarks>
    internal static class BarrierRunes
    {
        private const int PoolInitial = 12, PoolMax = 48;
        private const float Drag = 1.5f;          // exponential velocity decay per second: a rune slows to a hover (Njord's is 2.8; ours rise about 1.5-2 m in their 1.5 s)
        private const float Gravity = -0.15f;     // Njord's -0.35, eased: they rise, they do not sink back
        private const float SpriteUnits = BarrierRuneGlyphs.Size / 100f;   // a sprite of 128 px at 100 px per unit

        private static Sprite[] _sprites;
        private static Material _material;
        private static GameObject _root;
        private static readonly Queue<RuneFader> Pool = new Queue<RuneFader>(PoolMax);
        private static int _made;

        private static bool Alive(Object o) => !ReferenceEquals(o, null) && o != null;

        /// <summary>True once the sprites and their material exist.</summary>
        internal static bool Ready => _sprites != null && Alive(_material) && Alive(_sprites[0]);

        /// <summary>
        /// Builds the 16 rune sprites and the sprite material (once; again if the game destroyed them). Throws on
        /// failure, leaving nothing half-built, so BarrierFlare reports it and retries.
        /// </summary>
        internal static void Ensure()
        {
            if (Ready) return;

            var sprites = new Sprite[BarrierRuneGlyphs.Count];
            var made = new List<Object>(BarrierRuneGlyphs.Count * 2);
            try
            {
                var bytes = new byte[BarrierRuneGlyphs.Size * BarrierRuneGlyphs.Size * 4];
                var px = new Color32[BarrierRuneGlyphs.Size * BarrierRuneGlyphs.Size];
                for (int g = 0; g < BarrierRuneGlyphs.Count; g++)
                {
                    BarrierRuneGlyphs.Draw(g, bytes);
                    for (int i = 0; i < px.Length; i++) px[i] = new Color32(bytes[i * 4], bytes[i * 4 + 1], bytes[i * 4 + 2], bytes[i * 4 + 3]);
                    // Njord's: mipmapped RGBA32 (soft edges at a distance), bilinear, clamped.
                    var tex = new Texture2D(BarrierRuneGlyphs.Size, BarrierRuneGlyphs.Size, TextureFormat.RGBA32, true)
                    {
                        name = "TS_BarrierRune" + g,
                        filterMode = FilterMode.Bilinear,
                        wrapMode = TextureWrapMode.Clamp,
                        hideFlags = HideFlags.HideAndDontSave,
                    };
                    made.Add(tex);
                    tex.SetPixels32(px);
                    tex.Apply(true);
                    // FullRect: a plain quad, no alpha-tracing of the outline (Njord's default Tight does that work for 16 sprites at start-up).
                    Sprite sp = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
                    sp.name = "TS_BarrierRune" + g;
                    sp.hideFlags = HideFlags.HideAndDontSave;
                    made.Add(sp);
                    sprites[g] = sp;
                }
                Material mat = BarrierMaterials.Create("TS_BarrierRunes", null, false);
                made.Add(mat);
                _sprites = sprites;
                _material = mat;
            }
            catch
            {
                for (int i = 0; i < made.Count; i++) if (Alive(made[i])) Object.Destroy(made[i]);
                throw;
            }
            // Pooled renderers built on the old sprites or material point at dead ones: drop them with their root.
            Pool.Clear();
            _made = 0;
            if (Alive(_root)) Object.Destroy(_root);
            _root = null;
        }

        /// <summary>Throws a burst of runes from <paramref name="origin"/>, rising; <paramref name="outward"/> is the wall's normal, so the burst leans a little out of it.</summary>
        internal static void Burst(Vector3 origin, Vector3 outward, float strength)
        {
            if (!Ready) return;
            if (!Alive(_root))
            {
                Pool.Clear();
                _made = 0;
                _root = new GameObject("TS_BarrierRunes");
                Object.DontDestroyOnLoad(_root);
                for (int i = 0; i < PoolInitial; i++) Pool.Enqueue(NewFader());
            }

            Vector3 along = Vector3.Cross(Vector3.up, outward);   // the wall's own direction, left to right
            int count = BarrierPattern.RuneCount(strength);
            for (int i = 0; i < count; i++)
            {
                RuneFader f = Take();
                if (f == null) return;   // pool exhausted: skip, never grow without bound
                // Spread evenly across the strike (with a little jitter) so no two runes start on top of each other, which
                // read as one tangled scribble in the first frames; Njord's own burst is a random sphere, at 2.5 s and a
                // hand's breadth wide, which is why it needs no such care.
                float slot = (i + 0.5f) / count * 2f - 1f;                          // -1..1
                float across = slot * 1.1f + Random.Range(-0.12f, 0.12f);
                Vector3 pos = origin + along * across + Vector3.up * Random.Range(-0.15f, 0.2f) + outward * 0.15f;
                Vector3 vel = Vector3.up * Random.Range(2.0f, 3.0f) + along * (slot * 0.25f) + outward * Random.Range(0.1f, 0.4f);
                float size = BarrierPattern.RuneSize(strength) * Random.Range(0.85f, 1.15f);
                f.Play(_sprites[Random.Range(0, _sprites.Length)], pos, vel, size / SpriteUnits, Random.Range(-25f, 25f));
            }
        }

        private static RuneFader Take()
        {
            if (Pool.Count > 0)
            {
                RuneFader f = Pool.Dequeue();
                if (f != null) return f;   // a rune the scene destroyed is skipped, not returned
                return Take();
            }
            return _made < PoolMax ? NewFader() : null;
        }

        private static RuneFader NewFader()
        {
            var go = new GameObject("TS_Rune");
            go.transform.SetParent(_root.transform, false);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sharedMaterial = _material;
            sr.sortingLayerName = "Default";
            sr.sortingOrder = 5000;   // Njord's: drawn over what the wall's own glow is doing
            sr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            sr.receiveShadows = false;
            var f = go.AddComponent<RuneFader>();
            f.Init(sr);
            go.SetActive(false);
            _made++;
            return f;
        }

        internal static void Return(RuneFader f)
        {
            if (f == null) return;
            f.gameObject.SetActive(false);
            if (Alive(_root) && Pool.Count < PoolMax) Pool.Enqueue(f);
            else Object.Destroy(f.gameObject);
        }

        /// <summary>Njord's RuneSprite_Fader: one rune's short life - drift, dissolve, hand itself back.</summary>
        internal sealed class RuneFader : MonoBehaviour
        {
            private SpriteRenderer _sr;
            private Vector3 _vel;
            private float _age, _size;
            private Quaternion _roll;

            internal void Init(SpriteRenderer sr) => _sr = sr;

            internal void Play(Sprite sprite, Vector3 pos, Vector3 vel, float scale, float rollDegrees)
            {
                _sr.sprite = sprite;
                _vel = vel;
                _age = 0f;
                _size = scale;
                _roll = Quaternion.Euler(0f, 0f, rollDegrees);
                transform.position = pos;
                gameObject.SetActive(true);
                Apply(0f);
            }

            private void Update()
            {
                try
                {
                    float dt = Time.deltaTime;
                    _age += dt;
                    // Drag: exponential velocity decay, so the runes rise, slow and hover before they dissolve.
                    _vel *= Mathf.Exp(-Drag * dt);
                    _vel.y += Gravity * dt;
                    transform.position += _vel * dt;
                    float t = _age / BarrierPattern.RuneLifetime;
                    if (t >= 1f) { Return(this); return; }
                    Apply(t);
                }
                catch (Exception ex)
                {
                    BarrierFlare.Failed("rune", ex);
                    Return(this);
                }
            }

            private void Apply(float life01)
            {
                // Face the viewer, so a rune always reads as a rune whichever side of the edge it is seen from.
                Camera cam = Camera.main;
                transform.rotation = cam != null ? cam.transform.rotation * _roll : _roll;
                BarrierPattern.RuneTint(BarrierPattern.RuneFade(life01) * BarrierPattern.RuneShimmer(_age, life01), BarrierMaterials.Mode,
                                        out float r, out float g, out float b, out float a);
                _sr.color = new Color(r, g, b, a);
                float s = _size * BarrierPattern.RuneScale(life01);
                transform.localScale = new Vector3(s, s, s);
            }
        }
    }
}
