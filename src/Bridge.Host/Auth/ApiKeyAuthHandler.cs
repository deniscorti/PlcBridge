using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Bridge.Host.Auth;

/// <summary>
/// Unified authentication handler: supports API key (header or query) and JWT bearer.
/// </summary>
public sealed class ApiKeyAuthHandler : AuthenticationHandler<ApiKeyAuthOptions>
{
    public const string SchemeName = "BridgeAuth";

    public ApiKeyAuthHandler(
        IOptionsMonitor<ApiKeyAuthOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder) { }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var expectedKey = Options.ApiKey;
        if (string.IsNullOrEmpty(expectedKey))
            return Task.FromResult(AuthenticateResult.NoResult()); // Auth disabled

        // Check X-Api-Key header
        if (Request.Headers.TryGetValue("X-Api-Key", out var headerKey) && headerKey == expectedKey)
            return Task.FromResult(Succeed());

        // Check Authorization: Bearer <key> (simple API key mode, not JWT)
        if (Request.Headers.TryGetValue("Authorization", out var auth))
        {
            var authStr = auth.ToString();
            if (authStr.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                var token = authStr["Bearer ".Length..].Trim();
                if (token == expectedKey)
                    return Task.FromResult(Succeed());
            }
        }

        // Check query string (for WebSocket connections)
        if (Request.Query.TryGetValue("apiKey", out var qsKey) && qsKey == expectedKey)
            return Task.FromResult(Succeed());

        if (Request.Query.TryGetValue("token", out var qsToken) && qsToken == expectedKey)
            return Task.FromResult(Succeed());

        return Task.FromResult(AuthenticateResult.Fail("Invalid or missing API key."));
    }

    private static AuthenticateResult Succeed()
    {
        var identity = new ClaimsIdentity(SchemeName);
        identity.AddClaim(new Claim(ClaimTypes.Name, "bridge-client"));
        var principal = new ClaimsPrincipal(identity);
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }
}

public sealed class ApiKeyAuthOptions : AuthenticationSchemeOptions
{
    public string? ApiKey { get; set; }
}
