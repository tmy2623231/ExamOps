using ExamOps.DAL;
using ExamOps.DAL.Repositories;
using ExamOps.Model.Common;
using ExamOps.Model.Dtos;
using ExamOps.Model.Entities;
using Microsoft.AspNetCore.Identity;

namespace ExamOps.BLL;

public sealed class AdminBootstrap(
    IUnitOfWork uow,
    UserRepository users,
    AuditRepository audit,
    IClock clock,
    PasswordHasher<AppUser> passwordHasher,
    IHostEnvironment environment)
{
    public async Task<Result> InitializeAsync()
    {
        var password = Environment.GetEnvironmentVariable("EXAMOPS_ADMIN_PASSWORD");
        if (string.IsNullOrWhiteSpace(password))
            return Result.Fail("请设置 EXAMOPS_ADMIN_PASSWORD 环境变量", "PASSWORD_REQUIRED");
        if (password.Length is < 12 or > 128) return Result.Fail("管理员密码需为 12–128 个字符", "INVALID_PASSWORD");
        var user = await users.GetByUsernameTrackedAsync(uow.Db, "admin");
        if (user is null) return Result.Fail("尚未执行 seed.sql", "ADMIN_NOT_FOUND");
        var changed = !string.IsNullOrEmpty(user.PasswordHash) && !user.MustChangePassword;
        user.PasswordHash = passwordHasher.HashPassword(user, password);
        user.MustChangePassword = false;
        user.Status = "Active";
        user.UpdatedAt = clock.UtcNow;
        user.RowVersion++;
        await audit.WriteAsync(uow.Db, clock, new AuditContext(user.Id, "PlatformAdmin", "bootstrap", "local", environment.EnvironmentName),
            "system.init_admin", "AppUser", user.Id, after: new { Initialized = true, Changed = changed });
        await uow.SaveChangesAsync();
        return Result.Ok();
    }
}
