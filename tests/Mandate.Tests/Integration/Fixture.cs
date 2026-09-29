using Mandate.Core;
using Mandate.Core.Execution;
using Mandate.Core.Persistence;
using Mandate.Core.Policy;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Testcontainers.PostgreSql;

namespace Mandate.Tests.Integration;

/// <summary>
/// One real Postgres for the whole run. The schema is migrated once into a template database
/// and every test gets its own copy (CREATE DATABASE ... TEMPLATE, a file-level copy that takes
/// milliseconds). Isolation matters here because the executor drains every Approved row it can
/// see, so tests running in parallel would otherwise execute each other's proposals.
/// </summary>
public static class Db
{
    private static readonly PostgreSqlContainer Container = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private static readonly Lazy<Task> Ready = new(async () =>
    {
        await Container.StartAsync();
        await Admin("CREATE DATABASE mandate_template");
        await using var db = Create(For("mandate_template") + ";Pooling=false");
        await db.Database.MigrateAsync();
    });

    private static string For(string database) =>
        new Npgsql.NpgsqlConnectionStringBuilder(Container.GetConnectionString()) { Database = database }.ConnectionString;

    private static async Task Admin(string sql)
    {
        await using var conn = new Npgsql.NpgsqlConnection(For("postgres") + ";Pooling=false");
        await conn.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task<string> ConnectionStringAsync()
    {
        await Ready.Value;
        var name = "t_" + Guid.NewGuid().ToString("N");
        await Admin($"CREATE DATABASE {name} TEMPLATE mandate_template");
        return For(name);
    }

    public static MandateDb Create(string cs) =>
        new(new DbContextOptionsBuilder<MandateDb>().UseNpgsql(cs).Options);
}

/// <summary>Hand-wired services with a controllable clock and provider, one DbContext per "request".</summary>
public class Harness
{
    public static readonly byte[] Key = "integration-test-signing-key-0123456789"u8.ToArray();
    public required string Cs { get; init; }
    public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);
    public FakeTransferProvider Provider { get; } = new();
    public KillSwitch Kill { get; } = new();
    public ApprovalTokens Tokens { get; } = new(Key);
    public ExecutorOptions Options { get; } = new() { NotFoundSettleTime = TimeSpan.FromMinutes(2), ExecutionLease = TimeSpan.FromMinutes(2) };
    public string UserId { get; } = "u-" + Guid.NewGuid().ToString("N")[..10];

    public static async Task<Harness> CreateAsync()
    {
        var h = new Harness { Cs = await Db.ConnectionStringAsync() };
        await using var db = h.NewDb();
        await DemoSeed.SeedUserAsync(db, h.UserId, h.Clock.GetUtcNow());
        return h;
    }

    public MandateDb NewDb() => Db.Create(Cs);
    public ProposalService Proposals(MandateDb db) => new(db, Tokens, Kill, Clock);
    public Executor Executor(MandateDb db) => new(db, Proposals(db), Provider, Options, Clock, NullLogger<Executor>.Instance);

    public async Task<ProposeResult> ProposeAsync(string alias, string amount, string? turn = null)
    {
        await using var db = NewDb();
        return await Proposals(db).ProposeAsync(new ProposeRequest(UserId, DemoSeed.AgentId, turn ?? Guid.NewGuid().ToString(),
            Mandate.Core.Domain.ActionKind.Transfer, alias, amount, "test"));
    }

    public async Task<T> With<T>(Func<MandateDb, Task<T>> f)
    {
        await using var db = NewDb();
        return await f(db);
    }
}

public class ApiFactory(string cs) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Mandate", cs);
        builder.UseSetting("Mandate:SigningKey", System.Text.Encoding.UTF8.GetString(Harness.Key));
        builder.UseSetting("Worker:Enabled", "false");
        builder.UseSetting("Database:MigrateOnStartup", "false");
        builder.UseSetting("LLM:ApiKey", "");
        builder.UseSetting("RateLimit:ChatPerMinute", "1000");
        builder.UseSetting("RateLimit:SessionsPer10Min", "1000");
    }
}
