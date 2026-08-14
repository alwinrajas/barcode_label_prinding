-- ============================================================================
-- 0002 — Product master (blueprint §7.2/§7.3, §9.1, §9.3, §9.4)
-- F-1 (readiness review): products ↔ product_images is a circular FK.
-- Resolution: create products WITHOUT the primary_image_id constraint, create
-- product_images, then ALTER products to add the pointer FK at the end.
-- ============================================================================

CREATE TABLE IF NOT EXISTS uoms (
    id        BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    code      VARCHAR(16)     NOT NULL,
    name      VARCHAR(64)     NOT NULL,
    is_active TINYINT(1)      NOT NULL DEFAULT 1,
    PRIMARY KEY (id),
    UNIQUE KEY uq_uoms_code (code)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci ROW_FORMAT=DYNAMIC;

CREATE TABLE IF NOT EXISTS product_categories (
    id        BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    code      VARCHAR(32)     NOT NULL,
    name      VARCHAR(96)     NOT NULL,
    parent_id BIGINT UNSIGNED NULL,
    is_active TINYINT(1)      NOT NULL DEFAULT 1,
    PRIMARY KEY (id),
    UNIQUE KEY uq_categories_code (code),
    CONSTRAINT fk_categories_parent FOREIGN KEY (parent_id) REFERENCES product_categories (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci ROW_FORMAT=DYNAMIC;

-- The `default_` prefix is deliberate: these are master defaults, overridable
-- at print time (A-9). What was actually printed lives ONLY on the print job.
CREATE TABLE IF NOT EXISTS products (
    id                       BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    code                     VARCHAR(64)     NOT NULL,
    description              VARCHAR(255)    NOT NULL,
    barcode_value            VARCHAR(128)    NULL,          -- defaults to code (A-33/A-4)
    uom_id                   BIGINT UNSIGNED NULL,
    size                     VARCHAR(64)     NULL,
    color                    VARCHAR(64)     NULL,
    category_id              BIGINT UNSIGNED NULL,
    default_batch            VARCHAR(64)     NULL,
    default_production_date  DATE            NULL,
    default_expiry_date      DATE            NULL,
    default_quantity         DECIMAL(18,3)   NULL,
    default_quantity_text    VARCHAR(32)     NULL,          -- e.g. '750[D]' (C-12)
    carton_quantity          DECIMAL(18,3)   NULL,
    cartons_per_pallet       INT             NULL,
    primary_image_id         BIGINT UNSIGNED NULL,          -- FK added below (F-1)
    default_template_id      BIGINT UNSIGNED NULL,          -- FK added in 0003
    is_active                TINYINT(1)      NOT NULL DEFAULT 1,
    search_text              VARCHAR(320) GENERATED ALWAYS AS
                             (CONCAT_WS(' ', code, description)) STORED,
    concurrency_stamp        CHAR(36)        NOT NULL,
    created_at               DATETIME(3)     NOT NULL,
    created_by               BIGINT UNSIGNED NULL,
    updated_at               DATETIME(3)     NULL,
    updated_by               BIGINT UNSIGNED NULL,
    PRIMARY KEY (id),
    UNIQUE KEY uq_products_code (code),
    KEY ix_products_active_code (is_active, code),
    KEY ix_products_desc (description(64)),
    KEY ix_products_barcode (barcode_value),
    KEY ix_products_batch (default_batch),
    KEY ix_products_category (category_id, is_active),
    FULLTEXT KEY ftx_products_search (search_text) WITH PARSER ngram,
    CONSTRAINT fk_products_uom FOREIGN KEY (uom_id) REFERENCES uoms (id),
    CONSTRAINT fk_products_category FOREIGN KEY (category_id) REFERENCES product_categories (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci ROW_FORMAT=DYNAMIC;

CREATE TABLE IF NOT EXISTS product_images (
    id           BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    product_id   BIGINT UNSIGNED NOT NULL,
    file_name    VARCHAR(255)    NOT NULL,
    content_hash CHAR(64)        NOT NULL,     -- SHA-256; content-addressed store + dedupe
    mime         VARCHAR(64)     NOT NULL,
    width_px     INT             NOT NULL,
    height_px    INT             NOT NULL,
    byte_size    INT             NOT NULL,
    storage_key  VARCHAR(320)    NULL,         -- file-store path (recommended, C-14)
    blob_data    LONGBLOB        NULL,         -- populated only in BLOB mode
    is_primary   TINYINT(1)      NOT NULL DEFAULT 0,
    created_at   DATETIME(3)     NOT NULL,
    created_by   BIGINT UNSIGNED NULL,
    PRIMARY KEY (id),
    UNIQUE KEY uq_img_hash (content_hash),
    KEY ix_img_product (product_id, is_primary),
    CONSTRAINT fk_img_product FOREIGN KEY (product_id) REFERENCES products (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci ROW_FORMAT=DYNAMIC;

-- Cached 1-bit dithered ^GFA renders per (image, target dot size): the
-- JPEG→mono conversion happens once per template size, not once per label.
CREATE TABLE IF NOT EXISTS product_image_renders (
    id          BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    image_id    BIGINT UNSIGNED NOT NULL,
    width_dots  SMALLINT        NOT NULL,
    height_dots SMALLINT        NOT NULL,
    dpi         SMALLINT        NOT NULL,
    dither      VARCHAR(16)     NOT NULL DEFAULT 'FloydSteinberg',
    grf_name    VARCHAR(16)     NOT NULL,
    grf_payload MEDIUMBLOB      NOT NULL,
    byte_count  INT             NOT NULL,
    created_at  DATETIME(3)     NOT NULL,
    PRIMARY KEY (id),
    UNIQUE KEY uq_render (image_id, width_dots, height_dots, dither),
    CONSTRAINT fk_render_image FOREIGN KEY (image_id) REFERENCES product_images (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci ROW_FORMAT=DYNAMIC;

-- F-1: close the circular reference now that both tables exist.
ALTER TABLE products
    ADD CONSTRAINT fk_products_primary_image
    FOREIGN KEY (primary_image_id) REFERENCES product_images (id);
