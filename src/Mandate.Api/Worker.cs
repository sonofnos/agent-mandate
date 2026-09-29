using Mandate.Core;
using Mandate.Core.Execution;

namespace Mandate.Api;

/// <summary>
/// Polls for work. Safe to run on several instances at once: every step is a compare-and-set
/// on proposal status, so instances race harmlessly instead of double-executing.
/// </summary>
public class Worker(IServiceScopeFactory scopes, ILogger<Worker> log, IConfiguration config) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMilliseconds(config.GetValue("Worker:IntervalMs", 1000));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var proposals = scope.ServiceProvider.GetRequiredService<ProposalService>();
                var executor = scope.ServiceProvider.GetRequiredService<Executor>();
                await proposals.ExpireStaleApprovalsAsync(stoppingToken);
                await executor.DrainAsync(stoppingToken);
                await executor.ReconcileAsync(stoppingToken);
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                log.LogError(e, "Worker iteration failed");
            }
            await Task.Delay(interval, stoppingToken);
        }
    }
}
