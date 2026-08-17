-- ============================================================================
-- 0102 — DEMO label template (Native format)
--
-- ***** THIS IS A DEVELOPMENT AND TESTING TEMPLATE. IT IS NOT THE CLIENT'S
-- ***** LABEL. Its dimensions, font sizes, barcode symbology, QR placement and
-- ***** field positions are PLACEHOLDERS chosen so the system can be exercised
-- ***** end to end while the client's real template is outstanding (BQ-2).
--
-- Every value here is DATA. When the client's specification or artwork arrives:
--   * if they supply a printer file, register it under template_format='Zpl'
--     and map its fields — this row is simply deactivated;
--   * if they supply a drawing or measurements, edit the definition JSON below
--     (or through the template screen).
-- In neither case does any printing code change. That is the point of the
-- Native format: the layout is configuration, and the engine never sees it.
--
-- It is seeded ACTIVE and DEFAULT so a fresh installation can print immediately
-- and be tested. Registering a real template and setting it default demotes this
-- one; deactivating it removes it from the print screen entirely. Both are
-- ordinary administrator actions, not migrations.
-- ============================================================================



-- Fresh databases only: never resurrect or overwrite a template an administrator
-- has since edited or replaced.
INSERT INTO label_templates (
    code, name, description, template_format,
    width_mm, height_mm, dpi, gap_mm, orientation, layout_type,
    media_type, media_tracking, current_version,
    is_active, is_default, requires_image, requires_qr, created_at)
SELECT
    'DEMO-CARTON',
    'DEMO carton label (development only)',
    'PLACEHOLDER layout for development and testing. Replace with the client''s template when supplied (BQ-2).',
    'Native',
    101.60, 152.40, 203, 3.00, 'Portrait', 'OneUp',
    'DirectThermal', 'Gap', 1,
    1, 1, 1, 1, UTC_TIMESTAMP(3)
WHERE NOT EXISTS (SELECT 1 FROM label_templates WHERE code = 'DEMO-CARTON');

INSERT INTO label_template_versions (
    template_id, version, artifact_blob, artifact_hash, artifact_filename,
    notes, created_at)
SELECT
    t.id, 1, CAST('{
  "schema": 1,
  "widthMm": 101.6,
  "heightMm": 152.4,
  "dpi": 203,
  "orientation": "Portrait",
  "gapMm": 3.0,
  "elements": [
    { "kind": "text", "id": "timestamp", "xMm": 4.0, "yMm": 4.0,
      "dataKey": "Job.PrintedAt", "fontHeightMm": 2.6 },

    { "kind": "barcode", "id": "barcode", "xMm": 16.0, "yMm": 11.0,
      "dataKey": "Product.BarcodeValue", "symbology": "Code128",
      "heightMm": 18.0, "moduleWidthDots": 2, "showHumanReadable": true },

    { "kind": "image", "id": "productImage", "xMm": 4.0, "yMm": 40.0,
      "dataKey": "Product.PrimaryImage", "widthMm": 30.0, "heightMm": 30.0 },

    { "kind": "text", "id": "lblProduct",  "xMm": 4.0, "yMm": 76.0,  "text": "Product",   "fontHeightMm": 3.4, "bold": true },
    { "kind": "text", "id": "lblSize",     "xMm": 4.0, "yMm": 86.0,  "text": "Size",      "fontHeightMm": 3.4, "bold": true },
    { "kind": "text", "id": "lblQty",      "xMm": 4.0, "yMm": 96.0,  "text": "Quantity",  "fontHeightMm": 3.4, "bold": true },
    { "kind": "text", "id": "lblBatch",    "xMm": 4.0, "yMm": 106.0, "text": "Batch",     "fontHeightMm": 3.4, "bold": true },
    { "kind": "text", "id": "lblColor",    "xMm": 4.0, "yMm": 116.0, "text": "Color",     "fontHeightMm": 3.4, "bold": true },
    { "kind": "text", "id": "lblProdDate", "xMm": 4.0, "yMm": 126.0, "text": "Prod Date", "fontHeightMm": 3.4, "bold": true },

    { "kind": "text", "id": "sepProduct",  "xMm": 30.0, "yMm": 76.0,  "text": ":", "fontHeightMm": 3.4 },
    { "kind": "text", "id": "sepSize",     "xMm": 30.0, "yMm": 86.0,  "text": ":", "fontHeightMm": 3.4 },
    { "kind": "text", "id": "sepQty",      "xMm": 30.0, "yMm": 96.0,  "text": ":", "fontHeightMm": 3.4 },
    { "kind": "text", "id": "sepBatch",    "xMm": 30.0, "yMm": 106.0, "text": ":", "fontHeightMm": 3.4 },
    { "kind": "text", "id": "sepColor",    "xMm": 30.0, "yMm": 116.0, "text": ":", "fontHeightMm": 3.4 },
    { "kind": "text", "id": "sepProdDate", "xMm": 30.0, "yMm": 126.0, "text": ":", "fontHeightMm": 3.4 },

    { "kind": "text", "id": "product",  "xMm": 35.0, "yMm": 76.0,  "dataKey": "Product.Description",     "fontHeightMm": 3.4, "blockWidthMm": 62.0, "maxLines": 2 },
    { "kind": "text", "id": "size",     "xMm": 35.0, "yMm": 86.0,  "dataKey": "Product.Size",            "fontHeightMm": 3.4 },
    { "kind": "text", "id": "quantity", "xMm": 35.0, "yMm": 96.0,  "dataKey": "Effective.QuantityText",  "fontHeightMm": 3.4 },
    { "kind": "text", "id": "batch",    "xMm": 35.0, "yMm": 106.0, "dataKey": "Effective.Batch",         "fontHeightMm": 3.4 },
    { "kind": "text", "id": "color",    "xMm": 35.0, "yMm": 116.0, "dataKey": "Product.Color",           "fontHeightMm": 3.4 },
    { "kind": "text", "id": "prodDate", "xMm": 35.0, "yMm": 126.0, "dataKey": "Effective.ProductionDate","fontHeightMm": 3.4, "formatString": "dd/MM/yyyy" },

    { "kind": "text", "id": "lblCarton", "xMm": 4.0,  "yMm": 140.0, "text": "Carton", "fontHeightMm": 4.0, "bold": true },
    { "kind": "text", "id": "carton",    "xMm": 26.0, "yMm": 140.0, "dataKey": "Carton.Text", "fontHeightMm": 4.0, "bold": true },

    { "kind": "qr", "id": "feedbackQr", "xMm": 80.0, "yMm": 132.0,
      "dataKey": "Settings.FeedbackFormUrl", "magnification": 4, "errorCorrection": "M" }
  ]
}' AS BINARY), '', 'demo-carton.labeldef.json',
    'Seeded DEMO template. Not the client label.', UTC_TIMESTAMP(3)
FROM label_templates t
WHERE t.code = 'DEMO-CARTON'
  AND NOT EXISTS (
      SELECT 1 FROM label_template_versions v WHERE v.template_id = t.id AND v.version = 1);

-- Hash the bytes that were actually stored, rather than a second copy of the
-- literal that could drift from them.
UPDATE label_template_versions v
JOIN label_templates t ON t.id = v.template_id AND t.code = 'DEMO-CARTON'
SET v.artifact_hash = SHA2(CAST(v.artifact_blob AS CHAR CHARACTER SET utf8mb4), 256)
WHERE v.version = 1 AND (v.artifact_hash IS NULL OR v.artifact_hash = '');

-- Field mapping. sample_value holds the adapter's COMMAND INDEX (0-based, in
-- bindable-element declaration order) and placeholder_ref the ^FN number — the
-- same two-part convention a client-supplied ZPL file uses, so both formats
-- travel the identical render path.
INSERT INTO label_template_fields (
    template_version_id, placeholder_ref, field_label, data_key, data_kind,
    format_string, transform, max_length, overflow, is_required, sample_value, sort_order)
SELECT v.id, f.placeholder_ref, f.field_label, f.data_key, f.data_kind,
       f.format_string, f.transform, f.max_length, f.overflow, f.is_required,
       f.cmd_index, f.sort_order
FROM label_template_versions v
JOIN label_templates t ON t.id = v.template_id AND t.code = 'DEMO-CARTON' AND v.version = 1
JOIN (
    SELECT '1'  AS placeholder_ref, 'Barcode'         AS field_label, 'Product.BarcodeValue'     AS data_key, 'Barcode'  AS data_kind, NULL         AS format_string, 'None' AS transform, NULL AS max_length, 'Error'    AS overflow, 1 AS is_required, '1' AS cmd_index, 1  AS sort_order
    UNION ALL SELECT '2',  'Product image', 'Product.PrimaryImage',     'Image',    NULL,         'None', NULL, 'Truncate', 0, '2', 2
    UNION ALL SELECT '3',  'Product',       'Product.Description',      'Text',     NULL,         'None', 30,   'Truncate', 1, '3', 3
    UNION ALL SELECT '4',  'Size',          'Product.Size',             'Text',     NULL,         'None', 16,   'Truncate', 0, '4', 4
    UNION ALL SELECT '5',  'Quantity',      'Effective.QuantityText',   'Text',     NULL,         'None', 16,   'Truncate', 0, '5', 5
    UNION ALL SELECT '6',  'Batch',         'Effective.Batch',          'Text',     NULL,         'None', 24,   'Truncate', 0, '6', 6
    UNION ALL SELECT '7',  'Color',         'Product.Color',            'Text',     NULL,         'None', 24,   'Truncate', 0, '7', 7
    UNION ALL SELECT '8',  'Prod Date',     'Effective.ProductionDate', 'DateTime', 'dd/MM/yyyy', 'None', NULL, 'Error',    0, '8', 8
    UNION ALL SELECT '9',  'Carton',        'Carton.Text',              'Text',     NULL,         'None', 24,   'Error',    0, '9', 9
    UNION ALL SELECT '10', 'Feedback QR',   'Settings.FeedbackFormUrl', 'QrCode',   NULL,         'None', NULL, 'Error',    0, '10', 10
    UNION ALL SELECT '11', 'Timestamp',     'Job.PrintedAt',            'DateTime', NULL,         'None', NULL, 'Error',    0, '0', 11
) f
WHERE NOT EXISTS (
    SELECT 1 FROM label_template_fields x
    WHERE x.template_version_id = v.id AND x.placeholder_ref = f.placeholder_ref);
