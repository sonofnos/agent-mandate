namespace Mandate.Core.Domain;

public enum ActionKind
{
    Transfer,
    BillPayment,
}

public class Beneficiary
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserId { get; set; } = "";

    /// <summary>What the user calls them ("mum", "landlord"). The agent can only refer to beneficiaries by alias.</summary>
    public string Alias { get; set; } = "";

    /// <summary>Account name returned by name enquiry. Shown to the user at approval time.</summary>
    public string AccountName { get; set; } = "";
    public string BankCode { get; set; } = "";
    public string AccountNumber { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A grant from a user to an agent: which actions it may propose, in which currency,
/// and within which limits. Revocation and expiry are checked on every proposal.
/// </summary>
public class AgentMandate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserId { get; set; } = "";
    public string AgentId { get; set; } = "";
    public List<ActionKind> AllowedActions { get; set; } = [];
    public string Currency { get; set; } = "NGN";

    /// <summary>Hard ceiling for a single action. Above this the proposal is denied outright.</summary>
    public long PerActionLimitMinor { get; set; }

    /// <summary>Ceiling on the sum of pending and executed actions per calendar day (Africa/Lagos).</summary>
    public long DailyLimitMinor { get; set; }

    /// <summary>At or below this amount an action to an established beneficiary runs without a human tap.</summary>
    public long AutoApproveLimitMinor { get; set; }

    /// <summary>Beneficiaries added more recently than this always need approval (account-takeover pattern).</summary>
    public TimeSpan NewBeneficiaryCoolOff { get; set; } = TimeSpan.FromHours(24);

    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

/// <summary>Spend already reserved against a mandate for one day. Updated with a single conditional upsert.</summary>
public class MandateUsage
{
    public Guid MandateId { get; set; }
    public DateOnly Day { get; set; }
    public long ReservedMinor { get; set; }
}

/// <summary>A record of one agent turn: model, prompt version, tool calls and token usage.</summary>
public class AgentRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserId { get; set; } = "";
    public string TurnId { get; set; } = "";
    public string Model { get; set; } = "";
    public string PromptVersion { get; set; } = "";
    public string Input { get; set; } = "";
    public string Output { get; set; } = "";
    public string ToolCallsJson { get; set; } = "[]";
    public long? InputTokens { get; set; }
    public long? OutputTokens { get; set; }
    public int LatencyMs { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Seeded history the agent can read. Narrations come from counterparties, so they are untrusted text.</summary>
public class LedgerEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserId { get; set; } = "";
    public long AmountMinor { get; set; }
    public string Direction { get; set; } = "credit";
    public string Counterparty { get; set; } = "";
    public string Narration { get; set; } = "";
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}
