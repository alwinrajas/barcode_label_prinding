using BarcodePrinter.Labels;
using BarcodePrinter.Labels.Binding;
using BarcodePrinter.Labels.Zpl;
using FluentAssertions;
using Xunit;

namespace BarcodePrinter.Labels.Tests;

/// <summary>
/// Exercises the full registration → render path against the synthetic capture
/// in Fixtures/ (see that folder's README: it is a stand-in for the client's
/// real template, which is blocker BQ-2).
/// </summary>
public class ZplTemplateAdapterTests
{
    private static readonly ZplTemplateAdapter Adapter = new();

    private static string Fixture() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "captured-label.prn"));

    // ---- Inspect --------------------------------------------------------------

    [Fact]
    public void Inspect_finds_every_field_and_infers_its_kind()
    {
        var fields = Adapter.Inspect(Fixture());

        // The barcode, the QR and every text field — including the static
        // captions, which the admin will simply leave unmapped.
        fields.Should().NotBeEmpty();

        fields.Should().ContainSingle(f => f.InferredKind == FieldDataKind.Barcode)
            .Which.SampleValue.Should().Be("5GCAPM2N");

        fields.Should().ContainSingle(f => f.InferredKind == FieldDataKind.QrCode)
            .Which.SampleValue.Should().StartWith("LA,https://");

        // Positions come through so the mapping UI can show admins where each
        // field sits on the label.
        var barcode = fields.Single(f => f.InferredKind == FieldDataKind.Barcode);
        barcode.X.Should().Be(232);
        barcode.Y.Should().Be(16);
    }

    [Fact]
    public void Inspect_surfaces_the_values_seen_on_the_physical_samples()
    {
        var samples = Adapter.Inspect(Fixture()).Select(f => f.SampleValue).ToList();

        // Exactly what the photographs show.
        samples.Should().Contain("5G M2 CAP");
        samples.Should().Contain("M2");
        samples.Should().Contain("750[D]");
        samples.Should().Contain("CONE");
        samples.Should().Contain("NATURAL");
        samples.Should().Contain("21/07/2026");
        samples.Should().Contain("21/07/2027");

        // Static captions are detected too — they stay literal when unmapped.
        samples.Should().Contain("Product");
        samples.Should().Contain("Carton");
    }

    [Fact]
    public void Inspect_does_not_modify_the_artifact()
    {
        var original = Fixture();
        Adapter.Inspect(original);
        Fixture().Should().Be(original);
    }

    // ---- Prepare ---------------------------------------------------------------

    [Fact]
    public void Prepare_replaces_only_mapped_fields_and_keeps_geometry_byte_for_byte()
    {
        var prepared = Adapter.Prepare(Fixture(), "R:LBL01.ZPL", Mapping().Placeholders);

        // Client geometry survives untouched (A-15).
        prepared.DefinePayload.Should().Contain("^PW812");
        prepared.DefinePayload.Should().Contain("^FO232,16^BY2,3.0,56^BCN,56,Y,N,N");
        prepared.DefinePayload.Should().Contain("^FO264,112^A0N,26,26");

        // Mapped values became placeholders.
        prepared.DefinePayload.Should().Contain("^FN1");
        prepared.DefinePayload.Should().NotContain("5GCAPM2N");
        prepared.DefinePayload.Should().NotContain("750[D]");

        // Static captions stayed literal.
        prepared.DefinePayload.Should().Contain("^FDProduct^FS");
        prepared.DefinePayload.Should().Contain("^FDSize^FS");

        // Wrapped as a stored format, and the capture's own ^PQ removed —
        // quantity belongs to the recall, not the definition.
        prepared.DefinePayload.Should().StartWith("^XA^DFR:LBL01.ZPL^FS");
        prepared.DefinePayload.Should().EndWith("^XZ");
        prepared.DefinePayload.Should().NotContain("^PQ");
    }

    [Fact]
    public void Prepare_output_matches_the_approved_golden_file()
    {
        var prepared = Adapter.Prepare(Fixture(), "R:LBL01.ZPL", Mapping().Placeholders);
        GoldenFile.Assert(prepared.DefinePayload, "prepared-define.zpl");
    }

    // ---- Render ------------------------------------------------------------------

    [Fact]
    public void RenderRecall_emits_only_the_recall_and_values()
    {
        var (mappings, placeholders) = Mapping();
        var prepared = Adapter.Prepare(Fixture(), "R:LBL01.ZPL", placeholders);
        var bound = new FieldBinder().Bind(mappings, SampleContext());

        var output = Adapter.RenderRecall(new RenderRequest(prepared, mappings, bound));

        output.Should().StartWith("^XA^XFR:LBL01.ZPL^FS");
        output.Should().EndWith("^XZ");
        output.Should().Contain("^FN1^FD5GCAPM2N^FS");
        output.Should().Contain("^FN8^FD1^FS");        // carton

        // The whole point: per-label payload carries no geometry at all.
        output.Should().NotContain("^FO");
        output.Should().NotContain("^A0N");
        output.Length.Should().BeLessThan(prepared.DefinePayload.Length / 3,
            "per-label payload must collapse to a fraction of the layout (§6.2)");
    }

    [Fact]
    public void RenderRecall_matches_the_approved_golden_file()
    {
        var (mappings, placeholders) = Mapping();
        var prepared = Adapter.Prepare(Fixture(), "R:LBL01.ZPL", placeholders);
        var bound = new FieldBinder().Bind(mappings, SampleContext());

        GoldenFile.Assert(
            Adapter.RenderRecall(new RenderRequest(prepared, mappings, bound)),
            "render-recall.zpl");
    }

    [Fact]
    public void RenderRecall_sets_quantity_only_for_multiple_copies()
    {
        var (mappings, placeholders) = Mapping();
        var prepared = Adapter.Prepare(Fixture(), "R:LBL01.ZPL", placeholders);
        var bound = new FieldBinder().Bind(mappings, SampleContext());

        Adapter.RenderRecall(new RenderRequest(prepared, mappings, bound))
            .Should().NotContain("^PQ");
        Adapter.RenderRecall(new RenderRequest(prepared, mappings, bound, Copies: 3))
            .Should().Contain("^PQ3");
    }

    [Fact]
    public void RenderInline_is_the_fallback_for_printers_without_stored_formats()
    {
        var (mappings, placeholders) = Mapping();
        var bound = new FieldBinder().Bind(mappings, SampleContext());

        var output = Adapter.RenderInline(Fixture(), placeholders, bound);

        // Risk R-13: same printed result, full payload per label.
        output.Should().Contain("^PW812");
        output.Should().Contain("^FO232,16");
        output.Should().Contain("^FD5GCAPM2N^FS");
        output.Should().Contain("^FDCONE^FS");
        output.Should().NotContain("^FN");
        GoldenFile.Assert(output, "render-inline.zpl");
    }

    [Fact]
    public void Carton_number_changes_per_label_while_the_layout_is_sent_once()
    {
        var (mappings, placeholders) = Mapping();
        var prepared = Adapter.Prepare(Fixture(), "R:LBL01.ZPL", placeholders);
        var binder = new FieldBinder();

        var outputs = Enumerable.Range(41, 3).Select(carton =>
        {
            var context = SampleContext() with
            {
                Carton = new CartonValues(carton, 65, 41, 65, carton.ToString()),
            };
            return Adapter.RenderRecall(new RenderRequest(prepared, mappings, binder.Bind(mappings, context)));
        }).ToList();

        outputs[0].Should().Contain("^FN8^FD41^FS");
        outputs[1].Should().Contain("^FN8^FD42^FS");
        outputs[2].Should().Contain("^FN8^FD43^FS");
        outputs.Should().OnlyContain(o => !o.Contains("^FO"));
    }

    /// <summary>
    /// Regression: the QR payload carries a `LA,` error-correction/input-mode
    /// prefix that is ZPL SYNTAX, not part of the URL. Dropping it on recall
    /// makes the printer read the first characters of the URL as the mode and
    /// emit a corrupt symbol — invisible until someone scans a printed label.
    /// </summary>
    [Fact]
    public void Qr_mode_prefix_survives_both_render_paths()
    {
        var (mappings, placeholders) = Mapping();
        var prepared = Adapter.Prepare(Fixture(), "R:LBL01.ZPL", placeholders);
        var bound = new FieldBinder().Bind(mappings, SampleContext());

        prepared.PlaceholderPrefixes.Should().ContainKey("11").WhoseValue.Should().Be("LA,");

        Adapter.RenderRecall(new RenderRequest(prepared, mappings, bound))
            .Should().Contain("^FN11^FDLA,https://forms.gle/EXAMPLE^FS");

        Adapter.RenderInline(Fixture(), placeholders, bound)
            .Should().Contain("^FDLA,https://forms.gle/EXAMPLE^FS");
    }

    [Theory]
    [InlineData("LA,https://x", "LA,", "https://x")]
    [InlineData("QM,PAYLOAD", "QM,", "PAYLOAD")]
    [InlineData("HA,DATA", "HA,", "DATA")]
    [InlineData("PLAIN TEXT", "", "PLAIN TEXT")]
    [InlineData("AB,not a mode", "", "AB,not a mode")]
    public void Qr_prefix_detection_is_precise(string data, string expectedPrefix, string expectedPayload)
    {
        ZplTemplateAdapter.TrySplitQrPrefix(data, out var prefix, out var payload);
        prefix.Should().Be(expectedPrefix);
        payload.Should().Be(expectedPayload);
    }

    // ---- Escaping ---------------------------------------------------------------

    [Fact]
    public void Field_data_containing_zpl_control_characters_is_escaped()
    {
        var (mappings, placeholders) = Mapping();
        var prepared = Adapter.Prepare(Fixture(), "R:LBL01.ZPL", placeholders);

        var context = SampleContext() with
        {
            Product = SampleContext().Product with { Description = "CAP ^ SPECIAL ~ 50%" },
        };
        var output = Adapter.RenderRecall(
            new RenderRequest(prepared, mappings, new FieldBinder().Bind(mappings, context)));

        // The raw control characters must never reach the printer.
        output.Should().Contain("^FH");
        output.Should().Contain("_5E");   // ^
        output.Should().Contain("_7E");   // ~
        output.Should().NotContain("CAP ^ SPECIAL");
    }

    // ---- shared test data ----------------------------------------------------------

    /// <summary>Maps the fixture's variable fields; ^FD indices come from
    /// Inspect() and mirror what an admin would choose in the mapping UI.</summary>
    private static (IReadOnlyList<FieldMapping> Mappings, Dictionary<int, string> Placeholders) Mapping()
    {
        var fields = Adapter.Inspect(Fixture());

        // Pick the field whose sample value matches each known sample.
        int IndexOfSample(string sample) =>
            fields.First(f => f.SampleValue == sample).CommandIndex;

        var placeholders = new Dictionary<int, string>
        {
            [IndexOfSample("5GCAPM2N")] = "1",
            [IndexOfSample("5G M2 CAP")] = "2",
            [IndexOfSample("M2")] = "3",
            [IndexOfSample("750[D]")] = "4",
            [IndexOfSample("CONE")] = "5",
            [IndexOfSample("NATURAL")] = "6",
            [IndexOfSample("21/07/2026")] = "7",
            [IndexOfSample("21/07/2027")] = "9",
            [IndexOfSample("1")] = "8",
            [IndexOfSample("12/08/2026 17:04")] = "10",
            [fields.First(f => f.InferredKind == FieldDataKind.QrCode).CommandIndex] = "11",
        };

        IReadOnlyList<FieldMapping> mappings =
        [
            new("1", "Product.BarcodeValue", FieldDataKind.Barcode, IsRequired: true),
            new("2", "Product.Description", FieldDataKind.Text),
            new("3", "Product.Size", FieldDataKind.Text),
            new("4", "Effective.QuantityText", FieldDataKind.Text),
            new("5", "Effective.Batch", FieldDataKind.Text),
            new("6", "Product.Color", FieldDataKind.Text),
            new("7", "Effective.ProductionDate", FieldDataKind.DateTime),
            new("9", "Effective.ExpiryDate", FieldDataKind.DateTime),
            new("8", "Carton.Text", FieldDataKind.Text),
            new("10", "Now", FieldDataKind.DateTime),
            new("11", TokenVocabulary.FeedbackUrlKey, FieldDataKind.QrCode),
        ];

        return (mappings, placeholders);
    }

    internal static PrintContext SampleContext() => new(
        new ProductValues("5GCAPM2N", "5G M2 CAP", "5GCAPM2N", "PCS", "M2", "NATURAL", null),
        new EffectiveValues("CONE", new DateOnly(2026, 7, 21), new DateOnly(2027, 7, 21), "750[D]"),
        new CartonValues(1, 10, 1, 10, "1"),
        new JobValues("PJ-260812-000431", "admin", "Line-2 Zebra", false),
        new SettingsValues("https://forms.gle/EXAMPLE", "Demo Co", "dd/MM/yyyy", "dd/MM/yyyy HH:mm"),
        new DateTime(2026, 8, 12, 17, 4, 0, DateTimeKind.Local));
}
