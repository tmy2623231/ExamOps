using ExamOps.DAL;
using ExamOps.Model.Common;
using ExamOps.Model.Dtos;
using ExamOps.Model.Entities;
using Microsoft.EntityFrameworkCore;

namespace ExamOps.DAL.Repositories;

public sealed class AttemptRepository(IClock clock)
{
    public Task<ExamAttempt?> GetTrackedAsync(ExamDbContext db, long attemptId)
        => db.ExamAttempts.FirstOrDefaultAsync(x => x.Id == attemptId);

    public Task<ExamAttempt?> GetNoTrackingAsync(ExamDbContext db, long attemptId)
        => db.ExamAttempts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == attemptId);

    public Task<ExamAssignment?> GetAssignmentTrackedAsync(ExamDbContext db, long assignmentId)
        => db.ExamAssignments.FirstOrDefaultAsync(x => x.Id == assignmentId);

    public async Task<PagedResult<MyExamRow>> GetMyPagedAsync(ExamDbContext db, long userId, int pageIndex, int pageSize)
    {
        var utcNow = clock.UtcNow;
        var now = TimeFormat.ToLocal(utcNow);
        var baseQuery = from assignment in db.ExamAssignments.AsNoTracking()
                        join task in db.ExamTasks.AsNoTracking() on assignment.TaskId equals task.Id
                        where assignment.UserId == userId && assignment.Status != "Revoked"
                        select new
                        {
                            assignment.Id,
                            assignment.TaskId,
                            task.Title,
                            task.Code,
                            assignment.Status,
                            TaskStatus = task.Status,
                            assignment.AvailableFrom,
                            assignment.AvailableTo,
                            DurationMinutes = assignment.DurationMinutesOverride ?? task.DurationMinutes,
                            AttemptLimit = assignment.AttemptLimitOverride ?? task.AttemptLimit,
                            assignment.AttemptsUsed,
                            assignment.InheritedAttemptsUsed,
                            assignment.RowVersion
                        };

        var total = await baseQuery.CountAsync();
        if (total == 0)
            return new PagedResult<MyExamRow>([], 0, pageIndex, pageSize);

        var page = await baseQuery
            .OrderByDescending(x => x.AvailableFrom)
            .ThenByDescending(x => x.Id)
            .Skip((pageIndex - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        var assignmentIds = page.Select(x => x.Id).ToArray();

        var attempts = await db.ExamAttempts.AsNoTracking()
            .Where(x => assignmentIds.Contains(x.AssignmentId) && x.Status != "Voided")
            .Select(x => new { x.AssignmentId, x.Id, x.Status, x.AttemptNo, x.DeadlineAt })
            .ToListAsync();

        var currentByAssignment = attempts
            .GroupBy(x => x.AssignmentId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(x => x.Status == "InProgress")
                      .ThenByDescending(x => x.AttemptNo)
                      .ThenByDescending(x => x.Id)
                      .First());

        var grants = await db.RetakeGrants.AsNoTracking()
            .Where(x => assignmentIds.Contains(x.AssignmentId) && (x.ExpiresAt == null || x.ExpiresAt > utcNow))
            .GroupBy(x => x.AssignmentId)
            .Select(g => new { AssignmentId = g.Key, Extra = g.Sum(x => x.ExtraAttempts) })
            .ToDictionaryAsync(x => x.AssignmentId, x => x.Extra);

        var rows = page.Select(item =>
        {
            currentByAssignment.TryGetValue(item.Id, out var current);
            var granted = grants.GetValueOrDefault(item.Id);
            var canStart = item.Status == "Available"
                && item.TaskStatus == "Published"
                && item.AvailableFrom <= now
                && item.AvailableTo >= now
                && item.AttemptsUsed + item.InheritedAttemptsUsed < item.AttemptLimit + granted
                && (current is null || current.Status != "InProgress");
            var statusMessage = item.Status == "Available" ? null : item.Status == "Revoked" ? "安排已撤回" : "当前不可开始";
            return new MyExamRow(
                item.Id, item.TaskId, item.Title, item.Code, item.Status,
                item.AvailableFrom, item.AvailableTo, item.DurationMinutes, item.AttemptLimit,
                item.AttemptsUsed, item.InheritedAttemptsUsed, granted,
                current?.Id, current?.Status, current?.DeadlineAt,
                canStart, statusMessage, item.RowVersion);
        }).ToList();

        return new PagedResult<MyExamRow>(rows, total, pageIndex, pageSize);
    }

    public async Task<(List<PaperSection> Sections, List<PaperPoolQuestion> Questions)> GetPaperSourcesAsync(ExamDbContext db, long paperVersionId)
    {
        var sections = await db.PaperSections.AsNoTracking().Where(x => x.PaperVersionId == paperVersionId)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Id).ToListAsync();
        var questions = await (from pq in db.PaperQuestions.AsNoTracking()
                               join qv in db.QuestionVersions.AsNoTracking() on pq.QuestionVersionId equals qv.Id
                               join q in db.Questions.AsNoTracking() on qv.QuestionId equals q.Id
                               where pq.PaperVersionId == paperVersionId
                               orderby pq.SectionId, pq.SortOrder, pq.Id
                               select new PaperPoolQuestion(pq.Id, pq.SectionId, pq.QuestionVersionId, q.Id, pq.Score, pq.IsPool)).ToListAsync();
        return (sections, questions);
    }

    public async Task<List<AttemptQuestionSource>> GetAttemptQuestionSourcesAsync(ExamDbContext db, IReadOnlyList<long> versionIds)
    {
        var ids = versionIds.Distinct().ToArray();
        return await (from version in db.QuestionVersions.AsNoTracking()
                      join question in db.Questions.AsNoTracking() on version.QuestionId equals question.Id
                      where ids.Contains(version.Id)
                      select new AttemptQuestionSource(version.Id, question.Type, version.Stem, version.AnswerJson)).ToListAsync();
    }

    public async Task<List<AttemptOptionSource>> GetOptionsAsync(ExamDbContext db, IReadOnlyList<long> versionIds)
    {
        var ids = versionIds.Distinct().ToArray();
        return await db.QuestionOptions.AsNoTracking().Where(x => ids.Contains(x.QuestionVersionId))
            .OrderBy(x => x.QuestionVersionId).ThenBy(x => x.SortOrder).ThenBy(x => x.Id)
            .Select(x => new AttemptOptionSource(x.QuestionVersionId, x.Label, x.Content)).ToListAsync();
    }

    public Task<AttemptAnswer?> GetAnswerTrackedAsync(ExamDbContext db, long attemptId, long questionVersionId)
        => db.AttemptAnswers.FirstOrDefaultAsync(x => x.AttemptId == attemptId && x.QuestionVersionId == questionVersionId);

    public async Task<List<AttemptAnswer>> GetAnswersTrackedAsync(ExamDbContext db, long attemptId)
        => await db.AttemptAnswers.Where(x => x.AttemptId == attemptId).ToListAsync();

    public void AddAnswer(ExamDbContext db, AttemptAnswer answer) => db.AttemptAnswers.Add(answer);

    public void AddEvent(ExamDbContext db, AttemptEvent attemptEvent) => db.AttemptEvents.Add(attemptEvent);

    public async Task<IReadOnlyList<ExamAttempt>> GetDueInProgressAsync(ExamDbContext db, DateTime cutoff, int limit)
        => await db.ExamAttempts.AsNoTracking().Where(x => x.Status == "InProgress" && x.DeadlineAt <= cutoff)
            .OrderBy(x => x.DeadlineAt).ThenBy(x => x.Id).Take(limit).ToListAsync();

    public async Task<PagedResult<AttemptEventRow>> GetEventsPagedAsync(ExamDbContext db, long attemptId, int pageIndex, int pageSize)
    {
        var query = from item in db.AttemptEvents.AsNoTracking()
                    join user in db.Users.AsNoTracking() on item.ActorId equals user.Id into users
                    from user in users.DefaultIfEmpty()
                    where item.AttemptId == attemptId
                    orderby item.OccurredAt descending, item.Id descending
                    select new AttemptEventRow(item.Id, item.EventType, item.PayloadJson, item.OccurredAt, item.ActorId, user == null ? null : user.DisplayName);
        var total = await query.CountAsync();
        var rows = await query.Skip((pageIndex - 1) * pageSize).Take(pageSize).ToListAsync();
        return new PagedResult<AttemptEventRow>(rows, total, pageIndex, pageSize);
    }
}

public sealed record PaperPoolQuestion(long PaperQuestionId, long SectionId, long QuestionVersionId, long QuestionId, decimal Score, bool IsPool);
public sealed record AttemptQuestionSource(long QuestionVersionId, string Type, string Stem, string CorrectJson);
public sealed record AttemptOptionSource(long QuestionVersionId, string Label, string Content);
