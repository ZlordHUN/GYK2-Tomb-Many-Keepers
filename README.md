# Graveyard Keeper 2: Tomb Many Keepers

An unofficial GYK2 mod, porting features from
[GYK: Back From the Grave](https://github.com/ZlordHUN/GYK-Back-From-The-Grave) and [Graveyard Keeper Multiplayer](https://github.com/Zonda001/graveyard-keeper-multiplayer).
**Version 0.1.**

## Features

- LAN co-op for up to four players: lobbies, new or saved campaigns, late joining and shared loading screens.
- Configurable player limits, saved host settings, Steam avatars and lobby chat.
- Persistent characters with personal inventories, points, talents and buffs.
- Multiplayer prison opening; rescue the other keepers with the pickaxe.
- Shared quests, unlocks and reputation, with story rewards for each player.
- Synced world objects, drops, NPC movement, time and weather; shared chests and crafting stations.
- Shared cutscenes, speech, dialogue choices, wisps and lighting within the same scene.
- Individual sleeping; time advances quickly only when everyone sleeps.
- Widescreen and ultrawide support, animated menu artwork and resolutions through 8K.
- Mods menu listing installed plugins.
- Left-click or Space to skip startup logos.

Choose **Multiplayer → Host Game**, select a campaign, then **Next** to set
the player limit (2–4). Choose **Next** again to open the lobby. Guests use
**Join Game** and **Ready**; the host selects **Start Game** once everyone
present is ready. Late joiners select **Ready → Join Game**.

Chat with **Enter** or **Send**. Visibility and Cheats are saved placeholders:
**Private/Friends does not restrict LAN access**. Keeper customization, invites
and lobby codes are not available yet.

Co-op is an early prototype. The host saves everyone's characters; keep the
save's `.tmk` and `.tmkhost` sidecars when backing up or moving a campaign.
Workers, player trading and dungeon fighting levels are not supported yet.
The session ends when the host leaves.

## Potential issues

- Overlapping station access may duplicate shared materials.
- Shared-account reconnects may select the wrong character.
- Concurrent world updates may lose changes or desynchronize players.

## Install

Targets **Graveyard Keeper 2** (Windows Mono, Steam build **25533739**)
and **BepInEx 5.4.23.5 x64**.
Use matching game and mod builds for all players. LAN play requires Steam running
and UDP ports **34271** (discovery) and **34272** (game connection).

Close the game and extract the [nightly release](https://github.com/ZlordHUN/GYK2-Tomb-Many-Keepers/releases/tag/nightly)
into the game folder. Keep only one copy of the plugin; remove any old
`GYK2.Multiplayer.dll`. Remove `GYK2.TombManyKeepers.dll` to uninstall.

## Build

Install the **.NET 8 SDK**. Copy `build/Local.props.example` to `build/Local.props`
and set your game and BepInEx paths, then run:

```sh
dotnet build GYK2.TombManyKeepers.csproj -c Release
```

Output: `bin/Release/netstandard2.1/GYK2.TombManyKeepers.dll`.
Build and test locally, then commit the DLL with your source changes. Nightlies
package that DLL after each push to the default branch.

Licensed under [MIT](LICENSE).
