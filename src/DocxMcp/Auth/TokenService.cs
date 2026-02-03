using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocxMcp.Auth;

/// <summary>
/// Service for OAuth token exchange, validation, and refresh.
/// Handles the OAuth 2.0 authorization code flow with Microsoft Entra ID.
/// </summary>
public sealed class TokenService
{
    private readonly OAuthOptions _options;
    private readonly HttpClient _httpClient;
    private readonly ILogger<TokenService> _logger;
    private readonly ConcurrentDictionary<string, TokenInfo> _tokenCache = new();

    public TokenService(IOptions<OAuthOptions> options, HttpClient httpClient, ILogger<TokenService> logger)
    {
        _options = options.Value;
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <summary>
    /// Generate the authorization URL to redirect the user to Microsoft login.
    /// </summary>
    public string GetAuthorizationUrl(string state)
    {
        var scopes = string.Join(" ", _options.Scopes);
        var authUrl = $"https://login.microsoftonline.com/{_options.TenantId}/oauth2/v2.0/authorize" +
            $"?client_id={Uri.EscapeDataString(_options.ClientId)}" +
            $"&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(_options.RedirectUri)}" +
            $"&scope={Uri.EscapeDataString(scopes)}" +
            $"&state={Uri.EscapeDataString(state)}" +
            $"&response_mode=query";
        return authUrl;
    }

    /// <summary>
    /// Exchange an authorization code for access and refresh tokens.
    /// </summary>
    public async Task<TokenInfo?> ExchangeCodeAsync(string code, CancellationToken ct = default)
    {
        var tokenEndpoint = $"https://login.microsoftonline.com/{_options.TenantId}/oauth2/v2.0/token";

        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _options.ClientId,
            ["client_secret"] = _options.ClientSecret,
            ["code"] = code,
            ["redirect_uri"] = _options.RedirectUri,
            ["grant_type"] = "authorization_code",
            ["scope"] = string.Join(" ", _options.Scopes)
        });

        try
        {
            var response = await _httpClient.PostAsync(tokenEndpoint, content, ct);
            var json = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Token exchange failed: {StatusCode} {Response}", response.StatusCode, json);
                return null;
            }

            var tokenResponse = JsonSerializer.Deserialize<TokenResponse>(json);
            if (tokenResponse is null)
                return null;

            var tokenInfo = new TokenInfo
            {
                AccessToken = tokenResponse.access_token ?? string.Empty,
                RefreshToken = tokenResponse.refresh_token ?? string.Empty,
                ExpiresAt = DateTime.UtcNow.AddSeconds(tokenResponse.expires_in - 60), // 60s buffer
                Scopes = tokenResponse.scope?.Split(' ') ?? Array.Empty<string>()
            };

            // Cache the token by user ID extracted from JWT
            var userId = ExtractUserIdFromToken(tokenInfo.AccessToken);
            if (!string.IsNullOrEmpty(userId))
            {
                _tokenCache[userId] = tokenInfo;
            }

            return tokenInfo;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to exchange authorization code for tokens");
            return null;
        }
    }

    /// <summary>
    /// Refresh an access token using a refresh token.
    /// </summary>
    public async Task<TokenInfo?> RefreshTokenAsync(string refreshToken, CancellationToken ct = default)
    {
        var tokenEndpoint = $"https://login.microsoftonline.com/{_options.TenantId}/oauth2/v2.0/token";

        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _options.ClientId,
            ["client_secret"] = _options.ClientSecret,
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token",
            ["scope"] = string.Join(" ", _options.Scopes)
        });

        try
        {
            var response = await _httpClient.PostAsync(tokenEndpoint, content, ct);
            var json = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Token refresh failed: {StatusCode} {Response}", response.StatusCode, json);
                return null;
            }

            var tokenResponse = JsonSerializer.Deserialize<TokenResponse>(json);
            if (tokenResponse is null)
                return null;

            return new TokenInfo
            {
                AccessToken = tokenResponse.access_token ?? string.Empty,
                RefreshToken = tokenResponse.refresh_token ?? refreshToken, // Keep old if not returned
                ExpiresAt = DateTime.UtcNow.AddSeconds(tokenResponse.expires_in - 60),
                Scopes = tokenResponse.scope?.Split(' ') ?? Array.Empty<string>()
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh token");
            return null;
        }
    }

    /// <summary>
    /// Validate a bearer token and extract claims.
    /// </summary>
    public ClaimsPrincipal? ValidateToken(string token)
    {
        try
        {
            var handler = new JwtSecurityTokenHandler();

            // For Microsoft tokens, we do basic validation
            // Full validation with keys would require fetching JWKS
            if (!handler.CanReadToken(token))
                return null;

            var jwt = handler.ReadJwtToken(token);

            // Check expiration
            if (jwt.ValidTo < DateTime.UtcNow)
            {
                _logger.LogWarning("Token has expired");
                return null;
            }

            // Check audience (client ID)
            var audience = jwt.Claims.FirstOrDefault(c => c.Type == "aud")?.Value;
            if (_options.ValidateTokens && audience != _options.ClientId)
            {
                _logger.LogWarning("Token audience mismatch: expected {Expected}, got {Actual}",
                    _options.ClientId, audience);
                return null;
            }

            // Build ClaimsPrincipal
            var identity = new ClaimsIdentity(jwt.Claims, "Bearer");
            return new ClaimsPrincipal(identity);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Token validation failed");
            return null;
        }
    }

    /// <summary>
    /// Get a valid access token for a user, refreshing if necessary.
    /// </summary>
    public async Task<string?> GetValidAccessTokenAsync(string userId, CancellationToken ct = default)
    {
        if (!_tokenCache.TryGetValue(userId, out var tokenInfo))
            return null;

        // If token is still valid, return it
        if (tokenInfo.ExpiresAt > DateTime.UtcNow)
            return tokenInfo.AccessToken;

        // Try to refresh
        if (!string.IsNullOrEmpty(tokenInfo.RefreshToken))
        {
            var newToken = await RefreshTokenAsync(tokenInfo.RefreshToken, ct);
            if (newToken is not null)
            {
                _tokenCache[userId] = newToken;
                return newToken.AccessToken;
            }
        }

        // Token expired and refresh failed
        _tokenCache.TryRemove(userId, out _);
        return null;
    }

    /// <summary>
    /// Store a token for a user.
    /// </summary>
    public void StoreToken(string userId, TokenInfo tokenInfo)
    {
        _tokenCache[userId] = tokenInfo;
    }

    private static string? ExtractUserIdFromToken(string token)
    {
        try
        {
            var handler = new JwtSecurityTokenHandler();
            var jwt = handler.ReadJwtToken(token);
            return jwt.Claims.FirstOrDefault(c => c.Type == "oid")?.Value
                ?? jwt.Claims.FirstOrDefault(c => c.Type == "sub")?.Value;
        }
        catch
        {
            return null;
        }
    }

    private sealed class TokenResponse
    {
        public string? access_token { get; set; }
        public string? refresh_token { get; set; }
        public int expires_in { get; set; }
        public string? scope { get; set; }
        public string? token_type { get; set; }
    }
}

/// <summary>
/// Holds OAuth token information.
/// </summary>
public sealed class TokenInfo
{
    public string AccessToken { get; init; } = string.Empty;
    public string RefreshToken { get; init; } = string.Empty;
    public DateTime ExpiresAt { get; init; }
    public string[] Scopes { get; init; } = Array.Empty<string>();
}
