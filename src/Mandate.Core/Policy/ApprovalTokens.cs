using System.Security.Cryptography;
using System.Text;

namespace Mandate.Core.Policy;

/// <summary>
/// An approval token is issued to the user's channel (app, push, USSD), never to the agent.
/// It is bound to the proposal's content hash and an expiry, so it cannot approve a different
/// amount or beneficiary, and a stale token dies on its own. Single use comes from the state
/// transition AwaitingApproval -> Approved, which only one request can win.
/// </summary>
public class ApprovalTokens(byte[] key)
{
    public string Issue(Guid proposalId, string contentHash, DateTimeOffset expiresAt)
    {
        var exp = expiresAt.ToUnixTimeSeconds();
        return $"{exp}.{Sign(proposalId, contentHash, exp)}";
    }

    public bool Verify(string? token, Guid proposalId, string contentHash, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(token)) return false;
        var dot = token.IndexOf('.');
        if (dot <= 0 || !long.TryParse(token.AsSpan(0, dot), out var exp)) return false;
        if (now.ToUnixTimeSeconds() > exp) return false;
        var expected = Encoding.ASCII.GetBytes(Sign(proposalId, contentHash, exp));
        var actual = Encoding.ASCII.GetBytes(token[(dot + 1)..]);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private string Sign(Guid proposalId, string contentHash, long exp) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"{proposalId}|{contentHash}|{exp}")));
}
