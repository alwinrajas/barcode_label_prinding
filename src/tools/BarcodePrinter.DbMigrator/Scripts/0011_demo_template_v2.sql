-- ============================================================================
-- 0011 — DEMO-CARTON version 2: layout corrected against the physical label.
--
-- Reported from the running system after comparing the on-screen preview with
-- the customer's printed sample:
--   * Exp Date was missing entirely.
--   * Carton sat under the product picture instead of being the last row of the
--     data column, as it is on the physical label.
--   * The printed timestamp is not wanted on the face of the label. The value
--     is still snapshotted on the job (snap_timestamp_text), so traceability is
--     unaffected — it simply stops being drawn.
--   * The barcode was too tall and too narrow.
--   * The product name was cut at 30 characters.
--
-- A version, not an edit: label_template_versions.artifact_blob is immutable so
-- that a reprint of an old job replays the bytes that job was printed with.
-- Existing jobs keep pointing at version 1 and reprint exactly as before.
--
-- Guarded so it only touches the untouched seed: if an administrator has
-- already replaced or re-versioned this template, nothing happens.
-- ============================================================================

INSERT INTO label_template_versions (
    template_id, version, artifact_blob, artifact_hash, artifact_filename,
    notes, created_at)
SELECT t.id, 2, CAST('{
  "schema": 1,
  "widthMm": 100.0,
  "heightMm": 50.0,
  "dpi": 203,
  "orientation": "Landscape",
  "gapMm": 3.0,
  "elements": [
    { "kind": "barcode", "id": "barcode", "xMm": 26.0, "yMm": 2.5,
      "dataKey": "Product.BarcodeValue", "symbology": "Code128",
      "heightMm": 8.0, "moduleWidthDots": 3, "showHumanReadable": true },

    { "kind": "image", "id": "productImage", "xMm": 3.0, "yMm": 14.0,
      "dataKey": "Product.PrimaryImage", "widthMm": 24.0, "heightMm": 24.0 },

    { "kind": "text", "id": "lblProduct",  "xMm": 30.0, "yMm": 12.5, "text": "Product",   "fontHeightMm": 3.0, "bold": true },
    { "kind": "text", "id": "lblSize",     "xMm": 30.0, "yMm": 19.5, "text": "Size",      "fontHeightMm": 3.0, "bold": true },
    { "kind": "text", "id": "lblQty",      "xMm": 30.0, "yMm": 23.8, "text": "Quantity",  "fontHeightMm": 3.0, "bold": true },
    { "kind": "text", "id": "lblBatch",    "xMm": 30.0, "yMm": 28.1, "text": "Batch",     "fontHeightMm": 3.0, "bold": true },
    { "kind": "text", "id": "lblColor",    "xMm": 30.0, "yMm": 32.4, "text": "Color",     "fontHeightMm": 3.0, "bold": true },
    { "kind": "text", "id": "lblProdDate", "xMm": 30.0, "yMm": 36.7, "text": "Prod Date", "fontHeightMm": 3.0, "bold": true },
    { "kind": "text", "id": "lblExpDate",  "xMm": 30.0, "yMm": 41.0, "text": "Exp Date",  "fontHeightMm": 3.0, "bold": true },
    { "kind": "text", "id": "lblCarton",   "xMm": 30.0, "yMm": 45.3, "text": "Carton",    "fontHeightMm": 3.2, "bold": true },

    { "kind": "text", "id": "sepProduct",  "xMm": 48.0, "yMm": 12.5, "text": ":", "fontHeightMm": 3.0 },
    { "kind": "text", "id": "sepSize",     "xMm": 48.0, "yMm": 19.5, "text": ":", "fontHeightMm": 3.0 },
    { "kind": "text", "id": "sepQty",      "xMm": 48.0, "yMm": 23.8, "text": ":", "fontHeightMm": 3.0 },
    { "kind": "text", "id": "sepBatch",    "xMm": 48.0, "yMm": 28.1, "text": ":", "fontHeightMm": 3.0 },
    { "kind": "text", "id": "sepColor",    "xMm": 48.0, "yMm": 32.4, "text": ":", "fontHeightMm": 3.0 },
    { "kind": "text", "id": "sepProdDate", "xMm": 48.0, "yMm": 36.7, "text": ":", "fontHeightMm": 3.0 },
    { "kind": "text", "id": "sepExpDate",  "xMm": 48.0, "yMm": 41.0, "text": ":", "fontHeightMm": 3.0 },
    { "kind": "text", "id": "sepCarton",   "xMm": 48.0, "yMm": 45.3, "text": ":", "fontHeightMm": 3.2 },

    { "kind": "text", "id": "product",  "xMm": 51.0, "yMm": 12.5, "dataKey": "Product.Description",     "fontHeightMm": 3.0, "blockWidthMm": 34.0, "maxLines": 2 },
    { "kind": "text", "id": "size",     "xMm": 51.0, "yMm": 19.5, "dataKey": "Product.Size",            "fontHeightMm": 3.0 },
    { "kind": "text", "id": "quantity", "xMm": 51.0, "yMm": 23.8, "dataKey": "Effective.QuantityText",  "fontHeightMm": 3.0 },
    { "kind": "text", "id": "batch",    "xMm": 51.0, "yMm": 28.1, "dataKey": "Effective.Batch",         "fontHeightMm": 3.0 },
    { "kind": "text", "id": "color",    "xMm": 51.0, "yMm": 32.4, "dataKey": "Product.Color",           "fontHeightMm": 3.0 },
    { "kind": "text", "id": "prodDate", "xMm": 51.0, "yMm": 36.7, "dataKey": "Effective.ProductionDate", "fontHeightMm": 3.0, "formatString": "dd/MM/yyyy" },
    { "kind": "text", "id": "expDate",  "xMm": 51.0, "yMm": 41.0, "dataKey": "Effective.ExpiryDate",     "fontHeightMm": 3.0, "formatString": "dd/MM/yyyy" },
    { "kind": "text", "id": "carton",   "xMm": 51.0, "yMm": 45.3, "dataKey": "Carton.Text",              "fontHeightMm": 3.2, "bold": true },

    { "kind": "qr",   "id": "feedbackQr", "xMm": 87.0, "yMm": 34.0,
      "dataKey": "Settings.FeedbackFormUrl", "magnification": 3, "errorCorrection": "M" }
  ]
}' AS BINARY), '', 'demo-carton-v2.labeldef.json',
    'Layout corrected against the printed sample: Exp Date added, Carton moved into the data column, timestamp removed from the face, barcode widened and shortened, product name no longer clipped.',
    UTC_TIMESTAMP(3)
FROM label_templates t
WHERE t.code = 'DEMO-CARTON'
  AND t.current_version = 1
  -- Only the untouched seed carries the timestamp element; an administrator who
  -- has already reworked this template keeps whatever they made.
  AND EXISTS (
      SELECT 1 FROM label_template_versions v
      WHERE v.template_id = t.id AND v.version = 1
        AND CAST(v.artifact_blob AS CHAR CHARACTER SET utf8mb4) LIKE '%"id": "timestamp"%')
  AND NOT EXISTS (
      SELECT 1 FROM label_template_versions v2 WHERE v2.template_id = t.id AND v2.version = 2);

UPDATE label_template_versions v
JOIN label_templates t ON t.id = v.template_id AND t.code = 'DEMO-CARTON'
SET v.artifact_hash = SHA2(CAST(v.artifact_blob AS CHAR CHARACTER SET utf8mb4), 256)
WHERE v.version = 2 AND (v.artifact_hash IS NULL OR v.artifact_hash = '');

-- sample_value is the adapter's COMMAND INDEX: the 0-based position among
-- elements that carry a dataKey, in declaration order. Static labels and the
-- ':' separators have no dataKey and therefore consume no index.
INSERT INTO label_template_fields (
    template_version_id, placeholder_ref, field_label, data_key, data_kind,
    format_string, transform, max_length, overflow, is_required, sample_value, sort_order)
SELECT v.id, f.placeholder_ref, f.field_label, f.data_key, f.data_kind,
       f.format_string, f.transform, f.max_length, f.overflow, f.is_required,
       f.cmd_index, f.sort_order
FROM label_template_versions v
JOIN label_templates t ON t.id = v.template_id AND t.code = 'DEMO-CARTON' AND v.version = 2
JOIN (
    SELECT '1'  AS placeholder_ref, 'Barcode'   AS field_label, 'Product.BarcodeValue'  AS data_key, 'Barcode' AS data_kind, NULL AS format_string, 'None' AS transform, NULL AS max_length, 'Error' AS overflow, 1 AS is_required, '0' AS cmd_index, 1 AS sort_order
    UNION ALL SELECT '2',  'Product image', 'Product.PrimaryImage',     'Image',    NULL,         'None', NULL, 'Truncate', 0, '1',  2
    -- 80 characters with Shrink: the block width decides what is drawn, so a
    -- long name is scaled down rather than silently chopped mid-word.
    UNION ALL SELECT '3',  'Product',       'Product.Description',      'Text',     NULL,         'None', 80,   'Shrink',   1, '2',  3
    UNION ALL SELECT '4',  'Size',          'Product.Size',             'Text',     NULL,         'None', 16,   'Truncate', 0, '3',  4
    UNION ALL SELECT '5',  'Quantity',      'Effective.QuantityText',   'Text',     NULL,         'None', 16,   'Truncate', 0, '4',  5
    UNION ALL SELECT '6',  'Batch',         'Effective.Batch',          'Text',     NULL,         'None', 24,   'Truncate', 0, '5',  6
    UNION ALL SELECT '7',  'Color',         'Product.Color',            'Text',     NULL,         'None', 24,   'Truncate', 0, '6',  7
    UNION ALL SELECT '8',  'Prod Date',     'Effective.ProductionDate', 'DateTime', 'dd/MM/yyyy', 'None', NULL, 'Error',    0, '7',  8
    UNION ALL SELECT '9',  'Exp Date',      'Effective.ExpiryDate',     'DateTime', 'dd/MM/yyyy', 'None', NULL, 'Error',    0, '8',  9
    UNION ALL SELECT '10', 'Carton',        'Carton.Text',              'Text',     NULL,         'None', 24,   'Error',    0, '9',  10
    UNION ALL SELECT '11', 'Feedback QR',   'Settings.FeedbackFormUrl', 'QrCode',   NULL,         'None', NULL, 'Error',    0, '10', 11
) f
WHERE NOT EXISTS (
    SELECT 1 FROM label_template_fields x
    WHERE x.template_version_id = v.id AND x.placeholder_ref = f.placeholder_ref);

-- Promote only once the new version is fully mapped, so no print can ever land
-- on a version whose fields are still half-inserted.
UPDATE label_templates t
JOIN label_template_versions v ON v.template_id = t.id AND v.version = 2
SET t.current_version = 2
WHERE t.code = 'DEMO-CARTON'
  AND t.current_version = 1
  AND (SELECT COUNT(*) FROM label_template_fields f WHERE f.template_version_id = v.id) = 11;
