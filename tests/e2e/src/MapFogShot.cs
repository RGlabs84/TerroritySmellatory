using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TMPro;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;
using TerroritySmellatory;

namespace TerrE2E
{
    /// <summary>
    /// Proof, in real pixels, that the map's explored-ground clip clips: the mod's own ExploredGate (the object MapOverlay puts every
    /// territory graphic under) is built on a canvas of the driver's own, under a RectMask2D as on the map, with a plain white image
    /// inside it, and a camera renders the canvas into a texture. Where the mask is covered the pixel is white; where it is fog, black.
    /// Only a --graphics client can run it. The off-game harness cannot see a stencil, and "the Mask component exists" proves nothing
    /// about what the GPU draws.
    /// </summary>
    /// <remarks>
    /// The mask is 32 x 32 cells, point filtered Alpha8 (as ExploredMask makes it), covered in the bottom-left quarter and everywhere in
    /// a vertical stripe, so a flipped axis, a bilinear blur, a swapped uvRect or a mask that did nothing each give a different picture.
    /// The canvas is 128 x 128 with the map rect inset 8 px (a mask cell is 3.5 px), and only the middle of each cell is sampled. Four things are checked on it:
    /// the clip with the gate on, the same canvas with the gate off (the control: everything white), a zoomed uvRect (the map's
    /// window onto the world), and the RectMask2D's own clip (a graphic larger than the map that must stop at its edge).
    /// </remarks>
    internal static class MapFogShot
    {
        private static readonly Type GateType = typeof(Territory).Assembly.GetType("TerroritySmellatory.ExploredGate");
        private static readonly MethodInfo BuildMethod = GateType?.GetMethod("Build", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        private static readonly MethodInfo SetMethod = GateType?.GetMethod("Set", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        private static readonly PropertyInfo RectProp = GateType?.GetProperty("Rect", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        private const int Cells = 32, Side = 128, Inset = 8, Map = Side - 2 * Inset;   // the map is 112 px wide: 3.5 px a mask cell

        /// <summary>The pixel at the middle of mask cell <paramref name="i"/> (of <paramref name="cells"/> across the map).</summary>
        private static int Mid(int i, int cells) => (int)(Inset + (i + 0.5f) * Map / cells);

        /// <summary>The DLL has the gate (0.4.1) and this process has a graphics device.</summary>
        internal static bool Available => BuildMethod != null && SetMethod != null && RectProp != null && SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;

        /// <summary>Covered cells: the bottom-left 16 x 8 block, plus the column x = 28 from top to bottom.</summary>
        private static bool Covered(int x, int y) => (x < 16 && y < 8) || x == 28;

        private static Texture2D MakeMask()
        {
            var tex = new Texture2D(Cells, Cells, TextureFormat.Alpha8, false, true) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, name = "TerrE2E_FogMask" };
            var bytes = new byte[Cells * Cells];
            for (int y = 0; y < Cells; y++)
                for (int x = 0; x < Cells; x++)
                    bytes[y * Cells + x] = Covered(x, y) ? (byte)255 : (byte)0;
            tex.LoadRawTextureData(bytes);
            tex.Apply(false);
            return tex;
        }

        internal static IEnumerator Run(string id)
        {
            string dir = Path.Combine(E2EEnv.RunDir ?? ".", "shots");
            Directory.CreateDirectory(dir);

            var camGo = new GameObject("TerrE2E_FogCamera");
            camGo.transform.position = new Vector3(9000f, 9000f, 9000f);
            var cam = camGo.AddComponent<Camera>();
            cam.enabled = false;
            cam.orthographic = true;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.cullingMask = ~0;
            cam.allowHDR = false;
            cam.allowMSAA = false;

            var canvasGo = new GameObject("TerrE2E_FogCanvas", typeof(RectTransform), typeof(Canvas));
            canvasGo.layer = 5;   // UI
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = cam;
            canvas.planeDistance = 1f;

            // The map's arrangement: the map picture's rect (here inset 8 pixels each side of the canvas, so there is canvas outside it for a
            // graphic to spill onto), a RectMask2D container filling it, the gate filling that, and the graphic inside the gate - which, like a
            // big territory, is larger than the map. The mask maps onto the gate's rect, which is the picture's, as on the real map.
            var mapGo = new GameObject("map", typeof(RectTransform));
            mapGo.layer = 5;
            var mapRect = (RectTransform)mapGo.transform;
            mapRect.SetParent(canvasGo.transform, false);
            mapRect.anchorMin = Vector2.zero;
            mapRect.anchorMax = Vector2.one;
            mapRect.offsetMin = new Vector2(Inset, Inset);
            mapRect.offsetMax = new Vector2(-Inset, -Inset);
            var clipGo = new GameObject("clip", typeof(RectTransform), typeof(RectMask2D));
            clipGo.layer = 5;
            var clip = (RectTransform)clipGo.transform;
            clip.SetParent(mapRect, false);
            clip.anchorMin = Vector2.zero;
            clip.anchorMax = Vector2.one;
            clip.offsetMin = Vector2.zero;
            clip.offsetMax = Vector2.zero;

            object gate = BuildMethod.Invoke(null, new object[] { clip });
            var gateRect = (RectTransform)RectProp.GetValue(gate);
            var whiteGo = new GameObject("white", typeof(RectTransform), typeof(RawImage));
            whiteGo.layer = 5;
            var white = (RectTransform)whiteGo.transform;
            white.SetParent(gateRect, false);
            // Fills the CANVAS, not the map: the RectMask2D is what cuts it.
            white.anchorMin = Vector2.zero;
            white.anchorMax = Vector2.one;
            white.offsetMin = new Vector2(-Inset, -Inset);
            white.offsetMax = new Vector2(Inset, Inset);
            whiteGo.GetComponent<RawImage>().color = Color.white;

            Texture2D mask = MakeMask();
            RenderTexture rt = new RenderTexture(Side, Side, 24, RenderTextureFormat.ARGB32);
            var failures = new System.Collections.Generic.List<string>();
            try
            {
                yield return Flow.Frames(3);
                Canvas.ForceUpdateCanvases();
                cam.orthographicSize = Side / 2f;   // one unit a pixel

                // 1. The gate on, the whole mask over the whole picture.
                SetMethod.Invoke(gate, new object[] { mask, new Rect(0f, 0f, 1f, 1f) });
                Color32[] on = Render(cam, rt, Path.Combine(dir, "fog-gate-on.png"));
                int wrongOn = 0, checkedOn = 0;
                for (int y = 0; y < Cells; y++)
                    for (int x = 0; x < Cells; x++)
                    {
                        int cx = Mid(x, Cells), cy = Mid(y, Cells);
                        checkedOn++;
                        if (Bright(on[cy * Side + cx]) != Covered(x, y)) wrongOn++;
                    }
                if (wrongOn > 0) failures.Add($"gate on: {wrongOn} of {checkedOn} cells drawn wrongly (the mask or the clip did not apply, or an axis is flipped)");

                // 2. Control: the same canvas with the gate off draws the whole picture inside the clip.
                SetMethod.Invoke(gate, new object[] { null, new Rect(0f, 0f, 1f, 1f) });
                Color32[] off = Render(cam, rt, Path.Combine(dir, "fog-gate-off.png"));
                int notWhiteOff = 0, whiteOutsideClip = 0;
                for (int y = 0; y < Side; y++)
                    for (int x = 0; x < Side; x++)
                    {
                        bool insideClip = x >= Inset && x < Side - Inset && y >= Inset && y < Side - Inset;
                        bool w = Bright(off[y * Side + x]);
                        if (insideClip && !w) notWhiteOff++;
                        if (!insideClip && w) whiteOutsideClip++;
                    }
                if (notWhiteOff > 0) failures.Add($"control, gate off: {notWhiteOff} pixels inside the clip are not white (an off gate still masks)");
                if (whiteOutsideClip > 0) failures.Add($"control, gate off: {whiteOutsideClip} pixels outside the RectMask2D's clip are white (the clip did nothing)");

                // 3. A zoomed window: the left half of the mask over the whole picture (the map's uvRect is a window onto the texture).
                SetMethod.Invoke(gate, new object[] { mask, new Rect(0f, 0f, 0.5f, 0.5f) });
                Color32[] zoom = Render(cam, rt, Path.Combine(dir, "fog-gate-zoom.png"));
                int wrongZoom = 0;
                for (int y = 0; y < Cells; y++)
                    for (int x = 0; x < Cells; x++)
                    {
                        int cx = Mid(x, Cells), cy = Mid(y, Cells);
                        // the window shows mask cells 0..15 in both axes, two picture cells to each
                        if (Bright(zoom[cy * Side + cx]) != Covered(x / 2, y / 2)) wrongZoom++;
                    }
                if (wrongZoom > 0) failures.Add($"zoomed uvRect: {wrongZoom} of {Cells * Cells} cells drawn wrongly (the mask does not follow the picture's window)");
            }
            finally
            {
                UnityEngine.Object.Destroy(rt);
                UnityEngine.Object.Destroy(mask);
                UnityEngine.Object.Destroy(canvasGo);
                UnityEngine.Object.Destroy(camGo);
            }

            Report.Check(id,
                $"the explored-ground gate on a real canvas, as the map has it ({Side} x {Side} px, a {Cells} x {Cells} mask on a {Map} px map rect under a RectMask2D): gate on draws white exactly on covered cells, the control with it off draws everything inside the clip, a zoomed uvRect shows the window's cells; pictures in {dir}",
                failures.Count == 0, string.Join("; ", failures));
        }

        // ── in a world: the mask against the game's own explored cells ─────────────────────────────────────────────────────

        private static readonly MethodInfo CurrentMethod = typeof(Territory).Assembly.GetType("TerroritySmellatory.ExploredMask")
            ?.GetMethod("Current", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        private static readonly FieldInfo ExploredField = AccessTools.Field(typeof(Minimap), "m_explored");
        private static readonly FieldInfo ExploredOthersField = AccessTools.Field(typeof(Minimap), "m_exploredOthers");

        /// <summary>The DLL keeps the mask, the game has its explored sets where this looks, and the minimap updates (a graphics device).</summary>
        internal static bool WorldAvailable => CurrentMethod != null && ExploredField != null && ExploredOthersField != null && SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;

        /// <summary>How many cells the game has uncovered (own or shared), counted from the packed words: cheap enough to poll.</summary>
        private static int CountUncovered(Minimap map)
        {
            var a = (System.Collections.BitArray)ExploredField.GetValue(map);
            var b = (System.Collections.BitArray)ExploredOthersField.GetValue(map);
            var wa = new int[(a.Length + 31) / 32];
            var wb = new int[wa.Length];
            a.CopyTo(wa, 0);
            b.CopyTo(wb, 0);
            int count = 0;
            for (int i = 0; i < wa.Length; i++)
            {
                uint v = (uint)(wa[i] | wb[i]);
                while (v != 0) { v &= v - 1; count++; }
            }
            return count;
        }

        /// <summary>How many cells the mask has wrong against the game's two explored sets, and how many it covers.</summary>
        private static int Mismatches(Texture2D mask, Minimap map, out int covered)
        {
            var a = (System.Collections.BitArray)ExploredField.GetValue(map);
            var b = (System.Collections.BitArray)ExploredOthersField.GetValue(map);
            NativeArray<byte> data = mask.GetRawTextureData<byte>();
            int wrong = 0;
            covered = 0;
            for (int i = 0; i < data.Length; i++)
            {
                bool explored = a[i] || b[i];
                if (explored) covered++;
                if ((data[i] != 0) != explored) wrong++;
            }
            return wrong;
        }

        /// <summary>
        /// The mask's copy of the explored cells must be the game's own, cell for cell: first as the mod first builds it, then after the player
        /// has been put down on new ground and vanilla has uncovered it - which reaches the mask only through the mod's hooks on the methods that
        /// uncover a cell (the incremental path), so a hook that never fired shows as cells the mask lacks. And the minimap's gate must be on,
        /// holding that texture, with the map picture's own uvRect.
        /// </summary>
        internal static IEnumerator WorldCheck(string id)
        {
            Minimap map = Minimap.instance;
            if (map == null || World.Me == null)
            {
                Report.Fail(id, "the map's explored mask follows the game's explored cells", $"no minimap ({map != null}) or no player ({World.Me != null})");
                yield break;
            }
            var why = new List<string>();

            // The overlay builds the mask the first time a map is drawn: wait for the gate to have one.
            Texture2D mask = null;
            float give = Time.realtimeSinceStartup + 20f;
            while (Time.realtimeSinceStartup < give)
            {
                mask = (Texture2D)CurrentMethod.Invoke(null, new object[] { map });
                if (mask != null) break;
                yield return Flow.Seconds(0.5f);
            }
            if (mask == null)
            {
                Report.Fail(id, "the map's explored mask follows the game's explored cells", "no mask after 20 s (ExploredMask.Current gave null: the map overlay never asked, or making the texture failed - see the log)");
                yield break;
            }
            int size = mask.width;
            int wrong0 = Mismatches(mask, map, out int covered0);

            // New ground: well outside what has been uncovered so far (the game explores a 100 m circle round the player every few seconds).
            Vector3 from = World.Me.transform.position;
            int covered1 = covered0, wrong1 = wrong0;
            for (int hop = 1; hop <= 3; hop++)
            {
                yield return World.Go(new Vector3(from.x + 260f * hop, 0f, from.z + 40f * hop), 0.5f);
                float until = Time.realtimeSinceStartup + 15f;
                while (Time.realtimeSinceStartup < until)
                {
                    yield return Flow.Seconds(1f);
                    if (CountUncovered(map) > covered1) break;   // the game has explored: compare
                }
                mask = (Texture2D)CurrentMethod.Invoke(null, new object[] { map });
                wrong1 = Mismatches(mask, map, out covered1);
            }
            if (covered1 <= covered0) why.Add($"the game uncovered no new cells after three 260 m hops ({covered0} -> {covered1}): the minimap is not exploring, so the incremental path was not exercised");
            if (wrong0 > 0) why.Add($"{wrong0} of {size * size} cells wrong as first built");
            if (wrong1 > 0) why.Add($"{wrong1} of {size * size} cells wrong after the hops ({covered0} -> {covered1} cells uncovered): the mask missed what the game uncovered");

            // The minimap's gate, on and holding the mask, lined up with the picture.
            RawImage gate = map.m_mapImageSmall != null
                ? map.m_mapImageSmall.GetComponentsInChildren<RawImage>(true).FirstOrDefault(r => r.name == "TS_ExploredGate")
                : null;
            if (gate == null) why.Add("no TS_ExploredGate under the small map");
            else
            {
                if (!gate.enabled) why.Add("the small map's gate is off");
                if (gate.texture != mask) why.Add($"the gate holds '{(gate.texture != null ? gate.texture.name : "nothing")}', not the mask");
                if (gate.uvRect != map.m_mapImageSmall.uvRect) why.Add($"the gate's uvRect {gate.uvRect} is not the picture's {map.m_mapImageSmall.uvRect}");
                if (gate.GetComponent<UnityEngine.UI.Mask>() == null || !gate.GetComponent<UnityEngine.UI.Mask>().enabled) why.Add("the gate's Mask is off");
            }

            Report.Check(id,
                $"the map's explored mask ({size} x {size}) matches the game's explored cells exactly, as built ({covered0} cells) and after the player was put down on new ground three times ({covered1} cells, uncovered through the hooks), and the small map's gate is on with the mask and the picture's uvRect",
                why.Count == 0, string.Join("; ", why));
        }

        // ── the minimap's name label ─────────────────────────────────────────────────────────────────────────────────────

        private static readonly MethodInfo TextForMethod = typeof(Territory).Assembly.GetType("TerroritySmellatory.MapBiomeLabel")
            ?.GetMethod("TextFor", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

        /// <summary>The DLL builds the label's text itself (0.4.1's NameFit) and the game has its small biome label (any client).</summary>
        internal static bool LabelAvailable => TextForMethod != null && Minimap.instance != null && Minimap.instance.m_biomeNameSmall != null;

        /// <summary>
        /// The minimap's real label (vanilla's TMP text, its font and its rect), given the text the mod makes for names of every length, lays each
        /// out on exactly ONE line no wider than its rect. The control: a name as long as the longest, written the way 0.4.0 wrote one that
        /// did not fit (a plain 80% tag), wraps in the same label - so the check could fail.
        /// </summary>
        internal static IEnumerator LabelCheck(string id)
        {
            TMP_Text label = Minimap.instance.m_biomeNameSmall;
            float room = label.rectTransform.rect.width;
            string saved = label.text;
            var why = new List<string>();
            var seen = new List<string>();
            string[] names =
            {
                "Meadows",
                "A Carrot Field in Britain 2",
                "Felucca Buccaneer's Den",
                "The Very Long Name Of A Territory Nobody Could Fit In Any Label At All Honestly Not Even Close"
            };
            try
            {
                for (int i = 0; i < names.Length; i++)
                {
                    var t = new Territory { Id = 9100 + i, Name = names[i], Revision = 1 };
                    string text = (string)TextForMethod.Invoke(null, new object[] { t, label });
                    label.text = text;
                    label.ForceMeshUpdate();
                    int lines = label.textInfo.lineCount;
                    float width = label.preferredWidth;
                    seen.Add($"'{names[i]}' -> {lines} line(s), {width:0.#} of {room:0.#}");
                    if (lines != 1) why.Add($"'{names[i]}' is on {lines} lines");
                    if (width > room + 0.5f) why.Add($"'{names[i]}' is {width:0.#} wide in a label {room:0.#} wide");
                }

                label.text = "<size=80%>" + names[names.Length - 1] + "</size>";
                label.ForceMeshUpdate();
                int plainLines = label.textInfo.lineCount;
                seen.Add($"control (the 0.4.0 way) -> {plainLines} lines");
                if (plainLines < 2) why.Add($"the control did not wrap ({plainLines} line(s)): the label is too wide for this check to prove anything");
            }
            finally
            {
                label.text = saved;
                label.ForceMeshUpdate();
            }
            yield return null;
            Report.Check(id, $"the minimap's real label shows a territory name of any length on one line within its width: {string.Join("; ", seen)}", why.Count == 0, string.Join("; ", why));
        }

        private static bool Bright(Color32 c) => c.r > 128 && c.g > 128 && c.b > 128;

        private static Color32[] Render(Camera cam, RenderTexture rt, string path)
        {
            RenderTexture prev = RenderTexture.active;
            var tex = new Texture2D(Side, Side, TextureFormat.RGBA32, false);
            try
            {
                cam.targetTexture = rt;
                Canvas.ForceUpdateCanvases();
                cam.Render();
                cam.targetTexture = null;
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, Side, Side), 0, 0, false);
                tex.Apply(false);
                Color32[] px = tex.GetPixels32();
                File.WriteAllBytes(path, ImageConversion.EncodeToPNG(tex));
                return px;
            }
            finally
            {
                cam.targetTexture = null;
                RenderTexture.active = prev;
                UnityEngine.Object.Destroy(tex);
            }
        }
    }
}
