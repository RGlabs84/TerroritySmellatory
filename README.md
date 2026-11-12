# TerroritySmellatory: developer notes

`wubarrk.terroritysmellatory`, **0.4.3**. Pure BepInEx (no Jotunn), Valheim **1.0**. The one package
dependency is the workspace's standard `ValheimModding-JsonDotNET` (Newtonsoft), used only by the server's
territory file. Valheim 1.0 ships Newtonsoft 13.0.2 in both Managed folders anyway. The player-facing description is in `HexiumDist/README.md`; the architecture write-up is `libs-Tools/IMPLEMENTATIONS/TerroritySmellatory.md`.

## The flow

```
ADMIN (Infinity Hammer installed)             SERVER                         EVERY CLIENT (no IH)
panel: rules, members  ─┐
IH "Territory" tool ────┴─ terr_place ──ZRpc──▶ admin? (adminlist.txt,
IH "Territory remove" ──── terr_remove_at        by the connection)
IH "Territory edit" ────── terr_edit_at          clamp, save per world ──ZRpc──▶ snapshot → TerritoryIndex
IH "Territory combine" ─── terr_combine_at
                                                  (JSON in BepInEx/config)        │
                                                                                  ├ ward CheckAccess (build/access)
                                                                                  ├ Pickable, WearNTear, Skills.OnDeath
                                                                                  ├ SpawnSystem / CreatureSpawner / SpawnArea
                                                                                  ├ PvP hold, Sanctuary SE, banners
                                                                                  └ map outline, ground border
                                                  RandEventSystem (raids) ◀── server-side rule
```

Infinity Hammer is for **placement only**. It is a soft dependency reached only through a yaml file (`infinity_tools_terroritysmellatory.yaml`) and IH's own `tool` / `tool_cmd` commands. The mod holds no reference to IH's assembly.

## Build, test, verify

```bash
export DOTNET_ROOT="$HOME/.dotnet"; export PATH="$HOME/.dotnet:$PATH"
cd ~/WubarrkCODING/ValheimMods/TerroritySmellatory

dotnet build -c Release                                   # also hex-folds HexiumDist (tools/fold.py)
dotnet run -c Release --project tests/TerritoryTests.csproj   # off-game harness, 7,329 checks
# check a territory world file the way the server would load it, and say what it would accept silently
# (typo'd keys and flags, missing sizes, clamps, cleared rules, a town that loses its continent's PvP); exit 0 unless there is an ERROR
dotnet run -c Release --project tests/TerritoryTests.csproj -- --check path/to/world.json [--at x,z]... [--tree]
# rewrite a 0.3.x file so its nested towns are sub-territories (0.4.0), changing nothing they do - checked territory by territory and for the whole file
dotnet run -c Release --project tests/TerritoryTests.csproj -- --link in.json --out out.json

R=~/WubarrkCODING/ValheimMods/libs-Tools
dotnet run --project $R/refcheck -c Release -- bin/TerroritySmellatory.dll \
  --refs "$HOME/.local/share/Steam/steamapps/common/Valheim/valheim_Data/Managed" --refs $R
dotnet run --project $R/refcheck -c Release -- bin/TerroritySmellatory.dll \
  --refs "$HOME/.local/share/Steam/steamapps/common/Valheim dedicated server/valheim_server_Data/Managed" --refs $R

~/valheim-testbed/boot-check.sh terr 2640 480 "TerroritySmellatory\]"   # local headless server, profile 'terr'
WORLD=ProxEra01 ~/valheim-testbed/boot-check.sh terr-bc 2650 540 "TerroritySmellatory\]"   # with Better Continents 0.9.2 on a BC world
tests/e2e/run-e2e.sh bin/TerroritySmellatory.dll [--bc]   # headless server + headless player, scripted (tests/e2e/README.md)
E2E_WINDOWED=1 E2E_BOOTH=1 E2E_SCREEN_W=1024 E2E_SCREEN_H=768 tests/e2e/run-e2e.sh bin/TerroritySmellatory.dll --spike a --graphics   # the panel booth: the panel photographed over the main menu
```

The icon is hand-made (`tools/icon_master.png`, 1024 px, by Rohan): **never run `tools/make_icon.py`**, which would overwrite `HexiumDist/icon.png`.

The harness (`tests/`) compiles the mod's **real** `TerritoryShape`, `TerritoryGround`, `Territory`, `TerritoryEdits`, `Inheritance`, `TerritoryIndex`, `Protocol`, `SnapshotParts`, `TerritoryJson`, `PlacementArgs`, `OutlineGeometry`, `UnionOutlineGeometry`, `BannerPlan`, `BarrierMath`, `BarrierPattern`, `DoorMath`, `ExploredBits` and `NameFit` against small Unity/ZPackage stubs and the real Newtonsoft 13.0.3 (the NuGet package's net6.0 build). Unity's quaternion maths is ported verbatim, and so is Infinity Hammer 1.84's command substitution (`ToolSelection.HandleCommand`). A deliberately flipped rotation sign fails 183 checks, so the harness can fail.

## Verified vs not yet

**Verified (2026-11-01, 0.4.3 - rebuilt for Valheim 1.0.21, network 41):**
- No source change. 0 warnings, `refcheck` OK on both sides against 1.0.21; none of its Harmony targets is among the 1.0.21 body changes (`Minimap.OnMapDblClick` and the Minimap texture cache changed, but the mod hooks neither), no `WearNTear.UpdateSupport`, `Player.PlacePiece`, `Destructible` or cheat-flag code is touched. Off-game harness and a headless server boot re-run on the rebuilt DLL.

**Verified (2026-10-14, 0.4.2 - Infinity Hammer default tools coexistence fix):**
- 0 warnings. `refcheck` OK on both sides against Valheim 1.0.17 (client md5 7cf50a7f, server 63943796): 48 Harmony targets. 7,354 off-game checks (25 new checks for IH default toolset definitions, parsing, case insensitivity, quotes, comments, and fallback).
- Bootstrapping: `infinity_tools.yaml` checked prior to writing `infinity_tools_terroritysmellatory.yaml`. Seeded via `InfinityTools.ToolManager.CreateFile()` reflection or embedded 17-tool template fallback (pipettes and 14 hoe tools), avoiding IH `ToolData.Count > 0` default tool suppression on clean profiles.

**Verified (2026-10-12, 0.4.1 - territories only over uncovered ground, the minimap name on one line):**
- 0 warnings. `refcheck` OK on both sides against Valheim 1.0.17 (client md5 7cf50a7f, server 63943796): 8,404 references, 48 Harmony targets (the five new hooks on `Minimap`). 7,329 off-game checks: 8 `fog:` / `name:` groups
  (`tests/MapFogTests.cs`: the copy of vanilla's two explored sets into the mask for random sets and every texture size, box by box against a full copy, and the name fitting over 4,000 random names). Three mutants of that code
  (either-set turned into both, a box that ignores the vertical extent, a fit that accepts names 50% too wide) each turn their own groups red on a copy of the tree.
- Rig, `--graphics`, the exact DLL (md5 31b8127a): spike a (`F04-fog-gate-pixels`): a real `ScreenSpaceCamera` canvas rendered into a texture through the mod's own `ExploredGate` draws white exactly on the covered cells of a 32 x 32 mask, the
  same canvas with the gate off draws the whole map rect, a zoomed `uvRect` shows the window's cells, a graphic larger than the map stops at the `RectMask2D` (its first version failed 70 of 1,024 cells because the TEST inset the clip and expected
  the mask to span the canvas; the pictures showed the clip was right). Full run `20261012-150314`: **183 passed, 0 failed, 0 soft** - `P41b-fog-mask` (the mask equals the game's own two BitArrays cell for cell as built and after the player was
  put down on new ground three times, 154 -> 397 cells uncovered through the hooks, and the small map's gate is on with the mask and the picture's `uvRect`) and `P41c-label-one-line` (the real label shows names of 7 to 94 characters on one
  line within its 183 units; the 0.4.0 way wraps the longest to 4 lines). The first full run (`20261012-144827`, the same DLL) was 134 / 1 / 0: `P30h-flare-drawn` failed because a wild `Skeleton_Meadows_noarcher` stood 0.0 m outside
  `TS Barrier Guard` and 20 of its flares were counted in the control (P30c records the same contamination and excuses it; P30h does not - an inconsistency in the rig, not in the mod: the second run was clean with no wild creature).
- Not yet seen: the clip on the real map while a person walks (the rig proves the mask, the gate and the pixels separately, and on Mesa llvmpipe's software GL only), and the name beside a skinned minimap frame.

**Verified (2026-10-11, 0.4.0 - sub-territories, the PvP notch, a territory list in pieces):**
- 0 warnings. `refcheck` OK on both sides against Valheim 1.0.17 (client md5 7cf50a7f, server 63943796): 8,285 references, 43 Harmony targets. 5,656 off-game checks in 132 groups: 14 `sub:` groups (resolution through three
  levels, precedence by link, create / update / delete / combine, the file's every shape of a sub-territory, banners by link, `--link`, the checker), 15 `panel:` groups (197 checks on `UI/PanelMath.cs`), 7 `parts:` groups
  (the piece sender and assembler, a real 600-territory snapshot over 512 KiB, and 600 random server-and-client runs that never apply an older list after a newer one).
- The e2e rig on the DLL (graphics world, run `20261011-164753`): **181 passed, 0 failed, 0 soft**. The 0.4.0 phase is 29 ids, and the rig's server sends the list in 512-byte pieces, so every phase of the run travels in pieces.
  Earlier runs the same day failed only on the rig's own mistakes (155/17: the test site overlapped an older territory and an old file check cast a flags object; 178/3 and 180/1: a banner check that looked after the banner had gone).
- The panel booth (`E2E_WINDOWED=1 E2E_BOOTH=1 ... --spike a --graphics`, run `20261011-180336`): a batchmode client never calls the panel's `OnGUI`, so a full run cannot draw it; a windowed client on the private Xvfb does. The panel
  over the main menu on a made-up world: 319 repaints, 0 draw errors, 19 pictures, looked at by eye (`tests/e2e/README.md`): fine at text scales 0.7, 1.0 and 1.5; at 2.0 on a 1024-pixel-wide screen the PvP row is wider than the panel.
- Boot checks with the exact DLL: `WORLD=subtest ~/valheim-testbed/boot-check.sh terr 2640` (a 5-territory file, 3 of them sub-territories): BOOTED in 20 s, "Loaded 5 territories ... 3 of them are sub-territories", self-check passed;
  `WORLD=ProxEra01 boot-check.sh terr-bc 2650`: BOOTED in 22 s, "Better Continents 0.9.2 detected", self-check passed.
- Mutations, harness only, on copies of the tree: 12 mutants of the model and file code, 11 caught (1 to 20 failing groups each), the twelfth equivalent; the panel helper broke `Rebase`, `PutInside`, `ToggleRule` and `Resync` in 11 variants, all caught.
- Reviews: an independent reviewer went through the model, server, file and console code (7 findings, all fixed: the snapshot carried a continent's members once per town, two caches went stale after a parent's edit, a v4 file 0.3.2
  loaded was refused, moving a territory changed its rules, combining dropped people-following, wrong rollback wording, two texts); a second review (a workflow of four reviewers) was started and stopped before it reported, on Rohan's
  usage rule, so the panel and the pieces were checked by their authors, the harness, the rig and the booth only.

**Not yet confirmed (0.4.0):** the panel used in a world by a person (it has been drawn and photographed in a test window, not clicked), the banner notch by eye (it is checked by its pixels and its place on the banner), the nested
barrier in a running game, and the panel at text scale 2.0 on a screen under about 1100 px wide (the PvP row overflows). The Valtima world's real file has not been run through 0.4.0 on a live server (it was run through `--link`
and the checker).

**Verified (2026-10-11, 0.3.2 - territories inside territories):**
- 0 warnings. `refcheck` OK on both sides against Valheim 1.0.17 (client md5 7cf50a7f, server 63943796): 7,569 references, 43 Harmony targets. 4,997 off-game checks.
  The new ones: the banner's nesting walks (a continent, a region and a town, in, out, logged in inside, silent outer, silent inner, next-door neighbours, Priority);
  the barrier's pockets (`BarrierMath.PocketsFor` / `Governs` / the pocket-aware `Decide`, against `TerritoryIndex.GoverningAt`); `MapNameOnly` (bit 14, needs `ShowOnMap`,
  all 2^15 flag combinations through `CleanFlags`, the file by name, the wire); file version 4 (`FileVersion`, and 3 / 2 / 1 files still load writable); a world file of two
  continents and 150 towns (one of 23 parts) read, sanitised, indexed, governed and sent as a snapshot; continent-sized parts (exact to 100 km, clamped past it); and the public
  README's own example file, read from `HexiumDist/README.md` by the test, so the documented recipe cannot rot. After the fold (the DLL is unchanged): `tests/WorldFileCheck.cs`,
  the `-- --check` mode above, written for the Valtima session's generator; its own three groups are in `tests/NestingTests.cs`.
- `~/valheim-testbed/boot-check.sh terr 2640` with `WORLD=nestedtest` and a 152-territory nested file in the profile: BOOTED in 21 s, "TerroritySmellatory 0.3.2 loaded",
  "Territory file format self-check passed", "Loaded 152 territories for world 'nestedtest'", no errors. `WORLD=ProxEra01 boot-check.sh terr-bc 2650`: BOOTED in 23 s,
  "Better Continents 0.9.2 detected", self-check passed, "Loaded 1 territory for world 'ProxEra01'". Both with the exact folded DLL.
- The e2e rig on the folded DLL (plain world, run `20261011-123954`): **147 passed, 0 failed, 1 soft** (P05h, the minimap, unobservable headless) - the same count as 0.3.1.
  Its barrier phase (a Greyling held at the edge and never in; a pushed-out thing; tamed and passive creatures through; a non-member held and a member through; flares) runs
  through the new pocket code with no change, and its two rule-2 checks say Rohan's game data was untouched. Its S31b file check was asserting `"version": 3` exactly; it now
  asks for 3 or more (0.3.2 writes 4). No `--bc` or `--graphics` rig run this time (the BC co-boot above stands in for the first; nothing visual was added that the rig draws).
- Mutations, harness only, on copies of the tree: the 0.3.1 `BannerPlan` fails the new banner walk and the changed market-in-a-kingdom check; a `Governs` that ignores pockets fails
  the barrier nesting group; a `CleanFlags` without the map rule fails the all-combinations check and the name-only group. (The README-example test also fails in those copies, only
  because they have no `HexiumDist`.)

**Not yet confirmed (0.3.2):** the nested barrier in a running game (it is enforced by a player's own game, which neither the harness nor a dedicated server runs; the rig has no
phase for it yet, so the rig's pass proves only that nothing else moved), the name-only label on the large map and the minimap, and the epic banner sequence on a real walk out of a
town. The border cap (`Borders.MaxDrawnEdge`) and `MapOverlay`'s name-only path are game-side and unrun. A rig phase for them is the next thing to write if a playtest is not soon.

**Verified (2026-09-29, 0.3.1 - auto-closing doors, and the barrier flare fixed):**
- 0 warnings. `refcheck` OK on both sides: 7,547 references, 43 Harmony targets. 4,694 off-game checks.
- The e2e rig: 147/0/1 on plain and on BC, and 152/0/0 with `--graphics` (a private Xvfb with `-force-glcore`: OpenGL through llvmpipe). With graphics the flare's pixels are counted in the game camera, and the minimap outline check passes too.
- The door checks: a door closes on time; it waits for a player within 3 m; and these stay open: outside territories, without the rule, drawbridges and window shutters, and a nested market without the rule. A forced run on 0.3.0 turns the door checks red.
- The 0.3.0 flare bug (`LoadRawTextureData` on a mip-chained texture, so no flare at all in game) is now caught twice: a harness source scan, and the rig's H08, which fails on "could not build".

**Verified (2026-09-29, 0.3.0 - the runic barrier):**
- 0 warnings. 2,893 off-game checks: the flag prerequisites (all 8,192 flag combinations), file v3, `NearestOutside` against a brute-force search on random compounds, the hold decisions, and the flare pattern (lattice, runes, ripple, envelope).
- The end-to-end rig: 134 passed, 0 failed, 1 soft (the map) on the plain world and on Better Continents. The barrier checks:
  - a Greyling hunting the player by its own AI is held at the edge, never deeper than 0.00 m, while in the control territory it gets 5-6 m in;
  - a Greyling put inside is out in 0.04 s;
  - flares fire;
  - tamed and passive creatures pass;
  - a non-member is held and evicted with the message, and walks in once made a member;
  - the flags are in the file (version 3) and survive a restart.
  A forced run of the 0.3.0 phase on the 0.2.0 DLL turns the nine barrier checks red, so they can fail. 0.2.0 and 0.1.1 still pass (114 and 104).
- Offline renders of the flare: `<scratchpad>/barrier-preview/`.

**Not yet confirmed (0.3.0):** a playtest - the flare's look in daylight and which stock shader the game picks (the log says on first flare), the feel of being held, sub-frame jitter of a monster slamming the wall, and remote players' flares (dual mode needs a second Steam account).

**Verified (2026-09-29, 0.2.0):**
- 0 warnings. `refcheck` OK on the client and dedicated-server `Managed` folders: 5,935 references, 42 Harmony targets.
- 1,304 off-game checks: combined ground, part edits, wire/file v2, the banner's decisions (`BannerPlan`), the union outline mesh.
- The end-to-end rig (`tests/e2e/`): 114 passed, 0 failed, 1 soft (map outlines cannot be seen headless) on the plain world and on Better Continents ProxEra01, including combining by hammer, a visitor refused in the second part, a quiet seam, the epic banner, the minimap name, and the parts saved and reloaded. The 0.1.1 DLL still passes at 104/0/1.
- Boots clean on the local dedicated server, with and without BC 0.9.2, loading each profile's 0.1.x territory file.

**Not yet confirmed (0.2.0):** a playtest; the combined outline and the epic banner on screen (the rig runs `-nographics`, so it reads their state, not pixels); the rig's mutation check (started, stopped unfinished to save usage); dual mode (needs a second Steam account).

**Verified (2026-09-29, 0.1.1):**
- Compiles against 1.0.16 with 0 warnings.
- `refcheck` is OK on the real client and dedicated-server `Managed` folders: 4,710 references, 40 Harmony targets.
- Boots clean on the local 1.0.16 dedicated server; the file-format self-check passes and a hand-written world file loads, with only the game's own Newtonsoft present.
- **Better Continents** ("BC compatible" is a confirmed requirement): boots alongside BC 0.9.2 on a copy of the BC world ProxEra01 (heightmap and biome map). BC reads the world's own settings, the self-check passes, the world's territory file loads, and the shutdown is clean. Static audit of the BC source: the only shared Harmony target is `ZNet.OnNewConnection` (BC's prefix never skips it). BC wraps the server's `PeerInfo` handler to send its settings first, and the snapshot waits for the joining player's character, which only exists after that. BC's world-size patches rewrite edge, water and height constants only, and the map overlay uses the minimap's own conversions.
- 613 off-game checks pass, including the map outline mesh.
- **Playtested once, as 0.1.0 (Rohan, 2026-09-29):** hammer placement, panel edits, the snapshot, banners, forced PvP and the large map all ran. Four findings, all fixed in 0.1.1:
  - the PvP notice wiped the entry and exit banners;
  - the panel's hints and rows did not line up;
  - there was no minimap outline;
  - members' and admins' hits still damaged "protected" buildings.

**Not yet confirmed in a playtest:**
- the four 0.1.1 fixes;
- harvest protection, spawns, raids, skill loss, the Sanctuary effect and ground borders;
- Territory remove and Territory edit;
- the save path (`File.Replace` falls back to copy/delete/move on any failure);
- a plain client joining a Better Continents world, so the join also runs BC's settings download before the snapshot arrives.

## Layout

| Path | |
|---|---|
| `Plugin.cs` | Entry point: isolated per-class patching, guarded ticks. |
| `Core/` | `TerritoryShape` (one part: circle, square or rotated rectangle), `TerritoryGround` (a territory's whole ground: its parts as one area), `Territory` (data, sanitising, wire record), `TerritoryEdits` (combine, add, move, remove and split parts, and the automatic parent / update rules of a create or an edit; pure), `Inheritance` (sub-territories: stored vs effective records, `Resolve` / `Canonicalize` / `Bake` / `ReleaseChildren`, the tree checks, the children-first order; pure), `TerritoryIndex` (precedence: a sub-territory first, then by the union's area; "who governs here"; `IsInside` by link), `TSConfig`. |
| `Net/` | `Protocol` (versioned ZRpc packages), `SnapshotParts` (a list over 64 KiB goes in pieces on the same ZRpc: pure `Sender` / `Assembler`; the server's `PumpParts` puts one piece a frame into a socket that has room, the client's `OnSnapshotPart` gathers them), `TerritoryServer` (authority), `TerritoryClient`, `TerritoryStore` + `TerritoryJson` (per-world file via Newtonsoft), `ConnectionPatches`. |
| `Rules/` | `AccessRules` (ward CheckAccess + pickables), `SpawnRules`, `WorldRules` (structures, raids, skill loss), `Presence` (the effective territory, PvP, the epic banner in vanilla's biome-banner slot), `BannerPlan` (the banner's decisions: hysteresis, cooldown, freezes; pure), `Sanctuary`, `Barrier` (the runic barrier: holds hostile creatures, and optionally non-member players, at a territory's edge, on the ground its territory governs), `BarrierMath` (its decisions: edge contact, push-back, who is held, the pockets of outranking territories inside a barrier; pure), `DoorCloser` (auto-close doors: the SERVER alone closes doors left open in a territory that asks, through `OwnershipHold`, the claim-jump-write-hand-back recipe taken from Get Off My Lawn, with its one Harmony target), `DoorMath` (its decisions: which territory's rule counts, clearance, the open-door clock, excluded prefabs; pure). |
| `Visuals/` | `Borders` (ground outline near the player, outer edge only), `MapOverlay` (outlines on both maps), `OutlineGeometry` / `UnionOutlineGeometry` (the single-part and combined outline meshes; pure), `MapBiomeLabel` (the territory's name in the minimap's biome label), `BarrierFlare` (the blue rune shield that flares where something pushes on the barrier), `BarrierPattern` (its procedural pattern: hex cells, runes, ripple; pure). |
| `UI/TerritoryPanel.cs` | The VikingOS-framed IMGUI panel (`libs-Tools/SharedUI`): 0.4.0's Inside row and picker, the follows / own marks column, the Members tab's people marks, the tree on the All tab, Delete with everything inside. |
| `UI/PanelMath.cs` | The panel's arithmetic and sentences, pure and in the harness: gaps, `Rebase` (an edit in progress carried onto a newer record, marks included), `PutInside`, `ToggleRule`, `Resync`, `FitWidths`, the picker rows. |
| `Integration/` | `InfinityHammerBridge` (yaml tools), `Commands` (`terr_list` as a tree, `terr_parent`, `terr_delete <id> [tree]`), `PlacementArgs`. |
| `tests/` | The off-game harness; `tests/e2e/` the end-to-end rig (a headless dedicated server and a headless client driven by a test plugin). Excluded from the plugin build. |
| `tools/` | `fold.py` (hex fold); `icon_master.png`, the hand-made icon (`make_icon.py` is retired: never run it). |

## Versioning

Bump `<TSVersion>` in the csproj only; it flows into the plugin attribute, the assembly, the manifest and the zip name. Raise `<TSSyncFloor>` for any change an older client would get **wrong**, not only one it cannot read. Appending fields to a territory record or a snapshot is safe for the wire (records are length-prefixed and versioned, and the harness proves a newer record is read cleanly by an older reader), but it still needs a floor raise when an older client would enforce or show something wrong without the new field. 0.2.0 is that case: a 0.1.x client skips a combined territory's extra parts and would protect only its first part, so the floor is 0.2.0. 0.3.0 is another: the runic barrier is enforced in each player's game, so a 0.2.x client would let monsters walk through it, and the floor is 0.3.0. The territory file moved to version 3 with it (two new flag names, no new key) so that a rolled-back 0.2.x server, whose newer-file guard is `version > 2`, refuses to save over a file that holds a barrier. 0.3.1 added a flag name (`AutoCloseDoors`) without moving it, which a rolled-back 0.3.0 server would have dropped on its next save; from 0.3.2 the rule is: **a new flag name moves `TerritoryJson.FileVersion`** (0.3.2 is version 4, for `MapNameOnly`), and the rig's file check asks for "3 or more" rather than one build's number. 0.3.2's floor is 0.3.2: the barrier now holds only the ground its territory governs (each game enforces it for itself), and a 0.3.1 admin's panel would strip the new bit. 0.4.0's floor is 0.4.0 for the same reason: which territory governs a spot is decided in each player's game, and a 0.3.x admin's panel that saved a sub-territory would write its followed values into it and cut it loose. 0.4.0 is **record version 3** (appends `ParentId`, `FollowFlags`, `Follow`; protocol stays 2, `RequestOp.DeleteTree = 11` is appended) and **file version 5** (one new optional key, `"parent"`, and every key of a sub-territory optional: absent = follows). Rolling a world that holds parent links back to 0.3.x is unsupported (the file guard stops its saving; the links would be lost if it did).
