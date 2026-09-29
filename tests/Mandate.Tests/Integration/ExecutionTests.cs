using Mandate.Core.Domain;
using Mandate.Core.Execution;
using Mandate.Core.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Mandate.Tests.Integration;

public class ExecutionTests
{
    private static Task<ActionProposal> Load(Harness h, Guid id) => h.With(db => db.Proposals.AsNoTracking().FirstAsync(p => p.Id == id));

    private static Task<long> Reserved(Harness h) =>
        h.With(db => db.MandateUsage.Where(u => db.Mandates.Any(m => m.Id == u.MandateId && m.UserId == h.UserId)).SumAsync(u => u.ReservedMinor));

    [Fact]
    public async Task Five_workers_racing_on_one_proposal_send_it_once()
    {
        var h = await Harness.CreateAsync();
        h.Provider.HonoursIdempotencyKey = false; // our side alone must prevent the duplicate
        h.Provider.Latency = TimeSpan.FromMilliseconds(50);
        var p = (await h.ProposeAsync("mum", "5000")).Proposal!;

        var claims = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Task.Run(async () =>
        {
            await using var db = h.NewDb();
            return await h.Executor(db).ExecuteAsync(p.Id);
        })));

        Assert.Single(claims, c => c);
        Assert.Equal(1, h.Provider.AppliedCount(p.IdempotencyKey));
        Assert.Equal(ProposalStatus.Succeeded, (await Load(h, p.Id)).Status);
    }

    [Fact]
    public async Task Timeout_after_the_money_moved_is_reconciled_not_resent()
    {
        var h = await Harness.CreateAsync();
        h.Provider.HonoursIdempotencyKey = false;
        h.Provider.Enqueue(Fault.TimeoutAfterApply);
        var p = (await h.ProposeAsync("mum", "5000")).Proposal!;

        await h.With(db => h.Executor(db).ExecuteAsync(p.Id));
        Assert.Equal(ProposalStatus.Unknown, (await Load(h, p.Id)).Status);

        // Any number of worker loops must not send it again.
        await h.With(db => h.Executor(db).DrainAsync());
        await h.With(db => h.Executor(db).ReconcileAsync());
        await h.With(db => h.Executor(db).DrainAsync());

        var done = await Load(h, p.Id);
        Assert.Equal(ProposalStatus.Succeeded, done.Status);
        Assert.NotNull(done.ProviderReference);
        Assert.Equal(1, h.Provider.AppliedCount(p.IdempotencyKey));
        Assert.Equal(5_000_00, await Reserved(h)); // money moved, spend stays counted
    }

    [Fact]
    public async Task Timeout_before_the_provider_saw_it_is_resent_only_after_the_settle_window()
    {
        var h = await Harness.CreateAsync();
        h.Provider.Enqueue(Fault.TimeoutBeforeApply);
        var p = (await h.ProposeAsync("mum", "5000")).Proposal!;
        await h.With(db => h.Executor(db).ExecuteAsync(p.Id));

        // Provider says "not found", but the request could still be in flight: wait.
        await h.With(db => h.Executor(db).ReconcileAsync());
        Assert.Equal(ProposalStatus.Unknown, (await Load(h, p.Id)).Status);

        h.Clock.Advance(h.Options.NotFoundSettleTime + TimeSpan.FromSeconds(1));
        await h.With(db => h.Executor(db).ReconcileAsync());
        Assert.Equal(ProposalStatus.Approved, (await Load(h, p.Id)).Status);

        await h.With(db => h.Executor(db).DrainAsync());
        var done = await Load(h, p.Id);
        Assert.Equal(ProposalStatus.Succeeded, done.Status);
        Assert.Equal(2, done.Attempts);
        Assert.Equal(1, h.Provider.AppliedCount(p.IdempotencyKey));
    }

    [Fact]
    public async Task Gives_up_after_max_attempts_and_releases_the_spend()
    {
        var h = await Harness.CreateAsync();
        h.Options.MaxAttempts = 2;
        h.Provider.Enqueue(Fault.TimeoutBeforeApply, Fault.TimeoutBeforeApply);
        var p = (await h.ProposeAsync("mum", "5000")).Proposal!;

        for (var i = 0; i < 3; i++)
        {
            await h.With(db => h.Executor(db).DrainAsync());
            h.Clock.Advance(h.Options.NotFoundSettleTime + TimeSpan.FromSeconds(1));
            await h.With(db => h.Executor(db).ReconcileAsync());
        }

        var done = await Load(h, p.Id);
        Assert.Equal(ProposalStatus.Failed, done.Status);
        Assert.Equal(0, h.Provider.AppliedCount(p.IdempotencyKey));
        Assert.Equal(0, await Reserved(h));
    }

    [Fact]
    public async Task Decline_fails_the_proposal_and_releases_the_spend()
    {
        var h = await Harness.CreateAsync();
        h.Provider.Enqueue(Fault.Decline);
        var p = (await h.ProposeAsync("mum", "5000")).Proposal!;
        await h.With(db => h.Executor(db).ExecuteAsync(p.Id));

        var done = await Load(h, p.Id);
        Assert.Equal(ProposalStatus.Failed, done.Status);
        Assert.Equal("Insufficient funds", done.LastError);
        Assert.Equal(0, await Reserved(h));
    }

    [Fact]
    public async Task Worker_that_died_mid_call_is_recovered_through_reconciliation()
    {
        var h = await Harness.CreateAsync();
        var p = (await h.ProposeAsync("mum", "5000")).Proposal!;

        // Claimed by a worker that crashed before calling the provider.
        await h.With(db => h.Proposals(db).TransitionAsync(p.Id, ProposalStatus.Approved, ProposalStatus.Executing, "executor", new { },
            s => s.SetProperty(x => x.Attempts, 1).SetProperty(x => x.ExecutionStartedAt, h.Clock.GetUtcNow()), default));

        await h.With(db => h.Executor(db).ReconcileAsync());
        Assert.Equal(ProposalStatus.Executing, (await Load(h, p.Id)).Status); // lease not expired yet

        h.Clock.Advance(h.Options.ExecutionLease + TimeSpan.FromSeconds(1));
        await h.With(db => h.Executor(db).ReconcileAsync()); // -> Unknown -> provider never saw it -> Approved
        await h.With(db => h.Executor(db).DrainAsync());

        Assert.Equal(ProposalStatus.Succeeded, (await Load(h, p.Id)).Status);
        Assert.Equal(1, h.Provider.AppliedCount(p.IdempotencyKey));
    }

    [Fact]
    public async Task Awaiting_approval_is_never_executed()
    {
        var h = await Harness.CreateAsync();
        var p = (await h.ProposeAsync("mum", "50000")).Proposal!;
        Assert.False(await h.With(db => h.Executor(db).ExecuteAsync(p.Id)));
        Assert.Equal(0, await h.With(db => h.Executor(db).DrainAsync()));
        Assert.Empty(h.Provider.Applied);
    }
}
