using Microsoft.AspNetCore.Http;
using System.Security.Claims;

namespace CustomCoverArt.Services;

/// <summary>
/// Service for managing user context and authentication
/// </summary>
public interface IUserContextService
{
    string? GetCurrentUserId();
    string? GetCurrentUserName();
    bool IsCurrentUserAdmin();
    bool IsCurrentUserAuthenticated();
}

/// <summary>
/// Implementation of user context service
/// </summary>
public class UserContextService : IUserContextService
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILoggingService _loggingService;

    public UserContextService(IHttpContextAccessor httpContextAccessor, ILoggingService loggingService)
    {
        _httpContextAccessor = httpContextAccessor;
        _loggingService = loggingService;
    }

    public string? GetCurrentUserId()
    {
        var user = _httpContextAccessor.HttpContext?.User;
        return user?.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? 
               user?.FindFirst("sub")?.Value;
    }

    public string? GetCurrentUserName()
    {
        var user = _httpContextAccessor.HttpContext?.User;
        return user?.FindFirst(ClaimTypes.Name)?.Value ?? 
               user?.FindFirst("name")?.Value ??
               user?.Identity?.Name;
    }

    public bool IsCurrentUserAdmin()
    {
        var user = _httpContextAccessor.HttpContext?.User;
        if (user == null) return false;

        return user.IsInRole("Administrator") || 
               user.HasClaim("role", "Administrator") ||
               user.HasClaim("policy", "RequiresElevation");
    }

    public bool IsCurrentUserAuthenticated()
    {
        var user = _httpContextAccessor.HttpContext?.User;
        return user?.Identity?.IsAuthenticated ?? false;
    }
}
