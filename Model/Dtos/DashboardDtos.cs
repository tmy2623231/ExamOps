namespace ExamOps.Model.Dtos;

public sealed record DashboardExamMetrics(
    int PublishedTaskCount,
    int DraftTaskCount,
    int ClosedUnpublishedCount,
    int ReleasedTaskCount,
    int TotalAssignmentCount,
    int CompletedAssignmentCount,
    int PendingReviewCount,
    decimal? AveragePassRate);

public sealed record DashboardQuestionMetrics(
    int BankCount,
    int PublishedQuestionCount,
    int DraftQuestionCount,
    int SingleChoiceCount,
    int MultipleChoiceCount,
    int TrueFalseCount);

public sealed record DashboardTaskItem(
    long Id,
    string Code,
    string Title,
    string DepartmentName,
    string Status,
    string ResultStatus,
    DateTime AvailableFrom,
    DateTime AvailableTo,
    int AssignmentCount,
    int CompletedCount);

public sealed record DashboardOverviewDto(
    string DisplayName,
    IReadOnlyList<string> RoleNames,
    OrganizationSummary? OrgSummary,
    DashboardExamMetrics? ExamMetrics,
    DashboardQuestionMetrics? QuestionMetrics,
    IReadOnlyList<DashboardTaskItem> RecentTasks,
    IReadOnlyList<MyExamRow> MyExams,
    bool CanQuickPublish,
    bool CanManageDepartments,
    bool CanImportQuestions,
    bool CanReviewAttempts,
    bool CanPublishResults,
    bool CanViewReports);
