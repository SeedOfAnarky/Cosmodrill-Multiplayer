# Cosmodrill Multiplayer v0.5.12

This release turns the prototype into a more complete host-authoritative co-op
build, with shared world changes, station resources, enemy deaths, persistent
players, reconnect handling, and clearer teammate navigation.

## Highlights since v0.5.6

- Synchronizes deaths for every currently identified enemy family: normal
  enemies, pirate-station guns, and worm bosses.
- Captures the host's live world before a guest joins, including previously dug
  tiles that have not reached the next normal autosave.
- Transfers world snapshots over bounded framed TCP with length and SHA-256
  validation, avoiding the legacy EZNet packet-split crash.
- Keeps guests connected if they join while the host save is still loading, then
  sends the authoritative world automatically when it becomes ready.
- Gives every installation a persistent player ID and keeps personal cargo,
  stats, upgrades, gadgets, and last position per host save.
- Replaces stale state sockets and supports graceful disconnect/reconnect without
  replaying the world load when the same world is already active.
- Adds an optional teammate indicator with name, direction, and distance. It is
  hidden while the remote ship is visible and appears only when they are fully
  off-screen.
- Splits world, economy, enemy, player-session, and locator synchronization into
  separate modules and expands bootstrap, reconnect, economy, and traffic logs.

## Working in this version

- Clickable host/join menus, custom player names, connected-player list, and
  compact nine-character join codes.
- Automatic UPnP hosting plus manual three-port hosting.
- Host-controlled save bootstrap and scene travel.
- Smoothed remote ship movement, rotation, drilling state, turning jets, and
  thruster trail.
- Shared drilled and bombed tiles, collision updates, removed-tile saves, tunnel
  lighting, and fog reveal.
- Host-authoritative delivered station inventory, guest deposits, and spending.
- Shared enemy deaths across all currently identified enemy types.
- Persistent reconnect profiles and teammate off-screen locator.

## Known limitations

- Enemy movement, attacks, and partial damage are not yet fully
  host-authoritative; confirmed deaths are synchronized.
- The mod uses direct IP networking and requires the host's TCP, UDP, and state
  TCP ports to be reachable. Automatic UPnP depends on router support.
- This remains an experimental mod. Back up important saves and use the same mod
  version on every player.

## Installation

Install MelonLoader, close every Cosmodrill instance, and extract
`Cosmodrill-Multiplayer-v0.5.12.zip` beside `Cosmodrill.exe`. The archive installs
the mod DLL under `Mods` and Open.NAT under `UserLibs`.
