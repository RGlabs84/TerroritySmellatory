using System;
using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TerroritySmellatory
{
    /// <summary>Territories drawn on the large map AND the minimap as their real outline, with their names.</summary>
    /// <remarks>
    /// NOT map pins. A pin would be in Minimap.m_pins, where the player's right-click removes it, the pin
    /// filters hide it and anything iterating pins trips over it. These are this mod's own UI elements: the
    /// outline is a TerritoryOutline mesh and the name is vanilla's pin-name prefab, so it reads like vanilla's.
    ///
    /// Each map image (m_mapImageLarge, m_mapImageSmall) gets one container of ours as its FIRST child, above
    /// the map picture and beneath the pin root, the player marker and every pin. The container carries its
    /// own RectMask2D, so an outline bigger than the view is clipped to the map's square. Vanilla's map images
    /// carry one too (libs-Tools/UI-DUMPS), but ours makes the clipping ours to rely on.
    ///
    /// Positioned every map frame with vanilla's own conversions (WorldToMapPoint, MapPointToLocalGuiPos,
    /// MapSizeToLocalGuiSize), the same calls Minimap.UpdatePins makes, so an outline cannot drift from the
    /// terrain under it at any zoom. Positions count from the map image's bottom-left corner, which is where
    /// vanilla's pin prefab is anchored, so ours are anchored there too.
    ///
    /// Better Continents worlds can be larger than vanilla's; nothing here assumes a world size - the
    /// conversions read m_pixelSize and m_textureSize from the live Minimap.
    ///
    /// A COMBINED TERRITORY (0.2.0, more than one part) is ONE TerritoryUnionOutline instead: one rim along the
    /// outer edge of the whole ground and one fill (UnionOutlineGeometry), so it reads as one territory. Its
    /// parts go through the very conversions above, one by one, into a frame centred on part 0 - where the name
    /// sits, always inside the ground - and are drawn in an unrotated rect around all of them: RectMask2D culls
    /// a graphic by its rect, so the rect must hold the whole mesh. The mesh is built at one zoom and rides the
    /// rect's scale while the map zooms, and is built again only once the zoom has drifted more than a tenth
    /// (the rim is then at most a tenth off its width) or the territory changes: a combined territory's mesh
    /// costs up to a few milliseconds to cut, too much for every frame of a zoom. Territories of one part keep
    /// the path above exactly.
    ///
    /// On the MINIMAP, the territory whose name MapBiomeLabel has put in the biome label loses its name here:
    /// the name once, in the label.
    ///
    /// UNEXPLORED GROUND (0.4.1): a territory is drawn only over ground the player's map has uncovered. Each layer's graphics sit under one
    /// "gate" - a RawImage carrying ExploredMask (a copy of vanilla's explored cells, given the map picture's own uvRect every frame) and
    /// a UI Mask - so fill, rim and combined outline are clipped to uncovered cells the way the fog covers the picture. A name sits on
    /// one point, so it is shown only while that point is uncovered (Minimap.IsExplored). Off with the HideUnexploredTerritories setting.
    ///
    /// A territory with MapNameOnly (0.3.2) has no outline here at all, only its name - and, on the minimap, its entry in the
    /// biome label. That is how a continent is shown: an outline and a tint over a whole continent would wash the map in one
    /// colour. Its Area and Union graphics are kept out of sight (taken down, or never built) rather than ever drawn.
    /// </remarks>
    internal static class MapOverlay
    {
        private sealed class Entry
        {
            public int Id;
            public int Revision = int.MinValue;

            /// <summary>Its colour and Name only as last drawn (LookOf): a sub-territory takes both from its parent, whose edit leaves its own Revision as it was.</summary>
            public long Look = -1;

            /// <summary>Part 0: the whole ground of a one-part territory, and where the name sits on any.</summary>
            public TerritoryShape Shape;

            /// <summary>Every part, 0 first, as last drawn: what "has the ground changed?" compares.</summary>
            public TerritoryShape[] Parts = new TerritoryShape[0];

            public RectTransform Area;
            public TerritoryOutline Outline;
            public RectTransform Label;
            public TMP_Text Text;

            // A combined territory's one outline, and the map scale (UI units per metre) its mesh was built at.
            public RectTransform UnionArea;
            public TerritoryUnionOutline Union;
            public Vector2 BuiltUnit;
            public bool UnionStale;

            /// <summary>TerritoryFlags.MapNameOnly: only the name is drawn - no outline, no tint (a continent or a region).</summary>
            public bool NameOnly;

            public bool Combined => Parts.Length > 1;
        }

        /// <summary>Everything drawn on one of the two maps.</summary>
        private sealed class Layer
        {
            public readonly bool Large;
            public readonly float Rim;
            public readonly Dictionary<int, Entry> Entries = new Dictionary<int, Entry>();
            public RectTransform Clip;

            /// <summary>Inside Clip, parent of every graphic: the explored-ground clip (ClipFor).</summary>
            public ExploredGate Gate;
            public int BuiltRevision = -1;
            public bool Visible;

            public Layer(bool large, float rim)
            {
                Large = large;
                Rim = rim;
            }

            public RawImage Image(Minimap map) => Large ? map.m_mapImageLarge : map.m_mapImageSmall;
            public RectTransform NameRoot(Minimap map) => Large ? map.m_pinNameRootLarge : map.m_pinNameRootSmall;
        }

        // Rim widths in the maps' own UI units (1.33 px each at 1440p): a little finer on the 188-unit minimap.
        private static readonly Layer LargeMap = new Layer(true, 2.5f);
        private static readonly Layer SmallMap = new Layer(false, 2f);
        private static Minimap _owner;

        internal static void Update(Minimap map)
        {
            if (map != _owner)
            {
                DestroyAll();
                _owner = map;
            }

            bool any = TSConfig.MapAreas.Value && TerritoryIndex.Any;
            Show(map, LargeMap, any && map.m_mode == Minimap.MapMode.Large);
            Show(map, SmallMap, any && map.m_mode == Minimap.MapMode.Small);
        }

        private static void Show(Minimap map, Layer layer, bool on)
        {
            if (!on)
            {
                SetVisible(layer, false);
                return;
            }

            if (layer.BuiltRevision != TerritoryIndex.LocalRevision) Rebuild(map, layer);
            SetVisible(layer, true);
            Position(map, layer);
        }

        private static void Rebuild(Minimap map, Layer layer)
        {
            layer.BuiltRevision = TerritoryIndex.LocalRevision;
            var live = new HashSet<int>();

            foreach (Territory t in TerritoryIndex.All)
            {
                if (!t.Has(TerritoryFlags.ShowOnMap)) continue;
                live.Add(t.Id);

                if (!layer.Entries.TryGetValue(t.Id, out Entry e) || e.Area == null)
                {
                    if (e != null) Destroy(e);
                    e = Create(map, layer);
                    if (e == null) continue;
                    e.Id = t.Id;
                    layer.Entries[t.Id] = e;
                }
                long look = LookOf(t);
                if (e.Revision == t.Revision && e.Look == look && SameGround(e, t)) continue;

                e.Revision = t.Revision;
                e.Look = look;
                e.Shape = t.Shape;
                if (e.Parts.Length != t.PartCount) e.Parts = new TerritoryShape[t.PartCount];
                for (int i = 0; i < e.Parts.Length; i++) e.Parts[i] = t.GetPart(i);
                Color c = t.Color;
                var tint = new Color(c.r, c.g, c.b, 0.9f);
                e.NameOnly = t.Has(TerritoryFlags.MapNameOnly);
                if (e.NameOnly)
                {
                    // Its name and nothing else: whatever outline it had is taken down, and put back if the flag goes.
                    DestroyUnion(e);
                    if (e.Area.gameObject.activeSelf) e.Area.gameObject.SetActive(false);
                }
                else if (e.Combined)
                {
                    if (e.Union == null && !CreateUnion(layer, e))
                    {
                        e.Revision = int.MinValue;   // try again at the next rebuild
                        continue;
                    }
                    e.Union.color = tint;
                    e.UnionStale = true;
                    if (e.Area.gameObject.activeSelf) e.Area.gameObject.SetActive(false);
                }
                else
                {
                    DestroyUnion(e);
                    if (!e.Area.gameObject.activeSelf) e.Area.gameObject.SetActive(true);
                    e.Outline.Configure(t.Kind == TerritoryShapeKind.Circle, layer.Rim);
                    e.Outline.color = tint;
                }
                if (e.Text != null)
                {
                    e.Text.text = t.DisplayName;
                    e.Text.color = new Color(Mathf.Lerp(c.r, 1f, 0.5f), Mathf.Lerp(c.g, 1f, 0.5f), Mathf.Lerp(c.b, 1f, 0.5f), 1f);
                }
            }

            var dead = new List<int>();
            foreach (KeyValuePair<int, Entry> kv in layer.Entries)
                if (!live.Contains(kv.Key)) dead.Add(kv.Key);
            foreach (int id in dead)
            {
                Destroy(layer.Entries[id]);
                layer.Entries.Remove(id);
            }
        }

        /// <summary>What a followed setting can change about an outline without touching the territory's own revision: its tint, and whether only its name is drawn.</summary>
        private static long LookOf(Territory t)
        {
            Color32 c = t.Color;
            return ((long)c.r << 24) | ((long)c.g << 16) | ((long)c.b << 8) | c.a | (t.Has(TerritoryFlags.MapNameOnly) ? 1L << 40 : 0L);
        }

        /// <summary>The same parts, number for number, as last drawn: a revision alone would miss an edit that kept it.</summary>
        private static bool SameGround(Entry e, Territory t)
        {
            if (e.Parts.Length != t.PartCount) return false;
            for (int i = 0; i < e.Parts.Length; i++)
                if (!e.Parts[i].SameAs(t.GetPart(i))) return false;
            return true;
        }

        private static Entry Create(Minimap map, Layer layer)
        {
            RectTransform clip = ClipFor(map, layer);
            if (clip == null) return null;

            var area = new GameObject("TS_TerritoryArea", typeof(RectTransform));
            area.layer = clip.gameObject.layer;
            var rt = (RectTransform)area.transform;
            rt.SetParent(clip, false);
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.zero;
            rt.pivot = new Vector2(0.5f, 0.5f);
            var outline = area.AddComponent<TerritoryOutline>();
            outline.raycastTarget = false;
            var e = new Entry { Area = rt, Outline = outline };

            RectTransform names = layer.NameRoot(map);
            if (map.m_pinNamePrefab != null && names != null)
            {
                GameObject label = UnityEngine.Object.Instantiate(map.m_pinNamePrefab, names);
                label.name = "TS_TerritoryName";
                label.transform.SetAsFirstSibling();   // beneath every real pin's name
                foreach (Graphic g in label.GetComponentsInChildren<Graphic>(true)) g.raycastTarget = false;
                e.Label = (RectTransform)label.transform;
                e.Text = label.GetComponentInChildren<TMP_Text>(true);
            }
            return e;
        }

        /// <summary>A combined territory's outline: one graphic in the clip, anchored where the single outlines are.</summary>
        private static bool CreateUnion(Layer layer, Entry e)
        {
            if (layer.Gate == null) return false;
            var go = new GameObject("TS_TerritoryUnion", typeof(RectTransform));
            go.layer = layer.Gate.Rect.gameObject.layer;
            var rt = (RectTransform)go.transform;
            rt.SetParent(layer.Gate.Rect, false);
            // Beside the territory's single outline, so it draws where that did among the others.
            rt.SetSiblingIndex(e.Area.GetSiblingIndex() + 1);
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.zero;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = Vector2.zero;
            var union = go.AddComponent<TerritoryUnionOutline>();
            union.raycastTarget = false;
            e.UnionArea = rt;
            e.Union = union;
            e.UnionStale = true;
            return true;
        }

        private static void DestroyUnion(Entry e)
        {
            if (e.UnionArea != null) UnityEngine.Object.Destroy(e.UnionArea.gameObject);
            e.UnionArea = null;
            e.Union = null;
        }

        /// <summary>
        /// This layer's container inside the map image - filling it, clipping to it, drawn first - and, inside that, the gate every
        /// territory graphic is a child of. Returns the gate's rect.
        /// </summary>
        private static RectTransform ClipFor(Minimap map, Layer layer)
        {
            if (layer.Gate != null) return layer.Gate.Rect;
            RawImage img = layer.Image(map);
            if (img == null) return null;

            if (layer.Clip == null)
            {
                var go = new GameObject(layer.Large ? "TS_TerritoriesLarge" : "TS_TerritoriesSmall", typeof(RectTransform), typeof(RectMask2D));
                go.layer = img.gameObject.layer;
                var rt = (RectTransform)go.transform;
                rt.SetParent(img.rectTransform, false);
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.offsetMin = Vector2.zero;
                rt.offsetMax = Vector2.zero;
                rt.SetAsFirstSibling();
                go.SetActive(layer.Visible);
                layer.Clip = rt;
            }

            layer.Gate = ExploredGate.Build(layer.Clip);
            return layer.Gate.Rect;
        }

        /// <summary>
        /// Turns the explored-ground clip on or off for this frame, and lines the mask up with the map picture: the picture's own uvRect,
        /// which vanilla sets every frame, so a cell of the mask is the cell of the picture the fog covers. With no mask to be had the
        /// territories are drawn unclipped, as before.
        /// </summary>
        private static void ApplyGate(Minimap map, Layer layer, RawImage img, bool want)
        {
            if (layer.Gate == null) return;
            layer.Gate.Set(want ? ExploredMask.Current(map) : null, img.uvRect);
        }

        private static void Position(Minimap map, Layer layer)
        {
            RawImage img = layer.Image(map);
            if (img == null) return;

            bool onlyExplored = TSConfig.MapHideUnexplored.Value;
            ApplyGate(map, layer, img, onlyExplored);

            float perPixel = map.m_pixelSize * map.m_textureSize;
            // UI units per metre, this frame: what a combined territory's mesh is scaled by between builds.
            Vector2 unit = map.MapSizeToLocalGuiSize(new Vector2(1f / perPixel, 1f / perPixel), img);
            // The minimap's biome label may be showing a territory's name already (MapBiomeLabel).
            Territory labelled = layer.Large ? null : MapBiomeLabel.Showing;

            foreach (Entry e in layer.Entries.Values)
            {
                if (e.Area == null) continue;
                TerritoryShape s = e.Shape;
                var centre = new Vector3(s.CenterX, 0f, s.CenterZ);

                map.WorldToMapPoint(centre, out float mx, out float my);
                Vector2 pos = map.MapPointToLocalGuiPos(mx, my, img);
                if (e.NameOnly)
                {
                    // no outline to place
                }
                else if (e.Combined)
                {
                    PositionUnion(map, layer, img, e, pos, perPixel, unit);
                }
                else
                {
                    Vector2 size = map.MapSizeToLocalGuiSize(new Vector2(s.HalfX * 2f / perPixel, s.HalfZ * 2f / perPixel), img);

                    e.Area.anchoredPosition = pos;
                    // Anchored to a point, so sizeDelta IS the size. Set only when it changes (a zoom), because a
                    // new size rebuilds the outline's mesh and a move does not.
                    if (e.Area.sizeDelta != size) e.Area.sizeDelta = size;
                    // World yaw turns +Z toward +X - clockwise seen from above, and the map is seen from above
                    // with +Z up. A UI rotation about Z is counter-clockwise, hence the minus.
                    e.Area.localRotation = Quaternion.Euler(0f, 0f, -s.Yaw);
                }

                if (e.Label != null)
                {
                    // The name roots sit outside the map's mask, so a name is shown only while the centre it
                    // rides on is on the map - vanilla's own rule for its pins (IsPointVisible).
                    // ...and, with the explored-ground rule, only while that point is uncovered.
                    bool named = map.IsPointVisible(centre, img) && (labelled == null || labelled.Id != e.Id)
                        && (!onlyExplored || map.IsExplored(centre));
                    if (e.Label.gameObject.activeSelf != named) e.Label.gameObject.SetActive(named);
                    if (named) e.Label.anchoredPosition = pos;
                }
            }
        }

        private static readonly List<UnionOutlineGeometry.Part> _parts = new List<UnionOutlineGeometry.Part>();

        /// <summary>
        /// A combined territory's outline, its origin on part 0's centre. The mesh is built again only when the
        /// territory changed or the zoom has drifted more than a tenth since; between builds it rides the rect's
        /// scale, and a pan only moves it.
        /// </summary>
        private static void PositionUnion(Minimap map, Layer layer, RawImage img, Entry e, Vector2 pos, float perPixel, Vector2 unit)
        {
            RectTransform rt = e.UnionArea;
            if (rt == null || e.Union == null) return;
            if (!(unit.x > 0f) || !(unit.y > 0f) || float.IsInfinity(unit.x) || float.IsInfinity(unit.y)) return;   // no size yet

            if (e.UnionStale || Drifted(unit.x / e.BuiltUnit.x) || Drifted(unit.y / e.BuiltUnit.y))
            {
                e.UnionStale = false;
                e.BuiltUnit = unit;
                _parts.Clear();
                foreach (TerritoryShape part in e.Parts)
                {
                    // Each part exactly as the single path places its rect: centre, size, and the yaw as a
                    // counter-clockwise UI rotation of minus the yaw.
                    map.WorldToMapPoint(part.Center, out float px, out float py);
                    Vector2 at = map.MapPointToLocalGuiPos(px, py, img) - pos;
                    Vector2 size = map.MapSizeToLocalGuiSize(new Vector2(part.HalfX * 2f / perPixel, part.HalfZ * 2f / perPixel), img);
                    _parts.Add(new UnionOutlineGeometry.Part(part.Kind == TerritoryShapeKind.Circle, at.x, at.y, size.x * 0.5f, size.y * 0.5f, -part.Yaw));
                }
                e.Union.Build(_parts, layer.Rim);

                // The rect around the whole mesh, its pivot on the mesh's origin (part 0's centre), which is what
                // the rect is placed and scaled about.
                Rect b = e.Union.Bounds;
                var pivot = new Vector2(b.width > 0f ? -b.xMin / b.width : 0.5f, b.height > 0f ? -b.yMin / b.height : 0.5f);
                if (rt.pivot != pivot) rt.pivot = pivot;
                if (rt.sizeDelta != b.size) rt.sizeDelta = b.size;
            }

            rt.anchoredPosition = pos;
            var scale = new Vector3(unit.x / e.BuiltUnit.x, unit.y / e.BuiltUnit.y, 1f);
            if (rt.localScale != scale) rt.localScale = scale;
        }

        /// <summary>More than a tenth from the scale the mesh was built at (or no scale at all).</summary>
        private static bool Drifted(float ratio) => !(ratio > 1f / 1.1f && ratio < 1.1f);

        private static void SetVisible(Layer layer, bool on)
        {
            if (layer.Visible == on) return;
            layer.Visible = on;
            if (layer.Clip != null) layer.Clip.gameObject.SetActive(on);
            if (on) return;
            foreach (Entry e in layer.Entries.Values)
                if (e.Label != null) e.Label.gameObject.SetActive(false);
        }

        private static void Destroy(Entry e)
        {
            if (e.Area != null) UnityEngine.Object.Destroy(e.Area.gameObject);
            if (e.Label != null) UnityEngine.Object.Destroy(e.Label.gameObject);
            DestroyUnion(e);
        }

        private static void DestroyAll()
        {
            foreach (Layer layer in new[] { LargeMap, SmallMap })
            {
                foreach (Entry e in layer.Entries.Values) Destroy(e);
                layer.Entries.Clear();
                if (layer.Clip != null) UnityEngine.Object.Destroy(layer.Clip.gameObject);
                layer.Clip = null;
                layer.Gate = null;
                layer.BuiltRevision = -1;
                layer.Visible = false;
            }
        }
    }

    /// <summary>
    /// A territory's outline on a map: OutlineGeometry's mesh, tinted by the graphic's colour, with a faint
    /// fill. A MaskableGraphic on the default UI material, so the map's RectMask2D clips it.
    /// </summary>
    internal sealed class TerritoryOutline : MaskableGraphic
    {
        /// <summary>The feather's width in UI units, shared by the combined territories' outline.</summary>
        internal const float Feather = 1f;

        /// <summary>How much of the rim's alpha the fill has, shared by the combined territories' outline.</summary>
        internal const float FillAlpha = 0.16f;

        private static readonly List<float> Xs = new List<float>();
        private static readonly List<float> Ys = new List<float>();
        private static readonly List<byte> Tones = new List<byte>();
        private static readonly List<int> Tris = new List<int>();

        private bool _round;
        private float _rim = 2f;

        internal void Configure(bool round, float rim)
        {
            if (_round == round && _rim == rim) return;
            _round = round;
            _rim = rim;
            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            Rect r = rectTransform.rect;
            OutlineGeometry.Build(_round, r.width * 0.5f, r.height * 0.5f, _rim, Feather, Xs, Ys, Tones, Tris);

            Color32 solid = color;
            var fill = new Color32(solid.r, solid.g, solid.b, (byte)Mathf.RoundToInt(solid.a * FillAlpha));
            var clear = new Color32(solid.r, solid.g, solid.b, 0);
            Vector2 c = r.center;
            for (int i = 0; i < Xs.Count; i++)
            {
                byte tone = Tones[i];
                Color32 col = tone == OutlineGeometry.Solid ? solid : tone == OutlineGeometry.Fill ? fill : clear;
                vh.AddVert(new Vector3(c.x + Xs[i], c.y + Ys[i], 0f), col, Vector4.zero);
            }
            for (int i = 0; i + 2 < Tris.Count; i += 3)
                vh.AddTriangle(Tris[i], Tris[i + 1], Tris[i + 2]);
        }
    }

    /// <summary>
    /// A combined territory's outline on a map: UnionOutlineGeometry's mesh, one rim along the outer edge of the
    /// whole ground and one fill, in the same three colours as TerritoryOutline - tinted by the graphic's colour.
    /// A MaskableGraphic on the default UI material, so the map's RectMask2D clips it.
    /// </summary>
    /// <remarks>
    /// Built by MapOverlay (Build), not here: OnPopulateMesh runs whenever the canvas asks - the graphic
    /// enabled again when the map opens, a colour change - and the cut costs milliseconds, so the mesh is kept
    /// and only turned into vertices here. Each vertex's colour mixes the three by its weights; they differ
    /// only in alpha, so it is the alpha that is mixed.
    /// </remarks>
    internal sealed class TerritoryUnionOutline : MaskableGraphic
    {
        private readonly List<float> _xs = new List<float>();
        private readonly List<float> _ys = new List<float>();
        private readonly List<float> _solid = new List<float>();
        private readonly List<float> _fill = new List<float>();
        private readonly List<int> _tris = new List<int>();
        private static float _lastError = -1000f;

        /// <summary>The mesh's extent about its origin, for the rect that must hold it.</summary>
        internal Rect Bounds { get; private set; }

        internal void Build(List<UnionOutlineGeometry.Part> parts, float rim)
        {
            try
            {
                UnionOutlineGeometry.Build(parts, rim, TerritoryOutline.Feather, _xs, _ys, _solid, _fill, _tris);
            }
            catch
            {
                // Half a mesh is worse than none: drop it, and let the overlay's guard report the failure.
                _xs.Clear();
                _ys.Clear();
                _solid.Clear();
                _fill.Clear();
                _tris.Clear();
                Bounds = Rect.zero;
                SetVerticesDirty();
                throw;
            }
            float minX = 0f, minY = 0f, maxX = 0f, maxY = 0f;
            for (int i = 0; i < _xs.Count; i++)
            {
                minX = Mathf.Min(minX, _xs[i]);
                maxX = Mathf.Max(maxX, _xs[i]);
                minY = Mathf.Min(minY, _ys[i]);
                maxY = Mathf.Max(maxY, _ys[i]);
            }
            Bounds = Rect.MinMaxRect(minX, minY, maxX, maxY);
            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            try
            {
                Color32 solid = color;
                float fillAlpha = solid.a * TerritoryOutline.FillAlpha;
                for (int i = 0; i < _xs.Count; i++)
                {
                    float a = solid.a * _solid[i] + fillAlpha * _fill[i];
                    var col = new Color32(solid.r, solid.g, solid.b, (byte)Mathf.Clamp(Mathf.RoundToInt(a), 0, 255));
                    vh.AddVert(new Vector3(_xs[i], _ys[i], 0f), col, Vector4.zero);
                }
                for (int i = 0; i + 2 < _tris.Count; i += 3)
                    vh.AddTriangle(_tris[i], _tris[i + 1], _tris[i + 2]);
            }
            catch (Exception ex)
            {
                // Called by the canvas, outside the overlay patch's own guard: draw nothing rather than throw into
                // the UI rebuild every frame.
                vh.Clear();
                if (Time.realtimeSinceStartup - _lastError < 30f) return;
                _lastError = Time.realtimeSinceStartup;
                Plugin.Log.LogError($"Combined territory outline failed (cosmetic only, repeats suppressed for 30 s): {ex}");
            }
        }
    }

    [HarmonyPatch(typeof(Minimap), "UpdateMap")]
    internal static class MinimapUpdateMapPatch
    {
        private static float _lastError = -1000f;

        private static void Postfix(Minimap __instance)
        {
            try
            {
                MapOverlay.Update(__instance);
            }
            catch (Exception ex)
            {
                // Cosmetic, inside a vanilla per-frame method: never let it throw into the map, and never
                // write the same failure sixty times a second.
                if (Time.realtimeSinceStartup - _lastError < 30f) return;
                _lastError = Time.realtimeSinceStartup;
                Plugin.Log.LogError($"Territory map overlay failed (cosmetic only, repeats suppressed for 30 s): {ex}");
            }
        }
    }
}
