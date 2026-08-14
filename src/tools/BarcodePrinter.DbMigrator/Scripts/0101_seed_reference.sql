-- ============================================================================
-- 0101 — Seed reference data: UOMs and application settings.
-- Setting defaults reflect blueprint decisions; values marked TBD are the
-- conservative default until the client answers the corresponding C-item.
-- ============================================================================

INSERT INTO uoms (code, name, is_active) VALUES
    ('PCS', 'Pieces', 1),
    ('BOX', 'Box',    1),
    ('CTN', 'Carton', 1),
    ('SET', 'Set',    1),
    ('KG',  'Kilogram', 1)
ON DUPLICATE KEY UPDATE name = VALUES(name);

INSERT INTO app_settings (setting_key, setting_value, value_type, scope, is_secret, description, updated_at) VALUES
    -- QR (A-14, confirmed): static Google Form URL, configured here, never parameterised.
    ('Label:FeedbackFormUrl',      '',             'String', 'Global', 0,
     'Static Google Form URL encoded into the label QR code. No dynamic parameters are ever appended.', UTC_TIMESTAMP(3)),

    -- C-1: client-stated DD/MM/YYYY; physical samples show DD/MMM/YYYY. Client-stated wins until resolved.
    ('Label:DateFormat',           'dd/MM/yyyy',   'String', 'Global', 0,
     'Format for Prod/Exp dates on labels. C-1: samples print dd/MMM/yyyy — pending client confirmation.', UTC_TIMESTAMP(3)),
    ('Label:TimestampFormat',      'dd/MM/yyyy HH:mm', 'String', 'Global', 0,
     'Format for the printed timestamp element. Time component precision is TBD (C-1).', UTC_TIMESTAMP(3)),

    -- C-11: ManualRange mirrors the legacy CTN Start/End inputs — safest default.
    ('Printing:CartonStrategy',    'ManualRange',  'String', 'Global', 0,
     'Carton numbering strategy code (ICartonNumberingStrategy registry).', UTC_TIMESTAMP(3)),
    ('Printing:CartonGapless',     'false',        'Bool',   'Global', 0,
     'Whether carton numbers must be gapless (C-11 — compliance question, pending).', UTC_TIMESTAMP(3)),

    -- C-17: Dispatched is universally supported; Confirmed needs ~HQES per printer.
    ('Print:CompletionSemantics',  'Dispatched',   'String', 'Global', 0,
     'What Completed means: Dispatched (bytes accepted) or Confirmed (printer verified).', UTC_TIMESTAMP(3)),

    -- C-13: AllOrNothing is the conservative default — never partially imports without explicit opt-in.
    ('Import:CommitPolicy',        'AllOrNothing', 'String', 'Global', 0,
     'Excel import commit policy: AllOrNothing or PartialCommit (C-13 pending).', UTC_TIMESTAMP(3)),
    ('Import:MaxRows',             '200000',       'Int',    'Global', 0,
     'Hard server-side cap on rows per Excel import.', UTC_TIMESTAMP(3)),
    ('Import:MaxUploadMb',         '100',          'Int',    'Global', 0,
     'Hard server-side cap on upload size.', UTC_TIMESTAMP(3)),

    ('Company:Name',               '',             'String', 'Global', 0,
     'Company name for reports and settings display.', UTC_TIMESTAMP(3)),

    ('Auth:LockoutThreshold',      '5',            'Int',    'Global', 0,
     'Failed logins before lockout.', UTC_TIMESTAMP(3)),
    ('Auth:LockoutMinutes',        '15',           'Int',    'Global', 0,
     'Lockout duration in minutes.', UTC_TIMESTAMP(3)),
    ('Auth:PasswordMinLength',     '8',            'Int',    'Global', 0,
     'Minimum password length.', UTC_TIMESTAMP(3))
ON DUPLICATE KEY UPDATE description = VALUES(description);
