using ExamOps.DAL;
using ExamOps.Model.Common;
using ExamOps.Model.Dtos;
using ExamOps.Model.Entities;
using Microsoft.EntityFrameworkCore;

namespace ExamOps.DAL.Repositories;

public sealed class UserRepository(IUnitOfWork uow)
{
    private ExamDbContext Db => uow.Db;

    public async Task<PagedResult<UserRow>> GetPagedAsync(string? keyword, string? status, long? departmentId, IReadOnlyList<long>? allowedDepartmentIds, int pageIndex, int pageSize)
    {
        var db = Db;
        var query = db.Users.AsNoTracking();
        if (allowedDepartmentIds is not null) query = query.Where(x => x.PrimaryDepartmentId.HasValue && allowedDepartmentIds.Contains(x.PrimaryDepartmentId.Value));
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var like = $"%{EscapeLike(keyword.Trim())}%";
            query = query.Where(x => EF.Functions.ILike(x.Username, like) || EF.Functions.ILike(x.DisplayName, like));
        }
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.Status == status);
        if (departmentId.HasValue) query = query.Where(x => x.PrimaryDepartmentId == departmentId);

        var total = await query.CountAsync();
        var users = await query.OrderBy(x => x.DisplayName).ThenBy(x => x.Id)
            .Skip((pageIndex - 1) * pageSize).Take(pageSize)
            .Select(x => new
            {
                x.Id, x.Username, x.DisplayName, x.Email, x.Status,
                x.PrimaryDepartmentId, x.MustChangePassword, x.LastLoginAt, x.RowVersion
            }).ToListAsync();
        var ids = users.Select(x => x.Id).ToArray();
        var roles = await db.UserRoles.AsNoTracking().Where(x => ids.Contains(x.UserId))
            .Select(x => new { x.UserId, RoleName = x.Role.Name }).ToListAsync();
        var roleMap = roles.GroupBy(x => x.UserId).ToDictionary(g => g.Key, g => string.Join("、", g.Select(x => x.RoleName)));

        var rows = users.Select(x => new UserRow(
            x.Id, x.Username, x.DisplayName, x.Email, x.Status,
            x.PrimaryDepartmentId, null, roleMap.GetValueOrDefault(x.Id, "未分配"),
            x.MustChangePassword, x.LastLoginAt, x.RowVersion)).ToList();
        var departmentIds = users.Where(x => x.PrimaryDepartmentId.HasValue).Select(x => x.PrimaryDepartmentId!.Value).Distinct().ToArray();
        var names = await db.Departments.AsNoTracking().Where(x => departmentIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.Name);
        return new PagedResult<UserRow>(rows.Select(x => x with
        {
            PrimaryDepartmentName = x.PrimaryDepartmentId.HasValue ? names.GetValueOrDefault(x.PrimaryDepartmentId.Value) : null
        }).ToList(), total, pageIndex, pageSize);
    }

    public Task<AppUser?> GetTrackedAsync(ExamDbContext db, long id)
        => db.Users.FirstOrDefaultAsync(x => x.Id == id);

    public Task<AppUser?> GetByUsernameTrackedAsync(ExamDbContext db, string username)
        => db.Users.FirstOrDefaultAsync(x => x.Username == username);

    public async Task<UserDetail?> GetDetailAsync(ExamDbContext db, long id)
    {
        var user = await db.Users.AsNoTracking().Where(x => x.Id == id)
            .Select(x => new
            {
                x.Id, x.Username, x.DisplayName, x.Email, x.Status,
                x.PrimaryDepartmentId, x.MustChangePassword, x.RowVersion
            }).FirstOrDefaultAsync();
        if (user is null) return null;
        var roles = await db.UserRoles.AsNoTracking().Where(x => x.UserId == id)
            .Select(x => new RoleAssignmentDto(x.RoleId, x.DepartmentId)).ToListAsync();
        var departments = await db.UserDepartments.AsNoTracking().Where(x => x.UserId == id)
            .Select(x => x.DepartmentId).ToListAsync();
        return new UserDetail(user.Id, user.Username, user.DisplayName, user.Email, user.Status,
            user.PrimaryDepartmentId, user.MustChangePassword, user.RowVersion, roles, departments);
    }

    public Task AddAsync(ExamDbContext db, AppUser user)
    {
        db.Users.Add(user);
        return Task.CompletedTask;
    }

    public async Task ReplaceAssignmentsAsync(ExamDbContext db, long userId, IReadOnlyList<RoleAssignmentDto> roles, IReadOnlyList<long> departmentIds, long primaryDepartmentId, DateTime now)
    {
        var user = await db.Users.FirstAsync(x => x.Id == userId);
        user.PrimaryDepartmentId = primaryDepartmentId;

        db.UserRoles.RemoveRange(db.UserRoles.Where(x => x.UserId == userId));
        db.UserRoles.AddRange(roles.Select(r => new UserRole { UserId = userId, RoleId = r.RoleId, DepartmentId = r.DepartmentId, CreatedAt = now }));

        db.UserDepartments.RemoveRange(db.UserDepartments.Where(x => x.UserId == userId));
        db.UserDepartments.AddRange(departmentIds.Select(id => new UserDepartment { UserId = userId, DepartmentId = id, IsPrimary = id == primaryDepartmentId }));
    }

    public Task<bool> ExistsOtherUsernameAsync(ExamDbContext db, long userId, string username)
        => db.Users.AsNoTracking().AnyAsync(x => x.Id != userId && x.Username == username);

    public async Task<bool> ExistsOtherPlatformAdminAsync(ExamDbContext db, long userId)
        => await db.UserRoles.AsNoTracking().AnyAsync(x => x.UserId != userId && x.Role.Code == "PlatformAdmin" && x.User.Status == "Active");

    public Task LockPlatformAdminManagementAsync(ExamDbContext db)
        => db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(684120001)");

    public async Task<(int Total, int Active)> GetCountsAsync(ExamDbContext db, IReadOnlyList<long>? allowedDepartmentIds)
    {
        var query = db.Users.AsNoTracking();
        if (allowedDepartmentIds is not null) query = query.Where(x => x.PrimaryDepartmentId.HasValue && allowedDepartmentIds.Contains(x.PrimaryDepartmentId.Value));
        return (await query.CountAsync(), await query.CountAsync(x => x.Status == "Active"));
    }

    private static string EscapeLike(string value) => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
