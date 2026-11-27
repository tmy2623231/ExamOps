using ExamOps.DAL;
using ExamOps.Model.Dtos;
using Microsoft.EntityFrameworkCore;

namespace ExamOps.DAL.Repositories;

public sealed class AccessRepository(IDbContextFactory<ExamDbContext> factory)
{
    public async Task<CurrentUserData?> GetAsync(long userId)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await GetAsync(userId, db);
    }

    public async Task<CurrentUserData?> GetAsync(long userId, ExamDbContext db)
    {
        var user = await db.Users.AsNoTracking().Where(x => x.Id == userId)
            .Select(x => new { x.Id, x.Username, x.DisplayName, x.Status, x.RowVersion, x.MustChangePassword }).FirstOrDefaultAsync();
        if (user is null) return null;
        var permissions = await db.UserRoles.AsNoTracking().Where(x => x.UserId == userId && x.Role.IsActive)
            .SelectMany(x => x.Role.RolePermissions.Select(p => p.Permission.Code)).Distinct().ToListAsync();
        var roles = await db.UserRoles.AsNoTracking().Where(x => x.UserId == userId && x.Role.IsActive)
            .Select(x => new
            {
                x.Role.Code,
                x.DepartmentId,
                x.Role.ScopeType,
                Permissions = x.Role.RolePermissions.Select(p => p.Permission.Code).ToList()
            }).ToListAsync();
        var roleData = roles.Select(x => new RoleAssignmentData(x.Code, x.DepartmentId, x.ScopeType, x.Permissions)).ToList();
        var departments = await db.UserDepartments.AsNoTracking().Where(x => x.UserId == userId)
            .Select(x => new DepartmentAccessData(x.DepartmentId, x.Department.Path, x.IsPrimary)).ToListAsync();
        return new CurrentUserData(user.Id, user.Username, user.DisplayName, user.Status, user.RowVersion, permissions, roleData, departments, user.MustChangePassword);
    }

    public async Task<UserStamp?> GetStampAsync(long userId)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Users.AsNoTracking().Where(x => x.Id == userId)
            .Select(x => new UserStamp(x.Id, x.Status, x.RowVersion)).FirstOrDefaultAsync();
    }
}

public sealed record UserStamp(long Id, string Status, long RowVersion);
