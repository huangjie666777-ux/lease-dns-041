using SubnetPlanner.Api.Dhcp;
using Xunit;

namespace SubnetPlanner.Tests;

public class DhcpPacketTests
{
    private static byte[] BuildDiscover(uint xid = 0x12345678)
    {
        var buffer = new byte[244];
        buffer[0] = 1; buffer[1] = 1; buffer[2] = 6;
        DhcpPacket.WriteUInt32(buffer, 4, xid);
        buffer[28] = 0x02; buffer[29] = 0x00; buffer[30] = 0x00;
        buffer[31] = 0x00; buffer[32] = 0x00; buffer[33] = 0x2A;
        DhcpConstants.MagicCookie.CopyTo(buffer, 236);
        buffer[240] = 53; buffer[241] = 1; buffer[242] = 1;
        buffer[243] = 255;
        return buffer;
    }

    [Fact]
    public void ParsesValidDiscover()
    {
        Assert.True(DhcpPacket.TryParse(BuildDiscover(), out var message));
        Assert.Equal(0x12345678u, message!.Xid);
        Assert.Equal(DhcpConstants.MsgDiscover, message.MessageType);
        Assert.Equal("02000000002A", Convert.ToHexString(message.Mac));
        Assert.Null(message.RequestedIp);
        Assert.Null(message.ServerId);
    }

    [Fact]
    public void RejectsTruncatedPacket()
    {
        Assert.False(DhcpPacket.TryParse(new byte[100], out _));
    }

    [Fact]
    public void RejectsBadMagicCookie()
    {
        var packet = BuildDiscover();
        packet[236] = 1;
        Assert.False(DhcpPacket.TryParse(packet, out _));
    }

    [Fact]
    public void RejectsNonEthernetHtype()
    {
        var packet = BuildDiscover();
        packet[1] = 6;
        Assert.False(DhcpPacket.TryParse(packet, out _));
    }

    [Fact]
    public void RejectsOptionLengthOverflow()
    {
        var packet = BuildDiscover();
        packet[240] = 51; packet[241] = 200; // 长度超出报文
        Assert.False(DhcpPacket.TryParse(packet, out _));
    }

    [Fact]
    public void RejectsMissingEndOption()
    {
        var packet = BuildDiscover()[..^1]; // 去掉End
        Assert.False(DhcpPacket.TryParse(packet, out _));
    }

    [Fact]
    public void ReplyPreservesXidAndClientMac()
    {
        Assert.True(DhcpPacket.TryParse(BuildDiscover(), out var request));
        var reply = DhcpPacket.BuildReply(request!, DhcpConstants.MsgOffer,
            0x0A000005, 0x7F000001, 0xFFFFFFC0, 300);
        Assert.Equal(DhcpConstants.OpReply, reply[0]);
        Assert.Equal(0x12345678u, DhcpPacket.ReadUInt32(reply, 4));
        Assert.Equal(0x0A000005u, DhcpPacket.ReadUInt32(reply, 16));
        Assert.Equal(0x7F000001u, DhcpPacket.ReadUInt32(reply, 20));
        Assert.Equal(request!.Mac, reply.Skip(28).Take(6).ToArray());
        // 检查选项
        var offset = 240;
        var seen = new Dictionary<byte, byte[]>();
        while (reply[offset] != 255)
        {
            var code = reply[offset];
            var len = reply[offset + 1];
            seen[code] = reply.Skip(offset + 2).Take(len).ToArray();
            offset += 2 + len;
        }
        Assert.Equal(new byte[] { DhcpConstants.MsgOffer }, seen[53]);
        Assert.Equal(new byte[] { 127, 0, 0, 1 }, seen[54]);
        Assert.Equal(new byte[] { 255, 255, 255, 192 }, seen[1]);
        Assert.Equal(new byte[] { 0, 0, 1, 44 }, seen[51]); // 300秒
    }
}
