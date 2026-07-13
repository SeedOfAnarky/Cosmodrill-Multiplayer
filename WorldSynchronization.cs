using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace CosmodrillMultiplayer;

/// <summary>
/// Initial world bootstrap, removed-tile replication, collision-cache updates, and
/// tunnel lighting reconciliation. This module owns world state only.
/// </summary>
public sealed partial class MultiplayerMod
{
    private readonly Dictionary<string, PendingLightingReveal> pendingLightingReveals = new Dictionary<string, PendingLightingReveal>();
    private readonly SortedDictionary<int, string> bootstrapChunks = new SortedDictionary<int, string>();
    private readonly HashSet<int> bootstrapSentConnections = new HashSet<int>();
    private PlayerDrill hookedDrill;
    private bool gadgetBombHooked;
    private float pendingRevealTimer;
    private int tilesSent;
    private int tilesReceived;
    private int lightingRevealsApplied;
    private bool applyingRemoteTile;
    private bool worldBootstrapApplied;
    private int bootstrapExpected;
    private int bootstrapExpectedBytes;
    private int bootstrapSaveIndex = -1;
    private string bootstrapExpectedHash = "";
    private string bootstrapScene = "";

    private sealed class PendingLightingReveal
    {
        internal string MapName;
        internal Vector3Int Cell;
    }

    private void SendWorldBootstrap(int connectionId)
    {
        if (!isHost || replication == null) return;
        if (!bootstrapSentConnections.Add(connectionId))
        {
            Log("BOOTSTRAP", "Snapshot already sent on state connection " + connectionId + "; duplicate identity ignored");
            return;
        }
        try
        {
            if (ScriptableObjectHolder.Instance == null || SaveManager.Instance == null) throw new Exception("Host save system is unavailable");
            int index = ScriptableObjectHolder.Instance.ThisVariousSaveData.saveFileIndex;
            if (index < 0) throw new Exception("Host has no active saved world (index " + index + ")");

            int removedTileCount = CaptureLiveHostWorld(index);
            string path = GetSavePath(index);
            if (!File.Exists(path)) throw new Exception("Fresh host world snapshot was not written to " + path);
            byte[] bytes = File.ReadAllBytes(path);
            string hash = ComputeSha256(bytes);
            string encoded = Convert.ToBase64String(bytes);
            const int chunkSize = 1400;
            int total = (encoded.Length + chunkSize - 1) / chunkSize;
            if (!replication.SendTo(connectionId, "WB|" + localId + "|" + index + "|" + total + "|" + B64(currentScene) + "|" + bytes.Length + "|" + hash + "|" + removedTileCount))
                throw new IOException("State connection closed while sending the world header");
            for (int i = 0; i < total; i++)
            {
                int length = Math.Min(chunkSize, encoded.Length - i * chunkSize);
                if (!replication.SendTo(connectionId, "WC|" + localId + "|" + i + "|" + encoded.Substring(i * chunkSize, length)))
                    throw new IOException("State connection closed while sending world chunk " + i + " of " + total);
            }
            Log("BOOTSTRAP", "Sent fresh save" + index + " snapshot (" + bytes.Length + " bytes, " + total + " chunks, " + removedTileCount + " removed tiles) over framed state TCP to connection " + connectionId + "; sha256=" + hash.Substring(0, 12));
        }
        catch (Exception ex)
        {
            bootstrapSentConnections.Remove(connectionId);
            LogError("BOOTSTRAP", ex.Message);
            replication?.SendTo(connectionId, "WF|" + localId + "|" + B64(ex.Message));
        }
    }

    private int CaptureLiveHostWorld(int index)
    {
        ScriptableObjectHolder holder = ScriptableObjectHolder.Instance;
        if (holder == null || SaveManager.Instance == null) throw new Exception("Host save system is unavailable");
        if (!holder.hasLoaded) throw new Exception("Host world is not fully loaded yet; wait until the player is in the world and invite again");
        if (TilemapSaveDataManager.Instance == null) throw new Exception("Host tile-save manager is not ready; wait until the world finishes loading and invite again");

        var snapshot = new FullSaveFile
        {
            isEmptyFile = false,
            thisStoryData = holder.thisStoryData.GetSaveData(),
            ThisPlayerData = holder.ThisPlayerData.GetSaveData(),
            ThisGadgetChoiceDataCollection = holder.ThisGadgetChoiceDataCollection.GetSaveData(),
            ThisUpgradePathData = holder.ThisUpgradePathData.GetSaveData(),
            ThisMissionData = holder.ThisMissionData.GetSaveData(),
            ThisMapSaveData = holder.ThisMapSaveData.GetSaveData(),
            variousSaveData = holder.ThisVariousSaveData.GetSaveData()
        };
        if (snapshot.variousSaveData == null) throw new Exception("Host live save data could not be captured");

        int removedTileCount = 0;
        if (snapshot.variousSaveData.tilemapChanges != null)
        {
            foreach (TilemapChangesDataContainer changes in snapshot.variousSaveData.tilemapChanges)
                if (changes != null && changes.removedTiles != null) removedTileCount += changes.removedTiles.Count;
        }
        SaveManager.Instance.SaveData(snapshot, index);
        Log("BOOTSTRAP", "Captured live host world before join: " + removedTileCount + " removed tiles across " + (snapshot.variousSaveData.tilemapChanges == null ? 0 : snapshot.variousSaveData.tilemapChanges.Count) + " map objects");
        return removedTileCount;
    }

    private void ApplyWorldBootstrap()
    {
        var encoded = new StringBuilder();
        for (int i = 0; i < bootstrapExpected; i++)
        {
            string chunk;
            if (!bootstrapChunks.TryGetValue(i, out chunk)) throw new Exception("Missing world chunk " + i);
            encoded.Append(chunk);
        }
        byte[] bytes = Convert.FromBase64String(encoded.ToString());
        if (bootstrapExpectedBytes > 0 && bytes.Length != bootstrapExpectedBytes) throw new Exception("World snapshot length mismatch; expected " + bootstrapExpectedBytes + ", received " + bytes.Length);
        string actualHash = ComputeSha256(bytes);
        if (!string.IsNullOrEmpty(bootstrapExpectedHash) && !string.Equals(actualHash, bootstrapExpectedHash, StringComparison.OrdinalIgnoreCase)) throw new Exception("World snapshot checksum mismatch");

        LocalPlayerProfile localProfile = PrepareLocalPlayerProfileForBootstrap(bootstrapSaveIndex);
        string path = GetSavePath(bootstrapSaveIndex);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllBytes(path, bytes);
        if (SaveManager.Instance == null || ScriptableObjectHolder.Instance == null) throw new Exception("Save system is unavailable");
        FullSaveFile save = SaveManager.Instance.LoadData(bootstrapSaveIndex);
        if (save == null || save.isEmptyFile) throw new Exception("Host snapshot could not be decoded");
        ScriptableObjectHolder.Instance.LoadDataFromSaveFile(save);
        ApplyLocalPlayerProfile(localProfile, bootstrapSaveIndex);
        ScriptableObjectHolder.Instance.ThisVariousSaveData.saveFileIndex = bootstrapSaveIndex;
        ScriptableObjectHolder.Instance.hasLoaded = true;
        worldBootstrapApplied = true;
        handshakePending = true;
        handshakeSent = false;
        clientTransport.SendCommand("/myname :" + playerName.Replace(" ", "_"));
        TrySendHandshake("world bootstrap");
        Log("BOOTSTRAP", "Validated and applied host save" + bootstrapSaveIndex + " (" + bytes.Length + " bytes, sha256=" + actualHash.Substring(0, 12) + "); entering " + bootstrapScene);
        bootstrapChunks.Clear();
        bootstrapExpected = 0;
        bootstrapExpectedBytes = 0;
        bootstrapExpectedHash = "";
        Travel(bootstrapScene);
    }

    private static string ComputeSha256(byte[] bytes)
    {
        using (SHA256 sha = SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
    }

    private static string GetSavePath(int index)
    {
        string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        return Path.Combine(root, "UserData", "Saves", "save" + index + ".dat");
    }

    private void HookDrill()
    {
        if (!sceneReady || PlayerDrill.Instance == null) return;
        if (!gadgetBombHooked)
        {
            GadgetBomb.TileRemoved -= OnBombTileRemoved;
            GadgetBomb.TileRemoved += OnBombTileRemoved;
            gadgetBombHooked = true;
            Log("SYNC", "Gadget-bomb tile hook attached");
        }
        if (hookedDrill == PlayerDrill.Instance) return;
        if (hookedDrill != null) hookedDrill.TileRemoved -= OnTileRemoved;
        hookedDrill = PlayerDrill.Instance;
        hookedDrill.TileRemoved += OnTileRemoved;
        Log("SYNC", "Drill tile hook attached");
    }

    private bool TryHandleWorldReplication(string[] p, string senderId)
    {
        if (p[0] == "WB")
        {
            if (isHost || p.Length != 8) throw new Exception("Invalid framed world snapshot header");
            if (!string.IsNullOrEmpty(currentHostId) && senderId != currentHostId) throw new Exception("World snapshot came from an unexpected host");
            bootstrapSaveIndex = int.Parse(p[2]);
            bootstrapExpected = int.Parse(p[3]);
            bootstrapScene = UB64(p[4]);
            bootstrapExpectedBytes = int.Parse(p[5]);
            bootstrapExpectedHash = p[6];
            int removedTileCount = int.Parse(p[7]);
            if (bootstrapSaveIndex < 0 || bootstrapSaveIndex > 99 || bootstrapExpected <= 0 || bootstrapExpected > 100000 || bootstrapExpectedBytes <= 0 || bootstrapExpectedBytes > 134217728 || bootstrapScene.Length == 0 || bootstrapScene.Length > 128 || bootstrapExpectedHash.Length != 64 || removedTileCount < 0)
                throw new Exception("Invalid framed world snapshot values");
            bootstrapChunks.Clear();
            worldBootstrapApplied = false;
            status = "Receiving host world (0/" + bootstrapExpected + ")";
            Log("BOOTSTRAP", "Receiving save" + bootstrapSaveIndex + " in " + bootstrapExpected + " chunks over framed state TCP (" + bootstrapExpectedBytes + " bytes, " + removedTileCount + " removed tiles, sha256=" + bootstrapExpectedHash.Substring(0, 12) + ")");
            return true;
        }
        if (p[0] == "WC")
        {
            if (isHost || p.Length != 4) throw new Exception("Invalid framed world chunk");
            if (!string.IsNullOrEmpty(currentHostId) && senderId != currentHostId) throw new Exception("World chunk came from an unexpected host");
            int sequence = int.Parse(p[2]);
            if (bootstrapExpected <= 0 || sequence < 0 || sequence >= bootstrapExpected) throw new Exception("World chunk is outside the active snapshot");
            if (string.IsNullOrEmpty(p[3]) || p[3].Length > 1900) throw new Exception("Invalid world chunk payload");
            bootstrapChunks[sequence] = p[3];
            status = "Receiving host world (" + bootstrapChunks.Count + "/" + bootstrapExpected + ")";
            if (bootstrapChunks.Count == bootstrapExpected) ApplyWorldBootstrap();
            return true;
        }
        if (p[0] == "WF")
        {
            if (isHost || p.Length != 3) throw new Exception("Invalid framed world failure");
            if (!string.IsNullOrEmpty(currentHostId) && senderId != currentHostId) throw new Exception("World failure came from an unexpected host");
            status = "Host world failed: " + UB64(p[2]);
            LogError("BOOTSTRAP", status);
            return true;
        }
        if (p[0] != "T") return false;
        if (p.Length != 6) throw new Exception("Invalid tile state");
        string mapName = UB64(p[2]);
        if (mapName.Length == 0 || mapName.Length > 128) throw new Exception("Invalid tilemap name");
        int cellX = int.Parse(p[3]), cellY = int.Parse(p[4]), cellZ = int.Parse(p[5]);
        ApplyTile(mapName, cellX, cellY, cellZ);
        tilesReceived++;
        if (isHost && replication != null) replication.Send("T|" + senderId + "|" + B64(mapName) + "|" + cellX + "|" + cellY + "|" + cellZ);
        return true;
    }

    private void OnBombTileRemoved(Tilemap map, Vector3Int cell)
    {
        if (!applyingRemoteTile && map != null) TryApplyLightingReveal(map, cell);
        OnTileRemoved(map, cell);
    }

    private void OnTileRemoved(Tilemap map, Vector3Int cell)
    {
        if (applyingRemoteTile || !sceneReady || map == null || replication == null) return;
        if (replication.Send("T|" + localId + "|" + B64(map.gameObject.name) + "|" + cell.x + "|" + cell.y + "|" + cell.z)) tilesSent++;
    }

    private void ApplyTile(string name, int x, int y, int z)
    {
        Vector3Int cell = new Vector3Int(x, y, z);
        bool found = false, hasLightingTarget = false, lightingApplied = false;
        foreach (Tilemap map in UnityEngine.Object.FindObjectsByType<Tilemap>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (map == null || map.gameObject.name != name) continue;
            found = true;
            map.SetTile(cell, null);
            // Replaying the game's event updates TileCollisionCache and
            // TilemapSaveDataManager. The guard prevents a network echo.
            applyingRemoteTile = true;
            try { PlayerDrill.Instance?.TileRemoved?.Invoke(map, cell); }
            finally { applyingRemoteTile = false; }
            if (map.GetComponent<TileLighting>() != null)
            {
                hasLightingTarget = true;
                if (TryApplyLightingReveal(map, cell)) lightingApplied = true;
            }
        }
        if (!found || (hasLightingTarget && !lightingApplied))
        {
            string key = name + "|" + x + "|" + y + "|" + z;
            pendingLightingReveals[key] = new PendingLightingReveal { MapName = name, Cell = cell };
        }
    }

    private bool TryApplyLightingReveal(Tilemap map, Vector3Int cell)
    {
        if (map == null || !map.enabled || !map.gameObject.activeInHierarchy) return false;
        TileLighting lighting = map.GetComponent<TileLighting>();
        if (lighting == null) return false;
        lighting.RecalculateBrightnessAroundTile(cell);
        lightingRevealsApplied++;
        return true;
    }

    private void ProcessPendingLightingReveals()
    {
        if (!sceneReady || pendingLightingReveals.Count == 0 || (pendingRevealTimer += Time.unscaledDeltaTime) < 1f) return;
        pendingRevealTimer = 0f;
        Tilemap[] maps = UnityEngine.Object.FindObjectsByType<Tilemap>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        var completed = new List<string>();
        int processed = 0;
        foreach (KeyValuePair<string, PendingLightingReveal> entry in pendingLightingReveals)
        {
            bool matched = false, hasLightingTarget = false, applied = false;
            foreach (Tilemap map in maps)
            {
                if (map == null || map.gameObject.name != entry.Value.MapName) continue;
                matched = true;
                // A streaming loader may repopulate an inactive runtime tilemap.
                map.SetTile(entry.Value.Cell, null);
                if (map.GetComponent<TileLighting>() != null)
                {
                    hasLightingTarget = true;
                    if (TryApplyLightingReveal(map, entry.Value.Cell)) applied = true;
                }
            }
            if (applied || (matched && !hasLightingTarget)) completed.Add(entry.Key);
            if (++processed >= 128) break;
        }
        foreach (string key in completed) pendingLightingReveals.Remove(key);
        if (completed.Count > 0) Log("LIGHTING", "Applied " + completed.Count + " deferred shared tunnel reveal(s); pending=" + pendingLightingReveals.Count);
    }
}
