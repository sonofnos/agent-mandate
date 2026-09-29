using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using Mandate.Api;
using Mandate.Core;
using Mandate.Core.Agent;
using Mandate.Core.Audit;
using Mandate.Core.Domain;
using Mandate.Core.Execution;
using Mandate.Core.Persistence;
using Mandate.Core.Policy;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

var signingKey = config["Mandate:SigningKey"];
if (string.IsNullOrEmpty(signingKey) || signingKey.Length < 32)
{
    if (!builder.Environment.IsDevelopment() && !config.GetValue<bool>("Mandate:AllowDevKey"))
        throw new InvalidOperationException("Mandate:SigningKey must be set to at least 32 characters.");
    signingKey = "dev-only-signing-key-change-me-0123456789";
}
var keyBytes = Encoding.UTF8.GetBytes(signingKey);

builder.Services.AddDbContext<MandateDb>(o => o.UseNpgsql(config.GetConnectionString("Mandate")));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(new ApprovalTokens(keyBytes));
builder.Services.AddSingleton(new TokenService(keyBytes));
builder.Services.AddSingleton(new KillSwitch { On = config.GetValue<bool>("Mandate:KillSwitch") });
builder.Services.AddSingleton(new ExecutorOptions
{
    NotFoundSettleTime = TimeSpan.FromSeconds(config.GetValue("Executor:NotFoundSettleSeconds", 120)),
    ExecutionLease = TimeSpan.FromSeconds(config.GetValue("Executor:LeaseSeconds", 120)),
});
builder.Services.AddSingleton<FakeTransferProvider>(_ => new FakeTransferProvider
{
    Latency = TimeSpan.FromMilliseconds(config.GetValue("Provider:LatencyMs", 0)),
});
builder.Services.AddSingleton<ITransferProvider>(sp => sp.GetRequiredService<FakeTransferProvider>());
builder.Services.AddScoped<ProposalService>();
builder.Services.AddScoped<Executor>();

var (chatClient, modelInfo) = ModelClients.Create(config);
builder.Services.AddSingleton(chatClient);
builder.Services.AddSingleton(modelInfo);
builder.Services.AddScoped<AgentRunner>();

builder.Services.AddAuthentication(TokenAuthHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, TokenAuthHandler>(TokenAuthHandler.SchemeName, null);
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Roles.User, p => p.RequireRole(Roles.User))
    .AddPolicy(Roles.Agent, p => p.RequireRole(Roles.Agent))
    .AddPolicy(Roles.Ops, p => p.RequireRole(Roles.Ops));

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddPolicy("chat", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "anon",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = config.GetValue("RateLimit:ChatPerMinute", 12), Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy("session", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Request.Headers["CF-Connecting-IP"].FirstOrDefault() ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "anon",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = config.GetValue("RateLimit:SessionsPer10Min", 10), Window = TimeSpan.FromMinutes(10) }));
});

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddHttpContextAccessor();
builder.Services.AddMcpServer().WithHttpTransport(o => o.Stateless = true).WithTools<McpTools>();

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("agent-mandate"))
    .WithTracing(t =>
    {
        t.AddSource("AgentMandate.*").AddAspNetCoreInstrumentation();
        if (!string.IsNullOrEmpty(config["OTEL_EXPORTER_OTLP_ENDPOINT"])) t.AddOtlpExporter();
    })
    .WithMetrics(m =>
    {
        m.AddMeter("Microsoft.Extensions.AI", "AgentMandate.*").AddAspNetCoreInstrumentation();
        if (!string.IsNullOrEmpty(config["OTEL_EXPORTER_OTLP_ENDPOINT"])) m.AddOtlpExporter();
    });

if (config.GetValue("Worker:Enabled", true)) builder.Services.AddHostedService<Worker>();

var app = builder.Build();

if (config.GetValue("Database:MigrateOnStartup", true))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<MandateDb>().Database.MigrateAsync();
}

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapGet("/health", () => Results.Ok(new { status = "ok", model = modelInfo.Model }));

// Demo only: mints an isolated user with seeded beneficiaries, mandate and transactions.
app.MapPost("/demo/session", async (MandateDb db, TokenService tokens, TimeProvider clock, CancellationToken ct) =>
{
    var userId = "demo-" + Guid.NewGuid().ToString("N")[..10];
    await DemoSeed.SeedUserAsync(db, userId, clock.GetUtcNow(), ct);
    return Results.Ok(new { userId, userToken = tokens.Issue(Roles.User, userId), agentToken = tokens.Issue(Roles.Agent, userId) });
}).RequireRateLimiting("session");

// Demo only: makes the next provider call fail in a chosen way, to show reconciliation.
app.MapPost("/demo/next-provider-fault", (FaultRequest r, FakeTransferProvider provider) =>
{
    provider.Enqueue(r.Fault);
    return Results.Ok(new { queued = r.Fault.ToString() });
}).RequireAuthorization(Roles.User);

app.MapPost("/agent/chat", async (ChatRequest r, ClaimsPrincipal user, AgentRunner runner, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(r.Message) || r.Message.Length > 2000) return Results.BadRequest(new { error = "message must be 1-2000 chars" });
    return Results.Ok(await runner.RunAsync(user.UserId(), DemoSeed.AgentId, r.Message, r.TurnId, r.History, ct));
}).RequireAuthorization(Roles.Agent).RequireRateLimiting("chat");

var me = app.MapGroup("/me").RequireAuthorization(Roles.User);

me.MapGet("/proposals", async (ClaimsPrincipal user, MandateDb db, ProposalService svc, CancellationToken ct) =>
{
    var list = await db.Proposals.AsNoTracking().Where(p => p.UserId == user.UserId())
        .OrderByDescending(p => p.CreatedAt).Take(30).ToListAsync(ct);
    return list.Select(p => ProposalView.From(p, p.Status == ProposalStatus.AwaitingApproval ? svc.ApprovalTokenFor(p) : null));
});

me.MapPost("/proposals/{id:guid}/approve", async (Guid id, ApproveRequest r, ClaimsPrincipal user, ProposalService svc, CancellationToken ct) =>
    await svc.ApproveAsync(id, user.UserId(), r.Token, ct) switch
    {
        ApprovalOutcome.Approved => Results.Ok(new { status = "Approved" }),
        ApprovalOutcome.AlreadyApproved => Results.Ok(new { status = "Approved", replay = true }),
        ApprovalOutcome.NotFound => Results.NotFound(),
        ApprovalOutcome.InvalidToken => Results.Json(new { error = "approval token invalid for this proposal" }, statusCode: 403),
        var o => Results.Conflict(new { error = o.ToString() }),
    });

me.MapPost("/proposals/{id:guid}/reject", async (Guid id, ClaimsPrincipal user, ProposalService svc, CancellationToken ct) =>
    await svc.RejectAsync(id, user.UserId(), ct) switch
    {
        ApprovalOutcome.Rejected => Results.Ok(new { status = "Rejected" }),
        ApprovalOutcome.NotFound => Results.NotFound(),
        var o => Results.Conflict(new { error = o.ToString() }),
    });

me.MapGet("/proposals/{id:guid}/audit", async (Guid id, ClaimsPrincipal user, MandateDb db, CancellationToken ct) =>
{
    if (!await db.Proposals.AnyAsync(p => p.Id == id && p.UserId == user.UserId(), ct)) return Results.NotFound();
    var events = await db.AuditEvents.AsNoTracking().Where(e => e.ProposalId == id).OrderBy(e => e.Seq).ToListAsync(ct);
    return Results.Ok(new
    {
        chainIntact = AuditChain.Verify(events) is null,
        events = events.Select(e => new { e.Seq, e.Type, e.Actor, e.At, data = System.Text.Json.JsonDocument.Parse(e.DataJson).RootElement, hash = e.Hash[..12] }),
    });
});

me.MapGet("/runs/{id:guid}", async (Guid id, ClaimsPrincipal user, MandateDb db, CancellationToken ct) =>
    await db.AgentRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id && r.UserId == user.UserId(), ct) is { } run
        ? Results.Ok(run)
        : Results.NotFound()).RequireAuthorization(Roles.User);

app.MapPost("/ops/kill-switch", (KillSwitchRequest r, KillSwitch ks) =>
{
    ks.On = r.On;
    return Results.Ok(new { killSwitch = ks.On });
}).RequireAuthorization(Roles.Ops);

app.MapMcp("/mcp").RequireAuthorization(Roles.Agent);

app.Run();

public record ChatRequest(string Message, string? TurnId, List<ChatTurn>? History);
public record ApproveRequest(string? Token);
public record KillSwitchRequest(bool On);
public record FaultRequest(Fault Fault);

public record ProposalView(Guid Id, string Status, string Amount, long AmountMinor, string To, string Bank, string Account, string Narration,
    List<string> Reasons, string? ApprovalToken, DateTimeOffset? ApprovalExpiresAt, string? ProviderReference, int Attempts, string? LastError,
    Guid? AgentRunId, DateTimeOffset CreatedAt)
{
    public static ProposalView From(ActionProposal p, string? token) => new(p.Id, p.Status.ToString(), Money.Format(p.AmountMinor, p.Currency),
        p.AmountMinor, p.BeneficiaryName, p.BankCode, "******" + p.AccountNumber[^4..], p.Narration, p.Reasons, token,
        token is null ? null : p.ApprovalExpiresAt, p.ProviderReference, p.Attempts, p.LastError, p.AgentRunId, p.CreatedAt);
}

public partial class Program;
