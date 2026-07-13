# Cosmodrill Multiplayer

An experimental cooperative multiplayer mod for Cosmodrill, built with
[MelonLoader](https://github.com/LavaGang/MelonLoader) and the networking components shipped with
the game.

## Features

- Clickable in-game host/join menu with persistent player names.
- Automatic UPnP hosting or manual port configuration.
- Compact nine-character join codes.
- Host-directed save bootstrap, scene travel, and loading.
- Smoothed remote ship movement, rotation, drill animation, and thrusters.
- Shared drill and gadget-bomb tile removal.
- Shared tunnel lighting/fog reveals, collision caches, and removed-tile saves.
- Host-authoritative delivered station resources and spending.
- Connected-player list and detailed traffic/economy/lighting logs.

## Requirements

- Cosmodrill 1.0 for Windows.
- MelonLoader installed in the Cosmodrill directory.
- The same mod version on the host and every guest.

## Installation

1. Close every running Cosmodrill instance.
2. Download the release ZIP.
3. Extract it into the game directory beside `Cosmodrill.exe`.
4. Confirm these files exist:
   - `Mods/CosmodrillMultiplayer.dll`
   - `UserLibs/Open.Nat.dll`
5. Launch the game and open **CO-OP MULTIPLAYER**.

## Hosting a game

1. Load or create the world on the host and wait until the ship can move.
2. Open **CO-OP** and select **AUTOMATIC HOST**.
3. If UPnP is unavailable, use **MANUAL HOST** and forward:
   - selected port: TCP;
   - selected port + 1: UDP;
   - selected port + 2: TCP.
4. Send the displayed nine-character join code to the guest.

The join code contains the host address and port; it is not an account password.
Only share it with players you trust.

## Joining a game

1. Wait until the host is fully inside the world.
2. Open **CO-OP MULTIPLAYER** from the main menu.
3. Enter the nine-character code and select **JOIN WORLD**.
4. The host's saved-world snapshot and current scene will load automatically.

## Shared and personal state

The host owns the authoritative world and delivered station inventory. Tiles,
tunnel lighting, removed-tile save data, and station resource totals are shared.
Ore being carried before delivery remains personal to the player carrying it.

Enemy AI, combat/health, and every progression interaction are not yet fully
authoritative. Back up important saves while testing development releases.

## Portable local test copies

For a copied game directory that must launch independently from Steam, create
`steam_appid.txt` beside `Cosmodrill.exe` containing:

```text
3722660
```

The mod will suppress Steam redirection for that copy and store its saves under
`UserData/Saves` inside the copied directory.

## Logs

Logs are written under `MelonLoader/Logs`. Useful categories include `STATE`,
`TRAFFIC`, `ECONOMY`, `LIGHTING`, `BOOTSTRAP`, `SERVER`, and `CLIENT`.

## Building from source

The project targets .NET Framework 4.7.2 and references assemblies from a local
Cosmodrill installation. Either place this repository directly inside the game
directory or set `COSMODRILL_DIR` to the directory containing `Cosmodrill.exe`.

```powershell
$env:COSMODRILL_DIR = 'D:\SteamLibrary\steamapps\common\Cosmodrill'
dotnet build .\CosmodrillMultiplayer.csproj -c Release
```

Run `scripts/pack.ps1` to build the GitHub release archive under `artifacts`.

## Third-party software

Release archives include Open.NAT 2.1.0 under its MIT license. See
`THIRD_PARTY_NOTICES.md`. No license has yet been selected for this mod's own
source code.
