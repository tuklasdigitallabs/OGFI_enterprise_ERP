using Ogfi.BuildingBlocks.Messaging.Contracts;
using Ogfi.BuildingBlocks.Multitenancy;
using Ogfi.Modules.DurableOperations;

namespace Ogfi.Workers;

public sealed class ReplayWorkerOptions
{
    public TimeSpan LeaseRenewalInterval { get; set; } = TimeSpan.FromMinutes(1);
    public TimeSpan LeaseDuration { get; set; } = DurableOperationService.DefaultAttemptLease;
    public int BatchSize { get; set; } = 25;
}

public interface IReplayLeaseRenewalObserver
{
    Task RenewedAsync(Guid tenantId, Guid attemptId, long attemptVersion, CancellationToken cancellationToken);
}

public sealed class NoopReplayLeaseRenewalObserver : IReplayLeaseRenewalObserver
{
    public Task RenewedAsync(Guid tenantId, Guid attemptId, long attemptVersion, CancellationToken cancellationToken)
        => Task.CompletedTask;
}

public sealed class ReplayLeaseRenewalRunner(
    IServiceScopeFactory scopeFactory,
    ReplayWorkerOptions options,
    IReplayLeaseRenewalObserver observer) : IReplayLeaseRenewalRunner
{
    public async Task<ReplayDispatchResult> RunAsync(
        Guid tenantId,
        OperationAttempt attempt,
        string workerCode,
        Func<CancellationToken, Task<ReplayDispatchResult>> ownerAction,
        CancellationToken cancellationToken)
    {
        using var leaseLost = new CancellationTokenSource();
        using var ownerCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, leaseLost.Token);
        var ownerTask = ownerAction(ownerCancellation.Token);
        var renewalTask = RenewUntilCompletedAsync(
            tenantId, attempt, workerCode, ownerTask, leaseLost, cancellationToken);
        try
        {
            var result = await ownerTask;
            await renewalTask;
            return result;
        }
        catch (OperationCanceledException) when (leaseLost.IsCancellationRequested
                                                  && !cancellationToken.IsCancellationRequested)
        {
            throw new DurableOperationRuleException(
                "OPERATIONS.ATTEMPT.LEASE_LOST", "Attempt lease was lost during owner execution.");
        }
    }

    private async Task RenewUntilCompletedAsync(
        Guid tenantId,
        OperationAttempt attempt,
        string workerCode,
        Task ownerTask,
        CancellationTokenSource leaseLost,
        CancellationToken hostCancellation)
    {
        while (!ownerTask.IsCompleted)
        {
            try
            {
                await Task.Delay(options.LeaseRenewalInterval, hostCancellation);
                if (ownerTask.IsCompleted) return;
                await using var scope = scopeFactory.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<ITenantExecutionContextAccessor>()
                    .SetCandidateTenant(tenantId);
                var renewed = await scope.ServiceProvider.GetRequiredService<DurableOperationService>()
                    .RenewAttemptLeaseAsync(tenantId, attempt.Id, attempt.LeaseToken,
                        workerCode, options.LeaseDuration, hostCancellation);
                await observer.RenewedAsync(
                    tenantId, renewed.Id, renewed.Version, hostCancellation);
            }
            catch (OperationCanceledException) when (hostCancellation.IsCancellationRequested)
            {
                return;
            }
            catch (DurableOperationRuleException exception)
                when (exception.Code == "OPERATIONS.ATTEMPT.LEASE_LOST")
            {
                leaseLost.Cancel();
                return;
            }
        }
    }
}
