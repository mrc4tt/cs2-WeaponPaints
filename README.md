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
- Translations support, submit a PR if you want to share your translation

## Images
**Are you looking after images? - You can find them here: [WeaponPaints IMG](https://git.miksen.me/mikkel/weaponpaints-img)**

## ⚙️ Requirements
**Ensure all the following dependencies are installed before proceeding**
- [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp)
- [CS2MenuManager by schwarper](https://github.com/schwarper/CS2MenuManager)
- MySQL database

## CS2 Server
- Have working CounterStrikeSharp (**with RUNTIME!**)
- Download from the release and copy the plugin to the plugins folder
- Run server with plugin, **it will generate config if installed correctly!**
- Edit **`addons/counterstrikesharp/configs/plugins/WeaponPaints/WeaponPaints.json`** include database credentials
- In **`addons/counterstrikesharp/configs/core.json`** set **FollowCS2ServerGuidelines** to **`false`**
- Copy from plugins folder gamedata file **`weaponpaints.json`** to folder **`addons/counterstrikesharp/gamedata/`**

## Plugin Configuration
<details>
  <summary>Click to expand</summary>
<code><pre>{
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
</pre></code>
</details>

## Troubleshooting
**Skins are not changing:**<br />
Set FollowCSGOGuidelines to false in cssharp’s core.jcon config

**Database error table does not exist:**<br />
The plugin is not loaded or configured with MySQL credentials. Tables are auto-created by the plugin.

### Use this plugin at your own risk! Using this may lead to GSLT ban or something else Valve comes up with. [Valve Server guidelines](https://blog.counter-strike.net/index.php/server_guidelines/)