namespace SubnetPlanner.Api.Dhcp;

public sealed class DhcpMessageHandler
{
    private readonly DhcpPool _pool;

    public DhcpMessageHandler(DhcpPool pool) => _pool = pool;

    public byte[]? Process(byte[] datagram)
    {
        if (!DhcpPacket.TryParse(datagram, out var message) || message is null)
            return null;

        return message.MessageType switch
        {
            DhcpConstants.MsgDiscover => HandleDiscover(message),
            DhcpConstants.MsgRequest => HandleRequest(message),
            DhcpConstants.MsgRelease => HandleRelease(message),
            _ => null
        };
    }

    private byte[]? HandleDiscover(DhcpRequestMessage message)
    {
        if (!_pool.TryOffer(message.Mac, out var address, out var offerSeconds))
            return null;
        return DhcpPacket.BuildReply(message, DhcpConstants.MsgOffer, address,
            ServerId, SubnetMask, offerSeconds);
    }

    private byte[]? HandleRequest(DhcpRequestMessage message)
    {
        var result = _pool.HandleRequest(message.Mac, message.ServerId, message.RequestedIp, message.Ciaddr);
        return result.Outcome switch
        {
            RequestOutcome.Ack => DhcpPacket.BuildReply(message, DhcpConstants.MsgAck,
                result.Address, ServerId, SubnetMask, result.LeaseSeconds),
            RequestOutcome.Nak => DhcpPacket.BuildReply(message, DhcpConstants.MsgNak,
                0, ServerId, SubnetMask, 0),
            _ => null
        };
    }

    private byte[]? HandleRelease(DhcpRequestMessage message)
    {
        _pool.HandleRelease(message.Mac, message.Ciaddr);
        return null;
    }

    private uint ServerId => _pool.ServerId;
    private uint SubnetMask => _pool.SubnetMask;
}
