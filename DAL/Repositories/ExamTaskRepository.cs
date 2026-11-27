using ExamOps.DAL;
using ExamOps.Model.Common;
using ExamOps.Model.Dtos;
using ExamOps.Model.Entities;
using Microsoft.EntityFrameworkCore;

namespace ExamOps.DAL.Repositories;

public sealed class ExamTaskRepository
{
    public async Task<PagedResult<ExamTaskRow>> GetPagedAsync(
        ExamDbContext db, IReadOnlyList<long>? accessibleDepartmentIds, ExamTaskQuery query,
        IReadOnlyList<long>? assignedTaskIds = null)
    {
        var allowed = accessibleDepartmentIds?.ToArray();
        var assigned = assignedTaskIds?.ToArray();
        var tasks = db.ExamTasks.AsNoTracking().Where(x => allowed == null
            || (allowed.Length > 0 && allowed.Contains(x.DepartmentId))
            || (assigned != null && assigned.Contains(x.Id)));
        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var pattern = $"%{EscapeLike(query.Keyword.Trim())}%";
            tasks = tasks.Where(x => EF.Functions.ILike(x.Code, pattern) || EF.Functions.ILike(x.Title, pattern));
        }
        if (query.DepartmentId.HasValue) tasks = tasks.Where(x => x.DepartmentId == query.DepartmentId.Value);
        if (!string.IsNullOrWhiteSpace(query.Status)) tasks = tasks.Where(x => x.Status == query.Status);

        var totalCount = await tasks.CountAsync();
        var rows = await (from task in tasks
                          join department in db.Departments.AsNoTracking() on task.DepartmentId equals department.Id
                          join version in db.PaperVersions.AsNoTracking() on task.PaperVersionId equals version.Id into versions
                          from version in versions.DefaultIfEmpty()
                          orderby (task.UpdatedAt ?? task.CreatedAt) descending, task.Id descending
                          select new ExamTaskRow(
                              task.Id, task.Code, task.Title, task.DepartmentId, department.Name, task.Status,
                              task.ExamDate, task.AvailableFrom, task.AvailableTo, task.DurationMinutes,
                              task.AttemptLimit, task.LateScorePolicy, task.PaperVersionId, version.Title,
                              db.ExamAssignments.AsNoTracking().Count(x => x.TaskId == task.Id),
                              (from assignment in db.ExamAssignments.AsNoTracking()
                               join attempt in db.ExamAttempts.AsNoTracking() on assignment.Id equals attempt.AssignmentId
                               where assignment.TaskId == task.Id && attempt.Status == "InProgress"
                               select assignment.Id).Count(),
                              task.UpdatedAt, task.RowVersion))
            .Skip((query.PageIndex - 1) * query.PageSize).Take(query.PageSize).ToListAsync();
        return new PagedResult<ExamTaskRow>(rows, totalCount, query.PageIndex, query.PageSize);
    }

    public Task<ExamTaskDetail?> GetDetailAsync(
        ExamDbContext db, long taskId, IReadOnlyList<long>? accessibleDepartmentIds,
        IReadOnlyList<long>? assignedTaskIds = null)
    {
        var allowed = accessibleDepartmentIds?.ToArray();
        var assigned = assignedTaskIds?.ToArray();
        return (from task in db.ExamTasks.AsNoTracking()
                join department in db.Departments.AsNoTracking() on task.DepartmentId equals department.Id
                join version in db.PaperVersions.AsNoTracking() on task.PaperVersionId equals version.Id into versions
                from version in versions.DefaultIfEmpty()
                where task.Id == taskId && (allowed == null
                    || (allowed.Length > 0 && allowed.Contains(task.DepartmentId))
                    || (assigned != null && assigned.Contains(task.Id)))
                select new ExamTaskDetail(
                    task.Id, task.Code, task.Title, task.Description, task.DepartmentId, department.Name,
                    task.Status, task.ExamDate, task.PaperVersionId, version.Title,
                    task.AvailableFrom, task.AvailableTo, task.DurationMinutes, task.AttemptLimit,
                    task.ScorePolicy, task.RetakeApproval, task.RetakeCooldownMinutes, task.PassScore,
                    task.ResultReleasePolicy, task.ResultStatus, task.LateScorePolicy,
                    db.ExamAssignments.AsNoTracking().Count(x => x.TaskId == task.Id),
                    (from assignment in db.ExamAssignments.AsNoTracking()
                     join attempt in db.ExamAttempts.AsNoTracking() on assignment.Id equals attempt.AssignmentId
                     where assignment.TaskId == task.Id && attempt.Status == "InProgress"
                     select assignment.Id).Count(),
                    task.PublishedAt, task.ClosedAt, task.RevokedAt, task.ArchivedAt,
                    task.CreatedAt, task.RowVersion)).FirstOrDefaultAsync();
    }

    public Task<ExamTask?> GetTrackedAsync(ExamDbContext db, long taskId)
        => db.ExamTasks.FirstOrDefaultAsync(x => x.Id == taskId);

    public Task<ExamTask?> GetNoTrackingAsync(ExamDbContext db, long taskId)
        => db.ExamTasks.AsNoTracking().FirstOrDefaultAsync(x => x.Id == taskId);

    public async Task<IReadOnlyList<ExamTaskStaffRow>> GetStaffAsync(ExamDbContext db, long taskId)
        => await (from staff in db.ExamTaskStaffs.AsNoTracking()
                  join user in db.Users.AsNoTracking() on staff.UserId equals user.Id
                  where staff.TaskId == taskId
                  orderby user.DisplayName, staff.StaffRole, staff.Id
                  select new ExamTaskStaffRow(staff.Id, staff.TaskId, staff.UserId,
                      user.DisplayName, staff.StaffRole, staff.CreatedAt)).ToListAsync();

    public Task AddStaffAsync(ExamDbContext db, ExamTaskStaff staff)
    {
        db.ExamTaskStaffs.Add(staff);
        return Task.CompletedTask;
    }

    public Task RemoveStaffAsync(ExamDbContext db, long taskId, long staffId)
    {
        db.ExamTaskStaffs.RemoveRange(db.ExamTaskStaffs.Where(x => x.Id == staffId && x.TaskId == taskId));
        return Task.CompletedTask;
    }

    public Task<bool> HasAssignmentsAsync(ExamDbContext db, long taskId)
        => db.ExamAssignments.AsNoTracking().AnyAsync(x => x.TaskId == taskId);

    public Task<bool> HasInProgressAttemptsAsync(ExamDbContext db, long taskId)
        => (from assignment in db.ExamAssignments.AsNoTracking()
            join attempt in db.ExamAttempts.AsNoTracking() on assignment.Id equals attempt.AssignmentId
            where assignment.TaskId == taskId && attempt.Status == "InProgress"
            select attempt.Id).AnyAsync();

    private static string EscapeLike(string value)
        => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
