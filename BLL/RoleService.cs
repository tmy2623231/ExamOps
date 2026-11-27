using ExamOps.DAL.Repositories;
using ExamOps.Model.Dtos;

namespace ExamOps.BLL;

public sealed class RoleService(RoleRepository roles, ICurrentUser currentUser)
{
    public async Task<IReadOnlyList<RoleRow>> GetAllAsync()
    {
        await currentUser.LoadAsync();
        if (!currentUser.HasPermission(Permissions.RoleAssign)) throw new UnauthorizedAccessException();
        return await roles.GetAllAsync();
    }

    public async Task<IReadOnlyList<PermissionRow>> GetPermissionsAsync()
    {
        await currentUser.LoadAsync();
        if (!currentUser.HasPermission(Permissions.RoleAssign)) throw new UnauthorizedAccessException();
        return await roles.GetPermissionsAsync();
    }
}
