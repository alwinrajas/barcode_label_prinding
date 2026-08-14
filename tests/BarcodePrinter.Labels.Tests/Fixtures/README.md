# Label fixtures — TEST ASSETS ONLY

`captured-label.prn` is a **synthetic** ZPL capture authored by the development
team from the two production label photographs in
`Barcode Printer docs/boobalan/`. It exists so the template engine can be built
and regression-tested **before** the client supplies their real templates
(blocker BQ-2).

It reproduces only what the photographs actually show:

| Element | Source |
|---|---|
| 1D barcode + human-readable code, top-centre | Observed |
| Product image square, left | Observed (`^GFA` stub — real graphic comes from the product image cache) |
| `Product / Size / Quantity / Batch / Color / Prod Date / Exp Date` label:value pairs, right | Observed, in this order |
| `Carton` row below the block | Observed |
| Timestamp bottom-left | **Handwritten annotation** on the sample — not on the printed label (C-8) |
| QR bottom-right | **Handwritten annotation** on the sample — not on the printed label (C-8) |

## What this fixture is NOT

It is **not** a claim about the client's label. Every absolute value below is a
placeholder chosen so the engine has something concrete to render, and all of
them are expected to change when the real file arrives:

- `^PW812 ^LL0609` — a 4"×3" label at 203 dpi. **Actual size and dpi are C-4.**
- `^BCN` (Code 128) — chosen because the observed codes are alphanumeric and
  variable-length, which rules out EAN-13/UPC-A/ITF-14. **Actual symbology is C-6.**
- `dd/MM/yyyy` dates — per the client's stated format. The photographs show
  `21/Jul/2026`. **The conflict is C-1.**
- QR and timestamp coordinates — invented; the sample has no space allocated
  for them. **C-8 / BQ-3.**

When the client's template lands, it is registered through the normal admin
flow and this fixture stays behind as a regression test.
