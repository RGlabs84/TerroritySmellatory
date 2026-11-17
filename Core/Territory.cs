using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace TerroritySmellatory
{
    /// <summary>
    /// What a territory does. PERSISTED as an int (file and wire), so bits are only ever added, never
    /// moved. Every protection is "for anyone who is not a member" - members, the owner and (when the
    /// server allows it) admins are never stopped by their own territory.
    /// </summary>
    [Flags]
    public enum TerritoryFlags
    {
        None = 0,

        /// <summary>Only members may build, remove, repair or reshape terrain here.</summary>
        BuildProtect = 1 << 0,

        /// <summary>Only members may use doors, chests, portals, signs, stands, stations and the like.</summary>
        AccessProtect = 1 << 1,

        /// <summary>Only members may pick crops, berries and other pickables.</summary>
        HarvestProtect = 1 << 2,

        /// <summary>
        /// No hit harms a building here, whoever lands it: creatures, fire and every player, members and admins included
        /// (Rules/WorldRules.cs refuses the damage RPC outright). A piece still comes down with the hammer, and weather
        /// and loss of support still apply.
        /// </summary>
        StructureProtect = 1 << 3,

        /// <summary>No hostile creature spawns: world spawns, raid spawns, spawn points and nests.</summary>
        NoMonsterSpawns = 1 << 4,

        /// <summary>Raids never pick a player standing here.</summary>
        NoRaids = 1 << 5,

        /// <summary>Dying here does not lower skills.</summary>
        NoSkillLoss = 1 << 6,

        /// <summary>Players here get the territory's status effect with its regen / carry / armour values.</summary>
        Sanctuary = 1 << 7,

        /// <summary>Show a centre-screen banner on the way in and out.</summary>
        Announce = 1 << 8,

        /// <summary>Draw the territory on the large map.</summary>
        ShowOnMap = 1 << 9,

        /// <summary>Draw its border on the ground for players near the edge.</summary>
        ShowBorder = 1 << 10,

        // 0.3.0 - the runic barrier (Rules/Barrier.cs enforces it, Visuals/BarrierFlare.cs shows it). Each needs the
        // flag before it, and Territory.CleanFlags clears one whose prerequisite is off - so a record never says
        // "barrier" without "no monster spawns", whichever of the panel, the file or a client it came from.

        /// <summary>
        /// Monsters cannot walk in: a hostile creature that reaches the edge is held outside it, and the barrier
        /// flares where it pushes. Part of the no-monster rule: needs NoMonsterSpawns.
        /// </summary>
        RunicBarrier = 1 << 11,

        /// <summary>With RunicBarrier: players who are not members cannot walk in either (admins pass while
        /// AdminsBypassProtection is on). Needs RunicBarrier.</summary>
        BarrierKeepsOutVisitors = 1 << 12,

        // 0.3.1 - a rule that stands alone (no prerequisite), OFF unless an admin switches it on: not in DefaultFlags, not
        // in DraftFlags, so no territory gains it by upgrading and a new one does not start with it.

        /// <summary>
        /// A door or gate left open inside closes itself after the server's AutoCloseDelay, unless a player is close to
        /// it. Run by the SERVER alone (Rules/DoorCloser.cs): a door's whole state is one number on its object, and the
        /// server writing it closes the door in every game. It is about the door, not who used it, so members are not
        /// exempt. Where territories overlap, the one that governs the door's spot decides (TerritoryIndex.GoverningAt).
        /// </summary>
        AutoCloseDoors = 1 << 13,

        // 0.3.2 - a setting of the map rule, so it needs the flag before it (Territory.CleanFlags), as the barrier's do.

        /// <summary>
        /// With ShowOnMap: the large map and the minimap show only its NAME - no outline, no tint. For a continent or a
        /// region that would otherwise wash the whole map in one colour, while its name still reads on the map and in the
        /// minimap's biome label (Visuals/MapOverlay.cs, Visuals/MapBiomeLabel.cs). Needs ShowOnMap. Off for every
        /// territory by default, and in no default set, so none changes by upgrading.
        /// </summary>
        MapNameOnly = 1 << 14,

        // 0.5.0 - a rule that stands alone (no prerequisite), OFF unless an admin switches it on, like the door closer: not in
        // DefaultFlags or DraftFlags, so no territory gains guards by upgrading and a new one does not start with them.

        /// <summary>
        /// Guards stand posted inside (Rules/Guards.cs): Dvergr the SERVER keeps at posts round the middle of each part
        /// (GuardMath.Posts), who fight any monster that comes into their sight and walk back to their post, cannot be hurt and
        /// never turn on a player. A player inside who types a call word in chat ("guards") brings the nearest of them against
        /// the monster after them, or a guard who appears beside them and goes when the fight is over. Switched off, or the
        /// territory gone, the server takes its guards away; moved, it moves them. Ported from ValtimaOnline's town guards.
        /// </summary>
        Guards = 1 << 15,
    }

    /// <summary>PvP inside a territory. PERSISTED - append only.</summary>
    public enum PvPRule
    {
        /// <summary>Leave each player's own toggle alone.</summary>
        Unchanged = 0,

        /// <summary>A safe zone: PvP off for everyone inside, and the toggle locked.</summary>
        ForcedOff = 1,

        /// <summary>A war zone: PvP on for everyone inside, and the toggle locked.</summary>
        ForcedOn = 2,
    }

    /// <summary>
    /// The settings of a territory that are not rules (flags), in the groups an admin changes together. A sub-territory
    /// (Territory.ParentId) follows its parent for every group set in Territory.Follow, and sets the rest itself.
    /// PERSISTED as an int on the wire - bits are only ever added.
    /// </summary>
    [Flags]
    public enum TerritoryFollow
    {
        None = 0,

        /// <summary>The PvP rule.</summary>
        PvP = 1 << 0,

        /// <summary>The priority.</summary>
        Priority = 1 << 1,

        /// <summary>The owner and the members: who counts as a member.</summary>
        People = 1 << 2,

        /// <summary>The five numbers of the Sanctuary effect: health, stamina and eitr regen, carry weight and armour.</summary>
        SanctuaryValues = 1 << 3,

        /// <summary>The text of the entry banner.</summary>
        EnterText = 1 << 4,

        /// <summary>The text of the goodbye.</summary>
        LeaveText = 1 << 5,

        /// <summary>The colour on the map and on the ground.</summary>
        Color = 1 << 6,

        All = PvP | Priority | People | SanctuaryValues | EnterText | LeaveText | Color
    }

    public sealed class TerritoryMember
    {
        /// <summary>
        /// The player identity: PlayerProfile.m_playerID, the same number a ward records in its permitted
        /// list and a piece records as its creator (PLAYER-IDENTITY-FACTS.md §1). Never a ZDOID or a peer
        /// uid - both are per-session and would orphan the member at the next login.
        /// </summary>
        public long Id;

        public string Name = "";

        public TerritoryMember() { }

        public TerritoryMember(long id, string name)
        {
            Id = id;
            Name = name ?? "";
        }
    }

    /// <summary>One admin-marked territory: where it is, who belongs to it, and what it does.</summary>
    public sealed class Territory
    {
        /// <summary>Wire/record format version. Bump when fields are APPENDED; never reorder existing ones.</summary>
        /// <remarks>2 (0.2.0): the parts beyond the first (<see cref="ExtraParts"/>). 3 (0.4.0): the parent and what follows it (<see cref="ParentId"/>).</remarks>
        public const int RecordVersion = 3;

        public const int MaxNameLength = 48;
        public const int MaxMessageLength = 160;
        public const int MaxMembers = 128;

        /// <summary>The gilt a new territory is drawn in.</summary>
        internal static readonly Color32 DefaultColor = new Color32(214, 170, 72, 255);

        public const TerritoryFlags KnownFlags =
            TerritoryFlags.BuildProtect | TerritoryFlags.AccessProtect | TerritoryFlags.HarvestProtect |
            TerritoryFlags.StructureProtect | TerritoryFlags.NoMonsterSpawns | TerritoryFlags.NoRaids |
            TerritoryFlags.NoSkillLoss | TerritoryFlags.Sanctuary | TerritoryFlags.Announce | TerritoryFlags.ShowOnMap |
            TerritoryFlags.ShowBorder | TerritoryFlags.RunicBarrier | TerritoryFlags.BarrierKeepsOutVisitors |
            TerritoryFlags.AutoCloseDoors | TerritoryFlags.MapNameOnly | TerritoryFlags.Guards;

        /// <summary>
        /// What a record with no "flags" key in the territory file reads as, and a bare <c>new Territory()</c>. Kept exactly
        /// as 0.2.x had it: no barrier, so nothing already written, or written by hand without the key, gains one by
        /// upgrading. What the panel's draft starts with is <see cref="DraftFlags"/>.
        /// </summary>
        public const TerritoryFlags DefaultFlags =
            TerritoryFlags.BuildProtect | TerritoryFlags.AccessProtect | TerritoryFlags.StructureProtect |
            TerritoryFlags.NoMonsterSpawns | TerritoryFlags.Announce | TerritoryFlags.ShowOnMap | TerritoryFlags.ShowBorder;

        /// <summary>
        /// What the panel's draft for a NEW territory starts with (0.3.0): the defaults, and the runic barrier on
        /// whenever they keep monsters from spawning - an admin who marks ground for "no monsters" gets the shield
        /// unless they switch it off, and does not have to know it exists. Written as a rule, not a bit, so it cannot
        /// come apart from <see cref="CleanFlags"/>: were the defaults ever to drop NoMonsterSpawns, the barrier goes too.
        /// </summary>
        public const TerritoryFlags DraftFlags = (DefaultFlags & TerritoryFlags.NoMonsterSpawns) != 0
            ? DefaultFlags | TerritoryFlags.RunicBarrier
            : DefaultFlags;

        /// <summary>Assigned by the server; 0 means "not placed yet" (a draft).</summary>
        public int Id;

        public string Name = "";

        public TerritoryShapeKind Kind = TerritoryShapeKind.Circle;
        public float X;
        public float Y;
        public float Z;
        public float HalfX = 20f;
        public float HalfZ = 20f;
        public float Yaw;

        public Color32 Color = DefaultColor;

        /// <summary>The owner is a member like any other; the field exists so the territory can be SAID to be someone's.</summary>
        public long OwnerId;
        public string OwnerName = "";

        public List<TerritoryMember> Members = new List<TerritoryMember>();

        public TerritoryFlags Flags = DefaultFlags;
        public PvPRule PvP = PvPRule.Unchanged;

        /// <summary>
        /// Where territories overlap, the higher priority governs; on a tie the SMALLER one does, so a
        /// territory drawn inside another overrides it without anybody touching this number.
        /// </summary>
        public int Priority;

        // Sanctuary values - read only while the Sanctuary flag is set. Multipliers, 1 = vanilla.
        public float HealthRegen = 1f;
        public float StaminaRegen = 1f;
        public float EitrRegen = 1f;
        public float CarryBonus;
        public float ArmorBonus;

        /// <summary>Banner text; empty means the default. {name} and {owner} are filled in.</summary>
        public string EnterMessage = "";
        public string LeaveMessage = "";

        public string CreatedBy = "";
        public long CreatedTicks;

        /// <summary>Bumped by the server on every change to this territory.</summary>
        public int Revision;

        /// <summary>
        /// Ground added to the first part (the Kind/X/Z/HalfX/HalfZ/Yaw fields above), by combining another
        /// territory into this one or by adding a part. Empty for a territory marked once. With the first part
        /// it is ONE area - <see cref="Ground"/> - with one set of rules, one banner and one outline.
        /// </summary>
        public List<TerritoryShape> ExtraParts = new List<TerritoryShape>();

        // ─────────────────────────────────────────────────────────────
        // SUB-TERRITORIES (0.4.0)
        //
        // A territory may sit inside another as its SUB-territory (a town in a continent). It starts as a copy of its parent's
        // settings and goes on FOLLOWING them - when the parent changes, so does it - until a setting is changed on the
        // sub-territory itself: that one setting is then its own, and the rest still follow. What follows is two masks,
        // FollowFlags (rule by rule) and Follow (group by group, for everything that is not a rule). A record with no parent
        // - every record before 0.4.0 - follows nothing and means exactly what it always did.
        //
        // TWO KINDS OF RECORD carry these fields, and the difference matters:
        //   * the STORED record (the territory file, TerritoryStore) holds only what the territory sets itself; what it follows is
        //     left at the plain defaults (Inheritance.Canonicalize);
        //   * the EFFECTIVE record (Inheritance.Resolve) has the followed values filled in from the parent. That is what
        //     TerritoryIndex holds and what every rule reads - so no rule needs to know any of this. The masks stay on it, so
        //     the panel can show which settings are inherited. The wire carries the STORED records (a continent's member list
        //     is not copied into every town that follows it), and every game resolves them with the same code.
        // ─────────────────────────────────────────────────────────────

        /// <summary>The territory this one is a sub-territory of; 0 for a top-level one. <see cref="AutoParent"/> only ever in a Create request.</summary>
        public int ParentId;

        /// <summary>The rules (flags) this sub-territory takes from its parent. Always none without a parent.</summary>
        public TerritoryFlags FollowFlags;

        /// <summary>The other settings this sub-territory takes from its parent. Always none without a parent.</summary>
        public TerritoryFollow Follow;

        /// <summary>In a Create request: the territory the new one is placed in, picked by the server (the one governing the new ground's middle).</summary>
        public const int AutoParent = -1;

        public bool IsSub => ParentId > 0;

        /// <summary>Does this territory take this ONE rule from its parent?</summary>
        public bool Follows(TerritoryFlags flag) => ParentId > 0 && (FollowFlags & flag) != 0;

        /// <summary>Does this territory take this ONE group of settings from its parent?</summary>
        public bool Follows(TerritoryFollow group) => ParentId > 0 && (Follow & group) != 0;

        /// <summary>The FIRST part only. Anything asking where the territory is wants <see cref="Ground"/>.</summary>
        public TerritoryShape Shape => new TerritoryShape(Kind, X, Z, HalfX, HalfZ, Yaw);

        /// <summary>The whole ground, every part: what the rules, the borders and the map read.</summary>
        public TerritoryGround Ground => new TerritoryGround(Shape, ExtraParts);

        public int PartCount => 1 + (ExtraParts?.Count ?? 0);

        /// <summary>Part <paramref name="index"/>: 0 is the first part, 1.. the extra parts in order.</summary>
        public TerritoryShape GetPart(int index) => index == 0 ? Shape : ExtraParts[index - 1];

        /// <summary>Replaces one part. Part 0 lives in this record's own geometry fields; its height (Y) is kept.</summary>
        public void SetPart(int index, TerritoryShape part)
        {
            if (index != 0)
            {
                ExtraParts[index - 1] = part;
                return;
            }
            Kind = part.Kind;
            X = part.CenterX;
            Z = part.CenterZ;
            HalfX = part.HalfX;
            HalfZ = part.HalfZ;
            Yaw = part.Yaw;
        }

        /// <summary>Drops one part; dropping the first moves the second into its place. The caller keeps at least one.</summary>
        public void RemovePartAt(int index)
        {
            if (index != 0)
            {
                ExtraParts.RemoveAt(index - 1);
                return;
            }
            SetPart(0, ExtraParts[0]);
            ExtraParts.RemoveAt(0);
        }

        public Vector3 Center => new Vector3(X, Y, Z);

        public bool Has(TerritoryFlags flag) => (Flags & flag) != 0;

        public void Set(TerritoryFlags flag, bool on)
        {
            if (on) Flags |= flag;
            else Flags &= ~flag;
        }

        public bool IsMember(long playerId)
        {
            if (playerId == 0L) return false;
            if (playerId == OwnerId) return true;
            for (int i = 0; i < Members.Count; i++)
                if (Members[i].Id == playerId) return true;
            return false;
        }

        public string DisplayName => string.IsNullOrEmpty(Name) ? $"Territory {Id}" : Name;

        public Territory Clone()
        {
            Territory t = (Territory)MemberwiseClone();
            t.Members = new List<TerritoryMember>(Members.Count);
            foreach (TerritoryMember m in Members) t.Members.Add(new TerritoryMember(m.Id, m.Name));
            t.ExtraParts = ExtraParts != null ? new List<TerritoryShape>(ExtraParts) : new List<TerritoryShape>();
            return t;
        }

        /// <summary>Copies everything an admin edits in the panel, leaving id, geometry and history alone.</summary>
        public void CopySettingsFrom(Territory other)
        {
            Name = other.Name;
            Color = other.Color;
            OwnerId = other.OwnerId;
            OwnerName = other.OwnerName;
            Members = new List<TerritoryMember>(other.Members.Count);
            foreach (TerritoryMember m in other.Members) Members.Add(new TerritoryMember(m.Id, m.Name));
            Flags = other.Flags;
            PvP = other.PvP;
            Priority = other.Priority;
            HealthRegen = other.HealthRegen;
            StaminaRegen = other.StaminaRegen;
            EitrRegen = other.EitrRegen;
            CarryBonus = other.CarryBonus;
            ArmorBonus = other.ArmorBonus;
            EnterMessage = other.EnterMessage;
            LeaveMessage = other.LeaveMessage;
            ParentId = other.ParentId;
            FollowFlags = other.FollowFlags;
            Follow = other.Follow;
        }

        /// <summary>Copies the whole ground: the first part and every extra part.</summary>
        public void CopyGeometryFrom(Territory other)
        {
            Kind = other.Kind;
            X = other.X;
            Y = other.Y;
            Z = other.Z;
            HalfX = other.HalfX;
            HalfZ = other.HalfZ;
            Yaw = other.Yaw;
            ExtraParts = other.ExtraParts != null ? new List<TerritoryShape>(other.ExtraParts) : new List<TerritoryShape>();
        }

        // ─────────────────────────────────────────────────────────────
        // SANITISING - run by the server on everything that arrives from a client, and on every load.
        // A client (even an admin's) is never trusted to have clamped anything.
        // ─────────────────────────────────────────────────────────────

        /// <summary>False when the geometry is unusable (NaN, infinite); everything else is clamped into range.</summary>
        public bool Sanitize(float maxHalfExtent)
        {
            if (!Finite(X) || !Finite(Y) || !Finite(Z) || !Finite(HalfX) || !Finite(HalfZ) || !Finite(Yaw))
                return false;

            if (!Enum.IsDefined(typeof(TerritoryShapeKind), Kind)) Kind = TerritoryShapeKind.Circle;
            float max = Mathf.Max(1f, maxHalfExtent);
            HalfX = Mathf.Clamp(HalfX, 1f, max);
            HalfZ = Kind == TerritoryShapeKind.Rectangle ? Mathf.Clamp(HalfZ, 1f, max) : HalfX;
            Yaw = Kind == TerritoryShapeKind.Circle ? 0f : Mathf.Repeat(Yaw, 360f);

            // Every extra part is held to the first part's rules. One that is not a number spoils the whole
            // record, as the first part would: a territory is never kept with a piece of its ground missing.
            var parts = new List<TerritoryShape>(ExtraParts?.Count ?? 0);
            foreach (TerritoryShape part in ExtraParts ?? new List<TerritoryShape>())
            {
                if (!SanitizePart(part, max, out TerritoryShape clean)) return false;
                parts.Add(clean);
            }
            ExtraParts = parts;

            Name = CleanName(Name, MaxNameLength);
            OwnerName = CleanName(OwnerName, 32);
            if (OwnerId == 0L) OwnerName = "";

            var seen = new HashSet<long>();
            var members = new List<TerritoryMember>();
            foreach (TerritoryMember m in Members ?? new List<TerritoryMember>())
            {
                if (m == null || m.Id == 0L || m.Id == OwnerId || !seen.Add(m.Id)) continue;
                members.Add(new TerritoryMember(m.Id, CleanName(m.Name, 32)));
                if (members.Count >= MaxMembers) break;
            }
            Members = members;

            if (ParentId < 0) ParentId = 0;
            FollowFlags &= KnownFlags;
            Follow &= TerritoryFollow.All;
            if (ParentId == 0)
            {
                FollowFlags = TerritoryFlags.None;
                Follow = TerritoryFollow.None;
            }

            // A territory that follows some of its rules is cleaned only AFTER those are filled in from its parent
            // (Inheritance.Resolve runs CleanFlags on the whole): its stored bits are just the ones it sets itself, and "a
            // barrier needs no-monsters" cannot be judged on them alone when the no-monsters rule is one it follows.
            Flags = FollowFlags == TerritoryFlags.None ? CleanFlags(Flags) : Flags & KnownFlags;
            if (!Enum.IsDefined(typeof(PvPRule), PvP)) PvP = PvPRule.Unchanged;
            Priority = Mathf.Clamp(Priority, -1000, 1000);

            HealthRegen = ClampFinite(HealthRegen, 0.1f, 10f, 1f);
            StaminaRegen = ClampFinite(StaminaRegen, 0.1f, 10f, 1f);
            EitrRegen = ClampFinite(EitrRegen, 0.1f, 10f, 1f);
            CarryBonus = ClampFinite(CarryBonus, 0f, 2000f, 0f);
            ArmorBonus = ClampFinite(ArmorBonus, 0f, 500f, 0f);

            EnterMessage = CleanMessage(EnterMessage);
            LeaveMessage = CleanMessage(LeaveMessage);
            CreatedBy = CleanName(CreatedBy, 64);
            Color.a = 255;
            return true;
        }

        /// <summary>
        /// The flags a record may carry: only the ones this build knows, and no flag whose prerequisite is off.
        /// RunicBarrier is part of the no-monster rule, so it needs NoMonsterSpawns; BarrierKeepsOutVisitors is a
        /// setting of the barrier, so it needs RunicBarrier. Cleared in that order, so switching the first off takes
        /// both away. MapNameOnly (0.3.2) is a setting of the map rule and needs ShowOnMap. This is the ONE place the rule lives: the server runs it through Sanitize on everything that
        /// arrives and on every load (a hand-written file included), and the panel runs it on its own copy, so what an
        /// admin sees, what is saved and what every player enforces are the same flags.
        /// </summary>
        internal static TerritoryFlags CleanFlags(TerritoryFlags flags)
        {
            flags &= KnownFlags;
            if ((flags & TerritoryFlags.NoMonsterSpawns) == 0) flags &= ~TerritoryFlags.RunicBarrier;
            if ((flags & TerritoryFlags.RunicBarrier) == 0) flags &= ~TerritoryFlags.BarrierKeepsOutVisitors;
            if ((flags & TerritoryFlags.ShowOnMap) == 0) flags &= ~TerritoryFlags.MapNameOnly;
            return flags;
        }

        private static bool Finite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);

        /// <summary>One part held to the same rules as a territory's first part: finite, a known shape, extents in [1, max].</summary>
        internal static bool SanitizePart(TerritoryShape part, float maxHalfExtent, out TerritoryShape clean)
        {
            clean = part;
            if (!Finite(part.CenterX) || !Finite(part.CenterZ) || !Finite(part.HalfX) || !Finite(part.HalfZ) || !Finite(part.Yaw))
                return false;

            float max = Mathf.Max(1f, maxHalfExtent);
            TerritoryShapeKind kind = Enum.IsDefined(typeof(TerritoryShapeKind), part.Kind) ? part.Kind : TerritoryShapeKind.Circle;
            float hx = Mathf.Clamp(part.HalfX, 1f, max);
            float hz = kind == TerritoryShapeKind.Rectangle ? Mathf.Clamp(part.HalfZ, 1f, max) : hx;
            clean = new TerritoryShape(kind, part.CenterX, part.CenterZ, hx, hz, part.Yaw);
            return true;
        }

        private static float ClampFinite(float v, float min, float max, float fallback) =>
            Finite(v) ? Mathf.Clamp(v, min, max) : fallback;

        /// <summary>
        /// Names are shown on the map, in banners and in the HUD. '&lt;' and '&gt;' would be rich-text markup
        /// and '$' a localisation token - vanilla strips all three from a map pin's name for the same reason
        /// (Minimap.OnPinTextEntered), so a territory name is held to the same rule.
        /// </summary>
        internal static string CleanName(string s, int max, bool trim = true)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(Math.Min(s.Length, max));
            foreach (char c in s)
            {
                if (c == '<' || c == '>' || c == '$' || char.IsControl(c)) continue;
                sb.Append(c);
                if (sb.Length >= max) break;
            }
            // Not trimmed while someone is TYPING it: a text field cleaned every keystroke would eat the space
            // between two words the moment it was typed. The server's Sanitize trims what finally arrives.
            return trim ? sb.ToString().Trim() : sb.ToString();
        }

        /// <summary>Banners are admin-written and may carry rich text on purpose; only length and control characters are policed.</summary>
        private static string CleanMessage(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(Math.Min(s.Length, MaxMessageLength));
            foreach (char c in s)
            {
                if (char.IsControl(c)) continue;
                sb.Append(c);
                if (sb.Length >= MaxMessageLength) break;
            }
            return sb.ToString().Trim();
        }

        // ─────────────────────────────────────────────────────────────
        // WIRE FORMAT
        //
        // Each record travels as its OWN length-prefixed ZPackage (ZPackage.Write(ZPackage) writes a byte
        // array; ReadPackage hands back a fresh package over just that array). That is what makes the
        // format safe to grow: a later build appends fields to the END of a record and bumps
        // RecordVersion, and an older reader simply stops after the fields it knows - the outer package
        // is already positioned past the whole record, so nothing after it is misread. Never reorder,
        // retype or remove a field; add new ones at the end under `if (version >= N)`.
        // ─────────────────────────────────────────────────────────────

        public void Write(ZPackage pkg)
        {
            var r = new ZPackage();
            r.Write(RecordVersion);
            r.Write(Id);
            r.Write(Name ?? "");
            r.Write((int)Kind);
            r.Write(X);
            r.Write(Y);
            r.Write(Z);
            r.Write(HalfX);
            r.Write(HalfZ);
            r.Write(Yaw);
            r.Write(PackColor(Color));
            r.Write(OwnerId);
            r.Write(OwnerName ?? "");
            r.Write(Members.Count);
            foreach (TerritoryMember m in Members)
            {
                r.Write(m.Id);
                r.Write(m.Name ?? "");
            }
            r.Write((int)Flags);
            r.Write((int)PvP);
            r.Write(Priority);
            r.Write(HealthRegen);
            r.Write(StaminaRegen);
            r.Write(EitrRegen);
            r.Write(CarryBonus);
            r.Write(ArmorBonus);
            r.Write(EnterMessage ?? "");
            r.Write(LeaveMessage ?? "");
            r.Write(CreatedBy ?? "");
            r.Write(CreatedTicks);
            r.Write(Revision);

            // version 2 (0.2.0): the parts beyond the first.
            int parts = ExtraParts?.Count ?? 0;
            r.Write(parts);
            for (int i = 0; i < parts; i++)
            {
                TerritoryShape p = ExtraParts[i];
                r.Write((int)p.Kind);
                r.Write(p.CenterX);
                r.Write(p.CenterZ);
                r.Write(p.HalfX);
                r.Write(p.HalfZ);
                r.Write(p.Yaw);
            }

            // version 3 (0.4.0): the parent, and which settings follow it.
            r.Write(ParentId);
            r.Write((int)FollowFlags);
            r.Write((int)Follow);
            pkg.Write(r);
        }

        /// <summary>Reads one record; null if it is not a record this build understands at all.</summary>
        public static Territory Read(ZPackage pkg)
        {
            ZPackage r = pkg.ReadPackage();
            int version = r.ReadInt();
            if (version < 1) return null;

            var t = new Territory
            {
                Id = r.ReadInt(),
                Name = r.ReadString(),
                Kind = (TerritoryShapeKind)r.ReadInt(),
                X = r.ReadSingle(),
                Y = r.ReadSingle(),
                Z = r.ReadSingle(),
                HalfX = r.ReadSingle(),
                HalfZ = r.ReadSingle(),
                Yaw = r.ReadSingle(),
                Color = UnpackColor(r.ReadInt()),
                OwnerId = r.ReadLong(),
                OwnerName = r.ReadString(),
            };

            int count = r.ReadInt();
            if (count < 0 || count > 4096) throw new FormatException($"territory {t.Id} claims {count} members");
            t.Members = new List<TerritoryMember>(count);
            for (int i = 0; i < count; i++)
                t.Members.Add(new TerritoryMember(r.ReadLong(), r.ReadString()));

            t.Flags = (TerritoryFlags)r.ReadInt();
            t.PvP = (PvPRule)r.ReadInt();
            t.Priority = r.ReadInt();
            t.HealthRegen = r.ReadSingle();
            t.StaminaRegen = r.ReadSingle();
            t.EitrRegen = r.ReadSingle();
            t.CarryBonus = r.ReadSingle();
            t.ArmorBonus = r.ReadSingle();
            t.EnterMessage = r.ReadString();
            t.LeaveMessage = r.ReadString();
            t.CreatedBy = r.ReadString();
            t.CreatedTicks = r.ReadLong();
            t.Revision = r.ReadInt();

            if (version >= 2)
            {
                int parts = r.ReadInt();
                if (parts < 0 || parts > 4096) throw new FormatException($"territory {t.Id} claims {parts} parts");
                for (int i = 0; i < parts; i++)
                {
                    var kind = (TerritoryShapeKind)r.ReadInt();
                    float x = r.ReadSingle();
                    float z = r.ReadSingle();
                    float halfX = r.ReadSingle();
                    float halfZ = r.ReadSingle();
                    float yaw = r.ReadSingle();
                    t.ExtraParts.Add(new TerritoryShape(kind, x, z, halfX, halfZ, yaw));
                }
            }

            if (version >= 3)
            {
                t.ParentId = r.ReadInt();
                t.FollowFlags = (TerritoryFlags)r.ReadInt();
                t.Follow = (TerritoryFollow)r.ReadInt();
            }

            // version >= 4 fields go here, each behind `if (version >= 4)`.
            return t;
        }

        internal static int PackColor(Color32 c) => (c.r << 24) | (c.g << 16) | (c.b << 8) | c.a;

        internal static Color32 UnpackColor(int v) =>
            new Color32((byte)((v >> 24) & 0xFF), (byte)((v >> 16) & 0xFF), (byte)((v >> 8) & 0xFF), 255);

        internal static string ColorToHex(Color32 c) => $"#{c.r:X2}{c.g:X2}{c.b:X2}";

        internal static bool TryParseHex(string hex, out Color32 c)
        {
            c = new Color32(214, 170, 72, 255);
            if (string.IsNullOrEmpty(hex)) return false;
            // Fully qualified: inside this class the simple name "Color" is the field, not the Unity type.
            if (!ColorUtility.TryParseHtmlString(hex.StartsWith("#") ? hex : "#" + hex, out UnityEngine.Color parsed)) return false;
            c = parsed;
            c.a = 255;
            return true;
        }

        // ─────────────────────────────────────────────────────────────
        // DESCRIPTION - one wording, shared by the panel, the status effect tooltip and the console.
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// One line per rule that is on. <paramref name="autoCloseSeconds"/> is the server's AutoCloseDelay, which the
        /// door line quotes: it lives in TSConfig, which this file (compiled into the off-game harness too) cannot see,
        /// so each caller passes it in rather than this file guessing a number that a server may have changed.
        /// </summary>
        public IEnumerable<string> FeatureLines(float autoCloseSeconds)
        {
            if (Has(TerritoryFlags.BuildProtect)) yield return "Building: members only";
            if (Has(TerritoryFlags.AccessProtect)) yield return "Doors, chests & stations: members only";
            if (Has(TerritoryFlags.HarvestProtect)) yield return "Harvesting: members only";
            if (Has(TerritoryFlags.StructureProtect)) yield return "Buildings cannot be damaged";
            if (Has(TerritoryFlags.NoMonsterSpawns)) yield return "No monster spawns";
            // The barrier is part of the no-monster rule, so its line follows that one - and is ONE line, the
            // visitors option folded in, so a card or a terr_list row does not grow for a setting of a setting.
            if (Has(TerritoryFlags.RunicBarrier))
                yield return Has(TerritoryFlags.BarrierKeepsOutVisitors)
                    ? "Runic barrier: monsters and non-members cannot walk in"
                    : "Runic barrier: monsters cannot walk in";
            if (Has(TerritoryFlags.NoRaids)) yield return "No raids";
            if (Has(TerritoryFlags.AutoCloseDoors)) yield return $"Doors close by themselves after {autoCloseSeconds:0.#} s";
            if (Has(TerritoryFlags.NoSkillLoss)) yield return "No skill loss on death";
            if (PvP == PvPRule.ForcedOff) yield return "PvP: forced off (safe zone)";
            if (PvP == PvPRule.ForcedOn) yield return "PvP: forced on (war zone)";
            if (Has(TerritoryFlags.Sanctuary))
            {
                foreach (string line in SanctuaryLines()) yield return line;
            }
        }

        public IEnumerable<string> SanctuaryLines()
        {
            if (!Mathf.Approximately(HealthRegen, 1f)) yield return $"Health regen x{HealthRegen:0.##}";
            if (!Mathf.Approximately(StaminaRegen, 1f)) yield return $"Stamina regen x{StaminaRegen:0.##}";
            if (!Mathf.Approximately(EitrRegen, 1f)) yield return $"Eitr regen x{EitrRegen:0.##}";
            if (CarryBonus > 0f) yield return $"Carry weight +{CarryBonus:0}";
            if (ArmorBonus > 0f) yield return $"Armor +{ArmorBonus:0}";
        }

        public string FillPlaceholders(string text) =>
            (text ?? "").Replace("{name}", DisplayName).Replace("{owner}", string.IsNullOrEmpty(OwnerName) ? "nobody" : OwnerName);
    }
}
