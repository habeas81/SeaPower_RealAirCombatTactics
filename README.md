# Sea Power: Real Air Combat Tactics (Fighter AI Overhaul)

Overhauls fighter AI with lead-pursuit intercepts, shoot-look-shoot missile discipline, tanker-aware bingo and wingman tactics. Adds a Doppler notch radar model with automated missile defence: notch, chaff, Split-S, kinematic weave and last-ditch pull. Includes a player notch-assist tool and crew-skill scaling.

A Harmony 2 code mod (loaded by the Anchor Chain chainloader) for *Sea Power: Naval Combat in the Missile Age*. It applies to AI and player fixed-wing aircraft on every side.

## Features

**Offence**
- Shoot-look-shoot: no second missile at a target already covered by a live one.
- Lead-pursuit intercepts (arc-aware, velocity-filtered, hot pursuit, pursuit speed management).
- Speed-aware launch-range gate against fast targets.
- Post-pitbull crank; pressure shots; 2-ship target deconfliction and combat spread.
- Wingman extend-and-turn-in: when the flight lead is stuck in a circle fight, the wingman extends away, then turns back in for a missile shot (aborts on any other threat).
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

- Sea Power 0.8.7+ (Steam).
- [Anchor Chain](https://steamcommunity.com/sharedfiles/filedetails/?id=3380210757) (Workshop) with BepInEx 5.4.x.
- .NET SDK 10.0 to build from source.

## Installing

**Steam Workshop (recommended):** subscribe to this mod and to Anchor Chain, enable both in the in-game *Mods* menu, then fully restart the game.

**Manual:** copy the `RealAirCombatTactics` folder (`RealAirCombatTactics.dll`, `_info.ini`, `thumbnail.png`) from the release zip into
`<Sea Power>\Sea Power_Data\StreamingAssets\`, enable it in the *Mods* menu and restart.

Logs are written to `<Sea Power>\BepInEx\SeaPowerFighterAI.log` (recreated on each game start).

## Building

The project references the game's assemblies by path. Edit `GameDir` and `AnchorChainDll` in
[SeaPowerFighterAI.csproj](SeaPowerFighterAI.csproj) if your paths differ, then:

```
dotnet build
```

The post-build step assembles the mod folder from `workshop\` (`_info.ini`, `thumbnail.png`) plus the DLL into `dist\RealAirCombatTactics\`
and mirrors it into the game's `StreamingAssets`. In VS Code, `Ctrl+Shift+B` runs the same build.

## Publishing to the Steam Workshop

1. `dotnet build`, then in the game open *Mods* -> *Create/Update mod* (the built-in uploader).
2. Select the `RealAirCombatTactics` folder in `StreamingAssets` and `workshop\thumbnail.png` as the preview image.
3. Title and description come from `workshop\_info.ini`; add Anchor Chain as a required item on the Workshop page.
4. After the first upload, bump `Version` in the `.csproj` and `PLUGIN_VERSION` in [Plugin.cs](Plugin.cs) for each update.
## Status

v0.9 Beta. Tuning is done through log-driven test missions; constants are at the top of each source file.

## License

MIT, see [LICENSE](LICENSE). Not affiliated with the developers of Sea Power. No game assets or code are included in this repository.
