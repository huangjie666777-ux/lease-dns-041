using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SubnetPlanner.Api;
using SubnetPlanner.Api.Dhcp;
using Xunit;

namespace SubnetPlanner.Tests;

public class DhcpIntegrationTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public DhcpIntegrationTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?> { ["Dhcp:Port"] = "0" })));
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private int ServerPort()
    {
        var server = _factory.Services.GetServices<IHostedService>().OfType<DhcpServer>().Single();
        for (var i = 0; i < 100 && server.BoundPort == 0; i++) Thread.Sleep(50);
        Assert.NotEqual(0, server.BoundPort);
        return server.BoundPort;
    }

    private async Task<JsonElement> Activate(object? overrides = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var body = new Dictionary<string, object?>
        {
            ["parentCidr"] = "10.9.0.0/24",
            ["departments"] = new[] { new { id = "eng", hosts = 50 } },
            ["departmentId"] = "eng",
            ["offerSeconds"] = 30,
            ["leaseSeconds"] = 300
        };
        if (overrides is not null)
            foreach (var kv in overrides.GetType().GetProperties())
                body[kv.Name] = kv.GetValue(overrides);
        using var response = await _client.PostAsJsonAsync("/dhcp/activate", body);
        Assert.Equal(expected, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task ActivateEnablesOnlyDepartmentRange()
    {
        var json = await Activate();
        Assert.True(json.GetProperty("activated").GetBoolean());
        Assert.Equal("eng", json.GetProperty("departmentId").GetString());
        Assert.Equal("10.9.0.0/26", json.GetProperty("cidr").GetString());
        Assert.Equal("10.9.0.1", json.GetProperty("firstUsable").GetString());
        Assert.Equal("10.9.0.62", json.GetProperty("lastUsable").GetString());
    }

    [Fact]
    public async Task InvalidActivateDoesNotDisturbExistingPool()
    {
        await Activate();
        // 非法请求：租约秒数为0
        await Activate(new { leaseSeconds = 0 }, HttpStatusCode.BadRequest);
        var status = await _client.GetFromJsonAsync<JsonElement>("/dhcp/status");
        Assert.True(status.GetProperty("active").GetBoolean());
        Assert.Equal("10.9.0.0/26", status.GetProperty("cidr").GetString());
    }

    [Fact]
    public async Task ActivateConflictWhileLeaseActive()
    {
        await Activate();
        var address = await FullHandshake();
        // 租约未过期，拒绝替换
        var conflict = await Activate(new { parentCidr = "10.10.0.0/24" }, HttpStatusCode.Conflict);
        Assert.Contains("拒绝替换", conflict.GetProperty("error").GetString());
        var status = await _client.GetFromJsonAsync<JsonElement>("/dhcp/status");
        Assert.Equal("10.9.0.0/26", status.GetProperty("cidr").GetString());
        Assert.Equal(address, status.GetProperty("leases")[0].GetProperty("address").GetString());
    }

    [Fact]
    public async Task UdpDiscoversRequestsRenewsAndReleases()
    {
        await Activate();
        var port = ServerPort();
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        udp.Client.ReceiveTimeout = 3000;
        var server = new IPEndPoint(IPAddress.Loopback, port);
        var mac = new byte[] { 2, 0, 0, 0, 0, 9 };
        const uint xid = 0xAABBCCDD;

        byte[] Send(byte msgType, uint ciaddr = 0, uint? reqIp = null, uint? serverId = null)
        {
            var packet = Build(msgType, mac, xid, ciaddr, reqIp, serverId);
            udp.Send(packet, server);
            var remote = new IPEndPoint(IPAddress.Any, 0);
            return udp.Receive(ref remote);
        }

        // DISCOVER -> OFFER
        var offer = Send(1);
        Assert.Equal(2, ReplyType(offer));
        var address = DhcpPacket.ReadUInt32(offer, 16);
        Assert.Equal("10.9.0.1", IpMath.Format(address));
        Assert.Equal(xid, DhcpPacket.ReadUInt32(offer, 4));
        Assert.Equal(mac, offer.Skip(28).Take(6).ToArray());

        // REQUEST 选择其他服务器 -> 不响应
        udp.Send(Build(3, mac, xid, 0, address, 0x0A000001), server);
        Assert.Throws<SocketException>(() =>
        {
            var remote = new IPEndPoint(IPAddress.Any, 0);
            udp.Receive(ref remote);
        });

        // REQUEST 选择本服务器 -> ACK
        var ack = Send(3, reqIp: address, serverId: 0x7F000001);
        Assert.Equal(5, ReplyType(ack));
        Assert.Equal(address, DhcpPacket.ReadUInt32(ack, 16));

        // REQUEST 续租 -> ACK
        var renew = Send(3, ciaddr: address);
        Assert.Equal(5, ReplyType(renew));

        // RELEASE -> 无响应，状态清空
        udp.Send(Build(7, mac, xid, address), server);
        await Task.Delay(200);
        var status = await _client.GetFromJsonAsync<JsonElement>("/dhcp/status");
        Assert.Empty(status.GetProperty("leases").EnumerateArray());
    }

    [Fact]
    public async Task MalformedDatagramsDoNotCrashOrChangeState()
    {
        await Activate();
        var port = ServerPort();
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        udp.Client.ReceiveTimeout = 500;
        var server = new IPEndPoint(IPAddress.Loopback, port);
        udp.Send(new byte[] { 1, 2, 3 }, server);
        udp.Send(new byte[600], server);
        var garbage = new byte[300];
        Random.Shared.NextBytes(garbage);
        udp.Send(garbage, server);
        await Task.Delay(200);
        var status = await _client.GetFromJsonAsync<JsonElement>("/dhcp/status");
        Assert.True(status.GetProperty("active").GetBoolean());
        Assert.Empty(status.GetProperty("offers").EnumerateArray());
        // 服务仍然可用
        var health = await _client.GetStringAsync("/healthz");
        Assert.Contains("ok", health);
    }

    private async Task<string> FullHandshake()
    {
        var port = ServerPort();
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        udp.Client.ReceiveTimeout = 3000;
        var server = new IPEndPoint(IPAddress.Loopback, port);
        var mac = new byte[] { 2, 0, 0, 0, 0, 7 };
        udp.Send(Build(1, mac, 1), server);
        var remote = new IPEndPoint(IPAddress.Any, 0);
        var offer = udp.Receive(ref remote);
        var address = DhcpPacket.ReadUInt32(offer, 16);
        udp.Send(Build(3, mac, 1, 0, address, 0x7F000001), server);
        udp.Receive(ref remote);
        return IpMath.Format(address);
    }

    private static byte ReplyType(byte[] reply)
    {
        for (var o = 240; o < reply.Length && reply[o] != 255;)
        {
            if (reply[o] == 0) { o++; continue; }
            if (reply[o] == 53) return reply[o + 2];
            o += 2 + reply[o + 1];
        }
        return 0;
    }

    private static byte[] Build(byte msgType, byte[] mac, uint xid,
        uint ciaddr = 0, uint? requestedIp = null, uint? serverId = null)
    {
        var buffer = new byte[256];
        buffer[0] = 1; buffer[1] = 1; buffer[2] = 6;
        DhcpPacket.WriteUInt32(buffer, 4, xid);
        DhcpPacket.WriteUInt32(buffer, 12, ciaddr);
        Array.Copy(mac, 0, buffer, 28, 6);
        DhcpConstants.MagicCookie.CopyTo(buffer, 236);
        var o = 240;
        buffer[o++] = 53; buffer[o++] = 1; buffer[o++] = msgType;
        if (requestedIp is not null) { buffer[o++] = 50; buffer[o++] = 4; DhcpPacket.WriteUInt32(buffer, o, requestedIp.Value); o += 4; }
        if (serverId is not null) { buffer[o++] = 54; buffer[o++] = 4; DhcpPacket.WriteUInt32(buffer, o, serverId.Value); o += 4; }
        buffer[o++] = 255;
        return buffer[..o];
    }
}
