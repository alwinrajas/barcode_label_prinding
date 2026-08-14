-- ============================================================================
-- 0005 — Excel import (blueprint §15 / §10 Rev A)
-- product_import_staging is a LANDING STRIP: no FKs, no constraints, one key.
-- Every extra index there is pure write cost during MySqlBulkCopy.
-- ============================================================================

CREATE TABLE IF NOT EXISTS import_batches (
    id                BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    file_name         VARCHAR(255)    NOT NULL,
    stored_path       VARCHAR(320)    NOT NULL,
    uploaded_by       BIGINT UNSIGNED NOT NULL,
    uploaded_at       DATETIME(3)     NOT NULL,
    status            ENUM('Uploaded','Validating','Committing','Completed',
                           'Failed','Cancelled') NOT NULL,
    commit_policy     ENUM('AllOrNothing','PartialCommit') NOT NULL,   -- C-13
    total_rows        INT             NOT NULL DEFAULT 0,
    processed_rows    INT             NOT NULL DEFAULT 0,
    valid_rows        INT             NOT NULL DEFAULT 0,
    invalid_rows      INT             NOT NULL DEFAULT 0,
    inserted_rows     INT             NOT NULL DEFAULT 0,
    updated_rows      INT             NOT NULL DEFAULT 0,
    started_at        DATETIME(3)     NULL,
    finished_at       DATETIME(3)     NULL,
    error_report_path VARCHAR(320)    NULL,
    error_message     VARCHAR(512)    NULL,
    PRIMARY KEY (id),
    KEY ix_import_user (uploaded_by, uploaded_at),
    CONSTRAINT fk_import_user FOREIGN KEY (uploaded_by) REFERENCES users (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci ROW_FORMAT=DYNAMIC;

CREATE TABLE IF NOT EXISTS import_errors (
    id          BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    batch_id    BIGINT UNSIGNED NOT NULL,
    row_no      INT             NOT NULL,
    column_name VARCHAR(64)     NULL,
    error_code  VARCHAR(32)     NOT NULL,
    message     VARCHAR(512)    NOT NULL,
    raw_value   VARCHAR(512)    NULL,
    PRIMARY KEY (id),
    KEY ix_imperr_batch (batch_id, row_no),
    CONSTRAINT fk_imperr_batch FOREIGN KEY (batch_id) REFERENCES import_batches (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci ROW_FORMAT=DYNAMIC;

-- c_* columns are raw text exactly as read from the sheet; n_* columns hold
-- the normalised values produced by validation. A startup sweep deletes rows
-- belonging to any batch that is not currently running.
CREATE TABLE IF NOT EXISTS product_import_staging (
    batch_id          BIGINT UNSIGNED NOT NULL,
    row_no            INT             NOT NULL,
    c_code            VARCHAR(255)    NULL,
    c_description     VARCHAR(512)    NULL,
    c_uom             VARCHAR(64)     NULL,
    c_size            VARCHAR(128)    NULL,
    c_color           VARCHAR(128)    NULL,
    c_batch           VARCHAR(128)    NULL,
    c_production_date VARCHAR(64)     NULL,
    c_expiry_date     VARCHAR(64)     NULL,
    c_quantity        VARCHAR(64)     NULL,
    c_carton_qty      VARCHAR(64)     NULL,
    c_category        VARCHAR(128)    NULL,
    is_valid          TINYINT(1)      NOT NULL DEFAULT 1,
    n_production_date DATE            NULL,
    n_expiry_date     DATE            NULL,
    n_quantity        DECIMAL(18,3)   NULL,
    n_carton_qty      DECIMAL(18,3)   NULL,
    n_uom_id          BIGINT UNSIGNED NULL,
    n_category_id     BIGINT UNSIGNED NULL,
    KEY ix_staging_batch (batch_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci ROW_FORMAT=DYNAMIC;
