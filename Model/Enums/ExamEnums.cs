namespace ExamOps.Model.Enums;

public static class ExamTaskStatus
{
    public const string Draft = "Draft";
    public const string Published = "Published";
    public const string Closed = "Closed";
    public const string Revoked = "Revoked";
    public const string Archived = "Archived";
}

public static class AssignmentStatus
{
    public const string Pending = "Pending";
    public const string Available = "Available";
    public const string InProgress = "InProgress";
    public const string Submitted = "Submitted";
    public const string Graded = "Graded";
    public const string Closed = "Closed";
    public const string Expired = "Expired";
    public const string Revoked = "Revoked";
}

public static class AttemptStatus
{
    public const string InProgress = "InProgress";
    public const string Submitted = "Submitted";
    public const string AutoSubmitted = "AutoSubmitted";
    public const string Graded = "Graded";
    public const string Voided = "Voided";
}

public static class RevocationScope
{
    public const string KeepScore = "KeepScore";
    public const string VoidScore = "VoidScore";
    public const string ForceSubmit = "ForceSubmit";
    public const string VoidAttempt = "VoidAttempt";
}

public static class OutboxStatus
{
    public const string Pending = "Pending";
    public const string Processing = "Processing";
    public const string Sent = "Sent";
    public const string Dead = "Dead";
}
