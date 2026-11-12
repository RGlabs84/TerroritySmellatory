using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace TerroritySmellatory
{
    /// <summary>
    /// 0.3.1: one pooled patch of the shield wall - a grid mesh standing on the territory's edge - and the fader that
    /// drives it, in Njord's shape (a pooled GameObject under a DontDestroyOnLoad root whose own MonoBehaviour runs its
    /// life and hands it back when it is done). What it looks like is BarrierPattern; BarrierFlare owns the pool.
    /// </summary>
    /// <remarks>
    /// The mesh's VERTEX COLOURS are the whole animation, rewritten each frame into arrays the patch owns, so nothing
    /// is allocated per frame. The material multiplies them into the lattice texture (BarrierMaterials).
    /// </remarks>
    internal sealed class BarrierPatch : MonoBehaviour
    {
        internal bool Live;
        internal float Born, LastBurst = -100f;
        internal Vector3 At, Outward;
        internal BarrierPattern.Look Look;
        internal MeshRenderer Renderer;

        private Mesh _mesh;
        private int _count;                                  // vertices in use
        private Color32[] _colors;
        private float[] _distance, _mask;
        private int[] _seed;

        // The grid's scratch, shared by every patch: a layout happens on the main thread, one at a time.
        private static readonly Vector3[] Verts = new Vector3[BarrierPattern.MaxVertices];
        private static readonly Vector2[] Uvs = new Vector2[BarrierPattern.MaxVertices];
        private static readonly int[] Tris = new int[BarrierPattern.MaxIndices];

        /// <summary>A new, inactive patch under <paramref name="parent"/>.</summary>
        internal static BarrierPatch Create(Transform parent, int index, Material material)
        {
            var go = new GameObject("patch" + index);
            go.transform.SetParent(parent, false);
            go.SetActive(false);
            var p = go.AddComponent<BarrierPatch>();
            p._mesh = new Mesh { name = "TS_BarrierPatch", hideFlags = HideFlags.HideAndDontSave };
            p._mesh.MarkDynamic();
            go.AddComponent<MeshFilter>().sharedMesh = p._mesh;
            p.Renderer = go.AddComponent<MeshRenderer>();
            p.Renderer.sharedMaterial = material;
            // A glow, not a solid: it neither casts nor takes shadows, and light probes mean nothing to it.
            p.Renderer.shadowCastingMode = ShadowCastingMode.Off;
            p.Renderer.receiveShadows = false;
            p.Renderer.lightProbeUsage = LightProbeUsage.Off;
            p.Renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            p._colors = new Color32[BarrierPattern.MaxVertices];
            p._distance = new float[BarrierPattern.MaxVertices];
            p._mask = new float[BarrierPattern.MaxVertices];
            p._seed = new int[BarrierPattern.MaxVertices];
            return p;
        }

        /// <summary>Builds the grid into the mesh and works out the parts of each vertex that never change.</summary>
        internal void Layout(float height, float strength, bool fresh, int seed)
        {
            BarrierPattern.Look old = Look;
            BarrierPattern.Look k = BarrierPattern.MakeLook(height, strength, fresh ? seed : 0);
            if (!fresh) { k.OffX = old.OffX; k.OffY = old.OffY; }   // a harder push resizes the flare it joined, but the runes stay put
            Look = k;

            int stride = k.Nx + 1;
            int n = BarrierPattern.VertexCount(in k);
            for (int j = 0; j <= k.Ny; j++)
                for (int i = 0; i <= k.Nx; i++)
                {
                    int idx = j * stride + i;
                    BarrierPattern.VertexPos(in k, i, j, out float u, out float v);
                    BarrierPattern.VertexUv(in k, u, v, out float tu, out float tv);
                    BarrierPattern.Precompute(in k, u, v, out _distance[idx], out _mask[idx], out _seed[idx]);
                    Verts[idx] = new Vector3(u, v, 0f);
                    Uvs[idx] = new Vector2(tu, tv);
                    _colors[idx] = default;
                }
            int indices = BarrierPattern.Triangles(k.Nx, k.Ny, Tris);
            _count = n;

            Mesh m = _mesh;
            m.Clear();
            m.SetVertices(Verts, 0, n);
            m.SetUVs(0, Uvs, 0, n);
            m.SetColors(_colors, 0, n);
            m.SetTriangles(Tris, 0, indices, 0, false);
            m.bounds = new Bounds(new Vector3(0f, k.V0 + k.Height * 0.5f, 0f), new Vector3(k.Width, k.Height, 0.2f));
            // A patch resized while it is showing must not render one blank frame between here and its next Update.
            if (Live) Paint(Time.time - Born);
        }

        /// <summary>Stands the patch on the edge at its point of contact, facing out, and switches it on.</summary>
        internal void Show(float now)
        {
            Born = now;
            Live = true;
            transform.SetPositionAndRotation(At, Quaternion.LookRotation(Outward, Vector3.up));
            Renderer.enabled = true;
            gameObject.SetActive(true);
            Paint(0f);   // the first frame is already lit: the impact's flash is never a fade-in
        }

        private void Update()
        {
            if (!Live) return;
            try
            {
                float t = Time.time - Born;
                if (t >= BarrierPattern.Lifetime)
                {
                    BarrierFlare.Retire(this);
                    return;
                }
                Paint(t);
            }
            catch (Exception ex)
            {
                BarrierFlare.Failed("tick", ex);
            }
        }

        private void Paint(float t)
        {
            int mode = BarrierMaterials.Mode;
            int n = _count;
            for (int v = 0; v < n; v++)
            {
                BarrierPattern.Shade(in Look, _distance[v], _mask[v], _seed[v], t, mode, out byte r, out byte g, out byte b, out byte a);
                _colors[v] = new Color32(r, g, b, a);
            }
            _mesh.SetColors(_colors, 0, n);
        }

        /// <summary>Off and back in the pool.</summary>
        internal void Hide()
        {
            Live = false;
            if (Renderer != null) Renderer.enabled = false;
            gameObject.SetActive(false);
        }
    }
}
