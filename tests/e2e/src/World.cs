using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HarmonyLib;
using TerroritySmellatory;
using UnityEngine;

namespace TerrE2E
{
    /// <summary>
    /// The client-side instruments: moving the local player, spawning fixtures, asking vanilla's own questions
    /// (PrivateArea.CheckAccess, Pickable.Interact, WearNTear damage, Door, SEMan, the PvP toggle), and
    /// recording what the game SHOWED (centre messages, console lines) through observer-only postfixes.
    /// Nothing here changes a rule; every answer comes from the game after the mod had its say.
    /// </summary>
    internal static class World
    {
        internal static Player Me => Player.m_localPlayer;

        internal static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        internal static string P(Vector3 v) => $"({F(v.x)}, {F(v.y)}, {F(v.z)})";

        /// <summary>Terrain height under (x, z): the loaded terrain's collider, else the world generator's maths.</summary>
        internal static float Ground(Vector3 p)
        {
            ZoneSystem zs = ZoneSystem.instance;
            if (zs != null && zs.GetGroundHeight(p, out float h)) return h;
            return WorldGenerator.instance != null ? WorldGenerator.instance.GetHeight(p.x, p.z) : p.y;
        }

        internal static float WaterLevel => ZoneSystem.instance != null ? ZoneSystem.instance.m_waterLevel : 30f;

        /// <summary>Moves the local player to (x, z) on the ground, at once (short hops only; the zones are loaded).</summary>
        internal static void MoveTo(Vector3 xz)
        {
            Player me = Me;
            if (me == null) return;
            var p = new Vector3(xz.x, Ground(xz) + 0.3f, xz.z);
            me.transform.position = p;
            Rigidbody body = me.GetComponent<Rigidbody>();
            if (body != null)
            {
                body.position = p;
                body.linearVelocity = Vector3.zero;
            }
            me.m_maxAirAltitude = p.y;
        }

        /// <summary>Moves the player and waits long enough for Presence's 0.25 s tick to notice.</summary>
        internal static IEnumerator Go(Vector3 xz, float settle = 1.0f)
        {
            MoveTo(xz);
            yield return Flow.Frames(2);
            MoveTo(xz);
            yield return Flow.Seconds(settle);
        }

        internal static GameObject Spawn(string prefabName, Vector3 xz, float yaw = 0f)
        {
            GameObject prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefabName) : null;
            if (prefab == null) return null;
            var pos = new Vector3(xz.x, Ground(xz), xz.z);
            GameObject go = UnityEngine.Object.Instantiate(prefab, pos, Quaternion.Euler(0f, yaw, 0f));
            Piece piece = go.GetComponent<Piece>();
            if (piece != null && Me != null) piece.SetCreator(Me.GetPlayerID(), Splatform.PlatformManager.DistributionPlatform.LocalUser.PlatformUserID);
            return go;
        }

        /// <summary>
        /// vanilla's own ward question at a point. <paramref name="build"/> asks it inside the mod's BuildScope,
        /// exactly as the mod's prefixes on UpdatePlacementGhost / RemovePiece / Repair / SpawnOnHitTerrain do.
        /// </summary>
        internal static bool CheckAccess(Vector3 p, bool build, bool flash = true)
        {
            if (build) BuildScope.Enter();
            try { return PrivateArea.CheckAccess(p, 0f, flash, false); }
            finally { if (build) BuildScope.Exit(); }
        }

        internal static float Health(WearNTear wnt)
        {
            if (wnt == null || wnt.m_nview == null || !wnt.m_nview.IsValid()) return -1f;
            return wnt.m_nview.GetZDO().GetFloat(ZDOVars.s_health, wnt.m_health);
        }

        internal static HitData Hit(Vector3 at, float blunt)
        {
            var hit = new HitData();
            hit.m_damage.m_blunt = blunt;
            hit.m_point = at;
            hit.m_dir = Vector3.down;
            hit.m_toolTier = 10;
            if (Me != null) hit.SetAttacker(Me);
            return hit;
        }

        internal static int DoorState(Door door) =>
            door != null && door.m_nview != null && door.m_nview.IsValid() ? door.m_nview.GetZDO().GetInt(ZDOVars.s_state) : int.MinValue;

        internal static bool Picked(Pickable p)
        {
            if (p == null) return true;   // destroyed after its pick (no respawn): that IS picked
            if (p.m_nview == null || !p.m_nview.IsValid()) return true;
            return p.m_nview.GetZDO().GetBool(ZDOVars.s_picked, p.m_defaultPicked);
        }

        // ─────────────────────────────────────────────────────────────
        // What the game showed - observers only
        // ─────────────────────────────────────────────────────────────

        internal sealed class Shown
        {
            public float At;
            public string Text;
        }

        internal static readonly List<Shown> CenterMessages = new List<Shown>();
        internal static readonly List<Shown> ConsoleLines = new List<Shown>();

        /// <summary>One step of vanilla's chest handshake or of the inventory closing, as it happened.</summary>
        internal sealed class ContainerEvent
        {
            public int Frame;
            public string What;          // RPC_RequestOpen | OpenResponse:granted | OpenResponse:refused | Show | Hide | CloseContainer
            public Container Container;  // the chest the step was about (for Hide / CloseContainer: the one the inventory held)
            public string Detail = "";
        }

        internal static readonly List<ContainerEvent> ContainerEvents = new List<ContainerEvent>();

        internal static void InstallObservers(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(MessageHud), nameof(MessageHud.ShowMessage)),
                postfix: new HarmonyMethod(typeof(World), nameof(OnShowMessage)) { priority = Priority.Last });
            harmony.Patch(AccessTools.Method(typeof(Console), nameof(Console.Print)),
                postfix: new HarmonyMethod(typeof(World), nameof(OnConsolePrint)) { priority = Priority.Last });

            // Container.Interact opens a chest through a handshake with the chest's OWNER: RPC_RequestOpen on the
            // owner, RPC_OpenResponse(granted) back to the asker, whose InventoryGui.Show(container) then holds it
            // (and UpdateContainer marks it in use every frame the inventory shows it). Observers only: each
            // records the step and returns; none of them changes an argument or skips the original.
            harmony.Patch(AccessTools.Method(typeof(Container), nameof(Container.RPC_RequestOpen)),
                prefix: new HarmonyMethod(typeof(World), nameof(OnRequestOpen)) { priority = Priority.Last });
            harmony.Patch(AccessTools.Method(typeof(Container), nameof(Container.RPC_OpenResponse)),
                prefix: new HarmonyMethod(typeof(World), nameof(OnOpenResponse)) { priority = Priority.Last });
            harmony.Patch(AccessTools.Method(typeof(InventoryGui), nameof(InventoryGui.Show)),
                prefix: new HarmonyMethod(typeof(World), nameof(OnInventoryShow)) { priority = Priority.Last });
            harmony.Patch(AccessTools.Method(typeof(InventoryGui), nameof(InventoryGui.Hide)),
                prefix: new HarmonyMethod(typeof(World), nameof(OnInventoryHide)) { priority = Priority.Last });
            harmony.Patch(AccessTools.Method(typeof(InventoryGui), nameof(InventoryGui.CloseContainer)),
                prefix: new HarmonyMethod(typeof(World), nameof(OnCloseContainer)) { priority = Priority.Last });
        }

        private static void Record(string what, Container c, string detail = "") =>
            ContainerEvents.Add(new ContainerEvent { Frame = Time.frameCount, What = what, Container = c, Detail = detail });

        /// <summary>Who called: the first frames above the observer, for the diagnostics of an unexpected close.</summary>
        private static string Caller()
        {
            try
            {
                var st = new System.Diagnostics.StackTrace(2, false);
                var names = new List<string>();
                for (int i = 0; i < st.FrameCount && names.Count < 4; i++)
                {
                    System.Reflection.MethodBase m = st.GetFrame(i).GetMethod();
                    if (m != null) names.Add((m.DeclaringType != null ? m.DeclaringType.Name + "." : "") + m.Name);
                }
                return string.Join(" < ", names);
            }
            catch (Exception ex) { return "stack unavailable: " + ex.Message; }
        }

        private static void OnRequestOpen(Container __instance, long uid) => Record("RPC_RequestOpen", __instance, $"from {uid}");

        private static void OnOpenResponse(Container __instance, bool granted) => Record(granted ? "OpenResponse:granted" : "OpenResponse:refused", __instance);

        private static void OnInventoryShow(Container container) => Record("Show", container);

        private static void OnInventoryHide(InventoryGui __instance)
        {
            // InventoryGui.Update calls Hide every frame while there is no local player (menus, loading): record
            // only a Hide that closes something.
            if (!InventoryGui.IsVisible() && ReferenceEquals(__instance.m_currentContainer, null)) return;
            Record("Hide", __instance.m_currentContainer, $"visible={InventoryGui.IsVisible()} caller={Caller()}");
        }

        private static void OnCloseContainer(InventoryGui __instance)
        {
            Container c = __instance.m_currentContainer;
            float d = c != null && Me != null ? Vector3.Distance(c.transform.position, Me.transform.position) : -1f;
            Record("CloseContainer", c, $"player {F(d)} m from it (auto-close beyond {F(__instance.m_autoCloseDistance)} m) caller={Caller()}");
        }

        internal static string DescribeEvents(IEnumerable<ContainerEvent> events, Container mine) =>
            string.Join(", ", events.Select(e => $"f{e.Frame} {e.What}{(ReferenceEquals(e.Container, null) ? "(none)" : ReferenceEquals(e.Container, mine) ? "(this)" : "(other)")}{(e.Detail.Length > 0 ? " " + e.Detail : "")}"));

        private static void OnShowMessage(MessageHud.MessageType type, string text)
        {
            if (type != MessageHud.MessageType.Center) return;
            string shown = Localization.instance != null ? Localization.instance.Localize(text ?? "") : text;
            CenterMessages.Add(new Shown { At = Time.realtimeSinceStartup, Text = shown });
        }

        private static void OnConsolePrint(string text) =>
            ConsoleLines.Add(new Shown { At = Time.realtimeSinceStartup, Text = text ?? "" });

        /// <summary>The centre text the HUD holds right now (what a player would be reading).</summary>
        internal static string CenterNow =>
            MessageHud.instance != null && MessageHud.instance.m_messageCenterText != null ? MessageHud.instance.m_messageCenterText.text ?? "" : "";

        internal static List<string> CenterSince(float t) => CenterMessages.Where(m => m.At >= t).Select(m => m.Text).ToList();

        internal static List<string> ConsoleSince(float t) => ConsoleLines.Where(m => m.At >= t).Select(m => m.Text).ToList();

        internal static string Loc(string token) => Localization.instance != null ? Localization.instance.Localize(token) : token;

        /// <summary>
        /// The banner in vanilla's biome-found slot (MessageHud.m_biomeMsgInstance) as a player reads it: the text of its
        /// "Title" and, if the mod added one, of its "TS_EntryText" subtitle - and, from 0.4.0, its PvP notch. 0.2.0's Epic entry
        /// banner lives here, so a banner of vanilla's own (a biome, a location) shows up with its own title. Null when the slot is empty.
        /// </summary>
        internal sealed class BannerView
        {
            public GameObject Go;
            public string Title;
            public string Subtitle;

            /// <summary>0.4.0: the PvP notch, the child named TS_PvPNotch, while the banner is up. Null when the banner carries none.</summary>
            public Transform Notch;

            /// <summary>Whether it carried a notch when seen. Unlike <see cref="Notch"/> this outlives the banner (see <see cref="BannersSeen"/>).</summary>
            public bool HasNotch;

            /// <summary>The colour the notch SHOWS: that of the last-drawn Image under it, the gem over its rim. Null without a notch or an Image.</summary>
            public Color? NotchColour;

            public string NotchText =>
                !HasNotch ? "(none)" : NotchColour.HasValue ? $"#{ColorUtility.ToHtmlStringRGB(NotchColour.Value)} ({HueOf(NotchColour.Value)})" : "(no image)";

            public override string ToString() => $"title '{Title}' subtitle '{Subtitle ?? "(none)"}' notch {NotchText}";
        }

        /// <summary>
        /// The last banner <see cref="BannerNow"/> saw under each title, as plain facts that outlive the banner. A scenario that only
        /// POLLS for a banner's title (the combine phase waits for TS Comb A's) can then be asked later, from another step, what that
        /// banner showed: the 0.4.0 notch's negative control reads the banner the combine phase waited for.
        /// </summary>
        internal static readonly Dictionary<string, BannerView> BannersSeen = new Dictionary<string, BannerView>();

        internal static BannerView BannerNow()
        {
            MessageHud hud = MessageHud.instance;
            GameObject go = hud != null ? hud.m_biomeMsgInstance : null;
            if (go == null) return null;
            var view = new BannerView { Go = go };
            foreach (TMPro.TMP_Text text in go.GetComponentsInChildren<TMPro.TMP_Text>(true))
            {
                string n = text.gameObject.name;
                if (n == "Title" && view.Title == null) view.Title = text.text ?? "";
                else if (n == "TS_EntryText" && view.Subtitle == null) view.Subtitle = text.text ?? "";
            }
            view.Notch = FindIn(go, "TS_PvPNotch");
            if (view.Notch != null)
            {
                view.HasNotch = true;
                UnityEngine.UI.Image[] images = view.Notch.GetComponentsInChildren<UnityEngine.UI.Image>(true);
                if (images.Length > 0) view.NotchColour = images[images.Length - 1].color;   // drawn last = on top: the gem over its rim
            }
            if (view.Title != null) BannersSeen[view.Title] = view;
            return view;
        }

        /// <summary>The first object called <paramref name="name"/> at or under <paramref name="root"/>, inactive ones included; null if none.</summary>
        internal static Transform FindIn(GameObject root, string name)
        {
            if (root == null) return null;
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                if (t.name == name) return t;
            return null;
        }

        private static readonly Type NotchType = typeof(Territory).Assembly.GetType("TerroritySmellatory.BannerNotch");

        /// <summary>
        /// 0.4.0: the DLL under test draws a PvP notch on its banners and colours the centre message's PvP line (it has the BannerNotch
        /// class). Found by name, as Features.cs finds every member newer than 0.1.1, because this driver is compiled against whichever
        /// DLL run-e2e.sh is given.
        /// </summary>
        internal static bool NotchSupported => NotchType != null;

        /// <summary>
        /// The hue class of a colour: "green" or "red" for a clearly saturated one of that hue, else "other" (the amber of the braids
        /// is "other"). The checks judge the notch by hue, never by the mod's exact tint, so a retuned tint does not break the rig and
        /// a swapped one does.
        /// </summary>
        internal static string HueOf(Color c)
        {
            Color.RGBToHSV(c, out float h, out float s, out float v);
            float deg = h * 360f;
            if (s < 0.5f || v < 0.4f) return "other";
            if (deg < 15f || deg > 345f) return "red";
            if (deg > 90f && deg < 150f) return "green";
            return "other";
        }

        /// <summary><see cref="HueOf(Color)"/> of an #RRGGBB (or RRGGBB) string, "other" if it is not one.</summary>
        internal static string HueOfHex(string rrggbb) =>
            ColorUtility.TryParseHtmlString(rrggbb.StartsWith("#") ? rrggbb : "#" + rrggbb, out Color c) ? HueOf(c) : "other";

        internal static void Run(string command)
        {
            Report.Info($"console> {command}");
            Console.instance.TryRunCommand(command);
        }

        // ─────────────────────────────────────────────────────────────
        // The mod's view (read, never written)
        // ─────────────────────────────────────────────────────────────

        internal static Territory ByName(string name) => TerritoryIndex.All.FirstOrDefault(t => t.Name == name);

        internal static string Describe(Territory t) =>
            t == null ? "null" :
            $"#{t.Id} '{t.Name}' {t.Kind} c=({F(t.X)},{F(t.Z)}) half=({F(t.HalfX)},{F(t.HalfZ)}) yaw={F(t.Yaw)} flags={t.Flags} pvp={t.PvP} members=[{string.Join(",", t.Members.Select(m => m.Name + ":" + m.Id))}] rev={t.Revision}";

        /// <summary>A point in a territory's own frame (x along its width, z along its depth), in world space.</summary>
        internal static Vector3 Local(Territory t, float lx, float lz)
        {
            Vector3 d = t.Kind == TerritoryShapeKind.Circle ? new Vector3(lx, 0f, lz) : Quaternion.Euler(0f, t.Yaw, 0f) * new Vector3(lx, 0f, lz);
            return new Vector3(t.X + d.x, 0f, t.Z + d.z);
        }
    }
}
