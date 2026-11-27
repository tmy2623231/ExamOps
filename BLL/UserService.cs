using ExamOps.BLL;
using ExamOps.DAL;
using ExamOps.DAL.Repositories;
using ExamOps.Model.Common;
using ExamOps.Model.Dtos;
using ExamOps.Model.Entities;
using ExamOps.Model.Enums;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ExamOps.BLL;

public sealed record UserEditInput(
    string Username,
    string DisplayName,
    string? Email,
    string Status,
    long PrimaryDepartmentId,
    string? Password,
    IReadOnlyList<RoleAssignmentDto> Roles,
    IReadOnlyList<long> DepartmentIds);

public sealed class UserService(
    IUnitOfWork uow,
    UserRepository users,
    DepartmentRepository departments,
    RoleRepository roles,
    DataScopeService scope,
    AuditRepository audit,
    ICurrentUser currentUser,
    IClock clock,
    PasswordHasher<AppUser> passwordHasher,
    IUserSessionInvalidator? sessionInvalidator = null)
{
    public async Task<IReadOnlyList<DepartmentRow>> GetDepartmentsAsync()
    {
        var all = await departments.GetAllAsync();
        var allowed = await scope.GetAccessibleDepartmentIdsAsync(Permissions.UserRead);
        return allowed is null ? all : all.Where(x => allowed.Contains(x.Id)).ToList();
    }
    public Task<IReadOnlyList<RoleRow>> GetRolesAsync() => roles.GetAllAsync();

    public async Task<PagedResult<UserRow>> GetPagedAsync(string? keyword, string? status, long? departmentId, int pageIndex, int pageSize)
    {
        var allowed = await scope.GetAccessibleDepartmentIdsAsync(Permissions.UserRead);
        return await users.GetPagedAsync(keyword, status, departmentId, allowed, pageIndex, pageSize);
    }

    public async Task<UserDetail?> GetDetailAsync(long id)
    {
        var detail = await users.GetDetailAsync(uow.Db, id);
        if (detail?.PrimaryDepartmentId is { } departmentId
            && !(await scope.EnsureDepartmentAccessAsync(departmentId, Permissions.UserRead)).IsSuccess) return null;
        return detail;
    }

    public async Task<Result> CreateAsync(UserEditInput input)
    {
        await currentUser.LoadAsync();
        if (!currentUser.HasPermission(Permissions.UserCreate))
            return Result.Fail("无权创建用户", "PERMISSION_DENIED");
        var validation = await ValidateInputAsync(input, null, Permissions.UserCreate);
        if (!validation.IsSuccess) return validation;
        var now = clock.UtcNow;
        try
        {
            await uow.ExecuteInTransactionAsync(async db =>
            {
                // 密码与实体在每次尝试内重新生成，避免重试复用上一次的 identity 值。
                var user = new AppUser
                {
                    Username = input.Username.Trim().ToLowerInvariant(),
                    DisplayName = input.DisplayName.Trim(),
                    Email = NormalizeEmail(input.Email),
                    Status = input.Status,
                    PrimaryDepartmentId = input.PrimaryDepartmentId,
                    PasswordHash = passwordHasher.HashPassword(new AppUser(), input.Password!),
                    MustChangePassword = true,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                await users.AddAsync(db, user);
                await db.SaveChangesAsync();
                await users.ReplaceAssignmentsAsync(db, user.Id, input.Roles, WithPrimary(input.DepartmentIds, input.PrimaryDepartmentId), input.PrimaryDepartmentId, now);
                await audit.WriteAsync(db, clock, currentUser.GetAuditContext(), "org.user.create", "AppUser", user.Id,
                    after: AuditUser(user, roles: input.Roles, departmentIds: WithPrimary(input.DepartmentIds, input.PrimaryDepartmentId)));
                await db.SaveChangesAsync();
            });
            return Result.Ok();
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return Result.Fail("用户名已存在", "DUPLICATE_USERNAME");
        }
    }

    public async Task<Result<UserBatchImportResult>> BatchImportAsync(IReadOnlyList<UserImportRowInput> rows)
    {
        await currentUser.LoadAsync();
        if (!currentUser.HasPermission(Permissions.UserCreate))
            return Result<UserBatchImportResult>.Fail("无权批量创建用户", "PERMISSION_DENIED");
        if (rows.Count == 0)
            return Result<UserBatchImportResult>.Fail("没有可导入的员工数据", "EMPTY_IMPORT");

        var allDepts = await departments.GetAllAsync();
        var activeDepts = allDepts.Where(d => d.IsActive).ToList();
        var allRoles = await roles.GetAllAsync();
        var canAssignRole = currentUser.HasPermission(Permissions.RoleAssign);

        var createdList = new List<UserImportCredentialRow>();
        var errors = new List<UserImportRowError>();
        var skipped = 0;

        foreach (var row in rows)
        {
            var username = row.Username.Trim().ToLowerInvariant();
            var displayName = row.DisplayName.Trim();
            if (username.Length is < 3 or > 64 || username.Any(x => !(char.IsAsciiLetterOrDigit(x) || x is '.' or '_' or '-')))
            {
                errors.Add(new(row.RowNo, row.Username, "账号需为 3–64 位字母、数字、点、下划线或短横线"));
                continue;
            }
            if (string.IsNullOrWhiteSpace(displayName) || displayName.Length > 100)
            {
                errors.Add(new(row.RowNo, username, "姓名需为 1–100 个字符"));
                continue;
            }

            var dept = activeDepts.FirstOrDefault(d => string.Equals(d.Name, row.DepartmentName.Trim(), StringComparison.Ordinal));
            if (dept is null)
            {
                errors.Add(new(row.RowNo, username, $"未找到启用部门“{row.DepartmentName}”"));
                continue;
            }
            if (!(await scope.EnsureDepartmentAccessAsync(dept.Id, Permissions.UserCreate)).IsSuccess)
            {
                errors.Add(new(row.RowNo, username, $"无权在部门“{dept.Name}”下创建用户"));
                continue;
            }

            var roleCode = MapRolePreset(row.RolePreset);
            var roleRow = allRoles.FirstOrDefault(r => r.IsActive && r.Code == roleCode);
            var roleAssignments = new List<RoleAssignmentDto>();
            var roleLabel = "考生";
            if (canAssignRole && roleRow is not null)
            {
                long? anchorDept = DataScopeRules.RequiresDepartmentAnchor(roleRow.ScopeType) ? dept.Id : null;
                roleAssignments.Add(new RoleAssignmentDto(roleRow.Id, anchorDept));
                roleLabel = roleRow.Name;
            }

            var password = string.IsNullOrWhiteSpace(row.InitialPassword)
                ? GenerateStrongPassword()
                : row.InitialPassword.Trim();
            if (password.Length is < 12 or > 128)
            {
                errors.Add(new(row.RowNo, username, "初始密码需为 12–128 个字符（或留空自动生成）"));
                continue;
            }

            var createRes = await CreateAsync(new UserEditInput(
                username, displayName, row.Email, "Active", dept.Id, password, roleAssignments, [dept.Id]));
            if (createRes.IsSuccess)
            {
                createdList.Add(new(row.RowNo, username, displayName, dept.Name, roleLabel, password));
            }
            else if (createRes.Code == "DUPLICATE_USERNAME")
            {
                skipped++;
            }
            else
            {
                errors.Add(new(row.RowNo, username, createRes.Error ?? "创建失败"));
            }
        }

        return Result<UserBatchImportResult>.Ok(new UserBatchImportResult(createdList.Count, skipped, createdList, errors));
    }

    private static string MapRolePreset(string preset) => preset.Trim() switch
    {
        "部门考务" or "考务管理员" or "ExamManager" => RoleCodes.ExamManager,
        "命题老师" or "题库管理员" or "QuestionManager" => RoleCodes.QuestionManager,
        "阅卷复核员" or "阅卷人" or "Grader" => RoleCodes.Grader,
        "部门管理员" or "DeptAdmin" => RoleCodes.DeptAdmin,
        _ => RoleCodes.Candidate
    };

    private static string GenerateStrongPassword()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789";
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(10);
        var body = new char[10];
        for (var i = 0; i < 10; i++) body[i] = chars[bytes[i] % chars.Length];
        return "Ex!" + new string(body);
    }

    public async Task<Result> UpdateAsync(long id, UserEditInput input, long expectedRowVersion)
    {
        await currentUser.LoadAsync();
        if (!currentUser.HasPermission(Permissions.UserUpdate))
            return Result.Fail("无权修改用户", "PERMISSION_DENIED");
        var user = await users.GetTrackedAsync(uow.Db, id);
        if (user is null) return Result.Fail("用户不存在", "NOT_FOUND");
        if (user.RowVersion != expectedRowVersion) return Result.Fail("用户已被其他管理员修改，请刷新后重试", "CONCURRENCY_CONFLICT");
        if (user.PrimaryDepartmentId is { } targetDepartment
            && !(await scope.EnsureDepartmentAccessAsync(targetDepartment, Permissions.UserUpdate)).IsSuccess)
            return Result.Fail("无权修改该用户", "OUT_OF_SCOPE");
        var currentRoles = await users.GetDetailAsync(uow.Db, id);
        if (!currentUser.HasPermission(Permissions.RoleAssign)
            && !SameAssignments(currentRoles?.Roles ?? [], input.Roles))
            return Result.Fail("无权修改用户的角色分配", "PERMISSION_DENIED");
        var validation = await ValidateInputAsync(input, id, Permissions.UserUpdate, currentRoles?.Roles);
        if (!validation.IsSuccess) return validation;
        var platformAdminRoleId = await roles.GetIdByCodeAsync(RoleCodes.PlatformAdmin);
        if (user.Id == currentUser.Id && (input.Status != "Active" || !input.Roles.Any(x => x.RoleId == platformAdminRoleId)))
            return Result.Fail("不能停用自己或移除自己的平台管理员角色", "SELF_PROTECTION");

        if (!scope.HasGlobalAccess(Permissions.UserUpdate) && currentRoles?.Roles.Any(x => x.RoleId == platformAdminRoleId) == true)
            return Result.Fail("不能修改平台管理员", "SELF_PROTECTION");
        var mayRemoveLastAdmin = currentRoles?.Roles.Any(x => x.RoleId == platformAdminRoleId) == true
            && !input.Roles.Any(x => x.RoleId == platformAdminRoleId);
        Result? lastAdminFailure = null;
        await uow.ExecuteInTransactionAsync(async db =>
        {
            if (mayRemoveLastAdmin)
            {
                await users.LockPlatformAdminManagementAsync(db);
                if (!await users.ExistsOtherPlatformAdminAsync(db, id))
                {
                    lastAdminFailure = Result.Fail("至少保留一名启用的平台管理员", "LAST_PLATFORM_ADMIN");
                    return;
                }
            }
            var user = await users.GetTrackedAsync(db, id) ?? throw new InvalidOperationException("用户不存在。");
            var before = AuditUser(user, currentRoles);
            user.DisplayName = input.DisplayName.Trim();
            user.Email = NormalizeEmail(input.Email);
            user.Status = input.Status;
            user.PrimaryDepartmentId = input.PrimaryDepartmentId;
            user.UpdatedAt = clock.UtcNow;
            user.RowVersion++;
            if (!string.IsNullOrWhiteSpace(input.Password))
            {
                user.PasswordHash = passwordHasher.HashPassword(user, input.Password);
                user.MustChangePassword = true;
            }
            await users.ReplaceAssignmentsAsync(db, id, input.Roles, WithPrimary(input.DepartmentIds, input.PrimaryDepartmentId), input.PrimaryDepartmentId, clock.UtcNow);
            await audit.WriteAsync(db, clock, currentUser.GetAuditContext(), "org.user.update", "AppUser", id,
                before, AuditUser(user, roles: input.Roles, departmentIds: WithPrimary(input.DepartmentIds, input.PrimaryDepartmentId)));
            await db.SaveChangesAsync();
        });
        if (lastAdminFailure is null && sessionInvalidator is not null)
            await sessionInvalidator.InvalidateUserAsync(id);
        return lastAdminFailure ?? Result.Ok();
    }

    public async Task<Result> SetStatusAsync(long id, string status, long expectedRowVersion)
    {
        await currentUser.LoadAsync();
        if (!currentUser.HasPermission(Permissions.UserDisable))
            return Result.Fail("无权修改用户状态", "PERMISSION_DENIED");
        var user = await users.GetTrackedAsync(uow.Db, id);
        if (user is null) return Result.Fail("用户不存在", "NOT_FOUND");
        if (user.RowVersion != expectedRowVersion) return Result.Fail("用户已被其他管理员修改，请刷新后重试", "CONCURRENCY_CONFLICT");
        if (user.PrimaryDepartmentId is { } targetDepartment
            && !(await scope.EnsureDepartmentAccessAsync(targetDepartment, Permissions.UserDisable)).IsSuccess)
            return Result.Fail("无权修改该用户", "OUT_OF_SCOPE");
        if (status is not ("Active" or "Disabled")) return Result.Fail("用户状态无效", "INVALID_STATUS");
        if (user.Id == currentUser.Id && status == "Disabled") return Result.Fail("不能停用当前登录账号", "SELF_PROTECTION");
        var platformAdminRoleId = await roles.GetIdByCodeAsync(RoleCodes.PlatformAdmin);
        var currentRoles = await users.GetDetailAsync(uow.Db, id);
        if (!scope.HasGlobalAccess(Permissions.UserDisable) && currentRoles?.Roles.Any(x => x.RoleId == platformAdminRoleId) == true)
            return Result.Fail("不能修改平台管理员", "SELF_PROTECTION");
        var mayDisableLastAdmin = status == "Disabled"
            && currentRoles?.Roles.Any(x => x.RoleId == platformAdminRoleId) == true;
        Result? lastAdminFailure = null;
        await uow.ExecuteInTransactionAsync(async db =>
        {
            if (mayDisableLastAdmin)
            {
                await users.LockPlatformAdminManagementAsync(db);
                if (!await users.ExistsOtherPlatformAdminAsync(db, id))
                {
                    lastAdminFailure = Result.Fail("至少保留一名启用的平台管理员", "LAST_PLATFORM_ADMIN");
                    return;
                }
            }
            var user = await users.GetTrackedAsync(db, id) ?? throw new InvalidOperationException("用户不存在。");
            var before = AuditUser(user, currentRoles);
            user.Status = status;
            user.UpdatedAt = clock.UtcNow;
            user.RowVersion++;
            await audit.WriteAsync(db, clock, currentUser.GetAuditContext(), status == "Active" ? "org.user.enable" : "org.user.disable", "AppUser", id,
                before, AuditUser(user, currentRoles));
            await db.SaveChangesAsync();
        });
        if (lastAdminFailure is null && status == "Disabled" && sessionInvalidator is not null)
            await sessionInvalidator.InvalidateUserAsync(id);
        return lastAdminFailure ?? Result.Ok();
    }

    private async Task<Result> ValidateInputAsync(UserEditInput input, long? existingId, string permission,
        IReadOnlyList<RoleAssignmentDto>? existingAssignments = null)
    {
        if (input.Roles.Any(assignment => existingAssignments?.Any(x =>
            x.RoleId == assignment.RoleId && x.DepartmentId == assignment.DepartmentId) != true)
            && !currentUser.HasPermission(Permissions.RoleAssign))
            return Result.Fail("无权分配用户角色", "PERMISSION_DENIED");
        var username = input.Username.Trim().ToLowerInvariant();
        if (username.Length is < 3 or > 64 || username.Any(x => !(char.IsAsciiLetterOrDigit(x) || x is '.' or '_' or '-')))
            return Result.Fail("账号需为 3–64 位字母、数字、点、下划线或短横线", "INVALID_USERNAME");
        if (string.IsNullOrWhiteSpace(input.DisplayName) || input.DisplayName.Trim().Length > 100)
            return Result.Fail("姓名需为 1–100 个字符", "INVALID_DISPLAY_NAME");
        if (input.Status is not ("Active" or "Disabled")) return Result.Fail("用户状态无效", "INVALID_STATUS");
        if (input.Password is not null && (input.Password.Length < 12 || input.Password.Length > 128))
            return Result.Fail("初始密码需为 12–128 个字符", "INVALID_PASSWORD");
        if (existingId is null && string.IsNullOrWhiteSpace(input.Password))
            return Result.Fail("新建用户必须设置初始密码", "PASSWORD_REQUIRED");
        if (input.Email?.Length > 254 || (input.Email is not null && !input.Email.Contains('@')))
            return Result.Fail("邮箱格式不正确", "INVALID_EMAIL");

        var department = await departments.GetByIdAsync(input.PrimaryDepartmentId);
        if (department is null || !department.IsActive) return Result.Fail("主部门不存在或已停用", "INVALID_DEPARTMENT");
        var scopeResult = await scope.EnsureDepartmentAccessAsync(input.PrimaryDepartmentId, permission);
        if (!scopeResult.IsSuccess) return scopeResult;
        foreach (var departmentId in WithPrimary(input.DepartmentIds, input.PrimaryDepartmentId))
        {
            var result = await scope.EnsureDepartmentAccessAsync(departmentId, permission);
            if (!result.IsSuccess) return result;
        }

        var roleRows = await roles.GetAllAsync();
        var roleMap = roleRows.ToDictionary(x => x.Id);
        var selectedRoleIds = input.Roles.Select(x => x.RoleId).Distinct().ToArray();
        if (selectedRoleIds.Length != input.Roles.Count)
            return Result.Fail("同一角色不能重复分配", "DUPLICATE_ROLE");
        foreach (var assignment in input.Roles)
        {
            var role = roleMap.GetValueOrDefault(assignment.RoleId);
            if (role is null) return Result.Fail("所选角色不存在", "INVALID_ROLE");
            var unchanged = existingAssignments?.Any(x =>
                x.RoleId == assignment.RoleId && x.DepartmentId == assignment.DepartmentId) == true;
            if (!role.IsActive && !unchanged)
                return Result.Fail($"角色“{role.Name}”已停用", "INACTIVE_ROLE");
            var mayGrantAll = currentUser.HasRole(RoleCodes.PlatformAdmin);
            if (!unchanged && !mayGrantAll && !role.Permissions.All(code => currentUser.HasPermission(code)))
                return Result.Fail($"不能授予超出自身权限的角色：{role.Name}", "PERMISSION_DENIED");
            if (!unchanged && role.ScopeType == "All" && !mayGrantAll)
                return Result.Fail("不能授予全局数据范围角色", "PERMISSION_DENIED");
            if (DataScopeRules.RequiresDepartmentAnchor(role.ScopeType))
            {
                if (assignment.DepartmentId is not { } roleDepartmentId)
                    return Result.Fail($"角色“{role.Name}”必须指定部门", "INVALID_ROLE_SCOPE");
                if (!unchanged)
                {
                    var result = await scope.EnsureDepartmentAccessAsync(roleDepartmentId, Permissions.RoleAssign);
                    if (!result.IsSuccess) return result;
                }
            }
            else if (assignment.DepartmentId is not null)
            {
                return Result.Fail($"角色“{role.Name}”不能限定部门", "INVALID_ROLE_SCOPE");
            }
        }

        if (existingId is not null && await users.ExistsOtherUsernameAsync(uow.Db, existingId.Value, username))
            return Result.Fail("用户名已存在", "DUPLICATE_USERNAME");
        return Result.Ok();
    }

    private static bool SameAssignments(IReadOnlyList<RoleAssignmentDto> current,
        IReadOnlyList<RoleAssignmentDto> proposed)
    {
        var currentSet = current.Select(x => (x.RoleId, x.DepartmentId)).ToHashSet();
        var proposedSet = proposed.Select(x => (x.RoleId, x.DepartmentId)).ToHashSet();
        return currentSet.SetEquals(proposedSet);
    }

    private static IReadOnlyList<long> WithPrimary(IReadOnlyList<long> ids, long primary)
        => ids.Append(primary).Distinct().ToArray();

    private static string? NormalizeEmail(string? email)
        => string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant();

    private static object AuditUser(AppUser user, UserDetail? detail = null,
        IReadOnlyList<RoleAssignmentDto>? roles = null, IReadOnlyList<long>? departmentIds = null)
        => new
        {
            user.Id, user.Username, user.DisplayName, user.Email, user.Status,
            user.PrimaryDepartmentId, user.MustChangePassword,
            Roles = roles ?? detail?.Roles ?? [],
            DepartmentIds = departmentIds ?? detail?.DepartmentIds ?? []
        };
}
