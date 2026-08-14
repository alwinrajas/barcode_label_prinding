using BarcodePrinter.Labels;
using BarcodePrinter.Labels.Binding;
using BarcodePrinter.Labels.Native;
using FluentAssertions;
using Xunit;

namespace BarcodePrinter.Labels.Tests;

/// <summary>
/// The Native format exists so a layout is configuration. These pin the two
/// properties that makes true: geometry is honoured exactly, and the output is
/// an ordinary stored format that the rest of the pipeline cannot distinguish
/// from a client-supplied file.
/// </summary>
public class NativeTemplateAdapterTests
{
    private static readonly NativeTemplateAdapter Adapter = new();

    private static LabelDefinition Label(params LabelElement[] elements) => new()
    {
        WidthMm = 100m,
        HeightMm = 50m,
        Dpi = 203,
        Elements = elements,
    };

    private static TextElement Text(string id, decimal x, decimal y, string? key = null,
        string? text = null, decimal height = 3m) => new()
    {
        Id = id, XMm = x, YMm = y, DataKey = key, Text = text, FontHeightMm = height,
    };

    // ---- geometry -----------------------------------------------------------------

    [Theory]
    [InlineData(203, 25.4, 203)]
    [InlineData(300, 25.4, 300)]
    [InlineData(203, 10.0, 80)]
    [InlineData(300, 10.0, 118)]
    public void Millimetres_convert_to_the_printer_s_dots(int dpi, double mm, int expectedDots)
    {
        var label = Label(Text("t", 0, 0, text: "x")) with { Dpi = dpi };
        label.MmToDots((decimal)mm).Should().Be(expectedDots);
    }

    /// <summary>The same definition on a 300-dpi printer must be the same
    /// PHYSICAL size, not the same number of dots — otherwise a 100 mm label
    /// prints at 68 mm and nobody notices until it is on the carton.</summary>
    [Fact]
    public void The_same_definition_keeps_its_physical_size_at_a_different_resolution()
    {
        var at203 = Label(Text("t", 50m, 25m, text: "x"));
        var at300 = at203.AtDpi(300);

        at203.WidthDots.Should().Be(799);
        at300.WidthDots.Should().Be(1181);

        // 50 mm is 50 mm on both.
        (at203.MmToDots(50m) / 203m).Should().BeApproximately(at300.MmToDots(50m) / 300m, 0.01m);
    }

    [Fact]
    public void Element_positions_are_emitted_as_dots_from_the_top_left()
    {
        var zpl = Adapter.Prepare(
            Label(Text("caption", 10m, 20m, text: "Batch")).ToJson(), "R:T.ZPL", new Dictionary<int, string>())
            .DefinePayload;

        // 10 mm = 80 dots, 20 mm = 160 dots at 203 dpi.
        zpl.Should().Contain("^FO80,160");
        zpl.Should().Contain("^PW799");
        zpl.Should().Contain("^LL400", "50 mm is 399.6 dots at 203 dpi");
    }

    // ---- the stored format --------------------------------------------------------

    [Fact]
    public void A_definition_compiles_to_an_ordinary_stored_format()
    {
        var prepared = Adapter.Prepare(
            Label(
                Text("caption", 5m, 5m, text: "Product"),
                Text("value", 25m, 5m, key: "Product.Description")).ToJson(),
            "R:DEMO.ZPL", new Dictionary<int, string>());

        prepared.DefinePayload.Should().StartWith("^XA^DFR:DEMO.ZPL^FS");
        prepared.DefinePayload.Should().EndWith("^XZ");
        prepared.DefinePayload.Should().Contain("^CI28", "UTF-8 so accented product names survive");

        // Static text is baked in; bound text becomes a placeholder.
        prepared.DefinePayload.Should().Contain("^FDProduct^FS");
        prepared.DefinePayload.Should().Contain("^FN1^FS");
    }

    [Fact]
    public void Only_data_bound_elements_become_fields()
    {
        var prepared = Adapter.Prepare(
            Label(
                Text("caption", 5m, 5m, text: "Batch"),
                Text("batch", 25m, 5m, key: "Effective.Batch"),
                new BoxElement { Id = "rule", XMm = 0, YMm = 15m, WidthMm = 100m, HeightMm = 0.4m })
            .ToJson(), "R:T.ZPL", new Dictionary<int, string>());

        prepared.Fields.Should().HaveCount(1, "captions and rules never need a value");
        prepared.Fields[0].SampleValue.Should().Be("1");
    }

    [Fact]
    public void Recall_emits_one_field_per_bound_value_and_nothing_else()
    {
        var prepared = Adapter.Prepare(
            Label(Text("batch", 25m, 5m, key: "Effective.Batch")).ToJson(),
            "R:T.ZPL", new Dictionary<int, string>());

        var recall = Adapter.RenderRecall(new RenderRequest(
            prepared,
            [new FieldMapping("1", "Effective.Batch", FieldDataKind.Text)],
            new Dictionary<string, string> { ["1"] = "CONE" },
            Copies: 1));

        recall.Should().Be("^XA^XFR:T.ZPL^FS^FN1^FDCONE^FS^XZ");
    }

    [Fact]
    public void Copies_are_requested_from_the_printer_rather_than_repeated_in_the_payload()
    {
        var prepared = Adapter.Prepare(
            Label(Text("batch", 5m, 5m, key: "Effective.Batch")).ToJson(),
            "R:T.ZPL", new Dictionary<int, string>());

        Adapter.RenderRecall(new RenderRequest(prepared,
            [new FieldMapping("1", "Effective.Batch", FieldDataKind.Text)],
            new Dictionary<string, string> { ["1"] = "CONE" }, Copies: 3))
            .Should().Contain("^PQ3");
    }

    // ---- the QR prefix, which is easy to lose and impossible to see -----------------

    /// <summary>
    /// ZPL carries the QR mode indicator inside the field DATA. Dropping it
    /// produces a symbol that looks perfectly normal and does not scan — a
    /// defect only discoverable on physical media.
    /// </summary>
    [Fact]
    public void The_qr_mode_indicator_survives_into_the_rendered_data()
    {
        var prepared = Adapter.Prepare(
            Label(new QrElement
            {
                Id = "qr", XMm = 80m, YMm = 30m,
                DataKey = "Settings.FeedbackFormUrl", Magnification = 4, ErrorCorrection = "M",
            }).ToJson(), "R:T.ZPL", new Dictionary<int, string>());

        prepared.PlaceholderPrefixes["1"].Should().Be("MA,");

        var recall = Adapter.RenderRecall(new RenderRequest(prepared,
            [new FieldMapping("1", "Settings.FeedbackFormUrl", FieldDataKind.QrCode)],
            new Dictionary<string, string> { ["1"] = "https://forms.gle/EXAMPLE" }, Copies: 1));

        recall.Should().Contain("^FDMA,https://forms.gle/EXAMPLE^FS");
    }

    /// <summary>An image resolves to a raster block substituted into the stored
    /// format. Emitting its value would print a 64-character hash on the label.</summary>
    [Fact]
    public void An_image_field_never_renders_its_hash_as_text()
    {
        var prepared = Adapter.Prepare(
            Label(new ImageElement
            {
                Id = "pic", XMm = 3m, YMm = 3m, WidthMm = 20m, HeightMm = 20m,
                DataKey = "Product.PrimaryImage",
            }).ToJson(), "R:T.ZPL", new Dictionary<int, string>());

        var recall = Adapter.RenderRecall(new RenderRequest(prepared,
            [new FieldMapping("1", "Product.PrimaryImage", FieldDataKind.Image)],
            new Dictionary<string, string> { ["1"] = new string('a', 64) }, Copies: 1));

        recall.Should().NotContain("aaaa");
        recall.Should().Be("^XA^XFR:T.ZPL^FS^XZ");
    }

    // ---- visibility and configuration ----------------------------------------------

    [Fact]
    public void A_hidden_element_is_not_printed_but_keeps_its_configuration()
    {
        var hidden = Text("timestamp", 3m, 3m, key: "Job.PrintedAt") with { Visible = false };
        var prepared = Adapter.Prepare(
            Label(Text("batch", 5m, 20m, key: "Effective.Batch"), hidden).ToJson(),
            "R:T.ZPL", new Dictionary<int, string>());

        prepared.Fields.Should().HaveCount(1);
        prepared.DefinePayload.Should().NotContain("^FO24,24");
    }

    [Theory]
    [InlineData(TextAlignment.Center, "C")]
    [InlineData(TextAlignment.Right, "R")]
    [InlineData(TextAlignment.Left, "L")]
    public void Alignment_is_emitted_as_a_field_block(TextAlignment alignment, string expected)
    {
        var element = Text("v", 5m, 5m, key: "Product.Code") with
        {
            Alignment = alignment, BlockWidthMm = 40m,
        };

        Adapter.Prepare(Label(element).ToJson(), "R:T.ZPL", new Dictionary<int, string>())
            .DefinePayload.Should().Contain($"^FB320,1,0,{expected},0");
    }

    [Fact]
    public void Darkness_and_speed_are_only_emitted_when_configured()
    {
        var plain = Adapter.Prepare(Label(Text("t", 1m, 1m, text: "x")).ToJson(),
            "R:T.ZPL", new Dictionary<int, string>()).DefinePayload;
        plain.Should().NotContain("~SD");
        plain.Should().NotContain("^PR");

        var tuned = Label(Text("t", 1m, 1m, text: "x")) with { Darkness = 22, PrintSpeedIps = 4 };
        var zpl = Adapter.Prepare(tuned.ToJson(), "R:T.ZPL", new Dictionary<int, string>()).DefinePayload;
        zpl.Should().Contain("~SD22");
        zpl.Should().Contain("^PR4");
    }

    // ---- validation: reject what would print wrongly ---------------------------------

    [Fact]
    public void A_definition_with_no_elements_is_rejected()
    {
        var act = () => (Label() with { Elements = [] }).Validate();
        act.Should().Throw<LabelDefinitionException>().WithMessage("*blank*");
    }

    [Fact]
    public void Duplicate_element_ids_are_rejected_because_ids_identify_mappings()
    {
        var act = () => Label(Text("same", 1m, 1m, text: "a"), Text("same", 2m, 2m, text: "b")).Validate();
        act.Should().Throw<LabelDefinitionException>().WithMessage("*used more than once*");
    }

    [Fact]
    public void An_element_positioned_off_the_label_is_rejected()
    {
        var act = () => Label(Text("t", 150m, 5m, text: "x")).Validate();
        act.Should().Throw<LabelDefinitionException>().WithMessage("*outside*");
    }

    [Fact]
    public void Centred_text_without_a_block_width_is_rejected()
    {
        var element = Text("t", 5m, 5m, text: "x") with { Alignment = TextAlignment.Center };
        var act = () => Label(element).Validate();
        act.Should().Throw<LabelDefinitionException>().WithMessage("*no block width*");
    }

    [Fact]
    public void A_barcode_with_no_data_key_is_rejected()
    {
        var act = () => Label(new BarcodeElement { Id = "b", XMm = 5m, YMm = 5m }).Validate();
        act.Should().Throw<LabelDefinitionException>().WithMessage("*no data key*");
    }

    [Fact]
    public void An_unsupported_resolution_is_rejected_rather_than_silently_mis_scaling()
    {
        var act = () => (Label(Text("t", 1m, 1m, text: "x")) with { Dpi = 250 }).Validate();
        act.Should().Throw<LabelDefinitionException>().WithMessage("*not a supported printer resolution*");
    }

    [Fact]
    public void A_future_schema_version_is_refused_instead_of_half_understood()
    {
        var act = () => (Label(Text("t", 1m, 1m, text: "x")) with { Schema = 99 }).Validate();
        act.Should().Throw<LabelDefinitionException>().WithMessage("*schema 99*");
    }

    // ---- round trip -----------------------------------------------------------------

    [Fact]
    public void A_definition_survives_being_stored_and_read_back()
    {
        var original = Label(
            Text("caption", 5m, 5m, text: "Batch"),
            Text("batch", 25m, 5m, key: "Effective.Batch") with
            {
                Alignment = TextAlignment.Right, BlockWidthMm = 30m, Bold = true,
            },
            new BarcodeElement
            {
                Id = "bc", XMm = 30m, YMm = 20m, DataKey = "Product.BarcodeValue",
                HeightMm = 12m, ModuleWidthDots = 3, ShowHumanReadable = false,
            },
            new QrElement { Id = "qr", XMm = 85m, YMm = 30m, DataKey = "Settings.FeedbackFormUrl" },
            new ImageElement { Id = "img", XMm = 3m, YMm = 20m, WidthMm = 20m, HeightMm = 20m });

        var restored = LabelDefinition.Parse(original.ToJson());

        restored.Should().BeEquivalentTo(original);
        Adapter.Prepare(restored.ToJson(), "R:T.ZPL", new Dictionary<int, string>())
            .DefinePayload.Should().Be(
                Adapter.Prepare(original.ToJson(), "R:T.ZPL", new Dictionary<int, string>()).DefinePayload);
    }
}
