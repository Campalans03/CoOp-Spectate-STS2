# Contributing to Co-op Spectate

Thanks for wanting to help. Bug reports, ideas and pull requests are all welcome.

## Reporting a bug

1. Open **Mod Configuration**, pick **Co-op Spectate** and click **Copy error report** under *Troubleshooting*.
2. Open a [new issue](../../issues) and paste the report.
3. Tell us what you were doing (in combat, in the shop, which turn...), your game version and platform, and whether
   the player you were watching also has the mod. Some features only work when both sides have it.

The report never includes your Steam ID or your Windows user name.

## Suggesting a change

Open an issue before writing a lot of code, so we can agree on it first. This mod only shows what a teammate sees: it
never plays, buys or picks anything for them, and it must not change gameplay (`affects_gameplay` stays `false`).
Ideas that go against that are unlikely to be accepted.

## Building and testing

See [Building](README.md#building) in the README. Testing needs two game instances, so `tools/run-host.ps1` and
`tools/run-client.ps1` start a host and a client on one machine. Please try your change in a real co-op run, in the
screens it touches, before opening a pull request.

## Code

- Everything the mod runs from inside the game's own code (event handlers, Harmony patches, per-frame methods) goes
  through `Safe.Run` (see `SpectateModCode/Safe.cs`), so a failure in the mod only costs the mod's own feature and never
  breaks the game's turns.
- The mod depends on the game's internals, which change between versions. Say which game version you tested with.
- Follow the style of the surrounding code, and keep comments to the reason for something, not what the line does.

## Pull requests

- One change per pull request, with a short description of what it does and how you tested it.
- Keep it small. A large change is easier to review as several.

## License of your contributions

The project is under [CC BY-NC-SA 4.0](LICENSE). By sending a contribution you agree that it is published under that
same license.
