using BarcodePrinter.Printing.Abstractions;
using FluentAssertions;
using Xunit;

namespace BarcodePrinter.Printing.Tests;

/// <summary>
/// The raster container is written once at submit and read back by reprint
/// indefinitely, so its framing is a compatibility contract: a payload stored
/// today must still unpack byte-for-byte later, and a corrupt one must be
/// reported rather than half-printed.
/// </summary>
public class RasterLabelPayloadTests
{
    private static byte[] FakePng(byte seed, int length) =>
        Enumerable.Range(0, length).Select(i => (byte)(seed + i)).ToArray();

    [Fact]
    public void A_packed_set_round_trips_byte_for_byte()
    {
        var images = new[] { FakePng(1, 40), FakePng(90, 7), FakePng(200, 512) };

        var unpacked = RasterLabelPayload.Unpack(RasterLabelPayload.Pack(images));

        unpacked.Should().HaveCount(3);
        unpacked[0].Should().Equal(images[0]);
        unpacked[1].Should().Equal(images[1]);
        unpacked[2].Should().Equal(images[2], "a reprint must reproduce the original label exactly");
    }

    [Fact]
    public void An_empty_set_round_trips()
    {
        RasterLabelPayload.Unpack(RasterLabelPayload.Pack([])).Should().BeEmpty();
    }

    [Fact]
    public void A_single_label_job_round_trips()
    {
        var image = FakePng(7, 128);
        RasterLabelPayload.Unpack(RasterLabelPayload.Pack([image]))
            .Single().Should().Equal(image);
    }

    [Fact]
    public void The_container_is_self_describing()
    {
        var packed = RasterLabelPayload.Pack([FakePng(1, 4)]);

        System.Text.Encoding.ASCII.GetString(packed, 0, 4).Should().Be("BPRL");
        packed[4].Should().Be(1, "the version byte lets a future reader refuse an unknown format");
    }

    [Fact]
    public void A_payload_that_is_not_a_raster_set_is_rejected()
    {
        var zpl = System.Text.Encoding.UTF8.GetBytes("^XA^FO40,40^FDnot a raster^FS^XZ");

        var act = () => RasterLabelPayload.Unpack(zpl);

        act.Should().Throw<InvalidDataException>().WithMessage("*not a raster label set*");
    }

    [Fact]
    public void A_future_version_is_refused_rather_than_misread()
    {
        var packed = RasterLabelPayload.Pack([FakePng(1, 4)]);
        packed[4] = 99;

        var act = () => RasterLabelPayload.Unpack(packed);

        act.Should().Throw<InvalidDataException>().WithMessage("*newer application version*");
    }

    [Fact]
    public void A_truncated_payload_is_reported_not_silently_short_printed()
    {
        var packed = RasterLabelPayload.Pack([FakePng(1, 64), FakePng(2, 64)]);

        var act = () => RasterLabelPayload.Unpack(packed[..(packed.Length - 20)]);

        act.Should().Throw<InvalidDataException>().WithMessage("*truncated*");
    }

    [Fact]
    public void An_implausible_label_count_is_refused()
    {
        var packed = RasterLabelPayload.Pack([FakePng(1, 4)]);
        // Corrupt the count field to something a real job could never contain.
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(packed.AsSpan(5), 500_000);

        var act = () => RasterLabelPayload.Unpack(packed);

        act.Should().Throw<InvalidDataException>().WithMessage("*implausible*");
    }

    [Fact]
    public void A_header_only_buffer_is_rejected()
    {
        var act = () => RasterLabelPayload.Unpack("BPRL"u8.ToArray());

        act.Should().Throw<InvalidDataException>();
    }
}
