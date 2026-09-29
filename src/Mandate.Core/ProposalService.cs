using Mandate.Core.Audit;
using Mandate.Core.Domain;
using Mandate.Core.Persistence;
using Mandate.Core.Policy;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Mandate.Core;

public record ProposeRequest(
    string UserId,
    string AgentId,
    string TurnId,
    ActionKind Kind,
    string BeneficiaryAlias,
    string Amount,
    string? Narration,
    Guid? AgentRunId = null,
    string? TraceId = null);

public record ProposeResult(ActionProposal? Proposal, bool Replayed, string? Error)
{
    public static ProposeResult Fail(string error) => new(null, false, error);
}

public enum ApprovalOutcome
{
    Approved,
    AlreadyApproved,
    Rejected,
    InvalidToken,
    NotFound,
    NotPending,
    Expired,
}

public class KillSwitch
{
    public volatile bool On;
}

public class ProposalService(MandateDb db, ApprovalTokens tokens, KillSwitch killSwitch, TimeProvider clock)
{
    public static readonly TimeSpan DuplicateWindow = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan ApprovalTtl = TimeSpan.FromMinutes(10);

    private static readonly ProposalStatus[] LiveStatuses =
    [
        ProposalStatus.AwaitingApproval, ProposalStatus.Approved, ProposalStatus.Executing,
        ProposalStatus.Succeeded, ProposalStatus.Unknown,
    ];

    public async Task<ProposeResult> ProposeAsync(ProposeRequest r, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(r.TurnId)) return ProposeResult.Fail("turn id is required for de-duplication.");
        if (!Money.TryParseMajor(r.Amount, out var amountMinor, out var amountError)) return ProposeResult.Fail(amountError!);

        var alias = r.BeneficiaryAlias.Trim().ToLowerInvariant();
        var beneficiary = await db.Beneficiaries.AsNoTracking()
            .FirstOrDefaultAsync(b => b.UserId == r.UserId && b.Alias == alias, ct);
        if (beneficiary is null)
            return ProposeResult.Fail(
                $"No saved beneficiary called '{r.BeneficiaryAlias}'. New recipients can only be added by the user in the app.");

        var now = clock.GetUtcNow();
        var mandate = await db.Mandates.AsNoTracking()
            .Where(m => m.UserId == r.UserId && m.AgentId == r.AgentId)
            .OrderByDescending(m => m.ExpiresAt).FirstOrDefaultAsync(ct);
        if (mandate is null) return ProposeResult.Fail("This agent has no mandate for this user.");

        var fingerprint = ActionProposal.ComputeFingerprint(r.Kind, r.UserId, beneficiary.Id, amountMinor, mandate.Currency);
        var existing = await FindByTurnAsync(r.UserId, r.TurnId, fingerprint, ct);
        if (existing is not null) return new ProposeResult(existing, true, null);

        var since = now - DuplicateWindow;
        var recentDuplicate = await db.Proposals.AsNoTracking()
            .Where(p => p.UserId == r.UserId && p.Fingerprint == fingerprint && p.CreatedAt >= since && p.TurnId != r.TurnId
                        && LiveStatuses.Contains(p.Status))
            .Select(p => (Guid?)p.Id).FirstOrDefaultAsync(ct);

        var decision = PolicyEngine.Evaluate(new PolicyInput(
            mandate, r.Kind, beneficiary, amountMinor, mandate.Currency, now, killSwitch.On, recentDuplicate));

        var p = new ActionProposal
        {
            UserId = r.UserId,
            AgentId = r.AgentId,
            MandateId = mandate.Id,
            Kind = r.Kind,
            BeneficiaryId = beneficiary.Id,
            BeneficiaryName = beneficiary.AccountName,
            BankCode = beneficiary.BankCode,
            AccountNumber = beneficiary.AccountNumber,
            AmountMinor = amountMinor,
            Currency = mandate.Currency,
            Narration = (r.Narration ?? "").Trim() is { Length: > 0 } n ? n[..Math.Min(n.Length, 100)] : "Transfer",
            TurnId = r.TurnId,
            Fingerprint = fingerprint,
            SpendDay = SpendReservations.LagosDay(now),
            Reasons = [.. decision.Reasons],
            AgentRunId = r.AgentRunId,
            TraceId = r.TraceId,
            CreatedAt = now,
            UpdatedAt = now,
            ApprovalExpiresAt = now + ApprovalTtl,
            Status = decision.Outcome switch
            {
                PolicyOutcome.Deny => ProposalStatus.Denied,
                PolicyOutcome.RequireApproval => ProposalStatus.AwaitingApproval,
                _ => ProposalStatus.Approved,
            },
        };
        p.ContentHash = p.ComputeContentHash();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            if (p.Status != ProposalStatus.Denied)
            {
                if (await SpendReservations.TryReserveAsync(db, mandate.Id, p.SpendDay, amountMinor, mandate.DailyLimitMinor, ct))
                {
                    p.ReservedSpend = true;
                }
                else
                {
                    var used = await SpendReservations.ReservedAsync(db, mandate.Id, p.SpendDay, ct);
                    p.Status = ProposalStatus.Denied;
                    p.Reasons = [$"Would exceed today's limit: {Money.Format(used)} of {Money.Format(mandate.DailyLimitMinor)} already used or pending."];
                }
            }

            db.Proposals.Add(p);
            await db.SaveChangesAsync(ct);
            await AuditLog.AppendAsync(db, p.Id, "proposed", $"agent:{r.AgentId}", new
            {
                p.Kind, p.AmountMinor, p.Currency, p.BeneficiaryId, beneficiaryAlias = alias, p.Narration, p.TurnId,
                rawAmount = r.Amount, r.AgentRunId, r.TraceId,
            }, now, ct);
            await AuditLog.AppendAsync(db, p.Id, "policy_evaluated", "policy", new
            {
                outcome = decision.Outcome.ToString(), status = p.Status.ToString(), p.Reasons, p.ReservedSpend,
                mandate = new { mandate.Id, mandate.PerActionLimitMinor, mandate.DailyLimitMinor, mandate.AutoApproveLimitMinor },
            }, now, ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Lost a race with an identical call in the same turn. The reservation rolled back with us.
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            var winner = await FindByTurnAsync(r.UserId, r.TurnId, fingerprint, ct);
            return new ProposeResult(winner, true, null);
        }

        return new ProposeResult(p, false, null);
    }

    public Task<ActionProposal?> FindByTurnAsync(string userId, string turnId, string fingerprint, CancellationToken ct) =>
        db.Proposals.AsNoTracking().FirstOrDefaultAsync(
            x => x.UserId == userId && x.TurnId == turnId && x.Fingerprint == fingerprint, ct);

    /// <summary>Token for the user's approval channel. Never returned to the agent.</summary>
    public string ApprovalTokenFor(ActionProposal p) => tokens.Issue(p.Id, p.ContentHash, p.ApprovalExpiresAt);

    public async Task<ApprovalOutcome> ApproveAsync(Guid id, string userId, string? token, CancellationToken ct = default)
    {
        var p = await db.Proposals.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct);
        if (p is null) return ApprovalOutcome.NotFound;
        var now = clock.GetUtcNow();

        if (p.Status is ProposalStatus.Approved or ProposalStatus.Executing or ProposalStatus.Succeeded or ProposalStatus.Unknown)
            return ApprovalOutcome.AlreadyApproved; // double tap
        if (p.Status != ProposalStatus.AwaitingApproval) return ApprovalOutcome.NotPending;
        // Recompute from stored fields: if anything was altered after proposal, the hash no longer matches the token.
        if (!tokens.Verify(token, p.Id, p.ComputeContentHash(), now)) return ApprovalOutcome.InvalidToken;
        if (now > p.ApprovalExpiresAt)
        {
            await TransitionAsync(id, ProposalStatus.AwaitingApproval, ProposalStatus.Expired, "system", new { }, null, ct);
            return ApprovalOutcome.Expired;
        }

        var won = await TransitionAsync(id, ProposalStatus.AwaitingApproval, ProposalStatus.Approved, $"user:{userId}",
            new { channel = "app" }, s => s.SetProperty(x => x.ApprovedBy, userId).SetProperty(x => x.ApprovedAt, now), ct);
        return won ? ApprovalOutcome.Approved : ApprovalOutcome.AlreadyApproved;
    }

    public async Task<ApprovalOutcome> RejectAsync(Guid id, string userId, CancellationToken ct = default)
    {
        var p = await db.Proposals.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct);
        if (p is null) return ApprovalOutcome.NotFound;
        var won = await TransitionAsync(id, ProposalStatus.AwaitingApproval, ProposalStatus.Rejected, $"user:{userId}", new { }, null, ct);
        return won ? ApprovalOutcome.Rejected : ApprovalOutcome.NotPending;
    }

    public async Task<int> ExpireStaleApprovalsAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var stale = await db.Proposals.AsNoTracking()
            .Where(p => p.Status == ProposalStatus.AwaitingApproval && p.ApprovalExpiresAt < now)
            .Select(p => p.Id).Take(100).ToListAsync(ct);
        var n = 0;
        foreach (var id in stale)
            if (await TransitionAsync(id, ProposalStatus.AwaitingApproval, ProposalStatus.Expired, "system", new { }, null, ct))
                n++;
        return n;
    }

    /// <summary>
    /// Compare-and-set on status. Exactly one caller can move a proposal out of a given state,
    /// which is what makes approval single-use and execution single-claim. The audit append and
    /// any spend release happen in the same transaction as the status change.
    /// </summary>
    public async Task<bool> TransitionAsync(Guid id, ProposalStatus from, ProposalStatus to, string actor, object data,
        Action<Microsoft.EntityFrameworkCore.Query.UpdateSettersBuilder<ActionProposal>>? extra, CancellationToken ct)
    {
        if (!ProposalTransitions.IsAllowed(from, to)) throw new InvalidTransitionException(from, to);
        var now = clock.GetUtcNow();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var rows = await db.Proposals.Where(p => p.Id == id && p.Status == from).ExecuteUpdateAsync(s =>
        {
            s.SetProperty(p => p.Status, to).SetProperty(p => p.UpdatedAt, now);
            extra?.Invoke(s);
        }, ct);
        if (rows == 0)
        {
            await tx.RollbackAsync(ct);
            return false;
        }

        var p = await db.Proposals.AsNoTracking().FirstAsync(x => x.Id == id, ct);
        if (ProposalTransitions.ReleasesSpend(to) && p.ReservedSpend)
        {
            await SpendReservations.ReleaseAsync(db, p.MandateId, p.SpendDay, p.AmountMinor, ct);
            await db.Proposals.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.ReservedSpend, false), ct);
        }

        await AuditLog.AppendAsync(db, id, to.ToString().ToLowerInvariant(), actor, new { from = from.ToString(), to = to.ToString(), data }, now, ct);
        await tx.CommitAsync(ct);
        return true;
    }
}
