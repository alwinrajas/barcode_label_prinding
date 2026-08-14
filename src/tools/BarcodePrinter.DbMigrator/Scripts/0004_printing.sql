-- ============================================================================
-- 0004 — Print history (blueprint §7.5 Rev A / §9.1–9.2 Rev B)
-- print_jobs and print_job_items are PARTITIONED monthly by date.
-- MySQL rules this forces (documented, deliberate):
--   * the partition column must be in every unique key → composite PKs
--   * partitioned InnoDB tables support NO foreign keys, either direction
--     → job→user/printer/product/template relationships are app-enforced,
--       indexed here for the report queries.
-- Snapshot (snap_*) columns realise A-10: what was printed is recorded
-- permanently, independent of later product/template edits.
-- ============================================================================

CREATE TABLE IF NOT EXISTS print_jobs (
    id                     BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    requested_at           DATETIME(3)     NOT NULL,
    job_no                 VARCHAR(24)     NOT NULL,     -- PJ-260812-000431
    requested_by_user_id   BIGINT UNSIGNED NOT NULL,
    printer_id             BIGINT UNSIGNED NOT NULL,
    template_id            BIGINT UNSIGNED NOT NULL,
    template_version       INT             NOT NULL,
    product_id             BIGINT UNSIGNED NOT NULL,

    -- Snapshot of the EFFECTIVE (post-override) values actually printed (A-10)
    snap_product_code      VARCHAR(64)     NOT NULL,
    snap_description       VARCHAR(255)    NOT NULL,
    snap_barcode_value     VARCHAR(128)    NOT NULL,
    snap_uom               VARCHAR(16)     NULL,
    snap_size              VARCHAR(64)     NULL,
    snap_color             VARCHAR(64)     NULL,
    snap_batch             VARCHAR(64)     NULL,
    snap_production_date   DATE            NULL,
    snap_expiry_date       DATE            NULL,
    snap_quantity_text     VARCHAR(32)     NULL,
    snap_image_hash        CHAR(64)        NULL,
    snap_timestamp_text    VARCHAR(64)     NULL,
    overrides_json         JSON            NULL,          -- which fields differed from master

    carton_from            INT             NULL,
    carton_to              INT             NULL,
    carton_total           INT             NULL,
    copies_per_label       SMALLINT        NOT NULL DEFAULT 1,
    label_count            INT             NOT NULL,

    status                 ENUM('Queued','Dispatching','Printing','Completed',
                                'PartiallyCompleted','Failed','Cancelled') NOT NULL,
    dispatched_at          DATETIME(3)     NULL,          -- bytes accepted     ┐ C-17: both
    confirmed_at           DATETIME(3)     NULL,          -- printer verified   ┘ recorded
    completed_at           DATETIME(3)     NULL,
    labels_confirmed       INT             NOT NULL DEFAULT 0,
    attempt_count          SMALLINT        NOT NULL DEFAULT 0,
    error_code             VARCHAR(48)     NULL,
    error_message          VARCHAR(512)    NULL,

    is_reprint             TINYINT(1)      NOT NULL DEFAULT 0,
    source_job_id          BIGINT UNSIGNED NULL,          -- reprint lineage (self-ref, app-enforced)
    reprint_reason         VARCHAR(255)    NULL,
    authorized_by_user_id  BIGINT UNSIGNED NULL,

    workstation            VARCHAR(64)     NULL,
    correlation_id         CHAR(36)        NOT NULL,
    lease_owner            VARCHAR(64)     NULL,
    lease_expires_at       DATETIME(3)     NULL,
    concurrency_stamp      CHAR(36)        NOT NULL,

    PRIMARY KEY (id, requested_at),
    UNIQUE KEY uq_pj_job_no (job_no, requested_at),
    KEY ix_pj_requested      (requested_at),
    KEY ix_pj_user_date      (requested_by_user_id, requested_at),
    KEY ix_pj_product_date   (product_id, requested_at),
    KEY ix_pj_printer_date   (printer_id, requested_at),
    KEY ix_pj_status_date    (status, requested_at),
    KEY ix_pj_reprint_date   (is_reprint, requested_at),
    KEY ix_pj_source         (source_job_id),
    KEY ix_pj_snapcode_date  (snap_product_code, requested_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci ROW_FORMAT=DYNAMIC
PARTITION BY RANGE (TO_DAYS(requested_at)) (
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

-- Rendered payload split out so the hot table stays narrow (§9.1). Unpartitioned;
-- pruned on the retention schedule (C-23). The payload is the COMPLETE job
-- stream (~DG image + ^DF definition + ^XF records) so a reprint replay is
-- self-contained (§19.1 checkpoint 10).
CREATE TABLE IF NOT EXISTS print_job_payloads (
    job_id       BIGINT UNSIGNED NOT NULL,
    requested_at DATETIME(3)     NOT NULL,     -- denormalised for pruning
    format       ENUM('Zpl','Raster') NOT NULL,
    compressed   TINYINT(1)      NOT NULL DEFAULT 0,
    payload      LONGBLOB        NOT NULL,
    byte_count   INT             NOT NULL,
    payload_hash CHAR(64)        NOT NULL,
    created_at   DATETIME(3)     NOT NULL,
    PRIMARY KEY (job_id),
    KEY ix_payload_date (requested_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci ROW_FORMAT=DYNAMIC;

-- One row per carton. HIGHEST-VOLUME TABLE (T19/C-23 govern retention).
CREATE TABLE IF NOT EXISTS print_job_items (
    id            BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    requested_at  DATETIME(3)     NOT NULL,
    job_id        BIGINT UNSIGNED NOT NULL,     -- app-enforced (partitioned: no FK)
    sequence_no   INT             NOT NULL,
    carton_no     INT             NULL,
    carton_total  INT             NULL,
    barcode_value VARCHAR(128)    NOT NULL,
    status        ENUM('Pending','Dispatched','Confirmed','Failed','Cancelled') NOT NULL,
    printed_at    DATETIME(3)     NULL,
    error_message VARCHAR(255)    NULL,
    PRIMARY KEY (id, requested_at),
    KEY ix_pji_job    (job_id, sequence_no),
    KEY ix_pji_carton (carton_no, requested_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci ROW_FORMAT=DYNAMIC
PARTITION BY RANGE (TO_DAYS(requested_at)) (
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

-- Carton sequence counters (§8.4). One row per scope; allocation is
-- SELECT ... FOR UPDATE inside the job transaction (blueprint §11.2 Rev A).
CREATE TABLE IF NOT EXISTS carton_sequences (
    id            BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    scope_key     VARCHAR(191)    NOT NULL,
    strategy_code VARCHAR(32)     NOT NULL,
    -- 'current_value' because LAST_VALUE is reserved in MySQL 8 (window functions)
    current_value BIGINT          NOT NULL DEFAULT 0,
    updated_at    DATETIME(3)     NOT NULL,
    updated_by    BIGINT UNSIGNED NULL,
    PRIMARY KEY (id),
    UNIQUE KEY uq_seq_scope (scope_key)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci ROW_FORMAT=DYNAMIC;
