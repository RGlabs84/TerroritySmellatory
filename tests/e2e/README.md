# TerroritySmellatory end-to-end rig (`tests/e2e`)

One command boots **one throwaway dedicated server** and **headless game clients** playing two characters,
**TSAdmin** (a real admin, by the server's own `adminlist.txt`) and **TSPlain** (a plain visitor), runs one
scripted scenario through the real mod on all of them, restarts the server to prove the territory file
reloads, prints PASS/FAIL per check and a summary, and exits non-zero on any failure.

It automates what `HANDOFF.md`'s "Next playtest" list can observe without a screen. It never builds or
touches the mod's own project: the DLL under test is an argument, so the same rig tests 0.2.0 later.

**Latest run, 2026-10-12 (0.4.1, the folded DLL md5 31b8127a).** `--graphics` world `20261012-150314`: **183 passed, 0 failed, 0 soft**. Two checks are new: `P41b-fog-mask` (graphics only: the map's explored mask equals the game's own two BitArrays cell for cell as built and after
the player is put down on new ground three times, and the small map's gate is on with the mask and the picture's uvRect) and `P41c-label-one-line` (any client: the real minimap label shows names of 7 to 94 characters on one line; a plain `<size=80%>` tag, the 0.4.0 way, is the control and wraps to 4 lines).
`--spike a --graphics` adds `F04-fog-gate-pixels`: a driver-owned `ScreenSpaceCamera` canvas rendered into a texture through the mod's own `ExploredGate` (covered cells white, the gate off as a control, a zoomed uvRect, a graphic larger than the map stopped by its RectMask2D). The first full run of the same DLL
(`20261012-144827`) was 134 / 1: `P30h-flare-drawn` failed because a wild `Skeleton_Meadows_noarcher` leaned on `TS Barrier Guard` and 20 of its flares were counted in the control territory (P30c records that and excuses it, P30h does not: a rig inconsistency; run 2 was clean). The runs were queued through `~/valheim-testbed/heavy.sh`, the machine's one-heavy-job lock.

**Latest run, 2026-10-11 (0.3.2, the folded DLL `bbcd51d9...`).** Plain world `20261011-123954`: **147 passed, 0 failed, 1 soft** (P05h), the same count as 0.3.1,
with `E2E_WORK` pointed at the session scratchpad. The barrier phase runs through 0.3.2's pocket code unchanged. The rig has no 0.3.2 phase yet (nested barrier, name-only label).
One edit to the rig: S31b asserted the territory file's `"version"` was exactly 3; it now asks for 3 or more, because 0.3.2 writes 4 (a new flag name moves the number from now on).
Another project's dedicated server (`wl1120`) was running next to it and the rig's memory pre-flight passed (15.3 GB available at the start).

**Earlier runs, 2026-09-29 late night (0.3.1 RC `terr-0.3.1-rc`: auto-close doors added to the flare rebuild below).** Plain world
`20260929-222317` and `--bc` world `20260929-222943`: **147 passed, 0 failed, 1 soft** (P05h); `--graphics` (plain) `20260929-223550`:
**152 passed, 0 failed, 0 soft**. Dry run of the door phase on the 0.3.0 DLL (`E2E_FORCE_031=1`, `224245`): P31a, P31f, S32a, S32b and R32 red,
the four negative controls green, as they must be. With the rig as it now stands the 0.3.0 DLL gives 134 / 0 / 1 (`224856`), the 0.2.0 DLL
114 / 0 / 1 (`225825`) and the 0.1.1 DLL 104 / 0 / 1 (`225450`). The P30c flake did not recur; the driver now notes wild hostile
creatures at a barrier in every run and one stood 0.8 m outside `TS Barrier Guard` in all three 0.3.1 runs (a Skeleton_Meadows_noarcher,
a Boar on `--bc`: creatures the barrier evicted when the guard was marked over them, idling at the edge), which is what makes the counter
global. The rig also exits 5 (memory) whenever other projects' test servers hold the RAM: it needs about 15.5 GB free at the start.

**Earlier, 2026-09-29 night (0.3.1 pre-release DLL: the flare rebuilt on Njord's rendering path).** `--graphics` run
`20260929-204409`: **139 passed, 0 failed, 0 soft** (the minimap check P05h passes for real with a GL device; the flare's P30h, P30i, P30j and H13 are new). Plain
`-nographics` world `20260929-205646`: **134 / 0 / 1**. (An earlier `-nographics` run, `205053`, failed P30c once: 2 flares were requested during the control hunt, which
wants 0; the same run's visitor press saw 26 flares instead of about 10, so something wild was pushing at a barrier nearby; the next run was clean.)

**Earlier, 2026-09-29 evening (0.3.0 pre-release DLL `b21bda68...`).** Plain world `20260929-191318` and `--bc` world
`20260929-191905`: **134 passed, 0 failed, 1 soft** (P05h). The first plain run (`190651`, 133/1/1) failed P30d only, a fixture
fault fixed since (see "The 0.3.0 phase"). With the rig as it now stands the 0.2.0 pre-release DLL gives **114 / 0 / 1** on the
plain world (`192539`) and on `--bc` (`192905`), and the frozen 0.1.1 DLL **104 / 0 / 1** (`193238`): nothing regressed. No safety
violation and no process left behind in any of them.

**Earlier full runs, 2026-09-29.** Frozen 0.1.1 DLL (`aa47afad...`), plain world `20260929-164828`: **104 passed, 0 failed,
1 soft**: the 102/0/1 of the earlier runs (`145214`, `145525`) plus H91 and H12, which the third pass added; the one soft
is P05h, the minimap, which cannot be seen headless (below). 0.2.0 pre-release DLL (`179121d2...`), plain world
`20260929-165642`: **114 passed, 0 failed, 1 soft** (P05h again; the same in `163616` and `164103`). Mutation run `20260929-164453` (0.2.0 with
`EntryBannerStyle = Simple` and `MinimapTerritoryName = false` in the client config): 113 / 1 / 1, the one FAIL being
P20e, as it must. No safety violation and no process left behind in any of them.

```bash
cd ~/WubarrkCODING/ValheimMods/TerroritySmellatory/tests/e2e
./run-e2e.sh /path/to/TerroritySmellatory.dll            # plain world (a copy of profiles/terr, world bcterr)
./run-e2e.sh /path/to/TerroritySmellatory.dll --bc       # Better Continents world ProxEra01 (a copy of profiles/terr-bc)
```

| Option | |
|:--|:--|
| `--bc` | server = copy of `profiles/terr-bc` (BC 0.9.2 + world ProxEra01); the client also gets Better Continents |
| `--keep` | keep the throwaway profiles and fake homes (logs are always kept) |
| `--mode solo\|dual` | `solo` (default): one client plays TSAdmin and TSPlain in turn. `dual`: two clients at once; needs two Steam accounts, see spike (c) |
| `--timeout <s>` | scenario timeout, default 900 |
| `--spike a\|b\|c\|s` | one feasibility step on its own (see "Why solo" below); `s` boots the server only |
| `--graphics` | clients with a real graphics device: `-batchmode -force-glcore -screen-width 640 -screen-height 360` (no `-nographics`) on the run's private Xvfb, where Mesa's llvmpipe gives OpenGL 4.6 in software (`glxinfo`: llvmpipe, core profile 4.6). Plain `-batchmode` still dies in Vulkan init here, hence `-force-glcore`. The whole scenario takes about 7 minutes, and adds the flare's proof (P30h, P30i, P30j, H13; `--spike a` runs F01 and F02) and P05h passes for real: the minimap is drawn |
| `--no-xvfb` | no private virtual X server for the clients: Unity 6 then picks window backend "(null)" and SIGSEGVs loading the first scene (spike a) |

Every run keeps its logs in `$E2E_WORK/e2e-runs/<timestamp>/` (default `E2E_WORK` is the session scratchpad
`/tmp/claude-1000/-home-rohan-WubarrkCODING-TerroritySmellatory/c05dbc9b-9aea-41b6-96de-fed685b99bdf/scratchpad`;
export `E2E_WORK` to put runs elsewhere): `summary.txt` (every check), `results-<role>.txt` (the raw `[E2E]`
lines), `rig.log`, `logs/` (Unity and BepInEx logs of every process), `barriers/` (what the processes told each
other), `artifacts/` (the server's world file and its `.bak`), `safety/` (the rule-2 manifest and check).

Exit codes: **0** all passed, **1** a check failed, **3** another rig run holds the lock, **4** a Valheim
client the rig did not start is running (Rohan may be playing), **5** less than 13 GB MemAvailable before the
clients start, or under 7 GB while they run (the rig yields: the machine's memory watchdog SIGTERMs the BIGGEST
`~/valheim-testbed` dedicated server under 5 GB, which can be another project's server running next to the rig),
**6** no free port pair in 2670-2699 or no Steam id in `loginusers.vdf`, **7** the driver does not compile
against this DLL, **8** the server profile could not be copied, **9** the server did not boot, **10 SAFETY
VIOLATION** (Rohan's Valheim data changed; the run stops at once), **11** the run's private Xvfb did not start.

## How it works

```
run-e2e.sh <dll>
 ├─ flock, pre-flight (no foreign Valheim client, >= 13 GB free, a free port pair in 2670-2699; a WARNING if a
 │    dedicated server this rig did not start runs with the real HOME: see "Rule 2 and the dedicated server")
 ├─ dotnet build TerrE2E.csproj -p:TerrDll=<dll>      the driver, compiled against the DLL under test (publicized)
 ├─ throwaway profiles  ~/valheim-testbed/profiles/terr-e2e-<ts>-{server,solo|admin,plain}
 │    server: copy of profiles/terr (or terr-bc), the DLL under test, TerrE2E, an EMPTY territory file,
 │            adminlist.txt = the logged-in Steam id, VerboseLogging on
 │    client: copy of base-profile (BepInEx 5.4.2350), the DLL under test, TerrE2E (+ Better Continents for --bc)
 ├─ rule-2 baseline (safety.py), BEFORE any Valheim process of the run starts
 ├─ dedicated server, in place, BepInEx via doorstop env vars (boot-check.sh's method), cwd = a throwaway dir,
 │    HOME/XDG = the run's own fake server home (Unity writes PlayerPrefs there, not into Rohan's ~/.config)
 ├─ client(s): valheim.x86_64 -batchmode -nographics, in place, env -i with a
 │                                 fake HOME/XDG, DISPLAY = the run's OWN Xvfb (no screen, -nolisten tcp; never
 │                                 Rohan's display), no audio, cwd = a throwaway dir with steam_appid.txt
 ├─ wait for every role's "[E2E] DONE"; stop the clients; rule-2 check (H90)
 ├─ stop the server (SIGTERM, it saves), boot it again as "server-restart": the file must reload unchanged
 ├─ stop it; rule-2 check again (H91), now that no process of the run is left
 └─ log checks (H-lines), summary, teardown (SIGTERM, SIGKILL after 30 s, profiles and fake homes removed)
```

The driver (`src/`, one BepInEx plugin, `TerrE2E.dll`) is **inert unless `TERR_E2E_ROLE` is set**. Roles:
`server`, `server-restart`, `solo` (or `admin` + `plain` in dual mode), and the spike roles `probe` and `join`.
The processes coordinate through JSON barrier files in the run dir (`admin.created`, `plain.visitor-done`, ...);
every step has a timeout (`TERR_E2E_STEP_TIMEOUT`, default 90 s), and every process quits by itself if the rig
script that started it disappears (`TERR_E2E_RIGPID`).

The admin drives the mod **the way the hammer and the panel do**: the panel's draft (`TerritoryPanel.NewDraft`)
is set field by field as the Rules tab's controls set it; the ground is marked with the exact command lines
Infinity Hammer's three tools run, built from the mod's own templates (`InfinityHammerBridge.PlaceCommand`,
`RemoveCommand`, `EditCommand`) through IH 1.84's substitution (ported as in the off-game harness);
edits go through `terr_edit_at` (which opens the panel on the territory), the panel's own `AddMember`, and what
"Save changes" sends; a reshape arms `TerritoryPanel.ReshapeId` exactly as "Move / reshape" does before
handing over to the hammer. Infinity Hammer itself is not installed in the rig.

Every assertion reads **what came back**: the snapshot the server pushed (`TerritoryIndex`), the answers of
vanilla's own code after the mod had its say (`PrivateArea.CheckAccess`, `Door.Interact`, `Container.Interact`,
`Pickable.Interact`, `WearNTear.Damage`, `SpawnSystem.IsSpawnPointGood`, `CreatureSpawner.Spawn`,
`Player.IsPVPEnabled`, the inventory's PvP toggle, `SEMan`, `GetMaxCarryWeight`, `GetBodyArmor`), what the HUD
showed (`MessageHud`'s centre text, captured by an observer-only postfix, and the status-effect row), what a
chest's open handshake and the inventory did (observer-only prefixes), and on the server the store and, parsed
independently, the JSON file it wrote.

## The scenario and what each check proves

Three territories, one per shape, each with its own rule set (`src/Layout.cs`), laid out around TSAdmin's spawn
point (rotated in 45 degree steps until every centre is on dry land, for Better Continents coasts):

| Territory | Shape | Rules |
|:--|:--|:--|
| `E2E Circle` | circle r=10 | Build + Access + Harvest protection, Sanctuary (health regen x2, stamina x1.5, carry +150, armour +10), PvP **forced off**, Announce with a custom "Welcome to {name}", ShowOnMap, ShowBorder |
| `E2E Square` | square 16 m | StructureProtect, NoMonsterSpawns, PvP **forced on**, Announce (default texts), ShowOnMap |
| `E2E Rect` | 24 x 10 m rectangle, yaw 30 | Build + Access protection, NoRaids, NoSkillLoss, ShowBorder (no banner, not on the map) |

Order (solo mode; dual mode runs the same phases on two processes with barrier waits between them):
**TSAdmin phase 1** (create the three, admin checks, a rules edit) → **TSPlain phase 1** (visitor in all three) →
**TSAdmin phase 2** (add TSPlain as a member, reshape the rectangle, remove the square) → **TSPlain phase 2**
(member checks, reshape and removal seen from outside) → **server restart**.

IDs: `C` client boot, `A` TSAdmin, `P` TSPlain, `S` server, `R` restarted server, `H` rig-level log checks,
`X` driver safety. A 0.2.0 run adds the ids `A20*`, `P20*`, `S30`, `S31` and `R30` (see "The 0.2.0 phase" below), and a 0.3.0 run
adds `A30*`, `A31`, `P30*`, `S31a`, `S31b`, `R31`, `H08` and two extra logins (see "The 0.3.0 phase"; note that `S31-parts-file`
of 0.2.0 and `S31a`/`S31b` of 0.3.0 are different ids);
existing ids keep their meaning, except the four banner checks that follow the Epic banner on 0.2.0.

### Client boot
| ID | Proves |
|:--|:--|
| C01-menu | a headless client (BepInEx + the DLL under test) reached `FejdStartup` with `SteamManager.Initialized` |
| C02-redirect | `Utils.GetSaveDataPath` is the run's save dir, `Application.persistentDataPath` is inside the fake home, Steam Cloud is off |
| A00/P00-character | the throwaway character was created `FileSource.Local` in the run's save dir |
| A00/P00(b)-join | the client joined the run's server with `-password` and has a `Player.m_localPlayer` of that name |

### TSAdmin
| ID | Proves |
|:--|:--|
| A01-snapshot-admin | the snapshot arrived and the SERVER says `IsAdmin=true`, `AdminsBypass=true` (adminlist.txt, by the connection) |
| A02-roster | (dual only) the admin roster the server sends lists TSPlain with its real player id |
| A03/A04/A05-create-* | `terr_place` from the panel's draft created the territory; the result message says "Created #N" and it came back in the snapshot |
| A0x-create-*-rules | the snapshot carries exactly the draft's shape, centre, size, yaw, flags, PvP, Sanctuary values, banner text, creator and revision 1 |
| A06a/b-bypass-* | a non-member admin passes `PrivateArea.CheckAccess` (access, and build inside `BuildScope`) in a protected territory |
| A06c-bypass-harvest | a non-member admin can pick a pickable in a harvest-protected territory |
| A06d-structure-admin | an **admin's** hit on a piece in a StructureProtect territory does no damage (the 0.1.1 fix); the piece's health must be readable before and after (an unreadable fixture FAILS instead of passing as "no damage") |
| A06e-structure-control | control: the same hit in open country does damage (the fixture works) |
| A07a-edit-tool | `terr_edit_at` (the "Territory edit" tool) opens the panel on the territory under the aim point |
| A07b-update-rules | an Update (Harvesting on, priority 5) is saved by the server and comes back with the geometry unchanged and revision+1 |
| A08a/b-member | the edit tool again; the panel's `AddMember` (Add, or "Add by id" when TSPlain is offline) plus a new banner text is saved and comes back |
| A09-reshape | "Move / reshape" + the Territory tool moves the rectangle: same id, same rules, new centre, size and yaw; the panel is disarmed |
| A10a-remove-arms | the first `terr_remove_at` only asks to place again and removes nothing |
| A10b-remove | the second within 6 s deletes it on the server ("Deleted #N") and from the admin's index |

### TSPlain
| ID | Proves |
|:--|:--|
| P01-snapshot-plain | the snapshot arrived and the SERVER says `IsAdmin=false` |
| P02-territories | the plain's `TerritoryIndex` equals the server's world FILE on disk: ids, names, shapes, centres, sizes, yaw, flags, PvP, members, priority, revision |
| P02b-home | the spawn point is open country (`Presence.Current` null) |
| P03a / P04c / P05c | `Presence.Current` follows the player into each territory, and is null again outside (P03l) |
| P03b / P04d | 0.1.1: the entry banner is in the HUD's centre slot: the custom or default text, with vanilla's PvP notice on the second line. **0.2.0 (Epic): see "The 0.2.0 phase"** |
| P03m / P04i | the exit banner, with the PvP notice |
| P05d-no-banner | no banner for a territory without Announce (0.2.0 also: none in the biome-found slot) |
| P03c / P04e | PvP forced OFF (own choice was on) / forced ON (own choice was off) inside |
| P03d / P04f | the inventory's PvP toggle is greyed out and a click on it does not change the player's PvP |
| P03n / P04j | the player's own choice comes back outside and the toggle is usable again |
| P03e-sanctuary | the mod's `SE_TerroritySanctuary` is on the player as SEMan knows it (`HaveStatusEffect` / `GetStatusEffect` by its name hash, exactly once), is an `SE_Stats`, carries the territory's name as its label (`m_name`) and the admin's values (health x2, stamina x1.5, carry +150, armour +10) |
| P03p-sanctuary-hud | what a player sees: the HUD's status-effect row (`Hud.m_statusEffects`, the text Hud writes `m_name` into) reads the territory's name and shows the guard stone's build icon |
| P03f-sanctuary-bonuses | vanilla's own numbers move: `GetMaxCarryWeight` +150 and `GetBodyArmor` +10 against home; the health regen multiplier of `SEMan.ModifyHealthRegen` equals, in the same frame, vanilla's fold over the other effects with the admin's x2 at the effect's place (and is above the fold without it) |
| P03o-sanctuary-gone | outside: no effect with the Sanctuary's name hash, none labelled with the territory's name, and the carry bonus is gone |
| P03g-access-refused | `PrivateArea.CheckAccess` says NO for access, with the territory's own "only members may use this" message |
| P03h-build-refused | `PrivateArea.CheckAccess` says NO for building (inside the mod's `BuildScope`, as its placement/remove/repair/terrain prefixes raise it) |
| P03i-door-refused | a real `wood_door` inside stays shut on `Door.Interact`, and the refusal is the ward's: the door was interactable (`CanInteract`) and `Interact` returned true (a door that cannot be interacted with returns false). The control door outside must open, else FAIL (the refusal would be untested) |
| P03j-chest-refused | a real chest inside does not open on `Container.Interact`: NONE of the open happens (no `RPC_RequestOpen` or `RPC_OpenResponse` for it, no `InventoryGui.Show(chest)`, the inventory never holds it, it is never in use), with the player within the 4 m auto-close distance, as for the control. The control chest in open country must open by all of those, else FAIL (see "Chests" below) |
| P03k-harvest-refused | a valid, unpicked pickable inside refuses `Pickable.Interact` and stays unpicked (vanilla also returns false for an invalid pickable, so it must have been valid); the control outside must pick, else FAIL |
| P04a-structure-protected | a visitor's hit on a piece in the StructureProtect square does no damage (health readable before and after); P04b the control does |
| P04g-no-monster-spawns | `SpawnSystem.IsSpawnPointGood` refuses a Greyling spawn point inside and accepts one outside (SOFT if vanilla refuses the control) |
| P04h-creature-spawner | `CreatureSpawner.Spawn` returns nothing inside and a creature outside (SOFT if the control spawns nothing) |
| P05a-rotation | the YAW is enforced: refused at a point inside only the rotated box, allowed at one inside only the unrotated box. Which box holds which point is the driver's own maths, never the mod's `Shape.Contains` |
| P05b-updated-rules | the admin's rules edit reached the plain client |
| P05e/P05f | access, build and (after the edit) harvesting refused in the rectangle (P05f as P03k: valid pickable, control must pick) |
| P05g-border | PASS/FAIL: the ground border `TS_Border_<id>` exists near the edge with segments (it is drawn headless; a missing border is a FAIL) |
| P05h-map | SOFT/PASS: `TS_TerritoryArea` map outlines under the minimap. Always SOFT headless (see "Map outlines stay SOFT") |
| P06-no-request | `terr_place` and `terr_remove_at` on the plain client are refused LOCALLY ("the server says you are not one") and nothing is sent |
| P07a-member-snapshot | the member edit reached the plain client |
| P07b-banner-updated | the edited banner text shows on entry (0.2.0: as the Epic banner's subtitle) |
| P07c/d/e/f-member-* | as a member: CheckAccess yes (access and build), the door opens, the chest opens by every signal of P03j, the pickable picks. These pass or fail on their own: a control that fails too never turns them SOFT |
| P08a/b/c-reshape-* | the moved rectangle arrived; the old ground is open, the new ground protected; Presence follows it |
| P09a-removed-snapshot | the removed square disappeared from the plain's index |
| P09b-removed-unprotected | the chest that was protected inside the removed square takes damage now |

### Server and restart
| ID | Proves |
|:--|:--|
| S01-mod-loaded / S02-selfcheck / S03-store | the DLL under test loaded on the dedicated server; its file-format self-check passed; the store saves to `BepInEx/config/TerroritySmellatory/worlds/<world>.json` |
| S10a / S12a / S20a | the JSON file on disk (parsed independently of the mod's reader) equals the server's store, every persisted field (geometry, colour, owner, members with names, flags, PvP, priority, the five Sanctuary values, messages, creator and creation time, revision), after the creates, after the rules edit, at the end. A key missing from the file is a FAIL with its reason |
| S10b / S12b | the file holds what the admin created (shape, size, flags, PvP, creator). Without a readable file S10b, S10c and S12b FAIL; they are never left out |
| S10c-bak | after the third save a `.bak` exists and holds the previous save (2 records): `File.Replace` (or its fallback) keeps one generation |
| S20b-final-content | on disk: TSPlain is a member of the circle, the rectangle moved under the same id, the square is gone |
| S21-no-request-from-plain | the server received 0 territory requests from TSPlain (an observer counts every request by sender) |
| S22-admin-by-server | TSAdmin is an admin by vanilla `ZNet.IsAdmin` and by the mod |
| S23-seam | TSPlain is a non-admin for the mod only (the seam), while vanilla lists its shared Steam id |
| S24-peers | how many players were in the world at once (1 in solo mode, 2 in dual mode) |
| R01-reload | after a real restart the store holds exactly what the FIRST server's store held at the end (written by the driver from the store, not copied from the file), every persisted field, the same number of territories, and is writable. So a field the save drops fails here even though the file and its own reload agree |
| H01..H07 | server log: "TerroritySmellatory x loaded.", "self-check passed", 3 "created territory #N" lines, no refusal for TSPlain, the restart's "Loaded N territories for world ..."; no mod error or exception in any BepInEx log; every patch class bound |
| H91-rule2-servers | rule 2 again after the server and the restarted server have stopped (the H90 check runs while the server is still up, and the restart comes after it). A violation here ends the run with exit 10 after the summary |
| H10-done-* | every role reported DONE |
| H12-results-complete | every driver's DONE counters (passed/failed/soft) match the lines in its results file, and in solo mode every one of the 104 check ids of the scenario reported (PASS, FAIL or SOFT; 114 when the DLL under test is 0.2.0 or later, 134 when it is 0.3.0 or later, judged by its own "loaded." line: a 0.2.0 or 0.3.0 DLL whose extra phase silently skipped therefore FAILS). A run passes on "no FAIL line", so a check that silently never ran would otherwise be invisible. Dual mode is untested and only gets the counter half |
| H11-mem-watchdog | the memory watchdog did not fire during the run |
| H90-rule2 | after every client run: every file under Rohan's `~/.config/unity3d/IronGate/Valheim` and Valheim's Steam Cloud dir is byte-for-byte unchanged (sha256) since the baseline taken BEFORE the run's first Valheim process (the server included), and no file in either install dir was added, removed or touched (size and mtime of every file); a violation stops the run at once (exit 10). Steam's own `userdata/<id>/892970/remotecache.vdf` changes on every run (Steam's bookkeeping when a Valheim session starts and ends; the cloud files in `remote/` stay unchanged) and is reported as a note in `safety/check.json`, not as a violation |

### The 0.2.0 phase (combined territories, the Epic entry banner)

The driver is compiled against whichever DLL it is given, so 0.2.0-only members are reached by name (`src/Features.cs`,
reflection) and the phase runs only on a DLL that has them; on 0.1.1 the admin logs ONE line, `[E2E] INFO 0.2.0 phase
skipped: ...`, and nothing else changes. `Features.EpicBanner` is true when the DLL has the `EntryBanner` class and the
client's `EntryBannerStyle` is `Epic` (the default); then the entry-banner checks below apply, otherwise the 0.1.1 forms.

**Entry banner on 0.2.0 (P03b, P04d, P07b, P05d).** No longer a centre message. It is vanilla's biome-found banner: an
instance in `MessageHud.instance.m_biomeMsgInstance` whose `Title` shows the territory's name and whose `TS_EntryText`
child is the entry text as a subtitle (none when the territory keeps the default text, as the square does). The HUD's
centre line then carries only vanilla's own PvP notice (`SetPVP` writes it), never the entry text. The check waits up to
12 s for the banner (the mod holds it while vanilla's own banner is up, `BannerPlan.MaxWait` 8 s) and logs when it came
(`[E2E] INFO P03b-banner-enter evidence ...`). **Leaving is still a centre message**, so P03m and P04i keep their form.
P05d also asserts that the slot holds no banner naming the rectangle.

The combine phase adds two territories named `TS Comb A` / `TS Comb B` (not `E2E ...`, so H03, S10-S20 and R01 keep
selecting the three scenario territories): two circles r=8, 13 m apart (3 m overlap). A protects (access, build),
announces ("Welcome to {name}") and is on the map; B protects nothing and has other words, so a visitor refused on B's
old ground afterwards can only be refused by A's rules. TSAdmin creates and combines them at the end of its phase 2,
TSPlain checks at the end of its phase 2, the server checks the store and the file, and the restart reloads it.

| ID | Proves |
|:--|:--|
| A20a-comb-create | both created through `terr_place` exactly as drafted (shape, centre, size, flags, entry text, one part, revision 1) |
| A20b-combine | `terr_combine_at x= z=` inside A only arms (nothing changes, "stays" line); inside B within 15 s joins B into A: the admin's index shows A with 2 parts (A's ground, then B's), B gone, A's name, rules and entry text unchanged, revision +1 |
| P20a-comb-snapshot | the plain client's index shows the ONE combined record with the same 2 parts, B gone, and it governs a point in each part |
| P20b-comb-access | as a visitor `PrivateArea.CheckAccess` says NO (access and build) at a point only in the second part, with A's own refusal message; NO in the first part; YES 4 m beyond (control) |
| P20c-comb-presence | `Presence.Current` is the combined territory (2 parts) in the first part and in the second part |
| P20d-comb-seam-quiet | walking across the seam in 1 m steps: no centre message (leave or enter), no new banner of ours, `Presence.Current` never leaves the territory; controls: the entry banner showed on the way in (title, subtitle) and "Leaving TS Comb A" showed on the way out |
| P20e-comb-minimap | `Minimap.Update` does not run headless, so vanilla's `UpdateBiome(Player.m_localPlayer)` is called directly: `m_biomeNameSmall` contains the territory's name inside (second part) and does not outside |
| S30-combined-store | the SERVER's store holds A with the 2 parts the admin saw, B is gone, one record named `TS Comb...` |
| S31-parts-file | the JSON file (parsed independently of the mod's reader) has a `"parts"` array for A: the part beyond the first, equal to the store's, and no record for B |
| R30-reload-parts | after the restart the store holds A with its 2 parts and revision, and B is still gone |

**Rig settings this needs.** The client's mod config gets `BannerCooldownSeconds = 0` (`run-e2e.sh`, harmless on 0.1.1):
the fixtures' control walks leave the circle and come back within seconds, and with the default 90 s the mod (by design,
`BannerPlan` COOLDOWN, covered by its own harness) says the second entry and the second goodbye silently, so P03m could not
see its "Leaving" line (the first 0.2.0 run with the unchanged rig: P03b, P04d, P07b failed on the banner form and P03m on
exactly this; with the cooldown off P03m passes). Everything else stays at the mod's defaults. `E2E_CLIENT_CFG_EXTRA`
(environment, newline-separated `key = value` lines) adds more lines to the client's `[2 - Client]` section for mutation
runs, e.g. `MinimapTerritoryName = false` must turn P20e red.

### The 0.3.0 phase (the runic barrier)

Gated by reflection like the 0.2.0 phase: it runs when `TerritoryFlags` has a member named `RunicBarrier` (`Features.Barrier`).
On a 0.2.0 or 0.1.1 DLL the admin logs ONE line, `[E2E] INFO 0.3.0 phase skipped: ...`, and nothing else changes; run-e2e.sh's H12
knows from the DLL's own "loaded." line which ids a 0.3.0 DLL must have reported (134 in all, 114 on 0.2.0, 104 on 0.1.1), so a
0.3.0 DLL whose phase silently skipped FAILS. The barrier's own members (`BarrierFlare.Requested`, `Territory.Ground`) are read by
reflection, so the driver still compiles against every version.

Three circles of r=10 named `TS Barrier`, `TS Barrier Guard`, `TS No Barrier` (not `E2E ...`, so H03, S10-S20 and R01 keep their
meaning), 120 degrees apart on a ring 46 m around the spawn, marked by TSAdmin at the end of its phase 2 through the draft and
`terr_place`: **barrier** = NoMonsterSpawns + RunicBarrier, **guard** = the same + BarrierKeepsOutVisitors, **control** =
NoMonsterSpawns only. TSPlain (a visitor, a member of none) checks at the end of its phase 2. Solo mode then plays **two more
logins**, admin phase 3 (adds TSPlain as a member of the guard) and plain phase 3 (walks in); `plain.final-done`, which the server
waits for, moves to the end of plain phase 3.

Positions are read every frame (creatures) or after every step (the player) and compared with the circle: the mod's own
`TerritoryGround.DistanceOutside` is logged next to the driver's own maths (`radius - distance to the centre`). "Outside" means at
most 0.1 m inside; a hold may show at most 0.5 m inside, ever (`Scenario030.InsideEps`, `HoldSlack`, `PushOutSeconds` = 2.5 s for
"about 2 s"). A drive that never reaches the edge FAILS (the hold would be untested), and so does a control that does not work.

| ID | Proves |
|:--|:--|
| A30a / A30b / A30c | the barrier, the guard and the control were created through the draft and `terr_place` and came back exactly as drafted (shape, centre, flags, revision 1, creator) |
| A31-guard-member | the admin's edit tool, the panel's Add member and Save changes made TSPlain a member of the guard and kept its barrier flags |
| P30a-hold | a hostile Greyling **hunting** TSPlain (`BaseAI.SetHuntPlayer`: its own MonsterAI walks it to the player standing at the centre, the way a raid monster walks) is held outside the barrier: never more than 0.5 m inside, ends outside, and came within 3 m of the edge. Its control: the same hunt into the plain no-monster territory gets it at least 4 m inside. If the AI cannot move the creature at all (no path headless), both hunts fall back to `BaseAI.MoveTowards` every frame with the AI off, and every evidence line says which drive ran |
| P30b-pushout | a Greyling put 3 m inside the barrier is outside within 2.5 s and stays out for the rest of 7 s |
| P30c-flare | `BarrierFlare.Requested` (accepted flares, counted headless) rose by at least 1 during the barrier hunt and by 0 during the control hunt |
| P30d-pass-tamed | a tamed creature (`Character.SetTamed`, `IsTamed`) 2 m from the centre stays inside for 6 s. P30d2-pass-passive: the same with a Deer (the player goes 30 m away so it does not flee out); SOFT when the fixture does not work or the deer wandered out, never FAIL |
| P30e-visitor-held | TSPlain, a non-member, pressing at the guard in 0.3 m Rigidbody steps every 0.1 s for up to 7 s stays outside (never more than 0.5 m inside) after coming within 3 m of the edge |
| P30g-visitor-flare | `BarrierFlare.Requested` rose during that press |
| P30f-visitor-pushout | TSPlain placed at the guard's centre is outside within 2.5 s and stays out |
| P30e2-open-barrier-free | the same walk into the plain barrier (no visitor option) gets TSPlain at least 4 m inside, and it stays inside |
| P30e3-member-passes | after the admin's member add, the same walk into the guard gets TSPlain inside (phase 3) |
| S31a-barrier-store | the server's store holds the three with exactly the flags marked (names written out by hand), `RunicBarrier` = 1<<11, `BarrierKeepsOutVisitors` = 1<<12, TSPlain a member of the guard, and no territory in the store breaks the prerequisites (RunicBarrier needs NoMonsterSpawns, BarrierKeepsOutVisitors needs RunicBarrier) |
| S31b-barrier-file | the file, parsed independently, says `"version": 3` and lists the flags by name (`RunicBarrier`, `BarrierKeepsOutVisitors`) on the right records, and TSPlain as the guard's member |
| R31-reload-barrier | after the restart the store holds the three with the same flags, members and revision |
| A00c-join / P00c-join | the two extra logins of solo mode |
| H08-no-barrier-errors | (rig-level, 0.3.0 only) no "Runic barrier ... failed" line, no "Barrier flare tick/flare/rune failed" line, no "Barrier flare: could not build" line at ANY level, and no ERROR from the flare, in any BepInEx log (the mod logs them once per 30 s). 0.3.0's flare threw building its texture, logged one WARNING and switched itself off for the session; the old H08 did not look for that line, and this one would have failed the run |
| P30h-flare-drawn | (`--graphics` only) `BarrierFlare.Drawn` rose by at least 1 during the barrier hunt (P30a) and during the visitor's press (P30e), and by 0 in the control: the flares the pushes asked for were put on screen, not only counted |
| P30i-flare-pixels | (`--graphics` only) a flare stood 6 m in front of a camera of the driver's own, parked at 9000 m over a black backdrop, lights at least 1 % of the frame in blue, has no magenta-pink pixel (a shader the game could not compile), and is gone (under 0.2 % of the pixels) 1.9 s later. Every shot is rendered twice in the same instant, flare objects on and off, so only the flare is in the difference. Frames kept in `shots/world-*.png` |
| P30j-flare-in-game-view | (`--graphics` only) the same, in front of the game's own camera (`Main Camera`, post-processing and all), over the real forest and the real player. Frames in `shots/world-game-*.png` |
| H13-flare-shader | (rig-level, `--graphics`, 0.3.0 only) the client logged "Barrier flare: drawing with shader X" (one line, once) |
| F01-flare-pixels / F02-flare-retry | (`--spike a --graphics`, the main menu) F01 is P30i in the menu. F02 makes the next asset build fail (`BarrierFlare.InjectBuildFaults`, a test seam): that flare must draw nothing and log an ERROR with the exception, and the next flare must rebuild and draw. The ERROR line is what H08 exists to catch in a real run, so it is only read in the probe's log |

What the real 0.3.0 DLL did in these checks (runs `191318`, `191905`): the hunted Greyling stood at the edge (deepest 0.00 m) with 13
flares requested, and 4.9 m / 6.5 m inside in the control with none; a Greyling put inside was outside after 0.04 s, 0.75 m past the edge;
TSPlain pressing at the guard stood 0.1 to 0.2 m outside with 10 to 28 flares and one "The runes of TS Barrier Guard turn you back";
TSPlain put at the centre was outside after 0.04 s, 0.75 m out, with that message. `BarrierFlare.Drawn` is 0 headless (expected).
P30f also asserts that message (the mod says it on every eviction of the local player). The AI drive worked headless (no assist
fallback needed): pathfinding builds its navmesh under a headless client.

**Fixture rule learnt (P30d):** a hostile creature is evicted by the barrier in the first Update after it exists, so a creature that
must pass as TAMED is tamed in the very frame it is created (`SetTamed`, before any yield). The first run tamed it 3 frames later and
found it already put out: a rig fault, not a mod fault.

**Dry run.** `E2E_FORCE_030=1 ./run-e2e.sh <a DLL without the barrier>` plays the phase anyway (`TERR_E2E_FORCE030`, passed to the
server and the client): the barrier territories are then plain no-monster ones, so P30a, P30b, P30c, P30e, P30f, P30g, S31a, S31b and R31
**must go RED** while the control, the create checks and P30e2 stay green. It exists to prove the fixtures work headless and that the
checks can fail; never set it for a real run.

### The 0.4.0 phase (sub-territories, and the banner's PvP notch)

Gated by reflection (`Scenario040.Supported`: `Territory` has `ParentId`, `FollowFlags` and `Follow`, and `RequestOp` has `DeleteTree`); on an older DLL the
admin logs one line, `[E2E] INFO 0.4.0 sub-territory phase skipped: ...`, and nothing else changes, and every new member is reached by name so the
driver still compiles against a 0.3.x DLL. H12 expects 29 more ids from a 0.4.0 DLL (judged, as always, from its own "loaded." line).

The server driver also LOWERS `Protocol.PartBytes` to 512 (`Scenario040.SmallParts`, reflection, only if the DLL has `SnapshotParts`): a territory list over 512 bytes goes to each player in pieces on the same ZRpc,
so EVERY phase of the run - the older ones included - is carried over the 0.4.0 piece transfer, and a 0.4.0 run is the stress test of it. (The real piece size is 64 KiB: no real world's list needs more than one.)

The land's site is chosen by `Layout.PlaceClear` from the client's own territory list: dry, and at least 30 m of clearance beyond the land's 22 m from the ground of EVERY territory the older phases made (a territory is bigger than its
centre, which is all the older `PlaceDoors` looks at; the first graphics run put the land inside 'TS Doors Nested' and the server rightly picked that one as the town's parent).

Every older phase marks its territories as TOP LEVEL (`Scenario040.TopLevel` in `ScenarioAdmin.SetDraft`): in 0.4.0 the panel's draft starts with its parent
on AUTOMATIC and every setting following, which is what a player wants for a town in a continent and what these phases must not get.

TSAdmin, at the end of phase 2, marks `TS Sub Land` (r=14: build and access protection, no raids, a banner, on the map, PvP forced ON, entry text "Land of {name}")
as a top-level territory through the draft and `terr_place`, then `TS Sub Town` (r=4, six metres along x, inside it) with the draft's parent left on
AUTOMATIC and every setting following except two the town sets itself: building off and PvP forced OFF. Then it edits the land (harvest protection on, entry
text "Realm of {name}") and watches the town follow. In phase 3 the town follows building again, the land is deleted (the town keeps every rule and moves to the
top level), a second pair is deleted in ONE request (`DeleteTree`), and a third land with a town inside it is combined into a small territory (the town keeps its
rules and moves inside the small one); `DeleteTree` on the small one cleans up. TSPlain, between the two, checks its own snapshot, the ward funnel and the banners.

| ID | Proves |
|:--|:--|
| A40a-create-land / A40a2-land-top-level | the land was marked through the draft as exactly what a 0.3.x draft makes: no parent, no marks |
| A40b-create-town | the server picked the land as the town's parent from the middle of the ground (AUTOMATIC), kept the marks the draft sent, and sent the town back with the land's values filled in: the land's rules less building, PvP forced off (its own), the land's entry text and colour; revision 1 |
| A40c-edit-land | the land's Update (harvest protection, new entry text) reaches the town through the snapshot while the town's own record stays at revision 1 and keeps building off and PvP forced off |
| A40d-follow-again | the town's mark for building put back: it protects building as the land does, its PvP is still its own, revision up by one |
| A40e-delete-land | deleting the land leaves the town top level with every rule, PvP, text and colour it had written into it, following nothing, revision up by one; the reply says what happened to it |
| A40f-pair-land / A40f-pair-town / A40f-pair, A40g-delete-tree | a second pair marked the same way, then deleted by ONE `DeleteTree` request: both gone |
| A40h-land3 / A40h-town3 / A40h-land4, A40h-combine, A40h-cleanup | a land with a town combined into a small territory: the land is gone, the small one has one more part, the town keeps every rule, sits inside the small one and follows nothing, revision up by one; then `DeleteTree` removes both |
| P40a-snapshot | TSPlain's snapshot has the town inside the land with the marks and effective values the admin saw |
| P40b-enforced | the ward funnel (`PrivateArea.CheckAccess`) as a visitor: in the town doors are refused (followed from the land) but building is allowed (its own); in the land beside it both are refused |
| P40c-pvp-banner | PvP is forced off in the town and on in the land beside it, and (Epic banner) each banner carries its notch (green, red) with the town showing the land's entry text filled in with its own name |
| S40a-sub-store | the server's STORED records: the land top level, the town holding only what it sets (building off, PvP forced off) and nothing it follows, the server's published (effective) record = the land's less building, and the contract numbers (TerritoryFollow bits 1..64, AutoParent -1, record 3, file 5, DeleteTree 11) |
| S40b-sub-file | the file, parsed independently of the mod's reader, writes the town with `"parent"`, `"flags": { "BuildProtect": false }`, `"pvp"` and none of the keys it follows; version 5 or more |
| S40c-sub-baked | after the land's deletion the town is a TOP-LEVEL territory in the store and the file with the rules it followed written into it; no land of any pair is left |
| R40-reload-sub | the restarted server loads that town back as the same top-level territory (rules, PvP, text, colour, revision) |
| H15-sub-log | (rig-level) the server's own log: the town created inside the land, the land deleted with its sub-territory kept and moved up, a land with a town combined into another, and two lands deleted with the town inside each in one request |
| S41a-parts-sent | with the piece size at 512 bytes the server sent at least one territory list in pieces (`TerritoryServer.PartedSends`, read by reflection) |
| P41a-parts-received | TSPlain put at least one list back together from pieces (`TerritoryClient.PartedSnapshots`), and the land and the town they carried are in its index |
| H16-parts-log | (rig-level) the server's own log says once that the list goes out in pieces of at most 512 bytes |
| P03q-banner-notch / P04k-banner-notch / P20f-banner-no-notch | the Epic banner's PvP notch: green for the forced-off sanctuary, red for the forced-on war zone (with its placement on the lower braid), none for a territory that leaves PvP to the player |

**Looking at the panel: the panel booth.** A batchmode client never calls the panel's `OnGUI` at all (counted: no Layout, no Repaint), so no full run can draw it, and a screen read of a batchmode client comes back black. A WINDOWED client on the private Xvfb does: `E2E_WINDOWED=1 E2E_BOOTH=1 E2E_SCREEN_W=1024 E2E_SCREEN_H=768 tests/e2e/run-e2e.sh <dll> --spike a --graphics` reaches the main menu, then `Scenario040.PanelBooth` puts a made-up world into the territory index as a client does it (a continent with an owner and three members; a town in it that sets only its priority, colour and welcome; a market in the town that sets only that building is off and PvP is forced off; a plain territory), skips the panel's and the client's own ticks (they would close a panel that has no player, and clear a list that has no ZNet), opens the panel by hand and photographs it with a screen read at the end of the frame: the Rules tab of the market (a sub-territory: the Inside row, the follows / own column) at four scroll positions and with the Inside picker open, its Members tab, the continent's Rules tab (no column; the foot with the two Delete rows), a fresh draft (parent Automatic), and the market's Rules at text scales 0.7, 1.5 and 2.0. The Here, All and Place tabs need a player and stay blank. The check F03-panel-drew says the panel was repainted and pictures were written; the run script prints how many `panel draw tick failed` errors the client logged (0 on 2026-10-11). Nothing is judged from the pictures; they are for the eye (`shots/panel-*.png`). Do not run a FULL run windowed (`E2E_WINDOWED=1` without `--spike a`): the game behaves differently in a real window (the inventory's PvP toggle, and the run stalls partway).

### The 0.3.1 phase (auto-close doors)

Gated by reflection: it runs when `TerritoryFlags` has a member named `AutoCloseDoors` (`Features.Doors`); on a 0.3.0 or older DLL the
admin logs one line, `[E2E] INFO 0.3.1 door phase skipped: ...`, and nothing else changes. H12 expects 13 more ids from a 0.3.1 DLL
(judged, as always, from its own "loaded." line).

The mod's rule (`Rules/DoorCloser.cs`) runs on the **server** alone: it scans the sectors around every connected player, and a door
that stands in a territory with the flag (the GOVERNING territory only: the smaller one wins), that has been open for
`AutoCloseDelay`, with no player within `AutoClosePlayerClearance` (3 m), gets its ZDO state written to 0 by the server, which takes
the door over for 0.75 s (`Rules/OwnershipHold.cs`) so that its owning client cannot win the race. Every client's `Door` re-reads the
state five times a second and closes. So the rig lays its doors as ZDOs OWNED BY THE CLIENT, exactly what a player's door is.
run-e2e.sh writes `AutoCloseDelay = 3` into the server's config (in place, or inserted under `[1 - Server]`; the shipped default is
10 s) and tells the driver (`TERR_E2E_DOORDELAY`); S32a checks that the server really runs with it.

Three circles named `TS Doors` (r=12, `AutoCloseDoors` + `NoRaids`), `TS Doors Nested` (r=4, inside it, `NoRaids`) and `TS Doors Off`
(r=8, 38 m away, `NoRaids`), placed on a ring around the spawn clear of everything else and marked by TSAdmin at the end of its phase 2.
TSPlain lays its doors, stands 2 m from one of them, opens all of them in the same frame through the door's own RPC (`UseDoor`), and
watches the client's ZDO copy (`ZDOVars.s_state`) and the animator's `state` every frame until delay + 4 s, then walks 8 m away
and watches 4 s more.

| ID | Proves |
|:--|:--|
| A32a / A32b / A32c | the three territories were created through the draft and `terr_place`, exactly as drafted |
| P31a-door-closes | a `wood_door` left open inside `TS Doors` with nobody within 3 m is closed by the server: ZDO state 0 and animator state 0, after the delay (not before: closing sooner means something else did it) and within delay + 4 s |
| P31b-door-outside-stays | the same door outside every territory stays open for the whole window (a control that never opened FAILS: the door is known to open headless) |
| P31c-door-flag-off-stays | a door inside `TS Doors Off`, a territory without the flag, stays open |
| P31d-door-excluded-stays | a `wood_window` (and a drawbridge, if the world has one) inside `TS Doors` stays open: both are excluded by default. SOFT when there is no such prefab or it cannot be opened headless |
| P31e-door-nested-stays | a door inside `TS Doors Nested`, an unflagged territory nested in the flagged one, stays open: the smaller territory governs |
| P31f-door-player-near | a door with the player 2 m away stays open past delay + 4 s, and closes within 4 s of the player walking 8 m away |
| S32a-doors-store | the server's store holds the three with exactly the marked flags (names written out by hand), only `TS Doors` has `AutoCloseDoors`, `AutoCloseDoors` = 1<<13, and the server's own `AutoCloseDelay` is what the rig wrote |
| S32b-doors-file | the file, parsed independently, lists `AutoCloseDoors` by name on `TS Doors` and on no other record |
| R32-reload-doors | after the restart the store holds the same flags and revisions |
| H14-door-log | (rig-level) the server's own log says it auto-closed two doors in `TS Doors` ("auto-closed wood_door at (...) in 'TS Doors' after Ns open", VerboseLogging) and none in the nested or the unflagged territory: the server's record, independent of what the client saw |

**Dry run.** `E2E_FORCE_031=1 ./run-e2e.sh <a DLL without AutoCloseDoors>` plays the phase anyway: the door that must close stays open,
so P31a and P31f (and S32a, S32b, R32, H14) must go RED while the four negative controls stay green.

**P30c hardening.** `BarrierFlare.Requested` is global: it counts a wild Greydwarf leaning on a barrier as well as the rig's own
Greyling (run `205053` saw 2 flares in the control hunt, most likely that). Now, while a hunt runs, every hostile creature that is not
the rig's and stands within 8 m of a barrier's edge is noted. A control hunt that saw flares while one was there is measured once more; if
it is still contaminated the control half of P30c is reported as not measurable in the check's text (never as a silent pass), and the
barrier half (at least one flare) is unchanged. A flare in a control hunt with no wild creature at any barrier still FAILS.

## Why one client plays both characters (solo is the default): the spikes

The rig was built one feasibility step at a time (`--spike`), on this machine and its one Steam account. Run dirs
of 2026-09-29 under `$E2E_WORK/e2e-runs/`:

| Spike | Result |
|:--|:--|
| (a) a client reaches the main menu headless | With no display at all Unity 6 picks window backend "(null)" and the client dies with signal 11 (SIGSEGV) loading the first scene (`135938`, `140012`). Without `-nographics` it dies in Vulkan init (`140115`); with `-force-glcore` as well (the `--graphics` option now) it reaches the menu with OpenGL 4.6 on llvmpipe (`201432`). What works (`140232`): `-batchmode -nographics` with `DISPLAY` pointing at an Xvfb the rig starts for that run only (`-nolisten tcp`, no screen attached, stopped in the teardown). Window backend x11, nothing on Rohan's desktop. There is no visible or windowed fallback. |
| (b) one client creates a throwaway character, joins the run's server and stays | passes (`140330`) |
| (c) two clients on the same Steam account at once | the second is refused (`140514`). `ZNet.RPC_PeerInfo` checks each Steam client's session ticket (`ZSteamMatchmaking.VerifySessionTicket`, i.e. Steam's `BeginAuthSession`), and it fails for the second connection of one Steam id while the first is still connected. The server logs "Peer 76561198224105156 has invalid session ticket" and the client gets `ErrorBanned` (error 8). |

So **`--mode dual` needs a second Steam account**, logged in to a second Steam client (another machine or OS
user) that runs the second game client. This machine has one. **Solo** is the default: one client plays TSAdmin,
logs out, plays TSPlain, and so on (four joins per run, about two and a half minutes of scenario). Solo mode differs
from dual mode in three ways:
- A02-roster (the admin's online roster lists TSPlain) runs only in dual mode. In solo mode TSPlain is offline while
  TSAdmin edits, so A08 adds it through "Add by id", which the panel offers for exactly that case.
- S24 expects 1 player at a time.
- TSPlain is a non-admin only through the rig's one test seam (`ServerRole.IsAdminSeam`): `adminlist.txt` lists
  Steam ids, and both characters share one. Dual mode would need the seam too, unless the second account is
  simply left out of the list.

## Chests: what "opened" means headless (P03j, P07e)

`Container.Interact` returns `true` whether it opens the chest or refuses it, so the rig checks what vanilla
changes when a chest opens. A chest this client spawned is owned by it, so vanilla's open handshake runs inside
`Interact`, because `ZRoutedRpc` handles an RPC to its own peer at once:
1. `RPC_RequestOpen` runs on the owner.
2. `RPC_OpenResponse(granted)` comes back.
3. `InventoryGui.Show(chest)` is called, and the inventory holds the chest (`m_currentContainer`).
4. `InventoryGui.UpdateContainer` marks it in use (`Container.IsInUse`, and the ZDO's `inUse`, which opens the lid
   for every player).

`ChestOpen` checks steps 1 to 3 the moment `Interact` returns and step 4 three frames later, through observer-only
prefixes that never change an argument or skip anything. A chest counts as opened only if all of them happened. A
ward refusal returns before step 1, so none of them happen, and P03j requires exactly that: not one handshake step,
`Show`, hold or in-use signal for the chest in the second after `Interact` (a chest that half-opened is not a
refusal), with the player standing within the auto-close distance as for the control. The refusal still needs its
control: a chest in open country must open by all four steps, or P03j FAILS (it used to go SOFT, which let a run
stay green with the protection untested; the control has opened in every run since `140926`). P07e (the member's
chest) passes only by all four steps, whatever the control did. Every run logs the evidence of both chests
(`[E2E] INFO P03j-chest-refused evidence - ...`), PASS or not.

The first version read only `InventoryGui.m_currentContainer`, once, 1 s after `Interact`, and in run `140926` it
found nothing for either control chest (SOFT twice). The chest had opened: the log has vanilla's "Player … wants to
open piece_chest_wood(Clone)" from `RPC_RequestOpen`, with no "in use" or "not yours" line after it, then the "Setting
selected recipe -1" that `InventoryGui.Show` prints through `SetupCrafting`. So something closed the inventory within
that second, and the first version kept no record of what did. It has not happened since. In every run after it the
control opened by every signal; the runs that log the evidence (`144458` with `--bc`, `144843`, `145214`, `145525` with `--bc`)
show the inventory still holding it after 1 s, with the player 1.8 m from the chest
(`InventoryGui.m_autoCloseDistance` is 4 m). Every `InventoryGui.Hide` and `CloseContainer` that
closes something is now recorded with its caller and the player's distance, so if a close happens again the
evidence line names who closed it.

## Map outlines stay SOFT (P05h)

`Minimap.Update` returns before `UpdateMap` when `SystemInfo.graphicsDeviceType` is `Null`, which is always the
case with `-nographics`. The mod draws its outlines from a `Minimap.UpdateMap` postfix (`MinimapUpdateMapPatch`), so
headless they are never built. A `--graphics` client (OpenGL on the rig's Xvfb, spike a) does draw the minimap, and then P05h passes. Calling
the mod's overlay code from the rig would test the rig's call, not the game's. P05h stays SOFT in the headless rig; the `--graphics`
run does have a device (see the option table) and there the check passes.

## Waiting for a run from another shell (the pgrep pitfall)

`until ! pgrep -f run-e2e.sh; do sleep 10; done` **never ends**. `pgrep -f` matches whole command lines, and the
waiting shell's own command line contains `run-e2e.sh`, so it always finds itself. Do one of these instead:
- wait on the rig's PID: `./run-e2e.sh … & P=$!; while kill -0 $P 2>/dev/null; do sleep 10; done`;
- use a pattern that cannot match itself: `pgrep -f '[r]un-e2e.sh'`;
- run the rig in the foreground with a long enough timeout (a whole run takes about 3-4 minutes, `--bc` included).

Only one run at a time: a second run exits **3** while another holds `$E2E_WORK/e2e-rig.lock`. Wait and retry.

## Checks whose meaning changed (2026-09-29, second pass)

| ID | Before | Now |
|:--|:--|:--|
| P03e-sanctuary | FAILED while the effect was plainly on the player. It looked for an `SE_Stats` whose `.name` `StartsWith("SE_Territor")`, the English spelling. The mod's effect is `SE_TerroritySanctuary` (the mod's own spelling), so the prefix could never match; the in-game diagnosis showed `StartsWith` false with both culture and ordinal comparison while the name hash matched. **A rig bug, not a mod bug.** | Looks the effect up the way SEMan does, by the name hash of the mod's own `Sanctuary.EffectName` (vanilla `GetStableHashCode`), and asserts exactly one copy, the `SE_Stats` type, the label and the four values. Each run also logs, as INFO, the first version's predicate part by part. |
| P03o-sanctuary-gone | Passed vacuously for the effect, because it used the same lookup, which could never find it. Only its carry part was real. | By name hash (`HaveStatusEffect`, `GetStatusEffect`) and by label, plus the carry weight. |
| P03f-sanctuary-bonuses | Health regen was the value inside minus the value at home. A night-time `Cold` effect (x0.5) was on the player both times, so it passed, but it would fail whenever `Cold`, `Wet` or `Rested` came or went in between. | Compared in the same frame with vanilla's fold over the other effects: equal to the fold with the admin's x2 at the effect's place, and above the fold without it. Carry and armour still count from home, since they are plain sums. |
| P03j / P07e (chests) | SOFT: read the inventory 1 s after `Interact`, and even the control chest "did not open". | PASS/FAIL on the handshake, `InventoryGui` and in-use signals above, with a working control. |
| P03p-sanctuary-hud | (new) | The HUD's status-effect row names the territory and shows the guard stone's icon. |

## Checks whose meaning changed (2026-09-29, third pass: independent verification)

An independent pass read every assertion for ways it could pass without the mod doing its job, then built
deliberately broken copies of 0.1.1 (mutants) to prove the rig fails them. What was tightened:

| ID | Weakness | Now |
|:--|:--|:--|
| P03j-chest-refused | Passed if ANY one of the five open signals was missing, while this README said "none of them": a chest that half-opened (say, auto-closed before the in-use read) counted as refused. | No handshake step, `Show`, hold or in-use signal at all, and the player within the auto-close distance, as for the control. |
| P03i / P03j / P03k / P05f (refusals) | A control that did not open or pick made the refusal SOFT, so the run stayed green with the protection untested. | FAIL: the controls are known to work headless. |
| P03i-door-refused | A door that could not be interacted with (`CanInteract` false, `Interact` returns false) also "stays shut". | The door must have been interactable and `Interact` must have returned true: the ward's refusal path. |
| P03k / P05f (harvest) | Vanilla's `Pickable.Interact` also returns false for an invalid or disabled pickable, which passed as a refusal. | The pickable inside must have been valid and unpicked. |
| P07d / P07e (member door and chest) | A member's door or chest that did not open went SOFT whenever the control also failed. | Pass or fail on their own. |
| A06d / P04a ("no damage") | `World.Health` answers -1 for an unreadable piece, and "-1 -> -1" passed as no damage. | The health must be readable before and after. |
| P03d / P04f / P03n (PvP toggle) | If the toggle did not reach the player in OPEN COUNTRY, the rig fell back to `SetPVP`; P03d/P04f went SOFT and P03n passed through `|| !_toggleWorks`. A broken toggle hid behind a SOFT and a PASS. | P03d/P04f FAIL in that case, and P03n always requires the toggle to be usable again. |
| P05a-rotation | Asked the MOD's `Shape.Contains` whether the unrotated-box probe lay in the rotated box, and skipped the "allowed there" half when the mod said yes. | The driver's own maths decides; both halves always run. |
| P05g-border | SOFT when the border was missing, although it is drawn headless in every run. | FAIL. |
| S10b / S10c / S12b | Silently not reported when the file was missing (only S10a failed). S20a/S20b and the NON-file checks S21-S24 were lost with them when `Final` threw on the missing file. | Each FAILS with the reason; S21-S24 always report. |
| S10a / S12a / S20a | Compared most fields; not the owner, member names, eitr regen, creator or creation time. A missing key threw inside the retry loop and FAILED with an empty reason. | Every persisted field; a missing key FAILS with its exception as the reason. |
| R01-reload | Compared the restarted store with the FILE (`server.expected` was copied from it), so a field the save drops came back "equal", and an empty file passed vacuously. | Compared with the first server's STORE (written by the driver), with the territory count, never vacuous. |
| (new) H12-results-complete | A check that never ran was invisible: the run passes on "no FAIL line". | DONE counters vs results lines, and the 103 solo ids. |
| (rig) memory | Under 6 GB MemAvailable during a run the rig only logged a warning, while the machine's watchdog SIGTERMs the biggest test server (possibly another project's) under 5 GB. | Under 7 GB the rig stops its own processes and exits 5. |

Kept as SOFT on purpose: P05h (no graphics device headless), P04g/P04h when VANILLA refuses the control spawn
(the mod only touches points inside territories, and a spawn check depends on the ground), P03p/P04g when there is no
`Hud`/`SpawnSystem` instance, and P09b when the fixture chest cannot be found again after the re-login.

## Rule 2 and the dedicated server (found 2026-09-29 15:31, third pass)

**Every rig run up to 15:31 wrote Rohan's `~/.config/unity3d/IronGate/Valheim/prefs`, and H90 passed anyway.**
The dedicated server is a Unity player with the client's identity (IronGate/Valheim), and the rig started it with
the real HOME (only the clients had a fake one), so Unity rewrote Rohan's `prefs` when the server started and when it
exited. The evidence: in every run with a server, the `prefs.copy` that safety.py saves at the baseline has an mtime
1-2 s after the rig's own "started server" line (14:03:31 -> 14:03:32, 14:09:26 -> 14:09:27, 14:52:15 -> 14:52:16,
... 15:28:03 -> 15:28:04), and in run `20260929-152801` the file was written at 15:31:13.3, the second the rig logged
"server-restart exited", while no other Valheim process started or stopped. H90 could not see it: its baseline was
taken after the server had booted, its check ran before the server stopped, and the restarted server ran after it.

What those writes changed (diffs of the saved copies; the file itself was never restored by the rig or by the
verifier, since that would be one more write to Rohan's data): Unity's own bookkeeping (`unity.player_session_count`,
643 at 13:17 and 663 at 15:28, and the three `unity_connect.*` / `unity.player_sessionid` session ids), and an EMPTY
`ConsoleBindings` key appearing and disappearing (Rohan has no console bindings: the value is always empty). No game
setting was changed by the rig: the other differences since the first-ever copy (13:17) are `profile` ('wubarrk
dev' -> 'ronnie redbeard') and `Screenmanager Resolution Use Native` (1 -> 0), written at 13:58:59, before the rig's
first server (14:03:31), by Rohan's own client (he played until about 14:05). A pristine copy from 13:17 is in
`$E2E_WORK/e2e-safety/first/prefs.copy` if Rohan wants it back.

Fixed: the server runs with its own fake home (`$RUN/home-server`, with the same `.steam` links as the clients'), the
baseline is taken before the first Valheim process of the run, and H91 checks again after the servers have stopped.
The rig logs where the server's own `prefs` went ("the servers' own Unity prefs went to their fake home: ...").

**Not the rig's to fix, but the same write:** any other dedicated server on this machine started with the real HOME
writes the same file when it starts and exits: `~/valheim-testbed/boot-check.sh` (the `terr` / `terr-bc` boot checks
in HANDOFF.md) and other projects' test harnesses. At 15:33:22 the NoodlesMcDoodles mirror harness's server exited and
wrote it, with its own stale copy (session count back to 659). The rig cannot stop those writes and cannot tell them
from its own, so it warns in the pre-flight when such a server is running, and H90/H91 then FAIL loudly if the file
changes during the run. Seen again at 16:35:13 on 2026-09-29: the
NoodlesMirror harness's server (pid 386779, real HOME, started 16:35:01) rewrote `prefs` in the gap between two rig runs (no
rig process was alive); the later runs saw it in their pre-flight WARNING and their own baselines were taken after it.

## Fourth pass (2026-09-29): the rig runs on 0.2.0

| Change | Why |
|:--|:--|
| Build: `--no-incremental -nr:false -p:UseSharedCompilation=false`, and the build runs with `9>&-` | The Roslyn compiler server the build started inherited fd 9, the run's `flock`, outlived the run and held the lock: the NEXT run refused with exit 3 ("another rig run holds the lock") for as long as the server lived (found 16:26, `VBCSCompiler` from the 0.1.1 baseline run). Now nothing lingers, and a full rebuild means a stale `obj/` from another DLL can never stand in for the one under test |
| `Features.cs`, `Scenario020.cs`, the 0.2.0 branches of P03b, P04d, P07b, P05d | 0.2.0's Epic entry banner and the combine phase (section "The 0.2.0 phase") |
| Client cfg `BannerCooldownSeconds = 0`; `E2E_CLIENT_CFG_EXTRA` | see "Rig settings this needs" |
| H12 | knows the 0.2.0 ids (114 in all) from the DLL's own "loaded." line; the 0.1.1 list is unchanged (104) |

First 0.2.0 run with the rig as it was (`20260929-162704`, 100 / 4 / 1): P03b, P04d and P07b failed because the entry text is
no longer a centre message (0.2.0's design, not a mod fault), and P03m because of the banner cooldown described above; no mod
finding. Every ID that passed on 0.1.1 passes on 0.2.0.

## Fifth pass (2026-09-29 evening): the rig runs on 0.3.0

| Change | Why |
|:--|:--|
| `Scenario030.cs`, `Features.cs`, `Layout.cs`, `ServerRole.cs`, `ClientRole.cs`, hooks in `ScenarioAdmin.cs` / `ScenarioPlain.cs`, H12 + H08 in `run-e2e.sh` | the 0.3.0 phase (section "The 0.3.0 phase"); on 0.2.0 and 0.1.1 it logs one INFO line and nothing else changes |
| `E2E_FORCE_030=1` | dry run of the phase on a DLL without the barrier. On the 0.2.0 DLL (`20260929-185217`) A30/A31, P30d, P30d2, P30e2 and P30e3 stayed green and P30a, P30b, P30c, P30e, P30f, P30g, S31a, S31b and R31 went red, as they must: the fixtures work headless (the hunted Greyling walked 20 m to the player in 8.7 s by its own AI) and the checks can fail |
| Client config overrides are set **in place** | BepInEx writes every entry into a profile's config the first time the mod runs there. The boot checks of 17:09 and 17:37 did that to `profiles/terr` and `terr-bc`, which then held `BannerCooldownSeconds = 90`; the rig's line `BannerCooldownSeconds = 0` was inserted ABOVE it, and the lower duplicate wins. P03m (the goodbye banner) failed in every run after those boot checks (`185217`, `185842`) with "Leaving ..." never shown: a rig fault, the cooldown of the mod working as designed. `set_client_cfg` now rewrites an existing key and inserts only a missing one (`E2E_CLIENT_CFG_EXTRA` too) |

