<#
.SYNOPSIS
    Launches a second Slay the Spire 2 instance that joins the local host, for testing SpectateMod on one machine.

.DESCRIPTION
    Run tools\run-host.ps1 first and start a multiplayer run there. In this instance pick
    Multiplayer > Join Friend: with -fastmp the screen connects straight to 127.0.0.1:33771 without going through
    Steam friends. The window retitles itself "Slay The Spire 2 (Client)" so the two are easy to tell apart.

    Godot reads user:// from the APPDATA environment variable, so this script points it somewhere else. Without that
    both instances share %APPDATA%\SlayTheSpire2 - the same saves, settings and mod config - and whichever quits last
    overwrites the other, which can damage your real profile. The cost is that the client starts on a fresh profile;
    pass -SeedProfile once to copy your real one across first.

    NetIds do not clash: the ENet host is always 1 and a client defaults to 1000. For a third instance pass
    --clientId=1001.

.EXAMPLE
    .\tools\run-client.ps1

.EXAMPLE
    .\tools\run-client.ps1 -SeedProfile
#>
[CmdletBinding()]
param(
    [string] $GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2',

    [string] $UserDataPath = 'C:\sts2_client',

    # Copy your real profile into the client's data folder if it isn't there yet.
    [switch] $SeedProfile,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $ExtraArgs
)

$ErrorActionPreference = 'Stop'

$exe = Join-Path $GamePath 'SlayTheSpire2.exe'
if (-not (Test-Path -LiteralPath $exe)) {
    throw "Could not find SlayTheSpire2.exe at '$exe'. Pass -GamePath if your Steam library is elsewhere."
}

$appIdFile = Join-Path $GamePath 'steam_appid.txt'
if (-not (Test-Path -LiteralPath $appIdFile)) {
    Write-Warning "steam_appid.txt is missing from the game folder."
    Write-Warning "Without it a directly launched exe cannot initialise Steam, so no Workshop mod loads - including BaseLib, which SpectateMod depends on."
    Write-Warning "Create it with:  Set-Content -LiteralPath '$appIdFile' -Value '2868840' -NoNewline"
}

if (-not (Test-Path -LiteralPath $UserDataPath)) {
    New-Item -ItemType Directory -Path $UserDataPath -Force | Out-Null
}

$clientProfile = Join-Path $UserDataPath 'SlayTheSpire2'
if ($SeedProfile -and -not (Test-Path -LiteralPath $clientProfile)) {
    $realProfile = Join-Path $env:APPDATA 'SlayTheSpire2'
    if (Test-Path -LiteralPath $realProfile) {
        Write-Host "Seeding the client profile from '$realProfile'."
        Copy-Item -LiteralPath $realProfile -Destination $clientProfile -Recurse
    }
    else {
        Write-Warning "No profile found at '$realProfile'; the client will start fresh."
    }
}

$arguments = @('-fastmp') + $ExtraArgs
$originalAppData = $env:APPDATA
try {
    $env:APPDATA = $UserDataPath
    Write-Host "Launching client with user data in '$UserDataPath': $exe $($arguments -join ' ')"
    $process = Start-Process -FilePath $exe -ArgumentList $arguments -WorkingDirectory $GamePath -PassThru
    Write-Host "Client started (PID $($process.Id)). Pick Multiplayer > Join Friend to connect to the host."
}
finally {
    # Only the launched process needed the redirect; leave this shell's own APPDATA alone.
    $env:APPDATA = $originalAppData
}
