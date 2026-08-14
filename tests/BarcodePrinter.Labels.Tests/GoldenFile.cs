using System.Runtime.CompilerServices;
using FluentAssertions;

namespace BarcodePrinter.Labels.Tests;

/// <summary>
/// Golden-file harness (blueprint B-19). Label defects are otherwise only
/// visible on physical media, after the fact — an accidental change to
/// spacing, dot math or escaping fails the build instead.
///
/// Set BARCODEPRINTER_APPROVE_GOLDEN=1 to write approved files after an
/// INTENTIONAL change, then review the diff in version control.
/// </summary>
public static class GoldenFile
{
    public static void Assert(string actual, string goldenName, [CallerFilePath] string callerPath = "")
    {
        var directory = Path.Combine(Path.GetDirectoryName(callerPath)!, "Golden");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, goldenName);

        var normalised = Normalise(actual);

        if (Environment.GetEnvironmentVariable("BARCODEPRINTER_APPROVE_GOLDEN") == "1")
        {
            File.WriteAllText(path, normalised);
            return;
        }

        File.Exists(path).Should().BeTrue(
            $"golden file '{goldenName}' must exist. Re-run with BARCODEPRINTER_APPROVE_GOLDEN=1 " +
            "to create it, then review the content before committing.");

        var expected = Normalise(File.ReadAllText(path));
        normalised.Should().Be(expected,
            $"generated output must match approved golden file '{goldenName}'. " +
            "If this change is intentional, re-approve and review the diff.");
    }

    /// <summary>Line endings only — every other byte is significant.</summary>
    private static string Normalise(string text) =>
        text.Replace("\r\n", "\n").TrimEnd('\n');
}
