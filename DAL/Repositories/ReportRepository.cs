using ExamOps.DAL;
using ExamOps.Model.Common;
using ExamOps.Model.Dtos;
using ExamOps.Model.Entities;
using Microsoft.EntityFrameworkCore;

namespace ExamOps.DAL.Repositories;

public sealed class ReportRepository
{
    public Task<ExamTask?> GetTaskAsync(ExamDbContext db, long taskId)
        => db.ExamTasks.AsNoTracking().SingleOrDefaultAsync(x => x.Id == taskId);

    public async Task<SummaryCounts> GetSummaryCountsAsync(ExamDbContext db, long taskId)
    {
        var assignedCount = await db.ExamAssignments.AsNoTracking().CountAsync(x => x.TaskId == taskId && x.RevokedAt == null);
        var startedCount = await (from assignment in db.ExamAssignments.AsNoTracking()
            join attempt in db.ExamAttempts.AsNoTracking() on assignment.Id equals attempt.AssignmentId
            where assignment.TaskId == taskId && assignment.RevokedAt == null
            select assignment.Id).Distinct().CountAsync();
        var completedCount = await (from assignment in db.ExamAssignments.AsNoTracking()
            join attempt in db.ExamAttempts.AsNoTracking() on assignment.Id equals attempt.AssignmentId
            where assignment.TaskId == taskId && assignment.RevokedAt == null
                && attempt.SubmittedAt != null && attempt.Status != "Voided"
            select assignment.Id).Distinct().CountAsync();
        return new SummaryCounts(assignedCount, startedCount, completedCount);
    }

    public async Task<PublishedSnapshot?> GetPublishedSnapshotAsync(ExamDbContext db, long taskId)
    {
        var release = await (from task in db.ExamTasks.AsNoTracking()
                             join item in db.ResultReleases.AsNoTracking() on task.ResultReleaseId equals item.Id
                             where task.Id == taskId && task.ResultStatus == "Published" && item.TaskId == taskId
                             select item).SingleOrDefaultAsync();
        if (release is null) return null;
        var items = await db.ResultReleaseItems.AsNoTracking()
            .Where(x => x.ReleaseId == release.Id && x.TaskId == taskId)
            .OrderBy(x => x.AssignmentId).ToListAsync();
        return new PublishedSnapshot(release, items);
    }

    public async Task<List<PublishedItemRow>> GetPublishedItemsAsync(ExamDbContext db,
        IReadOnlyList<long> taskIds, DateTime? fromDate, DateTime? toDate, int limit)
    {
        var query = from item in db.ResultReleaseItems.AsNoTracking()
                    join release in db.ResultReleases.AsNoTracking() on item.ReleaseId equals release.Id
                    join task in db.ExamTasks.AsNoTracking() on item.TaskId equals task.Id
                    join department in db.Departments.AsNoTracking() on item.DepartmentIdSnapshot equals department.Id into departments
                    from department in departments.DefaultIfEmpty()
                    join user in db.Users.AsNoTracking() on item.UserId equals user.Id into users
                    from user in users.DefaultIfEmpty()
                    where taskIds.Contains(item.TaskId)
                        && task.ResultStatus == "Published"
                        && task.ResultReleaseId == release.Id
                        && release.TaskId == item.TaskId
                        && (!fromDate.HasValue || release.PublishedAt >= fromDate.Value)
                        && (!toDate.HasValue || release.PublishedAt <= toDate.Value)
                    orderby item.TaskId, item.AssignmentId
                    select new
                    {
                        item.TaskId, TaskTitle = task.Title, TaskCode = task.Code, release.Id, release.ReleaseNo,
                        release.PublishedAt, item.AssignmentId, item.UserId, UserName = user.DisplayName,
                        item.DepartmentIdSnapshot, DepartmentName = department.Name, item.AttemptId, item.FinalScore,
                        item.MaxScore, item.Passed
                    };
        return await query.Take(limit).Select(x => new PublishedItemRow(x.TaskId, x.TaskTitle, x.TaskCode,
            x.Id, x.ReleaseNo, x.PublishedAt, x.AssignmentId, x.UserId, x.UserName, x.DepartmentIdSnapshot,
            x.DepartmentName, x.AttemptId, x.FinalScore, x.MaxScore, x.Passed)).ToListAsync();
    }

    public async Task<List<QuestionAttemptRow>> GetQuestionAttemptsAsync(ExamDbContext db,
        IReadOnlyList<long> taskIds, int limit)
    {
        var query = from attempt in db.ExamAttempts.AsNoTracking()
                    join assignment in db.ExamAssignments.AsNoTracking() on attempt.AssignmentId equals assignment.Id
                    where taskIds.Contains(assignment.TaskId) && attempt.Status == "Graded" && attempt.VoidedAt == null
                    orderby attempt.Id
                    select new QuestionAttemptRow(attempt.Id, attempt.PaperSnapshotJson);
        return await query.Take(limit).ToListAsync();
    }

    public async Task<List<QuestionAnswerRow>> GetQuestionAnswersAsync(ExamDbContext db,
        IReadOnlyList<long> taskIds, long? questionVersionId, int limit)
    {
        var query = from answer in db.AttemptAnswers.AsNoTracking()
                    join attempt in db.ExamAttempts.AsNoTracking() on answer.AttemptId equals attempt.Id
                    join assignment in db.ExamAssignments.AsNoTracking() on attempt.AssignmentId equals assignment.Id
                    where taskIds.Contains(assignment.TaskId) && attempt.Status == "Graded" && attempt.VoidedAt == null
                        && (!questionVersionId.HasValue || answer.QuestionVersionId == questionVersionId.Value)
                    orderby answer.AttemptId, answer.QuestionVersionId
                    select new QuestionAnswerRow(answer.AttemptId, answer.QuestionVersionId, answer.Score, answer.MaxScore,
                        answer.GradingStatus, answer.AnswerJson != null && answer.AnswerJson != "{}");
        return await query.Take(limit).ToListAsync();
    }

    public async Task<List<QuestionMetaRow>> GetQuestionMetadataAsync(ExamDbContext db, IReadOnlyList<long> versionIds)
    {
        return await (from version in db.QuestionVersions.AsNoTracking()
                      join question in db.Questions.AsNoTracking() on version.QuestionId equals question.Id
                      where versionIds.Contains(version.Id)
                      select new QuestionMetaRow(version.Id, question.Id, question.ExternalCode, question.Type, version.Stem))
            .ToListAsync();
    }

    public async Task<PagedResult<AnomalySourceRow>> GetAnomaliesAsync(ExamDbContext db, long taskId,
        int pageIndex, int pageSize)
    {
        var reviewTypes = new[] { ReviewTypes.FormatAnomaly, ReviewTypes.Dispute, ReviewTypes.VoidRequest, "ScoreAdjusted", "Voided" };
        var invalid = from answer in db.AttemptAnswers.AsNoTracking()
                      join attempt in db.ExamAttempts.AsNoTracking() on answer.AttemptId equals attempt.Id
                      join assignment in db.ExamAssignments.AsNoTracking() on attempt.AssignmentId equals assignment.Id
                      join user in db.Users.AsNoTracking() on assignment.UserId equals user.Id
                      join version in db.QuestionVersions.AsNoTracking() on answer.QuestionVersionId equals version.Id
                      join question in db.Questions.AsNoTracking() on version.QuestionId equals question.Id
                      where assignment.TaskId == taskId && attempt.Status != "Voided" && answer.GradingStatus == "Invalid"
                          && (question.Type == "SingleChoice" || question.Type == "MultipleChoice" || question.Type == "TrueFalse")
                      select new
                      {
                          SourceId = answer.Id, Source = "Answer", AttemptId = attempt.Id, attempt.AttemptNo, UserId = user.Id, UserName = user.DisplayName,
                          EventType = ReviewTypes.FormatAnomaly, ActorId = (long?)null,
                          ActorName = (string?)null, OccurredAt = answer.UpdatedAt, PayloadJson = "{\"reason\":\"答案格式无效\"}"
                      };
        var events = from item in db.AttemptEvents.AsNoTracking()
                     join attempt in db.ExamAttempts.AsNoTracking() on item.AttemptId equals attempt.Id
                     join assignment in db.ExamAssignments.AsNoTracking() on attempt.AssignmentId equals assignment.Id
                     join user in db.Users.AsNoTracking() on assignment.UserId equals user.Id
                     join actor in db.Users.AsNoTracking() on item.ActorId equals actor.Id into actors
                     from actor in actors.DefaultIfEmpty()
                     where assignment.TaskId == taskId && reviewTypes.Contains(item.EventType)
                     select new
                     {
                         SourceId = item.Id, Source = "Event", AttemptId = attempt.Id, attempt.AttemptNo, UserId = user.Id, UserName = user.DisplayName,
                         item.EventType, item.ActorId, ActorName = actor.DisplayName,
                         item.OccurredAt, item.PayloadJson
                     };
        var source = invalid.Concat(events);
        var total = await source.CountAsync();
        var rows = await source.OrderByDescending(x => x.OccurredAt).ThenBy(x => x.Source).ThenByDescending(x => x.SourceId)
            .Skip((pageIndex - 1) * pageSize).Take(pageSize).ToListAsync();
        return new PagedResult<AnomalySourceRow>(rows.Select(x => new AnomalySourceRow(x.SourceId, x.Source, x.AttemptId,
            x.AttemptNo, x.UserId, x.UserName, x.EventType, x.ActorId, x.ActorName,
            x.OccurredAt, x.PayloadJson)).ToArray(), total, pageIndex, pageSize);
    }
}

public sealed record SummaryCounts(int AssignedCount, int StartedCount, int CompletedCount);
public sealed record PublishedSnapshot(ResultRelease Release, IReadOnlyList<ResultReleaseItem> Items);
public sealed record PublishedItemRow(long TaskId, string TaskTitle, string TaskCode, long ReleaseId, int ReleaseNo,
    DateTime PublishedAt, long AssignmentId, long UserId, string? UserName, long DepartmentId, string? DepartmentName,
    long? AttemptId, decimal? FinalScore, decimal? MaxScore, bool? Passed);
public sealed record QuestionAttemptRow(long AttemptId, string PaperSnapshotJson);
public sealed record QuestionAnswerRow(long AttemptId, long QuestionVersionId, decimal Score, decimal MaxScore, string GradingStatus, bool HasAnswer);
public sealed record QuestionMetaRow(long VersionId, long QuestionId, string? ExternalCode, string Type, string Stem);
public sealed record AnomalySourceRow(long Id, string Source, long AttemptId, int AttemptNo, long UserId,
    string UserName, string EventType, long? ActorId, string? ActorName,
    DateTime OccurredAt, string PayloadJson);
