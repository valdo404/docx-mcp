using System.Security.Claims;

namespace DocxMcp.Auth;

/// <summary>
/// Represents the authenticated user context for the current request.
/// Provides access to user identity, tokens, and Microsoft Graph credentials.
/// </summary>
public sealed class UserContext
{
    /// <summary>
    /// Unique user identifier (object ID from Azure AD).
    /// </summary>
    public string UserId { get; init; } = string.Empty;

    /// <summary>
    /// User's email address.
    /// </summary>
    public string Email { get; init; } = string.Empty;

    /// <summary>
    /// User's display name.
    /// </summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>
    /// Tenant ID the user belongs to.
    /// </summary>
    public string TenantId { get; init; } = string.Empty;

    /// <summary>
    /// Access token for Microsoft Graph API calls.
    /// </summary>
    public string AccessToken { get; init; } = string.Empty;

    /// <summary>
    /// Whether the user is authenticated.
    /// </summary>
    public bool IsAuthenticated => !string.IsNullOrEmpty(UserId);

    /// <summary>
    /// Create a UserContext from ClaimsPrincipal and access token.
    /// </summary>
    public static UserContext FromClaimsPrincipal(ClaimsPrincipal principal, string accessToken)
    {
        return new UserContext
        {
            UserId = principal.FindFirstValue("oid")
                  ?? principal.FindFirstValue(ClaimTypes.NameIdentifier)
                  ?? string.Empty,
            Email = principal.FindFirstValue("preferred_username")
                 ?? principal.FindFirstValue(ClaimTypes.Email)
                 ?? string.Empty,
            DisplayName = principal.FindFirstValue("name")
                       ?? principal.FindFirstValue(ClaimTypes.Name)
                       ?? string.Empty,
            TenantId = principal.FindFirstValue("tid") ?? string.Empty,
            AccessToken = accessToken
        };
    }

    /// <summary>
    /// Create an anonymous (unauthenticated) context.
    /// </summary>
    public static UserContext Anonymous => new();
}
