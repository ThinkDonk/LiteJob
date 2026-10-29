# LiteJob · A Lightweight Single-Machine Task Scheduler

[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4.svg)](https://dotnet.microsoft.com/)
[![Tests](https://img.shields.io/badge/tests-160%2F160-brightgreen.svg)](#testing)
[![SQLite](https://img.shields.io/badge/SQLite-WAL-003B57.svg)](https://www.sqlite.org/)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](./LICENSE)

[中文说明](README.md) · English

A .NET 8 single-process scheduled-task hub: **the web console and the scheduler run in one process**. Tasks are `cron + TaskType + Params(JSON)`; the bundled executors are `http` and `mail`. It ships with timeout/retry/alerting quality controls, credential encryption, audit trails, an open API and a maintenance loop — all on a single-file SQLite database, with no external database or message queue required.

---

## Table of Contents

- [Features](#features)
- [Architecture](#architecture)
- [Security Design](#security-design)
- [Screenshots](#screenshots)
- [Repository Layout](#repository-layout)
- [Quick Start](#quick-start)
- [Configuration](#configuration)
- [Task Model Cheat Sheet](#task-model-cheat-sheet)
- [Testing](#testing)
- [FAQ](#faq)
- [License and Acknowledgments](#license-and-acknowledgments)

---

## Features

**Scheduling core**

- One entry point for trigger expressions: 6-field cron (with seconds; a 5-field expression gets a leading `0` auto-padded), `?` normalized to `*`, and `once:yyyy-MM-dd HH:mm:ss` one-shot tasks (fire once at the deadline, then auto-disable)
- Ready-heap + wakeable loop (`SemaphoreSlim`): editing cron, enabling/disabling or triggering now **takes effect immediately** — no waiting for the current sleep to finish
- Restart recovery chain: orphaned `running` rows marked failed → expired `next_planned_at` caught up (missed periods merged into a single run, `trigger_at` keeps the original planned time) → expired once-tasks run once and auto-disable
- Per-iteration exception isolation: a single trigger failure is logged, never kills the loop; loop uptime is visible on the dashboard

**Executors and execution quality**

- `http` executor: method/url/headers/body/successContains assertion; headers and body support template variables (`{date}`, `{datetime}`, `{yyyyMMdd}`, any .NET format string) interpolated at trigger time
- `mail` executor: MailKit-based sending with To/Cc, HTML body, `file`/`http` attachment sources; a missing attachment is skipped when optional, otherwise the task fails; http attachments support API-Key direct reference and declarative login (fresh token per execution, never cached)
- Unified execution pipeline: per-attempt timeout (CTS), retry backoff 30s/2m/8m, per-task semaphore (a run that starts while the previous one is unfinished is recorded as `skipped` — a task never runs concurrently with itself)
- `$cred:name.key` credential references resolved at execution time; no API or page ever echoes the plaintext

**Web console (Razor Pages + vanilla JS)**

- Dashboard: task/run statistics, scheduler health (loop uptime, heap size, last error), recent failures, alert-channel health lights
- Tasks: list (filters/paging/running indicator), editor (real-time cron preview of the next 5 triggers, common templates, cron/once mode switch), enable/disable, run now, copy, delete, JSON import/export
- Run history: filter by task/status/time, detail view (result JSON and error), CSV export (UTF-8 BOM, opens directly in Excel)
- Credentials: AES-256-GCM encrypted storage, write-only values, self-check (returns key names only)
- Alert channels: WeCom / DingTalk (HMAC signing) / Feishu / custom webhook, test send, health lights (ok green / fail red — failures are recorded too)
- Settings: SMTP (with test connection) and maintenance policy; user management (admin/viewer); audit log (search by operator/action/time)

**Alerting**

- Policies: `final_failure` (default) / `consecutive` (N consecutive failures) / `never`; recovery notifications fire on the "last run failed → this run succeeded" edge and stay accurate across restarts
- Three-tier channel fallback: task-specified → system default channels → all enabled channels; parallel sending with a per-channel 5s timeout that never affects other channels

**Open API and engineering**

- Three `/open/api` endpoints (task upsert, run now, recent runs) guarded by `X-API-Key` with fixed-time comparison; Swagger exposes the open group only
- Audit hooks on every write operation (open-API writes recorded as user `open-api`); config values, credential values and passwords never enter the audit trail
- Maintenance loop (hourly): batched cleanup of run records/audit logs, daily `VACUUM INTO` backup (retention configurable), channel health inspection
- Versioned SQL migrations (`user_version` mechanism) executed automatically at startup; SQLite WAL + busy_timeout

## Architecture

```
Browser ──► LiteJob.Web (single process: ASP.NET Core)
            ├── Pages/           Razor console (dashboard/tasks/runs/credentials/channels/settings/audits/users)
            ├── Api/             Management REST (JWT) + /open/api (X-API-Key)
            ├── Middlewares/     ApiKeyMiddleware (intercepts /open only)
            └── Program.cs       DI composition + migrations + AdminSeeder
                     │
            LiteJob.Core (class library, no ASP.NET Core dependency)
            ├── Scheduling/      Ready-heap + wakeable loop + restart recovery (TriggerCalc entry point)
            ├── Executors/       Pipeline + HttpTaskExecutor + MailTaskExecutor +
            │                     MailAttachmentCollector + HttpAuthHelper (declarative login)
            ├── Security/        CredentialResolver ($cred) / CryptoService (AES-256-GCM) /
            │                     Masker + SensitiveValueBag / ApiKeyComparer / LoginThrottler
            ├── Notify/          Four-platform sender + policy evaluation
            ├── Repositories/    Task/Run/Config/Credential/NotifyChannel/AuditLog/User (hand-written Dapper SQL)
            ├── Db/              Connection factory (WAL/busy_timeout) + MigrationRunner (user_version)
            ├── Hosting/         MaintenanceService (cleanup/backup/inspection)
            └── Tasks/           TaskImportExportService
                     │
                SQLite (data/litejob.db, WAL)
```

Why the three critical paths are designed this way:

1. **Trigger path**: the scheduler sleeps on the ready-heap until the next trigger; every write operation (cron edit / enable-disable / run now) only does "remove + re-insert + release the semaphore", so the loop wakes up and re-reads the heap top — changes take effect without a restart and without polling intervals.
2. **Execution path**: everything (scheduled / manual / caught-up) goes through one pipeline: insert a `running` row → execute attempt-by-attempt with independent timeouts → persist the final state and update `last_run_success` → alert per policy on failure. A per-task semaphore guarantees a task never runs concurrently with itself; lock contention records `skipped` instead of queueing.
3. **Notification path**: the pipeline tail calls `INotifyDispatcher` per `notify_policy`. A failed delivery only affects the channel health light (`fail:<reason>`) and never propagates back into the task result — scheduling correctness and alert deliverability are decoupled.

## Security Design

| Dimension | Approach |
| --- | --- |
| Authentication | Admin console: username/password (BCrypt cost=10) + JWT HS256 8h; open API: independent `X-API-Key` (`FixedTimeEquals`; length mismatch still performs an equivalent-hash comparison) |
| Authorization | Write operations `[Authorize(Roles="admin")]` enforced server-side; viewer is read-only (server-side 403, not frontend hiding); you cannot disable/demote/delete yourself or the last enabled admin |
| Login safety | Same-username lockout after 5 consecutive failures (15 min, in-memory counter); success/failure/lockout all audited |
| Credentials | Credential values, SMTP passwords and channel secrets are always AES-256-GCM ciphertext (`base64(nonce‖cipher‖tag)`); APIs/pages are write-only, verify returns key names only |
| Masking | `$cred` values and auth tokens are masked in run-record request headers, response snapshots and auth previews (mask first, truncate after) |
| Audit | Hooks on every write (including the open API, recorded as `open-api`); config/credential values and passwords never enter the audit trail; 90-day retention via the maintenance loop |
| Data | Attachment bodies and full response bodies are never stored; result/error truncated to 2000 chars; SQLite files and backups are excluded from version control |

## Screenshots

Login page:

![Login](screenshots/01-login.png)

Dashboard (task statistics, scheduler health, recent failures, alert-channel health lights):

![Dashboard](screenshots/02-dashboard.png)

Task list (filters, running indicator, row actions, import/export):

![Task list](screenshots/03-tasks.png)

Task editor (real-time cron preview of the next 5 triggers, common templates, executor params, execution quality, notification policy):

![Task editor](screenshots/04-task-edit.png)

Run history (filters, status badges, durations, CSV export):

![Run history](screenshots/05-runs.png)

Alert channels (health lights, test send):

![Alert channels](screenshots/06-channels.png)

> Screenshots use local demo data. Secret-type configuration in this repository is development placeholder values and must be replaced before deployment (see [Configuration](#configuration)).

## Repository Layout

```
LiteJob.sln              Solution (Core / Web / Core.Tests)
LiteJob.Core/            Class library (no ASP.NET Core dependency, unit-testable)
  Scheduling/            Core: TriggerCalc, SimpleTaskScheduler (ready-heap + wakeable loop)
  Executors/             ITaskExecutor, ExecutionPipeline, HttpTaskExecutor, MailTaskExecutor,
                         MailAttachmentCollector, HttpAuthHelper (declarative login)
  Security/              CredentialResolver ($cred), CryptoService (AES-256-GCM),
                         Masker / SensitiveValueBag (masking), ApiKeyComparer, LoginThrottler
  Notify/                INotifyDispatcher, RealNotifyDispatcher, NotifySender (four platforms)
  Repositories/          Task/Run/Config/Credential/NotifyChannel/AuditLog/User (hand-written Dapper SQL)
  Db/                    SqliteConnectionFactory (WAL/busy_timeout), MigrationRunner (user_version)
  Scripts/               V1__init.sql, V2__*.sql (embedded migration scripts)
  Hosting/               MaintenanceService (cleanup/backup/inspection)
  Tasks/                 TaskImportExportService
LiteJob.Web/             Host: Program.cs composition, Api/ controllers, Pages/ Razor pages, Middlewares/
LiteJob.Core.Tests/      xunit tests (160)
screenshots/             README screenshots
```

## Quick Start

### 0. Prerequisites

- .NET 8 SDK (the projects target net8.0)
- No external database: SQLite is created in-process; no message queue needed

### 1. Build and test

```bash
dotnet build LiteJob.sln --no-incremental   # 0 warnings 0 errors
dotnet test LiteJob.sln                     # 160/160
```

> Do not omit `--no-incremental`: an incremental build can mask compiler warnings.

### 2. Run

```bash
cd LiteJob.Web
dotnet run
# → http://localhost:5270
```

The first startup automatically: creates the database and runs migrations (advancing `user_version`) → creates the default `admin` user → **prints the initial password once as a `LogWarning` in the startup log** → loads enabled tasks into the scheduler. Log in with that password and change it in user management as soon as possible.

### 3. Three minimal verification paths

```bash
# a. HTTP task: create a task pointing at a reachable API; a success/failed row
#    appears in the run history at the trigger time
# b. Cron change takes effect immediately: edit a task's cron; the next trigger
#    follows the new expression — no restart needed
# c. Mail task: fill in SMTP under Settings → create a mail task
#    (to/subject/body, attachments optional) → run now
```

## Configuration

Configuration lives in `LiteJob.Web/appsettings.json` and can be overridden with environment variables (e.g. `ConnectionStrings__sqlite`, `Security__JwtKey`):

**Basics**

| Key | Default | Description |
| --- | --- | --- |
| `ConnectionStrings:sqlite` | `Data Source=data/litejob.db` | SQLite connection string (relative to the app working directory) |
| `Swagger:Enabled` | `true` | Swagger UI toggle (exposes the `/open/api` group only) |

**Security (placeholder values in this repository — replace before deployment)**

| Key | Description |
| --- | --- |
| `Security:JwtKey` | JWT HS256 signing key, at least 64 random characters |
| `Security:AesKey` | Credential/password encryption key, 32-byte base64 (AES-256-GCM) |
| `Security:ApiKey` | Open-API credential (sent in the `X-API-Key` header) |

Other business settings (SMTP, maintenance policy) are maintained on the Settings page and stored in the `t_config` table; password-type keys are write-only.

## Task Model Cheat Sheet

A task is `cron + taskType + params(JSON)`:

| Field | Description |
| --- | --- |
| `cron` | 6-field cron (with seconds; a 5-field expression is auto-padded); `?` normalized to `*`; or `once:yyyy-MM-dd HH:mm:ss` |
| `taskType` | `http` / `mail` (executor registry, registered at compile time) |
| `params` | Executor parameter JSON; string values support `{date}`/`{datetime}`/.NET format strings interpolated at trigger time, and `$cred:name.key` credential references |
| `timeoutSec` / `retryMax` | Per-attempt timeout (seconds) / retry count (excluding the first attempt; backoff 30s/2m/8m) |
| `notifyPolicy` | `{"notifyOn":"final_failure\|consecutive\|never","count":N,"channels":[id]}` |

Run-record state machine: `running → success / failed`; a lock-contention loss records `skipped` (it does not touch `last_run_success` and never alerts).

## Testing

```bash
dotnet test LiteJob.sln        # 160/160
```

Coverage highlights:

- Scheduling core: trigger computation (5/6 fields, `once`, invalid), wakeable changes, restart recovery (orphan cleanup / merged misfire catch-up / once auto-disable), concurrent `skipped`
- Execution pipeline: retry and backoff, timeout, cancellation, terminal-state assertions (poll to the final state, never depend on wall-clock time)
- Executors: HTTP success/assertion failure/non-2xx, `$cred` resolution, attachment optional semantics (missing file, http 404), declarative login (JsonPath token extraction, template injection), masking matrix
- Security: AES-256-GCM round-trip, ApiKey fixed-time comparison, login lockout
- Repositories: paged filtering (half-open time ranges), today's statistics, import/export (round-trip + credential reference validation), audit queries, user-management boundaries

Milestone-based integration acceptance (real browser + real SMTP + webhook capture) is executed round by round; methodology and evidence are archived in a local test knowledge base.

## FAQ

**Q: The admin password is printed only once in the startup log and I missed it. What now?**
Delete `data/litejob.db` (or the whole data directory) and restart to regenerate — AdminSeeder only creates the user when `t_user` is empty. If you have data you want to keep, modify the database directly or reset via user management in a later version.

**Q: I changed the cron but nothing happened.**
Immediate effect is core behavior (a write wakes the loop). If it seems ineffective, first check whether `trigger_at` in the run records follows the new expression, then confirm the task is enabled (disabled removes it from the heap).

**Q: A task failed but no alert arrived.**
Check in order: ① is the task's `notifyPolicy` `never`; ② did you specify channels that are now disabled (a disabled specified channel = no send); ③ is the channel health light `fail:...` (webhook unreachable); ④ if all are fine, channel selection falls back to "all enabled channels" — with none configured, nothing is sent.

**Q: Email is not going out.**
Click "Test connection" on the Settings page first (connect + authenticate only, no send); confirm `smtp.from` is non-empty and the port/SSL switch matches (465 = TLS on connect, 587 = STARTTLS). A failed attachment download also fails the whole task — check optional on http attachments to tolerate 404.

**Q: Where did the database file go? / Can I commit the database?**
Runtime data (`*.db`, `backup/`) is excluded via `.gitignore`. Backups are produced daily by the maintenance loop via `VACUUM INTO` into `backup/`, retained per the configured count.

**Q: What time zone is used?**
SQLite time columns are local-time TEXT (`yyyy-MM-dd HH:mm:ss`), lexicographic order equals chronological order; cron is interpreted in the server's local time zone. The `nextTriggerAt` returned by the API is local time in `yyyy-MM-dd HH:mm:ss`.

## License and Acknowledgments

This project is open source under the [MIT License](./LICENSE) — free to use, modify and commercialize (keep the copyright notice).

Standing on the shoulders of:

- [Cronos](https://github.com/HangfireIO/Cronos) — cron expression parsing
- [Dapper](https://github.com/DapperLib/Dapper) · [Microsoft.Data.Sqlite](https://learn.microsoft.com/sqlite/)
- [MailKit](https://github.com/jstedfast/MailKit) — SMTP sending
- [BCrypt.Net-Next](https://github.com/BcryptNet/bcrypt.net) · [Swashbuckle.AspNetCore](https://github.com/domaindrivendev/Swashbuckle.AspNetCore)
- [ASP.NET Core](https://learn.microsoft.com/aspnet/core/) · [xunit](https://xunit.net/)
