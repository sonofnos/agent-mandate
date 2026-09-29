using System.Diagnostics;
using Mandate.Core.Domain;
using Mandate.Core.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Mandate.Core.Execution;

public class ExecutorOptions
{
    public TimeSpan ProviderTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>An Executing row older than this belongs to a worker that died. It is treated as Unknown.</summary>
    public TimeSpan ExecutionLease { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// A provider may still be processing a request that timed out. "Not found" is only believed
    /// once the request is at least this old.
    /// </summary>
    public TimeSpan NotFoundSettleTime { get; set; } = TimeSpan.FromMinutes(2);

    public int MaxAttempts { get; set; } = 3;
}

public class Executor(
    MandateDb db,
    ProposalService proposals,
    ITransferProvider provider,
    ExecutorOptions options,
    TimeProvider clock,
    ILogger<Executor> log)
{
    public static readonly ActivitySource Source = new("AgentMandate.Execution");

    /// <summary>Runs every Approved proposal once. Returns how many this worker claimed.</summary>
    public async Task<int> DrainAsync(CancellationToken ct = default)
    {
        var ids = await db.Proposals.AsNoTracking().Where(p => p.Status == ProposalStatus.Approved)
            .OrderBy(p => p.CreatedAt).Select(p => p.Id).Take(50).ToListAsync(ct);
        var claimed = 0;
        foreach (var id in ids)
            if (await ExecuteAsync(id, ct))
                claimed++;
        return claimed;
    }

    public async Task<bool> ExecuteAsync(Guid id, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        // The claim is committed before the provider is called. Two workers, a retry of this
        // job, or a redelivered message all race on the same compare-and-set; only one wins.
        var claimed = await proposals.TransitionAsync(id, ProposalStatus.Approved, ProposalStatus.Executing, "executor", new { },
            s => s.SetProperty(p => p.Attempts, p => p.Attempts + 1).SetProperty(p => p.ExecutionStartedAt, now), ct);
        if (!claimed) return false;

        var p = await db.Proposals.AsNoTracking().FirstAsync(x => x.Id == id, ct);
        using var activity = Source.StartActivity("provider.send");
        activity?.SetTag("proposal.id", p.Id);
        activity?.SetTag("proposal.attempt", p.Attempts);

        SendResult result;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(options.ProviderTimeout);
            result = await provider.SendAsync(new TransferRequest(p.IdempotencyKey, p.BankCode, p.AccountNumber,
                p.BeneficiaryName, p.AmountMinor, p.Currency, p.Narration), timeout.Token);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // We do not know whether money moved. Do not retry and do not fail: park it for reconciliation.
            log.LogWarning(e, "Provider outcome unknown for {ProposalId}", p.Id);
            activity?.SetStatus(ActivityStatusCode.Error, "unknown");
            await proposals.TransitionAsync(id, ProposalStatus.Executing, ProposalStatus.Unknown, "executor",
                new { error = e.Message }, s => s.SetProperty(x => x.LastError, e.Message), CancellationToken.None);
            return true;
        }

        if (result.Status == SendStatus.Succeeded)
            await proposals.TransitionAsync(id, ProposalStatus.Executing, ProposalStatus.Succeeded, "provider",
                new { reference = result.Reference }, s => s.SetProperty(x => x.ProviderReference, result.Reference), CancellationToken.None);
        else
            await proposals.TransitionAsync(id, ProposalStatus.Executing, ProposalStatus.Failed, "provider",
                new { error = result.Error }, s => s.SetProperty(x => x.LastError, result.Error), CancellationToken.None);
        return true;
    }

    /// <summary>
    /// Resolves Unknown proposals by asking the provider what happened to the idempotency key,
    /// and reclaims Executing rows whose worker died mid-call.
    /// </summary>
    public async Task<int> ReconcileAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var leaseCutoff = now - options.ExecutionLease;
        var orphaned = await db.Proposals.AsNoTracking()
            .Where(p => p.Status == ProposalStatus.Executing && p.ExecutionStartedAt < leaseCutoff)
            .OrderBy(p => p.ExecutionStartedAt).Select(p => p.Id).Take(50).ToListAsync(ct);
        foreach (var id in orphaned)
            await proposals.TransitionAsync(id, ProposalStatus.Executing, ProposalStatus.Unknown, "reconciler",
                new { reason = "execution lease expired" }, null, ct);

        var unknown = await db.Proposals.AsNoTracking().Where(p => p.Status == ProposalStatus.Unknown)
            .OrderBy(p => p.UpdatedAt).Take(50).ToListAsync(ct);
        var resolved = 0;
        foreach (var p in unknown)
        {
            var lookup = await provider.LookupAsync(p.IdempotencyKey, ct);
            switch (lookup.Status)
            {
                case LookupStatus.Succeeded:
                    resolved += await Resolve(p.Id, ProposalStatus.Succeeded, new { reference = lookup.Reference },
                        s => s.SetProperty(x => x.ProviderReference, lookup.Reference), ct);
                    break;
                case LookupStatus.Failed:
                    resolved += await Resolve(p.Id, ProposalStatus.Failed, new { lookup = "failed" }, null, ct);
                    break;
                case LookupStatus.NotFound when now - (p.ExecutionStartedAt ?? p.UpdatedAt) < options.NotFoundSettleTime:
                case LookupStatus.Pending:
                    break; // too early to tell
                case LookupStatus.NotFound when p.Attempts < options.MaxAttempts:
                    // Provider confirms it never saw the key: safe to send again with the same key.
                    resolved += await Resolve(p.Id, ProposalStatus.Approved, new { lookup = "not_found", requeue = true }, null, ct);
                    break;
                case LookupStatus.NotFound:
                    resolved += await Resolve(p.Id, ProposalStatus.Failed, new { lookup = "not_found", reason = "max attempts" },
                        s => s.SetProperty(x => x.LastError, "Not received by provider after max attempts"), ct);
                    break;
            }
        }
        return resolved;
    }

    private async Task<int> Resolve(Guid id, ProposalStatus to, object data,
        Action<Microsoft.EntityFrameworkCore.Query.UpdateSettersBuilder<ActionProposal>>? extra, CancellationToken ct) =>
        await proposals.TransitionAsync(id, ProposalStatus.Unknown, to, "reconciler", data, extra, ct) ? 1 : 0;
}
