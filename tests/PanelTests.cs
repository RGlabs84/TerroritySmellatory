using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace TerroritySmellatory.Tests
{
    // The admin panel's arithmetic (UI/PanelMath.cs): how an edit in progress is carried onto the server's newer record, and, for
    // 0.4.0, what the panel says about where a territory sits and what it follows. All pure, so the harness runs the code the panel
    // runs. What it cannot check is how the panel LOOKS; the widths below are the arithmetic behind "nothing is wider than the panel".
    internal static partial class Program
    {
        static partial void PanelGroups()
        {
            Run("panel: Rebase - what the admin changed is theirs, everything else is the server's", PanelRebaseValues);
            Run("panel: Rebase - flags merge bit by bit, and a rule whose prerequisite went is cleaned", PanelRebaseFlags);
            Run("panel: Rebase - members merge one by one, and the owner is never also a member", PanelRebaseMembers);
            Run("panel: Rebase - the parent and the follow marks follow the same rule (0.4.0)", PanelRebaseMarks);
            Run("panel: Rebase - a town edited while another admin saves and the continent moves on", PanelRebaseFlow);
            Run("panel: Rebase - the parent was removed, or the admin took the territory out of it", PanelRebaseParentGone);
            Run("panel: marks - a draft with the automatic parent reads its marks, and nothing it follows has a value yet", PanelMarks);
            Run("panel: Inside - what choosing a parent does to the draft", PanelPutInsideDraft);
            Run("panel: Inside - what choosing a parent does to a territory already placed", PanelPutInsidePlaced);
            Run("panel: a click on a rule makes it the territory's own, and the rules that need it follow the change", PanelToggleRule);
            Run("panel: resync - a copy follows its parent's current values and keeps what it sets", PanelResync);
            Run("panel: the Inside row, the chain, the sentences", PanelWords);
            Run("panel: the Inside picker lists every territory in tree order and says why one is refused", PanelPicker);
            Run("panel: widths - a row of buttons takes the room it has, and no more", PanelWidths);
            Run("panel: distances and texts that were already there", PanelOldMath);
        }

        /// <summary>A top-level territory with every setting given a value, as the server holds it.</summary>
        private static Territory PanelBase()
        {
            Territory t = Sample();
            t.Revision = 5;
            t.Priority = 2;
            t.Flags = TerritoryFlags.BuildProtect | TerritoryFlags.NoMonsterSpawns | TerritoryFlags.ShowOnMap;
            return t;
        }

        private static void PanelRebaseValues()
        {
            Territory baseline = PanelBase();
            Territory mine = baseline.Clone();
            Territory server = baseline.Clone();

            // Nothing changed here: the result is the server's record, whatever it has moved on to.
            server.Revision = 6;
            server.Priority = 9;
            server.Name = "Renamed by another admin";
            server.CreatedBy = "Someone";
            server.ExtraParts.Add(new TerritoryShape(TerritoryShapeKind.Circle, 5f, 5f, 10f, 10f, 0f));
            Territory same = PanelMath.Rebase(mine, baseline, server);
            SameTerritory(server, same, "nothing changed here");
            Check(!ReferenceEquals(same, server) && !ReferenceEquals(same.Members, server.Members), "the result is a copy: the server's record is not handed out");

            // What was changed here is kept; what the server changed is the server's.
            mine.Name = "Mine";
            mine.Color = new Color32(1, 2, 3, 255);
            mine.PvP = PvPRule.ForcedOn;
            mine.HealthRegen = 3f;
            mine.ArmorBonus = 40f;
            mine.EnterMessage = "Welcome";
            server.StaminaRegen = 4f;
            server.LeaveMessage = "Bye from the server";
            Territory t = PanelMath.Rebase(mine, baseline, server);
            Check(t.Name == "Mine", "the name changed here");
            Check(t.Color.r == 1 && t.Color.g == 2 && t.Color.b == 3, "the colour changed here");
            Check(t.PvP == PvPRule.ForcedOn, "the PvP rule changed here");
            Check(t.HealthRegen == 3f && t.ArmorBonus == 40f, "the Sanctuary numbers changed here");
            Check(t.EnterMessage == "Welcome", "the banner text changed here");
            Check(t.Priority == 9, "the priority the server changed");
            Check(t.StaminaRegen == 4f && t.LeaveMessage == "Bye from the server", "other settings the server changed");
            Check(t.Revision == 6 && t.CreatedBy == "Someone" && t.PartCount == 2, "the history and the ground are the server's");

            // A setting changed on both sides is the admin's.
            mine.Priority = -7;
            Check(PanelMath.Rebase(mine, baseline, server).Priority == -7, "changed on both sides: the admin's");

            // The three records are not touched.
            Check(baseline.Name == "Hrafnborg" && baseline.Priority == 2 && server.Priority == 9 && server.Name == "Renamed by another admin" && mine.Name == "Mine",
                "the inputs are left as they were");
        }

        private static void PanelRebaseFlags()
        {
            const TerritoryFlags Monsters = TerritoryFlags.NoMonsterSpawns, Barrier = TerritoryFlags.RunicBarrier;
            Territory baseline = PanelBase();                       // BuildProtect, NoMonsterSpawns, ShowOnMap
            Territory mine = baseline.Clone();
            Territory server = baseline.Clone();

            mine.Flags |= TerritoryFlags.NoRaids;                   // switched on here
            server.Flags |= TerritoryFlags.NoSkillLoss;             // switched on by the server
            server.Flags &= ~TerritoryFlags.BuildProtect;           // switched off by the server
            Territory t = PanelMath.Rebase(mine, baseline, server);
            Check(t.Flags == (Monsters | TerritoryFlags.ShowOnMap | TerritoryFlags.NoRaids | TerritoryFlags.NoSkillLoss),
                $"bit by bit: the admin's NoRaids, the server's NoSkillLoss and no BuildProtect ({t.Flags})");

            // The same rule switched on here that the server switched off: the admin's.
            mine = baseline.Clone();
            mine.Flags &= ~TerritoryFlags.BuildProtect;
            server = baseline.Clone();
            server.Flags &= ~TerritoryFlags.BuildProtect;
            server.Flags |= TerritoryFlags.AccessProtect;
            Check(PanelMath.Rebase(mine, baseline, server).Flags == (Monsters | TerritoryFlags.ShowOnMap | TerritoryFlags.AccessProtect), "a rule both sides switched off stays off; the server's other change is kept");

            // The admin's barrier needs the server's no-monsters rule, which another admin has switched off meanwhile: cleaned.
            mine = baseline.Clone();
            mine.Flags |= Barrier;
            server = baseline.Clone();
            server.Flags &= ~Monsters;
            Territory cleaned = PanelMath.Rebase(mine, baseline, server);
            Check((cleaned.Flags & Barrier) == 0 && (cleaned.Flags & Monsters) == 0, "a barrier without the rule it stands on is cleaned, not left for the server");

            // The admin switched off the map rule that another admin's name-only setting needs.
            baseline = PanelBase();
            baseline.Flags |= TerritoryFlags.MapNameOnly;
            mine = baseline.Clone();
            mine.Flags &= ~TerritoryFlags.ShowOnMap;
            server = baseline.Clone();
            Check((PanelMath.Rebase(mine, baseline, server).Flags & TerritoryFlags.MapNameOnly) == 0, "and the setting that needs the map rule goes with it");
        }

        private static void PanelRebaseMembers()
        {
            Territory baseline = PanelBase();                        // owner Wubarrk (7654321987654321), members Astrid, Bjorn
            long astrid = baseline.Members[0].Id, bjorn = baseline.Members[1].Id;

            // The admin takes Astrid off and puts Cleo on; a combine meanwhile brings Dag in on the server.
            Territory mine = baseline.Clone();
            mine.Members.RemoveAll(m => m.Id == astrid);
            mine.Members.Add(new TerritoryMember(333L, "Cleo"));
            Territory server = baseline.Clone();
            server.Members.Add(new TerritoryMember(444L, "Dag"));
            Territory t = PanelMath.Rebase(mine, baseline, server);
            Check(t.Members.Select(m => m.Id).SequenceEqual(new[] { bjorn, 444L, 333L }),
                $"the server's list, less who the admin took off, plus who the admin put on ({string.Join(",", t.Members.Select(m => m.Name))})");

            // Nobody added or removed here: the server's list as it is.
            Territory idle = PanelMath.Rebase(baseline.Clone(), baseline, server);
            Check(idle.Members.Count == 3 && idle.Members.Exists(m => m.Id == 444L), "an admin who left the list alone gets the server's, Dag included");

            // A member put on here whom the server has put on as well is listed once.
            mine = baseline.Clone();
            mine.Members.Add(new TerritoryMember(444L, "Dag"));
            Check(PanelMath.Rebase(mine, baseline, server).Members.Count(m => m.Id == 444L) == 1, "listed once when both sides add them");

            // The owner: the admin's choice is kept, else the server's.
            mine = baseline.Clone();
            mine.OwnerId = 555L;
            mine.OwnerName = "Eir";
            server = baseline.Clone();
            server.OwnerId = 666L;
            server.OwnerName = "Fenrir";
            Territory owned = PanelMath.Rebase(mine, baseline, server);
            Check(owned.OwnerId == 555L && owned.OwnerName == "Eir", "the owner picked here");
            Territory theirs = PanelMath.Rebase(baseline.Clone(), baseline, server);
            Check(theirs.OwnerId == 666L && theirs.OwnerName == "Fenrir", "the owner the server picked, when none was picked here");

            // Make owner takes the member off the list; the merge reads that as taking them off, and the owner is not also listed.
            mine = baseline.Clone();
            mine.OwnerId = bjorn;
            mine.OwnerName = "Bjorn";
            mine.Members.RemoveAll(m => m.Id == bjorn);
            Territory made = PanelMath.Rebase(mine, baseline, baseline.Clone());
            Check(made.OwnerId == bjorn && !made.Members.Exists(m => m.Id == bjorn) && made.Members.Count == 1, "Make owner survives the merge");
            Territory listedToo = baseline.Clone();
            listedToo.OwnerId = astrid;                             // the server's owner is also on the server's list: the merge drops the double
            listedToo.OwnerName = "Astrid";
            Check(!PanelMath.Rebase(baseline.Clone(), baseline, listedToo).Members.Exists(m => m.Id == astrid), "the owner is never also a member");
        }

        private static void PanelRebaseMarks()
        {
            const TerritoryFlags Build = TerritoryFlags.BuildProtect, Raids = TerritoryFlags.NoRaids;
            Territory k = Continent();
            Territory town = Sub(2, 1, 100f, 0f, 50f, "Hrafnborg");
            Territory baseline = EffectiveOf(new[] { k, town }, 2);                    // follows everything, holds the continent's values
            Territory mine = baseline.Clone();
            Territory server = baseline.Clone();
            server.Revision = baseline.Revision + 1;

            // Nothing changed here: the parent and every mark are the server's.
            server.ParentId = 7;
            server.FollowFlags = Territory.KnownFlags & ~Raids;
            server.Follow = TerritoryFollow.All & ~TerritoryFollow.Color;
            Territory t = PanelMath.Rebase(mine, baseline, server);
            Check(t.ParentId == 7 && t.FollowFlags == server.FollowFlags && t.Follow == server.Follow, "no choice made here: where it sits and what it follows are the server's");

            // The parent moved here, whatever the server says.
            mine.ParentId = 9;
            t = PanelMath.Rebase(mine, baseline, server);
            Check(t.ParentId == 9, "a parent chosen here");
            Check(t.FollowFlags == server.FollowFlags && t.Follow == server.Follow, "(the marks are judged on their own)");

            // Per rule bit: the admin makes building its own, the server makes raids its own.
            mine = baseline.Clone();
            Inheritance.Own(mine, Build);
            mine.Flags &= ~Build;
            server = baseline.Clone();
            Inheritance.Own(server, Raids);
            server.Flags |= Raids;
            t = PanelMath.Rebase(mine, baseline, server);
            Check(t.FollowFlags == (Territory.KnownFlags & ~Build & ~Raids), $"each side's own rule stays own ({t.FollowFlags})");
            Check((t.Flags & Build) == 0 && (t.Flags & Raids) != 0, "with each side's value");

            // The admin follows again where the server had made the rule its own.
            baseline = EffectiveOf(new[] { k, town }, 2);
            baseline.FollowFlags &= ~Build;                                      // baseline: building is its own...
            baseline.Flags &= ~Build;
            mine = baseline.Clone();
            Inheritance.Pull(mine, k, Build);                                      // ...the admin pulls it back to the parent's
            server = baseline.Clone();
            server.Flags |= Raids;                                                  // and the server changes something else
            t = PanelMath.Rebase(mine, baseline, server);
            Check((t.FollowFlags & Build) != 0 && (t.Flags & Build) != 0, "pulled here: it follows, and shows the parent's value");
            Check((t.Flags & Raids) != 0, "(the server's other change is kept)");

            // Per group, the same: PvP is the admin's own, Priority the server's, People the admin follows again.
            baseline = EffectiveOf(new[] { k, town }, 2);
            baseline.Follow &= ~TerritoryFollow.People;
            baseline.OwnerId = 321L;
            baseline.OwnerName = "Own owner";
            mine = baseline.Clone();
            Inheritance.Own(mine, TerritoryFollow.PvP);
            mine.PvP = PvPRule.ForcedOff;
            Inheritance.Pull(mine, k, TerritoryFollow.People);
            server = baseline.Clone();
            Inheritance.Own(server, TerritoryFollow.Priority);
            server.Priority = 8;
            t = PanelMath.Rebase(mine, baseline, server);
            Check(t.Follow == (TerritoryFollow.All & ~TerritoryFollow.PvP & ~TerritoryFollow.Priority), $"group by group: PvP and Priority are own, People follows again ({t.Follow})");
            Check(t.PvP == PvPRule.ForcedOff && t.Priority == 8, "with each side's value");
            Check(t.OwnerId == 111L && t.Members.Count == 1 && t.Members[0].Id == 222L, "and the parent's people, as pulled here (an owner changed here is the admin's)");

            // A mark the admin did not touch is the server's even when the admin changed a value elsewhere.
            mine = baseline.Clone();
            mine.Name = "Renamed here";
            server = baseline.Clone();
            server.Follow |= TerritoryFollow.People;
            t = PanelMath.Rebase(mine, baseline, server);
            Check((t.Follow & TerritoryFollow.People) != 0 && t.Name == "Renamed here", "an untouched mark is the server's");
        }

        private static void PanelRebaseFlow()
        {
            // The real model end to end: the continent, a town that follows it, the admin's copy of the town, another admin's save, the
            // continent's own change, the panel's merge, then the refresh against the continent - and what the server would store.
            Territory k = Continent();
            Territory town = Sub(2, 1, 100f, 0f, 50f, "Hrafnborg");
            town.Revision = 4;
            var stored = new List<Territory> { k, town };

            Territory baseline = EffectiveOf(stored, 2);
            Territory mine = baseline.Clone();
            Inheritance.Own(mine, TerritoryFollow.PvP);                              // the panel: a click on a followed row is Own, then the change
            mine.PvP = PvPRule.ForcedOff;
            mine.Name = "Hrafnborg II";

            Territory other = baseline.Clone();                                       // another admin saves the same town
            Inheritance.Own(other, TerritoryFollow.Priority);
            other.Priority = 9;
            Check(TerritoryEdits.Update(stored, town, other, 600f, out Territory updated) == null, "(setup) the other admin's save is accepted");
            stored[1] = updated;
            k.Color = new Color32(1, 2, 3, 255);                                      // and the continent changes colour
            k.EnterMessage = "Hail";
            List<Territory> now = Inheritance.Resolve(stored);

            Territory merged = PanelMath.Rebase(mine, baseline, now[1]);
            Inheritance.Refresh(merged, now[0]);                                      // the panel does this after every change to the list
            Check(merged.Name == "Hrafnborg II" && merged.PvP == PvPRule.ForcedOff && !merged.Follows(TerritoryFollow.PvP), "the admin's name and own PvP survive");
            Check(!merged.Follows(TerritoryFollow.Priority) && merged.Priority == 9, "the other admin's own priority is kept");
            Check(merged.Follows(TerritoryFollow.Color) && merged.Color.r == 1 && merged.EnterMessage == "Hail", "what it follows is the continent's NEW values");
            Check(merged.Revision == updated.Revision, "and the revision is the server's");

            // What Save would send, and what the server then holds.
            string refused = TerritoryEdits.Update(stored, updated, merged, 600f, out Territory saved);
            Check(refused == null && saved.Name == "Hrafnborg II" && saved.PvP == PvPRule.ForcedOff && saved.Priority == 9, $"saved without losing either admin's change ({refused})");
            Check((saved.Follow & (TerritoryFollow.PvP | TerritoryFollow.Priority)) == TerritoryFollow.None && (saved.Follow & TerritoryFollow.Color) != 0, "the stored marks");
            Check(saved.Color.r == Territory.DefaultColor.r && saved.EnterMessage == "", "and nothing it follows is stored");
        }

        private static void PanelRebaseParentGone()
        {
            Territory k = Continent();
            Territory region = Sub(2, 1, 0f, 0f, 500f, "Region");
            Territory town = Sub(3, 2, 0f, 0f, 100f, "Town");
            var stored = new List<Territory> { k, region, town };
            List<Territory> before = Inheritance.Resolve(stored);
            Territory baseline = before[2];

            // The region is deleted while the admin has the town open: the town keeps what it did and moves up (ReleaseChildren).
            Territory mine = baseline.Clone();
            mine.Name = "Town (renamed here)";
            Territory freed = Inheritance.ReleaseChildren(stored, before, 2, 1)[0];
            Check(freed.ParentId == 1, "(setup) the town now sits in the continent");
            Territory server = Inheritance.Resolve(new[] { k, freed })[1];
            Territory t = PanelMath.Rebase(mine, baseline, server);
            Check(t.ParentId == 1 && t.Name == "Town (renamed here)", "the server's new parent, the admin's name");
            Check(t.FollowFlags == TerritoryFlags.None && t.Follow == TerritoryFollow.None, "and the server's marks: it follows nothing now, whatever the admin's copy said");

            // The admin takes the town out of any parent: it is top level, and follows nothing, though the server still has the marks.
            mine = baseline.Clone();
            mine.ParentId = 0;
            mine.FollowFlags = TerritoryFlags.None;
            mine.Follow = TerritoryFollow.None;
            server = baseline.Clone();
            server.Revision++;
            t = PanelMath.Rebase(mine, baseline, server);
            Check(t.ParentId == 0 && t.FollowFlags == TerritoryFlags.None && t.Follow == TerritoryFollow.None, "taken out: top level, no marks");

            // A top-level territory put inside another here: it follows nothing until asked.
            baseline = PanelBase();
            mine = baseline.Clone();
            mine.ParentId = 3;
            server = baseline.Clone();
            server.Revision++;
            t = PanelMath.Rebase(mine, baseline, server);
            Check(t.ParentId == 3 && t.FollowFlags == TerritoryFlags.None && t.Follow == TerritoryFollow.None, "put inside: the settings stay, nothing is followed");

            // A server whose record has no parent and marks it should not have: the result follows nothing.
            server = baseline.Clone();
            server.FollowFlags = TerritoryFlags.BuildProtect;
            Check(PanelMath.Rebase(baseline.Clone(), baseline, server).FollowFlags == TerritoryFlags.None, "marks without a parent are dropped, as the model drops them");
        }

        private static void PanelMarks()
        {
            // The draft: the automatic parent and every mark.
            var draft = new Territory { ParentId = Territory.AutoParent, FollowFlags = Territory.KnownFlags, Follow = TerritoryFollow.All };
            Check(!draft.Follows(TerritoryFlags.BuildProtect) && !draft.Follows(TerritoryFollow.PvP), "(why the panel has its own reading) Territory.Follows knows no automatic parent");
            Check(PanelMath.HasMarks(draft) && PanelMath.FollowsRule(draft, TerritoryFlags.BuildProtect) && PanelMath.FollowsGroup(draft, TerritoryFollow.People), "the draft's marks are read");
            Check(PanelMath.UnknownRule(draft, TerritoryFlags.NoRaids) && PanelMath.UnknownGroup(draft, TerritoryFollow.Color), "and nothing it follows has a value yet");
            Check(PanelMath.AllFollow(draft), "it follows everything");

            draft.FollowFlags &= ~TerritoryFlags.NoRaids;
            draft.Follow &= ~TerritoryFollow.Color;
            Check(!PanelMath.FollowsRule(draft, TerritoryFlags.NoRaids) && !PanelMath.UnknownRule(draft, TerritoryFlags.NoRaids), "a rule it sets itself has a value");
            Check(!PanelMath.UnknownGroup(draft, TerritoryFollow.Color) && PanelMath.UnknownGroup(draft, TerritoryFollow.PvP), "so does a group; the others do not");
            Check(!PanelMath.AllFollow(draft), "no longer everything follows");

            // An explicit parent: marks, and the value is known (Refresh has put it in).
            var sub = new Territory { ParentId = 4, FollowFlags = TerritoryFlags.NoRaids, Follow = TerritoryFollow.PvP };
            Check(PanelMath.FollowsRule(sub, TerritoryFlags.NoRaids) && PanelMath.FollowsGroup(sub, TerritoryFollow.PvP) && !PanelMath.UnknownRule(sub, TerritoryFlags.NoRaids) && !PanelMath.UnknownGroup(sub, TerritoryFollow.PvP),
                "inside a named territory a followed setting shows its value");

            // No parent: no marks, whatever a stray record holds.
            var top = new Territory { ParentId = 0, FollowFlags = TerritoryFlags.NoRaids, Follow = TerritoryFollow.PvP };
            Check(!PanelMath.HasMarks(top) && !PanelMath.FollowsRule(top, TerritoryFlags.NoRaids) && !PanelMath.FollowsGroup(top, TerritoryFollow.PvP) && !PanelMath.AllFollow(top), "a top-level territory has no marks column");
            Check(!PanelMath.HasMarks(null) && !PanelMath.AllFollow(null), "and nothing is nothing");
            Check(PanelMath.AllFollow(new Territory { ParentId = 1, FollowFlags = Territory.KnownFlags, Follow = TerritoryFollow.All }), "all marks set, with a parent: follow all has nothing left to do");

            // The panel's own moves on a draft with the automatic parent: the values come from a fresh draft, so what follows is clean.
            Territory fresh = new Territory { Flags = Territory.DraftFlags };
            var changed = new Territory { Flags = Territory.DraftFlags & ~TerritoryFlags.BuildProtect, ParentId = Territory.AutoParent, FollowFlags = Territory.KnownFlags & ~TerritoryFlags.BuildProtect, Follow = TerritoryFollow.All };
            changed.PvP = PvPRule.ForcedOn;
            changed.Follow &= ~TerritoryFollow.PvP;
            Inheritance.PullAll(changed, fresh);
            Check(changed.FollowFlags == Territory.KnownFlags && changed.Follow == TerritoryFollow.All && changed.Flags == fresh.Flags && changed.PvP == fresh.PvP && changed.ParentId == Territory.AutoParent,
                "follow all on an automatic draft: every mark, the fresh values, the parent still automatic");
        }

        /// <summary>What the panel's FreshDraft() is: a new territory marked inside whatever it lands in, following everything.</summary>
        private static Territory PanelFresh() => new Territory
        {
            Name = "",
            Flags = Territory.DraftFlags,
            ParentId = Territory.AutoParent,
            FollowFlags = Territory.KnownFlags,
            Follow = TerritoryFollow.All
        };

        /// <summary>The territory list as TerritoryIndex.Find sees it: the effective records.</summary>
        private static Func<int, Territory> PanelFind(IEnumerable<Territory> stored)
        {
            List<Territory> effective = Inheritance.Resolve(stored.ToList());
            return id => effective.FirstOrDefault(t => t.Id == id);
        }

        private static void PanelPutInsideDraft()
        {
            Territory k = Continent();
            Territory other = Circle(9, 5000f, 0f, 500f, TerritoryFlags.NoRaids | TerritoryFlags.ShowOnMap, priority: 1);
            other.PvP = PvPRule.ForcedOff;
            Func<int, Territory> find = PanelFind(new[] { k, other });
            Territory effK = find(1), effOther = find(9);

            // A fresh draft (automatic) is given a parent: it already follows everything, and now shows the parent's values.
            Territory draft = PanelFresh();
            PanelMath.PutInside(draft, 1, true, PanelFresh(), find);
            Check(draft.ParentId == 1 && draft.FollowFlags == Territory.KnownFlags && draft.Follow == TerritoryFollow.All, "an untouched draft inside a territory follows all of it");
            Check(draft.Flags == effK.Flags && draft.PvP == effK.PvP && draft.Priority == effK.Priority && draft.OwnerId == 111L && draft.Members.Count == 1 &&
                  draft.EnterMessage == effK.EnterMessage && draft.Color.r == effK.Color.r && draft.HealthRegen == effK.HealthRegen, "and shows the parent's values, not the draft's");

            // Another parent: the marks stay, the values change over.
            PanelMath.PutInside(draft, 9, true, PanelFresh(), find);
            Check(draft.ParentId == 9 && draft.FollowFlags == Territory.KnownFlags && draft.Flags == effOther.Flags && draft.PvP == PvPRule.ForcedOff && draft.Priority == 1, "moved to another parent: still following, now its values");

            // Back to automatic: the parent is unknown again, so what follows reads as a fresh draft's.
            PanelMath.PutInside(draft, Territory.AutoParent, true, PanelFresh(), find);
            Check(draft.ParentId == Territory.AutoParent && draft.FollowFlags == Territory.KnownFlags && draft.Flags == Territory.DraftFlags && draft.PvP == PvPRule.Unchanged && draft.Priority == 0 &&
                  draft.OwnerId == 0L && draft.Members.Count == 0, "automatic again: marks kept, values a fresh draft's");

            // What the admin set on it is its own through all of that.
            draft = PanelFresh();
            PanelMath.ToggleRule(draft, TerritoryFlags.BuildProtect, PanelFresh(), find);
            Inheritance.Own(draft, TerritoryFollow.PvP);
            draft.PvP = PvPRule.ForcedOn;
            PanelMath.PutInside(draft, 1, true, PanelFresh(), find);
            Check((draft.Flags & TerritoryFlags.BuildProtect) == 0 && !draft.Follows(TerritoryFlags.BuildProtect) && draft.PvP == PvPRule.ForcedOn && !draft.Follows(TerritoryFollow.PvP),
                "building off and PvP forced on, set before a parent was picked, stay its own");
            Check((draft.Flags & TerritoryFlags.AccessProtect) != 0 && draft.Follows(TerritoryFlags.AccessProtect) && draft.Follows(TerritoryFollow.Priority) && draft.Priority == 5, "the rest follows");

            // Same parent again: nothing happens (not even a resync that could undo anything).
            Territory before = draft.Clone();
            PanelMath.PutInside(draft, 1, true, PanelFresh(), find);
            SameTerritory(before, draft, "choosing where it already is");

            // Nothing: marks gone, the values stay as they were shown.
            draft = PanelFresh();
            PanelMath.PutInside(draft, 1, true, PanelFresh(), find);
            Territory shown = draft.Clone();
            PanelMath.PutInside(draft, 0, true, PanelFresh(), find);
            Check(draft.ParentId == 0 && draft.FollowFlags == TerritoryFlags.None && draft.Follow == TerritoryFollow.None, "Nothing (top level): no marks");
            Check(draft.Flags == shown.Flags && draft.PvP == shown.PvP && draft.OwnerId == shown.OwnerId && draft.Priority == shown.Priority, "and the values it showed are its own now");

            // A TOP-LEVEL draft (the admin chose Nothing, changed things) put inside: what is still as a fresh draft has it follows; what was changed is theirs.
            draft = new Territory { Flags = Territory.DraftFlags & ~TerritoryFlags.BuildProtect, PvP = PvPRule.ForcedOff, OwnerId = 5L, OwnerName = "Me", Priority = 0 };
            PanelMath.PutInside(draft, 1, true, PanelFresh(), find);
            Check(draft.FollowFlags == (Territory.KnownFlags & ~TerritoryFlags.BuildProtect), $"a changed rule is its own, the others follow ({draft.FollowFlags})");
            Check(draft.Follow == (TerritoryFollow.All & ~(TerritoryFollow.PvP | TerritoryFollow.People)), $"changed groups are its own, the others follow ({draft.Follow})");
            Check(draft.PvP == PvPRule.ForcedOff && draft.OwnerId == 5L && (draft.Flags & TerritoryFlags.BuildProtect) == 0 && draft.Priority == 5, "its own values stay; the followed ones are the parent's");

            // The parent record is missing from the list (deleted a moment ago): the pick is made, nothing is refreshed, nothing throws.
            draft = PanelFresh();
            PanelMath.PutInside(draft, 77, true, PanelFresh(), find);
            Check(draft.ParentId == 77 && draft.Flags == Territory.DraftFlags, "a parent that is not in the list: the draft is as it was");
        }

        private static void PanelPutInsidePlaced()
        {
            Territory k = Continent();
            Territory other = Circle(9, 5000f, 0f, 500f, TerritoryFlags.NoRaids | TerritoryFlags.ShowOnMap, priority: 1);
            Func<int, Territory> find = PanelFind(new[] { k, other });

            // A top-level territory put inside one: it means exactly what it did, and follows nothing until asked.
            Territory t = PanelBase();
            t.Id = 20;
            Territory was = t.Clone();
            PanelMath.PutInside(t, 1, false, PanelFresh(), find);
            Check(t.ParentId == 1 && t.FollowFlags == TerritoryFlags.None && t.Follow == TerritoryFollow.None, "put inside: no marks");
            SameSettings(was, t, "put inside");
            Check(PanelMath.HasMarks(t) && !PanelMath.AllFollow(t), "the marks column appears, every row 'own'");

            // "Follow all of Svartland": every setting is the parent's, name and ground are not.
            Inheritance.PullAll(t, find(1));
            Territory effK = find(1);
            Check(PanelMath.AllFollow(t) && t.Flags == effK.Flags && t.PvP == effK.PvP && t.OwnerId == effK.OwnerId && t.Name == was.Name && t.X == was.X, "follow all: the parent's settings, its own name and ground");

            // A sub-territory moved to another parent means exactly what it meant, and follows nothing until asked - as one put inside from the top level.
            Territory town = Sub(2, 1, 100f, 0f, 50f, "Hrafnborg");
            Territory view = Inheritance.Resolve(new[] { k, town })[1];
            Inheritance.Own(view, TerritoryFollow.Priority);
            view.Priority = 99;
            Territory shownBefore = view.Clone();
            PanelMath.PutInside(view, 9, false, PanelFresh(), find);
            Territory effOther = find(9);
            Check(view.ParentId == 9 && view.FollowFlags == TerritoryFlags.None && view.Follow == TerritoryFollow.None, "moved: no marks");
            SameSettings(shownBefore, view, "moved");
            Check(view.Priority == 99 && view.Flags != effOther.Flags, "(it is not the new parent's rules)");
            Territory followed = view.Clone();
            Inheritance.PullAll(followed, effOther);
            Check(PanelMath.AllFollow(followed) && followed.Flags == effOther.Flags && followed.PvP == effOther.PvP, "'Follow all of ...' then takes the new parent's");

            // Taken out of every parent: the marks go, and the values it showed are its own, so it does not change behaviour.
            Territory shown = view.Clone();
            PanelMath.PutInside(view, 0, false, PanelFresh(), find);
            Check(view.ParentId == 0 && view.FollowFlags == TerritoryFlags.None && view.Follow == TerritoryFollow.None, "taken out: no marks");
            Check(view.Flags == shown.Flags && view.PvP == shown.PvP && view.Priority == 99 && view.OwnerId == shown.OwnerId, "and it means what it meant");
        }

        private static T Also<T>(this T value, Action<T> change)
        {
            change(value);
            return value;
        }

        /// <summary>Everything a panel edits, apart from where it sits and the marks.</summary>
        private static void SameSettings(Territory a, Territory b, string what)
        {
            Check(a.Name == b.Name && a.Flags == b.Flags && a.PvP == b.PvP && a.Priority == b.Priority && a.OwnerId == b.OwnerId && a.Members.Count == b.Members.Count &&
                  a.HealthRegen == b.HealthRegen && a.ArmorBonus == b.ArmorBonus && a.EnterMessage == b.EnterMessage && a.LeaveMessage == b.LeaveMessage && a.Color.r == b.Color.r, what + ": the settings are as they were");
        }

        private static void PanelToggleRule()
        {
            const TerritoryFlags Build = TerritoryFlags.BuildProtect, Monsters = TerritoryFlags.NoMonsterSpawns, Barrier = TerritoryFlags.RunicBarrier, Keeps = TerritoryFlags.BarrierKeepsOutVisitors;
            Territory k = Circle(1, 0f, 0f, 1000f, Build | Monsters | Barrier | Keeps | TerritoryFlags.ShowOnMap);
            k.Name = "Svartland";
            Territory town = Sub(2, 1, 100f, 0f, 50f, "Hrafnborg");
            Func<int, Territory> find = PanelFind(new[] { k, town });
            Territory view = find(2).Clone();

            // Building is the parent's; switching it makes it the town's own, off, and leaves every other rule following.
            PanelMath.ToggleRule(view, Build, null, find);
            Check(!view.Follows(Build) && (view.Flags & Build) == 0, "own, and the opposite of what the parent has");
            Check(view.FollowFlags == (Territory.KnownFlags & ~Build) && (view.Flags & ~Build) == (k.Flags & ~Build), "the other rules still follow, with the parent's values");
            PanelMath.ToggleRule(view, Build, null, find);
            Check(!view.Follows(Build) && (view.Flags & Build) != 0, "switched again: still its own, on");

            // The rule the barrier needs: off takes the barrier and its option along though both follow; on brings them back.
            PanelMath.ToggleRule(view, Monsters, null, find);
            Check(!view.Follows(Monsters) && (view.Flags & (Monsters | Barrier | Keeps)) == 0, "no-monsters off: the barrier and the visitors option read off");
            Check(view.Follows(Barrier) && view.Follows(Keeps), "(they still follow)");
            PanelMath.ToggleRule(view, Monsters, null, find);
            Check((view.Flags & (Monsters | Barrier | Keeps)) == (Monsters | Barrier | Keeps), "no-monsters on again: the barrier is back, as the parent has it - the click re-reads the followed rules");

            // The same rules in a draft whose parent nobody knows: the fresh draft stands in for the parent.
            Territory draft = PanelFresh();
            PanelMath.ToggleRule(draft, Monsters, PanelFresh(), find);
            Check(!PanelMath.FollowsRule(draft, Monsters) && (draft.Flags & (Monsters | Barrier)) == 0 && PanelMath.FollowsRule(draft, Barrier), "draft: no-monsters off takes the barrier it follows");
            PanelMath.ToggleRule(draft, Monsters, PanelFresh(), find);
            Check((draft.Flags & (Monsters | Barrier)) == (Monsters | Barrier), "and on brings it back (the fresh draft has it)");
            Check(draft.ParentId == Territory.AutoParent, "(still automatic)");

            // A territory with no parent: the plain switch, with the rules that need it cleaned, exactly as before.
            Territory top = Circle(5, 0f, 0f, 10f, Monsters | Barrier | Keeps);
            PanelMath.ToggleRule(top, Monsters, null, find);
            Check(top.Flags == TerritoryFlags.None && top.FollowFlags == TerritoryFlags.None, "no parent: no-monsters off clears the barrier and the option; nothing to follow");
            PanelMath.ToggleRule(top, Build, null, find);
            Check(top.Flags == Build, "and a switch is a switch");
        }

        private static void PanelResync()
        {
            Territory k = Continent();
            Territory town = Sub(2, 1, 100f, 0f, 50f, "Hrafnborg");
            var stored = new List<Territory> { k, town };
            Territory view = Inheritance.Resolve(stored)[1];
            Inheritance.Own(view, TerritoryFollow.PvP);
            view.PvP = PvPRule.ForcedOff;

            // The continent moves on; the copy being edited follows it, and keeps its own PvP.
            k.Priority = 11;
            k.EnterMessage = "Hail";
            k.Flags |= TerritoryFlags.NoRaids;
            k.PvP = PvPRule.Unchanged;
            PanelMath.Resync(view, PanelFind(stored), null);
            Check(view.Priority == 11 && view.EnterMessage == "Hail" && (view.Flags & TerritoryFlags.NoRaids) != 0, "what it follows is the parent's new values");
            Check(view.PvP == PvPRule.ForcedOff && !view.Follows(TerritoryFollow.PvP), "what it sets is left alone");
            Territory again = view.Clone();
            PanelMath.Resync(view, PanelFind(stored), null);
            SameTerritory(again, view, "repeating it");

            // The parent has gone from the list: nothing changes, nothing throws.
            Territory orphan = view.Clone();
            orphan.ParentId = 99;
            PanelMath.Resync(orphan, PanelFind(stored), null);
            SameTerritory(view.Clone().Also(x => x.ParentId = 99), orphan, "a parent that is not there");

            // The automatic parent: a fresh draft stands in for it, and only what follows is read from it.
            Territory draft = PanelFresh();
            draft.Flags = Territory.DraftFlags | TerritoryFlags.NoRaids;
            draft.Priority = 4;
            draft.EnterMessage = "stale";
            Inheritance.Own(draft, TerritoryFlags.NoRaids);
            Inheritance.Own(draft, TerritoryFollow.Priority);
            PanelMath.Resync(draft, PanelFind(stored), PanelFresh());
            Check((draft.Flags & TerritoryFlags.NoRaids) != 0 && draft.Priority == 4, "automatic: what it sets stays");
            Check(draft.EnterMessage == "" && (draft.Flags & ~TerritoryFlags.NoRaids) == Territory.DraftFlags, "and what follows is a fresh draft's");

            // A territory with no parent has nothing to follow, and a missing copy is no copy.
            Territory top = PanelBase();
            Territory kept = top.Clone();
            PanelMath.Resync(top, PanelFind(stored), PanelFresh());
            SameTerritory(kept, top, "no parent");
            PanelMath.Resync(null, PanelFind(stored), PanelFresh());
            Check(true, "null is ignored");
        }

        private static void PanelWords()
        {
            Territory k = Continent();
            Territory town = Sub(2, 1, 100f, 0f, 50f, "Hrafnborg");
            var draft = new Territory { ParentId = Territory.AutoParent };

            Check(PanelMath.ParentLine(draft, null) == "Automatic: inside whatever it is marked in", "an automatic draft says so");
            Check(PanelMath.ParentLine(new Territory(), null) == "Nothing (top level)", "a top-level territory says so");
            Check(PanelMath.ParentLine(town, k) == "Svartland (#1)", "inside a territory: its name and number");
            Check(PanelMath.ParentLine(town, null) == "#1 (no longer exists)", "a parent that has gone says so");
            Territory unnamed = Circle(7, 0f, 0f, 5f, TerritoryFlags.None);
            unnamed.Name = "";
            Check(PanelMath.ParentLine(new Territory { ParentId = 7 }, unnamed) == "Territory 7 (#7)", "an unnamed parent is shown as the server names it");

            Check(PanelMath.PlacedWhere(draft, null) == "inside whatever it is marked in", "placed: automatic");
            Check(PanelMath.PlacedWhere(new Territory(), null) == "at the top level", "placed: top level");
            Check(PanelMath.PlacedWhere(town, k) == "inside Svartland" && PanelMath.PlacedWhere(town, null) == "inside #1", "placed: named, or gone");

            Check(PanelMath.PeopleCaption(town, k, true) == "Following Svartland's people", "people: followed");
            Check(PanelMath.PeopleCaption(draft, null, true) == "Following the people of the territory it is marked in", "people: followed, automatic");
            Check(PanelMath.PeopleCaption(town, k, false) == "Its own owner and members", "people: its own");
            Check(PanelMath.ParentName(town, k) == "Svartland" && PanelMath.ParentName(draft, null) == "the territory it is marked in" && PanelMath.ParentName(town, null) == "#1", "the parent's name in a sentence");
            Check(PanelMath.PeopleHint(town, k, true).StartsWith("The owner and members below are Svartland's") && PanelMath.PeopleHint(town, k, false).Contains("Following Svartland drops them and takes Svartland's people"), "the Members tab's hint, named");
            Check(PanelMath.PeopleHint(draft, null, true).Contains("the territory it is marked in") && PanelMath.PeopleHint(draft, null, false).Contains("the territory it is marked in supplies"), "and for the automatic parent, without a name that cannot take an apostrophe");
            Check(PanelMath.MarksHint(town, k).StartsWith("A setting marked 'follows' is Svartland's, now and when it changes.") && PanelMath.MarksHint(draft, null).StartsWith("A setting marked 'follows' takes the value of the territory it is marked in."), "the marks column's line");
            Check(new[] { PanelMath.MarkFollows, PanelMath.MarkOwn }.SequenceEqual(new[] { "follows", "own" }), "the two words of the marks column");

            // The chain: nearest first, as Inheritance.Ancestors gives it.
            Territory c = Circle(1, 0f, 0f, 1000f, TerritoryFlags.None);
            c.Name = "Svartland";
            Territory r = Sub(2, 1, 0f, 0f, 500f, "Midgard");
            Territory t = Sub(3, 2, 0f, 0f, 100f, "Hrafnborg");
            var all = new List<Territory> { c, r, t };
            Check(PanelMath.InsideChain(all, t) == "Midgard > Svartland", $"a town: its region, then the continent ({PanelMath.InsideChain(all, t)})");
            Check(PanelMath.InsideChain(all, r) == "Svartland" && PanelMath.InsideChain(all, c) == "", "a region: the continent; a continent: nothing");
            Check(PanelMath.InsideChain(all, Sub(9, 77, 0f, 0f, 1f)) == "" && PanelMath.InsideChain(all, null) == "", "a parent that is not in the list, or no territory: nothing");

            // The order siblings are listed in.
            Territory b = Circle(5, 0f, 0f, 1f, TerritoryFlags.None), a = Circle(6, 0f, 0f, 1f, TerritoryFlags.None), a2 = Circle(4, 0f, 0f, 1f, TerritoryFlags.None);
            b.Name = "bravo";
            a.Name = "Alpha";
            a2.Name = "alpha";
            var sorted = new List<Territory> { b, a, a2 };
            sorted.Sort(PanelMath.ByNameThenId);
            Check(string.Join(",", sorted.Select(x => x.Id)) == "4,6,5", $"by name (any case), then by id ({string.Join(",", sorted.Select(x => x.Id))})");

            Check(PanelMath.DeleteKeepsLabel(1) == "Delete (its 1 sub-territory keeps its rules)", "delete: one inside");
            Check(PanelMath.DeleteKeepsLabel(12) == "Delete (its 12 sub-territories keep their rules)", "delete: many inside");
            Check(PanelMath.Plural(1, "member", "members") == "1 member" && PanelMath.Plural(0, "member", "members") == "0 members" && PanelMath.Plural(5, "member", "members") == "5 members", "plurals");
        }

        private static void PanelPicker()
        {
            Territory k1 = Circle(1, 0f, 0f, 1000f, TerritoryFlags.None);
            k1.Name = "Svartland";
            Territory r = Sub(2, 1, 0f, 0f, 500f, "Midgard");
            Territory t = Sub(3, 2, 0f, 0f, 100f, "Bank");
            Territory t2 = Sub(4, 2, 300f, 0f, 100f, "Armoury");
            Territory k2 = Circle(5, 5000f, 0f, 1000f, TerritoryFlags.None);
            k2.Name = "Alfheim";
            Territory x = Sub(6, 5, 5000f, 0f, 100f, "Elm");
            var all = new List<Territory> { k1, r, t, t2, k2, x };

            // A draft (target 0): every territory can be the parent; the order is the tree, siblings by name.
            List<PanelMath.PickRow> rows = PanelMath.PickerRows(all, 0);
            Check(rows.Count == all.Count, "every territory is listed");
            Check(string.Join(" ", rows.Select(row => new string('>', row.Depth) + row.Territory.Id)) == "5 >6 1 >2 >>4 >>3", $"tree order, siblings by name ({string.Join(" ", rows.Select(row => new string('>', row.Depth) + row.Territory.Id))})");
            Check(rows.All(row => row.Refusal == null && row.Tag == null), "none is refused for a draft");

            // The region (2): itself and its own towns are refused, with the reasons the server gives; the rest are fine - its present parent too.
            rows = PanelMath.PickerRows(all, 2);
            PanelMath.PickRow self = rows.First(row => row.Territory.Id == 2), town = rows.First(row => row.Territory.Id == 3), parent = rows.First(row => row.Territory.Id == 1);
            Check(self.Tag == "itself" && self.Refusal != null && self.Refusal.Contains("inside itself"), $"itself ({self.Refusal})");
            Check(town.Tag == "inside it" && town.Refusal != null && town.Refusal.Contains("own sub-territories"), $"a territory inside it ({town.Refusal})");
            Check(rows.First(row => row.Territory.Id == 4).Tag == "inside it", "(and its other town)");
            Check(parent.Refusal == null && rows.First(row => row.Territory.Id == 5).Refusal == null && rows.First(row => row.Territory.Id == 6).Refusal == null, "where it is now, and the other tree, are fine");
            Check(rows.Count == all.Count, "refused ones are listed too, never left out");

            // A town (3): only itself is refused.
            rows = PanelMath.PickerRows(all, 3);
            Check(rows.Count(row => row.Refusal != null) == 1 && rows.First(row => row.Territory.Id == 3).Tag == "itself", "a leaf is refused only as its own parent");

            // Too deep: eight levels below the top is the most, so nothing may be put inside the ninth territory of a ladder.
            List<Territory> ladder = Ladder(9);
            rows = PanelMath.PickerRows(ladder, 0);
            PanelMath.PickRow bottom = rows.First(row => row.Territory.Id == 9);
            Check(bottom.Tag == "too deep" && bottom.Refusal != null && bottom.Refusal.Contains("at most 8"), $"a new territory below the ninth: too deep ({bottom.Refusal})");
            Check(rows.Where(row => row.Territory.Id != 9).All(row => row.Refusal == null), "and only that one");
            Check(rows.Select(row => row.Depth).SequenceEqual(Enumerable.Range(0, 9)), "a ladder is one step deeper each row");

            // A subtree that would not fit: the top of a three-level tree under the seventh of a ladder.
            var world = new List<Territory>(Ladder(7));
            Territory q = Circle(50, 9000f, 0f, 10f, TerritoryFlags.None);
            world.Add(q);
            world.Add(Sub(51, 50, 9000f, 0f, 9f));
            world.Add(Sub(52, 51, 9000f, 0f, 8f));
            rows = PanelMath.PickerRows(world, 50);
            Check(rows.First(row => row.Territory.Id == 6).Refusal == null && rows.First(row => row.Territory.Id == 7).Tag == "too deep", "what it brings with it counts: fine under the sixth, too deep under the seventh");

            Check(PanelMath.PickerRows(new List<Territory>(), 0).Count == 0, "an empty list is an empty picker");
        }

        private static void PanelWidths()
        {
            // The PvP row: three faces, and what is left of a panel's width after the label column, the gaps and the marks column.
            float[] mins = { 70.6f, 51.2f, 49.8f };

            float[] plenty = PanelMath.FitWidths(600f, 130f, mins);
            Check(plenty.All(w => w == 130f), "with room, every button is the width it is meant to be");

            float[] tight = PanelMath.FitWidths(376f, 130f, mins);
            Check(tight.All(w => w == 125f), $"short of room, alike, as wide as the room allows ({string.Join(",", tight)})");

            float[] cramped = PanelMath.FitWidths(193f, 130f, mins);
            Check(cramped.Zip(mins, (w, m) => w >= m).All(ok => ok) && cramped.Sum() <= 193f && cramped.Sum() > 193f - 3f, $"too little for equal buttons: each its face plus a share of the rest ({string.Join(",", cramped)})");
            Check(cramped[0] > cramped[1] && cramped[1] > cramped[2], "(the longer face is the wider button)");

            float[] hopeless = PanelMath.FitWidths(100f, 130f, mins);
            Check(hopeless.SequenceEqual(mins), "when even the faces do not fit, they get their faces: nothing is made smaller than its text");

            Check(PanelMath.FitWidths(100f, 30f, new float[0]).Length == 0, "no buttons, no widths");

            // The invariant, over a sweep: never wider than the room when the faces fit, never narrower than a face.
            var rng = new System.Random(7);
            for (int round = 0; round < 400; round++)
            {
                int n = 1 + rng.Next(8);
                var m = new float[n];
                for (int i = 0; i < n; i++) m[i] = 10f + rng.Next(90);
                float avail = rng.Next(0, 900), preferred = 20f + rng.Next(120);
                float[] w = PanelMath.FitWidths(avail, preferred, m);
                bool fitsFaces = avail >= m.Sum();
                bool ok = w.Length == n && Enumerable.Range(0, n).All(i => w[i] >= m[i]) && (!fitsFaces || w.Sum() <= avail + 0.001f) && w.All(x => x <= Math.Max(preferred, m.Max()) + avail);
                if (!ok)
                {
                    Check(false, $"widths for n={n} avail={avail} preferred={preferred} mins=[{string.Join(",", m)}] -> [{string.Join(",", w)}]");
                    return;
                }
            }
            Check(true, "400 random rows: never narrower than a face, never wider than the room unless the faces alone are");

            // The Colour row: eight chips, the same rule.
            float[] chips = PanelMath.FitWidths(181f, 24f, Enumerable.Repeat(10f, 8).ToArray());
            Check(chips.All(w => w == 22f), $"chips shrink alike to fit ({string.Join(",", chips)})");
            Check(PanelMath.FitWidths(400f, 34f, Enumerable.Repeat(10f, 8).ToArray()).All(w => w == 34f), "and are full size when there is room");
        }

        private static void PanelOldMath()
        {
            // These were in TerritoryPanel.cs before 0.4.0 and untested; the move to their own file changed nothing.
            Check(PanelMath.Where(-5f) == "overlaps" && PanelMath.Where(0.5f) == "touches" && PanelMath.Where(-0.5f) == "touches" && PanelMath.Where(120.4f) == "120 m away", "Where: overlaps, touches, N m away");
            var c = new TerritoryShape(TerritoryShapeKind.Circle, 120.4f, -340.6f, 20f, 20f, 0f);
            Check(PanelMath.PartSummary(c) == c.Describe() + " at (120, -341)", $"PartSummary ({PanelMath.PartSummary(c)})");

            var a = new TerritoryGround(new TerritoryShape(TerritoryShapeKind.Circle, 0f, 0f, 10f, 10f, 0f), new List<TerritoryShape>());
            var b = new TerritoryGround(new TerritoryShape(TerritoryShapeKind.Circle, 50f, 0f, 10f, 10f, 0f), new List<TerritoryShape>());
            Near(PanelMath.Gap(a, b), 30f, "two circles 50 m apart, 10 m radius each: 30 m between them", 1e-3f);
            var touching = new TerritoryGround(new TerritoryShape(TerritoryShapeKind.Circle, 20f, 0f, 10f, 10f, 0f), new List<TerritoryShape>());
            Near(PanelMath.Gap(a, touching), 0f, "touching", 1e-3f);
            var over = new TerritoryGround(new TerritoryShape(TerritoryShapeKind.Circle, 15f, 0f, 10f, 10f, 0f), new List<TerritoryShape>());
            Check(PanelMath.Gap(a, over) < 0f, "overlapping is negative");
            var box = new TerritoryGround(new TerritoryShape(TerritoryShapeKind.Rectangle, 40f, 0f, 10f, 30f, 0f), new List<TerritoryShape>());
            Near(PanelMath.Gap(a, box), 20f, "a circle and a box", 1e-3f);
        }
    }
}
