using System.Collections.Concurrent;

namespace Mandate.Core.Execution;

public record TransferRequest(string IdempotencyKey, string BankCode, string AccountNumber, string AccountName, long AmountMinor,
    string Currency, string Narration);

public enum SendStatus
{
    Succeeded,

    /// <summary>Provider definitively refused (insufficient funds, invalid account). Nothing moved.</summary>
    Declined,
}

public record SendResult(SendStatus Status, string? Reference, string? Error);

public enum LookupStatus
{
    Succeeded,
    Failed,
    Pending,
    NotFound,
}

public record LookupResult(LookupStatus Status, string? Reference);

/// <summary>
/// The bank/switch we pay out through. Timeouts and 5xx surface as exceptions, and the
/// executor treats every exception as "outcome unknown", never as "failed".
/// </summary>
public interface ITransferProvider
{
    Task<SendResult> SendAsync(TransferRequest request, CancellationToken ct);
    Task<LookupResult> LookupAsync(string idempotencyKey, CancellationToken ct);
}

public enum Fault
{
    None,

    /// <summary>The transfer is applied, then the response is lost. The dangerous case.</summary>
    TimeoutAfterApply,

    /// <summary>The request never reaches the provider.</summary>
    TimeoutBeforeApply,
    Decline,
}

/// <summary>
/// In-memory stand-in for a payout provider with scriptable failures. Setting
/// <see cref="HonoursIdempotencyKey"/> to false models a provider that treats every request as
/// new, so tests can show that duplicates are prevented on our side and not only by the provider.
/// </summary>
public class FakeTransferProvider : ITransferProvider
{
    private readonly ConcurrentDictionary<string, SendResult> _byKey = new();
    private readonly ConcurrentQueue<Fault> _faults = new();
    private int _sequence;

    public bool HonoursIdempotencyKey { get; set; } = true;
    public TimeSpan Latency { get; set; } = TimeSpan.Zero;
    public ConcurrentBag<TransferRequest> Applied { get; } = [];

    public void Enqueue(params Fault[] faults)
    {
        foreach (var f in faults) _faults.Enqueue(f);
    }

    public int AppliedCount(string idempotencyKey) => Applied.Count(a => a.IdempotencyKey == idempotencyKey);

    public async Task<SendResult> SendAsync(TransferRequest r, CancellationToken ct)
    {
        if (Latency > TimeSpan.Zero) await Task.Delay(Latency, ct);
        var fault = _faults.TryDequeue(out var f) ? f : Fault.None;
        if (fault == Fault.TimeoutBeforeApply) throw new TimeoutException("provider timeout (request not received)");
        if (fault == Fault.Decline) return _byKey[r.IdempotencyKey] = new SendResult(SendStatus.Declined, null, "Insufficient funds");

        if (HonoursIdempotencyKey && _byKey.TryGetValue(r.IdempotencyKey, out var prior)) return prior;

        var result = new SendResult(SendStatus.Succeeded, $"NIP{Interlocked.Increment(ref _sequence):D8}", null);
        Applied.Add(r);
        _byKey[r.IdempotencyKey] = result;

        if (fault == Fault.TimeoutAfterApply) throw new TimeoutException("provider timeout (transfer applied, response lost)");
        return result;
    }

    public Task<LookupResult> LookupAsync(string key, CancellationToken ct) =>
        Task.FromResult(_byKey.TryGetValue(key, out var r)
            ? new LookupResult(r.Status == SendStatus.Succeeded ? LookupStatus.Succeeded : LookupStatus.Failed, r.Reference)
            : new LookupResult(LookupStatus.NotFound, null));
}
