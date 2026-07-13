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
using UnityEngine.Tilemaps;

namespace CosmodrillMultiplayer;

public sealed class MultiplayerMod : MelonMod
{
    private static MultiplayerMod active;
    private static readonly HashSet<int> managedNetIds = new HashSet<int>();
    private readonly ConcurrentQueue<Action> mainThread = new ConcurrentQueue<Action>();
    private readonly Dictionary<string, GameObject> avatars = new Dictionary<string, GameObject>();
    private readonly Dictionary<string, string> peerNames = new Dictionary<string, string>();
    private readonly Dictionary<int, string> replicationPeerIds = new Dictionary<int, string>();
    private readonly Dictionary<string, long> currencyRequestSequences = new Dictionary<string, long>();
    private readonly Dictionary<string, PendingLightingReveal> pendingLightingReveals = new Dictionary<string, PendingLightingReveal>();
    private readonly SortedDictionary<int, string> bootstrapChunks = new SortedDictionary<int, string>();
    private readonly string localId = Guid.NewGuid().ToString("N");
    private BasicNetManager net;
    private NetClient clientTransport;
    private NetServer serverTransport;
    private ReplicationChannel replication;
    private PlayerDrill hookedDrill;
    private bool gadgetBombHooked;
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
    private float currencySyncTimer;
    private float pendingRevealTimer;
    private int positionsSent;
    private int positionsReceived;
    private int tilesSent;
    private int tilesReceived;
    private int currencyDeltasSent;
    private int currencyDeltasReceived;
    private int currencySnapshotsSent;
    private int currencySnapshotsReceived;
    private int lightingRevealsApplied;
    private long nextCurrencyRequestSequence;
    private long currencyRevision;
    private long lastCurrencyRevision;
    private bool applyingSharedCurrency;
    private bool stationCurrencyRequestActive;
    private bool stationCurrencyRequestSucceeded;
    private bool applyingRemoteTile;
    private int bootstrapExpected;
    private int bootstrapSaveIndex = -1;
    private string bootstrapScene = "";

    private sealed class PendingLightingReveal
    {
        internal string MapName;
        internal Vector3Int Cell;
    }

    private bool TransportConnected => net != null && ((isHost && net.server != null && net.server.running) || (!isHost && net.client != null && net.client.connected));
    private bool Connected => net != null && sessionApproved;

    public override void OnInitializeMelon()
    {
        active = this;
        Application.runInBackground = true;
        InstallGameplayPatches();
        InstallPortableCopyPatches();
        LoadPlayerName();
        currentScene = SceneManager.GetActiveScene().name;
        Log("BOOT", "Using Cosmodrill EZNet transport; peer=" + localId.Substring(0, 8));
    }

    private void InstallGameplayPatches()
    {
        try
        {
            var harmony = new global::HarmonyLib.Harmony("cosmodrill.multiplayer.gameplay");
            harmony.Patch(global::HarmonyLib.AccessTools.Method(typeof(PlayerCurrency), nameof(PlayerCurrency.ChangeCurrency), new[] { typeof(ResourceTypes), typeof(float) }),
                prefix: new global::HarmonyLib.HarmonyMethod(typeof(MultiplayerMod), nameof(BeforeCurrencyChange)),
                postfix: new global::HarmonyLib.HarmonyMethod(typeof(MultiplayerMod), nameof(AfterCurrencyChange)));
            harmony.Patch(global::HarmonyLib.AccessTools.Method(typeof(ResourceDeliveryStation), nameof(ResourceDeliveryStation.BuyResourceFromPlayer), new[] { typeof(string) }),
                prefix: new global::HarmonyLib.HarmonyMethod(typeof(MultiplayerMod), nameof(BeforeStationResourcePurchase)),
                postfix: new global::HarmonyLib.HarmonyMethod(typeof(MultiplayerMod), nameof(AfterStationResourcePurchase)));
            Log("ECONOMY", "Installed shared station-inventory hooks");
        }
        catch (Exception ex) { LogError("ECONOMY", "Could not install shared inventory hooks: " + ex); }
    }

    private static bool BeforeCurrencyChange(ResourceTypes __0, float __1)
    {
        return active == null || active.HandleCurrencyChangeRequest(__0, __1);
    }

    private static void AfterCurrencyChange(ResourceTypes __0, float __1)
    {
        if (active == null || active.applyingSharedCurrency || !active.Connected || !active.sceneReady || !active.isHost) return;
        active.BroadcastCurrencySnapshot("host currency changed");
    }

    private static void BeforeStationResourcePurchase(ResourceDeliveryStation __instance)
    {
        if (active == null) return;
        active.stationCurrencyRequestActive = active.Connected && active.sceneReady && !active.isHost && __instance != null && !__instance.IsDestroyedStation;
        active.stationCurrencyRequestSucceeded = false;
    }

    private static void AfterStationResourcePurchase(ref bool __result)
    {
        if (active == null || !active.stationCurrencyRequestActive) return;
        if (!active.stationCurrencyRequestSucceeded) __result = false;
        active.stationCurrencyRequestActive = false;
        active.stationCurrencyRequestSucceeded = false;
    }

    private bool HandleCurrencyChangeRequest(ResourceTypes resourceType, float amount)
    {
        if (applyingSharedCurrency || !Connected || !sceneReady || isHost) return true;
        if (!Finite(amount) || amount == 0f || Math.Abs(amount) > 100000f || !Enum.IsDefined(typeof(ResourceTypes), resourceType))
        {
            LogWarn("ECONOMY", "Blocked invalid guest currency delta for " + resourceType + ": " + amount);
            return false;
        }
        long sequence = ++nextCurrencyRequestSequence;
        bool sent = replication != null && replication.Send("D|" + localId + "|" + sequence + "|" + (int)resourceType + "|" + F(amount));
        if (stationCurrencyRequestActive) stationCurrencyRequestSucceeded = sent;
        if (sent) currencyDeltasSent++;
        else LogWarn("ECONOMY", "Shared inventory request could not reach the host; local balance was left unchanged");
        // Guests never directly mutate the shared ledger. The host's absolute
        // snapshot is applied to every peer, including the requesting guest.
        return false;
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
        currentScene = sceneName; sceneReady = false; hookedDrill = null; currencySyncTimer = 2f; pendingRevealTimer = 0f; pendingLightingReveals.Clear(); ClearAvatars();
        Log("SCENE", "Loaded " + sceneName + "; waiting for save, player, and tilemaps");
        if (Connected && isHost) Send("/cdmp-s " + B64(sceneName), true);
    }

    public override void OnUpdate()
    {
        Action action; while (mainThread.TryDequeue(out action)) action();
        PumpEzNet();
        RefreshPlayerRegistry();
        TrySendHandshake("update");
        UpdateReadiness(); HookDrill(); UpdateCurrencySynchronization(); ProcessPendingLightingReveals();
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
        if (Connected && (trafficLogTimer += Time.unscaledDeltaTime) >= 5f) { trafficLogTimer = 0f; Log("TRAFFIC", "Positions sent=" + positionsSent + ", received=" + positionsReceived + ", avatars=" + avatars.Count + "; tiles sent=" + tilesSent + ", received=" + tilesReceived + ", lighting reveals=" + lightingRevealsApplied + ", pending=" + pendingLightingReveals.Count + "; economy deltas sent=" + currencyDeltasSent + ", received=" + currencyDeltasReceived + ", snapshots sent=" + currencySnapshotsSent + ", received=" + currencySnapshotsReceived); }
    }

    private void RefreshPlayerRegistry()
    {
        if (!Connected || net == null) return;
        if (isHost && net.server != null && net.server.clientNames != null)
        {
            foreach (KeyValuePair<byte, string> entry in net.server.clientNames)
                if (!string.IsNullOrWhiteSpace(entry.Value)) peerNames["eznet-" + entry.Key] = entry.Value.Replace('_', ' ');
        }
        else if (!isHost && net.client != null && net.client.clientNames != null)
        {
            foreach (KeyValuePair<byte, string> entry in net.client.clientNames)
                if (!string.IsNullOrWhiteSpace(entry.Value) && entry.Key != net.client.cid) peerNames["eznet-" + entry.Key] = entry.Value.Replace('_', ' ');
        }
    }

    public override void OnGUI()
    {
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
            StopNetwork("Starting host"); isHost = true; net = CreateManager(true, port, (ushort)(port + 1), 0); net.Init(); serverTransport = net.server; net.ServerStart();
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
        SendWorldBootstrap(manager, clientId);
    }

    private void SendWorldBootstrap(BasicNetManager manager, byte clientId)
    {
        try
        {
            int index = ScriptableObjectHolder.Instance == null ? -1 : ScriptableObjectHolder.Instance.ThisVariousSaveData.saveFileIndex;
            string path = index < 0 ? "" : GetSavePath(index);
            if (index < 0 || !File.Exists(path)) throw new Exception("Host has no active saved world (index " + index + ")");
            byte[] bytes = File.ReadAllBytes(path);
            string encoded = Convert.ToBase64String(bytes);
            const int chunkSize = 700;
            int total = (encoded.Length + chunkSize - 1) / chunkSize;
            manager.server.SendCommand(clientId, "/cdmp-begin " + index + "|" + total + "|" + B64(currentScene) + "|" + bytes.Length);
            for (int i = 0; i < total; i++)
            {
                int length = Math.Min(chunkSize, encoded.Length - i * chunkSize);
                manager.server.SendCommand(clientId, "/cdmp-b " + i + "|" + encoded.Substring(i * chunkSize, length));
            }
            Log("BOOTSTRAP", "Sent save" + index + " snapshot (" + bytes.Length + " bytes, " + total + " chunks) to client " + clientId);
        }
        catch (Exception ex) { LogError("BOOTSTRAP", ex.Message); manager.server.SendCommand(clientId, "/cdmp-f " + B64(ex.Message)); }
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
                peerNames[welcome[2]] = UB64(welcome[3]);
                sessionApproved = true;
                bootstrapScene = UB64(welcome[1]);
                status = "Connected — waiting for host world";
                Log("SESSION", "Server approved connection as client ID " + assigned + "; host=" + peerNames[welcome[2]] + "; awaiting world snapshot");
            }
            else if (type == "/cdmp-begin" && !isHost)
            {
                string[] begin = data.Split('|');
                bootstrapSaveIndex = int.Parse(begin[0]); bootstrapExpected = int.Parse(begin[1]); bootstrapScene = UB64(begin[2]); bootstrapChunks.Clear();
                status = "Receiving host world (0/" + bootstrapExpected + ")";
                Log("BOOTSTRAP", "Receiving save" + bootstrapSaveIndex + " in " + bootstrapExpected + " chunks (" + begin[3] + " bytes)");
            }
            else if (type == "/cdmp-b" && !isHost)
            {
                int split = data.IndexOf('|'); if (split <= 0) throw new Exception("Invalid world chunk");
                int sequence = int.Parse(data.Substring(0, split)); bootstrapChunks[sequence] = data.Substring(split + 1);
                status = "Receiving host world (" + bootstrapChunks.Count + "/" + bootstrapExpected + ")";
                if (bootstrapExpected > 0 && bootstrapChunks.Count == bootstrapExpected) ApplyWorldBootstrap();
            }
            else if (type == "/cdmp-f" && !isHost) { status = "Host world failed: " + UB64(data); LogError("BOOTSTRAP", status); }
            else if (type == "/cdmp-a" && !isHost)
            {
                string[] accepted = data.Split('|');
                if (accepted.Length >= 3) peerNames[accepted[1]] = UB64(accepted[2]);
                status = "Connected to host"; Travel(UB64(accepted[0])); Log("CLIENT", "Host accepted connection");
            }
            else if (type == "/cdmp-s" && !isHost) { TrySendHandshake("scene command"); Travel(UB64(data)); }
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
                if (p[0] == "D")
                {
                    if (!isHost || p.Length != 5) throw new Exception("Invalid currency delta");
                    long sequence = long.Parse(p[2], CultureInfo.InvariantCulture);
                    int typeValue = int.Parse(p[3], CultureInfo.InvariantCulture);
                    float amount = P(p[4]);
                    if (sequence <= 0 || !Enum.IsDefined(typeof(ResourceTypes), typeValue) || !Finite(amount) || amount == 0f || Math.Abs(amount) > 100000f) throw new Exception("Invalid currency delta values");
                    long lastSequence;
                    if (currencyRequestSequences.TryGetValue(id, out lastSequence) && sequence <= lastSequence) throw new Exception("Stale currency delta sequence");
                    currencyRequestSequences[id] = sequence;
                    ApplyAuthoritativeCurrencyDelta(id, (ResourceTypes)typeValue, amount);
                    return;
                }
                if (p[0] == "C")
                {
                    if (isHost || p.Length != 10) throw new Exception("Invalid currency snapshot");
                    long revision = long.Parse(p[2], CultureInfo.InvariantCulture);
                    if (revision <= lastCurrencyRevision) return;
                    float[] amounts = new float[7];
                    for (int i = 0; i < amounts.Length; i++)
                    {
                        amounts[i] = P(p[i + 3]);
                        if (!Finite(amounts[i]) || amounts[i] < 0f || amounts[i] > 100000000f) throw new Exception("Invalid currency snapshot amount");
                    }
                    ApplyCurrencySnapshot(revision, amounts);
                    return;
                }
                if (p[0] == "T")
                {
                    if (p.Length != 6) throw new Exception("Invalid tile state");
                    string mapName = UB64(p[2]); if (mapName.Length == 0 || mapName.Length > 128) throw new Exception("Invalid tilemap name");
                    int cellX = int.Parse(p[3]), cellY = int.Parse(p[4]), cellZ = int.Parse(p[5]);
                    ApplyTile(mapName, cellX, cellY, cellZ); tilesReceived++;
                    if (isHost && replication != null) replication.Send("T|" + id + "|" + B64(mapName) + "|" + cellX + "|" + cellY + "|" + cellZ);
                    return;
                }
                if (p.Length < 10 || p[0] != "P") return;
                string name = UB64(p[2]); if (name.Length > 24) name = name.Substring(0, 24);
                float x = P(p[3]), y = P(p[4]), z = P(p[5]), rotation = P(p[6]), vx = P(p[7]), vy = P(p[8]);
                if (!Finite(x) || !Finite(y) || !Finite(z) || !Finite(rotation) || !Finite(vx) || !Finite(vy)) throw new Exception("Non-finite player state");
                peerNames[id] = name; positionsReceived++;
                bool drilling = p[9] == "1", moving = p.Length > 10 ? p[10] == "1" : (vx * vx + vy * vy > .04f), leftJet = p.Length > 11 && p[11] == "1", rightJet = p.Length > 12 && p[12] == "1";
                ApplyPosition(id, x, y, z, rotation, vx, vy, drilling, moving, leftJet, rightJet);
                if (isHost && replication != null) replication.Send("P|" + id + "|" + B64(name) + "|" + F(x) + "|" + F(y) + "|" + F(z) + "|" + F(rotation) + "|" + F(vx) + "|" + F(vy) + "|" + (drilling ? "1" : "0") + "|" + (moving ? "1" : "0") + "|" + (leftJet ? "1" : "0") + "|" + (rightJet ? "1" : "0"));
            }
            catch (Exception ex) { LogWarn("STATE", "Rejected replication state: " + ex.Message); }
        });
    }

    private void BindReplicationIdentity(int connectionId, string id)
    {
        string bound; if (replicationPeerIds.TryGetValue(connectionId, out bound) && bound != id) throw new Exception("Connection attempted to change player identity");
        replicationPeerIds[connectionId] = id;
    }

    private void UpdateCurrencySynchronization()
    {
        if (!Connected || !sceneReady || !isHost || PlayerCurrency.Instance == null) return;
        currencySyncTimer += Time.unscaledDeltaTime;
        if (currencySyncTimer < 2f) return;
        currencySyncTimer = 0f;
        BroadcastCurrencySnapshot("periodic reconciliation");
    }

    private void ApplyAuthoritativeCurrencyDelta(string playerId, ResourceTypes resourceType, float amount)
    {
        if (PlayerCurrency.Instance == null)
        {
            LogWarn("ECONOMY", "Rejected currency delta because the host inventory is not ready");
            return;
        }
        float current = PlayerCurrency.Instance.GetResourceAmount(resourceType);
        if (amount < 0f && current + amount < -0.001f)
        {
            LogWarn("ECONOMY", "Rejected unaffordable " + resourceType + " spend of " + (-amount) + " from " + playerId.Substring(0, 8) + "; available=" + current);
            BroadcastCurrencySnapshot("rejected unaffordable spend");
            return;
        }
        applyingSharedCurrency = true;
        try { PlayerCurrency.Instance.ChangeCurrency(resourceType, amount); }
        finally { applyingSharedCurrency = false; }
        currencyDeltasReceived++;
        Log("ECONOMY", "Accepted " + playerId.Substring(0, 8) + " " + resourceType + " delta " + (amount > 0f ? "+" : "") + F(amount) + "; shared total=" + F(PlayerCurrency.Instance.GetResourceAmount(resourceType)));
        BroadcastCurrencySnapshot("guest currency delta");
    }

    private void BroadcastCurrencySnapshot(string reason)
    {
        if (!isHost || replication == null || PlayerCurrency.Instance == null) return;
        var frame = new StringBuilder("C|").Append(localId).Append('|').Append(++currencyRevision);
        int resourceCount = Enum.GetValues(typeof(ResourceTypes)).Length;
        for (int i = 0; i < resourceCount; i++) frame.Append('|').Append(F(PlayerCurrency.Instance.GetResourceAmount((ResourceTypes)i)));
        if (replication.Send(frame.ToString())) currencySnapshotsSent++;
    }

    private void ApplyCurrencySnapshot(long revision, float[] amounts)
    {
        if (PlayerCurrency.Instance == null || amounts == null || amounts.Length != Enum.GetValues(typeof(ResourceTypes)).Length) return;
        bool firstSnapshot = lastCurrencyRevision == 0;
        applyingSharedCurrency = true;
        try
        {
            foreach (Currency currency in PlayerCurrency.Instance.CurrencyList)
            {
                int index = (int)currency.CurrencyType;
                if (index < 0 || index >= amounts.Length) continue;
                currency.CurrentCurrencyAmount = amounts[index];
                currency.UpdateCurrencyCounter();
            }
            PlayerCurrency.Instance.CurrencyChanged?.Invoke();
            lastCurrencyRevision = revision;
            currencySnapshotsReceived++;
        }
        finally { applyingSharedCurrency = false; }
        if (firstSnapshot) Log("ECONOMY", "Shared station inventory synchronized at revision " + revision + ": " + FormatCurrencyAmounts(amounts));
    }

    private static string FormatCurrencyAmounts(float[] amounts)
    {
        var result = new StringBuilder();
        for (int i = 0; i < amounts.Length; i++)
        {
            if (i > 0) result.Append(", ");
            result.Append((ResourceTypes)i).Append('=').Append(F(amounts[i]));
        }
        return result.ToString();
    }

    private void OnReplicationDisconnected(int connectionId)
    {
        if (connectionId < 0) return;
        mainThread.Enqueue(() => { string id; if (!replicationPeerIds.TryGetValue(connectionId, out id)) return; replicationPeerIds.Remove(connectionId); currencyRequestSequences.Remove(id); peerNames.Remove(id); GameObject avatar; if (avatars.TryGetValue(id, out avatar)) { if (avatar != null) UnityEngine.Object.Destroy(avatar); avatars.Remove(id); } Log("STATE", "Player state connection closed: " + id.Substring(0, 8)); });
    }

    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    private void ApplyWorldBootstrap()
    {
        var encoded = new StringBuilder();
        for (int i = 0; i < bootstrapExpected; i++) { string chunk; if (!bootstrapChunks.TryGetValue(i, out chunk)) throw new Exception("Missing world chunk " + i); encoded.Append(chunk); }
        byte[] bytes = Convert.FromBase64String(encoded.ToString());
        string path = GetSavePath(bootstrapSaveIndex); Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllBytes(path, bytes);
        if (SaveManager.Instance == null || ScriptableObjectHolder.Instance == null) throw new Exception("Save system is unavailable");
        FullSaveFile save = SaveManager.Instance.LoadData(bootstrapSaveIndex);
        if (save == null || save.isEmptyFile) throw new Exception("Host snapshot could not be decoded");
        ScriptableObjectHolder.Instance.LoadDataFromSaveFile(save);
        ScriptableObjectHolder.Instance.ThisVariousSaveData.saveFileIndex = bootstrapSaveIndex;
        ScriptableObjectHolder.Instance.hasLoaded = true;
        handshakePending = true; handshakeSent = false;
        clientTransport.SendCommand("/myname :" + playerName.Replace(" ", "_"));
        TrySendHandshake("world bootstrap");
        Log("BOOTSTRAP", "Host save" + bootstrapSaveIndex + " applied (" + bytes.Length + " bytes); entering " + bootstrapScene);
        bootstrapChunks.Clear(); bootstrapExpected = 0;
        Travel(bootstrapScene);
    }

    private static string GetSavePath(int index)
    {
        string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        return Path.Combine(root, "UserData", "Saves", "save" + index + ".dat");
    }

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
            // Local drilling raises this action after SetTile. Replaying it is what
            // updates TileCollisionCache and TilemapSaveDataManager for a remote dig.
            // The guard prevents our own network subscriber from echoing the tile.
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
                // A streaming loader may have repopulated the runtime tilemap while
                // it was inactive, so enforce the authoritative removal once more.
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
        pendingLightingReveals.Clear();
        net = null; clientTransport = null; serverTransport = null;
        isHost = false; sceneReady = false; sessionApproved = false; handshakePending = false; handshakeSent = false;
        applyingSharedCurrency = false; stationCurrencyRequestActive = false; stationCurrencyRequestSucceeded = false; applyingRemoteTile = false;
        positionsSent = 0; positionsReceived = 0; tilesSent = 0; tilesReceived = 0;
        currencyDeltasSent = 0; currencyDeltasReceived = 0; currencySnapshotsSent = 0; currencySnapshotsReceived = 0;
        lightingRevealsApplied = 0; pendingRevealTimer = 0f;
        nextCurrencyRequestSequence = 0; currencyRevision = 0; lastCurrencyRevision = 0; currencySyncTimer = 0f;
        bootstrapChunks.Clear(); bootstrapExpected = 0; bootstrapSaveIndex = -1;
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
