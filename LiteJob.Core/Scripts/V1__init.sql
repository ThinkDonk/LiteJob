-- 任务定义
CREATE TABLE IF NOT EXISTS t_task (
    id               INTEGER PRIMARY KEY AUTOINCREMENT,
    name             TEXT    NOT NULL,
    group_name       TEXT    NOT NULL DEFAULT 'default',
    task_type        TEXT    NOT NULL DEFAULT 'http',      -- http | mail
    cron             TEXT    NOT NULL,               -- cron 表达式，或 "once:2026-10-29 10:00"（一次性任务）
    params           TEXT    NOT NULL DEFAULT '{}',        -- 执行器参数 JSON
    timeout_sec      INTEGER NOT NULL DEFAULT 30,
    retry_max        INTEGER NOT NULL DEFAULT 3,           -- 重试次数（不含首次）
    enabled          INTEGER NOT NULL DEFAULT 1,
    last_run_success INTEGER,                              -- 上次最终结果 1/0，恢复通知判据
    notify_policy    TEXT    NOT NULL DEFAULT '{}',         -- {"notifyOn":"final_failure","channels":[]}
    remark           TEXT,
    created_by       TEXT,
    updated_by       TEXT,
    created_at       TEXT    NOT NULL,
    updated_at       TEXT    NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_task_name ON t_task(name);
CREATE INDEX IF NOT EXISTS idx_task_enabled ON t_task(enabled);

-- 执行记录（唯一高频写入方）
CREATE TABLE IF NOT EXISTS t_task_run (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    task_id     INTEGER NOT NULL,
    task_name   TEXT    NOT NULL,                -- 冗余存储，任务删除后记录仍有语义
    trigger_at  TEXT    NOT NULL,                -- 应触发时间（补跑时为原计划时间）
    started_at  TEXT    NOT NULL,
    finished_at TEXT,
    status      TEXT    NOT NULL,                -- running | success | failed | skipped
    attempt     INTEGER NOT NULL DEFAULT 1,       -- 第几次尝试（1=首次）
    duration_ms INTEGER,
    result      TEXT,                            -- 结果摘要 JSON（http code/附件清单/SMTP应答）
    error       TEXT,                            -- 错误摘要（脱敏后）
    create_at   TEXT    NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_run_task ON t_task_run(task_id, id DESC);
CREATE INDEX IF NOT EXISTS idx_run_time ON t_task_run(create_at);

-- 用户
CREATE TABLE IF NOT EXISTS t_user (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    username      TEXT    NOT NULL,
    password_hash TEXT    NOT NULL,               -- BCrypt cost=10
    role          TEXT    NOT NULL DEFAULT 'viewer', -- admin | viewer
    enabled       INTEGER NOT NULL DEFAULT 1,
    created_at    TEXT    NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_user_name ON t_user(username);

-- 凭证（值整体 AES-256-GCM 加密存储）
CREATE TABLE IF NOT EXISTS t_credential (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    name       TEXT    NOT NULL,                  -- 引用名，如 wms-report
    value_enc  TEXT    NOT NULL,                  -- 密文 = base64(nonce || cipher || tag)
                                                        -- 明文为 JSON 键值对象 {"key":"...","user":"...","pass":"..."}
    remark     TEXT,
    created_at TEXT    NOT NULL,
    updated_at TEXT    NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_cred_name ON t_credential(name);

-- 告警通道
CREATE TABLE IF NOT EXISTS t_notify_channel (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    name          TEXT    NOT NULL,
    type          TEXT    NOT NULL,               -- wecom | dingtalk | feishu | custom
    webhook_url   TEXT    NOT NULL,
    secret        TEXT,                          -- 钉钉加签密钥（AES 加密存储）
    body_template TEXT,                          -- custom 类型专用，支持 {title} {body} 插值
    at_mobiles    TEXT,                          -- @人（企微/钉钉），逗号分隔手机号
    enabled       INTEGER NOT NULL DEFAULT 1,
    last_status   TEXT,                          -- 最近一次发送 ok | fail:<error>
    last_sent_at  TEXT,
    created_at    TEXT    NOT NULL
);

-- 系统配置（键值）
CREATE TABLE IF NOT EXISTS t_config (
    key   TEXT PRIMARY KEY,
    value TEXT NOT NULL
);
-- 约定键：
--   smtp.host / smtp.port / smtp.ssl / smtp.user / smtp.pass(AES) / smtp.from / smtp.from_name
--   maintenance.run_retention_days=30 / maintenance.backup_keep=7
--   notify.default_channels        逗号分隔通道 id，任务未指定时使用

-- 操作审计日志
CREATE TABLE IF NOT EXISTS t_audit_log (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    username    TEXT    NOT NULL,                   -- 操作者（开放 API 记为 "open-api"）
    action      TEXT    NOT NULL,                   -- login|create|update|delete|enable|disable|run|copy|import|export
    target_type TEXT    NOT NULL,                   -- task|credential|channel|user|config
    target_id   INTEGER,
    target_name TEXT,                              -- 冗余名称，目标删除后仍有语义
    detail      TEXT,                              -- 变更摘要 JSON（脱敏；删除/导出可空）
    ip          TEXT,
    create_at   TEXT    NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_audit_time ON t_audit_log(create_at);
