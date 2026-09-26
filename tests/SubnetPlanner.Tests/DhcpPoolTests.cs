using SubnetPlanner.Api.Dhcp;
using Xunit;

namespace SubnetPlanner.Tests;

public class DhcpPoolTests
{
    private static readonly byte[] MacA = { 2, 0, 0, 0, 0, 1 };
    private static readonly byte[] MacB = { 2, 0, 0, 0, 0, 2 };
    private const uint Server = 0x7F000001;

    private static PoolConfig Config(int offerSeconds = 30, int leaseSeconds = 300) =>
        new("eng", 0x0A000040, 26, 0x0A000041, 0x0A00007E, offerSeconds, leaseSeconds, Server);

    [Fact]
    public void DiscoverOffersSmallestFreeAddress()
    {
        var pool = new DhcpPool();
        Assert.True(pool.Activate(Config()));
        Assert.True(pool.TryOffer(MacA, out var a, out _));
        Assert.Equal(0x0A000041u, a);
        Assert.True(pool.TryOffer(MacB, out var b, out _));
        Assert.Equal(0x0A000042u, b);
    }

    [Fact]
    public void DiscoverReusesExistingOfferForSameMac()
    {
        var pool = new DhcpPool();
        pool.Activate(Config());
        pool.TryOffer(MacA, out var first, out _);
        Assert.True(pool.TryOffer(MacA, out var second, out _));
        Assert.Equal(first, second);
    }

    [Fact]
    public void RequestWithMatchingOfferCommitsLease()
    {
        var pool = new DhcpPool();
        pool.Activate(Config());
        pool.TryOffer(MacA, out var address, out _);
        var result = pool.HandleRequest(MacA, Server, address, 0);
        Assert.Equal(RequestOutcome.Ack, result.Outcome);
        Assert.Equal(address, result.Address);
        Assert.Equal(300u, result.LeaseSeconds);
    }

    [Fact]
    public void RequestWithMismatchedOfferGetsNak()
    {
        var pool = new DhcpPool();
        pool.Activate(Config());
        pool.TryOffer(MacA, out _, out _);
        var result = pool.HandleRequest(MacA, Server, 0x0A000050, 0);
        Assert.Equal(RequestOutcome.Nak, result.Outcome);
    }

    [Fact]
    public void RequestForOtherServerIsIgnored()
    {
        var pool = new DhcpPool();
        pool.Activate(Config());
        pool.TryOffer(MacA, out var address, out _);
        var result = pool.HandleRequest(MacA, 0x0A000001, address, 0);
        Assert.Equal(RequestOutcome.Ignore, result.Outcome);
    }

    [Fact]
    public void RenewViaCiaddrExtendsLease()
    {
        var now = DateTimeOffset.UtcNow;
        var pool = new DhcpPool(() => now);
        pool.Activate(Config(leaseSeconds: 100));
        pool.TryOffer(MacA, out var address, out _);
        pool.HandleRequest(MacA, Server, address, 0);
        now = now.AddSeconds(40);
        var renewed = pool.HandleRequest(MacA, null, null, address);
        Assert.Equal(RequestOutcome.Ack, renewed.Outcome);
        var lease = pool.GetStatus().Leases.Single();
        Assert.Equal(now.AddSeconds(100), lease.ExpiresAt);
    }

    [Fact]
    public void RenewWithForeignCiaddrGetsNak()
    {
        var pool = new DhcpPool();
        pool.Activate(Config());
        pool.TryOffer(MacA, out var address, out _);
        pool.HandleRequest(MacA, Server, address, 0);
        var result = pool.HandleRequest(MacB, null, null, address);
        Assert.Equal(RequestOutcome.Nak, result.Outcome);
    }

    [Fact]
    public void ReleaseReturnsOnlyOwnAddress()
    {
        var pool = new DhcpPool();
        pool.Activate(Config());
        pool.TryOffer(MacA, out var address, out _);
        pool.HandleRequest(MacA, Server, address, 0);
        Assert.False(pool.HandleRelease(MacB, address)); // 他人不能释放
        Assert.True(pool.HandleRelease(MacA, address));
        Assert.Empty(pool.GetStatus().Leases);
        // 释放后地址回到最小空闲位置
        Assert.True(pool.TryOffer(MacB, out var reused, out _));
        Assert.Equal(address, reused);
    }

    [Fact]
    public void RepeatedAckReturnsSameLease()
    {
        var pool = new DhcpPool();
        pool.Activate(Config());
        pool.TryOffer(MacA, out var address, out _);
        pool.HandleRequest(MacA, Server, address, 0);
        var again = pool.HandleRequest(MacA, Server, address, 0);
        Assert.Equal(RequestOutcome.Ack, again.Outcome);
        Assert.Equal(address, again.Address);
    }

    [Fact]
    public void ExhaustedPoolStopsOffering()
    {
        var tiny = new PoolConfig("lab", 0x0A000064, 30, 0x0A000065, 0x0A000066, 30, 300, Server);
        var pool = new DhcpPool();
        pool.Activate(tiny);
        Assert.True(pool.TryOffer(MacA, out _, out _));
        Assert.True(pool.TryOffer(MacB, out _, out _));
        Assert.False(pool.TryOffer(new byte[] { 2, 0, 0, 0, 0, 3 }, out _, out _));
    }

    [Fact]
    public void ExpiredLeaseIsReclaimed()
    {
        var now = DateTimeOffset.UtcNow;
        var pool = new DhcpPool(() => now);
        pool.Activate(Config(offerSeconds: 10, leaseSeconds: 20));
        pool.TryOffer(MacA, out var address, out _);
        pool.HandleRequest(MacA, Server, address, 0);
        now = now.AddSeconds(25);
        Assert.True(pool.TryOffer(MacB, out var reused, out _));
        Assert.Equal(address, reused);
    }

    [Fact]
    public void ActivationRejectedWhileLeasesActive()
    {
        var pool = new DhcpPool();
        pool.Activate(Config());
        pool.TryOffer(MacA, out var address, out _);
        pool.HandleRequest(MacA, Server, address, 0);
        Assert.False(pool.Activate(Config()));
        // 旧池不受影响
        Assert.Equal("eng", pool.GetStatus().DepartmentId);
    }

    [Fact]
    public void ActivationAllowedAfterExpiry()
    {
        var now = DateTimeOffset.UtcNow;
        var pool = new DhcpPool(() => now);
        pool.Activate(Config(offerSeconds: 10, leaseSeconds: 20));
        pool.TryOffer(MacA, out var address, out _);
        pool.HandleRequest(MacA, Server, address, 0);
        now = now.AddSeconds(30);
        Assert.True(pool.Activate(Config()));
        Assert.Empty(pool.GetStatus().Leases);
    }

    [Fact]
    public void ConcurrentDiscoversNeverShareAddress()
    {
        var pool = new DhcpPool();
        pool.Activate(Config());
        var addresses = new System.Collections.Concurrent.ConcurrentBag<uint>();
        Parallel.For(0, 40, i =>
        {
            var mac = new byte[] { 2, 0, 0, 0, 0, (byte)i };
            if (pool.TryOffer(mac, out var address, out _))
                addresses.Add(address);
        });
        Assert.Equal(addresses.Count, addresses.Distinct().Count());
    }
}
