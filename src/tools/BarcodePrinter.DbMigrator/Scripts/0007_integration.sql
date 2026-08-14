-- ============================================================================
-- 0007 — External integration (blueprint §10 / §20 Rev A)
-- Oracle is optional and disabled by default. password_protected holds the
-- ASP.NET Core Data-Protection-encrypted credential — never plain text, never
-- returned by any API (A-30).
-- ============================================================================

CREATE TABLE IF NOT EXISTS integration_settings (
    id                  BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    provider            VARCHAR(32)     NOT NULL,          -- 'Oracle'
    is_enabled          TINYINT(1)      NOT NULL DEFAULT 0,
    environment         VARCHAR(32)     NULL,
    host                VARCHAR(128)    NULL,
    port                INT             NULL,
    service_name        VARCHAR(128)    NULL,
    sid                 VARCHAR(128)    NULL,
    username            VARCHAR(128)    NULL,
    password_protected  VARBINARY(1024) NULL,
    connect_timeout_sec INT             NOT NULL DEFAULT 15,
    command_timeout_sec INT             NOT NULL DEFAULT 30,
    options_json        JSON            NULL,              -- mapping/object names (C-20)
    last_test_at        DATETIME(3)     NULL,
    last_test_success   TINYINT(1)      NULL,
    last_test_message   VARCHAR(512)    NULL,
    created_at          DATETIME(3)     NOT NULL,
    created_by          BIGINT UNSIGNED NULL,
    updated_at          DATETIME(3)     NULL,
    updated_by          BIGINT UNSIGNED NULL,
    PRIMARY KEY (id),
    UNIQUE KEY uq_integration_provider (provider)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci ROW_FORMAT=DYNAMIC;

-- Transactional outbox (§10.2): written in the SAME MySQL transaction as the
-- business change; drained independently. At-least-once delivery.
CREATE TABLE IF NOT EXISTS integration_outbox (
    id              BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    provider        VARCHAR(32)     NOT NULL,
    event_type      VARCHAR(48)     NOT NULL,     -- 'PrintJobCompleted'
    aggregate_type  VARCHAR(48)     NOT NULL,
    aggregate_id    VARCHAR(64)     NOT NULL,
    payload_json    JSON            NOT NULL,
    created_at      DATETIME(3)     NOT NULL,
    status          ENUM('Pending','Sent','Failed','DeadLettered') NOT NULL DEFAULT 'Pending',
    attempt_count   SMALLINT        NOT NULL DEFAULT 0,
    next_attempt_at DATETIME(3)     NULL,
    last_error      VARCHAR(512)    NULL,
    sent_at         DATETIME(3)     NULL,
    PRIMARY KEY (id),
    KEY ix_outbox_due (status, next_attempt_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci ROW_FORMAT=DYNAMIC;

CREATE TABLE IF NOT EXISTS integration_sync_runs (
    id            BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    provider      VARCHAR(32)     NOT NULL,
    direction     ENUM('Inbound','Outbound') NOT NULL,
    object_name   VARCHAR(128)    NULL,
    started_at    DATETIME(3)     NOT NULL,
    finished_at   DATETIME(3)     NULL,
    status        VARCHAR(24)     NOT NULL,
    rows_read     INT             NOT NULL DEFAULT 0,
    rows_written  INT             NOT NULL DEFAULT 0,
    error_message VARCHAR(512)    NULL,
    PRIMARY KEY (id),
    KEY ix_syncrun (provider, started_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci ROW_FORMAT=DYNAMIC;
