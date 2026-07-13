using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CosmodrillMultiplayer;

internal sealed class ReplicationChannel
{
    private readonly Action<int, string> onLine;
    private readonly Action<int> onDisconnected;
    private readonly Action<string> log;
    private readonly ConcurrentDictionary<int, TcpClient> peers = new ConcurrentDictionary<int, TcpClient>();
    private TcpListener listener;
    private TcpClient client;
    private volatile bool running;
    private volatile bool clientConnected;
    private int nextPeer;

    internal bool IsConnected => listener != null ? !peers.IsEmpty : clientConnected;
    internal ReplicationChannel(Action<int, string> onLine, Action<int> onDisconnected, Action<string> log) { this.onLine = onLine; this.onDisconnected = onDisconnected; this.log = log; }

    internal void StartServer(int port)
    {
        running = true; listener = new TcpListener(IPAddress.Any, port); listener.Start();
        var thread = new Thread(AcceptLoop) { IsBackground = true, Name = "CosmodrillReplicationAccept" }; thread.Start();
        log("Authoritative state server listening on TCP " + port);
    }

    internal void StartClient(string address, int port)
    {
        running = true;
        var thread = new Thread(() =>
        {
            while (running)
            {
                try { client = new TcpClient(); client.NoDelay = true; client.Connect(address, port); clientConnected = true; log("State client connected to " + address + ":" + port); ReadLoop(client, -1); }
                catch (Exception ex) { if (running) log("State client retry: " + ex.Message); }
                finally { clientConnected = false; try { client?.Close(); } catch { } client = null; }
                if (running) Thread.Sleep(1500);
            }
        }) { IsBackground = true, Name = "CosmodrillReplicationClient" }; thread.Start();
    }

    private void AcceptLoop()
    {
        while (running)
        {
            try { TcpClient accepted = listener.AcceptTcpClient(); accepted.NoDelay = true; int id = Interlocked.Increment(ref nextPeer); peers[id] = accepted; new Thread(() => ReadLoop(accepted, id)) { IsBackground = true, Name = "CosmodrillReplicationPeer" }.Start(); }
            catch (SocketException ex) { if (running) log("State accept error: " + ex.Message); }
            catch (ObjectDisposedException) { return; }
        }
    }

    private void ReadLoop(TcpClient socket, int peerId)
    {
        try
        {
            using (var reader = new StreamReader(socket.GetStream(), Encoding.UTF8, false, 4096, true))
                while (running) { string line = reader.ReadLine(); if (line == null) break; if (line.Length > 2048) { log("State peer sent an oversized frame; disconnected"); break; } onLine(peerId, line); }
        }
        catch (Exception ex) { if (running) log("State receive error: " + ex.Message); }
        finally { TcpClient removed; if (peerId >= 0) { peers.TryRemove(peerId, out removed); onDisconnected(peerId); } try { socket.Close(); } catch { } }
    }

    internal bool Send(string line)
    {
        if (line == null || line.Length > 2048) return false;
        byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
        bool sent = false;
        if (listener != null)
        {
            foreach (var entry in peers) sent |= Write(entry.Value, bytes, entry.Key);
        }
        else if (client != null) sent = Write(client, bytes, -1);
        return sent;
    }

    internal bool SendTo(int peerId, string line)
    {
        if (listener == null || line == null || line.Length > 2048) return false;
        TcpClient peer;
        if (!peers.TryGetValue(peerId, out peer)) return false;
        return Write(peer, Encoding.UTF8.GetBytes(line + "\n"), peerId);
    }

    internal void DisconnectPeer(int peerId)
    {
        TcpClient peer;
        if (!peers.TryRemove(peerId, out peer)) return;
        try { peer.Close(); } catch { }
    }

    private bool Write(TcpClient socket, byte[] bytes, int peerId)
    {
        try { lock (socket) { NetworkStream stream = socket.GetStream(); stream.Write(bytes, 0, bytes.Length); } return true; }
        catch
        {
            TcpClient removed;
            if (peerId >= 0) peers.TryRemove(peerId, out removed);
            else { clientConnected = false; try { socket.Close(); } catch { } }
            return false;
        }
    }

    internal void Stop()
    {
        running = false; clientConnected = false; try { listener?.Stop(); } catch { } try { client?.Close(); } catch { }
        foreach (var peer in peers.Values) try { peer.Close(); } catch { } peers.Clear(); listener = null; client = null;
    }
}
