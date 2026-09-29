using Mandate.Core.Domain;

namespace Mandate.Core.Policy;

public enum PolicyOutcome
{
    Deny,
    RequireApproval,
    AutoApprove,
}

public record PolicyDecision(PolicyOutcome Outcome, IReadOnlyList<string> Reasons)
{
    public static PolicyDecision Deny(params string[] reasons) => new(PolicyOutcome.Deny, reasons);
}

public record PolicyInput(
    AgentMandate Mandate,
    ActionKind Kind,
    Beneficiary Beneficiary,
    long AmountMinor,
    string Currency,
    DateTimeOffset Now,
    bool KillSwitchOn,
    Guid? RecentDuplicateOf);

/// <summary>
/// Pure, deterministic rules. No I/O, no model involvement, so every branch is unit tested.
/// The daily limit is not checked here: it depends on concurrent spend and is enforced by an
/// atomic reservation in the database (see <c>SpendReservations</c>).
/// </summary>
public static class PolicyEngine
{
    public static PolicyDecision Evaluate(PolicyInput i)
    {
        var m = i.Mandate;
        if (i.KillSwitchOn) return PolicyDecision.Deny("Agent actions are paused by operations.");
        if (m.RevokedAt is not null) return PolicyDecision.Deny("The mandate has been revoked.");
        if (m.ExpiresAt <= i.Now) return PolicyDecision.Deny("The mandate has expired.");
        if (!m.AllowedActions.Contains(i.Kind)) return PolicyDecision.Deny($"The mandate does not allow {i.Kind}.");
        if (!string.Equals(m.Currency, i.Currency, StringComparison.Ordinal))
            return PolicyDecision.Deny($"The mandate only covers {m.Currency}.");
        if (i.Beneficiary.UserId != m.UserId) return PolicyDecision.Deny("Beneficiary does not belong to this user.");
        if (i.AmountMinor <= 0) return PolicyDecision.Deny("Amount must be positive.");
        if (i.AmountMinor > m.PerActionLimitMinor)
            return PolicyDecision.Deny(
                $"{Money.Format(i.AmountMinor)} is above the per-transfer limit of {Money.Format(m.PerActionLimitMinor)}.");
        if (i.AmountMinor > m.DailyLimitMinor)
            return PolicyDecision.Deny($"{Money.Format(i.AmountMinor)} is above the daily limit of {Money.Format(m.DailyLimitMinor)}.");

        var approvalReasons = new List<string>();
        if (i.AmountMinor > m.AutoApproveLimitMinor)
            approvalReasons.Add($"Above the auto-approve limit of {Money.Format(m.AutoApproveLimitMinor)}.");
        if (i.Now - i.Beneficiary.CreatedAt < m.NewBeneficiaryCoolOff)
            approvalReasons.Add("Beneficiary was added recently.");
        if (i.RecentDuplicateOf is { } dup)
            approvalReasons.Add($"Same transfer was proposed a few minutes ago ({dup}).");

        return approvalReasons.Count > 0
            ? new PolicyDecision(PolicyOutcome.RequireApproval, approvalReasons)
            : new PolicyDecision(PolicyOutcome.AutoApprove, ["Within auto-approve limit to an established beneficiary."]);
    }
}
