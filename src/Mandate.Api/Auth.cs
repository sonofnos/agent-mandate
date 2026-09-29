using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Mandate.Api;

/// <summary>
/// Three separate principals. The agent credential can chat and propose; only the user
/// credential can approve or reject; only ops can pull the kill switch. A leaked or
/// prompt-injected agent credential cannot approve its own proposals.
/// </summary>
public static class Roles
{
    public const string User = "user";
    public const string Agent = "agent";
    public const string Ops = "ops";
}

public class TokenService(byte[] key)
{
    public string Issue(string role, string subject) => $"{role}.{subject}.{Sign(role, subject)}";

    public (string Role, string Subject)? Validate(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 3) return null;
        var expected = Encoding.ASCII.GetBytes(Sign(parts[0], parts[1]));
        return CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(parts[2])) ? (parts[0], parts[1]) : null;
    }

    private string Sign(string role, string subject) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"{role}|{subject}")));
}

public class TokenAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, TokenService tokens)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Token";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal)) return Task.FromResult(AuthenticateResult.NoResult());
        if (tokens.Validate(header["Bearer ".Length..].Trim()) is not { } t) return Task.FromResult(AuthenticateResult.Fail("bad token"));

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, t.Subject), new Claim(ClaimTypes.Role, t.Role)], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}

public static class PrincipalExtensions
{
    public static string UserId(this ClaimsPrincipal p) => p.FindFirstValue(ClaimTypes.NameIdentifier)!;
}
