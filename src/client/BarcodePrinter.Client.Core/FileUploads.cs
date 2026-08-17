using System.IO;

namespace BarcodePrinter.Client.Core;

/// <summary>Result of client-side upload validation. A failure carries the
/// operator-facing message; the caller shows it without a server round trip.</summary>
public sealed record UploadValidation(bool IsValid, string? Error, string ContentType)
{
    public static UploadValidation Fail(string error) => new(false, error, "application/octet-stream");
    public static UploadValidation Ok(string contentType) => new(true, null, contentType);
}

/// <summary>Client-side image validation mirroring the server's constraints
/// (5 MB cap, decodable image). Sniffs the real file signature so the declared
/// content type is never guessed from a (possibly renamed) extension.</summary>
public static class ImageFileValidator
{
    public const long MaxBytes = 5 * 1024 * 1024;
    public const string SupportedText = "JPG, PNG or WebP, up to 5 MB";

    private static readonly string[] Extensions = [".jpg", ".jpeg", ".png", ".webp"];

    public static UploadValidation Validate(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        if (!Extensions.Contains(extension))
        {
            return UploadValidation.Fail($"Unsupported file type '{extension}'. Use {SupportedText}.");
        }

        FileInfo info;
        try
        {
            info = new FileInfo(filePath);
            if (!info.Exists)
            {
                return UploadValidation.Fail("The selected file no longer exists.");
            }
        }
        catch (IOException)
        {
            return UploadValidation.Fail("The selected file could not be read.");
        }

        if (info.Length == 0)
        {
            return UploadValidation.Fail("The selected file is empty.");
        }
        if (info.Length > MaxBytes)
        {
            return UploadValidation.Fail(
                $"Image is {info.Length / (1024.0 * 1024.0):0.#} MB — the maximum is 5 MB. " +
                "Resize or re-save the image and try again.");
        }

        Span<byte> header = stackalloc byte[12];
        int read;
        try
        {
            using var stream = File.OpenRead(filePath);
            read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        }
        catch (IOException)
        {
            return UploadValidation.Fail("The selected file could not be read. It may be open in another program.");
        }

        var contentType = Sniff(header[..read]);
        return contentType is null
            ? UploadValidation.Fail($"The file is not a valid image. Use {SupportedText}.")
            : UploadValidation.Ok(contentType);
    }

    /// <summary>File-signature sniffing: JPEG (FF D8 FF), PNG (89 'PNG'),
    /// WebP (RIFF....WEBP). Returns null when no known signature matches.</summary>
    private static string? Sniff(ReadOnlySpan<byte> header) => header switch
    {
        [0xFF, 0xD8, 0xFF, ..] => "image/jpeg",
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, ..] => "image/png",
        [0x52, 0x49, 0x46, 0x46, _, _, _, _, 0x57, 0x45, 0x42, 0x50] => "image/webp",
        _ => null,
    };
}

/// <summary>Read-through stream that reports cumulative progress as a 0–1
/// fraction — wraps the file stream handed to StreamContent so multipart
/// uploads can drive a progress bar.</summary>
public sealed class ProgressStream(Stream inner, long length, IProgress<double>? progress) : Stream
{
    private long _read;

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }

    public override int Read(byte[] buffer, int offset, int count) =>
        Report(inner.Read(buffer, offset, count));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
        Report(await inner.ReadAsync(buffer, ct));

    private int Report(int count)
    {
        _read += count;
        if (length > 0)
        {
            progress?.Report(Math.Min(1.0, (double)_read / length));
        }
        return count;
    }

    public override void Flush() => inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }
        base.Dispose(disposing);
    }
}
