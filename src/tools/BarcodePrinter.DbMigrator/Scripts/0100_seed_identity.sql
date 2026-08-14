-- ============================================================================
-- 0100 — Seed roles, permissions, role-permission matrix (blueprint §13, §19.1)
-- The permission codes here MUST match PermissionCodes.cs in Contracts —
-- a completeness test in Application.Tests asserts the two sets are identical.
-- The admin user itself is seeded by the migrator in C# (password hashing
-- cannot be done in SQL).
-- ============================================================================

INSERT INTO roles (code, name, description, is_system, created_at) VALUES
    ('Admin',   'Administrator', 'Full access to every module and setting.', 1, UTC_TIMESTAMP(3)),
    ('Manager', 'Manager',       'Operates and supervises: products, printing, reports.', 1, UTC_TIMESTAMP(3)),
    ('User',    'User',          'Prints labels and views own activity.', 1, UTC_TIMESTAMP(3))
ON DUPLICATE KEY UPDATE name = VALUES(name);

INSERT INTO permissions (code, module, action, display_name, sort_order) VALUES
    ('Product.View',                'Product',   'View',              'View products',            10),
    ('Product.Add',                 'Product',   'Add',               'Add products',             11),
    ('Product.Edit',                'Product',   'Edit',              'Edit products',            12),
    ('Product.Delete',              'Product',   'Delete',            'Delete products',          13),
    ('Product.Import',              'Product',   'Import',            'Import products (Excel)',  14),
    ('Product.Export',              'Product',   'Export',            'Export products (Excel)',  15),
    ('Print.View',                  'Print',     'View',              'Open printing screen',     20),
    ('Print.Execute',               'Print',     'Execute',           'Print labels',             21),
    ('Print.Reprint',               'Print',     'Reprint',           'Reprint labels',           22),
    ('Print.Cancel',                'Print',     'Cancel',            'Cancel print jobs',        23),
    ('History.View',                'History',   'View',              'View print history',       30),
    ('History.Export',              'History',   'Export',            'Export print history',     31),
    ('Report.View',                 'Report',    'View',              'View reports',             40),
    ('Report.Export',               'Report',    'Export',            'Export reports',           41),
    ('Report.Print',                'Report',    'Print',             'Print reports',            42),
    ('User.View',                   'User',      'View',              'View users',               50),
    ('User.Add',                    'User',      'Add',               'Add users',                51),
    ('User.Edit',                   'User',      'Edit',              'Edit users',               52),
    ('User.Deactivate',             'User',      'Deactivate',        'Activate/deactivate users',53),
    ('User.ResetPassword',          'User',      'ResetPassword',     'Reset user passwords',     54),
    ('Role.View',                   'Role',      'View',              'View roles',               60),
    ('Role.Manage',                 'Role',      'Manage',            'Manage roles/permissions', 61),
    ('Settings.View',               'Settings',  'View',              'View settings',            70),
    ('Settings.Manage',             'Settings',  'Manage',            'Manage settings',          71),
    ('Settings.ManagePrinters',     'Settings',  'ManagePrinters',    'Manage printers',          72),
    ('Settings.ManageTemplates',    'Settings',  'ManageTemplates',   'Manage label templates',   73),
    ('Settings.ManageIntegration',  'Settings',  'ManageIntegration', 'Manage Oracle integration',74),
    ('Audit.View',                  'Audit',     'View',              'View audit log',           80),
    ('Audit.Export',                'Audit',     'Export',            'Export audit log',         81),
    ('Dashboard.View',              'Dashboard', 'View',              'View dashboard',           1)
ON DUPLICATE KEY UPDATE display_name = VALUES(display_name), sort_order = VALUES(sort_order);

-- Admin: everything.
INSERT IGNORE INTO role_permissions (role_id, permission_id)
SELECT r.id, p.id FROM roles r CROSS JOIN permissions p WHERE r.code = 'Admin';

-- Manager: operate + supervise; no delete, no user/role/settings management.
INSERT IGNORE INTO role_permissions (role_id, permission_id)
SELECT r.id, p.id FROM roles r JOIN permissions p ON p.code IN (
    'Dashboard.View',
    'Product.View','Product.Add','Product.Edit','Product.Import','Product.Export',
    'Print.View','Print.Execute','Print.Reprint','Print.Cancel',
    'History.View','History.Export',
    'Report.View','Report.Export','Report.Print',
    'Audit.View'
) WHERE r.code = 'Manager';

-- User: print and view own activity. Print.Reprint NOT granted (C-15 TBD —
-- the permission exists so granting it later is a role edit, not a release).
INSERT IGNORE INTO role_permissions (role_id, permission_id)
SELECT r.id, p.id FROM roles r JOIN permissions p ON p.code IN (
    'Dashboard.View',
    'Product.View',
    'Print.View','Print.Execute',
    'History.View',
    'Report.View'
) WHERE r.code = 'User';
