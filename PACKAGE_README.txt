Cosmodrill Multiplayer v0.5.6
================================

Requirements
------------
- Cosmodrill 1.0
- MelonLoader installed in the Cosmodrill directory

Installation
------------
1. Close every running Cosmodrill instance.
2. Extract this ZIP into the Cosmodrill directory, beside Cosmodrill.exe.
3. Allow the archive to create the Mods and UserLibs folders.
4. Launch the game. The main menu will show the multiplayer panel.

Included files
--------------
- Mods\CosmodrillMultiplayer.dll
- UserLibs\Open.Nat.dll

Portable host/client copies
---------------------------
For a copied game directory that must launch independently from Steam, create a
steam_appid.txt file beside Cosmodrill.exe containing this number:

3722660

The mod detects that marker, prevents Steam from redirecting the copied game to
the official install, and keeps that copy's saves under UserData\Saves.

Networking
----------
The host menu creates the join code. Automatic UPnP maps the selected TCP port,
the following UDP port, and the second following TCP state-replication port.
If UPnP is unavailable, forward all three ports on the host router.

Shared resources
----------------
Delivered station resources use the host's inventory as the shared ledger.
Guest deposits and spending are sent to the host, validated, saved by the host,
and synchronized back to every connected player. Undelivered cargo remains
personal to the player carrying it.

Shared tunnels
--------------
Drill and gadget-bomb tile removals are shared through the host. Remote tunnels
update collision caches, removed-tile save data, and black lighting/fog reveals.
