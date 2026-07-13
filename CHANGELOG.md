# Changelog

## 0.5.12

- Hide teammate nameplates while any visible part of the remote ship is inside
  the camera viewport.
- Show the name, direction, and distance marker only after that teammate moves
  completely off-screen.

## 0.5.11

- Keep early joiners connected while the host finishes loading its selected
  world, then automatically release the reconnect state and world snapshot.
- Use the framed state connection as a healthy client transport signal instead
  of disconnecting when EZNet's legacy `connected` flag drops prematurely.
- Track the state client's real socket lifecycle and give interrupted transports
  a longer automatic-reconnect window.

## 0.5.10

- Move authoritative world snapshots off EZNet's legacy packet splitter and
  onto the bounded, framed state TCP channel.
- Identify the client's loaded host/save when its state socket reconnects so
  the world snapshot and scene intro are not replayed unnecessarily.
- Add explicit framed-bootstrap progress, checksum, tile-count, and transport
  failure logging.

## 0.5.9

- Give every installation a persistent player ID and replace stale duplicate
  state sockets when that player reconnects.
- Retain reconnect positions per host save slot and restore them after the host
  world has finished loading.
- Save player-owned carried cargo, ship stats, upgrades, and gadget setup in a
  per-player/per-host profile without replacing shared world or station state.
- Add graceful disconnect records, periodic crash-resistant profile saves, and
  clearer reconnect logging.
- Add clickable teammate locator markers with names, directions, and distances.
- Isolate teammate location and player-session persistence in dedicated modules.

## 0.5.8

- Capture a fresh host save when a player joins so previously dug tunnels are
  included even when the last disk autosave is stale.
- Defer client gameplay-scene travel until the full host snapshot has been
  received, checksum-validated, and applied.
- Split world, enemy, and economy synchronization into isolated source modules,
  including their packet handlers.

## 0.5.7

- Synchronize deaths for normal enemies, pirate-station guns, and worm bosses.
- Keep enemy drops host-authoritative and exclude silent chunk despawns.

## 0.5.6

- Recalculate remote tunnel lighting and fog after synchronized digging.
- Defer lighting updates for inactive streamed planet chunks.
- Route remote removals through collision-cache and save-data listeners.
- Synchronize gadget-bomb tile destruction.

## 0.5.5

- Add host-authoritative delivered station resource totals.
- Route guest deposits and spending through ordered host-validated requests.
- Reconcile absolute inventory totals across all connected players.
- Add economy transaction and traffic logging.

## 0.5.4

- Restore complete remote ship bodies when avatars are created during the local
  spawn fade.
- Correct remote body and drill sorting orders.

## 0.5.3

- Clone the complete player ship, animated drill, turning jets, and particle
  thruster trail for remote avatars.
- Add smoothed movement, rotation, and short velocity prediction.

## 0.4

- Add the authoritative framed TCP state channel.
- Synchronize player transforms and mined tiles bidirectionally through the host.
- Add world-save bootstrap and host-directed scene travel.

## 0.3

- Add clickable multiplayer menus, player names, join codes, manual hosting, and
  automatic UPnP hosting.
