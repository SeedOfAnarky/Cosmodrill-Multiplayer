using System.Globalization;
using UnityEngine;

namespace CosmodrillMultiplayer;

/// <summary>
/// Persistent player identity, reconnect records, player-owned profile storage,
/// graceful disconnect handling, and teammate location UI.
/// </summary>
public sealed partial class MultiplayerMod
{
    private readonly Dictionary<string, int> replicationConnectionsByPlayer = new Dictionary<string, int>();
    private readonly Dictionary<string, ReconnectRecord> reconnectRecords = new Dictionary<string, ReconnectRecord>();
    private readonly Dictionary<int, PendingPlayerJoin> pendingPlayerJoins = new Dictionary<int, PendingPlayerJoin>();
    private readonly HashSet<int> resumeSentConnections = new HashSet<int>();
    private string currentHostId = "";
    private string currentProfileWorldKey = "";
    private float localProfileTimer;
    private float reconnectRecordFlushTimer;
    private float replicationHelloTimer;
    private float transportLostTimer;
    private bool replicationHelloAcknowledged;
    private bool stateChannelWasConnected;
    private bool transportLossHandled;
    private bool reconnectRecordsDirty;
    private PendingResumeState pendingResume;

    [Serializable]
    private sealed class LocalPlayerProfile
    {
        public string PlayerId;
        public string HostId;
        public string WorldKey;
        public int SaveIndex;
        public string Scene;
        public long SavedUtcTicks;
        public bool HasPosition;
        public float X;
        public float Y;
        public float Z;
        public float Rotation;
        public PlayerDataContainer PlayerData;
        public UpgradePathDataContainer UpgradeData;
        public GadgetChoiceDataCollectionContainer GadgetData;
        public List<CurrencySaveDataContainer> CarriedInventory;
        public int SecondaryWeaponPieces;
        public List<GadgetFieldSaveDataContainer> GadgetFields;
        public bool RocketTrail1Unlocked;
        public bool RocketTrail2Unlocked;
        public int EquippedRocketTrailIndex;
    }

    [Serializable]
    private sealed class ReconnectRecord
    {
        public string PlayerId;
        public string PlayerName;
        public string WorldKey;
        public string Scene;
        public float X;
        public float Y;
        public float Z;
        public float Rotation;
        public long LastSeenUtcTicks;
    }

    [Serializable]
    private sealed class ReconnectRecordFile
    {
        public List<ReconnectRecord> Records = new List<ReconnectRecord>();
    }

    private sealed class PendingResumeState
    {
        internal string WorldKey;
        internal string Scene;
        internal Vector3 Position;
        internal float Rotation;
        internal string Source;
        internal float ExpiresAt;
    }

    private sealed class PendingPlayerJoin
    {
        internal string PlayerId;
        internal string ClientWorldKey;
    }

    private void LoadOrCreatePlayerId()
    {
        string path = Path.Combine(GetUserDataRoot(), "CosmodrillMultiplayer.id");
        try
        {
            if (File.Exists(path))
            {
                string saved = File.ReadAllText(path).Trim().ToLowerInvariant();
                Guid parsed;
                if (saved.Length == 32 && Guid.TryParseExact(saved, "N", out parsed)) localId = saved;
            }
            if (string.IsNullOrEmpty(localId))
            {
                localId = Guid.NewGuid().ToString("N");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, localId);
                Log("IDENTITY", "Created persistent player ID " + localId.Substring(0, 8));
            }
            else Log("IDENTITY", "Loaded persistent player ID " + localId.Substring(0, 8));
        }
        catch (Exception ex)
        {
            localId = Guid.NewGuid().ToString("N");
            LogWarn("IDENTITY", "Could not persist player ID; this launch will use " + localId.Substring(0, 8) + ": " + ex.Message);
        }
    }

    private static string GetUserDataRoot()
    {
        string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        return Path.Combine(root, "UserData");
    }

    private string CurrentHostWorldKey()
    {
        int index = ScriptableObjectHolder.Instance == null || ScriptableObjectHolder.Instance.ThisVariousSaveData == null
            ? -1
            : ScriptableObjectHolder.Instance.ThisVariousSaveData.saveFileIndex;
        return localId + ":" + index;
    }

    private static string RemoteWorldKey(string hostId, int saveIndex)
    {
        return hostId + ":" + saveIndex;
    }

    private void LoadHostReconnectRecords()
    {
        reconnectRecords.Clear();
        string path = Path.Combine(GetUserDataRoot(), "CosmodrillMultiplayer.reconnects.json");
        try
        {
            if (!File.Exists(path)) { Log("RECONNECT", "No previous reconnect records for this host"); return; }
            ReconnectRecordFile file = JsonUtility.FromJson<ReconnectRecordFile>(File.ReadAllText(path));
            if (file?.Records == null) return;
            foreach (ReconnectRecord record in file.Records)
            {
                if (record == null || record.PlayerId == null || record.PlayerId.Length != 32 || string.IsNullOrEmpty(record.WorldKey)) continue;
                reconnectRecords[ReconnectRecordKey(record.PlayerId, record.WorldKey)] = record;
            }
            Log("RECONNECT", "Loaded " + reconnectRecords.Count + " retained player position record(s)");
        }
        catch (Exception ex) { LogWarn("RECONNECT", "Could not load reconnect records: " + ex.Message); }
    }

    private void SaveHostReconnectRecords(bool logResult)
    {
        if (!isHost && reconnectRecords.Count == 0) return;
        string path = Path.Combine(GetUserDataRoot(), "CosmodrillMultiplayer.reconnects.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var file = new ReconnectRecordFile { Records = new List<ReconnectRecord>(reconnectRecords.Values) };
            File.WriteAllText(path, JsonUtility.ToJson(file, true));
            reconnectRecordsDirty = false;
            if (logResult) Log("RECONNECT", "Saved " + reconnectRecords.Count + " player reconnect record(s)");
        }
        catch (Exception ex) { LogWarn("RECONNECT", "Could not save reconnect records: " + ex.Message); }
    }

    private static string ReconnectRecordKey(string playerId, string worldKey) => playerId + "@" + worldKey;

    private void RegisterReplicationIdentity(int connectionId, string playerId)
    {
        int existingConnection;
        if (replicationConnectionsByPlayer.TryGetValue(playerId, out existingConnection) && existingConnection != connectionId)
        {
            replicationPeerIds.Remove(existingConnection);
            resumeSentConnections.Remove(existingConnection);
            bootstrapSentConnections.Remove(existingConnection);
            pendingPlayerJoins.Remove(existingConnection);
            replication?.DisconnectPeer(existingConnection);
            LogWarn("RECONNECT", "Replaced stale state connection for persistent player " + playerId.Substring(0, 8));
        }
        replicationConnectionsByPlayer[playerId] = connectionId;
    }

    private bool TryHandlePlayerSessionReplication(int connectionId, string[] p, string senderId)
    {
        if (p[0] == "I")
        {
            if (!isHost || (p.Length != 3 && p.Length != 4)) throw new Exception("Invalid persistent identity frame");
            string name = UB64(p[2]);
            if (name.Length > 24) name = name.Substring(0, 24);
            string clientWorldKey = p.Length == 4 ? UB64(p[3]) : "";
            if (clientWorldKey.Length > 80) throw new Exception("Invalid client world identity");
            peerNames[senderId] = name;
            QueueOrCompletePlayerJoin(connectionId, senderId, clientWorldKey);
            return true;
        }
        if (p[0] == "JW")
        {
            if (isHost || p.Length != 3) throw new Exception("Invalid join-wait frame");
            if (!string.IsNullOrEmpty(currentHostId) && senderId != currentHostId) throw new Exception("Join-wait state came from an unexpected host");
            string hostScene = UB64(p[2]);
            if (hostScene.Length > 128) throw new Exception("Invalid waiting scene");
            replicationHelloAcknowledged = true;
            status = "Connected — host world is loading";
            Log("BOOTSTRAP", "Host acknowledged this player; waiting for its save and tilemaps" + (string.IsNullOrEmpty(hostScene) ? "" : " (scene=" + hostScene + ")"));
            return true;
        }
        if (p[0] == "R")
        {
            if (isHost || p.Length != 9) throw new Exception("Invalid reconnect state frame");
            if (!string.IsNullOrEmpty(currentHostId) && senderId != currentHostId) throw new Exception("Reconnect state came from an unexpected host");
            bool found = p[2] == "1";
            string worldKey = UB64(p[3]);
            string scene = UB64(p[4]);
            replicationHelloAcknowledged = true;
            if (found)
            {
                float x = P(p[5]), y = P(p[6]), z = P(p[7]), rotation = P(p[8]);
                if (!Finite(x) || !Finite(y) || !Finite(z) || !Finite(rotation)) throw new Exception("Invalid reconnect position");
                pendingResume = new PendingResumeState { WorldKey = worldKey, Scene = scene, Position = new Vector3(x, y, z), Rotation = rotation, Source = "host reconnect record", ExpiresAt = Time.unscaledTime + 60f };
                Log("RECONNECT", "Host found a retained position for player " + localId.Substring(0, 8) + " in " + scene);
            }
            else Log("RECONNECT", "Host has no retained position for this player in the active world");
            return true;
        }
        if (p[0] == "Q")
        {
            if (!isHost || p.Length != 3) throw new Exception("Invalid disconnect notice");
            string name = UB64(p[2]);
            SaveHostReconnectRecords(false);
            Log("RECONNECT", name + " (" + senderId.Substring(0, 8) + ") disconnected gracefully; reconnect state retained");
            return true;
        }
        if (p[0] != "P") return false;
        if (p.Length < 10) throw new Exception("Invalid player state");
        string playerDisplayName = UB64(p[2]);
        if (playerDisplayName.Length > 24) playerDisplayName = playerDisplayName.Substring(0, 24);
        float px = P(p[3]), py = P(p[4]), pz = P(p[5]), playerRotation = P(p[6]), vx = P(p[7]), vy = P(p[8]);
        if (!Finite(px) || !Finite(py) || !Finite(pz) || !Finite(playerRotation) || !Finite(vx) || !Finite(vy)) throw new Exception("Non-finite player state");
        peerNames[senderId] = playerDisplayName;
        positionsReceived++;
        bool drilling = p[9] == "1", moving = p.Length > 10 ? p[10] == "1" : (vx * vx + vy * vy > .04f), leftJet = p.Length > 11 && p[11] == "1", rightJet = p.Length > 12 && p[12] == "1";
        if (isHost)
        {
            // Send the retained position before this first new-spawn state can
            // replace it, then begin recording the new live state.
            SendReconnectState(connectionId, senderId);
            RecordPeerPosition(senderId, playerDisplayName, px, py, pz, playerRotation);
        }
        ApplyPosition(senderId, px, py, pz, playerRotation, vx, vy, drilling, moving, leftJet, rightJet);
        if (isHost && replication != null) replication.Send("P|" + senderId + "|" + B64(playerDisplayName) + "|" + F(px) + "|" + F(py) + "|" + F(pz) + "|" + F(playerRotation) + "|" + F(vx) + "|" + F(vy) + "|" + (drilling ? "1" : "0") + "|" + (moving ? "1" : "0") + "|" + (leftJet ? "1" : "0") + "|" + (rightJet ? "1" : "0"));
        return true;
    }

    private void SendReconnectState(int connectionId, string playerId)
    {
        if (!isHost || replication == null || resumeSentConnections.Contains(connectionId)) return;
        resumeSentConnections.Add(connectionId);
        string worldKey = CurrentHostWorldKey();
        ReconnectRecord record;
        bool found = reconnectRecords.TryGetValue(ReconnectRecordKey(playerId, worldKey), out record) && record.Scene == currentScene;
        string frame = "R|" + localId + "|" + (found ? "1" : "0") + "|" + B64(worldKey) + "|" + B64(found ? record.Scene : currentScene) + "|" + F(found ? record.X : 0f) + "|" + F(found ? record.Y : 0f) + "|" + F(found ? record.Z : 0f) + "|" + F(found ? record.Rotation : 0f);
        if (!replication.SendTo(connectionId, frame)) resumeSentConnections.Remove(connectionId);
        else Log("RECONNECT", (found ? "Sent retained position to " : "Registered first join for ") + playerId.Substring(0, 8) + " in " + worldKey);
    }

    private bool HostWorldReadyForPlayerJoin()
    {
        return isHost && sceneReady && SaveManager.Instance != null && TilemapSaveDataManager.Instance != null &&
            ScriptableObjectHolder.Instance != null && ScriptableObjectHolder.Instance.hasLoaded &&
            ScriptableObjectHolder.Instance.ThisVariousSaveData != null &&
            ScriptableObjectHolder.Instance.ThisVariousSaveData.saveFileIndex >= 0;
    }

    private void QueueOrCompletePlayerJoin(int connectionId, string playerId, string clientWorldKey)
    {
        if (HostWorldReadyForPlayerJoin())
        {
            CompletePlayerJoin(connectionId, playerId, clientWorldKey);
            return;
        }
        bool firstNotice = !pendingPlayerJoins.ContainsKey(connectionId);
        pendingPlayerJoins[connectionId] = new PendingPlayerJoin { PlayerId = playerId, ClientWorldKey = clientWorldKey };
        if (!firstNotice) return;
        if (replication == null || !replication.SendTo(connectionId, "JW|" + localId + "|" + B64(currentScene)))
            LogWarn("BOOTSTRAP", "Could not acknowledge pending world join on state connection " + connectionId);
        Log("BOOTSTRAP", "Queued player " + playerId.Substring(0, 8) + " until the host save, player, and tilemaps are ready; current scene=" + currentScene);
    }

    private void ProcessPendingPlayerJoins()
    {
        if (!isHost || pendingPlayerJoins.Count == 0 || !HostWorldReadyForPlayerJoin()) return;
        foreach (KeyValuePair<int, PendingPlayerJoin> entry in pendingPlayerJoins.ToList())
        {
            string boundPlayer;
            if (!replicationPeerIds.TryGetValue(entry.Key, out boundPlayer) || boundPlayer != entry.Value.PlayerId)
            {
                pendingPlayerJoins.Remove(entry.Key);
                continue;
            }
            CompletePlayerJoin(entry.Key, entry.Value.PlayerId, entry.Value.ClientWorldKey);
        }
    }

    private void CompletePlayerJoin(int connectionId, string playerId, string clientWorldKey)
    {
        pendingPlayerJoins.Remove(connectionId);
        SendReconnectState(connectionId, playerId);
        string hostWorldKey = CurrentHostWorldKey();
        if (clientWorldKey == hostWorldKey)
            Log("BOOTSTRAP", "Player " + playerId.Substring(0, 8) + " already has " + hostWorldKey + "; state reconnect will not reload the world");
        else
        {
            Log("BOOTSTRAP", "Host world is ready; releasing authoritative snapshot to player " + playerId.Substring(0, 8));
            SendWorldBootstrap(connectionId);
        }
    }

    private void RecordPeerPosition(string playerId, string name, float x, float y, float z, float rotation)
    {
        string worldKey = CurrentHostWorldKey();
        if (worldKey.EndsWith(":-1", StringComparison.Ordinal)) return;
        reconnectRecords[ReconnectRecordKey(playerId, worldKey)] = new ReconnectRecord
        {
            PlayerId = playerId,
            PlayerName = name,
            WorldKey = worldKey,
            Scene = currentScene,
            X = x,
            Y = y,
            Z = z,
            Rotation = rotation,
            LastSeenUtcTicks = DateTime.UtcNow.Ticks
        };
        reconnectRecordsDirty = true;
    }

    private LocalPlayerProfile PrepareLocalPlayerProfileForBootstrap(int saveIndex)
    {
        currentProfileWorldKey = RemoteWorldKey(currentHostId, saveIndex);
        LocalPlayerProfile profile = LoadLocalPlayerProfile(saveIndex);
        if (profile != null) return profile;

        // One-time migration for players upgrading from versions that stored their
        // co-op ship state only in the client copy's save file.
        try
        {
            FullSaveFile existing = SaveManager.Instance?.LoadData(saveIndex);
            if (existing != null && !existing.isEmptyFile && existing.ThisPlayerData != null)
            {
                profile = ProfileFromSave(existing, saveIndex);
                SaveLocalPlayerProfile(profile, false);
                Log("PROFILE", "Migrated existing client ship progression into persistent player profile " + localId.Substring(0, 8));
            }
        }
        catch (Exception ex) { LogWarn("PROFILE", "Could not migrate the previous client profile: " + ex.Message); }
        return profile;
    }

    private LocalPlayerProfile ProfileFromSave(FullSaveFile save, int saveIndex)
    {
        VariousSaveDataContainer various = save.variousSaveData;
        return new LocalPlayerProfile
        {
            PlayerId = localId,
            HostId = currentHostId,
            WorldKey = RemoteWorldKey(currentHostId, saveIndex),
            SaveIndex = saveIndex,
            Scene = "",
            SavedUtcTicks = DateTime.UtcNow.Ticks,
            PlayerData = save.ThisPlayerData,
            UpgradeData = save.ThisUpgradePathData,
            GadgetData = save.ThisGadgetChoiceDataCollection,
            CarriedInventory = various?.CurrencyCurrentlyInStorage,
            SecondaryWeaponPieces = various == null ? 0 : various.AmountOfcollectedPiecesForSecondaryWeaponPartParent,
            GadgetFields = various?.gadgetFieldSaveDataContainers,
            RocketTrail1Unlocked = various != null && various.rocketTrail1Unlocked,
            RocketTrail2Unlocked = various != null && various.rocketTrail2Unlocked,
            EquippedRocketTrailIndex = various == null ? 0 : various.equippedRocketTrailIndex
        };
    }

    private LocalPlayerProfile LoadLocalPlayerProfile(int saveIndex)
    {
        string path = GetLocalProfilePath(currentHostId, saveIndex);
        try
        {
            if (!File.Exists(path)) return null;
            LocalPlayerProfile profile = JsonUtility.FromJson<LocalPlayerProfile>(File.ReadAllText(path));
            string expectedWorld = RemoteWorldKey(currentHostId, saveIndex);
            if (profile == null || profile.PlayerId != localId || profile.HostId != currentHostId || profile.WorldKey != expectedWorld) throw new Exception("Profile identity does not match this player and host world");
            return profile;
        }
        catch (Exception ex) { LogWarn("PROFILE", "Could not load local player profile: " + ex.Message); return null; }
    }

    private string GetLocalProfilePath(string hostId, int saveIndex)
    {
        string safeHost = hostId != null && hostId.Length == 32 ? hostId : "unknown-host";
        return Path.Combine(GetUserDataRoot(), "CosmodrillMultiplayerProfiles", safeHost + "_save" + saveIndex + "_" + localId + ".json");
    }

    private void ApplyLocalPlayerProfile(LocalPlayerProfile profile, int saveIndex)
    {
        currentProfileWorldKey = RemoteWorldKey(currentHostId, saveIndex);
        ScriptableObjectHolder holder = ScriptableObjectHolder.Instance;
        if (holder == null) return;
        if (profile == null)
        {
            // A first-time guest must not receive a duplicate of the host's carried
            // ore. Shared delivered resources remain in the economy module.
            var emptyCargo = new List<CurrencySaveDataContainer>();
            foreach (ResourceTypes type in Enum.GetValues(typeof(ResourceTypes))) emptyCargo.Add(new CurrencySaveDataContainer { resourceType = type, resourceAmount = 0f });
            holder.ThisVariousSaveData.CurrencyCurrentlyInStorage = emptyCargo;
            Log("PROFILE", "First join for player " + localId.Substring(0, 8) + "; using host ship progression with empty carried cargo");
            return;
        }
        if (profile.PlayerData != null) holder.ThisPlayerData.LoadDataFromSaveFile(profile.PlayerData);
        if (profile.UpgradeData != null) holder.ThisUpgradePathData.LoadUpgradePathDataFromSaveFile(profile.UpgradeData);
        if (profile.GadgetData != null) holder.ThisGadgetChoiceDataCollection.LoadGadgetChoiceDataCollectionFromSaveFile(profile.GadgetData);
        if (profile.CarriedInventory != null) holder.ThisVariousSaveData.CurrencyCurrentlyInStorage = profile.CarriedInventory;
        if (profile.GadgetFields != null) holder.ThisVariousSaveData.gadgetFieldSaveDataContainers = profile.GadgetFields;
        holder.ThisVariousSaveData.AmountOfcollectedPiecesForSecondaryWeaponPartParent = profile.SecondaryWeaponPieces;
        holder.ThisVariousSaveData.rocketTrail1Unlocked = profile.RocketTrail1Unlocked;
        holder.ThisVariousSaveData.rocketTrail2Unlocked = profile.RocketTrail2Unlocked;
        holder.ThisVariousSaveData.equippedRocketTrailIndex = profile.EquippedRocketTrailIndex;
        if (profile.HasPosition && (pendingResume == null || pendingResume.Source != "host reconnect record"))
        {
            pendingResume = new PendingResumeState { WorldKey = profile.WorldKey, Scene = profile.Scene, Position = new Vector3(profile.X, profile.Y, profile.Z), Rotation = profile.Rotation, Source = "local player profile", ExpiresAt = Time.unscaledTime + 60f };
        }
        Log("PROFILE", "Restored carried cargo, ship stats, upgrades, and gadget setup for player " + localId.Substring(0, 8));
    }

    private void CaptureLocalPlayerProfile(bool logResult)
    {
        if (isHost || string.IsNullOrEmpty(currentHostId) || string.IsNullOrEmpty(currentProfileWorldKey) || ScriptableObjectHolder.Instance == null) return;
        try
        {
            ScriptableObjectHolder holder = ScriptableObjectHolder.Instance;
            var profile = new LocalPlayerProfile
            {
                PlayerId = localId,
                HostId = currentHostId,
                WorldKey = currentProfileWorldKey,
                SaveIndex = holder.ThisVariousSaveData.saveFileIndex,
                Scene = currentScene,
                SavedUtcTicks = DateTime.UtcNow.Ticks,
                PlayerData = holder.ThisPlayerData.GetSaveData(),
                UpgradeData = holder.ThisUpgradePathData.GetSaveData(),
                GadgetData = holder.ThisGadgetChoiceDataCollection.GetSaveData(),
                CarriedInventory = CaptureCarriedInventory(),
                SecondaryWeaponPieces = holder.ThisVariousSaveData.AmountOfcollectedPiecesForSecondaryWeaponPartParent,
                GadgetFields = holder.ThisVariousSaveData.gadgetFieldSaveDataContainers,
                RocketTrail1Unlocked = holder.ThisVariousSaveData.rocketTrail1Unlocked,
                RocketTrail2Unlocked = holder.ThisVariousSaveData.rocketTrail2Unlocked,
                EquippedRocketTrailIndex = holder.ThisVariousSaveData.equippedRocketTrailIndex
            };
            if (PlayerDrill.Instance != null)
            {
                Transform transform = PlayerDrill.Instance.PlayerRB == null ? PlayerDrill.Instance.transform : PlayerDrill.Instance.PlayerRB.transform;
                profile.HasPosition = true;
                profile.X = transform.position.x;
                profile.Y = transform.position.y;
                profile.Z = transform.position.z;
                profile.Rotation = transform.eulerAngles.z;
            }
            SaveLocalPlayerProfile(profile, logResult);
        }
        catch (Exception ex) { LogWarn("PROFILE", "Could not capture local player profile: " + ex.Message); }
    }

    private static List<CurrencySaveDataContainer> CaptureCarriedInventory()
    {
        var result = new List<CurrencySaveDataContainer>();
        if (StorageManager.Instance != null && StorageManager.Instance.CurrencyInStorage != null)
        {
            foreach (Currency currency in StorageManager.Instance.CurrencyInStorage)
                result.Add(new CurrencySaveDataContainer { resourceType = currency.CurrencyType, resourceAmount = currency.CurrentCurrencyAmount });
        }
        else if (ScriptableObjectHolder.Instance != null && ScriptableObjectHolder.Instance.ThisVariousSaveData.CurrencyCurrentlyInStorage != null)
        {
            foreach (CurrencySaveDataContainer currency in ScriptableObjectHolder.Instance.ThisVariousSaveData.CurrencyCurrentlyInStorage)
                result.Add(new CurrencySaveDataContainer { resourceType = currency.resourceType, resourceAmount = currency.resourceAmount });
        }
        return result;
    }

    private void SaveLocalPlayerProfile(LocalPlayerProfile profile, bool logResult)
    {
        string path = GetLocalProfilePath(profile.HostId, profile.SaveIndex);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, JsonUtility.ToJson(profile, true));
        if (logResult) Log("PROFILE", "Saved player-owned reconnect profile for " + profile.WorldKey);
    }

    private void UpdatePlayerSessionSynchronization()
    {
        if (isHost) ProcessPendingPlayerJoins();
        bool stateConnected = replication != null && replication.IsConnected;
        if (!isHost && stateConnected != stateChannelWasConnected)
        {
            stateChannelWasConnected = stateConnected;
            replicationHelloAcknowledged = false;
            replicationHelloTimer = 1f;
            Log("RECONNECT", stateConnected ? "Player state channel connected; identifying persistent player" : "Player state channel interrupted; automatic state reconnect is active");
        }
        if (!isHost && Connected && stateConnected && !replicationHelloAcknowledged && (replicationHelloTimer += Time.unscaledDeltaTime) >= 1f)
        {
            replicationHelloTimer = 0f;
            replication.Send("I|" + localId + "|" + B64(playerName) + "|" + B64(currentProfileWorldKey));
        }
        if (!isHost && Connected)
        {
            if (TransportConnected) { transportLostTimer = 0f; transportLossHandled = false; }
            else if (!transportLossHandled && (transportLostTimer += Time.unscaledDeltaTime) >= 10f)
            {
                transportLossHandled = true;
                string message = "Connection lost — reconnect with the same join code";
                LogWarn("RECONNECT", message + "; persistent ID=" + localId.Substring(0, 8));
                panelOpen = true;
                StopNetwork(message);
                return;
            }
        }
        if (!isHost && sceneReady && (localProfileTimer += Time.unscaledDeltaTime) >= 5f)
        {
            localProfileTimer = 0f;
            CaptureLocalPlayerProfile(false);
        }
        if (isHost && reconnectRecordsDirty && (reconnectRecordFlushTimer += Time.unscaledDeltaTime) >= 5f)
        {
            reconnectRecordFlushTimer = 0f;
            SaveHostReconnectRecords(false);
        }
        TryApplyPendingResume();
    }

    private void TryApplyPendingResume()
    {
        if (pendingResume == null || !sceneReady || PlayerDrill.Instance == null || PlayerDrill.Instance.PlayerRB == null) return;
        if (Time.unscaledTime > pendingResume.ExpiresAt) { LogWarn("RECONNECT", "Retained position expired before the world became ready"); pendingResume = null; return; }
        if (string.IsNullOrEmpty(currentProfileWorldKey) || pendingResume.WorldKey != currentProfileWorldKey || pendingResume.Scene != currentScene) return;
        Rigidbody2D body = PlayerDrill.Instance.PlayerRB;
        body.position = pendingResume.Position;
        body.velocity = Vector2.zero;
        body.angularVelocity = 0f;
        body.transform.position = pendingResume.Position;
        body.transform.rotation = Quaternion.Euler(0f, 0f, pendingResume.Rotation);
        Log("RECONNECT", "Restored player position from " + pendingResume.Source + " at " + F(pendingResume.Position.x) + "," + F(pendingResume.Position.y));
        pendingResume = null;
    }

    private void BeforeNetworkStop(string reason)
    {
        if (!isHost && sceneReady)
        {
            CaptureLocalPlayerProfile(true);
            if (replication != null && replication.IsConnected) replication.Send("Q|" + localId + "|" + B64(playerName));
        }
        if (isHost) SaveHostReconnectRecords(true);
    }

    private void ResetPlayerSessionNetworkState()
    {
        replicationConnectionsByPlayer.Clear();
        resumeSentConnections.Clear();
        pendingPlayerJoins.Clear();
        reconnectRecords.Clear();
        currentHostId = "";
        currentProfileWorldKey = "";
        localProfileTimer = 0f;
        reconnectRecordFlushTimer = 0f;
        replicationHelloTimer = 0f;
        transportLostTimer = 0f;
        replicationHelloAcknowledged = false;
        stateChannelWasConnected = false;
        transportLossHandled = false;
        reconnectRecordsDirty = false;
        pendingResume = null;
    }

    private void HandleReplicationDisconnected(int connectionId)
    {
        string id;
        if (!replicationPeerIds.TryGetValue(connectionId, out id)) return;
        replicationPeerIds.Remove(connectionId);
        int activeConnection;
        if (replicationConnectionsByPlayer.TryGetValue(id, out activeConnection) && activeConnection == connectionId) replicationConnectionsByPlayer.Remove(id);
        resumeSentConnections.Remove(connectionId);
        bootstrapSentConnections.Remove(connectionId);
        pendingPlayerJoins.Remove(connectionId);
        currencyRequestSequences.Remove(id);
        enemyKillRequestSequences.Remove(id);
        peerNames.Remove(id);
        GameObject avatar;
        if (avatars.TryGetValue(id, out avatar))
        {
            if (avatar != null) UnityEngine.Object.Destroy(avatar);
            avatars.Remove(id);
        }
        if (isHost) SaveHostReconnectRecords(false);
        Log("RECONNECT", "Player state connection closed for " + id.Substring(0, 8) + "; identity, progression profile, and last position retained");
    }

}
