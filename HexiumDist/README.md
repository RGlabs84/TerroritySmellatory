<!-- README schema v2 -->
# 🏰 TerroritySmellatory

![TERRORITYSMELLATORY](https://img.shields.io/badge/TERRORITYSMELLATORY-MARK_THE_GROUND%2C_SET_THE_RULES-c9a227?style=for-the-badge&labelColor=1b1b1b)
![VERSION](https://img.shields.io/badge/VERSION-0.5.0-8b1e1e?style=for-the-badge&labelColor=1b1b1b)
![SERVER AND CLIENT](https://img.shields.io/badge/SERVER_%2B_CLIENT-INSTALL_ON_BOTH-1f4e79?style=for-the-badge&labelColor=1b1b1b)

> *The jarl drove his spear into the turf and said, "From the river to the ridge, mine." His neighbour laughed and built a hut on it. The trolls laughed and walked through it. Then the runes went up along the line, the guards took their posts, and nobody laughed again.*

### Draw a line on the ground. Every player's game keeps it.

An admin sets a territory's rules in a small panel and marks its ground with Infinity Hammer. The server saves it and sends it to every player, and from then on each game enforces it: build and chest protection for non-members, safe zones and war zones, no monster spawns, a runic barrier that holds monsters out, posted guards, no raids, no skill loss, and sanctuary bonuses.

Players need only this mod: they see territories on the map and on the ground, and walking in announces the territory the way the game announces a new biome. Territories combine into any shape, and a continent can hold its towns, each following the continent's rules until it sets its own.

---

![NEW IN 0.5.0](https://img.shields.io/badge/NEW_IN_0.5.0-TERRITORY_GUARDS-8b1e1e?style=for-the-badge&labelColor=1b1b1b)

**Armoured guards hold your territory, and come when you shout for them.** Switch on **Guards** (panel → **Rules** → **World**) and the server posts guards in a ring round the middle of each part of the territory, facing out. They hold their post, fight any monster that comes into their sight, and walk back to their post when it is over.

<!-- screenshot slot (no public address yet): two guards with atgeirs posted at a territory's middle -->

| You | It |
|---|---|
| **Switch on Guards** | guards are posted in each part of the territory: two a part, eight at most, both settings on the server |
| **Type "guards" in chat** inside | if a monster that is after somebody is within 40 m of you, the nearest posted guard of the territory runs to fight it |
| **No guard close enough** | a guard appears beside you, fights, and leaves a little after the fight is over |
| **Read the top-left** | *A guard answers your call.* · *The guards see no threat.* · *No guards watch this place.* |
| **Press E on a guard** | talk to it |
| **Move or reshape the territory** | its guards move to the new ground |
| **Switch the rule off, or delete the territory** | its guards are gone |

⚔️ **Real guards**: armoured town guards, men and women, carrying the iron atgeir, built into the mod (nothing else to install) · 🛡️ **Guards cannot be hurt**, never attack players or player buildings, and drop nothing · 🔷 **They pass the runic barrier** · 🔇 **Off by default**: no territory you already have gains guards by updating · 🔁 **One call every few seconds** per player, kept by the server.

<details>
<summary>📖 <b>Read the saga</b> — how the call works, and how many guards you get</summary>

**The call is a word, not a key.** Type *guards* in chat while you stand in a territory with guards: it is matched as a whole word in any letter case, so *Guards!* works and *safeguards* does not. The words are yours to choose (`GuardCallWords`, comma separated); leave it empty and you never call them.

**The nearest answers first.** A posted guard of the territory you are in, within `GuardCallRadius` of you, leaves its post for the monster and walks back once the fight is done. Only when none is that close does a new guard step out beside you, and that one stays for `GuardLingerSeconds` after the fight before it goes. If no monster near you is after anybody, the guards see no threat and nobody moves.

**How many.** `GuardsPerPart` guards stand in each part of the territory and `MaxGuardsPerTerritory` caps the whole territory, so a territory combined from many parts does not fill with guards; the first parts are served first.

**The server keeps them.** It posts the guards, answers every call and holds each player's cooldown (`GuardCallCooldown`), so the guards are the same for everybody and nobody can call them faster by changing their own settings.

The guards are the town guards of ValtimaOnline - the same bodies, the same atgeir held two-handed for the strike - brought over to work in any territory.

</details>

---

![BEFORE YOU INSTALL](https://img.shields.io/badge/BEFORE_YOU_INSTALL-READ_THIS-b8860b?style=for-the-badge&labelColor=1b1b1b)

| | If… | Then… |
|---|---|---|
| 🤝 | you play on a **server** | install it on the **server and every player**, and update them together. The server refuses older versions: an older admin panel would switch the newest rules off in any territory it saved. |
| 🛑 | you turn off **`RequireClientMod`** | a player without the mod is not bound by any territory. Protections run in each player's own game. Leave it on unless that is acceptable. |
| 🔨 | you are the **admin** marking territories | install [Infinity Hammer](https://valheim.thunderstore.io/package/JereKuusela/Infinity_Hammer/) with Server Devcommands, and turn devcommands on. Players do not need it, and the panel can mark a circle without it. |
| ↩️ | you ever **roll the server back** | an older build enforces only the rules it knows and never saves over the newer territory file, so updating again finds it intact. A world with sub-territories should not go back to a build from before them. |

---

![I](https://img.shields.io/badge/I-YOUR_LAND%2C_YOUR_RULES-c9a227?style=for-the-badge&labelColor=1b1b1b)

**Every rule is a switch in the panel, and every rule can be different in every territory.**

| Rule | What it does |
|---|---|
| 🔨 **Building** | only members may place, remove, repair or dig. It works exactly like a ward: red ghost, "private zone". |
| 🚪 **Doors, chests & stations** | only members may use doors, chests, portals, signs, item and armour stands, fermenters, beehives and the rest of what a ward locks |
| 🌾 **Harvesting** | only members may pick crops, berries and other pickables. Vanilla wards cannot do this. |
| ⏱️ **Doors close by themselves** | a door or gate left open inside shuts after a few seconds, never in the face of a player standing next to it |
| 🧱 **Buildings cannot be damaged** | no hit harms them: creatures, fire and every player, members and admins included. Take a piece down with the hammer instead. |
| 👹 **No monster spawns** | no hostile world spawns, raid spawns, spawn points or nests. Deer, boars, birds and fish still spawn. |
| 🔷 **Runic barrier** | monsters cannot walk in (chapter II) |
| 🪓 **Guards** | armoured guards posted inside, who come when called (new, above) |
| ⚔️ **No raids** | a raid never picks a player standing inside |
| 💀 **No skill loss on death** | dying inside does not lower your skills |
| 🗡️ **PvP** | *Players choose*, *Forced off* (a safe zone) or *Forced on* (a war zone). The inventory toggle locks while you are inside, and your own choice returns when you leave. |
| ✨ **Sanctuary effect** | a status effect named after the territory: health, stamina and eitr regen multipliers, bonus carry weight and bonus armour |

<details>
<summary>📖 <b>Read the saga</b> — who is bound, where territories overlap, and the doors</summary>

**Building, door and harvest protection bind everyone who is not a member.** The owner and members are never stopped by them, and neither are server admins while `AdminsBypassProtection` is on. *Buildings cannot be damaged* binds everyone; weather and support still apply.

**Where territories overlap, the smaller one rules its own ground**, so a public market drawn inside a protected kingdom works without further setup. **Priority** lets the bigger one win when you want that instead. A territory marked inside another is its sub-territory, and that one always rules its own ground (chapter V).

**Doors close by themselves** is off by default. A door or gate left open inside shuts after `AutoCloseDelay`, unless a player is within `AutoClosePlayerClearance` of it; then it waits, and closes as soon as they step away. The server does the closing, so every player sees the door close, and it applies whoever opened the door, members included. Drawbridges and window shutters are left alone (`AutoCloseExcludedPrefabs`), and so are doors that need a key. Where territories overlap, the one that governs the door's spot decides. If you press Use on a door in the moment the server is closing it, the press is dropped: press again.

**Owners and members** are picked on the panel's **Members** tab from the players online, or added while offline by their player id (`terr_whoami` prints yours).

</details>

---

![II](https://img.shields.io/badge/II-THE_RUNIC_BARRIER-8b1e1e?style=for-the-badge&labelColor=1b1b1b)

**Monsters stop at the edge, and the runes flare where they push.**

| | |
|---|---|
| 🔷 **Runic barrier** | part of *No monster spawns*, on by default in a new territory. A hostile creature that reaches the edge is held outside it, and a blue rune shield flares where it pushes. |
| 🐺 **Who passes** | tamed creatures and peaceful animals pass, and so do the territory's guards. Bosses are held like any monster. |
| 🚪 **Already inside?** | a monster inside when the barrier goes up is set down just outside |
| 🏹 **A wall for bodies** | arrows and spit still cross it |
| 🧍 **Keep non-members out too** | players who are not members are held at the edge as well, with the same flare, and one already inside is set down outside. Off by default. |

<details>
<summary>📖 <b>Read the saga</b> — the order of the switches, and one barrier for many parts</summary>

**The switches stack.** The barrier only exists while *No monster spawns* is on, and *Keep non-members out too* only while the barrier is. In the panel each is greyed while the one above it is off, and the server clears a barrier setting whose rule is off, so a hand-edited file cannot leave one switched on by itself.

**Members and the owner always pass**, and so do admins while `AdminsBypassProtection` is on. Riding, sailing and sitting are left alone.

**A combined territory has one barrier**, round all its ground. A town inside a barrier territory is not covered by the outer barrier: a visitor who logs in inside a public town, or arrives by portal, stays there, and an arena town inside a monster-free kingdom keeps its monsters. A thing in the town that walks out onto the barrier's ground is held at the town's edge, and one found on that ground is taken out by the nearest way out of the *outer* edge, never into the town.

**The flare is worked out in each player's game**, with no network traffic. `BarrierEffects` turns the flare and its sound off for you; the barrier holds monsters out either way.

</details>

---

![III](https://img.shields.io/badge/III-BANNERS_%C2%B7_MAP_%C2%B7_BORDERS-5b2c83?style=for-the-badge&labelColor=1b1b1b)

**Walking in feels like finding a new land.**

| | |
|---|---|
| 📯 **Entry banner** | the territory's name as the big title, the way the game announces a new biome, with your own text beneath it (`{name}` and `{owner}` filled in) and the discovery sound the first time |
| 👋 **Exit banner** | "Leaving Hrafnborg", or your own text |
| 💎 **PvP notch** | a green gem on the banner where PvP is forced off, a red one where it is forced on |
| 🗺️ **Show on the map** | its real outline and name on the large map and the minimap, a combined territory as one shape, **only over ground you have uncovered**: fog stays fog |
| 🏷️ **Name only** | just its name on the maps, with no outline and no tint: for a continent or a region that would wash the whole map in one colour |
| 🧭 **The minimap's name** | while you are inside, its name replaces the biome name above the minimap, on one line |
| 〰️ **Show its border** | players near the edge see where the border runs on the ground |

<!-- screenshot slot (no public address yet): the epic entry banner over a territory, with the PvP notch -->

<details>
<summary>📖 <b>Read the saga</b> — no banner spam, and a map that keeps its fog</summary>

**A banner never spams at an edge.** You count as inside until you are 3 m past the edge, re-entering within `BannerCooldownSeconds` is silent, and dying inside is not leaving. Prefer something quieter? `EntryBannerStyle = Simple` puts one line in the centre of the screen, and `EntrySound` plays the sound on every entry or never.

**The map gives nothing away.** A territory is drawn only where you have uncovered the ground, by walking it or from another player's shared map. A territory half explored shows the explored half and fills in as you walk. `HideUnexploredTerritories` turns that off.

**The minimap's name stays on one line**: shrunk to fit the label, and cut short with dots when even the smallest size is too wide.

**Ground borders** show the stretch of border within `BorderRange` of you, along the outside edge only. A territory whose edges add up to more than 100 km draws no border on the ground; it keeps its name on the map.

</details>

---

![IV](https://img.shields.io/badge/IV-ANY_SHAPE_YOU_CAN_BUILD-2e5e3a?style=for-the-badge&labelColor=1b1b1b)

**Circles, squares and rectangles, combined into one territory.**

An L-shaped keep, a road with a market at its end, a ring of towers: one territory with one set of rules. Its protections, PvP, Sanctuary and banner cover all of it, walking from one part into another is not leaving, and the map draws one outline round the whole shape.

| You | It |
|---|---|
| **Territory combine** (a hammer tool) | place it inside the territory to keep, then within 15 seconds inside the one to join into it. Placing in the first one again cancels. |
| **Place → Ground → Combine** in the panel | while editing the territory to keep, **Join into this** beside the other one (click twice) |
| **`terr_combine <into> <from>`** | the same from the console |
| **Move** a part | marks new ground for that one part with the hammer |
| **Split off** | makes a part a territory of its own, with the same rules (click twice) |
| **Remove** | drops a part (click twice) |
| **Add ground with the hammer** · **Add a circle here** | adds a part at each placement until you press **Cancel** · adds one round you |

The territory you combine *into* keeps its name, rules and owner. The other's owner and members become its members, and the other's own name and rules are dropped. The **Ground** list is under **Place** while you edit a territory. `MaxParts` sets how many parts one territory may have; 1 turns combining off.

---

![V](https://img.shields.io/badge/V-A_CONTINENT_WITH_ITS_TOWNS-1f4e79?style=for-the-badge&labelColor=1b1b1b)

**Mark the big land first, then the towns inside it. Each town says only what makes it different.**

| | |
|---|---|
| 🏙️ **Sub-territories** | a territory marked inside another starts as a copy of its settings and **keeps following them**, until you change a setting on the town itself |
| 🔀 **Follows / own** | beside every setting of a sub-territory. Changing a setting makes it the town's own; clicking *own* makes it follow again; **Follow all of …** takes everything back (click twice). |
| 👥 **People follow too** | the continent's members are members of its towns until a town presses **Set its own list** on the Members tab |
| 📍 **Inside** | the row under the name: *Automatic* for a new territory, or **Change** to put one inside another or take it out |
| 👑 **The town rules its own ground** | over everything it sits inside, whatever their sizes and priorities say. Up to eight levels: a continent, a region, a town, a district. |
| 🗑️ **Deleting a continent** | never changes how the towns behave: each keeps its rules and moves up a level. **Delete with everything inside** takes them with it. |

<details>
<summary>📖 <b>Read the saga</b> — what follows, banners on a continent, and how big a territory can be</summary>

**What follows:** every rule, PvP, priority, colour, the owner and members, the Sanctuary numbers, and the entry and exit texts. A sub-territory's name, ground and history are always its own. When the continent changes, so does every town still following that setting.

**Without a link, nothing changes:** a territory with no parent works exactly as territories always have, and two territories that merely overlap still follow "the smaller one governs its ground", replacing their surroundings' rules instead of following them.

**Banners follow the link, not the ground.** Walking into a town gives the town's banner and no goodbye to the continent. Walking out says goodbye to the town and does not announce the continent again, because you never left it. A continent you have not been told about this session, because you logged in inside a town, is announced the first time you step out.

**The minimap** shows the town's name inside the town and the continent's name everywhere else on the continent. Give a continent *Name only* so it does not tint the whole map.

**The panel shows the tree:** the **All** tab lists territories each under the one it sits in, and the **Here** card names the chain you are standing in.

**Size.** `MaxRadius` goes up to 100 km, so a continent can be marked in the game from a few big parts. A territory read from the world's file may have parts up to 100 km each whatever `MaxRadius` says, and a file may hold more parts than `MaxParts` and more territories than `MaxTerritories`; those two only limit what is marked in the game.

</details>

---

![THE TOOLBOX](https://img.shields.io/badge/THE_TOOLBOX-QUICK_START_%C2%B7_KEYS_%C2%B7_HOW--TO-1b1b1b?style=for-the-badge&labelColor=8b1e1e)

**First ten minutes (admin):** press **Home** → **Rules**: a name, a colour, the rules, and **Guards** → **Place → Pick up the hammer to mark a new territory** → scroll to size it, place it → walk out and back in for the banner → type *guards* in chat.

| Key | | Command | |
|---|---|---|---|
| **Home** | the territory panel | `terr` | open or close the panel |
| **E** on a guard | talk to it | `terr_list` | every territory as a tree, with ids, owners and rules |
| *guards* in chat | call the guards | `terr_here [radius]` | mark a circle round you with the panel's rules |
| **Scroll** (hammer) | resize the territory | `terr_delete <id> [tree]` | delete one; its towns move up a level, or with `tree` go too |
| **Infinity Hammer's shape key** | circle, square or rectangle | `terr_parent <id> <parent id> [follow]` · `terr_parent <id> none` | put a territory inside another or move it, or back to the top level; it follows the new parent only with `follow` |
| | | `terr_combine <into> <from>` | join the second territory into the first |
| | | `terr_reload` | re-read the territory file after a hand edit |
| | | `terr_whoami` | your player id, and what the server says about you |
| | | `terr_place` · `terr_remove_at` · `terr_edit_at` · `terr_combine_at` | what the hammer tools run |

**Players** have the panel too: **Here** shows where you are, whose land it is and what applies to you, and **All** lists every territory with its distance.

<details>
<summary>🛠️ <b>Mark a territory (admins)</b></summary>

1. Open the panel (**Home**). On **Rules**, set the name, colour and rules. On **Members**, pick an owner and members from the players online, or add someone offline by player id.
2. Press **Place → Pick up the hammer to mark a new territory**, or choose **Territory** from the hammer's tool menu.
3. The ruler shows the area. Scroll to resize, use Infinity Hammer's shape key for circle, square or rectangle, rotate as you would a piece, and place.

The server checks that you are an admin (its own `adminlist.txt`, never your game's opinion), saves the territory and sends it to everyone. The answer appears in your console, top-left and on the panel.

**No hammer?** Use **Place → Mark a circle here**, or `terr_here <radius>`, for a circle centred on you. Infinity Hammer's tools need devcommands on (Server Devcommands); the panel tells you if they are off.

</details>

<details>
<summary>🩹 <b>Fix a bad placement</b></summary>

Two more hammer tools come with the Territory tool:

- **Territory remove**: place it inside a territory, then place it again on the same one within 6 seconds. The confirmation stops a stray click deleting one.
- **Territory edit**: place it inside a territory to open the panel on it. Change its rules and members and press **Save changes**, or **Move / reshape** to mark new ground with the hammer while keeping its rules (and its guards, who move with it).

</details>

<details>
<summary>🌍 <b>Set a whole world's territories from a file</b></summary>

The territory file is plain JSON, so a world-building tool can write it: a continent as one territory of a few big rectangles (its `"parts"`), and every town inside it as a territory of its own with all of its rectangles as parts and `"parent"` set to the continent. A town then needs only what makes it different: every key it leaves out follows the continent.

Write the file to `BepInEx/config/TerroritySmellatory/worlds/<world name>.json` on the server before the world first loads, or replace it and run `terr_reload` on a running one. Members and owners added in the game live in that file too, so replacing it replaces them. Every territory needs a unique positive `"id"`; an unknown key is ignored. For example:

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

Hrafnborg and Ulfheim are sub-territories of Svartland (`"parent"` is the id, or the name when that is unique in the file). Hrafnborg follows Svartland for everything it does not write: the same protections, PvP and owner, but its own colour, its own welcome, an outline on the map (*Name only* off) and a border on the ground. Ulfheim follows it too, apart from the three things it writes: it is a war zone with open building, drawn on the map. A key a sub-territory writes is its own **even if it matches the parent's value today**, so write only what should differ.

**How a sub-territory's keys read.** *Absent* means follow the parent. `"flags"` as a **list** of names sets *all* its rules (as a top-level territory's does); as an **object** (`{ "BuildProtect": false, "NoRaids": true }`) it sets just those and follows the rest. `owner` / `ownerName` / `members` are one group: write any of them and the territory has its own people (`"members": []` is its own, empty, list), and so are `healthRegen` … `armorBonus` for the Sanctuary numbers. `pvp`, `priority`, `color`, `enterMessage` and `leaveMessage` are one setting each. A territory with no `"parent"` is read as always: a missing key takes its default. A `"parent"` that is not in the file, a territory inside its own sub-territory, or a chain more than eight levels deep makes the server refuse the file (it says which, and keeps the territories it had). Older files read exactly as before.

**The rule names** (any letter case): `BuildProtect`, `AccessProtect`, `HarvestProtect`, `StructureProtect`, `NoMonsterSpawns`, `RunicBarrier`, `BarrierKeepsOutVisitors`, `NoRaids`, `NoSkillLoss`, `Sanctuary`, `AutoCloseDoors`, `Guards`, `Announce`, `ShowOnMap`, `MapNameOnly`, `ShowBorder`; `"pvp"` is `Unchanged`, `ForcedOff` or `ForcedOn`. A rule whose prerequisite is off is cleared when the file loads (the barrier needs *No monster spawns*, *Keep non-members out too* needs the barrier, *Name only* needs *Show on the map*), after a sub-territory has taken what it follows, so a barrier it sets itself stands as soon as the territory has, or follows, a no-monster rule.

**Check a file before the server does.** The mod's source comes with a checker that reads a file with the real reader and the server's real rules, and names what the server would take silently (a misspelt key or rule, a missing size, ground cut at 100 km, a rule cleared because its prerequisite is off, a territory inside another with no `"parent"`, a sub-territory that writes what its parent already has): `dotnet tests/bin/Release/net8.0/TerritoryTests.dll --check <file> [--at x,z]... [--tree]`. `--at` names the territory that governs a point; `--tree` prints the nesting.

</details>

<details>
<summary>💾 <b>Where the server keeps territories</b></summary>

Territories are saved per world in `BepInEx/config/TerroritySmellatory/worlds/<world>.json`, with a `.bak` of the previous version beside it. The file is plain, readable JSON: rules are listed by name (`"flags": ["BuildProtect", "NoRaids"]`), a combined territory lists its extra ground under `"parts"`, and a barrier is the names `"RunicBarrier"` and `"BarrierKeepsOutVisitors"` in the same list. Edit it by hand and run `terr_reload`.

If the file cannot be read, the server says exactly where it is broken (line and column) and **refuses to save** until it is fixed, while the territories it already had stay in force. A typo never turns into lost territories. A file written by a newer version of the mod is enforced but never saved over.

</details>

---

![MULTIPLAYER](https://img.shields.io/badge/MULTIPLAYER-THE_SERVER_DECIDES%2C_EVERY_GAME_ENFORCES-1f4e79?style=for-the-badge&labelColor=1b1b1b)

| Run by the server, for everyone | Enforced in each player's own game | Needs it on the server and every player |
|---|---|---|
| saving territories · admin checks · raids · doors closing · posting the guards and answering their calls | building, door and harvest protection · spawns · the runic barrier · PvP · Sanctuary · banners · the map · ground borders | everything: the server sends every player the territories, and kicks a player without the mod (`RequireClientMod`) |

> ⚠️ **Versions must match.** The server refuses players on older versions of the mod; update the server and its players together.

---

![COMPATIBILITY](https://img.shields.io/badge/COMPATIBILITY-PLAYS_WELL_WITH_OTHERS-2e5e3a?style=for-the-badge&labelColor=1b1b1b)

| Mod | |
|---|---|
| **Infinity Hammer** (1.84 or newer) | Admins only, and only to *mark* territories, with Server Devcommands and World Edit Commands as it requires. Without it everything else still works. Its own default tools are kept beside the territory tools. |
| **Better Continents** | Compatible. Nothing here assumes a world size, heights come from the game's own (Better Continents-patched) terrain, and a joining player gets the territories well after Better Continents' own download. |
| **Servers with RPC whitelists** (such as Wonderland) | A regular player's game receives the territories and sends only a call for the guards; the territory requests come from admins. |

Valheim 1.0, pure BepInEx, no Jotunn.

---

![SETTINGS](https://img.shields.io/badge/SETTINGS-BEPINEX_CONFIG-b8860b?style=for-the-badge&labelColor=1b1b1b)

All safe to leave alone. `BepInEx/config/wubarrk.terroritysmellatory.cfg`, or Configuration Manager in game if you run it.

<details>
<summary>🔒 <b>Server-synced</b> — set on the server, locked for everyone else</summary>

| Section | Setting | What it does |
|---|---|---|
| 0 - Server Sync | `LockConfiguration` | only admins change synced settings (on) |
| 1 - Server | `RequireClientMod` | kick players without the mod (on). Protections run in each player's game, so a player without it is not bound by them. |
| 1 - Server | `AdminsBypassProtection` | admins are never stopped by territory protections (on) |
| 1 - Server | `MaxRadius` | the largest radius or half-width a placement may have (600 m, up to 100 km) |
| 1 - Server | `MaxTerritories` | territories per world (500) |
| 1 - Server | `MaxParts` | the most parts one territory may have (16; 1 turns combining off) |
| 1 - Server | `AutoCloseDelay` | seconds a door may stay open before *Doors close by themselves* shuts it (10) |
| 1 - Server | `AutoClosePlayerClearance` | no door closes while a player is this close to it (3 m; 0 turns the check off) |
| 1 - Server | `AutoCloseExcludedPrefabs` | doors never closed by themselves: the drawbridges and window shutters; add a modded door here to leave it open |
| 1 - Server | `GuardsPerPart` | guards posted in each part of a territory with *Guards* (2, from 1 to 8) |
| 1 - Server | `MaxGuardsPerTerritory` | the most guards in one territory, however many parts it has (8, from 1 to 32) |
| 1 - Server | `GuardCallRadius` | how near a posted guard must be to answer a call before one appears beside the caller instead (80 m, from 10 to 400) |
| 1 - Server | `GuardCallCooldown` | seconds before the same player can call the guards again (10, from 1 to 300) |
| 1 - Server | `GuardLingerSeconds` | how long a guard who appeared for a call stays after the fight (40 s, from 5 to 300) |

</details>

<details>
<summary>🖥️ <b>Local to your game</b> — yours alone</summary>

| Section | Settings |
|---|---|
| **2 - Client** | `PanelKey` (Home) · `ShowBorders` (Off, PanelOnly, PanelOrBuilding, Always) · `BorderRange` · `ShowOnMap` · `HideUnexploredTerritories` · `EnterLeaveBanners` · `EntryBannerStyle` (Epic, Simple) · `EntrySound` (FirstEntry, EveryEntry, Off) · `BannerCooldownSeconds` · `MinimapTerritoryName` · `BarrierEffects` · `PanelScale` · `GuardCallWords` (guards; empty never calls) |
| **3 - Infinity Hammer** | `WriteHammerTool` (keeps the hammer's tool file; delete its first line to take it over yourself) · `InitialRadius` (the Territory tool's starting size) |
| **9 - Debug** | `VerboseLogging` |

</details>

---

![CREDITS](https://img.shields.io/badge/CREDITS-THANK_YOU-1b1b1b?style=for-the-badge&labelColor=c9a227)

**Needs:** [BepInExPack Valheim](https://valheim.thunderstore.io/package/denikson/BepInExPack_Valheim/) `5.4.2360` · [JsonDotNET](https://valheim.thunderstore.io/package/ValheimModding/JsonDotNET/) `13.0.4` · ServerSync is compiled in. **Admins:** [Infinity Hammer](https://valheim.thunderstore.io/package/JereKuusela/Infinity_Hammer/).

- Inspired by HanonHeim's *BaseTerritory*.
- The guards are ported from ValtimaOnline's town guards; the panel wears Wubarrk's shared VikingOS frame.

*Made by Wubarrk. Bug reports welcome on [Discord](https://discord.com/invite/GTTCnGS6Ym).* **License:** MIT: keep the copyright notice. Full text in `LICENSE.md`.
