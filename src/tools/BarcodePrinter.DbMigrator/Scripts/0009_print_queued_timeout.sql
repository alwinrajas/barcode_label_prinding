-- A client-dispatched job whose owner workstation never polls (PC off, app not
-- running, workstation renamed) previously sat in Queued forever while the
-- operator saw a success message. The watchdog now fails such jobs after this
-- timeout with a message naming the expected workstation.
INSERT INTO app_settings (setting_key, setting_value, value_type, scope, is_secret, description, updated_at) VALUES
    ('Print:QueuedTimeoutMinutes', '5', 'Int', 'Global', 0,
     'Minutes a client-dispatched job may wait in Queued before it is failed as not collected.', UTC_TIMESTAMP(3))
ON DUPLICATE KEY UPDATE description = VALUES(description);
