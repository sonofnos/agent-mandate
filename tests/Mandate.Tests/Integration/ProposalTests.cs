using Mandate.Core;
using Mandate.Core.Audit;
using Mandate.Core.Domain;
using Mandate.Core.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Mandate.Tests.Integration;

public class ProposalTests
{
    [Fact]
    public async Task Daily_limit_holds_under_twenty_concurrent_proposals()
    {
        var h = await Harness.CreateAsync();
        // 20 x ₦20,000 against a ₦300,000 daily cap, all at once, each in its own connection.
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => h.ProposeAsync("mum", "20000"))));

        var admitted = results.Count(r => r.Proposal!.Status != ProposalStatus.Denied);
        Assert.Equal(15, admitted);
        Assert.All(results.Where(r => r.Proposal!.Status == ProposalStatus.Denied),
            r => Assert.Contains("today's limit", r.Proposal!.Reasons[0]));

        var reserved = await h.With(db => db.MandateUsage.Where(u => db.Mandates.Any(m => m.Id == u.MandateId && m.UserId == h.UserId))
            .SumAsync(u => u.ReservedMinor));
        Assert.Equal(300_000_00, reserved);
    }

    [Fact]
    public async Task Same_turn_replayed_concurrently_creates_one_proposal_and_one_reservation()
    {
        var h = await Harness.CreateAsync();
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(() => h.ProposeAsync("mum", "5000", "turn-1"))));

        Assert.Single(results.Select(r => r.Proposal!.Id).Distinct());
        Assert.Equal(9, results.Count(r => r.Replayed));
        Assert.Equal(1, await h.With(db => db.Proposals.CountAsync(p => p.UserId == h.UserId)));
        Assert.Equal(5_000_00, await h.With(db => db.MandateUsage.Where(u => db.Mandates.Any(m => m.Id == u.MandateId && m.UserId == h.UserId))
            .SumAsync(u => u.ReservedMinor)));
    }

    [Fact]
    public async Task Same_transfer_in_a_new_turn_is_escalated_as_a_possible_duplicate()
    {
        var h = await Harness.CreateAsync();
        var first = await h.ProposeAsync("mum", "5000");
        var second = await h.ProposeAsync("mum", "5000");

        Assert.Equal(ProposalStatus.Approved, first.Proposal!.Status);
        Assert.Equal(ProposalStatus.AwaitingApproval, second.Proposal!.Status);
        Assert.Contains(second.Proposal.Reasons, r => r.Contains(first.Proposal.Id.ToString()));
    }

    [Fact]
    public async Task Unknown_beneficiary_and_ambiguous_amounts_never_create_a_proposal()
    {
        var h = await Harness.CreateAsync();
        Assert.Contains("No saved beneficiary", (await h.ProposeAsync("0123456789", "5000")).Error);
        Assert.NotNull((await h.ProposeAsync("mum", "5k")).Error);
        Assert.Equal(0, await h.With(db => db.Proposals.CountAsync(p => p.UserId == h.UserId)));
    }

    [Fact]
    public async Task Concurrent_approvals_approve_once()
    {
        var h = await Harness.CreateAsync();
        var p = (await h.ProposeAsync("mum", "50000")).Proposal!;
        Assert.Equal(ProposalStatus.AwaitingApproval, p.Status);
        var token = h.Tokens.Issue(p.Id, p.ContentHash, p.ApprovalExpiresAt);

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(async () =>
        {
            await using var db = h.NewDb();
            return await h.Proposals(db).ApproveAsync(p.Id, h.UserId, token);
        })));

        Assert.Single(outcomes, o => o == ApprovalOutcome.Approved);
        Assert.All(outcomes, o => Assert.True(o is ApprovalOutcome.Approved or ApprovalOutcome.AlreadyApproved));
        Assert.Equal(1, await h.With(db => db.AuditEvents.CountAsync(e => e.ProposalId == p.Id && e.Type == "approved")));
    }

    [Fact]
    public async Task Approval_token_stops_working_if_the_stored_proposal_is_altered()
    {
        var h = await Harness.CreateAsync();
        var p = (await h.ProposeAsync("mum", "50000")).Proposal!;
        var token = h.Tokens.Issue(p.Id, p.ContentHash, p.ApprovalExpiresAt);

        // Someone with database access redirects the approved transfer.
        await h.With(db => db.Proposals.Where(x => x.Id == p.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.AccountNumber, "9999999999")));

        Assert.Equal(ApprovalOutcome.InvalidToken, await h.With(db => h.Proposals(db).ApproveAsync(p.Id, h.UserId, token)));
    }

    [Fact]
    public async Task Another_users_token_cannot_approve()
    {
        var h = await Harness.CreateAsync();
        var p = (await h.ProposeAsync("mum", "50000")).Proposal!;
        var token = h.Tokens.Issue(p.Id, p.ContentHash, p.ApprovalExpiresAt);
        Assert.Equal(ApprovalOutcome.NotFound, await h.With(db => h.Proposals(db).ApproveAsync(p.Id, "someone-else", token)));
    }

    [Fact]
    public async Task Rejection_and_expiry_give_the_reserved_spend_back()
    {
        var h = await Harness.CreateAsync();
        var a = (await h.ProposeAsync("mum", "150000")).Proposal!;
        var b = (await h.ProposeAsync("landlord", "150000")).Proposal!;
        Assert.Equal(ProposalStatus.Denied, (await h.ProposeAsync("mum", "1000")).Proposal!.Status); // cap full

        Assert.Equal(ApprovalOutcome.Rejected, await h.With(db => h.Proposals(db).RejectAsync(a.Id, h.UserId)));
        h.Clock.Advance(ProposalService.ApprovalTtl + TimeSpan.FromSeconds(1));
        Assert.Equal(1, await h.With(db => h.Proposals(db).ExpireStaleApprovalsAsync()));

        Assert.Equal(0, await h.With(db => db.MandateUsage.Where(u => db.Mandates.Any(m => m.Id == u.MandateId && m.UserId == h.UserId))
            .SumAsync(u => u.ReservedMinor)));
        Assert.Equal(ProposalStatus.Expired, (await h.With(db => db.Proposals.FirstAsync(x => x.Id == b.Id))).Status);
    }

    [Fact]
    public async Task Kill_switch_denies_new_proposals()
    {
        var h = await Harness.CreateAsync();
        h.Kill.On = true;
        var r = await h.ProposeAsync("mum", "100");
        Assert.Equal(ProposalStatus.Denied, r.Proposal!.Status);
        Assert.False(r.Proposal.ReservedSpend);
    }

    [Fact]
    public async Task Audit_chain_survives_a_real_round_trip_and_detects_tampering()
    {
        var h = await Harness.CreateAsync();
        var p = (await h.ProposeAsync("mum", "5000")).Proposal!;
        await h.With(db => h.Executor(db).ExecuteAsync(p.Id));

        var events = await h.With(db => db.AuditEvents.Where(e => e.ProposalId == p.Id).OrderBy(e => e.Seq).ToListAsync());
        Assert.Equal(["proposed", "policy_evaluated", "executing", "succeeded"], events.Select(e => e.Type));
        Assert.Null(AuditChain.Verify(events));

        await h.With(db => db.AuditEvents.Where(e => e.ProposalId == p.Id && e.Seq == 0)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.DataJson, "{\"amountMinor\":100}")));
        var tampered = await h.With(db => db.AuditEvents.Where(e => e.ProposalId == p.Id).OrderBy(e => e.Seq).ToListAsync());
        Assert.Equal(0, AuditChain.Verify(tampered));
    }
}
