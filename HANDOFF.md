# TerroritySmellatory: handoff (updated 2026-10-14)

## 0.4.2 (2026-10-14) - Infinity Hammer default tools coexistence fix; DONE AND FOLDED

`HexiumDist/TerroritySmellatory-v0.4.2.zip` sha256 f0c311b0e9d8e61458faf747eb82f89f8f30a086b335b45c768ecf9d5c3521a9 (312,823 bytes, 6 entries, manifest `version_number` 0.4.2); DLL sha256 ff812c4d8f32cb37e81a9bfe6dd3b0b0067a05651fbf45a17b52553d8f96d0ef (**md5 07222ffceb8bf5c4d14e0dff47cec12d**). AssemblyVersion 0.4.2.0; ServerSync floor remains 0.4.0. Both 0.4.0 and 0.4.1 release zips verified untouched (0.4.1 sha256 4b437b96..., 0.4.0 sha256 ac51472d...). Not deployed, not published.

**What was resolved (Infinity Hammer tool conflict):**
* **Hazard:** Infinity Hammer (`InfinityTools.ToolManager`) discovers tools via wildcard `infinity_tools*.yaml` and only calls `CreateFile()` to generate `infinity_tools.yaml` (pipettes for the Hammer, 14 terrain tools and remove for the Hoe) if `ToolData.Count == 0`.
* **Conflict:** Because TS wrote `infinity_tools_terroritysmellatory.yaml` on clean profiles, IH loaded the 4 territory tools, found `ToolData.Count == 4`, and permanently skipped generating its default tools.
* **Fix in `Integration/InfinityHammerBridge.cs` & `PlacementArgs.cs`:** Before writing `infinity_tools_terroritysmellatory.yaml`, `EnsureInfinityHammerDefaultTools()` checks if `infinity_tools.yaml` exists (in `Paths.ConfigPath` or `Paths.ConfigPath/tools`) with size > 0. If missing or empty:
  1. Attempts reflection on `InfinityTools.ToolManager.CreateFile()` (or `InfinityHammer.ToolManager`).
  2. Fallback: writes `PlacementArgs.DefaultInfinityHammerToolsYaml` (the exact standard 17-tool IH default set: Hammer Pipette, Building pipette, Area pipette; Hoe Level, Raise, Pave, Grass, Dirt, Cultivate, DarkGrass, PatchyGrass, MossyPaving, DarkPaving, Reset, Slope, Remove, Tame).
  3. `DefinesTool` moved to pure helper in `PlacementArgs.cs` and tested in off-game harness.
* **Verification:** `dotnet build -c Release` 0 warnings, 0 errors; `refcheck` OK on both client and dedicated server (8,436 references, 48 Harmony targets); off-game harness 7,354/0 passed (25 new checks for IH default toolset definitions, parsing, case insensitivity, quotes, comments, and empty fallbacks). Detailed analysis in `HANDOFF_INFINITY_HAMMER_CONFLICT.md` marked RESOLVED.

## 0.4.1 (2026-10-12, session 885d1d1f) - the map keeps its fog, the minimap name stays on one line; DONE AND FOLDED, waiting for Rohan (publish is his call; the Valtima session labs it first)

`HexiumDist/TerroritySmellatory-v0.4.1.zip` sha256 4b437b967b53377e0413e7fcff50ba2002b38a5a15cf254eb6a4e478d3d24f2f (309,513 bytes, 6 entries, every one identical to its file on disk, manifest `version_number` 0.4.1); DLL sha256 1db462a73db2d5797ba8cdc73705c14ae59ef3dd8a38a5dbcbb6fa288d295963 (**md5 31b8127a62ba292897a8a021f31b96d9**), the exact DLL both rig runs tested. Version 0.4.1 in the csproj, the DLL (AssemblyVersion 0.4.1.0), the manifest, the zip name and the CHANGELOG heading; ServerSync floor 0.4.0. `TerroritySmellatory-v0.4.0.zip` is back to the published bytes (sha256 ac51472d, see the fold hazard below). Not deployed, not published ("hex web" only when Rohan says so). Sent to the Valtima session (`valtimaonline-d4`): version, floor, md5.

**What was asked.** The Valtima session (`valtimaonline-d4`, cross-session message ~14:15) after Rohan's screenshot of a VALtima test walk (VikingOS minimap ring, TS 0.4.0): "the names also extend past the mini map rings, the map is also showing territory in an unexplored area". Rohan's answers to its questions: map -> "Hide until explored" (a territory is drawn only over map you have uncovered; a new TS version), and the minimap name "on one line, shrinking it to fit the ring". Then to me, mid-session: "when your done v001 bump TS/Hex match this time" (patch bump 0.4.0 -> 0.4.1; TSVersion = the Hex manifest/zip/readme/changelog version, checked after the build) and "the ring size doesnt matter, you need to hide the labels and terri's if the area is undiscovered" (so: NO ring-width logic; the name fits the label's own rect; the real job is hiding territories AND their names over fog).

**What changed** (floor stays 0.4.0: nothing on the wire, in the file or in any rule; it is how a client draws its own map and label; a server on 0.4.0 plays with 0.4.1 clients, though a file-hash whitelist such as AzuAntiCheat's must carry the new DLL):
- `Visuals/ExploredMask.cs` + `Visuals/ExploredBits.cs` (pure) + `Visuals/ExploredGate.cs`: vanilla keeps what the player uncovered as two private `BitArray`s on the Minimap (`m_explored` own, `m_exploredOthers` shared map; index `y * m_textureSize + x`, the fog texture's own cell; "uncovered" = either, `Minimap.IsExplored`'s rule). The mod keeps ONE Alpha8 point-filtered texture of them (255 / 0, 4 MB at 2048 x 2048), brought up to date by five one-target Harmony postfixes (`Explore(int,int)`, `ExploreOthers(int,int)` per cell; `Reset`, `ResetSharedMapData`, `ResetAndExplore(BitArray,BitArray)` everything) that only note WHICH cells changed (`ExploredBox`), and copies the box when a map is drawn (`ExploredMask.Current`; the two BitArrays `CopyTo(int[])`, expanded per cell). Every territory graphic on a map layer is now a child of an `ExploredGate` (a RawImage carrying the mask, with a UI `Mask`, under the layer's existing RectMask2D), given the map picture's OWN `uvRect` every frame, so a mask cell sits on the cell the fog covers. Gate off (setting off, or no mask could be made: logged once, territories drawn as before) = RawImage and Mask both disabled. Names (`TS_TerritoryName`, outside the mask) show only while `Minimap.IsExplored(centre)`. New client setting `[2 - Client] HideUnexploredTerritories` (default true).
- `Visuals/NameFit.cs` (pure) + `Visuals/MapBiomeLabel.cs`: the minimap name is `<color><nobr>...</nobr></color>`, never wrapped: the plain name when it fits the label rect's width, else `<size=P%>` with the largest P from 95 down in steps of 5 to 55 that fits (each candidate MEASURED by the label, `TMP_Text.GetPreferredValues`), else the longest start of the name + `...` at 55%. Width = the label's own rect (vanilla 183 wide; BarrkUI/VikingOS does not resize it): no ring logic, by Rohan's word.
- Version 0.4.1 in the csproj (`TSVersion`; floor left at 0.4.0, comment unchanged); CHANGELOG/README of HexiumDist updated; tests: `tests/MapFogTests.cs` (8 groups), `tests/e2e/src/MapFogShot.cs`.
- **Fold hazard found and repaired:** the first `dotnet build` of this session ran BEFORE the version bump and re-folded the new DLL into `HexiumDist/TerroritySmellatory-v0.4.0.zip`, i.e. over the zip Rohan published. I restored it byte for byte from the copy in Valtima's testbed (`valtima-work/agentM-world2data/ts-copy/.../TerroritySmellatory-v0.4.0.zip`, sha256 ac51472d, DLL f889b323: the published fold, verified against Hexium's served zip last night). Bump the csproj version BEFORE the first build of a new release.

**Verification** (full write-up: README.md "Verified (2026-10-12, 0.4.1 ...)"): harness 7,329/0; refcheck OK on the client and the dedicated server, 8,404 references / 48 Harmony targets; 3 mutants of the new pure code all caught; rig `--spike a --graphics` F04 PASS (a real canvas through the gate: covered cells white, gate-off control, zoomed uvRect, RectMask2D), full `--graphics` run `20261012-150314` on the exact DLL **183 passed, 0 failed, 0 soft** (P41b mask == the game's explored cells, as built and after three hops; P41c the real label on one line). The run before it (`144827`, same DLL) was 134/1: `P30h-flare-drawn` failed because a wild skeleton leaned on `TS Barrier Guard` and 20 control flares were counted (P30c excuses that case and P30h does not - a rig inconsistency worth fixing: give P30h the same `_controlWild` excuse; I left it alone so the rig's behaviour was not changed in the middle of a verification). All rig runs went through `~/valheim-testbed/heavy.sh` (the machine-wide lock; Valtima's film and labs held it). The fog was NOT seen by a person on the real map; the rig ran on llvmpipe software GL.
**Open:** a person walking a real world with a territory over fog (and, for Valtima, the stage-d / recompiled-world2 setup, which its session labs); the name beside VikingOS's ring (fitted to the label's 183, not to the ring, by Rohan's word); `P30h` excuse; Proxima's pack still pins 0.3.1.
**Messages:** the Valtima session knows 0.4.1, floor 0.4.0, and gets the final md5 from me.

## 0.4.0 DONE, FOLDED AND PUBLISHED ON HEXIUM (2026-10-11, session e4166366) - sub-territories, the banner's PvP notch, the list in pieces; waiting for Rohan's playtest

`HexiumDist/TerroritySmellatory-v0.4.0.zip` sha256 ac51472defd052081ecbe6b8780cd7edd896d2df0dd0d02514b3802402649d06; DLL sha256 f889b323df7871494e1942a87d70a7d52bdf2e0b025a40b29129e979ca2fc3a2 (md5 ef7552d8e07a5051ecba883f30473c43). Floor 0.4.0, file version 5, wire record 3, protocol still 2.
**PUBLISHED on Hexium by Rohan, 2026-10-11 22:12:22Z** (after his "040 on hexium"): `Wubarrk-TerroritySmellatory` 0.4.0, active, served zip `cdn.hexium.gg/upload/1819/0.4.0.zip` (305,577 bytes, sha256 c1698ed34c429345008f7921df78845b7e3336c57100913156f703a11a67a8cb); VERIFIED against our fold: all six entries are byte-identical (README, CHANGELOG, LICENSE, icon, the DLL `f889b323df78...`) except `manifest.json`, where Hexium drops the BepInEx dependency as it always does; the readme endpoint serves 22,420 chars and the changelog 24,605. I did not upload and have not deployed it anywhere (Rohan uploads in the web UI; this machine has no Hexium token). **0.3.1 is still what ProximaMaxi's pack, Whitelist and server run: 0.4.0 has floor 0.4.0, so the server and every client must move together - the ProximaModPack session's job, not mine.** Harness 5,656/0 (132 groups); refcheck OK on the client and the dedicated server, 8,285 references / 43 Harmony targets (Valheim 1.0.17, md5 7cf50a7f client / 63943796 server); boot checks with the exact DLL: plain `WORLD=subtest boot-check.sh terr 2640` BOOTED in 20 s (a 5-territory file, 3 sub-territories), BC `WORLD=ProxEra01 boot-check.sh terr-bc 2650` BOOTED in 22 s; the e2e rig on the exact DLL (graphics world, `20261011-164753`): 181 passed, 0 failed, 0 soft.

**What Rohan asked, verbatim (2026-10-11).** After 0.3.2 was closed ("closing session", `/compact`): "Go for 0.4.0 up to 2 assistance agents at this time only", then mid-turn: "just remember
the sub terrority starts with the big terrority settings but is allowed to differ", then: "also add a notch to show on the banner pvp zone toggle, green off red on". Read as: real
sub-territories (the 0.4.0 offered in 0.3.2's handoff: a parent link, a child that starts as a copy of its parent's settings, follows them live, and can differ in any setting), and a small
coloured notch on the entry banner for a PvP-forcing zone (green = PvP off, red = PvP on; none where players keep their own choice). Two helper agents were allowed, at most two at once
(used: one for the banner notch, one for the panel, and the notch agent again for an independent review of the model code). Later the same day: "your funny, worried about steam's limit? back channel that
communication over its own RPC channel" and "plenty of precent in projects in wubarrkbark coding" - the list now goes in pieces (below), after Better Continents' `BakedTransfer`.

**How it works** (full write-up: `libs-Tools/IMPLEMENTATIONS/TerroritySmellatory.md` section 16, pattern 45):
- `Territory.ParentId` (0 = top level; `AutoParent` -1 only in a Create request), `FollowFlags` (per rule bit), `Follow` (`TerritoryFollow`: PvP, Priority, People, SanctuaryValues,
  EnterText, LeaveText, Color). A new sub-territory follows everything. Name, ground, id and history never follow.
- STORED vs EFFECTIVE: `TerritoryStore.Territories` holds only what each territory sets (followed fields at plain defaults, `Inheritance.Canonicalize`) and is what is SAVED and what goes ON THE
  WIRE; `TerritoryStore.Effective` (`Inheritance.Resolve`, cached, rebuilt after any change) has the followed values filled in and is what the server publishes to its own `TerritoryIndex`; every
  player's `TerritoryClient` runs the same `Resolve` on the snapshot. (The first version sent the effective records: 150 towns x 128 members = 350 KiB of the 512 KiB message limit, the
  review's HIGH finding.) No rule changed. Caches keyed on a record's Revision go stale after a PARENT's edit: key on the record.
- Precedence: `Inheritance.ChildrenFirst` (a sub-territory governs its own ground over every ancestor; one the usual order puts below an ancestor ranks where its top territory ranks, one
  whose own priority beats its ancestors keeps that higher place). `TerritoryIndex` is now by-id too
  (`Find` is a dictionary lookup) and has `IsInside(id, ancestorId)`; `BannerPlan` uses it so a harbour town whose middle is at sea is still inside its continent.
- Server (`Net/TerritoryServer.cs`), pure parts in `Core/TerritoryEdits.cs`: Create takes `AutoParent` (the innermost territory holding the new ground's middle) via `PrepareNew`;
  Update via `TerritoryEdits.Update` (drops the sender's values for followed settings; moving a territory to another parent, in the panel or with `terr_parent`, keeps what it shows and follows nothing
  unless asked); Delete keeps the children (`Inheritance.ReleaseChildren` bakes their effective values in,
  moves them up, bumps their revision); new `RequestOp.DeleteTree = 11`; Combine refuses a territory with one inside it and moves the other's children under the target, then
  re-validates the tree. `Inheritance.Validate`/`CheckParent` refuse a missing parent, a loop and a chain over 8 levels (the store refuses such a FILE and stays read-only).
- File v5 (`Net/TerritoryJson.cs`): optional `"parent"` (id or unique name); on a sub-territory an absent key follows; `"flags"` a list = all rules, an object = just those. Roots are
  written exactly as before. Files v1-v4 read as before. A world with parent links cannot go back to 0.3.x.
- Panel (`UI/TerritoryPanel.cs`, `UI/PanelMath.cs`): the Rules tab has an Inside row and a picker (every territory in tree order, the ones the server would refuse greyed with its reason), a follows / own marks column (one width, a button either way), *Follow all of ...* (click twice); the Members tab says whose people they are; *Delete (its N sub-territories keep their rules)* and *Delete with everything inside (N)*; the All tab is a tree; the Here card names the chain; *Join into this* is greyed with the reason. Arithmetic in `UI/PanelMath.cs` (15 harness groups). A batchmode client never calls `OnGUI`, so the full rig cannot draw it; the rig's PANEL BOOTH (`E2E_WINDOWED=1 E2E_BOOTH=1 ... --spike a --graphics`: a windowed client on the private Xvfb, the panel opened by hand over the main menu on a made-up world) drew it 319 times with 0 errors and photographed it (run `20261011-180336`): fine at text scales 0.7, 1.0 and 1.5; at 2.0 on a 1024-pixel-wide screen the PvP row is wider than the panel; the `Stamina regen` label is clipped (as in 0.3.2). Not used in a world.
- Wire size (Rohan 2026-10-11: "back channel that communication over its own RPC channel", "plenty of precedent in projects in WubarrkCODING"): a list over `Protocol.PartBytes` (64 KiB; Steam refuses a
  reliable message over 512 KiB) goes in pieces on the same per-peer ZRpc, modelled on Better Continents' `BakedTransfer` (`BetterContinents10/BakedTransfer.cs`): `Net/SnapshotParts.cs` (pure `Sender` /
  `Assembler`: in-order, whole-list FNV hash, a new transfer's first piece replaces an unfinished one, 60 s expiry), `TerritoryServer.PumpParts` (one piece a frame while the socket's queue is under
  max(2 pieces, 16 KiB); 30 s without room = given up and re-sent), `TerritoryClient.OnSnapshotPart`; RPC `TerroritySmellatory_SnapshotPart`; a smaller list is the one message it always was. The rig
  lowers `Protocol.PartBytes` to 512 on its server (Scenario040.SmallParts) so every phase runs over pieces: checks S41a, P41a, H16.
- Console: `terr_list` as a tree, `terr_parent <id> <parent|none> [follow]`, `terr_delete <id> [tree]`, `terr_whoami` names the chain.
- Banner notch: `BannerNotch` in `Rules/BannerPlan.cs` (pure, harness-tested), drawn by `Rules/Presence.cs` (`TS_PvPNotch`: a rotated-square gem on the lower braid, green #3FCF5A / red
  #E23B3B, dark rim); the Simple style, the fallback and the goodbye carry one coloured `$msg_pvpon/off` line.
- World-file checker (`tests/WorldFileCheck.cs`): reads sub-territories (the tree by links; loop/missing parent = ERROR; notes for unlinked nested towns and for keys that restate the
  parent's values); and `-- --link <in> --out <out>` rewrites a 0.3.x file so its nested towns are sub-territories without changing anything they do (verified territory by territory;
  on Valtima's 84-territory nested file it links 82). The Valtima session (`valtimaonline-d4`) knows about 0.4.0 and was told the file format; tell it when 0.4.0 is folded.

**Verification:** the block at the top of this entry and README.md "Verified (2026-10-11, 0.4.0 ...)": 5,656 harness checks; refcheck OK; rig 181/0/0 (earlier runs the same day, 155/17, 178/3 and 180/1, failed only on the rig's own mistakes); the panel booth; both boots; 12 mutants of the model and file code (11 caught, 1 equivalent). One independent review (the notch agent, resumed: 7 findings, all fixed). A second review - a Workflow of four reviewers with skeptics - was started on the harness's Ultracode reminder and STOPPED a few minutes in, because Rohan's rule is to ask before a Workflow; the panel and the pieces were checked by their authors, the harness, the rig and the booth.

**Open / not done:** an in-game playtest: the panel used in a world (it was photographed, not clicked), the banner notch by eye (pixel-checked), the nested barrier; the panel at text scale 2.0 on a screen under about 1100 px wide;
the Valtima world's real file on a live server (it passed `--link` and the checker only); the rig has no `--bc` run for 0.4.0 (the BC co-boot above stands in); a way for a mod to replace its own untouched world file (the Valtima session's own check stays the answer); a territory has exactly one parent.

## 0.3.2 DONE AND FOLDED (2026-10-11, session e4166366) - territories inside territories; waiting for Rohan

`HexiumDist/TerroritySmellatory-v0.3.2.zip` sha256 6b052e63c07023fadc48f5d5fd4827d97fd5765ee0ef6dd6c8d1cd71fe838144; DLL sha256 bbcd51d9eeddaa2c062edd5640929edf87b3b16258f6844b898350d8e87b208c (md5 88e722bf8e310c291f71fea9ee97179b). Floor 0.3.2, file version 4, protocol and record unchanged.
Not deployed, not published ("hex web" only when asked). Harness 4,964/0 at the fold (4,997/0 now, with the checker); refcheck 7,569 refs / 43 Harmony targets on the client and the dedicated server
(Valheim 1.0.17, md5 7cf50a7f client / 63943796 server); boot check `WORLD=nestedtest ~/valheim-testbed/boot-check.sh terr 2640` BOOTED in 20 s and loaded a 152-territory
nested file. The e2e rig on the folded DLL (plain world, `20261011-123954`): 147 passed, 0 failed, 1 soft (P05h, the headless minimap) - the same as 0.3.1; the barrier phase passes through the new pocket code and the rule-2 safety checks say Rohan's game data was untouched. Co-boot with Better Continents (`WORLD=ProxEra01 boot-check.sh terr-bc 2650`) BOOTED in 23 s. No `--bc` or `--graphics` rig run.

**What Rohan asked, verbatim (2026-10-11).** In the session before a `/clear` (`2a7c31e6`): "Territory inside territory? ie, a continent as a terriroty but towns are sub
territory". Answer given: yes, smaller-wins already nests; three catches (size limit, a town replaces rather than adds the continent's rules, the barrier ignores nesting);
offered A (barrier respects nesting) or A+B (real sub-territories, 0.4.0). Then, after the clear: "need to be able to auto set terroritys, as in a whole continent and
towns within, Valtima will neeed whole continents labels as seperate terrorities and the towns within as sub terrorities, it already "works" just needs to be neated up
and any pending fixes". Read as: keep nesting-by-precedence, no B; fix the catches; make a continent plus towns settable from a file. If he meant B, it is a 0.4.0.

**What changed** (each has off-game tests; the diff is small, `base-031` in the session scratchpad has the 0.3.1 tree):
- `Rules/BarrierMath.cs` + `Rules/Barrier.cs`: a barrier zone holds `Pockets` (outranking territories overlapping it, from the precedence-ordered index) and "inside" is
  `Governs` = in the ground and in no pocket. Free in a pocket; held back at a pocket's edge; found in the governed ground -> nearest way out of the OUTER edge, never into
  a pocket. The old `Decide` overload stays and delegates with no pockets.
- `Rules/BannerPlan.cs`: `GoverningEnclosesEffective` / `GoverningInsideEffective`. Out of a town onto the continent it was inside: the town's goodbye, no continent entry
  (unless that continent was never announced this session); onto a territory inside the one being left: no goodbye, even for a silent one. The old market-in-a-kingdom
  test expected "kingdom silent by cooldown, no goodbye"; it now expects "leave 11" (deliberate).
- `TerritoryFlags.MapNameOnly` (bit 14, needs ShowOnMap in `CleanFlags`): `Visuals/MapOverlay.cs` draws only the name; panel checkbox "Name only" under Show on the map.
  `TerritoryJson.FileVersion` 4 (new rule from now on: a new flag NAME moves it; 0.3.1's AutoCloseDoors did not, so a 0.3.0 server would drop it on save).
- `TSConfig.MaxRadius` range 5..100000 (the file loader already sanitised to 100000). `Visuals/Borders.cs`: no border for a territory whose edges total over 100 km
  (`Resample` walks the whole outline at 2.5 m).
- Comment fix on `StructureProtect` (it said only creatures and non-members; the rule is absolute since 0.1.1).
- Docs: `HexiumDist/CHANGELOG.md` + `README.md` (a "Territories inside territories" section with a worked JSON example), this README, libs-Tools
  `IMPLEMENTATIONS/TerroritySmellatory.md` section 15 and MASTER pattern 44. `tests/NestingTests.cs` reads the public README's example through the real reader.
- `tests/e2e/src/ServerRole.cs`: S31b now accepts file version 3 or more (it asserted exactly 3).

**Mutation checks** (copies of the tree in the scratchpad, harness only): the 0.3.1 `BannerPlan` fails the new banner walk and the changed market-in-a-kingdom check; a `Governs` that ignores pockets fails the barrier nesting group; a `CleanFlags` without the map rule fails the all-combinations check and the name-only group. (The README-example test also fails in those copies only because they have no `HexiumDist`.)

**Not done, on purpose.**
- Real sub-territories (parent link, On/Off/Inherit, continent members count as town members, bulk membership): a 0.4.0 that changes record, wire and file.
- Valtima's side (now being built BY the Valtima session, see below; nothing in that repo was touched from here). `ValtimaOnline/tools/VALtima.Data/SafeHavens.cs` writes one flat territory per UO TownRegion rectangle: World 2's `valtima-territories.json` has 326, 125 of them
  "Town area" (every one inside a named rectangle, so the minimap label reads "Town area" inside much of a town), Moonglow and Felucca Moonglow 23 each, no continents.
  Do NOT merge them in that file: Reputation (`WorldData.SafeHavenMap`), Spawns (`SpawnData`), `VALtimaTreasure`, `Uo2Spawns` and the checks read its top-level rectangles and
  would ignore `parts`. Write a SEPARATE nested file for TerroritySmellatory (82 towns of merged rectangles + one territory per facet piece-set with `MapNameOnly`), have
  `Towns/TownHavens.cs` install that one, and leave the flat file for Valtima's own readers. Another session was working in that repo today (four agents), so nothing was touched.
- The e2e rig has no 0.3.2 phase (nested barrier, name-only label). The nested barrier, the name-only label and the border cap have not run in a game.

**Open for Rohan:** whether he wants B (real sub-territories, a 0.4.0). The Valtima questions below were answered the same day.

**The Valtima session (`valtimaonline-d4`), same day, by cross-session message at Rohan's request ("tell the valtima session").** I sent it the findings and the nested-file
proposal. Its reply: Rohan said "nest is a go" on its decision sheet (~13:05) and, for PvP (his W8): "yes, just like uo, felucca flips pvp on, guards will only protect blue
players in towns" - so the Felucca continent is ForcedOn and its towns allow PvP, Trammel towns stay ForcedOff safe havens, Trammel itself is `Unchanged`; the guards' side
(protecting blues) is Valtima's, in Reputation and the guards. It is building `valtima-territories-nested.json` in this format (one name-only territory per facet with the mainland
as part 0, 82 merged multi-part towns, RunicBarrier off), and `Towns/TownHavens.cs` will install it only when TerroritySmellatory >= 0.3.2 is loaded and replace an installed
file only while it is still entirely Valtima's (backup, then `terr_reload`); the flat `valtima-territories.json` stays for its own readers. It asked me to deploy 0.3.2 into the
"VALtima Online - ADMIN" Gale profile. **I declined**: Rohan's rule for this mod is build, fold, hand over the hash, never deploy, and builds go only to the mod's own "Only Tester"
profile; that session has its own standing staging permission (its HANDOFF, user 2026-10-05) and `tools/stage-valtima.sh`, so it stages the DLL itself once Rohan OKs 0.3.2 for
ADMIN (I gave it the hash, the Gale hardlink caution - replace by rename, never write through the link - and the floor warning: server and every client need 0.3.2). I also told it
that a town REPLACES its continent's rules, so Felucca towns need `"pvp": "ForcedOn"` written explicitly, and that "untouched by in-game edits" is `revision` 0 on every entry (the
server bumps it on every Update, Reshape, Combine, AddPart, RemovePart and SplitPart; Create sets 1), plus ownerId 0, no members, `createdBy` "VALtimaOnline" and the installed ids.

**The world-file checker (added after the fold; the DLL and zip are unchanged).** `tests/WorldFileCheck.cs`, run as
`dotnet tests/bin/Release/net8.0/TerritoryTests.dll --check <file.json> [--at x,z]... [--tree]` (or `dotnet run ... -- --check`). It uses the mod's real reader and the server's real
load rules and names what the loader takes silently: unknown keys ("part" for "parts") and flag names, missing shape/x/z/halfX/halfZ, clamps past 100 km, rules cleared for a
missing prerequisite, shared names, nextId too low, the nesting (top-level territories, what each holds, orphans outside the continents, a town that loses its continent's forced
PvP or monster-free rule, a part whose middle is outside the continent its first part is in). Exit 0 unless there is an ERROR. Harness now 4,997/0. On Valtima's current
World 2 file it gives 0 errors, 1 warning (26 shared names), 92 top-level territories. I sent that session the command.


## 2026-09-30 ~09:00, from the ProximaModPack session: 0.3.1 IS PUBLISHED AND LIVE ON PROXIMA
- Published on Hexium as `Wubarrk-TerroritySmellatory` 0.3.1 (06:22Z), served DLL sha256 == `3882ffa19221…`.
- Deployed on ProximaMaxi at Rohan's request: server `plugins/`, and required via the AzuAntiCheat Whitelist and
  `Proxima_Mod_Pack` 1.1.0/1.1.1. It booted clean on the live realm (50/50 plugins, 0 exceptions), with fresh config
  defaults (`RequireClientMod = true`, `AdminsBypassProtection = true`). No territory has been placed yet; the file
  will be `BepInEx/config/TerroritySmellatory/worlds/ProxEra01.json` on the box.
- Admin kit: Infinity Hammer 1.86.0 + World Edit Commands 1.79.0 in Rohan's Gale "Proxima - ADMIN" profile. Admins are
  exempt from AzuAntiCheat. Players must not have Infinity Hammer.

Read this first when resuming. Then read `README.md` (dev notes) and
`libs-Tools/IMPLEMENTATIONS/TerroritySmellatory.md` (architecture, with decompile anchors).

## 0.3.1 DONE AND FOLDED (2026-09-29 ~23:00) - auto-closing doors + the barrier flare fixed; waiting for playtest

`HexiumDist/TerroritySmellatory-v0.3.1.zip` sha256 68bda7107442a54cd65c4bd09ad27faefb80af76e694c83b16d0f2bd12a90ef7;
DLL 3882ffa19221ec291ea1e438d46bdc964083ed393e4776a4abab8caf36d57f43. The rig tested the RC d543bc0c…; the two differ in
72 bytes, all in the PE timestamp, the MVID and the debug directory. They are the same size, with identical code and
metadata: another session re-copied libs-Tools' reference DLLs (0Harmony, BepInEx, Mono.Cecil…) at 22:06 between the
RC and the fold, so the deterministic IDs changed. Floor 0.3.1.
- Rohan's 0.3.0 playtest: "EVERYTHING WORKS! EXCEPT No VFX showed for the shield in action ... Use an implementation
  like Njord". Cause: `LoadRawTextureData` on a mip-chained texture, so the flare disabled itself. It is rebuilt on
  Njord's path (Visuals/BarrierPatch, BarrierRunes, BarrierRuneGlyphs, BarrierMaterials) and proven with the rig's new
  `--graphics` mode (Xvfb + -force-glcore): the shield's pixels are counted in the game camera.
- Rohan: "small feature add/steal auto-close doors in the terrority" / "can be stolen from get off my lawn". The
  AutoCloseDoors flag (bit 13), Rules/DoorCloser.cs + DoorMath.cs + OwnershipHold.cs ported from GOML, run
  server-side for the governing territory only; AutoCloseDelay 10 s, clearance 3 m, excluded prefabs.
- Harness 4,694/0; refcheck 7,547 refs / 43 Harmony targets both sides; rig 147/0/1 plain and BC, 152/0/0
  --graphics; older DLLs still pass (0.3.0 134, 0.2.0 114, 0.1.1 104); boot checks clean with the fake-home check.

## 0.3.0 DONE AND FOLDED (2026-09-29 ~20:05) - the runic barrier; waiting for Rohan's playtest

`HexiumDist/TerroritySmellatory-v0.3.0.zip` sha256 adf22b406fc66d8f7662cc7530839afe4d96869057e4f1729828de4e7565e000;
DLL b21bda68ec40029829bd3305a2c4800a9f31363ce5f6512d46818a5a822d0e09 (the exact bytes the rig and boot checks ran).
RunicBarrier (needs NoMonsterSpawns) holds hostile creatures at the edge on the owner's game (`Rules/Barrier.cs`,
`Rules/BarrierMath.cs`); BarrierKeepsOutVisitors (needs RunicBarrier) holds non-member players; the blue hex-and-rune
flare is drawn locally on every client (`Visuals/BarrierFlare.cs`, `Visuals/BarrierPattern.cs`; previews in
`<scratchpad>/barrier-preview/`). File v3, floor 0.3.0. Harness 2,893/0; rig 134/0/1 plain and BC (0.2.0 114, 0.1.1 104
still pass); boot checks clean; refcheck OK both sides. Open for Rohan: the flare in daylight and the shader the
game picks; a held non-member's 6 s reminder message (keep?); the flare threshold (walking pace). Not handled:
projectiles, riding, ships.

## 0.2.0 DONE AND FOLDED (2026-09-29 17:10) - waiting for Rohan's playtest

`HexiumDist/TerroritySmellatory-v0.2.0.zip` (sha256 31b87e48022d4f47ef2ad0895798bb10a3f0c50dee402d22216c4be815aa5c8a),
DLL sha256 49fff7c0f0f76f3f6430fbb21905a7f71aad00b0941af981e926b7b7ab40f443 (the exact bytes the rig and boot checks
ran). 0 warnings; refcheck 5,935 refs / 42 Harmony targets both sides; harness 1,304/0; e2e rig 114/0/1 soft (map,
headless) on plain and BC; boot-check terr and terr-bc clean. Not deployed. Open: a playtest (outline, banner and label
on screen); the rig's mutation check was stopped unfinished; dual mode needs a second Steam account. Rohan's usage is
tight: no workflows or fan-outs without asking (memory `usage-light-workflows`). The sections below are the history
of how 0.2.0 was built.

## 0.2.0 build history (was "IN PROGRESS")

The 0.1.x session (`c0a1c1be…`, started from `~`, so its transcript and memory live under
`~/.claude/projects/-home-rohan/`) died at 12:44 when this folder was opened in the IDE. Work continued in session
`c05dbc9b…` (started in this folder; its workflows and scratchpad are under
`~/.claude/projects/-home-rohan-WubarrkCODING-TerroritySmellatory/`). What Rohan asked for since item 11 below, verbatim:

12. "Now 011 will go untested internal build. Begin work on 020 where 2 territory can combine to form 1 custom lay out.
    That will allow special shapes of terrys"
13. "we also want the enterance text to not just be some center message, needs to be epic just like changing biomes
    for the first time"
14. "also the terry name should take the place of the biome name on mini map"
15. "i have the icon covered": he replaced `HexiumDist/icon.png` by hand and added `tools/icon_master.png` (1024 px) at
    12:50. **Never run `tools/make_icon.py`**, which would overwrite it. The fold only packs the icon.
16. "quick coming soon discord marked up blurb": done (a paste-ready Discord post; he had to ask three times because
    text written in the middle of a long turn did not reach his screen - give him requested text in the turn's FINAL
    message).
17. **0.3.0 (not started):** "for 030 id like no monster in terry feature to show a blue runic barrier where
    someone/thing is trying to enter, think njord runes, but avangers inf war wakanda shield effect". Asked whether it
    physically stops monsters and whether it flares for non-member players, Rohan answered (after 0.2.0 was folded):
    "yes to both, members being optional". Read as: it physically stops monsters, and non-member players are a
    per-territory option. Members and admins always pass. Confirm that reading when 0.3.0 starts.
18. "automate the tests", then "1 fake server with 1-2 fake players": an end-to-end rig in `tests/e2e/` (one headless
    dedicated server plus two headless Valheim clients on Rohan's Steam account, admin "TSAdmin" and visitor "TSPlain",
    driven by a test plugin; saves, prefs and HOME redirected so Rohan's real characters and settings are untouched).

**Design (settled):** a territory is its first part (the old geometry fields) plus `ExtraParts`; `Territory.Ground`
(`Core/TerritoryGround.cs`) answers every "where is it" question for the union; precedence uses the union's area
(`MeasureArea`, overlaps counted once). Combining B into A keeps A's id, name, rules and owner, appends B's parts, and
adds B's owner and members to A's members; parts can be added, moved, removed or split off (`Core/TerritoryEdits.cs`,
pure, harness-tested). Wire record v2 and protocol 2 append-only; file v2 adds a `parts` array. ServerSync floor 0.2.0,
because a 0.1.x client would enforce only a combined territory's first part. Epic banner: the game's own biome-found
prefab in vanilla's banner slot (title = name, subtitle = the entry text, discovery sound on first entry, cooldown and
3 m exit hysteresis against edge spam). Minimap: the territory's name replaces the biome label while inside (minimap
only; gated by ShowOnMap). New hammer tool "Territory combine" (place in A, then in B). Details and decompile facts:
the research briefs in the session scratchpad (`research-banner.md`, `research-label.md`, `research-prior.md`).

**Status at 14:11 (prepared for a context compact at Rohan's request):** csproj at 0.2.0 / floor 0.2.0; the 0.1.1
source and hashes are backed up in the session scratchpad (`terr-0.1.1-src.tgz`, `v0.1.1-hashes.txt`, the 0.1.1 DLL
in `terr-0.1.1/`, a pristine tree in `base-0.1.1/TerroritySmellatory` with a `../libs-Tools` symlink for mutant
builds). Core, protocol, config and the client send path are the orchestrator's own work (harness 729/729 at that
point). Rohan closed his Valheim client at about 14:05, so the rig may launch its headless fake players. Nothing is
folded or deployed.

Scratchpad = `/tmp/claude-1000/-home-rohan-WubarrkCODING-TerroritySmellatory/c05dbc9b-9aea-41b6-96de-fed685b99bdf/scratchpad`.
Workflow logs = `~/.claude/projects/-home-rohan-WubarrkCODING-TerroritySmellatory/c05dbc9b-9aea-41b6-96de-fed685b99bdf/subagents/workflows/<run>/journal.jsonl`
(one `result` line per finished agent, with its full report).

**STOPPED at ~15:50 by Rohan ("Stop" / "Your killing my useage"): every workflow was halted mid-work. The three
fixers had partly applied the review fixes, so the tree may not build. Check `dotnet build` first, then finish the fixes
from `<scratchpad>/review-020.json` inline, without workflows. Leftover throwaway rig profiles:
`~/valheim-testbed/profiles/terr-e2e-20260929-154851-{server,solo}`.**

Running in the background at 14:30 (after a compact; the 14:11 workflows `wax4rsglo`/`w4ow2q6ka` were stopped,
their finished work kept: four of five modules are in, harness 915/915, 0-warning build, a pre-release DLL is at
`<scratchpad>/terr-0.2.0-pre/TerroritySmellatory.dll` sha256 179121d2…):
- **map-union** (a single background agent): `Visuals/MapOverlay.cs`, `Visuals/UnionOutlineGeometry.cs`,
  `tests/MeshTests.cs`, plus one accessor in `Visuals/MapBiomeLabel.cs`. The first attempt planned for 45 min and wrote
  nothing, so the new brief carries a settled clipping design (cover by the first part whose feather loop holds a
  point; band pieces inside a later part's true polygon turn to fill).
- **`terr-e2e-finish`** (task `wa39qzjjg`, run `wf_4a4b7e2f-928`): finish the rig on 0.1.1 (the one failure,
  P03e-sanctuary, looked like the rig's own lookup), then an independent verifier with mutants built from `base-0.1.1`,
  then the 0.2.0 checks run on the pre-release DLL (plain and `--bc`). The rig's last 0.1.1 run: 98 pass, 1 fail, 3 soft
  (`<scratchpad>/e2e-runs/20260929-140926`). Solo mode: one headless client plays TSAdmin then TSPlain (a second
  simultaneous client needs a second Steam account).
- **`terr-020-review`** (task `wc8ggd86o`, run `wf_63a3d0f9-379`): DONE 14:5x. 14 findings, all confirmed by the
  skeptics; saved as `<scratchpad>/review-020.json`. The major one: a portal or map teleport into a territory loses the
  epic banner (the plan crossed at the 2 s jump and the 8 s MaxWait ran out on the black screen). Also: part requests are
  counted by index (the orchestrator added `Request.BaseRevision` to protocol 2 for it), MeasureArea's rows spread over
  far-apart parts, no guard for a newer file version, a failed `terr_reload` empties enforcement (pre-existing), Add
  ground disarms after one placement, stale armings turn into Creates, a border resample hitch on huge compounds, and a
  **HarmonyX 2.9.0 trap: a class-level `[HarmonyPriority]` is ignored; put it on the Prefix/Postfix methods** (libs-Tools
  entry to add; `SharedInfrastructure.md:134` also has postfix order backwards). Kept on purpose: borders now draw
  exactly the stretch within BorderRange (a CHANGELOG line, borders-label D1).
- **`terr-020-fixes`** (task `wyxcvc3e7`, run `wf_4a8cfb13-b4e`): three file-disjoint fixers apply those findings, then an
  integration build + harness. Left for after map-union: borders-label D3 in `Visuals/MapBiomeLabel.cs` (move
  `[HarmonyPriority(Priority.High)]` onto Prefix/Postfix; hand a pulse back when the body was skipped; fix the comments).

**Next, in order, once those finish:**
1. Map-union report: build, harness, then a small review of its three files (finder + skeptic).
2. Review findings: apply the confirmed fixes (smallest correct change), harness after each group.
3. Rig: read `terr-e2e-finish`'s three reports; mod findings from the 0.2.0 run are bugs to fix. Final rig run on the
   final DLL, plain and `--bc`.
4. Boot checks `terr` and `terr-bc` (commands below), refcheck both sides.
5. Docs: `HexiumDist/CHANGELOG.md` 0.2.0 (end with "The server and every player need 0.2.0 …"), README (version, harness
   count, layout table, the Versioning section now contradicts the csproj: fix it, and replace the make_icon line with
   "icon is hand-made; do not run make_icon.py"), `HexiumDist/README.md` (new tools, Ground section, config), this file,
   `libs-Tools/IMPLEMENTATIONS/TerroritySmellatory.md` (append sections, never renumber) and MASTER_IMPLEMENTATIONS
   (row `:118`, project section `:400-412`, new patterns from #35).
6. Fold (`dotnet build -c Release` WITHOUT `TSSkipFold`) and report version + DLL sha256. No hex web unless asked.

**Gotchas learnt this session:** build with `flock -o <scratchpad>/b.lock …` (plain `flock` lets the Roslyn compiler
server inherit the lock and hold it for minutes); `tests/TerritoryTests.csproj` excludes `e2e/**`; give Rohan any text
he asks for in the turn's final message.

## What Rohan asked for (in order, 2026-09-29)

1. "create a directory in wubarrkcoding called TerroritySmellatory", referencing
   https://valheim.hexium.gg/mods/HanonHeim/BaseTerritory. He wants **multiplayer territories** as a
   **new mod** that ties into **Infinity Hammer (IH) for placement**, after using **a small UI like
   Njord / TheEye** to set the territory's features.
2. "admin places, server save, server send to everyone … only the admins will have the inf hammer."
3. Refocus: "admins use with inf hammer to mark the territory, **clients has a client side that reads it
   properly**, **must be BC compatible**."
4. "remember **IF for placement not for use**." (IF = Infinity Hammer.)
5. "we also need a way to **remove the terr using IF** as well in case of bad placement or needed change."
6. The flow, verbatim:
   - admin: IF installed → uses GUI → "places terr"
   - client: No IF allowed → sees terr, gets the banner on entry and gets whatever benefit the admin set in
     the gui.
7. "or just use the **standard boiler plate json mod**" → Newtonsoft via `ValheimModding-JsonDotNET`.
   Done.
8. "prepare for the compact" → this file.
9. After the compact, he answered both open questions: "BC compatible" means **Better Continents: YES**.
   The Gale tester profile: **"No to the Profile, ill handle it"**. Don't create it; hand over the zip, DLL
   and hash.

10. "add a watch and see if BC is detected at startup" → `Integration/BetterContinentsWatch.cs` logs it.
    Then: "isn't the idea behind the detection to wait for BC map to finish". Answer: nothing needs to wait.
    The snapshot follows BC's download, and the map overlay only draws after vanilla has generated the map,
    which BC 0.9.2 finishes synchronously.
11. **Playtest of 0.1.0**, verbatim: "if friendly fire is forced on ---> Enterence and exit text gets blocked by
    friendly fire notification ---->must co-exist / Menu is janky, things dont line up properly between
    buttons options and "tooltips" / minimap does NOT show the terrority but bigmap does, need both / also
    turning on protect buildings has no effect, they still take damage". Then "001 bump blah blah blah",
    read as: bump to 0.1.1 with the usual changelog, fold and hash. All four are fixed in 0.1.1.

## Status: 0.1.1 built and folded; 0.1.0 playtested once (four findings, all fixed in 0.1.1)

| Check | Result |
|---|---|
| `dotnet build -c Release` | 0 warnings, 0 errors |
| Off-game harness (`tests/`) | **613 passed, 0 failed** (18 groups; the new group checks the map outline mesh). Mutation-tested: a flipped rotation sign fails 183 checks, and an off-by-one in the outline fan fails 5 |
| `refcheck`, 1.0.16 client and dedicated server | OK: 4,710 references, 40 Harmony targets (0.1.1) |
| Headless boot, `~/valheim-testbed/boot-check.sh terr 2640` | BOOTED, 0 patch failures, "Territory file format self-check passed", "Loaded 1 territory for world 'bcterr'" (hand-written file, only the game's own Newtonsoft 13.0.2 present) |
| Better Continents co-boot, `WORLD=ProxEra01 ~/valheim-testbed/boot-check.sh terr-bc 2650 540` | BOOTED in 21 s with BC 0.9.2 on a copy of the BC world ProxEra01 (heightmap and biome map). BC read the world's own settings; "self-check passed"; "Loaded 1 territory for world 'ProxEra01'"; 0 errors; clean shutdown. Static audit of the BC source: see README "Verified" |
| Hex fold | `HexiumDist/TerroritySmellatory-v0.1.1.zip`, 6 entries. DLL sha256 `aa47afad3cd2cf53…`, md5 `9ff94209da217659e73b24d6a75dea36` (zip = bin = staged = boot-tested on `terr` and `terr-bc`). ServerSync floor 0.1.1 |
| Playtest, 0.1.0 (Rohan's Gale profile `testing-smelly`, IH 1.85, dedicated server, two players) | Placement, panel saves, snapshot, banners, forced PvP and the large map ran, with no mod errors in the client log. Four findings (see item 11 above), fixed in 0.1.1 |
| Better Continents start-up watch (`Integration/BetterContinentsWatch.cs`, from `Plugin.Start`) | Logs "Better Continents 0.9.2 detected." on `terr-bc` and "Better Continents not loaded." on `terr`. Both BOOTED, 0 errors |
| Deployed / published | **Nowhere.** Rohan makes the Gale profile himself. No hex web, no git repo yet |

**Never exercised with a client:**
- the panel;
- IH tool selection and placement;
- snapshot delivery on join;
- every client rule: ward access, pickables, spawns, structure damage, PvP hold, Sanctuary SE, banners, map
  outline, ground borders;
- the save path (`File.Replace`, with a copy/delete/move fallback).

## Settled (2026-09-29)

- **"BC compatible" = Better Continents**, confirmed by Rohan. A change to the join handshake, world size,
  spawns or the map must keep BC working. Re-run the `terr-bc` co-boot after any such change. The wire and
  file formats stay versioned anyway.
- **Gale tester profile:** Rohan makes "TerroritySmellatory Only Tester" himself. Don't create it.
- Owner-managed membership (non-admin requests) is out of scope for now. Only admins edit.

## Next playtest (0.1.1)

Re-check the four fixes first:
- Forced PvP on: entering shows the banner with the PvP notice on a second line, and so does leaving.
- The panel: hints sit under their option, rows are one height, and lit buttons don't grow.
- The minimap shows the outline, clipped to its square, with a thin rim even on a big territory. The name
  shows while the centre is in view.
- "Buildings cannot be damaged": no one's hits land, the owner's and an admin's included. The hammer's
  Remove still works for members.

Then the rest, on a listen host or the testbed plus clients, **on a Better Continents world**, so the plain client's join
also runs BC's settings download before the snapshot. That is the one BC interaction a headless boot cannot
cover:
- **Admin client:** TerroritySmellatory + Infinity Hammer 1.84 + Server Devcommands + World Edit Commands,
  with devcommands on.
- **Plain client:** TerroritySmellatory only (JsonDotNET comes with it).

Check in this order:
1. The Home panel opens, Esc closes it without opening the pause menu, and typing a name keeps its spaces.
2. **Territory** tool: circle, square and rotated rectangle. The ruler matches the ground border and the
   map outline.
3. The server log shows "created territory #N", and the plain client receives the snapshot (`terr_whoami`,
   `terr_list`).
4. On the plain client: build and door refused as a visitor; allowed after being added as a member.
   Pickables, structure damage, PvP forced off/on (the inventory toggle locks and is restored on leaving),
   Sanctuary icon and bonuses, the entry banner, and a territory with no monster spawns.
5. **Territory remove** (twice to confirm) and **Territory edit** → Move / reshape.
6. Restart the server; territories reload from `BepInEx/config/TerroritySmellatory/worlds/<world>.json`,
   and a `.bak` exists after the second save.

## How it is built (one line each)

- **Flow:** the admin's panel draft plus an IH ruler placement runs `terr_place` on the admin's machine →
  per-peer ZRpc `TerroritySmellatory_Request` → the server checks `ZNet.IsAdmin(socket host)`, sanitises,
  saves, bumps the revision → it pushes `TerroritySmellatory_Snapshot` over each peer's ZRpc once
  `m_characterID` is set → each client's `TerritoryIndex` → rules.
- **IH (placement only):** `infinity_tools_terroritysmellatory.yaml` holds the tools *Territory*,
  *Territory remove* and *Territory edit*, written only when IH is loaded. Its first line marks it as
  managed. Selected via `tool <name>`, or `tool_cmd` when the yaml is missing. There is no IH assembly
  reference.
- **Rules:**
  - `PrivateArea.CheckAccess` postfix, with a BuildScope counter (prefix + finalizer on
    `UpdatePlacementGhost`, `RemovePiece`, `Repair` and `Attack.SpawnOnHitTerrain`) to split build from access;
  - `Pickable.Interact`;
  - `SpawnSystem.IsSpawnPointGood`, `CreatureSpawner.Spawn`, `SpawnArea.SpawnOne`;
  - `WearNTear.RPC_Damage`;
  - `RandEventSystem.GetValidEventPoints` (server);
  - `Skills.OnDeath`;
  - the PvP hold, via an `InventoryGui.UpdateCharacterStats` prefix and postfix;
  - the Sanctuary `SE_Stats`, dual-registered on `ObjectDB.Awake` and `CopyOtherDB`.
- **Precedence:** Priority first, then smaller area, then lower id. `Denying(p, r, …)` handles sized checks.
- **UI:** VikingOS `GiltFrameTheme` IMGUI; `StoreGui.IsVisible` answers yes while open plus a 1-frame grace;
  `UIFocus` text token raised from Update; `_pending` actions run after the layout.
- **Visuals:**
  - Map: its own clipped mesh outline (`TerritoryOutline`, constant rim) plus vanilla's pin-name prefab, on
    the large map and the minimap. Not pins.
  - Ground: harvested `CircleProjector` segments, drawn only within `BorderRange` (60 m) of the player,
    default Always, with a per-territory `ShowBorder` flag.
- **Persistence:** Newtonsoft (`ValheimModding-JsonDotNET-13.0.4` in the manifest; `libs-Tools/Newtonsoft.Json.dll`,
  `Private=false`). Read-only after a failed read. A start-up self-check round-trips a full record.

## Lessons from this session (already filed in libs-Tools)

- `UnityEngine.JsonUtility` drops `List<T>` of mod classes in both directions. The self-check caught it on
  the 1.0.16 server → `libs-Tools/JSONUTILITY-FACTS.md`, master pattern 30.
- Valheim 1.0.16 ships **Newtonsoft 13.0.2 in both client and server `Managed`**; libs-Tools has 13.0.3.
  A .NET 8 harness needs the NuGet 13.0.3 package (net6.0 build), because the Framework build throws on
  `System.Security.Permissions`.
- IH tool integration facts → `libs-Tools/INFINITY-HAMMER-TOOLS-FACTS.md`. `<r>`/`<w>`/`<d>` are
  half-extents, `<a>` is the ghost's yaw, and the ruler frame is the placement frame.
- Use per-peer ZRpc, not routed, when the sender's identity matters → master pattern 31.
- 1.0.16 `ZNetPeer.m_playerID` exists (clients send it via `ZNet.RPC_PlayerID`).
- A client must never send a request after the server has said it is not an admin (Wonderland's RPC
  whitelist kicks non-admins).
- Vanilla's centre message is one slot, and the last writer in a frame wins (`SetPVP` writes its own notice)
  → master pattern 32.
- GiltFrameTheme: `Note` is centred, `Primary` is taller than `Button`, and no style has margins. Derive
  row styles → master pattern 33.
- Both map images carry a `RectMask2D`; draw area outlines as a mesh with a constant rim → master pattern 34.
- Small traps: an XML comment cannot contain `--`; read Esc with `ZInput.GetKeyDown`; never trim a text
  field while it is being typed; a C# verbatim string does not process `﻿`.

## Commands

```bash
export DOTNET_ROOT="$HOME/.dotnet"; export PATH="$HOME/.dotnet:$PATH"
cd ~/WubarrkCODING/ValheimMods/TerroritySmellatory
dotnet build -c Release                                        # builds and hex-folds
dotnet run -c Release --project tests/TerritoryTests.csproj    # 613 checks
R=~/WubarrkCODING/ValheimMods/libs-Tools
dotnet run --project $R/refcheck -c Release -- bin/TerroritySmellatory.dll --refs "$HOME/.local/share/Steam/steamapps/common/Valheim/valheim_Data/Managed" --refs $R
dotnet run --project $R/refcheck -c Release -- bin/TerroritySmellatory.dll --refs "$HOME/.local/share/Steam/steamapps/common/Valheim dedicated server/valheim_server_Data/Managed" --refs $R
cp bin/TerroritySmellatory.dll ~/valheim-testbed/profiles/terr/BepInEx/plugins/TerroritySmellatory/ && ~/valheim-testbed/boot-check.sh terr 2640 480 "TerroritySmellatory\]"
# with Better Continents 0.9.2 on a copy of the BC world ProxEra01 (profile terr-bc)
cp bin/TerroritySmellatory.dll ~/valheim-testbed/profiles/terr-bc/BepInEx/plugins/TerroritySmellatory/ && WORLD=ProxEra01 ~/valheim-testbed/boot-check.sh terr-bc 2650 540 "TerroritySmellatory\]"
```

## Rohan's standing rules (from memory)

1. Always check libs-Tools first (`IMPLEMENTATIONS/MASTER_IMPLEMENTATIONS.md`).
2. Never touch Wonderland Gale profiles; builds go to the mod's own "Only Tester" profile. For this mod,
   Rohan creates that profile himself.
3. Not the live server admin: look, never touch. Build, then hex fold, then report the hash; "hex web"
   only when asked by that name.

Also: use the standard boilerplate dependencies (JsonDotNET) rather than hand-rolling, and use `sudo -A`
for sudo.
