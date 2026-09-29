using Mandate.Core.Audit;
using Mandate.Core.Domain;
using Mandate.Core.Policy;

namespace Mandate.Tests.Unit;

public class MoneyTests
{
    [Theory]
    [InlineData("5000", 500_000)]
    [InlineData("5,000", 500_000)]
    [InlineData("₦5,000.5", 500_050)]
    [InlineData("NGN 1200.75", 120_075)]
    [InlineData(" 20000 ", 2_000_000)]
    public void Parses_plain_amounts(string input, long expected)
    {
        Assert.True(Money.TryParseMajor(input, out var minor, out _));
        Assert.Equal(expected, minor);
    }

    [Theory]
    [InlineData("5k")]
    [InlineData("five thousand")]
    [InlineData("-5000")]
    [InlineData("0")]
    [InlineData("5000.123")]
    [InlineData("50,00")]
    [InlineData("1e6")]
    [InlineData("")]
    [InlineData("99999999999999")]
    public void Rejects_anything_ambiguous(string input) => Assert.False(Money.TryParseMajor(input, out _, out _));
}

public class PolicyEngineTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private static AgentMandate Mandate() => new()
    {
        UserId = "u1", AgentId = "a", AllowedActions = [ActionKind.Transfer], PerActionLimitMinor = 200_000_00,
        DailyLimitMinor = 300_000_00, AutoApproveLimitMinor = 20_000_00, ExpiresAt = Now.AddDays(30),
    };

    private static Beneficiary Established() => new() { UserId = "u1", Alias = "mum", CreatedAt = Now.AddDays(-30) };

    private static PolicyDecision Eval(long amount, AgentMandate? m = null, Beneficiary? b = null, bool kill = false, Guid? dup = null,
        ActionKind kind = ActionKind.Transfer, string currency = "NGN") =>
        PolicyEngine.Evaluate(new PolicyInput(m ?? Mandate(), kind, b ?? Established(), amount, currency, Now, kill, dup));

    [Fact]
    public void Small_transfer_to_established_beneficiary_auto_approves() =>
        Assert.Equal(PolicyOutcome.AutoApprove, Eval(5_000_00).Outcome);

    [Fact]
    public void Exactly_at_auto_approve_limit_still_auto_approves() =>
        Assert.Equal(PolicyOutcome.AutoApprove, Eval(20_000_00).Outcome);

    [Fact]
    public void Above_auto_approve_limit_needs_a_human() => Assert.Equal(PolicyOutcome.RequireApproval, Eval(20_000_01).Outcome);

    [Fact]
    public void Above_per_action_limit_is_denied_not_escalated() => Assert.Equal(PolicyOutcome.Deny, Eval(200_000_01).Outcome);

    [Fact]
    public void New_beneficiary_needs_approval_even_for_small_amounts()
    {
        var d = Eval(1_000_00, b: new Beneficiary { UserId = "u1", CreatedAt = Now.AddHours(-2) });
        Assert.Equal(PolicyOutcome.RequireApproval, d.Outcome);
        Assert.Contains(d.Reasons, r => r.Contains("recently"));
    }

    [Fact]
    public void Recent_duplicate_needs_approval() =>
        Assert.Equal(PolicyOutcome.RequireApproval, Eval(1_000_00, dup: Guid.NewGuid()).Outcome);

    [Fact]
    public void Kill_switch_denies_everything() => Assert.Equal(PolicyOutcome.Deny, Eval(100, kill: true).Outcome);

    [Fact]
    public void Revoked_or_expired_mandate_denies()
    {
        var revoked = Mandate();
        revoked.RevokedAt = Now.AddMinutes(-1);
        var expired = Mandate();
        expired.ExpiresAt = Now;
        Assert.Equal(PolicyOutcome.Deny, Eval(100, revoked).Outcome);
        Assert.Equal(PolicyOutcome.Deny, Eval(100, expired).Outcome);
    }

    [Fact]
    public void Action_not_in_mandate_is_denied() => Assert.Equal(PolicyOutcome.Deny, Eval(100, kind: ActionKind.BillPayment).Outcome);

    [Fact]
    public void Wrong_currency_is_denied() => Assert.Equal(PolicyOutcome.Deny, Eval(100, currency: "USD").Outcome);

    [Fact]
    public void Another_users_beneficiary_is_denied() =>
        Assert.Equal(PolicyOutcome.Deny, Eval(100, b: new Beneficiary { UserId = "u2", CreatedAt = Now.AddDays(-30) }).Outcome);
}

public class ApprovalTokenTests
{
    private readonly ApprovalTokens _tokens = new("0123456789abcdef0123456789abcdef"u8.ToArray());
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public void Token_approves_only_the_exact_content_it_was_issued_for()
    {
        var id = Guid.NewGuid();
        var token = _tokens.Issue(id, "hash-a", Now.AddMinutes(5));
        Assert.True(_tokens.Verify(token, id, "hash-a", Now));
        Assert.False(_tokens.Verify(token, id, "hash-b", Now)); // amount or beneficiary changed
        Assert.False(_tokens.Verify(token, Guid.NewGuid(), "hash-a", Now)); // different proposal
        Assert.False(_tokens.Verify(token, id, "hash-a", Now.AddMinutes(6))); // expired
    }

    [Fact]
    public void Extending_the_expiry_invalidates_the_signature()
    {
        var id = Guid.NewGuid();
        var token = _tokens.Issue(id, "h", Now.AddMinutes(1));
        var forged = Now.AddDays(1).ToUnixTimeSeconds() + token[token.IndexOf('.')..];
        Assert.False(_tokens.Verify(forged, id, "h", Now));
    }

    [Fact]
    public void Content_hash_covers_amount_and_account()
    {
        var p = new ActionProposal { AmountMinor = 100, AccountNumber = "0123456789" };
        var h = p.ComputeContentHash();
        p.AmountMinor = 101;
        Assert.NotEqual(h, p.ComputeContentHash());
        p.AmountMinor = 100;
        p.AccountNumber = "0123456780";
        Assert.NotEqual(h, p.ComputeContentHash());
    }
}

public class AuditChainTests
{
    private static List<AuditEvent> Chain(int n)
    {
        var id = Guid.NewGuid();
        var prev = AuditChain.Genesis;
        var list = new List<AuditEvent>();
        for (var i = 0; i < n; i++)
        {
            var e = new AuditEvent { ProposalId = id, Seq = i, Type = "t" + i, Actor = "x", DataJson = "{}", At = DateTimeOffset.UnixEpoch, PrevHash = prev };
            e.Hash = AuditChain.ComputeHash(e);
            prev = e.Hash;
            list.Add(e);
        }
        return list;
    }

    [Fact]
    public void Intact_chain_verifies() => Assert.Null(AuditChain.Verify(Chain(5)));

    [Fact]
    public void Editing_an_event_is_detected()
    {
        var c = Chain(5);
        c[2].DataJson = "{\"amount\":1}";
        Assert.Equal(2, AuditChain.Verify(c));
    }

    [Fact]
    public void Deleting_an_event_is_detected()
    {
        var c = Chain(5);
        c.RemoveAt(1);
        Assert.Equal(2, AuditChain.Verify(c));
    }
}

public class TransitionTests
{
    [Theory]
    [InlineData(ProposalStatus.AwaitingApproval, ProposalStatus.Approved, true)]
    [InlineData(ProposalStatus.Approved, ProposalStatus.Executing, true)]
    [InlineData(ProposalStatus.Executing, ProposalStatus.Unknown, true)]
    [InlineData(ProposalStatus.Unknown, ProposalStatus.Approved, true)]
    [InlineData(ProposalStatus.Denied, ProposalStatus.Approved, false)]
    [InlineData(ProposalStatus.AwaitingApproval, ProposalStatus.Executing, false)]
    [InlineData(ProposalStatus.Executing, ProposalStatus.Approved, false)]
    [InlineData(ProposalStatus.Succeeded, ProposalStatus.Failed, false)]
    [InlineData(ProposalStatus.Failed, ProposalStatus.Approved, false)]
    public void Only_listed_transitions_are_allowed(ProposalStatus from, ProposalStatus to, bool allowed) =>
        Assert.Equal(allowed, ProposalTransitions.IsAllowed(from, to));
}
