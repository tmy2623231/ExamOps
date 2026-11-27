using System.Security.Claims;
using ExamOps.BLL;
using ExamOps.DAL;
using ExamOps.DAL.Repositories;
using ExamOps.Model.Common;
using ExamOps.Model.Dtos;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace ExamOps.Infra.Verification;

public sealed class Phase1Check(
    IServiceScopeFactory scopeFactory,
    IHttpContextAccessor httpContextAccessor,
    IClock clock)
{
    public async Task RunAsync()
    {
        using var scope = scopeFactory.CreateScope();
        var services = scope.ServiceProvider;
        var uow = services.GetRequiredService<IUnitOfWork>();
        var adminStamp = await uow.Db.Users.AsNoTracking()
            .Where(x => x.Username == "admin")
            .Select(x => new UserStamp(x.Id, x.Status, x.RowVersion))
            .SingleOrDefaultAsync() ?? throw new InvalidOperationException("缺少初始化管理员。");
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("examops_user_id", adminStamp.Id.ToString()),
                new Claim("examops_row_version", adminStamp.RowVersion.ToString()),
                new Claim(ClaimTypes.Name, "admin")
            ], "ExamOps.Check"))
        };
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
        httpContextAccessor.HttpContext = context;
        try
        {
            var organizations = services.GetRequiredService<OrganizationService>();
            var users = services.GetRequiredService<UserService>();
            var roles = services.GetRequiredService<RoleService>();
            var roleRepository = services.GetRequiredService<RoleRepository>();
            var departments = services.GetRequiredService<DepartmentRepository>();
            await uow.ExecuteInTransactionAsync(async _ =>
            {
                // 同一回调上下文内临时恢复管理员，commit:false 保证连同 fixture 一起回滚。
                var admin = await uow.Db.Users.SingleAsync(x => x.Id == adminStamp.Id);
                if (admin.Status != "Active")
                {
                    admin.Status = "Active";
                    admin.RowVersion++;
                    admin.UpdatedAt = clock.UtcNow;
                    await uow.SaveChangesAsync();
                }
                var currentUser = services.GetRequiredService<ICurrentUser>();
                var firstLoad = currentUser.LoadAsync();
                await currentUser.LoadAsync();
                Check(currentUser.HasPermission(Permissions.DepartmentRead), "concurrent current-user load completes before permission checks");
                await firstLoad;

                var root = (await organizations.GetDepartmentsAsync()).Single(x => x.Path == "001.");
                var suffix = clock.UtcNow.Ticks.ToString();
                var departmentName = $"自查部门-{suffix}";
                Check((await organizations.CreateDepartmentAsync(departmentName, root.Id)).IsSuccess, "create department");
                var department = await uow.Db.Departments.AsNoTracking().SingleAsync(x => x.Name == departmentName);
                Check((await organizations.UpdateDepartmentAsync(department.Id, departmentName + "-改", department.RowVersion)).IsSuccess, "update department");

                var scopedCheckUser = new ScopeCheckUser([
                    new RoleAssignmentData(RoleCodes.Auditor, null, "All", [Permissions.AuditRead]),
                    new RoleAssignmentData(RoleCodes.DeptAdmin, department.Id, "Department", [Permissions.UserRead])
                ]);
                var dataScope = new DataScopeService(scopedCheckUser, departments, uow);
                var userReadScope = await dataScope.GetAccessibleDepartmentIdsAsync(Permissions.UserRead);
                Check(!dataScope.HasGlobalAccess(Permissions.UserRead)
                    && userReadScope is not null
                    && userReadScope.SequenceEqual([department.Id]),
                    "an All role without UserRead cannot widen the UserRead scope");
                Check(dataScope.HasGlobalAccess(Permissions.AuditRead), "global access still applies to the role's own permission");

                var candidateRoleId = await roleRepository.GetIdByCodeAsync(RoleCodes.Candidate)
                    ?? throw new InvalidOperationException("缺少考生角色。");
                var username = $"check_{suffix}";
                var userInput = new UserEditInput(username, "自查用户", null, "Active", department.Id,
                    $"{Guid.NewGuid():N}Aa1!", [new RoleAssignmentDto(candidateRoleId, null)], []);
                Check((await users.CreateAsync(userInput)).IsSuccess, "create user");
                var user = await uow.Db.Users.AsNoTracking().SingleAsync(x => x.Username == username);
                var detail = await users.GetDetailAsync(user.Id) ?? throw new InvalidOperationException("用户详情读取失败。");
                Check(detail.Roles.Any(x => x.RoleId == candidateRoleId), "user role assignment");
                var update = userInput with { DisplayName = "自查用户-改", Password = null };
                Check((await users.UpdateAsync(user.Id, update, detail.RowVersion)).IsSuccess, "update user");
                var userUpdateAudit = await uow.Db.AuditEvents.AsNoTracking()
                    .Where(x => x.EntityType == "AppUser" && x.EntityId == user.Id && x.Action == "org.user.update")
                    .OrderByDescending(x => x.Id).FirstAsync();
                Check(userUpdateAudit.BeforeJson.Contains("\"roles\"", StringComparison.Ordinal)
                    && userUpdateAudit.AfterJson.Contains("\"departmentIds\"", StringComparison.Ordinal), "user assignment audit detail");
                detail = await users.GetDetailAsync(user.Id) ?? throw new InvalidOperationException("用户详情读取失败。");

                var candidateRole = await uow.Db.Roles.AsNoTracking()
                    .SingleAsync(x => x.Id == candidateRoleId);
                Check(candidateRole.ScopeType == "Self", "candidate role uses Self scope");
                var departmentRoleId = await roleRepository.GetIdByCodeAsync(RoleCodes.DeptAdmin)
                    ?? throw new InvalidOperationException("缺少部门管理员角色。");
                var invalidAnchor = await users.UpdateAsync(user.Id,
                    userInput with { Roles = [new RoleAssignmentDto(departmentRoleId, null)] }, detail.RowVersion);
                Check(!invalidAnchor.IsSuccess && invalidAnchor.Code == "INVALID_ROLE_SCOPE",
                    "department role requires an anchor");
                var invalidSelfAnchor = await users.UpdateAsync(user.Id,
                    userInput with { Roles = [new RoleAssignmentDto(candidateRoleId, department.Id)] }, detail.RowVersion);
                Check(!invalidSelfAnchor.IsSuccess && invalidSelfAnchor.Code == "INVALID_ROLE_SCOPE",
                    "Self role rejects a department anchor");
                Check((await users.SetStatusAsync(user.Id, "Disabled", detail.RowVersion)).IsSuccess, "disable user");
                Check((await organizations.SetDepartmentActiveAsync(department.Id, false, department.RowVersion + 1)).IsSuccess, "disable empty department");
                var roleRows = await roles.GetAllAsync();
                Check(roleRows.Count(x => x.IsSystem) == 9, "nine system roles");
                Check((await roles.GetPermissionsAsync()).Count == 37, "37 canonical permissions");
                Check(await uow.Db.AuditEvents.AsNoTracking().CountAsync() > 0, "audit records written");

                var access = services.GetRequiredService<AccessRepository>();
                var revocationProbe = new CurrentUserService(userId => access.GetAsync(userId, uow.Db), services.GetRequiredService<ICurrentRequestContext>());
                await revocationProbe.LoadAsync();
                Check(revocationProbe.HasPermission(Permissions.DepartmentRead), "active account loads permissions");
                var adminAccount = await uow.Db.Users.SingleAsync(x => x.Id == adminStamp.Id);
                adminAccount.Status = "Disabled";
                await uow.SaveChangesAsync();
                await revocationProbe.LoadAsync();
                Check(!revocationProbe.HasPermission(Permissions.DepartmentRead)
                    && !revocationProbe.HasRole(RoleCodes.PlatformAdmin),
                    "disabled account loses cached permissions on the next service call");
            }, commit: false);
            Console.WriteLine("Phase 1 self-check passed: organization, permission-aware scopes, session revocation, user assignments and audit writes rolled back.");
        }
        finally
        {
            httpContextAccessor.HttpContext = null;
        }
    }

    private sealed class ScopeCheckUser(IReadOnlyList<RoleAssignmentData> roles) : ICurrentUser
    {
        private readonly IReadOnlyList<string> _permissions = roles.SelectMany(x => x.Permissions).Distinct().ToArray();
        public long? Id => 1;
        public string? DisplayName => "ScopeCheck";
        public string? Username => "scope-check";
        public bool IsAuthenticated => true;
        public bool MustChangePassword => false;
        public Task LoadAsync() => Task.CompletedTask;
        public bool HasPermission(string permission) => _permissions.Contains(permission, StringComparer.Ordinal);
        public bool HasAnyPermission(params string[] permissions) => permissions.Any(HasPermission);
        public bool HasRole(string roleCode) => roles.Any(x => x.Code == roleCode);
        public IReadOnlyList<RoleAssignmentData> Roles => roles;
        public IReadOnlyList<DepartmentAccessData> Departments => [];
        public AuditContext GetAuditContext() => new(Id, string.Join(',', roles.Select(x => x.Code)), null, null, null);
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException($"Phase 1 self-check failed: {name}");
    }
}
