using System.Text.Json;
using Mandate.Core.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Mandate.Core.Audit;

public static class AuditLog
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Appends within the caller's transaction. Callers only append after winning a state
    /// transition, and the unique (ProposalId, Seq) index rejects a concurrent append anyway.
    /// </summary>
    public static async Task AppendAsync(MandateDb db, Guid proposalId, string type, string actor, object? data,
        DateTimeOffset at, CancellationToken ct)
    {
        var last = await db.AuditEvents.Where(e => e.ProposalId == proposalId)
            .OrderByDescending(e => e.Seq).Select(e => new { e.Seq, e.Hash }).FirstOrDefaultAsync(ct);

        // Postgres keeps microseconds; truncate so the hash we compute now matches the row we read back later.
        var e = new AuditEvent
        {
            ProposalId = proposalId,
            Seq = (last?.Seq ?? -1) + 1,
            Type = type,
            Actor = actor,
            DataJson = JsonSerializer.Serialize(data ?? new { }, Json),
            At = DateTimeOffset.FromUnixTimeMilliseconds(at.ToUnixTimeMilliseconds()),
            PrevHash = last?.Hash ?? AuditChain.Genesis,
        };
        e.Hash = AuditChain.ComputeHash(e);
        db.AuditEvents.Add(e);
        await db.SaveChangesAsync(ct);
    }
}
