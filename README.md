# Graveyard Keeper 2: Tomb Many Keepers

An unofficial GYK2 mod, porting features from
[GYK: Back From the Grave](https://github.com/ZlordHUN/GYK-Back-From-The-Grave) and [Graveyard Keeper Multiplayer](https://github.com/Zonda001/graveyard-keeper-multiplayer).
**Version 0.1.**

The functionality of both mods will be ported into GYK2. Players will be able to choose between the coop styles of both mods during Lobby setup.

## Features

- Multiplayer button that starts a campaign with two keepers in the opening scene.
- Widescreen and ultrawide support, animated menu artwork and resolutions through 8K.
- Mods menu listing installed plugins.
- Left-click or Space to skip startup logos.

The second keeper is a prototype: networking, second-player controls and save
persistence are not implemented yet.

## Install

Requires **Graveyard Keeper 2 1.004.3** (Windows Mono) and **BepInEx 5.4.23.5 x64**.
Tested through Proton on Linux.

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
