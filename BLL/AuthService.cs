using System.Security.Claims;
using ExamOps.DAL;
using ExamOps.DAL.Repositories;
using ExamOps.Model.Common;
using ExamOps.Model.Dtos;
using ExamOps.Model.Entities;
using Microsoft.AspNetCore.Identity;

namespace ExamOps.BLL;

public sealed record AuthenticatedUser(long Id, string Username, string DisplayName, long RowVersion, bool MustChangePassword);

public sealed class AuthService(
    IUnitOfWork uow,
    UserRepository users,
    AuditRepository audit,
    ICurrentUser currentUser,
    LoginThrottleService throttle,
    IClock clock,
    PasswordHasher<AppUser> passwordHasher)
{
    public async Task<Result<AuthenticatedUser>> LoginAsync(string username, string password, string? ip, string? userAgent)
    {
        username = username.Trim();
        ip ??= "unknown";
        if (throttle.IsBlocked(username, ip))
            return Result<AuthenticatedUser>.Fail("登录尝试过于频繁，请 5 分钟后再试", "LOGIN_THROTTLED");

        var user = await users.GetByUsernameTrackedAsync(uow.Db, username.ToLowerInvariant());
        var context = new AuditContext(user?.Id, null, null, ip, userAgent);
        if (user is null || string.IsNullOrEmpty(user.PasswordHash)
            || passwordHasher.VerifyHashedPassword(user, user.PasswordHash, password) != PasswordVerificationResult.Success)
        {
            throttle.RecordFailure(username, ip);
            await audit.WriteAsync(uow.Db, clock, context, "auth.login_failed", "AppUser", user?.Id ?? 0, after: new { Username = username });
            await uow.SaveChangesAsync();
            return Result<AuthenticatedUser>.Fail("账号或密码不正确", "INVALID_CREDENTIALS");
        }
        if (user.Status != "Active")
        {
            throttle.RecordFailure(username, ip);
            await audit.WriteAsync(uow.Db, clock, context, "auth.login_disabled", "AppUser", user.Id, after: new { user.Username });
            await uow.SaveChangesAsync();
            return Result<AuthenticatedUser>.Fail("该账号已停用，请联系管理员", "ACCOUNT_DISABLED");
        }

        user.LastLoginAt = clock.UtcNow;
        await audit.WriteAsync(uow.Db, clock, context, "auth.login", "AppUser", user.Id, after: new { user.Username, user.LastLoginAt });
        await uow.SaveChangesAsync();
        throttle.Reset(username, ip);
        return Result<AuthenticatedUser>.Ok(new AuthenticatedUser(user.Id, user.Username, user.DisplayName, user.RowVersion, user.MustChangePassword));
    }

    public ClaimsPrincipal CreatePrincipal(AuthenticatedUser user)
    {
        var claims = new[]
        {
            new Claim("examops_user_id", user.Id.ToString()),
            new Claim(ClaimTypes.NameIdentifier, user.Username),
            new Claim(ClaimTypes.Name, user.DisplayName),
            new Claim("examops_row_version", user.RowVersion.ToString())
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "ExamOps.Cookie"));
    }

    public async Task<Result> ChangePasswordAsync(long userId, string currentPassword, string newPassword)
    {
        if (newPassword.Length is < 12 or > 128) return Result.Fail("新密码需为 12–128 个字符", "INVALID_PASSWORD");
        var user = await users.GetTrackedAsync(uow.Db, userId);
        if (user is null) return Result.Fail("用户不存在", "NOT_FOUND");
        if (passwordHasher.VerifyHashedPassword(user, user.PasswordHash, currentPassword) != PasswordVerificationResult.Success)
            return Result.Fail("当前密码不正确", "INVALID_CURRENT_PASSWORD");
        if (passwordHasher.VerifyHashedPassword(user, user.PasswordHash, newPassword) == PasswordVerificationResult.Success)
            return Result.Fail("新密码不能与当前密码相同", "PASSWORD_UNCHANGED");
        user.PasswordHash = passwordHasher.HashPassword(user, newPassword);
        user.MustChangePassword = false;
        user.UpdatedAt = clock.UtcNow;
        user.RowVersion++;
        await audit.WriteAsync(uow.Db, clock, currentUser.GetAuditContext(), "account.password_change", "AppUser", userId,
            after: new { Changed = true });
        await uow.SaveChangesAsync();
        return Result.Ok();
    }

    public async Task LogoutAsync()
    {
        await currentUser.LoadAsync();
        if (currentUser.Id is { } userId)
        {
            await audit.WriteAsync(uow.Db, clock, currentUser.GetAuditContext(), "auth.logout", "AppUser", userId);
            await uow.SaveChangesAsync();
        }
    }
}
