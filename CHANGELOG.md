# Changelog

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
