using System.Buffers.Binary;

namespace BarcodePrinter.Printing.Abstractions;

/// <summary>
/// Container for a job rendered as pictures rather than printer commands.
///
/// A GDI printer — an office laser or inkjet — cannot interpret ZPL, so a job
/// targeting one is rendered to one image per label at submit time and stored
/// in exactly the same payload row (`print_job_payloads.format = 'Raster'`).
/// Byte-replay reprint, retry and the whole job lifecycle then work unchanged;
/// only the transport differs.
///
/// The format is deliberately trivial and self-describing: magic, version,
/// count, then length-prefixed PNGs. A stored payload must still be readable by
/// a future version, because reprint reaches back into history indefinitely.
/// </summary>
public static class RasterLabelPayload
{
    private static readonly byte[] Magic = "BPRL"u8.ToArray();
    private const byte Version = 1;

    public static byte[] Pack(IReadOnlyList<byte[]> images)
    {
        var size = Magic.Length + 1 + 4 + images.Sum(i => 4 + i.Length);
        var buffer = new byte[size];
        var span = buffer.AsSpan();

        Magic.CopyTo(span);
        var offset = Magic.Length;
        span[offset++] = Version;

        BinaryPrimitives.WriteInt32LittleEndian(span[offset..], images.Count);
        offset += 4;

        foreach (var image in images)
        {
            BinaryPrimitives.WriteInt32LittleEndian(span[offset..], image.Length);
            offset += 4;
            image.CopyTo(span[offset..]);
            offset += image.Length;
        }

        return buffer;
    }

    public static IReadOnlyList<byte[]> Unpack(byte[] payload)
    {
        var span = payload.AsSpan();
        if (span.Length < Magic.Length + 5 || !span[..Magic.Length].SequenceEqual(Magic))
        {
            throw new InvalidDataException("This print payload is not a raster label set.");
        }

        var offset = Magic.Length;
        var version = span[offset++];
        if (version != Version)
        {
            throw new InvalidDataException(
                $"Raster payload version {version} was produced by a newer application version.");
        }

        var count = BinaryPrimitives.ReadInt32LittleEndian(span[offset..]);
        offset += 4;
        if (count is < 0 or > 100_000)
        {
            throw new InvalidDataException($"Raster payload declares an implausible label count ({count}).");
        }

        var images = new List<byte[]>(count);
        for (var i = 0; i < count; i++)
        {
            if (offset + 4 > span.Length)
            {
                throw new InvalidDataException("Raster payload is truncated.");
            }
            var length = BinaryPrimitives.ReadInt32LittleEndian(span[offset..]);
            offset += 4;
            if (length < 0 || offset + length > span.Length)
            {
                throw new InvalidDataException("Raster payload is truncated.");
            }
            images.Add(span.Slice(offset, length).ToArray());
            offset += length;
        }

        return images;
    }
}
