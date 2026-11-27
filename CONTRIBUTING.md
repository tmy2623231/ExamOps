## 参与方式

感谢你帮助改进 ExamOps。提交变更前请先确认问题是否已有对应 Issue；较大的功能或架构调整建议先讨论再实现。

### 本地开发

1. 安装 .NET 8 SDK 与 PostgreSQL 17。
2. 按 `README.md` 初始化数据库和本地配置。
3. 复制 `appsettings.example.json` 为本地 `appsettings.json`，不要提交该文件。
4. 执行以下检查：

```powershell
dotnet restore ExamOps.sln
dotnet build ExamOps.sln --configuration Release --no-restore
dotnet run --no-build --configuration Release --project ExamOps.csproj -- --self-check
dotnet run --no-build --configuration Release --project ExamOps.csproj -- --import-parser-check
```

涉及数据库变更时，还必须更新 `sql/schema.sql` 与 `sql/smoke_test.sql`，并在临时数据库中执行验证。项目采用 SQL-first，禁止同时引入 EF Migrations。

### 变更约定

- 保持 `Components / Api → BLL → DAL → Model` 的单向依赖。
- 只读查询使用 `AsNoTracking()`；分页查询必须带唯一列兜底排序。
- 写操作必须记录审计日志，跨表写入必须有明确事务边界。
- 禁止提交真实连接串、密码、令牌、个人数据或业务导出文件。
- 禁止提交 `bin/`、`obj/`、`Data/`、`publish/` 与本地 IDE 配置。
- 保持 `dotnet build` 零警告、零错误。
- 修改数据库结构或核心业务规则时，补充可重复执行的验证步骤。

### Pull Request

请使用 Pull Request 模板，说明变更目的、验证方式、数据库影响和回滚方式。涉及安全修复时，请不要在公开 PR 中披露尚未修复的漏洞细节。
