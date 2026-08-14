-- ============================================================================
-- 0001 — Identity & access (blueprint §7.1 / §9.1)
-- Requires: MySQL 8.x, InnoDB, utf8mb4. Server must run READ-COMMITTED,
-- local_infile=1, ngram_token_size=2 (asserted by the API startup health check).
-- ============================================================================

CREATE TABLE IF NOT EXISTS users (
    id                    BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    username              VARCHAR(64)     NOT NULL,
    full_name             VARCHAR(128)    NOT NULL,
    email                 VARCHAR(128)    NULL,
    password_hash         VARCHAR(256)    NOT NULL,
    security_stamp        CHAR(36)        NOT NULL,
    is_active             TINYINT(1)      NOT NULL DEFAULT 1,
    must_change_password  TINYINT(1)      NOT NULL DEFAULT 0,
    failed_login_count    SMALLINT        NOT NULL DEFAULT 0,
    locked_until          DATETIME(3)     NULL,
    last_login_at         DATETIME(3)     NULL,
    concurrency_stamp     CHAR(36)        NOT NULL,
    created_at            DATETIME(3)     NOT NULL,
    created_by            BIGINT UNSIGNED NULL,
    updated_at            DATETIME(3)     NULL,
    updated_by            BIGINT UNSIGNED NULL,
    PRIMARY KEY (id),
    UNIQUE KEY uq_users_username (username),
    KEY ix_users_active (is_active, username)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci ROW_FORMAT=DYNAMIC;

CREATE TABLE IF NOT EXISTS roles (
    id          BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    code        VARCHAR(32)     NOT NULL,
    name        VARCHAR(64)     NOT NULL,
    description VARCHAR(255)    NULL,
    is_system   TINYINT(1)      NOT NULL DEFAULT 0,
    created_at  DATETIME(3)     NOT NULL,
    created_by  BIGINT UNSIGNED NULL,
    updated_at  DATETIME(3)     NULL,
    updated_by  BIGINT UNSIGNED NULL,
    PRIMARY KEY (id),
    UNIQUE KEY uq_roles_code (code)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci ROW_FORMAT=DYNAMIC;

CREATE TABLE IF NOT EXISTS permissions (
    id           BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    code         VARCHAR(64)     NOT NULL,   -- 'Product.Add', 'Print.Reprint'
    module       VARCHAR(32)     NOT NULL,
    action       VARCHAR(32)     NOT NULL,
    display_name VARCHAR(96)     NOT NULL,
    sort_order   SMALLINT        NOT NULL DEFAULT 0,
    PRIMARY KEY (id),
    UNIQUE KEY uq_perm_code (code),
    KEY ix_perm_module (module, sort_order)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci ROW_FORMAT=DYNAMIC;

CREATE TABLE IF NOT EXISTS user_roles (
    user_id BIGINT UNSIGNED NOT NULL,
    role_id BIGINT UNSIGNED NOT NULL,
    PRIMARY KEY (user_id, role_id),
    KEY ix_user_roles_role (role_id),
    CONSTRAINT fk_user_roles_user FOREIGN KEY (user_id) REFERENCES users (id),
    CONSTRAINT fk_user_roles_role FOREIGN KEY (role_id) REFERENCES roles (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci ROW_FORMAT=DYNAMIC;

CREATE TABLE IF NOT EXISTS role_permissions (
    role_id       BIGINT UNSIGNED NOT NULL,
    permission_id BIGINT UNSIGNED NOT NULL,
    PRIMARY KEY (role_id, permission_id),
    KEY ix_role_perms_perm (permission_id),
    CONSTRAINT fk_role_perms_role FOREIGN KEY (role_id) REFERENCES roles (id),
    CONSTRAINT fk_role_perms_perm FOREIGN KEY (permission_id) REFERENCES permissions (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci ROW_FORMAT=DYNAMIC;

-- Only the SHA-256 hash of a refresh token is stored: a DB read must never
-- yield a usable token (blueprint §13/§19.3).
CREATE TABLE IF NOT EXISTS refresh_tokens (
    id             BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    user_id        BIGINT UNSIGNED NOT NULL,
    token_hash     CHAR(64)        NOT NULL,
    issued_at      DATETIME(3)     NOT NULL,
    expires_at     DATETIME(3)     NOT NULL,
    revoked_at     DATETIME(3)     NULL,
    replaced_by_id BIGINT UNSIGNED NULL,
    workstation    VARCHAR(64)     NULL,
    ip             VARCHAR(45)     NULL,
    PRIMARY KEY (id),
    UNIQUE KEY uq_refresh_hash (token_hash),
    KEY ix_refresh_user_active (user_id, expires_at),
    CONSTRAINT fk_refresh_user FOREIGN KEY (user_id) REFERENCES users (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci ROW_FORMAT=DYNAMIC;
