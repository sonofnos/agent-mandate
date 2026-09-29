using Mandate.Core.Audit;
using Mandate.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mandate.Core.Persistence;

public class MandateDb(DbContextOptions<MandateDb> options) : DbContext(options)
{
    public DbSet<Beneficiary> Beneficiaries => Set<Beneficiary>();
    public DbSet<AgentMandate> Mandates => Set<AgentMandate>();
    public DbSet<MandateUsage> MandateUsage => Set<MandateUsage>();
    public DbSet<ActionProposal> Proposals => Set<ActionProposal>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<AgentRun> AgentRuns => Set<AgentRun>();
    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Beneficiary>(e =>
        {
            e.ToTable("beneficiaries");
            e.HasIndex(x => new { x.UserId, x.Alias }).IsUnique();
        });

        b.Entity<AgentMandate>(e =>
        {
            e.ToTable("mandates");
            e.HasIndex(x => new { x.UserId, x.AgentId });
            e.Property(x => x.AllowedActions).HasConversion(
                v => v.Select(a => a.ToString()).ToArray(),
                v => v.Select(Enum.Parse<ActionKind>).ToList(),
                new Microsoft.EntityFrameworkCore.ChangeTracking.ValueComparer<List<ActionKind>>(
                    (a, c) => a!.SequenceEqual(c!), v => v.Aggregate(0, (h, x) => HashCode.Combine(h, x)), v => v.ToList()));
        });

        b.Entity<MandateUsage>(e =>
        {
            e.ToTable("mandate_usage");
            e.HasKey(x => new { x.MandateId, x.Day });
            e.ToTable(t => t.HasCheckConstraint("ck_usage_non_negative", "\"ReservedMinor\" >= 0"));
        });

        b.Entity<ActionProposal>(e =>
        {
            e.ToTable("proposals");
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(32);
            // The de-duplication guarantee lives here, not in application code:
            // a replayed turn or a model that calls the tool twice collides on this index.
            e.HasIndex(x => new { x.UserId, x.TurnId, x.Fingerprint }).IsUnique();
            e.HasIndex(x => new { x.Status, x.UpdatedAt });
            e.HasIndex(x => new { x.UserId, x.Fingerprint, x.CreatedAt });
            e.Ignore(x => x.IdempotencyKey);
            e.ToTable(t => t.HasCheckConstraint("ck_proposal_amount_positive", "\"AmountMinor\" > 0"));
        });

        b.Entity<AuditEvent>(e =>
        {
            e.ToTable("audit_events");
            e.HasIndex(x => new { x.ProposalId, x.Seq }).IsUnique();
            // text, not jsonb: jsonb re-orders keys and strips whitespace, which would break the hash on read-back.
            e.Property(x => x.DataJson).HasColumnType("text");
        });

        b.Entity<AgentRun>(e =>
        {
            e.ToTable("agent_runs");
            e.HasIndex(x => new { x.UserId, x.CreatedAt });
            e.Property(x => x.ToolCallsJson).HasColumnType("jsonb");
        });

        b.Entity<LedgerEntry>(e =>
        {
            e.ToTable("ledger_entries");
            e.HasIndex(x => new { x.UserId, x.At });
        });
    }
}
