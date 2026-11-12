using System;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace TerroritySmellatory
{
    /// <summary>
    /// 0.3.1: the barrier's material, built the way Njord builds its rune material (VFX/NjordRuneVFX_Helper.cs,
    /// GetOrCreateRuneMaterial - the path its live rune trail has run on in real Valheim): no shader ships, a stock one is
    /// found at runtime, and the blend is set from the material's own properties.
    /// </summary>
    /// <remarks>
    /// Taken from Njord, kept as it earned them: "Particles/Standard Unlit" first, then its named fallbacks, then a
    /// score over the materials the game has loaded; _MainTex/_BaseMap/mainTexture all set; _Color, _TintColor and
    /// _EmissionColor set where the shader has them; additive blend as _SrcBlend = One, _DstBlend = One, _ZWrite = 0, the
    /// three keyword toggles, render queue 3000. Added here: the legacy additive shaders (their blend is fixed inside the
    /// shader, so nothing is overridden and the brightness rides in alpha), a mode report so the vertex colours match
    /// the blend, and one log line naming the shader chosen.
    /// </remarks>
    internal static class BarrierMaterials
    {
        /// <summary>Which shader family the material landed on (BarrierPattern.ModeAdditiveOne and friends); decides what a vertex or sprite colour must carry.</summary>
        internal static int Mode = BarrierPattern.ModeAdditiveOne;

        private static Shader _shader;
        private static bool _logged;

        /// <summary>
        /// Njord's own list first (Particles/Standard Unlit, then the alpha-blended fallbacks it names, then
        /// Particles/Additive), and after it two more legacy additive names this mod's earlier build tried.
        /// Shader.Find only sees what the game has loaded: in the world that is all of them, in the main menu almost none.
        /// </summary>
        private static readonly string[] Preferred =
        {
            "Particles/Standard Unlit",
            "Legacy Shaders/Particles/Alpha Blended",
            "Particles/Alpha Blended",
            "Particles/Additive",
            "Legacy Shaders/Particles/Additive",
            "Mobile/Particles/Additive",
        };

        /// <summary>Sprites/Default is in Unity's always-included set, the one shader certain to exist; alpha-blended, it makes a translucent pane instead of a glow, so it is only taken when nothing better scored.</summary>
        private const string LastResort = "Sprites/Default";

        private static bool Alive(Object o) => !ReferenceEquals(o, null) && o != null;

        /// <summary>The shader the barrier draws with, found once and kept; null when the game has none usable.</summary>
        internal static Shader Chosen()
        {
            if (Alive(_shader)) return _shader;
            _shader = Resolve();
            return _shader;
        }

        /// <summary>
        /// A material from the chosen stock shader carrying <paramref name="texture"/> (a sprite material passes null: a
        /// SpriteRenderer feeds its own sprite in as _MainTex), set up for additive glow. Throws when no shader was found,
        /// so the caller's one try/catch reports it like any other build failure.
        /// </summary>
        internal static Material Create(string name, Texture2D texture, bool doubleSided)
        {
            Shader shader = Chosen();
            if (!Alive(shader)) throw new InvalidOperationException("no usable stock particle shader was found (Particles/Standard Unlit and every fallback are missing, and no loaded material scored)");

            var m = new Material(shader) { name = name, hideFlags = HideFlags.HideAndDontSave };

            // Njord: the texture goes in under every name a shader might use.
            if (texture != null)
            {
                if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", texture);
                if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", texture);
                try { m.mainTexture = texture; } catch { }
            }

            // The legacy additive shader multiplies by 2 * _TintColor, so 0.5 is "as painted"; the others read _Color.
            // The real colour is in the vertices (the wall) or the sprite tint (the runes): the material adds nothing.
            if (m.HasProperty("_TintColor")) m.SetColor("_TintColor", new Color(0.5f, 0.5f, 0.5f, 0.5f));
            if (m.HasProperty("_Color")) m.SetColor("_Color", Color.white);
            if (m.HasProperty("_EmissionColor")) m.SetColor("_EmissionColor", Color.white);
            // Soft-particle fade would dim the wall wherever a creature stands against it; make it near-instant.
            if (m.HasProperty("_InvFade")) m.SetFloat("_InvFade", 3f);

            string lower = shader.name.ToLowerInvariant();
            bool hasBlendProps = m.HasProperty("_SrcBlend") && m.HasProperty("_DstBlend");
            string how;
            if (lower.Contains("additive"))
            {
                Mode = BarrierPattern.ModeAdditiveAlpha;   // Blend SrcAlpha One, fixed in the shader itself
                how = "additive, brightness in alpha";
            }
            else if (hasBlendProps)
            {
                // Njord's recipe, verbatim: One/One is pure add - "the classic glowing ink-on-water look" - and it needs no
                // black square around a glyph on a dark background.
                m.SetInt("_SrcBlend", (int)BlendMode.One);
                m.SetInt("_DstBlend", (int)BlendMode.One);
                m.DisableKeyword("_ALPHATEST_ON");
                m.EnableKeyword("_ALPHABLEND_ON");
                m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                Mode = BarrierPattern.ModeAdditiveOne;
                how = "additive One/One, brightness in colour";
            }
            else
            {
                Mode = lower.Contains("add") ? BarrierPattern.ModeAdditiveAlpha : BarrierPattern.ModeBlend;
                how = Mode == BarrierPattern.ModeBlend ? "alpha-blended fallback, a translucent pane" : "additive, brightness in alpha";
            }
            if (m.HasProperty("_ZWrite")) m.SetInt("_ZWrite", 0);
            // The wall's mesh carries both faces, so it culls the back of each (one face drawn from either side, not two
            // added together); a sprite is a billboard and shows whichever side it is given.
            if (m.HasProperty("_Cull")) m.SetInt("_Cull", doubleSided ? (int)CullMode.Back : (int)CullMode.Off);
            m.renderQueue = 3000;

            if (!_logged)
            {
                _logged = true;
                Plugin.Log.LogInfo($"Barrier flare: drawing with shader {shader.name} ({how}).");
            }
            return m;
        }

        private static Shader Resolve()
        {
            // 1. By name. Shader.Find sees Unity's always-included shaders and anything already loaded.
            for (int i = 0; i < Preferred.Length; i++)
            {
                Shader s = Shader.Find(Preferred[i]);   // explicit Alive checks, not ??: a destroyed Unity object is not CLR-null
                if (Alive(s)) return s;
            }

            // 2. By name among the shaders in memory. Some load with a bundle and Shader.Find misses them; the loaded
            //    object list still has them.
            Shader[] loaded = Resources.FindObjectsOfTypeAll<Shader>();
            for (int i = 0; i < Preferred.Length; i++)
                for (int j = 0; j < loaded.Length; j++)
                    if (Alive(loaded[j]) && loaded[j].name == Preferred[i]) return loaded[j];

            // 3. Njord's last resort: score the materials the game has loaded and take the best glowing, unlit,
            //    particle-ish shader that has a texture slot and a colour. Lit, bumped, distorting and Lux shaders are
            //    marked down because they would draw the wall as a lit surface or a heat haze.
            Shader best = null;
            float bestScore = 0f;
            Material[] mats = Resources.FindObjectsOfTypeAll<Material>();
            for (int i = 0; i < mats.Length; i++)
            {
                Material mat = mats[i];
                if (!Alive(mat) || !Alive(mat.shader)) continue;
                if (!mat.HasProperty("_MainTex") || (!mat.HasProperty("_TintColor") && !mat.HasProperty("_Color"))) continue;
                string n = mat.shader.name.ToLowerInvariant();
                float score = 0f;
                if (n.Contains("additive")) score += 6f;
                else if (n.Contains("add")) score += 3f;
                if (n.Contains("particle")) score += 3f;
                if (n.Contains("unlit")) score += 2f;
                if (n.Contains("transparent")) score += 1.5f;
                if (n.Contains("lux") || n.Contains("lit ") || n.Contains("bump") || n.Contains("distort") || n.Contains("heat") ||
                    n.Contains("surface") || n.Contains("refract") || n.Contains("hidden/")) score -= 8f;
                if (score > bestScore) { bestScore = score; best = mat.shader; }
            }
            if (Alive(best))
            {
                Plugin.Log.LogWarning($"Barrier flare: none of the usual stock shaders was found; fell back to '{best.name}' by scoring. The flare may look different from intended.");
                return best;
            }

            // 4. Nothing glowing at all: the always-included sprite shader, so the barrier still shows as a pane.
            Shader last = Shader.Find(LastResort);
            if (Alive(last))
            {
                Plugin.Log.LogWarning($"Barrier flare: no glowing stock shader was found; drawing with '{LastResort}', so the flare will be a translucent pane and not a glow.");
                return last;
            }
            return null;
        }
    }
}
