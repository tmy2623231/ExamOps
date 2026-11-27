using ExamOps.DAL;
using ExamOps.Model.Dtos;
using Microsoft.EntityFrameworkCore;

namespace ExamOps.DAL.Repositories;

public sealed class RoleRepository(IUnitOfWork uow)
{
    private ExamDbContext Db => uow.Db;

    public async Task<IReadOnlyList<RoleRow>> GetAllAsync()
    {
        var db = Db;
        var roles = await db.Roles.AsNoTracking().OrderBy(x => x.Id).ToListAsync();
        var permissions = await db.RolePermissions.AsNoTracking()
            .Select(x => new { x.RoleId, Code = x.Permission.Code }).ToListAsync();
        var counts = await db.UserRoles.AsNoTracking().GroupBy(x => x.RoleId).ToDictionaryAsync(x => x.Key, x => x.Count());
        var map = permissions.GroupBy(x => x.RoleId).ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(x => x.Code).OrderBy(x => x).ToList());
        return roles.Select(x => new RoleRow(x.Id, x.Code, x.Name, x.IsSystem, x.IsActive, x.ScopeType,
            counts.GetValueOrDefault(x.Id), map.GetValueOrDefault(x.Id, []), x.RowVersion)).ToList();
    }

    public async Task<IReadOnlyList<PermissionRow>> GetPermissionsAsync()
    {
        var db = Db;
        return await db.Permissions.AsNoTracking().OrderBy(x => x.Module).ThenBy(x => x.Code)
            .Select(x => new PermissionRow(x.Id, x.Code, x.Name, x.Module)).ToListAsync();
    }

    public async Task<long?> GetIdByCodeAsync(string code)
    {
        var db = Db;
        return await db.Roles.AsNoTracking().Where(x => x.Code == code).Select(x => (long?)x.Id).FirstOrDefaultAsync();
    }

    public Task<int> CountAsync(ExamDbContext db) => db.Roles.AsNoTracking().CountAsync();
}
