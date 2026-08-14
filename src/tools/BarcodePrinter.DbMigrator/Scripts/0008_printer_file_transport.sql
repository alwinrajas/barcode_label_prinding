-- ============================================================================
-- 0008 — Add the File transport to printers.connection_type.
--
-- FileTransport writes the exact printer bytes to disk (blueprint §7.2). It is
-- a first-class production capability, not a test fixture: commissioning a new
-- site, diagnosing a scanning complaint, or reproducing a customer's label all
-- need the real ZPL without occupying hardware. It is also how the print
-- pipeline is exercised in CI where no printer exists.
-- ============================================================================

ALTER TABLE printers
    MODIFY COLUMN connection_type
        ENUM('NetworkTcp','WindowsRaw','WindowsGraphics','File') NOT NULL;
