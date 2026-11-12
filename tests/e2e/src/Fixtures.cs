using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TerroritySmellatory;
using UnityEngine;

namespace TerrE2E
{
    /// <summary>
    /// Real game objects spawned for a check and asked vanilla's own questions: a pickable, a wooden chest
    /// (WearNTear + Container), a door. Each refusal test has a control in open country, so a fixture that does
    /// not work headless is reported as SOFT instead of passing a refusal it never tested.
    /// </summary>
    internal static class Fixtures
    {
        /// <summary>The role's own spawn point: outside every territory by construction of the layout.</summary>
        internal static Vector3 Home;

        /// <summary>A point near Home that no territory governs, at least 8 m from every edge.</summary>
        internal static Vector3 OpenGround(float side = 0f)
        {
            Vector3 origin = Home == Vector3.zero && World.Me != null ? World.Me.transform.position : Home;
            for (int ring = 0; ring < 6; ring++)
            {
                for (int k = 0; k < 8; k++)
                {
                    float a = (k * 45f + side) * Mathf.Deg2Rad;
                    float r = 4f + ring * 3f;
                    var p = new Vector3(origin.x + Mathf.Sin(a) * r, 0f, origin.z + Mathf.Cos(a) * r);
                    if (TerritoryIndex.GoverningAt(p) != null) continue;
                    TerritoryIndex.Nearest(p, out float dist);
                    if (dist < 8f) continue;
                    if (World.Ground(p) < World.WaterLevel + 0.3f) continue;
                    return p;
                }
            }
            return origin;
        }

        internal static void Destroy(GameObject go)
        {
            if (go == null) return;
            ZNetView nv = go.GetComponent<ZNetView>();
            if (nv != null && nv.IsValid() && ZNetScene.instance != null) ZNetScene.instance.Destroy(go);
            else Object.Destroy(go);
        }

        // ─────────────────────────────────────────────────────────────
        // Pickable.Interact
        // ─────────────────────────────────────────────────────────────

        /// <param name="ready">set true when the pickable was valid and NOT yet picked before Interact: only then can a
        /// "not picked" outcome be a refusal (vanilla's Interact also returns false for an invalid or disabled pickable).</param>
        internal static IEnumerator Pick(Vector3 at, Box<string> outcome, Box<bool> picked, Box<bool> returned, Box<bool> ready = null)
        {
            GameObject go = World.Spawn("RaspberryBush", at) ?? World.Spawn("Pickable_Mushroom", at);
            if (go == null)
            {
                outcome.Value = "no pickable prefab";
                yield break;
            }
            yield return Flow.Frames(3);
            Pickable p = go.GetComponent<Pickable>();
            bool before = World.Picked(p);
            if (ready != null) ready.Value = !before;
            returned.Value = p.Interact(World.Me, false, false);
            yield return Flow.Seconds(1f);
            picked.Value = !before && World.Picked(p);
            outcome.Value = $"{go.name}: picked before={before} Interact returned {returned.Value}, picked after={World.Picked(p)}";
            Destroy(p != null ? p.gameObject : null);
        }

        internal static IEnumerator PickTest(string id, string what, Vector3 at, bool expectPicked)
        {
            var outcome = new Box<string>();
            var picked = new Box<bool>();
            var returned = new Box<bool>();
            yield return Pick(at, outcome, picked, returned);
            if (outcome.Value == "no pickable prefab")
            {
                Report.Fail(id, what, outcome.Value);
                yield break;
            }
            Report.Check(id, what, picked.Value == expectPicked, outcome.Value);
        }

        /// <summary>
        /// A refusal inside, with a control outside that must succeed for the refusal to count. The control is known
        /// to pick headless, so a control that does not pick is a FAIL (the refusal is unverified), not a SOFT: a
        /// SOFT would let a run go green with the protection never tested. The pickable inside must have been a valid,
        /// unpicked pickable, so that "not picked" can only be the refusal.
        /// </summary>
        internal static IEnumerator PickRefusedWithControl(string id, string what, Vector3 inside)
        {
            var o1 = new Box<string>(); var p1 = new Box<bool>(); var r1 = new Box<bool>();
            yield return Pick(OpenGround(20f), o1, p1, r1);
            var o2 = new Box<string>(); var p2 = new Box<bool>(); var r2 = new Box<bool>(); var ready2 = new Box<bool>();
            yield return Pick(inside, o2, p2, r2, ready2);
            if (!p1.Value)
            {
                Report.Fail(id, what, $"the control pick in open country did not pick ({o1.Value}): the fixture or the game is broken, so the refusal inside is unverified; inside: {o2.Value}");
                yield break;
            }
            Report.Check(id, what, ready2.Value && !p2.Value && !r2.Value,
                $"inside: {o2.Value}{(ready2.Value ? "" : " (the pickable inside was not a valid, unpicked pickable, so 'not picked' proves nothing)")}; control: {o1.Value}");
        }

        // ─────────────────────────────────────────────────────────────
        // WearNTear damage (a player's hit, through WearNTear.Damage -> RPC_Damage on the owner)
        // ─────────────────────────────────────────────────────────────

        internal static IEnumerator Chest(Vector3 at, Box<WearNTear> result)
        {
            GameObject go = World.Spawn("piece_chest_wood", at);
            yield return Flow.Frames(5);
            result.Value = go != null ? go.GetComponent<WearNTear>() : null;
        }

        internal static IEnumerator Hit(WearNTear w, float blunt, Box<float> before, Box<float> after)
        {
            before.Value = World.Health(w);
            if (w != null) w.Damage(World.Hit(w.transform.position + Vector3.up * 0.4f, blunt));
            yield return Flow.Seconds(0.6f);
            after.Value = World.Health(w);
        }

        internal static IEnumerator DamageTest(string id, string what, Vector3 at, bool expectDamage, Box<WearNTear> keep = null)
        {
            var w = new Box<WearNTear>();
            yield return Chest(at, w);
            if (w.Value == null)
            {
                Report.Fail(id, what, "could not spawn piece_chest_wood");
                yield break;
            }
            var b = new Box<float>(); var a = new Box<float>();
            yield return Hit(w.Value, 20f, b, a);
            bool damaged = a.Value < b.Value - 0.01f;
            // World.Health answers -1 for a piece whose ZDO is not valid: "-1 -> -1" is no damage, and would pass a
            // "no damage" check without any hit having been tested.
            bool readable = b.Value > 0f && a.Value >= 0f;
            Report.Check(id, $"{what} (health {World.F(b.Value)} -> {World.F(a.Value)})", readable && damaged == expectDamage,
                $"{(readable ? "" : "the piece's health could not be read (fixture broken), so nothing was tested; ")}expected {(expectDamage ? "damage" : "no damage")}, governed by {World.Describe(TerritoryIndex.GoverningAt(w.Value.transform.position))}");
            if (keep != null) keep.Value = w.Value;
            else Destroy(w.Value.gameObject);
        }

        // ─────────────────────────────────────────────────────────────
        // Door.Interact (the ward's "access" side, with a real door)
        // ─────────────────────────────────────────────────────────────

        internal static IEnumerator Door(Vector3 at, Box<string> outcome, Box<bool> opened, Box<bool> interactable, Box<bool> returned = null)
        {
            GameObject go = World.Spawn("wood_door", at);
            if (go == null)
            {
                outcome.Value = "no wood_door prefab";
                yield break;
            }
            yield return Flow.Seconds(1f);
            Door door = go.GetComponent<Door>();
            int before = World.DoorState(door);
            interactable.Value = door.CanInteract();
            bool r = door.Interact(World.Me, false, false);
            if (returned != null) returned.Value = r;
            yield return Flow.Seconds(0.8f);
            int after = World.DoorState(door);
            opened.Value = before == 0 && after != 0;
            outcome.Value = $"state {before} -> {after}, CanInteract={interactable.Value}, Interact returned {r}";
            Destroy(go);
        }

        /// <remarks>
        /// A door the member must open (<paramref name="expectOpen"/>) passes or fails on its own; the control only
        /// adds evidence. A REFUSAL needs the control: the control door is known to open headless, so a control that
        /// stays shut is a FAIL (the refusal is unverified), not a SOFT. And the refusal must come from the ward: a
        /// keyless door's Interact returns false when the door cannot be interacted with (CanInteract, the animator)
        /// and true when the ward check refuses it - so a refused door must have been interactable, have returned
        /// true, and stayed shut.
        /// </remarks>
        internal static IEnumerator DoorWithControl(string id, string what, Vector3 inside, bool expectOpen)
        {
            var o1 = new Box<string>(); var op1 = new Box<bool>(); var i1 = new Box<bool>();
            yield return Door(OpenGround(40f), o1, op1, i1);
            var o2 = new Box<string>(); var op2 = new Box<bool>(); var i2 = new Box<bool>(); var r2 = new Box<bool>();
            yield return Door(inside, o2, op2, i2, r2);
            if (expectOpen)
            {
                Report.Check(id, what, op2.Value, $"inside: {o2.Value}; control: {o1.Value}");
                yield break;
            }
            if (!op1.Value)
            {
                Report.Fail(id, what, $"the control door in open country did not open ({o1.Value}), so the refusal inside is unverified; inside: {o2.Value}");
                yield break;
            }
            Report.Check(id, what, !op2.Value && i2.Value && r2.Value,
                $"inside: {o2.Value}{(i2.Value && r2.Value ? "" : " (not the ward's refusal: the door was not interactable or Interact returned false)")}; control: {o1.Value}");
        }

        // ─────────────────────────────────────────────────────────────
        // Container.Interact (a chest: opens in the inventory only if the ward check allows it)
        // ─────────────────────────────────────────────────────────────

        /// <remarks>
        /// What vanilla's Container.Interact changes when it opens a chest, read the moment it returns and three
        /// frames later: its handshake with the chest's owner ran (RPC_RequestOpen, then RPC_OpenResponse granted),
        /// InventoryGui.Show(chest) was called and the inventory holds the chest (m_currentContainer), and
        /// InventoryGui.UpdateContainer then marks the chest in use (Container.IsInUse, the ZDO's inUse that opens
        /// the lid for every player). A chest spawned by this client is owned by it, so the whole handshake runs
        /// inside Interact (ZRoutedRpc handles an RPC to its own peer at once). A ward refusal returns from
        /// Interact before the handshake: none of these happen. Interact's return value says nothing - it is
        /// true for both.
        /// </remarks>
        /// <param name="anySignal">set true when ANY part of an open happened (a handshake step for this chest, Show, the
        /// inventory holding it at any of the three reads, in use by either measure): a refusal must show none.</param>
        /// <param name="distance">the player's distance from the chest when Interact ran.</param>
        internal static IEnumerator ChestOpen(Vector3 at, Box<string> outcome, Box<bool> opened, Box<bool> anySignal = null, Box<float> distance = null)
        {
            InventoryGui gui = InventoryGui.instance;
            if (gui == null)
            {
                outcome.Value = "no InventoryGui";
                yield break;
            }
            if (InventoryGui.IsVisible() || gui.m_currentContainer != null)
            {
                gui.Hide();   // start from a closed inventory
                yield return Flow.Frames(2);
            }
            GameObject go = World.Spawn("piece_chest_wood", at);
            yield return Flow.Frames(5);
            Container c = go != null ? go.GetComponent<Container>() : null;
            if (c == null)
            {
                outcome.Value = "no container";
                yield break;
            }
            Player me = World.Me;
            bool owner = c.m_nview != null && c.m_nview.IsValid() && c.m_nview.IsOwner();
            float d0 = Vector3.Distance(c.transform.position, me.transform.position);
            int mark = World.ContainerEvents.Count;

            bool r = c.Interact(me, false, false);

            List<World.ContainerEvent> during = World.ContainerEvents.Skip(mark).ToList();
            bool requested = during.Any(e => e.What == "RPC_RequestOpen" && ReferenceEquals(e.Container, c));
            bool granted = during.Any(e => e.What == "OpenResponse:granted" && ReferenceEquals(e.Container, c));
            bool shown = during.Any(e => e.What == "Show" && ReferenceEquals(e.Container, c));
            bool holdsRef = ReferenceEquals(gui.m_currentContainer, c);
            bool holds = holdsRef && gui.IsContainerOpen();
            yield return Flow.Frames(3);
            bool inUse = c != null && c.IsInUse();
            int zdoInUse = c != null && c.m_nview != null && c.m_nview.IsValid() ? c.m_nview.GetZDO().GetInt(ZDOVars.s_inUse) : -1;
            bool holds3 = ReferenceEquals(gui.m_currentContainer, c);
            float d1 = c != null ? Vector3.Distance(c.transform.position, World.Me.transform.position) : -1f;
            yield return Flow.Seconds(1f);
            bool holds1s = ReferenceEquals(gui.m_currentContainer, c);
            float d2 = c != null ? Vector3.Distance(c.transform.position, World.Me.transform.position) : -1f;
            string events = World.DescribeEvents(World.ContainerEvents.Skip(mark), c);

            opened.Value = requested && granted && shown && holds && inUse;
            // Any handshake step or Show for this chest at any time in the second after Interact, the inventory holding
            // it at any of the three reads, or the chest in use by either measure: part of an open happened.
            bool anyHandshake = World.ContainerEvents.Skip(mark).Any(e => ReferenceEquals(e.Container, c) &&
                                    (e.What == "RPC_RequestOpen" || e.What.StartsWith("OpenResponse", System.StringComparison.Ordinal) || e.What == "Show"));
            if (anySignal != null) anySignal.Value = anyHandshake || holdsRef || holds3 || holds1s || inUse || zdoInUse > 0;
            if (distance != null) distance.Value = d0;
            outcome.Value = $"Interact returned {r}; handshake: RPC_RequestOpen={requested} granted={granted} InventoryGui.Show(chest)={shown}; " +
                            $"inventory holds it: at once {holds}, after 3 frames {holds3}, after 1 s {holds1s}; in use after 3 frames {inUse} (ZDO inUse={zdoInUse}); " +
                            $"chest owned by this client: {owner}; chest at {World.P(c.transform.position)}, player {World.F(d0)} m away (then {World.F(d1)} m, {World.F(d2)} m); events: [{events}]";
            if (InventoryGui.IsVisible() || gui.m_currentContainer != null) gui.Hide();
            yield return Flow.Frames(2);
            Destroy(go);
        }

        /// <remarks>
        /// The inventory closes a container more than 4 m from the player (InventoryGui.m_autoCloseDistance), so
        /// the player walks to the control chest and back; the inside chest must be within 4 m of where it stands.
        /// A chest the member must open (<paramref name="expectOpen"/>) passes only by every signal and fails
        /// otherwise; the control only adds evidence. A REFUSAL passes only if NONE of the open happened, the player
        /// stood within the auto-close distance (the control's conditions), and the control opened by every signal.
        /// The control is known to open headless, so a control that does not open is a FAIL (the refusal is
        /// unverified), not a SOFT.
        /// </remarks>
        internal static IEnumerator ChestWithControl(string id, string what, Vector3 inside, bool expectOpen)
        {
            Vector3 back = World.Me.transform.position;
            Vector3 control = OpenGround(60f);
            yield return World.Go(control, 0.8f);
            string where = $"control walk: asked for {World.P(control)}, player at {World.P(World.Me.transform.position)}";
            var o1 = new Box<string>(); var op1 = new Box<bool>();
            yield return ChestOpen(control + new Vector3(1.5f, 0f, 1f), o1, op1);
            yield return World.Go(back, 0.8f);
            var o2 = new Box<string>(); var op2 = new Box<bool>(); var any2 = new Box<bool>(); var d2 = new Box<float>(-1f);
            yield return ChestOpen(inside, o2, op2, any2, d2);
            Report.Info($"{id} evidence - control ({where}): {o1.Value} || inside: {o2.Value}");
            if (expectOpen)
            {
                Report.Check(id, what, op2.Value, $"inside: {o2.Value}; control: {o1.Value}");
                yield break;
            }
            if (!op1.Value)
            {
                Report.Fail(id, what, $"the control chest in open country did not open by every signal, so the refusal inside is unverified ({where}; control: {o1.Value}); inside: {o2.Value}");
                yield break;
            }
            float reach = InventoryGui.instance != null ? InventoryGui.instance.m_autoCloseDistance : 4f;
            bool near = d2.Value >= 0f && d2.Value < reach;
            Report.Check(id, what, !any2.Value && near,
                $"inside: {o2.Value}{(any2.Value ? " (part of an open happened: a refusal must show none of it)" : "")}" +
                $"{(near ? "" : $" (the player stood {World.F(d2.Value)} m from the chest, not within the {World.F(reach)} m the control was tested at)")}; control: {o1.Value}");
        }

        /// <summary>Every piece_chest_wood near a point (to find a fixture again after a solo re-login).</summary>
        internal static WearNTear FindPieceNear(Vector3 p, float r)
        {
            return Object.FindObjectsByType<WearNTear>(FindObjectsSortMode.None)
                .Where(w => w != null && w.name.StartsWith("piece_chest_wood") && Utils.DistanceXZ(w.transform.position, p) < r)
                .OrderBy(w => Utils.DistanceXZ(w.transform.position, p)).FirstOrDefault();
        }
    }
}
