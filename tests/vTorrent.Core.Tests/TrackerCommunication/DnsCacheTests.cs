using System;
using System.Net;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;
using vTorrent.Core.TrackerCommunication;

namespace vTorrent.Core.Tests.TrackerCommunication;

public class DnsCacheTests
{
    [Fact]
    public async Task ResolveAsync_CachesResult_SecondCallDoesNotResolveAgain()
    {
        int resolveCount = 0;
        var cache = new DnsCache(TimeSpan.FromMinutes(5), hostname =>
        {
            resolveCount++;
            return Task.FromResult(new[] { IPAddress.Loopback });
        });

        var first = await cache.ResolveAsync("tracker.example.com");
        var second = await cache.ResolveAsync("tracker.example.com");

        first.Should().BeEquivalentTo(new[] { IPAddress.Loopback });
        second.Should().BeEquivalentTo(new[] { IPAddress.Loopback });
        resolveCount.Should().Be(1, "second call should use cache");

        cache.Dispose();
    }

    [Fact]
    public async Task ResolveAsync_ExpiredEntry_ResolvesAgain()
    {
        int resolveCount = 0;
        var cache = new DnsCache(TimeSpan.FromMilliseconds(50), hostname =>
        {
            resolveCount++;
            return Task.FromResult(new[] { IPAddress.Loopback });
        });

        await cache.ResolveAsync("tracker.example.com");
        await Task.Delay(100); // Wait for expiry
        await cache.ResolveAsync("tracker.example.com");

        resolveCount.Should().Be(2, "expired entry should trigger fresh resolve");

        cache.Dispose();
    }

    [Fact]
    public async Task ResolveAsync_DifferentHostnames_ResolvedSeparately()
    {
        int resolveCount = 0;
        var cache = new DnsCache(TimeSpan.FromMinutes(5), hostname =>
        {
            resolveCount++;
            return Task.FromResult(new[] { IPAddress.Loopback });
        });

        await cache.ResolveAsync("tracker1.example.com");
        await cache.ResolveAsync("tracker2.example.com");

        resolveCount.Should().Be(2);

        cache.Dispose();
    }

    [Fact]
    public async Task Invalidate_ForcesReResolve()
    {
        int resolveCount = 0;
        var cache = new DnsCache(TimeSpan.FromMinutes(5), hostname =>
        {
            resolveCount++;
            return Task.FromResult(new[] { IPAddress.Loopback });
        });

        await cache.ResolveAsync("tracker.example.com");
        cache.Invalidate("tracker.example.com");
        await cache.ResolveAsync("tracker.example.com");

        resolveCount.Should().Be(2, "invalidation should force re-resolve");

        cache.Dispose();
    }

    [Fact]
    public async Task ResolveAsync_IpLiteral_ReturnedDirectly()
    {
        int resolveCount = 0;
        var cache = new DnsCache(TimeSpan.FromMinutes(5), hostname =>
        {
            resolveCount++;
            return Task.FromResult(new[] { IPAddress.Loopback });
        });

        var result = await cache.ResolveAsync("192.168.1.1");

        result.Should().ContainSingle().Which.ToString().Should().Be("192.168.1.1");
        resolveCount.Should().Be(0, "IP literals should not trigger DNS resolution");

        cache.Dispose();
    }

    [Fact]
    public async Task ResolveAsync_CaseInsensitive()
    {
        int resolveCount = 0;
        var cache = new DnsCache(TimeSpan.FromMinutes(5), hostname =>
        {
            resolveCount++;
            return Task.FromResult(new[] { IPAddress.Loopback });
        });

        await cache.ResolveAsync("Tracker.Example.COM");
        await cache.ResolveAsync("tracker.example.com");

        resolveCount.Should().Be(1, "hostnames should be case-insensitive");

        cache.Dispose();
    }
}
