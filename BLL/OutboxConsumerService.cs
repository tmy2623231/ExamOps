using System.Text.Json;
using ExamOps.DAL;
using ExamOps.DAL.Repositories;
using ExamOps.Model.Common;
using ExamOps.Model.Dtos;
using ExamOps.Model.Enums;
using Microsoft.EntityFrameworkCore;

namespace ExamOps.BLL;

public sealed class OutboxConsumerService(IUnitOfWork uow, AuditRepository audit, IClock clock, ILogger<OutboxConsumerService> logger)
{
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);
    private const int MaxAttempts = 5;
    private static readonly HashSet<string> KnownTypes = new(StringComparer.Ordinal)
    {
        "exam_task.published", "exam_task.closed", "exam_task.reopened", "exam_task.revoked", "exam_task.archived",
        "exam_assignment.created", "exam_assignment.batch_completed", "exam_assignment.revoked", "exam_assignment.reissued", "exam_assignment.retake_granted",
        "exam_attempt.started", "exam_attempt.submitted", "exam_attempt.graded", "exam_attempt.voided",
        "exam_attempt.review_recorded", "exam_attempt.score_adjusted",
        "exam_result.published", "exam_result.withdrawn", "report.exported"
    };

    public async Task<int> ProcessDueAsync(int limit = 100, CancellationToken cancellationToken = default)
    {
        var now = clock.UtcNow;
        var due = await ExamLockService.GetDueOutboxAsync(uow.Db, now, Lease, Math.Clamp(limit, 1, 500));
        var processed = 0;
        foreach (var item in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await uow.ExecuteInTransactionAsync(async db =>
                {
                    await ExamLockService.LockOutboxAsync(db, item.Id);
                    var message = await db.OutboxMessages.SingleAsync(x => x.Id == item.Id);
                    if (message.Status is OutboxStatus.Sent or OutboxStatus.Dead) return;
                    message.Status = OutboxStatus.Processing;
                    message.LockedAt = now;
                    message.RowVersion++;
                    await db.SaveChangesAsync(cancellationToken);

                    // 消费去重与业务状态在同一事务提交；重复投递只确认，不重复产生副作用。
                    var consumed = await db.AuditEvents.AsNoTracking()
                        .AnyAsync(x => x.EventKey == "consume:" + message.EventKey, cancellationToken);
                    if (!consumed)
                    {
                        if (!KnownTypes.Contains(message.Type))
                            throw new InvalidOperationException($"未知 outbox 事件类型：{message.Type}");
                        await audit.WriteAsync(db, clock, new AuditContext(null, "System", null, null, null),
                            "outbox.consume", message.AggregateType, message.AggregateId,
                            after: JsonSerializer.Deserialize<JsonElement>(message.PayloadJson),
                            eventKey: "consume:" + message.EventKey);
                    }
                    message.Status = OutboxStatus.Sent;
                    message.SentAt = now;
                    message.LockedAt = null;
                    message.LastError = null;
                    message.RowVersion++;
                    await db.SaveChangesAsync(cancellationToken);
                    processed++;
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Outbox 消费失败，事件 {EventKey}，将按重试策略处理", item.EventKey);
                await RecordFailureAsync(item.Id, ex);
            }
        }
        return processed;
    }

    private async Task RecordFailureAsync(long messageId, Exception exception)
    {
        try
        {
            await uow.ExecuteInTransactionAsync(async db =>
            {
                await ExamLockService.LockOutboxAsync(db, messageId);
                var message = await db.OutboxMessages.SingleAsync(x => x.Id == messageId);
                if (message.Status is OutboxStatus.Sent or OutboxStatus.Dead) return;
                var now = clock.UtcNow;
                message.RetryCount++;
                message.LastError = exception.ToString()[..Math.Min(exception.ToString().Length, 4000)];
                message.LockedAt = null;
                if (message.RetryCount >= MaxAttempts)
                {
                    message.Status = OutboxStatus.Dead;
                }
                else
                {
                    message.Status = OutboxStatus.Pending;
                    message.NextAttemptAt = now.AddSeconds(Math.Min(300, 15 * (1 << (message.RetryCount - 1))));
                }
                message.RowVersion++;
                await db.SaveChangesAsync();
            });
        }
        catch (Exception ex)
        {
            // 失败记录本身失败时保留原始错误，等待处理租约到期后由下一轮恢复。
            logger.LogError(ex, "无法记录 Outbox 失败状态，事件 ID {MessageId}", messageId);
        }
    }
}
