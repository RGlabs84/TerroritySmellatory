using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEngine;

namespace TerroritySmellatory
{
    /// <summary>
    /// Console commands. None is a cheat command: a client on a dedicated server can never pass vanilla's
    /// cheat gate (IsCheatsEnabled is IsServer), and none needs to - every change is authorised by the
    /// SERVER when the request arrives, never by a client-side flag (CONSOLE-COMMAND-ROUTING-FACTS).
    /// </summary>
    internal static class Commands
    {
        internal static void Register()
        {
            // Registration is a write into a static dictionary, so doing it in Awake works everywhere,
            // headless included - no waiting for a Terminal instance.
            new Terminal.ConsoleCommand("terr", "Opens or closes the territory panel.", args => TerritoryPanel.Toggle());

            new Terminal.ConsoleCommand("terr_place",
                "x=<x> z=<z> [y=] [r= | w= d=] [a=] - what Infinity Hammer's Territory tool runs. Creates a territory with the panel's rules, or adds ground to or moves a part of the territory armed in the panel. Admins.",
                Place);

            new Terminal.ConsoleCommand("terr_remove_at",
                "x=<x> z=<z> - what the 'Territory remove' hammer tool runs: removes the territory governing that spot, every part of it, on the second use within a few seconds. Admins.",
                RemoveAt);

            new Terminal.ConsoleCommand("terr_edit_at",
                "x=<x> z=<z> - what the 'Territory edit' hammer tool runs: opens the panel on the territory governing that spot, marking the part it landed in. Admins.",
                EditAt);

            new Terminal.ConsoleCommand("terr_combine_at",
                $"x=<x> z=<z> - what the 'Territory combine' hammer tool runs: the first use picks the territory to keep, a use inside another within {CombineSeconds:0} s joins that one into it. Admins.",
                CombineAt);

            new Terminal.ConsoleCommand("terr_here",
                "[radius] - marks a circle around you with the panel's rules - or, when the panel has armed it, adds the circle to a territory or moves a part there. No hammer needed. Admins.",
                Here);

            new Terminal.ConsoleCommand("terr_list", "Lists this world's territories as a tree - each sub-territory under the one it sits in, with what it sets itself - with every part of a combined one and each one's rules.", List);

            new Terminal.ConsoleCommand("terr_delete",
                "<id> [tree] - deletes a territory, every part of it. The territories inside it keep the rules they had and move up one level; with 'tree' they are deleted too. Admins.",
                Delete);

            new Terminal.ConsoleCommand("terr_parent",
                "<id> <parent id | none> [follow] - puts a territory inside another, or back at the top level. It keeps the settings it has, unless you add 'follow': then it takes all of the parent's and follows it. Admins.",
                SetParent);

            new Terminal.ConsoleCommand("terr_combine",
                "<into id> <from id> - joins the second territory into the first: its ground, owner and members join; its own name and rules are dropped. Admins.",
                Combine);

            // A statement body on purpose: an expression lambda returning bool would bind to the
            // ConsoleEventFailable overload, which treats a false return as an error message to print.
            new Terminal.ConsoleCommand("terr_reload", "Makes the server re-read its territory file from disk. Admins.",
                args => { TerritoryClient.Send(RequestOp.Reload); });

            new Terminal.ConsoleCommand("terr_whoami",
                "Shows your player id, what the server says about you, and what this game knows about territories.",
                WhoAmI);
        }

        // ─────────────────────────────────────────────────────────────
        // terr_place - the Infinity Hammer entry point
        // ─────────────────────────────────────────────────────────────

        private static void Place(Terminal.ConsoleEventArgs args)
        {
            float fallbackY = Player.m_localPlayer != null ? Player.m_localPlayer.transform.position.y : 0f;
            if (!PlacementArgs.TryParse(args.Args, fallbackY, TSConfig.HammerInitialSize.Value, out Territory geometry))
            {
                TerritoryClient.Print("terr_place needs at least x= and z= (Infinity Hammer's Territory tool fills them in).");
                return;
            }
            TerritoryPanel.SubmitPlacement(geometry);
        }

        // ─────────────────────────────────────────────────────────────
        // terr_remove_at / terr_edit_at / terr_combine_at - the hammer tools that aim at a territory
        // ─────────────────────────────────────────────────────────────

        // Removal is the one irreversible thing a hammer click can do, and a mis-aimed click inside a big
        // territory lands on it just as surely as a careful one. So the first use only NAMES what would go,
        // and the same territory has to be hit again within ConfirmSeconds.
        private const float ConfirmSeconds = 6f;
        private static int _removeArmedFor;
        private static float _removeArmedAt = -1000f;

        private static void RemoveAt(Terminal.ConsoleEventArgs args)
        {
            if (!TryAim(args, out Vector3 point)) return;
            Territory t = TargetAt(point);
            if (t == null) return;

            bool confirmed = _removeArmedFor == t.Id && Time.realtimeSinceStartup - _removeArmedAt <= ConfirmSeconds;
            if (!confirmed)
            {
                _removeArmedFor = t.Id;
                _removeArmedAt = Time.realtimeSinceStartup;
                // A combined territory goes whole - every part, not just the one under the hammer - so say so
                // while there is still time to change one's mind.
                string all = t.PartCount > 1 ? $" - all {t.PartCount} parts" : "";
                // Whatever sits inside it keeps the rules it had and moves up a level (the server says so in its answer).
                int inside = Inheritance.ChildrenOf(TerritoryIndex.All, t.Id).Count;
                string kept = inside == 0 ? "" : inside == 1 ? " (the sub-territory inside it stays)" : $" (the {inside} sub-territories inside it stay)";
                string ask = $"Place again inside '{t.DisplayName}' (#{t.Id}) within {ConfirmSeconds:0} s to REMOVE it{all}{kept}";
                Say(ask);
                TerritoryClient.Print(ask + ".");
                return;
            }

            _removeArmedFor = 0;
            TerritoryClient.Send(RequestOp.Delete, null, t.Id);
        }

        private static void EditAt(Terminal.ConsoleEventArgs args)
        {
            if (!TryAim(args, out Vector3 point)) return;
            Territory t = TargetAt(point);
            if (t == null) return;
            TerritoryPanel.OpenEditing(t, t.Ground.PartAt(point));
        }

        // COMBINING takes two placements: the first picks the territory that stays, the second the one that joins
        // it. Nothing is sent until the second, so a stray first click costs nothing; the second must land in a
        // DIFFERENT territory within CombineSeconds, and placing in the first one again calls it off. A placement
        // in open country is a miss, not a change of mind, and leaves it armed. The target is whatever governs the
        // spot - the one the Here tab would name - so a territory nested inside another is picked by aiming at it.
        internal const float CombineSeconds = 15f;
        private static int _combineInto;
        private static float _combineArmedAt = -1000f;

        /// <summary>The territory the combine tool's next placement joins another into; 0 when it is not armed.</summary>
        internal static int CombineArmedFor =>
            _combineInto != 0 && Time.realtimeSinceStartup - _combineArmedAt <= CombineSeconds ? _combineInto : 0;

        private static void CombineAt(Terminal.ConsoleEventArgs args)
        {
            if (!TryAim(args, out Vector3 point)) return;

            int armedFor = CombineArmedFor;
            Territory into = armedFor != 0 ? TerritoryIndex.Find(armedFor) : null;

            Territory t = TargetAt(point);
            if (t == null)
            {
                if (into != null)
                    TerritoryClient.Print($"Still armed: place inside another territory within {CombineSeconds:0} s of the first to join it into '{into.DisplayName}'.");
                return;
            }

            if (into == null)
            {
                _combineInto = t.Id;
                _combineArmedAt = Time.realtimeSinceStartup;
                Say($"Now place inside the territory to join into '{t.DisplayName}'");
                TerritoryClient.Print($"'{t.DisplayName}' (#{t.Id}) stays. Within {CombineSeconds:0} s, place inside another territory to join it into this one - or inside '{t.DisplayName}' again to cancel.");
                return;
            }

            _combineInto = 0;
            if (t.Id == into.Id)
            {
                Say("Combine cancelled");
                TerritoryClient.Print($"Combine cancelled: '{into.DisplayName}' was picked twice.");
                return;
            }

            Say($"Joining '{t.DisplayName}' into '{into.DisplayName}'");
            TerritoryClient.Send(RequestOp.Combine, null, into.Id, t.Id);
        }

        /// <summary>
        /// The x=/z= aim point a hammer tool hands over. Asked of an admin only: a client the server has called
        /// a non-admin gets no further, so it never sends anything (MayRequest - Wonderland's RPC whitelist kicks).
        /// </summary>
        private static bool TryAim(Terminal.ConsoleEventArgs args, out Vector3 point)
        {
            point = Vector3.zero;
            if (!TerritoryClient.MayRequest(out string why))
            {
                TerritoryClient.Print(why);
                return false;
            }

            Dictionary<string, string> kv = PlacementArgs.KeyValues(args.Args);
            if (!PlacementArgs.TryFloat(kv, "x", out float x) || !PlacementArgs.TryFloat(kv, "z", out float z))
            {
                TerritoryClient.Print("Needs x= and z= (the hammer tool fills them in).");
                return false;
            }
            point = new Vector3(x, 0f, z);
            return true;
        }

        /// <summary>The territory governing that spot - the one the Here tab would name - or null, said on screen.</summary>
        private static Territory TargetAt(Vector3 point)
        {
            Territory t = TerritoryIndex.GoverningAt(point);
            if (t == null)
            {
                Say("No territory here");
                TerritoryClient.Print($"No territory at ({F(point.x)}, {F(point.z)}).");
            }
            return t;
        }

        private static void Here(Terminal.ConsoleEventArgs args)
        {
            Player player = Player.m_localPlayer;
            if (player == null)
            {
                TerritoryClient.Print("You need to be in the world.");
                return;
            }

            float radius = args.Args.Length > 1 && float.TryParse(args.Args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float r)
                ? r
                : TerritoryPanel.HereRadius;

            // Through SubmitPlacement like a hammer placement, so an arming in the panel (add ground, move a
            // part) applies to this circle exactly as it would to the Territory tool's.
            Vector3 p = player.transform.position;
            TerritoryPanel.SubmitPlacement(new Territory
            {
                Kind = TerritoryShapeKind.Circle,
                X = p.x,
                Y = p.y,
                Z = p.z,
                HalfX = radius,
                HalfZ = radius
            });
        }

        private static void Delete(Terminal.ConsoleEventArgs args)
        {
            if (args.Args.Length < 2 || !TryId(args.Args[1], out int id))
            {
                TerritoryClient.Print("Usage: terr_delete <id> [tree]   (terr_list shows the ids; 'tree' deletes the territories inside it as well)");
                return;
            }
            bool tree = args.Args.Length > 2 && string.Equals(args.Args[2], "tree", StringComparison.OrdinalIgnoreCase);
            TerritoryClient.Send(tree ? RequestOp.DeleteTree : RequestOp.Delete, null, id);
        }

        /// <summary>terr_parent: what the panel's Inside row does, for an admin without the panel. The server checks it all again.</summary>
        private static void SetParent(Terminal.ConsoleEventArgs args)
        {
            string usage = "Usage: terr_parent <id> <parent id | none> [follow]   (terr_list shows the ids)";
            if (args.Args.Length < 3 || !TryId(args.Args[1], out int id)) { TerritoryClient.Print(usage); return; }

            int parent = 0;
            string word = args.Args[2];
            if (!string.Equals(word, "none", StringComparison.OrdinalIgnoreCase) && (!TryId(word, out parent) || parent < 0)) { TerritoryClient.Print(usage); return; }

            Territory live = TerritoryIndex.Find(id);
            if (live == null) { TerritoryClient.Print($"There is no territory #{id}."); return; }
            Territory up = parent > 0 ? TerritoryIndex.Find(parent) : null;
            if (parent > 0 && up == null) { TerritoryClient.Print($"There is no territory #{parent} to put it inside."); return; }

            string refused = Inheritance.CheckParent(TerritoryIndex.All, id, Math.Max(parent, 0));
            if (refused != null) { TerritoryClient.Print("Refused: " + refused); return; }

            bool follow = parent > 0 && args.Args.Length > 3 && string.Equals(args.Args[3], "follow", StringComparison.OrdinalIgnoreCase);
            if (live.ParentId == Math.Max(parent, 0) && !follow)
            {
                TerritoryClient.Print(parent > 0 ? $"#{id} is already inside #{parent}." : $"#{id} is already at the top level.");
                return;
            }

            // Moving a territory never changes how it behaves: what it shows now (what it followed from the parent it had included)
            // is what it keeps, and it follows the new parent only when asked.
            Territory record = live.Clone();
            record.ParentId = Math.Max(parent, 0);
            record.FollowFlags = TerritoryFlags.None;
            record.Follow = TerritoryFollow.None;
            if (follow) Inheritance.PullAll(record, up);
            TerritoryClient.Send(RequestOp.Update, record, id);
        }

        /// <summary>terr_combine: what the Combine list and the combine tool do, for an admin without the hammer.</summary>
        private static void Combine(Terminal.ConsoleEventArgs args)
        {
            if (args.Args.Length < 3 || !TryId(args.Args[1], out int into) || !TryId(args.Args[2], out int from))
            {
                TerritoryClient.Print("Usage: terr_combine <into id> <from id> - the second joins the first and keeps nothing of its own but its ground, owner and members (terr_list shows the ids).");
                return;
            }
            if (into == from)
            {
                TerritoryClient.Print("A territory cannot be combined with itself.");
                return;
            }
            TerritoryClient.Send(RequestOp.Combine, null, into, from);
        }

        private static void List(Terminal.ConsoleEventArgs args)
        {
            IReadOnlyList<Territory> all = TerritoryIndex.All;
            if (all.Count == 0)
            {
                TerritoryClient.Print(TerritoryClient.HasSnapshot ? "This world has no territories." : "No territory list has arrived from the server yet.");
                return;
            }

            Vector3 me = Player.m_localPlayer != null ? Player.m_localPlayer.transform.position : Vector3.zero;
            foreach (Inheritance.TreeRow row in Inheritance.TreeOrder(all, (a, b) =>
            {
                int c = string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
                return c != 0 ? c : a.Id.CompareTo(b.Id);
            }))
            {
                Territory t = row.Territory;
                string pad = new string(' ', row.Depth * 2);
                var sb = new StringBuilder();
                sb.Append(pad).Append($"#{t.Id} '{t.DisplayName}' - ");
                sb.Append(t.PartCount > 1 ? $"{t.PartCount} parts" : PanelMath.PartSummary(t.Shape));
                if (Player.m_localPlayer != null) sb.Append($", {t.Ground.DistanceOutside(me):0} m away");
                sb.Append($", owner {(string.IsNullOrEmpty(t.OwnerName) ? "none" : t.OwnerName)}, {t.Members.Count} member(s), priority {t.Priority}");
                TerritoryClient.Print(sb.ToString());
                if (t.IsSub)
                {
                    // Where it sits, and how much of it is its own: the rest is its parent's, now and after the parent changes.
                    Territory up = TerritoryIndex.Find(t.ParentId);
                    List<string> own = Inheritance.SetsItself(t);
                    TerritoryClient.Print($"{pad}    inside #{t.ParentId} '{(up != null ? up.DisplayName : "?")}': " +
                                          (own.Count == 0 ? "follows it for everything" : "sets itself: " + string.Join(", ", own)));
                }
                if (t.PartCount > 1)
                    for (int i = 0; i < t.PartCount; i++)
                        TerritoryClient.Print($"{pad}    part {i + 1}: {PanelMath.PartSummary(t.GetPart(i))}");
                string rules = string.Join("; ", t.FeatureLines(TSConfig.AutoCloseDelay.Value));
                if (rules.Length > 0) TerritoryClient.Print(pad + "    " + rules);
            }
        }

        private static void WhoAmI(Terminal.ConsoleEventArgs args)
        {
            Player player = Player.m_localPlayer;
            TerritoryClient.Print($"player: {(player != null ? $"{player.GetPlayerName()} (id {player.GetPlayerID()})" : "none")}");

            if (ZNet.instance == null)
            {
                TerritoryClient.Print("not in a world");
                return;
            }

            if (ZNet.instance.IsServer())
                TerritoryClient.Print("this machine IS the server (host / single player): you are its admin");
            else
                TerritoryClient.Print(TerritoryClient.HasSnapshot
                    ? $"server says: {(TerritoryClient.IsAdmin ? "ADMIN" : "not an admin")}, admins bypass protections: {TerritoryClient.AdminsBypass}"
                    : "the server has not sent a territory list yet (it does once your character is in the world)");

            TerritoryClient.Print($"territories known: {TerritoryIndex.All.Count}, server revision {TerritoryClient.ServerRevision}");
            Territory here = Presence.Current;
            string inside = here != null && here.IsSub
                ? ", inside " + string.Join(" > ", Inheritance.Ancestors(TerritoryIndex.All, here.Id).Select(a => $"'{a.DisplayName}'"))
                : "";
            TerritoryClient.Print(here != null
                ? $"standing in #{here.Id} '{here.DisplayName}'{inside} - you are {(player != null && here.IsMember(player.GetPlayerID()) ? "a member" : "not a member")}"
                : "standing in open country");
            TerritoryClient.Print($"Infinity Hammer: {(InfinityHammerBridge.Installed ? "installed" : "not installed")}, tool file: {InfinityHammerBridge.ToolFilePath}");

            string armed = TerritoryPanel.ArmedAction;
            if (armed != null) TerritoryClient.Print($"armed: the next placement {armed}");
            int combine = CombineArmedFor;
            if (combine != 0)
                TerritoryClient.Print($"combine tool: armed to join a territory into #{combine}, for {CombineSeconds - (Time.realtimeSinceStartup - _combineArmedAt):0} s more");
        }

        // ─────────────────────────────────────────────────────────────

        private static void Say(string text) => MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, text);

        private static bool TryId(string s, out int id) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out id);

        private static string F(float v) => v.ToString("0.#", CultureInfo.InvariantCulture);
    }
}
