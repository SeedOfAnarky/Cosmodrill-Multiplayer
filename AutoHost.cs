using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Open.Nat;

namespace CosmodrillMultiplayer;

internal sealed class AutoHostResult
{
    public bool Success; public IPAddress Address; public ushort Port; public string Error; public NatDevice Device;
}

internal static class AutoHost
{
    public static async Task<AutoHostResult> Run(Action<string> log)
    {
        try
        {
            log("Determining public IP through api.ipify.org");
            string value;
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) }) value = (await http.GetStringAsync("https://api.ipify.org")).Trim();
            IPAddress address; if (!IPAddress.TryParse(value, out address)) throw new Exception("Public IP service returned an invalid address.");
            log("Discovering UPnP gateway");
            NatDevice device = await new NatDiscoverer().DiscoverDeviceAsync(PortMapper.Upnp, new CancellationTokenSource(TimeSpan.FromSeconds(10)));
            var rng = new Random();
            for (int attempt = 1; attempt <= 12; attempt++)
            {
                // Do not use Windows' 49152-65535 ephemeral range. UPnP discovery
                // opens temporary UDP sockets there and can steal our selected pair.
                ushort tcp = (ushort)rng.Next(28000, 45000);
                if (!LocalPortsAreFree(tcp)) { log("Local session/state ports near " + tcp + " are busy; retrying"); continue; }
                try
                {
                    await device.CreatePortMapAsync(new Mapping(Protocol.Tcp, tcp, tcp, 3600, "Cosmodrill Multiplayer TCP"));
                    await device.CreatePortMapAsync(new Mapping(Protocol.Udp, (ushort)(tcp + 1), (ushort)(tcp + 1), 3600, "Cosmodrill Multiplayer UDP"));
                    await device.CreatePortMapAsync(new Mapping(Protocol.Tcp, (ushort)(tcp + 2), (ushort)(tcp + 2), 3600, "Cosmodrill Player Replication"));
                    log("Mapped session TCP " + tcp + ", UDP " + (tcp + 1) + ", and state TCP " + (tcp + 2));
                    return new AutoHostResult { Success = true, Address = address, Port = tcp, Device = device };
                }
                catch (Exception ex) { log("Mapping attempt " + attempt + " failed: " + ex.Message); }
            }
            return new AutoHostResult { Error = "UPnP could not map a port. Use Manual Host with a forwarded TCP port and the following UDP port." };
        }
        catch (Exception ex) { return new AutoHostResult { Error = "Automatic hosting failed: " + ex.Message }; }
    }

    private static bool LocalPortsAreFree(ushort tcpPort)
    {
        TcpListener tcp = null;
        TcpListener state = null;
        UdpClient udp = null;
        try
        {
            tcp = new TcpListener(IPAddress.Any, tcpPort);
            tcp.Start();
            udp = new UdpClient(new IPEndPoint(IPAddress.Any, tcpPort + 1));
            state = new TcpListener(IPAddress.Any, tcpPort + 2); state.Start();
            return true;
        }
        catch (SocketException) { return false; }
        finally
        {
            if (udp != null) udp.Close();
            if (state != null) state.Stop();
            if (tcp != null) tcp.Stop();
        }
    }
}
