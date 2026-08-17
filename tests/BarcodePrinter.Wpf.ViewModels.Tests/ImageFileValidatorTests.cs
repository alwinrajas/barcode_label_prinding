using System.IO;
using BarcodePrinter.Client.Core;
using FluentAssertions;
using Xunit;

namespace BarcodePrinter.Wpf.ViewModels.Tests;

/// <summary>Client-side image validation: the operator hears about a bad file
/// before any bytes leave the machine, and the declared content type comes
/// from the file signature, never the (possibly renamed) extension.</summary>
public sealed class ImageFileValidatorTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bp-imgval").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static readonly byte[] JpegHeader = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 5, 6, 7, 8];
    private static readonly byte[] PngHeader = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];
    private static readonly byte[] WebpHeader =
        [0x52, 0x49, 0x46, 0x46, 0x10, 0x00, 0x00, 0x00, 0x57, 0x45, 0x42, 0x50];

    [Fact]
    public void Jpeg_signature_is_detected_regardless_of_which_valid_extension_it_carries()
    {
        var result = ImageFileValidator.Validate(Write("photo.png", JpegHeader));
        result.IsValid.Should().BeTrue();
        result.ContentType.Should().Be("image/jpeg", "the signature wins over the renamed extension");
    }

    [Theory]
    [InlineData("a.jpg")]
    [InlineData("b.jpeg")]
    public void Jpeg_files_validate(string name) =>
        ImageFileValidator.Validate(Write(name, JpegHeader)).ContentType.Should().Be("image/jpeg");

    [Fact]
    public void Png_files_validate() =>
        ImageFileValidator.Validate(Write("a.png", PngHeader)).ContentType.Should().Be("image/png");

    [Fact]
    public void Webp_files_validate() =>
        ImageFileValidator.Validate(Write("a.webp", WebpHeader)).ContentType.Should().Be("image/webp");

    [Fact]
    public void Unsupported_extension_is_rejected_with_an_actionable_message()
    {
        var result = ImageFileValidator.Validate(Write("document.pdf", JpegHeader));
        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain(".pdf").And.Contain("JPG");
    }

    [Fact]
    public void A_non_image_with_an_image_extension_is_rejected()
    {
        var result = ImageFileValidator.Validate(Write("fake.jpg", "not an image at all"u8.ToArray()));
        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("not a valid image");
    }

    [Fact]
    public void Empty_file_is_rejected()
    {
        var result = ImageFileValidator.Validate(Write("empty.jpg", []));
        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("empty");
    }

    [Fact]
    public void Oversized_file_is_rejected_with_the_size_in_the_message()
    {
        var big = new byte[ImageFileValidator.MaxBytes + 1];
        JpegHeader.CopyTo(big, 0);
        var result = ImageFileValidator.Validate(Write("big.jpg", big));
        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("maximum is 5 MB");
    }

    [Fact]
    public void Missing_file_is_rejected_not_thrown() =>
        ImageFileValidator.Validate(Path.Combine(_dir, "gone.jpg"))
            .IsValid.Should().BeFalse();
}
