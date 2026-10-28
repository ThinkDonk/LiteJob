# AGENTS.md — LiteJob AI 协作规范

> 面向在本仓库工作的 AI 代理与开发者的项目约定。违反这些约定会破坏系统的隐式设计。

## 项目是什么

单机轻量定时任务调度中心（.NET 8 单进程：Web 管理端 + 调度器）。任务模型 `cron + TaskType + Params(JSON)`，
首发执行器 http/mail（邮件属 M4）。业务背景：服务对象为 WMS 2.0，示例中的 wms/api 均为业务事实，**但本项目
命名与 WMS 无关**。完整设计见 `docs/LiteJob-详细设计.md`（该目录不参与 git 提交，本地文件）。

## 仓库纪律

- `docs/` 不参与提交（.gitignore 已排除）；数据文件同样不入库：`*.db` `*.db-shm` `*.db-wal` `TestResults/` `bin/` `obj/`
- 里程碑对应提交：M1 = 骨架（208f0f3），M2 = 调度内核+HTTP 执行器（2890b82）；后续里程碑照此粒度提交
- git 写操作仅在用户明确授权后执行

## 目录导览

```
LiteJob.Core/           类库（不依赖 ASP.NET Core，可单测）
  Scheduling/           调度内核：TriggerCalc（触发表达式统一入口）、SimpleTaskScheduler（就绪堆+可唤醒循环）
  Executors/           ITaskExecutor、ExecutorRegistry、ExecutionPipeline（统一管道）、HttpTaskExecutor
  Interpolate/          TemplateEngine（{date}/{datetime}/{.NET格式串} 插值）
  Notify/               INotifyDispatcher（当前 Noop 实现，M5 换真实现）
  Repositories/          TaskRepository、RunRepository（Dapper 手写 SQL）
  Db/                    SqliteConnectionFactory（WAL/busy_timeout）、MigrationRunner（user_version 机制）
  Scripts/               V1__init.sql、V2__*.sql（内嵌资源迁移脚本）
  Security/              LoginThrottler（登录失败锁定）
LiteJob.Web/            宿主：Program.cs 组装、Api/ 控制器、Pages/ Razor 页面
LiteJob.Core.Tests/     xunit 单测（当前 67 项，全绿为交付基线）
```

## 开发约定（隐式契约）

- **时间**：SQLite 列 TEXT，本地时间 `yyyy-MM-dd HH:mm:ss`（记录类带 .fff）；ISO 字符串字典序即时间序，排序/比较直接按字符串
- **触发表达式**：6 字段 cron 含秒为准（5 字段自动补前导 0）；`?` 归一化为 `*`；`once:yyyy-MM-dd HH:mm:ss` 一次性触发，到期后 enabled 自动置 0；Cronos 返回 UTC，使用处必须 `.ToLocalTime()`
- **DDL 变更**：只新增 `Scripts/VN__xxx.sql`，禁止修改历史脚本；启动时 MigrationRunner 自动执行（user_version 前进）
- **实体映射**：C# PascalCase ↔ SQLite 下划线列，靠 `Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true`（SqliteConnectionFactory 静态构造设置），新实体自动受益
- **调度三纪律**（改内核前必读 §3.1）：循环单次迭代异常隔离（try-catch 不得逃逸）；等待用 SemaphoreSlim 可唤醒（改 cron/启停立即生效）；每任务 `SemaphoreSlim(1,1)` 不并发，TryEnter 失败记 `skipped`
- **认证**：JWT HS256 8h，Authorization header 优先、`lj_token` HttpOnly cookie 兜底；写操作 `[Authorize(Roles="admin")]`；登录审计写 t_audit_log（业务审计埋点属 M6）
- **执行质量**：超时 CTS 在统一管道（ExecutionPipeline）不在执行器；重试退避 30s/2m/8m，退避函数构造可注入（测试用毫秒级）；result/error 截断 2000 字符
- **凭据**：任何密钥/密码不明文入库（AES-256-GCM，M4 起实现；现有 JwtKey/AesKey/ApiKey 在 appsettings，部署必须更换）

## 常用命令

```powershell
dotnet build LiteJob.sln          # 0 警告 0 错误为基线
dotnet test LiteJob.sln            # 67/67 全绿为基线
cd LiteJob.Web; dotnet run         # http://localhost:5270（admin 初始密码见首启日志）
```

## 里程碑

M1 ✓ 骨架 · M2 ✓ 调度内核+HTTP · M3 ✓ 管理页（任务列表/编辑+cron预览与复制/记录+CSV导出/仪表盘） · M4 邮件执行器+凭证 · M5 告警通知 · M6 开放 API+审计埋点+导入导出+Swagger

## 交付门槛

任何代码改动后：`dotnet build` 0 警告 0 错误 **且** `dotnet test` 全绿才可交付；涉及时序的用例用短周期 cron 或注入退避，禁止真实长等待。
