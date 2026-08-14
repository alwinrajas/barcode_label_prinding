-- ============================================================================
-- 0003 — Label templates & printers (blueprint §4.3, §5.1)
-- The client's template FILE is the source of truth for layout; these tables
-- hold metadata + field mapping only (A-15/A-17). Versions are immutable so a
-- reprint reproduces the layout that was used, not the layout of today.
-- F-2 (readiness review): label_template_fields FKs to the VERSION ROW's PK,
-- not to a (template_id, version) pair.
-- ============================================================================

CREATE TABLE IF NOT EXISTS label_templates (
    id              BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    code            VARCHAR(32)     NOT NULL,       -- 'STD-CARTON-01'
    name            VARCHAR(128)    NOT NULL,
    description     VARCHAR(255)    NULL,
    template_format ENUM('Zpl','Epl','WindowsDocument','Native') NOT NULL,  -- C-2
    width_mm        DECIMAL(6,2)    NULL,           -- C-4: TBD until template files arrive
    height_mm       DECIMAL(6,2)    NULL,
    dpi             SMALLINT        NULL,
    gap_mm          DECIMAL(5,2)    NULL,
    orientation     ENUM('Portrait','Landscape') NULL,
    layout_type     ENUM('OneUp','TwoUp','MultiColumn') NULL,               -- C-5
    layout_columns  SMALLINT        NULL,
    layout_rows     SMALLINT        NULL,
    media_type      ENUM('DirectThermal','ThermalTransfer','Plain') NULL,
    media_tracking  ENUM('Gap','BlackMark','Continuous') NULL,
    current_version INT             NOT NULL DEFAULT 1,
    is_active       TINYINT(1)      NOT NULL DEFAULT 1,
    is_default      TINYINT(1)      NOT NULL DEFAULT 0,
    requires_image  TINYINT(1)      NOT NULL DEFAULT 0,
    requires_qr     TINYINT(1)      NOT NULL DEFAULT 0,
    created_at      DATETIME(3)     NOT NULL,
    created_by      BIGINT UNSIGNED NULL,
    updated_at      DATETIME(3)     NULL,
    updated_by      BIGINT UNSIGNED NULL,
    PRIMARY KEY (id),
    UNIQUE KEY uq_tpl_code (code),
    KEY ix_tpl_active (is_active, is_default)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci ROW_FORMAT=DYNAMIC;

CREATE TABLE IF NOT EXISTS label_template_versions (
    id                BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    template_id       BIGINT UNSIGNED NOT NULL,
    version           INT             NOT NULL,
    artifact_blob     LONGBLOB        NULL,      -- the client's file, as supplied
    artifact_path     VARCHAR(320)    NULL,
    artifact_hash     CHAR(64)        NOT NULL,  -- SHA-256: integrity + dedupe
    artifact_filename VARCHAR(255)    NULL,
    prepared_payload  LONGBLOB        NULL,      -- ^DF stored-format form, derived at registration
    reference_image   LONGBLOB        NULL,      -- approved sample for preview/verification
    notes             VARCHAR(512)    NULL,
    created_at        DATETIME(3)     NOT NULL,
    created_by        BIGINT UNSIGNED NULL,
    PRIMARY KEY (id),
    UNIQUE KEY uq_tplver (template_id, version),
    CONSTRAINT fk_tplver_template FOREIGN KEY (template_id) REFERENCES label_templates (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci ROW_FORMAT=DYNAMIC;

-- Field mapping: placeholder → closed-vocabulary data key (§5.2).
-- A QrCode field accepts ONLY Settings.FeedbackFormUrl (A-14), enforced by the
-- mapping validator in the Application layer.
CREATE TABLE IF NOT EXISTS label_template_fields (
    id                  BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    template_version_id BIGINT UNSIGNED NOT NULL,           -- F-2
    placeholder_ref     VARCHAR(32)     NOT NULL,           -- ZPL: '1' for ^FN1
    field_label         VARCHAR(64)     NOT NULL,
    data_key            VARCHAR(64)     NOT NULL,           -- 'Effective.Batch'
    data_kind           ENUM('Text','Barcode','QrCode','Image','DateTime','Number') NOT NULL,
    format_string       VARCHAR(64)     NULL,               -- 'dd/MM/yyyy' (C-1)
    transform           ENUM('None','Upper','Lower','Trim') NOT NULL DEFAULT 'None',
    max_length          SMALLINT        NULL,
    overflow            ENUM('Truncate','Error','Shrink') NOT NULL DEFAULT 'Error',
    is_required         TINYINT(1)      NOT NULL DEFAULT 0,
    fallback_value      VARCHAR(128)    NULL,
    sample_value        VARCHAR(128)    NULL,
    sort_order          SMALLINT        NOT NULL DEFAULT 0,
    PRIMARY KEY (id),
    UNIQUE KEY uq_tplfield (template_version_id, placeholder_ref),
    CONSTRAINT fk_tplfield_version FOREIGN KEY (template_version_id)
        REFERENCES label_template_versions (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci ROW_FORMAT=DYNAMIC;

CREATE TABLE IF NOT EXISTS printers (
    id                    BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    code                  VARCHAR(32)     NOT NULL,
    name                  VARCHAR(96)     NOT NULL,
    location              VARCHAR(128)    NULL,
    connection_type       ENUM('NetworkTcp','WindowsRaw','WindowsGraphics') NOT NULL,
    dispatch_mode         ENUM('Server','Client') NOT NULL,
    host                  VARCHAR(128)    NULL,
    port                  INT             NULL,
    windows_printer_name  VARCHAR(255)    NULL,
    owner_workstation     VARCHAR(64)     NULL,   -- Client dispatch: the PC that owns it (F-7)
    dpi                   SMALLINT        NULL,   -- C-18
    language              ENUM('Zpl','Windows') NOT NULL,
    default_template_id   BIGINT UNSIGNED NULL,
    supports_status_query TINYINT(1)      NOT NULL DEFAULT 0,  -- ~HQES capability (C-17/C-18)
    is_active             TINYINT(1)      NOT NULL DEFAULT 1,
    is_default            TINYINT(1)      NOT NULL DEFAULT 0,
    created_at            DATETIME(3)     NOT NULL,
    created_by            BIGINT UNSIGNED NULL,
    updated_at            DATETIME(3)     NULL,
    updated_by            BIGINT UNSIGNED NULL,
    PRIMARY KEY (id),
    UNIQUE KEY uq_printers_code (code),
    KEY ix_printers_active (is_active, dispatch_mode),
    CONSTRAINT fk_printers_default_tpl FOREIGN KEY (default_template_id)
        REFERENCES label_templates (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci ROW_FORMAT=DYNAMIC;

-- Compatibility: a 203-dpi template must not silently print on a 300-dpi
-- printer at half size (§4.3). Rule-based dpi/language fallback applies when
-- no explicit row exists.
CREATE TABLE IF NOT EXISTS label_template_printers (
    template_id  BIGINT UNSIGNED NOT NULL,
    printer_id   BIGINT UNSIGNED NOT NULL,
    is_preferred TINYINT(1)      NOT NULL DEFAULT 0,
    PRIMARY KEY (template_id, printer_id),
    CONSTRAINT fk_tplprn_template FOREIGN KEY (template_id) REFERENCES label_templates (id),
    CONSTRAINT fk_tplprn_printer FOREIGN KEY (printer_id) REFERENCES printers (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci ROW_FORMAT=DYNAMIC;

-- Deferred FK from 0002: products.default_template_id.
ALTER TABLE products
    ADD CONSTRAINT fk_products_default_tpl
    FOREIGN KEY (default_template_id) REFERENCES label_templates (id);
