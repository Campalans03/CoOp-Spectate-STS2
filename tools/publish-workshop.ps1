<#
.SYNOPSIS
    Builds SpectateMod and uploads it to the Steam Workshop through SteamCMD.

.DESCRIPTION
    1. Runs dotnet publish, which rebuilds the .dll and re-exports the .pck into the game's mods folder.
    2. Copies SpectateMod.dll, .json and .pck into <WorkRoot>\content\SpectateMod, the folder that gets uploaded.
       The game reads Workshop items recursively, so the mod sits in its own subfolder the way BaseLib's does.
    3. Makes a preview image under the Workshop's 1 MB limit from SpectateMod\mod_image.png.
    4. Downloads SteamCMD into <WorkRoot>\steamcmd the first time.
    5. Writes item.vdf and runs workshop_build_item.

    The item id lives in tools\workshop-item-id.txt. While that file is missing the upload creates a new item; once
    it holds an id, every run updates that item instead. Title, description and visibility are only sent when the
    item is created, so edits made on the Workshop page afterwards are never overwritten. Pass -UpdateDescription to
    send the title and description on an update too, when the text in this script should replace the page's.

    SteamCMD asks for your password and Steam Guard code on the first login and remembers them afterwards.

.EXAMPLE
    .\tools\publish-workshop.ps1 -StageOnly

.EXAMPLE
    .\tools\publish-workshop.ps1 -Username myaccount -ChangeNote "Fixed the star counter"

.EXAMPLE
    .\tools\publish-workshop.ps1 -Username myaccount -ChangeNote "Spectate the whole run" -UpdateDescription
#>
[CmdletBinding()]
param(
    # Steam account that owns the item. Not needed with -StageOnly.
    [string] $Username,

    [string] $ChangeNote = 'First release',

    # Only for a new item: 0 public, 1 friends only, 2 private, 3 unlisted.
    [ValidateSet(0, 1, 2, 3)]
    [int] $Visibility = 2,

    [string] $WorkRoot = 'C:\MegaDotMoods\workshop\SpectateMod',

    [string] $GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2',

    # Prepare the content folder, preview, SteamCMD and item.vdf, but don't upload.
    [switch] $StageOnly,

    # Use whatever is already in the mods folder instead of rebuilding.
    [switch] $SkipBuild,

    # Also send the title and the description below when updating an existing item. Off by default so edits made on
    # the Workshop page aren't overwritten; pass it when the text here is the one that should be on the page.
    [switch] $UpdateDescription
)

$ErrorActionPreference = 'Stop'

$appId = '2868840'
$modName = 'SpectateMod'
$repoRoot = Split-Path -Parent $PSScriptRoot
$idFile = Join-Path $PSScriptRoot 'workshop-item-id.txt'
$modsFolder = Join-Path $GamePath "mods\$modName"
$contentFolder = Join-Path $WorkRoot "content\$modName"
$previewFile = Join-Path $WorkRoot 'preview.jpg'
$vdfFile = Join-Path $WorkRoot 'item.vdf'
$steamCmdFolder = Join-Path $WorkRoot 'steamcmd'
$steamCmd = Join-Path $steamCmdFolder 'steamcmd.exe'

if (-not $StageOnly -and -not $Username) {
    throw 'Pass -Username with the Steam account to upload with, or -StageOnly to only prepare the files.'
}

# 1. Build.
if (-not $SkipBuild) {
    Write-Host 'Building and exporting the .pck...'
    dotnet publish (Join-Path $repoRoot "$modName.csproj") | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }
}

# 2. Content folder. Emptied first so nothing stale from an older build rides along.
if (Test-Path -LiteralPath $contentFolder) {
    Remove-Item -LiteralPath $contentFolder -Recurse -Force
}
New-Item -ItemType Directory -Path $contentFolder -Force | Out-Null
foreach ($file in "$modName.dll", "$modName.json", "$modName.pck") {
    $source = Join-Path $modsFolder $file
    if (-not (Test-Path -LiteralPath $source)) {
        throw "Missing '$source'. Build the mod first, or drop -SkipBuild."
    }
    Copy-Item -LiteralPath $source -Destination $contentFolder
}
$version = (Get-Content -LiteralPath (Join-Path $contentFolder "$modName.json") -Raw | ConvertFrom-Json).version
Write-Host "Staged $modName $version in '$contentFolder'."

# 3. Preview image.
Add-Type -AssemblyName System.Drawing
$source = [System.Drawing.Image]::FromFile((Join-Path $repoRoot "$modName\mod_image.png"))
try {
    $size = 640
    $bitmap = New-Object System.Drawing.Bitmap $size, $size
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.DrawImage($source, 0, 0, $size, $size)
        $graphics.Dispose()
        $jpeg = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object MimeType -eq 'image/jpeg'
        $quality = New-Object System.Drawing.Imaging.EncoderParameters 1
        $quality.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter ([System.Drawing.Imaging.Encoder]::Quality), 90L
        $bitmap.Save($previewFile, $jpeg, $quality)
    }
    finally {
        $bitmap.Dispose()
    }
}
finally {
    $source.Dispose()
}
Write-Host "Preview image: '$previewFile' ($([math]::Round((Get-Item $previewFile).Length / 1KB)) KB)."

# 4. SteamCMD.
if (-not (Test-Path -LiteralPath $steamCmd)) {
    Write-Host 'Downloading SteamCMD...'
    New-Item -ItemType Directory -Path $steamCmdFolder -Force | Out-Null
    $zip = Join-Path $steamCmdFolder 'steamcmd.zip'
    Invoke-WebRequest -Uri 'https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip' -OutFile $zip
    Expand-Archive -LiteralPath $zip -DestinationPath $steamCmdFolder -Force
    Remove-Item -LiteralPath $zip
    # The first run only updates SteamCMD itself, and exits with 7 once it has, which is not a failure.
    & $steamCmd +quit | Out-Host
    $global:LASTEXITCODE = 0
}

# 5. item.vdf.
$itemId = '0'
if (Test-Path -LiteralPath $idFile) {
    $itemId = (Get-Content -LiteralPath $idFile -Raw).Trim()
}
$isNew = $itemId -eq '0'

# Line breaks stay literal: a quoted VDF value may span lines, whereas a "\n" escape is not reliably expanded.
function Format-VdfValue([string] $value) {
    # Steam's reader ends the value at a double quote even when it is escaped as \", silently cutting the rest of a
    # description off, so there is no way to send one.
    if ($value.Contains('"')) {
        throw "A Workshop text contains a double quote, which Steam would cut the text off at: $value"
    }
    return '"' + ($value -replace '\\', '\\' -replace "`r`n", "`n") + '"'
}

$description = @"
Co-op Spectate lets you watch your teammates as if you were sitting next to them, for the whole run: their turn in combat, their rewards, the shop, events, and every card choice in between, live.

[h2]What you see[/h2]
[list]
[*][b]Their combat[/b]: their hand in place of yours, the card they're hovering raised, every card they pick up and aim with the targeting arrow and reticle, and their piles, energy and stars.
[*][b]Their choices[/b]: discarding or exhausting from their hand, picking one of three cards, upgrading, removing or transforming cards from their deck, choosing from their draw or discard pile. You see the cards they mark before they confirm.
[*][b]Their screens outside combat[/b]: their rewards, their card rewards, their shop with its prices and what they've already bought, and their own events, Neow included.
[*][b]Their top bar[/b]: relics, potions, gold, HP and portrait.
[*][b]Their cursor[/b] on top of all of it, so you can see what they're pointing at.
[/list]
Everything you see is the game's own screen, filled with their cards, so it looks exactly like theirs.

[h2]How to spectate[/h2]
[list]
[*][b]Player boxes[/b] on the right edge of the screen, there for the whole run. [b]Click[/b] one to watch that player until you click again (click your own to come back).
[*][b]End your turn[/b] and you automatically follow a teammate who is still playing.
[*][b]Click a teammate[/b] in combat, or the eye next to their icon in the party list.
[*][b]Died?[/b] You keep watching whoever is still alive instead of staring at your corpse.
[*]Your own turn, your own card choices, and opening your own map or deck always bring you back to your screen.
[/list]

[h2]Draw on their screen[/h2]
While spectating, [b]hold right click and drag[/b] to draw on your teammate's screen, anywhere: in combat, in the shop, on their rewards. Point at the enemy to hit, the card to take, or the relic to buy.

Every player draws in their own colour, and the lines fade away on their own a moment later.

[h2]Good to know[/h2]
[list]
[*][b]Look, don't touch[/b]: nothing you see can be played, bought or picked. Your teammate stays in full control.
[*][b]Doesn't affect gameplay[/b]: you can join lobbies with players who don't have it, and most of the spectating works on your own. A few things only come through when your teammate has the mod too: drawing, the cards they mark before confirming, their shop, and the pack they open with Scroll Boxes.
[*][b]Fully configurable[/b]: every feature can be turned on or off in the mod settings.
[*]Requires [url=https://steamcommunity.com/sharedfiles/filedetails/?id=3737335127]BaseLib[/url].
[/list]

[h2]Found a bug?[/h2]
The mod can put together an error report for you, so there's no need to dig through log files.
[olist]
[*]Open [b]Mod Configuration[/b] (on the main menu, or in Settings) and pick [b]Co-op Spectate[/b].
[*]At the bottom, under [b]Troubleshooting[/b], click [b]Copy error report[/b]. The button briefly changes to Copied!
[*]Paste it in a comment below, and tell me what you were doing when it went wrong (in combat, in the shop, on which turn...). If it's too long for a comment, paste it on [url=https://pastebin.com]pastebin.com[/url] and post the link instead.
[/olist]
The report covers the game session you're in and the one before it, so you can copy it right after the bug or after restarting the game once. It contains the game and mod versions, the list of your other mods, and the errors from those sessions. It never includes your Steam ID or your Windows user name.
"@

$lines = @(
    '"workshopitem"'
    '{'
    "  `"appid`" `"$appId`""
    "  `"publishedfileid`" `"$itemId`""
    "  `"contentfolder`" $(Format-VdfValue $contentFolder)"
    "  `"previewfile`" $(Format-VdfValue $previewFile)"
    "  `"changenote`" $(Format-VdfValue "$version - $ChangeNote")"
)
if ($isNew) {
    $lines += "  `"visibility`" `"$Visibility`""
}
if ($isNew -or $UpdateDescription) {
    $lines += "  `"title`" `"Co-op Spectate`""
    $lines += "  `"description`" $(Format-VdfValue $description)"
}
$lines += '}'
Set-Content -LiteralPath $vdfFile -Value $lines -Encoding UTF8
Write-Host "Wrote '$vdfFile' ($(if ($isNew) { 'new item' } else { "updating item $itemId" }))."

if ($StageOnly) {
    Write-Host 'Stage only: nothing uploaded.'
    return
}

# 6. Upload. SteamCMD talks to the console directly, so its password and Steam Guard prompts come through.
& $steamCmd +login $Username +workshop_build_item $vdfFile +quit
if ($LASTEXITCODE -ne 0) {
    throw "SteamCMD failed with exit code $LASTEXITCODE."
}

if ($isNew) {
    # SteamCMD writes the new item's id back into the vdf.
    $written = Select-String -LiteralPath $vdfFile -Pattern '"publishedfileid"\s+"(\d+)"' |
        ForEach-Object { $_.Matches[0].Groups[1].Value } | Select-Object -First 1
    if ($written -and $written -ne '0') {
        Set-Content -LiteralPath $idFile -Value $written -NoNewline
        Write-Host "Created item $written; saved its id to '$idFile'. Commit that file so later runs update it."
        Write-Host "https://steamcommunity.com/sharedfiles/filedetails/?id=$written"
    }
    else {
        Write-Warning "Could not read the new item's id back. Copy it from SteamCMD's output into '$idFile' before the next upload, or it will create another item."
    }
}
else {
    Write-Host "Updated https://steamcommunity.com/sharedfiles/filedetails/?id=$itemId"
}
