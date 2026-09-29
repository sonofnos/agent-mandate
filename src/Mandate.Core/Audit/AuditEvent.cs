using System.Security.Cryptography;
using System.Text;

namespace Mandate.Core.Audit;

/// <summary>
/// Append-only trail per proposal. Each event hashes the previous one, so editing or deleting
/// a row in the middle breaks every hash after it (checked by <see cref="AuditChain.Verify"/>).
/// The chain is per proposal rather than global to avoid serialising every write on one lock.
/// </summary>
public class AuditEvent
{
    public long Id { get; set; }
    public Guid ProposalId { get; set; }
    public int Seq { get; set; }
    public string Type { get; set; } = "";
    public string Actor { get; set; } = "";
    public string DataJson { get; set; } = "{}";
    public DateTimeOffset At { get; set; }
    public string PrevHash { get; set; } = "";
    public string Hash { get; set; } = "";
}

public static class AuditChain
{
    public const string Genesis = "0000000000000000000000000000000000000000000000000000000000000000";

    public static string ComputeHash(AuditEvent e) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{e.PrevHash}|{e.ProposalId}|{e.Seq}|{e.Type}|{e.Actor}|{e.DataJson}|{e.At.ToUnixTimeMilliseconds()}")));

    /// <summary>Returns the sequence number of the first bad event, or null if the chain is intact.</summary>
    public static int? Verify(IReadOnlyList<AuditEvent> events)
    {
        var prev = Genesis;
        for (var i = 0; i < events.Count; i++)
        {
            var e = events[i];
            if (e.Seq != i || e.PrevHash != prev || e.Hash != ComputeHash(e)) return e.Seq;
            prev = e.Hash;
        }
        return null;
    }
}
