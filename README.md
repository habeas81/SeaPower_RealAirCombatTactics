# Sea Power: Real Air Combat Tactics (Fighter AI Overhaul)

Overhauls fighter AI with lead-pursuit intercepts, shoot-look-shoot missile discipline, tanker-aware bingo and wingman tactics. Adds a Doppler notch radar model with automated missile defence: notch, chaff, Split-S, kinematic weave and last-ditch pull. Includes a player notch-assist tool and crew-skill scaling.

A Harmony 2 code mod (loaded by the Anchor Chain chainloader) for *Sea Power: Naval Combat in the Missile Age*. It applies to AI and player fixed-wing aircraft on every side.

## Features

**Offence**
- Shoot-look-shoot: no second missile at a target that already has a live missile in flight, so long- and medium-range missiles are not wasted.
- Lead-pursuit intercepts: aim where the target will be, not where it is (arc-aware, velocity-filtered, hot pursuit, pursuit speed management).
- Crew-skill launch range: green crews fire early, toward the missile's published maximum (easy to defeat); seasoned crews fire at the game's computed range; veterans wait for a closer, higher-probability shot.
- Post-launch crank (keeps the radar on the target while turning off); pressure shots.
- 2-ship tactics: combat spread and target deconfliction, so the pair does not stack up behind the leader and fire redundant salvos.
- Wingman extend-and-turn-in: when the flight lead is stuck in a turning fight, the wingman extends away, then turns back in for a missile shot (aborts on any other threat).
- **Ordnance jettison:** an aircraft carrying an air-to-ground loadout (free-fall bombs, cluster stores) or external fuel tanks that is threatened by an air-to-air engagement (inbound missile, or a close, fast hostile pointed at it) dumps those stores to fight clean and go defensive, as real crews do. Applies to AI and player aircraft. Guided air-to-ground and anti-ship missiles are kept, not jettisoned. Expect strike aircraft to lose their bombs when you engage them.

- Plain right-click attack orders: right-clicking a hostile air contact with the formation selected (no weapon chosen first) now commits the engage and the flight pursues, instead of staying on "heading to waypoint".
- Radar discipline: aircraft without a Fox-3 weapon keep radar off until inside kinematic range plus lock time plus 60 s.

**Tanker and fuel**
- Tanker-aware bingo: a low-fuel fighter heads for the tanker's planned route (including turns), not its current position.

**Radar and notch**
- Passive Doppler notch model for aircraft radars and missile seekers: flying at the right angle to the beam hides a target in ground clutter. Chaff widens the notch for a short time.
- Mid-course guidance link is cut while the shooter's radar cannot see a notched target; the missile coasts on its last track.
- Manual notch assist for the player (bottom-bar NOTCH button and map right-click menu).

**Automated missile defence**
- On a detected incoming missile: notch, chaff, Split-S / Immelman reversal inside the notch, hold, pitbull re-notch, kinematic weave, last-ditch pull.
- A pilot fighting for his life does not obey steering orders: a player order given during a defence shows "Unable, defending" and is followed once the threat is resolved. Orders with no threat to life are obeyed at once.
- Post-defence search: after surviving a missile with nothing of its own to re-engage, an aircraft (AI sides by default) weaves toward where the shot came from for a few minutes, radar on, checking its own and its wingman's six, until it sees a hostile, is shot at again or the time runs out. Wingmen of an aircraft that is shot at or shot down do the same. Map tag `DEF-SEARCH`; config `[PostDefenseSearch]`.

**Crew skill**
- Green to Ultra scales reaction delay, notch accuracy, last-ditch timing and air-to-air launch range.

**Diagnostics (off by default)**
- Verbose tracing (pursuit/missile traces, command log, missile-hold log, loadout dump) costs frame time and is disabled. To enable it for a bug report, set `Diagnostics = true` in `BepInEx\config\` (`<plugin guid>.cfg`, created on first run) and restart. The log is capped at 20 MB.

## Patch notes

**v0.9.4**
- Updated for Sea Power 0.9.0. Requires Anchor Chain 1.2.0 with its 1.2.0 preloader.
- Fixed: aircraft could ignore orders after a missile defence, notch or wingman extend (flying on at a stale point) and could be pinned at an old altitude; control is now handed back to whatever the game expects at that moment.
- Fixed: Split-S could carry on into the climbing half of a loop (Split-S then Immelman).
- Fixed: missile speed checks were always false on 0.9.0, so speed commands were re-issued every tick and Mach was not restored after a defence.
- Fixed: wingman extend sent the jet toward its ceiling (altitude passed in the wrong unit).
- Changed: the mod's own launch-range reduction is removed; 0.9.0's missile range already accounts for target speed and evasion, and applying both left fighters chasing for a Sidewinder shot with Sparrows aboard. Crew skill now sets the launch range instead (green crews fire early toward the label range, veterans hold for a closer shot).
- New: post-defence search, and wingmen of an aircraft that is shot at or shot down search for the shooter.
- New: a player order during a defence is acknowledged ("Unable, defending") and followed when the threat is resolved.
- Radar notch: every stock airborne radar classified from open sources (AWG-9/10/11/12 and APG-63 early pulse-Doppler; Sapfir-23ML, Saphir-25 and Cyrano IV a wider MTI notch; Blue Fox and the F-4D/E, F-5E radars pulse, cannot be notched).
- Fixed: the NOTCH bottom-bar button tooltip showed "MISSING TEXT".
- Removed: the vertical-arc fix (the game fixed that bug in 0.9.0).
- Diagnostics: pursuit lines show commanded altitude and each missile's fire state and range limits; enemy status tags are shown on the map while Diagnostics is on.

**v0.9.3**
- Fixed: Cease Fire (X) did not stop re-engagement. It now cancels hot pursuit, the player-ordered target, a wingman extend and a pending post-defence re-engage, and the aircraft returns to its own navigation. Weapons Hold also stops hot pursuit. (Under Weapons Tight/Free the game's own auto-engage can still pick up hostile air targets.)
- Fixed: fighters launching two missiles at one target. Hot-pursuit re-tasks now wait while any friendly missile is still viable, and a second queued missile task on the same target is blocked (player-ordered tasks are exempt).
- Fixed: Split-S restarting two or three times while levelling out. The roll reference now flips after the vertical, so the aircraft exits upright instead of re-inverting.
- Diagnostics: log timestamps use the mission clock (`T+HH:MM:SS`), so pausing no longer skews them.

**v0.9.2**
- New: combat jettison is gated by rules of engagement (`[Jettison]` config: bombs may be dumped on Hold and Tight by default, not on Free; `BombsPerJettison` = 0 drops all) and can drop only some bombs per release (`BombsPerJettison`), outermost first, alternating left and right.
- New: an aircraft defending against a missile stops its own attack and clears its engage tasks. If the threat is far off and its own missile needs support for much less time, it finishes supporting it first. When safe it turns back and re-engages (Hold is respected).
- New: map status labels for friendly aircraft (`DEF-NOTCH`, `DEF-REV`, `DEF-HOLD`, `DEF-PIT`, `DEF-KIN`, `DEF-LDM`, `NOTCH`, `EXT`, `CRANK`, `PRESS`, `PUR`, `ATT`) and missiles (`GUIDED`, `COAST`, `NOTCHED` for 3 s after a notch, `ACTIVE`, `IR`).
- Fixed: wingmen never defended. The game's formation state reset the flight model and altitude control every tick.
- Fixed: Split-S / Immelman restarted repeatedly and exited about 180 degrees off when chaff decoyed the missile mid-loop. A started reversal now completes.
- Fixed: defenders did not reach the notch (altitude command ignored); Split-S height check relaxed; notch-hold timeout doubled.
- Performance: map overlay tags cached 0.5 s; optional `PerfMonitor` profiler (off by default).

**v0.9.1**
- Fixed: plain right-click attack on a formation not starting pursuit.
- Fixed: radar missiles held until ~2.7 nm on E/W headings (vertical-arc helper).
- New: radar-on rule for aircraft without Fox-3 weapons.
- Performance: diagnostic patches are no longer installed unless `Diagnostics` is enabled; hostile-contact scans cached; hostile-propagation scan throttled to 1 s; taxi blocker lookup cached; log size capped.

## Requirements

- Sea Power 0.9.0+ (Steam).
- [Anchor Chain](https://steamcommunity.com/sharedfiles/filedetails/?id=3380210757) 1.2.0 (Workshop) with BepInEx 5.4.x. Follow its install instructions (https://github.com/SeaPower-Modders/AnchorChain) and use the 1.2.0 preloader (`ACPreloader.zip` from the [V1.2.0 release](https://github.com/SeaPower-Modders/AnchorChain/releases/tag/V1.2.0)); older preloaders do not start Anchor Chain 1.2.0 on Sea Power 0.9.0.
- .NET SDK 10.0 to build from source.

## Installing

**Steam Workshop (recommended):**
1. Follow the install instructions for the core Anchor Chain mod (https://github.com/SeaPower-Modders/AnchorChain), including the 1.2.0 preloader, then subscribe to this mod and Anchor Chain. Let Steam finish downloading.
2. Launch Sea Power and open the *Mods* menu.
3. Make sure both *Anchor Chain* and *Real Air Combat Tactics* are enabled.
4. Fully exit the game (quit to desktop) and start it again; mods only load at game start.
5. To check it loaded, look for `BepInEx\SeaPowerFighterAI.log`; its first line says the mod initialised. If the file is missing, one of the two mods is not enabled.

Restart the game every time you change the mod list.

**Manual:** copy the `RealAirCombatTactics` folder (`RealAirCombatTactics.dll`, `_info.ini`, `thumbnail.png`) from the release zip into
`<Sea Power>\Sea Power_Data\StreamingAssets\`, enable it in the *Mods* menu and restart.

Logs are written to `<Sea Power>\BepInEx\SeaPowerFighterAI.log` (recreated on each game start).

## Building

The project references the game's assemblies by path. Edit `GameDir` and `AnchorChainDll` in
[SeaPowerFighterAI.csproj](SeaPowerFighterAI.csproj) if your paths differ, then:

```
dotnet build
```

The post-build step assembles the mod folder from `workshop\` (`_info.ini`, `thumbnail.png`, `RealAirCombatTactics.jpg`) plus the DLL into `dist\RealAirCombatTactics\`
and mirrors it into the game's `StreamingAssets`. In VS Code, `Ctrl+Shift+B` runs the same build.

## Publishing to the Steam Workshop

1. `dotnet build`, then in the game open *Mods* -> *Create/Update mod* (the built-in uploader).
2. Open (double-click) the `RealAirCombatTactics` folder in `StreamingAssets` so it is the current folder, then select it. Pick `RealAirCombatTactics.jpg` as the preview image (the uploader only accepts `.jpg`).
3. Title and description come from `workshop\_info.ini`; add Anchor Chain as a required item on the Workshop page.
4. After the first upload, bump `Version` in the `.csproj` and `PLUGIN_VERSION` in [Plugin.cs](Plugin.cs) for each update.

## Status

v0.9.4 Beta. Tuning is done through log-driven test missions; constants are at the top of each source file.

## License

MIT, see [LICENSE](LICENSE). Not affiliated with the developers of Sea Power. No game assets or code are included in this repository.
