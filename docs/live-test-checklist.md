# Live-test checklist

Features that build and pass unit tests but have not been confirmed on a real CS2 server. Tick them off
on a test server (2+ clients: one to act, one to watch what *others* see), then delete the line.

Setup: fresh `WeaponPaints.zip` → server, `gamedata/weaponpaints.json` in
`addons/counterstrikesharp/gamedata/`, MenuManagerCore installed. Keep the server console open — every
item below should run without a `WeaponPaints` warning/exception.

## Config / startup
- [ ] Old `WeaponPaints.json` (v24, still has `MenuType`) migrates to v25: `MenuType` gone, every other
      value kept, DB creds kept, no `.bak` written, log line `Migrated ... from config version 24 to 25`.
- [ ] Creds only in `WeaponPaints.json` (`DatabaseHost`/...) → log `Using SQL config: WeaponPaints.json`.
- [ ] `css_plugins reload WeaponPaints` with players connected: no `UNEXPECTED LONG FRAME DETECTED`,
      skins stay applied, a change made just before the reload is in the DB afterwards.

## WeaponSkins port (2026-10-11)
- [ ] Customize menu after picking a skin (wear / seed / StatTrak / name tag / charm).
- [ ] `!nametag`.
- [ ] `!sticker` browser: tournament → kind → effect, capsules, chat search, "all slots".
- [ ] Charm menu.
- [ ] `!cp` while dead.
- [ ] StatTrak: grenade kill counts to the grenade's weapon, not the held one; count survives a reload.
- [ ] `css_wp_reloadcatalog`.

## Gen command (`!g`)
- [ ] AK-47 slot-4 sticker lands at the right spot (`StickerAnchors.cs`); spot-check 2–3 other weapons —
      if one is off, that row of the table is wrong.

## Fork ports (2026-10-24)
- [ ] Charm placement on AK.
- [ ] Gloves as seen by the *other* client.
- [ ] MVP music kit plays; `!musicoff`.
- [ ] Agent voice changes on team switch.
- [ ] Pet hats: attached and scaled right (`Pet hat check` log line).
- [ ] Pet emotes (`!petemote`).
