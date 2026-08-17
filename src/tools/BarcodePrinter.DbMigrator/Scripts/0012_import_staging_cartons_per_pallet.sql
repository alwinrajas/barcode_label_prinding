-- ============================================================================
-- 0012 — product_import_staging gains a real column for Cartons per Pallet.
--
-- The importer began mapping cartons_per_pallet, but the staging table had no
-- column for it, so the value was briefly smuggled through the now-unused
-- n_category_id column. That worked (staging has no FKs and is truncated per
-- batch) and it was isolated behind one constant, but a column whose name says
-- "category" and whose contents are a pallet count is a trap for whoever reads
-- this next. Give the value its own column and let the name mean what it says.
--
-- Additive and nullable: staging rows are deleted at the end of every batch, so
-- there is nothing to backfill and no existing import is affected.
-- ============================================================================
ALTER TABLE product_import_staging
    ADD COLUMN n_cartons_per_pallet INT UNSIGNED NULL AFTER n_carton_qty;
