using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using EZNet;
using HarmonyLib;
using MelonLoader;
using Steamworks;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CosmodrillMultiplayer;

public sealed partial class MultiplayerMod : MelonMod
{
    private static MultiplayerMod active;
    private static readonly HashSet<int> managedNetIds = new HashSet<int>();
    private readonly ConcurrentQueue<Action> mainThread = new ConcurrentQueue<Action>();
    private readonly Dictionary<string, GameObject> avatars = new Dictionary<string, GameObject>();
    private readonly Dictionary<string, string> peerNames = new Dictionary<string, string>();
    private readonly Dictionary<int, string> replicationPeerIds = new Dictionary<int, string>();
    private string localId;
    private BasicNetManager net;
    private NetClient clientTransport;
    private NetServer serverTransport;
    private ReplicationChannel replication;
    private string status = "Offline";
    private string joinInput = "";
    private string manualIp = "127.0.0.1";
    private string manualPort = "27850";
    private string joinCode = "";
    private string playerName = Environment.UserName;
    private string currentScene = "";
    private bool panelOpen;
    private bool isHost;
    private bool sceneReady;
    private bool autoHosting;
    private bool handshakePending;
    private bool handshakeSent;
    private bool sessionApproved;
    private float sendTimer;
    private float readinessLogTimer;
    private float trafficLogTimer;
    private int positionsSent;
    private int positionsReceived;

    private bool TransportConnected => net != null && ((isHost && net.server != null && net.server.running) || (!isHost && ((net.client != null && net.client.connected) || (replication != null && replication.IsConnected))));
    private bool Connected => net != null && sessionApproved;

    public override void OnInitializeMelon()
    {
        active = this;
        Application.runInBackground = true;
        LoadOrCreatePlayerId();
        InstallGameplayPatches();
        InstallPortableCopyPatches();
        LoadPlayerName();
        currentScene = SceneManager.GetActiveScene().name;
        Log("BOOT", "Using Cosmodrill EZNet transport; peer=" + localId.Substring(0, 8));
    }

    private void InstallGameplayPatches()
    {
        InstallEconomyPatches();
        InstallEnemyPatches();
    }

    private void InstallPortableCopyPatches()
    {
        string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        bool portable = File.Exists(Path.Combine(root, "steam_appid.txt"));
        if (!portable) return;
        try
        {
            var harmony = new global::HarmonyLib.Harmony("cosmodrill.multiplayer.portable");
            harmony.Patch(global::HarmonyLib.AccessTools.Method(typeof(SteamAPI), "RestartAppIfNecessary"),
                prefix: new global::HarmonyLib.HarmonyMethod(typeof(MultiplayerMod), nameof(SkipSteamRestart)));
            harmony.Patch(global::HarmonyLib.AccessTools.Method(typeof(SaveManager), "GetPath"),
                postfix: new global::HarmonyLib.HarmonyMethod(typeof(MultiplayerMod), nameof(UsePortableSavePath)));
            harmony.Patch(global::HarmonyLib.AccessTools.Method(typeof(NetClient), "OnCommandReceived"),
                postfix: new global::HarmonyLib.HarmonyMethod(typeof(MultiplayerMod), nameof(OnEzNetClientCommand)));
            harmony.Patch(global::HarmonyLib.AccessTools.Method(typeof(BasicNetManager), "Update"),
                prefix: new global::HarmonyLib.HarmonyMethod(typeof(MultiplayerMod), nameof(SkipManagedEzNetUpdate)));
            Log("PORTABLE", "Desktop-copy mode enabled; Steam relaunch suppressed and saves isolated under " + Path.Combine(root, "UserData", "Saves"));
        }
        catch (Exception ex) { LogError("PORTABLE", "Could not install portable-copy patches: " + ex); }
    }

    private static bool SkipSteamRestart(ref bool __result)
    {
        __result = false;
        return false;
    }

    private static void UsePortableSavePath(ref string __result)
    {
        string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string directory = Path.Combine(root, "UserData", "Saves");
        Directory.CreateDirectory(directory);
        __result = Path.Combine(directory, "save");
    }

    private static void OnEzNetClientCommand(NetClient __instance, NetCMD cmd)
    {
        if (active == null || cmd == null || string.IsNullOrEmpty(cmd.command) || !cmd.command.StartsWith("/setid")) return;
        byte assigned;
        if (!byte.TryParse(NetCMD.ExtractArgs(cmd.command).Trim(), out assigned) || assigned == 0) return;
        active.mainThread.Enqueue(() => active.SendHandshakeWithAssignedId(__instance, assigned));
    }

    private static bool SkipManagedEzNetUpdate(BasicNetManager __instance) => __instance == null || !managedNetIds.Contains(__instance.GetInstanceID());

    private void SendHandshakeWithAssignedId(NetClient client, byte assigned)
    {
        if (!handshakePending || handshakeSent || client == null) return;
        client.cid = assigned;
        handshakeSent = true;
        handshakePending = false;
        client.SendCommand("/cdmp-h " + localId + "|" + B64(playerName));
        Log("CLIENT", "Name handshake sent from EZNet /setid callback with client ID " + assigned);
    }

    public override void OnSceneWasLoaded(int buildIndex, string sceneName)
    {
        currentScene = sceneName; sceneReady = false; hookedDrill = null; currencySyncTimer = 2f; pendingRevealTimer = 0f; pendingEnemyTimer = 0f; pendingLightingReveals.Clear(); pendingEnemyDeaths.Clear(); requestedEnemyDeaths.Clear(); appliedEnemyDeaths.Clear(); recentlyAppliedEnemyLocators.Clear(); ClearAvatars();
        Log("SCENE", "Loaded " + sceneName + "; waiting for save, player, and tilemaps");
        if (Connected && isHost) Send("/cdmp-s " + B64(sceneName), true);
    }

    public override void OnUpdate()
    {
        Action action; while (mainThread.TryDequeue(out action)) action();
        PumpEzNet();
        RefreshPlayerRegistry();
        TrySendHandshake("update");
        UpdateReadiness(); UpdatePlayerSessionSynchronization(); HookDrill(); UpdateCurrencySynchronization(); ProcessPendingLightingReveals(); ProcessPendingEnemyDeaths();
        if (Connected && sceneReady && PlayerDrill.Instance != null && (sendTimer += Time.unscaledDeltaTime) >= 0.2f)
        {
            sendTimer = 0f; Transform t = PlayerDrill.Instance.PlayerRB == null ? PlayerDrill.Instance.transform : PlayerDrill.Instance.PlayerRB.transform;
            Vector2 velocity = PlayerDrill.Instance.PlayerRB == null ? Vector2.zero : PlayerDrill.Instance.PlayerRB.velocity;
            bool drilling = PlayerDrill.Instance.DrillAnimator != null && PlayerDrill.Instance.DrillAnimator.GetBool("Drilling");
            bool moving = velocity.sqrMagnitude > 0.04f;
            bool leftJet = PlayerAlternativeMovement2.Instance != null && PlayerAlternativeMovement2.Instance.leftRocket != null && PlayerAlternativeMovement2.Instance.leftRocket.enabled;
            bool rightJet = PlayerAlternativeMovement2.Instance != null && PlayerAlternativeMovement2.Instance.rightRocket != null && PlayerAlternativeMovement2.Instance.rightRocket.enabled;
            if (replication != null && replication.Send("P|" + localId + "|" + B64(playerName) + "|" + F(t.position.x) + "|" + F(t.position.y) + "|" + F(t.position.z) + "|" + F(t.eulerAngles.z) + "|" + F(velocity.x) + "|" + F(velocity.y) + "|" + (drilling ? "1" : "0") + "|" + (moving ? "1" : "0") + "|" + (leftJet ? "1" : "0") + "|" + (rightJet ? "1" : "0"))) positionsSent++;
        }
        if (Connected && (trafficLogTimer += Time.unscaledDeltaTime) >= 5f) { trafficLogTimer = 0f; Log("TRAFFIC", "Positions sent=" + positionsSent + ", received=" + positionsReceived + ", avatars=" + avatars.Count + "; tiles sent=" + tilesSent + ", received=" + tilesReceived + ", lighting reveals=" + lightingRevealsApplied + ", pending=" + pendingLightingReveals.Count + "; enemy kills requested=" + enemyKillsRequested + ", confirmed=" + enemyKillsConfirmed + ", applied=" + enemyKillsApplied + ", pending=" + pendingEnemyDeaths.Count + "; economy deltas sent=" + currencyDeltasSent + ", received=" + currencyDeltasReceived + ", snapshots sent=" + currencySnapshotsSent + ", received=" + currencySnapshotsReceived); }
    }

    private void RefreshPlayerRegistry()
    {
        // Persistent /cdmp and state-channel identities now provide the registry.
        // Remove legacy EZNet fallback rows so a disconnected transport entry
        // cannot remain as a duplicate or ghost player in the co-op panel.
        foreach (string fallback in peerNames.Keys.Where(key => key.StartsWith("eznet-", StringComparison.Ordinal)).ToList()) peerNames.Remove(fallback);
    }

    public override void OnGUI()
    {
        DrawTeammateLocator();
        if (!panelOpen)
        {
            if (GUI.Button(new Rect(Screen.width - 225, 30, 190, 44), currentScene == "MainMenu" ? "CO-OP MULTIPLAYER" : "CO-OP")) panelOpen = true;
            if (Connected) GUI.Box(new Rect(Screen.width - 225, 78, 190, 30), (isHost ? "HOST — " : "CLIENT — ") + playerName);
            return;
        }
        float x = Screen.width - 430; GUI.Box(new Rect(x, 20, 400, 490), "COSMODRILL CO-OP");
        GUI.Label(new Rect(x + 20, 45, 360, 22), Connected ? ((isHost ? "ROLE: HOST — " : "ROLE: CLIENT — ") + playerName) : "ROLE: OFFLINE");
        GUI.Label(new Rect(x + 20, 65, 360, 28), status);
        if (!Connected)
        {
            GUI.Label(new Rect(x + 20, 88, 360, 22), "Player name");
            playerName = GUI.TextField(new Rect(x + 20, 111, 245, 34), playerName, 24);
            if (GUI.Button(new Rect(x + 275, 111, 105, 34), "SAVE")) SavePlayerName();
            if (GUI.Button(new Rect(x + 20, 158, 360, 42), autoHosting ? "SETTING UP UPnP..." : "AUTOMATIC HOST") && !autoHosting) StartAutoHost();
            GUI.Label(new Rect(x + 20, 211, 170, 22), "Manual public IP");
            GUI.Label(new Rect(x + 215, 211, 165, 22), "TCP port (+2 state)");
            manualIp = GUI.TextField(new Rect(x + 20, 234, 185, 34), manualIp);
            manualPort = GUI.TextField(new Rect(x + 215, 234, 165, 34), manualPort);
            if (GUI.Button(new Rect(x + 20, 276, 360, 38), "MANUAL HOST")) StartManualHost();
            GUI.Label(new Rect(x + 20, 328, 360, 22), "Nine-character join code");
            joinInput = GUI.TextField(new Rect(x + 20, 351, 360, 34), joinInput, 9);
            if (GUI.Button(new Rect(x + 20, 395, 360, 40), "JOIN WORLD")) Join();
        }
        else
        {
            GUI.Label(new Rect(x + 20, 100, 360, 30), isHost ? "Join code: " + joinCode : "Connected to host");
            if (isHost && GUI.Button(new Rect(x + 20, 138, 360, 38), "COPY JOIN CODE")) GUIUtility.systemCopyBuffer = joinCode;
            GUI.Label(new Rect(x + 20, 184, 360, 24), "Connected players (" + (peerNames.Count + 1) + "):");
            GUI.Label(new Rect(x + 35, 208, 335, 22), "• " + playerName + (isHost ? " (Host)" : ""));
            int peerRow = 1;
            foreach (KeyValuePair<string, string> peer in peerNames)
            {
                if (peer.Key == localId || peerRow >= 5) continue;
                GUI.Label(new Rect(x + 35, 208 + peerRow * 22, 335, 22), "• " + peer.Value + ((!isHost && peerRow == 1) ? " (Host)" : ""));
                peerRow++;
            }
            GUI.Label(new Rect(x + 20, 320, 360, 42), isHost ? "Choose a save normally. Guests follow your scene." : "The host controls scene travel.");
            if (GUI.Button(new Rect(x + 20, 362, 175, 38), "DISCONNECT")) StopNetwork("Disconnected");
            if (GUI.Button(new Rect(x + 205, 362, 175, 38), "LOCATOR: " + (teammateLocatorEnabled ? "ON" : "OFF"))) teammateLocatorEnabled = !teammateLocatorEnabled;
        }
        if (GUI.Button(new Rect(x + 210, 448, 170, 34), "CLOSE")) panelOpen = false;
    }

    private async void StartAutoHost()
    {
        autoHosting = true; status = "Discovering router..."; Log("UPNP", "Automatic host started");
        AutoHostResult result = await AutoHost.Run(m => Log("UPNP", m));
        mainThread.Enqueue(() =>
        {
            autoHosting = false;
            if (!result.Success) { status = result.Error; LogWarn("UPNP", result.Error); return; }
            StartHost(result.Address, result.Port);
        });
    }

    private void StartManualHost()
    {
        IPAddress ip; ushort port;
        if (!IPAddress.TryParse(manualIp.Trim(), out ip) || !ushort.TryParse(manualPort, out port) || port == ushort.MaxValue) { status = "Invalid IP or TCP port"; return; }
        StartHost(ip, port);
    }

    private void StartHost(IPAddress advertisedIp, ushort port)
    {
        try
        {
            StopNetwork("Starting host"); isHost = true; LoadHostReconnectRecords(); net = CreateManager(true, port, (ushort)(port + 1), 0); net.Init(); serverTransport = net.server; net.ServerStart();
            replication = new ReplicationChannel(OnReplicationLine, OnReplicationDisconnected, m => Log("STATE", m)); replication.StartServer(port + 2); sessionApproved = true;
            joinCode = JoinCode.Encode(advertisedIp, port); status = "Hosting on TCP " + port + " / UDP " + (port + 1);
            Log("SERVER", "Started; advertised=" + advertisedIp + ", TCP=" + port + ", UDP=" + (port + 1) + ", code=" + joinCode);
        }
        catch (Exception ex) { status = "Host failed: " + ex.Message; LogError("SERVER", ex.ToString()); ForceCloseServerSockets(); StopNetwork(status); }
    }

    private void Join()
    {
        IPAddress ip; ushort port; string error;
        if (!JoinCode.TryDecode(joinInput, out ip, out port, out error)) { status = error; return; }
        try
        {
            StopNetwork("Connecting"); isHost = false; int localUdp = new System.Random().Next(20000, 48000);
            net = CreateManager(false, port, (ushort)(port + 1), localUdp); net.Init(); clientTransport = net.client; net.ClientConnect(ip.ToString());
            replication = new ReplicationChannel(OnReplicationLine, OnReplicationDisconnected, m => Log("STATE", m)); replication.StartClient(ip.ToString(), port + 2);
            handshakePending = true; handshakeSent = false; status = "Connecting to " + ip + ":" + port;
            Log("CLIENT", "Connecting; host=" + ip + ", TCP=" + port + ", UDP=" + (port + 1) + ", localUDP=" + localUdp);
        }
        catch (Exception ex) { status = "Join failed: " + ex.Message; LogError("CLIENT", ex.ToString()); StopNetwork(status); }
    }

    private BasicNetManager CreateManager(bool host, int tcp, int serverUdp, int clientUdp)
    {
        GameObject go = new GameObject("CosmodrillMultiplayer_EZNet"); UnityEngine.Object.DontDestroyOnLoad(go);
        BasicNetManager manager = go.AddComponent<BasicNetManager>(); manager.servermode = host; manager.Port = tcp; manager.ServerUDPPort = serverUdp; manager.ClientUDPPort = clientUdp; manager.ticksPerSecond = 10;
        managedNetIds.Add(manager.GetInstanceID());
        manager.onLog = m => Log("EZNET", m); manager.onCMD = OnCommand;
        manager.onLobbyInfoSignal = delegate(EZNet.LobbyInfo info, ref Dictionary<byte, string> names)
        {
            if (!host && handshakePending && !handshakeSent && manager.client != null && manager.client.cid != 0)
            {
                handshakeSent = true;
                handshakePending = false;
                manager.client.SendCommand("/cdmp-h " + localId + "|" + B64(playerName));
                Log("CLIENT", "Name handshake sent on EZNet main-thread signal with client ID " + manager.client.cid);
            }
        };
        manager.onClientConnectedSignal = cid => { Log("PEER", "Client ID " + cid + " transport connected"); if (isHost) SendWelcome(manager, cid); };
        manager.onRemoteTileEdits = e => { }; manager.onLocalTileEdits = e => { }; manager.onRemotePlayerAction = (cid, a) => false;
        manager.onLocalPlayerAction = a => { }; manager.onActionResult = a => { }; manager.onTempEntitySignal = s => { }; manager.onEntityCommand = c => false;
        manager.onResourceUpdate = r => { }; manager.onNetInfectionEvent = e => { }; manager.onPJobEvent = e => { }; manager.onPJobRequest = (cid, e) => { };
        return manager;
    }

    private void PumpEzNet()
    {
        if (net == null || !net.init) return;
        NetCMD command;
        while (BasicNetManager.COMMAND_RELAY != null && BasicNetManager.COMMAND_RELAY.TryDequeue(out command)) OnCommand(command);
        ThreadedSignal signal;
        while (ThreadedSignal.Queue != null && ThreadedSignal.Queue.TryDequeue(out signal))
        {
            if (signal.type == 1 && isHost && net.server != null)
            {
                Log("PEER", "Client ID " + signal.info_cid + " transport connected");
                SendWelcome(net, signal.info_cid);
            }
            else if (signal.type == 2 && !isHost) TrySendHandshake("EZNet signal");
        }
        string line;
        if (isHost && net.server != null)
            while (net.server.THREADEDLOG.TryDequeue(out line)) Log("EZNET", line);
        else if (!isHost && net.client != null)
            while (net.client.THREADEDLOG.TryDequeue(out line)) Log("EZNET", line);
    }

    private void SendWelcome(BasicNetManager manager, byte clientId)
    {
        manager.server.SendCommand(clientId, "/cdmp-w " + clientId + "|" + B64(currentScene) + "|" + localId + "|" + B64(playerName));
        Log("SESSION", "Welcome sent to client ID " + clientId + " for " + currentScene);
    }

    private void OnCommand(NetCMD cmd)
    {
        if (string.IsNullOrEmpty(cmd.command) || !cmd.command.StartsWith("/cdmp-")) return;
        if (isHost && cmd.cid != 0) net.server.SendCommandToAll(cmd.command);
        string type = NetCMD.ExtractCommand(cmd.command); string data = NetCMD.ExtractArgs(cmd.command).Trim();
        try
        {
            if (type == "/cdmp-h")
            {
                string[] hello = data.Split('|');
                if (hello.Length >= 2) { peerNames[hello[0]] = UB64(hello[1]); Log("PEER", "Handshake from " + peerNames[hello[0]] + " (client " + cmd.cid + ")"); }
                if (isHost && cmd.cid != 0) net.server.SendCommand(cmd.cid, "/cdmp-a " + B64(currentScene) + "|" + localId + "|" + B64(playerName));
            }
            else if (type == "/cdmp-w" && !isHost)
            {
                string[] welcome = data.Split('|');
                byte assigned;
                if (welcome.Length < 4 || !byte.TryParse(welcome[0], out assigned) || assigned == 0) throw new Exception("Invalid server welcome");
                net.client.cid = assigned;
                AccessTools.Field(typeof(NetClient), "cidinit").SetValue(net.client, true);
                if (welcome[2].Length != 32) throw new Exception("Invalid persistent host ID");
                currentHostId = welcome[2];
                peerNames[welcome[2]] = UB64(welcome[3]);
                sessionApproved = true;
                worldBootstrapApplied = false;
                bootstrapScene = UB64(welcome[1]);
                status = "Connected — waiting for host world";
                Log("SESSION", "Server approved connection as client ID " + assigned + "; host=" + peerNames[welcome[2]] + "; awaiting world snapshot");
            }
            else if (type == "/cdmp-a" && !isHost)
            {
                string[] accepted = data.Split('|');
                if (accepted.Length >= 3) { currentHostId = accepted[1]; peerNames[accepted[1]] = UB64(accepted[2]); }
                string acceptedScene = accepted.Length > 0 ? UB64(accepted[0]) : "";
                if (worldBootstrapApplied)
                {
                    status = "Connected to host";
                    Travel(acceptedScene);
                    Log("CLIENT", "Host accepted connection; authoritative world is ready");
                }
                else
                {
                    if (!string.IsNullOrEmpty(acceptedScene)) bootstrapScene = acceptedScene;
                    status = "Accepted — waiting for host world";
                    Log("CLIENT", "Host accepted connection; scene travel deferred until the authoritative world snapshot is applied");
                }
            }
            else if (type == "/cdmp-s" && !isHost)
            {
                TrySendHandshake("scene command");
                string requestedScene = UB64(data);
                if (worldBootstrapApplied) Travel(requestedScene);
                else { bootstrapScene = requestedScene; Log("SCENE", "Deferred host scene command until world bootstrap completes: " + requestedScene); }
            }
        }
        catch (Exception ex) { LogWarn("PROTOCOL", "Rejected " + type + ": " + ex.Message); }
    }

    private void OnReplicationLine(int connectionId, string line)
    {
        mainThread.Enqueue(() =>
        {
            try
            {
                string[] p = line.Split('|');
                if (p.Length < 2 || p[1] == localId) return;
                string id = p[1];
                if (id.Length != 32) throw new Exception("Invalid player ID");
                if (isHost) BindReplicationIdentity(connectionId, id);
                if (TryHandlePlayerSessionReplication(connectionId, p, id) || TryHandleEnemyReplication(p, id) || TryHandleEconomyReplication(p, id) || TryHandleWorldReplication(p, id)) return;
            }
            catch (Exception ex) { LogWarn("STATE", "Rejected replication state: " + ex.Message); }
        });
    }

    private void BindReplicationIdentity(int connectionId, string id)
    {
        string bound; if (replicationPeerIds.TryGetValue(connectionId, out bound) && bound != id) throw new Exception("Connection attempted to change player identity");
        replicationPeerIds[connectionId] = id;
        RegisterReplicationIdentity(connectionId, id);
    }

    private void OnReplicationDisconnected(int connectionId)
    {
        if (connectionId < 0) return;
        mainThread.Enqueue(() => HandleReplicationDisconnected(connectionId));
    }

    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    private void Send(string command, bool reliable)
    {
        if (!Connected) return;
        if (isHost) { if (serverTransport != null) serverTransport.SendCommandToAll(command); }
        else if (clientTransport != null)
        {
            AccessTools.Field(typeof(NetClient), "cidinit").SetValue(clientTransport, true);
            if (reliable) clientTransport.SendCommand(command); else clientTransport.SendCommandUDP(command);
        }
    }

    private void TrySendHandshake(string source)
    {
        if (!handshakePending || handshakeSent || clientTransport == null) return;
        System.Threading.Thread.MemoryBarrier();
        byte clientId = clientTransport.cid;
        if (clientId == 0) return;
        handshakeSent = true;
        handshakePending = false;
        clientTransport.SendCommand("/cdmp-h " + localId + "|" + B64(playerName));
        Log("CLIENT", "Name handshake sent from " + source + " with client ID " + clientId);
    }

    private void Travel(string scene)
    {
        if (string.IsNullOrEmpty(scene) || scene == currentScene) return; sceneReady = false; status = "Loading " + scene;
        if (!isHost && ScriptableObjectHolder.Instance != null && ScriptableObjectHolder.Instance.ThisVariousSaveData.saveFileIndex == -1)
        {
            ScriptableObjectHolder.Instance.LoadDefaultData();
            Log("SYNC", "Initialized guest defaults before host-directed scene load");
        }
        Log("SCENE", "Host-directed travel to " + scene); if (SceneLoader.Instance != null) SceneLoader.Instance.LoadScene(scene); else SceneManager.LoadScene(scene);
    }

    private void UpdateReadiness()
    {
        bool gameplay = currentScene != "MainMenu" && currentScene != "MissionMode";
        bool saveReady = ScriptableObjectHolder.Instance != null && (ScriptableObjectHolder.Instance.hasLoaded || (Connected && !isHost));
        bool ready = gameplay && PlayerDrill.Instance != null && saveReady;
        if (ready && !sceneReady) { sceneReady = true; status = isHost ? "World online — hosting" : "World online — connected"; Log("SYNC", "World ready in " + currentScene); }
        else if (TransportConnected && !sceneReady && (readinessLogTimer += Time.unscaledDeltaTime) >= 5f)
        {
            readinessLogTimer = 0f;
            Log("SYNC", "Waiting for world: scene=" + currentScene + ", approved=" + sessionApproved + ", player=" + (PlayerDrill.Instance != null) + ", saveHolder=" + (ScriptableObjectHolder.Instance != null) + ", saveReady=" + saveReady);
        }
    }

    private void ApplyPosition(string id, float x, float y, float z, float r, float vx, float vy, bool drilling, bool moving, bool leftJet, bool rightJet)
    {
        if (PlayerDrill.Instance == null || PlayerDrill.Instance.PlayerRB == null || PlayerHealth.Instance == null || RocketVisualizer.Instance == null || RocketTrail.Instance == null || PlayerAlternativeMovement2.Instance == null) return;
        GameObject a = GetAvatar(id); a.GetComponent<RemotePlayerAvatar>().SetTarget(new Vector3(x, y, z), r, new Vector2(vx, vy), drilling, moving, leftJet, rightJet);
    }
    private GameObject GetAvatar(string id)
    {
        GameObject a; if (avatars.TryGetValue(id, out a) && a != null) return a;
        a = new GameObject("RemotePlayer_" + id.Substring(0, 6));
        GameObject visuals = new GameObject("RemoteFullBody"); visuals.transform.SetParent(a.transform, false);
        GameObject playerRoot = PlayerDrill.Instance != null && PlayerDrill.Instance.PlayerRB != null ? PlayerDrill.Instance.PlayerRB.gameObject : null;
        GameObject sourceModel = PlayerDrill.Instance != null && PlayerDrill.Instance.DrillAnimator != null ? PlayerDrill.Instance.DrillAnimator.gameObject : null;
        GameObject model = sourceModel == null ? null : UnityEngine.Object.Instantiate(sourceModel);
        if (model != null)
        {
            model.name = "RemoteAnimatedDrill"; model.transform.SetParent(visuals.transform, false);
            if (playerRoot != null) CopyRelativeTransform(sourceModel.transform, playerRoot.transform, model.transform);
            model.SetActive(true);
            foreach (MonoBehaviour script in model.GetComponentsInChildren<MonoBehaviour>(true)) UnityEngine.Object.Destroy(script);
            foreach (Collider2D collider in model.GetComponentsInChildren<Collider2D>(true)) UnityEngine.Object.Destroy(collider);
            foreach (Rigidbody2D body in model.GetComponentsInChildren<Rigidbody2D>(true)) UnityEngine.Object.Destroy(body);
            foreach (Transform child in model.GetComponentsInChildren<Transform>(true)) child.gameObject.SetActive(true);
            foreach (SpriteRenderer renderer in model.GetComponentsInChildren<SpriteRenderer>(true))
            {
                // The local player is cloned while Respawner may still have the ship hidden,
                // black/transparent, and on sorting order -1. Remote visuals must not retain
                // that transient local-only spawn state.
                renderer.enabled = true;
                renderer.color = Color.white;
                renderer.sortingOrder = RemoteBodySortingOrder(renderer.gameObject.name);
            }
        }
        int bodyParts = 0, visibleBodyParts = 0, recoveredHiddenBodyParts = 0;
        SpriteRenderer remoteMainRocket = null, remoteLeftRocket = null, remoteRightRocket = null;
        if (playerRoot != null)
        {
            SpriteRenderer mainSource = RocketVisualizer.Instance == null ? null : RocketVisualizer.Instance.GetComponent<SpriteRenderer>();
            SpriteRenderer leftSource = PlayerAlternativeMovement2.Instance == null ? null : PlayerAlternativeMovement2.Instance.leftRocket;
            SpriteRenderer rightSource = PlayerAlternativeMovement2.Instance == null ? null : PlayerAlternativeMovement2.Instance.rightRocket;
            var sources = new HashSet<SpriteRenderer>();
            var bodySources = new HashSet<SpriteRenderer>();
            foreach (SpriteRenderer source in playerRoot.GetComponentsInChildren<SpriteRenderer>(true)) if (source != null) sources.Add(source);
            if (PlayerHealth.Instance != null) foreach (SpriteRenderer source in PlayerHealth.Instance.GetComponentsInChildren<SpriteRenderer>(true)) if (source != null) sources.Add(source);
            if (PlayerHealth.Instance != null && PlayerHealth.Instance.visuals != null) foreach (SpriteRenderer source in PlayerHealth.Instance.visuals) if (source != null) { sources.Add(source); bodySources.Add(source); }
            if (Respawner.Instance != null && Respawner.Instance.PlayerRenderers != null) foreach (SpriteRenderer source in Respawner.Instance.PlayerRenderers) if (source != null) { sources.Add(source); bodySources.Add(source); }
            if (mainSource != null) sources.Add(mainSource); if (leftSource != null) sources.Add(leftSource); if (rightSource != null) sources.Add(rightSource);
            foreach (SpriteRenderer source in sources)
            {
                if (source == null || source.sprite == null || (sourceModel != null && source.transform.IsChildOf(sourceModel.transform))) continue;
                GameObject part = new GameObject("Remote_" + source.gameObject.name); part.transform.SetParent(visuals.transform, false); part.layer = source.gameObject.layer;
                CopyRelativeTransform(source.transform, playerRoot.transform, part.transform);
                SpriteRenderer renderer = part.AddComponent<SpriteRenderer>();
                renderer.sprite = source.sprite;
                renderer.sharedMaterial = source.sharedMaterial;
                renderer.flipX = source.flipX; renderer.flipY = source.flipY;
                renderer.sortingLayerID = source.sortingLayerID;
                bool isBody = bodySources.Contains(source);
                renderer.sortingOrder = isBody ? RemoteBodySortingOrder(source.gameObject.name) : source.sortingOrder + 10;
                renderer.color = isBody ? Color.white : new Color(source.color.r, source.color.g, source.color.b, 1f);
                renderer.enabled = isBody || source.enabled;
                if (isBody)
                {
                    visibleBodyParts++;
                    if (!source.enabled || source.color.a < 0.99f || source.sortingOrder < 100) recoveredHiddenBodyParts++;
                }
                bodyParts++;
                if (source == mainSource) remoteMainRocket = renderer;
                if (source == leftSource) remoteLeftRocket = renderer;
                if (source == rightSource) remoteRightRocket = renderer;
            }
        }
        if (model == null && bodyParts == 0)
        {
            GameObject fallback = GameObject.CreatePrimitive(PrimitiveType.Quad); fallback.name = "RemotePlayerFallback"; fallback.transform.SetParent(a.transform, false); fallback.transform.localScale = new Vector3(.8f, 1.05f, 1);
            fallback.GetComponent<Renderer>().material.color = Color.cyan; UnityEngine.Object.Destroy(fallback.GetComponent<Collider>());
        }
        ParticleSystem remoteTrail = null;
        if (RocketTrail.Instance != null && RocketTrail.Instance.rocketTrail != null)
        {
            ParticleSystem sourceTrail = RocketTrail.Instance.rocketTrail;
            GameObject trailObject = UnityEngine.Object.Instantiate(sourceTrail.gameObject); trailObject.name = "RemoteAnimatedRocketTrail"; trailObject.transform.SetParent(visuals.transform, false);
            if (playerRoot != null) CopyRelativeTransform(sourceTrail.transform, playerRoot.transform, trailObject.transform);
            foreach (MonoBehaviour script in trailObject.GetComponentsInChildren<MonoBehaviour>(true)) UnityEngine.Object.Destroy(script);
            foreach (Collider2D collider in trailObject.GetComponentsInChildren<Collider2D>(true)) UnityEngine.Object.Destroy(collider);
            remoteTrail = trailObject.GetComponentInChildren<ParticleSystem>(true); if (remoteTrail != null) remoteTrail.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        }
        RemotePlayerAvatar networkAvatar = a.AddComponent<RemotePlayerAvatar>(); networkAvatar.Initialize(model == null ? null : model.GetComponentInChildren<Animator>(true), visuals.transform, remoteMainRocket, remoteLeftRocket, remoteRightRocket, remoteTrail);
        avatars[id] = a; Log("AVATAR", "Created complete rocket player " + id.Substring(0, 8) + " from PlayerHealth visuals; animatedDrill=" + (model != null) + ", spriteParts=" + bodyParts + ", bodyParts=" + visibleBodyParts + ", recoveredHidden=" + recoveredHiddenBodyParts + ", mainRocket=" + (remoteMainRocket != null) + ", turnJets=" + (remoteLeftRocket != null && remoteRightRocket != null) + ", particleTrail=" + (remoteTrail != null)); return a;
    }
    private static int RemoteBodySortingOrder(string objectName) => string.Equals(objectName, "DrillSprite", StringComparison.Ordinal) ? 411 : 410;
    private static void CopyRelativeTransform(Transform source, Transform root, Transform target)
    {
        target.localPosition = root.InverseTransformPoint(source.position);
        target.localRotation = Quaternion.Inverse(root.rotation) * source.rotation;
        Vector3 rootScale = root.lossyScale, sourceScale = source.lossyScale;
        target.localScale = new Vector3(rootScale.x == 0 ? 1 : sourceScale.x / rootScale.x, rootScale.y == 0 ? 1 : sourceScale.y / rootScale.y, rootScale.z == 0 ? 1 : sourceScale.z / rootScale.z);
    }
    private void ClearAvatars() { foreach (GameObject a in avatars.Values) if (a != null) UnityEngine.Object.Destroy(a); avatars.Clear(); }
    private void ForceCloseServerSockets()
    {
        if (net == null || net.server == null) return;
        try { var socket = AccessTools.Field(typeof(NetServer), "connectionListener").GetValue(net.server) as System.Net.Sockets.Socket; if (socket != null) socket.Close(); } catch { }
        try { var socket = AccessTools.Field(typeof(NetServer), "udpdata").GetValue(net.server) as System.Net.Sockets.Socket; if (socket != null) socket.Close(); } catch { }
        net.server.running = false;
    }
    private void StopNetwork(string reason)
    {
        BeforeNetworkStop(reason);
        try
        {
            replication?.Stop();
            if (net != null)
            {
                managedNetIds.Remove(net.GetInstanceID());
                if (isHost && serverTransport != null) { if (serverTransport.running) net.ServerStop(); else ForceCloseServerSockets(); }
                else if (clientTransport != null && clientTransport.connected) clientTransport.Disconnect();
                if (net.gameObject != null) UnityEngine.Object.Destroy(net.gameObject);
            }
        }
        catch (Exception ex) { LogWarn("NET", "Shutdown: " + ex.Message); ForceCloseServerSockets(); }
        replication = null;
        replicationPeerIds.Clear();
        currencyRequestSequences.Clear();
        enemyKillRequestSequences.Clear();
        pendingLightingReveals.Clear();
        pendingEnemyDeaths.Clear();
        requestedEnemyDeaths.Clear();
        appliedEnemyDeaths.Clear();
        recentlyAppliedEnemyLocators.Clear();
        bootstrapSentConnections.Clear();
        net = null; clientTransport = null; serverTransport = null;
        isHost = false; sceneReady = false; sessionApproved = false; handshakePending = false; handshakeSent = false; worldBootstrapApplied = false;
        applyingSharedCurrency = false; stationCurrencyRequestActive = false; stationCurrencyRequestSucceeded = false; applyingRemoteTile = false; applyingEnemyDeath = false;
        positionsSent = 0; positionsReceived = 0; tilesSent = 0; tilesReceived = 0;
        currencyDeltasSent = 0; currencyDeltasReceived = 0; currencySnapshotsSent = 0; currencySnapshotsReceived = 0;
        lightingRevealsApplied = 0; pendingRevealTimer = 0f; pendingEnemyTimer = 0f;
        enemyKillsRequested = 0; enemyKillsConfirmed = 0; enemyKillsApplied = 0;
        nextCurrencyRequestSequence = 0; currencyRevision = 0; lastCurrencyRevision = 0; currencySyncTimer = 0f;
        nextEnemyKillSequence = 0; enemyKillRevision = 0;
        bootstrapChunks.Clear(); bootstrapExpected = 0; bootstrapExpectedBytes = 0; bootstrapExpectedHash = ""; bootstrapSaveIndex = -1; bootstrapScene = "";
        ResetPlayerSessionNetworkState();
        status = reason; peerNames.Clear(); ClearAvatars();
    }
    private void LoadPlayerName() { try { string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "UserData", "CosmodrillMultiplayer.name"); if (File.Exists(path)) { string value = File.ReadAllText(path).Trim(); if (!string.IsNullOrEmpty(value)) playerName = value; } } catch { } }
    private void SavePlayerName() { playerName = string.IsNullOrWhiteSpace(playerName) ? "Player" : playerName.Trim(); string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "UserData"); Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "CosmodrillMultiplayer.name"), playerName); status = "Player name saved"; Log("PLAYER", "Name set to " + playerName); }
    public override void OnDeinitializeMelon()
    {
        if (hookedDrill != null) hookedDrill.TileRemoved -= OnTileRemoved;
        if (gadgetBombHooked) GadgetBomb.TileRemoved -= OnBombTileRemoved;
        hookedDrill = null; gadgetBombHooked = false;
        StopNetwork("Stopped");
    }
    private void Log(string a, string m) => LoggerInstance.Msg("[" + a + "] " + m); private void LogWarn(string a, string m) => LoggerInstance.Warning("[" + a + "] " + m); private void LogError(string a, string m) => LoggerInstance.Error("[" + a + "] " + m);
    private static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture); private static float P(string v) => float.Parse(v, CultureInfo.InvariantCulture); private static string B64(string v) => Convert.ToBase64String(Encoding.UTF8.GetBytes(v)); private static string UB64(string v) => Encoding.UTF8.GetString(Convert.FromBase64String(v));
}
