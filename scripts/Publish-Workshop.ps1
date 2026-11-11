<#
.SYNOPSIS
    Builds the mod's Workshop content folder and uploads it to Steam Workshop via steamcmd,
    without needing to launch the game or Steam client UI.

.DESCRIPTION
    This script is shared across any Sea Power (or other Steam Workshop) mod repo. It reads
    per-repo settings from scripts\publish.json next to this script (NOT under workshop\, since
    that folder is copied wholesale into the shipped mod content):

        {
          "appId": 1286220,
          "workshopItemId": 3827346397,
          "contentFolder": "dist/RealAirCombatTactics"
        }

    - appId:          the Steam app ID of the game the mod is for.
    - workshopItemId: the Workshop published file ID to update. Use 0 to publish a brand-new item
                       (steamcmd will print the new ID; copy it back into publish.json afterwards).
    - contentFolder:  path, relative to the repo root, that contains the finished mod files
                       (already produced by the project's PostBuild target; run the "build" task first).

    Run the "build" task first (or let the "Build and Publish to Workshop" VS Code task do it for
    you). This script then:
      1. Uses a steamcmd install shared across all mod repos on this machine
         (%LOCALAPPDATA%\SeaPowerModTools\steamcmd), downloading it on first use anywhere.
      2. Writes a workshop_item.vdf pointing at the repo's content folder.
      3. Runs steamcmd +login <you> +workshop_build_item ... +quit.

    The first run (per machine) will prompt interactively in the terminal for your Steam password
    and, if MFA is enabled (recommended), a Steam Guard code. steamcmd caches the session under the
    shared tools folder, so later runs usually won't re-prompt. No password is ever stored in this
    script or in git.

.PARAMETER SteamLogin
    Your Steam account login name. Defaults to $env:SP_STEAM_LOGIN if set, otherwise prompts.

.PARAMETER ChangeNote
    The Workshop changelog entry for this update. Defaults to the contents of scripts\changenote.txt
    (edit it before each release, BBCode allowed), then to the latest git commit subject.

.EXAMPLE
    .\scripts\Publish-Workshop.ps1
    .\scripts\Publish-Workshop.ps1 -ChangeNote "v0.9.3: cease-fire fix, duplicate missile guard, Split-S roll fix"
#>
param(
    [string]$SteamLogin = $env:SP_STEAM_LOGIN,
    [string]$ChangeNote
)

$ErrorActionPreference = "Stop"

$RepoRoot      = Split-Path -Parent $PSScriptRoot
$ConfigPath    = Join-Path $PSScriptRoot "publish.json"
$SharedTools   = Join-Path $env:LOCALAPPDATA "SeaPowerModTools\steamcmd"
$SteamCmdExe   = Join-Path $SharedTools "steamcmd.exe"

if (-not (Test-Path $ConfigPath)) {
    throw "No scripts\publish.json found at $ConfigPath. Create one with appId, workshopItemId and contentFolder (see this script's header comment for the format)."
}

$Config = Get-Content $ConfigPath -Raw | ConvertFrom-Json
foreach ($required in @("appId", "workshopItemId", "contentFolder")) {
    if (-not ($Config.PSObject.Properties.Name -contains $required)) {
        throw "workshop\publish.json is missing required field '$required'."
    }
}

$AppId      = $Config.appId
$WorkshopId = $Config.workshopItemId
$ContentDir = Join-Path $RepoRoot ($Config.contentFolder -replace '/', '\')
$VdfPath    = Join-Path $SharedTools ("workshop_item_{0}.vdf" -f $AppId)

if (-not (Test-Path $ContentDir) -or (Get-ChildItem $ContentDir -ErrorAction SilentlyContinue | Measure-Object).Count -eq 0) {
    throw "No build output found at $ContentDir. Run the 'build' task first (Ctrl+Shift+B)."
}

if ($WorkshopId -eq 0) {
    Write-Host "workshopItemId is 0: this will publish a BRAND NEW Workshop item. Copy the new published file id steamcmd reports back into $ConfigPath afterwards." -ForegroundColor Yellow
}

if (-not $SteamLogin) {
    $SteamLogin = Read-Host "Steam login name"
}

$ChangeNoteFile = Join-Path $PSScriptRoot "changenote.txt"
if (-not $ChangeNote -and (Test-Path $ChangeNoteFile)) {
    $ChangeNote = (Get-Content $ChangeNoteFile -Raw).Trim()
}

if (-not $ChangeNote) {
    $ChangeNote = (git -C $RepoRoot log -1 --pretty=%s) 2>$null
    if (-not $ChangeNote) {
        $ChangeNote = Read-Host "Workshop changelog note for this update"
    }
}

# --- Fetch steamcmd into the shared location if this is the first use on this machine ---
if (-not (Test-Path $SteamCmdExe)) {
    Write-Host "steamcmd not found; downloading a shared copy into $SharedTools ..." -ForegroundColor Cyan
    New-Item -ItemType Directory -Path $SharedTools -Force | Out-Null
    $zipPath = Join-Path $SharedTools "steamcmd.zip"
    Invoke-WebRequest -Uri "https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip" -OutFile $zipPath
    Expand-Archive -Path $zipPath -DestinationPath $SharedTools -Force
    Remove-Item $zipPath
}

# --- Write the workshop item script ---
# Quote the content folder path; steamcmd's VDF parser handles embedded spaces fine inside quotes.
# VDF values can't contain raw double quotes or line breaks; escape them.
$ChangeNoteVdf = ($ChangeNote -replace '"', "'") -replace '\r?\n', '\n'

function ConvertTo-VdfValue([string]$Text) { ($Text -replace '"', "'") -replace '\r?\n', '\n' }

# Optional metadata (needed when creating a new item; on updates it overwrites what is on the Workshop page):
# title, descriptionFile (path relative to the repo), previewFile (relative), visibility (0 public, 1 friends, 2 private).
$extra = ""
if ($Config.PSObject.Properties.Name -contains "title") { $extra += "    `"title`"          `"$(ConvertTo-VdfValue $Config.title)`"`r`n" }
if ($Config.PSObject.Properties.Name -contains "descriptionFile") {
    $descPath = Join-Path $RepoRoot ($Config.descriptionFile -replace '/', '\')
    $extra += "    `"description`"    `"$(ConvertTo-VdfValue ((Get-Content $descPath -Raw).Trim()))`"`r`n"
}
if ($Config.PSObject.Properties.Name -contains "previewFile") {
    $extra += "    `"previewfile`"    `"$(Join-Path $RepoRoot ($Config.previewFile -replace '/', '\'))`"`r`n"
}
if ($Config.PSObject.Properties.Name -contains "visibility") { $extra += "    `"visibility`"     `"$($Config.visibility)`"`r`n" }

$vdfContent = @"
"workshopitem"
{
    "appid"            "$AppId"
    "publishedfileid"  "$WorkshopId"
    "contentfolder"    "$ContentDir"
    "changenote"       "$ChangeNoteVdf"
$extra}
"@
Set-Content -Path $VdfPath -Value $vdfContent -Encoding ASCII

Write-Host "Publishing '$ChangeNote' to Workshop item $WorkshopId (app $AppId)..." -ForegroundColor Cyan
Write-Host "Content folder: $ContentDir" -ForegroundColor DarkGray

# No password is passed: steamcmd prompts for it (and any Steam Guard code) interactively.
& $SteamCmdExe +login $SteamLogin +workshop_build_item $VdfPath +quit
$exitCode = $LASTEXITCODE

# Exit code 7 is returned by steamcmd after a successful upload when it unloads the Steam API.
if ($exitCode -eq 7) {
    Write-Host "steamcmd exited with code 7 (normally benign after an upload). Confirm 'Committing update...Success' appears above." -ForegroundColor Yellow
}
elseif ($exitCode -ne 0) {
    throw "steamcmd exited with code $exitCode. Check the output above for the failure reason."
}

Write-Host "Workshop upload complete." -ForegroundColor Green
