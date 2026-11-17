using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HarmonyLib;
using SharedUI;
using UnityEngine;

namespace TerroritySmellatory
{
    /// <summary>
    /// The territory panel: where you are and what it means for everyone; for admins, the rules of the next
    /// territory the hammer will mark, and editing of the ones already marked.
    /// </summary>
    /// <remarks>
    /// Drawn in the VikingOS window chrome (libs-Tools/SharedUI/GiltFrameTheme.cs, compiled in), the same
    /// frame as Njord's Harbourmaster and every other Wubarrk panel.
    ///
    /// INPUT is not blocked here: StoreGui.IsVisible answers yes while the panel is open (the patch at the
    /// bottom), and vanilla asks that in every place that decides whether the player may move, look,
    /// swing, see a cursor or open the menu on Esc - Njord's proven trick, re-checked against 1.0.16's eight
    /// callers. While a text field has focus the shared UIFocus token is raised too, so typing a name does
    /// not also open the inventory or the map.
    ///
    /// LAYOUT DISCIPLINE: IMGUI lays out and then handles input in separate passes, and a control that
    /// appears or disappears between them throws. So anything that changes WHICH controls are drawn - tab,
    /// edit target, member list - runs from _pending after the layout closes, and a flag that only changes
    /// a label or a colour is applied at once; every section is always drawn, greyed out when it does not
    /// apply, never hidden. A list that is measured rather than stored (the Combine list's distances) is
    /// measured again only at a Layout event, so the event that follows it draws exactly the rows it counted;
    /// and nothing may appear or vanish on a TIMER, because the clock moves between the two passes - a
    /// click-twice button changes its face when armed, never whether it is there.
    ///
    /// GROUND (0.2.0): a territory can be several parts (Core/TerritoryGround.cs). The Place tab lists the
    /// edited territory's parts - read from the server's current record, not the private copy - and arms the
    /// hammer to move one of them or to add ground: ReshapeId + ReshapePart, or AddPartTo, never both. A move
    /// is used up by the placement that sends it. Adding ground stays armed until Cancel, another arming or
    /// leaving the world, because a shape is built from placement after placement and Infinity Hammer keeps
    /// the tool in hand. Neither lapses on its own into marking a new territory with the draft's rules: an
    /// arming whose territory or part has gone is refused, placement after placement, until it is cancelled
    /// or picked again. A part is named by its number, which removing or splitting off an earlier part
    /// changes, so every part request carries the revision it was counted in and the server refuses it when
    /// that is out of date (Protocol.Request.BaseRevision). Combining is asked of the server outright ("Join
    /// into this"), or picked with the hammer's Territory combine tool (Integration/Commands.cs).
    ///
    /// EDITING works on a private copy, so nothing reaches players until Save. The server can change the same
    /// territory meanwhile - a part added, another territory joined in (bringing its owner and members), a
    /// second admin's save - so when its revision moves on, the copy is carried onto the new record
    /// (PanelMath.Rebase): what was changed here is kept, the rest is the server's. Without that, the first
    /// Save after "Join into this" would quietly drop every member the join brought in.
    ///
    /// SUB-TERRITORIES (0.4.0): a territory may sit inside another and follow it (Core/Inheritance.cs). The records the
    /// panel holds are EFFECTIVE - what is followed is already filled in, the marks kept - so a followed row shows the
    /// parent's value, and the copy being edited is brought up to date with the parent whenever the list changes (Resync).
    /// Every setting row ends in the marks column, "follows" or "own", drawn by one helper (MarkCell) so it lands on the
    /// same x in every row; it exists only when the territory has a parent, and a top-level territory's panel has none. A
    /// draft's parent is Territory.AutoParent until the admin picks one - the server settles it from where the ground is
    /// marked - so no parent's value is known, and a followed row says "follows" where it would say ON or off: no row lies.
    /// A click that changes a followed row makes it the territory's own first (Touch); "own" is clicked to follow again.
    /// The Inside row and its picker follow LAYOUT DISCIPLINE: the picker opens and closes from _pending, and refused
    /// parents are listed, greyed, with the reason.
    /// </remarks>
    internal static class TerritoryPanel
    {
        private enum Tab
        {
            Here,
            All,
            Rules,
            Members,
            Place
        }

        private const string FocusId = "TerroritySmellatory.Panel";

        private static readonly Color32[] Palette =
        {
            new Color32(214, 170, 72, 255),   // gilt
            new Color32(200, 70, 60, 255),    // blood
            new Color32(225, 130, 50, 255),   // ember
            new Color32(95, 170, 80, 255),    // meadow
            new Color32(60, 165, 160, 255),   // fjord
            new Color32(75, 120, 210, 255),   // sea
            new Color32(150, 95, 200, 255),   // heather
            new Color32(215, 215, 205, 255)   // bone
        };

        internal static bool IsOpen { get; private set; }

        /// <summary>The rules the next hammer placement (or terr_here) creates a territory with.</summary>
        internal static Territory NewDraft { get; private set; } = FreshDraft();

        /// <summary>A private copy of the placed territory being edited, or null when editing the draft.</summary>
        private static Territory _editing;

        /// <summary>The server's record as <see cref="_editing"/> was last taken from it: what "changed here" is measured against.</summary>
        private static Territory _editingBase;

        /// <summary>
        /// When set, the next placement moves part <see cref="ReshapePart"/> of this territory instead of creating one -
        /// or, while that part or the territory has gone, is refused, and it stays set.
        /// </summary>
        internal static int ReshapeId { get; private set; }

        /// <summary>Which part a reshape moves, as it was numbered when armed (0 = the first).</summary>
        internal static int ReshapePart { get; private set; }

        /// <summary>When set, every placement becomes a new part of this territory instead of creating one - until Cancel, another arming, or leaving the world.</summary>
        internal static int AddPartTo { get; private set; }

        /// <summary>The armed part as it was when picked - how it is found again if parts before it went meanwhile.</summary>
        private static TerritoryShape _reshapeShape;

        /// <summary>
        /// Which territory <see cref="_reshapeShape"/> was taken from. An arming made without picking a part (the
        /// e2e rig sets ReshapeId itself, as 0.1.x's panel did) has none, and means part ReshapePart as numbered.
        /// </summary>
        private static int _reshapeShapeFor;

        internal static float HereRadius { get; private set; } = 20f;

        private static Tab _tab = Tab.Here;
        private static Rect _rect;
        private static Vector2 _scroll;
        private static Action _pending;
        private static bool _typing;
        private static string _manualId = "";
        private static string _manualName = "";
        private static string _localNote = "";
        private static float _localNoteAt = -1000f;

        /// <summary>The list revision the copies being edited were last brought up to date with their parents at (Resync).</summary>
        private static int _syncedRevision = -1;

        /// <summary>What a row may use of the scroll view's width, measured once per OnGUI: the body less the vertical scroll bar.</summary>
        private static float _contentW;

        /// <summary>The part the edit tool (or the Here tab) found the admin in: lit in the Ground list.</summary>
        private static bool _hasHighlight;
        private static int _highlightFor;
        private static TerritoryShape _highlight;

        private static Territory Target => _editing ?? NewDraft;

        // DraftFlags, not DefaultFlags: a new territory that keeps monsters from spawning starts with the runic barrier
        // on as well (0.3.0). Existing territories are never touched - they are edited from the server's own record.
        // 0.4.0: and it starts as a copy of whatever it is marked inside - AutoParent is the server's word for "the territory
        // that governs the new ground's middle, or none" - following every setting until the admin changes one. Marked in
        // open country, the server drops the marks and the draft's own values stand: these.
        private static Territory FreshDraft() => new Territory
        {
            Name = "",
            Flags = Territory.DraftFlags,
            ParentId = Territory.AutoParent,
            FollowFlags = Territory.KnownFlags,
            Follow = TerritoryFollow.All
        };

        // ─────────────────────────────────────────────────────────────
        // OPEN / CLOSE
        // ─────────────────────────────────────────────────────────────

        internal static void Toggle()
        {
            if (IsOpen) Close();
            else Open();
        }

        internal static void Open()
        {
            if (Player.m_localPlayer == null) return;
            IsOpen = true;
            _scroll = Vector2.zero;
            _rect = default;
            _pickerOpen = false;
            if (!IsAdminView && _tab > Tab.All) _tab = Tab.Here;
        }

        /// <summary>The frame the panel closed on - see StoreGuiIsVisiblePatch.</summary>
        internal static int ClosedFrame { get; private set; } = -10;

        internal static void Close()
        {
            if (IsOpen) ClosedFrame = Time.frameCount;
            IsOpen = false;
            _typing = false;
            _pending = null;
            _pickerOpen = false;
            GUIUtility.keyboardControl = 0;
            UIFocus.SetHasTextFocus(FocusId, false);
        }

        private static bool IsAdminView => TerritoryClient.IsAdmin;

        /// <summary>Driven from Plugin.Update - input tokens must be raised from Update, never OnGUI (UIFocus gotcha 3).</summary>
        internal static void Tick()
        {
            KeepInStep();

            Player player = Player.m_localPlayer;
            if (IsOpen && (player == null || ZNet.instance == null || player.IsDead()))
            {
                Close();
                return;
            }

            if (!_typing && player != null && TSConfig.PanelKey.Value.IsDown() && CanToggle()) Toggle();
            else if (IsOpen && ZInput.GetKeyDown(KeyCode.Escape)) Close();   // the same read Menu.Update makes

            // Admin tabs follow the SERVER's word, which can change while the panel is open (adminlist.txt
            // edited, permissions reloaded). Moved here, in Update, never mid-OnGUI.
            if (IsOpen && !IsAdminView && _tab > Tab.All) _tab = Tab.Here;

            UIFocus.SetHasTextFocus(FocusId, IsOpen && _typing);
        }

        /// <summary>
        /// Keeps the edit copy and the arming true to the territory list. Every change here can change which
        /// controls exist, which is why it runs from Update and never mid-OnGUI.
        /// </summary>
        private static void KeepInStep()
        {
            // Out of the world: ids belong to the world they were given in, and the next world has its own #3.
            // An edit or an arming carried across would save onto, or move, a stranger's territory.
            if (ZNet.instance == null)
            {
                if (_editing != null || IsArmed || _hasHighlight)
                {
                    EndEdit();
                    Disarm();
                }
                // The draft's parent is an id too: the next world's #3 is a stranger, and a territory marked inside it would follow it.
                if (NewDraft.ParentId > 0) PutInside(NewDraft, Territory.AutoParent);
                _pickerOpen = false;
                _syncedRevision = -1;
                return;
            }

            if (_editing != null && _editingBase != null)
            {
                Territory live = TerritoryIndex.Find(_editing.Id);
                if (live != null && live.Revision != _editingBase.Revision)
                {
                    _editing = PanelMath.Rebase(_editing, _editingBase, live);
                    _editingBase = live.Clone();
                    // A click-twice button armed on the old record could name a different part or neighbour now.
                    _confirmKey = "";
                }
            }

            // Whenever the list changes - this territory's, its parent's, or any above - what a copy follows is the parent's
            // CURRENT values, so a followed row always shows what is in force (Inheritance.Refresh).
            if (_syncedRevision != TerritoryIndex.LocalRevision)
            {
                _syncedRevision = TerritoryIndex.LocalRevision;
                SyncWithParents();
            }

            // An arming whose territory has since gone - deleted, or joined into another - is NOT dropped here.
            // Dropped, it would hand the tool back to marking new territories, and the admin's next placement,
            // still aimed at the territory they armed, would create one with the draft's rules and send it to
            // every player. Kept, each placement is sent and the server refuses it ("There is no territory #N"),
            // as 0.1.1 refused a stale move, until the admin cancels; the Armed line says so meanwhile. Ids are
            // never reused (the store's NextId only grows), so it cannot land on another territory. Leaving the
            // world still disarms, above.
        }

        private static bool CanToggle()
        {
            if (Console.IsVisible() || TextInput.IsVisible() || Menu.IsVisible()) return false;
            if (!IsOpen && (InventoryGui.IsVisible() || Minimap.IsOpen() || StoreGui.IsVisible())) return false;
            if (Chat.instance != null && Chat.instance.HasFocus()) return false;
            return true;
        }

        // ─────────────────────────────────────────────────────────────
        // PLACEMENT - what terr_place / terr_here hand over, and what the next one is armed to do
        // ─────────────────────────────────────────────────────────────

        internal static bool IsArmed => ReshapeId != 0 || AddPartTo != 0;

        internal static void Disarm()
        {
            ReshapeId = 0;
            ReshapePart = 0;
            AddPartTo = 0;
            _reshapeShapeFor = 0;
        }

        private static void ArmReshape(Territory t, int part)
        {
            Disarm();
            ReshapeId = t.Id;
            ReshapePart = part;
            _reshapeShape = t.GetPart(part);
            _reshapeShapeFor = t.Id;
        }

        private static void ArmAddPart(Territory t)
        {
            Disarm();
            AddPartTo = t.Id;
        }

        /// <summary>
        /// Where the part armed to move is NOW. Removing or splitting off a part before it renumbers it, and the
        /// panel can do both while the hammer is out. -1 when it is no longer there as it was picked.
        /// </summary>
        private static int ArmedPartIndex()
        {
            Territory live = TerritoryIndex.Find(ReshapeId);
            if (live == null || _reshapeShapeFor != ReshapeId) return ReshapePart;   // gone (the server says so), or no part was picked
            for (int i = 0; i < live.PartCount; i++)
                if (live.GetPart(i).SameAs(_reshapeShape)) return i;
            return -1;
        }

        /// <summary>What the next placement does, to finish "the next placement ..."; null when nothing is armed.</summary>
        /// <remarks>The one line an admin checks before placing, so it says what the placement will really meet - a refusal included.</remarks>
        internal static string ArmedAction
        {
            get
            {
                int armedFor = AddPartTo != 0 ? AddPartTo : ReshapeId;
                if (armedFor == 0) return null;
                // Still armed for a territory that has gone: KeepInStep keeps it on purpose, and the server refuses it.
                Territory live = TerritoryIndex.Find(armedFor);
                if (live == null && TerritoryClient.HasSnapshot) return $"is refused: #{armedFor} no longer exists - Cancel";
                if (AddPartTo != 0)
                {
                    // Adding ground outlasts placements, so it can outlast the room for them; MaxParts is the server's
                    // (a synced setting), and the server refuses a part over it.
                    int maxParts = Mathf.Max(1, TSConfig.MaxParts.Value);
                    return live != null && live.PartCount >= maxParts
                        ? $"is refused: {Named(AddPartTo)} has the most parts a territory may have here ({maxParts}) - Cancel"
                        : $"adds ground to {Named(AddPartTo)}, as does every one after it until you Cancel";
                }
                return ArmedPartChanged
                    ? $"moves nothing: the part picked in {Named(ReshapeId)} has changed - pick it again, or Cancel"
                    : $"moves {ArmedPartName()}";
            }
        }

        /// <summary>A move is armed, but its part is no longer there as it was picked: a placement would move nothing.</summary>
        /// <remarks>ArmedPartIndex answers ReshapePart, not -1, for a territory that has gone or a move with no part picked, so only this trips it.</remarks>
        private static bool ArmedPartChanged => ReshapeId != 0 && ArmedPartIndex() < 0;

        /// <summary>"part 2 of 'Harbour' (#5)", or "'Harbour' (#5)" for a territory of one part.</summary>
        private static string ArmedPartName()
        {
            Territory live = TerritoryIndex.Find(ReshapeId);
            int index = ArmedPartIndex();
            // A part that has changed keeps the number it was picked by, even with one part left: without it, the
            // name would read as moving the whole territory.
            if (index >= 0 && (live == null || live.PartCount < 2)) return Named(ReshapeId);
            return $"part {(index >= 0 ? index : ReshapePart) + 1} of {Named(ReshapeId)}";
        }

        private static string Named(int id)
        {
            Territory t = TerritoryIndex.Find(id);
            return t != null ? $"'{t.DisplayName}' (#{id})" : $"#{id}";
        }

        /// <summary>A marked area arrived: add it to, or move a part of, the armed territory - or create one from the draft.</summary>
        internal static void SubmitPlacement(Territory geometry)
        {
            if (AddPartTo != 0)
            {
                // Stays armed. Infinity Hammer keeps a tool in hand after a placement (it drops only a single-use
                // selection, and a tool never is one), and a shape is built from several placements in a row:
                // disarmed here, the second part would quietly become a new territory with the draft's rules.
                // Cancel, another arming or leaving the world ends it.
                int id = AddPartTo;
                var part = new Territory { Id = id };
                part.CopyGeometryFrom(geometry);
                TerritoryClient.Send(RequestOp.AddPart, part, id);
                return;
            }

            if (ReshapeId != 0)
            {
                int id = ReshapeId;
                int index = ArmedPartIndex();
                if (index < 0)
                {
                    // Moved or removed since it was picked: moving whatever now has its number would be a guess. It
                    // stays armed, so the placement after this one is refused too rather than marking a new
                    // territory - until the part is picked again, or Cancel.
                    MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, "That part has changed - pick it again, or Cancel");
                    TerritoryClient.Print($"Nothing moved: {ArmedPartName()} has changed since it was picked. Pick it again on the panel's Place tab, or Cancel there.");
                    return;
                }
                // Used up by this placement - unless the territory has gone, when it is sent all the same, the server
                // refuses it, and it stays armed like any other arming that has gone (KeepInStep).
                Territory live = TerritoryIndex.Find(id);
                if (live != null) Disarm();

                // With the revision of the list the part was just counted in. A removal still on its way, or a second
                // admin, can renumber the parts before this arrives, and the server then refuses it rather than move
                // whichever part has taken the number. 0 (the territory has gone) is not checked: the server's
                // "There is no territory" says it better.
                int seen = live?.Revision ?? 0;
                var record = new Territory { Id = id };
                record.CopyGeometryFrom(geometry);
                TerritoryClient.Send(RequestOp.Reshape, record, id, part: index, baseRevision: seen);
                return;
            }

            Territory t = NewDraft.Clone();
            t.Id = 0;
            t.CopyGeometryFrom(geometry);
            TerritoryClient.Send(RequestOp.Create, t);
        }

        /// <summary>A circle of the panel's radius centred where the player stands; null out of the world.</summary>
        private static Territory CircleHere()
        {
            Player player = Player.m_localPlayer;
            if (player == null) return null;
            Vector3 p = player.transform.position;
            return new Territory
            {
                Kind = TerritoryShapeKind.Circle,
                X = p.x,
                Y = p.y,
                Z = p.z,
                HalfX = HereRadius,
                HalfZ = HereRadius
            };
        }

        /// <summary>"Mark a circle here": whatever the next placement is armed to do, done with that circle.</summary>
        private static void PlaceCircleHere()
        {
            Territory circle = CircleHere();
            if (circle != null) SubmitPlacement(circle);
        }

        /// <summary>"Add a circle here": a new part for this territory, whatever else is armed.</summary>
        private static void AddCircleHere(int id)
        {
            Territory circle = CircleHere();
            if (circle == null) return;
            circle.Id = id;
            TerritoryClient.Send(RequestOp.AddPart, circle, id);
        }

        private static void Note(string text)
        {
            _localNote = text;
            _localNoteAt = Time.realtimeSinceStartup;
        }

        // ─────────────────────────────────────────────────────────────
        // DRAW
        // ─────────────────────────────────────────────────────────────

        internal static void OnGUI()
        {
            if (!IsOpen) return;

            GiltFrameTheme.EnsureBuilt(GiltFrameTheme.Gold, TSConfig.PanelScale.Value);
            EnsureStyles();

            float w = Mathf.Min(GiltFrameTheme.S(660f), Screen.width - 40f);
            float h = Mathf.Min(GiltFrameTheme.S(740f), Screen.height - 40f);
            if (_rect.width != w || _rect.height != h)
                _rect = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);

            GiltFrameTheme.DrawWindow(_rect, "Territories");
            Rect body = GiltFrameTheme.Body(_rect);
            GUIStyle bar = GUI.skin.verticalScrollbar;
            _contentW = Mathf.Floor(body.width) - (bar.fixedWidth + bar.margin.left) - 4f;   // 4 px of doubt: a row one pixel too wide is a sideways scroll bar

            GUILayout.BeginArea(body);
            GUILayout.BeginVertical();

            DrawTabs();
            GUILayout.Space(GiltFrameTheme.S(6f));

            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));
            switch (_tab)
            {
                case Tab.Here: DrawHere(); break;
                case Tab.All: DrawAll(); break;
                case Tab.Rules: DrawRules(); break;
                case Tab.Members: DrawMembers(); break;
                case Tab.Place: DrawPlace(); break;
            }
            GUILayout.EndScrollView();

            DrawStatus();

            GUILayout.EndVertical();
            GUILayout.EndArea();

            GUI.Label(GiltFrameTheme.FooterLine(_rect), $"{TSConfig.PanelKey.Value} or Esc closes.", GiltFrameTheme.Footer);

            // A focused text field owns the keyboard; read here, acted on from Update next frame.
            _typing = GUIUtility.keyboardControl != 0;

            // Everything that changes which controls exist runs only now, after the layout has closed.
            if (_pending != null)
            {
                Action action = _pending;
                _pending = null;
                try { action(); }
                catch (Exception ex) { Plugin.Log.LogWarning($"Territory panel action failed: {ex}"); }
            }
        }

        private static void DrawTabs()
        {
            GUILayout.BeginHorizontal();
            TabButton(Tab.Here, "Here", first: true);
            TabButton(Tab.All, "All");
            if (IsAdminView)
            {
                TabButton(Tab.Rules, "Rules");
                TabButton(Tab.Members, "Members");
                TabButton(Tab.Place, "Place");
            }
            GUILayout.EndHorizontal();
        }

        private static void TabButton(Tab tab, string label, bool first = false)
        {
            if (!first) GUILayout.Space(Gap);
            if (GUILayout.Button(label, _tab == tab ? _btnOn : _btn))
            {
                Tab to = tab;
                _pending = () =>
                {
                    _tab = to;
                    _scroll = Vector2.zero;
                };
            }
        }

        // ─────────────────────────────────────────────────────────────
        // HERE - everyone
        // ─────────────────────────────────────────────────────────────

        private static void DrawHere()
        {
            Player player = Player.m_localPlayer;
            if (player == null) return;

            if (!TerritoryClient.HasSnapshot)
            {
                GUILayout.Label("Waiting for the server's territory list.", _hint);
                GUILayout.Label("It arrives once your character is in the world. If it never does, the server may not be running TerroritySmellatory.", _hint);
                return;
            }

            Territory t = Presence.Current;
            if (t == null)
            {
                GUILayout.Label("Open country", _head);
                GUILayout.Label("No territory governs where you stand.", _hint);
                Territory near = TerritoryIndex.Nearest(player.transform.position, out float dist);
                if (near != null)
                {
                    GUILayout.Space(GiltFrameTheme.S(6f));
                    GUILayout.Label($"Nearest: {near.DisplayName}, {dist:0} m away.", _wrap);
                }
                return;
            }

            DrawTerritoryCard(t, player);

            if (IsAdminView)
            {
                GUILayout.Space(GiltFrameTheme.S(10f));
                if (GUILayout.Button($"Edit '{t.DisplayName}'", _act))
                {
                    Territory pick = t;
                    int part = t.Ground.PartAt(player.transform.position);
                    _pending = () => BeginEdit(pick, part);
                }
            }
        }

        private static void DrawTerritoryCard(Territory t, Player player)
        {
            GUILayout.BeginHorizontal();
            Swatch(t.Color);
            GUILayout.Space(Gap);
            GUILayout.Label(t.DisplayName, _head);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.Label($"{t.Ground.Describe()}, #{t.Id}", _hintCard);
            string inside = PanelMath.InsideChain(TerritoryIndex.All, t);
            if (inside.Length > 0) GUILayout.Label($"Inside: {inside}", _hintCard);
            GUILayout.Label($"Owner: {(string.IsNullOrEmpty(t.OwnerName) ? "none" : t.OwnerName)}", _wrap);
            GUILayout.Label($"Members: {(t.Members.Count == 0 ? "none" : string.Join(", ", t.Members.Select(m => m.Name)))}", _wrap);

            long me = player.GetPlayerID();
            string standing = me == t.OwnerId ? "You own this territory."
                : t.IsMember(me) ? "You are a member."
                : TerritoryClient.Bypass ? "You are not a member - as an admin, its protections do not stop you."
                : "You are a visitor: its protections apply to you.";
            GUILayout.Label(standing, _status);

            GUILayout.Space(GiltFrameTheme.S(6f));
            List<string> lines = t.FeatureLines(TSConfig.AutoCloseDelay.Value).ToList();
            if (lines.Count == 0) GUILayout.Label("No rules - a named place only.", _hint);
            foreach (string line in lines) GUILayout.Label(line, _item);
        }

        // ─────────────────────────────────────────────────────────────
        // ALL - everyone
        // ─────────────────────────────────────────────────────────────

        private static void DrawAll()
        {
            Player player = Player.m_localPlayer;
            if (player == null) return;

            IReadOnlyList<Territory> all = TerritoryIndex.All;
            if (all.Count == 0)
            {
                GUILayout.Label(TerritoryClient.HasSnapshot ? "This world has no territories yet." : "Waiting for the server's territory list.", _hint);
                return;
            }

            // A tree (0.4.0): each territory is followed by the ones inside it, a step in per level, siblings by name. The
            // order does not depend on where the player stands, so it is kept until the list changes (TreeRows).
            Vector3 me = player.transform.position;
            List<Inheritance.TreeRow> tree = TreeRows(all);
            for (int i = 0; i < tree.Count; i++)
            {
                Territory t = tree[i].Territory;
                float indent = tree[i].Depth * IndentW;

                GUILayout.BeginHorizontal();
                if (indent > 0f) GUILayout.Space(indent);
                Swatch(t.Color);
                GUILayout.Space(Gap);
                GUILayout.Label(t.DisplayName, _head, GUILayout.MinWidth(0f));
                GUILayout.FlexibleSpace();
                float d = t.Ground.DistanceOutside(me);
                GUILayout.Label(d <= 0f ? "you are here" : $"{d:0} m", _key);
                if (IsAdminView)
                {
                    GUILayout.Space(Gap);
                    if (GUILayout.Button("Edit", _btn, GUILayout.Width(GiltFrameTheme.S(70f))))
                    {
                        Territory pick = t;
                        _pending = () => BeginEdit(pick, -1);
                    }
                }
                GUILayout.EndHorizontal();

                string owner = string.IsNullOrEmpty(t.OwnerName) ? "no owner" : "owner " + t.OwnerName;
                string within = _treeParents[i] != null ? $" - inside {_treeParents[i]}" : "";
                string line = $"#{t.Id} - {t.Ground.Describe()}{within} - {owner} - {t.Members.Count} member(s)";
                if (indent > 0f)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Space(indent);
                    GUILayout.Label(line, _hintCard);
                    GUILayout.EndHorizontal();
                }
                else
                {
                    GUILayout.Label(line, _hintCard);
                }
                GUILayout.Space(GiltFrameTheme.S(4f));
            }
        }

        private static List<Inheritance.TreeRow> _treeRows;
        private static List<string> _treeParents;
        private static int _treeRevision = -1;

        /// <summary>
        /// The All tab's rows in tree order, and beside each the name of the territory it sits in (null for a top-level one).
        /// Rebuilt only when the list changes, and only at a Layout event, so the event that follows draws exactly the rows
        /// the layout counted (LAYOUT DISCIPLINE).
        /// </summary>
        private static List<Inheritance.TreeRow> TreeRows(IReadOnlyList<Territory> all)
        {
            bool layout = Event.current != null && Event.current.type == EventType.Layout;
            if (_treeRows == null || (_treeRevision != TerritoryIndex.LocalRevision && layout))
            {
                _treeRows = Inheritance.TreeOrder(all, PanelMath.ByNameThenId);
                var names = new Dictionary<int, string>(all.Count);
                foreach (Territory t in all) names[t.Id] = t.DisplayName;
                _treeParents = new List<string>(_treeRows.Count);
                foreach (Inheritance.TreeRow row in _treeRows)
                    _treeParents.Add(row.Territory.ParentId > 0 && names.TryGetValue(row.Territory.ParentId, out string parentName) ? parentName : null);
                _treeRevision = TerritoryIndex.LocalRevision;
            }
            return _treeRows;
        }

        // ─────────────────────────────────────────────────────────────
        // RULES - admins
        // ─────────────────────────────────────────────────────────────

        /// <param name="part">The part to light in the Ground list (where the edit tool landed), or -1.</param>
        private static void BeginEdit(Territory t, int part)
        {
            Territory live = TerritoryIndex.Find(t.Id) ?? t;
            _editing = live.Clone();
            _editingBase = live.Clone();
            _confirmKey = "";
            _pickerOpen = false;
            _pickNote = "";
            _hasHighlight = part >= 0 && part < live.PartCount;
            if (_hasHighlight)
            {
                _highlightFor = live.Id;
                _highlight = live.GetPart(part);
            }
            _tab = Tab.Rules;
            _scroll = Vector2.zero;
        }

        private static void EndEdit()
        {
            _editing = null;
            _editingBase = null;
            _confirmKey = "";
            _pickerOpen = false;
            _pickNote = "";
            _hasHighlight = false;
            _scroll = Vector2.zero;
        }

        /// <summary>The server's current record of the territory being edited, or null (the draft, or gone from the server).</summary>
        private static Territory EditingLive => _editing != null ? TerritoryIndex.Find(_editing.Id) : null;

        private static void DrawRules()
        {
            Territory t = Target;
            DrawEditingBanner();

            GUILayout.Label("Name", _key);
            t.Name = Territory.CleanName(GUILayout.TextField(t.Name ?? "", Territory.MaxNameLength, _field), Territory.MaxNameLength, trim: false);
            if (string.IsNullOrEmpty(t.Name)) GUILayout.Label(_editing == null ? "Left empty, the server names it 'Territory <id>'." : " ", _hint);

            DrawInside(t);

            GUILayout.Space(GiltFrameTheme.S(6f));
            DrawColour(t);

            t.Priority = Mathf.RoundToInt(Stepper("Priority", t.Priority, 1f, -1000f, 1000f, "0",
                "Where territories overlap, higher priority rules; on a tie the smaller one does. A sub-territory always rules its own ground over the one it is inside.",
                mark: Mark.Of(t, TerritoryFollow.Priority)));

            Section("Protection - applies to everyone who is not a member");
            Flag(t, TerritoryFlags.BuildProtect, "Building", "Placing, removing, repairing and digging.");
            Flag(t, TerritoryFlags.AccessProtect, "Doors, chests & stations", "Everything a ward locks: doors, chests, portals, signs, stands, fermenters, beehives...");
            Flag(t, TerritoryFlags.HarvestProtect, "Harvesting", "Crops, berries, mushrooms and other pickables, and mining world pieces (black marble, grausten, ruins) where buildings cannot be damaged.");

            // Not under Protection: that section's heading says "who is not a member", and a door closes whoever opened it.
            // It is the first row of World so it sits just below the doors rule it goes with.
            Section("World");
            Flag(t, TerritoryFlags.AutoCloseDoors, "Doors close by themselves", DoorHint());
            Flag(t, TerritoryFlags.StructureProtect, "Buildings cannot be damaged", "No hit harms what players built: creatures, fire and every player, members and admins included. Take a piece down with the hammer instead. Weather and support still apply. World pieces (black marble, grausten, ruins) can still be mined, as Harvesting allows.");
            Flag(t, TerritoryFlags.NoMonsterSpawns, "No monster spawns", "World spawns, raid spawns, spawn points and nests. Animals still spawn.");
            // Two steps in, each under the label above it and each greyed - never hidden - while what it needs is off.
            SubFlag(t, TerritoryFlags.RunicBarrier, Applies(t, TerritoryFlags.NoMonsterSpawns), 1, "Runic barrier",
                "Monsters cannot walk in: they are held at the edge, and the barrier flares blue where they push. Tamed creatures and peaceful animals pass.");
            SubFlag(t, TerritoryFlags.BarrierKeepsOutVisitors, Applies(t, TerritoryFlags.NoMonsterSpawns) && Applies(t, TerritoryFlags.RunicBarrier), 2, "Keep non-members out too",
                "Players who are not members are held at the edge as well. Members and admins pass.");
            Flag(t, TerritoryFlags.NoRaids, "No raids", "A raid never picks a player standing here.");
            Flag(t, TerritoryFlags.Guards, "Guards", GuardHint());

            Section("Players inside");
            Flag(t, TerritoryFlags.NoSkillLoss, "No skill loss on death", null);

            DrawPvP(t);
            GUILayout.Label("Forced: the inventory's PvP toggle locks while a player is inside, and their own choice returns when they leave.", _hint);

            Flag(t, TerritoryFlags.Sanctuary, "Sanctuary effect", "A status effect naming the territory, with the bonuses below.");
            bool was = GUI.enabled;
            GUI.enabled = was && Applies(t, TerritoryFlags.Sanctuary);
            // The five numbers are ONE group: touching any of them makes all five the territory's own.
            Mark values = Mark.Of(t, TerritoryFollow.SanctuaryValues);
            t.HealthRegen = Stepper("Health regen", t.HealthRegen, 0.25f, 0.25f, 5f, "x0.##", null, indent: true, mark: values);
            t.StaminaRegen = Stepper("Stamina regen", t.StaminaRegen, 0.25f, 0.25f, 5f, "x0.##", null, indent: true, mark: values);
            t.EitrRegen = Stepper("Eitr regen", t.EitrRegen, 0.25f, 0.25f, 5f, "x0.##", null, indent: true, mark: values);
            t.CarryBonus = Stepper("Carry weight", t.CarryBonus, 25f, 0f, 1000f, "+0", null, indent: true, mark: values);
            t.ArmorBonus = Stepper("Armour", t.ArmorBonus, 5f, 0f, 200f, "+0", null, indent: true, mark: values);
            GUI.enabled = was;

            Section("Messages & map");
            Flag(t, TerritoryFlags.Announce, "Entry / exit banner", "{name} and {owner} are filled in. A PvP change shows on the banner's second line.");
            GUI.enabled = was && Applies(t, TerritoryFlags.Announce);
            GUILayout.Label("On entry (empty: \"Entering {name}\")", _hintFlag);
            t.EnterMessage = IndentedField(t.EnterMessage, Mark.Of(t, TerritoryFollow.EnterText));
            GUILayout.Label("On exit (empty: \"Leaving {name}\")", _hintFlag);
            t.LeaveMessage = IndentedField(t.LeaveMessage, Mark.Of(t, TerritoryFollow.LeaveText));
            GUI.enabled = was;
            Flag(t, TerritoryFlags.ShowOnMap, "Show on the map", "Its outline and name on the large map and the minimap.");
            SubFlag(t, TerritoryFlags.MapNameOnly, Applies(t, TerritoryFlags.ShowOnMap), 1, "Name only",
                "Just its name on the maps and in the minimap's biome label, with no outline or tint. For a continent or a region, which would colour the whole map.");
            Flag(t, TerritoryFlags.ShowBorder, "Show its border on the ground", "Players near the edge see where it runs. Off keeps it hidden from all but admins with this panel open.");

            GUILayout.Space(GiltFrameTheme.S(12f));
            DrawRuleActions();
        }

        // The hint quotes the server's own settings (synced to every client), so it is rebuilt only when they change.
        private static string _doorHint;
        private static float _doorHintDelay = -1f, _doorHintClearance = -1f;

        private static string DoorHint()
        {
            float delay = TSConfig.AutoCloseDelay.Value, clearance = TSConfig.AutoClosePlayerClearance.Value;
            if (_doorHint == null || delay != _doorHintDelay || clearance != _doorHintClearance)
            {
                _doorHintDelay = delay;
                _doorHintClearance = clearance;
                string near = clearance > 0f ? $", unless a player is within {clearance.ToString("0.#", CultureInfo.InvariantCulture)} m" : "";
                _doorHint = $"A door or gate left open shuts after {delay.ToString("0.#", CultureInfo.InvariantCulture)} s{near}, whoever opened it. Drawbridges and window shutters are left alone. Times are set by the server.";
            }
            return _doorHint;
        }

        // Like the door hint: it quotes the server's synced numbers, so it is rebuilt only when they change.
        private static string _guardHint;
        private static int _guardHintPer = -1, _guardHintMax = -1;
        private static string _guardHintWords;

        private static string GuardHint()
        {
            int per = TSConfig.GuardsPerPart.Value, max = TSConfig.MaxGuardsPerTerritory.Value;
            string words = TSConfig.GuardCallWords.Value ?? "";
            if (_guardHint == null || per != _guardHintPer || max != _guardHintMax || words != _guardHintWords)
            {
                _guardHintPer = per;
                _guardHintMax = max;
                _guardHintWords = words;
                string call = GuardMath.FirstWord(words);
                _guardHint = $"Guards posted round its middle ({per} a part, at most {max}). They fight monsters that come into sight, cannot be hurt and never turn on a player." +
                             (call.Length > 0 ? $" Type \"{call}\" in chat inside and the nearest comes to fight what is after you." : "");
            }
            return _guardHint;
        }

        private static void DrawEditingBanner()
        {
            GUILayout.BeginHorizontal();
            if (_editing == null)
            {
                GUILayout.Label("The next territory you mark", _head);
                GUILayout.FlexibleSpace();
            }
            else
            {
                Territory shown = EditingLive ?? _editing;
                string ground = shown.PartCount > 1 ? $"{shown.PartCount} parts" : shown.Shape.Describe();
                GUILayout.Label($"Editing #{_editing.Id} - {ground}", _head, GUILayout.MinWidth(0f), GUILayout.ExpandWidth(true));
                GUILayout.Space(Gap);
                if (GUILayout.Button("Back to new", _btn, GUILayout.Width(GiltFrameTheme.S(130f))))
                    _pending = EndEdit;
            }
            GUILayout.EndHorizontal();
            if (_editing != null && EditingLive == null)
                GUILayout.Label("This territory no longer exists on the server.", _hint);
            GUILayout.Space(GiltFrameTheme.S(4f));
        }

        private static void ResetDraft()
        {
            NewDraft = FreshDraft();
            _pickerOpen = false;
            _pickNote = "";
        }

        private static void DrawRuleActions()
        {
            // How many territories sit inside this one decides how Delete is offered: with any, it says what happens to them,
            // and a second button takes them along (0.4.0). Counted from the list, which only changes between frames.
            int id = _editing != null ? _editing.Id : 0;
            int inside = _editing != null ? Inheritance.Descendants(TerritoryIndex.All, id).Count : 0;

            GUILayout.BeginHorizontal();
            if (_editing == null)
            {
                if (GUILayout.Button("Mark it with the hammer", _actMain)) _pending = StartNewTerritory;
                GUILayout.Space(Gap);
                if (GUILayout.Button("Reset these rules", _act)) _pending = ResetDraft;
            }
            else
            {
                if (GUILayout.Button("Save changes", _actMain))
                {
                    Territory send = _editing.Clone();
                    _pending = () => TerritoryClient.Send(RequestOp.Update, send, send.Id);
                }
                GUILayout.Space(Gap);
                // Greyed once the territory has gone from the server (deleted, or joined into another while this
                // stayed open): a move armed for it could only be refused. Greyed, not hidden (LAYOUT DISCIPLINE).
                bool was = GUI.enabled;
                GUI.enabled = was && EditingLive != null;
                if (GUILayout.Button("Move / reshape", _act))
                {
                    // One part moves as it always did. Several: moving "the territory" would move part 1 and leave
                    // the rest behind, so the Place tab's Ground list asks which.
                    Territory shown = EditingLive ?? _editing;
                    if (shown.PartCount > 1)
                    {
                        _pending = () =>
                        {
                            _tab = Tab.Place;
                            _scroll = Vector2.zero;
                        };
                    }
                    else
                    {
                        _pending = () => StartMovePart(shown, 0);
                    }
                }
                GUI.enabled = was;

                // A territory with nothing inside it keeps its Delete in this row, as ever.
                if (inside == 0)
                {
                    GUILayout.Space(Gap);
                    if (ConfirmButton($"delete {id}", "Delete", "Click again to delete", _act, _actMain))
                    {
                        _pending = () =>
                        {
                            TerritoryClient.Send(RequestOp.Delete, null, id);
                            EndEdit();
                        };
                    }
                }
            }
            GUILayout.EndHorizontal();

            // With territories inside, both deletions get a row of their own, so each face can say what it does. The plain
            // one leaves them standing, each with the rules it had, one level up (the server's Delete); the other removes the
            // whole tree (DeleteTree).
            if (_editing != null && inside > 0)
            {
                if (ConfirmButton($"delete {id}", PanelMath.DeleteKeepsLabel(inside), "Click again to delete", _act, _actMain, GUILayout.MinWidth(0f)))
                {
                    _pending = () =>
                    {
                        TerritoryClient.Send(RequestOp.Delete, null, id);
                        EndEdit();
                    };
                }
                if (ConfirmButton($"delete tree {id}", $"Delete with everything inside ({inside})", $"Click again to delete all {inside + 1}", _act, _actMain, GUILayout.MinWidth(0f)))
                {
                    _pending = () =>
                    {
                        TerritoryClient.Send(RequestOp.DeleteTree, null, id);
                        EndEdit();
                    };
                }
            }

            if (_editing == null)
                GUILayout.Label("Members and owner are on the Members tab; they are marked along with these rules.", _hint);
            else
                GUILayout.Label("Nothing changes for players until you Save. Move / reshape keeps these rules and asks the hammer for new ground; a territory of several parts is moved part by part on the Place tab."
                    + (inside > 0 ? $" Delete leaves the {PanelMath.Plural(inside, "territory", "territories")} inside it standing, each with the rules it had, one level up; Delete with everything inside removes them too." : ""), _hint);
        }

        // ─────────────────────────────────────────────────────────────
        // MEMBERS - admins
        // ─────────────────────────────────────────────────────────────

        private static void DrawMembers()
        {
            Territory t = Target;
            DrawEditingBanner();

            // 0.4.0: whose people these are. Followed, the owner and members below are the parent's (the copy holds them, kept
            // current by Resync), shown and greyed - never hidden - until the admin sets the territory's own list. A draft
            // marked inside whatever it lands in has no parent's list to show yet, and says so instead of showing an empty one.
            Mark people = Mark.Of(t, TerritoryFollow.People);
            if (people.Shown) DrawPeople(t, people);
            bool followed = people.Follows;
            bool unknown = people.Unknown;

            GUILayout.BeginHorizontal();
            GUILayout.Label(unknown ? "Owner: follows" : $"Owner: {(t.OwnerId == 0L ? "none" : $"{t.OwnerName} ({t.OwnerId})")}", _text);
            GUILayout.FlexibleSpace();
            GUI.enabled = !followed && t.OwnerId != 0L;
            if (GUILayout.Button("Clear owner", _btn, GUILayout.Width(GiltFrameTheme.S(130f))))
                _pending = () => { OwnPeople(Target); Target.OwnerId = 0L; Target.OwnerName = ""; };
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            GUILayout.Label("The owner is a member like any other - the territory is simply said to be theirs.", _hint);

            Section(unknown ? "Members (follow)" : $"Members ({t.Members.Count})");
            if (unknown) GUILayout.Label("Its members are those of the territory it is marked in.", _hint);
            else if (t.Members.Count == 0) GUILayout.Label("No members yet.", _hint);
            foreach (TerritoryMember m in t.Members)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label($"{m.Name}  ({m.Id})", _text);
                GUILayout.FlexibleSpace();
                GUI.enabled = !followed;
                if (GUILayout.Button("Remove", _btn, GUILayout.Width(GiltFrameTheme.S(100f))))
                {
                    long id = m.Id;
                    _pending = () => { OwnPeople(Target); Target.Members.RemoveAll(x => x.Id == id); };
                }
                GUI.enabled = true;
                GUILayout.EndHorizontal();
            }

            Section("Online now");
            List<TerritoryMember> online = TerritoryClient.OnlinePlayers;
            if (online.Count == 0) GUILayout.Label("The server has not sent the player list (it does for admins).", _hint);
            foreach (TerritoryMember p in online)
            {
                bool isOwner = p.Id == t.OwnerId;
                bool isMember = t.Members.Exists(x => x.Id == p.Id);
                GUILayout.BeginHorizontal();
                GUILayout.Label(p.Name + (isOwner ? "  (owner)" : isMember ? "  (member)" : ""), _text);
                GUILayout.FlexibleSpace();
                GUI.enabled = !followed && !isOwner;
                if (GUILayout.Button("Make owner", _btn, GUILayout.Width(GiltFrameTheme.S(120f))))
                {
                    TerritoryMember pick = p;
                    _pending = () =>
                    {
                        OwnPeople(Target);
                        Target.OwnerId = pick.Id;
                        Target.OwnerName = pick.Name;
                        Target.Members.RemoveAll(x => x.Id == pick.Id);
                    };
                }
                GUI.enabled = !followed && !isOwner && !isMember;
                GUILayout.Space(Gap);
                if (GUILayout.Button("Add", _btn, GUILayout.Width(GiltFrameTheme.S(80f))))
                {
                    TerritoryMember pick = p;
                    _pending = () => AddMember(pick.Id, pick.Name);
                }
                GUI.enabled = true;
                GUILayout.EndHorizontal();
            }

            Section("Someone who is offline");
            GUI.enabled = !followed;
            GUILayout.BeginHorizontal();
            GUILayout.Label("Player id", _text, GUILayout.Width(GiltFrameTheme.S(90f)));
            GUILayout.Space(Gap);
            _manualId = GUILayout.TextField(_manualId, 20, _field, GUILayout.Width(GiltFrameTheme.S(190f)));
            GUILayout.Space(Gap * 2f);
            GUILayout.Label("Name", _text, GUILayout.Width(GiltFrameTheme.S(60f)));
            GUILayout.Space(Gap);
            _manualName = GUILayout.TextField(_manualName, 32, _field);
            GUILayout.EndHorizontal();
            GUILayout.Space(GiltFrameTheme.S(4f));
            if (GUILayout.Button("Add by id", _act))
            {
                string idText = _manualId;
                string name = _manualName;
                _pending = () =>
                {
                    if (!long.TryParse(idText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long id) || id == 0L)
                    {
                        Note("A player id is a whole number - they can read theirs with terr_whoami.");
                        return;
                    }
                    AddMember(id, string.IsNullOrEmpty(name.Trim()) ? id.ToString(CultureInfo.InvariantCulture) : name.Trim());
                    _manualId = "";
                    _manualName = "";
                };
            }
            GUI.enabled = true;
            GUILayout.Label("The id is the player's character id (terr_whoami prints it), the same number a ward keeps in its permitted list.", _hint);

            if (_editing != null)
            {
                GUILayout.Space(GiltFrameTheme.S(10f));
                if (GUILayout.Button("Save changes", _actMain))
                {
                    Territory send = _editing.Clone();
                    _pending = () => TerritoryClient.Send(RequestOp.Update, send, send.Id);
                }
            }
        }

        /// <summary>
        /// The top of the Members tab for a territory that has a parent: whose people these are, the marks column's cell for
        /// them, and the move from one to the other. Followed, "Set its own list" copies the parent's owner and members in
        /// and makes them the territory's own. Own, "Follow" takes the parent's again - which drops this list, so it is
        /// click-twice, as the mark is.
        /// </summary>
        private static void DrawPeople(Territory t, Mark people)
        {
            Territory parent = ParentRecord(t);
            bool follows = people.Follows;

            GUILayout.BeginHorizontal();
            GUILayout.Label(PanelMath.PeopleCaption(t, parent, follows), _head, GUILayout.MinWidth(0f), GUILayout.ExpandWidth(true));
            MarkCell(people, "people follow");
            GUILayout.EndHorizontal();

            if (follows)
            {
                if (GUILayout.Button("Set its own list", _act, GUILayout.MinWidth(0f))) _pending = SetOwnPeople;
            }
            else
            {
                bool was = GUI.enabled;
                GUI.enabled = was && CanPull(t);
                if (ConfirmButton("people follow", $"Follow {PanelMath.ParentName(t, parent)}", "Click again to follow - this list is dropped", _act, _actMain, GUILayout.MinWidth(0f)))
                {
                    Mark pull = people;
                    _pending = () => PullMark(pull);
                }
                GUI.enabled = was;
            }
            GUILayout.Label(PanelMath.PeopleHint(t, parent, follows), _hint);
            GUILayout.Space(GiltFrameTheme.S(4f));
        }

        /// <summary>Copies the parent's owner and members into the territory and makes them its own.</summary>
        private static void SetOwnPeople()
        {
            Territory t = Target;
            Territory source = PullSource(t);
            if (source != null) Inheritance.Pull(t, source, TerritoryFollow.People);
            OwnPeople(t);
        }

        /// <summary>Whoever changes the owner or the members first makes them the territory's own (a followed list is not the territory's to edit).</summary>
        private static void OwnPeople(Territory t) => Inheritance.Own(t, TerritoryFollow.People);

        private static void AddMember(long id, string name)
        {
            Territory t = Target;
            if (id == 0L || id == t.OwnerId || t.Members.Exists(x => x.Id == id)) return;
            if (t.Members.Count >= Territory.MaxMembers)
            {
                Note($"A territory holds at most {Territory.MaxMembers} members.");
                return;
            }
            OwnPeople(t);
            t.Members.Add(new TerritoryMember(id, Territory.CleanName(name, 32)));
        }

        // ─────────────────────────────────────────────────────────────
        // PLACE - admins
        // ─────────────────────────────────────────────────────────────

        private static void DrawPlace()
        {
            DrawArmedLine();
            GUILayout.Space(GiltFrameTheme.S(8f));

            if (_editing != null) DrawPlaceEditing();
            else DrawPlaceNew();

            GUILayout.Space(GiltFrameTheme.S(12f));
            GUILayout.Label("Console: terr_here [radius], terr_list, terr_delete <id>, terr_combine <into> <from>, terr_reload, terr_whoami.", _hint);
        }

        /// <summary>What the next placement will do - always drawn, so the tab never jumps when it is armed or disarmed.</summary>
        private static void DrawArmedLine()
        {
            string action = ArmedAction;
            bool armed = action != null;

            GUILayout.BeginHorizontal();
            GUILayout.Label(armed ? "Armed" : "Not armed", armed ? _head : _key);
            GUILayout.FlexibleSpace();
            bool was = GUI.enabled;
            GUI.enabled = was && armed;
            if (GUILayout.Button("Cancel", _btn, GUILayout.Width(RowButtonW))) _pending = Disarm;
            GUI.enabled = was;
            GUILayout.EndHorizontal();

            string text = armed ? $"The next placement {action}."
                : _editing == null ? $"The next placement marks a new territory with the rules on the Rules tab, {PanelMath.PlacedWhere(NewDraft, ParentRecord(NewDraft))}."
                : $"The next placement marks a new territory, with the rules for new ones - not #{_editing.Id}'s.";
            GUILayout.Label(text, armed ? _wrap : _hint);
        }

        private static void DrawPlaceNew()
        {
            GUILayout.Label("With Infinity Hammer", _head);
            GUILayout.Label("The Territory tool shows the area on the ground. Scroll to resize, use the hammer's shape key for circle / square / rectangle, rotate as you would a piece, then place. The rules on the Rules and Members tabs go with it.", _hint);
            bool was = GUI.enabled;
            GUI.enabled = was && InfinityHammerBridge.Installed;
            if (GUILayout.Button("Pick up the hammer to mark a new territory", _actMain)) _pending = StartNewTerritory;
            GUI.enabled = was;
            if (!InfinityHammerBridge.Installed) GUILayout.Label("Infinity Hammer is not installed on this machine.", _hint);

            GUILayout.Space(GiltFrameTheme.S(8f));
            GUILayout.Label("Fixing a bad placement, or joining two territories: aim at a territory with one of these and place.", _hint);
            DrawToolButtons(withCombine: true);
            GUILayout.Label("Remove asks you to place a second time on the same territory, so a stray click cannot delete one.", _hint);
            GUILayout.Label($"Combine: place inside the territory to keep, then inside another within {Commands.CombineSeconds:0} s - that one joins it.", _hint);

            GUILayout.Space(GiltFrameTheme.S(12f));
            GUILayout.Label("Without the hammer", _head);
            HereRadius = Stepper("Radius", HereRadius, 5f, 5f, Mathf.Max(5f, TSConfig.MaxHalfExtent.Value), "0 m", "A circle centred where you stand.");
            string circle = AddPartTo != 0 ? $"Add a circle here to {Named(AddPartTo)}"
                : ReshapeId != 0 ? $"Move {ArmedPartName()} to a circle here"
                : "Mark a circle here";
            // Greyed while the armed move's part has changed: it would move nothing. MinWidth 0, because the face
            // can carry a territory's name, and a long one is clipped rather than widening the tab (LAYOUT, below).
            GUI.enabled = was && !ArmedPartChanged;
            if (GUILayout.Button(circle, _act, GUILayout.MinWidth(0f))) _pending = PlaceCircleHere;
            GUI.enabled = was;

            // The Ground section belongs to a territory being edited; drawn here all the same, empty, so it is
            // found - and so this tab has the same sections whatever is being edited.
            Section("Ground");
            GUILayout.Label("A territory being edited shows its parts here: add ground to give it any shape, move or split off a part, or join another territory into it. Pick one with Edit on the All tab, or with the hammer's Territory edit.", _hint);
        }

        private static void DrawPlaceEditing()
        {
            Territory live = EditingLive;
            Territory shown = live ?? _editing;
            int maxParts = Mathf.Max(1, TSConfig.MaxParts.Value);
            bool was = GUI.enabled;

            // GROUND
            Section("Ground");
            int parts = shown.PartCount;
            if (live == null) GUILayout.Label("This territory no longer exists on the server.", _hint);
            else if (parts == 1) GUILayout.Label("Add ground to give it any shape: each part is a circle, square or rectangle, and together they are one territory with one set of rules.", _hint);
            else GUILayout.Label($"{parts} parts, one territory: one set of rules, one banner, one outline.", _hint);

            GUI.enabled = was && live != null;
            for (int i = 0; i < parts; i++) DrawPartRow(shown, i);

            GUILayout.Space(GiltFrameTheme.S(4f));
            bool room = parts < maxParts;
            GUI.enabled = was && live != null && room && InfinityHammerBridge.Installed;
            if (GUILayout.Button("Add ground with the hammer", _act)) _pending = () => StartAddGround(shown);

            GUI.enabled = was && live != null;
            HereRadius = Stepper("Radius", HereRadius, 5f, 5f, Mathf.Max(5f, TSConfig.MaxHalfExtent.Value), "0 m", null);
            // The one circle button here follows the arming, as "Mark a circle here" does: while Move is armed for
            // one of these parts (the hammer out, the panel opened again), it moves that part to where you stand -
            // greyed once that part has changed, as it would move nothing; otherwise it adds a part. MinWidth 0
            // as there: the face can carry the name.
            bool moving = ReshapeId == shown.Id;
            GUI.enabled = was && live != null && (moving ? !ArmedPartChanged : room);
            if (GUILayout.Button(moving ? $"Move {ArmedPartName()} to a circle here" : "Add a circle here", _act, GUILayout.MinWidth(0f)))
            {
                int id = shown.Id;
                _pending = moving ? (Action)PlaceCircleHere : () => AddCircleHere(id);
            }
            GUI.enabled = was;
            GUILayout.Label(room
                ? "Add ground: every Territory tool placement becomes a new part, until you Cancel. Add a circle here adds one centred where you stand."
                : $"It has the most parts a territory may have here ({maxParts}, the server's MaxParts).", _hint);
            GUILayout.Label("Move picks up the hammer for that part. Split off makes a part a territory of its own, with these rules; Remove drops it. Both ask for a second click.", _hint);

            // COMBINE
            Section("Combine");
            GUILayout.Label(maxParts < 2
                ? "Combining is off on this server (MaxParts is 1)."
                : "Join into this: its ground becomes part of this territory, its owner and members join this one's members, and its own name and rules are dropped. Territories inside it keep their rules and move inside this one. Click twice.", _hint);
            if (live != null)
            {
                List<Neighbour> near = NeighboursOf(live);
                int count = Math.Min(near.Count, NeighboursShown);
                if (near.Count == 0) GUILayout.Label("There is no other territory to join.", _hint);
                for (int i = 0; i < count; i++) DrawNeighbour(live, near[i], maxParts);
                if (near.Count > count)
                    GUILayout.Label($"...and {near.Count - count} more, further away. The hammer tool below, or terr_combine, reaches any of them.", _hint);
            }
            GUILayout.Space(GiltFrameTheme.S(4f));
            GUI.enabled = was && InfinityHammerBridge.Installed && maxParts > 1;
            if (GUILayout.Button("Hammer: combine two territories", _act)) _pending = StartCombineTool;
            GUI.enabled = was;
            GUILayout.Label($"Place it inside the territory to keep, then inside another within {Commands.CombineSeconds:0} s: that one joins it.", _hint);

            // THE OTHER TOOLS
            Section("More hammer tools");
            DrawToolButtons(withCombine: false);
            GUILayout.Label("Remove asks you to place a second time on the same territory, so a stray click cannot delete one.", _hint);
            if (!InfinityHammerBridge.Installed) GUILayout.Label("Infinity Hammer is not installed on this machine.", _hint);
        }

        /// <summary>"Part 2 - 20 x 60 m at (x, z)"  [Move] [Split off] [Remove], lit when it is the part the admin was found in.</summary>
        private static void DrawPartRow(Territory t, int i)
        {
            TerritoryShape part = t.GetPart(i);
            bool lit = _hasHighlight && _highlightFor == t.Id && part.SameAs(_highlight);
            bool several = t.PartCount > 1;
            bool was = GUI.enabled;

            GUILayout.BeginHorizontal(lit ? _rowLit : _rowPlain);
            GUILayout.Label($"Part {i + 1} - {PanelMath.PartSummary(part)}", lit ? _partLit : _part, GUILayout.MinWidth(0f), GUILayout.ExpandWidth(true));
            GUILayout.Space(Gap);

            GUI.enabled = was && InfinityHammerBridge.Installed;
            if (GUILayout.Button("Move", _btn, GUILayout.Width(GiltFrameTheme.S(70f))))
            {
                Territory pick = t;
                int index = i;
                _pending = () => StartMovePart(pick, index);
            }

            // Split off and Remove name the part by this row's number, so they carry the revision of the record the
            // rows were counted from (t is the server's record: the rows are greyed without one). Resetting the
            // click-twice key when a new record arrives covers only a record that lands between the two clicks; a
            // removal still on its way, or a second admin's, can renumber the parts under a request already sent,
            // and the server then refuses it rather than take whichever part has this number now.
            GUILayout.Space(Gap);
            GUI.enabled = was && several;
            if (ConfirmButton($"split {t.Id} {i}", "Split off", "Click again", _btn, _btnOn, GUILayout.Width(RowButtonW)))
            {
                int id = t.Id, index = i, seen = t.Revision;
                _pending = () => TerritoryClient.Send(RequestOp.SplitPart, null, id, part: index, baseRevision: seen);
            }

            GUILayout.Space(Gap);
            if (ConfirmButton($"remove {t.Id} {i}", "Remove", "Click again", _btn, _btnOn, GUILayout.Width(RowButtonW)))
            {
                int id = t.Id, index = i, seen = t.Revision;
                _pending = () => TerritoryClient.Send(RequestOp.RemovePart, null, id, part: index, baseRevision: seen);
            }
            GUI.enabled = was;
            GUILayout.EndHorizontal();
        }

        private static void DrawNeighbour(Territory live, Neighbour n, int maxParts)
        {
            Territory other = n.Territory;
            int together = live.PartCount + other.PartCount;
            // A territory and one inside it cannot be combined (the server says so, naming Inside on the Rules tab): greyed here, with
            // the reason under the row, rather than a click-twice that comes back refused.
            string unrelated = TerritoryEdits.CombineRefusal(TerritoryIndex.All, live, other);
            bool was = GUI.enabled;

            GUILayout.BeginHorizontal();
            Swatch(other.Color);
            GUILayout.Space(Gap);
            GUILayout.Label(other.DisplayName, _head, GUILayout.MinWidth(0f), GUILayout.ExpandWidth(true));
            GUILayout.Space(Gap);
            GUILayout.Label(PanelMath.Where(n.Gap), _key, GUILayout.Width(GiltFrameTheme.S(110f)));
            GUILayout.Space(Gap);
            GUI.enabled = was && together <= maxParts && unrelated == null;
            if (ConfirmButton($"join {live.Id} {other.Id}", "Join into this", "Click again", _btn, _btnOn, GUILayout.Width(GiltFrameTheme.S(130f))))
            {
                int into = live.Id, from = other.Id;
                _pending = () => TerritoryClient.Send(RequestOp.Combine, null, into, from);
            }
            GUI.enabled = was;
            GUILayout.EndHorizontal();

            string owner = string.IsNullOrEmpty(other.OwnerName) ? "no owner" : "owner " + other.OwnerName;
            string line = $"#{other.Id} - {other.Ground.Describe()} - {owner} - {other.Members.Count} member(s)";
            if (together > maxParts && maxParts > 1) line += $" - together {together} parts, over the limit of {maxParts}";
            if (unrelated != null) line += " - " + unrelated;
            GUILayout.Label(line, _hintCard);
            GUILayout.Space(GiltFrameTheme.S(4f));
        }

        /// <summary>Remove / edit (and, for the draft, combine): the hammer tools that aim at a territory already marked.</summary>
        private static void DrawToolButtons(bool withCombine)
        {
            bool was = GUI.enabled;
            GUI.enabled = was && InfinityHammerBridge.Installed;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Hammer: remove a territory", _act, GUILayout.Width(ToolW))) _pending = StartRemoveTool;
            GUILayout.Space(Gap);
            if (GUILayout.Button("Hammer: edit a territory", _act, GUILayout.Width(ToolW))) _pending = StartEditTool;
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            if (withCombine)
            {
                GUI.enabled = was && InfinityHammerBridge.Installed && TSConfig.MaxParts.Value > 1;
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Hammer: combine two territories", _act, GUILayout.Width(ToolW))) _pending = StartCombineTool;
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
            }
            GUI.enabled = was;
        }

        // ─────────────────────────────────────────────────────────────
        // THE COMBINE LIST - measured when the list or the territory changes, never per frame
        // ─────────────────────────────────────────────────────────────

        private struct Neighbour
        {
            public Territory Territory;

            /// <summary>Metres between the two grounds; 0 or less where they meet or overlap.</summary>
            public float Gap;
        }

        private const int NeighboursShown = 8;
        private static readonly List<Neighbour> _neighbours = new List<Neighbour>();
        private static int _neighboursOf;
        private static int _neighboursRevision = -1;

        /// <summary>
        /// Every other territory, nearest first, measured from this one's whole ground. Every part against every
        /// part is too much to redo 60 times a second on a big server, and redoing it only at a Layout event
        /// means the event after it draws exactly the rows the layout counted (LAYOUT DISCIPLINE).
        /// </summary>
        private static List<Neighbour> NeighboursOf(Territory live)
        {
            bool stale = _neighboursOf != live.Id || _neighboursRevision != TerritoryIndex.LocalRevision;
            if (stale && Event.current != null && Event.current.type == EventType.Layout)
            {
                _neighbours.Clear();
                TerritoryGround ground = live.Ground;
                foreach (Territory t in TerritoryIndex.All)
                    if (t.Id != live.Id) _neighbours.Add(new Neighbour { Territory = t, Gap = PanelMath.Gap(ground, t.Ground) });
                _neighbours.Sort((a, b) => a.Gap != b.Gap ? a.Gap.CompareTo(b.Gap) : a.Territory.Id.CompareTo(b.Territory.Id));
                _neighboursOf = live.Id;
                _neighboursRevision = TerritoryIndex.LocalRevision;
            }
            return _neighbours;
        }

        // ─────────────────────────────────────────────────────────────
        // THE HAMMER - arm, pick up the tool, get out of the way
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Puts the Territory tool on the hammer for whatever is armed and closes the panel. When the tool cannot be
        /// had, nothing stays armed: an arming the admin did not see through would take over a later terr_here.
        /// </summary>
        private static void StartHammer(string centreMessage)
        {
            if (!InfinityHammerBridge.SelectPlace(out string why))
            {
                Disarm();
                Note(why);
                return;
            }
            Close();
            MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, centreMessage);
        }

        private static void StartNewTerritory()
        {
            Disarm();
            StartHammer("Place the Territory tool to mark the territory");
        }

        private static void StartMovePart(Territory t, int part)
        {
            Territory live = TerritoryIndex.Find(t.Id) ?? t;
            if (part < 0 || part >= live.PartCount) return;
            ArmReshape(live, part);
            StartHammer(live.PartCount > 1
                ? $"Place the Territory tool to move part {part + 1} of '{live.DisplayName}'"
                : $"Place the Territory tool to move '{live.DisplayName}'");
        }

        private static void StartAddGround(Territory t)
        {
            // Says "each", not "the": this arming outlasts a placement (SubmitPlacement), and the admin must know
            // to end it before the tool marks new territories again.
            ArmAddPart(t);
            StartHammer($"Each Territory tool placement adds ground to '{t.DisplayName}' - Cancel on the Place tab when done");
        }

        private static void StartRemoveTool()
        {
            if (!InfinityHammerBridge.SelectRemove(out string why))
            {
                Note(why);
                return;
            }
            Close();
            MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, "Place inside a territory, twice, to remove it");
        }

        private static void StartEditTool()
        {
            if (!InfinityHammerBridge.SelectEdit(out string why))
            {
                Note(why);
                return;
            }
            Close();
            MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, "Place inside a territory to edit it");
        }

        private static void StartCombineTool()
        {
            if (!InfinityHammerBridge.SelectCombine(out string why))
            {
                Note(why);
                return;
            }
            Close();
            MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, "Place inside the territory to keep, then inside the one to join into it");
        }

        /// <summary>The 'Territory edit' hammer tool's landing: the panel, open on that territory's rules.</summary>
        /// <param name="part">The part the tool was placed in, lit in the Place tab's Ground list; -1 for none.</param>
        internal static void OpenEditing(Territory t, int part = -1)
        {
            if (Player.m_localPlayer == null || t == null) return;
            if (!IsOpen) Open();
            BeginEdit(t, part);
        }

        // ─────────────────────────────────────────────────────────────
        // SHARED
        // ─────────────────────────────────────────────────────────────

        private static void DrawStatus()
        {
            float now = Time.realtimeSinceStartup;
            string text = null;
            if (now - _localNoteAt < 12f) text = _localNote;
            else if (now - TerritoryClient.LastResultAt < 20f) text = (TerritoryClient.LastResultOk ? "" : "Refused: ") + TerritoryClient.LastResult;

            GUILayout.Space(GiltFrameTheme.S(4f));
            GUILayout.Label(text ?? " ", _status);
        }

        private static void Section(string title)
        {
            GUILayout.Space(GiltFrameTheme.S(10f));
            GUILayout.Label(title, GiltFrameTheme.SubTitle);
        }

        // CLICK TWICE: Delete, Split off, Remove and Join act on the second click within ConfirmWindow; the first
        // only arms the button, which says so on its face. One key names the button and what it acts on, so a
        // second click on a different part or territory arms that one instead of firing.
        private const float ConfirmWindow = 4f;
        private static string _confirmKey = "";
        private static float _confirmAt = -1000f;

        private static bool ConfirmButton(string key, string label, string armedLabel, GUIStyle style, GUIStyle armedStyle, params GUILayoutOption[] options)
        {
            bool armed = _confirmKey == key && Time.realtimeSinceStartup - _confirmAt < ConfirmWindow;
            if (!GUILayout.Button(armed ? armedLabel : label, armed ? armedStyle : style, options)) return false;
            if (armed)
            {
                _confirmKey = "";
                return true;
            }
            _confirmKey = key;
            _confirmAt = Time.realtimeSinceStartup;
            return false;
        }

        /// <summary>[ON/off] Label, with its hint beneath the label - and, on a territory with a parent, the marks column at the right.</summary>
        private static void Flag(Territory t, TerritoryFlags flag, string label, string hint)
        {
            Mark mark = Mark.Of(t, flag);
            bool on = t.Has(flag);
            GUILayout.BeginHorizontal();
            if (RuleButton(on, mark)) ToggleRule(t, flag);   // PanelMath.ToggleRule: own, switch, and the rules that need it cleaned
            GUILayout.Space(Gap);
            RowLabel(label, mark);
            MarkCell(mark);
            GUILayout.EndHorizontal();
            if (hint != null) GUILayout.Label(hint, _hintFlag);
        }

        /// <summary>
        /// A flag that only means something while another is on - the runic barrier under No monster spawns, the
        /// visitors option under the barrier. <paramref name="depth"/> steps of FlagW + Gap in: its button starts
        /// under its parent's label and its own label and hint one step further, so the column lands on the same x
        /// in every row, as an indented Stepper's does. Greyed, never hidden, while <paramref name="applies"/> is
        /// false (LAYOUT DISCIPLINE): the set of controls is the same whatever the flags say. Cleared by the rule at
        /// the click, not left for the server, so a greyed row always reads "off".
        /// </summary>
        private static void SubFlag(Territory t, TerritoryFlags flag, bool applies, int depth, string label, string hint)
        {
            Mark mark = Mark.Of(t, flag);
            bool was = GUI.enabled;
            GUI.enabled = was && applies;
            bool on = applies && t.Has(flag);
            GUILayout.BeginHorizontal();
            GUILayout.Space(depth * (FlagW + Gap));
            if (RuleButton(on, mark)) ToggleRule(t, flag);
            GUILayout.Space(Gap);
            RowLabel(label, mark);
            MarkCell(mark);
            GUILayout.EndHorizontal();
            GUILayout.Label(hint, depth == 1 ? _hintSub : _hintSubSub);
            GUI.enabled = was;
        }

        private static void PvPButton(Territory t, PvPRule rule, string label, Mark mark, float width)
        {
            // None is lit while the rule follows a parent that is not known yet: any lit one would be a value nobody has.
            bool lit = t.PvP == rule && !mark.Unknown;
            if (GUILayout.Button(label, lit ? _btnOnTight : _btnTight, GUILayout.Width(width)))
            {
                Touch(mark);
                t.PvP = rule;
            }
        }

        /// <summary>
        /// PvP: label, three choices, marks column. The three share what is left of the row (PanelMath.FitWidths), which at
        /// 130 each was a little more than the scroll view had once the marks column joined it - and, at the smallest text
        /// scale, more than it had without.
        /// </summary>
        private static void DrawPvP(Territory t)
        {
            Mark mark = Mark.Of(t, TerritoryFollow.PvP);
            float room = _contentW - (LabelW + 3f * Gap + (mark.Shown ? Gap + MarkW : 0f));
            float[] w = PanelMath.FitWidths(room, GiltFrameTheme.S(130f), _pvpMins);

            GUILayout.BeginHorizontal();
            GUILayout.Label("PvP", _text, GUILayout.Width(LabelW));
            GUILayout.Space(Gap);
            PvPButton(t, PvPRule.Unchanged, PvPChoose, mark, w[0]);
            GUILayout.Space(Gap);
            PvPButton(t, PvPRule.ForcedOff, PvPOff, mark, w[1]);
            GUILayout.Space(Gap);
            PvPButton(t, PvPRule.ForcedOn, PvPOn, mark, w[2]);
            GUILayout.FlexibleSpace();
            MarkCell(mark);
            GUILayout.EndHorizontal();
        }

        private const string PvPChoose = "Players choose", PvPOff = "Forced off", PvPOn = "Forced on";

        /// <summary>
        /// Label [-] value [+]. The label fills the label column, so every stepper's buttons line up with the
        /// Colour and PvP rows; an indented one (a flag's sub-setting) starts its label under the flag's label.
        /// </summary>
        private static float Stepper(string label, float value, float step, float min, float max, string format, string hint, bool indent = false, Mark mark = default)
        {
            float lead = indent ? FlagW + Gap : 0f;
            GUILayout.BeginHorizontal();
            if (indent) GUILayout.Space(lead);
            GUILayout.Label(label, _text, GUILayout.Width(LabelW - lead));
            GUILayout.Space(Gap);
            if (GUILayout.Button("-", _btn, GUILayout.Width(NudgeW)))
            {
                Touch(mark);
                value -= step;
            }
            // A number that follows a parent nobody knows yet is not shown: it would be a guess.
            GUILayout.Label(mark.Unknown ? PanelMath.MarkFollows : value.ToString(format, CultureInfo.InvariantCulture), _number, GUILayout.Width(ValueW));
            if (GUILayout.Button("+", _btn, GUILayout.Width(NudgeW)))
            {
                Touch(mark);
                value += step;
            }
            GUILayout.FlexibleSpace();
            MarkCell(mark);
            GUILayout.EndHorizontal();
            if (hint != null) GUILayout.Label(hint, indent ? _hintFlag : _hint);
            return Mathf.Clamp(Mathf.Round(value / step) * step, min, max);
        }

        /// <summary>A text field under a flag, starting where the flag's label does - and, with a parent, ending at the marks column.</summary>
        private static string IndentedField(string value, Mark mark = default)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Space(FlagW + Gap);
            // Followed from a parent nobody knows yet, the field is empty and says so over itself: no text is shown that is not
            // in force. Typing in it makes the text the territory's own, the first character included. It stays a text field
            // either way, so the controls (and keyboard focus) are the same whether the text follows or not.
            string shown = mark.Unknown ? "" : value ?? "";
            // MinWidth 0: a text field's least width is its text's, so 160 typed characters would push the row past the panel -
            // and the marks column off its edge. It takes whatever room there is and scrolls its text inside.
            string text = GUILayout.TextField(shown, Territory.MaxMessageLength, _field, GUILayout.MinWidth(0f), GUILayout.ExpandWidth(true));
            if (mark.Unknown && text.Length == 0) GUI.Label(GUILayoutUtility.GetLastRect(), "follows the territory it is marked in", _fieldHint);
            bool edited = text != shown;
            if (edited) Touch(mark);
            MarkCell(mark);
            GUILayout.EndHorizontal();
            return edited ? text : value ?? "";
        }

        /// <summary>A colour dot, centred on the row it sits in.</summary>
        private static void Swatch(Color32 c)
        {
            Rect r = GUILayoutUtility.GetRect(SwatchW, RowH, _text, GUILayout.Width(SwatchW));
            GiltFrameTheme.DrawDot(new Rect(r.x, r.y + (r.height - SwatchW) * 0.5f, SwatchW, SwatchW), c);
        }

        // ─────────────────────────────────────────────────────────────
        // INSIDE AND THE FOLLOW MARKS (0.4.0) - where the territory sits, and which settings are the parent's
        // ─────────────────────────────────────────────────────────────

        /// <summary>One setting row's place in the marks: the copy being edited, and the rule or group the row stands for.</summary>
        private struct Mark
        {
            public Territory T;
            public TerritoryFlags Rule;
            public TerritoryFollow Group;

            public static Mark Of(Territory t, TerritoryFlags rule) => new Mark { T = t, Rule = rule };

            public static Mark Of(Territory t, TerritoryFollow group) => new Mark { T = t, Group = group };

            /// <summary>The marks column is drawn on this row: the territory has a parent, or (a draft) will be given one.</summary>
            public bool Shown => PanelMath.HasMarks(T);

            public bool Follows => Rule != TerritoryFlags.None ? PanelMath.FollowsRule(T, Rule) : Group != TerritoryFollow.None && PanelMath.FollowsGroup(T, Group);

            /// <summary>It follows a parent nobody knows yet, so the row cannot show a value.</summary>
            public bool Unknown => Rule != TerritoryFlags.None ? PanelMath.UnknownRule(T, Rule) : Group != TerritoryFollow.None && PanelMath.UnknownGroup(T, Group);
        }

        /// <summary>Is this rule in force, as far as the panel can tell? One that follows an unknown parent may be, so what hangs on it is not greyed.</summary>
        private static bool Applies(Territory t, TerritoryFlags rule) => t.Has(rule) || PanelMath.UnknownRule(t, rule);

        private static Territory ParentRecord(Territory t) => t.ParentId > 0 ? TerritoryIndex.Find(t.ParentId) : null;

        /// <summary>
        /// Where "follow again" takes its values from: the parent's record, or - for a draft whose parent is still to be
        /// picked - a fresh draft, so what it follows reads as it would marked in open country. Null when the parent has gone.
        /// </summary>
        private static Territory PullSource(Territory t) =>
            t.ParentId > 0 ? TerritoryIndex.Find(t.ParentId) : t.ParentId < 0 ? FreshDraft() : null;

        /// <summary>PullSource, without making one: is there something to follow?</summary>
        private static bool CanPull(Territory t) => t.ParentId < 0 || (t.ParentId > 0 && TerritoryIndex.Find(t.ParentId) != null);

        /// <summary>A change to this row is about to be made: what it followed is the territory's own from now on.</summary>
        private static void Touch(Mark m)
        {
            if (!m.Shown) return;
            if (m.Rule != TerritoryFlags.None) Inheritance.Own(m.T, m.Rule);
            else if (m.Group != TerritoryFollow.None) Inheritance.Own(m.T, m.Group);
        }

        /// <summary>"Follow again": the row takes the parent's value, and goes on taking it. Run from _pending.</summary>
        private static void PullMark(Mark m)
        {
            Territory source = PullSource(m.T);
            if (source == null) return;
            if (m.Rule != TerritoryFlags.None) Inheritance.Pull(m.T, source, m.Rule);
            else if (m.Group != TerritoryFollow.None) Inheritance.Pull(m.T, source, m.Group);
        }

        /// <summary>
        /// The marks column's cell, at the right end of a row, drawn only when the territory has a parent. ONE width for every
        /// row (MarkW) and the same kind of control whether it follows or not - a button either way, "follows" a flat dim one
        /// that does nothing - so the column lands on the same x in every row and the controls after it keep their ids when
        /// a mark flips. "own" is the click that follows again; a row greyed because the rule it needs is off still offers it.
        /// </summary>
        /// <param name="confirmKey">When set, following again is click-twice under this key (a list of people is too much to drop by mistake).</param>
        private static void MarkCell(Mark m, string confirmKey = null)
        {
            if (!m.Shown) return;
            GUILayout.Space(Gap);
            bool was = GUI.enabled;
            GUI.enabled = true;
            if (m.Follows)
            {
                GUILayout.Button(PanelMath.MarkFollows, _markFollows, GUILayout.Width(MarkW));
            }
            else
            {
                GUI.enabled = CanPull(m.T);
                bool clicked = confirmKey == null
                    ? GUILayout.Button(PanelMath.MarkOwn, _markOwn, GUILayout.Width(MarkW))
                    : ConfirmButton(confirmKey, PanelMath.MarkOwn, "sure?", _markOwn, _markOwn, GUILayout.Width(MarkW));
                if (clicked)
                {
                    Mark pull = m;
                    _pending = () => PullMark(pull);
                }
            }
            GUI.enabled = was;
        }

        /// <summary>
        /// A row's label. With the marks column after it the label takes all the room between (so the row reaches the right
        /// edge, where the column is) and a long one is clipped rather than pushing the column off the edge.
        /// </summary>
        private static void RowLabel(string label, Mark mark)
        {
            if (mark.Shown) GUILayout.Label(label, _text, GUILayout.MinWidth(0f), GUILayout.ExpandWidth(true));
            else GUILayout.Label(label, _text);
        }

        /// <summary>The ON / off button of a rule row; "follows" while the rule follows a parent nobody knows yet, so it never shows a value that may be wrong.</summary>
        private static bool RuleButton(bool on, Mark mark)
        {
            if (mark.Unknown) return GUILayout.Button(PanelMath.MarkFollows, _btnTight, GUILayout.Width(FlagW));
            return GUILayout.Button(on ? "ON" : "off", on ? _btnOn : _btn, GUILayout.Width(FlagW));
        }

        /// <summary>The colour row: eight chips that share what is left after the label and the marks column.</summary>
        private static void DrawColour(Territory t)
        {
            Mark mark = Mark.Of(t, TerritoryFollow.Color);
            float room = _contentW - (LabelW + Gap + (Palette.Length - 1) * GiltFrameTheme.S(4f) + (mark.Shown ? Gap + MarkW : 0f));
            float chip = PanelMath.FitWidths(room, GiltFrameTheme.S(34f), _chipMins)[0];

            GUILayout.BeginHorizontal();
            GUILayout.Label("Colour", _text, GUILayout.Width(LabelW));
            for (int i = 0; i < Palette.Length; i++)
            {
                Color32 c = Palette[i];
                GUILayout.Space(i == 0 ? Gap : GiltFrameTheme.S(4f));
                Rect r = GUILayoutUtility.GetRect(chip, RowH, _btn, GUILayout.Width(chip));
                if (GUI.Button(r, GUIContent.none, _btn))
                {
                    Touch(mark);
                    t.Color = c;
                }
                GiltFrameTheme.DrawFill(new Rect(r.x + 5f, r.y + 5f, r.width - 10f, r.height - 10f), c);
                if (!mark.Unknown && Same(c, t.Color)) GiltFrameTheme.DrawOutline(r, GiltFrameTheme.GoldBright, 2f);
            }
            GUILayout.FlexibleSpace();
            MarkCell(mark);
            GUILayout.EndHorizontal();
        }

        // -- the copy follows its parent --

        /// <summary>PanelMath.Resync on the live list: a copy's followed settings take their parent's values as they are NOW.</summary>
        private static void Resync(Territory t) => PanelMath.Resync(t, TerritoryIndex.Find, FreshDraft());

        private static void ToggleRule(Territory t, TerritoryFlags rule) => PanelMath.ToggleRule(t, rule, FreshDraft(), TerritoryIndex.Find);

        /// <summary>The list changed: the draft and the territory being edited follow their parents' current values; the baseline moves with them.</summary>
        private static void SyncWithParents()
        {
            // The draft's parent was deleted, or this is not the world it was picked in.
            if (NewDraft.ParentId > 0 && TerritoryIndex.Find(NewDraft.ParentId) == null)
            {
                PutInside(NewDraft, Territory.AutoParent);
                Note("The territory it was to be marked inside has gone: it is Automatic again.");
            }
            if (NewDraft.ParentId > 0) Resync(NewDraft);

            if (_editing == null) return;
            Resync(_editing);
            // Its own record is what the edit was taken from; unchanged, the server's effective record is that record with the
            // parents' CURRENT values, which is what the baseline has to say or Rebase would read the change as the admin's.
            Territory live = TerritoryIndex.Find(_editing.Id);
            if (live != null && _editingBase != null && live.Revision == _editingBase.Revision) _editingBase = live.Clone();
        }

        /// <summary>PanelMath.PutInside for the draft or the territory being edited.</summary>
        private static void PutInside(Territory t, int parentId) =>
            PanelMath.PutInside(t, parentId, ReferenceEquals(t, NewDraft), FreshDraft(), TerritoryIndex.Find);

        private static void ChooseParent(int parentId)
        {
            PutInside(Target, parentId);
            _pickerOpen = false;
            _pickNote = "";
            GUIUtility.keyboardControl = 0;
        }

        private static void TogglePicker()
        {
            _pickerOpen = !_pickerOpen;
            _pickScroll = Vector2.zero;
            _pickNote = "";
            // Controls come and go below the Inside row, and a text field's focus is its control id.
            GUIUtility.keyboardControl = 0;
        }

        private static void FollowAll()
        {
            Territory t = Target;
            Territory source = PullSource(t);
            if (source != null) Inheritance.PullAll(t, source);
        }

        // -- the Inside row and its picker --

        private static bool _pickerOpen;
        private static Vector2 _pickScroll;
        private static string _pickNote = "";
        private static List<PanelMath.PickRow> _pickRows;
        private static int _pickRowsFor = -1, _pickRowsRevision = -1;

        /// <summary>
        /// The picker's rows for territory <paramref name="targetId"/> (0 for the draft). Rebuilt only when the list or the
        /// target changes, and only at a Layout event, so the event that follows draws exactly the rows the layout counted.
        /// </summary>
        private static List<PanelMath.PickRow> PickerRows(int targetId)
        {
            bool layout = Event.current != null && Event.current.type == EventType.Layout;
            bool stale = _pickRows == null || _pickRowsFor != targetId || _pickRowsRevision != TerritoryIndex.LocalRevision;
            if (_pickRows == null || (stale && layout))
            {
                _pickRows = PanelMath.PickerRows(TerritoryIndex.All, targetId);
                _pickRowsFor = targetId;
                _pickRowsRevision = TerritoryIndex.LocalRevision;
            }
            return _pickRows;
        }

        /// <summary>
        /// "Inside  Svartland (#1)  [Change]", right under the Name, for the draft and for a territory being edited. Change opens
        /// the picker below it; on a territory with a parent (or an automatic one) the follow-all button and a line on the
        /// marks come after it.
        /// </summary>
        private static void DrawInside(Territory t)
        {
            GUILayout.Space(GiltFrameTheme.S(6f));
            GUILayout.BeginHorizontal();
            GUILayout.Label("Inside", _text, GUILayout.Width(LabelW));
            GUILayout.Space(Gap);
            GUILayout.Label(PanelMath.ParentLine(t, ParentRecord(t)), _head, GUILayout.MinWidth(0f), GUILayout.ExpandWidth(true));
            GUILayout.Space(Gap);
            if (GUILayout.Button(_pickerOpen ? "Close" : "Change", _btn, GUILayout.Width(ChangeW)))
                _pending = TogglePicker;
            GUILayout.EndHorizontal();

            if (_pickerOpen) DrawPicker(t);
            if (PanelMath.HasMarks(t)) DrawFollowAll(t);
        }

        /// <summary>
        /// A short scrolling list of every territory in tree order, a step in per level, the current choice lit. The ones the
        /// server would refuse as this territory's parent (itself, its own sub-territories, too deep) are greyed with the
        /// reason on the row and in full under the list when clicked - never left out. Opens and closes from _pending.
        /// </summary>
        private static void DrawPicker(Territory t)
        {
            bool draft = _editing == null;
            List<PanelMath.PickRow> rows = PickerRows(draft ? 0 : t.Id);

            _pickScroll = GUILayout.BeginScrollView(_pickScroll, GUILayout.Height(PickerH));
            if (draft) PickerEntry("Automatic: inside whatever it is marked in", 0, t.ParentId < 0, null, Territory.AutoParent);
            PickerEntry("Nothing (top level)", 0, t.ParentId == 0, null, 0);
            for (int i = 0; i < rows.Count; i++)
            {
                PanelMath.PickRow row = rows[i];
                string face = $"{row.Territory.DisplayName} (#{row.Territory.Id})" + (row.Tag != null ? " - " + row.Tag : "");
                PickerEntry(face, row.Depth, t.ParentId == row.Territory.Id, row.Refusal, row.Territory.Id);
            }
            GUILayout.EndScrollView();
            GUILayout.Label(string.IsNullOrEmpty(_pickNote) ? " " : _pickNote, _hint);
        }

        private static void PickerEntry(string face, int depth, bool current, string refusal, int parentId)
        {
            GUILayout.BeginHorizontal();
            if (depth > 0) GUILayout.Space(depth * IndentW);
            if (GUILayout.Button(face, refusal != null ? _pickDim : current ? _pickOn : _pick, GUILayout.MinWidth(0f)))
            {
                if (refusal != null)
                {
                    string why = refusal;
                    _pending = () => _pickNote = why;
                }
                else
                {
                    _pending = () => ChooseParent(parentId);
                }
            }
            GUILayout.EndHorizontal();
        }

        /// <summary>
        /// "Follow all of Svartland" (click-twice: it drops every setting this territory has of its own) and a line saying what
        /// the marks column means. Drawn whenever the territory has a parent; greyed when everything already follows, or
        /// when there is no parent record to follow.
        /// </summary>
        private static void DrawFollowAll(Territory t)
        {
            Territory parent = ParentRecord(t);
            string name = PanelMath.ParentName(t, parent);
            bool was = GUI.enabled;
            GUI.enabled = was && CanPull(t) && !PanelMath.AllFollow(t);
            if (ConfirmButton("follow all", $"Follow all of {name}", "Click again to follow everything", _act, _actMain, GUILayout.MinWidth(0f)))
                _pending = FollowAll;
            GUI.enabled = was;

            GUILayout.Label(PanelMath.MarksHint(t, parent), _hint);
        }

        // ─────────────────────────────────────────────────────────────
        // LAYOUT - one row height, one label column, every hint under what it explains
        // ─────────────────────────────────────────────────────────────
        //
        // 0.1.0's panel was janky (playtest, 2026-09-29), for three reasons, all fixed here:
        //  - hints used the theme's Note, which is CENTRED (it is written for stand-alone notes), under
        //    left-aligned options, and were "indented" with typed spaces that no proportional font lines up;
        //  - the lit state of a tab, flag or choice used the theme's Primary, a bigger font with more padding
        //    than Button, so a row changed height with whatever was switched on;
        //  - labels, buttons and fields in one row each had their own height, and the theme's styles have
        //    no margins, so text sat at different heights along a row and neighbouring buttons touched.
        //
        // The styles below are copies of the theme's, remade whenever the theme remakes its own (a scale
        // change). Every control in a row gets the same fixed height and the same vertical margin; horizontal
        // margins are zero, and gaps are explicit GUILayout.Space(Gap) calls, so a column lands on the same x
        // in every row. The shared theme itself is untouched: other mods draw with it.
        //
        // 0.2.0's lists keep to it: a part row and a Combine row end in fixed-width buttons, so the buttons form
        // columns, and the text before them may be CLIPPED (MinWidth 0) rather than push a row wider than the
        // panel - a long name or a far-off coordinate must never bring in a sideways scroll bar. A full-width
        // button whose face carries a name (the Place tab's circle buttons) is MinWidth 0 for the same reason:
        // GUILayout otherwise takes a button's whole label as its least width.
        //
        // 0.4.0's marks column keeps to it, and is the reason for three rules of its own. (1) It is a fixed width
        // (MarkW), measured with the styles, at the END of the row; every row that has one reaches the panel's right
        // edge first - a stretching label (RowLabel) or a FlexibleSpace - so the column starts at the same x in all
        // of them, and a row without the column (a top-level territory's) is exactly what it was. (2) Whatever is
        // in front of it may be clipped but never pushes it: labels and text fields are MinWidth 0, and the two
        // rows of fixed-width buttons that were close to the edge (PvP's three, Colour's eight) take what is left
        // (PanelMath.FitWidths) - _contentW is the scroll view's width less its bar, and a row wider than that is what
        // brings in the sideways scroll bar. (3) The cell is a button whether the setting follows or not, and so is
        // the ON / off button it sits beside: the number of controls, and so their ids, never depends on the data.

        private static GUIStyle _themeButton;
        private static GUIStyle _btn, _btnOn, _act, _actMain, _text, _key, _number, _head, _field;
        private static GUIStyle _wrap, _item, _status, _hint, _hintFlag, _hintSub, _hintSubSub, _hintCard;
        private static GUIStyle _part, _partLit, _rowPlain, _rowLit;

        // 0.4.0: the marks column ("follows" is flat and dim, "own" a small bright button), faces that must fit a fixed width,
        // the Inside picker's rows, and the empty-field caption of a followed text.
        private static GUIStyle _btnTight, _btnOnTight, _markFollows, _markOwn, _pick, _pickOn, _pickDim, _fieldHint;
        private static float[] _pvpMins, _chipMins;

        private static float Gap => GiltFrameTheme.S(8f);
        private static float LabelW => GiltFrameTheme.S(160f);
        private static float FlagW => GiltFrameTheme.S(64f);
        private static float NudgeW => GiltFrameTheme.S(34f);
        private static float ValueW => GiltFrameTheme.S(80f);
        private static float SwatchW => GiltFrameTheme.S(14f);

        /// <summary>The Inside row's button: its two faces, "Change" and "Close", and no more, so the row's text has the room.</summary>
        private static float ChangeW { get; set; }

        /// <summary>One step of indentation per level in the All tab's tree and the Inside picker.</summary>
        private static float IndentW => GiltFrameTheme.S(16f);

        /// <summary>The Inside picker's height: about six rows, then it scrolls.</summary>
        private static float PickerH => GiltFrameTheme.S(190f);

        /// <summary>
        /// The marks column: ONE width in every row, wide enough for "follows" in the dim font and for the "own" button (with
        /// its click-twice "sure?"), measured with the styles when they are made, so a larger or smaller text scale keeps
        /// the word in its column. It is a fixed width, never a share, so it starts at the same x in every row.
        /// </summary>
        private static float MarkW { get; set; }

        /// <summary>A row's own small button: wide enough for "Click again" in the button font.</summary>
        private static float RowButtonW => GiltFrameTheme.S(100f);

        /// <summary>A hammer tool button: the longest label, "Hammer: combine two territories", with room to spare; two fit a row.</summary>
        private static float ToolW => GiltFrameTheme.S(250f);

        /// <summary>One line of controls: the tallest of a button, a field and a label.</summary>
        private static float RowH { get; set; }

        /// <summary>A line of action buttons, tall enough for the theme's bigger Primary face.</summary>
        private static float ActionH { get; set; }

        private static void EnsureStyles()
        {
            if (_btn != null && ReferenceEquals(_themeButton, GiltFrameTheme.Button)) return;
            _themeButton = GiltFrameTheme.Button;

            var sample = new GUIContent("Ag");
            RowH = Mathf.Ceil(Mathf.Max(GiltFrameTheme.Button.CalcHeight(sample, 200f),
                Mathf.Max(GiltFrameTheme.Field.CalcHeight(sample, 200f), GiltFrameTheme.Value.CalcHeight(sample, 200f))));
            ActionH = Mathf.Ceil(Mathf.Max(RowH, GiltFrameTheme.Primary.CalcHeight(sample, 200f)));

            _btn = InRow(GiltFrameTheme.Button, RowH);
            // Lit: the button's own pressed face and bright text - same font, same size as unlit.
            _btnOn = InRow(GiltFrameTheme.Button, RowH);
            _btnOn.normal.background = GiltFrameTheme.Button.active.background;
            _btnOn.hover.background = GiltFrameTheme.Button.active.background;
            _btnOn.normal.textColor = GiltFrameTheme.GoldBright;

            _act = InRow(GiltFrameTheme.Button, ActionH);
            _actMain = InRow(GiltFrameTheme.Primary, ActionH);

            _text = InRow(GiltFrameTheme.Value, RowH);
            _key = InRow(GiltFrameTheme.Key, RowH);
            _number = InRow(GiltFrameTheme.Key, RowH);
            _number.alignment = TextAnchor.MiddleCenter;
            _head = InRow(GiltFrameTheme.Header, RowH);
            _field = InRow(GiltFrameTheme.Field, RowH);

            // A part row's text starts a little in, so on a lit row it clears the row's gilt edge; lit, it is bright.
            _part = InRow(GiltFrameTheme.Value, RowH);
            RectOffset tp = _part.padding;
            _part.padding = new RectOffset(tp.left + Mathf.RoundToInt(GiltFrameTheme.S(6f)), tp.right, tp.top, tp.bottom);
            _partLit = new GUIStyle(_part);
            _partLit.normal.textColor = GiltFrameTheme.GoldBright;

            // The row itself: nothing, or the theme's pressed-row face. BOTH are real styles with the same zero
            // padding and margins, so a lit row is exactly as tall as the rest, and both draw the same controls -
            // BeginHorizontal with a style draws a box, and a box takes a control id.
            _rowPlain = new GUIStyle { margin = new RectOffset(0, 0, 0, 0), padding = new RectOffset(0, 0, 0, 0) };
            RectOffset rb = GiltFrameTheme.Row.border;
            _rowLit = new GUIStyle(_rowPlain) { border = new RectOffset(rb.left, rb.right, rb.top, rb.bottom) };
            _rowLit.normal.background = GiltFrameTheme.Row.active.background;
            _rowLit.hover.background = GiltFrameTheme.Row.active.background;

            _wrap = Wrapped(GiltFrameTheme.Value, 0f);
            _item = Wrapped(GiltFrameTheme.Value, SwatchW + Gap);
            _status = Wrapped(GiltFrameTheme.Key, 0f);
            _hint = Wrapped(GiltFrameTheme.Note, 0f);
            _hintFlag = Wrapped(GiltFrameTheme.Note, FlagW + Gap);
            // A flag under a flag (SubFlag): its hint starts under its own label, one step in per level.
            _hintSub = Wrapped(GiltFrameTheme.Note, 2f * (FlagW + Gap));
            _hintSubSub = Wrapped(GiltFrameTheme.Note, 3f * (FlagW + Gap));
            _hintCard = Wrapped(GiltFrameTheme.Note, SwatchW + Gap);

            // 0.4.0. A button face that must fit a width it is given keeps its padding small: the theme's 12 px a side leaves
            // "Players choose" 90 px at the default scale and the smallest scale far less. Lit and unlit have the same size.
            int tight = Mathf.Max(2, Mathf.RoundToInt(GiltFrameTheme.S(3f)));
            RectOffset bp = GiltFrameTheme.Button.padding;
            _btnTight = InRow(GiltFrameTheme.Button, RowH);
            _btnTight.padding = new RectOffset(tight, tight, bp.top, bp.bottom);
            _btnOnTight = new GUIStyle(_btnTight);
            _btnOnTight.normal.background = GiltFrameTheme.Button.active.background;
            _btnOnTight.hover.background = GiltFrameTheme.Button.active.background;
            _btnOnTight.normal.textColor = GiltFrameTheme.GoldBright;

            // "follows": flat, dim, no hover. It is a button (see MarkCell) built from a label's style, which has colours only
            // for its normal state - and a button draws in its hover state while the pointer is over it.
            _markFollows = InRow(GiltFrameTheme.Key, RowH);
            _markFollows.alignment = TextAnchor.MiddleCenter;
            _markFollows.hover.textColor = _markFollows.normal.textColor;
            _markFollows.active.textColor = _markFollows.normal.textColor;
            _markFollows.focused.textColor = _markFollows.normal.textColor;
            // "own": a small button with bright text - the one the eye should find, it is what differs from the parent.
            _markOwn = new GUIStyle(_btnTight);
            _markOwn.normal.textColor = GiltFrameTheme.GoldBright;

            _pick = InRow(GiltFrameTheme.Button, RowH);
            _pick.alignment = TextAnchor.MiddleLeft;
            _pick.padding = new RectOffset(Mathf.RoundToInt(GiltFrameTheme.S(10f)), bp.right, bp.top, bp.bottom);
            _pickOn = new GUIStyle(_pick);
            _pickOn.normal.background = GiltFrameTheme.Button.active.background;
            _pickOn.hover.background = GiltFrameTheme.Button.active.background;
            _pickOn.normal.textColor = GiltFrameTheme.GoldBright;
            // Greyed: muted text and no hover face. Still clickable, because a click is how the reason is asked for.
            _pickDim = new GUIStyle(_pick);
            _pickDim.normal.textColor = GiltFrameTheme.Muted;
            _pickDim.hover.textColor = GiltFrameTheme.Muted;
            _pickDim.active.textColor = GiltFrameTheme.Muted;
            _pickDim.hover.background = _pickDim.normal.background;
            _pickDim.active.background = _pickDim.normal.background;

            _fieldHint = InRow(GiltFrameTheme.Note, RowH);
            _fieldHint.alignment = TextAnchor.MiddleLeft;
            _fieldHint.wordWrap = false;
            _fieldHint.padding = new RectOffset(GiltFrameTheme.Field.padding.left, 0, 0, 0);

            MarkW = Mathf.Ceil(Mathf.Max(GiltFrameTheme.S(52f), 2f + Mathf.Max(FaceW(_markFollows, PanelMath.MarkFollows),
                Mathf.Max(FaceW(_markOwn, PanelMath.MarkOwn), FaceW(_markOwn, "sure?")))));
            ChangeW = Mathf.Ceil(Mathf.Max(GiltFrameTheme.S(64f), 2f + Mathf.Max(FaceW(_btn, "Change"), FaceW(_btn, "Close"))));
            _pvpMins = new[] { FaceW(_btnTight, PvPChoose), FaceW(_btnTight, PvPOff), FaceW(_btnTight, PvPOn) };
            _chipMins = new float[Palette.Length];
            for (int i = 0; i < _chipMins.Length; i++) _chipMins[i] = GiltFrameTheme.S(14f);
        }

        /// <summary>How wide a face needs its button to be: the text and the style's padding.</summary>
        private static float FaceW(GUIStyle style, string face) => Mathf.Ceil(style.CalcSize(new GUIContent(face)).x);

        private static GUIStyle InRow(GUIStyle from, float height)
        {
            var s = new GUIStyle(from)
            {
                fixedHeight = height,
                margin = new RectOffset(0, 0, 2, 2)
            };
            if (s.alignment == TextAnchor.UpperLeft || s.alignment == TextAnchor.LowerLeft) s.alignment = TextAnchor.MiddleLeft;
            return s;
        }

        /// <summary>Left-aligned, wrapping text starting <paramref name="indent"/> in from the left edge.</summary>
        private static GUIStyle Wrapped(GUIStyle from, float indent)
        {
            var s = new GUIStyle(from)
            {
                alignment = TextAnchor.UpperLeft,
                wordWrap = true,
                fixedHeight = 0f,
                margin = new RectOffset(0, 0, 1, 3)
            };
            RectOffset p = from.padding;
            s.padding = new RectOffset(p.left + Mathf.RoundToInt(indent), p.right, p.top, p.bottom);
            return s;
        }

        private static bool Same(Color32 a, Color32 b) => a.r == b.r && a.g == b.g && a.b == b.b;
    }

    /// <summary>
    /// Makes the panel count as an open shop, so vanilla's own eight StoreGui.IsVisible checks stop movement,
    /// mouse look, attacks and the Esc menu, and free the cursor. Njord's Harbourmaster trick.
    /// </summary>
    /// <remarks>
    /// Still "visible" for one frame after closing, as vanilla's own StoreGui is (IsVisible is
    /// m_hiddenFrames &lt;= 1): Esc closes the panel from Plugin.Update, and Menu.Update - which runs in the
    /// same frame, in no guaranteed order - would otherwise see nothing open and pop the pause menu on the
    /// very key press that closed the panel.
    /// </remarks>
    [HarmonyPatch(typeof(StoreGui), nameof(StoreGui.IsVisible))]
    internal static class StoreGuiIsVisiblePatch
    {
        private static void Postfix(ref bool __result)
        {
            if (TerritoryPanel.IsOpen || Time.frameCount - TerritoryPanel.ClosedFrame <= 1) __result = true;
        }
    }
}
