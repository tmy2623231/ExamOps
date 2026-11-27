using ExamOps.Model.Common;
using ExamOps.Model.Dtos;
using Microsoft.EntityFrameworkCore;

namespace ExamOps.DAL.Repositories;

public sealed class DashboardRepository
{
    public async Task<IReadOnlyList<string>> GetRoleNamesAsync(ExamDbContext db, long userId)
        => await (from ur in db.UserRoles.AsNoTracking()
                  join r in db.Roles.AsNoTracking() on ur.RoleId equals r.Id
                  where ur.UserId == userId && r.IsActive
                  select r.Name)
            .Distinct()
            .OrderBy(x => x)
            .ToListAsync();

    public async Task<(DashboardExamMetrics Metrics, IReadOnlyList<DashboardTaskItem> RecentTasks)> GetExamOverviewAsync(
        ExamDbContext db, IReadOnlyList<long>? departmentIds, IReadOnlyList<long> assignedTaskIds)
    {
        var taskQuery = db.ExamTasks.AsNoTracking()
            .Where(t => departmentIds == null || departmentIds.Contains(t.DepartmentId) || assignedTaskIds.Contains(t.Id));

        var statusCounts = await taskQuery
            .GroupBy(t => new { t.Status, t.ResultStatus })
            .Select(g => new { g.Key.Status, g.Key.ResultStatus, Count = g.Count() })
            .ToListAsync();

        var publishedCount = statusCounts.Where(x => x.Status == "Published").Sum(x => x.Count);
        var draftCount = statusCounts.Where(x => x.Status == "Draft").Sum(x => x.Count);
        var closedUnpublished = statusCounts.Where(x => x.Status == "Closed" && x.ResultStatus != "Published").Sum(x => x.Count);
        var releasedCount = statusCounts.Where(x => x.ResultStatus == "Published").Sum(x => x.Count);

        var scopedTaskIds = await taskQuery.Select(t => t.Id).ToListAsync();

        int totalAssignments = 0;
        int completedAssignments = 0;
        int pendingReviews = 0;
        decimal? avgPassRate = null;

        if (scopedTaskIds.Count > 0)
        {
            totalAssignments = await db.ExamAssignments.AsNoTracking()
                .CountAsync(a => scopedTaskIds.Contains(a.TaskId) && a.RevokedAt == null);

            completedAssignments = await (from a in db.ExamAssignments.AsNoTracking()
                                          join att in db.ExamAttempts.AsNoTracking() on a.Id equals att.AssignmentId
                                          where scopedTaskIds.Contains(a.TaskId) && a.RevokedAt == null
                                                && att.SubmittedAt != null && att.Status != "Voided"
                                          select a.Id)
                .Distinct()
                .CountAsync();

            var reviewTypes = new[] { ReviewTypes.FormatAnomaly, ReviewTypes.Dispute, ReviewTypes.VoidRequest };
            pendingReviews = await (from att in db.ExamAttempts.AsNoTracking()
                                    join a in db.ExamAssignments.AsNoTracking() on att.AssignmentId equals a.Id
                                    where scopedTaskIds.Contains(a.TaskId)
                                          && att.SubmittedAt != null && att.Status != "Voided"
                                          && (db.AttemptAnswers.AsNoTracking().Any(ans => ans.AttemptId == att.Id && ans.GradingStatus == "Invalid")
                                              || db.AttemptEvents.AsNoTracking().Any(ev => ev.AttemptId == att.Id && reviewTypes.Contains(ev.EventType)))
                                    select att.Id)
                .CountAsync();

            var releaseStats = await (from t in db.ExamTasks.AsNoTracking()
                                      join r in db.ResultReleases.AsNoTracking() on t.ResultReleaseId equals r.Id
                                      join item in db.ResultReleaseItems.AsNoTracking() on r.Id equals item.ReleaseId
                                      where scopedTaskIds.Contains(t.Id) && t.ResultStatus == "Published" && item.Passed != null
                                      select item.Passed!.Value)
                .ToListAsync();

            if (releaseStats.Count > 0)
            {
                var passed = releaseStats.Count(x => x);
                avgPassRate = Math.Round(passed * 100m / releaseStats.Count, 1);
            }
        }

        var recentRaw = await (from t in taskQuery
                               join d in db.Departments.AsNoTracking() on t.DepartmentId equals d.Id
                               orderby t.UpdatedAt descending, t.Id descending
                               select new
                               {
                                   t.Id,
                                   t.Code,
                                   t.Title,
                                   DepartmentName = d.Name,
                                   t.Status,
                                   t.ResultStatus,
                                   t.AvailableFrom,
                                   t.AvailableTo
                               })
            .Take(6)
            .ToListAsync();

        var recentIds = recentRaw.Select(x => x.Id).ToArray();
        var assignCountMap = recentIds.Length == 0
            ? new Dictionary<long, int>()
            : await db.ExamAssignments.AsNoTracking()
                .Where(a => recentIds.Contains(a.TaskId) && a.RevokedAt == null)
                .GroupBy(a => a.TaskId)
                .Select(g => new { TaskId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.TaskId, x => x.Count);

        var completedCountMap = recentIds.Length == 0
            ? new Dictionary<long, int>()
            : await (from a in db.ExamAssignments.AsNoTracking()
                     join att in db.ExamAttempts.AsNoTracking() on a.Id equals att.AssignmentId
                     where recentIds.Contains(a.TaskId) && a.RevokedAt == null && att.SubmittedAt != null && att.Status != "Voided"
                     select new { a.TaskId, a.Id })
                .Distinct()
                .GroupBy(x => x.TaskId)
                .Select(g => new { TaskId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.TaskId, x => x.Count);

        var recentTasks = recentRaw.Select(x => new DashboardTaskItem(
            x.Id, x.Code, x.Title, x.DepartmentName, x.Status, x.ResultStatus,
            x.AvailableFrom, x.AvailableTo,
            assignCountMap.GetValueOrDefault(x.Id),
            completedCountMap.GetValueOrDefault(x.Id))).ToList();

        var metrics = new DashboardExamMetrics(
            publishedCount, draftCount, closedUnpublished, releasedCount,
            totalAssignments, completedAssignments, pendingReviews, avgPassRate);

        return (metrics, recentTasks);
    }

    public async Task<DashboardQuestionMetrics> GetQuestionOverviewAsync(ExamDbContext db, IReadOnlyList<long>? departmentIds)
    {
        var bankQuery = db.QuestionBanks.AsNoTracking()
            .Where(b => departmentIds == null || departmentIds.Contains(b.DepartmentId) || b.IsShared);
        var bankIds = await bankQuery.Select(b => b.Id).ToListAsync();
        if (bankIds.Count == 0)
            return new DashboardQuestionMetrics(0, 0, 0, 0, 0, 0);

        var qStats = await db.Questions.AsNoTracking()
            .Where(q => bankIds.Contains(q.BankId) && q.Status != "Archived")
            .GroupBy(q => new { q.Status, q.Type })
            .Select(g => new { g.Key.Status, g.Key.Type, Count = g.Count() })
            .ToListAsync();

        return new DashboardQuestionMetrics(
            bankIds.Count,
            qStats.Where(x => x.Status == "Published").Sum(x => x.Count),
            qStats.Where(x => x.Status == "Draft").Sum(x => x.Count),
            qStats.Where(x => x.Type == "SingleChoice").Sum(x => x.Count),
            qStats.Where(x => x.Type == "MultipleChoice").Sum(x => x.Count),
            qStats.Where(x => x.Type == "TrueFalse").Sum(x => x.Count));
    }
}
