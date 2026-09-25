<#
.SYNOPSIS
    Launches Slay the Spire 2 as a local co-op host, for testing SpectateMod on one machine.

.DESCRIPTION
    -fastmp makes the game host over ENet on 127.0.0.1:33771 instead of the Steam transport, even while Steam is
    running, and skips the intro logo. Start a multiplayer run here, then run tools\run-client.ps1 and pick
    Multiplayer > Join Friend in it: the client connects to localhost on its own.

    Steam still initialises, which matters because ModManager only scans the Steam Workshop when it does. Without it
    you would lose every Workshop mod, BaseLib included, and SpectateMod would not load at all. A directly launched
    exe can only initialise Steam if steam_appid.txt sits next to it, so this script checks for it.

.EXAMPLE
    .\tools\run-host.ps1
#>
[CmdletBinding()]
param(
    [string] $GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2',

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

$arguments = @('-fastmp') + $ExtraArgs
Write-Host "Launching host: $exe $($arguments -join ' ')"
$process = Start-Process -FilePath $exe -ArgumentList $arguments -WorkingDirectory $GamePath -PassThru
Write-Host "Host started (PID $($process.Id)). Start a multiplayer run, then launch tools\run-client.ps1."
