using ExamOps.DAL;
using ExamOps.Model.Common;
using ExamOps.Model.Dtos;
using ExamOps.Model.Entities;
using Microsoft.EntityFrameworkCore;

namespace ExamOps.DAL.Repositories;

public sealed class AssignmentRepository(IClock clock)
{
    public async Task<PagedResult<ExamAssignmentRow>> GetPagedAsync(ExamDbContext db, AssignmentQuery query,
        IReadOnlyList<long>? accessibleDepartmentIds, IReadOnlyList<long>? assignedTaskIds = null)
    {
        var assignments = ScopedAssignments(db, accessibleDepartmentIds, assignedTaskIds).Where(x => x.TaskId == query.TaskId);
        if (!string.IsNullOrWhiteSpace(query.Status)) assignments = assignments.Where(x => x.Status == query.Status);
        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var pattern = $"%{EscapeLike(query.Keyword.Trim())}%";
            assignments = assignments.Where(x => db.Users.AsNoTracking().Any(u => u.Id == x.UserId
                && (EF.Functions.ILike(u.DisplayName, pattern) || EF.Functions.ILike(u.Username, pattern))));
        }

        var total = await assignments.CountAsync();
        var page = assignments.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
            .Skip((query.PageIndex - 1) * query.PageSize).Take(query.PageSize);
        var rows = await ProjectAssignments(db, page, clock.UtcNow).ToListAsync();
        return new PagedResult<ExamAssignmentRow>(rows, total, query.PageIndex, query.PageSize);
    }

    public Task<ExamAssignmentRow?> GetRowAsync(ExamDbContext db, long assignmentId,
        IReadOnlyList<long>? accessibleDepartmentIds, IReadOnlyList<long>? assignedTaskIds = null)
        => ProjectAssignments(db, ScopedAssignments(db, accessibleDepartmentIds, assignedTaskIds).Where(x => x.Id == assignmentId),
            clock.UtcNow).FirstOrDefaultAsync();

    public Task<ExamAssignment?> GetTrackedAsync(ExamDbContext db, long assignmentId)
        => db.ExamAssignments.FirstOrDefaultAsync(x => x.Id == assignmentId);

    public async Task<IReadOnlyList<ExamAssignment>> GetTaskAssignmentsAsync(ExamDbContext db, long taskId, bool tracked)
    {
        var query = tracked ? db.ExamAssignments : db.ExamAssignments.AsNoTracking();
        return await query.Where(x => x.TaskId == taskId).ToListAsync();
    }

    public Task<AssignmentBatch?> GetBatchTrackedAsync(ExamDbContext db, long batchId)
        => db.AssignmentBatches.FirstOrDefaultAsync(x => x.Id == batchId);

    public async Task<AssignmentBatchDetail?> GetBatchDetailAsync(ExamDbContext db, long batchId, int pageIndex,
        int pageSize, IReadOnlyList<long>? accessibleDepartmentIds, IReadOnlyList<long>? assignedTaskIds = null)
    {
        var batches = db.AssignmentBatches.AsNoTracking();
        if (accessibleDepartmentIds is not null)
        {
            var allowed = accessibleDepartmentIds.ToArray();
            var assigned = assignedTaskIds?.ToArray();
            batches = batches.Where(x => db.ExamTasks.AsNoTracking().Any(t => t.Id == x.TaskId
                && ((allowed.Length > 0 && allowed.Contains(t.DepartmentId))
                    || (assigned != null && assigned.Contains(x.TaskId)))));
        }

        var batch = await batches.Where(x => x.Id == batchId)
            .Select(x => new
            {
                Batch = x,
                TaskTitle = db.ExamTasks.AsNoTracking().Where(t => t.Id == x.TaskId).Select(t => t.Title).First()
            }).FirstOrDefaultAsync();
        if (batch is null) return null;

        var rowsQuery = db.AssignmentBatchRows.AsNoTracking().Where(x => x.BatchId == batchId);
        var total = await rowsQuery.CountAsync();
        var rows = await rowsQuery.OrderBy(x => x.RowNo).ThenBy(x => x.Id)
            .Skip((pageIndex - 1) * pageSize).Take(pageSize)
            .Select(x => new AssignmentBatchRowDto(x.Id, x.BatchId, x.RowNo, x.UserId,
                x.UserId == null ? null : db.Users.AsNoTracking().Where(u => u.Id == x.UserId)
                    .Select(u => u.DisplayName).FirstOrDefault(),
                x.Status, x.AssignmentId, x.ErrorJson, x.RowVersion)).ToListAsync();
        var detail = batch.Batch;
        return new AssignmentBatchDetail(detail.Id, detail.TaskId, batch.TaskTitle, detail.Status,
            detail.TotalCount, detail.SuccessCount, detail.FailedCount, detail.SkippedCount,
            detail.StartedAt, detail.FinishedAt, detail.CreatedAt, detail.RowVersion,
            new PagedResult<AssignmentBatchRowDto>(rows, total, pageIndex, pageSize));
    }

    public async Task<IReadOnlyList<AssignmentBatchRow>> GetBatchRowsTrackedAsync(ExamDbContext db, long batchId)
        => await db.AssignmentBatchRows.Where(x => x.BatchId == batchId)
            .OrderBy(x => x.RowNo).ThenBy(x => x.Id).ToListAsync();

    public Task AddBatchAsync(ExamDbContext db, AssignmentBatch batch, IReadOnlyList<AssignmentBatchRow> rows)
    {
        // 批次与明细共用调用方的 DbContext/事务；这里只登记新增，提交由 UnitOfWork 负责。
        db.AssignmentBatches.Add(batch);
        db.AssignmentBatchRows.AddRange(rows);
        return Task.CompletedTask;
    }

    public Task AddAssignmentAsync(ExamDbContext db, ExamAssignment assignment)
    {
        db.ExamAssignments.Add(assignment);
        return Task.CompletedTask;
    }

    public async Task<int> SumGrantedAttemptsAsync(ExamDbContext db, long assignmentId, DateTime nowUtc)
        => await db.RetakeGrants.AsNoTracking().Where(x => x.AssignmentId == assignmentId
                && (x.ExpiresAt == null || x.ExpiresAt > nowUtc))
            .SumAsync(x => (int?)x.ExtraAttempts) ?? 0;

    public Task<ExamAssignment?> FindActiveAsync(ExamDbContext db, long taskId, long userId)
        => db.ExamAssignments.AsNoTracking().FirstOrDefaultAsync(x => x.TaskId == taskId
            && x.UserId == userId && x.Status != "Revoked");

    public Task<ExamAssignment?> FindReissuedAsync(ExamDbContext db, long taskId, long userId, long reissuedFromId)
        => db.ExamAssignments.AsNoTracking().FirstOrDefaultAsync(x => x.TaskId == taskId
            && x.UserId == userId && x.ReissuedFromId == reissuedFromId);

    public async Task<IReadOnlyList<AssignmentTargetUser>> ResolveTargetsAsync(ExamDbContext db, long taskId,
        string taskDepartmentPath, AssignmentTargetInput target)
    {
        var userIds = target.UserIds.Distinct().ToArray();
        var departmentIds = target.DepartmentIds.Distinct().ToArray();
        if (userIds.Length == 0 && departmentIds.Length == 0) return [];

        var candidates = from membership in db.UserDepartments.AsNoTracking()
                         join user in db.Users.AsNoTracking() on membership.UserId equals user.Id
                         join department in db.Departments.AsNoTracking() on membership.DepartmentId equals department.Id
                         where department.Path.StartsWith(taskDepartmentPath)
                         select new
                         {
                             UserId = user.Id, user.DisplayName, user.Status, membership.IsPrimary,
                             DepartmentId = department.Id, department.Name, department.Path
                         };
        if (target.IncludeDescendants)
        {
            candidates = candidates.Where(x => userIds.Contains(x.UserId) || db.Departments.AsNoTracking()
                .Any(root => departmentIds.Contains(root.Id) && x.Path.StartsWith(root.Path)));
        }
        else
        {
            candidates = candidates.Where(x => userIds.Contains(x.UserId) || departmentIds.Contains(x.DepartmentId));
        }

        var memberships = await candidates.OrderBy(x => x.UserId).ThenByDescending(x => x.IsPrimary)
            .ThenBy(x => x.DepartmentId).ToListAsync();
        var seen = new HashSet<long>();
        var targets = new List<AssignmentTargetUser>();
        foreach (var membership in memberships)
        {
            if (!seen.Add(membership.UserId)) continue;
            targets.Add(new AssignmentTargetUser(membership.UserId, membership.DisplayName, membership.DepartmentId,
                membership.Name, membership.Path, membership.Status, null, null));
        }
        if (targets.Count == 0) return targets;

        var targetIds = targets.Select(x => x.UserId).ToArray();
        var existing = await db.ExamAssignments.AsNoTracking()
            .Where(x => x.TaskId == taskId && targetIds.Contains(x.UserId))
            .OrderBy(x => x.Status == "Revoked").ThenByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
            .Select(x => new { x.UserId, x.Id, x.Status }).ToListAsync();
        var existingByUser = new Dictionary<long, (long? Id, string? Status)>();
        foreach (var assignment in existing)
        {
            existingByUser.TryAdd(assignment.UserId, (assignment.Id, assignment.Status));
        }

        return targets.Select(x =>
        {
            existingByUser.TryGetValue(x.UserId, out var assignment);
            return x with { ExistingAssignmentId = assignment.Id, ExistingStatus = assignment.Status };
        }).ToList();
    }

    private static IQueryable<ExamAssignment> ScopedAssignments(ExamDbContext db,
        IReadOnlyList<long>? accessibleDepartmentIds, IReadOnlyList<long>? assignedTaskIds)
    {
        var assignments = db.ExamAssignments.AsNoTracking();
        if (accessibleDepartmentIds is null) return assignments;
        var allowed = accessibleDepartmentIds.ToArray();
        var assigned = assignedTaskIds?.ToArray();
        return assignments.Where(x => db.ExamTasks.AsNoTracking().Any(t => t.Id == x.TaskId
            && ((allowed.Length > 0 && allowed.Contains(t.DepartmentId))
                || (assigned != null && assigned.Contains(x.TaskId)))));
    }

    private static IQueryable<ExamAssignmentRow> ProjectAssignments(ExamDbContext db,
        IQueryable<ExamAssignment> assignments, DateTime nowUtc)
    {
        var rows = from assignment in assignments
                   join task in db.ExamTasks.AsNoTracking() on assignment.TaskId equals task.Id
                   join user in db.Users.AsNoTracking() on assignment.UserId equals user.Id
                   join department in db.Departments.AsNoTracking() on assignment.DepartmentIdSnapshot equals department.Id
                   let currentAttemptId = db.ExamAttempts.AsNoTracking()
                       .Where(x => x.AssignmentId == assignment.Id && x.Status != "Voided")
                       .OrderByDescending(x => x.Status == "InProgress").ThenByDescending(x => x.AttemptNo)
                       .ThenByDescending(x => x.Id).Select(x => (long?)x.Id).FirstOrDefault()
                   let currentAttemptStatus = db.ExamAttempts.AsNoTracking()
                       .Where(x => x.AssignmentId == assignment.Id && x.Status != "Voided")
                       .OrderByDescending(x => x.Status == "InProgress").ThenByDescending(x => x.AttemptNo)
                       .ThenByDescending(x => x.Id).Select(x => x.Status).FirstOrDefault()
                   select new ExamAssignmentRow(assignment.Id, assignment.TaskId, task.Title, assignment.UserId,
                       user.DisplayName, assignment.DepartmentIdSnapshot, department.Name, assignment.Status,
                       assignment.AvailableFrom, assignment.AvailableTo,
                       assignment.AttemptLimitOverride ?? task.AttemptLimit, assignment.AttemptsUsed,
                       assignment.InheritedAttemptsUsed,
                       db.RetakeGrants.AsNoTracking().Where(x => x.AssignmentId == assignment.Id
                           && (x.ExpiresAt == null || x.ExpiresAt > nowUtc))
                           .Sum(x => (int?)x.ExtraAttempts) ?? 0,
                       currentAttemptId, currentAttemptStatus, assignment.CreatedAt, assignment.RowVersion);
        return rows;
    }

    private static string EscapeLike(string value)
        => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}

public sealed record AssignmentTargetUser(long UserId, string DisplayName, long DepartmentId,
    string DepartmentName, string DepartmentPath, string UserStatus, long? ExistingAssignmentId,
    string? ExistingStatus);
