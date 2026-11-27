using ExamOps.BLL;

namespace ExamOps.Infra.Background;

public sealed class ExamBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<ExamBackgroundService> logger) : IHostedService, IDisposable
{
    private readonly CancellationTokenSource _stopping = new();
    private Task? _loop;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _loop = Task.Run(() => RunAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync();
        if (_loop is not null) await _loop.WaitAsync(cancellationToken);
        Dispose();
    }

    public void Dispose() => _stopping.Dispose();

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ExamMaintenanceService>().SynchronizeAssignmentsAsync();
                await scope.ServiceProvider.GetRequiredService<ExamAttemptService>().AutoSubmitDueAsync();
                await scope.ServiceProvider.GetRequiredService<ResultReleaseService>().TryAutoPublishDueAsync();
                await scope.ServiceProvider.GetRequiredService<OutboxConsumerService>().ProcessDueAsync();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                logger.LogError(ex, "考试后台任务执行失败，下一轮继续重试");
            }
        }
    }
}
