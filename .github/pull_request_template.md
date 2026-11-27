## 变更摘要

<!-- 用 1–3 条说明为什么需要这次变更，以及做了什么。 -->

## 验证方式

<!-- 列出实际执行过的命令、结果和未完成的验证。 -->

- [ ] `dotnet build ExamOps.sln --configuration Release` 零警告、零错误
- [ ] `--self-check` 通过
- [ ] `--import-parser-check` 通过
- [ ] 涉及数据库变更时，`sql/schema.sql` 与 `sql/smoke_test.sql` 已同步并验证
- [ ] 未提交秘密、`bin/`、`obj/`、`Data/` 或本地配置

## 数据库与回滚

<!-- 无数据库影响请写“无”；否则说明 DDL、数据迁移和回滚步骤。 -->

## 安全说明

<!-- 是否涉及认证、授权、上传、秘密、审计或公开披露？没有请写“无”。 -->
