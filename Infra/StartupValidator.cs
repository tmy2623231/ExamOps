using ExamOps.DAL;
using Microsoft.EntityFrameworkCore;

namespace ExamOps.Infra;

public sealed class StartupValidator(IDbContextFactory<ExamDbContext> factory)
{
    public async Task ValidateAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        if (!await db.Database.CanConnectAsync())
            throw new InvalidOperationException("数据库连接失败，应用拒绝启动。");
        var version = await db.Database.SqlQuery<string>($"SELECT schema_version AS \"Value\" FROM schema_meta WHERE id = 1").SingleOrDefaultAsync();
        if (version != "1.1.0")
            throw new InvalidOperationException($"数据库 schema 版本不匹配：当前 {version ?? "缺失"}，要求 1.1.0。请先执行 schema.sql、security.sql、seed.sql。");
        var departmentExists = await db.Database.SqlQuery<bool>($"SELECT to_regclass('public.department') IS NOT NULL AS \"Value\"").SingleAsync();
        if (!departmentExists) throw new InvalidOperationException("缺少 department 表，应用拒绝启动。");
    }
}
