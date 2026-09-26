namespace SubnetPlanner.Api.Dhcp;

public sealed record PoolConfig(
    string DepartmentId,
    uint Network,
    int Prefix,
    uint FirstUsable,
    uint LastUsable,
    int OfferSeconds,
    int LeaseSeconds,
    uint ServerId);

public sealed record OfferInfo(string Mac, uint Address, DateTimeOffset ExpiresAt);
public sealed record LeaseInfo(string Mac, uint Address, DateTimeOffset ExpiresAt);

public sealed record PoolStatus(
    bool Active,
    string? DepartmentId,
    string? Cidr,
    string? FirstUsable,
    string? LastUsable,
    int? OfferSeconds,
    int? LeaseSeconds,
    IReadOnlyList<OfferInfo> Offers,
    IReadOnlyList<LeaseInfo> Leases);

public enum RequestOutcome { Ack, Nak, Ignore }

public sealed record RequestResult(RequestOutcome Outcome, uint Address, uint LeaseSeconds);

public sealed class DhcpPool
{
    private readonly object _gate = new();
    private readonly Func<DateTimeOffset> _clock;
    private PoolConfig? _config;
    private readonly Dictionary<string, OfferInfo> _offers = new();
    private readonly Dictionary<string, LeaseInfo> _leases = new();
    private readonly HashSet<uint> _usedAddresses = new();

    public DhcpPool() : this(() => DateTimeOffset.UtcNow) { }
    public DhcpPool(Func<DateTimeOffset> clock) => _clock = clock;

    public static string MacKey(byte[] mac) => Convert.ToHexString(mac);

    public uint ServerId
    {
        get { lock (_gate) return _config?.ServerId ?? 0; }
    }

    public uint SubnetMask
    {
        get { lock (_gate) return _config is null ? 0 : IpMath.PrefixMask(_config.Prefix); }
    }

    public bool Activate(PoolConfig config)
    {
        lock (_gate)
        {
            SweepExpired();
            if (_config is not null && (_offers.Count > 0 || _leases.Count > 0))
                return false;
            _config = config;
            _offers.Clear();
            _leases.Clear();
            _usedAddresses.Clear();
            return true;
        }
    }

    public PoolStatus GetStatus()
    {
        lock (_gate)
        {
            SweepExpired();
            if (_config is null)
                return new PoolStatus(false, null, null, null, null, null, null,
                    Array.Empty<OfferInfo>(), Array.Empty<LeaseInfo>());
            return new PoolStatus(
                true,
                _config.DepartmentId,
                $"{IpMath.Format(_config.Network)}/{_config.Prefix}",
                IpMath.Format(_config.FirstUsable),
                IpMath.Format(_config.LastUsable),
                _config.OfferSeconds,
                _config.LeaseSeconds,
                _offers.Values.OrderBy(o => o.Address).ToList(),
                _leases.Values.OrderBy(l => l.Address).ToList());
        }
    }

    public bool TryOffer(byte[] mac, out uint address, out uint offerSeconds)
    {
        address = 0;
        offerSeconds = 0;
        lock (_gate)
        {
            if (_config is null) return false;
            SweepExpired();
            var key = MacKey(mac);

            if (_leases.TryGetValue(key, out var lease))
            {
                address = lease.Address;
                offerSeconds = (uint)_config.OfferSeconds;
                return true;
            }
            if (_offers.TryGetValue(key, out var offer))
            {
                address = offer.Address;
                offerSeconds = (uint)_config.OfferSeconds;
                return true;
            }
            var candidate = FindSmallestFree();
            if (candidate is null) return false;
            address = candidate.Value;
            _offers[key] = new OfferInfo(key, address, _clock().AddSeconds(_config.OfferSeconds));
            _usedAddresses.Add(address);
            offerSeconds = (uint)_config.OfferSeconds;
            return true;
        }
    }

    public RequestResult HandleRequest(byte[] mac, uint? serverId, uint? requestedIp, uint ciaddr)
    {
        lock (_gate)
        {
            if (_config is null) return new RequestResult(RequestOutcome.Ignore, 0, 0);
            SweepExpired();
            var key = MacKey(mac);

            if (serverId is not null)
            {
                // 选择阶段：确认客户端选择的是本服务器
                if (serverId.Value != _config.ServerId)
                    return new RequestResult(RequestOutcome.Ignore, 0, 0);
                if (requestedIp is null)
                    return new RequestResult(RequestOutcome.Nak, 0, 0);
                // 重复确认：已持有该地址租约则直接返回原租约
                if (_leases.TryGetValue(key, out var existing) && existing.Address == requestedIp.Value)
                    return Ack(existing.Address);
                if (_offers.TryGetValue(key, out var offer) && offer.Address == requestedIp.Value)
                {
                    _offers.Remove(key);
                    CommitLease(key, requestedIp.Value);
                    return Ack(requestedIp.Value);
                }
                return new RequestResult(RequestOutcome.Nak, 0, 0);
            }

            // 续租阶段：通过ciaddr核对归属
            if (ciaddr == 0)
                return new RequestResult(RequestOutcome.Nak, 0, 0);
            if (_leases.TryGetValue(key, out var current) && current.Address == ciaddr)
            {
                CommitLease(key, ciaddr);
                return Ack(ciaddr);
            }
            return new RequestResult(RequestOutcome.Nak, 0, 0);
        }
    }

    public bool HandleRelease(byte[] mac, uint ciaddr)
    {
        lock (_gate)
        {
            if (_config is null) return false;
            SweepExpired();
            var key = MacKey(mac);
            if (_leases.TryGetValue(key, out var lease) && lease.Address == ciaddr)
            {
                _leases.Remove(key);
                _usedAddresses.Remove(ciaddr);
                return true;
            }
            return false;
        }
    }

    private RequestResult Ack(uint address) =>
        new(RequestOutcome.Ack, address, (uint)_config!.LeaseSeconds);

    private void CommitLease(string key, uint address)
    {
        _leases[key] = new LeaseInfo(key, address, _clock().AddSeconds(_config!.LeaseSeconds));
        _usedAddresses.Add(address);
    }

    private uint? FindSmallestFree()
    {
        var config = _config!;
        for (var candidate = config.FirstUsable; candidate <= config.LastUsable; candidate++)
        {
            if (!_usedAddresses.Contains(candidate))
                return candidate;
            if (candidate == uint.MaxValue) break;
        }
        return null;
    }

    private void SweepExpired()
    {
        var now = _clock();
        foreach (var (key, offer) in _offers.ToList())
        {
            if (offer.ExpiresAt <= now)
            {
                _offers.Remove(key);
                if (!_leases.ContainsKey(key))
                    _usedAddresses.Remove(offer.Address);
            }
        }
        foreach (var (key, lease) in _leases.ToList())
        {
            if (lease.ExpiresAt <= now)
            {
                _leases.Remove(key);
                if (!_offers.TryGetValue(key, out var offer) || offer.Address != lease.Address)
                    _usedAddresses.Remove(lease.Address);
            }
        }
    }
}
