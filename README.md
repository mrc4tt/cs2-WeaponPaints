# CS2 WeaponPaints

## Description
Unfinished, unoptimized and not fully functional ugly demo weapon paints plugin for **[CSSharp](https://docs.cssharp.dev/docs/guides/getting-started.html)**. 

## Features
- Changes only paint, seed and wear on weapons, knives, gloves and agents
- MySQL-based
- Data syncs on player connect
- Added command **`!wp`** to refresh skins ***(with cooldown in seconds can be configured)***
- Added command **`!ws`** to show website
- Added command **`!knife`** to show menu with knives
- Added command **`!gloves`** to show menu with gloves
- Added command **`!agents`** to show menu with agents
- Added command **`!pins`** to show menu with pins
- Added command **`!music`** to show menu with music
- Added command **`!pet`** to show menu with chicken pets (CS2 1.41.8.2), see [Pets](#pets)
- Stickers and charms on the **C4** (CS2 1.41.8.2), see [C4 stickers and charms](#c4-stickers-and-charms)
- Translations support, submit a PR if you want to share your translation

## Images
**Are you looking for images? - You can find them here: [WeaponPaints IMG](https://git.miksen.me/mikkel/weaponpaints-img)**

## ⚙️ Requirements
**Ensure all the following dependencies are installed before proceeding**
- [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp)
- [CS2MenuManager by schwarper (forked)](https://git.miksen.me/mikkel/CS2MenuManager/releases)
- MySQL database

## CS2 Server
- Have working CounterStrikeSharp (**with RUNTIME!**)
- Download from the release and copy the plugin to the plugins folder
- Run server with plugin, **it will generate config if installed correctly!**
- Edit **`addons/counterstrikesharp/configs/plugins/WeaponPaints/WeaponPaints.json`** include database credentials
- In **`addons/counterstrikesharp/configs/core.json`** set **FollowCS2ServerGuidelines** to **`false`**
- Copy from plugins folder gamedata file **`weaponpaints.json`** to folder **`addons/counterstrikesharp/gamedata/`**

## C4 stickers and charms
CS2 1.41.8.2 made the C4 sticker-capable. The C4 has no paint kits, so a C4 row is always
`weapon_paint_id = 0` - it goes through the same "sticker/charm without a skin" path as any unpainted gun.

- Row: `wp_player_skins(steamid, weapon_team 2 (or 0), weapon_defindex 49, weapon_paint_id 0, weapon_sticker_0..4, weapon_keychain)`.
  No schema change.
- All five sticker slots are written. The model only has four sticker homes, so slot 4 borrows home 1 and is
  shifted onto the side of the bomb (`StickerAnchors`), the same way the fifth sticker works on the four-home guns.
- The C4 always uses its one hd mesh and is not in the `!skins` menu (nothing to paint). `!sticker` works while
  holding it; the bomb is updated in place, never dropped and re-given.
- When the bomb is planted, the planted bomb gets the same stickers if the game did not carry them over itself.
  **Untested in game.**

## Pets
CS2 1.41.8.2 added chicken pets (item 4681). The plugin spawns its own `chicken` that follows the player,
carrying the pet id, stage, seed and name, and a `<player>'s <name>` label. **This has not been run on a server
yet.**

- **Colour** is `pet_variant`: a material group index (Catalana 0-13, Silkie 0-9, Polish 0-12; 0 = default, the
  chick has none). `NULL` or an index the model does not have means the default colour. A pet picked in `!pet`
  gets a colour rolled with the game's own weights; `!pet color` changes it.
- **Seed** (`pet_seed`) drives the body shape of a pullet/hen and a small colour jitter. Seed 0 is refused.
- **Stage** (`pet_stage`) is clamped to 0-3. Eggs never leave the nest.
- Spawned shortly after the owner spawns, stays put when the owner dies, comes back on the next spawn, and is
  removed on team change, disconnect, map end and plugin unload. The game removes owned chickens at every round
  start; the plugin puts them back a frame later. A player the game itself gave a pet gets no second one.

| Command | What it does |
| --- | --- |
| `!pet` | Menu: pick a pet and its stage, or None. "New random look" rolls a new seed and colour |
| `!pet name <text>` | Name the pet (32 characters max; empty clears it) |
| `!pet seed [number]` | A new random seed, or a specific one (not 0) |
| `!pet color <number\|random\|default>` | Pick a colour, roll one, or go back to the default |
| `!pet stage <chick\|pullet\|hen>` | Change the stage |
| `!pet off` | No pet |

Config (`Additional`): `PetsEnabled` (default `true`), `CommandPet` (default `["pet"]`), `PetPermission`
(default `""` = everyone, e.g. `"@css/vip"` - also decides whether a player's pet is spawned at all).

Table (created automatically; the column layout matches SkinHubgg/WeaponPaints so a site written for it works):

```sql
CREATE TABLE IF NOT EXISTS `wp_player_pets` (
  `steamid`     varchar(18)  NOT NULL PRIMARY KEY,  -- one pet per player, pets are "noteam"
  `pet_id`      int          NOT NULL,              -- 1 egg, 2 chick, 3 catalana, 4 silkie, 5 polish
  `pet_stage`   tinyint      NOT NULL DEFAULT 3,    -- 0 egg, 1 chick, 2 pullet, 3 hen
  `pet_variant` int          NULL,                  -- material group index; NULL = default colour
  `pet_seed`    int unsigned NOT NULL DEFAULT 0,    -- "pet seed" attribute
  `pet_name`    varchar(32)  NULL                   -- name tag
);
```

The pet models are precached on map load, so a plugin loaded mid-map may show pets on the default chicken model
until the next map.

## Plugin Configuration
<details>
  <summary>Click to expand</summary>
<code>
{
	"Version": 4, // Don't touch
	"DatabaseHost": "", // MySQL host
	"DatabasePort": 3306, // MySQL port
	"DatabaseUser": "", // MySQL username
	"DatabasePassword": "", // MySQL user password
	"DatabaseName": "", // MySQL database name
	"CmdRefreshCooldownSeconds": 60, // Cooldown time in refreshing skins (!wp command)
	"Prefix": "[WeaponPaints]", // Prefix every chat message
	"Website": "example.com/skins", // Website used in WebsiteMessageCommand (!ws command)
"Messages": {
	"WebsiteMessageCommand": "Visit {WEBSITE} where you can change skins.", // Information about the website where the player can change skins (!ws command). Set to empty to disable
	"SynchronizeMessageCommand": "Type !wp to synchronize chosen skins.", // Information about skins refreshing (!ws command) Set to empty to disable
	"KnifeMessageCommand": "Type !knife to open knife menu.", // Information about knife menu (!ws command) Set to empty to disable
	"CooldownRefreshCommand": "You can\u0027t refresh weapon paints right now.", // Cooldown information (!wp command) Set to empty to disable
	"SuccessRefreshCommand": "Refreshing weapon paints.", // Information about refreshing skins (!wp command) Set to empty to disable
	"ChosenKnifeMenu": "You have chosen {KNIFE} as your knife.", // Information about choosen knife (!knife command) Set to empty to disable
	"ChosenSkinMenu": "You have chosen {SKIN} as your skin.", // Information about choosen skin (!skins command) Set to empty to disable
	"ChosenKnifeMenuKill": "To correctly apply skin for knife, you need to type !kill.", // Information about suicide after knife selection (!knife command) Set to empty to disable
	"KnifeMenuTitle": "Knife Menu.",  // Menu title (!knife menu)
	"WeaponMenuTitle": "Weapon Menu.", // Menu title (!skins menu)
	"SkinMenuTitle": "Select skin for {WEAPON}" // Menu title (!skins menu, after weapon select)
},
"Additional": {
	"KnifeEnabled": true, // Enable or disable knife feature
	"SkinEnabled": true, // Enable or disable skin feature
	"CommandWpEnabled": true, // Enable or disable refreshing command
	"CommandKillEnabled": true, // Enable or disable kill command
	"CommandKnife": "knife", // Name of knife menu command, u can change to for e.g, knives
	"CommandSkin": "ws", // Name of skin information command, u can change to for e.g, skins
	"CommandSkinSelection": "skins", // Name of skins menu command, u can change to for e.g, weapons
	"CommandRefresh": "wp", // Name of skin refreshing command, u can change to for e.g, refreshskins
	"CommandKill": "kill", // Name of kill command, u can change to for e.g, suicide
	"GiveRandomKnife": false,  // Give random knife to players if they didn't choose
	"GiveRandomSkins": false  // Give random skins to players if they didn't choose
},
</code>
</details>

## Troubleshooting
**Skins are not changing:**<br />
Set FollowCSGOGuidelines to false in cssharp’s core.jcon config

**Database error: table does not exist**<br />
The plugin is not loaded or configured with MySQL credentials. Tables are auto-created by the plugin.

### Use this plugin at your own risk! Using this may lead to a GSLT ban or something else Valve comes up with. [Valve Server guidelines](https://blog.counter-strike.net/index.php/server_guidelines/)

## License
Licensed under the [GNU General Public License v3.0](LICENSE).
This is a fork of [Nereziel/cs2-WeaponPaints](https://github.com/Nereziel/cs2-WeaponPaints), which is also GPLv3;
changes and redistributions must stay under the same license.
