-- Last time the printer's dispatch path showed signs of life: for client-
-- dispatched printers, the owning workstation's poll; for server-dispatched
-- printers, a successful dispatch or status probe. Drives the Printers screen's
-- online/last-seen column — previously no status existed anywhere.
ALTER TABLE printers ADD COLUMN last_seen_at DATETIME(3) NULL;
