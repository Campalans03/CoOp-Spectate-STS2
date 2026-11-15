# Co-op Spectate

A [Slay the Spire 2](https://store.steampowered.com/app/2868840) mod that lets you watch your co-op teammates as if you
were sitting next to them, for the whole run: their turn in combat, their rewards, the shop, events and every card
choice in between, live.

Also on the [Steam Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3808076656).

## What you see

- **Their combat**: their hand in place of yours, the card they're hovering raised, every card they pick up and aim,
  and their piles, energy and stars. Cards move like yours do: drawn from the pile, discarded, exhausted, shuffled.
- **Their choices**: discarding or exhausting from their hand, picking one of three cards, upgrading, removing or
  transforming cards, choosing from their draw or discard pile.
- **Their screens outside combat**: rewards, card rewards, shop (prices and what they've bought), events (Neow and the
  Crystal Sphere included) and their options at the rest site.
- **Their top bar**: relics, potions, gold, HP, portrait and deck.
- **Their cursor** on top of all of it.

Everything shown is the game's own screen filled with their cards, so it looks exactly like theirs.

## How to spectate

- Click a **player box** on the right edge of the screen to watch that player; click it again to stop (click your own
  to come back).
- End your turn and you automatically follow a teammate who is still playing.
- If you die you keep watching whoever is still alive.
- Hold **right click** and drag to draw on the watched player's screen. Lines fade on their own.
- The forge's **View Upgrades** box also appears on card rewards and on the draw, discard and exhaust piles.

Look, don't touch: nothing you see can be played, bought or picked. The mod doesn't affect gameplay, so you can play
with people who don't have it, although a few things (drawing, their shop, the Crystal Sphere, cards they mark before
confirming) only come through when your teammate has it too. Every feature can be turned off in the mod settings.

## Install

Requires [BaseLib](https://steamcommunity.com/sharedfiles/filedetails/?id=3737335127) and Slay the Spire 2 v0.107.0 or
newer.

- **Steam Workshop**: subscribe to the item above.
- **Manually**: download the latest zip from [Releases](../../releases) and extract it into
  `<Slay the Spire 2>/mods/SpectateMod/`, so that `SpectateMod.dll`, `SpectateMod.json` and `SpectateMod.pck` sit
  together in that folder.

## Reporting a bug

Open **Mod Configuration**, pick **Co-op Spectate** and click **Copy error report** under *Troubleshooting*, then paste
it in a [new issue](../../issues) along with what you were doing. The report holds the game and mod versions, your list
of mods and the errors of the current and previous sessions. It never includes your Steam ID or your Windows user name.

## Building

You need the .NET 9 SDK, Slay the Spire 2 installed, and MegaDot / Godot 4.5.1 (mono) to export the `.pck`.

1. Set `GodotPath` in `Directory.Build.props`, and `Sts2Path` if the game isn't found automatically.
2. Run `dotnet publish SpectateMod.csproj`. The `.dll` and `.pck` land in the game's `mods/SpectateMod` folder.

`tools/` has scripts to run a host and a client on one machine for testing, and to publish to the Workshop.

## License

[MIT](LICENSE)
