using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace BarcodePrinter.Wpf.ViewModels.Tests;

/// <summary>
/// Printing consumes media and burns carton numbers, and neither is recoverable.
/// It must therefore be an explicit action.
///
/// This exists because the PRINT button shipped with IsDefault="True": pressing
/// Enter anywhere on the screen — after typing a batch number, say — silently
/// submitted a print job. Found by an unexplained second job appearing during
/// the packaged-build smoke test.
///
/// Asserted against the XAML source: instantiating the view needs the full
/// application resource system, and what matters here is a markup property.
/// </summary>
public class PrintScreenInputTests
{
    [Fact]
    public void No_control_on_the_print_screen_submits_on_Enter()
    {
        var xaml = PrintViewXaml();

        Regex.IsMatch(xaml, @"IsDefault\s*=\s*""True""", RegexOptions.IgnoreCase)
            .Should().BeFalse(
                "Enter must never print — an operator pressing Enter after typing a " +
                "batch number would waste media and consume carton numbers");
    }

    [Fact]
    public void Ctrl_P_is_bound_to_print_so_the_keyboard_route_still_exists()
    {
        var xaml = PrintViewXaml();

        xaml.Should().Contain("KeyBinding", "§12.4 keeps the screen keyboard-first");
        Regex.IsMatch(xaml,
                @"<KeyBinding[^>]*Modifiers\s*=\s*""Control""[^>]*Key\s*=\s*""P""[^>]*PrintCommand")
            .Should().BeTrue("removing Enter must not leave printing mouse-only");
    }

    private static string PrintViewXaml()
    {
        // Walk up from the test bin folder to the repo root.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BarcodePrinter.slnx")))
        {
            dir = dir.Parent!;
        }
        dir.Should().NotBeNull("the test must run from within the repository");

        return File.ReadAllText(Path.Combine(
            dir!.FullName, "src", "client", "BarcodePrinter.Wpf",
            "Features", "Printing", "PrintView.xaml"));
    }
}
