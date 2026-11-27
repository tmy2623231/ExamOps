using ExamOps.DAL;
using ExamOps.Model.Entities;

namespace ExamOps.DAL.Repositories;

public sealed class OutboxRepository
{
    public void Add(ExamDbContext db, string type, string aggregateType, long aggregateId, object payload,
        string eventKey, DateTime now)
        => db.OutboxMessages.Add(new OutboxMessage
        {
            EventKey = eventKey,
            Type = type,
            AggregateType = aggregateType,
            AggregateId = aggregateId,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(payload, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)),
            Status = Model.Enums.OutboxStatus.Pending,
            NextAttemptAt = now,
            CreatedAt = now,
            RowVersion = 1
        });
}
