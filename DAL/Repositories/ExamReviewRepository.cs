using ExamOps.DAL;
using ExamOps.Model.Common;
using ExamOps.Model.Dtos;
using ExamOps.Model.Entities;
using Microsoft.EntityFrameworkCore;

namespace ExamOps.DAL.Repositories;

public sealed class ExamReviewRepository
{
    public Task<ExamReviewContext?> GetContextAsync(ExamDbContext db, long attemptId)
        => (from attempt in db.ExamAttempts.AsNoTracking()
            join assignment in db.ExamAssignments.AsNoTracking() on attempt.AssignmentId equals assignment.Id
            join task in db.ExamTasks.AsNoTracking() on assignment.TaskId equals task.Id
            where attempt.Id == attemptId
            select new ExamReviewContext(attempt.Id, attempt.AssignmentId, assignment.UserId, task.Id,
                task.DepartmentId, task.ResultStatus)).SingleOrDefaultAsync();

    public Task<bool> HasObjectiveAnswersAsync(ExamDbContext db, long attemptId)
        => (from answer in db.AttemptAnswers.AsNoTracking()
            join version in db.QuestionVersions.AsNoTracking() on answer.QuestionVersionId equals version.Id
            join question in db.Questions.AsNoTracking() on version.QuestionId equals question.Id
            where answer.AttemptId == attemptId
                && (question.Type == "SingleChoice" || question.Type == "MultipleChoice" || question.Type == "TrueFalse")
            select answer.Id).AnyAsync();

    public async Task<PagedResult<ReviewQueueRow>> GetQueueAsync(ExamDbContext db,
        IReadOnlyList<long>? departmentIds, IReadOnlyList<long> assignedTaskIds,
        ExamReviewQuery query, int pageIndex, int pageSize)
    {
        var reviewTypes = new[] { ReviewTypes.FormatAnomaly, ReviewTypes.Dispute, ReviewTypes.VoidRequest };
        var source = from attempt in db.ExamAttempts.AsNoTracking()
                     join assignment in db.ExamAssignments.AsNoTracking() on attempt.AssignmentId equals assignment.Id
                     join task in db.ExamTasks.AsNoTracking() on assignment.TaskId equals task.Id
                     join user in db.Users.AsNoTracking() on assignment.UserId equals user.Id
                     join department in db.Departments.AsNoTracking() on task.DepartmentId equals department.Id
                     where attempt.SubmittedAt != null && attempt.Status != "Voided"
                         && (departmentIds == null || departmentIds.Contains(task.DepartmentId)
                             || assignedTaskIds.Contains(task.Id))
                         && (query.TaskId == null || task.Id == query.TaskId)
                         && (query.AttemptId == null || attempt.Id == query.AttemptId)
                     let objective = (from answer in db.AttemptAnswers.AsNoTracking()
                                      join version in db.QuestionVersions.AsNoTracking() on answer.QuestionVersionId equals version.Id
                                      join question in db.Questions.AsNoTracking() on version.QuestionId equals question.Id
                                      where answer.AttemptId == attempt.Id
                                          && (question.Type == "SingleChoice" || question.Type == "MultipleChoice" || question.Type == "TrueFalse")
                                      select answer.Id).Count()
                     let invalid = (from answer in db.AttemptAnswers.AsNoTracking()
                                    join version in db.QuestionVersions.AsNoTracking() on answer.QuestionVersionId equals version.Id
                                    join question in db.Questions.AsNoTracking() on version.QuestionId equals question.Id
                                    where answer.AttemptId == attempt.Id && answer.GradingStatus == "Invalid"
                                        && (question.Type == "SingleChoice" || question.Type == "MultipleChoice" || question.Type == "TrueFalse")
                                    select answer.Id).Count()
                     let latestType = db.AttemptEvents.AsNoTracking()
                         .Where(x => x.AttemptId == attempt.Id && reviewTypes.Contains(x.EventType))
                         .OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.Id)
                         .Select(x => x.EventType).FirstOrDefault()
                     let latestAt = db.AttemptEvents.AsNoTracking()
                         .Where(x => x.AttemptId == attempt.Id && reviewTypes.Contains(x.EventType))
                         .OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.Id)
                         .Select(x => (DateTime?)x.OccurredAt).FirstOrDefault()
                     where objective > 0 && (invalid > 0
                         || db.AttemptEvents.AsNoTracking().Any(x => x.AttemptId == attempt.Id && reviewTypes.Contains(x.EventType)))
                         && (query.ReviewType == null
                             || (query.ReviewType == ReviewTypes.FormatAnomaly && invalid > 0)
                             || db.AttemptEvents.AsNoTracking().Any(x => x.AttemptId == attempt.Id && x.EventType == query.ReviewType))
                     select new
                     {
                         AttemptId = attempt.Id, AssignmentId = assignment.Id, TaskId = task.Id,
                         TaskTitle = task.Title, UserId = user.Id, UserName = user.DisplayName,
                         DepartmentId = task.DepartmentId, DepartmentName = department.Name,
                         attempt.AttemptNo, attempt.Status, attempt.AutoScore, attempt.Score,
                         attempt.MaxScore, attempt.RowVersion, attempt.SubmittedAt, attempt.GradedAt,
                         InvalidAnswerCount = invalid, LatestReviewType = latestType, LatestReviewAt = latestAt
                     };
        var total = await source.CountAsync();
        var rows = await source.OrderByDescending(x => x.LatestReviewAt).ThenByDescending(x => x.SubmittedAt)
            .ThenByDescending(x => x.AttemptId).Skip((pageIndex - 1) * pageSize).Take(pageSize)
            .Select(x => new ReviewQueueRow(x.AttemptId, x.AssignmentId, x.TaskId, x.TaskTitle,
                x.UserId, x.UserName, x.DepartmentId, x.DepartmentName, x.AttemptNo, x.Status,
                x.AutoScore, x.Score, x.MaxScore, x.RowVersion, x.SubmittedAt, x.GradedAt,
                x.InvalidAnswerCount, x.LatestReviewType, x.LatestReviewAt)).ToListAsync();
        return new PagedResult<ReviewQueueRow>(rows, total, pageIndex, pageSize);
    }

    public async Task<ReviewQueueRow?> GetDetailAsync(ExamDbContext db, long attemptId)
    {
        var reviewTypes = new[] { ReviewTypes.FormatAnomaly, ReviewTypes.Dispute, ReviewTypes.VoidRequest };
        var row = await (from attempt in db.ExamAttempts.AsNoTracking()
                         join assignment in db.ExamAssignments.AsNoTracking() on attempt.AssignmentId equals assignment.Id
                         join task in db.ExamTasks.AsNoTracking() on assignment.TaskId equals task.Id
                         join user in db.Users.AsNoTracking() on assignment.UserId equals user.Id
                         join department in db.Departments.AsNoTracking() on task.DepartmentId equals department.Id
                         where attempt.Id == attemptId && attempt.SubmittedAt != null && attempt.Status != "Voided"
                         let objective = (from answer in db.AttemptAnswers.AsNoTracking()
                                          join version in db.QuestionVersions.AsNoTracking() on answer.QuestionVersionId equals version.Id
                                          join question in db.Questions.AsNoTracking() on version.QuestionId equals question.Id
                                          where answer.AttemptId == attempt.Id
                                              && (question.Type == "SingleChoice" || question.Type == "MultipleChoice" || question.Type == "TrueFalse")
                                          select answer.Id).Count()
                         let invalid = (from answer in db.AttemptAnswers.AsNoTracking()
                                        join version in db.QuestionVersions.AsNoTracking() on answer.QuestionVersionId equals version.Id
                                        join question in db.Questions.AsNoTracking() on version.QuestionId equals question.Id
                                        where answer.AttemptId == attempt.Id && answer.GradingStatus == "Invalid"
                                            && (question.Type == "SingleChoice" || question.Type == "MultipleChoice" || question.Type == "TrueFalse")
                                        select answer.Id).Count()
                         let latestType = db.AttemptEvents.AsNoTracking()
                             .Where(x => x.AttemptId == attempt.Id && reviewTypes.Contains(x.EventType))
                             .OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.Id)
                             .Select(x => x.EventType).FirstOrDefault()
                         let latestAt = db.AttemptEvents.AsNoTracking()
                             .Where(x => x.AttemptId == attempt.Id && reviewTypes.Contains(x.EventType))
                             .OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.Id)
                             .Select(x => (DateTime?)x.OccurredAt).FirstOrDefault()
                         where objective > 0
                         select new ReviewQueueRow(
                             attempt.Id, assignment.Id, task.Id, task.Title,
                             user.Id, user.DisplayName, task.DepartmentId, department.Name,
                             attempt.AttemptNo, attempt.Status, attempt.AutoScore, attempt.Score,
                             attempt.MaxScore, attempt.RowVersion, attempt.SubmittedAt, attempt.GradedAt,
                             invalid, latestType, latestAt))
                         .SingleOrDefaultAsync();
        return row;
    }

    public async Task<IReadOnlyList<AttemptEventRow>> GetEventsAsync(ExamDbContext db, long attemptId)
        => await (from item in db.AttemptEvents.AsNoTracking()
                  join user in db.Users.AsNoTracking() on item.ActorId equals user.Id into users
                  from user in users.DefaultIfEmpty()
                  where item.AttemptId == attemptId && (item.EventType == ReviewTypes.FormatAnomaly
                      || item.EventType == ReviewTypes.Dispute || item.EventType == ReviewTypes.VoidRequest)
                  orderby item.OccurredAt descending, item.Id descending
                  select new AttemptEventRow(item.Id, item.EventType, item.PayloadJson, item.OccurredAt,
                      item.ActorId, user.DisplayName)).Take(100).ToListAsync();

    public async Task<PagedResult<ScoreAdjustmentRow>> GetAdjustmentsPagedAsync(ExamDbContext db,
        long attemptId, int pageIndex, int pageSize)
    {
        var source = from item in db.ScoreAdjustments.AsNoTracking()
                     join user in db.Users.AsNoTracking() on item.AdjustedBy equals user.Id
                     where item.AttemptId == attemptId
                     select new
                     {
                         item.Id, item.AttemptId, item.OldScore, item.NewScore, item.Reason,
                         item.AdjustedBy, AdjustedByName = user.DisplayName, item.CreatedAt
                     };
        var total = await source.CountAsync();
        var rows = await source.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
            .Skip((pageIndex - 1) * pageSize).Take(pageSize)
            .Select(x => new ScoreAdjustmentRow(x.Id, x.AttemptId, x.OldScore, x.NewScore,
                x.Reason, x.AdjustedBy, x.AdjustedByName, x.CreatedAt)).ToListAsync();
        return new PagedResult<ScoreAdjustmentRow>(rows, total, pageIndex, pageSize);
    }

    public Task<ScoreAdjustment?> GetAdjustmentByKeyAsync(ExamDbContext db, long attemptId, string idempotencyKey)
        => db.ScoreAdjustments.AsNoTracking().SingleOrDefaultAsync(x => x.AttemptId == attemptId && x.IdempotencyKey == idempotencyKey);

    public void AddAdjustment(ExamDbContext db, ScoreAdjustment adjustment) => db.ScoreAdjustments.Add(adjustment);
}

public sealed record ExamReviewContext(
    long AttemptId, long AssignmentId, long UserId, long TaskId, long DepartmentId, string ResultStatus);
