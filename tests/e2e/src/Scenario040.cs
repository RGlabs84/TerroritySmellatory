using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.IO;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using TerroritySmellatory;
using UnityEngine;
using UnityEngine.Rendering;

namespace TerrE2E
{
    /// <summary>
    /// The 0.4.0 phase: SUB-TERRITORIES (Core/Inheritance.cs) - a territory inside another that starts as a copy of its settings,
    /// follows them, and differs only where it was told to. Gated by reflection (<see cref="Supported"/>: Territory has a parent
    /// and the two follow marks); on a DLL without them there is one INFO line and nothing else. The new members are reached by name,
    /// as Features.cs reaches every member newer than 0.1.1, because the driver is compiled against whichever DLL run-e2e.sh is given.
    ///
    /// TSAdmin, at the end of its phase 2, marks LAND (r=14: build and access protection, no raids, a banner, on the map, PvP forced
    /// ON, its own colour, entry text "Land of {name}") through the panel's draft as a TOP-LEVEL territory, then TOWN (r=4, six metres
    /// along x, inside it) with the draft's parent left on AUTOMATIC and every setting FOLLOWING except two it sets itself: building
    /// off and PvP forced OFF. The server has to pick LAND as its parent, store only those two, and send everything else as LAND's.
    /// Then LAND is edited (harvest protection on, a new entry text): TOWN has to follow both through the snapshot with its own record
    /// untouched (revision 1).
    ///   A40a LAND created.   A40b TOWN created inside it: parent picked by the server, marks kept, followed values filled in.
    ///   A40c LAND edited, and TOWN follows it (admin.sub then wakes the server's S40a / S40b and TSPlain's P40).
    /// TSPlain (solo mode: the same game, later) as a visitor:
    ///   P40a TOWN in its snapshot with the same marks and the same effective values;
    ///   P40b the ward funnel (PrivateArea.CheckAccess): in TOWN doors are refused (followed from LAND) but building is allowed (its
    ///        own), in LAND outside TOWN both are refused;
    ///   P40c the PvP rule held and the Epic banner on both: TOWN forced off with a green notch and LAND's entry text filled in with
    ///        TOWN's own name, LAND forced on with a red notch.
    /// TSAdmin, in phase 3:
    ///   A40d TOWN follows building again (its mark back, no value of its own);
    ///   A40e LAND deleted: TOWN keeps every rule and moves to the top level, its revision up by one (S40c checks the server's store
    ///        and file for the record that was baked);
    ///   A40f a second pair marked (LAND 2 and TOWN 2), A40g deleted in ONE request (DeleteTree);
    ///   A40h a third land with a town inside it combined INTO a small territory: the town keeps its rules and moves inside the small one.
    /// The server restart (R40) loads TOWN back as the top-level territory it ended as.
    /// </summary>
    internal static class Scenario040
    {
        private static float T => E2EEnv.StepTimeout;

        // ── the DLL under test, by reflection ──────────────────────────────────────────────────────────────

        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly Assembly Mod = typeof(Territory).Assembly;
        private static readonly FieldInfo ParentField = typeof(Territory).GetField("ParentId", Any);
        private static readonly FieldInfo FollowFlagsField = typeof(Territory).GetField("FollowFlags", Any);
        private static readonly FieldInfo FollowField = typeof(Territory).GetField("Follow", Any);
        private static readonly FieldInfo AutoParentField = typeof(Territory).GetField("AutoParent", Any);
        private static readonly Type FollowType = Mod.GetType("TerroritySmellatory.TerritoryFollow");

        /// <summary>Territory has a parent and both follow marks, and RequestOp has DeleteTree (0.4.0).</summary>
        internal static bool Supported =>
            ParentField != null && FollowFlagsField != null && FollowField != null && FollowType != null &&
            Enum.GetNames(typeof(RequestOp)).Contains("DeleteTree");

        internal static int Parent(Territory t) => ParentField == null || t == null ? 0 : (int)ParentField.GetValue(t);

        internal static TerritoryFlags FollowFlags(Territory t) => FollowFlagsField == null || t == null ? TerritoryFlags.None : (TerritoryFlags)FollowFlagsField.GetValue(t);

        internal static int FollowBits(Territory t) => FollowField == null || t == null ? 0 : Convert.ToInt32(FollowField.GetValue(t));

        internal static void SetParent(Territory t, int id) => ParentField?.SetValue(t, id);

        internal static void SetFollowFlags(Territory t, TerritoryFlags flags) => FollowFlagsField?.SetValue(t, flags);

        internal static void SetFollowBits(Territory t, int bits) => FollowField?.SetValue(t, Enum.ToObject(FollowType, bits));

        /// <summary>A draft that is TOP LEVEL, as the panel's controls make one when its Inside row says Nothing: every territory the older phases mark.</summary>
        internal static void TopLevel(Territory draft)
        {
            if (!Supported) return;
            SetParent(draft, 0);
            SetFollowFlags(draft, TerritoryFlags.None);
            SetFollowBits(draft, 0);
        }

        // Read at run time, from the DLL under test: a const compared with a literal is a compile-time answer (and a warning).
        private static readonly int RecordVersion = Territory.RecordVersion;
        private static readonly int FileVersion = TerritoryJson.FileVersion;

        /// <summary>The gilt a territory has when nothing sets its colour (Territory.DefaultColor, 0.4.0; the same colour since 0.1.0).</summary>
        private const string DefaultColourHex = "#D6AA48";

        private static RequestOp DeleteTree => (RequestOp)Enum.Parse(typeof(RequestOp), "DeleteTree");

        /// <summary>The contract's numbers: TerritoryFollow's bits, as the wire and the file's masks carry them.</summary>
        private static readonly KeyValuePair<string, int>[] FollowContract =
        {
            new KeyValuePair<string, int>("PvP", 1), new KeyValuePair<string, int>("Priority", 2), new KeyValuePair<string, int>("People", 4),
            new KeyValuePair<string, int>("SanctuaryValues", 8), new KeyValuePair<string, int>("EnterText", 16),
            new KeyValuePair<string, int>("LeaveText", 32), new KeyValuePair<string, int>("Color", 64), new KeyValuePair<string, int>("All", 127)
        };

        internal const int FollowPvP = 1;
        internal const int FollowAll = 127;
        internal const int AutoParent = -1;

        private static TerritoryFlags AllFlags => Enum.GetValues(typeof(TerritoryFlags)).Cast<TerritoryFlags>().Aggregate(TerritoryFlags.None, (a, f) => a | f);

        internal static List<string> ContractProblems()
        {
            var problems = new List<string>();
            if (!Supported) return problems;
            foreach (KeyValuePair<string, int> c in FollowContract)
            {
                if (!Enum.GetNames(FollowType).Contains(c.Key)) { problems.Add($"TerritoryFollow has no {c.Key}"); continue; }
                int got = Convert.ToInt32(Enum.Parse(FollowType, c.Key));
                if (got != c.Value) problems.Add($"TerritoryFollow.{c.Key} is {got}, the contract says {c.Value}");
            }
            if (AutoParentField == null || Convert.ToInt32(AutoParentField.GetRawConstantValue()) != AutoParent) problems.Add($"Territory.AutoParent is not {AutoParent}");
            if (RecordVersion != 3) problems.Add($"RecordVersion is {RecordVersion}, the contract says 3");
            if (FileVersion != 5) problems.Add($"the file version is {FileVersion}, the contract says 5");
            if ((int)DeleteTree != 11) problems.Add($"RequestOp.DeleteTree is {(int)DeleteTree}, the contract says 11");
            return problems;
        }

        // ── a list in pieces (Net/SnapshotParts.cs) ─────────────────────────────────────────────────────────

        private static readonly Type ProtocolType = Mod.GetType("TerroritySmellatory.Protocol");
        private static readonly FieldInfo PartBytesField = ProtocolType?.GetField("PartBytes", Any);
        private static readonly PropertyInfo PartedSendsProp = typeof(TerritoryServer).GetProperty("PartedSends", Any);
        private static readonly PropertyInfo PartedSnapshotsProp = typeof(TerritoryClient).GetProperty("PartedSnapshots", Any);

        /// <summary>This DLL sends a territory list that is too big for one message in pieces (0.4.0), and counts them.</summary>
        internal static bool PartsSupported => PartBytesField != null && PartedSendsProp != null && PartedSnapshotsProp != null;

        /// <summary>
        /// The size of a piece on the rig's server: half a kilobyte, so the test world's list goes in pieces from its second territory
        /// (the STORED records are small: twelve territories, three of them sub-territories, are about 2 KiB) and every older phase of
        /// the run is carried over them. The real size is 64 KiB (Protocol.PartBytes).
        /// </summary>
        internal const int RigPartBytes = 512;

        /// <summary>Called by the server driver as it starts.</summary>
        internal static void SmallParts()
        {
            if (!PartsSupported)
            {
                Report.Info("0.4.0 pieces skipped: this DLL has no SnapshotParts");
                return;
            }
            PartBytesField.SetValue(null, RigPartBytes);
            Report.Info($"server: a territory list over {RigPartBytes} bytes goes to each player in pieces of {RigPartBytes} bytes (test setting: the real size is {PartBytesDefault / 1024} KiB)");
        }

        private static readonly int PartBytesDefault = PartBytesField != null ? (int)PartBytesField.GetValue(null) : 0;

        /// <summary>S41a: the server sent at least one list in pieces.</summary>
        internal static void ServerParts()
        {
            if (!PartsSupported) return;
            int sends = (int)PartedSendsProp.GetValue(null);
            Report.Check("S41a-parts-sent", $"with pieces of {RigPartBytes} bytes the server sent territory lists in pieces ({sends} this session)", sends >= 1, $"PartedSends {sends}");
        }

        /// <summary>P41a: TSPlain put lists back together from pieces, and what they carried is in its index.</summary>
        private static void PlainParts()
        {
            if (!PartsSupported) return;
            int got = (int)PartedSnapshotsProp.GetValue(null);
            bool carried = World.ByName(LandName) != null && World.ByName(TownName) != null;
            Report.Check("P41a-parts-received", $"TSPlain put {got} territory list(s) back together from pieces, and the land and the town they carried are in its index", got >= 1 && carried,
                $"PartedSnapshots {got}, land and town in the index: {carried}");
        }

        // ── the territories ────────────────────────────────────────────────────────────────────────────────

        internal const string LandName = "TS Sub Land";
        internal const string TownName = "TS Sub Town";
        internal const string Land2Name = "TS Sub Land 2";
        internal const string Town2Name = "TS Sub Town 2";
        internal const string LandText = "Land of {name}";
        internal const string RealmText = "Realm of {name}";

        private const TerritoryFlags LandFlags = TerritoryFlags.BuildProtect | TerritoryFlags.AccessProtect | TerritoryFlags.NoRaids |
                                                 TerritoryFlags.Announce | TerritoryFlags.ShowOnMap | TerritoryFlags.ShowBorder;

        private static Spec MakeLand(string key, string name) => new Spec
        {
            Key = key,
            Name = name,
            Kind = TerritoryShapeKind.Circle,
            HalfX = 14f,
            HalfZ = 14f,
            Flags = LandFlags,
            PvP = PvPRule.ForcedOn,
            EnterMessage = LandText,
            Color = new Color32(210, 120, 40, 255)
        };

        /// <summary>The town sets only what it names: no rules of its own at all (so building is OFF), PvP forced off. Its colour here is never used: it follows LAND's.</summary>
        private static Spec MakeTown(string key, string name) => new Spec
        {
            Key = key,
            Name = name,
            Kind = TerritoryShapeKind.Circle,
            HalfX = 4f,
            HalfZ = 4f,
            Flags = TerritoryFlags.None,
            PvP = PvPRule.ForcedOff,
            Color = new Color32(60, 60, 200, 255)
        };

        internal const string Land3Name = "TS Sub Land 3";
        internal const string Town3Name = "TS Sub Town 3";
        internal const string Land4Name = "TS Sub Land 4";

        private static readonly Spec Land3 = MakeLand("subland3", Land3Name);
        private static readonly Spec Town3 = MakeTown("subtown3", Town3Name);

        /// <summary>A small top-level territory to combine the third land into.</summary>
        private static readonly Spec Land4 = new Spec
        {
            Key = "subland4",
            Name = Land4Name,
            Kind = TerritoryShapeKind.Circle,
            HalfX = 10f,
            HalfZ = 10f,
            Flags = TerritoryFlags.NoRaids | TerritoryFlags.Announce,
            PvP = PvPRule.Unchanged,
            Color = new Color32(90, 160, 90, 255)
        };

        private static readonly Spec Land = MakeLand("subland", LandName);
        private static readonly Spec Town = MakeTown("subtown", TownName);
        private static readonly Spec Land2 = MakeLand("subland2", Land2Name);
        private static readonly Spec Town2 = MakeTown("subtown2", Town2Name);

        private static JObject V(Vector3 v) => new JObject { ["x"] = v.x, ["y"] = v.y, ["z"] = v.z };

        private static string[] FlagNames(TerritoryFlags f) =>
            Enum.GetValues(typeof(TerritoryFlags)).Cast<TerritoryFlags>().Where(x => x != TerritoryFlags.None && (f & x) == x).Select(x => x.ToString()).ToArray();

        private static string Names(IEnumerable<string> names) => "[" + string.Join(",", names.OrderBy(x => x, StringComparer.Ordinal)) + "]";

        // ── TSAdmin, phase 2 ───────────────────────────────────────────────────────────────────────────────

        internal static IEnumerator AdminPart()
        {
            if (!Supported)
            {
                Report.Info("0.4.0 sub-territory phase skipped: this DLL's Territory has no parent / follow marks, so the sub-territory checks do not apply");
                yield break;
            }

            Vector3 origin = ScenarioAdmin.Origin;
            JObject layout = RunDir.Read("layout");
            float rotation = layout != null && layout["rotation"] != null ? (float)layout["rotation"] : 0f;
            // Clear of every territory the older phases made (the client's own list knows their real size), and of the land's own
            // neighbours: the land is r=14 with a town 6 m along inside it and a fourth territory 40 m off in A40h.
            bool placed = Layout.PlaceClear(origin, rotation, 22f, 30f, out Vector3 cLand);
            cLand.y = World.Ground(cLand);
            var cTown = new Vector3(cLand.x + 6f, 0f, cLand.z);
            cTown.y = World.Ground(cTown);
            ScenarioAdmin.Centres[Land.Key] = cLand;
            ScenarioAdmin.Centres[Town.Key] = cTown;
            Report.Info($"0.4.0 sub-territory phase: '{LandName}' {World.P(cLand)} r={Land.HalfX}, '{TownName}' {World.P(cTown)} r={Town.HalfX} inside it; placement clear of the others: {placed}");

            var made = new Dictionary<string, Territory>();
            yield return Flow.Step("A40a-create-land", "create the land", Scenario030.CreateOne(Land, cLand, "A40a-create-land", made));
            made.TryGetValue(Land.Key, out Territory land);
            if (land != null)
            {
                Report.Check("A40a2-land-top-level", $"'{LandName}' was marked as a TOP-LEVEL territory: no parent, no follow marks",
                    Parent(land) == 0 && FollowFlags(land) == TerritoryFlags.None && FollowBits(land) == 0,
                    $"parent {Parent(land)} followFlags {FollowFlags(land)} follow {FollowBits(land)}");
            }

            yield return Flow.Step("A40b-create-town", "create the town", CreateTown(Town, cTown, land, made, "A40b-create-town"));
            made.TryGetValue(Town.Key, out Territory town);

            if (land != null && town != null)
                yield return Flow.Step("A40c-edit-land", "edit the land", EditLand(land, town));

            Territory landNow = land != null ? TerritoryIndex.Find(land.Id) : null;
            Territory townNow = town != null ? TerritoryIndex.Find(town.Id) : null;
            yield return Flow.Step("A40-shots", "look at the panel", PanelShots(landNow, townNow));
            bool ok = landNow != null && townNow != null && Parent(townNow) == landNow.Id && _landEdited;
            RunDir.Signal("admin.sub", new JObject
            {
                ["ok"] = ok,
                ["why"] = ok ? "" : "the land and the town were not created and edited as planned (see A40a / A40b / A40c)",
                ["land"] = Rec(landNow, cLand),
                ["town"] = Rec(townNow, cTown),
                ["landFlags"] = new JArray(landNow != null ? FlagNames(landNow.Flags) : new string[0]),
                ["townFlags"] = new JArray(townNow != null ? FlagNames(townNow.Flags) : new string[0]),
                ["townFollowFlags"] = (int)FollowFlags(townNow),
                ["townFollow"] = FollowBits(townNow),
                ["enterText"] = RealmText,
                ["landColour"] = landNow != null ? Territory.ColorToHex(landNow.Color) : ""
            });
        }

        private static JObject Rec(Territory t, Vector3 c) => new JObject
        {
            ["id"] = t?.Id ?? 0,
            ["name"] = t?.Name ?? "",
            ["centre"] = V(c),
            ["revision"] = t?.Revision ?? 0,
            ["pvp"] = t?.PvP.ToString() ?? "",
            ["enter"] = t?.EnterMessage ?? "",
            ["colour"] = t != null ? Territory.ColorToHex(t.Color) : ""
        };

        private static bool _landEdited;

        /// <summary>
        /// The panel's draft as a sub-territory's is: parent AUTOMATIC, every rule and group following except building (off) and PvP
        /// (forced off), which the town sets itself. Marked with the same terr_place the hammer runs.
        /// </summary>
        private static IEnumerator CreateTown(Spec s, Vector3 c, Territory land, Dictionary<string, Territory> made, string id)
        {
            ScenarioAdmin.SetDraft(s);
            Territory d = TerritoryPanel.NewDraft;
            SetParent(d, AutoParent);
            SetFollowFlags(d, AllFlags & ~TerritoryFlags.BuildProtect);
            SetFollowBits(d, FollowAll & ~FollowPvP);

            float before = TerritoryClient.LastResultAt;
            World.Run(IhCommand.Place(IhCommand.ShapeOf(s.Kind), c, s.HalfX, s.HalfZ, s.Yaw));
            var ok = new Box<bool>();
            yield return Flow.Until(() => World.ByName(s.Name) != null && TerritoryClient.LastResultAt > before, T, ok);
            Territory t = World.ByName(s.Name);
            string result = TerritoryClient.LastResult ?? "";
            var why = new List<string>();
            if (!ok.Value || t == null || !TerritoryClient.LastResultOk || !result.StartsWith("Created #"))
                why.Add($"not created: result ok={TerritoryClient.LastResultOk} '{result}', in the index: {t != null}");
            else
            {
                made[s.Key] = t;
                if (land == null) why.Add("there is no land to be inside");
                else
                {
                    if (Parent(t) != land.Id) why.Add($"the parent is #{Parent(t)}, the server should have picked the land #{land.Id}");
                    TerritoryFlags want = land.Flags & ~TerritoryFlags.BuildProtect;
                    if (t.Flags != want) why.Add($"flags {t.Flags}, wanted the land's less building: {want}");
                    if (t.PvP != PvPRule.ForcedOff) why.Add($"pvp {t.PvP}, it set forced off itself");
                    if (t.EnterMessage != land.EnterMessage) why.Add($"entry text '{t.EnterMessage}', the land's is '{land.EnterMessage}'");
                    if (Territory.ColorToHex(t.Color) != Territory.ColorToHex(land.Color)) why.Add($"colour {Territory.ColorToHex(t.Color)}, the land's is {Territory.ColorToHex(land.Color)} (its own, drafted, was {Territory.ColorToHex(s.Color)})");
                    if (FollowFlags(t) != (AllFlags & ~TerritoryFlags.BuildProtect)) why.Add($"it follows rules {FollowFlags(t)}, drafted {AllFlags & ~TerritoryFlags.BuildProtect}");
                    if (FollowBits(t) != (FollowAll & ~FollowPvP)) why.Add($"it follows groups {FollowBits(t)}, drafted {FollowAll & ~FollowPvP}");
                }
                if (t.Revision != 1) why.Add($"revision {t.Revision}");
                if (!t.CreatedBy.StartsWith(E2EEnv.AdminName)) why.Add($"createdBy '{t.CreatedBy}'");
            }
            Report.Check(id, $"'{s.Name}' marked inside the land with the parent on AUTOMATIC: the server picks the land, keeps what the town sets (building off, PvP forced off) and fills in the rest from the land: {World.Describe(t)} parent #{Parent(t)}",
                why.Count == 0, string.Join("; ", why));
        }

        private static IEnumerator EditLand(Territory land, Territory town)
        {
            Territory fresh = TerritoryIndex.Find(land.Id) ?? land;
            Territory record = fresh.Clone();
            record.Set(TerritoryFlags.HarvestProtect, true);
            record.EnterMessage = RealmText;
            int landRev = fresh.Revision;
            int townRev = TerritoryIndex.Find(town.Id)?.Revision ?? 0;
            float before = TerritoryClient.LastResultAt;
            TerritoryClient.Send(RequestOp.Update, record, fresh.Id);   // what "Save changes" sends

            var ok = new Box<bool>();
            yield return Flow.Until(() =>
            {
                Territory l = TerritoryIndex.Find(land.Id), t = TerritoryIndex.Find(town.Id);
                return l != null && t != null && l.Revision > landRev && t.Has(TerritoryFlags.HarvestProtect) && TerritoryClient.LastResultAt > before;
            }, T, ok);
            Territory landNow = TerritoryIndex.Find(land.Id), townNow = TerritoryIndex.Find(town.Id);
            var why = new List<string>();
            if (!ok.Value || landNow == null || townNow == null) why.Add($"the edit did not come back in time: result ok={TerritoryClient.LastResultOk} '{TerritoryClient.LastResult}'");
            else
            {
                if (landNow.Revision != landRev + 1) why.Add($"the land's revision {landNow.Revision}, wanted {landRev + 1}");
                if (townNow.Revision != townRev) why.Add($"the town's revision moved to {townNow.Revision} from {townRev}: editing its parent must not touch the town's own record");
                if (townNow.EnterMessage != RealmText) why.Add($"the town's entry text '{townNow.EnterMessage}', it follows the land's '{RealmText}'");
                if (!townNow.Has(TerritoryFlags.HarvestProtect)) why.Add("the town did not take the land's new harvest protection");
                if (townNow.Has(TerritoryFlags.BuildProtect)) why.Add("the town has building protection although it sets building off itself");
                if (!townNow.Has(TerritoryFlags.AccessProtect)) why.Add("the town lost the land's door protection");
                if (townNow.PvP != PvPRule.ForcedOff) why.Add($"the town's PvP {townNow.PvP}, it sets forced off itself");
                _landEdited = why.Count == 0;
            }
            Report.Check("A40c-edit-land", $"the land edited (harvest protection on, entry text '{RealmText}'): the town follows both through the snapshot, still builds freely, still forced off, and its own revision stays {townRev}: {World.Describe(townNow)}",
                why.Count == 0, string.Join("; ", why));
        }

        // ── a look at the panel, in --graphics runs only ──────────────────────────────────────────────────────

        private static readonly FieldInfo PanelTab = AccessTools.Field(typeof(TerritoryPanel), "_tab");
        private static readonly FieldInfo PanelScroll = AccessTools.Field(typeof(TerritoryPanel), "_scroll");
        private static readonly MethodInfo PanelEndEdit = AccessTools.Method(typeof(TerritoryPanel), "EndEdit");

        /// <summary>
        /// The panel as the player sees it, saved as PNGs in the run's shots/ directory for the eye: IMGUI is drawn after the cameras, so the
        /// frame is read back from the screen at the end of the frame. Only a client with a graphics device can; others log one line. The
        /// rows of a sub-territory (the follows / own column), the Members tab, the list as a tree, a new territory's draft and a top-level
        /// territory (no column at all) - the layouts a player has to find aligned. Nothing is judged here: the look is read off the PNGs.
        /// </summary>
        private static IEnumerator PanelShots(Territory land, Territory town)
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                Report.Info("panel shots skipped: this client has no graphics device (run with --graphics)");
                yield break;
            }
            if (PanelTab == null || PanelScroll == null || town == null || land == null)
            {
                Report.Info("panel shots skipped: this DLL's panel has no _tab / _scroll to drive, or the territories are missing");
                yield break;
            }

            string dir = Path.Combine(E2EEnv.RunDir ?? ".", "shots");
            Directory.CreateDirectory(dir);
            // The panel is as big as the window lets it be (it adapts to Screen.width and Screen.height); a batchmode client starts at
            // 640 x 480, which cramps it. Asked for the virtual display's own size; what the game does with that is logged.
            const int wantW = 1024, wantH = 768;   // the rig's virtual display (run-e2e.sh: Xvfb -screen 0 1024x768x24)
            if (Screen.width != wantW || Screen.height != wantH)
            {
                Screen.SetResolution(wantW, wantH, false);
                yield return Flow.Frames(30);
                Report.Info($"panel shots: the screen is {Screen.width}x{Screen.height} after asking for {wantW}x{wantH}");
            }
            Type tabType = PanelTab.FieldType;
            object Tab(string name) => Enum.Parse(tabType, name);

            OpenPanelOn(town);
            yield return Flow.Frames(20);
            foreach (KeyValuePair<string, float> view in new[] { new KeyValuePair<string, float>("top", 0f), new KeyValuePair<string, float>("mid", 420f), new KeyValuePair<string, float>("low", 840f), new KeyValuePair<string, float>("bottom", 3000f) })
            {
                PanelTab.SetValue(null, Tab("Rules"));
                PanelScroll.SetValue(null, new Vector2(0f, view.Value));
                yield return Flow.Frames(12);
                yield return Shot(Path.Combine(dir, $"panel-sub-rules-{view.Key}.png"));
            }
            // The Inside picker open on the town (its rows greyed or lit as the server's rules say), then closed again.
            FieldInfo pickerOpen = AccessTools.Field(typeof(TerritoryPanel), "_pickerOpen");
            if (pickerOpen != null)
            {
                pickerOpen.SetValue(null, true);
                PanelTab.SetValue(null, Tab("Rules"));
                PanelScroll.SetValue(null, Vector2.zero);
                yield return Flow.Frames(12);
                yield return Shot(Path.Combine(dir, "panel-sub-picker.png"));
                pickerOpen.SetValue(null, false);
                yield return Flow.Frames(4);
            }
            foreach (string tab in new[] { "Members", "All", "Here", "Place" })
            {
                PanelTab.SetValue(null, Tab(tab));
                PanelScroll.SetValue(null, Vector2.zero);
                yield return Flow.Frames(12);
                yield return Shot(Path.Combine(dir, $"panel-sub-{tab.ToLowerInvariant()}.png"));
            }

            OpenPanelOn(land);
            yield return Flow.Frames(20);
            PanelTab.SetValue(null, Tab("Rules"));
            PanelScroll.SetValue(null, Vector2.zero);
            yield return Flow.Frames(12);
            yield return Shot(Path.Combine(dir, "panel-top-level-rules-top.png"));
            // The foot of the land's Rules tab: with the town inside it, Delete says what happens to it, and a second row removes both.
            PanelScroll.SetValue(null, new Vector2(0f, 3000f));
            yield return Flow.Frames(12);
            yield return Shot(Path.Combine(dir, "panel-top-level-rules-bottom.png"));

            // A new territory's draft: parent AUTOMATIC, everything following (the panel's own start).
            PanelEndEdit?.Invoke(null, null);
            PanelTab.SetValue(null, Tab("Rules"));
            PanelScroll.SetValue(null, Vector2.zero);
            yield return Flow.Frames(12);
            yield return Shot(Path.Combine(dir, "panel-draft-rules-top.png"));
            PanelScroll.SetValue(null, new Vector2(0f, 420f));
            yield return Flow.Frames(12);
            yield return Shot(Path.Combine(dir, "panel-draft-rules-mid.png"));

            TerritoryPanel.Close();
            Report.Info($"panel shots saved in {dir} (panel-*.png), screen {Screen.width}x{Screen.height}");
        }

        // ── the panel booth (spike a, a WINDOWED graphics client: E2E_WINDOWED=1 E2E_BOOTH=1) ───────────────────────────────────────

        private static readonly Type InheritanceType = Mod.GetType("TerroritySmellatory.Inheritance");

        /// <summary>The booth needs the 0.4.0 model (to build a world of sub-territories) and the panel's own tab field.</summary>
        internal static bool BoothSupported => Supported && InheritanceType != null && PanelTab != null && AccessTools.Method(typeof(TerritoryPanel), "BeginEdit") != null;

        private static bool SkipTick() => false;

        /// <summary>The panel opened on <paramref name="t"/>: as the hammer tool does it in a world; in the booth, where there is no player, by hand.</summary>
        private static void OpenPanelOn(Territory t)
        {
            if (Player.m_localPlayer != null)
            {
                TerritoryPanel.OpenEditing(t);
                return;
            }
            // Open() does nothing without a player (the booth has none), so what it would set is set by hand.
            if (!TerritoryPanel.IsOpen) typeof(TerritoryPanel).GetProperty("IsOpen", Any)?.SetValue(null, true);
            AccessTools.Method(typeof(TerritoryPanel), "BeginEdit")?.Invoke(null, new object[] { t, -1 });
        }

        private static List<Territory> BoothWorld()
        {
            Territory Make(int id, string name, TerritoryShapeKind kind, float x, float z, float halfX, float halfZ, TerritoryFlags flags, int parent)
            {
                var t = new Territory { Id = id, Name = name, Kind = kind, X = x, Y = 30f, Z = z, HalfX = halfX, HalfZ = halfZ, Flags = flags, Revision = 3, CreatedBy = "Booth" };
                SetParent(t, parent);
                return t;
            }

            const TerritoryFlags landRules = TerritoryFlags.BuildProtect | TerritoryFlags.AccessProtect | TerritoryFlags.HarvestProtect | TerritoryFlags.NoMonsterSpawns |
                                             TerritoryFlags.RunicBarrier | TerritoryFlags.Announce | TerritoryFlags.ShowOnMap | TerritoryFlags.ShowBorder;

            // A continent with an owner, members and its own words; a town inside it that sets only its priority, its colour and its welcome;
            // a market inside the town that sets only that building is off and PvP is forced off; and a plain territory with no parent.
            Territory svart = Make(1, "Svartland", TerritoryShapeKind.Rectangle, 0f, 0f, 3000f, 2000f, landRules, 0);
            svart.PvP = PvPRule.ForcedOn;
            svart.Priority = 5;
            svart.OwnerId = 111L;
            svart.OwnerName = "Jarl Svart";
            svart.Members.Add(new TerritoryMember(222L, "Kin of the Jarl"));
            svart.Members.Add(new TerritoryMember(333L, "Thrall"));
            svart.Members.Add(new TerritoryMember(444L, "Skald"));
            svart.HealthRegen = 1.5f;
            svart.CarryBonus = 100f;
            svart.EnterMessage = "Welcome to {name}";
            svart.LeaveMessage = "Fare well from {name}";
            svart.Color = new Color32(200, 70, 60, 255);

            Territory town = Make(2, "Hrafnborg", TerritoryShapeKind.Circle, 300f, 100f, 150f, 150f, TerritoryFlags.None, 1);
            SetFollowFlags(town, AllFlags);
            SetFollowBits(town, FollowAll & ~(2 | 16 | 64));   // Priority, EnterText and Color are its own
            town.Priority = 8;
            town.EnterMessage = "Hail, traveller, to {name}";
            town.Color = new Color32(60, 120, 200, 255);

            Territory market = Make(3, "Ulfheim Market", TerritoryShapeKind.Circle, 330f, 110f, 30f, 30f, TerritoryFlags.None, 2);
            SetFollowFlags(market, AllFlags & ~TerritoryFlags.BuildProtect);   // building is off here, the rest is the town's
            SetFollowBits(market, FollowAll & ~FollowPvP);
            market.PvP = PvPRule.ForcedOff;

            Territory free = Make(4, "Free Land", TerritoryShapeKind.Circle, -1500f, 0f, 80f, 80f, TerritoryFlags.Announce | TerritoryFlags.ShowOnMap, 0);
            free.Color = new Color32(90, 160, 90, 255);

            return new List<Territory> { svart, town, market, free };
        }

        /// <summary>
        /// The panel drawn over the main menu on a made-up world, and photographed (see PanelShots). A batchmode client never calls the panel's
        /// OnGUI, so this needs a WINDOWED client (E2E_WINDOWED=1) on the private Xvfb; a spike-a client has no server, no world and no player,
        /// so the panel's and the client's own ticks (which close a panel with no player and clear a list with no ZNet) are skipped, the list
        /// is put into the index as the client does it (stored records, resolved), and the tabs that need a player (Here, All, Place) stay blank.
        /// </summary>
        internal static IEnumerator PanelBooth()
        {
            if (!BoothSupported)
            {
                Report.Info("panel booth skipped: this DLL has no sub-territories or no panel to drive");
                yield break;
            }

            List<Territory> stored = BoothWorld();
            MethodInfo resolve = InheritanceType.GetMethod("Resolve", Any);
            List<Territory> effective = ((IEnumerable<Territory>)resolve.Invoke(null, new object[] { stored })).ToList();
            TerritoryIndex.Set(effective);
            typeof(TerritoryClient).GetProperty("HasSnapshot", Any)?.SetValue(null, true);
            typeof(TerritoryClient).GetProperty("IsAdmin", Any)?.SetValue(null, true);
            TerritoryClient.OnlinePlayers.Add(new TerritoryMember(555L, "Online Viking"));
            TerritoryClient.OnlinePlayers.Add(new TerritoryMember(666L, "Another Viking"));

            var harmony = new Harmony("terr.e2e.booth");
            harmony.Patch(AccessTools.Method(typeof(TerritoryPanel), "Tick"), prefix: new HarmonyMethod(typeof(Scenario040), nameof(SkipTick)));
            harmony.Patch(AccessTools.Method(typeof(TerritoryClient), "Tick"), prefix: new HarmonyMethod(typeof(Scenario040), nameof(SkipTick)));

            yield return Flow.Frames(10);
            Territory svart = TerritoryIndex.Find(1), market = TerritoryIndex.Find(3);
            Report.Info($"panel booth: {TerritoryIndex.All.Count} territories in the index ({market?.DisplayName} follows {Parent(market)}), screen {Screen.width}x{Screen.height}");
            int before = _panelRepaints;
            yield return PanelShots(svart, market);
            // The same rows at other text scales: the narrow and the wide cases the layout arithmetic was worked out for (TSConfig.PanelScale).
            string scaleDir = Path.Combine(E2EEnv.RunDir ?? ".", "shots");
            foreach (float scale in new[] { 0.7f, 1.5f, 2.0f })
            {
                TSConfig.PanelScale.Value = scale;
                OpenPanelOn(market);
                PanelTab.SetValue(null, Enum.Parse(PanelTab.FieldType, "Rules"));
                foreach (KeyValuePair<string, float> view in new[] { new KeyValuePair<string, float>("top", 0f), new KeyValuePair<string, float>("mid", 560f) })
                {
                    PanelScroll.SetValue(null, new Vector2(0f, view.Value));
                    yield return Flow.Frames(25);
                    yield return Shot(Path.Combine(scaleDir, $"panel-scale-{scale:0.0}-rules-{view.Key}.png"));
                }
            }
            TSConfig.PanelScale.Value = 1f;
            TerritoryPanel.Close();
            int drawn = _panelRepaints - before;
            string dir = Path.Combine(E2EEnv.RunDir ?? ".", "shots");
            int pictures = Directory.Exists(dir) ? Directory.GetFiles(dir, "panel-*.png").Length : 0;
            Report.Check("F03-panel-drew", $"the panel was repainted in the booth ({drawn} Repaint events, {pictures} pictures in {dir}); OnGUI events seen: {PanelEventsSeen()}",
                drawn > 0 && pictures > 0, $"Repaints {drawn}, pictures {pictures}, events: {PanelEventsSeen()}");
        }

        // IMGUI is drawn to the window, so a shot is the screen read back at the end of the frame - which only a WINDOWED client can give (a
        // batchmode client has no window, never calls the panel's OnGUI at all, and a screen read comes back black). A first version of this
        // pointed the render target at a texture of its own for the panel's Repaint; IMGUI ignores that and draws to the window anyway, and
        // the pictures were the empty backdrop. The Harmony patch below only counts what OnGUI is sent, so a run can say what it saw.

        private static bool _panelPatched;
        private static int _panelRepaints;
        private static readonly Dictionary<int, int> _panelEvents = new Dictionary<int, int>();

        private static string PanelEventsSeen() =>
            _panelEvents.Count == 0 ? "no OnGUI call at all" : string.Join(", ", _panelEvents.OrderBy(k => k.Key).Select(k => $"{(EventType)k.Key} x{k.Value}"));

        private static void EnsurePanelCounter()
        {
            if (_panelPatched) return;
            _panelPatched = true;
            MethodInfo onGui = AccessTools.Method(typeof(TerritoryPanel), "OnGUI");
            if (onGui == null) return;
            new Harmony("terr.e2e.panelcount").Patch(onGui, prefix: new HarmonyMethod(typeof(Scenario040), nameof(PanelOnGuiCount)));
        }

        private static void PanelOnGuiCount()
        {
            Event e = Event.current;
            if (e == null) return;
            _panelEvents[(int)e.type] = (_panelEvents.TryGetValue((int)e.type, out int seen) ? seen : 0) + 1;
            if (e.type == EventType.Repaint) _panelRepaints++;
        }

        private static IEnumerator Shot(string path)
        {
            EnsurePanelCounter();
            yield return new WaitForEndOfFrame();
            var tex = new Texture2D(Screen.width, Screen.height, TextureFormat.RGB24, false);
            try
            {
                tex.ReadPixels(new Rect(0f, 0f, Screen.width, Screen.height), 0, 0);
                tex.Apply();
                File.WriteAllBytes(path, tex.EncodeToPNG());
            }
            finally { UnityEngine.Object.Destroy(tex); }
        }

        // ── TSPlain ─────────────────────────────────────────────────────────────────────────────────────────

        private static readonly string[] PlainIds = { "P40a-snapshot", "P40b-enforced", "P40c-pvp-banner" };

        internal static IEnumerator PlainPart(Vector3 home)
        {
            if (!Supported) yield break;
            var barrier = new Box<JObject>();
            yield return RunDir.Wait("admin.sub", 300f, barrier);
            JObject c = barrier.Value;
            if (c == null || c["ok"] == null || !(bool)c["ok"])
            {
                string reason = c == null ? "admin.sub never signalled" : (string)c["why"] ?? "see A40a / A40b / A40c";
                foreach (string id in PlainIds) Report.Fail(id, "the sub-territories as a visitor sees them", reason);
                yield break;
            }
            var ok = new Box<bool>();
            yield return Flow.Until(() => World.ByName(LandName) != null && World.ByName(TownName) != null && World.ByName(TownName).EnterMessage == RealmText, T, ok);
            if (!ok.Value)
            {
                foreach (string id in PlainIds) Report.Fail(id, "the sub-territories as a visitor sees them", $"the land and the town (with the land's new entry text) did not reach TSPlain's index within {T:0} s");
                yield break;
            }

            PlainParts();
            Territory land = World.ByName(LandName), town = World.ByName(TownName);

            // P40a: the snapshot carries the parent and the marks, and the town's effective values are the land's.
            var why = new List<string>();
            if (Parent(town) != land.Id) why.Add($"the town's parent is #{Parent(town)}, the land is #{land.Id}");
            if ((int)FollowFlags(town) != (int)c["townFollowFlags"]) why.Add($"it follows rules {(int)FollowFlags(town)}, the admin saw {(int)c["townFollowFlags"]}");
            if (FollowBits(town) != (int)c["townFollow"]) why.Add($"it follows groups {FollowBits(town)}, the admin saw {(int)c["townFollow"]}");
            var have = new HashSet<string>(FlagNames(town.Flags));
            if (!have.SetEquals(((JArray)c["townFlags"]).Select(x => (string)x))) why.Add($"flags {Names(have)}, the admin saw {Names(((JArray)c["townFlags"]).Select(x => (string)x))}");
            if (town.PvP != PvPRule.ForcedOff || land.PvP != PvPRule.ForcedOn) why.Add($"PvP town {town.PvP} land {land.PvP}");
            if (Territory.ColorToHex(town.Color) != (string)c["landColour"]) why.Add($"the town's colour {Territory.ColorToHex(town.Color)}, the land's {c["landColour"]}");
            if (Parent(land) != 0) why.Add($"the land has a parent (#{Parent(land)})");
            Report.Check("P40a-snapshot", $"TSPlain's snapshot has the town inside the land with the marks and effective values the admin saw: parent #{Parent(town)}, flags {Names(have)}, pvp {town.PvP}, entry text '{town.EnterMessage}'",
                why.Count == 0, string.Join("; ", why));

            // P40b: the real ward funnel, as a visitor who is nobody's member.
            Vector3 inTown = World.Local(town, 0f, 0f);
            Vector3 inLand = World.Local(land, -9f, 0f);
            yield return Scenario030.Arrive(inTown, 0.8f);
            bool townAccess = World.CheckAccess(inTown, build: false, flash: false);
            bool townBuild = World.CheckAccess(inTown, build: true, flash: false);
            bool landAccess = World.CheckAccess(inLand, build: false, flash: false);
            bool landBuild = World.CheckAccess(inLand, build: true, flash: false);
            Report.Check("P40b-enforced",
                "as a visitor: inside the town doors are refused (it follows the land's) but building is allowed (its own); inside the land beside it both are refused",
                !townAccess && townBuild && !landAccess && !landBuild,
                $"town: doors {(townAccess ? "ALLOWED" : "refused")} (wanted refused), building {(townBuild ? "allowed" : "REFUSED")} (wanted allowed); land: doors {(landAccess ? "ALLOWED" : "refused")}, building {(landBuild ? "ALLOWED" : "refused")} (wanted both refused)");

            // P40c: PvP held and the banner, in the town and then on the land beside it.
            var pvp = new List<string>();
            // P40b's Arrive left the player standing IN the town, where its banner came up and went again: the player steps out into open
            // country, and in again, and the banner is looked for straight after that move (as P03b does) - it lives about 4 s, which a wait
            // for the area's objects (Scenario030.Arrive) would outlast. The hops are short, as World.MoveTo requires: the zones are loaded.
            yield return World.Go(StagingPoint(inTown), 1.0f);
            yield return World.Go(inTown, 0.5f);
            string inTownPvP = Presence.Current?.Id == town.Id && Presence.ForcedPvP == false ? "" : $"in the town: Presence.Current {World.Describe(Presence.Current)} ForcedPvP {Presence.ForcedPvP} (wanted the town, forced off)";
            if (inTownPvP != "") pvp.Add(inTownPvP);
            if (Features.EpicBanner && World.NotchSupported)
            {
                var found = new Box<World.BannerView>();
                yield return WaitForBanner(TownName, found);
                World.BannerView seen = found.Value;
                string subtitle = RealmText.Replace("{name}", TownName);
                if (seen == null || seen.Title != TownName) pvp.Add($"the town's banner never came up (slot: {(seen != null ? seen.ToString() : "empty")})");
                else
                {
                    if (seen.Subtitle != subtitle) pvp.Add($"the town's banner subtitle '{seen.Subtitle}', wanted the land's text with the town's own name: '{subtitle}'");
                    string hue = !seen.HasNotch ? "none" : seen.NotchColour.HasValue ? World.HueOf(seen.NotchColour.Value) : "no image";
                    if (hue != "green") pvp.Add($"the town's notch is {hue}, wanted green (PvP forced off)");
                }
            }

            yield return World.Go(inLand, 0.5f);
            if (!(Presence.Current?.Id == land.Id && Presence.ForcedPvP == true))
                pvp.Add($"on the land: Presence.Current {World.Describe(Presence.Current)} ForcedPvP {Presence.ForcedPvP} (wanted the land, forced on)");
            if (Features.EpicBanner && World.NotchSupported)
            {
                var found = new Box<World.BannerView>();
                yield return WaitForBanner(LandName, found);
                World.BannerView seen = found.Value;
                if (seen == null || seen.Title != LandName) pvp.Add($"the land's banner never came up (slot: {(seen != null ? seen.ToString() : "empty")})");
                else
                {
                    string hue = !seen.HasNotch ? "none" : seen.NotchColour.HasValue ? World.HueOf(seen.NotchColour.Value) : "no image";
                    if (hue != "red") pvp.Add($"the land's notch is {hue}, wanted red (PvP forced on)");
                }
            }
            Report.Check("P40c-pvp-banner",
                $"PvP is held where the territory says (forced off in the town, forced on in the land beside it){(Features.EpicBanner && World.NotchSupported ? ", and each Epic banner carries its notch (green, red) with the town showing the land's entry text filled in with its own name" : "")}",
                pvp.Count == 0, string.Join("; ", pvp));
            yield return World.Go(home, 1f);
        }

        /// <summary>
        /// A dry point 38 to 70 m from <paramref name="around"/> that is clear of every territory by 8 m or more (the best there is when none
        /// is): open country, where the player stands before stepping into one.
        /// </summary>
        private static Vector3 StagingPoint(Vector3 around)
        {
            float water = World.WaterLevel;
            Vector3 best = around + new Vector3(-45f, 0f, 0f);
            float bestRoom = float.MinValue;
            foreach (float ring in new[] { 45f, 38f, 52f, 60f, 70f })
            {
                for (int step = 0; step < 24; step++)
                {
                    Vector3 c = around + Quaternion.Euler(0f, step * 15f, 0f) * new Vector3(0f, 0f, ring);
                    c.y = World.Ground(c);
                    if (c.y - water < 1f) continue;
                    float room = float.MaxValue;
                    foreach (Territory t in TerritoryIndex.All) room = Mathf.Min(room, t.Ground.DistanceOutside(c));
                    if (room > bestRoom)
                    {
                        bestRoom = room;
                        best = c;
                    }
                    if (room >= 8f) return c;
                }
            }
            return best;
        }

        /// <summary>The banner in vanilla's slot titled <paramref name="title"/>, waited for up to 12 s (the mod holds it while vanilla's own is up); else whatever was last in the slot.</summary>
        private static IEnumerator WaitForBanner(string title, Box<World.BannerView> result)
        {
            float end = Time.realtimeSinceStartup + 12f;
            while (Time.realtimeSinceStartup < end)
            {
                World.BannerView v = World.BannerNow();
                if (v != null) result.Value = v;
                if (v != null && v.Title == title) yield break;
                yield return Flow.Frames(2);   // a banner lives 4 s, and a software-rendered client may draw only a few frames a second: look often
            }
        }

        // ── TSAdmin, phase 3 ───────────────────────────────────────────────────────────────────────────────

        internal static IEnumerator AdminLater()
        {
            if (!Supported) yield break;
            JObject sub = RunDir.Read("admin.sub");
            if (sub == null || sub["ok"] == null || !(bool)sub["ok"])
            {
                foreach (string id in new[] { "A40d-follow-again", "A40e-delete-land", "A40f-pair", "A40g-delete-tree", "A40h-combine" })
                    Report.Fail(id, "the later sub-territory steps", "the land and the town were not set up (admin.sub " + (sub == null ? "missing" : "says not ok") + ")");
                RunDir.Signal("admin.sub-deleted", new JObject { ["ok"] = false });
                yield break;
            }
            int landId = (int)sub["land"]["id"], townId = (int)sub["town"]["id"];
            var cLand = new Vector3((float)sub["land"]["centre"]["x"], (float)sub["land"]["centre"]["y"], (float)sub["land"]["centre"]["z"]);

            // A40d: the town follows building again.
            Territory town = TerritoryIndex.Find(townId);
            var why = new List<string>();
            if (town == null) why.Add("the town is not in the index");
            else
            {
                Territory record = town.Clone();
                SetFollowFlags(record, FollowFlags(record) | TerritoryFlags.BuildProtect);   // what the panel's 'follows' does on the building row
                int rev = town.Revision;
                float before = TerritoryClient.LastResultAt;
                TerritoryClient.Send(RequestOp.Update, record, townId);
                var ok = new Box<bool>();
                yield return Flow.Until(() => TerritoryIndex.Find(townId)?.Revision > rev && TerritoryClient.LastResultAt > before, T, ok);
                Territory now = TerritoryIndex.Find(townId);
                if (!ok.Value || now == null) why.Add($"the update did not come back: '{TerritoryClient.LastResult}'");
                else
                {
                    if (!now.Has(TerritoryFlags.BuildProtect)) why.Add("building is still open although the town follows the land's again");
                    if (FollowFlags(now) != AllFlags) why.Add($"it follows rules {FollowFlags(now)}, wanted every rule");
                    if (now.PvP != PvPRule.ForcedOff) why.Add($"PvP {now.PvP}, it still sets forced off itself");
                    if (now.Revision != rev + 1) why.Add($"revision {now.Revision}, wanted {rev + 1}");
                }
            }
            Report.Check("A40d-follow-again", "the town follows building again: the mark is back, building is protected as the land's, its PvP is still its own", why.Count == 0, string.Join("; ", why));

            // A40e: the land goes. The town keeps every rule and moves up.
            town = TerritoryIndex.Find(townId);
            why = new List<string>();
            JObject baked = null;
            if (town == null) why.Add("the town is not in the index");
            else
            {
                var flagsBefore = new HashSet<string>(FlagNames(town.Flags));
                PvPRule pvpBefore = town.PvP;
                string enterBefore = town.EnterMessage, colourBefore = Territory.ColorToHex(town.Color);
                int revBefore = town.Revision;
                float before = TerritoryClient.LastResultAt;
                TerritoryClient.Send(RequestOp.Delete, null, landId);
                var ok = new Box<bool>();
                yield return Flow.Until(() => TerritoryIndex.Find(landId) == null && TerritoryClient.LastResultAt > before, T, ok);
                Territory now = TerritoryIndex.Find(townId);
                string result = TerritoryClient.LastResult ?? "";
                if (!ok.Value || now == null) why.Add($"the delete did not come back: '{result}'");
                else
                {
                    if (!result.Contains("sub-territory")) why.Add($"the reply does not say what happened to the town: '{result}'");
                    if (Parent(now) != 0) why.Add($"the town is still inside #{Parent(now)}");
                    if (FollowFlags(now) != TerritoryFlags.None || FollowBits(now) != 0) why.Add($"it still follows (rules {FollowFlags(now)}, groups {FollowBits(now)})");
                    var flagsNow = new HashSet<string>(FlagNames(now.Flags));
                    if (!flagsNow.SetEquals(flagsBefore)) why.Add($"flags {Names(flagsNow)}, before the delete {Names(flagsBefore)}");
                    if (now.PvP != pvpBefore) why.Add($"PvP {now.PvP}, was {pvpBefore}");
                    if (now.EnterMessage != enterBefore) why.Add($"entry text '{now.EnterMessage}', was '{enterBefore}'");
                    if (Territory.ColorToHex(now.Color) != colourBefore) why.Add($"colour {Territory.ColorToHex(now.Color)}, was {colourBefore}");
                    if (now.Revision != revBefore + 1) why.Add($"revision {now.Revision}, wanted {revBefore + 1} (its record changed)");
                    baked = new JObject
                    {
                        ["id"] = now.Id, ["name"] = now.Name, ["flags"] = new JArray(FlagNames(now.Flags)), ["pvp"] = now.PvP.ToString(),
                        ["enter"] = now.EnterMessage ?? "", ["colour"] = Territory.ColorToHex(now.Color), ["revision"] = now.Revision
                    };
                }
            }
            Report.Check("A40e-delete-land", "the land deleted: the town keeps every rule, PvP, text and colour, moves to the top level, follows nothing, and its revision goes up by one", why.Count == 0, string.Join("; ", why));

            // A40f / A40g: a second pair, deleted in one request.
            var made = new Dictionary<string, Territory>();
            var c2 = new Vector3(cLand.x - 6f, 0f, cLand.z);
            c2.y = World.Ground(c2);
            yield return Flow.Step("A40f-pair", "mark a second pair", CreatePair(cLand, c2, made));
            made.TryGetValue(Land2.Key, out Territory land2);
            made.TryGetValue(Town2.Key, out Territory town2);
            yield return Flow.Step("A40g-delete-tree", "delete the pair in one request", DeletePair(land2, town2));
            yield return Flow.Step("A40h-combine", "combine a land that has a sub-territory into another territory", CombineWithSub(cLand));

            RunDir.Signal("admin.sub-deleted", new JObject { ["ok"] = baked != null, ["town"] = baked ?? new JObject() });
        }

        private static IEnumerator CreatePair(Vector3 cLand, Vector3 cTown2, Dictionary<string, Territory> made)
        {
            var land = new Dictionary<string, Territory>();
            yield return Scenario030.CreateOne(Land2, cLand, "A40f-pair-land", land);
            land.TryGetValue(Land2.Key, out Territory l);
            if (l != null) made[Land2.Key] = l;
            if (l == null)
            {
                Report.Fail("A40f-pair", "a second land and a town inside it", "the second land was not created (see A40f-pair-land)");
                yield break;
            }
            var town = new Dictionary<string, Territory>();
            yield return CreateTown(Town2, cTown2, l, town, "A40f-pair-town");
            town.TryGetValue(Town2.Key, out Territory t);
            if (t != null) made[Town2.Key] = t;
            Report.Check("A40f-pair", $"a second land and a town inside it (picked as its parent: #{Parent(t)}, the land is #{l.Id})", t != null && Parent(t) == l.Id,
                $"town {(t != null ? World.Describe(t) : "not created")}");
        }

        /// <summary>
        /// A third land with a town inside it is combined INTO a small top-level territory: the land goes (its ground becomes a part of the
        /// small one), and the town - which followed the land - keeps every rule it had and now sits inside the small one, its revision up
        /// by one. Then DeleteTree on the small one takes the town too, so nothing is left behind for the checks that follow.
        /// </summary>
        private static IEnumerator CombineWithSub(Vector3 cLand)
        {
            var made = new Dictionary<string, Territory>();
            var cTown3 = new Vector3(cLand.x - 6f, 0f, cLand.z);
            cTown3.y = World.Ground(cTown3);
            var cLand4 = new Vector3(cLand.x, 0f, cLand.z + 40f);
            cLand4.y = World.Ground(cLand4);

            yield return Scenario030.CreateOne(Land3, cLand, "A40h-land3", made);
            made.TryGetValue(Land3.Key, out Territory land3);
            if (land3 != null) yield return CreateTown(Town3, cTown3, land3, made, "A40h-town3");
            yield return Scenario030.CreateOne(Land4, cLand4, "A40h-land4", made);
            made.TryGetValue(Town3.Key, out Territory town3);
            made.TryGetValue(Land4.Key, out Territory land4);
            if (land3 == null || town3 == null || land4 == null)
            {
                Report.Fail("A40h-combine", "the third land combined into the small territory, its town kept and moved under it", "the three territories were not all created (see A40h-land3 / A40h-town3 / A40h-land4)");
                yield break;
            }

            town3 = TerritoryIndex.Find(town3.Id);
            var flagsBefore = new HashSet<string>(FlagNames(town3.Flags));
            string enterBefore = town3.EnterMessage, colourBefore = Territory.ColorToHex(town3.Color);
            PvPRule pvpBefore = town3.PvP;
            int revBefore = town3.Revision;
            int partsBefore = Features.PartCount(land4);
            float before = TerritoryClient.LastResultAt;
            TerritoryClient.Send(RequestOp.Combine, null, land4.Id, land3.Id);   // into, from
            var ok = new Box<bool>();
            yield return Flow.Until(() => TerritoryIndex.Find(land3.Id) == null && TerritoryClient.LastResultAt > before, T, ok);
            Territory now = TerritoryIndex.Find(town3.Id), into = TerritoryIndex.Find(land4.Id);
            var why = new List<string>();
            if (!ok.Value || now == null || into == null) why.Add($"the combine did not come back: ok={TerritoryClient.LastResultOk} '{TerritoryClient.LastResult}'");
            else
            {
                if (!TerritoryClient.LastResultOk) why.Add($"refused: '{TerritoryClient.LastResult}'");
                if (Parent(now) != land4.Id) why.Add($"the town is inside #{Parent(now)}, wanted the small territory #{land4.Id}");
                if (FollowFlags(now) != TerritoryFlags.None || FollowBits(now) != 0) why.Add($"it still follows (rules {FollowFlags(now)}, groups {FollowBits(now)})");
                var flagsNow = new HashSet<string>(FlagNames(now.Flags));
                if (!flagsNow.SetEquals(flagsBefore)) why.Add($"rules {Names(flagsNow)}, before {Names(flagsBefore)}");
                if (now.PvP != pvpBefore || now.EnterMessage != enterBefore || Territory.ColorToHex(now.Color) != colourBefore) why.Add($"PvP {now.PvP} / text '{now.EnterMessage}' / colour {Territory.ColorToHex(now.Color)} changed");
                if (now.Revision != revBefore + 1) why.Add($"revision {now.Revision}, wanted {revBefore + 1}");
                if (Features.PartCount(into) != partsBefore + 1) why.Add($"the small territory has {Features.PartCount(into)} parts, wanted {partsBefore + 1}");
            }
            Report.Check("A40h-combine", "the third land combined into the small territory: the land is gone, its town keeps every rule, moves inside the small territory and follows nothing, and the small one gains a part", why.Count == 0, string.Join("; ", why));

            // Clean up with one request, and prove the town went with it.
            float before2 = TerritoryClient.LastResultAt;
            TerritoryClient.Send(DeleteTree, null, land4.Id);
            var gone = new Box<bool>();
            yield return Flow.Until(() => TerritoryIndex.Find(land4.Id) == null && TerritoryIndex.Find(town3.Id) == null && TerritoryClient.LastResultAt > before2, T, gone);
            Report.Check("A40h-cleanup", $"DeleteTree on the small territory took the town inside it too: '{TerritoryClient.LastResult}'", gone.Value && TerritoryClient.LastResultOk,
                $"small in index: {TerritoryIndex.Find(land4.Id) != null}, town in index: {TerritoryIndex.Find(town3.Id) != null}");
        }

        private static IEnumerator DeletePair(Territory land2, Territory town2)
        {
            if (land2 == null || town2 == null)
            {
                Report.Fail("A40g-delete-tree", "the land and the town inside it deleted by one request", "the pair was not marked (see A40f)");
                yield break;
            }
            float before = TerritoryClient.LastResultAt;
            TerritoryClient.Send(DeleteTree, null, land2.Id);
            var ok = new Box<bool>();
            yield return Flow.Until(() => TerritoryIndex.Find(land2.Id) == null && TerritoryIndex.Find(town2.Id) == null && TerritoryClient.LastResultAt > before, T, ok);
            string result = TerritoryClient.LastResult ?? "";
            Report.Check("A40g-delete-tree", $"DeleteTree on the land took the town inside it too, in one request: '{result}'",
                ok.Value && TerritoryClient.LastResultOk && result.Contains("inside it"),
                $"land in index: {TerritoryIndex.Find(land2.Id) != null}, town in index: {TerritoryIndex.Find(town2.Id) != null}, result ok={TerritoryClient.LastResultOk} '{result}'");
        }

        // ── the server ───────────────────────────────────────────────────────────────────────────────────────

        private static readonly PropertyInfo EffectiveProp = typeof(TerritoryStore).GetProperty("Effective", Any);

        /// <summary>The effective records the server publishes (TerritoryStore.Effective): sub-territories with what they follow filled in.</summary>
        private static List<Territory> EffectiveList()
        {
            var list = new List<Territory>();
            if (EffectiveProp?.GetValue(null, null) is IEnumerable<Territory> all) list.AddRange(all);
            return list;
        }

        private static readonly string[] SubKeysFollowed =
        {
            "color", "ownerId", "ownerName", "members", "priority", "healthRegen", "staminaRegen", "eitrRegen", "carryBonus", "armorBonus", "enterMessage", "leaveMessage"
        };

        private static JObject TryParse(string path, out string error)
        {
            error = null;
            try
            {
                if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) { error = "no file"; return null; }
                return JObject.Parse(System.IO.File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return null;
            }
        }

        /// <summary>
        /// S40a: the SERVER's stored records - the land top level; the town inside it holding ONLY what it sets (building off, PvP forced
        /// off), everything else at the plain defaults; and the effective record the server publishes, which the rules read, is the land's
        /// less building. S40b: the file, parsed independently of the mod's reader, writes the town with "parent" and only the keys it sets
        /// (rules as an object of name: false, "pvp"), and none of the keys it follows; its "version" is 5 or more.
        /// </summary>
        internal static IEnumerator ServerChecks()
        {
            JObject c = RunDir.Read("admin.sub");
            if (!Supported)
            {
                Report.Info("0.4.0 sub-territory server checks skipped: this DLL has no parent / follow marks");
                yield break;
            }
            if (c == null || c["ok"] == null || !(bool)c["ok"])
            {
                string why = "the admin's land and town were not created and edited (admin.sub " + (c == null ? "missing" : "says not ok") + "; see A40a / A40b / A40c)";
                Report.Fail("S40a-sub-store", "the server's store holds the town inside the land with only what it sets", why);
                Report.Fail("S40b-sub-file", "the territory file writes the town with its parent and only the keys it sets", why);
                yield break;
            }
            int landId = (int)c["land"]["id"], townId = (int)c["town"]["id"];
            var problems = new List<string>(ContractProblems());
            Territory land = TerritoryStore.Get(landId), town = TerritoryStore.Get(townId);
            Territory eff = EffectiveList().FirstOrDefault(t => t.Id == townId);
            Territory published = TerritoryIndex.Find(townId);
            if (land == null || town == null) problems.Add($"the store lacks {(land == null ? "the land" : "")} {(town == null ? "the town" : "")}");
            else
            {
                if (Parent(land) != 0 || FollowFlags(land) != TerritoryFlags.None || FollowBits(land) != 0) problems.Add("the land is not a top-level territory in the store");
                if (!new HashSet<string>(FlagNames(land.Flags)).SetEquals(((JArray)c["landFlags"]).Select(x => (string)x))) problems.Add($"the land's rules {Names(FlagNames(land.Flags))} differ from what the admin saw");
                if (Parent(town) != landId) problems.Add($"the town's stored parent is #{Parent(town)}, the land is #{landId}");
                if (FollowFlags(town) != (AllFlags & ~TerritoryFlags.BuildProtect)) problems.Add($"the town stores that it follows rules {FollowFlags(town)}");
                if (FollowBits(town) != (FollowAll & ~FollowPvP)) problems.Add($"the town stores that it follows groups {FollowBits(town)}");
                if (town.Flags != TerritoryFlags.None) problems.Add($"the town's stored rules are {town.Flags}: it follows every rule but building, which it sets OFF, so nothing is stored");
                if (town.PvP != PvPRule.ForcedOff) problems.Add($"the town's stored PvP is {town.PvP}, it sets forced off");
                if (!string.IsNullOrEmpty(town.EnterMessage) || !string.IsNullOrEmpty(town.LeaveMessage)) problems.Add("the town stores banner text although it follows the land's");
                if (Territory.ColorToHex(town.Color) != DefaultColourHex) problems.Add($"the town stores colour {Territory.ColorToHex(town.Color)} although it follows the land's");
                if (town.OwnerId != 0L || town.Members.Count != 0 || town.Priority != 0) problems.Add("the town stores people or a priority although it follows the land's");
                if (town.Revision != 1) problems.Add($"the town's stored revision is {town.Revision}: editing its parent must not touch it");
                if (eff == null) problems.Add("the server has no effective record for the town");
                else
                {
                    if (!new HashSet<string>(FlagNames(eff.Flags)).SetEquals(((JArray)c["townFlags"]).Select(x => (string)x))) problems.Add($"the town's effective rules {Names(FlagNames(eff.Flags))} differ from what the admin saw {Names(((JArray)c["townFlags"]).Select(x => (string)x))}");
                    if (eff.PvP != PvPRule.ForcedOff || eff.EnterMessage != RealmText || Territory.ColorToHex(eff.Color) != (string)c["landColour"]) problems.Add($"the town's effective PvP {eff.PvP}, text '{eff.EnterMessage}', colour {Territory.ColorToHex(eff.Color)}");
                }
                if (published == null || eff == null || published.Flags != eff.Flags || published.PvP != eff.PvP) problems.Add("the server's own rules (TerritoryIndex) do not hold the effective record");
            }
            Report.Check("S40a-sub-store",
                $"the server stores the land top level and the town inside it holding only what it sets (building off, PvP forced off), publishes it with the land's rules filled in, and the contract numbers hold (TerritoryFollow bits, AutoParent, record 3, file 5, DeleteTree 11)",
                problems.Count == 0, string.Join("; ", problems));

            string path = TerritoryStore.FilePath;
            JObject file = null;
            string readError = null;
            var okFile = new Box<bool>();
            yield return Flow.Until(() => (file = TryParse(path, out readError)) != null && file["territories"] is JArray list && list.OfType<JObject>().Any(o => (int?)o["id"] == townId && o["parent"] != null), 15f, okFile);
            var fileProblems = new List<string>();
            try
            {
                if (file == null) fileProblems.Add($"no readable file ({readError})");
                else
                {
                    if ((int)file["version"] < 5) fileProblems.Add($"the file says version {file["version"]}, a file with a parent in it should say 5 or more");
                    List<JObject> recs = ((JArray)file["territories"]).OfType<JObject>().ToList();
                    JObject l = recs.FirstOrDefault(o => (int)o["id"] == landId), t = recs.FirstOrDefault(o => (int)o["id"] == townId);
                    if (l == null) fileProblems.Add("the land is not in the file");
                    else
                    {
                        if (l["parent"] != null) fileProblems.Add("the land has a 'parent' key");
                        if (!(l["flags"] is JArray)) fileProblems.Add("the land's rules are not the usual list of names");
                    }
                    if (t == null) fileProblems.Add("the town is not in the file");
                    else
                    {
                        if (t["parent"] == null || (int)t["parent"] != landId) fileProblems.Add($"the town's 'parent' is {t["parent"]}, wanted {landId}");
                        if (!(t["flags"] is JObject rules) || rules.Count != 1 || rules["BuildProtect"] == null || (bool)rules["BuildProtect"])
                            fileProblems.Add($"the town's 'flags' is {t["flags"]}: wanted exactly {{ \"BuildProtect\": false }}, the one rule it sets");
                        if ((string)t["pvp"] != "ForcedOff") fileProblems.Add($"the town's 'pvp' is {t["pvp"]}, wanted ForcedOff");
                        foreach (string key in SubKeysFollowed)
                            if (t[key] != null) fileProblems.Add($"the town's entry has '{key}', which it follows");
                        if ((int)t["revision"] != 1) fileProblems.Add($"the town's revision in the file is {t["revision"]}");
                    }
                }
            }
            catch (Exception ex) { fileProblems.Add($"the file could not be read field by field: {ex.GetType().Name}: {ex.Message}"); }
            Report.Check("S40b-sub-file",
                "the territory file (parsed independently) writes the town with \"parent\", the one rule it sets as { \"BuildProtect\": false } and its \"pvp\", none of the keys it follows, and a version of 5 or more; the land has no parent",
                okFile.Value && fileProblems.Count == 0, string.Join("; ", fileProblems));
        }

        /// <summary>
        /// S40c: after the admin deleted the land, the server's store AND file hold the town as a TOP-LEVEL territory with every rule it
        /// was following written into it, its revision up by one, no land and no second pair left. Returns what the restart must load
        /// back (R40), or null when the admin's steps did not complete.
        /// </summary>
        internal static JObject ServerFinal(JObject file, string readError)
        {
            if (!Supported) return null;
            JObject d = RunDir.Read("admin.sub-deleted");
            if (d == null || d["ok"] == null || !(bool)d["ok"])
            {
                Report.Fail("S40c-sub-baked", "the town survives its land's deletion as a top-level territory with its rules written into it, in the store and in the file",
                    "the admin's later steps did not complete (admin.sub-deleted " + (d == null ? "missing" : "says not ok") + "; see A40d / A40e)");
                return null;
            }
            JObject want = (JObject)d["town"];
            int id = (int)want["id"];
            var problems = new List<string>();
            Territory t = TerritoryStore.Get(id);
            if (t == null) problems.Add("the town is not in the store");
            else
            {
                if (Parent(t) != 0 || FollowFlags(t) != TerritoryFlags.None || FollowBits(t) != 0) problems.Add($"the stored town still has a parent or marks (parent #{Parent(t)}, rules {FollowFlags(t)}, groups {FollowBits(t)})");
                if (!new HashSet<string>(FlagNames(t.Flags)).SetEquals(((JArray)want["flags"]).Select(x => (string)x))) problems.Add($"its stored rules {Names(FlagNames(t.Flags))}, wanted {Names(((JArray)want["flags"]).Select(x => (string)x))}");
                if (t.PvP.ToString() != (string)want["pvp"] || t.EnterMessage != (string)want["enter"] || Territory.ColorToHex(t.Color) != (string)want["colour"]) problems.Add($"its stored PvP {t.PvP}, text '{t.EnterMessage}', colour {Territory.ColorToHex(t.Color)}");
                if (t.Revision != (int)want["revision"]) problems.Add($"its revision {t.Revision}, the admin saw {want["revision"]}");
            }
            foreach (string name in new[] { LandName, Land2Name, Town2Name, Land3Name, Town3Name, Land4Name })
                if (TerritoryStore.Territories.Any(x => x.Name == name)) problems.Add($"'{name}' is still in the store");

            try
            {
                JObject rec = file == null ? null : ((JArray)file["territories"]).OfType<JObject>().FirstOrDefault(o => (int)o["id"] == id);
                if (file == null) problems.Add($"no readable file ({readError})");
                else if (rec == null) problems.Add("the town is not in the file");
                else
                {
                    if (rec["parent"] != null) problems.Add("the town's file entry still has a 'parent'");
                    if (!(rec["flags"] is JArray list) || !new HashSet<string>(list.Select(x => (string)x)).SetEquals(((JArray)want["flags"]).Select(x => (string)x))) problems.Add($"its file 'flags' is {rec["flags"]}, wanted the list {Names(((JArray)want["flags"]).Select(x => (string)x))}");
                    if ((string)rec["pvp"] != (string)want["pvp"] || (string)rec["enterMessage"] != (string)want["enter"] || (string)rec["color"] != (string)want["colour"]) problems.Add("its file entry does not hold the baked PvP, text and colour");
                    if (((JArray)file["territories"]).OfType<JObject>().Any(o => ((string)o["name"] ?? "").StartsWith("TS Sub Land"))) problems.Add("a 'TS Sub Land' is still in the file");
                }
            }
            catch (Exception ex) { problems.Add($"the file could not be read field by field: {ex.GetType().Name}: {ex.Message}"); }
            Report.Check("S40c-sub-baked", "after its land was deleted the town is a TOP-LEVEL territory in the store and in the file, with the rules it was following written into it and its revision up by one; the land and the second pair are gone",
                problems.Count == 0, string.Join("; ", problems));
            return want;
        }

        /// <summary>R40: the restarted server loads the baked town back as the same top-level territory, and none of the lands.</summary>
        internal static void ServerRestart(JObject want)
        {
            if (!Supported) return;
            if (want == null)
            {
                Report.Fail("R40-reload-sub", "the restarted store holds the baked town as a top-level territory", "the first server expected none (server.expected has none: see S40c)");
                return;
            }
            var problems = new List<string>();
            Territory t = TerritoryStore.Get((int)want["id"]);
            if (t == null) problems.Add("the town did not load");
            else
            {
                if (t.Name != (string)want["name"]) problems.Add($"named '{t.Name}'");
                if (Parent(t) != 0 || FollowFlags(t) != TerritoryFlags.None || FollowBits(t) != 0) problems.Add("it loaded with a parent or follow marks");
                if (!new HashSet<string>(FlagNames(t.Flags)).SetEquals(((JArray)want["flags"]).Select(x => (string)x))) problems.Add($"rules {Names(FlagNames(t.Flags))}, wanted {Names(((JArray)want["flags"]).Select(x => (string)x))}");
                if (t.PvP.ToString() != (string)want["pvp"] || t.EnterMessage != (string)want["enter"] || Territory.ColorToHex(t.Color) != (string)want["colour"]) problems.Add("PvP, text or colour differ");
                if (t.Revision != (int)want["revision"]) problems.Add($"revision {t.Revision}, wanted {want["revision"]}");
            }
            foreach (string name in new[] { LandName, Land2Name, Town2Name, Land3Name, Town3Name, Land4Name })
                if (TerritoryStore.Territories.Any(x => x.Name == name)) problems.Add($"'{name}' came back");
            Report.Check("R40-reload-sub", "after the restart the baked town loads as the same top-level territory (rules, PvP, text, colour, revision), and none of the lands is back", problems.Count == 0, string.Join("; ", problems));
        }
    }
}
