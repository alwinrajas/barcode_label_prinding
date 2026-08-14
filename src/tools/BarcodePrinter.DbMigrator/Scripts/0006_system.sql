-- ============================================================================
-- 0006 — Settings & audit (blueprint §7.6 Rev A, §13/§21 logging separation)
-- audit_logs is partitioned monthly (same MySQL rules as 0004: composite PK,
-- no FKs — user_id is app-enforced and username_snapshot keeps a deleted
-- user's actions attributable).
-- ============================================================================

-- user_scope realises the (setting_key, scope, user) uniqueness under MySQL's
-- "multiple NULLs pass UNIQUE" rule: COALESCE(user_id, 0) makes Global rows
-- genuinely unique per key.
CREATE TABLE IF NOT EXISTS app_settings (
    id            BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    setting_key   VARCHAR(96)     NOT NULL,
    setting_value TEXT            NULL,
    value_type    ENUM('String','Int','Bool','Decimal','Json','Secret') NOT NULL DEFAULT 'String',
    scope         ENUM('Global','User','Workstation') NOT NULL DEFAULT 'Global',
    user_id       BIGINT UNSIGNED NULL,
    user_scope    BIGINT UNSIGNED GENERATED ALWAYS AS (COALESCE(user_id, 0)) STORED,
    is_secret     TINYINT(1)      NOT NULL DEFAULT 0,
    description   VARCHAR(255)    NULL,
    updated_at    DATETIME(3)     NOT NULL,
    updated_by    BIGINT UNSIGNED NULL,
    PRIMARY KEY (id),
    UNIQUE KEY uq_setting (setting_key, scope, user_scope),
    CONSTRAINT fk_settings_user FOREIGN KEY (user_id) REFERENCES users (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci ROW_FORMAT=DYNAMIC;

CREATE TABLE IF NOT EXISTS audit_logs (
    id                BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    occurred_at       DATETIME(3)     NOT NULL,
    user_id           BIGINT UNSIGNED NULL,          -- NULL for failed logins
    username_snapshot VARCHAR(64)     NOT NULL,
    action            VARCHAR(48)     NOT NULL,      -- Login, ProductCreated, RoleUpdated…
    entity_type       VARCHAR(48)     NULL,
    entity_id         VARCHAR(64)     NULL,
    before_json       JSON            NULL,          -- changed fields only, secrets redacted
    after_json        JSON            NULL,
    workstation       VARCHAR(64)     NULL,
    ip                VARCHAR(45)     NULL,
    correlation_id    CHAR(36)        NULL,
    severity          ENUM('Info','Warning','Security') NOT NULL DEFAULT 'Info',
    PRIMARY KEY (id, occurred_at),
    KEY ix_audit_user_date   (user_id, occurred_at),
    KEY ix_audit_entity      (entity_type, entity_id, occurred_at),
    KEY ix_audit_action_date (action, occurred_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci ROW_FORMAT=DYNAMIC
PARTITION BY RANGE (TO_DAYS(occurred_at)) (
    PARTITION p202608 VALUES LESS THAN (TO_DAYS('2026-09-01')),
    PARTITION p202609 VALUES LESS THAN (TO_DAYS('2026-10-01')),
    PARTITION p202610 VALUES LESS THAN (TO_DAYS('2026-11-01')),
    PARTITION p202611 VALUES LESS THAN (TO_DAYS('2026-12-01')),
    PARTITION p202612 VALUES LESS THAN (TO_DAYS('2027-01-01')),
    PARTITION p202701 VALUES LESS THAN (TO_DAYS('2027-02-01')),
    PARTITION p202702 VALUES LESS THAN (TO_DAYS('2027-03-01')),
    PARTITION p202703 VALUES LESS THAN (TO_DAYS('2027-04-01')),
    PARTITION p202704 VALUES LESS THAN (TO_DAYS('2027-05-01')),
    PARTITION p202705 VALUES LESS THAN (TO_DAYS('2027-06-01')),
    PARTITION p202706 VALUES LESS THAN (TO_DAYS('2027-07-01')),
    PARTITION p202707 VALUES LESS THAN (TO_DAYS('2027-08-01')),
    PARTITION p202708 VALUES LESS THAN (TO_DAYS('2027-09-01')),
    PARTITION pmax    VALUES LESS THAN MAXVALUE
);
