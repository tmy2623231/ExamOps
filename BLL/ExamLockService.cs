using ExamOps.DAL;
using ExamOps.Model.Entities;
using Microsoft.EntityFrameworkCore;

namespace ExamOps.BLL;

public static class ExamLockService
{
    public static Task LockTaskAsync(ExamDbContext db, long taskId)
        => db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"exam-task|" + taskId}, 0))");

    public static Task LockAssignmentAsync(ExamDbContext db, long assignmentId)
        => db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"exam-assignment|" + assignmentId}, 0))");

    public static Task LockAttemptAsync(ExamDbContext db, long attemptId)
        => db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"exam-attempt|" + attemptId}, 0))");

    public static Task LockOutboxAsync(ExamDbContext db, long messageId)
        => db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"exam-outbox|" + messageId}, 0))");

    public static async Task<IReadOnlyList<OutboxMessage>> GetDueOutboxAsync(ExamDbContext db, DateTime now, TimeSpan staleAfter, int limit)
    {
        var staleBefore = now - staleAfter;
        return await db.OutboxMessages
            .FromSql($"""SELECT * FROM outbox_message WHERE (status = 'Pending' AND next_attempt_at <= {now}) OR (status = 'Processing' AND locked_at < {staleBefore}) ORDER BY next_attempt_at, id FOR UPDATE SKIP LOCKED LIMIT {limit}""")
            .AsTracking().ToListAsync();
    }
}
