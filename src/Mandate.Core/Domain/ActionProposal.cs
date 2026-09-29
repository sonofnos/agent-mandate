using System.Security.Cryptography;
using System.Text;

namespace Mandate.Core.Domain;

public enum ProposalStatus
{
    Denied,
    AwaitingApproval,
    Approved,
    Executing,
    Succeeded,
    Failed,

    /// <summary>The provider call timed out or errored ambiguously. Money may or may not have moved; only reconciliation may decide.</summary>
    Unknown,
    Rejected,
    Expired,
}

/// <summary>
/// What the model asked for, after the server resolved it against real data. The model
/// never produces one of these directly: it names a beneficiary alias and an amount, and
/// the server fills in the account, the mandate and the policy decision.
/// </summary>
public class ActionProposal
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserId { get; set; } = "";
    public string AgentId { get; set; } = "";
    public Guid MandateId { get; set; }
    public ActionKind Kind { get; set; }
    public Guid BeneficiaryId { get; set; }
    public string BeneficiaryName { get; set; } = "";
    public string BankCode { get; set; } = "";
    public string AccountNumber { get; set; } = "";
    public long AmountMinor { get; set; }
    public string Currency { get; set; } = "NGN";
    public string Narration { get; set; } = "";

    /// <summary>Scope for de-duplication: one user turn (or one MCP call's idempotency key).</summary>
    public string TurnId { get; set; } = "";

    /// <summary>Hash of what is being done, excluding narration. Same turn + same fingerprint = same proposal.</summary>
    public string Fingerprint { get; set; } = "";

    /// <summary>Hash of every field the user approves. An approval token is bound to this.</summary>
    public string ContentHash { get; set; } = "";

    public ProposalStatus Status { get; set; }
    public List<string> Reasons { get; set; } = [];
    public bool ReservedSpend { get; set; }
    public DateOnly SpendDay { get; set; }

    public string? ProviderReference { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public DateTimeOffset? ExecutionStartedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ApprovalExpiresAt { get; set; }

    public Guid? AgentRunId { get; set; }
    public string? TraceId { get; set; }

    /// <summary>Provider idempotency key. Stable for the life of the proposal, so every retry is the same request.</summary>
    public string IdempotencyKey => $"am-{Id:N}";

    public static string ComputeFingerprint(ActionKind kind, string userId, Guid beneficiaryId, long amountMinor, string currency) =>
        Sha256($"{kind}|{userId}|{beneficiaryId}|{amountMinor}|{currency}");

    public string ComputeContentHash() =>
        Sha256($"{Id}|{Kind}|{UserId}|{BeneficiaryId}|{BankCode}|{AccountNumber}|{AmountMinor}|{Currency}|{Narration}");

    private static string Sha256(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
}

public static class ProposalTransitions
{
    private static readonly Dictionary<ProposalStatus, ProposalStatus[]> Allowed = new()
    {
        [ProposalStatus.AwaitingApproval] = [ProposalStatus.Approved, ProposalStatus.Rejected, ProposalStatus.Expired],
        [ProposalStatus.Approved] = [ProposalStatus.Executing],
        [ProposalStatus.Executing] = [ProposalStatus.Succeeded, ProposalStatus.Failed, ProposalStatus.Unknown],
        // Unknown -> Approved only after the provider has confirmed it never received the request.
        [ProposalStatus.Unknown] = [ProposalStatus.Succeeded, ProposalStatus.Failed, ProposalStatus.Approved],
    };

    public static bool IsAllowed(ProposalStatus from, ProposalStatus to) =>
        Allowed.TryGetValue(from, out var next) && next.Contains(to);

    public static bool IsTerminal(ProposalStatus s) => !Allowed.ContainsKey(s);

    /// <summary>Statuses whose reserved daily spend goes back to the mandate.</summary>
    public static bool ReleasesSpend(ProposalStatus s) =>
        s is ProposalStatus.Failed or ProposalStatus.Rejected or ProposalStatus.Expired;
}

public class InvalidTransitionException(ProposalStatus from, ProposalStatus to)
    : InvalidOperationException($"Proposal cannot move from {from} to {to}.");
