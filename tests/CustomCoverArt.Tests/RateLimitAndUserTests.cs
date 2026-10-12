using System;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using CustomCoverArt.Services;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using Xunit;

namespace CustomCoverArt.Tests;

public class RateLimitAndUserTests
{
    /// <summary>Check and record were two separate locks, so a concurrent burst all passed the check.</summary>
    [Fact]
    public void ConcurrentBurst_AdmitsExactlyTheLimit()
    {
        var limiter = new RateLimitingService();
        var admitted = 0;

        Parallel.For(0, 200, _ =>
        {
            if (limiter.TryAcquire("user", "apply", 5, TimeSpan.FromMinutes(1)))
            {
                Interlocked.Increment(ref admitted);
            }
        });

        Assert.Equal(5, admitted);
    }

    [Fact]
    public void TryAcquire_ReadmitsAfterTheWindow()
    {
        var limiter = new RateLimitingService();
        var window = TimeSpan.FromMilliseconds(100);

        Assert.True(limiter.TryAcquire("u", "upload", 1, window));
        Assert.False(limiter.TryAcquire("u", "upload", 1, window));
        Thread.Sleep(250);
        Assert.True(limiter.TryAcquire("u", "upload", 1, window));
    }

    [Fact]
    public void Limits_ArePerClientAndPerAction()
    {
        var limiter = new RateLimitingService();
        Assert.True(limiter.TryAcquire("a", "apply", 1, TimeSpan.FromMinutes(1)));
        Assert.True(limiter.TryAcquire("b", "apply", 1, TimeSpan.FromMinutes(1)));
        Assert.True(limiter.TryAcquire("a", "upload", 1, TimeSpan.FromMinutes(1)));
        Assert.False(limiter.TryAcquire("a", "apply", 1, TimeSpan.FromMinutes(1)));
    }

    private static UserContextService UserWith(params Claim[] claims)
    {
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) };
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(http);
        return new UserContextService(accessor);
    }

    /// <summary>Jellyfin issues ClaimTypes.Name plus "Jellyfin-UserId", with no NameIdentifier, so every admin fell back to the IP.</summary>
    [Fact]
    public void UserId_ReadsJellyfinsOwnClaim()
    {
        var svc = UserWith(new Claim(ClaimTypes.Name, "admin"), new Claim("Jellyfin-UserId", "4f2c0d1e"));
        Assert.Equal("4f2c0d1e", svc.GetCurrentUserId());
    }

    [Fact]
    public void UserId_FallsBackToNameIdentifier()
    {
        var svc = UserWith(new Claim(ClaimTypes.NameIdentifier, "legacy-id"));
        Assert.Equal("legacy-id", svc.GetCurrentUserId());
    }
}
