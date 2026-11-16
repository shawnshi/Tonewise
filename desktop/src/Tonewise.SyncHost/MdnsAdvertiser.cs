using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Tonewise.SyncHost;

/// <summary>
/// Lightweight zero-dependency mDNS responder/announcer for `_tonewise._tcp.local.`
/// enabling Android NsdManager auto-discovery on the local subnet.
/// </summary>
public sealed class MdnsAdvertiser : IAsyncDisposable
{
    private const string ServiceType = "_tonewise._tcp.local";
    private static readonly IPAddress MdnsMulticast = IPAddress.Parse("224.0.0.251");
    private const int MdnsPort = 5353;

    private readonly string _instanceName;
    private readonly int _servicePort;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loopTask;

    public MdnsAdvertiser(string instanceName, int servicePort)
    {
        _instanceName = instanceName;
        _servicePort = servicePort;
    }

    public void Start()
    {
        _loopTask = Task.Run(() => RunLoopAsync(_cts.Token));
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, MdnsPort));
            udp.JoinMulticastGroup(MdnsMulticast);

            byte[] announcement = BuildAnnouncementPacket();
            var target = new IPEndPoint(MdnsMulticast, MdnsPort);

            // Initial announcement burst + periodic keepalive
            await udp.SendAsync(announcement, announcement.Length, target);
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(15), ct);
                await udp.SendAsync(announcement, announcement.Length, target);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch
        {
            // Multicast unavailable in restricted/sandboxed environments; HTTP endpoint remains active
        }
    }

    private byte[] BuildAnnouncementPacket()
    {
        string hostLabel = Dns.GetHostName().Replace('.', '-');
        string fqdn = $"{hostLabel}.local";
        string fullService = $"{_instanceName}.{ServiceType}";
        IPAddress localIp = ResolveLanIpv4();

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        // DNS Header: ID=0, Flags=0x8400 (Response + Authoritative), QD=0, AN=4, NS=0, AR=0
        WriteU16Be(bw, 0);
        WriteU16Be(bw, 0x8400);
        WriteU16Be(bw, 0);
        WriteU16Be(bw, 4);
        WriteU16Be(bw, 0);
        WriteU16Be(bw, 0);

        // 1. PTR _tonewise._tcp.local -> instance._tonewise._tcp.local
        WriteDnsName(bw, ServiceType);
        WriteU16Be(bw, 12); // PTR
        WriteU16Be(bw, 1);  // IN
        WriteU32Be(bw, 120);
        byte[] ptrRdata = EncodeDnsNameBytes(fullService);
        WriteU16Be(bw, (ushort)ptrRdata.Length);
        bw.Write(ptrRdata);

        // 2. SRV instance._tonewise._tcp.local -> port + host.local
        WriteDnsName(bw, fullService);
        WriteU16Be(bw, 33); // SRV
        WriteU16Be(bw, 0x8001);
        WriteU32Be(bw, 120);
        byte[] targetBytes = EncodeDnsNameBytes(fqdn);
        WriteU16Be(bw, (ushort)(6 + targetBytes.Length));
        WriteU16Be(bw, 0); // priority
        WriteU16Be(bw, 0); // weight
        WriteU16Be(bw, (ushort)_servicePort);
        bw.Write(targetBytes);

        // 3. TXT instance._tonewise._tcp.local -> proto=1
        WriteDnsName(bw, fullService);
        WriteU16Be(bw, 16); // TXT
        WriteU16Be(bw, 0x8001);
        WriteU32Be(bw, 120);
        byte[] txt = Encoding.UTF8.GetBytes("proto=1");
        WriteU16Be(bw, (ushort)(1 + txt.Length));
        bw.Write((byte)txt.Length);
        bw.Write(txt);

        // 4. A host.local -> IPv4
        WriteDnsName(bw, fqdn);
        WriteU16Be(bw, 1); // A
        WriteU16Be(bw, 0x8001);
        WriteU32Be(bw, 120);
        WriteU16Be(bw, 4);
        bw.Write(localIp.GetAddressBytes());

        return ms.ToArray();
    }

    private static IPAddress ResolveLanIpv4()
    {
        try
        {
            foreach (var ip in Dns.GetHostAddresses(Dns.GetHostName()))
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                    return ip;
            }
        }
        catch
        {
            // ignore
        }
        return IPAddress.Loopback;
    }

    private static byte[] EncodeDnsNameBytes(string name)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        WriteDnsName(bw, name);
        return ms.ToArray();
    }

    private static void WriteDnsName(BinaryWriter bw, string name)
    {
        foreach (string label in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            byte[] bytes = Encoding.UTF8.GetBytes(label);
            bw.Write((byte)Math.Min(bytes.Length, 63));
            bw.Write(bytes, 0, Math.Min(bytes.Length, 63));
        }
        bw.Write((byte)0);
    }

    private static void WriteU16Be(BinaryWriter bw, ushort val)
    {
        Span<byte> buf = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buf, val);
        bw.Write(buf);
    }

    private static void WriteU32Be(BinaryWriter bw, uint val)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buf, val);
        bw.Write(buf);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        if (_loopTask is not null)
        {
            try { await _loopTask; } catch { /* ignore */ }
        }
        _cts.Dispose();
    }
}
