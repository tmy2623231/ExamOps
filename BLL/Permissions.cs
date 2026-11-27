namespace ExamOps.BLL;

public static class Permissions
{
    public const string DepartmentRead = "org.department.read";
    public const string DepartmentCreate = "org.department.create";
    public const string DepartmentUpdate = "org.department.update";
    public const string UserRead = "org.user.read";
    public const string UserCreate = "org.user.create";
    public const string UserUpdate = "org.user.update";
    public const string UserDisable = "org.user.disable";
    public const string RoleAssign = "org.role.assign";
    public const string QuestionRead = "question.read";
    public const string QuestionCreate = "question.create";
    public const string QuestionUpdate = "question.update";
    public const string QuestionPublish = "question.publish";
    public const string QuestionImport = "question.import";
    public const string PaperRead = "paper.read";
    public const string PaperCreate = "paper.create";
    public const string PaperUpdate = "paper.update";
    public const string PaperPublish = "paper.publish";
    public const string PaperArchive = "paper.archive";
    public const string ExamTaskRead = "exam.task.read";
    public const string ExamTaskCreate = "exam.task.create";
    public const string ExamTaskUpdate = "exam.task.update";
    public const string ExamTaskPublish = "exam.task.publish";
    public const string ExamTaskClose = "exam.task.close";
    public const string ExamTaskRevoke = "exam.task.revoke";
    public const string ExamAssignmentRead = "exam.assignment.read";
    public const string ExamAssignmentCreate = "exam.assignment.create";
    public const string ExamAssignmentRevoke = "exam.assignment.revoke";
    public const string ExamAssignmentGrantRetake = "exam.assignment.grant_retake";
    public const string ExamAttemptRead = "exam.attempt.read";
    public const string ExamAttemptGrade = "exam.attempt.grade";
    public const string ExamAttemptAdjustScore = "exam.attempt.adjust_score";
    public const string ExamAttemptVoid = "exam.attempt.void";
    public const string ExamResultPublish = "exam.result.publish";
    public const string AuditRead = "audit.read";
    public const string AuditExport = "audit.export";
    public const string ReportRead = "report.read";
    public const string ReportExport = "report.export";
}

public static class RoleCodes
{
    public const string PlatformAdmin = "PlatformAdmin";
    public const string OrgAdmin = "OrgAdmin";
    public const string DeptAdmin = "DeptAdmin";
    public const string ExamManager = "ExamManager";
    public const string QuestionManager = "QuestionManager";
    public const string Grader = "Grader";
    public const string Invigilator = "Invigilator";
    public const string Auditor = "Auditor";
    public const string Candidate = "Candidate";
}
