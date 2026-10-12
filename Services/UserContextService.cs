using Microsoft.AspNetCore.Http;
using System.Security.Claims;

namespace CustomCoverArt.Services;

public interface IUserContextService
{
    string? GetCurrentUserId();
    string? GetCurrentUserName();
}

public class UserContextService : IUserContextService
{
    /// <summary>The claim Jellyfin's own auth handler puts the user's id in.</summary>
    public const string JellyfinUserIdClaim = "Jellyfin-UserId";

    private readonly IHttpContextAccessor _httpContextAccessor;

    public UserContextService(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public string? GetCurrentUserId()
    {
        var user = _httpContextAccessor.HttpContext?.User;
        // Jellyfin issues ClaimTypes.Name and "Jellyfin-UserId"; the other two are kept
        // for any other auth scheme a host might put in front.
        return user?.FindFirst(JellyfinUserIdClaim)?.Value ??
               user?.FindFirst(ClaimTypes.NameIdentifier)?.Value ??
               user?.FindFirst("sub")?.Value;
    }

    public string? GetCurrentUserName()
    {
        var user = _httpContextAccessor.HttpContext?.User;
        return user?.FindFirst(ClaimTypes.Name)?.Value ??
               user?.FindFirst("name")?.Value ??
               user?.Identity?.Name;
    }
}
