using UnityEngine;
using UnityEngine.UI;

namespace TerroritySmellatory
{
    /// <summary>
    /// The clip that keeps map graphics to uncovered ground: a RawImage carrying the explored mask, with a UI Mask on it. Everything
    /// that is a child of <see cref="Rect"/> is drawn only where the mask is covered.
    /// </summary>
    /// <remarks>
    /// Built here, not in MapOverlay, so the end-to-end rig can build the very same object on a canvas of its own and look at the pixels
    /// (F04). The Mask uses the RawImage's alpha as its stencil, with an alpha test at 0.001, which is why the mask texture is point
    /// filtered Alpha8 with covered = 255 and fog = 0 (ExploredBits): a cell is wholly in or wholly out. With the gate off both the
    /// RawImage and the Mask are disabled and the children draw as if it were not there; enabling one without the other would draw
    /// nothing at all (a Mask whose graphic draws nothing masks everything), so they only ever change together.
    /// </remarks>
    internal sealed class ExploredGate
    {
        internal RectTransform Rect { get; }
        private readonly RawImage _image;
        private readonly Mask _mask;

        private ExploredGate(RectTransform rect, RawImage image, Mask mask)
        {
            Rect = rect;
            _image = image;
            _mask = mask;
        }

        internal bool On => _image.enabled;

        /// <summary>A gate filling <paramref name="parent"/>, off until it is given a mask.</summary>
        internal static ExploredGate Build(RectTransform parent)
        {
            // The RawImage before the Mask in the list: a Mask finds its graphic with GetComponent<Graphic>.
            var go = new GameObject("TS_ExploredGate", typeof(RectTransform), typeof(RawImage), typeof(Mask));
            go.layer = parent.gameObject.layer;
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            RawImage image = go.GetComponent<RawImage>();
            image.raycastTarget = false;
            image.enabled = false;
            Mask mask = go.GetComponent<Mask>();
            mask.showMaskGraphic = false;
            mask.enabled = false;
            return new ExploredGate(rect, image, mask);
        }

        /// <summary>
        /// Clips to <paramref name="texture"/>, which covers the same ground as the map picture in <paramref name="uv"/> (the picture's own
        /// uvRect), or lets everything through when there is no texture.
        /// </summary>
        internal void Set(Texture2D texture, Rect uv)
        {
            bool on = texture != null;
            if (on)
            {
                if (_image.texture != texture) _image.texture = texture;
                if (_image.uvRect != uv) _image.uvRect = uv;
            }
            if (_image.enabled == on) return;
            _image.enabled = on;
            _mask.enabled = on;
        }
    }
}
