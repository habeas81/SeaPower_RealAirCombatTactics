<#
.SYNOPSIS
    Builds the mod's Workshop content folder and uploads it to Steam Workshop via steamcmd,
    without needing to launch the game or Steam client UI.

.DESCRIPTION
    Run the "build" task first (or let the "Build and Publish to Workshop" VS Code task do it for you),
    which produces dist\RealAirCombatTactics\ via the project's PostBuild target. This script then:
      1. Downloads a private copy of steamcmd into tools\steamcmd\ if it isn't there yet (gitignored).
      2. Writes a workshop_item.vdf pointing at dist\RealAirCombatTactics\.
      3. Runs steamcmd +login <you> +workshop_build_item ... +quit.

    The first run will prompt interactively in the terminal for your Steam password and, if MFA is
    enabled (recommended), a Steam Guard code from email or the mobile app. Nothing is cached or
    written outside tools\steamcmd\, and no password is ever stored in this script or in git.

.PARAMETER SteamLogin
    Your Steam account login name. Defaults to $env:SP_STEAM_LOGIN if set, otherwise prompts.

.PARAMETER ChangeNote
    The Workshop changelog entry for this update. Defaults to the latest git commit subject.

.EXAMPLE
    .\scripts\Publish-Workshop.ps1
    .\scripts\Publish-Workshop.ps1 -ChangeNote "v0.9.3: cease-fire fix, duplicate missile guard, Split-S roll fix"
#>
param(
    [string]$SteamLogin = $env:SP_STEAM_LOGIN,
    [string]$ChangeNote
)

$ErrorActionPreference = "Stop"

$RepoRoot    = Split-Path -Parent $PSScriptRoot
$AppId       = 1286220       # Sea Power
$WorkshopId  = 3827346397    # Real Air Combat Tactics published file id
$ContentDir  = Join-Path $RepoRoot "dist\RealAirCombatTactics"
$ToolsDir    = Join-Path $RepoRoot "tools\steamcmd"
$SteamCmdExe = Join-Path $ToolsDir "steamcmd.exe"
$VdfPath     = Join-Path $ToolsDir "workshop_item.vdf"

if (-not (Test-Path $ContentDir) -or (Get-ChildItem $ContentDir -ErrorAction SilentlyContinue | Measure-Object).Count -eq 0) {
    throw "No build output found at $ContentDir. Run the 'build' task first (Ctrl+Shift+B)."
}

if (-not $SteamLogin) {
    $SteamLogin = Read-Host "Steam login name"
}

if (-not $ChangeNote) {
    $ChangeNote = (git -C $RepoRoot log -1 --pretty=%s) 2>$null
    if (-not $ChangeNote) {
        $ChangeNote = Read-Host "Workshop changelog note for this update"
    }
}

# --- Fetch steamcmd if this is the first run ---
if (-not (Test-Path $SteamCmdExe)) {
    Write-Host "steamcmd not found; downloading a private copy into $ToolsDir ..." -ForegroundColor Cyan
    New-Item -ItemType Directory -Path $ToolsDir -Force | Out-Null
    $zipPath = Join-Path $ToolsDir "steamcmd.zip"
    Invoke-WebRequest -Uri "https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip" -OutFile $zipPath
    Expand-Archive -Path $zipPath -DestinationPath $ToolsDir -Force
    Remove-Item $zipPath
}

# --- Write the workshop item script ---
# Quote the content folder path; steamcmd's VDF parser handles embedded spaces fine inside quotes.
$vdfContent = @"
"workshopitem"
{
    "appid"            "$AppId"
    "publishedfileid"  "$WorkshopId"
    "contentfolder"    "$ContentDir"
    "changenote"       "$ChangeNote"
}
"@
Set-Content -Path $VdfPath -Value $vdfContent -Encoding ASCII

Write-Host "Publishing '$ChangeNote' to Workshop item $WorkshopId (app $AppId)..." -ForegroundColor Cyan
Write-Host "Content folder: $ContentDir" -ForegroundColor DarkGray

# No password is passed: steamcmd prompts for it (and any Steam Guard code) interactively.
& $SteamCmdExe +login $SteamLogin +workshop_build_item $VdfPath +quit
$exitCode = $LASTEXITCODE

if ($exitCode -ne 0) {
    throw "steamcmd exited with code $exitCode. Check the output above for the failure reason."
}

Write-Host "Workshop upload complete." -ForegroundColor Green
