using ExamOps.BLL;
using ExamOps.DAL;
using ExamOps.DAL.Repositories;
using ExamOps.Model.Common;
using ExamOps.Model.Enums;
using Microsoft.EntityFrameworkCore;

namespace ExamOps.BLL;

public sealed class DataScopeService(ICurrentUser currentUser, DepartmentRepository departments,
    IUnitOfWork uow)
{
    public async Task<Result> EnsureDepartmentAccessAsync(long departmentId, string permission)
    {
        await currentUser.LoadAsync();
        if (!currentUser.IsAuthenticated) return Result.Fail("请先登录", "UNAUTHENTICATED");
        if (!currentUser.HasPermission(permission)) return Result.Fail("无权访问该部门", "PERMISSION_DENIED");
        if (HasGlobalAccess(permission)) return Result.Ok();

        var target = await departments.GetByIdAsync(departmentId);
        if (target is null) return Result.Fail("部门不存在", "NOT_FOUND");
        foreach (var assignment in currentUser.Roles.Where(x =>
                     x.DepartmentId.HasValue && x.Permissions.Contains(permission, StringComparer.Ordinal)))
        {
            var root = await departments.GetByIdAsync(assignment.DepartmentId!.Value);
            if (root is not null && DataScopeRules.MatchesDepartment(assignment.ScopeType, target.Id, target.Path, root.Id, root.Path))
                return Result.Ok();
        }
        return Result.Fail("无权访问该部门", "OUT_OF_SCOPE");
    }

    public async Task<bool> CanAccessDepartmentAsync(long departmentId, string permission)
        => (await EnsureDepartmentAccessAsync(departmentId, permission)).IsSuccess;

    public async Task<IReadOnlyList<long>?> GetAccessibleDepartmentIdsAsync(string permission)
    {
        await currentUser.LoadAsync();
        if (!currentUser.IsAuthenticated) throw new InvalidOperationException("请先登录");
        if (!currentUser.HasPermission(permission)) return [];
        if (HasGlobalAccess(permission)) return null;

        var all = await departments.GetAllAsync();
        var departmentMap = all.ToDictionary(x => x.Id);
        var anchors = currentUser.Roles
            .Where(x => x.DepartmentId.HasValue && x.Permissions.Contains(permission, StringComparer.Ordinal))
            .Select(x => (Id: x.DepartmentId!.Value, Scope: x.ScopeType))
            .ToArray();
        return all.Where(target => anchors.Any(anchor =>
            departmentMap.TryGetValue(anchor.Id, out var root)
            && DataScopeRules.MatchesDepartment(anchor.Scope, target.Id, target.Path, root.Id, root.Path)))
            .Select(x => x.Id).ToArray();
    }

    public async Task<IReadOnlyList<long>> GetAssignedTaskIdsAsync(string permission)
    {
        await currentUser.LoadAsync();
        if (!currentUser.IsAuthenticated || !currentUser.HasPermission(permission)) return [];
        var staffRoles = currentUser.Roles
            .Where(x => x.ScopeType == "Assigned" && x.Permissions.Contains(permission, StringComparer.Ordinal))
            .Select(x => x.Code).Distinct(StringComparer.Ordinal).ToArray();
        if (staffRoles.Length == 0 || currentUser.Id is not { } userId) return [];
        return await uow.Db.ExamTaskStaffs.AsNoTracking()
            .Where(x => x.UserId == userId && staffRoles.Contains(x.StaffRole))
            .Select(x => x.TaskId).Distinct().ToListAsync();
    }

    public async Task<bool> CanAccessTaskAsync(long taskId, long departmentId, string permission)
    {
        if (HasGlobalAccess(permission)) return true;
        if (await CanAccessDepartmentAsync(departmentId, permission)) return true;
        return (await GetAssignedTaskIdsAsync(permission)).Contains(taskId);
    }

    public bool HasGlobalAccess(string permission)
        => currentUser.HasPermission(permission)
            && (currentUser.HasRole(RoleCodes.PlatformAdmin)
                || currentUser.Roles.Any(x => x.DepartmentId is null && x.ScopeType == "All"
                    && x.Permissions.Contains(permission, StringComparer.Ordinal)));
}
