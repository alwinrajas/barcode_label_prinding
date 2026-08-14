using BarcodePrinter.Labels.Barcodes;
using BarcodePrinter.Labels.Binding;
using FluentAssertions;
using Xunit;

namespace BarcodePrinter.Labels.Tests;

public class FieldBinderTests
{
    private static readonly FieldBinder Binder = new();
    private static PrintContext Context() => ZplTemplateAdapterTests.SampleContext();

    // ---- The confirmed QR rule, enforced not documented (A-14) -----------------

    [Fact]
    public void Qr_field_may_bind_only_to_the_static_feedback_url()
    {
        var ok = new[] { new FieldMapping("1", TokenVocabulary.FeedbackUrlKey, FieldDataKind.QrCode) };
        Binder.Bind(ok, Context())["1"].Should().Be("https://forms.gle/EXAMPLE");
    }

    [Theory]
    [InlineData("Product.Code")]
    [InlineData("Effective.Batch")]
    [InlineData("Job.JobNo")]
    [InlineData("Carton.Current")]
    public void Qr_field_rejects_any_product_or_job_data(string dataKey)
    {
        var mappings = new[] { new FieldMapping("1", dataKey, FieldDataKind.QrCode) };

        var act = () => Binder.Bind(mappings, Context());

        act.Should().Throw<FieldBindingException>()
            .WithMessage("*static feedback URL only*",
                "A-14 is confirmed: no dynamic parameters may ever reach the QR code");
    }

    [Fact]
    public void Unknown_data_keys_are_rejected()
    {
        var mappings = new[] { new FieldMapping("1", "Product.SecretMargin", FieldDataKind.Text) };
        var act = () => Binder.Bind(mappings, Context());
        act.Should().Throw<FieldBindingException>();
    }

    // ---- Effective vs master defaults (A-9/A-10) ---------------------------------

    [Fact]
    public void Effective_values_are_what_gets_printed()
    {
        var mappings = new[]
        {
            new FieldMapping("1", "Effective.Batch", FieldDataKind.Text),
            new FieldMapping("2", "Effective.ProductionDate", FieldDataKind.DateTime),
        };
        var overridden = Context() with
        {
            Effective = new EffectiveValues("RUN-999", new DateOnly(2026, 8, 12), null, "500[D]"),
        };

        var bound = Binder.Bind(mappings, overridden);

        bound["1"].Should().Be("RUN-999");
        bound["2"].Should().Be("12/08/2026");
    }

    // ---- Formatting (C-1) ---------------------------------------------------------

    [Fact]
    public void Dates_use_the_configured_format_and_can_be_overridden_per_field()
    {
        var settingsFormat = new[] { new FieldMapping("1", "Effective.ProductionDate", FieldDataKind.DateTime) };
        Binder.Bind(settingsFormat, Context())["1"].Should().Be("21/07/2026");

        // C-1 conflict: the physical samples print dd/MMM/yyyy. Switching is a
        // settings/mapping change, never a code change.
        var perField = new[]
        {
            new FieldMapping("1", "Effective.ProductionDate", FieldDataKind.DateTime, FormatString: "dd/MMM/yyyy"),
        };
        Binder.Bind(perField, Context())["1"].Should().Be("21/Jul/2026");
    }

    // ---- Transforms, length, required ------------------------------------------------

    [Fact]
    public void Transform_and_truncate_are_applied()
    {
        var mappings = new[]
        {
            new FieldMapping("1", "Product.Description", FieldDataKind.Text,
                Transform: FieldTransform.Upper, MaxLength: 5, Overflow: OverflowBehaviour.Truncate),
        };
        Binder.Bind(mappings, Context())["1"].Should().Be("5G M2");
    }

    [Fact]
    public void Overflow_error_blocks_the_print_rather_than_silently_clipping()
    {
        var mappings = new[]
        {
            new FieldMapping("1", "Product.Description", FieldDataKind.Text,
                MaxLength: 5, Overflow: OverflowBehaviour.Error),
        };
        var act = () => Binder.Bind(mappings, Context());
        act.Should().Throw<FieldBindingException>().WithMessage("*allows 5*");
    }

    [Fact]
    public void Required_empty_field_fails_and_fallback_fills_optional_ones()
    {
        var empty = Context() with
        {
            Effective = new EffectiveValues(null, null, null, null),
        };

        var required = new[] { new FieldMapping("1", "Effective.Batch", FieldDataKind.Text, IsRequired: true) };
        var act = () => Binder.Bind(required, empty);
        act.Should().Throw<FieldBindingException>().WithMessage("*required*");

        var withFallback = new[]
        {
            new FieldMapping("1", "Effective.Batch", FieldDataKind.Text, FallbackValue: "N/A"),
        };
        Binder.Bind(withFallback, empty)["1"].Should().Be("N/A");
    }

    [Fact]
    public void Reprint_flag_renders_the_overlay_text_only_when_set()
    {
        var mappings = new[] { new FieldMapping("1", "Job.IsReprint", FieldDataKind.Text) };

        Binder.Bind(mappings, Context())["1"].Should().BeEmpty();

        var reprint = Context() with { Job = Context().Job with { IsReprint = true } };
        Binder.Bind(mappings, reprint)["1"].Should().Be("REPRINT");
    }
}

public class BarcodeEncoderTests
{
    private static readonly BarcodeEncoder Encoder = new();

    [Theory]
    [InlineData(BarcodeSymbology.Code128, "^BCN,56,Y,N,N")]
    [InlineData(BarcodeSymbology.Code39, "^B3N,N,56,Y,N")]
    [InlineData(BarcodeSymbology.Ean13, "^BEN,56,Y,N")]
    [InlineData(BarcodeSymbology.UpcA, "^BUN,56,Y,N,N")]
    [InlineData(BarcodeSymbology.Itf14, "^B2N,56,Y,N,N")]
    public void Symbology_maps_to_its_zpl_command(BarcodeSymbology symbology, string expected) =>
        Encoder.ZplCommand(symbology, 56, humanReadable: true).Should().Be(expected);

    /// <summary>R-8: if the client picks a numeric symbology, the existing
    /// alphanumeric product codes are incompatible — this must surface as a
    /// validation error at template-registration time, not on the shop floor.</summary>
    [Theory]
    [InlineData(BarcodeSymbology.Ean13)]
    [InlineData(BarcodeSymbology.UpcA)]
    [InlineData(BarcodeSymbology.Itf14)]
    public void Numeric_symbologies_reject_the_observed_product_codes(BarcodeSymbology symbology)
    {
        Encoder.Validate(symbology, "5GCAPM2N").IsValid.Should().BeFalse();
        Encoder.Validate(symbology, "5GCAPM3NOSW").IsValid.Should().BeFalse();
    }

    [Fact]
    public void Code128_and_code39_accept_the_observed_product_codes()
    {
        Encoder.Validate(BarcodeSymbology.Code128, "5GCAPM2N").IsValid.Should().BeTrue();
        Encoder.Validate(BarcodeSymbology.Code128, "5GCAPM3NOSW").IsValid.Should().BeTrue();
        Encoder.Validate(BarcodeSymbology.Code39, "5GCAPM2N").IsValid.Should().BeTrue();
    }

    [Fact]
    public void Code39_rejects_lowercase_which_code128_allows()
    {
        Encoder.Validate(BarcodeSymbology.Code39, "5gcapm2n").IsValid.Should().BeFalse();
        Encoder.Validate(BarcodeSymbology.Code128, "5gcapm2n").IsValid.Should().BeTrue();
    }

    [Fact]
    public void Ean13_accepts_a_proper_numeric_payload()
    {
        Encoder.Validate(BarcodeSymbology.Ean13, "123456789012").IsValid.Should().BeTrue();
        Encoder.Validate(BarcodeSymbology.Ean13, "12345").IsValid.Should().BeFalse();
    }
}

public class ZplSanitizerTests
{
    [Theory]
    [InlineData("PLAIN TEXT", false)]
    [InlineData("750[D]", false)]
    [InlineData("CAP ^ SPECIAL", true)]
    [InlineData("TILDE ~ HERE", true)]
    [InlineData("under_score", true)]
    [InlineData("café", true)]
    public void Detects_values_needing_escape(string value, bool needsEscape) =>
        ZplSanitizer.NeedsHexEscape(value).Should().Be(needsEscape);

    [Fact]
    public void Escapes_control_characters_to_hex()
    {
        ZplSanitizer.HexEscape("A^B~C").Should().Be("A_5EB_7EC");
        ZplSanitizer.HexEscape("a_b").Should().Be("a_5Fb");
    }

    [Fact]
    public void Safe_values_render_without_the_hex_prefix()
    {
        ZplSanitizer.RenderField("1", "5GCAPM2N").Should().Be("^FN1^FD5GCAPM2N^FS");
        ZplSanitizer.RenderField("2", "A^B").Should().Be("^FN2^FH^FDA_5EB^FS");
    }

    /// <summary>A comma inside a QR URL silently truncates the payload if it
    /// reaches ^BQ unescaped — the exact production defect this guards.</summary>
    [Fact]
    public void Url_with_comma_survives_intact()
    {
        const string url = "https://forms.gle/abc?entry=1,2";
        var rendered = ZplSanitizer.RenderField("11", url);
        rendered.Should().Contain(url);
    }
}
