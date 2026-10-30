# SeneaL UI Unlocker

SeneaL UI Unlocker turns SeneaL UI's features back on when you join a server that does not run SeneaL UI, hides the popup that says so, and lets SeneaL UI's mod settings open on the main menu.

> **Valheim with BepInEx. Client-side: only you need it, and a server does not.**

---

## What It Does

- **SeneaL UI on any server** - when the server does not answer SeneaL UI within Wait For Server Seconds (5 by default), its features run up to the limits you set, SeneaL UI's maximums by default.
- **What turns on** - up to 64 inventory slots, crafting and building from nearby chests, quick stack, restock, wallet, ore detection, live marks, compass and clock.
- **Older builds** - a feature your SeneaL UI build lacks stays off, and the log names it.
- **Your settings** - your own SeneaL UI settings still turn each feature on or off, and its mod settings open on the main menu too.
- **Server rules** - a SeneaL UI server keeps every rule it states; one it leaves out follows your choice while Own Settings For Unstated Server Rules is on (the default).
- **Safety** - items SeneaL UI drops because the inventory got smaller are pinged and listed, and your character file is copied aside the first time each server is unlocked.
- **SeneaL Radar** - when installed, it shows creatures on such a server too, up to 200 m by default, and only what you could see.
- **Inventory** - sort by value (Shift+V), restock from nearby chests (Shift+R), middle mouse rectangle select, chest cycling with Page Down and Page Up and a portal stash button.
- **Gear** - comparison cards on hover, Ctrl+1 to Ctrl+4 loadouts, an auto shield, a Cosmetics window and repair at crafting stations.
- **Reminders** - durability, food and effect reminders with optional auto-eat.
- **Planning** - a Tracker for recipes, trophies, skills and item amounts, a Food planner, a Death report card and a Progress view.
- **Map** - pin search and bulk edit, pin looks shared through cartography tables, auto-pins for bosses, traders, dungeons and more, live portal, ship, cart and tame pins, and range rings.
- **Party and chat** - a Party HUD and shared map for players with this mod, plus chat filters, colors, mentions and friends.
- **HUD editor** - lock, pick, line-up, copy-look and corner-grow tools, frames that match each element exactly, combat-only elements and a built-in layout that fits every resolution.
- **Previews** - building ranges, wards, monster-free areas, warmth and comfort drawn with the hammer.
- **Building and the world** - station extensions stand as close as their shapes allow, a build camera (O), Repair Nearby (U), Undo Remove (Backspace), Auto Doors (off by default), tame details and production alerts.
- **Finding things** - /find, /recipe, P to pin what you look at, sign and dropped-item pins, chest and vehicle names, skill progress and Sheltered and Comfort cards.
- **Cheats** - each switch is off by default, in the Cheats section of the settings page.
- **Update notice** - once per game start the mod asks alkeari.com for its newest version and says so in the top left corner only when a newer one is out. Nothing else is sent; General > Update Notice turns it off.

---

## Requirements

- Verified on Valheim 1.0.20.

## Installation

1. Start Valheim and click Mod Settings on the main menu (or press F1): SeneaL UI Unlocker is listed in SeneaL UI's mod settings window.

Before removing it, read Compatibility.

## Configuration

Settings are in SeneaL UI's mod settings window under SeneaL UI Unlocker and in `alkeari.valheim.senealuiunlocker.cfg`, debug settings in the cfg only. The groups below gather them by topic; the window and cfg use smaller sections. Every range has a slider with a box for an exact value, and every key setting takes one key or a combination.

| Group | What it sets |
|---|---|
| General | Unlocking, the wait for the server, your own settings for unstated server rules, the popups, mod settings on the main menu and the update notice |
| Unlocked Features and Limits | A switch per unlocked feature and the largest values allowed |
| Servers | Unlock on every server, only listed ones, or all but listed ones, with presets per server |
| Interface | Full, per-server and per-world presets, Merge Crafting Elements and text formatting |
| HUD Editor and Combat HUD | The editor additions, Frames Match Elements and combat-only elements |
| Inventory, Loadouts, Crafting | Sorting, restocking, chest cycling, the split wheel, loadout keys, item comparison, Cosmetics, recycling and repair at stations |
| Reminders, Food and Tracker | Durability, food and effect reminders, auto-eat (off), the food planner and the Tracker |
| Map, Auto Pins and Live Pins | Search, range rings, auto-pin kinds, live pins, cartography tables and old bed pins |
| Party, Party HUD and Chat | The party, its HUD rows, map sharing, chat filters, mentions and friends |
| Previews | When areas show, how far, and each kind's outline and fill |
| Building, Doors, Tames and Alerts | Build Restrictions, the build camera, Repair Nearby, Undo Remove, Auto Doors (off), tame details and production alerts |
| Find, Status Cards, Death and Progress | /find and /recipe, the status, skill progress, Sheltered and Comfort cards, the Death Report and the Progress card |
| Accessibility | Camera shake, damage flash and fire smoke |
| Compatibility | A switch for each mod under Compatibility Patches |
| Debug | The debug log and call tracing, off and set only in the cfg file |
| Cheats | Every cheat switch, off by default |
| Radar, SeneaL UI Keys, Server Rules | SeneaL Radar options, keys SeneaL UI does not let you change, and which features a server's players may use |
| Hotkeys | Every key setting together |

## Compatibility

- Client-side: only you need it, and it works on a vanilla dedicated server. A SeneaL UI server keeps every rule it sets, and the ones it leaves out follow your settings (Own Settings For Unstated Server Rules).
- A server that also runs this mod decides which of its features players may use, in its Server Rules settings: features are allowed and cheats denied until it says otherwise.
- Before you remove it, empty inventory slots above 32, action slots, key slots and the wallet: without it, SeneaL UI shrinks the inventory to 32 slots on that server.
- Items that no longer fit move to free cells or drop on the ground.
- Live portal, ship and cart pins replace SeneaL UI's own live marks while on.
- Tested on Valheim 1.0.20 with SeneaL UI 1.1.7, 1.1.8, 1.1.9 and 1.2.0 to 1.2.5.
- The HUD editor additions need a SeneaL UI build with a HUD editor, and a part whose SeneaL UI parts an update removes or renames stays off.
- Every cheat switch is off by default and named with (Cheat) in its settings.

### Compatibility Patches

These mods do things this mod does not, so it carries code that makes each work beside it:

- Input Tweaks: its inventory input works on SeneaL UI's grid.
- Valheim Terrain Tools: its brush costs show in SeneaL UI's build panel, and its panel hides this mod's cards it covers.
- SmartCraftStorage: SeneaL UI's crafting window counts and takes what it finds in nearby chests.
- KeyBindManager: your HUD layout stays as you last set it when you load a world.
- MultiCraft: SeneaL UI still crafts the amount its own stepper shows.
- AutoRemoveDeathPin and ComfyAutoRepair: death pin removal and repair at stations stand down.
- DadsFPP and DadsSkills: the build camera still flies free in first person, and the death preview shows DadsSkills' checkpoint.
- Quick Stack Store Sort Trash Restock, Extra Slots, MyLittleUI, ZenUI, AutomaticFuel, Chatter and Badgers Shading Overhaul: the features that duplicate SeneaL UI's and this mod's are switched off.
- Each of these nine, DadsFPP to Badgers Shading Overhaul, has its own on-by-default setting under Compatibility, shown while that mod is installed.

### Incompatible Mods

These change the same screens and features as this mod, so do not run them beside it: Craft From Containers (rendl0449, and UnderHeiz's port of aedenthorn's), Comfy Quick Slots, Equipment and Quick Slots, Sears Catalog, Minimal Status Effects, HUD Compass and InventorySlots.

## License

License terms are in the [Alkeari License Agreement](https://alkeari.com/license/).
