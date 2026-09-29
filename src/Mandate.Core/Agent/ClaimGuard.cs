using System.Text.RegularExpressions;
using Mandate.Core.Domain;

namespace Mandate.Core.Agent;

/// <summary>
/// The model is told never to say money was sent before the ledger says Succeeded. On the live
/// deployment it still replied "Approved and sent" for a transfer that was only queued. This is
/// the deterministic backstop: if the reply claims money moved and any proposal from this turn
/// has not succeeded, the ledger's status is appended to the reply.
/// </summary>
public static partial class ClaimGuard
{
    [GeneratedRegex(@"\b(sent|transferred|paid)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Claim();

    [GeneratedRegex(@"(\bnot\b|n't\b|\bonce\b|\buntil\b|\bto be\b|\bwill be\b)[^.;!?\n]{0,40}?\b(sent|transferred|paid)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Negated();

    public static bool ClaimsMoneyMoved(string reply) => Claim().IsMatch(reply) && !Negated().IsMatch(reply);

    public static string? Correction(string reply, IReadOnlyList<ActionProposal> turnProposals)
    {
        if (!ClaimsMoneyMoved(reply)) return null;
        var unconfirmed = turnProposals.Where(p => p.Status != ProposalStatus.Succeeded).ToList();
        if (unconfirmed.Count == 0) return null;
        return "Ledger status: " + string.Join("; ", unconfirmed.Select(p => $"{Money.Format(p.AmountMinor, p.Currency)} to {p.BeneficiaryName} is " + p.Status switch
        {
            ProposalStatus.Approved or ProposalStatus.Executing => "approved and still processing, not confirmed yet",
            ProposalStatus.AwaitingApproval => "waiting for your approval in the app",
            ProposalStatus.Unknown => "being confirmed with the bank",
            var s => s.ToString().ToLowerInvariant(),
        })) + ".";
    }
}
