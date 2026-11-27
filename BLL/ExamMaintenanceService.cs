using ExamOps.DAL;
using ExamOps.DAL.Repositories;
using ExamOps.Model.Common;
using ExamOps.Model.Dtos;
using ExamOps.Model.Enums;
using Microsoft.EntityFrameworkCore;

namespace ExamOps.BLL;

public sealed class ExamMaintenanceService(IUnitOfWork uow, AuditRepository audit, IClock clock, ILogger<ExamMaintenanceService> logger)
{
    public async Task<int> SynchronizeAssignmentsAsync(int limit = 500)
    {
        var now = clock.UtcNow;
        var localNow = TimeFormat.ToLocal(now);
        var candidates = await uow.Db.ExamAssignments.AsNoTracking()
            .Where(x => x.Status == AssignmentStatus.Pending || x.Status == AssignmentStatus.Available || x.Status == AssignmentStatus.Graded)
            .OrderBy(x => x.Id).Take(Math.Clamp(limit, 1, 2000))
            .Select(x => new
            {
                x.Id, x.RowVersion, x.Status, x.AvailableFrom, x.AvailableTo, x.TaskId,
                TaskStatus = uow.Db.ExamTasks.Where(t => t.Id == x.TaskId).Select(t => t.Status).FirstOrDefault()!
            }).ToListAsync();
        var changed = 0;
        foreach (var item in candidates)
        {
            var target = item.Status switch
            {
                AssignmentStatus.Pending when item.TaskStatus == ExamTaskStatus.Published && localNow >= item.AvailableFrom && localNow < item.AvailableTo => AssignmentStatus.Available,
                AssignmentStatus.Pending when item.TaskStatus != ExamTaskStatus.Published || localNow >= item.AvailableTo => AssignmentStatus.Expired,
                AssignmentStatus.Available when item.TaskStatus != ExamTaskStatus.Published || localNow >= item.AvailableTo => AssignmentStatus.Expired,
                AssignmentStatus.Graded when item.TaskStatus == ExamTaskStatus.Published && localNow >= item.AvailableTo => AssignmentStatus.Expired,
                _ => null
            };
            if (target is null) continue;
            try
            {
                await uow.ExecuteInTransactionAsync(async db =>
                {
                    await ExamLockService.LockAssignmentAsync(db, item.Id);
                    var assignment = await db.ExamAssignments.SingleAsync(x => x.Id == item.Id);
                    if (assignment.RowVersion != item.RowVersion || assignment.Status != item.Status) return;
                    if (await db.ExamAttempts.AnyAsync(x => x.AssignmentId == assignment.Id && x.Status == AttemptStatus.InProgress)) return;
                    assignment.Status = target;
                    assignment.RowVersion++;
                    await db.SaveChangesAsync();
                    await audit.WriteAsync(db, clock, new AuditContext(null, "System", null, null, null),
                        "exam_assignment.synchronize", "ExamAssignment", assignment.Id,
                        after: new { assignment.Status }, reason: "window-or-task-state");
                    await db.SaveChangesAsync();
                    changed++;
                });
            }
            catch (DbUpdateConcurrencyException ex)
            {
                // 与开始考试/撤回竞争时由下一轮重新读取，不把一次跳过误报为系统故障。
                logger.LogDebug(ex, "考试安排同步遇到并发修改，安排 {AssignmentId} 留待下轮处理", item.Id);
            }
        }
        return changed;
    }
}
