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
    item is created, so edits made on the Workshop page afterwards are never overwritten.

    SteamCMD asks for your password and Steam Guard code on the first login and remembers them afterwards.

.EXAMPLE
    .\tools\publish-workshop.ps1 -StageOnly

.EXAMPLE
    .\tools\publish-workshop.ps1 -Username myaccount -ChangeNote "Fixed the star counter"
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
    [switch] $SkipBuild
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
    return '"' + ($value -replace '\\', '\\' -replace '"', '\"' -replace "`r`n", "`n") + '"'
}

$description = @"
Watch your co-op teammates play their turn instead of staring at your own board.

[h2]Spectating[/h2]
[list]
[*]Finish your turn, or click a teammate, to see their hand, draw/discard/exhaust piles, energy, stars, relics and potions.
[*]See the card they are hovering, the card they pick up and the arrow they aim with, as they do it.
[*]Rest your mouse on their cards to read them at your own pace.
[*]Quick-peek boxes on the right edge of the screen: hover one to glance at that player's hand without switching.
[*]Automatically follows someone still playing when you die or end your turn.
[/list]

[h2]Draw on their screen[/h2]
While spectating, hold the right mouse button and drag to draw on your teammate's screen, just like on the map. Each player draws in their own colour and the lines wipe themselves away after a moment.

[h2]Notes[/h2]
[list]
[*]Everything you see is read-only: you can never play a teammate's cards or use their potions.
[*]Requires BaseLib.
[*]Spectating works on your own. To see what someone draws, they need the mod too.
[*]Every option can be turned off in the mod settings.
[/list]
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
