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
    private readonly IHttpContextAccessor _httpContextAccessor;

    public UserContextService(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
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
}
