using ExamOps.DAL;
using ExamOps.DAL.Repositories;
using ExamOps.Model.Common;
using ExamOps.Model.Dtos;
using ExamOps.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ExamOps.BLL;

public sealed class OrganizationService(
    IUnitOfWork uow,
    DepartmentRepository departments,
    UserRepository users,
    RoleRepository roles,
    AuditRepository audit,
    ICurrentUser currentUser,
    DataScopeService scope,
    IClock clock)
{
    public async Task<IReadOnlyList<DepartmentRow>> GetDepartmentsAsync()
    {
        var all = await departments.GetAllAsync();
        var allowed = await scope.GetAccessibleDepartmentIdsAsync(Permissions.DepartmentRead);
        return allowed is null ? all : all.Where(x => allowed.Contains(x.Id)).ToList();
    }

    public IReadOnlyList<DepartmentTemplatePreset> GetDepartmentTemplates() =>
    [
        new("safety", "安全生产 / 制造业标准架构", "推荐", "覆盖车间生产、安环监察、设备运维与质量管理",
            [
                new("生产制造部", [new("一车间", []), new("二车间", [])]),
                new("安全环保部", [new("安全监察组", []), new("应急管理组", [])]),
                new("设备运维部", []),
                new("质量管理部", [])
            ]),
        new("tech", "互联网 / 研发企业架构", "常用", "适用于研发团队、产品运营与信息安全考核",
            [
                new("产品研发中心", [new("前端开发组", []), new("后端架构组", []), new("质量测试组", [])]),
                new("运维与安全部", []),
                new("运营增长部", []),
                new("综合人力部", [])
            ]),
        new("edu", "院校 / 培训机构架构", "教学", "适用于教务管理、分科教研与实训考核",
            [
                new("教务管理部", []),
                new("基础教学部", [new("理工教研组", []), new("综合教研组", [])]),
                new("实训考核中心", []),
                new("学员服务部", [])
            ]),
        new("flat", "极简扁平团队", "轻量", "3 个核心分组，5 秒完成组织搭建",
            [
                new("管理组", []),
                new("业务执行组", []),
                new("考核监督组", [])
            ])
    ];

    public async Task<Result<ApplyDepartmentTemplateResult>> ApplyDepartmentTemplateAsync(long parentId, string templateKey)
    {
        await currentUser.LoadAsync();
        if (!currentUser.HasPermission(Permissions.DepartmentCreate))
            return Result<ApplyDepartmentTemplateResult>.Fail("无权创建部门", "PERMISSION_DENIED");
        var preset = GetDepartmentTemplates().FirstOrDefault(x => x.Key == templateKey);
        if (preset is null)
            return Result<ApplyDepartmentTemplateResult>.Fail("所选组织模板不存在", "TEMPLATE_NOT_FOUND");
        var parent = await departments.GetByIdAsync(parentId);
        if (parent is null || !parent.IsActive)
            return Result<ApplyDepartmentTemplateResult>.Fail("目标上级部门不存在或已停用", "PARENT_NOT_FOUND");
        if (!(await scope.EnsureDepartmentAccessAsync(parentId, Permissions.DepartmentCreate)).IsSuccess)
            return Result<ApplyDepartmentTemplateResult>.Fail("无权在该部门下套用模板", "OUT_OF_SCOPE");

        int created = 0;
        int skipped = 0;
        var now = clock.UtcNow;

        try
        {
            // 单事务批量套用组织模板并落汇总审计日志；同名启用部门自动跳过以保证幂等。
            await uow.ExecuteInTransactionAsync(async db =>
            {
                created = 0;
                skipped = 0;
                var existing = await db.Departments.AsNoTracking()
                    .Select(d => new { d.Id, d.ParentId, d.Name, d.Path, d.IsActive })
                    .ToListAsync();
                var pathMap = existing.ToDictionary(x => x.Id, x => x.Path);
                var childrenByParent = existing
                    .GroupBy(x => x.ParentId ?? 0L)
                    .ToDictionary(g => g.Key, g => g.Select(x => (x.Id, x.Name, x.Path, x.IsActive)).ToList());

                async Task EnsureNodesAsync(long currentParentId, IReadOnlyList<DepartmentTemplateNode> nodes)
                {
                    foreach (var node in nodes)
                    {
                        var siblings = childrenByParent.TryGetValue(currentParentId, out var list)
                            ? list
                            : childrenByParent[currentParentId] = [];
                        var matched = siblings.FirstOrDefault(x => x.IsActive && string.Equals(x.Name, node.Name, StringComparison.Ordinal));
                        long targetId;
                        if (matched.Id > 0)
                        {
                            skipped++;
                            targetId = matched.Id;
                        }
                        else
                        {
                            var parentPath = pathMap[currentParentId];
                            var siblingSegments = siblings
                                .Select(x => x.Path[parentPath.Length..].TrimEnd('.'))
                                .Where(x => int.TryParse(x, out _))
                                .Select(int.Parse);
                            var path = DepartmentPath.BuildChildPath(parentPath, siblingSegments);
                            var dept = new Department
                            {
                                ParentId = currentParentId,
                                Name = node.Name,
                                Path = path,
                                PathSegment = path.Split('.', StringSplitOptions.RemoveEmptyEntries)[^1],
                                SortOrder = await departments.GetNextSortOrderAsync(db, currentParentId),
                                IsActive = true,
                                CreatedAt = now,
                                UpdatedAt = now
                            };
                            await departments.AddAsync(db, dept);
                            await db.SaveChangesAsync();
                            created++;
                            targetId = dept.Id;
                            pathMap[dept.Id] = dept.Path;
                            siblings.Add((dept.Id, dept.Name, dept.Path, true));
                        }

                        if (node.Children.Count > 0)
                            await EnsureNodesAsync(targetId, node.Children);
                    }
                }

                await EnsureNodesAsync(parentId, preset.Nodes);

                await audit.WriteAsync(db, clock, currentUser.GetAuditContext(), "org.department.apply_template", "Department", parentId,
                    after: new { TemplateKey = preset.Key, TemplateName = preset.Name, ParentId = parentId, CreatedCount = created, SkippedCount = skipped });
                await db.SaveChangesAsync();
            });

            return Result<ApplyDepartmentTemplateResult>.Ok(new ApplyDepartmentTemplateResult(created, skipped));
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return Result<ApplyDepartmentTemplateResult>.Fail("部门路径冲突，请刷新后重试", "DUPLICATE_PATH");
        }
    }

    public async Task<Result> CreateDepartmentAsync(string name, long? parentId)
    {
        await currentUser.LoadAsync();
        if (!currentUser.HasPermission(Permissions.DepartmentCreate))
            return Result.Fail("无权创建部门", "PERMISSION_DENIED");
        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100)
            return Result.Fail("部门名称需为 1–100 个字符", "INVALID_NAME");
        Department? parent = null;
        if (parentId.HasValue)
        {
            parent = await departments.GetByIdAsync(parentId.Value);
            if (parent is null || !parent.IsActive) return Result.Fail("上级部门不存在或已停用", "PARENT_NOT_FOUND");
            if (!(await scope.EnsureDepartmentAccessAsync(parentId.Value, Permissions.DepartmentCreate)).IsSuccess) return Result.Fail("无权在该部门下创建子部门", "OUT_OF_SCOPE");
        }
        else if (!scope.HasGlobalAccess(Permissions.DepartmentCreate))
        {
            return Result.Fail("只有组织管理员可以创建根部门", "PERMISSION_DENIED");
        }

        var all = await departments.GetAllAsync();
        var parentPath = parent?.Path ?? "";
        var siblingSegments = all.Where(x => x.ParentId == parentId)
            .Select(x => x.Path[parentPath.Length..].TrimEnd('.'))
            .Where(x => int.TryParse(x, out _)).Select(int.Parse);
        string path;
        try
        {
            path = DepartmentPath.BuildChildPath(parentPath, siblingSegments);
        }
        catch (InvalidOperationException)
        {
            return Result.Fail("同一父部门下最多支持 999 个子部门", "TOO_MANY_CHILDREN");
        }
        var now = clock.UtcNow;
        try
        {
            await uow.ExecuteInTransactionAsync(async db =>
            {
                // 事务闭包必须可重放：实体和排序号都在每次尝试内重新构造。
                var department = new Department
                {
                    ParentId = parentId,
                    Name = name,
                    Path = path,
                    PathSegment = path.Split('.', StringSplitOptions.RemoveEmptyEntries)[^1],
                    SortOrder = await departments.GetNextSortOrderAsync(db, parentId),
                    IsActive = true,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                await departments.AddAsync(db, department);
                await db.SaveChangesAsync();
                await audit.WriteAsync(db, clock, currentUser.GetAuditContext(), "org.department.create", "Department", department.Id,
                    after: new { department.Id, department.Name, department.ParentId, department.Path, department.PathSegment });
                await db.SaveChangesAsync();
            });
            return Result.Ok();
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return Result.Fail("部门路径已存在，请重试", "DUPLICATE_PATH");
        }
    }

    public async Task<Result> UpdateDepartmentAsync(long id, string name, long expectedRowVersion)
    {
        await currentUser.LoadAsync();
        if (!currentUser.HasPermission(Permissions.DepartmentUpdate))
            return Result.Fail("无权修改部门", "PERMISSION_DENIED");
        var department = await departments.GetTrackedAsync(uow.Db, id);
        if (department is null) return Result.Fail("部门不存在", "NOT_FOUND");
        if (!(await scope.EnsureDepartmentAccessAsync(id, Permissions.DepartmentUpdate)).IsSuccess) return Result.Fail("无权修改该部门", "OUT_OF_SCOPE");
        if (department.RowVersion != expectedRowVersion) return Result.Fail("部门已被其他管理员修改，请刷新后重试", "CONCURRENCY_CONFLICT");
        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100) return Result.Fail("部门名称需为 1–100 个字符", "INVALID_NAME");
        var before = new { department.Name, department.IsActive };
        department.Name = name;
        department.UpdatedAt = clock.UtcNow;
        department.RowVersion++;
        await audit.WriteAsync(uow.Db, clock, currentUser.GetAuditContext(), "org.department.update", "Department", id, before,
            new { department.Name, department.IsActive });
        try
        {
            await uow.SaveChangesAsync();
            return Result.Ok();
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Fail("部门已被其他管理员修改，请刷新后重试", "CONCURRENCY_CONFLICT");
        }
    }

    public async Task<Result> SetDepartmentActiveAsync(long id, bool isActive, long expectedRowVersion)
    {
        await currentUser.LoadAsync();
        if (!currentUser.HasPermission(Permissions.DepartmentUpdate))
            return Result.Fail("无权修改部门状态", "PERMISSION_DENIED");
        var department = await departments.GetTrackedAsync(uow.Db, id);
        if (department is null) return Result.Fail("部门不存在", "NOT_FOUND");
        if (!(await scope.EnsureDepartmentAccessAsync(id, Permissions.DepartmentUpdate)).IsSuccess) return Result.Fail("无权修改该部门", "OUT_OF_SCOPE");
        if (department.RowVersion != expectedRowVersion) return Result.Fail("部门已被其他管理员修改，请刷新后重试", "CONCURRENCY_CONFLICT");
        if (department.ParentId is null && !isActive) return Result.Fail("不能停用根部门", "ROOT_DEPARTMENT");
        if (!isActive && await departments.ExistsChildAsync(id)) return Result.Fail("请先停用全部子部门", "HAS_ACTIVE_CHILD");
        if (!isActive && await departments.CountPrimaryUsersAsync(id) > 0) return Result.Fail("仍有活跃用户以该部门为主部门", "HAS_ACTIVE_USER");
        var before = new { department.Name, department.IsActive };
        department.IsActive = isActive;
        department.UpdatedAt = clock.UtcNow;
        department.RowVersion++;
        await audit.WriteAsync(uow.Db, clock, currentUser.GetAuditContext(), isActive ? "org.department.enable" : "org.department.disable", "Department", id, before,
            new { department.Name, department.IsActive });
        try
        {
            await uow.SaveChangesAsync();
            return Result.Ok();
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Fail("部门已被其他管理员修改，请刷新后重试", "CONCURRENCY_CONFLICT");
        }
    }

    public async Task<OrganizationSummary> GetSummaryAsync()
    {
        var all = await GetDepartmentsAsync();
        var allowedUserDepartments = currentUser.HasPermission(Permissions.UserRead)
            ? await scope.GetAccessibleDepartmentIdsAsync(Permissions.UserRead)
            : [];
        var counts = await users.GetCountsAsync(uow.Db, allowedUserDepartments);
        var roleCount = currentUser.HasPermission(Permissions.RoleAssign) ? await roles.CountAsync(uow.Db) : 0;
        return new OrganizationSummary(all.Count, counts.Total, counts.Active, roleCount);
    }
}
