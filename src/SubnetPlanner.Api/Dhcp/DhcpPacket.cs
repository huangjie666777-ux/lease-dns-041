namespace SubnetPlanner.Api.Dhcp;

public static class DhcpConstants
{
    public const int FixedHeaderLength = 236;
    public static readonly byte[] MagicCookie = { 99, 130, 83, 99 };

    public const byte OpRequest = 1;
    public const byte OpReply = 2;
    public const byte HtypeEthernet = 1;
    public const byte HlenEthernet = 6;

    public const byte MsgDiscover = 1;
    public const byte MsgOffer = 2;
    public const byte MsgRequest = 3;
    public const byte MsgAck = 5;
    public const byte MsgNak = 6;
    public const byte MsgRelease = 7;

    public const byte OptPad = 0;
    public const byte OptSubnetMask = 1;
    public const byte OptRequestedIp = 50;
    public const byte OptLeaseTime = 51;
    public const byte OptMessageType = 53;
    public const byte OptServerId = 54;
    public const byte OptEnd = 255;
}

public sealed record DhcpRequestMessage(
    uint Xid,
    byte[] Mac,
    uint Ciaddr,
    byte MessageType,
    uint? RequestedIp,
    uint? ServerId);

public static class DhcpPacket
{
    public static bool TryParse(ReadOnlySpan<byte> data, out DhcpRequestMessage? message)
    {
        message = null;
        if (data.Length < DhcpConstants.FixedHeaderLength + 4)
            return false;
        if (data[0] != DhcpConstants.OpRequest)
            return false;
        if (data[1] != DhcpConstants.HtypeEthernet || data[2] != DhcpConstants.HlenEthernet)
            return false;
        if (!data.Slice(DhcpConstants.FixedHeaderLength, 4).SequenceEqual(DhcpConstants.MagicCookie))
            return false;

        var xid = ReadUInt32(data, 4);
        var ciaddr = ReadUInt32(data, 12);
        var mac = data.Slice(28, 6).ToArray();

        byte? messageType = null;
        uint? requestedIp = null;
        uint? serverId = null;

        var offset = DhcpConstants.FixedHeaderLength + 4;
        var ended = false;
        while (offset < data.Length)
        {
            var code = data[offset++];
            if (code == DhcpConstants.OptPad) continue;
            if (code == DhcpConstants.OptEnd) { ended = true; break; }
            if (offset >= data.Length) return false;
            var length = data[offset++];
            if (offset + length > data.Length) return false;
            var value = data.Slice(offset, length);
            switch (code)
            {
                case DhcpConstants.OptMessageType:
                    if (length != 1) return false;
                    messageType = value[0];
                    break;
                case DhcpConstants.OptRequestedIp:
                    if (length != 4) return false;
                    requestedIp = ReadUInt32(value, 0);
                    break;
                case DhcpConstants.OptServerId:
                    if (length != 4) return false;
                    serverId = ReadUInt32(value, 0);
                    break;
            }
            offset += length;
        }
        if (!ended || messageType is null)
            return false;

        message = new DhcpRequestMessage(xid, mac, ciaddr, messageType.Value, requestedIp, serverId);
        return true;
    }

    public static byte[] BuildReply(DhcpRequestMessage request, byte replyType,
        uint yiaddr, uint serverId, uint subnetMask, uint leaseSeconds)
    {
        var buffer = new byte[DhcpConstants.FixedHeaderLength + 4 + 64];
        buffer[0] = DhcpConstants.OpReply;
        buffer[1] = DhcpConstants.HtypeEthernet;
        buffer[2] = DhcpConstants.HlenEthernet;
        WriteUInt32(buffer, 4, request.Xid);
        WriteUInt32(buffer, 16, yiaddr);
        WriteUInt32(buffer, 20, serverId);
        Array.Copy(request.Mac, 0, buffer, 28, 6);
        DhcpConstants.MagicCookie.CopyTo(buffer, DhcpConstants.FixedHeaderLength);

        var offset = DhcpConstants.FixedHeaderLength + 4;
        offset += WriteOption(buffer, offset, DhcpConstants.OptMessageType, new[] { replyType });
        offset += WriteOption(buffer, offset, DhcpConstants.OptServerId, ToBytes(serverId));
        offset += WriteOption(buffer, offset, DhcpConstants.OptSubnetMask, ToBytes(subnetMask));
        offset += WriteOption(buffer, offset, DhcpConstants.OptLeaseTime, ToBytes(leaseSeconds));
        buffer[offset++] = DhcpConstants.OptEnd;
        return buffer[..offset];
    }

    private static int WriteOption(byte[] buffer, int offset, byte code, byte[] value)
    {
        buffer[offset] = code;
        buffer[offset + 1] = (byte)value.Length;
        value.CopyTo(buffer, offset + 2);
        return value.Length + 2;
    }

    private static byte[] ToBytes(uint value) =>
        new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value };

    public static uint ReadUInt32(ReadOnlySpan<byte> data, int offset) =>
        ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) |
        ((uint)data[offset + 2] << 8) | data[offset + 3];

    public static void WriteUInt32(Span<byte> data, int offset, uint value)
    {
        data[offset] = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)value;
    }
}
