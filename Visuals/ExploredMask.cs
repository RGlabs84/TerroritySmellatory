using System;
using System.Collections;
using HarmonyLib;
using UnityEngine;

namespace TerroritySmellatory
{
    /// <summary>
    /// A copy of what the player's map has uncovered, as an alpha texture the map overlay clips the territories against, so a
    /// territory shows only over ground that is no longer fog.
    /// </summary>
    /// <remarks>
    /// One byte per map cell (Alpha8, point-filtered, the minimap's own texture size): 255 where the cell is explored - by this player
    /// or from another's shared map, Minimap.IsExplored's rule - and 0 elsewhere. MapOverlay puts it on a RawImage with a UI Mask over its
    /// territory graphics and gives it the map picture's own uvRect each frame, so a cell of the mask sits exactly on the cell of the
    /// map that the fog covers. The mask is the whole mechanism: nothing is cut or tested per territory.
    ///
    /// Kept up to date by small postfixes on the methods that write vanilla's two BitArrays (Explore and ExploreOthers, one cell at a
    /// time; Reset, ResetSharedMapData and ResetAndExplore, everything). They only note WHICH cells changed (ExploredBox); the copy
    /// happens in Current, when a map is being drawn, so a map that is closed costs nothing. A copy is the dirty box's cells (usually
    /// a 19 x 19 patch around the player) and one upload of the texture (4 MB at 2048 x 2048), about once per vanilla explore tick.
    /// The first copy after a world loads or a reset is every cell, a few milliseconds once.
    /// </remarks>
    internal static class ExploredMask
    {
        private static readonly ExploredBox Box = new ExploredBox();
        private static Minimap _owner;
        private static int _size;
        private static Texture2D _texture;
        private static byte[] _cells;
        private static int[] _self;
        private static int[] _others;

        /// <summary>Making the mask failed for this map: logged once, and the overlay draws unmasked rather than nothing.</summary>
        private static bool _failed;

        internal static void Mark(int x, int y) => Box.Mark(x, y);

        internal static void MarkAll() => Box.MarkAll();

        /// <summary>The mask brought up to date for this map, or null when there is none (the map is not ready, or it cannot be made).</summary>
        internal static Texture2D Current(Minimap map)
        {
            if (map == null) return null;
            if (map != _owner || map.m_textureSize != _size)
            {
                Discard();
                _owner = map;
                _size = map.m_textureSize;
                _failed = false;
                Box.MarkAll();
            }
            if (_failed || map.m_explored == null || map.m_exploredOthers == null || _size <= 0) return null;

            try
            {
                if (_texture == null) Create();
                if (Box.Dirty) Flush(map);
                return _texture;
            }
            catch (Exception ex)
            {
                _failed = true;
                Discard();
                Plugin.Log.LogError($"The map's explored mask could not be made, so territories are drawn over the whole map this session (cosmetic only): {ex}");
                return null;
            }
        }

        /// <summary>Lets go of the texture (a new Minimap, or a texture size that changed).</summary>
        internal static void Discard()
        {
            if (_texture != null) UnityEngine.Object.Destroy(_texture);
            _texture = null;
            _cells = null;
            _self = null;
            _others = null;
        }

        private static void Create()
        {
            if (!SystemInfo.SupportsTextureFormat(TextureFormat.Alpha8))
                throw new InvalidOperationException("this graphics device has no Alpha8 texture format");
            long cells = (long)_size * _size;
            if (cells > int.MaxValue / 2) throw new InvalidOperationException($"a {_size} x {_size} map texture is too big for the mask");

            _texture = new Texture2D(_size, _size, TextureFormat.Alpha8, false, true)
            {
                name = "TS_ExploredMask",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            _cells = new byte[cells];
            _self = new int[ExploredBits.Words((int)cells)];
            _others = new int[_self.Length];
            Box.MarkAll();
        }

        private static void Flush(Minimap map)
        {
            // Both sets, packed: a 512 KB copy each, whatever the box. Less than reading a bit at a time, and far less than the upload.
            map.m_explored.CopyTo(_self, 0);
            map.m_exploredOthers.CopyTo(_others, 0);
            if (!Box.Take(_size, out int x0, out int y0, out int x1, out int y1)) return;
            ExploredBits.Fill(_self, _others, _size, x0, y0, x1, y1, _cells);
            _texture.LoadRawTextureData(_cells);
            _texture.Apply(false);
        }
    }

    // One patch class per target (as in Rules/): a game update that moves one costs the mask's freshness, which Current's full copy
    // after the next reset puts right, and is logged by Plugin.PatchIsolated instead of taking the mod down.

    /// <summary>Vanilla uncovers one map cell of the player's own: the cell is the one that changed.</summary>
    [HarmonyPatch(typeof(Minimap), "Explore", typeof(int), typeof(int))]
    internal static class MinimapExploreCellPatch
    {
        private static void Postfix(bool __result, int x, int y)
        {
            if (__result) ExploredMask.Mark(x, y);
        }
    }

    /// <summary>Vanilla uncovers one map cell from another player's shared map.</summary>
    [HarmonyPatch(typeof(Minimap), "ExploreOthers", typeof(int), typeof(int))]
    internal static class MinimapExploreOthersCellPatch
    {
        private static void Postfix(bool __result, int x, int y)
        {
            if (__result) ExploredMask.Mark(x, y);
        }
    }

    /// <summary>A new character or world: the map is fog again.</summary>
    [HarmonyPatch(typeof(Minimap), nameof(Minimap.Reset))]
    internal static class MinimapResetPatch
    {
        private static void Postfix() => ExploredMask.MarkAll();
    }

    /// <summary>The shared map's part of it is dropped.</summary>
    [HarmonyPatch(typeof(Minimap), nameof(Minimap.ResetSharedMapData))]
    internal static class MinimapResetSharedPatch
    {
        private static void Postfix() => ExploredMask.MarkAll();
    }

    /// <summary>A saved map loaded, or another's map imported whole: both sets are replaced.</summary>
    [HarmonyPatch(typeof(Minimap), "ResetAndExplore", typeof(BitArray), typeof(BitArray))]
    internal static class MinimapResetAndExplorePatch
    {
        private static void Postfix() => ExploredMask.MarkAll();
    }
}
