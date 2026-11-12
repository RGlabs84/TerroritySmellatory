using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace TerroritySmellatory
{
    /// <summary>
    /// The territory file, through Newtonsoft Json.NET - the standard ValheimModding-JsonDotNET dependency
    /// the rest of the workspace (Fatty, VikingOS, Wonderland, TheEye) already declares.
    /// </summary>
    /// <remarks>
    /// NOT UnityEngine.JsonUtility: in this game's runtime it silently drops a List of a mod's own class in
    /// both directions (libs-Tools/JSONUTILITY-FACTS.md) - this store's own start-up self-check caught it
    /// returning the territory list empty on the 1.0.16 dedicated server.
    ///
    /// Newtonsoft ships in both 1.0 Managed folders (13.0.2) and the JsonDotNET package installs 13.0.3; both
    /// are AssemblyVersion 13.0.0.0, so the reference binds to whichever is present (REFERENCE-DLL-PROVENANCE).
    /// Only the SERVER ever runs this file - clients receive territories over the wire, never as JSON.
    ///
    /// SHAPE: version, worldName, worldUid, nextId and a territories array; each territory is a flat object
    /// plus a members array of {id, name}. Shape and PvP are written as names ("Rectangle", "ForcedOff") and
    /// read as names in any case or as numbers; flags are a list of names, and a name this build does not
    /// know (a newer build's rule) is skipped rather than refused. A missing key takes its default and an
    /// unknown key is ignored. Anything malformed throws with Newtonsoft's path, line and position, and the
    /// store then refuses to save until the file is fixed (Net/TerritoryStore.cs).
    ///
    /// VERSION 2 (0.2.0) adds one key: "parts", the ground a territory has beyond its first part, as a list of
    /// {shape, x, z, halfX, halfZ, yaw} - the first part's own keys less its height, read with the same leniency.
    /// It is left out when there are none, so a territory marked once is written exactly as 0.1.x wrote it, and
    /// a 0.1.x file, which has no "parts" anywhere, loads exactly as it always did. The one thing an older build
    /// cannot do is keep them: a 0.1.x server skips "parts" as an unknown key and would drop it on its next save.
    ///
    /// VERSION 3 (0.3.0) adds no key at all - only two flag names, RunicBarrier and BarrierKeepsOutVisitors, in the
    /// "flags" list like any other rule, and read back by name in any case. The number moves anyway, for the guard
    /// below: a 0.2.x server, rolled back to, reads "version": 3 as a newer build's file and refuses to save over it.
    /// Without the bump it would skip the two names as flags it does not know and write the file back without them, and
    /// every barrier would be gone after its first save. A version 1 or 2 file loads here writable, and is written as 3.
    /// The barrier's own rule - a flag needs the one before it - is not the file's business: the store sanitises every
    /// territory it loads (Territory.CleanFlags), so a hand edit that sets a barrier flag alone is cleared, not refused.
    ///
    /// VERSION 4 (0.3.2) adds no key either - one more flag name, MapNameOnly, which (like the two before it) an older build
    /// would skip as unknown and then drop on its next save, turning a continent's bare name on the map into a tinted wash.
    /// So the number moves again and a 0.3.1 server rolled back to treats the file as a newer build's: it enforces it and
    /// does not save over it. (0.3.1's AutoCloseDoors did not move it; a file that has that flag and goes through 0.3.0 loses
    /// it, and this note is the record that the gap exists.) A version 1, 2 or 3 file loads here writable, and is written as 4.
    ///
    /// VERSION 5 (0.4.0) adds sub-territories: a territory may name a "parent" - the id (a number) or the name (text) of another
    /// territory in the file - and then FOLLOWS it for every setting its entry does not give. Read as: no "parent" = a top-level
    /// territory and every absent key takes its default, exactly as before; with a "parent", an absent key means "as the parent
    /// has it". "flags" on a sub-territory is a list of names (it sets ALL its rules, as a top-level territory's does) or an
    /// object of name: true/false that sets only those rules and follows the parent for the rest; absent it follows them all.
    /// The owner, ownerName and members are one group (any of the three present: the territory has its own people), and so are
    /// healthRegen to armorBonus (any present: its own Sanctuary values, the others at their defaults). A sub-territory is
    /// written with only the keys it sets itself, and "parent" always as the id. A 0.3.x server rolled back to treats a version 5
    /// file as a newer build's - never saved over, so 0.4.0 finds it intact - but it does not understand it: "parent" is a key it
    /// skips, so a sub-territory is read as a top-level one with the DEFAULT for everything it follows (not the parent's), and a
    /// "flags" object makes 0.3.2 refuse the whole file (its reader wants a list of names), which enforces nothing. Do not roll
    /// back a world that has sub-territories.
    ///
    /// NEWER FILES. From 0.2.0, a file whose version is higher than this build writes is enforced as read but never
    /// saved over (FileData.FromNewerBuild), so a server rolled back from a later build keeps that build's keys
    /// until it runs it again. The guard has to be in the build a server rolls back TO, which is why it came with
    /// version 2 and not with whatever version first needs it. The version is no gate the other way: an older file,
    /// or one without a version at all, reads and saves as it always did.
    /// </remarks>
    internal static class TerritoryJson
    {
        internal const int FileVersion = 5;

        /// <summary>The most extra parts one territory record may claim on the wire: Territory.Read's own limit.</summary>
        internal const int WireMaxExtraParts = 4096;

        internal sealed class FileData
        {
            public int Version = FileVersion;
            public string WorldName = "";
            public long WorldUid;
            public int NextId = 1;
            public List<Territory> Territories = new List<Territory>();

            /// <summary>
            /// Written by a newer build than this one. It reads - a key or a flag name this build does not know is
            /// skipped - but whatever was skipped would be gone after one save, so the store enforces such a file
            /// and never saves over it (TerritoryStore.Load).
            /// </summary>
            public bool FromNewerBuild => Version > FileVersion;
        }

        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            MissingMemberHandling = MissingMemberHandling.Ignore,
            FloatFormatHandling = FloatFormatHandling.DefaultValue,   // never write NaN/Infinity, which JSON cannot hold
            DateParseHandling = DateParseHandling.None
        };

        // ─────────────────────────────────────────────────────────────
        // THE FILE'S SHAPE - plain data, kept apart from Territory so the file and the wire format can each
        // change without dragging the other along.
        // ─────────────────────────────────────────────────────────────

        private sealed class FileDto
        {
            [JsonProperty("version")] public int Version = FileVersion;
            [JsonProperty("worldName")] public string WorldName = "";
            [JsonProperty("worldUid")] public long WorldUid;
            [JsonProperty("nextId")] public int NextId = 1;
            [JsonProperty("territories")] public List<TerritoryDto> Territories = new List<TerritoryDto>();
        }

        private sealed class TerritoryDto
        {
            [JsonProperty("id")] public int Id;
            [JsonProperty("name")] public string Name = "";

            // 0.4.0: the territory this one is inside - its id (a number) or its name (text). Left out when there is none.
            [JsonProperty("parent", NullValueHandling = NullValueHandling.Ignore)] public JToken Parent;

            [JsonProperty("shape"), JsonConverter(typeof(StringEnumConverter))] public TerritoryShapeKind Shape = TerritoryShapeKind.Circle;
            [JsonProperty("x")] public float X;
            [JsonProperty("y")] public float Y;
            [JsonProperty("z")] public float Z;
            [JsonProperty("halfX")] public float HalfX = 20f;
            [JsonProperty("halfZ")] public float HalfZ = 20f;
            [JsonProperty("yaw")] public float Yaw;

            // Straight after the first part's geometry, so a territory's whole ground reads as one block. Null -
            // and so not written at all - when there are no extra parts: absent means none.
            [JsonProperty("parts", NullValueHandling = NullValueHandling.Ignore)] public List<PartDto> Parts;

            // Every setting from here to leaveMessage can be ABSENT: on a territory with no parent that means its default; on a
            // sub-territory it means "as the parent has it" (0.4.0), and it is left out of the file when followed. Hence nullable.
            [JsonProperty("color", NullValueHandling = NullValueHandling.Ignore)] public string Color;
            [JsonProperty("ownerId", NullValueHandling = NullValueHandling.Ignore)] public long? OwnerId;
            [JsonProperty("ownerName", NullValueHandling = NullValueHandling.Ignore)] public string OwnerName;
            [JsonProperty("members", NullValueHandling = NullValueHandling.Ignore)] public List<MemberDto> Members;
            [JsonProperty("flags", NullValueHandling = NullValueHandling.Ignore)] public JToken Flags;   // a list of names, or (sub-territories) an object of name: bool
            [JsonProperty("pvp", NullValueHandling = NullValueHandling.Ignore), JsonConverter(typeof(StringEnumConverter))] public PvPRule? PvP;
            [JsonProperty("priority", NullValueHandling = NullValueHandling.Ignore)] public int? Priority;
            [JsonProperty("healthRegen", NullValueHandling = NullValueHandling.Ignore)] public float? HealthRegen;
            [JsonProperty("staminaRegen", NullValueHandling = NullValueHandling.Ignore)] public float? StaminaRegen;
            [JsonProperty("eitrRegen", NullValueHandling = NullValueHandling.Ignore)] public float? EitrRegen;
            [JsonProperty("carryBonus", NullValueHandling = NullValueHandling.Ignore)] public float? CarryBonus;
            [JsonProperty("armorBonus", NullValueHandling = NullValueHandling.Ignore)] public float? ArmorBonus;
            [JsonProperty("enterMessage", NullValueHandling = NullValueHandling.Ignore)] public string EnterMessage;
            [JsonProperty("leaveMessage", NullValueHandling = NullValueHandling.Ignore)] public string LeaveMessage;
            [JsonProperty("createdBy")] public string CreatedBy = "";
            [JsonProperty("createdTicks")] public long CreatedTicks;
            [JsonProperty("revision")] public int Revision;
        }

        /// <summary>One extra part: the first part's keys and defaults, without the height a part does not have.</summary>
        private sealed class PartDto
        {
            [JsonProperty("shape"), JsonConverter(typeof(StringEnumConverter))] public TerritoryShapeKind Shape = TerritoryShapeKind.Circle;
            [JsonProperty("x")] public float X;
            [JsonProperty("z")] public float Z;
            [JsonProperty("halfX")] public float HalfX = 20f;
            [JsonProperty("halfZ")] public float HalfZ = 20f;
            [JsonProperty("yaw")] public float Yaw;
        }

        private sealed class MemberDto
        {
            [JsonProperty("id")] public long Id;
            [JsonProperty("name")] public string Name = "";
        }

        // ─────────────────────────────────────────────────────────────
        // WRITE / READ
        // ─────────────────────────────────────────────────────────────

        internal static string Write(FileData f)
        {
            var dto = new FileDto
            {
                WorldName = f.WorldName ?? "",
                WorldUid = f.WorldUid,
                NextId = f.NextId
            };
            foreach (Territory t in f.Territories) dto.Territories.Add(ToDto(t));
            return JsonConvert.SerializeObject(dto, Settings);
        }

        /// <summary>Parses a territory file. Throws with the path, line and position of any problem.</summary>
        internal static FileData Read(string json)
        {
            // File.ReadAllText already drops a byte-order mark; a string handed over some other way may not.
            FileDto dto = JsonConvert.DeserializeObject<FileDto>((json ?? "").TrimStart('\uFEFF'), Settings);
            if (dto == null) throw new FormatException("the file is empty");

            var f = new FileData
            {
                Version = dto.Version,
                WorldName = dto.WorldName ?? "",
                WorldUid = dto.WorldUid,
                NextId = dto.NextId
            };
            int index = 0;
            var named = new List<KeyValuePair<int, string>>();   // (position in the list, the parent's name) for the ones that name a parent
            foreach (TerritoryDto t in dto.Territories ?? new List<TerritoryDto>())
            {
                index++;
                if (t == null) throw new FormatException($"territory #{index} in the list is empty (null)");
                f.Territories.Add(FromDto(t, $"territory #{index} (id {t.Id})", $"territories[{index - 1}]", out string parentName));
                if (parentName != null) named.Add(new KeyValuePair<int, string>(f.Territories.Count - 1, parentName));
            }

            // A parent named by text is looked up once the whole list is read, so it may come later in the file than its sub-territory.
            foreach (KeyValuePair<int, string> link in named)
            {
                Territory sub = f.Territories[link.Key];
                var matches = new List<Territory>();
                foreach (Territory candidate in f.Territories)
                    if (string.Equals((candidate.Name ?? "").Trim(), link.Value, StringComparison.OrdinalIgnoreCase)) matches.Add(candidate);

                string where = $"territory #{link.Key + 1} (id {sub.Id})";
                if (matches.Count == 0) throw new FormatException($"{where}: 'parent' \"{link.Value}\" is not the name of any territory in the file");
                if (matches.Count > 1) throw new FormatException($"{where}: 'parent' \"{link.Value}\" is the name of {matches.Count} territories (ids {string.Join(", ", matches.ConvertAll(m => m.Id.ToString(CultureInfo.InvariantCulture)))}); use the id");
                sub.ParentId = matches[0].Id;
            }
            return f;
        }

        private static TerritoryDto ToDto(Territory t)
        {
            bool sub = t.ParentId > 0;
            var dto = new TerritoryDto
            {
                Id = t.Id,
                Name = t.Name ?? "",
                Parent = sub ? new JValue(t.ParentId) : null,
                Shape = t.Kind,
                X = t.X,
                Y = t.Y,
                Z = t.Z,
                HalfX = t.HalfX,
                HalfZ = t.HalfZ,
                Yaw = t.Yaw,
                CreatedBy = t.CreatedBy ?? "",
                CreatedTicks = t.CreatedTicks,
                Revision = t.Revision
            };

            // A sub-territory is written with only what it sets itself: a key that is absent is followed (0.4.0). A territory
            // with no parent sets everything, so it is written whole, exactly as 0.3.x wrote it.
            if (!t.Follows(TerritoryFollow.Color)) dto.Color = Territory.ColorToHex(t.Color);
            if (!t.Follows(TerritoryFollow.People))
            {
                dto.OwnerId = t.OwnerId;
                dto.OwnerName = t.OwnerName ?? "";
                dto.Members = new List<MemberDto>();
                foreach (TerritoryMember m in t.Members) dto.Members.Add(new MemberDto { Id = m.Id, Name = m.Name ?? "" });
            }
            if (!t.Follows(TerritoryFollow.PvP)) dto.PvP = t.PvP;
            if (!t.Follows(TerritoryFollow.Priority)) dto.Priority = t.Priority;
            if (!t.Follows(TerritoryFollow.SanctuaryValues))
            {
                dto.HealthRegen = t.HealthRegen;
                dto.StaminaRegen = t.StaminaRegen;
                dto.EitrRegen = t.EitrRegen;
                dto.CarryBonus = t.CarryBonus;
                dto.ArmorBonus = t.ArmorBonus;
            }
            if (!t.Follows(TerritoryFollow.EnterText)) dto.EnterMessage = t.EnterMessage ?? "";
            if (!t.Follows(TerritoryFollow.LeaveText)) dto.LeaveMessage = t.LeaveMessage ?? "";

            // Rules: the list of names a territory has always had. A sub-territory that follows some of them writes an object
            // of the ones it sets (name: true or false) instead, and none at all when it follows every one.
            if (!sub || t.FollowFlags == TerritoryFlags.None)
            {
                var names = new JArray();
                foreach (TerritoryFlags flag in Enum.GetValues(typeof(TerritoryFlags)))
                    if (flag != TerritoryFlags.None && t.Has(flag)) names.Add(flag.ToString());
                dto.Flags = names;
            }
            else
            {
                var own = new JObject();
                foreach (TerritoryFlags flag in Enum.GetValues(typeof(TerritoryFlags)))
                    if (flag != TerritoryFlags.None && !t.Follows(flag)) own[flag.ToString()] = t.Has(flag);
                if (own.Count > 0) dto.Flags = own;
            }

            if (t.ExtraParts != null && t.ExtraParts.Count > 0)
            {
                dto.Parts = new List<PartDto>(t.ExtraParts.Count);
                foreach (TerritoryShape p in t.ExtraParts)
                    dto.Parts.Add(new PartDto { Shape = p.Kind, X = p.CenterX, Z = p.CenterZ, HalfX = p.HalfX, HalfZ = p.HalfZ, Yaw = p.Yaw });
            }
            return dto;
        }

        private static Territory FromDto(TerritoryDto d, string where, string path, out string parentName)
        {
            int parentId = ParentOf(d.Parent, where, path, out parentName);
            bool sub = parentId > 0 || parentName != null;

            var t = new Territory
            {
                Id = d.Id,
                Name = d.Name ?? "",
                Kind = d.Shape,
                X = d.X,
                Y = d.Y,
                Z = d.Z,
                HalfX = d.HalfX,
                HalfZ = d.HalfZ,
                Yaw = d.Yaw,
                ParentId = parentId,
                CreatedBy = d.CreatedBy ?? "",
                CreatedTicks = d.CreatedTicks,
                Revision = d.Revision
            };

            // A sub-territory follows the parent for every setting its entry does not give; a territory with no parent has a
            // default for each. Either way the entry decides, key by key.
            if (sub)
            {
                t.FollowFlags = Territory.KnownFlags;
                t.Follow = TerritoryFollow.All;
            }

            if (!string.IsNullOrEmpty(d.Color))
            {
                if (!Territory.TryParseHex(d.Color, out Color32 c)) throw new FormatException($"{where}: 'color' \"{d.Color}\" is not a colour like \"#D6AA48\"");
                t.Color = c;
                t.Follow &= ~TerritoryFollow.Color;
            }

            if (d.PvP.HasValue)
            {
                t.PvP = d.PvP.Value;
                t.Follow &= ~TerritoryFollow.PvP;
            }
            if (d.Priority.HasValue)
            {
                t.Priority = d.Priority.Value;
                t.Follow &= ~TerritoryFollow.Priority;
            }

            if (d.OwnerId.HasValue || d.OwnerName != null || d.Members != null)
            {
                t.OwnerId = d.OwnerId ?? 0L;
                t.OwnerName = d.OwnerName ?? "";
                foreach (MemberDto m in d.Members ?? new List<MemberDto>())
                    if (m != null) t.Members.Add(new TerritoryMember(m.Id, m.Name ?? ""));
                t.Follow &= ~TerritoryFollow.People;
            }

            if (d.HealthRegen.HasValue || d.StaminaRegen.HasValue || d.EitrRegen.HasValue || d.CarryBonus.HasValue || d.ArmorBonus.HasValue)
            {
                t.HealthRegen = d.HealthRegen ?? 1f;
                t.StaminaRegen = d.StaminaRegen ?? 1f;
                t.EitrRegen = d.EitrRegen ?? 1f;
                t.CarryBonus = d.CarryBonus ?? 0f;
                t.ArmorBonus = d.ArmorBonus ?? 0f;
                t.Follow &= ~TerritoryFollow.SanctuaryValues;
            }

            if (d.EnterMessage != null)
            {
                t.EnterMessage = d.EnterMessage;
                t.Follow &= ~TerritoryFollow.EnterText;
            }
            if (d.LeaveMessage != null)
            {
                t.LeaveMessage = d.LeaveMessage;
                t.Follow &= ~TerritoryFollow.LeaveText;
            }

            ReadFlags(t, d.Flags, sub, where, path);

            // A part is counted from 1 along its own "parts" list, as a territory is along "territories" - so the
            // number is the entry to look at, not the panel's part number (which counts the first part as 1).
            // Newtonsoft has already refused anything of the wrong TYPE, naming its path; what is left to refuse
            // here is an empty entry and a number JSON parsers accept but a position cannot be: NaN, Infinity,
            // or a value too large for a float. The first part's own numbers are refused later, by the store's
            // Sanitize; a part's are refused here so the message can say which part it was.
            //
            // And a count the wire cannot carry: Territory.Read refuses a record claiming more extra parts than
            // that, and a client refuses the WHOLE snapshot over one such record - every player would silently lose
            // every territory. In-game edits stop at MaxParts (64 at most); only a scripted file gets near this.
            if (d.Parts != null && d.Parts.Count > WireMaxExtraParts)
                throw new FormatException($"{where} has {d.Parts.Count} entries in \"parts\"; a territory can be sent to players with at most {WireMaxExtraParts}");
            int partIndex = 0;
            foreach (PartDto p in d.Parts ?? new List<PartDto>())
            {
                partIndex++;
                string part = $"{where} part #{partIndex} in \"parts\"";
                if (p == null) throw new FormatException($"{part} is empty (null)");
                RequireFinite(p.X, part, "x");
                RequireFinite(p.Z, part, "z");
                RequireFinite(p.HalfX, part, "halfX");
                RequireFinite(p.HalfZ, part, "halfZ");
                RequireFinite(p.Yaw, part, "yaw");
                t.ExtraParts.Add(new TerritoryShape(p.Shape, p.X, p.Z, p.HalfX, p.HalfZ, p.Yaw));
            }
            return t;
        }

        /// <summary>A JSON null is a token of its own kind, not a missing one.</summary>
        private static bool Absent(JToken token) => token == null || token.Type == JTokenType.Null;

        /// <summary>
        /// The "parent" key: 0 and an empty name for none; an id (a whole number of 1 or more); or a name, which comes back in
        /// <paramref name="name"/> to be looked up once the whole file is read.
        /// </summary>
        private static int ParentOf(JToken token, string where, string path, out string name)
        {
            name = null;
            if (Absent(token)) return 0;

            if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
            {
                double v = (double)token;
                if (v == 0d) return 0;
                if (v < 1d || v > int.MaxValue || Math.Floor(v) != v)
                    throw new FormatException($"{where}: 'parent' ({path}.parent) is {token}; it must be a territory's id (a whole number of 1 or more) or its name");
                return (int)v;
            }

            if (token.Type == JTokenType.String)
            {
                string text = ((string)token ?? "").Trim();
                if (text.Length == 0) return 0;
                name = text;
                return 0;
            }
            throw new FormatException($"{where}: 'parent' ({path}.parent) must be a territory's id (a number) or its name (text), not {token.Type}");
        }

        /// <summary>
        /// "flags": a list of names sets every rule (the territory's whole rule set); an object of name: true/false sets those
        /// and, on a sub-territory, follows the parent for the others; absent is the defaults for a territory with no parent, and
        /// "follow every rule" for a sub-territory. A name this build does not know is skipped, as it always has been.
        /// </summary>
        private static void ReadFlags(Territory t, JToken token, bool sub, string where, string path)
        {
            if (Absent(token))
            {
                // Followed, so the stored record holds none of them (a new Territory starts with the defaults).
                t.Flags = sub ? TerritoryFlags.None : Territory.DefaultFlags;
                return;
            }

            if (token.Type == JTokenType.Array)
            {
                t.Flags = TerritoryFlags.None;
                foreach (JToken item in (JArray)token)
                {
                    // As 0.3.x read it, a list of strings: a null is skipped, a number is the text of the number (which names the
                    // rule bits it sets, as it always has), a word that is no rule is skipped - and only a list inside the list
                    // is refused. A file that loaded before must load now.
                    if (item.Type == JTokenType.Null || item.Type == JTokenType.Undefined) continue;
                    if (item.Type == JTokenType.Object || item.Type == JTokenType.Array)
                        throw new FormatException($"{where}: 'flags' ({path}.flags) lists rule names; {item.Type} found");
                    string name = Convert.ToString(((JValue)item).Value, CultureInfo.InvariantCulture);
                    if (!string.IsNullOrEmpty(name) && Enum.TryParse(name.Trim(), true, out TerritoryFlags flag)) t.Flags |= flag;
                }
                t.FollowFlags = TerritoryFlags.None;
                return;
            }

            if (token.Type == JTokenType.Object)
            {
                t.Flags = TerritoryFlags.None;
                TerritoryFlags set = TerritoryFlags.None;
                foreach (KeyValuePair<string, JToken> pair in (JObject)token)
                {
                    if (pair.Value == null || pair.Value.Type != JTokenType.Boolean)
                        throw new FormatException($"{where}: flag \"{pair.Key}\" ({path}.flags) must be true or false");
                    if (string.IsNullOrEmpty(pair.Key) || !Enum.TryParse(pair.Key.Trim(), true, out TerritoryFlags flag)) continue;
                    set |= flag;
                    if ((bool)pair.Value) t.Flags |= flag;
                }
                // A territory with no parent has nothing to follow: what the object leaves out is simply off.
                t.FollowFlags = sub ? t.FollowFlags & ~set : TerritoryFlags.None;
                return;
            }
            throw new FormatException($"{where}: 'flags' ({path}.flags) must be a list of rule names or an object of name: true/false, not {token.Type}");
        }

        private static void RequireFinite(float value, string where, string key)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                throw new FormatException($"{where}: '{key}' is {value.ToString(CultureInfo.InvariantCulture)}, not a usable number");
        }

        // ─────────────────────────────────────────────────────────────
        // SELF-CHECK - run once per process by the store before its first save (Net/TerritoryStore.cs), and by the
        // off-game harness, which also hands it a writer or reader that loses something on purpose to prove the
        // check notices. It lives here rather than in the store so the harness compiles the shipping check itself.
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// A territory with every field set - two extra parts and every flag, the barrier's two included - and a sub-territory inside it that follows some settings and sets others, through Write -> Read, field by field.
        /// "" when everything came back exactly; otherwise the name of the first thing that did not.
        /// </summary>
        internal static string RoundTripProblem() => RoundTripProblem(Write, Read);

        /// <summary>The same check through a given writer and reader: how the harness proves it can fail.</summary>
        internal static string RoundTripProblem(Func<FileData, string> write, Func<string, FileData> read)
        {
            var sample = new Territory
            {
                Id = 7,
                Name = "Selbstprobe \"quoted\" \\ Ærø",
                Kind = TerritoryShapeKind.Rectangle,
                X = 1234.5f,
                Y = 31.25f,
                Z = -987.3f,
                HalfX = 40f,
                HalfZ = 12.5f,
                Yaw = 33.3f,
                Color = new Color32(60, 165, 160, 255),
                OwnerId = 7654321987654321L,
                OwnerName = "Owner",
                // Every flag this build knows, 0.3.0's RunicBarrier and BarrierKeepsOutVisitors included: the two names
                // have to come back from a save. All on also satisfies the barrier's prerequisites, so it is a record
                // the server would keep as it stands.
                Flags = Territory.KnownFlags,
                PvP = PvPRule.ForcedOn,
                Priority = -3,
                HealthRegen = 1.5f,
                StaminaRegen = 2f,
                EitrRegen = 0.75f,
                CarryBonus = 150f,
                ArmorBonus = 10f,
                EnterMessage = "Welcome to <color=#FFD700>{name}</color>",
                LeaveMessage = "Safe travels",
                CreatedBy = "Self-check",
                CreatedTicks = 638630000000000000L,
                Revision = 9
            };
            sample.Members.Add(new TerritoryMember(1111111111111111111L, "Astrid"));
            sample.Members.Add(new TerritoryMember(-2222222222222L, "Bjorn"));

            // Numbers with no short decimal form (a third, tenths, a yaw past 270), so a writer that rounds - or a
            // runtime whose float formatting does not round-trip - shows up here, not as ground that creeps a
            // fraction of a millimetre on every save. Both kinds a part can be that carry different fields.
            sample.ExtraParts.Add(new TerritoryShape(TerritoryShapeKind.Rectangle, -1234.567f, 8765.432f, 22f / 3f, 31.1f, 271.7f));
            sample.ExtraParts.Add(new TerritoryShape(TerritoryShapeKind.Circle, 0.1f, -0.3f, 25.123f, 25.123f, 0f));

            // A sub-territory of it (0.4.0), as the store holds one: it follows the colour, the people and the entry text, sets its
            // PvP, priority, Sanctuary numbers and goodbye itself, and sets two rules - building on, raids off - while following
            // the rest. What it follows is left at the defaults in its stored record, so that is what must come back.
            var sub = new Territory
            {
                Id = 8,
                Name = "Unter",
                Kind = TerritoryShapeKind.Circle,
                X = 1240f,
                Y = 30f,
                Z = -980f,
                HalfX = 6.5f,
                HalfZ = 6.5f,
                ParentId = 7,
                FollowFlags = Territory.KnownFlags & ~(TerritoryFlags.BuildProtect | TerritoryFlags.NoRaids),
                Follow = TerritoryFollow.Color | TerritoryFollow.People | TerritoryFollow.EnterText,
                Flags = TerritoryFlags.BuildProtect,
                PvP = PvPRule.ForcedOff,
                Priority = 4,
                HealthRegen = 2.5f,
                StaminaRegen = 0.5f,
                EitrRegen = 1.25f,
                CarryBonus = 75f,
                ArmorBonus = 3f,
                LeaveMessage = "Back to the wild",
                CreatedBy = "Self-check",
                CreatedTicks = 638630000000000001L,
                Revision = 2
            };

            var file = new FileData { WorldName = "self-check", WorldUid = -4611686018427387904L, NextId = 9 };
            file.Territories.Add(sample);
            file.Territories.Add(sub);
            FileData back = read(write(file));
            Territory t = back.Territories.Count == 2 ? back.Territories[0] : null;

            if (back.WorldUid != file.WorldUid || back.NextId != 9 || back.WorldName != "self-check") return "file header";
            if (t == null) return "territory list";
            if (t.Id != 7 || t.Name != sample.Name || t.Kind != sample.Kind) return "id/name/shape";
            if (t.X != sample.X || t.Y != sample.Y || t.Z != sample.Z || t.HalfX != sample.HalfX || t.HalfZ != sample.HalfZ || t.Yaw != sample.Yaw) return "geometry";
            if (t.ExtraParts == null || t.ExtraParts.Count != 2 || !t.ExtraParts[0].SameAs(sample.ExtraParts[0]) || !t.ExtraParts[1].SameAs(sample.ExtraParts[1])) return "parts";
            if (t.Color.r != 60 || t.Color.g != 165 || t.Color.b != 160) return "colour";
            if (t.OwnerId != sample.OwnerId || t.OwnerName != sample.OwnerName) return "owner";
            if (t.Members.Count != 2 || t.Members[0].Id != 1111111111111111111L || t.Members[1].Id != -2222222222222L || t.Members[1].Name != "Bjorn") return "members";
            if (t.Flags != sample.Flags || t.PvP != sample.PvP || t.Priority != sample.Priority) return "rules";
            if (t.HealthRegen != 1.5f || t.StaminaRegen != 2f || t.EitrRegen != 0.75f || t.CarryBonus != 150f || t.ArmorBonus != 10f) return "sanctuary values";
            if (t.EnterMessage != sample.EnterMessage || t.LeaveMessage != sample.LeaveMessage) return "messages";
            if (t.CreatedTicks != sample.CreatedTicks || t.Revision != 9 || t.CreatedBy != sample.CreatedBy) return "history";
            if (t.ParentId != 0 || t.FollowFlags != TerritoryFlags.None || t.Follow != TerritoryFollow.None) return "a top-level territory's parent";

            Territory u = back.Territories[1];
            if (u.Id != 8 || u.Name != "Unter" || u.ParentId != 7) return "sub-territory: id/name/parent";
            if (u.FollowFlags != sub.FollowFlags || u.Follow != sub.Follow) return "sub-territory: what it follows";
            if (u.Flags != TerritoryFlags.BuildProtect || u.PvP != PvPRule.ForcedOff || u.Priority != 4) return "sub-territory: its own rules";
            if (u.HealthRegen != 2.5f || u.StaminaRegen != 0.5f || u.EitrRegen != 1.25f || u.CarryBonus != 75f || u.ArmorBonus != 3f) return "sub-territory: its own Sanctuary numbers";
            if (u.LeaveMessage != "Back to the wild" || u.EnterMessage != "") return "sub-territory: its own text";
            if (u.OwnerId != 0L || u.Members.Count != 0 || u.Color.r != Territory.DefaultColor.r) return "sub-territory: what it follows stays at the defaults";
            if (u.X != 1240f || u.Z != -980f || u.HalfX != 6.5f || u.Revision != 2) return "sub-territory: ground and history";
            return "";
        }
    }
}
