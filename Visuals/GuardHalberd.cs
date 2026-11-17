using System;
using System.Linq;
using UnityEngine;

namespace TerroritySmellatory
{
    // The atgeir in a Meshy guard's hands (the user, 2026-10-07: "Use the already existing [atgeir] ... it's basically a halberd"). The model
    // is AtgeirIron's own "attach" child, put on the right hand as VisEquipment puts it on a player's (with the item's "equipoffset");
    // GuardWeapon gives him the matching atgeir to fight with.
    // The grips are measured, never assumed, as the body's facing is (MeshyBodies.FacingTurn): from the mesh's BIND pose each hand runs from
    // its forearm (its fingers' way), its thumb is on the side the body faces (Meshy rigs a body in an A-pose, palms down) and its palm is the
    // side below. The shaft goes through the right fist along the thumb's way, head up past the thumb, its edge leading where the knuckles
    // point. Its line comes from the mesh itself: the model lies at an angle in its own frame (look walk 14: the box's longest axis stood
    // upright and the shaft leaned 25-30 deg forward).
    // Standing and walking, his right arm is put into an "order arms" carry each frame after the clips: the wrist a little below his hip
    // joint's reach, ahead of the shoulder, the fist turned thumb up (a right hand shaking hands, palm to the body), so the shaft stands
    // upright with its butt just off the ground by his right foot. He strikes with both hands (the user, look walk 14: "he wield a 2h weapon
    // like it was 1h"): only with the guard pack's two-handed overhead chop (Meshy's "Attack"), the shaft slid through his right fist until
    // it holds the lower part, and his left hand put on the shaft below the right, thumbs both toward the head. The carry and the two-handed
    // grip each blend over 0.2 s.
    internal sealed class GuardHalberd
    {
        internal const string ChildName = "TS_GuardHalberd";
        // where a fist closes on the shaft: from the wrist along the hand, and in toward the palm (guard_v2's gauntlet hand is about 24 cm)
        private const float GripAlong = 0.10f, GripIn = 0.03f;
        // the carry, in the NPC's axes: the wrist this far below the shoulder joint, ahead of it and out from it; the butt this high in the idle
        private const float CarryDrop = 0.47f, CarryAhead = 0.10f, CarryOut = 0.08f, ButtUp = 0.05f;
        // the strike: the right fist this far up the shaft from its butt, the left fist this far below the right
        private const float StrikeGrip = 0.60f, LeftGap = 0.30f;
        private const float BlendSeconds = 0.2f;

        // one hand's grip, measured in the bind pose, in the hand bone's space
        private sealed class Grip
        {
            public Transform Upper, Lower, Hand;
            public Vector3 Thumb, Fingers, Centre, BindThumb, BindPalm;
            public Quaternion ToFrame;   // undoes the hand's own frame: a hand turned to (thumb, fingers) = LookRotation(thumb, fingers) * ToFrame
        }

        private readonly Transform _root, _holder;
        private readonly Grip _right, _left;   // no left arm: he strikes one-handed
        private readonly Quaternion _turn;     // the holder's turn in the right hand
        private readonly Vector3 _near, _head; // the shaft's line in the holder's space: the point nearest the player's grip, the way to the head
        private readonly float _buttAt, _carryGrip, _handScale;
        private float _carry = 1f, _strike;    // carried from the first frame: never a flash of the atgeir lying level in a hanging hand

        private GuardHalberd(Transform root, Transform holder, Grip right, Grip left, Quaternion turn, Vector3 near, Vector3 head, float buttAt, float carryGrip)
        {
            _root = root;
            _holder = holder;
            _right = right;
            _left = left;
            _turn = turn;
            _near = near;
            _head = head;
            _buttAt = buttAt;
            _carryGrip = carryGrip;
            _handScale = right.Hand.lossyScale.x;
        }

        internal bool TwoHanded => _left != null;

        private static readonly System.Collections.Generic.HashSet<string> s_logged = new System.Collections.Generic.HashSet<string>();

        // Puts the atgeir in the body's right hand, the body standing in its idle pose on the ground (MeshyBody, after its feet are measured)
        internal static GuardHalberd Attach(Transform root, GameObject body, string asset)
        {
            Transform[] all = body.GetComponentsInChildren<Transform>(true);
            SkinnedMeshRenderer smr = body.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .Where(r => r != null && r.sharedMesh != null && r.bones != null && r.bones.Length > 0 && r.sharedMesh.bindposes.Length == r.bones.Length)
                .OrderByDescending(r => r.sharedMesh.vertexCount).FirstOrDefault();
            Grip right = smr != null ? MeasureGrip(root, smr, all, "Right") : null, left = smr != null ? MeasureGrip(root, smr, all, "Left") : null;
            if (right == null)
            {
                Once(asset, $"[guards] Meshy body {asset}: no right arm bones with a bind pose (RightArm, RightForeArm, RightHand), so he holds no atgeir");
                return null;
            }
            GameObject item = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(GuardWeapon.ModelItem) : null;
            Transform attach = null, offset = null;
            if (item != null)
                foreach (Transform c in item.transform)
                {
                    if (c.name == "attach") attach = c;
                    else if (c.name == "equipoffset") offset = c;
                }
            if (attach == null)
            {
                Once(asset, $"[guards] Meshy body {asset}: {GuardWeapon.ModelItem} has no model to hold, so he holds no atgeir");
                return null;
            }

            // the model under a holder that stands for the hand joint VisEquipment would use (the item's equipoffset applied, as there)
            var holder = new GameObject(ChildName).transform;
            GameObject model = UnityEngine.Object.Instantiate(attach.gameObject, holder, false);
            model.name = attach.name;
            model.SetActive(true);
            model.transform.localPosition = offset != null ? offset.position : Vector3.zero;
            model.transform.localRotation = offset != null ? offset.rotation : Quaternion.identity;
            foreach (Collider c in model.GetComponentsInChildren<Collider>(true)) c.enabled = false;
            Transform equipped = model.transform.Find("equiped");
            if (equipped != null) equipped.gameObject.SetActive(true);
            foreach (Transform t in holder.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = root.gameObject.layer;

            // its shape in the holder's space: the box of all its meshes gives a first axis (its longest) and the blade's side (the wider of
            // the other two); the shaft's own line then comes from the biggest mesh (ShaftLine)
            Bounds shape = default;
            bool any = false;
            Renderer main = null;
            Mesh mainMesh = null;
            foreach (Renderer r in holder.GetComponentsInChildren<Renderer>(true))
            {
                Mesh m = r is SkinnedMeshRenderer sk ? sk.sharedMesh : (r.GetComponent<MeshFilter>() != null ? r.GetComponent<MeshFilter>().sharedMesh : null);
                if (m == null) continue;
                if (mainMesh == null || m.vertexCount > mainMesh.vertexCount) { main = r; mainMesh = m; }
                Matrix4x4 mm = holder.worldToLocalMatrix * r.transform.localToWorldMatrix;
                Vector3 c = m.bounds.center, e = m.bounds.extents;
                for (int k = 0; k < 8; k++)
                {
                    Vector3 p = mm.MultiplyPoint3x4(c + Vector3.Scale(e, new Vector3((k & 1) == 0 ? -1 : 1, (k & 2) == 0 ? -1 : 1, (k & 4) == 0 ? -1 : 1)));
                    if (!any) { shape = new Bounds(p, Vector3.zero); any = true; }
                    else shape.Encapsulate(p);
                }
            }
            if (!any)
            {
                UnityEngine.Object.Destroy(holder.gameObject);
                Once(asset, $"[guards] Meshy body {asset}: {GuardWeapon.ModelItem}'s model has no mesh, so he holds no atgeir");
                return null;
            }
            int a = shape.size.x >= shape.size.y && shape.size.x >= shape.size.z ? 0 : shape.size.y >= shape.size.z ? 1 : 2;
            int b = (a + 1) % 3, b2 = (a + 2) % 3;
            if (shape.size[b2] > shape.size[b]) b = b2;
            Vector3 blade = Axis(b) * (shape.max[b] >= -shape.min[b] ? 1f : -1f);
            ShaftLine(holder, main, mainMesh, Axis(a), out Vector3 head, out Vector3 near, out float buttAt, out float headAt, out string how);
            float off = Vector3.Angle(head, Axis(a) * Mathf.Sign(Vector3.Dot(head, Axis(a))));

            // holder space -> hand space: the head along the thumb, the blade along the fingers
            Quaternion turn = Quaternion.LookRotation(right.Thumb, right.Fingers) * Quaternion.Inverse(Quaternion.LookRotation(head, blade));
            holder.SetParent(right.Hand, false);
            holder.localScale = Vector3.one / right.Hand.lossyScale.x;
            holder.localRotation = turn;

            // the carry in the idle pose this body stands in now: how high the fist is, so the shaft is slid through it until its butt is just
            // off the ground (the player's grip would hold it 0.5 m up)
            var probe = new GuardHalberd(root, holder, right, left, turn, near, head, buttAt, 0f);
            Vector3 fistAt = probe.CarryWrist() + probe.CarryHand() * (right.Centre * right.Hand.lossyScale.x);
            float fistHigh = root.InverseTransformPoint(fistAt).y;
            var halberd = new GuardHalberd(root, holder, right, left, turn, near, head, buttAt, fistHigh - ButtUp);
            halberd.Place(halberd._carryGrip);

            Once(asset, $"[guards] Meshy body {asset} holds {GuardWeapon.ModelItem}: {headAt - buttAt:0.00} m long, its shaft's line from {how} {off:0} deg off its box's " +
                        $"{"xyz"[a]} (blade along {"xyz"[b]}), carried {halberd._carryGrip:0.00} m up from its butt (the player's grip {-buttAt:0.00}), struck " +
                        $"{StrikeGrip:0.00} up {(left != null ? $"with the left hand {LeftGap:0.00} below" : "ONE-HANDED (no left arm)")}; the right thumb's way in the " +
                        $"bind pose {right.BindThumb:F2}, the palm's {right.BindPalm:F2}; carried with the fist {fistHigh:0.00} m up; hand scale {right.Hand.lossyScale:F3}");
            return halberd;
        }

        private static Grip MeasureGrip(Transform root, SkinnedMeshRenderer smr, Transform[] all, string side)
        {
            Transform Bone(string name) => all.FirstOrDefault(t => t.name == name);
            Transform upper = Bone(side + "Arm"), lower = Bone(side + "ForeArm"), hand = Bone(side + "Hand");
            int iHand = Array.IndexOf(smr.bones, hand), iFore = Array.IndexOf(smr.bones, lower);
            if (upper == null || lower == null || hand == null || iHand < 0 || iFore < 0) return null;
            // the hand in its bind pose, in the NPC's space (+z the body's forward: the body is turned to face it)
            Matrix4x4 meshToRoot = root.worldToLocalMatrix * smr.transform.localToWorldMatrix;
            Matrix4x4 handBind = meshToRoot * smr.sharedMesh.bindposes[iHand].inverse, foreBind = meshToRoot * smr.sharedMesh.bindposes[iFore].inverse;
            Vector3 wrist = handBind.MultiplyPoint3x4(Vector3.zero), elbow = foreBind.MultiplyPoint3x4(Vector3.zero);
            Vector3 along = (wrist - elbow).normalized;
            Vector3 thumb = Vector3.ProjectOnPlane(Vector3.forward, along).normalized;
            Vector3 palm = Vector3.Cross(along, thumb).normalized;
            if (palm.y > 0f) palm = -palm;
            Matrix4x4 toHand = handBind.inverse;
            var g = new Grip
            {
                Upper = upper, Lower = lower, Hand = hand, BindThumb = thumb, BindPalm = palm,
                Thumb = toHand.MultiplyVector(thumb).normalized, Fingers = toHand.MultiplyVector(along).normalized,
                Centre = toHand.MultiplyPoint3x4(wrist + along * GripAlong + palm * GripIn),
            };
            g.ToFrame = Quaternion.Inverse(Quaternion.LookRotation(g.Thumb, g.Fingers));
            return g;
        }

        // The shaft's line in the holder's space: its direction toward the head (the end farther from the player's grip, the holder's
        // origin), the point of the line nearest that grip, and the butt's and the head's ends as distances along the line from that point
        private static void ShaftLine(Transform holder, Renderer main, Mesh mesh, Vector3 first, out Vector3 head, out Vector3 near,
                                      out float buttAt, out float headAt, out string how)
        {
            Matrix4x4 toHolder = holder.worldToLocalMatrix * main.transform.localToWorldMatrix;
            Vector3 axis = first, a0, a1;
            float lo = 0f, hi = 0f;
            Vector3[] vs = null;
            if (mesh.isReadable)
            {
                try { vs = mesh.vertices; }
                catch (Exception) { vs = null; }
            }
            if (vs != null && vs.Length >= 8)
            {
                for (int i = 0; i < vs.Length; i++) vs[i] = toHolder.MultiplyPoint3x4(vs[i]);
                a0 = a1 = Vector3.zero;
                // three passes: the mean of each end's vertices (the outer 8% of the length) gives the line, which gives the ends again
                for (int pass = 0; pass < 3; pass++)
                {
                    lo = float.MaxValue;
                    hi = float.MinValue;
                    foreach (Vector3 v in vs)
                    {
                        float d = Vector3.Dot(v, axis);
                        lo = Mathf.Min(lo, d);
                        hi = Mathf.Max(hi, d);
                    }
                    float cut = (hi - lo) * 0.08f;
                    Vector3 sumLo = Vector3.zero, sumHi = Vector3.zero;
                    int nLo = 0, nHi = 0;
                    foreach (Vector3 v in vs)
                    {
                        float d = Vector3.Dot(v, axis);
                        if (d <= lo + cut) { sumLo += v; nLo++; }
                        else if (d >= hi - cut) { sumHi += v; nHi++; }
                    }
                    if (nLo == 0 || nHi == 0) break;
                    a0 = sumLo / nLo;
                    a1 = sumHi / nHi;
                    if ((a1 - a0).sqrMagnitude < 1e-6f) break;
                    axis = (a1 - a0).normalized;
                }
                near = a0 - axis * Vector3.Dot(a0, axis);
                lo = float.MaxValue;
                hi = float.MinValue;
                foreach (Vector3 v in vs)
                {
                    float d = Vector3.Dot(v - near, axis);
                    lo = Mathf.Min(lo, d);
                    hi = Mathf.Max(hi, d);
                }
                how = $"its {vs.Length} vertices";
            }
            else
            {
                Bounds mb = mesh.bounds;
                int k = mb.size.x >= mb.size.y && mb.size.x >= mb.size.z ? 0 : mb.size.y >= mb.size.z ? 1 : 2;
                Vector3 end0 = toHolder.MultiplyPoint3x4(mb.center - Axis(k) * mb.extents[k]), end1 = toHolder.MultiplyPoint3x4(mb.center + Axis(k) * mb.extents[k]);
                axis = (end1 - end0).normalized;
                near = end0 - axis * Vector3.Dot(end0, axis);
                lo = Vector3.Dot(end0 - near, axis);
                hi = Vector3.Dot(end1 - near, axis);
                how = "its mesh's own box (the mesh is not readable)";
            }
            // the head is the end farther from the grip, which lies at 0 on the line
            if (hi >= -lo)
            {
                head = axis;
                buttAt = lo;
                headAt = hi;
            }
            else
            {
                head = -axis;
                buttAt = -hi;
                headAt = -lo;
            }
        }

        private static Vector3 Axis(int i) => i == 0 ? Vector3.right : i == 1 ? Vector3.up : Vector3.forward;

        private static void Once(string asset, string line)
        {
            if (s_logged.Add(asset ?? "?")) Plugin.Log.LogInfo(line);
        }

        // the shaft slid through the right fist until the fist holds it this far up from the butt
        private void Place(float fromButt) =>
            _holder.localPosition = _right.Centre - _turn * (_near + _head * (_buttAt + fromButt)) / _handScale;

        // the wrist's place in the carry, from the shoulder joint as the clip has it this frame (so he sways with his idle and his walk)
        private Vector3 CarryWrist() => _right.Upper.position - _root.up * CarryDrop + _root.forward * CarryAhead + _root.right * CarryOut;

        // the right hand's turn in the carry: thumb up (the shaft upright), fingers ahead
        private Quaternion CarryHand() => Quaternion.LookRotation(_root.up, _root.forward) * _right.ToFrame;

        // After the clips each frame: the right arm into the carry while the clips leave it free (not in a one-shot), and both hands on the
        // shaft while he strikes; each blended in and out
        internal void Carry(float dt, bool free, bool striking)
        {
            float step = dt / BlendSeconds;
            _carry = Mathf.MoveTowards(_carry, free ? 1f : 0f, step);
            _strike = Mathf.MoveTowards(_strike, striking && _left != null ? 1f : 0f, step);
            Place(Mathf.Lerp(_carryGrip, StrikeGrip, _strike));
            if (_carry > 0.001f)
            {
                Vector3 shoulder = _right.Upper.position;
                Vector3 pole = shoulder - _root.up * 0.3f - _root.forward * 0.45f + _root.right * 0.25f;   // the elbow back, down and a little out
                Pose(_right, CarryWrist(), CarryHand(), pole, _carry);
            }
            if (_strike > 0.001f)
            {
                // the left fist on the shaft below the right one, thumbs both toward the head, knuckles the same way as the right hand's
                Vector3 headW = _holder.TransformDirection(_head).normalized;
                Vector3 leftFist = _right.Hand.TransformPoint(_right.Centre) - headW * LeftGap;
                Vector3 knuckles = Vector3.ProjectOnPlane(_right.Hand.rotation * _right.Fingers, headW);
                if (knuckles.sqrMagnitude < 1e-6f) knuckles = Vector3.ProjectOnPlane(_root.forward, headW);
                Quaternion hand = Quaternion.LookRotation(headW, knuckles.normalized) * _left.ToFrame;
                Vector3 wrist = leftFist - hand * (_left.Centre * _left.Hand.lossyScale.x);
                Vector3 pole = _left.Upper.position - _root.up * 0.3f - _root.right * 0.4f - _root.forward * 0.1f;   // the left elbow down and out
                Pose(_left, wrist, hand, pole, _strike);
            }
        }

        // an arm reaching its wrist to a place with its hand turned so, blended over the clip's own pose by a weight
        private static void Pose(Grip g, Vector3 wrist, Quaternion hand, Vector3 pole, float weight)
        {
            Quaternion u0 = g.Upper.localRotation, l0 = g.Lower.localRotation, h0 = g.Hand.localRotation;
            Reach(g.Upper, g.Lower, g.Hand, wrist, pole);
            g.Hand.rotation = hand;
            if (weight < 0.999f)
            {
                g.Upper.localRotation = Quaternion.Slerp(u0, g.Upper.localRotation, weight);
                g.Lower.localRotation = Quaternion.Slerp(l0, g.Lower.localRotation, weight);
                g.Hand.localRotation = Quaternion.Slerp(h0, g.Hand.localRotation, weight);
            }
        }

        // two-bone IK: the elbow bent for the reach, the arm swung onto the target, then turned about that line until the elbow points at the pole
        private static void Reach(Transform upper, Transform lower, Transform end, Vector3 target, Vector3 pole)
        {
            Vector3 a = upper.position, b = lower.position, c = end.position;
            float la = (b - a).magnitude, lb = (c - b).magnitude;
            if (la < 1e-4f || lb < 1e-4f) return;
            float lt = Mathf.Clamp((target - a).magnitude, Mathf.Abs(la - lb) + 1e-3f, la + lb - 1e-3f);
            Vector3 axis = Vector3.Cross(a - b, c - b);
            if (axis.sqrMagnitude < 1e-10f) axis = Vector3.Cross(a - b, pole - b);
            if (axis.sqrMagnitude > 1e-10f)
            {
                float cur = Vector3.Angle(a - b, c - b);
                float want = Mathf.Acos(Mathf.Clamp((la * la + lb * lb - lt * lt) / (2f * la * lb), -1f, 1f)) * Mathf.Rad2Deg;
                lower.rotation = Quaternion.AngleAxis(want - cur, axis.normalized) * lower.rotation;
            }
            upper.rotation = Quaternion.FromToRotation(end.position - a, target - a) * upper.rotation;
            Vector3 line = (target - a).normalized;
            Vector3 e = Vector3.ProjectOnPlane(lower.position - a, line), p = Vector3.ProjectOnPlane(pole - a, line);
            if (e.sqrMagnitude > 1e-8f && p.sqrMagnitude > 1e-8f)
                upper.rotation = Quaternion.AngleAxis(Vector3.SignedAngle(e, p, line), line) * upper.rotation;
        }
    }
}
