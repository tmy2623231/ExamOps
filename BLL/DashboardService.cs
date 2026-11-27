using ExamOps.DAL;
using ExamOps.DAL.Repositories;
using ExamOps.Model.Common;
using ExamOps.Model.Dtos;

namespace ExamOps.BLL;

public sealed class DashboardService(
    IUnitOfWork uow,
    DashboardRepository dashboard,
    AttemptRepository attempts,
    OrganizationService organization,
    DataScopeService scope,
    ICurrentUser currentUser)
{
    public async Task<DashboardOverviewDto> GetOverviewAsync()
    {
        await currentUser.LoadAsync();
        if (!currentUser.IsAuthenticated || currentUser.Id is not { } userId)
            throw new UnauthorizedAccessException("需要登录");

        var roles = await dashboard.GetRoleNamesAsync(uow.Db, userId);

        OrganizationSummary? orgSummary = null;
        if (currentUser.HasAnyPermission(Permissions.DepartmentRead, Permissions.UserRead, Permissions.RoleAssign))
            orgSummary = await organization.GetSummaryAsync();

        var isCandidateOnly = currentUser.Roles.Count > 0 && currentUser.Roles.All(r => r.Code == RoleCodes.Candidate);

        DashboardExamMetrics? examMetrics = null;
        IReadOnlyList<DashboardTaskItem> recentTasks = [];
        if (!isCandidateOnly && currentUser.HasPermission(Permissions.ExamTaskRead))
        {
            var deptIds = await scope.GetAccessibleDepartmentIdsAsync(Permissions.ExamTaskRead);
            var assigned = await scope.GetAssignedTaskIdsAsync(Permissions.ExamTaskRead);
            var examData = await dashboard.GetExamOverviewAsync(uow.Db, deptIds, assigned);
            examMetrics = examData.Metrics;
            recentTasks = examData.RecentTasks;
        }

        DashboardQuestionMetrics? questionMetrics = null;
        if (currentUser.HasPermission(Permissions.QuestionRead))
        {
            var qDeptIds = await scope.GetAccessibleDepartmentIdsAsync(Permissions.QuestionRead);
            questionMetrics = await dashboard.GetQuestionOverviewAsync(uow.Db, qDeptIds);
        }

        var myExamsPage = await attempts.GetMyPagedAsync(uow.Db, userId, 1, 5);

        return new DashboardOverviewDto(
            currentUser.DisplayName ?? currentUser.Username ?? "用户",
            roles,
            orgSummary,
            examMetrics,
            questionMetrics,
            recentTasks,
            myExamsPage.Items,
            currentUser.HasPermission(Permissions.ExamTaskCreate),
            currentUser.HasPermission(Permissions.DepartmentCreate),
            currentUser.HasPermission(Permissions.QuestionImport),
            currentUser.HasPermission(Permissions.ExamAttemptRead),
            currentUser.HasPermission(Permissions.ExamResultPublish),
            currentUser.HasPermission(Permissions.ReportRead));
    }
}
