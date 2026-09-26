using System.Net;
using System.Net.Sockets;

// 本机DHCP演示客户端：获取 -> 续租 -> 释放
// 用法: dotnet run --project src/DhcpDemo.Client -- [--port 6767] [--mac 02:00:00:00:00:01]

var port = 6767;
byte[] mac = { 0x02, 0x00, 0x00, 0x00, 0x00, 0x01 };
for (var i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--port") port = int.Parse(args[i + 1]);
    if (args[i] == "--mac") mac = args[i + 1].Split(':').Select(s => Convert.ToByte(s, 16)).ToArray();
}
if (mac.Length != 6) { Console.Error.WriteLine("MAC必须为6字节"); return 1; }

var server = new IPEndPoint(IPAddress.Loopback, port);
using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
udp.Client.ReceiveTimeout = 3000;
Console.WriteLine($"本机端点: {udp.Client.LocalEndPoint}, 服务器: {server}");

uint xid = (uint)Random.Shared.Next();

(byte Type, uint Yiaddr, uint LeaseSeconds)? Send(byte[] packet)
{
    udp.Send(packet, server);
    try
    {
        var remote = new IPEndPoint(IPAddress.Any, 0);
        var reply = udp.Receive(ref remote);
        var type = reply[240 + 2];
        var yiaddr = ReadUInt32(reply, 16);
        uint lease = 0;
        for (var o = 240; o < reply.Length && reply[o] != 255;)
        {
            if (reply[o] == 0) { o++; continue; }
            var code = reply[o]; var len = reply[o + 1];
            if (code == 51 && len == 4) lease = ReadUInt32(reply, o + 2);
            o += 2 + len;
        }
        return (type, yiaddr, lease);
    }
    catch (SocketException)
    {
        Console.WriteLine("  (无响应)");
        return null;
    }
}

byte[] Build(byte msgType, uint ciaddr = 0, uint? requestedIp = null, uint? serverId = null)
{
    var buffer = new byte[236 + 4 + 64];
    buffer[0] = 1; buffer[1] = 1; buffer[2] = 6;
    WriteUInt32(buffer, 4, xid);
    WriteUInt32(buffer, 12, ciaddr);
    Array.Copy(mac, 0, buffer, 28, 6);
    buffer[236] = 99; buffer[237] = 130; buffer[238] = 83; buffer[239] = 99;
    var o = 240;
    buffer[o++] = 53; buffer[o++] = 1; buffer[o++] = msgType;
    if (requestedIp is not null) { buffer[o++] = 50; buffer[o++] = 4; WriteUInt32(buffer, o, requestedIp.Value); o += 4; }
    if (serverId is not null) { buffer[o++] = 54; buffer[o++] = 4; WriteUInt32(buffer, o, serverId.Value); o += 4; }
    buffer[o++] = 255;
    return buffer[..o];
}

static uint ReadUInt32(byte[] d, int o) =>
    ((uint)d[o] << 24) | ((uint)d[o + 1] << 16) | ((uint)d[o + 2] << 8) | d[o + 3];
static void WriteUInt32(byte[] d, int o, uint v)
{ d[o] = (byte)(v >> 24); d[o + 1] = (byte)(v >> 16); d[o + 2] = (byte)(v >> 8); d[o + 3] = (byte)v; }
static string Ip(uint v) => $"{v >> 24}.{(v >> 16) & 255}.{(v >> 8) & 255}.{v & 255}";

// 1. DISCOVER -> OFFER
Console.WriteLine("[1] DISCOVER ...");
var offer = Send(Build(1));
if (offer is null || offer.Value.Type != 2) { Console.WriteLine("未收到OFFER，退出"); return 1; }
var address = offer.Value.Yiaddr;
Console.WriteLine($"    OFFER: 地址 {Ip(address)}");

// 2. REQUEST(选择) -> ACK
Console.WriteLine("[2] REQUEST(选择服务器 127.0.0.1) ...");
var ack = Send(Build(3, requestedIp: address, serverId: 0x7F000001));
if (ack is null || ack.Value.Type != 5) { Console.WriteLine("未收到ACK，退出"); return 1; }
Console.WriteLine($"    ACK: 地址 {Ip(ack.Value.Yiaddr)}, 租期 {ack.Value.LeaseSeconds} 秒");

// 3. REQUEST(续租, ciaddr) -> ACK
Console.WriteLine("[3] REQUEST(续租) ...");
var renew = Send(Build(3, ciaddr: address));
if (renew is null || renew.Value.Type != 5) { Console.WriteLine("续租失败"); return 1; }
Console.WriteLine($"    ACK: 地址 {Ip(renew.Value.Yiaddr)}, 租期 {renew.Value.LeaseSeconds} 秒");

// 4. RELEASE
Console.WriteLine("[4] RELEASE ...");
Send(Build(7, ciaddr: address));
Console.WriteLine("    已发送释放（按协议无响应）");
Console.WriteLine("演示完成");
return 0;
