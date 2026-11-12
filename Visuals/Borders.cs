using System;
using System.Collections.Generic;
using UnityEngine;

namespace TerroritySmellatory
{
    /// <summary>Territory borders drawn on the ground, near the player, when they are useful.</summary>
    /// <remarks>
    /// The look is vanilla's own: the segment prefab and ground-ray mask are harvested from the first
    /// CraftingStation whose area marker carries a CircleProjector (the workbench ring), exactly as
    /// AwayFromHome's leash does - scanned for, never named, because no prefab-name string for the workbench
    /// exists in the decompile to check a guess against (VANILLA-PIECE-INTEROP-FACTS §6). No art ships.
    ///
    /// The outline comes from TerritoryShape.PerimeterPoint, the same struct Contains() enforces with, so
    /// the drawn line and the enforced line cannot disagree.
    ///
    /// A territory whose edges add up to more than 100 km (a continent) gets no border: see MaxDrawnEdge.
    ///
    /// A territory of several parts (0.2.0) is ONE area, so it gets one border: its OUTER edge. Each part's
    /// outline is drawn except where it runs inside another part - the other parts' own Contains again
    /// (BorderOutline.Outer) - so there is no line across ground the territory holds, and where two parts
    /// cross, each part's stretch simply stops at the other. Where two parts' outlines run together, one
    /// draws it.
    /// </remarks>
    internal static class Borders
    {
        private static GameObject _segmentPrefab;
        private static LayerMask _mask;
        private static bool _harvested;

        private static readonly Dictionary<int, BorderOutline> _outlines = new Dictionary<int, BorderOutline>();
        private static float _selectTimer;
        private static int _selectedRevision = -1;

        internal static void Tick()
        {
            Player player = Player.m_localPlayer;
            if (player == null || !Wanted(player) || !TerritoryIndex.Any)
            {
                HideAll();
                return;
            }
            if (!Harvest()) return;

            _selectTimer -= Time.deltaTime;
            if (_selectTimer > 0f && _selectedRevision == TerritoryIndex.LocalRevision) return;
            _selectTimer = 0.5f;
            _selectedRevision = TerritoryIndex.LocalRevision;

            Vector3 me = player.transform.position;
            float range = TSConfig.BorderRange.Value;
            var keep = new HashSet<int>();
            // An admin with the panel open sees every border, hidden ones included - they are the one
            // person who needs to find a territory nobody else is meant to see.
            bool seeHidden = TerritoryPanel.IsOpen && TerritoryClient.IsAdmin;

            foreach (Territory t in TerritoryIndex.All)
            {
                if (!seeHidden && !t.Has(TerritoryFlags.ShowBorder)) continue;
                TerritoryGround ground = t.Ground;
                if (TooLong(ground))
                {
                    NoteTooLong(t);
                    continue;
                }
                // Near its EDGE, not its centre: standing in the middle of a 500 m territory, its border is
                // nowhere near you and drawing it helps nobody.
                if (ground.Contains(me) ? !NearEdge(ground, me, range) : ground.DistanceOutside(me) > range) continue;

                keep.Add(t.Id);
                if (!_outlines.TryGetValue(t.Id, out BorderOutline outline) || outline == null)
                {
                    var go = new GameObject($"TS_Border_{t.Id}");
                    UnityEngine.Object.DontDestroyOnLoad(go);
                    outline = go.AddComponent<BorderOutline>();
                    outline.Init(_segmentPrefab, _mask);
                    _outlines[t.Id] = outline;
                }
                outline.Show(ground);
            }

            var drop = new List<int>();
            foreach (KeyValuePair<int, BorderOutline> kv in _outlines)
                if (!keep.Contains(kv.Key)) drop.Add(kv.Key);
            foreach (int id in drop)
            {
                if (_outlines[id] != null) UnityEngine.Object.Destroy(_outlines[id].gameObject);
                _outlines.Remove(id);
            }
        }

        /// <summary>
        /// The longest edge a border is drawn for, in metres, all parts together. The border is worked out by walking the
        /// whole outline at 2.5 m (BorderOutline.Resample) the first time the player is near it: 40,000 samples at this
        /// length, a few tens of milliseconds, but a continent's coast would be hundreds of thousands and a stall of a second
        /// or more as a ship crosses it. A territory that long is a region, not a plot: it has its name on the map
        /// (MapNameOnly), and no line on the ground.
        /// </summary>
        private const float MaxDrawnEdge = 100000f;

        private static bool TooLong(TerritoryGround ground)
        {
            float edge = 0f;
            for (int i = 0; i < ground.Count; i++) edge += ground[i].Perimeter;
            return edge > MaxDrawnEdge;
        }

        private static readonly HashSet<int> _toldTooLong = new HashSet<int>();

        private static void NoteTooLong(Territory t)
        {
            if (_toldTooLong.Add(t.Id))
                Plugin.Log.LogInfo($"'{t.DisplayName}' is too big to draw a border for (an edge over {MaxDrawnEdge / 1000f:0} km); its name still shows on the map.");
        }

        private static bool NearEdge(TerritoryGround ground, Vector3 p, float range)
        {
            // Inside: is some point of the border within range? Shrinking the shape by `range` and asking
            // whether p is still inside answers exactly that. For several parts it is conservative - a circle
            // across the seam of two overlapping parts is held by neither, so this says "near an edge" where
            // there is none - and the outline then finds no outer edge within range and draws nothing.
            return !ground.ContainsCircle(p, range);
        }

        private static bool Wanted(Player player)
        {
            switch (TSConfig.Borders.Value)
            {
                case BorderMode.Off: return false;
                case BorderMode.PanelOnly: return TerritoryPanel.IsOpen;
                case BorderMode.PanelOrBuilding: return TerritoryPanel.IsOpen || player.InPlaceMode();
                default: return true;
            }
        }

        private static void HideAll()
        {
            if (_outlines.Count == 0) return;
            foreach (BorderOutline outline in _outlines.Values)
                if (outline != null) UnityEngine.Object.Destroy(outline.gameObject);
            _outlines.Clear();
            _selectedRevision = -1;
        }

        private static bool Harvest()
        {
            if (_harvested) return _segmentPrefab != null;
            if (ZNetScene.instance == null) return false;   // not in a world yet - try again next tick

            try
            {
                foreach (GameObject prefab in ZNetScene.instance.m_prefabs)
                {
                    if (prefab == null) continue;
                    CraftingStation station = prefab.GetComponent<CraftingStation>();
                    GameObject marker = station != null ? station.m_areaMarker : null;
                    CircleProjector cp = marker != null ? marker.GetComponent<CircleProjector>() : null;
                    if (cp == null || cp.m_prefab == null) continue;

                    _segmentPrefab = cp.m_prefab;
                    _mask = cp.m_mask;
                    Plugin.Debug($"border segments taken from '{prefab.name}'");
                    break;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Could not find a vanilla ground-outline to copy (borders will not be drawn): {ex.Message}");
            }

            _harvested = true;
            if (_segmentPrefab == null)
            {
                Plugin.Log.LogWarning("No vanilla ground-outline found - territory borders will not be drawn. Everything else works.");
            }
            return _segmentPrefab != null;
        }
    }

    /// <summary>One territory's visible stretch of border.</summary>
    internal sealed class BorderOutline : MonoBehaviour
    {
        // One segment every 2.5 m: close to the workbench ring's density (20 segments for ~10 m radius).
        private const float Spacing = 2.5f;

        // How far outside its own part a sample is asked about, and how close two parts' outlines must run to
        // count as one line: past float noise even kilometres from the world's centre, and far below anything
        // the eye can tell from the line itself.
        private const float Hair = 0.05f;

        /// <summary>A point of the outer edge: on one part's outline, where the territory ends.</summary>
        private struct Sample
        {
            public Vector3 At;       // on the outline, at height 0 - the ray seats it when it is drawn
            public int Part;
            public float T;          // where around its part: PerimeterPoint's t
            public float Step;       // half its part's sample spacing, in t: how far either side its tangent is taken
            public int Prev, Next;   // the samples either side along the same stretch of edge, or -1 where it stops
        }

        private GameObject _prefab;
        private LayerMask _mask;
        private bool _shown;                                     // false until the first Show: the default _first could pass for a ground
        private TerritoryShape _first;
        private readonly List<TerritoryShape> _more = new List<TerritoryShape>();
        private bool _resample;
        private float _reseatAt;
        private readonly List<GameObject> _pool = new List<GameObject>();
        private readonly List<Sample> _samples = new List<Sample>();
        private readonly List<int> _partEnd = new List<int>();   // one past each part's last sample
        private readonly List<int> _drawn = new List<int>();     // the samples drawn, in pool order
        private int[] _slot = new int[0];                        // each sample's pool slot while it is drawn, else -1
        private int[] _around = new int[0];                      // resampling scratch: the sample at each step around a part, or -1

        internal void Init(GameObject prefab, LayerMask mask)
        {
            _prefab = prefab;
            _mask = mask;
        }

        internal void Show(TerritoryGround ground)
        {
            // The outer edge is worked out once per change of GROUND - any part, number for number - never per
            // frame, and not per revision either: a rename, or an edit to the rules or members, bumps that without
            // moving an inch of border, and on a big compound one resample is tens of milliseconds, so every admin
            // save would stall each client standing near it. The parts are copied rather than viewed: the samples
            // belong to the ground they were taken from, and a view of the territory's own list would change under
            // them if that list were ever edited in place.
            if (_shown && SameGround(ground)) return;
            _shown = true;
            _first = ground[0];
            _more.Clear();
            for (int i = 1; i < ground.Count; i++) _more.Add(ground[i]);
            _resample = true;
            _reseatAt = 0f;
        }

        private bool SameGround(TerritoryGround ground)
        {
            if (ground.Count != 1 + _more.Count || !ground[0].SameAs(_first)) return false;
            for (int i = 1; i < ground.Count; i++)
                if (!ground[i].SameAs(_more[i - 1])) return false;
            return true;
        }

        private TerritoryShape Part(int index) => index == 0 ? _first : _more[index - 1];

        private void Update()
        {
            // Territories do not move by themselves and terrain barely does, so the segments are re-seated
            // once a second (and at once after a change), not every frame - a hundred downward raycasts a
            // frame per territory would be spent re-measuring ground that has not changed.
            if (Time.time < _reseatAt) return;
            _reseatAt = Time.time + 1f;

            Player player = Player.m_localPlayer;
            if (player == null || _prefab == null || (_first.IsEmpty && _more.Count == 0)) return;

            try
            {
                if (_resample) Resample();
                Reseat(player.transform.position);
            }
            catch (Exception ex)
            {
                enabled = false;   // a cosmetic overlay must never spam the log every second
                Plugin.Log.LogError($"A territory border failed and was switched off (cosmetic only): {ex}");
            }
        }

        /// <summary>
        /// Walks every part's outline at the border spacing and keeps the samples on the OUTER edge, each linked
        /// to its neighbours along it.
        /// </summary>
        private void Resample()
        {
            _resample = false;
            _samples.Clear();
            _partEnd.Clear();
            _drawn.Clear();
            var ground = new TerritoryGround(_first, _more);

            for (int p = 0; p < ground.Count; p++)
            {
                TerritoryShape part = ground[p];
                if (!part.IsEmpty)
                {
                    // The spacing and the points a lone territory has always had, so one of a single part is
                    // drawn exactly as before.
                    int total = Mathf.Max(8, Mathf.CeilToInt(part.Perimeter / Spacing));
                    float step = 0.5f / total;
                    if (_around.Length < total) _around = new int[total];
                    for (int k = 0; k < total; k++)
                    {
                        float t = k / (float)total;
                        Vector3 at = part.PerimeterPoint(t);
                        if (!Outer(ground, p, at, part.PerimeterPoint(t - step), part.PerimeterPoint(t + step)))
                        {
                            _around[k] = -1;
                            continue;
                        }
                        _around[k] = _samples.Count;
                        _samples.Add(new Sample { At = at, Part = p, T = t, Step = step });
                    }

                    // Neighbours around the part's own loop, wrapping at its start. A part nothing cuts into is one
                    // closed ring, as a lone territory always was; a part another one overlaps falls into open
                    // stretches, each ending where its edge runs into the other part.
                    for (int k = 0; k < total; k++)
                    {
                        int i = _around[k];
                        if (i < 0) continue;
                        Sample s = _samples[i];
                        s.Prev = _around[(k + total - 1) % total];
                        s.Next = _around[(k + 1) % total];
                        _samples[i] = s;
                    }
                }
                _partEnd.Add(_samples.Count);
            }

            if (_slot.Length < _samples.Count) _slot = new int[_samples.Count];
            for (int i = 0; i < _slot.Length; i++) _slot[i] = -1;
        }

        /// <summary>
        /// Is this sample of part <paramref name="p"/> on the ground's outer edge, and is it this part's to draw?
        /// <paramref name="before"/> and <paramref name="after"/> are the outline either side of it: its chord.
        /// </summary>
        private static bool Outer(TerritoryGround ground, int p, Vector3 at, Vector3 before, Vector3 after)
        {
            // Asked a hair OUTSIDE the part, square to its outline, not on the line itself: the territory ends
            // here if that point is in no other part. On the line, Contains counts a boundary as inside, so two
            // parts lined up along one side - two boxes side by side, an L whose arms share an edge - would each
            // find the shared stretch inside the other and neither would draw it, though the rules end the
            // territory right there; and far from the world's centre float noise would decide it sample by sample.
            // Stepping out still finds every real seam: a stretch inside another part, or back to back with one,
            // steps into it.
            Vector3 along = after - before;
            Vector3 outward = new Vector3(-along.z, 0f, along.x).normalized;
            Vector3 probe = at + outward * Hair;

            for (int j = 0; j < ground.Count; j++)
            {
                if (j == p) continue;
                TerritoryShape other = ground[j];

                // A part whose bounding circle is more than two hairs off can neither hold the probe, a hair from
                // here, nor run within a hair of this point, so it is passed over before its Contains builds a
                // rotation. Exact, and most parts of a big compound are nowhere near most samples: resampling sixteen
                // 1200 m squares went from about 26 ms to 7 (.NET 8), the samples the same to the bit.
                float dx = at.x - other.CenterX, dz = at.z - other.CenterZ, reach = other.OuterRadius + 2f * Hair;
                if (dx * dx + dz * dz > reach * reach) continue;

                if (other.Contains(probe)) return false;

                // Where two parts' outlines run together, the lower-numbered part draws the shared stretch, once.
                if (j < p && other.DistanceOutside(at) <= Hair && !other.ContainsCircle(at, Hair)) return false;
            }
            return true;
        }

        private void Reseat(Vector3 me)
        {
            float range = TSConfig.BorderRange.Value;
            float rangeSq = range * range;

            // Which stretch of the border to draw: every outer sample within range of the player, on whichever
            // part and stretch it lies - "the nearest point and a window either side" only works for one closed
            // loop. A part whose outline is nowhere within range is passed over whole, so the cost follows what
            // is near the player, not the territory's size.
            for (int d = 0; d < _drawn.Count; d++) _slot[_drawn[d]] = -1;
            _drawn.Clear();
            int from = 0;
            for (int p = 0; p < _partEnd.Count; p++)
            {
                int to = _partEnd[p];
                TerritoryShape part = Part(p);
                if (to > from && part.DistanceOutside(me) <= range && !part.ContainsCircle(me, range))
                {
                    for (int i = from; i < to; i++)
                    {
                        Vector3 at = _samples[i].At;
                        float dx = at.x - me.x, dz = at.z - me.z;
                        if (dx * dx + dz * dz > rangeSq) continue;
                        _slot[i] = _drawn.Count;
                        _drawn.Add(i);
                    }
                }
                from = to;
            }

            while (_pool.Count < _drawn.Count)
                _pool.Add(Instantiate(_prefab, transform));

            for (int s = 0; s < _pool.Count; s++)
            {
                bool used = s < _drawn.Count;
                if (_pool[s].activeSelf != used) _pool[s].SetActive(used);
                if (!used) continue;

                Vector3 at = _samples[_drawn[s]].At;
                at.y = me.y;
                // Vanilla's own numbers (CircleProjector): half a kilometre up, a kilometre down.
                if (Physics.Raycast(at + Vector3.up * 500f, Vector3.down, out RaycastHit hit, 1000f, _mask.value))
                    at.y = hit.point.y;
                _pool[s].transform.position = at;
            }

            // Orientation is a SECOND pass, aimed from the neighbours' seated positions, as vanilla does it:
            // doing both in one loop aims each segment at where its successor used to be, which twists the
            // line on a slope. Where the line stops - at a seam, or where the stretch in range ends - there is
            // no seated neighbour to aim at, and the segment aims along its part's own tangent instead.
            for (int s = 0; s < _drawn.Count; s++)
            {
                Sample sample = _samples[_drawn[s]];
                int before = sample.Prev >= 0 ? _slot[sample.Prev] : -1;
                int after = sample.Next >= 0 ? _slot[sample.Next] : -1;

                Vector3 prev, next;
                if (before >= 0 && after >= 0)
                {
                    prev = _pool[before].transform.position;
                    next = _pool[after].transform.position;
                }
                else
                {
                    TerritoryShape part = Part(sample.Part);
                    prev = part.PerimeterPoint(sample.T - sample.Step);
                    next = part.PerimeterPoint(sample.T + sample.Step);
                    prev.y = next.y = _pool[s].transform.position.y;
                }

                Vector3 along = next - prev;
                if (along.sqrMagnitude > 0.0001f)
                    _pool[s].transform.rotation = Quaternion.LookRotation(along.normalized, Vector3.up);
            }
        }
    }
}
