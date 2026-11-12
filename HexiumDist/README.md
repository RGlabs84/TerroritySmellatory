# TerroritySmellatory

**Admin-marked multiplayer territories for Valheim 1.0.** An admin sets a territory's rules in a small panel and marks the ground with Infinity Hammer, then combines territories into any shape they like. The server saves them and sends them to every player. Everyone's game then enforces them: build and chest protection for non-members, safe or war zones, no monster spawns with an optional runic barrier that holds monsters out, no raids, no skill loss, and sanctuary bonuses.

- **Players** need only this mod. They see territories on the map and on the ground, get an entry banner like a newly found biome, and get whatever the admin set.
- **Admins** also run [Infinity Hammer](https://valheim.thunderstore.io/package/JereKuusela/Infinity_Hammer/), and only to *mark* territories. Nothing a player experiences depends on it.

## What a territory can do

Building, door and harvest protection apply to **everyone who is not a member**. The owner and members are never stopped by them, and neither are server admins (configurable). **Buildings cannot be damaged** applies to everyone.

The **runic barrier** only exists while *No monster spawns* is on, and *Keep non-members out too* only while the barrier is. In the panel each is greyed while the one above it is off, and the server clears a barrier setting whose rule is off, so a hand-edited file cannot leave one switched on by itself.

| Rule | What it does |
|---|---|
| Building | Only members may place, remove, repair or dig. It works exactly like a ward: red ghost, "private zone". |
| Doors, chests & stations | Only members may use doors, chests, portals, signs, item/armour stands, fermenters, beehives and the rest of what a ward locks. |
| Harvesting | Only members may pick crops, berries and other pickables. Vanilla wards cannot do this. |
| Doors close by themselves | Off by default. A door or gate left open inside shuts after `AutoCloseDelay` (10 s), unless a player is within `AutoClosePlayerClearance` (3 m) of it, so nobody has a door shut in their face. Run by the server, so every player sees it close, and it applies whoever opened the door, members included. Drawbridges and window shutters are left alone, and so are doors that need a key. Where territories overlap, the one that governs the door's spot decides. If you press Use on a door in the moment the server is closing it, the press is dropped: press again. |
| Buildings cannot be damaged | No hit harms them: creatures, fire and every player, members and admins included. Take a piece down with the hammer instead. Weather and support still apply. |
| No monster spawns | No hostile world spawns, raid spawns, spawn points or nests. Deer, boars, birds and fish still spawn. |
| Runic barrier | Part of *No monster spawns*, and on by default in a new territory. Monsters cannot walk in: a hostile creature that reaches the edge is held outside it, and a blue rune shield flares where it pushes. Tamed creatures and peaceful animals pass; bosses are held too. A monster already inside is set down just outside. Arrows and spit still cross it. A combined territory has one barrier, around all its ground. |
| Keep non-members out too | With the barrier: players who are not members are held at the edge as well, with the same flare, and one already inside is set down outside. Members and the owner always pass, and so do admins while `AdminsBypassProtection` is on. Off by default. |
| No raids | A raid never picks a player standing inside. |
| No skill loss on death | Dying inside does not lower your skills. |
| PvP | *Players choose*, *Forced off* (a safe zone) or *Forced on* (a war zone). The inventory toggle locks while you are inside and your own choice returns when you leave. |
| Sanctuary effect | A status effect named after the territory, with health, stamina and eitr regen multipliers, bonus carry weight and bonus armour. |
| Entry / exit banner | Entering announces the territory the way the game announces a new biome: its name as the big title, your own text beneath it (`{name}` and `{owner}` filled in), and the discovery sound the first time. Leaving shows "Leaving Hrafnborg", or your own text. The game's PvP notice shows when the territory switches your PvP. |
| Show on the map | Its real outline and name on the large map and the minimap, a combined territory as one shape, **only over ground you have uncovered** (walked, or from a shared map): fog stays fog. While you are inside, its name replaces the biome name above the minimap, on one line. |
| Name only | With *Show on the map*: just its name on the maps (and above the minimap while you are inside), with no outline and no tint. For a continent or a region, which would otherwise wash the whole map in one colour. Off by default. |
| Show its border | Players near the edge see where the border runs on the ground. |

Where territories overlap, the **smaller one rules its own ground**, so a public market drawn inside a protected kingdom works without further setup. **Priority** lets the bigger one win when you want that instead. A territory marked inside another is its **sub-territory**: it starts as a copy of the big one's settings, follows them, and can differ in any setting. A continent with its towns inside it is the same thing on a bigger scale: see *Territories inside territories* below.

A banner never spams at an edge: you count as inside until you are 3 m past it, re-entering within 90 seconds is silent, and dying inside is not leaving.

## Players

- **Home** (or `terr` in the console) opens the panel. **Here** shows where you are, whose land it is and what applies to you. **All** lists every territory with its distance.
- `terr_whoami` prints your player id. An admin needs it to add you while you are offline.

## Admins: marking a territory

1. Open the panel (**Home**). On **Rules**, set the name, colour and rules. On **Members**, pick an owner and members from the players online, or add someone offline by player id.
2. Press **Place → Pick up the hammer to mark a new territory**, or choose **Territory** from the hammer's tool menu.
3. The ruler shows the area. Scroll to resize, use Infinity Hammer's shape key for circle, square or rectangle, rotate as you would a piece, and place.

The server checks that you are an admin (its own `adminlist.txt`, never your game's opinion), saves the territory and sends it to everyone. The answer appears in your console, top-left and on the panel.

**Fixing a bad placement.** Two more hammer tools come with it:

- **Territory remove**: place it inside a territory, then place it again on the same one within 6 seconds. The confirmation stops a stray click deleting one.
- **Territory edit**: place it inside a territory to open the panel on it. Change its rules and members and press **Save changes**, or **Move / reshape** to mark new ground with the hammer while keeping its rules.

**No hammer?** Use **Place → Mark a circle here**, or `terr_here <radius>`, for a circle centred on you.

### Combining territories: any shape

A territory can be made of several circles, squares and rectangles that count as **one** territory: an L-shaped keep, a road with a market at its end, a ring of towers. Its protections, PvP, Sanctuary and banner cover all of it, walking from one part into another is not leaving, and the map draws one outline around the whole shape.

- **Territory combine** (a hammer tool): place it inside the territory to keep, then within 15 seconds inside the one to join into it. Placing in the first one again cancels.
- Or in the panel: edit the territory to keep, and on **Place → Ground → Combine**, press **Join into this** beside the other one (click twice).
- Or `terr_combine <into id> <from id>`.

The territory you combine *into* keeps its name, rules and owner. The other's owner and members become its members, and the other's own name and rules are dropped.

The **Ground** section (**Place**, while editing a territory) lists its parts. **Move** marks new ground for one part with the hammer, **Split off** makes a part a territory of its own with the same rules, and **Remove** drops a part (click twice for both). **Add ground with the hammer** adds a part at each placement until you press **Cancel**; **Add a circle here** adds one around you.

Infinity Hammer's tools need devcommands on (Server Devcommands). The panel tells you if they are off.

### Territories inside territories: a continent with towns

Mark the big territory first (a kingdom, a continent), then the small ones inside it. A territory marked inside another becomes its **sub-territory**: it **starts as a copy of the big one's settings and keeps following them**, until you change a setting on the sub-territory itself. That one setting is then the sub-territory's own; everything you left alone still follows the big one, and when the big one changes, so does the sub-territory. Each sub-territory can differ from its parent in as many settings as you like, and go back to following any of them.

- **What follows:** every rule, PvP, priority, colour, the owner and members (so the continent's members are members of its towns until a town sets its own people), the Sanctuary numbers, and the entry and exit texts. A sub-territory's name, ground and history are always its own.
- **In the panel:** a new territory's **Inside** row (under its name) says *Automatic*: the territory it is marked in, or none if it is marked in open country. An existing one can be put inside another, or taken out, with **Change** on the same row. Every setting of a sub-territory is marked *follows* or *own* in a column at the right: changing a setting makes it own, clicking *own* makes it follow again, and **Follow all of ...** takes everything back (click twice). The owner and members follow too: the Members tab says whose people they are, and **Set its own list** copies the parent's and makes them the sub-territory's own.
- **A sub-territory governs its own ground over everything it sits inside**, whatever their sizes and priorities say. Sub-territories can have sub-territories (a continent, a region, a town, a district: up to eight levels).
- **Taking a territory away never changes how the ones inside it behave.** Delete a continent and each town keeps the rules it had and moves up a level; `terr_delete <id> tree`, or the panel's **Delete with everything inside**, deletes them with it.
- **Without a link, nothing changed:** a territory with no parent works exactly as in 0.3.x, so does every world made before, and two territories that merely overlap still follow "the smaller one governs its ground" - and then replace its surroundings' rules instead of following them.

What nesting does for the player and the admin:

- **Banners.** Walking into a town gives the town's banner and no goodbye to the continent. Walking out says goodbye to the town ("Leaving Hrafnborg") and does not announce the continent again, because you never left it. A continent you have not yet been told about this session, because you logged in inside a town, is announced the first time you step out.
- **The runic barrier** keeps things out of the ground its territory *governs*. A town inside a barrier territory is not covered by it: a visitor who logs in inside a public town, or arrives by portal, stays there instead of being set down at the continent's far edge, and an arena town inside a monster-free kingdom keeps its monsters. A thing in the town that walks out onto the barrier's ground is held at the town's edge. One found on that ground is taken out by the nearest way out of the *outer* edge, never into the town.
- **The minimap** shows the town's name inside the town and the continent's name everywhere else on the continent.
- **The map.** A continent's outline and tint would colour the whole map, so give it *Name only* (under *Show on the map*): its name on the large map and above the minimap, nothing else. A territory whose edges add up to more than 100 km gets no border on the ground either.
- **Size.** `[1 - Server] MaxRadius` goes up to 100 km, so a continent can be marked in the game from a few big parts. A territory read from the world's file may have parts up to 100 km each whatever `MaxRadius` says, and a file may hold more parts than `MaxParts` and more territories than `MaxTerritories`; those two only limit what is marked in the game.

**Setting a whole world's territories from a file.** The territory file is plain JSON, so a world-building tool can write it: a continent as one territory of a few big rectangles (its `"parts"`), and every town inside it as a territory of its own with all of its rectangles as parts and `"parent"` set to the continent. A town then needs only what makes it different: every key it leaves out follows the continent. Write the file to `BepInEx/config/TerroritySmellatory/worlds/<world name>.json` on the server before the world first loads, or replace it and run `terr_reload` on a running one (members and owners added in the game live in that file too, so replacing it replaces them). Every territory needs a unique positive `"id"`; an unknown key is ignored. For example:

```json
{
  "version": 5, "worldName": "Svartland", "worldUid": 0, "nextId": 4,
  "territories": [
    { "id": 1, "name": "Svartland", "shape": "Rectangle", "x": -28414, "z": 3591, "halfX": 21000, "halfZ": 9000, "yaw": 0,
      "parts": [ { "shape": "Rectangle", "x": -8076, "z": -42619, "halfX": 4000, "halfZ": 3000, "yaw": 0 } ],
      "color": "#3fa9ff", "pvp": "ForcedOff", "enterMessage": "You are in {name}.",
      "flags": [ "BuildProtect", "StructureProtect", "NoMonsterSpawns", "NoRaids", "Announce", "ShowOnMap", "MapNameOnly" ] },
    { "id": 2, "name": "Hrafnborg", "parent": 1, "shape": "Rectangle", "x": -27100, "z": 4200, "halfX": 220, "halfZ": 180, "yaw": 0,
      "parts": [ { "shape": "Rectangle", "x": -26900, "z": 4300, "halfX": 60, "halfZ": 90, "yaw": 0 } ],
      "color": "#c9b458", "enterMessage": "Welcome to {name}.", "flags": { "MapNameOnly": false, "ShowBorder": true } },
    { "id": 3, "name": "Ulfheim", "parent": "Svartland", "shape": "Circle", "x": -25000, "z": 1000, "halfX": 150,
      "pvp": "ForcedOn", "flags": { "BuildProtect": false, "MapNameOnly": false } }
  ]
}
```

Hrafnborg and Ulfheim are sub-territories of Svartland (`"parent"` is the id, or the name when that is unique in the file). Hrafnborg follows Svartland for everything it does not write: the same protections, PvP and owner, but its own colour, its own welcome, an outline on the map (*Name only* off) and a border on the ground. Ulfheim follows it too, apart from the three things it writes: it is a war zone with open building, drawn on the map. A key a sub-territory writes is its own **even if it matches the parent's value today**, so write only what should differ; the checker (below) points out the ones that do not.

How a sub-territory's keys read: **absent** means follow the parent. `"flags"` as a **list** of names sets *all* its rules (as a top-level territory's does); as an **object** (`{ "BuildProtect": false, "NoRaids": true }`) it sets just those and follows the rest. `owner` / `ownerName` / `members` are one group: write any of them and the territory has its own people (`"members": []` is its own, empty, list), and so are `healthRegen` ... `armorBonus` for the Sanctuary numbers. `pvp`, `priority`, `color`, `enterMessage` and `leaveMessage` are one setting each. A territory with no `"parent"` is read as always: a missing key takes its default. A `"parent"` that is not in the file, a territory inside its own sub-territory, or a chain more than eight levels deep makes the server refuse the file (it says which, and keeps the territories it had). The file's `"version"` is now 5; files of version 1 to 4 read exactly as before.

**Checking a file before the server does.** The mod's tests build a checker that reads a file with the real reader and the server's real rules, and names what the server would take silently (a misspelt key or rule, a missing size, ground cut at 100 km, a rule cleared because its prerequisite is off, a territory inside another with no `"parent"`, a sub-territory that writes what its parent already has): `dotnet tests/bin/Release/net8.0/TerritoryTests.dll --check <file> [--at x,z]... [--tree]`; `--at` names the territory that governs a point, `--tree` prints the nesting.

The rule names a file understands (any letter case) are `BuildProtect`, `AccessProtect`, `HarvestProtect`, `StructureProtect`, `NoMonsterSpawns`, `RunicBarrier`, `BarrierKeepsOutVisitors`, `NoRaids`, `NoSkillLoss`, `Sanctuary`, `AutoCloseDoors`, `Announce`, `ShowOnMap`, `MapNameOnly`, `ShowBorder`; `"pvp"` is `Unchanged`, `ForcedOff` or `ForcedOn`. A rule whose prerequisite is off is cleared when the file loads (the barrier needs *No monster spawns*, *Keep non-members out too* needs the barrier, *Name only* needs *Show on the map*) - after a sub-territory has taken what it follows, so a barrier it sets itself stands as soon as the territory has, or follows, a no-monster rule.

### Console

| Command | |
|---|---|
| `terr` | Open or close the panel. |
| `terr_list` | Every territory as a tree - each one under the territory it is inside, with what it sets itself - with ids, owners and rules. |
| `terr_here [radius]` | Mark a circle around you with the panel's rules. |
| `terr_delete <id> [tree]` | Delete a territory; the territories inside it keep their rules and move up a level, or with `tree` are deleted too. |
| `terr_parent <id> <parent id \| none> [follow]` | Put a territory inside another, move it to a different parent, or put it back at the top level. Moving never changes how it behaves: it keeps what it shows, and follows the new parent only if you add `follow`. |
| `terr_reload` | Make the server re-read its territory file after a hand edit. |
| `terr_whoami` | Your player id, and what the server says about you. |
| `terr_combine <into> <from>` | Join the second territory into the first. |
| `terr_place`, `terr_remove_at`, `terr_edit_at`, `terr_combine_at` | What the hammer tools run. |

## Server

Territories are saved per world in `BepInEx/config/TerroritySmellatory/worlds/<world>.json`, with a `.bak` of the previous version beside it. The file is plain, readable JSON; rules are listed by name (`"flags": ["BuildProtect", "NoRaids"]`), a combined territory lists its extra ground under `"parts"`, and a barrier is the names `"RunicBarrier"` and `"BarrierKeepsOutVisitors"` in the same list. You can edit it by hand and run `terr_reload`.

If the file cannot be read, the server says exactly where it is broken (line and column) and **refuses to save** until it is fixed, while the territories it already had stay in force. A typo never turns into lost territories. A file written by a newer version of the mod is enforced but never saved over. 0.3.2 writes file version 4, so a server rolled back to 0.3.1 or earlier keeps the file safe the same way and will not save over it.

### Configuration (`wubarrk.terroritysmellatory.cfg`)

| Setting | Default | |
|---|---|---|
| `[1 - Server] RequireClientMod` | true | Kick players without the mod. Protections run in each player's game, so a player without it is not bound by them. |
| `[1 - Server] AdminsBypassProtection` | true | Admins are never stopped by territory protections. |
| `[1 - Server] MaxRadius` | 600 | Largest radius or half-width a placement may have, in metres (5 to 100,000: a continent can be marked in the game). A file may hold parts up to 100 km whatever this says. |
| `[1 - Server] MaxTerritories` | 500 | Per world. |
| `[1 - Server] MaxParts` | 16 | The most parts one territory may have. 1 turns combining off. |
| `[1 - Server] AutoCloseDelay` | 10 | Seconds a door may stay open before a territory with *Doors close by themselves* closes it (1 to 600). |
| `[1 - Server] AutoClosePlayerClearance` | 3 | A door is not closed while a player is within this many metres of it; the delay keeps counting and it closes when they step away (0 to 30, 0 turns the check off). |
| `[1 - Server] AutoCloseExcludedPrefabs` | `piece_drawbridge,piece_drawbridge_log,Morkhalla_Drawbridge,wood_window` | Door prefab names that are never closed by themselves. Add a modded door here to leave it open. |
| `[2 - Client] PanelKey` | Home | |
| `[2 - Client] ShowBorders` | Always | Off, PanelOnly, PanelOrBuilding or Always (near an edge). |
| `[2 - Client] BorderRange` | 60 | How close to an edge before its border shows. |
| `[2 - Client] ShowOnMap` / `EnterLeaveBanners` | true | |
| `[2 - Client] HideUnexploredTerritories` | true | A territory is drawn on the map, and its name shown, only over ground you have uncovered - by walking it or from another player's shared map. Off draws territories over the whole map, fog included. |
| `[2 - Client] EntryBannerStyle` | Epic | Epic (the big title, like a new biome) or Simple (a line in the centre of the screen). |
| `[2 - Client] EntrySound` | FirstEntry | The discovery sound with the banner: FirstEntry (the first time each session), EveryEntry or Off. |
| `[2 - Client] BannerCooldownSeconds` | 90 | Re-entering a territory within this time shows no banner. |
| `[2 - Client] MinimapTerritoryName` | true | The territory's name replaces the biome name above the minimap while you are inside. It stays on one line: shrunk to fit the label, and cut short with dots when even the smallest size is too wide. |
| `[2 - Client] BarrierEffects` | true | Show a runic barrier's blue flare, with its sound, where something pushes against it. The barrier holds monsters out either way. |
| `[3 - Infinity Hammer] WriteHammerTool` | true | Keep `infinity_tools_terroritysmellatory.yaml` for the hammer's tool menu. Delete its first line to take it over yourself. |
| `[3 - Infinity Hammer] InitialRadius` | 20 | The Territory tool's starting size. |

`[1 - Server]` settings are locked to the server's values.

## Compatibility

- **Valheim 1.0**, BepInEx 5.4.23+. No Jotunn. Install on the server and on every client.
- **ValheimModding-JsonDotNET** (Newtonsoft), the usual JSON dependency. Mod managers install it automatically; the server uses it for the territory file.
- **Better Continents**: compatible. Nothing here assumes a world size, heights come from the game's own (Better Continents-patched) terrain, and the network sync waits until a joining player is fully in the world, well after Better Continents' own download.
- **Infinity Hammer 1.84+** (admins only), with Server Devcommands and World Edit Commands as IH requires. IH is a soft dependency: without it everything else still works.
- **Servers with RPC whitelists** (such as Wonderland): regular players only ever *receive* this mod's traffic, and only admins send requests.

## Credits

By Wubarrk. Inspired by HanonHeim's *BaseTerritory*.
