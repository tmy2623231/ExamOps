# ExamOps · 企业级在线考试运营平台

> 基于 **.NET 8 Blazor Server + EF Core 8 + PostgreSQL 17（SQL-first）** 构建的严谨、轻量、零第三方运行时依赖的在线考试运营系统。

---

## 核心特性

- **严谨范围与自动判分**：单组织 + 多部门树（物化路径），聚焦客观题（**单选 / 多选 / 判断**），交卷即精确自动判分；支持异常复核留痕、调分历史与不可变成绩发布快照。
- **权限感知实时运营看板 (`/`)**：按当前登录角色与数据范围实时聚合进行中考试、完成进度、待复核答卷与及格率；内置滚动数字 KPI、可交互重分环形图、考中催考进度抽屉与全局 `Ctrl+K` 快捷命令面板。
- **极简运营体验**：
  - **组织架构模板 (`/org/departments`)**：内置安全生产/制造业、互联网研发、院校培训、极简扁平 4 套行业组织模板，单事务幂等套用。
  - **员工 Excel 批量导入与岗位预设 (`/org/users`)**：下载 `.xlsx` 模板批量建号并自动生成 13 位强密码发号清单；4 套直观岗位预设一键配置 RBAC 角色。
  - **极速发考四步向导 (`/exams/quick-publish`)**：选择或站内直接上传 `.xlsx` 题库自动发布 → 勾选固定/随机出题规则 → 快捷时间预设 → 全景预览并在单一事务内完成建卷、发卷、建任务、发布与下发。
  - **扫码开考与一键复发**：纯本地离线生成 SVG 二维码与直达高亮链接；历史考试支持一键复制配置快速重发。
  - **纯考生极简免打扰模式**：仅具备考生角色的账号自动隐藏全部管理侧边栏入口，聚焦待考与成绩查询。
- **生产级安全基线**：
  - **即时 Blazor Circuit 下线**：停用账号或变更权限后，内存瞬时广播 + 4 秒数据库探针双保险确保 5 秒内强制下线并拒绝旧 Cookie。
  - **生产密钥门禁**：生产环境强制通过环境变量 `EXAMOPS_DB_CONNECTION` 或 `ConnectionStrings__App` 注入，启动时拦截空密码、占位符与 `postgres` 超级用户。
  - **全链路审计与 SQL-first**：所有业务写操作同事务落审计日志与 Outbox；append-only 审计/事件/快照表受 PostgreSQL 触发器与最小权限角色保护。
  - **浅/深双色 Swiss 理性深蓝主题**：支持顶栏昼夜主题切换、375px 移动端适配与 `prefers-reduced-motion` 无障碍降级。

---

## 目录结构

```text
ExamOps/
├─ Api/                          # 鉴权导出、模板下载与 REST 端点
├─ BLL/                          # 业务服务、校验器与编排事务
├─ Components/                   # Pages / Layout / Shared (.razor + .razor.css + .razor.js)
├─ DAL/                          # ExamDbContext / Configurations / Repositories / UnitOfWork
├─ Infra/                        # 认证鉴权、安全存储、日志、后台任务、自检与 UI 服务
├─ Model/                        # Entities / Dtos / Enums / Common
├─ sql/                          # schema.sql / seed.sql / security.sql / smoke_test.sql
├─ wwwroot/                      # 静态 CSS 设计令牌与官方题目导入模板
├─ appsettings.example.json      # 配置示例（真实配置不入库）
└─ README.md
```

---

## 快速开始

### 1. 初始化 PostgreSQL 17 数据库

创建数据库 `examops_db` 与最小权限应用角色 `examops_app` 后，依次执行 SQL-first 脚本：

```bash
psql -h 127.0.0.1 -U postgres -d examops_db -v ON_ERROR_STOP=1 -f sql/schema.sql
psql -h 127.0.0.1 -U postgres -d examops_db -v ON_ERROR_STOP=1 -f sql/seed.sql
psql -h 127.0.0.1 -U postgres -d examops_db -v ON_ERROR_STOP=1 -f sql/security.sql
```

### 2. 配置连接串与初始化管理员

复制 `appsettings.example.json` 为本地 `appsettings.json`（已被 `.gitignore` 忽略），或通过环境变量注入：

```powershell
$env:EXAMOPS_DB_CONNECTION = "Host=localhost;Port=5432;Database=examops_db;Username=examops_app;Password=<YOUR_PASSWORD>;SSL Mode=Disable"
$env:EXAMOPS_ADMIN_PASSWORD = "<12位以上强密码>"

dotnet run --project ExamOps.csproj -- --init-admin
```

### 3. 启动应用

```powershell
dotnet run --project ExamOps.csproj --urls http://localhost:5080
```

浏览器访问 `http://localhost:5080`，使用 `admin` 与刚设置的管理员密码登录。

---

## 内置自检

```powershell
# 零警告构建
dotnet build ExamOps.sln

# 离线自检与 XLSX 解析自检
dotnet run --no-build --project ExamOps.csproj -- --self-check
dotnet run --no-build --project ExamOps.csproj -- --import-parser-check

# Phase 1–5 数据库事务自检（全部 fixture 自动回滚，不污染数据）
dotnet run --no-build --project ExamOps.csproj -- --phase1-check
dotnet run --no-build --project ExamOps.csproj -- --phase2-check
dotnet run --no-build --project ExamOps.csproj -- --phase3-check
dotnet run --no-build --project ExamOps.csproj -- --phase4-check
dotnet run --no-build --project ExamOps.csproj -- --phase5-check
```

---

## 致谢与许可

- 部分界面交互动效移植自 [Lucian UX/UI Prompt](https://lucianlabs.dev/uxui-prompt/)（MIT License），各对应组件顶部均保留原始许可证注释。
