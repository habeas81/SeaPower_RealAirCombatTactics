# Sea Power: Real Air Combat Tactics (Fighter AI Overhaul)

Overhauls fighter AI with lead-pursuit intercepts, shoot-look-shoot missile discipline, tanker-aware bingo and wingman tactics. Adds a Doppler notch radar model with automated missile defence: notch, chaff, Split-S, kinematic weave and last-ditch pull. Includes a player notch-assist tool and crew-skill scaling.

A Harmony 2 code mod (loaded by the Anchor Chain chainloader) for *Sea Power: Naval Combat in the Missile Age*. It applies to AI and player fixed-wing aircraft on every side.

## Features

**Offence**
- Shoot-look-shoot: no second missile at a target that already has a live missile in flight, so long- and medium-range missiles are not wasted.
- Lead-pursuit intercepts: aim where the target will be, not where it is (arc-aware, velocity-filtered, hot pursuit, pursuit speed management).
- Speed-aware launch-range gate against fast targets.
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

**Crew skill**
- Green to Ultra scales reaction delay, notch accuracy, last-ditch timing and offensive launch range.

**Game-bug fixes**
- Vertical-arc fix: the game's pitch-to-target helper is only correct when flying north/south, so radar missiles were held ("target not in vertical arc") until ~2.7 nm on east/west headings. Replaced with a correct elevation calculation; Sparrows/AMRAAMs now fire at their real range on any heading.

**Diagnostics (off by default)**
- Verbose tracing (pursuit/missile traces, command log, missile-hold log, loadout dump) costs frame time and is disabled. To enable it for a bug report, set `Diagnostics = true` in `BepInEx\config\` (`<plugin guid>.cfg`, created on first run) and restart. The log is capped at 20 MB.

## Patch notes

**v0.9.1**
- Fixed: plain right-click attack on a formation not starting pursuit.
- Fixed: radar missiles held until ~2.7 nm on E/W headings (vertical-arc helper).
- New: radar-on rule for aircraft without Fox-3 weapons.
- Performance: diagnostic patches are no longer installed unless `Diagnostics` is enabled; hostile-contact scans cached; hostile-propagation scan throttled to 1 s; taxi blocker lookup cached; log size capped.

## Requirements

- Sea Power 0.8.7+ (Steam).
- [Anchor Chain](https://steamcommunity.com/sharedfiles/filedetails/?id=3380210757) (Workshop) with BepInEx 5.4.x.
- .NET SDK 10.0 to build from source.

## Installing

**Steam Workshop (recommended):**
1. Subscribe to this mod and to Anchor Chain, and let Steam finish downloading both.
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

v0.9.1 Beta. Tuning is done through log-driven test missions; constants are at the top of each source file.

## License

MIT, see [LICENSE](LICENSE). Not affiliated with the developers of Sea Power. No game assets or code are included in this repository.
