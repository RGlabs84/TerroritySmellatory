using UnityEngine;

namespace TerroritySmellatory
{
    /// <summary>The outline a territory covers on the ground.</summary>
    /// <remarks>
    /// The numbers are PERSISTED (territory file and wire format), so they may only ever be appended to,
    /// never renumbered. They match the three Infinity Hammer ruler shapes a territory can be marked with.
    /// </remarks>
    public enum TerritoryShapeKind
    {
        Circle = 0,
        Square = 1,
        Rectangle = 2
    }

    // ONE PIECE OF GEOMETRY, READ BY EVERYTHING THAT CARES WHERE A TERRITORY IS.
    //
    // This is AwayFromHome's PenArea, carried over with its reasoning intact: the containment test that
    // enforces a territory, the outline drawn on the ground and the area drawn on the map all come from
    // this one struct through the same Quaternion, so a rotated box cannot be enforced one way and drawn
    // the other. A territory that visibly disagrees with itself would look exactly like a broken feature
    // and be nearly impossible to read from a log (VANILLA-PIECE-INTEROP-FACTS.md §6).
    //
    // HALF-EXTENTS, NOT WIDTHS - because that is what Infinity Hammer hands over. Its ruler draws a
    // rectangle from -Width to +Width and -Depth to +Depth (RectangleRuler.UpdateLines), a circle at
    // Radius, and substitutes <r>, <w> and <d> with exactly those numbers. So <r>=20 is a 40 m wide circle
    // and <w>=10 <d>=30 is a 20 x 60 m box, and nothing converts on the way in. Players think in full
    // widths, so Describe() quotes those.
    //
    // YAW is Infinity Hammer's <a>: the placement ghost's rotation about Y in degrees, which is also what
    // its ruler is rotated by (Ruler.Update: Quaternion.Euler(0, ghost.eulerAngles.y, 0)). Local X is the
    // width axis and local Z the depth axis, as in the ruler.
    //
    // HEIGHT IS IGNORED everywhere. A territory is a footprint, exactly like a vanilla ward, whose own
    // IsInside is a horizontal DistanceXZ test.
    public readonly struct TerritoryShape
    {
        public readonly TerritoryShapeKind Kind;
        public readonly float CenterX;
        public readonly float CenterZ;

        /// <summary>Half the extent along the territory's own X axis. For a circle this IS the radius.</summary>
        public readonly float HalfX;

        /// <summary>Half the extent along the territory's own Z axis. Equal to HalfX for a circle or square.</summary>
        public readonly float HalfZ;

        /// <summary>Rotation about Y in degrees. Meaningless for a circle and held at 0.</summary>
        public readonly float Yaw;

        public TerritoryShape(TerritoryShapeKind kind, float centerX, float centerZ, float halfX, float halfZ, float yaw)
        {
            Kind = kind;
            CenterX = centerX;
            CenterZ = centerZ;
            // A circle and a square are described by one number; carrying a second that must stay equal to
            // it is an invariant waiting for a partial write, so it is collapsed here instead of trusted.
            HalfX = Mathf.Max(0f, halfX);
            HalfZ = kind == TerritoryShapeKind.Rectangle ? Mathf.Max(0f, halfZ) : HalfX;
            Yaw = kind == TerritoryShapeKind.Circle ? 0f : Mathf.Repeat(yaw, 360f);
        }

        public Vector3 Center => new Vector3(CenterX, 0f, CenterZ);

        public bool IsEmpty => HalfX <= 0f || HalfZ <= 0f;

        /// <summary>Radius of the smallest circle holding the whole shape: a cheap conservative range.</summary>
        public float OuterRadius => Kind == TerritoryShapeKind.Circle ? HalfX : Mathf.Sqrt(HalfX * HalfX + HalfZ * HalfZ);

        public float Area => Kind == TerritoryShapeKind.Circle ? Mathf.PI * HalfX * HalfX : 4f * HalfX * HalfZ;

        public float Perimeter => Kind == TerritoryShapeKind.Circle ? 2f * Mathf.PI * HalfX : 4f * (HalfX + HalfZ);

        /// <summary>The point in the shape's own frame: X along its width, Z along its depth.</summary>
        private Vector3 ToLocal(Vector3 p)
        {
            Vector3 d = new Vector3(p.x - CenterX, 0f, p.z - CenterZ);
            return Kind == TerritoryShapeKind.Circle ? d : Quaternion.Euler(0f, -Yaw, 0f) * d;
        }

        public bool Contains(Vector3 p)
        {
            if (IsEmpty) return false;
            Vector3 l = ToLocal(p);
            if (Kind == TerritoryShapeKind.Circle) return l.x * l.x + l.z * l.z <= HalfX * HalfX;
            return Mathf.Abs(l.x) <= HalfX && Mathf.Abs(l.z) <= HalfZ;
        }

        /// <summary>How far outside the shape this point is, in metres; 0 when it is inside.</summary>
        public float DistanceOutside(Vector3 p)
        {
            if (IsEmpty) return float.MaxValue;
            Vector3 l = ToLocal(p);
            if (Kind == TerritoryShapeKind.Circle) return Mathf.Max(0f, Mathf.Sqrt(l.x * l.x + l.z * l.z) - HalfX);
            float dx = Mathf.Max(0f, Mathf.Abs(l.x) - HalfX);
            float dz = Mathf.Max(0f, Mathf.Abs(l.z) - HalfZ);
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        /// <summary>
        /// Does a circle of <paramref name="radius"/> around the point touch the shape? The same question a
        /// vanilla ward answers with IsInside(point, radius) - it is how ward placement and terrain ops, which
        /// have a size of their own, are tested against an area.
        /// </summary>
        public bool Overlaps(Vector3 p, float radius)
        {
            if (radius <= 0f) return Contains(p);
            return DistanceOutside(p) < radius;
        }

        /// <summary>Is the whole circle of <paramref name="radius"/> around the point inside the shape?</summary>
        public bool ContainsCircle(Vector3 p, float radius)
        {
            if (radius <= 0f) return Contains(p);
            if (IsEmpty) return false;
            Vector3 l = ToLocal(p);
            if (Kind == TerritoryShapeKind.Circle) return Mathf.Sqrt(l.x * l.x + l.z * l.z) + radius <= HalfX;
            return Mathf.Abs(l.x) + radius <= HalfX && Mathf.Abs(l.z) + radius <= HalfZ;
        }

        /// <summary>
        /// A point on the outline, t running 0..1 once around it, spaced by ARC LENGTH so a long thin box
        /// gets its segments spread along the fence line rather than bunched at the short ends.
        /// </summary>
        public Vector3 PerimeterPoint(float t)
        {
            t = Mathf.Repeat(t, 1f);

            if (Kind == TerritoryShapeKind.Circle)
            {
                // sin on X, cos on Z: CircleProjector's own convention, so segment 0 sits where a vanilla
                // ring's segment 0 does.
                float a = t * Mathf.PI * 2f;
                return new Vector3(CenterX + Mathf.Sin(a) * HalfX, 0f, CenterZ + Mathf.Cos(a) * HalfX);
            }

            float w = HalfX * 2f;
            float d = HalfZ * 2f;
            float s = t * (w + d) * 2f;

            Vector3 local;
            if (s < w) local = new Vector3(-HalfX + s, 0f, HalfZ);
            else if (s < w + d) local = new Vector3(HalfX, 0f, HalfZ - (s - w));
            else if (s < w + w + d) local = new Vector3(HalfX - (s - w - d), 0f, -HalfZ);
            else local = new Vector3(-HalfX, 0f, -HalfZ + (s - w - w - d));

            return new Vector3(CenterX, 0f, CenterZ) + Quaternion.Euler(0f, Yaw, 0f) * local;
        }

        /// <summary>Exactly the same ground, number for number: "has this changed?" for the visuals.</summary>
        public bool SameAs(TerritoryShape o) =>
            Kind == o.Kind && CenterX == o.CenterX && CenterZ == o.CenterZ && HalfX == o.HalfX && HalfZ == o.HalfZ && Yaw == o.Yaw;

        /// <summary>
        /// A box's corner in world X/Z: 0 bottom-left, 1 top-left, 2 top-right, 3 bottom-right of its own frame,
        /// so in order around it. Through the same Quaternion as Contains, so the corners are the enforced ones.
        /// Meaningless for a circle.
        /// </summary>
        public Vector3 Corner(int i)
        {
            var local = new Vector3(i == 0 || i == 1 ? -HalfX : HalfX, 0f, i == 0 || i == 3 ? -HalfZ : HalfZ);
            return new Vector3(CenterX, 0f, CenterZ) + Quaternion.Euler(0f, Yaw, 0f) * local;
        }

        /// <summary>The smallest world-axis box around the shape, in X and Z.</summary>
        public void Bounds(out float minX, out float minZ, out float maxX, out float maxZ)
        {
            if (Kind == TerritoryShapeKind.Circle)
            {
                minX = CenterX - HalfX;
                maxX = CenterX + HalfX;
                minZ = CenterZ - HalfX;
                maxZ = CenterZ + HalfX;
                return;
            }

            minX = minZ = float.MaxValue;
            maxX = maxZ = float.MinValue;
            for (int i = 0; i < 4; i++)
            {
                Vector3 c = Corner(i);
                minX = Mathf.Min(minX, c.x);
                maxX = Mathf.Max(maxX, c.x);
                minZ = Mathf.Min(minZ, c.z);
                maxZ = Mathf.Max(maxZ, c.z);
            }
        }

        /// <summary>The shape in one short phrase, in FULL widths - the unit players think in.</summary>
        public string Describe()
        {
            switch (Kind)
            {
                case TerritoryShapeKind.Square: return $"{HalfX * 2f:0.#} m square";
                case TerritoryShapeKind.Rectangle: return $"{HalfX * 2f:0.#} x {HalfZ * 2f:0.#} m";
                default: return $"{HalfX * 2f:0.#} m circle";
            }
        }
    }
}
