# Sea Power: Real Air Combat Tactics (Fighter AI Overhaul)

Overhauls fighter AI with lead-pursuit intercepts, shoot-look-shoot missile discipline, tanker-aware bingo and wingman tactics. Adds a Doppler notch radar model with automated missile defence: notch, chaff, Split-S, kinematic weave and last-ditch pull. Includes a player notch-assist tool and crew-skill scaling.

A BepInEx 5 / Harmony 2 plugin for *Sea Power: Naval Combat in the Missile Age*. It applies to AI and player fixed-wing aircraft on every side.

## Features

**Offence**
- Shoot-look-shoot: no second missile at a target already covered by a live one.
- Lead-pursuit intercepts (arc-aware, velocity-filtered, hot pursuit, pursuit speed management).
- Speed-aware launch-range gate against fast targets.
- Post-pitbull crank; pressure shots; 2-ship target deconfliction and combat spread.
- Combat jettison of tanks and air-to-ground stores when threatened.

**Tanker and fuel**
- Tanker-aware bingo, using the tanker's planned route (including turns) for the rendezvous point.

**Radar and notch**
- Passive Doppler notch model for aircraft radars and missile seekers, with chaff bloom.
- Mid-course guidance link is cut while the shooter's radar cannot see a notched target; the missile coasts on its last track.
- Manual notch assist for the player (bottom-bar NOTCH button and map right-click menu).

**Automated missile defence**
- Notch, chaff, Split-S / Immelman reversal inside the notch, hold, pitbull notch, kinematic weave, last-ditch pull.

**Crew skill**
- Green to Ultra scales reaction delay, notch accuracy, last-ditch timing and offensive launch range.

**Diagnostics**
- Command logging, loadout/crew logging, `DEFENSE` / `NOTCH` / `JETTISON` event logs, emissions overlay.

## Requirements

- Sea Power (Steam) with BepInEx 5.4.x and the AnchorChain preloader.
- .NET SDK 10.0 to build.

## Building

The project references the game's assemblies by path. Edit `GameDir` in
[SeaPowerFighterAI.csproj](SeaPowerFighterAI.csproj) if Sea Power is not at
`E:\SteamLibrary\steamapps\common\Sea Power`, then:

```
dotnet build
```

A post-build step copies `SeaPowerFighterAI.dll` into `BepInEx\plugins\`. In VS Code, `Ctrl+Shift+B` runs the same build.

## Installing

Copy `SeaPowerFighterAI.dll` to `<Sea Power>\BepInEx\plugins\`. Logs are written to
`<Sea Power>\BepInEx\SeaPowerFighterAI.log` (deleted on each game start).

## Status

Early development. Tuning is done through log-driven test missions; constants are at the top of each source file.

## License

MIT, see [LICENSE](LICENSE). Not affiliated with the developers of Sea Power. No game assets or code are included in this repository.
