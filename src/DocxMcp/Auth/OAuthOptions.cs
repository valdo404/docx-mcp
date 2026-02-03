namespace DocxMcp.Auth;

/// <summary>
/// Configuration options for Microsoft Entra ID (Azure AD) OAuth.
/// </summary>
public sealed class OAuthOptions
{
    public const string SectionName = "AzureAd";

    /// <summary>
    /// Azure AD tenant ID. Use "common" for multi-tenant, or a specific tenant GUID.
    /// </summary>
    public string TenantId { get; set; } = "common";

    /// <summary>
    /// Application (client) ID from Azure AD app registration.
    /// </summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// Client secret for confidential client flow.
    /// </summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>
    /// OAuth scopes to request. Defaults include Graph API access for OneDrive/SharePoint.
    /// </summary>
    public string[] Scopes { get; set; } = new[]
    {
        "openid",
        "profile",
        "email",
        "offline_access",
        "Files.ReadWrite.All",    // OneDrive personal files
        "Sites.ReadWrite.All"     // SharePoint sites
    };

    /// <summary>
    /// Base URL for this service (used for redirect URI).
    /// </summary>
    public string BaseUrl { get; set; } = "http://localhost:8080";

    /// <summary>
    /// Redirect path after OAuth callback.
    /// </summary>
    public string RedirectPath { get; set; } = "/auth/callback";

    /// <summary>
    /// Full redirect URI for OAuth flow.
    /// </summary>
    public string RedirectUri => $"{BaseUrl.TrimEnd('/')}{RedirectPath}";

    /// <summary>
    /// Authority URL for Microsoft identity platform.
    /// </summary>
    public string Authority => $"https://login.microsoftonline.com/{TenantId}/v2.0";

    /// <summary>
    /// Whether to validate tokens (disable only for development).
    /// </summary>
    public bool ValidateTokens { get; set; } = true;
}
