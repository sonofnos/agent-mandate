using Microsoft.EntityFrameworkCore;

namespace Mandate.Core.Persistence;

/// <summary>
/// Daily-limit enforcement as one conditional upsert. Checking "used + amount &lt;= cap" in
/// application code and then writing is a race: two proposals of ₦200k against a ₦300k cap
/// can both read ₦0 used and both pass. Here the check and the write are the same statement,
/// Postgres takes the row lock for ON CONFLICT DO UPDATE and re-evaluates the WHERE against
/// the latest committed value, so the cap holds under any concurrency.
/// </summary>
public static class SpendReservations
{
    public static async Task<bool> TryReserveAsync(MandateDb db, Guid mandateId, DateOnly day, long amountMinor, long capMinor,
        CancellationToken ct)
    {
        if (amountMinor > capMinor) return false;
        var rows = await db.Database.SqlQuery<long>($"""
            INSERT INTO mandate_usage ("MandateId", "Day", "ReservedMinor")
            VALUES ({mandateId}, {day}, {amountMinor})
            ON CONFLICT ("MandateId", "Day") DO UPDATE
               SET "ReservedMinor" = mandate_usage."ReservedMinor" + EXCLUDED."ReservedMinor"
             WHERE mandate_usage."ReservedMinor" + EXCLUDED."ReservedMinor" <= {capMinor}
            RETURNING "ReservedMinor" AS "Value"
            """).ToListAsync(ct);
        return rows.Count == 1;
    }

    public static Task ReleaseAsync(MandateDb db, Guid mandateId, DateOnly day, long amountMinor, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync($"""
            UPDATE mandate_usage SET "ReservedMinor" = "ReservedMinor" - {amountMinor}
             WHERE "MandateId" = {mandateId} AND "Day" = {day}
            """, ct);

    public static async Task<long> ReservedAsync(MandateDb db, Guid mandateId, DateOnly day, CancellationToken ct) =>
        await db.MandateUsage.Where(u => u.MandateId == mandateId && u.Day == day)
            .Select(u => (long?)u.ReservedMinor).FirstOrDefaultAsync(ct) ?? 0;

    /// <summary>Limits reset at Lagos midnight. WAT is UTC+1 with no daylight saving.</summary>
    public static DateOnly LagosDay(DateTimeOffset t) => DateOnly.FromDateTime(t.ToOffset(TimeSpan.FromHours(1)).DateTime);
}
