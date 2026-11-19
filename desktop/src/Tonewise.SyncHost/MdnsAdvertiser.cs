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
        UdpClient? udp = null;
        Task<UdpReceiveResult>? recvTask = null;
        var target = new IPEndPoint(MdnsMulticast, MdnsPort);
        try
        {
            udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, MdnsPort));
            udp.JoinMulticastGroup(MdnsMulticast);

            byte[] initial = BuildAnnouncementPacket();
            await udp.SendAsync(initial.AsMemory(), target, ct);

            DateTime nextAnnounceUtc = DateTime.UtcNow.AddSeconds(15);
            DateTime lastQueryReplyUtc = DateTime.MinValue;
            string hostFqdn = $"{GetHostLabel()}.local";

            while (!ct.IsCancellationRequested)
            {
                recvTask ??= udp.ReceiveAsync(ct).AsTask();
                TimeSpan remaining = nextAnnounceUtc - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    byte[] keepalive = BuildAnnouncementPacket();
                    await udp.SendAsync(keepalive.AsMemory(), target, ct);
                    nextAnnounceUtc = DateTime.UtcNow.AddSeconds(15);
                    continue;
                }

                var delayTask = Task.Delay(remaining, ct);
                var completed = await Task.WhenAny(recvTask, delayTask);
                if (completed == recvTask)
                {
                    UdpReceiveResult pkt = await recvTask;
                    recvTask = null;
                    DateTime now = DateTime.UtcNow;
                    if ((now - lastQueryReplyUtc).TotalMilliseconds >= 500
                        && IsMatchingMdnsQuery(pkt.Buffer, _instanceName, hostFqdn))
                    {
                        byte[] reply = BuildAnnouncementPacket();
                        await udp.SendAsync(reply.AsMemory(), target, ct);
                        lastQueryReplyUtc = now;
                        nextAnnounceUtc = now.AddSeconds(15);
                    }
                }
                else
                {
                    await delayTask;
                    byte[] keepalive = BuildAnnouncementPacket();
                    await udp.SendAsync(keepalive.AsMemory(), target, ct);
                    nextAnnounceUtc = DateTime.UtcNow.AddSeconds(15);
                }
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
        finally
        {
            if (udp is not null)
            {
                try
                {
                    byte[] goodbye = BuildAnnouncementPacket(ttlSeconds: 0);
                    await udp.SendAsync(goodbye, goodbye.Length, target);
                }
                catch
                {
                    // Ignore best-effort shutdown goodbye failure
                }
                udp.Dispose();
                if (recvTask is not null)
                {
                    try { await recvTask; } catch { /* observe disposal/cancellation */ }
                }
            }
        }
    }

    public byte[] BuildAnnouncementPacket(uint ttlSeconds = 120, IPAddress? overrideIp = null)
    {
        string fqdn = $"{GetHostLabel()}.local";
        string fullService = $"{_instanceName}.{ServiceType}";
        IPAddress localIp = overrideIp ?? ResolveLanIpv4();

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
        WriteU32Be(bw, ttlSeconds);
        byte[] ptrRdata = EncodeDnsNameBytes(fullService);
        WriteU16Be(bw, (ushort)ptrRdata.Length);
        bw.Write(ptrRdata);

        // 2. SRV instance._tonewise._tcp.local -> port + host.local
        WriteDnsName(bw, fullService);
        WriteU16Be(bw, 33); // SRV
        WriteU16Be(bw, 0x8001);
        WriteU32Be(bw, ttlSeconds);
        byte[] targetBytes = EncodeDnsNameBytes(fqdn);
        WriteU16Be(bw, (ushort)(6 + targetBytes.Length));
        WriteU16Be(bw, 0); // priority
        WriteU16Be(bw, 0); // weight
        WriteU16Be(bw, (ushort)_servicePort);
        bw.Write(targetBytes);

        // 3. TXT instance._tonewise._tcp.local -> proto=2
        WriteDnsName(bw, fullService);
        WriteU16Be(bw, 16); // TXT
        WriteU16Be(bw, 0x8001);
        WriteU32Be(bw, ttlSeconds);
        byte[] txt = Encoding.UTF8.GetBytes("proto=2");
        WriteU16Be(bw, (ushort)(1 + txt.Length));
        bw.Write((byte)txt.Length);
        bw.Write(txt);

        // 4. A host.local -> IPv4
        WriteDnsName(bw, fqdn);
        WriteU16Be(bw, 1); // A
        WriteU16Be(bw, 0x8001);
        WriteU32Be(bw, ttlSeconds);
        WriteU16Be(bw, 4);
        bw.Write(localIp.GetAddressBytes());

        return ms.ToArray();
    }

    public static bool IsMatchingMdnsQuery(ReadOnlySpan<byte> packet, string instanceName, string hostFqdn)
    {
        if (packet.Length < 12) return false;
        ushort flags = BinaryPrimitives.ReadUInt16BigEndian(packet[2..4]);
        if ((flags & 0x8000) != 0) return false; // Must be DNS query (QR=0)
        ushort qdCount = BinaryPrimitives.ReadUInt16BigEndian(packet[4..6]);
        if (qdCount == 0 || qdCount > 64) return false;

        string cleanHost = hostFqdn.TrimEnd('.');
        string fullService = $"{instanceName}.{ServiceType}";
        int offset = 12;

        for (int q = 0; q < qdCount && offset < packet.Length; q++)
        {
            if (!TryDecodeDnsName(packet, offset, out string qname, out int afterName))
                return false;
            if (afterName + 4 > packet.Length)
                return false;

            offset = afterName + 4; // skip QTYPE & QCLASS
            if (string.Equals(qname, ServiceType, StringComparison.OrdinalIgnoreCase)
                || string.Equals(qname, "_services._dns-sd._udp.local", StringComparison.OrdinalIgnoreCase)
                || string.Equals(qname, fullService, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrEmpty(cleanHost) && string.Equals(qname, cleanHost, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }
        return false;
    }

    private static bool TryDecodeDnsName(ReadOnlySpan<byte> packet, int startOffset, out string name, out int nextOffsetAfterName)
    {
        name = "";
        nextOffsetAfterName = startOffset;
        var labels = new List<string>();
        int offset = startOffset;
        int hops = 0;
        bool jumped = false;

        while (offset < packet.Length)
        {
            byte len = packet[offset];
            if (len == 0)
            {
                if (!jumped) nextOffsetAfterName = offset + 1;
                name = string.Join('.', labels);
                return true;
            }
            if ((len & 0xC0) == 0xC0)
            {
                if (offset + 1 >= packet.Length) return false;
                if (!jumped) nextOffsetAfterName = offset + 2;
                jumped = true;
                int ptr = ((len & 0x3F) << 8) | packet[offset + 1];
                if (ptr >= packet.Length || ++hops > 16) return false;
                offset = ptr;
                continue;
            }
            if ((len & 0xC0) != 0) return false; // Invalid reserved label bits
            offset++;
            if (offset + len > packet.Length) return false;
            labels.Add(Encoding.UTF8.GetString(packet.Slice(offset, len)));
            offset += len;
        }
        return false;
    }

    private static string GetHostLabel() =>
        Dns.GetHostName().Replace('.', '-');

    private static IPAddress ResolveLanIpv4()
    {
        var preferred = SyncOutboxAndPairingStore.ResolvePreferredLanIpv4();
        if (preferred is not null) return preferred;
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
